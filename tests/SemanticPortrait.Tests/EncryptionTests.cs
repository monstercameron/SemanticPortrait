using SemanticPortrait.Core;

namespace SemanticPortrait.Tests;

public class EncryptionTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"sp_enc_{Guid.NewGuid():N}.db");
    private readonly string _vault = Path.Combine(Path.GetTempPath(), $"sp_vault_{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        foreach (var p in new[] { _db, _db + ".plaintext.bak", _db + ".enc", _vault })
            try { if (File.Exists(p)) File.Delete(p); } catch { }
    }

    private static string Now() => DateTime.UtcNow.ToString("o");

    // ---- KeyVault ----
    [Fact]
    public void Vault_pin_wrap_unwrap_roundtrips()
    {
        var v = new KeyVault(_vault);
        var k = v.CreateOrAddPin("1234");
        Assert.Equal(32, k.Length);

        var v2 = new KeyVault(_vault);                  // reload
        Assert.True(v2.HasPin);
        Assert.Equal(k, v2.UnwrapWithPin("1234"));      // correct pin recovers the exact key
        Assert.Null(v2.UnwrapWithPin("0000"));          // wrong pin → null

        var raw = File.ReadAllText(_vault);
        Assert.DoesNotContain(Convert.ToHexString(k), raw);   // raw key never on disk
    }

    [Fact]
    public void Legacy_vault_without_PinIter_still_unwraps_at_120k()
    {
        // Hand-build a vault exactly as the pre-versioning code wrote it: 120k iterations,
        // no PinIter field. Raising the default work factor must not lock these out.
        var k = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var wrapKey = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            System.Text.Encoding.UTF8.GetBytes("4321"), salt, 120_000,
            System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
        var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
        var ct = new byte[32]; var tag = new byte[16];
        using (var gcm = new System.Security.Cryptography.AesGcm(wrapKey, 16))
            gcm.Encrypt(nonce, k, ct, tag);
        var blob = new byte[12 + 32 + 16];
        Buffer.BlockCopy(nonce, 0, blob, 0, 12);
        Buffer.BlockCopy(ct, 0, blob, 12, 32);
        Buffer.BlockCopy(tag, 0, blob, 44, 16);
        File.WriteAllText(_vault, System.Text.Json.JsonSerializer.Serialize(new
        { PinSalt = Convert.ToBase64String(salt), PinWrap = Convert.ToBase64String(blob) }));

        Assert.Equal(k, new KeyVault(_vault).UnwrapWithPin("4321"));
    }

    [Fact]
    public void New_vault_records_the_raised_iteration_count()
    {
        var v = new KeyVault(_vault);
        var k = v.CreateOrAddPin("123456");
        Assert.Contains("600000", File.ReadAllText(_vault));       // PinIter persisted
        Assert.Equal(k, new KeyVault(_vault).UnwrapWithPin("123456"));
    }

    // ---- encrypted DB ----
    [Fact]
    public void Usage_persists_across_encrypted_restart()
    {
        var key = new KeyVault(_vault).CreateOrAddPin("1234");

        var a = new Db(_db);
        a.Open(key);
        a.AddUsage("gpt-5.5", 1000, 200, 0.011);
        a.Close();                                      // simulate app close

        var b = new Db(_db);
        b.Open(key);                                    // simulate restart + unlock
        a.GetType();                                    // (a is closed)
        var t = b.GetUsageTotals();
        Assert.Equal(1000, t.Input);
        Assert.Equal(200, t.Output);
        Assert.Equal(0.011, t.CostUsd, 5);

        b.AddUsage("gpt-5.5", 500, 100, 0.0055);        // accrues on top across sessions
        Assert.Equal(1500, b.GetUsageTotals().Input);
        b.Close();
    }

    [Fact]
    public void Encrypted_db_roundtrips_and_rejects_wrong_key()
    {
        var key = new KeyVault(_vault).CreateOrAddPin("1234");

        var a = new Db(_db);
        a.Open(key);
        a.AddMessage("user", "secret entry", Now());
        a.Close();

        var b = new Db(_db);
        b.Open(key);                                    // right key
        Assert.Contains(b.GetMessages(), m => m.Text == "secret entry");
        b.Close();

        var wrong = new byte[32];                       // all-zero wrong key
        var c = new Db(_db);
        Assert.ThrowsAny<Exception>(() => c.Open(wrong));
    }

    [Fact]
    public void Plaintext_db_is_migrated_to_encrypted_preserving_data()
    {
        // 1) create a legacy plaintext DB with data
        var p = new Db(_db);
        p.OpenPlaintext();
        p.AddMessage("user", "legacy entry", Now());
        p.Close();
        Assert.True(DbIsReadableWithoutKey(_db));       // confirm it's plaintext

        // 2) open with a key → triggers migration
        var key = new KeyVault(_vault).CreateOrAddPin("1234");
        var enc = new Db(_db);
        enc.Open(key);
        Assert.Contains(enc.GetMessages(), m => m.Text == "legacy entry");   // data preserved
        enc.Close();

        // 3) now encrypted: unreadable without the key, and no plaintext backup left behind
        Assert.False(DbIsReadableWithoutKey(_db));
        Assert.False(File.Exists(_db + ".plaintext.bak"));
    }

    [Fact]
    public void WipeAll_on_encrypted_db_does_not_throw_and_clears()
    {
        var key = new KeyVault(_vault).CreateOrAddPin("1234");
        var db = new Db(_db);
        db.Open(key);
        var m = db.AddMessage("user", "secret", Now());
        db.AddEmbedding("message", m, new float[] { 1, 2, 3 });
        db.UpsertNode("fire", "inventing", false, 1);

        db.WipeAll();                       // must not throw on an encrypted DB

        Assert.Empty(db.GetMessages());
        Assert.Empty(db.GetNodes());
        // DB still usable after wipe (re-add works)
        var m2 = db.AddMessage("user", "after wipe", Now());
        Assert.True(m2 > 0);
        db.Close();
    }

    [Fact]
    public void DestroyFile_deletes_db_then_reopen_is_empty()
    {
        var key = new KeyVault(_vault).CreateOrAddPin("1234");
        var a = new Db(_db); a.Open(key); a.AddMessage("user", "leak me", Now());
        a.DestroyFile();
        Assert.False(File.Exists(_db));            // file is gone, not just rows

        var b = new Db(_db); b.Open(key);           // recreated fresh + empty
        Assert.Empty(b.GetMessages());
        b.Close();
    }

    private static bool DbIsReadableWithoutKey(string path)
    {
        try
        {
            using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM sqlite_master;";
            cmd.ExecuteScalar();
            return true;
        }
        catch { return false; }
    }
}
