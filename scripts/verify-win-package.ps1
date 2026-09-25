<#
Verifies a DialShift win-x64 package folder against the Windows packaging rules
(brief 1 §8; decisions D2, D7; acceptance rows PK-02, PK-05, HS-15).

Usage: scripts/verify-win-package.ps1 -Path artifacts\DialShift-win-x64

scripts/build.ps1 runs it on the published folder before zipping. CI runs it again on the
folder extracted from the release zip, so the checks cover what a user downloads.
#>
param([Parameter(Mandatory = $true)][string]$Path)
$ErrorActionPreference = 'Stop'

$package = [IO.Path]::GetFullPath($Path)
if (-not (Test-Path -LiteralPath $package -PathType Container)) { throw "Package folder not found: $package" }

$required = @(
    'DialShift.exe',
    'DialShift.dll',
    'libvlc\win-x64\libvlc.dll',
    'libvlc\win-x64\libvlccore.dll',
    'libvlc\win-x64\plugins',
    'libSkiaSharp.dll',
    'libHarfBuzzSharp.dll',
    'av_libglesv2.dll',
    'Install.ps1',
    'README.md',
    'THIRD-PARTY-NOTICES.md',
    'licenses\LibVLC-LGPL-2.1.txt',
    'licenses\VLC-GPL-2.0.txt'
)
foreach ($item in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $package $item))) { throw "Missing from the Windows package: $item" }
}

# win-x64 only (D2): the VLC runtime for other architectures must not ship.
$otherVlc = @(Get-ChildItem -LiteralPath (Join-Path $package 'libvlc') -Directory | Where-Object { $_.Name -ne 'win-x64' })
if ($otherVlc.Count -gt 0) {
    throw "VLC runtimes for other architectures found in libvlc\: $(($otherVlc | ForEach-Object Name) -join ', '). Disable them in DialShift.App.csproj (VlcWindowsX86Enabled/VlcWindowsArm64Enabled=false)."
}

# DialShift.exe must be an x64 (0x8664) Windows GUI (subsystem 2) executable: no console window.
$exe = [IO.File]::ReadAllBytes((Join-Path $package 'DialShift.exe'))
$peOffset = [BitConverter]::ToInt32($exe, 0x3C)
if ([BitConverter]::ToUInt32($exe, $peOffset) -ne 0x00004550) { throw 'DialShift.exe is not a PE executable.' }
$machine = [BitConverter]::ToUInt16($exe, $peOffset + 4)
if ($machine -ne 0x8664) { throw ('DialShift.exe machine is 0x{0:X4}, expected 0x8664 (x64).' -f $machine) }
$subsystem = [BitConverter]::ToUInt16($exe, $peOffset + 24 + 68)
if ($subsystem -ne 2) { throw "DialShift.exe subsystem is $subsystem, expected 2 (Windows GUI)." }

Write-Host "verified: $package (x64 GUI exe, libvlc\win-x64 only, notices and licenses present)"
