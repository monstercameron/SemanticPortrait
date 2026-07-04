using SemanticPortrait.Core;

namespace SemanticPortrait.Tests;

/// <summary>
/// The unified agenda (`upcoming`) and the attention-aware toast timing: grouping is by LOCAL
/// day, every line carries an id, overdue is separated, todos trail undated, and OS toasts
/// never fire inside quiet hours (and always trail the true due time by the grace window so a
/// focused app can cancel them first).
/// </summary>
public class UpcomingTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sp_upcoming_{Guid.NewGuid():N}.db");
    private readonly Db _db;
    private readonly TaskTools _tools;

    public UpcomingTests()
    {
        _db = new Db(_path);
        _db.OpenPlaintext();
        _tools = new TaskTools(_db);
    }
    public void Dispose() => _db.DestroyFile();

    private static string Iso(DateTime utc) => utc.ToString("o");

    [Fact]
    public void Agenda_groups_by_when_it_lands_and_carries_ids()
    {
        // Deterministic clock: anchor "now" at LOCAL noon so a +2h reminder can't cross midnight
        // into TOMORROW (the old real-clock version flaked when run late in the evening).
        var nowLocal = DateTime.Today.AddHours(12);
        var now = nowLocal.ToUniversalTime();
        var overdueId = _db.AddReminder(Iso(now.AddHours(-3)), "restring racquet");
        var todayId = _db.AddReminder(Iso(now.AddHours(2)), "buy the iPad");     // 2pm local — same day
        var laterId = _db.AddReminder(Iso(now.AddDays(10)), "renew passport");
        _db.AddEvent(Iso(now.AddDays(1).Date.AddHours(18)), "tennis with Alex");
        var todoId = _db.AddTodo("clean the garage");

        var agenda = _tools.BuildUpcoming(14, now);

        Assert.Contains("OVERDUE:", agenda);
        Assert.Contains($"[reminder #{overdueId}]", agenda);
        Assert.Contains("TODAY:", agenda);
        Assert.Contains($"[reminder #{todayId}]", agenda);
        Assert.Contains("tennis with Alex", agenda);
        Assert.Contains($"LATER (within 14 days):", agenda);
        Assert.Contains($"[reminder #{laterId}]", agenda);
        Assert.Contains("OPEN TODOS (no date):", agenda);
        Assert.Contains($"[todo #{todoId}]", agenda);
        // grouped sections appear in landing order
        Assert.True(agenda.IndexOf("OVERDUE:") < agenda.IndexOf("TODAY:"));
        Assert.True(agenda.IndexOf("TODAY:") < agenda.IndexOf("LATER"));
    }

    [Fact]
    public void Horizon_bounds_what_is_shown()
    {
        var now = DateTime.UtcNow;
        _db.AddReminder(Iso(now.AddDays(3)), "inside horizon");
        _db.AddReminder(Iso(now.AddDays(40)), "beyond horizon");

        var agenda = _tools.BuildUpcoming(7);
        Assert.Contains("inside horizon", agenda);
        Assert.DoesNotContain("beyond horizon", agenda);
    }

    [Fact]
    public void Fired_reminders_completed_todos_and_resolved_predictions_stay_out()
    {
        var now = DateTime.UtcNow;
        var r = _db.AddReminder(Iso(now.AddHours(1)), "already fired");
        _db.MarkReminderFired(r);
        var t = _db.AddTodo("already done");
        _db.SetTodoDone(t, true);

        var agenda = _tools.BuildUpcoming(14);
        Assert.DoesNotContain("already fired", agenda);
        Assert.DoesNotContain("already done", agenda);
    }

    [Fact]
    public void Empty_agenda_says_so_honestly()
    {
        var agenda = _tools.BuildUpcoming(14);
        Assert.Contains("nothing scheduled", agenda);
    }

    [Fact]
    public void Todo_overflow_is_capped_with_a_pointer()
    {
        for (var i = 0; i < 12; i++) _db.AddTodo($"todo number {i}");
        var agenda = _tools.BuildUpcoming(14);
        Assert.Contains("(+4 more — list_todos for all)", agenda);
    }

    [Fact]
    public void Dated_todos_join_the_day_groups_undated_trail_below()
    {
        var now = DateTime.UtcNow;
        var datedId = _db.AddTodo("file the tax extension", Iso(now.AddHours(2)));
        var freeId = _db.AddTodo("clean the garage");

        var agenda = _tools.BuildUpcoming(14);
        Assert.Contains($"[todo #{datedId}]", agenda);
        Assert.Contains("file the tax extension", agenda);
        // the dated one lives in its day group, NOT duplicated in the undated section
        var openIdx = agenda.IndexOf("OPEN TODOS (no date):");
        Assert.True(openIdx > 0);
        Assert.DoesNotContain("file the tax extension", agenda[openIdx..]);
        Assert.Contains($"[todo #{freeId}]", agenda[openIdx..]);
    }

    [Fact]
    public async Task Evening_checkin_tool_sets_validates_and_hides_without_delegates()
    {
        var stored = -1;
        var wired = new TaskTools(_db, null, getCheckinHour: () => stored, setCheckinHour: h => stored = h);
        Assert.True(wired.Handles("set_evening_checkin"));
        Assert.Contains(wired.Specs, s2 => s2.ToString()!.Contains("set_evening_checkin"));

        Assert.Contains("21:00", await wired.ExecuteAsync("set_evening_checkin", "{\"hour\":21}"));
        Assert.Equal(21, stored);
        Assert.Contains("turned off", await wired.ExecuteAsync("set_evening_checkin", "{\"hour\":0}"));
        Assert.Equal(0, stored);
        Assert.Contains("error", await wired.ExecuteAsync("set_evening_checkin", "{\"hour\":12}"));   // noon is not evening
        Assert.Equal(0, stored);

        // without the host delegates the tool neither advertises nor answers
        var bare = new TaskTools(_db);
        Assert.False(bare.Handles("set_evening_checkin"));
        Assert.DoesNotContain(bare.Specs, s2 => s2.ToString()!.Contains("set_evening_checkin"));
    }

    [Fact]
    public void Tool_is_wired_and_executes()
    {
        Assert.True(_tools.Handles("upcoming"));
        _db.AddReminder(Iso(DateTime.UtcNow.AddHours(1)), "wired check");
        var result = _tools.ExecuteAsync("upcoming", "{}").Result;
        Assert.Contains("wired check", result);
    }

    // ---- attention-aware toast timing -------------------------------------------------

    [Fact]
    public void Toasts_never_fire_inside_quiet_hours()
    {
        // 2:30 AM local due time → the toast rolls to 9 AM (+grace); the local wall-clock hour
        // is what matters, so build the due times in LOCAL terms.
        var local230am = new DateTimeOffset(DateTime.Today.AddDays(1).AddHours(2).AddMinutes(30));
        var shifted = NotificationService.ToastTimeFor(local230am).ToLocalTime();
        Assert.Equal(9, shifted.Hour);
        Assert.Equal(local230am.Date, shifted.Date);

        // 11:40 PM local → rolls to 9 AM the NEXT day
        var local1140pm = new DateTimeOffset(DateTime.Today.AddDays(1).AddHours(23).AddMinutes(40));
        var shifted2 = NotificationService.ToastTimeFor(local1140pm).ToLocalTime();
        Assert.Equal(9, shifted2.Hour);
        Assert.Equal(local1140pm.Date.AddDays(1), shifted2.Date);
    }

    [Fact]
    public void Daytime_toasts_trail_the_due_time_by_the_grace_window()
    {
        // The grace exists so a FOCUSED app can deliver in-thread and cancel the toast first.
        var localNoon = new DateTimeOffset(DateTime.Today.AddDays(1).AddHours(12));
        var t = NotificationService.ToastTimeFor(localNoon);
        Assert.Equal(localNoon.ToUniversalTime() + NotificationService.ToastGrace, t);
    }
}
