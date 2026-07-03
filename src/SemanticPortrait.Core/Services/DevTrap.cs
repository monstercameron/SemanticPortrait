namespace SemanticPortrait.Core;

/// <summary>
/// Dev-visibility hook for errors that are deliberately swallowed so the app keeps running
/// (background timers, best-effort parses, fire-and-forget tasks). Production behavior is
/// unchanged — swallowed stays swallowed, and with no subscribers a Report is a no-op. In
/// DEBUG the app subscribes twice: CrashLog (full stack to AppData/crashlog.txt) and the
/// chat surface (a ⚠️ sys bubble), so no error is ever invisible while developing.
///
/// House rule: a bare `catch { }` is acceptable only for best-effort file cleanup where
/// failure is truly meaningless. Any catch that protects real logic reports here:
///     catch (Exception e) { DevTrap.Report("where-it-happened", e); }
/// </summary>
public static class DevTrap
{
    /// <summary>Raised on every reported error. Source is a short stable site name.</summary>
    public static event Action<string, Exception>? Trapped;

    public static void Report(string source, Exception ex)
    {
        // A throwing listener must not re-crash the site that was defending itself.
        try { Trapped?.Invoke(source, ex); } catch { }
    }

    /// <summary>
    /// Guard a fire-and-forget task (`_ = DoAsync()`): a fault gets reported instead of
    /// vanishing. (Unobserved task exceptions only surface at GC time, if ever — every
    /// discarded task in this codebase goes through here.)
    /// </summary>
    public static Task Guard(this Task task, string source)
    {
        task.ContinueWith(
            t => Report(source, t.Exception!.GetBaseException()),
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        return task;
    }
}
