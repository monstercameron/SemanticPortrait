using Markdig;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using SemanticPortrait.App.Services;
using SemanticPortrait.Core;

namespace SemanticPortrait.App.Components.Pages;

// Home shell: shared state, lifecycle, thread loading, theme, and small cross-cutting helpers.
// Feature logic lives in the sibling Home.*.cs partials (Chat / Lock / Import / Notifications /
// Settings / Constellation); markup stays in Home.razor.
public partial class Home
{
    private bool _dark = true;
    private bool _showConstellation = false;   // hidden on startup; toggle with the ✦ button
    private string _provider = "GPT-5.5 · low";
    // persisted setting: when on, hide all $ spend in the UI
    private bool _hideCosts = Microsoft.Maui.Storage.Preferences.Default.Get("hide_costs", false);
    // when true, honor the OS "reduce motion" setting; default off so animations always play
    private bool _respectMotion = Microsoft.Maui.Storage.Preferences.Default.Get("respect_motion", false);
    // Discreet toasts: force EVERY OS toast to the generic placeholder (skip smart classification).
    private bool _discreet = Microsoft.Maui.Storage.Preferences.Default.Get("discreet_toasts", false);
    private void ToggleDiscreet()
    {
        _discreet = !_discreet;
        Microsoft.Maui.Storage.Preferences.Default.Set("discreet_toasts", _discreet);
        NotificationService.Discreet = _discreet;
    }

    // Update check: metadata-only ping at launch (disclosed in privacy_status); menu-only surfacing.
    private string? _updateTag;
    private void StartUpdateCheck() => _ = Task.Run(async () =>
    {
        var tag = await UpdateCheck.NewerReleaseAsync(Http, Microsoft.Maui.ApplicationModel.AppInfo.Current.VersionString);
        if (tag is not null) await InvokeAsync(() => { _updateTag = tag; StateHasChanged(); });
    }).Guard("update-check");
    private void OpenReleases() =>
        _ = Microsoft.Maui.ApplicationModel.Launcher.Default.OpenAsync(UpdateCheck.ReleasesUrl);

    // Evening check-in: 0 = off; otherwise the local hour the daily reflection nudge may fire.
    // Read LIVE from Preferences — the agent can change it too (set_evening_checkin), and the
    // menu label must reflect whoever wrote last.
    private static int CheckinHour => Microsoft.Maui.Storage.Preferences.Default.Get("checkin_hour", 0);
    private void CycleCheckin() =>
        Microsoft.Maui.Storage.Preferences.Default.Set("checkin_hour",
            CheckinHour switch { 0 => 19, 19 => 20, 20 => 21, 21 => 22, _ => 0 });

    private string _draft = "";
    private bool _busy;

    // Cached topbar name: Profile.Get("name") is a locked SQLite point-query — avoid running it
    // on every render (30fps during streaming, once per keystroke). Refreshed in LoadThread and
    // again after Send() completes (set_profile_field, the only writer, runs during that turn).
    private string? _who;

    private bool _showDev;
    private bool _showMenu;

    private ElementReference _input;
    private ElementReference _messagesEl;
    private bool _focusNext = true;
    private bool _scrollDown = true;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // First paint is done: give it a beat to settle, then lift the boot splash off the app
        // (the veil lives OUTSIDE #app precisely so this can be a fade, not a hard cut).
        if (firstRender)
            _ = InvokeAsync(async () =>
            {
                await Task.Delay(350);
                try { await JS.InvokeVoidAsync("spDismissBoot"); } catch { }
            }).Guard("boot-veil");
        // Returning from the constellation recreates the chat DOM at scrollTop 0 — put the
        // user back at their last position (or the newest message if they were at the bottom).
        if (_restoreChatScroll && !_showConstellation)
        {
            _restoreChatScroll = false;
            if (!_scrollDown)
                try { await JS.InvokeVoidAsync("spRestoreScroll", _messagesEl); } catch { }
        }
        if (_scrollDown)
        {
            _scrollDown = false;
            try { await JS.InvokeVoidAsync("spScrollToBottom", _messagesEl); } catch { }
        }
        if (!_showConstellation && !_locked && !_configuring)
        {
            try { await JS.InvokeVoidAsync("spTrackScroll", _messagesEl); } catch { }
            // the composer grows with its content (typing AND dictation), capped by CSS max-height
            try { await JS.InvokeVoidAsync("spWatchComposer", _input); } catch { }
        }
        if (_focusNext && !_busy)
        {
            _focusNext = false;
            try { await _input.FocusAsync(); } catch { /* element not ready */ }
        }

        // run the animated constellation only while the lock screen is visible
        var showNet = _locked && !_respectMotion;
        if (showNet && !_netRunning)
        {
            _netRunning = true;
            try { await JS.InvokeVoidAsync("spConstellation.start"); } catch { }
        }
        else if (!showNet && _netRunning)
        {
            _netRunning = false;
            try { await JS.InvokeVoidAsync("spConstellation.stop"); } catch { }
        }
    }

    private void ScrollDown() => _scrollDown = true;

    // Journal + model text is rendered into a MarkupString shown in the WebView, so it is UNTRUSTED:
    // DisableHtml() strips raw <script>/<img onerror>/<iframe> (a stored-XSS path — imported
    // third-party chat logs and prompt-injected model output both reach here), and the link pass in
    // Md() neutralizes javascript:/data: URIs that DisableHtml doesn't cover. Do NOT drop either
    // without a security review: without them, any HTML in an entry executes in the host WebView.
    private static readonly Markdig.MarkdownPipeline _md =
        new Markdig.MarkdownPipelineBuilder().DisableHtml().Build();

    private static MarkupString Md(string text)
    {
        var doc = Markdig.Markdown.Parse(text ?? "", _md);
        foreach (var link in Markdig.Syntax.MarkdownObjectExtensions.Descendants<Markdig.Syntax.Inlines.LinkInline>(doc))
            if (!IsSafeLinkUrl(link.Url)) link.Url = "#";
        using var sw = new System.IO.StringWriter();
        var renderer = new Markdig.Renderers.HtmlRenderer(sw);
        _md.Setup(renderer);
        renderer.Render(doc);
        sw.Flush();
        return (MarkupString)sw.ToString();
    }

    // Allow only benign link schemes; a relative/anchor URL (no scheme before the first '/') is fine.
    private static bool IsSafeLinkUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return true;
        var u = url.TrimStart();
        int colon = u.IndexOf(':');
        if (colon < 0) return true;                        // no scheme → relative / anchor
        int slash = u.IndexOf('/');
        if (slash >= 0 && slash < colon) return true;      // ':' sits inside a path segment, not a scheme
        var scheme = u[..colon].ToLowerInvariant();
        return scheme is "http" or "https" or "mailto" or "tel";
    }

    private sealed class Msg
    {
        public string Role { get; init; } = "ai";
        public string Text { get; set; } = "";
        public string Time { get; init; } = "now";
        /// <summary>Stable identity for Blazor's @key so the diff survives the tool-bubble
        /// remove/re-add dance instead of churning message elements by index.</summary>
        public Guid Key { get; } = Guid.NewGuid();
        // Rendered-markdown cache: re-parsing every message's Markdown on every render (once per
        // streamed token, across the whole thread) was the dominant streaming cost. Parse only
        // when Text actually changes; prior bubbles return their cached HTML untouched.
        private string? _htmlFrom;
        private MarkupString _html;
        public MarkupString Html
        {
            get
            {
                if (!ReferenceEquals(_htmlFrom, Text)) { _html = Md(Text); _htmlFrom = Text; }
                return _html;
            }
        }
        public string? Detail { get; set; }
        public bool Expanded { get; set; }
        public bool Sourced { get; set; }
        /// <summary>True while this AI reply is still streaming: empty text renders the typing
        /// dots, non-empty gets the live caret. Cleared in the stream's finally.</summary>
        public bool Streaming { get; set; }
        /// <summary>Folder open/closed for a run of tool calls (kept on the run's FIRST message;
        /// separate from Expanded, which marks the one selected chip inside the folder).</summary>
        public bool FolderOpen { get; set; }
        /// <summary>DB row id (0 for not-yet-persisted) — the link to attachments.</summary>
        public long DbId { get; set; }
        /// <summary>Attached photo thumbnails (data URIs), loaded with the thread.</summary>
        public List<SemanticPortrait.Core.AttachmentThumb>? Photos { get; set; }
    }

    /// <summary>Solitary chip selection inside a tool folder: selecting a step deselects the
    /// others, so exactly one detail pane shows (clicking the open one closes it).</summary>
    private static void SelectChip(List<Msg> group, Msg chip)
    {
        var open = !chip.Expanded;
        foreach (var g in group) g.Expanded = false;
        chip.Expanded = open;
    }

    private readonly List<Msg> _messages = new();

    /// <summary>Load the thread + graph from the (now-open) DB. Called only after unlock/open.</summary>
    private void LoadThread()
    {
        _messages.Clear();
        _clearedBefore = 0;   // a fresh thread load always shows everything
        _who = Profile.Get("name");
        var withPhotos = Database.MessageIdsWithAttachments();
        foreach (var m in Database.GetMessages())
        {
            var role = m.Role switch { "user" => "user", "tool" => "tool", _ => "ai" };
            _messages.Add(new()
            {
                Role = role, Text = m.Text, Time = Friendly(m.CreatedUtc), Detail = m.Detail, DbId = m.Id,
                Photos = withPhotos.Contains(m.Id) ? Database.ThumbsFor(m.Id) : null,
            });
        }
        LoadGraph();
        Usage.LoadBaseline();       // snapshot persisted spend so the chip shows the all-time total
        ReconcileNotifs();          // back-fill drawer for reminders that fired while locked
        RefreshTodos();             // populate the to-do badge/list for this session
        _scrollDown = true;
        StartIdle();
        StartReminders();
        // Process a toast click that arrived while we were locked (now that the DB is open).
        if (_pendingToastArg is { } pending) { _pendingToastArg = null; _ = HandleToastActivation(pending).Guard("toast-activation"); }
        if (_messages.Count == 0) _ = GreetOnboarding().Guard("greet-onboarding");   // fresh / post-wipe → don't show a blank page
        else MaybeSessionDigest();   // first open of the day → one grounded line about today's items
        KickEmbeddingBackfill();
    }

    private bool _backfillKicked;
    /// <summary>One-shot per session: embed any nodes/events written before embed-on-write existed
    /// (or whose embed failed at write time), so semantic entity/timeline lookup covers everything.
    /// Runs in the background — recall works meanwhile, just with fewer semantic rows.</summary>
    private void KickEmbeddingBackfill()
    {
        if (_backfillKicked || !Database.IsOpen) return;
        _backfillKicked = true;
        _ = Task.Run(() => Backfill.RunAsync()).Guard("embedding-backfill");
    }

    private static string Friendly(string utc) =>
        DateTime.TryParse(utc, out var dt) ? dt.ToLocalTime().ToString("MMM d, h:mm tt") : "now";

    private static string Elapsed(string utc)
    {
        var dt = Compactor.ParseUtc(utc);
        var ts = DateTime.UtcNow - dt;
        if (ts < TimeSpan.FromMinutes(1)) return "moments";
        if (ts < TimeSpan.FromHours(1)) return $"{(int)ts.TotalMinutes} minute(s)";
        if (ts < TimeSpan.FromDays(1)) return $"{(int)ts.TotalHours} hour(s)";
        return $"{(int)ts.TotalDays} day(s)";
    }

    protected override async Task OnInitializedAsync()
    {
        _provider = Ai.DisplayName;
        NotificationService.Discreet = _discreet;   // sync the Core-side gate with the saved setting
        InitVoice();                                // optional sidecar voice — buttons render only if present
        StartUpdateCheck();                         // metadata-only; silent when offline
        ToastActivation.Activated += OnToastActivated;
        if (ToastActivation.PendingArg is { } pendingArg) _pendingToastArg = pendingArg;   // cold-start click

        _helloAvailable = await Hello.IsAvailableAsync();
#if DEBUG
        DevTrap.Trapped += OnDevTrap;   // dev builds surface every swallowed error in-chat
#endif
#if DEBUG
        // DEV modes (.env flags, DEBUG builds only — none of this compiles into Release):
        //   (default)          → PERSISTENT sandbox: isolated plaintext DB, data survives runs.
        //   dev_mode=fresh     → FRESH sandbox: wiped on every launch (virgin onboarding each run).
        //   dev_unlock=true    → the REAL encrypted DB, lock screen bypassed (key from the
        //                        DPAPI Hello seal, or dev_pin=... as fallback); idle re-lock off.
        //   dev_security=true  → the real lock flow end-to-end, exactly like Release.
        if (IsTrue(EnvLoader.Get("dev_unlock")))
        {
            if (TryDevUnlock()) return;
            // no key material available → fall through to the normal lock/setup flow
        }
        else if (IsTrue(EnvLoader.Get("dev_security")))
        {
            SetDevMode("DEV · security flow (real lock)");
        }
        else if (!IsTrue(EnvLoader.Get("dev_security")))
        {
            var fresh = string.Equals(EnvLoader.Get("dev_mode"), "fresh", StringComparison.OrdinalIgnoreCase);
            Database.OpenDevSandbox(fresh);
            SetDevMode(fresh ? "DEV · fresh scratch" : "DEV · persistent sandbox");
            LoadThread();
            _locked = false;
            // Screenshot automation: land directly on the constellation (design-review loops).
            if (Environment.GetEnvironmentVariable("SP_OPEN_CONSTELLATION") == "1")
                _showConstellation = true;
            _messages.Insert(0, new()
            {
                Role = "sys",
                Text = fresh
                    ? "🧪 dev sandbox (FRESH scratch) — wiped every launch; your persistent sandbox is untouched — unset dev_mode in .env to return to it"
                    : "🧪 dev sandbox (persistent) — data survives runs; dev_mode=fresh in .env for a per-launch scratch sandbox, or ⋯ → Reset sandbox",
            });
            return;
        }
#endif
        if (Vault.Exists) { _locked = true; return; }              // encrypted + locked: DB stays closed
        _configuring = true;                                       // no lock yet → it's mandatory, set one up
        // (Users of older builds who chose "no lock" land here too; their plaintext DB is
        //  migrated to encrypted on the first Open(key) after they finish setup.)
    }

#if DEBUG
    private bool _devUnlocked;

    /// <summary>
    /// dev_unlock: open the REAL encrypted database without the lock ceremony. Key sources, in
    /// order: the DPAPI-sealed Hello key (unseals for this Windows user without a prompt — the
    /// same-user path the threat model documents), then a `dev_pin=...` from .env. Returns false
    /// when neither yields the key, so the caller falls back to the normal lock screen.
    /// DEBUG-only by compilation — Release builds cannot contain this path.
    /// </summary>
    private bool TryDevUnlock()
    {
        try
        {
            var key = HelloKeys.Unseal();
            if (key is null && EnvLoader.Get("dev_pin") is { Length: > 0 } pin)
                key = Vault.UnwrapWithPin(pin);
            if (key is null) return false;

            _sessionKey = key;
            Database.Open(key);
            _devUnlocked = true;               // also suppresses the 15-min idle re-lock
            SetDevMode("DEV · REAL DB (unlocked)");
            LoadThread();
            _locked = false;
            _messages.Add(new()
            {
                Role = "sys",
                Text = "🧪 dev_unlock — lock screen bypassed on the REAL database (DEBUG build); idle auto-lock disabled",
            });
            return true;
        }
        catch { return false; }
    }
#endif

#if DEBUG
    private static bool IsTrue(string? v) =>
        v is not null && (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1");
#endif

    private async Task Note(string text) =>
        await InvokeAsync(() => { _messages.Add(new() { Role = "sys", Text = text }); _scrollDown = true; StateHasChanged(); });

    private void ToggleTheme() => _dark = !_dark;
    private void ToggleHideCosts()
    {
        _hideCosts = !_hideCosts;
        Microsoft.Maui.Storage.Preferences.Default.Set("hide_costs", _hideCosts);   // persist the setting
    }
    private void ToggleMotion()
    {
        _respectMotion = !_respectMotion;
        Microsoft.Maui.Storage.Preferences.Default.Set("respect_motion", _respectMotion);
    }

    public void Dispose()
    {
        ToastActivation.Activated -= OnToastActivated;
#if DEBUG
        DevTrap.Trapped -= OnDevTrap;
#endif
        _reminders?.Dispose();
        _idle?.Dispose();
        if (_voiceAudio is not null) _voiceAudio.LevelChanged -= OnMicLevel;
        _voice?.Dispose();
        _voiceAudio?.Dispose();
    }

    /// <summary>Dev-only on-screen error surface. Two outlets per DevTrap report: a ⚠️ sys
    /// bubble in the chat (persistent, expandable) AND a floating toast rendered above EVERY
    /// view — errors that only landed in the chat were invisible while the user was in the
    /// constellation, which read as "the app silently crashed". The types live outside #if so
    /// the markup compiles in Release, where the list simply never fills (no subscription).</summary>
    public sealed record DevToast(string Source, string Message, string Detail, DateTime AtUtc);
    private readonly List<DevToast> _devToasts = new();
    private void DismissDevToast(DevToast t) => _devToasts.Remove(t);

#if DEBUG
    private void OnDevTrap(string source, Exception ex) =>
        _ = InvokeAsync(async () =>
        {
            _messages.Add(new()
            {
                Role = "sys",
                Text = $"⚠️ dev error [{source}] {ex.GetType().Name}: {ex.Message}",
                Detail = ex.ToString(),
            });
            var toast = new DevToast(source, $"{ex.GetType().Name}: {ex.Message}", ex.ToString(), DateTime.UtcNow);
            _devToasts.Add(toast);
            while (_devToasts.Count > 4) _devToasts.RemoveAt(0);
            _scrollDown = true;
            StateHasChanged();
            await Task.Delay(TimeSpan.FromSeconds(20));
            if (_devToasts.Remove(toast)) StateHasChanged();
        });
#endif

    // Right-click "clear chat": a VIEW-only fresh start — earlier messages are tucked out of
    // the viewport (nothing is deleted; the thread, memory and analysis are untouched) so the
    // user can clear their head. A quiet chip at the top brings them back.
    private (int X, int Y)? _chatCtx;
    private int _clearedBefore;
    private void OnChatContextMenu(Microsoft.AspNetCore.Components.Web.MouseEventArgs e) =>
        _chatCtx = ((int)e.ClientX, (int)e.ClientY);
    private void ClearChatView() { _clearedBefore = _messages.Count; _chatCtx = null; }
    private void UnclearChatView() { _clearedBefore = 0; _chatCtx = null; _scrollDown = true; }

    private bool _restoreChatScroll;
    private async Task ToggleConstellation()
    {
        // Leaving the chat: snapshot its scroll NOW — the chat DOM is about to be torn down.
        if (!_showConstellation)
            try { await JS.InvokeVoidAsync("spCaptureScroll", _messagesEl); } catch { }
        _showConstellation = !_showConstellation;
        if (!_showConstellation) _restoreChatScroll = true;
        // First time the map is ever opened: pin the "how to read" key open so a newcomer sees
        // it without discovering the hover chip. Persisted, so it only ever auto-shows once.
        else if (!Microsoft.Maui.Storage.Preferences.Default.Get("map_legend_seen", false))
        {
            _peekLegend = true;
            Microsoft.Maui.Storage.Preferences.Default.Set("map_legend_seen", true);
        }
    }

    private bool _peekLegend;

    /// <summary>Which data world this session runs in — shown in the WINDOW TITLE and the header
    /// so it's never ambiguous where writes are going. Null in Release (plain title).</summary>
    private string? _devModeLabel;

    private void SetDevMode(string? label)
    {
        _devModeLabel = label;
        var title = label is null ? "SemanticPortrait" : $"SemanticPortrait — {label}";
        Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
        {
            var w = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault();
            if (w is not null) w.Title = title;
        });
    }

    /// <summary>True in DEBUG builds — gates dev-only UI in razor markup (which can't #if).</summary>
    private static bool IsDevBuild =>
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>Dev-only: wipe the sandbox and start a virgin thread in place (no relaunch).
    /// Guarded in Db — throws unless the open DB is actually a .dev sandbox. No-op in Release
    /// (the button is hidden and the body compiled out).</summary>
    private void ResetDevSandbox()
    {
#if DEBUG
        try
        {
            _showMenu = false;
            Database.ResetDevSandbox();
            _messages.Clear();
            _handoffDedup = null;      // session caches belong to the wiped data
            LoadThread();              // empty thread → onboarding greeting fires
            _messages.Insert(0, new() { Role = "sys", Text = "🧪 sandbox reset — fresh thread" });
            StateHasChanged();
        }
        catch (Exception ex) { DevTrap.Report("sandbox-reset", ex); }
#endif
    }
}
