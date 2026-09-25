---
name: release-packaging
description: Use when building or packaging DialShift release artifacts (macOS .app bundle and zip, Windows publish/zip, installer, CI, GitHub Releases).
---

# Release packaging

One project, `DialShift.App`, two artifacts: `win-x64` and `osx-arm64`, never `osx-x64` (decision D13, which amends D2; the last Intel/Rosetta build is only at tag `legacy-last-known-good`). The scripts are the single source of the artifact layout; CI runs the same scripts.

| Artifact | Script | Output | Verified by |
|---|---|---|---|
| Windows `win-x64` | `scripts/build.ps1 [-SkipTests] [-Version <semver>]` | `artifacts/DialShift-win-x64/` + `artifacts/DialShift-win-x64.zip` (release asset under the same name) | `scripts/verify-win-package.ps1 -Path <folder>` |
| macOS Apple Silicon `osx-arm64` | `scripts/build-mac-app.sh [--version <semver>] [--build-number <n>]` | `dist/DialShift.app` + `dist/DialShift-osx-arm64-<label>.zip` (release asset `DialShift-macos-arm64.zip`) | `scripts/verify-mac-app.sh <DialShift.app>` and `scripts/verify-mac-app.sh --zip <zip>` |

Versions (D53): the csproj `<Version>` is the only source of `MAJOR.MINOR.PATCH`. `-Version`/`--version` take SemVer without build metadata and may only add a pre-release suffix (for example `0.3.0-rc.1`); a different `MAJOR.MINOR.PATCH` is refused. The value goes to `dotnet publish` as `-p:Version`, so the assembly informational version (DialShift.exe's product version, the first log line) carries it; `build.ps1` checks the product version. `--build-number` is the macOS `CFBundleVersion`, one to three dot-separated integers. Both default to the csproj `<Version>`.

## macOS (`osx-arm64`, native, no VLC)

- `scripts/build-mac-app.sh`: self-contained `osx-arm64` publish into `publish/osx-arm64`, `.app` assembly (layout below), `.icns` from `DialShift.App/Assets/icon-512.png` (`sips` + `iconutil`), `Info.plist`, notices and the macOS license texts in `Contents/Resources`, ad-hoc signature, bundle verification, then the zip and its verification. It exports `PATH="$HOME/.dotnet:$PATH"`; the .NET 10 SDK is at `~/.dotnet` on this Mac. macOS only.
- **Bundle layout (SR-03, D51).** `Contents/MacOS` holds **only Mach-O files**: the `DialShift` apphost, the .NET runtime and native dylibs, `createdump`, plus a `DialShift.dll` symlink to `../Resources/app/DialShift.dll`. Everything else the publish produces (managed `.dll` files, `deps.json`, `runtimeconfig.json`) lives in `Contents/Resources/app`, which has symlinks back to the Mach-O files in `Contents/MacOS` (the .NET host uses the symlink target folder as its application folder). With this layout codesign seals the managed files as resources. Never put a non-Mach-O file in `Contents/MacOS`: codesign would keep its signature in extended attributes, which `unzip` and many archivers drop, and the bundle would stop verifying. The script fails if the publish output has subfolders or links.
- **Zip:** `ditto -c -k --norsrc --noextattr --noacl --keepParent dist/DialShift.app <zip>`: no extended attributes and no AppleDouble `._*` entries; permissions and symlinks are kept.
- Playback is Apple AVPlayer through system framework linkage. The bundle must contain **no VLC dylibs**. The check is case-sensitive: `LibVLCSharp.dll` (managed, compile-time reference) is allowed; `libvlc*`, `*vlc*.dylib` and a `vlc` folder are not.
- `Info.plist`: `CFBundleIdentifier=com.tsiger.dialshift`, `CFBundleName`/`CFBundleDisplayName=DialShift`, `CFBundleExecutable=DialShift`, `CFBundlePackageType=APPL`, `CFBundleIconFile=DialShift.icns`, `LSUIElement=true` (menu-bar app, no Dock icon), `NSHighResolutionCapable`, `LSMinimumSystemVersion=14.0` (decision D22: the binaries load on 12.0, but the AVPlayer format corpus is verified on macOS 26.5 only, so older versions are not claimed), `CFBundleShortVersionString`=MAJOR.MINOR.PATCH, `CFBundleVersion`=build number (D53). Apple allows only integers in both, so a pre-release suffix is dropped from the plist; `release.yml` passes its run number as the build number so a newer release always sorts higher.
- `NSAppTransportSecurity` → `NSAllowsArbitraryLoadsForMedia=true` is **mandatory** (D32): without it AVPlayer inside a bundle rejects every `http://` stream (43 % of the catalog, docs/spikes.md). `dotnet run` and the plain build output do not enforce ATS, so only a bundled run shows the failure. Never use the blanket `NSAllowsArbitraryLoads`.
- **Honest labeling (D36):** the local zip and the CI artifact carry `MACOS_LABEL`, which is `native-avplayer` in `.github/workflows/build.yml` and the script's default (lowercase letters, digits and dashes). The release asset drops it (`DialShift-macos-arm64.zip`, a stable name for `/releases/latest/download/` links); the release notes (`scripts/release-notes.sh`) and the README carry the label instead. The label says what the build is (native arm64, AVPlayer, no LibVLC), not that it is release-ready: the README keeps "ad-hoc signed, not yet notarized or clean-machine tested" until NC-07 and NC-09 pass.
- `scripts/verify-mac-app.sh <DialShift.app>` asserts: the `Info.plist` keys `CFBundleIdentifier`, `CFBundleDisplayName`, `CFBundleExecutable`, `CFBundlePackageType`, `CFBundleIconFile`, `LSUIElement`, `LSMinimumSystemVersion=14.0`, a version string and the ATS media exception (and no blanket `NSAllowsArbitraryLoads`), icon, notices and license texts, the executable is a file with the exec bit, an arm64-only executable, an arm64 slice in every dylib, no VLC natives, only Mach-O files in `Contents/MacOS`, the `Contents/MacOS/DialShift.dll` link, no broken links, no `com.apple.cs.*` signature in extended attributes, no loose `._*` files, `codesign --verify --deep --strict`, and an ad-hoc signature.
- `scripts/verify-mac-app.sh --zip <zip>` rejects `._*`/`__MACOSX` entries, extracts the zip with `ditto -x -k` and with `unzip`, and runs the bundle checks on each copy.
- Smoke-test the **bundled** executable, not `dotnet run`: `dist/DialShift.app/Contents/MacOS/DialShift --smoke-test [--recovery-test] [--output <dir>]`. It uses an isolated temp data folder and volume 0, writes `results.json`, screenshots and the log to `<dir>`, and exits 0 when every check passed or 4 when a check failed or the watchdog fired.
- With `LSUIElement=true` there is no Dock icon: test quit, reopen and second-instance activation under that condition.

## Windows (`win-x64`)

- `scripts/build.ps1 [-SkipTests] [-Version <semver>]`: tests (unless `-SkipTests`), a fresh self-contained `win-x64` publish of `DialShift.App` into `artifacts/DialShift-win-x64` (with `-p:Version`; DialShift.exe's product version is checked against it), `.pdb` files removed, README, `THIRD-PARTY-NOTICES.md`, `Install.ps1` and the Windows license texts added, verification, then the zip (D7: zip first, MSIX/installer later). Uses a local SDK at `%LOCALAPPDATA%\DialShift\sdk\dotnet.exe` when present, else `dotnet` on `PATH`.
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

## CI (`build.yml`, called by `ci.yml` and `release.yml`)

- `.github/workflows/build.yml` (`workflow_call`, read-only `contents` permission) is the one definition of the build. `ci.yml` calls it on every branch push and `workflow_dispatch` (tag pushes do not run CI); `release.yml` calls it for a version tag, with the inputs `ref` (the tag), `version` (the tag without `v`), `build-number` (the run number) and `nuget-cache: false` (restore from nuget.org only). Matrix `windows-latest` + `macos-latest` (Apple Silicon).
- Phases per job: build (`dotnet build DialShift.slnx -c Release -warnaserror`) → test (`dotnet run --project DialShift.Tests/DialShift.Tests.csproj -c Release`) → native UI smoke (the `dotnet build DialShift.App -r <rid>` output with `--smoke-test --recovery-test`; Windows with `DIALSHIFT_AUDIO_OUTPUT=dummy`; results uploaded as `smoke-<rid>`) → package (the scripts above, with `-Version`/`--version`/`--build-number` from the inputs) → verify the zip a user downloads (Windows: `Expand-Archive` + `verify-win-package.ps1`; macOS: `verify-mac-app.sh --zip`) → macOS bundle smoke (`unzip` the release zip, run `DialShift.app/Contents/MacOS/DialShift --smoke-test`; `bundle-smoke-<rid>` is uploaded on failure) → upload (workflow artifacts `DialShift-win-x64` and `DialShift-osx-arm64-<label>`, 7-day retention; `release.yml` renames them to the release asset names).
- Add new checks as steps in the matching phase of `build.yml`, not as placeholders; releases then get them too.
- CI does not replace a manual Apple Silicon gate or clean-machine installs (acceptance matrix §9: NC-05, NC-07, NC-09, NC-17).

## Releases (`release.yml`, D53)

- Trigger: a pushed tag `v*.*.*`, or `workflow_dispatch` with an existing `tag` (to publish a tag again, delete its release first and keep the tag). The workflow names no repository: a tag on `private` (spyroskotsakis/dialshift-dev) is the dry run, the same tag on `origin` (spyroskotsakis/DialShift) is the public release.
- `resolve` (before any build): the tag is `vMAJOR.MINOR.PATCH[-prerelease]`, its `MAJOR.MINOR.PATCH` equals the csproj `<Version>`, and `scripts/release-notes.sh <version>` finds a non-empty `## [<version>]` section in `CHANGELOG.md`. `build`: `build.yml` at the tag. `publish` (the only job with `contents: write`): downloads the verified `DialShift-*` artifacts, renames the macOS zip to `DialShift-macos-arm64.zip`, writes `SHA256SUMS.txt`, and runs `gh release create --verify-tag` with the three assets `DialShift-win-x64.zip`, `DialShift-macos-arm64.zip`, `SHA256SUMS.txt`, and the changelog section plus download, checksum, install and signing notes as the text.
- A `-` suffix (`v0.3.0-rc.1`) makes a pre-release (`--prerelease --latest=false`); `vX.Y.Z` becomes the latest release.
- Steps: bump `<Version>` in `DialShift.App/DialShift.App.csproj`, add the `CHANGELOG.md` section, commit, `git tag -a vX.Y.Z[-rc.N] -m "DialShift X.Y.Z"`, push the tag to `private` and check the release page and checksums. Only the maintainer then pushes `main` + the tag to `origin`, manually (`git push origin main vX.Y.Z`); the hook in `.claude/settings.json` blocks agent pushes to `origin` and `upstream`. Actions must be enabled on the public fork. Full steps: README "Releasing (maintainers)".

## Checks before shipping (both)

- A second launch must ACTIVATE the existing instance, not merely exit.
- Clean-machine test the downloaded artifacts (the macOS zip extracted by Finder and by `unzip`); smoke-test tray, schedule, settings persistence, and one `http://` stream from the **bundled** macOS app.
