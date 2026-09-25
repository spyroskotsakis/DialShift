#!/usr/bin/env bash
# Builds the macOS (Avalonia) port of DialShift into dist/DialShift.app.
# Requires: .NET 10 SDK (on PATH), and Rosetta 2 for the x86_64-only libvlc.dylib.
set -euo pipefail
cd "$(dirname "$0")/.."

export PATH="$HOME/.dotnet:$PATH"

echo "== Publishing self-contained osx-x64 build =="
dotnet publish DialShift.App/DialShift.App.csproj -c Release -r osx-x64 --self-contained -o publish/osx-x64

APP="dist/DialShift.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R publish/osx-x64/. "$APP/Contents/MacOS/"

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
    <string>0.1.0</string>
    <key>CFBundleShortVersionString</key>
    <string>0.1.0</string>
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
echo "== Built $APP =="
