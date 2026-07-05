using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using SemanticPortrait.App.Services;
using SemanticPortrait.Core;

namespace SemanticPortrait.App.Components.Pages;

// The central lock: setup/enrollment, Hello/PIN unlock (with brute-force backoff), idle
// auto-lock, and the Security panel. Everything in the app sits behind this gate.
public partial class Home
{
    // Faint constellation network drawn over the lock-screen aurora (matches the design mock).
    // Empty layers; constellation.js populates + animates them so line endpoints track the nodes.
    private static readonly MarkupString LockConstellation = (MarkupString)(
        "<svg class=\"sp-lock-net\" viewBox=\"0 0 1200 700\" preserveAspectRatio=\"xMidYMid slice\" aria-hidden=\"true\">" +
        "<g class=\"sp-net-lines\"></g><g class=\"sp-net-dots\"></g></svg>");
    private bool _netRunning;

    // lock state
    private bool _locked;
    private bool _unlocking;     // true during the exit animation (lock fades out, app revealed)
    private bool _configuring;
    private bool _helloAvailable;
    private bool _cfgHello;
    private bool _helloBusy;
    private bool _cfgMask = true;   // onboarding masking consent (default on / recommended)
    private bool _maskOn;           // current masking setting (Security panel mirror)
    private string _cfgPin = "";
    private string _cfgPin2 = "";
    private string _pinEntry = "";
    private string _lockMsg = "";

    // security settings
    private byte[]? _sessionKey;     // the live DB key while unlocked (for re-wrapping)
    private bool _showSecurity;

    private string _newPin = "";
    private string _newPin2 = "";
    private string _secMsg = "";

    // auto-lock on idle (only when a lock is configured)
    private System.Timers.Timer? _idle;
    private const int IdleMinutes = 15;

    private void StartIdle()
    {
#if DEBUG
        if (_devUnlocked) return;   // dev_unlock: no idle re-lock while developing against real data
#endif
        // Gate on THIS SESSION holding the encrypted DB (_sessionKey), not on Vault.Exists:
        // the vault existing on disk says nothing about what this session opened. Observed live:
        // a DEV SANDBOX session idle-locked because the real vault existed — Database.Close()
        // fired mid-import ("database is locked (connection closed)"), and the PIN screen it
        // raised would have tried the real key against the plaintext sandbox file.
        if (_sessionKey is null) return;
        _idle ??= new System.Timers.Timer { AutoReset = false };
        _idle.Interval = IdleMinutes * 60 * 1000;
        _idle.Elapsed -= IdleElapsed;
        _idle.Elapsed += IdleElapsed;
        _idle.Stop(); _idle.Start();
    }
    private void ResetIdle() { if (_idle is not null) { _idle.Stop(); _idle.Start(); } }
    private void IdleElapsed(object? s, System.Timers.ElapsedEventArgs e) =>
        _ = InvokeAsync(async () => { if (!_locked && !_configuring && _sessionKey is not null) { await LockNow(); StateHasChanged(); } })
            .Guard("idle-lock");   // a fault here means the idle re-lock silently stopped working

    // A sandbox session that locks must UNLOCK back into the sandbox: every other unlock path
    // reopens with the real key, and a key-open against the plaintext sandbox encrypts it in
    // place (observed 2026-07-02: a fresh hour-long import quarantined as *.notadb; Db.Open(key)
    // now also refuses sandbox paths as defense-in-depth). Auth still verifies the REAL vault.
    private bool _relockToSandbox;

    private async Task LockNow()
    {
#if DEBUG
        _devUnlocked = false;       // a deliberate lock ends the dev bypass for this session
#endif
        if (!Vault.Exists) { _configuring = true; return; }   // not set up yet → configure
        if (_locked) return;

        // Cover FIRST, clear second: the lock veil blur/scales in OVER the live app, and only
        // once it's opaque does anything visibly vanish — locking mirrors the unlock animation.
        _relockToSandbox = Database.IsSandbox;
        _locked = true; _pinEntry = ""; _lockMsg = "";
        StateHasChanged();
        await Task.Delay(650);          // sp-lock-in completes

        Database.Close();               // data becomes inaccessible until re-auth
        _reminders?.Stop();
        // Leave nothing personal readable in memory-backed UI state while locked:
        _messages.Clear();
        _notifs.Clear();
        _constModel = null;
        Trace.Clear();                  // dev traces carry entry text
        // Scrub the key bytes before dropping the reference — shrinks the memory-dump window.
        if (_sessionKey is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(_sessionKey);
        _sessionKey = null;
        StateHasChanged();
    }

    // Persist the onboarding masking choice (asked once, changeable later in Security).
    private void SaveMaskConsent() =>
        Microsoft.Maui.Storage.Preferences.Default.Set("masking", _cfgMask);

    private async Task SetupHello()
    {
        _lockMsg = "";
        _helloBusy = true; StateHasChanged();
        var ok = await Hello.VerifyAsync("Set up Windows Hello for SemanticPortrait");  // real Hello prompt
        _helloBusy = false;
        if (ok) _cfgHello = true;
        else _lockMsg = L["Lock.HelloDidNotComplete"];
        StateHasChanged();
    }

    // A short all-numeric passcode is the weak case: ~20 bits for 6 digits — brute-forceable
    // offline from a stolen keyvault.json even at 600k PBKDF2 iterations. Require letters, or 8+
    // digits. Gates only NEW/changed passcodes; existing ones still unlock unchanged.
    private static bool IsPasscodeTooWeak(string pin)
    {
        if (pin.Length >= 8) return false;
        foreach (var c in pin) if (!char.IsDigit(c)) return false;   // has a letter/symbol → strong enough
        return true;                                                 // all digits and under 8
    }

    private void SaveSetup()
    {
        _lockMsg = "";
        var pin = _cfgPin.Trim();
        // This passcode is the cryptographic floor of the whole vault. A 6-digit numeric PIN is
        // only ~20 bits — offline-brute-forceable on a stolen device even at 600k PBKDF2 iters.
        // Encourage a longer alphanumeric passphrase; require at least 6 chars.
        if (pin.Length < 6) { _lockMsg = L["Lock.PinTooShort"]; return; }
        if (IsPasscodeTooWeak(pin)) { _lockMsg = L["Lock.PinTooWeak"]; return; }
        if (pin != _cfgPin2.Trim()) { _lockMsg = L["Lock.PinsDontMatch"]; return; }

        try
        {
            SaveMaskConsent();
            var key = Vault.CreateOrAddPin(pin);                   // random AES key, wrapped by the PIN
                                                                   // (throws if the wrap can't persist —
                                                                   //  never encrypt with an unsaved key)
            if (_cfgHello) HelloKeys.Seal(key);                    // DPAPI-sealed copy for the Hello path

            _sessionKey = key;
            // Setting up the lock FROM a sandbox session: the sandbox stays open and plaintext
            // (the vault now exists for real runs); a key-open here would encrypt it in place.
            if (Database.IsSandbox) _sessionKey = null;
            else Database.Open(key);                               // creates/migrates → encrypted
            LoadThread();
            _cfgPin = _cfgPin2 = "";
            _configuring = false;
        }
        catch (Exception ex) { _lockMsg = L["Lock.SetupSaveFailed", ex.Message]; }
    }

    // --- security settings (change PIN, re-enroll Hello) — only while unlocked ---
    private void ChangePin()
    {
        _secMsg = "";
        if (_sessionKey is null) { _secMsg = L["Security.UnlockFirst"]; return; }
        var pin = _newPin.Trim();
        if (pin.Length < 6) { _secMsg = L["Security.NewPinTooShort"]; return; }
        if (IsPasscodeTooWeak(pin)) { _secMsg = L["Security.NewPinTooWeak"]; return; }
        if (pin != _newPin2.Trim()) { _secMsg = L["Security.PinsDontMatch"]; return; }
        try
        {
            Vault.CreateOrAddPin(pin, _sessionKey);                // re-wrap the SAME key with the new PIN
            _newPin = _newPin2 = "";
            _secMsg = L["Lock.PinUpdated"];
        }
        catch (Exception ex) { _secMsg = L["Security.PinSaveFailed", ex.Message]; }
    }

    private async Task ReenrollHello()
    {
        _secMsg = "";
        if (_sessionKey is null) { _secMsg = L["Security.UnlockFirst"]; return; }
        _helloBusy = true; StateHasChanged();
        var ok = await Hello.VerifyAsync("Enable Windows Hello for SemanticPortrait");
        _helloBusy = false;
        if (ok) { HelloKeys.Seal(_sessionKey); _secMsg = L["Security.HelloEnrolled"]; }
        else _secMsg = L["Lock.HelloDidNotComplete"];
        StateHasChanged();
    }

    private void RemoveHello() { HelloKeys.Clear(); _secMsg = L["Security.HelloRemoved"]; }

    private void OpenSecurity()
    {
        _secMsg = ""; _newPin = _newPin2 = "";
        _maskOn = Microsoft.Maui.Storage.Preferences.Default.Get("masking", true);
        if (!Vault.Exists) { _configuring = true; return; }   // no lock yet → set one up
        _showSecurity = true;
    }

    private void ToggleMasking(ChangeEventArgs e)
    {
        _maskOn = e.Value is bool b ? b : !_maskOn;
        Microsoft.Maui.Storage.Preferences.Default.Set("masking", _maskOn);
    }

    private async Task UnlockWithHello()
    {
        _lockMsg = "";
        _helloBusy = true; StateHasChanged();
        var ok = await Hello.VerifyAsync("Unlock SemanticPortrait");   // real Hello prompt
        _helloBusy = false;
        var key = ok ? HelloKeys.Unseal() : null;
        if (key is null) { _lockMsg = L["Lock.HelloDidNotVerify"]; StateHasChanged(); return; }
        await FinishUnlockAsync(key);
    }

    private void UnlockWithPin() => _ = TryUnlockWithPinAsync(showError: true).Guard("unlock-pin");

    // Auto-accept: as the user types, silently try the PIN each keystroke (>=4 digits, so legacy
    // 4-digit PINs still auto-unlock) — don't flash "Wrong PIN" mid-typing.
    private void OnPinInput(ChangeEventArgs e)
    {
        _pinEntry = e.Value?.ToString() ?? "";
        if (_lockMsg.Length > 0) _lockMsg = "";
        if (_pinEntry.Length >= 4) _ = TryUnlockWithPinAsync(showError: false).Guard("unlock-pin-auto");
    }

    // Brute-force friction: a 30s hold after repeated wrong attempts. (The real strength is the
    // PBKDF2 work factor — this just stops casual keyboard guessing at the lock screen.)
    private const int MaxPinFails = 5;
    private bool _pinBusy;
    private int _pinFails;
    private DateTime _pinHoldUntil = DateTime.MinValue;

    private async Task TryUnlockWithPinAsync(bool showError)
    {
        if (_pinBusy || string.IsNullOrEmpty(_pinEntry)) return;
        if (DateTime.UtcNow < _pinHoldUntil)
        {
            if (showError)
            {
                _lockMsg = L["Lock.TooManyAttemptsWait", (int)(_pinHoldUntil - DateTime.UtcNow).TotalSeconds + 1];
                StateHasChanged();
            }
            return;
        }
        _pinBusy = true;
        try
        {
            var pin = _pinEntry;
            var key = await Task.Run(() => Vault.UnwrapWithPin(pin));   // PBKDF2 off the UI thread
            if (key is null)
            {
                if (++_pinFails >= MaxPinFails)
                {
                    _pinFails = 0;
                    _pinHoldUntil = DateTime.UtcNow.AddSeconds(Config.Ui.PinHoldSeconds);
                    _lockMsg = L["Lock.LockedOut", Config.Ui.PinHoldSeconds];
                }
                else if (showError) { _lockMsg = L["Lock.WrongPin"]; _pinEntry = ""; }
                StateHasChanged();
                return;
            }
            _pinFails = 0; _pinHoldUntil = DateTime.MinValue;
            await FinishUnlockAsync(key);
        }
        finally { _pinBusy = false; }
    }

    // Open the vault, then play the lock's exit animation before tearing it down so the app is
    // revealed with an elegant fade/scale-out rather than a hard cut.
    private async Task FinishUnlockAsync(byte[] key)
    {
        if (_unlocking) return;
        _sessionKey = key;
        // A sandbox session re-opens the SANDBOX (plaintext) — the real key must never touch a
        // sandbox path (encrypt-in-place quarantine). The auth above still proved the real vault.
        if (_relockToSandbox) { _relockToSandbox = false; _sessionKey = null; Database.OpenDevSandbox(); }
        else Database.Open(key);
        LoadThread();
        _pinEntry = ""; _lockMsg = "";
        _unlocking = true; StateHasChanged();     // adds .sp-lock-out → exit animation runs
        await Task.Delay(820);
        _locked = false; _unlocking = false;
        StateHasChanged();
    }

    private void OnPinKey(KeyboardEventArgs e) { if (e.Key == "Enter") UnlockWithPin(); }
}
