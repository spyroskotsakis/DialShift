<#
Builds the Windows release of DialShift.App (win-x64, self-contained; decisions D2, D7).

  artifacts\DialShift-win-x64\       published app + Install.ps1, README, notices, licenses
  artifacts\DialShift-win-x64.zip    the distributable: a zip of that folder (MSIX/installer later)

Development builds are unsigned (D7). Public releases need Authenticode signing, which needs
a code-signing certificate and is not performed here.
Uses a local SDK at %LOCALAPPDATA%\DialShift\sdk\dotnet.exe when present, else dotnet on PATH.
#>
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $env:LOCALAPPDATA 'DialShift\sdk\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
if (-not $SkipTests) {
    & $dotnet run --project (Join-Path $root 'DialShift.Tests\DialShift.Tests.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Core checks failed.' }
}
$output = Join-Path $root 'artifacts\DialShift-win-x64'
$archive = Join-Path $root 'artifacts\DialShift-win-x64.zip'
# A fresh folder keeps files from an earlier build out of the package.
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
& $dotnet publish (Join-Path $root 'DialShift.App\DialShift.App.csproj') -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o $output
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
# DebugType=None covers our assemblies only; native NuGet assets still bring symbol files
# (libSkiaSharp.pdb + libHarfBuzzSharp.pdb, about 105 MB) that users do not need.
Get-ChildItem -LiteralPath $output -Filter '*.pdb' -Recurse | Remove-Item -Force
Copy-Item -LiteralPath (Join-Path $root 'README.md'),(Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $output
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install.ps1') -Destination $output
# Only the texts that apply to the Windows package (keep in sync with the "Windows package"
# section of THIRD-PARTY-NOTICES.md).
$licenses = @(
    'LibVLC-LGPL-2.1.txt', 'VLC-GPL-2.0.txt',
    'Avalonia-LICENSE.txt', 'ANGLE-LICENSE.txt',
    'SkiaSharp-LICENSE.txt', 'SkiaSharp-THIRD-PARTY-NOTICES.txt',
    'HarfBuzzSharp-LICENSE.txt', 'HarfBuzzSharp-THIRD-PARTY-NOTICES.txt',
    'MicroCom-LICENSE.txt', 'Tmds.DBus-LICENSE.txt',
    'NET-LICENSE.txt', 'NET-THIRD-PARTY-NOTICES.txt'
)
$licenseFolder = New-Item -ItemType Directory -Path (Join-Path $output 'licenses') -Force
foreach ($license in $licenses) {
    Copy-Item -LiteralPath (Join-Path $root "licenses\$license") -Destination $licenseFolder.FullName
}
& (Join-Path $PSScriptRoot 'verify-win-package.ps1') -Path $output
if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
Compress-Archive -Path (Join-Path $output '*') -DestinationPath $archive
Write-Host "Built: $output\DialShift.exe"
Write-Host "Archive: $archive"
