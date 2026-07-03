// recover mode — un-quarantine a dev sandbox that got encrypted in place.
//
// What happened (2026-07-02): an idle-lock fired while the app was redirected to the dev
// sandbox; the PIN unlock then ran Db.Open(realKey) against the sandbox path, whose
// plaintext-migration step encrypted the (freshly imported) sandbox with the REAL vault key.
// The next plaintext open hit SQLITE_NOTADB and quarantined it to "*.dev.notadb".
//
// This mode reverses that: unseal the vault key from hello.bin exactly like the app's Hello
// path (DPAPI CurrentUser + fixed entropy — by design it unseals for any process running as
// this Windows user), open the quarantined file keyed, sqlcipher_export it back to plaintext,
// and report per-table row counts so the operator can verify the import survived before
// swapping the file in. Never touches the live "*.dev" or the real DB.
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using SemanticPortrait.Core;

static class Recover
{
    private static string DefaultDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "User Name", "com.companyname.semanticportrait.app", "Data");

    public static int Run(string? dataDir)
    {
        dataDir ??= DefaultDataDir;
        var helloBin = Path.Combine(dataDir, "hello.bin");
        var quarantined = Path.Combine(dataDir, "semanticportrait.db.dev.notadb");
        var recovered = Path.Combine(dataDir, "semanticportrait.db.dev.recovered");

        if (!File.Exists(quarantined)) { Console.WriteLine($"nothing to recover: {quarantined} not found"); return 1; }
        if (!File.Exists(helloBin)) { Console.WriteLine($"no sealed key: {helloBin} not found"); return 1; }

        var entropy = Encoding.UTF8.GetBytes("SemanticPortrait.hello.v1");   // must match HelloKeyStore
        byte[]? key = null;
        try
        {
            key = ProtectedData.Unprotect(File.ReadAllBytes(helloBin), entropy, DataProtectionScope.CurrentUser);
            Console.WriteLine($"vault key unsealed ({key.Length} bytes)");

            if (File.Exists(recovered)) File.Delete(recovered);
            using (var c = new SqliteConnection($"Data Source={quarantined};Pooling=False"))
            {
                c.Open();
                Exec(c, $"PRAGMA key = \"{KeyVault.ToSqlCipherKey(key)}\";");
                var tables = Scalar(c, "SELECT count(*) FROM sqlite_master WHERE type='table';");
                Console.WriteLine($"decrypted OK — {tables} tables");

                Exec(c, $"ATTACH DATABASE '{recovered.Replace("'", "''")}' AS plain KEY '';" +
                        "SELECT sqlcipher_export('plain');" +
                        "DETACH DATABASE plain;");
            }
            SqliteConnection.ClearAllPools();

            Console.WriteLine($"\nexported plaintext → {recovered}\nrow counts:");
            using (var p = new SqliteConnection($"Data Source={recovered};Mode=ReadOnly;Pooling=False"))
            {
                p.Open();
                using var cmd = p.CreateCommand();
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
                var names = new List<string>();
                using (var r = cmd.ExecuteReader()) while (r.Read()) names.Add(r.GetString(0));
                foreach (var t in names)
                    Console.WriteLine($"  {t,-24} {Scalar(p, $"SELECT count(*) FROM \"{t}\";")}");
            }
            SqliteConnection.ClearAllPools();
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine($"RECOVERY FAILED: {e.Message}");
            return 1;
        }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }

    private static void Exec(SqliteConnection c, string sql)
    { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

    private static long Scalar(SqliteConnection c, string sql)
    { using var cmd = c.CreateCommand(); cmd.CommandText = sql; return (long)cmd.ExecuteScalar()!; }
}
