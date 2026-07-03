# Security Policy

SemanticPortrait holds the most sensitive document a person can produce — their journal — so
security reports are taken seriously and handled quickly.

## Reporting a vulnerability

**Do not open a public issue for security problems.**

Use GitHub's private reporting: **Security → Report a vulnerability** on this repository
([direct link](https://github.com/monstercameron/SemanticPortrait/security/advisories/new)).

Please include reproduction steps and your assessment of impact. You'll get an acknowledgement
as soon as the report is read, and a fix or a timeline once it's confirmed.

## Scope highlights

Especially interested in reports about:

- **Vault/at-rest encryption** — SQLCipher keying, Windows Hello / PIN key derivation and
  wrapping, anything that lets data be read without unlocking.
- **Lock-screen bypasses** — toast activation, tray, hotkeys, or timers reaching decrypted
  state while locked.
- **Egress** — journal content leaving the machine beyond the disclosed provider calls, or the
  PII-masking layer failing open.
- **OS notification leaks** — private text reaching the lock screen despite classification /
  discreet mode.

## Supported versions

Pre-1.0: only the latest release is supported — please reproduce on the newest version before
reporting.
