# Installing SemanticPortrait

Grab the latest build from **[Releases](https://github.com/monstercameron/SemanticPortrait/releases)**.

## Which file?

| File | What it is |
|---|---|
| `SemanticPortrait-<ver>-x64-setup.exe` | **Recommended.** Per-user installer — no admin prompt, Start-menu shortcut, uninstall from Settings. |
| `SemanticPortrait-<ver>-x64.msi` | Same install as an MSI, for scripted/enterprise flows. |
| `SemanticPortrait-<ver>-win-x64-portable.zip` | No install: unzip anywhere, run `SemanticPortrait.App.exe`. |

Pick **x64** for most PCs. Pick **arm64** if you're on Windows-on-ARM (Snapdragon laptops —
where the app is developed, and where the future on-device voice features will live).

Requirements: **Windows 10 1809+ / Windows 11**, with the
[WebView2 runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (preinstalled on
Windows 11 and most updated Windows 10 machines).

## The SmartScreen warning (read this once)

These builds are **not code-signed** — signing certificates that satisfy Microsoft SmartScreen
cost real money per year, and this is a young open-source project. So the first launch will
likely show *"Windows protected your PC."*

1. Verify your download first: compare its SHA-256 against the `SHA256SUMS-<arch>.txt` on the
   release (`Get-FileHash .\SemanticPortrait-...-setup.exe` in PowerShell).
2. Click **More info → Run anyway**.

If the project earns it, a signing story (Azure Trusted Signing) is the planned fix — tracked
in the backlog.

## Where your data lives

Everything stays on your machine, in
`%LOCALAPPDATA%\Packages`-adjacent app data (an **encrypted** SQLite vault keyed off your
Windows Hello / PIN). Uninstalling the app **does not delete your data** — reinstall or upgrade
and your journal is still there. To erase everything, use **⋯ → Erase all data** inside the app
before uninstalling.

## Upgrading

Install the new version over the old one (setup EXE and MSI both handle upgrades in place).
Portable-zip users: unzip over the old folder, or anywhere new — the data lives outside the app
folder either way.
