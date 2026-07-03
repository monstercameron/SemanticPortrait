using System.Text.Json;

namespace SemanticPortrait.Core;

/// <summary>
/// Deterministic onboarding-intake counter. The LLM judges WHETHER a question got a useful answer
/// (irreducibly semantic); everything countable is code: which of the 20 are resolved, how many,
/// what's next, when it's done. State lives in the settings table (NOT the profile — profile
/// fields echo back through get_profile into agent context; this is operational state, not
/// memory). The main agent gets the tool only while the intake is unfinished, and the Now block
/// carries a one-line status computed here — so tracking never rides on the model's mood and
/// never stacks context.
/// </summary>
public sealed class IntakeTools
{
    public const int QuestionCount = 20;
    private const string SettingKey = "intake_state";

    private readonly Db _db;
    public IntakeTools(Db db) => _db = db;

    public bool Handles(string name) => name == "record_intake_progress";

    public IReadOnlyList<object> Specs => new object[]
    {
        new
        {
            type = "function",
            name = "record_intake_progress",
            description = "REQUIRED bookkeeping during the onboarding intake: the moment a numbered " +
                          "intake question is resolved — usefully answered OR explicitly skipped — " +
                          "record it here (one call per question). If the user opts out of the whole " +
                          "intake ('just start'), call once with status='aborted'. The counter is " +
                          "deterministic; the result tells you exactly what's next.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    question = new { type = "integer", description = "The intake question number, 1-20. Omit only when status='aborted'." },
                    status = new { type = "string", description = "'answered' (useful answer given), 'skipped' (user declined this one), or 'aborted' (user ended the whole intake)." },
                },
                required = new[] { "status" },
                additionalProperties = false,
            },
        },
    };

    // Arcs: 1-3 basics · 4-8 life snapshot · 9-12 inner weather · 13-16 patterns · 17-20 fires/aims.
    private static string ArcOf(int q) => q switch
    {
        <= 3 => "arc 1 · basics",
        <= 8 => "arc 2 · life snapshot",
        <= 12 => "arc 3 · inner weather",
        <= 16 => "arc 4 · patterns",
        _ => "arc 5 · fires and aims",
    };

    private sealed class State
    {
        public Dictionary<int, string> Resolved { get; set; } = new();   // question → answered|skipped
        public bool Aborted { get; set; }
    }

    private State Load()
    {
        try
        {
            var raw = _db.GetSetting(SettingKey);
            if (!string.IsNullOrEmpty(raw)) return JsonSerializer.Deserialize<State>(raw) ?? new();
        }
        catch (Exception e) { DevTrap.Report("intake-state-load", e); }   // corrupt state → start clean
        return new();
    }

    private void Save(State s) => _db.SetSetting(SettingKey, JsonSerializer.Serialize(s));

    public bool IsComplete()
    {
        var s = Load();
        return s.Aborted || s.Resolved.Count >= QuestionCount;
    }

    /// <summary>A completed history import supersedes the intake — the model already knows them
    /// far better than 20 questions would. Ends the intake so it never nags a returning life.</summary>
    public void CompleteViaImport()
    {
        var s = Load();
        s.Aborted = true;
        Save(s);
    }

    /// <summary>One deterministic status line for the system prompt, or null when the intake is
    /// finished (complete/aborted) and should cost zero context.</summary>
    public string? StatusLine()
    {
        var s = Load();
        if (s.Aborted || s.Resolved.Count >= QuestionCount) return null;
        var next = NextUnresolved(s);
        return $"Intake status: {s.Resolved.Count}/{QuestionCount} questions resolved; next is Q{next} ({ArcOf(next)}). " +
               "Resume per your onboarding section — weave it in naturally, and record each resolution with record_intake_progress.";
    }

    private static int NextUnresolved(State s)
    {
        for (var q = 1; q <= QuestionCount; q++)
            if (!s.Resolved.ContainsKey(q)) return q;
        return QuestionCount;
    }

    private static readonly (int From, int To, string Name)[] Arcs =
    {
        (1, 3, "Arc 1 (basics)"), (4, 8, "Arc 2 (life snapshot)"), (9, 12, "Arc 3 (inner weather)"),
        (13, 16, "Arc 4 (patterns)"), (17, 20, "Arc 5 (fires and aims)"),
    };

    /// <summary>The arc that question q belongs to, if recording q made it fully resolved.</summary>
    private static string? ArcJustCompleted(State s, int q)
    {
        var arc = Arcs.First(a => q >= a.From && q <= a.To);
        for (var i = arc.From; i <= arc.To; i++)
            if (!s.Resolved.ContainsKey(i)) return null;
        return arc.Name;
    }

    public Task<string> ExecuteAsync(string name, string argumentsJson)
    {
        try
        {
            if (name != "record_intake_progress") return Task.FromResult($"error: unknown tool '{name}'");
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var r = doc.RootElement;

            var status = r.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String
                ? st.GetString()!.Trim().ToLowerInvariant() : "";
            if (status is not ("answered" or "skipped" or "aborted"))
                return Task.FromResult("error: status must be 'answered', 'skipped', or 'aborted'.");

            var s = Load();

            if (status == "aborted")
            {
                s.Aborted = true;
                Save(s);
                return Task.FromResult($"intake ended at the user's request ({s.Resolved.Count}/{QuestionCount} resolved). " +
                                       "Close it warmly — the rest fills in from real entries.");
            }

            if (!(r.TryGetProperty("question", out var qe) && qe.TryGetInt32(out var q)))
                return Task.FromResult("error: 'question' (1-20) is required for answered/skipped.");
            if (q is < 1 or > QuestionCount)
                return Task.FromResult($"error: question must be 1-{QuestionCount}.");

            s.Resolved[q] = status;   // idempotent: re-recording updates, never double-counts
            Save(s);

            if (s.Resolved.Count >= QuestionCount)
                return Task.FromResult($"Q{q} {status} — {QuestionCount}/{QuestionCount}: the intake is COMPLETE. " +
                                       "Send the final arc's batch to analysis, then deliver the closing sketch " +
                                       "(stated vs inferred) and the one concrete first action.");
            var next = NextUnresolved(s);
            // Deterministic batching nudge: the counter knows the arc boundaries, so the tool
            // result — not the model's memory — announces when a batch is due. (Observed live:
            // a safety branch mid-arc-4 disrupted the "arc done → hand off" reflex and the arc
            // was never sent; its SI content never reached the analyst.)
            var arcDone = ArcJustCompleted(s, q);
            var nudge = arcDone is null ? "" :
                $" {arcDone} is COMPLETE — send_to_analysis its distilled batch NOW, before asking Q{next}.";
            return Task.FromResult($"Q{q} {status} — {s.Resolved.Count}/{QuestionCount} resolved; next is Q{next} ({ArcOf(next)}).{nudge}");
        }
        catch (Exception ex) { return Task.FromResult($"error: {ex.Message}"); }
    }
}
