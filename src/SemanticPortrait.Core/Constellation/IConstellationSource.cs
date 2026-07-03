using System.Collections.Concurrent;

namespace SemanticPortrait.Core.Constellation;

/// <summary>The two renderings of one self-model: the private Observatory (labeled, inspectable —
/// useful) and the public Sigil (aggregate-derived artwork — masked by construction).</summary>
public sealed record ConstellationBundle(VisualModel Visual, SigilPainting Sigil);

/// <summary>
/// The decoupling seam: anything that can produce a <see cref="ConstellationBundle"/> for the
/// renderers. Views depend ONLY on the bundle, never on the DB — so the design can be prototyped
/// against a fixture (<see cref="SampleConstellationSource"/>) and swapped to live data
/// (<see cref="DbConstellationSource"/>) without touching the render layer. Async because the
/// live source runs semantic joins/classification through the embedder.
/// </summary>
public interface IConstellationSource
{
    Task<ConstellationBundle> BuildAsync();
}

/// <summary>
/// Live source: reads the encrypted graph + entry metadata and runs the metrics→encoding pipeline,
/// with the SEMANTIC upgrades injected into the pure layers:
///  - entry→node join: refs the token matcher misses resolve via alias registry + node embeddings
///    ("rejection concern" → rejection-radar) — the same ladder recall uses.
///  - mood→emotion: lexicon first; unlexiconed moods ("sadge", "guarded interest") classify by
///    embedding similarity to per-emotion anchor texts.
/// Both memoize process-wide: mood strings and ref strings recur constantly across rebuilds.
/// </summary>
public sealed class DbConstellationSource : IConstellationSource
{
    /// <summary>Ref→node cosine floor. Slightly below the resolver's entity floor: a topic tag is
    /// vaguer than a deliberate entity mention, and a wrong join here only tints a node's mood
    /// blend rather than asserting an identity.</summary>
    public const double JoinThreshold = 0.55;
    /// <summary>Mood→anchor cosine floor; below it the mood stays honestly Unmapped.</summary>
    public const double MoodThreshold = 0.35;

    // Mood→emotion is DB-independent (a string classifies the same everywhere) → process-wide.
    private static readonly ConcurrentDictionary<string, Emotion> _moodMemo = new(StringComparer.OrdinalIgnoreCase);
    // Ref→node-ids is NOT: ids are per-database, and one session can host both the sandbox and
    // the real DB — a static memo could join a ref to the WRONG database's node when ids
    // coincide. Instance-scoped (the source is a singleton per Db anyway).
    private readonly ConcurrentDictionary<string, long[]> _refMemo = new(StringComparer.OrdinalIgnoreCase);
    private string? _memoDbPath;
    private static readonly SemaphoreSlim _anchorGate = new(1, 1);
    private static Dictionary<Emotion, float[]>? _anchors;

    private readonly Db _db;
    private readonly IEmbedder? _embedder;

    public DbConstellationSource(Db db, IEmbedder? embedder = null) { _db = db; _embedder = embedder; }

    public Task<ConstellationBundle> BuildAsync() => BuildAsync(null);

    /// <summary>Build the bundle, optionally AS OF a past moment (the time scrub): only nodes/
    /// edges/entries that existed then, with recency/salience computed against that moment —
    /// what the portrait actually looked like, not today's portrait minus rows.</summary>
    public async Task<ConstellationBundle> BuildAsync(DateTime? asOfUtc)
    {
        if (!_db.IsOpen)
        {
            var empty = new ConstellationModel(Array.Empty<NodeMetric>(), Array.Empty<EdgeMetric>(),
                new JoinReport(0, 0, Array.Empty<string>()));
            return new ConstellationBundle(Encoding.Build(empty), SigilForge.Paint(SigilForge.From(empty)));
        }

        // The Db instance can be redirected to a different file within one lifetime (dev
        // sandbox) — a ref memo built against another database's node ids must not survive that.
        if (_db.CurrentPath != _memoDbPath) { _refMemo.Clear(); _memoDbPath = _db.CurrentPath; }

        var iso = asOfUtc?.ToUniversalTime().ToString("o");
        var nodes = iso is null ? _db.GetNodes() : _db.GetNodesAsOf(iso);
        var edges = iso is null ? _db.GetEdges() : _db.GetEdgesAsOf(iso);
        var entries = _db.GetAllEntryMeta();
        if (iso is not null)
            entries = entries.Where(e => string.CompareOrdinal(e.EntryUtc, iso) <= 0).ToList();

        // Pre-resolve everything async (Compute itself stays pure + synchronous).
        var classify = await BuildMoodClassifierAsync(entries);
        var resolve = await BuildRefResolverAsync(nodes, entries);

        var model = ConstellationMetrics.Compute(nodes, edges, entries,
            nowUtc: asOfUtc?.ToUniversalTime() ?? default,
            resolveRef: resolve, classifyMood: classify);
        return new ConstellationBundle(Encoding.Build(model), SigilForge.Paint(SigilForge.From(model)));
    }

    // ---- mood → emotion: lexicon, then embedding-nearest-anchor, else Unmapped ----
    private async Task<Func<string, Emotion>> BuildMoodClassifierAsync(IReadOnlyList<EntryMeta> entries)
    {
        if (_embedder is null) return EmotionColor.Classify;

        foreach (var mood in entries.Select(e => e.Mood).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(mood) || _moodMemo.ContainsKey(mood)) continue;
            var lex = EmotionColor.Classify(mood);
            if (lex != Emotion.Unmapped) { _moodMemo[mood] = lex; continue; }

            var anchors = await AnchorsAsync();
            var vec = anchors is null ? null : await _embedder.EmbedAsync(mood);
            if (vec is null) { _moodMemo[mood] = Emotion.Unmapped; continue; }
            Emotion best = Emotion.Unmapped; double bestScore = MoodThreshold;
            foreach (var (em, av) in anchors!)
            {
                var s = Cosine(av, vec);
                if (s > bestScore) { best = em; bestScore = s; }
            }
            _moodMemo[mood] = best;
        }
        return mood => string.IsNullOrWhiteSpace(mood) ? Emotion.Unmapped
            : _moodMemo.TryGetValue(mood, out var e) ? e : EmotionColor.Classify(mood);
    }

    private async Task<Dictionary<Emotion, float[]>?> AnchorsAsync()
    {
        if (_anchors is not null || _embedder is null) return _anchors;
        await _anchorGate.WaitAsync();
        try
        {
            if (_anchors is not null) return _anchors;
            var built = new Dictionary<Emotion, float[]>();
            foreach (var (em, text) in AnchorTexts)
            {
                var v = await _embedder.EmbedAsync(text);
                if (v is null) return null;   // embedder unavailable → lexicon-only this session
                built[em] = v;
            }
            _anchors = built;
            return _anchors;
        }
        finally { _anchorGate.Release(); }
    }

    private static readonly (Emotion, string)[] AnchorTexts =
    {
        (Emotion.Anger, "angry furious enraged irritated frustrated resentful mad"),
        (Emotion.Fear, "anxious afraid scared nervous worried dreading panicked insecure uneasy"),
        (Emotion.Sadness, "sad depressed grieving hopeless lonely hurt heartbroken dejected rejected down"),
        (Emotion.Joy, "happy joyful glad excited elated grateful hopeful playful delighted"),
        (Emotion.Calm, "calm peaceful relaxed settled steady grounded serene at ease"),
        (Emotion.Creativity, "creative inspired productive in flow building energized motivated driven"),
        (Emotion.Love, "loving affectionate tender longing yearning warm attached romantic"),
        (Emotion.Disgust, "disgusted repulsed ashamed guilty gross self-loathing"),
    };

    // ---- entry ref → node ids: alias registry, then node-embedding similarity ----
    private async Task<Func<string, IReadOnlyCollection<long>>> BuildRefResolverAsync(
        IReadOnlyList<GraphNode> nodes, IReadOnlyList<EntryMeta> entries)
    {
        if (_embedder is null) return _ => Array.Empty<long>();

        var liveIds = nodes.Select(n => n.Id).ToHashSet();
        var tokensByNode = nodes.ToDictionary(n => n.Id, n => ConstellationMetrics.Tokens(n.Label));

        // Only refs the token matcher will miss need the ladder — precompute those async.
        var missing = entries
            .SelectMany(e => ConstellationMetrics.ParseRefs(e.Topics).Concat(ConstellationMetrics.ParseRefs(e.People)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(raw =>
            {
                var rt = ConstellationMetrics.Tokens(raw);
                return !nodes.Any(n => ConstellationMetrics.Match(tokensByNode[n.Id], rt));
            });

        foreach (var raw in missing)
        {
            if (_refMemo.TryGetValue(raw, out var cached) && cached.Any(liveIds.Contains)) continue;
            // Tier 1: alias registry (then exact label on the canonical name).
            var canonical = _db.ResolveCanonical(raw.Trim());
            var byLabel = _db.FindNodesByLabel(canonical).Select(n => n.Id).ToArray();
            if (byLabel.Length > 0) { _refMemo[raw] = byLabel; continue; }
            // Tier 2: node-embedding similarity.
            var vec = await _embedder.EmbedAsync(raw);
            _refMemo[raw] = vec is null
                ? Array.Empty<long>()
                : _db.SearchNodes(vec, 3).Where(h => h.Score >= JoinThreshold).Select(h => h.Node.Id).ToArray();
        }

        return raw => _refMemo.TryGetValue(raw, out var ids)
            ? ids.Where(liveIds.Contains).ToArray() : Array.Empty<long>();
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++) { dot += (double)a[i] * b[i]; na += (double)a[i] * a[i]; nb += (double)b[i] * b[i]; }
        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
