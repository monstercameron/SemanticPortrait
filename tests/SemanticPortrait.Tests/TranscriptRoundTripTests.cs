using System.Text.Json;
using SemanticPortrait.Core;

namespace SemanticPortrait.Tests;

/// <summary>
/// Round-trip integration test on the REAL 2026-03-20 transcript: replay the session's extraction
/// (entries, moods, notes, events, graph — all through the gated tools, real MiniLM embeddings),
/// then ask the questions a FUTURE session would ask and assert the recall bundles carry the
/// load-bearing facts. This is the test the live session couldn't run — everything happened
/// inside the 2-day verbatim window, so recall was never exercised.
/// Skips gracefully when the repo MiniLM model isn't downloaded.
/// </summary>
public class TranscriptRoundTripTests : IAsyncLifetime, IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sp_rt_{Guid.NewGuid():N}.db");
    private Db _db = null!;
    private LocalEmbedder? _emb;
    private RecallEngine _engine = null!;
    private EntryTools _entry = null!;
    private GraphTools _graph = null!;
    private bool Ready => _emb is not null;

    private static string J(object o) => JsonSerializer.Serialize(o);

    public void Dispose() { _db?.DestroyFile(); _emb?.Dispose(); }
    public Task DisposeAsync() { Dispose(); return Task.CompletedTask; }

    public async Task InitializeAsync()
    {
        _db = new Db(_path);
        _db.OpenPlaintext();

        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++, dir = Path.GetDirectoryName(dir)!)
        {
            var p = Path.Combine(dir, "models", "minilm");
            if (File.Exists(Path.Combine(p, "model.onnx")))
            {
                var local = new LocalEmbedder(p);
                if (local.IsAvailable) _emb = local;
                break;
            }
        }
        if (_emb is null) return;

        _engine = new RecallEngine(_db, _emb, new EntityResolver(_db, _emb));
        _entry = new EntryTools(_db, _emb);
        _graph = new GraphTools(_db, _emb);
        await SeedFromTranscript();
    }

    // ------------------------------------------------------------ seeding (the transcript, replayed)

    private async Task<long> Msg(string role, string text, string utc)
    {
        var id = _db.AddMessage(role, text, utc);
        var vec = await _emb!.EmbedAsync(text);
        if (vec is not null) _db.AddEmbedding("message", id, vec);
        return id;
    }

    /// <summary>log_event the way the hardened analyst does: judge a gray-zone flag, force when
    /// the happening is genuinely distinct.</summary>
    private async Task LogEventJudged(string summary, string when)
    {
        var res = await _entry.ExecuteAsync("log_event", J(new { summary, when }));
        if (res.StartsWith("possible duplicate"))
            res = await _entry.ExecuteAsync("log_event", J(new { summary, when, force = true }));
        Assert.StartsWith("logged event #", res);
    }

    private async Task SeedFromTranscript()
    {
        // --- the load-bearing exchanges, verbatim from the log (raw entries + anchor replies)
        var m27 = await Msg("user", "i liek priya from my tennis group but I dont think shes attracted to me", "2026-03-20T16:50:00.0000000Z");
        await Msg("assistant", "Priya is a live thread, but keep this clean: \"I don't think she's attracted to me\" is an inference unless she's directly shown it.", "2026-03-20T16:51:00.0000000Z");
        var m49 = await Msg("user", "sadge, seperated from my partner for a few months", "2026-03-20T17:05:00.0000000Z");
        await Msg("assistant", "A few months separated from your partner is recent enough that sadge isn't mysterious — it's grief plus adjustment.", "2026-03-20T17:06:00.0000000Z");
        var m94 = await Msg("user", "pining after a low interest woman", "2026-03-20T17:20:00.0000000Z");
        var m157 = await Msg("user", "we went on 2 dinners and the 2nd dinner I just shot my shot and told her I was interested in her", "2026-03-20T17:30:00.0000000Z");
        await Msg("assistant", "This is not just friendly warmth anymore — two dinners plus you directly saying you're interested.", "2026-03-20T17:31:00.0000000Z");
        var m168 = await Msg("user", "well she said, she isnt looking to date and wasnt interested in me in not so many words :(", "2026-03-20T17:34:00.0000000Z");
        await Msg("assistant", "Her warmth was real, but her answer was also real: not available / not interested enough.", "2026-03-20T17:35:00.0000000Z");

        // --- entry_meta as the analyst recorded it
        _db.SetEntryMeta(m27, "insecure", -0.45, 0.45, 0.4, "[\"work crush\",\"attraction uncertainty\"]", "[\"Priya\"]", "Likes Priya from his tennis group while believing she is probably not attracted to him.");
        _db.SetEntryMeta(m49, "sad", -0.65, 0.55, 0.35, "[\"marital separation\",\"mood\"]", "[\"partner\"]", "Feeling sadge; separated from his partner for a few months.");
        _db.SetEntryMeta(m94, "yearning", -0.55, 0.6, 0.35, "[\"pining after low-interest woman\"]", "[\"Priya\"]", "Names a recurring loop of pining after a low-interest woman.");
        _db.SetEntryMeta(m157, "assertive/hopeful", 0.35, 0.45, 0.55, "[\"dating\",\"direct disclosure\"]", "[\"Priya\"]", "Told her he was interested on the second dinner.");
        _db.SetEntryMeta(m168, "sad/rejected", -0.75, 0.78, 0.35, "[\"romantic rejection\",\"Priya\"]", "[\"Priya\"]", "Priya said she isnt looking to date and is not interested; he is sad and rejected.");

        // --- notes (the analyst's distilled reads, with embeddings)
        async Task Note(string text)
        {
            var id = _db.AddNote(text, DateTime.Parse("2026-03-20T17:30:00Z").ToUniversalTime().ToString("o"));
            var vec = await _emb!.EmbedAsync(text);
            if (vec is not null) _db.UpsertEmbedding("note", id, vec);
        }
        await Note("Sam identifies a recurring romantic loop in his own words: pining after a low interest woman. Current example is Priya from his tennis group; conversation avoidance keeps ambiguity alive instead of forcing clarity (inferred, moderate confidence).");
        await Note("Sam reports Priya is genuinely warm, very kind, always offers snacks; text evidence supports real friendliness. Romantic reciprocity remained unproven until he asked directly.");
        await Note("Sam made a concrete move toward clarity with Priya: after two dinners he shot his shot and told her he was interested. She replied she isnt looking to date and was not interested. His directness ended the ambiguity — useful calibration against hopium.");

        // --- events through the gated tool (dedup judged like the analyst would)
        await LogEventJudged("User reported renting their place with a roommate.", "2026-03-20");
        await LogEventJudged("Sam reported liking Priya from his tennis group and thinking she is not attracted to him.", "2026-03-20");
        await LogEventJudged("Sam reports he has been separated from his partner for a few months.", "2026-03-20");
        await LogEventJudged("Sam and Priya exchanged warm, playful texts about plants, food, games, a bouldering outing with Mia, and travel plans.", "2026-03-25");
        await LogEventJudged("Sam told Priya he was interested after two dinners; she said she isn't looking to date and is not interested.", "2026-03-20");

        // --- the graph, respecting the node bar (durable entities/patterns only, canonical edges)
        async Task Link(string fc, string fl, string tc, string tl, string type)
        {
            var res = await _graph.ExecuteAsync("link_nodes", J(new
            { from_category = fc, from_label = fl, to_category = tc, to_label = tl, type, inferred = true, confidence = 0.7 }));
            if (res.StartsWith("error:")) res = await _graph.ExecuteAsync("link_nodes", J(new
            { from_category = fc, from_label = fl, to_category = tc, to_label = tl, type, inferred = true, confidence = 0.7, force = true }));
            Assert.StartsWith("linked ", res);
        }
        await _graph.ExecuteAsync("upsert_node", J(new { category = "connection", label = "Priya", inferred = false, confidence = 0.95 }));
        await _graph.ExecuteAsync("upsert_node", J(new { category = "connection", label = "partner", inferred = false, confidence = 0.95 }));
        await Link("distortion", "rejection-radar", "wound", "pining after low-interest women", "amplifies");
        await Link("self", "conversation avoidance", "wound", "pining after low-interest women", "keeps-alive");
        await Link("connection", "Priya", "wound", "pining after low-interest women", "case-example-of");
        await Link("distortion", "fantasy re-inflation", "wound", "pining after low-interest women", "amplifies");
        _db.RegisterEntityAlias("Priya", "P");

        // --- profile, kept CURRENT per the new rule (post-rejection state)
        var store = new ProfileStore(_db, Path.GetTempFileName());
        store.Set("name", "Sam");
        store.Set("age", "38");
        store.Set("relationship_status", "Separated from his partner for a few months (reported 2026-03-20).");
        store.Set("key_people_current", "Priya (from his tennis group; Sam expressed interest 2026-03-20 after two dinners — she declined, isnt looking to date)");
    }

    // ------------------------------------------------------------ the future session's questions

    [Fact]
    public async Task Portrait_of_Priya_carries_the_whole_arc()
    {
        if (!Ready) return;
        var res = await _engine.PortraitAsync("Priya");

        Assert.Contains("case-example-of", res);                    // her place in the pattern
        Assert.Contains("isnt looking to date", res);                 // the resolution, from notes
        Assert.Contains("shot his shot", res);                    // his move, verbatim phrasing kept
        Assert.Contains("told Priya he was interested", res);       // the timeline event
        Assert.Contains("sad/rejected", res);                       // his recorded state when it landed
        Assert.True(res.Length <= RecallEngine.MaxBundleChars + 200, $"bundle blew the budget: {res.Length}");
    }

    [Fact]
    public async Task Alias_still_resolves_the_portrait_with_provenance()
    {
        if (!Ready) return;
        var res = await _engine.PortraitAsync("P");
        Assert.Contains("\"P\" → Priya (alias)", res);
        Assert.Contains("isnt looking to date", res);
    }

    [Fact]
    public async Task Recall_what_happened_with_Priya_surfaces_the_rejection_with_mood_and_exchange()
    {
        if (!Ready) return;
        var res = await _engine.RecallAsync("what happened with Priya — the dinners and how it ended", person: "Priya");

        Assert.Contains("isnt looking to date", res);                 // the resolution (note or raw entry)
        Assert.Contains("sad/rejected", res);                       // his state when it landed
        Assert.Contains("v -0.8", res);                             // with calibrated valence
        Assert.Contains("Priya", res);
        Assert.Contains("case-example-of", res);                    // person section: graph context came along
    }

    [Fact]
    public async Task Recall_wife_separation_finds_the_entry_with_its_state()
    {
        if (!Ready) return;
        var res = await _engine.RecallAsync("separation from partner, how he felt");
        Assert.Contains("seperated from my partner", res);             // his verbatim words (typo and all)
        Assert.Contains("mood: sad", res);
    }

    [Fact]
    public async Task Recall_date_window_shows_the_day_trend_and_events()
    {
        if (!Ready) return;
        var res = await _engine.RecallAsync("mood", fromIso: "2026-03-19", toIso: "2026-03-21");
        Assert.Contains("Mood trend", res);
        Assert.Contains("wk of", res);
        Assert.Contains("told Priya he was interested", res);       // events in range
    }

    [Fact]
    public async Task Node_narration_reads_like_prose_not_stats()
    {
        if (!Ready) return;
        var priya = _db.GetNodes().Single(n => n.Label == "Priya").Id;
        var story = await _engine.NarrateNodeAsync(priya, DateTime.Parse("2026-07-03T00:00:00Z").ToUniversalTime());

        Assert.Contains("You've written about this", story);      // presence, in words
        Assert.Contains("In your constellation", story);          // relations as sentences
        Assert.Contains("case example of", story);                // relation verbs humanized (no hyphens)
        Assert.Contains("From the analysis:", story);             // the analyst's own read quoted
        Assert.Contains("both light and weight", story);          // ambivalence flagged honestly
        Assert.DoesNotContain("salience", story);                 // no metric jargon
        Assert.DoesNotContain("0.", story);                       // no raw numbers
    }

    [Fact]
    public void The_store_itself_stayed_clean()
    {
        if (!Ready) return;
        // One Priya, one fantasy re-inflation, no phantom people, no duplicate events.
        Assert.Single(_db.GetNodes(), n => n.Label.Contains("Priya", StringComparison.OrdinalIgnoreCase));
        Assert.Single(_db.GetNodes(), n => n.Label.Contains("re-inflation"));
        Assert.DoesNotContain(_db.GetNodes(), n => n.Label.Contains("unnamed", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(5, _db.GetEvents().Count);
        Assert.All(_db.GetEdges(), e => Assert.True(e.Type.Length <= GraphTools.MaxEdgeTypeLength));
    }
}
