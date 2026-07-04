using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace SemanticPortrait.App.Components.Pages;

// Read-only journal views: exact-text search (jump to entry) + an Insights panel
// (writing stats, mood over time, calendar month).
public partial class Home
{
    // --- search ---------------------------------------------------------------
    private bool _showSearch;
    private string _searchQuery = "";
    private List<(long Id, string WhenUtc, string Snippet)> _searchHits = new();

    private void OpenSearch() { _showSearch = true; _searchQuery = ""; _searchHits = new(); _focusNext = false; }
    private void RunSearch(ChangeEventArgs e)
    {
        _searchQuery = e.Value?.ToString() ?? "";
        _searchHits = Database.IsOpen && _searchQuery.Trim().Length > 0
            ? Database.SearchEntries(_searchQuery) : new();
    }

    /// <summary>Jump the thread to a matched entry: un-clear the view if needed, close search,
    /// and scroll the message element into view (highlight via a CSS class the JS toggles).</summary>
    private async Task JumpToEntry(long messageId)
    {
        _showSearch = false;
        _clearedBefore = 0;   // ensure the target isn't hidden by a "clear view"
        StateHasChanged();
        await Task.Yield();
        try { await JS.InvokeVoidAsync("spScrollToMessage", messageId); } catch { }
    }

    // --- insights panel (stats / mood / calendar) -----------------------------
    private bool _showInsights;
    private DateTime _calMonth = new(DateTime.Now.Year, DateTime.Now.Month, 1);

    private void OpenInsights() { _showInsights = true; _calMonth = new(DateTime.Now.Year, DateTime.Now.Month, 1); }

    /// <summary>Click a calendar day → jump the thread to that day's first entry.</summary>
    private async Task JumpToDay(int day)
    {
        if (!Database.IsOpen) return;
        var id = Database.FirstEntryOnDay(_calMonth.Year, _calMonth.Month, day);
        if (id is { } mid) { _showInsights = false; await JumpToEntry(mid); }
    }
    private void CalPrev() => _calMonth = _calMonth.AddMonths(-1);
    private void CalNext() { var n = _calMonth.AddMonths(1); if (n <= new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1)) _calMonth = n; }

    /// <summary>Mood series → an SVG polyline path in a 0..100 × 0..100 viewbox (valence −1..1
    /// mapped so higher = up). Returns "" when there's nothing to plot.</summary>
    private static string MoodPath(IReadOnlyList<(DateTime LocalDate, double Valence, double Energy)> series, bool energy)
    {
        if (series.Count < 2) return "";
        var pts = new (double X, double Y)[series.Count];
        for (var i = 0; i < series.Count; i++)
        {
            var x = 100.0 * i / (series.Count - 1);
            var val = energy ? Math.Clamp(series[i].Energy, 0, 1) : (Math.Clamp(series[i].Valence, -1, 1) + 1) / 2;
            pts[i] = (x, 100 - val * 100);
        }
        // Catmull-Rom → cubic Bézier so the trend reads as a gentle curve, not a jagged zig-zag.
        static string N(double d) => d.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        var sb = new System.Text.StringBuilder();
        sb.Append('M').Append(N(pts[0].X)).Append(' ').Append(N(pts[0].Y)).Append(' ');
        for (var i = 0; i < pts.Length - 1; i++)
        {
            var p0 = pts[i == 0 ? 0 : i - 1];
            var p1 = pts[i];
            var p2 = pts[i + 1];
            var p3 = pts[i + 2 < pts.Length ? i + 2 : pts.Length - 1];
            var c1x = p1.X + (p2.X - p0.X) / 6.0;
            var c1y = p1.Y + (p2.Y - p0.Y) / 6.0;
            var c2x = p2.X - (p3.X - p1.X) / 6.0;
            var c2y = p2.Y - (p3.Y - p1.Y) / 6.0;
            sb.Append('C').Append(N(c1x)).Append(' ').Append(N(c1y)).Append(' ')
              .Append(N(c2x)).Append(' ').Append(N(c2y)).Append(' ')
              .Append(N(p2.X)).Append(' ').Append(N(p2.Y)).Append(' ');
        }
        return sb.ToString().Trim();
    }

    /// <summary>Calendar cell tint from a day's mean valence (green ↔ red), or transparent for
    /// a day with no entries.</summary>
    /// <summary>Wrap each case-insensitive occurrence of the query in the snippet with &lt;mark&gt;.
    /// HTML-encodes the surrounding text so a snippet can't inject markup.</summary>
    private static MarkupString Highlight(string snippet, string query)
    {
        var q = query.Trim();
        if (q.Length == 0) return (MarkupString)System.Net.WebUtility.HtmlEncode(snippet);
        var sb = new System.Text.StringBuilder();
        int i = 0;
        while (i < snippet.Length)
        {
            var hit = snippet.IndexOf(q, i, StringComparison.OrdinalIgnoreCase);
            if (hit < 0) { sb.Append(System.Net.WebUtility.HtmlEncode(snippet[i..])); break; }
            sb.Append(System.Net.WebUtility.HtmlEncode(snippet[i..hit]));
            sb.Append("<mark>").Append(System.Net.WebUtility.HtmlEncode(snippet.Substring(hit, q.Length))).Append("</mark>");
            i = hit + q.Length;
        }
        return (MarkupString)sb.ToString();
    }

    private static string FmtWhen(string utc) =>
        DateTime.TryParse(utc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d)
            ? d.ToLocalTime().ToString("MMM d, yyyy") : utc;

    // --- guided journaling programs -------------------------------------------
    private bool _showPrograms;
    private void OpenPrograms() => _showPrograms = true;

    /// <summary>Start a program and have the agent introduce day 1 in-thread.</summary>
    private async Task StartProgram(string id)
    {
        _showPrograms = false;
        var res = await Programs.ExecuteAsync("start_program", $"{{\"id\":\"{id}\"}}");
        StateHasChanged();
        await FireProactive(
            $"[The user just started a guided journaling program. Program tool says: \"{res}\". " +
            "Welcome them to it warmly in one or two lines and offer today's prompt as an invitation " +
            "— an opening, not an assignment. No preamble.]");
    }

    // Calendar tint on a colorblind-SAFE diverging scale (no red↔green): negative valence →
    // violet, positive → emerald; magnitude drives opacity so the tints actually read. This
    // palette is deliberately DISTINCT from the mood-chart lines (valence = cyan #38bdf8,
    // energy = amber #ffa840) since both render together in the Insights panel — an amber tint
    // here read as the energy line.
    private static string DayTint(double meanValence)
    {
        var v = Math.Clamp(meanValence, -1, 1);
        var a = (0.20 + 0.45 * Math.Abs(v)).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        return v >= 0 ? $"rgba(52,211,153,{a})" : $"rgba(167,139,250,{a})";
    }
}
