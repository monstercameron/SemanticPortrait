using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SemanticPortrait.Core;

/// <summary>
/// Operational config: timeouts, compaction window/batch, background poll intervals, and a
/// couple of UI timing constants — the "how fast/how long" knobs an owner might want to tune
/// without a rebuild. Deliberately NOT here: algorithm constants, security floors (PBKDF2
/// iterations), or user preferences (those already live in Preferences/DB).
/// Every property has a DEFAULT equal to the value that used to be hard-coded, so a missing or
/// partial YAML file still yields today's exact behavior.
/// </summary>
public sealed record AppConfig
{
    public TimeoutOptions Timeouts { get; init; } = new();
    public CompactionOptions Compaction { get; init; } = new();
    public BackgroundOptions Background { get; init; } = new();
    public UiOptions Ui { get; init; } = new();

    /// <summary>
    /// Fail-safe load: never throws. A missing or malformed file yields defaults (mirrors
    /// KeyVault's Load pattern). On first run (file doesn't exist), best-effort writes a default
    /// template to <paramref name="path"/> so the user has something to edit.
    /// </summary>
    public static AppConfig Load(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(CamelCaseNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();
                var text = File.ReadAllText(path);
                return deserializer.Deserialize<AppConfig>(text) ?? new AppConfig();
            }
            catch { return new AppConfig(); }
        }

        var defaults = new AppConfig();
        try
        {
            var serializer = new SerializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build();
            File.WriteAllText(path, serializer.Serialize(defaults));
        }
        catch { /* best-effort — defaults still apply even if the template can't be written */ }
        return defaults;
    }
}

/// <summary>HttpClient / stream watchdog timeouts, in minutes.</summary>
public sealed record TimeoutOptions
{
    public int HttpClientMinutes { get; init; } = 5;
    public int ExportHttpMinutes { get; init; } = 10;
    public int ProactiveStreamMinutes { get; init; } = 4;
    public int MainStreamMinutes { get; init; } = 5;
    public int AnalystStreamMinutes { get; init; } = 8;
}

/// <summary>Thought-compaction window + per-call batch size.</summary>
public sealed record CompactionOptions
{
    public int WindowDays { get; init; } = 2;
    public int MaxBatch { get; init; } = 80;
}

/// <summary>Background poll intervals, in milliseconds.</summary>
public sealed record BackgroundOptions
{
    public int ReminderPollMs { get; init; } = 30000;
}

/// <summary>Small UI timing knobs.</summary>
public sealed record UiOptions
{
    public int StreamRepaintMs { get; init; } = 33;
    public int PinHoldSeconds { get; init; } = 30;
}
