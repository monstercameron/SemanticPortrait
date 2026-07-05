using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace SemanticPortrait.Core;

/// <summary>
/// UNOFFICIAL chat provider that rides a ChatGPT subscription via the Codex backend instead of
/// pay-per-token API billing. The wire protocol is identical to <see cref="OpenAIClient"/> (OpenAI
/// Responses) — this only swaps the endpoint (chatgpt.com/backend-api/codex/responses), the auth
/// (OAuth bearer + ChatGPT-Account-ID + Codex originator, via <see cref="CodexAuth"/>), and pricing
/// ($0 — it's the subscription). SSE parsing + the tool loop reuse OpenAIClient's tested code.
///
/// This is opt-in and at-the-user's-ChatGPT-account risk (client impersonation of a private backend).
/// </summary>
public sealed class CodexChatClient : IChatProvider
{
    private const string Endpoint = "https://chatgpt.com/backend-api/codex/responses";
    private const string DefaultModel = "gpt-5.5";
    private const int MaxToolRounds = 6;
    private static readonly ModelPricing Free = new(0, 0, 0);   // covered by the subscription

    private readonly HttpClient _http;
    private readonly CodexAuth _auth;
    private readonly UsageTracker _usage;
    private readonly LlmConfig _cfg;

    // ONE stable id for this app session — reused as the session_id header AND the request's
    // prompt_cache_key. The real Codex client keeps a single id per conversation so the backend can
    // cache the (large, unchanging) system-prompt + history prefix across turns; a fresh GUID per
    // request defeats that caching and makes every turn re-process the whole prompt (slow, and it
    // worsens as the thread grows). Resetting on restart is fine — caching within a session is the win.
    private readonly string _sessionId = Guid.NewGuid().ToString();

    public CodexChatClient(HttpClient http, CodexAuth auth, UsageTracker usage, LlmConfig cfg)
    { _http = http; _auth = auth; _usage = usage; _cfg = cfg; }

    public string ProviderId => "codex";
    public string DisplayName => "ChatGPT plan · " + ModelName;
    public bool HasKey => _auth.IsSignedIn;                     // "signed in" is this provider's key
    public string ModelName { get { var m = _cfg.SelectedModelId("codex"); return string.IsNullOrWhiteSpace(m) || m == "codex" ? DefaultModel : m; } }
    public ModelPricing Pricing => Free;

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
        if (!_auth.IsSignedIn)
        {
            const string msg = "[not signed in — use ⋯ → LLM settings → Sign in with ChatGPT]";
            onError?.Invoke(msg); yield return msg; yield break;
        }
        var model = ModelName;

        var input = new List<object>();
        foreach (var m in history) input.Add(new { role = m.Role, content = m.Content });

        for (int round = 0; round < MaxToolRounds; round++)
        {
            var calls = new Dictionary<string, OpenAIClient.ToolCall>();
            var sawError = false;
            var reasoning = new StringBuilder();
            var roundText = new StringBuilder();

            await foreach (var ev in StreamRoundAsync(model, systemPrompt, input, tools, effort, ct))
            {
                if (ev.Text is not null) { roundText.Append(ev.Text); yield return ev.Text; }
                else if (ev.Reasoning is not null) reasoning.Append(ev.Reasoning);
                else if (ev.Usage is { } u) _usage.Record(model, u.In, u.Out, 0);   // subscription: no per-token cost
                else if (ev.ItemAdded is { } added) calls[added.ItemId] = added;
                else if (ev.ArgsDelta is { } ad && calls.TryGetValue(ad.ItemId, out var c)) c.Args.Append(ad.Delta);
                else if (ev.Error is not null) { onError?.Invoke(ev.Error); yield return ev.Error; sawError = true; }
            }

            if (reasoning.Length > 0) onReasoning?.Invoke(reasoning.ToString());
            if (sawError || calls.Count == 0 || tools is null || toolExecutor is null) yield break;

            if (roundText.Length > 0) input.Add(new { role = "assistant", content = roundText.ToString() });
            foreach (var call in calls.Values)
            {
                var args = call.Args.ToString();
                input.Add(new { type = "function_call", call_id = call.CallId, name = call.Name, arguments = args });
                var output = await toolExecutor(call.Name, args);
                input.Add(new { type = "function_call_output", call_id = call.CallId, output });
            }
        }

        const string capped = "\n[stopped: tool-call round limit reached]";
        onError?.Invoke(capped); yield return capped;
    }

    private async IAsyncEnumerable<OpenAIClient.StreamEvent> StreamRoundAsync(
        string model, string systemPrompt, List<object> input, IReadOnlyList<object>? tools, string effort,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var reasoning = new { effort, summary = "auto" };
        // prompt_cache_key (stable per session) lets the backend reuse the cached system-prompt +
        // history prefix across turns — the main lever against per-turn latency growth.
        object payload = tools is null
            ? new { model, reasoning, instructions = systemPrompt, input, stream = true, store = false, prompt_cache_key = _sessionId }
            : new { model, reasoning, instructions = systemPrompt, input, tools, stream = true, store = false, prompt_cache_key = _sessionId };
        var json = JsonSerializer.Serialize(payload);

        var resp = await SendAsync(json, ct);
        if (resp is null) { yield return OpenAIClient.StreamEvent.Err("[ChatGPT sign-in expired — sign in again in ⋯ → LLM settings]"); yield break; }
        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                yield return OpenAIClient.StreamEvent.Err($"[ChatGPT/Codex error {(int)resp.StatusCode}: {Trunc(body, 300)}]");
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
                var ev = OpenAIClient.ParseFrame(data);   // shared Responses SSE parser
                if (ev is not null) yield return ev;
            }
        }
    }

    /// <summary>POST the Responses request with the OAuth bearer + account header; on a 401 refresh
    /// the token once and retry. Returns null only if we have no usable token at all.</summary>
    private async Task<HttpResponseMessage?> SendAsync(string json, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var auth = await _auth.GetValidAsync(ct);
            if (auth is null) return null;
            var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {auth.Value.AccessToken}");
            req.Headers.TryAddWithoutValidation("ChatGPT-Account-ID", auth.Value.AccountId);
            req.Headers.TryAddWithoutValidation("originator", CodexAuth.Originator);
            req.Headers.TryAddWithoutValidation("OpenAI-Beta", "responses=experimental");
            req.Headers.TryAddWithoutValidation("session_id", _sessionId);
            req.Headers.Accept.ParseAdd("text/event-stream");

            var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                resp.Dispose();
                if (await _auth.RefreshAsync(ct) is null) return null;   // refresh failed → "sign in again"
                continue;   // retry with the refreshed token
            }
            return resp;
        }
        return null;
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
