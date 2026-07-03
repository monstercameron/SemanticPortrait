namespace SemanticPortrait.Core;

/// <summary>
/// Catalog of available chat providers + the active selection. This is the seam for plugging in
/// more providers later (Claude / Kimi / GLM / DeepSeek): implement <see cref="IChatProvider"/>,
/// register it in DI, and it shows up here. Active selection is persisted by ProviderId; the picker
/// UI and per-provider key entry hang off this. Today there's one provider (OpenAI).
/// </summary>
public sealed class ProviderRegistry
{
    private readonly IReadOnlyList<IChatProvider> _providers;
    private readonly Func<string?> _readSelected;
    private readonly Action<string> _writeSelected;

    /// <param name="readSelected">Returns the persisted provider id (null if unset).</param>
    /// <param name="writeSelected">Persists the chosen provider id.</param>
    public ProviderRegistry(IEnumerable<IChatProvider> providers,
        Func<string?>? readSelected = null, Action<string>? writeSelected = null)
    {
        _providers = providers.ToList();
        _readSelected = readSelected ?? (() => null);
        _writeSelected = writeSelected ?? (_ => { });
    }

    public IReadOnlyList<IChatProvider> Available => _providers;

    /// <summary>The selected provider, or the first available if none/unknown is persisted.</summary>
    public IChatProvider Active
    {
        get
        {
            var id = _readSelected();
            return _providers.FirstOrDefault(p => p.ProviderId == id) ?? _providers[0];
        }
    }

    /// <summary>Select a provider by id (no-op if unknown). Persisted; takes effect next launch.</summary>
    public bool Select(string providerId)
    {
        if (!_providers.Any(p => p.ProviderId == providerId)) return false;
        _writeSelected(providerId);
        return true;
    }
}
