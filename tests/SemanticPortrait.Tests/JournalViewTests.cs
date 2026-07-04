using SemanticPortrait.Core;

namespace SemanticPortrait.Tests;

/// <summary>Read-only journal views: exact-text search, writing stats, mood series, calendar month.</summary>
public class JournalViewTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sp_jview_{Guid.NewGuid():N}.db");
    private readonly Db _db;
    public JournalViewTests() { _db = new Db(_path); _db.OpenPlaintext(); }
    public void Dispose() => _db.DestroyFile();

    [Fact]
    public void Search_is_case_insensitive_substring_over_user_entries_only()
    {
        _db.AddMessage("user", "went to Tennis practice", DateTime.UtcNow.ToString("o"));
        _db.AddMessage("assistant", "tennis is a great outlet", DateTime.UtcNow.ToString("o"));  // not a user entry
        _db.AddMessage("user", "no match here", DateTime.UtcNow.ToString("o"));

        var hits = _db.SearchEntries("tennis");
        Assert.Single(hits);
        Assert.Contains("Tennis practice", hits[0].Snippet);
    }

    [Fact]
    public void Search_escapes_like_wildcards()
    {
        _db.AddMessage("user", "100% sure about this", DateTime.UtcNow.ToString("o"));
        _db.AddMessage("user", "totally different", DateTime.UtcNow.ToString("o"));
        Assert.Single(_db.SearchEntries("100%"));            // the % is literal, not a wildcard
        Assert.Empty(_db.SearchEntries("xyz%"));
    }

    [Fact]
    public void Writing_stats_count_entries_words_and_distinct_days()
    {
        // Anchor to LOCAL noon: WritingStats buckets by local day, so a UTC "now" near local
        // midnight would push the +1h entry into the next local day and make "days" flaky.
        var t = DateTime.Today.AddHours(12).ToUniversalTime();
        _db.AddMessage("user", "one two three", t.ToString("o"));
        _db.AddMessage("user", "four five", t.AddHours(1).ToString("o"));       // same day
        _db.AddMessage("user", "six", t.AddDays(-2).ToString("o"));            // another day
        _db.AddMessage("assistant", "not counted", t.ToString("o"));

        var (entries, words, days, window) = _db.WritingStats(60);
        Assert.Equal(3, entries);
        Assert.Equal(6, words);
        Assert.Equal(2, days);
        Assert.Equal(60, window);
    }

    [Fact]
    public void Calendar_month_buckets_by_local_day_with_mean_valence()
    {
        var day = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Local);
        var m1 = _db.AddMessage("user", "a", day.ToUniversalTime().ToString("o"));
        _db.SetEntryMeta(m1, "ok", 0.2, 0.5, 0.5, "[]", "[]", "s");
        var m2 = _db.AddMessage("user", "b", day.AddHours(3).ToUniversalTime().ToString("o"));
        _db.SetEntryMeta(m2, "ok", 0.6, 0.5, 0.5, "[]", "[]", "s");

        var cal = _db.CalendarMonth(2026, 6);
        Assert.True(cal.ContainsKey(15));
        Assert.Equal(2, cal[15].Count);
        Assert.Equal(0.4, cal[15].MeanValence, 3);   // mean of 0.2 and 0.6
    }

    [Fact]
    public void Mood_series_is_chronological()
    {
        var t = DateTime.UtcNow;
        var older = _db.AddMessage("user", "x", t.AddDays(-5).ToUniversalTime().ToString("o"));
        _db.SetEntryMeta(older, "sad", -0.5, 0.4, 0.3, "[]", "[]", "s");
        var newer = _db.AddMessage("user", "y", t.ToUniversalTime().ToString("o"));
        _db.SetEntryMeta(newer, "glad", 0.5, 0.6, 0.7, "[]", "[]", "s");

        var series = _db.MoodSeries();
        Assert.Equal(2, series.Count);
        Assert.True(series[0].LocalDate < series[1].LocalDate);
        Assert.Equal(-0.5, series[0].Valence, 3);
    }
}
