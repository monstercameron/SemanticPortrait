using System.Text.Json;
using SemanticPortrait.Core;

namespace SemanticPortrait.Tests;

public class PredictionTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sp_pred_{Guid.NewGuid():N}.db");
    private readonly Db _db;
    private readonly PredictionTools _t;

    public PredictionTests()
    {
        _db = new Db(_path); _db.OpenPlaintext();
        _t = new PredictionTools(_db);
    }
    public void Dispose() { try { if (File.Exists(_path)) File.Delete(_path); } catch { } }
    private static string Args(object o) => JsonSerializer.Serialize(o);

    [Fact]
    public async Task Make_list_resolve_flow_scores()
    {
        var made = await _t.ExecuteAsync("make_prediction", Args(new { claim = "she replies", criterion = "text by Sat" }));
        Assert.StartsWith("prediction #1", made);

        Assert.Contains("#1", await _t.ExecuteAsync("list_open_predictions", "{}"));

        var res = await _t.ExecuteAsync("resolve_prediction", Args(new { id = 1, outcome = "she replied Friday", score = 1.0 }));
        Assert.Contains("resolved", res);

        var p = _db.GetPredictions().Single();
        Assert.Equal(1.0, p.Score);
        Assert.Equal("she replied Friday", p.Outcome);
        Assert.Contains("no open predictions", await _t.ExecuteAsync("list_open_predictions", "{}"));
    }

    [Fact]
    public async Task Resolve_unknown_is_error()
    {
        Assert.Contains("not found", await _t.ExecuteAsync("resolve_prediction", Args(new { id = 99, outcome = "x", score = 0.5 })));
    }

    [Fact]
    public async Task Make_requires_fields()
    {
        Assert.StartsWith("error", await _t.ExecuteAsync("make_prediction", Args(new { claim = "x" })));
    }
}
