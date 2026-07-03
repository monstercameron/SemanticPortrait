using System.Text.Json;
using SemanticPortrait.Core;
using SemanticPortrait.Core.Constellation;

namespace SemanticPortrait.Tests;

public class ConstellationSourceTests
{
    [Fact]
    public void Sample_source_matches_mockup_shape_and_is_valid()
    {
        var vm = new SampleConstellationSource().Build();
        Assert.True(vm.Nodes.Count >= 18);                     // Core + the six clusters
        Assert.Contains(vm.Nodes, n => n.Label == "Core Self");
        Assert.Contains(vm.Nodes, n => n.Label == "Ambition");
        Assert.Contains(vm.Nodes, n => n.Label == "Creativity");

        var ids = vm.Nodes.Select(n => n.Id).ToHashSet();
        foreach (var e in vm.Edges)
        {
            Assert.Contains(e.Src, ids);                       // no dangling edges
            Assert.Contains(e.Dst, ids);
        }
        foreach (var n in vm.Nodes)
        {
            Assert.InRange(n.X, 0, 1); Assert.InRange(n.Y, 0, 1);
            Assert.InRange(n.FillH, 0, 360);
        }
    }

    [Fact]
    public void VisualModel_round_trips_through_json()
    {
        // The decoupling contract: a VisualModel is plain serializable data the design consumes.
        var vm = new SampleConstellationSource().Build();
        var json = JsonSerializer.Serialize(vm);
        var back = JsonSerializer.Deserialize<VisualModel>(json)!;
        Assert.Equal(vm.Nodes.Count, back.Nodes.Count);
        Assert.Equal(vm.Edges.Count, back.Edges.Count);
        Assert.Equal(vm.Nodes[0].Label, back.Nodes[0].Label);
    }

    [Fact]
    public async Task Db_source_is_empty_when_locked()
    {
        var db = new Db(Path.Combine(Path.GetTempPath(), $"sp_src_{Guid.NewGuid():N}.db"));   // not opened
        var bundle = await new DbConstellationSource(db).BuildAsync();
        Assert.Empty(bundle.Visual.Nodes);
        Assert.Equal(0, bundle.Visual.Join.TotalNodes);
        Assert.NotEmpty(bundle.Sigil.Cells);   // even locked, the Sigil paints a quiet nebula
    }

    [Fact]
    public async Task Db_source_joins_semantically_and_classifies_slang_moods()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sp_srcj_{Guid.NewGuid():N}.db");
        var db = new Db(path);
        db.OpenPlaintext();
        try
        {
            var emb = new FakeEmbedder();
            var graph = new GraphTools(db, emb);
            await graph.ExecuteAsync("upsert_node", JsonSerializer.Serialize(new
            { category = "distortion", label = "rejection radar pattern", inferred = true, confidence = 0.8 }));
            var nodeId = db.GetNodes().Single().Id;

            // Topic "rejection radar worry" token-mismatches the label but is semantically close
            // (FakeEmbedder = word overlap); the injected ladder must join it.
            var m = db.AddMessage("user", "entry", DateTime.UtcNow.ToString("o"));
            db.SetEntryMeta(m, "anxious", -0.4, 0.6, 0.4, "[\"rejection radar worry\"]", "[]", "s");

            var bundle = await new DbConstellationSource(db, emb).BuildAsync();
            var node = bundle.Visual.Nodes.Single(n => n.Id == nodeId);
            Assert.False(node.Quiet);                       // the join landed → node is lit
            Assert.Equal(1.0, bundle.Visual.LinkedFraction, 3);
        }
        finally { db.DestroyFile(); }
    }
}
