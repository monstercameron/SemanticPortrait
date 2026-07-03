using System.Globalization;

namespace SemanticPortrait.Core.Constellation;

/// <summary>
/// Maps a <see cref="ConstellationModel"/> (metrics) → a <see cref="VisualModel"/> (render-agnostic
/// visual attributes), and lays nodes out RADIALLY on the affect circumplex: angle = emotion,
/// radius = closeness to the self. This is the single place where metric→channel scales live
/// (design §5). Pure + deterministic. Invariants: affect HALF-PLANES are exact (valence sign ↔
/// side of the vertical axis, energy half ↔ side of the horizontal), and lightness still encodes
/// valence — together these replace the older cartesian x-rank redundancy.
/// </summary>
public static class Encoding
{
    private const string Green = "#28e070", Red = "#ff2e6e", Grey = "#5a5a64";

    /// <summary>The id of the synthesized core star ("Od") used when the graph has no real
    /// core node yet — negative, so it can never collide with a DB rowid.</summary>
    public const long SyntheticCoreId = -999_999;

    /// <summary>The rendered world is widescreen: one normalized X unit spans this many times the
    /// height. Overlap math must weigh dx by it or circles that LOOK apart still get pushed.</summary>
    public const double WorldAspect = 1.5;

    public static VisualModel Build(ConstellationModel model)
    {
        // Pre-assign quiet nodes their belt slots (stable order by id → deterministic layout).
        var quietIds = model.Nodes.Where(m => m.Quiet && m.Category != "core")
            .OrderBy(m => m.NodeId).Select((m, i) => (m.NodeId, Index: i))
            .ToDictionary(t => t.NodeId, t => t.Index);

        // RADIAL circumplex layout (the classic affect circle): ANGLE encodes the emotion —
        // positive valence at 3 o'clock, high energy at 12, negative at 9, low at 6 — and
        // RADIUS encodes closeness to the self: salient + central themes orbit near the core,
        // dormant ones drift to the rim. Cartesian x=valence/y=energy piled real graphs (which
        // sit at mild-negative valence / mid energy) into one diagonal blob; polar placement
        // fans the same data out around the core. Half-plane membership is EXACT (negative
        // valence never crosses the vertical axis, low energy never the horizontal), and the
        // lightness channel still encodes valence — that pair replaces the old x-rank invariant.
        var planeNodes = model.Nodes
            .Where(m => m.Category != "core" && !quietIds.ContainsKey(m.NodeId)).ToList();
        var placed = RadialLayout(planeNodes, model.Edges, out var clustered, out var hubIds);

        var beltSlots = QuietRimSlots(quietIds.Count);

        var nodes = new List<VisualNode>(model.Nodes.Count);
        foreach (var m in model.Nodes)
        {
            // Position: radial circumplex; Core pinned center; quiet nodes on the unread belt.
            var (x, y) = m.Category == "core" ? (0.5, 0.5)
                : placed.TryGetValue(m.NodeId, out var p) ? p : (0.5, 0.5);
            if (quietIds.TryGetValue(m.NodeId, out var qi))
                (x, y) = beltSlots[qi];

            var fill = EmotionColor.ToHsl(m.Emotion, m.Valence, m.Intensity);
            double radius = 0.30 + 0.70 * Math.Sqrt(Math.Clamp(m.Centrality, 0, 1));   // area ∝ value
            if (quietIds.ContainsKey(m.NodeId)) radius = Math.Min(radius, 0.55);       // belt slots are tight
            double opacity = 0.35 + 0.65 * Math.Clamp(San(m.Confidence), 0, 1);
            double pulseAmp = 0.18 * San(m.Salience);             // fraction of radius (clamped vs size)
            double pulseFreq = 0.3 + 1.2 * Math.Clamp(San(m.Intensity), 0, 1);
            double tremor = m.Volatility is double v ? Math.Clamp(San(v), 0, 1) * 0.02 : 0;
            double glow = (m.Category is "fire" or "joy" || m.Emotion == Emotion.Creativity) ? San(m.Salience) : 0;

            // Temporal depth: how often entries have revisited this node → 0..3 core rings.
            int rings = m.ReferencingEntries switch { 0 => 0, < 3 => 1, < 7 => 2, _ => 3 };

            // NaN insurance: one bad metric must tint one node, not blank the whole SVG.
            // (Math.Clamp(NaN) is NaN, so the raw fields need it too.)
            if (double.IsNaN(x)) x = 0.5;
            if (double.IsNaN(y)) y = 0.5;

            nodes.Add(new VisualNode(
                m.NodeId, m.Label, m.Category, x, y,
                Math.Clamp(San(m.Valence), -1, 1), Math.Clamp(San(m.Energy), 0, 1),
                m.Sides, m.IsCircle,
                fill.H, fill.S, fill.L, Ontology.CategoryStroke(m.Category),
                radius, opacity, m.Inferred, pulseAmp, pulseFreq, tremor, glow,
                m.Quiet, m.Emotion == Emotion.Unmapped,
                Ambivalent: m.Contradiction > 0, Rings: rings, Explain(m)));
        }

        // The core Od — the center everything traces back to, and the BRIGHTEST star. A real
        // core node is pinned and amplified; a graph without one gets a synthetic Od so the
        // portrait always has a heart (it explains itself and how to make it real).
        var coreIdx = nodes.FindIndex(n => model.Nodes.Any(m => m.NodeId == n.Id && m.Category == "core"));
        long coreVid;
        if (coreIdx >= 0)
        {
            var c = nodes[coreIdx];
            nodes[coreIdx] = c with { Radius = Math.Max(c.Radius, 1.1), GlowWarmth = Math.Max(c.GlowWarmth, 0.9), Rings = 3 };
            coreVid = c.Id;
        }
        else
        {
            coreVid = SyntheticCoreId;
            // Only a NON-empty model earns the synthetic star: an empty model means locked or
            // brand-new, and a locked portrait must render as NOTHING, not as a lone Od.
            if (model.Nodes.Count > 0)
                nodes.Add(new VisualNode(SyntheticCoreId, "Od", "core", 0.5, 0.5, 0, 0.5,
                Geometry.CircleSides, true, 45, 0.85, 0.72, Ontology.CategoryStroke("core"),
                1.15, 1.0, false, 0.10, 0.5, 0, 1.0, false, false, false, 3,
                "Od — your core. Everything here traces back to it. It isn't written into your " +
                "graph yet: ask the agent to \"create my core self node\" and this star becomes real."));
        }

        var pinned = new HashSet<long>(quietIds.Keys) { coreVid };
        foreach (var m in model.Nodes) if (m.Category == "core") pinned.Add(m.NodeId);
        RelaxOverlaps(nodes, pinned, clustered);

        // Edges are multi-dimensional: color=valence, wiggle=uncertainty, thickness=confidence,
        // dashed=inferred, pulse=liveness. Parallel relations between one pair bow to ALTERNATING
        // sides (signed amplitude), widening per pair-index, so every typed thread stays visible.
        var edges = model.Edges.Select(e =>
        {
            var side = e.ParallelIndex % 2 == 0 ? 1 : -1;
            var spread = 1 + 0.6 * (e.ParallelIndex / 2);
            var amp = Math.Max(0.05, 0.04 + 0.10 * e.Uncertainty) * side * spread;
            return new VisualEdge(
                e.EdgeId, e.Src, e.Dst,
                e.Valence > 0 ? Green : e.Valence < 0 ? Red : Grey,
                1 + 4 * e.Uncertainty,                    // Frequency
                amp,
                e.Valence,
                Width: Math.Min(2.0, 0.8 + 2.2 * e.Confidence),   // >2px reads as rope, not thread
                Dashed: e.Inferred,
                PulseAmp: e.Activity > 0.08 ? 0.30 * e.Activity : 0,
                PulseFreq: 0.25 + 0.55 * e.Activity,
                Intra: IsIntra(model.Edges, e));
        }).ToList();

        // The self is a NETWORK, not an archipelago: strong ties run between peers (the real
        // edges above); weak ties tether everything else to a core concept, and every thread
        // ultimately traces back to the core Od. Which hub a free-floating node ties to is
        // ANALYZED, not just nearest-pixel: same-category hubs and similar-affect hubs win, and
        // a kindred tie draws slightly firmer. Synthetic, dashed, dim: structure, not evidence.
        {
            const string weakTie = "#4a4a5e";   // visible thread, still clearly quieter than evidence
            long wid = -1;   // negative synthetic ids can never collide with real edge ids
            var hubMetrics = planeNodes.Where(n => hubIds.Contains(n.NodeId) && placed.ContainsKey(n.NodeId)).ToList();
            NodeMetric? BestHub(NodeMetric n)
            {
                if (hubMetrics.Count == 0 || !placed.TryGetValue(n.NodeId, out var np)) return null;
                return hubMetrics.OrderBy(h =>
                {
                    var hp = placed[h.NodeId];
                    var dx = (hp.X - np.X) * WorldAspect; var dy = hp.Y - np.Y;
                    var affect = Math.Abs(San(h.Valence) - San(n.Valence)) / 2
                               + Math.Abs(San(h.Energy) - San(n.Energy));
                    return (dx * dx + dy * dy) * (h.Category == n.Category ? 0.55 : 1.0) * (1 + affect);
                }).ThenBy(h => h.NodeId).First();
            }
            foreach (var m in planeNodes)
            {
                var isHub = hubIds.Contains(m.NodeId);
                if (!isHub && clustered.Contains(m.NodeId)) continue;   // satellites: strong peer ties
                // gentle alternating bow: dead-straight synthetic spokes read mechanical
                var bow = (wid % 2 == 0 ? 1 : -1) * 0.09;
                if (isHub)
                {
                    edges.Add(new VisualEdge(wid--, coreVid, m.NodeId, weakTie,
                        Frequency: 0.5, Amplitude: bow, Valence: 0,
                        Width: 0.9, Dashed: true, PulseAmp: 0, PulseFreq: 0.3));
                    continue;
                }
                var best = BestHub(m);
                var anchor = best?.NodeId ?? coreVid;
                var kindred = best is { } bh && bh.Category == m.Category;
                edges.Add(new VisualEdge(wid--, anchor, m.NodeId, weakTie,
                    Frequency: 0.5, Amplitude: bow, Valence: 0,
                    Width: kindred ? 0.8 : 0.5, Dashed: true, PulseAmp: 0, PulseFreq: 0.3));
            }
            // The unread rim keeps the faintest thread of all back to the core.
            foreach (var qid in quietIds.Keys.OrderBy(q => q))
                edges.Add(new VisualEdge(wid--, coreVid, qid, weakTie,
                    Frequency: 1, Amplitude: 0.02, Valence: 0,
                    Width: 0.35, Dashed: true, PulseAmp: 0, PulseFreq: 0.3));
        }

        double meanVal = model.Nodes.Count > 0 ? model.Nodes.Average(n => n.Valence) : 0;
        double linkedFrac = model.Join.TotalNodes > 0 ? (double)model.Join.LinkedNodes / model.Join.TotalNodes : 0;
        return new VisualModel(nodes, edges, BuildHulls(model, nodes, clustered), meanVal, linkedFrac, model.Join,
            FingerprintMetrics.Compute(model));
    }

    // ---------------------------------------------------------------- radial cluster layout
    // Community-first placement (design/constellation_design2.png): the graph's connected
    // communities are the first-class layer. Each community's HUB (its most central member)
    // sits radially around the core in the community's aggregate affect direction; its
    // SATELLITES ring the hub, each on the side matching its OWN mood. Unconnected nodes form
    // the outer field at their exact affect angle. Edges therefore stay short and local — the
    // picture reads as named neighborhoods around a center, not a scatter.

    private static double Closeness(NodeMetric n) =>
        Math.Clamp(0.55 * San(n.Salience) + 0.45 * San(n.Centrality), 0, 1);

    private static double AffectAngleRaw(NodeMetric n)
    {
        var u = San(n.Valence); var v = San(n.Energy) * 2 - 1;
        if (Math.Abs(u) < 1e-9 && Math.Abs(v) < 1e-9)
            return n.NodeId * 2.399963 % (2 * Math.PI);   // neutral → deterministic scatter
        var a = Math.Atan2(v, u);
        return a < 0 ? a + 2 * Math.PI : a;
    }

    /// <summary>Shortest-arc blend from <paramref name="a"/> toward <paramref name="b"/>.</summary>
    private static double BlendAngles(double a, double b, double towardB)
    {
        var d = (b - a) % (2 * Math.PI);
        if (d > Math.PI) d -= 2 * Math.PI;
        if (d < -Math.PI) d += 2 * Math.PI;
        var r = a + d * towardB;
        return r < 0 ? r + 2 * Math.PI : r % (2 * Math.PI);
    }

    // Vertical scale is smaller than horizontal — the world is widescreen, so the field is a
    // wide ellipse and nothing touches the canvas edges.
    private static (double X, double Y) ToXY(double u, double v) =>
        (Math.Clamp(0.5 + 0.47 * u, 0.03, 0.97), Math.Clamp(0.5 - 0.41 * v, 0.05, 0.95));

    private static int RingCapacity(int ring) => ring == 0 ? 8 : 8 + 6 * ring;

    private static int RingCount(int satellites)
    {
        int rings = 0;
        for (var left = satellites; left > 0; rings++) left -= RingCapacity(rings);
        return rings;
    }

    private static Dictionary<long, (double X, double Y)> RadialLayout(
        List<NodeMetric> nodes, IReadOnlyList<EdgeMetric> edges,
        out HashSet<long> clustered, out HashSet<long> hubs)
    {
        var result = new Dictionary<long, (double X, double Y)>(nodes.Count);
        clustered = new HashSet<long>();
        hubs = new HashSet<long>();
        if (nodes.Count == 0) return result;

        // Communities over the plane subgraph (edges touching core/quiet nodes don't merge).
        var parent = nodes.ToDictionary(n => n.NodeId, n => n.NodeId);
        long Find(long x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        foreach (var e in edges)
            if (parent.ContainsKey(e.Src) && parent.ContainsKey(e.Dst))
                parent[Find(e.Src)] = Find(e.Dst);

        var groups = nodes.GroupBy(n => Find(n.NodeId)).ToList();
        var singles = groups.Where(g => g.Count() == 1).SelectMany(g => g).ToList();

        // --- layer 1+2: communities — hub radial by aggregate affect, satellites ring the hub ---
        var clusters = groups.Where(g => g.Count() >= 2).Select(g =>
        {
            var members = g.OrderBy(n => n.NodeId).ToList();
            double wu = 0, wv = 0;
            foreach (var n in members)
            {
                var w = 0.25 + San(n.Salience);
                wu += San(n.Valence) * w; wv += (San(n.Energy) * 2 - 1) * w;
            }
            double theta;
            if (Math.Abs(wu) < 1e-9 && Math.Abs(wv) < 1e-9) theta = g.Key * 2.399963 % (2 * Math.PI);
            else { theta = Math.Atan2(wv, wu); if (theta < 0) theta += 2 * Math.PI; }
            return (Members: members, Theta: theta, Heat: members.Max(Closeness));
        }).OrderBy(c => c.Theta).ThenBy(c => c.Members[0].NodeId).ToList();

        // Angular allocation: slots proportional to sqrt(size) in true-angle order, blended back
        // toward each community's own affect angle — direction stays honest, clusters never merge.
        var total = clusters.Sum(c => Math.Sqrt(c.Members.Count) + 0.6);
        double cursor = 0;
        foreach (var c in clusters)
        {
            var span = 2 * Math.PI * (Math.Sqrt(c.Members.Count) + 0.6) / total;
            var ang = BlendAngles(c.Theta, cursor + span / 2, 0.6);
            cursor += span;

            var hub = c.Members.OrderByDescending(n => 0.6 * San(n.Centrality) + 0.4 * San(n.Salience))
                               .ThenBy(n => n.NodeId).First();
            // Hotter communities orbit nearer the core — but a big cluster's outermost ring must
            // still FIT inside the plane, or its far side piles against the rim clamp.
            var ringsNeeded = RingCount(c.Members.Count - 1);
            var maxD = ringsNeeded == 0 ? 0 : 0.17 + 0.14 * (ringsNeeded - 1);
            var rHub = Math.Min(0.34 + 0.24 * (1 - c.Heat), Math.Max(0.20, 0.90 - maxD));
            double hu = rHub * Math.Cos(ang), hv = rHub * Math.Sin(ang);
            result[hub.NodeId] = ToXY(hu, hv);
            clustered.Add(hub.NodeId);
            hubs.Add(hub.NodeId);

            // Satellites on rings: slot spacing guarantees local air; each slot is pulled toward
            // the satellite's OWN affect angle, so grief hangs low-left of its hub, joy upper-right.
            var sats = c.Members.Where(n => n.NodeId != hub.NodeId)
                .OrderBy(AffectAngleRaw).ThenBy(n => n.NodeId).ToList();
            int done = 0, ring = 0;
            while (done < sats.Count)
            {
                var cap = RingCapacity(ring);
                var count = Math.Min(cap, sats.Count - done);
                var d = 0.17 + 0.14 * ring;
                for (var s = 0; s < count; s++, done++)
                {
                    var n = sats[done];
                    var slot = 2 * Math.PI * (s + 0.5 * (ring % 2)) / count;
                    var dir = BlendAngles(slot, AffectAngleRaw(n), 0.35);
                    result[n.NodeId] = ToXY(hu + d * Math.Cos(dir), hv + d * Math.Sin(dir));
                    clustered.Add(n.NodeId);
                }
                ring++;
            }
        }

        // --- layer 3: unconnected nodes — the outer field, exact affect quadrant, fanned ---
        const double inset = 7 * Math.PI / 180;
        foreach (var quadrant in singles.GroupBy(n => (Pos: San(n.Valence) >= 0, Hi: San(n.Energy) >= 0.5)))
        {
            double start = (quadrant.Key.Pos, quadrant.Key.Hi) switch
            {
                (true, true) => 0,
                (false, true) => Math.PI / 2,
                (false, false) => Math.PI,
                _ => 3 * Math.PI / 2,
            };
            double InQuadrant(NodeMetric n) =>
                Math.Clamp(AffectAngleRaw(n), start + inset, start + Math.PI / 2 - inset);

            // Category-first ordering: same-domain singles land ADJACENT in the fan, so they
            // form coherent arcs that the hull layer can outline and NAME as star groups.
            var members = quadrant.OrderBy(n => n.Category, StringComparer.Ordinal)
                .ThenBy(InQuadrant).ThenBy(n => n.NodeId).ToList();
            // Sparse graphs make this the DOMINANT layer (17 edges / 83 nodes observed live), so
            // the field is a deep annulus, filled area-uniformly: radius rank-spread between the
            // squared band edges keeps density even instead of piling a thick rim arc.
            var closeRank = members.OrderByDescending(Closeness).ThenBy(n => n.NodeId)
                .Select((n, k) => (n.NodeId, k)).ToDictionary(t => t.NodeId, t => t.k);
            const double rIn = 0.46, rOut = 0.90;   // rim pulled in — the ellipse bottom is flat
                                                    // near 6 o'clock and must not read as a row
            for (var i = 0; i < members.Count; i++)
            {
                var n = members[i];
                var rankA = start + inset + (Math.PI / 2 - 2 * inset) * (i + 0.5) / members.Count;
                var angle = 0.15 * InQuadrant(n) + 0.85 * rankA;
                var rr = (closeRank[n.NodeId] + 0.5) / members.Count;
                var rMix = Math.Clamp(0.25 * (1 - Closeness(n)) + 0.75 * rr, 0, 1);
                var r = Math.Sqrt(rIn * rIn + (rOut * rOut - rIn * rIn) * rMix);
                result[n.NodeId] = ToXY(r * Math.Cos(angle), r * Math.Sin(angle));
            }
        }
        return result;
    }

    /// <summary>
    /// The UNREAD RIM: quiet (entry-unlinked) nodes circle the outermost ring, beyond the affect
    /// field — distant unread stars, not rows glued to an edge. Angle carries no affect meaning
    /// out here (their metrics are all zero); even spacing with a small deterministic wobble and
    /// an alternating two-shell stagger keeps the ring organic and collision-free.
    /// </summary>
    private static List<(double X, double Y)> QuietRimSlots(int count)
    {
        var slots = new List<(double X, double Y)>(count);
        for (var i = 0; i < count; i++)
        {
            var a = 2 * Math.PI * (i + 0.5) / Math.Max(1, count) + (i % 3 - 1) * 0.05;
            var r = 0.96 + i % 2 * 0.05;
            slots.Add(ToXY(r * Math.Cos(a), r * Math.Sin(a)));
        }
        return slots;
    }

    /// <summary>
    /// Asterisms: the NAMED gestalt layer, visible zoomed out. Two kinds of star group:
    /// connected communities (≥3 members), and category ARCS in the singles field — same-domain
    /// free-floaters fan adjacently (the layout sorts them so), and ≥3 of them share a concept
    /// worth naming. Names come from the shared concept: the dominant token across member
    /// labels; a community falls back to its hub's label, an arc to its category.
    /// </summary>
    private static List<VisualHull> BuildHulls(ConstellationModel model, List<VisualNode> visualNodes,
        HashSet<long> clustered)
    {
        var byId = visualNodes.ToDictionary(n => n.Id);
        var parent = model.Nodes.ToDictionary(n => n.NodeId, n => n.NodeId);
        long Find(long x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        foreach (var e in model.Edges)
            if (parent.ContainsKey(e.Src) && parent.ContainsKey(e.Dst))
                parent[Find(e.Src)] = Find(e.Dst);

        var hulls = new List<VisualHull>();
        void AddHull(List<NodeMetric> members, string fallbackName)
        {
            var pts = members.Select(m => new Pt(byId[m.NodeId].X, byId[m.NodeId].Y)).ToList();
            var hull = Geometry.Expand(Geometry.ConvexHull(pts), 0.045);
            if (hull.Length < 3) return;
            var domCat = members.GroupBy(m => m.Category).OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key).First().Key;
            var name = ConceptName(members.Select(m => m.Label).ToList()) ?? fallbackName;
            hulls.Add(new VisualHull(hull, Ontology.CategoryStroke(domCat), name));
        }

        // Connected communities — fallback name: the hub's own label (short) or its domain.
        foreach (var comp in model.Nodes.Where(n => !n.Quiet && n.Category != "core").GroupBy(n => Find(n.NodeId)))
        {
            var members = comp.ToList();
            if (members.Count < 3) continue;
            var hub = members.OrderByDescending(m => m.Centrality).ThenBy(m => m.NodeId).First();
            var domCat = members.GroupBy(m => m.Category).OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key).First().Key;
            AddHull(members, hub.Label.Length <= 20 ? hub.Label.Replace('-', ' ') : domCat);
        }

        // Category arcs in the singles field — the shared concept is the category itself.
        var singles = model.Nodes
            .Where(n => !n.Quiet && n.Category != "core" && !clustered.Contains(n.NodeId)).ToList();
        foreach (var arc in singles.GroupBy(n => (Pos: San(n.Valence) >= 0, Hi: San(n.Energy) >= 0.5, n.Category)))
        {
            var members = arc.ToList();
            if (members.Count < 3) continue;
            AddHull(members, arc.Key.Category);
        }

        // One NAME per concept: when two groups share a name (a fire community + a fire arc),
        // only the larger keeps the label — twin floating names read as a rendering bug.
        foreach (var dup in hulls.GroupBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
            foreach (var extra in dup.OrderByDescending(h => HullArea(h.Points)).Skip(1))
                hulls[hulls.IndexOf(extra)] = extra with { Name = "" };
        return hulls;
    }

    private static double HullArea(IReadOnlyList<Pt> pts)
    {
        double a = 0;
        for (var i = 0; i < pts.Count; i++)
        {
            var (p, q) = (pts[i], pts[(i + 1) % pts.Count]);
            a += p.X * q.Y - q.X * p.Y;
        }
        return Math.Abs(a) / 2;
    }

    /// <summary>The group's shared concept, if the labels agree on one: the token (≥4 chars)
    /// present in at least half the member labels. Null when no concept dominates.</summary>
    private static string? ConceptName(IReadOnlyList<string> labels)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in labels)
            foreach (var t in ConstellationMetrics.Tokens(l).Where(t => t.Length >= 4 && !HullStop.Contains(t)))
                counts[t] = counts.GetValueOrDefault(t) + 1;
        var need = Math.Max(2, (labels.Count + 1) / 2);
        return counts.Where(kv => kv.Value >= need)
            .OrderByDescending(kv => kv.Value).ThenByDescending(kv => kv.Key.Length).ThenBy(kv => kv.Key)
            .Select(kv => kv.Key).FirstOrDefault();
    }

    private static readonly HashSet<string> HullStop = new(StringComparer.OrdinalIgnoreCase)
        { "with", "from", "your", "this", "that", "into", "over" };

    /// <summary>
    /// Bounded, deterministic 2D overlap relaxation (design P3). Overlapping pairs are pushed
    /// apart along their separation direction. UNCONNECTED nodes are caged inside their affect
    /// half-planes (their position IS their mood — a grief node slid into the positive half
    /// would be a lie); community members may roam the plane (their position is their
    /// neighborhood; their mood lives in their color). Core and belt nodes are pinned.
    /// </summary>
    private static void RelaxOverlaps(List<VisualNode> nodes, HashSet<long> pinnedIds, HashSet<long> clusteredIds)
    {
        const int passes = 160;
        bool Movable(VisualNode n) => !pinnedIds.Contains(n.Id);
        // Cage ceilings/floors match the plane's ellipse (y ∈ [0.09, 0.91] after the vertical
        // flatten), so relaxation can't shove crowded nodes flush against the canvas walls.
        (double lo, double hi) XCage(VisualNode n) =>
            clusteredIds.Contains(n.Id) ? (0.04, 0.96) : n.Valence >= 0 ? (0.515, 0.96) : (0.04, 0.485);
        (double lo, double hi) YCage(VisualNode n) =>
            clusteredIds.Contains(n.Id) ? (0.09, 0.91) : n.Energy >= 0.5 ? (0.09, 0.485) : (0.515, 0.91);
        VisualNode Caged(VisualNode n, double x, double y)
        {
            var (xlo, xhi) = XCage(n); var (ylo, yhi) = YCage(n);
            return n with { X = Math.Clamp(x, xlo, xhi), Y = Math.Clamp(y, ylo, yhi) };
        }

        for (var pass = 0; pass < passes; pass++)
        {
            var moved = false;
            for (var i = 0; i < nodes.Count; i++)
                for (var j = i + 1; j < nodes.Count; j++)
                {
                    var a = nodes[i]; var b = nodes[j];
                    if (!Movable(a) && !Movable(b)) continue;
                    // Matches the view's rendered radii ((11 + 26R)px on the ~820px plane span)
                    // plus a visible gap — "not overlapping" is defined by what the user SEES.
                    var minD = 0.034 + (a.Radius + b.Radius) * 0.037;
                    // dx weighed by the world aspect: distances are judged as RENDERED, not in
                    // normalized units, or horizontally-clear circles still get pushed apart.
                    var dx = (b.X - a.X) * WorldAspect; var dy = b.Y - a.Y;
                    var d = Math.Sqrt(dx * dx + dy * dy);
                    if (d >= minD) continue;
                    var push = (minD - d) / 2 + 0.001;
                    double ux, uy;
                    if (d > 1e-9) { ux = dx / d; uy = dy / d; }
                    else { ux = 0; uy = a.Id < b.Id ? 1 : -1; }   // deterministic tie-break
                    if (Movable(a)) nodes[i] = Caged(a, a.X - push * ux / WorldAspect, a.Y - push * uy);
                    if (Movable(b)) nodes[j] = Caged(b, b.X + push * ux / WorldAspect, b.Y + push * uy);
                    moved = true;
                }
            if (!moved) break;
        }
    }

    /// <summary>
    /// "Same community" for the intra-cluster brightening (design punch list): NOT simply "both
    /// endpoints end up in the same connected component," because ANY real edge trivially unions
    /// its own two endpoints — under that reading every edge would be intra, including the one
    /// link that stitches two otherwise-separate clusters together. Instead: exclude this edge and
    /// re-run union-find over every OTHER real edge. If the endpoints are STILL connected (some
    /// other path — a cycle, a parallel relation), this edge is redundant scaffolding inside a
    /// genuinely multi-connected community → intra. If removing it splits the graph, this edge
    /// IS the sole connector (a graph-theoretic bridge/cut-edge) → not intra. A lone two-node pair
    /// with a single edge and no other path also reads as not-intra by this test, which matches
    /// BuildHulls' own ≥3-member threshold for drawing a community's fog in the first place.
    /// </summary>
    private static bool IsIntra(IReadOnlyList<EdgeMetric> allEdges, EdgeMetric target)
    {
        if (target.Src == target.Dst) return true;
        var parent = new Dictionary<long, long>();
        long Find(long x)
        {
            if (!parent.ContainsKey(x)) parent[x] = x;
            while (parent[x] != x) x = parent[x] = parent[parent[x]];
            return x;
        }
        foreach (var e in allEdges)
        {
            if (e.EdgeId == target.EdgeId) continue;   // the edge under test never votes for itself
            parent[Find(e.Src)] = Find(e.Dst);
        }
        return Find(target.Src) == Find(target.Dst);
    }

    private static double San(double v) => double.IsNaN(v) ? 0 : v;

    // The honesty inspector sentence — every node can explain itself.
    private static string Explain(NodeMetric m)
    {
        var c = CultureInfo.InvariantCulture;
        if (m.Quiet)
            return $"\"{m.Label}\" — not yet linked to any entry (quiet). Shape from {m.Degree} structural link(s).";
        string shape = m.IsCircle ? "a circle (resolved: well-evidenced, no contradiction)"
                                  : $"a {m.Sides}-sided shape ({m.Complexity} facets = {m.Degree} links + {m.Contradiction} contradiction)";
        string emo = m.Emotion == Emotion.Unmapped ? "mood unread" : m.Emotion.ToString().ToLowerInvariant();
        string vol = m.Volatility is double v ? $", volatility {v.ToString("0.00", c)}" : "";
        return $"\"{m.Label}\" — {shape}; {emo}, valence {m.Valence.ToString("+0.0;-0.0;0.0", c)}, "
             + $"salience {(m.Salience * 100).ToString("0", c)}%{vol}; from {m.ReferencingEntries} entr"
             + (m.ReferencingEntries == 1 ? "y." : "ies.");
    }
}
