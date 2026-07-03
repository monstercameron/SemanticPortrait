using System.Text.Json;

namespace SemanticPortrait.Core;

/// <summary>
/// Persistent key/value store for facts about the user (identity, purpose, …), kept in the
/// ENCRYPTED database so nothing personal sits outside the central lock. Earlier builds wrote a
/// plaintext profile.json next to the DB — that was a bypass of the at-rest boundary; it is
/// migrated into the DB on first unlocked access and the file is deleted.
/// </summary>
public sealed class ProfileStore
{
    private readonly Db _db;
    private readonly string _legacyJsonPath;
    private bool _migrated;

    /// <param name="legacyJsonPath">Where the old plaintext profile.json lived (migrated + removed).</param>
    public ProfileStore(Db db, string legacyJsonPath) { _db = db; _legacyJsonPath = legacyJsonPath; }

    private void EnsureMigrated()
    {
        if (_migrated || !_db.IsOpen) return;
        // The dev sandbox is an isolated plaintext DB — importing the REAL profile there (and
        // deleting the file!) would leak it into dev and lose it for the encrypted DB.
        if (_db.IsDevSandbox) { _migrated = true; return; }
        _migrated = true;
        try
        {
            if (!File.Exists(_legacyJsonPath)) return;
            var fields = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_legacyJsonPath));
            if (fields is not null)
                foreach (var (k, v) in fields)
                    if (_db.GetProfileField(k) is null) _db.SetProfileField(k, v);
            File.Delete(_legacyJsonPath);   // the point of the migration: no plaintext copy outside the lock
        }
        catch { _migrated = false; }        // retry on the next call rather than silently dropping the file
    }

    public string? Get(string key)
    {
        EnsureMigrated();
        return _db.IsOpen ? _db.GetProfileField(key.Trim()) : null;
    }

    public void Set(string key, string value)
    {
        EnsureMigrated();
        _db.SetProfileField(key.Trim(), value.Trim());   // throws if locked — writes need the vault open
    }

    public IReadOnlyDictionary<string, string> All()
    {
        EnsureMigrated();
        return _db.IsOpen ? _db.AllProfileFields() : new Dictionary<string, string>();
    }

    /// <summary>Permanently delete all stored profile fields (and any lingering legacy file).</summary>
    public void Clear()
    {
        if (_db.IsOpen) _db.ClearProfile();
        try { if (File.Exists(_legacyJsonPath)) File.Delete(_legacyJsonPath); } catch { }
    }
}
