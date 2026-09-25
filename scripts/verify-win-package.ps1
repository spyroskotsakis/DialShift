<#
Verifies a DialShift win-x64 package folder against the Windows packaging rules
(brief 1 §8; decisions D2 as amended by D13, D7, D60, D81; acceptance rows PK-02, PK-05, HS-15, CAT-03).

Usage: scripts/verify-win-package.ps1 -Path artifacts\DialShift-win-x64

scripts/build.ps1 runs it on the published folder before zipping. CI runs it again on the
folder extracted from the release zip, so the checks cover what a user downloads.
The station catalog JSON is parsed with System.Text.Json (JsonDocument, default options) under
PowerShell 7, as CI and scripts/release-local.sh run it; Windows PowerShell 5.1, which has no
System.Text.Json, falls back to DataContractJsonSerializer's JSON reader (see the catalog check).
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

# win-x64 only (D13): the VLC runtime for other architectures must not ship.
$otherVlc = @(Get-ChildItem -LiteralPath (Join-Path $package 'libvlc') -Directory | Where-Object { $_.Name -ne 'win-x64' })
if ($otherVlc.Count -gt 0) {
    throw "VLC runtimes for other architectures found in libvlc\: $(($otherVlc | ForEach-Object Name) -join ', '). Disable them in DialShift.App.csproj (VlcWindowsX86Enabled/VlcWindowsArm64Enabled=false)."
}

$symbols = @(Get-ChildItem -LiteralPath $package -Filter '*.pdb' -Recurse)
if ($symbols.Count -gt 0) { throw "Debug symbol files in the Windows package: $(($symbols | ForEach-Object Name) -join ', ')" }

# DialShift.exe must be an x64 (0x8664) Windows GUI (subsystem 2) executable: no console window.
$exe = [IO.File]::ReadAllBytes((Join-Path $package 'DialShift.exe'))
$peOffset = [BitConverter]::ToInt32($exe, 0x3C)
if ([BitConverter]::ToUInt32($exe, $peOffset) -ne 0x00004550) { throw 'DialShift.exe is not a PE executable.' }
$machine = [BitConverter]::ToUInt16($exe, $peOffset + 4)
if ($machine -ne 0x8664) { throw ('DialShift.exe machine is 0x{0:X4}, expected 0x8664 (x64).' -f $machine) }
$subsystem = [BitConverter]::ToUInt16($exe, $peOffset + 24 + 68)
if ($subsystem -ne 2) { throw "DialShift.exe subsystem is $subsystem, expected 2 (Windows GUI)." }

# Brief 3 (D59, D60, D81; CAT-03): the station catalog, a loose file next to DialShift.exe where AppContext.BaseDirectory
# finds it. It must parse strictly, as the app parses it: System.Text.Json's JsonDocument with default options (no
# comments, no trailing commas, no NaN, one value, depth 64), over the bytes decoded as strict UTF-8 after an optional
# byte order mark. ConvertFrom-Json is not used: it accepts comments, trailing commas and NaN. Windows PowerShell 5.1
# has no System.Text.Json; there the file is read with DataContractJsonSerializer's JSON reader, which rejects comments
# and malformed tokens, plus checks for what that reader lets through: a trailing comma, a second value, and a number
# that is not a plain JSON number (NaN, Infinity, 01, 0x1).
# schema_version must be the literal 1 (not "1", true, 1.0 or 1e0: the app reads it as an integer), and stations a
# non-empty array whose every element is an object.
$catalogFile = Join-Path $package 'app-catalog.json'
if (-not (Test-Path -LiteralPath $catalogFile -PathType Leaf)) { throw 'Missing from the Windows package: app-catalog.json (the station catalog next to DialShift.exe, D60).' }
$catalogBytes = [IO.File]::ReadAllBytes($catalogFile)
$bom = if ($catalogBytes.Length -ge 3 -and $catalogBytes[0] -eq 0xEF -and $catalogBytes[1] -eq 0xBB -and $catalogBytes[2] -eq 0xBF) { 3 } else { 0 }
if ('System.Text.Json.JsonDocument' -as [type]) {
    try {
        $text = [Text.UTF8Encoding]::new($false, $true).GetString($catalogBytes, $bom, $catalogBytes.Length - $bom)
        $json = [System.Text.Json.JsonDocument]::Parse($text, [System.Text.Json.JsonDocumentOptions]::new())
    }
    catch {
        $failure = if ($_.Exception.InnerException) { $_.Exception.InnerException } else { $_.Exception }
        throw "app-catalog.json does not parse as JSON: $($failure.Message)"
    }
    try {
        $root = $json.RootElement
        if ($root.ValueKind -ne 'Object') { throw "app-catalog.json holds a JSON $($root.ValueKind), not an object." }
        # A repeated key: the last one wins, as in the app's parse.
        $properties = @{}
        foreach ($property in $root.EnumerateObject()) { $properties[$property.Name] = $property.Value }
        $schemaVersion = if ($properties.ContainsKey('schema_version')) { $properties['schema_version'].GetRawText() } else { '<missing>' }
        if ($schemaVersion -cne '1') { throw "app-catalog.json schema_version is $schemaVersion, expected the integer 1." }
        $stations = $properties['stations']
        if ($null -eq $stations -or $stations.ValueKind -ne 'Array' -or $stations.GetArrayLength() -lt 1) {
            throw 'app-catalog.json has no stations array with at least one station.'
        }
        $stationCount = $stations.GetArrayLength()
        $notObjects = @($stations.EnumerateArray() | Where-Object { $_.ValueKind -ne 'Object' }).Count
    }
    finally { $json.Dispose() }
}
else {
    Add-Type -AssemblyName System.Runtime.Serialization
    try {
        $reader = [System.Runtime.Serialization.Json.JsonReaderWriterFactory]::CreateJsonReader($catalogBytes, $bom, $catalogBytes.Length - $bom, [Text.Encoding]::UTF8, [System.Xml.XmlDictionaryReaderQuotas]::Max, $null)
        $xml = New-Object System.Xml.XmlDocument
        try { $xml.Load($reader) } finally { $reader.Close() }
    }
    catch { throw "app-catalog.json does not parse as JSON: $($_.Exception.Message)" }
    # The reader accepts a trailing comma and stops after the first value. With every string blanked out, a comma before
    # a closing bracket is a trailing comma, and the text must be one balanced object from its first to its last brace.
    $blanked = [regex]::Replace([Text.Encoding]::UTF8.GetString($catalogBytes, $bom, $catalogBytes.Length - $bom), '"(?:[^"\\]|\\.)*"', '""')
    if ($blanked -match ',\s*[\]}]') { throw 'app-catalog.json does not parse as JSON: it has a trailing comma.' }
    if ($blanked -notmatch '^\s*\{(?>[^{}]+|\{(?<open>)|\}(?<-open>))*(?(open)(?!))\}\s*$') {
        throw 'app-catalog.json does not parse as JSON: it is not exactly one JSON object.'
    }
    $root = $xml.DocumentElement
    $badNumber = @($xml.SelectNodes('//*[@type="number"]') | Where-Object { $_.InnerText -cnotmatch '^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?$' }) | Select-Object -First 1
    if ($badNumber) { throw "app-catalog.json does not parse as JSON: '$($badNumber.InnerText)' is not a JSON number." }
    # A repeated key: the last one wins, as in the app's parse.
    $versionNode = $root.SelectNodes('schema_version') | Select-Object -Last 1
    if ($null -eq $versionNode -or $versionNode.GetAttribute('type') -ne 'number' -or $versionNode.InnerText -cne '1') {
        throw "app-catalog.json schema_version is '$(if ($versionNode) { $versionNode.InnerText } else { '<missing>' })', expected the integer 1."
    }
    $stations = $root.SelectNodes('stations') | Select-Object -Last 1
    if ($null -eq $stations -or $stations.GetAttribute('type') -ne 'array' -or $stations.ChildNodes.Count -lt 1) {
        throw 'app-catalog.json has no stations array with at least one station.'
    }
    $stationCount = $stations.ChildNodes.Count
    $notObjects = @($stations.ChildNodes | Where-Object { $_.GetAttribute('type') -ne 'object' }).Count
}
if ($notObjects -gt 0) { throw "app-catalog.json: $notObjects of the $stationCount stations are not JSON objects." }

Write-Host "verified: $package (x64 GUI exe, libvlc\win-x64 only, notices and licenses present, station catalog with $stationCount stations)"
