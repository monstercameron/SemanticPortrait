using System.Net.Http;

namespace SemanticPortrait.Core;

/// <summary>
/// Local, 100%-offline provider backed by LM Studio's OpenAI-compatible server (by default
/// http://localhost:1234/v1). No data leaves the machine, so it is NOT wrapped with masking and
/// pricing is zero. The streaming/tool-loop core lives in <see cref="OpenAICompatChatClient"/>;
/// this subclass adds server discovery (ping + loaded-model listing) for the settings UI.
/// </summary>
public sealed class LMStudioClient : OpenAICompatChatClient
{
    private static readonly ModelPricing Free = new(0, 0, 0);

    public LMStudioClient(HttpClient http, UsageTracker usage, LlmConfig cfg)
        : base(http, usage, cfg, "lmstudio", "LM Studio", "http://localhost:1234/v1", requiresKey: false) { }

    public override ModelPricing Pricing => Free;
}
