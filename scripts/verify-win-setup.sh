#!/usr/bin/env bash
# Verifies DialShift-Setup-win-x64.exe without running it (brief 4 §7, docs/windows-installer.md; decision D98;
# acceptance rows INS-04, INS-05).
#
# Usage: scripts/verify-win-setup.sh <DialShift-Setup-win-x64.exe> [--version <semver>] [--package <folder>]
#                                    [--require-contents]
#
# Always checked, from the file's bytes:
#   1. PE: MZ, PE\0\0, machine 0x14c (i386: NSIS ships x86 stubs only), optional-header magic 0x10b, subsystem 2 (GUI).
#   2. The NSIS first header at the end of the PE image (512-byte aligned): 0xDEADBEEF, NullsoftInst, and image end +
#      data length = file size (nothing cut off, nothing appended; an Authenticode signature would append data and
#      needs this rule revisited, NC-05 (b)).
#   3. The integrity CRC: zlib CRC-32 of bytes [512, size - 4) equals the last 4 bytes.
#   4. The version resource: ProductName DialShift, CompanyName DialShift, FileDescription DialShift Setup,
#      ProductVersion = --version (default the csproj <Version>), FileVersion and the fixed file and product versions
#      MAJOR.MINOR.PATCH.0.
#   5. The manifest: asInvoker, dpiAware true, and "Nullsoft Install System v<the pin of build-win-setup.sh>".
#   6. The icon: the group icon's frames are dialshift.ico's frames, byte for byte, in the same order.
# With --package and 7-Zip (7z, 7zz, or $SEVEN_ZIP): the installer is listed and extracted, and its payload must equal
# the package minus Install.ps1 plus licenses/NSIS-COPYING.txt: the same paths, sizes and CRC-32; DialShift.exe,
# app-catalog.json, libvlc/win-x64/libvlc.dll, THIRD-PARTY-NOTICES.md and every licence text present, Install.ps1
# absent. The only other entries allowed are exactly NSIS's own $PLUGINSDIR/System.dll, $PLUGINSDIR/nsDialogs.dll and
# $PLUGINSDIR/modern-wizard.bmp, the Welcome/Finish image at the other display scales ($PLUGINSDIR/dialshift-wizard-<scale>.bmp,
# D104), and an uninstaller entry, if a 7-Zip version shows one. Without 7-Zip the contents are reported
# as not checked and the script still passes, unless --require-contents (CI) is given.
# Not checkable without running it: the install logic, which build.yml's setup cases run on windows-latest, and the
# wizard, SmartScreen and Apps & features, which NC-19 checks by hand.
#
# Prints "verified: ..." and exits 0, or "error: ..." and exits 1; 2 for a usage error. bash 3.2+ and Python 3
# (standard library only): macOS (/usr/bin/python3) and Git Bash on Windows (python); $PYTHON names another.
set -euo pipefail

usage() { echo "usage: scripts/verify-win-setup.sh <setup.exe> [--version <semver>] [--package <folder>] [--require-contents]" >&2; exit 2; }
fail() { echo "error: $*" >&2; exit 1; }

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
# A path as the native tools of this platform read it: Windows form under Git Bash, unchanged elsewhere.
native() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s\n' "$1"; fi; }
SETUP=""
VERSION=""
PACKAGE=""
REQUIRE_CONTENTS=false
while [ $# -gt 0 ]; do
    case "$1" in
        --version) [ $# -ge 2 ] || usage; VERSION="$2"; shift 2 ;;
        --package) [ $# -ge 2 ] || usage; PACKAGE="${2%/}"; shift 2 ;;
        --require-contents) REQUIRE_CONTENTS=true; shift ;;
        -*) usage ;;
        *) [ -z "$SETUP" ] || usage; SETUP="$1"; shift ;;
    esac
done
[ -n "$SETUP" ] || usage
[ -f "$SETUP" ] || fail "setup not found: $SETUP"
[ -z "$PACKAGE" ] || [ -d "$PACKAGE" ] || fail "package folder not found: $PACKAGE"
if [ -z "$VERSION" ]; then
    VERSION="$(awk -F'[<>]' '/<Version>/ { print $3; exit }' "$ROOT/DialShift.App/DialShift.App.csproj")"
fi
NSIS_VERSION="$(bash "$ROOT/scripts/build-win-setup.sh" --print-nsis-pin | awk -F= '$1 == "nsis_version" { print $2 }')"
[ -n "$NSIS_VERSION" ] || fail "could not read the NSIS pin from scripts/build-win-setup.sh."

# Python 3: $PYTHON, else /usr/bin/python3, python3 or python (as build-win-setup.sh finds it).
python_candidates=(/usr/bin/python3 python3 python)
if [ -n "${PYTHON:-}" ]; then python_candidates=("$PYTHON"); fi
PYTHON=""
for candidate in "${python_candidates[@]}"; do
    if command -v "$candidate" >/dev/null 2>&1 && "$candidate" -c 'import sys; sys.exit(sys.version_info[0] != 3)' >/dev/null 2>&1; then
        PYTHON="$candidate"; break
    fi
done
[ -n "$PYTHON" ] || fail "Python 3 not found (macOS: xcode-select --install; Windows: python on PATH; or set PYTHON)."

SEVEN_ZIP_TOOL=""
if [ -n "$PACKAGE" ]; then
    for candidate in ${SEVEN_ZIP:+"$SEVEN_ZIP"} 7z 7zz; do
        if command -v "$candidate" >/dev/null 2>&1; then SEVEN_ZIP_TOOL="$(native "$(command -v "$candidate")")"; break; fi
    done
    if [ -z "$SEVEN_ZIP_TOOL" ] && $REQUIRE_CONTENTS; then
        fail "7-Zip not found (7z, 7zz or \$SEVEN_ZIP), and --require-contents was given."
    fi
fi
TEMP_ROOT="${TMPDIR:-/tmp}"
WORK="$(mktemp -d "${TEMP_ROOT%/}/dialshift-verify-setup.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

"$PYTHON" - "$(native "$SETUP")" "$VERSION" "$NSIS_VERSION" "$(native "$ROOT/DialShift.App/Assets/dialshift.ico")" \
    "${PACKAGE:+$(native "$PACKAGE")}" "$(native "$ROOT/licenses/NSIS-COPYING.txt")" "$SEVEN_ZIP_TOOL" \
    "$(native "$WORK")" <<'PY'
import os, re, struct, subprocess, sys, zlib

setup, version, nsis_version, icon_path, package, copying, seven_zip, work = sys.argv[1:9]
name = os.path.basename(setup)

def fail(message):
    print("error: %s: %s" % (name, message), file=sys.stderr)
    sys.exit(1)

with open(setup, "rb") as f:
    data = f.read()
size = len(data)

# 1. PE
if size < 1024 or data[:2] != b"MZ":
    fail("not a Windows executable (no MZ header).")
pe = struct.unpack_from("<I", data, 0x3C)[0]
if pe + 24 > size or data[pe:pe + 4] != b"PE\0\0":
    fail("not a PE executable.")
machine, sections, _, _, _, optional_size, _ = struct.unpack_from("<HHIIIHH", data, pe + 4)
if machine != 0x14C:
    fail("machine is 0x%04X, expected 0x014C (i386): not an NSIS setup." % machine)
optional = pe + 24
magic = struct.unpack_from("<H", data, optional)[0]
if magic != 0x10B:
    fail("optional-header magic is 0x%X, expected 0x10B (PE32)." % magic)
subsystem = struct.unpack_from("<H", data, optional + 68)[0]
if subsystem != 2:
    fail("subsystem is %d, expected 2 (Windows GUI)." % subsystem)
resource_rva = struct.unpack_from("<I", data, optional + 96 + 2 * 8)[0]
table = optional + optional_size
section_list = []
image_end = 0
for i in range(sections):
    virtual_size, virtual_address, raw_size, raw_pointer = struct.unpack_from("<IIII", data, table + i * 40 + 8)
    section_list.append((virtual_address, max(virtual_size, raw_size), raw_pointer))
    image_end = max(image_end, raw_pointer + raw_size)

# 2. NSIS first header right after the image, and nothing after the data it announces.
if image_end % 512 or image_end + 28 > size:
    fail("no NSIS data after the PE image (image ends at %d of %d bytes)." % (image_end, size))
flags, signature, magic_text, header_length, data_length = struct.unpack_from("<II12sII", data, image_end)
if signature != 0xDEADBEEF or magic_text != b"NullsoftInst":
    fail("no NSIS first header (0xDEADBEEF, NullsoftInst) at the end of the PE image (offset %d)." % image_end)
if image_end + data_length != size:
    fail("the file is %d bytes, but the NSIS data announces %d (image end %d + data %d): the file was cut off or "
         "has data appended." % (size, image_end + data_length, image_end, data_length))

# 3. Integrity CRC (NSIS checks it at startup and refuses a damaged setup).
stored = struct.unpack_from("<I", data, size - 4)[0]
computed = zlib.crc32(data[512:size - 4]) & 0xFFFFFFFF
if stored != computed:
    fail("integrity CRC mismatch: stored 0x%08X, computed 0x%08X over bytes [512, %d): the file is damaged."
         % (stored, computed, size - 4))

# Resources: type -> id -> first language's bytes.
def file_offset(rva):
    for address, length, pointer in section_list:
        if address <= rva < address + length:
            return rva - address + pointer
    fail("resource RVA 0x%X is outside every section." % rva)
if not resource_rva:
    fail("no resources.")
base = file_offset(resource_rva)
def directory(offset):
    named, ids = struct.unpack_from("<HH", data, base + offset + 12)
    for i in range(named + ids):
        key, target = struct.unpack_from("<II", data, base + offset + 16 + i * 8)
        yield key, target
resources = {}
for type_id, type_target in directory(0):
    if not type_target & 0x80000000:
        continue
    for res_id, id_target in directory(type_target & 0x7FFFFFFF):
        if not id_target & 0x80000000:
            continue
        for _, leaf in directory(id_target & 0x7FFFFFFF):
            rva, length = struct.unpack_from("<II", data, base + leaf)
            start = file_offset(rva)
            resources.setdefault(type_id, {}).setdefault(res_id, data[start:start + length])
            break

# 4. Version resource.
numeric = version.split("-")[0] + ".0"
if 16 not in resources:
    fail("no version resource.")
info = next(iter(resources[16].values()))
def node(offset):
    length, value_length, kind = struct.unpack_from("<HHH", info, offset)
    end_key = offset + 6
    while info[end_key:end_key + 2] != b"\0\0":
        end_key += 2
    key = info[offset + 6:end_key].decode("utf-16-le")
    value_start = (end_key + 2 + 3) & ~3
    value_bytes = value_length * 2 if kind == 1 else value_length
    value = info[value_start:value_start + value_bytes]
    children = []
    child = (value_start + value_bytes + 3) & ~3
    while child < offset + length:
        child_node, child_length = node(child)
        children.append(child_node)
        child = (child + child_length + 3) & ~3
    return (key, value, kind, children), length
root, _ = node(0)
if root[0] != "VS_VERSION_INFO" or len(root[1]) < 52 or struct.unpack_from("<I", root[1], 0)[0] != 0xFEEF04BD:
    fail("the version resource has no VS_FIXEDFILEINFO.")
def dotted(ms, ls):
    return "%d.%d.%d.%d" % (ms >> 16, ms & 0xFFFF, ls >> 16, ls & 0xFFFF)
file_ms, file_ls, product_ms, product_ls = struct.unpack_from("<IIII", root[1], 8)
strings = {}
for child in root[3]:
    if child[0] == "StringFileInfo":
        for string_table in child[3]:
            for key, value, kind, _ in string_table[3]:
                strings[key] = value.decode("utf-16-le").rstrip("\0")
expected = {"ProductName": "DialShift", "CompanyName": "DialShift", "FileDescription": "DialShift Setup",
            "ProductVersion": version, "FileVersion": numeric}
for key, wanted in expected.items():
    if strings.get(key) != wanted:
        fail("version resource %s is %r, expected %r." % (key, strings.get(key), wanted))
for label, value in (("file", dotted(file_ms, file_ls)), ("product", dotted(product_ms, product_ls))):
    if value != numeric:
        fail("fixed %s version is %s, expected %s." % (label, value, numeric))

# 5. Manifest.
if 24 not in resources:
    fail("no manifest.")
manifest = next(iter(resources[24].values())).decode("utf-8", "replace")
if not re.search(r'requestedExecutionLevel[^>]*level="asInvoker"', manifest):
    fail("the manifest does not request asInvoker (a per-user setup must never ask for administrator rights).")
if not re.search(r"<dpiAware[^>]*>\s*true\s*</dpiAware>", manifest):
    fail("the manifest does not declare dpiAware true.")
if "Nullsoft Install System v%s<" % nsis_version not in manifest:
    found = re.search(r"Nullsoft Install System (v[^<]*)", manifest)
    fail("built with NSIS %s, but the pin is %s (scripts/build-win-setup.sh)." % (found.group(1) if found else "?", nsis_version))

# 6. Icon.
with open(icon_path, "rb") as f:
    ico = f.read()
_, _, frame_count = struct.unpack_from("<HHH", ico, 0)
frames = []
for i in range(frame_count):
    width, height, _, _, _, _, length, offset = struct.unpack_from("<BBBBHHII", ico, 6 + i * 16)
    frames.append(((width or 256), (height or 256), ico[offset:offset + length]))
groups = resources.get(14, {})
if len(groups) != 1:
    fail("expected one group icon, found %d." % len(groups))
group = next(iter(groups.values()))
count = struct.unpack_from("<H", group, 4)[0]
icons = []
for i in range(count):
    width, height, _, _, _, _, length, icon_id = struct.unpack_from("<BBBBHHIH", group, 6 + i * 14)
    icons.append(((width or 256), (height or 256), resources.get(3, {}).get(icon_id, b"")))
if [(w, h) for w, h, _ in icons] != [(w, h) for w, h, _ in frames]:
    fail("the icon frames are %s, expected dialshift.ico's %s." % ([w for w, _, _ in icons], [w for w, _, _ in frames]))
for (width, _, got), (_, _, wanted) in zip(icons, frames):
    if got != wanted:
        fail("the %d px icon frame differs from dialshift.ico's." % width)

summary = ("NSIS %s, PE i386 GUI, %d bytes, integrity CRC 0x%08X, version %s, asInvoker and DPI-aware, %d icon frames"
           % (nsis_version, size, stored, version, len(icons)))

# Contents (7-Zip).
if not package:
    contents = "contents: not checked (no --package)"
elif not seven_zip:
    contents = "contents: not checked (7-Zip not found)"
else:
    def crc_of(path):
        crc = 0
        with open(path, "rb") as f:
            while True:
                chunk = f.read(1 << 20)
                if not chunk:
                    return crc & 0xFFFFFFFF
                crc = zlib.crc32(chunk, crc)
    expected_files = {}
    for current, _, names in os.walk(package):
        relative = os.path.relpath(current, package).replace(os.sep, "/")
        for file_name in names:
            path = file_name if relative == "." else relative + "/" + file_name
            if path != "Install.ps1":
                expected_files[path] = os.path.join(current, file_name)
    expected_files["licenses/NSIS-COPYING.txt"] = copying
    listing = subprocess.run([seven_zip, "l", "-slt", setup], stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    text = listing.stdout.decode("utf-8", "replace")
    if listing.returncode != 0 or "\n----------" not in text.replace("\r", ""):
        fail("7-Zip could not list the setup (exit %d):\n%s" % (listing.returncode, text[-2000:]))
    entries = {}
    current_path = None
    for line in text.replace("\r", "").split("\n----------", 1)[1].split("\n"):
        if line.startswith("Path = "):
            current_path = line[7:].replace("\\", "/")
            entries[current_path] = None
        elif line.startswith("Size = ") and current_path is not None:
            # Empty when 7-Zip only learns it by extracting (the last file of a solid NSIS block).
            entries[current_path] = int(line[7:]) if line[7:].strip() else None
    # The payload's folder is a variable of the script (7-Zip shows it as $_<n>_), the one holding DialShift.exe.
    prefixes = {p.split("/")[0] for p in entries if p.count("/") == 1 and p.endswith("/DialShift.exe")}
    if len(prefixes) != 1:
        fail("7-Zip lists no single payload folder holding DialShift.exe (found %s)." % sorted(prefixes))
    prefix = prefixes.pop() + "/"
    # NSIS's own files: the System plugin, nsDialogs (the Modern UI's pages) and the Welcome/Finish image, at 100 % as
    # modern-wizard.bmp and at the other display scales (D104).
    nsis_files = {"$PLUGINSDIR/System.dll", "$PLUGINSDIR/nsDialogs.dll", "$PLUGINSDIR/modern-wizard.bmp"}
    nsis_files |= {"$PLUGINSDIR/dialshift-wizard-%d.bmp" % scale for scale in (125, 150, 175, 200, 250, 300)}
    listed = {}
    for path, length in entries.items():
        if path == prefix + "Uninstall DialShift.exe":
            continue
        if path.startswith(prefix):
            listed[path[len(prefix):]] = length
        elif path not in nsis_files and not path.endswith("/Uninstall DialShift.exe"):
            fail("unexpected entry outside the payload: %s" % path)
    for required in ("DialShift.exe", "app-catalog.json", "libvlc/win-x64/libvlc.dll", "THIRD-PARTY-NOTICES.md",
                     "licenses/NSIS-COPYING.txt"):
        if required not in listed:
            fail("the payload has no %s." % required)
    if "Install.ps1" in listed:
        fail("the payload holds Install.ps1, which the setup must leave out.")
    missing = sorted(set(expected_files) - set(listed))
    extra = sorted(set(listed) - set(expected_files))
    if missing or extra:
        fail("the payload differs from the package minus Install.ps1 plus licenses/NSIS-COPYING.txt: missing %s; extra %s."
             % (missing[:10] or "none", extra[:10] or "none"))
    for path, length in sorted(listed.items()):
        if length is not None and length != os.path.getsize(expected_files[path]):
            fail("%s is listed with %d bytes in the setup and has %d in the package." % (path, length, os.path.getsize(expected_files[path])))
    target = os.path.join(work, "extracted")
    extraction = subprocess.run([seven_zip, "x", "-y", "-o" + target, setup], stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    if extraction.returncode != 0:
        fail("7-Zip could not extract the setup (exit %d):\n%s" % (extraction.returncode, extraction.stdout.decode("utf-8", "replace")[-2000:]))
    root_folder = os.path.join(target, prefix[:-1])
    for path in sorted(listed):
        got = os.path.join(root_folder, *path.split("/"))
        if not os.path.isfile(got):
            fail("7-Zip did not extract %s." % path)
        if os.path.getsize(got) != os.path.getsize(expected_files[path]):
            fail("%s is %d bytes in the setup and %d in the package." % (path, os.path.getsize(got), os.path.getsize(expected_files[path])))
        if crc_of(got) != crc_of(expected_files[path]):
            fail("%s differs from the package's copy (CRC-32 0x%08X, expected 0x%08X)." % (path, crc_of(got), crc_of(expected_files[path])))
    contents = "contents: %d files equal to the package minus Install.ps1 plus licenses/NSIS-COPYING.txt (paths, sizes, CRC-32; %s)" % (
        len(listed), os.path.basename(seven_zip))

print("verified: %s (%s; %s)" % (setup, summary, contents))
PY
