using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace SemanticPortrait.Core;

/// <summary>
/// Chat provider for any OpenAI-compatible Chat Completions server (LM Studio locally; Kimi /
/// GLM / DeepSeek in the cloud). Streams tokens + reasoning and runs the tool-call loop,
/// mirroring <see cref="OpenAIClient"/> but over /chat/completions. Cloud instances are wrapped
/// with egress masking in DI; the local LM Studio subclass is not (nothing leaves the machine).
/// </summary>
public class OpenAICompatChatClient : IChatProvider
{
    private const int MaxToolRounds = 6;

    private readonly HttpClient _http;
    private readonly UsageTracker _usage;
    private readonly LlmConfig _cfg;
    private readonly string _id;
    private readonly string _displayPrefix;
    private readonly string _defaultBaseUrl;
    private readonly bool _requiresKey;

    public OpenAICompatChatClient(HttpClient http, UsageTracker usage, LlmConfig cfg,
        string providerId, string displayPrefix, string defaultBaseUrl, bool requiresKey)
    {
        _http = http; _usage = usage; _cfg = cfg;
        _id = providerId; _displayPrefix = displayPrefix;
        _defaultBaseUrl = defaultBaseUrl; _requiresKey = requiresKey;
    }

    public string ProviderId => _id;
    public string DisplayName => _displayPrefix + " · " + _cfg.SelectedModel(_id).Name;
    public bool HasKey => !_requiresKey || _cfg.HasKey(_id);
    public string ModelName => _cfg.SelectedModelId(_id);
    public virtual ModelPricing Pricing => _cfg.SelectedModel(_id).Pricing ?? new ModelPricing(0, 0, 0);

    private string BaseUrl => (_cfg.GetBaseUrl(_id) ?? _defaultBaseUrl).TrimEnd('/');
    private string BearerToken => _cfg.GetKey(_id) ?? "none";

    /// <summary>Lists the model ids the server reports at /models (empty on failure).</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/models");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", BearerToken);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return Array.Empty<string>();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync(ct));
            var list = new List<string>();
            if (doc.RootElement.TryGetProperty("data", out var data))
                foreach (var m in data.EnumerateArray())
                    if (m.TryGetProperty("id", out var id) && id.GetString() is { } s) list.Add(s);
            return list;
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>True if the server responds at /models (used for the "reachable" status dot).</summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/models");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", BearerToken);
            using var r = await _http.SendAsync(req, ct);
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

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
        if (_requiresKey && !_cfg.HasKey(_id))
        {
            var noKey = $"[no {_displayPrefix} API key — add one in ⋯ → LLM settings]";
            onError?.Invoke(noKey);
            yield return noKey;
            yield break;
        }
        var model = ModelName;
        var chatTools = ToChatTools(tools);

        // running message list (OpenAI chat format); grows with tool calls + results across rounds
        var messages = new List<object> { new { role = "system", content = systemPrompt } };
        foreach (var m in history) messages.Add(new { role = m.Role, content = m.Content });

        for (int round = 0; round < MaxToolRounds; round++)
        {
            var content = new StringBuilder();
            var reasoning = new StringBuilder();
            var calls = new SortedDictionary<int, ToolAcc>();
            var sawError = false;

            await foreach (var ev in StreamRoundAsync(model, messages, chatTools, ct))
            {
                if (ev.Content is { } c) { content.Append(c); yield return c; }
                else if (ev.Reasoning is { } r) reasoning.Append(r);
                else if (ev.Usage is { } u) _usage.Record(model, u.In, u.Out, 0);
                else if (ev.Call is { } tc)
                {
                    if (!calls.TryGetValue(tc.Index, out var acc)) calls[tc.Index] = acc = new ToolAcc();
                    if (tc.Id is not null) acc.Id = tc.Id;
                    if (tc.Name is not null) acc.Name = tc.Name;
                    if (tc.Args is not null) acc.Args.Append(tc.Args);
                }
                else if (ev.Error is { } e) { onError?.Invoke(e); yield return e; sawError = true; }
            }

            if (reasoning.Length > 0) onReasoning?.Invoke(reasoning.ToString());
            if (sawError) yield break;
            if (calls.Count == 0 || toolExecutor is null) yield break;   // final answer produced

            // echo the assistant's tool-call turn (INCLUDING any text it streamed alongside the
            // calls — dropping it would erase that text from later rounds), then each tool result
            messages.Add(new
            {
                role = "assistant",
                content = content.Length > 0 ? content.ToString() : null,
                tool_calls = calls.Values.Select(a => new
                {
                    id = a.Id ?? a.Name,
                    type = "function",
                    function = new { name = a.Name, arguments = a.Args.ToString() }
                }).ToArray()
            });
            foreach (var a in calls.Values)
            {
                string result;
                try { result = await toolExecutor(a.Name ?? "", a.Args.ToString()); }
                catch (Exception ex) { result = "error: " + ex.Message; }
                messages.Add(new { role = "tool", tool_call_id = a.Id ?? a.Name, content = result });
            }
        }

        // Every round ended in more tool calls — say so instead of stopping silently mid-thought.
        const string capped = "\n[stopped: tool-call round limit reached]";
        onError?.Invoke(capped);
        yield return capped;
    }

    private async IAsyncEnumerable<StreamEv> StreamRoundAsync(
        string model, List<object> messages, object[]? tools,
        [EnumeratorCancellation] CancellationToken ct)
    {
        object payload = tools is null
            ? new { model, messages, stream = true, stream_options = new { include_usage = true } }
            : new { model, messages, tools, stream = true, stream_options = new { include_usage = true } };

        using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", BearerToken);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        HttpResponseMessage? resp = null;
        string? sendError = null;
        try { resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (Exception ex) { sendError = $"[{_displayPrefix} unreachable at {BaseUrl} ({ex.Message})]"; }
        if (sendError is not null) { yield return new StreamEv { Error = sendError }; yield break; }

        using (var response = resp!)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                yield return new StreamEv { Error = $"[{_displayPrefix} error {(int)response.StatusCode}: {Truncate(body, 200)}]" };
                yield break;
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            while (true)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line[5..].Trim();
                if (data == "[DONE]") break;

                StreamEv? ev = null;
                try { ev = Parse(data); } catch (Exception e) { ev = null; DevTrap.Report("sse-parse-compat", e); }
                if (ev is not null) yield return ev;
            }
        }
    }

    private static StreamEv? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
        {
            long inT = u.TryGetProperty("prompt_tokens", out var p) ? p.GetInt64() : 0;
            long outT = u.TryGetProperty("completion_tokens", out var c) ? c.GetInt64() : 0;
            if (inT > 0 || outT > 0) return new StreamEv { Usage = (inT, outT) };
        }

        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) return null;
        var delta = choices[0].TryGetProperty("delta", out var d) ? d : default;
        if (delta.ValueKind != JsonValueKind.Object) return null;

        if (delta.TryGetProperty("content", out var ct2) && ct2.ValueKind == JsonValueKind.String)
        {
            var s = ct2.GetString();
            if (!string.IsNullOrEmpty(s)) return new StreamEv { Content = s };
        }
        // some local reasoning models stream chain-of-thought separately
        if (delta.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
        {
            var s = rc.GetString();
            if (!string.IsNullOrEmpty(s)) return new StreamEv { Reasoning = s };
        }
        if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array && tcs.GetArrayLength() > 0)
        {
            var tc = tcs[0];
            int idx = tc.TryGetProperty("index", out var ix) ? ix.GetInt32() : 0;
            string? id = tc.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            string? name = null, args = null;
            if (tc.TryGetProperty("function", out var fn))
            {
                if (fn.TryGetProperty("name", out var n)) name = n.GetString();
                if (fn.TryGetProperty("arguments", out var a)) args = a.GetString();
            }
            return new StreamEv { Call = new ToolDelta(idx, id, name, args) };
        }
        return null;
    }

    /// <summary>Reshape the app's flat Responses-style tool specs into Chat Completions tool specs.</summary>
    private static object[]? ToChatTools(IReadOnlyList<object>? tools)
    {
        if (tools is null || tools.Count == 0) return null;
        var outList = new List<object>(tools.Count);
        foreach (var t in tools)
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(t));
            var r = doc.RootElement;
            var fn = new Dictionary<string, object?>();
            if (r.TryGetProperty("name", out var n)) fn["name"] = n.GetString();
            if (r.TryGetProperty("description", out var de)) fn["description"] = de.GetString();
            if (r.TryGetProperty("parameters", out var pa)) fn["parameters"] = JsonSerializer.Deserialize<object>(pa.GetRawText());
            outList.Add(new { type = "function", function = fn });
        }
        return outList.ToArray();
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private sealed class ToolAcc { public string? Id; public string? Name; public StringBuilder Args = new(); }
    private readonly record struct ToolDelta(int Index, string? Id, string? Name, string? Args);
    private sealed class StreamEv
    {
        public string? Content;
        public string? Reasoning;
        public string? Error;
        public (long In, long Out)? Usage;
        public ToolDelta? Call;
    }
}
