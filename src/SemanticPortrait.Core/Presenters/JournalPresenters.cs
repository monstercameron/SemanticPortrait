using System.Globalization;

namespace SemanticPortrait.Core;

// Presenters move read-only query + aggregation logic OUT of the Razor markup and into testable
// Core services that return plain, UI-agnostic DTOs. The components then only render the DTO —
// no data fetching, parsing, grouping, or averaging inside the view.

public enum TimelineKind { Event, Entry }

/// <summary>One dated item on the timeline. Icon/formatting is the view's job — this stays data.</summary>
public sealed record TimelineItem(DateTime Utc, TimelineKind Kind, string Text);

/// <summary>A local calendar day with its items, newest-first.</summary>
public sealed record TimelineDay(DateTime LocalDate, IReadOnlyList<TimelineItem> Items);

/// <summary>Builds the Timeline view: dated events + user entries, grouped by local day, newest
/// first. Previously assembled inside Home.razor's markup.</summary>
public sealed class TimelinePresenter
{
    private readonly Db _db;
    private const int SnippetMax = 120;

    public TimelinePresenter(Db db) => _db = db;

    public IReadOnlyList<TimelineDay> Build()
    {
        if (!_db.IsOpen) return Array.Empty<TimelineDay>();

        var items = new List<TimelineItem>();
        foreach (var ev in _db.GetEvents())
            if (DateTime.TryParse(ev.EventUtc, null, DateTimeStyles.RoundtripKind, out var eu))
                items.Add(new TimelineItem(eu.ToUniversalTime(), TimelineKind.Event, ev.Summary));
        foreach (var m in _db.GetMessages())
            if (m.Role == "user" && DateTime.TryParse(m.CreatedUtc, null, DateTimeStyles.RoundtripKind, out var mu))
                items.Add(new TimelineItem(mu.ToUniversalTime(), TimelineKind.Entry,
                    m.Text.Length > SnippetMax ? m.Text[..SnippetMax] + "…" : m.Text));

        return items
            .OrderByDescending(x => x.Utc)
            .GroupBy(x => x.Utc.ToLocalTime().Date)
            .Select(g => new TimelineDay(g.Key, g.ToList()))
            .ToList();
    }
}

/// <summary>The calibration/track-record view: the raw predictions plus the derived accuracy over
/// the resolved (scored) ones. Averaging previously lived in Home.razor's markup.</summary>
public sealed record CalibrationView(IReadOnlyList<Prediction> Predictions, int ResolvedCount, double Accuracy);

public sealed class LedgerPresenter
{
    private readonly Db _db;

    public LedgerPresenter(Db db) => _db = db;

    public CalibrationView Build()
    {
        var preds = _db.IsOpen ? _db.GetPredictions() : new List<Prediction>();
        var resolved = preds.Where(p => p.Score is not null).ToList();
        var accuracy = resolved.Count > 0 ? resolved.Average(p => p.Score!.Value) : 0.0;
        return new CalibrationView(preds, resolved.Count, accuracy);
    }
}
