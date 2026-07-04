using Microsoft.Data.Sqlite;

namespace SemanticPortrait.Core;

// Image attachments on entries. Bytes live in the encrypted DB behind the same lock as the words.
public sealed partial class Db
{
    public long AddAttachment(long messageId, string mime, byte[] full, byte[] thumb, string? caption = null)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO attachments(message_id, kind, mime, thumb, bytes, caption, created_utc) " +
                "VALUES($m,'image',$mime,$th,$b,$cap,$c); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$m", messageId);
            cmd.Parameters.AddWithValue("$mime", mime);
            cmd.Parameters.AddWithValue("$th", thumb);
            cmd.Parameters.AddWithValue("$b", full);
            cmd.Parameters.AddWithValue("$cap", (object?)caption ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("o"));
            return (long)(cmd.ExecuteScalar() ?? 0L);
        }
    }

    /// <summary>Attachment thumbs for a message as ready-to-render data URIs (id + mime + thumb).
    /// One query per message; thumbs are small (~420px) so inline base64 stays cheap.</summary>
    public List<AttachmentThumb> ThumbsFor(long messageId)
    {
        lock (_gate)
        {
            var list = new List<AttachmentThumb>();
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT id, mime, thumb, caption FROM attachments WHERE message_id=$m ORDER BY id;";
            cmd.Parameters.AddWithValue("$m", messageId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var uri = $"data:{r.GetString(1)};base64,{Convert.ToBase64String((byte[])r["thumb"])}";
                list.Add(new AttachmentThumb(r.GetInt64(0), uri, r.IsDBNull(3) ? null : r.GetString(3)));
            }
            return list;
        }
    }

    /// <summary>Every message id that has at least one attachment — one query so the thread
    /// renderer doesn't fan out N per-message lookups.</summary>
    public HashSet<long> MessageIdsWithAttachments()
    {
        lock (_gate)
        {
            var set = new HashSet<long>();
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT message_id FROM attachments;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) set.Add(r.GetInt64(0));
            return set;
        }
    }

    /// <summary>The full-size bytes as a data URI (for the lightbox / export). Null if gone.</summary>
    public string? AttachmentDataUri(long id)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT mime, bytes FROM attachments WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? $"data:{r.GetString(0)};base64,{Convert.ToBase64String((byte[])r["bytes"])}" : null;
        }
    }
}
