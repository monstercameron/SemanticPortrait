using Microsoft.Data.Sqlite;

namespace SemanticPortrait.Core;

// Analysis artifacts: per-entry metadata, dated life events, calibration predictions.
public sealed partial class Db
{
    // --- entry metadata (contemporaneous; validated complete) -----------------
    /// <summary>
    /// Store complete metadata for an entry. entry_utc is taken from the message itself (accurate,
    /// not model-supplied). Throws if message_id is unknown or any CHECK fails (incomplete data).
    /// </summary>
    public void SetEntryMeta(long messageId, string mood, double valence, double intensity,
        double energy, string topicsJson, string peopleJson, string summary)
    {
        lock (_gate)
        {
            string? entryUtc;
            using (var look = Conn.CreateCommand())
            {
                look.CommandText = "SELECT created_utc FROM messages WHERE id=$id;";
                look.Parameters.AddWithValue("$id", messageId);
                entryUtc = look.ExecuteScalar() as string;
            }
            if (entryUtc is null)
                throw new InvalidOperationException($"unknown message_id {messageId}");

            using var cmd = Conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO entry_meta(message_id, entry_utc, mood, valence, intensity, energy, topics, people, summary)
                VALUES($m,$u,$mood,$val,$int,$en,$top,$ppl,$sum)
                ON CONFLICT(message_id) DO UPDATE SET
                    mood=$mood, valence=$val, intensity=$int, energy=$en, topics=$top, people=$ppl, summary=$sum;
                """;
            cmd.Parameters.AddWithValue("$m", messageId);
            cmd.Parameters.AddWithValue("$u", entryUtc);
            cmd.Parameters.AddWithValue("$mood", mood);
            cmd.Parameters.AddWithValue("$val", valence);
            cmd.Parameters.AddWithValue("$int", intensity);
            cmd.Parameters.AddWithValue("$en", energy);
            cmd.Parameters.AddWithValue("$top", topicsJson);
            cmd.Parameters.AddWithValue("$ppl", peopleJson);
            cmd.Parameters.AddWithValue("$sum", summary);
            cmd.ExecuteNonQuery();
        }
    }

    public EntryMeta? GetEntryMeta(long messageId)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT message_id, entry_utc, mood, valence, intensity, energy, topics, people, summary FROM entry_meta WHERE message_id=$id;";
            cmd.Parameters.AddWithValue("$id", messageId);
            using var r = cmd.ExecuteReader();
            return r.Read()
                ? new EntryMeta(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetDouble(3), r.GetDouble(4),
                    r.GetDouble(5), r.GetString(6), r.GetString(7), r.GetString(8))
                : null;
        }
    }

    public List<EntryMeta> GetAllEntryMeta()
    {
        lock (_gate)
        {
            var list = new List<EntryMeta>();
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT message_id, entry_utc, mood, valence, intensity, energy, topics, people, summary FROM entry_meta ORDER BY message_id;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new EntryMeta(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetDouble(3), r.GetDouble(4),
                    r.GetDouble(5), r.GetString(6), r.GetString(7), r.GetString(8)));
            return list;
        }
    }

    // --- events --------------------------------------------------------------
    public long AddEvent(string eventUtc, string summary)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "INSERT INTO events(event_utc, summary, created_utc) VALUES($e,$s,$c); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$e", eventUtc);
            cmd.Parameters.AddWithValue("$s", summary);
            cmd.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("o"));
            return (long)(cmd.ExecuteScalar() ?? 0L);
        }
    }

    public List<EventRow> GetEvents()
    {
        lock (_gate)
        {
            var list = new List<EventRow>();
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT id, event_utc, summary FROM events ORDER BY event_utc;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new EventRow(r.GetInt64(0), r.GetString(1), r.GetString(2)));
            return list;
        }
    }

    /// <summary>"On this day": events + dated entries that fell on this calendar month+day in
    /// a PRIOR year, most-recent first. Comparison is on the LOCAL date, since that's the day the
    /// user lived. Cheap enough at journal scale to filter in memory.</summary>
    public List<(int YearsAgo, string Kind, string Summary, string WhenUtc)> OnThisDay(DateTime nowLocal)
    {
        lock (_gate)
        {
            var hits = new List<(int, string, string, string)>();
            void Consider(string kind, string summary, string utc)
            {
                if (!DateTime.TryParse(utc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d)) return;
                var local = d.ToLocalTime();
                if (local.Month == nowLocal.Month && local.Day == nowLocal.Day && local.Year < nowLocal.Year)
                    hits.Add((nowLocal.Year - local.Year, kind, summary, utc));
            }
            using (var cmd = Conn.CreateCommand())
            {
                cmd.CommandText = "SELECT event_utc, summary FROM events;";
                using var r = cmd.ExecuteReader();
                while (r.Read()) Consider("event", r.GetString(1), r.GetString(0));
            }
            using (var cmd = Conn.CreateCommand())
            {
                // dated entries: the user's own words, keyed by their contemporaneous entry time
                cmd.CommandText = "SELECT em.entry_utc, m.text FROM entry_meta em JOIN messages m ON m.id=em.message_id;";
                using var r = cmd.ExecuteReader();
                while (r.Read()) Consider("entry", r.GetString(1), r.GetString(0));
            }
            return hits.OrderByDescending(h => h.Item4).ToList();
        }
    }

    // --- predictions (calibration) -------------------------------------------
    public long AddPrediction(string claim, string criterion, string? dueUtc)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO predictions(created_utc, claim, criterion, due_utc) VALUES($c,$cl,$cr,$d); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$cl", claim);
            cmd.Parameters.AddWithValue("$cr", criterion);
            cmd.Parameters.AddWithValue("$d", (object?)dueUtc ?? DBNull.Value);
            return (long)(cmd.ExecuteScalar() ?? 0L);
        }
    }

    /// <summary>Resolve a prediction with an outcome + accuracy score (0..1). False if id unknown.</summary>
    public bool ResolvePrediction(long id, string outcome, double score)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText =
                "UPDATE predictions SET resolved_utc=$r, outcome=$o, score=$s WHERE id=$id AND resolved_utc IS NULL;";
            cmd.Parameters.AddWithValue("$r", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$o", outcome);
            cmd.Parameters.AddWithValue("$s", Math.Clamp(score, 0, 1));
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public List<Prediction> GetPredictions()
    {
        lock (_gate)
        {
            var list = new List<Prediction>();
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT id, created_utc, claim, criterion, due_utc, resolved_utc, outcome, score FROM predictions ORDER BY id DESC;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new Prediction(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
                    r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetDouble(7)));
            return list;
        }
    }

    /// <summary>Unresolved predictions whose due time has passed and that haven't been surfaced yet.</summary>
    public List<Prediction> DuePredictions(DateTime nowUtc)
    {
        return GetPredictions()
            .Where(p => p.ResolvedUtc is null && p.DueUtc is not null && !IsPredictionNotified(p.Id))
            .Where(p => DateTime.TryParse(p.DueUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d)
                        && d.ToUniversalTime() <= nowUtc)
            .ToList();
    }

    public void MarkPredictionNotified(long id)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "UPDATE predictions SET notified=1 WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public bool IsPredictionNotified(long id)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT notified FROM predictions WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) != 0;
        }
    }

    // --- metrics snapshots (schema v2 §6) --------------------------------------
    /// <summary>Store one self-model metrics snapshot (JSON payload; extend freely).</summary>
    public long SaveMetricsSnapshot(string payloadJson)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "INSERT INTO metrics(snapshot_utc, payload) VALUES($u,$p); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$p", payloadJson);
            return (long)(cmd.ExecuteScalar() ?? 0L);
        }
    }

    /// <summary>Newest-first metrics snapshots (for trend views).</summary>
    public List<(string Utc, string Payload)> GetMetricsSnapshots(int limit = 100)
    {
        lock (_gate)
        {
            var list = new List<(string, string)>();
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT snapshot_utc, payload FROM metrics ORDER BY id DESC LIMIT $n;";
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.GetString(0), r.GetString(1)));
            return list;
        }
    }

    // --- bulk-import ledger (resumable, idempotent import) ---------------------
    public bool IsChunkImported(string hash)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM import_ledger WHERE hash=$h;";
            cmd.Parameters.AddWithValue("$h", hash);
            return cmd.ExecuteScalar() is not null;
        }
    }

    /// <summary>Record a chunk as fully analyzed (call only AFTER the analyst succeeded).</summary>
    public void MarkChunkImported(string hash, string source)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO import_ledger(hash, source, imported_utc) VALUES($h,$s,$u)
                ON CONFLICT(hash) DO NOTHING;
                """;
            cmd.Parameters.AddWithValue("$h", hash);
            cmd.Parameters.AddWithValue("$s", source);
            cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    // --- offline analysis queue -------------------------------------------------
    /// <summary>Retry ceiling: items at/over this many failed attempts are parked (never selected
    /// again, but kept in the table for inspection). Prevents a poison payload from retrying forever
    /// and pinning the "N to go" counter.</summary>
    public const int MaxAnalysisAttempts = 5;

    public long EnqueueAnalysis(long entryId, string payload)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO pending_analyses(entry_id, payload, created_utc) VALUES($e,$p,$u); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$e", entryId);
            cmd.Parameters.AddWithValue("$p", payload);
            cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("o"));
            return (long)(cmd.ExecuteScalar() ?? 0L);
        }
    }

    /// <summary>Oldest retryable queued analysis (parked items excluded), or null when none.</summary>
    public (long Id, long EntryId, string Payload, long Attempts)? NextPendingAnalysis()
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = $"SELECT id, entry_id, payload, attempts FROM pending_analyses WHERE attempts < {MaxAnalysisAttempts} ORDER BY id LIMIT 1;";
            using var r = cmd.ExecuteReader();
            return r.Read()
                ? (r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt64(3))
                : ((long, long, string, long)?)null;
        }
    }

    public int PendingAnalysisCount()
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM pending_analyses WHERE attempts < {MaxAnalysisAttempts};";   // parked items don't count as "to go"
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
    }

    public void CompletePendingAnalysis(long id)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "DELETE FROM pending_analyses WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public void BumpPendingAnalysisAttempts(long id)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "UPDATE pending_analyses SET attempts = attempts + 1 WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }
}
