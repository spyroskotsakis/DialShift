# Single Codebase Refactor — Windows + macOS via Avalonia

> **Status:** Deferred — not a priority yet. This document is a bootstrap: a ready-to-execute plan for when we want to collapse the two front-ends into one.
>
> **Updated 2026-09-16** (2nd revision, post-audit): architecture research folded in, then refined after review — platform/process concerns kept out of the domain core, an explicit playback state machine + cancellation policy, honest .NET 10 pipe-permission wording, three early spikes (Apple Silicon LibVLC, Windows `SystemEvents`, macOS wake notification), and formal definition-of-done + acceptance matrix.

## 1. Context

DialShift currently ships two desktop front-ends over a shared core:

| Project | UI toolkit | Platform | Purpose |
|---|---|---|---|
| `DialShift/` | WPF + WinForms | Windows (`win-x64`) | original app |
| `DialShift.Mac/` | Avalonia | macOS (`osx-x64`) | the macOS port |
| `DialShift.Core/` | — | any | models, scheduler, settings persistence (shared) |
| `DialShift.Tests/` | — | any | deterministic logic tests (shared) |

The domain logic lives in `DialShift.Core` (one source). But the **UI and playback orchestration are duplicated** across the two front-ends: `MainWindow`, `Dialogs`, `RadioController`, and app lifecycle/tray/startup each exist twice (WPF and Avalonia). Today that is ~1,300 duplicated lines out of ~1,700 total.

## 2. Goal (upgraded)

Maintain **one front-end** that builds for both platforms — framed not as "one big shared file each" but as:

```text
Avalonia UI
    ↓
Application coordinator / view model
    ↓
Playback service + scheduling service
    ↓
LibVLC adapter, clock/timer, settings, platform lifecycle
```

One presentation layer, explicit **capability-based platform services**, and playback/application coordination that is **independently testable**. `RadioController` must not become a UI-thread-bound, platform-aware god object — it is split into a UI-agnostic playback coordinator plus thin adapters.

`dotnet publish` produces **two or three platform artifacts** (published application directories — *not* installers; the macOS `.app` script and a Windows packaging stage create the installable distributables, §8):

```bash
dotnet publish DialShift.App -c Release -r win-x64
dotnet publish DialShift.App -c Release -r osx-x64
# osx-arm64 becomes a routine publish target as soon as we adopt the AVPlayer
# backend (§4.3 Option A — proven upstream); until then the honest macOS
# artifact is a clearly labeled osx-x64 Rosetta build.
```

## 3. What is already good (validated by research)

Decisions that research confirms — keep them:

- **Starting from the existing Avalonia macOS port** is sensible: it stops further WPF/WinForms investment and preserves the port work already done.
- **Retiring WPF/WinForms** is the correct long-term trade-off at ~1,300 of ~1,700 duplicated front-end lines. Two UIs inevitably drift (playback fallback, retry, schedules, tray actions, lifecycle edge cases).
- **Keeping `DialShift.Core` unchanged initially** is a disciplined migration boundary. Do not combine a UI migration with a domain-model/storage/scheduler redesign unless a concrete incompatibility forces it.
- **Avalonia `TrayIcon` + `NativeMenu`** is the right tray mechanism: Avalonia documents full tray support on Windows and macOS (Windows: right-click shows the menu, left-click fires `Command`; macOS: normal click shows the menu). No custom tray abstraction is needed at the outset.
- **Named pipes for single-instance** are the right call: `NamedPipeServerStream`/`NamedPipeClientStream` are cross-platform in .NET (Unix domain sockets under the hood on macOS/Linux). Much better than Windows named-`Mutex` + `EventWaitHandle` (named events are not supported on Unix). *Hardening requirements in §7.4.*
- **"Cross-platform" ≠ "no platform-specific code"** — launch-at-login, reveal-in-file-manager, app bundling, icons, and power events are naturally platform-dependent; isolate them, don't pretend they don't exist.

## 4. Main improvements

### 4.1 Move playback orchestration out of the UI layer (most important)

`RadioController` today mixes playback engine and UI/lifecycle participation: `DispatcherTimer`, UI dispatching, wake recovery, volume, retries, fallback, schedule triggering. Keeping the Avalonia copy as the shared one removes duplication but preserves the wrong dependency direction: `UI framework → RadioController → timing/retry/playback/scheduler`.

Target ownership instead:

- **`PlaybackCoordinator`** must not know what Avalonia, WPF, `DispatcherTimer`, `Dispatcher.UIThread`, or a modal dialog is. It consumes `IClock` (and, only if tests later require it, a timer abstraction — see §4.1a) and an `IPlaybackEngine` port.
- Marshal back to the UI thread **only when updating view-model state**:

```csharp
await Dispatcher.UIThread.InvokeAsync(() => StatusText = playbackState.StatusText);
```

Four benefits: deterministic unit tests for playback/retry/fallback; sleep/wake detection independent of the UI message loop; future UI changes (Avalonia upgrades, headless mode, web remote, mobile companion) don't force a playback rewrite; the last structurally important UI coupling is removed rather than hidden.

The old plan called this "optional" — it is now **part of the migration**, kept intentionally small: extract interfaces and orchestration, do not redesign the whole domain.

#### 4.1a Clock abstraction — minimal, and split by purpose

Inject **only `IClock`** for deterministic tests:

```csharp
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
```

- **Do not introduce `IPeriodicTimer` yet.** Use `PeriodicTimer` directly in the orchestration layer:

```csharp
using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
while (await timer.WaitForNextTickAsync(cancellationToken))
{
    await coordinator.OnTickAsync(cancellationToken);
}
```

Tests call `OnTickAsync()` manually with a fake clock — a fake async timer abstraction invites brittle tests around task scheduling, cancellation, and races. Add `IPeriodicTimer` later only if lifecycle ownership of the ticking loop itself must be tested, or the interval becomes dynamic and behaviorally significant.

- **Two time sources, two purposes:**
  - *Schedules / persisted timestamps:* `DateTimeOffset.UtcNow` (wall clock — NTP/timezone/DST changes are fine here).
  - *Wake/tick gaps / elapsed durations:* a **monotonic** source (`Stopwatch.GetTimestamp()` or a small monotonic abstraction). Wall-clock jumps would otherwise produce false "slept for 15 s" detections.

### 4.2 Interfaces + dependency injection — not `OperatingSystem` scattered through files

`Platform/` separation is good, but avoid OS-check branches throughout business code. Define narrow services:

```csharp
public sealed record StartupRegistrationStatus(
    bool IsEnabled,
    string? DiagnosticMessage = null);

public interface IStartupRegistration
{
    Task<StartupRegistrationStatus> GetStatusAsync(CancellationToken ct = default);
    Task<StartupRegistrationStatus> SetEnabledAsync(bool enabled, CancellationToken ct = default);
}

public interface ISystemPowerEvents : IDisposable
{
    event EventHandler? Resumed;
    void Start();
}

public interface IFileRevealService
{
    Task RevealAsync(string path, CancellationToken ct = default);
}
```

The richer `StartupRegistrationStatus` (not bare `bool`) maps directly to the transactional/observable requirement: the UI can show *why* registration failed instead of a wrong "enabled" state.

Select implementations **once** at composition time (`Program.cs` / `App.axaml.cs` / small bootstrapper), e.g. `services.AddSingleton<IStartupRegistration>(OperatingSystem.IsWindows() ? new WindowsStartupRegistration() : new MacStartupRegistration())`. Plain `Microsoft.Extensions.DependencyInjection` is enough — no heavy framework. Use **runtime checks**, not `#if WINDOWS`/`#if MACOS`, unless a source file or reference cannot compile on the other OS.

### 4.3 Treat macOS ARM64 as a product requirement

The biggest practical weakness today: `VideoLAN.LibVLC.Mac` is x86_64, so Apple Silicon runs under Rosetta 2. Do not accept that silently. Runtime matrix:

| Platform | Initial support | Preferred target | Release decision |
|---|---|---|---|
| Windows Intel/AMD | `win-x64` | `win-x64` | Ship |
| macOS Intel | `osx-x64` | `osx-x64` | Ship if still needed |
| macOS Apple Silicon | Rosetta fallback | `osx-arm64` | **Investigate before calling the refactor complete** |

**Spike before starting the consolidation:**

1. Publish the current Mac app as `osx-arm64`.
2. Confirm the exact architecture of the bundled `libvlc.dylib` (`file` / `lipo -info`).
3. Test whether a native arm64 app + x64 LibVLC works, whether the process needs Rosetta, and what users actually see.
4. Evaluate an arm64-compatible LibVLC package/build or a replacement playback backend if it becomes user-facing.
5. Record the outcome in the README and release notes.

**Honest labeling rule:** the macOS path in every matrix/docs table is either *"native `osx-arm64` once a compatible LibVLC runtime is proven"* or *"clearly labeled `osx-x64` Rosetta build"*. Until the spike succeeds, do not list `osx-arm64` as an ordinary publish target and never ship it unlabeled.

**A proven answer already exists (upstream v0.2.0 reference):** the replacement-backend option is implemented and shipped — AVPlayer via raw `objc_msgSend` P/Invoke (~70 lines in `MacAudioSession.cs`, zero dependencies, no Rosetta, no VLC bundling). The spike therefore becomes *validation and adaptation* of that reference, not open-ended research. Decision shortlist:

| Option | Status | Notes |
|---|---|---|
| **A. AVPlayer P/Invoke (macOS) + LibVLC (Windows)** | **Proven — upstream ships it** | Native arm64; trade-off: no track-title metadata on Mac, fewer exotic formats (MP3/AAC/HLS covered); **two playback adapters** behind `IPlaybackEngine` — fits the capability-service architecture |
| B. Arm64 LibVLC build/package | Not demonstrated | Investigate only if VLC-specific formats matter on Mac |
| C. Labeled `osx-x64` Rosetta build | Fallback | Honest labeling still required |

Option A is the default plan: it closes the ARM64 risk, removes the VLC native dependency from the Mac bundle entirely, and the two-engine shape (`LibVlcPlaybackEngine` + `MacAvPlayerPlaybackEngine`) is exactly what §6's `IPlaybackEngine` port exists for.

### 4.4 Sleep/wake recovery: layered, not timer-only

The timer-gap heuristic (tick gap ≥ 15 s ⇒ wake) is a reasonable **fallback**, not a full replacement for an OS wake notification:

- Gaps can come from debugger pauses, CPU pressure, a blocked UI thread, GC pauses, or modal/native operations — not only sleep.
- Recovery may be needed immediately on wake, while the timer may be delayed.
- A timer gap only detects wake if the timer was running before sleep.
- Network restoration can lag wake — an immediate reconnect may fail and needs delayed retry/backoff.

Layered design:

1. **Windows:** subscribe to `SystemEvents.PowerModeChanged` — **with a packaging spike** (§7.3): confirm the `Microsoft.Win32.SystemEvents` package/reference resolves in a `net10.0` project *without* a `-windows` target framework, subscribe after the Avalonia desktop lifetime/message loop is established, unsubscribe deterministically at shutdown, and keep timer-gap recovery as fallback on Windows too.
2. **macOS:** prefer `NSWorkspace.DidWakeNotification` — **spike with acceptance criteria** (§7.3). Accepted only if it: builds without turning `DialShift.App` into a macOS-only target; fires after lid-close and normal sleep/wake on supported macOS versions; does not retain/leak observer handles after shutdown; is isolated entirely behind `MacPowerEvents`; and leaves timer-gap recovery fully functional if registration fails. If these are not met quickly, use the timer-gap fallback for the initial consolidation and schedule native wake events as a separate enhancement — **do not let it block the codebase unification.**
3. **Both platforms:** keep the timer-gap check (monotonic clock, §4.1a) as a defensive fallback.
4. **Playback coordinator:** `ResumeFromSleepAsync()` is **idempotent and rate-limited**:

```text
Wake detected
  → cancel stale reconnect work
  → wait 1–3 s for network recovery
  → verify intended playback state
  → reconnect once
  → fall back to normal retry policy if it fails
```

`RadioController.Tick()` should not decide all of this — it emits "suspected time discontinuity" or calls a single idempotent recovery path.

### 4.5 Do not defer smoke testing

Do not delete or defer `SmokeChecks.cs` without a replacement. Lifecycle, tray visibility, startup registration, single-instance signaling, settings persistence, scheduling, and playback error behavior are exactly what breaks in this refactor. Three levels (full matrix in §11):

| Test level | Scope | Runs where | Recommendation |
|---|---|---|---|
| Unit tests | Scheduler, settings, retry policy, wake-gap logic, state machine, command routing | every PR | Expand significantly |
| Headless integration tests | Startup service, single-instance pipe protocol, persistence, platform adapter contracts | every PR or nightly | Add now |
| UI smoke tests | Launch, tray, main window, stream start/stop, **second-instance activation** | native Windows/macOS runners | Keep or replace before removing old harness |

- Avalonia: build testable view models + Avalonia headless testing where it provides value.
- CI: GitHub Actions native runners, `matrix: os: [windows-latest, macos-latest]`, compile + test + publish both artifacts; add a manual Apple Silicon gate if hosted runners don't match production hardware.
- Test that **a second process activates the first**, not merely that it exits.

### 4.6 Single project today — keep the two-shell door open

| Option | Best when | Trade-off |
|---|---|---|
| One Avalonia executable project | Platform differences stay in small adapters; native dependencies resolve cleanly | Simplest dev/release workflow |
| Shared app/UI library + tiny platform host projects | Native package assets, signing, app lifecycles, entitlements, or platform compilation get complex | Slightly more structure, cleaner packaging boundaries |

One project is better **while**: both platforms start via `StartWithClassicDesktopLifetime`, LibVLC packaging works with runtime-conditioned assets, macOS `.app` creation stays a packaging concern (not a source fork), and adapters stay truly small.

Switch to two thin hosts **if**: macOS entitlements/Objective-C/`Info.plist` lifecycle/wake notification create significant macOS bootstrap code; Windows packaging (MSIX, registry, installer) significantly alters build assets; per-platform VLC packages need mutually incompatible build configs; or `Platform/` approaches hundreds of lines with UI-specific branching. Two shells would still be **one UI + shared application logic** — far better than maintaining WPF and Avalonia separately.

## 5. Playback state machine & cancellation ownership (non-negotiable behavior spec)

Before code moves, the coordinator's behavioral boundaries must be explicit — otherwise the refactor merely re-homes implicit state and race conditions.

### 5.1 States

```text
Stopped · ScheduledWaiting · Connecting · Playing · Reconnecting · Failed
· SuspendedBySystem · Disposing
```

### 5.2 Inputs (events)

```text
UserPlay · UserStop · ScheduleDue · PlaybackEnded · PlaybackError
· WakeDetected · SettingsChanged · Dispose
```

### 5.3 Invariants

- At most one active LibVLC playback session.
- At most one reconnect/recovery operation in flight.
- **User Stop cancels scheduled and automatic reconnect work.**
- Wake recovery is idempotent.
- UI observes immutable state snapshots; UI never owns playback state.
- **Explicit per-state event acceptance** — e.g. a delayed retry completing *after* the user pressed Stop must not revive playback; `PlaybackError` in `Stopped` is ignored, etc. Document which events are ignored in which state in the coordinator's source header.

### 5.4 Cancellation & ownership rules

- The coordinator owns **one lifetime `CancellationTokenSource`**; each playback attempt / reconnect loop receives a **linked operation CTS**.
- Cancel and replace the operation CTS on: user Stop, new user Play, stream/source change, schedule cancellation, shutdown, and sleep/wake recovery superseding an in-flight reconnect.
- **Never await an old operation while holding the coordinator state lock.**
- Every delayed retry validates its **generation** before starting playback:

```csharp
long operationGeneration;
```

Every `PlayAsync`, retry, or wake recovery captures the generation; if a delayed operation wakes up and its captured value no longer matches the current one, it exits harmlessly. This prevents old retry tasks from restarting radio playback after a newer user action.

## 6. Revised target structure

```text
DialShift.Core/                      (domain — see purity rule below)
  Models/
  Settings/
  Scheduling/
  Playback/
    PlaybackCoordinator.cs
    RetryPolicy.cs
    PlaybackState.cs
    Contracts/
      IPlaybackEngine.cs
      IClock.cs
      (IPeriodicTimer.cs — only if tests later require it, §4.1a)

DialShift.App/
  App.axaml
  App.axaml.cs
  Program.cs                 (composition root: picks platform implementations)
  Views/
    MainWindow.axaml
    Dialogs/
  ViewModels/
    MainWindowViewModel.cs
  Services/
    LibVlcPlaybackEngine.cs      (Windows backend)
    MacAvPlayerPlaybackEngine.cs (macOS backend — AVPlayer P/Invoke, upstream reference)
    AvaloniaDialogService.cs
    AvaloniaUiDispatcher.cs
  Platform/
    Abstractions/
      IStartupRegistration.cs
      ISystemPowerEvents.cs
      IFileRevealService.cs
    Windows/
      WindowsStartupRegistration.cs
      WindowsPowerEvents.cs
      WindowsFileRevealService.cs
    MacOS/
      MacStartupRegistration.cs
      MacPowerEvents.cs
      MacFileRevealService.cs
  SingleInstance/            (process/transport concern — deliberately NOT in Core)
    SingleInstanceService.cs
    SingleInstanceMessage.cs
  Assets/

DialShift.Tests/
  Core/
  App/
  Platform/
```

**Core purity rule:** `DialShift.Core` stays free of Avalonia, Windows, macOS, LibVLC, registry, file-system-location, named-pipe, process, and UI-dispatch references. `IPlaybackEngine` is an application *port* (a small contract the coordinator consumes), not a domain model — that's the one deliberate boundary exception, and it carries no infrastructure. `ISingleInstanceService`/`SingleInstanceMessage` stay in `DialShift.App` (or a future infrastructure project) because they control process lifecycle and transport; a `SingleInstanceProtocol` DTO belongs in Core only if a separate host/service process ever needs to share it — not the case for the single executable.

Clear ownership: **`DialShift.Core`** = behavior and rules · **`DialShift.App/Services`** = LibVLC adapter + Avalonia-specific services · **`DialShift.App/Platform`** = OS capabilities only · **`Views`/`ViewModels`** = display and user actions · **`Program.cs`/bootstrapper** = implementation selection and composition.

`DialShift/` (WPF) is retired.

## 7. Detailed change list (bootstrap checklist)

No code written here — what changes and where — so it can be executed directly when we start.

### 7.1 Project structure

1. **Write a behavior inventory first** (§12 step 1); it becomes the living acceptance matrix (§11).
2. **Create `DialShift.App/`** from `DialShift.Mac/` sources (rename + relocate); keep it Mac-only initially and verify it behaves exactly like the existing Mac app.
3. **Retire `DialShift/`** (WPF) only after Windows behavior is ported and demonstrated (§12 steps 7–9).
4. **Update `DialShift.slnx`** → `DialShift.Core`, `DialShift.Tests`, `DialShift.App`.
5. **Delete `DialShift.Mac/`** once its contents live in `DialShift.App/`.

### 7.2 `DialShift.App/DialShift.App.csproj`

- `TargetFramework` stays `net10.0` (no `-windows` suffix).
- `RuntimeIdentifiers`: `win-x64;osx-x64` now; `osx-arm64` is added **only after** the §4.3 spike succeeds.
- Conditional native VLC packages: `VideoLAN.LibVLC.Windows 3.0.23.1` on Windows, `VideoLAN.LibVLC.Mac 3.1.3.1` on macOS; `LibVLCSharp 3.10.1` unconditional.
- Avalonia packages unchanged (`Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent` 11.3.22).
- Icon assets are **part of release packaging, not optional polish**: Windows tray expects `.ico`; macOS menu bar prefers a template image (monochrome PNG); the `.app` bundle gets its own `.icns`.

### 7.3 Early spikes (run before code movement)

| Spike | Goal | Exit criteria |
|---|---|---|
| **Apple Silicon playback** | arm64 feasibility | **Option A (AVPlayer P/Invoke) already proven upstream — spike = adapt + verify on M-series; honest labeling either way (§4.3)** |
| **Windows `SystemEvents`** | `PowerModeChanged` in `net10.0` Avalonia | package resolves without `-windows` TFM; subscribe after message loop; deterministic unsubscribe; timer-gap fallback retained |
| **macOS wake notification** | `NSWorkspace.DidWakeNotification` | accepted only per the five criteria in §4.4; otherwise timer-gap first, native events as separate enhancement |

### 7.4 Platform capability services (replaces per-OS `if/else` in app code)

| Service | Windows impl | macOS impl | Notes |
|---|---|---|---|
| `IStartupRegistration` | Registry `HKCU\...\Run` value | `~/Library/LaunchAgents/com.tsiger.dialshift.plist` | **Transactional + observable** via `StartupRegistrationStatus`: if plist write succeeds but load/unload fails, report the failure state + diagnostic log — never report "enabled" incorrectly |
| `ISystemPowerEvents` | `SystemEvents.PowerModeChanged` (spike-gated, §7.3) | `NSWorkspace.DidWakeNotification` (spike-gated) + timer-gap fallback | Both platforms keep the monotonic timer-gap heuristic as defensive fallback (§4.4) |
| `IFileRevealService` | Avalonia `Launcher`/file API if it can reveal-in-file-manager; `explorer.exe` only if "reveal containing folder" semantics are needed | `open <path>` | Evaluate the built-in Avalonia launcher **first**; keep the adapter only for reveal-folder semantics |
| `ISingleInstanceService` | named pipe (server) | named pipe (server) | Lives in `DialShift.App/SingleInstance/`; hardening in §7.5 |

### 7.5 Single-instance hardening (net10.0-accurate)

- **File-lock location**: per-user, writable, version-independent — inside the application support/data directory, **not** beside the executable or inside a signed app bundle.
- **Pipe options**: use `PipeOptions.CurrentUserOnly` on both client and server — it enforces same-user connectivity (on Windows it additionally considers elevation level). **Note:** the documented mode-`0600` backing-file behavior is a **.NET 11** change; on our `net10.0` target the Unix-domain-socket file permissions come from the process umask. **Validate the actual socket path and permissions on macOS** in the production launch environment — do not claim `0600` merely from using `CurrentUserOnly`. If/when targeting .NET 11+, it sets `0600` explicitly.
- **Operational rules:**
  - Stable per-user pipe namespace (hashed by app identity + user scope), never derived from untrusted input.
  - Bounded input: message size limit 4–16 KB.
  - Explicit connection and read timeouts.
  - Versioned message, e.g. `{"version":1,"command":"activate"}` — validate JSON/schema/version **before** acting.
  - Malformed input = failed activation request, **not** an app error.
  - Close each connection promptly; one short command per connection.
  - If the lock is acquired but the pipe server cannot start: **report the error and release the lock** — never leave the app in a non-activatable state.
  - No privileged actions triggered solely by pipe input.

### 7.6 Shared front-end files

- **`App.axaml(.cs)`** — single lifecycle; Avalonia `TrayIcon` + `NativeMenu`; startup/power routed through the capability services; existing modal-window helper instead of `MessageBox`.
- **⚠️ Tray menu identity (macOS crash pitfall — mandatory pattern):** Avalonia's macOS exporter binds its **native proxy to the `NativeMenu` instance** when the tray icon is initialized. Replacing `TrayIcon.Menu` (or re-registering via `TrayIcon.SetIcons`) **after** initialization throws a native exception — the crash upstream v0.2.0 explicitly fixed ("Mac native menu now retains the same root object while its entries update"). Rule for all refresh paths: **create the tray + menu exactly once; on refresh, mutate `menu.Items` in place** (`Items.Clear()` + re-add). Never assign a new `NativeMenu` to `TrayIcon.Menu` after startup, and never call `SetIcons` again. Regression-guard it in smoke tests with a *"tray menu identity preserved"* assertion after every editor operation (upstream's `EditorSmokeChecks` pattern). Also fixes our current stale-tray-menu bug (our `Refresh()` rebuilds a menu it never assigns).
- **Playback** — `PlaybackCoordinator` (Core, UI-agnostic, state machine per §5) + `LibVlcPlaybackEngine` (App/Services) + `AvaloniaUiDispatcher`; `PeriodicTimer` + cancellation in the orchestration layer; no `DispatcherTimer` in coordinator logic.
- **`MainWindow` / `Dialogs` / `Program.cs`** — keep the Avalonia versions; `Program.cs` becomes the composition root.

### 7.7 Retired / repurposed pieces

- `DialShift/NativeChrome.cs` — retire (Avalonia theming replaces DWM dark-title-bar).
- `DialShift/SmokeChecks.cs` — **repurpose, don't drop**: replaced by the three-level strategy (§4.5) before removal.
- Icons — `.ico` (Windows tray), macOS template image (menu bar), `.icns` (bundle) as release packaging.

### 7.8 Docs

- `README.md` — single `DialShift.App`; `dotnet publish -r` targets with ARM64 status recorded honestly (§4.3 step 5).
- `THIRD-PARTY-NOTICES.md` — confirm conditional-package wording.
- Drop "Run on macOS" special-casing once the README describes a single app.

## 8. Release packaging & signing

- **Windows:** decide the artifact format now — `.zip` initially, MSIX/installer later; verify SmartScreen/code-signing policy before public distribution.
- **macOS:** package a real `.app` bundle: `Info.plist` with `CFBundleIdentifier`, `CFBundleDisplayName`, `CFBundleIconFile`, `LSUIElement` policy, correct executable permissions, and architecture-specific native libraries.
- **Signing:** decide explicitly whether development builds are unsigned and whether release builds must be code-signed + notarized.
- **Clean-machine testing:** test distribution artifacts on a clean machine, not only developer machines.
- **Tray-first behavior:** with `LSUIElement=true` there is no Dock icon — deliberately test quit, reopen, and second-instance activation under that condition.
- **Reference implementation (upstream v0.2.0 — adopt as the HOW):** the packaging pipeline is already solved end-to-end and cross-builds **from Windows**: `build-macos.ps1` + `package-macos.py` download a **checksum-verified** `rcodesign` (apple-platform-rs), assemble the `.app` with `plistlib`, ad-hoc sign, then **mechanically verify the bundle** — pure-Python Mach-O parser asserting the **ARM64 slice** exists and the **ad-hoc CodeDirectory page hashes** match, and a ZIP with explicit Unix permissions. This is §4.3's honest-labeling rule enforced at build time: an Intel/Rosetta or unsigned artifact *cannot* pass the script. Keep `build-mac-app.sh` as the Mac-hosted convenience path; the Windows cross-build becomes the canonical release path.

## 9. What stays OS-specific after the refactor

| Concern | Windows | macOS | Where |
|---|---|---|---|
| Playback engine | LibVLC (`LibVlcPlaybackEngine`) | **AVPlayer P/Invoke (`MacAvPlayerPlaybackEngine`) — native `osx-arm64`**, per §4.3 Option A | `Services/` (two `IPlaybackEngine` adapters) |
| Launch at login | Registry `Run` key | LaunchAgent plist | `Platform/Windows|MacOS` |
| Sleep/resume | `SystemEvents.PowerModeChanged` (spike) + timer-gap fallback | `NSWorkspace.DidWakeNotification` (spike) + timer-gap fallback | `Platform/...PowerEvents` |
| Reveal folder | Avalonia Launcher / `explorer.exe` fallback | `open` | `Platform/...FileRevealService` |
| Tray icon | Avalonia `TrayIcon` (`.ico`) | Avalonia `TrayIcon` (template image) | shared (no split) |
| UI + playback | Avalonia | Avalonia | shared (no split) |

Everything else — models, scheduler, settings persistence, playback **coordination**, dialogs — is single-source.

## 10. Risks & gotchas

- **Loss of WPF-native look** — Windows renders with the Avalonia dark theme; confirm this is desired. Native controls, font rendering, window chrome, menu placement, and tray behavior will still differ legitimately between platforms — plan for **shared visual-regression baselines where feasible**, not "one set of screenshots".
- **Rosetta / x86_64 libvlc** — an explicit deliverable (§4.3 spike), not a footnote.
- **Sleep/resume** — timer-gap (monotonic) is the fallback; `SystemEvents` and `NSWorkspace` are spike-gated (§7.3); neither may block the consolidation.
- **Single-instance** — net10.0-accurate hardening (§7.5): per-user pipe name, versioned protocol, bounded input, lock file in app-support dir, validation of macOS socket permissions.
- **Tray menu identity** — never replace the bound `NativeMenu` after startup (§7.6); the crash upstream fixed is exactly what a naive refresh implementation reintroduces on macOS.
- **Smoke coverage** — three-level strategy replaces `SmokeChecks.cs` before it's removed (§4.5, matrix in §11).
- **App bundle** — keep `build-mac-app.sh` (Info.plist + `LSUIElement=true`); Windows packaging script in parallel; `.icns`/`.ico`/template icons part of packaging; clean-machine testing (§8).

## 11. Definition of done + acceptance matrix

**Definition of done:**

- One Avalonia UI implementation is the only maintained frontend.
- No Avalonia/WPF/WinForms API is referenced from `DialShift.Core`.
- Windows and macOS builds compile, test, and publish in CI.
- Both packages launch, retain settings, show a tray/menu-bar item, play/stop/retry streams, obey schedules, and exit cleanly.
- A second launch activates the existing instance on both operating systems.
- Launch-at-login can be enabled, disabled, and verified on both systems.
- Sleep/wake recovery is verified using a manual native-OS checklist.
- Apple Silicon status is published honestly: native arm64 **or** Rosetta-required.
- The legacy WPF/WinForms app is removed only after equivalent checks pass.

**Acceptance matrix** (the behavior inventory as a living test matrix):

| Behavior | Automated unit | Headless integration | Native Windows smoke | Native macOS smoke |
|---|---|---|---|---|
| Settings persistence | Yes | Yes | Yes | Yes |
| Schedule evaluation | Yes | Yes | Yes | Yes |
| Retry and fallback | Yes | Yes | Yes | Yes |
| Tray/menu actions | Limited | Possibly | Yes | Yes |
| Tray menu identity (no crash on edit/refresh) | No | Yes (smoke assert) | Yes | Yes |
| Second-instance activation | No | Yes | Yes | Yes |
| Launch at login | No | Contract test | Yes | Yes |
| Sleep/wake recovery | Gap logic only | Limited | Yes | Yes |
| LibVLC playback | Fake engine | Limited | Yes | Yes |
| Native ARM64 media playback | No | No | No | Manual Apple Silicon gate |

Not every native integration is practical to fully automate — but every critical capability has an owner and a verification method.

## 12. Revised execution order (when we start)

1. **Behavior inventory** — list every behavior in either front-end: first launch, restore settings, tray click, close-to-tray, quit, schedule starts/stops, retry/fallback, volume persistence, stream errors, sleep/wake recovery, startup registration, open-settings-folder, second-launch activation, crash recovery, log locations. It becomes the matrix in §11.
2. **Characterization tests before moving code** — scheduler, retry/fallback, state-machine transitions, second-instance protocol. Guard existing behavior even if the tests initially mirror quirks.
3. **Run the three spikes** (§7.3) — Apple Silicon LibVLC, Windows `SystemEvents`, macOS wake notification — before code movement consumes the migration.
4. **Extract playback coordination from Avalonia** — before merging the Windows shell; per the §5 state machine + cancellation spec.
5. **Create `DialShift.App` from the Avalonia project** — Mac-only initially; verify identical behavior to the current Mac app.
6. **Add platform-service abstractions + macOS implementations** — preserve existing Mac behavior *through* the abstraction first; this proves the boundary before Windows features arrive.
7. **Implement Windows platform services** — startup registration, file reveal, sleep/resume, icon assets, platform release settings.
8. **Port Windows behavior incrementally** — tray, single instance, settings, playback, schedule, sleep/wake, launch-at-login validated individually; don't wait for WPF deletion to test.
9. **Build CI + release artifacts** — native Windows/macOS runners (matrix), compile + test + publish, before retiring old projects.
10. **Short parallel validation phase** — keep WPF temporarily available for a release/beta cycle, or retain a tagged last-known-good commit + behavior checklist; don't delete immediately after the first green build.
11. **Retire old projects + update documentation** — delete WPF/WinForms only after the equivalent Windows smoke path is demonstrated.

## 13. Open questions to confirm before starting

- Is losing the WPF-native window styling acceptable for the Windows build?
- ARM64 spike outcome — native arm64 LibVLC feasible, or ship an explicit Intel/Rosetta build?
- Is the `NSWorkspace.DidWakeNotification` interop dependency acceptable, or timer-gap-only for the first consolidation?
- `.icns` app icon as part of this work — yes (recommended) or later?

## 14. Decision

**Proceed with Avalonia consolidation** — with the upgraded plan. Non-negotiable before implementation begins:

- Define playback state transitions, cancellation ownership, and stale-operation handling (§5) before code moves.
- Keep process/shell concerns — especially single-instance — out of the domain core (§6).
- Treat `PipeOptions.CurrentUserOnly` accurately for `net10.0`; validate macOS socket permissions rather than assuming the .NET 11 `0600` behavior (§7.5).
- Run the three early spikes (§7.3): Apple Silicon LibVLC, Windows `SystemEvents`, macOS wake notification.
- Add the definition of done and cross-platform acceptance matrix (§11) before code movement begins.
- Preserve — or replace — every meaningful legacy smoke check before deleting the WPF/WinForms frontend.

That gives the biggest benefit — one UI and one behavior implementation — without embedding a new long-term architectural constraint inside the Avalonia app.
