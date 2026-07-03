using System.Globalization;
using System.Text;

namespace SemanticPortrait.Core;

/// <summary>
/// Thought compaction: the last <see cref="Window"/> of messages stay in flight (sent verbatim);
/// everything older is folded into a rolling summary. Full raw detail remains in the vector DB,
/// so the model recovers specifics via search_memory.
/// </summary>
public sealed class Compactor
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(2);

    private readonly Db _db;
    // Resolved per call so compaction always runs on the user-selected provider.
    private readonly ProviderRegistry _providers;

    public Compactor(Db db, ProviderRegistry providers) { _db = db; _providers = providers; }

    public DateTime CutoffUtc(DateTime nowUtc) => nowUtc - Window;
    public string CurrentSummary() => _db.GetCompaction()?.Summary ?? "";

    public static DateTime ParseUtc(string s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d)
            ? d.ToUniversalTime() : DateTime.MinValue;

    /// <summary>
    /// Fold messages that have aged past the window into the rolling summary, at most
    /// <paramref name="maxBatch"/> per call (a bulk-imported backlog can be years of entries —
    /// folding it in one provider call would blow the context). Returns how many were folded;
    /// callers with a backlog loop until 0.
    /// </summary>
    public async Task<int> EnsureCompactedAsync(DateTime nowUtc, int maxBatch = 80, CancellationToken ct = default)
    {
        var cutoff = nowUtc - Window;
        var existing = _db.GetCompaction();
        var through = existing is { } e ? ParseUtc(e.ThroughUtc) : DateTime.MinValue;

        var aged = _db.GetMessages()
            .Where(m => m.Role is "user" or "assistant")
            .Where(m => ParseUtc(m.CreatedUtc) <= cutoff && ParseUtc(m.CreatedUtc) > through)
            .OrderBy(m => ParseUtc(m.CreatedUtc))
            .Take(Math.Max(1, maxBatch))
            .ToList();

        if (aged.Count == 0) return 0;   // nothing newly aged out

        var transcript = string.Join("\n",
            aged.Select(m => $"{(m.Role == "user" ? "User" : "Analyst")} ({m.CreatedUtc}): {m.Text}"));
        var prior = existing?.Summary ?? "";
        var user = (prior.Length > 0 ? $"EXISTING SUMMARY:\n{prior}\n\n" : "")
                 + $"OLDER MESSAGES TO FOLD IN:\n{transcript}";

        // If the provider errors, the stream contains error text, not a summary — persisting it
        // would overwrite the rolling summary AND advance through_utc (those messages would never
        // be re-folded). Bail and retry on a later call instead.
        var failed = false;
        var sb = new StringBuilder();
        await foreach (var tok in _providers.Active.StreamReplyAsync(Prompts.Compaction, new[] { new ChatMessage("user", user) },
            onError: _ => failed = true, ct: ct))
            sb.Append(tok);

        var updated = sb.ToString().Trim();
        if (failed || updated.Length == 0) return 0;

        var newThrough = aged.Max(m => ParseUtc(m.CreatedUtc)).ToString("o");
        _db.SetCompaction(updated, newThrough);
        return aged.Count;
    }
}
