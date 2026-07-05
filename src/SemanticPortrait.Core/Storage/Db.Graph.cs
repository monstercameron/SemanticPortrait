using Microsoft.Data.Sqlite;

namespace SemanticPortrait.Core;

// The self-model graph (nodes + edges) behind the Constellation.
public sealed partial class Db
{
    // --- graph (nodes + edges, the Constellation) ----------------------------
    /// <summary>
    /// Insert or update a node, keyed by (category,label). Dedup/merge: the label is first
    /// resolved through the entity registry (nickname → canonical), then matched
    /// case-insensitively against existing nodes so "alice"/"Alice"/"Ali" land on ONE node.
    /// Returns the node id.
    /// </summary>
    public long UpsertNode(string category, string label, bool inferred, double confidence)
    {
        lock (_gate)
        {
            label = ResolveCanonical(label.Trim());
            var now = DateTime.UtcNow.ToString("o");

            // case-insensitive merge onto an existing node (keeps its original casing)
            long? existing = null;
            using (var find = Conn.CreateCommand())
            {
                find.CommandText = "SELECT id FROM nodes WHERE category=$c AND label=$l COLLATE NOCASE;";
                find.Parameters.AddWithValue("$c", category);
                find.Parameters.AddWithValue("$l", label);
                if (find.ExecuteScalar() is long id) existing = id;
            }
            if (existing is { } eid)
            {
                using var up = Conn.CreateCommand();
                up.CommandText = "UPDATE nodes SET inferred=$i, confidence=$conf, updated_utc=$now WHERE id=$id;";
                up.Parameters.AddWithValue("$i", inferred ? 1 : 0);
                up.Parameters.AddWithValue("$conf", confidence);
                up.Parameters.AddWithValue("$now", now);
                up.Parameters.AddWithValue("$id", eid);
                up.ExecuteNonQuery();
                return eid;
            }

            using var ins = Conn.CreateCommand();
            ins.CommandText = """
                INSERT INTO nodes(category, label, inferred, confidence, created_utc, updated_utc)
                VALUES($c,$l,$i,$conf,$now,$now);
                SELECT last_insert_rowid();
                """;
            ins.Parameters.AddWithValue("$c", category);
            ins.Parameters.AddWithValue("$l", label);
            ins.Parameters.AddWithValue("$i", inferred ? 1 : 0);
            ins.Parameters.AddWithValue("$conf", confidence);
            ins.Parameters.AddWithValue("$now", now);
            return (long)(ins.ExecuteScalar() ?? 0L);
        }
    }

    // --- canonical entity registry (schema v2 §2) -----------------------------
    /// <summary>
    /// Resolve a mention (nickname, spelling variant) to its canonical name; returns the input
    /// unchanged when nothing is registered. Case-insensitive.
    /// </summary>
    public string ResolveCanonical(string mention)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = """
                SELECT e.canonical_name FROM entities e WHERE e.canonical_name = $m COLLATE NOCASE
                UNION
                SELECT e.canonical_name FROM entity_aliases a
                    JOIN entities e ON e.id = a.entity_id
                    WHERE a.mention = $m COLLATE NOCASE
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$m", mention);
            return cmd.ExecuteScalar() as string ?? mention;
        }
    }

    /// <summary>
    /// Register a canonical entity (created if new) and optionally a mention that should resolve
    /// to it. Also merges any pre-existing node whose label matches the mention into the
    /// canonical node (edges re-pointed). Returns the entity id.
    /// </summary>
    public long RegisterEntityAlias(string canonical, string? mention, string kind = "person")
    {
        lock (_gate)
        {
            canonical = canonical.Trim();
            var now = DateTime.UtcNow.ToString("o");
            return InTransaction(tx =>
            {
                using (var up = Conn.CreateCommand())
                {
                    up.Transaction = tx;
                    up.CommandText = """
                        INSERT INTO entities(canonical_name, kind, created_utc) VALUES($c,$k,$now)
                        ON CONFLICT(canonical_name) DO NOTHING;
                        """;
                    up.Parameters.AddWithValue("$c", canonical);
                    up.Parameters.AddWithValue("$k", kind);
                    up.Parameters.AddWithValue("$now", now);
                    up.ExecuteNonQuery();
                }
                long entityId;
                using (var sel = Conn.CreateCommand())
                {
                    sel.Transaction = tx;
                    sel.CommandText = "SELECT id FROM entities WHERE canonical_name=$c COLLATE NOCASE;";
                    sel.Parameters.AddWithValue("$c", canonical);
                    entityId = (long)(sel.ExecuteScalar() ?? 0L);
                }
                if (!string.IsNullOrWhiteSpace(mention) &&
                    !string.Equals(mention.Trim(), canonical, StringComparison.OrdinalIgnoreCase))
                {
                    using var ins = Conn.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText = """
                        INSERT INTO entity_aliases(entity_id, mention, created_utc) VALUES($e,$m,$now)
                        ON CONFLICT(mention) DO NOTHING;
                        """;
                    ins.Parameters.AddWithValue("$e", entityId);
                    ins.Parameters.AddWithValue("$m", mention.Trim());
                    ins.Parameters.AddWithValue("$now", now);
                    ins.ExecuteNonQuery();
                    MergeNodesByLabel(mention.Trim(), canonical, tx);
                }
                return entityId;
            });
        }
    }

    /// <summary>All registered entities with their aliases (canonical → comma-joined mentions).</summary>
    public List<(string Canonical, string Kind, string Aliases)> GetEntities()
    {
        lock (_gate)
        {
            var list = new List<(string, string, string)>();
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = """
                SELECT e.canonical_name, e.kind, COALESCE(GROUP_CONCAT(a.mention, ', '), '')
                FROM entities e LEFT JOIN entity_aliases a ON a.entity_id = e.id
                GROUP BY e.id ORDER BY e.canonical_name;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
            return list;
        }
    }

    /// <summary>Every entity name the egress masker should pseudonymize — the canonical name plus
    /// each registered mention — paired with an uppercased kind token (person → PERSON). This is
    /// how free-form names/places (which the regex patterns can't catch) get masked before any
    /// cloud call; the first mention of a not-yet-registered entity still egresses in the clear,
    /// but every mention after the analyst has registered it is tokenized.</summary>
    public List<(string Kind, string Value)> GetEntityMentions()
    {
        lock (_gate)
        {
            var list = new List<(string, string)>();
            if (_conn is null) return list;
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = """
                SELECT kind, canonical_name FROM entities
                UNION
                SELECT e.kind, a.mention FROM entity_aliases a JOIN entities e ON e.id = a.entity_id;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var val = r.GetString(1);
                if (!string.IsNullOrWhiteSpace(val)) list.Add((r.GetString(0).ToUpperInvariant(), val));
            }
            return list;
        }
    }

    // A late alias registration can leave two nodes for the same entity (one under the mention,
    // one under the canonical). Re-point the mention-node's edges at the canonical node and
    // delete the duplicate. Called under _gate, and always inside the caller's transaction (tx)
    // so the repoint+delete-edges+delete-embeddings+delete-node group per pair is all-or-nothing.
    private void MergeNodesByLabel(string fromLabel, string toLabel, SqliteTransaction tx)
    {
        using var find = Conn.CreateCommand();
        find.Transaction = tx;
        find.CommandText = """
            SELECT f.id, t.id FROM nodes f
            JOIN nodes t ON t.category = f.category AND t.label = $to COLLATE NOCASE
            WHERE f.label = $from COLLATE NOCASE AND f.id <> t.id;
            """;
        find.Parameters.AddWithValue("$from", fromLabel);
        find.Parameters.AddWithValue("$to", toLabel);
        var pairs = new List<(long From, long To)>();
        using (var r = find.ExecuteReader())
            while (r.Read()) pairs.Add((r.GetInt64(0), r.GetInt64(1)));

        foreach (var (from, to) in pairs)
        {
            using var fix = Conn.CreateCommand();
            fix.Transaction = tx;
            fix.CommandText = """
                UPDATE OR IGNORE edges SET src_id=$to WHERE src_id=$from;
                UPDATE OR IGNORE edges SET dst_id=$to WHERE dst_id=$from;
                DELETE FROM edges WHERE src_id=$from OR dst_id=$from;  -- duplicates the ignore kept
                DELETE FROM embeddings WHERE ref_type='node' AND ref_id=$from;  -- no dangling semantic hits
                DELETE FROM nodes WHERE id=$from;
                """;
            fix.Parameters.AddWithValue("$from", from);
            fix.Parameters.AddWithValue("$to", to);
            fix.ExecuteNonQuery();
        }
    }

    /// <summary>Insert (or update) an edge keyed by (src,dst,type). Returns its id.</summary>
    public long AddEdge(long src, long dst, string type, string label, bool inferred, double confidence)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO edges(src_id, dst_id, type, label, inferred, confidence, created_utc)
                VALUES($s,$d,$t,$l,$i,$conf,$now)
                ON CONFLICT(src_id, dst_id, type) DO UPDATE SET
                    label=$l, inferred=$i, confidence=$conf;
                SELECT id FROM edges WHERE src_id=$s AND dst_id=$d AND type=$t;
                """;
            cmd.Parameters.AddWithValue("$s", src);
            cmd.Parameters.AddWithValue("$d", dst);
            cmd.Parameters.AddWithValue("$t", type);
            cmd.Parameters.AddWithValue("$l", label ?? "");
            cmd.Parameters.AddWithValue("$i", inferred ? 1 : 0);
            cmd.Parameters.AddWithValue("$conf", confidence);
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            return (long)(cmd.ExecuteScalar() ?? 0L);
        }
    }

    public List<GraphNode> GetNodes()
    {
        lock (_gate)
        {
            var list = new List<GraphNode>();
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT id, category, label, inferred, confidence FROM nodes ORDER BY id;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new GraphNode(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3) != 0, r.GetDouble(4)));
            return list;
        }
    }

    public List<GraphEdge> GetEdges()
    {
        lock (_gate)
        {
            var list = new List<GraphEdge>();
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT id, src_id, dst_id, type, label, inferred, confidence FROM edges;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new GraphEdge(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3),
                    r.GetString(4), r.GetInt64(5) != 0, r.GetDouble(6)));
            return list;
        }
    }

    /// <summary>Delete a node and any edges touching it (e.g. user rejects an over-parsed inference).</summary>
    public void DeleteNode(long id)
    {
        lock (_gate)
        {
            InTransaction(tx =>
            {
                using var cmd = Conn.CreateCommand();
                cmd.Transaction = tx;
                // Also drop the node's embedding — a dangling 'node' embedding would keep resolving
                // semantic lookups to a node that no longer exists.
                cmd.CommandText = """
                    DELETE FROM edges WHERE src_id=$id OR dst_id=$id;
                    DELETE FROM embeddings WHERE ref_type='node' AND ref_id=$id;
                    DELETE FROM nodes WHERE id=$id;
                    """;
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            });
        }
    }

    public long? FindNodeId(string category, string label)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT id FROM nodes WHERE category=$c AND label=$l;";
            cmd.Parameters.AddWithValue("$c", category);
            cmd.Parameters.AddWithValue("$l", label);
            var r = cmd.ExecuteScalar();
            return r is null ? null : (long)r;
        }
    }
}
