using System.Text;
using System.Text.Json;

namespace SemanticPortrait.Core;

/// <summary>
/// Retrieval-time context assembly: one call returns either a pinpoint fact or a dense
/// interconnected bundle, built by JOINING the stores (messages ↔ entry_meta ↔ events ↔ notes ↔
/// graph) at query time — never by dumping stores into the prompt.
///
/// The token budget is the contract: the whole bundle is hard-capped (<see cref="MaxBundleChars"/>,
/// ~2K tokens), sections are built in priority order into the remaining budget, and anything
/// dropped is SAID inline ("+N more — narrow the range") so the model knows the picture is
/// truncated rather than complete. Fuzzy name joins carry their provenance (via EntityResolver)
/// under the same stated-vs-inferred discipline as the rest of the system.
///
/// Lanes: RecallAsync includes RAW chat messages → main agent only. PortraitAsync reads only
/// analyst-authored stores (graph, notes, events, entry_meta summaries) → safe for the clean-room
/// analyst too.
/// </summary>
public sealed class RecallEngine
{
    private readonly Db _db;
    private readonly IEmbedder _embedder;
    private readonly EntityResolver _resolver;

    public RecallEngine(Db db, IEmbedder embedder, EntityResolver resolver)
    {
        _db = db; _embedder = embedder; _resolver = resolver;
    }

    /// <summary>Whole-bundle hard cap, in chars (~4 chars/token → ~2K tokens).</summary>
    public const int MaxBundleChars = 8000;

    // Per-item trims. Notes stay longer than raw entries: they're curated insight.
    private const int EntryTrim = 280;
    private const int NoteTrim = 400;
    private const int ExchangeTurnTrim = 200;

    // ---------------------------------------------------------------- recall
    /// <summary>
    /// Granular → interconnected in one call. Semantic hits enriched with mood + surrounding
    /// exchange, plus (opt-in via args) a person section and a timeline/trend window.
    /// </summary>
    public async Task<string> RecallAsync(string query, string? person = null,
        string? fromIso = null, string? toIso = null, int k = 5, CancellationToken ct = default)
    {
        k = Math.Clamp(k, 1, 12);
        var b = new Budget(MaxBundleChars);

        var vec = await _embedder.EmbedAsync(query, ct);
        if (vec is null) return "error: could not embed the query.";

        // 1) Semantic hits over messages + notes (note-biased), enriched inline.
        var hits = _db.Search(vec, k);
        if (hits.Count == 0 && person is null && fromIso is null)
            return "(nothing stored yet)";

        b.Section("## Matches");
        var exchangesShown = 0;
        foreach (var h in hits)
        {
            if (h.RefType == "note")
            {
                b.Line($"- [note #{h.RefId} · {Day(h.CreatedUtc)} · sim {h.Score:0.00}] {Trim(h.Text, NoteTrim)}");
                continue;
            }
            // Raw entry: attach the contemporaneous state so the hit arrives with its mood.
            var meta = _db.GetEntryMeta(h.RefId);
            var moodTag = meta is null ? "" : $" [mood: {meta.Mood} · v {meta.Valence:+0.0;-0.0} · e {meta.Energy:0.0}]";
            b.Line($"- [entry · {Day(h.CreatedUtc)} · sim {h.Score:0.00}]{moodTag} {Trim(h.Text, EntryTrim)}");

            // Top hits get their surrounding exchange — "that night" arrives with what that night was.
            if (exchangesShown < 2)
            {
                var ex = _db.GetExchange(h.RefId, radius: 1);
                if (ex.Count > 1)
                {
                    exchangesShown++;
                    foreach (var m in ex.Where(m => m.Id != h.RefId))
                        b.Line($"    · {(m.Role == "user" ? "user" : "analyst")}: {Trim(m.Text, ExchangeTurnTrim)}");
                }
            }
        }

        // 2) Person section (explicit ask only — deriving one from hits invites wrong joins).
        if (!string.IsNullOrWhiteSpace(person))
            await AppendPersonSectionAsync(b, person!, ct);

        // 3) Timeline window: events + a mood trend, when a date range was given.
        if (fromIso is not null || toIso is not null)
        {
            var from = NormalizeIso(fromIso, DateTime.UtcNow.AddYears(-10));
            var to = NormalizeIso(toIso, DateTime.UtcNow.AddDays(1));
            AppendTimelineSection(b, from, to);
        }
        else
        {
            // No range: still surface semantically-related events (timeline questions rarely
            // come with dates attached).
            var evHits = _db.SearchEvents(vec, 3).Where(e => e.Score >= 0.30).ToList();
            if (evHits.Count > 0)
            {
                b.Section("## Possibly related events");
                foreach (var (ev, score) in evHits)
                    b.Line($"- {Day(ev.EventUtc)}: {Trim(ev.Summary, 160)} (sim {score:0.00})");
            }
        }

        return b.Render("(no matching context)");
    }

    // -------------------------------------------------------------- portrait
    /// <summary>
    /// The interconnected view of a person/theme/node — or "overview" for the whole self-model.
    /// Analyst-authored stores only (no raw chat): clean-room safe.
    /// </summary>
    public async Task<string> PortraitAsync(string focus, CancellationToken ct = default)
    {
        var b = new Budget(MaxBundleChars);

        if (string.IsNullOrWhiteSpace(focus) || focus.Trim().Equals("overview", StringComparison.OrdinalIgnoreCase))
        {
            AppendOverview(b);
            return b.Render("(the self-model is empty — nothing recorded yet)");
        }

        var r = await _resolver.ResolveAsync(focus, ct);
        if (!r.IsResolved && r.Nodes.Count == 0)
        {
            // Don't force a bad join — but hand the model the closest labels so it can re-ask.
            var vec = await _embedder.EmbedAsync(focus, ct);
            var near = vec is null ? new() : _db.SearchNodes(vec, 3);
            var hint = near.Count > 0
                ? " Closest labels: " + string.Join(", ", near.Select(n => $"{n.Node.Category}/{n.Node.Label} ({n.Score:0.00})"))
                : "";
            return $"(no '{focus}' in the self-model.{hint})";
        }

        b.Section($"## {r.Provenance}");
        foreach (var node in r.Nodes)
        {
            var hood = _db.GetNeighborhood(node.Id, limit: 12);
            if (hood.Count == 0) { b.Line($"[{node.Category}/{node.Label}] no connections yet"); continue; }
            b.Line($"[{node.Category}/{node.Label}{(node.Inferred ? " · inferred" : "")} · conf {node.Confidence:0.0}]");
            foreach (var e in hood)
                b.Line(e.Outgoing
                    ? $"  {node.Label} —{e.Type}→ {e.Peer.Category}/{e.Peer.Label}{Conf(e)}"
                    : $"  {e.Peer.Category}/{e.Peer.Label} —{e.Type}→ {node.Label}{Conf(e)}");
        }

        // Notes + events + recorded states that mention any variant of the name.
        var variants = _resolver.Variants(r.Canonical);

        var notes = variants.SelectMany(v => _db.FindNotes(v, 5))
            .DistinctBy(n => n.Id).OrderByDescending(n => n.UpdatedUtc).ToList();
        if (notes.Count > 0)
        {
            b.Section("## Notes");
            foreach (var n in notes.Take(5))
                b.Line($"- [#{n.Id} · {Day(n.UpdatedUtc)}] {Trim(n.Text, NoteTrim)}");
            b.More(notes.Count - 5, "notes — search_past_analysis for the rest");
        }

        var events = _db.GetEvents()
            .Where(e => variants.Any(v => e.Summary.Contains(v, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(e => e.EventUtc).ToList();
        if (events.Count > 0)
        {
            b.Section("## Timeline");
            foreach (var e in events.Take(6))
                b.Line($"- {Day(e.EventUtc)}: {Trim(e.Summary, 160)}");
            b.More(events.Count - 6, "earlier events");
        }

        var meta = variants.SelectMany(v => _db.FindEntryMetaMentioning(v, 6))
            .DistinctBy(m => m.MessageId).OrderByDescending(m => m.EntryUtc).ToList();
        if (meta.Count > 0)
        {
            b.Section("## Recorded states when they came up");
            foreach (var m in meta.Take(4))
                b.Line($"- {Day(m.EntryUtc)}: {m.Mood} (v {m.Valence:+0.0;-0.0}, e {m.Energy:0.0}) — {Trim(m.Summary, 120)}");
            b.More(meta.Count - 4, "more states");
        }

        return b.Render("(nothing recorded about this yet)");
    }

    // ---------------------------------------------------------------- narration
    /// <summary>
    /// A node's story in PLAIN LANGUAGE for the Observatory inspector — what this is in their
    /// life, how it feels lately, what it's tangled with, and what the analysis has said.
    /// Deterministic composition from stored data (no model call): instant, offline, testable.
    /// </summary>
    public async Task<string> NarrateNodeAsync(long nodeId, DateTime nowUtc = default)
    {
        if (nowUtc == default) nowUtc = DateTime.UtcNow;
        if (nodeId == Constellation.Encoding.SyntheticCoreId) return CoreThesis(nowUtc);
        var node = _db.GetNode(nodeId);
        if (node is null) return "";
        if (node.Category == "core") return CoreThesis(nowUtc);   // the center speaks its thesis
        var sb = new StringBuilder();

        var variants = _resolver.Variants(node.Label);
        var meta = variants.SelectMany(v => _db.FindEntryMetaMentioning(v, 12))
            .DistinctBy(m => m.MessageId).OrderByDescending(m => m.EntryUtc).ToList();

        // --- THE THESIS LEADS: what this IS in their life — the analysis's distilled read,
        // or failing that the entries' own summaries. Frequency/feeling talk comes AFTER the
        // substance; metadata lives in the folded "how this is drawn", not here.
        var note = variants.SelectMany(v => _db.FindNotes(v, 3))
            .DistinctBy(n => n.Id).OrderByDescending(n => n.UpdatedUtc).FirstOrDefault();
        if (note is not null)
            sb.AppendLine($"From the analysis: “{Trim(note.Text, 300)}”");
        else if (meta.Count > 0)
        {
            var gists = meta.Select(m => m.Summary)
                .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().Take(2).ToList();
            if (gists.Count > 0)
                sb.AppendLine($"What this has been about: {Trim(string.Join(" · ", gists), 280)}");
        }

        // --- presence + current feeling -------------------------------------------------
        if (meta.Count == 0)
        {
            sb.AppendLine(node.Inferred
                ? "I inferred this from the shape of what you've written — you haven't named it directly yet. If it doesn't ring true, reject it."
                : "This is on your map, but you haven't written about it directly yet — it shows up only through its connections.");
        }
        else
        {
            var latest = meta[0];
            var freq = meta.Count switch { 1 => "once", <= 3 => "a few times", <= 8 => "regularly", _ => "a lot" };
            var days = (nowUtc - Compactor.ParseUtc(latest.EntryUtc)).TotalDays;
            var when = days < 1.5 ? "as recently as today" : days < 8 ? "within the last week" : $"last about {(int)days} days ago";
            var recentVal = meta.Take(4).Average(m => m.Valence);
            var recentEnergy = meta.Take(4).Average(m => m.Energy);
            var feel = recentVal <= -0.4 ? "it tends to pull you down"
                : recentVal < 0.05 ? "it sits on the heavier side for you"
                : recentVal < 0.4 ? "it leans gently positive"
                : "it genuinely lifts you";
            var energy = recentEnergy < 0.3 ? " and leaves you drained" : recentEnergy > 0.7 ? " and gets you activated" : "";
            sb.AppendLine().AppendLine($"You've written about this {freq}, {when}. Lately {feel}{energy} — most recently you were feeling {latest.Mood}.");
        }

        // --- the web it lives in ---------------------------------------------------------
        var hood = _db.GetNeighborhood(nodeId, 5);
        if (hood.Count > 0)
        {
            var links = hood.Take(3).Select(e =>
            {
                var verb = e.Type.Replace('-', ' ');
                var tag = e.Inferred ? " (my read)" : "";
                return e.Outgoing ? $"it {verb} {e.Peer.Label}{tag}" : $"{e.Peer.Label} {verb} it{tag}";
            });
            sb.AppendLine().AppendLine($"In your constellation, {string.Join("; ", links)}.");
        }

        // --- honest flags ------------------------------------------------------------------
        var flags = new List<string>();
        var vols = meta.Count >= 5 ? StdDev(meta.Select(m => m.Valence)) : 0;
        var posCount = meta.Count(m => m.Valence > 0.15);
        var negCount = meta.Count(m => m.Valence < -0.15);
        if (posCount > 0 && negCount > 0) flags.Add("it carries both light and weight for you — both are real");
        if (vols > 0.4) flags.Add("how it feels swings widely from entry to entry");
        if (node.Inferred) flags.Add("parts of this are my inference, not your words — push back where I've over-read");
        if (flags.Count > 0)
            sb.AppendLine().AppendLine($"Worth knowing: {string.Join("; ", flags)}.");

        return sb.ToString().Trim();

        static double StdDev(IEnumerable<double> xs)
        {
            var a = xs.ToArray();
            if (a.Length < 2) return 0;
            var m = a.Average();
            return Math.Sqrt(a.Sum(x => (x - m) * (x - m)) / a.Length);
        }
    }

    /// <summary>
    /// The core Od's story: the THESIS of this person — who they are, what's alive, what lifts
    /// and what weighs — synthesized deterministically from profile + recent entries + notes.
    /// This is the one place the map speaks about the whole, not a part.
    /// </summary>
    private string CoreThesis(DateTime nowUtc)
    {
        var sb = new StringBuilder();

        // WHO — the profile's own words. Keys are model-written and often date-suffixed
        // ("career_north_star_2026_06"), so match by STEM and prefer the latest (suffixes sort).
        var prof = _db.AllProfileFields();
        string? P(params string[] stems) => stems
            .Select(stem => prof
                .Where(kv => kv.Key.Contains(stem, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => kv.Value)
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)))
            .FirstOrDefault(v => v is not null);
        var name = P("name");
        var who = new[]
            {
                P("builder_identity", "identity", "occupation", "craft", "role"),
                P("north_star", "meaning", "core_values", "values"),
                P("current_arc", "situation", "season"),
            }
            .Where(v => v is not null).Select(v => Trim(v!.TrimEnd('.'), 160)).ToList();
        sb.AppendLine(who.Count > 0
            ? $"{name ?? "You"} — {string.Join("; ", who)}."
            : $"{name ?? "You"} — the center this map orbits.");

        // WHAT'S ALIVE + WHAT LIFTS / WHAT WEIGHS — recency-weighted themes from the entries.
        var entries = _db.GetAllEntryMeta()
            .OrderByDescending(m => m.EntryUtc).Take(60).ToList();
        if (entries.Count > 0)
        {
            var themes = new Dictionary<string, (double W, double Val, int N)>(StringComparer.OrdinalIgnoreCase);
            var people = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries)
            {
                var age = Math.Max(0, (nowUtc - Compactor.ParseUtc(e.EntryUtc)).TotalDays);
                var w = Math.Pow(0.5, age / 14.0);
                foreach (var t in Constellation.ConstellationMetrics.ParseRefs(e.Topics))
                {
                    var cur = themes.GetValueOrDefault(t);
                    themes[t] = (cur.W + w, cur.Val + e.Valence * w, cur.N + 1);
                }
                foreach (var p in Constellation.ConstellationMetrics.ParseRefs(e.People))
                    people[p] = people.GetValueOrDefault(p) + w;
            }
            var alive = themes.OrderByDescending(t => t.Value.W).Take(3).Select(t => t.Key).ToList();
            // Cryptic short tags ("si") must not carry the lifts/weighs sentence — the heavy
            // subjects speak through the notes and entries, not through a two-letter code.
            var recurring = themes.Where(t => t.Value.N >= 2 && t.Value.W > 0 && t.Key.Length >= 3).ToList();
            var lifts = recurring.Where(t => t.Value.Val / t.Value.W > 0.15)
                .OrderByDescending(t => t.Value.Val / t.Value.W).FirstOrDefault().Key;
            var weighs = recurring.Where(t => t.Value.Val / t.Value.W < -0.15)
                .OrderBy(t => t.Value.Val / t.Value.W).FirstOrDefault().Key;
            var whoElse = people.OrderByDescending(p => p.Value).Take(2).Select(p => p.Key).ToList();

            if (alive.Count > 0)
                sb.AppendLine().AppendLine($"What's most alive right now: {string.Join(", ", alive)}" +
                    (whoElse.Count > 0 ? $" — with {string.Join(" and ", whoElse)} at the center of the human side." : "."));
            if (lifts is not null || weighs is not null)
                sb.AppendLine((lifts is not null ? $"What lifts you: {lifts}. " : "") +
                              (weighs is not null ? $"What weighs: {weighs}." : ""));
        }

        // THE CLEAREST READ — the analysis's most recent SUBSTANTIVE distillation, verbatim
        // (housekeeping notes like "correction: ..." are true but not a thesis).
        var allNotes = _db.GetNotes().OrderByDescending(n => n.UpdatedUtc).ToList();
        var note = allNotes.FirstOrDefault(n =>
                !n.Text.StartsWith("correction", StringComparison.OrdinalIgnoreCase) &&
                !n.Text.StartsWith("meta", StringComparison.OrdinalIgnoreCase))
            ?? allNotes.FirstOrDefault();
        if (note is not null)
            sb.AppendLine().AppendLine($"The analysis's clearest current read: “{Trim(note.Text, 300)}”");

        if (entries.Count == 0 && note is null)
            sb.AppendLine().AppendLine("Nothing written yet — this center sharpens as you write.");

        return sb.ToString().Trim();
    }

    /// <summary>Existing node labels grouped by category — lets the analyst reuse labels instead
    /// of duplicating, and register_alias against the real canonical set.</summary>
    public string ListNodeLabels(string? category = null)
    {
        var nodes = _db.GetNodes();
        if (category is not null)
            nodes = nodes.Where(n => n.Category.Equals(category.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (nodes.Count == 0) return "(no nodes yet)";
        var b = new Budget(MaxBundleChars / 2);
        foreach (var g in nodes.GroupBy(n => n.Category).OrderBy(g => g.Key))
            b.Line($"{g.Key}: {string.Join(", ", g.Select(n => n.Label).OrderBy(l => l))}");
        return b.Render("(no nodes yet)");
    }

    // ---------------------------------------------------------------- pieces
    private async Task AppendPersonSectionAsync(Budget b, string person, CancellationToken ct)
    {
        var r = await _resolver.ResolveAsync(person, ct);
        b.Section($"## {r.Provenance}");
        if (!r.IsResolved && r.Nodes.Count == 0)
        {
            b.Line("(not in the self-model yet)");
            return;
        }

        foreach (var node in r.Nodes)
            foreach (var e in _db.GetNeighborhood(node.Id, limit: 8))
                b.Line(e.Outgoing
                    ? $"- {node.Label} —{e.Type}→ {e.Peer.Category}/{e.Peer.Label}{Conf(e)}"
                    : $"- {e.Peer.Category}/{e.Peer.Label} —{e.Type}→ {node.Label}{Conf(e)}");

        var variants = _resolver.Variants(r.Canonical);
        var notes = variants.SelectMany(v => _db.FindNotes(v, 4)).DistinctBy(n => n.Id).ToList();
        foreach (var n in notes.Take(4))
            b.Line($"- [note #{n.Id} · {Day(n.UpdatedUtc)}] {Trim(n.Text, NoteTrim)}");
        b.More(notes.Count - 4, "notes");

        // The person's recorded states ride along too — mood context for a person question
        // shouldn't depend on the raw entry winning a top-k slot against the notes about it.
        var meta = variants.SelectMany(v => _db.FindEntryMetaMentioning(v, 4))
            .DistinctBy(m => m.MessageId).OrderByDescending(m => m.EntryUtc).ToList();
        foreach (var m in meta.Take(3))
            b.Line($"- {Day(m.EntryUtc)}: {m.Mood} (v {m.Valence:+0.0;-0.0}) — {Trim(m.Summary, 120)}");
        b.More(meta.Count - 3, "recorded states");
    }

    private void AppendTimelineSection(Budget b, string fromIso, string toIso)
    {
        var events = _db.GetEventsRange(fromIso, toIso);
        if (events.Count > 0)
        {
            b.Section("## Events in range");
            foreach (var e in events.Take(8))
                b.Line($"- {Day(e.EventUtc)}: {Trim(e.Summary, 160)}");
            b.More(events.Count - 8, "events — narrow the range");
        }

        var meta = _db.GetEntryMetaRange(fromIso, toIso);
        if (meta.Count == 0) return;
        b.Section("## Mood trend (weekly)");
        foreach (var wk in meta
            .GroupBy(m => Iso(m.EntryUtc).AddDays(-(int)Iso(m.EntryUtc).DayOfWeek))
            .OrderBy(g => g.Key)
            .Take(10))
            b.Line($"- wk of {wk.Key:MMM d}: v {wk.Average(m => m.Valence):+0.00;-0.00}, " +
                   $"e {wk.Average(m => m.Energy):0.00}, {wk.Count()} entr{(wk.Count() == 1 ? "y" : "ies")} " +
                   $"({string.Join(", ", wk.Select(m => m.Mood).Distinct().Take(4))})");
    }

    private void AppendOverview(Budget b)
    {
        var nodes = _db.GetNodes();
        var edges = _db.GetEdges();
        if (nodes.Count == 0) return;

        b.Section($"## Self-model overview — {nodes.Count} nodes, {edges.Count} threads");
        var degree = edges.SelectMany(e => new[] { e.Src, e.Dst })
            .GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
        foreach (var g in nodes.GroupBy(n => n.Category).OrderByDescending(g => g.Count()))
        {
            var top = g.OrderByDescending(n => degree.GetValueOrDefault(n.Id)).Take(6)
                .Select(n => degree.GetValueOrDefault(n.Id) > 0 ? $"{n.Label} ({degree[n.Id]})" : n.Label);
            b.Line($"- {g.Key} ({g.Count()}): {string.Join(", ", top)}{(g.Count() > 6 ? ", …" : "")}");
        }

        var hubs = nodes.Where(n => degree.GetValueOrDefault(n.Id) >= 2)
            .OrderByDescending(n => degree[n.Id]).Take(5).ToList();
        if (hubs.Count > 0)
        {
            b.Section("## Most connected");
            foreach (var h in hubs)
            {
                var lines = _db.GetNeighborhood(h.Id, limit: 4)
                    .Select(e => e.Outgoing ? $"—{e.Type}→ {e.Peer.Label}" : $"←{e.Type}— {e.Peer.Label}");
                b.Line($"- {h.Category}/{h.Label}: {string.Join("; ", lines)}");
            }
        }
    }

    // ---------------------------------------------------------------- budget
    /// <summary>
    /// Char-budgeted text assembly. Lines that don't fit are counted, not written, and surface as
    /// one honest "(+N more lines omitted — narrow the query)" at render time: silent truncation
    /// reads as "covered everything" when it didn't.
    /// </summary>
    private sealed class Budget
    {
        private readonly StringBuilder _sb = new();
        private readonly int _cap;
        private int _omitted;
        public Budget(int cap) => _cap = cap;

        public void Section(string header)
        {
            if (_sb.Length + header.Length + 2 > _cap) { _omitted++; return; }
            if (_sb.Length > 0) _sb.AppendLine();
            _sb.AppendLine(header);
        }

        public void Line(string line)
        {
            if (_sb.Length + line.Length + 1 > _cap) { _omitted++; return; }
            _sb.AppendLine(line);
        }

        /// <summary>Explicit per-section drop note (count of items a section chose not to show).</summary>
        public void More(int count, string what) { if (count > 0) Line($"  (+{count} more {what})"); }

        public string Render(string emptyFallback)
        {
            if (_omitted > 0) _sb.AppendLine($"(+{_omitted} more lines omitted — over budget; narrow the query)");
            var s = _sb.ToString().TrimEnd();
            return s.Length == 0 ? emptyFallback : s;
        }
    }

    // ---------------------------------------------------------------- helpers
    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";
    private static string Conf(NeighborEdge e) => e.Inferred ? $" (inferred {e.Confidence:0.0})" : "";
    private static DateTime Iso(string s) => Compactor.ParseUtc(s);
    // Weekday + LOCAL date: a journal's rhythms live in weekdays and evenings — "Tue 2026-07-01"
    // lets the agent see "Tuesday nights" as a pattern instead of opaque dates.
    private static string Day(string utc)
    {
        var d = Compactor.ParseUtc(utc);
        return d == DateTime.MinValue ? utc : d.ToLocalTime().ToString("ddd yyyy-MM-dd");
    }
    /// <summary>Model-supplied dates arrive in many shapes; parse loosely, fall back wide.</summary>
    private static string NormalizeIso(string? iso, DateTime fallbackUtc) =>
        !string.IsNullOrWhiteSpace(iso) && DateTime.TryParse(iso, out var d)
            ? d.ToUniversalTime().ToString("o") : fallbackUtc.ToString("o");
}
