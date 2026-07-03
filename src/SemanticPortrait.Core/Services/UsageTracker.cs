namespace SemanticPortrait.Core;

/// <summary>
/// Token + spend tracking across chat + embeddings (from the API's reported usage).
/// Holds an in-memory SESSION tally and persists a cumulative GLOBAL tally to the encrypted
/// DB (per model). Cost is the real billed amount, computed by the caller from the model's
/// <see cref="ModelPricing"/> — not a guessed rate.
/// </summary>
public sealed class UsageTracker
{
    private readonly object _gate = new();
    private readonly Db? _db;

    // Usage recorded before the DB is unlocked is buffered and flushed on first persist.
    private readonly List<(string Model, long In, long Out, double Cost)> _pending = new();

    public UsageTracker(Db? db = null) => _db = db;

    // --- session (this app run) ---------------------------------------------
    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public long Calls { get; private set; }
    public double CostUsd { get; private set; }
    public long Total => InputTokens + OutputTokens;

    /// <summary>Record one call's reported usage and its real billed cost.</summary>
    public void Record(string model, long input, long output, double costUsd)
    {
        lock (_gate)
        {
            InputTokens += input;
            OutputTokens += output;
            CostUsd += costUsd;
            Calls++;
            _pending.Add((model, input, output, costUsd));
            FlushPending();
        }
    }

    // Persist buffered usage if the DB is open; called under _gate.
    private void FlushPending()
    {
        if (_db is null || !_db.IsOpen || _pending.Count == 0) return;
        try
        {
            foreach (var p in _pending) _db.AddUsage(p.Model, p.In, p.Out, p.Cost);
            _pending.Clear();
        }
        catch { /* best-effort telemetry; never break a chat over a usage write */ }
    }

    // --- this month (persisted, resets each calendar month) -----------------
    // The displayed spend is scoped to the current calendar month so the user never stares at an
    // ever-growing lifetime total. Snapshot the month's persisted spend once on unlock; the live
    // total = that baseline + this session (all of which is also in the current month).
    private bool _baseLoaded;
    private string _basePeriod = "";
    private long _baseIn, _baseOut, _baseCalls;
    private double _baseCost;

    /// <summary>Snapshot the current month's persisted spend (call once after the DB unlocks).</summary>
    public void LoadBaseline()
    {
        lock (_gate)
        {
            if (_baseLoaded || _db is not { IsOpen: true }) return;
            _basePeriod = Db.CurrentMonth();
            var g = _db.GetMonthlyTotals(_basePeriod);
            (_baseIn, _baseOut, _baseCalls, _baseCost) = g;
            _baseLoaded = true;
        }
    }

    public long MonthTokens => _baseIn + _baseOut + Total;
    public long MonthCalls => _baseCalls + Calls;
    public double MonthCostUsd => _baseCost + CostUsd;

    /// <summary>Lifetime totals across all sessions, read live from the DB (for the detail line).</summary>
    public (long Input, long Output, long Calls, double CostUsd) Global()
    {
        lock (_gate)
        {
            FlushPending();
            return _db is { IsOpen: true } ? _db.GetUsageTotals() : (0, 0, 0, 0.0);
        }
    }

    public string Summary() =>
        Total == 0 ? "no usage this session"
        : $"≈{Total:N0} tokens this session (~${CostUsd:0.000})";

    public string MonthSummary() =>
        MonthCalls == 0
            ? "no spend this month"
            : $"≈{MonthTokens:N0} tokens this month (~${MonthCostUsd:0.00}) over {MonthCalls:N0} calls";
}
