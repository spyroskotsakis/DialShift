; DialShift Setup: a per-user NSIS installer for the verified win-x64 package (brief 4, docs/windows-installer.md;
; decisions D93-D99, D101). Built only by scripts/build-win-setup.sh, which checks the package and the version, generates
; the three files included below into a temporary folder and passes every host path as a whole -D value in the host's
; own form (Windows paths for makensis.exe): the files DEFINES_NSH, INSTALL_FILES_NSH, UNINSTALL_FILES_NSH, the
; package folder PAYLOAD, the licence NSIS_COPYING, the icon ICON and the setup OUTPUT. This script joins no path to
; them (makensis.exe splits an !include or File path at its last backslash, so "<folder>/<file>" is not found).
;   WIZARD_BMP_<scale>   the Welcome/Finish image at 100, 125, 150, 175, 200, 250 and 300 % (wizard-image.py, D104)
;   DEFINES_NSH          VERSION, VERSION_NUMERIC, ESTIMATED_SIZE_KB, PAYLOAD_LONGEST_FILE and
;                        PAYLOAD_LONGEST_FOLDER (the longest relative paths, for CheckPathLength) and the macro
;                        PAYLOAD_TOP_LEVEL_NAME (the payload's top-level names that are not .dll or .json files)
;   INSTALL_FILES_NSH    SetOutPath/File lines for the payload (the package minus Install.ps1, plus
;                        licenses\NSIS-COPYING.txt), in a fixed order so the output does not depend on the host (D98);
;                        its File lines name ${PAYLOAD}\<relative path> with backslashes and ${NSIS_COPYING}
;   UNINSTALL_FILES_NSH  one un.DeleteListed call per installed file, DialShift.exe last, then one RMDir per folder,
;                        deepest first (D96)
;
; Install (D94): refusals before anything changes (a /D= folder is used exactly as given or refused, D101), then,
; holding the install lock Local\DialShift.Install until the setup exits, remove exact-name leftovers beside the
; install, extract into DialShift.new-<8 hex> beside it, write the uninstaller there, swap it in by renames (as
; Install.ps1 does, D56), delete the previous copy when it holds nothing but DialShift's own files, add the Start menu
; (and optional desktop) shortcut, move an existing Run value, write the Apps & features entry. A failed extraction or
; swap leaves the previous install unchanged. DialShift is never closed or killed. Uninstall (D96): the same lock, the
; _?= folder exactly as given and an ordinary absolute path (D101), the running check, a keep-settings question (silent:
; keep), then only the files of the build-time list and empty folders, and the shortcuts, the Run/StartupApproved
; values and the entry only when they point here. Settings in
; %LOCALAPPDATA%\DialShift are never touched, except on an explicit No to the uninstaller's question. Nothing is ever
; deleted recursively through a link: every recursive delete is SafeRemoveTree (SafeRemoveTree.nsh).
;
; Exit codes of the setup and the uninstaller: 0 done, 1 cancelled by the user (NSIS), and from 10 on, so none meets
; NSIS's own 2 ("aborted by script"): the EXIT_* defines below.
;
; ASCII only; NSIS's own includes and the System plugin only. Deterministic (D98): SetDateSave off, no timestamps,
; random values or host paths in the output (the staging id is generated at install time).

Unicode true
ManifestDPIAware true
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetDateSave off
AllowSkipFiles off
XPStyle on

!include "${DEFINES_NSH}"

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "Sections.nsh"
!include "x64.nsh"
!include "WinVer.nsh"
!addincludedir "${__FILEDIR__}"
!include "SafeRemoveTree.nsh"

!define PRODUCT "DialShift"
; DialShift.exe's CompanyName (the .NET SDK default), shown as the publisher in Apps & features (D97).
!define PUBLISHER "DialShift"
!define ABOUT_URL "https://github.com/spyroskotsakis/DialShift"
!define UNINSTALLER "Uninstall DialShift.exe"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\DialShift"
!define RUN_KEY "Software\Microsoft\Windows\CurrentVersion\Run"
!define APPROVED_KEY "Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"
!define LOCK_NAME "Local\DialShift.Install"
!define OWN_MESSAGE "This uninstaller won't remove files from $INSTDIR: that isn't the DialShift install it belongs to. Run the Uninstall DialShift.exe in the folder you want to uninstall (/D= does not apply to the uninstaller)."
!define RUNNING_MESSAGE "DialShift is running. Quit it from its tray menu (Quit DialShift), then choose Retry."

!define EXIT_RUNNING 10         ; DialShift runs from the folder (R5)
!define EXIT_LOCKED 11          ; another install holds the install lock (R2)
!define EXIT_WINDOWS 12         ; not 64-bit Windows 10 or later (R1)
!define EXIT_FOLDER 13          ; the folder is refused (R3, R4, another install location; the uninstaller's root)
!define EXIT_FAILED 14          ; the extraction, the swap or an uninstall delete failed; the previous state is kept
!define EXIT_CHECK_FAILED 15     ; DialShift.exe could not be opened to check whether it runs
; The most of the command line NSIS keeps ($CMDLINE, its /D= and the System plugin's copy; lstrcpyn with
; NSIS_MAX_STRLEN): CheckRequestedFolder refuses a longer one, whose /D= could be cut short or unseen (D101).
!define /math CMDLINE_MAX ${NSIS_MAX_STRLEN} - 1

Name "${PRODUCT}"
OutFile "${OUTPUT}"
; The folder Install.ps1 uses (D56). An earlier setup's folder wins; /D=<folder> overrides both (automation only), and
; a /D= folder is used as given or refused, never replaced (CheckRequestedFolder, D101).
InstallDir "$LOCALAPPDATA\Programs\DialShift"
InstallDirRegKey HKCU "${UNINSTALL_KEY}" "InstallLocation"
; Without it, NSIS silently replaces a drive or share root given with /D= before .onInit runs; with it, the root
; reaches .onInit, where CheckFolderPath refuses it (R3, D101). It also admits a root as the uninstaller's _?=, which
; un.onInit refuses the same way.
AllowRootDirInstall true
BrandingText "DialShift ${VERSION}"

; The version resource matches DialShift.exe's (D97), whose copyright text is a single space.
VIProductVersion "${VERSION_NUMERIC}.0"
VIFileVersion "${VERSION_NUMERIC}.0"
VIAddVersionKey "ProductName" "${PRODUCT}"
VIAddVersionKey "CompanyName" "${PUBLISHER}"
VIAddVersionKey "FileDescription" "DialShift Setup"
VIAddVersionKey "FileVersion" "${VERSION_NUMERIC}.0"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "LegalCopyright" " "

Var WelcomeText
Var FinishText
Var UnFinishText
Var InstallLock
Var Parent
Var Staging
Var Old
Var Phase
Var DesktopExisted
Var KeepSettings
Var UnFailed
Var Kind            ; ClassifyFolder: empty, install, foreign or mixed
Var Foreign         ; ClassifyFolder: the first top-level entry that is not DialShift's
Var IsInstall       ; 1 when the install folder holds an install to swap out
Var UnStartMenuHere ; the uninstaller: 1 when $SMPROGRAMS\DialShift.lnk starts this install
Var UnDesktopHere   ; the uninstaller: 1 when $DESKTOP\DialShift.lnk starts this install

; ---------------------------------------------------------------------------------------------------------------
; Pages (brief 4 section 5.10)

!define MUI_ICON "${ICON}"
!define MUI_UNICON "${ICON}"
; DialShift's own Welcome/Finish image (D104), not the Modern UI's stock art: the 100 % drawing here, replaced on show by
; the drawing for the display's scale (WizardImage).
!define MUI_WELCOMEFINISHPAGE_BITMAP "${WIZARD_BMP_100}"
!define MUI_UNWELCOMEFINISHPAGE_BITMAP "${WIZARD_BMP_100}"
!define MUI_ABORTWARNING
!define MUI_UNABORTWARNING

!define MUI_WELCOMEPAGE_TEXT "$WelcomeText"
!define MUI_PAGE_CUSTOMFUNCTION_SHOW WelcomeImage
!insertmacro MUI_PAGE_WELCOME
!define MUI_COMPONENTSPAGE_TEXT_TOP "DialShift is always added to your Start menu. Choose whether to add a desktop shortcut too."
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_TEXT "$FinishText"
!define MUI_FINISHPAGE_RUN "$INSTDIR\DialShift.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Run DialShift"
!define MUI_PAGE_CUSTOMFUNCTION_SHOW FinishImage
!insertmacro MUI_PAGE_FINISH

!define MUI_PAGE_CUSTOMFUNCTION_LEAVE un.AskKeepSettings
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!define MUI_FINISHPAGE_TEXT "$UnFinishText"
!define MUI_PAGE_CUSTOMFUNCTION_SHOW un.FinishImage
!insertmacro MUI_UNPAGE_FINISH

!insertmacro MUI_LANGUAGE "English"

; ---------------------------------------------------------------------------------------------------------------
; Helpers shared by the installer and the uninstaller (the un. copies are what the uninstaller calls).

; Exits with a code after telling an interactive user why; a silent run shows nothing (brief 4 section 5.4).
!macro REFUSE CODE MESSAGE
    MessageBox MB_OK|MB_ICONSTOP "${MESSAGE}" /SD IDOK
    SetErrorLevel ${CODE}
    Quit
!macroend

!insertmacro SAFE_REMOVE_TREE ""
!insertmacro SAFE_REMOVE_TREE "un."

!macro HELPERS UN
!if "${UN}" == "un."
    !define /redef LOCK_MESSAGE "Another DialShift install is running. Wait for it to finish, then uninstall DialShift again."
    !define /redef CHECK_FIX "then uninstall DialShift again"
    !define /redef ROOT_MESSAGE "This uninstaller won't remove files from $INSTDIR\, the top of a drive or network share: DialShift Setup never installs there. Delete the uninstaller yourself."
    !define /redef PATH_MESSAGE "This uninstaller won't remove files from $R9: that isn't an ordinary full path to a folder. Give the install folder as one, such as _?=C:\Apps\DialShift."
    !define /redef FOLDER_SWITCH " _?="
    !define /redef FOLDER_MESSAGE "This uninstaller won't remove files from _?=$2: Windows would read that as another folder. Put _?= last on the command line, followed by the install folder as a full path with backslashes, such as _?=C:\Apps\DialShift."
    !define /redef LONG_MESSAGE "This uninstaller's command line is too long to read ($1 characters). Keep it to ${CMDLINE_MAX} characters or fewer."
!else
    !define /redef LOCK_MESSAGE "Another DialShift install is running. Wait for it to finish, then run DialShift Setup again."
    !define /redef CHECK_FIX "then run DialShift Setup again"
    !define /redef ROOT_MESSAGE "DialShift can't be installed in $INSTDIR\, the top of a drive or network share. Choose a folder in it, such as $INSTDIR\DialShift."
    !define /redef PATH_MESSAGE "DialShift can't be installed in $R9: that isn't an ordinary full path to a folder. Give one such as C:\Apps\DialShift or \\server\share\DialShift."
    !define /redef FOLDER_SWITCH " /D="
    !define /redef FOLDER_MESSAGE "/D=$2 isn't a folder DialShift can be installed in. Put /D= last on the command line, followed by a full path with backslashes on a drive that exists, such as /S /D=C:\Apps\DialShift."
    !define /redef LONG_MESSAGE "DialShift Setup's command line is too long to read ($1 characters). Keep it to ${CMDLINE_MAX} characters or fewer, for example with a shorter /D= folder."
!endif

; R3 (D101): the folder the command line names is the folder used, or the run stops (exit 13): the setup's /D= and the
; uninstaller's _?=, which NSIS both read before .onInit / un.onInit. Changes $0-$5.
; - NSIS takes /D= only as the last switch, written in capitals, after a space, and _?= from its last " _?=": the rest
;   of the command line, quotes and all, is the folder. It cuts " /D=<folder>" or " _?=<folder>" off $CMDLINE, so the
;   process's own command line (GetCommandLineW) holds that switch exactly at the length of $CMDLINE. The setup's
;   NSIS replaces a /D= folder it can't use (no drive or share root, as "DialShift" or "C:DialShift"; a drive that
;   doesn't exist; a file in the path; nothing) with the InstallDirRegKey folder or InstallDir; the uninstaller's
;   refuses such a _?= itself (exit 2). Both copies hold at most CMDLINE_MAX (1023) characters, so a longer command
;   line, whose folder NSIS may have cut short or never seen, is refused first; one of exactly CMDLINE_MAX is whole.
; - Rule: $INSTDIR, as NSIS set it and as every read returns it, must equal the switch's text with only its trailing
;   backslashes and spaces removed, character for character (S!=). Every read of $INSTDIR goes through NSIS's
;   validate_filename, which removes trailing backslashes and spaces (the same folder) but also control characters and
;   *?|<>/": after the drive (another folder: "C:\t3/DialShift" reads "C:\t3DialShift", "C:\t\Dial|Shift"
;   "C:\t\DialShift"). So a folder NSIS replaced, and one it kept that reads as another (a slash, a quote, a wildcard,
;   or text after it such as " /S" or " /NCRC"), are refused.
; - A \\?\ text must not end with a space before its trailing backslashes: \\?\ names "DialShift " literally, the
;   ordinary form "DialShift". (Trailing backslashes alone keep the same folder.)
; - The uninstaller always has " _?=" (NSIS starts it only with one); the setup without /D= has nothing to check here.
;   An uninstaller started without _?= (Apps & features, a double-click) is a first process that runs no script: it
;   relaunches its temporary copy with _?=<$INSTDIR>\, and NSIS gives that first process a /D= too, so the copy gets a
;   /D= folder already cleaned by NSIS, and this check sees that text, not what was typed. un.CheckOwnFolder covers it.
Function ${UN}CheckNamedFolder
    System::Call 'kernel32::GetCommandLineW() p .r5'
    System::Call 'kernel32::lstrlenW(p r5) i .r1'
    ${If} $1 > ${CMDLINE_MAX}
        !insertmacro REFUSE ${EXIT_FOLDER} "${LONG_MESSAGE}"
    ${EndIf}
    System::Call 'kernel32::GetCommandLineW() w .r0'
    StrLen $1 $CMDLINE
    StrCpy $2 $0 4 $1
    ${If} $2 S== "${FOLDER_SWITCH}"
        IntOp $1 $1 + 4
        StrCpy $2 $0 "" $1
        ; Without its trailing backslashes, then without its trailing spaces too.
        StrCpy $3 $2
        ${Do}
            StrCpy $4 $3 1 -1
            ${If} $4 != "\"
                ${Break}
            ${EndIf}
            StrCpy $3 $3 -1
        ${Loop}
        StrCpy $4 $3 1 -1
        StrCpy $5 $2 4
        ${If} $5 == "\\?\"
        ${AndIf} $4 == " "
            !insertmacro REFUSE ${EXIT_FOLDER} "${FOLDER_MESSAGE}"
        ${EndIf}
        ${Do}
            StrCpy $4 $3 1 -1
            ${If} $4 != "\"
            ${AndIf} $4 != " "
                ${Break}
            ${EndIf}
            StrCpy $3 $3 -1
        ${Loop}
        ${If} $INSTDIR S!= $3
            !insertmacro REFUSE ${EXIT_FOLDER} "${FOLDER_MESSAGE}"
        ${EndIf}
!if "${UN}" == "un."
    ${Else}
        StrCpy $2 ""
        !insertmacro REFUSE ${EXIT_FOLDER} "${FOLDER_MESSAGE}"
!endif
    ${EndIf}
FunctionEnd

; $R5 = the length of the root of the absolute path $R0: 2 for "C:...", else ("\\server\share...") the position of the
; backslash after the share name, or the whole length. Changes $R3-$R6.
Function ${UN}RootLength
    StrCpy $R3 $R0 1 1
    ${If} $R3 == ":"
        StrCpy $R5 2
        Return
    ${EndIf}
    StrLen $R5 $R0
    StrCpy $R3 2
    StrCpy $R6 0
    ${DoWhile} $R3 < $R5
        StrCpy $R4 $R0 1 $R3
        ${If} $R4 == "\"
            IntOp $R6 $R6 + 1
            ${If} $R6 = 2
                StrCpy $R5 $R3
                Return
            ${EndIf}
        ${EndIf}
        IntOp $R3 $R3 + 1
    ${Loop}
FunctionEnd

; $INSTDIR with the part of it that exists spelled with long names: GetLongPathNameW on its deepest existing folder,
; the rest appended as given. So InstallLocation and the Run value are written in one spelling, and an 8.3 spelling of
; the settings folder ("C:\Users\CROS~MYY\...") meets the settings-folder rule. A root ("C:\", "\\server\share") is
; never looked up. Changes $R0-$R6.
Function ${UN}LongForm
    StrCpy $R0 $INSTDIR
    Call ${UN}RootLength
    StrCpy $R1 ""
    ${Do}
        System::Call 'kernel32::GetLongPathNameW(w R0, w .R2, i ${NSIS_MAX_STRLEN}) i .R3'
        ${If} $R3 > 0
        ${AndIf} $R3 < ${NSIS_MAX_STRLEN}
            StrCpy $INSTDIR "$R2$R1"
            Return
        ${EndIf}
        ; Move the last part of $R0 to the front of $R1, until only the root would be left.
        StrLen $R3 $R0
        ${Do}
            IntOp $R3 $R3 - 1
            ${If} $R3 <= $R5
                Return
            ${EndIf}
            StrCpy $R4 $R0 1 $R3
            ${If} $R4 == "\"
                ${Break}
            ${EndIf}
        ${Loop}
        StrCpy $R4 $R0 "" $R3
        StrCpy $R1 "$R4$R1"
        StrCpy $R0 $R0 $R3
    ${Loop}
FunctionEnd

; $0 = 1 when the shortcut $R0 starts $INSTDIR\DialShift.exe (IShellLinkW::GetPath through IPersistFile::Load, compared
; case-insensitively). The uninstaller removes a DialShift.lnk only then, and the setup a desktop one it was asked not to
; keep. Changes $0-$4.
Function ${UN}ShortcutPointsHere
    StrCpy $0 0
    System::Call 'ole32::CoCreateInstance(g "{00021401-0000-0000-C000-000000000046}", p 0, i 1, g "{000214F9-0000-0000-C000-000000000046}", *p .r1) i .r2'
    ${If} $2 <> 0
        Return
    ${EndIf}
    System::Call '$1->0(g "{0000010B-0000-0000-C000-000000000046}", *p .r3) i .r2'
    ${If} $2 = 0
        System::Call '$3->5(w R0, i 0) i .r2'
        ${If} $2 = 0
            System::Call '$1->3(w .r4, i ${NSIS_MAX_STRLEN}, p 0, i 0) i .r2'
            ${If} $2 = 0
            ${AndIf} $4 == "$INSTDIR\DialShift.exe"
                StrCpy $0 1
            ${EndIf}
        ${EndIf}
        System::Call '$3->2()'
    ${EndIf}
    System::Call '$1->2()'
FunctionEnd

; $2 = 1 when $INSTDIR is a root: a drive ("C:", which is how NSIS reads "C:\") or a network server or share
; ("\\server", "\\server\share"), which has at most one backslash after its leading two. No read of $INSTDIR ends
; with a backslash (validate_filename), so there is none to strip here. Changes $0-$4.
Function ${UN}IsRootFolder
    StrLen $0 $INSTDIR
    StrCpy $1 $INSTDIR 1 1
    StrCpy $2 0
    ${If} $0 = 2
    ${AndIf} $1 == ":"
        StrCpy $2 1
    ${EndIf}
    StrCpy $1 $INSTDIR 2
    ${If} $1 == "\\"
        StrCpy $2 1
        StrCpy $3 0
        StrCpy $4 2
        ${DoWhile} $4 < $0
            StrCpy $1 $INSTDIR 1 $4
            ${If} $1 == "\"
                IntOp $3 $3 + 1
            ${EndIf}
            IntOp $4 $4 + 1
        ${Loop}
        ${If} $3 > 1
            StrCpy $2 0
        ${EndIf}
    ${EndIf}
FunctionEnd

; $2 = 1 when $INSTDIR is absolute: a drive letter A-Z and a colon, then nothing or a backslash; or two backslashes and
; a server name (not "\\?", "\\." or a third backslash). Changes $0-$4.
Function ${UN}IsAbsoluteFolder
    StrCpy $2 0
    StrCpy $0 $INSTDIR 1 1
    StrCpy $1 $INSTDIR 1 2
    ${If} $0 == ":"
        ${If} $1 == ""
        ${OrIf} $1 == "\"
            StrCpy $1 $INSTDIR 1
            StrCpy $3 "ABCDEFGHIJKLMNOPQRSTUVWXYZ"
            StrCpy $4 0
            ${Do}
                StrCpy $0 $3 1 $4
                ${If} $0 == ""
                    ${Break}
                ${EndIf}
                ${If} $0 == $1
                    StrCpy $2 1
                    ${Break}
                ${EndIf}
                IntOp $4 $4 + 1
            ${Loop}
        ${EndIf}
    ${Else}
        StrCpy $0 $INSTDIR 2
        ${If} $0 == "\\"
        ${AndIf} $1 != ""
        ${AndIf} $1 != "\"
        ${AndIf} $1 != "?"
        ${AndIf} $1 != "."
            StrCpy $2 1
        ${EndIf}
    ${EndIf}
FunctionEnd

; R3 (exit 13, D101): $INSTDIR becomes an ordinary, normalized, absolute path that is not a root, or the run stops. For
; the setup's /D= and, since AllowRootDirInstall also admits a root there, the uninstaller's _?= (an uninstaller given
; a root or a relative path would delete DialShift-named files from it, or from the working folder). In order:
; - A leading \\?\ is dropped ("\\?\C:\x" is "C:\x", "\\?\UNC\server\share\x" is "\\server\share\x"), so the
;   settings-folder rule, the one-install rule, InstallLocation, the Run value and the shortcuts see the ordinary
;   form. What remains must be absolute: any other \\?\ path ("\\?\Volume{...}\x", "\\?\GLOBALROOT\...") would be
;   left relative, and is refused.
; - Not a root before it is normalized ("C:" alone would resolve to the current folder of drive C).
; - Normalized with kernel32's GetFullPathNameW, not NSIS's GetFullPathName, which returns "" when the last part does
;   not exist yet and so left "C:\x\..\new" as it was. A \\?\ path must already be in normal form: Windows reads
;   "\\?\C:\x\DialShift." or "\\?\C:\x\..\y" literally, but their ordinary form as another folder.
; - Absolute and not a root after it ("C:\x\.." is the root of C); then its existing part in long names (LongForm).
; Changes $0-$4, $R0-$R6, $R8 and $R9 (the folder as given, for the messages).
Function ${UN}CheckFolderPath
    StrCpy $R9 $INSTDIR
    StrCpy $R8 0
    StrCpy $0 $INSTDIR 4
    ${If} $0 == "\\?\"
        StrCpy $R8 1
        StrCpy $0 $INSTDIR 4 4
        ${If} $0 == "UNC\"
            StrCpy $0 $INSTDIR "" 7
            StrCpy $INSTDIR "\$0"
        ${Else}
            StrCpy $INSTDIR $INSTDIR "" 4
        ${EndIf}
    ${EndIf}
    Call ${UN}IsAbsoluteFolder
    ${If} $2 = 0
        !insertmacro REFUSE ${EXIT_FOLDER} "${PATH_MESSAGE}"
    ${EndIf}
    Call ${UN}IsRootFolder
    ${If} $2 = 1
        !insertmacro REFUSE ${EXIT_FOLDER} "${ROOT_MESSAGE}"
    ${EndIf}
    System::Call 'kernel32::GetFullPathNameW(w "$INSTDIR", i ${NSIS_MAX_STRLEN}, w .r0, p 0) i .r1'
    ${If} $1 = 0
    ${OrIf} $1 >= ${NSIS_MAX_STRLEN}
        !insertmacro REFUSE ${EXIT_FOLDER} "${PATH_MESSAGE}"
    ${EndIf}
    ${If} $R8 = 1
    ${AndIf} $0 != $INSTDIR
        !insertmacro REFUSE ${EXIT_FOLDER} "${PATH_MESSAGE}"
    ${EndIf}
    StrCpy $INSTDIR $0
    Call ${UN}IsAbsoluteFolder
    ${If} $2 = 0
        !insertmacro REFUSE ${EXIT_FOLDER} "${PATH_MESSAGE}"
    ${EndIf}
    Call ${UN}IsRootFolder
    ${If} $2 = 1
        !insertmacro REFUSE ${EXIT_FOLDER} "${ROOT_MESSAGE}"
    ${EndIf}
    Call ${UN}LongForm
FunctionEnd

; $R1 = the text of the Windows error code in $R0 (FormatMessage), without the final period and line break.
Function ${UN}ErrorText
    System::Call 'kernel32::FormatMessageW(i 0x1200, p 0, i R0, i 0, w .R1, i ${NSIS_MAX_STRLEN}, p 0) i .R2'
    ${If} $R2 = 0
        StrCpy $R1 "Windows error $R0"
        Return
    ${EndIf}
    ${Do}
        StrCpy $R2 $R1 1 -1
        ${If} $R2 == "$\r"
        ${OrIf} $R2 == "$\n"
        ${OrIf} $R2 == "."
        ${OrIf} $R2 == " "
            StrCpy $R1 $R1 -1
        ${Else}
            ${Break}
        ${EndIf}
    ${Loop}
FunctionEnd

; R2: one install or uninstall at a time, shared with Install.ps1 (D56, D94). Waits 5 s; an abandoned lock (a run that
; ended without releasing it) counts as acquired. The handle stays open, so the lock is held until this process exits.
Function ${UN}AcquireInstallLock
    System::Call 'kernel32::CreateMutexW(p 0, i 0, w "${LOCK_NAME}") p .r0'
    ${If} $0 P= 0
        !insertmacro REFUSE ${EXIT_LOCKED} "${LOCK_MESSAGE}"
    ${EndIf}
    System::Call 'kernel32::WaitForSingleObject(p r0, i 5000) i .r1'
    ; WAIT_OBJECT_0 (0) or WAIT_ABANDONED (0x80): acquired. WAIT_TIMEOUT or WAIT_FAILED: held by another run.
    ${If} $1 <> 0
    ${AndIf} $1 <> 0x80
        System::Call 'kernel32::CloseHandle(p r0)'
        !insertmacro REFUSE ${EXIT_LOCKED} "${LOCK_MESSAGE}"
    ${EndIf}
    StrCpy $InstallLock $0
FunctionEnd

; R5: Windows refuses to open the image of a running executable for writing with a sharing violation (error 32), so
; DialShift.exe runs when that open fails with 32 (the retired WPF app had the same file name). Retry/Cancel; a silent
; run cancels. Any other failure is not taken for "running": it is reported with its reason. The file is opened, never
; written, and shared for reading, writing and deleting. Out: $0 = 0 to go on, else the exit code (EXIT_RUNNING after
; Cancel, EXIT_CHECK_FAILED). Changes $1, $R0-$R2.
Function ${UN}CheckNotRunning
    ${Do}
        System::Call 'kernel32::CreateFileW(w "$INSTDIR\DialShift.exe", i 0x40000000, i 7, p 0, i 3, i 0x80, p 0) p .r0 ?e'
        Pop $1
        ${If} $0 <> -1
            System::Call 'kernel32::CloseHandle(p r0)'
            StrCpy $0 0
            Return
        ${EndIf}
        ; No DialShift.exe (file or path not found): nothing can run from here.
        ${If} $1 = 2
        ${OrIf} $1 = 3
            StrCpy $0 0
            Return
        ${EndIf}
        ${If} $1 <> 32
            StrCpy $R0 $1
            Call ${UN}ErrorText
            MessageBox MB_OK|MB_ICONSTOP "Could not open $INSTDIR\DialShift.exe to check whether DialShift is running ($R1). Make sure the folder is not read-only or blocked by security software, ${CHECK_FIX}." /SD IDOK
            StrCpy $0 ${EXIT_CHECK_FAILED}
            Return
        ${EndIf}
        ${If} ${Cmd} `MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "${RUNNING_MESSAGE}" /SD IDCANCEL IDCANCEL`
            StrCpy $0 ${EXIT_RUNNING}
            Return
        ${EndIf}
    ${Loop}
FunctionEnd

; $Parent = the folder that holds $INSTDIR (no trailing backslash).
Function ${UN}SetParent
    StrCpy $0 $INSTDIR
    StrLen $1 $0
    ${Do}
        IntOp $1 $1 - 1
        ${If} $1 < 1
            StrCpy $Parent $INSTDIR
            Return
        ${EndIf}
        StrCpy $2 $0 1 $1
        ${If} $2 == "\"
            StrCpy $Parent $0 $1
            Return
        ${EndIf}
    ${Loop}
FunctionEnd

; Removes what an earlier run (of this setup or of Install.ps1) left beside the install, only under the exact generated
; names ^DialShift\.(new|old)-[0-9a-f]{8}$ (case-sensitive), never a junction or symbolic link (and never through one:
; SafeRemoveTree), and keeps a DialShift.old-* while there is no install, because it is then the only copy of the
; previous install (D56, D94).
Function ${UN}RemoveLeftovers
    StrCpy $5 0
    ${If} ${FileExists} "$INSTDIR\*.*"
        StrCpy $5 1
    ${EndIf}
    FindFirst $3 $4 "$Parent\DialShift.*"
    ${DoWhile} $4 != ""
        StrLen $0 $4
        StrCpy $1 $4 14
        StrCpy $2 0
        ${If} $0 = 22
            ${If} $1 S== "DialShift.new-"
                StrCpy $2 1
            ${ElseIf} $1 S== "DialShift.old-"
            ${AndIf} $5 = 1
                StrCpy $2 1
            ${EndIf}
        ${EndIf}
        ; The last eight characters: lowercase hexadecimal digits only.
        StrCpy $6 14
        ${Do}
            ${If} $2 = 0
            ${OrIf} $6 >= 22
                ${Break}
            ${EndIf}
            StrCpy $7 $4 1 $6
            StrCpy $8 0
            ${Do}
                StrCpy $1 "0123456789abcdef" 1 $8
                ${If} $1 == ""
                    StrCpy $2 0
                    ${Break}
                ${EndIf}
                ${If} $1 S== $7
                    ${Break}
                ${EndIf}
                IntOp $8 $8 + 1
            ${Loop}
            IntOp $6 $6 + 1
        ${Loop}
        ${If} $2 = 1
            ; A folder, and not a reparse point (FILE_ATTRIBUTE_DIRECTORY 0x10 set, FILE_ATTRIBUTE_REPARSE_POINT 0x400 clear).
            System::Call 'kernel32::GetFileAttributesW(w "$Parent\$4") i .r0'
            ${If} $0 <> -1
                IntOp $1 $0 & 0x410
                ${If} $1 = 0x10
                    Push "$Parent\$4"
                    Call ${UN}SafeRemoveTree
                    ${If} ${FileExists} "$Parent\$4\*.*"
                        DetailPrint "Could not remove $Parent\$4, left by an earlier install."
                    ${Else}
                        DetailPrint "Removed $Parent\$4, left by an earlier install."
                    ${EndIf}
                ${EndIf}
            ${EndIf}
        ${EndIf}
        FindNext $3 $4
    ${Loop}
    FindClose $3
FunctionEnd
!macroend

!insertmacro HELPERS ""
!insertmacro HELPERS "un."

; ---------------------------------------------------------------------------------------------------------------
; The Welcome/Finish image at the display's scale (D104). The Modern UI shows the 100 % drawing stretched to its image
; control (109 x 193 dialog units: 164 x 314 pixels at 100 %, 328 x 628 at 200 %); this puts the drawing whose width is
; nearest the control's in its place, fitted exactly (LoadAndSetImage deletes the bitmap it replaces). In: $R0 the image
; control. Out: $R1 the new bitmap, which the page frees when it ends, or "" when the 100 % one stays. Changes $R2-$R4.
!macro WIZARD_IMAGE UN
Function ${UN}WizardImage
    System::Call '*(&i16) p .R2'
    System::Call 'user32::GetClientRect(p R0, p R2)'
    System::Call '*$R2(&i8, i .R3)'
    System::Free $R2
    StrCpy $R1 ""
    ; Widths 164, 205, 246, 287, 328, 410 and 492; the thresholds are the midpoints between them.
    ${If} $R3 >= 451
        File "/oname=$PLUGINSDIR\dialshift-wizard-300.bmp" "${WIZARD_BMP_300}"
        StrCpy $R4 "$PLUGINSDIR\dialshift-wizard-300.bmp"
    ${ElseIf} $R3 >= 369
        File "/oname=$PLUGINSDIR\dialshift-wizard-250.bmp" "${WIZARD_BMP_250}"
        StrCpy $R4 "$PLUGINSDIR\dialshift-wizard-250.bmp"
    ${ElseIf} $R3 >= 308
        File "/oname=$PLUGINSDIR\dialshift-wizard-200.bmp" "${WIZARD_BMP_200}"
        StrCpy $R4 "$PLUGINSDIR\dialshift-wizard-200.bmp"
    ${ElseIf} $R3 >= 267
        File "/oname=$PLUGINSDIR\dialshift-wizard-175.bmp" "${WIZARD_BMP_175}"
        StrCpy $R4 "$PLUGINSDIR\dialshift-wizard-175.bmp"
    ${ElseIf} $R3 >= 226
        File "/oname=$PLUGINSDIR\dialshift-wizard-150.bmp" "${WIZARD_BMP_150}"
        StrCpy $R4 "$PLUGINSDIR\dialshift-wizard-150.bmp"
    ${ElseIf} $R3 >= 185
        File "/oname=$PLUGINSDIR\dialshift-wizard-125.bmp" "${WIZARD_BMP_125}"
        StrCpy $R4 "$PLUGINSDIR\dialshift-wizard-125.bmp"
    ${Else}
        Return
    ${EndIf}
    ${NSD_SetStretchedImage} $R0 "$R4" $R1
FunctionEnd

Function ${UN}FinishImage
    StrCpy $R0 $mui.FinishPage.Image
    Call ${UN}WizardImage
    ${If} $R1 != ""
        StrCpy $mui.FinishPage.Image.Bitmap $R1
    ${EndIf}
FunctionEnd
!macroend

!insertmacro WIZARD_IMAGE ""
!insertmacro WIZARD_IMAGE "un."

Function WelcomeImage
    StrCpy $R0 $mui.WelcomePage.Image
    Call WizardImage
    ${If} $R1 != ""
        StrCpy $mui.WelcomePage.Image.Bitmap $R1
    ${EndIf}
FunctionEnd

; ---------------------------------------------------------------------------------------------------------------
; Installer

; DialShift.exe's product version (the assembly informational version), without build metadata; empty if unreadable.
; In: $R0 the file. Out: $R1.
Function ReadProductVersion
    StrCpy $R1 ""
    System::Call 'version::GetFileVersionInfoSizeW(w R0, p 0) i .R2'
    ${If} $R2 > 0
        System::Alloc $R2
        Pop $R3
        System::Call 'version::GetFileVersionInfoW(w R0, i 0, i R2, p R3) i .R4'
        ${If} $R4 <> 0
            System::Call 'version::VerQueryValueW(p R3, w "\VarFileInfo\Translation", *p .R5, *i .R6) i .R4'
            ${If} $R4 <> 0
            ${AndIf} $R6 >= 4
                System::Call '*$R5(&i2 .R7, &i2 .R8)'
                IntFmt $R7 "%04x" $R7
                IntFmt $R8 "%04x" $R8
                System::Call 'version::VerQueryValueW(p R3, w "\StringFileInfo\$R7$R8\ProductVersion", *p .R5, *i .R6) i .R4'
                ${If} $R4 <> 0
                ${AndIf} $R6 > 0
                    System::Call 'kernel32::lstrcpynW(w .R1, p R5, i ${NSIS_MAX_STRLEN}) p'
                ${EndIf}
            ${EndIf}
        ${EndIf}
        System::Free $R3
    ${EndIf}
    ; 0.4.0+3c8543c... -> 0.4.0
    StrLen $R2 $R1
    StrCpy $R3 0
    ${DoWhile} $R3 < $R2
        StrCpy $R4 $R1 1 $R3
        ${If} $R4 == "+"
            StrCpy $R1 $R1 $R3
            ${Break}
        ${EndIf}
        IntOp $R3 $R3 + 1
    ${Loop}
FunctionEnd

; Renames $R0 to $R1, trying 10 times 400 ms apart (antivirus and indexer handles on a fresh folder are brief, D56).
; Out: $R2 = 0, or the Windows error code of the last attempt.
Function MoveWithRetry
    StrCpy $R3 0
    ${Do}
        System::Call 'kernel32::MoveFileW(w R0, w R1) i .R2 ?e'
        Pop $R4
        ${If} $R2 <> 0
            StrCpy $R2 0
            Return
        ${EndIf}
        IntOp $R3 $R3 + 1
        ${If} $R3 >= 10
            StrCpy $R2 $R4
            Return
        ${EndIf}
        Sleep 400
    ${Loop}
FunctionEnd

; Whether the top-level entry $R7 of the folder $R6 belongs to a DialShift build: Install.ps1, the uninstaller, a
; top-level name of this build's payload, or a .dll or .json file (the runtime files an older or newer build may have
; and this one not). Out: $R5 = 1 or 0. Changes $R4.
Function IsDialShiftEntry
    StrCpy $R5 0
    ${If} $R7 == "Install.ps1"
    ${OrIf} $R7 == "${UNINSTALLER}"
        StrCpy $R5 1
    ${EndIf}
    !insertmacro PAYLOAD_TOP_LEVEL_NAME $R7 $R5
    ${IfNot} ${FileExists} "$R6\$R7\*.*"
        StrCpy $R4 $R7 "" -4
        ${If} $R4 == ".dll"
            StrCpy $R5 1
        ${EndIf}
        StrCpy $R4 $R7 "" -5
        ${If} $R4 == ".json"
            StrCpy $R5 1
        ${EndIf}
    ${EndIf}
FunctionEnd

; What the folder $R6 holds. Out: $Kind = empty (missing, or no entries), install (a DialShift install: it holds the
; uninstaller, or DialShift.exe, DialShift.dll and libvlc\ as Install.ps1 leaves it, and nothing else), mixed (such an
; install plus entries that are not DialShift's) or foreign (anything else, a file in the folder's place included);
; $Foreign = the first entry that is not DialShift's, if any. Changes $R3-$R5, $R7.
Function ClassifyFolder
    StrCpy $Kind "empty"
    StrCpy $Foreign ""
    ${IfNot} ${FileExists} "$R6\*.*"
        ${If} ${FileExists} "$R6"
            StrCpy $Kind "foreign"
        ${EndIf}
        Return
    ${EndIf}
    FindFirst $R3 $R7 "$R6\*.*"
    ${DoWhile} $R7 != ""
        ${If} $R7 != "."
        ${AndIf} $R7 != ".."
            StrCpy $Kind "files"
            Call IsDialShiftEntry
            ${If} $R5 = 0
            ${AndIf} $Foreign == ""
                StrCpy $Foreign $R7
            ${EndIf}
        ${EndIf}
        FindNext $R3 $R7
    ${Loop}
    FindClose $R3
    ${If} $Kind == "empty"
        Return
    ${EndIf}
    StrCpy $Kind "foreign"
    ${If} ${FileExists} "$R6\${UNINSTALLER}"
        StrCpy $Kind "install"
    ${ElseIf} ${FileExists} "$R6\DialShift.exe"
    ${AndIf} ${FileExists} "$R6\DialShift.dll"
    ${AndIf} ${FileExists} "$R6\libvlc\*.*"
        StrCpy $Kind "install"
    ${EndIf}
    ${If} $Kind == "install"
    ${AndIf} $Foreign != ""
        StrCpy $Kind "mixed"
    ${EndIf}
FunctionEnd

; R4 (exit 13): the install folder must be missing, empty, or a DialShift install holding nothing else; a DialShift.exe
; alone does not make a folder an install (an extracted zip in Downloads). Sets $IsInstall.
Function CheckInstallFolder
    StrCpy $R6 $INSTDIR
    Call ClassifyFolder
    ${If} $Kind == "foreign"
        !insertmacro REFUSE ${EXIT_FOLDER} "$INSTDIR already holds other files. Choose an empty folder or the folder DialShift is installed in."
    ${ElseIf} $Kind == "mixed"
        !insertmacro REFUSE ${EXIT_FOLDER} "$INSTDIR holds files that are not part of DialShift, such as $Foreign. Move them out of that folder, then run DialShift Setup again."
    ${EndIf}
    StrCpy $IsInstall 0
    ${If} $Kind == "install"
        StrCpy $IsInstall 1
    ${EndIf}
FunctionEnd

; R3 (D101): the setup installs in exactly the folder /D= names (CheckNamedFolder), or stops (exit 13); and a /D= that
; NSIS did not take (quoted, lowercase, no space before it) is left in $CMDLINE: any "/D=" in the arguments there, in
; any case, is refused too, rather than installing in the default folder. Changes $0-$5.
Function CheckRequestedFolder
    Call CheckNamedFolder
    ; The arguments start after the program name, as NSIS reads it: up to the closing quote, else the first space.
    StrLen $1 $CMDLINE
    StrCpy $2 $CMDLINE 1
    StrCpy $3 " "
    StrCpy $4 0
    ${If} $2 == '"'
        StrCpy $3 '"'
        StrCpy $4 1
    ${EndIf}
    ${DoWhile} $4 < $1
        StrCpy $2 $CMDLINE 1 $4
        ${If} $2 == $3
            ${Break}
        ${EndIf}
        IntOp $4 $4 + 1
    ${Loop}
    ${DoWhile} $4 < $1
        StrCpy $2 $CMDLINE 3 $4
        ${If} $2 == "/D="
            !insertmacro REFUSE ${EXIT_FOLDER} "DialShift Setup could not read its /D= switch. Put it last on the command line, in capitals and without quotes, even when the folder has spaces: /S /D=C:\My Apps\DialShift."
        ${EndIf}
        IntOp $4 $4 + 1
    ${Loop}
FunctionEnd

; $R1 = the absolute path $R0 as Windows resolves it: GetFinalPathNameByHandleW ("\\?\C:\..." or "\\?\UNC\...") on its
; deepest existing folder, the root last ("C:\", "\\server\share\"), the rest appended as given; "" when nothing of it
; can be opened. It resolves 8.3 names, subst drives, junctions, folder symbolic links and mount points, for the
; settings-folder rule only (D101). Changes $R1-$R8.
Function FinalForm
    Call RootLength
    StrCpy $R7 $R5
    StrCpy $R2 $R0
    StrCpy $R8 ""
    StrCpy $R1 ""
    ${Do}
        ; FILE_READ_ATTRIBUTES, all sharing, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS (needed to open a folder).
        System::Call 'kernel32::CreateFileW(w R2, i 0x80, i 7, p 0, i 3, i 0x02000000, p 0) p .R3'
        ${If} $R3 <> -1
            System::Call 'kernel32::GetFinalPathNameByHandleW(p R3, w .R4, i ${NSIS_MAX_STRLEN}, i 0) i .R6'
            System::Call 'kernel32::CloseHandle(p R3)'
            ${If} $R6 > 0
            ${AndIf} $R6 < ${NSIS_MAX_STRLEN}
                StrCpy $R3 $R4 1 -1
                ${If} $R3 == "\"
                    StrCpy $R4 $R4 -1
                ${EndIf}
                StrCpy $R1 "$R4$R8"
            ${EndIf}
            Return
        ${EndIf}
        ; The root was the last try.
        StrCpy $R3 $R2 1 -1
        ${If} $R3 == "\"
            Return
        ${EndIf}
        ; Up one folder; at the root's backslash, the root itself with its backslash.
        StrLen $R3 $R2
        ${Do}
            IntOp $R3 $R3 - 1
            StrCpy $R4 $R2 1 $R3
            ${If} $R4 == "\"
            ${OrIf} $R3 <= $R7
                ${Break}
            ${EndIf}
        ${Loop}
        StrCpy $R4 $R2 "" $R3
        StrCpy $R8 "$R4$R8"
        ${If} $R3 <= $R7
            IntOp $R3 $R7 + 1
        ${EndIf}
        StrCpy $R2 $R2 $R3
    ${Loop}
FunctionEnd

; $R2 = 1 when the folder $R0 is, is inside or contains the folder $R1: compared as text, case-insensitively, each with
; one trailing backslash (as D56). Changes $R2-$R6.
Function OverlapsFolder
    StrCpy $R3 "$R0\"
    StrCpy $R4 "$R1\"
    StrLen $R5 $R3
    StrLen $R6 $R4
    StrCpy $R2 0
    StrCpy $R5 $R3 $R6
    ${If} $R5 == $R4
        StrCpy $R2 1
        Return
    ${EndIf}
    StrLen $R5 $R3
    StrCpy $R6 $R4 $R5
    ${If} $R6 == $R3
        StrCpy $R2 1
    ${EndIf}
FunctionEnd

; R3 (exit 13, D101): every payload file and folder must fit Windows' path limits (MAX_PATH: a file path of at most
; 259 characters, a folder path of at most 247) both in the install folder and in the DialShift.new-/old-<8 hex>
; folders beside it; the setup is not long-path aware, so a longer path would fail the extraction instead. Changes
; $0-$3.
Function CheckPathLength
    Call SetParent
    StrLen $0 $Parent
    IntOp $0 $0 + 23        ; \DialShift.new-<8 hex>
    StrLen $1 $INSTDIR
    ${If} $1 > $0
        StrCpy $0 $1
    ${EndIf}
    IntOp $1 $0 + 1
    IntOp $2 $1 + ${PAYLOAD_LONGEST_FILE}
    IntOp $3 $1 + ${PAYLOAD_LONGEST_FOLDER}
    ${If} $2 > 259
    ${OrIf} $3 > 247
        !insertmacro REFUSE ${EXIT_FOLDER} "DialShift can't be installed in $INSTDIR: its path is too long for DialShift's files (Windows allows 259 characters in a path). Choose a folder with a shorter path, such as C:\Apps\DialShift."
    ${EndIf}
FunctionEnd

; Stops the install with EXIT_FAILED after the extraction or the swap failed; the previous install is unchanged.
!macro FAIL_INSTALL MESSAGE
    DetailPrint "${MESSAGE}"
    MessageBox MB_OK|MB_ICONSTOP "${MESSAGE}" /SD IDOK
    SetErrorLevel ${EXIT_FAILED}
    Abort "${MESSAGE}"
!macroend

Section "DialShift" SEC_APP
    SectionIn RO

    ; R5 again: DialShift may have been started while the wizard was open.
    Call CheckNotRunning
    ${If} $0 <> 0
        SetErrorLevel $0
        Abort "${RUNNING_MESSAGE}"
    ${EndIf}
    ; R4 again, inside the lock: the folder may have changed while the wizard was open.
    Call CheckInstallFolder

    Call SetParent
    CreateDirectory "$Parent"
    ; The working folder stays outside the install and the staging folder, so both can be renamed.
    SetOutPath "$Parent"
    Call RemoveLeftovers

    ; A fresh random id per run, never a name that exists.
    ${Do}
        System::Call 'advapi32::SystemFunction036(*i .r0, i 4) i .r1'
        ${If} $1 = 0
            System::Call 'kernel32::GetTickCount() i .r0'
        ${EndIf}
        IntFmt $0 "%08x" $0
        StrCpy $Staging "$Parent\DialShift.new-$0"
        StrCpy $Old "$Parent\DialShift.old-$0"
        ${IfNot} ${FileExists} "$Staging"
        ${AndIfNot} ${FileExists} "$Old"
            ${Break}
        ${EndIf}
    ${Loop}

    DetailPrint "Extracting DialShift ${VERSION} into $Staging"
    StrCpy $Phase "extract"
    !include "${INSTALL_FILES_NSH}"
    ClearErrors
    WriteUninstaller "$Staging\${UNINSTALLER}"
    ${If} ${Errors}
        SetOutPath "$Parent"
        Push "$Staging"
        Call SafeRemoveTree
        StrCpy $Phase "failed"
        !insertmacro FAIL_INSTALL "Could not write $Staging\${UNINSTALLER}. Nothing was changed."
    ${EndIf}
    SetOutPath "$Parent"

    ; Swap (D56): the install aside, the new build in; on failure, the previous install back.
    StrCpy $Phase "swap"
    ${If} $IsInstall = 1
        StrCpy $R0 "$INSTDIR"
        StrCpy $R1 "$Old"
        Call MoveWithRetry
        ${If} $R2 <> 0
            StrCpy $R0 $R2
            Call ErrorText
            Push "$Staging"
            Call SafeRemoveTree
            StrCpy $Phase "failed"
            !insertmacro FAIL_INSTALL "Could not move the previous install in $INSTDIR aside ($R1). It is unchanged; quit programs using files in it and run DialShift Setup again."
        ${EndIf}
    ${ElseIf} ${FileExists} "$INSTDIR\*.*"
        ; An empty folder (R4 allows no other): make room for the rename.
        RMDir "$INSTDIR"
    ${EndIf}
    StrCpy $R0 "$Staging"
    StrCpy $R1 "$INSTDIR"
    Call MoveWithRetry
    ${If} $R2 <> 0
        StrCpy $R0 $R2
        Call ErrorText
        StrCpy $R6 $R1
        Push "$Staging"
        Call SafeRemoveTree
        StrCpy $Phase "failed"
        ${If} $IsInstall = 1
            StrCpy $R0 "$Old"
            StrCpy $R1 "$INSTDIR"
            Call MoveWithRetry
            ${If} $R2 <> 0
                !insertmacro FAIL_INSTALL "Could not replace $INSTDIR ($R6), and could not move the previous install back. Rename $Old to DialShift to restore it."
            ${EndIf}
        ${EndIf}
        !insertmacro FAIL_INSTALL "Could not move the new build into $INSTDIR ($R6). The previous install is unchanged; quit programs using files in it and run DialShift Setup again."
    ${EndIf}
    StrCpy $Phase "done"

    ; The new build is in place: no file of the old one survives (a stale LibVLC plugin would load). The previous copy
    ; is deleted only when it holds nothing but DialShift's own files, and never through a link. An old copy that stays
    ; is only a warning (exit 0).
    StrCpy $FinishText "DialShift ${VERSION} is installed. Find it in your Start menu; uninstall it in Settings > Apps."
    ${If} $IsInstall = 1
        StrCpy $R6 "$Old"
        Call ClassifyFolder
        ${If} $Foreign == ""
            Push "$Old"
            Call SafeRemoveTree
        ${Else}
            DetailPrint "The previous copy holds $Foreign, which is not part of DialShift, so it was not deleted."
        ${EndIf}
        ${If} ${FileExists} "$Old\*.*"
            DetailPrint "DialShift is installed, but the previous copy could not be deleted. Delete $Old yourself."
            StrCpy $FinishText "$FinishText$\r$\n$\r$\nThe previous copy could not be deleted. Delete $Old yourself."
        ${EndIf}
    ${EndIf}

    ; CreateShortcut takes the working folder from SetOutPath.
    SetOutPath "$INSTDIR"
    CreateShortcut "$SMPROGRAMS\DialShift.lnk" "$INSTDIR\DialShift.exe" "" "$INSTDIR\DialShift.exe" 0 SW_SHOWNORMAL "" "Your radio, on time."
SectionEnd

Section /o "Desktop shortcut" SEC_DESKTOP
    SetOutPath "$INSTDIR"
    CreateShortcut "$DESKTOP\DialShift.lnk" "$INSTDIR\DialShift.exe" "" "$INSTDIR\DialShift.exe" 0 SW_SHOWNORMAL "" "Your radio, on time."
SectionEnd

Section "-Register"
    ; Unchecked on the Components page although it existed: the user asked for no desktop shortcut to this install. A
    ; shortcut to another copy is not this install's.
    ${IfNot} ${SectionIsSelected} ${SEC_DESKTOP}
    ${AndIf} $DesktopExisted = 1
        StrCpy $R0 "$DESKTOP\DialShift.lnk"
        Call ShortcutPointsHere
        ${If} $0 = 1
            Delete "$DESKTOP\DialShift.lnk"
        ${EndIf}
    ${EndIf}

    ; Launch at sign-in (D94): an existing Run value, whatever it points at, now starts this install with --tray, as
    ; Install.ps1 does. None is created, and the StartupApproved switch is not touched.
    StrCpy $0 0
    ${Do}
        ClearErrors
        EnumRegValue $1 HKCU "${RUN_KEY}" $0
        ${If} ${Errors}
        ${OrIf} $1 == ""
            ${Break}
        ${EndIf}
        ${If} $1 == "DialShift"
            WriteRegStr HKCU "${RUN_KEY}" "DialShift" '"$INSTDIR\DialShift.exe" --tray'
            ${Break}
        ${EndIf}
        IntOp $0 $0 + 1
    ${Loop}

    ; Apps & features (D97), rewritten on every install.
    DeleteRegKey HKCU "${UNINSTALL_KEY}"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "${PRODUCT}"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${VERSION}"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "${PUBLISHER}"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" '"$INSTDIR\DialShift.exe",0'
    WriteRegDWORD HKCU "${UNINSTALL_KEY}" "EstimatedSize" ${ESTIMATED_SIZE_KB}
    WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\${UNINSTALLER}"'
    WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '"$INSTDIR\${UNINSTALLER}" /S'
    WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "URLInfoAbout" "${ABOUT_URL}"
    WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
    WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
    DetailPrint "Installed DialShift ${VERSION} in $INSTDIR."
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
    !insertmacro MUI_DESCRIPTION_TEXT ${SEC_APP} "DialShift ${VERSION}, installed for your user account in $INSTDIR, with a Start menu shortcut."
    !insertmacro MUI_DESCRIPTION_TEXT ${SEC_DESKTOP} "A DialShift shortcut on your desktop."
!insertmacro MUI_FUNCTION_DESCRIPTION_END

Function .onInit
    SetShellVarContext current
    StrCpy $Phase "check"

    ; R1: 64-bit Windows 10 or later; the setup is a 32-bit program that installs the x64 app.
    ${IfNot} ${RunningX64}
    ${OrIfNot} ${AtLeastWin10}
        !insertmacro REFUSE ${EXIT_WINDOWS} "DialShift needs 64-bit Windows 10 or later."
    ${EndIf}

    ; R2, held from here until the setup exits.
    Call AcquireInstallLock

    ; R3: a /D= folder exactly as given (D101); an ordinary absolute path, normalized, not a drive or share root
    ; (CheckFolderPath); short enough for the payload (CheckPathLength); not, not inside and not holding the settings
    ; folder (textual, case-insensitive, one trailing backslash, as D56).
    Call CheckRequestedFolder
    Call CheckFolderPath
    Call CheckPathLength
    ; The settings rule as text (D56), then as Windows resolves both folders (D101): an alias through a subst drive, a
    ; junction, a folder symbolic link, a mount point or an 8.3 name is the settings folder too.
    StrCpy $R0 $INSTDIR
    StrCpy $R1 "$LOCALAPPDATA\DialShift"
    Call OverlapsFolder
    ${If} $R2 = 0
        StrCpy $R0 $INSTDIR
        Call FinalForm
        StrCpy $R9 $R1
        StrCpy $R0 "$LOCALAPPDATA\DialShift"
        Call FinalForm
        ${If} $R9 != ""
        ${AndIf} $R1 != ""
            StrCpy $R0 $R9
            Call OverlapsFolder
        ${EndIf}
    ${EndIf}
    ${If} $R2 = 1
        !insertmacro REFUSE ${EXIT_FOLDER} "DialShift can't be installed in $INSTDIR: that folder holds your DialShift settings. Choose another folder."
    ${EndIf}

    ; R4.
    Call CheckInstallFolder

    ; One install per user: the Apps & features entry is one key. A setup install elsewhere that still has its
    ; uninstaller is refused rather than losing its entry.
    ReadRegStr $0 HKCU "${UNINSTALL_KEY}" "InstallLocation"
    ${If} $0 != ""
    ${AndIf} $0 != $INSTDIR
    ${AndIf} ${FileExists} "$0\${UNINSTALLER}"
        !insertmacro REFUSE ${EXIT_FOLDER} "DialShift is already installed in $0. To install it in $INSTDIR, uninstall it in Settings > Apps first."
    ${EndIf}

    ; R5.
    Call CheckNotRunning
    ${If} $0 <> 0
        SetErrorLevel $0
        Quit
    ${EndIf}

    ; The Welcome page: fresh install or upgrade (brief 4 section 5.3). The installed version comes from the entry of an
    ; earlier setup in this folder, else from DialShift.exe (an Install.ps1 install).
    ${If} $IsInstall = 1
        StrCpy $R1 ""
        ReadRegStr $0 HKCU "${UNINSTALL_KEY}" "InstallLocation"
        ${If} $0 == $INSTDIR
            ReadRegStr $R1 HKCU "${UNINSTALL_KEY}" "DisplayVersion"
        ${EndIf}
        ${If} $R1 == ""
        ${AndIf} ${FileExists} "$INSTDIR\DialShift.exe"
            StrCpy $R0 "$INSTDIR\DialShift.exe"
            Call ReadProductVersion
        ${EndIf}
        ${If} $R1 == ""
            StrCpy $WelcomeText "An earlier version of DialShift is installed in $INSTDIR."
        ${Else}
            StrCpy $WelcomeText "DialShift $R1 is installed in $INSTDIR."
        ${EndIf}
        StrCpy $WelcomeText "$WelcomeText Setup will replace it with ${VERSION}. Your stations, schedule and settings are kept."
    ${Else}
        StrCpy $WelcomeText "Setup will install DialShift ${VERSION} for your user account in $INSTDIR, with a Start menu shortcut."
    ${EndIf}
    StrCpy $WelcomeText "$WelcomeText$\r$\n$\r$\nNo administrator rights are needed.$\r$\n$\r$\nClick Next to continue."

    ; The desktop shortcut is optional: unchecked, or checked when it already exists (then an upgrade refreshes it).
    ; A silent run keeps that default, so it never creates one and refreshes an existing one.
    StrCpy $DesktopExisted 0
    ${If} ${FileExists} "$DESKTOP\DialShift.lnk"
        StrCpy $DesktopExisted 1
        !insertmacro SelectSection ${SEC_DESKTOP}
    ${EndIf}
FunctionEnd

; Extraction failed (a File error the user cancelled, or a silent run): nothing was swapped yet, so the staging folder
; is removed and the previous install is unchanged.
Function .onInstFailed
    ${If} $Phase == "extract"
        SetOutPath "$Parent"
        Push "$Staging"
        Call SafeRemoveTree
        SetErrorLevel ${EXIT_FAILED}
    ${EndIf}
FunctionEnd

; ---------------------------------------------------------------------------------------------------------------
; Uninstaller (D96). NSIS runs it from a temporary copy, so $INSTDIR is the folder of "Uninstall DialShift.exe".

; $R1 = "<size high>/<size low>" and $R8 = the last-write time (a 64-bit count of 100 ns) of the file $R0, from its
; directory entry (FindFirstFileW), or $R1 = "" when it has none or isn't a regular file: a folder, or a name-surrogate
; reparse point (a symbolic link or a junction: IsReparseTagNameSurrogate, bit 0x20000000 of the tag). Other reparse
; points, such as a deduplicated file or a cloud placeholder, count as files. Changes $R1-$R8.
Function un.FileStamp
    StrCpy $R1 ""
    StrCpy $R8 ""
    System::Call '*(&i592) p .R2'
    System::Call 'kernel32::FindFirstFileW(w R0, p R2) p .R3'
    ${If} $R3 = -1
        System::Free $R2
        Return
    ${EndIf}
    System::Call 'kernel32::FindClose(p R3)'
    ; WIN32_FIND_DATAW: attributes; creation and last access (16 bytes, skipped: each register is named once, since the
    ; System plugin keeps the first field a repeated register is given); last write (low, high); size (high, low); the
    ; reparse tag (dwReserved0).
    System::Call '*$R2(i .R3, &i16, i .R4, i .R5, i .R6, i .R7, i .R8)'
    System::Free $R2
    IntOp $R2 $R3 & 0x10
    ${If} $R2 <> 0
        StrCpy $R8 ""
        Return
    ${EndIf}
    IntOp $R2 $R3 & 0x400
    ${If} $R2 <> 0
        IntOp $R2 $R8 & 0x20000000
        ${If} $R2 <> 0
            StrCpy $R8 ""
            Return
        ${EndIf}
    ${EndIf}
    StrCpy $R1 "$R6/$R7"
    ; high * 2^32 + low, the low half read as unsigned.
    ${If} $R4 < 0
        System::Int64Op $R4 + 4294967296
        Pop $R4
    ${EndIf}
    System::Int64Op $R5 * 4294967296
    Pop $R8
    System::Int64Op $R8 + $R4
    Pop $R8
FunctionEnd

; The folder must be the DialShift install this uninstaller belongs to (exit 13, D101). An uninstaller started without
; _?= runs as a first process that runs no script and relaunches its temporary copy (in "$TEMP\~nsu<n>.tmp", made with
; CopyFile, which keeps the size and the last-write time) with _?=<$INSTDIR>\; NSIS gives that first process a /D= too
; (cut off its command line), and $INSTDIR is then the /D= text already cleaned, so CheckNamedFolder cannot see what was
; typed ("/D=C:\t/Victim" arrives as "C:\tVictim"). So:
; - the folder must hold Uninstall DialShift.exe as a regular file (FileStamp; every install does; it is deleted last);
; - a temporary copy must have that file's size, and a last-write time within 2 s of it (the copy keeps it exactly on
;   NTFS; 2 s is FAT's resolution, so a copy in a $TEMP on any file system matches; the creation time is not compared,
;   since a copy gets a new one), so another install, of any build, written more than 2 s apart is refused. Residual:
;   another install of the same build whose uninstaller was written within those 2 s.
; A file whose directory entry can't be read refuses. Changes $0-$4, $R0-$R8.
Function un.CheckOwnFolder
    StrCpy $R0 "$INSTDIR\${UNINSTALLER}"
    Call un.FileStamp
    ${If} $R1 == ""
        !insertmacro REFUSE ${EXIT_FOLDER} "${OWN_MESSAGE}"
    ${EndIf}
    StrCpy $3 $R1
    StrCpy $4 $R8
    ; The name of the folder this copy runs from.
    StrLen $1 $EXEDIR
    ${Do}
        IntOp $1 $1 - 1
        ${If} $1 < 0
            ${Break}
        ${EndIf}
        StrCpy $0 $EXEDIR 1 $1
        ${If} $0 == "\"
            ${Break}
        ${EndIf}
    ${Loop}
    IntOp $1 $1 + 1
    StrCpy $0 $EXEDIR "" $1
    StrCpy $1 $0 4
    StrCpy $2 $0 "" -4
    ${If} $1 == "~nsu"
    ${AndIf} $2 == ".tmp"
        StrCpy $R0 $EXEPATH
        Call un.FileStamp
        ${If} $R1 == ""
        ${OrIf} $R1 S!= $3
            !insertmacro REFUSE ${EXIT_FOLDER} "${OWN_MESSAGE}"
        ${EndIf}
        ; |copy - original| <= 2 s, in 64-bit arithmetic (IntCmp is 32-bit).
        System::Int64Op $R8 - $4
        Pop $0
        System::Int64Op $0 < 0
        Pop $1
        ${If} $1 = 1
            System::Int64Op 0 - $0
            Pop $0
        ${EndIf}
        System::Int64Op $0 > 20000000
        Pop $1
        ${If} $1 = 1
            !insertmacro REFUSE ${EXIT_FOLDER} "${OWN_MESSAGE}"
        ${EndIf}
    ${EndIf}
FunctionEnd

Function un.onInit
    SetShellVarContext current
    StrCpy $KeepSettings 1
    Call un.AcquireInstallLock
    ; _?= exactly as given, then an ordinary absolute path that is not a root (AllowRootDirInstall admits one there
    ; too, and \\?\ could leave a relative path): refused with 13 (D101).
    Call un.CheckNamedFolder
    Call un.CheckFolderPath
    ; An install, and the one this uninstaller belongs to (a relaunch from $TEMP carries a /D= NSIS already cleaned).
    Call un.CheckOwnFolder
    Call un.CheckNotRunning
    ${If} $0 <> 0
        SetErrorLevel $0
        Quit
    ${EndIf}
FunctionEnd

; After Confirm, before anything is removed. Only asked when there are settings to keep; a silent run keeps them.
Function un.AskKeepSettings
    ${If} ${FileExists} "$LOCALAPPDATA\DialShift\*.*"
        ${If} ${Cmd} `MessageBox MB_YESNO|MB_ICONQUESTION|MB_DEFBUTTON1 "Keep your stations, schedule and settings?$\r$\n$\r$\nChoose No to also delete %LOCALAPPDATA%\DialShift (settings, log and settings backups). This can't be undone." /SD IDYES IDNO`
            StrCpy $KeepSettings 0
        ${EndIf}
    ${EndIf}
FunctionEnd

; Deletes one file of the build-time list (relative to $INSTDIR, on the stack); remembers the first that stays.
; DialShift.exe comes last and stays when an earlier file could not be deleted, so the folder is still an install that
; the setup can repair and this uninstaller can finish.
Function un.DeleteListed
    Exch $0
    ${If} $0 == "DialShift.exe"
    ${AndIf} $UnFailed != ""
        Pop $0
        Return
    ${EndIf}
    Delete "$INSTDIR\$0"
    ${If} ${FileExists} "$INSTDIR\$0"
    ${AndIf} $UnFailed == ""
        StrCpy $UnFailed "$INSTDIR\$0"
    ${EndIf}
    Pop $0
FunctionEnd

Section "Uninstall"
    ; Never work inside the install folder: it could not be removed.
    SetOutPath "$TEMP"
    Call un.CheckNotRunning
    ${If} $0 <> 0
        SetErrorLevel $0
        Abort "${RUNNING_MESSAGE}"
    ${EndIf}
    Call un.SetParent
    Call un.RemoveLeftovers
    ; Whether the shortcuts start this install, decided while its DialShift.exe is still there (D101).
    StrCpy $R0 "$SMPROGRAMS\DialShift.lnk"
    Call un.ShortcutPointsHere
    StrCpy $UnStartMenuHere $0
    StrCpy $R0 "$DESKTOP\DialShift.lnk"
    Call un.ShortcutPointsHere
    StrCpy $UnDesktopHere $0

    ; Only the files this build installed, then its folders if empty: never a recursive delete of the install folder,
    ; so a file the user put there stays, with its folder.
    StrCpy $UnFailed ""
    !include "${UNINSTALL_FILES_NSH}"
    ${If} $UnFailed != ""
        StrCpy $0 "Could not delete $UnFailed. Quit DialShift and any program using files in $INSTDIR, then uninstall DialShift again."
        DetailPrint "$0"
        MessageBox MB_OK|MB_ICONSTOP "$0" /SD IDOK
        SetErrorLevel ${EXIT_FAILED}
        Abort "$0"
    ${EndIf}
    ; Fails, as it should, when this is the in-place run (_?=): the caller deletes the uninstaller then.
    Delete "$INSTDIR\${UNINSTALLER}"
    RMDir "$INSTDIR"

    ; The shortcuts, the Run value and the entry belong to this install only when they point at it (D101).
    ${If} $UnStartMenuHere = 1
        Delete "$SMPROGRAMS\DialShift.lnk"
    ${EndIf}
    ${If} $UnDesktopHere = 1
        Delete "$DESKTOP\DialShift.lnk"
    ${EndIf}

    ; Launch at sign-in belongs to this install only when the Run value starts it (the app's own case-insensitive
    ; comparison); a value that points at another copy stays, with its StartupApproved switch.
    StrCpy $1 '"$INSTDIR\DialShift.exe" --tray'
    ClearErrors
    ReadRegStr $0 HKCU "${RUN_KEY}" "DialShift"
    ${IfNot} ${Errors}
    ${AndIf} $0 == $1
        DeleteRegValue HKCU "${RUN_KEY}" "DialShift"
        DeleteRegValue HKCU "${APPROVED_KEY}" "DialShift"
    ${EndIf}

    ReadRegStr $0 HKCU "${UNINSTALL_KEY}" "InstallLocation"
    ${If} $0 == $INSTDIR
        DeleteRegKey HKCU "${UNINSTALL_KEY}"
    ${EndIf}

    StrCpy $UnFinishText "DialShift has been uninstalled."
    ${If} $KeepSettings = 1
        ${If} ${FileExists} "$LOCALAPPDATA\DialShift\*.*"
            StrCpy $UnFinishText "$UnFinishText Your stations, schedule and settings are kept in $LOCALAPPDATA\DialShift."
        ${EndIf}
    ${Else}
        ; Exactly the settings folder, never a DIALSHIFT_DATA_DIR override, and never through a link: a junction in its
        ; place, or in it (a backups folder moved to another drive), loses only the link.
        Push "$LOCALAPPDATA\DialShift"
        Call un.SafeRemoveTree
        ${If} ${FileExists} "$LOCALAPPDATA\DialShift"
            StrCpy $UnFinishText "$UnFinishText Some of your settings files could not be deleted: delete $LOCALAPPDATA\DialShift yourself."
        ${Else}
            StrCpy $UnFinishText "$UnFinishText Your stations, schedule and settings were deleted."
        ${EndIf}
    ${EndIf}
    ${If} ${FileExists} "$INSTDIR\*.*"
    ${AndIfNot} ${FileExists} "$INSTDIR\${UNINSTALLER}"
        StrCpy $UnFinishText "$UnFinishText$\r$\n$\r$\nSome files you added were left in $INSTDIR."
    ${EndIf}
    DetailPrint "$UnFinishText"
SectionEnd
