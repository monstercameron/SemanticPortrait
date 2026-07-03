using System.Text;
using System.Text.Json;

namespace SemanticPortrait.Core;

/// <summary>
/// Full local export — your data is yours. Formats: JSON (everything), Markdown (readable),
/// CSV (entries), Mermaid .mmd + GraphML (the graph). All formats support an optional UTC date
/// range and an optional mask function (forced pseudonymization for a SHAREABLE variant —
/// independent of the user's egress-masking setting). Exports are PLAINTEXT on disk; the
/// encrypted-backup path is <see cref="Db.BackupTo"/>.
/// </summary>
public sealed class ExportService
{
    private readonly Db _db;
    private readonly ProfileStore _profile;

    public ExportService(Db db, ProfileStore profile) { _db = db; _profile = profile; }

    private static bool InRange(string utc, DateTime? from, DateTime? to)
    {
        if (from is null && to is null) return true;
        if (!DateTime.TryParse(utc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d)) return true;
        d = d.ToUniversalTime();
        return (from is null || d >= from) && (to is null || d < to.Value.AddDays(1));
    }

    public string ToJson(DateTime? from = null, DateTime? to = null, Func<string, string>? mask = null)
    {
        var isMasked = mask is not null;
        mask ??= s => s;
        var payload = new
        {
            exported_utc = DateTime.UtcNow.ToString("o"),
            app = "SemanticPortrait",
            schema = 2,
            masked = isMasked,
            range = new { from = from?.ToString("o"), to = to?.ToString("o") },
            messages = _db.GetMessages().Where(m => InRange(m.CreatedUtc, from, to))
                .Select(m => new { m.Id, m.Role, Text = mask(m.Text), m.CreatedUtc }),
            events = _db.GetEvents().Where(e => InRange(e.EventUtc, from, to))
                .Select(e => new { e.Id, e.EventUtc, Summary = mask(e.Summary) }),
            entry_meta = _db.GetAllEntryMeta().Where(e => InRange(e.EntryUtc, from, to)),
            notes = _db.GetNotes().Select(n => new { n.Id, Text = mask(n.Text), n.CreatedUtc, n.UpdatedUtc }),
            nodes = _db.GetNodes().Select(n => new { n.Id, n.Category, Label = mask(n.Label), n.Inferred, n.Confidence }),
            edges = _db.GetEdges(),
            predictions = _db.GetPredictions().Select(p => new
            {
                p.Id, p.CreatedUtc, Claim = mask(p.Claim), Criterion = mask(p.Criterion),
                p.DueUtc, p.ResolvedUtc, Outcome = p.Outcome is null ? null : mask(p.Outcome), p.Score,
            }),
            metrics = _db.GetMetricsSnapshots(500).Select(m => new { utc = m.Utc, payload = m.Payload }),
            profile = _profile.All().ToDictionary(kv => kv.Key, kv => mask(kv.Value)),
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    public string ToMarkdown(DateTime? from = null, DateTime? to = null, Func<string, string>? mask = null)
    {
        mask ??= s => s;
        var sb = new StringBuilder();
        sb.AppendLine("# SemanticPortrait export");
        sb.AppendLine($"*Exported {DateTime.Now:dddd, MMM d yyyy h:mm tt}*\n");

        sb.AppendLine("## Thread\n");
        foreach (var m in _db.GetMessages().Where(m => InRange(m.CreatedUtc, from, to)))
        {
            if (m.Role is not ("user" or "assistant")) continue;
            sb.AppendLine($"**{(m.Role == "user" ? "You" : "Analyst")}** · {m.CreatedUtc}\n\n{mask(m.Text)}\n");
        }

        var events = _db.GetEvents().Where(e => InRange(e.EventUtc, from, to)).ToList();
        if (events.Count > 0)
        {
            sb.AppendLine("## Events\n");
            foreach (var e in events) sb.AppendLine($"- {e.EventUtc[..Math.Min(10, e.EventUtc.Length)]} — {mask(e.Summary)}");
            sb.AppendLine();
        }

        var notes = _db.GetNotes();
        if (notes.Count > 0)
        {
            sb.AppendLine("## Notes\n");
            foreach (var n in notes) sb.AppendLine($"- {mask(n.Text)}");
            sb.AppendLine();
        }

        var preds = _db.GetPredictions();
        if (preds.Count > 0)
        {
            sb.AppendLine("## Predictions\n");
            foreach (var p in preds)
            {
                var status = p.ResolvedUtc is null ? "open"
                    : $"resolved: {mask(p.Outcome ?? "")} ({((p.Score ?? 0) * 100):0}%)";
                sb.AppendLine($"- {mask(p.Claim)} — *{mask(p.Criterion)}* — {status}");
            }
        }
        return sb.ToString();
    }

    /// <summary>Entries (user + assistant messages) as CSV: created_utc, role, text.</summary>
    public string ToCsv(DateTime? from = null, DateTime? to = null, Func<string, string>? mask = null)
    {
        mask ??= s => s;
        var sb = new StringBuilder();
        sb.AppendLine("created_utc,role,text");
        foreach (var m in _db.GetMessages().Where(m => InRange(m.CreatedUtc, from, to)))
        {
            if (m.Role is not ("user" or "assistant")) continue;
            sb.AppendLine($"{Csv(m.CreatedUtc)},{Csv(m.Role)},{Csv(mask(m.Text))}");
        }
        return sb.ToString();
    }

    /// <summary>The self-model graph as a Mermaid flowchart (.mmd) — same family as the seed mindmap.</summary>
    public string ToMermaid(Func<string, string>? mask = null)
    {
        mask ??= s => s;
        var sb = new StringBuilder();
        sb.AppendLine("graph TD");
        var nodes = _db.GetNodes();
        foreach (var n in nodes)
            sb.AppendLine($"    n{n.Id}[\"{Mermaid(mask(n.Label))}\"]:::{San(n.Category)}");
        foreach (var e in _db.GetEdges())
            sb.AppendLine($"    n{e.Src} -->|{Mermaid(e.Type)}| n{e.Dst}");
        foreach (var cat in nodes.Select(n => n.Category).Distinct().OrderBy(c => c))
            sb.AppendLine($"    classDef {San(cat)} stroke-width:2px;");
        return sb.ToString();
    }

    /// <summary>The self-model graph as GraphML (imports into Gephi / yEd / Cytoscape).</summary>
    public string ToGraphML(Func<string, string>? mask = null)
    {
        mask ??= s => s;
        var sb = new StringBuilder();
        sb.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        sb.AppendLine("""<graphml xmlns="http://graphml.graphdrawing.org/xmlns">""");
        sb.AppendLine("""  <key id="label" for="node" attr.name="label" attr.type="string"/>""");
        sb.AppendLine("""  <key id="category" for="node" attr.name="category" attr.type="string"/>""");
        sb.AppendLine("""  <key id="confidence" for="node" attr.name="confidence" attr.type="double"/>""");
        sb.AppendLine("""  <key id="inferred" for="node" attr.name="inferred" attr.type="boolean"/>""");
        sb.AppendLine("""  <key id="type" for="edge" attr.name="type" attr.type="string"/>""");
        sb.AppendLine("""  <graph id="portrait" edgedefault="directed">""");
        foreach (var n in _db.GetNodes())
        {
            sb.AppendLine($"""    <node id="n{n.Id}">""");
            sb.AppendLine($"""      <data key="label">{Xml(mask(n.Label))}</data>""");
            sb.AppendLine($"""      <data key="category">{Xml(n.Category)}</data>""");
            sb.AppendLine($"""      <data key="confidence">{n.Confidence.ToString(System.Globalization.CultureInfo.InvariantCulture)}</data>""");
            sb.AppendLine($"""      <data key="inferred">{(n.Inferred ? "true" : "false")}</data>""");
            sb.AppendLine("    </node>");
        }
        foreach (var e in _db.GetEdges())
        {
            sb.AppendLine($"""    <edge source="n{e.Src}" target="n{e.Dst}">""");
            sb.AppendLine($"""      <data key="type">{Xml(e.Type)}</data>""");
            sb.AppendLine("    </edge>");
        }
        sb.AppendLine("  </graph>");
        sb.AppendLine("</graphml>");
        return sb.ToString();
    }

    /// <summary>Writes all export formats to the folder; returns the JSON path.</summary>
    public string WriteToFolder(string folder, DateTime? from = null, DateTime? to = null, Func<string, string>? mask = null)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var suffix = mask is null ? "" : "-masked";
        string P(string ext) => Path.Combine(folder, $"semanticportrait-export-{stamp}{suffix}.{ext}");
        var json = P("json");
        File.WriteAllText(json, ToJson(from, to, mask));
        File.WriteAllText(P("md"), ToMarkdown(from, to, mask));
        File.WriteAllText(P("csv"), ToCsv(from, to, mask));
        File.WriteAllText(P("mmd"), ToMermaid(mask));
        File.WriteAllText(P("graphml"), ToGraphML(mask));
        return json;
    }

    private static string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
    private static string Xml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    private static string Mermaid(string s) =>
        s.Replace("\"", "'").Replace("[", "(").Replace("]", ")").Replace("|", "/").Replace("\n", " ");
    private static string San(string s) =>
        new string(s.Where(char.IsLetterOrDigit).ToArray()) is { Length: > 0 } ok ? ok : "cat";
}
