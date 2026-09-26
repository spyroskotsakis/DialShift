# DialShift

**Your radio, on time.** A native tray radio with a weekly listening schedule — for Windows and macOS.

One Avalonia app, `DialShift.App`, ships as two self-contained packages, and the Windows package also as a setup:

| Package | Platform | Playback | Status |
|---|---|---|---|
| `DialShift-Setup-win-x64.exe` | Windows 10/11, x64 | LibVLC (bundled) | Windows setup of the package below (per user, no administrator rights); unsigned; new since `v0.4.0`: CI is set up to build it on `windows-latest` and on macOS, check it and run its install, upgrade and uninstall silently, and a green run of those is still pending; it has not yet been run by hand on a real Windows PC (NC-19) |
| `DialShift-win-x64.zip` | Windows 10/11, x64 | LibVLC (bundled) | Windows build; unsigned; passes CI on `windows-latest`, not yet tested by hand on a real Windows PC |
| `DialShift-macos-arm64.zip` | macOS 14.0+, Apple Silicon only | Apple AVPlayer (part of macOS) | Native `osx-arm64` (AVPlayer, label `native-avplayer`); development build: ad-hoc signed, not yet notarized or clean-machine tested |

There is no Intel Mac build.

> **Testing status of 0.4.0.** On Windows, the build, the automated checks and the app's smoke test pass in CI on GitHub-hosted machines, but this version has **not yet been tested by hand on a real Windows PC**: the tray, audible playback, launch at sign-in, sleep and wake, and upgrading with `Install.ps1` are unverified on real hardware. The Mac build has been tested on the developer's Apple Silicon Mac only, not on a clean Mac. Please report problems as [issues](https://github.com/spyroskotsakis/DialShift/issues), with your `dialshift.log` (see [Data](#data)). The full list of open checks is in [Open items](#open-items--release-status).
>
> **New in 0.4.0: the station catalog search** (the Add station dialog below; see `[0.4.0]` in [CHANGELOG.md](CHANGELOG.md)). Its build, automated checks, package checks and smoke test pass in CI on `windows-latest` and `macos-latest`, which every release must pass before it is published, and it has been tested on the developer's Apple Silicon Mac. It has not yet been used by hand on a real Windows PC, and it has had no screen-reader or real-logo check by hand on either OS.

## Download

Download DialShift from the **[Releases page](https://github.com/spyroskotsakis/DialShift/releases)**. Every release has these files (releases up to `v0.4.0` have no setup, and their checksum file covers the two zips):

| File | For |
|---|---|
| `DialShift-Setup-win-x64.exe` | Windows 10/11, x64: installs for your user, no administrator rights; uninstall in Settings → Apps |
| `DialShift-win-x64.zip` | Windows 10/11, x64, without installing (or with `Install.ps1`) |
| `DialShift-macos-arm64.zip` | macOS 14.0 or later, Apple Silicon |
| `SHA256SUMS.txt` | SHA-256 checksums of the three downloads |

**Releases and "latest".** The "latest" links below always point at the newest full release, now `v0.4.0` (`v0.3.0` was the first). Pre-releases, such as `v0.3.0-rc.1` and `v0.3.0-rc.2`, are listed on the [Releases page](https://github.com/spyroskotsakis/DialShift/releases) but are never the "latest" release. `v0.3.0` and `v0.4.0` were released before every native check had passed; see the testing status above and [Open items](#open-items--release-status).

- Latest release: <https://github.com/spyroskotsakis/DialShift/releases/latest>
- Windows setup (from the first release with the setup on): <https://github.com/spyroskotsakis/DialShift/releases/latest/download/DialShift-Setup-win-x64.exe>
- Windows zip: <https://github.com/spyroskotsakis/DialShift/releases/latest/download/DialShift-win-x64.zip>
- macOS: <https://github.com/spyroskotsakis/DialShift/releases/latest/download/DialShift-macos-arm64.zip>
- Checksums: <https://github.com/spyroskotsakis/DialShift/releases/latest/download/SHA256SUMS.txt>

A specific release's files are always at `https://github.com/spyroskotsakis/DialShift/releases/download/<tag>/<file>`, for example `…/download/v0.4.0/DialShift-win-x64.zip`. What changed in each release is in [CHANGELOG.md](CHANGELOG.md).

### Check the download

Put `SHA256SUMS.txt` next to the file you downloaded. On macOS, in Terminal, in that folder (prints `OK` for each file you have):

```sh
shasum -a 256 -c SHA256SUMS.txt --ignore-missing
```

On Windows, in PowerShell, in that folder (prints `True` for each intact file you have):

```powershell
foreach ($file in 'DialShift-Setup-win-x64.exe', 'DialShift-win-x64.zip') {
    if (Test-Path $file) {
        $expected = ((Select-String -Path SHA256SUMS.txt -Pattern $file -SimpleMatch).Line -split '\s+')[0]
        "${file}: $((Get-FileHash $file -Algorithm SHA256).Hash -eq $expected)"
    }
}
```

### Install on Windows

**With the setup (recommended):** run `DialShift-Setup-win-x64.exe` (a local build is at `artifacts/DialShift-Setup-win-x64.exe`). It installs DialShift for your user in `%LOCALAPPDATA%\Programs\DialShift`, without administrator rights, adds a Start menu shortcut (and, if you tick it, a desktop shortcut) and lists DialShift in **Settings → Apps → Installed apps**. The last page starts DialShift unless you untick **Run DialShift**. The setup is not code-signed, so SmartScreen may say "Windows protected your PC": choose **More info**, then **Run anyway**. If Smart App Control is on, Windows 11 blocks unsigned programs without that choice; this setup and `DialShift.exe` from the zip alike.

- **Upgrading:** quit DialShift from its tray menu, then run the new release's setup. It installs the new version next to the installed one and swaps them, so a failed upgrade leaves the installed version as it was, and no file of the old version is left behind. It refuses a folder that holds files that are not part of DialShift and names one, so move your own files out of the install folder before you upgrade. It keeps your stations, schedule and settings, keeps launch at sign-in on if you turned it on, and also upgrades an install made with `Install.ps1`. If DialShift is still running, the setup says so and waits for **Retry**. Running an older release's setup the same way goes back to that version.
- **Uninstalling:** in **Settings → Apps → Installed apps**, choose DialShift → **Uninstall**. It asks whether to keep your stations, schedule and settings (**Yes** keeps `%LOCALAPPDATA%\DialShift`; **No** deletes that folder). It removes the files it installed, the shortcuts and launch at sign-in when that starts this install; files you added to the install folder stay.
- **Automation:** `DialShift-Setup-win-x64.exe /S` installs silently (no desktop shortcut, DialShift is not started) and `"%LOCALAPPDATA%\Programs\DialShift\Uninstall DialShift.exe" /S` uninstalls silently and keeps the settings; add `_?=%LOCALAPPDATA%\Programs\DialShift` to wait for the uninstaller to finish (the uninstaller file itself then stays for you to delete). `/D=<folder>`, last on the command line, in capitals and without quotes (even when the folder has spaces), installs into another folder; it is meant for automation and tests. The setup installs only into exactly the folder `/D=` names: a folder it can't use (a relative path, a drive that doesn't exist, a file in its place, a drive or network share root), anything that makes Windows read another folder (a forward slash, quotes or a wildcard in it, or another switch after it, such as `/D=C:\Apps\DialShift /S`), a `/D=` it can't read (quoted as a whole, lowercase) or a command line of 1,023 characters or more stops it with exit code 13, and it never installs into another folder instead. The uninstaller likewise refuses to work in a drive or share root (`_?=C:\`), with 13. Exit codes of the setup and the uninstaller: 0 done, 1 cancelled, 10 DialShift is running, 11 another install is running, 12 not 64-bit Windows 10 or later, 13 a refused folder (the settings folder, a drive or network share root, a folder with other files, a `/D=` folder or switch the setup can't use, or a second install location while DialShift is installed elsewhere; for the uninstaller, a drive or share root), 14 the install failed and the previous install is unchanged, or the uninstaller could not delete a file (it names it; the entry in Settings → Apps stays, so you can uninstall again), 15 `DialShift.exe` could not be opened to check whether DialShift is running.

**Without installing:** extract `DialShift-win-x64.zip` and open `DialShift.exe` (a local build is at `artifacts/DialShift-win-x64/DialShift.exe`). Keep the whole folder together: it includes .NET and VLC. No separate runtime or VLC installation is needed. The same SmartScreen note applies.

Optional install from the zip: right-click `Install.ps1` in the extracted folder and choose **Run with PowerShell**. It copies the app to `%LOCALAPPDATA%\Programs\DialShift` and adds a Start menu shortcut, without administrator rights, but no Apps & features entry and no uninstaller. It does not turn on launch at sign-in unless you already chose that setting. To upgrade, quit DialShift and run the new release's `Install.ps1` the same way: it copies the new version next to the installed one and then swaps them, so if the upgrade fails (for example because a file of the installed copy is still open) the installed version stays as it was. The window stays open with the result until you press Enter. Run it from the extracted folder; it refuses to run from inside `%LOCALAPPDATA%\Programs\DialShift`, and it refuses to replace an install made with the setup (upgrade that one with the new setup, or uninstall it first).

### Install on macOS

Requires macOS 14.0 or later on Apple Silicon (M1 or newer); there is no Intel build. Unzip `DialShift-macos-arm64.zip` with Finder or any unzip tool (a local build is at `dist/DialShift.app`), move `DialShift.app` to `/Applications` and open it. The build is ad-hoc signed, not notarized, so macOS blocks the first launch: open **System Settings → Privacy & Security** and choose **Open Anyway** (on older macOS, right-click the app and choose **Open**).

DialShift runs natively on Apple Silicon with Apple's AVPlayer: no bundled VLC and no Rosetta 2. It is a menu-bar app, so there is no Dock icon.

Move DialShift to `/Applications` before you turn on launch at login. If you open it straight from Downloads, macOS runs it from a temporary copy that disappears when it quits, so DialShift refuses with "Move DialShift to Applications first, then turn this on again."

### Upgrading from an earlier DialShift

This applies if you used the earlier separate Windows or macOS app, including `v0.2.0` from [tsiger/DialShift](https://github.com/tsiger/DialShift).

- **Your settings carry over.** DialShift keeps using the same data folder (see [Data](#data)), so your stations, schedule, fallback and volume are there on first start. Slots that an earlier version saved with a dot in the time (`08.30`, on computers set to Danish, Finnish, Indonesian and some other formats) play again and are saved as `08:30`.
- **Quit the old app first**, from its tray or menu-bar icon. While it runs, the new DialShift shows "An older DialShift is still running. Quit it from its tray icon, then open DialShift again." and exits, so the two never play at once.
- **Windows, launch at sign-in:** the setup and `Install.ps1` keep it. They use the same `Run` value as before and point it at the new copy. If you run the new `DialShift.exe` from another folder instead, Settings shows "Launch at sign-in points to an older copy of DialShift"; turn it on again to fix it.
- **macOS, launch at login:** an older app's launch-at-login entry is recognized. Settings shows it on, with a note if the entry came from an older DialShift; turn launch at login off and on to replace it with the new one. If Settings says it "points to an older copy", turn it on again. Move DialShift to `/Applications` first (see [Install on macOS](#install-on-macos)).
- **Intel Macs:** there is no Intel build of this version, so keep using your current app. The last Intel build's source is kept at the tag `legacy-last-known-good` (see [Earlier versions](#earlier-versions)).
- **Going back to an older version** keeps your stations and schedule. `v0.3.0` drops the notes of stations added from the catalog the next time it saves; the apps before it also ignore slot time zones and remove them.

## Open items / release status

Both packages build, test and pass the native smoke in CI. Some checks still need things CI can't provide: Windows hardware, a clean Mac, signing credentials, a real login and a person at the screen. `v0.3.0` was released as a full release before those checks (decision D57 in [docs/decisions.md](docs/decisions.md)), and so is `v0.4.0` (D92), with the testing status stated in the release notes and at the top of this README; the checks stay open and are tracked for the next releases. [docs/open-items.md](docs/open-items.md) lists every one of them, with why it is blocked and how to run and record it.
To run them, use the guided [native-check kit](scripts/native-check/README.md): one script per OS that walks through the checks and produces an evidence zip to send back.

## Listen

- **Stations → Add station:** the *Add a frequency* dialog starts with a search of the built-in [station catalog](#radio-station-catalog) (8,270 stations with a working stream). Type part of a station's name, local name or city, in any case and with or without accents, or a frequency (`1015` or `101.5` finds FM 101.5); the **Country**, **City**, **Type**, **Genre** and **Language** filters narrow the list, and **Clear** resets the search and the filters. The best matches come first (a name that starts with your text, then one that contains it, then by listener votes), up to 50 at a time with a count such as `Showing 50 of 214 matches`; with an empty search, Down lists the most-voted stations. Down and Up move through the results, Enter picks one, Escape closes the list (a second Escape closes the dialog). The pane beside the form shows the highlighted or picked station: name, place, frequency, type and genre, language, votes, its notes in full and its logo. Picking a station fills **Name**, **Description / genre** and **Stream URL**; change any field before you save. A station added from the catalog keeps its notes as long as its stream URL stays the catalog's.
- **Manual entry** still works for any stream, under *Or enter stream details manually*: name, optional description, direct HTTP/HTTPS audio URL. MP3, AAC and HLS streams play on both platforms, and so do `.pls`/`.m3u` playlist files (the first entry plays). Ordinary webpage URLs are not supported; use the direct stream URL. When nothing matches, the dialog says `No stations match — adjust filters or enter the stream manually` and leaves the form free. If the catalog file is missing or unreadable, the dialog says `Catalog unavailable — enter stream details manually`, the search is off and manual entry works as before. **Edit station** is the plain form, without the search.
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

A curated station catalog (Greece, France, Germany — Munich included — and an internet **Ambient & Chill** collection) lives in [`data/`](data/). The canonical lists hold 8,661 stations, including stations with no working stream so that each country's whole FM dial is there; **8,270** of them, every station with a working stream that the app accepts, are the catalog the app searches (Germany 4,753, France 1,826, Greece 1,667, internet 24).

- `data/output/app-catalog.json` — the catalog the Add station dialog searches. It is generated from the files below by the data pipeline, validated in the same run and checked in, so a fresh `dotnet build` needs no Python. The build copies it next to the app in both packages: next to `DialShift.exe` in the Windows zip, and in `DialShift.app/Contents/Resources/app` on the Mac. The dialog's status line shows its size and date (`8,270 stations · catalog updated 2026-09-26`).
- `data/output/dialshift-radio-catalog.xlsx` — multi-tab workbook (Numbers-friendly): an **Import Ready** tab with every working stream and the three columns to copy when you enter a station by hand (the fallback, for a station that is not in the catalog or when the dialog says the catalog is unavailable), per-country tabs, local **focus tabs (Munich, Paris, Toulouse, Aude)**, the **Ambient & Chill** collection tab (the SomaFM ambient family around the app's default stations + the best ambient/chillout streams worldwide), **station logos** (embedded images on the focus/collection tabs, Logo URL everywhere), and a README tab with instructions.
- `data/canonical/*.csv` — clean per-country lists.
- `data/countries/*.yaml` and `data/collections/*.yaml` — the single source of truth for curated station facts (names, cities, genres, political leanings, verified stream URLs), with `data/languages.yaml` and `data/frequency-bands.yaml` for the catalog's language names and frequency band words. Station data never lives in code.
- `data/build/` — one generic pipeline + the app-catalog export + the XLSX writer; adding a country means dropping in one YAML.

**Refreshing the catalog** is manual (decision D65): run `data/.venv/bin/python data/build/build_all.py --refresh`, check the `app-catalog:` lines it prints (for example `app-catalog: working=8277 url_excluded=7 duplicates_removed=0 exported=8270` and `app-catalog: languages=42 unknown=0`), then commit the regenerated `data/canonical/*.csv`, `data/output/dialshift-radio-catalog.xlsx` and `data/output/app-catalog.json` together. The next app build ships the new catalog, with no app code change. CI checks only that the file is in both packages and parses, not that it is fresh (D67).

See [`data/README.md`](data/README.md) for the full guide (schema, the app catalog's rules, the refresh procedure, how to add a country).

## Dependencies

Everything the app needs to build and to run, per platform.

### Build — what you install by hand

| Requirement | Windows | macOS |
|---|---|---|
| **.NET 10 SDK** | ✅ required | ✅ required |
| **Shell** | PowerShell 5.1+ | bash + `sh` |

- **.NET 10 SDK** (tested with `10.0.401`) is the only build tool you install yourself. On Windows it can come from the installer, or the build script will use a local copy at `%LOCALAPPDATA%\DialShift\sdk\dotnet.exe`. On macOS the script looks for `dotnet` on `PATH` (including `~/.dotnet`).
- The macOS package script also uses built-in macOS tools (`sips`, `iconutil`, `codesign`, `ditto`, `lipo`, `plutil`, `PlistBuddy`, `unzip`, `zipinfo`).
- **Release builds only:** the Windows setup is built with **NSIS 3.12** (`makensis`; `brew install makensis` on macOS; CI uses the official `nsis-3.12.zip`, pinned by its SHA-256 in `scripts/build-win-setup.sh`) and Python 3 (standard library; `/usr/bin/python3` on macOS, `python` on Windows) under bash (Git Bash on Windows). 7-Zip (`7z`, or `7zz` from `brew install sevenzip`) is optional locally: with it, the setup's verifier also compares its contents with the package. Nothing of NSIS is needed to build or run the app.

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
- A **network connection** to reach each radio station's direct HTTP/HTTPS audio URL, and for the station logos the Add station dialog shows (a logo that does not load shows the station's initial instead). The station catalog itself is a file in the package; searching it needs no network.

Bundled third-party components and their licenses are tracked in [`licenses/`](licenses/) and summarized in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md), which also credits the sources of the bundled station catalog (radio-browser.info, and Wikipedia under CC BY-SA 4.0).

## Build, test and publish

Requires a .NET 10 SDK. The solution builds on either OS, with no runtime identifier:

```bash
dotnet build DialShift.slnx -warnaserror
dotnet run --project DialShift.Tests/DialShift.Tests.csproj
```

The tests are deterministic console checks (no test framework); a non-zero exit code means a failure. On a Mac, run an unattended suite under `caffeinate -i` (for example `caffeinate -i dotnet run --project DialShift.Tests/DialShift.Tests.csproj`), so the Mac does not go to sleep during the run. The UI checks drive the real views headless, so no display is needed. A handful of checks that need the other OS, or its time-zone data, are reported as `SKIP`.

On Windows, `DialShift.Tests` also runs the real LibVLC engine behind the playback coordinator (`LibVlcEngine` suite, about 40 s). It uses the `adummy` output and an in-process HTTP/ICY server that serves a synthesized WAV tone, HTTP errors, a redirect, Basic auth, a captive-portal-style page, a server that never answers, a stream that ends, and `.pls`/`.m3u` playlists. The build copies the native runtime next to the test binary. On macOS its LibVLC checks report `SKIP (Windows only)`; the suite's audio-output and composition checks run on both.

Publish per runtime identifier. `DialShift.App` has exactly two, `win-x64` and `osx-arm64`:

```bash
dotnet publish DialShift.App/DialShift.App.csproj -c Release -r win-x64 --self-contained
dotnet publish DialShift.App/DialShift.App.csproj -c Release -r osx-arm64 --self-contained
```

The release scripts wrap that publish and are the single source of the package layout:

| Package | Script | Output | Verifier |
|---|---|---|---|
| Windows | `./scripts/build.ps1 [-SkipTests] [-Version <semver>]` (Windows, or PowerShell 7 on macOS) | `artifacts/DialShift-win-x64/`, `artifacts/DialShift-win-x64.zip` | `./scripts/verify-win-package.ps1 -Path <folder>` |
| Windows setup | `bash scripts/build-win-setup.sh [--package <folder> \| --zip <zip>] [--version <semver>] [--output <exe>]` (macOS, or Git Bash on Windows; after `build.ps1`) | `artifacts/DialShift-Setup-win-x64.exe` | `bash scripts/verify-win-setup.sh <exe> [--version <semver>] [--package <folder>] [--require-contents]` |
| macOS | `./scripts/build-mac-app.sh [--version <semver>] [--build-number <n>]` (macOS) | `dist/DialShift.app`, `dist/DialShift-osx-arm64-native-avplayer.zip` (published as `DialShift-macos-arm64.zip`) | `./scripts/verify-mac-app.sh <DialShift.app>` or `--zip <zip>` |

**Versions.** `<Version>` in `DialShift.App/DialShift.App.csproj` is the one source of the version number (`MAJOR.MINOR.PATCH`). `-Version`/`--version` may only add a pre-release suffix, for example `0.3.0-rc.1`; a different `MAJOR.MINOR.PATCH` is refused. The full version becomes the assembly's informational version (DialShift.exe's product version, and the first line of the log). The macOS `Info.plist` takes numbers only: `CFBundleShortVersionString` is `MAJOR.MINOR.PATCH`, and `CFBundleVersion` is the build number (the release workflow's run number; by default `MAJOR.MINOR.PATCH`). Without these options both scripts build the csproj version.

`build.ps1` runs the tests first, then publishes, copies the notices, licenses and `Install.ps1`, verifies and zips. `build-win-setup.sh` packs that verified folder (default `artifacts/DialShift-win-x64`, or the folder inside a zip given with `--zip`) into the setup with NSIS: the package minus `Install.ps1`, plus the NSIS licence (`licenses/NSIS-COPYING.txt`). It refuses any `makensis -VERSION` other than the pinned `v3.12` (`--print-nsis-pin` prints the pin), a version whose `MAJOR.MINOR.PATCH` is not the csproj's, and a package whose `DialShift.dll` carries another version. Its output is deterministic: the same package, version and NSIS give the same bytes, on the Mac and, the target, on Windows. The setup verifier checks the file without running it: a 32-bit GUI executable with the NSIS data at the end of the image and nothing cut off or appended, the integrity CRC, the version resource, the manifest (no administrator rights, DPI-aware, NSIS 3.12) and the icon; with `--package` and 7-Zip also that its contents equal the package, file by file. The Windows verifier checks for an x64 GUI executable, only the `win-x64` VLC runtime, the Avalonia/Skia/ANGLE natives, no `.pdb` files, the notices, and the station catalog: `app-catalog.json` next to `DialShift.exe`, parseable JSON with `schema_version` 1 and a non-empty `stations` array of objects. `build-mac-app.sh` publishes, assembles the bundle (`Info.plist`, `.icns`, notices), signs it ad-hoc, verifies, zips and verifies the zip. In the bundle, `Contents/MacOS` holds only Mach-O code (the executable, the .NET runtime and the native libraries, plus a `DialShift.dll` link); the managed `.dll` and `.json` files live in `Contents/Resources/app`, joined by symlinks, so the signature is sealed in the files themselves and survives any unzip tool. The zip is written without extended attributes (no `._*` entries). The macOS verifier checks for an arm64-only executable, an arm64 slice in every native library, only Mach-O files in `Contents/MacOS` and no signature kept in extended attributes, no VLC libraries, the `Info.plist` keys (`LSUIElement`, `LSMinimumSystemVersion` 14.0, the ATS media exception), the icon, the notices, the station catalog (`Contents/Resources/app/app-catalog.json`, with the same checks as on Windows) and the signature; with `--zip` it rejects `._*` entries and verifies the bundle extracted with both `ditto` and `unzip`.

### Continuous integration

`.github/workflows/build.yml` is the one definition of the build. On `windows-latest` and `macos-latest` (Apple Silicon) it builds the solution with warnings as errors, runs the tests, runs the native UI smoke (with `DIALSHIFT_AUDIO_OUTPUT=dummy` on Windows, so playback does not depend on the runner's audio device), builds the package with the script above, extracts the zip a user would download and verifies it again (on Windows, then runs its `Install.ps1` under Windows PowerShell into a temporary `LOCALAPPDATA`: a fresh install, an upgrade over it, an upgrade while a file of the install is open and a run while another install holds the install lock (both must fail and change nothing), and the refused cases: a copy inside the install folder, the installed copy itself, and an install folder inside the source; on macOS with both `ditto` and `unzip`, then launches the `unzip`-extracted app with `--smoke-test`), runs `scripts/test-package-verifiers.sh` on both OSes (the package verifier against a set of broken and valid station catalog files, each of which it must reject or accept; on macOS the three files only the app rejects also go through the copied bundle's `--smoke-test`), then, on Windows, installs NSIS 3.12 from its pinned zip, builds the setup from the extracted zip, verifies it (contents included) and its broken-file fixtures, checks that its recursive delete removes junctions and symbolic links as links only, and runs it silently into a temporary folder: a fresh install, an upgrade, a locked file, a held install lock, DialShift running, refused folders, `Install.ps1` over the setup install, a silent uninstall and the setup over an `Install.ps1` install, checking files, shortcuts, the Apps & features entry and launch at sign-in each time, and that the settings folder is never touched. It then uploads the zips and the setup (7-day retention) and the smoke results. A separate macOS job builds the setup again from the Windows job's zip with Homebrew's `makensis` and compares the two setups byte for byte. `.github/workflows/ci.yml` runs it on every branch push; `.github/workflows/release.yml` runs it for a version tag and publishes the release only if every step passed.

### Releasing (maintainers)

Development and dry-run releases happen on the private repository `spyroskotsakis/dialshift-dev` (git remote `private`); users download from the public repository `spyroskotsakis/DialShift` (remote `origin`). `release.yml` names no repository: it publishes to whichever repository the tag is pushed to.

1. **Version.** Set `<Version>` in `DialShift.App/DialShift.App.csproj` to the new `MAJOR.MINOR.PATCH`. In `CHANGELOG.md`, move the `[Unreleased]` notes into a new `## [X.Y.Z] - YYYY-MM-DD` section (`## [X.Y.Z-rc.N]` for a pre-release) and update the link references at the bottom. Commit.
2. **Tag** the commit: `git tag -a vX.Y.Z -m "DialShift X.Y.Z"`. A pre-release uses a `-rc.N` suffix: `git tag -a v0.3.0-rc.1 -m "DialShift 0.3.0-rc.1"`.
3. **Dry run on the private repository:** `git push private vX.Y.Z`. `release.yml` first checks that the tag's `MAJOR.MINOR.PATCH` equals the csproj `<Version>` and that `CHANGELOG.md` has the section, then runs the whole build, test, smoke and verification workflow at the tag, then creates the GitHub Release with `DialShift-win-x64.zip`, `DialShift-Setup-win-x64.exe`, `DialShift-macos-arm64.zip` and `SHA256SUMS.txt`, and the changelog section plus install, checksum and signing notes as its text. A tag with a `-` suffix becomes a pre-release; `vX.Y.Z` becomes the latest release. Check the release page, download the three files and check them against `SHA256SUMS.txt`.
4. **Public release:** push `main` and then the tag to the public repository yourself, in two pushes: `git push origin main`, then `git push origin vX.Y.Z`. The same workflow builds, verifies and publishes the public release. The push-guard hook in `.claude/settings.json` blocks agent pushes to `origin` and `upstream` on purpose, so this push is always a manual maintainer step. Then check that a **Release** run for the tag started in the Actions tab (see the first-push caveat below).
5. **Never push to `upstream`** (`tsiger/DialShift`).

GitHub Actions must be enabled on the public repository: it is a fork, and forks start with Actions turned off (**Actions** tab → enable workflows). They have been enabled since the first public push on 2026-09-25.

**First-push caveat.** When one push first brings (or changes) `.github/workflows` on the default branch and also carries tags or other branches, GitHub may start workflows for `main` only. On 2026-09-25 the first public push, of `main`, the feature branch and three tags, started CI for `main` and nothing else: no CI for the branch and no Release run for either version tag, most likely because the workflows were not yet on the default branch when those ref events were handled. GitHub also documents that it creates no events for tags when more than three tags are pushed at once. So push `main` first and each tag in a separate, later push (`git push origin main`, then `git push origin vX.Y.Z`). If a Release run is missing, start it by hand **on the tag itself**: `gh workflow run Release --ref vX.Y.Z -f tag=vX.Y.Z` (add `-R <owner>/<repo>` outside the clone). Never `--ref main`: GitHub takes the workflow files from the ref the run is started on while the build checks out the tag, so the workflow and the code must come from the same commit. `release.yml` refuses a run whose ref is not the tag. `gh workflow run CI --ref <branch>` starts CI for a branch.

**To publish a tag again,** delete its release (keep the tag) and run **Release** from the Actions tab (`workflow_dispatch`): choose the tag under **Use workflow from → Tags** and enter the same tag name, or run `gh workflow run Release --ref vX.Y.Z -f tag=vX.Y.Z`.

**Actions billing.** GitHub Actions is free on public repositories. On the private repository the minutes count against the account's plan, and macOS and Windows runners use them at a multiple of the Linux rate, so the private dry run can stop for billing rather than for the code: GitHub then does not start a job ("The job was not started because recent account payments have failed or your spending limit needs to be increased"). Fix the billing or spending limit in the account settings, then re-run only what did not run: `gh run rerun <run-id> -R spyroskotsakis/dialshift-dev --failed`.

#### Backup: release from a local build

Use this only when GitHub Actions cannot run `release.yml` for a tag, for example while the private repository's jobs are refused for billing. The normal path stays the tag push and `release.yml`. `scripts/release-local.sh` (decision D58) makes the same release from a build on an Apple Silicon Mac with the .NET 10 SDK, `gh` logged in, PowerShell 7 (`brew install powershell`) and NSIS 3.12 (`brew install makensis`; 7-Zip, `brew install sevenzip`, adds the setup's content check):

```bash
scripts/release-local.sh vX.Y.Z                                                  # dry run: build, verify, write the assets, print the gh command
scripts/release-local.sh vX.Y.Z --repo spyroskotsakis/dialshift-dev --publish   # the private dry run
scripts/release-local.sh vX.Y.Z --publish                                        # the public release (the default --repo spyroskotsakis/DialShift)
```

It builds the tag in a temporary git worktree, so your checkout (which must be clean) is not touched. It runs the `release.yml` checks of the tag, version and changelog, then the build with warnings as errors, the tests, both packages (the Windows one cross-built with `scripts/build.ps1` under `pwsh`), `verify-win-package.ps1` and `verify-mac-app.sh --zip` on the zips it uploads, the Windows setup built from the uploaded zip with `scripts/build-win-setup.sh` (which verifies it), the macOS native and bundle smokes, and `scripts/test-package-verifiers.sh` (the station catalog fixtures `build.yml` runs, on the bundle and the extracted Windows zip, and the setup verifier fixtures). The smokes and the fixtures' bundle smokes open DialShift windows for a few minutes. It writes `DialShift-win-x64.zip`, `DialShift-Setup-win-x64.exe`, `DialShift-macos-arm64.zip`, `SHA256SUMS.txt` and `release-notes.md` to `artifacts/release-local/<tag>/` (tags up to `v0.4.0` have no setup). With `--publish` it first checks that the tag is on the target repository at the same commit and has no release yet, then runs the same `gh release create` as `release.yml`. The notes end with a line saying the release was built and uploaded locally, and which checks ran.

- **Not checked locally:** the tests' Windows-only checks (they report `SKIP` on macOS), the Windows native smoke, the `Install.ps1` cases and the setup's install, upgrade and uninstall cases; those need Windows. The setup's contents are compared only when 7-Zip is installed. Packages are restored from your NuGet cache; `release.yml` restores from nuget.org.
- **Without PowerShell,** build the Windows zip on Windows at the tag (`.\scripts\build.ps1 -Version X.Y.Z`) and pass `--win-zip <path>`. The script refuses a zip that was not built as that version from the tag's commit. Tags up to `v0.3.0` have a Windows-only `build.ps1`, so they need `--win-zip`.
- **Pushing the tag stays your step:** push it to the target repository before `--publish`. If a **Release** run for the tag can still run there, cancel it (`gh run cancel <run-id> -R <owner>/<repo>`): it would fail at `gh release create` once the release exists. On the public repository Actions are free, so the tag push normally publishes there by itself.

### Developer runs

`DIALSHIFT_DATA_DIR` points the app at a throwaway data folder (developer and CI use only). It must be an absolute path, and it replaces the settings, log and single-instance lock location.

```bash
DIALSHIFT_DATA_DIR=/tmp/dialshift-dev dotnet run --project DialShift.App -- --tray
```

`DIALSHIFT_CATALOG_PATH=<absolute path>` (developer and test use only) makes the Add station dialog read another catalog file instead of the `app-catalog.json` next to the app, for example `data/output/app-catalog.json` right after a pipeline run, without rebuilding. A relative path, or a file that is missing or not a valid catalog, makes the catalog unavailable (the dialog falls back to manual entry and the log has a `catalog.unavailable` warning); there is no fallback to the bundled file.

```bash
DIALSHIFT_CATALOG_PATH="$PWD/data/output/app-catalog.json" dotnet run --project DialShift.App
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

Runs the real app (window, tray, dialogs and player) in a fresh temporary data folder, unless `DIALSHIFT_DATA_DIR` is set, at volume 0 and without touching launch at sign-in. It checks live playback, pause and schedule holds, the tray menu after every editor operation, hiding and restoring the window, persistence, a real second process activating the first, and that the station catalog loads from the app folder (this check fails whenever `DIALSHIFT_CATALOG_PATH` is set, because the smoke must test the bundled file). `--recovery-test` adds the retry and fallback checks against an unavailable local endpoint. It writes `results.json`, screenshots and a copy of the log to `<dir>` (default: `smoke` in the data folder) and exits 0 only when every check passes. Live-stream checks need network access. Sleep/wake, a real sign-in and audible output still need a manual check on the target machine.

On macOS, run the bundled executable, `dist/DialShift.app/Contents/MacOS/DialShift`, to test what users get. App Transport Security applies only inside the bundle, so an unbundled run (`dotnet run`, or the build output that CI's first smoke uses) can't catch a missing `http://` media exception. CI therefore also runs `--smoke-test` on the packaged app, extracted from the release zip with `unzip`.

### Signing tiers

- **Development (what the scripts produce):** the Windows build, its setup and the setup's uninstaller are unsigned; the macOS bundle is ad-hoc signed. Used for local and CI builds and, so far, for every release, including `v0.3.0` (D57) and `v0.4.0`. Gatekeeper and SmartScreen will warn.
- **Signed release (not done yet):** Authenticode on Windows; Developer ID signing with the hardened runtime, notarization and stapling on macOS. Both need credentials and a clean-machine install test. `v0.3.0` (decision D57) and `v0.4.0` shipped in the development tier before them.

## Project

- `DialShift.Core`: station/settings models, local persistence, weekly schedule evaluation and occurrence tracking, the playback coordinator (retry, fallback, schedule, wake), and the station catalog's search (`StationCatalogQuery`: matching, frequency queries, filters and ranking over a pre-folded `StationCatalogIndex`). No UI, OS or file code.
- `DialShift.App`: the single Avalonia app for `win-x64` and `osx-arm64`. `Program.cs`, `AppComposition.cs` and `App.axaml.cs` are the composition root: `Microsoft.Extensions.DependencyInjection` wires the platform services (launch at login, power events, file reveal, logging, single instance, sleep-inclusive clock), the playback engine (LibVLC on Windows, AVPlayer on macOS), the `DialShift.Core` playback coordinator, the station catalog provider (reads `app-catalog.json` once, off the UI thread) and logo loader, and the MVVM views, tray and dialogs.
- `DialShift.Tests`: deterministic scheduling, persistence, playback, platform, catalog and UI checks.

Design notes live in [`docs/`](docs/): [docs/decisions.md](docs/decisions.md) records the decisions, and [docs/acceptance-matrix.md](docs/acceptance-matrix.md) tracks what has been verified.

Starter stations use SomaFM's published direct stream links: [Groove Salad](https://somafm.com/groovesalad/directstreamlinks.html), [Drone Zone](https://somafm.com/dronezone/directstreamlinks.html), [Secret Agent](https://somafm.com/secretagent/directstreamlinks.html). Streams can change; edit a station to update its URL. Station names belong to their respective owners; DialShift is unaffiliated.

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for bundled dependencies and the station catalog's data sources.

### Earlier versions

Before this single app, Windows and macOS had separate front-ends. The tag `legacy-last-known-good` (commit `82281e5`) keeps the last build of both, including the last Intel Mac build, as a rollback point.

## Inspiration & related projects

> *"Here's to the crazy ones. The rebels. The troublemakers… They push the human race forward."*
> — Apple, *Think Different* campaign (1997).

Saved-post research and the knowledge corpus (including the LinkedIn post that quotes the text above, corpus post #1173 by Charly Wargnier) live in a **separate** project: `~/Desktop/Coding-Agent-Research/`. It is deliberately not part of this repository — DialShift only contains the radio app and the station catalog in [`data/`](data/).
