using System.Text.Json;
using SemanticPortrait.Core;

namespace SemanticPortrait.Tests;

public class EntityRegistryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sp_entity_{Guid.NewGuid():N}.db");
    private readonly Db _db;
    public EntityRegistryTests() { _db = new Db(_path); _db.OpenPlaintext(); }
    public void Dispose() { _db.DestroyFile(); }

    [Fact]
    public void Upsert_dedupes_case_insensitively()
    {
        var a = _db.UpsertNode("connection", "Alice", false, 0.9);
        var b = _db.UpsertNode("connection", "alice", true, 0.5);
        Assert.Equal(a, b);
        var node = Assert.Single(_db.GetNodes());
        Assert.Equal("Alice", node.Label);        // original casing kept
        Assert.Equal(0.5, node.Confidence);       // latest write wins
    }

    [Fact]
    public void Registered_alias_resolves_mentions_to_one_node()
    {
        _db.RegisterEntityAlias("Alice", "Ali");
        var a = _db.UpsertNode("connection", "Ali", false, 0.9);
        var b = _db.UpsertNode("connection", "Alice", false, 0.9);
        Assert.Equal(a, b);
        Assert.Equal("Alice", Assert.Single(_db.GetNodes()).Label);
    }

    [Fact]
    public void Late_alias_merges_existing_duplicate_nodes_and_repoints_edges()
    {
        // Two nodes exist BEFORE the alias is learned; edges hang off the duplicate.
        var dup = _db.UpsertNode("connection", "Ali", false, 0.9);
        var canon = _db.UpsertNode("connection", "Alice", false, 0.9);
        var other = _db.UpsertNode("work", "CashFlux", false, 1.0);
        _db.AddEdge(dup, other, "works-on", "works-on", false, 0.9);
        Assert.NotEqual(dup, canon);

        _db.RegisterEntityAlias("Alice", "Ali");

        var nodes = _db.GetNodes();
        Assert.DoesNotContain(nodes, n => n.Label == "Ali");            // duplicate gone
        var edge = Assert.Single(_db.GetEdges());
        Assert.Equal(canon, edge.Src);                                   // edge re-pointed
    }

    [Fact]
    public async Task Alias_tool_roundtrip_via_graph_tools()
    {
        var tools = new GraphTools(_db, new FakeEmbedder());
        var res = await tools.ExecuteAsync("register_alias",
            JsonSerializer.Serialize(new { canonical = "Alice", mention = "Ali", kind = "person" }));
        Assert.Contains("resolves to 'Alice'", res);
        Assert.Equal("Alice", _db.ResolveCanonical("ali"));
        var e = Assert.Single(_db.GetEntities());
        Assert.Equal("Alice", e.Canonical);
        Assert.Contains("Ali", e.Aliases);
    }

    [Fact]
    public void Metrics_snapshots_roundtrip_newest_first()
    {
        _db.SaveMetricsSnapshot("{\"nodes\":1}");
        _db.SaveMetricsSnapshot("{\"nodes\":2}");
        var snaps = _db.GetMetricsSnapshots();
        Assert.Equal(2, snaps.Count);
        Assert.Contains("2", snaps[0].Payload);
    }
}
