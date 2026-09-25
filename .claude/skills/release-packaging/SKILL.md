---
name: release-packaging
description: Use when building or packaging DialShift release artifacts (macOS .app bundle and zip, Windows publish/zip, installer, CI).
---

# Release packaging

One project, `DialShift.App`, two artifacts: `win-x64` and `osx-arm64`, never `osx-x64` (decision D13, which amends D2; the last Intel/Rosetta build is only at tag `legacy-last-known-good`). The scripts are the single source of the artifact layout; CI runs the same scripts.

| Artifact | Script | Output | Verified by |
|---|---|---|---|
| Windows `win-x64` | `scripts/build.ps1 [-SkipTests]` | `artifacts/DialShift-win-x64/` + `artifacts/DialShift-win-x64.zip` | `scripts/verify-win-package.ps1 -Path <folder>` |
| macOS Apple Silicon `osx-arm64` | `scripts/build-mac-app.sh` | `dist/DialShift.app` + `dist/DialShift-osx-arm64-<label>.zip` | `scripts/verify-mac-app.sh <DialShift.app>` and `scripts/verify-mac-app.sh --zip <zip>` |

## macOS (`osx-arm64`, native, no VLC)

- `scripts/build-mac-app.sh`: self-contained `osx-arm64` publish into `publish/osx-arm64`, `.app` assembly (layout below), `.icns` from `DialShift.App/Assets/icon-512.png` (`sips` + `iconutil`), `Info.plist`, notices and the macOS license texts in `Contents/Resources`, ad-hoc signature, bundle verification, then the zip and its verification. It exports `PATH="$HOME/.dotnet:$PATH"`; the .NET 10 SDK is at `~/.dotnet` on this Mac. macOS only.
- **Bundle layout (SR-03, D51).** `Contents/MacOS` holds **only Mach-O files**: the `DialShift` apphost, the .NET runtime and native dylibs, `createdump`, plus a `DialShift.dll` symlink to `../Resources/app/DialShift.dll`. Everything else the publish produces (managed `.dll` files, `deps.json`, `runtimeconfig.json`) lives in `Contents/Resources/app`, which has symlinks back to the Mach-O files in `Contents/MacOS` (the .NET host uses the symlink target folder as its application folder). With this layout codesign seals the managed files as resources. Never put a non-Mach-O file in `Contents/MacOS`: codesign would keep its signature in extended attributes, which `unzip` and many archivers drop, and the bundle would stop verifying. The script fails if the publish output has subfolders or links.
- **Zip:** `ditto -c -k --norsrc --noextattr --noacl --keepParent dist/DialShift.app <zip>`: no extended attributes and no AppleDouble `._*` entries; permissions and symlinks are kept.
- Playback is Apple AVPlayer through system framework linkage. The bundle must contain **no VLC dylibs**. The check is case-sensitive: `LibVLCSharp.dll` (managed, compile-time reference) is allowed; `libvlc*`, `*vlc*.dylib` and a `vlc` folder are not.
- `Info.plist`: `CFBundleIdentifier=com.tsiger.dialshift`, `CFBundleName`/`CFBundleDisplayName=DialShift`, `CFBundleExecutable=DialShift`, `CFBundlePackageType=APPL`, `CFBundleIconFile=DialShift.icns`, `LSUIElement=true` (menu-bar app, no Dock icon), `NSHighResolutionCapable`, `LSMinimumSystemVersion=14.0` (decision D22: the binaries load on 12.0, but the AVPlayer format corpus is verified on macOS 26.5 only, so older versions are not claimed), version from the csproj `<Version>`.
- `NSAppTransportSecurity` → `NSAllowsArbitraryLoadsForMedia=true` is **mandatory** (D32): without it AVPlayer inside a bundle rejects every `http://` stream (43 % of the catalog, docs/spikes.md). `dotnet run` and the plain build output do not enforce ATS, so only a bundled run shows the failure. Never use the blanket `NSAllowsArbitraryLoads`.
- **Honest labeling (D36):** the zip and CI artifact carry `MACOS_LABEL`, which is `native-avplayer` in `.github/workflows/ci.yml` and the script's default (lowercase letters, digits and dashes). The label says what the build is (native arm64, AVPlayer, no LibVLC), not that it is release-ready: the README keeps "ad-hoc signed, not yet notarized or clean-machine tested" until NC-07 and NC-09 pass.
- `scripts/verify-mac-app.sh <DialShift.app>` asserts: the `Info.plist` keys `CFBundleIdentifier`, `CFBundleDisplayName`, `CFBundleExecutable`, `CFBundlePackageType`, `CFBundleIconFile`, `LSUIElement`, `LSMinimumSystemVersion=14.0`, a version string and the ATS media exception (and no blanket `NSAllowsArbitraryLoads`), icon, notices and license texts, the executable is a file with the exec bit, an arm64-only executable, an arm64 slice in every dylib, no VLC natives, only Mach-O files in `Contents/MacOS`, the `Contents/MacOS/DialShift.dll` link, no broken links, no `com.apple.cs.*` signature in extended attributes, no loose `._*` files, `codesign --verify --deep --strict`, and an ad-hoc signature.
- `scripts/verify-mac-app.sh --zip <zip>` rejects `._*`/`__MACOSX` entries, extracts the zip with `ditto -x -k` and with `unzip`, and runs the bundle checks on each copy.
- Smoke-test the **bundled** executable, not `dotnet run`: `dist/DialShift.app/Contents/MacOS/DialShift --smoke-test [--recovery-test] [--output <dir>]`. It uses an isolated temp data folder and volume 0, writes `results.json`, screenshots and the log to `<dir>`, and exits 0 when every check passed or 4 when a check failed or the watchdog fired.
- With `LSUIElement=true` there is no Dock icon: test quit, reopen and second-instance activation under that condition.

## Windows (`win-x64`)

- `scripts/build.ps1 [-SkipTests]`: tests (unless `-SkipTests`), a fresh self-contained `win-x64` publish of `DialShift.App` into `artifacts/DialShift-win-x64`, `.pdb` files removed, README, `THIRD-PARTY-NOTICES.md`, `Install.ps1` and the Windows license texts added, verification, then the zip (D7: zip first, MSIX/installer later). Uses a local SDK at `%LOCALAPPDATA%\DialShift\sdk\dotnet.exe` when present, else `dotnet` on `PATH`.
- `scripts/verify-win-package.ps1 -Path <folder>` asserts: x64 GUI-subsystem `DialShift.exe`, `libvlc\win-x64\libvlc.dll` + plugins, **no other VLC architectures** (`win-x86`, `win-arm64`), Avalonia/Skia/ANGLE natives, no `.pdb` symbol files (`build.ps1` strips the ones native NuGet assets bring), notices and licenses.
- Smoke-test the published executable: `(Start-Process .\artifacts\DialShift-win-x64\DialShift.exe -ArgumentList '--smoke-test','--recovery-test','--output',"$PWD\smoke" -Wait -PassThru).ExitCode` (PowerShell does not wait for a GUI-subsystem exe started with `&`). On a machine without an audio device, set `$env:DIALSHIFT_AUDIO_OUTPUT = 'dummy'` first: LibVLC then plays through its silent `adummy` output (D35; developer and CI use only; any other value is ignored with a warning, and macOS ignores the variable).
- Icon: `DialShift.App/Assets/dialshift.ico` is the executable (`<ApplicationIcon>`) and tray icon, derived from `Assets/icon-512.png`, with frames 16, 20, 24, 32, 40, 48, 64, 128 (32-bit DIB) and 256 (PNG). Frames of 40 px and below are redrawn with thicker strokes so they stay legible in the tray; 48 px and up are downscales. Regenerate it whenever the art changes. The opaque dark tile reads on both light and dark taskbars.
- The csproj references `VideoLAN.LibVLC.Windows` only when `RuntimeIdentifier` is `win-x64` and sets `VlcWindowsX86Enabled`/`VlcWindowsArm64Enabled=false`, so only `libvlc/win-x64` ships.
- `scripts/Install.ps1`: per-user install to `%LOCALAPPDATA%\Programs\DialShift`, no administrator rights; refuses while that copy is running; replaces the previous install folder, adds a Start menu shortcut, keeps an existing `HKCU\...\Run` `DialShift` entry pointing at the new path with `--tray`. Settings in `%LOCALAPPDATA%\DialShift` are never touched.

## Exit codes (D29)

| Code | Meaning |
|---|---|
| 0 | Normal quit, or a second launch whose activation was acknowledged |
| 1 | Startup failed (dialog shown), including an unusable single-instance lock file, a bad `DIALSHIFT_DATA_DIR`, or an exception escaping the UI toolkit |
| 2 | A second launch whose activation was rejected or not answered |
| 3 | The lock was acquired but the activation channel could not start |
| 4 | `--smoke-test`: at least one check failed, or the smoke watchdog fired |

## Third-party notices

- `THIRD-PARTY-NOTICES.md` is conditional per package: the LibVLC/VLC native runtime is in the Windows package only; macOS uses Apple system frameworks and ships no VLC. Each script copies only the license texts that apply to its package (`licenses/` in the Windows zip, `DialShift.app/Contents/Resources/licenses/` in the macOS bundle); keep the script lists and the notices sections in sync when a dependency changes.
- Check shipped natives after any package bump: publish both RIDs and list `*.dll` / `*.dylib`.

## Signing tiers (D7)

- **Ad-hoc** (`codesign --force --deep --sign -`): what the macOS script does. Development and CI verification only; not publicly distributable without Gatekeeper warnings (users allow it once with System Settings → Privacy & Security → **Open Anyway**, or right-click → Open on older macOS).
- **Developer ID + notarization** (macOS public release): sign with a Developer ID Application certificate and the hardened runtime (`--options runtime`, entitlement `com.apple.security.cs.allow-jit` for the .NET JIT), submit with `xcrun notarytool submit --wait`, then `xcrun stapler staple`. Keep the D51 layout so the managed files stay sealed resources. Needs Apple credentials; documented, not performed here (NC-09).
- **Windows:** development builds are unsigned. Public releases need Authenticode signing (`signtool`, with an RFC 3161 timestamp); check SmartScreen reputation on a clean machine (NC-05).

## CI (`.github/workflows/ci.yml`)

- Runs on every branch push and `workflow_dispatch`, on the **private** remote only. Matrix `windows-latest` + `macos-latest` (Apple Silicon).
- Phases per job: build (`dotnet build DialShift.slnx -c Release -warnaserror`) → test (`dotnet run --project DialShift.Tests/DialShift.Tests.csproj -c Release`) → native UI smoke (the `dotnet build DialShift.App -r <rid>` output with `--smoke-test --recovery-test`; Windows with `DIALSHIFT_AUDIO_OUTPUT=dummy`; results uploaded as `smoke-<rid>`) → package (the scripts above) → verify the zip a user downloads (Windows: `Expand-Archive` + `verify-win-package.ps1`; macOS: `verify-mac-app.sh --zip`) → macOS bundle smoke (`unzip` the release zip, run `DialShift.app/Contents/MacOS/DialShift --smoke-test`; `bundle-smoke-<rid>` is uploaded on failure) → upload (`DialShift-win-x64`, `DialShift-osx-arm64-<label>`, 7-day retention).
- Add new checks as steps in the matching phase, not as placeholders.
- CI does not replace a manual Apple Silicon gate or clean-machine installs (acceptance matrix §9: NC-05, NC-07, NC-09, NC-17).

## Checks before shipping (both)

- A second launch must ACTIVATE the existing instance, not merely exit.
- Clean-machine test the downloaded artifacts (the macOS zip extracted by Finder and by `unzip`); smoke-test tray, schedule, settings persistence, and one `http://` stream from the **bundled** macOS app.
