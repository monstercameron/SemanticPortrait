using SemanticPortrait.Core;

namespace SemanticPortrait.Tests;

/// <summary>"On this day": events + dated entries from this calendar day in a prior year surface;
/// same-day-this-year and other days don't. Comparison is on the LOCAL day the user lived.</summary>
public class OnThisDayTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sp_otd_{Guid.NewGuid():N}.db");
    private readonly Db _db;
    public OnThisDayTests() { _db = new Db(_path); _db.OpenPlaintext(); }
    public void Dispose() => _db.DestroyFile();

    [Fact]
    public void Surfaces_prior_year_same_day_only()
    {
        var now = DateTime.Now;
        var lastYear = now.AddYears(-1);
        var twoYears = now.AddYears(-2);
        var otherDay = now.AddYears(-1).AddDays(3);

        _db.AddEvent(lastYear.ToUniversalTime().ToString("o"), "first climbing send");
        _db.AddEvent(twoYears.ToUniversalTime().ToString("o"), "moved apartments");
        _db.AddEvent(otherDay.ToUniversalTime().ToString("o"), "unrelated day");
        _db.AddEvent(now.ToUniversalTime().ToString("o"), "today's event");   // this year → excluded

        var hits = _db.OnThisDay(now);

        Assert.Contains(hits, h => h.Summary == "first climbing send" && h.YearsAgo == 1);
        Assert.Contains(hits, h => h.Summary == "moved apartments" && h.YearsAgo == 2);
        Assert.DoesNotContain(hits, h => h.Summary == "unrelated day");
        Assert.DoesNotContain(hits, h => h.Summary == "today's event");
        // most-recent first
        Assert.True(hits.FindIndex(h => h.YearsAgo == 1) < hits.FindIndex(h => h.YearsAgo == 2));
    }

    [Fact]
    public void Includes_dated_entries_not_just_events()
    {
        var lastYear = DateTime.Now.AddYears(-1);
        var m = _db.AddMessage("user", "a hard but honest day", lastYear.ToUniversalTime().ToString("o"));
        _db.SetEntryMeta(m, "sad", -0.4, 0.5, 0.4, "[]", "[]", "reflected on the day");

        var hits = _db.OnThisDay(DateTime.Now);
        Assert.Contains(hits, h => h.Kind == "entry" && h.Summary.Contains("hard but honest"));
    }

    [Fact]
    public void Empty_history_is_empty_not_a_crash()
        => Assert.Empty(_db.OnThisDay(DateTime.Now));
}
