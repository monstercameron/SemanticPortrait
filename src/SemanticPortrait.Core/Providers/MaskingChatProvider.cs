using System.Runtime.CompilerServices;
using System.Text;

namespace SemanticPortrait.Core;

/// <summary>
/// Wraps any <see cref="IChatProvider"/> with egress masking. When the masker is enabled, ALL
/// outbound text is pseudonymized before it leaves the machine — the system prompt (which carries
/// the rolling compaction summary), the conversation history, and tool results — and the model's
/// streamed reply, reasoning, and the args it passes to tools are restored from tokens. When the
/// masker is off this is a transparent pass-through. Metadata (id/name/pricing) delegates to the
/// inner provider so the registry + cost tracking are unaffected.
/// </summary>
public sealed class MaskingChatProvider : IChatProvider
{
    private readonly IChatProvider _inner;
    private readonly IMasker _masker;

    public MaskingChatProvider(IChatProvider inner, IMasker masker) { _inner = inner; _masker = masker; }

    public string ProviderId => _inner.ProviderId;
    public string DisplayName => _inner.DisplayName;
    public bool HasKey => _inner.HasKey;
    public string ModelName => _inner.ModelName;
    public ModelPricing Pricing => _inner.Pricing;

    public async IAsyncEnumerable<string> StreamReplyAsync(
        string systemPrompt,
        IEnumerable<ChatMessage> history,
        IReadOnlyList<object>? tools = null,
        Func<string, string, Task<string>>? toolExecutor = null,
        Action<string>? onReasoning = null,
        string effort = "low",
        Action<string>? onError = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_masker.Enabled)
        {
            await foreach (var t in _inner.StreamReplyAsync(systemPrompt, history, tools, toolExecutor, onReasoning, effort, onError, ct))
                yield return t;
            yield break;
        }

        var maskedSystem = _masker.Mask(systemPrompt);
        var maskedHistory = history.Select(m => new ChatMessage(m.Role, _masker.Mask(m.Content))).ToList();

        // Tools receive real values (unmask args) and their PII-bearing results are masked on the way back.
        Func<string, string, Task<string>>? wrappedExec = toolExecutor is null ? null
            : async (name, args) => _masker.Mask(await toolExecutor(name, _masker.Unmask(args)));

        Action<string>? wrappedReason = onReasoning is null ? null : r => onReasoning(_masker.Unmask(r));

        // Rolling unmask: hold back the trailing word-in-progress so a token split across stream
        // chunks is never half-restored; flush + unmask completed text on each chunk.
        var pending = new StringBuilder();
        await foreach (var chunk in _inner.StreamReplyAsync(maskedSystem, maskedHistory, tools, wrappedExec, wrappedReason, effort, onError, ct))
        {
            pending.Append(chunk);
            var s = pending.ToString();
            int keep = s.Length;
            while (keep > 0 && IsWordChar(s[keep - 1])) keep--;   // trailing identifier run may be a partial token
            if (keep > 0)
            {
                yield return _masker.Unmask(s[..keep]);
                pending.Clear();
                pending.Append(s[keep..]);
            }
        }
        if (pending.Length > 0) yield return _masker.Unmask(pending.ToString());
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
