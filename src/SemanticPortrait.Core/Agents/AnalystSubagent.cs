using System.Text;

namespace SemanticPortrait.Core;

/// <summary>
/// Clean-room analyst (plan §7). Runs in a fresh context that never sees the live chat, so the
/// user can't steer what gets written into the long-term model. Reads relevant memory and
/// performs all durable writes (notes + profile) from un-poisoned context. The entry/reply are
/// passed as DATA, fenced and labeled, not as instructions.
/// </summary>
public sealed class AnalystSubagent
{
    // Resolved per call from the registry so the analyst always runs on the provider the user
    // selected (a fixed IChatProvider from DI silently pins to whichever registered last).
    private readonly ProviderRegistry _providers;
    private readonly MemoryTools _memory;
    private readonly ProfileTools _profile;
    private readonly GraphTools _graph;
    private readonly EntryTools _entry;
    private readonly PredictionTools _pred;
    private readonly RecallTools _recall;   // AnalystSpecs lane only: portrait + list_node_labels (no raw chat)
    private readonly TraceLog _trace;

    public AnalystSubagent(ProviderRegistry providers, MemoryTools memory, ProfileTools profile,
        GraphTools graph, EntryTools entry, PredictionTools pred, RecallTools recall, TraceLog trace)
    {
        _providers = providers; _memory = memory; _profile = profile; _graph = graph; _entry = entry; _pred = pred; _recall = recall; _trace = trace;
    }

    /// <summary>
    /// Analyze the latest exchange and update memory. Returns a terse change summary.
    /// <paramref name="rawEntry"/> is the user's verbatim text (exact word choices are load-bearing);
    /// <paramref name="distilled"/> is the anchor's hand-off summary (may be empty). Passing both keeps
    /// entry_meta honest — mood/valence come from the user's own words, not a paraphrase.
    /// <paramref name="onProviderError"/> fires when the provider failed (offline, bad key) —
    /// callers use it to queue the reflection for retry instead of losing it.
    /// </summary>
    public async Task<string> ReflectAsync(long entryId, string rawEntry, string distilled, string reply,
        Action<string, string>? onToolCall = null, Action<string>? onProviderError = null,
        CancellationToken ct = default)
    {
        // Structured DATA, not conversation. The subagent treats this as content to analyze.
        // The raw entry can be missing (persist failed / legacy queue rows) — fall back to the
        // distillation as the entry rather than analyzing an empty string.
        var haveRaw = !string.IsNullOrWhiteSpace(rawEntry);
        var data =
            "Analyze this exchange and update long-term memory as instructed by your system prompt.\n" +
            "FIRST call record_entry_meta for message_id " + entryId + " with COMPLETE metadata.\n\n" +
            "<entry message_id=\"" + entryId + "\">\n" + (haveRaw ? rawEntry : distilled) + "\n</entry>\n\n" +
            (haveRaw && !string.IsNullOrWhiteSpace(distilled)
                ? "<anchor_distillation>\n" + distilled + "\n</anchor_distillation>\n\n" : "") +
            (!string.IsNullOrWhiteSpace(reply)
                ? "<assistant_reply>\n" + reply + "\n</assistant_reply>" : "");

        var input = new[] { new ChatMessage("user", data) };
        // Clean-room recall lanes: notes-only search (AnalystSpecs) + portrait/list_node_labels —
        // all analyst-authored stores; the raw chat thread stays out of reach by construction.
        var specs = _profile.Specs.Concat(_memory.AnalystSpecs).Concat(_graph.Specs)
            .Concat(_entry.Specs).Concat(_pred.Specs).Concat(_recall.AnalystSpecs).ToList();

        _trace.Add("analyst", "start", "reflect", Truncate(haveRaw ? rawEntry : distilled, 200));

        async Task<string> Exec(string name, string args)
        {
            string result;
            if (_entry.Handles(name)) result = await _entry.ExecuteAsync(name, args);
            else if (_graph.Handles(name)) result = await _graph.ExecuteAsync(name, args);
            else if (_pred.Handles(name)) result = await _pred.ExecuteAsync(name, args);
            else if (_recall.Handles(name)) result = await _recall.ExecuteAsync(name, args);
            else if (_memory.Handles(name)) result = await _memory.ExecuteAsync(name, args);
            else result = await _profile.ExecuteAsync(name, args);
            var detail = $"params: {args}\nresult: {result}";
            _trace.Add("analyst", "tool", name, $"params: {args}\nresult: {Truncate(result, 400)}");
            onToolCall?.Invoke(name, detail);
            return result;
        }

        // Time is load-bearing in a journal: without Now the analyst cannot resolve "yesterday"
        // or "last Tuesday" into the absolute dates recall depends on.
        var sysWithNow = Prompts.AnalystSubagent + NowBlock(
            "The entry above was written at this time unless it says otherwise - resolve every " +
            "relative time reference in it against this clock before logging events or notes.");

        var summary = new StringBuilder();
        await foreach (var tok in _providers.Active.StreamReplyAsync(
            sysWithNow, input, specs, Exec,
            onReasoning: r => _trace.Add("analyst", "thought", "reasoning", r),
            effort: "high",              // deep reasoning for the durable model
            onError: e => { _trace.Add("analyst", "error", "provider", e); onProviderError?.Invoke(e); }, ct: ct))
            summary.Append(tok);

        var final = summary.ToString().Trim();
        _trace.Add("analyst", "summary", "reflect", final);
        return final;
    }

    /// <summary>Import a chunk of historical material (notes / prior analysis) into the model.
    /// Medium effort (backfill tier); notes-only recall; log_event allowed, no entry_meta.</summary>
    public async Task<string> ImportAsync(string text, Action<string, string>? onToolCall = null, CancellationToken ct = default)
    {
        var data = "Incorporate this imported material per your system prompt.\n\n" +
                   "<imported_material>\n" + text + "\n</imported_material>";
        var input = new[] { new ChatMessage("user", data) };
        var specs = _profile.Specs.Concat(_memory.AnalystSpecs).Concat(_graph.Specs)
            .Concat(_entry.ImportSpecs).Concat(_pred.Specs).Concat(_recall.AnalystSpecs).ToList();

        _trace.Add("analyst", "start", "import", Truncate(text, 200));

        async Task<string> Exec(string name, string args)
        {
            string result;
            if (_entry.Handles(name)) result = await _entry.ExecuteAsync(name, args);
            else if (_graph.Handles(name)) result = await _graph.ExecuteAsync(name, args);
            else if (_pred.Handles(name)) result = await _pred.ExecuteAsync(name, args);
            else if (_recall.Handles(name)) result = await _recall.ExecuteAsync(name, args);
            else if (_memory.Handles(name)) result = await _memory.ExecuteAsync(name, args);
            else result = await _profile.ExecuteAsync(name, args);
            _trace.Add("analyst", "tool", name, $"params: {args}\nresult: {Truncate(result, 400)}");
            onToolCall?.Invoke(name, $"params: {args}\nresult: {result}");
            return result;
        }

        var summary = new System.Text.StringBuilder();
        await foreach (var tok in _providers.Active.StreamReplyAsync(Prompts.BulkImport + NowBlock(
            "This is HISTORICAL material being imported now - date entries and events from the " +
            "dates IN THE TEXT, never from the import time; use this clock only to resolve " +
            "relative references the text itself makes."), input, specs, Exec, effort: "medium",
            onError: e => _trace.Add("analyst", "error", "provider", e), ct: ct))
            summary.Append(tok);
        var final = summary.ToString().Trim();
        _trace.Add("analyst", "summary", "import", final);
        return final;
    }

    /// <summary>Cheap low-effort pre-pass: count analysis-worthy facts + a one-line gist (for progress).</summary>
    public async Task<(int Count, string About)> CountFactsAsync(string text, CancellationToken ct = default)
    {
        var sb = new System.Text.StringBuilder();
        await foreach (var tok in _providers.Active.StreamReplyAsync(Prompts.FactCount,
            new[] { new ChatMessage("user", text) }, effort: "low", ct: ct))
            sb.Append(tok);
        try
        {
            var s = sb.ToString();
            var i = s.IndexOf('{'); var j = s.LastIndexOf('}');
            if (i >= 0 && j > i)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(s[i..(j + 1)]);
                var root = doc.RootElement;
                var count = root.TryGetProperty("count", out var c) && c.TryGetInt32(out var cv) ? cv : 1;
                var about = root.TryGetProperty("about", out var a) ? (a.GetString() ?? "") : "";
                return (Math.Max(1, count), about);
            }
        }
        catch (Exception e) { DevTrap.Report("fact-count-parse", e); }   // malformed model JSON → default
        return (1, "");
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    /// <summary>Current-time block for analyst prompts. A journal's analysis is worthless
    /// without a clock: every relative reference must resolve to an absolute date.</summary>
    private static string NowBlock(string framing) =>
        $"\n\n## Now\nCurrent local time: {DateTime.Now:dddd, MMM d, yyyy h:mm tt} " +
        $"({DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC). {framing}";
}
