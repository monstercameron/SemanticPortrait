using System.Text.Json;

namespace SemanticPortrait.Core.Constellation;

/// <summary>Per-node computed metrics (design §4). Pure outputs of <see cref="ConstellationMetrics"/>.</summary>
public readonly record struct NodeMetric(
    long NodeId, string Label, string Category, bool Inferred, double Confidence,
    int Degree, int Contradiction, int Complexity, int Sides, bool IsCircle,
    Emotion Emotion, double Valence, double Intensity, double Energy, double? Volatility,
    double Salience, double Centrality, int ReferencingEntries, bool Quiet);

/// <summary>Per-edge computed metrics. A connection is multi-dimensional: its valence (helps/
/// harms), how sure the analyst is (Confidence/Uncertainty), whether it was inferred or stated,
/// how ALIVE it is right now (mean endpoint salience), and its index among parallel relations
/// between the same pair (so multiple typed threads stay individually visible).</summary>
public readonly record struct EdgeMetric(
    long EdgeId, long Src, long Dst, int Valence, double Uncertainty,
    double Confidence, bool Inferred, double Activity, int ParallelIndex);

/// <summary>Join coverage so a dead graph reads as "not linked yet," not "you feel nothing".</summary>
public sealed record JoinReport(int LinkedNodes, int TotalNodes, IReadOnlyList<string> UnmatchedRefs);

public sealed record ConstellationModel(
    IReadOnlyList<NodeMetric> Nodes, IReadOnlyList<EdgeMetric> Edges, JoinReport Join);

/// <summary>
/// Pure metrics layer: (nodes, edges, entry_meta) → a <see cref="ConstellationModel"/>. No DB, no
/// rendering, no clock dependency (now is injected). The entry→node join is explicit and reports
/// misses. See design/constellation.md §2/§4.
/// </summary>
public static class ConstellationMetrics
{
    public const double RecencyHalfLifeDays = 14.0;
    public const double VolatilityMinSamples = 5;

    /// <param name="resolveRef">Optional semantic fallback for the entry→node join: called for a
    /// ref string ONLY when token matching found nothing; returns the node ids it resolves to
    /// (alias registry / embeddings — supplied by the caller so this layer stays pure).</param>
    /// <param name="classifyMood">Optional mood classifier override (semantic upgrade of the
    /// lexicon); defaults to <see cref="EmotionColor.Classify"/>.</param>
    public static ConstellationModel Compute(
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<GraphEdge> edges,
        IReadOnlyList<EntryMeta> entries,
        DateTime nowUtc = default,
        Func<string, IReadOnlyCollection<long>>? resolveRef = null,
        Func<string, Emotion>? classifyMood = null)
    {
        if (nowUtc == default) nowUtc = DateTime.UtcNow;
        classifyMood ??= EmotionColor.Classify;

        // --- precompute the entry→node join ---
        // Each entry contributes its refs (topics ∪ people). Build, per node, the entries referencing it.
        var byNode = nodes.ToDictionary(n => n.Id, _ => new List<EntryMeta>());
        var matchedRefs = new HashSet<string>();
        var allRefs = new List<(EntryMeta Entry, string Raw)>();
        foreach (var e in entries)
            foreach (var raw in ParseRefs(e.Topics).Concat(ParseRefs(e.People)))
                allRefs.Add((e, raw));

        // Index nodes by token set once.
        var nodeTokens = nodes.ToDictionary(n => n.Id, n => Tokens(n.Label));
        // Semantic-resolution memo: one ladder call per distinct ref string, not per (entry, ref).
        var semanticMemo = new Dictionary<string, IReadOnlyCollection<long>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (entry, raw) in allRefs)
        {
            var refTokens = Tokens(raw);
            bool any = false;
            foreach (var n in nodes)
                if (Match(nodeTokens[n.Id], refTokens))
                {
                    byNode[n.Id].Add(entry);
                    any = true;
                }
            if (!any && resolveRef is not null)
            {
                // Token matching misses model-written variants ("rejection concern" vs
                // "rejection-radar") — the injected ladder (alias → embeddings) catches those.
                if (!semanticMemo.TryGetValue(raw, out var ids))
                    semanticMemo[raw] = ids = resolveRef(raw);
                foreach (var id in ids)
                    if (byNode.TryGetValue(id, out var list)) { list.Add(entry); any = true; }
            }
            if (any) matchedRefs.Add(raw);
        }
        var unmatched = allRefs.Select(r => r.Raw).Where(r => !matchedRefs.Contains(r)).Distinct().ToList();

        // --- edges touching each node ---
        var degree = nodes.ToDictionary(n => n.Id, _ => 0);
        var posDeg = nodes.ToDictionary(n => n.Id, _ => 0);
        var negDeg = nodes.ToDictionary(n => n.Id, _ => 0);
        var edgeTypes = nodes.ToDictionary(n => n.Id, _ => new HashSet<string>());
        foreach (var ed in edges)
        {
            int v = Ontology.EdgeValence(ed.Type);
            foreach (var endpoint in new[] { ed.Src, ed.Dst })
            {
                if (!degree.ContainsKey(endpoint)) continue;   // dangling edge → ignore
                degree[endpoint]++;
                edgeTypes[endpoint].Add(ed.Type);
                if (v > 0) posDeg[endpoint]++;
                else if (v < 0) negDeg[endpoint]++;
            }
        }

        // --- per-node emotion bundle from referencing entries ---
        var bundle = new Dictionary<long, (Emotion Em, double Val, double Inten, double Energy, double? Vol, int N, double RawSal)>();
        foreach (var n in nodes)
        {
            var es = byNode[n.Id];
            if (es.Count == 0)
            {
                bundle[n.Id] = (Emotion.Neutral, 0, 0, 0, null, 0, 0);   // quiet
                continue;
            }
            // Recency-WEIGHTED means (half-life = RecencyHalfLifeDays): the position answers
            // "where does this sit for them NOW", not "averaged over all time" — an entity's
            // June hope and July rejection shouldn't cancel into a washed-out middle forever.
            // Same-day entries weigh equally, so short fixtures behave like plain means. The
            // time scrub recomputes as-of a date, so positions visibly migrate through history.
            double wSum = 0, mv = 0, mi = 0, me = 0;
            foreach (var x in es)
            {
                var w = EntryRecencyWeight(x, nowUtc);
                wSum += w; mv += x.Valence * w; mi += x.Intensity * w; me += x.Energy * w;
            }
            if (wSum > 0) { mv /= wSum; mi /= wSum; me /= wSum; }
            double? vol = es.Count >= VolatilityMinSamples ? StdDev(es.Select(x => x.Valence)) : null;
            var em = DominantEmotion(es, classifyMood);
            double recency = RecencyWeight(es, nowUtc);
            double rawSal = es.Count * Math.Max(mi, 0.0) * recency;
            bundle[n.Id] = (em, mv, mi, me, vol, es.Count, rawSal);
        }

        double maxRawSal = bundle.Values.Select(b => b.RawSal).DefaultIfEmpty(0).Max();
        double maxCentralityRaw = nodes.Select(n => degree[n.Id] * Math.Log(1 + bundle[n.Id].N)).DefaultIfEmpty(0).Max();

        // --- assemble node metrics ---
        var nodeMetrics = new List<NodeMetric>(nodes.Count);
        foreach (var n in nodes)
        {
            var b = bundle[n.Id];
            int contradiction = Math.Min(posDeg[n.Id], negDeg[n.Id]);
            int complexity = edgeTypes[n.Id].Count + contradiction;
            int sides = Math.Clamp(3 + complexity, 3, 8);

            int netValence = posDeg[n.Id] - negDeg[n.Id];
            bool isCircle = degree[n.Id] >= 3 && b.N >= 3 && contradiction == 0
                            && n.Confidence >= 0.8 && netValence > 0;
            if (isCircle) sides = Geometry.CircleSides;

            double salience = maxRawSal > 0 ? b.RawSal / maxRawSal : 0;
            double centralityRaw = degree[n.Id] * Math.Log(1 + b.N);
            double centrality = maxCentralityRaw > 0 ? centralityRaw / maxCentralityRaw : 0;

            nodeMetrics.Add(new NodeMetric(
                n.Id, n.Label, n.Category, n.Inferred, n.Confidence,
                degree[n.Id], contradiction, complexity, sides, isCircle,
                b.Em, b.Val, b.Inten, b.Energy, b.Vol,
                salience, centrality, b.N, b.N == 0));
        }

        // --- edge metrics (after node salience exists: edge activity = how alive its ends are) ---
        var salByNode = nodeMetrics.ToDictionary(m => m.NodeId, m => m.Salience);
        var parallelSeen = new Dictionary<(long, long), int>();
        var edgeMetrics = new List<EdgeMetric>(edges.Count);
        foreach (var ed in edges)
        {
            var pair = ed.Src < ed.Dst ? (ed.Src, ed.Dst) : (ed.Dst, ed.Src);
            parallelSeen[pair] = parallelSeen.TryGetValue(pair, out var seen) ? seen + 1 : 0;
            double activity = (salByNode.GetValueOrDefault(ed.Src) + salByNode.GetValueOrDefault(ed.Dst)) / 2;
            edgeMetrics.Add(new EdgeMetric(ed.Id, ed.Src, ed.Dst,
                Ontology.EdgeValence(ed.Type), Math.Clamp(1 - ed.Confidence, 0, 1),
                Math.Clamp(ed.Confidence, 0, 1), ed.Inferred, activity, parallelSeen[pair]));
        }

        int linked = nodeMetrics.Count(m => m.ReferencingEntries > 0);
        return new ConstellationModel(nodeMetrics, edgeMetrics, new JoinReport(linked, nodes.Count, unmatched));
    }

    // ---- join helpers ----
    internal static IEnumerable<string> ParseRefs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) yield break;
        var s = json.Trim();
        if (s.StartsWith("["))
        {
            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(s); } catch { doc = null; }
            if (doc is not null)
            {
                using (doc)
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            var v = el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
                            if (!string.IsNullOrWhiteSpace(v)) yield return v!;
                        }
                yield break;
            }
        }
        // Fallback: comma/semicolon-separated or a single value.
        foreach (var part in s.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (part.Length > 0) yield return part;
    }

    internal static HashSet<string> Tokens(string? s)
    {
        var set = new HashSet<string>();
        if (string.IsNullOrWhiteSpace(s)) return set;
        var cur = new System.Text.StringBuilder();
        foreach (var ch in s.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) cur.Append(ch);
            else if (cur.Length > 0) { set.Add(cur.ToString()); cur.Clear(); }
        }
        if (cur.Length > 0) set.Add(cur.ToString());
        return set;
    }

    // Match if either token set fully contains the other (forgiving but boundary-safe). Empty never matches.
    internal static bool Match(HashSet<string> nodeTokens, HashSet<string> refTokens)
    {
        if (nodeTokens.Count == 0 || refTokens.Count == 0) return false;
        return nodeTokens.IsSubsetOf(refTokens) || refTokens.IsSubsetOf(nodeTokens);
    }

    // ---- stats ----
    private static double StdDev(IEnumerable<double> xs)
    {
        var arr = xs.ToArray();
        if (arr.Length < 2) return 0;
        double mean = arr.Average();
        return Math.Sqrt(arr.Sum(x => (x - mean) * (x - mean)) / arr.Length);
    }

    private static Emotion DominantEmotion(List<EntryMeta> es, Func<string, Emotion> classify)
    {
        var counts = new Dictionary<Emotion, int>();
        foreach (var e in es)
        {
            var em = classify(e.Mood);
            counts[em] = counts.GetValueOrDefault(em) + 1;
        }
        // Prefer a real (non-Unmapped/Neutral) emotion if any entry produced one; else fall back.
        Emotion best = Emotion.Unmapped; int bestC = -1;
        foreach (var kv in counts)
        {
            bool realer = kv.Key is not (Emotion.Unmapped or Emotion.Neutral);
            bool curReal = best is not (Emotion.Unmapped or Emotion.Neutral);
            if ((realer && !curReal) || (realer == curReal && kv.Value > bestC))
            { best = kv.Key; bestC = kv.Value; }
        }
        return best;
    }

    /// <summary>Per-entry recency weight: 0.5^(age/half-life); unparseable dates weigh 1 (never
    /// silently zero out an entry's contribution).</summary>
    private static double EntryRecencyWeight(EntryMeta e, DateTime nowUtc)
    {
        if (!DateTime.TryParse(e.EntryUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d))
            return 1.0;
        var days = Math.Max(0, (nowUtc - d.ToUniversalTime()).TotalDays);
        return Math.Pow(0.5, days / RecencyHalfLifeDays);
    }

    private static double RecencyWeight(List<EntryMeta> es, DateTime nowUtc)
    {
        DateTime mostRecent = DateTime.MinValue;
        foreach (var e in es)
            if (DateTime.TryParse(e.EntryUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d))
            {
                var u = d.ToUniversalTime();
                if (u > mostRecent) mostRecent = u;
            }
        if (mostRecent == DateTime.MinValue) return 1.0;
        double days = Math.Max(0, (nowUtc - mostRecent).TotalDays);
        return Math.Pow(0.5, days / RecencyHalfLifeDays);
    }
}
