using System.Text.Json;
using SemanticPortrait.Core;

namespace SemanticPortrait.Tests;

/// <summary>End-to-end tests of the agentic memory tools, using a deterministic fake embedder.</summary>
public class MemoryToolsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sp_mt_{Guid.NewGuid():N}.db");
    private readonly Db _db;
    private readonly MemoryTools _tools;

    public MemoryToolsTests()
    {
        _db = new Db(_path);
        _db.OpenPlaintext();
        _tools = new MemoryTools(_db, new FakeEmbedder());
    }

    public void Dispose() { try { if (File.Exists(_path)) File.Delete(_path); } catch { } }

    private static string Args(object o) => JsonSerializer.Serialize(o);

    [Fact]
    public async Task SaveNote_persists_and_is_searchable()
    {
        var res = await _tools.ExecuteAsync("save_note", Args(new { text = "loneliness spikes when alone at night" }));
        Assert.StartsWith("saved note #", res);
        Assert.Equal(1, _db.EmbeddingCount("note", 1));

        var hits = await _tools.ExecuteAsync("search_memory", Args(new { query = "loneliness at night" }));
        Assert.Contains("note #1", hits);
        Assert.Contains("loneliness", hits);
    }

    [Fact]
    public async Task RefineNote_updates_text_and_reembeds()
    {
        await _tools.ExecuteAsync("save_note", Args(new { text = "guess: avoids the gym from laziness" }));

        var res = await _tools.ExecuteAsync("refine_note",
            Args(new { id = 1, text = "correction: avoids the gym from shame, not laziness" }));
        Assert.Equal("refined note #1", res);

        // text updated, still exactly one embedding (re-embedded, not duplicated)
        Assert.Equal("correction: avoids the gym from shame, not laziness", _db.GetNote(1)!.Text);
        Assert.Equal(1, _db.EmbeddingCount("note", 1));

        // now findable by the refined topic
        var hits = await _tools.ExecuteAsync("search_memory", Args(new { query = "shame about the gym" }));
        Assert.Contains("note #1", hits);
        Assert.Contains("shame", hits);
    }

    [Fact]
    public async Task NoteHits_return_full_text_never_truncated()
    {
        // refine_note REPLACES the whole note, so a truncated search view = silent data loss.
        var longNote = "pattern: " + string.Join(" ", Enumerable.Repeat("rejection-radar fires on ambiguous silences", 12))
            + " TAIL-MARKER-END";
        Assert.True(longNote.Length > 280);
        await _tools.ExecuteAsync("save_note", Args(new { text = longNote }));

        foreach (var tool in new[] { "search_memory", "search_past_analysis" })
        {
            var hits = await _tools.ExecuteAsync(tool, Args(new { query = "rejection radar silences" }));
            Assert.Contains("TAIL-MARKER-END", hits);   // the tail survived
            Assert.DoesNotContain("…", hits);
        }
    }

    [Fact]
    public async Task RefineNote_missing_id_errors()
    {
        var res = await _tools.ExecuteAsync("refine_note", Args(new { id = 42, text = "x" }));
        Assert.Contains("not found", res);
    }

    [Fact]
    public async Task SearchMemory_empty_is_graceful()
    {
        var res = await _tools.ExecuteAsync("search_memory", Args(new { query = "anything" }));
        Assert.Contains("nothing stored", res);
    }

    [Fact]
    public async Task BadArgs_return_errors_not_throw()
    {
        Assert.Contains("required", await _tools.ExecuteAsync("save_note", "{}"));
        Assert.Contains("required", await _tools.ExecuteAsync("search_memory", "{}"));
        Assert.StartsWith("error", await _tools.ExecuteAsync("unknown_tool", "{}"));
    }

    [Fact]
    public void Handles_only_memory_tools()
    {
        Assert.True(_tools.Handles("search_memory"));
        Assert.True(_tools.Handles("save_note"));
        Assert.True(_tools.Handles("refine_note"));
        Assert.False(_tools.Handles("set_profile_field"));
    }
}
