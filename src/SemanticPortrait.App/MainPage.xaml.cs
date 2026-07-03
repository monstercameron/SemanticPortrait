namespace SemanticPortrait.App;

public partial class MainPage : ContentPage
{
	public MainPage()
	{
		InitializeComponent();

#if WINDOWS && DEBUG
		// A dying WebView2 renderer/GPU process leaves NO managed trace — no exception, no
		// boundary, no DevTrap; the app "crashes" (blank/frozen page) with every channel silent.
		// This is the only seam that sees it. Log the failure kind + exit code, then let the
		// built-in recovery (or the user's restart) proceed.
		blazorWebView.BlazorWebViewInitialized += (_, e) =>
		{
			e.WebView.CoreWebView2.ProcessFailed += (_, pf) =>
				Services.CrashLog.Write("webview-process-failed", new InvalidOperationException(
					$"WebView2 process failed: kind={pf.ProcessFailedKind}, reason={pf.Reason}, " +
					$"exitCode={pf.ExitCode}, description='{pf.ProcessDescription}'"), fatal: false);
		};
#endif
	}
}
