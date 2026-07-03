using SemanticPortrait.Core;
using SemanticPortrait.Core.Constellation;

namespace SemanticPortrait.Tests;

public class ConstellationEncodingTests
{
    private static readonly DateTime Now = new(2026, 6, 21, 0, 0, 0, DateTimeKind.Utc);
    private static string Recent => new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc).ToString("o");
    private static GraphNode Node(long id, string label, string cat = "self", double conf = 1.0, bool inf = false)
        => new(id, cat, label, inf, conf);
    private static EntryMeta Entry(long id, string mood, double val, string topics)
        => new(id, Recent, mood, val, 0.5, 0.5, topics, "[]", "s");

    private static VisualModel Build(IReadOnlyList<GraphNode> n, IReadOnlyList<GraphEdge> e, IReadOnlyList<EntryMeta> m)
        => Encoding.Build(ConstellationMetrics.Compute(n, e, m, Now));

    [Fact]
    public void All_visual_fields_are_in_range()
    {
        var nodes = new[] { Node(1, "a"), Node(2, "b", inf: true, conf: 0.3) };
        var entries = new[] { Entry(10, "anxious", -0.5, "[\"a\"]"), Entry(11, "happy", 0.7, "[\"b\"]") };
        var vm = Build(nodes, Array.Empty<GraphEdge>(), entries);
        foreach (var n in vm.Nodes)
        {
            Assert.InRange(n.X, 0, 1); Assert.InRange(n.Y, 0, 1);
            Assert.InRange(n.FillH, 0, 360); Assert.InRange(n.FillS, 0, 1); Assert.InRange(n.FillL, 0, 1);
            Assert.InRange(n.Opacity, 0, 1);
            Assert.True(n.Radius > 0);
            Assert.True(n.PulseAmp >= 0 && n.TremorAmp >= 0);
            Assert.False(string.IsNullOrEmpty(n.Why));
        }
    }

    [Fact]
    public void Core_is_pinned_center()
    {
        var vm = Build(new[] { Node(1, "SAM", "core") }, Array.Empty<GraphEdge>(), Array.Empty<EntryMeta>());
        Assert.Equal(0.5, vm.Nodes[0].X, 6);
        Assert.Equal(0.5, vm.Nodes[0].Y, 6);
    }

    [Fact]
    public void Valence_redundancy_half_plane_side_and_lightness_encode_valence()
    {
        // Radial layout (2026-07-03): the old cartesian x-rank redundancy is replaced by
        // (a) EXACT half-plane membership — valence sign decides the side of the vertical
        // axis — and (b) the lightness channel still ranking with valence.
        var nodes = new[] { Node(1, "low"), Node(2, "mid"), Node(3, "high") };
        var entries = new[]
        {
            Entry(10, "anxious", -0.6, "[\"low\"]"),
            Entry(11, "anxious",  0.1, "[\"mid\"]"),
            Entry(12, "anxious",  0.6, "[\"high\"]"),
        };
        var vm = Build(nodes, Array.Empty<GraphEdge>(), entries);
        var real = vm.Nodes.Where(n => n.Id > 0).ToList();   // exclude the synthesized core star

        foreach (var n in real)
            Assert.True(n.Valence >= 0 == n.X > 0.5,
                $"{n.Label}: valence {n.Valence:0.##} on the wrong side of the axis (x={n.X:0.###})");

        var byVal = real.OrderBy(n => n.Valence).Select(n => n.Id).ToArray();
        var byL = real.OrderBy(n => n.FillL).Select(n => n.Id).ToArray();
        Assert.Equal(byVal, byL);   // lightness remains a faithful valence channel
    }

    [Fact]
    public void Edge_color_follows_valence()
    {
        var nodes = new[] { Node(1, "a"), Node(2, "b"), Node(3, "c") };
        var edges = new[]
        {
            new GraphEdge(1, 1, 2, "lifts", "lifts", false, 1.0),     // +
            new GraphEdge(2, 1, 3, "drains", "drains", false, 0.5),   // −, uncertain
            new GraphEdge(3, 2, 3, "causes", "causes", false, 1.0),   // neutral
        };
        var vm = Build(nodes, edges, Array.Empty<EntryMeta>());
        Assert.Equal("#28e070", vm.Edges.Single(e => e.Src == 1 && e.Dst == 2).ColorHex);
        Assert.Equal("#ff2e6e", vm.Edges.Single(e => e.Src == 1 && e.Dst == 3).ColorHex);
        Assert.Equal("#5a5a64", vm.Edges.Single(e => e.Src == 2 && e.Dst == 3).ColorHex);
        // uncertain edge waves more than a certain one
        Assert.True(vm.Edges.Single(e => e.Dst == 3 && e.Src == 1).Frequency
                  > vm.Edges.Single(e => e.Src == 1 && e.Dst == 2).Frequency);
    }

    [Fact]
    public void Edges_carry_their_dimensions_and_parallels_bow_apart()
    {
        var nodes = new[] { Node(1, "a"), Node(2, "b") };
        var edges = new[]
        {
            new GraphEdge(11, 1, 2, "amplifies", "amplifies", true, 0.9),      // sure, inferred
            new GraphEdge(12, 1, 2, "keeps-alive", "keeps-alive", false, 0.3), // shaky, stated
        };
        var entries = new[] { Entry(20, "anxious", -0.3, "[\"a\"]"), Entry(21, "anxious", -0.3, "[\"b\"]") };
        var vm = Build(nodes, edges, entries);

        var e1 = vm.Edges.Single(x => x.Id == 11);
        var e2 = vm.Edges.Single(x => x.Id == 12);
        Assert.True(e1.Width > e2.Width);                                 // thickness = confidence
        Assert.True(e1.Dashed); Assert.False(e2.Dashed);                  // dotted = inferred
        Assert.True(Math.Abs(e2.Amplitude) > Math.Abs(e1.Amplitude));    // wiggle = uncertainty
        Assert.True(Math.Sign(e1.Amplitude) != Math.Sign(e2.Amplitude)); // parallels bow apart
        Assert.True(e1.PulseAmp > 0);                                     // linked ends → alive
    }

    [Fact]
    public void Parallel_edges_between_one_pair_keep_distinct_identities()
    {
        // Two typed relations between the same nodes are legitimate ("amplifies" AND "keeps-alive").
        // The renderer keys edge elements by VisualEdge.Id — duplicate keys crash the whole render
        // (observed live: "More than one sibling of element 'polyline' has the same key value").
        var nodes = new[] { Node(1, "a"), Node(2, "b") };
        var edges = new[]
        {
            new GraphEdge(11, 1, 2, "amplifies", "amplifies", true, 0.7),
            new GraphEdge(12, 1, 2, "keeps-alive", "keeps-alive", true, 0.6),
        };
        var vm = Build(nodes, edges, Array.Empty<EntryMeta>());
        Assert.Equal(2, vm.Edges.Count(e => e.Id >= 0));   // synthetic weak ties carry negative ids
        Assert.Equal(vm.Edges.Count, vm.Edges.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public void Inferred_node_is_dashed_and_fainter()
    {
        var vm = Build(new[] { Node(1, "x", inf: true, conf: 0.2) }, Array.Empty<GraphEdge>(), Array.Empty<EntryMeta>());
        Assert.True(vm.Nodes[0].Dashed);
        Assert.True(vm.Nodes[0].Opacity < 0.6);
    }

    [Fact]
    public void Entities_land_in_their_affect_quadrant()
    {
        // The circumplex contract: x = valence (right = positive), y = 1 − energy (up = high).
        var nodes = new[] { Node(1, "flow"), Node(2, "grief"), Node(3, "rage"), Node(4, "rest") };
        var entries = new[]
        {
            new EntryMeta(20, Recent, "energized", 0.7, 0.6, 0.9, "[\"flow\"]", "[]", "s"),   // +val, hi-E
            new EntryMeta(21, Recent, "hopeless", -0.7, 0.6, 0.15, "[\"grief\"]", "[]", "s"), // −val, lo-E
            new EntryMeta(22, Recent, "furious", -0.6, 0.8, 0.85, "[\"rage\"]", "[]", "s"),   // −val, hi-E
            new EntryMeta(23, Recent, "content", 0.6, 0.3, 0.2, "[\"rest\"]", "[]", "s"),     // +val, lo-E
        };
        var vm = Build(nodes, Array.Empty<GraphEdge>(), entries);
        VisualNode N(string l) => vm.Nodes.Single(n => n.Label == l);

        Assert.True(N("flow").X > 0.5 && N("flow").Y < 0.5, "flow → top-right");
        Assert.True(N("grief").X < 0.5 && N("grief").Y > 0.5, "grief → bottom-left");
        Assert.True(N("rage").X < 0.5 && N("rage").Y < 0.5, "rage → top-left");
        Assert.True(N("rest").X > 0.5 && N("rest").Y > 0.5, "rest → bottom-right");
    }

    [Fact]
    public void Position_tracks_the_present_not_the_all_time_average()
    {
        // A month-old hopeful read + a fresh rejection: the node should sit clearly negative NOW
        // (recency-weighted), not at the washed-out midpoint an all-time mean would give.
        var nodes = new[] { Node(1, "alice") };
        string old = new DateTime(2026, 5, 20, 0, 0, 0, DateTimeKind.Utc).ToString("o");
        var entries = new[]
        {
            new EntryMeta(20, old, "hopeful", 0.6, 0.5, 0.6, "[\"alice\"]", "[]", "s"),
            new EntryMeta(21, Recent, "rejected", -0.7, 0.7, 0.3, "[\"alice\"]", "[]", "s"),
        };
        var vm = Build(nodes, Array.Empty<GraphEdge>(), entries);
        var x = vm.Nodes.Single(n => n.Id > 0).X;    // exclude the synthesized core star
        Assert.True(x < 0.38, $"expected the fresh rejection to dominate; x={x:0.00}");
    }

    [Fact]
    public void Ambivalence_rings_and_hulls_encode()
    {
        // Triangle community with a contradictory hub: lifts + drains on the same node.
        var nodes = new[] { Node(1, "hub"), Node(2, "up"), Node(3, "down") };
        var edges = new[]
        {
            new GraphEdge(1, 2, 1, "lifts", "lifts", false, 0.9),      // + into hub
            new GraphEdge(2, 3, 1, "drains", "drains", false, 0.9),    // − into hub → contradiction
            new GraphEdge(3, 2, 3, "causes", "causes", false, 0.9),    // closes the triangle
        };
        // All three linked (quiet nodes are rightly hull-excluded — their belt position is
        // bookkeeping, not affect-space); energies VARY so the triangle has area (collinear
        // points have no hull); the hub revisited 4×.
        var entries = Enumerable.Range(0, 4).Select(i => Entry(20 + i, "anxious", -0.2, "[\"hub\"]"))
            .Append(new EntryMeta(30, Recent, "anxious", 0.3, 0.5, 0.9, "[\"up\"]", "[]", "s"))
            .Append(new EntryMeta(31, Recent, "anxious", -0.5, 0.5, 0.1, "[\"down\"]", "[]", "s"))
            .ToArray();
        var vm = Build(nodes, edges, entries);

        var hub = vm.Nodes.Single(n => n.Label == "hub");
        Assert.True(hub.Ambivalent);                              // both-sides truth → split fill
        Assert.Equal(2, hub.Rings);                               // 4 revisits → 2 temporal rings
        Assert.False(vm.Nodes.Single(n => n.Label == "up").Ambivalent);

        var hull = Assert.Single(vm.Hulls);                       // one 3-member community
        Assert.True(hull.Points.Count >= 3);
        Assert.All(hull.Points, p => { Assert.InRange(p.X, -0.1, 1.1); Assert.InRange(p.Y, -0.1, 1.1); });
    }

    [Fact]
    public void ConvexHull_wraps_points_and_survives_degenerates()
    {
        var square = Geometry.ConvexHull(new[]
            { new Pt(0, 0), new Pt(1, 0), new Pt(1, 1), new Pt(0, 1), new Pt(0.5, 0.5) });
        Assert.Equal(4, square.Length);                           // interior point excluded
        Assert.DoesNotContain(new Pt(0.5, 0.5), square);

        Assert.Equal(2, Geometry.ConvexHull(new[] { new Pt(0, 0), new Pt(1, 1) }).Length);
        Assert.Single(Geometry.ConvexHull(new[] { new Pt(0.3, 0.3), new Pt(0.3, 0.3) }));

        var expanded = Geometry.Expand(square, 0.1);
        Assert.True(expanded.All(p => Math.Abs(p.X - 0.5) > 0.49));   // pushed outward from centroid
    }

    [Fact]
    public void Linked_fraction_reflects_join_coverage()
    {
        var nodes = new[] { Node(1, "seen"), Node(2, "unseen") };
        var entries = new[] { Entry(10, "fine", 0, "[\"seen\"]") };
        Assert.Equal(0.5, Build(nodes, Array.Empty<GraphEdge>(), entries).LinkedFraction, 6);
    }
}
