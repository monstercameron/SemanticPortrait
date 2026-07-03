using Microsoft.AspNetCore.Components.Web;

namespace SemanticPortrait.App.Components;

/// <summary>
/// Root error boundary: component lifecycle exceptions (OnInitializedAsync etc.) in MAUI
/// Blazor Hybrid never reach AppDomain / the WinUI dispatcher / ILogger — the renderer eats
/// them and the user just gets the "An unhandled error has occurred" strip over a blank page.
/// This is the only seam that actually receives them. DEBUG: full stack to the crash log and
/// on screen. Release: generic message only (exception text can reference user data).
/// </summary>
public sealed class CrashLoggingErrorBoundary : ErrorBoundary
{
    // Property (not const) so razor's @if doesn't fold it into unreachable-code warnings.
    public static bool ShowDetail { get; } =
#if DEBUG
        true;
#else
        false;
#endif

    protected override Task OnErrorAsync(Exception exception)
    {
#if DEBUG
        Services.CrashLog.Write("blazor-boundary", exception);
#endif
        return base.OnErrorAsync(exception);
    }
}
