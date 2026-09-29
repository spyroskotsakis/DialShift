# CLAUDE.md — Claude Code project notes

Full project instructions live in **AGENTS.md** (auto-loaded alongside this file) — follow it every session, including its Quality gates section (no dead code, docs always current, best UI/UX, prod-ready test-and-fix). This file adds Claude-Code-specific setup only.

## Team & tooling available in this repo

- **Expert subagents** pre-defined in `.claude/agents/`: `spec-architect`, `core-engineer`, `playback-engineer`, `platform-engineer`, `ui-engineer`, `release-engineer`, `test-engineer`, and for brief 3 `data-engineer`, `app-integration-engineer`, `docs-engineer` and the read-only or measurement-only reviewers `qa-auditor`, `design-reviewer`, `perf-auditor` (implementer ≠ reviewer ≠ auditor). For multi-lane work, act as orchestrator: plan → delegate via Task → review each result vs acceptance criteria → re-delegate until green. Do not implement yourself.
- **Project skills:** `/radio-catalog-pipeline` (station catalog edits), `/release-packaging` (macOS `.app` + Windows packaging).
- **Path-scoped rules:** `.claude/rules/core-purity.md` (`DialShift.Core/**`), `.claude/rules/data-catalog-only.md` (`data/**`).
- **Enforcement:** `.claude/settings.json` has a PreToolUse hook that blocks `git push` to `origin`/`upstream` — pushes go to the `private` remote only.
- **Active brief:** brief 3 addendum (D120) — a generic VPN/geo-restriction station signal (`requires_vpn`/`vpn_region`) plus UI badges; contracts `docs/catalog-contracts.md` §2.1/§2.3/§3.1/§6–§8 (CAT-19..21), acceptance `docs/acceptance-matrix.md` §14 (VPN-01..09).
