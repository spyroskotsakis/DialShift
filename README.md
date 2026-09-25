# DialShift

**Your radio, on time.** A native tray radio with a weekly listening schedule — for Windows and macOS.

## Run

Open `artifacts/DialShift-win-x64/DialShift.exe`, or extract the release ZIP and open `DialShift.exe`. Keep the whole folder together: it includes .NET and VLC. No separate runtime or VLC installation is needed. Windows 10/11, x64.

Optional install: right-click `Install.ps1` in the extracted release and choose **Run with PowerShell**. It copies the app to `%LOCALAPPDATA%\Programs\DialShift` and adds a Start menu shortcut, without administrator rights. It does not enable Windows startup unless you already chose that setting.

## Run on macOS

macOS 11+ on Intel or Apple Silicon. Copy `dist/DialShift.app` into `/Applications` (or run it from anywhere), then open it once.

- DialShift is a menu-bar app — there is no Dock icon. Use the tray icon to open the window, play/pause, skip, pick a station, toggle the schedule, adjust volume, or quit.
- On Apple Silicon it runs under **Rosetta 2**, because the bundled VLC native library (`libvlc.dylib`) ships x86_64-only. Rosetta installs automatically the first time you open an Intel app (`softwareupdate --install-rosetta` if prompted); Intel Macs run it natively.
- Preferences live at `~/Library/Application Support/DialShift/settings.json`; diagnostic errors go to `dialshift.log` in the same folder. Optional launch at sign-in uses a LaunchAgent, off by default.

## Listen

- **Stations → Add station:** name, optional description, direct HTTP/HTTPS audio URL. MP3, AAC and HLS are handled by VLC. Ordinary webpage URLs and playlist files that require selecting a child stream are not supported; use the direct stream URL.
- **Schedule → Add time slot:** choose a station, 24-hour start time and days. Optional show label and enabled toggle. Conflicting enabled slots on the same day/time are rejected.
- Turn on **Follow my schedule** to immediately tune into the latest matching slot, even if that slot began on a previous day.
- Each station continues until the next scheduled start. There are no end-time/stop slots in this version.
- Manual station selection and Pause last until the next scheduled switch. Pause disconnects the live stream; Play rejoins live rather than replaying buffered audio.
- The schedule repeats weekly in your local time zone. Sleep/resume and missed starts catch up to the current slot. It does not wake a sleeping computer. During a repeated daylight-saving hour, a slot fires once per running session; skipped starts catch up after the jump.
- A failed or stalled stream retries, then uses your optional fallback after three failures. While playing a fallback, the original is retried every two minutes. With no fallback, retries continue every 30 seconds after the initial quick retries.
- Closing/minimizing the window keeps the app running in the tray (notification area on Windows, menu bar on macOS). Use the tray icon to reopen the window and for playback, stations, volume, schedule toggle and **Quit DialShift**.
- **Settings:** optional launch at sign-in, start in tray, fallback station and local settings folder. Startup is off by default. No playback starts on first launch until you press Play or enable a populated schedule.

## Data

Preferences live at `%LOCALAPPDATA%\DialShift\settings.json` on Windows and `~/Library/Application Support/DialShift/settings.json` on macOS, with atomic writes. An unreadable file is preserved as `settings.json.unreadable-*` before defaults are used. Back up this folder to move stations and schedules. Diagnostic errors go to `dialshift.log` in the same folder. No account, server, analytics or cloud sync. Listening connects directly to each selected radio provider.

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
| **Rosetta 2** | — | Apple Silicon only |

- **.NET 10 SDK** (tested with `10.0.401`) is the only build tool you install yourself. On Windows it can come from the installer, or the build script will use a local copy at `%LOCALAPPDATA%\DialShift\sdk\dotnet.exe`. On macOS the script looks for `dotnet` on `PATH` (including `~/.dotnet`).
- **Rosetta 2** — macOS on **Apple Silicon only**. The bundled VLC native library (`libvlc.dylib`) ships x86_64-only, so the build and run use x64 under Rosetta. Install once with `softwareupdate --install-rosetta`; Intel Macs need nothing extra.

### Build — NuGet packages (restored automatically)

These are resolved by `dotnet restore`; you never download them by hand.

| Package | Version | Used by |
|---|---|---|
| `Avalonia` / `Avalonia.Desktop` / `Avalonia.Themes.Fluent` | 11.3.22 | macOS UI toolkit |
| `LibVLCSharp` | 3.10.1 | managed bindings over LibVLC (both platforms) |
| `VideoLAN.LibVLC.Mac` | 3.1.3.1 | VLC native runtime for macOS (x86_64 `libvlc.dylib`) |
| `VideoLAN.LibVLC.Windows` | 3.0.23.1 | VLC native runtime for Windows |
| WPF + Windows Forms | in SDK | Windows-only UI frameworks (enabled via `UseWPF`/`UseWindowsForms`) |

`DialShift.Core` and `DialShift.Tests` add no external packages — they are plain `net10.0` class libraries/console.

### Run — what the end user needs

- **Nothing to install.** Both builds are **self-contained**: the .NET runtime and VLC are bundled with the app.
  - Windows: `DialShift.exe` bundles .NET + VLC. Windows 10/11, x64.
  - macOS: `DialShift.app` bundles .NET + VLC. macOS 11+, Intel or Apple Silicon (Rosetta 2).
- A **network connection** to reach each radio station's direct HTTP/HTTPS audio URL.

Bundled third-party components and their licenses are tracked in [`licenses/`](licenses/) and summarized in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Build and verify

Requires a .NET 10 SDK. On Windows the build script also recognizes a local SDK at `%LOCALAPPDATA%\DialShift\sdk`.

Windows:

```powershell
./scripts/build.ps1
```

macOS — assembles `dist/DialShift.app`; on Apple Silicon, ensure Rosetta 2 is installed for the x86_64 VLC library:

```bash
./scripts/build-mac-app.sh
```

Core checks without external test packages:

```powershell
dotnet run --project DialShift.Tests -c Release
```

Application integration checks (isolated temporary preferences, muted live playback, no startup changes):

```powershell
./artifacts/DialShift-win-x64/DialShift.exe --smoke-test --output C:\temp\dialshift-checks
```

Writes `results.json` and UI renders, then exits. Live-stream checks require network access. `--recovery-test` together with `--smoke-test` additionally checks retry/fallback using an intentionally unavailable local endpoint. Hardware sleep, actual Windows sign-in and audible output require a manual check on the target PC.

## Project

- `DialShift.Core`: station/settings models, local persistence, weekly schedule evaluation and occurrence tracking.
- `DialShift`: Windows WPF interface, native notification icon, LibVLC playback, retry/fallback and Windows startup/resume integration.
- `DialShift.App`: the Avalonia interface. It currently ships as the macOS app: a transitional `osx-x64` build that runs under Rosetta on Apple Silicon (decision D2). It provides the menu-bar tray icon, LibVLC playback, retry/fallback and optional LaunchAgent startup, and uses `DialShift.Core` unchanged. It becomes the single Windows + macOS front-end as [docs/single-codebase-refactor.md](docs/single-codebase-refactor.md) is carried out.
- `DialShift.Tests`: deterministic scheduling, persistence and validation checks.

Starter stations use SomaFM's published direct stream links: [Groove Salad](https://somafm.com/groovesalad/directstreamlinks.html), [Drone Zone](https://somafm.com/dronezone/directstreamlinks.html), [Secret Agent](https://somafm.com/secretagent/directstreamlinks.html). Streams can change; edit a station to update its URL. Station names belong to their respective owners; DialShift is unaffiliated.

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for bundled dependencies.

## Roadmap

The Windows (WPF) and macOS (Avalonia) front-ends currently duplicate the UI and playback layer. [docs/single-codebase-refactor.md](docs/single-codebase-refactor.md) captures the plan to converge them into a single Avalonia codebase — deferred, not yet started.

## Inspiration & related projects

> *"Here's to the crazy ones. The rebels. The troublemakers… They push the human race forward."*
> — Apple, *Think Different* campaign (1997).

Saved-post research and the knowledge corpus (including the LinkedIn post that quotes the text above, corpus post #1173 by Charly Wargnier) live in a **separate** project: `~/Desktop/Coding-Agent-Research/`. It is deliberately not part of this repository — DialShift only contains the radio app and the station catalog in [`data/`](data/).
