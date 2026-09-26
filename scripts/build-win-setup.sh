#!/usr/bin/env bash
# Builds DialShift-Setup-win-x64.exe, the per-user Windows setup, from the verified win-x64 package with NSIS
# (brief 4 §6, docs/windows-installer.md; decisions D93, D97, D98).
#
# Usage: scripts/build-win-setup.sh [--package <folder> | --zip <DialShift-win-x64.zip>] [--version <semver>]
#                                   [--output <exe>]
#        scripts/build-win-setup.sh --print-nsis-pin
#
#   --package  the package folder scripts/build.ps1 wrote and verified, or the folder extracted from its zip.
#              Default: artifacts/DialShift-win-x64.
#   --zip      a DialShift-win-x64.zip instead, extracted into a temporary folder first (release-local.sh --win-zip).
#   --version  SemVer without build metadata; its MAJOR.MINOR.PATCH must equal the csproj <Version> (D53), and the
#              package's DialShift.dll must carry it as its informational version (<version> or <version>+<commit>),
#              so a setup never claims another version than the app it installs. Default: the csproj <Version>.
#   --output   default artifacts/DialShift-Setup-win-x64.exe (the release asset name, D53).
#   --print-nsis-pin  prints the pinned NSIS version, the SHA-256 and the URL of the official nsis-<version>.zip as
#              key=value lines (CI's install step appends them to $GITHUB_OUTPUT) and exits.
#
# The NSIS pin lives here, once (D98, D99). makensis must print v<NSIS_VERSION>: on macOS Homebrew's makensis, which
# installs the stubs, plugins and UI of that same official zip; on Windows (CI) the zip itself, checked by its
# SHA-256. $MAKENSIS names the compiler, else makensis on PATH. A different version is refused: bump NSIS_VERSION and
# NSIS_ZIP_SHA256 together, with a decision, and check that the Mac and CI still build the same setup.
#
# The payload is the package minus Install.ps1 plus licenses/NSIS-COPYING.txt (the NSIS licence, brief 4 §10); the
# setup writes "Uninstall DialShift.exe" at install time. The script generates defines.nsh, install-files.nsh (the
# payload in a fixed, host-independent order) and uninstall-files.nsh (the uninstaller's file and folder list) into a
# temporary folder, never into the repository, runs makensis -WX -V2 on scripts/windows-setup/DialShift.nsi, then
# scripts/verify-win-setup.sh (with the package, so the contents are compared when 7-Zip is found) and prints the
# size and SHA-256. Deterministic: the same payload, version and NSIS give the same bytes (SetDateSave off).
#
# bash 3.2+ and Python 3 (standard library): runs on macOS and under Git Bash on Windows, where paths reach
# makensis.exe and Python through cygpath -w.
set -euo pipefail

NSIS_VERSION=3.12
NSIS_ZIP_SHA256=56581f90db321581c5381193d796fffcf2d24b2f8fed2160a6c6a3baa67f2c4f
NSIS_ZIP_URL="https://downloads.sourceforge.net/project/nsis/NSIS%203/$NSIS_VERSION/nsis-$NSIS_VERSION.zip"

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
USAGE="usage: scripts/build-win-setup.sh [--package <folder> | --zip <zip>] [--version <semver>] [--output <exe>] | --print-nsis-pin"
fail() { echo "error: $*" >&2; exit 1; }

PACKAGE=""
ZIP=""
VERSION=""
OUTPUT=""
while [ $# -gt 0 ]; do
    case "$1" in
        --package) [ $# -ge 2 ] || fail "$USAGE"; PACKAGE="${2%/}"; shift 2 ;;
        --zip) [ $# -ge 2 ] || fail "$USAGE"; ZIP="$2"; shift 2 ;;
        --version) [ $# -ge 2 ] || fail "$USAGE"; VERSION="$2"; shift 2 ;;
        --output) [ $# -ge 2 ] || fail "$USAGE"; OUTPUT="$2"; shift 2 ;;
        --print-nsis-pin)
            [ $# -eq 1 ] || fail "$USAGE"
            printf 'nsis_version=%s\nnsis_zip_sha256=%s\nnsis_zip_url=%s\n' "$NSIS_VERSION" "$NSIS_ZIP_SHA256" "$NSIS_ZIP_URL"
            exit 0 ;;
        *) fail "$USAGE" ;;
    esac
done
[ -z "$PACKAGE" ] || [ -z "$ZIP" ] || fail "give --package or --zip, not both."

# A path as the native tools of this platform read it: Windows form under Git Bash, unchanged elsewhere.
native() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s\n' "$1"; fi; }
absolute() { case "$1" in /*) printf '%s\n' "$1" ;; *) printf '%s\n' "$PWD/$1" ;; esac; }

# Python 3: macOS's own (as verify-mac-app.sh uses), else python3, else python (the Windows runner). Each candidate
# must run: on Windows, python3 can be a Microsoft Store stub that fails.
PYTHON=""
for candidate in /usr/bin/python3 python3 python; do
    if command -v "$candidate" >/dev/null 2>&1 && "$candidate" -c 'import sys; sys.exit(sys.version_info[0] != 3)' >/dev/null 2>&1; then
        PYTHON="$candidate"; break
    fi
done
[ -n "$PYTHON" ] || fail "Python 3 not found (macOS: xcode-select --install; Windows: python on PATH)."

MAKENSIS="${MAKENSIS:-makensis}"
command -v "$MAKENSIS" >/dev/null 2>&1 \
    || fail "makensis not found: install NSIS $NSIS_VERSION (macOS: brew install makensis; Windows: nsis-$NSIS_VERSION.zip, SHA-256 $NSIS_ZIP_SHA256) or set MAKENSIS."
found="$("$MAKENSIS" -VERSION 2>&1 | tr -d '\r')" || true
[ "$found" = "v$NSIS_VERSION" ] \
    || fail "$MAKENSIS -VERSION printed '$found', but the pin is NSIS $NSIS_VERSION (NSIS_VERSION and NSIS_ZIP_SHA256 in scripts/build-win-setup.sh). Install NSIS $NSIS_VERSION, or bump the pin and CI's zip together, with a decision (D98, D99)."

CSPROJ_VERSION="$(awk -F'[<>]' '/<Version>/ { print $3; exit }' "$ROOT/DialShift.App/DialShift.App.csproj")"
[[ "$CSPROJ_VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "could not read a <Version>x.y.z</Version> from DialShift.App/DialShift.App.csproj."
[ -n "$VERSION" ] || VERSION="$CSPROJ_VERSION"
if [[ ! "$VERSION" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$ ]]; then
    fail "--version must be SemVer MAJOR.MINOR.PATCH[-prerelease] without build metadata (got '$VERSION')."
fi
[ "${VERSION%%-*}" = "$CSPROJ_VERSION" ] \
    || fail "--version $VERSION does not match the csproj <Version>$CSPROJ_VERSION</Version>; bump the csproj first (D53)."

WORK="$(mktemp -d "${TMPDIR:-/tmp}/dialshift-setup.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK/generated"

if [ -n "$ZIP" ]; then
    [ -f "$ZIP" ] || fail "zip not found: $ZIP"
    PACKAGE="$WORK/package"
    # Python's zipfile, with backslash entry names read as folders: Compress-Archive on Windows may write either.
    "$PYTHON" - "$(native "$ZIP")" "$(native "$PACKAGE")" <<'PY' || fail "could not extract $ZIP."
import os, sys, zipfile
source, target = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(source) as archive:
    for entry in archive.infolist():
        parts = [p for p in entry.filename.replace("\\", "/").split("/") if p]
        if not parts or any(p in (".", "..") for p in parts) or ":" in parts[0]:
            sys.exit("unsafe entry name in the zip: %r" % entry.filename)
        path = os.path.join(target, *parts)
        if entry.filename.endswith(("/", "\\")):
            os.makedirs(path, exist_ok=True)
            continue
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with archive.open(entry) as src, open(path, "wb") as dst:
            while True:
                chunk = src.read(1 << 20)
                if not chunk:
                    break
                dst.write(chunk)
PY
fi
[ -n "$PACKAGE" ] || PACKAGE="$ROOT/artifacts/DialShift-win-x64"
[ -d "$PACKAGE" ] || fail "package folder not found: $PACKAGE (build it with scripts/build.ps1, or pass --package or --zip)."
PACKAGE="$(cd "$PACKAGE" && pwd)"
for item in DialShift.exe DialShift.dll app-catalog.json; do
    [ -f "$PACKAGE/$item" ] || fail "the package $PACKAGE has no $item; build it with scripts/build.ps1 (it verifies the package)."
done

[ -n "$OUTPUT" ] || OUTPUT="$ROOT/artifacts/DialShift-Setup-win-x64.exe"
OUTPUT="$(absolute "$OUTPUT")"
mkdir -p "$(dirname "$OUTPUT")"
rm -f "$OUTPUT"

echo "Building DialShift Setup $VERSION (NSIS $NSIS_VERSION) from $PACKAGE"
# Checks DialShift.dll's version and writes the three generated include files.
"$PYTHON" - "$(native "$PACKAGE")" "$(native "$ROOT/licenses/NSIS-COPYING.txt")" "$(native "$WORK/generated")" \
    "$VERSION" "${VERSION%%-*}" "$(native "$ROOT/DialShift.App/Assets/dialshift.ico")" "$(native "$OUTPUT")" <<'PY' \
    || exit 1
import math, os, re, sys

package, copying, generated, version, numeric, icon, output = sys.argv[1:8]

# The informational version is a length-prefixed UTF-8 string in DialShift.dll's AssemblyInformationalVersion
# attribute blob (prolog 01 00, length, text, no named arguments 00 00): <version> or <version>+<commit>.
with open(os.path.join(package, "DialShift.dll"), "rb") as f:
    dll = f.read()
blob = re.compile(rb"\x01\x00([\x01-\x7f])([0-9]+\.[0-9]+\.[0-9]+[0-9A-Za-z.+-]*)\x00\x00")
# The AssemblyFileVersion attribute (four numbers) has the same shape; it is not the informational version.
found = sorted({m.group(2).decode() for m in blob.finditer(dll)
                if m.group(1)[0] == len(m.group(2)) and not re.fullmatch(rb"[0-9]+(\.[0-9]+){3}", m.group(2))})
if not any(v == version or v.startswith(version + "+") for v in found):
    sys.exit("error: %s carries version %s, not %s; build the package with scripts/build.ps1 -Version %s."
             % (os.path.join(package, "DialShift.dll"), ", ".join(found) or "<none found>", version, version))

def checked(text, what):
    # Names become NSIS strings: no variables, quotes or control characters.
    if any(c in text for c in '$"`') or any(ord(c) < 32 for c in text):
        sys.exit("error: %s %r has a character the setup script cannot carry ($, quotes or a control character)." % (what, text))
    return text

files = {}   # relative path with "/" -> source path
folders = set()
for current, dirs, names in os.walk(package):
    dirs.sort()
    relative = os.path.relpath(current, package).replace(os.sep, "/")
    relative = "" if relative == "." else relative
    if relative:
        folders.add(relative)
    for name in names + dirs:
        if os.path.islink(os.path.join(current, name)):
            sys.exit("error: %s is a link; the package must hold plain files." % os.path.join(current, name))
    if not names and not dirs:
        sys.exit("error: %s is an empty folder; the setup installs files only." % current)
    for name in names:
        path = (relative + "/" if relative else "") + name
        if path == "Install.ps1":
            continue   # it would refuse to run from the install folder anyway (D56)
        files[checked(path, "file name")] = os.path.join(current, name)
if "licenses/NSIS-COPYING.txt" in files:
    sys.exit("error: the package already has licenses/NSIS-COPYING.txt; the setup adds it (brief 4 section 10).")
files["licenses/NSIS-COPYING.txt"] = copying
folders.add("licenses")
for source in list(files.values()) + [icon, output]:
    checked(source, "path")

total = sum(os.path.getsize(source) for source in files.values())
estimated_kb = math.ceil(total / 1024)

def nsis(path):
    return path.replace("/", "\\")

def write(name, lines):
    # UTF-8 with a byte order mark, so makensis reads the same text on every host.
    with open(os.path.join(generated, name), "w", encoding="utf-8-sig", newline="\n") as f:
        f.write("\n".join(lines) + "\n")

write("defines.nsh", [
    "!define VERSION \"%s\"" % version,
    "!define VERSION_NUMERIC \"%s\"" % numeric,
    "!define ESTIMATED_SIZE_KB %d" % estimated_kb,
    "!define ICON \"%s\"" % icon,
    "!define OUTPUT \"%s\"" % output,
])

# Folder by folder in code-point order, files by name: the order is the setup's, not the file system's (D98).
by_folder = {}
for path in files:
    folder, _, name = path.rpartition("/")
    by_folder.setdefault(folder, []).append(path)
install = []
for folder in sorted(by_folder):
    install.append("SetOutPath \"$Staging%s\"" % ("\\" + nsis(folder) if folder else ""))
    for path in sorted(by_folder[folder]):
        install.append("File \"%s\"" % files[path])
write("install-files.nsh", install)

uninstall = []
for folder in sorted(by_folder):
    for path in sorted(by_folder[folder]):
        uninstall += ["Push \"%s\"" % nsis(path), "Call un.DeleteListed"]
for folder in sorted(folders, key=lambda f: (-f.count("/"), f)):
    uninstall.append("RMDir \"$INSTDIR\\%s\"" % nsis(folder))
write("uninstall-files.nsh", uninstall)

print("Payload: %d files in %d folders, %d bytes (EstimatedSize %d KiB); DialShift.dll version %s"
      % (len(files), len(folders), total, estimated_kb, ", ".join(found)))
PY

# MSYS must not rewrite the arguments of the Windows compiler (Git Bash).
MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*' "$MAKENSIS" -WX -V2 "-DGENERATED=$(native "$WORK/generated")" \
    "$(native "$ROOT/scripts/windows-setup/DialShift.nsi")" \
    || fail "makensis failed (see above); with -WX every warning is an error."
[ -f "$OUTPUT" ] || fail "makensis wrote no $OUTPUT."

bash "$ROOT/scripts/verify-win-setup.sh" "$OUTPUT" --version "$VERSION" --package "$PACKAGE"

if command -v sha256sum >/dev/null 2>&1; then digest="$(sha256sum "$OUTPUT" | cut -d' ' -f1)"
else digest="$(shasum -a 256 "$OUTPUT" | cut -d' ' -f1)"; fi
echo "Built: $OUTPUT"
echo "Size: $(wc -c <"$OUTPUT" | tr -d ' ') bytes"
echo "SHA-256: $digest"
