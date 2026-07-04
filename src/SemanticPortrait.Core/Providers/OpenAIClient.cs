using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace SemanticPortrait.Core;

public record ChatMessage(string Role, string Content);

/// <summary>
/// Minimal OpenAI Responses API client with token streaming and a tool-call loop.
/// Model: gpt-5.5, reasoning effort: low.
/// </summary>
public sealed class OpenAIClient : IChatProvider, IEmbedder
{
    private const string Endpoint = "https://api.openai.com/v1/responses";
    private const string Model = "gpt-5.5";
    private const int MaxToolRounds = 6;

    // OpenAI list pricing per 1M tokens (standard short-context), as of 2026-06.
    // gpt-5.5: $5 input / $30 output, cached input $0.50.
    public static readonly ModelPricing Gpt55Pricing = new(InputPerM: 5.00, OutputPerM: 30.00, CachedInputPerM: 0.50);
    // text-embedding-3-small: $0.02 / 1M (input only).
    private static readonly ModelPricing EmbedPricing = new(InputPerM: 0.02, OutputPerM: 0.00);

    private readonly HttpClient _http;
    private readonly UsageTracker _usage;
    private readonly LlmConfig _cfg;
    public OpenAIClient(HttpClient http, UsageTracker usage, LlmConfig cfg) { _http = http; _usage = usage; _cfg = cfg; }

    public string ProviderId => "openai";
    public string DisplayName => "OpenAI · " + _cfg.SelectedModel("openai").Name;
    public bool HasKey => _cfg.HasKey("openai");
    public string ModelName => _cfg.SelectedModelId("openai");
    public ModelPricing Pricing => _cfg.SelectedModel("openai").Pricing ?? Gpt55Pricing;

    private const string EmbedEndpoint = "https://api.openai.com/v1/embeddings";
    private const string EmbedModel = "text-embedding-3-small";

    /// <summary>Returns an embedding vector for the text, or null on failure.</summary>
    public async Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
    {
        var key = _cfg.GetKey("openai");
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(text)) return null;

        var payload = new { model = EmbedModel, input = text };
        using var req = new HttpRequestMessage(HttpMethod.Post, EmbedEndpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync(ct));
        if (doc.RootElement.TryGetProperty("usage", out var u) && u.TryGetProperty("prompt_tokens", out var pt))
        {
            var n = pt.GetInt64();
            _usage.Record(EmbedModel, n, 0, EmbedPricing.CostUsd(n, 0));
        }
        var arr = doc.RootElement.GetProperty("data")[0].GetProperty("embedding");
        var vec = new float[arr.GetArrayLength()];
        int i = 0;
        foreach (var x in arr.EnumerateArray()) vec[i++] = x.GetSingle();
        return vec;
    }

    /// <summary>
    /// Streams the assistant reply token-by-token. If tools are supplied, executes any
    /// tool calls (via <paramref name="toolExecutor"/>) and continues until the model
    /// produces a final answer.
    /// </summary>
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
        var key = _cfg.GetKey("openai");
        if (string.IsNullOrWhiteSpace(key))
        {
            const string noKey = "[no OpenAI API key — add one in ⋯ → LLM settings (or set `openai=sk-...` in .env)]";
            onError?.Invoke(noKey);
            yield return noKey;
            yield break;
        }
        var model = ModelName;
        var pricing = Pricing;

        // Mutable conversation input; grows with function_call / output items across rounds.
        var input = new List<object>();
        foreach (var m in history)
            input.Add(new { role = m.Role, content = m.Content });

        for (int round = 0; round < MaxToolRounds; round++)
        {
            var calls = new Dictionary<string, ToolCall>(); // keyed by item id (fc_...)
            var sawError = false;
            var reasoning = new StringBuilder();
            var roundText = new StringBuilder();

            await foreach (var ev in StreamRoundAsync(key, model, systemPrompt, input, tools, effort, ct))
            {
                if (ev.Text is not null)
                {
                    roundText.Append(ev.Text);
                    yield return ev.Text;
                }
                else if (ev.Reasoning is not null)
                {
                    reasoning.Append(ev.Reasoning);
                }
                else if (ev.Usage is { } u)
                {
                    _usage.Record(model, u.In, u.Out, pricing.CostUsd(u.In, u.Out, u.Cached));
                }
                else if (ev.ItemAdded is { } added)
                {
                    calls[added.ItemId] = added;
                }
                else if (ev.ArgsDelta is { } ad && calls.TryGetValue(ad.ItemId, out var c))
                {
                    c.Args.Append(ad.Delta);
                }
                else if (ev.Error is not null)
                {
                    onError?.Invoke(ev.Error);
                    yield return ev.Error;
                    sawError = true;
                }
            }

            if (reasoning.Length > 0) onReasoning?.Invoke(reasoning.ToString());

            if (sawError || calls.Count == 0 || tools is null || toolExecutor is null)
                yield break;

            // Keep any text the model emitted alongside its tool calls — without this it vanishes
            // from the conversation the next round sees.
            if (roundText.Length > 0)
                input.Add(new { role = "assistant", content = roundText.ToString() });

            // Append each function_call + its output to the input, then loop for the final answer.
            foreach (var call in calls.Values)
            {
                var args = call.Args.ToString();
                input.Add(new { type = "function_call", call_id = call.CallId, name = call.Name, arguments = args });
                var output = await toolExecutor(call.Name, args);
                input.Add(new { type = "function_call_output", call_id = call.CallId, output });
            }
        }

        // Every round ended in more tool calls — say so instead of stopping silently mid-thought.
        const string capped = "\n[stopped: tool-call round limit reached]";
        onError?.Invoke(capped);
        yield return capped;
    }

    private async IAsyncEnumerable<StreamEvent> StreamRoundAsync(
        string key, string model, string systemPrompt, List<object> input,
        IReadOnlyList<object>? tools, string effort,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var reasoning = new { effort, summary = "auto" };
        // store=false → OpenAI does not retain the thread server-side (stateless; privacy).
        object payload = tools is null
            ? new { model, reasoning, instructions = systemPrompt, input, stream = true, store = false }
            : new { model, reasoning, instructions = systemPrompt, input, tools, stream = true, store = false };

        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            yield return StreamEvent.Err($"[OpenAI error {(int)resp.StatusCode}: {Truncate(body, 300)}]");
            yield break;
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var data = line["data:".Length..].Trim();
            if (data.Length == 0 || data == "[DONE]") continue;

            var ev = ParseFrame(data);
            if (ev is not null) yield return ev;
        }
    }

    internal static StreamEvent? ParseFrame(string data)   // internal: pinned by unit tests
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            switch (type)
            {
                case "response.output_text.delta":
                    return StreamEvent.TextDelta(root.GetProperty("delta").GetString() ?? "");

                case "response.reasoning_summary_text.delta":
                    return StreamEvent.ReasoningDelta(root.GetProperty("delta").GetString() ?? "");

                case "response.output_item.added":
                    var item = root.GetProperty("item");
                    if (item.TryGetProperty("type", out var it) && it.GetString() == "function_call")
                    {
                        return StreamEvent.Added(new ToolCall
                        {
                            ItemId = item.GetProperty("id").GetString() ?? "",
                            CallId = item.GetProperty("call_id").GetString() ?? "",
                            Name = item.GetProperty("name").GetString() ?? "",
                        });
                    }
                    return null;

                case "response.function_call_arguments.delta":
                    return StreamEvent.Args(
                        root.GetProperty("item_id").GetString() ?? "",
                        root.GetProperty("delta").GetString() ?? "");

                // Server-side failures arrive as EVENTS inside a 200 stream — unmapped, they fell
                // into `default: null` and a failed response looked like a clean empty reply
                // ("the agent just stopped"). Surface every failure shape as an error.
                case "response.failed":
                case "response.incomplete":
                {
                    string? msg = null;
                    if (root.TryGetProperty("response", out var fr))
                    {
                        if (fr.TryGetProperty("error", out var fe) && fe.ValueKind == JsonValueKind.Object &&
                            fe.TryGetProperty("message", out var fm)) msg = fm.GetString();
                        else if (fr.TryGetProperty("incomplete_details", out var inc) && inc.ValueKind == JsonValueKind.Object &&
                                 inc.TryGetProperty("reason", out var ir)) msg = $"incomplete: {ir.GetString()}";
                    }
                    return StreamEvent.Err($"[OpenAI {type}: {msg ?? Truncate(data, 200)}]");
                }
                case "error":
                {
                    var msg = root.TryGetProperty("message", out var em) ? em.GetString()
                        : root.TryGetProperty("error", out var eo) && eo.ValueKind == JsonValueKind.Object &&
                          eo.TryGetProperty("message", out var eom) ? eom.GetString()
                        : Truncate(data, 200);
                    return StreamEvent.Err($"[OpenAI stream error: {msg}]");
                }

                case "response.completed":
                    if (root.TryGetProperty("response", out var resp) && resp.TryGetProperty("usage", out var usg))
                    {
                        long inTok = usg.TryGetProperty("input_tokens", out var itk) ? itk.GetInt64() : 0;
                        long outTok = usg.TryGetProperty("output_tokens", out var otk) ? otk.GetInt64() : 0;
                        long cached = usg.TryGetProperty("input_tokens_details", out var idt)
                            && idt.TryGetProperty("cached_tokens", out var ctk) ? ctk.GetInt64() : 0;
                        return StreamEvent.UsageEv(inTok, outTok, cached);
                    }
                    return null;

                default:
                    return null;
            }
        }
        catch (Exception e) { DevTrap.Report("sse-parse-openai", e); return null; }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    // --- small event/value types ---
    internal sealed class ToolCall
    {
        public string ItemId = "";
        public string CallId = "";
        public string Name = "";
        public StringBuilder Args = new();
    }

    internal sealed class StreamEvent
    {
        public string? Text;
        public string? Reasoning;
        public string? Error;
        public ToolCall? ItemAdded;
        public (string ItemId, string Delta)? ArgsDelta;
        public (long In, long Out, long Cached)? Usage;

        public static StreamEvent TextDelta(string s) => new() { Text = s };
        public static StreamEvent ReasoningDelta(string s) => new() { Reasoning = s };
        public static StreamEvent Err(string s) => new() { Error = s };
        public static StreamEvent Added(ToolCall c) => new() { ItemAdded = c };
        public static StreamEvent Args(string id, string d) => new() { ArgsDelta = (id, d) };
        public static StreamEvent UsageEv(long i, long o, long cached) => new() { Usage = (i, o, cached) };
    }
}
