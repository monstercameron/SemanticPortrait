using System.Text.Json;

namespace SemanticPortrait.Core;

/// <summary>
/// Voice setup for the agent: report whether on-device voice is ready, and — ONLY with the
/// user's explicit in-chat consent — download the missing NPU models (~770 MB). The consent
/// gate is structural: `action:"download"` without `confirmed:true` refuses and tells the agent
/// to ask the user first. The agent can never toggle privacy; here it can never spend the
/// user's bandwidth/disk without their yes.
/// </summary>
public sealed class VoiceTools
{
    private readonly SidecarVoice _voice;
    private readonly Action<string>? _progress;   // surfaced as quiet sys lines by the app

    public VoiceTools(SidecarVoice voice, Action<string>? progress = null)
    {
        _voice = voice; _progress = progress;
    }

    public bool Handles(string name) => name == "voice_setup";

    public IReadOnlyList<object> Specs => new object[]
    {
        new { type = "function", name = "voice_setup",
              description = "Check or set up on-device voice (dictation + read-aloud; runs on the Snapdragon NPU, " +
                            "nothing leaves the machine). action:'status' reports whether the runtime and models are " +
                            "present and what a download would fetch. action:'download' fetches the missing models " +
                            "(~770 MB) — REQUIRES confirmed:true, which you may only set after the user explicitly " +
                            "agreed IN THIS CONVERSATION to the download and its size. Never assume consent.",
              parameters = new
              {
                  type = "object",
                  properties = new Dictionary<string, object>
                  {
                      ["action"] = new { type = "string", description = "'status' or 'download'." },
                      ["confirmed"] = new { type = "boolean", description = "true ONLY after the user explicitly agreed to the download." },
                  },
                  required = new[] { "action" },
                  additionalProperties = false,
              } },
    };

    public async Task<string> ExecuteAsync(string name, string argsJson)
    {
        if (name != "voice_setup") return "error: unknown tool.";
        string action = "status"; bool confirmed = false;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            if (doc.RootElement.TryGetProperty("action", out var a)) action = a.GetString() ?? "status";
            if (doc.RootElement.TryGetProperty("confirmed", out var c) && c.ValueKind == JsonValueKind.True) confirmed = true;
        }
        catch { /* defaults */ }

        if (!_voice.Available)
            return "voice runtime not present on this machine. On-device voice needs a Snapdragon (Hexagon NPU) " +
                   "Windows PC plus the WhisperToMe companion runtime (github.com/monstercameron — see the " +
                   "SemanticPortrait README's voice section). Until then, dictation and read-aloud stay hidden; " +
                   "everything else works normally. Tell the user this plainly — no workaround exists to enable it from here.";

        var missing = await _voice.VoiceStatusAsync();
        if (missing is null)
            return "voice runtime found, but its status could not be read (the helper process failed to start). " +
                   "Suggest restarting the app; if it persists this needs a look at the WhisperToMe install.";

        if (action == "status")
            return missing.Count == 0
                ? "voice is READY: models present, dictation (🎙) and read-aloud (🔊) are live. All on-device — nothing leaves the machine."
                : "voice runtime present but models are missing: "
                  + string.Join(", ", missing.Select(m => $"{m.Name} (~{m.Mb} MB)"))
                  + $". Total ~{missing.Sum(m => m.Mb)} MB, downloaded once from the model hosts. "
                  + "ASK the user if they want the download; only call action:'download' with confirmed:true after they say yes.";

        if (action == "download")
        {
            if (missing.Count == 0) return "nothing to download — voice is already ready.";
            if (!confirmed)
                return "refused: downloading requires the user's explicit consent in this conversation. Tell them the "
                       + $"size (~{missing.Sum(m => m.Mb)} MB) and ask; call again with confirmed:true only after a clear yes.";
            _progress?.Invoke($"⬇ downloading voice models (~{missing.Sum(m => m.Mb)} MB)…");
            var lastPct = -20;
            var ok = await _voice.DownloadModelsAsync((pct, group) =>
            {
                if (pct - lastPct >= 20) { lastPct = pct; _progress?.Invoke($"⬇ {group}: {pct}%"); }
            });
            _progress?.Invoke(ok ? "✅ voice models ready — 🎙 and 🔊 are live" : "⚠ voice model download failed");
            return ok
                ? "download complete — voice is ready. Dictation (🎙 in the composer) and read-aloud (🔊 on replies) are live now."
                : "download failed — network or disk issue. The user can retry later; nothing partial is left in a broken state.";
        }

        return "error: action must be 'status' or 'download'.";
    }
}
