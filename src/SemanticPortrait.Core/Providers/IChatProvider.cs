namespace SemanticPortrait.Core;

/// <summary>
/// Per-million-token USD rates for a model. Cached input (prompt-cache hits) bills at the
/// cheaper <paramref name="CachedInputPerM"/> rate; the rest of the input bills at <paramref name="InputPerM"/>.
/// </summary>
public readonly record struct ModelPricing(double InputPerM, double OutputPerM, double CachedInputPerM = 0)
{
    /// <summary>USD cost for one call. <paramref name="cachedInput"/> is the cached subset of <paramref name="input"/>.</summary>
    public double CostUsd(long input, long output, long cachedInput = 0)
    {
        var fresh = Math.Max(0, input - cachedInput);
        return fresh       / 1_000_000.0 * InputPerM
             + cachedInput / 1_000_000.0 * CachedInputPerM
             + output      / 1_000_000.0 * OutputPerM;
    }
}

/// <summary>
/// A streaming chat provider with tool-calling. Abstraction so Claude / Kimi / GLM / DeepSeek can
/// drop in behind the same interface (OpenAIClient is the current implementation).
/// </summary>
public interface IChatProvider
{
    /// <summary>Stable id for selection/persistence (e.g. "openai", "claude", "kimi").</summary>
    string ProviderId { get; }

    /// <summary>Human-facing label for the picker + topbar chip (e.g. "OpenAI · GPT-5.5").</summary>
    string DisplayName { get; }

    bool HasKey { get; }

    /// <summary>The chat model id this provider streams from (e.g. "gpt-5.5").</summary>
    string ModelName { get; }

    /// <summary>Per-token USD rates for <see cref="ModelName"/>, used for spend tracking.</summary>
    ModelPricing Pricing { get; }

    /// <remarks>
    /// Errors (missing key, unreachable server, HTTP failures) are yielded as bracketed text so
    /// chat UIs can display them inline — but they are ALSO reported via <paramref name="onError"/>.
    /// Any caller that PERSISTS the streamed text (compaction, analysis, classification) must pass
    /// onError and discard the output when it fires, or error text ends up stored as content.
    /// </remarks>
    IAsyncEnumerable<string> StreamReplyAsync(
        string systemPrompt,
        IEnumerable<ChatMessage> history,
        IReadOnlyList<object>? tools = null,
        Func<string, string, Task<string>>? toolExecutor = null,
        Action<string>? onReasoning = null,
        string effort = "low",
        Action<string>? onError = null,
        CancellationToken ct = default);
}
