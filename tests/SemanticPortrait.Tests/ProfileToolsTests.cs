using System.Text.Json;
using SemanticPortrait.Core;

namespace SemanticPortrait.Tests;

public class ProfileToolsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"sp_prof_{Guid.NewGuid():N}.db");
    private readonly string _legacy = Path.Combine(Path.GetTempPath(), $"sp_prof_{Guid.NewGuid():N}.json");
    private readonly Db _db;
    private readonly ProfileStore _store;
    private readonly ProfileTools _tools;

    public ProfileToolsTests()
    {
        _db = new Db(_dbPath);
        _db.OpenPlaintext();
        _store = new ProfileStore(_db, _legacy);
        _tools = new ProfileTools(_store);
    }

    public void Dispose()
    {
        _db.Close();
        foreach (var p in new[] { _dbPath, _legacy })
            try { if (File.Exists(p)) File.Delete(p); } catch { }
    }

    private static string Args(object o) => JsonSerializer.Serialize(o);

    [Fact]
    public async Task Set_then_get_roundtrips_and_persists()
    {
        await _tools.ExecuteAsync("set_profile_field", Args(new { key = "name", value = "Sam" }));
        await _tools.ExecuteAsync("set_profile_field", Args(new { key = "purpose", value = "self-discovery" }));

        var got = await _tools.ExecuteAsync("get_profile", "{}");
        Assert.Contains("Sam", got);
        Assert.Contains("self-discovery", got);

        // a fresh store over the same DB sees persisted values (case-insensitive keys)
        Assert.Equal("Sam", new ProfileStore(_db, _legacy).Get("NAME"));
    }

    [Fact]
    public async Task Get_empty_is_graceful()
    {
        Assert.Contains("nothing stored", await _tools.ExecuteAsync("get_profile", "{}"));
    }

    [Fact]
    public async Task Set_missing_args_errors()
    {
        Assert.Contains("required", await _tools.ExecuteAsync("set_profile_field", Args(new { key = "name" })));
    }

    [Fact]
    public void Legacy_plaintext_profile_migrates_into_db_and_file_is_removed()
    {
        File.WriteAllText(_legacy, JsonSerializer.Serialize(
            new Dictionary<string, string> { ["name"] = "Sam", ["purpose"] = "grounding" }));

        var store = new ProfileStore(_db, _legacy);
        Assert.Equal("Sam", store.Get("name"));            // triggers + reads the migration
        Assert.Equal("grounding", store.Get("purpose"));
        Assert.False(File.Exists(_legacy));                 // no plaintext copy left outside the lock
    }

    [Fact]
    public void Locked_db_reads_empty_and_never_touches_the_legacy_file()
    {
        File.WriteAllText(_legacy, JsonSerializer.Serialize(new Dictionary<string, string> { ["name"] = "Sam" }));
        _db.Close();

        var store = new ProfileStore(_db, _legacy);
        Assert.Null(store.Get("name"));                     // locked → nothing readable
        Assert.Empty(store.All());
        Assert.True(File.Exists(_legacy));                  // migration deferred until unlocked
    }
}
