using Microsoft.Data.Sqlite;

namespace SemanticPortrait.Core;

// User-facing task surface: todos, reminders, and the in-app notification feed.
public sealed partial class Db
{
    // --- todos ---------------------------------------------------------------
    public long AddTodo(string text, string? dueUtc = null)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "INSERT INTO todos(created_utc, text, due_utc) VALUES($c,$t,$d); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$t", text);
            cmd.Parameters.AddWithValue("$d", (object?)dueUtc ?? DBNull.Value);
            return (long)(cmd.ExecuteScalar() ?? 0L);
        }
    }
    public List<TodoItem> ListTodos()
    {
        lock (_gate)
        {
            var list = new List<TodoItem>();
            using var cmd = Conn.CreateCommand();
            // dated first (soonest up), then undated, done last — the agenda's natural read order
            cmd.CommandText = "SELECT id, text, done, created_utc, due_utc FROM todos ORDER BY done, due_utc IS NULL, due_utc, id DESC;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new TodoItem(r.GetInt64(0), r.GetString(1), r.GetInt64(2) != 0, r.GetString(3),
                                                   r.IsDBNull(4) ? null : r.GetString(4)));
            return list;
        }
    }

    /// <summary>Snooze: push the due time forward and re-arm (fired=0) so it fires again.</summary>
    public bool SnoozeReminder(long id, string newDueUtc)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "UPDATE reminders SET due_utc=$d, fired=0 WHERE id=$id;";
            cmd.Parameters.AddWithValue("$d", newDueUtc);
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public Reminder? GetReminder(long id)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT id, due_utc, text, fired FROM reminders WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? new Reminder(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3) != 0) : null;
        }
    }
    public bool SetTodoDone(long id, bool done)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "UPDATE todos SET done=$d, done_utc=$u WHERE id=$id;";
            cmd.Parameters.AddWithValue("$d", done ? 1 : 0);
            cmd.Parameters.AddWithValue("$u", done ? DateTime.UtcNow.ToString("o") : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    // --- reminders -----------------------------------------------------------
    public long AddReminder(string dueUtc, string text)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "INSERT INTO reminders(created_utc, due_utc, text) VALUES($c,$d,$t); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$d", dueUtc);
            cmd.Parameters.AddWithValue("$t", text);
            return (long)(cmd.ExecuteScalar() ?? 0L);
        }
    }
    public List<Reminder> ListReminders(bool pendingOnly)
    {
        lock (_gate)
        {
            var list = new List<Reminder>();
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = pendingOnly
                ? "SELECT id, due_utc, text, fired FROM reminders WHERE fired=0 ORDER BY due_utc;"
                : "SELECT id, due_utc, text, fired FROM reminders ORDER BY due_utc;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new Reminder(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3) != 0));
            return list;
        }
    }
    public List<Reminder> DueReminders(DateTime nowUtc)
    {
        return ListReminders(pendingOnly: true)
            .Where(r => DateTime.TryParse(r.DueUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) && d.ToUniversalTime() <= nowUtc)
            .ToList();
    }
    public void MarkReminderFired(long id)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "UPDATE reminders SET fired=1 WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }
    public bool CancelReminder(long id)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "DELETE FROM reminders WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Record the AI privacy classification for a reminder (governs the OS toast body).</summary>
    public void SetReminderPrivate(long id, bool isPrivate)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "UPDATE reminders SET is_private=$p WHERE id=$id;";
            cmd.Parameters.AddWithValue("$p", isPrivate ? 1 : 0);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public bool GetReminderPrivate(long id)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT is_private FROM reminders WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) != 0;
        }
    }

    // --- notifications (the bell + drawer) -----------------------------------
    public long AddNotification(string refType, long refId, string title, string body)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO notifications(created_utc, ref_type, ref_id, title, body) VALUES($c,$rt,$ri,$t,$b); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$rt", refType);
            cmd.Parameters.AddWithValue("$ri", refId);
            cmd.Parameters.AddWithValue("$t", title);
            cmd.Parameters.AddWithValue("$b", body);
            return (long)(cmd.ExecuteScalar() ?? 0L);
        }
    }

    /// <summary>Notifications newest-first (unread first within that).</summary>
    public List<Notification> ListNotifications()
    {
        lock (_gate)
        {
            var list = new List<Notification>();
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT id, created_utc, ref_type, ref_id, title, body, read, surfaced FROM notifications ORDER BY read ASC, id DESC;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new Notification(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3),
                    r.GetString(4), r.GetString(5), r.GetInt64(6) != 0, r.GetInt64(7) != 0));
            return list;
        }
    }

    public Notification? GetNotification(long id)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT id, created_utc, ref_type, ref_id, title, body, read, surfaced FROM notifications WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            return r.Read()
                ? new Notification(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3),
                    r.GetString(4), r.GetString(5), r.GetInt64(6) != 0, r.GetInt64(7) != 0)
                : null;
        }
    }

    public void MarkNotificationSurfaced(long id)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "UPDATE notifications SET surfaced=1 WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public bool DeleteNotification(long id)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "DELETE FROM notifications WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public void DeleteAllNotifications()
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "DELETE FROM notifications;";
            cmd.ExecuteNonQuery();
        }
    }

    public int UnreadNotificationCount()
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM notifications WHERE read=0;";
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
    }

    public bool MarkNotificationRead(long id)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "UPDATE notifications SET read=1 WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public void MarkAllNotificationsRead()
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "UPDATE notifications SET read=1 WHERE read=0;";
            cmd.ExecuteNonQuery();
        }
    }
}
