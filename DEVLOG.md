# Dev log

Reverse-chronological notes on notable development work. Newest first.

---

## 2026-07-05 — Security review + remediation (v1.3.0)

A full security pass over the app (~22k LOC), then fixes for everything actionable
it surfaced. Techniques: manual review of the crypto/key-storage/masking/provider
crown jewels, a dedicated SQL-injection + path-traversal sweep, source→sink
data-flow tracing from untrusted inputs (imported files, model output, tool args),
and a secrets/`.gitignore` audit.

**Already solid (no change needed):** SQL is fully parameterized (LIKE queries
escape wildcards); no path-traversal sinks and no filesystem access in the LLM tool
surface; crypto is textbook (random 256-bit key, PBKDF2-SHA256 @ 600k, AES-256-GCM
wrap); OAuth is state-validated PKCE; masking wraps every cloud path in DI (chat,
analyst, compactor, embeddings) with no unmasked bypass; secrets are git-ignored and
none are tracked; crash/PII logging is DEBUG-only.

**Fixed:**

- **Stored XSS (high)** — `Home.razor.cs` rendered messages through a default Markdig
  pipeline into a `MarkupString` in the Hybrid WebView, so raw HTML / `javascript:`
  links in an entry (reachable via imported third-party chat logs or prompt-injected
  model output) executed with the decrypted journal on screen — an exfiltration path.
  Now `DisableHtml()` + a link-scheme allowlist, plus a defense-in-depth CSP in
  `index.html` (`connect-src 'self'` closes the beacon channel; the app's own network
  calls run in .NET `HttpClient`, not the WebView, so they're unaffected).
  Verified against script/img/iframe/`javascript:`/`data:` payloads. `700d23a`
- **Masking gap (medium)** — the egress masker only tokenized structured PII
  (email/phone/card/SSN/URL/handle); free-form names/places left in the clear. It now
  also pulls the canonical entity registry, so once the analyst has registered a
  person/place/org, every later mention is pseudonymized before any cloud call.
  Honest limit (tested): the *first* mention, before registration, still egresses.
  `253a3d7`
- **Weak passcodes (medium)** — a short numbers-only PIN is offline-brute-forceable
  from a stolen `keyvault.json` even at 600k PBKDF2 iterations. Set/change now rejects
  all-digit codes under 8 chars (letters or 8+ digits required); existing passcodes
  still unlock. `3261407`
- **XML + CSV injection (low)** — SMS import now parses with `DtdProcessing.Prohibit`
  (billion-laughs closed; external-entity XXE was already blocked by .NET's null
  resolver); CSV export apostrophe-guards cells starting with `= + - @` (spreadsheet
  formula injection). `3807b2a`
- **Import OOM (low)** — bulk import now skips files over 50 MB instead of reading
  them whole into memory. `73c394d`

**Verification:** full test suite 350/350 (7 new security tests); App builds with 0
warnings; the XSS sanitizer confirmed end-to-end against attack strings.

**Residual accepted risks** (design-inherent, documented in `docs/threat-model.md`):
the DPAPI/Hello convenience unlock is exposed to same-user malware by design (the PIN
wrap is the real lock); an entity's first mention egresses before it's registered;
`SidecarVoice` builds its process arg string by interpolation (not exploitable —
`UseShellExecute=false`, temp+GUID paths).
