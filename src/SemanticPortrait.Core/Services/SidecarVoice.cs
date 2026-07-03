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

    /// <summary>Transcribe a WAV file → text (null on failure/empty).</summary>
    public async Task<string?> TranscribeAsync(string wavPath, CancellationToken ct = default)
    {
        var output = await RunAsync($"-m whispertome test-stt --wav \"{wavPath}\"", ct);
        return output is null ? null : ParseSttText(output);
    }

    /// <summary>Synthesize text → a WAV file at <paramref name="outWavPath"/> (null on failure).</summary>
    public async Task<string?> SpeakAsync(string text, string outWavPath, CancellationToken ct = default)
    {
        // The CLI takes the text as one argv element; strip quotes so it can't break out of them.
        var safe = text.Replace('"', '″');
        var output = await RunAsync($"-m whispertome test-tts \"{safe}\" --out \"{outWavPath}\"", ct);
        return output is not null && File.Exists(outWavPath) ? outWavPath : null;
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

    /// <summary>The CLI logs `stt_text='…' language=…` — pull the transcription out.</summary>
    internal static string? ParseSttText(string output)
    {
        const string marker = "stt_text='";
        var i = output.LastIndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return null;
        var start = i + marker.Length;
        var end = output.IndexOf("' language=", start, StringComparison.Ordinal);
        if (end < 0) end = output.IndexOf('\'', start);
        if (end <= start) return null;
        var text = output[start..end].Trim();
        return text.Length > 0 ? text : null;
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
