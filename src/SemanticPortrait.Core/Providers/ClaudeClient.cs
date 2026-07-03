using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;

namespace SemanticPortrait.Core;

/// <summary>
/// Claude provider over the Anthropic Messages API (official SDK): SSE token streaming, the
/// tool-call loop, adaptive thinking (summaries surfaced via onReasoning), and real usage-based
/// cost tracking. Mirrors the contract of <see cref="OpenAIClient"/>: errors are reported via
/// onError AND yielded as bracketed text for inline chat display.
/// </summary>
public sealed class ClaudeClient : IChatProvider
{
    private const int MaxToolRounds = 6;
    private const long MaxOutputTokens = 16_000;

    private readonly UsageTracker _usage;
    private readonly LlmConfig _cfg;

    public ClaudeClient(UsageTracker usage, LlmConfig cfg) { _usage = usage; _cfg = cfg; }

    public string ProviderId => "anthropic";
    public string DisplayName => "Claude · " + _cfg.SelectedModel("anthropic").Name;
    public bool HasKey => _cfg.HasKey("anthropic");
    public string ModelName => _cfg.SelectedModelId("anthropic");
    public ModelPricing Pricing => _cfg.SelectedModel("anthropic").Pricing
        ?? new ModelPricing(5.00, 25.00, 0.50);   // claude-opus-4-8 list rates

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
        var key = _cfg.GetKey("anthropic");
        if (string.IsNullOrWhiteSpace(key))
        {
            const string noKey = "[no Anthropic API key — add one in ⋯ → LLM settings (or set `anthropic=sk-ant-...` in .env)]";
            onError?.Invoke(noKey);
            yield return noKey;
            yield break;
        }

        var model = ModelName;
        var pricing = Pricing;
        var sdk = new AnthropicClient { ApiKey = key };
        var sdkTools = ToTools(tools);

        // Growing conversation: history + (assistant turn incl. thinking/tool_use + tool results) per round.
        var messages = new List<MessageParam>();
        foreach (var m in history)
            messages.Add(new MessageParam
            {
                Role = m.Role == "user" ? Role.User : Role.Assistant,
                Content = m.Content,
            });

        for (int round = 0; round < MaxToolRounds; round++)
        {
            // Adaptive thinking + effort only on models that support them (Haiku 4.5 rejects both).
            var supportsThinking = !model.StartsWith("claude-haiku", StringComparison.OrdinalIgnoreCase);
            var p = new MessageCreateParams
            {
                Model = model,
                MaxTokens = MaxOutputTokens,
                System = systemPrompt,
                Messages = messages,
                Tools = sdkTools,
                Thinking = supportsThinking
                    ? (ThinkingConfigParam)new ThinkingConfigAdaptive { Display = Display.Summarized }
                    : null,
                OutputConfig = supportsThinking ? new OutputConfig { Effort = MapEffort(effort) } : null,
            };

            // Per-round accumulation. Assistant content blocks must be echoed back VERBATIM on the
            // next round (thinking blocks carry a signature the API validates).
            var blocks = new SortedDictionary<long, BlockAcc>();
            var reasoning = new StringBuilder();
            string? error = null;
            long inTok = 0, cacheRead = 0, cacheWrite = 0, outTok = 0;

            var e = sdk.Messages.CreateStreaming(p).GetAsyncEnumerator(ct);
            try
            {
                while (true)
                {
                    bool moved;
                    string? text = null;
                    try { moved = await e.MoveNextAsync(); }
                    catch (Exception ex)
                    {
                        error = $"[Claude error: {Truncate(ex.Message, 300)}]";
                        break;
                    }
                    if (!moved) break;

                    var ev = e.Current;
                    if (ev.TryPickStart(out var start))
                    {
                        var u = start.Message.Usage;
                        inTok = u.InputTokens;
                        cacheRead = u.CacheReadInputTokens ?? 0;
                        cacheWrite = u.CacheCreationInputTokens ?? 0;
                    }
                    else if (ev.TryPickContentBlockStart(out var bs))
                    {
                        var acc = new BlockAcc();
                        if (bs.ContentBlock.TryPickText(out _)) acc.Kind = BlockKind.Text;
                        else if (bs.ContentBlock.TryPickThinking(out _)) acc.Kind = BlockKind.Thinking;
                        else if (bs.ContentBlock.TryPickToolUse(out var tu))
                        {
                            acc.Kind = BlockKind.ToolUse;
                            acc.ToolId = tu.ID;
                            acc.ToolName = tu.Name;
                        }
                        else acc.Kind = BlockKind.Other;
                        blocks[bs.Index] = acc;
                    }
                    else if (ev.TryPickContentBlockDelta(out var bd) && blocks.TryGetValue(bd.Index, out var acc2))
                    {
                        if (bd.Delta.TryPickText(out var td)) { acc2.Content.Append(td.Text); text = td.Text; }
                        else if (bd.Delta.TryPickThinking(out var th)) { acc2.Content.Append(th.Thinking); reasoning.Append(th.Thinking); }
                        else if (bd.Delta.TryPickInputJson(out var ij)) acc2.Content.Append(ij.PartialJson);
                        else if (bd.Delta.TryPickSignature(out var sig)) acc2.Signature += sig.Signature;
                    }
                    else if (ev.TryPickDelta(out var md))
                    {
                        outTok = md.Usage.OutputTokens;
                    }

                    if (text is not null) yield return text;
                }
            }
            finally { await e.DisposeAsync(); }

            // Anthropic reports uncached/cache-read/cache-write input separately; fold them into
            // our (input, cached-subset) pricing model. Cache writes bill at ≥1× — counted as fresh.
            var totalIn = inTok + cacheRead + cacheWrite;
            if (totalIn > 0 || outTok > 0)
                _usage.Record(model, totalIn, outTok, pricing.CostUsd(totalIn, outTok, cacheRead));

            if (reasoning.Length > 0) onReasoning?.Invoke(reasoning.ToString());

            if (error is not null)
            {
                onError?.Invoke(error);
                yield return error;
                yield break;
            }

            var calls = blocks.Values.Where(b => b.Kind == BlockKind.ToolUse).ToList();
            if (calls.Count == 0 || toolExecutor is null) yield break;

            // Echo the assistant turn verbatim (thinking + text + tool_use), then the tool results.
            var assistant = new List<ContentBlockParam>();
            foreach (var b in blocks.Values)
            {
                switch (b.Kind)
                {
                    case BlockKind.Thinking:
                        assistant.Add(new ThinkingBlockParam { Thinking = b.Content.ToString(), Signature = b.Signature ?? "" });
                        break;
                    case BlockKind.Text when b.Content.Length > 0:
                        assistant.Add(new TextBlockParam { Text = b.Content.ToString() });
                        break;
                    case BlockKind.ToolUse:
                        assistant.Add(new ToolUseBlockParam
                        {
                            ID = b.ToolId ?? "",
                            Name = b.ToolName ?? "",
                            Input = ParseInput(b.Content.ToString()),
                        });
                        break;
                }
            }
            messages.Add(new MessageParam { Role = Role.Assistant, Content = assistant });

            var results = new List<ContentBlockParam>();
            foreach (var call in calls)
            {
                string result;
                try { result = await toolExecutor(call.ToolName ?? "", call.Content.ToString()); }
                catch (Exception ex) { result = "error: " + ex.Message; }
                results.Add(new ToolResultBlockParam { ToolUseID = call.ToolId ?? "", Content = result });
            }
            messages.Add(new MessageParam { Role = Role.User, Content = results });
        }

        const string capped = "\n[stopped: tool-call round limit reached]";
        onError?.Invoke(capped);
        yield return capped;
    }

    private static Effort MapEffort(string effort) => effort switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        _ => Effort.High,
    };

    private static Dictionary<string, JsonElement> ParseInput(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                string.IsNullOrWhiteSpace(json) ? "{}" : json) ?? new();
        }
        catch { return new(); }
    }

    /// <summary>Reshape the app's flat Responses-style tool specs into Anthropic Tool definitions.</summary>
    internal static List<ToolUnion>? ToTools(IReadOnlyList<object>? tools)
    {
        if (tools is null || tools.Count == 0) return null;
        var list = new List<ToolUnion>(tools.Count);
        foreach (var t in tools)
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(t));
            var r = doc.RootElement;
            Dictionary<string, JsonElement>? props = null;
            List<string>? required = null;
            if (r.TryGetProperty("parameters", out var pa))
            {
                if (pa.TryGetProperty("properties", out var pr))
                {
                    props = new Dictionary<string, JsonElement>();
                    foreach (var prop in pr.EnumerateObject()) props[prop.Name] = prop.Value.Clone();
                }
                if (pa.TryGetProperty("required", out var req))
                    required = req.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
            }
            list.Add(new Tool
            {
                Name = r.GetProperty("name").GetString() ?? "",
                Description = r.TryGetProperty("description", out var de) ? de.GetString() : null,
                InputSchema = new InputSchema { Properties = props, Required = required },
            });
        }
        return list;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private enum BlockKind { Text, Thinking, ToolUse, Other }

    private sealed class BlockAcc
    {
        public BlockKind Kind;
        public string? ToolId;
        public string? ToolName;
        public string? Signature;
        public StringBuilder Content = new();
    }
}
