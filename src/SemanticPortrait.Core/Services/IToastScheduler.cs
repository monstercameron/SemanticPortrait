namespace SemanticPortrait.Core;

/// <summary>
/// OS-level toast scheduling, abstracted so Core stays platform-free. The Windows implementation
/// (WindowsToastScheduler, in the App's Platforms/Windows) schedules a notification that the OS
/// delivers at <paramref name="whenUtc"/> even if the app is later locked or closed.
/// </summary>
public interface IToastScheduler
{
    /// <summary>Schedule a toast for whenUtc. (tag, group) identify it for cancellation.</summary>
    Task ScheduleAsync(string tag, string group, DateTimeOffset whenUtc, string title, string body, string argument);

    /// <summary>Cancel a previously scheduled toast. No-op if not found / not supported.</summary>
    void Cancel(string tag, string group);
}
