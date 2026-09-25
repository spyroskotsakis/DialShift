# DialShift — Project Instructions

Native menu-bar/tray internet-radio app with a weekly listening schedule. Windows + macOS. (This file is the canonical project instruction set — Claude Code reads it alongside CLAUDE.md; Codex/OpenCode read it as AGENTS.md.)

## Architecture

- `DialShift.Core/` — models, scheduler, settings persistence. UI-free domain. Purity rules below are absolute.
- `DialShift.Mac/` — Avalonia macOS front-end (the current shipping Mac app; becomes `DialShift.App` per the refactor brief).
- `DialShift/` — WPF/WinForms Windows front-end (scheduled for retirement per the refactor brief).
- `DialShift.Tests/` — deterministic console checks, no test framework: `Check(name, condition)` throws on FAIL, prints `PASS:` lines.
- `data/` — radio station catalog (YAML is the ONLY source of truth; see `.claude/rules/data-catalog-only.md`).

## Design briefs (authoritative specs — read before touching the affected code)

- `docs/single-codebase-refactor.md` — one Avalonia codebase, LibVLC (Windows) / AVPlayer (macOS) adapters. Frozen final revision. Follow its §12 execution order, §5 state-machine spec, and all §4–§11 non-negotiables.
- `docs/schedule-timezone-research.md` — per-slot IANA timezone support. QA-B1..B4 are BLOCKING fixes; QA-N1..N9 non-blocking. Never bump `Settings.Version`.
- `docs/claude-goal-execution.md` — the orchestrator goal that sequences both briefs (main agent orchestrates, 7 expert subagents implement).
- `docs/upstream-main-repo-comparison.md` — upstream v0.2.0 reference (AVPlayer `MacAudioSession.cs`, tray-menu fix).

## Git rules (enforced by a hook in .claude/settings.json — never violate)

- Work on a NEW branch off main; commit freely on it.
- Push ONLY to the `private` remote: `git@github.com:spyroskotsakis/dialshift-dev.git`.
- NEVER push to `origin` (public fork spyroskotsakis/DialShift) or `upstream` (tsiger/DialShift, read-only). No PRs to upstream.

## Commands

- Build: `dotnet build DialShift.slnx` — .NET 10 SDK lives at `~/.dotnet` on this Mac (`export PATH="$HOME/.dotnet:$PATH"`).
- Tests: `dotnet run --project DialShift.Tests/DialShift.Tests.csproj` (console checks; non-zero exit = FAIL).
- Mac publish (current): `dotnet publish DialShift.Mac/DialShift.Mac.csproj -c Release -r osx-x64 --self-contained`
- Mac .app: `scripts/build-mac-app.sh` → `dist/DialShift.app` (menu-bar app, `LSUIElement=true`, no Dock icon).
- Windows: `scripts/build.ps1` (falls back to a local SDK at `%LOCALAPPDATA%\DialShift\sdk\dotnet.exe`); optional per-user install `scripts/Install.ps1` (no admin).
- Radio catalog: `data/.venv/bin/python data/build/build_all.py --refresh`

## Conventions & hard rules

- `net10.0`, NO `-windows` TFM. Prefer runtime OS checks over `#if`.
- **Core purity:** `DialShift.Core` never references Avalonia / Windows / macOS / LibVLC / registry / filesystem locations / named pipes / processes / UI dispatch. Clocks: `IClock` (wall) for schedules + persisted timestamps, `IMonotonicClock` for elapsed/wake-gap — wake detection uses monotonic time only.
- **Settings:** JSON in canonical per-user data dirs — `%LOCALAPPDATA%\DialShift\` (Windows), `~/Library/Application Support/DialShift/` (macOS). Never bump `Settings.Version`. Corrupt file → preserve as `settings.json.unreadable-*`, reset defaults.
- **Logs:** structured, redacted — never log credentials or full private stream URLs.
- **Tray (macOS crash pitfall):** create `TrayIcon` + root `NativeMenu` exactly ONCE; refresh by mutating `menu.Items` in place (`Items.Clear()` + re-add). Never reassign `TrayIcon.Menu` or call `SetIcons` again. Monochrome template image + `MacOSProperties.IsTemplateIcon="True"`.
- **Honest labeling:** macOS path is "native osx-arm64 (AVPlayer)" or "clearly labeled osx-x64 Rosetta build". Never ship arm64 unlabeled.
- **Timezone ids:** store IANA only; canonicalize at the boundary via `TryConvertWindowsIdToIanaId`; never compare `entry.TimeZone == TimeZoneInfo.Local.Id`.
- **Playback policy** (retry/fallback/schedule/wake/cancellation) lives in `PlaybackCoordinator` — never in engine adapters. `IPlaybackEngine` is lowest-common-denominator; no ObjC/AppKit types across it.

## Quality gates (apply to EVERY task, non-negotiable)

- **No dead code left behind:** retire fully — deleted, not commented out; grep for stale references (WPF/WinForms, retired files, obsolete branches) and remove them in the same change.
- **Documentation always up to date:** README, THIRD-PARTY-NOTICES, briefs, acceptance matrix, and data/README are updated in the SAME change as the code they describe. A change is incomplete while its docs are stale.
- **Best UI/UX:** the app ships clean and polished — consistent theming, clear schedule/status feedback, fast tray interactions, no dead-end dialogs. UI/UX polish is a gate, not an extra.
- **Prod-ready test-and-fix:** the loop is test → fix → retest, never test → report. Every acceptance-matrix row green + native smokes + clean-machine installs pass before any phase or artifact is declared done.
