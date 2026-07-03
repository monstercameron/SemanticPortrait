namespace SemanticPortrait.Core;

/// <summary>
/// Local pseudonymization at the cloud boundary (plan §6). When enabled, PII in OUTBOUND text is
/// replaced with stable tokens (e.g. "Alice" → PERSON_1) before it leaves the machine, and tokens
/// in the model's reply are restored for display. The token↔original map lives only in the encrypted
/// DB and is never sent. Masking ≠ anonymity (content can re-identify) — it's harm reduction.
/// </summary>
public interface IMasker
{
    /// <summary>Whether masking is currently active (user consent + DB available).</summary>
    bool Enabled { get; }

    /// <summary>Replace detected PII with stable alias tokens. Returns text unchanged if disabled.</summary>
    string Mask(string text);

    /// <summary>Restore alias tokens back to their originals (for display / tool execution).</summary>
    string Unmask(string text);
}
