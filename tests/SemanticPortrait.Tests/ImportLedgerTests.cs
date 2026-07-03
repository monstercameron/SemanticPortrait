using SemanticPortrait.Core;

namespace SemanticPortrait.Tests;

public class ImportLedgerTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sp_ledger_{Guid.NewGuid():N}.db");
    private readonly Db _db;
    public ImportLedgerTests() { _db = new Db(_path); _db.OpenPlaintext(); }
    public void Dispose() { _db.DestroyFile(); }

    [Fact]
    public void Chunk_marks_and_checks_idempotently()
    {
        const string hash = "ABC123";
        Assert.False(_db.IsChunkImported(hash));
        _db.MarkChunkImported(hash, "diary.md");
        Assert.True(_db.IsChunkImported(hash));
        _db.MarkChunkImported(hash, "diary.md");   // re-mark is a no-op, not an error
        Assert.True(_db.IsChunkImported(hash));
    }

    [Fact]
    public void Ledger_survives_reopen_and_is_wiped_by_WipeAll()
    {
        _db.MarkChunkImported("H1", "a.md");
        _db.Close();
        var again = new Db(_path);
        again.OpenPlaintext();
        Assert.True(again.IsChunkImported("H1"));   // resume works across restarts
        again.WipeAll();
        Assert.False(again.IsChunkImported("H1"));
        again.Close();
    }
}
