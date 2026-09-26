; DialShift Setup: a per-user NSIS installer for the verified win-x64 package (brief 4, docs/windows-installer.md;
; decisions D93-D99). Built only by scripts/build-win-setup.sh, which checks the package and the version, generates
; the three files included below into a temporary folder and passes that folder as GENERATED:
;   defines.nsh          VERSION, VERSION_NUMERIC, ESTIMATED_SIZE_KB, ICON, OUTPUT
;   install-files.nsh    SetOutPath/File lines for the payload (the package minus Install.ps1, plus
;                        licenses\NSIS-COPYING.txt), in a fixed order so the output does not depend on the host (D98)
;   uninstall-files.nsh  one un.DeleteListed call per installed file, then one RMDir per folder, deepest first (D96)
;
; Install (D94): refusals R1-R5 before anything changes (exit 4, 3, 4, 4, 2), then, holding the install lock
; Local\DialShift.Install until the setup exits, remove exact-name leftovers beside the install, extract into
; DialShift.new-<8 hex> beside it, write the uninstaller there, swap it in by renames (as Install.ps1 does, D56),
; add the Start menu (and optional desktop) shortcut, move an existing Run value, write the Apps & features entry.
; A failed extraction or swap exits 5 with the previous install unchanged. DialShift is never closed or killed.
; Uninstall (D96): the same lock and running check, a keep-settings question (silent: keep), then only the files of
; the build-time list, empty folders, the shortcuts, the Run/StartupApproved values when they point here, the key.
; Settings in %LOCALAPPDATA%\DialShift are never touched, except on an explicit No to the uninstaller's question.
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

!include "${GENERATED}/defines.nsh"

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "Sections.nsh"
!include "x64.nsh"
!include "WinVer.nsh"

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

Name "${PRODUCT}"
OutFile "${OUTPUT}"
; The folder Install.ps1 uses (D56). An earlier setup's folder wins; /D=<folder> overrides both (automation only).
InstallDir "$LOCALAPPDATA\Programs\DialShift"
InstallDirRegKey HKCU "${UNINSTALL_KEY}" "InstallLocation"
BrandingText "DialShift ${VERSION}"

; The version resource matches DialShift.exe's (D97). DialShift.exe has no copyright text (a single space), so the
; setup has no LegalCopyright key; warning 9100 reports exactly that omission and is the only warning turned off.
!pragma warning disable 9100
VIProductVersion "${VERSION_NUMERIC}.0"
VIFileVersion "${VERSION_NUMERIC}.0"
VIAddVersionKey "ProductName" "${PRODUCT}"
VIAddVersionKey "CompanyName" "${PUBLISHER}"
VIAddVersionKey "FileDescription" "DialShift Setup"
VIAddVersionKey "FileVersion" "${VERSION_NUMERIC}.0"
VIAddVersionKey "ProductVersion" "${VERSION}"

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

!macro HELPERS UN
!if "${UN}" == "un."
    !define /redef LOCK_MESSAGE "Another DialShift install is running. Wait for it to finish, then uninstall DialShift again."
!else
    !define /redef LOCK_MESSAGE "Another DialShift install is running. Wait for it to finish, then run DialShift Setup again."
!endif
; R2: one install or uninstall at a time, shared with Install.ps1 (D56, D94). Waits 5 s; an abandoned lock (a run that
; ended without releasing it) counts as acquired. The handle stays open, so the lock is held until this process exits.
Function ${UN}AcquireInstallLock
    System::Call 'kernel32::CreateMutexW(p 0, i 0, w "${LOCK_NAME}") p .r0'
    ${If} $0 P= 0
        !insertmacro REFUSE 3 "${LOCK_MESSAGE}"
    ${EndIf}
    System::Call 'kernel32::WaitForSingleObject(p r0, i 5000) i .r1'
    ; WAIT_OBJECT_0 (0) or WAIT_ABANDONED (0x80): acquired. WAIT_TIMEOUT or WAIT_FAILED: held by another run.
    ${If} $1 <> 0
    ${AndIf} $1 <> 0x80
        System::Call 'kernel32::CloseHandle(p r0)'
        !insertmacro REFUSE 3 "${LOCK_MESSAGE}"
    ${EndIf}
    StrCpy $InstallLock $0
FunctionEnd

; R5: Windows denies write access to the image of a running executable, so DialShift.exe runs when it cannot be opened
; for writing (the retired WPF app had the same file name). Retry/Cancel; a silent run cancels (exit 2). The file is
; opened, not written. Returns 1 in $0 when the user chose Cancel (the caller exits 2), else 0.
Function ${UN}CheckNotRunning
    ${Do}
        ${IfNot} ${FileExists} "$INSTDIR\DialShift.exe"
            StrCpy $0 0
            Return
        ${EndIf}
        ClearErrors
        FileOpen $0 "$INSTDIR\DialShift.exe" a
        ${IfNot} ${Errors}
            FileClose $0
            StrCpy $0 0
            Return
        ${EndIf}
        ${If} ${Cmd} `MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "${RUNNING_MESSAGE}" /SD IDCANCEL IDCANCEL`
            StrCpy $0 1
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
; names ^DialShift\.(new|old)-[0-9a-f]{8}$ (case-sensitive), never a junction or symbolic link, and keeps a
; DialShift.old-* while there is no install, because it is then the only copy of the previous install (D56, D94).
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
                    RMDir /r "$Parent\$4"
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

; $R1 = "<text>" for a Windows error code in $R0 (FormatMessage), without the final period and line break.
Function ErrorText
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

; Stops the install with exit code 5 after the extraction or the swap failed; the previous install is unchanged.
!macro FAIL_INSTALL MESSAGE
    DetailPrint "${MESSAGE}"
    MessageBox MB_OK|MB_ICONSTOP "${MESSAGE}" /SD IDOK
    SetErrorLevel 5
    Abort "${MESSAGE}"
!macroend

Section "DialShift" SEC_APP
    SectionIn RO

    ; R5 again: DialShift may have been started while the wizard was open.
    Call CheckNotRunning
    ${If} $0 = 1
        SetErrorLevel 2
        Abort "${RUNNING_MESSAGE}"
    ${EndIf}

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
    !include "${GENERATED}/install-files.nsh"
    ClearErrors
    WriteUninstaller "$Staging\${UNINSTALLER}"
    ${If} ${Errors}
        SetOutPath "$Parent"
        RMDir /r "$Staging"
        StrCpy $Phase "failed"
        !insertmacro FAIL_INSTALL "Could not write $Staging\${UNINSTALLER}. Nothing was changed."
    ${EndIf}
    SetOutPath "$Parent"

    ; Swap (D56): the install aside, the new build in; on failure, the previous install back.
    StrCpy $Phase "swap"
    StrCpy $R5 0
    ${If} ${FileExists} "$INSTDIR\*.*"
    ${AndIf} ${FileExists} "$INSTDIR\DialShift.exe"
        StrCpy $R5 1
        StrCpy $R0 "$INSTDIR"
        StrCpy $R1 "$Old"
        Call MoveWithRetry
        ${If} $R2 <> 0
            StrCpy $R0 $R2
            Call ErrorText
            RMDir /r "$Staging"
            StrCpy $Phase "failed"
            !insertmacro FAIL_INSTALL "Could not move the new build into $INSTDIR ($R1). The previous install is unchanged; quit programs using files in it and run DialShift Setup again."
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
        RMDir /r "$Staging"
        StrCpy $Phase "failed"
        ${If} $R5 = 1
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

    ; The new build is in place: no file of the old one survives (a stale LibVLC plugin would load). An old copy that
    ; cannot be deleted is only a warning (exit 0).
    StrCpy $FinishText "DialShift ${VERSION} is installed. Find it in your Start menu; uninstall it in Settings > Apps."
    ${If} $R5 = 1
        RMDir /r "$Old"
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
    ; Unchecked on the Components page although it existed: the user asked for no desktop shortcut.
    ${IfNot} ${SectionIsSelected} ${SEC_DESKTOP}
    ${AndIf} $DesktopExisted = 1
        Delete "$DESKTOP\DialShift.lnk"
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

    ; R1 (exit 4): 64-bit Windows 10 or later; the setup is a 32-bit program that installs the x64 app.
    ${IfNot} ${RunningX64}
    ${OrIfNot} ${AtLeastWin10}
        !insertmacro REFUSE 4 "DialShift needs 64-bit Windows 10 or later."
    ${EndIf}

    ; R2 (exit 3), held from here until the setup exits.
    Call AcquireInstallLock

    ; R3 (exit 4): the folder must not be a drive root, or be, be inside or contain the settings folder (textual,
    ; case-insensitive, one trailing backslash, as D56). Checked before the path is normalized: "C:" alone would
    ; otherwise resolve to the current folder of drive C.
    StrCpy $0 $INSTDIR 1 -1
    ${If} $0 == "\"
        StrCpy $INSTDIR $INSTDIR -1
    ${EndIf}
    StrLen $0 $INSTDIR
    StrCpy $1 $INSTDIR 1 1
    ${If} $0 <= 2
    ${AndIf} $1 == ":"
    ${OrIf} $0 = 0
        !insertmacro REFUSE 4 "DialShift can't be installed in $INSTDIR\: choose a folder, not a drive."
    ${EndIf}
    GetFullPathName $0 $INSTDIR
    ${If} $0 != ""
        StrCpy $INSTDIR $0
    ${EndIf}
    StrCpy $0 $INSTDIR 1 -1
    ${If} $0 == "\"
        StrCpy $INSTDIR $INSTDIR -1
    ${EndIf}
    StrCpy $0 "$INSTDIR\"
    StrCpy $1 "$LOCALAPPDATA\DialShift\"
    StrLen $2 $0
    StrLen $3 $1
    StrCpy $4 $0 $3
    StrCpy $5 $1 $2
    ${If} $4 == $1
    ${OrIf} $5 == $0
        !insertmacro REFUSE 4 "DialShift can't be installed in $INSTDIR: that folder holds your DialShift settings. Choose another folder."
    ${EndIf}

    ; R4 (exit 4): an existing folder must be empty or hold DialShift.exe; a file in the folder's place is refused too.
    ${If} ${FileExists} "$INSTDIR\*.*"
        ${IfNot} ${FileExists} "$INSTDIR\DialShift.exe"
            FindFirst $0 $1 "$INSTDIR\*.*"
            ${DoWhile} $1 != ""
                ${If} $1 != "."
                ${AndIf} $1 != ".."
                    FindClose $0
                    !insertmacro REFUSE 4 "$INSTDIR already holds other files. Choose an empty folder or the folder DialShift is installed in."
                ${EndIf}
                FindNext $0 $1
            ${Loop}
            FindClose $0
        ${EndIf}
    ${ElseIf} ${FileExists} "$INSTDIR"
        !insertmacro REFUSE 4 "$INSTDIR already holds other files. Choose an empty folder or the folder DialShift is installed in."
    ${EndIf}

    ; R5 (exit 2).
    Call CheckNotRunning
    ${If} $0 = 1
        SetErrorLevel 2
        Quit
    ${EndIf}

    ; The Welcome page: fresh install or upgrade (brief 4 section 5.3). The installed version comes from the entry of an
    ; earlier setup in this folder, else from DialShift.exe (an Install.ps1 install).
    ${If} ${FileExists} "$INSTDIR\DialShift.exe"
        StrCpy $R1 ""
        ReadRegStr $0 HKCU "${UNINSTALL_KEY}" "InstallLocation"
        ${If} $0 == $INSTDIR
            ReadRegStr $R1 HKCU "${UNINSTALL_KEY}" "DisplayVersion"
        ${EndIf}
        ${If} $R1 == ""
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
; is removed and the previous install is unchanged (exit 5).
Function .onInstFailed
    ${If} $Phase == "extract"
        SetOutPath "$Parent"
        RMDir /r "$Staging"
        SetErrorLevel 5
    ${EndIf}
FunctionEnd

; ---------------------------------------------------------------------------------------------------------------
; Uninstaller (D96). NSIS runs it from a temporary copy, so $INSTDIR is the folder of "Uninstall DialShift.exe".

Function un.onInit
    SetShellVarContext current
    StrCpy $KeepSettings 1
    Call un.AcquireInstallLock
    Call un.CheckNotRunning
    ${If} $0 = 1
        SetErrorLevel 2
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
Function un.DeleteListed
    Exch $0
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
    ${If} $0 = 1
        SetErrorLevel 2
        Abort "${RUNNING_MESSAGE}"
    ${EndIf}
    Call un.SetParent
    Call un.RemoveLeftovers

    ; Only the files this build installed, then its folders if empty: never a recursive delete of the install folder,
    ; so a file the user put there stays, with its folder.
    StrCpy $UnFailed ""
    !include "${GENERATED}/uninstall-files.nsh"
    ${If} $UnFailed != ""
        StrCpy $0 "Could not delete $UnFailed. Quit DialShift and any program using files in $INSTDIR, then uninstall DialShift again."
        DetailPrint "$0"
        MessageBox MB_OK|MB_ICONSTOP "$0" /SD IDOK
        SetErrorLevel 5
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
        ; Exactly the settings folder, never a DIALSHIFT_DATA_DIR override; a junction in its place loses only the link.
        System::Call 'kernel32::GetFileAttributesW(w "$LOCALAPPDATA\DialShift") i .r0'
        IntOp $1 $0 & 0x400
        ${If} $0 <> -1
        ${AndIf} $1 = 0x400
            RMDir "$LOCALAPPDATA\DialShift"
        ${Else}
            RMDir /r "$LOCALAPPDATA\DialShift"
        ${EndIf}
        ${If} ${FileExists} "$LOCALAPPDATA\DialShift\*.*"
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
