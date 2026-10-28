param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [Parameter(Mandatory = $true)][string]$DestinationPath,
    [switch]$EnableAnimationStartup,
    [string]$BackupRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'publish\backups')
)
$ErrorActionPreference = 'Stop'

# Update the installed copy and its login entry together. Never stop Explorer,
# DWM, or another application to replace a locked component.
$package = (Resolve-Path -LiteralPath $PackagePath).ProviderPath.TrimEnd('\')
$destination = [IO.Path]::GetFullPath($DestinationPath).TrimEnd('\')
$backupBase = [IO.Path]::GetFullPath($BackupRoot).TrimEnd('\')
if ($destination -eq [IO.Path]::GetPathRoot($destination).TrimEnd('\')) { throw 'Cannot install into a drive root.' }
$folders = @($package, $destination, $backupBase)
for ($i = 0; $i -lt $folders.Count; $i++) {
    for ($j = $i + 1; $j -lt $folders.Count; $j++) {
        if ($folders[$i].Equals($folders[$j], [StringComparison]::OrdinalIgnoreCase) -or
            $folders[$i].StartsWith($folders[$j] + '\', [StringComparison]::OrdinalIgnoreCase) -or
            $folders[$j].StartsWith($folders[$i] + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The package, installation, and backup must be separate directories.'
        }
    }
}
function Assert-NoDirectoryLinks([string]$folder) {
    for ($part = $folder; $part; $part = Split-Path -Parent $part) {
        if ((Test-Path -LiteralPath $part) -and
            ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw ('A deployment path contains a directory link: ' + $part)
        }
    }
}
foreach ($folder in $folders) { Assert-NoDirectoryLinks $folder }
$manifest = Join-Path $package 'SHA256SUMS.txt'
$entries = @(foreach ($line in [IO.File]::ReadAllLines($manifest)) {
    if ($line -notmatch '^([a-fA-F0-9]{64})  (.+)$') { throw 'Invalid package manifest.' }
    $hash = $Matches[1]
    $relative = $Matches[2].Replace('/', '\')
    $source = [IO.Path]::GetFullPath((Join-Path $package $relative))
    $target = [IO.Path]::GetFullPath((Join-Path $destination $relative))
    if (-not $source.StartsWith($package + '\', [StringComparison]::OrdinalIgnoreCase) -or
        -not $target.StartsWith($destination + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $relative.Contains(':')) { throw 'A package file is outside the installation directory.' }
    Assert-NoDirectoryLinks $source
    Assert-NoDirectoryLinks $target
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $hash) {
        throw ('Package checksum mismatch: ' + $relative)
    }
    [pscustomobject]@{ Relative = $relative; Source = $source; Target = $target; Hash = $hash }
})
if (@($entries | Select-Object -ExpandProperty Relative -Unique).Count -ne $entries.Count -or
    -not ($entries.Relative -contains 'WinDock.exe')) { throw 'Incomplete or duplicate package manifest.' }
if ($entries.Relative -contains 'SHA256SUMS.txt') { throw 'The checksum manifest cannot include itself.' }
$entries += [pscustomobject]@{ Relative = 'SHA256SUMS.txt'; Source = $manifest
    Target = (Join-Path $destination 'SHA256SUMS.txt'); Hash = (Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash }
$version = (Get-Item -LiteralPath (Join-Path $package 'WinDock.exe')).VersionInfo.ProductVersion
$buildInfo = Get-Content -LiteralPath (Join-Path $package 'build-info.json') -Raw | ConvertFrom-Json
if ($version -ne $buildInfo.productVersion) { throw 'Package version does not match its build record.' }

Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class WinDockDeploymentProbe {
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr p, uint flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr p);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    public static string PathFor(int pid) {
        var handle=OpenProcess(0x1000, false, pid);
        if(handle==IntPtr.Zero) return null;
        try { var text=new StringBuilder(32768); int size=text.Capacity;
            return QueryFullProcessImageName(handle,0,text,ref size) ? text.ToString() : null;
        } finally { CloseHandle(handle); }
    }
}
'@
$session = [Diagnostics.Process]::GetCurrentProcess().SessionId
$running = @(Get-Process -Name WinDock -ErrorAction SilentlyContinue | Where-Object SessionId -eq $session)
$previousPaths = @($running | ForEach-Object { [WinDockDeploymentProbe]::PathFor($_.Id) } | Select-Object -Unique)
if ($running.Count -gt 0 -and ($previousPaths.Count -ne 1 -or -not $previousPaths[0])) {
    throw 'Cannot unambiguously identify the running WinDock installation.'
}
$previousExe = if ($previousPaths.Count) { $previousPaths[0] } else { $null }
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$startupBefore = (Get-ItemProperty -LiteralPath $runKey -Name WinDock -ErrorAction SilentlyContinue).WinDock
$backup = Join-Path $backupBase ('install-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $backup -Force | Out-Null
$installedBefore = Join-Path $backup 'installed'
if (Test-Path -LiteralPath $destination) {
    Copy-Item -LiteralPath $destination -Destination $installedBefore -Recurse
    foreach ($original in Get-ChildItem -LiteralPath $destination -File -Recurse -Force) {
        $saved = Join-Path $installedBefore ([IO.Path]::GetRelativePath($destination, $original.FullName))
        if ((Get-FileHash -LiteralPath $original.FullName -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $saved -Algorithm SHA256).Hash) { throw 'Installation backup checksum mismatch.' }
    }
}
[ordered]@{ previousExecutable = $previousExe; startupCommand = $startupBefore; destination = $destination } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $backup 'before.json') -Encoding utf8
$settingsFile = Join-Path $env:APPDATA 'WinDock\settings.json'
$copied = [Collections.Generic.List[object]]::new()
$stopped = $false
$startupChanged = $false
$launched = $null
$settingsRaw = $null
function Save-SettingsWithNativeTaskbar([string]$Raw, [bool]$EnableNativeAnimations = $false) {
    $pattern = '"replaceTaskbar"\s*:\s*(true|false)'
    if ([regex]::Matches($Raw, $pattern).Count -ne 1) { throw 'Cannot safely preserve the native taskbar setting.' }
    $updated = [regex]::Replace($Raw, $pattern, '"replaceTaskbar": false')
    if ($EnableNativeAnimations) {
        $animationPattern = '"nativeWindowAnimations"\s*:\s*(true|false)'
        if ([regex]::Matches($updated, $animationPattern).Count -ne 1) { throw 'Cannot safely enable native animations.' }
        $updated = [regex]::Replace($updated, $animationPattern, '"nativeWindowAnimations": true')
    }
    $temporary = $settingsFile + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($temporary, $updated, [Text.UTF8Encoding]::new($false))
        $parsed = Get-Content -LiteralPath $temporary -Raw | ConvertFrom-Json
        if ($parsed.dock.replaceTaskbar -ne $false -or [IO.File]::ReadAllText($temporary) -cne $updated) {
            throw 'Temporary settings readback failed.'
        }
        [IO.File]::Replace($temporary, $settingsFile, $null)
    }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}
try {
    if ($running.Count) {
        $signal = [Threading.EventWaitHandle]::OpenExisting('WinDock.Exit')
        try { [void]$signal.Set() } finally { $signal.Dispose() }
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        do {
            $remaining = @($running | Where-Object { -not $_.HasExited })
            if ($remaining.Count) { Start-Sleep -Milliseconds 200 }
        } while ($remaining.Count -and [DateTime]::UtcNow -lt $deadline)
        if ($remaining.Count) { throw 'WinDock did not exit normally; installed files were not changed.' }
    }
    $stopped = $true
    $settingsRaw = [IO.File]::ReadAllText($settingsFile)
    $settingsBefore = $settingsRaw | ConvertFrom-Json
    Copy-Item -LiteralPath $settingsFile -Destination (Join-Path $backup 'settings.json')
    if ((Get-FileHash -LiteralPath $settingsFile).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $backup 'settings.json')).Hash) { throw 'Settings backup checksum mismatch.' }
    Save-SettingsWithNativeTaskbar $settingsRaw
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    foreach ($entry in $entries) {
        $parent = Split-Path -Parent $entry.Target
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
        if ((Test-Path -LiteralPath $entry.Target) -and
            (Get-FileHash -LiteralPath $entry.Target -Algorithm SHA256).Hash -eq $entry.Hash) { continue }
        $copied.Add($entry)
        Copy-Item -LiteralPath $entry.Source -Destination $entry.Target -Force
        if ((Get-FileHash -LiteralPath $entry.Target -Algorithm SHA256).Hash -ne $entry.Hash) {
            throw ('Installed file checksum mismatch: ' + $entry.Relative)
        }
    }
    $installedExe = Join-Path $destination 'WinDock.exe'
    $startupCommand = '"' + $installedExe + '" --tray'
    if (-not (Test-Path -LiteralPath $runKey)) { New-Item -Path $runKey -Force | Out-Null }
    New-ItemProperty -LiteralPath $runKey -Name WinDock -Value $startupCommand -PropertyType String -Force | Out-Null
    $startupChanged = $true
    if ((Get-ItemProperty -LiteralPath $runKey -Name WinDock).WinDock -cne $startupCommand) { throw 'Login entry readback failed.' }
    $animationLogin = [ordered]@{ requested = [bool]$EnableAnimationStartup; registered = $false; exitCode = $null }
    if ($EnableAnimationStartup) {
        # Windows presents its normal administrator consent prompt. This entry
        # only registers the protected login helper; it never starts an animation.
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        try { $userSid = $identity.User.Value } finally { $identity.Dispose() }
        try {
            $registration = Start-Process -FilePath (Join-Path $destination 'WinDock.MotionHost.exe') -ArgumentList @('--register-login', $userSid) -Verb RunAs -WindowStyle Hidden -PassThru
            $registration.WaitForExit()
            $animationLogin.exitCode = $registration.ExitCode
            $animationLogin.registered = $registration.ExitCode -eq 0
        }
        catch {
            $registrationError = $_.Exception
            if ($registrationError.InnerException -is [System.ComponentModel.Win32Exception]) { $registrationError = $registrationError.InnerException }
            $animationLogin.exitCode = if ($registrationError -is [System.ComponentModel.Win32Exception]) { $registrationError.NativeErrorCode } else { $registrationError.HResult }
        }
        if ($animationLogin.registered) { Save-SettingsWithNativeTaskbar $settingsRaw $true }
    }
    $launched = Start-Process -FilePath $installedExe -ArgumentList '--tray' -WorkingDirectory $destination -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 2
    if ($launched.HasExited -or [WinDockDeploymentProbe]::PathFor($launched.Id) -ine $installedExe) {
        throw 'Updated WinDock did not remain running from the installation directory.'
    }
    $settingsAfter = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
    $pinsPreserved = ($settingsBefore.alwaysPinned | ConvertTo-Json -Depth 20 -Compress) -ceq
                     ($settingsAfter.alwaysPinned | ConvertTo-Json -Depth 20 -Compress)
    if (-not $pinsPreserved -or $settingsAfter.dock.replaceTaskbar) { throw 'Settings readback failed.' }
    $taskbar = [WinDockDeploymentProbe]::FindWindow('Shell_TrayWnd', $null)
    $report = [ordered]@{
        installedAt = [DateTimeOffset]::Now.ToString('o'); version = $version
        installedExecutable = $installedExe; runningExecutable = [WinDockDeploymentProbe]::PathFor($launched.Id)
        processId = $launched.Id; startupCommand = $startupCommand; backup = $backup
        animationLogin = $animationLogin
        verifiedFiles = $entries.Count; pinsPreserved = $pinsPreserved; replaceTaskbar = $settingsAfter.dock.replaceTaskbar
        nativeTaskbarWindowVisible = [WinDockDeploymentProbe]::IsWindowVisible($taskbar)
        desktopInteractionTested = $false; loginCycleTested = $false
    }
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $backup 'install-report.json') -Encoding utf8
    $report | ConvertTo-Json -Depth 5
}
catch {
    $failure = $_
    if ($launched -and -not $launched.HasExited -and
        [WinDockDeploymentProbe]::PathFor($launched.Id) -ieq $installedExe) {
        try {
            $signal = [Threading.EventWaitHandle]::OpenExisting('WinDock.Exit')
            try { [void]$signal.Set() } finally { $signal.Dispose() }
            [void]$launched.WaitForExit(20000)
        }
        catch { Write-Warning 'The updated instance could not be asked to exit normally.' }
    }
    # A live process can hold native modules. Leave verified backups intact;
    # do not force-kill it or risk a partial rollback under that process.
    $stillRunning = @(Get-Process -Name WinDock -ErrorAction SilentlyContinue | Where-Object SessionId -eq $session)
    if ($stopped -and -not $stillRunning.Count) {
        $restoreErrors = [Collections.Generic.List[string]]::new()
        foreach ($entry in $copied) {
            $original = Join-Path $installedBefore $entry.Relative
            try {
                if (Test-Path -LiteralPath $original) {
                    $hash = (Get-FileHash -LiteralPath $original).Hash
                    if ((Test-Path -LiteralPath $entry.Target) -and (Get-FileHash -LiteralPath $entry.Target).Hash -eq $hash) { continue }
                    Copy-Item -LiteralPath $original -Destination $entry.Target -Force
                    if ((Get-FileHash -LiteralPath $entry.Target).Hash -ne $hash) { throw 'Restored checksum mismatch.' }
                }
                elseif (Test-Path -LiteralPath $entry.Target) { Remove-Item -LiteralPath $entry.Target }
            }
            catch { $restoreErrors.Add($entry.Relative + ': ' + $_.Exception.Message) }
        }
        if ($startupChanged) {
            try {
                if ($null -ne $startupBefore) { Set-ItemProperty -LiteralPath $runKey -Name WinDock -Value $startupBefore }
                else { Remove-ItemProperty -LiteralPath $runKey -Name WinDock -ErrorAction SilentlyContinue }
            }
            catch { $restoreErrors.Add('Startup: ' + $_.Exception.Message) }
        }
        if ($settingsRaw) {
            try { Save-SettingsWithNativeTaskbar $settingsRaw }
            catch { $restoreErrors.Add('Settings: ' + $_.Exception.Message) }
        }
        if ($restoreErrors.Count) { Write-Warning ('Restoration incomplete: ' + ($restoreErrors -join '; ')) }
        elseif ($previousExe) {
            try { Start-Process -FilePath $previousExe -ArgumentList '--tray' -WindowStyle Hidden -WorkingDirectory (Split-Path -Parent $previousExe) }
            catch { Write-Warning ('Previous WinDock could not be restarted: ' + $_.Exception.Message) }
        }
    }
    throw ('Installation incomplete. Backup: ' + $backup + '. ' + $failure.Exception.Message)
}
