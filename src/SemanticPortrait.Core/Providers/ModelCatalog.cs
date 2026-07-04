namespace SemanticPortrait.Core;

/// <summary>One selectable model with its (optional) known pricing.</summary>
public sealed record LlmModel(string Id, string Name, ModelPricing? Pricing, string Note = "");

/// <summary>
/// A provider and the models it offers. <see cref="Connected"/> = wired to a working client today
/// (only OpenAI for now); others are listed so keys/selection can be staged ahead of support.
/// </summary>
public sealed record LlmProvider(
    string Id, string Name, string KeyEnv, string KeyHint, string SignupUrl,
    bool Connected, IReadOnlyList<LlmModel> Models, bool Local = false);

/// <summary>
/// Static catalog of inference providers + models surfaced in the LLM-settings modal. Prices are
/// per-1M-token USD list rates; only rates we can state with confidence are filled in (others show
/// "—" until the provider is wired up). Extend by adding entries here.
/// </summary>
public static class ModelCatalog
{
    public static readonly IReadOnlyList<LlmProvider> Providers = new LlmProvider[]
    {
        new("openai", "OpenAI", "openai", "sk-…", "https://platform.openai.com/api-keys", true, new LlmModel[]
        {
            new("gpt-5.5", "GPT-5.5", new ModelPricing(5.00, 30.00, 0.50), "Flagship reasoning"),
        }),
        new("lmstudio", "LM Studio · Local", "lmstudio", "", "https://lmstudio.ai", true, new LlmModel[]
        {
            new("local-model", "Loaded model", new ModelPricing(0, 0, 0), "chat runs offline · free"),
        }, Local: true),
        new("anthropic", "Anthropic · Claude", "anthropic", "sk-ant-…", "https://console.anthropic.com/settings/keys", true, new LlmModel[]
        {
            new("claude-opus-4-8", "Claude Opus 4.8", new ModelPricing(5.00, 25.00, 0.50), "Most capable Opus"),
            // intro pricing ($2/$10) runs through 2026-08-31; sticker is $3/$15 after
            new("claude-sonnet-5", "Claude Sonnet 5", new ModelPricing(2.00, 10.00, 0.20), "Near-Opus, cheaper (intro rates)"),
            new("claude-haiku-4-5", "Claude Haiku 4.5", new ModelPricing(1.00, 5.00, 0.10), "Fast + cheap"),
        }),
        // Stretch providers (OpenAI-compatible chat completions). Rates aren't tracked yet —
        // costs display as $0; verify the model id on the provider's console if a request 404s.
        new("moonshot", "Moonshot · Kimi", "moonshot", "sk-…", "https://platform.moonshot.ai/console/api-keys", true, new LlmModel[]
        {
            new("kimi-k2-0905-preview", "Kimi K2", null, "rates not tracked"),
        }),
        new("zhipu", "Zhipu · GLM", "zhipu", "…", "https://open.bigmodel.cn/usercenter/apikeys", true, new LlmModel[]
        {
            new("glm-4.6", "GLM-4.6", null, "rates not tracked"),
        }),
        new("deepseek", "DeepSeek", "deepseek", "sk-…", "https://platform.deepseek.com/api_keys", true, new LlmModel[]
        {
            new("deepseek-chat", "DeepSeek Chat (V3)", null, "rates not tracked"),
            new("deepseek-reasoner", "DeepSeek Reasoner (R1)", null, "rates not tracked"),
        }),
    };

    public static LlmProvider? Find(string id) => Providers.FirstOrDefault(p => p.Id == id);
}
