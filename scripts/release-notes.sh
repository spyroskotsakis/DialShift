#!/usr/bin/env bash
# Prints the GitHub Release notes for a version (decision D53): the version's section of
# CHANGELOG.md, then a footer with the downloads, how to check and install them, the signing
# status and, when a checksum file is given, the SHA-256 sums.
#
# Usage: scripts/release-notes.sh <version> [SHA256SUMS.txt]
#
# Fails when CHANGELOG.md has no "## [<version>]" section or the section is empty, so
# release.yml runs it before building to stop a release that has no notes. In GitHub Actions,
# relative links in the section are rewritten to the files at tag v<version> in the repository
# the workflow runs in (GITHUB_SERVER_URL, GITHUB_REPOSITORY). Nothing names a repository, so
# the same notes work for a dry run on the private repository and for the public release.
# The section carries the pre-release status; GitHub also badges a pre-release.
set -euo pipefail
cd "$(dirname "$0")/.."

fail() { echo "error: $*" >&2; exit 1; }

[ "$#" -ge 1 ] && [ "$#" -le 2 ] || fail "usage: scripts/release-notes.sh <version> [SHA256SUMS.txt]"
VERSION="$1"
SUMS="${2:-}"
[ -z "$SUMS" ] || [ -f "$SUMS" ] || fail "checksum file not found: $SUMS"

# The section runs from its heading to the next version heading or the link references at the end.
section="$(awk -v heading="## [$VERSION]" '
    index($0, heading) == 1 { found = 1; next }
    !found { next }
    /^## \[/ || /^\[[^]]+\]: / { exit }
    !started && /^[[:space:]]*$/ { next }
    { started = 1; print }
' CHANGELOG.md)"
[ -n "$(tr -d '[:space:]' <<<"$section")" ] || fail "CHANGELOG.md has no non-empty '## [$VERSION]' section."

# A release page resolves no relative links: in Actions, point them at the files at this tag.
if [ -n "${GITHUB_REPOSITORY:-}" ]; then
    base="${GITHUB_SERVER_URL:-https://github.com}/$GITHUB_REPOSITORY/blob/v$VERSION" \
        perl -pe 's{\]\((?![a-z]+:|#)([^)\s]+)\)}{]($ENV{base}/$1)}g' <<<"$section"
else
    printf '%s\n' "$section"
fi
cat <<EOF

---

## Download

| File | For |
|---|---|
| \`DialShift-win-x64.zip\` | Windows 10/11, x64. Self-contained: .NET and VLC are included. |
| \`DialShift-macos-arm64.zip\` | macOS 14.0 or later, Apple Silicon only (there is no Intel build). The native \`osx-arm64\` build with Apple's AVPlayer (\`native-avplayer\`); .NET is included. |
| \`SHA256SUMS.txt\` | SHA-256 checksums of both zips. |

### Check the download

On macOS, in the folder with the downloads:

\`\`\`sh
shasum -a 256 -c SHA256SUMS.txt --ignore-missing
\`\`\`

On Windows, in PowerShell, in the folder with the downloads (\`True\` means the file is intact):

\`\`\`powershell
\$expected = ((Select-String -Path SHA256SUMS.txt -Pattern 'DialShift-win-x64.zip' -SimpleMatch).Line -split '\s+')[0]
(Get-FileHash DialShift-win-x64.zip -Algorithm SHA256).Hash -eq \$expected
\`\`\`

### Install

**Windows:** extract the zip and run \`DialShift.exe\` from the extracted folder (keep the folder together). Optional: right-click \`Install.ps1\` and choose **Run with PowerShell** to install for your user, with a Start menu shortcut and no administrator rights. The build is not code-signed, so SmartScreen may say "Windows protected your PC": choose **More info**, then **Run anyway**.

**macOS:** unzip, move \`DialShift.app\` to \`/Applications\` and open it. The app is ad-hoc signed and not notarized, so macOS blocks the first launch: open **System Settings → Privacy & Security** and choose **Open Anyway** (on older macOS, right-click the app and choose **Open**). DialShift lives in the menu bar; it has no Dock icon.

### Signing status

- Windows: unsigned (no Authenticode signature).
- macOS: ad-hoc signed; not signed with a Developer ID and not notarized by Apple.
EOF
if [ -n "$SUMS" ]; then
    cat <<EOF

### SHA-256

\`\`\`text
$(cat "$SUMS")
\`\`\`
EOF
fi
