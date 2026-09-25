# Per-user install of the extracted DialShift win-x64 release; no administrator rights needed.
# Copies the app to %LOCALAPPDATA%\Programs\DialShift and adds a Start menu shortcut.
# Settings live separately in %LOCALAPPDATA%\DialShift and are never touched here.
# The new build is copied into a staging folder beside the install first and then swapped in by renames (SW-N4, D56),
# so a failed copy or a locked file leaves the previous install as it was.
param([switch]$NoLaunch)
$ErrorActionPreference = 'Stop'
$source = $PSScriptRoot
if (-not (Test-Path -LiteralPath (Join-Path $source 'DialShift.exe'))) { throw 'Run Install.ps1 from the extracted release folder.' }
$destination = Join-Path $env:LOCALAPPDATA 'Programs\DialShift'
$executable = Join-Path $destination 'DialShift.exe'
# Full paths with one trailing separator, compared case-insensitively, so C:\x\DialShift2 is not inside C:\x\DialShift.
$sourceKey = [IO.Path]::GetFullPath($source).TrimEnd('\') + '\'
$destinationKey = [IO.Path]::GetFullPath($destination).TrimEnd('\') + '\'
if ($sourceKey.StartsWith($destinationKey, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Install.ps1 is running from inside the install folder $destination. Extract the release zip to another folder, such as Downloads, and run Install.ps1 from there."
}
if ($destinationKey.StartsWith($sourceKey, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The install folder $destination is inside $source. Extract the release zip to a folder of its own and run Install.ps1 from there."
}
$running = Get-Process DialShift -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $executable }
if ($running) { throw 'Quit DialShift from its tray menu before installing an update.' }
# Stage beside the install, in the same folder, so the swap below is two renames on one volume.
$parent = Split-Path -Parent $destination
$suffix = [guid]::NewGuid().ToString('N').Substring(0, 8)
$staging = Join-Path $parent "DialShift.new-$suffix"
$previous = Join-Path $parent "DialShift.old-$suffix"
New-Item -ItemType Directory -Path $staging -Force | Out-Null
try {
    Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $staging -Recurse -Force
} catch {
    Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    throw "Could not copy the new build: $($_.Exception.Message) Nothing was changed."
}
# Swap: move the previous install aside and the new build in; on failure, put the previous install back.
$hadPrevious = Test-Path -LiteralPath $destination
try {
    if ($hadPrevious) { Rename-Item -LiteralPath $destination -NewName (Split-Path -Leaf $previous) }
    Rename-Item -LiteralPath $staging -NewName (Split-Path -Leaf $destination)
} catch {
    $reason = $_.Exception.Message
    Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    if ($hadPrevious -and -not (Test-Path -LiteralPath $destination)) {
        try { Rename-Item -LiteralPath $previous -NewName (Split-Path -Leaf $destination) }
        catch { throw "Could not replace $destination ($reason), and could not move the previous install back. Rename $previous to DialShift to restore it." }
    }
    throw "Could not move the new build into $destination ($reason). Any previous install is unchanged; quit programs using files in it and run Install.ps1 again."
}
# The new build is in place; an old copy that cannot be deleted (a file still locked) is only a warning.
if ($hadPrevious) {
    try { Remove-Item -LiteralPath $previous -Recurse -Force }
    catch { Write-Warning "DialShift is installed, but the previous copy could not be deleted ($($_.Exception.Message)). Delete $previous yourself." }
}
$shell = New-Object -ComObject WScript.Shell
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'DialShift.lnk'
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $executable
$shortcut.WorkingDirectory = $destination
$shortcut.IconLocation = "$executable,0"
$shortcut.Description = 'Your radio, on time.'
$shortcut.Save()
# Preserve the user's existing startup choice while relocating the executable.
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (Get-ItemProperty -Path $runKey -Name DialShift -ErrorAction SilentlyContinue) {
    Set-ItemProperty -Path $runKey -Name DialShift -Value "`"$executable`" --tray"
}
Write-Host "Installed DialShift to $destination. Find it in your Start menu."
if (-not $NoLaunch) { Start-Process -FilePath $executable -WindowStyle Hidden }
