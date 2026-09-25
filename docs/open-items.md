# Open items: native verification and release sign-off

> **As of 2026-09-25**, branch `refactor/single-codebase-timezone` at `a54e423`.
> This is the hand-off list for the work that is still open after both briefs were implemented. The implementation task is closed. Only the items below remain.
>
> Related documents:
> - [README.md](../README.md): the project and how to build, test and run it.
> - [Acceptance matrix §9](acceptance-matrix.md#9-remaining-native-checks): the full procedure and pass criteria for every native check (NC-*).
> - [Matrix §9.1](acceptance-matrix.md#91-native-checks-that-require-the-user): the same checks grouped by what they need.
> - [docs/decisions.md](decisions.md): decisions D1–D52.
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
| Brief 1 (single codebase) and brief 2 (per-slot time zones) | Implemented. WPF and `DialShift.Mac` are retired, and one Avalonia app, `DialShift.App`, ships as `win-x64` and `osx-arm64` | Branch `refactor/single-codebase-timezone` at `a54e423`, pushed to the `private` remote (`spyroskotsakis/dialshift-dev`) |
| CI | Green on both OSes | Run [`36121345627`](https://github.com/spyroskotsakis/dialshift-dev/actions/runs/36121345627) at `a54e423`: **windows-latest 1,634 passed / 17 skipped**, **macos-latest 1,656 passed / 4 skipped**, **23/23 suites** on both |
| Native smoke in CI | `--smoke-test --recovery-test` **34/34 on both OSes**. The **bundle smoke** (the packaged `osx-arm64` app, extracted from the release zip with `unzip`) passes **32/32** with `MacAvPlayerPlaybackEngine` | Same run: `smoke-win-x64` and `smoke-osx-arm64` artifacts, and the "Bundle smoke" step of the macOS job |
| Packaging | Both downloadable zips verified: `win-x64` by `verify-win-package.ps1`, `osx-arm64` by `verify-mac-app.sh --zip` after both `ditto` and `unzip` extraction | Same run |
| Acceptance matrix §3 (106 rows) | **62 GREEN / 44 NATIVE-PENDING / 0 TODO** | [Matrix §3](acceptance-matrix.md#3-acceptance-matrix) |
| Timezone QA tracker | QA-B1..B4 (blocking) all GREEN. QA-N1..N9: 8 GREEN, 1 NATIVE-PENDING (QA-N1). §9.2: 14 of 15 GREEN, TZ-12 NATIVE-PENDING (the unit half is green) | [Matrix §6](acceptance-matrix.md#6-timezone-qa-tracker-brief-2) |
| Review findings (§7.10) | All GREEN except SR-02 and NX-01, both NATIVE-PENDING | [Matrix §7.10](acceptance-matrix.md#710-characterization-and-review-findings-tracked) |
| Decisions | D1–D52 recorded | [docs/decisions.md](decisions.md) |
| Rollback point | Tag `legacy-last-known-good` (commit `82281e5`) keeps the last build of both old front-ends, including the last Intel Mac build | `git show legacy-last-known-good` |

### What keeps the phase from sign-off

**This list is the only thing left.** Every item needs something the development session cannot provide: Windows hardware, a clean or older Mac, signing credentials, a real login, a lid close, real network faults, or a person looking at the screen. No product code change is known to be needed. The only engineering left is:
- the temporary dev builds for NC-02 and NC-08 step 4, which disable the power-event source;
- the release-lane signing steps, once the credentials exist ([§2.2](#22-signing-credentials-and-certificates-2-items-release-only-not-performed));
- the small follow-ups in [§2.9](#29-follow-ups-that-depend-on-the-above), which depend on what these checks observe.

Native checks: 16 open. NC-01..NC-13 and NC-15..NC-17, where NC-07, NC-13 and NC-17 are partly done. NC-14 (Intel) is not applicable (D13).

---

## 2. Blockers by category

How to read the tables:
- **Procedure**: the row in [matrix §9](acceptance-matrix.md#9-remaining-native-checks) with the full steps.
- **Pass (short)**: a summary. The full pass criteria are in §9.
- **Rows it unblocks**: the matrix rows that name this check. A row flips to GREEN only when **every** check it names has passed. [§2.9](#29-follow-ups-that-depend-on-the-above) groups the rows by the checks they need.
- Every check runs from the **CI artifact**, the zip a user downloads, with `dialshift.log` kept as evidence (§9 rule).

### 2.1 Windows physical hardware (6 items)

**Missing resource:** a physical Windows 11 x64 PC with speakers, a real mouse on the notification area, real sleep and a real sign-in. The hosted `windows-latest` runner cannot sleep, sign in interactively, click the tray or play audio. It uses the silent `adummy` output.

**Who can unblock:** the user, or anyone with such a PC who can download the CI artifact `DialShift-win-x64`.

| Item | What's left | Why blocked | Procedure | Pass (short) | Rows it unblocks |
|---|---|---|---|---|---|
| NC-01 | The Windows tray, window, audio and dialog pass. The smoke part already passes in CI | Needs a real mouse on the notification area, focus rules and audible output | §9 NC-01, steps 1–7 | The smoke shows 34/34, then by hand: tray clicks and every menu item work, the tooltip shows, audio switches one stream at a time, close and minimize hide the window with no flash, dialogs are owned and Enter/Escape work, the recovery and lock-failure dialogs appear (exit 1), no clipped text. The log ends `app.exit … code=0 clean=true` | BHV-03, 04, 09, 12, 13, 14, 15, 19, 20, 21, 23, 24, 27, 29, 50, 51, 55, 63, 64, 65; MX-04; DOD-04; PK-04; QG-03 |
| NC-02 | `SystemEvents` delivery on a real sleep, including a zoned slot that starts during sleep (TZ-12) and a run with only the tick gap. Also record the thread `Resumed` arrives on (for SR-02) and the delay from wake to `Resume` (for HZ-04) | Needs a hardware sleep. A hosted runner can't be suspended | §9 NC-02, steps 1–4 | On each wake: `power_events.resumed`, one `wake.detected`, exactly one `wake.recovery` and one reconnect. Paused stays paused. The Athens slot fires once. The tick gap alone recovers. Quit does not hang. Record the thread and the delay | BHV-42; MX-08; DOD-07; SP-03. Also QA-N1 and TZ-12 (with NC-08), SR-02, and HZ-04's recorded delay |
| NC-03 | Run the LibVLC corpus (C1–C15, T1–T15) on speakers with public streams. Check the real-engine retry and fallback timings, the 25 s watchdog, and title behavior on public Icecast `http://` stations | Needs Windows audio and public streams. CI has only the silent output and a local server | §9 NC-03 | The failure kinds match the adapter column, or the difference is explained. No crash. No audio after Stop. The 3/6/30 s retries, the fallback after 3 failures and the 120 s primary re-check match matrix §5.1 | BHV-37 (with NC-11), BHV-39, MX-10 (with NC-15), MX-13 (with NC-11, NC-15, NC-16) |
| NC-04 | Launch at sign-in, a moved copy, and the Task Manager Startup-apps switch | Needs a real Windows sign-in | §9 NC-04, steps 1–5 | Starts in the tray at sign-in. A moved copy shows off with the stale diagnostic, and turning it on repairs it. Off in Task Manager shows off. `StartupApproved\Run` starts with `03` after step 3 and `02` after step 4 (D39). Turning it off removes both registry values | BHV-59, MX-07, MX-15, DOD-06 (each with NC-10) |
| NC-06 | A second launch brings the hidden window to the foreground while another app has focus | Needs a Windows desktop with real focus rules | §9 NC-06 | The window comes to the foreground, not just a flashing taskbar button. The second process exits 0. The log has `single_instance.activated … delivered` | BHV-08, BHV-15, MX-06, DOD-05 (with NC-13, NC-17, and NC-07 for DOD-05) |
| NC-15 | LibVLC fast-switch stress: 7 runs of 160 UI Automation "Next station" presses with random 0–300 ms gaps | Needs native Windows with a real audio device | §9 NC-15 | No crash or hang. At most one station audible. After runs 2–7 the working set stays within ±20 MB of its value after run 1. The last station plays. No `playback.engine_error` | MX-10 (with NC-03), MX-13 |

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
| NC-07 | Partly done. **Done on 2026-09-25**, on the dev box (macOS 26.5.2, build `0.3.0+2c0909e`, the zip from `scripts/build-mac-app.sh`): the zip was given Safari's quarantine attribute and extracted with `ditto`. Quarantine propagated to `DialShift.app`, `codesign --verify --deep --strict` reports it valid, and `spctl --assess --type execute` reports `rejected`, which is expected for an ad-hoc signed app that isn't notarized. So the user must choose **Open Anyway**, as the README says. **Remaining:** (a) a real Safari download and a Finder unzip; (b) the first launch through Open Anyway; (c) a menu-bar icon and no Dock icon; (d) `https://` and `http://` stations audible; (e) a relaunch with no prompt, and a second `open` activates the window; (f) no Rosetta prompt ever; (g) the `unzip`-extracted copy opens after Open Anyway | Needs a clean Apple Silicon Mac | §9 NC-07 | Each observation holds. `app.start` shows `rid=osx-arm64 arch=Arm64 engine=MacAvPlayerPlaybackEngine`. No Rosetta prompt | MX-12; SP-02 (with NC-08); DOD-04 (with NC-01, NC-17); DOD-05 (with NC-06, NC-17). When it passes, the README drops "not clean-machine tested" (DOD-08, D36) |

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
| NC-10 | The LaunchAgent at a real login, a moved bundle, Login Items "Allow in the Background", and `launchctl disable`/`enable` | Needs a real log out and log in | §9 NC-10, steps 1–4 | Starts in the menu bar with no window. A moved bundle shows off with the stale diagnostic, and turning it on repairs it. Record whether the Login Items switch shows up in `launchctl print-disabled`; if it doesn't, amend D39 and the README. `plutil -lint` passes on the plist. No second instance | BHV-59, MX-07, MX-15, DOD-06 (each with NC-04) |
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
| NC-17 (steps 1–9) | **The automated part PASSED on 2026-09-25** through LaunchServices: `open -n -W dist/DialShift.app --args --smoke-test --recovery-test`, **34/34** in 76.5 s with `MacAvPlayerPlaybackEngine`. **Remaining by hand:** (1) Reopen from a Finder double-click (BHV-18); (2) a real click on the menu-bar icon, every item, the tooltip; (3) audible Listen, Skip and Next station, one stream at a time, and Pause silences; (4) close, minimize and hide keep audio playing, and start in tray shows no flash; (5) the Finder reveal; (6) the focus ring, Enter/Escape, VoiceOver; (7) the recovery dialog, and the lock-failure dialog with exit 1; (8) no clipped text at the default and the largest text size; (9) Quit from the menu. Step 10 is NX-01 ([§2.4](#24-displays-genuinely-asleep-and-a-macbook-lid-close-2-items)) | Needs the bundled app under LaunchServices, real clicks and audio | §9 NC-17 | Each observation holds, and the log records each action, ending `app.exit … code=0 clean=true` | BHV-03, 04, 08, 09, 12, 13, 14, 15, 18, 20, 21, 23, 24, 27, 29, 50, 51, 55, 63, 64, 65; MX-04, MX-06; DOD-04, DOD-05; QG-03 (all except BHV-18 also need NC-01, NC-06, NC-07 or NC-13; see §2.9) |

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

NC-05 and NC-09 flip no §3 row. They are the D7 release gate (see [§6](#6-closure-criteria)).

---

## 3. Suggested order of execution

The fastest wins come first. They run on the dev box, and each one unblocks the most rows per hour. The efforts are estimates of hands-on time, not measurements.

| Order | Item | Where | Estimated effort | Why at this point |
|---|---|---|---|---|
| 1 | **NX-01** (NC-17 step 10) | Dev box, unattended | About 5 min to start, 2 min run, 5 min to read the results. The Mac must not be touched during the run | Confirms the fix for the only crash-severity finding. Needs no person during the run: start it and walk away |
| 2 | **NC-12** (template icon) | Dev box | 10–15 min | Purely visual and quick |
| 3 | **NC-17 steps 1–9** | Dev box | 45–60 min | Its half of 26 rows. After it, those rows wait only on Windows (NC-01, NC-06), NC-13's login half or NC-07 |
| 4 | **NC-10 + NC-13 LaunchAgent half** | Dev box, 3–4 log out/in cycles | 45–60 min | One session closes both. After it, the launch-at-login rows wait only on NC-04 |
| 5 | **NC-11** (network faults) | Dev box, plus a captive-portal network | 30–60 min, plus finding a captive portal | Needed for BHV-37, MX-11 and MX-13 |
| 6 | **NC-08** (lid close) | A MacBook | 45–60 min, plus a platform-lane dev build with `MacPowerEvents.Start` disabled for step 4 (about 15 min) | Pairs with NC-02 for the sleep rows, QA-N1 and TZ-12 |
| 7 | **Windows hardware block**, in this order: NC-01, NC-06, NC-04, NC-02, NC-03, NC-15 (and NC-05 (a) if the PC is clean) | One Windows 11 x64 PC with speakers | NC-01: 1–1.5 h; NC-06: 15 min; NC-04: 45–60 min (several sign-outs); NC-02: 1.5–2 h, plus a dev build for step 4 and for recording the thread; NC-03: 2–3 h; NC-15: 1–2 h including the UI Automation script; NC-05 (a): 15 min | With steps 2–4 done, NC-01 and NC-06 flip 25 rows, and NC-04 flips the 4 launch-at-login rows. NC-02 unblocks SR-02 and HZ-04 |
| 8 | **SR-02** comment fix | Any machine | About 10 min, platform lane | Right after NC-02 |
| 9 | **NC-07** (clean Apple Silicon Mac) | A clean Mac | 30–45 min once the machine exists | The last check for MX-12, SP-02, DOD-04 and DOD-05, and it changes the README wording |
| 10 | **NC-16** (macOS 14 corpus) | A macOS 14 Mac | 1–2 h | May change the README "Formats" line or the minimum version |
| 11 | **Signing: NC-05 (b), NC-09** | Release pipeline | Getting the certificates is outside the team and can take days to weeks. Then about half a day to a day of release-lane script work (Developer ID signing, hardened runtime, entitlements, notarization, Authenticode), plus about 1 h to verify each | Release gate only. No §3 row depends on it |

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

**The phase is signed off when both of these hold:**
1. Every item in this file is PASS and recorded in [matrix §9](acceptance-matrix.md#9-remaining-native-checks), and its [§2.9](#29-follow-ups-that-depend-on-the-above) follow-ups are done.
2. The matrix reads **106 GREEN / 0 NATIVE-PENDING / 0 TODO** in §3, with QA-N1, TZ-12, SR-02 and NX-01 GREEN.

NC-14 stays N/A (D13).

NC-05 (b) and NC-09 affect no §3 row, so the matrix can reach 106 GREEN without them. Under D7 they gate the **public release**, not the matrix. If the phase is to be signed off before the certificates exist, record that as a decision in `docs/decisions.md` and keep NC-05 (b) and NC-09 open here as release items.

**When to delete this file:** once every item is done, delete `docs/open-items.md` in the same change that flips the last row. In that same change, remove the links to it from [README.md](../README.md) and from the matrix "Current status" block.
