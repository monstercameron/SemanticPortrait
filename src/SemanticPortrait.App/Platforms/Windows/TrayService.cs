using System.Runtime.InteropServices;

namespace SemanticPortrait.App.Services;

/// <summary>
/// Tray presence via raw Shell_NotifyIcon (spike outcome: no package dependency — plain Win32
/// works fine on arm64 and the same subclassed WndProc also serves the global quick-capture
/// hotkey and the single-instance activation broadcast).
///  - left-click / "Open" menu / Ctrl+Alt+J / second-instance launch → surface the window
///  - right-click → Open / Quit menu
///  - closing the window hides to tray instead (see MauiProgram); Quit really exits
/// </summary>
public sealed class TrayService : IDisposable
{
    // --- Win32 ---------------------------------------------------------------
    private const uint WM_TRAYICON = 0x8000 + 27;   // WM_APP + 27
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4;
    private const uint NIM_ADD = 0x0, NIM_DELETE = 0x2;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    private const int HOTKEY_ID = 0xC0DE;
    private const uint VK_J = 0x4A;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIconW(uint msg, ref NOTIFYICONDATA data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, IntPtr id, IntPtr refData);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc proc, IntPtr id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(IntPtr hMenu, uint flags, UIntPtr id, string text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr hMenu, uint flags, int x, int y, IntPtr hWnd, IntPtr tpm);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconExW(string file, int index, out IntPtr large, out IntPtr small, uint count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowW(string? cls, string? title);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);

    private static readonly uint ActivateMsg = RegisterWindowMessageW("SemanticPortrait.Activate");
    private const IntPtr HWND_BROADCAST = 0xffff;

    /// <summary>Set by Quit so the close-interception in MauiProgram lets the app really exit.</summary>
    public static bool ReallyQuit;

    /// <summary>
    /// Hide the window to the tray (same behavior as the titlebar ✕) — wired in MauiProgram
    /// where the AppWindow is in scope, so the in-app menu can offer an explicit "Hide".
    /// </summary>
    public static Action? HideToTray;

    /// <summary>Second instance: wake whoever holds the tray icon, then exit.</summary>
    public static void BroadcastActivate() => PostMessageW(HWND_BROADCAST, ActivateMsg, 0, 0);

    /// <summary>
    /// Second instance: surface the FIRST instance's window ourselves. Crucial detail: Windows
    /// only lets the FOREGROUND process assign foreground — that's us (the user just launched
    /// us), not the hidden instance reacting to a broadcast. Without this, a second launch
    /// looks exactly like a crash: instant exit, nothing visibly happens.
    /// </summary>
    public static void SurfaceExisting()
    {
        const int SW_SHOW = 5, SW_RESTORE = 9;
        var hwnd = FindWindowW(null, "SemanticPortrait");
        if (hwnd != IntPtr.Zero)
        {
            ShowWindow(hwnd, SW_SHOW);
            ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
        }
        BroadcastActivate();   // managed side re-Shows its AppWindow too (covers the hidden case)
    }

    // --- instance --------------------------------------------------------------
    private IntPtr _hwnd;
    private IntPtr _icon;
    private SubclassProc? _proc;    // held so the delegate isn't GC'd under the native callback
    private Action? _onOpen;
    private Action? _onQuit;
    private bool _attached;

    public void Attach(IntPtr hwnd, Action onOpen, Action onQuit)
    {
        _hwnd = hwnd; _onOpen = onOpen; _onQuit = onQuit;
        _proc = WndProc;
        if (!SetWindowSubclass(_hwnd, _proc, 1, IntPtr.Zero)) return;

        ExtractIconExW(Environment.ProcessPath ?? "", 0, out var large, out var small, 1);
        _icon = small != IntPtr.Zero ? small : large;
        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _icon,
            szTip = "SemanticPortrait",
            szInfo = "", szInfoTitle = "",
        };
        _attached = Shell_NotifyIconW(NIM_ADD, ref data);

        // Ctrl+Alt+J — global quick-capture: surfaces the app focused on the composer.
        RegisterHotKey(_hwnd, HOTKEY_ID, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_J);
    }

    private bool _hideHintShown;

    /// <summary>Balloon on the FIRST hide-to-tray per run, so "the window vanished" is never a
    /// mystery (the close-to-tray behavior looked like a crash before this).</summary>
    public void ShowFirstHideHint()
    {
        if (_hideHintShown || !_attached) return;
        _hideHintShown = true;
        const uint NIM_MODIFY = 0x1, NIF_INFO = 0x10;
        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd, uID = 1, uFlags = NIF_INFO,
            szTip = "SemanticPortrait",
            szInfoTitle = "Still running",
            szInfo = "SemanticPortrait is in the tray — click the icon to reopen, or Quit from its menu.",
        };
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr refData)
    {
        if (msg == WM_TRAYICON)
        {
            var evt = (uint)(lParam.ToInt64() & 0xFFFF);
            if (evt == WM_LBUTTONUP) _onOpen?.Invoke();
            else if (evt == WM_RBUTTONUP) ShowMenu();
            return 0;
        }
        if (msg == WM_HOTKEY && wParam.ToInt64() == HOTKEY_ID) { _onOpen?.Invoke(); return 0; }
        if (msg == ActivateMsg) { _onOpen?.Invoke(); return 0; }
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        const uint MF_STRING = 0x0;
        const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2;
        var menu = CreatePopupMenu();
        try
        {
            AppendMenuW(menu, MF_STRING, 1, "Open SemanticPortrait");
            AppendMenuW(menu, MF_STRING, 2, "Quit");
            GetCursorPos(out var p);
            SetForegroundWindow(_hwnd);                    // required or the menu won't dismiss
            var cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, p.X, p.Y, _hwnd, IntPtr.Zero);
            if (cmd == 1) _onOpen?.Invoke();
            else if (cmd == 2) _onQuit?.Invoke();
        }
        finally { DestroyMenu(menu); }
    }

    /// <summary>Surface + focus the main window (used by open/hotkey/activation paths).</summary>
    public static void SurfaceWindow(IntPtr hwnd)
    {
        const int SW_RESTORE = 9, SW_SHOW = 5;
        ShowWindow(hwnd, SW_SHOW);
        ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
    }

    public void Dispose()
    {
        if (_attached)
        {
            var data = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd, uID = 1, szTip = "", szInfo = "", szInfoTitle = "",
            };
            Shell_NotifyIconW(NIM_DELETE, ref data);
            _attached = false;
        }
        UnregisterHotKey(_hwnd, HOTKEY_ID);
        if (_proc is not null) RemoveWindowSubclass(_hwnd, _proc, 1);
        if (_icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
    }
}
