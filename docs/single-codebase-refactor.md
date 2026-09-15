# Single Codebase Refactor — Windows + macOS via Avalonia

> **Status:** Deferred — not a priority yet. This document is a bootstrap: a ready-to-execute plan for when we want to collapse the two front-ends into one.

## 1. Context

DialShift currently ships two desktop front-ends over a shared core:

| Project | UI toolkit | Platform | Purpose |
|---|---|---|---|
| `DialShift/` | WPF + WinForms | Windows (`win-x64`) | original app |
| `DialShift.Mac/` | Avalonia | macOS (`osx-x64`) | the macOS port |
| `DialShift.Core/` | — | any | models, scheduler, settings persistence (shared) |
| `DialShift.Tests/` | — | any | deterministic logic tests (shared) |

The domain logic lives in `DialShift.Core` (one source). But the **UI and playback orchestration are duplicated** across the two front-ends: `MainWindow`, `Dialogs`, `RadioController`, and app lifecycle/tray/startup each exist twice (WPF and Avalonia). Today that is ~1,300 duplicated lines out of ~1,700 total.

## 2. Goal

Maintain **one front-end** that builds for both platforms. One `MainWindow`, one `Dialogs`, one `RadioController`, one `App`. Publishing produces two installers from the same source:

```bash
dotnet publish -r win-x64   # Windows
dotnet publish -r osx-x64   # macOS
```

The result is one codebase with a thin, isolated platform layer — not zero platform-specific code, but ~50 lines of it instead of a whole second project.

## 3. High-level benefits

- **One place to change UI or playback behavior** — no more editing the same feature twice.
- **No drift** — the two `RadioController`s (retry/fallback/volume/schedule-trigger) can no longer fall out of sync.
- **One UI to design and polish** — a single look across both OSes.
- **Simpler tooling** — one project to build/test, one CI path, one set of screenshots.
- **Keeps the hard-won port work** — the Avalonia macOS port becomes the front-end for both, rather than a parallel maintenance burden.

## 4. Before → after

### Before (current)

```
DialShift/            WPF + WinForms (Windows)
  App.xaml(.cs)       lifecycle, tray, startup, sleep/resume, single-instance
  MainWindow.cs       UI
  Dialogs.cs          dialogs (MessageBox)
  RadioController.cs  playback engine (WPF DispatcherTimer)
  NativeChrome.cs     DWM dark title bar
  SmokeChecks.cs      Windows smoke-test harness
  Assets/dialshift.ico
DialShift.Mac/        Avalonia (macOS)
  App.axaml(.cs)      lifecycle, tray, startup, single-instance
  MainWindow.cs       UI
  Dialogs.cs          dialogs (modal windows)
  RadioController.cs  playback engine (Avalonia DispatcherTimer)
  Program.cs          entry point
  Assets/tray.png
DialShift.Core/       shared logic (Models.cs)
DialShift.Tests/      shared tests
```

### After (target)

```
DialShift.App/        Avalonia, cross-platform (single front-end)
  App.axaml(.cs)      one lifecycle, one tray (Avalonia TrayIcon), one single-instance
  MainWindow.cs       one UI
  Dialogs.cs          one set of dialogs (modal windows)
  RadioController.cs  one playback engine (Avalonia DispatcherTimer)
  Program.cs          entry point
  Platform/
    Startup.cs        launch-at-login  (Windows registry vs macOS LaunchAgent)
    Power.cs          sleep/resume hook (Windows SystemEvents vs macOS NSWorkspace)
    OpenFolder.cs     reveal settings dir (explorer.exe vs open)
  Assets/             tray.png, dialshift.ico, (optional) dialshift.icns
DialShift.Core/       shared logic (unchanged)
DialShift.Tests/      shared tests (unchanged)
```

`DialShift/` (WPF) is retired.

## 5. What changes — backend vs frontend

### Backend (logic) — essentially unchanged

- `DialShift.Core` stays as-is: `Models.cs` already contains the models, `Scheduler`, and `SettingsStore`, and both platforms already consume it identically. **No backend change is required for this refactor.**
- *Optional (not required):* the playback/retry/fallback engine in `RadioController` could later be lifted into a shared project to remove the last piece of duplicated "hard" logic. This is a separate, lower-priority step and is out of scope here.

### Frontend (UI + platform integration) — fully consolidated

- Merge the two UI projects into one Avalonia project.
- Replace the WPF/WinForms-specific pieces (WinForms `NotifyIcon`, WPF `DispatcherTimer`, `SystemEvents`, `Registry`) with Avalonia equivalents plus a small `Platform/` abstraction.
- Keep the macOS port's Avalonia implementation as the base, then re-add Windows support on top of it.

## 6. Detailed change list (bootstrap checklist)

This is the concrete work, listed file-by-file. No code is written here — just what changes and where — so it can be executed directly when we start.

### 6.1 Project structure

1. **Create `DialShift.App/`** as the new single front-end, based on the current `DialShift.Mac/` sources (rename + relocate).
2. **Retire `DialShift/`** (WPF) — delete the project after its Windows-specific pieces have been ported (see 6.3).
3. **Update `DialShift.slnx`** to reference `DialShift.Core`, `DialShift.Tests`, and `DialShift.App` (dropping the two UI projects).
4. **Delete `DialShift.Mac/`** once its contents have moved into `DialShift.App/`.

### 6.2 `DialShift.App/DialShift.App.csproj`

- `TargetFramework` stays `net10.0` (no `-windows` suffix), `ImplicitUsings=enable`, `Nullable=enable`.
- Replace the fixed `<RuntimeIdentifier>osx-x64</RuntimeIdentifier>` with `<RuntimeIdentifiers>win-x64;osx-x64</RuntimeIdentifiers>` so both can be published.
- **Conditional native VLC package** — pull the right one per platform:
  - `VideoLAN.LibVLC.Windows 3.0.23.1` on Windows.
  - `VideoLAN.LibVLC.Mac 3.1.3.1` on macOS.
  - `LibVLCSharp 3.10.1` (shared, unconditional).
- Keep the Avalonia packages (`Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent` 11.3.22).
- Add both icon assets (`Assets/**` via `AvaloniaResource`); consider `ApplicationIcon` per platform.

### 6.3 Platform-specific code → `DialShift.App/Platform/`

Extract the four genuinely OS-specific behaviors into small interfaces + per-OS implementations, chosen at startup with `OperatingSystem.IsWindows()` / `IsMacOS()` or `#if WINDOWS` / `#if MACOS` guards.

1. **`Platform/Startup.cs`** — launch-at-login:
   - Windows: write/remove the `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value (currently `DialShift/App.xaml.cs` `SetStartup`).
   - macOS: write/remove `~/Library/LaunchAgents/com.tsiger.dialshift.plist` (currently `DialShift.Mac/App.axaml.cs` `SetStartup`).

2. **`Platform/Power.cs`** — sleep/resume:
   - Windows: subscribe to `SystemEvents.PowerModeChanged` → call `Radio.ResumeFromSleep()` on `PowerModes.Resume` (currently in `DialShift/App.xaml.cs`).
   - macOS: **currently missing** — wire `NSWorkspace` sleep/wake notifications (e.g. `NSWorkspace.DidWakeNotification`) so macOS also reconnects after sleep. This closes a real gap in the current Mac port.
   - **Note:** this requires a small native interop on macOS (no pure-managed Avalonia API); either a tiny `[DllImport]`/`NSWorkspace` observer or an Avalonia platform hook.

3. **`Platform/OpenFolder.cs`** — "Open settings folder":
   - Windows: `explorer.exe <path>`.
   - macOS: `open <path>` (already used in `DialShift.Mac/MainWindow.cs`).

4. **Single-instance** — unify on one mechanism:
   - The Mac port already uses a portable file-lock (`FileStream` with `FileShare.None` on `.single-instance.lock`) that works on Windows too. Replace the Windows named-`Mutex` + `EventWaitHandle` approach with this (or keep an OS-conditional pair if the Windows "activate existing window" behavior must be preserved).

### 6.4 Shared front-end files

5. **`App.axaml` / `App.axaml.cs`** — keep the Avalonia version as the single lifecycle:
   - Tray icon: Avalonia `TrayIcon` + `NativeMenu` (already cross-platform; used on macOS, works on Windows too).
   - Drop WPF/WinForms references (`Forms.NotifyIcon`, `ContextMenuStrip`, balloon tips, `MessageBox`) in favor of the existing Avalonia equivalents.
   - Route startup and power hooks through `Platform/` (6.3) instead of `Registry`/`SystemEvents` directly.
   - Error/save dialogs use the existing `Message` modal-window helper instead of `MessageBox`.

6. **`RadioController.cs`** — keep the Avalonia version (uses Avalonia `DispatcherTimer`), delete the WPF one. Changes:
   - Drop the WPF `Dispatcher` constructor parameter; use the Avalonia `DispatcherTimer`/`Dispatcher.UIThread.Post` path already in the Mac version.
   - Ensure `ResumeFromSleep()` is actually invoked from `Platform/Power.cs` on both OSes.

7. **`MainWindow.cs`** — keep the Avalonia version as the single UI. Changes:
   - "Open settings folder" → call `Platform/OpenFolder` instead of hard-coding `open`.

8. **`Dialogs.cs`** — keep the Avalonia dialogs (already modal, cross-platform); delete the WPF `MessageBox`-based dialogs.

9. **`Program.cs`** — keep the Avalonia entry point (single-instance + `StartWithClassicDesktopLifetime`).

### 6.5 Retired / undecided pieces

10. **`DialShift/NativeChrome.cs`** — WPF/DWM dark-title-bar helper; not needed under Avalonia (theming lives in `App.axaml`). Retire.
11. **`DialShift/SmokeChecks.cs`** — the Windows-only `--smoke-test` integration harness. Decide: port to Avalonia, or drop for now and rely on `DialShift.Tests` + manual checks. (Low priority; do not block the merge on this.)
12. **Icons** — keep `Assets/tray.png` (Avalonia tray); add a Windows `.ico` and (optional) a macOS `.icns` app icon for the bundle. The current Mac `.app` has no icon file yet.

### 6.6 Docs

13. **`README.md`** — update the "Project" section (single `DialShift.App`, remove `DialShift`/`DialShift.Mac`) and the build instructions (one project, two `dotnet publish -r` targets).
14. **`THIRD-PARTY-NOTICES.md`** — already lists both VLC packages and Avalonia; confirm the conditional-package wording still reads correctly.
15. **Remove the "Run on macOS" special-casing** if/when the README is restructured around a single app (optional polish).

## 7. What stays OS-specific after the refactor

| Concern | Windows | macOS | Where |
|---|---|---|---|
| Native VLC lib | `VideoLAN.LibVLC.Windows` | `VideoLAN.LibVLC.Mac` (x86_64, Rosetta 2 on Apple Silicon) | csproj (conditional) |
| Launch at login | Registry `Run` key | LaunchAgent plist | `Platform/Startup.cs` |
| Sleep/resume | `SystemEvents.PowerModeChanged` | `NSWorkspace` wake notification | `Platform/Power.cs` |
| Reveal folder | `explorer.exe` | `open` | `Platform/OpenFolder.cs` |
| Tray icon | Avalonia `TrayIcon` | Avalonia `TrayIcon` | shared (no split) |
| UI + playback | Avalonia | Avalonia | shared (no split) |

Everything else — models, scheduler, settings persistence, UI, dialogs, playback engine — is single-source.

## 8. Risks & gotchas

- **Loss of WPF-native look** — the Windows app loses `NativeChrome.cs` and WPF styling; it will render with the Avalonia dark theme instead. Acceptable, but confirm this is desired.
- **Rosetta / x86_64 libvlc on macOS** — unchanged; Apple Silicon still runs under Rosetta 2 because `VideoLAN.LibVLC.Mac` ships no arm64 `libvlc.dylib`.
- **Sleep/resume on macOS is a real gap today** — the current Mac port never calls `ResumeFromSleep()`. The refactor should close this, but it needs native interop (`NSWorkspace`), which is the one non-trivial platform hook.
- **Single-instance behavior** — the Windows app activates the existing window on a second launch; the file-lock approach just exits. Confirm whether "bring existing window to front" is worth preserving on Windows.
- **Smoke test harness** — decide whether to port `SmokeChecks` to Avalonia or defer it; don't lose the Windows integration checks silently.
- **App bundle** — keep the `build-mac-app.sh` assembly step (Info.plist + `LSUIElement=true`) for macOS; the Windows side may want a parallel packaging script.

## 9. Suggested execution order (when we start)

1. Scaffold `DialShift.App/` from `DialShift.Mac/`, target both RIDs, add conditional VLC packages. Build for both.
2. Add `Platform/` (Startup, Power, OpenFolder) and route the app through it.
3. Wire macOS sleep/resume (`NSWorkspace`) and confirm Windows `SystemEvents` still works.
4. Unify single-instance; verify second-launch behavior on both OSes.
5. Delete `DialShift/` (WPF) and `DialShift.Mac/`; update `DialShift.slnx`.
6. Update `README.md` + `THIRD-PARTY-NOTICES.md`; refresh build scripts.
7. Publish both `win-x64` and `osx-x64`; smoke-test playback, tray, startup, and schedule on each.

## 10. Open questions to confirm before starting

- Is losing the WPF-native window styling acceptable for the Windows build?
- Should the Windows "activate existing instance on second launch" behavior be preserved?
- Port `SmokeChecks.cs` to Avalonia, or defer it?
- Add a proper macOS `.icns` app icon as part of this work, or later?
