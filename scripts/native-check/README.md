# Native-check kit

Two guided scripts for the native checks that CI can't run ([docs/open-items.md](../../docs/open-items.md), [acceptance matrix §9](../../docs/acceptance-matrix.md#9-remaining-native-checks)). You run them on a real Mac or a real Windows PC. Each script:

1. prints the procedure and pass criteria for a check;
2. does everything a script can do;
3. asks you to do the physical steps and give a verdict;
4. writes one evidence bundle that you zip and send back.

| Script | Runs on | Checks |
|---|---|---|
| `macos.sh` | macOS 14 or later, Apple Silicon (bash 3.2, built-in tools only: no developer tools, no Python) | NX-01 (NC-17 step 10), NC-12, NC-17 steps 1–9, NC-10 with the LaunchAgent half of NC-13, NC-11, NC-08, NC-07, NC-16 |
| `windows.ps1` | Windows 10/11 x64, Windows PowerShell 5.1 or PowerShell 7 | NC-01, NC-06, NC-04, NC-02, NC-03, NC-15, NC-05 (a; and b once a build is signed) |

Not covered: NC-09 (Developer ID signing and notarization) needs credentials and release-lane script work first. NC-05 (b) is checked only when the build is signed. NC-14 is N/A (D13).

Each script is a single self-contained file. For a clean machine (NC-07, NC-05), copying that one file is enough.

## Running the scripts

Get the CI artifact first (docs/open-items.md §4, "Get the CI artifact"). Matrix §9 asks for the zip a user downloads, not a local build.

**macOS**

```bash
bash scripts/native-check/macos.sh --zip ~/Downloads/DialShift-osx-arm64-native-avplayer.zip
bash scripts/native-check/macos.sh --app dist/DialShift.app     # an already extracted bundle
bash scripts/native-check/macos.sh --run <CI run id>            # downloads the artifact with gh (dev box)
bash scripts/native-check/macos.sh                              # asks, or resumes the last bundle
```

`--zip` extracts the zip twice, with `ditto` (as Finder and Safari do) and with `unzip`, and checks both copies with `codesign --verify --deep --strict`. It then tests the `ditto` copy.

**Windows**

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\native-check\windows.ps1 -Zip "$env:USERPROFILE\Downloads\DialShift-win-x64.zip"
powershell -ExecutionPolicy Bypass -File .\scripts\native-check\windows.ps1 -AppFolder C:\Temp\DialShift-win-x64
```

`-Zip` asks you to extract the zip with Explorer, which keeps the Mark of the Web as a user's copy has it, or lets the script extract it with `Expand-Archive`. Use `-Evidence <folder>` with either script to choose the bundle folder.

A menu lists the checks in the order from docs/open-items.md §3, each with its status. You can run one check, run every pending check (`a`), restore moved-aside files (`r`), finish and zip (`f`), or quit (`q`).

**Resuming.** Running a script again resumes the same bundle and skips checks that are already recorded (you can re-run one on purpose). The login checks survive the logouts:
- **NC-10 (macOS):** three log-out and log-in cycles, five with step 6.
- **NC-04 (Windows):** three sign-out and sign-in cycles, four with step 6.

Before each logout the script prints the command to run afterwards, and then it continues where it stopped.

**Older apps.** Some steps need an older DialShift that only you may have. The script asks for its path, remembers it, and records the step as SKIP, with the reason, if you don't have it:
- NC-01 step 5 (Windows): the WPF app, upstream v0.2.0 from [tsiger/DialShift's releases](https://github.com/tsiger/DialShift/releases) or a build of tag `legacy-last-known-good`.
- NC-10 step 5 (macOS): DialShift.Mac (a build of tag `legacy-last-known-good`; it needs Rosetta) or upstream v0.2.0's app.
- NC-10 step 6 (macOS): upstream v0.2.0's app.

## Automated and manual parts of each check

| Check | What the script does | What you do |
|---|---|---|
| NX-01 | Keeps the Mac awake with `caffeinate -i`. Runs `pmset displaysleepnow; sleep 15; open -n -W <app> --args --smoke-test --output <dir>`. Compares `~/Library/Logs/DiagnosticReports` before and after. Checks that the log names `app.render_timer_fallback … CVReturn -6661` and that `results.json` reports every check passed. Saves the `pmset -g log` excerpt | Don't touch the Mac for about 2 minutes, then wake the displays |
| NC-12 | Starts the app with an isolated data folder and `--tray`. Records the display info and your Light or Dark appearance at each step. Checks for crash reports, error lines and a clean exit | Look at the icon in Light, in Dark and highlighted. Edit stations and slots |
| NC-17 | Moves the real data aside. Starts and restarts the app, reveals it in Finder, runs a second `open -n` and counts the activations. Reads the exit code of a direct second launch (must be 0). Writes the broken `settings.json` and checks for the `unreadable-*` copy. Creates the lock folder and reads the exit code (must be 1). Checks the clean exit and crash reports | Finder double-click, menu clicks, tooltip, listening, hide and minimize, the Finder reveal, keyboard and VoiceOver, the text sizes, Quit |
| NC-10 + NC-13 | Moves your install, data and LaunchAgent aside and installs the build under test in `/Applications`. Runs `plutil -lint` and checks the plist target after each step. After each login, checks the process start time and `--tray`. For NC-13: `stat` of the socket (`600`, owned by you), the `single_instance.socket` line, a second `open`. Moves the bundle for step 2. Reads `launchctl print-disabled` and runs `launchctl disable` for step 4. Step 5: starts the older app, records its single-instance marker (DialShift.Mac's pipe socket or v0.2.0's `running.lock`), runs the new executable and checks exit code 1, `app.legacy_instance_running` and that only the older app is left, then checks that the new app starts normally once the older one quits. Step 6: copies v0.2.0 to `/Applications` without its quarantine flag, checks the `com.dialshift.radio.plist` it writes (`plutil -lint`, target), puts the build under test back over it, and reads the `startup_registration.result` check line; after each of the two logins it checks the start, which plists exist, and that only `com.tsiger.dialshift.plist` is left after the off-and-on. Lists any `startup_registration.error` lines. Restores everything | Three log-outs (five with step 6), the settings checkbox, the Login Items switch, watching for windows. Step 5: the older app's path, playing a station in it, the dialog. Step 6: v0.2.0's path, its launch-at-login setting, the diagnostic under the checkbox, the off-and-on |
| NC-11 | Isolated data folder. Can switch Wi-Fi off and on (`networksetup`). Records the log timeline and the failure kinds, and checks the recovery. A seeded "refuses connections, Groove Salad fallback" scenario checks the §5.1 timings in the log: 3/6/30 s, the fallback after 3 failures, the re-check at about 120 s, alternation | Press Listen, listen, join a captive-portal network (if you have one) |
| NC-08 | Isolated data folder. Checks `power_events.started`. After each wake, checks the log (`power_events.resumed`, one accepted `wake.detected`, one `wake.recovery`, Playing again, or no playback when paused). Computes a slot time in another zone (TZ-12) and counts `schedule.fired`. Runs a dev build for the tick-gap step if you give one. Does the display-sleep start (step 5). Saves the `pmset` sleep and wake log | Close the lid, set up the slot, give the dev build, wake the displays, judge the window |
| NC-07 | Checks that the Mac is clean: `pgrep oahd` and `arch -x86_64 /usr/bin/true` both fail, and there are no developer tools. Checks the quarantine flag on the zip and the app, and runs `codesign -dv`, `codesign --verify --deep --strict` and `spctl -a -vv`. Checks the `app.start` line and runs a second `open`. Makes the `unzip` copy and gives it the download's quarantine flag. For the copy opened from Downloads: checks its quarantine flag, that the process runs from under `/AppTranslocation/`, the `app.translocated` line, the `startup_registration.result` line (`requested=True enabled=False diagnostic=True`), and that `com.tsiger.dialshift.plist` is unchanged; then moves the copy into `test-leftovers/` | Safari download, Finder unzip, drag to Applications, Open Anyway, menu-bar and Dock, listening, relaunch, watching for a Rosetta prompt. A second Finder unzip opened in place, and turning on launch at login there |
| NC-16 | Checks the precondition (macOS 14.x). Seeds the corpus stations C1–C15, plus T1–T9 if you choose. Reads each station's outcome and time from the log into `corpus.tsv` | Press Listen on each station and say whether you hear it |
| NC-01 | Runs the smoke with `Start-Process -Wait -PassThru` and reads `results.json`. Moves the real data aside and starts the app normally and with `--tray`. Writes the broken `settings.json` and checks the copy. Creates the lock folder and reads exit code 1. Step 5's older app: starts it, checks that it holds the mutex `Local\DialShift.App`, reads the new app's exit code 1 and `app.legacy_instance_running`, checks that only the older app is left, then that the new app starts normally once the older one quits. Checks the clean exit | Tray clicks, listening, window behavior, dialogs, focus ring, visual pass. The older app's path, playing a station in it, the dialog |
| NC-06 | Moves the real data aside. Opens Notepad to hold the focus and opens Explorer at `DialShift.exe`. Starts one more second launch to read its exit code (must be 0). Counts `single_instance.activated … delivered` and checks that one process remains | Launch from Start (needs the entry Install.ps1 creates) and from Explorer. Judge whether the window came to the front |
| NC-04 | Saves your data and both registry values (`Run`, `StartupApproved\Run`). After each step, checks that `Run\DialShift` is `"<exe>" --tray` and reads the first `StartupApproved` byte (`03`, then `02`). After each sign-in, checks the process start time and command line. Moves the folder for step 2 and checks that both values are gone after step 5. Step 6: moves an existing `%LOCALAPPDATA%\Programs\DialShift` and its Start menu shortcut aside, runs the folder's `Install.ps1 -NoLaunch` and reads its exit code, checks that `Run\DialShift` now names the installed `DialShift.exe`, starts the installed copy and reads its `startup_registration.result` check line, and after the next sign-in checks that the installed copy started. Lists any `startup_registration.error` lines. Restores everything | Three sign-outs (four with step 6), the settings checkbox, Task Manager Disable and refresh |
| NC-02 | Isolated data folder. Sleeps the PC if you like (`rundll32 powrprof.dll,SetSuspendState 0,1,0` with a scheduled wake task), otherwise you sleep it. After each wake, checks the log. Measures the wake-to-Resume delay against the System log's Power-Troubleshooter event (HZ-04). Suggests a slot time in another zone (TZ-12). Runs a dev build for step 4 if you give one. Checks that Quit finishes within 15 s | Sleep and wake the PC, set up the slot, give the dev build, record the `Resumed` thread (SR-02) |
| NC-03 | Isolated data folder with the corpus stations and T14 against a local server that never answers. Reads each station's outcome and time into `corpus.tsv`. Runs the seeded retry and fallback scenario with its timing checks | Press Listen, listen, check titles and volume 0, Stop. Optionally, the hosts-file variant |
| NC-15 | Isolated data folder with 13 public stations. Drives **Next station** through UI Automation (`InvokePattern`): 7 runs of 160 presses with random 0–300 ms gaps, and Pause right after the last press of run 4. After each run, checks that the process is alive and responding, the last state, and the working set (±20 MB of run 1, in `stress.tsv`). Checks for `playback.engine_error`. If UI Automation can't find the button, you click and the script still checks each run | Listen: at most one station at a time, and the last station plays |
| NC-05 | Reads `ZoneId` from `Zone.Identifier` on the zip and on `DialShift.exe`. Reads the Authenticode status. For (b), when the build is signed, checks the publisher and timestamp and runs `signtool verify /pa /v` if it is installed | Browser download, Explorer extraction, double-click. Report what SmartScreen showed |

## What the scripts touch, and what they don't

**Work folder:** `~/DialShift-native-check` (Windows: `%USERPROFILE%\DialShift-native-check`). It holds extracted apps, isolated data folders, backups (`backup/`) and what tests leave behind (`test-leftovers/`). Nothing there goes into the bundle. The scripts never delete your files: they move test leftovers aside instead.

**Isolated checks** (NX-01, NC-12, NC-11, NC-08, NC-16; Windows NC-02, NC-03, NC-15) run DialShift with `DIALSHIFT_DATA_DIR` pointing into the work folder. The smoke uses its own temporary folder. Your settings are never read or written.

**Real-location checks** need the real data folder, because Finder, Start menu, login and LaunchServices launches can't use an isolated one. The script asks first and restores the originals when the check ends (or with menu option `r`). A backup that is still pending is offered for restore at the next start.

- **macOS: NC-17, NC-10, NC-07.** The script moves `~/Library/Application Support/DialShift` aside. It copies `~/Library/LaunchAgents/com.tsiger.dialshift.plist` and records its `launchctl` override. It also copies an older DialShift's `com.dialshift.radio.plist` if there is one, because turning launch at login on or off deletes that file; one that NC-10 step 6 leaves behind is moved into `test-leftovers/`. NC-10 also moves `/Applications/DialShift.app` aside and installs the build under test there; step 6 puts a copy of v0.2.0 there for a moment (your own v0.2.0 is only read). NC-07 moves it aside and you install the downloaded copy; its translocated copy in Downloads is moved into `test-leftovers/` if you agree.
- **Windows: NC-01, NC-04, NC-05, NC-06.** The script moves `%LOCALAPPDATA%\DialShift` aside and saves `HKCU\…\Run\DialShift` and `HKCU\…\Explorer\StartupApproved\Run\DialShift`. For NC-04 it moves the extracted folder to `<folder>-moved` and back. NC-04 step 6 runs `Install.ps1`, after asking: the script first moves an existing `%LOCALAPPDATA%\Programs\DialShift` aside and copies the Start menu shortcut `DialShift.lnk`, and restoring puts both back (what `Install.ps1` installed goes into `test-leftovers/`).
- **Older apps** (NC-01 step 5, NC-10 steps 5–6) run against the same moved-aside data folder, so they never see or rewrite your settings.

**Temporary system changes** happen only during a check and only as described:
- The displays are put to sleep (`pmset displaysleepnow`).
- `caffeinate -i` keeps the Mac awake.
- Wi-Fi is switched off and on, only if you agree. It is turned back on even if the script is interrupted.
- `launchctl disable` (NC-10 step 4). Restoring puts back the original state.
- A scheduled task `DialShift native-check wake` is created for an automatic sleep and removed afterwards.
- A loopback-only TCP listener runs for T14 and is stopped afterwards.
- Notepad windows are opened.

**Never:** administrator rights or `sudo`, changes to power, display or appearance settings (you change the appearance yourself), `Install.ps1` outside NC-04 step 6, the hosts file (optional and done by you), other apps' files. The scripts send nothing anywhere. The only network traffic is the streams DialShift plays, and `gh run download` if you use `--run`. DialShift variables (`DIALSHIFT_DATA_DIR`, `DIALSHIFT_AUDIO_OUTPUT`) set in your shell are ignored for the run. On Windows they are put back afterwards.

## Privacy

- **Log excerpts** come only from the test data folders. DialShift redacts its log itself (D25, F4):
  - URLs are reduced to `scheme://host[:port]/…`;
  - user info and secret-named values are removed;
  - the home folder becomes `~`.
- **Command outputs** the scripts save also have the home folder replaced by `~`. For `Zone.Identifier`, only the `ZoneId` is recorded, not the download URL. Display serial numbers are filtered out of `system_profiler`.
- **Machine details** in the bundle: the host name (also in the bundle's name), the OS version and build, architecture, model, CPU, whether Rosetta and the developer tools are installed, and on Windows the audio device names.
- **Test data folders** (`settings.json` and so on) are not included.
- **Screenshots are optional.** The script asks once whether to offer them, then asks before each one.
  - On macOS you pick the window or area. The one full-screen capture (the open menu) warns you first.
  - On Windows a screenshot is the whole primary screen, after 5 seconds.
- Notes are free text. Look through the folder before you zip it if you like, and delete anything.

## What to send back

Choose **f** in the menu. It writes `summary.md` and `summary.json` and zips the bundle to `native-evidence-<host>-<yyyymmdd>.zip`, next to the folder on the Desktop. Send that zip. You can zip again after recording more checks.

```text
native-evidence-<host>-<yyyymmdd>/
  summary.md          one table: check, result, timestamp, machine, notes, artifacts; then every step
  summary.json        the same, machine-readable (schema below)
  machine.txt         OS version and build, architecture, model, Rosetta and developer tools (macOS), sleep states (Windows)
  records/<ID>.json   one record per check (summary.json concatenates them), plus the Markdown fragments
  state/              the script's resume state: the build under test, its source, the CI run id
  <ID>/               the check's artifacts: steps.tsv, <name>.jsonl log excerpts, <name>-timeline.txt,
                      smoke/ (results.json, dialshift.log, the smoke's screenshots), corpus.tsv, stress.tsv,
                      command outputs (*.txt), optional screenshots (*.png)
```

## Evidence format (`summary.json`, schema version 1)

```jsonc
{
  "schemaVersion": 1,
  "kit": "macos.sh",                 // or "windows.ps1"
  "kitVersion": 1,
  "platform": "macOS",               // or "Windows"
  "host": "Spyross-MacBook-Pro",
  "generatedUtc": "2026-09-26T09:12:44Z",
  "checks": [                        // in docs/open-items.md §3 order; only recorded checks
    {
      "checkId": "NC-12",            // NX-01, NC-01..NC-17 (NX-01 is NC-17 step 10; NC-13 is its LaunchAgent half)
      "title": "Menu-bar template icon",
      "result": "PASS",              // PASS | FAIL | PARTIAL | SKIP
      "timestampUtc": "2026-09-26T09:10:02Z",
      "machine": {                   // macOS: os, osVersion, osBuild, arch, model, cpu, rosettaInstalled, developerTools, host
                                     // Windows: os, osVersion, osBuild, displayVersion, arch, model, audioDevices, powershell, host
        "os": "macOS", "osVersion": "26.5.2", "osBuild": "25F71", "arch": "arm64", "model": "Mac15,6",
        "cpu": "Apple M3 Pro", "rosettaInstalled": "yes", "developerTools": "yes", "host": "Spyross-MacBook-Pro"
      },
      "build": {                     // null when the check needs no build
        "app": "~/DialShift-native-check/apps/DialShift-osx-arm64-native-avplayer-1a2b3c4d/ditto/DialShift.app",
        "version": "0.3.0+2c0909e", "source": "zip DialShift-osx-arm64-native-avplayer.zip", "ciRunId": "36121345627"
      },
      "notes": "Overall note typed by the tester",
      "artifacts": ["NC-12/displays.txt", "NC-12/log.jsonl", "NC-12/log-timeline.txt"],
      "steps": [
        { "step": "1. Appearance is Light", "kind": "auto", "result": "PASS", "note": "Light" },
        { "step": "1. Light menu bar: the icon is sharp at 17 pt (no blur, no jagged edges)", "kind": "manual", "result": "PASS", "note": "" }
      ]
    }
  ]
}
```

- **`result`** is suggested from the steps. Any `FAIL` makes it `FAIL`. No `PASS` makes it `SKIP`. `PASS` together with `SKIP` makes it `PARTIAL`. Otherwise it is `PASS`. The tester may override the suggestion and should say why in `notes`. A check run on a machine that doesn't meet its precondition (for example NC-16 on macOS 26, or NC-07 on a Mac with Rosetta) is `SKIP`, and its `notes` start with `Precondition not met:`.
- **`steps[].kind`**: `auto` means the script measured it; `manual` means the tester judged it.
- **`steps[].result`**: `PASS`, `FAIL`, `SKIP` or `INFO`. `INFO` is a recorded observation that doesn't count toward the result, for example the wake-to-Resume delay (HZ-04), the `Resumed` thread (SR-02), the `launchctl` view of the Login Items switch (NC-10 step 3), the SmartScreen prompt (NC-05 a), or a failure of an optional format on macOS 14.
- **`artifacts`** are paths relative to the bundle root. A trailing `/` means a folder.
- Every string is a single line: tabs and line breaks become spaces. Timestamps are UTC ISO 8601 with `Z`.
- Recording a result in the matrix follows docs/open-items.md §4. The record's date, machine, build and CI run id are the fields §9 asks for.

## Checking the scripts themselves

The interactive flows need the real machine. Static checks:

```bash
bash -n scripts/native-check/macos.sh
shellcheck scripts/native-check/macos.sh
pwsh -NoProfile -Command '$errors = $null; [void][System.Management.Automation.Language.Parser]::ParseFile("scripts/native-check/windows.ps1", [ref]$null, [ref]$errors); $errors'
```

`windows.ps1` must stay ASCII-only. Windows PowerShell 5.1 reads a script without a byte-order mark in the ANSI code page, where some UTF-8 characters turn into quote characters. The script builds the `·` of DialShift's texts at run time for this reason.
