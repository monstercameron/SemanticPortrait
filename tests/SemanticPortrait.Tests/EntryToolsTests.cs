using System.Text.Json;
using SemanticPortrait.Core;

namespace SemanticPortrait.Tests;

public class EntryToolsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sp_em_{Guid.NewGuid():N}.db");
    private readonly Db _db;
    private readonly EntryTools _tools;
    private readonly long _msgId;

    public EntryToolsTests()
    {
        _db = new Db(_path);
        _db.OpenPlaintext();
        _tools = new EntryTools(_db, new FakeEmbedder());
        _msgId = _db.AddMessage("user", "rough day, fought with my brother", DateTime.UtcNow.ToString("o"));
    }

    public void Dispose() { try { if (File.Exists(_path)) File.Delete(_path); } catch { } }

    private static string Args(object o) => JsonSerializer.Serialize(o);

    [Fact]
    public async Task ImportEntry_creates_backdated_entry_with_meta_and_embedding()
    {
        var res = await _tools.ExecuteAsync("import_entry", Args(new
        {
            text = "Moved to Florida today. Everything I own fits in two suitcases.",
            when = "2019-03-12",
            mood = "uprooted", valence = -0.2, intensity = 0.6, energy = 0.55,
            topics = new[] { "moving", "Florida" }, people = Array.Empty<string>(),
            summary = "arrival day, unmoored but moving",
        }));
        Assert.StartsWith("imported entry #", res);
        Assert.Contains("2019-03-12", res);

        var msg = _db.GetMessages().Single(m => m.Text.Contains("two suitcases"));
        Assert.Equal("user", msg.Role);                          // it IS a journal entry
        Assert.StartsWith("2019-03-12", msg.CreatedUtc);         // dated when it was LIVED
        Assert.Equal(1, _db.EmbeddingCount("message", msg.Id));  // recallable
        var meta = _db.GetEntryMeta(msg.Id)!;
        Assert.Equal("uprooted", meta.Mood);
        Assert.StartsWith("2019-03-12", meta.EntryUtc);          // meta timestamp follows the entry
    }

    [Fact]
    public async Task ImportEntry_validates_like_record_entry_meta()
    {
        var res = await _tools.ExecuteAsync("import_entry", Args(new
        { text = "something", when = "2020", mood = "low", valence = -0.4 }));
        Assert.Contains("incomplete", res);
        Assert.Contains("intensity", res);

        var bad = await _tools.ExecuteAsync("import_entry", Args(new
        {
            text = "x", when = "not a date", mood = "low", valence = -0.4, intensity = 0.5,
            energy = 0.5, topics = new[] { "t" }, people = Array.Empty<string>(), summary = "s",
        }));
        Assert.Contains("'when'", bad);
    }

    [Fact]
    public void Coarse_lived_dates_land_mid_period()
    {
        Assert.StartsWith("2019-07-01", EntryTools.NormalizeLivedDate("2019"));
        Assert.StartsWith("2021-04-15", EntryTools.NormalizeLivedDate("2021-4"));
        Assert.StartsWith("2022-11-03", EntryTools.NormalizeLivedDate("2022-11-03"));
        Assert.Null(EntryTools.NormalizeLivedDate("sometime in childhood"));
    }

    private object Complete() => new
    {
        message_id = _msgId, mood = "angry", valence = -0.6, intensity = 0.7, energy = 0.5,
        topics = new[] { "family", "conflict" }, people = new[] { "brother" },
        summary = "frustrated after an argument",
    };

    [Fact]
    public async Task Complete_metadata_is_stored()
    {
        var res = await _tools.ExecuteAsync("record_entry_meta", Args(Complete()));
        Assert.StartsWith("recorded metadata", res);

        var meta = _db.GetEntryMeta(_msgId)!;
        Assert.Equal("angry", meta.Mood);
        Assert.Equal(-0.6, meta.Valence, 3);
        Assert.Contains("brother", meta.People);
        Assert.False(string.IsNullOrWhiteSpace(meta.EntryUtc));   // datetime captured from the message
    }

    [Fact]
    public async Task Missing_fields_are_rejected_and_nothing_stored()
    {
        var res = await _tools.ExecuteAsync("record_entry_meta",
            Args(new { message_id = _msgId, mood = "angry" }));   // lazy/incomplete
        Assert.StartsWith("error: incomplete", res);
        Assert.Contains("valence", res);
        Assert.Contains("summary", res);
        Assert.Null(_db.GetEntryMeta(_msgId));                     // not persisted
    }

    [Fact]
    public async Task Empty_topics_rejected()
    {
        var bad = new
        {
            message_id = _msgId, mood = "flat", valence = 0.0, intensity = 0.2, energy = 0.2,
            topics = Array.Empty<string>(), people = Array.Empty<string>(), summary = "meh",
        };
        var res = await _tools.ExecuteAsync("record_entry_meta", Args(bad));
        Assert.Contains("topics", res);
        Assert.Null(_db.GetEntryMeta(_msgId));
    }

    [Fact]
    public async Task Out_of_range_valence_rejected()
    {
        var bad = new
        {
            message_id = _msgId, mood = "x", valence = 5.0, intensity = 0.2, energy = 0.2,
            topics = new[] { "t" }, people = Array.Empty<string>(), summary = "s",
        };
        var res = await _tools.ExecuteAsync("record_entry_meta", Args(bad));
        Assert.Contains("valence", res);
        Assert.Null(_db.GetEntryMeta(_msgId));
    }

    [Fact]
    public async Task People_may_be_empty_but_present()
    {
        var ok = new
        {
            message_id = _msgId, mood = "calm", valence = 0.3, intensity = 0.3, energy = 0.4,
            topics = new[] { "solitude" }, people = Array.Empty<string>(), summary = "quiet evening",
        };
        var res = await _tools.ExecuteAsync("record_entry_meta", Args(ok));
        Assert.StartsWith("recorded metadata", res);
    }
}
