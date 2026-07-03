using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace SemanticPortrait.Core;

/// <summary>
/// Source adapters for bulk import (design §13a): each converts a known export format into the
/// normalized dated-entry text the analyst pipeline already understands ("[date] speaker: text"
/// lines, chronological). Pure string → string, no IO, so every format is unit-testable. Unknown
/// content passes through untouched — plain journals/notes never get mangled.
///
/// Supported:
///  - Discord (DiscordChatExporter JSON)
///  - WhatsApp chat export (_chat.txt / "12/06/2024, 15:45 - Name: message")
///  - SMS ("SMS Backup &amp; Restore" XML)
///  - Generic CSV with a recognizable timestamp column
/// </summary>
public static class ImportAdapters
{
    /// <summary>Detect the format from name + content and normalize; passthrough when unknown.</summary>
    public static string Normalize(string fileName, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        try
        {
            if (ext == ".json" && LooksLikeDiscordJson(text)) return FromDiscordJson(text);
            if (ext == ".xml" && text.Contains("<sms", StringComparison.OrdinalIgnoreCase)) return FromSmsXml(text);
            if (ext == ".csv") return FromCsv(text) ?? text;
            if (LooksLikeWhatsApp(text)) return FromWhatsApp(text);
        }
        catch (Exception e)
        {
            // A malformed export should degrade to raw text (the analyst still copes), never fail the import.
            DevTrap.Report("import-adapter", e);
        }
        return text;
    }

    // ------------------------------------------------------------------ Discord (JSON)
    internal static bool LooksLikeDiscordJson(string text)
    {
        var head = text.Length > 4000 ? text[..4000] : text;
        return head.Contains("\"messages\"") && (head.Contains("\"channel\"") || head.Contains("\"guild\""));
    }

    internal static string FromDiscordJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var sb = new StringBuilder();
        var channel = root.TryGetProperty("channel", out var ch) && ch.TryGetProperty("name", out var cn)
            ? cn.GetString() : null;
        if (!string.IsNullOrEmpty(channel)) sb.AppendLine($"Discord conversation — #{channel}").AppendLine();

        if (!root.TryGetProperty("messages", out var msgs) || msgs.ValueKind != JsonValueKind.Array)
            return json;
        foreach (var m in msgs.EnumerateArray())
        {
            var content = m.TryGetProperty("content", out var c) ? c.GetString() : null;
            if (string.IsNullOrWhiteSpace(content)) continue;   // stickers/embeds/attachments-only
            var author = m.TryGetProperty("author", out var a)
                ? (a.TryGetProperty("nickname", out var nick) && nick.ValueKind == JsonValueKind.String ? nick.GetString()
                   : a.TryGetProperty("name", out var an) ? an.GetString() : "unknown")
                : "unknown";
            var stamp = m.TryGetProperty("timestamp", out var ts) && DateTimeOffset.TryParse(ts.GetString(), out var dto)
                ? dto.ToString("yyyy-MM-dd HH:mm") : "";
            sb.AppendLine($"[{stamp}] {author}: {content.Trim()}");
        }
        return sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------ WhatsApp (txt)
    // "[6/12/24, 3:45:12 PM] Name: message"   or   "12/06/2024, 15:45 - Name: message"
    private static readonly System.Text.RegularExpressions.Regex WaLine = new(
        @"^[‎‏]?(?:\[(?<d1>[^\]]+)\]|(?<d2>\d{1,2}[./-]\d{1,2}[./-]\d{2,4},? \d{1,2}:\d{2}(?::\d{2})?(?:\s?[AP]M)?))\s*(?:-\s*)?(?<who>[^:]{1,60}):\s?(?<msg>.*)$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static bool LooksLikeWhatsApp(string text)
    {
        int hits = 0, seen = 0;
        foreach (var line in Head(text, 40))
        {
            seen++;
            if (WaLine.IsMatch(line)) hits++;
        }
        return seen > 0 && hits >= Math.Max(3, seen / 3);
    }

    internal static string FromWhatsApp(string text)
    {
        var sb = new StringBuilder();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var m = WaLine.Match(line);
            if (!m.Success) { if (line.Length > 0) sb.AppendLine("    " + line.Trim()); continue; }   // continuation line
            var msg = m.Groups["msg"].Value.Trim();
            if (msg is "<Media omitted>" or "image omitted" or "audio omitted" or "video omitted" or "sticker omitted") continue;
            var when = m.Groups["d1"].Success ? m.Groups["d1"].Value : m.Groups["d2"].Value;
            var norm = DateTime.TryParse(when.Replace(" ", " "), CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces, out var dt)
                ? dt.ToString("yyyy-MM-dd HH:mm") : when;
            sb.AppendLine($"[{norm}] {m.Groups["who"].Value.Trim()}: {msg}");
        }
        return sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------ SMS (Backup & Restore XML)
    internal static string FromSmsXml(string xml)
    {
        var sb = new StringBuilder();
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        foreach (XmlNode sms in doc.SelectNodes("//sms")!)
        {
            var body = sms.Attributes?["body"]?.Value;
            if (string.IsNullOrWhiteSpace(body)) continue;
            var who = sms.Attributes?["contact_name"]?.Value is { Length: > 0 } cnm && cnm != "(Unknown)"
                ? cnm : sms.Attributes?["address"]?.Value ?? "unknown";
            // type 2 = sent by the user, type 1 = received
            var sent = sms.Attributes?["type"]?.Value == "2";
            var stamp = long.TryParse(sms.Attributes?["date"]?.Value, out var ms)
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "";
            sb.AppendLine(sent ? $"[{stamp}] me → {who}: {body.Trim()}" : $"[{stamp}] {who}: {body.Trim()}");
        }
        return sb.Length > 0 ? sb.ToString().TrimEnd() : xml;
    }

    // ------------------------------------------------------------------ CSV (generic)
    /// <summary>Heuristic CSV: needs a header with a recognizable date/time column and a text
    /// column; returns null (→ passthrough) when the shape isn't recognizable.</summary>
    internal static string? FromCsv(string csv)
    {
        var rows = ParseCsv(csv);
        if (rows.Count < 2) return null;
        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int dateCol = header.FindIndex(h => h is "date" or "timestamp" or "time" or "datetime" or "created" or "created_at" or "when");
        int textCol = header.FindIndex(h => h is "text" or "body" or "content" or "message" or "entry" or "note" or "notes");
        if (dateCol < 0 || textCol < 0) return null;
        int whoCol = header.FindIndex(h => h is "author" or "from" or "sender" or "name" or "who");

        var sb = new StringBuilder();
        foreach (var row in rows.Skip(1))
        {
            if (row.Count <= Math.Max(dateCol, textCol)) continue;
            var body = row[textCol].Trim();
            if (body.Length == 0) continue;
            var norm = DateTime.TryParse(row[dateCol], CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dt)
                ? dt.ToString("yyyy-MM-dd HH:mm") : row[dateCol].Trim();
            var who = whoCol >= 0 && row.Count > whoCol && row[whoCol].Trim().Length > 0 ? $" {row[whoCol].Trim()}:" : "";
            sb.AppendLine($"[{norm}]{who} {body}");
        }
        return sb.Length > 0 ? sb.ToString().TrimEnd() : null;
    }

    /// <summary>Minimal RFC-4180 parser (quotes, escaped quotes, newlines in quoted fields).</summary>
    internal static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else inQuotes = false;
                }
                else cell.Append(ch);
            }
            else switch (ch)
            {
                case '"': inQuotes = true; break;
                case ',': row.Add(cell.ToString()); cell.Clear(); break;
                case '\r': break;
                case '\n': row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = new(); break;
                default: cell.Append(ch); break;
            }
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }

    private static IEnumerable<string> Head(string text, int lines)
    {
        int start = 0, produced = 0;
        while (start < text.Length && produced < lines)
        {
            int nl = text.IndexOf('\n', start);
            if (nl < 0) { yield return text[start..].TrimEnd('\r'); yield break; }
            yield return text[start..nl].TrimEnd('\r');
            start = nl + 1; produced++;
        }
    }
}
