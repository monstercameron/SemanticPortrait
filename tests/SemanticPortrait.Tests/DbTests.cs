using SemanticPortrait.Core;

namespace SemanticPortrait.Tests;

public class DbTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sp_test_{Guid.NewGuid():N}.db");
    private readonly FakeEmbedder _emb = new();

    private Db NewDb() { var d = new Db(_path); d.OpenPlaintext(); return d; }
    private float[] Embed(string t) => _emb.EmbedAsync(t).Result!;

    public void Dispose() { try { if (File.Exists(_path)) File.Delete(_path); } catch { } }

    [Fact]
    public void Messages_roundtrip_in_order()
    {
        var db = NewDb();
        db.AddMessage("user", "first", Now());
        db.AddMessage("assistant", "second", Now());

        var msgs = db.GetMessages();
        Assert.Equal(2, msgs.Count);
        Assert.Equal("first", msgs[0].Text);
        Assert.Equal("user", msgs[0].Role);
        Assert.Equal("second", msgs[1].Text);
    }

    [Fact]
    public void Usage_accumulates_per_model_and_persists()
    {
        var db = NewDb();
        db.AddUsage("gpt-5.5", 1000, 200, 0.011);
        db.AddUsage("gpt-5.5", 500, 100, 0.0055);
        db.AddUsage("text-embedding-3-small", 2000, 0, 0.00004);

        var totals = db.GetUsageTotals();
        Assert.Equal(3500, totals.Input);
        Assert.Equal(300, totals.Output);
        Assert.Equal(3, totals.Calls);
        Assert.Equal(0.01654, totals.CostUsd, 5);

        var byModel = db.GetUsageByModel();
        Assert.Equal("gpt-5.5", byModel[0].Model);   // highest cost first
        Assert.Equal(2, byModel[0].Calls);

        // survives a fresh connection to the same file
        db = NewDb();
        Assert.Equal(3500, db.GetUsageTotals().Input);
    }

    [Fact]
    public void UsageTracker_records_session_and_global_via_db()
    {
        var db = NewDb();
        var tracker = new UsageTracker(db);
        tracker.Record("gpt-5.5", 1000, 200, OpenAIClient.Gpt55Pricing.CostUsd(1000, 200));

        Assert.Equal(1200, tracker.Total);
        Assert.Equal(0.011, tracker.CostUsd, 5);     // 1000*5/1e6 + 200*30/1e6
        Assert.Equal((1000, 200, 1, 0.011), Round(tracker.Global()));
    }

    private static (long, long, long, double) Round((long a, long b, long c, double d) g) => (g.a, g.b, g.c, Math.Round(g.d, 5));

    [Fact]
    public void UsageTracker_month_is_persisted_baseline_plus_session()
    {
        var db = NewDb();
        db.AddUsage("gpt-5.5", 1000, 200, 0.011);          // earlier this month, already persisted

        var tracker = new UsageTracker(db);
        tracker.LoadBaseline();
        Assert.Equal(0.011, tracker.MonthCostUsd, 5);      // shows this month's persisted total
        Assert.Equal(0.0, tracker.CostUsd, 5);             // session starts at zero

        tracker.Record("gpt-5.5", 500, 100, 0.0055);       // this session's usage
        Assert.Equal(0.0165, tracker.MonthCostUsd, 5);     // baseline + session, no double-count
        Assert.Equal(1800, tracker.MonthTokens);
        Assert.Equal(2, tracker.MonthCalls);
    }

    [Fact]
    public void Schema_migrations_are_idempotent_across_reopen()
    {
        // First open creates the schema; populate the migrated columns/tables.
        var a = new Db(_path);
        a.OpenPlaintext();
        var rid = a.AddReminder(DateTime.UtcNow.ToString("o"), "x");
        a.SetReminderPrivate(rid, true);
        var nid = a.AddNotification("reminder", rid, "Reminder", "x");
        a.MarkNotificationSurfaced(nid);
        a.AddUsage("gpt-5.5", 10, 2, 0.001);
        a.Close();

        // Reopen the SAME file: EnsureSchema + the try/catch ALTERs re-run. Must not throw,
        // and every migrated column/table must still be usable with data intact.
        var b = new Db(_path);
        b.OpenPlaintext();
        Assert.True(b.GetReminderPrivate(rid));
        Assert.True(b.GetNotification(nid)!.Surfaced);
        Assert.Equal(10, b.GetMonthlyTotals(Db.CurrentMonth()).Input);
        Assert.Equal(10, b.GetUsageTotals().Input);
        b.Close();
    }

    [Fact]
    public void Monthly_usage_is_bucketed_by_period()
    {
        var db = NewDb();
        db.AddUsage("gpt-5.5", 1000, 200, 0.011);   // lands in the current month

        var month = db.GetMonthlyTotals(Db.CurrentMonth());
        Assert.Equal(1000, month.Input);
        Assert.Equal(0.011, month.CostUsd, 5);

        // a different month is its own (empty) bucket → the display resets each month
        var other = db.GetMonthlyTotals("1999-01");
        Assert.Equal(0, other.Calls);
        Assert.Equal(0.0, other.CostUsd, 5);

        // lifetime total is unchanged (still tracked separately)
        Assert.Equal(1000, db.GetUsageTotals().Input);
    }

    [Fact]
    public void Notifications_roundtrip_unread_and_read()
    {
        var db = NewDb();
        var id1 = db.AddNotification("reminder", 5, "Reminder", "gym at 6");
        db.AddNotification("reminder", 6, "Reminder", "call mom");

        Assert.Equal(2, db.UnreadNotificationCount());
        Assert.Equal(2, db.ListNotifications().Count);

        Assert.True(db.MarkNotificationRead(id1));
        Assert.Equal(1, db.UnreadNotificationCount());

        db.MarkAllNotificationsRead();
        Assert.Equal(0, db.UnreadNotificationCount());

        // unread sorts first
        var fresh = db.AddNotification("reminder", 7, "Reminder", "stretch");
        Assert.Equal(fresh, db.ListNotifications()[0].Id);
    }

    [Fact]
    public void Notifications_surfaced_dismiss_and_clear()
    {
        var db = NewDb();
        var a = db.AddNotification("reminder", 1, "Reminder", "a");
        var b = db.AddNotification("reminder", 2, "Reminder", "b");

        Assert.False(db.GetNotification(a)!.Surfaced);
        db.MarkNotificationSurfaced(a);
        Assert.True(db.GetNotification(a)!.Surfaced);

        Assert.True(db.DeleteNotification(a));
        Assert.Null(db.GetNotification(a));
        Assert.Single(db.ListNotifications());

        db.DeleteAllNotifications();
        Assert.Empty(db.ListNotifications());
    }

    [Fact]
    public void Reminder_private_flag_persists()
    {
        var db = NewDb();
        var id = db.AddReminder(DateTime.UtcNow.ToString("o"), "insulin");
        Assert.False(db.GetReminderPrivate(id));   // default
        db.SetReminderPrivate(id, true);
        Assert.True(db.GetReminderPrivate(id));
    }

    [Fact]
    public void Messages_persist_across_new_connections()
    {
        NewDb().AddMessage("user", "persist me", Now());
        var reopened = NewDb();                 // fresh Db on the same file
        Assert.Contains(reopened.GetMessages(), m => m.Text == "persist me");
    }

    [Fact]
    public void Search_ranks_by_cosine_similarity()
    {
        var db = NewDb();
        var cat = db.AddMessage("user", "the cat sat on the mat", Now());
        var car = db.AddMessage("user", "the car drove down the road", Now());
        db.AddEmbedding("message", cat, Embed("the cat sat on the mat"));
        db.AddEmbedding("message", car, Embed("the car drove down the road"));

        var hits = db.Search(Embed("a cat on a mat"), 2);
        Assert.Equal(cat, hits[0].RefId);        // closest match ranks first
        Assert.True(hits[0].Score >= hits[1].Score);
    }

    [Fact]
    public void Notes_add_update_and_get()
    {
        var db = NewDb();
        var id = db.AddNote("initial insight", Now());
        Assert.Equal("initial insight", db.GetNote(id)!.Text);

        Assert.True(db.UpdateNote(id, "refined insight", Now()));
        Assert.Equal("refined insight", db.GetNote(id)!.Text);
        Assert.False(db.UpdateNote(9999, "nope", Now()));   // missing id
    }

    [Fact]
    public void UpsertEmbedding_replaces_not_duplicates()
    {
        var db = NewDb();
        var id = db.AddNote("v1", Now());
        db.UpsertEmbedding("note", id, Embed("v1"));
        db.UpsertEmbedding("note", id, Embed("v2 completely different words"));  // re-embed
        Assert.Equal(1, db.EmbeddingCount("note", id));     // exactly one, not two
    }

    [Fact]
    public void Search_spans_entries_and_notes()
    {
        var db = NewDb();
        var entry = db.AddMessage("user", "tennis on tuesday", Now());
        db.AddEmbedding("message", entry, Embed("tennis on tuesday"));
        var note = db.AddNote("they value movement and routine", Now());
        db.UpsertEmbedding("note", note, Embed("they value movement and routine"));

        var hits = db.Search(Embed("tennis"), 5);
        Assert.Contains(hits, h => h.RefType == "message" && h.RefId == entry);
    }

    [Fact]
    public void Graph_upsert_node_is_idempotent_and_links()
    {
        var db = NewDb();
        var a = db.UpsertNode("distortion", "rejection-radar", true, 0.8);
        var a2 = db.UpsertNode("distortion", "rejection-radar", true, 0.9);  // same key
        Assert.Equal(a, a2);                                                  // no duplicate
        Assert.Single(db.GetNodes());

        var b = db.UpsertNode("fire", "inventing", false, 1.0);
        db.AddEdge(a, b, "steals-the-fuel", "steals-the-fuel", true, 0.7);
        db.AddEdge(a, b, "steals-the-fuel", "steals-the-fuel", true, 0.9);   // same key
        Assert.Single(db.GetEdges());                                         // upserted, not duplicated
        Assert.Equal(0.9, db.GetNodes().First(n => n.Id == a).Confidence);    // confidence updated
    }

    [Fact]
    public void Compaction_roundtrips_and_wipes()
    {
        var db = NewDb();
        Assert.Null(db.GetCompaction());
        db.SetCompaction("summary v1", "2026-06-19T00:00:00.0000000Z");
        var c = db.GetCompaction()!.Value;
        Assert.Equal("summary v1", c.Summary);
        db.SetCompaction("summary v2", "2026-06-20T00:00:00.0000000Z");   // upsert
        Assert.Equal("summary v2", db.GetCompaction()!.Value.Summary);
        db.WipeAll();
        Assert.Null(db.GetCompaction());
    }

    [Fact]
    public void Compactor_window_is_two_days()
    {
        Assert.Equal(TimeSpan.FromDays(2), Compactor.Window);
        var now = new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);
        var db = NewDb();
        var c = new Compactor(db, new ProviderRegistry(new IChatProvider[] { new OpenAIClient(new System.Net.Http.HttpClient(), new UsageTracker(), new LlmConfig(db)) }));
        Assert.Equal(now.AddDays(-2), c.CutoffUtc(now));
    }

    [Fact]
    public void Settings_roundtrip_and_delete()
    {
        var db = NewDb();
        Assert.Null(db.GetSetting("k"));
        db.SetSetting("k", "v1");
        Assert.Equal("v1", db.GetSetting("k"));
        db.SetSetting("k", "v2");                 // upsert
        Assert.Equal("v2", db.GetSetting("k"));
        db.SetSetting("k", "");                   // empty deletes
        Assert.Null(db.GetSetting("k"));
    }

    [Fact]
    public void LlmConfig_stores_key_and_model_selection()
    {
        var db = NewDb();
        var cfg = new LlmConfig(db);

        Assert.False(cfg.HasStoredKey("openai"));
        cfg.SetKey("openai", "  sk-test  ");
        Assert.True(cfg.HasStoredKey("openai"));
        Assert.Equal("sk-test", cfg.GetKey("openai"));   // trimmed

        Assert.Equal("gpt-5.5", cfg.SelectedModelId("openai"));   // catalog default
        cfg.SetModel("openai", "nonexistent");
        Assert.Equal("gpt-5.5", cfg.SelectedModelId("openai"));   // unknown ignored → default

        cfg.SetKey("openai", null);
        Assert.False(cfg.HasStoredKey("openai"));
    }

    [Fact]
    public void Graph_is_wiped_by_WipeAll()
    {
        var db = NewDb();
        var a = db.UpsertNode("self", "weird-is-the-engine", false, 1);
        var b = db.UpsertNode("joy", "tennis", false, 1);
        db.AddEdge(a, b, "lifts", "lifts", false, 1);
        db.WipeAll();
        Assert.Empty(db.GetNodes());
        Assert.Empty(db.GetEdges());
    }

    [Fact]
    public void WipeAll_clears_messages_notes_and_embeddings()
    {
        var db = NewDb();
        var m = db.AddMessage("user", "something", Now());
        db.AddEmbedding("message", m, Embed("something"));
        var n = db.AddNote("a note", Now());
        db.UpsertEmbedding("note", n, Embed("a note"));

        db.WipeAll();

        Assert.Empty(db.GetMessages());
        Assert.Null(db.GetNote(n));
        Assert.Empty(db.Search(Embed("something"), 5));
    }

    [Fact]
    public void WipeAll_clears_settings_too()
    {
        var db = NewDb();
        db.SetSetting("llmkey:openai", "sk-secret");
        db.WipeAll();
        Assert.Null(db.GetSetting("llmkey:openai"));
    }

    [Fact]
    public void Closed_db_throws_clear_locked_error_not_NRE()
    {
        var db = NewDb();
        db.Close();
        var ex = Assert.Throws<InvalidOperationException>(() => db.AddNote("x", Now()));
        Assert.Contains("locked", ex.Message);
    }

    [Fact]
    public void Alias_tokens_number_per_kind_stably_and_survive_wipe()
    {
        var db = NewDb();
        Assert.Equal("PERSON_1", db.GetOrCreateAlias("PERSON", "Alice"));
        Assert.Equal("PERSON_2", db.GetOrCreateAlias("PERSON", "Bob"));
        Assert.Equal("EMAIL_1", db.GetOrCreateAlias("EMAIL", "a@b.co"));   // numbering is per kind
        Assert.Equal("PERSON_1", db.GetOrCreateAlias("PERSON", "Alice")); // stable on re-ask

        db.WipeAll();
        Assert.Equal("PERSON_1", db.GetOrCreateAlias("PERSON", "Carol")); // restarts cleanly, no UNIQUE clash
    }

    [Fact]
    public void Dev_sandbox_fresh_wipes_and_reset_guards_the_real_db()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sp_devmode_{Guid.NewGuid():N}.db");

        // persistent mode: data survives a reopen
        var db = new Db(root);
        db.OpenDevSandbox();
        db.AddNote("keep me", Now());
        db.Close();
        db = new Db(root);
        db.OpenDevSandbox();
        Assert.Single(db.GetNotes());

        // in-place reset: wiped + reopened, still usable
        db.ResetDevSandbox();
        Assert.Empty(db.GetNotes());
        db.AddNote("post-reset", Now());
        Assert.Single(db.GetNotes());
        db.Close();

        // fresh mode: its OWN scratch file, wiped at open - the persistent sandbox is untouched
        db = new Db(root);
        db.OpenDevSandbox(fresh: true);
        Assert.Empty(db.GetNotes());
        db.AddNote("scratch", Now());
        db.DestroyFile();

        // back to persistent mode: the old data is still there
        db = new Db(root);
        db.OpenDevSandbox();
        Assert.Equal("post-reset", db.GetNotes().Single().Text);
        db.DestroyFile();

        // the guard: reset is REFUSED on a non-sandbox database
        var real = new Db(Path.Combine(Path.GetTempPath(), $"sp_real_{Guid.NewGuid():N}.db"));
        real.OpenPlaintext();
        real.AddNote("precious", Now());
        Assert.Throws<InvalidOperationException>(() => real.ResetDevSandbox());
        Assert.Single(real.GetNotes());   // untouched
        real.DestroyFile();
    }

    [Fact]
    public void Same_instance_mode_switches_land_on_the_right_sandbox_file()
    {
        // Regression: OpenDevSandbox appended its suffix unconditionally, so a reopen after
        // Close() on the SAME instance stacked suffixes (live stray "*.dev.dev" file) and a
        // persistent→fresh switch stayed on the old file.
        var db = new Db(Path.Combine(Path.GetTempPath(), $"sp_switch_{Guid.NewGuid():N}.db"));
        db.OpenDevSandbox();
        db.AddNote("persistent", Now());
        db.Close();

        db.OpenDevSandbox();                                  // reopen: same file, no ".dev.dev"
        Assert.EndsWith(".dev", db.CurrentPath);
        Assert.False(db.CurrentPath.EndsWith(".dev.dev"));
        Assert.Equal("persistent", db.GetNotes().Single().Text);
        db.Close();

        db.OpenDevSandbox(fresh: true);                       // switch flavors on one instance
        Assert.EndsWith(".dev-fresh", db.CurrentPath);
        Assert.Empty(db.GetNotes());                          // scratch is its own (wiped) world
        db.DestroyFile();

        db.OpenDevSandbox();                                  // and back
        Assert.EndsWith(".dev", db.CurrentPath);
        Assert.Equal("persistent", db.GetNotes().Single().Text);
        db.DestroyFile();
    }

    [Fact]
    public void Keyed_open_is_refused_on_a_sandbox_path()
    {
        // Regression (2026-07-02): an idle-lock in sandbox mode raised the PIN screen; the unlock
        // ran Open(realKey) against the sandbox path, whose plaintext-migration ENCRYPTED the
        // freshly-imported sandbox in place — the next plaintext open quarantined it as *.notadb.
        var db = new Db(Path.Combine(Path.GetTempPath(), $"sp_keyed_{Guid.NewGuid():N}.db"));
        db.OpenDevSandbox();
        Assert.True(db.IsSandbox);
        db.AddNote("an hour of import", Now());
        db.Close();

        Assert.Throws<InvalidOperationException>(() => db.Open(new byte[32]));

        db.OpenDevSandbox();                              // still plaintext, still readable
        Assert.Equal("an hour of import", db.GetNotes().Single().Text);
        db.DestroyFile();
    }

    private static string Now() => DateTime.UtcNow.ToString("o");
}
