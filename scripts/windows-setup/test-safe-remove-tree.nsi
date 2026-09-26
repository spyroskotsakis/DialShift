; A function-level check of SafeRemoveTree.nsh, the only recursive delete of the setup and its uninstaller, including
; the uninstaller's "No, delete my settings" path, which a silent run cannot reach. build.yml builds it with the pinned
; makensis and runs it on windows-latest over a tree with a junction, a folder symbolic link and a file symbolic link
; whose targets lie outside the tree:
;   makensis -WX -V2 -DOUTPUT=<exe> scripts/windows-setup/test-safe-remove-tree.nsi
;   <exe> /S /D=<tree>
; Exit 0 when <tree> is gone, 1 when it is still there; the step then checks that every link target is intact.
Unicode true
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetDateSave off
Name "SafeRemoveTree check"
OutFile "${OUTPUT}"

!addincludedir "${__FILEDIR__}"
!include "LogicLib.nsh"
!include "SafeRemoveTree.nsh"
!insertmacro SAFE_REMOVE_TREE ""

Section
    ; Never inside the tree: it could not be removed.
    SetOutPath "$TEMP"
    Push "$INSTDIR"
    Call SafeRemoveTree
    ${If} ${FileExists} "$INSTDIR"
        SetErrorLevel 1
    ${Else}
        SetErrorLevel 0
    ${EndIf}
SectionEnd
