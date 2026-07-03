using Microsoft.Data.Sqlite;

namespace SemanticPortrait.Core;

// Retrieval-side joins for the recall engine. Everything here joins on REAL keys — message ids,
// node ids, ISO-8601 UTC date strings (lexicographically ordered by construction, all written via
// ToString("o")). Name-shaped lookups (labels, people, mentions) are model-written free text and
// must go through the resolver ladder (alias registry → NOCASE → embeddings) before landing here.
public sealed partial class Db
{
    /// <summary>
    /// The conversational exchange around a message: the message itself ± <paramref name="radius"/>
    /// user/assistant turns in thread order (tool/sys rows skipped). Empty if the id is unknown —
    /// callers treat a missing exchange as "hit had no surrounding context", not an error.
    /// </summary>
    public List<StoredMessage> GetExchange(long messageId, int radius = 1)
    {
        lock (_gate)
        {
            var list = new List<StoredMessage>();
            using var cmd = Conn.CreateCommand();
            // Window by thread order: `radius` turns before (id <=) and after (id >) the anchor.
            // Two ordered slices beat OFFSET math and stay correct when ids have gaps.
            cmd.CommandText = """
                SELECT * FROM (
                    SELECT id, role, text, created_utc FROM messages
                    WHERE role IN ('user','assistant') AND id <= $id
                    ORDER BY id DESC LIMIT $before)
                UNION ALL
                SELECT * FROM (
                    SELECT id, role, text, created_utc FROM messages
                    WHERE role IN ('user','assistant') AND id > $id
                    ORDER BY id ASC LIMIT $after)
                ORDER BY id ASC;
                """;
            cmd.Parameters.AddWithValue("$id", messageId);
            cmd.Parameters.AddWithValue("$before", radius + 1);   // +1 = the anchor itself
            cmd.Parameters.AddWithValue("$after", radius);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new StoredMessage(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3)));
            // If the anchor id itself is unknown (deleted / never existed), the window is meaningless.
            return list.Any(m => m.Id == messageId) ? list : new();
        }
    }

    /// <summary>Entry metadata (mood/valence/energy…) in [fromUtc, toUtc], oldest first.</summary>
    public List<EntryMeta> GetEntryMetaRange(string fromUtc, string toUtc)
    {
        lock (_gate)
        {
            var list = new List<EntryMeta>();
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = """
                SELECT message_id, entry_utc, mood, valence, intensity, energy, topics, people, summary
                FROM entry_meta WHERE entry_utc >= $f AND entry_utc <= $t ORDER BY entry_utc;
                """;
            cmd.Parameters.AddWithValue("$f", fromUtc);
            cmd.Parameters.AddWithValue("$t", toUtc);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new EntryMeta(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetDouble(3),
                    r.GetDouble(4), r.GetDouble(5), r.GetString(6), r.GetString(7), r.GetString(8)));
            return list;
        }
    }

    /// <summary>Dated life events in [fromUtc, toUtc], oldest first.</summary>
    public List<EventRow> GetEventsRange(string fromUtc, string toUtc)
    {
        lock (_gate)
        {
            var list = new List<EventRow>();
            using var cmd = Conn.CreateCommand();
            cmd.CommandText =
                "SELECT id, event_utc, summary FROM events WHERE event_utc >= $f AND event_utc <= $t ORDER BY event_utc;";
            cmd.Parameters.AddWithValue("$f", fromUtc);
            cmd.Parameters.AddWithValue("$t", toUtc);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new EventRow(r.GetInt64(0), r.GetString(1), r.GetString(2)));
            return list;
        }
    }

    /// <summary>Nodes that existed at a moment in time — the substrate of the time scrub
    /// ("show me my portrait as it was in June"). ISO-UTC strings compare lexicographically.</summary>
    public List<GraphNode> GetNodesAsOf(string utc)
    {
        lock (_gate)
        {
            var list = new List<GraphNode>();
            using var cmd = Conn.CreateCommand();
            cmd.CommandText =
                "SELECT id, category, label, inferred, confidence FROM nodes WHERE created_utc <= $u ORDER BY id;";
            cmd.Parameters.AddWithValue("$u", utc);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new GraphNode(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3) != 0, r.GetDouble(4)));
            return list;
        }
    }

    /// <summary>Edges that existed at a moment in time (see <see cref="GetNodesAsOf"/>).</summary>
    public List<GraphEdge> GetEdgesAsOf(string utc)
    {
        lock (_gate)
        {
            var list = new List<GraphEdge>();
            using var cmd = Conn.CreateCommand();
            cmd.CommandText =
                "SELECT id, src_id, dst_id, type, label, inferred, confidence FROM edges WHERE created_utc <= $u;";
            cmd.Parameters.AddWithValue("$u", utc);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new GraphEdge(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3),
                    r.GetString(4), r.GetInt64(5) != 0, r.GetDouble(6)));
            return list;
        }
    }

    /// <summary>One node by id (canonical label/casing as stored).</summary>
    public GraphNode? GetNode(long id)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT id, category, label, inferred, confidence FROM nodes WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            return r.Read()
                ? new GraphNode(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3) != 0, r.GetDouble(4))
                : null;
        }
    }

    /// <summary>
    /// All nodes matching a label across categories (NOCASE). Callers resolve aliases first;
    /// multiple hits are legitimate (e.g. 'running' as both joy and body).
    /// </summary>
    public List<GraphNode> FindNodesByLabel(string label)
    {
        lock (_gate)
        {
            var list = new List<GraphNode>();
            using var cmd = Conn.CreateCommand();
            cmd.CommandText =
                "SELECT id, category, label, inferred, confidence FROM nodes WHERE label = $l COLLATE NOCASE;";
            cmd.Parameters.AddWithValue("$l", label);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new GraphNode(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3) != 0, r.GetDouble(4)));
            return list;
        }
    }

    /// <summary>
    /// 1-hop neighborhood of a node: every edge touching it, oriented from the focus node's point
    /// of view, highest-confidence first, capped (a hub node can touch a lot of the graph).
    /// </summary>
    public List<NeighborEdge> GetNeighborhood(long nodeId, int limit = 24)
    {
        lock (_gate)
        {
            var list = new List<NeighborEdge>();
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = """
                SELECT e.type, (e.src_id = $id) AS outgoing,
                       n.id, n.category, n.label, n.inferred, n.confidence, e.inferred, e.confidence
                FROM edges e
                JOIN nodes n ON n.id = CASE WHEN e.src_id = $id THEN e.dst_id ELSE e.src_id END
                WHERE e.src_id = $id OR e.dst_id = $id
                ORDER BY e.confidence DESC LIMIT $k;
                """;
            cmd.Parameters.AddWithValue("$id", nodeId);
            cmd.Parameters.AddWithValue("$k", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new NeighborEdge(
                    r.GetString(0), r.GetInt64(1) != 0,
                    new GraphNode(r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetInt64(5) != 0, r.GetDouble(6)),
                    r.GetInt64(7) != 0, r.GetDouble(8)));
            return list;
        }
    }

    /// <summary>
    /// Notes containing a needle (NOCASE substring). The blunt tier of person/theme lookup —
    /// the resolver expands the needle to canonical + aliases and calls this per variant.
    /// </summary>
    public List<StoredNote> FindNotes(string needle, int limit = 10)
    {
        lock (_gate)
        {
            var list = new List<StoredNote>();
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, text, created_utc, updated_utc FROM notes
                WHERE text LIKE $n ESCAPE '\' COLLATE NOCASE
                ORDER BY updated_utc DESC LIMIT $k;
                """;
            cmd.Parameters.AddWithValue("$n", "%" + needle.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%");
            cmd.Parameters.AddWithValue("$k", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new StoredNote(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3)));
            return list;
        }
    }

    /// <summary>Cosine top-k over NODE embeddings — the semantic tier of the resolver ladder
    /// ("the radar thing" → distortion/rejection-radar). Score included so callers can threshold.</summary>
    public List<(GraphNode Node, double Score)> SearchNodes(float[] query, int k)
    {
        lock (_gate)
        {
            var results = new List<(GraphNode, double)>();
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = """
                SELECT n.id, n.category, n.label, n.inferred, n.confidence, e.vec
                FROM embeddings e JOIN nodes n ON n.id = e.ref_id WHERE e.ref_type='node';
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                results.Add((new GraphNode(r.GetInt64(0), r.GetString(1), r.GetString(2),
                    r.GetInt64(3) != 0, r.GetDouble(4)), Cosine(query, FromBytes((byte[])r["vec"]))));
            return results.OrderByDescending(x => x.Item2).Take(k).ToList();
        }
    }

    /// <summary>Cosine top-k over EVENT embeddings — semantic timeline lookup.</summary>
    public List<(EventRow Event, double Score)> SearchEvents(float[] query, int k)
    {
        lock (_gate)
        {
            var results = new List<(EventRow, double)>();
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = """
                SELECT ev.id, ev.event_utc, ev.summary, e.vec
                FROM embeddings e JOIN events ev ON ev.id = e.ref_id WHERE e.ref_type='event';
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                results.Add((new EventRow(r.GetInt64(0), r.GetString(1), r.GetString(2)),
                    Cosine(query, FromBytes((byte[])r["vec"]))));
            return results.OrderByDescending(x => x.Item2).Take(k).ToList();
        }
    }

    // --- backfill support: rows written before node/event embedding existed -----
    public List<GraphNode> GetNodesWithoutEmbedding()
    {
        lock (_gate)
        {
            var list = new List<GraphNode>();
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = """
                SELECT n.id, n.category, n.label, n.inferred, n.confidence FROM nodes n
                WHERE NOT EXISTS (SELECT 1 FROM embeddings e WHERE e.ref_type='node' AND e.ref_id=n.id);
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new GraphNode(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3) != 0, r.GetDouble(4)));
            return list;
        }
    }

    public List<EventRow> GetEventsWithoutEmbedding()
    {
        lock (_gate)
        {
            var list = new List<EventRow>();
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = """
                SELECT ev.id, ev.event_utc, ev.summary FROM events ev
                WHERE NOT EXISTS (SELECT 1 FROM embeddings e WHERE e.ref_type='event' AND e.ref_id=ev.id);
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new EventRow(r.GetInt64(0), r.GetString(1), r.GetString(2)));
            return list;
        }
    }

    /// <summary>Entries whose recorded people/topics mention the needle (matches inside the JSON
    /// arrays, NOCASE). Resolver expands aliases and unions per variant.</summary>
    public List<EntryMeta> FindEntryMetaMentioning(string needle, int limit = 20)
    {
        lock (_gate)
        {
            var list = new List<EntryMeta>();
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = """
                SELECT message_id, entry_utc, mood, valence, intensity, energy, topics, people, summary
                FROM entry_meta
                WHERE people LIKE $n ESCAPE '\' COLLATE NOCASE OR topics LIKE $n ESCAPE '\' COLLATE NOCASE
                ORDER BY entry_utc DESC LIMIT $k;
                """;
            cmd.Parameters.AddWithValue("$n", "%" + needle.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%");
            cmd.Parameters.AddWithValue("$k", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new EntryMeta(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetDouble(3),
                    r.GetDouble(4), r.GetDouble(5), r.GetString(6), r.GetString(7), r.GetString(8)));
            return list;
        }
    }
}
