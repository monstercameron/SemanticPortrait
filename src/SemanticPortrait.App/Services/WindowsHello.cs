using Windows.Security.Credentials.UI;

namespace SemanticPortrait.App.Services;

/// <summary>Windows Hello consent (face / fingerprint / device PIN) via WinRT UserConsentVerifier.
/// Reliable in unpackaged desktop apps (unlike KeyCredentialManager).</summary>
public sealed class WindowsHello
{
    public async Task<bool> IsAvailableAsync()
    {
        try { return await UserConsentVerifier.CheckAvailabilityAsync() == UserConsentVerifierAvailability.Available; }
        catch { return false; }
    }

    public async Task<bool> VerifyAsync(string message)
    {
        try { return await UserConsentVerifier.RequestVerificationAsync(message) == UserConsentVerificationResult.Verified; }
        catch { return false; }
    }
}
