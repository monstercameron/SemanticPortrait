# Contributing to SemanticPortrait

Thanks for the interest! This is a young, opinionated project — small, focused PRs land best.

## Build

- **Windows 10/11**, [.NET 10 SDK](https://dotnet.microsoft.com/download), MAUI workload:
  `dotnet workload install maui-windows`
- Build the app: `dotnet build src/SemanticPortrait.App/SemanticPortrait.App.csproj -f net10.0-windows10.0.19041.0`
  (use `-r win-arm64` on Windows-on-ARM, `-r win-x64` otherwise)
- Run tests: `dotnet test tests/SemanticPortrait.Tests/SemanticPortrait.Tests.csproj`
- More detail: [`docs/GETTING_STARTED.md`](docs/GETTING_STARTED.md)

## Ground rules

- **One logical change per PR.** Small, atomic, self-contained.
- **Build must pass, tests must pass** (`dotnet test`) — CI enforces both.
- Conventional-commit style messages: `feat:`, `fix:`, `docs:`, `chore:`, `refactor:`, `test:`.
- Match the surrounding code's style and comment density; comments explain constraints, not
  what the next line does.
- **Never commit** secrets, API keys, databases, models, or personal data. Test fixtures must be
  fictional. `.gitignore` enforces most of this — but review your diff before pushing.

## Design boundaries (don't drift)

- Desktop-only Windows app (.NET MAUI Blazor Hybrid). Not a web app.
- Storage is encrypted at rest; cloud calls may be PII-masked. Don't weaken either.
- The clean-room analyst never sees the live chat; personalization flexes tone, never truth.

## Bugs & ideas

Open an [issue](https://github.com/monstercameron/SemanticPortrait/issues) — templates provided.
For anything security-sensitive, see [SECURITY.md](SECURITY.md) instead of a public issue.
