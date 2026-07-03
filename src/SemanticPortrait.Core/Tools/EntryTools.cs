using System.Text.Json;

namespace SemanticPortrait.Core;

/// <summary>
/// record_entry_meta — captures contemporaneous metadata for a journal entry (mood, valence,
/// intensity, energy, topics, people, a one-line state summary). Validation is strict: EVERY
/// field is required and range-checked, and the call is REJECTED if anything is missing or out
/// of range. The schema forces complete, honest metadata — no lazy blanks. The entry's datetime
/// is taken from the stored message itself (not model-supplied), so it's always accurate.
/// </summary>
public sealed class EntryTools
{
    private readonly Db _db;
    private readonly IEmbedder _embedder;
    public EntryTools(Db db, IEmbedder embedder) { _db = db; _embedder = embedder; }

    // Duplicate-event bands, measured against real MiniLM cosines from the 2026-07-02 transcript:
    // same-happening retells scored 0.657–0.831 while a DIFFERENT happening in the same storyline
    // scored up to 0.689 — the bands overlap, so no single threshold separates them. Resolution
    // mirrors the intake counter's design: deterministic where certain, LLM judgment where not.
    //  ≥ AutoBlock → clearly the same event: rejected outright.
    //  ≥ GrayZone  → ambiguous: the tool returns BOTH texts and asks the analyst to judge
    //                (re-send with force=true if genuinely distinct).
    //  < GrayZone  → clearly new: logs silently.
    public const double EventDupAutoBlock = 0.82;
    public const double EventDupGrayZone = 0.60;

    public bool Handles(string name) => name is "record_entry_meta" or "log_event" or "import_entry";

    /// <summary>The embedder is required for import_entry (imported entries must be recallable);
    /// injected at construction like everything else.</summary>
    private static readonly object ImportEntrySpec = new
    {
        type = "function",
        name = "import_entry",
        description = "Reconstruct ONE journal entry from imported material into the user's real " +
                      "thread, dated when it was LIVED (not imported). Use the author's own words — " +
                      "quote/lightly stitch the source; never invent facts or feelings not in it. " +
                      "when accepts a full ISO date, 'YYYY-MM', or 'YYYY' (coarse dates land " +
                      "mid-period). ALL metadata fields are required and validated, same contract " +
                      "as record_entry_meta.",
        parameters = new
        {
            type = "object",
            properties = new
            {
                text = new { type = "string", description = "The entry, first person, faithful to the source's wording." },
                when = new { type = "string", description = "When it was lived: ISO date/datetime, 'YYYY-MM', or 'YYYY'." },
                mood = new { type = "string", description = "Mood label evidenced by the text." },
                valence = new { type = "number", description = "-1..1." },
                intensity = new { type = "number", description = "0..1." },
                energy = new { type = "number", description = "0..1." },
                topics = new { type = "array", items = new { type = "string" }, description = "1+ topics." },
                people = new { type = "array", items = new { type = "string" }, description = "People mentioned (empty array if none)." },
                summary = new { type = "string", description = "One-line state summary at that time." },
            },
            required = new[] { "text", "when", "mood", "valence", "intensity", "energy", "topics", "people", "summary" },
            additionalProperties = false,
        },
    };

    private static bool WithinDays(string aUtc, string bUtc, int days) =>
        DateTime.TryParse(aUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var a) &&
        DateTime.TryParse(bUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var b) &&
        Math.Abs((a.ToUniversalTime() - b.ToUniversalTime()).TotalDays) <= days;

    public IReadOnlyList<object> Specs => new object[]
    {
        new
        {
            type = "function",
            name = "record_entry_meta",
            description = "REQUIRED for every substantive entry: record the user's contemporaneous " +
                          "state so later analysis has the mood/context at that moment. ALL fields are " +
                          "mandatory and validated — the call is rejected if any are missing or out of " +
                          "range, so fill them all honestly. Datetime is captured automatically.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    message_id = new { type = "integer", description = "The id of the entry being annotated (given to you)." },
                    mood = new { type = "string", description = "Short mood label, e.g. anxious, content, numb, angry." },
                    valence = new { type = "number", description = "Pleasantness, -1 (very negative) .. 1 (very positive)." },
                    intensity = new { type = "number", description = "Strength of the feeling, 0 (flat) .. 1 (overwhelming)." },
                    energy = new { type = "number", description = "Activation/energy, 0 (depleted) .. 1 (wired)." },
                    topics = new { type = "array", items = new { type = "string" }, description = "1+ topics in the entry." },
                    people = new { type = "array", items = new { type = "string" }, description = "People mentioned (empty array if none)." },
                    summary = new { type = "string", description = "One-line summary of their state right now." },
                },
                required = new[] { "message_id", "mood", "valence", "intensity", "energy", "topics", "people", "summary" },
                additionalProperties = false,
            },
        },
        new
        {
            type = "function",
            name = "log_event",
            description = "Record a concrete life event with WHEN it happened (ISO date if known, " +
                          "else today). Use for datable happenings the user mentions — recounted or " +
                          "current — so later analysis has an accurate timeline. Don't log feelings " +
                          "here. One happening = ONE event: retellings are rejected or flagged as " +
                          "possible duplicates — judge those honestly and force=true only when the " +
                          "event is genuinely distinct.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    summary = new { type = "string", description = "What happened (one line)." },
                    when = new { type = "string", description = "ISO date/time it occurred; omit for now." },
                    force = new { type = "boolean", description = "Confirm a possible-duplicate flag was judged and this IS a distinct event." },
                },
                required = new[] { "summary" },
                additionalProperties = false,
            },
        },
    };

    /// <summary>Import-time toolset: reconstruct entries + log events. record_entry_meta is
    /// excluded — import_entry records the metadata atomically with the entry it creates.</summary>
    public IReadOnlyList<object> ImportSpecs => Specs
        .Where(s => s.GetType().GetProperty("name")?.GetValue(s) as string == "log_event")
        .Append(ImportEntrySpec).ToList();

    public async Task<string> ExecuteAsync(string name, string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var r = doc.RootElement;

            if (name == "log_event")
            {
                var evSummary = Str(r, "summary")?.Trim();
                if (string.IsNullOrWhiteSpace(evSummary)) return "error: 'summary' is required.";
                var when = Str(r, "when");
                var whenUtc = !string.IsNullOrWhiteSpace(when) && DateTime.TryParse(when, out var dt)
                    ? dt.ToUniversalTime().ToString("o") : DateTime.UtcNow.ToString("o");

                // Dedup gate: the same happening retold across turns produced 4 records of one
                // text-thread review in real transcripts. Date window first — annual recurrences
                // (similar text, distant date) always log. force=true = the model already judged
                // a gray-zone flag and confirmed this is a distinct happening.
                var force = r.TryGetProperty("force", out var ff) && ff.ValueKind == JsonValueKind.True;
                var vec = await _embedder.EmbedAsync(evSummary);
                if (vec is not null && !force)
                {
                    var top = _db.SearchEvents(vec, 1).FirstOrDefault();
                    if (top.Event is not null && WithinDays(top.Event.EventUtc, whenUtc, 7))
                    {
                        if (top.Score >= EventDupAutoBlock)
                            return $"already logged as event #{top.Event.Id} ({top.Event.EventUtc[..10]}): " +
                                   $"\"{top.Event.Summary}\" — not duplicated. log_event is for genuinely NEW happenings.";
                        if (top.Score >= EventDupGrayZone)
                            return $"possible duplicate of event #{top.Event.Id} ({top.Event.EventUtc[..10]}): " +
                                   $"\"{top.Event.Summary}\". If your new summary describes the SAME happening " +
                                   "retold, do NOT re-log it. Only if it is genuinely a different event, resend " +
                                   "with force=true.";
                    }
                }

                var eid = _db.AddEvent(whenUtc, evSummary);
                // Embed the summary so the timeline is semantically searchable (best-effort —
                // a failed embed means this event is date-range/substring findable only).
                if (vec is not null) _db.UpsertEmbedding("event", eid, vec);
                return $"logged event #{eid}";
            }

            if (name == "import_entry")
            {
                var text = Str(r, "text");
                var whenRaw = Str(r, "when");
                if (string.IsNullOrWhiteSpace(text)) return "error: 'text' is required.";
                var whenUtc = NormalizeLivedDate(whenRaw);
                if (whenUtc is null) return "error: 'when' must be an ISO date, 'YYYY-MM', or 'YYYY'.";
                var (meta, err) = ParseMeta(r);
                if (err is not null) return err;

                // The entry lands in the REAL thread, dated when it was lived — recall, the time
                // scrub, and the constellation treat imported life exactly like lived history.
                var id = _db.AddMessage("user", text!.Trim(), whenUtc);
                var vec = await _embedder.EmbedAsync(text.Trim());
                if (vec is not null) _db.AddEmbedding("message", id, vec);
                _db.SetEntryMeta(id, meta.Mood, meta.Valence, meta.Intensity, meta.Energy,
                    meta.TopicsJson, meta.PeopleJson, meta.Summary);
                return $"imported entry #{id} dated {whenUtc[..10]} (mood: {meta.Mood})";
            }

            if (name != "record_entry_meta") return $"error: unknown tool '{name}'";

            long messageId = 0;
            var hasId = r.TryGetProperty("message_id", out var mid) && mid.TryGetInt64(out messageId);
            var (m2, err2) = ParseMeta(r, requireMessageId: !hasId);
            if (err2 is not null) return err2;

            _db.SetEntryMeta(messageId, m2.Mood, m2.Valence, m2.Intensity, m2.Energy,
                m2.TopicsJson, m2.PeopleJson, m2.Summary);

            return $"recorded metadata for entry #{messageId} (mood: {m2.Mood})";
        }
        catch (Exception ex)
        {
            return $"error: {ex.Message}";
        }
    }

    private readonly record struct ParsedMeta(
        string Mood, double Valence, double Intensity, double Energy,
        string TopicsJson, string PeopleJson, string Summary);

    /// <summary>The shared strict-validation contract for entry metadata (record_entry_meta and
    /// import_entry) — every field required, range-checked, same error wording.</summary>
    private static (ParsedMeta Meta, string? Error) ParseMeta(JsonElement r, bool requireMessageId = false)
    {
        var missing = new List<string>();
        if (requireMessageId) missing.Add("message_id");

        string? mood = Str(r, "mood"); if (string.IsNullOrWhiteSpace(mood)) missing.Add("mood");
        string? summary = Str(r, "summary"); if (string.IsNullOrWhiteSpace(summary)) missing.Add("summary");
        double? valence = Num(r, "valence"); if (valence is null) missing.Add("valence");
        double? intensity = Num(r, "intensity"); if (intensity is null) missing.Add("intensity");
        double? energy = Num(r, "energy"); if (energy is null) missing.Add("energy");
        var topics = Arr(r, "topics");
        if (topics is null) missing.Add("topics");
        else if (topics.Count == 0) missing.Add("topics (need at least one)");
        var people = Arr(r, "people");          // may be empty, but the field must be present
        if (people is null) missing.Add("people");

        if (missing.Count > 0)
            return (default, $"error: incomplete — fill these and resend: {string.Join(", ", missing)}");
        if (valence is < -1 or > 1) return (default, "error: valence must be between -1 and 1.");
        if (intensity is < 0 or > 1) return (default, "error: intensity must be between 0 and 1.");
        if (energy is < 0 or > 1) return (default, "error: energy must be between 0 and 1.");

        return (new ParsedMeta(mood!.Trim(), valence!.Value, intensity!.Value, energy!.Value,
            JsonSerializer.Serialize(topics), JsonSerializer.Serialize(people), summary!.Trim()), null);
    }

    /// <summary>Lived-date normalization for imports: full ISO passes through; 'YYYY-MM' lands on
    /// the 15th, 'YYYY' at mid-year — coarse memories get honest mid-period timestamps rather
    /// than a false precision or a rejection.</summary>
    internal static string? NormalizeLivedDate(string? when)
    {
        if (string.IsNullOrWhiteSpace(when)) return null;
        var s = when.Trim();
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^\d{4}$") && int.TryParse(s, out var y))
            return new DateTime(y, 7, 1, 12, 0, 0, DateTimeKind.Utc).ToString("o");
        var ym = System.Text.RegularExpressions.Regex.Match(s, @"^(\d{4})-(\d{1,2})$");
        if (ym.Success && int.TryParse(ym.Groups[1].Value, out var yy) && int.TryParse(ym.Groups[2].Value, out var mm) && mm is >= 1 and <= 12)
            return new DateTime(yy, mm, 15, 12, 0, 0, DateTimeKind.Utc).ToString("o");
        if (DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d))
            return d.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(d, DateTimeKind.Utc).ToString("o")
                : d.ToUniversalTime().ToString("o");
        return null;
    }

    private static string? Str(JsonElement r, string k) =>
        r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static double? Num(JsonElement r, string k) =>
        r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;
    private static List<string>? Arr(JsonElement r, string k)
    {
        if (!r.TryGetProperty(k, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        var list = new List<string>();
        foreach (var e in v.EnumerateArray())
            if (e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()))
                list.Add(e.GetString()!.Trim());
        return list;
    }
}
