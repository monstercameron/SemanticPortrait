using System.Text.Json;
using SemanticPortrait.Core;

namespace SemanticPortrait.Tests;

/// <summary>
/// The name-resolution ladder (alias → NOCASE → embedding) and the node/event embedding coverage
/// underneath it. Uses the deterministic word-overlap FakeEmbedder, so semantic-tier tests are
/// phrased via shared words, not real semantics.
/// </summary>
public class EntityResolverTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sp_res_{Guid.NewGuid():N}.db");
    private readonly Db _db;
    private readonly GraphTools _graph;
    private readonly EntityResolver _resolver;

    public EntityResolverTests()
    {
        _db = new Db(_path);
        _db.OpenPlaintext();
        _graph = new GraphTools(_db, new FakeEmbedder());
        _resolver = new EntityResolver(_db, new FakeEmbedder());
    }

    public void Dispose() { _db.DestroyFile(); }

    private static string Args(object o) => JsonSerializer.Serialize(o);

    private async Task UpsertViaTools(string category, string label) =>
        await _graph.ExecuteAsync("upsert_node", Args(new { category, label }));

    [Fact]
    public async Task Tier1_alias_registry_wins_and_is_labeled()
    {
        _db.RegisterEntityAlias("Priya", "P");
        await UpsertViaTools("connection", "Priya");

        var r = await _resolver.ResolveAsync("P");
        Assert.Equal(ResolutionVia.Alias, r.Via);
        Assert.Equal("Priya", r.Canonical);
        Assert.Single(r.Nodes);
        Assert.Contains("(alias)", r.Provenance);
    }

    [Fact]
    public async Task Tier2_nocase_label_match_is_exact()
    {
        await UpsertViaTools("connection", "Priya");
        var r = await _resolver.ResolveAsync("priya");
        Assert.Equal(ResolutionVia.Exact, r.Via);
        Assert.Equal("Priya", r.Canonical);    // stored casing, not query casing
    }

    [Fact]
    public async Task Tier3_semantic_match_fires_and_is_marked_inferred()
    {
        await UpsertViaTools("distortion", "rejection radar");   // embedded as "distortion rejection radar"

        var r = await _resolver.ResolveAsync("radar rejection"); // no alias, no NOCASE hit; 2 shared words
        Assert.Equal(ResolutionVia.Semantic, r.Via);
        Assert.Equal("rejection radar", r.Canonical);
        Assert.Contains("inferred", r.Provenance);
    }

    [Fact]
    public async Task Unrelated_query_stays_unresolved_not_force_matched()
    {
        await UpsertViaTools("distortion", "rejection radar");
        var r = await _resolver.ResolveAsync("quantum chromodynamics");
        Assert.Equal(ResolutionVia.Unresolved, r.Via);
        Assert.Empty(r.Nodes);
    }

    [Fact]
    public async Task Same_label_in_multiple_categories_returns_all_nodes()
    {
        await UpsertViaTools("joy", "running");
        await UpsertViaTools("body", "running");
        var r = await _resolver.ResolveAsync("running");
        Assert.Equal(2, r.Nodes.Count);
    }

    [Fact]
    public void Variants_expand_canonical_plus_aliases()
    {
        _db.RegisterEntityAlias("Priya", "P");
        _db.RegisterEntityAlias("Priya", "Pis");
        var v = _resolver.Variants("Priya");
        Assert.Equal(3, v.Count);
        Assert.Contains("P", v);
        Assert.Contains("Pis", v);
    }

    // --- embedding coverage under the ladder --------------------------------

    [Fact]
    public async Task Nodes_and_events_are_embedded_on_write()
    {
        await UpsertViaTools("connection", "Priya");
        var nodeId = _db.GetNodes().Single().Id;
        Assert.Equal(1, _db.EmbeddingCount("node", nodeId));

        var entry = new EntryTools(_db, new FakeEmbedder());
        await entry.ExecuteAsync("log_event", Args(new { summary = "dinner with Priya", when = "2026-03-14" }));
        Assert.Equal(1, _db.EmbeddingCount("event", _db.GetEvents().Single().Id));
    }

    [Fact]
    public async Task LinkNodes_embeds_both_autocreated_endpoints()
    {
        await _graph.ExecuteAsync("link_nodes", Args(new
        {
            from_category = "wound", from_label = "abandonment",
            to_category = "distortion", to_label = "rejection radar", type = "manufactures",
        }));
        foreach (var n in _db.GetNodes()) Assert.Equal(1, _db.EmbeddingCount("node", n.Id));
    }

    [Fact]
    public async Task Backfill_embeds_only_missing_rows_and_is_idempotent()
    {
        // Written straight through Db — simulating rows from before embed-on-write existed.
        _db.UpsertNode("fire", "creating", false, 0.9);
        _db.AddEvent(DateTime.UtcNow.ToString("o"), "shipped the widget builder");

        var backfill = new EmbeddingBackfill(_db, new FakeEmbedder());
        Assert.Equal(2, await backfill.RunAsync());
        Assert.Equal(0, await backfill.RunAsync());   // second run: nothing missing

        Assert.Empty(_db.GetNodesWithoutEmbedding());
        Assert.Empty(_db.GetEventsWithoutEmbedding());
    }

    [Fact]
    public async Task Deleting_or_merging_a_node_removes_its_embedding()
    {
        await UpsertViaTools("connection", "Ali");
        await UpsertViaTools("connection", "Alice");
        var ali = _db.GetNodes().Single(n => n.Label == "Ali").Id;

        // late alias merges Ali into Alice — the dead node's embedding must go with it
        _db.RegisterEntityAlias("Alice", "Ali");
        Assert.Equal(0, _db.EmbeddingCount("node", ali));

        var alice = _db.GetNodes().Single().Id;
        _db.DeleteNode(alice);
        Assert.Equal(0, _db.EmbeddingCount("node", alice));
    }
}
