# SemanticPortrait

*One conversation, kept for a lifetime — and a quiet intelligence that learns who you are from it.*

Most journaling ends where it should begin: the words go down, and nothing reads them back.
SemanticPortrait is the other half. You write to a single, unending thread, and a frontier
model listens the way a good analyst would — noticing, remembering, gently telling you the
truth. Over time it draws a portrait of you that you can actually see, and returns it to you
when you need it most: when you're about to believe something about yourself that isn't so.

---

## The idea

Turn an ongoing conversation into a living, self-correcting model of who you are — and let it
keep you in reality, not in your distortions.

It is built around a simple discipline most of us can't keep alone: **separate what happened
from the story you told about it.** The model records the timestamped facts, marks its own
inferences as inferences, and holds the line between the two so the record stays honest as the
years accumulate.

## What it does

- **One eternal chat thread** — your whole history in a single conversation, streamed token by
  token, rendered in clean Markdown. Nothing is ever deleted.
- **A self-updating memory.** Every meaningful exchange is studied by a background analyst that
  writes durable notes, stores stable facts about you and the people in your life, and keeps the
  record current as new information refines the old.
- **Semantic recall.** Ask about something from months ago and it finds it — a vector index over
  every entry and note surfaces the relevant past, not just the recent.
- **The Constellation.** A toggleable, force-directed mindmap of you — people, themes, patterns,
  distortions, values — built entirely from the analysis.
- **A prediction ledger / track record.** It logs falsifiable forecasts and scores itself when
  reality arrives, so you can see — over time — where your instincts are sharp and where they lie.
- **Inspectable thinking.** Tool calls appear as quiet reference chips in the chat ("noted this for
  later"); click one to see exactly what it recorded. A developer trace shows the full reasoning.
- **Bulk import & export.** Bring in years of old notes and prior analysis from text files — a
  pre-pass counts the facts and a live status bar tracks the work — or export your whole thread.
- **Thought compaction.** Conversations older than two days fold into a rolling summary to stay
  fast and focused; the full detail never leaves the searchable store.
- **Guided onboarding.** A short, conversational setup that gets the portrait started — and runs
  again from a clean slate if you ever erase everything.
- **Locked, encrypted, light or dark.** Windows Hello or PIN to enter; everything encrypted on
  your machine; a calm UI in either theme, with a quiet aurora over the lock screen.
- **Cost in the open.** Live token-usage and spend tracking, per session and lifetime — visible or
  hidden as you prefer.

## What it feels like

- **One thread, forever.** Never a new chat. Continuity *is* the product — nothing is deleted,
  edits are versioned, and the past stays the past.
- **An analyst, not a cheerleader.** The voice is calm and unsentimental. It won't flatter you,
  and it won't catastrophize with you. When you drift into rejection-radar or hope-as-fact, it
  says so — kindly, plainly.
- **It keeps score.** When you forecast something, it logs a falsifiable prediction with an
  observable criterion, then checks itself against what actually happened. Calibration, not
  reassurance. Anti-delusion by construction.
- **The Constellation.** A living mindmap of you — people, themes, patterns, the small
  distortions and the load-bearing values — whose colours, shapes and motion are a *deterministic
  function of the analysis itself.* Two people's portraits are comparable; none of it is random.

## How it stays honest

The analysis you can't argue with runs somewhere you can't reach. A **clean-room subagent** does
the durable thinking in a fresh context that never sees the live chat — so the long-term model
can't be coaxed, charmed, or talked soft by what you say in the moment. Personalization flexes
its *tone*; it never touches its *truthfulness*. New claims are researched against what's already
known before they're committed — confirmed, refined, or flagged as a contradiction — so the
portrait accretes rather than drifts.

## Built quietly underneath

A desktop application, not a website — **.NET MAUI Blazor Hybrid** on **.NET 10**, native to
Windows on ARM, light and dark. Conversations reach a cloud frontier model through the streaming
**Responses API** (Claude / OpenAI today; Kimi, GLM and DeepSeek on the horizon). Memory lives in
**encrypted-at-rest SQLite (SQLCipher, AES-256)** with a small vector index for semantic recall,
so the model can find the one thing you said two years ago that matters now. Older turns fold into
a rolling summary; the detail never leaves the searchable store. Voice — on-device speech via the
**Snapdragon NPU** (WhisperToMe for listening, Supertonic for speaking) — is the last mile.

## Yours, and only yours

The vault opens with **Windows Hello or a PIN**, and the encryption key is *derived* from that
secret — there is no copy of it sitting in the clear. Everything is encrypted on your machine.
Optional local PII masking pseudonymizes names and addresses before any cloud call; treat it as
harm-reduction, not anonymity — content can still re-identify you. Reaching for a frontier model
is a conscious, disclosed trade: more intelligence, in exchange for not being fully local. That
choice stays in your hands.

## License

MIT — see [`docs/LICENSE`](./docs/LICENSE).

## More

- [`docs/GETTING_STARTED.md`](./docs/GETTING_STARTED.md) — install, deps, build, entry points, and how to add a feature.
- [`plan.md`](./plan.md) — the full design.
- [`todos.md`](./todos.md) — the granular backlog, by milestone.
