using Microsoft.Data.Sqlite;

namespace SemanticPortrait.Core;

// The eternal thread: messages, analyst notes, embeddings + vector search, compaction.
public sealed partial class Db
{
    // --- compaction (older-than-window summary) -------------------------------
    public (string Summary, string ThroughUtc)? GetCompaction()
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT summary, through_utc FROM compaction WHERE id=1;";
            using var r = cmd.ExecuteReader();
            return r.Read() ? (r.GetString(0), r.GetString(1)) : ((string, string)?)null;
        }
    }

    public void SetCompaction(string summary, string throughUtc)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO compaction(id, summary, through_utc) VALUES(1,$s,$u)
                ON CONFLICT(id) DO UPDATE SET summary=$s, through_utc=$u;
                """;
            cmd.Parameters.AddWithValue("$s", summary);
            cmd.Parameters.AddWithValue("$u", throughUtc);
            cmd.ExecuteNonQuery();
        }
    }

    // --- messages -------------------------------------------------------------
    public long AddMessage(string role, string text, string createdUtc, string? detail = null)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO messages(created_utc, role, text, detail) VALUES($c,$r,$t,$d); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$c", createdUtc);
            cmd.Parameters.AddWithValue("$r", role);
            cmd.Parameters.AddWithValue("$t", text);
            cmd.Parameters.AddWithValue("$d", (object?)detail ?? DBNull.Value);
            return (long)(cmd.ExecuteScalar() ?? 0L);
        }
    }

    /// <summary>One message by id (e.g. re-fetching the raw entry for a queued analysis).</summary>
    public StoredMessage? GetMessage(long id)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT id, role, text, created_utc, detail FROM messages WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            return r.Read()
                ? new StoredMessage(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4))
                : null;
        }
    }

    public List<StoredMessage> GetMessages()
    {
        lock (_gate)
        {
            var list = new List<StoredMessage>();
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT id, role, text, created_utc, detail FROM messages ORDER BY id ASC;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new StoredMessage(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4)));
            return list;
        }
    }

    // --- notes (the agent's own durable, refinable insights) ------------------
    public long AddNote(string text, string utc)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO notes(created_utc, updated_utc, text) VALUES($c,$c,$t); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$c", utc);
            cmd.Parameters.AddWithValue("$t", text);
            return (long)(cmd.ExecuteScalar() ?? 0L);
        }
    }

    /// <summary>Refine an existing note; returns false if the id doesn't exist.</summary>
    public bool UpdateNote(long id, string text, string utc)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "UPDATE notes SET text=$t, updated_utc=$u WHERE id=$id;";
            cmd.Parameters.AddWithValue("$t", text);
            cmd.Parameters.AddWithValue("$u", utc);
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public List<StoredNote> GetNotes()
    {
        lock (_gate)
        {
            var list = new List<StoredNote>();
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT id, text, created_utc, updated_utc FROM notes ORDER BY id;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new StoredNote(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3)));
            return list;
        }
    }

    public StoredNote? GetNote(long id)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT id, text, created_utc, updated_utc FROM notes WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? new StoredNote(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3)) : null;
        }
    }

    // --- embeddings + vector search ------------------------------------------
    public void AddEmbedding(string refType, long refId, float[] vec)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO embeddings(ref_type, ref_id, dim, vec) VALUES($t,$i,$d,$v);";
            cmd.Parameters.AddWithValue("$t", refType);
            cmd.Parameters.AddWithValue("$i", refId);
            cmd.Parameters.AddWithValue("$d", vec.Length);
            cmd.Parameters.AddWithValue("$v", ToBytes(vec));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Replace the embedding for a ref (used when a note is refined / re-embedded).</summary>
    public void UpsertEmbedding(string refType, long refId, float[] vec)
    {
        lock (_gate)
        {
            using (var del = Conn.CreateCommand())
            {
                del.CommandText = "DELETE FROM embeddings WHERE ref_type=$t AND ref_id=$i;";
                del.Parameters.AddWithValue("$t", refType);
                del.Parameters.AddWithValue("$i", refId);
                del.ExecuteNonQuery();
            }
            AddEmbedding(refType, refId, vec);   // lock is reentrant
        }
    }

    public int EmbeddingCount(string refType, long refId)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM embeddings WHERE ref_type=$t AND ref_id=$i;";
            cmd.Parameters.AddWithValue("$t", refType);
            cmd.Parameters.AddWithValue("$i", refId);
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
    }

    /// <summary>Top-k most similar entries (messages) and notes to the query vector (cosine).</summary>
    public List<SearchHit> Search(float[] query, int k)
    {
        lock (_gate)
        {
            var results = new List<SearchHit>();
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = """
                SELECT e.ref_type, e.ref_id, e.vec,
                       COALESCE(m.text, n.text)               AS text,
                       COALESCE(m.created_utc, n.created_utc)  AS created
                FROM embeddings e
                LEFT JOIN messages m ON e.ref_type='message' AND m.id = e.ref_id
                LEFT JOIN notes    n ON e.ref_type='note'    AND n.id = e.ref_id;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (r["text"] is DBNull) continue;
                var vec = FromBytes((byte[])r["vec"]);
                results.Add(new SearchHit(
                    r.GetString(0), r.GetInt64(1), r.GetString(3), r.GetString(4), Cosine(query, vec)));
            }
            // Ranking = cosine + two small nudges (each ≤ 0.08, so real similarity always
            // dominates):
            //  - note bias: the analyst's distilled notes over raw chat lines (curated recall).
            //  - recency: exp decay, 90-day half-life — at equal similarity a fresh item outranks
            //    a stale one (a corrected read beats the one it replaced), but a strong old match
            //    still beats a weak new one.
            var nowUtc = DateTime.UtcNow;
            return results
                .OrderByDescending(x => x.Score
                    + (x.RefType == "note" ? 0.08 : 0)
                    + RecencyBoost(x.CreatedUtc, nowUtc))
                .Take(k).ToList();
        }
    }

    /// <summary>Recency term for search ranking: 0.08 · 2^(−age/90d). Unparseable dates get no
    /// boost rather than an error. Deliberately absent from SearchNotes — contradiction-checking
    /// needs old notes to surface on similarity alone.</summary>
    internal static double RecencyBoost(string createdUtc, DateTime nowUtc)
    {
        var created = ParseUtcOrMin(createdUtc);
        if (created == DateTime.MinValue) return 0;
        var ageDays = Math.Max(0, (nowUtc - created).TotalDays);
        return 0.08 * Math.Pow(2, -ageDays / 90.0);
    }

    private static DateTime ParseUtcOrMin(string s) =>
        DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var d)
            ? d.ToUniversalTime() : DateTime.MinValue;

    /// <summary>Search ONLY the analyst's notes (its prior analysis) — no raw chat entries.</summary>
    public List<SearchHit> SearchNotes(float[] query, int k)
    {
        lock (_gate)
        {
            var results = new List<SearchHit>();
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = """
                SELECT e.ref_id, e.vec, n.text, n.created_utc
                FROM embeddings e JOIN notes n ON n.id = e.ref_id
                WHERE e.ref_type='note';
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                results.Add(new SearchHit("note", r.GetInt64(0), r.GetString(2), r.GetString(3),
                    Cosine(query, FromBytes((byte[])r["vec"]))));
            return results.OrderByDescending(x => x.Score).Take(k).ToList();
        }
    }
}
