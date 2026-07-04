using Microsoft.Data.Sqlite;

namespace SemanticPortrait.Core;

// Read-only journal insight queries: exact-text search, writing stats, mood series, calendar days.
public sealed partial class Db
{
    /// <summary>Exact (case-insensitive) substring search over the user's own entries. Returns
    /// the message id, when, and a snippet around the hit — for a jump-to-entry search box.</summary>
    public List<(long Id, string WhenUtc, string Snippet)> SearchEntries(string query, int limit = 60)
    {
        var q = query.Trim();
        var results = new List<(long, string, string)>();
        if (q.Length == 0) return results;
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText =
                "SELECT id, created_utc, text FROM messages WHERE role='user' AND text LIKE $q ESCAPE '\\' " +
                "ORDER BY id DESC LIMIT $n;";
            cmd.Parameters.AddWithValue("$q", "%" + q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var text = r.GetString(2);
                var i = text.IndexOf(q, StringComparison.OrdinalIgnoreCase);
                var start = Math.Max(0, i - 40);
                var snip = text.Substring(start, Math.Min(text.Length - start, 140)).Trim();
                results.Add((r.GetInt64(0), r.GetString(1), (start > 0 ? "…" : "") + snip + (start + 140 < text.Length ? "…" : "")));
            }
            return results;
        }
    }

    /// <summary>"Showing up" facts (no streaks, no guilt): user entries + words in the last
    /// <paramref name="days"/>, and how many distinct LOCAL days had at least one entry.</summary>
    public (int Entries, int Words, int DaysWritten, int WindowDays) WritingStats(int days = 60)
    {
        lock (_gate)
        {
            var sinceUtc = DateTime.UtcNow.AddDays(-days).ToString("o");
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT created_utc, text FROM messages WHERE role='user' AND created_utc >= $s;";
            cmd.Parameters.AddWithValue("$s", sinceUtc);
            int entries = 0, words = 0;
            var localDays = new HashSet<DateTime>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                entries++;
                var text = r.GetString(1);
                words += text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
                if (DateTime.TryParse(r.GetString(0), null, System.Globalization.DateTimeStyles.RoundtripKind, out var d))
                    localDays.Add(d.ToLocalTime().Date);
            }
            return (entries, words, localDays.Count, days);
        }
    }

    /// <summary>The id of the FIRST user entry on a given LOCAL day (for a calendar day → thread
    /// jump), or null if that day has no entries.</summary>
    public long? FirstEntryOnDay(int year, int month, int day)
    {
        lock (_gate)
        {
            var target = new DateTime(year, month, day);
            var start = target.AddDays(-1).ToUniversalTime().ToString("o");
            var end = target.AddDays(2).ToUniversalTime().ToString("o");
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT id, created_utc FROM messages WHERE role='user' AND created_utc >= $a AND created_utc < $b ORDER BY id;";
            cmd.Parameters.AddWithValue("$a", start);
            cmd.Parameters.AddWithValue("$b", end);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (DateTime.TryParse(r.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind, out var d)
                    && d.ToLocalTime().Date == target)
                    return r.GetInt64(0);
            return null;
        }
    }

    /// <summary>Per-entry (localDate, valence, energy) for the mood chart, oldest first, bounded to
    /// the last <paramref name="days"/> so one stray old/imported date can't crush the axis into an
    /// unreadable multi-year span. Future-dated and unparseable rows are dropped (data hygiene).</summary>
    public List<(DateTime LocalDate, double Valence, double Energy)> MoodSeries(int days = 90)
    {
        lock (_gate)
        {
            var list = new List<(DateTime, double, double)>();
            var nowLocal = DateTime.Now;
            var since = nowLocal.Date.AddDays(-days);
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT entry_utc, valence, energy FROM entry_meta ORDER BY entry_utc;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (DateTime.TryParse(r.GetString(0), null, System.Globalization.DateTimeStyles.RoundtripKind, out var d))
                {
                    var local = d.ToLocalTime();
                    if (local.Date >= since && local <= nowLocal.AddDays(1))   // in-window, not future
                        list.Add((local, r.GetDouble(1), r.GetDouble(2)));
                }
            return list;
        }
    }

    /// <summary>For a calendar month: per LOCAL day → (entry count, mean valence). Only days with
    /// at least one entry appear. Keyed by day-of-month.</summary>
    public Dictionary<int, (int Count, double MeanValence)> CalendarMonth(int year, int month)
    {
        lock (_gate)
        {
            // Pull entries whose LOCAL date lands in the month. Cheapest correct path: read the
            // month's UTC-ish window wide, filter to local in memory (journal scale is small).
            var start = new DateTime(year, month, 1).AddDays(-1).ToUniversalTime().ToString("o");
            var end = new DateTime(year, month, 1).AddMonths(1).AddDays(1).ToUniversalTime().ToString("o");
            using var cmd = Conn.CreateCommand();
            cmd.CommandText =
                "SELECT m.created_utc, em.valence FROM messages m " +
                "LEFT JOIN entry_meta em ON em.message_id = m.id " +
                "WHERE m.role='user' AND m.created_utc >= $a AND m.created_utc < $b;";
            cmd.Parameters.AddWithValue("$a", start);
            cmd.Parameters.AddWithValue("$b", end);
            var acc = new Dictionary<int, (int, double)>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (!DateTime.TryParse(r.GetString(0), null, System.Globalization.DateTimeStyles.RoundtripKind, out var d)) continue;
                var local = d.ToLocalTime();
                if (local.Year != year || local.Month != month) continue;
                var v = r.IsDBNull(1) ? 0.0 : r.GetDouble(1);
                var cur = acc.GetValueOrDefault(local.Day, (0, 0.0));
                acc[local.Day] = (cur.Item1 + 1, cur.Item2 + v);
            }
            return acc.ToDictionary(kv => kv.Key, kv => (kv.Value.Item1, kv.Value.Item2 / kv.Value.Item1));
        }
    }
}
