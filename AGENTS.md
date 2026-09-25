# DialShift — Project Instructions

Native menu-bar/tray internet-radio app with a weekly listening schedule. Windows + macOS. (This file is the canonical project instruction set — Claude Code reads it alongside CLAUDE.md; Codex/OpenCode read it as AGENTS.md.)

## Architecture

- `DialShift.Core/` — models, scheduler (per-slot IANA time zones), settings persistence, and the `PlaybackCoordinator` (retry, fallback, schedule, wake). UI-free domain. Purity rules below are absolute.
- `DialShift.App/` — the single Avalonia 12.1.2 app for Windows (`win-x64`, LibVLC) and macOS (`osx-arm64`, AVPlayer): composition root (`Program.cs`, `AppComposition.cs`, `App.axaml.cs`), `Views/`, `ViewModels/`, `Tray/`, `Services/` (engines, dialogs, `FileAppLog`), `Platform/`, `SingleInstance/`, `Interop/` (the only Objective-C declarations) and the `--smoke-test` harness (`Smoke/`).
- The WPF `DialShift/` and Avalonia `DialShift.Mac/` front-ends are retired (decision D8). Their last build, including the last Intel Mac build, is kept at tag `legacy-last-known-good` (commit `82281e5`).
- `DialShift.Tests/` — deterministic console checks, no test framework: `Check(name, condition)` throws on FAIL, prints `PASS:` lines.
- `data/` — radio station catalog (YAML is the ONLY source of truth; see `.claude/rules/data-catalog-only.md`).

## Design briefs (authoritative specs — read before touching the affected code)

- `docs/single-codebase-refactor.md` — one Avalonia codebase, LibVLC (Windows) / AVPlayer (macOS) adapters. Frozen final revision. Follow its §12 execution order, §5 state-machine spec, and all §4–§11 non-negotiables.
- `docs/schedule-timezone-research.md` — per-slot IANA timezone support. QA-B1..B4 are BLOCKING fixes; QA-N1..N9 non-blocking. Never bump `Settings.Version`.
- `docs/add-station-catalog-search.md` — brief 3: a searchable station catalog in the Add-station dialog, fed by the generated `data/output/app-catalog.json`. Its frozen contracts, file-ownership map and test plan are `docs/catalog-contracts.md`; acceptance rows CAT-01..18 (matrix §11); decisions D59+. Goal prompt: `docs/add-station-catalog-search-goal.md`.
- `docs/claude-goal-execution.md` — the orchestrator goal that sequences both briefs (main agent orchestrates, 7 expert subagents implement).
- `docs/upstream-main-repo-comparison.md` — upstream v0.2.0 reference (AVPlayer `MacAudioSession.cs`, tray-menu fix).

## Git rules (enforced by a hook in .claude/settings.json — never violate)

- Work on a NEW branch off main; commit freely on it.
- Push ONLY to the `private` remote: `git@github.com:spyroskotsakis/dialshift-dev.git`.
- The public repo `origin` (spyroskotsakis/DialShift) receives `main` + release tags only by a manual maintainer push after the private dry run; agents never push there. Never push to `upstream` (tsiger/DialShift, read-only). No PRs to upstream.

## Commands

- Build: `dotnet build DialShift.slnx -warnaserror` — .NET 10 SDK lives at `~/.dotnet` on this Mac (`export PATH="$HOME/.dotnet:$PATH"`).
- Tests: `dotnet run --project DialShift.Tests/DialShift.Tests.csproj` (console checks in suites; non-zero exit = FAIL; `-- --filter <text>` runs matching suites; checks that need the other OS print `SKIP`).
- Publish: `dotnet publish DialShift.App/DialShift.App.csproj -c Release -r win-x64 --self-contained` or `-r osx-arm64`. These are the only two RIDs; never `osx-x64` (D13).
- Mac .app + zip: `scripts/build-mac-app.sh [--version <semver>] [--build-number <n>]` → `dist/DialShift.app` (menu-bar app, `LSUIElement=true`, no Dock icon, ad-hoc signed) and `dist/DialShift-osx-arm64-native-avplayer.zip` (published as the release asset `DialShift-macos-arm64.zip`). Bundle layout (D51): `Contents/MacOS` holds only Mach-O files (the apphost, the runtime and native dylibs, `createdump`, a `DialShift.dll` symlink); the managed `.dll`/`.json` files live in `Contents/Resources/app`, joined by symlinks. The zip is written with `ditto -c -k --norsrc --noextattr --noacl --keepParent` (no `._*` entries). Verify with `scripts/verify-mac-app.sh dist/DialShift.app` and `scripts/verify-mac-app.sh --zip <zip>` (extracts with both `ditto` and `unzip`).
- Windows: `scripts/build.ps1 [-SkipTests] [-Version <semver>]` → `artifacts/DialShift-win-x64/` + `artifacts/DialShift-win-x64.zip`, verified by `scripts/verify-win-package.ps1 -Path <folder>` (falls back to a local SDK at `%LOCALAPPDATA%\DialShift\sdk\dotnet.exe`); optional per-user install `scripts/Install.ps1` (no admin).
- Versions (D53): the `<Version>` in `DialShift.App/DialShift.App.csproj` is the only source of `MAJOR.MINOR.PATCH`; `--version`/`-Version` may only add a pre-release suffix (a different `MAJOR.MINOR.PATCH` is refused). Default: the csproj `<Version>`.
- Release: bump `<Version>` in `DialShift.App/DialShift.App.csproj`, add a `## [X.Y.Z[-rc.N]] - YYYY-MM-DD` section to `CHANGELOG.md` (it becomes the release notes via `scripts/release-notes.sh`), commit, then `git tag -a vX.Y.Z[-rc.N] -m "DialShift X.Y.Z"`. Push the tag to `private` (dry run); the maintainer pushes `main` plus the tag to `origin` manually. `.github/workflows/release.yml` verifies (the full `.github/workflows/build.yml` run at the tag) and publishes the three assets `DialShift-win-x64.zip`, `DialShift-macos-arm64.zip` and `SHA256SUMS.txt`. A `-suffix` makes it a pre-release (D53). Steps: README "Releasing (maintainers)".
- Native smoke: `<app> --smoke-test [--recovery-test] [--output <dir>]`, with `<app>` = `dist/DialShift.app/Contents/MacOS/DialShift` (run the bundle: App Transport Security applies only inside it) or `artifacts\DialShift-win-x64\DialShift.exe`. It uses an isolated temp data folder and volume 0, and writes `results.json`, screenshots and the log to `<dir>`. On Windows start it with `(Start-Process <exe> -ArgumentList '--smoke-test','--recovery-test','--output',"$PWD\smoke" -Wait -PassThru).ExitCode` (PowerShell does not wait for a GUI exe), and on a machine without an audio device set `$env:DIALSHIFT_AUDIO_OUTPUT = 'dummy'` first (LibVLC's silent output, D35; ignored on macOS). `DIALSHIFT_DATA_DIR=<absolute path>` isolates settings, log and lock for any dev run.
- Exit codes (D29): 0 normal quit or second launch activated; 1 startup failed (dialog shown; also an unusable lock file, a bad `DIALSHIFT_DATA_DIR`, or an exception escaping the UI toolkit); 2 second launch couldn't activate; 3 activation channel failed to start; 4 smoke test failed or its watchdog fired.
- Radio catalog: `data/.venv/bin/python data/build/build_all.py --refresh`

## Conventions & hard rules

- `net10.0`, NO `-windows` TFM. Prefer runtime OS checks over `#if`.
- **Core purity:** `DialShift.Core` never references Avalonia / Windows / macOS / LibVLC / registry / filesystem locations / named pipes / processes / UI dispatch. Clocks: `IClock` (wall) for schedules + persisted timestamps, `IMonotonicClock` for elapsed/wake-gap — wake detection uses monotonic time only.
- **Settings:** JSON in canonical per-user data dirs — `%LOCALAPPDATA%\DialShift\` (Windows), `~/Library/Application Support/DialShift/` (macOS). Never bump `Settings.Version`. Corrupt file → preserve as `settings.json.unreadable-<timestamp>` (`-2`, `-3`, … when taken; never overwrite a backup), reset to defaults. Never replace the original without a flushed, preserved copy: if the copy fails, run on defaults and refuse to save until it succeeds (D50).
- **Logs:** structured, redacted — never log credentials or full private stream URLs.
- **Tray (macOS crash pitfall):** create `TrayIcon` + root `NativeMenu` exactly ONCE; refresh by mutating `menu.Items` in place (`Items.Clear()` + re-add). Never reassign `TrayIcon.Menu` or call `SetIcons` again. Monochrome template image + `MacOSProperties.IsTemplateIcon="True"`.
- **Honest labeling:** the only macOS build is "native osx-arm64 (AVPlayer)", artifact label `native-avplayer` (D36). It is ad-hoc signed, not notarized and not clean-machine tested until NC-07/NC-09 pass, and the README says so. No `osx-x64` artifact is produced (D13); the last Intel/Rosetta build is only at tag `legacy-last-known-good`.
- **Timezone ids:** store IANA only; canonicalize at the boundary via `TryConvertWindowsIdToIanaId`; never compare `entry.TimeZone == TimeZoneInfo.Local.Id`.
- **Playback policy** (retry/fallback/schedule/wake/cancellation) lives in `PlaybackCoordinator` — never in engine adapters. `IPlaybackEngine` is lowest-common-denominator; no ObjC/AppKit types across it.

## Quality gates (apply to EVERY task, non-negotiable)

- **No dead code left behind:** retire fully — deleted, not commented out; grep for stale references (WPF/WinForms, retired files, obsolete branches) and remove them in the same change.
- **Documentation always up to date:** README, THIRD-PARTY-NOTICES, briefs, acceptance matrix, and data/README are updated in the SAME change as the code they describe. A change is incomplete while its docs are stale.
- **Best UI/UX:** the app ships clean and polished — consistent theming, clear schedule/status feedback, fast tray interactions, no dead-end dialogs. UI/UX polish is a gate, not an extra.
- **Prod-ready test-and-fix:** the loop is test → fix → retest, never test → report. Every acceptance-matrix row green + native smokes + clean-machine installs pass before any phase or artifact is declared done.
