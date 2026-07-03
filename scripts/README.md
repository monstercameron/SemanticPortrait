# scripts/

Build helpers for SemanticPortrait. Two equivalent wrappers around the `dotnet` CLI — use the
PowerShell one on Windows (the primary dev environment) and the bash one in POSIX shells.

| File | Shell | Notes |
|------|-------|-------|
| [`build.ps1`](./build.ps1) | PowerShell | Primary on Windows. |
| [`build.sh`](./build.sh)   | bash       | Same interface for POSIX shells. |

Both pin the target framework (`net10.0-windows10.0.19041.0`) so you don't have to remember it,
and resolve paths relative to the repo root — run them from anywhere.

## What they do

By default: **build the desktop app** (`SemanticPortrait.App`) in `Debug`. Flags add steps on top.

## Options

| Action | `build.ps1` | `build.sh` |
|--------|-------------|------------|
| Configuration | `-Configuration Debug\|Release` | `-c, --configuration <Debug\|Release>` |
| Run tests after build | `-Test` | `-t, --test` |
| Launch app after build | `-Run` | `-r, --run` |
| Clean `bin/` + `obj/` first | `-Clean` | `--clean` |
| Help | `Get-Help ./scripts/build.ps1` | `-h, --help` |

Configuration defaults to `Debug`. Steps run in order: clean → build → test → run.

## Examples

PowerShell:

```powershell
./scripts/build.ps1                         # build (Debug)
./scripts/build.ps1 -Test                   # build + run tests
./scripts/build.ps1 -Configuration Release -Run   # release build, then launch
./scripts/build.ps1 -Clean -Test            # wipe bin/obj, rebuild, test
```

bash:

```bash
scripts/build.sh                            # build (Debug)
scripts/build.sh --test                     # build + run tests
scripts/build.sh -c Release --run           # release build, then launch
scripts/build.sh --clean --test             # wipe bin/obj, rebuild, test
```

## Requirements

- **.NET 10 SDK** (arm64 on this machine — the X2 / Snapdragon laptop).
- **Windows** to actually build/run the app: it targets `net10.0-windows…` (MAUI Blazor Hybrid),
  so `-Run`/`--run` only works on Windows. The `SemanticPortrait.Core` + test projects are plain
  `net10.0` and build/test cross-platform.

## Notes

- A non-zero exit from `dotnet` aborts the script (PowerShell throws; bash uses `set -euo pipefail`).
- No `.sln` is used — the scripts target the project files directly.
- First make `build.sh` executable if needed: `chmod +x scripts/build.sh`.
