namespace SemanticPortrait.Core;

/// <summary>
/// Runtime LLM configuration: per-provider API keys and model selection. Keys are stored in the
/// encrypted DB (so they're at rest behind the lock); a `.env` value is used as a dev fallback.
/// Model selection is also persisted in the DB so this stays MAUI-free (lives in Core).
/// </summary>
public sealed class LlmConfig
{
    private readonly Db _db;
    public LlmConfig(Db db) => _db = db;

    private static readonly ModelPricing Free = new(0, 0, 0);
    private static string KeyKey(string providerId)   => "llmkey:" + providerId;
    private static string ModelKey(string providerId) => "llmmodel:" + providerId;
    private static string UrlKey(string providerId)   => "llmurl:" + providerId;

    /// <summary>Base URL for a local/self-hosted provider (e.g. LM Studio), or null if unset.</summary>
    public string? GetBaseUrl(string providerId) => _db.GetSetting(UrlKey(providerId));
    public void SetBaseUrl(string providerId, string? url) => _db.SetSetting(UrlKey(providerId), url?.Trim());

    /// <summary>The API key for a provider: stored key first, else the `.env` fallback.</summary>
    public string? GetKey(string providerId)
    {
        var stored = _db.GetSetting(KeyKey(providerId));
        if (!string.IsNullOrWhiteSpace(stored)) return stored;
        var prov = ModelCatalog.Find(providerId);
        return prov is null ? null : EnvLoader.Get(prov.KeyEnv);
    }

    public bool HasKey(string providerId) => !string.IsNullOrWhiteSpace(GetKey(providerId));

    /// <summary>True when the key came from the user (stored), not the dev .env fallback.</summary>
    public bool HasStoredKey(string providerId) => !string.IsNullOrWhiteSpace(_db.GetSetting(KeyKey(providerId)));

    public void SetKey(string providerId, string? key) => _db.SetSetting(KeyKey(providerId), key?.Trim());

    /// <summary>The chosen model id for a provider, or the catalog default.</summary>
    public string SelectedModelId(string providerId)
    {
        var prov = ModelCatalog.Find(providerId);
        if (prov is null || prov.Models.Count == 0) return providerId;
        var stored = _db.GetSetting(ModelKey(providerId));
        // local providers (LM Studio) take any free-text model id the user loaded
        if (prov.Local) return string.IsNullOrWhiteSpace(stored) ? prov.Models[0].Id : stored!;
        return prov.Models.Any(m => m.Id == stored) ? stored! : prov.Models[0].Id;
    }

    /// <summary>The chosen model (record) for a provider, or the catalog default.</summary>
    public LlmModel SelectedModel(string providerId)
    {
        var prov = ModelCatalog.Find(providerId)!;
        var id = SelectedModelId(providerId);
        var known = prov.Models.FirstOrDefault(m => m.Id == id);
        if (known is not null) return known;
        // local free-text model not in the catalog → synthesize a zero-priced entry
        return new LlmModel(id, id, Free, prov.Local ? "local" : "");
    }

    public void SetModel(string providerId, string modelId) => _db.SetSetting(ModelKey(providerId), modelId);
}
