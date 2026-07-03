using System.Text;
using System.Text.Json;

namespace SemanticPortrait.Core;

/// <summary>
/// User-facing actionables: a todo list and time-based reminders. Reminders fire proactively
/// (a timer triggers the agent to message the user when one is due). These are explicit user
/// requests (not the self-model), so the main chat agent gets them directly.
/// </summary>
public sealed class TaskTools
{
    private readonly Db _db;
    private readonly NotificationService? _notify;
    public TaskTools(Db db, NotificationService? notify = null) { _db = db; _notify = notify; }

    private static readonly HashSet<string> _names = new()
        { "add_todo", "list_todos", "complete_todo", "set_reminder", "list_reminders", "cancel_reminder", "upcoming" };
    public bool Handles(string name) => _names.Contains(name);

    public IReadOnlyList<object> Specs => new object[]
    {
        new { type = "function", name = "add_todo",
              description = "Add an item to the user's todo list when they say they need/want to do something.",
              parameters = Obj(new[]{"text"}, ("text", "string", "The task.")) },
        new { type = "function", name = "list_todos",
              description = "List the user's todos (open + done).",
              parameters = Obj(Array.Empty<string>()) },
        new { type = "function", name = "complete_todo",
              description = "Mark a todo done (by id from list_todos).",
              parameters = Obj(new[]{"id"}, ("id", "integer", "Todo id.")) },
        new { type = "function", name = "set_reminder",
              description = "Set a time-based reminder. When it's due, you'll be triggered to message the " +
                            "user about it. 'when' must be an ISO 8601 date/time (compute it from the current time). " +
                            "Choose the fire time with LEAD built in: if the user gave an explicit time, use it; " +
                            "errands with a day but no time fire that morning (~9:30am); appointments fire 1 hour " +
                            "before; anything needing prep also merits an evening-before (~7pm) reminder. Check " +
                            "'upcoming' first so you never double-book something already tracked.",
              parameters = Obj(new[]{"text","when"}, ("text", "string", "What to remind them about."),
                               ("when", "string", "ISO 8601 due date/time.")) },
        new { type = "function", name = "list_reminders",
              description = "List pending reminders.", parameters = Obj(Array.Empty<string>()) },
        new { type = "function", name = "cancel_reminder",
              description = "Cancel a reminder by id.", parameters = Obj(new[]{"id"}, ("id","integer","Reminder id.")) },
        new { type = "function", name = "upcoming",
              description = "The user's time-ordered agenda in one view: pending reminders, future dated events, " +
                            "predictions awaiting resolution, and open todos. Call when they ask what's coming up / " +
                            "what's on their plate / planning a day or week — and BEFORE setting a new reminder " +
                            "(never double-book something already tracked). Lines carry ids for follow-up actions.",
              parameters = Obj(Array.Empty<string>(), ("horizon_days", "integer", "How far ahead to look (default 14, max 90).")) },
    };

    public Task<string> ExecuteAsync(string name, string argsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            var r = doc.RootElement;
            switch (name)
            {
                case "add_todo":
                    var tt = Str(r, "text");
                    if (string.IsNullOrWhiteSpace(tt)) return Task.FromResult("error: 'text' required.");
                    return Task.FromResult($"todo #{_db.AddTodo(tt!)} added");
                case "list_todos":
                    var todos = _db.ListTodos();
                    if (todos.Count == 0) return Task.FromResult("(no todos)");
                    var sb = new StringBuilder();
                    foreach (var t in todos) sb.AppendLine($"- #{t.Id} [{(t.Done ? "x" : " ")}] {t.Text}");
                    return Task.FromResult(sb.ToString().TrimEnd());
                case "complete_todo":
                    return Task.FromResult(TryId(r, out var cid) && _db.SetTodoDone(cid, true)
                        ? $"todo #{cid} done" : "error: todo not found.");
                case "set_reminder":
                    var rt = Str(r, "text"); var when = Str(r, "when");
                    if (string.IsNullOrWhiteSpace(rt) || string.IsNullOrWhiteSpace(when)) return Task.FromResult("error: 'text' and 'when' required.");
                    if (!DateTime.TryParse(when, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                        return Task.FromResult("error: 'when' must be ISO 8601.");
                    var dueUtc = dt.ToUniversalTime();
                    var remId = _db.AddReminder(dueUtc.ToString("o"), rt!);
                    // Classify + schedule the OS toast in the background so the tool reply stays snappy.
                    if (_notify is not null)
                        _ = _notify.ScheduleReminderAsync(remId, rt!, new DateTimeOffset(dueUtc, TimeSpan.Zero));
                    return Task.FromResult($"reminder #{remId} set for {dt.ToLocalTime():MMM d h:mm tt}");
                case "list_reminders":
                    var rems = _db.ListReminders(pendingOnly: true);
                    if (rems.Count == 0) return Task.FromResult("(no pending reminders)");
                    var sb2 = new StringBuilder();
                    foreach (var rm in rems)
                    {
                        var lt = DateTime.TryParse(rm.DueUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d.ToLocalTime().ToString("MMM d h:mm tt") : rm.DueUtc;
                        sb2.AppendLine($"- #{rm.Id} {lt}: {rm.Text}");
                    }
                    return Task.FromResult(sb2.ToString().TrimEnd());
                case "cancel_reminder":
                    if (TryId(r, out var rid) && _db.CancelReminder(rid))
                    {
                        _notify?.CancelReminder(rid);
                        return Task.FromResult($"reminder #{rid} cancelled");
                    }
                    return Task.FromResult("error: reminder not found.");
                case "upcoming":
                    var horizon = r.TryGetProperty("horizon_days", out var hd) && hd.TryGetInt32(out var hv)
                        ? Math.Clamp(hv, 1, 90) : 14;
                    return Task.FromResult(BuildUpcoming(horizon));
                default: return Task.FromResult($"error: unknown tool '{name}'");
            }
        }
        catch (Exception ex) { return Task.FromResult($"error: {ex.Message}"); }
    }

    /// <summary>
    /// The unified agenda: pending reminders + future dated events + open predictions with a due
    /// window + open todos (undated tail), grouped by when they land. Every line carries its id
    /// so the agent can complete/cancel in the same turn. Deterministic; bounded output.
    /// </summary>
    public string BuildUpcoming(int horizonDays, DateTime? nowUtcOverride = null)
    {
        var nowUtc = nowUtcOverride ?? DateTime.UtcNow;
        var endUtc = nowUtc.AddDays(horizonDays);
        var todayLocal = nowUtc.ToLocalTime().Date;

        static DateTime? P(string? s) =>
            DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d)
                ? d.ToUniversalTime() : null;

        var dated = new List<(DateTime WhenUtc, string Line)>();
        foreach (var rem in _db.ListReminders(pendingOnly: true))
            if (P(rem.DueUtc) is { } d && d <= endUtc)
                dated.Add((d, $"[reminder #{rem.Id}] {When(d)} — {rem.Text}"));
        foreach (var ev in _db.GetEventsRange(nowUtc.ToString("o"), endUtc.ToString("o")))
            if (P(ev.EventUtc) is { } d)
                dated.Add((d, $"[event #{ev.Id}] {When(d)} — {ev.Summary}"));
        foreach (var p in _db.GetPredictions().Where(p => p.ResolvedUtc is null))
            if (P(p.DueUtc) is { } d && d <= endUtc)
                dated.Add((d, $"[prediction #{p.Id}] resolves {When(d)} — {p.Claim}"));

        var sb = new StringBuilder();
        void Group(string title, Func<DateTime, bool> pick)
        {
            var rows = dated.Where(x => pick(x.WhenUtc)).OrderBy(x => x.WhenUtc).ToList();
            if (rows.Count == 0) return;
            sb.AppendLine(title);
            foreach (var (_, line) in rows.Take(10)) sb.AppendLine("- " + line);
            if (rows.Count > 10) sb.AppendLine($"  (+{rows.Count - 10} more in this group)");
        }
        DateTime LocalDay(DateTime utc) => utc.ToLocalTime().Date;

        Group("OVERDUE:", d => d < nowUtc);
        Group("TODAY:", d => d >= nowUtc && LocalDay(d) == todayLocal);
        Group("TOMORROW:", d => d >= nowUtc && LocalDay(d) == todayLocal.AddDays(1));
        Group("THIS WEEK:", d => d >= nowUtc && LocalDay(d) > todayLocal.AddDays(1) && LocalDay(d) <= todayLocal.AddDays(7));
        Group($"LATER (within {horizonDays} days):", d => d >= nowUtc && LocalDay(d) > todayLocal.AddDays(7));

        var open = _db.ListTodos().Where(t => !t.Done).ToList();
        if (open.Count > 0)
        {
            sb.AppendLine("OPEN TODOS (no date):");
            foreach (var t in open.Take(8)) sb.AppendLine($"- [todo #{t.Id}] {t.Text}");
            if (open.Count > 8) sb.AppendLine($"  (+{open.Count - 8} more — list_todos for all)");
        }

        return sb.Length == 0
            ? $"(nothing scheduled in the next {horizonDays} days, and no open todos)"
            : sb.ToString().TrimEnd();

        string When(DateTime utc)
        {
            var local = utc.ToLocalTime();
            var days = (local.Date - todayLocal).Days;
            var rel = days switch
            {
                < -1 => $"{-days} days ago", -1 => "yesterday", 0 => "today",
                1 => "tomorrow", <= 7 => local.ToString("dddd"), _ => $"in {days} days",
            };
            return $"{rel}, {local:MMM d h:mm tt}";
        }
    }

    private static string? Str(JsonElement r, string k) =>
        r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool TryId(JsonElement r, out long id)
    { id = 0; return r.TryGetProperty("id", out var v) && v.TryGetInt64(out id); }

    private static object Obj(string[] required, params (string name, string type, string desc)[] props)
    {
        var dict = new Dictionary<string, object>();
        foreach (var p in props) dict[p.name] = new { type = p.type, description = p.desc };
        return new { type = "object", properties = dict, required, additionalProperties = false };
    }
}
