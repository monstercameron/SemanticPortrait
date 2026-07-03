using System.Collections.Concurrent;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.UI.Xaml;
using SemanticPortrait.App.Services;
using SemanticPortrait.Core;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace SemanticPortrait.App.WinUI;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : MauiWinUIApplication
{
	// Toast activations can arrive on a background COM thread during cold-start, before MAUI's DI
	// container exists. Buffer them here and drain once the app has launched.
	private static readonly ConcurrentQueue<string> _pendingToastArgs = new();

	/// <summary>
	/// Initializes the singleton application object.  This is the first line of authored code
	/// executed, and as such is the logical equivalent of main() or WinMain().
	/// </summary>
	public App()
	{
		this.InitializeComponent();

#if DEBUG
		// XAML-dispatcher crashes (incl. Blazor exceptions marshaled to the UI thread) — the
		// most common way a MAUI Blazor app dies without a WER entry. Log, then let it crash.
		this.UnhandledException += (_, e) => CrashLog.Write("winui-dispatcher", e.Exception);
#endif

		// Unpackaged toast activation. The compat layer auto-registers the AUMID + COM activator
		// (and a Start-Menu shortcut) so scheduled toasts can cold-start the app on click.
		// Failure here = scheduled toasts silently stop working; surface it in dev builds.
		try { ToastNotificationManagerCompat.OnActivated += OnToastActivated; }
		catch (Exception e) { DevTrap.Report("toast-hookup", e); }
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

	protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
	{
		base.OnLaunched(args);
		DrainPendingToasts();   // DI is ready now — flush any cold-start activation
	}

	private void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
	{
		string arg;
		try { arg = ToastArguments.Parse(e.Argument)["a"]; }
		catch { arg = e.Argument ?? ""; }
		if (string.IsNullOrEmpty(arg)) return;

		var svc = ActivationSvc();
		if (svc is null) { _pendingToastArgs.Enqueue(arg); return; }
		Foreground();
		svc.RaiseActivation(arg);
	}

	private void DrainPendingToasts()
	{
		var svc = ActivationSvc();
		if (svc is null) return;
		var any = false;
		while (_pendingToastArgs.TryDequeue(out var a)) { svc.RaiseActivation(a); any = true; }
		if (any) Foreground();
	}

	private static ToastActivationService? ActivationSvc() =>
		IPlatformApplication.Current?.Services?.GetService<ToastActivationService>();

	// Best-effort: bring the existing single window to the foreground on activation.
	private static void Foreground()
	{
		try
		{
			Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
			{
				var win = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?
					.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
				win?.Activate();
			});
		}
		catch (Exception e) { DevTrap.Report("foreground-activate", e); }
	}
}
