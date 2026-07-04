using Microsoft.Data.Sqlite;

namespace SemanticPortrait.Core;

public sealed partial class Db : IDisposable
{
    private SqliteConnection? _conn;
    private readonly object _gate = new();
    private string _dbPath;

    // A clear "locked" error instead of a NullReferenceException when a background worker
    // (analyst / compactor / reminder tick) races the user locking the app.
    private SqliteConnection Conn =>
        _conn ?? throw new InvalidOperationException("database is locked (connection closed)");

    // Runs body inside a single transaction on the shared connection, so a multi-statement write
    // is all-or-nothing. Caller MUST already hold _gate. Every command created inside body MUST
    // set `cmd.Transaction = tx` — Microsoft.Data.Sqlite throws otherwise (a command on a
    // connection with a pending transaction requires that transaction to be attached explicitly).
    private void InTransaction(Action<SqliteTransaction> body)
    {
        using var tx = Conn.BeginTransaction();
        body(tx);
        tx.Commit();
    }

    private T InTransaction<T>(Func<SqliteTransaction, T> body)
    {
        using var tx = Conn.BeginTransaction();
        var result = body(tx);
        tx.Commit();
        return result;
    }

    /// <summary>Idempotent. Closes the underlying connection if still open.</summary>
    public void Dispose()
    {
        lock (_gate) { _conn?.Dispose(); _conn = null; }
    }

    static Db() => SQLitePCL.Batteries_V2.Init();   // register the SQLCipher provider

    // Pooling=False so migration can delete/move the file without a lingering handle.
    private static string ConnStr(string path) => $"Data Source={path};Pooling=False";

    /// <param name="dbPath">Full path to the SQLite file. The DB is NOT opened until Open(key).</param>
    public Db(string dbPath) => _dbPath = dbPath;

    public bool IsOpen => _conn is not null;

    /// <summary>True once this instance has been redirected to a dev sandbox file.</summary>
    public bool IsSandbox => _dbPath.EndsWith(".dev") || _dbPath.EndsWith(".dev-fresh");

    /// <summary>The current backing file — for caches keyed by database identity.</summary>
    public string CurrentPath => _dbPath;

    /// <summary>
    /// Open the encrypted database with the derived AES key. If a legacy plaintext DB exists, it is
    /// migrated into an encrypted copy first (a one-time *.plaintext.bak is taken, then removed once
    /// the encrypted DB verifies). Idempotent.
    /// </summary>
    public void Open(byte[] key)
    {
        lock (_gate)
        {
            if (_conn is not null) return;
            // A keyed open on a sandbox path is ALWAYS a wiring bug (lock screen raised while
            // redirected to the sandbox). Observed live 2026-07-02: the migration below encrypted
            // a freshly-imported plaintext sandbox in place with the real vault key, and the next
            // plaintext open quarantined it as *.notadb. Refuse loudly instead.
            if (IsSandbox)
                throw new InvalidOperationException(
                    "Open(key) on a dev sandbox path — refusing (would encrypt the plaintext sandbox in place). " +
                    "Sandboxes open via OpenDevSandbox() only.");
            if (File.Exists(_dbPath) && IsPlaintext(_dbPath)) MigrateToEncrypted(_dbPath, key);

            _conn = new SqliteConnection(ConnStr(_dbPath));
            try
            {
                _conn.Open();
                Exec($"PRAGMA key = \"{KeyVault.ToSqlCipherKey(key)}\";");
                EnsureSchema();
            }
            catch
            {
                // e.g. wrong key: don't stay half-open (IsOpen would lie and later Open()s would no-op)
                _conn.Dispose();
                _conn = null;
                throw;
            }

            // encryption verified working → remove any lingering plaintext backup
            try { var bak = _dbPath + ".plaintext.bak"; if (File.Exists(bak)) File.Delete(bak); } catch { }
        }
    }

    private static bool IsPlaintext(string path)
    {
        try
        {
            using var c = new SqliteConnection(ConnStr(path));
            c.Open();                                  // SQLCipher with no PRAGMA key = plain SQLite
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM sqlite_master;";
            cmd.ExecuteScalar();
            return true;                               // read without a key → it's plaintext
        }
        catch { return false; }                        // unreadable without key → already encrypted
    }

    private static void MigrateToEncrypted(string path, byte[] key)
    {
        File.Copy(path, path + ".plaintext.bak", true);     // safety backup (deleted after verify)
        var enc = path + ".enc";
        if (File.Exists(enc)) File.Delete(enc);
        using (var c = new SqliteConnection(ConnStr(path)))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText =
                $"ATTACH DATABASE '{enc.Replace("'", "''")}' AS enc KEY \"{KeyVault.ToSqlCipherKey(key)}\";" +
                "SELECT sqlcipher_export('enc');" +
                "DETACH DATABASE enc;";
            cmd.ExecuteNonQuery();
        }
        File.Delete(path);
        File.Move(enc, path);
    }

    /// <summary>Close the connection (e.g. on re-lock) — data becomes inaccessible until reopened.</summary>
    public void Close()
    {
        lock (_gate) { _conn?.Close(); _conn?.Dispose(); _conn = null; }
    }

    /// <summary>Close and DELETE the database file(s) entirely (used by Erase — certain, not row-by-row).</summary>
    public void DestroyFile()
    {
        lock (_gate)
        {
            _conn?.Close(); _conn?.Dispose(); _conn = null;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var p in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm",
                                      _dbPath + ".plaintext.bak", _dbPath + ".enc" })
                try { if (File.Exists(p)) File.Delete(p); } catch { }
        }
    }

    /// <summary>
    /// Write a consistent, self-contained backup of the LIVE database to <paramref name="destPath"/>
    /// via VACUUM INTO. On an encrypted DB the copy is encrypted with the SAME key — the backup
    /// file never contains plaintext, and it includes everything (embeddings, settings, ledger).
    /// </summary>
    public void BackupTo(string destPath)
    {
        lock (_gate)
        {
            if (File.Exists(destPath)) File.Delete(destPath);
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "VACUUM INTO $p;";
            cmd.Parameters.AddWithValue("$p", destPath);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Replace the live database with a backup file, then reopen with <paramref name="key"/>
    /// (null → plaintext open, dev only). The replaced DB is kept as *.pre-restore.bak until the
    /// restored copy opens successfully. Throws if the backup can't be opened with the key.
    /// </summary>
    public void RestoreFrom(string backupPath, byte[]? key)
    {
        lock (_gate)
        {
            _conn?.Close(); _conn?.Dispose(); _conn = null;
            SqliteConnection.ClearAllPools();
            var safety = _dbPath + ".pre-restore.bak";
            if (File.Exists(_dbPath)) File.Copy(_dbPath, safety, true);
            try
            {
                File.Copy(backupPath, _dbPath, true);
                foreach (var w in new[] { _dbPath + "-wal", _dbPath + "-shm" })
                    try { if (File.Exists(w)) File.Delete(w); } catch { }
                if (key is not null) Open(key); else OpenPlaintext();
            }
            catch
            {
                // roll back to the pre-restore database so a bad backup can't brick the app
                try { if (File.Exists(safety)) File.Copy(safety, _dbPath, true); } catch { }
                if (key is not null) Open(key); else OpenPlaintext();
                throw;
            }
            try { if (File.Exists(safety)) File.Delete(safety); } catch { }
        }
    }

    /// <summary>Open WITHOUT encryption (for users who decline the lock). No migration.</summary>
    public void OpenPlaintext()
    {
        lock (_gate)
        {
            if (_conn is not null) return;
            _conn = new SqliteConnection(ConnStr(_dbPath));
            _conn.Open();
            EnsureSchema();
        }
    }

    /// <summary>
    /// DEV-ONLY sandbox: open an ISOLATED plaintext database so dev runs never touch — or need
    /// the key for — the real encrypted DB. The instance is redirected to the sandbox file for
    /// the rest of its life. Callers gate this behind #if DEBUG.
    /// TWO sandboxes, deliberately separate files: persistent mode lives in "*.dev" (data
    /// survives runs); <paramref name="fresh"/> mode lives in "*.dev-fresh", wiped at every open —
    /// a scratchpad that never disturbs the persistent sandbox, so flipping between modes keeps
    /// the persistent world intact.
    /// </summary>
    public void OpenDevSandbox(bool fresh = false)
    {
        lock (_gate)
        {
            if (_conn is not null) return;
            // Redirect from the REAL path; real encrypted DB untouched. Recomputed from the base
            // so a reopen after Close() on the same instance doesn't stack suffixes (observed
            // live: a stray "*.dev.dev" file) and a mode switch lands on the right sandbox.
            var basePath = IsSandbox ? _dbPath[.._dbPath.LastIndexOf(".dev", StringComparison.Ordinal)] : _dbPath;
            _dbPath = basePath + (fresh ? ".dev-fresh" : ".dev");
            if (fresh) WipeSandboxFiles();             // wipes ONLY the scratch sandbox
            try
            {
                OpenSandboxCore();
            }
            catch (SqliteException e) when (e.SqliteErrorCode == 26 /* SQLITE_NOTADB */)
            {
                // The sandbox file isn't plaintext SQLite (e.g. an encrypted leftover from a
                // run that keyed it). Sandbox data is disposable — quarantine it and start
                // fresh instead of killing the app at startup. NEVER do this for the real DB:
                // there, NOTADB means "wrong/missing key" and the data must be preserved.
                _conn?.Dispose(); _conn = null;
                SqliteConnection.ClearAllPools();
                File.Move(_dbPath, _dbPath + ".notadb", overwrite: true);
                foreach (var w in new[] { _dbPath + "-wal", _dbPath + "-shm" })
                    try { if (File.Exists(w)) File.Delete(w); } catch { }
                OpenSandboxCore();
            }
        }
    }

    /// <summary>
    /// DEV-ONLY: wipe and reopen the sandbox in place (the in-app "reset sandbox" button). Refuses
    /// unless the CURRENT path is a sandbox file — this must never be reachable for the real DB.
    /// </summary>
    public void ResetDevSandbox()
    {
        lock (_gate)
        {
            if (!_dbPath.EndsWith(".dev", StringComparison.Ordinal) &&
                !_dbPath.EndsWith(".dev-fresh", StringComparison.Ordinal))
                throw new InvalidOperationException("ResetDevSandbox is only valid on a dev sandbox.");
            _conn?.Dispose(); _conn = null;
            SqliteConnection.ClearAllPools();
            WipeSandboxFiles();
            OpenSandboxCore();
        }
    }

    // Called under _gate with _dbPath already pointing at the sandbox.
    private void WipeSandboxFiles()
    {
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm", _dbPath + ".notadb" })
            try { if (File.Exists(f)) File.Delete(f); } catch { }
    }

    private void OpenSandboxCore()
    {
        _conn = new SqliteConnection(ConnStr(_dbPath));
        _conn.Open();
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS messages (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                created_utc TEXT NOT NULL,
                role        TEXT NOT NULL,
                text        TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS notes (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                text        TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS embeddings (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                ref_type  TEXT NOT NULL,
                ref_id    INTEGER NOT NULL,
                dim       INTEGER NOT NULL,
                vec       BLOB NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_emb_ref ON embeddings(ref_type, ref_id);
            -- detail holds expandable payloads for tool-call rows (role='tool')
            """);

        // lightweight migration: add messages.detail if missing (existing DBs)
        try { Exec("ALTER TABLE messages ADD COLUMN detail TEXT;"); } catch { /* already present */ }
        Exec("""

            CREATE TABLE IF NOT EXISTS nodes (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                category    TEXT NOT NULL,
                label       TEXT NOT NULL,
                inferred    INTEGER NOT NULL DEFAULT 0,
                confidence  REAL NOT NULL DEFAULT 1.0,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_node ON nodes(category, label);

            CREATE TABLE IF NOT EXISTS edges (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                src_id      INTEGER NOT NULL,
                dst_id      INTEGER NOT NULL,
                type        TEXT NOT NULL,
                label       TEXT NOT NULL DEFAULT '',
                inferred    INTEGER NOT NULL DEFAULT 0,
                confidence  REAL NOT NULL DEFAULT 1.0,
                created_utc TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_edge ON edges(src_id, dst_id, type);

            -- Contemporaneous metadata per entry. Columns are NOT NULL with CHECK ranges so
            -- partial/lazy writes are rejected at the storage layer.
            CREATE TABLE IF NOT EXISTS entry_meta (
                message_id  INTEGER PRIMARY KEY,
                entry_utc   TEXT NOT NULL,
                mood        TEXT NOT NULL CHECK(length(trim(mood)) > 0),
                valence     REAL NOT NULL CHECK(valence  >= -1 AND valence  <= 1),
                intensity   REAL NOT NULL CHECK(intensity >= 0 AND intensity <= 1),
                energy      REAL NOT NULL CHECK(energy    >= 0 AND energy    <= 1),
                topics      TEXT NOT NULL CHECK(length(trim(topics))  > 0),
                people      TEXT NOT NULL,
                summary     TEXT NOT NULL CHECK(length(trim(summary)) > 0),
                FOREIGN KEY(message_id) REFERENCES messages(id)
            );

            -- rolling compaction of everything older than the in-flight window
            CREATE TABLE IF NOT EXISTS compaction (
                id          INTEGER PRIMARY KEY CHECK(id = 1),
                summary     TEXT NOT NULL,
                through_utc TEXT NOT NULL
            );

            -- user todos
            CREATE TABLE IF NOT EXISTS todos (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                created_utc TEXT NOT NULL,
                text        TEXT NOT NULL,
                done        INTEGER NOT NULL DEFAULT 0,
                done_utc    TEXT
            );

            -- time-based reminders (the agent proactively messages when due)
            CREATE TABLE IF NOT EXISTS reminders (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                created_utc TEXT NOT NULL,
                due_utc     TEXT NOT NULL,
                text        TEXT NOT NULL,
                fired       INTEGER NOT NULL DEFAULT 0
            );

            -- timestamped life events (contemporaneous + recounted), for time inferences
            CREATE TABLE IF NOT EXISTS events (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                event_utc   TEXT NOT NULL,
                summary     TEXT NOT NULL,
                created_utc TEXT NOT NULL
            );

            -- calibration: falsifiable predictions scored against reality
            CREATE TABLE IF NOT EXISTS predictions (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                created_utc  TEXT NOT NULL,
                claim        TEXT NOT NULL,
                criterion    TEXT NOT NULL,
                due_utc      TEXT,
                resolved_utc TEXT,
                outcome      TEXT,
                score        REAL
            );

            -- cumulative token spend, one row per model (lifetime, across sessions)
            CREATE TABLE IF NOT EXISTS usage (
                model       TEXT PRIMARY KEY,
                input_tok   INTEGER NOT NULL DEFAULT 0,
                output_tok  INTEGER NOT NULL DEFAULT 0,
                calls       INTEGER NOT NULL DEFAULT 0,
                cost_usd    REAL    NOT NULL DEFAULT 0,
                updated_utc TEXT NOT NULL
            );

            -- same spend bucketed by calendar month (period = local 'yyyy-MM'); drives the
            -- monthly-reset display so the user never stares at an ever-growing lifetime total.
            CREATE TABLE IF NOT EXISTS usage_monthly (
                period      TEXT NOT NULL,
                model       TEXT NOT NULL,
                input_tok   INTEGER NOT NULL DEFAULT 0,
                output_tok  INTEGER NOT NULL DEFAULT 0,
                calls       INTEGER NOT NULL DEFAULT 0,
                cost_usd    REAL    NOT NULL DEFAULT 0,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY(period, model)
            );

            -- in-app notification feed (the bell + drawer). One row per delivered notification.
            CREATE TABLE IF NOT EXISTS notifications (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                created_utc TEXT NOT NULL,
                ref_type    TEXT NOT NULL,            -- e.g. 'reminder'
                ref_id      INTEGER NOT NULL,
                title       TEXT NOT NULL,
                body        TEXT NOT NULL,            -- full text in-app (DB is encrypted at rest)
                read        INTEGER NOT NULL DEFAULT 0,
                surfaced    INTEGER NOT NULL DEFAULT 0 -- agent already brought it up in-thread?
            );
            """);

        Exec("""

            -- egress masking alias map: original PII <-> stable token (e.g. "Alice" <-> PERSON_1).
            -- Stays LOCAL + encrypted, never sent. Doubles as the canonical-entity registry (plan §6).
            CREATE TABLE IF NOT EXISTS aliases (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                kind        TEXT NOT NULL,            -- PERSON / EMAIL / PHONE / ...
                original    TEXT NOT NULL,
                token       TEXT NOT NULL UNIQUE,
                created_utc TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_alias_orig ON aliases(kind, original);

            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            -- identity facts about the user (name, purpose, key people). Lives INSIDE the
            -- encrypted DB so nothing personal sits outside the lock (was plaintext profile.json).
            CREATE TABLE IF NOT EXISTS profile (
                key   TEXT PRIMARY KEY COLLATE NOCASE,
                value TEXT NOT NULL
            );

            -- canonical entity registry (schema v2 §2): one row per real-world entity; mentions
            -- (nicknames, spelling variants) resolve to the canonical name so graph nodes merge
            -- instead of duplicating.
            CREATE TABLE IF NOT EXISTS entities (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                canonical_name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                kind           TEXT NOT NULL DEFAULT 'person',
                created_utc    TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS entity_aliases (
                entity_id   INTEGER NOT NULL REFERENCES entities(id),
                mention     TEXT NOT NULL UNIQUE COLLATE NOCASE,
                created_utc TEXT NOT NULL
            );

            -- periodic self-model metrics snapshots (schema v2 §6): JSON payload per snapshot so
            -- new metrics need no migration; feeds trend views / the visual fingerprint later.
            CREATE TABLE IF NOT EXISTS metrics (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                snapshot_utc TEXT NOT NULL,
                payload      TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_metrics_utc ON metrics(snapshot_utc);

            -- bulk-import ledger: one row per successfully analyzed chunk (content hash), so a
            -- died import resumes where it stopped and re-importing a file never double-adds.
            CREATE TABLE IF NOT EXISTS import_ledger (
                hash         TEXT PRIMARY KEY,
                source       TEXT NOT NULL,
                imported_utc TEXT NOT NULL
            );

            -- offline analysis queue: reflections that failed on a provider error wait here and
            -- retry when the provider is reachable again (capture/store/render never block).
            CREATE TABLE IF NOT EXISTS pending_analyses (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                entry_id    INTEGER NOT NULL,
                payload     TEXT NOT NULL,
                attempts    INTEGER NOT NULL DEFAULT 0,
                created_utc TEXT NOT NULL
            );
            """);

        // lightweight migrations for existing DBs
        try { Exec("ALTER TABLE reminders ADD COLUMN is_private INTEGER NOT NULL DEFAULT 0;"); } catch { /* present */ }
        try { Exec("ALTER TABLE notifications ADD COLUMN surfaced INTEGER NOT NULL DEFAULT 0;"); } catch { /* present */ }
        try { Exec("ALTER TABLE predictions ADD COLUMN notified INTEGER NOT NULL DEFAULT 0;"); } catch { /* present */ }
        try { Exec("ALTER TABLE todos ADD COLUMN due_utc TEXT;"); } catch { /* present */ }
        // Image attachments live in the ENCRYPTED DB (blob), so a photo of a hard day is behind
        // the same lock as the words about it — never a loose file on disk.
        try
        {
            Exec("""
                CREATE TABLE IF NOT EXISTS attachments (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    message_id  INTEGER NOT NULL,
                    kind        TEXT NOT NULL DEFAULT 'image',
                    mime        TEXT NOT NULL,
                    thumb       BLOB NOT NULL,   -- small inline-render copy (fast thread paint)
                    bytes       BLOB NOT NULL,   -- full display copy (lightbox / export)
                    caption     TEXT,
                    created_utc TEXT NOT NULL,
                    FOREIGN KEY(message_id) REFERENCES messages(id)
                );
                CREATE INDEX IF NOT EXISTS ix_attach_msg ON attachments(message_id);
                """);
        }
        catch { /* present */ }
    }

    /// <summary>Permanently delete ALL data — including settings/API keys — and reset ids
    /// (same end state as the Erase flow's DestroyFile, minus the file swap).</summary>
    public void WipeAll()
    {
        lock (_gate)
        {
            // Delete each table independently; tolerate a missing table (e.g. sqlite_sequence
            // doesn't exist until an AUTOINCREMENT row has been inserted). No VACUUM (it can fail
            // on an open SQLCipher connection and isn't needed to clear data). All-or-nothing:
            // wrapped in one transaction so a mid-loop failure can't leave a partially-wiped DB.
            InTransaction(tx =>
            {
                foreach (var t in new[] { "embeddings", "notes", "messages", "edges", "nodes",
                                          "entry_meta", "compaction", "predictions", "events",
                                          "todos", "reminders", "usage", "usage_monthly", "notifications",
                                          "aliases", "settings", "profile",
                                          "entities", "entity_aliases", "metrics", "import_ledger", "pending_analyses", "sqlite_sequence" })
                {
                    try
                    {
                        using var cmd = Conn.CreateCommand();
                        cmd.Transaction = tx;
                        cmd.CommandText = $"DELETE FROM {t};";
                        cmd.ExecuteNonQuery();
                    }
                    catch { /* table may not exist */ }
                }
            });
        }
    }

    // --- helpers --------------------------------------------------------------
    private void Exec(string sql)
    {
        using var cmd = Conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static byte[] ToBytes(float[] v)
    {
        var b = new byte[v.Length * sizeof(float)];
        Buffer.BlockCopy(v, 0, b, 0, b.Length);
        return b;
    }

    private static float[] FromBytes(byte[] b)
    {
        var v = new float[b.Length / sizeof(float)];
        Buffer.BlockCopy(b, 0, v, 0, b.Length);
        return v;
    }

    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length) return -1;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return (na == 0 || nb == 0) ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
