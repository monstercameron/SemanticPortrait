namespace SemanticPortrait.App;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		// The Win32 title never renders (ExtendsContentIntoTitleBar draws the in-app brand
		// instead) but it MUST be set: FindWindow-based single-instance activation and the
		// alt-tab label depend on it. An empty title made second launches look like crashes.
		var window = new Window(new MainPage()) { Title = "SemanticPortrait" };
		// Attention tracking: reminder delivery routes by focus (in-thread when focused,
		// OS toast when not) — see SemanticPortrait.Core.AppFocus.
		window.Activated += (_, _) => SemanticPortrait.Core.AppFocus.IsFocused = true;
		window.Deactivated += (_, _) => SemanticPortrait.Core.AppFocus.IsFocused = false;
		return window;
	}
}
