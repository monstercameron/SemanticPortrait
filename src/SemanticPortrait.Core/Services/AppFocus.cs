namespace SemanticPortrait.Core;

/// <summary>
/// Whether the app window currently holds the user's attention (foreground focus). Set by the
/// platform head's window events; consumed by delivery logic: a FOCUSED app delivers a due
/// reminder in-thread and cancels the redundant OS toast (which trails the true due time by
/// <see cref="NotificationService.ToastGrace"/> precisely so this cancel can win the race).
/// An UNFOCUSED app lets the toast do what toasts are for — grabbing attention elsewhere.
/// </summary>
public static class AppFocus
{
    public static volatile bool IsFocused = true;
}
