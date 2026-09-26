; SafeRemoveTree: deletes a file or a folder tree without following links (brief 4 section 11, "settings are sacred").
; NSIS's own RMDir /r recurses into junctions and symbolic links to folders and deletes the files of their targets, so
; the setup and its uninstaller never use it. This walks the tree itself: an entry that is a reparse point (a junction,
; a symbolic link, a mount point) is removed as a link only (RMDir for a folder link, Delete for a file link) and never
; entered; a real folder is emptied, then removed; a file is deleted. What cannot be deleted stays; the caller checks.
;
; Usage:  !insertmacro SAFE_REMOVE_TREE ""   (or "un." for the uninstaller's copy), then
;         Push <path>
;         Call SafeRemoveTree                (Call un.SafeRemoveTree)
; Needs LogicLib.nsh. Recursive: it keeps its registers on the stack, so it changes no register of its caller.
; scripts/windows-setup/test-safe-remove-tree.nsi runs it on real links on windows-latest (build.yml).

!macro SAFE_REMOVE_TREE UN
Function ${UN}SafeRemoveTree
    Exch $0     ; the path
    Push $1     ; find handle
    Push $2     ; entry name
    Push $3     ; attributes
    Push $4
    System::Call 'kernel32::GetFileAttributesW(w r0) i .r3'
    ${If} $3 <> -1
        IntOp $4 $3 & 0x400             ; FILE_ATTRIBUTE_REPARSE_POINT
        ${If} $4 <> 0
            IntOp $4 $3 & 0x10          ; FILE_ATTRIBUTE_DIRECTORY
            ${If} $4 <> 0
                RMDir "$0"
            ${Else}
                Delete "$0"
            ${EndIf}
        ${Else}
            IntOp $4 $3 & 0x10
            ${If} $4 = 0
                Delete "$0"
            ${Else}
                FindFirst $1 $2 "$0\*.*"
                ${DoWhile} $2 != ""
                    ${If} $2 != "."
                    ${AndIf} $2 != ".."
                        Push "$0\$2"
                        Call ${UN}SafeRemoveTree
                    ${EndIf}
                    FindNext $1 $2
                ${Loop}
                FindClose $1
                RMDir "$0"
            ${EndIf}
        ${EndIf}
    ${EndIf}
    Pop $4
    Pop $3
    Pop $2
    Pop $1
    Pop $0
FunctionEnd
!macroend
