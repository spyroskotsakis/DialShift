#!/usr/bin/env bash
# Backup release path (decision D58): builds, verifies and, with --publish, publishes the GitHub Release of a version
# tag from the maintainer's Apple Silicon Mac, for when GitHub Actions cannot run release.yml (for example when the
# account's Actions minutes are refused). The normal path stays the tag push and release.yml (D53).
#
# It makes the release release.yml would: the tag's code and scripts, the same checks where a Mac can run them, the
# same assets (DialShift-win-x64.zip, DialShift-Setup-win-x64.exe for tags from brief 4 on, DialShift-macos-arm64.zip,
# SHA256SUMS.txt), the notes of scripts/release-notes.sh and the same gh release create call. The notes end with a
# line saying the release was built and uploaded locally and which checks ran.
#
# Usage: scripts/release-local.sh <tag> [--repo <owner/name>] [--win-zip <path>] [--publish]
#
#   <tag>       an existing local tag vMAJOR.MINOR.PATCH[-prerelease]. It is built in a temporary git worktree at the
#               tag, so this checkout is not touched.
#   --repo      the repository to publish to. Default: spyroskotsakis/DialShift, the public repository.
#               spyroskotsakis/dialshift-dev is the private dry run.
#   --win-zip   a DialShift-win-x64.zip built on Windows at the tag (scripts/build.ps1 -Version <version>), used instead
#               of cross-building the Windows package here with PowerShell 7 (pwsh; brew install powershell).
#               The Windows setup is built from the zip that is uploaded, either way, with Homebrew's makensis at the
#               version the tag's scripts/build-win-setup.sh pins (brew install makensis; D99).
#   --publish   create the release. Without it, a dry run: everything is built and verified, the assets and the notes
#               are written, and the gh command is printed; nothing is uploaded.
#
# Checks before any build: the tag format (release.yml's regex), the tag exists here, this checkout is clean, the
# tag's MAJOR.MINOR.PATCH equals the csproj <Version>, CHANGELOG.md has the version's section; with --publish also a
# gh login, the tag on the target repository at the same commit, and no release for it there yet.
# Then, like build.yml: build (-warnaserror) -> tests -> package (Windows, macOS) -> verify the zips that are
# uploaded -> the Windows setup from the uploaded zip and its verification (tags from brief 4 on; the contents only
# with 7-Zip: brew install sevenzip) -> macOS native smoke (build output, --recovery-test) and bundle smoke (the app
# unzipped from the release zip) -> the CAT-03 package verifier fixtures (tags from brief 3 on) and the setup
# verifier fixtures (from brief 4 on). Not run here: the tests' Windows-only checks (SKIP on macOS), the Windows
# native smoke, build.yml's Install.ps1 cases and its setup install, upgrade and uninstall cases.
# CFBundleVersion: the number of the release.yml run that the tag push started on the target repository (a run whose
# jobs GitHub refused still has one), else the next run number, so the build sorts like a CI release (D53).
# Output: artifacts/release-local/<tag>/ in this checkout (git-ignored).
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT="$PWD"

fail() { echo "error: $*" >&2; exit 1; }
phase() { echo; echo "== $* =="; }

TAG=""
REPO="spyroskotsakis/DialShift"
WIN_ZIP=""
PUBLISH=false
while [ $# -gt 0 ]; do
    case "$1" in
        --repo) [ $# -ge 2 ] || fail "--repo needs a value"; REPO="$2"; shift 2 ;;
        --win-zip) [ $# -ge 2 ] || fail "--win-zip needs a value"; WIN_ZIP="$2"; shift 2 ;;
        --publish) PUBLISH=true; shift ;;
        -*) fail "unknown option '$1' (usage: scripts/release-local.sh <tag> [--repo <owner/name>] [--win-zip <path>] [--publish])" ;;
        *) [ -z "$TAG" ] || fail "only one tag, got '$TAG' and '$1'"; TAG="$1"; shift ;;
    esac
done
[ -n "$TAG" ] || fail "usage: scripts/release-local.sh <tag> [--repo <owner/name>] [--win-zip <path>] [--publish]"

export PATH="$HOME/.dotnet:$PATH" DOTNET_NOLOGO=true DOTNET_CLI_TELEMETRY_OPTOUT=true
# The smokes run as in CI: in a fresh temp data folder, with the system audio output.
unset DIALSHIFT_DATA_DIR DIALSHIFT_AUDIO_OUTPUT

phase "Checking the tag, version and changelog"
[[ "$REPO" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] || fail "--repo must be <owner>/<name> (got '$REPO')."
if [[ ! "$TAG" =~ ^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$ ]]; then
    fail "'$TAG' is not vMAJOR.MINOR.PATCH or vMAJOR.MINOR.PATCH-<prerelease> (for example v0.3.0 or v0.3.0-rc.1)."
fi
[ "$(uname -s)" = Darwin ] && [ "$(uname -m)" = arm64 ] || fail "release-local.sh runs on an Apple Silicon Mac."
for tool in git gh dotnet jq perl shasum unzip; do
    command -v "$tool" >/dev/null 2>&1 || fail "required tool not found: $tool"
done
if [ -z "$WIN_ZIP" ] && ! command -v pwsh >/dev/null 2>&1; then
    fail "PowerShell 7 (pwsh) is needed to build the Windows package on this Mac: install it (brew install powershell), or build DialShift-win-x64.zip on Windows at $TAG (scripts/build.ps1 -Version <version>) and pass --win-zip <path>."
fi
if [ -n "$WIN_ZIP" ]; then
    [ -f "$WIN_ZIP" ] || fail "--win-zip: file not found: $WIN_ZIP"
    WIN_ZIP="$(cd "$(dirname "$WIN_ZIP")" && pwd)/$(basename "$WIN_ZIP")"
fi
COMMIT="$(git rev-parse -q --verify "refs/tags/$TAG^{commit}")" || fail "tag $TAG does not exist in this clone."
# This script comes from the checkout, the build from the tag: the script that runs must be the committed one.
[ -z "$(git status --porcelain)" ] || fail "the working tree has changes; commit or stash them first."
VERSION="${TAG#v}"
PRERELEASE=false
if [[ "$VERSION" == *-* ]]; then PRERELEASE=true; fi

# /tmp, not $TMPDIR: macOS's per-user $TMPDIR is /var/folders/<2 random characters>/..., and the tests of tags up to
# v0.3.0 fail when the source path has a folder starting with x or y (a redaction check in DialShift.Tests looks for
# "/x" and "/y" in an exception text that includes the stack trace's source paths).
WORK="$(mktemp -d /tmp/dialshift-release.XXXXXX)"
SRC="$WORK/src"
cleanup() {
    cd "$ROOT"
    git -C "$ROOT" worktree remove --force "$SRC" >/dev/null 2>&1 || true
    rm -rf "$WORK"
    git -C "$ROOT" worktree prune
}
trap cleanup EXIT
git worktree add --quiet --detach "$SRC" "$TAG"
cd "$SRC"

csproj="$(awk -F'[<>]' '/<Version>/ { print $3; exit }' DialShift.App/DialShift.App.csproj)"
[ "${VERSION%%-*}" = "$csproj" ] \
    || fail "$TAG is version ${VERSION%%-*}, but DialShift.App/DialShift.App.csproj has <Version>$csproj</Version> at the tag."
scripts/release-notes.sh "$VERSION" >/dev/null \
    || fail "add a '## [$VERSION]' section to CHANGELOG.md at the tagged commit; it becomes the release notes."
LABEL="$(awk '$1 == "MACOS_LABEL:" { print $2; exit }' .github/workflows/build.yml)"
[ -n "$LABEL" ] || fail "no MACOS_LABEL in .github/workflows/build.yml at $TAG."
# The Windows setup exists from brief 4 on. Its build script refuses another makensis version with the pin to bump.
SETUP=false
if [ -f scripts/build-win-setup.sh ]; then
    SETUP=true
    command -v makensis >/dev/null 2>&1 \
        || fail "makensis is needed to build the Windows setup of $TAG: brew install makensis (NSIS $(bash scripts/build-win-setup.sh --print-nsis-pin | awk -F= '$1 == "nsis_version" { print $2 }'))."
fi
echo "Release $TAG ($COMMIT): version $VERSION, pre-release: $PRERELEASE, target: $REPO"

if [ "$PUBLISH" = true ]; then
    phase "Checking $REPO"
    gh auth status --hostname github.com >/dev/null 2>&1 || fail "gh is not logged in: run gh auth login."
    if ! ref="$(gh api "repos/$REPO/git/ref/tags/$TAG" --jq '.object.type + " " + .object.sha' 2>&1)"; then
        grep -q 'HTTP 404' <<<"$ref" || fail "could not read tag $TAG on $REPO: $ref"
        fail "tag $TAG is not on $REPO; the maintainer pushes it first (git push <remote> $TAG)."
    fi
    read -r type sha <<<"$ref"
    if [ "$type" = tag ]; then sha="$(gh api "repos/$REPO/git/tags/$sha" --jq .object.sha)"; fi
    [ "$sha" = "$COMMIT" ] || fail "tag $TAG is at $sha on $REPO but at $COMMIT here."
    if out="$(gh api "repos/$REPO/releases/tags/$TAG" --jq .html_url 2>&1)"; then
        fail "$REPO already has a release for $TAG: $out (delete it first to publish again)."
    fi
    grep -q 'HTTP 404' <<<"$out" || fail "could not check the releases of $REPO: $out"
    echo "$TAG is on $REPO at $COMMIT and has no release yet."
fi

BUILD_NUMBER="$(gh run list --repo "$REPO" --workflow release.yml --branch "$TAG" --limit 1 --json number --jq '.[0].number // empty')"
if [ -z "$BUILD_NUMBER" ]; then
    last="$(gh run list --repo "$REPO" --workflow release.yml --limit 1 --json number --jq '.[0].number // 0')"
    BUILD_NUMBER=$((last + 1))
fi
echo "Build number (CFBundleVersion): $BUILD_NUMBER"

CHECKED=()
NOT_CHECKED=("the tests' Windows-only checks, the Windows native smoke and the Install.ps1 checks (they need Windows)")
if $SETUP; then NOT_CHECKED+=("the setup's install, upgrade and uninstall cases (they need Windows)"); fi

phase "Build DialShift.slnx (-warnaserror)"
dotnet build DialShift.slnx -c Release -warnaserror
CHECKED+=("the build with warnings as errors")

phase "Test DialShift.Tests"
dotnet run --project DialShift.Tests/DialShift.Tests.csproj -c Release
CHECKED+=("the tests")

OUT="$ROOT/artifacts/release-local/$TAG"
rm -rf "$OUT"
mkdir -p "$OUT"

if [ -n "$WIN_ZIP" ]; then
    phase "Windows package from --win-zip"
    cp "$WIN_ZIP" "$OUT/DialShift-win-x64.zip"
    win_source="the Windows zip given with --win-zip"
else
    phase "Package win-x64 zip (scripts/build.ps1 under pwsh)"
    pwsh -NoProfile -File scripts/build.ps1 -SkipTests -Version "$VERSION" \
        || fail "scripts/build.ps1 at $TAG failed (see above). build.ps1 runs on macOS from D58 on; for an older tag, build the zip on Windows (scripts/build.ps1 -Version $VERSION) and pass --win-zip."
    cp artifacts/DialShift-win-x64.zip "$OUT/DialShift-win-x64.zip"
    win_source="the Windows package, cross-built on the Mac with scripts/build.ps1"
fi
# The SDK writes the version and the source commit into the informational version: the zip must come from this tag.
unzip -p "$OUT/DialShift-win-x64.zip" DialShift.dll > "$WORK/DialShift.dll" || fail "DialShift-win-x64.zip has no DialShift.dll."
LC_ALL=C grep -a -q -F "$VERSION+$COMMIT" "$WORK/DialShift.dll" \
    || fail "DialShift-win-x64.zip was not built as version $VERSION from $TAG ($COMMIT)."
if command -v pwsh >/dev/null 2>&1; then
    phase "Verify win-x64 zip (extracted, scripts/verify-win-package.ps1)"
    ZIP="$OUT/DialShift-win-x64.zip" DEST="$WORK/DialShift-win-x64" \
        pwsh -NoProfile -Command 'Expand-Archive -LiteralPath $env:ZIP -DestinationPath $env:DEST'
    pwsh -NoProfile -File scripts/verify-win-package.ps1 -Path "$WORK/DialShift-win-x64"
    CHECKED+=("$win_source (version $VERSION at the tag's commit; verify-win-package.ps1 on the extracted zip)")
else
    echo "warning: pwsh not found: verify-win-package.ps1 did not run on the Windows zip here (build.ps1 verified it before zipping)." >&2
    CHECKED+=("$win_source (version $VERSION at the tag's commit)")
    NOT_CHECKED+=("verify-win-package.ps1 on the extracted Windows zip")
fi

if $SETUP; then
    # From the zip that is uploaded, so the setup and the zip carry the same bytes; the script checks the version and
    # runs verify-win-setup.sh (the contents too when 7-Zip is found).
    phase "Package win-x64 setup (scripts/build-win-setup.sh, from DialShift-win-x64.zip)"
    bash scripts/build-win-setup.sh --zip "$OUT/DialShift-win-x64.zip" --version "$VERSION" --output "$OUT/DialShift-Setup-win-x64.exe"
    if command -v 7z >/dev/null 2>&1 || command -v 7zz >/dev/null 2>&1; then
        CHECKED+=("the Windows setup, built with makensis $(makensis -VERSION) from the uploaded zip (verify-win-setup.sh, contents compared with 7-Zip)")
    else
        CHECKED+=("the Windows setup, built with makensis $(makensis -VERSION) from the uploaded zip (verify-win-setup.sh static checks)")
        NOT_CHECKED+=("the setup's contents (7-Zip not found: brew install sevenzip)")
    fi
fi

phase "Package osx-arm64 app bundle and zip"
MACOS_LABEL="$LABEL" scripts/build-mac-app.sh --version "$VERSION" --build-number "$BUILD_NUMBER"
cp "dist/DialShift-osx-arm64-$LABEL.zip" "$OUT/DialShift-macos-arm64.zip"

phase "Verify osx-arm64 zip"
scripts/verify-mac-app.sh --zip "$OUT/DialShift-macos-arm64.zip"
CHECKED+=("the macOS zip verification (ditto and unzip)")

# Both smokes: the app's own watchdog ends a stuck run; the alarm ends a run that hangs past it.
smoke() {
    local output="$1"; shift
    local code=0
    perl -e 'alarm shift; exec @ARGV or die "exec: $!\n"' 360 "$@" --output "$output" || code=$?
    [ -f "$output/results.json" ] || fail "the smoke run exited with code $code and wrote no results.json."
    jq -r '.checks[] | select(.passed | not) | "failed: \(.name): \(.detail)"' "$output/results.json" >&2
    [ "$code" -eq 0 ] || fail "the smoke run exited with code $code (results: $output)."
    jq -r '"\([.checks[] | select(.passed)] | length)/\(.checks | length) checks passed (\(.engine))"' "$output/results.json"
}

phase "Native UI smoke (build output, --smoke-test --recovery-test)"
dotnet build DialShift.App/DialShift.App.csproj -c Release -r osx-arm64 -warnaserror -o "$WORK/smoke-app"
smoke "$WORK/smoke" "$WORK/smoke-app/DialShift" --smoke-test --recovery-test
CHECKED+=("the macOS native smoke (--smoke-test --recovery-test)")

phase "Bundle smoke (the app unzipped from DialShift-macos-arm64.zip, --smoke-test)"
unzip -q "$OUT/DialShift-macos-arm64.zip" -d "$WORK/bundle"
smoke "$WORK/bundle-smoke" "$WORK/bundle/DialShift.app/Contents/MacOS/DialShift" --smoke-test
CHECKED+=("the bundle smoke of the app unzipped from DialShift-macos-arm64.zip")

# build.yml's CAT-03 steps (brief 3): the tag's own fixture script, on the bundle and, when pwsh extracted it above,
# on the Windows package. Tags before brief 3 have no station catalog and no such script.
if [ -f scripts/test-package-verifiers.sh ]; then
    phase "Package verifier fixtures (CAT-03, scripts/test-package-verifiers.sh)"
    fixture_args=(--mac-app dist/DialShift.app)
    fixture_packages="the macOS bundle"
    if [ -d "$WORK/DialShift-win-x64" ]; then
        fixture_args+=(--win-package "$WORK/DialShift-win-x64")
        fixture_packages="the macOS bundle and the extracted Windows zip"
    else
        NOT_CHECKED+=("the CAT-03 verifier fixtures on the Windows package (pwsh not found)")
    fi
    fixture_names="the CAT-03 package verifier fixtures on $fixture_packages"
    if $SETUP; then
        unzip -p "$OUT/DialShift-win-x64.zip" DialShift.exe > "$WORK/DialShift.exe"
        fixture_args+=(--win-setup "$OUT/DialShift-Setup-win-x64.exe" --win-exe "$WORK/DialShift.exe" --version "$VERSION")
        fixture_names+=" and the setup verifier fixtures"
    fi
    bash scripts/test-package-verifiers.sh "${fixture_args[@]}"
    CHECKED+=("$fixture_names")
fi

phase "SHA256SUMS.txt and release notes"
ASSETS=(DialShift-win-x64.zip)
if $SETUP; then ASSETS+=(DialShift-Setup-win-x64.exe); fi
ASSETS+=(DialShift-macos-arm64.zip)
(cd "$OUT" && shasum -a 256 "${ASSETS[@]}" > SHA256SUMS.txt)
cat "$OUT/SHA256SUMS.txt"
join() { local out="$1"; shift; for item in "$@"; do out+="; $item"; done; printf '%s' "$out"; }
GITHUB_REPOSITORY="$REPO" scripts/release-notes.sh "$VERSION" "$OUT/SHA256SUMS.txt" > "$OUT/release-notes.md"
printf '\n---\n\n_Built and uploaded from the maintainer'"'"'s Mac with `scripts/release-local.sh` (the GitHub Actions release run was skipped). Checked there: %s. Not checked there: %s._\n' \
    "$(join "${CHECKED[@]}")" "$(join "${NOT_CHECKED[@]}")" >> "$OUT/release-notes.md"
echo "Notes: $OUT/release-notes.md"

if [ "$PRERELEASE" = true ]; then channel=(--prerelease --latest=false); else channel=(--latest); fi
# gh uploads the assets to a draft and publishes it only when every upload succeeded.
create=(gh release create "$TAG" --verify-tag --repo "$REPO" --title "DialShift $VERSION" --notes-file "$OUT/release-notes.md"
    "${channel[@]}")
for asset in "${ASSETS[@]}" SHA256SUMS.txt; do create+=("$OUT/$asset"); done
if [ "$PUBLISH" = true ]; then
    phase "Publish the GitHub Release on $REPO"
    "${create[@]}"
    echo "Published $(gh release view "$TAG" --repo "$REPO" --json url --jq .url) (pre-release: $PRERELEASE)"
else
    phase "Dry run: nothing uploaded"
    echo "With --publish, after the maintainer has pushed $TAG to $REPO, this runs:"
    printf '%q ' "${create[@]}"; echo
fi
echo
echo "Checked on this Mac: $(join "${CHECKED[@]}")."
echo "Not checked on this Mac: $(join "${NOT_CHECKED[@]}")."
