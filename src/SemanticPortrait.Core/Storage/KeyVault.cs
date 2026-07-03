using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SemanticPortrait.Core;

/// <summary>
/// Holds the database encryption key (K), wrapped so the raw key is NEVER stored here. K is a
/// random 256-bit key generated once. It is wrapped (AES-256-GCM) by:
///   • a PIN-derived key (PBKDF2-SHA256) — the cryptographic path: unwrapping REQUIRES the PIN;
///     forgetting it means K is unrecoverable, by design.
/// Honest limit: the app's Windows Hello CONVENIENCE path does not live here — it keeps a
/// DPAPI-sealed copy of K (HelloKeyStore) whose Hello prompt is UI gating, not cryptography; any
/// process running as the same Windows user could unseal it. Protects against device theft
/// (DPAPI needs the account credentials), not same-user malware. The PIN wrap is the real lock.
/// </summary>
public sealed class KeyVault
{
    private sealed class Vault
    {
        public string? PinSalt { get; set; }
        public string? PinWrap { get; set; }
        public int? PinIter { get; set; }       // absent in legacy vaults → LegacyIter
    }

    // Iteration count is stored per-vault so it can be raised without breaking existing wraps:
    // new/changed PINs get the current count; unwrap uses whatever the wrap was created with.
    private const int LegacyIter = 120_000;
    private const int Iter = 600_000;           // ~OWASP 2023+ guidance for PBKDF2-SHA256
    private readonly string _path;
    private Vault _v = new();
    private bool _loaded;

    public KeyVault(string jsonPath) => _path = jsonPath;

    private void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try { if (File.Exists(_path)) _v = JsonSerializer.Deserialize<Vault>(File.ReadAllText(_path)) ?? new(); }
        catch { _v = new(); }
    }
    // MUST throw on failure: if a new PIN wrap isn't persisted, the DB gets encrypted with a key
    // that exists nowhere — silently swallowing that is unrecoverable data loss at next lock.
    private void Save() => File.WriteAllText(_path, JsonSerializer.Serialize(_v));

    public bool Exists { get { Load(); return _v.PinWrap is not null; } }
    public bool HasPin { get { Load(); return _v.PinWrap is not null; } }
    // (The old "declined / no lock" state is gone: the lock is mandatory. A legacy vault file
    //  with a Declined flag simply parses without it and reads as not-set-up.)

    /// <summary>Create K (if needed) and add a PIN wrap. Returns K.</summary>
    public byte[] CreateOrAddPin(string pin, byte[]? existingK = null)
    {
        Load();
        var k = existingK ?? RandomNumberGenerator.GetBytes(32);
        var salt = RandomNumberGenerator.GetBytes(16);
        var wrapKey = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, Iter, HashAlgorithmName.SHA256, 32);
        _v.PinSalt = Convert.ToBase64String(salt);
        _v.PinWrap = Wrap(k, wrapKey);
        _v.PinIter = Iter;
        Save();
        return k;
    }

    public byte[]? UnwrapWithPin(string pin)
    {
        Load();
        if (_v.PinWrap is null || _v.PinSalt is null) return null;
        var wrapKey = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin),
            Convert.FromBase64String(_v.PinSalt), _v.PinIter ?? LegacyIter, HashAlgorithmName.SHA256, 32);
        return Unwrap(_v.PinWrap, wrapKey);
    }

    public void Clear() { _v = new(); _loaded = true; try { if (File.Exists(_path)) File.Delete(_path); } catch { } }

    /// <summary>SQLCipher raw-key form for a 32-byte key: x'HEX' (no KDF — key used directly).</summary>
    public static string ToSqlCipherKey(byte[] k) => "x'" + Convert.ToHexString(k) + "'";

    // --- AES-256-GCM wrap/unwrap ---
    private static string Wrap(byte[] plaintext, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ct = new byte[plaintext.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(nonce, plaintext, ct, tag);
        var blob = new byte[12 + ct.Length + 16];
        Buffer.BlockCopy(nonce, 0, blob, 0, 12);
        Buffer.BlockCopy(ct, 0, blob, 12, ct.Length);
        Buffer.BlockCopy(tag, 0, blob, 12 + ct.Length, 16);
        return Convert.ToBase64String(blob);
    }

    private static byte[]? Unwrap(string blobB64, byte[] key)
    {
        try
        {
            var blob = Convert.FromBase64String(blobB64);
            var nonce = blob[..12];
            var tag = blob[^16..];
            var ct = blob[12..^16];
            var pt = new byte[ct.Length];
            using var gcm = new AesGcm(key, 16);
            gcm.Decrypt(nonce, ct, tag, pt);
            return pt;
        }
        catch { return null; }   // wrong credential / tampered
    }

}
