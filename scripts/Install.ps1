# Per-user install of the extracted DialShift win-x64 release; no administrator rights needed.
# Copies the app to %LOCALAPPDATA%\Programs\DialShift and adds a Start menu shortcut.
# Settings live separately in %LOCALAPPDATA%\DialShift and are never touched here.
# The new build is copied into a staging folder beside the install first and then swapped in by renames (SW-N4, D56),
# so a failed copy or a locked file leaves the previous install as it was.
param([switch]$NoLaunch)
$ErrorActionPreference = 'Stop'

# "Run with PowerShell" closes its window as soon as the script ends, so an interactive run waits for Enter, after a
# success too: the result can carry a warning that names a folder to delete. -NonInteractive (CI, the native-check kit)
# and redirected input never wait.
function Wait-BeforeClose {
    $nonInteractive = @([Environment]::GetCommandLineArgs() | Where-Object { $_ -match '^(-{1,2}|/)non' }).Count -gt 0
    if ([Environment]::UserInteractive -and -not $nonInteractive -and -not [Console]::IsInputRedirected) {
        [void](Read-Host 'Press Enter to close')
    }
}

# Antivirus and indexer handles on a freshly copied folder are usually brief: try a rename for about 4 seconds.
function Rename-WithRetry([string]$Path, [string]$NewName) {
    for ($attempt = 1; ; $attempt++) {
        try { Rename-Item -LiteralPath $Path -NewName $NewName; return }
        catch { if ($attempt -ge 10) { throw }; Start-Sleep -Milliseconds 400 }
    }
}

try {
    $source = $PSScriptRoot
    if (-not (Test-Path -LiteralPath (Join-Path $source 'DialShift.exe'))) { throw 'Run Install.ps1 from the extracted release folder.' }
    $destination = Join-Path $env:LOCALAPPDATA 'Programs\DialShift'
    $executable = Join-Path $destination 'DialShift.exe'
    # Full paths with one trailing separator, compared case-insensitively, so C:\x\DialShift2 is not inside
    # C:\x\DialShift. The comparison is textual: 8.3 names, junctions and subst drives are not resolved (D56).
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
    # One install at a time: a second run (a double-click) must not clean up or swap while this one is between renames.
    # Held until the shortcut and Run value are written, and released before any wait for Enter.
    $lock = [Threading.Mutex]::new($false, 'Local\DialShift.Install')
    $held = $false
    try {
        try { $held = $lock.WaitOne(5000) }
        catch {
            # An install that ended without releasing the lock leaves it abandoned; the wait still acquired it.
            if ($_.Exception.GetBaseException() -is [Threading.AbandonedMutexException]) { $held = $true } else { throw }
        }
        if (-not $held) { throw 'Another DialShift install is running. Wait for it to finish, then run Install.ps1 again.' }
        # Stage beside the install, in the same folder, so the swap below is two renames on one volume.
        $parent = Split-Path -Parent $destination
        # Best effort: remove what an earlier run left behind, only under the exact names this script generates and never
        # through a junction or symbolic link. A DialShift.old-* folder is kept while there is no install, because it is
        # then the only copy of the previous install.
        $installed = Test-Path -LiteralPath $destination
        Get-ChildItem -LiteralPath $parent -Directory -Force -ErrorAction SilentlyContinue |
            Where-Object {
                -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -and
                $_.Name -cmatch '^DialShift\.(new|old)-[0-9a-f]{8}$' -and ($installed -or $_.Name -clike 'DialShift.new-*')
            } |
            ForEach-Object {
                Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
                if (Test-Path -LiteralPath $_.FullName) { Write-Host "Could not remove $($_.FullName), left by an earlier install." }
                else { Write-Host "Removed $($_.FullName), left by an earlier install." }
            }
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
        try {
            if ($installed) { Rename-WithRetry $destination (Split-Path -Leaf $previous) }
            Rename-WithRetry $staging (Split-Path -Leaf $destination)
        } catch {
            $reason = $_.Exception.Message
            Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
            if ($installed -and -not (Test-Path -LiteralPath $destination)) {
                try { Rename-WithRetry $previous (Split-Path -Leaf $destination) }
                catch { throw "Could not replace $destination ($reason), and could not move the previous install back. Rename $previous to DialShift to restore it." }
            }
            throw "Could not move the new build into $destination ($reason). Any previous install is unchanged; quit programs using files in it and run Install.ps1 again."
        }
        # The new build is in place; an old copy that cannot be deleted (a file still locked) is only a warning.
        if ($installed) {
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
    } finally {
        if ($held) { $lock.ReleaseMutex() }
        $lock.Dispose()
    }
    Write-Host "Installed DialShift to $destination. Find it in your Start menu."
    if (-not $NoLaunch) { Start-Process -FilePath $executable -WindowStyle Hidden }
} catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    Wait-BeforeClose
    exit 1
}
Wait-BeforeClose
