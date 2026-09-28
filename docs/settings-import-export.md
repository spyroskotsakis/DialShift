# Settings Import & Export — Move Stations and Schedule Between Computers

> **Status: Ready to execute (spec frozen 2026-09-28).** This is brief 5, after
> `single-codebase-refactor.md` (brief 1), `schedule-timezone-research.md` (brief 2),
> `add-station-catalog-search.md` (brief 3) and `windows-installer.md` (brief 4).
> Execution per §12 by the orchestrator (goal prompt: `docs/settings-import-export-goal.md`,
> ≤4,000 chars, same discipline as `claude-goal-execution-timezone.md`). All decisions taken
> during execution are recorded as D105+ (D104 is the latest in `docs/decisions.md`).

## 1. Context

Today the Settings page's third card (`DialShift.App/Views/Pages/SettingsPage.axaml:41`) has a single
data affordance: **"Open settings folder ↗"**, which reveals `settings.json` in the file manager
(`SettingsPageViewModel.OpenSettingsFolderAsync`, `IFileRevealService`). Moving stations and the
schedule to another computer — or keeping a backup — means manually copying that file, which also
drags along preferences (`Volume`, `LaunchAtLogin`, `StartInTray`, `LastStationId`) and invites
hand-editing of a file whose format is undocumented.

Facts that anchor the design:

- `Settings` (`DialShift.Core/Models/Settings.cs`): `Version` (never bumps), `Stations`, `Schedule`,
  `Volume`, `ScheduleEnabled`, `LaunchAtLogin`, `StartInTray`, `FallbackStationId`, `LastStationId`.
- `Station` = `Id, Name, Url, Tag, Notes?`; `ScheduleEntry` = `Id, StationId, Label, Time, Days,
  Enabled, TimeZone?`. `Notes` and `TimeZone` are nullable and serialize away when null
  (`[JsonIgnore(Condition = WhenWritingNull)]`).
- `SettingsStore` validates only non-empty `Name` + valid `Url`; anything else loads.
- `ISettingsService.CommitAsync(SettingsChange.Stations | Schedule)` is the one mutation path: it
  notifies the coordinator, saves atomically (temp + move), and raises `SettingsChanged` so pages
  and the tray re-render (`DialShift.App/Services/SettingsService.cs`).
- **No file picker exists anywhere in the app today** (no `StorageProvider` usage; grep it).
  Modal dialogs flow through `IDialogService`/`AvaloniaDialogService` (owner = main window or the
  most recent open dialog, §8.2.5).

## 2. Goal

Two new buttons next to **"Open settings folder ↗"** in the Settings page's About card:
**"Import stations & schedule…"** and **"Export stations & schedule…"**. Export writes a small,
versioned transfer file containing **only** the stations and the schedule; Import reads it back,
replacing the current stations and schedule after an explicit confirmation. Same quality gates as
always: no Settings.Version bump, no new NuGet packages, both platforms, deterministic tests.

1. **Export** — native Save dialog (suggested name `DialShift-transfer-<yyyy-MM-dd>.json`) → write
   the transfer file → success dialog with counts.
2. **Import** — native Open dialog → read + **validate before any mutation** → confirmation dialog
   with counts → replace stations + schedule → coordinator/tray refresh → success dialog with
   counts (including any dropped orphan slots).
3. Every failure path shows an honest error dialog and changes nothing; every success/failure is
   logged with counts only (**never stream URLs** — redaction rule).

## 3. What is already good (keep it)

- `SettingsService` atomic saves, `CommitAsync` change flags, `ISettingsService.SettingsChanged`.
- `IDialogService.ConfirmAsync` with the §8.2.5 dialog rules (confirm is the default button,
  cancel is the cancel, owner chain).
- The `UiRig` headless harness (`DialShift.Tests/Ui/UiRig.cs`) with recording dialogs, the
  journaling `SettingsService` and fakes per boundary (`FakeFileReveal`, `FakeCatalogProvider`…)
  — the exact pattern a fake file picker will follow.
- `UiText` for all user-visible strings; `PageViewModel`/`ViewModelServices` for page-level DI.
- Core purity discipline (brief 1): models + pure engines in `DialShift.Core`, I/O only in
  `DialShift.App` Services, wired in `AppComposition`.
- 28 deterministic test suites (`DialShift.Tests/Program.cs`); the smoke run already screenshots
  the Settings page (`settings.png`, `SmokeRunner.cs:641`).

## 4. Main improvements

1. **Pure transfer codec in Core** — `TransferFile` model + `TransferCodec` (serialize, parse,
   validate). No I/O, no clocks: the exporter passes the timestamp and app version in.
2. **Transfer file format v1** (§5) — an envelope around **verbatim** `Station`/`ScheduleEntry`
   JSON, so a roundtrip is byte-stable and there is zero mapping code.
3. **App service** — `ISettingsTransferService` (orchestration: read/write/validate/confirm/commit)
   over explicit paths, plus a thin `ITransferFilePicker` boundary for the native Save/Open
   dialogs (Avalonia `StorageProvider`, owner = main window). Tests and the smoke pass paths
   directly, so the OS dialog never blocks automation.
4. **Two buttons in the About card** + VM commands with busy-state disable and count-based
   success/error feedback.
5. **Import = replace after confirm**, with the two dangling-reference cleanups (§6).
6. **Tests**: new `Transfer` suite + Ui/headless checks + a smoke roundtrip; the existing
   28 suites stay green.

## 5. Transfer file contract (non-negotiable)

```json
{
  "schema_version": 1,
  "app_version": "0.5.0",
  "exported_utc": "2026-09-28T10:00:00Z",
  "schedule_enabled": true,
  "stations": [ { "Id": "…", "Name": "…", "Url": "https://…", "Tag": "…" } ],
  "schedule": [ { "Id": "…", "StationId": "…", "Label": "…", "Time": "08:00", "Days": [1], "Enabled": true } ]
}
```

- `stations` and `schedule` entries serialize **exactly as in `settings.json`** — same property
  names and casing, same null rules (`Notes` and `TimeZone` omitted when null), so export→import
  roundtrips every field with no translation layer.
- `schema_version` is the ONLY version gate. Import accepts `1`; a higher value is refused with
  "This transfer file was made by a newer DialShift." `app_version` is informational (shown in
  the import confirmation); it never blocks.
- **Export** never touches the live `Settings`. **Import validation runs fully before any
  mutation** and refuses the whole file when: JSON is malformed, the shape is wrong,
  `schema_version` ≠ 1, any station has an empty `Name` or an invalid http(s) `Url` (the same
  rules `SettingsStore` applies), or a station `Id` is duplicated. Schedule slots whose
  `StationId` is not among the file's stations are **dropped and counted**, never fatal.
- Import **does not touch** `Volume`, `LaunchAtLogin`, `StartInTray`, the station catalog, or
  `Settings.Version` (never bumped — the file's own `schema_version` is the format gate).
  `schedule_enabled` is restored from the file.
- UTF-8, human-readable JSON (indented), written atomically (temp file + move, the
  `SettingsStore` pattern) so a crash never leaves a half-written transfer file.

## 6. UX spec (non-negotiable behaviors)

- The About card (third `Border` in `SettingsPage.axaml`) gains two buttons **side by side in one
  horizontal row under** "Open settings folder ↗": first "Export stations & schedule…", then
  "Import stations & schedule…" — each `MinWidth="264"`, `HorizontalAlignment="Left"`, ~6 px
  spacing (D118, overriding the Phase 0 stacked default of §11 #7 and D111; the window height stays
  **860**, and the whole About card stays above the fold). Automation names exactly `Export stations
  & schedule` / `Import stations & schedule`. The two button labels live in `UiText` (`Content`
  bindings); the transfer dialogs' copy lives in `Services/TransferText.cs`, not `UiText` (D117).
- **Export flow:** Save picker (suggested name `DialShift-transfer-<yyyy-MM-dd>.json`, `*.json`
  + "All files" filters) → write → success dialog: "Exported N stations and M schedule slots."
  Picker cancelled → nothing happens, no dialog.
- **Import flow:** Open picker → read + validate (nothing mutated yet) → confirmation dialog:
  "Replace your N stations and M schedule slots with the file's X stations and Y slots?" (confirm
  button "Import", per the §8.2.5 default-button rule); when the file carries a non-empty
  `app_version` the confirmation's second line reads "This file was made by DialShift
  {app_version}." (D119; a hand-edited file with no `app_version` shows no such line) → on confirm:
  replace both lists, drop orphan slots, restore `schedule_enabled`, null
  `FallbackStationId`/`LastStationId` when they no longer point at an imported station, then
  `CommitAsync(Stations | Schedule)` → success dialog
  with counts plus an orphan notice when any were dropped ("Z schedule slots referenced missing
  stations and were not imported."). Confirm cancelled → nothing changes, not even a save.
- **Failure feedback:** any read/validate/write failure → a message dialog that reuses
  `UiText.UnexpectedErrorTitle` (D117; the rest of the transfer copy is `TransferText`), state
  untouched.
- **Busy state:** both buttons disabled while a transfer runs (`IsTransferBusy`).
- **Logging:** `transfer.export` / `transfer.import` events carry counts, outcome and the
  validation reason — **never station names, URLs or other data** (redaction rule).
- **QG-03:** DialShift theme + palette, automation names, visible focus, no clipped text at
  780×650, Tab order natural. Existing HS checks on the Settings page are updated in the same
  change (HS-01 asserts the page content).

## 7. Revised target structure

```text
DialShift.Core/
  Transfer/TransferFile.cs            NEW — record: schema_version, app_version, exported_utc,
                                       schedule_enabled, stations[], schedule[] (no I/O)
  Transfer/TransferCodec.cs           NEW — pure: Serialize(TransferFile)→string,
                                       Parse(string)→TransferFile, Validate→errors (§5)
DialShift.App/
  Services/ISettingsTransferService.cs  NEW — ExportAsync(path) / ImportAsync(path) →
                                       TransferResult (counts, orphan count, error reason)
  Services/SettingsTransferService.cs   NEW — file I/O (atomic write), validation, confirm +
                                       success/error dialogs via IDialogService, commit via
                                       ISettingsService.CommitAsync(Stations | Schedule),
                                       logs via IAppLog (counts only)
  Services/ITransferFilePicker.cs       NEW — PickSavePathAsync(defaultName) /
                                       PickOpenPathAsync() → string? (null = cancelled)
  Services/TransferFilePicker.cs        NEW — Avalonia StorageProvider on the main window
                                       (AvaloniaDialogService owner pattern)
  ViewModels/SettingsPageViewModel.cs   + ImportStationsCommand, ExportStationsCommand,
                                       IsTransferBusy; service + picker injected
  Views/Pages/SettingsPage.axaml        + two buttons in the About card (§6)
  AppComposition.cs                     + registrations (singletons, like the other services)
DialShift.Tests/
  Transfer/TransferTests.cs             NEW — the Transfer suite (§8)
  Fakes/FakeTransferFilePicker.cs       NEW — mirrors FakeFileReveal
  Ui/… (HeadlessUiTests, ViewModelTests) — new HS checks (§8)
  Smoke/SmokeRunner.cs                  + transfer roundtrip step (explicit paths, no OS dialog)
```

- **Core purity** (absolute): `TransferCodec` takes and returns strings/objects; it never touches
  files, paths, clocks or the process environment. The App layer passes in `app_version`
  (`AppInfo`) and `exported_utc` (`DateTimeOffset.UtcNow`).
- No new NuGet packages — `System.Text.Json` is already referenced.

## 8. Tests (DialShift.Tests — deterministic console checks, no framework)

New `Transfer` suite registered in `Program.cs` (the 29th; `-- --filter Transfer`), plus Ui and
smoke additions; the existing 28 suites stay green on both CI OSes:

- Roundtrip: export → parse → re-import equals the original stations/schedule byte-stably,
  including `Notes`/`TimeZone` null omission (a null field appears nowhere in the JSON).
- Validation refusals: malformed JSON; wrong shape; `schema_version` 0/2; blank `Name`; invalid
  `Url`; duplicated station `Id` — each refused with the reason, no partial state.
- Orphan slots: dropped and counted, never fatal. Dangling `FallbackStationId`/`LastStationId`
  cleared by the import; valid ones kept.
- Envelope: `app_version` and `schedule_enabled` roundtrip; `exported_utc` parseable UTC.
- Settings discipline: import leaves `Volume`/`LaunchAtLogin`/`StartInTray` untouched and
  `Settings.Version == 1`; an old settings file still loads untouched.
- Ui (headless + view-model): both buttons visible with the exact automation names; export with
  a fake picker path writes a parseable file + success dialog; picker cancelled = no dialog, no
  file; import with a fake path replaces lists, clears dangling ids, journals
  `Stations | Schedule`, shows counts; confirm-cancel = zero mutation; malformed file = error
  dialog and zero mutation; buttons disabled while busy.
- Smoke: export to the smoke output dir (service called with an explicit path), mutate a
  station, import the file back, verify the restore — no OS dialog in smoke; `settings.png`
  shows all three buttons.

## 9. Risks & gotchas

- **Settings.Version is sacred** — the transfer file carries its own `schema_version`; the app's
  settings Version stays 1 no matter what is imported.
- **Redaction** — the transfer file contains stream URLs; log events must carry counts only.
  The existing redaction suite guards this (`Redaction` suite).
- **OS dialogs block automation** — the picker is a thin boundary; the service takes explicit
  paths and every test/smoke bypasses the picker (the `FakeFileReveal` precedent).
- **Compiled bindings** — `SettingsPage.axaml` uses `x:DataType`; every new binding needs real
  VM properties or the XAML fails to compile.
- **Import while playing** — `CommitAsync(Stations | Schedule)` refreshes the coordinator; a
  currently-playing station that disappears follows the existing fallback/retry policy
  (`PlaybackCoordinator`). No new playback logic.
- **Ownerless pickers** — when the main window is hidden (tray-only) the picker must still get
  an owner (window shown / `Show()` path), per the §8.2.5 owner rule.
- **Atomicity** — write the export atomically; validate imports fully before the first mutation.
- **Stale wording** — `scripts/native-check/macos.sh:909,1095` and
  `scripts/native-check/windows.ps1:935,1124` describe the Settings page; update the wording in
  the same change. HS-01 asserts the page content; update it in the same change.
- **No dead code** — grep for stale "copy settings.json by hand" phrasing in docs after the
  change and remove it.

## 10. Definition of done + acceptance matrix (IE rows)

**Definition of done:** the Settings page exports and imports a versioned transfer file
containing exactly stations + schedule (+ `schedule_enabled`); import validates before mutating,
replaces after confirmation, cleans up dangling references and commits through the one
`ISettingsService` path; the 28 existing suites plus the new `Transfer` suite pass on both CI
OSes; the native regression (IE-13) passes on this Mac and in the Windows 11 Parallels VM
(fresh install after uninstall); the four standing quality gates hold (no dead code; docs current
in the same change; QG-03 UI/UX polish; test→fix→retest until green). **The branch is NOT merged
to `main`** — the orchestrator stops after the final report; the user checks everything and only
then confirms the merge.

| ID | Behavior | Evidence |
|---|---|---|
| IE-01 | Export writes a `schema_version=1` transfer file: envelope + verbatim `Station`/`ScheduleEntry` JSON; `Notes`/`TimeZone` omitted when null | `Transfer` suite (byte-level) |
| IE-02 | Roundtrip: export → import reproduces stations, schedule and `schedule_enabled` exactly (ids preserved) | `Transfer` suite |
| IE-03 | Import replaces after confirmation; cancel leaves everything untouched (no mutation, no save) | `Transfer` + `Ui` checks |
| IE-04 | Validation before any mutation: malformed JSON, wrong shape, `schema_version` 0/2, blank `Name`, invalid `Url`, duplicate station `Id` → error dialog, nothing changes | `Transfer` suite |
| IE-05 | Orphan schedule slots (missing `StationId`) dropped and counted in the result message | `Transfer` + `Ui` checks |
| IE-06 | Dangling `FallbackStationId`/`LastStationId` nulled after import; valid ones kept | `Ui` checks |
| IE-07 | `Volume`/`LaunchAtLogin`/`StartInTray` untouched; `Settings.Version` stays 1 | `Transfer` suite |
| IE-08 | Buttons "Export stations & schedule…" / "Import stations & schedule…" side by side in one horizontal row under "Open settings folder ↗" (each `MinWidth=264`; D118), exact automation names, disabled while busy | headless HS checks |
| IE-09 | Native Save/Open pickers via `StorageProvider`, main-window owner, suggested name `DialShift-transfer-<yyyy-MM-dd>.json`; cancel = null, no side effects | `FakeTransferFilePicker` checks + native check |
| IE-10 | Success dialogs show counts (+ orphan notice); failures via `UiText`; `transfer.import`/`transfer.export` log counts only, never URLs | `Ui` checks + redaction suite |
| IE-11 | Import commits via `CommitAsync(Stations | Schedule)` → coordinator notified, tray and pages re-render | `Ui` journal checks |
| IE-12 | QG-03 polish (theme, focus, no clipped text at 780×650, Tab order); docs current in the same change (README, matrix, decisions D105+, native-check wording, HS-01); 29 suites green on both CI OSes; smoke roundtrip passes in the bundle | design review + CI + smoke |
| IE-13 | **Native regression (both OSes):** macOS — build + full suites + smoke + by-hand export→import roundtrip on this Mac. Windows — in the user's Windows 11 ARM64 Parallels VM (24H2, x64 under emulation): **uninstall the existing DialShift, install the fresh build**, run `--smoke-test --recovery-test` from the installed package, then by-hand export→import roundtrip + regression (tray, playback, Settings page) | native check, recorded in the report |

## 11. Open questions (pre-seeded defaults — record as decisions, do not stop to ask)

| # | Question | Default (adopt) |
|---|---|---|
| 1 | Replace vs merge on import | Replace after confirmation (predictable, idempotent restore; merge/dedupe is a future ask) |
| 2 | What the file contains | Stations + schedule + `schedule_enabled` only — exactly what the buttons say |
| 3 | Entry JSON shape | Verbatim `Station`/`ScheduleEntry` properties incl. casing + null rules (byte-stable, zero mapping) |
| 4 | Default file name / filters | `DialShift-transfer-<yyyy-MM-dd>.json`; `*.json` + "All files" |
| 5 | Version gating | `schema_version` only; higher → refuse with a message; `app_version` informational |
| 6 | Orphan slots | Drop + count in the success message, never a hard failure |
| 7 | Button layout | Side by side in one horizontal row under "Open settings folder ↗", each `MinWidth=264` (the Phase 0 default was stacked, `MinWidth=216`; D118 overrides it and the window height stays 860) |
| 8 | Feedback style | Message dialogs (consistent with the BHV-16 save-failure dialog) |
| 9 | Smoke automation | Service called with explicit paths — no OS dialog in smoke |
| 10 | `exported_utc` source | App layer passes `DateTimeOffset.UtcNow` into the pure codec |
| 11 | Import confirmation default button | "Import" is the default (the §8.2.5 confirm-button rule) |

## 12. Execution order (phases, orchestrator-run, existing roster — no new agent files)

The main agent **only orchestrates** (never edits code/docs): delegates via the Task tool to the
`.claude/agents/` roster, reviews diffs + real build/test output, rejects and re-delegates until
green. All 13 agent files exist — reuse them; **`data-engineer`, `playback-engineer`,
`platform-engineer`, `perf-auditor` are off-mission** (declare it explicitly). No Phase-0 agent
bootstrap.

1. **Phase 0 — spec (spec-architect only):** freeze the contracts (`TransferFile`,
   `TransferCodec`, `ISettingsTransferService`, `ITransferFilePicker`); IE-01..12 rows into a new
   section of `docs/acceptance-matrix.md`; record §11 defaults as decisions D105+. Gate: the
   existing 28 suites still green (baseline).
2. **Parallel lanes (non-overlapping file ownership):**
   - **Phase 1 — core (core-engineer):** `Transfer/TransferFile` + `TransferCodec` + validation
     (§5, §7, pure, no I/O). Gate: Core builds `-warnaserror`, new `Transfer` suite green (with
     test-engineer).
   - **Phase 2 — integration (app-integration-engineer):** `ISettingsTransferService` +
     `SettingsTransferService` (atomic write, replace-after-confirm, dangling-id cleanup,
     `CommitAsync(Stations | Schedule)`) + `ITransferFilePicker`/`TransferFilePicker`
     (`StorageProvider`, main-window owner) + `AppComposition` DI. Gate: build green, service
     roundtrip on real files in tests.
   - **Phase 3 — UI (ui-engineer):** §6 buttons, VM commands, busy state, dialogs. Gate: build
     green, HS-01 updated, new HS checks green.
3. **Phase 4 — verification wave (test-engineer + qa-auditor + design-reviewer,** all
   fresh-context on the diff only): full suite on both CI OSes, IE-01..12 all GREEN, QG-03
   audit, dead-code grep, Settings.Version audit, redaction grep (no URL logging), correctness-
   only findings (Critical blocks, ordered, each with the concrete fix). Fix loop: max 3 rounds,
   then escalate to the user.
4. **Phase 5 — native regression, both OSes (orchestrator-driven, evidence in the report, IE-13):**
   - **macOS (this Mac):** `dotnet build DialShift.slnx -c Release -warnaserror`, the full test
     run, the native smoke and a by-hand pass of the Settings page: real Save/Open pickers,
     export → change a station → import back → verify.
   - **Windows (the user's Windows 11 ARM64 Parallels VM, OS 10.0.26100 24H2, x64 package under
     emulation — the platform the repo's NC-18/NC-19/NC-20 evidence already uses):**
     **uninstall the existing DialShift, then install the fresh build** (setup or zip), run
     `--smoke-test --recovery-test` from the installed package, then by-hand: export → change →
     import back → verify, plus a regression pass (tray, playback, Settings page, dialogs).
     Record what the VM cannot prove (a physical Windows 11 PC, Narrator/VoiceOver) as remaining
     native checks.
5. **Phase 6 — docs + report (docs-engineer + release-engineer):** README, matrix status banner,
   decisions D105+, native-check wording, smoke roundtrip evidence; release notes NOT yet
   (release-time); final report (per-agent summaries, real build/test/publish excerpts, matrix
   status, the IE-13 native evidence from both OSes, remaining native checks, decisions,
   `git log --stat`). **Then STOP: the branch stays unmerged.**

Global guardrails for every agent: branch `feature/settings-import-export` off `main` (created
if missing), commit freely, push ONLY the `private` remote
(`git@github.com:spyroskotsakis/dialshift-dev.git`); never `origin`/`upstream`
(tsiger/DialShift, read-only); no PRs; never bump `Settings.Version`; Core purity (no
Avalonia/OS/filesystem in Core); no dead code (delete, never comment-out); docs updated in the
SAME change as code; log only counts for transfers, never URLs; verify with real command output,
never invented results. **NEVER merge to `main`:** the orchestrator leaves the branch unmerged
after the final report; the user checks all of it and confirms the merge himself — merging before
his confirmation is a blocker.

## 13. Decision

**Proceed** with this design once the user approves the brief. Execution follows §12 with the
orchestrator-only main agent and the goal prompt in `docs/settings-import-export-goal.md`. The
orchestrator never merges to `main`: it stops after the final report (including the IE-13 native
regression evidence from macOS and the Windows 11 Parallels VM), and the user merges after
checking and confirming everything.
