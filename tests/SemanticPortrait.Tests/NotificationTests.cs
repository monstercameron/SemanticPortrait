using System.Runtime.CompilerServices;
using SemanticPortrait.Core;

namespace SemanticPortrait.Tests;

public class NotificationTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sp_notif_{Guid.NewGuid():N}.db");
    private Db NewDb() { var d = new Db(_path); d.OpenPlaintext(); return d; }
    public void Dispose() { try { if (File.Exists(_path)) File.Delete(_path); } catch { } }

    // --- fakes ---
    private sealed class FakeChat : IChatProvider
    {
        private readonly string _out; private readonly bool _hasKey;
        public FakeChat(string output, bool hasKey = true) { _out = output; _hasKey = hasKey; }
        public string ProviderId => "fake";
        public string DisplayName => "Fake";
        public bool HasKey => _hasKey;
        public string ModelName => "fake";
        public ModelPricing Pricing => default;
        public async IAsyncEnumerable<string> StreamReplyAsync(string systemPrompt,
            IEnumerable<ChatMessage> history, IReadOnlyList<object>? tools = null,
            Func<string, string, Task<string>>? toolExecutor = null, Action<string>? onReasoning = null,
            string effort = "low", Action<string>? onError = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return _out;
        }
    }

    private sealed class FakeScheduler : IToastScheduler
    {
        public List<(string Tag, string Group, DateTimeOffset When, string Title, string Body, string Arg)> Scheduled = new();
        public List<(string Tag, string Group)> Cancelled = new();
        public List<IReadOnlyList<(string Label, string Argument)>?> Buttons = new();   // parallel to Scheduled
        public Task ScheduleAsync(string tag, string group, DateTimeOffset whenUtc, string title, string body, string argument,
            IReadOnlyList<(string Label, string Argument)>? buttons = null)
        { Scheduled.Add((tag, group, whenUtc, title, body, argument)); Buttons.Add(buttons); return Task.CompletedTask; }
        public void Cancel(string tag, string group) { Cancelled.Add((tag, group)); }
    }

    private static NotificationService Svc(Db db, string classifierOut, FakeScheduler sched, bool hasKey = true)
        => new(db, new ProviderRegistry(new IChatProvider[] { new FakeChat(classifierOut, hasKey) }), sched);

    [Theory]
    [InlineData("{\"private\": true}", true)]
    [InlineData("sure: {\"private\": false}", false)]
    [InlineData("not json at all", true)]      // fail safe
    public async Task Classifier_parses_and_fails_safe(string output, bool expected)
    {
        var db = NewDb();
        Assert.Equal(expected, await Svc(db, output, new FakeScheduler()).ClassifyPrivateAsync("x"));
    }

    [Fact]
    public async Task No_api_key_defaults_to_private()
    {
        var db = NewDb();
        Assert.True(await Svc(db, "{\"private\": false}", new FakeScheduler(), hasKey: false).ClassifyPrivateAsync("x"));
    }

    [Fact]
    public async Task NonPrivate_reminder_shows_text_in_toast()
    {
        var db = NewDb();
        var id = db.AddReminder(DateTime.UtcNow.AddHours(1).ToString("o"), "drink water");
        var sched = new FakeScheduler();
        await Svc(db, "{\"private\": false}", sched).ScheduleReminderAsync(id, "drink water", DateTimeOffset.UtcNow.AddHours(1));

        var t = Assert.Single(sched.Scheduled);
        Assert.Equal("drink water", t.Body);
        Assert.Equal(id.ToString(), t.Tag);
        Assert.Equal(NotificationService.ReminderGroup, t.Group);
        Assert.Equal($"reminder:{id}", t.Arg);
        Assert.False(db.GetReminderPrivate(id));
    }

    [Fact]
    public async Task Private_reminder_is_generic_in_toast_and_flagged()
    {
        var db = NewDb();
        var id = db.AddReminder(DateTime.UtcNow.AddHours(1).ToString("o"), "take insulin");
        var sched = new FakeScheduler();
        await Svc(db, "{\"private\": true}", sched).ScheduleReminderAsync(id, "take insulin", DateTimeOffset.UtcNow.AddHours(1));

        var t = Assert.Single(sched.Scheduled);
        Assert.DoesNotContain("insulin", t.Body);
        Assert.True(db.GetReminderPrivate(id));
    }

    [Fact]
    public void RaiseInApp_adds_unread_notification()
    {
        var db = NewDb();
        var rid = db.AddReminder(DateTime.UtcNow.ToString("o"), "call mom");
        var svc = Svc(db, "{\"private\": false}", new FakeScheduler());
        svc.RaiseInAppForReminder(new Reminder(rid, DateTime.UtcNow.ToString("o"), "call mom", true));

        Assert.Equal(1, svc.UnreadCount());
        var n = Assert.Single(svc.List());
        Assert.Equal("call mom", n.Body);
        Assert.Equal(rid, n.RefId);
    }

    [Fact]
    public async Task Prediction_toast_scheduled_on_make_and_cancelled_on_resolve()
    {
        var db = NewDb();
        var sched = new FakeScheduler();
        var tools = new PredictionTools(db, Svc(db, "{\"private\": false}", sched));

        var due = DateTime.UtcNow.AddDays(2).ToString("o");
        var res = await tools.ExecuteAsync("make_prediction",
            $"{{\"claim\": \"she replies\", \"criterion\": \"a reply arrives\", \"due\": \"{due}\"}}");
        Assert.Contains("logged", res);
        var t = Assert.Single(sched.Scheduled);
        Assert.Equal(NotificationService.PredictionGroup, t.Group);
        Assert.Contains("she replies", t.Body);

        var id = db.GetPredictions().Single().Id;
        await tools.ExecuteAsync("resolve_prediction",
            $"{{\"id\": {id}, \"outcome\": \"she did\", \"score\": 1}}");
        var c = Assert.Single(sched.Cancelled);
        Assert.Equal(NotificationService.PredictionGroup, c.Group);
    }

    [Fact]
    public void Due_predictions_fire_once()
    {
        var db = NewDb();
        var id = db.AddPrediction("claim", "criterion", DateTime.UtcNow.AddMinutes(-5).ToString("o"));
        db.AddPrediction("future", "criterion", DateTime.UtcNow.AddDays(1).ToString("o"));
        db.AddPrediction("undated", "criterion", null);

        var due = Assert.Single(db.DuePredictions(DateTime.UtcNow));
        Assert.Equal(id, due.Id);

        db.MarkPredictionNotified(id);
        Assert.Empty(db.DuePredictions(DateTime.UtcNow));   // never re-fires
    }

    [Fact]
    public async Task TaskTools_cancel_reminder_cancels_toast()
    {
        var db = NewDb();
        var sched = new FakeScheduler();
        var tools = new TaskTools(db, Svc(db, "{\"private\": false}", sched));
        var rid = db.AddReminder(DateTime.UtcNow.AddHours(1).ToString("o"), "later");

        var res = await tools.ExecuteAsync("cancel_reminder", $"{{\"id\": {rid}}}");
        Assert.Contains("cancelled", res);
        var c = Assert.Single(sched.Cancelled);
        Assert.Equal(rid.ToString(), c.Tag);
    }

    [Fact]
    public async Task Discreet_mode_forces_generic_toast_without_asking_the_classifier()
    {
        var db = NewDb();
        var id = db.AddReminder(DateTime.UtcNow.AddHours(1).ToString("o"), "collect the test results");
        var sched = new FakeScheduler();
        NotificationService.Discreet = true;
        try
        {
            // classifier would say NON-private — discreet must override and never even ask
            await Svc(db, "{\"private\": false}", sched).ScheduleReminderAsync(id, "collect the test results", DateTimeOffset.UtcNow.AddHours(1));
        }
        finally { NotificationService.Discreet = false; }

        var t = Assert.Single(sched.Scheduled);
        Assert.DoesNotContain("test results", t.Body);
        Assert.True(db.GetReminderPrivate(id));
    }

    [Fact]
    public async Task Reminder_toasts_carry_snooze_and_done_buttons()
    {
        var db = NewDb();
        var id = db.AddReminder(DateTime.UtcNow.AddHours(1).ToString("o"), "stretch");
        var sched = new FakeScheduler();
        await Svc(db, "{\"private\": false}", sched).ScheduleReminderAsync(id, "stretch", DateTimeOffset.UtcNow.AddHours(1));

        var buttons = Assert.Single(sched.Buttons);
        Assert.NotNull(buttons);
        Assert.Equal(3, buttons!.Count);
        Assert.Contains(buttons, b => b.Argument == $"snooze:reminder:{id}:15");
        Assert.Contains(buttons, b => b.Argument == $"snooze:reminder:{id}:60");
        Assert.Contains(buttons, b => b.Argument == $"done:reminder:{id}");
    }

    [Fact]
    public void Snooze_rearms_a_fired_reminder_at_the_new_time()
    {
        var db = NewDb();
        var id = db.AddReminder(DateTime.UtcNow.AddMinutes(-5).ToString("o"), "water the plants");
        db.MarkReminderFired(id);
        Assert.Empty(db.DueReminders(DateTime.UtcNow));            // fired -> silent

        var newDue = DateTime.UtcNow.AddMinutes(15).ToString("o");
        Assert.True(db.SnoozeReminder(id, newDue));

        var rem = db.GetReminder(id);
        Assert.NotNull(rem);
        Assert.False(rem!.Fired);                                   // re-armed
        Assert.Equal(newDue, rem.DueUtc);
        Assert.Single(db.DueReminders(DateTime.UtcNow.AddMinutes(20)));   // fires again at the new time
    }
}
