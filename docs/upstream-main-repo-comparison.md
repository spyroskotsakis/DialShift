# Report: Upstream tsiger/DialShift vs our fork — macOS support comparison

> Date: 2026-09-16 · Method: read-only `git clone` of `https://github.com/tsiger/DialShift` (no commits, no changes, no PRs to the upstream repo).
> Local clone: `~/Desktop/RadioTsigerProject/dialshift-upstream-114846/`

## 1. Repo identity & history

| | Upstream `tsiger/DialShift` | Ours `spyroskotsakis/DialShift` (local) |
|---|---|---|
| Visibility | Public, 6 stars | Private/local working copy |
| Commits | **3** | 11 (incl. 3 shared with upstream) |
| Branches | `main`, `codex/macos-support` | `main` |
| Tags/releases | `v0.1.0`, `v0.2.0` | — |
| Common ancestor | `71f3cd3` "Initial DialShift Windows tray radio app" (2026-09-15 12:02) | same |

Both repos **forked from the identical initial commit** `71f3cd3`. After that they diverged completely — upstream evolved via the `codex/macos-support` branch, ours via our own work.

### Upstream commit timeline (complete — all 3 commits)

| Commit | Date | Tag | Content |
|---|---|---|---|
| `71f3cd3` | 2026-09-15 12:02 | — | Initial commit: **Windows-only** WPF app (DialShift/, DialShift.Core, DialShift.Tests, build.ps1). No macOS anywhere. |
| `ab896f3` | 2026-09-16 10:30 | `v0.1.0` | README polish: prominent Windows release download + launch instructions. Still Windows-only. |
| `6071e66` | 2026-09-16 11:42 | `v0.2.0` | **macOS support lands entirely here** (merged from `codex/macos-support`): +7,194 / −51 lines, 34 files. |

## 2. Was Apple macOS supported from the beginning?

**No.**

- The initial commit and `v0.1.0` are Windows-only.
- macOS support is 100% contained in the **final commit `6071e66`** (v0.2.0, released ~1 hour before this report).
- The macOS work was developed on a separate branch `codex/macos-support` (an autonomous-agent branch, per its name) and merged once.
- Meanwhile **we** independently added our own macOS port (`DialShift.Mac`, commit `12a9a12`) from the same Windows-only base the day before. Two parallel, independent Mac implementations of the same app now exist.

## 3. What upstream improved from v0.1.0 → v0.2.0 (the whole Mac support)

| Area | What shipped in v0.2.0 |
|---|---|
| New project | `DialShift.Desktop` — Avalonia **12.1.2** (we: 11.3.22), `net10.0`, no fixed RID |
| **Playback** | **Native AVPlayer via raw `objc_msgSend` P/Invoke** (`MacAudioSession.cs`, ~70 lines, zero dependencies). **No LibVLC, no Rosetta, native Apple Silicon.** |
| Audio abstraction | `Audio.cs`: `IAudioSession` + `AudioSnapshot`; `AudioFactory` selects backend at runtime (macOS → AVPlayer; Windows dev-preview → LibVLC behind `WINDOWS_PREVIEW`) |
| Controller design | `RadioController` with **constructor-injected `open` and `clock` delegates** — unit-testable, UI-agnostic (exactly the architecture our refactor brief §4.1/§5 asks for) |
| Sleep/wake | Timer-gap heuristic: `Tick()` gap > 15 s ⇒ `ResumeFromSleep()` — **the same approach we implemented locally** (commit `6e99799`) |
| Single instance | File-lock (`running.lock`, `FileShare.None`) + **"show-window" signal file** polled by the 1 s dispatcher timer (2nd instance writes the file and exits) |
| Tray/menu bar | `TrayIcon` + `NativeMenu`, **template image** via `MacOSProperties.SetIsTemplateIcon`; dock activation via `IActivatableLifetime` (`ActivationKind.Reopen`) |
| **Bug fix** | **Native menu identity: menu contents update in place instead of replacing `TrayIcon.Menu`** — fixes a crash after saving station/schedule edits |
| Launch at login | Per-user LaunchAgent plist, launched as `open -a <bundle> --args --tray`; requires the app in `/Applications` (explicit error otherwise) |
| **Mac smoke tests** | `--smoke-test --output <dir>`: real editor-window automation (field validation, add/edit/delete, conflict rejection, menu-identity assertions), **muted live-stream playback check for every station**, page screenshots, settings round-trip, hide/restore; writes `results.json` |
| New test project | `DialShift.Desktop.Tests` — controller checks with a **simulated audio backend** |
| Packaging | Cross-build **from Windows**: `build-macos.ps1` + `package-macos.py` — downloads checksum-verified `rcodesign` (apple-platform-rs), assembles `.app`, ad-hoc signs, verifies Mach-O ARM64 slices + code-signature page hashes in pure Python, ZIPs with explicit Unix permissions |
| Settings portability | **Import/export stations & schedule** (JSON), incl. importing the Windows `settings.json`; strict import validation; backup before import |
| Docs/release | `MACOS.md` (install, limits, build-from-source), `docs/releases/v0.2.0.md`, SHA256SUMS, Avalonia/SkiaSharp/HarfBuzz/MicroCom/Tmds.DBus licenses |

Release artifacts: `DialShift-0.2.0-win-x64.zip` and `DialShift-0.2.0-osx-arm64.zip` (Apple Silicon, macOS 14+, ad-hoc signed, not notarized). Known limits stated: no track-title metadata on Mac (AVPlayer), hardware sleep/wake + login "need broader Mac testing", cosmetic issues.

## 4. Side-by-side: our Mac implementation vs upstream's

| Concern | Ours (`DialShift.Mac`) | Upstream (`DialShift.Desktop`) | Verdict |
|---|---|---|---|
| UI toolkit | Avalonia 11.3.22 | Avalonia 12.1.2 | upstream newer |
| **Playback** | **LibVLC (`VideoLAN.LibVLC.Mac` x86_64)** → **Rosetta 2 on Apple Silicon**, `osx-x64` RID | **Native AVPlayer (P/Invoke)** → **native arm64**, no VLC/no Rosetta | **upstream solves the ARM64 problem we listed as an open spike** (§4.3 of our brief: "replacement playback backend" — that's exactly what they did) |
| Sleep/resume | timer-gap heuristic (our fix `6e99799`) | same timer-gap heuristic | equivalent |
| Single-instance activation | **Named pipe** (instant, in-proc signal) | signal **file** polled every 1 s (2nd instance writes file + exits) | ours cleaner/more robust; theirs simpler, works |
| Tray menu updates | **rebuilds `TrayIcon` + `NativeMenu` on refresh** | **updates menu items in place** (fixes a real crash) | **we still have the crash bug upstream explicitly fixed** |
| Tray icon | `tray.png` | `tray.png` **+ `SetIsTemplateIcon(true)`** (menu-bar-correct monochrome) | upstream more correct on macOS |
| Launch at login | LaunchAgent plist (ours) | LaunchAgent via `open -a --args --tray` + Applications-location guard | similar; theirs has clearer error handling |
| Smoke tests (Mac) | **none** | full editor-automation + muted playback + screenshots + results.json | **we have nothing; upstream shipped the harness our brief says not to drop** |
| Controller testability | UI-bound (Avalonia `DispatcherTimer`) | injected `open`/`clock` delegates + `Desktop.Tests` with fake backend | upstream already matches our refactor target |
| Packaging | `build-mac-app.sh` on Mac | cross-build on Windows + rcodesign + Mach-O/signature verification + ZIP perms | upstream more robust (also verifiable cross-platform) |
| Settings import/export | no | yes (incl. Windows → Mac) | upstream |
| Station catalog (`data/`) | **yes — 8.6k stations, XLSX, collections** | no (3 SomaFM defaults only) | ours |
| Refactor plan docs | yes (`docs/single-codebase-refactor.md`) | no | ours |
| Version | 0.1.0 | 0.2.0 | upstream |

## 5. Answers to the audit questions

1. **"Was Apple OSX supported from the beginning?"** — No. Windows-only until the final commit (v0.2.0, 2026-09-16). Our own Mac port was also added by us, not inherited.

2. **"What exactly improved vs the initial first version?"** — Everything in §3: the entire macOS app (UI, AVPlayer audio, tray, lifecycle, packaging, tests, docs) arrived in one commit; the first two commits contain no macOS code at all.

3. **"Did we oversee something?"** — Yes, three concrete items:
   - **a) The native-menu crash.** Upstream's v0.2.0 release notes state: *"Fixed the Mac crash after station/schedule edits by updating the existing native menu"* (Avalonia's macOS exporter binds the native proxy to the menu instance; replacing it throws). Our `DialShift.Mac/App.axaml.cs` `Refresh()` still creates a **new** `TrayIcon` + `NativeMenu` → we carry the same bug class.
   - **b) Native ARM64.** We ship LibVLC-x64 (Rosetta). Upstream replaced the backend entirely with AVPlayer — answering our refactor brief's §4.3 spike with the "replacement playback backend" option, and demonstrating the P/Invoke approach is small (~70 lines) and workable.
   - **c) Mac smoke coverage.** Upstream ships the automated editor/playback smoke harness our brief §4.5 said must not be dropped; we have no Mac smoke checks at all.
   - Also worth adopting: template tray icon, Avalonia 12, injected-clock controller + fake-backend tests, settings import/export, and their robust cross-build packaging if we ever want Windows-hosted Mac builds.

4. **What do WE have that upstream doesn't?** Named-pipe activation (better than their signal file), the entire radio-station data section (catalog/XLSX/collections/logos), and the architecture research/refactor brief. No upstream-only feature blocks ours; the divergences are additive both ways.

## 6. Recommendations (map to our refactor brief)

| # | Recommendation | Brief section | Effort |
|---|---|---|---|
| 1 | **Adopt AVPlayer (`MacAudioSession`-style P/Invoke) for the Mac backend** — closes the ARM64 spike decisively; or port upstream's file verbatim (LGPL-free, ~70 lines) | §4.3 spike → answered | small |
| 2 | **Fix tray-menu identity** in our `DialShift.Mac` now (update items in place) — real crash we currently have | (pre-refactor hotfix) | tiny |
| 3 | Add `SetIsTemplateIcon(true)` for the menu-bar icon | §7.2 icons | tiny |
| 4 | Rebase smoke coverage on upstream's `--smoke-test` editor/playback harness when we merge | §4.5 | port |
| 5 | Treat upstream's injected-`open`/`clock` `RadioController` as the reference implementation for our `PlaybackCoordinator` extraction | §5 | reference |
| 6 | Upgrade Avalonia to 12.1.2 during the merge | §7.2 | small |
| 7 | Consider upstream's Windows cross-build packaging (rcodesign) if Mac builds must run on Windows | §8 packaging | optional |

## 7. Appendix — upstream file inventory added in v0.2.0

```
DialShift.Desktop/
  App.cs                     lifecycle, tray (template icon), single-instance file-lock + show-window file,
                             launch-at-login plist, import/export, debounced save, log
  Audio.cs                   IAudioSession + AudioFactory (runtime OS switch)
  MacAudioSession.cs         AVPlayer via objc_msgSend P/Invoke (~70 lines)
  WindowsAudioSession.cs     LibVLC dev-preview backend (WINDOWS_PREVIEW)
  RadioController.cs         injected open/clock delegates; retry/fallback/schedule/wake-gap (125 lines)
  MainWindow.cs / Dialogs.cs / MessageDialog.cs / EditorSmokeChecks.cs / SmokeChecks.cs / Program.cs
  DialShift.Desktop.csproj   Avalonia 12.1.2, net10.0, WINDOWS_PREVIEW + LibVLC for win-x64 preview
DialShift.Desktop.Tests/     controller checks with simulated audio backend
scripts/build-macos.ps1      cross-build orchestration (tests → publish osx-arm64 → package)
scripts/package-macos.py     206-line pure-Python .app assembler + ad-hoc signer + Mach-O/signature verifier
MACOS.md / docs/releases/v0.2.0.md
licenses/                    Avalonia, SkiaSharp, HarfBuzzSharp, MicroCom, Tmds.DBus
```

*This report is a read-only analysis artifact. No upstream files were modified, and nothing was committed anywhere.*

---

## 8. Q: Was Apple macOS supported from the beginning? — CODE-LEVEL proof (not README)

The earlier tree-level answer ("no") is confirmed **at source level** — v1 could not even *compile* for macOS:

| v1 evidence (commit `71f3cd3`) | File | Why it excludes macOS |
|---|---|---|
| `<TargetFramework>net10.0-windows</TargetFramework>` | `DialShift/DialShift.csproj` | Windows-only TFM — cannot target macOS |
| `<UseWPF>true</UseWPF>` `<UseWindowsForms>true</UseWindowsForms>` | same | WPF + WinForms are Windows-exclusive UI frameworks |
| `<RuntimeIdentifier>win-x64</RuntimeIdentifier>` | same | fixed Windows RID |
| `VideoLAN.LibVLC.Windows` | same | Windows-only native VLC assets |
| `using System.Windows; … Microsoft.Win32; Forms = System.Windows.Forms;` | `DialShift/App.xaml.cs` | WPF + Registry + WinForms NotifyIcon |
| `new Mutex(true, "Local\\DialShift.App", …)` + `EventWaitHandle("Local\\DialShift.Activate")` | same | named kernel sync objects — Windows-only semantics |
| `SystemEvents.PowerModeChanged += PowerChanged;` | same | `Microsoft.Win32.SystemEvents` — Windows-only |
| `MessageBox.Show(...)` | same | WPF |
| `using System.Windows.Threading; … DispatcherTimer` | `DialShift/RadioController.cs` | WPF dispatcher |
| `app.manifest` | `DialShift/` | Windows DWM/dpiAware application manifest |

**Conclusion:** from the beginning (initial commit and v0.1.0) the codebase was **not macOS-compatible at any level** — not in TFM, not in UI framework, not in APIs, not in native assets. macOS support exists only from v0.2.0 (upstream) and from our own `DialShift.Mac` port (`12a9a12`). The middle commit `ab896f3` (v0.1.0) contains README changes only.

## 9. Deep audit: upstream v2 vs our implementation — which is better?

Both implementations share the same ancestor and the same one-codebase idea; they diverge in execution. Head-to-head on code:

| Dimension | Upstream v2 (`DialShift.Desktop`) | Ours (`DialShift.Mac`) | Winner |
|---|---|---|---|
| **Playback backend** | Native AVPlayer via `objc_msgSend` P/Invoke (~70 lines, zero deps) → **native arm64, no Rosetta** | LibVLC x86_64 → **Rosetta 2 on Apple Silicon** | **Upstream** (native arm64 solves our open spike; AVPlayer covers the app's documented MP3/AAC/HLS) — trade-off: no track metadata, fewer exotic formats |
| **Controller architecture** | Injected `open` + `clock` delegates; **no internal timer** (App ticks it); UI-agnostic | Creates own LibVLC engine task + **own `DispatcherTimer`** in ctor; UI-bound | **Upstream** (exactly our brief's §4.1 target) |
| **Controller tests** | `DialShift.Desktop.Tests` with `FakeAudio` + fake clock — deterministic schedule/wake/retry/fallback/volume assertions | none for the controller (only Core logic tests) | **Upstream** |
| **Stale-retry protection** | retry timing by wall-clock fields only; no generation counter | `generation` counter + `retiring` task list (matches our brief §5.4) | **Ours** |
| **Single-instance activation** | file-lock + `show-window` **file polled every 1 s** by the UI timer | file-lock + **named pipe**, async `WaitForConnectionAsync` → instant | **Ours** (upstream's file approach also sidesteps pipe-permission concerns entirely) |
| **Tray/menu** | updates `NativeMenuItem`s **in place** (fixes the edit-crash), `SetIsTemplateIcon(true)` (monochrome menu-bar icon) | **rebuilds `TrayIcon`+`NativeMenu` on refresh → carries the crash upstream fixed**; plain PNG icon | **Upstream** (we have a live bug) |
| **Dock reopen** | `IActivatableLifetime` `ActivationKind.Reopen` handled | not handled | Upstream |
| **Launch at login** | plist + `open -a … --args --tray` + requires-Applications guard with explicit error | plist (direct), no location guard | Upstream |
| **Mac smoke tests** | `--smoke-test`: editor automation (add/edit/delete/conflicts/menu-identity), **muted live playback per station**, screenshots, settings round-trip, `results.json` | **none** | **Upstream** |
| **Packaging** | cross-build from Windows; checksum-verified `rcodesign`; pure-Python Mach-O ARM64-slice + code-signature page-hash verification; ZIP with Unix perms; SHA256SUMS | `build-mac-app.sh` (build on Mac) | **Upstream** (more rigorous + reproducible) |
| **Settings portability** | import/export JSON incl. Windows `settings.json`, strict validation, backup | no | Upstream |
| **Station catalog** | 3 SomaFM defaults only | 8.6k-station `data/` catalog, XLSX, collections, logos | **Ours** |
| **Architecture research** | — (implicitly implements much of our brief) | `docs/single-codebase-refactor.md` (post-audit revision) | Ours |
| Avalonia | 12.1.2 | 11.3.22 | Upstream |

### Verdict

**Upstream v2 is the better macOS implementation today**, on four decisive axes: native-ARM64 playback (no Rosetta), a testable injected controller with real fake-backend tests, macOS lifecycle polish (menu-identity crash fix, template icon, Dock reopen, Applications guard), and a rigorous cross-build/verification packaging chain. It also proves our refactor brief was directionally right: upstream implemented its §4.1 (coordination out of the UI) and §4.5 (don't drop smoke testing) targets independently.

**We win** on single-instance activation (named pipe vs 1 s file polling) and on the radio data catalog, which upstream lacks entirely.

**Recommended endgame** (all local, no upstream interaction): merge upstream's four wins into our codebase — adopt `MacAudioSession`-style AVPlayer (closes the ARM64 spike), port their injected-controller + `Desktop.Tests` pattern, fix our tray-menu-identity crash now, add template icon + Dock reopen — while keeping our named-pipe activation, data catalog, and the brief's state-machine/cancellation rigor (generation counters — which *neither* implementation fully formalizes). This lands exactly where our brief §14 says the merge should go.
