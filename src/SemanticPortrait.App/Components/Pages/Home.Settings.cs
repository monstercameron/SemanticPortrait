using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using SemanticPortrait.App.Services;
using SemanticPortrait.Core;

namespace SemanticPortrait.App.Components.Pages;

// Settings + data controls: LLM provider/model/keys, prediction ledger, export, and the
// phrase-guarded erase.
public partial class Home
{
    private const string ResetPhrase = "ERASE EVERYTHING";
    private bool _showReset;
    private string _resetText = "";

    // LLM settings modal
    private bool _showLlm;
    private string _llmProvider = "openai";
    private string _llmModel = "";
    private string _llmKey = "";
    private string _llmMsg = "";
    private string _llmUrl = "";
    private List<string> _llmDetected = new();
    private bool? _llmReachable;
    private bool _llmDetecting;
    private bool _codexBusy;   // waiting on the browser OAuth round-trip

    private bool _showLedger;

    private void OpenLedger() => _showLedger = true;

    // ---- LLM settings -------------------------------------------------------
    private void OpenLlm()
    {
        _llmProvider = Providers.Active.ProviderId;
        if (ModelCatalog.Find(_llmProvider) is null) _llmProvider = "openai";
        LoadLlmProviderState();
        _showLlm = true;
    }
    private void CloseLlm() { _showLlm = false; _llmKey = ""; _llmMsg = ""; }

    private void SelectLlmProvider(string id)
    {
        _llmProvider = id;
        LoadLlmProviderState();
    }
    private void LoadLlmProviderState()
    {
        _llmModel = Llm.SelectedModelId(_llmProvider);
        _llmUrl = Llm.GetBaseUrl(_llmProvider) ?? "http://localhost:1234/v1";
        _llmKey = ""; _llmMsg = ""; _llmReachable = null; _llmDetected = new();
    }
    private void SaveLlmUrl()
    {
        Llm.SetBaseUrl(_llmProvider, _llmUrl);
        _llmMsg = L["Llm.ServerUrlSaved"]; _llmReachable = null;
    }
    private void SaveLlmModel()
    {
        if (string.IsNullOrWhiteSpace(_llmModel)) return;
        Llm.SetModel(_llmProvider, _llmModel.Trim());
        RefreshProviderChip();
        _llmMsg = L["Llm.ModelSet"];
    }
    private async Task DetectLmModels()
    {
        Llm.SetBaseUrl(_llmProvider, _llmUrl);       // use what's typed
        _llmDetecting = true; _llmMsg = ""; StateHasChanged();
        try
        {
            _llmReachable = await LmStudio.PingAsync();
            _llmDetected = (await LmStudio.ListModelsAsync()).ToList();
            if (_llmReachable == true && _llmDetected.Count == 0)
                _llmMsg = L["Llm.ServerUpNoModel"];
            else if (_llmReachable == true && string.IsNullOrWhiteSpace(_llmModel))
                _llmModel = _llmDetected[0];
        }
        finally { _llmDetecting = false; }
    }
    private void SelectLlmModel(string id)
    {
        _llmModel = id;
        Llm.SetModel(_llmProvider, id);
        RefreshProviderChip();
        _llmMsg = L["Llm.ModelSet"];
    }
    // Activate a provider via the shared ProviderRegistry seam (so it composes with parallel work).
    private void UseLlmProvider(string id)
    {
        Providers.Select(id);
        RefreshProviderChip();
        _llmMsg = L["Llm.ProviderSelected"];
    }
    private void SaveLlmKey()
    {
        if (string.IsNullOrWhiteSpace(_llmKey)) return;
        Llm.SetKey(_llmProvider, _llmKey);
        _llmKey = ""; _llmMsg = L["Llm.ApiKeySaved"];
        RefreshProviderChip();
    }
    private void ClearLlmKey()
    {
        Llm.SetKey(_llmProvider, null);
        _llmMsg = L["Llm.KeyRemoved"];
        RefreshProviderChip();
    }

    // ---- Codex (ChatGPT-subscription) OAuth --------------------------------
    // Opens the system browser for OpenAI's login; the loopback listener in CodexAuth catches the
    // redirect and stores the (encrypted) tokens. English strings inline — opt-in experiment.
    private async Task SignInCodex()
    {
        if (_codexBusy) return;
        _codexBusy = true; _llmMsg = "Opening your browser — approve the ChatGPT sign-in…"; StateHasChanged();
        try
        {
            var plan = await Codex.LoginAsync(url =>
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }));
            _llmMsg = $"✓ Signed in — {plan} plan. Select this provider to use it.";
            RefreshProviderChip();
        }
        catch (Exception ex) { _llmMsg = "Sign-in failed: " + ex.Message; }
        finally { _codexBusy = false; StateHasChanged(); }
    }

    private void SignOutCodex()
    {
        Codex.SignOut();
        _llmMsg = "Signed out of ChatGPT.";
        RefreshProviderChip();
    }
    private void RefreshProviderChip() => _provider = Providers.Active.DisplayName;

    private static string Price(double? perM) =>
        perM is { } v && v > 0 ? "$" + v.ToString("0.##") : "—";

    private LlmModel CurrentLlmModel()
    {
        var prov = ModelCatalog.Find(_llmProvider)!;
        return prov.Models.FirstOrDefault(m => m.Id == _llmModel) ?? prov.Models[0];
    }

    // The chat provider is whatever the user selected in the registry (NOT a fixed DI singleton —
    // multiple IChatProviders are registered, so resolve the active one each use).
    private IChatProvider Ai => Providers.Active;

    private void CloseReset() { _showReset = false; _resetText = ""; }

    // --- export dialog (all formats; optional range; optional forced-mask shareable variant) ---
    private bool _showExport;
    private DateTime? _expFrom, _expTo;
    private string _expMsg = "";
    private bool _expMasked;

    private void OpenExport() { _expMsg = ""; _showExport = true; }

    private void RunExport()
    {
        if (_locked || _configuring || !Database.IsOpen) return;   // defense-in-depth: unlocked only
        try
        {
            DateTime? from = _expFrom is { } f ? DateTime.SpecifyKind(f.Date, DateTimeKind.Utc) : null;
            DateTime? to = _expTo is { } t ? DateTime.SpecifyKind(t.Date, DateTimeKind.Utc) : null;
            // The shareable variant forces pseudonymization regardless of the egress setting.
            Func<string, string>? mask = _expMasked
                ? new RegexMasker(Database, () => true).Mask : null;

            var folder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var path = Export.WriteToFolder(folder, from, to, mask);
            // The DB is encrypted; the export is not. Say so — and flag a OneDrive-redirected
            // Desktop, where "a file on my desk" is actually "uploaded to Microsoft".
            var warn = _expMasked
                ? L["Export.WarnMasked"].Value
                : L["Export.WarnPlain"].Value;
            if (folder.Contains("OneDrive", StringComparison.OrdinalIgnoreCase))
                warn += L["Export.WarnOneDrive"].Value;
            _messages.Add(new() { Role = "sys", Text = "⬇ " + L["Export.Exported", path, warn].Value });
            _showExport = false;
        }
        catch (Exception ex)
        {
            _expMsg = L["Export.Failed", ex.Message];
        }
        StateHasChanged();
    }

    /// <summary>Export a printable PDF "book" of the user's entries (same date range as the data
    /// export). Pure-managed PDFsharp — no native deps, so it runs on this arm64 box.</summary>
    private void RunPdfExport()
    {
        if (_locked || _configuring || !Database.IsOpen) return;
        try
        {
            DateTime? from = _expFrom is { } f ? f.Date : null;
            DateTime? to = _expTo is { } t ? t.Date : null;
            var pdf = new SemanticPortrait.Core.PdfExport()
                .BuildJournalPdf(Database.GetMessages(), Profile.Get("name"), from, to);

            var folder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var path = Path.Combine(folder, $"SemanticPortrait-journal-{DateTime.Now:yyyyMMdd-HHmm}.pdf");
            File.WriteAllBytes(path, pdf);
            var warn = L["Export.PdfWarnPlain"].Value;
            if (folder.Contains("OneDrive", StringComparison.OrdinalIgnoreCase))
                warn += L["Export.WarnOneDrive"].Value;
            _messages.Add(new() { Role = "sys", Text = "📖 " + L["Export.PdfExported", path, warn].Value });
            _showExport = false;
        }
        catch (Exception ex) { _expMsg = L["Export.PdfFailed", ex.Message]; }
        StateHasChanged();
    }

    // --- encrypted backup + restore --------------------------------------------
    private bool _showBackup;
    private string _backupMsg = "";
    private string _restoreConfirm = "";

    private void RunBackup()
    {
        if (_locked || _configuring || !Database.IsOpen) return;
        try
        {
            var folder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var path = Path.Combine(folder, $"semanticportrait-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.spdb");
            Database.BackupTo(path);
            _backupMsg = _sessionKey is not null
                ? "✓ " + L["Backup.Success", path].Value
                : "✓ " + L["Backup.SuccessPlaintext", path].Value;
        }
        catch (Exception ex) { _backupMsg = L["Backup.Failed", ex.Message]; }
    }

    private async Task PickAndRestoreBackup()
    {
        if (_locked || _configuring) return;
        if (_restoreConfirm != "RESTORE")
        {
            _backupMsg = L["Backup.ConfirmRequired"];
            return;
        }
        try
        {
            var picked = await Microsoft.Maui.Storage.FilePicker.Default.PickAsync(
                new Microsoft.Maui.Storage.PickOptions { PickerTitle = L["Backup.PickerTitle"] });
            if (picked is null) return;
            Database.RestoreFrom(picked.FullPath, _sessionKey);   // rolls back automatically on failure
            _restoreConfirm = ""; _showBackup = false;
            LoadThread();
            _messages.Add(new() { Role = "sys", Text = "🔐 " + L["Backup.Restored", picked.FileName].Value });
        }
        catch (Exception ex)
        {
            _backupMsg = L["Backup.RestoreFailed", ex.Message];
        }
        StateHasChanged();
    }

    // --- local embeddings (MiniLM download + re-embed) ---------------------------
    private bool _showLocalEmb;
    private bool _embBusy;
    private string _embMsg = "";

    private const string MiniLmModelUrl =
        "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx";
    private const string MiniLmVocabUrl =
        "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt";

    private async Task DownloadLocalModelAsync()
    {
        if (_embBusy || LocalEmb.IsAvailable) return;
        _embBusy = true; _embMsg = L["LocalEmb.Downloading"]; StateHasChanged();
        try
        {
            Directory.CreateDirectory(LocalEmb.ModelDir);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(Config.Timeouts.ExportHttpMinutes) };
            // temp + atomic move so a half-download never counts as installed
            var tmpModel = LocalEmb.ModelPath + ".part";
            await File.WriteAllBytesAsync(tmpModel, await http.GetByteArrayAsync(MiniLmModelUrl));
            await File.WriteAllBytesAsync(LocalEmb.VocabPath, await http.GetByteArrayAsync(MiniLmVocabUrl));
            File.Move(tmpModel, LocalEmb.ModelPath, true);

            _embMsg = L["LocalEmb.Reembedding"]; StateHasChanged();
            var (done, failed) = await ReembedAllAsync();
            var failedSuffix = failed > 0 ? L["LocalEmb.FailedSuffix", failed].Value : "";
            _embMsg = L["LocalEmb.Active", done, failedSuffix];
        }
        catch (Exception ex) { _embMsg = L["LocalEmb.DownloadFailed", ex.Message]; }
        finally { _embBusy = false; StateHasChanged(); }
    }

    /// <summary>Re-embed every message + note with the CURRENT embedder (the vector dimension
    /// changes 1536→384 when going local, so everything must be redone to stay searchable).</summary>
    private async Task<(int Done, int Failed)> ReembedAllAsync()
    {
        int done = 0, failed = 0;
        foreach (var m in Database.GetMessages().Where(m => m.Role is "user" or "assistant"))
        {
            var v = await Embedder.EmbedAsync(m.Text);
            if (v is null) { failed++; continue; }
            Database.UpsertEmbedding("message", m.Id, v); done++;
        }
        foreach (var n in Database.GetNotes())
        {
            var v = await Embedder.EmbedAsync(n.Text);
            if (v is null) { failed++; continue; }
            Database.UpsertEmbedding("note", n.Id, v); done++;
        }
        return (done, failed);
    }

    /// <summary>One line of truth for the menu: what actually leaves this machine.</summary>
    private string EgressSummary()
    {
        var provider = Providers.Active;
        var maskOn = Microsoft.Maui.Storage.Preferences.Default.Get("masking", true);
        var chat = provider.ProviderId == "lmstudio"
            ? "chat: local (nothing leaves)"
            : $"chat: {provider.DisplayName}{(maskOn ? " (masked)" : " (unmasked)")}";
        var emb = Embedder is ILocalityProbe p && p.LocalActive
            ? "recall: local"
            : $"recall: OpenAI embeddings{(maskOn ? " (masked)" : " (unmasked)")}";
        return $"☁ {chat} · {emb}";
    }

    // --- tray / startup ----------------------------------------------------------
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunKeyName = "SemanticPortrait";
    private bool _startWithWindows = ReadStartupFlag();

    private static bool ReadStartupFlag()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunKeyName) is string;
        }
        catch { return false; }
    }

    private void ToggleStartup()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (_startWithWindows) key.DeleteValue(RunKeyName, throwOnMissingValue: false);
            else key.SetValue(RunKeyName, $"\"{Environment.ProcessPath}\"");
            _startWithWindows = !_startWithWindows;
        }
        catch (Exception ex)
        {
            _messages.Add(new() { Role = "sys", Text = $"startup toggle failed: {ex.Message}" });
        }
    }

    /// <summary>Hard exit — a beat on the farewell screen, then the process really ends.</summary>
    private bool _quitting;
    private async Task QuitApp()
    {
        if (_quitting) return;
        _quitting = true; _showMenu = false;
        StateHasChanged();
        await Task.Delay(1500);
        SemanticPortrait.App.Services.TrayService.ReallyQuit = true;
        Microsoft.Maui.Controls.Application.Current?.Quit();
    }

    /// <summary>Hide to the tray (timers/reminders keep running) — the titlebar ✕ path, but explicit.</summary>
    private void HideApp()
    {
        _showMenu = false;
        SemanticPortrait.App.Services.TrayService.HideToTray?.Invoke();
    }

    // --- timeline ---------------------------------------------------------------
    private bool _showTimeline;

    private void WipeEverything()
    {
        if (_resetText != ResetPhrase) return;   // phrase guard
        try
        {
            Profile.Clear();
            Database.DestroyFile();                 // certain wipe — delete the DB file entirely
            if (_sessionKey is not null) Database.Open(_sessionKey);   // recreate empty (encrypted)
#if DEBUG
            else Database.OpenPlaintext();                             // dev sandbox has no key
#else
            else { _configuring = true; return; }                      // never recreate unencrypted
#endif
            _showReset = false;
            _resetText = "";
            LoadThread();          // reloads empty → triggers the onboarding greeting
        }
        catch (Exception ex)
        {
            _showReset = false; _resetText = "";
            _messages.Add(new() { Role = "sys", Text = $"erase failed: {ex.Message}" });
        }
        StateHasChanged();
    }
}
