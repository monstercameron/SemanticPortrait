using Microsoft.Extensions.Logging;
using SemanticPortrait.App.Services;
using SemanticPortrait.Core;
#if WINDOWS
using Microsoft.Maui.LifecycleEvents;
#endif

namespace SemanticPortrait.App;

public static class MauiProgram
{
#if WINDOWS
	// Held for the process lifetime — owning it means we are the single running instance.
	private static Mutex? _singleInstance;
#endif

	public static MauiApp CreateMauiApp()
	{
#if WINDOWS
		// Single instance: a second launch wakes the running one (tray broadcast) and exits.
		_singleInstance = new Mutex(true, @"Local\SemanticPortrait.SingleInstance", out var isFirst);
		if (!isFirst)
		{
			// We hold foreground rights (the user just launched us) — surface the running
			// instance's window ourselves, then hand off. Without this the handoff is
			// invisible and a second launch reads as "it crashed".
			TrayService.SurfaceExisting();
#if DEBUG
			CrashLog.WriteLine($"--- second instance {DateTime.Now:HH:mm:ss}: surfaced the running app and exited (this is NOT a crash) ---");
#endif
			Environment.Exit(0);
		}
#endif
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

#if WINDOWS
		// Extend the app content under the native title bar so the lock-screen aurora (and the
		// topbar) shine all the way to the top edge. The min/max/close caption buttons stay,
		// drawn on a transparent background; the in-app topbar becomes the draggable title bar.
		builder.ConfigureLifecycleEvents(events =>
		{
			events.AddWindows(w => w.OnWindowCreated(window =>
			{
				// Apply AFTER MAUI finishes wiring its own title bar (it overrides OnWindowCreated),
				// once, on first activation — so the transparent caption actually sticks.
				var applied = false;
				void Apply()
				{
					if (applied) return;
					applied = true;
					window.ExtendsContentIntoTitleBar = true;
					var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
					var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
					var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
					var tb = appWindow.TitleBar;
					tb.ExtendsContentIntoTitleBar = true;
					var transparent = Windows.UI.Color.FromArgb(0, 0, 0, 0);
					tb.BackgroundColor = transparent;
					tb.InactiveBackgroundColor = transparent;
					tb.ButtonBackgroundColor = transparent;
					tb.ButtonInactiveBackgroundColor = transparent;
					tb.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(40, 128, 128, 128);
				}
				window.Activated += (_, _) => Apply();

				// Tray presence: closing hides to the tray (timers, reminders, queued analysis
				// keep running); the tray menu / a second launch / Ctrl+Alt+J bring it back;
				// Quit (tray menu or in-app) really exits.
				var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
				var winId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
				var appWin = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(winId);
				var tray = new TrayService();
				tray.Attach(hwnd,
					onOpen: () => window.DispatcherQueue.TryEnqueue(() =>
					{
						appWin.Show();
						TrayService.SurfaceWindow(hwnd);
					}),
					onQuit: () => window.DispatcherQueue.TryEnqueue(() =>
					{
						TrayService.ReallyQuit = true;
						tray.Dispose();
						Microsoft.UI.Xaml.Application.Current.Exit();
					}));
				appWin.Closing += (s, e) =>
				{
					if (TrayService.ReallyQuit) { tray.Dispose(); return; }
					e.Cancel = true;
					s.Hide();
					tray.ShowFirstHideHint();   // first hide per run announces itself
				};
				// The in-app menu's explicit "Hide to tray" — identical to the titlebar ✕ path.
				TrayService.HideToTray = () => window.DispatcherQueue.TryEnqueue(() =>
				{
					appWin.Hide();
					tray.ShowFirstHideHint();
				});
			}));
		});
#endif

		builder.Services.AddMauiBlazorWebView();

		var dataDir = FileSystem.AppDataDirectory;
		builder.Services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromMinutes(5) });
		builder.Services.AddSingleton(new Db(Path.Combine(dataDir, "semanticportrait.db")));
		builder.Services.AddSingleton(sp => new UsageTracker(sp.GetRequiredService<Db>()));
		builder.Services.AddSingleton<LlmConfig>();
		builder.Services.AddSingleton<OpenAIClient>();
		// Egress masking: on/off from the onboarding consent (persisted). RegexMasker now; on-device NER later.
		builder.Services.AddSingleton<IMasker>(sp => new RegexMasker(
			sp.GetRequiredService<Db>(),
			enabled: () => Microsoft.Maui.Storage.Preferences.Default.Get("masking", true)));
		// Embeddings: local MiniLM when installed (nothing leaves, free); else cloud — masked,
		// because embedding text otherwise bypasses the egress consent.
		builder.Services.AddSingleton(new LocalEmbedder(Path.Combine(dataDir, "models", "minilm")));
		builder.Services.AddSingleton<IEmbedder>(sp => new PreferLocalEmbedder(
			sp.GetRequiredService<LocalEmbedder>(),
			new MaskingEmbedder(sp.GetRequiredService<OpenAIClient>(), sp.GetRequiredService<IMasker>())));
		// Register every chat provider here; the registry surfaces them for selection.
		// Cloud providers are wrapped with egress masking; LM Studio is 100% local so it isn't.
		builder.Services.AddSingleton<IChatProvider>(sp =>
			new MaskingChatProvider(sp.GetRequiredService<OpenAIClient>(), sp.GetRequiredService<IMasker>()));
		builder.Services.AddSingleton<ClaudeClient>();
		builder.Services.AddSingleton<IChatProvider>(sp =>
			new MaskingChatProvider(sp.GetRequiredService<ClaudeClient>(), sp.GetRequiredService<IMasker>()));
		// Stretch cloud providers ride the OpenAI-compatible chat-completions client; masked like
		// every cloud provider. Default base URLs are overridable in ⋯ → LLM settings.
		foreach (var (id, name, url) in new[]
		{
			("moonshot", "Kimi", "https://api.moonshot.ai/v1"),
			("zhipu", "GLM", "https://open.bigmodel.cn/api/paas/v4"),
			("deepseek", "DeepSeek", "https://api.deepseek.com/v1"),
		})
		{
			builder.Services.AddSingleton<IChatProvider>(sp => new MaskingChatProvider(
				new OpenAICompatChatClient(sp.GetRequiredService<HttpClient>(),
					sp.GetRequiredService<UsageTracker>(), sp.GetRequiredService<LlmConfig>(),
					id, name, url, requiresKey: true),
				sp.GetRequiredService<IMasker>()));
		}
		builder.Services.AddSingleton<LMStudioClient>();
		builder.Services.AddSingleton<IChatProvider>(sp => sp.GetRequiredService<LMStudioClient>());
		builder.Services.AddSingleton(sp => new ProviderRegistry(
			sp.GetServices<IChatProvider>(),
			readSelected: () => Microsoft.Maui.Storage.Preferences.Default.Get<string?>("provider", null),
			writeSelected: id => Microsoft.Maui.Storage.Preferences.Default.Set("provider", id)));
		// Profile lives in the encrypted DB; the path is only the legacy plaintext file to migrate.
		builder.Services.AddSingleton(sp => new ProfileStore(
			sp.GetRequiredService<Db>(), Path.Combine(dataDir, "profile.json")));
		builder.Services.AddSingleton(new KeyVault(Path.Combine(dataDir, "keyvault.json")));
		builder.Services.AddSingleton(new HelloKeyStore(Path.Combine(dataDir, "hello.bin")));
		builder.Services.AddSingleton<WindowsHello>();
		builder.Services.AddSingleton<ProfileTools>();
		builder.Services.AddSingleton<MemoryTools>();
#if WINDOWS
		builder.Services.AddSingleton<IToastScheduler, WindowsToastScheduler>();
#else
		builder.Services.AddSingleton<IToastScheduler, NullToastScheduler>();
#endif
		builder.Services.AddSingleton<ToastActivationService>();
		builder.Services.AddSingleton<NotificationService>();
		// TaskTools gets the evening-check-in setting via delegates (Core can't reach Preferences).
		builder.Services.AddSingleton(sp => new TaskTools(
			sp.GetRequiredService<Db>(), sp.GetRequiredService<NotificationService>(),
			getCheckinHour: () => Microsoft.Maui.Storage.Preferences.Default.Get("checkin_hour", 0),
			setCheckinHour: h => Microsoft.Maui.Storage.Preferences.Default.Set("checkin_hour", h)));
		// Read-only privacy awareness for the agent (report, never toggle — consent stays the user's).
		builder.Services.AddSingleton(sp => new PrivacyTools(
			() => Microsoft.Maui.Storage.Preferences.Default.Get("masking", true),
			sp.GetRequiredService<ProviderRegistry>(),
			() => ((PreferLocalEmbedder)sp.GetRequiredService<IEmbedder>()).LocalActive));
		builder.Services.AddSingleton<ProgramTools>();
		builder.Services.AddSingleton<GraphTools>();
		builder.Services.AddSingleton<EntryTools>();
		builder.Services.AddSingleton<PredictionTools>();
		builder.Services.AddSingleton<EntityResolver>();
		builder.Services.AddSingleton<EmbeddingBackfill>();
		builder.Services.AddSingleton<RecallEngine>();
		builder.Services.AddSingleton<RecallTools>();
		builder.Services.AddSingleton<IntakeTools>();
		// Long-lived so its semantic memo caches (mood→emotion, ref→nodes) persist across rebuilds.
		// LOCAL embedder only — deliberately NOT the PreferLocal/cloud chain: toggling a view must
		// never egress moods/topics, and the cloud path's dozens of sequential HTTPS embed calls
		// hung the whole build when the local model wasn't downloaded (observed as an app "crash").
		// Without the local model the source degrades to lexicon + token-join and stays instant.
		builder.Services.AddSingleton(sp => new SemanticPortrait.Core.Constellation.DbConstellationSource(
			sp.GetRequiredService<Db>(), sp.GetRequiredService<LocalEmbedder>()));
		builder.Services.AddSingleton<ExportService>();
		builder.Services.AddSingleton<Compactor>();
		builder.Services.AddSingleton<TraceLog>();
		builder.Services.AddSingleton<AnalystSubagent>();

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
		// Dev crash log → AppData/crashlog.txt (AppDomain + tasks + Blazor error channel;
		// the WinUI dispatcher hook lives in Platforms/Windows/App.xaml.cs).
		CrashLog.Install();
		builder.Logging.AddProvider(new CrashLogLoggerProvider());
		// Swallowed-but-reported errors (DevTrap sites) → crashlog with full stack. The
		// on-screen ⚠️ bubble subscription lives in Home (it needs the chat surface).
		DevTrap.Trapped += (src, ex) => CrashLog.Write($"devtrap:{src}", ex, fatal: false);
#endif

		return builder.Build();
	}
}
