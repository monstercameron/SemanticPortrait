using SemanticPortrait.Core;

namespace SemanticPortrait.Tests;

/// <summary>Guided programs: catalog listing, start persists state, today's prompt advances by
/// local day, completion is honest, and stop clears.</summary>
public class ProgramToolsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sp_prog_{Guid.NewGuid():N}.db");
    private readonly Db _db;
    private readonly ProgramTools _t;
    public ProgramToolsTests() { _db = new Db(_path); _db.OpenPlaintext(); _t = new ProgramTools(_db); }
    public void Dispose() => _db.DestroyFile();

    [Fact]
    public async Task Catalog_lists_and_start_returns_day_one()
    {
        Assert.Contains("gratitude7", await _t.ExecuteAsync("list_programs", "{}"));
        var res = await _t.ExecuteAsync("start_program", "{\"id\":\"gratitude7\"}");
        Assert.Contains("Day 1 prompt", res);
        Assert.Equal("gratitude7", _db.GetSetting("program_id"));

        var active = _t.Active();
        Assert.NotNull(active);
        Assert.Equal(1, active!.Value.Day);
    }

    [Fact]
    public async Task Unknown_program_errors()
        => Assert.Contains("error", await _t.ExecuteAsync("start_program", "{\"id\":\"nope\"}"));

    [Fact]
    public async Task Today_prompt_advances_by_local_day_and_completes()
    {
        await _t.ExecuteAsync("start_program", "{\"id\":\"reset5\"}");   // 5-day program
        // simulate having started 6 local days ago → past the end
        _db.SetSetting("program_start", DateTime.Now.Date.AddDays(-6).ToString("yyyy-MM-dd"));
        Assert.Contains("complete", await _t.ExecuteAsync("program_today", "{}"));

        // day 3
        _db.SetSetting("program_start", DateTime.Now.Date.AddDays(-2).ToString("yyyy-MM-dd"));
        Assert.Contains("day 3/5", await _t.ExecuteAsync("program_today", "{}"));
    }

    [Fact]
    public async Task Delivery_claim_is_once_per_day_and_stop_clears()
    {
        await _t.ExecuteAsync("start_program", "{\"id\":\"gratitude7\"}");
        Assert.True(_t.ClaimTodayDelivery());    // first today
        Assert.False(_t.ClaimTodayDelivery());   // already delivered

        await _t.ExecuteAsync("stop_program", "{}");
        Assert.Null(_t.Active());
        Assert.Contains("no guided program", await _t.ExecuteAsync("program_today", "{}"));
    }
}
