#!/usr/bin/env bash
# Verifies an assembled DialShift.app against the macOS packaging rules
# (brief 1 §8; decisions D2, D4, D7; acceptance rows PK-02, PK-03, PK-04, HS-15).
#
# Usage: scripts/verify-mac-app.sh path/to/DialShift.app
#
# scripts/build-mac-app.sh runs it on the bundle before zipping. CI runs it again on the
# bundle extracted from the release zip, so the checks cover what a user downloads.
# macOS built-in tools only (lipo, codesign, plutil, PlistBuddy).
set -euo pipefail

fail() { echo "error: $*" >&2; exit 1; }

[ "$#" -eq 1 ] || fail "usage: $0 path/to/DialShift.app"
APP="${1%/}"
[ -d "$APP" ] || fail "bundle not found: $APP"

PLIST="$APP/Contents/Info.plist"
EXE="$APP/Contents/MacOS/DialShift"
PLISTBUDDY=/usr/libexec/PlistBuddy

for tool in lipo codesign plutil "$PLISTBUDDY"; do
    command -v "$tool" >/dev/null 2>&1 || fail "required macOS tool not found: $tool"
done

[ -f "$PLIST" ] || fail "missing $PLIST"
plutil -lint -s "$PLIST" || fail "Info.plist is not a valid property list"

plist_value() { "$PLISTBUDDY" -c "Print :$1" "$PLIST" 2>/dev/null || true; }
expect_plist() {
    local actual
    actual="$(plist_value "$1")"
    [ "$actual" = "$2" ] || fail "Info.plist $1 is '${actual:-<missing>}', expected '$2'"
}

expect_plist CFBundleIdentifier com.tsiger.dialshift
expect_plist CFBundleDisplayName DialShift
expect_plist CFBundleExecutable DialShift
expect_plist CFBundlePackageType APPL
expect_plist CFBundleIconFile DialShift.icns
# Menu-bar app: no Dock icon (brief 1 §8 tray-first behavior).
expect_plist LSUIElement true
# AVPlayer inside a bundle refuses cleartext http:// media without this key (docs/spikes.md).
# Only the media exception is allowed; blanket NSAllowsArbitraryLoads is rejected.
expect_plist NSAppTransportSecurity:NSAllowsArbitraryLoadsForMedia true
[ -z "$(plist_value NSAppTransportSecurity:NSAllowsArbitraryLoads)" ] \
    || fail "Info.plist must not set NSAppTransportSecurity:NSAllowsArbitraryLoads (only the media exception)"
[ -n "$(plist_value CFBundleShortVersionString)" ] || fail "Info.plist CFBundleShortVersionString is missing"
# Decision D22: macOS 14.0 minimum (AVPlayer corpus not verified on older versions).
expect_plist LSMinimumSystemVersion 14.0

[ -s "$APP/Contents/Resources/DialShift.icns" ] || fail "missing bundle icon Contents/Resources/DialShift.icns"
[ -s "$APP/Contents/Resources/THIRD-PARTY-NOTICES.md" ] || fail "missing Contents/Resources/THIRD-PARTY-NOTICES.md"
[ -s "$APP/Contents/Resources/licenses/Avalonia-LICENSE.txt" ] || fail "missing third-party license texts in Contents/Resources/licenses"

[ -f "$EXE" ] || fail "missing executable $EXE"
[ -x "$EXE" ] || fail "executable bit not set on $EXE"
# No VLC native runtime on macOS (PK-02). Case-sensitive on purpose: the managed
# LibVLCSharp.dll (compile-time reference) is allowed; libvlc*/VLC dylibs/plugins are not.
vlc="$(find "$APP" \( -name 'libvlc*' -o -name '*vlc*.dylib' -o \( -name 'vlc' -type d \) \))"
[ -z "$vlc" ] || fail "VLC native files found in the macOS bundle (it must not ship VLC):"$'\n'"$vlc"

archs="$(lipo -archs "$EXE" 2>/dev/null)" || fail "$EXE is not a Mach-O executable"
[ "$archs" = "arm64" ] || fail "$EXE architectures are '$archs', expected exactly 'arm64' (D2)"

# Every native library must carry an arm64 slice (universal is fine; the arm64 process can't load an x86_64-only one).
while IFS= read -r -d '' lib; do
    lib_archs="$(lipo -archs "$lib" 2>/dev/null)" || fail "$lib is not a Mach-O library"
    [[ " $lib_archs " == *" arm64 "* ]] || fail "no arm64 slice in $lib (has: $lib_archs)"
done < <(find "$APP" -name '*.dylib' -print0)

codesign --verify --deep --strict "$APP" || fail "codesign verification failed for $APP"
# Captured first: grep -q closing the pipe early would trip pipefail.
signature="$(codesign -dv "$APP" 2>&1)"
grep -qx 'Signature=adhoc' <<<"$signature" || fail "expected an ad-hoc signature (D7 development tier)"

echo "verified: $APP (arm64, LSUIElement, ATS media exception, icon, no VLC, ad-hoc signature)"
