namespace SemanticPortrait.Core.Constellation;

/// <summary>
/// One continuous channel of the generative visual fingerprint. Honesty guardrail (design §M5):
/// every channel names the exact metric it renders and the visual it drives — the sky is "your
/// data", never a personality type. Values are 0..1, per-map normalized (proportions and shares,
/// never raw counts, so a fuller life doesn't read as a different person).
/// </summary>
public readonly record struct FingerprintChannel(
    string Key,      // stable id: breadth / keystone / motion / warmth / tint / weave / texture
    string Name,     // human name shown in the legend
    double Value,    // 0..1 continuous — never a threshold/bucket
    string Visual,   // what it drives on screen
    string Metric);  // the formula, in words — the explainability contract

public sealed record Fingerprint(IReadOnlyList<FingerprintChannel> Channels)
{
    public double this[string key]
    {
        get { foreach (var c in Channels) if (c.Key == key) return c.Value; return 0; }
    }
}

/// <summary>
/// Pure mapping: ConstellationModel metrics → the seven fingerprint channels. Deterministic,
/// no clock, no randomness — the same model always wears the same sky.
/// </summary>
public static class FingerprintMetrics
{
    public static Fingerprint Compute(ConstellationModel m)
    {
        var nodes = m.Nodes;
        var linked = new List<NodeMetric>();
        foreach (var n in nodes) if (!n.Quiet) linked.Add(n);

        // breadth — connection density (introvert ↔ extrovert silhouette). Edges per node,
        // saturating at 2.5 (beyond that a self-map is "densely connected" — more adds nothing).
        double breadth = nodes.Count == 0 ? 0 : Math.Min(1, m.Edges.Count / (double)nodes.Count / 2.5);

        // keystone — how much the map leans on one anchor: Herfindahl concentration of the
        // centrality distribution, rescaled so an even spread reads 0 and a single pillar reads 1.
        double keystone = KeystoneConcentration(nodes);

        // motion — restlessness: the salience share carried by distortion nodes, blended with
        // mean volatility. Drives ambient movement only (per-node tremor stays per-node evidence).
        double totalSal = 0, distortionSal = 0, fireSal = 0;
        foreach (var n in nodes)
        {
            totalSal += n.Salience;
            if (n.Category is "distortion" or "distortion-machines") distortionSal += n.Salience;
            if (n.Category is "fire" or "joy") fireSal += n.Salience;
        }
        double vols = 0; int volN = 0;
        foreach (var n in nodes) if (n.Volatility is { } v) { vols += Math.Min(1, v * 2); volN++; }
        double meanVol = volN > 0 ? vols / volN : 0;
        double distortionShare = totalSal > 0 ? distortionSal / totalSal : 0;
        double motion = Math.Clamp(0.6 * distortionShare + 0.4 * meanVol, 0, 1);

        // warmth — the creative heat: fire/joy share of total salience.
        double warmth = totalSal > 0 ? Math.Clamp(fireSal / totalSal, 0, 1) : 0;

        // tint — how luminous the night is: recency-weighted mean valence over linked nodes.
        double meanVal = 0;
        if (linked.Count > 0) { foreach (var n in linked) meanVal += n.Valence; meanVal /= linked.Count; }
        double tint = Math.Clamp((meanVal + 1) / 2, 0, 1);

        // weave — modularity: the share of connected nodes living in real communities (component
        // size ≥ 3) rather than stray pairs. Distinct neighborhoods → distinct nebula territories.
        double weave = CommunityShare(nodes, m.Edges);

        // texture — how much of the model is inference vs stated: inferred nodes + edges over all.
        int total = nodes.Count + m.Edges.Count, inferred = 0;
        foreach (var n in nodes) if (n.Inferred) inferred++;
        foreach (var e in m.Edges) if (e.Inferred) inferred++;
        double texture = total > 0 ? inferred / (double)total : 0;

        return new Fingerprint(new[]
        {
            new FingerprintChannel("breadth", "breadth", breadth,
                "how open the sky is (vignette lifts as connections multiply)",
                "edges per node ÷ 2.5, capped at 1"),
            new FingerprintChannel("keystone", "keystone", keystone,
                "the core star's reach — ray span and aura",
                "concentration (Herfindahl) of the centrality distribution, 0 = even, 1 = one pillar"),
            new FingerprintChannel("motion", "restlessness", motion,
                "ambient drift of the whole field",
                "0.6 × distortion share of salience + 0.4 × mean volatility"),
            new FingerprintChannel("warmth", "fire", warmth,
                "gold warmth glowing through the fog",
                "fire/joy share of total salience"),
            new FingerprintChannel("tint", "valence", tint,
                "how luminous the night reads",
                "mean valence of linked nodes, −1..1 mapped to 0..1"),
            new FingerprintChannel("weave", "weave", weave,
                "how distinct the nebula territories are",
                "share of connected nodes in communities of 3+"),
            new FingerprintChannel("texture", "inference", texture,
                "graininess of the dust field",
                "inferred nodes + edges over all nodes + edges"),
        });
    }

    private static double KeystoneConcentration(IReadOnlyList<NodeMetric> nodes)
    {
        double sum = 0; int n = 0;
        foreach (var x in nodes) if (x.Centrality > 0) { sum += x.Centrality; n++; }
        if (n == 0 || sum <= 0) return 0;
        if (n == 1) return 1;
        double hhi = 0;
        foreach (var x in nodes)
            if (x.Centrality > 0) { var p = x.Centrality / sum; hhi += p * p; }
        return Math.Clamp((hhi - 1.0 / n) / (1 - 1.0 / n), 0, 1);
    }

    private static double CommunityShare(IReadOnlyList<NodeMetric> nodes, IReadOnlyList<EdgeMetric> edges)
    {
        if (nodes.Count == 0 || edges.Count == 0) return 0;
        var parent = new Dictionary<long, long>();
        long Find(long a) { while (parent.TryGetValue(a, out var p) && p != a) { parent[a] = parent.GetValueOrDefault(p, p); a = parent[a]; } return a; }
        foreach (var n in nodes) parent[n.NodeId] = n.NodeId;
        foreach (var e in edges)
            if (parent.ContainsKey(e.Src) && parent.ContainsKey(e.Dst))
            {
                var ra = Find(e.Src); var rb = Find(e.Dst);
                if (ra != rb) parent[ra] = rb;
            }
        var size = new Dictionary<long, int>();
        var connected = new HashSet<long>();
        foreach (var e in edges) { connected.Add(e.Src); connected.Add(e.Dst); }
        foreach (var id in connected)
            if (parent.ContainsKey(id)) { var r = Find(id); size[r] = size.GetValueOrDefault(r) + 1; }
        if (connected.Count == 0) return 0;
        int inCommunity = 0;
        foreach (var id in connected)
            if (parent.ContainsKey(id) && size[Find(id)] >= 3) inCommunity++;
        return inCommunity / (double)connected.Count;
    }
}
