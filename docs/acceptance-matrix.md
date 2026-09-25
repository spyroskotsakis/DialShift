# Acceptance matrix: behavior inventory, verification plan, QA tracker, contracts

> **Current status (2026-09-25, branch `refactor/single-codebase-timezone` at `a54e423`; the native-check pass on the dev box ran on `2c0909e`).** Phase 1 and Phase 2 are merged, and WPF is retired. The merged waves are:
> - **spec**: inventory, this matrix, Core contracts, decisions D1–D52;
> - **core**: `PlaybackCoordinator`, `RetryPolicy`, the CR-01/CR-03 fixes; Phase 2 per-slot time zones (`30cc8b5`); CF-03 and CF-04 (`ec09114`, D50);
> - **release**: CI matrix, `osx-arm64` `.app` and `win-x64` zip, packaging verification; the WPF retirement (D8) and the `native-avplayer` label (D36); SR-01 and SR-03, the Mach-O-only bundle and the bundle smoke (`dafb74c`, D51); the instruction-file update that closed QG-01 and QG-04 ([Appendix A](#appendix-a-instruction-file-updates-applied));
> - **platform**: startup registration, power events, file reveal, sleep-inclusive clocks, single instance, `FileAppLog`, one ObjC interop; the review fixes F1–F3 and F5–F10 (`2470fee`, D37–D39, D49) and the accept-failure classification (`26763f4`, D46); the no-display startup fix that NC-17 found (`2c0909e`, NX-01, D52);
> - **UI**: MVVM views, tray, dialogs, `PlaybackHost`; UI-D1/UI-D2 and accent contrast (`b37f8ec`, D44, D45); the Phase 2 time-zone picker, rows and UP NEXT (`5ef803b`, `5b2619b`, D43);
> - **playback**: both engines, `PlaybackEngineFactory`, the `DIALSHIFT_AUDIO_OUTPUT` seam (D35), the Windows LibVLC engine tests (HS-17), one redactor (F4, D40), the AVPlayer observer retry (F9, D41), LV-08 credential reuse accepted (D42);
> - **integration**: the composition root;
> - **test**: the native smoke runner; the headless UI and view-model suites HS-01..08, HS-13, HS-14 (`bafffc0`); the Phase 2 `Timezone` suite (`eef6168`); the TZ-12 unit half, 87 checks (`59c6b7c`).
>
> **Tests:** 23 suites. CI run [`36121345627`](https://github.com/spyroskotsakis/dialshift-dev/actions/runs/36121345627) at `a54e423` is green: **windows-latest 1634 passed / 17 skipped, macos-latest 1656 / 4**, 23/23 suites on both; the native smoke (`--smoke-test --recovery-test`) **34/34 on both OSes**; the **bundle smoke** (`--smoke-test` on the packaged `osx-arm64` app extracted with `unzip`) **32/32**; the `win-x64` zip verified, and the `osx-arm64` zip verified after both `ditto` and `unzip` extraction. A local run on macOS arm64 at `2c0909e` gives the same 1656 / 4. The new `RenderTimerFallback` suite (RT-01..RT-07, 16 checks, D52; 16 passed on macOS, 15 passed and 1 skipped on Windows) accounts for the whole difference from the previous run, [`36111775825`](https://github.com/spyroskotsakis/dialshift-dev/actions/runs/36111775825) at `39f5c8a` (windows-latest 1619 / 16, macos-latest 1640 / 4, 22/22 suites), which many row notes below cite. The platform harness and the playback-adapter harness results are in `docs/spikes.md`.
>
> **Final counts.** §3 (106 rows): **62 GREEN, 44 NATIVE-PENDING, 0 TODO** (was 60 / 44 / 2 before the instruction-file update closed QG-01 and QG-04, 59 / 44 / 3 before MX-02; at the first review 28 / 19 / 59). Timezone tracker (§6): QA-B1..B4 all GREEN; QA-N1..N9 8 GREEN and 1 NATIVE-PENDING (QA-N1); §9.2 rows 14 GREEN and 1 NATIVE-PENDING (TZ-12: the unit half is green, the native half is NC-02/NC-08). §7.10: every finding GREEN except SR-02 (NATIVE-PENDING on NC-02) and NX-01 (fixed in `2c0909e`; NATIVE-PENDING only for the start with the displays genuinely asleep, NC-17 step 10 and NC-08 step 5). §7.11: every hazard mitigated, fixed or accepted.
> - **No TODO rows remain.** QG-01 and QG-04 closed when the instruction files `AGENTS.md`, `.claude/skills/release-packaging/SKILL.md` and the release, ui, test, platform and core agent files were brought up to date ([Appendix A](#appendix-a-instruction-file-updates-applied)). Code, projects, scripts, CI, README and `docs/` are current. The "22 suites" count that `AGENTS.md:29` and `.claude/agents/test-engineer.md:7` still carried after `2c0909e` added the 23rd suite was removed from both files in `a54e423` (user-approved), so they no longer state a count (QG-02).
> - **Native pass on the dev box (2026-09-25, macOS 26.5.2, Apple Silicon, build `0.3.0+2c0909e`).** The bundled app was run through LaunchServices for the first time:
>   - **NC-17** found a production crash: launched while the displays were asleep, the app died in Avalonia.Native with `kCVReturnInvalidDisplay` (-6661). It is fixed in `2c0909e` (NX-01, D52).
>   - **NC-17, automated part: PASS.** `open -n -W` smoke plus recovery, 34/34.
>   - **NC-13, LaunchServices half: PASS.** Socket mode 0600, activation of the running instance, clean SIGTERM exit.
>   - **NC-07: partial.** Quarantine, `codesign` and `spctl` behave as expected on this non-clean Mac.
>
>   No §9 check is complete: each of the three keeps a part that needs a person, a real login or a clean machine. So no §3 status changed, and the counts above stand.
> - **Remaining work and blockers:** [docs/open-items.md](open-items.md). It lists every open §9 check by the resource it waits for, with an order of execution, how to record results, and the closure criteria.
> - **NATIVE-PENDING:** every such row names its §9 check. The native checks in §9 are all open: NC-01..13 and NC-15..17 (NC-05 and NC-09, signing and notarization, are release-only and not performed; NC-14 is N/A). NC-07, NC-13 and NC-17 carry "Results so far". What needs the user is listed in [§9.1](#91-native-checks-that-require-the-user).

> **Living document.** This is the behavior inventory required by brief 1 §12 step 1. It is also the acceptance matrix of brief 1 §11 and the timezone QA tracker of brief 2 §9.
>
> - **Every lane updates the rows it touches in the same change as its code** (quality gate: docs always current).
> - A phase is done only when every row in scope is `GREEN`. Rows that need hardware or a native desktop this environment does not have may be `NATIVE-PENDING`, and each of those must also appear in [Remaining native checks](#9-remaining-native-checks).
>
> Baseline for "current behavior" is tag `legacy-last-known-good` (commit `82281e5`): WPF `DialShift/` and Avalonia `DialShift.Mac/`. On this branch `DialShift.Mac/` no longer exists: it was relocated to `DialShift.App/` (commit `2f0ef5c`), and `DialShift.Core/Models.cs` was split into folders (commit `bc38259`). The §2 evidence key maps the old names to the current files. Upstream v0.2.0 (`upstream/main:DialShift.Desktop/`) is a reference only. Line numbers in the briefs are historical, so the evidence here is given as `file:symbol`.

## Contents

1. [Legend](#1-legend)
2. [Behavior inventory](#2-behavior-inventory)
3. [Acceptance matrix](#3-acceptance-matrix)
4. [Legacy SmokeChecks coverage map](#4-legacy-smokechecks-coverage-map)
5. [Playback coordinator behavior spec](#5-playback-coordinator-behavior-spec)
6. [Timezone QA tracker](#6-timezone-qa-tracker-brief-2)
7. [Characterization-test plan](#7-characterization-test-plan)
8. [Contracts](#8-contracts)
9. [Remaining native checks](#9-remaining-native-checks)
10. [Open questions](#10-open-questions-recommended-defaults)
- [Appendix A: Instruction-file updates (applied)](#appendix-a-instruction-file-updates-applied)

---

## 1. Legend

**Status values**

| Status | Meaning |
|---|---|
| `TODO` | Not yet verified. |
| `GREEN` | Every non-N/A column for the row passes, with evidence: a test ID in green CI, or a signed-off manual checklist run. |
| `NATIVE-PENDING` | Every automatable column is green. Only a hardware or native-desktop check is outstanding, and it is listed in §9. |

A status cell may carry a short note: the evidence for `GREEN`/`NATIVE-PENDING`, or what is still missing for `TODO` ("TODO: HS-04, then SMK"). **`SMK` cells count as automatable.** The app's own `--smoke-test` harness (BHV-66) runs in CI on both hosted runners, which give it a desktop session with a tray, and on Windows a silent audio output (`DIALSHIFT_AUDIO_OUTPUT=dummy`, D35). A passing CI smoke makes an `SMK` cell green on that OS. What the smoke cannot show (audible output, a real click on the tray, focus rules, sleep, a real sign-in) stays in `MAN` cells and §9. A cell is green only with evidence from this environment: a green CI run, a local run, or a recorded harness run in `docs/spikes.md`. Code inspection alone never makes a cell green.

**Owner lanes:** `core` · `playback` · `platform` · `ui` · `release` · `test` · `spec`. These match the subagents in `.claude/agents/`.

**Test ID prefixes**

| Prefix | Kind of test |
|---|---|
| `CT-*` | Characterization / unit test in `DialShift.Tests`, deterministic, runs on every PR (§7). |
| `HS-*` | Headless integration test, run in CI on `windows-latest` and `macos-latest` (§7.7). It uses Avalonia headless, in-process services, or a process-level launch with `DIALSHIFT_DATA_DIR`. |
| `SMK` | The app's own `--smoke-test` harness, run by CI on `windows-latest` and `macos-latest` (a real desktop session, not headless), and runnable by hand on any machine. It replaced WPF `SmokeChecks.cs` (§4). |
| `MAN` | Manual native checklist step (§9). |

**Cell values:** `—` means not meaningful at that level. `N/A` means the platform does not apply.

**Behavior-difference tags used in §2**

| Tag | Meaning |
|---|---|
| **[DIFF]** | WPF and Mac behave differently. |
| **[QUIRK]** | Current behavior that the characterization tests mirror. |
| **[BUG]** | Current behavior that the target deliberately fixes. It must not be mirrored. |
| **[NEW]** | Target behavior that neither front-end has today. |

---

## 2. Behavior inventory

**Evidence abbreviations**

| Abbreviation | Source |
|---|---|
| `W:` | `DialShift/` (WPF). Deleted in `0d0e524` (D8); every `W:` reference resolves at tag `legacy-last-known-good` (commit `82281e5`). |
| `M:` | Legacy `DialShift.Mac/` (Avalonia) at the baseline tag. On this branch it is replaced by `DialShift.App/` with the brief 1 §6 layout: the composition root (`Program.cs`, `AppComposition.cs`, `App.axaml.cs`), `AppPaths.cs`, `Views/` (`MainWindow`, `Pages/`, `Dialogs/`), `ViewModels/`, `Tray/`, `Services/` (engines, `PlaybackEngineFactory`, `PlaybackHost`, `IDialogService`, `IUiDispatcher`, `FileAppLog`), `Platform/`, `SingleInstance/` and `Interop/` (the only Objective-C declarations, D27). The legacy code-built views and `RadioController` were deleted in `4ef9515`; references to them below point at the baseline tag. |
| `C:` | `DialShift.Core/`. The former `Models.cs` is split into `Models/` (`Settings`, `Station`, `ScheduleEntry`, `Occurrence`), `Scheduling/` (`Scheduler`, `ScheduleSession`) and `Settings/` (`SettingsStore`). The coordinator is in `Playback/` (`PlaybackCoordinator`, `RetryPolicy`, `Contracts/`). |

`RadioController` is byte-identical on both front-ends except for the wake preamble (QA-N1) and the dispatcher type. Evidence written as `RC:` applies to both: `DialShift/RadioController.cs` (WPF, deleted with `DialShift/` in `0d0e524`) and `DialShift.App/Services/RadioController.cs` (the relocated Mac copy, deleted in `4ef9515` when the coordinator was wired in). Both files now exist only at tag `legacy-last-known-good`.

### 2.1 Lifecycle, startup, data

| ID | Behavior (current) | Evidence | WPF vs Mac differences / notes | Target (`DialShift.App`) |
|---|---|---|---|---|
| BHV-01 | **First launch defaults.** With no `settings.json`, the app loads `Settings.Defaults()`: three SomaFM https stations (Groove Salad, Drone Zone, Secret Agent), Volume 60, schedule off, no fallback, `LaunchAtLogin=false`, `StartInTray=false`. The window is shown, status is "Ready when you are", track is "Choose a station and make yourself at home.", and nothing plays. | C:`Settings.Defaults`, C:`SettingsStore.Load`; RC:`Status`/`Track` initializers | Same. | Identical. `PlaybackSnapshot.Initial`. |
| BHV-02 | **Restore settings.** On start, `settings.json` is read from the data directory. Volume is clamped to 0–100, and stations, schedule, fallback, `LastStationId`, `LaunchAtLogin` and `StartInTray` are restored. Unknown JSON properties are ignored. | C:`SettingsStore.Load` | Same. | Identical. Never bump `Settings.Version` (QA-N3). |
| BHV-03 | **Corrupt-settings recovery.** These cases all throw inside `Load`: invalid JSON, `Version != 1`, a null or blank station name, an invalid URL, null `Days`. The original is copied to `settings.json.unreadable-yyyyMMddHHmmssfff`, defaults are used, and `Warning` is set. A dialog "DialShift · Settings recovered" then shows the backup path. Defaults reach disk only at the next save. | C:`SettingsStore.Load`; W:`App.OnStartup` (`MessageBox`); M:`App.OnFrameworkInitializationCompleted` (`Message.Show(MainWindow, …)`) | **[DIFF]** WPF uses a native `MessageBox`. Mac uses a custom modal owned by `MainWindow`, even when the window is hidden (start in tray). **[QUIRK, fixed by CF-04]** If the backup `File.Copy` itself throws (for example a permission problem, or a backup with the same millisecond name), the exception escapes `Load`. WPF then shows the startup-failure dialog. Mac crashes. | The recovery dialog is shown through `IDialogService`, with the owner resolved safely (§8.2.5). **Fixed in `ec09114` (CF-04, D50):** no exception escapes `Load`. A taken backup name gets `-2`, `-3`, …; if no copy can be made, defaults load with a warning that says so, and `Save` refuses (an `IOException`, shown as "Save failed") until a flushed copy of the original exists. |
| BHV-04 | **Startup failure dialog.** An exception during startup is logged, and a dialog says "DialShift couldn't start. <message> Details: <log path>". The app then exits. | W:`App.OnStartup` try/catch → `ExitApp` | **[DIFF]** WPF only. On Mac, an exception in `OnFrameworkInitializationCompleted` or `Program.Main` is unhandled: no dialog and no log. | **[NEW] on Mac.** Both platforms log `app.startup_failed` and show the dialog. The process exits non-zero. |
| BHV-05 | **Data directory.** Per-user `Environment.SpecialFolder.LocalApplicationData/DialShift`. On Windows that is `%LOCALAPPDATA%\DialShift`. On macOS, .NET 8+ maps it to `~/Library/Application Support/DialShift`. It holds `settings.json`, `dialshift.log`, backups and (Mac only) the lock. | W:`App.DataDirectory`; M:`App.DataDirectory` | Same resolved paths. WPF's `--smoke-test` uses `%TEMP%\DialShift-smoke-<guid>`. | `AppPaths` (§8.2.6), adding the `DIALSHIFT_DATA_DIR` override. macOS is computed explicitly as `~/Library/Application Support/DialShift`. |
| BHV-06 | **Log location and format.** `<data>/dialshift.log` receives appended lines of `{DateTime.Now:O} {exception.ToString()}`. Only exceptions are logged. The log is unstructured, **unredacted** (exception text can contain stream URLs), and grows without bound. Logging failures are swallowed. | W:`App.Log`; M:`App.Log` | Same. | **[BUG→NEW]** Structured JSONL behind `IAppLog`, with redaction and 1 MiB rotation, plus the required events (§8.2.7). |
| BHV-07 | **Single instance: become primary.** WPF uses the named mutex `Local\DialShift.App`, which `--smoke-test` bypasses. Mac takes a file lock on `<data>/.single-instance.lock` (`FileShare.None`) in `Program.Main` before Avalonia starts. | W:`App.OnStartup`; M:`Program.Main`, M:`App.TryAcquireSingleInstance` | **[DIFF]** Mutex vs file lock. | Lock file `<data>/.single-instance.lock` plus a pipe server (§8.2.4). |
| BHV-08 | **Second-launch activation.** A second process asks the first to show its window, then exits silently with code 0. WPF does this with `EventWaitHandle.OpenExisting("Local\DialShift.Activate").Set()`, and a waiter thread in the first process calls `ShowWindow`. Mac connects `NamedPipeClientStream` to the fixed name `"DialShift.App.Pipe"` (250 ms timeout) with **no payload**. The first process treats any connection as activation. | W:`App.OnStartup`; M:`App.SignalExistingInstance`, M:`App.StartActivationListener` | **[DIFF]** Named event vs pipe. **[BUG]** On Mac, any exception stops the listener for good, leaving the app non-activatable. There is no `CurrentUserOnly`, no message, no validation, and the pipe name is not per-user. | Versioned protocol, hardened per brief 1 §7.5, with an ack so the second process can confirm (§8.2.4). |
| BHV-09 | **Start in tray.** Passing `--tray` or setting `Settings.StartInTray` means the main window is not shown at launch. Launch-at-login entries pass `--tray`. | W:`App.OnStartup` (skips `Show`); M:`App.OnFrameworkInitializationCompleted` (assigns `MainWindow`, then `Hide()`) | **[DIFF]** On Mac, the window is assigned as the lifetime `MainWindow` and then hidden, which may flash. Needs native verification. | The window is never shown when starting in tray, with no flash. |
| BHV-10 | **Startup schedule catch-up.** After the UI exists, `StartSchedule()` runs `CheckSchedule(force: true)`. If the schedule is on and a current occurrence exists, that slot's station plays. Otherwise nothing auto-plays; `LastStationId` is **not** auto-resumed. | RC:`StartSchedule`; W/M:`App` startup | Same. | `IPlaybackCoordinator.StartScheduleAsync`. |
| BHV-11 | **Quit.** Tray "Quit DialShift" calls `ExitApp`, which is idempotent (`exiting` flag). It saves settings, disposes the radio (stops audio and releases the engine after retiring players), hides the tray and releases the lock or mutex. WPF also unsubscribes `PowerModeChanged` and sets the activation event so the waiter thread ends. Mac also cancels the pipe listener. Quit is the **only** way to exit (`ShutdownMode` is explicit). | W:`App.ExitApp`; M:`App.ExitApp` | Minor differences in teardown. | A single quit path. Order: save → dispose coordinator and engine → dispose power events → dispose single-instance service (releases the lock) → tray invisible → shutdown. Logs `app.exit`. |
| BHV-12 | **Close to tray.** The window `Closing` event is cancelled and `HideToTray()` runs. Audio and schedule keep running. | W/M:`MainWindow` ctor `Closing` | Same. | Identical. |
| BHV-13 | **Minimize hides.** Minimizing the window hides it. | W:`StateChanged`; M:`PropertyChanged(WindowState)` | Same. | Identical. |
| BHV-14 | **"↘ Hide to tray" header button.** Hides the window. WPF also shows a balloon: "DialShift is in your tray / Your radio and schedule keep running. Right-click the dial icon to quit." (2.5 s, suppressed in smoke mode). | W:`App.HideToTray`; M:`App.HideToTray` | **[DIFF]** The balloon is WPF-only. Avalonia `TrayIcon` has no balloon API. | See OQ-3. |
| BHV-15 | **Show window.** Tray, activation or menu calls `Show()`, sets `WindowState = Normal` and calls `Activate()`. | W/M:`App.ShowWindow` | Same. | Identical. Must bring the window to front from `LSUIElement` (macOS) and from background (Windows focus rules). |
| BHV-16 | **Settings save failure.** The exception is logged and a dialog says "DialShift · Save failed / Couldn't save your changes: <msg>". Saves are atomic (write `.tmp`, flush, `File.Move` with overwrite). | W/M:`App.Save`; C:`SettingsStore.Save` | **[DIFF]** `MessageBox` vs custom modal. | `IDialogService`. Identical text. |
| BHV-17 | **Save triggers.** Saves happen on: Play/Pause button, Skip, Listen, volume slider release or KeyUp, tray station pick, tray volume ±10, page toggles (via `Refresh`), dialog save/delete, and quit. **[QUIRK]** Tray "Play / Pause" and "Next station" do **not** save, so `LastStationId` is persisted only at the next save or at quit. | W/M:`MainWindow`, `App.BuildTrayMenu` | Same. | See OQ-11: save after every tray command. |
| BHV-18 | **Mac Dock/Finder reopen.** With `LSUIElement=true` there is no Dock icon, and there is no `ActivationKind.Reopen` handler. Relaunching from Finder goes through the second-instance path. | M:`scripts/build-mac-app.sh`, M:`App` | Mac-only. Upstream handles `IActivatableLifetime` Reopen. | Handle Reopen by calling `ShowWindow`. The second-instance path is still required. |

### 2.2 Tray / menu bar

| ID | Behavior (current) | Evidence | Differences / notes | Target |
|---|---|---|---|---|
| BHV-19 | **Tray icon.** WPF: `NotifyIcon` with `Assets/dialshift.ico`, tooltip "DialShift · Ready". Mac: `TrayIcon` with `Assets/tray.png` (color, **not** a template image), registered via `TrayIcon.SetIcons` once. | W:`App.CreateTray`; M:`App.CreateTray` | **[DIFF]** Icon assets differ. **[BUG]** The Mac icon is not a template image. | `.ico` on Windows. Monochrome template image with `MacOSProperties.IsTemplateIcon="True"` on macOS (D4). Created exactly once. |
| BHV-20 | **Tray click.** WPF: double-click opens the window, right-click shows the menu. Mac: a click shows the menu; there is no `Clicked` handler. | W:`tray.DoubleClick`; M: none | **[DIFF]** | Windows: left-click (`TrayIcon.Clicked`) opens the window and right-click shows the menu. macOS: click shows the menu. |
| BHV-21 | **Tray menu items, in order.** Open DialShift · Play / Pause · Next station · Stations ▸ (one item per station: `Play` + `Save`) · Follow schedule (checkbox: toggle `ScheduleEnabled`, `RefreshSchedule`, `Refresh`) · separator · Volume +10 · Volume −10 (each saves) · separator · Quit DialShift. | W:`App.BuildTrayMenu`; M:`App.BuildTrayMenu` | Same items and order. | Same items. Command routing is covered by HS-04. |
| BHV-22 | **Tray menu refresh after edits.** WPF rebuilds the `ContextMenuStrip` and disposes the old one, which works. **[BUG] Mac:** `App.Refresh()` calls `BuildTrayMenu()` and discards the result, so the tray never updates: new or renamed or deleted stations and the "Follow schedule" check stay stale. | W:`App.Refresh`; M:`App.Refresh` | **[DIFF]/[BUG]** | Create the `TrayIcon` and root `NativeMenu` **once**. Refresh with `Items.Clear()` and re-add. Never reassign `Menu` and never call `SetIcons` again (brief 1 §7.6). Identity is asserted after every editor operation (HS-03). |
| BHV-23 | **Tray tooltip.** "DialShift · {Current name ?? "Connecting"}" when active, "DialShift · Paused" when inactive. Truncated to 63 characters and refreshed on every `Changed`, which fires every tick. | W/M:`App.UpdateTray` | Same. | Identical, driven by `SnapshotChanged`. |

### 2.3 Main window and player

| ID | Behavior (current) | Evidence | Differences / notes | Target |
|---|---|---|---|---|
| BHV-24 | **Window shell.** Title "DialShift", 1050×860, minimum 780×650, centered. Header has the ◴ brand, "YOUR RADIO, ON TIME." and the hide button. Tabs: Stations / Schedule / Settings. Dark palette `#10191B` with accent `#C2F278`. | W/M:`MainWindow` ctor | WPF adds the DWM dark title bar (`NativeChrome`) and the window icon. | Avalonia Fluent dark on both platforms (D1). |
| BHV-25 | **Player card.** Status is shown upper-cased. The title is `Current.Name`, or "Your next favorite frequency." when empty. The track line follows. The Play button reads "▶  Play" or "Ⅱ  Pause" depending on `IsActive`. There is also a Skip button, a volume slider and a "N%" label. | W/M:`MainWindow.UpdatePlayer` | Same. | Rendered from `PlaybackSnapshot` (HS-13). |
| BHV-26 | **Play/Pause button.** Calls `Toggle()` then `Save()`. `Toggle`: when active, pause. Otherwise play `Desired`, or else the `LastStationId` station, or else the first station. With no stations, nothing happens. | RC:`Toggle` | Same. | `ToggleAsync`. |
| BHV-27 | **Skip.** Calls `NextStation()` then `Save()`. Plays the station after `Desired ?? Current`, wrapping around. If neither is found (index −1), it plays the first station. With no stations, nothing happens. | RC:`NextStation` | Same. | `NextStationAsync`. |
| BHV-28 | **Volume.** Slider 0–100 in steps of 1. `ValueChanged` calls `SetVolume`, which clamps and forwards to the engine; `Mute` is set when the value is 0. The value is applied at session creation and again on `Playing`. It persists on pointer release or KeyUp (Mac) and on LostMouseCapture or KeyUp (WPF). Tray ±10. `Settings.Volume` survives restarts. The WPF slider has an automation name, "Playback volume". | RC:`SetVolume`, RC:`Open`; W/M:`MainWindow` | **[DIFF]** The Mac slider has no automation name. | `SetVolumeAsync(int)`. The engine receives `v/100.0`, and 0 mutes. Automation name on the slider. |
| BHV-29 | **Footer.** Left, with the schedule on: "UP NEXT · {ddd HH:mm}  /  {station}", or "SCHEDULE ON · Add your first time slot" when there is no next slot. With the schedule off: "SCHEDULE OFF · You're in control". Right: "LOCAL TIME · " + `TimeZoneInfo.Local.StandardName`, which shows the standard name even during DST. | W/M:`MainWindow.UpdatePlayer`, ctor | Same. | Adds the zone label for zoned slots (QA-N6, D12). |

### 2.4 Playback, retry, fallback, recovery

| ID | Behavior (current) | Evidence | Differences / notes | Target |
|---|---|---|---|---|
| BHV-30 | **Manual play.** `Play(station, manual:true)` holds the current occurrence (`HoldCurrent`) and sets `Desired`, `LastStationId`, `failures=0`, `fallback=false` and `IsActive`, then calls `Open`. `Open` sets status "Connecting…" (or "Connecting to fallback…") and track "Opening the live stream". | RC:`Play`, RC:`Open` | Same. | CT-PB-03, CT-SM-02. |
| BHV-31 | **Live.** When the engine reports `Playing`: `IsPlaying=true`, status "Live broadcast" (or "Live · fallback station"), volume and mute re-applied, activity and stable-playback timers restarted. | RC:`Open` (`session.Playing`) | Same. | CT-PB-02. |
| BHV-32 | **Now-playing metadata.** On every tick while playing, the LibVLC `Meta(NowPlaying)` value becomes the track line. If it is blank, the station tag is used, else "Live radio". | RC:`Tick` | Same (both use LibVLC). | `ITrackMetadataProvider` is optional. AVPlayer may provide no title, in which case the tag is shown. Document this in README (brief 1 §4.3 step 7). |
| BHV-33 | **Pause (user stop).** `HoldCurrent`, `IsActive=false`, `IsPlaying=false`, retry clock reset, session retired. Status is "Paused · resumes at the next scheduled change" with the schedule on, "Paused" otherwise. Track is "Press play to return to the live broadcast." `Desired` is kept. | RC:`Pause` | Same. | `StopAsync`. CT-PB-38. |
| BHV-34 | **Stream error or end.** `EncounteredError` and `EndReached` both call `Fail()`. A `failurePending` guard means one failure per attempt. `failures++`, the session is retired, `retrySeconds = failures < 3 ? failures*3 : 30`, status "Stream unavailable · retry in Ns", track "Your next scheduled change will still run.", and the retry clock restarts. | RC:`Fail` | Same. | CT-PB-05, CT-PB-17, CT-PB-18. |
| BHV-35 | **Retry backoff.** Delays of 3 s, then 6 s, then 30 s for every later attempt. It **never gives up**. While waiting, the status countdown updates each tick with `ceil(retrySeconds − elapsed)`. | RC:`Fail`, RC:`Tick` | Same. | CT-PB-05, CT-PB-06, CT-PB-08. |
| BHV-36 | **Fallback after 3 failures.** When a retry is due, `failures ≥ 3`, a fallback is configured and differs from `Desired`, and we are not already on the fallback: open the fallback. `Desired` is unchanged, and the snapshot shows "Connecting to fallback…". | RC:`Tick` | Same. | CT-PB-07, CT-PB-09. |
| BHV-37 | **Fallback re-tries the primary after 120 s.** While the fallback plays, the retry clock restarts with `retrySeconds=120`. When that expires, `Desired` is reopened. If the primary fails again, the app waits 30 s and returns to the fallback, so the two alternate. If the fallback itself fails, the app waits 30 s and tries the primary. | RC:`Tick` (last three statements) | Same. | CT-PB-10, CT-PB-11, CT-PB-12. |
| BHV-38 | **60 s of stable playback resets failures.** Applies only to the primary (`!fallback`), after 60 s of continuous playing. | RC:`Tick` | Same. | CT-PB-13, CT-PB-14. |
| BHV-39 | **25 s stall watchdog.** While a session exists, activity is `Open`, `Playing` or every LibVLC `TimeChanged`. After more than 25 s with no activity, `Fail()` runs. This also acts as the connect timeout. It is checked only when no retry is due (`else if`). | RC:`Tick`, RC:`Open` | Same. | Measured as time outside the engine's `Playing` state (see the `IPlaybackEngine` progress rule). CT-PB-15, CT-PB-16. See OQ-6. |
| BHV-40 | **Engine lifetime.** LibVLC is created asynchronously, once (`--no-video --no-osd --network-caching=1500 --http-reconnect`, media option `:no-video`). If creation fails, every `Open` fails and the retry loop runs. Old players are stopped and disposed on a background task, and the next session waits for them. | RC ctor, RC:`Retire`, RC:`Open`, RC:`ReleaseEngine` | Same. **[DIFF]** in native runtime: `VideoLAN.LibVLC.Windows` on WPF, and x86_64 `VideoLAN.LibVLC.Mac` (Rosetta) on Mac. | Windows: `LibVlcPlaybackEngine` with the same options. macOS: `MacAvPlayerPlaybackEngine`, with no LibVLC in the package (D13). |
| BHV-41 | **Stale-callback guard.** Every `Open` and `Retire` bumps `generation`. Callbacks act only if `player == session && IsActive && !disposed`. | RC:`Open` (`OnUi`) | Same. | Session id plus operation generation (brief 1 §5.4–5.5). CT-PB-19, CT-PB-22. |
| BHV-42 | **Sleep/wake recovery.** Windows: `SystemEvents.PowerModeChanged` with `Resume` is dispatched to `ResumeFromSleep`. Mac: in `Tick`, a gap of 15 s or more **measured with `DateTime.UtcNow`** triggers `ResumeFromSleep`. `ResumeFromSleep` runs `CheckSchedule()` (not forced). Then, if active, it sets `failures=0`, `fallback=false` and reopens `Desired`. A paused app stays paused within the same slot; a slot that started during sleep begins playing. | W:`App.PowerChanged`; M:`RadioController.Tick` preamble; RC:`ResumeFromSleep` | **[DIFF] QA-N1:** the resume path differs by platform. **[BUG]** The Mac gap uses wall-clock time, so NTP or manual clock jumps look like wakes. **[BUG]** When `CheckSchedule` just started a new slot, `ResumeFromSleep` opens it a second time (upstream guards this with `before == session`). | Layered (brief 1 §4.4): OS power event plus a tick gap of 15 s or more on a **sleep-inclusive** monotonic clock (D14), both feeding one idempotent recovery with a 10 s debounce (D15). CT-PB-29 to CT-PB-34. |
| BHV-43 | **Dispose.** Sets the `disposed` flag, stops the timer, retires the player and releases the engine after retiring tasks finish. No playback can start afterwards. | RC:`Dispose` | Same. | `DisposeAsync`. CT-PB-35. |

### 2.5 Schedule

| ID | Behavior (current) | Evidence | Differences / notes | Target |
|---|---|---|---|---|
| BHV-44 | **Scheduled switch.** On each 1 s tick, `CheckSchedule()` calls `TakeChange`. A new occurrence triggers `Play(station, manual:false)`, **even while paused**: a pause holds only within the current slot. | RC:`Tick`, RC:`CheckSchedule`; C:`ScheduleSession.TakeChange` | Same. | CT-PB-04, CT-SES-02. |
| BHV-45 | **Manual hold within a slot.** A manual play (Listen, tray station, Toggle, Skip, URL edit) calls `HoldCurrent`, so the next tick does not override the choice. The next occurrence does override it. | RC:`Play`; C:`ScheduleSession.HoldCurrent` | Same. | CT-PB-03. |
| BHV-46 | **Pause holds within a slot.** Uses the same mechanism as BHV-45. | RC:`Pause` | Same. | CT-PB-04. |
| BHV-47 | **Forced refresh replays the current slot [QUIRK, intended].** `RefreshSchedule()` runs `TakeChange(force:true)`. With the schedule on, the current slot's station starts playing even if the user paused or chose manually. Triggers: toggling "Follow schedule" (page and tray), saving or deleting a slot (`EditSlot`), and deleting a station. | RC:`RefreshSchedule`; W/M:`MainWindow.EditSlot`, `ShowSchedule`, `StationDialog` delete; `App.BuildTrayMenu` | Same. Brief 2 §1.3 says "that is intended". | `RefreshScheduleAsync`. CT-PB-27. |
| BHV-48 | **Catch-up across sleep or late start.** `Evaluate` looks ±7 days, so the latest past occurrence is current ("Late wake catches latest slot"). | C:`Scheduler.Evaluate` | Same. | Unchanged on the null-zone path (QA-B1). |
| BHV-49 | **DST (computer-local).** A skipped hour is caught up on the first tick after the jump. A repeated hour does not fire twice, because the key is wall-clock based. | C:`Scheduler` comment, `Occurrence.Key` | Same. | Unchanged on the null-zone path. |
| BHV-50 | **"Follow schedule" toggle.** The page checkbox "Follow my schedule" and the tray item "Follow schedule" both toggle `ScheduleEnabled`, then run `RefreshSchedule` and `Refresh`. | W/M:`MainWindow.ShowSchedule`; `App.BuildTrayMenu` | **[BUG] Mac:** the tray check state goes stale (BHV-22). | Both entry points stay in sync (HS-03). |

### 2.6 Stations and schedule editors

| ID | Behavior (current) | Evidence | Differences / notes | Target |
|---|---|---|---|---|
| BHV-51 | **Stations page.** Shows "NN  SAVED FREQUENCIES" and "+  Add station". Each row has an initial-letter tile, the name, the tag (plus " · Fallback" for the fallback station), "▶  Listen" (`Play` + `Save`) and "Edit". The empty-state text is "Start with a station you love…", followed by the SomaFM credit line. | W/M:`MainWindow.ShowStations` | Same. | Identical (HS-01 screenshots). |
| BHV-52 | **Add/edit station dialog.** Fields: name (≤100 characters, required: "Give this station a name."), description (≤160, defaults to "Internet radio"), URL (≤2048, must be http/https with a host: "Enter a valid HTTP or HTTPS stream URL."). Values are trimmed. Cancel/Save; Save is the default button, Cancel the cancel button. The name field is focused on open. If the URL of the active desired station changes, it plays again as a manual play. | W/M:`StationDialog` | **[DIFF]** WPF sets `AutomationProperties.Name` on fields. Mac does not, and the headless field lookup needs it. | Identical. Automation names on every field. HS-02. |
| BHV-53 | **Delete station.** A confirm dialog asks "Delete {name}[ and its N schedule slot(s)]?". If the station is desired or current, the app pauses. The station and its slots are removed, fallback and last-station references are cleared, then `ForgetStation` and a forced `RefreshSchedule` run, which may start the current slot. | W:`StationDialog` delete lambda; M:`StationDialog.DeleteStation` | **[DIFF]** `MessageBox` vs `Message.Confirm`. | `IDialogService.ConfirmAsync`, then `ForgetStationAsync`, then `RefreshScheduleAsync`. HS-02. |
| BHV-54 | **Settings import/export.** **Not present in either local front-end.** Upstream v0.2.0 has it (`App.ImportSettings`/`ExportSettings`, `.before-import-*` backup). | `upstream/main:DialShift.Desktop/App.cs` | — | Out of scope for this refactor; see OQ-2. |
| BHV-55 | **Schedule page.** Contains "Follow my schedule" and "+  Add time slot" (disabled when there are no stations). Mon–Sun day tabs default to today (local). The selected day's slots are ordered by `Time`. Each row shows the time (accent color if enabled), the station name (or "Missing station"), and the label (or "Scheduled switch" / "Disabled") · days. The empty state reads "No switches on <Day>…". Helper text follows. | W/M:`MainWindow.ShowSchedule` | **[DIFF]** Helper text: "…your Windows time zone" vs "…your local time zone". | Helper text reworded for zones; zone and next local fire time on each row (QA-N6, D12). |
| BHV-56 | **Add/edit slot dialog.** Fields: label (≤150, optional); station combo (the original's station, or the first); time as "HH:mm" (≤5, error "Use a 24-hour time, such as 08:30 or 21:00."); day checkboxes (a new slot pre-selects the selected day); Weekdays/Weekend/Every day presets; Enabled. Other errors: "Choose a station first." and "Choose at least one day." Editing keeps the entry `Id`. Save is followed by `RefreshSchedule` and `Refresh`. | W/M:`ScheduleDialog`; `MainWindow.EditSlot` | **[DIFF]** Automation name on the station combo is WPF only. | Adds the timezone combo (Phase 2). HS-02. |
| BHV-57 | **Schedule conflict.** An enabled candidate conflicts with another enabled entry if the `Time` is equal and the days overlap. The error is "Another enabled slot already starts at this time on one of those days." Editing the same entry is allowed. | C:`Scheduler.Conflicts`; `ScheduleDialog` save | Same. | Zone-aware in Phase 2 (QA-N5). |
| BHV-58 | **Delete slot.** "Delete slot" removes the slot immediately, **without confirmation**. | W/M:`ScheduleDialog` | Same. Station delete does confirm. | See OQ-10. |

### 2.7 Settings page and OS integration

| ID | Behavior (current) | Evidence | Differences / notes | Target |
|---|---|---|---|---|
| BHV-59 | **Launch at login.** Checkbox. WPF writes `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `DialShift` = `"<exe>" --tray`. Mac writes `~/Library/LaunchAgents/com.tsiger.dialshift.plist` with `ProgramArguments=[exe, --tray]`, `RunAtLoad` and `ProcessType=Interactive`. The Mac exe path is **not XML-escaped** and there is no `launchctl` call, so the entry takes effect at next login. On failure, the checkbox reverts and a "Couldn't update startup" dialog appears. The checkbox state comes from `Settings.LaunchAtLogin`, **not from the OS**. | W:`App.SetStartup`, `MainWindow.ShowSettings`; M:`App.SetStartup` | **[BUG] Mac:** unescaped path. **[BUG] Mac:** reverting `IsChecked` fires `IsCheckedChanged` again, so `SetStartup` runs a second time with the old value. **[BUG] both:** the checkbox shows "enabled" even when the entry is stale (the app was moved or upgraded). | `IStartupRegistration`, transactional and observable (§8.2.1). The checkbox reflects `GetStatusAsync()` and shows the diagnostic text. MX-07, MX-15. |
| BHV-60 | **Start in tray checkbox.** "Start in the tray when opened normally" sets `Settings.StartInTray` and saves. The help text reads "Closing the window keeps your radio running. Choose Quit DialShift in the tray to exit." | W/M:`MainWindow.ShowSettings` | Same. | Identical. |
| BHV-61 | **Fallback picker.** Options are "No fallback · keep retrying" plus each station. The choice sets `FallbackStationId` and saves. The help text reads "Retry a failed stream, then use this station as a fallback. Try the original again every 2 minutes." | W/M:`MainWindow.ShowSettings` | Same. | Identical. `NotifySettingsChangedAsync`. |
| BHV-62 | **About card.** "DialShift  /  0.1.0" (hard-coded) plus the tagline. | W/M:`MainWindow.ShowSettings` | Same. | Version read from the assembly, not hard-coded. |
| BHV-63 | **Open settings folder.** WPF runs `ProcessStartInfo("explorer.exe", dir){UseShellExecute=true}`. Mac runs `ProcessStartInfo("open", dir){UseShellExecute=true}`. | W/M:`MainWindow.ShowSettings` | **[DIFF]** Different commands; the path is passed as a single argument string. | `IFileRevealService.RevealInFileManagerAsync(AppPaths.DataDirectory)`, with `ArgumentList` only. HS-12. |
| BHV-64 | **Message and confirm dialogs.** WPF uses `MessageBox`. Mac uses a custom 460×250 modal with OK or No/Yes buttons (Yes is the default, No the cancel). | M:`Message`; W: `MessageBox` | **[BUG] Mac:** a null owner calls `window.ShowDialog(window)`, so the window is its own owner. | `IDialogService` (§8.2.5). An ownerless dialog is centered on screen. |
| BHV-65 | **Accessibility and keyboard.** Save/Yes/OK are the default buttons and Cancel/No the cancel buttons. WPF has automation names and a focus ring. | W:`Dialogs`, `App.xaml` | **[DIFF]** Mac lacks automation names. | Automation names on all inputs, visible keyboard focus (UX gate). |
| BHV-66 | **Legacy native smoke harness.** `DialShift.exe --smoke-test [--output DIR] [--recovery-test]` uses an isolated temp data dir and volume 0, skips single-instance, runs the checks, writes `results.json` and screenshots, then exits. | W:`SmokeChecks.Run`, `App.OnStartup` | WPF only. | Replaced per §4 before WPF is removed (brief 1 §4.5, §7.7). |

---

## 3. Acceptance matrix

Every row started as `TODO`. The status column was last reviewed in the final status pass, on `39f5c8a` (branch head), against CI run [`36111775825`](https://github.com/spyroskotsakis/dialshift-dev/actions/runs/36111775825) at the same commit: windows-latest 1619 passed / 16 skipped, macos-latest 1640 / 4, 22/22 suites, the native smoke 34/34 on both OSes, and the bundle smoke 32/32 on the packaged `osx-arm64` app. A local run on macOS arm64 gives the same 1640 / 4. "On both OSes" in a status note means that run. A note that cites "SMK" means its smoke; the `results.json`, screenshots and log are the `smoke-win-x64` and `smoke-osx-arm64` artifacts (7-day retention; the next CI run repeats the smoke). Notes that cite the previous run, `36108959649` at `923e62e`, were green there and are green again in `36111775825`. The latest CI run, [`36121345627`](https://github.com/spyroskotsakis/dialshift-dev/actions/runs/36121345627) at `a54e423`, is green with 23/23 suites (windows-latest 1634 passed / 17 skipped, macos-latest 1656 / 4), the native smoke 34/34 on both OSes and the bundle smoke 32/32. Every check that a note cites from the earlier runs passes again there; the extra suite is `RenderTimerFallback` (D52). The native pass of 2026-09-25 on the dev box, at `2c0909e` (§9 NC-07, NC-13 and NC-17, each partial), changed no status: every row it touches still has an open part in §9. Test IDs are defined in §7 (`CT-*`, `HS-*`). `SMK` is the app's `--smoke-test` run on a native desktop. `MAN` is a manual checklist step in §9.

**Changes to verification columns made in this review** (spec lane):
- **BHV-27 (Skip):** the native cells change from `SMK` to `MAN`. The smoke never presses Skip. What an SMK check would add, a real engine opening the next station, is the same `UserPlay` transition the smoke's Listen checks already use (CT-PB-25 pins that `NextStationAsync` plays the next station). A real Skip press with audible output is now a step of NC-01 and NC-17.
- **BHV-40, MX-11 (the macOS half of HS-17):** there is no macOS engine check in `DialShift.Tests`. The smoke's `--recovery-test` run on `macos-latest` runs HS-17's case with the real AVPlayer engine, natively: a station at `http://127.0.0.1:1/unavailable` fails `NetworkUnavailable` without a crash, and the run ends through the quit path, which stops and disposes the engine, with exit 0. That run is the evidence for the macOS half.

### 3.1 Behavior rows

| ID | Behavior | Owner lane | Unit | Headless integration | Native Windows smoke | Native macOS smoke | Status |
|---|---|---|---|---|---|---|---|
| BHV-01 | First launch defaults | core, ui | CT-SET-07 | HS-01 | SMK | SMK | GREEN (CT-SET-07; HS-01 on both OSes; SMK: a fresh data folder loads and plays the three default stations; CI `36111775825`) |
| BHV-02 | Restore settings | core | CT-SET-03, CT-SET-04, CT-SET-08 | HS-01 | SMK | SMK | GREEN (CT-SET-03/04/08; HS-01 restores stations, fallback, volume, schedule and flags; SMK "Persistence" reload on both OSes) |
| BHV-03 | Corrupt-settings recovery | core, ui | CT-SET-02, CT-SET-05, CT-SET-06, CT-SET-10, CF-03, CF-04 | HS-07 | MAN | MAN | NATIVE-PENDING (NC-01, NC-17; CT-SET-02/05/06/10 and HS-07 (backup, one notice, defaults written only at the next save) green on both OSes. The CF-04 fix (`ec09114`, D50) is checked in `SettingsStore`: same-millisecond recoveries get `-2`, skip a taken `-3` and use `-4`; with no free name, or a read-only folder (macOS; Windows skips it), `Load` does not throw, the warning says no copy exists, and `Save` refuses with an `IOException` and leaves the original untouched until a copy is made. The BHV-03 quirk (a failed backup copy escaping `Load`) is gone) |
| BHV-04 | Startup failure dialog + log | ui, platform | — | HS-08 | MAN | MAN | NATIVE-PENDING (NC-01, NC-17; HS-08 on both OSes: `app.startup_failed`, the dialog (ownerless before the window exists, owned after), exit 1, exit 3 for a single-instance failure, lock released) |
| BHV-05 | Canonical data directory | platform | — | HS-18 | SMK | SMK | GREEN (HS-18 on both OSes; SMK runs in its isolated `SmokeTestTemp` folder and reloads its settings from there, D21) |
| BHV-06 | Log location, structured + redacted | platform | CT-LOG-01..04 | HS-10 | SMK | SMK | GREEN (CT-LOG-01..04, HS-10, the `Redaction` suite (D40); SMK logs on both OSes: every line valid JSON, `app.start` with version, RID, arch and engine, the unavailable stream logged as `http://127.0.0.1:1/…`, no station path) |
| BHV-07 | Single instance, primary acquisition | platform | — | CT-SI-01, CT-SI-09, CT-SI-11 | SMK | SMK | GREEN (CT-SI-01/09/11 and F6/F7 lock classification (D38) on both OSes; SMK logs `single_instance.primary`, then a real second launch activates it) |
| BHV-08 | Second launch activates the first | platform, ui | — | CT-SI-02..08, CT-SI-10, CT-SI-13, HS-09 | MAN | MAN | NATIVE-PENDING (NC-06, NC-13, NC-17; CT-SI-02..08, 10, 13, F3 activation latch (D37), HS-09 and SMK "Second launch activates the running instance" green on both OSes. Under LaunchServices on the dev box (2026-09-25, `2c0909e`), a second `open` was delivered and acknowledged (NC-13's LaunchServices half), and the smoke's second launch activated in 0.1 s (NC-17's automated part)) |
| BHV-09 | Start in tray (`--tray` / setting) | ui | — | HS-06 | MAN | MAN | NATIVE-PENDING (NC-01, NC-17: no window flash; HS-06 on both OSes: `--tray` and `StartInTray` never show the window, a later activation shows it) |
| BHV-10 | Startup schedule catch-up | core | CT-PB-01 | HS-14 | SMK | SMK | GREEN (CT-PB-01; HS-14 startup catch-up; SMK "Schedule catch-up selects current station" on both OSes) |
| BHV-11 | Quit (clean teardown) | ui, platform | CT-PB-35 | HS-04, HS-08 | SMK | SMK | GREEN (CT-PB-35; HS-04 through the real `App.Run` on both OSes: one quit path, save first, order playback → power events → lock → shutdown, tray hidden, `app.exit … clean=true`, exit 0, a second quit is a no-op, an OS shutdown is never vetoed; HS-08 teardown after a failed start; HS-14 bounded dispose (CR-02); SMK: both runs end through the quit path with exit 0). The quit from a real tray click is also a step of NC-01/NC-17 |
| BHV-12 | Close to tray | ui | — | HS-05 | MAN | MAN | NATIVE-PENDING (NC-01, NC-17; HS-05 green on both OSes) |
| BHV-13 | Minimize hides | ui | — | HS-05 | MAN | MAN | NATIVE-PENDING (NC-01, NC-17; HS-05 green on both OSes) |
| BHV-14 | Hide-to-tray button (+ Windows balloon, OQ-3) | ui | — | HS-05 | MAN | MAN | NATIVE-PENDING (NC-01, NC-17; HS-05 and SMK "Hide to tray" green on both OSes; OQ-3: no balloon) |
| BHV-15 | Show window / bring to front | ui | — | HS-05 | MAN | MAN | NATIVE-PENDING (NC-01, NC-06, NC-17; HS-05 and SMK "Restore from tray" green on both OSes) |
| BHV-16 | Save failure dialog, atomic save | core, ui | CT-SET-09, CF-04 | HS-07 | — | — | GREEN (CT-SET-09; HS-07 on both OSes: `settings.save_failed`, the "DialShift · Save failed" text, the previous file untouched; the read-only-folder variant runs on macOS, Windows skips it (Unix permissions). CF-04 (D50): while an unreadable original has no preserved copy, `SettingsStore.Save` throws `IOException` without writing, which `SettingsService` logs and shows through the same `IOException` path HS-07 checks) |
| BHV-17 | Save triggers (incl. tray commands, OQ-11) | ui | — | HS-04 | — | — | GREEN (HS-04 on both OSes: every window and tray command is followed by a save, including tray Play/Pause and Next station (OQ-11) and the volume release) |
| BHV-18 | Mac Reopen activation (LSUIElement) | ui | — | — | N/A | MAN | NATIVE-PENDING (NC-17; no automatable column) |
| BHV-19 | Tray icon assets (.ico / template) | ui, release | — | HS-15 | MAN | MAN | NATIVE-PENDING (NC-01, NC-12; HS-03 creates the App's real tray with the per-OS asset (`.ico` on Windows, `tray.png` on macOS) and HS-04 asserts the macOS template flag, on both OSes; PK-04) |
| BHV-20 | Tray click semantics per OS | ui | — | — | MAN | MAN | NATIVE-PENDING (NC-01, NC-17; no automatable column; `TrayIconOptions` wires left-click on Windows only) |
| BHV-21 | Tray menu items + command routing | ui | — | HS-04 | MAN | MAN | NATIVE-PENDING (NC-01, NC-17; HS-04 on both OSes: item order, one radio item per station, Pause/checked state, every item routes to the right coordinator call) |
| BHV-22 | Tray menu refresh, identity preserved | ui | — | HS-03 | SMK | SMK | GREEN (HS-03 on both OSes: the same `RootMenu` after every editor operation and schedule toggle, also on the App's own tray; SMK tray identity after every editor operation on both OSes) |
| BHV-23 | Tray tooltip text | ui | — | HS-13 | MAN | MAN | NATIVE-PENDING (NC-01, NC-17: the tooltip on hover; HS-13 on both OSes: "DialShift · Paused", "· Connecting", the station name, truncation to 63) |
| BHV-24 | Window shell, tabs, theme (D1) | ui | — | HS-01 | MAN | MAN | NATIVE-PENDING (NC-01, NC-17 visual pass; HS-01 on both OSes: title, 1050×860, minimum 780×650, centered, D1 palette, header, tabs) |
| BHV-25 | Player card rendering | ui | — | HS-13 | SMK | SMK | GREEN (HS-13 on both OSes; SMK status text, track line and Play/Pause label on both OSes) |
| BHV-26 | Play/Pause toggle resolution | core, ui | CT-PB-24 | HS-04 | SMK | SMK | GREEN (CT-PB-24; HS-04 on both OSes; SMK "Pause stops playback" and live playback on both OSes) |
| BHV-27 | Skip / next station | core | CT-PB-25 | HS-04 | MAN | MAN | NATIVE-PENDING (NC-01, NC-17: a real Skip press and tray Next station; CT-PB-25 and HS-04 (button and tray → `NextStationAsync`, wrap-around, then a save) green on both OSes. The smoke never presses Skip: the native cells were changed from SMK to MAN in this review, see the §3 note) |
| BHV-28 | Volume clamp, forward, mute at 0, persist | core, ui | CT-PB-23 | HS-13 | SMK | SMK | GREEN (CT-PB-23; HS-13 on both OSes: clamp, `v/100`, mute at 0, the release persists; SMK tray Volume ±10 reaches settings and engine on both OSes) |
| BHV-29 | Footer UP NEXT / LOCAL TIME | ui | CT-PB-39 | HS-13 | MAN | MAN | NATIVE-PENDING (NC-01, NC-17; CT-PB-39; HS-13 on both OSes: UP NEXT, SCHEDULE ON/OFF, LOCAL TIME standard name; the zone label for zoned slots in `UiTimeZone` (QA-N6)) |
| BHV-30 | Manual play | core | CT-PB-03, CT-SM-02 | HS-14 | SMK | SMK | GREEN (CT-PB-03, CT-SM-02; HS-14 Listen on both OSes; SMK "Manual selection holds within current slot") |
| BHV-31 | Live state + texts | core | CT-PB-02 | HS-14 | SMK | SMK | GREEN (CT-PB-02; HS-14 "LIVE BROADCAST" on both OSes; SMK "Live broadcast") |
| BHV-32 | Now-playing metadata (optional) | core, playback | CT-PB-37 | HS-17 LV-04 (Windows) | SMK | SMK | GREEN (CT-PB-37; HS-17 LV-04: an ICY StreamTitle becomes the track line on the real LibVLC engine, `windows-latest`, CI `36111775825`; SMK shows the station tag for the `https://` defaults on both OSes; the README states the limits (D26). NC-03 records what public Icecast stations do) |
| BHV-33 | Pause (user stop) texts + cancellation | core | CT-PB-20, CT-PB-38 | HS-14 | SMK | SMK | GREEN (CT-PB-20, CT-PB-38; HS-14 on both OSes; SMK "Pause stops playback") |
| BHV-34 | Stream error / end → failure (once per attempt) | core | CT-PB-05, CT-PB-17, CT-PB-18 | — | SMK (recovery) | SMK (recovery) | GREEN (CT-PB-05/17/18; HS-17 LV-08 failure kinds on the real LibVLC engine; SMK `--recovery-test` on both OSes: three `playback.failed kind=NetworkUnavailable`, one per attempt) |
| BHV-35 | Retry backoff 3/6/30 s, countdown, never gives up | core | CT-PB-05, CT-PB-06, CT-PB-08 | — | SMK (recovery) | SMK (recovery) | GREEN (CT-PB-05/06/08; HS-17 LV-09 (3 s, countdown, 6 s) on the real LibVLC engine; the SMK recovery log shows `retry_in=3s`, `6s`, then `30s` on both OSes) |
| BHV-36 | Fallback after 3 failures | core | CT-PB-07, CT-PB-09 | — | SMK (recovery) | SMK (recovery) | GREEN (CT-PB-07/09; SMK "Failed stream retries and plays fallback": the fallback plays after 3 failures, "Live · fallback station", on both OSes) |
| BHV-37 | Fallback re-tries primary after 120 s; alternation | core | CT-PB-10, CT-PB-11, CT-PB-12 | — | MAN | MAN | NATIVE-PENDING (NC-03, NC-11; CT-PB-10..12 green) |
| BHV-38 | 60 s stable playback resets failures (primary only) | core | CT-PB-13, CT-PB-14 | — | — | — | GREEN (CT-PB-13, CT-PB-14; CI `36111775825`) |
| BHV-39 | 25 s stall watchdog | core, playback | CT-PB-15, CT-PB-16 | — | MAN | MAN | NATIVE-PENDING (NC-03; CT-PB-15/16 green; HS-17 LV-08: a silent server fails `Stalled` after > 25 s outside Playing on the real LibVLC engine, `windows-latest`; the macOS real-engine watchdog is in the adapter-harness corpus, T9/T10/T14) |
| BHV-40 | Engine selection + lifetime (LibVLC Win / AVPlayer mac) | playback | — | HS-15, HS-17 | SMK | SMK | GREEN (HS-15 on both packages; HS-17 LV-01..LV-11 on `windows-latest` (CI `36111775825`): factory, play, volume, stop, 160 rapid switches, stop-while-connecting, transport cases, dispose; macOS: the HS-17 case (real engine against `127.0.0.1:1`, `Failed(NetworkUnavailable)`, no crash, clean stop and dispose) is run by the SMK recovery run on `macos-latest` with AVPlayer, which ends through the quit path with exit 0; SMK live playback on LibVLC and AVPlayer) |
| BHV-41 | Stale callbacks discarded | core | CT-PB-19, CT-PB-22, CT-PB-40 | — | — | — | GREEN (CT-PB-19, CT-PB-22, CT-PB-40; CI `36111775825`) |
| BHV-42 | Sleep/wake recovery (layered) | core, platform | CT-PB-29..34 | HS-16 | MAN | MAN | NATIVE-PENDING (NC-02, NC-08; CT-PB-29..34 green, HS-16 green on both OSes; SMK "Resume preserves pause within same slot" green through `NotifyWakeAsync`) |
| BHV-43 | Dispose prevents further playback | core | CT-PB-35, CT-SM-17 | HS-14 | — | — | GREEN (CT-PB-35, CT-SM-17; HS-14 on both OSes: after quit no tick runs and no command starts playback) |
| BHV-44 | Scheduled switch (even when paused) | core | CT-PB-04, CT-SES-02 | HS-14 | SMK | SMK | GREEN (CT-PB-04, CT-SES-02; HS-14 on the real 1 s heartbeat: the next slot fires within 2 s of its boundary, even when paused; SMK "New schedule occurrence overrides manual station") |
| BHV-45 | Manual hold within slot | core | CT-PB-03, CT-SES-02 | — | SMK | SMK | GREEN (CT-PB-03, CT-SES-02; SMK "Manual selection holds within current slot" on both OSes) |
| BHV-46 | Pause holds within slot | core | CT-PB-04, CT-PB-31 | — | SMK | SMK | GREEN (CT-PB-04, CT-PB-31; SMK "Pause holds within current slot" on both OSes) |
| BHV-47 | Forced refresh replays current slot [QUIRK] | core | CT-PB-27, CT-SES-05 | — | — | — | GREEN (CT-PB-27, CT-SES-05; CI `36111775825`) |
| BHV-48 | Catch-up ±7 days | core | CT-SCH-04, existing "Late wake…" | — | — | — | GREEN (CT-SCH-04, legacy "Late wake…"; TZ-01 keeps the null path byte-identical; CI `36111775825`) |
| BHV-49 | DST, computer-local | core | existing DST checks, CT-SES-07 | — | — | — | GREEN (legacy DST checks and CT-SES-07, unmodified, on the UTC CI runners and the CEST dev box; TZ-01; CI `36111775825`) |
| BHV-50 | Follow-schedule toggle (page + tray in sync) | ui | — | HS-03, HS-04 | MAN | MAN | NATIVE-PENDING (NC-01, NC-17; HS-03/HS-04 on both OSes: the page toggle and the tray item stay in sync; SMK both toggles) |
| BHV-51 | Stations page | ui | — | HS-01 | MAN | MAN | NATIVE-PENDING (NC-01, NC-17 visual pass; HS-01 on both OSes) |
| BHV-52 | Station add/edit validation | ui | — | HS-02 | — | — | GREEN (HS-02 on both OSes: cancel, missing name, invalid URL, add, edit, trimming, automation names; SMK rejects an empty name and an invalid URL) |
| BHV-53 | Station delete (confirm, cleanup, forget) | ui, core | CT-PB-26 | HS-02, HS-03 | — | — | GREEN (CT-PB-26; HS-02, HS-03 on both OSes: confirm text, cleanup of slots, fallback and last station, tray identity) |
| BHV-54 | Settings import/export (absent; OQ-2) | spec | — | — | — | — | GREEN (spec: absent in both front-ends and in `DialShift.App` at `39f5c8a` (no `ImportSettings`, `ExportSettings` or `before-import` in `DialShift.App` or `DialShift.Core`); out of scope per OQ-2) |
| BHV-55 | Schedule page | ui | — | HS-01 | MAN | MAN | NATIVE-PENDING (NC-01, NC-17 visual pass; HS-01 on both OSes (tabs, rows, empty state, the reworded helper text); zone tag and next local start per row in `UiTimeZone` (QA-N6)) |
| BHV-56 | Slot add/edit validation | ui | — | HS-02 | — | — | GREEN (HS-02 on both OSes: invalid time, add, edit keeps the `Id`, presets; the time-zone picker: `UiTimeZone` rows 13/14 and the no-match block) |
| BHV-57 | Slot conflict | core, ui | CT-SCH-07, existing conflict checks | HS-02 | — | — | GREEN (CT-SCH-07, legacy conflict checks, TZ-09/TZ-10; HS-02 and the zone-aware editor wording in `UiTimeZone` on both OSes) |
| BHV-58 | Delete slot (OQ-10) | ui | — | HS-02 | — | — | GREEN (HS-02 on both OSes: delete asks for confirmation (OQ-10), and the question names the slot's zone) |
| BHV-59 | Launch at login (transactional status) | platform, ui | — | HS-11, HS-13 | MAN | MAN | NATIVE-PENDING (NC-04, NC-10; HS-11 on both OSes including the F1/F2 disabled states (D39); HS-13: the checkbox shows the verified OS status and `startup_registration.result` is logged) |
| BHV-60 | Start-in-tray setting | ui | — | HS-06 | — | — | GREEN (HS-06 on both OSes) |
| BHV-61 | Fallback picker | ui, core | CT-PB-36 | HS-02 | — | — | GREEN (CT-PB-36; HS-02 on both OSes: picker options, selection saves, the fallback marker on the row) |
| BHV-62 | About shows the real version | ui | — | HS-13 | — | — | GREEN (HS-13 on both OSes: the assembly version, not 0.1.0) |
| BHV-63 | Open settings folder | platform | — | HS-12 | MAN | MAN | NATIVE-PENDING (NC-01, NC-17; HS-12 on both OSes; F10: a failed reveal shows an error dialog) |
| BHV-64 | Message/confirm dialogs, safe owner | ui | — | HS-07 | MAN | MAN | NATIVE-PENDING (NC-01, NC-17; HS-07/HS-08 on both OSes: owned by the visible window, ownerless and centered otherwise, never its own owner, Enter/Escape) |
| BHV-65 | Accessibility names, keyboard defaults | ui | — | HS-02 | MAN | MAN | NATIVE-PENDING (NC-01, NC-17: visible keyboard focus, Enter/Escape, a screen reader reads the names; HS-02 on both OSes: automation names on every field located by name) |
| BHV-66 | Smoke harness replaced before WPF removal | test | — | — (the headless suites are tracked on their own rows) | SMK | SMK | GREEN (`--smoke-test --recovery-test` 34/34 on `windows-latest` and `macos-latest`, CI `36111775825`; replacement map in §4) |
### 3.2 Brief 1 §11 matrix rows

| ID | Behavior | Owner lane | Unit | Headless integration | Native Windows smoke | Native macOS smoke | Status |
|---|---|---|---|---|---|---|---|
| MX-01 | Settings persistence | core | CT-SET-* | HS-01, HS-02 | SMK | SMK | GREEN (CT-SET-*, TZ QA-N3 round-trips; HS-01, HS-02 on both OSes; SMK "Persistence") |
| MX-02 | Schedule evaluation | core | CT-SCH-*, CT-SES-*, TZ-* | HS-14 | SMK | SMK | GREEN (CT-SCH-*, CT-SES-*, TZ-01..15 unit checks including the TZ-12 unit half (`59c6b7c`, 87 checks: a wake into an Athens slot on a New York computer through both resume paths), HS-14, and the SMK schedule checks, on both OSes, CI `36111775825`. The native half of TZ-12 (a real sleep) is NC-02/NC-08, tracked on TZ-12, QA-N1 and MX-08) |
| MX-03 | Retry and fallback | core | CT-PB-05..14 | HS-17 | SMK (recovery) | SMK (recovery) | GREEN (CT-PB-05..14; HS-17 LV-08/LV-09 on the real LibVLC engine, `windows-latest`; SMK `--recovery-test` on both OSes) |
| MX-04 | Tray/menu actions | ui | Limited: view-model command tests | HS-04 | MAN | MAN | NATIVE-PENDING (NC-01, NC-17; the view-model command tests (`UiViewModels`) and HS-04 on both OSes) |
| MX-05 | Tray menu identity (no crash on edit/refresh) | ui | — | HS-03 | SMK | SMK | GREEN (HS-03 on both OSes; SMK tray identity after every editor operation on both OSes) |
| MX-06 | Second-instance activation | platform | — | CT-SI-*, HS-09 | MAN | MAN | NATIVE-PENDING (NC-06, NC-13, NC-17; CT-SI-*, HS-09 and the SMK second launch green on both OSes; NC-13's LaunchServices half passed on the dev box, 2026-09-25) |
| MX-07 | Launch at login | platform | — | HS-11 (contract test) | MAN | MAN | NATIVE-PENDING (NC-04, NC-10; HS-11 green on both OSes) |
| MX-08 | Sleep/wake recovery | core, platform | CT-PB-29..34 (gap logic) | HS-16 (limited) | MAN | MAN | NATIVE-PENDING (NC-02, NC-08; CT-PB-29..34, HS-16 green) |
| MX-09 | Playback coordinator vs fake engine | core, test | CT-PB-*, CT-SM-* | HS-14 | SMK | SMK | GREEN (CT-PB-*, CT-SM-*; HS-14 with the real coordinator and heartbeat; SMK on both OSes) |
| MX-10 | Windows LibVLC playback | playback | — | HS-17 (Windows: LV-01..LV-11, real LibVLC + coordinator, `adummy` output, local server; no audible check) | SMK + corpus | N/A | NATIVE-PENDING (NC-03, NC-15; HS-17 LV-01..LV-11 green on `windows-latest`, CI `36111775825`; SMK: three live stations Playing on LibVLC) |
| MX-11 | macOS AVPlayer playback | playback | — | HS-17 (limited) | N/A | SMK + corpus | NATIVE-PENDING (NC-11, NC-16; SMK: three live stations Playing on AVPlayer, and the recovery run fails `NetworkUnavailable` on `127.0.0.1:1` without a crash (the HS-17 case, BHV-40); the macOS 26.5 adapter-harness corpus is in `docs/spikes.md`) |
| MX-12 | Native ARM64 macOS playback | playback, release | — | HS-15 (arm64 Mach-O, no libvlc) | N/A | MAN (Apple Silicon gate, SP-01/SP-02) | NATIVE-PENDING (NC-07; HS-15 arm64 Mach-O and no libvlc via `verify-mac-app.sh` in CI; SP-01 green; SMK on `macos-latest` logs `rid=osx-arm64 arch=Arm64 engine=MacAvPlayerPlaybackEngine`, and the bundle smoke passes 32/32 with AVPlayer in the packaged app extracted with `unzip`, CI `36111775825`. On the dev box (2026-09-25, `2c0909e`), the bundle launched through LaunchServices played all three stations live with `MacAvPlayerPlaybackEngine`, 34/34 (NC-17). NC-07's Gatekeeper result is partial, from a Mac that is not clean) |
| MX-13 | Media compatibility corpus | playback | — | Limited | MAN | MAN | NATIVE-PENDING (NC-03, NC-11, NC-15, NC-16; the macOS 26.5 corpus is recorded) |
| MX-14 | Redacted diagnostic logging | platform, core | CT-LOG-01..04 | HS-10 | SMK | SMK | GREEN (CT-LOG-01..04, the `Redaction` suite (D40), HS-10, HS-17 LV-11; SMK logs on both OSes are valid JSON and redacted, see BHV-06) |
| MX-15 | App upgrade/move + launch-at-login recovery | platform | — | HS-11 (stale-path status) | MAN | MAN | NATIVE-PENDING (NC-04, NC-10; HS-11 stale-path checks green on both OSes) |
### 3.3 Definition of done, spikes, packaging, decisions, quality gates

| ID | Item | Owner lane | Verification | Status |
|---|---|---|---|---|
| DOD-01 | One Avalonia UI is the only maintained front-end (WPF and `DialShift.Mac` deleted; `DialShift.slnx` lists Core, Tests, App) | release | Repo tree, `DialShift.slnx`, and a grep for `System.Windows`, `WinForms`, `DialShift.Mac` and `UseWPF` returns nothing | GREEN (re-run at `39f5c8a`: `DialShift/` and `DialShift.Mac/` are gone; `DialShift.slnx` lists Core, Tests and App; no source, project, script or workflow matches, and `System.Windows.Input` appears only as the cross-platform `ICommand` in `ViewModels/RelayCommand.cs` and `Smoke/SmokeUi.cs`. Stale mentions in the project instruction files were removed with QG-01) |
| DOD-02 | No Avalonia/WPF/WinForms/OS/filesystem-location/pipe/process/UI-dispatch reference from `DialShift.Core` | spec | Grep `DialShift.Core/**` for `using Avalonia`, `System.Windows`, `Microsoft.Win32`, `System.IO.Pipes`, `System.Diagnostics.Process`, `Environment.GetFolderPath`, `Dispatcher` returns nothing. `DialShift.Core.csproj` has no `PackageReference`. Re-run at every Core change | GREEN (re-run at `39f5c8a`, after the CF-03/CF-04 change to `SettingsStore` (its new `IClock` parameter is the Core contract): no matches under `DialShift.Core/**`, and no `PackageReference` in `DialShift.Core.csproj`. Its only `ItemGroup` is `InternalsVisibleTo` for the QA-N4 memo check) |
| DOD-03 | Windows and macOS compile, test and publish in CI | release | GitHub Actions on the private remote, matrix `windows-latest` + `macos-latest` | GREEN (CI `36111775825` at `39f5c8a`: both jobs build with `-warnaserror`, test (windows-latest 1619 passed / 16 skipped, macos-latest 1640 / 4, 22/22 suites), run the native smoke (34/34 on both), package, verify the downloadable zips (the macOS one after `ditto` and after `unzip`), and run the bundle smoke of the packaged macOS app (32/32)) |
| DOD-04 | Both packages launch, retain settings, show tray, play/stop/retry, obey schedule, exit cleanly | test | SMK on both OSes plus §9 | NATIVE-PENDING (NC-01, NC-07, NC-17: the downloaded zip on a clean machine, audible output, a real quit from the tray. Automated on both OSes: launch, settings, tray, play/stop/retry and schedule by SMK; a clean exit by HS-04 (`app.exit … clean=true`, exit 0, through the real `App.Run`) and by both smoke runs ending through the quit path with exit 0. The packaged macOS app itself, extracted from the release zip with `unzip`, passes `--smoke-test` 32/32 in CI (the bundle smoke, `dafb74c`, CI `36111775825`), so the smoke no longer covers only the build output. The Windows smoke runs the `win-x64` build output, which `build.ps1` publishes into the zip unchanged) |
| DOD-05 | A second launch activates the existing instance on both OSes | platform | CT-SI-*, HS-09, MAN | NATIVE-PENDING (NC-06, NC-07, NC-17; CT-SI-*, the F3 latch, HS-09 and the SMK second launch green on both OSes) |
| DOD-06 | Launch at login enabled, disabled and verified on both | platform | HS-11 plus MAN NC-04/NC-10 | NATIVE-PENDING (NC-04, NC-10; HS-11 green on both OSes, including the disabled states of D39) |
| DOD-07 | Sleep/wake verified by the manual native checklist | platform | NC-02/NC-08 | NATIVE-PENDING (NC-02, NC-08) |
| DOD-08 | Apple Silicon status published honestly (D2 as amended by D13) | release | README and release notes say "native osx-arm64 (AVPlayer)"; no Intel artifact is produced; the last Intel/Rosetta build is only at tag `legacy-last-known-good` (D13) | GREEN (README at `39f5c8a`: the macOS package is "Native `osx-arm64` (AVPlayer)", "ad-hoc signed, not yet notarized or clean-machine tested"; "There is no Intel Mac build"; tag `legacy-last-known-good` keeps the last Intel Mac build. Artifact label `native-avplayer` (D36); no `osx-x64` (PK-01). There are no release notes yet: the first release must repeat this (release)) |
| DOD-09 | Structured, redacted logs with version, RID/arch, engine, transitions, startup-registration outcome, wake outcome, recoverable failures | platform | HS-10 | GREEN (HS-10: `app.start` with version, RID, arch, engine and data-dir source; HS-13 DOD-09: `startup_registration.result` for the startup check and each write, through the real App; CT-LOG (core): transitions, failures, wake outcomes; CI `36111775825`) |
| DOD-10 | Legacy app removed only after equivalent checks pass (D8) | release | All §4 rows GREEN or NATIVE-PENDING before the deletion commit | GREEN (every legacy check in §4 had a passing replacement on both OSes, CI `36100367406` at `4f22dd0`, before the deletion in `0d0e524`; D8 status note) |
| SP-01 | AVPlayer spike, checkpoint 1 (feasibility): compiles; start, stop, volume; plays the core MP3/AAC/HLS corpus on an M-series Mac | playback | Run on this Apple Silicon dev box. "Audio came out once" is not a pass | GREEN (checkpoint A PASS and the production-adapter corpus on macOS 26.5 arm64, `docs/spikes.md`) |
| SP-02 | AVPlayer spike, checkpoint 2 (reliability): repeated source changes, wake/reconnect, stop-while-connecting, second-instance activation, clean exit, clean-machine `.app` install | playback | Dev box plus NC-07 | NATIVE-PENDING (NC-07, NC-08; checkpoint B PASS; second-instance protocol green (CT-SI-*); SMK source changes, second launch and recovery green on `macos-latest`) |
| SP-03 | Windows `SystemEvents` spike: package resolves in `net10.0` without the `-windows` TFM; subscribe after the message loop; deterministic unsubscribe; timer-gap retained | playback, platform | CI `windows-latest` compile, plus NC-02 runtime | NATIVE-PENDING (NC-02; compile PASS; HS-16 Windows green on `windows-latest`) |
| SP-04 | macOS `NSWorkspace.DidWakeNotification` spike: all five §4.4 criteria (D3) | playback, platform | Dev box plus NC-08 (lid close) | NATIVE-PENDING (NC-08; criteria 1, 3, 4, 5 PASS; HS-16 macOS green) |
| PK-01 | `DialShift.App` RIDs are `win-x64;osx-arm64` and never `osx-x64` (D13) | release | csproj review plus HS-15. The csproj part is verified (`RuntimeIdentifiers` = `win-x64;osx-arm64` at `d83f946`); the HS-15 part is covered by `verify-mac-app.sh` in CI | GREEN (csproj `win-x64;osx-arm64`; at `39f5c8a` the only `osx-x64` in any csproj, script or workflow is the csproj comment "No osx-x64" (D13); `lipo -archs` = arm64 in CI `36111775825`) |
| PK-02 | LibVLC packages only for `win-x64`; the macOS package contains no `libvlc*` dylibs | release, playback | HS-15 (`find … -name 'libvlc*'` is empty) | GREEN (`verify-mac-app.sh` (no `libvlc*`/VLC) and `verify-win-package.ps1` (`libvlc\win-x64` only) in CI `36111775825`) |
| PK-03 | macOS `.app`: `Info.plist` with `CFBundleIdentifier=com.tsiger.dialshift`, `CFBundleDisplayName`, `CFBundleIconFile`, `LSUIElement=true`; exec bit set; arm64 Mach-O slice; ad-hoc signed; the signature survives any extractor (SR-03, D51) | release | `plutil -p`, `lipo -archs`, `codesign -dv` in CI (macos-latest); `verify-mac-app.sh --zip` | GREEN (`verify-mac-app.sh --zip` in CI `36111775825` on the uploaded zip: no `._*`/`__MACOSX` entries, then each bundle, extracted with `ditto` and with `unzip`, passes: plist keys including `LSMinimumSystemVersion` 14.0 (D22) and ATS media-only (D32), exec bit, arm64, only Mach-O files in `Contents/MacOS` (the managed files in `Contents/Resources/app`, D51), no signature in extended attributes, no loose `._*` files, no broken links, `codesign --verify --deep --strict`, ad-hoc signature. The bundle smoke then runs the `unzip` copy, 32/32) |
| PK-04 | Icons: `.ico`, macOS template PNG, `.icns` (D4) | release, ui | Asset presence plus HS-15 | NATIVE-PENDING (NC-01, NC-12. `.icns`: asserted by `verify-mac-app.sh` in CI. The tray `.ico` and `tray.png` are Avalonia resources compiled into `DialShift.dll`, not loose package files, so their presence is shown by loading them: HS-03 creates the App's real tray through `TrayIconOptions.ForCurrentPlatform()` on both OSes (a missing resource throws), and the SMK tray checks run on the built app on both OSes. The 44×44 size is D34; rendering is NC-12 (macOS) and NC-01 (Windows)) |
| PK-05 | Windows artifact = `.zip` of the `win-x64` publish directory (D7) | release | CI artifact | GREEN (CI artifact `DialShift-win-x64`, verified by `verify-win-package.ps1` on the extracted zip in CI `36111775825`) |
| PK-06 | Avalonia 12.1.2 in the App project (D6) | ui, release | csproj; record the actual version here. **Actual: 12.1.2** for `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent` (D19) | GREEN (csproj at `39f5c8a`: `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent` 12.1.2; the test-only `Avalonia.Headless` is pinned to the same 12.1.2. Since `2c0909e` the App and the tests opt in to Avalonia's private APIs (`AvaloniaAccessUnstablePrivateApis`) for the no-display fallback, so any Avalonia version change must pass the D52 re-verification first) |
| PK-07 | README, THIRD-PARTY-NOTICES (conditional LibVLC wording), `data/README` updated | release | Docs review in the same change | GREEN (spec sweep at `39f5c8a`: README verified against the code (commands, exit codes 0–4, `DIALSHIFT_DATA_DIR`, `DIALSHIFT_AUDIO_OUTPUT`, data and log paths, the `.unreadable-*` backup names and the no-copy rule (D50), engine differences, macOS 14 minimum, per-slot time zones and the restart caveat, the test project's packages, the bundle layout, zip and CI steps (D51), signing tiers, the legacy tag); THIRD-PARTY-NOTICES re-checked against the shipped packages, which are unchanged since `0d0e524` (the test-only `Avalonia.Headless` does not ship; the macOS license texts stay in `Contents/Resources/licenses`); `data/README.md` has no stale front-end references) |
| QG-01 | No dead code: grep for stale references after each retirement | spec | Grep list in DOD-01/DOD-02 plus obsolete platform branches | GREEN (on top of `5e9ca34`: after the text was checked again against the scripts, CI, `LaunchOptions.cs` and README, `AGENTS.md` (Architecture, Commands, Settings, Honest labeling), `.claude/skills/release-packaging/SKILL.md` and the release, ui, test, platform and core agent files were updated for D8, D13, D29, D35, D36, D50 and D51. The grep `DialShift\.Mac\|WPF\|WinForms\|osx-x64\|Rosetta\|VideoLAN\.LibVLC\.Mac\|SmokeChecks\|11\.3\.22` over `AGENTS.md`, `CLAUDE.md` and `.claude/` finds only 7 intentional lines: the retirement history (the `AGENTS.md` Architecture line and the release-engineer retirement bullet), the D13 statements (never `osx-x64`: `AGENTS.md` Commands and Honest labeling, the release-engineer artifacts bullet and the skill introduction), and the `AGENTS.md` quality gate that tells agents to grep for WPF/WinForms. Code, projects, scripts, CI, README and `docs/` were already clean at `39f5c8a`; intentional hits in `docs/` are provenance and the tag references) |
| QG-02 | Docs updated in the same change as code (this matrix included) | spec | Per-lane review | GREEN (spec doc sweep at `39f5c8a`: this matrix, `docs/decisions.md` (D37–D51, D24/D30 amendments), `docs/schedule-timezone-research.md` (status and §13 implementation notes; §13.3 now records the TZ-12 unit half from `59c6b7c` and the open native half), README and THIRD-PARTY-NOTICES are current. Code-comment drift found in the sweeps is tracked in §7.10: SR-01 fixed (`dafb74c`), SR-02 waits for NC-02. At `2c0909e` the native-check pass brought this matrix, D52 and the README up to date for NX-01. The "22 suites" drift in `AGENTS.md:29` and `.claude/agents/test-engineer.md:7` (23 suites since `2c0909e`) was fixed in `a54e423` (user-approved): the count was removed from both instruction files) |
| QG-03 | Best UI/UX: consistent theme, clear status, fast tray, no dead-end dialogs | ui, spec | HS-01 screenshots reviewed; §9 native UX pass | NATIVE-PENDING (NC-01, NC-17 native UX pass. Done: the UI review's defects UI-D1 (trimmed pickers), UI-D2 (D44) and UI-D3 (D43) are fixed and checked, accent contrast is 12.7:1 (D45), the compact 780×650 layout is checked headless on both OSes with a negative control, and the spec review of the 12 SMK screenshots from CI `36108959649` (six views on each OS; no UI code changed since) found no clipped or overlapping text) |
| QG-04 | Test-and-fix until prod-ready | all | This matrix fully GREEN or NATIVE-PENDING | GREEN: every §3 row is GREEN or NATIVE-PENDING (62 / 44 / 0), every row in §6, §7.10 and §7.11 is GREEN, NATIVE-PENDING, mitigated or accepted, and CI `36111775825` is green on both OSes. The NATIVE-PENDING rows close with their §9 native checks |
---

## 4. Legacy SmokeChecks coverage map

Every check in WPF `DialShift/SmokeChecks.cs` (now at tag `legacy-last-known-good`) was covered here, and the upstream `EditorSmokeChecks` patterns were adopted, **before** WPF was deleted in `0d0e524` (brief 1 §4.5, §7.7; DOD-10). The **Passing at retirement** column names the native smoke check (`--smoke-test --recovery-test`, 34/34 on `windows-latest` and `macos-latest`, CI `36100367406` at `4f22dd0`) that covered each legacy check. The headless parts of the plan (HS-01..05, HS-14) landed later (`bafffc0`) and are green on both OSes (§7.0). The smoke still runs in every CI run; at `39f5c8a` it is 34/34 on both OSes (CI `36111775825`), and the packaged macOS app passes the bundle smoke, 32/32.

| Legacy check (WPF `SmokeChecks.Run`) | Replacement | Level | Passing at retirement |
|---|---|---|---|
| "Live playback: <station>" for each default station | SMK live playback per station (volume 0), plus the corpus (MX-13) | Native Windows + macOS | SMK "Live playback: Groove Salad / Drone Zone / Secret Agent" (LibVLC and AVPlayer); the corpus is NC-03, NC-11 |
| "Pause stops playback" | CT-PB-20, CT-SM-08; SMK | Unit + native | CT-PB-20, CT-SM-08; SMK "Pause stops playback" |
| "Schedule catch-up selects current station" | CT-PB-01; HS-14 | Unit + headless | CT-PB-01; SMK "Schedule catch-up selects current station" |
| "Pause holds within current slot" | CT-PB-04 | Unit | CT-PB-04; SMK "Pause holds within current slot" |
| "Manual selection holds within current slot" | CT-PB-03 | Unit | CT-PB-03; SMK "Manual selection holds within current slot" |
| "New schedule occurrence overrides manual station" | CT-PB-03 (second half) | Unit | CT-PB-03; SMK "New schedule occurrence overrides manual station" |
| "Resume preserves pause within same slot" | CT-PB-31 | Unit | CT-PB-31; SMK "Resume preserves pause within same slot" |
| Screenshots: stations, schedule, settings, station-editor, schedule-editor, compact (780×650) | HS-01 / HS-02 headless PNGs (D5) | Headless | SMK "Screenshot: …", the same six views on both OSes (native captures; the D5 headless baselines are HS-01/HS-02) |
| "Hide to tray" / "Restore from tray" | HS-05; SMK | Headless + native | SMK "Hide to tray", "Restore from tray" |
| "Persistence" (schedule count, volume 0) | CT-SET-*; HS-02 | Unit + headless | CT-SET-*; SMK "Persistence" |
| `--recovery-test`: "Failed stream retries and plays fallback" (`http://127.0.0.1:1/unavailable`) | CT-PB-05..07; SMK `--recovery-test` with the real engine | Unit + native | CT-PB-05..07; SMK "Failed stream retries and plays fallback" |
| `--recovery-test`: "Pause cancels fallback and retry" | CT-PB-21; SMK | Unit + native | CT-PB-21; SMK "Pause cancels fallback and retry" |
| Upstream `EditorSmokeChecks`: cancel/validation/add/edit/delete-confirm station, invalid-time/add/conflict/edit slot, persistence, **tray menu identity after every edit**, schedule toggle label | HS-02, HS-03 | Headless | SMK "Station editor: cancel …" and "Tray menu identity: …" after each station and slot operation, Volume ±10 and both schedule toggles |

The native smoke also added checks that WPF never had: the window and tray at launch, no error dialog left open, and a real second process activating the first.

---

## 5. Playback coordinator behavior spec

This is the normative companion to `DialShift.Core/Playback/Contracts/IPlaybackCoordinator.cs`. The core lane copies the event-acceptance table (§5.2) into the `PlaybackCoordinator` source header (brief 1 §5.3). Done in `e875c8e`: the spec review found the header consistent with this table, and this table now includes the header's extra row and footnotes.

### 5.1 Timing constants

These are characterized from `RadioController` and must be preserved.

| Constant | Value | Source |
|---|---|---|
| Tick interval | 1 s (`PeriodicTimer` in the orchestration layer) | RC ctor `DispatcherTimer` |
| Retry delay after failure *n* | `n < 3 ? n × 3 s : 30 s`, so 3 s, 6 s, 30 s, 30 s, … | RC:`Fail` |
| Fallback switch | when a retry is due, `failures ≥ 3`, and the fallback is configured, differs from desired, and is not already active | RC:`Tick` |
| Primary re-check while the fallback plays | 120 s after the fallback reached `Playing` | RC:`Tick` |
| Stable-playback failure reset | 60 s continuous `Playing` on the primary only | RC:`Tick` |
| Stall watchdog | failure on the first tick where time outside `Playing` since the last attempt start or last `Playing` is **> 25 s** | RC:`Tick` |
| Wake tick gap | ≥ 15 s on **`IMonotonicClock`**, which must be **sleep-inclusive** in production (D14). **[BUG fix]:** the Mac used `UtcNow` | M:`RadioController.Tick` |
| Wake network settle | 2 s, measured on the monotonic clock and completed by the tick that observes it (OQ-7) | brief 1 §4.4 |
| Wake debounce | 10 s after an accepted wake or a completed recovery (D15, `PlaybackCoordinator.WakeDebounce`) | brief 1 §4.4 |

**Determinism rule:** every timer above is evaluated inside `OnTickAsync` against `IMonotonicClock`. There is no `Task.Delay` in coordinator policy, so tests drive time only by advancing fake clocks and calling `OnTickAsync`. Schedule evaluation uses the local time `TimeZoneInfo.ConvertTime(clock.UtcNow, localZone)` with `Kind` normalized to `Unspecified`, and `localZone` is injected (QA-B2).

**Constructor** (implemented as recommended in `e875c8e`; the coordinator owns and disposes the engine, D17):
`PlaybackCoordinator(Settings settings, IPlaybackEngine engine, IClock clock, IMonotonicClock monotonicClock, IAppLog log, TimeZoneInfo? localZone = null)`

### 5.2 Event acceptance

Rows are inputs, columns are states. `→X` means a transition to state X. `ign` means ignored, with no state change and no engine call.

| Input \ State | Stopped | ScheduledWaiting | Connecting | Playing | Reconnecting | Failed | SuspendedBySystem | Disposing |
|---|---|---|---|---|---|---|---|---|
| UserPlay | →Connecting | →Connecting | →Connecting (supersede) | →Connecting | →Connecting | →Connecting | →Connecting (cancel recovery) | ign |
| UserStop | ign (hold only) | ign (hold only) | →Stopped/SW¹ | →Stopped/SW¹ | →Stopped/SW¹ | →Stopped/SW¹ | →Stopped/SW¹ | ign |
| ScheduleDue (new occurrence, or forced) | →Connecting | →Connecting | →Connecting | →Connecting | →Connecting | →Connecting | deferred to recovery² | ign |
| Engine Playing (current session) | ign | ign | →Playing | ign (refresh) | →Playing | ign | ign | ign |
| Engine Opening/Buffering/Ended/Stopped/Idle (current session) | ign | ign | ign (stall clock already runs from the attempt start) | stays Playing⁵ | ign | ign | ign | ign |
| Engine Failed / EndOfStream / stall (current session) | ign | ign | →Failed | →Failed | →Failed | ign (once per attempt) | ign | ign |
| Any engine event, stale session | ign | ign | ign | ign | ign | ign | ign | ign |
| RetryDue (generation current) | — | — | — | primary re-check → Reconnecting (fallback only) | — | →Reconnecting | — | ign |
| WakeDetected (OS or tick gap)⁴ | schedule check only | schedule check only | →SuspendedBySystem | →SuspendedBySystem | →SuspendedBySystem | →SuspendedBySystem | ign (idempotent) | ign |
| Settle elapsed | — | — | — | — | — | — | schedule check, then exactly one reconnect: a new slot →Connecting, otherwise the desired station →Reconnecting | — |
| SettingsChanged | revalidate | revalidate | revalidate³ | revalidate³ | revalidate³ | revalidate³ | revalidate³ | ign |
| Dispose | →Disposing | →Disposing | →Disposing | →Disposing | →Disposing | →Disposing | →Disposing | ign |

¹ The target is `ScheduledWaiting` when `ScheduleEnabled` is true and a next occurrence exists, otherwise `Stopped`. UserStop always runs `HoldCurrent`.
² ScheduleDue while `SuspendedBySystem` is evaluated as the recovery's schedule check, so exactly one reconnect happens (the double-open fix).
³ If the desired or current station was removed, this behaves like `ForgetStation` (→Stopped/SW). If the active desired station's URL changed, it behaves like UserPlay of that station (so in `SuspendedBySystem` it supersedes the recovery and goes →Connecting). A changed fallback applies to the next due retry, and a changed volume is re-sent.
⁴ In every state, a wake within 10 s of the last accepted wake or of the last completed recovery is ignored (D15), so an OS notification and a tick gap for the same wake give one recovery.
⁵ `IsPlaying=false`, the 60 s stable-playback timer resets, and the 25 s stall clock starts (OQ-6). Status stays `Playing`, and the fallback's primary re-check timer is not reset.

**Serialization and ownership (implemented, D16–D18).** One `SemaphoreSlim(1,1)` state gate, never held across an await. Every attempt, retry, primary re-check, wake recovery, stop and dispose replaces the linked operation CTS and bumps the operation generation. Retry and settle timers and engine callbacks act only for the current generation (and, for callbacks, the current session id). Engine commands go through the FIFO pump of D16. The tick loop and `Settings` mutation stay on the UI thread (D18).

---

## 6. Timezone QA tracker (brief 2)

Phase 2 starts only after Phase 1 builds, tests and publishes green. The Phase 2 UI targets `DialShift.App`, since WPF is retired by then. Verification must follow the machine-independence rule: inject `now` and `localZone`, and use `DateTime.SpecifyKind(…, Unspecified)`. Never rely on the host's zone.

**Status (final status pass, `39f5c8a`):** Phase 2 is implemented (`30cc8b5`, `eef6168`, `5ef803b`, `5b2619b`; brief 2 §13), and the TZ-12 unit half is in (`59c6b7c`). All four BLOCKING items (QA-B1..B4) and all nine non-blocking items are GREEN, except QA-N1, which is NATIVE-PENDING. §9.2: 14 of 15 rows are GREEN; TZ-12 is NATIVE-PENDING (its unit half is green; the native half is NC-02/NC-08). The checks are the `Timezone` suite (`DialShift.Tests/Core/TimezoneTests.cs`: 205 checks on macOS; 200 passed and 1 skipped on Windows) and the `UiTimeZone` suite (`DialShift.Tests/Ui/TimeZoneUiTests.cs`: 118 on macOS; 115 passed and 3 skipped on Windows). Both are machine-independent by construction and run on `windows-latest` and `macos-latest` (CI `36111775825`). A check's name starts with its §9.2 row ("Row n" = TZ-0n; the row 12 checks start with "TZ-12") or its QA id. Guard rails, re-checked at `39f5c8a`: no Windows id is ever offered or written by the app (an untouched save keeps whatever was stored); no comparison with `TimeZoneInfo.Local.Id`; no `InvariantGlobalization` in any project, props or targets file; `Settings.Version` is 1.

### 6.1 QA items

| ID | Severity | Item | Owner lane | Verification method | Status |
|---|---|---|---|---|---|
| QA-B1 | **BLOCKING** | `ResolveZone(null/""/whitespace)` returns `null`, so the zero-conversion path is taken and `TimeZoneInfo.Local` is never used on it | core | TZ-01 (byte-identical `At`); the existing DST checks stay green; code review of `ResolveZone` | GREEN. `Scheduler.TryResolveZone` returns `Local` with a null zone for null/""/whitespace (`Scheduler.cs:51`); the null path (`AddStarts`, `Scheduler.cs:154–162`) uses `now` only, and `TimeZoneInfo.Local` is reached only through `LocalClock.Zone` for zoned slots (`:264`). Checks: "QA-B1 …", TZ-01 (byte-identical including `Kind`, D47), "the null path never touches the conversion memo"; the legacy DST checks unchanged. CI `36111775825` |
| QA-B2 | **BLOCKING** | `localZone` is injected: `now` is normalized with `SpecifyKind(Unspecified)`, only 3-arg `ConvertTime` overloads are used, and it is threaded through `ScheduleSession.HoldCurrent`/`TakeChange` | core | TZ-02, TZ-03, TZ-11 pass with a non-host `localZone`; grep shows no 2-arg `ConvertTime`/`ConvertTimeToUtc(DateTime)` in `Scheduler` | GREEN. Zoned math normalizes to `Unspecified` (`LocalClock.Wall`, `Scheduler.cs:262`) and uses only the 3-argument `ConvertTime` (`:82`, `:164`, `:219`); no 2-argument `ConvertTime`, `ConvertTimeToUtc` or `DateTime.Now` in `Scheduler`/`ScheduleSession`. `localZone` is threaded through `HoldCurrent`/`TakeChange` (`ScheduleSession.cs:28`, `:38`) and every coordinator schedule call (`PlaybackCoordinator.cs:250`, `:268`, `:408`, `:593`). Checks: "QA-B2 …" (four zones for one instant; `Kind` Local/Utc with a non-host zone), TZ-02/03/11. CI `36111775825` |
| QA-B3 | **BLOCKING** | Ambiguous (fall-back) wall time fires once, at the earlier daylight instant: `zoneWall − GetAmbiguousTimeOffsets(zoneWall).Max()` | core, test | TZ-05, a **differing-zone** ambiguous case (Europe/Athens slot, America/New_York computer) | GREEN. `ToUtc`: an ambiguous wall time → `wall − GetAmbiguousTimeOffsets(wall).Max()` (`Scheduler.cs:208`). TZ-05 (Athens slot, New York computer: 20:30, and not plain `ConvertTime`'s 21:30); TZ-11 and the coordinator fall-overlap check fire once. CI `36111775825` |
| QA-B4 | **BLOCKING** | Only IANA ids are stored. The picker maps `GetSystemTimeZones()` through `TryConvertWindowsIdToIanaId`. Resolution is IANA-first with a `TryConvertIanaIdToWindowsId` fallback. There is never a comparison to `TimeZoneInfo.Local.Id` | ui, core | TZ-14 on CI `windows-latest` plus a unit test for the canonicalization helper; grep for `TimeZoneInfo.Local.Id` returns nothing | GREEN. The picker offers only IANA ids (`TimeZoneCatalog.Load`, `TimeZoneCatalog.cs:68–88`; D43); resolution is IANA-first with the `TryConvertIanaIdToWindowsId` fallback (`Scheduler.Find`, `:240–250`); the only `.Local` + `.Id` in the code is `ScheduleSession.ZoneId` (`ScheduleSession.cs:63`), an identity for the computer's own zone, never compared with `entry.TimeZone`. TZ-14 on `windows-latest` (CI `36111775825`); the "QA-B4 …" catalog checks (real list on both OSes, a simulated Windows list on macOS) |
| QA-N1 | non-blocking | The resume path differs by OS (Mac timer gap vs Windows `SystemEvents`) | core, platform | The layered design routes both into one recovery path: CT-PB-29/30 cover both sources. TZ-12 on both OSes (NC-02, NC-08) | NATIVE-PENDING (NC-02, NC-08). Both resume sources feed one recovery: CT-PB-29..33 cover the OS notification, the tick gap and both together, and the TZ-12 unit half (`59c6b7c`) runs every zoned wake scenario through both sources with the same outcome. What is left is the real sleep on each OS |
| QA-N2 | non-blocking | The existing DST tests are already machine-independent | test | Keep them unchanged; run CI on hosts in different zones (the runners' zones differ) | GREEN (the legacy DST checks are unmodified and pass on the UTC CI runners and on the CEST dev box; CI `36111775825`) |
| QA-N3 | non-blocking | Never bump `Settings.Version`. An invalid `TimeZone` string must not reach the `.unreadable-*` path | core | CT-SET-02 (version guard); TZ-08; `"TimeZone": 123` hazard pinned (CT-SET-11) | GREEN. `Settings.Version` = 1 (`Settings.cs:5`); `SettingsStore.Load` does not validate `TimeZone` (`SettingsStore.cs:53–55`); a null zone is not written (`ScheduleEntry.cs:24`). Checks: "QA-N3 …" (version, round-trip, a pre-timezone file), TZ-08 (no `.unreadable-*`), CT-SET-02, CT-SET-11 (the `"TimeZone": 123` hazard, pinned). CI `36111775825` |
| QA-N4 | non-blocking | Per-(zone, localZone, zoneDate, time) memo with day-change invalidation. The real staleness is the OS tzdata snapshot, so a restart is required on a zone change | core | Unit test for memo invalidation across midnight; README restart note (D12) | GREEN. Memo per (zone, local zone, zone wall time) for the current local day, dropped on a day change, bounded at 4096 (`Scheduler.cs:174–193`); the zone cache and the tzdata snapshot are documented (`:21–22`). Checks: "QA-N4 …" (15 entries, stable, dropped at midnight, keyed by local zone, unused by the null path). Restart caveat: README "Changed your computer's time zone?" and the Schedule page helper text |
| QA-N5 | non-blocking | Conflicts normalize the zone (`IsNullOrWhiteSpace → ""`, `Trim`) and compare the **resolved** `zone?.Id`. This is an approximation and is documented | core | TZ-09, TZ-10; `null` vs `" "` rejected; case-variant id rejected | GREEN. `Conflicts` normalizes, resolves and compares resolved ids, unknown = local (`Scheduler.cs:130–136`, `:148`). Checks: TZ-09/TZ-10, `null` vs `" "`, a case variant, unknown vs local, the documented approximation (Athens/Helsinki), and the zone-aware editor wording in `UiTimeZone`. CI `36111775825` |
| QA-N6 | non-blocking | The day tab and "UP NEXT" can disagree with the slot's zone day, so show the zone and the next local fire time on the row, and the zone label in UP NEXT | ui | HS-13 with a cross-zone slot; TZ-07 | GREEN. Each zoned row shows its zone and next local start (`SlotRowViewModel`, `SchedulePageViewModel.cs:24–73`, refreshed every 15 s while visible); UP NEXT adds the slot's own time and zone, with its day when it differs (`UiText.UpNext`, `UiText.cs:52–61`). Checks in `UiTimeZone` on both OSes: "QA-N6 …" (the Kolkata slot on a Pago Pago computer, a disabled row, the compact layout) |
| QA-N7 | non-blocking | A spring gap goes to the first valid instant (same instant as today). Midnight and whole-day gap sub-cases are in the matrix | core, test | TZ-04, TZ-15 | GREEN. A gap goes to the first valid instant, including base-offset gaps `IsInvalidTime` misses (`ToUtc`/`GapEnd`, `Scheduler.cs:195–238`). TZ-04 and TZ-15 (Santiago on both OSes; Apia on macOS, SKIP on Windows, whose zone data lacks Apia 2011) |
| QA-N8 | non-blocking | A zone change can suppress catch-up (`current.At < last.At`). Key the dedup on `Occurrence.Key`/zone-wall identity, or reset `last` on zone change | core | CT-SES-08 flips from "suppressed" (Phase 1 pin) to "fires" once fixed; unit test for zone edit on the current slot | GREEN. Identity is the zone wall-time `Key` (`Occurrence.cs:47–49`); `ScheduleSession.IsNew` (`ScheduleSession.cs:49–56`) implements D48. Checks: "QA-N8 …" (a zone edit fires; an OS zone change does not refire; the next start still fires), CT-SES-08 and CF-02 flipped to "fires". CI `36111775825` |
| QA-N9 | non-blocking | `Occurrence.At` is always a computer-local wall clock; never call `ToLocalTime`/`ToUniversalTime` on it | core | CT-SCH-05 (Kind pin); code comment on `Occurrence`; grep | GREEN. The invariant is in the `Occurrence` doc comment (`Occurrence.cs:19–24`); no `ToLocalTime`/`ToUniversalTime` in Core or App code. Checks: CT-SCH-05, "QA-N9 …" (a zoned `At` is `Unspecified` for every `now.Kind` and is a local wall time), CF-01 (a zoned `Key` is culture-invariant) |
### 6.2 Brief 2 §9.2 test matrix

| ID | Scenario | Setup | Expected | Owner lane | Method | Status |
|---|---|---|---|---|---|---|
| TZ-01 | Null zone | Existing settings, `TimeZone = null` | `At` byte-identical to today, including `Kind` | test (core) | Unit: compare against Phase 1 `Evaluate` output for the same `now` | GREEN ("Row 1 …": `At`, `At.Kind`, `Entry` and `Key` byte-identical to a re-statement of Phase 1 `Evaluate` over 2026, four blank spellings × three `Kind`s; CI `36111775825`) |
| TZ-02 | Zone = computer-local | Slot zone equals the injected `localZone` | Identical fire time | test | Unit, injected `localZone` | GREEN ("Row 2 …": Athens, New York and Kolkata on their own computers, over 2026 including both DST days) |
| TZ-03 | East offset | `08:00 Europe/Athens`, computer = UTC | Fires 06:00 UTC in winter | test | Unit | GREEN ("Row 3 …": 06:00 UTC in winter, 05:00 in summer) |
| TZ-04 | DST spring gap | `02:30` in a zone on its jump day | Fires once at the first valid instant, same instant as today | test | Unit | GREEN ("Row 4 …": Athens and New York gap slots fire once at the first valid instant, the same instant as a local slot) |
| TZ-05 | DST fall overlap, **differing zone** | `02:30 Europe/Athens`, computer = America/New_York | Earlier (daylight) instant (QA-B3) | test | Unit | GREEN ("Row 5 …": Athens 03:30 on 2026-10-25 on a New York computer → Sat 20:30, the earlier instant) |
| TZ-06 | Non-hour offset | `Asia/Kolkata` (+05:30); also Nepal +05:45 | Correct instant | test | Unit | GREEN ("Row 6 …": Kolkata, Kathmandu, Chatham standard and daylight) |
| TZ-07 | Cross-midnight day | `Mon 23:00 UTC`, computer at +2 | Local `Tue 01:00`; the row shows zone + next local fire time | test, ui | Unit + HS-13 | GREEN ("Row 7 …" in `Timezone`; the row's zone and next local start in `UiTimeZone` (QA-N6) on both OSes) |
| TZ-08 | Invalid stored id | `"Bad/Zone"` in `settings.json` | Falls back to local, UI shows "(unknown zone)", log warning, no crash, no `.unreadable-*` | test, ui | Unit (`SettingsStore` + `Evaluate`) + HS | GREEN ("Row 8 …": Unknown with local fallback, one `schedule.zone_unknown` warning per id through the app log, hostile ids, `SettingsStore` round-trip with no `.unreadable-*`; UI: "(unknown zone)" on the row, in UP NEXT and in the editor, in amber) |
| TZ-09 | Conflict, same zone and time | Two `08:00` slots in the same zone | Rejected | test | Unit | GREEN ("Row 9 …" unit and editor checks) |
| TZ-10 | Conflict, different zone, same time | `08:00 UTC` vs `08:00` local (non-UTC `localZone`) | Allowed | test | Unit | GREEN ("Row 10 …" unit and editor checks) |
| TZ-11 | Session dedup across DST | Ambiguous slot | Fires once (needs `localZone` threaded through) | test | Unit | GREEN ("Row 11 …": fall-back on the zone's own and a differing computer, local fall-back, local spring gap; each fires once) |
| TZ-12 | Sleep/resume into a zone-shifted slot | Resume after a zoned slot started | Reconnects the current slot, verified on **macOS (timer gap) and Windows (SystemEvents)** | test, platform | CT-PB-32 variant (unit) + NC-02/NC-08 (native) | NATIVE-PENDING (NC-02, NC-08). The unit half is GREEN (`59c6b7c`; 87 "TZ-12 …" checks in `Timezone`, on both OSes, CI `36111775825`): a New York computer (injected) with a local slot and a `Europe/Athens` slot sleeps across the Athens slot's start and wakes through each of three paths: `NotifyWakeAsync` (the OS notification), a tick gap of exactly 15 s, and a tick gap of the whole sleep. Each time exactly one start follows, of the Athens slot, with `schedule.fired` naming `<time> Europe/Athens` (and `wake.recovery` = `schedule_slot_started` when it was playing), and a minute later nothing fires again. Scenarios: a September day; the Athens fall-back day (and an earlier wake that reconnects the local slot only); the Athens spring-forward day (and an earlier wake that fires nothing); a slot in the Athens spring gap (first valid instant); a slot in the Athens fall overlap (the earlier instant, once); a wake while paused in the preceding local slot (the new Athens slot still plays, once); and a user stop inside the zoned slot, which stays stopped with no engine call, also across the recurring fall-overlap wall time. The native half is NC-02 (Windows `SystemEvents`) and NC-08 (macOS lid close) |
| TZ-13 | UI round-trip | Set zone, save, reload | Persists | ui | HS-02 variant | GREEN ("Row 13 …" in `UiTimeZone` on both OSes: choose by keyboard, save, a fresh `SettingsStore` load returns `Europe/Athens`, reopen shows it; also through the real App's tray (HS-03)) |
| TZ-14 | Mac→Windows migration | `settings.json` with an IANA id opened on Windows | Id survives, picker shows it, no silent rewrite (QA-B4) | ui, test | HS on CI `windows-latest` | GREEN ("Row 14 …" on `windows-latest`, CI `36111775825`: stored ids open on the stored value and an untouched save keeps them byte-identical, including an unknown id; plus the simulated Windows-id list checks (QA-B4)) |
| TZ-15 | Midnight / whole-day gap | Santiago/Beirut/Cuba-style 00:00→01:00 gap; `Pacific/Apia` 2011-12-30 | Fires on the correct shifted day (QA-N7) | test | Unit | GREEN ("Row 15 …": the Santiago midnight gap on both OSes; the Apia whole-day gap on macOS. `windows-latest` reports it SKIP: Windows time-zone data lacks Apia's 2011 date-line change, a data limit, not a code path) |
---

## 7. Characterization-test plan

The test lane writes these **before code moves** (brief 1 §12 step 2), in `DialShift.Tests`: console checks, `Check(name, condition)`, non-zero exit on failure, no framework.

"Mirrors quirk" means the expectation copies current behavior even though it is odd. **Fix** means the expectation deliberately differs from current behavior, with the reason given.

### 7.0 Implementation status

"Implemented" means written and passing both locally and in CI. At `39f5c8a`, `dotnet run --project DialShift.Tests -c Release` reports **1640 passed, 4 skipped, 22/22 suites** on macOS arm64. CI run `36111775825` at the same commit reports windows-latest **1619 passed / 16 skipped** and macos-latest **1640 / 4**, 22/22 suites on both. The skips are checks that need the other OS or its data: Windows skips the Unix socket, `chmod` and read-only-folder checks (including the CF-04 read-only folder), the macOS HS-11/12/16 halves, the macOS picker-list and `zone.tab` checks, the HS-16 real `PowerModes.Resume` (NC-02), and the Apia 2011 case (its zone data lacks it). macOS skips the Windows halves of HS-11/12/16 and HS-17 LV-01..LV-11. DOD-03 is green, so every "Implemented" row below is CI evidence. `2c0909e` adds a 23rd suite, `RenderTimerFallback`, with 16 checks (RT-01..RT-07, D52; RT-06 probes the real CoreVideo call on macOS and is skipped elsewhere). Locally it gives **1656 passed, 4 skipped, 23/23 suites**. It has not yet run in CI, so it is not listed per suite below.

Per suite (passed / skipped), macOS · Windows, CI `36111775825`: Scheduler 33 · 33, ScheduleSession 25 · 25, Timezone 205 · 200 / 1, SettingsStore 35 · 31 / 1, RetryPolicy 23 · 23, PlaybackCoordinator 188 · 188, PlaybackStateMachine 116 · 116, PlaybackProperty 6 · 6, PlaybackRace 3 · 3, UiViewModels 141 · 140 / 1, HeadlessUi 230 · 230, UiTimeZone 118 · 115 / 3, Fakes 36 · 36, SingleInstance 170 · 154 / 4, FileAppLog 69 · 66 / 1, Redaction 96 · 96, AppPaths 19 · 21, StartupRegistration 80 / 1 · 32 / 2, FileReveal 19 / 1 · 16 / 1, MonotonicClock 7 · 7, PowerEvents 13 / 1 · 7 / 2, LibVlcEngine 8 / 1 · 74 (LV-01..LV-11 run on Windows only). Since `923e62e`: Timezone +87 (TZ-12), SettingsStore +12 on macOS and +8 / 1 on Windows (CF-03, CF-04).

| IDs | Status | Location |
|---|---|---|
| CT-SCH-01..08 | Implemented; CT-SCH-02 flipped to the culture-invariant `Key` (CF-01); CT-SCH-06 no longer rejects `08.00` (D53) | `DialShift.Tests/Core/SchedulerTests.cs` |
| CT-SCH-09..11 (D53) | Implemented, green locally (not yet in CI): `HH.mm` parses, fires and conflicts like `HH:mm`; every host culture's `HH:mm` text parses back; `FormatTime` is invariant under da-DK, fi-FI and a synthetic `.` culture | `DialShift.Tests/Core/SchedulerTests.cs` |
| CT-SES-01..06, CT-SES-08 | Implemented; CT-SES-08 flipped to "fires" (QA-N8, D48) | `DialShift.Tests/Core/ScheduleSessionTests.cs` |
| CT-SES-07 | Implemented as the legacy DST dedup checks, kept unmodified | `DialShift.Tests/Core/ScheduleSessionTests.cs` |
| CT-SET-01..10 | Implemented | `DialShift.Tests/Core/SettingsStoreTests.cs` |
| CF-03, CF-04 (the fixes, D50) | Implemented (`ec09114`): "CF-03 Warning is cleared by a later successful Load" replaces the `[quirk]` pin; CF-04: same-millisecond recoveries (`-2`, a taken `-3` skipped, `-4`), no free backup name (`Load` does not throw, `Save` throws `IOException` and leaves the original untouched, then preserves it first once a name is free), and a read-only folder (Unix only; Windows skips it) | `DialShift.Tests/Core/SettingsStoreTests.cs` |
| CT-SET-11 | Implemented: `"TimeZone": 123` takes the recovery path, pinned as a documented hazard (QA-N3 note) | `DialShift.Tests/Core/TimezoneTests.cs` |
| CT-SET-12 (D53) | Implemented, green locally (not yet in CI) | `DialShift.Tests/Core/SettingsStoreTests.cs` |
| §7.1 harness fakes | Implemented, with self-tests | `DialShift.Tests/Fakes/` |
| CT-PB-01..40, plus the adversarial checks (ADV-*) and CT-LOG (core) | Implemented (`7ea767d`) | `DialShift.Tests/Core/PlaybackCoordinatorTests.cs`, `CoordinatorRig.cs` |
| CT-SM-01..22, including the property (seed 42) and race suites | Implemented (`7ea767d`) | `DialShift.Tests/Core/PlaybackStateMachineTests.cs` |
| RetryPolicy (§5.1) | Implemented | `DialShift.Tests/Core/RetryPolicyTests.cs` |
| CT-SI-01..13 (CT-SI-03 = HS-09, process level through a child test process), SI-D1, stale-socket recovery, the platform review fixes F3/F5/F6/F7 | Implemented (`a0f5f8f`, `895e141`, `26763f4`, `2470fee`) | `DialShift.Tests/App/SingleInstanceTests.cs` |
| CT-LOG-01..04, LOG-D1 (two processes), §8.2.7 never-throws | Implemented (`a0f5f8f`, `895e141`) | `DialShift.Tests/App/FileAppLogTests.cs` |
| Redaction (the F4 leak-shape table, D40) | Implemented (`bf8f882`) | `DialShift.Tests/App/RedactionTests.cs` |
| HS-01..08, HS-13, HS-14 | Implemented (`bafffc0`): the real views and dialogs on Avalonia's headless platform (Skia), located by accessible name; the App's startup and quit sequence through `App.Run`; the real `PlaybackHost` heartbeat | `DialShift.Tests/Ui/` (`HeadlessUiTests.cs`, `AppLifecycleTests.cs`, `PlaybackLoopTests.cs`, `ViewModelTests.cs`, `AppHarness.cs`, `UiRig.cs`) |
| HS-10 (`app.start` fields and redaction on disk), HS-18 | Implemented | `DialShift.Tests/App/FileAppLogTests.cs`, `AppPathsTests.cs` |
| HS-11 (including the F1/F2 disabled states, D39), HS-12, HS-16 (including F8, nothing raised after `Dispose` returns) (the macOS half runs on macOS, the Windows half on `windows-latest`), §8.2.8 clocks | Implemented. The F10 reveal-failure dialog is checked in `UiViewModels` | `DialShift.Tests/Platform/`, `DialShift.Tests/Ui/ViewModelTests.cs` |
| HS-15 | Implemented as the package verifiers: `scripts/verify-mac-app.sh` and `verify-win-package.ps1` run in CI on the downloadable zips (arm64-only Mach-O, no VLC on macOS, `libvlc\win-x64` only, Info.plist keys, `.icns`, ad-hoc signature, x64 GUI PE). Since `dafb74c` (SR-03, D51) the macOS verifier also rejects non-Mach-O files in `Contents/MacOS`, signatures in extended attributes, loose `._*` files and broken links, and with `--zip` rejects `._*`/`__MACOSX` entries and verifies the bundle extracted with `ditto` and with `unzip`. CI then runs the bundle smoke (`--smoke-test`, 32/32) on the `unzip` copy. The tray `.ico` and `tray.png` are compiled-in resources, loaded by HS-03 on both OSes (PK-04) | `scripts/`, `.github/workflows/ci.yml` |
| HS-17 | Windows: LV-01..LV-11 pass on `windows-latest` (CI `36111775825`), including LV-08 in its final wording (D42). macOS: the smoke's recovery run is the HS-17 case with the real AVPlayer engine (§3 note). The real engines were also exercised against the full corpus by the out-of-repo playback harness (`docs/spikes.md`, "Adapter (production)" column) | `DialShift.Tests/App/LibVlcEngineTests.cs`, `DialShift.Tests/TestServers/LocalMediaServer.cs` |
| SMK (`--smoke-test --recovery-test`, BHV-66) | Implemented (`4f22dd0`); 34/34 on `windows-latest` and `macos-latest` in CI `36111775825`. The bundle smoke (`--smoke-test` without the recovery checks, on the packaged `osx-arm64` app extracted with `unzip`; `dafb74c`) passes 32/32 there | `DialShift.App/Smoke/`, `.github/workflows/ci.yml` |
| TZ-01..11, TZ-13..15, every QA item | Implemented (`eef6168`, `5ef803b`, `5b2619b`) | `DialShift.Tests/Core/TimezoneTests.cs`, `DialShift.Tests/Ui/TimeZoneUiTests.cs` |
| TZ-12 (unit half) | Implemented (`59c6b7c`): 87 checks, a CT-PB-32/CT-PB-31 zoned variant through both wake sources (§6.2). The native half is NC-02/NC-08 | `DialShift.Tests/Core/TimezoneTests.cs` |

Known test hazards (HZ-07, HZ-08) are tracked in §7.11.

Characterization surprises found while writing these are tracked in §7.10.

### 7.1 Test harness

The test lane builds this against the D10 contracts:

| Component | Behavior |
|---|---|
| `FakeClock : IClock` | `UtcNow` is settable, plus `Advance(TimeSpan)`. |
| `FakeMonotonicClock : IMonotonicClock` | `GetTimestamp()` returns a settable tick counter. `GetElapsedTime(a, b) = TimeSpan.FromTicks(b − a)`. |
| `FakePlaybackEngine : IPlaybackEngine, ITrackMetadataProvider` | Session ids follow the contract counter. It records `(sessionId, source, volume)` for every start, plus stop calls and volume calls. It exposes `RaiseState(sessionId, state)`, `RaiseFailed(sessionId, kind)` and `Title`, and can hold a start incomplete via a `TaskCompletionSource` (for the stop-while-connecting races). |
| `Step(n)` | Advances both clocks by 1 s and awaits `coordinator.OnTickAsync(ct)`, *n* times. |
| `localZone` | Always injected as UTC, or `TimeZoneInfo.CreateCustomTimeZone` for fixed offsets. `now` is built with `DateTimeKind.Unspecified`. |

### 7.2 Scheduler: gaps in the existing `DialShift.Tests/Program.cs` checks

The existing 26 checks stay as they are.

| ID | Given / When / Then | Mirrors quirk? |
|---|---|---|
| CT-SCH-01 | Given two enabled entries at the same instant (added directly, bypassing `Conflicts`). When `Evaluate` runs. Then `Current`/`Next` pick the smaller `Entry.Id`. | Yes (deterministic tiebreak) |
| CT-SCH-02 | Given an occurrence. Then `Key == $"{Id}:yyyy-MM-ddTHH:mm"`, invariant-formatted. | Pin |
| CT-SCH-03 | Given an entry with empty `Days`. Then it never appears as `Current` or `Next`. | Pin |
| CT-SCH-04 | Given a single weekly Monday 08:00 slot and now Monday 07:59. Then `Current.At` is the previous Monday 08:00 (6 days 23 h 59 min back). | Pin |
| CT-SCH-05 | Given `now` with `Kind=Local` and then `Kind=Unspecified`. Then the null-path `At.Kind` equals `now.Kind` (QA-N9). | Pin |
| CT-SCH-06 | `TryTime` rejects `"08:00:00"`, `" 08:00"`, `"08:00 "`, `"08:60"`, `""`, `"24:00"`, `"8:00"` and null, and accepts `"23:59"`. (`"08.00"` was rejected until D53.) | Pin |
| CT-SCH-07 | `Conflicts`: a disabled candidate never conflicts; a disabled existing entry is ignored; the same day at a different time does not conflict. | Pin |
| CT-SCH-08 | `Evaluate` ignores `ScheduleEnabled`: it still returns occurrences when the flag is off, because gating is in `ScheduleSession`. | Pin |
| CT-SCH-09 | *(D53)* `TryTime` accepts `"08.30"` (as 08:30), `"00.00"` and `"23.59"`, and rejects `"8.30"`, `"08.3"`, `"08.60"`, `"24.00"`, padding, `","` and `"-"` separators and seconds. For every culture of the host, `ToString("HH:mm", culture)` parses back. `Evaluate` fires a slot stored as `"08.30"` at 08:30, and `NextFor` sees it. | Pin (the D53 fix) |
| CT-SCH-10 | *(D53)* `Conflicts` compares parsed times: `"08:30"` and `"08.30"` on the same day conflict in either order; `"08.30"` and `"08:31"` do not; a time that does not parse never conflicts. | Pin (the D53 fix) |
| CT-SCH-11 | *(D53)* `Scheduler.FormatTime` writes invariant `HH:mm` (`08:30`, `00:00`, seconds dropped), also with `CurrentCulture` set to da-DK, fi-FI or a synthetic culture whose time separator is `.`, and `TryTime` reads it back. | Pin (the D53 fix) |

### 7.3 ScheduleSession dedup

| ID | Given / When / Then | Mirrors quirk? |
|---|---|---|
| CT-SES-01 | Given `ScheduleEnabled=false`. When `TakeChange` runs. Then it returns null and does not mutate state; after re-enabling, the same occurrence fires. | Pin |
| CT-SES-02 | Given `HoldCurrent` at Monday 10:00 (slot A 09:00). When `TakeChange` runs at 10:05. Then null. When it runs at 11:00 (slot B). Then B. | Pin (manual/pause hold) |
| CT-SES-03 | Given `last` = the Monday 11:00 occurrence. When `HoldCurrent` runs with a clock at Monday 10:30 (current 09:00). Then `last` is unchanged (`current.At >= last.At` guard), and `TakeChange` at 10:31 returns null. | Yes |
| CT-SES-04 | Given a fresh session with no current occurrence. When `HoldCurrent` and then `TakeChange` run after the first slot starts. Then the first slot fires. | Pin |
| CT-SES-05 | Given an occurrence already taken. When `TakeChange(force:true)` runs. Then the same occurrence is returned again. | Yes (intended replay, BHV-47) |
| CT-SES-06 | Given an occurrence taken, then all slots disabled (the next `TakeChange` resets `last`), then re-enabled. Then the same occurrence fires again. | Yes |
| CT-SES-07 | The existing DST dedup checks (spring/fall) stay green, unmodified. | Pin |
| CT-SES-08 | Given `last` = today's 10:00 occurrence, and the slot edited to 09:30 while now is 10:30. When `TakeChange(force:false)` runs. Then null (suppressed). When `force:true` runs. Then 09:30. | Phase 1 pin of QA-N8, flipped in Phase 2 (D48): without `force` the edited 09:30 occurrence now fires, once. |

### 7.4 SettingsStore

| ID | Given / When / Then | Mirrors quirk? |
|---|---|---|
| CT-SET-01 | Missing file: `Load` returns defaults, `Warning == null`, and no file is created. | Pin |
| CT-SET-02 | `"Version": 2`: preserved as `.unreadable-*`, defaults loaded, warning set (QA-N3 guard). | Pin |
| CT-SET-03 | Unknown extra properties (`"TimeZone":"Europe/Athens"` on an entry, `"Future":1` at the root) load fine, which is forward compatibility for older builds. | Pin |
| CT-SET-04 | `"Volume":150` loads as 100; `-5` loads as 0. | Pin |
| CT-SET-05 | A station with an `ftp://` URL goes to `.unreadable-*` plus defaults. | Pin |
| CT-SET-06 | A schedule entry with `"Days": null` goes to `.unreadable-*`. | Pin |
| CT-SET-07 | `Settings.Defaults()`: 3 https SomaFM stations, Volume 60, `ScheduleEnabled=false`, `Fallback=null`, `LaunchAtLogin=false`, `StartInTray=false`, empty schedule. | Pin |
| CT-SET-08 | Empty `Stations` and `Schedule` arrays (the user deleted everything) load as-is with no recovery and no warning, and do not reset to defaults. | Pin |
| CT-SET-09 | `Save` into a non-existent directory creates it and leaves no `.tmp`. | Pin |
| CT-SET-10 | The backup file name matches `settings.json.unreadable-\d{17}`. When that name is already taken, the next free `-N` suffix is used (`-2`, `-3`, …, up to 100 names; the CF-04 checks), and an existing backup is never overwritten (`FileMode.CreateNew`). | Pin (the `-N` suffix is the CF-04 fix, D50) |
| CT-SET-11 | *(Phase 2)* An entry with `"TimeZone": 123` behaves as documented in the QA-N3 hazard note. Phase 2 decides whether it is tolerated; it must be pinned either way. | Pinned in Phase 2: it takes the recovery path (`.unreadable-*` plus defaults), a documented hazard, not fixed |
| CT-SET-12 | *(D53)* A stored `"08.30"` loads as `"08:30"`; `"21:05"` and an unparseable `"8.30"` load unchanged, with no warning and no backup. `Load` does not rewrite the file; the next `Save` persists `"08:30"`. | Pin (the D53 fix) |

### 7.5 Playback coordinator: retry, fallback, stall, schedule, wake

Expected values come from `RadioController` (legacy `DialShift.Mac`, now `DialShift.App/Services/RadioController.cs`) and upstream's `DialShift.Desktop.Tests`. The shared setup is the harness from §7.1, defaults stations A/B/C, `localZone = UTC`, and now Monday 2026-09-14 10:00.

| ID | Given / When / Then | Mirrors quirk? |
|---|---|---|
| CT-PB-01 | Given the schedule on, slot A Mon 09:00, slot B Mon 11:00. When `StartScheduleAsync`. Then there is one engine start for A at volume `Settings.Volume/100.0`; the snapshot is `Connecting`, Desired=A, `IsActive`, "Connecting…"/"Opening the live stream". | Pin |
| CT-PB-02 | When the engine raises `Playing` for the current session. Then `Playing`, `IsPlaying`, "Live broadcast". | Pin |
| CT-PB-03 | Given A playing inside slot A. When `PlayAsync(C)` and `Step(60)`. Then Desired stays C. When the clock passes 11:00. Then Desired=B. | Pin (manual hold; legacy SmokeChecks) |
| CT-PB-04 | Given A playing. When `StopAsync` and `Step(5)`. Then no engine start and status `ScheduledWaiting`. When 11:00 arrives. Then B starts. | Pin (pause holds within slot only) |
| CT-PB-05 | Given the schedule off and A playing. When the engine fails (#1). Then `Failed`, `RetryInSeconds=3`, "Stream unavailable · retry in 3s", track "Your next scheduled change will still run.", engine stopped. `Step(2)` gives no start; `Step(1)` starts A (`Reconnecting`). Failure #2 waits 6 s; #3 waits 30 s. | Pin |
| CT-PB-06 | During a 3 s backoff, the status reads "retry in 3s", then "2s", then "1s" on successive ticks. | Pin |
| CT-PB-07 | Given `FallbackStationId=B` and Desired=A. After failures #1–#3 and 30 s. Then the engine starts B; `IsFallback`; Desired still A; "Connecting to fallback…". On `Playing`: "Live · fallback station". | Pin |
| CT-PB-08 | With no fallback, failures #3, #4 and #5 each wait 30 s and retry A. There is no terminal state. | Pin (never gives up) |
| CT-PB-09 | With `FallbackStationId == Desired`, it behaves as if there were no fallback. | Pin |
| CT-PB-10 | Given the fallback B `Playing`. After `Step(119)` no start; after `Step(1)` the engine starts A, `IsFallback=false`, `Reconnecting`. | Pin |
| CT-PB-11 | Given the primary re-check (CT-PB-10). When A fails. Then after 30 s, B starts (alternation). | Yes |
| CT-PB-12 | Given the fallback B opening. When B fails. Then after 30 s, A starts. | Yes |
| CT-PB-13 | Given two failures, then A plays. A failure after 59 s of playing gives a 30 s delay (failures=3). Repeating with a failure after 60 s or more gives a 3 s delay (failures reset). | Pin |
| CT-PB-14 | On the fallback, 60 s or more of playing does not reset failures. | Pin |
| CT-PB-15 | Given a start that never reaches `Playing`. `Step(25)` gives no failure. `Step(1)` produces a failure (`Stalled`), with a 3 s retry. | Pin (`> 25` strict) |
| CT-PB-16 | Given `Playing`, then the engine reports `Buffering`. More than 25 s outside `Playing` produces a failure. | **Fix/normalization:** measured from `Buffering` rather than the last LibVLC `TimeChanged` (OQ-6) |
| CT-PB-17 | `Failed(EndOfStream)` is handled exactly like an error. | Pin (`EndReached → Fail`) |
| CT-PB-18 | Two `Failed` events for one session count as one failure (a single `failures++`). | Pin (`failurePending`) |
| CT-PB-19 | Start A (s1), then `PlayAsync(B)` (s2). `Playing` and `Failed` for s1 are ignored, and the snapshot still follows s2. | Pin |
| CT-PB-20 | Given `Failed` with a retry pending. When `StopAsync` and `Step(40)`. Then no engine start. | Pin (legacy "Pause stops playback") |
| CT-PB-21 | Given the fallback playing or waiting on the primary re-check. When `StopAsync` and `Step(200)`. Then no start. | Pin (legacy "Pause cancels fallback and retry") |
| CT-PB-22 | Given a retry start held incomplete by the fake. When `StopAsync`, then the fake completes the start and raises `Playing` for it. Then the state stays stopped, the engine was stopped, and nothing plays. | **Fix:** the §5.4 generation rule. There is no equivalent guard today beyond `IsActive`. |
| CT-PB-23 | `SetVolumeAsync(200)` gives `Settings.Volume=100` and engine 1.0. `SetVolumeAsync(-5)` gives 0 and engine 0.0. The next start receives the current volume. | Pin |
| CT-PB-24 | `ToggleAsync` when inactive picks Desired, else `LastStationId`, else the first station. With no stations it is a no-op with no exception. | Pin |
| CT-PB-25 | `NextStationAsync` wraps from C to A, and with no desired or current station plays A. | Pin |
| CT-PB-26 | `ForgetStationAsync(desired)` stops playback and clears Desired and Current. Later ticks do not start it. | **Merge:** the local dialog paused before `ForgetStation`; upstream moved the pause into `ForgetStation`. |
| CT-PB-27 | Given the schedule on, slot A current, and the user stopped. When `RefreshScheduleAsync`. Then A starts. | Yes (BHV-47) |
| CT-PB-28 | With the schedule off, `StartScheduleAsync` and 3 h of ticks never start the engine. | Pin |
| CT-PB-29 | Given A playing. When `NotifyWakeAsync`. Then `SuspendedBySystem`. After `Step(2)` there is exactly one start of A, and failures are reset. | **Fix:** the §4.4 settle delay. Today the reconnect is immediate. |
| CT-PB-30 | Advancing the monotonic clock by 20 s between two ticks triggers the wake path. Advancing `UtcNow` by 1 h with only 1 s of monotonic time does **not** trigger a wake, although the schedule sees the new wall time. | **Fix:** the Mac used `UtcNow` |
| CT-PB-31 | Given A stopped by the user within slot A. When wake occurs. Then it stays stopped. | Pin (legacy "Resume preserves pause within same slot") |
| CT-PB-32 | Given A playing and slot B starting during sleep. When wake occurs. Then **one** start, of B. | **Fix:** the double-open quirk |
| CT-PB-33 | `NotifyWakeAsync` plus a 20 s tick gap within the same recovery window give one recovery. A second `NotifyWakeAsync` while `SuspendedBySystem` is ignored. | New (idempotent) |
| CT-PB-34 | A reconnect after wake that fails falls back to the normal policy (a 3 s retry). | New |
| CT-PB-35 | After `DisposeAsync`: ticks, commands and engine events never start the engine; the engine is disposed; the status is `Disposing`. | Pin (upstream "Disposal prevents further playback") |
| CT-PB-36 | Given A active. When A's URL changes and `NotifySettingsChangedAsync` runs. Then a start with the new URL (manual hold applied). When the fallback changes. Then no immediate start. | Pin (`StationDialog` behavior) |
| CT-PB-37 | The metadata title "Artist – Song" becomes `TrackText`. A null or blank title shows the station tag. An engine without the provider shows the tag. | Pin |
| CT-PB-38 | `StopAsync` with the schedule on gives "Paused · resumes at the next scheduled change"; with it off, "Paused". Both have track "Press play to return to the live broadcast." | Pin |
| CT-PB-39 | `Snapshot.Next` equals `Scheduler.Evaluate(settings, localNow, localZone).Next` when the schedule is on, and is null when it is off. | Pin |
| CT-PB-40 | Rapid `PlayAsync(A)`, `PlayAsync(B)` and `PlayAsync(C)` with the fake's starts held: only C's session is accepted, and at most one session is ever un-stopped. | New (invariant) |

### 7.6 State transitions

These are table-driven over §5.2.

| ID | Given / When / Then | Mirrors quirk? |
|---|---|---|
| CT-SM-01 | Initially `Stopped`. After `StartScheduleAsync` with the schedule on and no current slot but a next one: `ScheduledWaiting`. With no slots at all: `Stopped`. | New mapping |
| CT-SM-02 | `Stopped` + UserPlay → `Connecting` | — |
| CT-SM-03 | `Connecting` + Playing → `Playing` | — |
| CT-SM-04 | `Connecting` + error → `Failed` | — |
| CT-SM-05 | `Failed` + RetryDue → `Reconnecting` | — |
| CT-SM-06 | `Reconnecting` + Playing → `Playing` | — |
| CT-SM-07 | `Playing` + error → `Failed` | — |
| CT-SM-08 | Each active state + UserStop → `Stopped` or `ScheduledWaiting` | — |
| CT-SM-09 | `Stopped` + current-session or stale error → unchanged; no engine call | — |
| CT-SM-10 | `Failed` + second error → unchanged; failures incremented once | — |
| CT-SM-11 | Active state + Wake → `SuspendedBySystem` → (settle) → `Reconnecting` | — |
| CT-SM-12 | `SuspendedBySystem` + Wake → ignored | — |
| CT-SM-13 | `SuspendedBySystem` + engine error → ignored | — |
| CT-SM-14 | `SuspendedBySystem` + UserStop → `Stopped`/`ScheduledWaiting`; the recovery never reconnects | — |
| CT-SM-15 | `SuspendedBySystem` + UserPlay → `Connecting` for the chosen station; the recovery is superseded | — |
| CT-SM-16 | `ScheduledWaiting` + ScheduleDue → `Connecting` | — |
| CT-SM-17 | Any state + Dispose → `Disposing`; every later input is ignored | — |
| CT-SM-18 | `Playing` (fallback) + primary re-check → `Reconnecting` | — |
| CT-SM-19 | A snapshot instance received earlier never changes (the record is immutable; stored references are compared) | — |
| CT-SM-20 | `SnapshotChanged` is not raised by a tick that changes nothing | **Change:** today `Changed` fires every tick |
| CT-SM-21 | Invariant, property-style: fixed seed 42, 2 000 random commands, ticks and engine events. Un-stopped engine sessions ≤ 1 at every step, and no start follows a UserStop until the next UserPlay or ScheduleDue. | New |
| CT-SM-22 | Race: UserStop, current-session error, WakeDetected and RetryDue issued concurrently with `Task.WhenAll`, repeated 200 times. The final state is `Stopped`/`ScheduledWaiting`, and no engine start happens after the stop. | New (§5.5) |

### 7.7 Second-instance protocol

These are headless integration tests against `ISingleInstanceService` (§8.2.4). Each test uses a unique temp data dir, so each gets a unique pipe name. Current behavior is **replaced**, not mirrored: today any Mac connection means activation, and WPF uses a named event.

| ID | Given / When / Then |
|---|---|
| CT-SI-01 | Service 1 `TryStartPrimary()` returns `Primary`. Service 2 with the same data dir returns `AlreadyRunning`. |
| CT-SI-02 | Service 2 `ActivateExistingAsync()` returns `Activated`, and service 1 raises `ActivationRequested` exactly once. |
| CT-SI-03 | Process level: start the app twice with the same `DIALSHIFT_DATA_DIR`. The second process exits 0 within 3 s, and the first process's log contains `single_instance.activated`. The first process must remain running. This proves the second **activates** the first rather than merely exiting. |
| CT-SI-04 | Raw client sends `not json\n`: reply `rejected`, no activation, and the next valid `activate` succeeds. |
| CT-SI-05 | Raw client sends 5 000 bytes without a newline: reply `rejected`. The server reads at most `MaxMessageBytes + 1`, then serves the next client. |
| CT-SI-06 | `{"version":2,"command":"activate"}`, `{"version":1,"command":"quit"}` and `{"version":1,"command":"activate","x":1}` are each `rejected`. |
| CT-SI-07 | A client connects and sends nothing: the server closes it after the 2 s read timeout, and the next client is served. |
| CT-SI-08 | Lock held but no server: `ActivateExistingAsync` returns `NoResponse` within the 1 s connect timeout plus 500 ms. |
| CT-SI-09 | The pipe name is already served by a foreign server: `TryStartPrimary` returns `Failed`, the lock file is released (a new `TryStartPrimary` can take it), and `single_instance.server_failed` is logged. |
| CT-SI-10 | The pipe name is deterministic for the same data dir, differs for a different one, and matches `^DialShift-[0-9a-f]{16}$`. |
| CT-SI-11 | `DisposeAsync` releases the lock, and a new primary can start in the same process. |
| CT-SI-12 | macOS: record the socket file path and mode under `$TMPDIR`. Assert it is not group- or world-writable under the runner's umask. The production-launch validation is NC-13. |
| CT-SI-13 | A client disconnecting mid-message or mid-reply does not stop the listener: the next client is served. |

### 7.8 Logging

| ID | Given / When / Then |
|---|---|
| CT-LOG-01 | The redactor turns `https://user:pass@radio.example.com:8443/live/stream.mp3?token=abc#x` into `https://radio.example.com:8443/…`. The output contains none of `user`, `pass`, `token=abc`, `/live/stream.mp3`. |
| CT-LOG-02 | Free-text redaction replaces every `http(s)://…` substring inside a message or exception text. The full leak-shape table (review F4: glued, apostrophe, whitespace-split, scheme-less, JSON-escaped, IPv6, percent-encoded, uppercase, other schemes, several URLs, exception chains, detached tokens) is `RedactionTests`, including a real-log check of README's "never contains stream credentials". |
| CT-LOG-03 | Each line is valid JSON with `ts`, `level`, `event`, `msg`, and `ex` when present. |
| CT-LOG-04 | When the file exceeds 1 MiB, it rotates to `dialshift.log.1` (replacing the old one), and logging continues. |

### 7.9 Headless integration list

HS tests run in CI on both OSes and use `DIALSHIFT_DATA_DIR` and fake engines.

| ID | Scope |
|---|---|
| HS-01 | Boot with an empty data dir. Render the Stations, Schedule and Settings pages plus the compact 780×650 size to PNG (D5). Assert defaults are visible. |
| HS-02 | Editor smoke, ported from upstream `EditorSmokeChecks` and located via automation names. Station: cancel, missing name, invalid URL, add, edit, delete-confirm. Slot: invalid time, add, conflict, edit, delete. Settings are persisted to disk. Fallback picker. |
| HS-03 | After every HS-02 operation and every schedule toggle: `ReferenceEquals(tray.Menu, originalMenu)`. Items reflect current stations. The "Follow schedule" state matches the setting. |
| HS-04 | Every tray item and main-window command routes to the correct `IPlaybackCoordinator` call (fake coordinator), and a save follows. |
| HS-05 | Close → hidden and still running. Minimize → hidden. `ShowWindow` restores and activates. The hide button hides. |
| HS-06 | `--tray` and `StartInTray=true` each start with the window never shown. |
| HS-07 | A corrupt `settings.json` produces the recovery dialog via a fake `IDialogService` and a `.unreadable-*` file. A save failure (read-only dir) produces the "Save failed" dialog. |
| HS-08 | Forced startup exception: `app.startup_failed` logged, dialog shown, non-zero exit code, lock released. |
| HS-09 | CT-SI-03, the process-level activation. |
| HS-10 | Run a session with a station whose URL contains credentials and a token. The log file never contains them. `app.start` includes version, RID, `ProcessArchitecture` and the engine name. |
| HS-11 | `IStartupRegistration` contract. On Windows, a test registry subkey injected through a constructor seam. On macOS, a temp LaunchAgents dir injected the same way. Covers enable→status enabled, disable→status disabled, a stale executable path→`IsEnabled=false` with a diagnostic, and a write failure→status carries a diagnostic with no incorrect "enabled". |
| HS-12 | `IFileRevealService` builds `ProcessStartInfo` with `ArgumentList` only (a process-launcher seam). Paths with spaces, quotes and `;`/`&` are not interpolated. Folder → `open <dir>` / `explorer.exe <dir>`; file → `open -R <file>` / `explorer.exe /select,<file>`. |
| HS-13 | The view model renders snapshot fields: status upper-cased, title fallback, Play/Pause label, volume label, UP NEXT/SCHEDULE texts, LOCAL TIME footer, tray tooltip truncated to 63, About version. |
| HS-14 | A real `PeriodicTimer` loop plus the coordinator plus a fake engine: the schedule fires within 2 s of a slot boundary. Quit cancels the loop and disposes the coordinator. |
| HS-15 | Publish checks. `osx-arm64`: `lipo -archs` equals `arm64`, no `libvlc*`, `Info.plist` keys (PK-03), template icon present, only Mach-O files in `Contents/MacOS` and no signature in extended attributes (SR-03, D51); the zip has no `._*` entries and the bundle verifies after both `ditto` and `unzip` extraction; the `unzip`-extracted app passes `--smoke-test` (the bundle smoke). `win-x64`: `libvlc.dll` present, `.ico` present. `DialShift.App.csproj` has no `osx-x64`. |
| HS-16 | Power events contract: `Start()` is idempotent, `Dispose()` unsubscribes, no `Resumed` after dispose (fake source). Windows: `SystemEvents` compile-level check on `net10.0`. |
| HS-17 | Real engine against `http://127.0.0.1:1/unavailable`: `Failed` with `NetworkUnavailable` or `Unknown`, no crash, and `StopAsync`/`DisposeAsync` are clean. Runs where the engine loads (CI). **Windows (`LibVlcEngineTests`, `windows-latest`):** LV-01..LV-11 drive the real `LibVlcPlaybackEngine` behind the real `PlaybackCoordinator`, using LibVLC's `adummy` output (`DIALSHIFT_AUDIO_OUTPUT=dummy` through `PlaybackEngineFactory`) and an in-process HTTP/ICY server (`DialShift.Tests/TestServers/LocalMediaServer.cs`): LV-01 factory resolution; LV-02 a WAV stream plays; LV-03 volume 0/40 while playing; LV-04 ICY StreamTitle → track line; LV-05 stop releases the connection; LV-06 8 rounds of 20 rapid switches end Playing with exactly one open connection; LV-07 stop-while-connecting at coordinator and engine level; LV-08 transport cases and failure kinds (redirect, user-info Basic auth, PLS, M3U play; a station on the user-info path without credentials either plays with the credentials LibVLC kept, sent preemptively by 3.0.23.1, or fails `HttpError`, and the check names which; no request to another path ever carries an Authorization header; 404/403/500 and a second realm's 401 → `HttpError`; HTML → `UnsupportedFormat`; refused and `.invalid` → `NetworkUnavailable`; `ftp://` → `InvalidUrl`; a stream that ends → `Ended` + `EndOfStream`; a silent server → `Stalled` by the 25 s watchdog on a stepped clock); LV-09 retry countdown 3 s then 6 s; LV-10 dispose while playing; LV-11 log redaction. The native volume level cannot be read back on `adummy`, so audible output and mute stay in NC-03. **macOS:** the smoke's `--recovery-test` run on `macos-latest` covers the base case with the real AVPlayer engine (§3 note). |
| HS-18 | `AppPaths.Resolve` order: env override, then smoke temp, then OS default. A relative override is rejected. The macOS default is exactly `~/Library/Application Support/DialShift`. |

### 7.10 Characterization and review findings (tracked)

Findings from the Phase 1 characterization suites (`CF-*`), from the spec review of the core lane (`CR-*`), from the platform-lane verification (SI-D1, LOG-D1), from the final spec sweep (SR-*), and from the native checks in §9 (NX-*). The `[quirk]` checks named below pin the current behavior until the listed fix lands, and flip in the same change as the fix.

| ID | Severity | Finding | Recommendation | Owner lane | Phase | Status |
|---|---|---|---|---|---|---|
| CF-01 | low | `Occurrence.Key` is culture-dependent: it formats `At` with the current culture, so `:` becomes the culture's time separator, and a non-Gregorian calendar changes `yyyy`. Harmless in-process today (keys are never persisted and the culture does not change mid-session), but not the invariant format CT-SCH-02 specifies. Pinned by `[quirk] CT-SCH-02 Key follows CurrentCulture's time separator`. | Format with `CultureInfo.InvariantCulture` (`yyyy-MM-dd'T'HH':'mm`) in Phase 2, since `Scheduler` and `Occurrence` are changed then anyway. No migration is needed because keys are in-memory only. | core | 2 | GREEN. Fixed in `30cc8b5`: `Occurrence.Key` is formatted with `CultureInfo.InvariantCulture` (`Occurrence.cs:47–49`). The pin flipped to "CT-SCH-02 Key is culture-invariant (CF-01)", plus a zoned-key check under th-TH |
| CF-02 | non-blocking | A backward wall-clock jump suppresses older slots until real time passes the last fired slot (`current.At < last.At` in `ScheduleSession.TakeChange`). This is the same root cause as QA-N8. Pinned by `[quirk] Backward clock jump: Tue 09:00 slot is suppressed`. | Apply the Phase 2 fix from brief 2 §7.8: key the dedup on `Occurrence.Key`/zone-wall identity, or reset `last` on a zone change. The fix must keep the intended guard (`[quirk] Backward clock jump: Mon 12:00 does not replay last week's Wed slot` stays), and flips the Tue 09:00 check together with CT-SES-08 (QA-N8). | core | 2 | GREEN. Fixed in `30cc8b5` with QA-N8 (D48). "CF-02 fixed: Tue 09:00 fires when reached"; "Backward clock jump: Mon 12:00 does not replay last week's Wed slot" still holds |
| CF-03 | low | `SettingsStore.Warning` is never cleared: a later successful `Load` on the same store keeps the old warning. No user impact today, because the App loads once per process. Pinned by `[quirk] Warning is never cleared by a later successful Load on the same store`. | Reset `Warning = null` at the start of `Load`. Batch it with the Phase 2 `SettingsStore` change for QA-N3/CT-SET-11. | core | 2 | GREEN. Fixed in `ec09114`: `Load` resets `Warning` (and the pending-backup flag of CF-04) at its start (`SettingsStore.cs:44–45`). The pin flipped to "CF-03 Warning is cleared by a later successful Load on the same store" (`SettingsStoreTests.cs:193`), green on both OSes, CI `36111775825` |
| CF-04 | low | If a `settings.json.unreadable-<yyyyMMddHHmmssfff>` backup with the same millisecond name already exists, `File.Copy` throws `IOException` from inside the recovery `catch`, so it escapes `Load` (see the BHV-03 quirk). | In Phase 1 the ui/platform lanes' startup-failure path (BHV-04, HS-08) turns the escape into a dialog and a log instead of a crash. The Core fix in Phase 2 is a collision-free backup name (retry with a suffix) plus a test. | core (fix); ui, platform (Phase 1 mitigation) | 2 (fix), 1 (mitigation) | GREEN. Fixed in `ec09114` (D50): `TryPreserve` creates the backup with `FileMode.CreateNew` under `settings.json.unreadable-<yyyyMMddHHmmssfff>`, then `-2`, `-3`, … up to 100 names, so a copy is never overwritten; the copy is flushed, and a partial copy is deleted. It never throws: any failure (disk full, read-only folder, permissions, every name taken) leaves `Load` returning defaults with a warning that says no copy exists, and the store remembers it. The next `Save` retries the copy first and throws `IOException` while it still fails, which `SettingsService` logs as `settings.save_failed` and shows as "Save failed", so the unreadable original is only ever replaced once a flushed copy of it exists. The timestamp comes from an injected `IClock`, so the checks force the collision. Checks: "CF-04 …" in `SettingsStore` (same-millisecond `-2`/`-4`, no free name, read-only folder on Unix), green on both OSes, CI `36111775825`. The Phase 1 mitigation (the startup-failure path) is no longer needed for this case |
| CR-01 | low (latent) | `PlaybackCoordinator.Open` increments `startsIssued` and sets `currentSessionId` (`PlaybackCoordinator.cs:310`) **before** it builds the `StreamSource`/`Uri` and enqueues the start (`:314`). An exception in between would permanently shift the "N-th start is session N" mapping (D16), so every later session's events would be dropped as stale. More generally, a transition that throws after `NewOperation()` leaves an active attempt with `currentSessionId == 0`, which the stall watchdog (`:360`) never fails, so the coordinator would stay in `Connecting` until the next user or schedule action. This is unreachable today because `SettingsStore.ValidUrl` and `new Uri` agree. | Build the command first, then assign the id and enqueue, so that nothing can throw between the increment and the enqueue. | core | 1 | GREEN. Fixed in `af816ad`: `Open` validates the URL and builds the `EngineCommand` before it commits `startsIssued`/`currentSessionId` (`PlaybackCoordinator.cs:326–332`), and fault recovery never leaves `Connecting` stuck. Covered by ADV-09 (synchronous and faulted `StartAsync`) and ADV-16 (invalid URL never reaches the engine) |
| CR-02 | low | `DisposeAsync` awaits the engine's `StopAsync` completion (`PlaybackCoordinator.cs:767`) with no bound, so an adapter whose stop hangs blocks quit forever. | The App's quit path (BHV-11) awaits coordinator disposal with a timeout (about 3 s), then logs `app.exit` with a timeout flag and continues the teardown. | ui | 1 | GREEN. Fixed in `4ef9515`. HS-14 on both OSes: a coordinator whose dispose hangs cannot block quit (exit 0 within the bound, `app.quit.dispose_timeout`, `app.exit … clean=False`, the rest of the teardown still runs) |
| CR-03 | nit | `playback.state` logs `session={startsIssued}`, the last issued id, rather than the current session. In Stopped, Failed and Suspended it names the previous attempt. | Keep it, and document it as "last issued session" in §8.2.7, or log `currentSessionId`. | core | 1 | GREEN. Fixed in `af816ad`: `playback.state` logs `session={currentSessionId}` (`PlaybackCoordinator.cs:613`), which is 0 when no session is live |
| SI-D1 | medium | On Unix, all server instances of a pipe share one ref-counted listening socket. When the handled instance was disposed before the next was created, the socket closed, and a second launch that connected in that gap was dropped (no activation, exit 2). | Keep a listening instance armed at all times; bound concurrent handlers (D24). | platform | 1 | GREEN. Fixed in `e3ceaa2`, and a default check since `895e141`: "SI-D1 an activation arriving while the previous connection is torn down is still served (5 of 5)", plus CT-SI-12 re-bind mode checks |
| LOG-D1 | medium | Two processes (the primary and a second launch) appending to `dialshift.log` overwrote each other's lines, because .NET `FileMode.Append` is not `O_APPEND`, and two rotations could race and lose `dialshift.log.1`. | A cross-process lock file around the size check, rotate and append, with a bounded wait (D25). | platform | 1 | GREEN. Fixed in `e3ceaa2`, and a default check since `895e141`: LOG-D1 two-process tests (4 000 lines, none lost or torn; per-process order kept across rotations) |
| SR-01 | nit | Code and script comments cite D2 where D13 is the decision: `DialShift.App/DialShift.App.csproj:7` ("Release artifacts (decision D2)") and `:16` ("Ship only the x64 VLC runtime … (D2)"), `scripts/verify-win-package.ps1:3`/`:35`, `scripts/verify-mac-app.sh:3`/`:64`, `scripts/build.ps1:2`, `scripts/build-mac-app.sh:3`, `.github/workflows/ci.yml:1`/`:32`. D2 still holds (Apple Silicon only), but the RID set and "no `osx-x64`" are D13. Found in the final spec sweep (F11) | Cite D13 (or "D2 as amended by D13") in those comments | release | — | GREEN. Fixed in `dafb74c`: the csproj cites D13 (`RuntimeIdentifiers`, VLC `win-x64` only), `build.ps1` and `verify-win-package.ps1:35` cite D13, and `verify-win-package.ps1:3`, `verify-mac-app.sh`, `build-mac-app.sh` and `ci.yml:1` cite "D2 as amended by D13"; `ci.yml` (the `macos-latest` runner line) cites D13 |
| SR-02 | nit | `DialShift.App/Platform/Windows/WindowsPowerEvents.cs:16–17` says `Resumed` "is raised on the `SystemEvents` thread". With `Start()` on the STA UI thread it is most likely the UI thread (D49) | Correct the comment once NC-02 has recorded the thread | platform | — | NATIVE-PENDING (NC-02). The only open step is the native observation: NC-02 records the thread `Resumed` arrives on, then the platform lane corrects the comment to match (D49) |
| SR-03 | low | The macOS zip (`ditto -c -k --keepParent`) stores 206 AppleDouble `._*` entries in `Contents/MacOS/`. They carry the `com.apple.cs.*` code-signature extended attributes of the managed `.dll` files, because codesign signs non-Mach-O files in `Contents/MacOS` as code and keeps their signatures in extended attributes. Finder, Safari and `ditto -x` restore them. A tool that does not (`unzip`, many third-party archivers) leaves loose `._*` files and unsigned DLLs, so the ad-hoc signature no longer verifies and Gatekeeper may call the app damaged. CI extracts with `ditto` and cannot see this. Found in the final spec sweep | Add the non-Apple extraction to NC-07 (done). Before a public release, decide between keeping only Mach-O files in `Contents/MacOS` (as far as the .NET host allows) and shipping a notarized `.dmg`, and verify a copy extracted without `ditto` | release | — | GREEN. Fixed in `dafb74c` by the bundle structure (D51): `Contents/MacOS` holds only Mach-O code (the apphost, the runtime and native dylibs, `createdump`) plus a `DialShift.dll` symlink; the managed `.dll` and `.json` files live in `Contents/Resources/app`, with symlinks back to the Mach-O files (the .NET host uses the symlink target folder as its application folder). codesign now seals the managed files as resources, so no file carries `com.apple.cs.*` attributes, and the zip is written with `ditto -c -k --norsrc --noextattr --noacl --keepParent`: 0 `._*` entries (was 243). `verify-mac-app.sh --zip` rejects `._*`/`__MACOSX` entries and verifies the bundle extracted with `ditto` and with `unzip`; its bundle checks reject non-Mach-O files in `Contents/MacOS`, signatures in extended attributes, loose `._*` files and broken links (negative cases checked). Dev box (macOS 26.5.2, Apple Silicon): the `unzip` copy passes `codesign --verify --deep --strict` and `--smoke-test` 32/32. **CI `36111775825` at `39f5c8a`:** the `--zip` verification passes on the uploaded zip ("no AppleDouble entries; the bundle verifies after ditto and after unzip extraction"), and the bundle smoke of the `unzip`-extracted app passes 32/32 with `MacAvPlayerPlaybackEngine`. The Finder/Safari and Gatekeeper side stays with NC-07 |
| NX-01 | high (crash) | **Startup crash with no active display.** Found by NC-17 on 2026-09-25. The bundled app was launched through LaunchServices while the displays were asleep, and it crashed at startup with `System.InvalidOperationException: Avalonia.Native was not able to start the RenderTimer. Native error code is: -6661` (`kCVReturnInvalidDisplay`). Avalonia.Native 12.1.2 registers a `CVDisplayLink` while the platform initializes, and that registration fails when no display is active. The same crash would hit a login or restart with the screens off, a closed lid with no external display, and a headless Mac mini. The tray, the schedule and playback never started. | Probe `CVDisplayLinkCreateWithActiveCGDisplays` before Avalonia.Native initializes. When the probe fails, initialize on a `SleepLoopRenderTimer` render loop and log `app.render_timer_fallback` (D52) | platform | — | NATIVE-PENDING (NC-17 step 10, NC-08 step 5), only for the confirmation with the displays genuinely asleep. **Fixed in `2c0909e` (D52).** The fix is `DialShift.App/Interop/CoreVideo.cs` (`ProbeDisplayLink`), `DialShift.App/Platform/MacOS/MacRenderTimerFallback.cs` (`UseRenderTimerFallback`, the `AvaloniaLocator` scope swap) and `Program.cs` (macOS only). It relies on the `AvaloniaAccessUnstablePrivateApis` opt-in, with AVA3001 downgraded to a message (`DialShift.App.csproj:20–25`). Evidence: the `RenderTimerFallback` suite, 16 checks, green locally (RT-01 with a display nothing changes; RT-02 without one the native timer never starts, the fallback loop is bound and there is one warning naming -6661; RT-03 other errors; RT-04 a failure restores the resolver; RT-07 the timer idles when stopped). The orchestrator also forced the probe to -6661 against the real Avalonia.Native in the bundle: the smoke passed 32/32, and idle CPU was 0.05 s per 30 s, the same as the normal path. The fix has not yet been seen to work with the displays really asleep, because they woke before the rebuild |

### 7.11 Integration hazards (tracked)

These are risks found by the test lane during integration, and by the spec review of the integration merge. Each one is mitigated, accepted with a reason, or open with an owner.

| ID | Severity | Hazard | Disposition | Owner lane | Status |
|---|---|---|---|---|---|
| HZ-01 | high (if it happened) | **A used engine never plays.** If an engine that was already started (for example resolved from the container elsewhere) were handed to the coordinator, its session ids would not match the coordinator's N-th-start mapping (D16). Every event would be discarded as stale and nothing would ever play. | **Mitigated by D23:** the single-use `PlaybackEngineFactory` is the only source, `IPlaybackEngine` is never registered, and a second `Create()` throws. The "fresh engine" rule is in the `IPlaybackEngine` contract remarks. | playback | Mitigated |
| HZ-02 | medium | **Shutdown hang.** An adapter whose stop never completes would block quit forever. | **Mitigated by CR-02:** quit is bounded (3 s for playback, 2 s for single instance, 2 s for the provider). | ui | Mitigated and tested (HS-14 CR-02 on both OSes) |
| HZ-03 | low | **Duplicate snapshot on a synchronous `StartAsync` throw.** When an adapter throws synchronously from `StartAsync`, the failure reaches the coordinator through the pump's faulted-task path, and subscribers can see a duplicate `SnapshotChanged` for that transition. | **Accepted.** The contract forbids throwing for stream problems, both adapters report failures through `Failed`, and snapshot consumers are idempotent (the view models render the latest value). | core | Accepted |
| HZ-04 | low | **A Windows `Resume` that arrives more than 10 s late causes a second reconnect.** If the tick gap has already detected the wake and the recovery completed more than 10 s (D15) before `SystemEvents` delivers `PowerModes.Resume`, a second recovery runs. That is one extra reconnect after a 2 s settle. | **Accepted.** The cost is a brief audio restart. NC-02 records the observed delay between wake and `Resume`; revisit D15 if it is routinely more than 10 s. | platform | Accepted |
| HZ-05 | medium | **A blocking `StartAsync`.** The coordinator's pump invokes `StartAsync` synchronously, usually on the UI thread, and issues the next command (typically the superseding stop) only after it returns. | **Contract rule added** to `IPlaybackEngine` ("StartAsync returns promptly"). Both adapters comply by design: AVPlayer posts to the main queue, and LibVLC creates the player after the previous one is released on a pool thread. | playback | Mitigated (contract) |
| HZ-06 | low | **About 100–200 ms activation gap at startup.** `Program.Main` starts the single-instance listener before Avalonia, but `App.Run` subscribes to `ActivationRequested` only when the desktop lifetime starts. A second launch in that window is acknowledged (it exits 0), but nothing shows the window. That matters only when the first instance starts with `--tray`. | **Fixed by D37 (F3).** Originally accepted. The service now acknowledges an early `activate`, keeps one pending activation and replays it to the first subscriber (`single_instance.activation_replayed`), so the window is shown. | ui, platform | Fixed (D37, F3 checks in `SingleInstance`) |
| HZ-07 | low | **CT-LOG (core) fails when the checkout path contains `/private`.** The sentinel list includes `"/private"`, and the `playback.engine_error` entry carries an exception stack trace with build-time source paths. A checkout under `/private/tmp` or `/private/var/folders` (the macOS temp dirs) therefore fails with "no coordinator log entry contains …". Reproduced at `e5d70e2`; passes with `-p:PathMap=<repo>=/src` and in the normal checkout. | Use sentinels that cannot occur in a path (for example `/private-alpha`), or assert only on `msg` and on the exception message rather than the full stack text. | test | Fixed in `bafffc0` ("CT-LOG (core) HZ-07 the secret sentinels cannot occur in this checkout's paths") |
| HZ-08 | low | **Flaky macOS clock check on hosted runners.** "§8.2.8 mac CLOCK_MONOTONIC: GetElapsedTime over a 200 ms sleep is 200 ms ±50 ms" failed in CI run `36086991425` (`b09dd03`; 738 passed, 1 failed) because the runner overslept, then passed on the rerun at `e5d70e2`. | Assert a lower bound (≥ 200 ms), and keep the relative check against `Stopwatch` over the same interval, which already exists. Drop the absolute upper bound. | test | Fixed in `bafffc0` (median of 5 samples within [0.5×, 3×], plus the rate check against `Stopwatch`) |
---

## 8. Contracts

### 8.1 Core contracts

These are defined in `DialShift.Core/Playback/Contracts/`, namespace `DialShift.Core.Playback`. They are the approved contract-first surface, and their XML docs are normative.

| File | Contents |
|---|---|
| `IPlaybackEngine.cs` | Adapters must tolerate overlapping calls (stop-while-connecting, D16). `StreamSource(Uri Url, string DisplayName)`; `PlaybackEngineState {Idle, Opening, Buffering, Playing, Ended, Stopped}`; `PlaybackFailureKind {NetworkUnavailable, HttpError, TlsFailure, UnsupportedFormat, InvalidUrl, Stalled, EndOfStream, Unknown}`; `PlaybackEngineStateChangedEventArgs(long SessionId, PlaybackEngineState State)`; `PlaybackEngineFailedEventArgs(long SessionId, PlaybackFailureKind Kind, string? Diagnostic = null)`; `IPlaybackEngine : IAsyncDisposable` with `StateChanged`, `Failed`, `StartAsync(StreamSource, double volume, CancellationToken)`, `StopAsync(CancellationToken)`, `SetVolumeAsync(double, CancellationToken)`. The session-id counter rule, the "successful start = `StateChanged(Playing)`" rule, the volume 0.0–1.0 rule (`Settings.Volume / 100.0`, 0 mutes), the threading rules, "StartAsync returns promptly" (HZ-05) and the fresh-engine ownership rule (D17, D23) are documented on the interface. |
| `ITrackMetadataProvider.cs` | Optional capability: `string? CurrentTitle { get; }`, `event EventHandler? MetadataChanged`. Per D26, it is implemented by `LibVlcPlaybackEngine` only (titles for `http://` streams only) and not by `MacAvPlayerPlaybackEngine`. |
| `IClock.cs` | `IClock { DateTimeOffset UtcNow { get; } }` and `SystemClock.Instance`. |
| `IMonotonicClock.cs` | `IMonotonicClock { long GetTimestamp(); TimeSpan GetElapsedTime(long, long); }` and `StopwatchMonotonicClock.Instance`, the Core default for tests. Production injects a per-OS **sleep-inclusive** clock (§8.2.8, D14), because `Stopwatch` on macOS stops during sleep. |
| `IAppLog.cs` | `IAppLog { Info(eventName, message); Warn(eventName, message, ex?); Error(eventName, message, ex?) }` with the redaction rule, and `NullAppLog.Instance`. |
| `PlaybackStatus.cs` | The `PlaybackStatus` enum (8 states, brief 1 §5.1) and the `PlaybackSnapshot` record: `Status`, `DesiredStationId/Name`, `CurrentStationId/Name`, `IsActive`, `IsPlaying`, `IsFallback`, `StatusText`, `TrackText`, `RetryInSeconds`, `Next`, `NextStationName`, `Volume`; plus `PlaybackSnapshot.Initial(volume)`. |
| `IPlaybackCoordinator.cs` | `Snapshot`, `SnapshotChanged`, `PlayAsync(Guid)`, `ToggleAsync()`, `StopAsync()`, `NextStationAsync()`, `SetVolumeAsync(int)`, `StartScheduleAsync()`, `RefreshScheduleAsync()`, `OnTickAsync(CancellationToken)`, `NotifyWakeAsync()`, `NotifySettingsChangedAsync()`, `ForgetStationAsync(Guid)`, and `IAsyncDisposable`. The input→state mapping is in its remarks and in §5.2. Its remarks also carry the 10 s wake debounce (D15), engine ownership (D17) and the UI-thread rule for the tick loop and `Settings` mutation (D18). |

**Engine-selection rule** (composition root only): Windows gets `LibVlcPlaybackEngine`, macOS gets `MacAvPlayerPlaybackEngine`, and anything else fails with a clear startup error. `DialShift.App` never selects LibVLC on macOS. Implemented in `4ef9515` as the single-use `PlaybackEngineFactory` (D23, `Services/PlaybackServices.cs`). `AddDialShiftPlayback` registers only the factory, the coordinator registration is the only caller of `Create()`, and a second call throws.

### 8.2 App and platform contracts (specification)

The platform lane creates these in `DialShift.App`. The signatures are normative. §8.2.1–§8.2.3 are verbatim from brief 1 §4.2. The platform contracts use the namespace `DialShift.App.Platform`, with the files in `Platform/Abstractions/` (D20).

#### 8.2.1 `IStartupRegistration`

Location: `DialShift.App/Platform/Abstractions/IStartupRegistration.cs`, namespace `DialShift.App.Platform`.

```csharp
public sealed record StartupRegistrationStatus(
    bool IsEnabled,
    string? DiagnosticMessage = null);

public interface IStartupRegistration
{
    Task<StartupRegistrationStatus> GetStatusAsync(CancellationToken ct = default);
    Task<StartupRegistrationStatus> SetEnabledAsync(bool enabled, CancellationToken ct = default);
}
```

**Semantics (both implementations)**

- The status reflects the **OS state**, not `Settings.LaunchAtLogin`.
- `IsEnabled` is true only if the entry exists **and** targets the current executable or bundle with `--tray`.
- An entry that points elsewhere (the app was moved or upgraded) gives `IsEnabled=false` with the diagnostic "Launch at login points to an older copy of DialShift. Turn it on again to fix it." This covers MX-15.
- `SetEnabledAsync` writes, reads back and returns the verified status. It never throws for OS failures; the failure is returned in `DiagnosticMessage`.
- `Settings.LaunchAtLogin` is updated from the **returned** status, then saved.
- The outcome is logged as `startup_registration.result`.

**Windows**

- Key `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value name `DialShift`, data `"\"<Environment.ProcessPath>\" --tray"`.
- Task Manager's Startup apps switch: `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run`, `REG_BINARY` value `DialShift`. The format is undocumented; observed: byte 0 with the low bit set (`0x03`, `0x07`) is disabled, clear (`0x02`, `0x06`) is enabled, and a missing value is enabled. A disabled value gives `IsEnabled=false` with "Turned off in Task Manager's Startup apps. Turn it on again here to fix it."; a value that isn't a non-empty `REG_BINARY` is not enabled either. `SetEnabledAsync(true)` replaces a disabled or unreadable value with `02 00 00 00 00 00 00 00 00 00 00 00` (what Task Manager writes on re-enable), then verifies. `SetEnabledAsync(false)` removes both values. Confirmed natively by NC-04.

**macOS**

- File `~/Library/LaunchAgents/com.tsiger.dialshift.plist`. The label `com.tsiger.dialshift` is kept so existing users' entries are recognized.
- Contents: `ProgramArguments` = `["/usr/bin/open", "-a", "<bundle path>", "--args", "--tray"]` when running inside a `.app`, otherwise `[<exe>, "--tray"]` (development runs). Also `RunAtLoad=true` and `ProcessType=Interactive`.
- The plist is produced by `XmlWriter`, so every string (including a bundle path with `&`, `<` or quotes) is escaped by the writer. The file is written to a temp path and moved into place atomically.
- **No `launchctl bootstrap` or `bootout`**, because it would launch a second instance immediately or could kill the running one (OQ-4). The entry takes effect at the next login.
- Disabled states are never reported as enabled. A plist with `Disabled=true` gives the diagnostic "The launch-at-login entry is marked as disabled. Turn it on again to fix it." launchd's override is read with `/bin/launchctl print-disabled gui/<uid>` (read-only, `ArgumentList`, 5 s timeout): `"com.tsiger.dialshift" => disabled` (or legacy `=> true`) gives "macOS has launch at login turned off for DialShift. If macOS Login Items shows DialShift as not allowed, enable it there. Then turn it on again here." If that check fails (tool error, non-zero exit, timeout, unrecognized output) the status is **not enabled** with "DialShift couldn't check with macOS whether launch at login is allowed. If macOS Login Items shows DialShift as not allowed, enable it there."
- `SetEnabledAsync(true)` rewrites the plist without `Disabled` and clears a launchd override with `launchctl enable gui/<uid>/com.tsiger.dialshift` (removes the override only; it does not load or start the job), then verifies.
- The macOS 13+ Login Items "Allow in the Background" switch lives in the Background Task Management database, which has no public read API. Whether it is reflected in `print-disabled` is confirmed natively by NC-10.

#### 8.2.2 `ISystemPowerEvents`

Location: `DialShift.App/Platform/Abstractions/ISystemPowerEvents.cs`.

```csharp
public interface ISystemPowerEvents : IDisposable
{
    event EventHandler? Resumed;
    void Start();
}
```

- `Start()` is idempotent and is called after the Avalonia desktop lifetime and message loop exist.
- `Resumed` is raised on an arbitrary thread; the App forwards it to `IPlaybackCoordinator.NotifyWakeAsync()`. In practice, macOS raises it synchronously on the posting thread, which is the main thread for a real wake (D30). On Windows, `Start()` subscribes from the STA UI thread, so `SystemEvents` most likely raises it on that thread rather than on a thread of its own (D49); NC-02 records the actual thread.
- `Dispose()` unsubscribes deterministically. Nothing is raised after it returns: the disposed check and the raise share one lock, so `Dispose()` on another thread waits for a raise in progress (handlers must not block on a thread that may dispose).
- Windows: `SystemEvents.PowerModeChanged` with `PowerModes.Resume` (SP-03).
- macOS: `NSWorkspace.DidWakeNotification` only if SP-04 passes (D3). Otherwise a no-op implementation, and the coordinator's monotonic tick gap covers wake. SP-04 was adopted, pending NC-08. `MacPowerEvents` uses the shared `Interop/NotificationObserver` (runtime class `DialShiftNotificationObserver`, D27).
- Registration failure is logged as `power_events.unavailable` and never fatal.

#### 8.2.3 `IFileRevealService`

Location: `DialShift.App/Platform/Abstractions/IFileRevealService.cs`.

```csharp
public interface IFileRevealService
{
    Task RevealInFileManagerAsync(
        string fileOrDirectoryPath,
        CancellationToken cancellationToken = default);
}
```

- A directory is opened. A file is revealed.
- macOS: `/usr/bin/open <dir>` for a folder, `/usr/bin/open -R <file>` for a file.
- Windows: `explorer.exe <dir>` for a folder, `explorer.exe /select,<file>` for a file (see the note on Avalonia `Launcher` below).
- Always `UseShellExecute=false` with `ProcessStartInfo.ArgumentList`. Paths are never interpolated into a command string.
- A missing path raises `DirectoryNotFoundException`/`FileNotFoundException`, and the UI shows it via `IDialogService`.

Avalonia's `Launcher.LaunchDirectoryInfoAsync` may replace the Windows folder case if it opens the folder, as brief 1 §7.4 prefers. `explorer.exe /select` stays for files.

#### 8.2.4 Single instance

Location: `DialShift.App/SingleInstance/`, namespace `DialShift.App.SingleInstance`. It does not belong in Core.

```csharp
public sealed record SingleInstanceMessage(int Version, string Command)
{
    public const int CurrentVersion = 1;
    public const string ActivateCommand = "activate";
    public const int MaxMessageBytes = 4096;
    public static SingleInstanceMessage Activate { get; } = new(CurrentVersion, ActivateCommand);

    /// <summary>UTF-8 JSON + '\n', e.g. {"version":1,"command":"activate"}\n</summary>
    public byte[] ToUtf8Line();

    /// <summary>Strict: object with exactly "version" (== 1) and "command" (== "activate"); size ≤ MaxMessageBytes.</summary>
    public static bool TryParse(ReadOnlySpan<byte> utf8Line, [NotNullWhen(true)] out SingleInstanceMessage? message);
}

public enum SingleInstanceStartResult { Primary, AlreadyRunning, Failed, LockFailed }
public enum SingleInstanceActivationResult { Activated, Rejected, NoResponse }

public interface ISingleInstanceService : IAsyncDisposable
{
    string PipeName { get; }
    event EventHandler? ActivationRequested;                 // raised on a thread-pool thread
    SingleInstanceStartResult TryStartPrimary();              // lock, then server; on server failure releases the lock
    Task<SingleInstanceActivationResult> ActivateExistingAsync(CancellationToken cancellationToken = default);
}
```

**Wire protocol (v1)**

- One short command per connection.
- The client connects with a **1 000 ms** connect timeout and writes one UTF-8 line of at most **4 096 bytes** plus `\n`.
- The server reads with a **2 000 ms** timeout and at most `MaxMessageBytes + 1` bytes. It then replies with one line, `{"version":1,"status":"ok"}\n` or `{"version":1,"status":"rejected"}\n`, and closes.
- The client waits up to **2 000 ms** for the reply.
- Validation (JSON, schema, version, command) happens **before** acting.
- Malformed, oversize, unknown-version, unknown-command and extra-property messages all get `rejected`. They are logged as `single_instance.rejected`, with the byte count only and never the payload. They are not app errors.
- No privileged action is ever triggered by pipe input. `activate` only shows the window.
- Bytes that are not valid UTF-8, and escaped text that can't be decoded (a lone surrogate such as `"\ud800"`), are `rejected` like any other malformed message: `TryParse` never throws.
- Startup window: the listener starts before the app subscribes to `ActivationRequested` (the UI toolkit initializes in between). An `activate` that arrives with no subscriber is acknowledged `ok`, kept as **one** pending activation (later ones coalesce into it) and raised for the next subscriber (`single_instance.activation_replayed`). `single_instance.activated` says "delivered" or "queued". After disposal nothing is kept or raised, and a late message gets `rejected`.

**Pipe name**

- `"DialShift-" + first 16 lower-hex chars of SHA-256(UTF-8("com.tsiger.dialshift/single-instance/v1|" + normalizedDataDirectory))`.
- `normalizedDataDirectory` is `Path.GetFullPath(AppPaths.DataDirectory)` without a trailing separator, and upper-invariant on Windows.
- The data directory is per-user, so the namespace is per-user. It is stable, never derived from untrusted input, and gives `DIALSHIFT_DATA_DIR` test instances isolated pipes.
- The short name keeps the macOS socket path (`$TMPDIR/CoreFxPipe_DialShift-…`) under the 104-byte `sun_path` limit.

**Options**

- Server and client both use `PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous`, in `PipeTransmissionMode.Byte`.
- **Superseded by D24 (SI-D1) and D46.** The server keeps one instance **armed**, created before an accepted connection is handed off. It handles at most **2** connections at once (D31), so the service holds at most `MaxServerInstances` = 3 instances. It enforces that bound itself through its handler slots (CT-SI-13 asserts the peak). The `maxNumberOfServerInstances` passed to the OS is `NamedPipeServerStream.MaxAllowedServerInstances`: on Windows an instance counts against the OS limit while any handle to it is open, including a slow client's, so an OS limit of 3 could refuse the next armed instance. It adds `PipeOptions.FirstPipeInstance` only on a real bind (no live instance of its own), and after every create or re-bind it tightens the Unix socket to owner-only.
- On `net10.0` the macOS socket mode comes from the process umask. The service therefore removes group/other bits itself and logs the observed mode once (`single_instance.socket`). CT-SI-12 asserts 0600 after start and after a re-bind; NC-13 confirms it under LaunchServices.

**Lock and lifecycle**

- Lock file `<data>/.single-instance.lock`, opened with `FileShare.None` and held for the process lifetime.
- `TryStartPrimary`:
  - lock held by another process (Windows sharing/lock violation `0x80070020`/`0x80070021`, macOS `flock` `EWOULDBLOCK` errno 35) → `AlreadyRunning`;
  - any other error creating or opening the lock (a file in the way of the data folder, read-only volume, disk full, access denied) → log `single_instance.lock_failed`, return `LockFailed`. The app shows the startup-failure dialog ("couldn't create its lock file in <data folder>") and exits 1;
  - lock acquired but the server fails to start → release the lock, log `single_instance.server_failed`, return `Failed`. The app then shows the startup-failure dialog and exits.
- The listener loop **never stops** on its own. A failure of one connection (including a client that left before it was accepted, Windows `ERROR_NO_DATA`) is logged as `single_instance.connection_error` and closes only that connection: the dead instance is replaced before it is released, with **no back-off** (D46). Any other accept or create failure is logged as `single_instance.listener_error`, and the loop retries after a 1 s back-off. Only disposal stops it.

**Process exit codes** (D29, `DialShift.App/LaunchOptions.cs` `ExitCodes`)

| Code | Constant | Meaning |
|---|---|---|
| 0 | `Success` | Normal quit, or a second launch that got `Activated` |
| 1 | `StartupFailed` | Startup failure (dialog shown), including a single-instance lock that couldn't be created or opened (`LockFailed`); also an invalid `DIALSHIFT_DATA_DIR`, or an exception escaping the UI toolkit |
| 2 | `ActivationFailed` | Second launch got `Rejected` / `NoResponse` (logged as `single_instance.activate_failed`) |
| 3 | `SingleInstanceFailed` | Lock acquired but the activation pipe failed to start (`Failed`); the startup-failure dialog is shown |
| 4 | `SmokeTestFailed` | A `--smoke-test` run in which at least one check failed, or the smoke watchdog fired (`results.json` says which) |

#### 8.2.5 `IUiDispatcher` and `IDialogService`

Location: `DialShift.App/Services/`.

```csharp
public interface IUiDispatcher
{
    bool CheckAccess();
    void Post(Action action);
    Task InvokeAsync(Action action);
}

public interface IDialogService
{
    Task ShowMessageAsync(string title, string message);
    Task<bool> ConfirmAsync(string title, string message, string confirmText = "Yes", string cancelText = "No");
}
```

- `AvaloniaUiDispatcher` wraps `Dispatcher.UIThread`. It is the only place playback state is marshalled to the UI, for view-model updates only (brief 1 §4.1).
- `AvaloniaDialogService` owns its dialogs:
  - The owner is the main window when it is visible. Otherwise the dialog is ownerless and `CenterScreen`, and is **never** its own owner (fixes BHV-64).
  - The confirm button is the default and the cancel button is the cancel.
  - Every text is selectable and wraps.
  - Tests inject a fake.

#### 8.2.6 `AppPaths` and the `DIALSHIFT_DATA_DIR` override

Location: `DialShift.App/AppPaths.cs`.

```csharp
public enum DataDirectorySource { Default, EnvironmentOverride, SmokeTestTemp }

public sealed class AppPaths
{
    public const string DataDirectoryOverrideVariable = "DIALSHIFT_DATA_DIR";
    public string DataDirectory { get; }
    public DataDirectorySource Source { get; }
    public string LogFile => Path.Combine(DataDirectory, "dialshift.log");
    public string SingleInstanceLockFile => Path.Combine(DataDirectory, ".single-instance.lock");

    public static AppPaths Resolve(Func<string, string?> getEnvironmentVariable, bool smokeTest);
}
```

**Resolution order.** This decision is taken here and recorded as D21.

1. `DIALSHIFT_DATA_DIR` is set and non-blank. It must be an **absolute** path, otherwise startup fails with a clear message. It is used verbatim and created if missing. Source is `EnvironmentOverride`, logged as `app.data_dir_override`. It is intended for smoke and integration tests and CI, and is documented in the README developer section only.
2. `--smoke-test` without an override uses `Path.Combine(Path.GetTempPath(), "DialShift-smoke-" + Guid.NewGuid().ToString("N"))`. Source is `SmokeTestTemp`.
3. Default:
   - Windows: `Path.Combine(Environment.GetFolderPath(SpecialFolder.LocalApplicationData), "DialShift")`, which is `%LOCALAPPDATA%\DialShift`.
   - macOS: `Path.Combine(Environment.GetFolderPath(SpecialFolder.UserProfile), "Library", "Application Support", "DialShift")`, computed explicitly rather than relying on .NET's `LocalApplicationData` mapping.

**Other rules**

- There is no separate "Preview" directory: `DialShift.App` on Windows uses the same directory and format as WPF (OQ-5).
- `DataDirectory` is computed once in `Program.Main` and passed to `SettingsStore`, the logger and the single-instance service.
- Files: `settings.json`, `dialshift.log` (and `.1`), `.single-instance.lock`, `settings.json.unreadable-*`. The `settings.json.before-import-*` name is reserved (OQ-2). The settings file name belongs to Core's `SettingsStore` (`FilePath`, built from the `DataDirectory` it is given), so `AppPaths` does not repeat it.

#### 8.2.7 App file logger and redaction rules

The App implementation of `IAppLog` is `FileAppLog` plus `StreamUrlRedactor`, both in `DialShift.App/Services/`.

**Format and file handling**

- JSON Lines, one object per line: `{"ts":"<DateTimeOffset ISO-8601>","level":"info|warn|error","event":"<dotted.name>","msg":"<redacted>","ex":"<redacted type: message + stack>"}`.
- UTF-8 without BOM.
- Thread-safe (a single lock, append-only). **Never throws**: logging failures are swallowed.
- **Cross-process (D25, LOG-D1).** The primary and a second launch share the file. Each size check, rotation and append also holds `<per-user temp>/DialShift-log-<16 hex>.lock` (`FileShare.None`, never deleted). A writer waits at most 250 ms; after that it appends without the lock and without rotating.
- Rotation: before a write that would push the file past **1 MiB**, rename it to `dialshift.log.1` (replacing the old one).

**Redaction rules**

1. A URL is logged as `scheme://host[:port]/…`. User-info, path, query and fragment are dropped.
2. As a second line of defense, `msg` and `ex` go through the same redactor as the engines' diagnostics (`StreamUrlRedactor`, which prefers over-redaction to a leak): every URL-like run (any scheme and case, also glued to a word, JSON-escaped `:\/\/`, scheme-less `//host`, IPv6 literals, split by whitespace) becomes `scheme://host[:port]/…`; any `user:password@` is removed even without a scheme; a detached `?key=value` query becomes `?…`; the value of any `…token=`, `…key=`, `…sig=`, `…secret=`, `…auth=`, `…password=` pair, `Authorization:` header or `Bearer` token becomes `…`. The case table is `DialShift.Tests/App/RedactionTests.cs`.
3. The user home prefix is replaced by `~` (only at a path boundary).
4. Settings file contents, pipe payloads and HTTP headers are never logged.
5. Station **names** are allowed.

**Required events** (brief 1 §11 DoD)

| Event | Content |
|---|---|
| `app.start` | version, `RuntimeInformation.RuntimeIdentifier`, `ProcessArchitecture`, OS description, engine name, data-dir source |
| `playback.state` | each coordinator transition: from→to, station name, current session id (0 when none live; CR-03) |
| `playback.failed` | kind, attempt, retry delay, fallback flag |
| `schedule.fired` | — |
| `startup_registration.result` | — |
| `wake.detected` | source `os` or `tick_gap` |
| `wake.recovery` | outcome |
| `settings.recovered` / `settings.save_failed` | — |
| `single_instance.*` | — |
| `app.startup_failed` | — |
| `app.render_timer_fallback` | macOS, warn: no `CVDisplayLink` at startup (displays asleep, lid closed); the `CVReturn` code. Rendering runs on a 60 fps timer that idles when nothing changes, until the next start (`MacRenderTimerFallback`, suite `RenderTimerFallback`) |
| `app.exit` | — |

#### 8.2.8 Sleep-inclusive `IMonotonicClock` (D14)

Location: `DialShift.App/Platform/` (per-OS files chosen by the platform lane), namespace `DialShift.App.Platform`. Each class implements `DialShift.Core.Playback.IMonotonicClock`. The composition root injects the one for the current OS into `PlaybackCoordinator`, which uses it for every policy timer.

- **macOS:** `clock_gettime_nsec_np(CLOCK_MONOTONIC)` from `libSystem` (`CLOCK_MONOTONIC_RAW` is equally acceptable). Timestamps are nanoseconds; `GetElapsedTime(a, b) = TimeSpan.FromTicks((b − a) / 100)`.
- **Windows:** a source that keeps counting through sleep. `Stopwatch`/QPC is unverified. `QueryInterruptTime` and `GetTickCount64` (`Environment.TickCount64`) are documented as including sleep; `QueryUnbiasedInterruptTime` is not. Implemented: `WindowsMonotonicClock` uses `Environment.TickCount64` (`GetTickCount64`), which is documented as sleep-inclusive. Its behavior across sleep must still be confirmed on hardware (NC-02).
- Other OSes: `StopwatchMonotonicClock` (the app fails at startup there anyway, §8.1).
- Tests: unit checks that timestamps never decrease and that `GetElapsedTime` converts units correctly. The sleep behavior itself is native-only (NC-02, NC-08).

---

## 9. Remaining native checks

These cannot be verified by automation in this environment: an Apple Silicon Mac dev box (macOS 26.5) plus GitHub Actions on the **private** remote (`spyroskotsakis/dialshift-dev`). They appear as `MAN` cells above. A row whose only open items are here is `NATIVE-PENDING`.

CI (`windows-latest`, `macos-latest` on Apple Silicon) already covers compile, all 23 `DialShift.Tests` suites (including the headless UI suites HS-01..08, HS-13, HS-14 on both OSes, CT-SI-12, the Windows halves of HS-11/12/16, and HS-17 LV-01..LV-11 on Windows), the native smoke in each runner's desktop session (window, tray, live playback at volume 0, recovery, a real second launch; on Windows through the silent `adummy` output, D35), publishing, packaging verification of the downloadable zips (the macOS zip after both `ditto` and `unzip` extraction), and the bundle smoke of the packaged macOS app extracted with `unzip` (CI `36121345627` at `a54e423`). It cannot sleep a runner, sign in interactively, click the tray with a real mouse, observe focus rules, run the bundled macOS app under LaunchServices, hear audio, or sign and notarize.

Each check is run from the **CI artifact** (the zip a user downloads), not from a dev build, with `dialshift.log` kept as evidence. "Pass" means every listed observation holds. Record the result (date, OS build, hardware, artifact run id, pass/fail with notes) in the Status column.

| ID | Check: procedure and pass criteria | Why it cannot run here | Covers | Status |
|---|---|---|---|---|
| NC-01 | **Windows native tray, window and audio pass.** On a Windows 11 x64 desktop with speakers, extract the CI `DialShift-win-x64` zip with Explorer. First run `DialShift.exe --smoke-test --recovery-test --output <dir>` once (it must exit 0 with 34/34 in `results.json`). Then launch normally and check by hand: (1) **tray**: left-click opens the window; right-click shows Open, Play/Pause, Next station, Stations ▸, Follow schedule (its check matches the Schedule page), Volume +10/−10, Quit DialShift, and each item works; hovering shows the tooltip `DialShift · <station>` or `DialShift · Paused`. (2) **Audio**: Listen to a station and hear it; Skip and tray Next station each switch to the next station audibly, and exactly one stream is audible; at volume 0 nothing is heard; after Pause nothing is heard. (3) **Window**: close (X) and minimize hide it while audio continues; "↘ Hide to tray" hides it; `--tray` and "Start in the tray" start with **no window flash**. (4) **Dialogs**: with the window visible, a confirm (delete a slot) is owned by it and Enter/Escape work; "Open settings folder ↗" opens Explorer at `%LOCALAPPDATA%\DialShift`; keyboard Tab shows a visible focus ring on every control. (5) **Recovery and failure**: quit, replace `settings.json` with `{` and launch: one "DialShift · Settings recovered" dialog names the `settings.json.unreadable-*` copy; quit, create a **folder** named `.single-instance.lock` in the data folder and launch: the "DialShift couldn't start" dialog says it couldn't create its lock file, and the process exits 1 (launch it with `(Start-Process .\DialShift.exe -Wait -PassThru).ExitCode` in PowerShell); delete the folder. (6) **Look**: the three pages, both editors and the compact 780×650 size show no clipped or overlapping text (QG-03). (7) Quit from the tray. **Pass:** every observation holds with the §2 texts; the log has `app.exit … code=0 clean=true` and no `DialShift.exe` process remains. | Needs a real mouse on the notification area, focus rules and audible output on a Windows desktop | BHV-03, 04, 09, 11–15, 19–21, 23, 24, 27, 29, 50, 51, 55, 63–65; MX-04; DOD-04; PK-04; QG-03 | Open (the smoke part: PASS in CI `36111775825`) |
| NC-02 | **Windows `SystemEvents` delivery and real sleep.** On Windows hardware with DialShift from the CI zip: (1) while playing, Start → Sleep for at least 30 s (on a laptop, also close the lid), then wake. (2) Repeat while paused inside a slot. (3) Add a slot **in another time zone** (for example `Europe/Athens` when the PC is not on Athens time) that starts while the PC sleeps, and wake after its start (TZ-12). (4) Repeat (1) with power events unavailable, so that only the `GetTickCount64` tick gap detects the wake (D14): run a build with `WindowsPowerEvents.Start` disabled, or confirm `power_events.unavailable` in the log. **Pass:** `power_events.started` at startup, after the message loop; on each wake `power_events.resumed`, one `wake.detected` (`os` or `tick_gap`), then exactly one `wake.recovery` and one reconnect. Record the delay between wake and `Resume`, and if a second recovery follows a `Resume` more than 10 s late (HZ-04). A paused app stays paused. The slot that started during sleep plays, and only once (`schedule.fired` names `… Europe/Athens`). With power events off, the tick gap still recovers. Record the thread `Resumed` arrives on (D49: expected the UI thread), for example from a debugger breakpoint or a temporary log line in a dev build. Quit unsubscribes with no `SystemEvents` hang. | Needs Windows hardware sleep; a hosted runner can't be suspended | BHV-42; MX-08; SP-03; QA-N1; TZ-12; D14; D30; D49; HZ-04; DOD-07; SR-02 | Open |
| NC-03 | **Windows LibVLC real playback and corpus.** Run the `docs/spikes.md` corpus (C1–C15, T1–T15) against the Windows build (`VideoLAN.LibVLC.Windows` 3.0.23.1) on speakers, and fill the "LibVLC (Windows)" column (time to Playing or Failed, and the kind). Also run the recovery policy with the real engine against a public station made unreachable (hosts-file entry to `127.0.0.1`): retries at 3/6/30 s, the fallback after 3 failures, the primary re-check at 120 s, alternation. Check the 25 s watchdog on a hanging server (T14). Check titles: shown for a Shoutcast v1 `http://` station (it answers `ICY 200 OK`); for an `http://` Icecast station, record whether titles or the tag show; the tag for `https://` (D26). HS-17 LV-01..LV-11 already pass on `windows-latest` (CI `36111775825`) for the local transport cases, state mapping, failure kinds, the watchdog and 160 rapid switches, on the silent `adummy` output. This check adds audible output, mute at volume 0, and public MP3/AAC/HLS/HTTPS streams. **Pass:** the kinds match the Adapter column or the difference is explained; no crash; no audio after Stop; the retry and fallback timings match §5.1. | Needs Windows audio and public streams | MX-10, MX-13; BHV-32, BHV-37, BHV-39 | Open |
| NC-15 | **LibVLC fast-switch stress on real Windows hardware.** On a physical Windows 11 x64 PC with speakers, add at least 10 public stations, start one, then drive the window's **Next station** button (accessible name "Next station") through UI Automation: a PowerShell script that finds the button with `System.Windows.Automation` and calls `InvokePattern.Invoke()` 160 times with random 0–300 ms gaps. Do 7 runs. Between runs, record `(Get-Process DialShift).WorkingSet64`. In one run, also press Pause while a station is connecting. **Pass:** no crash or hang in any run (the SIGILL seen with x86_64 LibVLC under Rosetta must not reproduce natively); at most one station audible at any moment; the working set after runs 2–7 stays within ±20 MB of its value after run 1; after each run the last station plays (or stays paused after Pause); the log has no `playback.engine_error`. LV-06 (160 switches against a local server, `adummy`) already passes in CI. | Needs native Windows with a real audio device; CI has only the silent output, and the other data is from Rosetta | MX-10, MX-13; BHV-40 | Open |
| NC-04 | **Windows launch at login and the Task Manager switch.** From the extracted CI zip: (1) turn on "Launch DialShift in the tray when I sign in", sign out and back in: DialShift starts in the tray with no window. (2) Move the extracted folder (or replace it with a newer build) and sign in again: the checkbox shows **off** with "Launch at login points to an older copy of DialShift…"; turning it on repairs it. (3) In Task Manager → Startup apps, disable DialShift, then reopen DialShift's Settings: the checkbox shows **off** with "Turned off in Task Manager's Startup apps…". (4) Turn it on in DialShift: Task Manager shows DialShift **Enabled** after a refresh, and the next sign-in starts it. (5) Turn it off in DialShift: both registry values are gone. **Pass:** the `startup_registration.result` lines match each step; `HKCU\…\Run\DialShift` is `"<exe>" --tray`; `reg query HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run /v DialShift` starts with `03` after (3) and `02` after (4), confirming the §8.2.1 flag semantics (D39). | Needs a real Windows sign-in | BHV-59; MX-07, MX-15; DOD-06; D39 | Open |
| NC-05 | **Windows SmartScreen and Authenticode** (release, D7; not performed: no code-signing certificate). (a) **Development build, now:** on a clean Windows 11 PC, download the CI `DialShift-win-x64.zip` with a browser (so it carries Mark of the Web), extract it with Explorer and run `DialShift.exe`. Record whether SmartScreen shows "Windows protected your PC" and whether "More info → Run anyway" starts the app. (b) **Release:** sign `DialShift.exe` with an Authenticode certificate and an RFC 3161 timestamp, then zip. **Pass for (b):** `signtool verify /pa /v DialShift.exe` succeeds; `Get-AuthenticodeSignature .\DialShift.exe` is `Valid` with the publisher's name; on a clean PC the downloaded zip launches with the publisher shown and no "unknown publisher" block (SmartScreen reputation for a new certificate may still warn; record it). | Needs a signing certificate and a clean Windows machine | D7; PK-05 | Open (release; not performed) |
| NC-06 | **Windows second-launch foreground.** With the app hidden in the tray, launch it again from the Start menu and from an Explorer double-click, both while another app has focus. **Pass:** the existing window comes to the **foreground** (not just a flashing taskbar button); the second process exits 0; the log has `single_instance.activated … delivered`. | Needs a Windows desktop with focus rules | BHV-08, BHV-15; MX-06; DOD-05 | Open |
| NC-07 | **Clean-machine Apple Silicon install and Gatekeeper first launch (ad-hoc signed).** On an Apple Silicon Mac with no Rosetta (`/usr/bin/pgrep oahd` finds nothing, and `arch -x86_64 /usr/bin/true` fails) and no developer tools, download the CI `DialShift-osx-arm64-native-avplayer.zip` with Safari, unzip it in Finder, move `DialShift.app` to `/Applications` and double-click it. **Expected first:** Gatekeeper blocks it, because the build is ad-hoc signed and not notarized (`xattr -p com.apple.quarantine /Applications/DialShift.app` shows the quarantine flag, `codesign -dv` shows `Signature=adhoc`, and `spctl -a -vv` reports it rejected). Open it with System Settings → Privacy & Security → **Open Anyway** (or right-click → Open on older macOS). Then: the menu-bar icon appears and there is no Dock icon; an `https://` **and** an `http://` station play audibly (the ATS media exception, T16, D32); Quit, then relaunch without a prompt; a second `open` activates the window. **Pass:** each observation holds; `app.start` shows `rid=osx-arm64 arch=Arm64 engine=MacAvPlayerPlaybackEngine`; no Rosetta prompt ever appears. Also extract the same zip with `unzip` in Terminal: `codesign --verify --deep --strict DialShift.app` must pass and the app must open after the same Open Anyway step (SR-03, D51; CI already checks the signature of the `ditto` and `unzip` copies and runs the bundle smoke on the `unzip` copy, 32/32 in CI `36111775825`). | Needs a clean Apple Silicon Mac | SP-02; MX-12; DOD-04, DOD-05; D32. When it passes, the README drops "not clean-machine tested" (DOD-08, D36) | Open (partial). **Results so far (2026-09-25; macOS 26.5.2 on the Apple Silicon dev box, build `0.3.0+2c0909e`; the zip that `scripts/build-mac-app.sh` builds, the same script CI packages with, not a downloaded CI artifact):** the zip was given the quarantine attribute Safari sets (`com.apple.quarantine 0081;…;Safari;`) and extracted with `ditto`. The quarantine propagated to `DialShift.app`, `codesign --verify --deep --strict` reports it valid, and `spctl --assess --type execute` reports `rejected`. That is the expected result for an ad-hoc signed app that isn't notarized, so the user must choose **Open Anyway**, as this row and the README say. **Not a clean machine:** Rosetta is installed on this Mac, and it is the development machine. **Remaining (needs a clean Apple Silicon Mac):** (a) a real Safari download and a Finder unzip; (b) the first launch through Open Anyway; (c) the menu-bar icon appears with no Dock icon; (d) `https://` and `http://` stations are audible; (e) a relaunch shows no prompt, and a second `open` activates the window; (f) no Rosetta prompt ever appears; (g) the `unzip`-extracted copy opens after Open Anyway |
| NC-08 | **macOS lid-close wake.** On a MacBook with the bundled app: (1) while playing, close the lid for at least 60 s, then open it. (2) Repeat while paused inside a slot. (3) Add a slot **in another time zone** that starts while the lid is closed (TZ-12, the macOS path). (4) Repeat (1) with the wake observer unavailable (a dev build with `MacPowerEvents.Start` disabled), so that only the tick gap on `CLOCK_MONOTONIC` detects the wake (D14). (5) **No active display at startup (NX-01, D52).** Quit DialShift, then start it while every display is asleep but the Mac is awake: run `pmset displaysleepnow; sleep 15; open -n /Applications/DialShift.app` and leave the keyboard and mouse alone. Wake the displays, then open the window from the menu bar. **Pass:** `power_events.resumed` on each wake, on the main thread (D30); exactly one `wake.recovery` per wake; paused stays paused; the zoned slot plays once (`schedule.fired` names its zone); the tick gap alone recovers when the observer is off. After (5), the start did not crash and the log has `app.render_timer_fallback` naming `CVReturn -6661`. Once the displays wake, the window renders and updates normally (it keeps the fallback timer until the next start). | Needs a physical lid close | SP-02, SP-04; BHV-42; MX-08; QA-N1; TZ-12; D3, D14, D30; DOD-07; NX-01, D52 | Open |
| NC-09 | **Developer ID signing and notarization** (release, D7; not performed: no Apple Developer ID). Sign the bundle's native libraries and executable with a Developer ID Application certificate, the hardened runtime and the entitlements .NET needs under it (at least `com.apple.security.cs.allow-jit`), then `xcrun notarytool submit --wait` and `xcrun stapler staple`. **Pass:** `spctl -a -vv DialShift.app` reports "accepted, source=Notarized Developer ID"; `xcrun stapler validate` succeeds; on a clean Mac the downloaded zip opens with no Gatekeeper warning; `http://` and `https://` stations still play, and launch at login still works. | Needs an Apple Developer ID and notarization credentials | D7; PK-03 | Open (release; not performed) |
| NC-10 | **macOS LaunchAgent at a real login, and Login Items "Allow in the Background".** With the bundled app in `/Applications`: (1) turn on "Launch DialShift in the tray when I sign in", log out and in: DialShift starts in the menu bar with no window (`open -a <bundle> --args --tray`). (2) Move `DialShift.app` elsewhere and log in again: the checkbox shows **off** with the stale diagnostic, and turning it on repairs it. (3) In System Settings → General → Login Items, switch DialShift off under "Allow in the Background", then reopen DialShift's Settings. Record whether `launchctl print-disabled gui/$UID` lists `"com.tsiger.dialshift" => disabled` and whether the checkbox shows **off** with the Login Items diagnostic. If it still shows on, the Background Task Management state is not visible to `print-disabled`: record that limitation in `docs/decisions.md` (amending D39) and the README. (4) `launchctl disable gui/$UID/com.tsiger.dialshift`: the checkbox shows **off**; turning it on runs `launchctl enable` (the override is gone from `print-disabled`) without starting a second instance, and the next login starts DialShift. **Pass:** each step as described; `~/Library/LaunchAgents/com.tsiger.dialshift.plist` passes `plutil -lint` and targets the current bundle; no second instance is ever spawned. | Needs a login-session cycle and System Settings (possible on the dev box, by hand) | BHV-59; MX-07, MX-15; DOD-06; D39 | Open |
| NC-11 | **macOS AVPlayer under real network faults.** From the bundled app: Wi-Fi off in the middle of a stream, then on (retry, then recovery); a real captive portal; fallback alternation with the real engine (BHV-37). **Pass:** failures are classified as in the corpus; the retry and fallback timings match §5.1; no audio after Stop. | Needs network manipulation (possible on the dev box, by hand) | MX-11, MX-13; SP-01; BHV-37 | Open |
| NC-12 | **Menu-bar template icon.** The 44×44 `tray.png` (D34) renders sharp at 17 pt in light and dark menu bars, and follows highlight inversion when the menu is open. The tray menu updates after station and slot edits with no native crash. **Pass:** each observation holds on a Retina display. | A visual check (possible on the dev box, by hand) | BHV-19, BHV-22; MX-05; PK-04; D34 | Open |
| NC-13 | **Socket permissions under LaunchServices.** Launch the bundled app via `open /Applications/DialShift.app`, and again via the LaunchAgent at login (not from a terminal). **Pass:** `stat -f %Lp "$TMPDIR"/CoreFxPipe_DialShift-*` prints `600` both times; the log has `single_instance.socket` with the observed mode; a second `open` activates the window. | Needs the production launch environment | BHV-08; CT-SI-12; MX-06; D24 | Open (the LaunchServices half: PASS). **Results so far (2026-09-25; macOS 26.5.2 on the Apple Silicon dev box, build `0.3.0+2c0909e`, from `dist/`):** `open -n --env DIALSHIFT_DATA_DIR=<dir> dist/DialShift.app --args --tray` created `$TMPDIR/CoreFxPipe_DialShift-d4b8bbfd4b8f41de`, mode `600` and owned by the user. The `single_instance.socket` log line says it "had umask-derived mode 0755; set to 0600". A second `open` left one process only: the log has `single_instance.activated … delivered` and `activate_sent … acknowledged`. SIGTERM gave `app.exit code=0 clean=True` and removed the socket file. The log also has `power_events.started` (NSWorkspace) and `startup_registration.result`. The launch used `dist/` and a data-folder override rather than `/Applications`; neither changes how LaunchServices starts the process. **Observation:** sockets left by earlier crashed runs stay in `$TMPDIR`. All of them are mode 0600, and each name belongs to one data folder. The stale-socket recovery removes a leftover on the next start with the same data folder. The sockets of throwaway data folders stay until macOS cleans its temp folder. They are harmless, because none is writable by others. **Remaining:** the LaunchAgent-at-login half, `stat` printing `600` after a real logout and login. It needs a real login and runs together with NC-10 |
| NC-14 | Intel Mac: **not applicable**. No `osx-x64` artifact is produced; the last Intel/Rosetta build exists only at tag `legacy-last-known-good` (D13) | No Intel hardware; policy | D13 (D2 as amended) | N/A |
| NC-16 | **AVPlayer format corpus on macOS 14** (the minimum, D22). Run the corpus (C1–C15, T1–T15) from the bundled app on macOS 14.x on Apple Silicon. **Pass:** MP3, AAC and HLS play. Any other format that fails (Ogg Vorbis, Opus, FLAC-in-Ogg, `.aacp`) goes into the README's "Formats" line. If MP3, AAC or HLS fail, raise `LSMinimumSystemVersion` by a new decision. | Needs a macOS 14 machine; the dev box and the CI runners run macOS 26 | MX-11, MX-13; D22 | Open |
| NC-17 | **macOS native UI pass of the bundled app** (dev box, by hand). CI runs the smoke on the build output and, since `dafb74c`, on the packaged app's executable (the bundle smoke), but always launched directly from a shell, never through LaunchServices; this pass covers what needs LaunchServices, a real click and audio. (1) Finder double-click or `open` on the running app shows the window (Reopen, BHV-18); a second `open` activates it and exits 0. (2) Clicking the menu-bar icon shows the menu, each item works, and hovering shows the tooltip. (3) Listen, Skip and the menu's Next station switch audibly, one stream at a time; Pause silences it. (4) Close, minimize and "Hide to tray" hide the window while audio continues; start in tray shows no window flash (BHV-09). (5) "Open settings folder ↗" reveals `~/Library/Application Support/DialShift` in Finder (BHV-63). (6) Keyboard: Tab shows a visible focus ring, Enter/Escape work in the dialogs, and VoiceOver reads the field and button names. (7) Recovery and failure: `settings.json` replaced with `{` gives one "Settings recovered" dialog; a **folder** named `.single-instance.lock` in the data folder gives the "couldn't start" dialog and exit 1 (run `/Applications/DialShift.app/Contents/MacOS/DialShift` from Terminal, press OK, then `echo $?`). (8) The pages, both editors and the compact size show no clipped text, in the default and the largest macOS text size. (9) Quit from the menu. (10) **No active display at startup (NX-01, D52).** Run `pmset displaysleepnow; sleep 15; open -n -W dist/DialShift.app --args --smoke-test --output <dir>` and leave the keyboard and mouse alone until it exits, so that the app starts while the displays are asleep. It must not crash (no new `DialShift` report in `~/Library/Logs/DiagnosticReports`), the log must show `app.render_timer_fallback` naming `CVReturn -6661`, and `results.json` must report every check passed, as the run with the probe forced to -6661 did (32/32). **Pass:** each observation holds, and the log records each action, ending with `app.exit … code=0 clean=true`. | Needs the bundled app under LaunchServices, a real click on the menu bar and audio; both CI smokes drive the app in-process from a shell | BHV-03, 04, 08, 09, 11–15, 18, 20, 21, 23, 24, 27, 29, 50, 51, 55, 63–65; MX-04, MX-06; DOD-04, DOD-05; QG-03; NX-01, D52 | Open (the automated part: PASS; the steps by hand and step 10 remain). **Results so far (2026-09-25; macOS 26.5.2 on the Apple Silicon dev box, build `0.3.0+2c0909e`, from `dist/`):** (a) With the build before the fix, the first launch through LaunchServices happened while the displays were asleep, and it crashed at startup: `System.InvalidOperationException: Avalonia.Native was not able to start the RenderTimer. Native error code is: -6661` (`kCVReturnInvalidDisplay`). That is NX-01, fixed in `2c0909e` (D52). (b) `open -n -W dist/DialShift.app --args --smoke-test --recovery-test --output <dir>` passed **34/34** in 76.5 s with `MacAvPlayerPlaybackEngine`. That run covers the bundled app under LaunchServices: real AVPlayer live playback of all three stations, retry then fallback after 44 s and 3 failures, Pause cancelling, the tray menu identity after every editor operation, and a second launch activating the first in 0.1 s. **Remaining, by hand (a person at this Mac):** steps (1)–(9): the Reopen from a Finder double-click showing the window (BHV-18); a real click on the menu-bar icon, each item, and the tooltip; audible switching, one stream at a time; close, minimize and hide with audio continuing, and no window flash; the Finder reveal; the focus ring, Enter/Escape and VoiceOver; the recovery and failure dialogs; the text-size visual pass; Quit from the menu. **Remaining without a person:** step (10), the start with the displays really asleep. It can run on this Mac whenever the displays can stay asleep |

### 9.1 Native checks that require the user

These checks cannot be done from this environment, the Apple Silicon dev box plus hosted CI. Each needs hardware, a session or credentials that only the user has.

| Needs | Checks | Rows and items they unblock (other checks may also be needed) |
|---|---|---|
| **Windows physical hardware** (a Windows 11 x64 PC with speakers, a real mouse, sleep and sign-in) | NC-01 (tray, window, audio), NC-02 (`SystemEvents` and real sleep), NC-03 (LibVLC real playback and corpus), NC-04 (launch at sign-in and Task Manager), NC-06 (second-launch foreground), NC-15 (fast-switch stress). NC-05 (a), the SmartScreen check of the unsigned build, also needs a clean Windows PC | The Windows `MAN` cells; QA-N1 and TZ-12 together with NC-08; SR-02 (the `Resumed` thread, D49); HZ-04's recorded delay |
| **A lid close** (a MacBook) | NC-08 steps 1–4. Step 5, the start with no active display (NX-01), needs no lid, but it needs a person to wake the displays and look at the window | BHV-42, MX-08, SP-02 and SP-04 on macOS; QA-N1 and TZ-12 together with NC-02 |
| **A real login** (log out and in on the Mac) | NC-10 (LaunchAgent at login, Login Items "Allow in the Background"); NC-13's remaining half, the socket mode under the LaunchAgent, runs in the same session | BHV-59, MX-07, MX-15, DOD-06 on macOS; NC-13 |
| **Network faults on real Wi-Fi** | NC-11 (Wi-Fi off mid-stream, a captive portal, fallback alternation with the real engine) | MX-11 and MX-13 on macOS, BHV-37 |
| **Human eyes and hands at the Mac** | NC-12 (the menu-bar template icon in light and dark, highlight inversion); NC-17 steps 1–9 (Reopen, real clicks, audio, dialogs, VoiceOver, visual pass) | BHV-18 and the macOS `MAN` cells of the NC-17 rows; QG-03 together with NC-01 |
| **macOS 14** (an Apple Silicon Mac on 14.x) | NC-16 (the AVPlayer format corpus at the minimum version, D22) | The README "Formats" line; D22 stays or a new decision raises `LSMinimumSystemVersion` |
| **A clean Apple Silicon Mac** (no Rosetta, no developer tools) | NC-07 (download, Gatekeeper first launch, audible `http://` and `https://`) | MX-12, SP-02, DOD-04, DOD-05 on macOS; the README's "not clean-machine tested" wording (D36) |
| **Signing credentials** (release only; not performed) | NC-05 (b), Authenticode; NC-09, Developer ID signing and notarization | D7's release tier, PK-05 |

One open item does not need the user: **NC-17 step 10**, the smoke started with the displays really asleep. It runs on this Mac whenever the displays can stay asleep for the run. NC-14 (Intel) is not applicable (D13).

---

## 10. Open questions (recommended defaults)

Unless the orchestrator overrides one, each lane proceeds on the default. Any override is recorded in `docs/decisions.md`.

| ID | Question | Recommended default |
|---|---|---|
| OQ-1 | How does the coordinator correlate engine callbacks with its session? | **Resolved by D16** (FIFO engine-command pump, every queued start invoked). Original default: per the `IPlaybackEngine` contract, the engine assigns session ids from a counter incremented synchronously on each `StartAsync` entry. The coordinator serializes its engine commands (a small engine-command gate, separate from the state gate) and so knows that the N-th start is session N. **If** the core lane finds this awkward, the alternative is a caller-supplied correlation id, as a contract revision through the spec lane. It is not a silent change. |
| OQ-2 | Settings import/export (upstream v0.2.0) is absent from both local front-ends. Add it? | **Out of scope** for this refactor, because the refactor is behavior parity. Track it as a follow-up. `settings.json.before-import-*` stays reserved. |
| OQ-3 | The WPF "DialShift is in your tray" balloon has no Avalonia equivalent. | Accept the loss (D1). Keep the Settings help text. Optionally the ui lane may show a one-time in-window hint the first time the window is closed, but not a Windows-only native notification. |
| OQ-4 | macOS LaunchAgent: call `launchctl`? | **No.** Write, verify and delete only; the entry takes effect at next login. Use `open -a <bundle> --args --tray` when inside a `.app`. Keep the label `com.tsiger.dialshift`. `SMAppService` (macOS 13+) is a future enhancement. **Amended by D39:** the read-only `launchctl print-disabled` and `launchctl enable` (which only removes a disabled override) are used; `bootstrap`/`bootout` still are not. |
| OQ-5 | Should Windows use a separate "Preview" data dir during the transition? | **No.** `DialShift.App` uses `%LOCALAPPDATA%\DialShift` (same format, Version 1), so users keep their data. Do not run WPF and App at the same time: the mutex and the lock file do not see each other. |
| OQ-6 | Stall watchdog: measure from the last LibVLC `TimeChanged` (today) or time outside `Playing` (contract)? | Time outside `Playing`. Adapters must report `Buffering` when progress stops (contract rule). CT-PB-16 is marked as a normalization. |
| OQ-7 | Wake settle delay? | 2 s (within the brief's 1–3 s), monotonic and tick-driven. |
| OQ-8 | What does a second launch do when the primary does not respond? | Exit code 2 plus a log line, no dialog. A hung primary is a bug to fix, not a UX path. |
| OQ-9 | Avalonia 12.1.3 is also on NuGet. | Pin 12.1.2 per D6 unless a lane shows a needed 12.1.3 fix. Record any change in `docs/decisions.md`. Any bump must also pass the D52 re-verification of the no-display render-timer fallback, which uses Avalonia private APIs. |
| OQ-10 | Delete slot has no confirmation, while station delete does. | Add a confirmation (UX gate: no destructive action without confirmation). Record the behavior change in BHV-58. |
| OQ-11 | Tray "Play / Pause" and "Next station" do not save. | Save after every tray command, as the window buttons do. |

---

## Appendix A: Instruction-file updates (applied)

Applied in the QG-01/QG-04 change on top of `5e9ca34`. The replacement text listed here earlier, first written against `39f5c8a`, was checked again against the current scripts, `.github/workflows/ci.yml`, `DialShift.App/LaunchOptions.cs` (exit codes 0–4), the README commands, the D51 bundle layout, `DIALSHIFT_AUDIO_OUTPUT`, `DIALSHIFT_DATA_DIR` and the smoke commands, and then applied to `AGENTS.md` (Architecture, Commands, Settings (D50), Honest labeling), `.claude/skills/release-packaging/SKILL.md` and `.claude/agents/{release,ui,test,platform,core}-engineer.md`. Two things differ from the earlier listing. The skill now lists the `Info.plist` keys that `scripts/verify-mac-app.sh` actually asserts: the script does not check `CFBundleName` or `NSHighResolutionCapable`, and the earlier text said "the keys above". The agent frontmatter (`name`, `description`, `tools`) is unchanged, so `core-engineer.md` keeps its original description. `CLAUDE.md`, `.claude/settings.json`, `.claude/rules/*`, `playback-engineer.md` and `spec-architect.md` were already current. The instruction files themselves are now the reference, so this appendix no longer repeats their text. See QG-01 for the grep and its intentional hits.
