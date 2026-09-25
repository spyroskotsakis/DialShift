---
name: release-packaging
description: Use when building or packaging DialShift release artifacts (macOS .app bundle, Windows publish/zip, installer, CI).
---

# Release packaging

## macOS

- `scripts/build-mac-app.sh` → `dist/DialShift.app`. It exports `PATH="$HOME/.dotnet:$PATH"` — .NET 10 SDK is at `~/.dotnet` on this Mac. Requires Rosetta 2 on Apple Silicon (today's x64 LibVLC build).
- Menu-bar app: `Info.plist` with `LSUIElement=true`, no Dock icon — test quit/reopen/second-instance under that condition.
- Bundle must contain **no VLC dylibs** once the AVPlayer target ships (brief §8): verify with `find dist/DialShift.app -name "*.dylib"`.
- Today: `osx-x64` + Rosetta (LibVLC). Target per `docs/single-codebase-refactor.md`: `osx-arm64` with `MacAvPlayerPlaybackEngine`. **Honest labeling:** never ship arm64 unlabeled.

## Windows

- `scripts/build.ps1` (uses a local SDK at `%LOCALAPPDATA%\DialShift\sdk\dotnet.exe` if present); `scripts/Install.ps1` = per-user install, no admin rights.
- Release artifact is a self-contained folder/zip — keep the whole folder together.

## Checks before shipping (both)

- A second launch must ACTIVATE the existing instance (not merely exit).
- Clean-machine test the artifacts; smoke-test tray, schedule, settings persistence.
- Signing tiers: ad-hoc = local dev only; Developer ID + notarization = public distribution.

## CI

- GitHub Actions, `matrix: os: [windows-latest, macos-latest]` — compile + test + publish both artifacts; manual Apple Silicon gate per brief §4.5.
