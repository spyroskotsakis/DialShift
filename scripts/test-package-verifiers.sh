#!/usr/bin/env bash
# Runs the package verifiers against the station catalog fixtures of CAT-03 (brief 3; docs/catalog-contracts.md §4.4
# and §8; decisions D81, D82 (c) and (d), D58) and checks each verdict.
#
# Usage: scripts/test-package-verifiers.sh [--mac-app <DialShift.app>] [--win-package <folder>]
#                                          [--powershell <command>] [--no-smoke]
#
#   --mac-app      a verified macOS bundle, for example dist/DialShift.app from scripts/build-mac-app.sh (macOS only).
#   --win-package  a verified win-x64 package folder, for example artifacts/DialShift-win-x64 from scripts/build.ps1,
#                  or the folder extracted from the release zip.
#   --powershell   the PowerShell that runs verify-win-package.ps1. Default: pwsh (PowerShell 7, as CI runs it).
#                  powershell.exe (Windows PowerShell 5.1) runs the script's fallback parser, native check NC-18; there
#                  the invalid UTF-8 fixture is reported as "noted", not checked (D82 (d)).
#   --no-smoke     skip the bundle smoke runs (they open DialShift windows for about a minute each).
#   Without --mac-app and --win-package: dist/DialShift.app and artifacts/DialShift-win-x64, whichever exist.
#
# Each package is copied once to a temporary folder. For every fixture the copy's app-catalog.json is replaced and
# each verifier runs on the copy: scripts/verify-mac-app.sh (after an ad-hoc re-signature, since the catalog is a
# sealed resource of the bundle) and scripts/verify-win-package.ps1. A fixture that must fail passes this test only
# when the verifier exits non-zero with a message about app-catalog.json, so a package broken in some other way does
# not count as a correct rejection. The fixtures the app must reject although both verifiers accept them (D82 (c):
# a quoted number, a number past Int32, an escaped lone surrogate) also run the copied bundle's --smoke-test, whose
# "Catalog loads from the app folder" check must fail with "not valid catalog JSON"; a minimal valid file is the
# control that the check passes on the same re-signed copy.
# Prints one table row per fixture and exits 1 on any mismatch, 2 on a usage or setup error. CI (build.yml) runs it
# after the verify steps: on macOS with --mac-app dist/DialShift.app, on Windows with the folder extracted from the zip.
# Requires bash 3.2 or later; on macOS the tools of verify-mac-app.sh (with /usr/bin/python3) and codesign; for the
# Windows package a PowerShell (pwsh on macOS: a dotnet tool or brew install powershell). On Windows, run it from
# Git Bash (CI's `shell: bash`); paths reach PowerShell through cygpath.
set -euo pipefail

usage() { echo "usage: $0 [--mac-app <DialShift.app>] [--win-package <folder>] [--powershell <command>] [--no-smoke]" >&2; exit 2; }
fail() { echo "error: $*" >&2; exit 2; }

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
MAC_APP=""
WIN_PACKAGE=""
POWERSHELL="pwsh"
SMOKE=true
while [ $# -gt 0 ]; do
    case "$1" in
        --mac-app) [ $# -ge 2 ] || usage; MAC_APP="${2%/}"; shift 2 ;;
        --win-package) [ $# -ge 2 ] || usage; WIN_PACKAGE="${2%/}"; shift 2 ;;
        --powershell) [ $# -ge 2 ] || usage; POWERSHELL="$2"; shift 2 ;;
        --no-smoke) SMOKE=false; shift ;;
        *) usage ;;
    esac
done
if [ -z "$MAC_APP" ] && [ -z "$WIN_PACKAGE" ]; then
    if [ "$(uname -s)" = Darwin ] && [ -d "$ROOT/dist/DialShift.app" ]; then MAC_APP="$ROOT/dist/DialShift.app"; fi
    if [ -d "$ROOT/artifacts/DialShift-win-x64" ]; then WIN_PACKAGE="$ROOT/artifacts/DialShift-win-x64"; fi
    [ -n "$MAC_APP" ] || [ -n "$WIN_PACKAGE" ] \
        || fail "no package to test: build one (scripts/build-mac-app.sh, scripts/build.ps1) or pass --mac-app / --win-package."
fi
if [ -n "$MAC_APP" ]; then
    [ "$(uname -s)" = Darwin ] || fail "--mac-app needs macOS (verify-mac-app.sh and codesign)."
    [ -d "$MAC_APP" ] || fail "bundle not found: $MAC_APP"
    command -v codesign >/dev/null 2>&1 || fail "required tool not found: codesign"
    [ -x /usr/bin/python3 ] || fail "required tool not found: /usr/bin/python3"
fi
if [ -n "$WIN_PACKAGE" ]; then
    [ -d "$WIN_PACKAGE" ] || fail "package folder not found: $WIN_PACKAGE"
    command -v "$POWERSHELL" >/dev/null 2>&1 || fail "PowerShell not found: $POWERSHELL (pass --powershell <command>)"
fi
[ -n "$MAC_APP" ] || SMOKE=false

# A path as the PowerShell of this platform reads it: Windows form under Git Bash, unchanged elsewhere.
native_path() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s\n' "$1"; fi; }
PS_ARGS=(-NoProfile -NonInteractive)
if command -v cygpath >/dev/null 2>&1; then PS_ARGS+=(-ExecutionPolicy Bypass); fi
DESKTOP_POWERSHELL=false
case "$(basename "$POWERSHELL")" in powershell | powershell.exe | powershell.EXE) DESKTOP_POWERSHELL=true ;; esac

TEMP_ROOT="${TMPDIR:-/tmp}"
# The physical path, the one the app reports (macOS's TMPDIR is under a /var link to /private/var).
WORK="$(cd "$(mktemp -d "${TEMP_ROOT%/}/dialshift-verifier-fixtures.XXXXXX")" && pwd -P)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK/fixtures" "$WORK/logs"

# The fixtures: id | verdict of both verifiers (pass or fail) | bundle smoke's catalog check (loads, rejects or -) |
# what the file holds. real-catalog is the package's own app-catalog.json.
FIXTURES=(
    "real-catalog|pass|-|the packaged catalog"
    "minimal-valid|pass|loads|one station with name, country and stream_url"
    "utf8-bom|pass|-|the minimal file after a UTF-8 byte order mark"
    "name-brackets-quote|pass|-|a station name holding ,] { } and an escaped quote"
    "duplicate-key-last-1|pass|-|schema_version 2 then 1 (the last key wins)"
    "stations-number|fail|-|stations [1]"
    "stations-null|fail|-|stations [null]"
    "stations-object-string|fail|-|stations [{}, \"x\"]"
    "stations-array|fail|-|stations [[]]"
    "stations-empty|fail|-|stations []"
    "stations-missing|fail|-|no stations key"
    "stations-object|fail|-|stations {} (not an array)"
    "schema-string|fail|-|schema_version \"1\""
    "schema-true|fail|-|schema_version true"
    "schema-1.0|fail|-|schema_version 1.0"
    "schema-1e0|fail|-|schema_version 1e0"
    "schema-missing|fail|-|no schema_version key"
    "duplicate-key-last-2|fail|-|schema_version 1 then 2 (the last key wins)"
    "line-comment|fail|-|a // comment"
    "block-comment|fail|-|a /* */ comment"
    "trailing-comma-stations|fail|-|a trailing comma in stations"
    "trailing-comma-station|fail|-|a trailing comma in a station"
    "nan|fail|-|votes NaN"
    "infinity|fail|-|votes Infinity"
    "negative-infinity|fail|-|votes -Infinity"
    "leading-zero|fail|-|votes 01"
    "two-values|fail|-|a second object after the root object"
    "root-array|fail|-|a root array"
    "invalid-utf8|fail|-|a 0xFF byte in a station name"
    "quoted-number|pass|rejects|votes \"12\" (app only, D82 (c))"
    "number-past-int32|pass|rejects|votes 2147483648 (app only, D82 (c))"
    "lone-surrogate|pass|rejects|a name with an escaped \\ud800 (app only, D82 (c))"
)

STATION='{"name":"Fixture FM","country":"GR","stream_url":"https://example.com/stream"}'
# Writes a fixture's bytes to stdout. Contents are single-quoted and printed with %s, so no escape is interpreted;
# the only raw bytes (a byte order mark, an invalid UTF-8 byte) are octal escapes in a format string.
fixture_bytes() {
    local s="$STATION"
    case "$1" in
        minimal-valid) printf '%s\n' '{"schema_version":1,"stations":['"$s"']}' ;;
        utf8-bom) printf '\357\273\277%s\n' '{"schema_version":1,"stations":['"$s"']}' ;;
        name-brackets-quote) printf '%s\n' '{"schema_version":1,"stations":[{"name":"Odd ,] { } \" FM","country":"GR","stream_url":"https://example.com/stream"}]}' ;;
        duplicate-key-last-1) printf '%s\n' '{"schema_version":2,"schema_version":1,"stations":['"$s"']}' ;;
        stations-number) printf '%s\n' '{"schema_version":1,"stations":[1]}' ;;
        stations-null) printf '%s\n' '{"schema_version":1,"stations":[null]}' ;;
        stations-object-string) printf '%s\n' '{"schema_version":1,"stations":[{},"x"]}' ;;
        stations-array) printf '%s\n' '{"schema_version":1,"stations":[[]]}' ;;
        stations-empty) printf '%s\n' '{"schema_version":1,"stations":[]}' ;;
        stations-missing) printf '%s\n' '{"schema_version":1}' ;;
        stations-object) printf '%s\n' '{"schema_version":1,"stations":{}}' ;;
        schema-string) printf '%s\n' '{"schema_version":"1","stations":['"$s"']}' ;;
        schema-true) printf '%s\n' '{"schema_version":true,"stations":['"$s"']}' ;;
        schema-1.0) printf '%s\n' '{"schema_version":1.0,"stations":['"$s"']}' ;;
        schema-1e0) printf '%s\n' '{"schema_version":1e0,"stations":['"$s"']}' ;;
        schema-missing) printf '%s\n' '{"stations":['"$s"']}' ;;
        duplicate-key-last-2) printf '%s\n' '{"schema_version":1,"schema_version":2,"stations":['"$s"']}' ;;
        line-comment) printf '%s\n' '// a comment' '{"schema_version":1,"stations":['"$s"']}' ;;
        block-comment) printf '%s\n' '{"schema_version":1,/* a comment */"stations":['"$s"']}' ;;
        trailing-comma-stations) printf '%s\n' '{"schema_version":1,"stations":['"$s"',]}' ;;
        trailing-comma-station) printf '%s\n' '{"schema_version":1,"stations":[{"name":"Fixture FM","country":"GR","stream_url":"https://example.com/stream",}]}' ;;
        nan) printf '%s\n' '{"schema_version":1,"stations":[{"name":"Fixture FM","country":"GR","stream_url":"https://example.com/stream","votes":NaN}]}' ;;
        infinity) printf '%s\n' '{"schema_version":1,"stations":[{"name":"Fixture FM","country":"GR","stream_url":"https://example.com/stream","votes":Infinity}]}' ;;
        negative-infinity) printf '%s\n' '{"schema_version":1,"stations":[{"name":"Fixture FM","country":"GR","stream_url":"https://example.com/stream","votes":-Infinity}]}' ;;
        leading-zero) printf '%s\n' '{"schema_version":1,"stations":[{"name":"Fixture FM","country":"GR","stream_url":"https://example.com/stream","votes":01}]}' ;;
        two-values) printf '%s\n' '{"schema_version":1,"stations":['"$s"']}' '{"schema_version":1,"stations":['"$s"']}' ;;
        root-array) printf '%s\n' '[{"schema_version":1,"stations":['"$s"']}]' ;;
        invalid-utf8) printf '%s\377%s\n' '{"schema_version":1,"stations":[{"name":"Fixture' ' FM","country":"GR","stream_url":"https://example.com/stream"}]}' ;;
        quoted-number) printf '%s\n' '{"schema_version":1,"stations":[{"name":"Fixture FM","country":"GR","stream_url":"https://example.com/stream","votes":"12"}]}' ;;
        number-past-int32) printf '%s\n' '{"schema_version":1,"stations":[{"name":"Fixture FM","country":"GR","stream_url":"https://example.com/stream","votes":2147483648}]}' ;;
        lone-surrogate) printf '%s\n' '{"schema_version":1,"stations":[{"name":"Fixture \ud800 FM","country":"GR","stream_url":"https://example.com/stream"}]}' ;;
        *) echo "error: no content for fixture $1" >&2; return 1 ;;
    esac
}

# The verdict of one verifier run: pass (exit 0 and "verified:"), fail (non-zero exit and a message naming
# app-catalog.json) or ERROR (anything else, for example a package that fails an unrelated check).
verdict() {
    local status="$1" log="$2"
    if [ "$status" -eq 0 ] && grep -q '^verified: ' "$log"; then echo pass
    elif [ "$status" -ne 0 ] && grep -q 'app-catalog\.json' "$log"; then echo fail
    else echo ERROR; fi
}

# The last non-empty line of a log for the messages section, with the temporary copies' paths shortened.
last_line() {
    local line
    line="$(grep -v '^[[:space:]]*$' "$1" | tail -n 1 | tr -s '[:space:]' ' ')"
    if [ -n "$MAC_APP" ]; then line="${line//"$MAC_COPY"/<bundle copy>}"; fi
    if [ -n "$WIN_PACKAGE" ]; then line="${line//"$WIN_COPY_NATIVE"/<package copy>}"; fi
    printf '%s\n' "${line:0:300}"
}

# Copies of the packages, and their own catalogs for the real-catalog fixture.
if [ -n "$MAC_APP" ]; then
    MAC_COPY="$WORK/mac/DialShift.app"
    mkdir -p "$WORK/mac"
    ditto "$MAC_APP" "$MAC_COPY"
    MAC_CATALOG="$MAC_COPY/Contents/Resources/app/app-catalog.json"
    [ -f "$MAC_CATALOG" ] || fail "$MAC_APP has no Contents/Resources/app/app-catalog.json"
    cp "$MAC_CATALOG" "$WORK/fixtures/real-catalog.mac.json"
fi
if [ -n "$WIN_PACKAGE" ]; then
    WIN_COPY="$WORK/win/DialShift-win-x64"
    mkdir -p "$WORK/win"
    cp -R "$WIN_PACKAGE" "$WIN_COPY"
    WIN_COPY_NATIVE="$(native_path "$WIN_COPY")"
    WIN_CATALOG="$WIN_COPY/app-catalog.json"
    [ -f "$WIN_CATALOG" ] || fail "$WIN_PACKAGE has no app-catalog.json"
    cp "$WIN_CATALOG" "$WORK/fixtures/real-catalog.win.json"
fi

run_mac_verifier() {
    local id="$1" file="$2" log="$WORK/logs/$1.mac.log" status=0
    cp "$file" "$MAC_CATALOG"
    # The catalog is a sealed resource: re-sign ad-hoc as scripts/build-mac-app.sh does, so only the catalog differs.
    codesign --force --deep --sign - "$MAC_COPY" >"$log" 2>&1 || { echo ERROR; return; }
    "$BASH" "$ROOT/scripts/verify-mac-app.sh" "$MAC_COPY" >"$log" 2>&1 || status=$?
    verdict "$status" "$log"
}

run_win_verifier() {
    local id="$1" file="$2" log="$WORK/logs/$1.win.log" status=0
    cp "$file" "$WIN_CATALOG"
    "$POWERSHELL" "${PS_ARGS[@]}" -File "$(native_path "$ROOT/scripts/verify-win-package.ps1")" -Path "$(native_path "$WIN_COPY")" \
        >"$log" 2>&1 </dev/null || status=$?
    verdict "$status" "$log"
}

# The bundle smoke on the re-signed copy (its catalog is the fixture): prints loads, rejects or ERROR from the
# "Catalog loads from the app folder" check of results.json; the other checks do not matter here.
run_smoke() {
    local id="$1" output="$WORK/smoke/$1" log="$WORK/logs/$1.smoke.log"
    mkdir -p "$output"
    (
        unset DIALSHIFT_CATALOG_PATH DIALSHIFT_DATA_DIR
        "$MAC_COPY/Contents/MacOS/DialShift" --smoke-test --output "$output"
    ) >/dev/null 2>&1 </dev/null || true
    /usr/bin/python3 - "$output/results.json" >"$log" 2>&1 <<'PY' || true
import json, sys
try:
    with open(sys.argv[1], encoding="utf-8") as f:
        checks = json.load(f)["checks"]
except (OSError, ValueError, KeyError) as e:
    sys.exit("ERROR no results.json: %s" % e)
check = next((c for c in checks if c.get("name") == "Catalog loads from the app folder"), None)
if check is None:
    sys.exit("ERROR no catalog check in results.json")
detail = " ".join(str(check.get("detail", "")).split())
if check.get("passed"):
    print("loads " + detail)
elif "not valid catalog JSON" in detail:
    print("rejects " + detail)
else:
    print("ERROR " + detail)
PY
    head -n 1 "$log" | cut -d' ' -f1
}

echo "Package verifier fixtures (CAT-03; contracts §4.4, §8; D81, D82 (c))"
[ -z "$MAC_APP" ] || echo "  macOS bundle:    $MAC_APP (verify-mac-app.sh; bundle smoke: $SMOKE)"
[ -z "$WIN_PACKAGE" ] || echo "  Windows package: $WIN_PACKAGE (verify-win-package.ps1 under $POWERSHELL)"
echo
# Verifiers: the verdict both must give, then each one's; Smoke exp / Smoke: the catalog check expected, then actual.
ROW='%-24s | %-9s | %-7s | %-7s | %-9s | %-8s | %-8s | %s\n'
# shellcheck disable=SC2059
printf "$ROW" Fixture Verifiers macOS Windows "Smoke exp" Smoke Result File
# shellcheck disable=SC2059
printf "$ROW" ------------------------ --------- ------- ------- --------- -------- -------- ------------------------------
mismatches=0
noted=0
for row in "${FIXTURES[@]}"; do
    IFS='|' read -r id expected smoke_expected description <<<"$row"
    if [ "$id" != real-catalog ]; then fixture_bytes "$id" >"$WORK/fixtures/$id.json"; fi
    mac="n/a"; win="n/a"; smoke="-"
    if [ -n "$MAC_APP" ]; then
        file="$WORK/fixtures/$id.json"; [ "$id" != real-catalog ] || file="$WORK/fixtures/real-catalog.mac.json"
        mac="$(run_mac_verifier "$id" "$file")"
        if $SMOKE && [ "$smoke_expected" != - ]; then smoke="$(run_smoke "$id")"; fi
    fi
    if [ -n "$WIN_PACKAGE" ]; then
        file="$WORK/fixtures/$id.json"; [ "$id" != real-catalog ] || file="$WORK/fixtures/real-catalog.win.json"
        win="$(run_win_verifier "$id" "$file")"
    fi
    result=ok
    if [ -n "$MAC_APP" ] && [ "$mac" != "$expected" ]; then result=MISMATCH; fi
    if [ -n "$WIN_PACKAGE" ] && [ "$win" != "$expected" ]; then
        # NC-18 records, but does not gate, what an invalid UTF-8 byte does under Windows PowerShell 5.1 (D82 (d)).
        if $DESKTOP_POWERSHELL && [ "$id" = invalid-utf8 ]; then [ "$result" != ok ] || result=noted
        else result=MISMATCH; fi
    fi
    if [ "$smoke" != - ] && [ "$smoke" != "$smoke_expected" ]; then result=MISMATCH; fi
    [ "$result" != MISMATCH ] || mismatches=$((mismatches + 1))
    [ "$result" != noted ] || noted=$((noted + 1))
    if [ "$smoke_expected" != - ] && ! $SMOKE; then smoke=skipped; fi
    # shellcheck disable=SC2059
    printf "$ROW" "$id" "$expected" "$mac" "$win" "$smoke_expected" "$smoke" "$result" "$description"
done

echo
echo "Messages (the last line of each run):"
for row in "${FIXTURES[@]}"; do
    IFS='|' read -r id _ smoke_expected _ <<<"$row"
    [ -z "$MAC_APP" ] || echo "  $id [macOS]: $(last_line "$WORK/logs/$id.mac.log")"
    [ -z "$WIN_PACKAGE" ] || echo "  $id [Windows]: $(last_line "$WORK/logs/$id.win.log")"
    [ ! -f "$WORK/logs/$id.smoke.log" ] || echo "  $id [smoke]: $(last_line "$WORK/logs/$id.smoke.log")"
done

echo
total=${#FIXTURES[@]}
if [ "$mismatches" -gt 0 ]; then
    echo "FAILED: $mismatches of $total fixtures did not get the expected verdict."
    exit 1
fi
echo "All $total fixtures got the expected verdict$([ "$noted" -eq 0 ] || echo " ($noted noted, not checked, under Windows PowerShell 5.1)")."
