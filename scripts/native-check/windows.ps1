<#
.SYNOPSIS
DialShift native-check kit for Windows. It guides you through the native checks in docs/acceptance-matrix.md
section 9 that need a real Windows PC and a person, and collects one evidence bundle to hand back
(docs/open-items.md).

.DESCRIPTION
Covers NC-01, NC-02, NC-03, NC-04, NC-05 (a; b when the build is signed), NC-06 and NC-15. For each check it prints the
procedure and pass criteria from matrix section 9, automates what a script can do, asks you to do the physical steps
and give a PASS/FAIL/SKIP verdict with a note, and records everything in
    Desktop\native-evidence-<host>-<yyyymmdd>\   summary.md, summary.json, machine.txt, one folder per check
which it zips when you finish. It is safe to re-run: checks already recorded are skipped, and NC-04 resumes after each
sign-out and sign-in. It never touches your real DialShift data or launch-at-sign-in entry without asking first, and it
moves or saves the originals so they can be restored.

Runs in Windows PowerShell 5.1 and PowerShell 7. See scripts\native-check\README.md.
(This file is ASCII only: Windows PowerShell 5.1 reads a script without a byte-order mark in the ANSI code page.)

.PARAMETER AppFolder
The extracted DialShift-win-x64 folder (or its DialShift.exe) to test.

.PARAMETER Zip
The CI zip DialShift-win-x64.zip. The kit asks you to extract it with Explorer (which keeps the Mark of the Web) or
extracts it itself.

.PARAMETER Evidence
The evidence folder to create or resume (default: the last one, or a new one on the Desktop).

.EXAMPLE
powershell -ExecutionPolicy Bypass -File .\scripts\native-check\windows.ps1 -Zip "$env:USERPROFILE\Downloads\DialShift-win-x64.zip"
#>
[CmdletBinding()]
param([string]$AppFolder, [string]$Zip, [string]$Evidence)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'This kit runs on Windows only (use macos.sh on a Mac).' }

# ---------------------------------------------------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------------------------------------------------
$KitVersion = 1
$WorkRoot = Join-Path $env:USERPROFILE 'DialShift-native-check'   # extracted apps, isolated data, backups; never zipped
$ManifestPath = Join-Path $WorkRoot 'backup\manifest.json'        # exists while real DialShift files are moved aside
$RealData = Join-Path $env:LOCALAPPDATA 'DialShift'
$RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$ApprovedKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
$EntryName = 'DialShift'
$InstalledDir = Join-Path $env:LOCALAPPDATA 'Programs\DialShift'                      # where Install.ps1 installs
$StartMenuShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'DialShift.lnk'   # the shortcut it creates
$LegacyMutexName = 'Local\DialShift.App'   # held by the older WPF DialShift while it runs (sweep S2)
$WakeTaskName = 'DialShift native-check wake'
$CheckOrder = @('NC-01', 'NC-06', 'NC-04', 'NC-02', 'NC-03', 'NC-15', 'NC-05')   # docs/open-items.md section 3
$Titles = @{
    'NC-01' = 'Tray, window, audio and dialogs'
    'NC-02' = 'SystemEvents and a real sleep (TZ-12, SR-02, HZ-04)'
    'NC-03' = 'LibVLC real playback and corpus'
    'NC-04' = 'Launch at sign-in and the Task Manager switch'
    'NC-05' = 'SmartScreen (a) and Authenticode (b)'
    'NC-06' = 'Second-launch foreground'
    'NC-15' = 'LibVLC fast-switch stress'
}
$Utf8NoBom = New-Object System.Text.UTF8Encoding $false
$Invariant = [Globalization.CultureInfo]::InvariantCulture
$Dot = [char]0x00B7   # the middle dot in DialShift's window, dialog and tooltip texts
$LogPattern = [regex]'^\{"ts":"(?<ts>[^"]+)","level":"(?<level>[^"]+)","event":"(?<event>[^"]+)","msg":"(?<msg>(?:[^"\\]|\\.)*)"'
$TimelineEvents = '^(app\.(start|exit|startup_failed|legacy_instance_running)|playback\.(state|failed|fallback|engine_error)|wake\.|power_events\.|schedule\.fired|single_instance\.(activated|socket)|settings\.recovered|startup_registration\.)'

# The corpus (docs/spikes.md) with the LibVLC outcome each case is expected to give.
function New-CorpusEntry([string]$Id, [string]$Url, [string]$What, [string]$Expect) {
    [pscustomobject]@{ Id = $Id; Url = $Url; What = $What; Expect = $Expect }
}
$Corpus = @(
    New-CorpusEntry 'C1' 'https://ice1.somafm.com/groovesalad-128-mp3' 'MP3, HTTPS Icecast' 'Playing'
    New-CorpusEntry 'C2' 'https://ice5.somafm.com/groovesalad-128-aac' 'AAC (ADTS), HTTPS Icecast' 'Playing'
    New-CorpusEntry 'C3' 'https://a.files.bbci.co.uk/ms6/live/3441A116-B12E-4D2F-ACA8-C1984642FA4B/audio/simulcast/hls/nonuk/pc_hd_abr_v2/ak/bbc_world_service.m3u8' 'HLS AAC, HTTPS' 'Playing'
    New-CorpusEntry 'C4' 'https://stream.radiofrance.fr/franceinterlamusiqueinter/franceinterlamusiqueinter_hifi.m3u8?id=radiofrance' 'HLS AAC, HTTPS with a query' 'Playing'
    New-CorpusEntry 'C5' 'https://stream.radios.bzh/hls/boa/aac_hifi.m3u8' 'HLS AAC, HTTPS' 'Playing'
    New-CorpusEntry 'C6' 'http://radiorecord.hostingradio.ru/deep96.aacp' 'HE-AAC (.aacp), cleartext HTTP' 'Playing'
    New-CorpusEntry 'C7' 'https://icecast.radiofrance.fr/fip-hifi.aac' 'AAC, HTTPS Icecast' 'Playing'
    New-CorpusEntry 'C8' 'http://stream.power-radio.de:8020/listen.pls' 'MP3 served at a .pls path (watchdog)' 'Stalled'
    New-CorpusEntry 'C9' 'http://france16.coollabel-productions.com:8276/;' 'MP3, Shoutcast v2, cleartext HTTP' 'Playing'
    New-CorpusEntry 'C10' 'https://radio.ekodesgarrigues.com/eko-des-garrigues-256k.ogg' 'Ogg Vorbis' 'Playing'
    New-CorpusEntry 'C11' 'https://st02.sslstream.dlf.de/dlf/02/low/opus/stream.opus?aggregator=web' 'Opus, HTTPS 302' 'Playing'
    New-CorpusEntry 'C12' 'https://onair.net-radio.fr/frequence3dance.flac' 'FLAC in Ogg' 'Playing'
    New-CorpusEntry 'C13' 'https://streams.br.de/br-klassik_3.m3u' 'M3U playlist file' 'Playing'
    New-CorpusEntry 'C14' 'https://somafm.com/groovesalad.pls' 'PLS playlist file' 'Playing'
    New-CorpusEntry 'C15' 'https://st01.sslstream.dlf.de/dlf/01/128/mp3/stream.mp3?aggregator=web' 'MP3, HTTPS 302 with a token' 'Playing'
    New-CorpusEntry 'T1' 'http://127.0.0.1:1/unavailable' 'connection refused' 'NetworkUnavailable'
    New-CorpusEntry 'T2' 'https://stream.nonexistent.invalid/radio.mp3' 'DNS failure' 'NetworkUnavailable'
    New-CorpusEntry 'T3' 'https://ice5.somafm.com/does-not-exist-xyz' 'HTTP 404' 'HttpError'
    New-CorpusEntry 'T5a' 'https://expired.badssl.com/' 'expired certificate' 'TlsFailure'
    New-CorpusEntry 'T5b' 'https://self-signed.badssl.com/' 'self-signed certificate' 'TlsFailure'
    New-CorpusEntry 'T5c' 'https://wrong.host.badssl.com/' 'wrong host name' 'TlsFailure'
    New-CorpusEntry 'T5d' 'https://untrusted-root.badssl.com/' 'untrusted root' 'TlsFailure'
    New-CorpusEntry 'T6' 'http://st01.dlf.de/dlf/01/128/mp3/stream.mp3' 'redirect, http to http' 'Playing'
    New-CorpusEntry 'T9' 'https://example.com/' 'HTTPS HTML page (captive-portal style)' 'UnsupportedFormat'
)
# NC-15: at least 10 public stations that play.
$StressStations = @($Corpus | Where-Object { @('C1', 'C2', 'C3', 'C4', 'C5', 'C7', 'C9', 'C13', 'C14', 'C15') -contains $_.Id }) + @(
    New-CorpusEntry 'Drone Zone' 'https://ice5.somafm.com/dronezone-128-aac' 'SomaFM' 'Playing'
    New-CorpusEntry 'Secret Agent' 'https://ice5.somafm.com/secretagent-128-aac' 'SomaFM' 'Playing'
    New-CorpusEntry 'Groove Salad AAC' 'https://ice5.somafm.com/groovesalad-128-aac' 'SomaFM' 'Playing'
)

# Mutable state of this run.
$script:Evid = $null           # evidence bundle folder
$script:Exe = $null            # DialShift.exe under test
$script:CiRun = ''             # CI run id of the build under test, if known
$script:CurId = $null          # the check being recorded
$script:CurDir = $null         # its evidence folder
$script:Precondition = ''      # set when you chose to continue although a precondition was not met
$script:MachineJson = $null    # cached machine description
$script:ShotsOk = $null        # $true/$false once you have answered the screenshot question

# ---------------------------------------------------------------------------------------------------------------------
# Output and prompts
# ---------------------------------------------------------------------------------------------------------------------
function Write-Title([string]$Text) { Write-Host ''; Write-Host "== $Text ==" -ForegroundColor Cyan }
function Write-Warn([string]$Text) { Write-Host "WARNING: $Text" -ForegroundColor Yellow }
function Read-Line([string]$Prompt) { $answer = Read-Host $Prompt; if ($null -eq $answer) { return '' }; return $answer.Trim() }
function Confirm-Choice([string]$Prompt, [bool]$DefaultYes) {
    $hint = 'y/N'
    if ($DefaultYes) { $hint = 'Y/n' }
    while ($true) {
        $answer = Read-Line "$Prompt [$hint]"
        if ($answer -eq '') { return $DefaultYes }
        if ($answer -match '^[Yy]') { return $true }
        if ($answer -match '^[Nn]') { return $false }
    }
}
function Wait-Enter([string]$Prompt = 'Press Enter to continue') { [void](Read-Host $Prompt) }
# Prints an instruction for a physical step, then waits for Enter.
function Invoke-Step([string]$Instruction, [string]$Prompt = 'Press Enter when done') { Write-Host ''; Write-Host $Instruction; Wait-Enter $Prompt }
function Start-Countdown([int]$Seconds, [string]$Label) {
    for ($n = $Seconds; $n -gt 0; $n--) { Write-Host -NoNewline ("`r{0} {1,4}s " -f $Label, $n); Start-Sleep -Seconds 1 }
    Write-Host ("`r{0} done.       " -f $Label)
}
function Hide-Home([string]$Text) { if ($null -eq $Text) { return '' }; return $Text.Replace($env:USERPROFILE, '~') }
function Get-UtcStamp { [DateTime]::UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", $Invariant) }

# ---------------------------------------------------------------------------------------------------------------------
# JSON and Markdown text
# ---------------------------------------------------------------------------------------------------------------------
function ConvertTo-OneLine([string]$Text) { if ($null -eq $Text) { return '' }; return (($Text -replace '[\t\r\n]', ' ') -replace '[\x00-\x1F]', '') }
function ConvertTo-JsonString([string]$Text) { ConvertTo-Json -InputObject (ConvertTo-OneLine $Text) -Compress }
function ConvertTo-MdCell([string]$Text) { (ConvertTo-OneLine $Text) -replace '\|', '\|' }
function Write-Utf8([string]$Path, [string]$Text) { [IO.File]::WriteAllText($Path, $Text, $Utf8NoBom) }
function Add-Utf8Line([string]$Path, [string]$Line) { [IO.File]::AppendAllText($Path, $Line + "`n", $Utf8NoBom) }
function Read-Utf8Lines([string]$Path) { if (-not (Test-Path -LiteralPath $Path)) { return @() }; return @([IO.File]::ReadAllLines($Path) | Where-Object { $_ }) }

# ---------------------------------------------------------------------------------------------------------------------
# Evidence bundle: folder, state, per-check steps and records, summary.md / summary.json, zip
# ---------------------------------------------------------------------------------------------------------------------
function Get-HostName { $env:COMPUTERNAME -replace '[^A-Za-z0-9-]', '-' }

function Initialize-Evidence {
    New-Item -ItemType Directory -Force -Path $WorkRoot | Out-Null
    $pointer = Join-Path $WorkRoot 'current-evidence.txt'
    if ($Evidence) {
        $script:Evid = $Evidence
    } elseif (Test-Path -LiteralPath $pointer) {
        $previous = ([IO.File]::ReadAllText($pointer)).Trim()
        if ($previous -and (Test-Path -LiteralPath $previous) -and (Confirm-Choice "Resume the evidence bundle $(Hide-Home $previous)?" $true)) { $script:Evid = $previous }
    }
    if (-not $script:Evid) {
        $script:Evid = Join-Path ([Environment]::GetFolderPath('Desktop')) ('native-evidence-{0}-{1}' -f (Get-HostName), (Get-Date).ToString('yyyyMMdd', $Invariant))
    }
    New-Item -ItemType Directory -Force -Path (Join-Path $script:Evid 'records'), (Join-Path $script:Evid 'state') | Out-Null
    $script:Evid = (Resolve-Path -LiteralPath $script:Evid).ProviderPath
    Write-Utf8 $pointer $script:Evid
    Write-Host "Evidence bundle: $(Hide-Home $script:Evid)"
}

function Get-State([string]$Name) {
    $path = Join-Path $script:Evid "state\$Name"
    if (Test-Path -LiteralPath $path) { return ([IO.File]::ReadAllText($path)).Trim() }
    return ''
}
function Set-State([string]$Name, [string]$Value) { Write-Utf8 (Join-Path $script:Evid "state\$Name") $Value }

function Get-RecordPath([string]$Id) { Join-Path $script:Evid "records\$Id.json" }
function Get-RecordField([string]$Id, [string]$Field) {
    $path = Get-RecordPath $Id
    if (-not (Test-Path -LiteralPath $path)) { return '' }
    return [regex]::Match([IO.File]::ReadAllText($path), ('"{0}":"([^"]*)"' -f $Field)).Groups[1].Value
}
function Test-InProgress([string]$Id) { Test-Path -LiteralPath (Join-Path $script:Evid "$Id\phase") }
function Get-CheckStatus([string]$Id) {
    if (Test-InProgress $Id) { return 'in progress (after sign-in {0})' -f ([IO.File]::ReadAllText((Join-Path $script:Evid "$Id\phase"))).Trim() }
    if (Test-Path -LiteralPath (Get-RecordPath $Id)) { return '{0}  {1}' -f (Get-RecordField $Id 'result'), (Get-RecordField $Id 'timestampUtc') }
    return 'pending'
}

function Start-Check([string]$Id, [switch]$Resume) {
    $script:CurId = $Id
    $script:CurDir = Join-Path $script:Evid $Id
    $script:Precondition = ''
    New-Item -ItemType Directory -Force -Path $script:CurDir | Out-Null
    if (-not $Resume) {
        Write-Utf8 (Join-Path $script:CurDir 'steps.tsv') ''
        Write-Utf8 (Join-Path $script:CurDir 'artifacts.txt') ''
    }
    Write-Title "$($Id): $($Titles[$Id])"
}

function Add-Artifact([string]$RelativePath) { Add-Utf8Line (Join-Path $script:CurDir 'artifacts.txt') $RelativePath }
function Add-Step([string]$Kind, [string]$Result, [string]$Label, [string]$Note = '') {
    Add-Utf8Line (Join-Path $script:CurDir 'steps.tsv') ((@($Kind, $Result, (ConvertTo-OneLine $Label), (ConvertTo-OneLine $Note))) -join "`t")
    $suffix = ''
    if ($Note) { $suffix = ": $Note" }
    Write-Host "   -> $Result ($Kind) $Label$suffix"
}
function Add-AutoStep([string]$Label, [bool]$Ok, [string]$Detail = '') {
    $result = 'FAIL'
    if ($Ok) { $result = 'PASS' }
    Add-Step 'auto' $result $Label $Detail
}
function Read-Step([string]$Label) {
    while ($true) {
        $answer = Read-Line "   Result for `"$Label`": [p]ass, [f]ail or [s]kip?"
        if ($answer -match '^[Pp]') { $result = 'PASS'; break }
        if ($answer -match '^[Ff]') { $result = 'FAIL'; break }
        if ($answer -match '^[Ss]') { $result = 'SKIP'; break }
    }
    $note = Read-Line '   Note (optional, Enter for none)'
    Add-Step 'manual' $result $Label $note
}
# Records a precondition. Returns $false when it isn't met and you choose not to continue.
function Test-Precondition([string]$What, [bool]$Met) {
    if ($Met) { Add-Step 'auto' 'PASS' "Precondition: $What"; return $true }
    Add-Step 'auto' 'INFO' "Precondition not met: $What"
    Write-Warn "precondition not met: $What."
    if (Confirm-Choice 'Continue anyway? The check is then recorded as SKIP, with your observations.' $false) {
        if ($script:Precondition) { $script:Precondition += '; ' }
        $script:Precondition += $What
        return $true
    }
    return $false
}

function Get-Steps([string]$Dir = $script:CurDir) {
    foreach ($line in (Read-Utf8Lines (Join-Path $Dir 'steps.tsv'))) {
        $fields = $line -split "`t", 4
        $note = ''
        if ($fields.Count -gt 3) { $note = $fields[3] }
        [pscustomobject]@{ Kind = $fields[0]; Result = $fields[1]; Label = $fields[2]; Note = $note }
    }
}

# Saves a command's output (home folder hidden) as an artifact of the current check. Never throws.
function Save-Command([string]$File, [scriptblock]$Command) {
    $ErrorActionPreference = 'Continue'
    try { $text = (& $Command 2>&1 | Out-String) } catch { $text = "error: $($_.Exception.Message)" }
    Write-Utf8 (Join-Path $script:CurDir $File) ('PS> ' + $Command.ToString().Trim() + "`n" + (Hide-Home $text))
    Add-Artifact "$($script:CurId)/$File"
}

function Complete-Check {
    $steps = @(Get-Steps)
    $fails = @($steps | Where-Object { $_.Result -eq 'FAIL' }).Count
    $passes = @($steps | Where-Object { $_.Result -eq 'PASS' }).Count
    $skips = @($steps | Where-Object { $_.Result -eq 'SKIP' }).Count
    if ($fails -gt 0) { $computed = 'FAIL' } elseif ($passes -eq 0) { $computed = 'SKIP' } elseif ($skips -gt 0) { $computed = 'PARTIAL' } else { $computed = 'PASS' }
    if ($script:Precondition) { $computed = 'SKIP' }
    Write-Title "$($script:CurId): steps recorded"
    foreach ($step in $steps) {
        $suffix = ''
        if ($step.Note) { $suffix = ": $($step.Note)" }
        Write-Host ('  {0,-7} {1,-6} {2}{3}' -f $step.Result, $step.Kind, $step.Label, $suffix)
    }
    while ($true) {
        $answer = Read-Line "Overall result for $($script:CurId) [Enter = $computed, or pass/fail/partial/skip]"
        if ($answer -eq '') { $result = $computed; break }
        if ($answer -match '^(?i)par') { $result = 'PARTIAL'; break }
        if ($answer -match '^(?i)p') { $result = 'PASS'; break }
        if ($answer -match '^(?i)f') { $result = 'FAIL'; break }
        if ($answer -match '^(?i)s') { $result = 'SKIP'; break }
    }
    $note = Read-Line 'Overall note (what failed, why skipped, anything unusual; Enter for none)'
    if ($script:Precondition) { $note = "Precondition not met: $($script:Precondition). $note" }
    Write-Record $script:CurId $result $note
    Write-Summary
    Write-Host "Recorded $($script:CurId) as $result."
}

function Write-Record([string]$Id, [string]$Result, [string]$Note) {
    $stamp = Get-UtcStamp
    $dir = Join-Path $script:Evid $Id
    $steps = @(Get-Steps $dir)
    $artifacts = @(Read-Utf8Lines (Join-Path $dir 'artifacts.txt') | Select-Object -Unique)
    $stepsJson = @($steps | ForEach-Object {
            '{"step":' + (ConvertTo-JsonString $_.Label) + ',"kind":' + (ConvertTo-JsonString $_.Kind) +
            ',"result":' + (ConvertTo-JsonString $_.Result) + ',"note":' + (ConvertTo-JsonString $_.Note) + '}'
        }) -join ','
    $json = '{"checkId":' + (ConvertTo-JsonString $Id) + ',"title":' + (ConvertTo-JsonString $Titles[$Id]) +
        ',"result":' + (ConvertTo-JsonString $Result) + ',"timestampUtc":' + (ConvertTo-JsonString $stamp) +
        ',"machine":' + (Get-MachineJson) + ',"build":' + (Get-BuildJson) + ',"notes":' + (ConvertTo-JsonString $Note) +
        ',"artifacts":[' + (@($artifacts | ForEach-Object { ConvertTo-JsonString $_ }) -join ',') + '],"steps":[' + $stepsJson + ']}'
    Write-Utf8 (Get-RecordPath $Id) ($json + "`n")
    Write-Utf8 (Join-Path $script:Evid "records\$Id.md-row") ('| {0} | {1} | {2} | {3} | {4} | {5} |' -f $Id, $Result, $stamp,
        (ConvertTo-MdCell (Get-MachineShort)), (ConvertTo-MdCell $Note), (ConvertTo-MdCell ($artifacts -join ' ')))
    $details = @("### $($Id): $($Titles[$Id])", '', "Result **$Result** at $stamp. Build: $(ConvertTo-MdCell (Get-BuildShort)).", '',
        '| Step | Kind | Result | Note |', '|---|---|---|---|')
    $details += @($steps | ForEach-Object { '| {0} | {1} | {2} | {3} |' -f (ConvertTo-MdCell $_.Label), $_.Kind, $_.Result, (ConvertTo-MdCell $_.Note) })
    Write-Utf8 (Join-Path $script:Evid "records\$Id.md-steps") (($details -join "`n") + "`n`n")
}

function Write-Summary {
    $records = @()
    $rows = @()
    $details = @()
    $pending = @()
    foreach ($id in $CheckOrder) {
        $path = Get-RecordPath $id
        if (-not (Test-Path -LiteralPath $path)) { $pending += $id; continue }
        $records += ([IO.File]::ReadAllText($path)).Trim()
        $rows += ([IO.File]::ReadAllText((Join-Path $script:Evid "records\$id.md-row"))).Trim()
        $details += [IO.File]::ReadAllText((Join-Path $script:Evid "records\$id.md-steps"))
    }
    Write-Utf8 (Join-Path $script:Evid 'summary.json') ('{"schemaVersion":1,"kit":"windows.ps1","kitVersion":' + $KitVersion +
        ',"platform":"Windows","host":' + (ConvertTo-JsonString (Get-HostName)) + ',"generatedUtc":' + (ConvertTo-JsonString (Get-UtcStamp)) +
        ',"checks":[' + ($records -join ',') + ']}' + "`n")
    $pendingText = ' none'
    if ($pending.Count) { $pendingText = ' ' + ($pending -join ' ') }
    $md = @("# Native-check evidence: $(Get-HostName)", '',
        "Written $(Get-UtcStamp) by ``scripts/native-check/windows.ps1`` (kit v$KitVersion). Machine-readable copy: ``summary.json``. Machine details: ``machine.txt``.", '',
        '| Check | Result | Timestamp (UTC) | Machine | Notes | Artifacts |', '|---|---|---|---|---|---|') + $rows +
        @('', "Not recorded:$pendingText", '', '## Steps', '')
    Write-Utf8 (Join-Path $script:Evid 'summary.md') (($md -join "`n") + "`n" + ($details -join ''))
}

function Complete-Bundle {
    Write-Summary
    Write-MachineTxt
    $zipPath = "$($script:Evid).zip"
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Compress-Archive -LiteralPath $script:Evid -DestinationPath $zipPath
    Write-Title 'Done'
    Write-Host "Send this file back: $(Hide-Home $zipPath)"
    Write-Host 'You can run the kit again later; it adds to the same bundle and you zip it again.'
}

# ---------------------------------------------------------------------------------------------------------------------
# Machine and build description
# ---------------------------------------------------------------------------------------------------------------------
function Get-MachineInfo {
    $os = Get-CimInstance Win32_OperatingSystem
    $computer = Get-CimInstance Win32_ComputerSystem
    $current = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    [pscustomobject]@{
        Os = $os.Caption
        Build = '{0}.{1}' -f $os.BuildNumber, $current.UBR
        DisplayVersion = [string]$current.DisplayVersion
        Arch = $env:PROCESSOR_ARCHITECTURE
        Model = '{0} {1}' -f $computer.Manufacturer, $computer.Model
        Audio = @(Get-CimInstance Win32_SoundDevice -ErrorAction SilentlyContinue | ForEach-Object { $_.Name }) -join '; '
        PowerShell = $PSVersionTable.PSVersion.ToString()
    }
}
function Get-MachineJson {
    if (-not $script:MachineJson) {
        $m = Get-MachineInfo
        $script:MachineJson = '{"os":"Windows","osVersion":' + (ConvertTo-JsonString $m.Os) + ',"osBuild":' + (ConvertTo-JsonString $m.Build) +
            ',"displayVersion":' + (ConvertTo-JsonString $m.DisplayVersion) + ',"arch":' + (ConvertTo-JsonString $m.Arch) +
            ',"model":' + (ConvertTo-JsonString $m.Model) + ',"audioDevices":' + (ConvertTo-JsonString $m.Audio) +
            ',"powershell":' + (ConvertTo-JsonString $m.PowerShell) + ',"host":' + (ConvertTo-JsonString (Get-HostName)) + '}'
    }
    return $script:MachineJson
}
function Get-MachineShort { $m = Get-MachineInfo; '{0} {1} (build {2}), {3}, {4}' -f $m.Os, $m.DisplayVersion, $m.Build, $m.Arch, $m.Model }
function Write-MachineTxt {
    $ErrorActionPreference = 'Continue'   # a native tool's stderr must not stop the kit
    $m = Get-MachineInfo
    $lines = @("Kit: windows.ps1 v$KitVersion", "Written: $(Get-UtcStamp)", '',
        "OS: $($m.Os) $($m.DisplayVersion) (build $($m.Build))", "Architecture: $($m.Arch)", "Model: $($m.Model)",
        "Audio devices: $($m.Audio)", "PowerShell: $($m.PowerShell)", '', 'powercfg /a (sleep states):')
    $lines += @(powercfg /a 2>&1 | ForEach-Object { [string]$_ })
    Write-Utf8 (Join-Path $script:Evid 'machine.txt') (($lines -join "`n") + "`n")
}

function Get-AppVersion { if (-not $script:Exe) { return 'none' }; return (Get-Item -LiteralPath $script:Exe).VersionInfo.ProductVersion }
function Get-BuildJson {
    if (-not $script:Exe) { return 'null' }
    '{"app":' + (ConvertTo-JsonString (Hide-Home $script:Exe)) + ',"version":' + (ConvertTo-JsonString (Get-AppVersion)) +
    ',"source":' + (ConvertTo-JsonString (Get-State 'source')) + ',"ciRunId":' + (ConvertTo-JsonString $script:CiRun) + '}'
}
function Get-BuildShort {
    if (-not $script:Exe) { return 'none' }
    $run = ''
    if ($script:CiRun) { $run = " (CI run $($script:CiRun))" }
    return "$(Get-AppVersion) from $(Get-State 'source')$run"
}

# ---------------------------------------------------------------------------------------------------------------------
# The build under test: an extracted folder, or the CI zip (extracted with Explorer or by the kit)
# ---------------------------------------------------------------------------------------------------------------------
function Set-App([string]$Path, [string]$Source) {
    $exe = $Path
    if (Test-Path -LiteralPath $Path -PathType Container) {
        $found = Get-ChildItem -LiteralPath $Path -Filter 'DialShift.exe' -Recurse -Depth 2 -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $found) { throw "No DialShift.exe in $Path" }
        $exe = $found.FullName
    }
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Not found: $exe" }
    $script:Exe = (Resolve-Path -LiteralPath $exe).ProviderPath
    Set-State 'app' $script:Exe
    Set-State 'source' $Source
    $script:CiRun = Get-State 'ci_run'
    if (-not (Test-Path -LiteralPath (Join-Path $script:Evid 'state\ci_run'))) {
        $script:CiRun = Read-Line 'CI run id this build came from (checks should use the CI artifact; Enter if unknown or a local build)'
        Set-State 'ci_run' $script:CiRun
    }
    Write-Host "Build under test: $(Hide-Home $script:Exe), version $(Get-AppVersion)"
}

function Expand-TestZip([string]$ZipPath) {
    $ZipPath = (Resolve-Path -LiteralPath $ZipPath).ProviderPath
    $how = Read-Line 'Extract with [e]xplorer (recommended: it keeps the Mark of the Web, as a user gets it) or let the [k]it extract it? [e]'
    if ($how -match '^[Kk]') {
        $hash = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash.Substring(0, 8)
        $dest = Join-Path $WorkRoot ("apps\{0}-{1}" -f [IO.Path]::GetFileNameWithoutExtension($ZipPath), $hash)
        if (-not (Test-Path -LiteralPath (Join-Path $dest 'DialShift.exe'))) { Expand-Archive -LiteralPath $ZipPath -DestinationPath $dest -Force }
        Set-App $dest "zip $(Split-Path -Leaf $ZipPath), Expand-Archive"
        return
    }
    Start-Process -FilePath explorer.exe -ArgumentList ('/select,"{0}"' -f $ZipPath)
    Invoke-Step 'In the Explorer window, right-click the zip > Extract All..., and extract it.'
    $default = Join-Path (Split-Path -Parent $ZipPath) ([IO.Path]::GetFileNameWithoutExtension($ZipPath))
    $folder = Read-Line "Extracted folder [$default]"
    if (-not $folder) { $folder = $default }
    Set-App $folder "zip $(Split-Path -Leaf $ZipPath), Explorer extraction"
}

function Resolve-App {
    if ($script:Exe -and (Test-Path -LiteralPath $script:Exe)) { return }
    if ($AppFolder) { Set-App $AppFolder "folder $(Hide-Home $AppFolder)"; return }
    if ($Zip) { Expand-TestZip $Zip; return }
    $saved = Get-State 'app'
    if ($saved -and (Test-Path -LiteralPath $saved)) {
        $script:Exe = $saved
        $script:CiRun = Get-State 'ci_run'
        Write-Host "Build under test: $(Hide-Home $script:Exe), version $(Get-AppVersion)"
        return
    }
    Write-Host 'Which DialShift build should the kit test? Matrix section 9 asks for the CI artifact, the zip a user downloads.'
    while ($true) {
        $answer = Read-Line 'Path to DialShift-win-x64.zip, to the extracted folder, or to DialShift.exe'
        $answer = $answer.Trim('"')
        if ($answer -and (Test-Path -LiteralPath $answer)) {
            if ($answer -like '*.zip') { Expand-TestZip $answer } else { Set-App $answer "folder $(Hide-Home $answer)" }
            return
        }
        Write-Host 'Not found. Try again.'
    }
}

# ---------------------------------------------------------------------------------------------------------------------
# Running DialShift and reading its log
# ---------------------------------------------------------------------------------------------------------------------
function Get-DialShiftCount { @(Get-Process -Name DialShift -ErrorAction SilentlyContinue).Count }
function Wait-DialShiftRunning([int]$Seconds = 30) {
    for ($n = 0; $n -lt $Seconds; $n++) { if ((Get-DialShiftCount) -gt 0) { return }; Start-Sleep -Seconds 1 }
    Write-Warn 'DialShift did not start. If SmartScreen asked, choose More info > Run anyway.'
}
function Wait-DialShiftExit([int]$Seconds = 15) {
    for ($n = 0; $n -lt $Seconds; $n++) { if ((Get-DialShiftCount) -eq 0) { return $true }; Start-Sleep -Seconds 1 }
    return $false
}
function Request-Quit {
    if ((Get-DialShiftCount) -eq 0) { return }
    Wait-Enter 'Quit DialShift from its tray menu (right-click the tray icon > Quit DialShift), then press Enter'
    if (Wait-DialShiftExit 15) { return }
    Write-Warn 'DialShift is still running.'
    if (Confirm-Choice 'Stop it with Stop-Process (not a clean quit; note it)?' $false) {
        Get-Process -Name DialShift -ErrorAction SilentlyContinue | Stop-Process -Force
        [void](Wait-DialShiftExit 10)
    }
}
function Assert-NoDialShift {
    if ((Get-DialShiftCount) -eq 0) { return }
    Write-Host 'DialShift is running. This check needs it closed first: your own copy too, and any older DialShift (a new one refuses to start beside it).'
    Request-Quit
    if ((Get-DialShiftCount) -gt 0) { throw 'DialShift is still running.' }
}
function New-DataDir([string]$Id) {
    $dir = Join-Path $WorkRoot ('data\{0}-{1}' -f $Id, (Get-Date).ToString('yyyyMMdd-HHmmss', $Invariant))
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    return $dir
}
# Starts DialShift; with -DataDir it runs on an isolated data folder (DIALSHIFT_DATA_DIR, inherited by the process only).
function Start-DialShift([string]$DataDir = '', [string]$Arguments = '', [string]$Exe = $script:Exe) {
    if ($DataDir) { $env:DIALSHIFT_DATA_DIR = $DataDir }
    try {
        if ($Arguments) { $process = Start-Process -FilePath $Exe -ArgumentList $Arguments -PassThru }
        else { $process = Start-Process -FilePath $Exe -PassThru }
    } finally { Remove-Item Env:DIALSHIFT_DATA_DIR -ErrorAction SilentlyContinue }
    Wait-DialShiftRunning 30
    return $process
}

# The log is JSON Lines; DialShift keeps it open for writing only briefly, so it is read with sharing allowed.
function Read-LogLines([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return @() }
    $stream = New-Object IO.FileStream ($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try { $text = (New-Object IO.StreamReader ($stream, $Utf8NoBom)).ReadToEnd() } finally { $stream.Dispose() }
    return @($text -split "`n" | ForEach-Object { $_.TrimEnd("`r") } | Where-Object { $_ })
}
function Get-LogMark([string]$Path) { @(Read-LogLines $Path).Count }
function Get-LogSince([string]$Path, [int]$Mark) {   # covers one rotation to dialshift.log.1
    $lines = @(Read-LogLines $Path)
    if ($lines.Count -lt $Mark) { return @(@(Read-LogLines "$Path.1") | Select-Object -Skip $Mark) + $lines }
    return @($lines | Select-Object -Skip $Mark)
}
function ConvertFrom-LogLine([string]$Line) {
    $match = $LogPattern.Match($Line)
    if (-not $match.Success) { return }
    [pscustomobject]@{
        Time = [DateTimeOffset]::Parse($match.Groups['ts'].Value, $Invariant)
        Level = $match.Groups['level'].Value
        Event = $match.Groups['event'].Value
        Msg = $match.Groups['msg'].Value -replace '\\(["\\/])', '$1'
    }
}
function Read-Log([string]$Path, [int]$Mark = 0) { @(Get-LogSince $Path $Mark | ForEach-Object { ConvertFrom-LogLine $_ }) }
function Get-EventCount($Entries, [string]$EventName, [string]$Contains = '') {
    @($Entries | Where-Object { $_.Event -eq $EventName -and ($Contains -eq '' -or $_.Msg.Contains($Contains)) }).Count
}
# Saves the log lines after a mark as <name>.jsonl plus a readable <name>-timeline.txt; returns the parsed entries.
function Save-LogExcerpt([string]$Name, [string]$Path, [int]$Mark) {
    $lines = @(Get-LogSince $Path $Mark)
    Write-Utf8 (Join-Path $script:CurDir "$Name.jsonl") (($lines -join "`n") + "`n")
    $entries = @($lines | ForEach-Object { ConvertFrom-LogLine $_ })
    $timeline = @($entries | Where-Object { $_.Event -match $TimelineEvents })
    $text = ''
    if ($timeline.Count) {
        $first = $timeline[0].Time
        $text = (@($timeline | ForEach-Object {
                    '{0}  +{1,7}s  {2,-30} {3}' -f $_.Time.ToString('o'), ($_.Time - $first).TotalSeconds.ToString('0.0', $Invariant), $_.Event, $_.Msg
                }) -join "`n") + "`n"
    }
    Write-Utf8 (Join-Path $script:CurDir "$Name-timeline.txt") $text
    Add-Artifact "$($script:CurId)/$Name.jsonl"
    Add-Artifact "$($script:CurId)/$Name-timeline.txt"
    return $entries
}
function Test-CleanExit([string]$Log, [string]$Prefix) {
    $exit = @(Read-Log $Log | Where-Object { $_.Event -eq 'app.exit' }) | Select-Object -Last 1
    $msg = ''
    if ($exit) { $msg = $exit.Msg }
    Add-AutoStep "$Prefix The log ends with app.exit code=0 clean=true" ($msg -match '(?i)code=0 clean=true') $msg
    Add-AutoStep "$Prefix No DialShift.exe process remains" ((Get-DialShiftCount) -eq 0) "$(Get-DialShiftCount) running"
}
function Test-Activation([string]$Prefix, [string]$Log, [int]$Before) {
    $after = Get-EventCount (Read-Log $Log) 'single_instance.activated' 'delivered'
    Add-AutoStep "$Prefix The log has single_instance.activated ... delivered" ($after -gt $Before) "before $Before, after $after"
    Add-AutoStep "$Prefix One DialShift process remains" ((Get-DialShiftCount) -eq 1) "$(Get-DialShiftCount) running"
}

# ---------------------------------------------------------------------------------------------------------------------
# Real data: move the user's DialShift data aside and save the launch-at-sign-in values, restore them afterwards
# ---------------------------------------------------------------------------------------------------------------------
function Read-Manifest { [IO.File]::ReadAllText($ManifestPath) | ConvertFrom-Json }
function Save-Manifest($Manifest) { Write-Utf8 $ManifestPath (ConvertTo-Json -InputObject $Manifest) }
function Get-EntryValue([string]$Key) {
    $item = Get-ItemProperty -LiteralPath $Key -Name $EntryName -ErrorAction SilentlyContinue
    if ($null -eq $item) { return $null }
    return $item.$EntryName
}
function Get-ApprovedByte { $value = Get-EntryValue $ApprovedKey; if ($null -eq $value) { return 'absent' }; return '{0:X2}' -f $value[0] }
function Set-EntryValue([string]$Key, [string]$Type, $Value) {
    if ($null -eq $Value) { Remove-ItemProperty -LiteralPath $Key -Name $EntryName -ErrorAction SilentlyContinue; return }
    if (-not (Test-Path -LiteralPath $Key)) { New-Item -Path $Key -Force | Out-Null }
    New-ItemProperty -LiteralPath $Key -Name $EntryName -PropertyType $Type -Value $Value -Force | Out-Null
}
function Get-StartupEntries {
    $bytes = Get-EntryValue $ApprovedKey
    $approved = 'absent'
    if ($null -ne $bytes) { $approved = (@($bytes | ForEach-Object { '{0:X2}' -f $_ })) -join ' ' }
    "HKCU\...\Run\DialShift: $(Get-EntryValue $RunKey)"
    "HKCU\...\Explorer\StartupApproved\Run\DialShift: $approved"
}

function Backup-RealData([string]$Owner) {
    if (Test-Path -LiteralPath $ManifestPath) {
        if ((Read-Manifest).owner -eq $Owner) { return $true }
        Write-Warn "Your real DialShift files are still moved aside by $((Read-Manifest).owner). Restore them first (menu option r)."
        return $false
    }
    Write-Host ''
    Write-Warn "$Owner needs DialShift's real data folder: Explorer, Start menu and sign-in launches can't use an isolated one."
    Write-Host "The kit moves $(Hide-Home $RealData) aside (the check starts with default settings) and saves the"
    Write-Host 'launch-at-sign-in registry values (Run and StartupApproved\Run, value DialShift). It restores both when'
    Write-Host "$Owner ends, or with menu option r."
    if (-not (Confirm-Choice 'Continue?' $false)) { return $false }
    Assert-NoDialShift
    $dir = Join-Path $WorkRoot ('backup\{0}-{1}' -f (Get-Date).ToString('yyyyMMdd-HHmmss', $Invariant), $Owner)
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $approved = Get-EntryValue $ApprovedKey
    $approvedText = $null
    if ($null -ne $approved) { $approvedText = [Convert]::ToBase64String([byte[]]$approved) }
    $manifest = [pscustomobject]@{ owner = $Owner; dir = $dir; data = $null; run = (Get-EntryValue $RunKey); approved = $approvedText; folderFrom = $null; folderTo = $null }
    Save-Manifest $manifest
    if (Test-Path -LiteralPath $RealData) {
        Move-Item -LiteralPath $RealData -Destination (Join-Path $dir 'data')
        $manifest.data = Join-Path $dir 'data'
        Save-Manifest $manifest
    }
    Write-Host "Originals saved in $(Hide-Home $dir)."
    return $true
}

# NC-04 step 6: moves an existing Install.ps1 install and its Start menu shortcut aside before Install.ps1 replaces them.
function Backup-Install {
    $manifest = Read-Manifest
    if ($manifest.installRan) { return }   # already saved by an earlier run of step 6
    $programs = $null
    if (Test-Path -LiteralPath $InstalledDir) {
        $programs = Join-Path $manifest.dir 'Programs-DialShift'
        Move-Item -LiteralPath $InstalledDir -Destination $programs
    }
    $shortcut = $null
    if (Test-Path -LiteralPath $StartMenuShortcut) {
        $shortcut = Join-Path $manifest.dir 'DialShift.lnk'
        Copy-Item -LiteralPath $StartMenuShortcut -Destination $shortcut
    }
    $manifest | Add-Member -NotePropertyName installRan -NotePropertyValue $true -Force
    $manifest | Add-Member -NotePropertyName programs -NotePropertyValue $programs -Force
    $manifest | Add-Member -NotePropertyName shortcut -NotePropertyValue $shortcut -Force
    Save-Manifest $manifest
    Write-Host "Saved the existing install and shortcut (if any) in $(Hide-Home $manifest.dir)."
}

function Restore-RealData {
    if (-not (Test-Path -LiteralPath $ManifestPath)) { Write-Host 'Nothing to restore.'; return }
    $manifest = Read-Manifest
    Write-Title "Restoring your DialShift files (saved by $($manifest.owner))"
    Assert-NoDialShift
    $aside = Join-Path $WorkRoot ('test-leftovers\{0}-{1}' -f (Get-Date).ToString('yyyyMMdd-HHmmss', $Invariant), $manifest.owner)
    New-Item -ItemType Directory -Force -Path $aside | Out-Null
    if (Test-Path -LiteralPath $RealData) { Move-Item -LiteralPath $RealData -Destination (Join-Path $aside 'data') }
    if ($manifest.data) { Move-Item -LiteralPath $manifest.data -Destination $RealData }
    Set-EntryValue $RunKey 'String' $manifest.run
    $approved = $null
    if ($manifest.approved) { $approved = [Convert]::FromBase64String($manifest.approved) }
    Set-EntryValue $ApprovedKey 'Binary' $approved
    if ($manifest.installRan) {   # NC-04 step 6 ran Install.ps1: put back the install and shortcut it replaced
        if (Test-Path -LiteralPath $InstalledDir) { Move-Item -LiteralPath $InstalledDir -Destination (Join-Path $aside 'Programs-DialShift') }
        if ($manifest.programs) { Move-Item -LiteralPath $manifest.programs -Destination $InstalledDir }
        if (Test-Path -LiteralPath $StartMenuShortcut) { Move-Item -LiteralPath $StartMenuShortcut -Destination (Join-Path $aside 'DialShift.lnk') }
        if ($manifest.shortcut) { Copy-Item -LiteralPath $manifest.shortcut -Destination $StartMenuShortcut }
    }
    if ($manifest.folderFrom -and (Test-Path -LiteralPath $manifest.folderTo) -and -not (Test-Path -LiteralPath $manifest.folderFrom)) {
        Move-Item -LiteralPath $manifest.folderTo -Destination $manifest.folderFrom
        Set-App (Join-Path $manifest.folderFrom 'DialShift.exe') (Get-State 'source')
    }
    Move-Item -LiteralPath $ManifestPath -Destination (Join-Path $aside 'manifest.restored.json')
    Write-Host "Restored. What the test left behind (its data folder, and the Install.ps1 copy if step 6 ran) is in $(Hide-Home $aside)."
}
function Request-Restore { if ((Test-Path -LiteralPath $ManifestPath) -and (Confirm-Choice 'Restore your real DialShift files now?' $true)) { Restore-RealData } }

# ---------------------------------------------------------------------------------------------------------------------
# Optional screenshots (only with your consent, one prompt per screenshot)
# ---------------------------------------------------------------------------------------------------------------------
function Save-Screenshot([string]$Name, [string]$What) {
    if ($null -eq $script:ShotsOk) {
        $script:ShotsOk = Confirm-Choice 'Screenshots are optional. Should the kit offer them (each one asks first and captures the whole primary screen)?' $false
    }
    if (-not $script:ShotsOk) { return }
    if (-not (Confirm-Choice "Screenshot of $What in 5 seconds (close anything private first)?" $true)) { return }
    Start-Sleep -Seconds 5
    Add-Type -AssemblyName System.Windows.Forms, System.Drawing
    if (-not ('NativeCheck.Dpi' -as [type])) {
        Add-Type -Namespace NativeCheck -Name Dpi -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();'
    }
    [void][NativeCheck.Dpi]::SetProcessDPIAware()   # full-resolution capture on scaled displays
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bitmap = New-Object System.Drawing.Bitmap ($bounds.Width, $bounds.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
        $bitmap.Save((Join-Path $script:CurDir "$Name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
    Add-Artifact "$($script:CurId)/$Name.png"
    Write-Host 'Saved.'
}

# ---------------------------------------------------------------------------------------------------------------------
# Shared scenarios: seeded settings, the smoke, the corpus run, the retry timeline, sleep and wake
# ---------------------------------------------------------------------------------------------------------------------
function Write-SettingsFile([string]$DataDir, $Stations, [string]$FallbackId = '') {
    $settings = [ordered]@{ Version = 1; Stations = @($Stations); Schedule = @(); Volume = 60 }
    if ($FallbackId) { $settings.FallbackStationId = $FallbackId }
    Write-Utf8 (Join-Path $DataDir 'settings.json') (ConvertTo-Json -InputObject $settings -Depth 5)
}
function Write-CorpusSettings([string]$DataDir, $List) {
    Write-SettingsFile $DataDir @($List | ForEach-Object { [ordered]@{ Name = $_.Id; Url = $_.Url; Tag = "$($_.What); expect $($_.Expect)" } })
}
function Write-RetrySettings([string]$DataDir) {   # an unreachable primary whose fallback is Groove Salad
    $fallback = [guid]::NewGuid().ToString()
    Write-SettingsFile $DataDir @(
        [ordered]@{ Id = [guid]::NewGuid().ToString(); Name = 'RETRY-PRIMARY'; Url = 'http://127.0.0.1:1/unavailable'; Tag = 'Refuses connections: retries, then the fallback' },
        [ordered]@{ Id = $fallback; Name = 'RETRY-FALLBACK'; Url = 'https://ice1.somafm.com/groovesalad-128-mp3'; Tag = 'Groove Salad, the fallback' }
    ) $fallback
}

function Invoke-Smoke([string]$Out) {
    if (Test-Path -LiteralPath $Out) { Remove-Item -LiteralPath $Out -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $Out | Out-Null
    Write-Host 'Running DialShift --smoke-test --recovery-test (about 1-2 minutes; windows appear and close by themselves).'
    return (Start-Process -FilePath $script:Exe -ArgumentList ('--smoke-test --recovery-test --output "{0}"' -f $Out) -Wait -PassThru).ExitCode
}
function Test-SmokeResults([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path)) { Add-AutoStep $Label $false 'results.json is missing'; return }
    $results = [IO.File]::ReadAllText($Path) | ConvertFrom-Json
    $checks = @($results.checks)
    $failed = @($checks | Where-Object { -not $_.passed } | ForEach-Object { $_.name })
    $detail = '{0} checks, passed={1}, complete={2}' -f $checks.Count, $results.passed, $results.complete
    if ($failed.Count) { $detail += '; failed: ' + ($failed -join '; ') }
    Add-AutoStep $Label ([bool]$results.passed -and [bool]$results.complete) $detail
}

# Waits up to 40 s after a Listen for the station's first outcome: Playing, a failure kind, or timeout.
function Measure-Station([string]$Log, [string]$Id, [int]$Mark) {
    $deadline = (Get-Date).AddSeconds(40)
    $tag = "station='$Id'"
    while ((Get-Date) -lt $deadline) {
        $mine = @(Read-Log $Log $Mark | Where-Object { $_.Msg.Contains($tag) })
        $end = $mine | Where-Object { ($_.Event -eq 'playback.state' -and $_.Msg.Contains('> Playing;')) -or $_.Event -eq 'playback.failed' } | Select-Object -First 1
        if ($end) {
            $outcome = 'Playing'
            if ($end.Event -eq 'playback.failed' -and $end.Msg -match 'kind=(\w+)') { $outcome = $Matches[1] }
            $start = $mine | Where-Object { $_.Event -eq 'playback.state' -and $_.Msg -match '> (Connecting|Reconnecting);' } | Select-Object -First 1
            $seconds = ''
            if ($start) { $seconds = ($end.Time - $start.Time).TotalSeconds.ToString('0.0', $Invariant) }
            return [pscustomobject]@{ Outcome = $outcome; Seconds = $seconds }
        }
        Start-Sleep -Seconds 1
    }
    return [pscustomobject]@{ Outcome = 'timeout'; Seconds = '' }
}
function Invoke-Corpus([string]$Log, $List) {   # you press Listen on each station; the kit reads the outcome from the log
    $table = Join-Path $script:CurDir 'corpus.tsv'
    if (-not (Test-Path -LiteralPath $table)) { Write-Utf8 $table "id`turl`texpected`toutcome`tseconds`taudible`n" }
    Add-Artifact "$($script:CurId)/corpus.tsv"
    foreach ($entry in $List) {
        Write-Host ''
        Write-Host "$($entry.Id): $($entry.What) (expected: $($entry.Expect))"
        $mark = Get-LogMark $Log
        $answer = Read-Line "   On the Stations page press Listen on $($entry.Id), then press Enter here (s = skip)"
        if ($answer -eq 's') { Add-Step 'manual' 'SKIP' "$($entry.Id) $($entry.What)" 'skipped'; continue }
        $measured = Measure-Station $Log $entry.Id $mark
        $secondsText = ''
        if ($measured.Seconds) { $secondsText = " in $($measured.Seconds) s" }
        Write-Host "   Outcome: $($measured.Outcome)$secondsText"
        $heard = '-'
        if ($measured.Outcome -eq 'Playing') { $heard = 'no'; if (Confirm-Choice '   Do you hear it?' $true) { $heard = 'yes' } }
        Add-Utf8Line $table ((@($entry.Id, $entry.Url, $entry.Expect, $measured.Outcome, $measured.Seconds, $heard)) -join "`t")
        Add-AutoStep "$($entry.Id) $($entry.What)" ($measured.Outcome -eq $entry.Expect -and $heard -ne 'no') "expected $($entry.Expect), got $($measured.Outcome)$secondsText; audible: $heard"
    }
}

function Test-RetryTimeline($Entries) {   # the section 5.1 retry and fallback timings for RETRY-PRIMARY / RETRY-FALLBACK
    $failures = @($Entries | Where-Object { $_.Event -eq 'playback.failed' -and $_.Msg.Contains("station='RETRY-PRIMARY'") })
    $retries = (@($failures | Select-Object -First 3 | ForEach-Object { if ($_.Msg -match 'retry_in=(\d+)s') { $Matches[1] } })) -join ' '
    Add-AutoStep 'Retries after failures 1, 2 and 3 are due in 3 s, 6 s and 30 s' ($retries -eq '3 6 30') "retry_in: $retries"
    $switches = @($Entries | Where-Object { $_.Event -eq 'playback.fallback' -and $_.Msg.Contains("Switching to fallback 'RETRY-FALLBACK'") })
    Add-AutoStep 'The fallback takes over after 3 failures' ($switches.Count -ge 1) "$($switches.Count) switch(es) to the fallback"
    $playing = $Entries | Where-Object { $_.Event -eq 'playback.state' -and $_.Msg.Contains("> Playing; station='RETRY-FALLBACK'") } | Select-Object -First 1
    $recheck = $Entries | Where-Object { $_.Event -eq 'playback.fallback' -and $_.Msg.Contains("Re-trying primary 'RETRY-PRIMARY'") } | Select-Object -First 1
    if ($playing -and $recheck) {
        $delay = [int]($recheck.Time - $playing.Time).TotalSeconds
        Add-AutoStep 'The primary is re-checked 120 s after the fallback plays' ($delay -ge 115 -and $delay -le 130) "$delay s"
    } else {
        Add-AutoStep 'The primary is re-checked 120 s after the fallback plays' $false 'no fallback Playing or no primary re-check in the log'
    }
    Add-AutoStep 'Alternation: the fallback takes over again after the re-check fails' ($switches.Count -ge 2) "$($switches.Count) switch(es)"
}
function Invoke-RetryScenario {
    $data = New-DataDir "$($script:CurId)-fallback"
    Write-RetrySettings $data
    $log = Join-Path $data 'dialshift.log'
    [void](Start-DialShift -DataDir $data)
    $mark = Get-LogMark $log
    Invoke-Step 'Open the window (click the tray icon) and press Listen on RETRY-PRIMARY. It refuses connections; its fallback is RETRY-FALLBACK (Groove Salad). The kit then records for about 4 minutes.' 'Press Enter right after pressing Listen'
    Start-Countdown 250 'Recording retries, the fallback and the primary re-check:'
    Test-RetryTimeline @(Save-LogExcerpt 'fallback' $log $mark)
    Read-Step 'Groove Salad (the fallback) was audible while the primary failed'
    Invoke-Step 'Press Pause and listen for 10 seconds.'
    Read-Step 'No audio after Stop'
    Request-Quit
}

# A local server that accepts connections and never answers (corpus T14, the 25 s watchdog).
function Start-HangServer {
    $probe = New-Object System.Net.Sockets.TcpListener ([System.Net.IPAddress]::Loopback, 0)
    $probe.Start()
    $port = $probe.LocalEndpoint.Port
    $probe.Stop()
    $job = Start-Job -ArgumentList $port -ScriptBlock {
        param($Port)
        $listener = New-Object System.Net.Sockets.TcpListener ([System.Net.IPAddress]::Loopback, $Port)
        $listener.Start()
        $held = New-Object System.Collections.ArrayList
        while ($true) { [void]$held.Add($listener.AcceptTcpClient()) }
    }
    Start-Sleep -Seconds 2
    return [pscustomobject]@{ Job = $job; Port = $port }
}
function Stop-HangServer($Server) { Stop-Job -Job $Server.Job; Remove-Job -Job $Server.Job -Force }

# Sleeps the PC (you, or rundll32 with a scheduled wake) and returns once you are back.
function Invoke-SleepCycle([datetime]$WakeAfter = [datetime]::MinValue) {
    $until = ''
    if ($WakeAfter -ne [datetime]::MinValue) { $until = " and until after $($WakeAfter.ToString('HH:mm', $Invariant))" }
    $how = Read-Line 'Sleep by [m]anual Start > Power > Sleep (as the matrix says), or let the kit sleep the PC and schedule a wake [a]? [m]'
    if ($how -notmatch '^[Aa]') {
        Wait-Enter "Press Enter, then sleep the PC (Start > Power > Sleep; on a laptop also close the lid) for at least 30 seconds$until"
        Wait-Enter 'Welcome back. Sign in if asked, wait 20 seconds, then press Enter'
        return
    }
    $wakeAt = (Get-Date).AddMinutes(2)
    if ($WakeAfter -ne [datetime]::MinValue) { $wakeAt = $WakeAfter.AddMinutes(1) }
    try {
        $action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument '/c exit 0'
        $trigger = New-ScheduledTaskTrigger -Once -At $wakeAt
        $settings = New-ScheduledTaskSettingsSet -WakeToRun -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
        Register-ScheduledTask -TaskName $WakeTaskName -Action $action -Trigger $trigger -Settings $settings -Force | Out-Null
        Write-Host "A wake is scheduled for $($wakeAt.ToString('HH:mm', $Invariant)). Wake timers must be allowed (Power Options > Change advanced settings > Sleep > Allow wake timers); if not, press the power button then."
    } catch {
        Write-Warn "couldn't schedule a wake ($($_.Exception.Message)). Wake the PC yourself after $($wakeAt.ToString('HH:mm', $Invariant))."
    }
    Write-Host 'Note: with hibernation turned on, this command hibernates instead of sleeping; note it if that happens.'
    Write-Host 'Stay near the PC: after an unattended wake Windows sleeps again after about 2 minutes, so press a key when it wakes.'
    Start-Countdown 10 'Sleeping in'
    Start-Process -FilePath rundll32.exe -ArgumentList 'powrprof.dll,SetSuspendState 0,1,0' -Wait
    Wait-Enter 'Welcome back. Sign in if asked, wait 20 seconds, then press Enter'
    Remove-WakeTask
}
function Remove-WakeTask {
    if (Get-ScheduledTask -TaskName $WakeTaskName -ErrorAction SilentlyContinue) { Unregister-ScheduledTask -TaskName $WakeTaskName -Confirm:$false }
}

function Test-Wake($Entries, [string]$Mode, [string]$Prefix) {   # Mode: playing, paused or tick_gap
    $resumed = Get-EventCount $Entries 'power_events.resumed'
    if ($Mode -eq 'tick_gap') {
        Add-AutoStep "$Prefix The tick gap detected the wake (wake.detected source=tick_gap)" ((Get-EventCount $Entries 'wake.detected' 'source=tick_gap') -ge 1) "power_events.resumed: $resumed"
    } else {
        Add-AutoStep "$Prefix power_events.resumed was logged" ($resumed -ge 1) "$resumed line(s)"
    }
    if ($Mode -eq 'paused') {
        $active = @($Entries | Where-Object { $_.Event -eq 'playback.state' -and $_.Msg -match '> (Connecting|Reconnecting|Playing);' }).Count
        Add-AutoStep "$Prefix Paused stays paused (no Connecting, Reconnecting or Playing)" ($active -eq 0) "$active such transition(s)"
        return
    }
    $accepted = (Get-EventCount $Entries 'wake.detected') - (Get-EventCount $Entries 'wake.detected' 'ignored:')
    $recoveries = @($Entries | Where-Object { $_.Event -eq 'wake.recovery' })
    Add-AutoStep "$Prefix Exactly one accepted wake.detected" ($accepted -eq 1) "$accepted accepted"
    Add-AutoStep "$Prefix Exactly one wake.recovery (one reconnect)" ($recoveries.Count -eq 1) ((@($recoveries | ForEach-Object { $_.Msg })) -join '; ')
    Add-AutoStep "$Prefix Playback is Playing again after the recovery" ((Get-EventCount $Entries 'playback.state' '> Playing;') -ge 1)
}
# HZ-04: the delay from the wake (System log, Power-Troubleshooter event 1) to DialShift's power_events.resumed.
function Measure-ResumeDelay($Entries, [datetime]$Since) {
    $resumed = @($Entries | Where-Object { $_.Event -eq 'power_events.resumed' }) | Select-Object -First 1
    $wakeEvent = $null
    try {
        $wakeEvent = Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-Power-Troubleshooter'; Id = 1; StartTime = $Since } -MaxEvents 1 -ErrorAction Stop
    } catch { $wakeEvent = $null }   # no such event since $Since
    if (-not $resumed -or -not $wakeEvent) { return 'not measured (no power_events.resumed line or no Power-Troubleshooter event 1)' }
    $wake = [DateTimeOffset]$wakeEvent.TimeCreated
    $data = @(([xml]$wakeEvent.ToXml()).Event.EventData.Data | Where-Object { $_.Name -eq 'WakeTime' }) | Select-Object -First 1
    if ($data -and $data.'#text') { $wake = [DateTimeOffset]::Parse(($data.'#text' -replace '(\.\d{7})\d+', '$1'), $Invariant) }
    return 'wake {0} -> Resume {1}: {2} s' -f $wake.ToString('o'), $resumed.Time.ToString('o'), ($resumed.Time - $wake).TotalSeconds.ToString('0.0', $Invariant)
}
# A slot start a few minutes ahead in a zone whose offset differs from this PC's (TZ-12).
function Get-SlotSuggestion([int]$Minutes) {
    $now = [DateTime]::UtcNow
    $zone = 'Europe/Athens'
    $windowsZone = 'GTB Standard Time'
    if ([TimeZoneInfo]::FindSystemTimeZoneById($windowsZone).GetUtcOffset($now) -eq [TimeZoneInfo]::Local.GetUtcOffset($now)) {
        $zone = 'America/New_York'
        $windowsZone = 'Eastern Standard Time'
    }
    $start = $now.AddMinutes($Minutes)
    $start = [DateTime]::new($start.Year, $start.Month, $start.Day, $start.Hour, $start.Minute, 0, [DateTimeKind]::Utc)
    $inZone = [TimeZoneInfo]::ConvertTimeFromUtc($start, [TimeZoneInfo]::FindSystemTimeZoneById($windowsZone))
    $local = [TimeZoneInfo]::ConvertTimeFromUtc($start, [TimeZoneInfo]::Local)
    [pscustomobject]@{ Zone = $zone; WindowsZone = $windowsZone; Time = $inZone.ToString('HH:mm', $Invariant); Day = $inZone.DayOfWeek.ToString(); Local = $local }
}

# UI Automation (NC-15): the window's controls by accessible name.
function Find-UiButton([int]$ProcessId, [string]$Name) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $byProcess = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    $byName = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    foreach ($window in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $byProcess)) {
        $element = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $byName)
        if ($element) { return $element }
    }
    return $null
}
function Invoke-UiButton($Element) { $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }

# After a run: waits up to 20 s for the last playback.state to be Playing (or, after Pause, checks it stayed inactive).
function Wait-LastState([string]$Log, [int]$Mark, [bool]$Paused) {
    $deadline = (Get-Date).AddSeconds(20)
    do {
        $last = @(Read-Log $Log $Mark | Where-Object { $_.Event -eq 'playback.state' }) | Select-Object -Last 1
        $msg = 'no state change'
        if ($last) { $msg = $last.Msg }
        if ($Paused) { $ok = [bool]$last -and $msg -notmatch '> (Connecting|Reconnecting|Playing);' }
        else { $ok = [bool]$last -and $msg.Contains('> Playing;') }
        if ($ok -and -not $Paused) { break }
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $deadline)
    return [pscustomobject]@{ Ok = $ok; Msg = $msg }
}

# ---------------------------------------------------------------------------------------------------------------------
# Procedures and pass criteria (a summary of acceptance matrix section 9; section 9 wins if they disagree)
# ---------------------------------------------------------------------------------------------------------------------
function Show-Procedure([string]$Id) {
    Write-Title "$($Id): procedure and pass criteria (acceptance matrix section 9)"
    switch ($Id) {
        'NC-01' {
            Write-Host @"
Procedure (a Windows 11 x64 desktop with speakers; the CI DialShift-win-x64 zip extracted with Explorer): first run
DialShift.exe --smoke-test --recovery-test --output <dir> once; it must exit 0 with 34/34 in results.json. Then launch
normally and check by hand:
(1) Tray: left-click opens the window; right-click shows Open, Play/Pause, Next station, Stations, Follow schedule (its
    check matches the Schedule page), Volume +10/-10 and Quit DialShift, and each item works; hovering shows the tooltip
    "DialShift $Dot <station>" or "DialShift $Dot Paused".
(2) Audio: Listen plays audibly; Skip and the tray's Next station each switch audibly, and exactly one stream is
    audible; nothing is heard at volume 0 or after Pause.
(3) Window: close (X) and minimize hide it while audio continues; "Hide to tray" hides it; --tray and "Start in the
    tray" start with no window flash.
(4) Dialogs: a confirm (delete a slot) is owned by the window and Enter/Escape work; "Open settings folder" opens
    Explorer at %LOCALAPPDATA%\DialShift; Tab shows a visible focus ring on every control.
(5) Recovery and failure: settings.json replaced with { gives one "DialShift $Dot Settings recovered" dialog naming the
    settings.json.unreadable-* copy; a folder named .single-instance.lock in the data folder gives the "DialShift
    couldn't start" dialog about its lock file, and the process exits 1. Then start an older DialShift, the WPF app
    (upstream v0.2.0 from tsiger/DialShift's releases, or the WPF build at tag legacy-last-known-good; it holds the
    mutex Local\DialShift.App), and launch the new DialShift.exe the same way: the "DialShift couldn't start. An older
    DialShift is still running. Quit it from its tray icon, then open DialShift again." dialog appears, the process
    exits 1, the log has app.legacy_instance_running, and only the older app plays. Quit the older app from its tray:
    the new one then starts normally (SW-S2, D55).
(6) Look: the three pages, both editors and the compact 780x650 size show no clipped or overlapping text (QG-03).
(7) Quit from the tray.

Pass: every observation holds; the log has app.exit ... code=0 clean=true and no DialShift.exe process remains.

The kit runs the smoke and reads results.json, moves your real DialShift data aside (so %LOCALAPPDATA%\DialShift is
the folder under test), starts and restarts the app, prepares the broken settings file and the lock folder, reads the
exit code, checks the log, and restores your data at the end. For the older app it asks for its DialShift.exe (or
records SKIP), starts it, checks that it holds the mutex, reads the new app's exit code and the log, and checks that
only the older app is left. DIALSHIFT_AUDIO_OUTPUT must not be set: this check needs real audio.
"@
        }
        'NC-02' {
            Write-Host @"
Procedure (Windows hardware):
(1) While playing, Start > Sleep for at least 30 s (on a laptop, also close the lid), then wake.
(2) Repeat while paused inside a slot.
(3) Add a slot in another time zone (for example Europe/Athens when the PC is not on Athens time) that starts while the
    PC sleeps, and wake after its start (TZ-12).
(4) Repeat (1) with power events unavailable, so only the GetTickCount64 tick gap detects the wake (D14): a build with
    WindowsPowerEvents.Start disabled, or confirm power_events.unavailable in the log.

Pass: power_events.started at startup; on each wake power_events.resumed, one wake.detected (os or tick_gap), then
exactly one wake.recovery and one reconnect. Record the delay between wake and Resume, and whether a second recovery
follows a Resume more than 10 s late (HZ-04). A paused app stays paused. The slot that started during sleep plays, only
once (schedule.fired names the zone). With power events off, the tick gap still recovers. Record the thread Resumed
arrives on (D49 expects the UI thread; SR-02). Quit unsubscribes with no SystemEvents hang.

The kit uses an isolated data folder, can sleep the PC for you (rundll32 powrprof.dll,SetSuspendState 0,1,0 with a
scheduled wake), computes the slot time in the other zone, analyses the log after each wake, and reads the wake time
from the System event log to measure the wake-to-Resume delay.
"@
        }
        'NC-03' {
            Write-Host @"
Procedure: run the docs/spikes.md corpus against the Windows build (LibVLC) on speakers and record the time to Playing
or Failed and the kind. Run the recovery policy with the real engine against an unreachable station: retries at 3/6/30 s,
the fallback after 3 failures, the primary re-check at 120 s, alternation. Check the 25 s watchdog on a hanging server
(T14). Check titles: shown for a Shoutcast v1 http:// station (it answers ICY 200 OK); for an http:// Icecast station,
record whether titles or the tag show; the tag for https:// (D26). This adds audible output, mute at volume 0 and public
MP3/AAC/HLS/HTTPS streams to what CI covers (HS-17 LV-01..LV-11 on the silent output).

Pass: the kinds match the Adapter column or the difference is explained; no crash; no audio after Stop; the retry and
fallback timings match section 5.1.

The kit seeds an isolated data folder with the corpus stations (C1-C15, the public T cases, and T14 against a local
server the kit runs that accepts connections and never answers). You press Listen on each; the kit reads the outcome
and time from the log and asks whether you hear it (corpus.tsv). For the timings it sets up a station that refuses
connections, with Groove Salad as its fallback, and checks them in the log. The cases that need the test harness's
local server (T4, T7, T8, T10-T13) stay covered by CI. The matrix's hosts-file variant needs administrator rights: the
kit explains it and records it if you run it.
"@
        }
        'NC-04' {
            Write-Host @"
Procedure (the extracted CI zip):
(1) Turn on "Launch DialShift in the tray when I sign in", sign out and back in: DialShift starts in the tray with no
    window.
(2) Move the extracted folder and sign in again: the checkbox shows off with "Launch at sign-in points to an older copy
    of DialShift..."; turning it on repairs it.
(3) In Task Manager > Startup apps, disable DialShift, then reopen DialShift's Settings: the checkbox shows off with
    "Turned off in Task Manager's Startup apps...".
(4) Turn it on in DialShift: Task Manager shows DialShift Enabled after a refresh, and the next sign-in starts it.
(5) Turn it off in DialShift: both registry values are gone.
(6) Upgrade with Install.ps1: turn launch at sign-in on from an extracted copy outside
    %LOCALAPPDATA%\Programs\DialShift, quit it, then run that folder's Install.ps1: the Run value now names
    %LOCALAPPDATA%\Programs\DialShift\DialShift.exe, the installed app's Settings shows the checkbox on with no
    diagnostic, and the next sign-in starts it (README "Upgrading from an earlier DialShift", SW-S5).

Pass: the startup_registration.result lines match each step; HKCU\...\Run\DialShift is "<exe>" --tray;
StartupApproved\Run\DialShift starts with 03 after (3) and 02 after (4) (D39).

The kit saves your DialShift data and both registry values and restores them at the end. It reads the registry after
each step, checks what started after each sign-in, and moves the folder for step 2 (back at the end). For step 6 it
moves an existing %LOCALAPPDATA%\Programs\DialShift and Start menu shortcut aside (they are put back at the end), runs
Install.ps1 with -NoLaunch so it can wait for it, reads the Run value and starts the installed copy. It needs four
sign-out and sign-in cycles (three if you skip step 6): after each sign-in, run the kit again and it continues.
"@
        }
        'NC-05' {
            Write-Host @"
Procedure (release item, D7): (a) development build, now: on a clean Windows 11 PC, download the CI
DialShift-win-x64.zip with a browser (so it carries the Mark of the Web), extract it with Explorer and run
DialShift.exe. Record whether SmartScreen shows "Windows protected your PC" and whether "More info > Run anyway" starts
the app. (b) release: DialShift.exe signed with an Authenticode certificate and an RFC 3161 timestamp.

Pass for (b): signtool verify /pa /v DialShift.exe succeeds; Get-AuthenticodeSignature is Valid with the publisher's
name; on a clean PC the downloaded zip launches with the publisher shown and no "unknown publisher" block (a new
certificate may still get a SmartScreen reputation warning; record it). (a) is an observation to record.

The kit checks the Mark of the Web on the zip and on the extracted DialShift.exe, asks what SmartScreen showed, and
reads the Authenticode status. (b) runs only when the build is signed; otherwise it is recorded as SKIP.
"@
        }
        'NC-06' {
            Write-Host @"
Procedure: with the app hidden in the tray, launch it again from the Start menu and from an Explorer double-click, both
while another app has focus.

Pass: the existing window comes to the foreground (not just a flashing taskbar button); the second process exits 0;
the log has single_instance.activated ... delivered.

The kit moves your real DialShift data aside (Start menu and Explorer launches use the real data folder), opens Notepad
to hold the focus, opens Explorer at DialShift.exe, starts one more second launch itself to read its exit code, counts
the activations in the log, and restores your data at the end. The Start menu variant needs the entry Install.ps1
creates; the kit runs Install.ps1 only in NC-04 step 6, and undoes it when it restores.
"@
        }
        'NC-15' {
            Write-Host @"
Procedure (a physical Windows 11 x64 PC with speakers): with at least 10 public stations, start one, then drive the
window's Next station button (accessible name "Next station") through UI Automation: InvokePattern.Invoke() 160 times
with random 0-300 ms gaps. Do 7 runs. Between runs record (Get-Process DialShift).WorkingSet64. In one run, also press
Pause while a station is connecting.

Pass: no crash or hang in any run; at most one station audible at any moment; the working set after runs 2-7 stays
within +/-20 MB of its value after run 1; after each run the last station plays (or stays paused after Pause); the log
has no playback.engine_error.

The kit seeds an isolated data folder with 13 public stations, drives the button through UI Automation, presses Pause
right after the last press of run 4, checks the process and the log after each run, records the working set
(stress.tsv) and asks what you heard. If UI Automation can't find the button, you click it by hand and the kit still
checks each run.
"@
        }
    }
    Write-Host '(A summary of matrix section 9. If the two disagree, section 9 is right.)'
}

# ---------------------------------------------------------------------------------------------------------------------
# NC-01: tray, window, audio and dialogs (the smoke first; then the real data folder)
# ---------------------------------------------------------------------------------------------------------------------
function Test-NC01 {
    Start-Check 'NC-01'
    Show-Procedure 'NC-01'
    Resolve-App
    if (-not (Test-Precondition 'DIALSHIFT_AUDIO_OUTPUT is not set for your account (real audio)' (-not (Test-AudioOverride)))) { return }
    Write-Title 'The smoke test'
    $code = Invoke-Smoke (Join-Path $script:CurDir 'smoke')
    Add-AutoStep 'Smoke: --smoke-test --recovery-test exits 0' ($code -eq 0) "exit code $code"
    Test-SmokeResults (Join-Path $script:CurDir 'smoke\results.json') 'Smoke: results.json reports every check passed (34/34 expected)'
    Add-Artifact 'NC-01/smoke/'
    if (-not (Backup-RealData 'NC-01')) {
        Add-Step 'manual' 'SKIP' 'Steps 1-7 by hand' 'the real data folder was not made available'
        Complete-Check
        return
    }
    $log = Join-Path $RealData 'dialshift.log'
    [void](Start-DialShift)

    Write-Title 'Step 1: the tray'
    Invoke-Step 'Left-click the DialShift tray icon (in the notification area, maybe under the ^ arrow). Right-click it and try each item except Quit DialShift: Open, Play/Pause, Next station, Stations, Follow schedule (compare its check with the Schedule page), Volume +10 and -10. Then hover over the icon.'
    Read-Step '1a. Left-click opens the window'
    Read-Step '1b. The right-click menu has every item, and each item works'
    Read-Step "1c. The tooltip shows 'DialShift $Dot <station>' or 'DialShift $Dot Paused'"

    Write-Title 'Step 2: audio'
    Invoke-Step "Press Listen on a station and hear it. Press Skip, then the tray menu's Next station. Set the volume to 0 and back. Press Pause."
    Read-Step '2a. Listen, Skip and Next station each switch audibly, and exactly one stream is audible'
    Read-Step '2b. Nothing is heard at volume 0'
    Read-Step '2c. Nothing is heard after Pause'

    Write-Title 'Step 3: the window'
    Invoke-Step 'While a station plays: close the window (X) and reopen it from the tray; minimize it and reopen it; press "Hide to tray". Audio must continue each time.'
    Read-Step '3a. Close, minimize and Hide to tray hide the window while audio continues'
    Request-Quit
    Write-Host 'The kit now starts DialShift with --tray. Watch the screen: no window may flash up.'
    [void](Start-DialShift -Arguments '--tray')
    Start-Sleep -Seconds 3
    Read-Step '3b. --tray starts with no window flash'
    Invoke-Step 'Open the window from the tray, go to Settings, turn on "Start in the tray when opened normally", then quit from the tray.'
    [void](Wait-DialShiftExit 15)
    Write-Host 'The kit starts DialShift normally. Watch for a window flash.'
    [void](Start-DialShift)
    Start-Sleep -Seconds 3
    Read-Step '3c. With "Start in the tray" on, a normal start shows no window flash'
    Invoke-Step 'Open the window from the tray and turn "Start in the tray when opened normally" off again.'

    Write-Title 'Step 4: dialogs and keyboard'
    Invoke-Step 'With the window visible, delete a schedule slot (add one first if there is none): try Enter and Escape in the confirm dialog. In Settings click "Open settings folder". Press Tab through every page.'
    Read-Step '4a. The confirm dialog is owned by the window, and Enter and Escape work'
    Read-Step '4b. "Open settings folder" opens Explorer at %LOCALAPPDATA%\DialShift'
    Read-Step '4c. Tab shows a visible focus ring on every control'

    Write-Title 'Step 5: the recovery and failure dialogs'
    Write-Host 'The kit edits the test data folder (your own data is moved aside).'
    Request-Quit
    Write-Utf8 (Join-Path $RealData 'settings.json') '{'
    [void](Start-DialShift)
    Invoke-Step "A 'DialShift $Dot Settings recovered' dialog should appear. Read it and press OK."
    $copies = @(Get-ChildItem -LiteralPath $RealData -Filter 'settings.json.unreadable-*' -ErrorAction SilentlyContinue)
    Add-AutoStep '5a. A settings.json.unreadable-* copy exists' ($copies.Count -ge 1) ((@($copies | ForEach-Object { $_.Name })) -join ', ')
    Add-AutoStep '5b. The log has settings.recovered' ((Get-EventCount (Read-Log $log) 'settings.recovered') -ge 1)
    Read-Step '5c. Exactly one "Settings recovered" dialog appeared, naming the settings.json.unreadable-* copy'
    Request-Quit
    $lock = Join-Path $RealData '.single-instance.lock'
    if (Test-Path -LiteralPath $lock -PathType Leaf) { Remove-Item -LiteralPath $lock -Force }
    New-Item -ItemType Directory -Path $lock | Out-Null
    Write-Host "The kit starts DialShift and waits for it to exit. Press OK in the `"DialShift couldn't start`" dialog."
    $code = (Start-Process -FilePath $script:Exe -Wait -PassThru).ExitCode
    Remove-Item -LiteralPath $lock -Recurse -Force
    Add-AutoStep '5d. The lock failure exits 1' ($code -eq 1) "exit code $code"
    Read-Step "5e. The 'DialShift couldn't start' dialog says it couldn't create its lock file"
    Test-LegacyInstance $log

    Write-Title 'Step 6: look'
    if ((Get-DialShiftCount) -eq 0) { [void](Start-DialShift) }
    Invoke-Step 'Open the window. Look at the three pages (Stations, Schedule, Settings), both editors (add a station, add a slot) and the compact size (resize the window to its smallest, 780x650).'
    Save-Screenshot 'window' 'the DialShift window'
    Read-Step '6. No clipped or overlapping text (QG-03)'

    Write-Title 'Step 7: Quit from the tray'
    Invoke-Step 'Right-click the tray icon and choose Quit DialShift.'
    [void](Wait-DialShiftExit 15)
    Test-CleanExit $log '7.'
    [void](Save-LogExcerpt 'log' $log 0)
    Complete-Check
    Request-Restore
}
function Test-AudioOverride { [bool][Environment]::GetEnvironmentVariable('DIALSHIFT_AUDIO_OUTPUT', 'User') }

# True while a process holds the older WPF app's mutex. Only opens it, as DialShift's own check does; never owns it.
function Test-LegacyMutex {
    $mutex = $null
    try {
        if ([System.Threading.Mutex]::TryOpenExisting($LegacyMutexName, [ref]$mutex)) { $mutex.Dispose(); return $true }
        return $false
    } catch [System.UnauthorizedAccessException] { return $true }   # it exists, but its security denies us
}
# Asks for the older WPF DialShift.exe, which only the tester can have. Returns its full path, or '' to skip.
function Read-LegacyExe {
    $saved = Get-State 'legacy_exe'
    $hint = ''
    if ($saved) { $hint = " [$saved]" }
    while ($true) {
        $answer = (Read-Line "Path to the older DialShift.exe$hint (s = skip)").Trim('"')
        if ($answer -match '^[Ss]$') { return '' }
        if (-not $answer) { $answer = $saved }
        if (-not $answer) { return '' }
        if ((Test-Path -LiteralPath $answer -PathType Leaf) -and ((Resolve-Path -LiteralPath $answer).ProviderPath -ne $script:Exe)) {
            $answer = (Resolve-Path -LiteralPath $answer).ProviderPath
            Set-State 'legacy_exe' $answer
            return $answer
        }
        Write-Host 'Not found, or it is the build under test. Try again.'
    }
}
# NC-01 step 5, the older-app part (SW-S2, D55): with the WPF app running, the new app refuses to start and exits 1.
function Test-LegacyInstance([string]$Log) {
    Write-Title 'Step 5, continued: an older DialShift still running (SW-S2)'
    Write-Host 'This needs the older WPF DialShift: upstream v0.2.0 from github.com/tsiger/DialShift/releases, or the WPF'
    Write-Host 'build at tag legacy-last-known-good. Extract it to its own folder (not the folder under test).'
    $legacyExe = Read-LegacyExe
    if (-not $legacyExe) { Add-Step 'manual' 'SKIP' '5f. An older DialShift still running' 'no older WPF DialShift available'; return }
    Add-Step 'auto' 'INFO' '5f. Older app' ('{0}, version {1}' -f (Hide-Home $legacyExe), (Get-Item -LiteralPath $legacyExe).VersionInfo.ProductVersion)
    Request-Quit
    Assert-NoDialShift
    Write-Host 'The kit starts the older DialShift. If SmartScreen asks, choose More info > Run anyway.'
    $legacy = Start-Process -FilePath $legacyExe -PassThru
    $held = $false
    for ($n = 0; $n -lt 60 -and -not $held; $n++) { Start-Sleep -Seconds 1; $held = Test-LegacyMutex }
    if (-not $held) {
        Add-Step 'auto' 'SKIP' "5g. The older app holds the mutex $LegacyMutexName" 'not held after 60 s: not the WPF app, or it did not start'
        Invoke-Step 'Quit the older DialShift if it is running (its tray icon).'
        return
    }
    Add-AutoStep "5g. The older app holds the mutex $LegacyMutexName" $true "pid $($legacy.Id)"
    Invoke-Step 'In the older DialShift start a station, so you can hear which app plays.'
    $mark = Get-LogMark $Log
    Write-Host "The kit starts the new DialShift.exe and waits for it to exit. Press OK in the `"DialShift couldn't start`" dialog."
    $code = (Start-Process -FilePath $script:Exe -Wait -PassThru).ExitCode
    Add-AutoStep '5h. The new DialShift exits 1' ($code -eq 1) "exit code $code"
    $lines = @(Read-Log $Log $mark | Where-Object { $_.Event -eq 'app.legacy_instance_running' })
    $msg = ''
    if ($lines.Count) { $msg = $lines[-1].Msg }
    Add-AutoStep '5i. The log has app.legacy_instance_running naming the mutex' ($msg.Contains('DialShift.App')) $msg
    $legacy.Refresh()
    $others = @(Get-Process -Name DialShift -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $legacy.Id }).Count
    Add-AutoStep '5j. The older app is still running, and no new DialShift is left' ((-not $legacy.HasExited) -and $others -eq 0) "other DialShift processes: $others"
    Read-Step "5k. The dialog says 'DialShift couldn't start. An older DialShift is still running. Quit it from its tray icon, then open DialShift again.', and only the older app plays"
    Wait-Enter 'Quit the older DialShift from its tray icon, then press Enter'
    if (-not $legacy.WaitForExit(15000)) {
        Write-Warn 'The older DialShift is still running.'
        if (Confirm-Choice 'Stop it with Stop-Process (not a clean quit; note it)?' $false) { Stop-Process -Id $legacy.Id -Force; [void]$legacy.WaitForExit(10000) }
    }
    Add-AutoStep '5l. The older app has quit' $legacy.HasExited
    $mark = Get-LogMark $Log
    [void](Start-DialShift)
    Start-Sleep -Seconds 3
    $entries = @(Read-Log $Log $mark)
    $starts = Get-EventCount $entries 'app.start'
    $refusals = Get-EventCount $entries 'app.legacy_instance_running'
    Add-AutoStep '5m. With the older app gone, the new DialShift starts normally (app.start, no app.legacy_instance_running)' ($starts -ge 1 -and $refusals -eq 0 -and (Get-DialShiftCount) -eq 1) "app.start: $starts; app.legacy_instance_running: $refusals; $(Get-DialShiftCount) running"
}

# ---------------------------------------------------------------------------------------------------------------------
# NC-06: second-launch foreground (real data folder)
# ---------------------------------------------------------------------------------------------------------------------
function Test-NC06 {
    Start-Check 'NC-06'
    Show-Procedure 'NC-06'
    Resolve-App
    if (-not (Backup-RealData 'NC-06')) { return }
    $log = Join-Path $RealData 'dialshift.log'
    [void](Start-DialShift)
    Invoke-Step 'Open the window once (click the tray icon), then close it with X so DialShift is hidden in the tray.'

    Write-Title 'Launch 1: the Start menu'
    $installed = Join-Path $InstalledDir 'DialShift.exe'
    if (Test-Path -LiteralPath $installed) {
        Start-Process -FilePath notepad.exe
        Start-Sleep -Seconds 2
        $before = Get-EventCount (Read-Log $log) 'single_instance.activated' 'delivered'
        Invoke-Step 'Notepad has the focus. Open Start, type DialShift and launch it (the Start menu entry from Install.ps1 uses the same data folder).'
        Start-Sleep -Seconds 3
        Test-Activation '1.' $log $before
        Read-Step '1. From the Start menu, the window came to the foreground (not just a flashing taskbar button)'
        Invoke-Step 'Close the DialShift window with X again, and close Notepad without saving.'
    } else {
        Add-Step 'manual' 'SKIP' '1. Second launch from the Start menu' 'no Start menu entry (Install.ps1 was not run; the kit runs it only in NC-04 step 6)'
    }

    Write-Title 'Launch 2: an Explorer double-click'
    Start-Process -FilePath notepad.exe
    Start-Sleep -Seconds 2
    $before = Get-EventCount (Read-Log $log) 'single_instance.activated' 'delivered'
    Start-Process -FilePath explorer.exe -ArgumentList ('/select,"{0}"' -f $script:Exe)
    Invoke-Step 'An Explorer window shows DialShift.exe. Click into Notepad, then double-click DialShift.exe in Explorer.'
    Start-Sleep -Seconds 3
    Test-Activation '2.' $log $before
    Read-Step '2. From Explorer, the window came to the foreground'
    Invoke-Step 'Close the DialShift window with X again, and close Notepad without saving.'

    Write-Title 'Launch 3: the exit code of a second launch'
    Start-Process -FilePath notepad.exe
    Start-Sleep -Seconds 2
    $before = Get-EventCount (Read-Log $log) 'single_instance.activated' 'delivered'
    Write-Host 'Notepad has the focus. The kit now starts a second DialShift and waits for its exit code.'
    $code = (Start-Process -FilePath $script:Exe -Wait -PassThru).ExitCode
    Add-AutoStep '3a. The second process exits 0' ($code -eq 0) "exit code $code"
    Test-Activation '3b.' $log $before
    Read-Step '3c. The window came to the foreground over Notepad'
    Invoke-Step 'Close Notepad without saving.'
    Request-Quit
    Test-CleanExit $log 'End:'
    [void](Save-LogExcerpt 'log' $log 0)
    Complete-Check
    Request-Restore
}

# ---------------------------------------------------------------------------------------------------------------------
# NC-04: launch at sign-in and the Task Manager switch; resumes after each of three sign-ins
# ---------------------------------------------------------------------------------------------------------------------
function Test-NC04 {
    $phaseFile = Join-Path $script:Evid 'NC-04\phase'
    $phase = 0
    if (Test-Path -LiteralPath $phaseFile) { $phase = [int]([IO.File]::ReadAllText($phaseFile)).Trim() }
    if ($phase -eq 0) { Start-Check 'NC-04' } else { Start-Check 'NC-04' -Resume }
    Resolve-App
    switch ($phase) {
        0 { Start-NC04 }
        1 { Resume-NC04SignIn1 }
        2 { Resume-NC04SignIn2 }
        3 { Resume-NC04SignIn3 }
        4 { Resume-NC04SignIn4 }
    }
}
function Exit-ForSignOut([int]$Next) {   # records the sign-out time and exits; the next run continues
    Write-Utf8 (Join-Path $script:CurDir 'phase') "$Next"
    Write-Utf8 (Join-Path $script:CurDir 'signout-at') ([DateTime]::UtcNow.ToString('o', $Invariant))
    Write-Title "Sign out and back in (NC-04, sign-in $Next of 4; 3 without step 6)"
    Write-Host '1. Start > your account picture > Sign out.'
    Write-Host '2. Sign back in and wait about 20 seconds.'
    Write-Host '3. Open PowerShell and run the kit again; it continues NC-04 from here:'
    Write-Host "     powershell -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit 0
}
function Test-SignInStart([string]$Prefix, [string]$Exe = $script:Exe) {   # what launch at sign-in started
    $process = Get-Process -Name DialShift -ErrorAction SilentlyContinue | Select-Object -First 1
    Add-AutoStep "$Prefix DialShift started at sign-in" ($null -ne $process)
    if (-not $process) { return }
    $signOut = [DateTime]::Parse(([IO.File]::ReadAllText((Join-Path $script:CurDir 'signout-at'))).Trim(), $Invariant, [Globalization.DateTimeStyles]::RoundtripKind)
    Add-AutoStep "$Prefix It started after the sign-out (not a leftover process)" ($process.StartTime.ToUniversalTime() -gt $signOut) $process.StartTime.ToString('o', $Invariant)
    $commandLine = [string](Get-CimInstance Win32_Process -Filter "ProcessId=$($process.Id)").CommandLine
    $ok = $commandLine.IndexOf($Exe, [StringComparison]::OrdinalIgnoreCase) -ge 0 -and $commandLine.Contains('--tray')
    Add-AutoStep "$Prefix It runs $(Hide-Home $Exe) with --tray" $ok (Hide-Home $commandLine)
    Add-AutoStep "$Prefix Exactly one DialShift instance" ((Get-DialShiftCount) -eq 1) "$(Get-DialShiftCount) running"
}
function Test-RunValue([string]$Prefix) {
    $value = [string](Get-EntryValue $RunKey)
    Add-AutoStep "$Prefix HKCU\...\Run\DialShift is `"<exe>`" --tray for this copy" ($value -eq ('"{0}" --tray' -f $script:Exe)) (Hide-Home $value)
}

function Start-NC04 {
    Show-Procedure 'NC-04'
    if (-not (Backup-RealData 'NC-04')) { return }
    [void](Start-DialShift)
    Invoke-Step 'Step 1: open the window (click the tray icon), go to Settings and turn on "Launch DialShift in the tray when I sign in".'
    Test-RunValue '1a.'
    Add-Step 'auto' 'INFO' '1a. StartupApproved\Run\DialShift first byte' (Get-ApprovedByte)
    Save-Command 'registry-step1.txt' { Get-StartupEntries }
    Request-Quit
    Exit-ForSignOut 1
}
function Resume-NC04SignIn1 {
    Write-Host 'Welcome back. Checking what started at sign-in.'
    Start-Sleep -Seconds 5
    Test-SignInStart '1b.'
    Read-Step '1c. DialShift is in the tray and no window appeared'
    Write-Title 'Step 2: a moved copy'
    Request-Quit
    $folder = Split-Path -Parent $script:Exe
    $moved = "$folder-moved"
    Move-Item -LiteralPath $folder -Destination $moved
    $manifest = Read-Manifest
    $manifest.folderFrom = $folder
    $manifest.folderTo = $moved
    Save-Manifest $manifest
    Set-App (Join-Path $moved 'DialShift.exe') (Get-State 'source')
    Write-Host "Moved the folder to $(Hide-Home $moved)."
    Exit-ForSignOut 2
}
function Resume-NC04SignIn2 {
    Start-Sleep -Seconds 5
    Add-AutoStep '2a. Nothing started at sign-in (the entry points at the old folder)' ((Get-DialShiftCount) -eq 0) "$(Get-DialShiftCount) running"
    [void](Start-DialShift)
    Invoke-Step 'Open the window (click the tray icon) and go to Settings.'
    Read-Step '2b. The checkbox shows off with "Launch at sign-in points to an older copy of DialShift..."'
    Invoke-Step 'Turn the checkbox on.'
    Test-RunValue '2c. Repaired:'

    Write-Title 'Step 3: Task Manager'
    Invoke-Step 'Open Task Manager (Ctrl+Shift+Esc) > Startup apps, select DialShift and click Disable. Then in DialShift go to another page and back to Settings.'
    $byte = Get-ApprovedByte
    Add-AutoStep '3a. StartupApproved\Run\DialShift starts with 03 (disabled)' ($byte -eq '03') "first byte: $byte"
    Save-Command 'registry-step3.txt' { Get-StartupEntries }
    Read-Step "3b. The checkbox shows off with 'Turned off in Task Manager's Startup apps...'"

    Write-Title 'Step 4: turning it on in DialShift'
    Invoke-Step "Turn the checkbox on in DialShift. Then press F5 in Task Manager's Startup apps."
    $byte = Get-ApprovedByte
    Add-AutoStep '4a. StartupApproved\Run\DialShift starts with 02 (enabled)' ($byte -eq '02') "first byte: $byte"
    Test-RunValue '4b.'
    Save-Command 'registry-step4.txt' { Get-StartupEntries }
    Read-Step '4c. Task Manager shows DialShift Enabled after a refresh'
    Request-Quit
    Exit-ForSignOut 3
}
function Resume-NC04SignIn3 {
    Start-Sleep -Seconds 5
    Test-SignInStart '4d. Next sign-in:'
    Write-Title 'Step 5: turning it off'
    Invoke-Step 'Open the window (click the tray icon), go to Settings and turn the checkbox off.'
    $run = Get-EntryValue $RunKey
    $runText = 'absent'
    if ($null -ne $run) { $runText = Hide-Home $run }
    Add-AutoStep '5. Both registry values are gone' ($null -eq $run -and (Get-ApprovedByte) -eq 'absent') "Run: $runText; StartupApproved: $(Get-ApprovedByte)"
    Save-Command 'registry-step5.txt' { Get-StartupEntries }
    if (Start-NC04Install) { Exit-ForSignOut 4 }
    Complete-NC04
}
function Complete-NC04 {
    $entries = @(Save-LogExcerpt 'log' (Join-Path $RealData 'dialshift.log') 0)
    Add-Step 'auto' 'INFO' 'The startup_registration.result lines' ((@($entries | Where-Object { $_.Event -eq 'startup_registration.result' } | ForEach-Object { $_.Msg })) -join ' | ')
    $errors = @($entries | Where-Object { $_.Event -eq 'startup_registration.error' } | ForEach-Object { $_.Msg })
    Add-Step 'auto' 'INFO' 'startup_registration.error lines in the log' ("$($errors.Count)" + (@($errors | ForEach-Object { "; $_" }) -join ''))
    Request-Quit
    Remove-Item -LiteralPath (Join-Path $script:CurDir 'phase'), (Join-Path $script:CurDir 'signout-at') -Force
    Complete-Check
    Request-Restore
}
# Step 6 (SW-S5): Install.ps1 moves launch at sign-in to the installed copy. Returns $true when the next sign-in is due,
# $false when the step is skipped.
function Start-NC04Install {
    Write-Title 'Step 6: upgrade with Install.ps1'
    $folder = Split-Path -Parent $script:Exe
    $install = Join-Path $folder 'Install.ps1'
    $installedExe = Join-Path $InstalledDir 'DialShift.exe'
    $log = Join-Path $RealData 'dialshift.log'
    if (-not (Test-Path -LiteralPath $install)) { Add-Step 'manual' 'SKIP' '6. Upgrade with Install.ps1' "no Install.ps1 in $(Hide-Home $folder)"; return $false }
    if ($folder.TrimEnd('\') -ieq $InstalledDir.TrimEnd('\')) { Add-Step 'manual' 'SKIP' '6. Upgrade with Install.ps1' 'the copy under test is the installed copy; step 6 needs an extracted copy outside it'; return $false }
    Write-Host "Install.ps1 replaces $(Hide-Home $InstalledDir) and the Start menu shortcut DialShift.lnk. The kit moves an"
    Write-Host 'existing install and shortcut aside first and puts them back when it restores your files.'
    if (-not (Confirm-Choice 'Run step 6 (one more sign-out)?' $true)) { Add-Step 'manual' 'SKIP' '6. Upgrade with Install.ps1' 'not run'; return $false }
    if ((Get-DialShiftCount) -eq 0) { [void](Start-DialShift) }
    Invoke-Step 'Open the window of this extracted copy (click the tray icon), go to Settings and turn "Launch DialShift in the tray when I sign in" on again.'
    Test-RunValue '6a. Before Install.ps1:'
    Request-Quit
    Assert-NoDialShift
    Backup-Install
    Write-Host "The kit runs this folder's Install.ps1 with -NoLaunch (so it can wait for it), then starts the installed copy."
    $global:LASTEXITCODE = -1
    Save-Command 'install-ps1.txt' { & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $install -NoLaunch 2>&1 }
    $code = $global:LASTEXITCODE
    Add-AutoStep '6b. Install.ps1 finishes with exit code 0' ($code -eq 0) "exit code $code"
    $value = [string](Get-EntryValue $RunKey)
    Add-AutoStep '6c. Run\DialShift now names %LOCALAPPDATA%\Programs\DialShift\DialShift.exe with --tray' ($value -ieq ('"{0}" --tray' -f $installedExe)) (Hide-Home $value)
    Save-Command 'registry-step6.txt' { Get-StartupEntries }
    if (-not (Test-Path -LiteralPath $installedExe)) { Add-AutoStep '6d. The installed copy exists' $false (Hide-Home $installedExe); return $false }
    $mark = Get-LogMark $log
    [void](Start-DialShift -Exe $installedExe)
    Invoke-Step 'Open the installed DialShift window (click the tray icon) and go to Settings.'
    Start-Sleep -Seconds 2
    Read-Step '6d. The installed copy shows the checkbox on, with no message under it'
    $check = @(Read-Log $log $mark | Where-Object { $_.Event -eq 'startup_registration.result' -and $_.Msg.StartsWith('check ') }) | Select-Object -Last 1
    $msg = ''
    if ($check) { $msg = $check.Msg }
    Add-AutoStep '6e. startup_registration.result: check enabled=True with no diagnostic' ($msg -match 'enabled=True' -and $msg -match 'diagnostic=False') $msg
    Request-Quit
    return $true
}
function Resume-NC04SignIn4 {
    Start-Sleep -Seconds 5
    Test-SignInStart '6f. Next sign-in:' (Join-Path $InstalledDir 'DialShift.exe')
    Complete-NC04
}

# ---------------------------------------------------------------------------------------------------------------------
# NC-02: SystemEvents and a real sleep (isolated data folder)
# ---------------------------------------------------------------------------------------------------------------------
function Test-NC02 {
    Start-Check 'NC-02'
    Show-Procedure 'NC-02'
    Resolve-App
    Assert-NoDialShift
    $data = New-DataDir 'NC-02'
    $log = Join-Path $data 'dialshift.log'
    [void](Start-DialShift -DataDir $data)
    Start-Sleep -Seconds 5
    Add-AutoStep '0. power_events.started at startup' ((Get-EventCount (Read-Log $log) 'power_events.started') -ge 1)
    Save-Command 'sleep-states.txt' { powercfg /a }

    Write-Title 'Step 1: sleep while playing'
    Invoke-Step 'Open the window (click the tray icon) and press Listen on Groove Salad. Wait until you hear it.' 'Press Enter when it plays'
    $mark = Get-LogMark $log
    $since = Get-Date
    Invoke-SleepCycle
    $entries = @(Save-LogExcerpt 'wake-playing' $log $mark)
    Test-Wake $entries 'playing' '1.'
    Add-Step 'auto' 'INFO' '1. Wake-to-Resume delay (HZ-04)' (Measure-ResumeDelay $entries $since)
    Read-Step '1. Audio came back by itself after the wake'

    Write-Title 'Step 2: sleep while paused inside a slot'
    Invoke-Step 'On the Schedule page turn on Follow my schedule and add a slot that started earlier today, so a slot is active and playing. Then press Pause.'
    $mark = Get-LogMark $log
    Invoke-SleepCycle
    Test-Wake @(Save-LogExcerpt 'wake-paused' $log $mark) 'paused' '2.'
    Read-Step '2. DialShift stayed paused and silent after the wake'

    Write-Title 'Step 3: a slot in another time zone starts during sleep (TZ-12)'
    $minutes = Read-Line 'Minutes from now for the slot to start (time to add it, then sleep) [5]'
    if ($minutes -notmatch '^\d+$') { $minutes = '5' }
    $slot = Get-SlotSuggestion ([int]$minutes)
    Invoke-Step "Press Listen on a station and let it play. On the Schedule page add a slot in time zone $($slot.Zone) at $($slot.Time) on $($slot.Day) ($($slot.Local.ToString('HH:mm', $Invariant)) your time) with a different station, and keep Follow my schedule on."
    $mark = Get-LogMark $log
    Invoke-SleepCycle -WakeAfter $slot.Local
    $entries = @(Save-LogExcerpt 'wake-zoned-slot' $log $mark)
    $fired = @($entries | Where-Object { $_.Event -eq 'schedule.fired' -and ($_.Msg.Contains($slot.Zone) -or $_.Msg.Contains($slot.WindowsZone)) })
    Add-AutoStep "3a. The $($slot.Zone) slot fired exactly once (schedule.fired names the zone)" ($fired.Count -eq 1) ((@($entries | Where-Object { $_.Event -eq 'schedule.fired' } | ForEach-Object { $_.Msg })) -join '; ')
    Read-Step "3b. The slot's station played after the wake, once"

    Write-Title 'Step 4: the tick gap alone'
    $dev = (Read-Line 'Path to a dev build DialShift.exe with WindowsPowerEvents.Start disabled (the platform lane provides it; Enter to skip)').Trim('"')
    if ($dev -and (Test-Path -LiteralPath $dev -PathType Leaf)) {
        Request-Quit
        $mark = Get-LogMark $log
        [void](Start-DialShift -DataDir $data -Exe $dev)
        Start-Sleep -Seconds 5
        $startup = @(Read-Log $log $mark)
        Add-AutoStep '4a. The dev build has no power events (no power_events.started, or power_events.unavailable)' (((Get-EventCount $startup 'power_events.started') -eq 0) -or ((Get-EventCount $startup 'power_events.unavailable') -ge 1))
        Invoke-Step 'Open the window and press Listen on Groove Salad. Wait until you hear it.' 'Press Enter when it plays'
        $mark = Get-LogMark $log
        Invoke-SleepCycle
        Test-Wake @(Save-LogExcerpt 'wake-tick-gap' $log $mark) 'tick_gap' '4b.'
        Read-Step '4c. Audio came back by itself after the wake'
    } else {
        Add-Step 'manual' 'SKIP' '4. Tick gap alone' 'no dev build with power events disabled'
    }

    $thread = Read-Line 'Which thread did SystemEvents Resumed arrive on (D49 expects the UI thread; SR-02)? From a dev build log line or a debugger; Enter to skip'
    if ($thread) { Add-Step 'manual' 'INFO' 'Thread of Resumed (D49, SR-02)' $thread }
    Invoke-Step 'Quit DialShift from the tray menu (right-click the tray icon > Quit DialShift).' 'Press Enter right after clicking Quit'
    Add-AutoStep 'Quit completes within 15 s (no SystemEvents hang)' (Wait-DialShiftExit 15)
    Test-CleanExit $log 'End:'
    Complete-Check
}

# ---------------------------------------------------------------------------------------------------------------------
# NC-03: LibVLC real playback, the corpus and the retry timings (isolated data folders)
# ---------------------------------------------------------------------------------------------------------------------
function Test-NC03 {
    Start-Check 'NC-03'
    Show-Procedure 'NC-03'
    Resolve-App
    if (-not (Test-Precondition 'DIALSHIFT_AUDIO_OUTPUT is not set for your account (real audio)' (-not (Test-AudioOverride)))) { return }
    Assert-NoDialShift
    $server = Start-HangServer
    try {
        $data = New-DataDir 'NC-03'
        $list = @($Corpus) + @(New-CorpusEntry 'T14' ('http://127.0.0.1:{0}/hang' -f $server.Port) 'server accepts and never answers (25 s watchdog)' 'Stalled')
        Write-CorpusSettings $data $list
        $log = Join-Path $data 'dialshift.log'
        [void](Start-DialShift -DataDir $data)
        Invoke-Step 'Open the window (click the tray icon) and go to the Stations page. It lists the corpus stations C1-C15, T1-T9 and T14. Keep the speakers on.'
        Invoke-Corpus $log $list
    } finally { Stop-HangServer $server }

    Write-Title 'Titles, mute and Stop'
    Invoke-Step "Play C9 (http://, Shoutcast v2) and C6 (http://) and watch the title line; then play C1 (https://). If you know a Shoutcast v1 http:// station (it answers 'ICY 200 OK'), add it and play it too."
    Read-Step 'Titles: a Shoutcast v1 http:// station shows song titles; https:// shows the station description (D26). Note what each station showed'
    Invoke-Step 'While a station plays, set the volume to 0, then back up. Then press Pause.'
    Read-Step 'Nothing is heard at volume 0'
    Read-Step 'No audio after Stop (Pause)'
    Request-Quit

    Write-Title 'The retry and fallback timings with the real engine'
    Invoke-RetryScenario
    Write-Host ''
    Write-Host 'Optional, the matrix hosts-file variant (needs administrator rights): add "127.0.0.1 ice1.somafm.com" to'
    Write-Host 'C:\Windows\System32\drivers\etc\hosts, play C1, watch the retries, then remove the line again.'
    if (Confirm-Choice 'Did you run the hosts-file variant?' $false) { Read-Step 'Hosts-file variant: the retries and fallback with a public station made unreachable follow section 5.1' }
    Complete-Check
}

# ---------------------------------------------------------------------------------------------------------------------
# NC-15: LibVLC fast-switch stress through UI Automation (isolated data folder)
# ---------------------------------------------------------------------------------------------------------------------
function Test-NC15 {
    Start-Check 'NC-15'
    Show-Procedure 'NC-15'
    Resolve-App
    if (-not (Test-Precondition 'DIALSHIFT_AUDIO_OUTPUT is not set for your account (real audio)' (-not (Test-AudioOverride)))) { return }
    Assert-NoDialShift
    $data = New-DataDir 'NC-15'
    Write-CorpusSettings $data $StressStations
    $log = Join-Path $data 'dialshift.log'
    $process = Start-DialShift -DataDir $data
    Invoke-Step 'Open the window (click the tray icon), press Listen on the first station and wait until it plays. Keep the window open and not minimized, and keep listening: at most one station may be audible at any moment.'
    $manual = $true
    try {
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
        $manual = -not (Find-UiButton $process.Id 'Next station')
    } catch { $manual = $true }
    if ($manual) { Write-Warn 'UI Automation could not find the "Next station" button (is the window open?). You click it by hand; the kit still checks each run.' }
    Add-Step 'auto' 'INFO' 'Driver' $(if ($manual) { 'manual clicks' } else { 'UI Automation InvokePattern' })
    $startMark = Get-LogMark $log
    $table = @("run`tpresses`tworkingSetMB`tlastState")
    $sets = @()
    for ($run = 1; $run -le 7; $run++) {
        $mark = Get-LogMark $log
        $paused = $false
        if ($manual) {
            $pauseHint = ''
            if ($run -eq 4) { $pauseHint = ' In this run, press Pause right after your last click, while the station is still connecting.' }
            Invoke-Step "Run $run of 7: click Next station about 160 times, quickly and irregularly.$pauseHint"
            if ($run -eq 4) { $paused = Confirm-Choice 'Did you press Pause while it was connecting?' $true }
            $presses = @(Read-Log $log $mark | Where-Object { $_.Event -eq 'playback.state' -and $_.Msg -match '> (Connecting|Reconnecting);' }).Count
        } else {
            Write-Host "Run $run of 7: 160 presses of Next station with random 0-300 ms gaps..."
            $button = Find-UiButton $process.Id 'Next station'
            $presses = 0
            for ($i = 1; $i -le 160 -and $button; $i++) {
                try { Invoke-UiButton $button; $presses++ }
                catch { $button = Find-UiButton $process.Id 'Next station' }
                Start-Sleep -Milliseconds (Get-Random -Minimum 0 -Maximum 301)
            }
            if ($run -eq 4) {
                $pause = Find-UiButton $process.Id 'Pause'
                if ($pause) { Invoke-UiButton $pause; $paused = $true }
            }
        }
        Start-Sleep -Seconds 5
        $process.Refresh()
        $alive = (-not $process.HasExited) -and $process.Responding
        Add-AutoStep "Run $($run): no crash or hang" $alive "$presses presses"
        if (-not $alive) { break }
        $state = Wait-LastState $log $mark $paused
        $expectation = 'the last station plays'
        if ($paused) { $expectation = 'it stays paused after Pause' }
        Add-AutoStep "Run $($run): $expectation" $state.Ok $state.Msg
        $process.Refresh()
        $workingSet = [math]::Round($process.WorkingSet64 / 1MB, 1)
        $sets += $workingSet
        $table += (@($run, $presses, $workingSet.ToString($Invariant), $state.Msg)) -join "`t"
        Write-Host "   working set: $workingSet MB"
        if ($paused) {
            $play = $null
            if (-not $manual) { $play = Find-UiButton $process.Id 'Play' }
            if ($play) { Invoke-UiButton $play } else { Invoke-Step 'Press Play so the next run starts from a playing station.' }
            Start-Sleep -Seconds 5
        }
    }
    Write-Utf8 (Join-Path $script:CurDir 'stress.tsv') (($table -join "`n") + "`n")
    Add-Artifact 'NC-15/stress.tsv'
    if ($sets.Count -ge 2) {
        $largest = ($sets | Select-Object -Skip 1 | ForEach-Object { [math]::Abs($_ - $sets[0]) } | Measure-Object -Maximum).Maximum
        Add-AutoStep 'The working set after runs 2-7 stays within +/-20 MB of run 1' ($largest -le 20) ('after run 1: {0} MB; largest difference: {1} MB; all: {2}' -f $sets[0], $largest, ($sets -join ', '))
    }
    $errors = Get-EventCount (Read-Log $log $startMark) 'playback.engine_error'
    Add-AutoStep 'The log has no playback.engine_error' ($errors -eq 0) "$errors line(s)"
    Read-Step 'At most one station was audible at any moment'
    Read-Step 'After each run the last station was audible (or silent after the Pause run)'
    Request-Quit
    Test-CleanExit $log 'End:'
    [void](Save-LogExcerpt 'log' $log $startMark)
    Complete-Check
}

# ---------------------------------------------------------------------------------------------------------------------
# NC-05: SmartScreen on a clean PC (a), Authenticode when the build is signed (b)
# ---------------------------------------------------------------------------------------------------------------------
function Get-ZoneId([string]$Path) {
    $line = @(Get-Content -LiteralPath $Path -Stream Zone.Identifier -ErrorAction SilentlyContinue) | Where-Object { $_ -match '^ZoneId=' } | Select-Object -First 1
    if ($line) { return ($line -replace '^ZoneId=', '').Trim() }
    return 'none'
}
function Test-NC05 {
    Start-Check 'NC-05'
    Show-Procedure 'NC-05'
    if (-not (Test-Precondition 'A clean Windows PC where DialShift has never run' (Confirm-Choice 'Is this a clean Windows PC where DialShift has never run?' $false))) { return }
    Write-Host 'Download DialShift-win-x64 from the CI run page (Actions > the run > Artifacts) with a browser. GitHub wraps artifacts in a zip: extract the outer zip in Explorer and use the DialShift-win-x64.zip inside.'
    $default = Join-Path $env:USERPROFILE 'Downloads\DialShift-win-x64.zip'
    $zipPath = (Read-Line "Path of DialShift-win-x64.zip [$default]").Trim('"')
    if (-not $zipPath) { $zipPath = $default }
    $zone = 'none'
    if (Test-Path -LiteralPath $zipPath) { $zone = Get-ZoneId $zipPath }
    Add-AutoStep '(a) 1. The zip carries the Mark of the Web (Zone.Identifier ZoneId=3)' ($zone -eq '3') "ZoneId: $zone"
    Invoke-Step 'In Explorer right-click DialShift-win-x64.zip > Extract All..., and extract it.'
    $folderDefault = Join-Path (Split-Path -Parent $zipPath) 'DialShift-win-x64'
    $folder = (Read-Line "Extracted folder [$folderDefault]").Trim('"')
    if (-not $folder) { $folder = $folderDefault }
    Set-App $folder "browser download, Explorer extraction"
    $zone = Get-ZoneId $script:Exe
    Add-AutoStep '(a) 2. The extracted DialShift.exe carries the Mark of the Web' ($zone -eq '3') "ZoneId: $zone"
    if (-not (Backup-RealData 'NC-05')) { Complete-Check; return }
    Start-Process -FilePath explorer.exe -ArgumentList ('/select,"{0}"' -f $script:Exe)
    Invoke-Step 'Double-click DialShift.exe in the Explorer window. If "Windows protected your PC" appears, click More info, then Run anyway.'
    $warned = Confirm-Choice '   Did "Windows protected your PC" appear?' $true
    $warnedText = 'no'
    if ($warned) { $warnedText = 'yes' }
    Add-Step 'manual' 'INFO' "(a) 3. SmartScreen showed 'Windows protected your PC': $warnedText"
    if ($warned) {
        $ran = Confirm-Choice '   Did More info > Run anyway start DialShift?' $true
        Add-AutoStep '(a) 4. More info > Run anyway starts the app' $ran
    }
    Wait-DialShiftRunning 30
    Add-AutoStep '(a) 5. DialShift is running' ((Get-DialShiftCount) -ge 1)
    $signature = Get-AuthenticodeSignature -LiteralPath $script:Exe
    Add-Step 'auto' 'INFO' '(a) Authenticode status of this build' ('{0}: {1}' -f $signature.Status, $signature.StatusMessage)
    if ($signature.Status -eq 'Valid') {
        Add-AutoStep '(b) Get-AuthenticodeSignature is Valid with the publisher' $true $signature.SignerCertificate.Subject
        Add-AutoStep '(b) The signature has an RFC 3161 timestamp' ($null -ne $signature.TimeStamperCertificate)
        if (Get-Command signtool.exe -ErrorAction SilentlyContinue) {
            Save-Command 'signtool-verify.txt' { signtool.exe verify /pa /v $script:Exe }
            Add-AutoStep '(b) signtool verify /pa /v succeeds' ($LASTEXITCODE -eq 0)
        } else {
            Add-Step 'auto' 'SKIP' '(b) signtool verify /pa /v' 'signtool.exe is not installed (Windows SDK)'
        }
        Read-Step '(b) The downloaded zip launches with the publisher shown and no "unknown publisher" block'
    } else {
        Add-Step 'auto' 'SKIP' '(b) Authenticode' 'this build is not signed; (b) needs a code-signing certificate (release only, D7)'
    }
    Request-Quit
    Complete-Check
    Request-Restore
}

# ---------------------------------------------------------------------------------------------------------------------
# Menu and entry point
# ---------------------------------------------------------------------------------------------------------------------
function Invoke-Check([string]$Id) {
    if ((Test-Path -LiteralPath (Get-RecordPath $Id)) -and -not (Test-InProgress $Id)) {
        if (-not (Confirm-Choice "$Id is already recorded as $(Get-RecordField $Id 'result'). Run it again and replace the record?" $false)) { return }
    }
    switch ($Id) {
        'NC-01' { Test-NC01 }
        'NC-02' { Test-NC02 }
        'NC-03' { Test-NC03 }
        'NC-04' { Test-NC04 }
        'NC-05' { Test-NC05 }
        'NC-06' { Test-NC06 }
        'NC-15' { Test-NC15 }
    }
}
function Invoke-Pending {
    foreach ($id in $CheckOrder) {
        if ((Test-Path -LiteralPath (Get-RecordPath $id)) -and -not (Test-InProgress $id)) { continue }
        $answer = Read-Line "Run $id ($($Titles[$id])) now? [Y]es, [n]o (next check), [q] back to the menu"
        if ($answer -match '^[Qq]') { return }
        if ($answer -match '^[Nn]') { continue }
        Invoke-Check $id
    }
}
function Show-Menu {
    while ($true) {
        Write-Title 'Checks, in the suggested order (docs/open-items.md section 3)'
        for ($i = 0; $i -lt $CheckOrder.Count; $i++) {
            $id = $CheckOrder[$i]
            Write-Host ('  {0}) {1,-6} {2,-54} {3}' -f ($i + 1), $id, $Titles[$id], (Get-CheckStatus $id))
        }
        Write-Host '  a) run every pending check in this order'
        if (Test-Path -LiteralPath $ManifestPath) { Write-Host "  r) restore your real DialShift files now (saved by $((Read-Manifest).owner))" }
        Write-Host '  f) finish: write summary.md and summary.json and zip the evidence'
        Write-Host '  q) quit (run the kit again to resume)'
        $choice = Read-Line 'Choice'
        if ($choice -match '^\d+$' -and [int]$choice -ge 1 -and [int]$choice -le $CheckOrder.Count) { Invoke-Check $CheckOrder[[int]$choice - 1] }
        elseif ($choice -match '^[Aa]$') { Invoke-Pending }
        elseif ($choice -match '^[Rr]$') { Restore-RealData }
        elseif ($choice -match '^[Ff]$') { Complete-Bundle }
        elseif ($choice -match '^[Qq]$') { Write-Summary; Write-Host "Evidence so far: $(Hide-Home $script:Evid)"; return }
        else { Write-Host 'Unknown choice.' }
    }
}

function Invoke-Main {
    Write-Title "DialShift native-check kit for Windows (v$KitVersion)"
    Write-Host 'Runs the Windows checks of docs/acceptance-matrix.md section 9 and records them in one evidence bundle to send back as a zip.'
    Write-Host "Extracted apps, isolated data folders and backups live in $(Hide-Home $WorkRoot)."
    Initialize-Evidence
    Write-MachineTxt
    if (Test-InProgress 'NC-04') {
        if (Confirm-Choice "NC-04 is waiting for you after sign-in $(([IO.File]::ReadAllText((Join-Path $script:Evid 'NC-04\phase'))).Trim()). Continue it now?" $true) { Invoke-Check 'NC-04' }
    } elseif (Test-Path -LiteralPath $ManifestPath) {
        Write-Warn "your real DialShift files are still saved aside by an interrupted $((Read-Manifest).owner)."
        Request-Restore
    }
    Show-Menu
}

# The session's DialShift variables would leak into every launch; they are set aside for this run and put back after.
$savedVariables = @{}
foreach ($name in 'DIALSHIFT_DATA_DIR', 'DIALSHIFT_AUDIO_OUTPUT') {
    $value = [Environment]::GetEnvironmentVariable($name, 'Process')
    if ($value) {
        Write-Warn "ignoring $name from this PowerShell session for this run."
        $savedVariables[$name] = $value
        [Environment]::SetEnvironmentVariable($name, $null, 'Process')
    }
}
Push-Location $env:USERPROFILE   # so moving the folder under test (NC-04) is never blocked by this shell
try {
    Invoke-Main
} finally {
    Pop-Location
    foreach ($name in $savedVariables.Keys) { [Environment]::SetEnvironmentVariable($name, $savedVariables[$name], 'Process') }
    Remove-WakeTask
    if ($script:Evid -and (Test-Path -LiteralPath $ManifestPath) -and -not (Test-InProgress (Read-Manifest).owner)) {
        Write-Host 'Your real DialShift files are still saved aside. Run the kit again and choose r to restore them.'
    }
}
