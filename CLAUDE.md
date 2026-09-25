# CLAUDE.md — Claude Code project notes

Full project instructions live in **AGENTS.md** (auto-loaded alongside this file) — follow it every session, including its Quality gates section (no dead code, docs always current, best UI/UX, prod-ready test-and-fix). This file adds Claude-Code-specific setup only.

## Team & tooling available in this repo

- **Expert subagents** pre-defined in `.claude/agents/`: `spec-architect`, `core-engineer`, `playback-engineer`, `platform-engineer`, `ui-engineer`, `release-engineer`, `test-engineer`. For multi-lane work, act as orchestrator: plan → delegate via Task → review each result vs acceptance criteria → re-delegate until green. Do not implement yourself.
- **Project skills:** `/radio-catalog-pipeline` (station catalog edits), `/release-packaging` (macOS `.app` + Windows packaging).
- **Path-scoped rules:** `.claude/rules/core-purity.md` (`DialShift.Core/**`), `.claude/rules/data-catalog-only.md` (`data/**`).
- **Enforcement:** `.claude/settings.json` has a PreToolUse hook that blocks `git push` to `origin`/`upstream` — pushes go to the `private` remote only.

## Execution goal

The complete two-brief execution plan — orchestrator role, 7 agent lanes, parallel fan-out, sequencing gates, quality gates — is in `docs/claude-goal-execution.md`. Pipe it as the session goal when executing the briefs.
