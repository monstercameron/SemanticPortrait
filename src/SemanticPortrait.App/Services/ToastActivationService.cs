namespace SemanticPortrait.App.Services;

/// <summary>
/// Bridges OS toast-activation (a background COM thread, possibly during cold-start before the UI
/// exists) to the Blazor UI. App.xaml.cs raises activations here; Home.razor subscribes. If no
/// subscriber is attached yet (cold start), the argument is held in <see cref="PendingArg"/> and the
/// component drains it once it initializes.
/// </summary>
public sealed class ToastActivationService
{
    private readonly object _gate = new();
    private string? _pending;

    /// <summary>Raised (on whatever thread the toast fired) when the user activates a toast.</summary>
    public event Action<string>? Activated;

    /// <summary>An activation that arrived before any subscriber was attached (cold start).</summary>
    public string? PendingArg
    {
        get { lock (_gate) { var p = _pending; _pending = null; return p; } }
    }

    public void RaiseActivation(string argument)
    {
        if (string.IsNullOrEmpty(argument)) return;
        var handler = Activated;
        if (handler is null) { lock (_gate) { _pending = argument; } }
        else handler(argument);
    }
}
