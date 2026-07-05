using SemanticPortrait.Core;

namespace SemanticPortrait.Tests;

public class ExportBackupTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sp_export_{Guid.NewGuid():N}.db");
    private readonly List<string> _cleanup = new();
    private readonly Db _db;
    private readonly ExportService _export;

    public ExportBackupTests()
    {
        _db = new Db(_path);
        _db.OpenPlaintext();
        _export = new ExportService(_db, new ProfileStore(_db, Path.GetTempFileName()));
    }

    public void Dispose()
    {
        _db.DestroyFile();
        foreach (var p in _cleanup) try { if (File.Exists(p)) File.Delete(p); } catch { }
    }

    private string Temp(string ext)
    {
        var p = Path.Combine(Path.GetTempPath(), $"sp_export_{Guid.NewGuid():N}.{ext}");
        _cleanup.Add(p);
        return p;
    }

    [Fact]
    public void Csv_escapes_quotes_and_filters_range()
    {
        _db.AddMessage("user", "said \"hi\", twice", "2026-06-01T10:00:00.0000000Z");
        _db.AddMessage("user", "outside range", "2026-06-20T10:00:00.0000000Z");
        var csv = _export.ToCsv(new DateTime(2026, 5, 30, 0, 0, 0, DateTimeKind.Utc),
                                new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc));
        Assert.Contains("\"said \"\"hi\"\", twice\"", csv);
        Assert.DoesNotContain("outside range", csv);
    }

    [Fact]
    public void Csv_neutralizes_formula_injection()
    {
        // An imported chat log could carry a cell that Excel would execute; the export prefixes a
        // leading = + - @ with an apostrophe so it lands as text, not a live formula.
        _db.AddMessage("user", "=HYPERLINK(\"http://evil\",\"x\")", "2026-06-01T10:00:00.0000000Z");
        var csv = _export.ToCsv();
        Assert.Contains("\"'=HYPERLINK", csv);          // apostrophe-guarded
        Assert.DoesNotContain(",\"=HYPERLINK", csv);    // never a bare leading =
    }

    [Fact]
    public void Mermaid_and_graphml_carry_the_graph()
    {
        var a = _db.UpsertNode("fire", "inventing", false, 1.0);
        var b = _db.UpsertNode("work", "Cash<Flux>", false, 0.8);
        _db.AddEdge(a, b, "feeds", "feeds", false, 0.8);

        var mmd = _export.ToMermaid();
        Assert.Contains("graph TD", mmd);
        Assert.Contains($"n{a}", mmd);
        Assert.Contains("-->|feeds|", mmd);

        var gml = _export.ToGraphML();
        Assert.Contains("<graphml", gml);
        Assert.Contains("Cash&lt;Flux&gt;", gml);   // XML-escaped
        Assert.Contains($"<edge source=\"n{a}\" target=\"n{b}\">", gml);
    }

    [Fact]
    public void Masked_export_pseudonymizes_regardless_of_setting()
    {
        _db.AddMessage("user", "mail me at sam@example.com", DateTime.UtcNow.ToString("o"));
        var mask = new RegexMasker(_db, () => false).Mask;               // user's egress masking OFF
        Func<string, string> forced = new RegexMasker(_db, () => true).Mask;

        var json = _export.ToJson(mask: forced);
        Assert.DoesNotContain("sam@example.com", json);
        Assert.Contains("EMAIL_1", json);
        Assert.Contains("\"masked\": true", json);
    }

    [Fact]
    public void Encrypted_backup_roundtrips_and_bad_restore_rolls_back()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"sp_bak_{Guid.NewGuid():N}.db");
        _cleanup.Add(dbPath);
        var key = new byte[32]; new Random(7).NextBytes(key);
        var db = new Db(dbPath);
        db.Open(key);
        db.AddMessage("user", "keep me safe", DateTime.UtcNow.ToString("o"));

        var backup = Temp("spdb");
        db.BackupTo(backup);
        Assert.True(File.Exists(backup));

        db.AddMessage("user", "written after the backup", DateTime.UtcNow.ToString("o"));
        db.RestoreFrom(backup, key);
        var texts = db.GetMessages().Select(m => m.Text).ToList();
        Assert.Contains("keep me safe", texts);
        Assert.DoesNotContain("written after the backup", texts);   // restore replaced everything

        // restoring garbage must throw AND leave the current data intact
        var garbage = Temp("spdb");
        File.WriteAllText(garbage, "this is not a database");
        Assert.ThrowsAny<Exception>(() => db.RestoreFrom(garbage, key));
        Assert.Contains("keep me safe", db.GetMessages().Select(m => m.Text));
        db.DestroyFile();
    }
}
