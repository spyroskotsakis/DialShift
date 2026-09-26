# Open items: native verification and release sign-off

> **As of 2026-09-25**, after the pre-merge sweep, SW-N4, the first public push and the `v0.3.0` release. The public repository has `main` at `6d1a1cc`, `refactor/single-codebase-timezone` at `b9cbeea` (= `v0.3.0`) and the tags `v0.3.0-rc.1`, `v0.3.0-rc.2`, `v0.3.0` and `legacy-last-known-good`. **`v0.3.0` is published as the latest full release** (public Release run [`36172793355`](https://github.com/spyroskotsakis/DialShift/actions/runs/36172793355)), before the native checks (D57). The CI evidence below is private run `36163420184` at `bc807b5` and the public runs listed in [§2.10](#210-release-engineering-not-blocked-on-hardware-7-items); the private repository refuses every Actions job for billing, and the backup release path is `scripts/release-local.sh` (D58). Since 2026-09-26 the public repository also has `feature/add-station-catalog-search` and `main` at `3c8543c` (= `v0.4.0`), green in CI [`36221096207`](https://github.com/spyroskotsakis/DialShift/actions/runs/36221096207) and [`36221096133`](https://github.com/spyroskotsakis/DialShift/actions/runs/36221096133) (`main` at `aaa3cb1` had failed, in CI `36219355899`, the one Windows check that D91 fixes in `cceff38`), and **`v0.4.0` is published as the latest release on both repositories** (public Release run [`36221102328`](https://github.com/spyroskotsakis/DialShift/actions/runs/36221102328); private by `scripts/release-local.sh`, D58; D92).
> **Brief 3 (the station catalog search in the Add station dialog, `docs/add-station-catalog-search.md`), 2026-09-26:** implemented on `feature/add-station-catalog-search` (app code at `cceff38`) and released as **`v0.4.0`** (tag at `3c8543c`, published 2026-09-26), a full release before the by-hand checks (D92; CHANGELOG `[0.4.0]`; [§2.10](#210-release-engineering-not-blocked-on-hardware-7-items)). Its acceptance rows, matrix §11 CAT-01..18, read 15 GREEN and 3 NATIVE-PENDING since the Windows CI run of [§2.12](#212-a-windows-ci-run-of-brief-3-done) (public CI `36219966368`); what it leaves open is NC-18 ([§2.11](#211-a-windows-machine-with-windows-powershell-51-1-item-brief-3)), NC-01's smoke from the extracted zip and its step (4b), step (6b) of NC-17, and the data decisions of [§2.13](#213-station-catalog-data-maintainer-decisions-brief-3).
> **Brief 4 (the Windows setup, `docs/windows-installer.md`), 2026-09-26: implemented, not merged or released.** An unsigned, per-user NSIS setup `DialShift-Setup-win-x64.exe` beside the unchanged zip and macOS download, on `feature/windows-installer` (D93–D99, D101); public CI `36230181228` at `b655d67`, `36231552864` at `cf21fc1` and `36233711767` at `b7017d7` are green on both OSes and the cross-build job; `36238278091` at `b76356e` green on all three jobs; the D101 round-seven commit awaits its run. Its rows INS-01..INS-20 (matrix §12): 1 GREEN, 10 WINDOWS-PENDING (that run), 5 NATIVE-PENDING (NC-19), 4 TODO (INS-13's R1 review, INS-17 and INS-18 at the first release with the setup, INS-19's docs review); its by-hand check NC-19 ([§2.1](#21-windows-physical-hardware-7-items)) is open.
> This is the hand-off list for the work that is still open after the briefs were implemented. The implementation task is closed. Only the items below remain.
>
> Related documents:
> - [README.md](../README.md): the project and how to build, test and run it.
> - [Acceptance matrix §9](acceptance-matrix.md#9-remaining-native-checks): the full procedure and pass criteria for every native check (NC-*).
> - [Matrix §9.1](acceptance-matrix.md#91-native-checks-that-require-the-user): the same checks grouped by what they need.
> - [docs/decisions.md](decisions.md): decisions D1–D99 and D101 (brief 3 from D59, brief 4 from D93; D100 is `main`'s).
>
> This file sums up and orders the work. If this file and matrix §9 disagree, §9 is right.

## Contents

1. [Status summary](#1-status-summary)
2. [Blockers by category](#2-blockers-by-category)
3. [Suggested order of execution](#3-suggested-order-of-execution)
4. [How to record results](#4-how-to-record-results)
5. [Known accepted limitations (not blockers)](#5-known-accepted-limitations-not-blockers)
6. [Closure criteria](#6-closure-criteria)

---

## 1. Status summary

### What is done

| Area | State | Evidence |
|---|---|---|
| Brief 1 (single codebase) and brief 2 (per-slot time zones) | Implemented. WPF and `DialShift.Mac` are retired, and one Avalonia app, `DialShift.App`, ships as `win-x64` and `osx-arm64` | Branch `refactor/single-codebase-timezone` at `1b16518`, pushed to the `private` remote (`spyroskotsakis/dialshift-dev`); since 2026-09-25 also on the public repository (the branch at `b9cbeea` = `v0.3.0`; `main` at `6d1a1cc`, since 2026-09-26 at `3c8543c` = `v0.4.0`, [§2.10](#210-release-engineering-not-blocked-on-hardware-7-items)) |
| CI | Green on both OSes | Run [`36163420184`](https://github.com/spyroskotsakis/dialshift-dev/actions/runs/36163420184) at `bc807b5`: **windows-latest 1,702 passed / 18 skipped**, **macos-latest 1,769 passed / 5 skipped**, **24/24 suites** on both. At `99a1122` (docs-only changes since): private runs `36164427376` (`main`) and `36164427469` (the branch), both green on `windows-latest` and `macos-latest`. On the public repository, every job green on `windows-latest` and `macos-latest`: `36168559994` (`main`, `99a1122`), `36168665136` (the branch, dispatched, `99a1122`), `36171245499` / `36171246418` (`main` / the branch at `b9cbeea`), `36172793815` / `36172796772` (`main` / `feature/add-station-catalog-search` at `6d1a1cc`). Brief 3 on the public repository: `36219267519` / `36219355899` (the branch / `main` at `aaa3cb1`) failed one `windows-latest` check (CAT-14's separator, D91); [`36219966368`](https://github.com/spyroskotsakis/DialShift/actions/runs/36219966368) (the branch at `cceff38`) is green: **windows-latest 2,627 passed / 25 skipped**, **macos-latest 2,704 passed / 6 skipped**, **26/26 suites** on both, the native smoke 36/36 on both, the bundle smoke 34/34, the `Install.ps1` cases and 32/32 verifier fixtures on both |
| Native smoke in CI | `--smoke-test --recovery-test` passes on both OSes (**34/34** in run `36126312566` at `1b16518`; the step fails on any failed check). The **bundle smoke** (the packaged `osx-arm64` app, extracted from the release zip with `unzip`) passes **32/32** with `MacAvPlayerPlaybackEngine` | Run `36163420184`: `smoke-win-x64` and `smoke-osx-arm64` artifacts, and the "Bundle smoke" step of the macOS job |
| Packaging | Both downloadable zips verified: `win-x64` by `verify-win-package.ps1`, `osx-arm64` by `verify-mac-app.sh --zip` after both `ditto` and `unzip` extraction. `Install.ps1` from the `win-x64` zip passes fresh install, upgrade, locked file, install lock and the three refusals (SW-N4) | Run `36163420184` |
| Acceptance matrix §3 (106 rows) | **62 GREEN / 44 NATIVE-PENDING / 0 TODO** | [Matrix §3](acceptance-matrix.md#3-acceptance-matrix) |
| Timezone QA tracker | QA-B1..B4 (blocking) all GREEN. QA-N1..N9: 8 GREEN, 1 NATIVE-PENDING (QA-N1). §9.2: 14 of 15 GREEN, TZ-12 NATIVE-PENDING (the unit half is green) | [Matrix §6](acceptance-matrix.md#6-timezone-qa-tracker-brief-2) |
| Review findings (§7.10) | All GREEN or accepted except SR-02 and NX-01, both NATIVE-PENDING. The pre-merge sweep's blockers (SW-B1 RT-07 flake, SW-B2 culture-dependent slot times) and should-fixes (SW-S1..S5) are fixed, and SW-N4 (`Install.ps1`, D56) is GREEN in CI `36163420184` ([§2.10](#210-release-engineering-not-blocked-on-hardware-7-items)) | [Matrix §7.10](acceptance-matrix.md#710-characterization-and-review-findings-tracked) |
| Decisions | D1–D99 and D101 recorded (brief 3: D59–D92; brief 4, the Windows setup: D93–D99 and D101; D100 is `main`'s) | [docs/decisions.md](decisions.md) |
| Brief 3 (station catalog search) | Implemented; released as `v0.4.0` on 2026-09-26 (D92; public Release run [`36221102328`](https://github.com/spyroskotsakis/DialShift/actions/runs/36221102328), private release by `release-local.sh`, [§2.10](#210-release-engineering-not-blocked-on-hardware-7-items)). Local gate on the dev Mac (D75): build with warnings as errors clean, **2,702 passed / 6 skipped, 26/26 suites** (orchestrator at `8836ec3`, under `caffeinate -i`; the 6 SKIPs are Windows-only), the pipeline self-test 320/320, the bundle smoke 36/36, both package verifiers on the real packages (8,270 stations) and the 32 verifier fixtures on both halves. Windows and macOS CI: public [`36219966368`](https://github.com/spyroskotsakis/DialShift/actions/runs/36219966368) at `cceff38` green on both OSes (**windows-latest 2,627 passed / 25 skipped, macos-latest 2,704 passed / 6 skipped, 26/26 suites**), after D91 fixed the one Windows failure of the first run | [Matrix §11](acceptance-matrix.md#11-add-station-catalog-search-brief-3): 15 GREEN, 3 NATIVE-PENDING (CAT-03, CAT-12, CAT-14), 0 WINDOWS-PENDING, 0 TODO |
| Brief 4 (Windows setup) | **Implemented, not merged or released** (2026-09-26): `docs/windows-installer.md`, D93–D99, D101; public CI `36230181228` at `b655d67`, `36231552864` at `cf21fc1` and `36233711767` at `b7017d7` green on both OSes, the Mac-built and Windows-built setups byte-identical; `36238278091` at `b76356e` green on all three jobs; the D101 round-seven commit awaits its run | [Matrix §12](acceptance-matrix.md#12-windows-installer-brief-4): 1 GREEN, 10 WINDOWS-PENDING, 5 NATIVE-PENDING, 4 TODO |
| Rollback point | Tag `legacy-last-known-good` (commit `82281e5`) keeps the last build of both old front-ends, including the last Intel Mac build. On the private and, since 2026-09-25, the public repository | `git show legacy-last-known-good`; `git ls-remote --tags origin` |

**Release status:** **`v0.4.0`** (`3c8543c`), the station catalog search, is the latest release on both repositories, published on 2026-09-26 on the same basis as `v0.3.0` (D92): on the public repository by Release run [`36221102328`](https://github.com/spyroskotsakis/DialShift/actions/runs/36221102328) with `DialShift-win-x64.zip`, `DialShift-macos-arm64.zip` and `SHA256SUMS.txt`, and on the private repository by `scripts/release-local.sh v0.4.0 --repo spyroskotsakis/dialshift-dev --publish` (D58) with the same three files ([§2.10](#210-release-engineering-not-blocked-on-hardware-7-items)). Its testing status is in the `[0.4.0]` section of `CHANGELOG.md` and in the README, and every check below stays open and is tracked for 0.4.x. Before it, **`v0.3.0`** (`b9cbeea`) was the first full release on the public repository, published on 2026-09-25 at 18:29 UTC by Release run [`36172793355`](https://github.com/spyroskotsakis/DialShift/actions/runs/36172793355) with `DialShift-win-x64.zip`, `DialShift-macos-arm64.zip` and `SHA256SUMS.txt` (D57). It has the same app code as the pre-release **`v0.3.0-rc.2`** (`99a1122`, the SW-N4 `Install.ps1` fix, D56), which follows **`v0.3.0-rc.1`** (`9b47afb`); both are published as pre-releases. Its testing status (Windows not yet tested by hand on a real PC; macOS tested on the developer's Mac only; unsigned) is in the `[0.3.0]` section of `CHANGELOG.md`, which is the release notes, and in the README. The private repository refuses every Actions job for billing, so its releases need the account fixed or the local backup path, `scripts/release-local.sh` (D58; [§2.10](#210-release-engineering-not-blocked-on-hardware-7-items)), which made its `v0.4.0` release. Releases follow the D53 pipeline (README "Releasing (maintainers)").

### What keeps the phase from sign-off

**This list is the only thing left.** It did not gate the `v0.3.0` release (D57), but it still gates the phase sign-off ([§6](#6-closure-criteria)). Every item needs something the development session cannot provide: Windows hardware, a clean or older Mac, signing credentials, a real login, a lid close, real network faults, or a person looking at the screen. No product code change is known to be needed. The only engineering left is:
- the temporary dev builds for NC-02 and NC-08 step 4, which disable the power-event source;
- the release-lane signing steps, once the credentials exist ([§2.2](#22-signing-credentials-and-certificates-2-items-release-only-not-performed));
- the small follow-ups in [§2.9](#29-follow-ups-that-depend-on-the-above), which depend on what these checks observe;
- the release-engineering and maintainer items in [§2.10](#210-release-engineering-not-blocked-on-hardware-7-items), which need no hardware.

Native checks: 18 open. NC-01..NC-13 and NC-15..NC-19, where NC-07, NC-13 and NC-17 are partly done; NC-18 comes from brief 3 (the catalog search, D82) and needs only a Windows machine with Windows PowerShell 5.1 ([§2.11](#211-a-windows-machine-with-windows-powershell-51-1-item-brief-3)); NC-19 comes from brief 4 (the Windows setup, D93–D99) and can run only once a build carries the setup. NC-14 (Intel) is not applicable (D13).

---

## 2. Blockers by category

How to read the tables:
- **Procedure**: the row in [matrix §9](acceptance-matrix.md#9-remaining-native-checks) with the full steps.
- **Pass (short)**: a summary. The full pass criteria are in §9.
- **Rows it unblocks**: the matrix rows that name this check. A row flips to GREEN only when **every** check it names has passed. [§2.9](#29-follow-ups-that-depend-on-the-above) groups the rows by the checks they need.
- Every check runs from the **CI artifact**, the zip a user downloads, with `dialshift.log` kept as evidence (§9 rule).

### 2.1 Windows physical hardware (7 items)

**Missing resource:** a physical Windows 11 x64 PC with speakers, a real mouse on the notification area, real sleep and a real sign-in. The hosted `windows-latest` runner cannot sleep, sign in interactively, click the tray or play audio. It uses the silent `adummy` output.

**Who can unblock:** the user, or anyone with such a PC who can download the CI artifact `DialShift-win-x64`.

| Item | What's left | Why blocked | Procedure | Pass (short) | Rows it unblocks |
|---|---|---|---|---|---|
| NC-01 | The Windows tray, window, audio and dialog pass, including the refusal to start beside the older WPF app (step 5, SW-S2, D55) and the Add-station catalog dialog (step 4b, brief 3: Narrator reading "Catalog status", "Station details" and the live regions; focus outlines on the pickers, Clear, Save and Cancel; Segoe UI widths; real remote logos, dark ones included; D83, D85). The smoke passes in CI on the build output (36/36 with the catalog check in public CI `36219966368`); the run from the zip extracted with Explorer is still open (CAT-03) | Needs a real mouse on the notification area, focus rules and audible output | §9 NC-01, steps 1–7 | The smoke shows 34/34 (36/36 on a build with the station catalog), then by hand: tray clicks and every menu item work, the tooltip shows, audio switches one stream at a time, close and minimize hide the window with no flash, dialogs are owned and Enter/Escape work, the recovery and lock-failure dialogs appear (exit 1); in the Add-station dialog Narrator reads "Catalog status", "Station details" and the polite live regions, every control shows a focus outline, nothing is clipped in Segoe UI, and real logos (dark ones too) show on the light tile with no monogram behind them; with the WPF app running, "An older DialShift is still running…" appears, the exit code is 1 and only the older app plays; no clipped text. The log ends `app.exit … code=0 clean=true` | BHV-03, 04, 09, 12, 13, 14, 15, 19, 20, 21, 23, 24, 27, 29, 50, 51, 55, 63, 64, 65; MX-04; DOD-04; PK-04; QG-03; the native confirmation of SW-S2; brief 3: CAT-03 (the smoke from the extracted zip, with NC-18) and the Windows native part of CAT-12 and CAT-14 |
| NC-02 | `SystemEvents` delivery on a real sleep, including a zoned slot that starts during sleep (TZ-12) and a run with only the tick gap. Also record the thread `Resumed` arrives on (for SR-02) and the delay from wake to `Resume` (for HZ-04) | Needs a hardware sleep. A hosted runner can't be suspended | §9 NC-02, steps 1–4 | On each wake: `power_events.resumed`, one `wake.detected`, exactly one `wake.recovery` and one reconnect. Paused stays paused. The Athens slot fires once. The tick gap alone recovers. Quit does not hang. Record the thread and the delay | BHV-42; MX-08; DOD-07; SP-03. Also QA-N1 and TZ-12 (with NC-08), SR-02, and HZ-04's recorded delay |
| NC-03 | Run the LibVLC corpus (C1–C15, T1–T15) on speakers with public streams. Check the real-engine retry and fallback timings, the 25 s watchdog, and title behavior on public Icecast `http://` stations | Needs Windows audio and public streams. CI has only the silent output and a local server | §9 NC-03 | The failure kinds match the adapter column, or the difference is explained. No crash. No audio after Stop. The 3/6/30 s retries, the fallback after 3 failures and the 120 s primary re-check match matrix §5.1 | BHV-37 (with NC-11), BHV-39, MX-10 (with NC-15), MX-13 (with NC-11, NC-15, NC-16) |
| NC-04 | Launch at sign-in, a moved copy, the Task Manager Startup-apps switch, and an upgrade with `Install.ps1` keeping launch at sign-in (step 6) | Needs a real Windows sign-in | §9 NC-04, steps 1–6 | Starts in the tray at sign-in. A moved copy shows off with "Launch at sign-in points to an older copy of DialShift…", and turning it on repairs it. Off in Task Manager shows off. `StartupApproved\Run` starts with `03` after step 3 and `02` after step 4 (D39). Turning it off removes both registry values. After `Install.ps1`, the Run value names the installed exe and Settings shows on | BHV-59, MX-07, MX-15, DOD-06 (each with NC-10); the README upgrade note (SW-S5) |
| NC-06 | A second launch brings the hidden window to the foreground while another app has focus | Needs a Windows desktop with real focus rules | §9 NC-06 | The window comes to the foreground, not just a flashing taskbar button. The second process exits 0. The log has `single_instance.activated … delivered` | BHV-08, BHV-15, MX-06, DOD-05 (with NC-13, NC-17, and NC-07 for DOD-05) |
| NC-15 | LibVLC fast-switch stress: 7 runs of 160 UI Automation "Next station" presses with random 0–300 ms gaps | Needs native Windows with a real audio device | §9 NC-15 | No crash or hang. At most one station audible. After runs 2–7 the working set stays within ±20 MB of its value after run 1. The last station plays. No `playback.engine_error` | MX-10 (with NC-03), MX-13 |
| NC-19 | The Windows setup (brief 4) on a real PC: download with a browser, SmartScreen, Smart App Control and Defender recorded; the wizard; the refusal while DialShift runs; an upgrade of a `0.4.0` `Install.ps1` install with launch at sign-in on; Apps & features; reinstall, upgrade, and uninstall with both answers to the keep-settings question | Needs a real Windows PC, a person at the wizard and a real sign-in; CI runs the setup silently only. Also needs a build that carries the setup (brief 4 not implemented yet) | §9 NC-19, steps 1–8 | SmartScreen and the rest recorded; the wizard reads right at 150 %; the Retry/Cancel message; stations, schedule and launch at sign-in (on, no diagnostic) kept; the installed exe starts without a second SmartScreen prompt; Apps & features shows DialShift, its version, publisher `DialShift`, icon and size; Yes keeps and No deletes `%LOCALAPPDATA%\DialShift`; shortcuts and the Run value removed | Matrix §12 INS-07, INS-11, INS-15, INS-16, INS-20 |

### 2.2 Signing credentials and certificates (2 items; release only, not performed)

**Missing resource:** an Authenticode code-signing certificate (Windows), and an Apple Developer Program membership with a Developer ID Application certificate and notarization credentials (macOS). Under D7 these are release requirements that this work does not perform. The scripts sign ad-hoc only (`scripts/build-mac-app.sh`) or not at all (`scripts/build.ps1`). So a real signed release also needs a release-lane change to the scripts, not only the credentials.

**Who can unblock:** the user, by getting the certificates. After that, the release lane adds the signing steps and runs the checks.

| Item | What's left | Why blocked | Procedure | Pass (short) | Rows it unblocks |
|---|---|---|---|---|---|
| NC-05 | (a) Record SmartScreen's behavior for the unsigned development zip, downloaded by a browser, on a clean Windows PC. (b) Sign `DialShift.exe` with Authenticode and an RFC 3161 timestamp, then verify | (a) needs a clean Windows PC but **no** certificate. (b) needs the certificate | §9 NC-05 | (a): record whether "Windows protected your PC" appears and whether "Run anyway" starts the app. (b): `signtool verify /pa /v` succeeds, `Get-AuthenticodeSignature` is `Valid` with the publisher's name, and the downloaded zip launches with the publisher shown | No §3 row (PK-05 is already GREEN). It closes D7's Windows release tier |
| NC-09 | Developer ID signing with the hardened runtime and the entitlements .NET needs (at least `com.apple.security.cs.allow-jit`), then `notarytool` and `stapler` | Needs an Apple Developer ID and notarization credentials | §9 NC-09 | `spctl -a -vv` says "accepted, source=Notarized Developer ID". `xcrun stapler validate` succeeds. A clean Mac opens the downloaded zip with no Gatekeeper warning. `http://` and `https://` still play, and launch at login still works | No §3 row (PK-03 is already GREEN). It closes D7's macOS release tier and changes the README's signing wording (D36) |

### 2.3 Clean Apple Silicon Mac without Rosetta (1 item)

**Missing resource:** an Apple Silicon Mac with no Rosetta (`/usr/bin/pgrep oahd` finds nothing, and `arch -x86_64 /usr/bin/true` fails) and no developer tools. The dev box has Rosetta installed and is the development machine, so it does not count.

**Who can unblock:** the user, with a spare or freshly set-up Apple Silicon Mac.

| Item | What's left | Why blocked | Procedure | Pass (short) | Rows it unblocks |
|---|---|---|---|---|---|
| NC-07 | Partly done. **Done on 2026-09-25**, on the dev box (macOS 26.5.2, build `0.3.0+2c0909e`, the zip from `scripts/build-mac-app.sh`): the zip was given Safari's quarantine attribute and extracted with `ditto`. Quarantine propagated to `DialShift.app`, `codesign --verify --deep --strict` reports it valid, and `spctl --assess --type execute` reports `rejected`, which is expected for an ad-hoc signed app that isn't notarized. So the user must choose **Open Anyway**, as the README says. **Remaining:** (a) a real Safari download and a Finder unzip; (b) the first launch through Open Anyway; (c) a menu-bar icon and no Dock icon; (d) `https://` and `http://` stations audible; (e) a relaunch with no prompt, and a second `open` activates the window; (f) no Rosetta prompt ever; (g) the `unzip`-extracted copy opens after Open Anyway; (h) **App Translocation (SW-S3, D55):** a copy opened from Downloads without moving it runs translocated (`app.translocated` in the log), and turning on launch at login is refused with "Move DialShift to Applications first, then turn this on again.", writing no LaunchAgent | Needs a clean Apple Silicon Mac | §9 NC-07 | Each observation holds. `app.start` shows `rid=osx-arm64 arch=Arm64 engine=MacAvPlayerPlaybackEngine`. No Rosetta prompt. The translocated copy refuses launch at login | MX-12; SP-02 (with NC-08); DOD-04 (with NC-01, NC-17); DOD-05 (with NC-06, NC-17). When it passes, the README drops "not clean-machine tested" (DOD-08, D36) |

### 2.4 Displays genuinely asleep, and a MacBook lid close (2 items)

**Missing resource:** for NC-08 steps 1–4, a MacBook whose lid can be closed. For NX-01, only an unattended window in which the Mac stays awake with every display asleep.

**Who can unblock:** the user, for the lid close. For NX-01, anyone who can leave the Mac alone for about two minutes: no keyboard, no mouse, nothing that wakes the displays.

| Item | What's left | Why blocked | Procedure | Pass (short) | Rows it unblocks |
|---|---|---|---|---|---|
| NC-08 | Lid-close wake while playing, while paused, with a zoned slot that starts while the lid is closed (TZ-12), and with only the tick gap (a dev build with `MacPowerEvents.Start` disabled). Step 5 is the no-display start (NX-01, below) | Needs a physical lid close | §9 NC-08, steps 1–5 | `power_events.resumed` on the main thread on each wake (D30). Exactly one `wake.recovery` per wake. Paused stays paused. The zoned slot plays once. The tick gap alone recovers | BHV-42, MX-08, DOD-07 (each with NC-02); SP-02 (with NC-07); SP-04. Also QA-N1 and TZ-12 (with NC-02), and NX-01 (step 5) |
| NX-01 | **The crash:** NC-17 found it on 2026-09-25. Launched through LaunchServices while the displays were asleep, the app died at startup in Avalonia.Native: `kCVReturnInvalidDisplay`, `CVReturn -6661`. The tray, the schedule and playback never started. **The fix:** `2c0909e` (D52) probes `CVDisplayLink` before Avalonia.Native initializes and falls back to a `SleepLoopRenderTimer` render loop. **Verified so far:** only with the failure forced. The `RenderTimerFallback` suite passes (16 checks), and with the probe forced to -6661 against the real Avalonia.Native the smoke passed 32/32 with normal idle CPU. **Remaining:** one genuine run with the displays really asleep. It has not happened yet, because the user was active at the machine and woke the displays | Needs the displays to stay asleep for the whole run, with nobody touching the Mac | §9 NC-17 step 10 (and NC-08 step 5, which also opens the window after the displays wake) | No crash, and no new `DialShift` report in `~/Library/Logs/DiagnosticReports`. The log has `app.render_timer_fallback` naming `CVReturn -6661`. `results.json` reports every check passed | §7.10 NX-01. It is also NC-17 step 10, so NC-17 closes only when this passes too |

### 2.5 A real macOS log out and log in (2 items)

**Missing resource:** a real login-session cycle on the Mac, plus System Settings → General → Login Items. A shell session cannot trigger a LaunchAgent at login.

**Who can unblock:** the user, on the dev box. Both items run in the same session.

| Item | What's left | Why blocked | Procedure | Pass (short) | Rows it unblocks |
|---|---|---|---|---|---|
| NC-10 | The LaunchAgent at a real login, a moved bundle, Login Items "Allow in the Background", and `launchctl disable`/`enable`. Also (5) an older DialShift still running (DialShift.Mac from `legacy-last-known-good`, or upstream v0.2.0) makes the new app show "An older DialShift is still running…" and exit 1 (SW-S2); (6) upstream v0.2.0's `com.dialshift.radio` entry shows on with "set up by an older DialShift", starts DialShift once at login, and is replaced by ours after off and on (SW-S1) | Needs a real log out and log in | §9 NC-10, steps 1–6 | Starts in the menu bar with no window. A moved bundle shows off with the stale diagnostic, and turning it on repairs it. Record whether the Login Items switch shows up in `launchctl print-disabled`; if it doesn't, amend D39 and the README. `plutil -lint` passes on the plist. No second instance. The older-app dialog and exit 1; one LaunchAgent after the v0.2.0 entry is replaced | BHV-59, MX-07, MX-15, DOD-06 (each with NC-04); the native confirmation of SW-S1 and SW-S2 |
| NC-13 (LaunchAgent half) | **The LaunchServices half PASSED on 2026-09-25** (dev box, build `0.3.0+2c0909e`): the socket was mode `0600` and owned by the user, a second `open` was delivered and acknowledged (`single_instance.activated … delivered`), and SIGTERM gave `app.exit code=0 clean=True` and removed the socket. **Remaining:** the same socket-mode check when the LaunchAgent starts the app at a real login | Needs a real login | §9 NC-13 | `stat -f %Lp "$TMPDIR"/CoreFxPipe_DialShift-*` prints `600`. The log has `single_instance.socket`. A second `open` activates the window | BHV-08, MX-06 (each with NC-06, NC-17) |

### 2.6 Real network faults (1 item)

**Missing resource:** real Wi-Fi that can be switched off in the middle of a stream, and a real captive portal, such as a public hotspot.

**Who can unblock:** the user. The Wi-Fi part runs on the dev box, and the captive portal needs such a network.

| Item | What's left | Why blocked | Procedure | Pass (short) | Rows it unblocks |
|---|---|---|---|---|---|
| NC-11 | AVPlayer with Wi-Fi turned off and on mid-stream, a real captive portal, and fallback alternation with the real engine | Needs network manipulation by hand | §9 NC-11 | Failures are classified as in the corpus (`docs/spikes.md`). The retry and fallback timings match matrix §5.1. No audio after Stop | BHV-37 (with NC-03), MX-11 (with NC-16), MX-13 |

### 2.7 A person looking and clicking (2 items)

**Missing resource:** a person at the Mac, with eyes, a mouse, speakers and VoiceOver. No automation can judge rendering or hear audio.

**Who can unblock:** the user, on the dev box.

| Item | What's left | Why blocked | Procedure | Pass (short) | Rows it unblocks |
|---|---|---|---|---|---|
| NC-12 | The 44×44 template menu-bar icon (D34): sharp at 17 pt in light and dark menu bars, and inverted when highlighted. The menu updates after edits with no crash | A visual check | §9 NC-12 | Each observation holds on a Retina display | BHV-19, PK-04 (each with NC-01). BHV-22 and MX-05 are already GREEN |
| NC-17 (steps 1–9) | **The automated part PASSED on 2026-09-25** through LaunchServices: `open -n -W dist/DialShift.app --args --smoke-test --recovery-test`, **34/34** in 76.5 s with `MacAvPlayerPlaybackEngine`. **Remaining by hand:** (1) Reopen from a Finder double-click (BHV-18); (2) a real click on the menu-bar icon, every item, the tooltip; (3) audible Listen, Skip and Next station, one stream at a time, and Pause silences; (4) close, minimize and hide keep audio playing, and start in tray shows no flash; (5) the Finder reveal; (6) the focus ring, Enter/Escape, VoiceOver; (6b) the Add-station catalog dialog (brief 3, D83, D85): VoiceOver reading "Catalog status", "Station details" and the live regions, focus rings on the pickers, Clear, Save and Cancel, and real remote logos, dark ones included; (7) the recovery dialog, and the lock-failure dialog with exit 1; (8) no clipped text at the default and the largest text size; (9) Quit from the menu. Step 10 is NX-01 ([§2.4](#24-displays-genuinely-asleep-and-a-macbook-lid-close-2-items)) | Needs the bundled app under LaunchServices, real clicks and audio | §9 NC-17 | Each observation holds, and the log records each action, ending `app.exit … code=0 clean=true` | BHV-03, 04, 08, 09, 12, 13, 14, 15, 18, 20, 21, 23, 24, 27, 29, 50, 51, 55, 63, 64, 65; MX-04, MX-06; DOD-04, DOD-05; QG-03 (all except BHV-18 also need NC-01, NC-06, NC-07 or NC-13; see §2.9); brief 3: the macOS native part of CAT-12 and CAT-14 (step 6b) |

### 2.8 A macOS 14 test machine (1 item)

**Missing resource:** an Apple Silicon Mac on macOS 14.x, the minimum version (D22). The dev box and the CI runners run macOS 26.

**Who can unblock:** the user, with a machine or a VM on macOS 14.

| Item | What's left | Why blocked | Procedure | Pass (short) | Rows it unblocks |
|---|---|---|---|---|---|
| NC-16 | The AVPlayer format corpus (C1–C15, T1–T15) from the bundled app on macOS 14 | Needs a macOS 14 machine | §9 NC-16 | MP3, AAC and HLS play. Any other format that fails goes into the README's "Formats" line. If MP3, AAC or HLS fail, raise `LSMinimumSystemVersion` by a new decision | MX-11 (with NC-11), MX-13 (with NC-03, NC-11, NC-15) |

### 2.9 Follow-ups that depend on the above

**Code, comment and doc follow-ups triggered by a check's result:**

| Item | What's left | Waits for | Owner lane | Done when |
|---|---|---|---|---|
| SR-02 | The comment at `DialShift.App/Platform/Windows/WindowsPowerEvents.cs:16–17` says `Resumed` "is raised on the `SystemEvents` thread". D49 expects the UI thread. Correct the comment to match what NC-02 records | NC-02 (the observed thread) | platform | The comment matches NC-02, D49 is confirmed or amended, and §7.10 SR-02 is GREEN |
| HZ-04 | Accepted hazard (§7.11). NC-02 records the delay from wake to `Resume` | NC-02 | platform | If `Resume` routinely arrives more than 10 s after the wake, revisit D15. Otherwise note the measured delay on HZ-04 |
| D39 / README | If the Login Items "Allow in the Background" switch does not show up in `launchctl print-disabled`, record that limit | NC-10 step 3 | platform, release | D39 amended and the README "Launch at sign-in" cell updated, or nothing to do |
| D22 / README "Formats" | Formats that fail on macOS 14 go into the README; a failing MP3, AAC or HLS needs a new decision | NC-16 | playback, release | The README "Formats" line is updated, or a new decision raises `LSMinimumSystemVersion` |
| D36 / README | Drop "not clean-machine tested" from the README package table and from D36's limits | NC-07 | release | The README and D36 are updated in the same change that records NC-07 |
| D7 / README signing tiers | Update "Development builds are unsigned", the Open Anyway paragraph and "Signing tiers" once releases are signed | NC-05 (b), NC-09 | release | The README describes the signed release |

**Matrix rows that flip to GREEN, grouped by the checks they need.** All 44 NATIVE-PENDING rows of §3 are below. A row flips only when every listed check has passed.

| Checks needed | §3 rows | Count |
|---|---|---|
| NC-01 + NC-17 | BHV-03, BHV-04, BHV-09, BHV-12, BHV-13, BHV-14, BHV-20, BHV-21, BHV-23, BHV-24, BHV-27, BHV-29, BHV-50, BHV-51, BHV-55, BHV-63, BHV-64, BHV-65, MX-04, QG-03 | 20 |
| NC-01 + NC-06 + NC-17 | BHV-15 | 1 |
| NC-06 + NC-13 + NC-17 | BHV-08, MX-06 | 2 |
| NC-17 | BHV-18 | 1 |
| NC-01 + NC-12 | BHV-19, PK-04 | 2 |
| NC-01 + NC-07 + NC-17 | DOD-04 | 1 |
| NC-06 + NC-07 + NC-17 | DOD-05 | 1 |
| NC-02 + NC-08 | BHV-42, MX-08, DOD-07 | 3 |
| NC-02 | SP-03 | 1 |
| NC-08 | SP-04 | 1 |
| NC-07 + NC-08 | SP-02 | 1 |
| NC-07 | MX-12 | 1 |
| NC-04 + NC-10 | BHV-59, MX-07, MX-15, DOD-06 | 4 |
| NC-03 | BHV-39 | 1 |
| NC-03 + NC-11 | BHV-37 | 1 |
| NC-03 + NC-15 | MX-10 | 1 |
| NC-11 + NC-16 | MX-11 | 1 |
| NC-03 + NC-11 + NC-15 + NC-16 | MX-13 | 1 |
| **Total** | | **44** |

Outside §3, these rows flip as well:
- **§6 QA-N1** and **§6.2 TZ-12**: need NC-02 + NC-08.
- **§7.10 SR-02**: needs NC-02, then the comment fix.
- **§7.10 NX-01**: needs NC-17 step 10 (NC-08 step 5 repeats the start and also checks that the window renders once the displays wake).
- **§11 CAT-03** (brief 3): `NATIVE-PENDING`; it needs NC-18 and NC-01's first step, the smoke from the zip extracted with Explorer (the Windows CI of [§2.12](#212-a-windows-ci-run-of-brief-3-done) smokes the build output).
- **§12 INS-07, INS-11, INS-15, INS-16 and INS-20** (brief 4): need NC-19 (their CI halves, where they have one, are green; INS-15 also the spec lane's review, INS-16 also the design review).
- **§11 CAT-12 and CAT-14** (brief 3): `NATIVE-PENDING`; their headless checks are green on `windows-latest` ([§2.12](#212-a-windows-ci-run-of-brief-3-done)), and they need NC-01 step (4b) (Narrator, focus outlines, Segoe UI widths, real logos) and NC-17 step (6b) (VoiceOver, focus rings, real logos).

NC-05 and NC-09 flip no §3 row. They are the D7 release gate (see [§6](#6-closure-criteria)).

### 2.10 Release engineering, not blocked on hardware (7 items)

**Missing resource:** none for releasing. The private repository's Actions are still refused for billing (the maintainer's account), but the local backup release path (D58) works without them and made the private `v0.4.0` release. SW-N4, the legacy tag, the public runs, Issues and the `v0.3.0` and `v0.4.0` releases are done.

| Item | What's left | Severity | Owner | Done when |
|---|---|---|---|---|
| SW-N4 | **Done: GREEN in CI [`36163420184`](https://github.com/spyroskotsakis/dialshift-dev/actions/runs/36163420184) at `bc807b5`.** `Install.ps1` robustness, fixed in `58b813a` with the review fixes in `dc032fe` and the hardening in `65771f7` (D56). `scripts/Install.ps1` used to delete `%LOCALAPPDATA%\Programs\DialShift` before copying the new build. It now: copies into `DialShift.new-<id>` beside the install; renames the old install to `DialShift.old-<id>` and the new one in, retrying each rename for about 4 s; deletes the old one (a failed delete is a warning); on a failed copy or swap, restores the old install and exits 1; refuses a source inside or equal to the install folder, or an install folder inside the source, before changing anything; runs one install at a time (named mutex `Local\DialShift.Install`); removes leftovers of earlier runs only under their exact generated names, never through a junction; and shows every message and waits for Enter unless the run is non-interactive. In run `36163420184` the `build.yml` step "Install.ps1 (fresh install, upgrade, locked file, install lock, refusals)" passed on `windows-latest` under Windows PowerShell 5.1: (a) fresh install; (b) upgrade, with both planted leftovers removed; (e) locked file and (g) install lock held, both exit 1 with the install unchanged; (c) and (f) refused with the install unchanged; (d) refused. Remaining native part: NC-04 step 6 | SHOULD-FIX before the full release `v0.3.0`; ships in `v0.3.0-rc.2`; not a native check | release | Done: the step passed with `build.ps1` and `verify-win-package.ps1` in run `36163420184` at `bc807b5`, and matrix §7.10 SW-N4 is GREEN. NC-04 step 6 runs the fixed script on hardware (as a fresh install: the kit moves an existing install aside first) |
| Legacy tag on the public repository | **Done on 2026-09-25.** The maintainer pushed `legacy-last-known-good` (`82281e5`) to the public repository with the first public push; `git ls-remote --tags origin` lists it. The tag that the README ("Upgrading", "Earlier versions") and CHANGELOG name now exists on the public repository | Was: before the public `v0.3.0-rc.1` push | maintainer | Done: `git ls-remote --tags origin` lists `legacy-last-known-good` |
| Public runs of the first public push | **Done: recorded on 2026-09-25.** The maintainer pushed `main` and `refactor/single-codebase-timezone` (both `99a1122`) and the tags `v0.3.0-rc.1` (`9b47afb`), `v0.3.0-rc.2` (`99a1122`) and `legacy-last-known-good` to the public repository in one push, the first that brought `.github/workflows` there. GitHub started only CI for `main`; the branch and the two version tags got no run (the first-push caveat, README "Releasing (maintainers)", D53 update). They were started by hand. The runs: CI [`36168559994`](https://github.com/spyroskotsakis/DialShift/actions/runs/36168559994) (`main`, push) and [`36168665136`](https://github.com/spyroskotsakis/DialShift/actions/runs/36168665136) (the branch, `gh workflow run CI --ref refactor/single-codebase-timezone`), both green on `windows-latest` and `macos-latest`. The first Release dispatch for `v0.3.0-rc.1` used `--ref main` (run [`36168661177`](https://github.com/spyroskotsakis/DialShift/actions/runs/36168661177)): it took the workflows from `main` but built the rc.1 code, failed at the newer `Install.ps1` step's case (b) and skipped publishing (matrix §7.10 RL-01, now refused by `release.yml`). rc.1 was dispatched again on its tag, run [`36169361956`](https://github.com/spyroskotsakis/DialShift/actions/runs/36169361956), which published "DialShift 0.3.0-rc.1"; rc.2 was dispatched from `main` while `main` was the tag's own commit (`99a1122`), run [`36169277177`](https://github.com/spyroskotsakis/DialShift/actions/runs/36169277177), which published "DialShift 0.3.0-rc.2". Both are pre-releases with the three assets | Was: before the public `v0.3.0` push | maintainer (orchestrator records the runs) | Done: CI for `main` and the branch green on both OSes, both pre-releases published with the three assets, and the run IDs recorded here |
| Private dry run of `v0.3.0-rc.2`: blocked by billing | **Done through the backup path on 2026-09-26:** the private `v0.4.0` release was published with `scripts/release-local.sh` (row `v0.4.0` below). The billing itself is not fixed, so Actions still refuse every job on the private repository and its CI and Release workflows do not run; the private `v0.3.0-rc.2` release was never published. Release run `36164430765` on `spyroskotsakis/dialshift-dev` passed `resolve` and both builds, but GitHub did not start its "Publish the GitHub Release" job: "The job was not started because recent account payments have failed or your spending limit needs to be increased." Account billing, not code: Actions minutes are billed on the private repository (macOS and Windows runners at a multiple of the Linux rate) and free on the public one. Since about 17:55 UTC on 2026-09-25 GitHub refuses every job on the private repository, including the `v0.3.0` Release run `36170131029` and CI runs `36170126288` (`main`) and `36169814295` (`refactor/single-codebase-timezone`); `v0.3.0` was therefore published on the public repository without a private dry run. Backup path without Actions (D58, README "Backup: release from a local build"): `scripts/release-local.sh <tag> --repo spyroskotsakis/dialshift-dev --publish` on the maintainer's Mac builds, verifies and publishes the private release locally (creating a release uses no Actions minutes); run it without `--publish` first as a dry run | Blocks the private repository's Actions until fixed; the backup path does not need Actions | maintainer (GitHub account billing, or the local backup release) | Done by the second alternative (the private `v0.4.0` release). Billing or the spending limit is fixed, then `gh run rerun 36164430765 -R spyroskotsakis/dialshift-dev --failed` publishes the private `v0.3.0-rc.2` release; or the private dry run of the next tag is published with `scripts/release-local.sh` |
| Issues on the public repository | **Done on 2026-09-25.** The `[0.3.0]` release notes and the README ask users to report problems at `https://github.com/spyroskotsakis/DialShift/issues`. The public repository, a fork, started with Issues turned off; they are now turned on, so that link works | Was: before the public `v0.3.0` push | maintainer (repository **Settings → General → Features → Issues**) | Done: `gh repo view spyroskotsakis/DialShift --json hasIssuesEnabled` gives `true` |
| Release `v0.3.0` (D57) | **Done on 2026-09-25.** A full release on the `v0.3.0-rc.2` app code, with the testing status in its notes. The maintainer pushed the tag `v0.3.0` (`b9cbeea`) to the public repository; its Release run [`36172793355`](https://github.com/spyroskotsakis/DialShift/actions/runs/36172793355) (a tag push) passed `resolve`, including the RL-01 run-on-the-tag check, both builds and publishing. "DialShift 0.3.0" was published at 18:29 UTC as the latest release (not a pre-release) with `DialShift-win-x64.zip` (SHA-256 `3cfb1681…ff8c`), `DialShift-macos-arm64.zip` (`7d2b77ba…0d5a`) and `SHA256SUMS.txt`; both downloads pass `shasum -a 256 -c SHA256SUMS.txt`. The private dry run did not happen: the private repository refused the run (billing row above) | Was: after the rows above | maintainer (tag and push); release (notes) | Done: the public Releases page shows `v0.3.0` as the latest release with the three assets, the checksums match, and its `resolve` job was the first public run of the RL-01 ref check (matrix §7.10 RL-01 GREEN) |
| Release `v0.4.0` (D92) | **Done on 2026-09-26.** A full release on `cceff38`'s app code: the tag `v0.4.0` is on `3c8543c`, whose difference from `cceff38` is documentation only, with the testing status in CHANGELOG `[0.4.0]`, which cites CI run `36219966368`; `main` on both repositories is `3c8543c`, fast-forwarded from `aaa3cb1`. **Public** ([release](https://github.com/spyroskotsakis/DialShift/releases/tag/v0.4.0)): the tag push started Release run [`36221102328`](https://github.com/spyroskotsakis/DialShift/actions/runs/36221102328), which passed "Check the tag, version and changelog", `build / windows-latest`, `build / macos-latest` and "Publish the GitHub Release"; "DialShift 0.4.0" was published at 05:40:46 UTC, not a pre-release, as Latest, with `DialShift-win-x64.zip` (97,931,547 B, SHA-256 `66f92f79…61847cff`), `DialShift-macos-arm64.zip` (45,661,564 B, `33fa81d2…e67f4439`) and `SHA256SUMS.txt`, whose lines equal the digests GitHub reports for the two zips. CI on the same push is green for `main` ([`36221096133`](https://github.com/spyroskotsakis/DialShift/actions/runs/36221096133)) and the branch ([`36221096207`](https://github.com/spyroskotsakis/DialShift/actions/runs/36221096207)). **Private** ([release](https://github.com/spyroskotsakis/dialshift-dev/releases/tag/v0.4.0)): the maintainer ran `scripts/release-local.sh v0.4.0 --repo spyroskotsakis/dialshift-dev --publish` on the Mac (D58): build with warnings as errors, tests `2704 passed, 6 skipped` (26/26 suites), the Windows package cross-built with `build.ps1` and verified, the macOS zip verified after `ditto` and `unzip`, native smoke 36/36, bundle smoke 34/34 (`MacAvPlayerPlaybackEngine`), verifier fixtures 32/32 on both packages; "DialShift 0.4.0" was published at 05:39:23 UTC, not a pre-release, as Latest, with `DialShift-win-x64.zip` (97,929,853 B, `d477f967…6d3b416e`), `DialShift-macos-arm64.zip` (45,661,577 B, `a6dfbfc6…77d8bd17`) and a matching `SHA256SUMS.txt`. Not checked in the private run, because they need Windows: the Windows-only tests, the Windows native smoke and `Install.ps1`; they ran in public CI `36219966368` and in the public Release run. The two repositories' zips differ in bytes: separate builds of the same commit | Was: release, user decision (D92) | maintainer (tag, push, local release); release (notes) | Done: both Releases pages show `v0.4.0` as the latest release with the three assets, the checksums match, and D92's update records both |

### 2.11 A Windows machine with Windows PowerShell 5.1 (1 item, brief 3)

**Missing resource:** any Windows 10/11 x64 machine with Windows PowerShell 5.1 (`powershell.exe`, part of Windows); a VM will do. This Mac has only PowerShell 7, where `System.Text.Json` exists and the verifier's fallback never runs, and CI runs the script under `pwsh`.

**Who can unblock:** the user, or anyone with a Windows machine and a `DialShift-win-x64` zip built at or after `951b8aa`.

| Item | What's left | Why blocked | Procedure | Pass (short) | Rows it unblocks |
|---|---|---|---|---|---|
| NC-18 | Run `scripts/verify-win-package.ps1` under Windows PowerShell 5.1 on the real package and on the contracts §8 CAT-03 fixtures, to exercise its fallback parser (`DataContractJsonSerializer`'s reader plus the trailing-comma, single-value and JSON-number checks, `951b8aa`); repeat under `pwsh` on the same machine (D82 (d)) | The fallback runs only on Windows PowerShell 5.1, which exists only on Windows; no automated run reaches it | §9 NC-18 | The real package verifies with the same station count as under `pwsh`; every fixture gets the same verdict under both (a BOM and strings holding `,]`, `{`, `}` pass; comments, trailing commas, a second value, `NaN`, `01`, a non-literal `schema_version`, non-object stations fail) | CAT-03 (brief 3), together with NC-01's smoke from the extracted zip |


### 2.12 A Windows CI run of brief 3 (done)

**Done on 2026-09-26.** The private repository still refuses Actions for billing, so the maintainer pushed the branch to the public repository, where Actions are free. The first run there failed one check; after D91 the next is green on both OSes. D75's `WINDOWS-PENDING` status is used by no row any more.

| Item | What was left | Result | Procedure | Pass (short) | Rows it unblocked |
|---|---|---|---|---|---|
| CI-B3-WIN | The `windows-latest` job of `build.yml` at the branch: the build with warnings as errors and the `win-x64` publish (the catalog next to `DialShift.exe`), `DialShift.Tests` (including the Windows-only checks, and CAT-05's drive and UNC rules that SKIP on macOS), the native smoke with its "Catalog loads from the app folder" check, `verify-win-package.ps1` on the extracted zip, and the step `Package verifier fixtures (win-x64, CAT-03)` | **Done: green in public CI [`36219966368`](https://github.com/spyroskotsakis/DialShift/actions/runs/36219966368) at `cceff38`.** Public CI `36219267519` (the branch) and `36219355899` (`main`), both at `aaa3cb1`, had failed exactly one `windows-latest` check, "CAT-14 D85 B5 row :selected (state set directly) … separator 5.02/4.22" (the ` · ` glyph antialiased on Windows); D91 (`b2b81a9`, merged at `cceff38`) gates the separator on its brush and keeps every word gated on brush and pixels. In `36219966368` on `windows-latest`: `Build DialShift.slnx` with `0 Warning(s)`; `Test DialShift.Tests` `2627 passed, 25 skipped; 26/26 suites green.` (HS-11 win HKCU Run, HS-17 LV-01..LV-11 on real LibVLC, `CAT-05 (Windows)` and every CAT UI check passed); `Native UI smoke (--smoke-test --recovery-test)` 36/36 on `LibVlcPlaybackEngine` with `Loaded: 8270 stations`; `Package win-x64 zip` and `Verify win-x64 zip` (`station catalog with 8270 stations`, on the `build.ps1` output and on the zip extracted with `Expand-Archive`); the `Install.ps1` step, cases (a) to (g); `Package verifier fixtures (win-x64, CAT-03)`: `All 32 fixtures got the expected verdict.` On `macos-latest`: `2704 passed, 6 skipped; 26/26 suites green.`, the native smoke 36/36, the bundle smoke `34/34 checks passed (MacAvPlayerPlaybackEngine)`, the 32 fixtures | The CI workflow on the branch; the job logs and the `smoke-win-x64` artifact | Every step green; the smoke's `results.json` all passed with the catalog check; 32/32 fixture verdicts | CAT-02, CAT-04, CAT-05, CAT-06, CAT-11 → `GREEN`. CAT-03, CAT-12 and CAT-14 → `NATIVE-PENDING`: CAT-03 still needs NC-18 and NC-01's smoke from the zip extracted with Explorer, because the Windows smoke in CI runs the `win-x64` build output, not the zip (the fixtures' app-only smoke runs a copied macOS bundle only); CAT-12 and CAT-14 need NC-01 step (4b) and NC-17 step (6b) |

The run also settles brief §10's definition of done ("the 24 existing suites plus the new `Catalog` suite pass on both CI OSes") for the "DoD only" rows: 26/26 suites green on both OSes.

### 2.13 Station catalog data: maintainer decisions (brief 3)

**Missing resource:** a person who decides about places and names; no code is missing. D86 and D90 unified the City filter's spellings only where the place was certain, and left the rest for a maintainer, because a guessed alias would be wrong data (D71). Each change goes in the country YAML's `city_aliases` (`data/README.md`, "Adding a new country"), is regenerated without `--refresh`, and changes the country CSVs, so it needs its own decision (D86 item 3, D90 item 4).

| Item | What's left | Severity | Owner | Done when |
|---|---|---|---|---|
| Ambiguous city values (D86, D90) | Left as they are by D90: `Ba` (perhaps Bamberg) and `Saxon` (Saxony or Saxony-Anhalt); country-only values (`Deutschland (Germany)` 24, `Germany` 7, `France` 41, `Europe` 9, …); values naming several places; areas that are not states (`Berlin-Brandenburg` 32, `Franken` 14, `Baden`); values that name no place (`Wdr`, `Nord`, `No`, `Prussia`, `World`, `Privatkunde`, `69`, `Aucun`, `Please Select`, `Outside Us`); `Brittany` against `Bretagne`; `Hauts`, `Ile`, `Occ`, `Paca`; `Bouche De Rhône`; `Estrasburgo`; `Mytilini (Lesbos)` (a curated city); `Fanos`; `Kilkis`; `Xanhi` (→ `Xanthi`, below the detection's length rule); `Kerkyra` against `Corfu`; `The Rest Of Greece` 17; `In Macedonia` 12; D86's close spellings of different places (`Pyrgos`/`Pyrgetos`, `Kalamata`/`Kalambaka`, `Baden`/`Baden-Baden`) and `Korinthia` (region) against `Korinthos` (town). The full list with counts is D86 "Not done / follow-ups" | Nice to have: the City filter lists these values as they are; searching and the other filters are unaffected | maintainer (data) | Each value is aliased to one place in YAML with a decision, or recorded as deliberately left |
| Empty city (D90 (a)) | The pipeline cannot give a station an empty city through an alias: `common.city_aliases` rejects an empty value, so a value that names no place can only be mapped to a place or left. Blanking such values needs a pipeline change and a decision | Limitation, accepted by D90 | maintainer (data), then the data lane | A decision either accepts the limitation for good or asks for an explicit "no city" rule in the pipeline |
| Greek-script `norm()` keys (D90 (b)) | `norm()`'s Greek transliteration does not cover accented Greek letters, so a Greek-script city value gives a mixed-script key (`norm('Βέροια (Greece)')` = `vεroia greece`); an alias for such a value must be written in that mixed form. The Greek-script values (`Αθήνα`, `Θεσσαλονίκη`, `Καστοριά`, …) and `Берлин` are still separate City values | Limitation of the pipeline; affects only how those aliases are written | maintainer (data), then the data lane | Either the transliteration covers the tonos (a pipeline change, which would change keys and so needs a decision), or the Greek-script values are aliased in the mixed form |
| German state names: the tie-break (D90 item 1) | Each state has the spelling with the most exported rows; on a tie the spelling more rows already carried exactly wins (accepted: `Saxony-Anhalt` over `Sachsen-Anhalt`, 43 to 43, decided 43 to 42). After a `--refresh` the majority can move: the aliases then still map to the old choice, and any change of target is a new decision, not a silent re-run | Note for the next refresh | maintainer (data) | Checked at each refresh: the D90 counts in the run's output and the committed JSON |


---

## 3. Suggested order of execution

The fastest wins come first. They run on the dev box, and each one unblocks the most rows per hour. The efforts are estimates of hands-on time, not measurements.

| Order | Item | Where | Estimated effort | Why at this point |
|---|---|---|---|---|
| 1 | **NX-01** (NC-17 step 10) | Dev box, unattended | About 5 min to start, 2 min run, 5 min to read the results. The Mac must not be touched during the run | Confirms the fix for the only crash-severity finding. Needs no person during the run: start it and walk away |
| 2 | **NC-12** (template icon) | Dev box | 10–15 min | Purely visual and quick |
| 3 | **NC-17 steps 1–9** | Dev box | 60–75 min (about 15 min of it for step 6b, the Add-station dialog) | Its half of 26 rows. After it, those rows wait only on Windows (NC-01, NC-06), NC-13's login half or NC-07 |
| 4 | **NC-10 + NC-13 LaunchAgent half** | Dev box, 3–4 log out/in cycles | 45–60 min | One session closes both. After it, the launch-at-login rows wait only on NC-04 |
| 5 | **NC-11** (network faults) | Dev box, plus a captive-portal network | 30–60 min, plus finding a captive portal | Needed for BHV-37, MX-11 and MX-13 |
| 6 | **NC-08** (lid close) | A MacBook | 45–60 min, plus a platform-lane dev build with `MacPowerEvents.Start` disabled for step 4 (about 15 min) | Pairs with NC-02 for the sleep rows, QA-N1 and TZ-12 |
| 7 | **Windows hardware block**, in this order: NC-01, NC-06, NC-04, NC-02, NC-03, NC-15 (and NC-05 (a) if the PC is clean; NC-18 on the same PC, or earlier on any Windows VM) | One Windows 11 x64 PC with speakers | NC-01: 1.25–1.75 h (about 15 min of it for step 4b, the Add-station dialog); NC-06: 15 min; NC-04: 45–60 min (several sign-outs); NC-02: 1.5–2 h, plus a dev build for step 4 and for recording the thread; NC-03: 2–3 h; NC-15: 1–2 h including the UI Automation script; NC-05 (a): 15 min; NC-18: about 30 min with the contracts §8 CAT-03 fixtures; NC-19 (brief 4, once a release carries the setup): 45–60 min including one sign-out | With steps 2–4 done, NC-01 and NC-06 flip 25 rows, and NC-04 flips the 4 launch-at-login rows. NC-02 unblocks SR-02 and HZ-04 |
| 8 | **SR-02** comment fix | Any machine | About 10 min, platform lane | Right after NC-02 |
| 9 | **NC-07** (clean Apple Silicon Mac) | A clean Mac | 30–45 min once the machine exists | The last check for MX-12, SP-02, DOD-04 and DOD-05, and it changes the README wording |
| 10 | **NC-16** (macOS 14 corpus) | A macOS 14 Mac | 1–2 h | May change the README "Formats" line or the minimum version |
| 11 | **Signing: NC-05 (b), NC-09** | Release pipeline | Getting the certificates is outside the team and can take days to weeks. Then about half a day to a day of release-lane script work (Developer ID signing, hardened runtime, entitlements, notarization, Authenticode), plus about 1 h to verify each | Release gate only. No §3 row depends on it |

The [§2.10](#210-release-engineering-not-blocked-on-hardware-7-items) items need no hardware. All are done; the private repository's Actions stay refused for billing until the maintainer fixes it, and the local backup path (D58) releases without them. `v0.3.0` (D57) and `v0.4.0` (D92) are released, so the native checks above run against a released build and are tracked for 0.4.x; run the Windows hardware block and NC-18 from the `v0.4.0` release's `DialShift-win-x64.zip`, so that NC-01's smoke (36/36) and step (4b) cover the station catalog. Any zip built at or after `65771f7` (the `v0.3.0` and `v0.4.0` releases' included) has the fixed `Install.ps1` that NC-04 step 6 tests.

---

## 4. How to record results

For each check that passes (or fails):

1. **Update the NC row** in [matrix §9](acceptance-matrix.md#9-remaining-native-checks): the Status column, or its "Results so far". Record the date, OS build, hardware, artifact run id (or commit), PASS or FAIL, and notes, as the §9 rule asks. Keep `dialshift.log` (and `results.json` where there is one) as evidence.
2. **Flip the affected rows** once all of their checks have passed (see the [§2.9](#29-follow-ups-that-depend-on-the-above) grouping): §3 status cells, §6 QA-N1 and TZ-12, §7.10 SR-02 and NX-01.
3. **Update the counts** in the matrix "Current status" block (106 rows: GREEN / NATIVE-PENDING / TODO), in the §6 status line, and in [matrix §9.1](acceptance-matrix.md#91-native-checks-that-require-the-user).
4. **Update this file**: mark the item done or remove its row. Then apply the §2.9 follow-ups (README, decisions) in the same change.
5. **A failure** becomes a new finding in matrix §7.10 (NX-02, …), with a fix in its owner lane and a decision if needed, just as NX-01 was handled.
6. **Commit on the branch and push only to `private`:**

   ```bash
   git switch refactor/single-codebase-timezone
   git add docs/acceptance-matrix.md docs/open-items.md   # plus README.md / docs/decisions.md if changed
   git commit -m "Docs: record NC-xx result"
   git push private refactor/single-codebase-timezone
   ```

   Never push to `origin` or `upstream`. A hook in `.claude/settings.json` blocks it.

### Common commands (from the matrix, README and AGENTS.md)

**Get the CI artifact.** Checks run from the zip a user downloads. CI artifacts are kept for 7 days, so the artifacts of run `36121345627` expire around 2026-10-02. For a fresh one, run CI again, or push:

```bash
gh workflow run ci.yml -R spyroskotsakis/dialshift-dev --ref refactor/single-codebase-timezone
gh run download <run-id> -R spyroskotsakis/dialshift-dev -n DialShift-win-x64
gh run download <run-id> -R spyroskotsakis/dialshift-dev -n DialShift-osx-arm64-native-avplayer
```

**Build and verify locally.**

```bash
# macOS (.NET 10 SDK at ~/.dotnet on the dev box)
export PATH="$HOME/.dotnet:$PATH"
dotnet build DialShift.slnx -warnaserror
dotnet run --project DialShift.Tests/DialShift.Tests.csproj
./scripts/build-mac-app.sh
./scripts/verify-mac-app.sh dist/DialShift.app
./scripts/verify-mac-app.sh --zip dist/DialShift-osx-arm64-native-avplayer.zip
```

```powershell
# Windows
./scripts/build.ps1                      # tests, publish, verify, zip
./scripts/verify-win-package.ps1 -Path <extracted folder>
```

**Native smoke.**

```bash
# macOS: always the bundled app (App Transport Security applies only inside the bundle)
dist/DialShift.app/Contents/MacOS/DialShift --smoke-test --recovery-test --output <dir>
# Through LaunchServices (NC-17's automated part)
open -n -W dist/DialShift.app --args --smoke-test --recovery-test --output <dir>
# NX-01 / NC-17 step 10: start with the displays asleep, then leave the Mac alone until it exits
pmset displaysleepnow; sleep 15; open -n -W dist/DialShift.app --args --smoke-test --output <dir>
```

```powershell
# Windows (PowerShell does not wait for a GUI exe started with &)
(Start-Process .\DialShift.exe -ArgumentList '--smoke-test','--recovery-test','--output',"$PWD\smoke" -Wait -PassThru).ExitCode
```

The smoke exits 0 when every check passes and 4 when one fails. `results.json` in `<dir>` lists the checks.

**Environment variables** (developer and CI use only):
- `DIALSHIFT_AUDIO_OUTPUT=dummy` (in PowerShell: `$env:DIALSHIFT_AUDIO_OUTPUT = 'dummy'`) plays through LibVLC's silent output on Windows (D35). Use it only on a machine with no audio device. **Do not set it for NC-01, NC-03 or NC-15**, which need audible output. macOS ignores it.
- `DIALSHIFT_DATA_DIR=<absolute path>` isolates settings, the log and the lock, for example `DIALSHIFT_DATA_DIR=/tmp/dialshift-dev dotnet run --project DialShift.App -- --tray`. Leave it unset for the login checks (NC-04, NC-10) and the clean-machine check (NC-07), which test the real data folder.

**Evidence commands used by the checks.**

```bash
# macOS
tail -n 50 ~/Library/Application\ Support/DialShift/dialshift.log
stat -f %Lp "$TMPDIR"/CoreFxPipe_DialShift-*                   # NC-13: must print 600
launchctl print-disabled gui/$UID                             # NC-10
plutil -lint ~/Library/LaunchAgents/com.tsiger.dialshift.plist  # NC-10
xattr -p com.apple.quarantine /Applications/DialShift.app     # NC-07
codesign --verify --deep --strict /Applications/DialShift.app # NC-07
spctl -a -vv /Applications/DialShift.app                      # NC-07 (rejected while ad-hoc), NC-09 (accepted)
/usr/bin/pgrep oahd; arch -x86_64 /usr/bin/true               # NC-07: both must fail on a clean Mac
ls ~/Library/Logs/DiagnosticReports | grep -i DialShift       # NX-01: no new report
```

```powershell
# Windows
Get-Content "$env:LOCALAPPDATA\DialShift\dialshift.log" -Tail 50
reg query HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v DialShift                          # NC-04
reg query HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run /v DialShift # NC-04
(Get-Process DialShift).WorkingSet64                                                               # NC-15
```

### Using the native-check kit

`scripts/native-check/` runs the checks in this file on the real machines and collects the evidence. Its [README](../scripts/native-check/README.md) says what each script automates, what it touches and how the evidence is laid out.

| Where | Command | Checks |
|---|---|---|
| A Mac: the dev box; a MacBook for NC-08; a clean Mac for NC-07; macOS 14 for NC-16 | `bash scripts/native-check/macos.sh --zip <DialShift-osx-arm64-native-avplayer.zip>` | NX-01, NC-12, NC-17 steps 1–9, NC-10 with NC-13's LaunchAgent half, NC-11, NC-08, NC-07, NC-16 |
| A Windows 11 x64 PC with speakers | `powershell -ExecutionPolicy Bypass -File .\scripts\native-check\windows.ps1 -Zip <DialShift-win-x64.zip>` | NC-01, NC-06, NC-04, NC-02, NC-03, NC-15, NC-05 (a; b once a build is signed) |

- **How a script works.** It offers the checks in the [§3](#3-suggested-order-of-execution) order. For each one it prints the procedure and pass criteria from matrix §9 and does what a script can do: launches, log and `results.json` verdicts, `codesign`/`spctl`/`stat`/registry reads, and timings. It asks the tester for the physical steps and a PASS/FAIL/SKIP verdict with a note.
- **Re-running.** It is safe to run a script again. NC-10 and NC-04 continue after each log-out.
- **Real data.** The checks that need the real data folder ask first, move the originals aside and restore them afterwards: NC-17, NC-10 and NC-07 on macOS; NC-01, NC-04, NC-05 and NC-06 on Windows. The other checks use an isolated `DIALSHIFT_DATA_DIR`.
- **The evidence.** The tester sends back `native-evidence-<host>-<yyyymmdd>.zip`. Its `summary.json` has one record per check with these fields: `checkId`, `result`, `timestampUtc`, `machine`, `build` (including the CI run id), `notes`, `artifacts` and `steps`. The schema is in the kit README.
- **Recording.** Recording then follows steps 1–6 above. Each record gives the date, OS build, hardware, artifact run id and result that §9 asks for. The `dialshift.log` excerpts and `results.json` are in the check's folder in the zip.
- **Not in the kit:**
  - NC-09, and NC-05 (b) until a build is signed ([§2.2](#22-signing-credentials-and-certificates-2-items-release-only-not-performed)).
  - NC-19 (brief 4): not in the kit yet; the setup is implemented, so the test lane adds it (brief 4 §15).
  - The dev builds with the power-event source disabled, for NC-02 step 4 and NC-08 step 4. The kit asks for their path.
  - The thread `Resumed` arrives on (SR-02, D30). The kit only records what the tester observed from such a build or a debugger.

---

## 5. Known accepted limitations (not blockers)

These are deliberate and documented. They are listed here so that nobody mistakes them for open work.

| Limitation | Where it is decided or documented |
|---|---|
| **No track titles on macOS.** AVPlayer's metadata was unreliable in the harness, so macOS always shows the station description. On Windows, titles arrive only for `http://` streams whose server answers `ICY 200 OK` | D26; README "Differences between the two players" |
| **LibVLC credential reuse on the same path.** Windows keeps a station URL's user and password until DialShift quits, and may send them to the same server and path for another station. They never go to another server or path | D42; README "Stream passwords"; HS-17 LV-08 |
| **The Windows time-zone picker lists fewer zones than macOS.** 332 IANA zones on Windows (from 141 system zones), against 420 on macOS (CI `36121345627`, the `QA-B4` checks; the dev box also shows 420). Windows zone data maps to fewer IANA ids. A stored id that isn't listed is kept as its own entry and is never rewritten | D43; TZ-14; QA-B4 |
| **No Apia 2011 in Windows zone data.** Windows lacks Pacific/Apia's 2011 date-line change, so TZ-15's Apia case reports SKIP on `windows-latest`. That is a data limit, not a code path | Matrix §6.2 TZ-15, §6.1 QA-N7 |
| **The fallback render timer lasts until the next start.** After a start with no active display, the app keeps the `SleepLoopRenderTimer` loop until it is restarted. It renders normally once a display wakes and uses no CPU when idle | D52; README "Reading the log" (`app.render_timer_fallback`) |
| **The Avalonia private-API upgrade rule.** The no-display fallback uses Avalonia's private APIs (`AvaloniaAccessUnstablePrivateApis`), which is safe only while Avalonia is pinned to exactly 12.1.2. Any Avalonia version change must re-verify the fallback (four steps) before it merges | D52; PK-06; OQ-9 |
| **Ad-hoc signing only.** The macOS bundle is ad-hoc signed and not notarized, so Gatekeeper asks for Open Anyway. The Windows build is unsigned, so SmartScreen may warn. Public signing is NC-05 (b) and NC-09 | D7, D36; README "Signing tiers" |

---

## 6. Closure criteria

**Release versus sign-off (D57).** By the user's decision D57, `v0.3.0` shipped as a full release **before** these criteria hold (published 2026-09-25, public Release run `36172793355`), with its testing status in the release notes and the README. That overrides, for 0.3.0 only, the rule that a full release waits for the native checks and signing; D92 does the same for 0.4.0 (published 2026-09-26, public Release run `36221102328`, and on the private repository by `release-local.sh`). Neither changes the criteria below: they still decide when the phase is signed off, and every open item stays tracked for 0.4.x.

**The phase is signed off when both of these hold:**
1. Every item in this file is PASS and recorded in [matrix §9](acceptance-matrix.md#9-remaining-native-checks), its [§2.9](#29-follow-ups-that-depend-on-the-above) follow-ups are done, and the [§2.10](#210-release-engineering-not-blocked-on-hardware-7-items) items are done.
2. The matrix reads **106 GREEN / 0 NATIVE-PENDING / 0 TODO** in §3, with QA-N1, TZ-12, SR-02, NX-01 and SW-N4 GREEN, and, for brief 3, §11 reads **18 GREEN** (NC-18, NC-01's smoke from the extracted zip and its step (4b), and NC-17 step (6b); the CI run of [§2.12](#212-a-windows-ci-run-of-brief-3-done) is done). The data decisions of [§2.13](#213-station-catalog-data-maintainer-decisions-brief-3) do not gate sign-off. Brief 4 (the Windows setup) is not part of this phase: it has its own rows, matrix §12 (INS-01..INS-20, with NC-19), and is done when they read 20 `GREEN`.

NC-14 stays N/A (D13).

NC-05 (b) and NC-09 affect no §3 row, so the matrix can reach 106 GREEN without them. Under D7 they gate a **signed public release**, not the matrix; `v0.3.0` shipped unsigned by D57. If the phase is to be signed off before the certificates exist, record that as a decision in `docs/decisions.md` and keep NC-05 (b) and NC-09 open here as release items.

**When to delete this file:** once every item is done, delete `docs/open-items.md` in the same change that flips the last row. In that same change, remove the links to it from [README.md](../README.md) and from the matrix "Current status" block.
