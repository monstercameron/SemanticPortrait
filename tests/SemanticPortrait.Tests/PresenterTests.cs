using SemanticPortrait.Core;

namespace SemanticPortrait.Tests;

// These presenters carry logic that used to live inside Home.razor's markup (untestable there).
// Now it's plain Core code with a real temp DB, like the rest of the suite.
public class PresenterTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"pres_{Guid.NewGuid():N}.db");
    private readonly Db _db;

    public PresenterTests()
    {
        _db = new Db(_path);
        _db.OpenPlaintext();
    }

    public void Dispose() { _db.Dispose(); try { File.Delete(_path); } catch { } }

    [Fact]
    public void Timeline_groups_events_and_entries_by_local_day_newest_first()
    {
        var t = DateTime.Today.AddHours(12).ToUniversalTime();   // local noon, no midnight-boundary flake
        _db.AddMessage("user", "today's entry", t.ToString("o"));
        _db.AddMessage("assistant", "not on the timeline", t.ToString("o"));   // only user entries appear
        _db.AddEvent(t.AddDays(-2).ToString("o"), "a dated event two days ago");
        _db.AddMessage("user", "older entry", t.AddDays(-2).AddHours(1).ToString("o"));

        var days = new TimelinePresenter(_db).Build();

        Assert.Equal(2, days.Count);                          // two distinct local days
        Assert.True(days[0].LocalDate > days[1].LocalDate);   // newest first
        Assert.Single(days[0].Items);                          // today: just the one user entry
        Assert.Equal(2, days[1].Items.Count);                  // two days ago: event + entry
        Assert.Contains(days[1].Items, i => i.Kind == TimelineKind.Event);
        Assert.Contains(days[1].Items, i => i.Kind == TimelineKind.Entry);
        Assert.DoesNotContain(days.SelectMany(d => d.Items), i => i.Text == "not on the timeline");
    }

    [Fact]
    public void Timeline_truncates_long_entry_text()
    {
        var t = DateTime.Today.AddHours(12).ToUniversalTime();
        _db.AddMessage("user", new string('x', 200), t.ToString("o"));

        var item = new TimelinePresenter(_db).Build().SelectMany(d => d.Items).Single();

        Assert.EndsWith("…", item.Text);
        Assert.Equal(121, item.Text.Length);   // 120 chars + ellipsis
    }

    [Fact]
    public void Ledger_accuracy_is_mean_of_resolved_scores_only()
    {
        var p1 = _db.AddPrediction("she replies by Saturday", "a reply arrives", null);
        var p2 = _db.AddPrediction("the interview goes well", "an offer follows", null);
        _db.AddPrediction("still open", "unresolved", null);   // not scored → excluded from accuracy
        _db.ResolvePrediction(p1, "she replied", 1.0);
        _db.ResolvePrediction(p2, "no offer", 0.0);

        var view = new LedgerPresenter(_db).Build();

        Assert.Equal(3, view.Predictions.Count);
        Assert.Equal(2, view.ResolvedCount);
        Assert.Equal(0.5, view.Accuracy, 3);   // mean of 1.0 and 0.0, the open one ignored
    }

    [Fact]
    public void Ledger_is_empty_and_zero_accuracy_with_no_predictions()
    {
        var view = new LedgerPresenter(_db).Build();
        Assert.Empty(view.Predictions);
        Assert.Equal(0, view.ResolvedCount);
        Assert.Equal(0.0, view.Accuracy);
    }
}
