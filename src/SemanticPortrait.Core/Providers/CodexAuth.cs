using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SemanticPortrait.Core;

/// <summary>
/// OAuth "Sign in with ChatGPT" for the (unofficial) Codex-subscription chat path. Runs the PKCE
/// loopback flow the Codex CLI uses, and persists the token bundle in the ENCRYPTED DB (behind the
/// vault lock — never a plaintext auth.json). Tokens refresh silently via the refresh_token grant.
///
/// This impersonates the first-party Codex client (client_id + originator) against a private ChatGPT
/// backend — it is NOT an OpenAI-sanctioned third-party integration and can put a ChatGPT account at
/// risk. Surfaced in the app strictly as an opt-in, clearly-labeled experiment.
/// </summary>
public sealed class CodexAuth
{
    public const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";   // Codex public client
    public const string Originator = "codex_cli_rs";
    private const string Auth = "https://auth.openai.com";
    private const int Port = 1455;
    private const string Redirect = "http://localhost:1455/auth/callback";
    private const string Scope = "openid profile email offline_access api.connectors.read api.connectors.invoke";
    private const string StoreKey = "codex_auth";

    private readonly Db _db;
    private readonly HttpClient _http;
    private CodexTokens? _cache;

    public CodexAuth(Db db, HttpClient http) { _db = db; _http = http; }

    public sealed record CodexTokens(string AccessToken, string RefreshToken, string IdToken,
        string AccountId, string Plan, DateTime ExpiresUtc);

    private CodexTokens? Load()
    {
        if (_cache is not null) return _cache;
        var json = _db.IsOpen ? _db.GetSetting(StoreKey) : null;
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return _cache = JsonSerializer.Deserialize<CodexTokens>(json); } catch { return null; }
    }

    private void Save(CodexTokens t)
    {
        _cache = t;
        if (_db.IsOpen) _db.SetSetting(StoreKey, JsonSerializer.Serialize(t));
    }

    public bool IsSignedIn => Load() is not null;
    public string? Plan => Load()?.Plan;
    public string? AccountId => Load()?.AccountId;

    public void SignOut() { _cache = null; if (_db.IsOpen) _db.SetSetting(StoreKey, null); }

    // ---- PKCE helpers ----
    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static JsonElement? JwtPayload(string? jwt)
    {
        if (jwt is null) return null;
        var parts = jwt.Split('.'); if (parts.Length < 2) return null;
        var p = parts[1].Replace('-', '+').Replace('_', '/');
        p = p.PadRight(p.Length + (4 - p.Length % 4) % 4, '=');
        try { return JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(p))).RootElement.Clone(); }
        catch { return null; }
    }

    /// <summary>Interactive PKCE loopback login. <paramref name="openUrl"/> launches the system
    /// browser (injected so Core stays UI-free). Returns the plan on success, or throws on failure.</summary>
    public async Task<string> LoginAsync(Action<string> openUrl, CancellationToken ct = default)
    {
        var verifier = B64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = B64Url(RandomNumberGenerator.GetBytes(16));

        var url = $"{Auth}/oauth/authorize?response_type=code&client_id={ClientId}" +
            $"&redirect_uri={Uri.EscapeDataString(Redirect)}&scope={Uri.EscapeDataString(Scope)}" +
            $"&code_challenge={challenge}&code_challenge_method=S256" +
            $"&id_token_add_organizations=true&codex_cli_simplified_flow=true" +
            $"&state={state}&originator={Originator}";

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{Port}/");
        listener.Start();
        try
        {
            openUrl(url);
            string? code = null;
            using var reg = ct.Register(() => { try { listener.Stop(); } catch { } });
            while (code is null)
            {
                var ctx = await listener.GetContextAsync();
                var q = ctx.Request.QueryString;
                if (ctx.Request.Url!.AbsolutePath == "/auth/callback" && q["code"] is { } c && q["state"] == state)
                {
                    code = c;
                    var html = Encoding.UTF8.GetBytes(
                        "<!doctype html><meta charset=utf-8><body style='font:16px system-ui;background:#1c1c20;color:#ececf0;text-align:center;padding-top:20vh'>" +
                        "<h2>Signed in to SemanticPortrait.</h2><p>You can close this tab.</p>");
                    ctx.Response.ContentType = "text/html";
                    await ctx.Response.OutputStream.WriteAsync(html, ct);
                    ctx.Response.Close();
                }
                else { ctx.Response.StatusCode = 404; ctx.Response.Close(); }
            }

            var body = $"grant_type=authorization_code&code={Uri.EscapeDataString(code)}" +
                $"&redirect_uri={Uri.EscapeDataString(Redirect)}&client_id={ClientId}" +
                $"&code_verifier={Uri.EscapeDataString(verifier)}";
            using var res = await _http.PostAsync($"{Auth}/oauth/token",
                new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded"), ct);
            var txt = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"token exchange failed ({(int)res.StatusCode})");

            var tokens = Parse(txt) ?? throw new InvalidOperationException("no ChatGPT account in the token — is this a ChatGPT (not API-only) login?");
            Save(tokens);
            return tokens.Plan;
        }
        finally { try { listener.Stop(); } catch { } }
    }

    /// <summary>Build a token bundle from an /oauth/token response body.</summary>
    private static CodexTokens? Parse(string tokenJson)
    {
        using var doc = JsonDocument.Parse(tokenJson);
        var r = doc.RootElement;
        var access = r.TryGetProperty("access_token", out var a) ? a.GetString() : null;
        var refresh = r.TryGetProperty("refresh_token", out var rf) ? rf.GetString() : null;
        var id = r.TryGetProperty("id_token", out var it) ? it.GetString() : null;
        if (access is null || id is null) return null;

        string? accountId = null, plan = null;
        if (JwtPayload(id) is { } idp && idp.TryGetProperty("https://api.openai.com/auth", out var ac))
        {
            if (ac.TryGetProperty("chatgpt_account_id", out var acc)) accountId = acc.GetString();
            if (ac.TryGetProperty("chatgpt_plan_type", out var pl)) plan = pl.GetString();
        }
        if (accountId is null) return null;

        // expiry from the access token's exp claim (fall back to 50 min if absent)
        var exp = DateTime.UtcNow.AddMinutes(50);
        if (JwtPayload(access) is { } ap && ap.TryGetProperty("exp", out var e) && e.TryGetInt64(out var secs))
            exp = DateTimeOffset.FromUnixTimeSeconds(secs).UtcDateTime;

        return new CodexTokens(access, refresh ?? "", id, accountId, plan ?? "unknown", exp);
    }

    /// <summary>A currently-valid access token, refreshing first if it's within 2 min of expiry.
    /// Returns null if not signed in or refresh fails with no usable token.</summary>
    public async Task<(string AccessToken, string AccountId)?> GetValidAsync(CancellationToken ct = default)
    {
        var t = Load();
        if (t is null) return null;
        if (DateTime.UtcNow < t.ExpiresUtc.AddMinutes(-2)) return (t.AccessToken, t.AccountId);
        var refreshed = await RefreshAsync(ct);
        return refreshed is { } rt ? (rt.AccessToken, rt.AccountId)
             : DateTime.UtcNow < t.ExpiresUtc ? (t.AccessToken, t.AccountId) : null;
    }

    /// <summary>Refresh via the refresh_token grant. Returns the new bundle, or null on failure.</summary>
    public async Task<CodexTokens?> RefreshAsync(CancellationToken ct = default)
    {
        var t = Load();
        if (t is null || string.IsNullOrEmpty(t.RefreshToken)) return null;
        try
        {
            var body = $"grant_type=refresh_token&refresh_token={Uri.EscapeDataString(t.RefreshToken)}" +
                $"&client_id={ClientId}&scope={Uri.EscapeDataString(Scope)}";
            using var res = await _http.PostAsync($"{Auth}/oauth/token",
                new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded"), ct);
            if (!res.IsSuccessStatusCode) return null;

            // A refresh response typically returns a new access_token (and often a new id_token /
            // refresh_token); account_id/plan carry over from the prior bundle if the response omits
            // a fresh id_token, so refresh never requires re-deriving the account.
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var r = doc.RootElement;
            var access = r.TryGetProperty("access_token", out var a) ? a.GetString() : null;
            if (access is null) return null;
            var newRefresh = r.TryGetProperty("refresh_token", out var rf) ? rf.GetString() : null;
            var newId = r.TryGetProperty("id_token", out var it) ? it.GetString() : null;
            var exp = DateTime.UtcNow.AddMinutes(50);
            if (JwtPayload(access) is { } ap && ap.TryGetProperty("exp", out var e) && e.TryGetInt64(out var secs))
                exp = DateTimeOffset.FromUnixTimeSeconds(secs).UtcDateTime;

            var merged = t with
            {
                AccessToken = access,
                RefreshToken = string.IsNullOrEmpty(newRefresh) ? t.RefreshToken : newRefresh!,
                IdToken = string.IsNullOrEmpty(newId) ? t.IdToken : newId!,
                ExpiresUtc = exp,
            };
            Save(merged);
            return merged;
        }
        catch (Exception e) { DevTrap.Report("codex-refresh", e); return null; }
    }
}
