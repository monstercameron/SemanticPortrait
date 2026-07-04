using System.Text.Json;

namespace SemanticPortrait.Core;

/// <summary>
/// Owns the notification lifecycle: AI privacy classification, OS-toast scheduling, and the in-app
/// feed (bell + drawer). Lives in Core and depends only on abstractions (IChatProvider, Db,
/// IToastScheduler) so it stays platform-free and testable.
///
/// Privacy model (per plan.md §6): a reminder's text is classified private/non-private at creation
/// (unlocked, DB open). Non-private text is handed to the OS notification platform so it can show on
/// a locked-screen toast; private items get only a generic placeholder. Classification fails SAFE
/// (private) on any error. The in-app feed always stores the full text (the DB is encrypted at rest
/// and closed while locked).
/// </summary>
public sealed class NotificationService
{
    public const string ReminderGroup = "reminders";
    public const string PredictionGroup = "predictions";

    private readonly Db _db;
    // Resolved per call so classification runs on the user-selected provider.
    private readonly ProviderRegistry _providers;
    private readonly IToastScheduler _toasts;

    public NotificationService(Db db, ProviderRegistry providers, IToastScheduler toasts)
    {
        _db = db; _providers = providers; _toasts = toasts;
    }

    /// <summary>Quiet hours (local): OS toasts never light the screen in this window — an
    /// overnight due time rolls its TOAST to the next 9 AM (in-app timing is unaffected;
    /// the item still fires into the drawer and thread at its true time).</summary>
    public const int QuietStartHour = 23;
    public const int QuietEndHour = 9;

    /// <summary>The toast delivery grace: the OS toast trails the true due time slightly, so
    /// when the app is FOCUSED the in-app delivery wins and cancels the toast before it pops —
    /// no toast over the window the user is already looking at.</summary>
    public static readonly TimeSpan ToastGrace = TimeSpan.FromSeconds(90);

    /// <summary>User setting (⋯ menu): when true, EVERY toast shows the generic placeholder —
    /// no classification call, nothing personal ever crosses to the OS. Synced from Preferences
    /// by the UI at startup and on toggle.</summary>
    public static volatile bool Discreet;

    /// <summary>Where the OS toast should actually fire for a given due time.</summary>
    public static DateTimeOffset ToastTimeFor(DateTimeOffset dueUtc)
    {
        var local = dueUtc.ToLocalTime();
        if (local.Hour >= QuietStartHour)
            local = new DateTimeOffset(local.Date.AddDays(1).AddHours(QuietEndHour), local.Offset);
        else if (local.Hour < QuietEndHour)
            local = new DateTimeOffset(local.Date.AddHours(QuietEndHour), local.Offset);
        return local.ToUniversalTime() + ToastGrace;
    }

    /// <summary>Classify + schedule the OS toast for a reminder. Safe to call fire-and-forget.</summary>
    public async Task ScheduleReminderAsync(long reminderId, string text, DateTimeOffset dueUtc, CancellationToken ct = default)
    {
        // Discreet mode short-circuits the classifier: nothing personal ever leaves, and no
        // provider call is spent deciding.
        bool isPrivate = Discreet || await ClassifyPrivateAsync(text, ct);
        try { if (_db.IsOpen) _db.SetReminderPrivate(reminderId, isPrivate); } catch { }

        // Locked-screen safety: only non-private text crosses into the OS notification platform.
        var title = "SemanticPortrait";
        var body = isPrivate ? "1 reminder — unlock to view" : text;
        var tag = reminderId.ToString();
        try
        {
            await _toasts.ScheduleAsync(tag, ReminderGroup, ToastTimeFor(dueUtc), title, body, $"reminder:{reminderId}",
                buttons: new[]
                {
                    ("Snooze 15m", $"snooze:reminder:{reminderId}:15"),
                    ("Snooze 1h",  $"snooze:reminder:{reminderId}:60"),
                    ("Done",       $"done:reminder:{reminderId}"),
                });
        }
        catch { /* OS scheduling is best-effort; the in-app feed still works */ }
    }

    public void CancelReminder(long reminderId)
    {
        try { _toasts.Cancel(reminderId.ToString(), ReminderGroup); } catch { }
    }

    /// <summary>Immediate OS toast for the evening journal nudge — fires only when the app
    /// doesn't have the user's attention (the in-thread invitation lands either way). The text
    /// is FIXED and content-free, so it needs no privacy classification and is lock-screen safe
    /// by construction; a stable tag means a repeat replaces rather than stacks.</summary>
    public async Task ShowCheckinToastAsync()
    {
        try
        {
            await _toasts.ScheduleAsync("checkin", "checkin", DateTimeOffset.UtcNow,
                "SemanticPortrait", "A quiet moment for today? Nothing in the journal yet this evening.",
                "checkin");
        }
        catch { /* best-effort — the in-thread line still lands */ }
    }

    /// <summary>Classify + schedule the OS toast for a prediction's due time (same privacy model
    /// as reminders: only non-private text crosses to the locked screen).</summary>
    public async Task SchedulePredictionAsync(long predictionId, string claim, DateTimeOffset dueUtc, CancellationToken ct = default)
    {
        bool isPrivate = Discreet || await ClassifyPrivateAsync(claim, ct);
        var body = isPrivate ? "A prediction is due — unlock to score it" : $"Prediction due: {claim}";
        try
        {
            await _toasts.ScheduleAsync(predictionId.ToString(), PredictionGroup, ToastTimeFor(dueUtc),
                "SemanticPortrait", body, $"prediction:{predictionId}");
        }
        catch { /* OS scheduling is best-effort */ }
    }

    public void CancelPrediction(long predictionId)
    {
        try { _toasts.Cancel(predictionId.ToString(), PredictionGroup); } catch { }
    }

    /// <summary>In-app feed row for a due prediction (full text — DB is open here).</summary>
    public long RaiseInAppForPrediction(Prediction p) =>
        _db.AddNotification("prediction", p.Id, "Prediction due", $"{p.Claim} — resolves when: {p.Criterion}");

    /// <summary>Add the in-app feed row for a due reminder (full text — DB is open/unlocked here).</summary>
    public long RaiseInAppForReminder(Reminder rem) =>
        _db.AddNotification("reminder", rem.Id, "Reminder", rem.Text);

    /// <summary>Mark that the agent has already brought this notification up in-thread (dedup on click).</summary>
    public void MarkSurfaced(long id) { if (_db.IsOpen) _db.MarkNotificationSurfaced(id); }

    public bool Dismiss(long id) => _db.IsOpen && _db.DeleteNotification(id);
    public void ClearAll() { if (_db.IsOpen) _db.DeleteAllNotifications(); }

    public int UnreadCount() => _db.IsOpen ? _db.UnreadNotificationCount() : 0;
    public List<Notification> List() => _db.IsOpen ? _db.ListNotifications() : new();

    /// <summary>AI privacy classifier. Returns true (private) on any failure — fail safe.</summary>
    public async Task<bool> ClassifyPrivateAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var ai = _providers.Active;
        if (!ai.HasKey) return true;
        try
        {
            var failed = false;
            var sb = new System.Text.StringBuilder();
            var input = new[] { new ChatMessage("user", text) };
            await foreach (var tok in ai.StreamReplyAsync(Prompts.PrivacyClassifier, input, effort: "low",
                onError: _ => failed = true, ct: ct))
                sb.Append(tok);
            if (failed) return true;   // provider error → fail safe, don't parse error text

            var s = sb.ToString();
            var i = s.IndexOf('{'); var j = s.LastIndexOf('}');
            if (i >= 0 && j > i)
            {
                using var doc = JsonDocument.Parse(s[i..(j + 1)]);
                if (doc.RootElement.TryGetProperty("private", out var p))
                    return p.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.String => !string.Equals(p.GetString(), "false", StringComparison.OrdinalIgnoreCase),
                        _ => true
                    };
            }
        }
        catch (Exception e) { DevTrap.Report("privacy-classifier", e); }
        return true;   // fail safe: treat as private
    }
}
