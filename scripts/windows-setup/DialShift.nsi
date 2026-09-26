; DialShift Setup: a per-user NSIS installer for the verified win-x64 package (brief 4, docs/windows-installer.md;
; decisions D93-D99). Built only by scripts/build-win-setup.sh, which checks the package and the version, generates
; the three files included below into a temporary folder and passes every host path as a whole -D value in the host's
; own form (Windows paths for makensis.exe): the files DEFINES_NSH, INSTALL_FILES_NSH, UNINSTALL_FILES_NSH, the
; package folder PAYLOAD, the licence NSIS_COPYING, the icon ICON and the setup OUTPUT. This script joins no path to
; them (makensis.exe splits an !include or File path at its last backslash, so "<folder>/<file>" is not found).
;   DEFINES_NSH          VERSION, VERSION_NUMERIC, ESTIMATED_SIZE_KB and the macro PAYLOAD_TOP_LEVEL_NAME (the
;                        payload's top-level names that are not .dll or .json files)
;   INSTALL_FILES_NSH    SetOutPath/File lines for the payload (the package minus Install.ps1, plus
;                        licenses\NSIS-COPYING.txt), in a fixed order so the output does not depend on the host (D98);
;                        its File lines name ${PAYLOAD}\<relative path> with backslashes and ${NSIS_COPYING}
;   UNINSTALL_FILES_NSH  one un.DeleteListed call per installed file, DialShift.exe last, then one RMDir per folder,
;                        deepest first (D96)
;
; Install (D94): refusals before anything changes, then, holding the install lock Local\DialShift.Install until the
; setup exits, remove exact-name leftovers beside the install, extract into DialShift.new-<8 hex> beside it, write the
; uninstaller there, swap it in by renames (as Install.ps1 does, D56), delete the previous copy when it holds nothing but
; DialShift's own files, add the Start menu (and optional desktop) shortcut, move an existing Run value, write the Apps
; & features entry. A failed extraction or swap leaves the previous install unchanged. DialShift is never closed or
; killed. Uninstall (D96): the same lock and running check, a keep-settings question (silent: keep), then only the
; files of the build-time list, empty folders, the shortcuts, the Run/StartupApproved values when they point here, the
; entry. Settings in %LOCALAPPDATA%\DialShift are never touched, except on an explicit No to the uninstaller's question.
; Nothing is ever deleted recursively through a link: every recursive delete is SafeRemoveTree (SafeRemoveTree.nsh).
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
!define RUNNING_MESSAGE "DialShift is running. Quit it from its tray menu (Quit DialShift), then choose Retry."

!define EXIT_RUNNING 10         ; DialShift runs from the folder (R5)
!define EXIT_LOCKED 11          ; another install holds the install lock (R2)
!define EXIT_WINDOWS 12         ; not 64-bit Windows 10 or later (R1)
!define EXIT_FOLDER 13          ; the folder is refused (R3, R4, another install location)
!define EXIT_FAILED 14          ; the extraction, the swap or an uninstall delete failed; the previous state is kept
!define EXIT_CHECK_FAILED 15     ; DialShift.exe could not be opened to check whether it runs

Name "${PRODUCT}"
OutFile "${OUTPUT}"
; The folder Install.ps1 uses (D56). An earlier setup's folder wins; /D=<folder> overrides both (automation only).
InstallDir "$LOCALAPPDATA\Programs\DialShift"
InstallDirRegKey HKCU "${UNINSTALL_KEY}" "InstallLocation"
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

; ---------------------------------------------------------------------------------------------------------------
; Pages (brief 4 section 5.10)

!define MUI_ICON "${ICON}"
!define MUI_UNICON "${ICON}"
!define MUI_ABORTWARNING
!define MUI_UNABORTWARNING

!define MUI_WELCOMEPAGE_TEXT "$WelcomeText"
!insertmacro MUI_PAGE_WELCOME
!define MUI_COMPONENTSPAGE_TEXT_TOP "DialShift is always added to your Start menu. Choose whether to add a desktop shortcut too."
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_TEXT "$FinishText"
!define MUI_FINISHPAGE_RUN "$INSTDIR\DialShift.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Run DialShift"
!insertmacro MUI_PAGE_FINISH

!define MUI_PAGE_CUSTOMFUNCTION_LEAVE un.AskKeepSettings
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!define MUI_FINISHPAGE_TEXT "$UnFinishText"
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
!else
    !define /redef LOCK_MESSAGE "Another DialShift install is running. Wait for it to finish, then run DialShift Setup again."
    !define /redef CHECK_FIX "then run DialShift Setup again"
!endif

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

; Removes trailing backslashes (and slashes) from $INSTDIR, then refuses a drive root (exit 13).
Function RefuseDriveRoot
    ${Do}
        StrCpy $0 $INSTDIR 1 -1
        ${If} $0 != "\"
        ${AndIf} $0 != "/"
            ${Break}
        ${EndIf}
        StrCpy $INSTDIR $INSTDIR -1
    ${Loop}
    StrLen $0 $INSTDIR
    StrCpy $1 $INSTDIR 1 1
    ; LogicLib reads left to right: (length 2 and "X:") or empty.
    ${If} $0 = 2
    ${AndIf} $1 == ":"
    ${OrIf} $0 = 0
        !insertmacro REFUSE ${EXIT_FOLDER} "DialShift can't be installed in $INSTDIR\: choose a folder, not a drive."
    ${EndIf}
FunctionEnd

; $0 = 1 when $DESKTOP\DialShift.lnk starts $INSTDIR\DialShift.exe (IShellLinkW::GetPath through IPersistFile::Load).
Function DesktopShortcutPointsHere
    StrCpy $0 0
    System::Call 'ole32::CoCreateInstance(g "{00021401-0000-0000-C000-000000000046}", p 0, i 1, g "{000214F9-0000-0000-C000-000000000046}", *p .r1) i .r2'
    ${If} $2 <> 0
        Return
    ${EndIf}
    System::Call '$1->0(g "{0000010B-0000-0000-C000-000000000046}", *p .r3) i .r2'
    ${If} $2 = 0
        System::Call '$3->5(w "$DESKTOP\DialShift.lnk", i 0) i .r2'
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
        Call DesktopShortcutPointsHere
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

    ; R3: the folder must not be a drive root, or be, be inside or contain the settings folder (textual,
    ; case-insensitive, one trailing backslash, as D56). The drive check runs before the path is normalized ("C:" alone
    ; would resolve to the current folder of drive C) and after it ("C:\." is the root of C).
    Call RefuseDriveRoot
    GetFullPathName $0 $INSTDIR
    ${If} $0 != ""
        StrCpy $INSTDIR $0
    ${EndIf}
    Call RefuseDriveRoot
    StrCpy $0 "$INSTDIR\"
    StrCpy $1 "$LOCALAPPDATA\DialShift\"
    StrLen $2 $0
    StrLen $3 $1
    StrCpy $4 $0 $3
    StrCpy $5 $1 $2
    ${If} $4 == $1
    ${OrIf} $5 == $0
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

Function un.onInit
    SetShellVarContext current
    StrCpy $KeepSettings 1
    Call un.AcquireInstallLock
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

    Delete "$SMPROGRAMS\DialShift.lnk"
    Delete "$DESKTOP\DialShift.lnk"

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
    ${OrIf} $0 == ""
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
