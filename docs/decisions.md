# Decision log — single-codebase refactor + schedule timezones

ADR-lite record of the decisions taken to execute `docs/single-codebase-refactor.md` (brief 1) and `docs/schedule-timezone-research.md` (brief 2). Each entry: what was decided, why, and where the briefs raise it. A decision changes only by adding a new entry that supersedes it. The acceptance matrix (`docs/acceptance-matrix.md`) tracks verification of every decision that has a testable consequence.

| ID | Date | Decision | Brief ref |
|---|---|---|---|
| D1 | 2026-09-25 | Windows uses the Avalonia Fluent dark theme; WPF-native styling is dropped | brief 1 §13 Q1, §10 |
| D2 | 2026-09-25 | Intel Mac policy: Apple Silicon only | brief 1 §7.2, §13 Q2, §4.3 |
| D3 | 2026-09-25 | NSWorkspace wake notification is adopted only if the spike meets all five §4.4 criteria | brief 1 §13 Q3, §4.4, §7.3 |
| D4 | 2026-09-25 | `.icns` app icon is part of this work | brief 1 §13 Q4, §7.2, §8 |
| D5 | 2026-09-25 | Shared visual baselines come from Avalonia headless screenshots where feasible | brief 1 §10 |
| D6 | 2026-09-25 | Avalonia is bumped to 12.1.2 during the merge | brief 1 §7.2 |
| D7 | 2026-09-25 | Release artifact formats and signing tiers | brief 1 §8 |
| D8 | 2026-09-25 | Parallel validation uses a tag plus the behavior checklist; retirement is CI-gated | brief 1 §12.10–11 |
| D9 | 2026-09-25 | The §7.3 spikes run at the same time as the inventory | brief 1 §7.3, §12.3 |
| D10 | 2026-09-25 | `IPlaybackCoordinator` interface lives in Core, defined contract-first | brief 1 §4.1, §5, §6 |
| D11 | 2026-09-25 | Core logging port `IAppLog`; the App implements the redacted file log | brief 1 §6, §7.9, §11 DoD |
| D12 | 2026-09-25 | All brief 2 §10 timezone recommendations are adopted | brief 2 §10, §4–§7 |
| D13 | 2026-09-25 | `DialShift.App` publishes only `win-x64` and `osx-arm64`; no `osx-x64` artifact is produced any more (supersedes D2's transitional sentence) | brief 1 §7.2, §4.3 |
| D14 | 2026-09-25 | Wake-gap detection needs a sleep-inclusive monotonic clock; per-OS `IMonotonicClock` from the platform lane | brief 1 §4.1a, §4.4 |
| D15 | 2026-09-25 | Wake debounce: 10 s after an accepted wake or a completed recovery | brief 1 §4.4, §5.3 |
| D16 | 2026-09-25 | Engine-command FIFO pump: every queued start is invoked, so the N-th start is session N | brief 1 §5.4–§5.6 |
| D17 | 2026-09-25 | The coordinator owns engine disposal; the composition root must not dispose the engine | brief 1 §5.4, §4.1 |
| D18 | 2026-09-25 | The tick loop and all `Settings` mutation run on the UI thread | brief 1 §4.1, §5.5 |
| D19 | 2026-09-25 | Avalonia 12.1.2 is the version actually used; `NativeMenuItemToggleType` became `MenuItemToggleType` | brief 1 §7.2 |
| D20 | 2026-09-25 | Platform contract namespace is `DialShift.App.Platform` (files in `Platform/Abstractions/`) | brief 1 §4.2, §6 |
| D21 | 2026-09-25 | `--smoke-test` uses an isolated temp data dir; `DIALSHIFT_DATA_DIR` is an absolute-path override | brief 1 §4.5, §7.7 |
| D22 | 2026-09-25 | `LSMinimumSystemVersion` is 14.0; the AVPlayer corpus is verified only on macOS 26.5 | brief 1 §4.3, §7.2, §8 |
| D23 | 2026-09-25 | The engine comes from a single-use `PlaybackEngineFactory`; `IPlaybackEngine` is never registered | brief 1 §4.2, §5.4–§5.6 |
| D24 | 2026-09-25 | Single instance keeps a listening server instance armed, handles at most 2 connections at once, uses `FirstPipeInstance` only on a real bind, and tightens socket permissions on every (re-)bind (SI-D1) | brief 1 §7.5 |
| D25 | 2026-09-25 | `FileAppLog` serializes processes with a lock file in the per-user temp dir, waits at most 250 ms, and never throws (LOG-D1) | brief 1 §7.9, §11 DoD |
| D26 | 2026-09-25 | No track metadata on macOS; LibVLC titles on Windows only for `http://` streams | brief 1 §4.3 step 7, §7.8 |
| D27 | 2026-09-25 | Objective-C interop lives only in `DialShift.App/Interop/` | brief 1 §6, §7.8 |
| D28 | 2026-09-25 | SIGTERM/SIGINT use the normal quit path; an OS-initiated shutdown is never vetoed, and settings are saved synchronously | brief 1 §4.1, BHV-11 |
| D29 | 2026-09-25 | Process exit codes: 0 ok, 1 startup failure, 2 second launch couldn't activate, 3 single-instance channel failure, 4 smoke test failed | brief 1 §7.5; matrix §8.2.4 |
| D30 | 2026-09-25 | `ISystemPowerEvents.Resumed` on macOS is raised on the posting thread (the main thread for real wakes) | brief 1 §4.4; matrix §8.2.2 |
| D31 | 2026-09-25 | The single-instance concurrency limit stays at 2 | brief 1 §7.5 |
| D32 | 2026-09-25 | ATS: only `NSAllowsArbitraryLoadsForMedia`, never blanket `NSAllowsArbitraryLoads` | brief 1 §8; spikes finding 2 |
| D33 | 2026-09-25 | CI uses the v5/v6 action majors | brief 1 §4.5, §8 |
| D34 | 2026-09-25 | The macOS tray keeps one 44×44 alpha-only `tray.png`; the unused `tray@2x.png` is deleted | brief 1 §7.2, §7.6; D4 |
| D35 | 2026-09-25 | `DIALSHIFT_AUDIO_OUTPUT=dummy` is a CI/headless seam, read once in the composition layer and honoured on Windows only | brief 1 §4.5, §7.7; D21, D23 |
| D36 | 2026-09-25 | The macOS artifact label is `native-avplayer`; the build is still ad-hoc signed and not clean-machine tested | brief 1 §4.3, §8; D2, D7, D13 |
| D37 | 2026-09-25 | An activation that arrives before the app subscribes is latched (one pending, coalesced) and replayed (F3) | brief 1 §7.5; HZ-06 |
| D38 | 2026-09-25 | Only lock contention means "already running"; any other lock error is a startup failure (`LockFailed`, exit 1) (F6/F7) | brief 1 §7.5; D29 |
| D39 | 2026-09-25 | Launch at login honors the OS "disabled" switches (Task Manager, launchd); an uncertain check is "not enabled" (F1/F2) | brief 1 §4.2, §7.4; OQ-4 |
| D40 | 2026-09-25 | One redactor, `StreamUrlRedactor`, for the log and both engines; it prefers over-redaction to a leak (F4) | brief 1 §7.9, §11 DoD; D11 |
| D41 | 2026-09-25 | The AVPlayer notification observer is retried at each session start until it registers (F9) | brief 1 §7.8; D27 |
| D42 | 2026-09-25 | LibVLC's same-server, same-path reuse of user-info credentials is accepted and documented (LV-08) | brief 1 §7.8; HS-17 |
| D43 | 2026-09-25 | The Windows time-zone picker offers every region-mapped IANA id, with legacy CLDR ids renamed to current tzdata names (UI-D3, QA-B4) | brief 2 §4.5, §6; D12 |
| D44 | 2026-09-25 | Text buttons get a `MinWidth` with at least 15 % headroom and trim with an ellipsis instead of clipping (UI-D2) | brief 1 §10; QG-03 |
| D45 | 2026-09-25 | Accent (lime) buttons keep dark ink text, 12.7:1 contrast | brief 1 §10; QG-03; D1 |
| D46 | 2026-09-25 | Single instance: a client gone before accept is per-connection (no back-off); the service enforces its own 3-instance bound; the OS limit is the maximum (CT-SI-13; amends D24) | brief 1 §7.5; D24, D31 |
| D47 | 2026-09-25 | Phase 2: a slot with no zone keeps `now`'s `Kind` in `At` (the null path is byte-identical) | brief 2 §4.3, §7.9; QA-B1, QA-N9 |
| D48 | 2026-09-25 | Phase 2 dedup: identity is the zone wall-time key; an older occurrence fires only after a forward wall-clock transition | brief 2 §7.8; QA-N8, CF-02 |
| D49 | 2026-09-25 | Windows `Resumed` is most likely raised on the UI thread, not a separate `SystemEvents` thread (amends D30; NC-02 confirms) | brief 1 §4.4; D30 |
| D50 | 2026-09-25 | Settings recovery never replaces the original without a preserved copy: collision-free `-N` backup names, and `Save` refuses while no copy exists (CF-03/CF-04) | brief 1 §7.9; brief 2 §7 (QA-N3); BHV-03 |
| D51 | 2026-09-25 | macOS bundle layout: only Mach-O files in `Contents/MacOS`, managed files in `Contents/Resources/app` via symlinks; the zip carries no extended attributes and is verified after `ditto` and `unzip`; CI smoke-tests the packaged app (SR-03) | brief 1 §8; D7, D13 |
| D52 | 2026-09-25 | macOS starts with no active display: probe `CVDisplayLink` before Avalonia.Native initializes and fall back to a `SleepLoopRenderTimer` render loop, using Avalonia's private APIs; every Avalonia version change must re-verify it (NX-01) | brief 1 §7.2, §11 DoD; D6, D27 |
| D53 | 2026-09-25 | GitHub Releases from version tags: one reusable build workflow for CI and releases, the csproj `<Version>` as the single source of `MAJOR.MINOR.PATCH`, stable asset names, `-` suffix = pre-release | brief 1 §8; D7, D13, D33, D36, D51 |
| D54 | 2026-09-25 | Slot times: `HH:mm` is canonical and written only by `Scheduler.FormatTime` (invariant); `TryTime` also accepts the culture-written `HH.mm`; `SettingsStore.Load` canonicalizes in memory; `Conflicts` compares parsed times | brief 1 §5 (Core), §7.9; brief 2 §7 (QA-N3) |

---

## D1 — Accept the loss of WPF-native styling

- **Decision:** The consolidated app renders on Windows with the same Avalonia Fluent **dark** theme and DialShift palette as macOS. WPF's custom control templates, the DWM dark title-bar hint (`DialShift/NativeChrome.cs`) and WinForms `NotifyIcon` balloon tips are not carried over.
- **Rationale:** One maintained UI is the whole point of the refactor. The WPF look is already a hand-rolled dark theme with the same colors, so users will see little difference. Font rendering, window chrome, menu placement and tray behavior may still differ between platforms, and that is fine.
- **Brief ref:** brief 1 §13 Q1, §10 ("Loss of WPF-native look"), §7.7.

## D2 — Intel Mac policy: Apple Silicon only (default)

- **Decision:** The consolidated `DialShift.App` has `RuntimeIdentifiers` = `win-x64;osx-arm64`. `DialShift.App` **never** publishes `osx-x64`. Until it is retired on this branch, the legacy `DialShift.Mac` project remains the only macOS Intel artifact, clearly labeled as an **`osx-x64` Rosetta build**. After `DialShift.Mac` is retired, no Intel Mac artifact is shipped, and the README says so explicitly.
- **Rationale:** This is the brief's default and the simplest honest option. AVPlayer runs natively on arm64 with no third-party media runtime. Shipping AVPlayer on `osx-x64` would need separate Intel validation that nobody has asked for. The honest-labeling rule (§4.3) is met because the macOS path is either "native `osx-arm64` (AVPlayer)" or the labeled legacy Rosetta build, never an unlabeled one.
- **Brief ref:** brief 1 §7.2 (Intel Mac release policy table, "Apple Silicon only — Default"), §13 Q2, §4.3, §2 transition note.
- **Amended by D13:** `DialShift.Mac` has since been relocated into `DialShift.App`, so the transitional sentence about a labeled legacy `osx-x64` Rosetta artifact no longer applies. No Intel artifact is produced; the last Intel/Rosetta build exists only at tag `legacy-last-known-good`.

## D3 — NSWorkspace wake: spike-gated, timer-gap otherwise

- **Decision:** `NSWorkspace.DidWakeNotification` is tried in a spike. `MacPowerEvents` adopts it only if **all five** §4.4 criteria hold:
  1. It builds without making `DialShift.App` a macOS-only target.
  2. It fires after lid-close and after normal sleep/wake.
  3. It does not retain or leak observer handles after shutdown.
  4. It is fully isolated behind `MacPowerEvents`.
  5. The timer-gap recovery still works if registration fails.

  If any criterion fails, the first consolidation uses the monotonic timer-gap heuristic alone, and native wake becomes a separate enhancement. The timer-gap fallback stays on both platforms either way.
- **Rationale:** Native wake is faster and more reliable than gap detection, but it must not block unification. Lid-close wake can only be verified on real hardware (see "Remaining native checks" in the acceptance matrix).
- **Brief ref:** brief 1 §13 Q3, §4.4, §7.3.

## D4 — `.icns` app icon is in scope

- **Decision:** This work produces the macOS bundle `.icns` (`CFBundleIconFile`), the Windows `.ico` (tray icon and executable), and a monochrome macOS menu-bar **template** image (`MacOSProperties.IsTemplateIcon="True"`).
- **Rationale:** The brief treats icons as release packaging, not optional polish. They are also part of the UI/UX quality gate.
- **Brief ref:** brief 1 §13 Q4, §7.2 (icon assets), §7.6 (tray asset rule), §8.

## D5 — Visual baselines via Avalonia headless screenshots

- **Decision:** Visual regression uses **Avalonia headless rendering**: main window pages, the station editor, the schedule editor, and the compact size, captured to PNG in CI where feasible. There is one shared baseline set because the UI code is shared. Differences in native look (fonts, chrome, tray rendering) between Windows and macOS are accepted and are not tracked as regressions.
- **Rationale:** This replaces the `RenderTargetBitmap` captures in WPF `SmokeChecks.cs` without needing a native desktop session in CI.
- **Brief ref:** brief 1 §10 ("shared visual-regression baselines where feasible"), §4.5.

## D6 — Avalonia 12.1.2

- **Decision:** During the merge, `Avalonia`, `Avalonia.Desktop` and `Avalonia.Themes.Fluent` move from 11.3.22 to **12.1.2**. On 2026-09-25 the NuGet flat-container index lists `12.1.2`, so no fallback is needed. The index also lists `12.1.3`; the brief's version is pinned unless a lane shows that a 12.1.3 fix is needed, and any such change is recorded as a new decision. The actual version used is recorded in the acceptance matrix row for the App project.
- **Rationale:** The brief asks for the bump. Doing it during the merge means breaking API changes (tray, `NativeMenu`, headless) are absorbed once.
- **Brief ref:** brief 1 §7.2 ("bump to 12.1.2 during the merge").

## D7 — Artifact format and signing

- **Decision:**
  - **Windows:** the artifact starts as a `.zip` of the `win-x64` publish directory. MSIX or an installer comes later.
  - **macOS:** a `.app` bundle with `LSUIElement=true` and no VLC dylibs, zipped.
  - **Development builds:** unsigned on Windows, ad-hoc signed on macOS.
  - **Public release:** requires **Authenticode** on Windows and **Developer ID signing + notarization** on macOS. These are documented as release requirements and are **not performed** in this work.
- **Rationale:** Signing needs credentials and accounts that this environment does not have. Ad-hoc signing is enough for local and CI verification. SmartScreen and Gatekeeper behavior is a listed native check.
- **Brief ref:** brief 1 §8 (artifact format, signing tiers).

## D8 — Parallel validation via tag + checklist; retirement gated on CI

- **Decision:** The §12.10 parallel-validation phase is satisfied by:
  - the annotated tag **`legacy-last-known-good`**, pointing at commit `82281e5` and pushed to the `private` remote;
  - the behavior checklist in `docs/acceptance-matrix.md`.

  WPF (`DialShift/`) and `DialShift.Mac/` are retired **only after**:
  - GitHub Actions on the private remote (`windows-latest` + `macos-latest`) shows compile, test and publish green;
  - the headless smokes are green.

  Native Windows UI smoke stays open as a listed native check. It is not faked.
- **Rationale:** Keeping WPF alive for a release cycle would mean maintaining two UIs, which is what the refactor removes. The tag keeps a known-good rollback point.
- **Brief ref:** brief 1 §12 steps 9–11, §4.5, §11 ("legacy WPF/WinForms app is removed only after equivalent checks pass").
- **Status (2026-09-25):** the retirement was executed in `0d0e524`: `DialShift/` deleted, `DialShift.slnx` = Core, Tests, App (DOD-01, DOD-10). The gate was met by CI run `36100367406` at `4f22dd0`: compile, test and publish green on both runners, and the app's native smoke (`--smoke-test --recovery-test`) 34/34 on `windows-latest` and `macos-latest`, which covered every legacy `SmokeChecks` check (matrix §4). That smoke stood in for the "headless smokes" condition. The headless UI suites (HS-01..08, HS-13, HS-14) landed afterwards (`bafffc0`) and are green on both OSes (CI `36108959649`). "Native Windows UI smoke stays open" is **superseded** by the 34/34 CI smoke on `windows-latest`: it ran the real window, tray and LibVLC playback in the runner's desktop session. What stays open as native checks (matrix §9) is what a hosted runner cannot do: real-hardware sleep/wake and `SystemEvents` delivery (NC-02, NC-08), the tray at a real login (NC-04, NC-10), a real click on the tray and focus rules (NC-01, NC-06), and audible output (NC-01, NC-03).

## D9 — Spikes run concurrently with the inventory

- **Decision:** The three §7.3 spikes (Apple Silicon AVPlayer with its two checkpoints, Windows `SystemEvents`, macOS wake notification) run at the same time as the behavior inventory and contract work.
- **Rationale:** The spikes touch only spike/scratch files, not shared sources or docs. The brief requires them before code movement, not before the inventory.
- **Brief ref:** brief 1 §7.3, §12 step 3.

## D10 — `IPlaybackCoordinator` in Core, contract-first

- **Decision:** `DialShift.Core/Playback/Contracts/IPlaybackCoordinator.cs` defines the async, UI-agnostic coordinator surface, together with `PlaybackSnapshot`/`PlaybackStatus`, `IPlaybackEngine`, `IClock`/`IMonotonicClock` and `IAppLog`. These are written **before** any implementation.
- **Consequence:** The UI and view-model lane, the test lane and the core lane can work in parallel. The UI builds against a fake coordinator. Tests build against a fake `IPlaybackEngine` and fake clocks. The core lane implements `PlaybackCoordinator` against the same contract.
- **Rationale:** Brief 1 §14 says the state transitions, cancellation ownership and stale-operation handling must be defined before code moves.
- **Brief ref:** brief 1 §4.1, §5, §6, §14.

## D11 — `IAppLog` logging port

- **Decision:**
  - Core depends only on `IAppLog` (`Info`/`Warn`/`Error` with an event name) and ships `NullAppLog`. Core never computes a path or writes a file.
  - `DialShift.App` implements a thread-safe, never-throwing, **redacted** JSON-lines file log at `<DataDirectory>/dialshift.log` with size-based rotation. The redaction rules are in the acceptance matrix "Contracts" section.
- **Rationale:** This keeps Core pure (§6) while meeting the DoD logging requirement: version, RID, engine, transitions, startup-registration and wake outcomes, and recoverable failures, without credentials or full private URLs.
- **Brief ref:** brief 1 §6 (purity), §7.9, §11 DoD.

## D12 — Timezone recommendations adopted

- **Decision:** All six brief 2 §10 recommendations are adopted:
  1. A zone **per schedule entry** (`ScheduleEntry.TimeZone`, `string?`).
  2. The picker defaults to **"Local time"**, which stores `null` (the zero-conversion path, QA-B1).
  3. Only **IANA** ids are stored, canonicalized at the boundary with `TryConvertWindowsIdToIanaId`. The code never compares against `TimeZoneInfo.Local.Id` (QA-B4).
  4. `localZone` is **injected** into `Scheduler.Evaluate` and threaded through `ScheduleSession`, with `SpecifyKind(Unspecified)` and the 3-arg `ConvertTime` (QA-B2).
  5. **"UP NEXT" shows the zone label** for zoned slots (QA-N6).
  6. A zone or OS-tzdata change **requires a restart**, and this is documented (QA-N4/QA-N8).

  `Settings.Version` stays at 1 (QA-N3).
- **Rationale:** These are the brief's own recommendations. The QA review found they are needed for correctness or backward compatibility.
- **Brief ref:** brief 2 §10 Q1–Q6, §4.2–§4.5, §6, §7.

## D13 — Only `win-x64` and `osx-arm64` are published

- **Decision:** `DialShift.App` publishes exactly two artifacts: `win-x64` and `osx-arm64`. No `osx-x64` artifact is produced any more, by any project on this branch. The last Intel/Rosetta macOS build exists only at tag `legacy-last-known-good` (commit `82281e5`), and the README says so explicitly. This supersedes D2's transitional sentence ("until it is retired … the legacy `DialShift.Mac` project remains the only macOS Intel artifact"), because `DialShift.Mac` was relocated into `DialShift.App` (commit `2f0ef5c`) and retargeted (commit `d83f946`).
- **Rationale:** After the relocation there is no project left that could build the labeled Rosetta artifact without reintroducing LibVLC on macOS. Pointing Intel users at the tag keeps the honest-labeling rule (§4.3) without maintaining a second macOS build.
- **Consequence:** PK-01 and HS-15 assert that `DialShift.App.csproj` contains no `osx-x64`. NC-14 is not applicable. DOD-08 references this decision.
- **Brief ref:** brief 1 §7.2, §4.3, §13 Q2.

## D14 — Wake-gap detection uses a sleep-inclusive monotonic clock

- **Decision:** The tick-gap wake heuristic needs a clock that is monotonic **and** keeps counting while the machine sleeps. The platform lane supplies per-OS `IMonotonicClock` implementations in `DialShift.App`, and the composition root injects them into `PlaybackCoordinator`:
  - **macOS:** `clock_gettime_nsec_np(CLOCK_MONOTONIC)`. `CLOCK_MONOTONIC_RAW` (the spike's choice, the same as `mach_continuous_time`) is equally sleep-inclusive and acceptable.
  - **Windows:** a sleep-inclusive source, to be verified on native Windows hardware (whether `Stopwatch`/QPC counts through sleep is unverified; NC-02 covers it).
  - `StopwatchMonotonicClock` remains the Core default for tests and for contexts that do not need to observe sleep.
- **Evidence:** .NET's `Stopwatch` on macOS reads `CLOCK_UPTIME_RAW`, which does not advance during sleep. Measured on the dev Mac on 2026-09-25: `CLOCK_MONOTONIC − CLOCK_UPTIME_RAW = 548,087 s` of accumulated sleep missing from `Stopwatch` (`CLOCK_MONOTONIC_RAW − CLOCK_UPTIME_RAW = 548,104 s`; see `docs/spikes.md` finding 1). With `Stopwatch`, a tick before sleep and a tick after wake are about 1 s apart, so a 15 s gap is never seen.
- **Consequence:** One clock drives every coordinator timer. That is safe with a sleep-inclusive clock, because a detected wake retires the session and clears the retry, stall and stable timers before they are evaluated in the same tick. The OS wake notification (D3, spike verdict ADOPT) and the tick gap both stay in place. CT-PB-30 is unaffected because it uses a fake clock. The contract remark lives on `IMonotonicClock`.
- **Brief ref:** brief 1 §4.1a, §4.4, §7.3.

## D15 — Wake debounce of 10 s

- **Decision:** A wake detected within **10 s** (`PlaybackCoordinator.WakeDebounce`), measured on the monotonic clock, of the last accepted wake **or** of the last completed recovery is ignored and logged as `wake.detected … ignored: rate-limited`. A wake while `SuspendedBySystem` is ignored as "recovery already in progress".
- **Rationale:** The OS notification and the tick gap usually both report the same wake. Restarting the window when the recovery completes means a late duplicate cannot trigger a second reconnect. 10 s is shorter than the 15 s gap threshold, so a genuine new sleep is never swallowed by the tick-gap path.
- **Brief ref:** brief 1 §4.4 ("idempotent, rate-limited"), §5.3.

## D16 — Engine-command FIFO pump

- **Decision:** Engine commands (start, stop, set volume) are appended to a FIFO queue while the state gate is held and invoked strictly in that order after it is released, by at most one pumping thread. The pump does not await a command's completion before invoking the next one, so a stop reaches the engine immediately even while a slow connect is in flight (stop-while-connecting). **Every** queued start is invoked, and a superseded one receives a token that is already cancelled, or is cancelled right after the superseding transition completes. Because the engine increments its session counter synchronously on entry to each `StartAsync`, the N-th queued start is always session N, and the coordinator assigns that id when it enqueues. A start that throws synchronously still consumes its id and becomes a `Failed(Unknown)` signal for that session.
- **Consequence:** Adapters must tolerate overlapping calls, as documented on `IPlaybackEngine`. This resolves OQ-1 without a caller-supplied correlation id.
- **Brief ref:** brief 1 §5.4–§5.6.

## D17 — The coordinator owns engine disposal

- **Decision:** `PlaybackCoordinator` takes ownership of the `IPlaybackEngine` it is constructed with. `DisposeAsync` stops the engine, unsubscribes and disposes it exactly once (CT-PB-35). The composition root must **not** dispose the engine itself; it disposes the coordinator.
- **Rationale:** Disposal is ordered after the final stop and after every queued command. A second owner could dispose the engine while commands are still being pumped.
- **Brief ref:** brief 1 §4.1, §5.4 ("shutdown").

## D18 — Tick loop and `Settings` mutation on the UI thread

- **Decision:** The 1 s `PeriodicTimer` loop that calls `OnTickAsync`, all coordinator commands, and every mutation of `Settings` run on the UI thread. The coordinator awaits its gate without `ConfigureAwait(false)`, so those transitions run on the caller's context. `NotifyWakeAsync`, `Snapshot` and `DisposeAsync` may be called from any thread, and engine callbacks run on arbitrary threads, because they read only scalar settings (`Volume`, `ScheduleEnabled`) and never enumerate `Settings` collections.
- **Rationale:** `Settings` is plain mutable data (`List<Station>`, `List<ScheduleEntry>`) that the editors change in place. Confining its readers and writers to one thread avoids locks in the data model and "collection was modified" races during schedule evaluation.
- **Brief ref:** brief 1 §4.1, §5.5.

## D19 — Avalonia 12.1.2 actually used

- **Decision:** `DialShift.App` references `Avalonia`, `Avalonia.Desktop` and `Avalonia.Themes.Fluent` **12.1.2**, which completes D6 and PK-06. The one API break met during the bump was `NativeMenuItemToggleType`, which became `MenuItemToggleType` (used for the tray's "Follow schedule" checkbox).
- **Brief ref:** brief 1 §7.2.

## D20 — Platform contract namespace

- **Decision:** The platform contracts `IStartupRegistration`, `ISystemPowerEvents` and `IFileRevealService` live in `DialShift.App/Platform/Abstractions/` under the namespace **`DialShift.App.Platform`** (not `…Platform.Abstractions`). Implementations go in per-OS folders under `Platform/` in the same namespace family.
- **Brief ref:** brief 1 §4.2, §6.

## D21 — Smoke-test data isolation and `DIALSHIFT_DATA_DIR`

- **Decision:** `AppPaths.Resolve` applies this order: (1) a non-blank `DIALSHIFT_DATA_DIR`, which must be an absolute path and is used verbatim; (2) with `--smoke-test` and no override, a fresh `DialShift-smoke-<guid>` directory under the temp path; (3) the OS default. `--smoke-test` therefore never touches the user's real settings, log or single-instance lock. The override exists for smoke tests, integration tests and CI, and is documented only in the README developer section.
- **Brief ref:** brief 1 §4.5, §7.7; acceptance matrix §8.2.6.

## D22 — Minimum macOS 14.0

- **Decision:** The bundle's `Info.plist` sets `LSMinimumSystemVersion` = **14.0** (`scripts/build-mac-app.sh`, `MIN_MACOS`), and `scripts/verify-mac-app.sh` asserts it in CI.
- **Rationale:** 14.0 matches upstream v0.2.0. The only AVPlayer evidence, the spike and the production adapter corpus in `docs/spikes.md`, comes from **macOS 26.5**. AVFoundation's format support depends on the OS version, and Ogg Vorbis, Opus, FLAC-in-Ogg and `.aacp` were only seen working on 26.5. Nothing in between 14.0 and 26.5 has been tested.
- **Consequence:** Before release notes list formats beyond MP3/AAC/HLS, the corpus must be re-run on macOS 14 (NC-16). Raising the minimum later is a new decision.
- **Brief ref:** brief 1 §4.3, §7.2, §8; `docs/spikes.md` finding 5.

## D23 — Single-use `PlaybackEngineFactory`; the engine is never a service

- **Decision:** `AddDialShiftPlayback` registers only a singleton `PlaybackEngineFactory` (`DialShift.App/Services/PlaybackServices.cs`). **No `IPlaybackEngine` is registered.**
  - The `IPlaybackCoordinator` registration in `AppComposition` is the only caller of `Create()`.
  - A second `Create()` throws `InvalidOperationException`.
  - Windows gets `LibVlcPlaybackEngine`, macOS gets `MacAvPlayerPlaybackEngine`, and any other OS throws `PlatformNotSupportedException`.
- **Rationale:** The coordinator maps its N-th queued start to engine session N (D16), so it needs an engine whose counter has never moved. An engine that was resolved and started anywhere else would have every event discarded as stale and would never play (hazard HZ-01). The container never holds the engine, so disposing the provider can't dispose it a second time, and D17 ownership holds by construction.
- **Consequence:** The "fresh engine" rule is part of the `IPlaybackEngine` contract remarks. `EngineName` feeds the `app.start` log line (HS-10).
- **Brief ref:** brief 1 §4.2, §5.4–§5.6; D16, D17.

## D24 — Single-instance server: always armed, bounded, bind-aware (SI-D1)

- **Decision:** `SingleInstanceService` (`DialShift.App/SingleInstance/SingleInstanceService.cs`):
  - Keeps one listening server instance **armed** at all times. The next instance is created *before* an accepted connection is handed to its handler, so the name never has zero instances between connections.
  - Handles at most `MaxConcurrentConnections` = **2** connections at once, each off the accept loop. `MaxServerInstances` is 3: two handled connections plus the armed instance.
  - Adds `PipeOptions.FirstPipeInstance` **only when it really binds the name**, meaning none of its own instances is alive. Joining instances omit it, because a second first-instance create fails on Windows and throws on net10.0 Unix while the name is served.
  - After **every** create or re-bind, inspects the Unix socket file, removes group/other bits, and logs the observed mode once (`single_instance.socket`).
  - Recovers a leftover socket from a crashed primary: a probe connect, then delete and retry once.
- **Rationale:** This fixes SI-D1. On Unix all instances share one ref-counted listening socket, so disposing the last instance between connections closed it, and a client that connected in that gap was dropped. With one armed instance, a stalled client (held for at most the 2 s read timeout) can't block a real activation.
- **Consequence:** This supersedes the `maxNumberOfServerInstances: 1` option in matrix §8.2.4. It is tested by the SI-D1 check (5 of 5 activations served during teardown), CT-SI-12 (0600 after start and after a re-bind), and the stale-socket checks.
- **Brief ref:** brief 1 §7.5; matrix §8.2.4, CT-SI-*.
- **Amended by D46:** the 3-instance bound is enforced by the service itself (its handler slots), and the `maxNumberOfServerInstances` passed to the OS is `NamedPipeServerStream.MaxAllowedServerInstances`. A client that leaves before its connection is accepted is a per-connection failure with no back-off.

## D25 — Cross-process log lock (LOG-D1)

- **Decision:** `FileAppLog` (`DialShift.App/Services/FileAppLog.cs`) runs each size check, rotation and append under two locks:
  - an in-process lock shared by every instance writing to the same file;
  - a cross-process lock: `<per-user temp>/DialShift-log-<16 hex of SHA-256(log path)>.lock`, opened with `FileShare.None` (`flock` on Unix, a sharing violation on Windows). The file is never deleted.

  A writer waits at most **250 ms** (`LockWaitBudget`). After that it assumes the holder is stuck and appends without the lock and **without rotating**. If the lock file can't be opened at all, it writes at once and still rotates. Logging **never throws**.
- **Rationale:** This fixes LOG-D1. A second launch logs to the same file while the primary runs. .NET's `FileMode.Append` is not an atomic append, so two processes overwrote each other's lines, and two rotations could lose `dialshift.log.1`. The lock file lives in the temp dir because the data directory holds only the files §8.2.6 lists.
- **Consequence:** Tested by LOG-D1: two processes write 4 000 lines with none lost or torn, and under rotation each process's kept lines are consecutive. Also by the §8.2.7 never-throws checks.
- **Brief ref:** brief 1 §7.9, §11 DoD; matrix §8.2.7.

## D26 — No track metadata on macOS; `http://` only on Windows

- **Decision:** `MacAvPlayerPlaybackEngine` does **not** implement `ITrackMetadataProvider`, so macOS always shows the station tag (BHV-32). `LibVlcPlaybackEngine` implements it by polling `Meta(NowPlaying)`, but titles only arrive for **`http://`** streams.
- **Rationale:** In the adapter harness, `AVPlayerItemMetadataOutput` delivered `icy`/`StreamTitle` reliably only for a Shoutcast v2 server, and never for Icecast MP3/AAC or HLS. A title that appears on a few stations and silently goes missing on most would be worse UX than a consistent tag. LibVLC 3's `https://` access module does not send `Icy-MetaData`, so an https station shows its tag.
- **Consequence:** The README and release notes must state both limits (PK-07, owned by the release lane). The contract remark is on `ITrackMetadataProvider`. CT-PB-37 already covers "no provider → tag".
- **Finding (HS-17 harness, `docs/spikes.md`):** even over `http://`, LibVLC 3.0.4 requested a plain `HTTP/1.0 200` stream without `Icy-MetaData`, so no title arrived. Only a Shoutcast v1 `ICY 200 OK` reply made LibVLC retry through its legacy HTTP module, which sent `Icy-MetaData: 1` and delivered the `StreamTitle`. On Windows, titles therefore arrive only from servers that answer `ICY 200 OK`; other stations show the tag. The README says so. The decision itself is unchanged. HS-17 LV-04 checks the `ICY 200 OK` path on `windows-latest`, and NC-03 records the behavior of public Icecast stations on LibVLC 3.0.23.1.
- **Brief ref:** brief 1 §4.3 step 7, §7.8; `docs/spikes.md` capability differences.

## D27 — One home for Objective-C interop

- **Decision:** Every Objective-C declaration lives in `DialShift.App/Interop/`:
  - `ObjCRuntime`: libobjc, the typed `objc_msgSend` entry points, and autorelease pools;
  - `AVFoundation`: selectors and constants;
  - `MacMainQueue`: `dispatch_async_f` to the main queue;
  - `NotificationObserver`: the one runtime class `DialShiftNotificationObserver`, shared by the AVPlayer adapter and `MacPowerEvents`.

  Nothing else declares `objc_msgSend`, selectors or runtime classes. The only other P/Invoke in the App is the libSystem `clock_gettime_nsec_np` in `MacMonotonicClock`, which is not Objective-C.
- **Rationale:** Each `objc_msgSend` entry point must match its native prototype exactly on arm64. One copy means one place to review ABI signatures, ownership (+1/+0) and the process-global class registration. The platform lane had duplicated the interop (fixed in `20a398a`).
- **Brief ref:** brief 1 §6, §7.8; `docs/spikes.md` production guidance.

## D28 — Signals and OS shutdown

- **Decision:** SIGTERM (`kill`, `launchctl bootout`) and SIGINT (Ctrl+C) are registered with `PosixSignalRegistration`. They cancel the default termination and post the normal `Quit()` to the UI thread, which runs the full BHV-11 teardown. A `ShutdownRequested` that the app did not start (the app menu's Quit, or the OS ending the session) is **never cancelled**. Avalonia 12 does not say which of the two it is, and cancelling would veto a macOS logout. In that case settings are saved **synchronously**, `app.exit … reason=shutdown_requested` is logged, and the process exits without the asynchronous playback teardown. Where a signal can't be registered, that is logged (`app.signal_unavailable`) and startup continues.
- **Rationale:** A launch-at-login agent can be stopped by launchd, and a user's settings must survive both that and a logout. A clean quit must never block the OS.
- **Brief ref:** brief 1 §4.1; BHV-11; `DialShift.App/App.axaml.cs`.

## D29 — Process exit codes

- **Decision:** `DialShift.App/LaunchOptions.cs` `ExitCodes` defines the process exit codes:

  | Code | Constant | Meaning |
  |---|---|---|
  | 0 | `Success` | Normal quit, or a second launch whose activation was acknowledged |
  | 1 | `StartupFailed` | Startup failed and the startup-failure dialog was shown (also used for a bad `DIALSHIFT_DATA_DIR` before any UI, and for an exception that escapes the UI toolkit) |
  | 2 | `ActivationFailed` | A second launch whose activation was rejected or not answered |
  | 3 | `SingleInstanceFailed` | The lock was acquired but the activation channel could not start |
  | 4 | `SmokeTestFailed` | A `--smoke-test` run in which at least one check failed, or the smoke watchdog fired (`results.json` says which) |

- **Rationale:** Scripts, CT-SI-03 and the smoke harness need distinct, stable outcomes. Codes 0–3 were verified against the source at `82a9900`. Code 4 was added with the native smoke runner (`4f22dd0`); CI fails the smoke step on any non-zero exit.
- **Brief ref:** brief 1 §7.5; matrix §8.2.4 (the exit-code table there is updated to match).

## D30 — Thread of `Resumed`

- **Decision:** `MacPowerEvents` raises `Resumed` **synchronously on the thread that posted the notification**. For a real wake, AppKit posts `NSWorkspaceDidWakeNotification` on the main thread, which is the Avalonia UI thread. `WindowsPowerEvents` raises it on the `SystemEvents` thread. The `ISystemPowerEvents` contract still says "arbitrary thread": consumers must return quickly and must not block. The App forwards it to `NotifyWakeAsync()`, which is safe from any thread (D18).
- **Rationale:** This was measured in the spike (N1 and N1b) and asserted by HS-16. Documenting it avoids a needless re-dispatch, while keeping the contract portable.
- **Brief ref:** brief 1 §4.4; matrix §8.2.2.
- **Amended by D49:** the Windows half ("on the `SystemEvents` thread") is probably wrong. `Start()` subscribes from the STA UI thread, and `SystemEvents` then creates its hidden window on that thread, so `Resumed` most likely arrives on the UI thread. NC-02 records the actual thread. The macOS half stands.

## D31 — The single-instance concurrency limit stays at 2

- **Decision:** `MaxConcurrentConnections` stays **2**. It is not raised and not made configurable.
- **Rationale:** Both server and client use `PipeOptions.CurrentUserOnly`. On Unix the socket is also 0600 in a per-user `$TMPDIR` (D24). Only the same user can connect, and that user could simply kill the process, so a larger pool would defend against nothing. Two slots already guarantee that one stalled client (held for at most 2 s) can't block a real activation. Further clients wait in the backlog.
- **Brief ref:** brief 1 §7.5; D24.

## D32 — ATS media exception only

- **Decision:** The bundle's `Info.plist` has `NSAppTransportSecurity` → `NSAllowsArbitraryLoadsForMedia` = true and **never** `NSAllowsArbitraryLoads`. `scripts/verify-mac-app.sh` asserts both in CI.
- **Rationale:** Without the media exception, AVPlayer inside a `.app` fails every cleartext `http://` stream at once (`-1022`). That is 43 % of the catalog (`docs/spikes.md` finding 2). A blanket exception would also open cleartext for every non-media request, which the app does not need.
- **Brief ref:** brief 1 §8; `docs/spikes.md` finding 2, corpus T16.

## D33 — CI action majors

- **Decision:** `.github/workflows/ci.yml` uses `actions/checkout@v5`, `actions/setup-dotnet@v5`, `actions/cache@v5` and `actions/upload-artifact@v6`, pinned by major version.
- **Rationale:** These are the current majors, which run on GitHub's newer Node runtime instead of the Node 20 runtime that is being retired. Pinning by major takes fixes without surprise breaking changes.
- **Brief ref:** brief 1 §4.5, §8; DOD-03.

## D34 — One 44×44 template tray image

- **Decision:** Keep the single **44×44, alpha-only** `DialShift.App/Assets/tray.png` as the macOS menu-bar template image. The unused `tray@2x.png` is deleted (`554228a`, merged in `82a9900`).
- **Rationale:** The release lane disassembled `AvnTrayIcon::SetIcon` in `libAvaloniaNative` 12.1.2. Avalonia takes one PNG, sizes it to `floor(menuFont.pointSize × 1.3333)` pt (17 pt, which is 34 px on a Retina display here), and marks it as a template. An `@2x` file is never used. Downscaling a 44 px source to 34 px stays sharp, while a 22 px source would be upscaled and blurry.
- **Consequence:** Menu-bar rendering in light and dark is still verified natively (NC-12). The tray icons are Avalonia resources compiled into the app, so there is no loose file for HS-15 to find. HS-03 shows they load on both OSes (matrix PK-04).
- **Brief ref:** brief 1 §7.2, §7.6; D4.

## D35 — `DIALSHIFT_AUDIO_OUTPUT=dummy` is a CI/headless seam

- **Decision:** `DIALSHIFT_AUDIO_OUTPUT` is read **once**, in the composition layer: `AddDialShiftPlayback` builds `PlaybackEngineOptions.FromEnvironment` when the `PlaybackEngineFactory` is first resolved (`DialShift.App/Services/PlaybackServices.cs`). The engines never read the environment.
  - Only `dummy` (trimmed, any case) has an effect, and **only on Windows**. `PlaybackEngineFactory.Create()` then gives `LibVlcPlaybackEngine` the `adummy` audio output (`--aout=adummy`), which decodes and discards audio, and logs `playback.audio_output` at info level.
  - On macOS the value is ignored and logged at info level: AVPlayer always uses the system output.
  - Any other value is ignored and logged as a `playback.audio_output` warning. The module name is never taken from the variable, so it can't inject LibVLC options.
  - It is for developer, CI and smoke runs only, and is documented in the README developer section, like `DIALSHIFT_DATA_DIR` (D21).
- **Rationale:** A hosted Windows runner is not guaranteed to have a working audio device, and LibVLC's playback depends on its audio output opening. The seam makes HS-17 LV-01..LV-11 and the Windows native smoke independent of the runner's audio hardware, without a test-only branch in the engine. Nothing is audible on `adummy`, and the native volume can't be read back, so audible output and mute at volume 0 stay native checks (NC-01, NC-03).
- **Consequence:** CI sets the variable for the Windows smoke step (`.github/workflows/ci.yml`), and `LibVlcEngineTests` uses the same factory path (LV-01 asserts the `playback.audio_output` line). The Windows smoke at `4f22dd0` passed before the seam existed, so its playback ran on the runner's default output; later runs use `adummy`.
- **Brief ref:** brief 1 §4.5, §7.7; D21, D23.

## D36 — The macOS artifact label is `native-avplayer`

- **Decision:** `MACOS_LABEL` is `native-avplayer` in `.github/workflows/ci.yml`, and it is the default in `scripts/build-mac-app.sh`. The macOS artifact is `DialShift-osx-arm64-native-avplayer.zip`, replacing the earlier `preview` label. The README names the build "Native `osx-arm64` (AVPlayer)".
- **Basis:** SP-01 is green (the production-adapter corpus on macOS 26.5 arm64), SP-02 checkpoint B passed, and the native smoke passed 34/34 on `macos-latest` with `rid=osx-arm64 arch=Arm64 engine=MacAvPlayerPlaybackEngine` (CI `36100367406`).
- **Limits:** the label says what the build **is**: native arm64 with AVPlayer and no LibVLC. It does not say the build is release-ready. The build is still ad-hoc signed and not notarized (D7, NC-09), and it has not been tested on a clean machine (NC-07). The README says both. It changes when NC-07 passes, and again when the first release is signed.
- **Brief ref:** brief 1 §4.3 (honest labeling), §8; D2, D7, D13; DOD-08.

## D37 — Latch an early activation (F3)

- **Decision:** `SingleInstanceService` starts listening in `Program.Main`, before Avalonia exists. The app subscribes to `ActivationRequested` only once the desktop lifetime starts. An `activate` that arrives while nobody is subscribed is:
  - acknowledged `ok`;
  - kept as **one** pending activation, with later ones coalescing into it;
  - raised for the next subscriber, and only that one.

  The log says `single_instance.activated … queued`, then `single_instance.activation_replayed`. A delivered activation logs `… delivered`. After disposal nothing is kept or raised, and a late message is answered `rejected`.
- **Rationale:** Previously a second launch in the 100–200 ms startup window got an acknowledgement (it exited 0) but nothing showed the window, which mattered when the first instance started with `--tray` (HZ-06). A latch fixes that without starting the UI toolkit earlier.
- **Consequence:** HZ-06 changes from "accepted" to fixed. Covered by the F3 checks in the `SingleInstance` suite. The replay goes through the normal activation path, so `ShowWindow` runs on the UI thread as before.
- **Brief ref:** brief 1 §7.5; matrix §8.2.4, HZ-06.

## D38 — Lock contention vs lock failure, and their exit codes (F6/F7)

- **Decision:** `TryStartPrimary` returns `AlreadyRunning` only when another handle holds the lock file:
  - Windows: `IOException.HResult` = `0x80070020` (sharing violation) or `0x80070021` (lock violation);
  - macOS: `flock` `EWOULDBLOCK`, errno 35.

  Any other failure to create or open `<data>/.single-instance.lock` (a file in the way of the data folder, a read-only volume, a full disk, access denied) logs `single_instance.lock_failed` and returns `LockFailed`. The app then shows the startup-failure dialog ("It couldn't create its lock file in <data folder>…") and exits **1** (`StartupFailed`). A lock that was acquired but whose activation pipe failed stays `Failed`, exit **3**. The D29 table is unchanged. The lock-failure case is added to code 1.
- **Rationale:** Previously every `IOException` counted as contention. A broken data folder therefore sent the process down the second-launch path: it tried to activate a copy that wasn't running and exited 2 with no dialog. The user got no explanation and the log didn't show the cause.
- **Consequence:** Covered by the F6 checks (a read-only data folder gives `LockFailed` on macOS; the Windows variant is skipped because it needs an ACL denial) and by HS-08 (the startup-failure dialog and exit codes).
- **Brief ref:** brief 1 §7.5; D29; matrix §8.2.4.

## D39 — Launch at login honors the OS "disabled" switches (F1/F2)

- **Decision:** `IStartupRegistration.GetStatusAsync` reports enabled only if the entry exists, targets the current executable with `--tray`, **and** the OS has not switched it off:
  - **Windows:** Task Manager's Startup apps switch is `HKCU\…\Explorer\StartupApproved\Run`, `REG_BINARY` value `DialShift`. The format is undocumented. Observed: byte 0 with the low bit set (`0x03`, `0x07`) means disabled, and a missing value means enabled. A value that isn't a non-empty `REG_BINARY` is not enabled. `SetEnabledAsync(true)` replaces a disabled or unreadable value with `02` followed by 11 zero bytes (what Task Manager writes on re-enable). `SetEnabledAsync(false)` removes both the `Run` and the `StartupApproved` values.
  - **macOS:** the plist's `Disabled=true` is not enabled. launchd's override is read with `/bin/launchctl print-disabled gui/<uid>`: read-only, `ArgumentList`, 5 s timeout. `=> disabled` (or the legacy `=> true`) is not enabled.
  - **Uncertain means not enabled.** If `print-disabled` can't run, exits non-zero, times out, or doesn't show a readable state for the label, the status is **not enabled**, with a diagnostic that points at System Settings → Login Items. DialShift never shows "on" for something it could not confirm.
  - **`launchctl enable` only removes an override.** `SetEnabledAsync(true)` runs `launchctl enable gui/<uid>/com.tsiger.dialshift`, which clears the disabled override and neither loads nor starts the job. There is still no `bootstrap`/`bootout` (OQ-4), so no second instance is spawned and the entry takes effect at the next login.
- **Rationale:** The checkbox must reflect the OS state (§8.2.1). A switch the user turned off elsewhere is the most common reason an entry "exists" but does not run.
- **Consequence:** The macOS 13+ "Allow in the Background" switch lives in the Background Task Management database, which has no public read API. Whether it shows up in `print-disabled` is confirmed natively by NC-10. The Windows flag semantics are confirmed natively by NC-04. Covered by the F1/F2 checks in `StartupRegistration` (HS-11).
- **Brief ref:** brief 1 §4.2, §7.4; OQ-4; matrix §8.2.1.

## D40 — One redactor for the log and the engines (F4)

- **Decision:** `DialShift.App/Services/StreamUrlRedactor` is the **only** redaction code. `FileAppLog` runs every `msg` and `ex` through it, and both engines run their diagnostics through it before those reach the coordinator. It never throws, runs in linear time (`NonBacktracking` regexes; it is on the log hot path), and prefers over-redaction to a leak. Its passes are URLs (any scheme and case, glued, JSON-escaped, scheme-less `//host`, IPv6, whitespace-split continuations) → `scheme://host[:port]/…`; user-info anywhere; detached queries; secret-named pairs, `Authorization:` headers and `Bearer` tokens; the home directory → `~`.
- **Rationale:** The review (F4) found two redactors with different coverage, and leak shapes (glued, split, escaped) that one of them missed. One implementation tested against one table cannot drift.
- **Consequence:** The case table is `DialShift.Tests/App/RedactionTests.cs` (the `Redaction` suite), including a real-log check of the README's "never contains stream credentials". HS-17 LV-11 checks the real LibVLC diagnostics. Matrix §8.2.7 rule 2 describes the passes.
- **Brief ref:** brief 1 §7.9, §11 DoD; D11.

## D41 — Retry the AVPlayer notification observer (F9)

- **Decision:** `MacAvPlayerPlaybackEngine` registers its single `NotificationObserver` (end, failure-to-play-to-end, stall) at the first session start where the registration succeeds. The engine calls `EnsureObserver` at every session start. A failed registration is disposed, logged as `playback.observer_unavailable`, and retried by the next session. It is never lost for the engine's lifetime.
- **Rationale:** A session without the observer still works on the 250 ms poll, which detects Failed, end of stream and stalls. But it loses the NSError of a mid-stream failure and the immediacy of the stall notification. One transient failure should not degrade every later session.
- **Brief ref:** brief 1 §7.8; D27.

## D42 — LibVLC's credential reuse on the same path is accepted (LV-08)

- **Decision:** LibVLC 3.0.23.1 keeps a station URL's user-info credentials for the life of the LibVLC instance (the app's lifetime). It sends them preemptively with later requests to the same `scheme://host:port` **and path**, even for a station whose URL has none. This is accepted and documented in the README ("Stream passwords"). LV-08 accepts "Playing with the reused credentials" or `HttpError` for the same-path station, and fails on any `Authorization` header sent to another path or realm.
- **Rationale:** This matches how browsers treat a Basic-auth protection space, and it never crosses to another server or path (CI evidence, `windows-latest`). LibVLC 3 has no option that disables or scopes the store; `--keystore` only selects the persistent store. A LibVLC instance per session would reload the plugins at every station change. AVPlayer (macOS 26) reuses credentials only after a 401 from the same server and realm.
- **Consequence:** Removing a password from a station URL keeps playback working until DialShift quits. The README says so for both platforms.
- **Brief ref:** brief 1 §7.8; HS-17; matrix §7.9.

## D43 — Windows time-zone picker: every region-mapped IANA id, current tzdata names (UI-D3, QA-B4)

- **Decision:** `TimeZoneCatalog` builds the picker from `TimeZoneInfo.GetSystemTimeZones()`:
  - A zone with a Windows id (every zone on Windows) becomes **every** IANA id CLDR maps it to. That is its default id, plus `TryConvertWindowsIdToIanaId(id, region)` for each ISO region this computer's cultures know. So `GTB Standard Time` gives `Europe/Bucharest`, `Europe/Athens` and `Asia/Nicosia`.
  - An id CLDR still spells the old way is offered under its current tzdata name, from `TimeZoneCatalog.Renamed` (14 entries, for example `Asia/Calcutta` → `Asia/Kolkata`, `Europe/Kiev` → `Europe/Kyiv`, `America/Godthab` → `America/Nuuk`), but only when the new name resolves on this computer.
  - A zone whose id is already IANA (every zone on macOS) is offered as it is. Nothing is added or dropped there.
  - Only ids `FindSystemTimeZoneById` resolves are offered, and a Windows id is never offered.
  - Search matches every typed word against the id and its segments, the zone's display, standard and daylight names, the Windows id and its display name, and the country (code and English name, from the region mapping and, on macOS, `zone.tab`), plus the offset as `UTC+03:00` or `UTC+3`. Cities of the same Windows zone's other entries are removed from each entry's search text, so "athens" finds `Europe/Athens` and not also `Europe/Bucharest`.
- **Rationale:** The plain mapping gave one id per Windows zone, so "athens" found nothing on Windows (UI-D3). A Windows user choosing "GTB" would store `Europe/Bucharest` where a Mac user stores `Europe/Athens`. Renaming the legacy ids makes both OSes store the same name, so the conflict hint (QA-N5) sees them as equal.
- **Consequence:** A test checks the `Renamed` table against the macOS zone list. A stored id that isn't listed (an alias, a hand-typed Windows id, an unknown id) is kept as its own entry and is never rewritten by an untouched save (TZ-14).
- **Brief ref:** brief 2 §4.5, §6; QA-B4; D12.

## D44 — Button sizing: headroom, then an ellipsis (UI-D2)

- **Decision:** A button's label box stretches across the button and centers its text. Every text button sets a `MinWidth` that leaves at least **15 %** of the label's width free. That absorbs wider platform fonts (Segoe UI on Windows) and fallback glyphs (`▶ ↘ ↗`). A button that is squeezed anyway ends its label with `…` (`TextTrimming=CharacterEllipsis`) rather than cutting a glyph. Pickers trim long names the same way, and a drop-down is never wider than its picker (UI-D1).
- **Rationale:** On Windows some labels ("Hide to tray", "Skip →", the tabs) were clipped mid-glyph at the compact size, because the buttons were sized to macOS font metrics.
- **Consequence:** The compact 780×650 headless check (with a negative control) asserts no clipped text on both OSes, and the CI smoke screenshots show it natively. The native visual pass is NC-01/NC-17.
- **Brief ref:** brief 1 §10 (UX gate); QG-03.

## D45 — Accent buttons keep dark ink

- **Decision:** Buttons with the `accent` class (lime `#C2F278`, hover `#D2F79A`, pressed `#A9D95F`) always draw their text in the dark ink `#172216`, including over Fluent's own accent template, which paints white text. A disabled accent button keeps Fluent's disabled colors.
- **Rationale:** White on lime is 1.3:1, unreadable. Dark ink is 12.7:1 on the resting fill, 13.7:1 on hover and 10.0:1 when pressed, all well above WCAG AA (4.5:1).
- **Brief ref:** brief 1 §10; QG-03; D1.

## D46 — Single-instance accept failures and the instance bound (CT-SI-13)

- **Decision:**
  - **A client that left before its connection was accepted** (Windows `ERROR_NO_DATA` and similar) is a **per-connection** failure. The dead instance is replaced before it is released, so the name stays served, and the loop goes on with **no back-off**. It is logged as `single_instance.connection_error`.
  - **Any other accept or create failure** is a listener error: `single_instance.listener_error`, a 1 s back-off, then the loop continues. Only disposal stops the listener.
  - **The service enforces its own bound** of `MaxServerInstances` = 3 (2 handled connections plus the armed instance, D24/D31) through its handler slots. A slot is taken before waiting on an instance.
  - **The OS limit is the maximum**, `NamedPipeServerStream.MaxAllowedServerInstances`, not 3.
- **Rationale:** On Windows a pipe instance counts against the OS limit as long as *any* handle to it is open, including a client's. A client that is slow to close, or a stalled one that keeps its handle after the read timeout, could use up an OS limit of 3. Creating the next armed instance would then fail with "All pipe instances are busy", so a real activation was refused. A burst of clients that connect and leave used to trigger the 1 s back-off each time.
- **Consequence:** CT-SI-13 checks 50 rapid connect-then-close and disconnect-before-reply cycles and two stalled clients. It asserts the peak instance count (3), that no back-off is needed, and that the next second launch is `Activated`. Matrix §8.2.4 is updated. D24's instance numbers stand as the service's own bound.
- **Brief ref:** brief 1 §7.5; D24, D31.

## D47 — Phase 2: the null path keeps `now`'s `Kind`

- **Decision:** A slot without a zone, or with an id this computer can't resolve, is evaluated exactly as in Phase 1: `now.Date.AddDays(offset).Add(time)`. Its `Occurrence.At` therefore has the `Kind` of the `now` it was given. Only zoned slots normalize `now` to `Unspecified` (for the 3-argument conversions, QA-B2), and a converted `At` is always `Unspecified`.
- **Rationale:** Brief 2 §4.3 sketched `now = SpecifyKind(now, Unspecified)` at the top of `Evaluate`. That would make the null path's `At.Kind` `Unspecified` where Phase 1 gave `Local`, breaking the "byte-identical" guarantee of QA-B1 and TZ-01. `Kind` is only a tag here (QA-N9: never call `ToLocalTime`/`ToUniversalTime` on `At`), so keeping it costs nothing.
- **Consequence:** TZ-01 compares `At`, `At.Kind`, `Entry` and `Key` against a re-statement of Phase 1 `Evaluate` over 2026, for four blank spellings and three `Kind`s. The `Occurrence` doc comment states the invariant.
- **Brief ref:** brief 2 §4.3, §7.9; QA-B1, QA-N9.

## D48 — Phase 2 schedule dedup rule

- **Decision:**
  - **Identity** is `Occurrence.Key`, `{Entry.Id}:yyyy-MM-ddTHH:mm` of the slot's own zone wall time (plus `@{Zone.Id}` for a zoned slot), formatted culture-invariantly. The same start keeps the same key when the computer's zone changes.
  - **`ScheduleSession.TakeChange`** returns the current occurrence when its key differs from the last one fired or held and it is **not older**, comparing `At` only within the same computer zone.
  - **An older current occurrence fires only after a forward wall-clock transition:** a slot edit, a zone edit, an OS zone change, or a slot reached after a backward clock correction.
  - **When the wall clock itself moves backward** (a correction, a DST fall-back), the older occurrence that becomes current is adopted silently: last week's slot is never replayed.
  - `HoldCurrent` never moves the hold back to an older occurrence of the same clock. `force` (refresh) still replays (BHV-47).
- **Rationale:** Brief 2 §7.8 found that the Phase 1 guard `current.At < last.At` swallowed a genuinely new current occurrence after a zone edit (QA-N8). CF-02 found the same after a backward clock jump. A key-only rule would replay last week's slot after a backward jump, so the age check stays, narrowed to the one case where it is right.
- **Consequence:** CT-SES-08 and "Backward clock jump: Tue 09:00" flipped to "fires"; "does not replay last week's Wed slot" stays. QA-N8 and TZ-11 checks cover zone edits, OS zone changes and DST.
- **Brief ref:** brief 2 §7.8; QA-N8; CF-02.

## D49 — Thread of Windows `Resumed` (amends D30)

- **Decision:** D30's Windows half ("raised on the `SystemEvents` thread") is withdrawn. `WindowsPowerEvents.Start()` runs on the UI thread, which is STA (`[STAThread] Main`) and pumps messages. When the first subscription comes from such a thread, `SystemEvents` creates its hidden notification window on that thread instead of starting its own. `PowerModeChanged` is therefore most likely raised on the Avalonia UI thread. NC-02 records the actual thread.
- **Consequence:** Nothing in the App depends on either answer. `NotifyWakeAsync` is safe from any thread (D18), and the `ISystemPowerEvents` contract still says "arbitrary thread". The `WindowsPowerEvents` doc comment still says "the `SystemEvents` thread", and the platform lane corrects it when NC-02 has run (matrix §7.10 SR-02).
- **Brief ref:** brief 1 §4.4; D30; matrix §8.2.2, NC-02.

## D50 — Settings recovery never replaces the original without a preserved copy (CF-03/CF-04)

- **Decision:** `SettingsStore` (`DialShift.Core/Settings/SettingsStore.cs`) treats the user's unreadable `settings.json` as data that must survive:
  - **Backup names are unique.** The copy is created with `FileMode.CreateNew` as `settings.json.unreadable-<yyyyMMddHHmmssfff>` (local time, from an injected `IClock`). If that name exists, `-2`, `-3`, … up to 100 names are tried. An existing backup is never overwritten. The copy is flushed to disk, and a partial copy is deleted.
  - **`Load` never throws on the recovery path.** Any failure to make the copy (disk full, read-only folder, permissions, every name taken) is caught. `Load` still returns defaults, with a `Warning` that says no copy exists and that the original won't be replaced until one can be made.
  - **The original is only ever replaced once a flushed copy of it exists.** The store remembers a failed copy. The next `Save` retries the copy first. While it still fails, `Save` throws `IOException` and writes nothing, and `SettingsService` logs it as `settings.save_failed` and shows the usual "DialShift · Save failed" dialog (BHV-16). Once the copy succeeds, the save goes ahead.
  - **`Warning` belongs to one `Load`.** Every `Load` resets `Warning` and the pending-copy flag first (CF-03).
- **Rationale:** The Phase 1 store copied with a millisecond name and no collision check. A second recovery in the same millisecond made `File.Copy` throw inside the recovery `catch`, so the exception escaped `Load` (the BHV-03 quirk) and reached the startup-failure path. Worse, a copy that failed for any other reason would have let the next save replace the only copy of the user's stations and schedule with defaults. Refusing to save is the lesser harm: the app still runs on defaults, and the user is told why changes aren't kept.
- **Consequence:** The BHV-03 quirk is gone. The Phase 1 mitigation (a `Load` exception reaching the startup-failure dialog) is no longer needed for this case. CT-SET-10 still pins the plain 17-digit name; the `-N` suffix, the no-free-name case and the read-only folder (Unix only) are the "CF-04 …" checks, and "CF-03 Warning is cleared by a later successful Load" replaced the `[quirk]` pin (matrix §7.10 CF-03, CF-04; `ec09114`). The README describes the backup names and the save refusal. `Settings.Version` is unchanged (QA-N3).
- **Brief ref:** brief 1 §7.9 (settings backups, `.unreadable-<timestamp>`); BHV-03, BHV-16; brief 2 §7 (QA-N3); matrix §7.10.

## D51 — macOS bundle layout: Mach-O-only `Contents/MacOS`, an xattr-free zip, and a bundle smoke (SR-03)

- **Decision:**
  - **Only Mach-O files in `Contents/MacOS`.** `scripts/build-mac-app.sh` publishes into `Contents/Resources/app`, then moves every Mach-O file (the `DialShift` apphost, the .NET runtime and native dylibs, `createdump`) to `Contents/MacOS`, leaving a symlink to each in `Contents/Resources/app`. A `Contents/MacOS/DialShift.dll` symlink points at `../Resources/app/DialShift.dll`. The .NET host takes the real path of that link as its application folder, so it finds the managed files in `Contents/Resources/app` and the runtime through the links. The managed `.dll`, `deps.json` and `runtimeconfig.json` files are therefore sealed by codesign as resources, and no file keeps its signature in extended attributes.
  - **The zip carries no extended attributes:** `ditto -c -k --norsrc --noextattr --noacl --keepParent`. It has no AppleDouble `._*` entries (243 before); permissions and symlinks are kept.
  - **Verified after both extractors.** `scripts/verify-mac-app.sh --zip` rejects `._*`/`__MACOSX` entries, extracts the zip with `ditto` and with `unzip`, and verifies each bundle. The bundle checks reject non-Mach-O files in `Contents/MacOS`, `com.apple.cs.*` extended attributes, loose `._*` files and broken links, on top of the existing checks.
  - **CI smoke-tests the packaged app.** After the zip is verified, the macOS job extracts it with `unzip` and runs `DialShift.app/Contents/MacOS/DialShift --smoke-test` (the "bundle smoke"), so the bundle layout, the host's application folder and the ATS media exception are exercised through the executable users get.
- **Rationale:** codesign signs non-Mach-O files in `Contents/MacOS` as code and stores those signatures in extended attributes. `ditto`, Finder and Safari restore them; `unzip` and many third-party archivers do not, leaving loose `._*` files and unsigned DLLs, so the ad-hoc signature no longer verified ("code object is not signed at all") and Gatekeeper could call the app damaged. CI extracted with `ditto` only and could not see it. Moving the managed files out of `Contents/MacOS` removes the cause instead of depending on the user's extractor. The earlier CI smoke ran the build output, which ATS and the bundle layout don't apply to.
- **Consequence:** Evidence: `dafb74c`; CI `36111775825` at `39f5c8a` (the zip verified after `ditto` and `unzip`, the bundle smoke 32/32 with `MacAvPlayerPlaybackEngine`); on the dev box the `unzip` copy passes `codesign --verify --deep --strict` and the smoke. Matrix SR-03, PK-03, HS-15 and DOD-04 are updated; NC-07 still checks Finder, Safari and Gatekeeper on a clean Mac. Developer ID signing and notarization (D7, NC-09) must keep this layout.
- **Brief ref:** brief 1 §8 (artifact format, signing); D7, D13; matrix §7.10 SR-03.

## D52 — Start with no active display: a render-timer fallback on Avalonia's private APIs (NX-01)

- **Decision:**
  - **Probe first.** On macOS, before Avalonia.Native initializes, `CoreVideo.ProbeDisplayLink` (`DialShift.App/Interop/CoreVideo.cs`) makes the call Avalonia.Native's render timer makes when it registers: `CVDisplayLinkCreateWithActiveCGDisplays`. It then releases the link.
  - **Fall back when the probe fails.** If the probe returns an error (`kCVReturnInvalidDisplay`, -6661, when no display is active, or any other `CVReturn` error), `MacRenderTimerFallback.UseRenderTimerFallback` (`DialShift.App/Platform/MacOS/MacRenderTimerFallback.cs`) initializes the platform behind a child `AvaloniaLocator` scope. That scope answers `IRenderLoop` with a loop on Avalonia's `SleepLoopRenderTimer` at 60 fps. The loop is then bound for the rest of the process, and the app logs one warning, `app.render_timer_fallback`, with the `CVReturn` code. With a display, the platform initializes exactly as before and nothing is logged.
  - **The fallback lasts until the next start.** The fallback timer parks its thread whenever the render loop has nothing to draw, so an idle tray app spends no CPU on it. It renders normally once a display wakes.
  - **Avalonia's private APIs are opted into on purpose.** `AvaloniaLocator`, `RenderLoop` and `SleepLoopRenderTimer` are `[PrivateApi]` in Avalonia 12 and are left out of its reference assemblies. `DialShift.App.csproj` and `DialShift.Tests.csproj` set `AvaloniaAccessUnstablePrivateApis`, and they downgrade AVA3001, the opt-in's reminder, to a message so that `-warnaserror` builds pass.
  - **Upgrade rule.** This is safe only because every Avalonia package is pinned to exactly 12.1.2 (D6, D19, PK-06). **Any Avalonia version change must re-verify the fallback before it merges.** That means four things:
    1. the `RenderTimerFallback` suite passes;
    2. the smoke passes with the probe forced to -6661 against the real Avalonia.Native in the bundle;
    3. idle CPU in the fallback stays at the normal level;
    4. Avalonia.Native still resolves its render loop through `AvaloniaLocator.Current` when it creates the compositor, and still registers the display link while the platform initializes.

    If any of these no longer holds, the fallback is redesigned in the same change. It is never silently dropped.
- **Rationale:** NC-17 found the crash on 2026-09-25. The bundled app was launched through LaunchServices while the displays were asleep, and it died at startup: `System.InvalidOperationException: Avalonia.Native was not able to start the RenderTimer. Native error code is: -6661`. The same start happens at a login or restart with the screens off and on a headless Mac mini. The app would have crashed there, so the tray, the schedule and playback never started. Avalonia.Native 12.1.2 has no option for another render timer, so there is no public way to avoid the failing registration. The locator swap reaches the timer at the one point where Avalonia resolves the loop, and it leaves the normal path untouched. Waiting for a display instead would hold back the tray, the schedule and playback, none of which needs one.
- **Consequence:** Evidence: `2c0909e`, and the `RenderTimerFallback` suite (RT-01..RT-07, 16 checks), green locally at 1656 passed and 4 skipped, 23/23 suites. With the probe forced to -6661 against the real Avalonia.Native, the smoke passed 32/32, and idle CPU was 0.05 s per 30 s, equal to the normal path. The start with the displays really asleep is still to be observed: NC-17 step 10 and NC-08 step 5. Matrix §7.10 NX-01, §8.2.7 (`app.render_timer_fallback`), PK-06 and OQ-9 are updated, and the README explains the log line. The probe is a plain C call into CoreVideo, declared in `DialShift.App/Interop/` with the other native declarations (D27).
- **Brief ref:** brief 1 §7.2 (Avalonia version), §11 DoD (packages launch and show the tray); D6, D19, D27; matrix §7.10 NX-01, NC-08, NC-17.

## D53 — GitHub Releases from version tags

- **Decision:**
  - **One build definition.** The CI job moved from `.github/workflows/ci.yml` into the reusable workflow `.github/workflows/build.yml` (`workflow_call`), unchanged in its phases: build (`-warnaserror`), tests, native smoke, package, zip verification, macOS bundle smoke, upload. `ci.yml` calls it on every branch push. `release.yml` calls it for a tag. The D33, D35 and D36 mentions of `ci.yml` now refer to `build.yml`, where the steps and `MACOS_LABEL` live.
  - **Release workflow** (`.github/workflows/release.yml`): runs on a `v*.*.*` tag push, or by hand (`workflow_dispatch`) with an existing tag. A `resolve` job checks the tag against `vMAJOR.MINOR.PATCH[-prerelease]`, checks its `MAJOR.MINOR.PATCH` against the csproj, and checks that `CHANGELOG.md` has the version's section, all before anything is built. Then `build.yml` runs at the tag, and a `publish` job creates the release with `gh release create --verify-tag`. `publish` is the only job with `contents: write`; everything else is read-only. Concurrency is one run per tag and never cancels. The workflow names no repository: it publishes to the repository it runs in, so a tag pushed to the private `dialshift-dev` repository is a dry run, and the same tag pushed to the public `spyroskotsakis/DialShift` repository is the public release.
  - **Version source.** The csproj `<Version>` is the single source of `MAJOR.MINOR.PATCH`. A tag may only add a SemVer pre-release suffix (`v0.3.0-rc.1`); a different `MAJOR.MINOR.PATCH` fails the release, so every version bump is a source commit. `scripts/build.ps1 -Version` and `scripts/build-mac-app.sh --version` enforce the same rule and pass the full version to `dotnet publish` as `-p:Version`. The build, test and smoke steps compile the csproj version, which the rule makes equal to the tag in `MAJOR.MINOR.PATCH`; the packages carry the full version.
  - **Version mapping.** The assembly `InformationalVersion` is the full SemVer plus the SDK's `+<commit>` (DialShift.exe's product version and the log's `app.start` line). `AssemblyVersion` and `FileVersion` are `MAJOR.MINOR.PATCH.0`. On macOS, `CFBundleShortVersionString` is `MAJOR.MINOR.PATCH`, because Apple allows only integers there, and `CFBundleVersion` is the build number: the release workflow's run number, which grows with every release run, so a newer build, including a later pre-release of the same version, always sorts higher for LaunchServices. Local and CI builds default the build number to `MAJOR.MINOR.PATCH`. `verify-mac-app.sh` checks both formats.
  - **Assets.** `DialShift-win-x64.zip`, `DialShift-macos-arm64.zip` and `SHA256SUMS.txt`, which covers both zips in `shasum -a 256 -c` format. Names carry no version, so `/releases/latest/download/<asset>` links never change; a specific version is at `/releases/download/<tag>/<asset>`, so versioned copies would only double the storage. The macOS zip is the build's `DialShift-osx-arm64-<label>.zip` renamed: the label (`native-avplayer`, D36) is expected to change when NC-07 passes and when the build is signed, and a label in the asset name would break the stable link each time. The label stays in the CI artifact name, the release notes and the README.
  - **Pre-releases.** A tag with a `-` suffix is published with `--prerelease --latest=false`; `vX.Y.Z` is published with `--latest`. GitHub never resolves `/releases/latest` to a pre-release, so the README points at the Releases page until the first full release.
  - **Release notes.** `scripts/release-notes.sh <version> [SHA256SUMS.txt]` prints the version's `CHANGELOG.md` section (Keep a Changelog), with relative links rewritten to the files at the tag, followed by the downloads, how to check and install them, the signing status (Windows unsigned; macOS ad-hoc, not notarized, D7) and the checksums.
  - **Supply chain.** Release builds skip the NuGet cache (`nuget-cache: false`) and restore from nuget.org; checkouts don't keep credentials; inputs reach scripts through environment variables, never through expressions in the script text; there is no `pull_request_target`. Actions are pinned by major, as in D33; `actions/download-artifact@v8`, the current major, errors on a digest mismatch.
- **Rationale:** Workflow artifacts expire after 7 days and need a GitHub login, so they are not a download channel. Reusing `build.yml` means a release ships only packages that passed exactly the CI checks, with no second copy of the steps to drift. A version-free asset name gives permanent "latest" links. Keeping the numeric version in source makes a tag unable to ship a version the code doesn't declare.
- **Consequence:** The About screen shows `Assembly.GetName().Version` to three parts (`AppComposition.cs`), so a pre-release shows `0.3.0`, not `0.3.0-rc.1`; the full version is in the log's `app.start` line and in DialShift.exe's product version. Signing is unchanged (D7): a public release still needs Authenticode, Developer ID signing and notarization (NC-05, NC-09), and clean-machine installs (NC-07) before a full release. The first release, `v0.3.0-rc.1`, is a pre-release for that reason.
- **Brief ref:** brief 1 §8 (artifact format, signing); D7, D13, D33, D36, D51.

## D54 — Slot times: one canonical `HH:mm`, and the culture-written `HH.mm` still plays

- **Decision:**
  - **`HH:mm` is canonical and has one formatter.** `Scheduler.FormatTime(TimeOnly)` (`DialShift.Core/Scheduling/Scheduler.cs`) returns 24-hour `HH:mm` in the invariant culture, whatever the current culture is. Code that writes `ScheduleEntry.Time` uses it and has no format string of its own.
  - **`TryTime` also accepts `HH.mm`.** Both forms are parsed culture-invariantly, with exactly two-digit hours and minutes. Everything else is still rejected: `8:00`, `8.30`, `24:00`, `08:60`, padding, seconds and other separators (CT-SCH-06, CT-SCH-09). `.` is the only other separator any culture produces. A scratch run over all 1063 `CultureInfo.GetCultures(AllCultures)` cultures on .NET 10 (ICU) found that `ToString("HH:mm", culture)` gives `08:30` everywhere except 27 cultures that give `08.30`: as, as-IN, da, da-DK, da-GL, en-DK, en-FI, en-ID, fi, fi-FI, id, id-ID, kl, kl-GL, mni-Mtei, mni-Mtei-IN, ms-ID, rej, rej-ID, si, si-LK, smn, smn-FI, su, su-Latn, su-Latn-ID and sv-FI. The time separators found were `:` and `.`, nothing else. CT-SCH-09 re-checks every culture of the host at test time.
  - **`SettingsStore.Load` canonicalizes in memory.** Every `ScheduleEntry.Time` that `TryTime` accepts becomes `FormatTime` of it, so conflicts, the schedule list's sort and display see one form. `Load` does not rewrite the file; the next `Save` persists the canonical text. A time that does not parse is left as it is and is not corruption: no `.unreadable-*` copy and no warning, and the slot is ignored at evaluation, as before.
  - **`Conflicts` compares parsed times**, so `08:30` and `08.30` conflict (CT-SCH-10). A time that does not parse never conflicts, because that slot never fires. Before, two identical unparseable strings "conflicted".
- **Rationale:** The schedule editor saved `parsed.ToString("HH:mm")` without a culture, and the legacy apps did too. On the 27 cultures above that writes `08.30`, which the strict `HH:mm` parser rejected, so `Evaluate` skipped the slot without a word: saved slots never played, and conflict detection compared raw text and missed them. Settings files written by those builds exist, so fixing only the writer would leave existing users' slots silent. Accepting `HH.mm` on read fixes those files; canonicalizing at load keeps comparison and display consistent; one invariant formatter stops new ones. Rewriting the file on load was rejected: `Load` never modifies the user's file (D50), and the next save persists the canonical form anyway.
- **Consequence:** CT-SCH-06 no longer lists `08.00` as rejected (it now parses as 08:00). The new checks are CT-SCH-09..11 and CT-SET-12. `Settings.Version` is unchanged (QA-N3); an older build that reads a canonicalized file sees the `HH:mm` it always accepted. The editor's save path switches to `Scheduler.FormatTime` in the UI lane's half of this fix.
- **Brief ref:** brief 1 §5 (Core), §7.9 (settings); brief 2 §7 (QA-N3); D50; matrix §7.2, §7.4.
