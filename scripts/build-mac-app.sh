#!/usr/bin/env bash
# Builds the native Apple Silicon (osx-arm64) DialShift.app and its release zip
# (brief 1 §8; decisions D2, D4, D7).
#
#   dist/DialShift.app                       menu-bar app bundle (LSUIElement), ad-hoc signed
#   dist/DialShift-osx-arm64-<label>.zip     the distributable (ditto keeps permissions + signature)
#
# The bundle carries no VLC libraries: macOS playback uses Apple's AVPlayer through system
# framework linkage. <label> comes from MACOS_LABEL (default "preview"). Honest labeling rule
# (brief 1 §4.3): switch to "native-avplayer" only once MacAvPlayerPlaybackEngine passes the
# SP-01/SP-02 acceptance rows.
#
# Requires: macOS with the .NET 10 SDK (on PATH or at ~/.dotnet) and the built-in
# sips, iconutil, codesign, ditto, lipo, plutil and PlistBuddy tools.
set -euo pipefail
cd "$(dirname "$0")/.."

fail() { echo "error: $*" >&2; exit 1; }

export PATH="$HOME/.dotnet:$PATH"

[ "$(uname -s)" = "Darwin" ] || fail "build-mac-app.sh must run on macOS (it uses sips, iconutil and codesign)."
for tool in dotnet sips iconutil codesign ditto; do
    command -v "$tool" >/dev/null 2>&1 || fail "required tool not found: $tool"
done

CSPROJ="DialShift.App/DialShift.App.csproj"
ICON_SRC="DialShift.App/Assets/icon-512.png"
PUBLISH="publish/osx-arm64"
APP="dist/DialShift.app"
LABEL="${MACOS_LABEL:-preview}"
# Floor of the shipped binaries: the .NET 10 host/runtime is built for macOS 12.0
# (Avalonia/Skia/HarfBuzz natives for 11.0). AVPlayer media behavior is verified on
# macOS 26.5 only (docs/spikes.md); re-run the corpus before claiming older versions.
MIN_MACOS="12.0"

[[ "$LABEL" =~ ^[a-z0-9][a-z0-9-]*$ ]] || fail "MACOS_LABEL must be lowercase letters, digits and dashes (got '$LABEL')."
ZIP="dist/DialShift-osx-arm64-$LABEL.zip"

VERSION="$(awk -F'[<>]' '/<Version>/ { print $3; exit }' "$CSPROJ")"
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "could not read a <Version>x.y.z</Version> from $CSPROJ (got '$VERSION')."

[ -f "$ICON_SRC" ] || fail "missing icon source $ICON_SRC"
icon_w="$(sips -g pixelWidth "$ICON_SRC" | awk '/pixelWidth/ {print $2}')"
icon_h="$(sips -g pixelHeight "$ICON_SRC" | awk '/pixelHeight/ {print $2}')"
[ "$icon_w" = "512" ] && [ "$icon_h" = "512" ] || fail "$ICON_SRC must be 512x512 (got ${icon_w}x${icon_h})."

echo "== Publishing self-contained osx-arm64 build (DialShift $VERSION) =="
rm -rf "$PUBLISH"
dotnet publish "$CSPROJ" -c Release -r osx-arm64 --self-contained \
    -p:DebugType=None -p:DebugSymbols=false -o "$PUBLISH"

echo "== Assembling $APP =="
rm -rf "$APP" "$ZIP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUBLISH/." "$APP/Contents/MacOS/"
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
    <string>$VERSION</string>
    <key>CFBundleShortVersionString</key>
    <string>$VERSION</string>
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

echo "== Zipping $ZIP =="
ditto -c -k --keepParent "$APP" "$ZIP"

echo "== Built $APP and $ZIP (label: $LABEL) =="
