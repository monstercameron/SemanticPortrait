using Microsoft.Extensions.Logging;

namespace SemanticPortrait.App.Services;

/// <summary>
/// Dev crash log: every channel a MAUI Blazor app can die through appends to
/// AppData/crashlog.txt with a timestamp and full exception (stack + inners).
/// Channels: AppDomain unhandled, unobserved task exceptions, the WinUI dispatcher
/// (Platforms/Windows/App.xaml.cs), and Blazor's Error/Critical logger output (which is
/// where "Unhandled exception rendering component" lands before the WebView dies).
/// Installed in DEBUG builds only — exception text can contain journal content, so it
/// stays out of Release. Synchronous append: the process may be about to die.
/// </summary>
public static class CrashLog
{
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(FileSystem.AppDataDirectory, "crashlog.txt");

    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("appdomain", e.ExceptionObject as Exception, fatal: e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("unobserved-task", e.Exception, fatal: false);
            e.SetObserved();   // observed → doesn't escalate; the log is the point
        };
        WriteLine($"--- app start {DateTime.Now:yyyy-MM-dd HH:mm:ss} (pid {Environment.ProcessId}) ---");
    }

    public static void Write(string source, Exception? ex, bool fatal = true)
    {
        WriteLine($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} [{source}]{(fatal ? " FATAL" : "")} ===\n{ex}\n");
        // Every crash-channel hit also fans out to DevTrap so dev builds can SHOW it on screen
        // in whatever view is active — errors buried in a log the user isn't reading looked like
        // "silent crashes" all day. Guard: DevTrap's own subscriber writes back here with a
        // "devtrap:" prefix; don't re-raise those or the two would ping-pong.
        if (ex is not null && !source.StartsWith("devtrap:"))
            SemanticPortrait.Core.DevTrap.Report($"crash:{source}", ex);
    }

    public static void WriteLine(string text)
    {
        try { lock (Gate) File.AppendAllText(FilePath, text + Environment.NewLine); } catch { }
    }
}

/// <summary>Routes Error/Critical log records (Blazor's crash channel) into the crash log.</summary>
public sealed class CrashLogLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new CrashLogger(categoryName);
    public void Dispose() { }

    private sealed class CrashLogger : ILogger
    {
        private readonly string _category;
        public CrashLogger(string category) => _category = category;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Error;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            CrashLog.WriteLine(
                $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} [log:{_category}] {level} ===\n" +
                $"{formatter(state, exception)}\n{exception}\n");
            if (exception is not null)   // Blazor render crashes land here — show them on screen too
                SemanticPortrait.Core.DevTrap.Report($"crash:log:{_category}", exception);
        }
    }
}
