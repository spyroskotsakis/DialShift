<#
Builds the Windows release of DialShift.App (win-x64, self-contained; decisions D13, D7).

  artifacts\DialShift-win-x64\       published app + Install.ps1, README, notices, licenses
  artifacts\DialShift-win-x64.zip    the distributable: a zip of that folder (MSIX/installer later)

-Version <semver>: SemVer without build metadata, for example 0.3.0-rc.1. Its MAJOR.MINOR.PATCH
must equal the csproj <Version>, the single source of the numeric version (D53): the override
only adds a pre-release suffix. It goes to dotnet publish as -p:Version, so DialShift.exe's
product version (the assembly InformationalVersion) carries it and its file version is
MAJOR.MINOR.PATCH.0. Default (omitted or empty): the csproj <Version>.

Development builds are unsigned (D7). Public releases need Authenticode signing, which needs
a code-signing certificate and is not performed here.
Uses a local SDK at %LOCALAPPDATA%\DialShift\sdk\dotnet.exe when present, else dotnet on PATH.
Also runs under PowerShell 7 on macOS, where scripts/release-local.sh uses it to cross-build the
Windows package (D58); the Windows-only native checks (smoke test, Install.ps1) still need Windows.
#>
param([switch]$SkipTests, [string]$Version = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$csproj = Join-Path $root 'DialShift.App\DialShift.App.csproj'
$csprojVersion = (Select-Xml -LiteralPath $csproj -XPath '//Version' | Select-Object -First 1).Node.InnerText
if ($csprojVersion -notmatch '^\d+\.\d+\.\d+$') { throw "Could not read a <Version>x.y.z</Version> from $csproj (got '$csprojVersion')." }
if (-not $Version) { $Version = $csprojVersion }
if ($Version -cnotmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$') {
    throw "-Version must be SemVer MAJOR.MINOR.PATCH[-prerelease] without build metadata (got '$Version')."
}
if ($Version.Split('-')[0] -ne $csprojVersion) {
    throw "-Version $Version does not match the csproj <Version>$csprojVersion</Version>; bump the csproj first (D53)."
}
$dotnet = if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'DialShift\sdk\dotnet.exe' } else { '' }
if (-not $dotnet -or -not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
if (-not $SkipTests) {
    & $dotnet run --project (Join-Path $root 'DialShift.Tests\DialShift.Tests.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Core checks failed.' }
}
$output = Join-Path $root 'artifacts\DialShift-win-x64'
$archive = Join-Path $root 'artifacts\DialShift-win-x64.zip'
# A fresh folder keeps files from an earlier build out of the package.
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
Write-Host "Publishing DialShift $Version (win-x64)"
& $dotnet publish $csproj -c Release -r win-x64 --self-contained true "-p:Version=$Version" -p:DebugType=None -p:DebugSymbols=false -o $output
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
# The SDK appends +<commit> to the product version when it knows the source revision.
# Windows reads DialShift.exe's version resource. Elsewhere .NET reads version data only from managed
# assemblies, so the check reads DialShift.dll, whose informational version the SDK copies into that resource.
$versionFile = if ($env:OS -eq 'Windows_NT') { 'DialShift.exe' } else { 'DialShift.dll' }
$productVersion = [string](Get-Item -LiteralPath (Join-Path $output $versionFile)).VersionInfo.ProductVersion
if ($productVersion -ne $Version -and -not $productVersion.StartsWith("$Version+")) {
    throw "$versionFile has product version '$productVersion', expected $Version."
}
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
