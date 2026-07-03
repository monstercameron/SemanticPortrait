# SemanticPortrait — SDLC rules (for Claude)

Working agreement for building this project. Follow these unless Cam says otherwise.

## Product shape (don't drift)
- **Desktop-only** Windows app — **.NET MAUI Blazor Hybrid**, **.NET 10 (LTS)**, **arm64** (X2).
  Not a web app. Razor UI hosted in a native window.
- Source of design truth: `plan.md`. Task tracker ("the jira"): `todos.md`.

## Commit discipline (Cam's rule: one commit at a time, and committed)
- **One logical change per commit.** Small, atomic, self-contained. No mega-commits.
- **Commit as soon as a unit of work is done** — don't let changes pile up uncommitted.
- **Build must pass before committing.** Never commit a broken build.
- **Local commits only. NEVER add a remote / push / sync** without Cam's explicit say-so.
- Conventional-commit style messages: `feat:`, `fix:`, `docs:`, `chore:`, `refactor:`, `test:`.
- End every commit message with:
  `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`

## Jira hygiene (keep `todos.md` clean)
- `todos.md` is the single backlog. Update it **in the same change** as the work it tracks.
- Status markers: `[ ]` todo · `[~]` in progress · `[x]` done · ⚠️ gate · 🔬 spike.
- **Mark items done the moment they're done.** Keep statuses current every session.
- **Report status honestly — do not over-claim.** "Done" means built + working, not "written."
  If something is partial, mark `[~]` and say what's left. (Reality-anchor: accuracy over optimism.)
- New work discovered mid-task → add it to `todos.md`, don't silently expand scope.

## Change management
- Propose non-trivial design changes (and get a nod) before building them; small/obvious fixes
  just do. Keep `plan.md` and `todos.md` in sync with reality.
- Milestones gate each other; respect the M1 (analysis spike) go/no-go before scaling.

## Quality
- Build clean (0 warnings target). Verify behavior, don't assume.
- Tests for: extraction determinism, schema migrations, masking round-trip, edit-versioning.

## Security & privacy (hard rules)
- **Never commit** secrets, API keys, `*.db`, user data, or models (`.gitignore` enforces this).
- Storage is encrypted at rest; cloud calls may be PII-masked (see `plan.md` §6). Don't weaken
  these without an explicit decision logged in `plan.md`.
