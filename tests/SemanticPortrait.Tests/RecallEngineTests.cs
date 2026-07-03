using System.Text.Json;
using SemanticPortrait.Core;

namespace SemanticPortrait.Tests;

/// <summary>
/// The recall engine: joined bundles (hits + mood + exchange + person + timeline), the portrait
/// view, budget honesty, and — the load-bearing one — the clean-room lane never exposing raw chat.
/// FakeEmbedder is word-overlap, so queries are phrased to share words with their targets.
/// </summary>
public class RecallEngineTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sp_eng_{Guid.NewGuid():N}.db");
    private readonly Db _db;
    private readonly FakeEmbedder _emb = new();
    private readonly RecallEngine _engine;
    private readonly RecallTools _tools;

    public RecallEngineTests()
    {
        _db = new Db(_path);
        _db.OpenPlaintext();
        _engine = new RecallEngine(_db, _emb, new EntityResolver(_db, _emb));
        _tools = new RecallTools(_engine);
    }

    public void Dispose() { _db.DestroyFile(); }

    private static string Args(object o) => JsonSerializer.Serialize(o);

    private async Task<long> Persist(string role, string text, string utc)
    {
        var id = _db.AddMessage(role, text, utc);
        var vec = await _emb.EmbedAsync(text);
        _db.AddEmbedding("message", id, vec!);
        return id;
    }

    // ------------------------------------------------------------ recall

    [Fact]
    public async Task Recall_enriches_hits_with_mood_and_surrounding_exchange()
    {
        await Persist("user", "feeling stuck about work today", "2026-03-14T18:00:00.0000000Z");
        await Persist("assistant", "what changed since yesterday?", "2026-03-14T18:01:00.0000000Z");
        var hit = await Persist("user", "the dinner with Priya went better than feared", "2026-03-14T22:00:00.0000000Z");
        await Persist("assistant", "note the gap between forecast and outcome", "2026-03-14T22:01:00.0000000Z");
        _db.SetEntryMeta(hit, "hopeful", 0.5, 0.6, 0.7, "[\"dating\"]", "[\"Priya\"]", "buoyed after dinner");

        var res = await _engine.RecallAsync("dinner Priya feared");

        Assert.Contains("mood: hopeful", res);            // entry_meta joined onto the hit
        Assert.Contains("v +0.5", res);
        Assert.Contains("2026-03-14", res);               // dated
        Assert.Contains("forecast and outcome", res);     // the surrounding exchange came along
    }

    [Fact]
    public async Task Recall_person_section_resolves_aliases_and_pulls_graph_plus_notes()
    {
        _db.RegisterEntityAlias("Priya", "P");
        var priya = _db.UpsertNode("connection", "Priya", false, 0.9);
        var radar = _db.UpsertNode("distortion", "rejection-radar", true, 0.8);
        _db.AddEdge(radar, priya, "targets", "targets", true, 0.7);
        var noteId = _db.AddNote("Priya sent the first message — evidence against the radar", DateTime.UtcNow.ToString("o"));
        _db.AddEmbedding("note", noteId, (await _emb.EmbedAsync("unrelated words entirely"))!);

        var res = await _engine.RecallAsync("anything at all", person: "P");

        Assert.Contains("\"P\" → Priya (alias)", res);              // fuzzy join carries provenance
        Assert.Contains("distortion/rejection-radar —targets→ Priya", res);
        Assert.Contains("sent the first message", res);      // note found by variant substring
    }

    [Fact]
    public async Task Recall_date_window_returns_events_and_weekly_mood_trend()
    {
        var m1 = await Persist("user", "flat", "2026-06-01T12:00:00.0000000Z");
        var m2 = await Persist("user", "up", "2026-06-10T12:00:00.0000000Z");
        _db.SetEntryMeta(m1, "numb", -0.4, 0.3, 0.2, "[\"mood\"]", "[]", "flat day");
        _db.SetEntryMeta(m2, "hopeful", 0.6, 0.5, 0.7, "[\"mood\"]", "[]", "turning up");
        _db.AddEvent("2026-06-05T00:00:00.0000000Z", "started the new routine");
        _db.AddEvent("2026-07-20T00:00:00.0000000Z", "outside the window");

        var res = await _engine.RecallAsync("mood", fromIso: "2026-06-01", toIso: "2026-06-15");

        Assert.Contains("started the new routine", res);
        Assert.DoesNotContain("outside the window", res);
        Assert.Contains("Mood trend", res);
        Assert.Contains("wk of", res);                    // weekly buckets rendered
        Assert.Contains("numb", res);
    }

    [Fact]
    public async Task Recall_without_dates_surfaces_semantically_related_events()
    {
        var eid = _db.AddEvent("2026-03-14T00:00:00.0000000Z", "dinner with Priya at the noodle bar");
        _db.UpsertEmbedding("event", eid, (await _emb.EmbedAsync("dinner with Priya at the noodle bar"))!);
        await Persist("user", "irrelevant filler entry", "2026-03-15T00:00:00.0000000Z");

        var res = await _engine.RecallAsync("the Priya dinner");
        Assert.Contains("Possibly related events", res);
        Assert.Contains("noodle bar", res);
    }

    // ------------------------------------------------------------ portrait

    [Fact]
    public async Task Portrait_joins_graph_notes_timeline_and_states()
    {
        var priya = _db.UpsertNode("connection", "Priya", false, 0.9);
        var fire = _db.UpsertNode("fire", "creating", false, 0.95);
        _db.AddEdge(priya, fire, "lifts", "lifts", true, 0.6);
        _db.AddNote("Priya pattern: initiates when interested", DateTime.UtcNow.ToString("o"));
        _db.AddEvent("2026-03-14T00:00:00.0000000Z", "dinner with Priya");
        var m = _db.AddMessage("user", "raw entry text", "2026-03-15T00:00:00.0000000Z");
        _db.SetEntryMeta(m, "anxious", -0.2, 0.6, 0.5, "[\"dating\"]", "[\"Priya\"]", "waiting on a reply");

        var res = await _engine.PortraitAsync("priya");

        Assert.Contains("Priya —lifts→ fire/creating (inferred 0.6)", res);
        Assert.Contains("initiates when interested", res);
        Assert.Contains("dinner with Priya", res);
        Assert.Contains("anxious", res);
        Assert.DoesNotContain("raw entry text", res);     // states join meta summaries, never raw chat
    }

    [Fact]
    public async Task Portrait_unresolved_focus_offers_closest_labels_not_a_forced_match()
    {
        var id = _db.UpsertNode("distortion", "rejection radar", true, 0.8);
        _db.UpsertEmbedding("node", id, (await _emb.EmbedAsync("distortion rejection radar"))!);

        var res = await _engine.PortraitAsync("radar");   // partial word overlap, below NOCASE
        // either resolves semantically or names the closest label — never silently empty
        Assert.Contains("rejection radar", res);
    }

    [Fact]
    public async Task Portrait_overview_maps_categories_and_hubs()
    {
        var radar = _db.UpsertNode("distortion", "rejection-radar", true, 0.8);
        _db.AddEdge(radar, _db.UpsertNode("fire", "creating", false, 0.9), "steals-the-fuel", "", true, 0.8);
        _db.AddEdge(radar, _db.UpsertNode("connection", "Priya", false, 0.9), "targets", "", true, 0.7);

        var res = await _engine.PortraitAsync("overview");
        Assert.Contains("3 nodes, 2 threads", res);
        Assert.Contains("Most connected", res);
        Assert.Contains("rejection-radar", res);
    }

    [Fact]
    public void ListNodeLabels_groups_by_category_and_filters()
    {
        _db.UpsertNode("connection", "Priya", false, 0.9);
        _db.UpsertNode("connection", "Manny", false, 0.9);
        _db.UpsertNode("fire", "creating", false, 0.9);

        var all = _engine.ListNodeLabels();
        Assert.Contains("connection: Manny, Priya", all);
        Assert.Contains("fire: creating", all);

        var one = _engine.ListNodeLabels("fire");
        Assert.DoesNotContain("Priya", one);
    }

    // ------------------------------------------------------------ budget honesty

    [Fact]
    public void Bundles_over_budget_say_what_they_dropped()
    {
        for (var i = 0; i < 400; i++)
            _db.UpsertNode("mind", $"some-fairly-long-node-label-number-{i:D3}", false, 0.5);

        var res = _engine.ListNodeLabels();
        Assert.True(res.Length <= RecallEngine.MaxBundleChars / 2 + 100, $"len {res.Length}");
        Assert.Contains("omitted", res);                  // truncation is stated, not silent
    }

    // ------------------------------------------------------------ clean-room lane

    [Fact]
    public async Task Analyst_lane_never_exposes_raw_chat()
    {
        // The analyst's spec list must not offer recall (the only raw-chat reader) at all…
        var specNames = _tools.AnalystSpecs
            .Select(s => s.GetType().GetProperty("name")!.GetValue(s) as string).ToList();
        Assert.DoesNotContain("recall", specNames);
        Assert.Equal(new[] { "portrait", "list_node_labels" }, specNames.ToArray());

        // …and the tools it does get never return message text, even when everything mentions it.
        const string secret = "RAW-CHAT-SECRET-73";
        var id = _db.AddMessage("user", $"note about Priya {secret}", "2026-06-01T00:00:00.0000000Z");
        _db.AddEmbedding("message", id, (await _emb.EmbedAsync($"note about Priya {secret}"))!);
        _db.UpsertNode("connection", "Priya", false, 0.9);

        foreach (var call in new[]
        {
            ("portrait", Args(new { focus = "Priya" })),
            ("portrait", Args(new { focus = "overview" })),
            ("list_node_labels", "{}"),
        })
            Assert.DoesNotContain(secret, await _tools.ExecuteAsync(call.Item1, call.Item2));
    }
}
