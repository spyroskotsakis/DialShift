# Windows Installer — a per-user NSIS setup beside the zip (brief 4)

> **Status: implemented, 2026-09-26.** Branch `feature/windows-installer` (from `main` at `fb0ef0b`). Public CI is green
> on `windows-latest`, `macos-latest` and the cross-build job (the Mac-built and Windows-built setups byte-identical) at
> `b655d67` (run `36230181228`), `cf21fc1` (`36231552864`), `b7017d7` (`36233711767`), `9322106` (`36235173662`) and
> `b76356e` (`36238278091`), the last two after macOS re-runs, and at `6cbc311` (`36240957220`), the last of
> D101's eight rounds. Prepared for release as `0.5.0` (not tagged or published). NC-19 (a real PC) is
> open. Where the build departs from this spec, the D93–D99 "Update" notes and D101 in
> `docs/decisions.md` say so, and §5 below follows the build.
> This is brief 4, after `single-codebase-refactor.md` (brief 1), `schedule-timezone-research.md` (brief 2) and
> `add-station-catalog-search.md` (brief 3). Its decisions are **D93–D99** in `docs/decisions.md` (§14's defaults,
> adopted); its acceptance rows are **INS-01..INS-20** in `docs/acceptance-matrix.md` §12; its by-hand check is
> **NC-19** (matrix §9, `docs/open-items.md` §2.1). The user's requirement (2026-09-26): keep the current downloads
> (`DialShift-win-x64.zip` with `Install.ps1`, `DialShift-macos-arm64.zip`) and **add** a Windows setup, only if it
> can be built both in CI and on the maintainer's Mac. Nothing in this brief changes the app itself
> (`DialShift.Core`, `DialShift.App`), the settings format or `Settings.Version`.

## 1. Context

- **Windows ships as a zip** (D7: "the artifact starts as a `.zip` … MSIX or an installer comes later").
  `scripts/build.ps1` publishes the self-contained `win-x64` app into `artifacts/DialShift-win-x64/` (666 files,
  about 211 MB: the app, .NET, LibVLC with its plugins, `app-catalog.json`, README, notices, licenses and
  `Install.ps1`), verifies it with `scripts/verify-win-package.ps1` and zips it (`DialShift-win-x64.zip`, about 98 MB).
- **`Install.ps1` is the only installer today** (D56): a per-user copy to `%LOCALAPPDATA%\Programs\DialShift`
  with a Start menu shortcut, no administrator rights, a staged copy beside the install swapped in by renames,
  refusals for overlapping folders and a running installed copy ("Quit DialShift from its tray menu before installing
  an update."), one run at a time (`Local\DialShift.Install`), exact-name leftover cleanup, and an existing
  `HKCU\…\Run\DialShift` value moved to the new path with `--tray`. Its limits for ordinary users: it needs
  "Run with PowerShell" (execution policy, a console window), leaves **no Apps & features entry** and has **no
  uninstaller**; removing DialShift means deleting a folder and a shortcut by hand.
- **Releases** (D53, D58, D92): `release.yml` runs `build.yml` at the tag and publishes `DialShift-win-x64.zip`,
  `DialShift-macos-arm64.zip` and `SHA256SUMS.txt`; when Actions cannot run (the private repository's jobs are refused
  for billing), `scripts/release-local.sh` makes the same release on the maintainer's Apple Silicon Mac, cross-building
  the Windows package with `build.ps1` under PowerShell 7 or taking a Windows-built `--win-zip`.
- **Signing** (D7, NC-05): Windows builds are unsigned. SmartScreen may warn on first launch.

## 2. Goal

A real Windows setup, `DialShift-Setup-win-x64.exe`, published with every release **beside** the existing downloads:

1. **Double-click install for one user, no administrator rights**, into the same folder `Install.ps1` uses, with a
   Start menu shortcut, an optional desktop shortcut and an **Apps & features** entry with an uninstaller.
2. **Upgrades in place** over an earlier setup install **and** over an `Install.ps1` install, keeping settings and
   launch at sign-in, with `Install.ps1`'s safety (staged swap, restore on failure, refusal while DialShift runs).
3. **Uninstalls cleanly**, asking whether to keep the user's stations, schedule and settings.
4. **Silent install and uninstall** (`/S`) for CI and for people who script installs.
5. **Built from the same verified package** on `windows-latest` in CI and on the maintainer's Mac, with one pinned
   NSIS version, deterministic output and a verifier that runs on both.
6. The zip, `Install.ps1` and the macOS download stay exactly as they are (except the one new `Install.ps1` refusal
   of §5.11).

**Non-goals:** code signing (D7 stays: unsigned; NC-05 (b) is the signing work), MSIX, a per-machine
(all-users, `Program Files`) install, an installer for macOS, auto-update, a Windows ARM64 build, localization (the
app is English only), a license page (the repository ships notices, not an EULA), and any change to the app.

## 3. What is already good (keep it)

- **One package layout.** `build.ps1` is the single source of the `win-x64` layout and `verify-win-package.ps1` its
  gate. The setup packs that verified folder; it never publishes on its own.
- **`Install.ps1`'s rules (D56)** are the model for the setup's upgrade: the same install folder, the same staging and
  leftover names (`DialShift.new-<8 hex>`, `DialShift.old-<8 hex>`, exact-name cleanup, never through a junction), the
  same install lock `Local\DialShift.Install`, the same "quit it first" rule, the same Run-value handling, the same
  Start menu shortcut path, and settings in `%LOCALAPPDATA%\DialShift` never touched.
- **The app's own launch-at-sign-in contract** (`WindowsStartupRegistration`, matrix §8.2.1): the Run value is
  `"<full path of DialShift.exe>" --tray`, compared case-insensitively; turning it off deletes both the Run value and
  the `…\Explorer\StartupApproved\Run` value. The setup writes and the uninstaller removes exactly that form.
- **Stable asset names** (D53): no version in a release asset's name, so `/releases/latest/download/<asset>` links
  never change.
- **The honest-status rule**: README and release notes say plainly what was and was not tested by hand.

## 4. Tool choice: NSIS (D93)

| Tool | Builds on macOS | Builds on Windows / CI | Verdict |
|---|---|---|---|
| **NSIS 3.12** (`makensis`) | **Yes**, natively: Homebrew `makensis` (installed on the dev Mac: `makensis -VERSION` → `v3.12`) | Yes: the official `nsis-3.12.zip`, portable, no install | **Chosen** |
| Inno Setup | No (`ISCC.exe` is Windows-only; only under Wine) | Yes | Rejected: not buildable on the Mac, which is the backup release path (D58) |
| WiX (MSI) | No (the toolset needs Windows) | Yes | Rejected: same reason; an MSI adds a Windows Installer database and per-user MSI caveats for no gain here |
| MSIX | Tooling needs Windows | Yes | Rejected: installing needs a signature the PC trusts, and there is no certificate (D7) |

**One toolchain on both sides.** Homebrew's formula builds only the `makensis` compiler from `nsis-3.12-src.tar.bz2`
and installs the stubs, plugins, include files and UI from the **official Windows distribution**
`https://downloads.sourceforge.net/project/nsis/NSIS%203/3.12/nsis-3.12.zip`, SHA-256
`56581f90db321581c5381193d796fffcf2d24b2f8fed2160a6c6a3baa67f2c4f` (`brew cat makensis`, its `resource "nsis"`). CI
on Windows uses that same zip, pinned by the same SHA-256 (D99). So the installer's executable header ("exehead"
stub), its LZMA decompressor, its plugins and its UI come from the same bytes on the Mac and in CI; only the
compiler binary differs.

## 5. Behavior spec (non-negotiable)

### 5.1 Package, name and version

- **Asset:** `DialShift-Setup-win-x64.exe`, no version in the name (D53). Build output
  `artifacts/DialShift-Setup-win-x64.exe`.
- **Payload:** the verified `win-x64` package folder **minus `Install.ps1`** (it would refuse to run from the install
  folder anyway, D56) **plus `licenses\NSIS-COPYING.txt`** (§10), plus the uninstaller the setup writes at install
  time, `Uninstall DialShift.exe`.
- **Version** (D53, D97): `--version` of the build script, default the csproj `<Version>`; a pre-release suffix is
  allowed, a different `MAJOR.MINOR.PATCH` is refused, and the payload's `DialShift.dll` must carry the same
  informational version (`<version>` or `<version>+<commit>`), so a setup can never be labeled with another version
  than the app it installs. The setup's version resource: `FileVersion` and the fixed `VIProductVersion`
  `MAJOR.MINOR.PATCH.0`; `ProductVersion` the full SemVer (`0.5.0-rc.1`); `ProductName` `DialShift`;
  `FileDescription` `DialShift Setup`; `CompanyName` `DialShift`, as in DialShift.exe's own version resource (the SDK
  default; read from the `v0.4.0` package). DialShift.exe's `LegalCopyright` is blank (a single space), so the setup
  omits that key.
- **Icon:** `DialShift.App/Assets/dialshift.ico`, the app's icon, for the setup and the uninstaller (all nine frames,
  16–256 px, are kept; spike §6.3).
- **Unsigned** (D7). The SmartScreen and Smart App Control behavior of the unsigned setup is recorded by NC-19.
- **32-bit stub, x64 app.** NSIS installers are 32-bit x86 executables (Homebrew's and the official build ship x86
  stubs only); they run on 64-bit Windows under WOW64. Nothing the setup touches is redirected: the install folder is
  under `%LOCALAPPDATA%`, and `HKCU\Software\Microsoft\Windows\CurrentVersion\{Uninstall,Run}` and
  `…\Explorer\StartupApproved\Run` are shared between the 32- and 64-bit views. The setup never uses `$PROGRAMFILES`,
  `$SYSDIR` or `HKLM`.

### 5.2 Fresh install

- **Per user, no elevation:** `RequestExecutionLevel user` (the manifest says `asInvoker`, which also stops Windows'
  "installer detection" from asking for administrator rights because the name contains "Setup"). `SetShellVarContext
  current`.
- **Install folder:** `%LOCALAPPDATA%\Programs\DialShift` (`$LOCALAPPDATA\Programs\DialShift`; the parent is created if
  missing), the folder `Install.ps1` uses. **No directory page** (D94): one fixed per-user location keeps upgrades,
  the Run value and `Install.ps1` coexistence simple. An earlier setup's `InstallLocation` (the uninstall key, §5.7) is
  used when present (`InstallDirRegKey`), and the standard NSIS `/D=<folder>` switch overrides both, for automation
  and CI only (README documents it as such). **A `/D=` folder is used exactly as given, or the setup stops** (R3,
  D101): NSIS itself would silently replace a `/D=` folder it can't use with that `InstallLocation` or the default
  folder, so a mistyped path would upgrade or create an install the command line never named.
- **Steps, all inside the install lock** (§5.4): refuse per §5.4 → remove exact-name leftovers of earlier runs beside
  the install (§5.3) → extract the payload into `DialShift.new-<8 lowercase hex>` beside the install folder → write
  `Uninstall DialShift.exe` into it → swap it in (§5.3; for a fresh install, one rename) → Start menu shortcut, optional
  desktop shortcut (§5.6) → Run value (§5.5) → uninstall key (§5.7).
- **Settings:** `%LOCALAPPDATA%\DialShift` (settings, log, lock, backups) is never read, written, created or removed
  by the setup. First-run behavior is the app's own.
- **Launch:** interactive runs end on a Finish page with **Run DialShift** checked (the app starts normally, window
  shown, as a child of the non-elevated setup). A silent run **never** starts DialShift.

### 5.3 Upgrade (setup over setup, and setup over an `Install.ps1` install)

- **Detected** when the install folder contains `DialShift.exe` (whether the setup or `Install.ps1` put it there). The
  Welcome page then says `DialShift <installed version> is installed in <folder>. Setup will replace it with
  <version>. Your stations, schedule and settings are kept.` (the installed version from the uninstall key's
  `DisplayVersion`, else from `DialShift.exe`'s product version, else "an earlier version").
- **Staged swap, as D56:** the new build is extracted into `DialShift.new-<id>` in the install folder's parent (same
  volume), then the install is renamed to `DialShift.old-<id>`, the staging folder to `DialShift`, and the old copy is
  deleted. Each rename, and the restore rename, is tried 10 times 400 ms apart. A failed extraction removes the
  staging folder and changes nothing; a failed swap removes the staging folder, renames the old install back and
  exits 14 with the reason; if the restore also fails, the message names the `DialShift.old-<id>` folder to rename back.
  A failure to delete the old copy after a successful swap is a warning that names the folder (exit 0). Result: no
  file of the old build survives an upgrade (a stale LibVLC plugin from an older VLC would otherwise be loaded).
- **Leftovers** (as D56): before staging, folders beside the install named exactly `^DialShift\.(new|old)-[0-9a-f]{8}$`
  (case-sensitive) are removed; never a reparse point (junction or symbolic link: the setup checks
  `FILE_ATTRIBUTE_REPARSE_POINT` before any recursive delete), and a `DialShift.old-*` is kept while there is no
  install, because it is then the only copy of the previous install.
- **Kept across an upgrade:** settings (never touched), the Run value (moved, §5.5), an existing desktop shortcut
  (refreshed, §5.6). The uninstall key is rewritten with the new version.
- **Downgrade** (an older setup over a newer install) is allowed with no extra prompt, as with `Install.ps1`; it is
  the documented way back (README "Going back").
- **Over an `Install.ps1` install** in the same folder: the same swap; afterwards there is one install, one Start menu
  shortcut (the same `DialShift.lnk`), and an Apps & features entry that did not exist before.

### 5.4 Refusals, the install lock and exit codes

Checked in this order, before anything changes. Interactive runs show the message in a dialog; silent runs
(`/S`) show nothing and exit with the code. Messages are exact.

| # | Condition | Message | Silent exit |
|---|---|---|---|
| R1 | Not 64-bit Windows, or older than Windows 10 (`${RunningX64}`, `${AtLeastWin10}`) | `DialShift needs 64-bit Windows 10 or later.` | 12 |
| R2 | Another install holds `Local\DialShift.Install` for 5 s (created with the System plugin; a `WAIT_ABANDONED` counts as acquired, as in D56). Held from here until the setup exits, so a double-click and a concurrent `Install.ps1` never interleave | `Another DialShift install is running. Wait for it to finish, then run DialShift Setup again.` | 11 |
| R3 | (a) D101: the command line has more than 1,023 characters (NSIS keeps 1,023, so its `/D=` could be cut short or unseen; exactly 1,023 is kept whole); or the folder the setup would use, `$INSTDIR` as NSIS reads it, is not exactly the `/D=` text with its trailing `\` and spaces removed: `/D=` names a folder NSIS can't use and replaces before the setup's checks run (no drive or share root, such as `DialShift` or `C:DialShift`; a drive that doesn't exist; a file in the path; nothing), or one NSIS keeps but reads as another folder (a `/`, `"`, `*`, `?`, `\|`, `<`, `>` or `:` after the drive, or a control character, which NSIS drops, or anything after the folder, such as ` /S`, since `/D=` takes the rest of the line); a `\\?\` text that ends with a space before its trailing backslashes (`\\?\` names `DialShift ` literally, the ordinary form `DialShift`); or the command line carries a `/D=` NSIS does not take (quoted as a whole, lowercase, no space before it); (b) `CheckFolderPath`: a leading `\\?\` is dropped (`\\?\UNC\` becoming `\\`, so R3 (c), R4, the entry, the Run value and the shortcuts see the ordinary path); the folder must then be absolute (a drive letter and `:`, or `\\server`; not a `\\?\Volume{…}` or `\\?\GLOBALROOT` path, which would be left relative, nor a `\\.\` device path), must not be a drive or network share root (`C:\`, `\\server\share`) before or after it is normalized with kernel32's `GetFullPathNameW` (NSIS's own `GetFullPathName` returns nothing for a folder that doesn't exist yet), and a `\\?\` path must already be in normal form (`\\?\C:\x\DialShift.` or `\\?\C:\x\..\y` name other folders once ordinary); `CheckPathLength`: the payload's longest file (at most 259 characters) and folder (at most 247) must fit Windows' path limits in the install folder and in `DialShift.new-<id>` beside it, since the setup is not long-path aware (the lengths are generated at build time: 89 and 47 for `0.4.0`); (c) it is, is inside, or contains `%LOCALAPPDATA%\DialShift` (textual, case-insensitive, one trailing `\`, as D56), compared after `CheckFolderPath` has spelled the existing part of the folder with long names (`GetLongPathNameW` on its deepest existing folder), so an 8.3 spelling such as `C:\Users\CROS~MYY\AppData\Local\DialShift` is caught; then compared again as Windows resolves both folders (`GetFinalPathNameByHandleW` on the deepest existing folder of each, the rest appended), which catches a `subst` drive, a junction, a folder symbolic link or a mount point leading to it (D101). This second comparison fails open: when either folder cannot be opened or resolved, only the textual rule applies. Not resolved: a network path to the same disk (`\\localhost\C$\…`); R4 still refuses the settings folder then unless it is empty, which it is only before DialShift first runs | (a) `DialShift Setup's command line is too long to read (<n> characters). Keep it to 1023 characters or fewer, for example with a shorter /D= folder.`, `/D=<text> isn't a folder DialShift can be installed in. Put /D= last on the command line, followed by a full path with backslashes on a drive that exists, such as /S /D=C:\Apps\DialShift.` or `DialShift Setup could not read its /D= switch. Put it last on the command line, in capitals and without quotes, even when the folder has spaces: /S /D=C:\My Apps\DialShift.`; (b) `DialShift can't be installed in <folder as given>: that isn't an ordinary full path to a folder. Give one such as C:\Apps\DialShift or \\server\share\DialShift.`, `DialShift can't be installed in <root>\, the top of a drive or network share. Choose a folder in it, such as <root>\DialShift.` or `DialShift can't be installed in <folder>: its path is too long for DialShift's files (Windows allows 259 characters in a path). Choose a folder with a shorter path, such as C:\Apps\DialShift.`; (c) `DialShift can't be installed in <folder>: that folder holds your DialShift settings. Choose another folder.` | 13 |
| R4 | The install folder is neither missing, nor empty, nor a DialShift install holding nothing else (an install: `Uninstall DialShift.exe`, or `DialShift.exe`, `DialShift.dll` and `libvlc\` as `Install.ps1` leaves them; every top-level entry DialShift's); then, while a setup install elsewhere still has its uninstaller, any other folder (one Apps & features entry per user) | `<folder> already holds other files. Choose an empty folder or the folder DialShift is installed in.`, `<folder> holds files that are not part of DialShift, such as <name>. Move them out of that folder, then run DialShift Setup again.`, or `DialShift is already installed in <folder>. To install it in <other>, uninstall it in Settings > Apps first.` | 13 |
| R5 | `DialShift.exe` in the install folder is running: opening it for writing fails (Windows denies write access to a running executable's image; this also catches the retired WPF app, same file name) | `DialShift is running. Quit it from its tray menu (Quit DialShift), then choose Retry.` with **Retry** / **Cancel**; any other failure to open it is reported with its reason | 10 (Cancel); 15 (could not open it) |

Other exit codes: **0** success (also after the old-copy warning), **1** the user cancelled (NSIS standard),
**14** the extraction or the swap failed and the previous install is unchanged. The setup's own codes start at 10, so
none meets NSIS's own 2 ("aborted by script"); D101 records them. **The setup never closes or kills
DialShift** (D94): the app has no "quit" command over its activation channel, a forced kill could interrupt a settings
save, and "quit it first" is D56's rule. R3 and R4 can only be reached through `/D=` today; they guard the uninstaller's
and the swap's deletes from ever pointing at the settings folder or at someone else's files. NSIS reads `/D=` before
`.onInit`; `AllowRootDirInstall true` lets a root through to R3 (b), and R3 (a) compares the `/D=` text of the
process's command line (`GetCommandLineW`), less its trailing `\` and spaces, with the folder NSIS chose as every read
of `$INSTDIR` returns it (D101).

### 5.5 Launch at sign-in (Run value)

- **Install and upgrade:** if `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` has a `DialShift` value (whatever it
  points at), it is set to `"<install folder>\DialShift.exe" --tray`, exactly as `Install.ps1` does (D56 "Unchanged").
  No value is created when there is none: launch at sign-in stays off unless the user chose it. The
  `StartupApproved\Run` value (the Task Manager switch) is not touched.
- **Uninstall:** the Run value and the `StartupApproved\Run` `DialShift` value are removed **only when** the Run value
  equals `"<install folder>\DialShift.exe" --tray` (case-insensitive, the app's own comparison). A value that points
  anywhere else (another copy, an extracted zip) is left as it is.

### 5.6 Shortcuts

- **Start menu:** `$SMPROGRAMS\DialShift.lnk` (the per-user Programs folder, the same file `Install.ps1` writes):
  target `<install folder>\DialShift.exe`, working folder the install folder, icon `DialShift.exe,0`, description
  `Your radio, on time.`. No Start menu subfolder and no uninstall shortcut (Apps & features is the uninstall place).
- **Desktop:** `$DESKTOP\DialShift.lnk`, same properties, **optional**: a Components page item "Desktop shortcut",
  unchecked by default, checked by default when that shortcut already exists. A silent install never creates one; a
  silent upgrade refreshes an existing one.
- **Uninstall** deletes `DialShift.lnk` in the per-user Programs folder and on the desktop only when it starts
  `<install folder>\DialShift.exe` (D101): a shortcut to another copy stays, whichever uninstaller runs.

### 5.7 Apps & features entry

Key `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\DialShift`, written after a successful swap, rewritten
on every upgrade, removed by the uninstaller:

| Value | Type | Content |
|---|---|---|
| `DisplayName` | REG_SZ | `DialShift` |
| `DisplayVersion` | REG_SZ | the full version (`0.5.0`, `0.5.0-rc.1`) |
| `Publisher` | REG_SZ | `DialShift` (D97: DialShift.exe's `CompanyName`; one `!define` to change) || `DisplayIcon` | REG_SZ | `"<install folder>\DialShift.exe",0` |
| `EstimatedSize` | REG_DWORD | KiB of the installed files except the uninstaller, rounded up, computed at build time |
| `UninstallString` | REG_SZ | `"<install folder>\Uninstall DialShift.exe"` |
| `QuietUninstallString` | REG_SZ | `"<install folder>\Uninstall DialShift.exe" /S` |
| `InstallLocation` | REG_SZ | `<install folder>` |
| `URLInfoAbout` | REG_SZ | `https://github.com/spyroskotsakis/DialShift` |
| `NoModify`, `NoRepair` | REG_DWORD | `1` (there is nothing to modify; "repair" is running the setup again) |

### 5.8 Uninstall

- **Refusals first:** R2 (the install lock, exit 11), `CheckFolderPath` as in R3 (b) (exit 13, D101): a drive or share
  root as the folder (`_?=C:\`, which `AllowRootDirInstall` admits there too; `This uninstaller won't remove files from
  <root>\, the top of a drive or network share: DialShift Setup never installs there. Delete the uninstaller
  yourself.`; the folder is normalized first, so `_?=C:\x\..` is that root) or a `_?=` that is not an ordinary full path
  (`_?=\\?\Volume{…}\t\DialShift` would be relative without its prefix, and deleted from the working folder; `This
  uninstaller won't remove files from <folder>: that isn't an ordinary full path to a folder. …`); before that, the
  `_?=` text exactly as given, as R3 (a) checks `/D=`: NSIS drops `/ " * ? | < > :` from a `_?=` too, so `_?=C:\t/Victim`
  would uninstall `C:\tVictim` (`This uninstaller won't remove files from _?=<text>: Windows would read that as another
  folder. …`; exit 13); then `un.CheckOwnFolder` (exit 13, `This uninstaller won't remove files from <folder>: that
  isn't the DialShift install it belongs to. …`): run in place (`_?=`), the folder must hold `Uninstall
  DialShift.exe` as a regular file, by its directory entry (`FindFirstFileW`): not a folder, not a symbolic link or
  junction (a name-surrogate reparse point; a deduplicated file or a cloud placeholder counts as a file); run as the
  temporary copy (the relaunch from `$TEMP\~nsu<n>.tmp`, made by `CopyFile`, which keeps the size and the last-write
  time and gives the copy a new creation time), that file must also have the copy's size and a last-write time
  within 2 s of the copy's (FAT's resolution, so a `$TEMP` on any file system matches); the creation time is never
  compared. A file without a directory entry refuses. An uninstaller started without `_?=` runs as a first process with no script,
  which NSIS also gives a `/D=`, and relaunches its copy with that folder already cleaned (`/D=C:\t/Victim` arrives
  as `C:\tVictim`), so the `_?=` text check cannot see it there; this check does. Residual: another DialShift
  install of the same build whose uninstaller was written within those 2 s. And R5 (DialShift running, Retry/Cancel, exit 10; 15 when it
  can't be checked), as in §5.4. Whether the Start menu and desktop shortcuts start this install is decided before
  the file list removes its `DialShift.exe`.
- **Question** (interactive only), before anything is removed: `Keep your stations, schedule and settings?` with the
  detail `Choose No to also delete %LOCALAPPDATA%\DialShift (settings, log and settings backups). This can't be
  undone.`; buttons **Yes** (default) / **No**. A silent uninstall keeps them (`/SD IDYES`). **No** removes exactly
  `%LOCALAPPDATA%\DialShift`, never another folder (not a `DIALSHIFT_DATA_DIR` override, which is a developer seam).
- **Removes:** every file the build installed, from a list generated at build time from the payload, then each of the
  payload's folders if empty (deepest first), then `Uninstall DialShift.exe` and the install folder if empty; the
  Start menu and desktop shortcuts when they start this install (§5.6); the Run and StartupApproved values when they
  point here (§5.5); the uninstall key when its `InstallLocation` is this folder; exact-name leftovers beside the install (§5.3 rules). **Never `RMDir /r` of the install folder**:
  a file the user put there stays, with its folder, and the Finish text says `Some files you added were left in
  <folder>.`
- **Exit codes:** 0, 1 (cancelled), 10, 11, 13, 15; 14 when a listed file cannot be deleted (it is named); 2 from NSIS
  itself, before `un.onInit`, when `_?=` is not a full path on an existing drive or share (`_?=C:`, `_?=relative`; NSIS
  checks the drive or share and that no part of the path is a file, not that the folder exists): nothing is deleted, and a run that is not silent shows NSIS's "Error launching installer". NSIS runs an
  uninstaller from a temporary copy and the first process returns at once; automation that needs the result runs
  `"Uninstall DialShift.exe" /S _?=<install folder>` (runs in place and waits; the uninstaller file itself then stays
  and is deleted by the caller). §8 tests both forms.

### 5.9 Switches

`/S` silent (install and uninstall), `/D=<folder>` (install; last on the command line, in capitals, unquoted even with
spaces, NSIS rule; the setup installs in exactly that folder or refuses with 13, R3 (a)),
`_?=<folder>` (uninstall in place, NSIS rule; exactly that folder, R3 (a)). `/D=` does not apply to the uninstaller:
NSIS passes it on as the folder, and the uninstaller then refuses any folder that is not its own install (§5.8). No
custom switches: a silent install never launches DialShift and never
creates a desktop shortcut; a silent uninstall always keeps settings.

### 5.10 Wizard UX (QG-03)

Modern UI 2, English, `ManifestDPIAware true` (crisp at 150 % and 200 %), `BrandingText "DialShift <version>"`
(not "Nullsoft Install System"), the app icon, no license page. Pages: **Welcome** (what is installed where; "No
administrator rights are needed."; the upgrade sentence of §5.3) → **Components** (DialShift, required; Desktop
shortcut) → **Installing** → **Finish** (Run DialShift, checked). Uninstaller: **Confirm** → the §5.8 question →
**Uninstalling** → **Finish**. Every message names the fix; no dead-end dialog (Retry where a retry can succeed). Tab
and Enter/Escape work on every page (NSIS standard), checked by hand in NC-19.

### 5.11 `Install.ps1` over a setup install (D95)

`Install.ps1` gains one refusal, before it changes anything and after its existing overlap checks: when the
destination holds `Uninstall DialShift.exe`, or the uninstall key's `InstallLocation` equals the destination, it stops
with `DialShift in <folder> was installed with DialShift Setup. To upgrade it, run the new DialShift-Setup-win-x64.exe,
or uninstall DialShift in Settings > Apps first.` and exits 1. Without it, `Install.ps1`'s swap would drop the
uninstaller and leave an Apps & features entry pointing at a missing file. Everything else in `Install.ps1` stays
(D56); it stays ASCII and Windows PowerShell 5.1 compatible.

## 6. Build

### 6.1 `scripts/build-win-setup.sh` and `scripts/windows-setup/DialShift.nsi`

```text
scripts/build-win-setup.sh [--package <folder> | --zip <DialShift-win-x64.zip>] [--version <semver>] [--output <exe>]
scripts/build-win-setup.sh --print-nsis-pin
```

- **bash** (3.2+), so it runs on macOS without PowerShell and under Git Bash on `windows-latest` (as
  `test-package-verifiers.sh` does). Defaults: `--package artifacts/DialShift-win-x64`, `--version` the csproj
  `<Version>`, `--output artifacts/DialShift-Setup-win-x64.exe`. `--zip` extracts the zip into a temp folder first
  (release-local's `--win-zip` mode).
- **The pin is here, once:** `NSIS_VERSION=3.12` and the SHA-256 of `nsis-3.12.zip`. `--print-nsis-pin` prints both,
  so `build.yml`'s install step reads them instead of repeating them. The script finds `makensis` on `PATH` (or
  `$MAKENSIS`) and refuses any `makensis -VERSION` other than `v3.12`, naming the pin to bump.
- **Refuses** (before running makensis): a version that is not SemVer or whose `MAJOR.MINOR.PATCH` differs from the
  csproj (D53); a package without `DialShift.exe`, `DialShift.dll` or `app-catalog.json`; a `DialShift.dll` whose
  informational version is not the version (the same `grep -a` test `release-local.sh` uses).
- **Generates** into a temp folder, never into the repository: `defines.nsh` (`VERSION`, `VERSION_NUMERIC`,
  `ESTIMATED_SIZE_KB`, the payload, icon and `NSIS-COPYING.txt` paths) and `uninstall-files.nsh` (one `Delete` per
  installed file and one `RMDir` per folder, deepest first, from the payload listing). On Windows the paths are
  converted with `cygpath -w` for `makensis.exe`.
- **The script file** `scripts/windows-setup/DialShift.nsi` is ASCII, `Unicode true`, and uses only NSIS's own
  includes and plugins (`MUI2.nsh`, `LogicLib.nsh`, `x64.nsh`, `WinVer.nsh`, `FileFunc.nsh`, the System plugin).
  makensis runs with `-WX` (warnings are errors, like `-warnaserror`) and `-V2`.
- **Then** it runs `scripts/verify-win-setup.sh` on the output (with `--package`, and contents when 7-Zip is found)
  and prints the size and SHA-256, as `build.ps1` verifies before it zips.
- `build.ps1` is unchanged: the setup is a separate step on its verified output, so the zip path and its CI checks do
  not move.

### 6.2 Determinism (D98)

- `SetCompressor /SOLID lzma` and **`SetDateSave off`**: NSIS otherwise stores each file's modification time, so a
  fresh checkout or extraction would change the bytes (spike §6.3). With it, the installed files get their install
  time as modification time, which nothing in DialShift reads.
- No timestamps, random values, absolute paths or host names go into the script. The random `<id>` is generated at
  install time, not build time.
- **Target:** the same payload bytes, version and NSIS 3.12 give the same installer bytes on the Mac and on
  `windows-latest`. The Mac is proven (two builds, same SHA-256). **Cross-OS identity is unproven** until the first CI
  run of §8.2; if it differs (for example in the order `File /r` walks folders, or in the LZMA encoder between the two
  compiler builds), a new decision records the difference and the gate becomes **content equivalence**: both
  installers pass `verify-win-setup.sh`, and their 7-Zip listings (paths, sizes, CRC-32) are equal.

### 6.3 Spike results (2026-09-26, the dev Mac, Homebrew makensis 3.12; scratch files only, nothing committed)

- **Real package:** the `v0.4.0` private release's `DialShift-win-x64.zip` (666 files, 216,304 KiB on disk), packed
  with `File /r /x Install.ps1`, `SetDateSave off`: **LZMA solid 63,645,145 B in 42 s**, twice, byte-identical
  (SHA-256 `8573e995…4f056b`); zlib solid 93,765,635 B in 14 s (the zip asset is 97.9 MB). LZMA is the default.
- **Timestamps:** without `SetDateSave off`, touching one payload file changed the output; with it, the output was
  identical after touching files and after recreating the payload folder in another order.
- **File order:** `File /r` added files in sorted, case-sensitive order (`B.txt` before `a.txt`) regardless of creation
  order, on macOS.
- **Static structure** of both the small and the real installer: PE machine `0x14c` (i386), optional-header magic
  `0x10b`, subsystem 2 (GUI); the NSIS first header right after the PE image's last section (offset 155,648 on the
  real one): flags, `0xDEADBEEF`, `NullsoftInst`, header length, data length, and **image end + data length = file
  size**; the **integrity CRC** is zlib CRC-32 over bytes `[512, size − 4)` and equals the last 4 bytes (a one-byte
  flip in the payload made it mismatch); `RT_MANIFEST` with `requestedExecutionLevel level="asInvoker"` and
  `<description>Nullsoft Install System v3.12</description>`; `RT_GROUP_ICON` with all nine frames of
  `dialshift.ico` (16, 20, 24, 32, 40, 48, 64, 128, 256), and the nine `RT_ICON` resources byte-equal to the `.ico`'s
  frames; `VS_VERSIONINFO` strings as defined (`ProductVersion` `0.4.0-rc.1` kept as text). DialShift.exe's own
  version resource in the same package: `CompanyName` and `ProductName` `DialShift`, `ProductVersion`
  `0.4.0+3c8543c…`, `LegalCopyright` a single space.

## 7. Verification without running it: `scripts/verify-win-setup.sh` (D98)

```text
scripts/verify-win-setup.sh <DialShift-Setup-win-x64.exe> [--version <semver>] [--package <folder>] [--require-contents]
```

bash plus Python 3 (standard library only: `/usr/bin/python3` on macOS as `verify-mac-app.sh` uses, `python` on the
Windows runner), so it runs on the Mac without PowerShell and in CI.

**Always checked (static, from the file's bytes):**
1. PE: `MZ`, `PE\0\0`, machine `0x14c`, optional-header magic `0x10b`, subsystem 2.
2. NSIS first header at the end of the PE image (512-byte aligned): `0xDEADBEEF`, `NullsoftInst`, and image end +
   data length = file size (no truncation, nothing appended; a later Authenticode signature would append data and
   needs this rule revisited, NC-05 (b)).
3. Integrity CRC: zlib CRC-32 of `[512, size − 4)` equals the stored last 4 bytes.
4. Version resource: `ProductName` `DialShift`, `FileDescription` `DialShift Setup`, `ProductVersion` = `--version`
   (default the csproj `<Version>`), `FileVersion` and the fixed-info file version = `MAJOR.MINOR.PATCH.0`.
5. Manifest: `asInvoker`, `dpiAware` true, and the NSIS version `v3.12` = the build script's pin.
6. Icon: the group icon's frame sizes equal `dialshift.ico`'s, and each `RT_ICON` equals the `.ico`'s frame bytes.

**Checked with 7-Zip** (`7z` on `windows-latest`; `7zz` from `brew install sevenzip` on a Mac, optional there), when
`--package` is given: the listing, with the output-folder prefix removed and the uninstaller entry 7-Zip synthesizes
ignored, equals the package's files minus `Install.ps1` plus `licenses\NSIS-COPYING.txt`: same paths, sizes and
CRC-32; `DialShift.exe`, `app-catalog.json`, `libvlc\win-x64\libvlc.dll`, `THIRD-PARTY-NOTICES.md` and every license
text present, `Install.ps1` absent. Without 7-Zip the verifier prints `contents: not checked (7-Zip not found)` and
still passes, unless `--require-contents` (CI) is given. If 7-Zip turns out unable to list this installer, a decision
records it and CI's content proof is the installed tree of §8.1 (s1) alone.

**Not checkable without running it:** the install logic (swap, refusals, registry, shortcuts, uninstaller), which §8
exercises on `windows-latest`, and the wizard, SmartScreen and Apps & features, which NC-19 checks by hand.

**Fixtures** (`scripts/test-package-verifiers.sh --win-setup <exe>`, both OSes): the real setup passes; it fails
with `--version 9.9.9`; a copy with one payload byte flipped (CRC), one truncated by a byte, one with a byte appended,
and `DialShift.exe` itself (an x64 PE without NSIS data) each fail with the verifier's message.

## 8. CI (`build.yml`, D99)

### 8.1 The Windows job (after "Install.ps1 (…)" and the CAT-03 fixtures, before the uploads)

1. **Install NSIS (pinned):** read the pin with `bash scripts/build-win-setup.sh --print-nsis-pin`; restore
   `nsis-3.12.zip` from `actions/cache@v5` (key on the SHA-256) or download it with `curl -L --retry 5`; **check the
   SHA-256 after every restore or download**; extract to `$RUNNER_TEMP\nsis`, put it first on `PATH`,
   `makensis -VERSION` must print `v3.12`. Not Chocolatey: its package version and install script are not pinned to
   these bytes. A `makensis` preinstalled on the runner image is never used.
2. **Package win-x64 setup:** `bash scripts/build-win-setup.sh --package "$RUNNER_TEMP/DialShift-win-x64" --version
   "$VERSION"`, from the folder extracted from the zip a user downloads (already verified), so the setup and the zip
   carry the same bytes.
3. **Verify win-x64 setup:** `verify-win-setup.sh … --package … --require-contents`, then the §7 fixtures.
4. **Setup (silent install, upgrade, Install.ps1 coexistence, refusals, uninstall):** pwsh, 10-minute timeout, with
   `$root = $RUNNER_TEMP\setup-check`, the install folder `$root\Programs\DialShift` through `/D=` (the setup reads
   `%LOCALAPPDATA%` through the shell's known-folder API, so the environment variable `Install.ps1`'s step sets does
   not redirect it), and `Install.ps1` run with `LOCALAPPDATA=$root` so both tools meet in the same folder. Before the
   cases: the real `%LOCALAPPDATA%\DialShift` gets a sentinel `settings.json` (the runner has none; the smoke tests
   use temporary data folders), whose SHA-256 is checked after every case. Every case also checks that no DialShift
   process was started, except (s5), which starts one on purpose.
   - **(s1) Fresh silent install** (`/S /D=…`): exit 0; the tree equals the package minus `Install.ps1` plus
     `licenses\NSIS-COPYING.txt` and `Uninstall DialShift.exe` (paths and SHA-256); `$root\Programs` holds only
     `DialShift`; the Start menu shortcut's target, working folder and icon; no desktop shortcut; every §5.7 value
     (`EstimatedSize` recomputed from the tree); no Run value created.
   - **(s2) Upgrade setup over setup** with planted state: a file only the old build has (gone afterwards);
     `DialShift.new-0123abcd` and `DialShift.old-0123abcd` (removed); `DialShift.old-backup` and a junction
     `DialShift.new-0badf00d` with its target (kept); run as `/D=\\?\<install>` (D101: the entry, the Run value and the
     shortcuts hold the ordinary path); a Run value `"C:\Old\DialShift\DialShift.exe" --tray` (now
     `"<install>\DialShift.exe" --tray`); a desktop `DialShift.lnk` pointing elsewhere (now pointing here); the
     uninstall key's `DisplayVersion` rewritten.
   - **(s3) Locked file:** `DialShift.dll` of the install open without delete sharing: exit 14, tree unchanged, no
     leftovers.
   - **(s4) Install lock held** by the step: exit 11 after about 5 s, nothing changed.
   - **(s5) DialShift running** from the install (`--tray`, `DIALSHIFT_DATA_DIR` in a temp folder, `DIALSHIFT_AUDIO_OUTPUT=dummy`):
     the setup `/S` exits 10 and the uninstaller `/S _?=` exits 10, nothing changed; then the step stops the process.
   - **(s6) Folder refusals**, each with the uninstaller moved aside so that the one-install rule cannot refuse it
     too: `/D=` the emptied data folder (also as `\\?\<folder>`), a folder inside it (also spelled
     `<LOCALAPPDATA>\setup-check-nothing\..\DialShift\inside`, which only kernel32's `GetFullPathNameW` normalizes), the
     settings folder through a junction, a `subst` drive on its parent and its 8.3 name (when the drive has 8.3 names),
     the uninstaller started without `_?=` and given `/D=<root>\x/DialShift` (its relaunched copy refuses
     `<root>\xDialShift`, where a decoy stays), the
     volume GUID path of drive C (`\\?\Volume{…}\…`) for the setup and for the uninstaller's `_?=`, with a decoy install
     in the working folder that must stay, the uninstaller's `_?=<root>\x/DialShift` and `_?=<root>\Dial|Shift` with
     decoys at the folders NSIS would read (they and the Start menu shortcut stay), the uninstaller on a `\\.\` path,
     `\\?\` with a trailing dot, a trailing space or `..`, a `\\.\` device path, folder paths too long for the payload
     (one character over the limit, and far over), a folder with foreign files, a folder with `DialShift.exe`
     among other files, drive roots (an empty `subst` drive as `X:\`, `X:\\` and `X:\sub\..`, and the system
     drive), the uninstaller's `_?=` on the `subst` root (`X:\` and `X:\sub\..`, a planted `DialShift.dll` and the Start
     menu shortcut kept), and (D101) `/D=` folders NSIS can't use (a missing drive, a relative and a drive-relative
     path, a file in the path, a file, forward slashes, a quoted folder, nothing), `/D=` folders it keeps but reads as
     another (a slash inside, ` /NCRC` after the folder), a 1,030-character argument before `/D=`, a command line of exactly 1,024 characters, and `/D=` switches
     it does not take (quoted, lowercase): exit 13 each, nothing created or changed (not the install, which NSIS would otherwise have
     upgraded in their place, not the default folder, not the named folders); then, with the uninstaller back, a
     second install location: exit 13.
   - **(s7) `Install.ps1` over the setup install** (D95): exit 1 with the §5.11 message (whitespace-insensitive, as
     the existing step matches), tree unchanged.
   - **(s8)** First (D101) the uninstaller of a copy of the install, in place: the install's Start menu shortcut, entry
     and Run value stay. Then the installed uninstaller's creation time is set to 2000-01-01 (a relaunch copy is
     compared by size and last-write time only), and the **silent uninstall**, run from `QuietUninstallString` exactly as Windows would, then waiting up to 60 s for
     the temporary uninstaller to finish (the key and the files gone): the Run value pointing here is removed together
     with a planted `StartupApproved\Run` value; the shortcuts, the key and every payload file are gone; a file the
     step added to the install folder is still there, with its folder; the settings sentinel is unchanged.
   - **(s9) Setup over an `Install.ps1` install:** `Install.ps1 -NoLaunch` into the same folder, then the setup `/S`:
     one install, one Start menu shortcut, the key created; then `Uninstall DialShift.exe /S _?=…` exits 0 and a Run
     value pointing at another copy is **kept** byte for byte; a reinstall into the folder that uninstall left (only
     the uninstaller) with `/D=<folder>\` (a trailing backslash) and its uninstall; then (D101) a fresh install into
     `Spaced Café Folder\DialShift` (spaces, a non-ASCII letter, `/D=` unquoted) on a command line of exactly 1,023
     characters: the payload, `InstallLocation` and the shortcut, and its in-place uninstall; then a fresh install whose
     parent folder is exactly as long as the payload allows (259 characters for its longest file) and its uninstall.
   The step restores or removes the Run, StartupApproved and uninstall-key values, both shortcuts and the sentinel in
   `finally`, and resets `$LASTEXITCODE`, as the `Install.ps1` step does. The existing `Install.ps1` step keeps its
   seven cases unchanged and gains nothing; the D95 refusal is (s7).
5. **Upload** the artifact `DialShift-Setup-win-x64` (the `.exe`, `compression-level: 0`, 7 days), matching
   `release.yml`'s `DialShift-*` pattern.

The job's `timeout-minutes` goes from 45 to 60 (about 1 min of makensis on the runner, the fixtures, and nine
extractions of a 64 MB installer).

### 8.2 The cross-build job (macOS, D99)

A new job in `build.yml`, `setup-crossbuild`, `needs: build-test-package`, on `macos-latest`: download the
`DialShift-win-x64` and `DialShift-Setup-win-x64` artifacts of the same run; `brew install makensis` and require
`v3.12` (the pin; on a newer Homebrew version it fails with "bump NSIS_VERSION and the pinned zip together", because
the Mac is the backup release path and must build with the same NSIS as CI); `unzip` the zip;
`build-win-setup.sh --package <that folder> --version "$VERSION"`; `verify-win-setup.sh` (static); compare the
SHA-256 of the Mac-built and the Windows-built setup and write both, and the verdict, to the step summary. Byte
identity passes; a difference fails the job until a decision (§6.2) switches the gate to content equivalence (then
the job also installs `sevenzip` and compares listings). Its artifacts do not match `DialShift-*`. Being part of
`build.yml`, it gates releases like every other check.

## 9. Release

- **`release.yml`:** the publish step also requires `DialShift-Setup-win-x64.exe` among the downloaded artifacts,
  writes `SHA256SUMS.txt` over **the three downloads** (`sha256sum DialShift-win-x64.zip DialShift-Setup-win-x64.exe
  DialShift-macos-arm64.zip`), and passes four files to `gh release create`. `resolve` is unchanged.
- **`scripts/release-notes.sh`:** the Download table gains `DialShift-Setup-win-x64.exe` ("Windows 10/11, x64: installs
  for your user, no administrator rights; Apps & features uninstall"); the Install section puts the setup first and
  the zip second; the Windows checksum snippet names both Windows files; the signing status names the setup.
- **`scripts/release-local.sh`** (D58): requires `makensis` `v3.12` (`brew install makensis`), builds the setup from
  the cross-built package folder (or from `--win-zip` through `--zip`), runs `verify-win-setup.sh` (contents when
  `7zz` is present, else listed under "not checked"), includes it in `SHA256SUMS.txt` and `gh release create`. Its
  "not checked" line adds "the setup's install, upgrade and uninstall cases (they need Windows)".
- **The first release with the setup** is `0.5.0` (prepared: csproj `<Version>` and CHANGELOG `[0.5.0]`; not tagged or
  published), with a CHANGELOG section that states the
  setup's testing status (CI only until NC-19).

## 10. Docs, README and notices

- **README:** Download table (four files), the `latest/download` link for the setup, "Check the download" for both
  Windows files; **Install on Windows**: the setup first (double-click, SmartScreen **More info → Run anyway**, where it
  installs, Apps & features, `/S` and `/D=` for automation, uninstalling and the settings question), then the zip and
  `Install.ps1` as the portable alternative; **Upgrading**: from a zip or `Install.ps1` install, the setup upgrades in
  place; `Install.ps1` refuses over a setup install (D95); **Build** table and **Releasing**: the setup script,
  verifier, the NSIS pin and `brew install makensis`; **Dependencies**: NSIS 3.12 as a build tool (release builds
  only); the package table's status line for the setup.
- **THIRD-PARTY-NOTICES.md**, new section "Windows setup only", from the licence text shipped with NSIS 3.12
  (`/opt/homebrew/Cellar/makensis/3.12/COPYING`, identical to `share/nsis/COPYING` from the official zip, 15,632 B,
  SHA-256 `388357c1215ff403c5ebde3a5ecd273e68f8b79a579996775245d1ee65442aba`): the setup and its uninstaller contain
  NSIS 3.12 code: the executable header, the System plugin and the Modern UI under the **zlib/libpng license**, and
  the **LZMA decompressor under the Common Public License 1.0 with NSIS's "special exception for LZMA compression
  module"** (linking DialShift's installer to it does not put the installer under the CPL; the module itself stays
  CPL, unmodified, source at `https://nsis.sourceforge.io/Download`, `nsis-3.12-src.tar.bz2`). The bzip2 module is
  not used. The text goes verbatim into `licenses/NSIS-COPYING.txt`, which the setup installs (§5.1) and the zip does
  not carry; the license table gains a row for it ("Windows setup only"). DialShift's own installer script is not
  third-party.
- **CHANGELOG `[Unreleased]`**, `.claude/skills/release-packaging/SKILL.md` (release lane; D7's "installer later"
  becomes the setup), `AGENTS.md` (the Windows command line and the brief list), `scripts/native-check/` (NC-19),
  matrix §12 statuses, D93–D99 updates with commits and runs, open items. All in the same change as the code they
  describe.

## 11. Risks and gotchas

- **Settings are sacred.** The only recursive deletes are the generated `DialShift.new-/old-<id>` folders (never
  through a reparse point) and, on an explicit **No**, `%LOCALAPPDATA%\DialShift`. The install folder is never
  deleted recursively; R3 and R4 keep `/D=` from pointing the swap or the uninstaller at the settings or at foreign
  files.
- **NSIS replaces an unusable `/D=` silently** (before `.onInit`, with the `InstallDirRegKey` folder or `InstallDir`)
  and ignores a `/D=` that is quoted or lowercase, so a typo in automation would upgrade the existing install or
  install into the default folder with exit 0. R3 (a) refuses both instead (D101; CI run `36226968746` exposed it
  with `/D=<subst root>`, which reinstalled into the existing install).
- **Stale files after an upgrade** would load: LibVLC scans `libvlc\win-x64\plugins`, so an in-place overwrite could
  mix VLC versions. The staged swap removes the whole old build.
- **The uninstaller returns before it finishes** (it runs from a temporary copy); tests and scripts must wait or use
  `_?=`.
- **`$LOCALAPPDATA` in NSIS is the shell's known folder**, not the environment variable: CI isolates with `/D=`, and
  the Start menu, desktop and HKCU of the runner are real (cleaned in `finally`).
- **Running-copy detection** uses the executable's write lock; an antivirus scan can hold the file briefly and cause a
  false "running", which Retry resolves. The swap's rename retries cover the rest.
- **Unsigned installers** draw SmartScreen warnings and sometimes heuristic antivirus flags; Windows 11 **Smart App
  Control**, when on, blocks unsigned executables with no "Run anyway" (the zip's `DialShift.exe` too). NC-19 records
  what happens; the README says it.
- **Files the setup extracts carry no Mark of the Web**, unlike files from a zip extracted by Explorer, so the
  installed `DialShift.exe` should start without a second SmartScreen prompt (NC-19 records it).
- **Homebrew moves on.** A new Homebrew `makensis` fails the cross-build job and `release-local.sh` until the pin (and
  CI's zip) is bumped in one change, with a decision; Homebrew cannot install an older version.
- **SourceForge downloads** can be slow or fail: cached by SHA-256, retried, and the hash is always checked.
- **The setup does not change the app:** no new `IStartupRegistration` behavior, no settings change, no
  `Settings.Version` bump, no new NuGet package.

## 12. Definition of done + acceptance rows (INS)

**Definition of done:** `DialShift-Setup-win-x64.exe` is built by one script on the Mac and on `windows-latest` from
the verified package with NSIS 3.12, verified statically and by content, exercised silently on `windows-latest`
(install, upgrade, coexistence, refusals, uninstall), published with every release beside the unchanged zip and macOS
download and covered by `SHA256SUMS.txt`; README, notices, CHANGELOG, skill and matrix are current; the four quality
gates hold (no dead code; docs current in the same change; QG-03 for the wizard; test → fix → retest until green).

| ID | Behavior | Evidence |
|---|---|---|
| INS-01 | `build-win-setup.sh` builds the setup on the Mac with Homebrew makensis `v3.12`; refuses another makensis version, a version not matching the csproj or the payload's `DialShift.dll`, a package missing `DialShift.exe`/`DialShift.dll`/`app-catalog.json` | local run log (size, SHA-256) and each refusal's message |
| INS-02 | The same script builds on `windows-latest` with the pinned `nsis-3.12.zip` (SHA-256 checked after download and cache restore) | CI step log |
| INS-03 | Deterministic: two builds are byte-identical, file times do not matter; Mac-built = Windows-built from the same zip, or a recorded decision and content equivalence | local double build; `setup-crossbuild` summary |
| INS-04 | `verify-win-setup.sh` static checks pass on the real setup; the §7 fixtures are rejected | verifier and fixture runs, both OSes |
| INS-05 | Contents: the installer holds exactly the package minus `Install.ps1` plus `licenses\NSIS-COPYING.txt` | 7-Zip content check with `--require-contents` in CI |
| INS-06 | Fresh silent install: exit 0, tree = payload + uninstaller, Start menu shortcut, no desktop shortcut, no Run value created, no DialShift started, settings untouched | CI (s1) |
| INS-07 | Apps & features entry: every §5.7 value, `EstimatedSize` exact | CI (s1), (s2); NC-19 for the Settings UI |
| INS-08 | Upgrade setup over setup: old files gone, exact-name leftovers removed, junction and foreign folder kept, Run value moved, desktop shortcut kept, entry rewritten, settings untouched | CI (s2) |
| INS-09 | Setup over an `Install.ps1` install in the same folder: one install, one shortcut, entry created | CI (s9) |
| INS-10 | `Install.ps1` refuses over a setup install (D95); its seven existing CI cases still pass | CI (s7) and the `Install.ps1` step |
| INS-11 | DialShift running: setup and uninstaller refuse (exit 10), nothing changed; interactive Retry/Cancel wording | CI (s5); NC-19 step 4 |
| INS-12 | Locked file (exit 14, install restored) and held install lock (exit 11), nothing changed | CI (s3), (s4) |
| INS-13 | Folder refusals R3/R4 (exit 13, nothing created), each through its own rule; the setup installs in exactly the `/D=` folder or refuses, and the uninstaller refuses a root (D101); valid `/D=` forms (a trailing backslash, spaces, a non-ASCII letter) install; R1 by review (no 32-bit or pre-10 runner) | CI (s6), (s9); review of the script |
| INS-14 | Silent uninstall: payload, uninstaller, shortcuts, key removed; Run + StartupApproved removed only when pointing here; a user file and its folder kept; settings kept | CI (s8), (s9) |
| INS-15 | Interactive uninstall asks to keep settings: Yes (default) keeps, No deletes exactly `%LOCALAPPDATA%\DialShift` | NC-19 step 8; review |
| INS-16 | Wizard UX (QG-03): pages of §5.10, branding, icon, DPI, keyboard, messages that name the fix | NC-19 steps 3–4; design review of screenshots |
| INS-17 | `release.yml` publishes four assets; `SHA256SUMS.txt` covers the three downloads; the notes list the setup | the next release's run and release page |
| INS-18 | `release-local.sh` builds, verifies and uploads the setup; its notes name the unchecked install cases | a dry run's output (`artifacts/release-local/<tag>/`) |
| INS-19 | Docs and notices current: README, THIRD-PARTY-NOTICES (NSIS section) + `licenses/NSIS-COPYING.txt`, CHANGELOG, skill, AGENTS.md, matrix, decisions | docs review |
| INS-20 | Real PC: SmartScreen / Smart App Control / Defender recorded, Apps & features, upgrade of a `0.4.0` `Install.ps1` install with launch at sign-in on, uninstall | NC-19 |

## 13. Native check (NC-19)

**Windows setup on a real PC** (Windows 11 x64, a real sign-in; a second run on Windows 10 if one is available).
(1) With `0.4.0` installed by `Install.ps1`, launch at sign-in on and a few stations and slots, download
`DialShift-Setup-win-x64.exe` with a browser (Mark of the Web). (2) Run it: record SmartScreen ("Windows protected your
PC", **More info → Run anyway**), whether Smart App Control is on and what it does, and any Defender verdict. (3) The
wizard: Welcome names the installed `0.4.0` and the folder; no administrator prompt; Components with Desktop shortcut
unchecked; crisp at 150 %; Tab/Enter/Escape work; branding reads `DialShift <version>`. (4) With DialShift running, the
Retry/Cancel message; quit from the tray, Retry continues. (5) Finish with **Run DialShift**: the app opens with the
stations and schedule kept; Settings shows launch at sign-in **on** with no diagnostic; the installed `DialShift.exe`
starts without another SmartScreen prompt; sign out and in: it starts in the tray. (6) **Settings → Apps → Installed
apps**: DialShift with version, publisher `DialShift`, icon and size; no Modify. (7) Run the same setup again
(reinstall), then a newer build's setup (upgrade). (8) Uninstall from Apps & features: the keep-settings question;
**Yes** keeps `%LOCALAPPDATA%\DialShift`; reinstall, uninstall again with **No**: the folder is gone; the Start menu
entry, the desktop shortcut and the Run value are gone. **Pass:** each observation holds; record the Windows build,
the setup's SHA-256 and the release or CI run it came from.

## 14. Open questions (pre-seeded defaults, adopted as D93–D99)

| # | Question | Default (adopted) | Decision |
|---|---|---|---|
| 1 | Installer technology | NSIS 3.12; Inno Setup and WiX rejected (Windows-only builds), MSIX rejected (needs a trusted certificate) | D93 |
| 2 | A running DialShift: refuse or close it? | Refuse with Retry/Cancel (silent: exit 10, D101; the spec's 2 was NSIS's own "aborted" code); never close or kill it | D94 |
| 3 | Choice of install folder | Fixed `%LOCALAPPDATA%\Programs\DialShift`, no directory page; `/D=` for automation only | D94 |
| 4 | How an upgrade replaces files | D56's staged swap, names, retries, lock and leftover rules | D94 |
| 5 | Desktop shortcut | Optional, unchecked, kept when present; never created silently | D94 |
| 6 | Launch after install | Finish-page checkbox, checked; never after a silent install | D94 |
| 7 | Supported Windows | 64-bit Windows 10 or later; refuse otherwise | D94 |
| 8 | `Install.ps1` over a setup install | Refuse with a message (new D56 refusal) | D95 |
| 9 | Settings on uninstall | Ask, default keep; silent keeps | D96 |
| 10 | How the uninstaller deletes | A build-time file list plus empty folders; never `RMDir /r` of the install folder | D96 |
| 11 | Publisher and version in Apps & features | `DialShift`; the full SemVer | D97 |
| 12 | Verifier language and scope | bash + Python 3 standard library; static checks always, contents with 7-Zip | D98 |
| 13 | Byte identity between the Mac and CI | The target; else a decision and content equivalence | D98 |
| 14 | NSIS on the CI runner | The official zip pinned by SHA-256, cached; not Chocolatey | D99 |
| 15 | Signing | Unsigned (D7); SmartScreen note in README and notes | D93 |

## 15. Execution order and file ownership

Orchestrator-run, as briefs 1 and 3: the main agent delegates and reviews; the spec lane verifies each lane's diff
against §12 and reports PASS/FAIL with file and line evidence.

1. **Phase 0 — spec (done by this change):** this brief, D93–D99, matrix §12 and NC-19, open items.
2. **Phase 1 — release lane** (`release-engineer`): `scripts/windows-setup/DialShift.nsi`,
   `scripts/build-win-setup.sh`, `scripts/verify-win-setup.sh`, `licenses/NSIS-COPYING.txt`, the D95 refusal in
   `scripts/Install.ps1`, `build.yml` (§8), `release.yml`, `release-notes.sh`, `release-local.sh`. Gate: INS-01,
   INS-03 (Mac half), INS-04 on the Mac, with real output.
3. **Phase 2 — test lane** (`test-engineer`): the §8.1 step and its cases, the `setup-crossbuild` job's comparison,
   the `test-package-verifiers.sh --win-setup` fixtures (mutation-proven), the native-check kit's NC-19. Gate: the
   fixtures on the Mac; the Windows cases need a CI run.
4. **Phase 3 — Windows CI:** the private repository's Actions are refused for billing (D58), so the maintainer pushes
   the branch to the public repository (as for brief 3, D91); rows that need `windows-latest` stay `WINDOWS-PENDING`
   until that run is green (D75 as extended by D99).
5. **Phase 4 — docs** (README, notices, CHANGELOG, skill, AGENTS.md, matrix statuses, decision updates) in the same
   change as each piece they describe, then the QA and design review of the wizard screenshots (QG-03).

Guardrails: branch `feature/windows-installer`; push only to `private`; the app, `Settings.Version`, the zip and the
macOS package unchanged; no dead code; verify with real command output, never invented results.

## 16. Decision

**Proceed** once the user approves the brief. D93–D99 record the defaults above; a lane that needs to depart from
one records a new decision rather than changing this text.
