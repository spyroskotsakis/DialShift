#!/usr/bin/env bash
# Verifies an assembled DialShift.app, or the release zip, against the macOS packaging rules
# (brief 1 §8; decisions D2 as amended by D13, D4, D7; acceptance rows PK-02, PK-03, PK-04,
# HS-15, SR-03).
#
# Usage: scripts/verify-mac-app.sh path/to/DialShift.app
#        scripts/verify-mac-app.sh --zip path/to/DialShift-osx-arm64-<label>.zip
#
# With --zip it checks that the zip has no AppleDouble ._* entries, extracts it twice, with
# ditto (Finder, Safari) and with unzip (a non-Apple extractor that drops extended
# attributes), and verifies each extracted bundle, so the checks cover what a user downloads.
# scripts/build-mac-app.sh runs both modes; CI runs the --zip mode again on the uploaded zip.
# macOS built-in tools only (lipo, codesign, plutil, PlistBuddy, ditto, unzip, zipinfo).
set -euo pipefail

fail() { echo "error: $*" >&2; exit 1; }

if [ "$#" -eq 2 ] && [ "$1" = "--zip" ]; then
    ZIP="$2"
    [ -f "$ZIP" ] || fail "zip not found: $ZIP"
    for tool in ditto unzip zipinfo; do
        command -v "$tool" >/dev/null 2>&1 || fail "required macOS tool not found: $tool"
    done
    # Captured first: grep closing the pipe early would trip pipefail.
    entries="$(zipinfo -1 "$ZIP")"
    appledouble="$(grep -E '(^|/)(\._|__MACOSX/)' <<<"$entries" || true)"
    [ -z "$appledouble" ] || fail "$ZIP has AppleDouble entries (extended attributes a non-Apple extractor leaves as loose files):"$'\n'"$(head -5 <<<"$appledouble")"
    work="$(mktemp -d)"
    trap 'rm -rf "$work"' EXIT
    ditto -x -k "$ZIP" "$work/ditto"
    unzip -q "$ZIP" -d "$work/unzip"
    for extractor in ditto unzip; do
        "$BASH" "$0" "$work/$extractor/DialShift.app" || fail "the bundle extracted from $ZIP with $extractor failed verification"
    done
    echo "verified: $ZIP (no AppleDouble entries; the bundle verifies after ditto and after unzip extraction)"
    exit 0
fi

[ "$#" -eq 1 ] || fail "usage: $0 path/to/DialShift.app | $0 --zip path/to/DialShift-osx-arm64-<label>.zip"
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

[ -f "$EXE" ] && [ ! -L "$EXE" ] || fail "missing executable $EXE (it must be a file, not a link)"
[ -x "$EXE" ] || fail "executable bit not set on $EXE"
# No VLC native runtime on macOS (PK-02). Case-sensitive on purpose: the managed
# LibVLCSharp.dll (compile-time reference) is allowed; libvlc*/VLC dylibs/plugins are not.
vlc="$(find "$APP" \( -name 'libvlc*' -o -name '*vlc*.dylib' -o \( -name 'vlc' -type d \) \))"
[ -z "$vlc" ] || fail "VLC native files found in the macOS bundle (it must not ship VLC):"$'\n'"$vlc"

archs="$(lipo -archs "$EXE" 2>/dev/null)" || fail "$EXE is not a Mach-O executable"
[ "$archs" = "arm64" ] || fail "$EXE architectures are '$archs', expected exactly 'arm64' (D2, D13)"

# Every native library must carry an arm64 slice (universal is fine; the arm64 process can't load an x86_64-only one).
while IFS= read -r -d '' lib; do
    lib_archs="$(lipo -archs "$lib" 2>/dev/null)" || fail "$lib is not a Mach-O library"
    [[ " $lib_archs " == *" arm64 "* ]] || fail "no arm64 slice in $lib (has: $lib_archs)"
done < <(find "$APP" -name '*.dylib' -print0)

# SR-03: Contents/MacOS holds only Mach-O files (links to Contents/Resources/app are fine).
# codesign stores the signature of any other file there in extended attributes, which a
# non-Apple extractor drops; with this layout the managed files are sealed as resources.
while IFS= read -r -d '' file; do
    lipo -archs "$file" >/dev/null 2>&1 || fail "non-Mach-O file in Contents/MacOS: $file (it belongs in Contents/Resources/app)"
done < <(find "$APP/Contents/MacOS" -type f -print0)
while IFS= read -r -d '' link; do
    [ -e "$link" ] || fail "broken link in the bundle: $link -> $(readlink "$link")"
done < <(find "$APP" -type l -print0)
[ -e "$APP/Contents/MacOS/DialShift.dll" ] || fail "missing Contents/MacOS/DialShift.dll (the apphost's link to Contents/Resources/app)"
signed_xattrs="$(find "$APP" -xattrname com.apple.cs.CodeSignature)"
[ -z "$signed_xattrs" ] || fail "signatures stored in extended attributes (lost by non-Apple extractors):"$'\n'"$signed_xattrs"
loose="$(find "$APP" -name '._*')"
[ -z "$loose" ] || fail "loose AppleDouble files in the bundle:"$'\n'"$loose"

codesign --verify --deep --strict "$APP" || fail "codesign verification failed for $APP"
# Captured first: grep -q closing the pipe early would trip pipefail.
signature="$(codesign -dv "$APP" 2>&1)"
grep -qx 'Signature=adhoc' <<<"$signature" || fail "expected an ad-hoc signature (D7 development tier)"

echo "verified: $APP (arm64, Mach-O-only Contents/MacOS, LSUIElement, ATS media exception, icon, no VLC, ad-hoc signature)"
