# Acceptance matrix: behavior inventory, verification plan, QA tracker, contracts

> **Current status (2026-09-25, branch `refactor/single-codebase-timezone` at `0d0e524` plus the playback-lane merge).** Phase 1 code is merged, and WPF is retired. The merged waves are:
> - **spec**: inventory, this matrix, Core contracts, decisions D1–D36;
> - **core**: `PlaybackCoordinator`, `RetryPolicy`, the CR-01/CR-03 fixes in `af816ad`;
> - **release**: CI matrix, `osx-arm64` `.app` and `win-x64` zip, packaging verification, D22/D34; the WPF retirement (D8) and the `native-avplayer` label (D36) in `0d0e524`;
> - **platform**: startup registration, power events, file reveal, sleep-inclusive clocks, single instance, `FileAppLog`, the SI-D1/LOG-D1 fixes in `e3ceaa2`, and one ObjC interop in `e5d70e2`;
> - **UI**: MVVM views, tray, dialogs, `PlaybackHost`;
> - **playback**: `MacAvPlayerPlaybackEngine`, `LibVlcPlaybackEngine`, `PlaybackEngineFactory`; the `DIALSHIFT_AUDIO_OUTPUT` seam (D35) and the Windows LibVLC engine tests (HS-17 LV-01..LV-11);
> - **integration**: the composition root, with the legacy views and `RadioController` deleted, in `4ef9515`;
> - **test**: the native smoke runner (`--smoke-test`, BHV-66) in `4f22dd0`.
>
> **Tests:** 17 suites, **751 passed / 4 skipped** locally (macOS arm64, merged tree). CI run [`36100367406`](https://github.com/spyroskotsakis/dialshift-dev/actions/runs/36100367406) at `4f22dd0` is green: **windows-latest 695 / 9 skipped, macos-latest 743 / 3** (16 suites; the `LibVlcEngine` suite is newer), both packages built and verified, and the native smoke **34/34 on both OSes**. The platform harness and the playback-adapter harness results are in `docs/spikes.md`.
>
> **Matrix (§3, 106 rows):** 28 GREEN, 19 NATIVE-PENDING, 59 TODO (was 14 / 19 / 73 before the smoke and the retirement).
> - **Done:** WPF retired in `0d0e524` (DOD-01, DOD-10). The native smoke (`--smoke-test --recovery-test`) passed 34/34 on windows-latest and macos-latest in CI run `36100367406`: real LibVLC playback on Windows, AVPlayer on macOS, tray identity after every editor operation, and a real second-process activation.
> - **Pending:** the LibVLC engine tests (HS-17 LV-01..LV-11) are merged, and their first Windows CI run is pending.
> - **In progress:** the headless UI suites (HS-01..08, HS-13, HS-14; test, ui), which almost every remaining TODO row waits on, and Phase 2 Core (§6; core).
> - **Then:** the native checks in §9.

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
| BHV-03 | **Corrupt-settings recovery.** These cases all throw inside `Load`: invalid JSON, `Version != 1`, a null or blank station name, an invalid URL, null `Days`. The original is copied to `settings.json.unreadable-yyyyMMddHHmmssfff`, defaults are used, and `Warning` is set. A dialog "DialShift · Settings recovered" then shows the backup path. Defaults reach disk only at the next save. | C:`SettingsStore.Load`; W:`App.OnStartup` (`MessageBox`); M:`App.OnFrameworkInitializationCompleted` (`Message.Show(MainWindow, …)`) | **[DIFF]** WPF uses a native `MessageBox`. Mac uses a custom modal owned by `MainWindow`, even when the window is hidden (start in tray). **[QUIRK]** If the backup `File.Copy` itself throws (for example a permission problem), the exception escapes `Load`. WPF then shows the startup-failure dialog. Mac crashes. | The recovery dialog is shown through `IDialogService`, with the owner resolved safely (§8.2.5). If the backup copy fails, the startup-failure path runs. |
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
| BHV-40 | **Engine lifetime.** LibVLC is created asynchronously, once (`--no-video --no-osd --network-caching=1500 --http-reconnect`, media option `:no-video`). If creation fails, every `Open` fails and the retry loop runs. Old players are stopped and disposed on a background task, and the next session waits for them. | RC ctor, RC:`Retire`, RC:`Open`, RC:`ReleaseEngine` | Same. **[DIFF]** in native runtime: `VideoLAN.LibVLC.Windows` on WPF, and x86_64 `VideoLAN.LibVLC.Mac` (Rosetta) on Mac. | Windows: `LibVlcPlaybackEngine` with the same options. macOS: `MacAvPlayerPlaybackEngine`, with no LibVLC in the package (D2, D13). |
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

Every row started as `TODO`; the status column was last reviewed on the merged tree (`0d0e524` plus the playback-lane merge) against CI run `36100367406` at `4f22dd0` (tests and the native smoke; the counts are in the status header). A status note that cites "SMK" means that run's 34/34 smoke on both OSes; its `results.json`, screenshots and log are the `smoke-win-x64` and `smoke-osx-arm64` artifacts (7-day retention; the next CI run re-runs the smoke). Test IDs are defined in §7 (`CT-*`, `HS-*`). `SMK` is the new app's `--smoke-test` run on a native desktop. `MAN` is a manual checklist step in §9.

### 3.1 Behavior rows

| ID | Behavior | Owner lane | Unit | Headless integration | Native Windows smoke | Native macOS smoke | Status |
|---|---|---|---|---|---|---|---|
| BHV-01 | First launch defaults | core, ui | CT-SET-07 | HS-01 | SMK | SMK | TODO: HS-01 (CT-SET-07 green; SMK green on both OSes: a fresh data folder loads and plays the three default stations) |
| BHV-02 | Restore settings | core | CT-SET-03, CT-SET-04, CT-SET-08 | HS-01 | SMK | SMK | TODO: HS-01 (unit green; SMK "Persistence" reload green on both OSes) |
| BHV-03 | Corrupt-settings recovery | core, ui | CT-SET-02, CT-SET-05, CT-SET-06, CT-SET-10 | HS-07 | MAN | MAN | TODO: HS-07, then MAN (unit green) |
| BHV-04 | Startup failure dialog + log | ui, platform | — | HS-08 | MAN | MAN | TODO: HS-08, then MAN |
| BHV-05 | Canonical data directory | platform | — | HS-18 | SMK | SMK | GREEN (HS-18 on both OSes; SMK runs in its isolated `SmokeTestTemp` folder and reloads its settings from there, D21) |
| BHV-06 | Log location, structured + redacted | platform | CT-LOG-01..04 | HS-10 | SMK | SMK | GREEN (CT-LOG-01..04, HS-10; SMK logs on both OSes: every line valid JSON, `app.start` with version, RID, arch and engine, the unavailable stream logged as `http://127.0.0.1:1/…`, no station path) |
| BHV-07 | Single instance, primary acquisition | platform | — | CT-SI-01, CT-SI-09, CT-SI-11 | SMK | SMK | GREEN (CT-SI-01/09/11 on both OSes; SMK logs `single_instance.primary`, then a real second launch activates it) |
| BHV-08 | Second launch activates the first | platform, ui | — | CT-SI-02..08, CT-SI-10, CT-SI-13, HS-09 | MAN | MAN | NATIVE-PENDING (NC-06, NC-13, NC-17; CT-SI-*, HS-09 and SMK "Second launch activates the running instance" green on both OSes) |
| BHV-09 | Start in tray (`--tray` / setting) | ui | — | HS-06 | MAN | MAN | TODO: HS-06, then MAN |
| BHV-10 | Startup schedule catch-up | core | CT-PB-01 | HS-14 | SMK | SMK | TODO: HS-14 (CT-PB-01 and SMK "Schedule catch-up selects current station" green) |
| BHV-11 | Quit (clean teardown) | ui, platform | CT-PB-35 | HS-04, HS-08 | SMK | SMK | TODO: HS-04, HS-08, and an SMK quit check: the smoke copies the log before it quits, so no `app.exit clean=true` is evidenced (test) (CT-PB-35 green) |
| BHV-12 | Close to tray | ui | — | HS-05 | MAN | MAN | TODO: HS-05, then MAN |
| BHV-13 | Minimize hides | ui | — | HS-05 | MAN | MAN | TODO: HS-05, then MAN |
| BHV-14 | Hide-to-tray button (+ Windows balloon, OQ-3) | ui | — | HS-05 | MAN | MAN | TODO: HS-05, then MAN (SMK "Hide to tray" green on both OSes) |
| BHV-15 | Show window / bring to front | ui | — | HS-05 | MAN | MAN | TODO: HS-05, then MAN (SMK "Restore from tray" green on both OSes) |
| BHV-16 | Save failure dialog, atomic save | core, ui | CT-SET-09 | HS-07 | — | — | TODO: HS-07 (CT-SET-09 green) |
| BHV-17 | Save triggers (incl. tray commands, OQ-11) | ui | — | HS-04 | — | — | TODO: HS-04 |
| BHV-18 | Mac Reopen activation (LSUIElement) | ui | — | — | N/A | MAN | NATIVE-PENDING (NC-17; no automatable column) |
| BHV-19 | Tray icon assets (.ico / template) | ui, release | — | HS-15 | MAN | MAN | TODO: HS-15 icon-asset assertion (release), then NC-01, NC-12 |
| BHV-20 | Tray click semantics per OS | ui | — | — | MAN | MAN | NATIVE-PENDING (NC-01, NC-12; no automatable column) |
| BHV-21 | Tray menu items + command routing | ui | — | HS-04 | MAN | MAN | TODO: HS-04, then MAN |
| BHV-22 | Tray menu refresh, identity preserved | ui | — | HS-03 | SMK | SMK | TODO: HS-03 (SMK tray identity after every editor operation green on both OSes) |
| BHV-23 | Tray tooltip text | ui | — | HS-13 | MAN | MAN | TODO: HS-13, then MAN |
| BHV-24 | Window shell, tabs, theme (D1) | ui | — | HS-01 | MAN | MAN | TODO: HS-01, then MAN |
| BHV-25 | Player card rendering | ui | — | HS-13 | SMK | SMK | TODO: HS-13 (SMK status text, track line and Play/Pause label green on both OSes) |
| BHV-26 | Play/Pause toggle resolution | core, ui | CT-PB-24 | HS-04 | SMK | SMK | TODO: HS-04 (CT-PB-24 and SMK "Pause stops playback" green) |
| BHV-27 | Skip / next station | core | CT-PB-25 | HS-04 | SMK | SMK | TODO: HS-04, and an SMK Skip check: the smoke never presses Skip or "Next station" (test) (CT-PB-25 green) |
| BHV-28 | Volume clamp, forward, mute at 0, persist | core, ui | CT-PB-23 | HS-13 | SMK | SMK | TODO: HS-13 (CT-PB-23 green; SMK tray Volume ±10 reaches settings and engine on both OSes) |
| BHV-29 | Footer UP NEXT / LOCAL TIME | ui | CT-PB-39 | HS-13 | MAN | MAN | TODO: HS-13, then MAN (CT-PB-39 green) |
| BHV-30 | Manual play | core | CT-PB-03, CT-SM-02 | HS-14 | SMK | SMK | TODO: HS-14 (unit and SMK "Manual selection holds within current slot" green) |
| BHV-31 | Live state + texts | core | CT-PB-02 | HS-14 | SMK | SMK | TODO: HS-14 (CT-PB-02 and SMK "Live broadcast" green) |
| BHV-32 | Now-playing metadata (optional) | core, playback | CT-PB-37 | HS-17 LV-04 (Windows) | SMK | SMK | TODO: HS-17 LV-04 (ICY title on the real LibVLC engine) in its first Windows CI run (playback, test). CT-PB-37 green; SMK shows the station tag for the `https://` defaults on both OSes; the README documents the limits (D26, ICY finding) |
| BHV-33 | Pause (user stop) texts + cancellation | core | CT-PB-20, CT-PB-38 | HS-14 | SMK | SMK | TODO: HS-14 (unit and SMK "Pause stops playback" green) |
| BHV-34 | Stream error / end → failure (once per attempt) | core | CT-PB-05, CT-PB-17, CT-PB-18 | — | SMK (recovery) | SMK (recovery) | GREEN (CT-PB-05/17/18; SMK `--recovery-test` on both OSes: three `playback.failed kind=NetworkUnavailable`, one per attempt) |
| BHV-35 | Retry backoff 3/6/30 s, countdown, never gives up | core | CT-PB-05, CT-PB-06, CT-PB-08 | — | SMK (recovery) | SMK (recovery) | GREEN (CT-PB-05/06/08; the SMK recovery log shows `retry_in=3s`, `6s`, then `30s` on both OSes) |
| BHV-36 | Fallback after 3 failures | core | CT-PB-07, CT-PB-09 | — | SMK (recovery) | SMK (recovery) | GREEN (CT-PB-07/09; SMK "Failed stream retries and plays fallback": the fallback plays after 3 failures, "Live · fallback station", on both OSes) |
| BHV-37 | Fallback re-tries primary after 120 s; alternation | core | CT-PB-10, CT-PB-11, CT-PB-12 | — | MAN | MAN | NATIVE-PENDING (NC-03, NC-11; unit green) |
| BHV-38 | 60 s stable playback resets failures (primary only) | core | CT-PB-13, CT-PB-14 | — | — | — | GREEN (CT-PB-13, CT-PB-14; CI `36088138439`) |
| BHV-39 | 25 s stall watchdog | core, playback | CT-PB-15, CT-PB-16 | — | MAN | MAN | NATIVE-PENDING (NC-03; unit green; the macOS real-engine watchdog shown by the adapter harness, corpus T9/T10/T14; the Windows watchdog is HS-17 LV-08, first CI run pending) |
| BHV-40 | Engine selection + lifetime (LibVLC Win / AVPlayer mac) | playback | — | HS-15, HS-17 | SMK | SMK | TODO: HS-17 LV-01..LV-11 first Windows CI run, and an HS-17 macOS check (playback, test). HS-15 engine part green; SMK green (LibVLC on Windows, AVPlayer on macOS, three live stations each) |
| BHV-41 | Stale callbacks discarded | core | CT-PB-19, CT-PB-22, CT-PB-40 | — | — | — | GREEN (CT-PB-19, CT-PB-22, CT-PB-40; CI `36088138439`) |
| BHV-42 | Sleep/wake recovery (layered) | core, platform | CT-PB-29..34 | HS-16 | MAN | MAN | NATIVE-PENDING (NC-02, NC-08; CT-PB-29..34 green, HS-16 green on both OSes; SMK "Resume preserves pause within same slot" green through `NotifyWakeAsync`) |
| BHV-43 | Dispose prevents further playback | core | CT-PB-35, CT-SM-17 | HS-14 | — | — | TODO: HS-14 (CT-PB-35, CT-SM-17 green) |
| BHV-44 | Scheduled switch (even when paused) | core | CT-PB-04, CT-SES-02 | HS-14 | SMK | SMK | TODO: HS-14 (unit and SMK "New schedule occurrence overrides manual station" green) |
| BHV-45 | Manual hold within slot | core | CT-PB-03, CT-SES-02 | — | SMK | SMK | GREEN (CT-PB-03, CT-SES-02; SMK "Manual selection holds within current slot" on both OSes) |
| BHV-46 | Pause holds within slot | core | CT-PB-04, CT-PB-31 | — | SMK | SMK | GREEN (CT-PB-04, CT-PB-31; SMK "Pause holds within current slot" on both OSes) |
| BHV-47 | Forced refresh replays current slot [QUIRK] | core | CT-PB-27, CT-SES-05 | — | — | — | GREEN (CT-PB-27, CT-SES-05; CI `36088138439`) |
| BHV-48 | Catch-up ±7 days | core | CT-SCH-04, existing "Late wake…" | — | — | — | GREEN (CT-SCH-04, legacy "Late wake…"; CI `36088138439`) |
| BHV-49 | DST, computer-local | core | existing DST checks, CT-SES-07 | — | — | — | GREEN (legacy DST checks, CT-SES-07; CI `36088138439` (UTC runners) and dev box (CEST)) |
| BHV-50 | Follow-schedule toggle (page + tray in sync) | ui | — | HS-03, HS-04 | MAN | MAN | TODO: HS-03, HS-04, then MAN (SMK: after each toggle the tray check matches the page toggle, on both OSes) |
| BHV-51 | Stations page | ui | — | HS-01 | MAN | MAN | TODO: HS-01, then MAN |
| BHV-52 | Station add/edit validation | ui | — | HS-02 | — | — | TODO: HS-02 (SMK rejects an empty name and an invalid URL on both OSes) |
| BHV-53 | Station delete (confirm, cleanup, forget) | ui, core | CT-PB-26 | HS-02, HS-03 | — | — | TODO: HS-02, HS-03 (CT-PB-26 green; SMK station delete keeps the tray identity) |
| BHV-54 | Settings import/export (absent; OQ-2) | spec | — | — | — | — | GREEN (spec: absent in both front-ends and in `DialShift.App` at `82a9900`; out of scope per OQ-2) |
| BHV-55 | Schedule page | ui | — | HS-01 | MAN | MAN | TODO: HS-01, then MAN |
| BHV-56 | Slot add/edit validation | ui | — | HS-02 | — | — | TODO: HS-02 (SMK rejects an invalid time, and an edit keeps the slot `Id`) |
| BHV-57 | Slot conflict | core, ui | CT-SCH-07, existing conflict checks | HS-02 | — | — | TODO: HS-02 (unit green; SMK rejects a conflicting slot) |
| BHV-58 | Delete slot (OQ-10) | ui | — | HS-02 | — | — | TODO: HS-02 (SMK slot delete green) |
| BHV-59 | Launch at login (transactional status) | platform, ui | — | HS-11, HS-13 | MAN | MAN | TODO: HS-13, checkbox bound to the verified status (ui); HS-11 green on both OSes; then NC-04, NC-10 |
| BHV-60 | Start-in-tray setting | ui | — | HS-06 | — | — | TODO: HS-06 |
| BHV-61 | Fallback picker | ui, core | CT-PB-36 | HS-02 | — | — | TODO: HS-02 (CT-PB-36 green) |
| BHV-62 | About shows the real version | ui | — | HS-13 | — | — | TODO: HS-13 |
| BHV-63 | Open settings folder | platform | — | HS-12 | MAN | MAN | NATIVE-PENDING (NC-01, NC-17; HS-12 green on both OSes) |
| BHV-64 | Message/confirm dialogs, safe owner | ui | — | HS-07 | MAN | MAN | TODO: HS-07, then MAN |
| BHV-65 | Accessibility names, keyboard defaults | ui | — | HS-02 | MAN | MAN | TODO: HS-02, then MAN |
| BHV-66 | Smoke harness replaced before WPF removal | test | — | — (the headless suites are tracked on their own rows) | SMK | SMK | GREEN (`--smoke-test --recovery-test` 34/34 on `windows-latest` and `macos-latest`, CI `36100367406`; replacement map in §4) |

### 3.2 Brief 1 §11 matrix rows

| ID | Behavior | Owner lane | Unit | Headless integration | Native Windows smoke | Native macOS smoke | Status |
|---|---|---|---|---|---|---|---|
| MX-01 | Settings persistence | core | CT-SET-* | HS-01, HS-02 | SMK | SMK | TODO: HS-01, HS-02 (CT-SET-* and SMK "Persistence" green) |
| MX-02 | Schedule evaluation | core | CT-SCH-*, CT-SES-*, TZ-* | HS-14 | SMK | SMK | TODO: TZ-* (Phase 2), HS-14 (CT-SCH/SES and the SMK schedule checks green) |
| MX-03 | Retry and fallback | core | CT-PB-05..14 | HS-17 | SMK (recovery) | SMK (recovery) | TODO: HS-17 LV-08/LV-09 first Windows CI run (CT-PB-05..14 and SMK `--recovery-test` green on both OSes) |
| MX-04 | Tray/menu actions | ui | Limited: view-model command tests | HS-04 | MAN | MAN | TODO: view-model command tests, HS-04 (ui, test) |
| MX-05 | Tray menu identity (no crash on edit/refresh) | ui | — | HS-03 | SMK | SMK | TODO: HS-03 (SMK tray identity after every editor operation green on both OSes) |
| MX-06 | Second-instance activation | platform | — | CT-SI-*, HS-09 | MAN | MAN | NATIVE-PENDING (NC-06, NC-13, NC-17; CT-SI-*, HS-09 and the SMK second launch green on both OSes) |
| MX-07 | Launch at login | platform | — | HS-11 (contract test) | MAN | MAN | NATIVE-PENDING (NC-04, NC-10; HS-11 green on both OSes) |
| MX-08 | Sleep/wake recovery | core, platform | CT-PB-29..34 (gap logic) | HS-16 (limited) | MAN | MAN | NATIVE-PENDING (NC-02, NC-08; CT-PB-29..34, HS-16 green) |
| MX-09 | Playback coordinator vs fake engine | core, test | CT-PB-*, CT-SM-* | HS-14 | SMK | SMK | TODO: HS-14 (CT-PB-*, CT-SM-* and SMK green) |
| MX-10 | Windows LibVLC playback | playback | — | HS-17 (Windows: LV-01..LV-11, real LibVLC + coordinator, `adummy` output, local server; no audible check) | SMK + corpus | N/A | TODO: HS-17 LV-01..LV-11 first Windows CI run (playback, test); SMK green (three live stations Playing on LibVLC); then NC-03, NC-15 |
| MX-11 | macOS AVPlayer playback | playback | — | HS-17 (limited) | N/A | SMK + corpus | TODO: an HS-17 macOS check in `DialShift.Tests` (playback, test). SMK green (three live stations Playing on AVPlayer; the recovery run fails `NetworkUnavailable` on `127.0.0.1:1` without a crash); then NC-11, NC-16. The adapter-harness corpus on macOS 26.5 is recorded in `docs/spikes.md` |
| MX-12 | Native ARM64 macOS playback | playback, release | — | HS-15 (arm64 Mach-O, no libvlc) | N/A | MAN (Apple Silicon gate, SP-01/SP-02) | NATIVE-PENDING (NC-07; HS-15 arm64 Mach-O and no libvlc green via `verify-mac-app.sh` in CI; SP-01 green; SMK on `macos-latest` logs `rid=osx-arm64 arch=Arm64 engine=MacAvPlayerPlaybackEngine`) |
| MX-13 | Media compatibility corpus | playback | — | Limited | MAN | MAN | NATIVE-PENDING (NC-03, NC-11, NC-15, NC-16; the macOS 26.5 corpus is recorded) |
| MX-14 | Redacted diagnostic logging | platform, core | CT-LOG-01..04 | HS-10 | SMK | SMK | GREEN (CT-LOG-01..04, HS-10; SMK logs on both OSes are valid JSON and redacted, see BHV-06) |
| MX-15 | App upgrade/move + launch-at-login recovery | platform | — | HS-11 (stale-path status) | MAN | MAN | NATIVE-PENDING (NC-04, NC-10; HS-11 stale-path checks green on both OSes) |

### 3.3 Definition of done, spikes, packaging, decisions, quality gates

| ID | Item | Owner lane | Verification | Status |
|---|---|---|---|---|
| DOD-01 | One Avalonia UI is the only maintained front-end (WPF and `DialShift.Mac` deleted; `DialShift.slnx` lists Core, Tests, App) | release | Repo tree, `DialShift.slnx`, and a grep for `System.Windows`, `WinForms`, `DialShift.Mac` and `UseWPF` returns nothing | GREEN (`0d0e524`: `DialShift/` deleted; `DialShift.slnx` lists Core, Tests and App only. Re-run on the merged tree: no source, project, script or workflow matches; `System.Windows.Input` appears only in `ViewModels/RelayCommand.cs` and `Smoke/SmokeUi.cs`, both the cross-platform `ICommand`. Stale mentions in project docs are tracked in QG-01) |
| DOD-02 | No Avalonia/WPF/WinForms/OS/filesystem-location/pipe/process/UI-dispatch reference from `DialShift.Core` | spec | Grep `DialShift.Core/**` for `using Avalonia`, `System.Windows`, `Microsoft.Win32`, `System.IO.Pipes`, `System.Diagnostics.Process`, `Environment.GetFolderPath`, `Dispatcher` returns nothing. `DialShift.Core.csproj` has no `PackageReference`. Re-run at every Core change | GREEN (re-run on the merged tree: no matches, no `PackageReference`) |
| DOD-03 | Windows and macOS compile, test and publish in CI | release | GitHub Actions on the private remote, matrix `windows-latest` + `macos-latest` | GREEN (CI `36100367406` at `4f22dd0`: both jobs build with `-warnaserror`, test (windows 695 passed / 9 skipped, macos 743 / 3), smoke, package and verify. The retirement and playback-lane commits have no CI run yet) |
| DOD-04 | Both packages launch, retain settings, show tray, play/stop/retry, obey schedule, exit cleanly | test | SMK on both OSes plus §9 | TODO: "exit cleanly" has no evidence yet (the smoke copies its log before it quits; an SMK quit check, HS-08 or HS-14) (test); and the smoke runs the build output, not the zip. Launch, settings, tray, play/stop/retry and schedule: SMK green on both OSes. Then NC-01, NC-07 |
| DOD-05 | A second launch activates the existing instance on both OSes | platform | CT-SI-*, HS-09, MAN | NATIVE-PENDING (NC-06, NC-07, NC-17; CT-SI-*, HS-09 and the SMK second launch green on both OSes) |
| DOD-06 | Launch at login enabled, disabled and verified on both | platform | HS-11 plus MAN NC-04/NC-10 | NATIVE-PENDING (NC-04, NC-10; HS-11 green on both OSes) |
| DOD-07 | Sleep/wake verified by the manual native checklist | platform | NC-02/NC-08 | NATIVE-PENDING (NC-02, NC-08) |
| DOD-08 | Apple Silicon status published honestly (D2, D13) | release | README and release notes say "native osx-arm64 (AVPlayer)"; no Intel artifact is produced; the last Intel/Rosetta build is only at tag `legacy-last-known-good` (D13) | GREEN (README at `0d0e524`: the macOS package is "Native `osx-arm64` (AVPlayer)", "ad-hoc signed, not yet notarized or clean-machine tested"; "There is no Intel Mac build"; tag `legacy-last-known-good` keeps the last Intel Mac build. Artifact label `native-avplayer` (D36); no `osx-x64` (PK-01). There are no release notes yet: the first release must repeat this (release)) |
| DOD-09 | Structured, redacted logs with version, RID/arch, engine, transitions, startup-registration outcome, wake outcome, recoverable failures | platform | HS-10 | TODO: `startup_registration.result` is emitted at startup (it is in both SMK logs) but no check asserts it; add it to HS-13 or SMK (test). `app.start` fields: HS-10 green; coordinator transitions, failures and wake: CT-LOG (core) green |
| DOD-10 | Legacy app removed only after equivalent checks pass (D8) | release | All §4 rows GREEN or NATIVE-PENDING before the deletion commit | GREEN (every legacy check in §4 had a passing replacement on both OSes, CI `36100367406` at `4f22dd0`, before the deletion in `0d0e524`; D8 status note) |
| SP-01 | AVPlayer spike, checkpoint 1 (feasibility): compiles; start, stop, volume; plays the core MP3/AAC/HLS corpus on an M-series Mac | playback | Run on this Apple Silicon dev box. "Audio came out once" is not a pass | GREEN (checkpoint A PASS and the production-adapter corpus on macOS 26.5 arm64, `docs/spikes.md`) |
| SP-02 | AVPlayer spike, checkpoint 2 (reliability): repeated source changes, wake/reconnect, stop-while-connecting, second-instance activation, clean exit, clean-machine `.app` install | playback | Dev box plus NC-07 | NATIVE-PENDING (NC-07, NC-08; checkpoint B PASS; second-instance protocol green (CT-SI-*); SMK source changes, second launch and recovery green on `macos-latest`) |
| SP-03 | Windows `SystemEvents` spike: package resolves in `net10.0` without the `-windows` TFM; subscribe after the message loop; deterministic unsubscribe; timer-gap retained | playback, platform | CI `windows-latest` compile, plus NC-02 runtime | NATIVE-PENDING (NC-02; compile PASS; HS-16 Windows green on `windows-latest`) |
| SP-04 | macOS `NSWorkspace.DidWakeNotification` spike: all five §4.4 criteria (D3) | playback, platform | Dev box plus NC-08 (lid close) | NATIVE-PENDING (NC-08; criteria 1, 3, 4, 5 PASS; HS-16 macOS green) |
| PK-01 | `DialShift.App` RIDs are `win-x64;osx-arm64` and never `osx-x64` (D2, D13) | release | csproj review plus HS-15. The csproj part is verified (`RuntimeIdentifiers` = `win-x64;osx-arm64` at `d83f946`); the HS-15 part is covered by `verify-mac-app.sh` in CI | GREEN (csproj `win-x64;osx-arm64`; no `osx-x64` in any csproj, script or workflow at `82a9900`; `lipo -archs` = arm64 in CI `36088138439`) |
| PK-02 | LibVLC packages only for `win-x64`; the macOS package contains no `libvlc*` dylibs | release, playback | HS-15 (`find … -name 'libvlc*'` is empty) | GREEN (`verify-mac-app.sh` (no `libvlc*`/VLC) and `verify-win-package.ps1` (`libvlc\win-x64` only) in CI `36088138439`) |
| PK-03 | macOS `.app`: `Info.plist` with `CFBundleIdentifier=com.tsiger.dialshift`, `CFBundleDisplayName`, `CFBundleIconFile`, `LSUIElement=true`; exec bit set; arm64 Mach-O slice; ad-hoc signed | release | `plutil -p`, `lipo -archs`, `codesign -dv` in CI (macos-latest) | GREEN (`verify-mac-app.sh` in CI `36088138439`: plist keys including `LSMinimumSystemVersion` 14.0 (D22) and ATS media-only (D32), exec bit, arm64, ad-hoc signature) |
| PK-04 | Icons: `.ico`, macOS template PNG, `.icns` (D4) | release, ui | Asset presence plus HS-15 | TODO: `tray.png` (44×44, D34) and `.ico` presence are not yet asserted by HS-15 (release). `.icns` is asserted by `verify-mac-app.sh`; rendering is NC-12 |
| PK-05 | Windows artifact = `.zip` of the `win-x64` publish directory (D7) | release | CI artifact | GREEN (CI artifact `DialShift-win-x64`, verified by `verify-win-package.ps1` in CI `36088138439`) |
| PK-06 | Avalonia 12.1.2 in the App project (D6) | ui, release | csproj; record the actual version here. **Actual: 12.1.2** for `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent` (D19) | GREEN (csproj at `d83f946`) |
| PK-07 | README, THIRD-PARTY-NOTICES (conditional LibVLC wording), `data/README` updated | release | Docs review in the same change | GREEN (README: the D26 limits and the ICY finding ("Differences between the two players"), the native AVPlayer status and the developer variables. THIRD-PARTY-NOTICES: the LibVLC runtime and GPL plugins in the Windows package only; the macOS package carries only the managed `LibVLCSharp.dll`, with no VLC runtime. `data/README.md` has no stale front-end references) |
| QG-01 | No dead code: grep for stale references after each retirement | spec | Grep list in DOD-01/DOD-02 plus obsolete platform branches | TODO: re-run on the merged tree. Code, projects, scripts and CI are clean, but project docs still describe the retired front-ends: `AGENTS.md:8–9` (`DialShift.Mac/` and `DialShift/` as current), `AGENTS.md:30` (Mac publish through `DialShift.Mac.csproj -r osx-x64`), and `.claude/skills/release-packaging/SKILL.md:48` (CI phases: a WPF build step and no smoke phase) (release). Intentional hits: provenance in `docs/`, history in `.claude/agents/`, the tag references |
| QG-02 | Docs updated in the same change as code (this matrix included) | spec | Per-lane review | TODO: ongoing per-lane review (spec) |
| QG-03 | Best UI/UX: consistent theme, clear status, fast tray, no dead-end dialogs | ui, spec | HS-01 screenshots reviewed; §9 native UX pass | TODO: review the SMK screenshots (six views on both OSes, CI artifacts), HS-01 screenshots, and the §9 native UX pass (ui, spec) |
| QG-04 | Test-and-fix until prod-ready | all | This matrix fully GREEN or NATIVE-PENDING | TODO: this matrix fully GREEN or NATIVE-PENDING (all) |

---

## 4. Legacy SmokeChecks coverage map

Every check in WPF `DialShift/SmokeChecks.cs` (now at tag `legacy-last-known-good`) was covered here, and the upstream `EditorSmokeChecks` patterns were adopted, **before** WPF was deleted in `0d0e524` (brief 1 §4.5, §7.7; DOD-10). The **Passing at retirement** column names the native smoke check (`--smoke-test --recovery-test`, 34/34 on `windows-latest` and `macos-latest`, CI `36100367406` at `4f22dd0`) that covered each legacy check. The headless parts of the plan (HS-01..05, HS-14) are still in progress and are tracked on their own rows.

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

### 6.1 QA items

| ID | Severity | Item | Owner lane | Verification method | Status |
|---|---|---|---|---|---|
| QA-B1 | **BLOCKING** | `ResolveZone(null/""/whitespace)` returns `null`, so the zero-conversion path is taken and `TimeZoneInfo.Local` is never used on it | core | TZ-01 (byte-identical `At`); the existing DST checks stay green; code review of `ResolveZone` | TODO |
| QA-B2 | **BLOCKING** | `localZone` is injected: `now` is normalized with `SpecifyKind(Unspecified)`, only 3-arg `ConvertTime` overloads are used, and it is threaded through `ScheduleSession.HoldCurrent`/`TakeChange` | core | TZ-02, TZ-03, TZ-11 pass with a non-host `localZone`; grep shows no 2-arg `ConvertTime`/`ConvertTimeToUtc(DateTime)` in `Scheduler` | TODO |
| QA-B3 | **BLOCKING** | Ambiguous (fall-back) wall time fires once, at the earlier daylight instant: `zoneWall − GetAmbiguousTimeOffsets(zoneWall).Max()` | core, test | TZ-05, a **differing-zone** ambiguous case (Europe/Athens slot, America/New_York computer) | TODO |
| QA-B4 | **BLOCKING** | Only IANA ids are stored. The picker maps `GetSystemTimeZones()` through `TryConvertWindowsIdToIanaId`. Resolution is IANA-first with a `TryConvertIanaIdToWindowsId` fallback. There is never a comparison to `TimeZoneInfo.Local.Id` | ui, core | TZ-14 on CI `windows-latest` plus a unit test for the canonicalization helper; grep for `TimeZoneInfo.Local.Id` returns nothing | TODO |
| QA-N1 | non-blocking | The resume path differs by OS (Mac timer gap vs Windows `SystemEvents`) | core, platform | The layered design routes both into one recovery path: CT-PB-29/30 cover both sources. TZ-12 on both OSes (NC-02, NC-08) | TODO |
| QA-N2 | non-blocking | The existing DST tests are already machine-independent | test | Keep them unchanged; run CI on hosts in different zones (the runners' zones differ) | GREEN (the legacy DST checks are unmodified and pass on the CI runners in UTC, CI `36088138439`, and on the dev box in CEST, +02:00) |
| QA-N3 | non-blocking | Never bump `Settings.Version`. An invalid `TimeZone` string must not reach the `.unreadable-*` path | core | CT-SET-02 (version guard); TZ-08; Phase 2 pin: `"TimeZone": 123` hazard documented (CT-SET-11) | TODO |
| QA-N4 | non-blocking | Per-(zone, localZone, zoneDate, time) memo with day-change invalidation. The real staleness is the OS tzdata snapshot, so a restart is required on a zone change | core | Unit test for memo invalidation across midnight; README restart note (D12) | TODO (Phase 2: the memo test lands with the zone code (core), and the README restart note is added with the zone picker (release); the README has no zone text yet) |
| QA-N5 | non-blocking | Conflicts normalize the zone (`IsNullOrWhiteSpace → ""`, `Trim`) and compare the **resolved** `zone?.Id`. This is an approximation and is documented | core | TZ-09, TZ-10; `null` vs `" "` rejected; case-variant id rejected | TODO |
| QA-N6 | non-blocking | The day tab and "UP NEXT" can disagree with the slot's zone day, so show the zone and the next local fire time on the row, and the zone label in UP NEXT | ui | HS-13 with a cross-zone slot; TZ-07 | TODO |
| QA-N7 | non-blocking | A spring gap goes to the first valid instant (same instant as today). Midnight and whole-day gap sub-cases are in the matrix | core, test | TZ-04, TZ-15 | TODO |
| QA-N8 | non-blocking | A zone change can suppress catch-up (`current.At < last.At`). Key the dedup on `Occurrence.Key`/zone-wall identity, or reset `last` on zone change | core | CT-SES-08 flips from "suppressed" (Phase 1 pin) to "fires" once fixed; unit test for zone edit on the current slot | TODO |
| QA-N9 | non-blocking | `Occurrence.At` is always a computer-local wall clock; never call `ToLocalTime`/`ToUniversalTime` on it | core | CT-SCH-05 (Kind pin); code comment on `Occurrence`; grep | TODO |

### 6.2 Brief 2 §9.2 test matrix

| ID | Scenario | Setup | Expected | Owner lane | Method | Status |
|---|---|---|---|---|---|---|
| TZ-01 | Null zone | Existing settings, `TimeZone = null` | `At` byte-identical to today, including `Kind` | test (core) | Unit: compare against Phase 1 `Evaluate` output for the same `now` | TODO |
| TZ-02 | Zone = computer-local | Slot zone equals the injected `localZone` | Identical fire time | test | Unit, injected `localZone` | TODO |
| TZ-03 | East offset | `08:00 Europe/Athens`, computer = UTC | Fires 06:00 UTC in winter | test | Unit | TODO |
| TZ-04 | DST spring gap | `02:30` in a zone on its jump day | Fires once at the first valid instant, same instant as today | test | Unit | TODO |
| TZ-05 | DST fall overlap, **differing zone** | `02:30 Europe/Athens`, computer = America/New_York | Earlier (daylight) instant (QA-B3) | test | Unit | TODO |
| TZ-06 | Non-hour offset | `Asia/Kolkata` (+05:30); also Nepal +05:45 | Correct instant | test | Unit | TODO |
| TZ-07 | Cross-midnight day | `Mon 23:00 UTC`, computer at +2 | Local `Tue 01:00`; the row shows zone + next local fire time | test, ui | Unit + HS-13 | TODO |
| TZ-08 | Invalid stored id | `"Bad/Zone"` in `settings.json` | Falls back to local, UI shows "(unknown zone)", log warning, no crash, no `.unreadable-*` | test, ui | Unit (`SettingsStore` + `Evaluate`) + HS | TODO |
| TZ-09 | Conflict, same zone and time | Two `08:00` slots in the same zone | Rejected | test | Unit | TODO |
| TZ-10 | Conflict, different zone, same time | `08:00 UTC` vs `08:00` local (non-UTC `localZone`) | Allowed | test | Unit | TODO |
| TZ-11 | Session dedup across DST | Ambiguous slot | Fires once (needs `localZone` threaded through) | test | Unit | TODO |
| TZ-12 | Sleep/resume into a zone-shifted slot | Resume after a zoned slot started | Reconnects the current slot, verified on **macOS (timer gap) and Windows (SystemEvents)** | test, platform | CT-PB-32 variant (unit) + NC-02/NC-08 (native) | TODO |
| TZ-13 | UI round-trip | Set zone, save, reload | Persists | ui | HS-02 variant | TODO |
| TZ-14 | Mac→Windows migration | `settings.json` with an IANA id opened on Windows | Id survives, picker shows it, no silent rewrite (QA-B4) | ui, test | HS on CI `windows-latest` | TODO |
| TZ-15 | Midnight / whole-day gap | Santiago/Beirut/Cuba-style 00:00→01:00 gap; `Pacific/Apia` 2011-12-30 | Fires on the correct shifted day (QA-N7) | test | Unit | TODO. The Apia whole-day gap reports SKIP on `windows-latest` (CI `36103139541`): Windows time-zone data lacks Apia's 2011 date-line change |

---

## 7. Characterization-test plan

The test lane writes these **before code moves** (brief 1 §12 step 2), in `DialShift.Tests`: console checks, `Check(name, condition)`, non-zero exit on failure, no framework.

"Mirrors quirk" means the expectation copies current behavior even though it is odd. **Fix** means the expectation deliberately differs from current behavior, with the reason given.

### 7.0 Implementation status

"Implemented" means written and passing both locally and in CI. `dotnet run --project DialShift.Tests -c Release` reports **751 passed, 4 skipped, 17/17 suites** on macOS arm64 on the merged tree. CI run `36100367406` (`4f22dd0`, before the `LibVlcEngine` suite) reports windows-latest **695 / 9 skipped** and macos-latest **743 / 3**, 16/16 suites. The skips are the checks for the other OS: Windows skips the Unix socket and `chmod` checks and the macOS HS-11/12/16 halves, and macOS skips the Windows halves. DOD-03 is green, so every "Implemented" row below is CI evidence.

Per suite on macOS (passed / skipped): Scheduler 33, ScheduleSession 25, SettingsStore 23, RetryPolicy 23, PlaybackCoordinator 186, PlaybackStateMachine 116, PlaybackProperty 6, PlaybackRace 3, Fakes 36, SingleInstance 127, FileAppLog 68, AppPaths 19, StartupRegistration 41 / 1, FileReveal 19 / 1, MonotonicClock 7, PowerEvents 11 / 1, LibVlcEngine 8 / 1 (the audio-output and composition checks run everywhere; LV-01..LV-11 are Windows only).

| IDs | Status | Location |
|---|---|---|
| CT-SCH-01..08 | Implemented | `DialShift.Tests/Core/SchedulerTests.cs` |
| CT-SES-01..06, CT-SES-08 | Implemented | `DialShift.Tests/Core/ScheduleSessionTests.cs` |
| CT-SES-07 | Implemented as the legacy DST dedup checks, kept unmodified | `DialShift.Tests/Core/ScheduleSessionTests.cs` |
| CT-SET-01..10 | Implemented | `DialShift.Tests/Core/SettingsStoreTests.cs` |
| CT-SET-11 | TODO (Phase 2) | — |
| §7.1 harness fakes | Implemented, with self-tests | `DialShift.Tests/Fakes/` |
| CT-PB-01..40, plus the adversarial checks (ADV-*) and CT-LOG (core) | Implemented (`7ea767d`) | `DialShift.Tests/Core/PlaybackCoordinatorTests.cs`, `CoordinatorRig.cs` |
| CT-SM-01..22, including the property (seed 42) and race suites | Implemented (`7ea767d`) | `DialShift.Tests/Core/PlaybackStateMachineTests.cs` |
| RetryPolicy (§5.1) | Implemented | `DialShift.Tests/Core/RetryPolicyTests.cs` |
| CT-SI-01..13 (CT-SI-03 = HS-09, process level through a child test process), SI-D1, stale-socket recovery | Implemented (`a0f5f8f`, `895e141`) | `DialShift.Tests/App/SingleInstanceTests.cs` |
| CT-LOG-01..04, LOG-D1 (two processes), §8.2.7 never-throws | Implemented (`a0f5f8f`, `895e141`) | `DialShift.Tests/App/FileAppLogTests.cs` |
| HS-10 (`app.start` fields and redaction on disk), HS-18 | Implemented | `DialShift.Tests/App/FileAppLogTests.cs`, `AppPathsTests.cs` |
| HS-11, HS-12, HS-16 (the macOS half runs on macOS, the Windows half on `windows-latest`), §8.2.8 clocks | Implemented | `DialShift.Tests/Platform/` |
| HS-15 | Partial. `scripts/verify-mac-app.sh` and `verify-win-package.ps1` run in CI on the downloadable zips: arm64-only Mach-O, no VLC on macOS, `libvlc\win-x64` only, Info.plist keys, `.icns`, ad-hoc signature, x64 GUI PE. **Missing:** a template `tray.png` / `.ico` presence assertion (PK-04) | `scripts/`, `.github/workflows/ci.yml` |
| HS-17 | Windows: LV-01..LV-11 pass on `windows-latest` (CI `36103139541`) except the LV-08 same-path credentials check in its first wording, which expected a 401 before any credentials. LibVLC 3.0.23.1 reuses a user-info station's credentials preemptively for the same scheme://host:port and path, and never sent them to another path. The check is reworded to accept that or `HttpError`, and its next CI run is pending. macOS: no `DialShift.Tests` check yet; the SMK recovery run exercises AVPlayer against `127.0.0.1:1`. The real engines were also exercised against the full corpus by the out-of-repo playback harness (`docs/spikes.md`, "Adapter (production)" column) | `DialShift.Tests/App/LibVlcEngineTests.cs`, `DialShift.Tests/TestServers/LocalMediaServer.cs` |
| SMK (`--smoke-test --recovery-test`, BHV-66) | Implemented (`4f22dd0`); 34/34 on `windows-latest` and `macos-latest` in CI `36100367406` | `DialShift.App/Smoke/`, `.github/workflows/ci.yml` |
| HS-01..08, HS-13, HS-14 | TODO (test, ui): the headless UI suites are in progress | — |
| TZ-* | TODO (Phase 2) | — |

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
| CT-SCH-06 | `TryTime` rejects `"08:00:00"`, `" 08:00"`, `"08:60"`, `""`, `"24:00"`, and accepts `"23:59"`. | Pin |
| CT-SCH-07 | `Conflicts`: a disabled candidate never conflicts; a disabled existing entry is ignored; the same day at a different time does not conflict. | Pin |
| CT-SCH-08 | `Evaluate` ignores `ScheduleEnabled`: it still returns occurrences when the flag is off, because gating is in `ScheduleSession`. | Pin |

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
| CT-SES-08 | Given `last` = today's 10:00 occurrence, and the slot edited to 09:30 while now is 10:30. When `TakeChange(force:false)` runs. Then null (suppressed). When `force:true` runs. Then 09:30. | Yes. Phase 1 pin of QA-N8; Phase 2 may flip it. |

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
| CT-SET-10 | The backup file name matches `settings.json.unreadable-\d{17}`. | Pin |
| CT-SET-11 | *(Phase 2)* An entry with `"TimeZone": 123` behaves as documented in the QA-N3 hazard note. Phase 2 decides whether it is tolerated; it must be pinned either way. | Pin later |

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
| HS-15 | Publish checks. `osx-arm64`: `lipo -archs` equals `arm64`, no `libvlc*`, `Info.plist` keys (PK-03), template icon present. `win-x64`: `libvlc.dll` present, `.ico` present. `DialShift.App.csproj` has no `osx-x64`. |
| HS-16 | Power events contract: `Start()` is idempotent, `Dispose()` unsubscribes, no `Resumed` after dispose (fake source). Windows: `SystemEvents` compile-level check on `net10.0`. |
| HS-17 | Real engine against `http://127.0.0.1:1/unavailable`: `Failed` with `NetworkUnavailable` or `Unknown`, no crash, and `StopAsync`/`DisposeAsync` are clean. Runs where the engine loads (CI). **Windows (`LibVlcEngineTests`, `windows-latest`):** LV-01..LV-11 drive the real `LibVlcPlaybackEngine` behind the real `PlaybackCoordinator`, using LibVLC's `adummy` output (`DIALSHIFT_AUDIO_OUTPUT=dummy` through `PlaybackEngineFactory`) and an in-process HTTP/ICY server (`DialShift.Tests/TestServers/LocalMediaServer.cs`): LV-01 factory resolution; LV-02 a WAV stream plays; LV-03 volume 0/40 while playing; LV-04 ICY StreamTitle → track line; LV-05 stop releases the connection; LV-06 8 rounds of 20 rapid switches end Playing with exactly one open connection; LV-07 stop-while-connecting at coordinator and engine level; LV-08 transport cases and failure kinds (redirect, user-info Basic auth, PLS, M3U play; a station on the user-info path without credentials either plays with the credentials LibVLC kept, sent preemptively by 3.0.23.1, or fails `HttpError`, and the check names which; no request to another path ever carries an Authorization header; 404/403/500 and a second realm's 401 → `HttpError`; HTML → `UnsupportedFormat`; refused and `.invalid` → `NetworkUnavailable`; `ftp://` → `InvalidUrl`; a stream that ends → `Ended` + `EndOfStream`; a silent server → `Stalled` by the 25 s watchdog on a stepped clock); LV-09 retry countdown 3 s then 6 s; LV-10 dispose while playing; LV-11 log redaction. The native volume level cannot be read back on `adummy`, so audible output and mute stay in NC-03. |
| HS-18 | `AppPaths.Resolve` order: env override, then smoke temp, then OS default. A relative override is rejected. The macOS default is exactly `~/Library/Application Support/DialShift`. |

### 7.10 Characterization and review findings (tracked)

Findings from the Phase 1 characterization suites (`CF-*`), from the spec review of the core lane (`CR-*`), and from the platform-lane verification (SI-D1, LOG-D1). The `[quirk]` checks named below pin the current behavior until the listed fix lands, and flip in the same change as the fix.

| ID | Severity | Finding | Recommendation | Owner lane | Phase | Status |
|---|---|---|---|---|---|---|
| CF-01 | low | `Occurrence.Key` is culture-dependent: it formats `At` with the current culture, so `:` becomes the culture's time separator, and a non-Gregorian calendar changes `yyyy`. Harmless in-process today (keys are never persisted and the culture does not change mid-session), but not the invariant format CT-SCH-02 specifies. Pinned by `[quirk] CT-SCH-02 Key follows CurrentCulture's time separator`. | Format with `CultureInfo.InvariantCulture` (`yyyy-MM-dd'T'HH':'mm`) in Phase 2, since `Scheduler` and `Occurrence` are changed then anyway. No migration is needed because keys are in-memory only. | core | 2 | TODO |
| CF-02 | non-blocking | A backward wall-clock jump suppresses older slots until real time passes the last fired slot (`current.At < last.At` in `ScheduleSession.TakeChange`). This is the same root cause as QA-N8. Pinned by `[quirk] Backward clock jump: Tue 09:00 slot is suppressed`. | Apply the Phase 2 fix from brief 2 §7.8: key the dedup on `Occurrence.Key`/zone-wall identity, or reset `last` on a zone change. The fix must keep the intended guard (`[quirk] Backward clock jump: Mon 12:00 does not replay last week's Wed slot` stays), and flips the Tue 09:00 check together with CT-SES-08 (QA-N8). | core | 2 | TODO |
| CF-03 | low | `SettingsStore.Warning` is never cleared: a later successful `Load` on the same store keeps the old warning. No user impact today, because the App loads once per process. Pinned by `[quirk] Warning is never cleared by a later successful Load on the same store`. | Reset `Warning = null` at the start of `Load`. Batch it with the Phase 2 `SettingsStore` change for QA-N3/CT-SET-11. | core | 2 | TODO |
| CF-04 | low | If a `settings.json.unreadable-<yyyyMMddHHmmssfff>` backup with the same millisecond name already exists, `File.Copy` throws `IOException` from inside the recovery `catch`, so it escapes `Load` (see the BHV-03 quirk). | In Phase 1 the ui/platform lanes' startup-failure path (BHV-04, HS-08) turns the escape into a dialog and a log instead of a crash. The Core fix in Phase 2 is a collision-free backup name (retry with a suffix) plus a test. | core (fix); ui, platform (Phase 1 mitigation) | 2 (fix), 1 (mitigation) | TODO. The Phase 1 mitigation is in place in `4ef9515`: `Settings` is resolved inside `App.StartAsync`'s try block, so an escaping `Load` exception goes to `FailStartupAsync` (dialog, `app.startup_failed`, exit 1). The check is pending HS-08 |
| CR-01 | low (latent) | `PlaybackCoordinator.Open` increments `startsIssued` and sets `currentSessionId` (`PlaybackCoordinator.cs:310`) **before** it builds the `StreamSource`/`Uri` and enqueues the start (`:314`). An exception in between would permanently shift the "N-th start is session N" mapping (D16), so every later session's events would be dropped as stale. More generally, a transition that throws after `NewOperation()` leaves an active attempt with `currentSessionId == 0`, which the stall watchdog (`:360`) never fails, so the coordinator would stay in `Connecting` until the next user or schedule action. This is unreachable today because `SettingsStore.ValidUrl` and `new Uri` agree. | Build the command first, then assign the id and enqueue, so that nothing can throw between the increment and the enqueue. | core | 1 | GREEN. Fixed in `af816ad`: `Open` validates the URL and builds the `EngineCommand` before it commits `startsIssued`/`currentSessionId` (`PlaybackCoordinator.cs:326–332`), and fault recovery never leaves `Connecting` stuck. Covered by ADV-09 (synchronous and faulted `StartAsync`) and ADV-16 (invalid URL never reaches the engine) |
| CR-02 | low | `DisposeAsync` awaits the engine's `StopAsync` completion (`PlaybackCoordinator.cs:767`) with no bound, so an adapter whose stop hangs blocks quit forever. | The App's quit path (BHV-11) awaits coordinator disposal with a timeout (about 3 s), then logs `app.exit` with a timeout flag and continues the teardown. | ui | 1 | FIXED in `4ef9515`, test pending. `PlaybackHost.StopAsync` bounds coordinator disposal at 3 s (`DisposeTimeout`) and logs `app.quit.dispose_timeout`. `App.TeardownAsync` also bounds the single-instance and provider disposal at 2 s each and logs `app.exit … clean=false`, so the worst-case quit is about 7 s. The automated check is HS-14 (test) |
| CR-03 | nit | `playback.state` logs `session={startsIssued}`, the last issued id, rather than the current session. In Stopped, Failed and Suspended it names the previous attempt. | Keep it, and document it as "last issued session" in §8.2.7, or log `currentSessionId`. | core | 1 | GREEN. Fixed in `af816ad`: `playback.state` logs `session={currentSessionId}` (`PlaybackCoordinator.cs:613`), which is 0 when no session is live |
| SI-D1 | medium | On Unix, all server instances of a pipe share one ref-counted listening socket. When the handled instance was disposed before the next was created, the socket closed, and a second launch that connected in that gap was dropped (no activation, exit 2). | Keep a listening instance armed at all times; bound concurrent handlers (D24). | platform | 1 | GREEN. Fixed in `e3ceaa2`, and a default check since `895e141`: "SI-D1 an activation arriving while the previous connection is torn down is still served (5 of 5)", plus CT-SI-12 re-bind mode checks |
| LOG-D1 | medium | Two processes (the primary and a second launch) appending to `dialshift.log` overwrote each other's lines, because .NET `FileMode.Append` is not `O_APPEND`, and two rotations could race and lose `dialshift.log.1`. | A cross-process lock file around the size check, rotate and append, with a bounded wait (D25). | platform | 1 | GREEN. Fixed in `e3ceaa2`, and a default check since `895e141`: LOG-D1 two-process tests (4 000 lines, none lost or torn; per-process order kept across rotations) |

### 7.11 Integration hazards (tracked)

These are risks found by the test lane during integration, and by the spec review of the integration merge. Each one is mitigated, accepted with a reason, or open with an owner.

| ID | Severity | Hazard | Disposition | Owner lane | Status |
|---|---|---|---|---|---|
| HZ-01 | high (if it happened) | **A used engine never plays.** If an engine that was already started (for example resolved from the container elsewhere) were handed to the coordinator, its session ids would not match the coordinator's N-th-start mapping (D16). Every event would be discarded as stale and nothing would ever play. | **Mitigated by D23:** the single-use `PlaybackEngineFactory` is the only source, `IPlaybackEngine` is never registered, and a second `Create()` throws. The "fresh engine" rule is in the `IPlaybackEngine` contract remarks. | playback | Mitigated |
| HZ-02 | medium | **Shutdown hang.** An adapter whose stop never completes would block quit forever. | **Mitigated by CR-02:** quit is bounded (3 s for playback, 2 s for single instance, 2 s for the provider). | ui | Mitigated; test pending HS-14 |
| HZ-03 | low | **Duplicate snapshot on a synchronous `StartAsync` throw.** When an adapter throws synchronously from `StartAsync`, the failure reaches the coordinator through the pump's faulted-task path, and subscribers can see a duplicate `SnapshotChanged` for that transition. | **Accepted.** The contract forbids throwing for stream problems, both adapters report failures through `Failed`, and snapshot consumers are idempotent (the view models render the latest value). | core | Accepted |
| HZ-04 | low | **A Windows `Resume` that arrives more than 10 s late causes a second reconnect.** If the tick gap has already detected the wake and the recovery completed more than 10 s (D15) before `SystemEvents` delivers `PowerModes.Resume`, a second recovery runs. That is one extra reconnect after a 2 s settle. | **Accepted.** The cost is a brief audio restart. NC-02 records the observed delay between wake and `Resume`; revisit D15 if it is routinely more than 10 s. | platform | Accepted |
| HZ-05 | medium | **A blocking `StartAsync`.** The coordinator's pump invokes `StartAsync` synchronously, usually on the UI thread, and issues the next command (typically the superseding stop) only after it returns. | **Contract rule added** to `IPlaybackEngine` ("StartAsync returns promptly"). Both adapters comply by design: AVPlayer posts to the main queue, and LibVLC creates the player after the previous one is released on a pool thread. | playback | Mitigated (contract) |
| HZ-06 | low | **About 100–200 ms activation gap at startup.** `Program.Main` starts the single-instance listener before Avalonia, but `App.Run` subscribes to `ActivationRequested` only when the desktop lifetime starts. A second launch in that window is acknowledged (it exits 0), but nothing shows the window. That matters only when the first instance starts with `--tray`. | **Accepted and documented here.** The window is a fraction of a second at process start, and relaunching shows the window. | ui, platform | Accepted |
| HZ-07 | low | **CT-LOG (core) fails when the checkout path contains `/private`.** The sentinel list includes `"/private"`, and the `playback.engine_error` entry carries an exception stack trace with build-time source paths. A checkout under `/private/tmp` or `/private/var/folders` (the macOS temp dirs) therefore fails with "no coordinator log entry contains …". Reproduced at `e5d70e2`; passes with `-p:PathMap=<repo>=/src` and in the normal checkout. | Use sentinels that cannot occur in a path (for example `/private-alpha`), or assert only on `msg` and on the exception message rather than the full stack text. | test | TODO |
| HZ-08 | low | **Flaky macOS clock check on hosted runners.** "§8.2.8 mac CLOCK_MONOTONIC: GetElapsedTime over a 200 ms sleep is 200 ms ±50 ms" failed in CI run `36086991425` (`b09dd03`; 738 passed, 1 failed) because the runner overslept, then passed on the rerun at `e5d70e2`. | Assert a lower bound (≥ 200 ms), and keep the relative check against `Stopwatch` over the same interval, which already exists. Drop the absolute upper bound. | test | TODO |

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

**macOS**

- File `~/Library/LaunchAgents/com.tsiger.dialshift.plist`. The label `com.tsiger.dialshift` is kept so existing users' entries are recognized.
- Contents: `ProgramArguments` = `["/usr/bin/open", "-a", "<bundle path>", "--args", "--tray"]` when running inside a `.app`, otherwise `[<exe>, "--tray"]` (development runs). Also `RunAtLoad=true` and `ProcessType=Interactive`.
- All strings are XML-escaped (`SecurityElement.Escape`). The file is written to a temp path and moved into place atomically.
- **No `launchctl bootstrap` or `bootout`**, because it would launch a second instance immediately or could kill the running one (OQ-4). The entry takes effect at the next login.

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
- `Resumed` is raised on an arbitrary thread; the App forwards it to `IPlaybackCoordinator.NotifyWakeAsync()`. In practice (D30), macOS raises it synchronously on the posting thread, which is the main thread for a real wake, and Windows raises it on the `SystemEvents` thread.
- `Dispose()` unsubscribes deterministically. Nothing is raised after dispose.
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

public enum SingleInstanceStartResult { Primary, AlreadyRunning, Failed }
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

**Pipe name**

- `"DialShift-" + first 16 lower-hex chars of SHA-256(UTF-8("com.tsiger.dialshift/single-instance/v1|" + normalizedDataDirectory))`.
- `normalizedDataDirectory` is `Path.GetFullPath(AppPaths.DataDirectory)` without a trailing separator, and upper-invariant on Windows.
- The data directory is per-user, so the namespace is per-user. It is stable, never derived from untrusted input, and gives `DIALSHIFT_DATA_DIR` test instances isolated pipes.
- The short name keeps the macOS socket path (`$TMPDIR/CoreFxPipe_DialShift-…`) under the 104-byte `sun_path` limit.

**Options**

- Server and client both use `PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous`, in `PipeTransmissionMode.Byte`.
- **Superseded by D24 (SI-D1).** The server keeps one instance **armed**, created before an accepted connection is handed off. It handles at most **2** connections at once (D31), so `maxNumberOfServerInstances` is 3. It adds `PipeOptions.FirstPipeInstance` only on a real bind (no live instance of its own), and after every create or re-bind it tightens the Unix socket to owner-only.
- On `net10.0` the macOS socket mode comes from the process umask. The service therefore removes group/other bits itself and logs the observed mode once (`single_instance.socket`). CT-SI-12 asserts 0600 after start and after a re-bind; NC-13 confirms it under LaunchServices.

**Lock and lifecycle**

- Lock file `<data>/.single-instance.lock`, opened with `FileShare.None` and held for the process lifetime.
- `TryStartPrimary`:
  - lock taken by someone else → `AlreadyRunning`;
  - lock acquired but the server fails to start → release the lock, log `single_instance.server_failed`, return `Failed`. The app then shows the startup-failure dialog and exits.
- The listener loop **never stops** on a per-connection exception. It logs, backs off 1 s and continues. Only disposal stops it.

**Process exit codes** (D29, `DialShift.App/LaunchOptions.cs` `ExitCodes`)

| Code | Constant | Meaning |
|---|---|---|
| 0 | `Success` | Normal quit, or a second launch that got `Activated` |
| 1 | `StartupFailed` | Startup failure (dialog shown); also an invalid `DIALSHIFT_DATA_DIR`, or an exception escaping the UI toolkit |
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
    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");
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
- Files: `settings.json`, `dialshift.log` (and `.1`), `.single-instance.lock`, `settings.json.unreadable-*`. The `settings.json.before-import-*` name is reserved (OQ-2).

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

CI (`windows-latest`, `macos-latest` on Apple Silicon) already covers compile, `DialShift.Tests` (including CT-SI-12, the Windows halves of HS-11/12/16, and HS-17 LV-01..LV-11 on Windows), the native smoke in each runner's desktop session (window, tray, live playback at volume 0, recovery, a real second launch; on Windows through the silent `adummy` output, D35), publishing, and packaging verification of the downloadable zips. It cannot sleep a runner, sign in interactively, click the tray with a real mouse, observe focus rules, run the bundled macOS app under LaunchServices, or check audible output.

Each check is run from the **CI artifact** (the zip a user downloads), not from a dev build, with `dialshift.log` kept as evidence. "Pass" means every listed observation holds. Record the result (date, OS build, hardware, artifact run id, pass/fail with notes) in the Status column.

| ID | Check: procedure and pass criteria | Why it cannot run here | Covers | Status |
|---|---|---|---|---|
| NC-01 | **Windows native tray and window pass.** The automated part, `DialShift.exe --smoke-test --recovery-test` (BHV-66), already passes in CI on `windows-latest` (34/34, CI `36100367406`). On a Windows 11 x64 desktop, run it once more from the CI artifact zip, then check by hand: tray **left-click opens** the window; **right-click** shows the menu, and every item works (Open, Play/Pause, Next station, Stations ▸, Follow schedule check state, Volume ±10, Quit); close-to-tray and minimize hide the window while audio continues; the hide button works; `--tray` and "Start in tray" start with **no window flash**; "Open settings folder" opens Explorer at the data dir; save-failure and confirm dialogs are owned by the window. **Pass:** smoke exits 0; every step matches the §2 texts; Quit leaves `app.exit code=0 clean=true` and no process. | Needs a real mouse on the notification area, focus behavior and audible output on a Windows desktop | BHV-09, 11–15, 19–25, 50, 63, 64; MX-04, MX-05; DOD-04 | Open (the smoke part: PASS in CI `36100367406`) |
| NC-02 | **Windows `SystemEvents` delivery and real sleep.** On Windows hardware, while playing, sleep for at least 30 s (Start → Sleep; on a laptop also lid close), then wake. Repeat while paused inside a slot, and across a slot boundary (TZ-12). Then repeat with power events forced unavailable, so that only the `GetTickCount64` tick gap can detect the wake (D14). **Pass:** `power_events.started` at startup (after the message loop); on each wake, `power_events.resumed` and one `wake.detected` (`os` or `tick_gap`), then exactly one `wake.recovery` and one reconnect. The exception is a `Resume` arriving more than 10 s after the recovery completed (HZ-04): record that delay. A paused app stays paused; a slot that started during sleep plays; with power events off the tick gap still recovers; quit unsubscribes with no `SystemEvents` hang. | Needs Windows hardware sleep; a hosted runner can't be suspended | BHV-42; MX-08; SP-03; QA-N1; TZ-12; D14; D30; HZ-04; DOD-07 | Open |
| NC-03 | **Windows LibVLC real playback and corpus.** Run the `docs/spikes.md` corpus (C1–C15, T1–T15) against the Windows build (`VideoLAN.LibVLC.Windows` 3.0.23.1), and fill the "LibVLC (Windows)" column (time to Playing or Failed, and the kind). Also: the recovery policy with the real engine (3/6/30 s retries, fallback after 3 failures, primary re-check at 120 s, alternation), the 25 s watchdog on a hanging server (T14), titles shown for a Shoutcast v1 `http://` station (it answers `ICY 200 OK`), for an `http://` Icecast station either titles or the tag (record which: LibVLC 3.0.4 sent no `Icy-MetaData` on a plain HTTP 200, `docs/spikes.md`), and the tag shown for `https://` (D26). HS-17 LV-01..LV-11 already cover the local transport cases, state mapping and failure kinds on `windows-latest` without audio (first CI run pending); this check adds audible output, mute at volume 0, and the public MP3/AAC/HLS/HTTPS streams. **Pass:** kinds match the Adapter column or the difference is explained; no crash; no audio after Stop. | Needs Windows audio | MX-10, MX-13; BHV-32, BHV-37, BHV-39 | Open |
| NC-15 | **LibVLC fast-switch stress on real Windows.** 7 runs of 160 rapid station switches (0–300 ms apart), plus 50 Skip presses and a Stop in the middle of a connect. **Pass:** no crash in any run (the SIGILL seen under Rosetta must not reproduce natively); at most one audible player at any time; working set flat (±20 MB) after the first run; the final station plays. | Needs native Windows; the only data so far is x86_64 LibVLC under Rosetta | MX-10; BHV-40 | Open |
| NC-04 | **Windows launch at login.** Enable it, sign out and in: DialShift starts in the tray, with no window. Move the extracted folder (or replace it with a newer build) and sign in again: the Settings checkbox shows **off** with the stale diagnostic, and turning it on repairs it. **Pass:** `startup_registration.result` lines match each step; the HKCU `Run` value is `"<exe>" --tray`. | Needs a real Windows sign-in | BHV-59; MX-07, MX-15; DOD-06 | Open |
| NC-05 | Windows SmartScreen / Authenticode behavior of the `.zip` artifact (release only, D7) | Needs a signing certificate and a clean Windows machine | D7 | Open (release) |
| NC-06 | **Windows second-launch foreground.** With the app hidden in the tray, launch it again from the Start menu and from an Explorer double-click, both while another app has focus. **Pass:** the existing window comes to the **foreground** (not just a flashing taskbar button); the second process exits 0; the log has `single_instance.activated`. | Needs a Windows desktop with focus rules | BHV-08, BHV-15; MX-06; DOD-05 | Open |
| NC-07 | **Clean-machine Apple Silicon install plus Gatekeeper first launch.** On an Apple Silicon Mac with no Rosetta and no developer tools, unzip the CI `DialShift-osx-arm64-*` artifact, move it to `/Applications`, and open it (ad-hoc signed: right-click → Open, or System Settings → Open Anyway). **Pass:** the menu-bar icon appears with no Dock icon; an `https://` **and** an `http://` station play (ATS media exception, T16, D32); Quit, then relaunch; a second launch activates the window; `app.start` shows `rid=osx-arm64 arch=Arm64 engine=MacAvPlayerPlaybackEngine`; no Rosetta prompt ever appears. | Needs a clean Apple Silicon Mac | SP-02; MX-12; DOD-04, DOD-05; D32. When it passes, the README drops "not clean-machine tested" (DOD-08, D36) | Open |
| NC-08 | **macOS lid-close wake.** While playing, close the lid for at least 60 s, then open it. Repeat while paused, across a slot boundary (TZ-12 macOS path), and with the wake observer forced unavailable (tick gap on `CLOCK_MONOTONIC` only, D14). **Pass:** `power_events.resumed` on wake (the main thread, D30); exactly one `wake.recovery` per wake; paused stays paused; a new slot plays; the tick gap alone recovers when the observer is off. | Needs a physical lid close | SP-04; BHV-42; MX-08; QA-N1; TZ-12; D3, D14, D30; DOD-07 | Open |
| NC-09 | Gatekeeper with Developer ID signing and notarization (release only, D7): `spctl -a -vv` reports "accepted, source=Notarized Developer ID"; first launch shows no warning | Needs an Apple Developer ID and notarization credentials | D7; PK-03 | Open (release) |
| NC-10 | **macOS LaunchAgent at a real login.** Enable it, log out and in: DialShift starts in the menu bar with no window (`open -a <bundle> --args --tray`). Move `DialShift.app` elsewhere and log in again: the checkbox shows **off** with the stale diagnostic, and re-enabling repairs it. **Pass:** `~/Library/LaunchAgents/com.tsiger.dialshift.plist` passes `plutil -lint` and targets the current bundle; no second instance is spawned. | Needs a login-session cycle (possible on the dev box, by hand) | BHV-59; MX-07, MX-15; DOD-06 | Open |
| NC-11 | **macOS AVPlayer under real network faults.** From the bundled app: Wi-Fi off in the middle of a stream, then on (retry, then recovery); a real captive portal; fallback alternation with the real engine (BHV-37). **Pass:** failures are classified as in the corpus; the retry and fallback timings match §5.1; no audio after Stop. | Needs network manipulation (possible on the dev box, by hand) | MX-11, MX-13; SP-01; BHV-37 | Open |
| NC-12 | **Menu-bar template icon.** The 44×44 `tray.png` (D34) renders sharp at 17 pt in light and dark menu bars, and follows highlight inversion. The tray menu updates after station and slot edits with no native crash. | A visual check (possible on the dev box, by hand) | BHV-19, BHV-22; MX-05; PK-04; D34 | Open |
| NC-13 | **Socket permissions under LaunchServices.** Launch the bundled app via `open DialShift.app`, and again via the LaunchAgent at login (not from a terminal). **Pass:** `stat -f %Lp "$TMPDIR"/CoreFxPipe_DialShift-*` prints `600`; the log has `single_instance.socket` with the observed mode; a second `open` activates. | Needs the production launch environment | BHV-08; CT-SI-12; MX-06; D24 | Open |
| NC-14 | Intel Mac: **not applicable**. No `osx-x64` artifact is produced; the last Intel/Rosetta build exists only at tag `legacy-last-known-good` (D13) | No Intel hardware; policy | D2, D13 | N/A |
| NC-16 | **AVPlayer format corpus on macOS 14** (the minimum, D22). Run the corpus (C1–C15, T1–T15) from the bundled app on macOS 14.x Apple Silicon. **Pass:** MP3, AAC and HLS play. Any other format that fails (Ogg Vorbis, Opus, FLAC-in-Ogg, `.aacp`) goes into the README capability list. If MP3/AAC/HLS fail, raise `LSMinimumSystemVersion` by a new decision. | Needs a macOS 14 machine; the dev box runs 26.5 | MX-11, MX-13; D22 | Open |
| NC-17 | **macOS native UI pass of the bundled app** (dev box, by hand). The CI smoke runs the unbundled build and already covers a second launch; this pass covers what needs the bundle and a real click: Finder double-click or `open` on the running app shows the window (Reopen, BHV-18); a second `open` of the bundle activates it and exits 0; clicking the menu-bar icon shows the menu (BHV-20); "Open settings folder" reveals the data dir in Finder (BHV-63); start in tray shows no window flash (BHV-09). **Pass:** each observation holds, and the log records each action. | Needs the bundled app under LaunchServices and a real click on the menu bar; the smoke drives the app in-process | BHV-08, 09, 18, 20, 63; MX-06; DOD-05 | Open |

---

## 10. Open questions (recommended defaults)

Unless the orchestrator overrides one, each lane proceeds on the default. Any override is recorded in `docs/decisions.md`.

| ID | Question | Recommended default |
|---|---|---|
| OQ-1 | How does the coordinator correlate engine callbacks with its session? | **Resolved by D16** (FIFO engine-command pump, every queued start invoked). Original default: per the `IPlaybackEngine` contract, the engine assigns session ids from a counter incremented synchronously on each `StartAsync` entry. The coordinator serializes its engine commands (a small engine-command gate, separate from the state gate) and so knows that the N-th start is session N. **If** the core lane finds this awkward, the alternative is a caller-supplied correlation id, as a contract revision through the spec lane. It is not a silent change. |
| OQ-2 | Settings import/export (upstream v0.2.0) is absent from both local front-ends. Add it? | **Out of scope** for this refactor, because the refactor is behavior parity. Track it as a follow-up. `settings.json.before-import-*` stays reserved. |
| OQ-3 | The WPF "DialShift is in your tray" balloon has no Avalonia equivalent. | Accept the loss (D1). Keep the Settings help text. Optionally the ui lane may show a one-time in-window hint the first time the window is closed, but not a Windows-only native notification. |
| OQ-4 | macOS LaunchAgent: call `launchctl`? | **No.** Write, verify and delete only; the entry takes effect at next login. Use `open -a <bundle> --args --tray` when inside a `.app`. Keep the label `com.tsiger.dialshift`. `SMAppService` (macOS 13+) is a future enhancement. |
| OQ-5 | Should Windows use a separate "Preview" data dir during the transition? | **No.** `DialShift.App` uses `%LOCALAPPDATA%\DialShift` (same format, Version 1), so users keep their data. Do not run WPF and App at the same time: the mutex and the lock file do not see each other. |
| OQ-6 | Stall watchdog: measure from the last LibVLC `TimeChanged` (today) or time outside `Playing` (contract)? | Time outside `Playing`. Adapters must report `Buffering` when progress stops (contract rule). CT-PB-16 is marked as a normalization. |
| OQ-7 | Wake settle delay? | 2 s (within the brief's 1–3 s), monotonic and tick-driven. |
| OQ-8 | What does a second launch do when the primary does not respond? | Exit code 2 plus a log line, no dialog. A hung primary is a bug to fix, not a UX path. |
| OQ-9 | Avalonia 12.1.3 is also on NuGet. | Pin 12.1.2 per D6 unless a lane shows a needed 12.1.3 fix. Record any change in `docs/decisions.md`. |
| OQ-10 | Delete slot has no confirmation, while station delete does. | Add a confirmation (UX gate: no destructive action without confirmation). Record the behavior change in BHV-58. |
| OQ-11 | Tray "Play / Pause" and "Next station" do not save. | Save after every tray command, as the window buttons do. |
