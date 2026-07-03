using System.Security.Cryptography;
using SemanticPortrait.Core;

namespace SemanticPortrait.App.Services;

/// <summary>
/// Stores the DB key sealed with DPAPI (CurrentUser) for the Windows Hello unlock path. The blob is
/// bound to the Windows account; access is gated at unlock time by a real Hello consent prompt
/// (UserConsentVerifier). PIN remains the recovery factor; this is the convenience path.
/// Honest limit: the Hello prompt is app-level UI gating, NOT part of the cryptography — DPAPI
/// will unseal for ANY process running as this Windows user. Guards against device theft (DPAPI
/// requires the account credentials), not same-user malware. See docs/threat-model.md.
/// </summary>
public sealed class HelloKeyStore
{
    private static readonly byte[] Entropy = System.Text.Encoding.UTF8.GetBytes("SemanticPortrait.hello.v1");
    private readonly string _path;

    public HelloKeyStore(string path) => _path = path;

    public bool Exists => File.Exists(_path);

    public void Seal(byte[] key)
    {
        // A failed seal means the Hello convenience unlock silently won't exist next launch
        // (PIN still works) — worth seeing in dev.
        try { File.WriteAllBytes(_path, ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser)); }
        catch (Exception e) { DevTrap.Report("hello-seal", e); }
    }

    public byte[]? Unseal()
    {
        if (!File.Exists(_path)) return null;   // no seal yet — the normal first-run path
        // Existing blob that won't unprotect (DPAPI key change, corruption) → null falls back
        // to PIN, but the cause should be visible in dev.
        try { return ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser); }
        catch (Exception e) { DevTrap.Report("hello-unseal", e); return null; }
    }

    public void Clear() { try { if (File.Exists(_path)) File.Delete(_path); } catch { } }
}
