---
name: release-packaging
description: Use when building or packaging DialShift release artifacts (macOS .app bundle, Windows publish/zip, installer, CI).
---

# Release packaging

One project, `DialShift.App`, two artifacts (decision D2: `win-x64;osx-arm64`, never `osx-x64`). The scripts are the single source of the artifact layout; CI runs the same scripts.

| Artifact | Script | Output | Verified by |
|---|---|---|---|
| Windows `win-x64` | `scripts/build.ps1` | `artifacts/DialShift-win-x64/` + `artifacts/DialShift-win-x64.zip` | `scripts/verify-win-package.ps1` |
| macOS Apple Silicon `osx-arm64` | `scripts/build-mac-app.sh` | `dist/DialShift.app` + `dist/DialShift-osx-arm64-<label>.zip` | `scripts/verify-mac-app.sh` |

## macOS (`osx-arm64`, native, no VLC)

- `scripts/build-mac-app.sh`: self-contained `osx-arm64` publish, `.app` assembly, `.icns` from `DialShift.App/Assets/icon-512.png` (`sips` + `iconutil`), `Info.plist`, ad-hoc signature, verification, then `ditto -c -k --keepParent` zip. It exports `PATH="$HOME/.dotnet:$PATH"`; the .NET 10 SDK is at `~/.dotnet` on this Mac. macOS only.
- Playback is Apple AVPlayer through system framework linkage. The bundle must contain **no VLC dylibs**. The check is case-sensitive: `LibVLCSharp.dll` (managed, compile-time reference) is allowed; `libvlc*` and `*vlc*.dylib` are not.
- `Info.plist`: `CFBundleIdentifier=com.tsiger.dialshift`, `CFBundleDisplayName`, `CFBundleIconFile=DialShift.icns`, `LSUIElement=true` (menu-bar app, no Dock icon), `NSHighResolutionCapable`, `LSMinimumSystemVersion=12.0` (floor of the .NET 10 runtime binaries), version from the csproj `<Version>`.
- `NSAppTransportSecurity` → `NSAllowsArbitraryLoadsForMedia=true` is **mandatory**: without it AVPlayer inside a bundle rejects every `http://` stream (43 % of the catalog, docs/spikes.md). `dotnet run` does not enforce ATS, so only a bundled run shows the failure. Never use the blanket `NSAllowsArbitraryLoads`.
- **Honest labeling:** the zip and CI artifact carry `MACOS_LABEL` (default `preview`). Change it to `native-avplayer` in `.github/workflows/ci.yml` only after `MacAvPlayerPlaybackEngine` passes SP-01/SP-02 in `docs/acceptance-matrix.md`.
- `scripts/verify-mac-app.sh <DialShift.app>` asserts: arm64-only executable, arm64 slice in every dylib, no VLC natives, `Info.plist` keys above, icon, notices, exec bit, `codesign --verify --deep --strict`, ad-hoc signature.
- With `LSUIElement=true` there is no Dock icon: test quit, reopen and second-instance activation under that condition.

## Windows (`win-x64`)

- `scripts/build.ps1 [-SkipTests]`: tests, fresh self-contained `win-x64` publish of `DialShift.App`, adds `Install.ps1`, README, notices and the Windows license texts, verifies, then zips (D7: zip first, MSIX/installer later). Uses a local SDK at `%LOCALAPPDATA%\DialShift\sdk\dotnet.exe` when present.
- `scripts/verify-win-package.ps1 -Path <folder>` asserts: x64 GUI-subsystem `DialShift.exe`, `libvlc\win-x64\libvlc.dll` + plugins, **no other VLC architectures** (`win-x86`, `win-arm64`), Avalonia/Skia/ANGLE natives, notices and licenses.
- `scripts/Install.ps1`: per-user install to `%LOCALAPPDATA%\Programs\DialShift`, no administrator rights; replaces the previous install folder, adds a Start menu shortcut, keeps an existing `HKCU\...\Run` `DialShift` entry pointing at the new path with `--tray`. Settings in `%LOCALAPPDATA%\DialShift` are never touched.

## Third-party notices

- `THIRD-PARTY-NOTICES.md` is conditional per package: LibVLC/VLC native runtime in the Windows package only; macOS uses Apple system frameworks and ships no VLC. Each script copies only the license texts that apply to its package; keep the script lists and the notices table in sync when a dependency changes.
- Check shipped natives after any package bump: publish both RIDs and list `*.dll` / `*.dylib`.
- `licenses/WindowsDesktop-LICENSE.txt` covers only the legacy WPF project; remove it (and its notices row) when `DialShift/` is retired.

## Signing tiers (D7)

- **Ad-hoc** (`codesign --force --deep --sign -`): what the macOS script does. Development and CI verification only; not publicly distributable without Gatekeeper warnings (users must right-click → Open).
- **Developer ID + notarization** (macOS public release): sign with a Developer ID Application certificate and the hardened runtime (`--options runtime`, entitlement `com.apple.security.cs.allow-jit` for the .NET JIT), submit with `xcrun notarytool submit --wait`, then `xcrun stapler staple`. Needs Apple credentials; documented, not performed here.
- **Windows:** development builds are unsigned. Public releases need Authenticode signing (`signtool`); check SmartScreen reputation on a clean machine.

## CI (`.github/workflows/ci.yml`)

- Runs on every branch push and `workflow_dispatch`, on the **private** remote only. Matrix `windows-latest` + `macos-latest` (Apple Silicon).
- Phases per job: build (Core, Tests, App one by one, no RID, `-warnaserror`; plus the WPF project on Windows while `DialShift/DialShift.csproj` exists) → test (`dotnet run --project DialShift.Tests`) → package (the scripts above) → verify the zip a user downloads (extract, re-run the verifier) → upload (`DialShift-win-x64`, `DialShift-osx-arm64-<label>`, 7-day retention).
- Add new checks (for example headless smokes) as steps in the matching phase, not as placeholders.
- CI does not replace a manual Apple Silicon gate or clean-machine installs (acceptance matrix §9: NC-05, NC-07, NC-09).

## Checks before shipping (both)

- A second launch must ACTIVATE the existing instance, not merely exit.
- Clean-machine test the artifacts; smoke-test tray, schedule, settings persistence, and one `http://` stream from the **bundled** macOS app.
