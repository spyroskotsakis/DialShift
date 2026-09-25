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
