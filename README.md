# DialShift

**Your radio, on time.** A native tray radio with a weekly listening schedule — for Windows and macOS.

One Avalonia app, `DialShift.App`, ships as two self-contained packages:

| Package | Platform | Playback | Status |
|---|---|---|---|
| `DialShift-win-x64.zip` | Windows 10/11, x64 | LibVLC (bundled) | Windows build; unsigned |
| `DialShift-macos-arm64.zip` | macOS 14.0+, Apple Silicon only | Apple AVPlayer (part of macOS) | Native `osx-arm64` (AVPlayer, label `native-avplayer`); development build: ad-hoc signed, not yet notarized or clean-machine tested |

There is no Intel Mac build.

## Download

Download DialShift from the **[Releases page](https://github.com/spyroskotsakis/DialShift/releases)**. Every release has three files:

| File | For |
|---|---|
| `DialShift-win-x64.zip` | Windows 10/11, x64 |
| `DialShift-macos-arm64.zip` | macOS 14.0 or later, Apple Silicon |
| `SHA256SUMS.txt` | SHA-256 checksums of both zips |

**Pre-releases and "latest".** The first release, `v0.3.0-rc.1`, is a pre-release: native checks are still open (see [Open items](#open-items--release-status)). GitHub never treats a pre-release as the latest release, so until the first full release, `v0.3.0`, the "latest" links below return "not found"; take the newest release from the [Releases page](https://github.com/spyroskotsakis/DialShift/releases) instead. From the first full release on, these links always point at the newest full release:

- Latest release: <https://github.com/spyroskotsakis/DialShift/releases/latest>
- Windows: <https://github.com/spyroskotsakis/DialShift/releases/latest/download/DialShift-win-x64.zip>
- macOS: <https://github.com/spyroskotsakis/DialShift/releases/latest/download/DialShift-macos-arm64.zip>
- Checksums: <https://github.com/spyroskotsakis/DialShift/releases/latest/download/SHA256SUMS.txt>

A specific release's files are always at `https://github.com/spyroskotsakis/DialShift/releases/download/<tag>/<file>`, for example `…/download/v0.3.0-rc.1/DialShift-win-x64.zip`. What changed in each release is in [CHANGELOG.md](CHANGELOG.md).

### Check the download

Put `SHA256SUMS.txt` next to the zip you downloaded. On macOS, in Terminal, in that folder (prints `OK` for each file you have):

```sh
shasum -a 256 -c SHA256SUMS.txt --ignore-missing
```

On Windows, in PowerShell, in that folder (prints `True` when the file is intact):

```powershell
$expected = ((Select-String -Path SHA256SUMS.txt -Pattern 'DialShift-win-x64.zip' -SimpleMatch).Line -split '\s+')[0]
(Get-FileHash DialShift-win-x64.zip -Algorithm SHA256).Hash -eq $expected
```

### Install on Windows

Extract `DialShift-win-x64.zip` and open `DialShift.exe` (a local build is at `artifacts/DialShift-win-x64/DialShift.exe`). Keep the whole folder together: it includes .NET and VLC. No separate runtime or VLC installation is needed.

Optional install: right-click `Install.ps1` in the extracted folder and choose **Run with PowerShell**. It copies the app to `%LOCALAPPDATA%\Programs\DialShift` and adds a Start menu shortcut, without administrator rights. It does not turn on launch at sign-in unless you already chose that setting.

The build is not code-signed, so SmartScreen may say "Windows protected your PC" on first launch: choose **More info**, then **Run anyway**.

### Install on macOS

Requires macOS 14.0 or later on Apple Silicon (M1 or newer); there is no Intel build. Unzip `DialShift-macos-arm64.zip` with Finder or any unzip tool (a local build is at `dist/DialShift.app`), move `DialShift.app` to `/Applications` and open it. The build is ad-hoc signed, not notarized, so macOS blocks the first launch: open **System Settings → Privacy & Security** and choose **Open Anyway** (on older macOS, right-click the app and choose **Open**).

DialShift runs natively on Apple Silicon with Apple's AVPlayer: no bundled VLC and no Rosetta 2. It is a menu-bar app, so there is no Dock icon.

Move DialShift to `/Applications` before you turn on launch at login. If you open it straight from Downloads, macOS runs it from a temporary copy that disappears when it quits, so DialShift refuses with "Move DialShift to Applications first, then turn this on again."

### Upgrading from an earlier DialShift

This applies if you used the earlier separate Windows or macOS app, including `v0.2.0` from [tsiger/DialShift](https://github.com/tsiger/DialShift).

- **Your settings carry over.** DialShift keeps using the same data folder (see [Data](#data)), so your stations, schedule, fallback and volume are there on first start. Slots that an earlier version saved with a dot in the time (`08.30`, on computers set to Danish, Finnish, Indonesian and some other formats) play again and are saved as `08:30`.
- **Quit the old app first**, from its tray or menu-bar icon. While it runs, the new DialShift shows "An older DialShift is still running. Quit it from its tray icon, then open DialShift again." and exits, so the two never play at once.
- **Windows, launch at sign-in:** `Install.ps1` keeps it. It uses the same `Run` value as before and points it at the new copy. If you run the new `DialShift.exe` from another folder instead, Settings shows "Launch at sign-in points to an older copy of DialShift"; turn it on again to fix it.
- **macOS, launch at login:** an older app's launch-at-login entry is recognized. Settings shows it on, with a note if the entry came from an older DialShift; turn launch at login off and on to replace it with the new one. If Settings says it "points to an older copy", turn it on again. Move DialShift to `/Applications` first (see [Install on macOS](#install-on-macos)).
- **Intel Macs:** there is no Intel build of this version, so keep using your current app. The last Intel build's source is kept at the tag `legacy-last-known-good` (see [Earlier versions](#earlier-versions)).
- **Going back to an older version** keeps your stations and schedule, but the older app ignores slot time zones and removes them the next time it saves.

## Open items / release status

Both packages build, test and pass the native smoke in CI. Before the phase can be signed off and a full release (`v0.3.0`) made, some checks still need things CI can't provide: Windows hardware, a clean Mac, signing credentials, a real login and a person at the screen. [docs/open-items.md](docs/open-items.md) lists every one of them, with why it is blocked and how to run and record it. Until they pass, releases are pre-releases, starting with `v0.3.0-rc.1`.
To run them, use the guided [native-check kit](scripts/native-check/README.md): one script per OS that walks through the checks and produces an evidence zip to send back.

## Listen

- **Stations → Add station:** name, optional description, direct HTTP/HTTPS audio URL. MP3, AAC and HLS streams play on both platforms, and so do `.pls`/`.m3u` playlist files (the first entry plays). Ordinary webpage URLs are not supported; use the direct stream URL.
- **Schedule → Add time slot:** choose a station, 24-hour start time, time zone and days. Optional show label and enabled toggle. Enabled slots with the same start time, day and time zone conflict and are rejected (a zone this computer doesn't recognize counts as local time). This check compares zone names, so two differently named zones that share a clock (such as Europe/Athens and Europe/Helsinki) are not flagged.
- **Time zones:** each slot has its own time zone and defaults to **Local time** (this computer's zone). Pick another zone to follow a station's program guide abroad: type a city, region or offset (for example `Athens`, `new york` or `UTC+2`) or use **Browse**. The start time and days are in that zone, and DialShift switches at the matching moment on this computer. A slot with a zone shows it on the Schedule page with its next start in your time, for example `Next: Sun 11:00 your time`. A slot can fire on a different local day than its tab: a Monday 01:00 slot in `Asia/Kolkata` plays on Sunday evening in New York. **UP NEXT** adds the slot's own time and zone.
- Turn on **Follow my schedule** to immediately tune into the latest matching slot, even if that slot began on a previous day.
- Each station continues until the next scheduled start. There are no end-time/stop slots in this version.
- Manual station selection and Pause last until the next scheduled switch. Pause disconnects the live stream; Play rejoins live rather than replaying buffered audio.
- The schedule repeats weekly, each slot in its own time zone. Sleep/resume and missed starts catch up to the current slot. It does not wake a sleeping computer. During a repeated daylight-saving hour, a slot fires once per running session, at the first of the two times; a start inside a skipped hour fires when the clock jumps.
- **Changed your computer's time zone?** Restart DialShift. Time zone rules are read at startup, so a zone change (or an operating system time zone update) takes effect after a restart.
- A failed or stalled stream retries, then uses your optional fallback after three failures. While playing a fallback, the original is retried every two minutes. With no fallback, retries continue every 30 seconds after the initial quick retries.
- Closing or minimizing the window keeps DialShift running in the tray (notification area on Windows, menu bar on macOS). The tray menu has playback, stations, volume, the schedule toggle and **Quit DialShift**. On Windows, clicking the tray icon opens the window; on macOS, clicking it opens the menu.
- **Settings:** optional launch at sign-in, start in tray, fallback station and local settings folder. Launch at sign-in is off by default. No playback starts on first launch until you press Play or enable a populated schedule.

### Differences between the two players

- **Track titles:** Windows shows the current song title for `http://` streams that send one; `https://` stations show the station description instead. In testing, LibVLC received titles only from servers that answer in the older Shoutcast style (`ICY 200 OK`), so many other `http://` stations show the station description too. macOS always shows the station description (decision D26).
- **Streams at a `.pls` or `.m3u` path:** a raw audio stream served at a URL ending in `.pls` or `.m3u` may fail on macOS, because AVPlayer treats it as a playlist. Use the server's direct stream path instead (often `/;` or `/stream`). Real playlist files are fine.
- **Stream passwords:** a station URL can carry a user name and password (`http://user:password@host/…`). On Windows, the player keeps them until DialShift quits and may send them with other requests to the same server address and path, even for a station whose URL has none and even before the server asks. On macOS, the player keeps them until DialShift quits too, but sends them to another station only when the same server asks for a password for the same login realm. On both, the password never goes to another server, and on Windows never to another path. So removing the password from a station URL keeps playback working until DialShift quits.
- **Formats:** beyond MP3, AAC and HLS, macOS 26.5 also played Ogg Vorbis, Opus and FLAC-in-Ogg in testing. Older macOS versions have not been tested with those formats.

## Data

| | Windows | macOS |
|---|---|---|
| Settings | `%LOCALAPPDATA%\DialShift\settings.json` | `~/Library/Application Support/DialShift/settings.json` |
| Log | `dialshift.log` in the same folder (rotated to `dialshift.log.1` at 1 MiB) | same |
| Launch at sign-in | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `DialShift`. Settings shows it as off when it's turned off in Task Manager's Startup apps; turning it on in DialShift turns it back on there too. | `~/Library/LaunchAgents/com.tsiger.dialshift.plist`. Settings shows it as off when launchd has it disabled, and when DialShift can't confirm the state with launchd. If macOS Login Items shows DialShift as not allowed, enable it there. |

Writes are atomic. An unreadable settings file is copied to `settings.json.unreadable-<timestamp>` (with `-2`, `-3`, … if that name is taken; an earlier copy is never overwritten) before defaults are used, and a dialog names the copy. If no copy can be made (for example a full disk or a read-only folder), DialShift runs on defaults but won't replace the original: saving shows "Save failed" until a copy can be made. Back up the settings folder to move stations and schedules. Settings move between Windows and macOS: slot time zones are saved as IANA names (such as `Europe/Athens`), which both systems understand. If this computer doesn't recognize a saved zone, the slot runs on local time and shows `(unknown zone)` in the warning color. Editing the slot keeps the saved zone unless you pick another one. The log never contains stream credentials or full private stream URLs. No account, server, analytics or cloud sync. Listening connects directly to each selected radio provider.

**Reading the log:** on macOS, an `app.render_timer_fallback` line means that no display was active when DialShift started, for example because the screens were asleep. It is harmless: the tray, the schedule and playback work as usual, and the window draws normally once a display wakes.

## Radio station catalog

A curated catalog of **8,700+ radio stations** (Greece, France, Germany — Munich included) lives in [`data/`](data/):

- `data/output/dialshift-radio-catalog.xlsx` — multi-tab workbook (Numbers-friendly): an **Import Ready** tab with every working stream plus the exact three fields the app's *Add a frequency* dialog needs, per-country tabs, local **focus tabs (Munich, Paris, Toulouse, Aude)**, a curated **Ambient & Chill** collection tab (the SomaFM ambient family around the app's default stations + the best ambient/chillout streams worldwide), **station logos** (embedded images on the focus/collection tabs, Logo URL everywhere), and a README tab with instructions.
- `data/canonical/*.csv` — clean per-country lists.
- `data/countries/*.yaml` — the single source of truth for curated station facts (names, cities, genres, political leanings, verified stream URLs). Station data never lives in code.
- `data/build/` — one generic pipeline + XLSX writer; adding a country means dropping in one YAML.

See [`data/README.md`](data/README.md) for the full guide (schema, refresh command, how to import stations). The catalog is regenerated with `data/.venv/bin/python data/build/build_all.py --refresh`.

## Dependencies

Everything the app needs to build and to run, per platform.

### Build — what you install by hand

| Requirement | Windows | macOS |
|---|---|---|
| **.NET 10 SDK** | ✅ required | ✅ required |
| **Shell** | PowerShell 5.1+ | bash + `sh` |

- **.NET 10 SDK** (tested with `10.0.401`) is the only build tool you install yourself. On Windows it can come from the installer, or the build script will use a local copy at `%LOCALAPPDATA%\DialShift\sdk\dotnet.exe`. On macOS the script looks for `dotnet` on `PATH` (including `~/.dotnet`).
- The macOS package script also uses built-in macOS tools (`sips`, `iconutil`, `codesign`, `ditto`, `lipo`, `plutil`, `PlistBuddy`, `unzip`, `zipinfo`).

### Build — NuGet packages (restored automatically)

These are resolved by `dotnet restore`; you never download them by hand.

| Package | Version | Used by |
|---|---|---|
| `Avalonia` / `Avalonia.Desktop` / `Avalonia.Themes.Fluent` | 12.1.2 | `DialShift.App` UI toolkit |
| `Microsoft.Extensions.DependencyInjection` | 10.0.12 | `DialShift.App` composition root |
| `Microsoft.Win32.SystemEvents` | 10.0.12 | `DialShift.App` Windows resume notifications |
| `LibVLCSharp` | 3.10.1 | managed bindings over LibVLC (Windows playback) |
| `VideoLAN.LibVLC.Windows` | 3.0.23.1 | VLC native runtime for Windows (`win-x64` builds only) |

`DialShift.Core` adds no external packages; it is a plain `net10.0` class library. `DialShift.Tests` (a `net10.0` console app) adds two, and neither ships in a package:

| Package | Version | Used by |
|---|---|---|
| `Avalonia.Headless` | 12.1.2 | the headless UI checks (the real views and dialogs, rendered with Skia, no display needed) |
| `VideoLAN.LibVLC.Windows` | 3.0.23.1 | the real-LibVLC checks, referenced only when the tests are built on Windows |

### Run — what the end user needs

- **Nothing to install.** Both packages are **self-contained**: the .NET runtime is bundled with the app, and VLC is bundled on Windows.
  - Windows: `DialShift.exe` bundles .NET + VLC. Windows 10/11, x64.
  - macOS: `DialShift.app` bundles .NET only; playback uses AVPlayer from macOS. macOS 14.0 or later on Apple Silicon.
- A **network connection** to reach each radio station's direct HTTP/HTTPS audio URL.

Bundled third-party components and their licenses are tracked in [`licenses/`](licenses/) and summarized in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Build, test and publish

Requires a .NET 10 SDK. The solution builds on either OS, with no runtime identifier:

```bash
dotnet build DialShift.slnx -warnaserror
dotnet run --project DialShift.Tests/DialShift.Tests.csproj
```

The tests are deterministic console checks (no test framework); a non-zero exit code means a failure. The UI checks drive the real views headless, so no display is needed. A handful of checks that need the other OS, or its time-zone data, are reported as `SKIP`.

On Windows, `DialShift.Tests` also runs the real LibVLC engine behind the playback coordinator (`LibVlcEngine` suite, about 40 s). It uses the `adummy` output and an in-process HTTP/ICY server that serves a synthesized WAV tone, HTTP errors, a redirect, Basic auth, a captive-portal-style page, a server that never answers, a stream that ends, and `.pls`/`.m3u` playlists. The build copies the native runtime next to the test binary. On macOS its LibVLC checks report `SKIP (Windows only)`; the suite's audio-output and composition checks run on both.

Publish per runtime identifier. `DialShift.App` has exactly two, `win-x64` and `osx-arm64`:

```bash
dotnet publish DialShift.App/DialShift.App.csproj -c Release -r win-x64 --self-contained
dotnet publish DialShift.App/DialShift.App.csproj -c Release -r osx-arm64 --self-contained
```

The release scripts wrap that publish and are the single source of the package layout:

| Package | Script | Output | Verifier |
|---|---|---|---|
| Windows | `./scripts/build.ps1 [-SkipTests] [-Version <semver>]` (Windows) | `artifacts/DialShift-win-x64/`, `artifacts/DialShift-win-x64.zip` | `./scripts/verify-win-package.ps1 -Path <folder>` |
| macOS | `./scripts/build-mac-app.sh [--version <semver>] [--build-number <n>]` (macOS) | `dist/DialShift.app`, `dist/DialShift-osx-arm64-native-avplayer.zip` (published as `DialShift-macos-arm64.zip`) | `./scripts/verify-mac-app.sh <DialShift.app>` or `--zip <zip>` |

**Versions.** `<Version>` in `DialShift.App/DialShift.App.csproj` is the one source of the version number (`MAJOR.MINOR.PATCH`). `-Version`/`--version` may only add a pre-release suffix, for example `0.3.0-rc.1`; a different `MAJOR.MINOR.PATCH` is refused. The full version becomes the assembly's informational version (DialShift.exe's product version, and the first line of the log). The macOS `Info.plist` takes numbers only: `CFBundleShortVersionString` is `MAJOR.MINOR.PATCH`, and `CFBundleVersion` is the build number (the release workflow's run number; by default `MAJOR.MINOR.PATCH`). Without these options both scripts build the csproj version.

`build.ps1` runs the tests first, then publishes, copies the notices, licenses and `Install.ps1`, verifies and zips. The Windows verifier checks for an x64 GUI executable, only the `win-x64` VLC runtime, the Avalonia/Skia/ANGLE natives, no `.pdb` files, and the notices. `build-mac-app.sh` publishes, assembles the bundle (`Info.plist`, `.icns`, notices), signs it ad-hoc, verifies, zips and verifies the zip. In the bundle, `Contents/MacOS` holds only Mach-O code (the executable, the .NET runtime and the native libraries, plus a `DialShift.dll` link); the managed `.dll` and `.json` files live in `Contents/Resources/app`, joined by symlinks, so the signature is sealed in the files themselves and survives any unzip tool. The zip is written without extended attributes (no `._*` entries). The macOS verifier checks for an arm64-only executable, an arm64 slice in every native library, only Mach-O files in `Contents/MacOS` and no signature kept in extended attributes, no VLC libraries, the `Info.plist` keys (`LSUIElement`, `LSMinimumSystemVersion` 14.0, the ATS media exception), the icon, the notices and the signature; with `--zip` it rejects `._*` entries and verifies the bundle extracted with both `ditto` and `unzip`.

### Continuous integration

`.github/workflows/build.yml` is the one definition of the build. On `windows-latest` and `macos-latest` (Apple Silicon) it builds the solution with warnings as errors, runs the tests, runs the native UI smoke (with `DIALSHIFT_AUDIO_OUTPUT=dummy` on Windows, so playback does not depend on the runner's audio device), builds the package with the script above, extracts the zip a user would download and verifies it again (on macOS with both `ditto` and `unzip`, then launches the `unzip`-extracted app with `--smoke-test`), then uploads the zip (7-day retention) and the smoke results. `.github/workflows/ci.yml` runs it on every branch push; `.github/workflows/release.yml` runs it for a version tag and publishes the release only if every step passed.

### Releasing (maintainers)

Development and dry-run releases happen on the private repository `spyroskotsakis/dialshift-dev` (git remote `private`); users download from the public repository `spyroskotsakis/DialShift` (remote `origin`). `release.yml` names no repository: it publishes to whichever repository the tag is pushed to.

1. **Version.** Set `<Version>` in `DialShift.App/DialShift.App.csproj` to the new `MAJOR.MINOR.PATCH`. In `CHANGELOG.md`, move the `[Unreleased]` notes into a new `## [X.Y.Z] - YYYY-MM-DD` section (`## [X.Y.Z-rc.N]` for a pre-release) and update the link references at the bottom. Commit.
2. **Tag** the commit: `git tag -a vX.Y.Z -m "DialShift X.Y.Z"`. A pre-release uses a `-rc.N` suffix: `git tag -a v0.3.0-rc.1 -m "DialShift 0.3.0-rc.1"`.
3. **Dry run on the private repository:** `git push private vX.Y.Z`. `release.yml` first checks that the tag's `MAJOR.MINOR.PATCH` equals the csproj `<Version>` and that `CHANGELOG.md` has the section, then runs the whole build, test, smoke and verification workflow at the tag, then creates the GitHub Release with `DialShift-win-x64.zip`, `DialShift-macos-arm64.zip` and `SHA256SUMS.txt`, and the changelog section plus install, checksum and signing notes as its text. A tag with a `-` suffix becomes a pre-release; `vX.Y.Z` becomes the latest release. Check the release page, download both zips and check them against `SHA256SUMS.txt`.
4. **Public release:** push `main` and the tag to the public repository yourself: `git push origin main vX.Y.Z`. The same workflow builds, verifies and publishes the public release. The push-guard hook in `.claude/settings.json` blocks agent pushes to `origin` and `upstream` on purpose, so this push is always a manual maintainer step.
5. **Never push to `upstream`** (`tsiger/DialShift`).

GitHub Actions must be enabled on the public repository: it is a fork, and forks start with Actions turned off (**Actions** tab → enable workflows). To publish a tag again, delete its release (keep the tag) and run **Release** from the Actions tab (`workflow_dispatch`) with the tag name.

### Developer runs

`DIALSHIFT_DATA_DIR` points the app at a throwaway data folder (developer and CI use only). It must be an absolute path, and it replaces the settings, log and single-instance lock location.

```bash
DIALSHIFT_DATA_DIR=/tmp/dialshift-dev dotnet run --project DialShift.App -- --tray
```

`DIALSHIFT_AUDIO_OUTPUT=dummy` (developer and CI use only) makes the Windows build play through LibVLC's silent `adummy` output, so playback advances on machines without an audio device, such as hosted CI runners. Other values are ignored with a `playback.audio_output` warning in the log. macOS ignores the variable, because AVPlayer always uses the system output.

`--tray` starts with the window hidden. A second launch with the same data folder brings the running window to the front. SIGTERM and Ctrl+C quit through the same clean path as **Quit DialShift**.

| Exit code | Meaning |
|---|---|
| 0 | Normal quit, or a second launch whose activation was acknowledged |
| 1 | Startup failed and the startup-failure dialog was shown, including a single-instance lock file that can't be created in the data folder, or an older DialShift is still running (also used for a bad `DIALSHIFT_DATA_DIR` before any UI, and for an exception that escapes the UI toolkit) |
| 2 | A second launch whose activation was rejected or not answered |
| 3 | The lock was acquired but the activation channel could not start |
| 4 | `--smoke-test`: at least one check failed, or the smoke watchdog fired (`results.json` says which) |

### Native smoke test

```text
DialShift --smoke-test [--recovery-test] [--output <dir>]
```

Runs the real app (window, tray, dialogs and player) in a fresh temporary data folder, unless `DIALSHIFT_DATA_DIR` is set, at volume 0 and without touching launch at sign-in. It checks live playback, pause and schedule holds, the tray menu after every editor operation, hiding and restoring the window, persistence, and a real second process activating the first. `--recovery-test` adds the retry and fallback checks against an unavailable local endpoint. It writes `results.json`, screenshots and a copy of the log to `<dir>` (default: `smoke` in the data folder) and exits 0 only when every check passes. Live-stream checks need network access. Sleep/wake, a real sign-in and audible output still need a manual check on the target machine.

On macOS, run the bundled executable, `dist/DialShift.app/Contents/MacOS/DialShift`, to test what users get. App Transport Security applies only inside the bundle, so an unbundled run (`dotnet run`, or the build output that CI's first smoke uses) can't catch a missing `http://` media exception. CI therefore also runs `--smoke-test` on the packaged app, extracted from the release zip with `unzip`.

### Signing tiers

- **Development (what the scripts produce):** the Windows build is unsigned; the macOS bundle is ad-hoc signed. Used for local and CI builds and for pre-releases. Gatekeeper and SmartScreen will warn.
- **Signed release (not done yet):** Authenticode on Windows; Developer ID signing with the hardened runtime, notarization and stapling on macOS. Both need credentials, and a clean-machine install test comes before the first full release. Until then, releases are pre-releases built from the development tier (decision D53).

## Project

- `DialShift.Core`: station/settings models, local persistence, weekly schedule evaluation and occurrence tracking, and the playback coordinator (retry, fallback, schedule, wake). No UI or OS code.
- `DialShift.App`: the single Avalonia app for `win-x64` and `osx-arm64`. `Program.cs`, `AppComposition.cs` and `App.axaml.cs` are the composition root: `Microsoft.Extensions.DependencyInjection` wires the platform services (launch at login, power events, file reveal, logging, single instance, sleep-inclusive clock), the playback engine (LibVLC on Windows, AVPlayer on macOS), the `DialShift.Core` playback coordinator, and the MVVM views, tray and dialogs.
- `DialShift.Tests`: deterministic scheduling, persistence, playback, platform and UI checks.

Design notes live in [`docs/`](docs/): [docs/decisions.md](docs/decisions.md) records the decisions, and [docs/acceptance-matrix.md](docs/acceptance-matrix.md) tracks what has been verified.

Starter stations use SomaFM's published direct stream links: [Groove Salad](https://somafm.com/groovesalad/directstreamlinks.html), [Drone Zone](https://somafm.com/dronezone/directstreamlinks.html), [Secret Agent](https://somafm.com/secretagent/directstreamlinks.html). Streams can change; edit a station to update its URL. Station names belong to their respective owners; DialShift is unaffiliated.

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for bundled dependencies.

### Earlier versions

Before this single app, Windows and macOS had separate front-ends. The tag `legacy-last-known-good` (commit `82281e5`) keeps the last build of both, including the last Intel Mac build, as a rollback point.

## Inspiration & related projects

> *"Here's to the crazy ones. The rebels. The troublemakers… They push the human race forward."*
> — Apple, *Think Different* campaign (1997).

Saved-post research and the knowledge corpus (including the LinkedIn post that quotes the text above, corpus post #1173 by Charly Wargnier) live in a **separate** project: `~/Desktop/Coding-Agent-Research/`. It is deliberately not part of this repository — DialShift only contains the radio app and the station catalog in [`data/`](data/).
