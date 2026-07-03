using System.Text.Json;
using SemanticPortrait.Core;

namespace SemanticPortrait.Tests;

public class TaskToolsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sp_task_{Guid.NewGuid():N}.db");
    private readonly Db _db;
    private readonly TaskTools _t;

    public TaskToolsTests() { _db = new Db(_path); _db.OpenPlaintext(); _t = new TaskTools(_db); }
    public void Dispose() { try { if (File.Exists(_path)) File.Delete(_path); } catch { } }
    private static string Args(object o) => JsonSerializer.Serialize(o);

    [Fact]
    public async Task Todo_add_list_complete()
    {
        Assert.StartsWith("todo #1", await _t.ExecuteAsync("add_todo", Args(new { text = "call Alex" })));
        Assert.Contains("call Alex", await _t.ExecuteAsync("list_todos", "{}"));
        Assert.Contains("done", await _t.ExecuteAsync("complete_todo", Args(new { id = 1 })));
        Assert.True(_db.ListTodos().Single().Done);
    }

    [Fact]
    public async Task Reminder_set_and_becomes_due()
    {
        var past = DateTime.UtcNow.AddMinutes(-1).ToString("o");
        await _t.ExecuteAsync("set_reminder", Args(new { text = "drink water", when = past }));
        var due = _db.DueReminders(DateTime.UtcNow);
        Assert.Single(due);
        Assert.Equal("drink water", due[0].Text);

        _db.MarkReminderFired(due[0].Id);
        Assert.Empty(_db.DueReminders(DateTime.UtcNow));   // not re-fired
    }

    [Fact]
    public async Task Future_reminder_not_due_yet()
    {
        var future = DateTime.UtcNow.AddHours(2).ToString("o");
        await _t.ExecuteAsync("set_reminder", Args(new { text = "later", when = future }));
        Assert.Empty(_db.DueReminders(DateTime.UtcNow));
        Assert.Contains("later", await _t.ExecuteAsync("list_reminders", "{}"));
    }
}
