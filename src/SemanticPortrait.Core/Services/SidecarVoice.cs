using System.Diagnostics;

namespace SemanticPortrait.Core;

/// <summary>
/// Optional voice via the sibling WhisperToMe project's CLI (design §M5 voice): STT and TTS both
/// run on the Snapdragon NPU inside a short-lived python sidecar process. Deliberately v1:
/// each call pays the model load (~5-10s) because the process exits — a persistent sidecar or
/// in-proc ORT-QNN is the upgrade path. When the sidecar isn't present (installed users,
/// non-Snapdragon machines) <see cref="Available"/> is false and the UI shows nothing.
///
/// Configuration (overridable): voice_py = python.exe path, voice_dir = whispertome repo root.
/// Defaults probe the conventional sibling-checkout location.
/// </summary>
public sealed class SidecarVoice
{
    private readonly string _python;
    private readonly string _workDir;

    public SidecarVoice(string? pythonPath = null, string? workDir = null)
    {
        // The shell Desktop is often OneDrive-redirected while dev checkouts live in the raw
        // profile Desktop — probe both before giving up.
        _workDir = workDir ?? new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop", "whispertome"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "whispertome"),
            }.FirstOrDefault(Directory.Exists) ?? "";
        _python = pythonPath ?? (_workDir.Length > 0 ? Path.Combine(_workDir, ".venv", "Scripts", "python.exe") : "");
    }

    public bool Available => File.Exists(_python) && Directory.Exists(_workDir);

    /// <summary>Transcribe a WAV file → text (null on failure/empty). Persistent server first
    /// (models stay warm), per-call CLI as the fallback.</summary>
    public async Task<string?> TranscribeAsync(string wavPath, CancellationToken ct = default)
    {
        var viaServer = await ServerRequestAsync(
            System.Text.Json.JsonSerializer.Serialize(new { op = "stt", wav = wavPath }), ct);
        if (viaServer is { } el && el.TryGetProperty("text", out var t))
        {
            var text = t.GetString()?.Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        var output = await RunAsync($"-m whispertome test-stt --wav \"{wavPath}\"", ct);
        return output is null ? null : ParseSttText(output);
    }

    /// <summary>Synthesize text → a WAV file at <paramref name="outWavPath"/> (null on failure).</summary>
    public async Task<string?> SpeakAsync(string text, string outWavPath, CancellationToken ct = default)
    {
        var viaServer = await ServerRequestAsync(
            System.Text.Json.JsonSerializer.Serialize(new { op = "tts", text, @out = outWavPath }), ct);
        if (viaServer is not null && File.Exists(outWavPath)) return outWavPath;
        // The CLI takes the text as one argv element; strip quotes so it can't break out of them.
        var safe = text.Replace('"', '″');
        var output = await RunAsync($"-m whispertome test-tts \"{safe}\" --out \"{outWavPath}\"", ct);
        return output is not null && File.Exists(outWavPath) ? outWavPath : null;
    }

    /// <summary>Which model groups are missing (name, approx MB). Null = runtime unavailable.</summary>
    public async Task<IReadOnlyList<(string Name, int Mb)>?> VoiceStatusAsync(CancellationToken ct = default)
    {
        var reply = await ServerRequestAsync("{\"op\":\"voice_status\"}", ct);
        if (reply is not { } el || !el.TryGetProperty("missing", out var missing)) return null;
        var list = new List<(string, int)>();
        foreach (var g in missing.EnumerateArray())
            list.Add((g.GetProperty("name").GetString() ?? "?", g.GetProperty("mb").GetInt32()));
        return list;
    }

    /// <summary>Download all missing model groups (streams progress). True on success. The
    /// CALLER owns consent — this must never run without the user's explicit yes.</summary>
    public async Task<bool> DownloadModelsAsync(Action<int, string>? onProgress = null, CancellationToken ct = default)
    {
        if (!Available) return false;
        await _serverGate.WaitAsync(ct);
        try
        {
            if (_server is null || _server.HasExited)
                if (!await StartServerAsync(ct)) return false;
            _lastUse = DateTime.UtcNow;
            await _server!.StandardInput.WriteLineAsync("{\"op\":\"download_models\"}".AsMemory(), ct);
            // progress lines stream until the final ok/fail — a ~770MB download needs patience
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(45);
            while (DateTime.UtcNow < deadline)
            {
                _lastUse = DateTime.UtcNow;   // keep the idle reaper off an active download
                var line = await ReadLineWithTimeoutAsync(_server.StandardOutput, TimeSpan.FromMinutes(3), ct);
                if (line is null) break;
                using var doc = System.Text.Json.JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("ok", out var ok))
                    return ok.GetBoolean();
                if (doc.RootElement.TryGetProperty("progress", out var p))
                    onProgress?.Invoke(p.GetInt32(), doc.RootElement.TryGetProperty("group", out var g) ? g.GetString() ?? "" : "");
            }
            KillServer();
            return false;
        }
        catch (Exception e) { DevTrap.Report("voice-download", e); KillServer(); return false; }
        finally { _serverGate.Release(); }
    }

    // ---- persistent server: models load once, requests answer in ~1-3s instead of ~10s -----
    // The server exits on stdin EOF, so it can never outlive the app; an idle timer also shuts
    // it down after 5 minutes (fanless-laptop battery philosophy: warm is a lease, not a right).
    private Process? _server;
    private readonly SemaphoreSlim _serverGate = new(1, 1);   // one in-flight request at a time
    private DateTime _lastUse = DateTime.MinValue;
    private System.Threading.Timer? _idleKill;
    private static readonly TimeSpan IdleShutdown = TimeSpan.FromMinutes(5);

    /// <summary>Send one request line; returns the parsed ok-reply or null (caller falls back).</summary>
    private async Task<System.Text.Json.JsonElement?> ServerRequestAsync(string requestJson, CancellationToken ct)
    {
        if (!Available) return null;
        await _serverGate.WaitAsync(ct);
        try
        {
            if (_server is null || _server.HasExited)
                if (!await StartServerAsync(ct)) return null;
            _lastUse = DateTime.UtcNow;
            await _server!.StandardInput.WriteLineAsync(requestJson.AsMemory(), ct);
            var reply = await ReadLineWithTimeoutAsync(_server.StandardOutput, TimeSpan.FromSeconds(90), ct);
            if (reply is null) { KillServer(); return null; }
            var doc = System.Text.Json.JsonDocument.Parse(reply);
            if (doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean())
                return doc.RootElement.Clone();
            DevTrap.Report("voice-server", new InvalidOperationException(reply.Length > 300 ? reply[..300] : reply));
            return null;   // server healthy but request failed → let the CLI fallback try
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            DevTrap.Report("voice-server", e);
            KillServer();
            return null;
        }
        finally { _serverGate.Release(); }
    }

    private async Task<bool> StartServerAsync(CancellationToken ct)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "voice", "sp_voice_server.py");   // ships with the app
        if (!File.Exists(script)) return false;
        var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _python,
                Arguments = $"\"{Path.GetFullPath(script)}\" \"{_workDir}\"",
                WorkingDirectory = _workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new System.Text.UTF8Encoding(false),   // NO BOM — it breaks json.loads
                StandardOutputEncoding = new System.Text.UTF8Encoding(false),
            }
        };
        p.Start();
        _ = p.StandardError.ReadToEndAsync();   // drain so the pipe never blocks the server
        var ready = await ReadLineWithTimeoutAsync(p.StandardOutput, TimeSpan.FromSeconds(30), ct);
        if (ready is null || !ready.Contains("\"ready\""))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return false;
        }
        _server = p;
        _idleKill ??= new System.Threading.Timer(_ =>
        {
            if (_server is { } s && !s.HasExited && DateTime.UtcNow - _lastUse > IdleShutdown)
                KillServer();
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        return true;
    }

    private static async Task<string?> ReadLineWithTimeoutAsync(StreamReader reader, TimeSpan timeout, CancellationToken ct)
    {
        var read = reader.ReadLineAsync(ct).AsTask();
        var done = await Task.WhenAny(read, Task.Delay(timeout, ct));
        return done == read ? await read : null;
    }

    private void KillServer()
    {
        try { _server?.Kill(entireProcessTree: true); } catch { }
        _server?.Dispose(); _server = null;
    }

    /// <summary>Markdown → speakable plain text, capped at a sentence boundary (~900 chars) so a
    /// long reply doesn't synthesize forever. Links keep their label; code blocks are elided.</summary>
    public static string StripForSpeech(string md)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(md ?? "", @"```[\s\S]*?```", " code omitted. ");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\[([^\]]*)\]\([^)]*\)", "$1");   // links → label
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[*_`#>|]+", "");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        if (s.Length <= 900) return s;
        var cut = s.LastIndexOfAny(new[] { '.', '!', '?' }, 899);
        return (cut > 200 ? s[..(cut + 1)] : s[..900]) + " …and more in the written reply.";
    }

    /// <summary>The CLI logs `stt_text=%r` — PYTHON REPR, so the quoting flips with content:
    /// plain text gets 'single quotes', anything with an apostrophe ("I don't…") gets "double
    /// quotes", and internal same-quotes arrive backslash-escaped. Parse the repr properly —
    /// the naive single-quote scan silently dropped every transcript containing a contraction.</summary>
    internal static string? ParseSttText(string output)
    {
        const string marker = "stt_text=";
        var i = output.LastIndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return null;
        var pos = i + marker.Length;
        if (pos >= output.Length) return null;
        var quote = output[pos];
        if (quote != '\'' && quote != '"') return null;

        var sb = new System.Text.StringBuilder();
        for (var j = pos + 1; j < output.Length; j++)
        {
            var ch = output[j];
            if (ch == '\\' && j + 1 < output.Length)
            {
                var n = output[++j];
                sb.Append(n switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => n });
                continue;
            }
            if (ch == quote)
            {
                var text = sb.ToString().Trim();
                return text.Length > 0 ? text : null;
            }
            sb.Append(ch);
        }
        return null;   // unterminated — treat as no transcript
    }

    private async Task<string?> RunAsync(string args, CancellationToken ct)
    {
        if (!Available) return null;
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _python,
                    Arguments = args,
                    WorkingDirectory = _workDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }
            };
            p.Start();
            var stdout = p.StandardOutput.ReadToEndAsync(ct);
            var stderr = p.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(120));   // model load + inference, generously
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { p.Kill(entireProcessTree: true); } catch { } return null; }
            // The CLI logs to stderr via python logging; the transcript line can land either side.
            var all = (await stdout) + "\n" + (await stderr);
            return p.ExitCode == 0 ? all : null;
        }
        catch (Exception e)
        {
            DevTrap.Report("sidecar-voice", e);
            return null;
        }
    }
}
