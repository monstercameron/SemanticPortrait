using Microsoft.Toolkit.Uwp.Notifications;
using SemanticPortrait.Core;
using Windows.UI.Notifications;

namespace SemanticPortrait.App.Services;

/// <summary>
/// Windows implementation of IToastScheduler using the unpackaged-friendly compat APIs
/// (Microsoft.Toolkit.Uwp.Notifications). Scheduled toasts are stored by the OS notification
/// platform and delivered at the due time even if this process is locked or has exited.
/// </summary>
public sealed class WindowsToastScheduler : IToastScheduler
{
    public Task ScheduleAsync(string tag, string group, DateTimeOffset whenUtc, string title, string body, string argument)
    {
        var content = new ToastContentBuilder()
            .AddArgument("a", argument)
            .AddText(title)
            .AddText(body)
            .GetToastContent();

        var xml = content.GetXml();
        var notifier = ToastNotificationManagerCompat.CreateToastNotifier();

        // Windows silently drops schedules in the past (or near-now). Fire immediately instead.
        if (whenUtc <= DateTimeOffset.UtcNow.AddSeconds(5))
        {
            notifier.Show(new ToastNotification(xml) { Tag = Tag(tag), Group = Tag(group) });
        }
        else
        {
            var stn = new ScheduledToastNotification(xml, whenUtc) { Tag = Tag(tag), Group = Tag(group) };
            notifier.AddToSchedule(stn);
        }
        return Task.CompletedTask;
    }

    public void Cancel(string tag, string group)
    {
        try
        {
            var notifier = ToastNotificationManagerCompat.CreateToastNotifier();
            // No cancel-by-tag for scheduled toasts — iterate and match.
            foreach (var s in notifier.GetScheduledToastNotifications())
                if (s.Tag == Tag(tag) && s.Group == Tag(group)) notifier.RemoveFromSchedule(s);
        }
        catch { /* best-effort */ }
    }

    // Tag/Group have a length cap on the WinRT API; our values are short, but stay safe.
    private static string Tag(string s) => s.Length <= 16 ? s : s[..16];
}
