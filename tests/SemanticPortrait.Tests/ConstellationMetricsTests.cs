using SemanticPortrait.Core;
using SemanticPortrait.Core.Constellation;

namespace SemanticPortrait.Tests;

public class ConstellationMetricsTests
{
    private static readonly DateTime Now = new(2026, 6, 21, 0, 0, 0, DateTimeKind.Utc);
    private static string Recent => new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc).ToString("o");

    private static GraphNode Node(long id, string label, string cat = "self", double conf = 1.0, bool inf = false)
        => new(id, cat, label, inf, conf);
    private static GraphEdge Edge(long id, long src, long dst, string type)
        => new(id, src, dst, type, type, false, 1.0);
    private static EntryMeta Entry(long id, string mood, double val, double inten, double energy,
        string topicsJson, string peopleJson = "[]", string? utc = null)
        => new(id, utc ?? Recent, mood, val, inten, energy, topicsJson, peopleJson, "summary");

    private static ConstellationModel Run(
        IReadOnlyList<GraphNode> n, IReadOnlyList<GraphEdge> e, IReadOnlyList<EntryMeta> m)
        => ConstellationMetrics.Compute(n, e, m, Now);

    [Fact]
    public void Empty_graph_does_not_throw()
    {
        var model = Run(Array.Empty<GraphNode>(), Array.Empty<GraphEdge>(), Array.Empty<EntryMeta>());
        Assert.Empty(model.Nodes);
        Assert.Empty(model.Edges);
        Assert.Equal(0, model.Join.TotalNodes);
    }

    [Fact]
    public void Untouched_node_is_quiet_triangle_neutral_zero_salience()
    {
        var model = Run(new[] { Node(1, "lonely thing") }, Array.Empty<GraphEdge>(), Array.Empty<EntryMeta>());
        var m = model.Nodes[0];
        Assert.True(m.Quiet);
        Assert.Equal(3, m.Sides);
        Assert.False(m.IsCircle);
        Assert.Equal(Emotion.Neutral, m.Emotion);
        Assert.Equal(0, m.Salience);
        Assert.Equal(0, m.ReferencingEntries);
    }

    [Fact]
    public void Join_matches_varied_forms_and_reports_misses()
    {
        var nodes = new[] { Node(1, "Alice"), Node(2, "weak self esteem"), Node(3, "Work") };
        var entries = new[]
        {
            // exact + multiword-subset + person + a miss ("the weather") in one entry
            Entry(10, "anxious", -0.5, 0.7, 0.6, "[\"work\", \"self-esteem\", \"the weather\"]", "[\"Alice\"]"),
            Entry(11, "sad", -0.6, 0.5, 0.4, "[]", "[\"alice\"]"),         // lowercase person
            Entry(12, "fine", 0.0, 0.2, 0.5, "[\"homework\"]"),            // must NOT match "Work"
        };
        var model = Run(nodes, Array.Empty<GraphEdge>(), entries);

        Assert.Equal(2, model.Nodes.Single(n => n.NodeId == 1).ReferencingEntries);   // Alice: e10 + e11
        Assert.Equal(1, model.Nodes.Single(n => n.NodeId == 2).ReferencingEntries);   // self-esteem ⊆ weak self esteem
        Assert.Equal(1, model.Nodes.Single(n => n.NodeId == 3).ReferencingEntries);   // "work" exact; "homework" excluded
        Assert.Contains("the weather", model.Join.UnmatchedRefs);
        Assert.Contains("homework", model.Join.UnmatchedRefs);
        Assert.Equal(3, model.Join.LinkedNodes);
    }

    [Fact]
    public void Contradiction_raises_sides_and_blocks_circle()
    {
        var nodes = new[] { Node(1, "A"), Node(2, "B"), Node(3, "C") };
        var edges = new[] { Edge(1, 1, 2, "lifts"), Edge(2, 1, 3, "drains") };   // +1 and −1 on node 1
        var model = Run(nodes, edges, Array.Empty<EntryMeta>());
        var a = model.Nodes.Single(n => n.NodeId == 1);
        Assert.Equal(1, a.Contradiction);
        Assert.False(a.IsCircle);
        Assert.True(a.Sides >= 4);
    }

    [Fact]
    public void Circle_requires_evidence()
    {
        // node 1: 3 distinct positive edges, 3 referencing entries, high confidence, net-positive.
        var nodes = new[] { Node(1, "growth", conf: 1.0), Node(2, "B"), Node(3, "C"), Node(4, "D") };
        var edges = new[]
        {
            Edge(1, 1, 2, "lifts"), Edge(2, 1, 3, "disproves"), Edge(3, 1, 4, "the-cure"),
        };
        var entries = new[]
        {
            Entry(10, "grateful", 0.7, 0.6, 0.6, "[\"growth\"]"),
            Entry(11, "inspired", 0.6, 0.7, 0.7, "[\"growth\"]"),
            Entry(12, "happy", 0.8, 0.5, 0.6, "[\"growth\"]"),
        };
        var m = Run(nodes, edges, entries).Nodes.Single(n => n.NodeId == 1);
        Assert.True(m.IsCircle);
        Assert.Equal(Geometry.CircleSides, m.Sides);

        // strip evidence (one edge, no entries) → never a circle
        var weak = Run(new[] { Node(1, "growth") }, new[] { Edge(1, 1, 1, "lifts") }, Array.Empty<EntryMeta>())
                   .Nodes.Single(n => n.NodeId == 1);
        Assert.False(weak.IsCircle);
    }

    [Fact]
    public void Volatility_null_below_five_entries_else_computed()
    {
        var nodes = new[] { Node(1, "v") };
        var few = Enumerable.Range(0, 3).Select(i => Entry(i, "sad", i % 2 == 0 ? -0.8 : 0.8, 0.5, 0.5, "[\"v\"]")).ToArray();
        Assert.Null(Run(nodes, Array.Empty<GraphEdge>(), few).Nodes[0].Volatility);

        var many = Enumerable.Range(0, 6).Select(i => Entry(i, "sad", i % 2 == 0 ? -0.8 : 0.8, 0.5, 0.5, "[\"v\"]")).ToArray();
        var vol = Run(nodes, Array.Empty<GraphEdge>(), many).Nodes[0].Volatility;
        Assert.NotNull(vol);
        Assert.True(vol > 0);
    }

    [Fact]
    public void Salience_is_normalized_to_one_at_the_peak()
    {
        var nodes = new[] { Node(1, "hot"), Node(2, "cool") };
        var entries = new[]
        {
            Entry(10, "anxious", -0.5, 0.9, 0.8, "[\"hot\"]"),
            Entry(11, "anxious", -0.5, 0.9, 0.8, "[\"hot\"]"),
            Entry(12, "fine", 0.0, 0.1, 0.3, "[\"cool\"]"),
        };
        var model = Run(nodes, Array.Empty<GraphEdge>(), entries);
        Assert.Equal(1.0, model.Nodes.Single(n => n.NodeId == 1).Salience, 6);
        Assert.True(model.Nodes.Single(n => n.NodeId == 2).Salience < 1.0);
    }

    [Fact]
    public void Dangling_edge_is_ignored()
    {
        var nodes = new[] { Node(1, "A") };
        var edges = new[] { Edge(1, 1, 999, "lifts") };   // 999 doesn't exist
        var m = Run(nodes, edges, Array.Empty<EntryMeta>()).Nodes[0];
        Assert.Equal(1, m.Degree);   // counts the endpoint that exists, no throw
    }

    [Fact]
    public void Unreadable_moods_are_unmapped_not_neutral()
    {
        var nodes = new[] { Node(1, "x") };
        var entries = new[] { Entry(10, "zxcvbn", 0.0, 0.4, 0.5, "[\"x\"]"), Entry(11, "qwerty", 0.0, 0.4, 0.5, "[\"x\"]") };
        Assert.Equal(Emotion.Unmapped, Run(nodes, Array.Empty<GraphEdge>(), entries).Nodes[0].Emotion);
    }

    [Fact]
    public void Recency_decays_salience_for_old_entries()
    {
        var nodes = new[] { Node(1, "old"), Node(2, "new") };
        var old = Entry(10, "anxious", -0.5, 0.9, 0.8, "[\"old\"]", "[]",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToString("o"));   // ~5 months old
        var fresh = Entry(11, "anxious", -0.5, 0.9, 0.8, "[\"new\"]");
        var model = Run(nodes, Array.Empty<GraphEdge>(), new[] { old, fresh });
        Assert.True(model.Nodes.Single(n => n.NodeId == 1).Salience
                  < model.Nodes.Single(n => n.NodeId == 2).Salience);
    }
}
