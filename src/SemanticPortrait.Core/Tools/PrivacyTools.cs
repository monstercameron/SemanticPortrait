namespace SemanticPortrait.Core;

/// <summary>
/// READ-ONLY privacy awareness for the main agent: one tool that reports the live egress
/// posture (provider locality, masking, embeddings path, toast discretion, storage) so the
/// agent answers privacy questions with facts instead of guesses.
///
/// Deliberately NOT a toggle: masking and provider choice are user consent decisions — an agent
/// with the power to flip them could be talked into weakening the user's own privacy settings
/// mid-conversation. The agent may DESCRIBE where the switches live; only the user flips them.
/// </summary>
public sealed class PrivacyTools
{
    private readonly Func<bool> _maskingOn;
    private readonly ProviderRegistry _providers;
    private readonly Func<bool> _localEmbeddings;

    public PrivacyTools(Func<bool> maskingOn, ProviderRegistry providers, Func<bool> localEmbeddings)
    {
        _maskingOn = maskingOn; _providers = providers; _localEmbeddings = localEmbeddings;
    }

    public bool Handles(string name) => name == "privacy_status";

    public IReadOnlyList<object> Specs => new object[]
    {
        new { type = "function", name = "privacy_status",
              description = "The live privacy/egress posture: which model provider is in use and whether it is " +
                            "local or cloud, whether PII masking is on, where semantic recall runs, how OS " +
                            "notifications are handled, and how storage is protected. Call whenever the user asks " +
                            "what leaves their machine, whether this is private, or how to make it more private. " +
                            "Read-only: you can report and explain settings, never change them.",
              parameters = new { type = "object", properties = new Dictionary<string, object>(),
                                 required = Array.Empty<string>(), additionalProperties = false } },
    };

    public Task<string> ExecuteAsync(string name, string argsJson)
    {
        if (name != "privacy_status") return Task.FromResult("error: unknown tool.");
        var p = _providers.Active;
        bool local = p.ProviderId == "lmstudio";
        bool mask = _maskingOn();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(local
            ? $"chat model: {p.DisplayName} — LOCAL; conversation content does not leave this machine."
            : $"chat model: {p.DisplayName} — CLOUD; entries are sent to this provider under its privacy policy"
              + (mask ? ", with local PII masking applied first (names/emails/phones pseudonymized — harm reduction, not anonymity)."
                      : ", UNMASKED (PII masking is off)."));
        sb.AppendLine(_localEmbeddings()
            ? "semantic recall: LOCAL on-device embeddings — the index never touches the network."
            : $"semantic recall: cloud embeddings{(mask ? " (masked)" : " (unmasked)")}.");
        sb.AppendLine(NotificationService.Discreet
            ? "OS notifications: ALWAYS DISCREET — every toast shows a generic line, nothing personal on the lock screen."
            : "OS notifications: smart privacy — each reminder is classified; private text shows a generic placeholder (fails safe to private).");
        sb.AppendLine("storage: everything lives on this machine in an AES-256 (SQLCipher) vault keyed from Windows Hello/PIN; no accounts, no sync, no telemetry.");
        sb.AppendLine("update check: one metadata-only ping to GitHub releases at launch (version number only, never content); updates are never downloaded automatically.");
        sb.AppendLine("to change any of this (user-only, you cannot): provider in ⋯ → LLM settings (LM Studio = fully local); masking in ⋯ → Security; toast discretion in the ⋯ menu.");
        return Task.FromResult(sb.ToString().TrimEnd());
    }
}
