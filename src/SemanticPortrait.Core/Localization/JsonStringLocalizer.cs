using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Localization;

namespace SemanticPortrait.Core.Localization;

/// <summary>
/// Minimal IStringLocalizer backed by a flat JSON dictionary (key -> English text) shipped as
/// an embedded resource in this assembly. Chosen over .resx + ResourceManager because MAUI
/// single-project resx embedding (satellite assemblies, SourceGen XAML inflator, the special
/// "Resources" asset globs) is finicky and hard to unit-test without loading the compiled app;
/// a JSON blob loaded via Assembly.GetManifestResourceStream is deterministic in both the app
/// and the plain-library test project, and trivial to hand-edit.
///
/// A missing key returns the key itself (LocalizedString.ResourceNotFound = true) — callers
/// never see an exception or a blank string.
/// </summary>
public sealed class JsonStringLocalizer : IStringLocalizer
{
    private readonly IReadOnlyDictionary<string, string> _map;

    public JsonStringLocalizer(IReadOnlyDictionary<string, string> map) => _map = map;

    /// <summary>Loads a flat string->string JSON object embedded in <paramref name="assembly"/>
    /// (defaults to this assembly) under the given manifest logical name.</summary>
    public static JsonStringLocalizer FromEmbeddedResource(string logicalName, Assembly? assembly = null)
    {
        assembly ??= typeof(JsonStringLocalizer).Assembly;
        using var stream = assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException(
                $"Localization resource '{logicalName}' not found in {assembly.FullName}.");
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? new Dictionary<string, string>();
        return new JsonStringLocalizer(map);
    }

    public LocalizedString this[string name]
    {
        get
        {
            var found = _map.TryGetValue(name, out var value);
            return new LocalizedString(name, found ? value! : name, resourceNotFound: !found);
        }
    }

    public LocalizedString this[string name, params object[] arguments]
    {
        get
        {
            var found = _map.TryGetValue(name, out var value);
            var text = found ? string.Format(value!, arguments) : name;
            return new LocalizedString(name, text, resourceNotFound: !found);
        }
    }

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
        _map.Select(kv => new LocalizedString(kv.Key, kv.Value));
}

/// <summary>
/// IStringLocalizerFactory that always hands back the single shared JSON-backed localizer —
/// there's only one resource catalog in this app (no per-feature resx split), so the
/// resourceSource/baseName/location parameters are irrelevant and ignored.
/// </summary>
public sealed class JsonStringLocalizerFactory : IStringLocalizerFactory
{
    public const string DefaultLogicalName = "SemanticPortrait.Core.Localization.Strings.en.json";

    private readonly Lazy<JsonStringLocalizer> _shared;

    public JsonStringLocalizerFactory(string logicalName = DefaultLogicalName, Assembly? assembly = null) =>
        _shared = new Lazy<JsonStringLocalizer>(() => JsonStringLocalizer.FromEmbeddedResource(logicalName, assembly));

    public IStringLocalizer Create(Type resourceSource) => _shared.Value;
    public IStringLocalizer Create(string baseName, string location) => _shared.Value;
}
