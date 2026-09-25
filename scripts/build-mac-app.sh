#!/usr/bin/env bash
# Builds the native Apple Silicon (osx-arm64) DialShift.app and its release zip
# (brief 1 §8; decisions D2 as amended by D13, D4, D7).
#
#   dist/DialShift.app                       menu-bar app bundle (LSUIElement), ad-hoc signed
#   dist/DialShift-osx-arm64-<label>.zip     the distributable (permissions and symlinks kept, no
#                                            extended attributes: the signature lives in the files)
#
# Bundle layout (SR-03): Contents/MacOS holds only Mach-O code (the DialShift apphost, the
# runtime and native dylibs, createdump). Everything else the .NET publish produces (managed
# .dll files, deps.json, runtimeconfig.json) lives in Contents/Resources/app. The apphost
# resolves its DialShift.dll through a symlink in Contents/MacOS, and the .NET host then uses
# the symlink's target folder as the application folder, so Contents/Resources/app carries
# symlinks back to the Mach-O files in Contents/MacOS. codesign then seals the managed files as
# resources and signs only Mach-O code; with non-Mach-O files in Contents/MacOS it would store
# their signatures in extended attributes, which a non-Apple unzip drops.
#
# The bundle carries no VLC libraries: macOS playback uses Apple's AVPlayer through system
# framework linkage. <label> comes from MACOS_LABEL (default "native-avplayer", the same value
# CI sets). Honest labeling rule (brief 1 §4.3): the label says which macOS build this is; the
# native osx-arm64 AVPlayer build earned "native-avplayer" by passing SP-01/SP-02 and the
# native smoke (docs/acceptance-matrix.md).
#
# Usage: scripts/build-mac-app.sh [--version <semver>] [--build-number <n>]
#
#   --version       SemVer without build metadata, for example 0.3.0-rc.1. Its MAJOR.MINOR.PATCH
#                   must equal the csproj <Version>, the single source of the numeric version
#                   (D53): the override only adds a pre-release suffix. It goes to dotnet publish
#                   as -p:Version, so the assembly InformationalVersion carries it.
#                   Default: the csproj <Version>.
#   --build-number  CFBundleVersion: one to three dot-separated integers. The release workflow
#                   passes its run number. Default: the csproj <Version>.
#
# Info.plist versions (D53): CFBundleShortVersionString is MAJOR.MINOR.PATCH (Apple allows only
# integers there, so a pre-release suffix is dropped) and CFBundleVersion is the build number.
#
# Requires: macOS with the .NET 10 SDK (on PATH or at ~/.dotnet) and the built-in
# sips, iconutil, codesign, ditto, lipo, plutil and PlistBuddy tools.
set -euo pipefail
cd "$(dirname "$0")/.."

fail() { echo "error: $*" >&2; exit 1; }

VERSION=""
BUILD_NUMBER=""
while [ $# -gt 0 ]; do
    case "$1" in
        --version) [ $# -ge 2 ] || fail "--version needs a value"; VERSION="$2"; shift 2 ;;
        --build-number) [ $# -ge 2 ] || fail "--build-number needs a value"; BUILD_NUMBER="$2"; shift 2 ;;
        *) fail "unknown argument '$1' (usage: scripts/build-mac-app.sh [--version <semver>] [--build-number <n>])" ;;
    esac
done

export PATH="$HOME/.dotnet:$PATH"

[ "$(uname -s)" = "Darwin" ] || fail "build-mac-app.sh must run on macOS (it uses sips, iconutil and codesign)."
for tool in dotnet sips iconutil codesign ditto lipo; do
    command -v "$tool" >/dev/null 2>&1 || fail "required tool not found: $tool"
done

CSPROJ="DialShift.App/DialShift.App.csproj"
ICON_SRC="DialShift.App/Assets/icon-512.png"
PUBLISH="publish/osx-arm64"
APP="dist/DialShift.app"
LABEL="${MACOS_LABEL:-native-avplayer}"
# Decision D22: macOS 14.0 (upstream precedent). The binaries would load on 12.0 (.NET 10
# runtime floor), but the AVPlayer format corpus is verified on macOS 26.5 only
# (docs/spikes.md), so older versions are not claimed. verify-mac-app.sh checks this value.
MIN_MACOS="14.0"

[[ "$LABEL" =~ ^[a-z0-9][a-z0-9-]*$ ]] || fail "MACOS_LABEL must be lowercase letters, digits and dashes (got '$LABEL')."
ZIP="dist/DialShift-osx-arm64-$LABEL.zip"

CSPROJ_VERSION="$(awk -F'[<>]' '/<Version>/ { print $3; exit }' "$CSPROJ")"
[[ "$CSPROJ_VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "could not read a <Version>x.y.z</Version> from $CSPROJ (got '$CSPROJ_VERSION')."
VERSION="${VERSION:-$CSPROJ_VERSION}"
[[ "$VERSION" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$ ]] \
    || fail "--version must be SemVer MAJOR.MINOR.PATCH[-prerelease] without build metadata (got '$VERSION')."
SHORT_VERSION="${VERSION%%-*}"
[ "$SHORT_VERSION" = "$CSPROJ_VERSION" ] \
    || fail "--version $VERSION does not match the csproj <Version>$CSPROJ_VERSION</Version>; bump the csproj first (D53)."
BUILD_NUMBER="${BUILD_NUMBER:-$SHORT_VERSION}"
[[ "$BUILD_NUMBER" =~ ^[0-9]+(\.[0-9]+){0,2}$ ]] \
    || fail "--build-number must be one to three dot-separated integers (got '$BUILD_NUMBER')."

[ -f "$ICON_SRC" ] || fail "missing icon source $ICON_SRC"
icon_w="$(sips -g pixelWidth "$ICON_SRC" | awk '/pixelWidth/ {print $2}')"
icon_h="$(sips -g pixelHeight "$ICON_SRC" | awk '/pixelHeight/ {print $2}')"
[ "$icon_w" = "512" ] && [ "$icon_h" = "512" ] || fail "$ICON_SRC must be 512x512 (got ${icon_w}x${icon_h})."

echo "== Publishing self-contained osx-arm64 build (DialShift $VERSION, build $BUILD_NUMBER) =="
rm -rf "$PUBLISH"
dotnet publish "$CSPROJ" -c Release -r osx-arm64 --self-contained \
    -p:Version="$VERSION" -p:DebugType=None -p:DebugSymbols=false -o "$PUBLISH"

echo "== Assembling $APP =="
rm -rf "$APP" "$ZIP"
APP_FILES="$APP/Contents/Resources/app"
mkdir -p "$APP/Contents/MacOS" "$APP_FILES"
cp -R "$PUBLISH/." "$APP_FILES/"
[ -z "$(find "$APP_FILES" -mindepth 1 ! -type f)" ] \
    || fail "the publish output has subfolders or links; the bundle layout below expects a flat folder."
for path in "$APP_FILES"/*; do
    name="$(basename "$path")"
    lipo -archs "$path" >/dev/null 2>&1 || continue   # not Mach-O: stays in Resources/app
    mv "$path" "$APP/Contents/MacOS/"
    # The host looks for the runtime and native libraries in the application folder.
    [ "$name" = DialShift ] || ln -s "../../MacOS/$name" "$APP_FILES/$name"
done
ln -s ../Resources/app/DialShift.dll "$APP/Contents/MacOS/DialShift.dll"
chmod +x "$APP/Contents/MacOS/DialShift"

# Third-party notices travel with the binaries: only the texts that apply to the macOS
# package (keep in sync with the "macOS package" section of THIRD-PARTY-NOTICES.md).
# LibVLC-LGPL-2.1.txt covers the managed LibVLCSharp.dll, which ships unused; no VLC runtime does.
MAC_LICENSES=(
    Avalonia-LICENSE.txt
    SkiaSharp-LICENSE.txt SkiaSharp-THIRD-PARTY-NOTICES.txt
    HarfBuzzSharp-LICENSE.txt HarfBuzzSharp-THIRD-PARTY-NOTICES.txt
    MicroCom-LICENSE.txt
    Tmds.DBus-LICENSE.txt
    LibVLC-LGPL-2.1.txt
    NET-LICENSE.txt NET-THIRD-PARTY-NOTICES.txt
)
cp THIRD-PARTY-NOTICES.md "$APP/Contents/Resources/"
mkdir -p "$APP/Contents/Resources/licenses"
for license in "${MAC_LICENSES[@]}"; do
    [ -f "licenses/$license" ] || fail "missing licenses/$license"
    cp "licenses/$license" "$APP/Contents/Resources/licenses/"
done

# Bundle icon (.icns, D4) generated from the 512 px source with macOS built-ins.
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
ICONSET="$WORK/DialShift.iconset"
mkdir -p "$ICONSET"
make_icon() { sips -z "$1" "$1" "$ICON_SRC" --out "$ICONSET/$2" >/dev/null; }
make_icon 16  icon_16x16.png
make_icon 32  icon_16x16@2x.png
make_icon 32  icon_32x32.png
make_icon 64  icon_32x32@2x.png
make_icon 128 icon_128x128.png
make_icon 256 icon_128x128@2x.png
make_icon 256 icon_256x256.png
make_icon 512 icon_256x256@2x.png
cp "$ICON_SRC" "$ICONSET/icon_512x512.png"
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/DialShift.icns"

# NSAllowsArbitraryLoadsForMedia: AVPlayer inside a bundle blocks cleartext http:// streams
# without it (43 % of the catalog; docs/spikes.md). Media only; never NSAllowsArbitraryLoads.
cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleName</key>
    <string>DialShift</string>
    <key>CFBundleDisplayName</key>
    <string>DialShift</string>
    <key>CFBundleIdentifier</key>
    <string>com.tsiger.dialshift</string>
    <key>CFBundleVersion</key>
    <string>$BUILD_NUMBER</string>
    <key>CFBundleShortVersionString</key>
    <string>$SHORT_VERSION</string>
    <key>CFBundleExecutable</key>
    <string>DialShift</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleIconFile</key>
    <string>DialShift.icns</string>
    <key>LSUIElement</key>
    <true/>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>LSMinimumSystemVersion</key>
    <string>$MIN_MACOS</string>
    <key>NSAppTransportSecurity</key>
    <dict>
        <key>NSAllowsArbitraryLoadsForMedia</key>
        <true/>
    </dict>
</dict>
</plist>
EOF

# Ad-hoc signature: development and CI verification only (D7). It is not publicly
# distributable without Gatekeeper warnings. Public releases need Developer ID signing
# with the hardened runtime plus notarization (xcrun notarytool) and stapling; those need
# Apple credentials and are documented in .claude/skills/release-packaging, not performed here.
echo "== Ad-hoc signing (development tier) =="
codesign --force --deep --sign - "$APP"

echo "== Verifying bundle =="
scripts/verify-mac-app.sh "$APP"

# No extended attributes or resource forks: nothing in the bundle needs them (the verifier
# rejects signatures stored in extended attributes), and without them the zip has no AppleDouble
# ._* entries that non-Apple extractors would leave behind as loose files.
echo "== Zipping $ZIP =="
ditto -c -k --norsrc --noextattr --noacl --keepParent "$APP" "$ZIP"

echo "== Verifying $ZIP (extracted with ditto and with unzip) =="
scripts/verify-mac-app.sh --zip "$ZIP"

echo "== Built $APP and $ZIP (version $VERSION, build $BUILD_NUMBER, label $LABEL) =="
