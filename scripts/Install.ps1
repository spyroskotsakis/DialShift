# Per-user install of the extracted DialShift win-x64 release; no administrator rights needed.
# Copies the app to %LOCALAPPDATA%\Programs\DialShift and adds a Start menu shortcut.
# Settings live separately in %LOCALAPPDATA%\DialShift and are never touched here.
param([switch]$NoLaunch)
$ErrorActionPreference = 'Stop'
$source = $PSScriptRoot
if (-not (Test-Path -LiteralPath (Join-Path $source 'DialShift.exe'))) { throw 'Run Install.ps1 from the extracted release folder.' }
$destination = Join-Path $env:LOCALAPPDATA 'Programs\DialShift'
$executable = Join-Path $destination 'DialShift.exe'
$running = Get-Process DialShift -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $executable }
if ($running) { throw 'Quit DialShift from its tray menu before installing an update.' }
if ([IO.Path]::GetFullPath($source) -ne [IO.Path]::GetFullPath($destination)) {
    # Replace the previous install completely so no files from an older build linger.
    if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $destination -Recurse -Force
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
