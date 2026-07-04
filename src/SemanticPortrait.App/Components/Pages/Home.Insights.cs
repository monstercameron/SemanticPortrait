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
    private void CalPrev() => _calMonth = _calMonth.AddMonths(-1);
    private void CalNext() { var n = _calMonth.AddMonths(1); if (n <= new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1)) _calMonth = n; }

    /// <summary>Mood series → an SVG polyline path in a 0..100 × 0..100 viewbox (valence −1..1
    /// mapped so higher = up). Returns "" when there's nothing to plot.</summary>
    private static string MoodPath(IReadOnlyList<(DateTime LocalDate, double Valence, double Energy)> series, bool energy)
    {
        if (series.Count < 2) return "";
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < series.Count; i++)
        {
            var x = series.Count == 1 ? 0 : 100.0 * i / (series.Count - 1);
            var val = energy ? Math.Clamp(series[i].Energy, 0, 1) : (Math.Clamp(series[i].Valence, -1, 1) + 1) / 2;
            var y = 100 - val * 100;
            sb.Append(i == 0 ? "M" : "L").Append(x.ToString("0.#")).Append(' ').Append(y.ToString("0.#")).Append(' ');
        }
        return sb.ToString().Trim();
    }

    /// <summary>Calendar cell tint from a day's mean valence (green ↔ red), or transparent for
    /// a day with no entries.</summary>
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

    private static string DayTint(double meanValence)
    {
        var t = Math.Clamp((meanValence + 1) / 2, 0, 1);   // 0 = red, 1 = green
        int r = (int)(220 - t * 140), g = (int)(80 + t * 140);
        return $"rgba({r},{g},90,0.32)";
    }
}
