#!/usr/bin/env bash
# Builds the native Apple Silicon (osx-arm64) DialShift.App into dist/DialShift.app (decision D2).
# The bundle carries no VLC libraries: macOS playback uses AVPlayer (engine pending).
# Requires: .NET 10 SDK (on PATH).
set -euo pipefail
cd "$(dirname "$0")/.."

export PATH="$HOME/.dotnet:$PATH"

echo "== Publishing self-contained osx-arm64 build =="
rm -rf publish/osx-arm64
dotnet publish DialShift.App/DialShift.App.csproj -c Release -r osx-arm64 --self-contained -o publish/osx-arm64

APP="dist/DialShift.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R publish/osx-arm64/. "$APP/Contents/MacOS/"

# LibVLCSharp.dll (managed, compile-time reference) is expected; VLC native libraries and plugins are not.
if find "$APP" \( -name 'libvlc*' -o -name 'vlc' -type d \) | grep -q .; then
    echo "error: VLC native libraries found in $APP; the macOS bundle must not ship VLC." >&2
    exit 1
fi

cat > "$APP/Contents/Info.plist" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>DialShift</string>
    <key>CFBundleDisplayName</key>
    <string>DialShift</string>
    <key>CFBundleIdentifier</key>
    <string>com.tsiger.dialshift</string>
    <key>CFBundleVersion</key>
    <string>0.3.0</string>
    <key>CFBundleShortVersionString</key>
    <string>0.3.0</string>
    <key>CFBundleExecutable</key>
    <string>DialShift</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>LSUIElement</key>
    <true/>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>LSMinimumSystemVersion</key>
    <string>11.0</string>
</dict>
</plist>
EOF

chmod +x "$APP/Contents/MacOS/DialShift"

if [ "$(/usr/libexec/PlistBuddy -c 'Print :LSUIElement' "$APP/Contents/Info.plist")" != "true" ]; then
    echo "error: Info.plist must set LSUIElement=true (menu-bar app, no Dock icon)." >&2
    exit 1
fi
echo "== Built $APP =="
