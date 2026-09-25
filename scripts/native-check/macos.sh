#!/usr/bin/env bash
# DialShift native-check kit for macOS. It guides you through the native checks in docs/acceptance-matrix.md §9
# that need a real Mac and a person, and collects one evidence bundle to hand back (docs/open-items.md).
#
# Covers NX-01 (NC-17 step 10), NC-12, NC-17 steps 1-9, NC-10 with the LaunchAgent half of NC-13, NC-11, NC-08,
# NC-07 and NC-16. For each check it prints the procedure and pass criteria from matrix §9, automates what a script
# can do, asks you to do the physical steps and give a PASS/FAIL/SKIP verdict with a note, and records everything in
#   ~/Desktop/native-evidence-<host>-<yyyymmdd>/   summary.md, summary.json, machine.txt, one folder per check
# which it zips when you finish. It is safe to re-run: checks already recorded are skipped, and NC-10 resumes
# after each log out and log in. It never touches your real DialShift data without asking first, and it moves
# the originals aside so they can be restored.
#
# Usage: scripts/native-check/macos.sh [--app <DialShift.app> | --zip <DialShift-osx-arm64-*.zip> | --run <CI run id>]
#                                      [--evidence <dir>]
#   --app       test this bundle
#   --zip       extract this CI zip with ditto and with unzip, then test the ditto copy
#   --run       download this CI run's macOS artifact with the GitHub CLI (gh), then continue as with --zip
#   --evidence  the evidence folder to create or resume (default: the last one, or a new one on the Desktop)
#
# Uses only tools built into macOS (no developer tools, no Python). Compatible with bash 3.2.
# See scripts/native-check/README.md.
set -euo pipefail

[ "$(uname -s)" = Darwin ] || { echo "error: this kit runs on macOS only (use windows.ps1 on Windows)" >&2; exit 1; }

# ---------------------------------------------------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------------------------------------------------
KIT_VERSION=1
KIT_PATH="$(cd "$(dirname "$0")" && pwd)/$(basename "$0")"
WORK_ROOT="$HOME/DialShift-native-check"   # extracted apps, isolated data folders, backups; never part of the bundle
MANIFEST="$WORK_ROOT/backup/manifest"      # exists while real DialShift files are moved aside
REAL_DATA="$HOME/Library/Application Support/DialShift"
AGENT_LABEL=com.tsiger.dialshift
AGENT_PLIST="$HOME/Library/LaunchAgents/$AGENT_LABEL.plist"
LEGACY_AGENT_PLIST="$HOME/Library/LaunchAgents/com.dialshift.radio.plist"   # upstream v0.2.0's entry; DialShift may delete it
INSTALLED_APP=/Applications/DialShift.app
CI_REPO=spyroskotsakis/dialshift-dev
MAC_ARTIFACT=DialShift-osx-arm64-native-avplayer
CHECK_ORDER="NX-01 NC-12 NC-17 NC-10 NC-13 NC-11 NC-08 NC-07 NC-16"   # docs/open-items.md §3

# The AVPlayer corpus (docs/spikes.md): id|url|what|expected outcome|class.
# Class "required" must match; "optional" is a format that goes into the README "Formats" line if it fails (NC-16);
# "info" is recorded only.
CORPUS_FORMATS='C1|https://ice1.somafm.com/groovesalad-128-mp3|MP3, HTTPS Icecast|Playing|required
C2|https://ice5.somafm.com/groovesalad-128-aac|AAC (ADTS), HTTPS Icecast|Playing|required
C3|https://a.files.bbci.co.uk/ms6/live/3441A116-B12E-4D2F-ACA8-C1984642FA4B/audio/simulcast/hls/nonuk/pc_hd_abr_v2/ak/bbc_world_service.m3u8|HLS AAC, HTTPS|Playing|required
C4|https://stream.radiofrance.fr/franceinterlamusiqueinter/franceinterlamusiqueinter_hifi.m3u8?id=radiofrance|HLS AAC, HTTPS with a query|Playing|required
C5|https://stream.radios.bzh/hls/boa/aac_hifi.m3u8|HLS AAC, HTTPS|Playing|required
C6|http://radiorecord.hostingradio.ru/deep96.aacp|HE-AAC (.aacp), cleartext HTTP|Playing|optional
C7|https://icecast.radiofrance.fr/fip-hifi.aac|AAC, HTTPS Icecast|Playing|required
C8|http://stream.power-radio.de:8020/listen.pls|MP3 served at a .pls path|UnsupportedFormat|info
C9|http://france16.coollabel-productions.com:8276/;|MP3, Shoutcast v2, cleartext HTTP|Playing|required
C10|https://radio.ekodesgarrigues.com/eko-des-garrigues-256k.ogg|Ogg Vorbis|Playing|optional
C11|https://st02.sslstream.dlf.de/dlf/02/low/opus/stream.opus?aggregator=web|Opus, HTTPS 302|Playing|optional
C12|https://onair.net-radio.fr/frequence3dance.flac|FLAC in Ogg|Playing|optional
C13|https://streams.br.de/br-klassik_3.m3u|M3U playlist file|Playing|required
C14|https://somafm.com/groovesalad.pls|PLS playlist file|Playing|required
C15|https://st01.sslstream.dlf.de/dlf/01/128/mp3/stream.mp3?aggregator=web|MP3, HTTPS 302 with a token|Playing|required'
CORPUS_FAILURES='T1|http://127.0.0.1:1/unavailable|connection refused|NetworkUnavailable|info
T2|https://stream.nonexistent.invalid/radio.mp3|DNS failure|NetworkUnavailable|info
T3|https://ice5.somafm.com/does-not-exist-xyz|HTTP 404|HttpError|info
T5a|https://expired.badssl.com/|expired certificate|TlsFailure|info
T5b|https://self-signed.badssl.com/|self-signed certificate|TlsFailure|info
T5c|https://wrong.host.badssl.com/|wrong host name|TlsFailure|info
T5d|https://untrusted-root.badssl.com/|untrusted root|TlsFailure|info
T6|http://st01.dlf.de/dlf/01/128/mp3/stream.mp3|redirect, http to http|Playing|info
T9|https://example.com/|HTTPS HTML page (captive-portal style)|UnsupportedFormat|info'

# Mutable state of this run.
EVID=""                 # evidence bundle folder
APP=""                  # the DialShift.app under test
CI_RUN=""               # CI run id of the build under test, if known
CUR_ID="" CUR_DIR=""    # the check being recorded and its evidence folder
PRECOND_FAILED=""       # set when you chose to continue although a precondition was not met
EXCERPT=""              # the last log excerpt saved by save_excerpt
MACHINE_JSON=""         # cached machine description
SHOTS_OK=""             # yes/no once you have answered the screenshot question
CAFFEINATE_PID=""       # keeps the Mac awake (not the displays) during NX-01
WIFI_OFF=""             # the Wi-Fi device the kit turned off, so the exit handler turns it back on
OPT_APP="" OPT_ZIP="" OPT_RUN="" OPT_EVID=""

# ---------------------------------------------------------------------------------------------------------------------
# Output and prompts
# ---------------------------------------------------------------------------------------------------------------------
if [ -t 1 ]; then BOLD=$'\033[1m'; YELLOW=$'\033[33m'; RESET=$'\033[0m'; else BOLD=""; YELLOW=""; RESET=""; fi

say() { printf '%s\n' "$*"; }
title() { printf '\n%s== %s ==%s\n' "$BOLD" "$*" "$RESET"; }
die() { printf 'error: %s\n' "$*" >&2; exit 1; }
term_width() {
    local w
    w=$(tput cols 2>/dev/null || true)
    case $w in '' | *[!0-9]*) w=100 ;; esac
    if [ "$w" -gt 120 ]; then w=120; fi
    printf '%s' "$w"
}
wrap() { fold -s -w "$(term_width)"; }
warn() { printf '%sWARNING:%s %s\n' "$YELLOW" "$RESET" "$*" | wrap; }
redact() { sed -e "s#$HOME#~#g"; }   # home folder → ~ in anything the kit writes
tilde() { printf '%s' "$1" | redact; }

pause() { local _reply; read -r -p "${1:-Press Enter to continue.} " _reply || die "input closed"; }
ask_line() { local reply; read -r -p "$1 " reply || die "input closed"; printf '%s' "$reply"; }
ask_yn() {   # prompt [default y|n]; returns 0 for yes
    local reply hint="y/N"
    if [ "${2:-n}" = y ]; then hint="Y/n"; fi
    while true; do
        read -r -p "$1 [$hint] " reply || die "input closed"
        case ${reply:-${2:-n}} in [Yy]*) return 0 ;; [Nn]*) return 1 ;; esac
    done
}
# Prints an instruction for a physical step, then waits for Enter.
do_step() { printf '\n%s\n' "$1" | wrap; pause "${2:-Press Enter when done.}"; }
countdown() {   # seconds label
    local n=$1
    while [ "$n" -gt 0 ]; do printf '\r%s %4ds ' "$2" "$n"; sleep 1; n=$((n - 1)); done
    printf '\r%s done.       \n' "$2"
}
pf() { if "$@" >/dev/null 2>&1; then echo PASS; else echo FAIL; fi; }   # runs a test, prints PASS or FAIL
in_range() { [ "$1" -ge "$2" ] && [ "$1" -le "$3" ]; }
yes_no_of() { if "$@" >/dev/null 2>&1; then echo yes; else echo no; fi; }

# ---------------------------------------------------------------------------------------------------------------------
# JSON and Markdown text
# ---------------------------------------------------------------------------------------------------------------------
one_line() { printf '%s' "$1" | LC_ALL=C tr '\t\r\n' '   ' | LC_ALL=C tr -d '\000-\037'; }
json_str() { printf '"%s"' "$(one_line "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g')"; }
json_lines_array() {   # file with one string per line → JSON array (duplicates dropped)
    local line first=1
    printf '['
    if [ -f "$1" ]; then
        while IFS= read -r line; do
            [ -n "$line" ] || continue
            if [ $first -eq 0 ]; then printf ','; fi
            first=0
            json_str "$line"
        done < <(awk '!seen[$0]++' "$1")
    fi
    printf ']'
}
md_cell() { one_line "$1" | sed -e 's/|/\\|/g'; }

# ---------------------------------------------------------------------------------------------------------------------
# Evidence bundle: folder, state, per-check steps and records, summary.md / summary.json, zip
# ---------------------------------------------------------------------------------------------------------------------
host_name() {
    local h
    h=$(scutil --get LocalHostName 2>/dev/null || hostname -s)
    printf '%s' "$h" | LC_ALL=C tr -c 'A-Za-z0-9-' '-'
}

init_evidence() {
    local prev base
    if [ -n "$OPT_EVID" ]; then
        EVID=$OPT_EVID
    elif [ -f "$WORK_ROOT/current-evidence" ]; then
        prev=$(cat "$WORK_ROOT/current-evidence")
        if [ -d "$prev" ] && ask_yn "Resume the evidence bundle $(tilde "$prev")?" y; then EVID=$prev; fi
    fi
    if [ -z "$EVID" ]; then
        base="$HOME/Desktop"
        [ -d "$base" ] || base=$HOME
        EVID="$base/native-evidence-$(host_name)-$(date +%Y%m%d)"
    fi
    mkdir -p "$EVID/records" "$EVID/state"
    EVID=$(cd "$EVID" && pwd)
    printf '%s\n' "$EVID" > "$WORK_ROOT/current-evidence"
    say "Evidence bundle: $(tilde "$EVID")"
}

state_get() { if [ -f "$EVID/state/$1" ]; then cat "$EVID/state/$1"; fi; }
state_set() { printf '%s\n' "$2" > "$EVID/state/$1"; }

check_title() {
    case $1 in
        NX-01) echo "No active display at startup (NC-17 step 10)" ;;
        NC-07) echo "Clean Apple Silicon Mac: download, Gatekeeper, first launch" ;;
        NC-08) echo "MacBook lid-close wake, and the no-display start (step 5)" ;;
        NC-10) echo "LaunchAgent at a real login, Login Items" ;;
        NC-11) echo "AVPlayer under real network faults" ;;
        NC-12) echo "Menu-bar template icon" ;;
        NC-13) echo "Socket mode under the LaunchAgent (runs inside NC-10)" ;;
        NC-16) echo "AVPlayer format corpus on macOS 14" ;;
        NC-17) echo "Native UI pass of the bundled app (steps 1-9)" ;;
    esac
}

record_field() { grep -o "\"$2\":\"[^\"]*\"" "$EVID/records/$1.json" 2>/dev/null | head -n 1 | cut -d'"' -f4 || true; }
in_progress() { [ -f "$EVID/$1/phase" ]; }
check_status() {
    if in_progress "$1"; then
        printf 'in progress (after log-in %s)' "$(cat "$EVID/$1/phase")"
    elif [ -f "$EVID/records/$1.json" ]; then
        printf '%s  %s' "$(record_field "$1" result)" "$(record_field "$1" timestampUtc)"
    else
        printf 'pending'
    fi
}

begin_check() {   # id [resume]
    CUR_ID=$1
    CUR_DIR="$EVID/$1"
    PRECOND_FAILED=""
    mkdir -p "$CUR_DIR"
    if [ "${2:-}" != resume ]; then : > "$CUR_DIR/steps.tsv"; : > "$CUR_DIR/artifacts.txt"; fi
    title "$1: $(check_title "$1")"
}

add_artifact() { printf '%s\n' "$1" >> "$CUR_DIR/artifacts.txt"; }
add_step() {   # kind(auto|manual) result(PASS|FAIL|SKIP|INFO) label note
    printf '%s\t%s\t%s\t%s\n' "$1" "$2" "$(one_line "$3")" "$(one_line "${4:-}")" >> "$CUR_DIR/steps.tsv"
    printf '   -> %s (%s) %s%s\n' "$2" "$1" "$3" "${4:+: $4}"
}
auto_step() { add_step auto "$2" "$1" "${3:-}"; }   # label result detail
ask_step() {   # label: asks for PASS/FAIL/SKIP and a note
    local reply result note
    while true; do
        read -r -p "   Result for \"$1\": [p]ass, [f]ail or [s]kip? " reply || die "input closed"
        case $reply in [Pp]*) result=PASS; break ;; [Ff]*) result=FAIL; break ;; [Ss]*) result=SKIP; break ;; esac
    done
    note=$(ask_line "   Note (optional, Enter for none):")
    add_step manual "$result" "$1" "$note"
}
count_steps() { awk -F'\t' -v r="$1" '$2 == r { n++ } END { print n + 0 }' "$CUR_DIR/steps.tsv"; }

# Saves a command's output (home folder redacted) as an artifact of the current check. Never fails.
save_cmd() {   # file command...
    local file=$1
    shift
    { printf '$ %s\n' "$*"; "$@" 2>&1 || printf '(exit %s)\n' "$?"; } | redact > "$CUR_DIR/$file"
    add_artifact "$CUR_ID/$file"
}

finish_check() {
    local fails passes skips computed reply result note
    fails=$(count_steps FAIL)
    passes=$(count_steps PASS)
    skips=$(count_steps SKIP)
    if [ "$fails" -gt 0 ]; then computed=FAIL
    elif [ "$passes" -eq 0 ]; then computed=SKIP
    elif [ "$skips" -gt 0 ]; then computed=PARTIAL
    else computed=PASS; fi
    if [ -n "$PRECOND_FAILED" ]; then computed=SKIP; fi
    title "$CUR_ID: steps recorded"
    awk -F'\t' '{ printf "  %-7s %-6s %s%s\n", $2, $1, $3, ($4 == "" ? "" : ": " $4) }' "$CUR_DIR/steps.tsv"
    while true; do
        read -r -p "Overall result for $CUR_ID [Enter = $computed, or pass/fail/partial/skip]: " reply || die "input closed"
        case $reply in
            '') result=$computed; break ;;
            [Pp][Aa][Rr]*) result=PARTIAL; break ;;
            [Pp]*) result=PASS; break ;;
            [Ff]*) result=FAIL; break ;;
            [Ss]*) result=SKIP; break ;;
        esac
    done
    note=$(ask_line "Overall note (what failed, why skipped, anything unusual; Enter for none):")
    if [ -n "$PRECOND_FAILED" ]; then note="Precondition not met: $PRECOND_FAILED. $note"; fi
    write_record "$CUR_ID" "$result" "$note"
    write_summary
    say "Recorded $CUR_ID as $result."
}

write_record() {   # id result note
    local id=$1 ts dir="$EVID/$1"
    ts=$(date -u +%Y-%m-%dT%H:%M:%SZ)
    {
        printf '{"checkId":%s,"title":%s,"result":%s,"timestampUtc":%s,' \
            "$(json_str "$id")" "$(json_str "$(check_title "$id")")" "$(json_str "$2")" "$(json_str "$ts")"
        printf '"machine":%s,"build":%s,"notes":%s,"artifacts":' "$(machine_json)" "$(build_json)" "$(json_str "$3")"
        json_lines_array "$dir/artifacts.txt"
        printf ',"steps":['
        awk -F'\t' '{ print $1; print $2; print $3; print $4 }' "$dir/steps.tsv" | {
            local kind result label note first=1
            while IFS= read -r kind && IFS= read -r result && IFS= read -r label && IFS= read -r note; do
                if [ $first -eq 0 ]; then printf ','; fi
                first=0
                printf '{"step":%s,"kind":%s,"result":%s,"note":%s}' \
                    "$(json_str "$label")" "$(json_str "$kind")" "$(json_str "$result")" "$(json_str "$note")"
            done
        }
        printf ']}\n'
    } > "$EVID/records/$id.json"
    printf '| %s | %s | %s | %s | %s | %s |\n' "$id" "$2" "$ts" "$(md_cell "$(machine_short)")" "$(md_cell "$3")" \
        "$(md_cell "$(awk '!seen[$0]++' "$dir/artifacts.txt" | tr '\n' ' ')")" > "$EVID/records/$id.md-row"
    {
        printf '### %s: %s\n\nResult **%s** at %s. Build: %s.\n\n| Step | Kind | Result | Note |\n|---|---|---|---|\n' \
            "$id" "$(check_title "$id")" "$2" "$ts" "$(md_cell "$(build_short)")"
        awk -F'\t' '{ gsub(/\|/, "\\|"); printf "| %s | %s | %s | %s |\n", $3, $1, $2, $4 }' "$dir/steps.tsv"
        printf '\n'
    } > "$EVID/records/$id.md-steps"
}

write_summary() {
    local id first=1 pending=""
    {
        printf '{"schemaVersion":1,"kit":"macos.sh","kitVersion":%s,"platform":"macOS","host":%s,"generatedUtc":%s,"checks":[' \
            "$KIT_VERSION" "$(json_str "$(host_name)")" "$(json_str "$(date -u +%Y-%m-%dT%H:%M:%SZ)")"
        for id in $CHECK_ORDER; do
            [ -f "$EVID/records/$id.json" ] || continue
            if [ $first -eq 0 ]; then printf ','; fi
            first=0
            tr -d '\n' < "$EVID/records/$id.json"
        done
        printf ']}\n'
    } > "$EVID/summary.json"
    for id in $CHECK_ORDER; do [ -f "$EVID/records/$id.json" ] || pending="$pending $id"; done
    {
        printf '# Native-check evidence: %s\n\n' "$(host_name)"
        printf 'Written %s by `scripts/native-check/macos.sh` (kit v%s). Machine-readable copy: `summary.json`. Machine details: `machine.txt`.\n\n' \
            "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$KIT_VERSION"
        printf '| Check | Result | Timestamp (UTC) | Machine | Notes | Artifacts |\n|---|---|---|---|---|---|\n'
        for id in $CHECK_ORDER; do if [ -f "$EVID/records/$id.md-row" ]; then cat "$EVID/records/$id.md-row"; fi; done
        printf '\nNot recorded:%s\n\n## Steps\n\n' "${pending:- none}"
        for id in $CHECK_ORDER; do if [ -f "$EVID/records/$id.md-steps" ]; then cat "$EVID/records/$id.md-steps"; fi; done
    } > "$EVID/summary.md"
}

finish_bundle() {
    local zip="$EVID.zip"
    write_summary
    write_machine_txt
    rm -f "$zip"
    ditto -c -k --norsrc --noextattr --noacl --keepParent "$EVID" "$zip"
    title "Done"
    say "Send this file back: $(tilde "$zip")"
    say "You can run the kit again later; it adds to the same bundle and you zip it again."
}

# ---------------------------------------------------------------------------------------------------------------------
# Machine and build description
# ---------------------------------------------------------------------------------------------------------------------
rosetta_installed() { [ -e /Library/Apple/usr/share/rosetta/rosetta ] || /usr/bin/pgrep -q oahd; }
dev_tools_installed() { xcode-select -p >/dev/null 2>&1; }

machine_json() {
    if [ -z "$MACHINE_JSON" ]; then
        MACHINE_JSON=$(printf '{"os":"macOS","osVersion":%s,"osBuild":%s,"arch":%s,"model":%s,"cpu":%s,"rosettaInstalled":%s,"developerTools":%s,"host":%s}' \
            "$(json_str "$(sw_vers -productVersion)")" "$(json_str "$(sw_vers -buildVersion)")" "$(json_str "$(uname -m)")" \
            "$(json_str "$(sysctl -n hw.model 2>/dev/null || true)")" \
            "$(json_str "$(sysctl -n machdep.cpu.brand_string 2>/dev/null || true)")" \
            "$(json_str "$(yes_no_of rosetta_installed)")" "$(json_str "$(yes_no_of dev_tools_installed)")" \
            "$(json_str "$(host_name)")")
    fi
    printf '%s' "$MACHINE_JSON"
}
machine_short() {
    printf 'macOS %s (%s), %s, %s' "$(sw_vers -productVersion)" "$(sw_vers -buildVersion)" "$(uname -m)" \
        "$(sysctl -n hw.model 2>/dev/null || true)"
}
write_machine_txt() {
    {
        printf 'Kit: macos.sh v%s\nWritten: %s\n\n' "$KIT_VERSION" "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
        sw_vers
        printf 'uname -m: %s\nModel: %s\nCPU: %s\n' "$(uname -m)" "$(sysctl -n hw.model 2>/dev/null || true)" \
            "$(sysctl -n machdep.cpu.brand_string 2>/dev/null || true)"
        printf 'Rosetta runtime present (/Library/Apple/usr/share/rosetta/rosetta): %s\n' \
            "$(yes_no_of test -e /Library/Apple/usr/share/rosetta/rosetta)"
        printf 'oahd running (pgrep oahd): %s\n' "$(yes_no_of /usr/bin/pgrep -q oahd)"
        printf 'Developer tools (xcode-select -p): %s\n' "$(yes_no_of dev_tools_installed)"
    } > "$EVID/machine.txt"
}

app_version() {
    /usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$1/Contents/Info.plist" 2>/dev/null || echo unknown
}
build_json() {
    if [ -z "$APP" ]; then printf 'null'; return 0; fi
    printf '{"app":%s,"version":%s,"source":%s,"ciRunId":%s}' "$(json_str "$(tilde "$APP")")" \
        "$(json_str "$(app_version "$APP")")" "$(json_str "$(state_get source)")" "$(json_str "$CI_RUN")"
}
build_short() {
    if [ -z "$APP" ]; then printf 'none'; return 0; fi
    printf '%s from %s%s' "$(app_version "$APP")" "$(state_get source)" "${CI_RUN:+ (CI run $CI_RUN)}"
}

# ---------------------------------------------------------------------------------------------------------------------
# The build under test: a bundle, a CI zip (extracted with ditto and with unzip) or a CI run downloaded with gh
# ---------------------------------------------------------------------------------------------------------------------
set_app() {   # bundle source
    [ -x "$1/Contents/MacOS/DialShift" ] || die "not a DialShift bundle: $1"
    APP=$(cd "$1" && pwd)
    state_set app "$APP"
    state_set source "$2"
    CI_RUN=$(state_get ci_run)
    if [ ! -f "$EVID/state/ci_run" ]; then
        CI_RUN=$(ask_line "CI run id this build came from (checks should use the CI artifact; Enter if unknown or a local build):")
        state_set ci_run "$CI_RUN"
    fi
    say "Build under test: $(tilde "$APP"), version $(app_version "$APP")"
}

extract_zip() {   # zip: extracts with ditto (as Finder and Safari do) and with unzip (as other tools do)
    local zip=$1 dest extractor
    [ -f "$zip" ] || die "zip not found: $zip"
    zip="$(cd "$(dirname "$zip")" && pwd)/$(basename "$zip")"
    dest="$WORK_ROOT/apps/$(basename "$zip" .zip)-$(shasum -a 256 "$zip" | cut -c1-8)"
    if [ ! -d "$dest/ditto/DialShift.app" ] || [ ! -d "$dest/unzip/DialShift.app" ]; then
        rm -rf "$dest"
        mkdir -p "$dest"
        ditto -x -k "$zip" "$dest/ditto"
        unzip -q "$zip" -d "$dest/unzip"
    fi
    say "Extracted with ditto and with unzip into $(tilde "$dest")."
    for extractor in ditto unzip; do
        if codesign --verify --deep --strict "$dest/$extractor/DialShift.app" >/dev/null 2>&1; then
            say "  $extractor copy: codesign --verify --deep --strict passes."
        else
            warn "the $extractor copy fails codesign --verify --deep --strict (SR-03). Record it in the check you run."
        fi
    done
    set_app "$dest/ditto/DialShift.app" "zip $(basename "$zip")"
}

download_run() {   # CI run id
    local dir="$WORK_ROOT/downloads/$1"
    command -v gh >/dev/null 2>&1 || die "--run needs the GitHub CLI (gh). Download the zip from the run page and use --zip."
    mkdir -p "$dir"
    if [ ! -f "$dir/$MAC_ARTIFACT.zip" ]; then gh run download "$1" -R "$CI_REPO" -n "$MAC_ARTIFACT" -D "$dir"; fi
    state_set ci_run "$1"
    extract_zip "$dir/$MAC_ARTIFACT.zip"
}

resolve_app() {
    local saved answer
    if [ -n "$APP" ] && [ -d "$APP" ]; then return 0; fi
    if [ -n "$OPT_APP" ]; then set_app "$OPT_APP" "bundle $(tilde "$OPT_APP")"; return 0; fi
    if [ -n "$OPT_ZIP" ]; then extract_zip "$OPT_ZIP"; return 0; fi
    if [ -n "$OPT_RUN" ]; then download_run "$OPT_RUN"; return 0; fi
    saved=$(state_get app)
    if [ -n "$saved" ] && [ -d "$saved" ]; then
        APP=$saved
        CI_RUN=$(state_get ci_run)
        say "Build under test: $(tilde "$APP"), version $(app_version "$APP")"
        return 0
    fi
    say "Which DialShift build should the kit test? Matrix §9 asks for the CI artifact, the zip a user downloads."
    say "Give a path to DialShift.app or to $MAC_ARTIFACT.zip$(if command -v gh >/dev/null 2>&1; then printf ', or a CI run id'; fi)."
    while true; do
        answer=$(ask_line "Path or run id:")
        answer=${answer/#\~/$HOME}
        case $answer in
            *.zip) if [ -f "$answer" ]; then extract_zip "$answer"; return 0; fi ;;
            *.app | *.app/) if [ -d "$answer" ]; then set_app "${answer%/}" "bundle $(tilde "${answer%/}")"; return 0; fi ;;
            *[!0-9]* | '') ;;
            *) if command -v gh >/dev/null 2>&1; then download_run "$answer"; return 0; fi ;;
        esac
        say "Not found. Try again."
    done
}

# ---------------------------------------------------------------------------------------------------------------------
# Running DialShift and reading its log
# ---------------------------------------------------------------------------------------------------------------------
dialshift_count() { /usr/bin/pgrep -x DialShift | wc -l | tr -d ' ' || true; }
first_dialshift_pid() { /usr/bin/pgrep -x DialShift | head -n 1 || true; }

wait_running() {   # seconds; warns on timeout
    local n=${1:-30}
    while [ "$n" -gt 0 ]; do
        if [ "$(dialshift_count)" -gt 0 ]; then return 0; fi
        sleep 1
        n=$((n - 1))
    done
    warn "DialShift did not start. If macOS blocked it, use System Settings → Privacy & Security → Open Anyway."
}
wait_quit() {   # seconds; returns 1 if DialShift is still running
    local n=${1:-15}
    while [ "$n" -gt 0 ]; do
        if [ "$(dialshift_count)" -eq 0 ]; then return 0; fi
        sleep 1
        n=$((n - 1))
    done
    return 1
}
ask_user_quit() {
    if [ "$(dialshift_count)" -eq 0 ]; then return 0; fi
    pause "Quit DialShift from its menu-bar menu (Quit DialShift), then press Enter."
    if wait_quit 15; then return 0; fi
    warn "DialShift is still running."
    if ask_yn "Send it SIGTERM (the same clean quit path as Quit DialShift)?" y; then
        /usr/bin/pkill -TERM -x DialShift || true
        wait_quit 15 || warn "It is still running. Quit it by hand before going on."
    fi
}
require_no_dialshift() {
    if [ "$(dialshift_count)" -eq 0 ]; then return 0; fi
    say "DialShift is running. This check needs it closed first: your own copy too, and any older DialShift (a new one refuses to start beside it)."
    ask_user_quit
    [ "$(dialshift_count)" -eq 0 ] || die "DialShift is still running"
}
new_data_dir() { local d; d="$WORK_ROOT/data/$1-$(date +%Y%m%d-%H%M%S)"; mkdir -p "$d"; printf '%s' "$d"; }
launch_isolated() {   # app data-dir [args...]: an isolated data folder (DIALSHIFT_DATA_DIR) through LaunchServices
    local app=$1 dir=$2
    shift 2
    if [ $# -gt 0 ]; then open -n --env "DIALSHIFT_DATA_DIR=$dir" "$app" --args "$@"; else open -n --env "DIALSHIFT_DATA_DIR=$dir" "$app"; fi
    wait_running 30
}

crash_reports() { find "$HOME/Library/Logs/DiagnosticReports" -mindepth 1 -maxdepth 1 ! -name '.*' -iname '*dialshift*' 2>/dev/null | sed 's|^.*/||' | sort || true; }
new_crash_reports() {   # before-file: prints reports that appeared since
    crash_reports > "$CUR_DIR/.crash-after"
    comm -13 "$1" "$CUR_DIR/.crash-after" || true
    rm -f "$CUR_DIR/.crash-after"
}

log_lines() { if [ -f "$1" ]; then wc -l < "$1" | tr -d ' '; else echo 0; fi; }
log_since() {   # file mark: the lines written after the mark (covers one rotation to .1)
    local now
    now=$(log_lines "$1")
    if [ "$now" -lt "$2" ]; then
        if [ -f "$1.1" ]; then tail -n +"$(($2 + 1))" "$1.1"; fi
        if [ -f "$1" ]; then cat "$1"; fi
    elif [ -f "$1" ]; then
        tail -n +"$(($2 + 1))" "$1"
    fi
}
log_field() { sed -n "s/^.*\"$2\":\"\\([^\"]*\\)\".*$/\\1/p" <<<"$1" | head -n 1; }
log_msg() { sed -e 's/","ex":".*$/"}/' -e 's/^.*"msg":"//' -e 's/"}$//' <<<"$1"; }
count_ev() {   # file event [substring]
    local lines
    lines=$(grep -F "\"event\":\"$2\"" "$1" 2>/dev/null || true)
    if [ -n "${3:-}" ]; then lines=$(grep -F -- "$3" <<<"$lines" || true); fi
    if [ -z "$lines" ]; then echo 0; else printf '%s\n' "$lines" | wc -l | tr -d ' '; fi
}
ts_ms() {   # 2026-09-25T10:11:12.1234567+00:00 → epoch milliseconds
    local s frac
    s=$(date -j -u -f '%Y-%m-%dT%H:%M:%S' "${1:0:19}" +%s 2>/dev/null) || { echo 0; return 0; }
    frac=${1:20:3}
    case $frac in [0-9][0-9][0-9]) ;; *) frac=000 ;; esac
    echo $((s * 1000 + 10#$frac))
}
ms_to_s() { printf '%d.%d' $(($1 / 1000)) $((($1 % 1000) / 100)); }
timeline() {   # jsonl → one line per event the checks read, with seconds since the first
    local line ts first=""
    grep -E '"event":"(app\.(start|exit|render_timer_fallback|startup_failed)|playback\.(state|failed|fallback|engine_error)|wake\.|power_events\.|schedule\.fired|single_instance\.(activated|socket)|settings\.recovered|startup_registration\.)' "$1" 2>/dev/null |
        while IFS= read -r line; do
            ts=$(log_field "$line" ts)
            if [ -z "$first" ]; then first=$(ts_ms "$ts"); fi
            printf '%s  +%ss  %-30s %s\n' "$ts" "$(ms_to_s $(($(ts_ms "$ts") - first)))" "$(log_field "$line" event)" "$(log_msg "$line")"
        done || true
}
# Saves the log lines after a mark as <name>.jsonl plus a readable <name>-timeline.txt; sets EXCERPT.
save_excerpt() {   # name log mark
    EXCERPT="$CUR_DIR/$1.jsonl"
    log_since "$2" "$3" > "$EXCERPT"
    timeline "$EXCERPT" > "$CUR_DIR/$1-timeline.txt"
    add_artifact "$CUR_ID/$1.jsonl"
    add_artifact "$CUR_ID/$1-timeline.txt"
}
check_clean_exit() {   # log label-prefix: the last app.exit is code=0 clean=true and no DialShift remains
    local line
    line=$(grep -F '"event":"app.exit"' "$1" 2>/dev/null | tail -n 1 || true)
    auto_step "$2 The log ends with app.exit code=0 clean=true" "$(pf grep -qiF 'code=0 clean=true' <<<"$line")" "$(log_msg "$line")"
    auto_step "$2 No DialShift process remains" "$(pf [ "$(dialshift_count)" -eq 0 ])" "$(dialshift_count) running"
}

# ---------------------------------------------------------------------------------------------------------------------
# Real data: move the user's DialShift files aside, restore them afterwards
# ---------------------------------------------------------------------------------------------------------------------
mget() { sed -n "s/^$1=//p" "$MANIFEST" | tail -n 1; }
manifest_add() { printf '%s=%s\n' "$1" "$2" >> "$MANIFEST"; }
agent_state() {   # disabled | enabled | none: launchd's override for the DialShift label
    local value
    value=$(launchctl print-disabled "gui/$(id -u)" 2>/dev/null | awk -v l="\"$AGENT_LABEL\"" '$1 == l { print $3 }' | head -n 1 || true)
    case $value in disabled | true) echo disabled ;; enabled | false) echo enabled ;; *) echo none ;; esac
}
launchd_line() { launchctl print-disabled "gui/$(id -u)" 2>&1 | grep -F "\"$AGENT_LABEL\"" || echo "(no launchd override for $AGENT_LABEL)"; }

real_begin() {   # owner [install|aside]: install puts the build under test in /Applications; aside only clears it
    local owner=$1 app_mode=${2:-} dir
    if [ -f "$MANIFEST" ]; then
        if [ "$(mget owner)" = "$owner" ]; then return 0; fi
        warn "Your real DialShift files are still moved aside by $(mget owner). Restore them first (menu option r)."
        return 1
    fi
    say ""
    warn "$owner needs DialShift's real locations: Finder, login and LaunchServices launches can't use an isolated data folder."
    say "The kit moves your originals aside and puts them back when $owner ends (or with menu option r):"
    say "  $(tilde "$REAL_DATA")   moved aside; the check starts with default settings"
    say "  $(tilde "$AGENT_PLIST")   copied, then restored with its launchctl enable/disable state"
    say "  $(tilde "$LEGACY_AGENT_PLIST")   (an older DialShift's entry) copied and restored, if present"
    if [ -n "$app_mode" ]; then say "  $INSTALLED_APP   moved aside if present"; fi
    if [ "$app_mode" = install ]; then say "  and the build under test is copied to $INSTALLED_APP"; fi
    ask_yn "Continue?" n || return 1
    require_no_dialshift
    dir="$WORK_ROOT/backup/$(date +%Y%m%d-%H%M%S)-$owner"
    mkdir -p "$dir"
    printf 'owner=%s\ndir=%s\nagent_state=%s\n' "$owner" "$dir" "$(agent_state)" > "$MANIFEST"
    if [ -e "$REAL_DATA" ]; then mv "$REAL_DATA" "$dir/data"; manifest_add data "$dir/data"; fi
    if [ -e "$AGENT_PLIST" ]; then cp -p "$AGENT_PLIST" "$dir/agent.plist"; manifest_add agent "$dir/agent.plist"; fi
    if [ -e "$LEGACY_AGENT_PLIST" ]; then cp -p "$LEGACY_AGENT_PLIST" "$dir/legacy-agent.plist"; manifest_add legacy_agent "$dir/legacy-agent.plist"; fi
    if [ -n "$app_mode" ] && ! [ "$APP" -ef "$INSTALLED_APP" ]; then
        if [ -e "$INSTALLED_APP" ]; then mv "$INSTALLED_APP" "$dir/DialShift.app"; manifest_add app "$dir/DialShift.app"; fi
        manifest_add test_app_in_applications yes
        if [ "$app_mode" = install ]; then ditto "$APP" "$INSTALLED_APP"; fi
    fi
    say "Originals moved aside into $(tilde "$dir")."
}

real_restore() {
    local aside original current
    if [ ! -f "$MANIFEST" ]; then say "Nothing to restore."; return 0; fi
    title "Restoring your DialShift files (moved aside by $(mget owner))"
    require_no_dialshift
    aside="$WORK_ROOT/test-leftovers/$(date +%Y%m%d-%H%M%S)-$(mget owner)"
    mkdir -p "$aside"
    if [ -e "$REAL_DATA" ]; then mv "$REAL_DATA" "$aside/data"; fi
    original=$(mget data)
    if [ -n "$original" ]; then mv "$original" "$REAL_DATA"; fi
    original=$(mget agent)
    if [ -n "$original" ]; then cp -p "$original" "$AGENT_PLIST"; elif [ -e "$AGENT_PLIST" ]; then mv "$AGENT_PLIST" "$aside/"; fi
    original=$(mget legacy_agent)
    if [ -n "$original" ]; then cp -p "$original" "$LEGACY_AGENT_PLIST"; fi
    original=$(mget agent_state)
    current=$(agent_state)
    if [ "$original" = disabled ] && [ "$current" != disabled ]; then launchctl disable "gui/$(id -u)/$AGENT_LABEL"; fi
    if [ "$original" != disabled ] && [ "$current" = disabled ]; then launchctl enable "gui/$(id -u)/$AGENT_LABEL"; fi
    if [ -e "$WORK_ROOT/moved/DialShift.app" ]; then mv "$WORK_ROOT/moved/DialShift.app" "$aside/DialShift-moved.app"; fi
    if [ "$(mget test_app_in_applications)" = yes ] && [ -e "$INSTALLED_APP" ]; then mv "$INSTALLED_APP" "$aside/DialShift.app"; fi
    original=$(mget app)
    if [ -n "$original" ]; then mv "$original" "$INSTALLED_APP"; fi
    mv "$MANIFEST" "$aside/manifest.restored"
    say "Restored. What the test left behind (its data folder, and the test app if it was installed) is in $(tilde "$aside")."
}
offer_restore() { if [ -f "$MANIFEST" ] && ask_yn "Restore your real DialShift files now?" y; then real_restore; fi; }

# ---------------------------------------------------------------------------------------------------------------------
# Optional screenshots (only with your consent, one prompt per screenshot)
# ---------------------------------------------------------------------------------------------------------------------
screenshot() {   # name mode(window|region|timed) description
    local file="$CUR_DIR/$1.png"
    if [ -z "$SHOTS_OK" ]; then
        if ask_yn "Screenshots are optional. Should the kit offer them (each one asks first; you pick the window or area)?" n; then SHOTS_OK=yes; else SHOTS_OK=no; fi
    fi
    [ "$SHOTS_OK" = yes ] || return 0
    ask_yn "Screenshot of $3?" y || return 0
    case $2 in
        window) say "Click the window to capture (Esc cancels)."; screencapture -i -W -x "$file" || true ;;
        region) say "Drag over the area to capture (Esc cancels)."; screencapture -i -s -x "$file" || true ;;
        timed) warn "this captures the WHOLE screen in 6 seconds. Close anything private first."; screencapture -T 6 -x "$file" || true ;;
    esac
    if [ -s "$file" ]; then add_artifact "$CUR_ID/$1.png"; say "Saved."; else say "No screenshot saved (if macOS asked for Screen Recording permission for Terminal, the capture may be empty)."; fi
}

# ---------------------------------------------------------------------------------------------------------------------
# Shared scenarios: seeded settings, the corpus run, the retry and fallback timeline, wake analysis
# ---------------------------------------------------------------------------------------------------------------------
write_corpus_settings() {   # data-dir list
    local first=1 id url what expect class
    {
        printf '{\n  "Version": 1,\n  "Stations": [\n'
        while IFS='|' read -r id url what expect class; do
            [ -n "$id" ] || continue
            if [ $first -eq 0 ]; then printf ',\n'; fi
            first=0
            printf '    { "Name": %s, "Url": %s, "Tag": %s }' "$(json_str "$id")" "$(json_str "$url")" "$(json_str "$what; expect $expect ($class)")"
        done <<<"$2"
        printf '\n  ],\n  "Schedule": [],\n  "Volume": 60\n}\n'
    } > "$1/settings.json"
}

write_retry_settings() {   # data-dir: an unreachable primary whose fallback is Groove Salad
    local primary fallback
    primary=$(uuidgen)
    fallback=$(uuidgen)
    cat > "$1/settings.json" <<EOF
{
  "Version": 1,
  "Stations": [
    { "Id": "$primary", "Name": "RETRY-PRIMARY", "Url": "http://127.0.0.1:1/unavailable", "Tag": "Refuses connections: retries, then the fallback" },
    { "Id": "$fallback", "Name": "RETRY-FALLBACK", "Url": "https://ice1.somafm.com/groovesalad-128-mp3", "Tag": "Groove Salad, the fallback" }
  ],
  "Schedule": [],
  "Volume": 60,
  "FallbackStationId": "$fallback"
}
EOF
}

# Waits up to 40 s after a Listen for the station's first outcome; sets OUTCOME (Playing, a failure kind or timeout)
# and SECS (from its first Connecting or Reconnecting).
corpus_measure() {   # log id mark
    local deadline ex states end_line start_line
    OUTCOME=timeout
    SECS=""
    deadline=$(($(date +%s) + 40))
    while [ "$(date +%s)" -lt "$deadline" ]; do
        ex=$(log_since "$1" "$3")
        states=$(grep -F '"event":"playback.state"' <<<"$ex" | grep -F "station='$2'" || true)
        end_line=$(grep -F -- "> Playing; station='$2'" <<<"$states" | head -n 1 || true)
        if [ -n "$end_line" ]; then
            OUTCOME=Playing
        else
            end_line=$(grep -F '"event":"playback.failed"' <<<"$ex" | grep -F "station='$2'" | head -n 1 || true)
            if [ -n "$end_line" ]; then OUTCOME=$(sed -n 's/.*kind=\([A-Za-z]*\).*/\1/p' <<<"$end_line"); fi
        fi
        if [ -n "$end_line" ]; then
            start_line=$(grep -E -- "> (Connecting|Reconnecting); station='$2'" <<<"$states" | head -n 1 || true)
            if [ -n "$start_line" ]; then
                SECS=$(ms_to_s $(($(ts_ms "$(log_field "$end_line" ts)") - $(ts_ms "$(log_field "$start_line" ts)"))))
            fi
            return 0
        fi
        sleep 1
    done
}

corpus_run() {   # log list: you press Listen on each station, the kit reads the outcome from the log
    local id url what expect class reply heard result detail mark
    [ -f "$CUR_DIR/corpus.tsv" ] || printf 'id\turl\texpected\toutcome\tseconds\taudible\n' > "$CUR_DIR/corpus.tsv"
    add_artifact "$CUR_ID/corpus.tsv"
    while IFS='|' read -r id url what expect class <&3; do
        [ -n "$id" ] || continue
        printf '\n%s: %s (expected: %s)\n' "$id" "$what" "$expect"
        mark=$(log_lines "$1")
        reply=$(ask_line "   On the Stations page press Listen on $id, then press Enter here (s = skip):")
        if [ "$reply" = s ]; then add_step manual SKIP "$id $what" "skipped"; continue; fi
        corpus_measure "$1" "$id" "$mark"
        say "   Outcome: $OUTCOME${SECS:+ after $SECS s}"
        heard=-
        if [ "$OUTCOME" = Playing ]; then if ask_yn "   Do you hear it?" y; then heard=yes; else heard=no; fi; fi
        printf '%s\t%s\t%s\t%s\t%s\t%s\n' "$id" "$url" "$expect" "$OUTCOME" "${SECS:--}" "$heard" >> "$CUR_DIR/corpus.tsv"
        if [ "$OUTCOME" = "$expect" ]; then result=PASS; elif [ "$class" = required ]; then result=FAIL; else result=INFO; fi
        if [ "$heard" = no ]; then result=FAIL; fi
        detail="expected $expect, got $OUTCOME${SECS:+ in $SECS s}; audible: $heard"
        if [ "$class" = optional ] && [ "$OUTCOME" != Playing ]; then detail="$detail; optional format: list it in the README Formats line"; fi
        add_step auto "$result" "$id $what" "$detail"
    done 3<<<"$2"
}

retry_verdict() {   # excerpt: the §5.1 retry and fallback timings for RETRY-PRIMARY / RETRY-FALLBACK
    local retries switches playing recheck delta
    retries=$(grep -F '"event":"playback.failed"' "$1" | grep -F "station='RETRY-PRIMARY'" | sed -n 's/.*retry_in=\([0-9]*\)s.*/\1/p' | head -n 3 | tr '\n' ' ' || true)
    auto_step "Retries after failures 1, 2 and 3 are due in 3 s, 6 s and 30 s" "$(pf [ "$retries" = "3 6 30 " ])" "retry_in: ${retries:-none}"
    switches=$(count_ev "$1" playback.fallback "Switching to fallback 'RETRY-FALLBACK'")
    auto_step "The fallback takes over after 3 failures" "$(pf [ "$switches" -ge 1 ])" "$switches switch(es) to the fallback"
    playing=$(grep -F '"event":"playback.state"' "$1" | grep -F "> Playing; station='RETRY-FALLBACK'" | head -n 1 || true)
    recheck=$(grep -F '"event":"playback.fallback"' "$1" | grep -F "Re-trying primary 'RETRY-PRIMARY'" | head -n 1 || true)
    if [ -n "$playing" ] && [ -n "$recheck" ]; then
        delta=$((($(ts_ms "$(log_field "$recheck" ts)") - $(ts_ms "$(log_field "$playing" ts)")) / 1000))
        auto_step "The primary is re-checked 120 s after the fallback plays" "$(pf in_range "$delta" 115 130)" "${delta} s"
    else
        auto_step "The primary is re-checked 120 s after the fallback plays" FAIL "no fallback Playing or no primary re-check in the log"
    fi
    auto_step "Alternation: the fallback takes over again after the re-check fails" "$(pf [ "$switches" -ge 2 ])" "$switches switch(es)"
}

wake_verdict() {   # excerpt mode(playing|paused|tick_gap) label-prefix
    local resumed accepted recoveries playing active
    resumed=$(count_ev "$1" power_events.resumed)
    accepted=$(($(count_ev "$1" wake.detected) - $(count_ev "$1" wake.detected "ignored:")))
    recoveries=$(count_ev "$1" wake.recovery)
    if [ "$2" = tick_gap ]; then
        auto_step "$3 The tick gap detected the wake (wake.detected source=tick_gap)" "$(pf [ "$(count_ev "$1" wake.detected source=tick_gap)" -ge 1 ])" "power_events.resumed: $resumed"
    else
        auto_step "$3 power_events.resumed was logged" "$(pf [ "$resumed" -ge 1 ])" "$resumed line(s)"
    fi
    if [ "$2" = paused ]; then
        active=$(grep -F '"event":"playback.state"' "$1" | grep -cE -- '> (Connecting|Reconnecting|Playing);' || true)
        auto_step "$3 Paused stays paused (no Connecting, Reconnecting or Playing)" "$(pf [ "${active:-0}" -eq 0 ])" "${active:-0} such transition(s)"
        return 0
    fi
    auto_step "$3 Exactly one accepted wake.detected" "$(pf [ "$accepted" -eq 1 ])" "$accepted accepted"
    auto_step "$3 Exactly one wake.recovery (one reconnect)" "$(pf [ "$recoveries" -eq 1 ])" \
        "$(grep -F '"event":"wake.recovery"' "$1" | while IFS= read -r l; do log_msg "$l"; printf '; '; done || true)"
    playing=$(count_ev "$1" playback.state "> Playing;")
    auto_step "$3 Playback is Playing again after the recovery" "$(pf [ "$playing" -ge 1 ])" "$playing Playing transition(s)"
}

smoke_verdict() {   # results.json label: the smoke's own verdict (System.Text.Json indented layout)
    local overall complete total failed result=FAIL
    if [ ! -f "$1" ]; then auto_step "$2" FAIL "results.json is missing"; return 0; fi
    overall=$(awk '/^  "passed": / { gsub(/[ ",]/, "", $2); print $2; exit }' "$1")
    complete=$(awk '/^  "complete": / { gsub(/[ ",]/, "", $2); print $2; exit }' "$1")
    total=$(awk '/^      "name": / { n++ } END { print n + 0 }' "$1")
    failed=$(awk '/^      "name": / { name = $0; sub(/^      "name": "/, "", name); sub(/",$/, "", name) }
                  /^      "passed": false/ { printf "%s; ", name }' "$1")
    if [ "$overall" = true ] && [ "$complete" = true ]; then result=PASS; fi
    auto_step "$2" "$result" "$total checks, passed=$overall, complete=$complete${failed:+; failed: $failed}"
}

pmset_excerpt() { pmset -g log | grep -E '[[:space:]](Sleep|Wake|DarkWake)[[:space:]]|Display is turned' | tail -n 60; }
display_info() { system_profiler SPDisplaysDataType 2>/dev/null | grep -viE 'serial|EDID' || true; }
appearance() { if [ "$(defaults read -g AppleInterfaceStyle 2>/dev/null || true)" = Dark ]; then echo Dark; else echo Light; fi; }
other_zone() { if [ "$(TZ=Europe/Athens date +%z)" != "$(date +%z)" ]; then echo Europe/Athens; else echo America/New_York; fi; }
is_macos14() { case $(sw_vers -productVersion) in 14.*) return 0 ;; *) return 1 ;; esac; }
no_rosetta() { ! /usr/bin/pgrep -q oahd && ! arch -x86_64 /usr/bin/true >/dev/null 2>&1; }
no_dev_tools() { ! dev_tools_installed; }
lid_cycle() {   # [local time the lid must stay closed until]
    pause "Press Enter, then close the lid right away and keep it closed for at least 60 seconds${1:+ and until after $1}."
    pause "Welcome back. After logging in, wait 20 seconds, then press Enter."
}
# Records a precondition. Returns 1 when it isn't met and you choose not to continue.
precondition() {   # description test...
    local what=$1
    shift
    if "$@" >/dev/null 2>&1; then add_step auto PASS "Precondition: $what"; return 0; fi
    add_step auto INFO "Precondition not met: $what"
    warn "precondition not met: $what."
    if ask_yn "Continue anyway? The check is then recorded as SKIP, with your observations." n; then
        PRECOND_FAILED="${PRECOND_FAILED:+$PRECOND_FAILED; }$what"
        return 0
    fi
    return 1
}
# Starts DialShift while every display is asleep, with the Mac itself kept awake; plays a sound when done.
start_with_displays_asleep() {   # command...
    countdown 10 "Hands off. The displays sleep in"
    caffeinate -i -w $$ &
    CAFFEINATE_PID=$!
    pmset displaysleepnow
    sleep 15
    "$@" || true
    kill "$CAFFEINATE_PID" 2>/dev/null || true
    CAFFEINATE_PID=""
    afplay /System/Library/Sounds/Glass.aiff 2>/dev/null || true
}
fallback_verdict() {   # log label: the app.render_timer_fallback line of a start with no active display
    local line
    line=$(grep -F '"event":"app.render_timer_fallback"' "$1" 2>/dev/null || true)
    if [ -z "$line" ]; then
        auto_step "$2 The start had no active display (app.render_timer_fallback)" SKIP \
            "no fallback line: the displays were awake at launch, so this run did not test NX-01; run it again without touching the Mac"
    else
        auto_step "$2 app.render_timer_fallback names CVReturn -6661" "$(pf grep -qF 'CVReturn -6661' <<<"$line")" "$(log_msg "$line")"
    fi
}
second_open() {   # app log label: a second 'open -n' must reach the running instance and leave one process
    local before
    before=$(count_ev "$2" single_instance.activated delivered)
    do_step "Click another app (for example Finder) so DialShift is not in front." "Press Enter and the kit runs a second 'open -n'."
    open -n "$1"
    sleep 5
    auto_step "$3 The second open is delivered (single_instance.activated … delivered)" \
        "$(pf [ "$(count_ev "$2" single_instance.activated delivered)" -gt "$before" ])"
    auto_step "$3 One DialShift process remains" "$(pf [ "$(dialshift_count)" -eq 1 ])" "$(dialshift_count) running"
    ask_step "$3 The second open brought the DialShift window to the front"
}

# ---------------------------------------------------------------------------------------------------------------------
# Procedures and pass criteria (a summary of acceptance matrix §9; §9 wins if they disagree)
# ---------------------------------------------------------------------------------------------------------------------
procedure() {
    title "$1: procedure and pass criteria (acceptance matrix §9)"
    procedure_text "$1" | wrap
    say "(A summary of matrix §9. If the two disagree, §9 is right.)"
}
procedure_text() {
    case $1 in
        NX-01) cat <<'EOF'
Procedure (NC-17 step 10; finding NX-01, decision D52): start the bundled app while every display is asleep but the Mac is awake: pmset displaysleepnow; sleep 15; open -n -W <DialShift.app> --args --smoke-test --output <dir>. Leave the keyboard, mouse and trackpad alone until it exits (about 1 to 2 minutes).

Pass: no crash (no new DialShift report in ~/Library/Logs/DiagnosticReports); the log shows app.render_timer_fallback naming CVReturn -6661; results.json reports every check passed.

The kit runs exactly that. It keeps the Mac itself awake with caffeinate -i (the displays still sleep), plays a sound when the run ends, compares the crash reports before and after, and reads results.json and the log. If the fallback line is missing, the displays were awake at launch and the run did not test NX-01: run it again.
EOF
            ;;
        NC-12) cat <<'EOF'
Procedure: with the bundled app running, look at the 44x44 template menu-bar icon (tray.png, D34) at 17 pt in a light and in a dark menu bar, and with its menu open (highlight inversion). Then edit stations and schedule slots and look at the tray menu after each edit.

Pass: each observation holds on a Retina display: the icon is sharp in light and dark menu bars, it follows highlight inversion when the menu is open, and the tray menu updates after station and slot edits with no native crash.

The kit starts DialShift with an isolated data folder, records the display type and the appearance setting at each step, offers optional screenshots, and checks afterwards that the app kept running, logged no error and left no crash report.
EOF
            ;;
        NC-17) cat <<'EOF'
Procedure (the bundled app under LaunchServices, real clicks and audio):
(1) A Finder double-click or open on the running app shows the window (Reopen, BHV-18); a second open activates it and exits 0.
(2) Clicking the menu-bar icon shows the menu, each item works, and hovering shows the tooltip.
(3) Listen, Skip and the menu's Next station switch audibly, one stream at a time; Pause silences it.
(4) Close, minimize and Hide to tray hide the window while audio continues; start in tray shows no window flash (BHV-09).
(5) "Open settings folder" reveals ~/Library/Application Support/DialShift in Finder (BHV-63).
(6) Tab shows a visible focus ring, Enter and Escape work in the dialogs, and VoiceOver reads the field and button names.
(7) settings.json replaced with { gives one "Settings recovered" dialog; a folder named .single-instance.lock in the data folder gives the "couldn't start" dialog and exit 1.
(8) The pages, both editors and the compact size show no clipped text, at the default and the largest macOS text size.
(9) Quit from the menu.
Step 10 is NX-01, a separate entry in this kit.

Pass: each observation holds, and the log records each action, ending with app.exit ... code=0 clean=true.

The kit moves your real DialShift data aside (Finder launches can't use an isolated folder), starts and restarts the app, runs the second launches and reads their exit codes, prepares the broken settings file and the lock folder, checks the log and the process list, and restores your data at the end.
EOF
            ;;
        NC-10) cat <<'EOF'
Procedure (with the bundled app in /Applications):
(1) Turn on "Launch DialShift in the tray when I log in" (the macOS wording of the setting), log out and in: DialShift starts in the menu bar with no window (open -a <bundle> --args --tray).
(2) Move DialShift.app elsewhere and log in again: the checkbox shows off with the stale diagnostic, and turning it on repairs it.
(3) In System Settings > General > Login Items, switch DialShift off under "Allow in the Background", then reopen DialShift's Settings. Record whether launchctl print-disabled gui/$UID lists "com.tsiger.dialshift" => disabled, and whether the checkbox shows off with the Login Items diagnostic. If it still shows on, print-disabled does not see that switch, and D39 and the README must record the limit.
(4) launchctl disable gui/$UID/com.tsiger.dialshift: the checkbox shows off; turning it on runs launchctl enable (the override is gone from print-disabled) without starting a second instance, and the next login starts DialShift.

Pass: each step as described; ~/Library/LaunchAgents/com.tsiger.dialshift.plist passes plutil -lint and targets the current bundle; no second instance is ever started.

NC-13's remaining half runs after the first login: the socket must be mode 600, the log must have single_instance.socket, and a second open must activate the window.

The kit moves your real install aside and installs the build under test in /Applications. After each login it checks the plist, the launchd state, the process start time and the socket. It moves the bundle for step 2, runs launchctl disable for step 4, and restores everything at the end. It needs three log-out and log-in cycles: after each login, run the kit again and it continues.
EOF
            ;;
        NC-13) cat <<'EOF'
Procedure: with DialShift started by the LaunchAgent at a real login (not from a terminal), check the activation socket. The LaunchServices half passed on 2026-09-25; this is the remaining half.

Pass: stat -f %Lp "$TMPDIR"/CoreFxPipe_DialShift-* prints 600; the log has single_instance.socket with the observed mode; a second open activates the window.
EOF
            ;;
        NC-11) cat <<'EOF'
Procedure (the bundled app): Wi-Fi off in the middle of a stream, then on (retry, then recovery); a real captive portal; fallback alternation with the real engine (BHV-37).

Pass: failures are classified as in the corpus (docs/spikes.md); the retry and fallback timings match matrix §5.1 (retries after 3 s and 6 s, then every 30 s; the fallback after 3 failures; the primary re-checked 120 s after the fallback plays); no audio after Stop.

The kit runs DialShift with isolated data folders. It can switch Wi-Fi off and on for you (networksetup) and records the log timeline of each part. For the fallback part it sets up a station that refuses connections, with Groove Salad as its fallback, and checks the timings in the log. The captive portal needs such a network (hotel, cafe, airport); skip that part if you have none.
EOF
            ;;
        NC-08) cat <<'EOF'
Procedure (a MacBook, the bundled app):
(1) While playing, close the lid for at least 60 s, then open it.
(2) Repeat while paused inside a slot.
(3) Add a slot in another time zone that starts while the lid is closed (TZ-12).
(4) Repeat (1) with the wake observer unavailable (a dev build with MacPowerEvents.Start disabled), so only the tick gap detects the wake (D14).
(5) No active display at startup (NX-01, D52): quit DialShift, start it while every display is asleep but the Mac is awake, leave the keyboard and mouse alone, then wake the displays and open the window from the menu bar.

Pass: power_events.resumed on each wake, on the main thread (D30); exactly one wake.recovery per wake; paused stays paused; the zoned slot plays once (schedule.fired names its zone); the tick gap alone recovers when the observer is off. After (5): no crash, the log has app.render_timer_fallback naming CVReturn -6661, and once the displays wake the window renders and updates normally.

The kit uses an isolated data folder, computes a slot time in another zone for you, analyses the log after each wake, saves the pmset sleep and wake log, and runs the display-sleep start of step 5. The main-thread delivery is not visible in the log: note it if you observed it with a dev build or a debugger.
EOF
            ;;
        NC-07) cat <<'EOF'
Procedure (an Apple Silicon Mac with no Rosetta and no developer tools): download the CI DialShift-osx-arm64-native-avplayer.zip with Safari, unzip it in Finder, move DialShift.app to /Applications and double-click it. Expected first: Gatekeeper blocks it (ad-hoc signed, not notarized): the quarantine flag is set, codesign -dv shows Signature=adhoc, and spctl -a -vv reports it rejected. Open it with System Settings > Privacy & Security > Open Anyway. Then: the menu-bar icon appears and there is no Dock icon; an https:// and an http:// station play audibly; Quit, then relaunch without a prompt; a second open activates the window. Also extract the same zip with unzip in Terminal: codesign --verify --deep --strict must pass, and that copy must open after the same Open Anyway step.

Pass: each observation holds; app.start shows rid=osx-arm64 arch=Arm64 engine=MacAvPlayerPlaybackEngine; no Rosetta prompt ever appears.

The kit checks that the Mac is clean (pgrep oahd and arch -x86_64 /usr/bin/true must both fail, and no developer tools), checks the quarantine flag on the download, runs the xattr, codesign and spctl checks, reads app.start from the log, runs the second open and makes the unzip copy. Your real DialShift data and any /Applications/DialShift.app are moved aside first and restored at the end.
EOF
            ;;
        NC-16) cat <<'EOF'
Procedure: run the AVPlayer corpus (C1-C15; the public T cases are optional) from the bundled app on macOS 14.x on Apple Silicon, the minimum version (D22).

Pass: MP3, AAC and HLS play. Any other format that fails (Ogg Vorbis, Opus, FLAC in Ogg, .aacp) goes into the README "Formats" line. If MP3, AAC or HLS fail, a new decision raises LSMinimumSystemVersion.

The kit runs DialShift with an isolated data folder that holds the corpus stations (named C1, C2 and so on). You press Listen on each one; the kit reads the time to Playing or the failure kind from the log and asks whether you hear it. corpus.tsv holds the table.
EOF
            ;;
    esac
}

# ---------------------------------------------------------------------------------------------------------------------
# NX-01 (NC-17 step 10): the smoke started with the displays asleep
# ---------------------------------------------------------------------------------------------------------------------
check_nx01() {
    local out before new
    begin_check NX-01
    procedure NX-01
    resolve_app
    out="$CUR_DIR/smoke"
    warn "plug in the power adapter. During the run (about 2 minutes) don't touch the keyboard, mouse or trackpad, and make sure nothing else wakes the displays."
    ask_yn "Start now? The displays go to sleep 10 seconds after you answer." y || return 0
    rm -rf "$out"
    mkdir -p "$out"
    before="$CUR_DIR/crash-reports-before.txt"
    crash_reports > "$before"
    start_with_displays_asleep open -n -W "$APP" --args --smoke-test --output "$out"
    pause "The run has ended. Wake the displays, then press Enter."
    add_artifact "NX-01/smoke/"
    new=$(new_crash_reports "$before")
    auto_step "No crash: no new DialShift report in ~/Library/Logs/DiagnosticReports" "$(pf [ -z "$new" ])" "${new:-none}"
    fallback_verdict "$out/dialshift.log" "Log:"
    smoke_verdict "$out/results.json" "results.json reports every check passed"
    save_cmd pmset-log.txt pmset_excerpt
    finish_check
}

# ---------------------------------------------------------------------------------------------------------------------
# NC-12: the menu-bar template icon
# ---------------------------------------------------------------------------------------------------------------------
check_nc12() {
    local data log before new errors result=FAIL
    begin_check NC-12
    procedure NC-12
    resolve_app
    require_no_dialshift
    data=$(new_data_dir NC-12)
    log="$data/dialshift.log"
    save_cmd displays.txt display_info
    before="$CUR_DIR/crash-reports-before.txt"
    crash_reports > "$before"
    launch_isolated "$APP" "$data" --tray
    do_step "1. Switch System Settings → Appearance to Light. Look at the DialShift icon in the menu bar from a normal viewing distance."
    auto_step "1. Appearance is Light" "$(pf [ "$(appearance)" = Light ])" "$(appearance)"
    screenshot icon-light region "the menu-bar area around the DialShift icon (Light)"
    ask_step "1. Light menu bar: the icon is sharp at 17 pt (no blur, no jagged edges)"
    do_step "2. Switch System Settings → Appearance to Dark and look at the icon again."
    auto_step "2. Appearance is Dark" "$(pf [ "$(appearance)" = Dark ])" "$(appearance)"
    screenshot icon-dark region "the menu-bar area around the DialShift icon (Dark)"
    ask_step "2. Dark menu bar: the icon is sharp at 17 pt"
    do_step "3. Click the DialShift icon so its menu opens and look at the icon while the menu is open, in Dark and again in Light. Then set your usual appearance back."
    screenshot icon-highlighted timed "the screen with the DialShift menu open (open it within 6 seconds)"
    ask_step "3. With the menu open the icon is highlighted and inverted correctly, in Light and in Dark"
    do_step "4. Open the window (menu → Open). Add a station, rename it, delete it; add a schedule slot, change it, delete it. After each edit open the menu-bar menu and look at Stations ▸ and Follow schedule."
    ask_step "4. The tray menu follows each station and slot edit"
    auto_step "4. DialShift is still running (no native crash)" "$(pf [ "$(dialshift_count)" -gt 0 ])" "$(dialshift_count) running"
    new=$(new_crash_reports "$before")
    errors=$(grep -c '"level":"error"' "$log" 2>/dev/null || true)
    if [ -z "$new" ] && [ "${errors:-0}" -eq 0 ]; then result=PASS; fi
    auto_step "4. No error line in the log and no crash report" "$result" "errors: ${errors:-0}; crash reports: ${new:-none}"
    ask_user_quit
    check_clean_exit "$log" "5."
    save_excerpt log "$log" 0
    finish_check
}

# ---------------------------------------------------------------------------------------------------------------------
# NC-17 steps 1-9: the native UI pass of the bundled app under LaunchServices (real data folder)
# ---------------------------------------------------------------------------------------------------------------------
check_nc17() {
    local log="$REAL_DATA/dialshift.log" exe before new rc
    begin_check NC-17
    procedure NC-17
    resolve_app
    exe="$APP/Contents/MacOS/DialShift"
    real_begin NC-17 || return 0
    before="$CUR_DIR/crash-reports-before.txt"
    crash_reports > "$before"

    title "Step 1: Reopen and a second launch"
    open "$APP"
    wait_running 30
    do_step "Close the DialShift window with its red close button. DialShift keeps running in the menu bar."
    open -R "$APP"
    do_step "In the Finder window the kit just opened, double-click DialShift."
    ask_step "1a. The Finder double-click on the running app shows the window (Reopen, BHV-18)"
    do_step "Close the window again."
    second_open "$APP" "$log" "1b."
    say "The kit now starts the executable directly as another second launch, to read its exit code."
    rc=0
    "$exe" >/dev/null 2>&1 || rc=$?
    auto_step "1c. A second launch exits 0" "$(pf [ "$rc" -eq 0 ])" "exit code $rc"

    title "Step 2: the menu-bar menu"
    do_step "Click the DialShift menu-bar icon. Try every item: Open, Play/Pause, Next station, Stations ▸, Follow schedule (its check must match the Schedule page), Volume +10 and −10. Leave Quit for step 9. Then hover over the icon."
    ask_step "2. The menu opens on click, each item works, and the tooltip shows 'DialShift · <station>' or 'DialShift · Paused'"

    title "Step 3: audio"
    do_step "In the window press Listen on a station and hear it. Press Skip, then the menu's Next station: each switches audibly to the next station. Then press Pause."
    ask_step "3. Listen, Skip and Next station switch audibly, one stream at a time; Pause silences it"

    title "Step 4: hiding the window"
    do_step "While a station plays: close the window and reopen it from the menu; minimize it and reopen it; press ↘ Hide to tray. Audio must continue each time."
    ask_step "4a. Close, minimize and Hide to tray hide the window while audio continues"
    ask_user_quit
    say "The kit now starts DialShift with --tray. Watch the screen: no window may flash up."
    open "$APP" --args --tray
    wait_running 30
    sleep 3
    ask_step "4b. Starting in the tray shows no window flash (BHV-09)"

    title "Step 5: the settings folder"
    do_step "Open the window (menu → Open), go to Settings and click 'Open settings folder ↗'."
    ask_step "5. Finder reveals ~/Library/Application Support/DialShift (BHV-63)"

    title "Step 6: keyboard and VoiceOver"
    do_step "Press Tab repeatedly through each page. Open a confirm dialog (for example delete a schedule slot) and try Enter and Escape. Turn VoiceOver on (Command-F5), move through fields and buttons, then turn it off (Command-F5)."
    ask_step "6a. Tab shows a visible focus ring on every control"
    ask_step "6b. Enter and Escape work in the dialogs"
    ask_step "6c. VoiceOver reads the field and button names"

    title "Step 7: the recovery and failure dialogs"
    say "The kit edits the test data folder (your own data is moved aside)."
    ask_user_quit
    printf '{' > "$REAL_DATA/settings.json"
    open "$APP"
    wait_running 30
    do_step "A 'DialShift · Settings recovered' dialog should appear. Read it and press OK."
    auto_step "7a. A settings.json.unreadable-* copy exists" "$(pf ls "$REAL_DATA"/settings.json.unreadable-*)" \
        "$(cd "$REAL_DATA" && ls -1 settings.json.unreadable-* 2>/dev/null | tr '\n' ' ' || true)"
    auto_step "7b. The log has settings.recovered" "$(pf [ "$(count_ev "$log" settings.recovered)" -ge 1 ])"
    ask_step "7c. Exactly one 'Settings recovered' dialog appeared, naming the settings.json.unreadable-* copy"
    ask_user_quit
    rm -f "$REAL_DATA/.single-instance.lock"
    mkdir "$REAL_DATA/.single-instance.lock"
    say "The kit starts the executable from Terminal. Press OK in the 'DialShift couldn't start' dialog."
    rc=0
    "$exe" >/dev/null 2>&1 || rc=$?
    rmdir "$REAL_DATA/.single-instance.lock"
    auto_step "7d. The lock failure exits 1" "$(pf [ "$rc" -eq 1 ])" "exit code $rc"
    ask_step "7e. The 'DialShift couldn't start' dialog says it couldn't create its lock file"

    title "Step 8: text"
    open "$APP"
    wait_running 30
    do_step "Look at each page (Stations, Schedule, Settings), both editors (add a station, add a slot) and the window at its smallest size. Then choose the largest text size (System Settings → Accessibility → Display → Text size) and look again. Set it back afterwards."
    screenshot window-text window "the DialShift window"
    ask_step "8. No clipped or overlapping text at the default and the largest text size (QG-03)"

    title "Step 9: Quit"
    do_step "Quit from the menu-bar menu: Quit DialShift."
    wait_quit 15 || true
    check_clean_exit "$log" "9."
    new=$(new_crash_reports "$before")
    auto_step "9. No DialShift crash report during the pass" "$(pf [ -z "$new" ])" "${new:-none}"
    save_excerpt log "$log" 0
    finish_check
    offer_restore
}

# ---------------------------------------------------------------------------------------------------------------------
# NC-10 (+ NC-13's LaunchAgent half): launch at login across three real log-outs; resumes after each login
# ---------------------------------------------------------------------------------------------------------------------
check_nc10() {
    local phase
    phase=$(cat "$EVID/NC-10/phase" 2>/dev/null || echo 0)
    if [ "$phase" = 0 ]; then begin_check NC-10; else begin_check NC-10 resume; fi
    resolve_app
    case $phase in
        0) nc10_start ;;
        1) nc10_after_login_1 ;;
        2) nc10_after_login_2 ;;
        3) nc10_after_login_3 ;;
    esac
}

nc10_logout() {   # next phase: records the log-out time and exits; the next run continues
    printf '%s\n' "$1" > "$CUR_DIR/phase"
    date +%s > "$CUR_DIR/logout-at"
    title "Log out and back in (NC-10, login $1 of 3)"
    say "1. Apple menu → Log Out $(id -un). In the dialog, turn off 'Reopen windows when logging back in'."
    say "2. Log back in and wait about 20 seconds."
    say "3. Open Terminal and run the kit again; it continues NC-10 from here:"
    say "     bash $(printf '%q' "$KIT_PATH")"
    exit 0
}

started_after_logout() {   # pid
    local started
    started=$(LC_ALL=C date -j -f '%a %b %d %T %Y' "$(LC_ALL=C ps -o lstart= -p "$1" | sed -e 's/  */ /g' -e 's/^ //')" +%s 2>/dev/null || echo 0)
    [ "$started" -gt "$(cat "$CUR_DIR/logout-at")" ]
}

login_start_checks() {   # label-prefix: what the LaunchAgent started at this login
    local pid cmd result=FAIL
    pid=$(first_dialshift_pid)
    auto_step "$1 DialShift started at login" "$(pf [ -n "$pid" ])" "${pid:+pid $pid}"
    [ -n "$pid" ] || return 0
    cmd=$(ps -o command= -p "$pid" | redact)
    auto_step "$1 It started after the log-out (not a leftover process)" "$(pf started_after_logout "$pid")"
    if grep -qF "$INSTALLED_APP/" <<<"$cmd" && grep -qF -- '--tray' <<<"$cmd"; then result=PASS; fi
    auto_step "$1 It runs $INSTALLED_APP with --tray" "$result" "$cmd"
    auto_step "$1 Exactly one DialShift instance" "$(pf [ "$(dialshift_count)" -eq 1 ])" "$(dialshift_count) running"
}

agent_checks() {   # label-prefix bundle: the LaunchAgent plist is valid and runs open -a <bundle> --args --tray
    local result=FAIL
    auto_step "$1 The LaunchAgent plist exists" "$(pf [ -f "$AGENT_PLIST" ])" "$(tilde "$AGENT_PLIST")"
    auto_step "$1 plutil -lint passes" "$(pf plutil -lint -s "$AGENT_PLIST")"
    if grep -qF "<string>$2</string>" "$AGENT_PLIST" 2>/dev/null && grep -qF '<string>--tray</string>' "$AGENT_PLIST"; then result=PASS; fi
    auto_step "$1 It targets $(tilde "$2") with --tray" "$result"
}

nc10_start() {
    procedure NC-10
    real_begin NC-10 install || return 0
    open "$INSTALLED_APP"
    say "If macOS blocks it, use System Settings → Privacy & Security → Open Anyway."
    wait_running 60
    do_step "Step 1: open the window (menu-bar icon → Open), go to Settings and turn on 'Launch DialShift in the tray when I log in'."
    agent_checks "1a." "$INSTALLED_APP"
    save_cmd agent-plist-step1.txt plutil -p "$AGENT_PLIST"
    save_cmd launchd-step1.txt launchd_line
    ask_user_quit
    nc10_logout 1
}

nc10_after_login_1() {
    say "Welcome back. Checking what started at login."
    sleep 5
    login_start_checks "1b."
    ask_step "1c. DialShift is in the menu bar and no window appeared"
    nc13_socket
    title "Step 2: a moved bundle"
    ask_user_quit
    mkdir -p "$WORK_ROOT/moved"
    mv "$INSTALLED_APP" "$WORK_ROOT/moved/DialShift.app"
    say "Moved $INSTALLED_APP to $(tilde "$WORK_ROOT/moved/DialShift.app")."
    nc10_logout 2
}

nc13_socket() {   # records NC-13 from inside NC-10, then returns to NC-10
    local log="$REAL_DATA/dialshift.log" line sock mode owner result=FAIL
    begin_check NC-13
    procedure NC-13
    line=$(grep -F '"event":"single_instance.socket"' "$log" 2>/dev/null | tail -n 1 || true)
    auto_step "The log has single_instance.socket" "$(pf [ -n "$line" ])" "$(log_msg "$line")"
    sock=$(log_msg "$line" | sed -n 's/^Activation socket \([^ ]*\) .*/\1/p')
    if [ -z "$sock" ]; then sock=$(ls -t "${TMPDIR%/}"/CoreFxPipe_DialShift-* 2>/dev/null | head -n 1 || true); fi
    mode=$(stat -f %Lp "$sock" 2>/dev/null || echo missing)
    owner=$(stat -f %Su "$sock" 2>/dev/null || echo missing)
    if [ "$mode" = 600 ] && [ "$owner" = "$(id -un)" ]; then result=PASS; fi
    auto_step "stat -f %Lp prints 600 for the socket, owned by you" "$result" "mode=$mode owner=$owner socket=$(basename "${sock:-none}")"
    save_cmd socket-stat.txt stat -f '%Sp %Lp %Su %N' "$sock"
    second_open "$INSTALLED_APP" "$log" "2."
    save_excerpt login-launch "$log" 0
    finish_check
    begin_check NC-10 resume
}

nc10_after_login_2() {
    local moved="$WORK_ROOT/moved/DialShift.app" line listed off
    sleep 5
    auto_step "2a. Nothing started at login (the entry points at the moved bundle's old place)" "$(pf [ "$(dialshift_count)" -eq 0 ])" "$(dialshift_count) running"
    open "$moved"
    wait_running 30
    do_step "Open the window (menu-bar icon → Open) and go to Settings."
    ask_step "2b. The checkbox shows off with 'Launch at login points to an older copy of DialShift…'"
    do_step "Turn the checkbox on."
    agent_checks "2c. Repaired:" "$moved"
    ask_user_quit
    mv "$moved" "$INSTALLED_APP"
    say "Moved the bundle back to $INSTALLED_APP, so the entry points at the old place again."
    open "$INSTALLED_APP"
    wait_running 30
    do_step "Open Settings again: it shows off with the older-copy message. Turn it on."
    agent_checks "2d. Repaired again:" "$INSTALLED_APP"

    title "Step 3: Login Items, Allow in the Background"
    do_step "Open System Settings → General → Login Items and switch DialShift off under 'Allow in the Background'. Then in DialShift go to another page and back to Settings."
    line=$(launchd_line)
    save_cmd launchd-step3.txt launchd_line
    listed=no
    if grep -qE '=> (disabled|true)' <<<"$line"; then listed=yes; fi
    add_step auto INFO "3a. launchctl print-disabled lists DialShift as disabled: $listed" "$line"
    if ask_yn "   Does the checkbox now show off with the Login Items message?" n; then off=yes; else off=no; fi
    add_step manual INFO "3b. The checkbox shows off with the Login Items message: $off"
    say "   Pass when both answers agree. Both 'no' means print-disabled can't see the Login Items switch: that is the limit D39 and the README must record (docs/open-items.md §2.9). A mismatch is a failure; explain it in the note."
    ask_step "3c. The Login Items observation is recorded and consistent"
    do_step "Switch DialShift back on under 'Allow in the Background'."

    title "Step 4: launchctl disable and enable"
    launchctl disable "gui/$(id -u)/$AGENT_LABEL"
    auto_step "4a. launchctl disable took effect" "$(pf [ "$(agent_state)" = disabled ])" "$(launchd_line)"
    do_step "In DialShift go to another page and back to Settings."
    ask_step "4b. The checkbox shows off with 'macOS has launch at login turned off for DialShift…'"
    do_step "Turn the checkbox on."
    sleep 2
    auto_step "4c. Turning it on removed the override (launchctl enable)" "$(pf [ "$(agent_state)" != disabled ])" "$(launchd_line)"
    auto_step "4d. No second instance was started" "$(pf [ "$(dialshift_count)" -eq 1 ])" "$(dialshift_count) running"
    agent_checks "4e." "$INSTALLED_APP"
    ask_user_quit
    nc10_logout 3
}

nc10_after_login_3() {
    sleep 5
    login_start_checks "4f. Next login:"
    agent_checks "4g." "$INSTALLED_APP"
    save_cmd agent-plist-final.txt plutil -p "$AGENT_PLIST"
    save_excerpt log "$REAL_DATA/dialshift.log" 0
    rm -f "$CUR_DIR/phase" "$CUR_DIR/logout-at"
    finish_check
    offer_restore
}

# ---------------------------------------------------------------------------------------------------------------------
# NC-11: AVPlayer under real network faults
# ---------------------------------------------------------------------------------------------------------------------
check_nc11() {
    local data log mark wifi route_if failures data3 log3
    begin_check NC-11
    procedure NC-11
    resolve_app
    require_no_dialshift
    data=$(new_data_dir NC-11)
    log="$data/dialshift.log"
    launch_isolated "$APP" "$data"

    title "Part 1: Wi-Fi off in the middle of a stream"
    wifi=$(networksetup -listallhardwareports 2>/dev/null | awk '/^Hardware Port: (Wi-Fi|AirPort)/ { getline; print $2; exit }' || true)
    route_if=$(route -n get default 2>/dev/null | awk '/interface:/ { print $2 }' || true)
    if [ -n "$wifi" ] && [ "$route_if" != "$wifi" ]; then
        warn "the default route uses ${route_if:-no interface}, not Wi-Fi ($wifi). Turning Wi-Fi off won't cut the stream: unplug Ethernet or turn off the other network service first."
    fi
    do_step "Open the window (menu-bar icon → Open) and press Listen on Groove Salad. Wait until you hear it." "Press Enter when it plays."
    mark=$(log_lines "$log")
    if [ -n "$wifi" ] && ask_yn "Let the kit turn Wi-Fi ($wifi) off for 60 seconds, then back on?" y && networksetup -setairportpower "$wifi" off; then
        WIFI_OFF=$wifi
        countdown 60 "Wi-Fi is off. Back on in"
        networksetup -setairportpower "$wifi" on
        WIFI_OFF=""
    else
        do_step "Turn Wi-Fi off from the menu bar, wait about 60 seconds, then turn it back on."
    fi
    countdown 60 "Letting the stream recover:"
    save_excerpt wifi "$log" "$mark"
    failures=$(count_ev "$EXCERPT" playback.failed)
    auto_step "1a. The outage is detected (playback.failed)" "$(pf [ "$failures" -ge 1 ])" \
        "$(grep -F '"event":"playback.failed"' "$EXCERPT" | sed -n 's/.*kind=\([A-Za-z]*\);.*retry_in=\([0-9]*\)s.*/\1 (retry in \2 s)/p' | tr '\n' ',' || true)"
    auto_step "1b. Playback recovered after Wi-Fi returned (the last state is Playing)" \
        "$(pf grep -qF '> Playing;' <<<"$(grep -F '"event":"playback.state"' "$EXCERPT" | tail -n 1)")"
    ask_step "1c. Audio came back by itself, with no click"

    title "Part 2: a captive portal"
    if ask_yn "This part needs a Wi-Fi network with a captive portal (hotel, cafe, airport) that you have not logged in to. Are you on one now?" n; then
        mark=$(log_lines "$log")
        do_step "Close the macOS login sheet if it appears. Press Listen on Groove Salad (https) and wait 40 seconds. Then add a station with the http:// URL http://france16.coollabel-productions.com:8276/; (C9), play it and wait 40 seconds."
        save_excerpt captive "$log" "$mark"
        add_step auto INFO "2a. Failure kinds seen" \
            "$(grep -F '"event":"playback.failed"' "$EXCERPT" | sed -n "s/.*kind=\([A-Za-z]*\);.*station='\([^']*\)'.*/\1 \2/p" | sort | uniq -c | tr '\n' ';' || true)"
        ask_step "2b. The failures match the corpus (an HTML page: UnsupportedFormat, or Stalled by the 25 s watchdog; a certificate problem: TlsFailure) and the retries follow §5.1"
    else
        add_step manual SKIP "2. Captive portal" "no captive-portal network available"
    fi

    title "Part 3: fallback alternation with the real engine"
    ask_user_quit
    data3=$(new_data_dir NC-11-fallback)
    write_retry_settings "$data3"
    log3="$data3/dialshift.log"
    launch_isolated "$APP" "$data3"
    mark=$(log_lines "$log3")
    do_step "Open the window and press Listen on RETRY-PRIMARY. It refuses connections; its fallback is RETRY-FALLBACK (Groove Salad). The kit then records for about 4 minutes." "Press Enter right after pressing Listen."
    countdown 250 "Recording retries, the fallback and the primary re-check:"
    save_excerpt fallback "$log3" "$mark"
    retry_verdict "$EXCERPT"
    ask_step "3a. Groove Salad (the fallback) was audible while the primary failed"
    do_step "Press Pause and listen for 10 seconds."
    ask_step "3b. No audio after Stop"
    ask_user_quit
    finish_check
}

# ---------------------------------------------------------------------------------------------------------------------
# NC-08: lid-close wake (steps 1-4) and the start with no active display (step 5)
# ---------------------------------------------------------------------------------------------------------------------
check_nc08() {
    local data log mark zone mins zone_time zone_day local_time dev before reply
    begin_check NC-08
    procedure NC-08
    resolve_app
    require_no_dialshift
    case $(sysctl -n hw.model 2>/dev/null || true) in
        MacBook*) ;;
        *) warn "this Mac has no lid: steps 1-4 need a MacBook. Skip them and run step 5 only." ;;
    esac
    warn "with an external display connected, closing the lid keeps the Mac awake (clamshell mode). Disconnect external displays for steps 1-4."
    data=$(new_data_dir NC-08)
    log="$data/dialshift.log"
    launch_isolated "$APP" "$data"
    sleep 5
    auto_step "0. power_events.started at startup" "$(pf [ "$(count_ev "$log" power_events.started)" -ge 1 ])"

    title "Step 1: lid close while playing"
    if ask_yn "Run step 1 (needs a lid)?" y; then
        do_step "Open the window (menu-bar icon → Open) and press Listen on Groove Salad. Wait until you hear it." "Press Enter when it plays."
        mark=$(log_lines "$log")
        lid_cycle
        save_excerpt wake-playing "$log" "$mark"
        wake_verdict "$EXCERPT" playing "1."
        ask_step "1. Audio came back by itself after the wake"
    else
        add_step manual SKIP "1. Lid close while playing" "not run"
    fi

    title "Step 2: lid close while paused inside a slot"
    if ask_yn "Run step 2 (needs a lid)?" y; then
        do_step "On the Schedule page turn on Follow my schedule and add a slot that started earlier today, so a slot is active and playing. Then press Pause."
        mark=$(log_lines "$log")
        lid_cycle
        save_excerpt wake-paused "$log" "$mark"
        wake_verdict "$EXCERPT" paused "2."
        ask_step "2. DialShift stayed paused and silent after the wake"
    else
        add_step manual SKIP "2. Lid close while paused" "not run"
    fi

    title "Step 3: a slot in another time zone starts while the lid is closed (TZ-12)"
    if ask_yn "Run step 3 (needs a lid)?" y; then
        zone=$(other_zone)
        mins=$(ask_line "Minutes from now for the slot to start (time to add it, then close the lid) [5]:")
        case $mins in '' | *[!0-9]*) mins=5 ;; esac
        zone_time=$(TZ="$zone" date -v+"$mins"M +%H:%M)
        zone_day=$(TZ="$zone" date -v+"$mins"M +%A)
        local_time=$(date -v+"$mins"M +%H:%M)
        do_step "Press Listen on a station and let it play. On the Schedule page add a slot in time zone $zone at $zone_time on $zone_day ($local_time your time) with a different station, and keep Follow my schedule on."
        mark=$(log_lines "$log")
        lid_cycle "$local_time"
        save_excerpt wake-zoned-slot "$log" "$mark"
        auto_step "3a. The $zone slot fired exactly once (schedule.fired names the zone)" \
            "$(pf [ "$(count_ev "$EXCERPT" schedule.fired "$zone")" -eq 1 ])" \
            "$(grep -F '"event":"schedule.fired"' "$EXCERPT" | while IFS= read -r l; do log_msg "$l"; printf '; '; done || true)"
        ask_step "3b. The slot's station played after the wake, once"
    else
        add_step manual SKIP "3. Zoned slot during sleep (TZ-12)" "not run"
    fi

    title "Step 4: the tick gap alone"
    dev=$(ask_line "Path to a dev build of DialShift.app with MacPowerEvents.Start disabled (the platform lane provides it; Enter to skip):")
    dev=${dev/#\~/$HOME}
    if [ -n "$dev" ] && [ -x "$dev/Contents/MacOS/DialShift" ]; then
        ask_user_quit
        mark=$(log_lines "$log")
        launch_isolated "$dev" "$data"
        sleep 5
        auto_step "4a. The dev build has no wake observer (no power_events.started)" \
            "$(pf [ "$(log_since "$log" "$mark" | grep -cF '"event":"power_events.started"' || true)" -eq 0 ])"
        do_step "Open the window and press Listen on Groove Salad. Wait until you hear it." "Press Enter when it plays."
        mark=$(log_lines "$log")
        lid_cycle
        save_excerpt wake-tick-gap "$log" "$mark"
        wake_verdict "$EXCERPT" tick_gap "4b."
        ask_step "4c. Audio came back by itself after the wake"
    else
        add_step manual SKIP "4. Tick gap alone" "no dev build with the wake observer disabled"
    fi

    title "Step 5: start with no active display (NX-01)"
    ask_user_quit
    if ask_yn "Run step 5 now? Hands off after you answer: the displays sleep in 10 s, DialShift starts 15 s later, and a sound plays about 30 s after that." y; then
        before="$CUR_DIR/crash-reports-before.txt"
        crash_reports > "$before"
        mark=$(log_lines "$log")
        start_with_displays_asleep open_and_wait "$data"
        pause "Wake the displays, then press Enter."
        save_excerpt no-display-start "$log" "$mark"
        fallback_verdict "$EXCERPT" "5a."
        auto_step "5b. No crash: no new DialShift report" "$(pf [ -z "$(new_crash_reports "$before")" ])"
        auto_step "5c. DialShift is running" "$(pf [ "$(dialshift_count)" -eq 1 ])" "$(dialshift_count) running"
        do_step "Open the window from the menu bar. Switch pages, press Listen, change the volume."
        ask_step "5d. The window renders and updates normally after the displays woke"
    else
        add_step manual SKIP "5. Start with no active display" "not run"
    fi

    reply=$(ask_line "Did you observe the thread power_events.resumed arrives on (D30: the main thread), with a dev build or a debugger? Describe it, or press Enter to skip:")
    if [ -n "$reply" ]; then add_step manual INFO "Thread of power_events.resumed (D30)" "$reply"; fi
    save_cmd pmset-log.txt pmset_excerpt
    ask_user_quit
    check_clean_exit "$log" "6."
    finish_check
}
open_and_wait() { open -n --env "DIALSHIFT_DATA_DIR=$1" "$APP"; sleep 30; }   # NC-08 step 5's launch

# ---------------------------------------------------------------------------------------------------------------------
# NC-07: clean Apple Silicon Mac, Safari download, Gatekeeper first launch
# ---------------------------------------------------------------------------------------------------------------------
check_nc07() {
    local zip quarantine line log="$REAL_DATA/dialshift.log" unzip_dir result=FAIL
    begin_check NC-07
    procedure NC-07
    precondition "Apple Silicon" [ "$(uname -m)" = arm64 ] || return 0
    say "The next precondition runs 'arch -x86_64 /usr/bin/true', which just fails on a Mac without Rosetta; it installs nothing."
    precondition "No Rosetta (pgrep oahd and arch -x86_64 /usr/bin/true both fail)" no_rosetta || return 0
    precondition "No developer tools (xcode-select -p fails)" no_dev_tools || return 0
    real_begin NC-07 aside || return 0

    title "Download and install"
    say "In Safari open the CI run page (Actions → the run → Artifacts) and download $MAC_ARTIFACT. GitHub wraps artifacts in a zip: if Safari unzips it once, the $MAC_ARTIFACT.zip inside is the file a user gets."
    zip=$(ask_line "Path of the downloaded $MAC_ARTIFACT.zip [~/Downloads/$MAC_ARTIFACT.zip]:")
    zip=${zip:-$HOME/Downloads/$MAC_ARTIFACT.zip}
    zip=${zip/#\~/$HOME}
    quarantine=$(xattr -p com.apple.quarantine "$zip" 2>/dev/null || true)
    auto_step "1. The downloaded zip carries the quarantine flag" "$(pf [ -n "$quarantine" ])" "${quarantine:-none}"
    do_step "Double-click the zip in Finder to unzip it, then drag DialShift.app into Applications (Finder: Go → Applications)."
    while [ ! -d "$INSTALLED_APP" ]; do do_step "$INSTALLED_APP isn't there yet. Drag DialShift.app into Applications."; done
    set_app "$INSTALLED_APP" "Safari download $(basename "$zip"), Finder unzip"
    quarantine=$(xattr -p com.apple.quarantine "$INSTALLED_APP" 2>/dev/null || true)
    auto_step "2a. The quarantine flag propagated to $INSTALLED_APP" "$(pf [ -n "$quarantine" ])" "${quarantine:-none}"
    save_cmd codesign-dv.txt codesign -dv "$INSTALLED_APP"
    auto_step "2b. codesign -dv shows Signature=adhoc" "$(pf grep -qx 'Signature=adhoc' "$CUR_DIR/codesign-dv.txt")"
    auto_step "2c. codesign --verify --deep --strict passes" "$(pf codesign --verify --deep --strict "$INSTALLED_APP")"
    save_cmd spctl.txt spctl -a -vv "$INSTALLED_APP"
    auto_step "2d. spctl -a -vv reports it rejected (ad-hoc signed, not notarized)" "$(pf grep -q rejected "$CUR_DIR/spctl.txt")"

    title "First launch"
    do_step "Double-click DialShift in Applications. macOS blocks it. Open System Settings → Privacy & Security, scroll down, click Open Anyway and confirm."
    wait_running 60
    ask_step "3a. Gatekeeper blocked the first launch, and Open Anyway started DialShift"
    sleep 3
    line=$(grep -F '"event":"app.start"' "$log" 2>/dev/null | tail -n 1 || true)
    if grep -qF 'rid=osx-arm64' <<<"$line" && grep -qF 'arch=Arm64' <<<"$line" && grep -qF 'engine=MacAvPlayerPlaybackEngine' <<<"$line"; then result=PASS; fi
    auto_step "3b. app.start shows rid=osx-arm64 arch=Arm64 engine=MacAvPlayerPlaybackEngine" "$result" "$(log_msg "$line")"
    auto_step "3c. It runs from /Applications, not a translocated copy (no app.translocated)" "$(pf [ "$(count_ev "$log" app.translocated)" -eq 0 ])"
    ask_step "4. The menu-bar icon appears and there is no Dock icon"
    do_step "Press Listen on Groove Salad (https) and hear it. Then add a station with the http:// URL http://france16.coollabel-productions.com:8276/; and play it."
    ask_step "5a. The https:// station plays audibly"
    ask_step "5b. The http:// station plays audibly (the ATS media exception, D32)"
    ask_user_quit
    do_step "Double-click DialShift in Applications again."
    wait_running 30
    ask_step "6. The relaunch shows no Gatekeeper prompt"
    second_open "$INSTALLED_APP" "$log" "7."
    ask_step "8a. No Rosetta prompt appeared at any point"
    auto_step "8b. Rosetta is still absent" "$(pf no_rosetta)"

    title "The unzip copy (SR-03)"
    ask_user_quit
    if [ -f "$zip" ]; then
        unzip_dir="$WORK_ROOT/nc07-unzip-$(date +%Y%m%d-%H%M%S)"
        mkdir -p "$unzip_dir"
        unzip -q "$zip" -d "$unzip_dir"
        auto_step "9a. The unzip copy passes codesign --verify --deep --strict" "$(pf codesign --verify --deep --strict "$unzip_dir/DialShift.app")"
        if [ -n "$quarantine" ]; then
            xattr -w com.apple.quarantine "$quarantine" "$unzip_dir/DialShift.app"
            say "Gave the unzip copy the download's quarantine flag, as a browser download would have."
        fi
        open "$unzip_dir/DialShift.app" || true
        do_step "If macOS blocks this copy, use Open Anyway again (System Settings → Privacy & Security)."
        wait_running 30
        ask_step "9b. The unzip copy opens after the same Open Anyway step"
        ask_user_quit
    else
        add_step manual SKIP "9. The unzip copy" "the downloaded zip is no longer there"
    fi
    check_clean_exit "$log" "10."
    save_excerpt log "$log" 0
    finish_check
    offer_restore
}

# ---------------------------------------------------------------------------------------------------------------------
# NC-16: the AVPlayer format corpus on macOS 14
# ---------------------------------------------------------------------------------------------------------------------
check_nc16() {
    local data log
    begin_check NC-16
    procedure NC-16
    precondition "macOS 14.x (this Mac runs $(sw_vers -productVersion))" is_macos14 || return 0
    resolve_app
    require_no_dialshift
    data=$(new_data_dir NC-16)
    write_corpus_settings "$data" "$CORPUS_FORMATS
$CORPUS_FAILURES"
    log="$data/dialshift.log"
    launch_isolated "$APP" "$data"
    do_step "Open the window (menu-bar icon → Open) and go to the Stations page. It lists the corpus stations C1 to C15 and T1 to T9. Keep the volume audible."
    corpus_run "$log" "$CORPUS_FORMATS"
    if ask_yn "Also run the public failure cases T1-T9 (recorded only; NC-16 doesn't need them)?" n; then corpus_run "$log" "$CORPUS_FAILURES"; fi
    do_step "Press Pause."
    ask_user_quit
    check_clean_exit "$log" "End:"
    save_excerpt log "$log" 0
    finish_check
}

# ---------------------------------------------------------------------------------------------------------------------
# Menu and entry point
# ---------------------------------------------------------------------------------------------------------------------
run_check() {
    local id=$1
    if [ "$id" = NC-13 ]; then say "NC-13's remaining half runs inside NC-10, after its first login."; id=NC-10; fi
    if [ -f "$EVID/records/$id.json" ] && ! in_progress "$id"; then
        ask_yn "$id is already recorded as $(record_field "$id" result). Run it again and replace the record?" n || return 0
    fi
    case $id in
        NX-01) check_nx01 ;;
        NC-07) check_nc07 ;;
        NC-08) check_nc08 ;;
        NC-10) check_nc10 ;;
        NC-11) check_nc11 ;;
        NC-12) check_nc12 ;;
        NC-16) check_nc16 ;;
        NC-17) check_nc17 ;;
    esac
}

run_pending() {
    local id reply
    for id in $CHECK_ORDER; do
        if [ "$id" = NC-13 ]; then continue; fi
        if [ -f "$EVID/records/$id.json" ] && ! in_progress "$id"; then continue; fi
        reply=$(ask_line "Run $id ($(check_title "$id")) now? [Y]es, [n]o (next check), [q] back to the menu:")
        case $reply in [Qq]*) return 0 ;; [Nn]*) continue ;; esac
        run_check "$id"
    done
}

menu() {
    local choice i id
    while true; do
        title "Checks, in the suggested order (docs/open-items.md §3)"
        i=0
        for id in $CHECK_ORDER; do
            i=$((i + 1))
            printf '  %d) %-6s %-58s %s\n' "$i" "$id" "$(check_title "$id")" "$(check_status "$id")"
        done
        say "  a) run every pending check in this order"
        if [ -f "$MANIFEST" ]; then say "  r) restore your real DialShift files now (moved aside by $(mget owner))"; fi
        say "  f) finish: write summary.md and summary.json and zip the evidence"
        say "  q) quit (run the kit again to resume)"
        choice=$(ask_line "Choice:")
        case $choice in
            [1-9])
                # shellcheck disable=SC2086 # CHECK_ORDER is a word list
                id=$(printf '%s\n' $CHECK_ORDER | sed -n "${choice}p")
                if [ -n "$id" ]; then run_check "$id"; fi
                ;;
            a | A) run_pending ;;
            r | R) real_restore ;;
            f | F) finish_bundle ;;
            q | Q) write_summary; say "Evidence so far: $(tilde "$EVID")"; return 0 ;;
            *) say "Unknown choice." ;;
        esac
    done
}

usage() { sed -n '2,/^set -euo pipefail/p' "$KIT_PATH" | sed -e '$d' -e 's/^# \{0,1\}//'; }

parse_args() {
    while [ $# -gt 0 ]; do
        case $1 in
            --app) OPT_APP=${2:?--app needs a path}; shift 2 ;;
            --zip) OPT_ZIP=${2:?--zip needs a path}; shift 2 ;;
            --run) OPT_RUN=${2:?--run needs a CI run id}; shift 2 ;;
            --evidence) OPT_EVID=${2:?--evidence needs a folder}; shift 2 ;;
            -h | --help) usage; exit 0 ;;
            *) die "unknown option: $1 (see --help)" ;;
        esac
    done
}

on_exit() {
    if [ -n "$WIFI_OFF" ]; then networksetup -setairportpower "$WIFI_OFF" on >/dev/null 2>&1 || true; say "Wi-Fi turned back on."; fi
    if [ -n "$CAFFEINATE_PID" ]; then kill "$CAFFEINATE_PID" 2>/dev/null || true; fi
    if [ -n "$EVID" ] && [ -f "$MANIFEST" ] && ! in_progress "$(mget owner)"; then
        say "Your real DialShift files are still moved aside (by $(mget owner)). Run the kit again and choose r to restore them."
    fi
}

main() {
    parse_args "$@"
    trap on_exit EXIT
    trap 'printf "\nInterrupted. Run the kit again to resume.\n"; exit 130' INT
    # Launches from Terminal must see the same data folder as Finder and login launches do.
    if [ -n "${DIALSHIFT_DATA_DIR:-}" ]; then warn "ignoring DIALSHIFT_DATA_DIR from your shell; the kit sets it per check."; unset DIALSHIFT_DATA_DIR; fi
    mkdir -p "$WORK_ROOT"
    title "DialShift native-check kit for macOS (v$KIT_VERSION)"
    say "Runs the macOS checks of docs/acceptance-matrix.md §9 and records them in one evidence bundle to send back as a zip."
    say "Extracted apps, isolated data folders and backups live in $(tilde "$WORK_ROOT")."
    init_evidence
    write_machine_txt
    if in_progress NC-10; then
        if ask_yn "NC-10 is waiting for you after log-in $(cat "$EVID/NC-10/phase"). Continue it now?" y; then run_check NC-10; fi
    elif [ -f "$MANIFEST" ]; then
        warn "your real DialShift files are still moved aside by an interrupted $(mget owner)."
        offer_restore
    fi
    menu
}

main "$@"
