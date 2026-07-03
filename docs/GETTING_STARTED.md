# Getting Started

A developer's guide to building, running, and extending SemanticPortrait. For what the product
*is*, see the [root README](../README.md); for the full design, [`plan.md`](../plan.md).

---

## 1. What to install

SemanticPortrait is a **Windows-native .NET MAUI Blazor Hybrid** desktop app on **.NET 10**,
built for **ARM64** (the Snapdragon X-series laptop). To build and run the app you need Windows.

| Requirement | Notes |
|-------------|-------|
| **Windows 11** (ARM64 or x64) | Required to build/run the MAUI app (`net10.0-windows10.0.19041.0`). |
| **.NET 10 SDK** | This repo is built with `10.0.301`. Get it from <https://dotnet.microsoft.com/download/dotnet/10.0>. On ARM hardware, install the **arm64** SDK. |
| **MAUI workload** | `dotnet workload install maui` (one time). |
| **Windows App SDK / WinUI runtime** | Pulled in by the MAUI workload; a recent Windows 11 already has the WebView2 runtime the Blazor host needs. |
| **An OpenAI API key** | The chat + embeddings run on OpenAI's Responses API today. See §3. |

> The `SemanticPortrait.Core` library and the test project are plain `net10.0` and build/test on
> any OS — only the `.App` project is Windows-only.

Verify your toolchain:

```powershell
dotnet --version          # expect 10.0.x
dotnet workload list      # expect 'maui' (or maui-windows) present
```

---

## 2. Dependencies (NuGet)

These restore automatically on first build (`dotnet restore` / `dotnet build`).

**`SemanticPortrait.Core`** (engine — no MAUI):
- `Microsoft.Data.Sqlite.Core` — SQLite data access.
- `SQLitePCLRaw.bundle_e_sqlcipher` — the **SQLCipher** native provider (AES-256 encryption at rest).

**`SemanticPortrait.App`** (MAUI desktop):
- `Microsoft.Maui.Controls`, `Microsoft.AspNetCore.Components.WebView.Maui` — the Blazor Hybrid host.
- `Markdig` — Markdown rendering in chat bubbles.
- `System.Security.Cryptography.ProtectedData` — DPAPI, for the Windows Hello key path.
- `Microsoft.Toolkit.Uwp.Notifications` — Windows toast notifications.

**`SemanticPortrait.Tests`**: `xunit`, `Microsoft.NET.Test.Sdk`, `coverlet.collector`.

No `.sln` is used — projects reference each other directly (`App` → `Core`, `Tests` → `Core`).

---

## 3. Configuration — the `.env` file

Secrets live in a git-ignored **`.env`** at the repo root. [`EnvLoader`](../src/SemanticPortrait.Core/Storage/EnvLoader.cs)
walks up from the app's base directory (up to 8 levels), only trusting a directory that looks
like a repo root (has `.git`/a `.sln`), and parses simple `KEY=VALUE` lines. Note `.env` is a
DEV fallback — user-entered keys live in the encrypted DB (⋯ → LLM settings).

Create `.env` in the repo root:

```ini
# OpenAI API key — used for chat (Responses API) and embeddings
openai=sk-...
# Anthropic key for the Claude provider (optional)
anthropic=sk-ant-...

# --- dev modes (DEBUG builds only; none of these compile into Release) ---
# (no flag)          → isolated plaintext sandbox DB, no lock — real data untouched
# dev_unlock=true    → the REAL encrypted DB with the lock screen bypassed (key from the
#                      DPAPI Hello seal, or dev_pin below); idle re-lock disabled
# dev_pin=123456     → PIN fallback for dev_unlock on machines without a Hello enrollment
# dev_security=true  → exercise the full lock flow exactly like Release
```

If no key is found, the app still launches but the model replies with
`[no OpenAI key found in .env — expected 'openai=sk-...']`.

> **Privacy:** `.env` is git-ignored and must never be committed. Nothing personal leaves the
> machine except the conversation content sent to the cloud model — a conscious, disclosed trade
> (see the root README's privacy section).

---

## 4. Build & run

Use the [`scripts/`](../scripts/) helpers (see [`scripts/README.md`](../scripts/README.md)):

```powershell
./scripts/build.ps1                # build (Debug)
./scripts/build.ps1 -Test          # build + run the test suite
./scripts/build.ps1 -Run           # build + launch the app
./scripts/build.ps1 -Clean -Test   # wipe bin/obj, rebuild, test
```

…or call `dotnet` directly:

```powershell
dotnet build src/SemanticPortrait.App -c Debug -f net10.0-windows10.0.19041.0
dotnet test  tests/SemanticPortrait.Tests
dotnet run --project src/SemanticPortrait.App -f net10.0-windows10.0.19041.0
```

bash equivalents: `scripts/build.sh --test`, `scripts/build.sh --run`.

---

## 5. Program entry points & layout

**Process startup (MAUI):**
1. [`MauiProgram.cs`](../src/SemanticPortrait.App/MauiProgram.cs) — `CreateMauiApp()` builds the host
   and **registers every service in DI** (the `Db`, `OpenAIClient`, all the `*Tools`, the
   `AnalystSubagent`, lock/key stores, notifications). This is the wiring hub.
2. [`App.xaml.cs`](../src/SemanticPortrait.App/App.xaml.cs) — the MAUI `Application`; creates the window
   and handles Windows-specific startup (toast activation).
3. [`MainPage.xaml.cs`](../src/SemanticPortrait.App/MainPage.xaml.cs) — hosts the `BlazorWebView`.
4. [`Components/Routes.razor`](../src/SemanticPortrait.App/Components/Routes.razor) → routes to the page.

**The UI** is one page, split by feature:
- [`Components/Pages/Home.razor`](../src/SemanticPortrait.App/Components/Pages/Home.razor) — markup only.
  The logic lives in code-behind partials next to it: `Home.razor.cs` (shell/state/lifecycle),
  `Home.Chat.cs` (send/stream/tool bubbles/analyst hand-off), `Home.Lock.cs` (setup + unlock +
  security), `Home.Import.cs`, `Home.Notifications.cs`, `Home.Settings.cs` (LLM/export/erase),
  `Home.Constellation.cs`. New feature logic goes in the matching partial — or a new one.
- [`wwwroot/app.css`](../src/SemanticPortrait.App/wwwroot/app.css) — all styling (themes, lock-screen aurora).

**The engine** (`SemanticPortrait.Core`, namespace `SemanticPortrait.Core`, no MAUI — that's what
makes it testable) is organized by domain folder:
- `Providers/` — `IChatProvider`/`IEmbedder` + `OpenAIClient`, `LMStudioClient`,
  `MaskingChatProvider`, `ProviderRegistry` (the ACTIVE-provider seam — resolve per call, never
  pin a bare `IChatProvider` from DI), `ModelCatalog`, `LlmConfig`.
- `Storage/` — `Db` (partial classes by domain: `Db.Thread` / `Db.Graph` / `Db.Insights` /
  `Db.Tasks` / `Db.Settings` + `DbRecords`), `KeyVault`, `ProfileStore`, `EnvLoader`.
- `Masking/` — `IMasker`, `RegexMasker`, `MaskingEmbedder` (egress pseudonymization).
- `Tools/` — the function tools exposed to the model (profile, memory, graph, entries,
  predictions, tasks). See §6.
- `Agents/` — `AnalystSubagent` (clean-room analyst), `Compactor`, `Prompts` (the "Anchor" persona).
- `Services/` — `NotificationService`, `ExportService`, `UsageTracker`, `TraceLog`, `IToastScheduler`.
- `Constellation/` — the decoupled VisualModel pipeline behind the graph view.

(App-side platform services — `HelloKeyStore`, `WindowsHello`, toast schedulers — live in
`src/SemanticPortrait.App/Services` + `Platforms/Windows`, namespace `SemanticPortrait.App.Services`.)

---

## 6. Adding a new feature 101

Most features are a **new tool the model can call**. Here's the end-to-end pattern, following the
shape of [`ProfileTools.cs`](../src/SemanticPortrait.Core/Tools/ProfileTools.cs).

### a. (If it needs storage) add to `Db`
Add a table in the schema block (`Storage/Db.cs`) and the read/write methods in the matching
`Db.*.cs` domain partial (or a new one). Keep methods small and
synchronous where the existing ones are.

### b. Create a `FooTools` class in `Core/Tools`
A tool class has four parts:

```csharp
public sealed class FooTools
{
    private readonly Db _db;
    public FooTools(Db db) => _db = db;

    // 1. A tool SPEC (OpenAI flat function-tool JSON shape)
    private static readonly object DoFooSpec = new {
        type = "function",
        name = "do_foo",
        description = "What it does and WHEN the model should call it.",
        parameters = new {
            type = "object",
            properties = new { thing = new { type = "string", description = "..." } },
            required = new[] { "thing" },
            additionalProperties = false,
        },
    };

    // 2. Which specs are exposed (analyst gets writes; main chat agent is usually read-only)
    public IReadOnlyList<object> Specs => new[] { DoFooSpec };

    // 3. Routing: does this class own the tool name?
    public bool Handles(string name) => name is "do_foo";

    // 4. Execution → returns a short string the model sees as the tool result
    public Task<string> ExecuteAsync(string name, string argumentsJson)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var thing = doc.RootElement.GetProperty("thing").GetString();
        // ...do the work via _db...
        return Task.FromResult("ok: noted " + thing);
    }
}
```

### c. Register it in DI
Add `builder.Services.AddSingleton<FooTools>();` in
[`MauiProgram.cs`](../src/SemanticPortrait.App/MauiProgram.cs).

### d. Wire it into the agent(s)
- **Clean-room analyst** ([`AnalystSubagent.cs`](../src/SemanticPortrait.Core/Agents/AnalystSubagent.cs)):
  inject `FooTools`, add `.Concat(_foo.Specs)` to the `specs` list, and add a branch to the local
  `Exec` router (`else if (_foo.Handles(name)) result = await _foo.ExecuteAsync(name, args);`).
- **Main chat agent** ([`Home.Chat.cs`](../src/SemanticPortrait.App/Components/Pages/Home.Chat.cs) `Send`):
  add the tool's read-only spec to the tool list and a branch in its `Exec` router. Remember the
  design rule — **durable writes go through the analyst subagent**, not the main agent.

### e. Tell the model about it
If the behaviour is non-obvious, add a line to the relevant prompt in
[`Prompts.cs`](../src/SemanticPortrait.Core/Agents/Prompts.cs) so the agent knows when to use the tool.

### f. Test it
Add an xUnit test in [`tests/`](../tests/SemanticPortrait.Tests/). DB features get a temp-file `Db`
(`OpenPlaintext()`); anything touching the model uses the `FakeEmbedder`/fakes so tests stay
offline and deterministic. Run `./scripts/build.ps1 -Test`.

### g. Commit
One logical change per commit, build green first, conventional-commit message — see
[`CLAUDE.md`](../CLAUDE.md). Keep [`todos.md`](../todos.md) in sync.

> **Not a tool?** Pure UI features live in `Home.razor` (markup) + the matching `Home.*.cs`
> partial (logic) + `app.css`; new background behaviour (like compaction or notifications) is a
> `Core` service registered in DI. The same DI → wire → test → commit loop applies.
