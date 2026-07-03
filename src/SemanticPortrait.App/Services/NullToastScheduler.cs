using SemanticPortrait.Core;

namespace SemanticPortrait.App.Services;

/// <summary>No-op IToastScheduler for non-Windows targets / safety fallback. The in-app feed still works.</summary>
public sealed class NullToastScheduler : IToastScheduler
{
    public Task ScheduleAsync(string tag, string group, DateTimeOffset whenUtc, string title, string body, string argument,
        IReadOnlyList<(string Label, string Argument)>? buttons = null) => Task.CompletedTask;
    public void Cancel(string tag, string group) { }
}
