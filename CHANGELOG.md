# Changelog

All notable changes to DialShift are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). A version's section becomes the notes of its GitHub Release (`scripts/release-notes.sh`), so every release tag needs a section here; see "Releasing (maintainers)" in the README.

Versions before 0.3.0 are not covered here: `v0.1.0` and `v0.2.0` were released by the upstream project, [tsiger/DialShift](https://github.com/tsiger/DialShift), with separate Windows and macOS front-ends.

## [Unreleased]

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

[Unreleased]: https://github.com/spyroskotsakis/DialShift/compare/v0.3.0-rc.1...HEAD
[0.3.0-rc.1]: https://github.com/spyroskotsakis/DialShift/releases/tag/v0.3.0-rc.1
