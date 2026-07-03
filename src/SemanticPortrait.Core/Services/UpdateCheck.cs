using System.Text.Json;

namespace SemanticPortrait.Core;

/// <summary>
/// Launch-time update check against GitHub releases. Metadata-only egress (one GET for the
/// latest tag — no journal content, disclosed in privacy_status), silent on ANY failure, and
/// never downloads anything: it only tells the menu a newer version exists.
/// </summary>
public static class UpdateCheck
{
    public const string ReleasesUrl = "https://github.com/monstercameron/SemanticPortrait/releases";
    private const string LatestApi = "https://api.github.com/repos/monstercameron/SemanticPortrait/releases/latest";

    /// <summary>Latest release tag (e.g. "v0.9.1") if it's newer than <paramref name="current"/>; else null.</summary>
    public static async Task<string?> NewerReleaseAsync(HttpClient http, string current, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestApi);
            req.Headers.UserAgent.ParseAdd("SemanticPortrait-UpdateCheck");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var res = await http.SendAsync(req, cts.Token);
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(cts.Token));
            var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            return tag is not null && IsNewer(tag, current) ? tag : null;
        }
        catch { return null; }   // offline / rate-limited / anything: the check just doesn't happen
    }

    /// <summary>Compare "v1.2.3[-suffix]"-style versions numerically; malformed → not newer.</summary>
    internal static bool IsNewer(string candidate, string current)
    {
        var a = Parse(candidate); var b = Parse(current);
        if (a is null || b is null) return false;
        return a.CompareTo(b) > 0;
    }

    private static Version? Parse(string v)
    {
        var s = v.Trim().TrimStart('v', 'V');
        var dash = s.IndexOf('-');           // strip prerelease suffix ("0.9.0-beta")
        if (dash > 0) s = s[..dash];
        return Version.TryParse(s.Count(c => c == '.') == 0 ? s + ".0" : s, out var parsed) ? parsed : null;
    }
}
