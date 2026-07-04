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

    /// <summary>When the user last wrote anything (ISO utc), or null on an empty thread —
    /// cheap single-row query for presence checks like the evening check-in.</summary>
    public string? LastUserMessageUtc()
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT created_utc FROM messages WHERE role='user' ORDER BY id DESC LIMIT 1;";
            return cmd.ExecuteScalar() as string;
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

    /// <summary>Newest conversational message's created_utc, or null if none. GetMessages() orders
    /// by id ASC, so LastOrDefault(role in user/assistant) == the highest matching id == this row —
    /// a scoped equivalent that avoids the full-table scan for callers that only need the timestamp.</summary>
    public string? LastConversationalUtc()
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT created_utc FROM messages WHERE role IN ('user','assistant') ORDER BY id DESC LIMIT 1;";
            return cmd.ExecuteScalar() as string;
        }
    }

    /// <summary>Conversational (user/assistant) messages with created_utc strictly after a cutoff,
    /// id ASC — the scoped equivalent of GetMessages().Where(...).Where(...) for the in-flight
    /// history window. Uses ix_messages_role_created.</summary>
    public List<StoredMessage> GetRecentConversational(string cutoffIso)
    {
        lock (_gate)
        {
            var list = new List<StoredMessage>();
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT id, role, text, created_utc, detail FROM messages WHERE role IN ('user','assistant') AND created_utc > $cutoff ORDER BY id ASC;";
            cmd.Parameters.AddWithValue("$cutoff", cutoffIso);
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

    /// <summary>Top-k most similar entries (messages) and notes to the query vector (cosine).
    /// Two-phase: phase 1 scores every embedding row using only ref_type/ref_id/vec/created_utc
    /// (no text), then phase 2 hydrates text for just the top-k winners — avoids materializing
    /// the whole corpus's text to discard all but k of it.</summary>
    public List<SearchHit> Search(float[] query, int k)
    {
        lock (_gate)
        {
            var scored = new List<(string RefType, long RefId, string CreatedUtc, double Score)>();
            using (var cmd = Conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT e.ref_type, e.ref_id, e.vec,
                           COALESCE(m.created_utc, n.created_utc) AS created
                    FROM embeddings e
                    LEFT JOIN messages m ON e.ref_type='message' AND m.id = e.ref_id
                    LEFT JOIN notes    n ON e.ref_type='note'    AND n.id = e.ref_id;
                    """;
                double qn = 0; for (int i = 0; i < query.Length; i++) qn += query[i] * query[i];
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    // created is NULL exactly when neither join matched (ref_type isn't
                    // message/note, or the referenced row is gone) — messages.created_utc and
                    // notes.created_utc are both NOT NULL, so this is equivalent to the old
                    // "text IS NULL" skip without ever selecting text here.
                    if (r["created"] is DBNull) continue;
                    var vec = AsFloatSpan((byte[])r["vec"]);
                    scored.Add((r.GetString(0), r.GetInt64(1), r.GetString(3), Cosine(query, qn, vec)));
                }
            }
            // Ranking = cosine + two small nudges (each ≤ 0.08, so real similarity always
            // dominates):
            //  - note bias: the analyst's distilled notes over raw chat lines (curated recall).
            //  - recency: exp decay, 90-day half-life — at equal similarity a fresh item outranks
            //    a stale one (a corrected read beats the one it replaced), but a strong old match
            //    still beats a weak new one.
            var nowUtc = DateTime.UtcNow;
            var top = scored
                .OrderByDescending(x => x.Score
                    + (x.RefType == "note" ? 0.08 : 0)
                    + RecencyBoost(x.CreatedUtc, nowUtc))
                .Take(k).ToList();
            if (top.Count == 0) return new List<SearchHit>();

            // Phase 2: hydrate text for exactly the top-k winners, per-type so a message id and
            // a note id that happen to collide numerically never cross-contaminate.
            var msgText = FetchTextById("messages", top.Where(x => x.RefType == "message").Select(x => x.RefId));
            var noteText = FetchTextById("notes", top.Where(x => x.RefType == "note").Select(x => x.RefId));
            return top.Select(x => new SearchHit(
                x.RefType, x.RefId, x.RefType == "note" ? noteText[x.RefId] : msgText[x.RefId],
                x.CreatedUtc, x.Score)).ToList();
        }
    }

    /// <summary>Fetch just the `text` column for a handful of ids from `messages` or `notes` (both
    /// have an id/text column) — the phase-2 hydration step for the vector-search two-phase split.
    /// Called while already holding <see cref="_gate"/>.</summary>
    private Dictionary<long, string> FetchTextById(string table, IEnumerable<long> ids)
    {
        var list = ids.Distinct().ToList();
        var map = new Dictionary<long, string>();
        if (list.Count == 0) return map;
        using var cmd = Conn.CreateCommand();
        var ps = new string[list.Count];
        for (int i = 0; i < list.Count; i++) ps[i] = "$id" + i;
        cmd.CommandText = $"SELECT id, text FROM {table} WHERE id IN ({string.Join(",", ps)});";
        for (int i = 0; i < list.Count; i++) cmd.Parameters.AddWithValue(ps[i], list[i]);
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetInt64(0)] = r.GetString(1);
        return map;
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

    /// <summary>Search ONLY the analyst's notes (its prior analysis) — no raw chat entries.
    /// Two-phase (see <see cref="Search"/>): score on ref_id/vec alone, then hydrate text+created
    /// for just the top-k.</summary>
    public List<SearchHit> SearchNotes(float[] query, int k)
    {
        lock (_gate)
        {
            var scored = new List<(long RefId, double Score)>();
            using (var cmd = Conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT e.ref_id, e.vec
                    FROM embeddings e JOIN notes n ON n.id = e.ref_id
                    WHERE e.ref_type='note';
                    """;
                double qn = 0; for (int i = 0; i < query.Length; i++) qn += query[i] * query[i];
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    scored.Add((r.GetInt64(0), Cosine(query, qn, AsFloatSpan((byte[])r["vec"]))));
            }
            var top = scored.OrderByDescending(x => x.Score).Take(k).ToList();
            if (top.Count == 0) return new List<SearchHit>();

            var notes = FetchNoteTextAndCreated(top.Select(x => x.RefId));
            return top.Select(x =>
            {
                var (text, created) = notes[x.RefId];
                return new SearchHit("note", x.RefId, text, created, x.Score);
            }).ToList();
        }
    }

    /// <summary>Fetch text+created_utc for a handful of note ids — phase-2 hydration for
    /// <see cref="SearchNotes"/>. Called while already holding <see cref="_gate"/>.</summary>
    private Dictionary<long, (string Text, string CreatedUtc)> FetchNoteTextAndCreated(IEnumerable<long> ids)
    {
        var list = ids.Distinct().ToList();
        var map = new Dictionary<long, (string, string)>();
        if (list.Count == 0) return map;
        using var cmd = Conn.CreateCommand();
        var ps = new string[list.Count];
        for (int i = 0; i < list.Count; i++) ps[i] = "$id" + i;
        cmd.CommandText = $"SELECT id, text, created_utc FROM notes WHERE id IN ({string.Join(",", ps)});";
        for (int i = 0; i < list.Count; i++) cmd.Parameters.AddWithValue(ps[i], list[i]);
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetInt64(0)] = (r.GetString(1), r.GetString(2));
        return map;
    }
}
