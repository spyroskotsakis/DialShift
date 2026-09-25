# Changelog

All notable changes to DialShift are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). A version's section becomes the notes of its GitHub Release (`scripts/release-notes.sh`), so every release tag needs a section here; see "Releasing (maintainers)" in the README.

Versions before 0.3.0 are not covered here: `v0.1.0` and `v0.2.0` were released by the upstream project, [tsiger/DialShift](https://github.com/tsiger/DialShift), with separate Windows and macOS front-ends.

## [Unreleased]

## [0.3.0] - 2026-09-25

The first full release of the rebuilt DialShift. The app is the same as in [0.3.0-rc.2](CHANGELOG.md#030-rc2---2026-09-25); only the documentation and a check in the release workflow changed.

### Testing status and known limitations

Please read this before you install.

- **Windows has not been tested by hand on a real PC yet.** The build, more than 1,700 automated checks and the app's own smoke test (window, tray, dialogs, playback through a silent audio output, a second launch) pass on Windows in CI, on GitHub-hosted `windows-latest` machines. Nobody has yet used this version on a physical Windows PC. So these are **unverified on real hardware**: clicking the tray icon, audible playback, launch at sign-in, sleep and wake, and upgrading with `Install.ps1`. If something doesn't work, please [open an issue](https://github.com/spyroskotsakis/DialShift/issues) and attach `%LOCALAPPDATA%\DialShift\dialshift.log`.
- **macOS has been tested only on the developer's own Apple Silicon Mac** (macOS 26.5). There the packaged app passed its smoke test when started the way Finder starts apps (through LaunchServices), and a second launch brought the running app to the front. It has not been tested on a clean Mac or on macOS 14 and 15. The hands-on checks of the menu-bar icon, audible playback, launch at login, and closing the lid are still open.
- **Neither download is signed for public distribution.** Windows is not code-signed, so SmartScreen may warn: choose **More info**, then **Run anyway**. The Mac app is ad-hoc signed and not notarized, so the first launch needs **System Settings → Privacy & Security → Open Anyway**. See [Install on Windows](README.md#install-on-windows) and [Install on macOS](README.md#install-on-macos).
- **Going back:** `v0.2.0` is still on the [tsiger/DialShift Releases page](https://github.com/tsiger/DialShift/releases/tag/v0.2.0). The tag `legacy-last-known-good` holds the source of the last build of the earlier Windows and macOS apps, including the last Intel Mac build. Your stations and schedule carry over when you go back, but the older app ignores slot time zones and deletes them the next time it saves.
- All the known limitations of [0.3.0-rc.1](CHANGELOG.md#030-rc1---2026-09-25) still apply. The checks that are still open are listed in [docs/open-items.md](docs/open-items.md), and they are tracked for 0.3.x.

### What's new since 0.2.0

- **One app for Windows and Apple Silicon Macs.** One Avalonia app replaces the separate WPF and macOS apps. On Windows it plays through LibVLC. On the Mac it is native Apple Silicon and plays through Apple's AVPlayer, with no VLC and no Rosetta 2.
- **Per-slot time zones.** Each schedule slot can have its own time zone, and daylight saving time is handled.
- **Sturdier playback and startup.** Both players share one tested recovery path: retries, the fallback station, and catching up after sleep. The Mac app no longer crashes when it starts with no active display. An unreadable settings file is kept, never overwritten, and stream passwords are removed from the log. A second launch brings the running app to the front.
- **Safer upgrades.** Your settings carry over from the earlier apps. DialShift won't play alongside an older DialShift that is still running. On the Mac, launch-at-login entries from the older apps are recognized. On Windows, `Install.ps1` swaps the new version in and puts the installed copy back if the swap fails.

The full lists are in [0.3.0-rc.1](CHANGELOG.md#030-rc1---2026-09-25) (the rebuild) and [0.3.0-rc.2](CHANGELOG.md#030-rc2---2026-09-25) (the `Install.ps1` fix).

## [0.3.0-rc.2] - 2026-09-25

**Pre-release: native checks are pending.** Everything in [0.3.0-rc.1](CHANGELOG.md#030-rc1---2026-09-25) still applies, including its known limitations. [docs/open-items.md](docs/open-items.md) lists the checks that are still open.

### Fixed

- **Windows: `Install.ps1` no longer deletes the installed copy before the new one is in place.** It copies the new version into a folder next to `%LOCALAPPDATA%\Programs\DialShift`, moves the installed copy aside, moves the new one in, and then deletes the old one. A file that is only briefly locked (antivirus, the search indexer) is retried for a few seconds. If a file stays locked or the copy fails, the installed version is put back as it was and the script stops with a message that says what happened. If only deleting the old copy fails, DialShift is installed and the script tells you which folder to delete; the next run of `Install.ps1` removes it. Two runs at once (a double-click) no longer interfere: the second waits a few seconds, then stops with "Another DialShift install is running."
- **Windows: `Install.ps1` refuses to run from inside the install folder.** Running it from a zip extracted into `%LOCALAPPDATA%\Programs\DialShift` used to delete the files it was copying. It now stops with "Extract the release zip to another folder, such as Downloads, and run Install.ps1 from there." It also refuses a folder that contains the install folder.
- **Windows: `Install.ps1` keeps its window open until you press Enter.** "Run with PowerShell" closed the window as soon as the script ended, so its messages could not be read.

## [0.3.0-rc.1] - 2026-09-25

**Pre-release: native checks are pending.** Clean-machine installs, a real sign-in, sleep and wake on real hardware, audible output and signing still need checks that CI can't run. [docs/open-items.md](docs/open-items.md) lists each one.

### Added

- **Per-slot time zones.** Each schedule slot has its own time zone, with **Local time** as the default. The picker searches cities, regions and offsets (`Athens`, `new york`, `UTC+2`). The Schedule page shows each zoned slot's next start in your time, and **UP NEXT** shows the slot's own time and zone. Daylight saving is handled: in a repeated hour a slot fires once, at the first of the two times, and a start inside a skipped hour fires when the clock jumps. Zones are saved as IANA names, so settings still move between Windows and macOS. A zone this computer doesn't recognize runs on local time and is shown as `(unknown zone)`.
- **Native smoke test.** `DialShift --smoke-test [--recovery-test] [--output <dir>]` runs the real app (window, tray, dialogs, live playback, pause and schedule holds, persistence, and a real second launch) in an isolated data folder, writes `results.json` and screenshots, and exits 4 when a check fails. CI runs it on Windows and macOS, and again on the packaged macOS app.
- **Documented exit codes:** 0 normal quit or activated second launch, 1 startup failure, 2 second launch not activated, 3 activation channel failure, 4 smoke test failed.
- **Developer settings:** `DIALSHIFT_DATA_DIR` (an isolated data folder) and `DIALSHIFT_AUDIO_OUTPUT=dummy` (silent LibVLC output on Windows machines without an audio device).
- **GitHub Releases** built from version tags, with `SHA256SUMS.txt` and this changelog as the release notes.
- **Safe upgrade from the earlier apps.** DialShift won't start while an older DialShift is still running: it says "An older DialShift is still running. Quit it from its tray icon, then open DialShift again." and exits, so two players never play at once. See [Upgrading from an earlier DialShift](README.md#upgrading-from-an-earlier-dialshift) in the README.

### Changed

- **One app for both platforms.** A single Avalonia 12 app, `DialShift.App`, replaces the WPF Windows app and the separate macOS app. Windows now uses the Fluent dark theme.
- **Native Apple Silicon.** The macOS build is native `osx-arm64` and plays through Apple's AVPlayer: no bundled VLC and no Rosetta 2. `http://` stations play inside the app bundle through App Transport Security's media-only exception. The minimum is macOS 14.0.
- **Hardened single instance.** A second launch brings the running window to the front, including when it arrives before the window exists. On macOS the activation socket is readable by your user only. A lock file that can't be used is reported as a startup failure instead of "already running".
- **Launch at sign-in shows the real state.** Settings shows it as off when it has been turned off in Task Manager's Startup apps (Windows) or disabled in launchd (macOS), and when DialShift can't confirm the state. The setting says "sign in" on Windows and "log in" on macOS.
- **macOS: launch at login from an earlier DialShift carries over.** An entry made by an earlier app, including `v0.2.0`, is recognized; turning launch at login off and on replaces it, so there is only ever one. After an in-place upgrade in `/Applications`, DialShift still starts at login.
- **macOS: launch at login needs DialShift in Applications.** Opened straight from Downloads, macOS runs the app from a temporary copy, so DialShift refuses to turn launch at login on and says "Move DialShift to Applications first, then turn this on again."
- **About shows the full version**, including a pre-release suffix such as `0.3.0-rc.1`.
- **Redacted logs.** One redactor removes stream credentials and private URL parts from the log and from both players' diagnostics. Several processes can share the log safely. The first line of each start records the version, runtime, architecture and player.
- **Playback recovery shared by both players:** retries, the fallback station after three failures, and catch-up after sleep, from one tested coordinator.
- **Settings recovery never loses the original.** An unreadable `settings.json` is copied to `settings.json.unreadable-<timestamp>` (with `-2`, `-3`, … so no copy is overwritten), and saving is refused while no copy could be made.
- **Packages.** Exactly two: `DialShift-win-x64.zip` (with only the x64 VLC runtime) and `DialShift-macos-arm64.zip`. The macOS bundle keeps only native code in `Contents/MacOS`, so its signature survives any unzip tool.
- Buttons size to their text with headroom and shorten long labels with an ellipsis instead of clipping them.

### Fixed

- macOS: the app no longer crashes at startup when no display is active, for example when it starts at login with the screens asleep. It starts on a fallback render loop and draws normally once a display wakes.
- **Schedule slots on computers whose time format uses a dot** (for example Danish, Finnish or Indonesian) now play. Earlier versions saved such a slot as `08.30`, which never played and was not caught as a conflict. Slots already saved that way play again and are saved as `08:30`.

### Removed

- The WPF Windows app (`DialShift/`) and the separate macOS app (`DialShift.Mac/`). The tag `legacy-last-known-good` keeps their last build.
- The Intel Mac (`osx-x64`) build. The last one is at `legacy-last-known-good`.

### Known limitations

- **Unsigned for public distribution.** Windows builds are not Authenticode-signed, so SmartScreen may warn. The macOS app is ad-hoc signed and not notarized, so the first launch needs **System Settings → Privacy & Security → Open Anyway**. The macOS build has not yet been tested on a clean Mac.
- Apple Silicon only; there is no Intel Mac build.
- Track titles: macOS always shows the station description. Windows shows song titles only for some `http://` streams.
- On macOS, a raw audio stream served at a URL ending in `.pls` or `.m3u` may fail; use the server's direct stream path.
- Formats beyond MP3, AAC and HLS (Ogg Vorbis, Opus, FLAC in Ogg) were tested on macOS 26.5 only.
- A change of the computer's time zone takes effect after DialShift restarts.
- The schedule does not wake a sleeping computer, and slots have no end time.
- Going back to an earlier DialShift keeps stations and schedule, but the earlier app ignores slot time zones and removes them when it next saves.

[Unreleased]: https://github.com/spyroskotsakis/DialShift/compare/v0.3.0...HEAD
[0.3.0]: https://github.com/spyroskotsakis/DialShift/compare/v0.3.0-rc.2...v0.3.0
[0.3.0-rc.2]: https://github.com/spyroskotsakis/DialShift/compare/v0.3.0-rc.1...v0.3.0-rc.2
[0.3.0-rc.1]: https://github.com/spyroskotsakis/DialShift/releases/tag/v0.3.0-rc.1
