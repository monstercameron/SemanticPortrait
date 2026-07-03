using SemanticPortrait.Core;

namespace SemanticPortrait.Tests;

/// <summary>The exact-join layer behind the recall engine (Db.Recall.cs).</summary>
public class DbRecallTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sp_recall_{Guid.NewGuid():N}.db");
    private readonly Db _db;
    public DbRecallTests() { _db = new Db(_path); _db.OpenPlaintext(); }
    public void Dispose() { _db.DestroyFile(); }

    private long Msg(string role, string text, string utc) => _db.AddMessage(role, text, utc);

    [Fact]
    public void Exchange_returns_window_and_skips_tool_rows()
    {
        var a = Msg("user", "first", "2026-01-01T10:00:00.0000000Z");
        Msg("assistant", "reply1", "2026-01-01T10:01:00.0000000Z");
        Msg("tool", "🔎 looked", "2026-01-01T10:01:30.0000000Z");     // must not appear
        var c = Msg("user", "second", "2026-01-01T10:02:00.0000000Z");
        Msg("assistant", "reply2", "2026-01-01T10:03:00.0000000Z");

        var ex = _db.GetExchange(c, radius: 1);
        Assert.Equal(3, ex.Count);                                     // reply1, second, reply2
        Assert.Equal(new[] { "reply1", "second", "reply2" }, ex.Select(m => m.Text).ToArray());
        Assert.DoesNotContain(ex, m => m.Role == "tool");

        // thread edges clamp instead of erroring
        var atStart = _db.GetExchange(a, radius: 2);
        Assert.Equal(a, atStart.First().Id);
        Assert.Equal(3, atStart.Count);                                // nothing before 'first'
    }

    [Fact]
    public void Exchange_of_unknown_id_is_empty()
        => Assert.Empty(_db.GetExchange(999, radius: 2));

    [Fact]
    public void EntryMeta_and_events_range_by_iso_date()
    {
        var m1 = Msg("user", "e1", "2026-01-05T09:00:00.0000000Z");
        var m2 = Msg("user", "e2", "2026-02-05T09:00:00.0000000Z");
        _db.SetEntryMeta(m1, "numb", -0.4, 0.5, 0.2, "[\"work\"]", "[]", "flat morning");
        _db.SetEntryMeta(m2, "hopeful", 0.5, 0.4, 0.6, "[\"dating\"]", "[\"Priya\"]", "good sign");
        _db.AddEvent("2026-01-10T00:00:00.0000000Z", "dinner happened");
        _db.AddEvent("2026-03-01T00:00:00.0000000Z", "out of range");

        var meta = _db.GetEntryMetaRange("2026-01-01T00:00:00.0000000Z", "2026-01-31T23:59:59.0000000Z");
        Assert.Single(meta);
        Assert.Equal("numb", meta[0].Mood);

        var events = _db.GetEventsRange("2026-01-01T00:00:00.0000000Z", "2026-01-31T23:59:59.0000000Z");
        Assert.Single(events);
        Assert.Equal("dinner happened", events[0].Summary);
    }

    [Fact]
    public void Neighborhood_orients_edges_from_the_focus_node()
    {
        var radar = _db.UpsertNode("distortion", "rejection-radar", true, 0.9);
        var fire = _db.UpsertNode("fire", "creating", false, 0.95);
        var wound = _db.UpsertNode("wound", "abandonment", true, 0.6);
        _db.AddEdge(radar, fire, "steals-the-fuel", "steals-the-fuel", true, 0.8);
        _db.AddEdge(wound, radar, "manufactures", "manufactures", true, 0.7);

        var hood = _db.GetNeighborhood(radar);
        Assert.Equal(2, hood.Count);
        var toFire = hood.Single(n => n.Peer.Label == "creating");
        Assert.True(toFire.Outgoing);                                  // radar -> fire
        var fromWound = hood.Single(n => n.Peer.Label == "abandonment");
        Assert.False(fromWound.Outgoing);                              // wound -> radar
        Assert.Equal(0.8, hood[0].Confidence);                         // highest confidence first
    }

    [Fact]
    public void Neighborhood_respects_cap()
    {
        var hub = _db.UpsertNode("self", "hub", false, 1);
        for (var i = 0; i < 30; i++)
            _db.AddEdge(hub, _db.UpsertNode("mind", $"n{i}", false, 0.5), "links", "links", false, 0.5);
        Assert.Equal(24, _db.GetNeighborhood(hub).Count);
        Assert.Equal(5, _db.GetNeighborhood(hub, limit: 5).Count);
    }

    [Fact]
    public async Task AsOf_reads_return_only_what_existed_then()
    {
        var early = _db.UpsertNode("fire", "creating", false, 0.9);
        var earlyPeer = _db.UpsertNode("joy", "gaming", false, 0.8);
        _db.AddEdge(early, earlyPeer, "expresses-as", "expresses-as", false, 0.8);
        await Task.Delay(30);
        var cut = DateTime.UtcNow.ToString("o");
        await Task.Delay(30);
        var late = _db.UpsertNode("wound", "new wound", true, 0.6);
        _db.AddEdge(early, late, "manufactures", "manufactures", true, 0.6);

        var nodes = _db.GetNodesAsOf(cut);
        Assert.Equal(2, nodes.Count);
        Assert.DoesNotContain(nodes, n => n.Id == late);
        Assert.Single(_db.GetEdgesAsOf(cut));
        Assert.Equal(3, _db.GetNodes().Count);      // live view unaffected
        Assert.Equal(2, _db.GetEdges().Count);
    }

    [Fact]
    public void FindNodesByLabel_is_case_insensitive_across_categories()
    {
        _db.UpsertNode("connection", "Priya", false, 0.9);
        Assert.Single(_db.FindNodesByLabel("priya"));
        Assert.Empty(_db.FindNodesByLabel("nobody"));
    }

    [Fact]
    public void FindNotes_matches_substring_and_escapes_like_wildcards()
    {
        _db.AddNote("Priya initiated contact — evidence against the radar", DateTime.UtcNow.ToString("o"));
        _db.AddNote("gym: 100% attendance this week", DateTime.UtcNow.ToString("o"));

        Assert.Single(_db.FindNotes("priya"));
        Assert.Single(_db.FindNotes("100%"));      // literal %, not a wildcard
        Assert.Empty(_db.FindNotes("1_0%"));       // literal _, not a wildcard
    }

    [Fact]
    public async Task Recency_breaks_similarity_ties_but_never_beats_relevance()
    {
        var emb = new FakeEmbedder();
        // Identical text → identical cosine; only recency differs.
        var stale = Msg("user", "gym shame spiral again", "2024-06-01T00:00:00.0000000Z");
        var fresh = Msg("user", "gym shame spiral again", DateTime.UtcNow.ToString("o"));
        _db.AddEmbedding("message", stale, (await emb.EmbedAsync("gym shame spiral again"))!);
        _db.AddEmbedding("message", fresh, (await emb.EmbedAsync("gym shame spiral again"))!);
        // A weakly-related but brand-new message must NOT outrank a strong old match.
        var noise = Msg("user", "gym schedule tuesday", DateTime.UtcNow.ToString("o"));
        _db.AddEmbedding("message", noise, (await emb.EmbedAsync("gym schedule tuesday"))!);

        var hits = _db.Search((await emb.EmbedAsync("gym shame spiral again"))!, 3);
        Assert.Equal(fresh, hits[0].RefId);   // tie → newer wins
        Assert.Equal(stale, hits[1].RefId);   // strong-but-old still beats weak-but-new
        Assert.Equal(noise, hits[2].RefId);
    }

    [Fact]
    public void RecencyBoost_decays_and_tolerates_garbage_dates()
    {
        var now = DateTime.UtcNow;
        var freshBoost = Db.RecencyBoost(now.ToString("o"), now);
        var halfLife = Db.RecencyBoost(now.AddDays(-90).ToString("o"), now);
        var ancient = Db.RecencyBoost(now.AddYears(-3).ToString("o"), now);
        Assert.InRange(freshBoost, 0.079, 0.081);
        Assert.InRange(halfLife, 0.039, 0.041);
        Assert.True(ancient < 0.001);
        Assert.Equal(0, Db.RecencyBoost("not a date", now));
    }

    [Fact]
    public void FindEntryMetaMentioning_searches_people_and_topics_json()
    {
        var m = Msg("user", "entry", "2026-06-01T12:00:00.0000000Z");
        _db.SetEntryMeta(m, "anxious", -0.2, 0.6, 0.5, "[\"dating\"]", "[\"Priya\"]", "waiting on a text");

        Assert.Single(_db.FindEntryMetaMentioning("priya"));   // people
        Assert.Single(_db.FindEntryMetaMentioning("dating"));  // topics
        Assert.Empty(_db.FindEntryMetaMentioning("work"));
    }
}
