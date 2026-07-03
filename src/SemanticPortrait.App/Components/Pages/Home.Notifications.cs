using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using SemanticPortrait.App.Services;
using SemanticPortrait.Core;

namespace SemanticPortrait.App.Components.Pages;

// Notifications: the reminder poller, the bell + drawer, and OS-toast activation routing
// (which never bypasses the lock).
public partial class Home
{
    // notifications (bell + drawer)
    private bool _showNotifs;
    private int _unread;
    private List<Notification> _notifs = new();
    private string? _pendingToastArg;   // a toast activation that arrived while locked / pre-init

    // reminders: poll for due reminders and let the agent message proactively
    private System.Timers.Timer? _reminders;
    private void StartReminders()
    {
        _reminders ??= new System.Timers.Timer(30_000) { AutoReset = true };
        _reminders.Elapsed -= ReminderTick;
        _reminders.Elapsed += ReminderTick;
        _reminders.Start();
    }
    private long _tickCount;
    private void ReminderTick(object? s, System.Timers.ElapsedEventArgs e) =>
        _ = InvokeAsync(async () =>
        {
            await FireDueReminders();
            await DrainAnalysisQueue();
            // background compaction (~hourly): folds aged messages even while hidden in the tray,
            // so the next Send doesn't pay the compaction latency.
            if (++_tickCount % 120 == 0 && !_busy && !_locked && Database.IsOpen)
                try { await Compaction.EnsureCompactedAsync(DateTime.UtcNow); }
                catch (Exception ex) { DevTrap.Report("compaction-tick", ex); }
        }).Guard("reminder-tick");   // a fault here would otherwise kill the timer loop silently

    // One queued reflection per tick: success removes it, failure bumps attempts and waits for
    // the next tick (cheap while offline — the provider fails fast).
    private bool _drainBusy;
    private async Task DrainAnalysisQueue()
    {
        if (_drainBusy || _busy || _locked || _configuring || !Database.IsOpen) return;
        var item = Database.NextPendingAnalysis();
        if (item is not { } pending) return;
        _drainBusy = true;
        try
        {
            var failed = false;
            // The queue stores only the distillation; the raw entry is re-fetched by id so the
            // analyst still sees the user's verbatim words (null if the persist failed — the
            // subagent falls back to the distillation). Watchdog: a wedged stream would leave
            // _drainBusy true forever and silently stop the whole retry queue.
            var raw = Database.GetMessage(pending.EntryId)?.Text ?? "";
            using var watchdog = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(8));
            await Analyst.ReflectAsync(pending.EntryId, raw, pending.Payload, "",
                onProviderError: _ => failed = true, ct: watchdog.Token);
            if (failed) { Bump(pending); return; }
            Database.CompletePendingAnalysis(pending.Id);
            var left = Database.PendingAnalysisCount();
            await InvokeAsync(() =>
            {
                LoadGraph();
                SaveMetricsSnapshot();
                _messages.Add(new() { Role = "sys", Text = left > 0
                    ? $"🧠 caught up on 1 queued analysis ({left} to go)"
                    : "🧠 caught up on queued analysis" });
                StateHasChanged();
            });
        }
        catch (Exception e)
        {
            DevTrap.Report("drain-analysis", e);
            try { Bump(pending); } catch { }   // best-effort bump
        }
        finally { _drainBusy = false; }
    }

    // Bump the attempt counter; when an item hits the retry cap it stops being selected
    // (parked, not deleted — the payload stays inspectable). Surface the parking in dev:
    // a parked analysis is a permanent memory gap unless someone looks.
    private void Bump((long Id, long EntryId, string Payload, long Attempts) pending)
    {
        Database.BumpPendingAnalysisAttempts(pending.Id);
        if (pending.Attempts + 1 >= Db.MaxAnalysisAttempts)
            DevTrap.Report("analysis-parked", new InvalidOperationException(
                $"queued analysis #{pending.Id} (entry {pending.EntryId}) parked after {Db.MaxAnalysisAttempts} failed attempts"));
    }

    private async Task FireDueReminders()
    {
        if (_busy || _locked || _configuring || !Database.IsOpen) return;
        foreach (var rem in Database.DueReminders(DateTime.UtcNow))
        {
            Database.MarkReminderFired(rem.Id);
            // Attention routing: FOCUSED → the agent tells them in-thread and the pending OS
            // toast (which trails by ToastGrace for exactly this reason) is cancelled — no toast
            // over the window they're already in. UNFOCUSED → the toast is the tap on the
            // shoulder; the in-thread line still lands and waits for their return.
            if (AppFocus.IsFocused) Notify.CancelReminder(rem.Id);
            var notifId = Notify.RaiseInAppForReminder(rem);   // feed the bell + drawer
            RefreshNotifs();
            await FireProactive(
                $"[A reminder the user set is now due: \"{rem.Text}\". Message them now to remind them — " +
                "brief and warm, one or two lines. No preamble.]");
            Notify.MarkSurfaced(notifId);   // now in-thread → clicking it later won't re-nudge
            RefreshNotifs();
        }
        foreach (var p in Database.DuePredictions(DateTime.UtcNow))
        {
            Database.MarkPredictionNotified(p.Id);
            if (AppFocus.IsFocused) Notify.CancelPrediction(p.Id);
            var notifId = Notify.RaiseInAppForPrediction(p);
            RefreshNotifs();
            await FireProactive(
                $"[Prediction #{p.Id} is now due: \"{p.Claim}\" (resolves when: {p.Criterion}). " +
                "Ask the user what actually happened, in one short line — the answer scores the " +
                "prediction. No preamble.]");
            Notify.MarkSurfaced(notifId);
            RefreshNotifs();
        }
    }

    /// <summary>
    /// Session-open digest: the first time the app opens each local day, if anything lands
    /// today (or is overdue), the agent says ONE grounded line about it — 3+ items become an
    /// offer of the list, never a dump. Runs at most once per day (settings-keyed), never on a
    /// brand-new thread (the greeting owns that moment).
    /// </summary>
    private void MaybeSessionDigest()
    {
        try
        {
            if (_locked || _configuring || !Database.IsOpen || _messages.Count == 0) return;
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            if (Database.GetSetting("digest_day") == today) return;

            var nowUtc = DateTime.UtcNow;
            var endOfDayUtc = DateTime.Now.Date.AddDays(1).ToUniversalTime();
            var items = Database.ListReminders(pendingOnly: true)
                .Where(rem => Compactor.ParseUtc(rem.DueUtc) is var d && d != DateTime.MinValue && d < endOfDayUtc)
                .Select(rem => rem.Text)
                .Concat(Database.GetEventsRange(nowUtc.ToString("o"), endOfDayUtc.ToString("o")).Select(ev => ev.Summary))
                .ToList();

            Database.SetSetting("digest_day", today);   // one shot per day, hit or miss
            if (items.Count == 0) return;
            _ = FireProactive(
                $"[Session-open digest — on the user's plate today ({items.Count} item(s)): " +
                string.Join("; ", items.Take(5)) +
                ". Mention this in ONE grounded line. If there are 3 or more, name the most " +
                "time-sensitive one and OFFER the rest (\"want the list?\") instead of dumping " +
                "them. Neutral framing for anything overdue — \"still open\", never guilt. No preamble.]")
                .Guard("session-digest");
        }
        catch (Exception e) { DevTrap.Report("session-digest", e); }
    }

    /// <summary>
    /// After unlock, back-fill notifications for reminders that came due while the app was locked
    /// (the OS toast already alerted; this keeps the in-app drawer correct). Then refresh the bell.
    /// </summary>
    private void ReconcileNotifs()
    {
        if (!Database.IsOpen) return;
        foreach (var rem in Database.DueReminders(DateTime.UtcNow))
        {
            Database.MarkReminderFired(rem.Id);
            Notify.RaiseInAppForReminder(rem);
        }
        RefreshNotifs();
    }

    // --- notifications -------------------------------------------------------
    /// <summary>Refresh the unread count + drawer list from the DB (no-op while locked).</summary>
    private void RefreshNotifs()
    {
        if (!Database.IsOpen) { _unread = 0; _notifs = new(); return; }
        _unread = Notify.UnreadCount();
        _notifs = Notify.List();
    }

    private void ToggleNotifs()
    {
        _showNotifs = !_showNotifs;
        if (_showNotifs) RefreshNotifs();
    }

    private void MarkAllRead()
    {
        if (Database.IsOpen) Database.MarkAllNotificationsRead();
        RefreshNotifs();
    }

    /// <summary>Dismiss a single notification (the × on a drawer item).</summary>
    private void DismissNotif(Notification n)
    {
        Notify.Dismiss(n.Id);
        RefreshNotifs();
    }

    /// <summary>Clear the whole drawer.</summary>
    private void ClearNotifs()
    {
        Notify.ClearAll();
        RefreshNotifs();
    }

    /// <summary>
    /// Open a notification. If the agent hasn't already brought this reminder up in-thread (e.g. it
    /// fired while locked/closed), it says ONE clean line about it; otherwise we just focus the chat
    /// so the user can respond — no duplicate nudge.
    /// </summary>
    private async Task OpenNotification(Notification n)
    {
        if (Database.IsOpen) Database.MarkNotificationRead(n.Id);
        _showNotifs = false;

        if (!n.Surfaced)
        {
            Notify.MarkSurfaced(n.Id);
            RefreshNotifs();
            _scrollDown = true; StateHasChanged();
            await FireProactive(n.RefType == "prediction"
                ? $"[The user just opened a due prediction: \"{n.Body}\". Ask them what actually " +
                  "happened in ONE short line so it can be scored. No preamble.]"
                : $"[The user just opened a reminder they had set: \"{n.Body}\". It is now due. Bring it up " +
                  "with them in ONE short, warm line — remind them of it directly. No preamble, and do not " +
                  "coach them about unrelated actions.]");
        }
        else
        {
            // Already in the thread — just surface the conversation, don't repeat it.
            RefreshNotifs();
            _scrollDown = true; _focusNext = true; StateHasChanged();
        }
    }

    /// <summary>Route a toast activation argument (e.g. "reminder:123") to the right surface.</summary>
    private async Task HandleToastActivation(string arg)
    {
        // Never bypass the lock: stash and process after unlock.
        if (_locked || _configuring || !Database.IsOpen) { _pendingToastArg = arg; return; }

        foreach (var kind in new[] { "reminder", "prediction" })
        {
            if (arg.StartsWith(kind + ":", StringComparison.Ordinal)
                && long.TryParse(arg[(kind.Length + 1)..], out var refId))
            {
                var n = _notifs.FirstOrDefault(x => x.RefType == kind && x.RefId == refId)
                        ?? Notify.List().FirstOrDefault(x => x.RefType == kind && x.RefId == refId);
                if (n is not null) { await OpenNotification(n); return; }
            }
        }
        // Fallback: just open the drawer.
        _showNotifs = true; RefreshNotifs(); StateHasChanged();
    }

    private void OnToastActivated(string arg) => _ = InvokeAsync(async () =>
    {
        await HandleToastActivation(arg);
        StateHasChanged();
    }).Guard("toast-activated");
}
