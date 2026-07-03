namespace SemanticPortrait.Core;

/// <summary>
/// The agent's hand-off MEMORY — a sense, not a wall. Observed live: the model re-sent the same
/// arc batch twice and the same safety check-in three times across turns, because it has no
/// memory of what it already handed off. This supplies that memory: a near-duplicate send gets
/// tipped off with WHAT it matched (the model judges the delta and re-sends only what's new);
/// the ledger lets tool results echo what's already gone so redundancy is inferable BEFORE
/// sending. Sliding window: recurring themes weeks apart are legitimately new hand-offs.
/// </summary>
public sealed class HandoffDeduper
{
    public const double Threshold = 0.80;
    private const int Window = 20;
    private const int SnippetLen = 80;

    private readonly IEmbedder _embedder;
    private readonly List<(float[] Vec, string Snippet)> _sent = new();
    private readonly object _gate = new();

    public HandoffDeduper(IEmbedder embedder) => _embedder = embedder;

    /// <summary>The snippet of the earlier hand-off this payload near-duplicates, or null when
    /// it's new (a new payload is recorded as sent). No embedder / failed embed → never blocks
    /// (better a duplicate analyst run than a silently dropped hand-off).</summary>
    public async Task<string?> MatchAsync(string payload, CancellationToken ct = default)
    {
        var vec = await _embedder.EmbedAsync(payload, ct);
        if (vec is null) return null;
        lock (_gate)
        {
            var hit = _sent.FirstOrDefault(s => Cosine(s.Vec, vec) >= Threshold);
            if (hit.Snippet is not null) return hit.Snippet;
            _sent.Add((vec, Snip(payload)));
            if (_sent.Count > Window) _sent.RemoveAt(0);
            return null;
        }
    }

    /// <summary>What's been handed off this session — the ledger echoed back in tool results so
    /// the agent can sense redundancy before sending.</summary>
    public string Ledger()
    {
        lock (_gate)
        {
            if (_sent.Count == 0) return "(first hand-off this session)";
            var recent = _sent.TakeLast(6).Select((s, i) => $"{_sent.Count - Math.Min(6, _sent.Count) + i + 1}) {s.Snippet}");
            return $"{_sent.Count} so far: " + string.Join(" · ", recent);
        }
    }

    private static string Snip(string s)
    {
        var line = s.ReplaceLineEndings(" ").Trim();
        return line.Length <= SnippetLen ? line : line[..SnippetLen] + "…";
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++) { dot += (double)a[i] * b[i]; na += (double)a[i] * a[i]; nb += (double)b[i] * b[i]; }
        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
