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
        _ = InvokeAsync(() => { if (!_locked && !_configuring && _sessionKey is not null) { LockNow(); StateHasChanged(); } })
            .Guard("idle-lock");   // a fault here means the idle re-lock silently stopped working

    private void LockNow()
    {
#if DEBUG
        _devUnlocked = false;       // a deliberate lock ends the dev bypass for this session
#endif
        // Sandbox sessions never lock: the lock protects the ENCRYPTED real DB, and every unlock
        // path reopens with the real key — run against a plaintext sandbox that key-open would
        // encrypt it in place (observed 2026-07-02: a fresh hour-long import quarantined as
        // *.notadb). Db.Open(key) now also refuses sandbox paths as defense-in-depth.
        if (Database.IsSandbox) return;
        if (Vault.Exists)
        {
            Database.Close();           // data becomes inaccessible until re-auth
            _reminders?.Stop();
            // Leave nothing personal readable in memory-backed UI state while locked:
            _messages.Clear();
            _notifs.Clear();
            _constModel = null;
            Trace.Clear();              // dev traces carry entry text
            _sessionKey = null;
            _locked = true; _pinEntry = ""; _lockMsg = "";
        }
        else { _configuring = true; }   // not set up yet → configure
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
        else _lockMsg = "Windows Hello didn't complete.";
        StateHasChanged();
    }

    private void SaveSetup()
    {
        _lockMsg = "";
        var pin = _cfgPin.Trim();
        if (pin.Length < 6) { _lockMsg = "Set a PIN of at least 6 digits (it's your recovery key — longer is stronger)."; return; }
        if (pin != _cfgPin2.Trim()) { _lockMsg = "PINs don't match."; return; }

        try
        {
            SaveMaskConsent();
            var key = Vault.CreateOrAddPin(pin);                   // random AES key, wrapped by the PIN
                                                                   // (throws if the wrap can't persist —
                                                                   //  never encrypt with an unsaved key)
            if (_cfgHello) HelloKeys.Seal(key);                    // DPAPI-sealed copy for the Hello path

            _sessionKey = key;
            Database.Open(key);                                    // creates/migrates → encrypted
            LoadThread();
            _cfgPin = _cfgPin2 = "";
            _configuring = false;
        }
        catch (Exception ex) { _lockMsg = $"Couldn't save the lock setup: {ex.Message}"; }
    }

    // --- security settings (change PIN, re-enroll Hello) — only while unlocked ---
    private void ChangePin()
    {
        _secMsg = "";
        if (_sessionKey is null) { _secMsg = "Unlock first."; return; }
        var pin = _newPin.Trim();
        if (pin.Length < 6) { _secMsg = "New PIN must be at least 6 digits."; return; }
        if (pin != _newPin2.Trim()) { _secMsg = "PINs don't match."; return; }
        try
        {
            Vault.CreateOrAddPin(pin, _sessionKey);                // re-wrap the SAME key with the new PIN
            _newPin = _newPin2 = "";
            _secMsg = "✓ PIN updated.";
        }
        catch (Exception ex) { _secMsg = $"Couldn't save the new PIN — after a restart the previous PIN still applies. ({ex.Message})"; }
    }

    private async Task ReenrollHello()
    {
        _secMsg = "";
        if (_sessionKey is null) { _secMsg = "Unlock first."; return; }
        _helloBusy = true; StateHasChanged();
        var ok = await Hello.VerifyAsync("Enable Windows Hello for SemanticPortrait");
        _helloBusy = false;
        if (ok) { HelloKeys.Seal(_sessionKey); _secMsg = "✓ Windows Hello enrolled."; }
        else _secMsg = "Windows Hello didn't complete.";
        StateHasChanged();
    }

    private void RemoveHello() { HelloKeys.Clear(); _secMsg = "Windows Hello removed."; }

    private void OpenSecurity()
    {
        _secMsg = ""; _newPin = _newPin2 = "";
        _maskOn = Microsoft.Maui.Storage.Preferences.Default.Get("masking", false);
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
        if (key is null) { _lockMsg = "Windows Hello didn't verify."; StateHasChanged(); return; }
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
                _lockMsg = $"Too many attempts — wait {(int)(_pinHoldUntil - DateTime.UtcNow).TotalSeconds + 1}s.";
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
                    _pinHoldUntil = DateTime.UtcNow.AddSeconds(30);
                    _lockMsg = "Too many wrong attempts — locked for 30 seconds.";
                }
                else if (showError) { _lockMsg = "Wrong PIN."; _pinEntry = ""; }
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
        Database.Open(key); LoadThread();
        _pinEntry = ""; _lockMsg = "";
        _unlocking = true; StateHasChanged();     // adds .sp-lock-out → exit animation runs
        await Task.Delay(820);
        _locked = false; _unlocking = false;
        StateHasChanged();
    }

    private void OnPinKey(KeyboardEventArgs e) { if (e.Key == "Enter") UnlockWithPin(); }
}
