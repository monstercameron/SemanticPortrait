using System.Text;
using System.Text.Json;

namespace SemanticPortrait.Core;

/// <summary>A guided journaling "program": a named sequence of daily prompts. The agent runs it —
/// it can list programs, start one, and fetch today's prompt; the app also surfaces the day's
/// prompt in the once-a-day digest. State (active id + start date + last-delivered day) lives in
/// DB settings so it survives restarts and stays behind the lock.</summary>
public sealed record JournalProgram(string Id, string Name, string Blurb, IReadOnlyList<string> Prompts);

public sealed class ProgramTools
{
    private readonly Db _db;
    public ProgramTools(Db db) { _db = db; }

    public static readonly IReadOnlyList<JournalProgram> Catalog = new[]
    {
        new JournalProgram("gratitude7", "7 Days of Gratitude",
            "A gentle week noticing what's good — small and large.",
            new[]
            {
                "What's one small thing today that you'd have missed if you weren't paying attention?",
                "Who made your life a little easier recently, and how?",
                "What's something your body let you do today that you're glad for?",
                "What's a place — a room, a corner, a view — that you're grateful exists?",
                "What's a past version of you that you're thankful for today?",
                "What's something hard that you're — even a little — glad happened?",
                "Looking back over the week: what are you carrying forward?",
            }),
        new JournalProgram("morningpages", "Morning Pages",
            "Three unfiltered stream-of-consciousness clears, one a day, no editing.",
            new[]
            {
                "Just write. Whatever's at the top of your mind — don't shape it, don't judge it. Empty the pockets.",
                "Again, unfiltered. What's loud in your head this morning? Let it spill without fixing it.",
                "One more clear. Write past the first tired thought — what's underneath it?",
            }),
        new JournalProgram("reset5", "5-Day Reset",
            "A short, honest audit of where you are and where you're pointed.",
            new[]
            {
                "Where are you right now — not where you should be. Just the honest reading.",
                "What's one thing draining you that you have some control over?",
                "What's one thing that reliably makes a day better, that you've been skipping?",
                "If nothing external changed, what could you change in how you meet it?",
                "What's the smallest concrete step you'd actually take this week?",
            }),
    };

    public bool Handles(string name) =>
        name is "list_programs" or "start_program" or "program_today" or "stop_program";

    public IReadOnlyList<object> Specs => new object[]
    {
        new { type = "function", name = "list_programs",
              description = "List the guided journaling programs (short named prompt sequences) the user can start.",
              parameters = Empty() },
        new { type = "function", name = "start_program",
              description = "Start a guided journaling program by id (from list_programs). Returns day 1's prompt. " +
                            "Use when the user wants a structured journaling practice (gratitude, morning pages, a reset).",
              parameters = Obj("id", "Program id from list_programs.") },
        new { type = "function", name = "program_today",
              description = "Get the active program's prompt for today (or a note that none is active / it's complete).",
              parameters = Empty() },
        new { type = "function", name = "stop_program",
              description = "Stop the active guided program.", parameters = Empty() },
    };

    public Task<string> ExecuteAsync(string name, string argsJson)
    {
        switch (name)
        {
            case "list_programs":
                var sb = new StringBuilder("Guided programs:\n");
                foreach (var p in Catalog) sb.AppendLine($"- {p.Id}: {p.Name} ({p.Prompts.Count} days) — {p.Blurb}");
                var active0 = Active();
                if (active0 is not null) sb.AppendLine($"(currently running: {active0.Value.Program.Name}, day {active0.Value.Day})");
                return Task.FromResult(sb.ToString().TrimEnd());

            case "start_program":
                var id = Str(argsJson, "id");
                var prog = Catalog.FirstOrDefault(p => p.Id == id);
                if (prog is null) return Task.FromResult($"error: no program '{id}'. Call list_programs.");
                _db.SetSetting("program_id", prog.Id);
                _db.SetSetting("program_start", DateTime.Now.ToString("yyyy-MM-dd"));
                _db.SetSetting("program_delivered", "");
                return Task.FromResult($"started \"{prog.Name}\" ({prog.Prompts.Count} days). Day 1 prompt: {prog.Prompts[0]}");

            case "program_today":
                var a = Active();
                if (a is null) return Task.FromResult("no guided program is active. Call list_programs to offer some.");
                var (program, day) = a.Value;
                if (day > program.Prompts.Count)
                    return Task.FromResult($"\"{program.Name}\" is complete ({program.Prompts.Count} days done). Offer to reflect on the whole arc, or start another.");
                return Task.FromResult($"\"{program.Name}\" day {day}/{program.Prompts.Count}: {program.Prompts[day - 1]}");

            case "stop_program":
                _db.SetSetting("program_id", "");
                return Task.FromResult("guided program stopped.");
        }
        return Task.FromResult("error: unknown tool.");
    }

    /// <summary>The active program + which day it is (1-based, by local days since start), or null.</summary>
    public (JournalProgram Program, int Day)? Active()
    {
        var id = _db.GetSetting("program_id");
        if (string.IsNullOrEmpty(id)) return null;
        var prog = Catalog.FirstOrDefault(p => p.Id == id);
        if (prog is null) return null;
        var startStr = _db.GetSetting("program_start");
        var start = DateTime.TryParse(startStr, out var s) ? s.Date : DateTime.Now.Date;
        var day = (int)(DateTime.Now.Date - start).TotalDays + 1;
        return (prog, Math.Max(1, day));
    }

    /// <summary>Mark today's prompt delivered (so the digest doesn't repeat it); true if it was
    /// NOT already delivered today (i.e. the caller should deliver now).</summary>
    public bool ClaimTodayDelivery()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (_db.GetSetting("program_delivered") == today) return false;
        _db.SetSetting("program_delivered", today);
        return true;
    }

    private static object Empty() => new { type = "object", properties = new Dictionary<string, object>(), required = Array.Empty<string>(), additionalProperties = false };
    private static object Obj(string key, string desc) => new
    {
        type = "object",
        properties = new Dictionary<string, object> { [key] = new { type = "string", description = desc } },
        required = new[] { key },
        additionalProperties = false,
    };
    private static string Str(string json, string key)
    {
        try { using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json); return d.RootElement.TryGetProperty(key, out var v) ? v.GetString() ?? "" : ""; }
        catch { return ""; }
    }
}
