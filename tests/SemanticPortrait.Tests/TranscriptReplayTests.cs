using System.Text.Json;
using SemanticPortrait.Core;

namespace SemanticPortrait.Tests;

/// <summary>
/// Integration test built from the REAL 2026-03-20 intake transcript: replays the analyst's actual
/// tool-call sequences (params lifted from the sandbox log) and asserts the hygiene gates catch
/// exactly the defects observed — duplicate concept nodes, phantom endpoints, prose edge types,
/// re-logged events — while everything legitimate the analyst did still lands.
/// </summary>
public class TranscriptReplayTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sp_replay_{Guid.NewGuid():N}.db");
    private readonly Db _db;
    private readonly GraphTools _graph;
    private readonly EntryTools _entry;

    public TranscriptReplayTests()
    {
        _db = new Db(_path);
        _db.OpenPlaintext();
        var emb = new FakeEmbedder();
        _graph = new GraphTools(_db, emb);
        _entry = new EntryTools(_db, emb);
    }

    public void Dispose() { _db.DestroyFile(); }

    private static string J(object o) => JsonSerializer.Serialize(o);
    private Task<string> Node(string category, string label, bool force = false) =>
        _graph.ExecuteAsync("upsert_node", J(new { category, label, inferred = true, confidence = 0.8, force }));

    /// <summary>Replay the legitimate node set from the transcript — all of it must land.</summary>
    private async Task SeedTranscriptGraph()
    {
        foreach (var (cat, label) in new[]
        {
            ("connection", "Priya"), ("connection", "partner"),
            ("distortion", "rejection-radar"), ("distortion", "fantasy re-inflation"),
            ("wound", "down on luck in relationships"), ("wound", "pining after low-interest women"),
            ("joy", "flirting/sexual-social validation"), ("body", "tennis and HIIT routine"),
            ("fire", "competence under pressure"), ("self", "conversation avoidance"),
            ("heart", "romance and passion"),
        })
            Assert.StartsWith("node #", await Node(cat, label));
    }

    // ---------------------------------------------------------------- nodes

    [Fact]
    public async Task Observed_duplicate_hopium_fantasy_reinflation_is_rejected_with_the_existing_label()
    {
        await SeedTranscriptGraph();

        // Transcript #157-era: the analyst created distortion/"hopium/fantasy re-inflation"
        // while distortion/"fantasy re-inflation" existed AND had appeared in its own label check.
        var res = await Node("distortion", "hopium/fantasy re-inflation");
        Assert.StartsWith("error: ", res);
        Assert.Contains("near-duplicate", res);
        Assert.Contains("fantasy re-inflation", res);      // told exactly which label to reuse
        Assert.Contains("register_alias", res);            // and given the merge path
        Assert.DoesNotContain(_db.GetNodes(), n => n.Label.Contains("hopium"));   // nothing written
    }

    [Fact]
    public async Task Force_lands_a_judged_distinct_concept()
    {
        await SeedTranscriptGraph();
        var res = await Node("distortion", "hopium/fantasy re-inflation", force: true);
        Assert.StartsWith("node #", res);
    }

    [Fact]
    public async Task Genuinely_distinct_same_category_nodes_are_not_blocked()
    {
        await SeedTranscriptGraph();
        // Real additions from the transcript that SHOULD coexist within a category.
        Assert.StartsWith("node #", await Node("wound", "marital separation sadness"));
        Assert.StartsWith("node #", await Node("connection", "Mia"));
        Assert.StartsWith("node #", await Node("connection", "Manny"));
        Assert.StartsWith("node #", await Node("heart", "family/partner aspiration"));
    }

    [Fact]
    public async Task Same_label_in_another_category_stays_a_modeling_choice()
    {
        await SeedTranscriptGraph();
        Assert.StartsWith("node #", await Node("joy", "romance and passion"));   // heart + joy both fine
    }

    [Fact]
    public async Task Aliased_mentions_pass_the_gate_and_merge()
    {
        await SeedTranscriptGraph();
        _db.RegisterEntityAlias("Priya", "P");
        var res = await Node("connection", "P");
        Assert.StartsWith("node #", res);
        Assert.Single(_db.GetNodes(), n => n.Label == "Priya");   // merged, not duplicated
    }

    // ---------------------------------------------------------------- edges

    [Fact]
    public async Task Observed_prose_edge_type_is_rejected()
    {
        await SeedTranscriptGraph();
        // Transcript verbatim: hopium -[amplifies-romantic-meaning-of-warmth]-> Priya
        var res = await _graph.ExecuteAsync("link_nodes", J(new
        {
            from_category = "distortion", from_label = "fantasy re-inflation",
            to_category = "connection", to_label = "Priya",
            type = "amplifies-romantic-meaning-of-warmth",
        }));
        Assert.StartsWith("error: ", res);
        Assert.Contains("sentence, not a relation", res);
        Assert.Empty(_db.GetEdges());
    }

    [Fact]
    public async Task Vocabulary_edges_land_and_types_normalize()
    {
        await SeedTranscriptGraph();
        var res = await _graph.ExecuteAsync("link_nodes", J(new
        {
            from_category = "distortion", from_label = "rejection-radar",
            to_category = "wound", to_label = "pining after low-interest women",
            type = "Keeps Alive",                       // sloppy casing/spacing from the model
        }));
        Assert.Contains("-[keeps-alive]->", res);        // normalized, not rejected
    }

    [Fact]
    public async Task Phantom_endpoint_via_link_is_gated_too()
    {
        await SeedTranscriptGraph();
        var res = await _graph.ExecuteAsync("link_nodes", J(new
        {
            from_category = "distortion", from_label = "fantasy re-inflation amplified",
            to_category = "connection", to_label = "Priya",
            type = "amplifies",
        }));
        Assert.StartsWith("error: ", res);
        Assert.Contains("near-duplicate", res);
    }

    // ---------------------------------------------------------------- events
    // Paraphrased retellings need REAL semantics — the hash-bag FakeEmbedder can't see that
    // "exchanged flirt-adjacent texts…" and "provided a text thread as evidence…" are one
    // happening. These run against the repo-root MiniLM (LocalEmbedderTests convention) and
    // skip gracefully where the model isn't downloaded.

    private EntryTools? RealEntryTools()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++, dir = Path.GetDirectoryName(dir)!)
        {
            var p = Path.Combine(dir, "models", "minilm");
            if (File.Exists(Path.Combine(p, "model.onnx")))
            {
                var local = new LocalEmbedder(p);
                return local.IsAvailable ? new EntryTools(_db, local) : null;
            }
        }
        return null;
    }

    [Fact]
    public async Task Observed_quadruple_event_logs_collapse_to_one()
    {
        var entry = RealEntryTools();
        if (entry is null) return;   // model not downloaded — real-semantics case can't run offline

        // Transcript ev#5/6/8/9: four records of ONE text-thread review, logged across turns.
        var texts = new[]
        {
            "Sam exchanged friendly/flirt-adjacent texts with Priya about plants, food, work, games, a bouldering-gym outing, coffee, and travel; he now reads her as genuinely warm and kind rather than a misread.",
            "Sam and Priya exchanged warm, playful texts about plants/yoga, food/culture, games, a bouldering outing with Mia, and travel plans; Sam is using them as evidence that her warmth is real.",
            "Sam provided a text thread with Priya as evidence that her warmth is genuine; Priya discussed plants/yoga, bouldering with Mia, work overwhelm, coffee, and travel to Denver then Japan.",
            "Sam reviewed a set of friendly texts with Priya and argued her warmth/kindness is genuine, including snacks, banter, media, food/culture talk, and travel/family/work details.",
        };
        var first = await entry.ExecuteAsync("log_event", J(new { summary = texts[0], when = "2026-03-25" }));
        Assert.StartsWith("logged event #", first);

        // Measured MiniLM sims vs the original: 0.831 (auto-block band) / 0.657, 0.787 in the
        // gray zone. The invariant: NO retelling silently logs — each is either rejected outright
        // or bounced back for judgment, and the timeline keeps exactly one record.
        foreach (var retell in texts.Skip(1))
        {
            var res = await entry.ExecuteAsync("log_event", J(new { summary = retell, when = "2026-03-20" }));
            Assert.True(res.Contains("already logged as event #1") || res.Contains("possible duplicate of event #1"),
                $"retell silently logged: {res}");
        }
        Assert.Single(_db.GetEvents());
    }

    [Fact]
    public async Task GrayZone_flag_resolves_via_force_for_a_genuinely_new_event()
    {
        var entry = RealEntryTools();
        if (entry is null) return;

        await entry.ExecuteAsync("log_event", J(new
        { summary = "Sam exchanged friendly/flirt-adjacent texts with Priya about plants, food, games, and travel; he reads her warmth as genuine.", when = "2026-03-25" }));

        // The dinners-confession is a DIFFERENT happening in the same storyline — measured 0.689
        // vs the texts-review, i.e. inside the gray zone. It must be flagged (not silently logged,
        // not silently dropped), and force=true — the analyst's judgment — must land it.
        var confession = "Sam told Priya he was interested after two dinners; she said she isn't looking to date.";
        var flagged = await entry.ExecuteAsync("log_event", J(new { summary = confession, when = "2026-03-20" }));
        Assert.Contains("possible duplicate", flagged);
        Assert.Single(_db.GetEvents());

        var forced = await entry.ExecuteAsync("log_event", J(new { summary = confession, when = "2026-03-20", force = true }));
        Assert.StartsWith("logged event #", forced);
        Assert.Equal(2, _db.GetEvents().Count);
    }

    [Fact]
    public async Task Distinct_happenings_and_distant_recurrences_still_log()
    {
        var entry = RealEntryTools();
        if (entry is null) return;

        await entry.ExecuteAsync("log_event", J(new { summary = "Sam told Priya he was interested after two dinners; she said she isn't looking to date.", when = "2026-03-20" }));
        // Measured 0.511 vs the confession — below the gray zone: logs with zero friction.
        var other = await entry.ExecuteAsync("log_event", J(new { summary = "Sam reports he has been separated from his partner for a few months.", when = "2026-03-20" }));
        Assert.StartsWith("logged event #", other);

        // Similar text far apart in time (annual recurrence) is a NEW event, not a duplicate.
        await entry.ExecuteAsync("log_event", J(new { summary = "played in the summer tennis league finals", when = "2025-07-01" }));
        var nextYear = await entry.ExecuteAsync("log_event", J(new { summary = "played in the summer tennis league finals", when = "2026-07-01" }));
        Assert.StartsWith("logged event #", nextYear);
        Assert.Equal(4, _db.GetEvents().Count);
    }

    [Fact]
    public async Task Exact_retell_is_caught_even_without_the_real_model()
    {
        // FakeEmbedder tier: identical wording near in time must always dedup, model or not.
        var first = await _entry.ExecuteAsync("log_event", J(new { summary = "dinner with Priya at the noodle bar", when = "2026-03-14" }));
        Assert.StartsWith("logged event #", first);
        var again = await _entry.ExecuteAsync("log_event", J(new { summary = "dinner with Priya at the noodle bar", when = "2026-03-15" }));
        Assert.Contains("already logged", again);
        Assert.Single(_db.GetEvents());
    }

    // ---------------------------------------------------------------- profile currency (data layer)

    [Fact]
    public void Profile_fields_update_in_place_so_currency_is_possible()
    {
        var store = new ProfileStore(_db, Path.GetTempFileName());
        store.Set("key_people_current", "Priya (from his tennis group; Sam likes her, thinks she is not attracted to him)");
        store.Set("key_people_current", "Priya (from his tennis group; Sam expressed interest 2026-03-20, she declined — isnt looking to date)");
        Assert.Contains("declined", store.Get("key_people_current"));
        Assert.Single(store.All().Keys, k => k == "key_people_current");
    }
}
