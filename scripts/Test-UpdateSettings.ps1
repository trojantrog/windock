param([string]$FixtureRoot = (Join-Path ([IO.Path]::GetTempPath()) ('WinDock-update-fixtures-' + [Guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'UpdateSettings.ps1')
$results = [Collections.Generic.List[string]]::new()

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Throws([scriptblock]$Action, [string]$Message) {
    $failed = $false
    try { & $Action } catch { $failed = $true }
    if (-not $failed) { throw $Message }
}

function New-StartupFixture([string]$Command, [bool]$Present = $true, [string]$Approval = 'enabled', [string]$Policy = 'unconfigured') {
    $record = if ($Approval -eq 'unrecorded') { $null } elseif ($Approval -eq 'disabled') {
        [byte[]]@(3, 0, 0, 0, 15, 18, 22, 30, 40, 50, 60, 70)
    } elseif ($Approval -eq 'unknown') { [byte[]]@(250, 1, 2, 3, 4) } else { [byte[]]@(2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0) }
    return [pscustomobject]@{
        readable = $true; registered = $Present; runValue = if ($Present) { $Command } else { $null }
        runKind = if ($Present) { 'String' } else { $null }
        approvalReadable = $true; approvalPresent = $Approval -ne 'unrecorded'; approvalRecord = $record
        windowsApproval = $Approval; runPolicy = $Policy
    }
}

New-Item -ItemType Directory -Path $FixtureRoot | Out-Null
$fixtureRootFull = (Resolve-Path -LiteralPath $FixtureRoot).ProviderPath
foreach ($name in @('Install-LocalBuild.ps1', 'Update-InstalledBuild.ps1', 'UpdateSettings.ps1', 'Test-UpdateSettings.ps1')) {
    $tokens = $null
    $parseErrors = $null
    [void][Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $name), [ref]$tokens, [ref]$parseErrors)
    Assert-True ($parseErrors.Count -eq 0) ('PowerShell parse failed: ' + $name + ' ' + ($parseErrors -join '; '))
}
$results.Add('All four PowerShell files parse')

$source = @'
{
  "language": "zh-CN",
  "checkUpdates": false,
  "syncTaskbar": false,
  "activeId": "daily",
  "dock": {
    "enabled": true,
    "replaceTaskbar": true,
    "autoHide": false,
    "reserve": true,
    "followWindows": false,
    "nativeWindowAnimations": false,
    "iconSize": 57,
    "magnification": 1.83,
    "futureOption": { "value": "保留历史设置", "empty": [], "nullable": null }
  },
  "alwaysPinned": [{ "path": "C:\\程序\\应用.exe", "arguments": "--sample \"test\"" }],
  "futureRoot": [false, 17, { "id": "keep" }]
}
'@
$userDirectory = Join-Path $fixtureRootFull 'user-data'
New-Item -ItemType Directory -Path (Join-Path $userDirectory 'presets') -Force | Out-Null
$settingsFile = Join-Path $userDirectory 'settings.json'
[IO.File]::WriteAllText($settingsFile, $source, [Text.UTF8Encoding]::new($true))
[IO.File]::WriteAllText(($settingsFile + '.bak'), '{"oldHistory":true}', [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $userDirectory 'presets/daily.json'), '{"id":"daily","items":[{"path":"C:\\工具.exe"}],"future":42}')
[IO.File]::WriteAllText((Join-Path $userDirectory 'presets/daily.json.bak'), '{"id":"daily","items":[]}')
[IO.File]::WriteAllText((Join-Path $userDirectory 'custom-user-file.txt'), 'keep this file')
$originalHash = (Get-FileHash -LiteralPath $settingsFile -Algorithm SHA256).Hash
$snapshot = Backup-UpdateUserData $userDirectory (Join-Path $fixtureRootFull 'backup')
Assert-UpdateUserDataUnchanged $snapshot
Assert-UpdateUserDataPreserved $snapshot
Assert-True ($snapshot.Files.Count -eq 5) 'The complete user data backup was not captured.'
Assert-True ((Get-FileHash -LiteralPath $settingsFile -Algorithm SHA256).Hash -ceq $originalHash) 'Backing up settings changed their bytes.'
Assert-True ((Get-FileHash -LiteralPath (Join-Path $snapshot.BackupDirectory 'settings.json') -Algorithm SHA256).Hash -ceq $originalHash) 'The original settings bytes or BOM were changed in the backup.'
$results.Add('Settings bytes, BOM, unknown fields, pins, environments and history are preserved')

$unchanged = Read-UpdateSettings $settingsFile
$flagsReversed = $source.Replace('"replaceTaskbar": true', '"replaceTaskbar": false').Replace('"nativeWindowAnimations": false', '"nativeWindowAnimations": true')
[IO.File]::WriteAllText((Join-Path $fixtureRootFull 'reversed.json'), $flagsReversed)
$reversed = Read-UpdateSettings (Join-Path $fixtureRootFull 'reversed.json')
Assert-Throws { Assert-UpdateSettingsPreserved $unchanged $reversed } 'The previous forced taskbar and native animation resets were accepted.'
$taskbarOnly = $source.Replace('"replaceTaskbar": true', '"replaceTaskbar": false')
[IO.File]::WriteAllText((Join-Path $fixtureRootFull 'taskbar-reset.json'), $taskbarOnly)
Assert-Throws { Assert-UpdateSettingsPreserved $unchanged (Read-UpdateSettings (Join-Path $fixtureRootFull 'taskbar-reset.json')) } 'Resetting the taskbar choice was accepted.'
$animationsOnly = $source.Replace('"nativeWindowAnimations": false', '"nativeWindowAnimations": true')
[IO.File]::WriteAllText((Join-Path $fixtureRootFull 'animations-reset.json'), $animationsOnly)
Assert-Throws { Assert-UpdateSettingsPreserved $unchanged (Read-UpdateSettings (Join-Path $fixtureRootFull 'animations-reset.json')) } 'Resetting the native animation choice was accepted.'
Assert-UpdateSettingsPreserved $reversed $reversed
$results.Add('Both taskbar and animation choices remain valid; forced resets are rejected')

foreach ($change in @(
    @('"checkUpdates": false', '"checkUpdates": true'),
    @('"syncTaskbar": false', '"syncTaskbar": true'),
    @('"enabled": true', '"enabled": false'),
    @('"autoHide": false', '"autoHide": true'),
    @('"reserve": true', '"reserve": false'),
    @('"followWindows": false', '"followWindows": true'),
    @('"iconSize": 57', '"iconSize": 46'),
    @('"magnification": 1.83', '"magnification": 1.75'),
    @('"id": "keep"', '"id": "reset"'),
    @('"nullable": null', '"nullable": false')
)) {
    $path = Join-Path $fixtureRootFull 'changed.json'
    [IO.File]::WriteAllText($path, $source.Replace($change[0], $change[1]))
    Assert-Throws { Assert-UpdateSettingsPreserved $unchanged (Read-UpdateSettings $path) } ('A saved option reset was accepted: ' + $change[0])
}
$results.Add('Every existing setting is compared, including unknown nested values')

$normalized = ($source | ConvertFrom-Json | ConvertTo-Json -Depth 20 -Compress).Replace('"iconSize":57', '"iconSize":57.0')
[IO.File]::WriteAllText((Join-Path $fixtureRootFull 'normalized.json'), $normalized)
Assert-UpdateSettingsPreserved $unchanged (Read-UpdateSettings (Join-Path $fixtureRootFull 'normalized.json'))
$results.Add('Formatting and equivalent numeric JSON do not trigger a reset report')

[IO.File]::WriteAllText($settingsFile, $flagsReversed)
Assert-Throws { Assert-UpdateUserDataUnchanged $snapshot } 'A settings write before launch was accepted.'
Assert-Throws { Assert-UpdateUserDataPreserved $snapshot } 'A settings reset after launch was accepted.'
[IO.File]::WriteAllText($settingsFile, $source, [Text.UTF8Encoding]::new($true))
$pinPath = Join-Path $userDirectory 'presets/daily.json'
$pinBefore = [IO.File]::ReadAllBytes($pinPath)
[IO.File]::WriteAllText($pinPath, '{"id":"daily","items":[]}')
Assert-Throws { Assert-UpdateUserDataPreserved $snapshot } 'Lost environment pins were accepted.'
[IO.File]::WriteAllBytes($pinPath, $pinBefore)
Assert-UpdateUserDataUnchanged $snapshot
$results.Add('Concurrent settings writes and lost environment pins are detected')

$fallbackDirectory = Join-Path $fixtureRootFull 'fallback-data'
New-Item -ItemType Directory -Path $fallbackDirectory | Out-Null
[IO.File]::WriteAllText((Join-Path $fallbackDirectory 'settings.json'), '{broken json')
[IO.File]::WriteAllText((Join-Path $fallbackDirectory 'settings.json.bak'), $source)
$fallback = Backup-UpdateUserData $fallbackDirectory (Join-Path $fixtureRootFull 'fallback-backup')
Assert-UpdateUserDataUnchanged $fallback
Assert-UpdateUserDataPreserved $fallback
Assert-True ([IO.File]::ReadAllText((Join-Path $fallbackDirectory 'settings.json')) -ceq '{broken json') 'A recoverable settings original was overwritten.'
Assert-Throws { Backup-UpdateUserData $userDirectory (Join-Path $userDirectory 'nested-backup') } 'A user data backup inside the original was accepted.'
$results.Add('Recoverable settings originals stay intact and overlapping backups are rejected')

$oldInstallation = Join-Path $fixtureRootFull 'installed'
$newPackage = Join-Path $fixtureRootFull 'package'
New-Item -ItemType Directory -Path (Join-Path $oldInstallation 'shortcuts'), (Join-Path $oldInstallation 'icons'), $newPackage -Force | Out-Null
$shortcutPath = Join-Path $oldInstallation 'shortcuts/editor.lnk'
$shortcutBytes = [byte[]]@(76, 0, 0, 0, 1, 20, 2, 0, 0, 0, 0, 0, 192, 0, 0, 0, 0, 0, 0, 70) + [Text.Encoding]::Unicode.GetBytes('C:\Windows\notepad.exe')
[IO.File]::WriteAllBytes($shortcutPath, $shortcutBytes)
$iconPath = Join-Path $oldInstallation 'icons/editor.png'
$iconBytes = [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a8e0AAAAASUVORK5CYII=')
[IO.File]::WriteAllBytes($iconPath, $iconBytes)
foreach ($name in @('WinDock.exe', 'WinDock.component.dll')) {
    [IO.File]::WriteAllText((Join-Path $oldInstallation $name), ('old ' + $name))
    [IO.File]::WriteAllText((Join-Path $newPackage $name), ('new ' + $name))
}
[IO.File]::WriteAllText((Join-Path $oldInstallation 'user-note.txt'), 'keep this installed file')
$installedSnapshot = Get-UpdateInstalledResources $oldInstallation
$packageEntries = @(foreach ($name in @('WinDock.exe', 'WinDock.component.dll')) {
    [pscustomobject]@{ Relative = $name; Source = Join-Path $newPackage $name; Target = Join-Path $oldInstallation $name
        Hash = (Get-FileHash -LiteralPath (Join-Path $newPackage $name) -Algorithm SHA256).Hash }
})
Assert-UpdatePackageResourceBoundaries $packageEntries $oldInstallation
$copied = [Collections.Generic.List[object]]::new()
Copy-UpdatePackageFiles $packageEntries $copied
Assert-UpdateInstalledResourcesPreserved $installedSnapshot
Assert-True ($copied.Count -eq 2) 'The new product files were not installed.'
Assert-True ([IO.File]::ReadAllText((Join-Path $oldInstallation 'WinDock.exe')) -ceq 'new WinDock.exe') 'The old executable was restored over the update.'
Assert-True ([IO.File]::ReadAllText((Join-Path $oldInstallation 'WinDock.component.dll')) -ceq 'new WinDock.component.dll') 'The old DLL was restored over the update.'
Assert-True ([IO.File]::ReadAllText((Join-Path $oldInstallation 'user-note.txt')) -ceq 'keep this installed file') 'An unlisted installed file was removed.'
Assert-True ($installedSnapshot.Files.Count -eq 2) 'The shortcut and icon were not tracked.'
$results.Add('A real file overlay replaces old program files and preserves shortcut, icon and unlisted user bytes')

foreach ($relative in @('shortcuts/editor.lnk', 'icons/editor.png', 'SHORTCUTS/other.dll', 'nested/../shortcuts/editor.lnk', '../outside.exe')) {
    $entry = [pscustomobject]@{ Relative = $relative; Target = [IO.Path]::GetFullPath((Join-Path $oldInstallation $relative)) }
    Assert-Throws { Assert-UpdatePackageResourceBoundaries @($entry) $oldInstallation } ('A package could overwrite or escape user resources: ' + $relative)
}
$results.Add('Packages cannot overwrite shortcuts/icons or escape the installation boundary')

[IO.File]::WriteAllText($iconPath, 'unexpected image change')
Assert-Throws { Assert-UpdateInstalledResourcesPreserved $installedSnapshot } 'A changed installed icon was accepted.'
[IO.File]::WriteAllBytes($iconPath, $iconBytes)
$recoveryInstallation = Join-Path $fixtureRootFull 'recovered-shortcuts-only'
New-Item -ItemType Directory -Path (Join-Path $recoveryInstallation 'shortcuts') -Force | Out-Null
[IO.File]::WriteAllBytes((Join-Path $recoveryInstallation 'shortcuts/editor.lnk'), $shortcutBytes)
$recoveredSnapshot = Get-UpdateInstalledResources $recoveryInstallation
$recoveryEntries = @($packageEntries | ForEach-Object {
    [pscustomobject]@{ Relative = $_.Relative; Source = $_.Source; Target = Join-Path $recoveryInstallation $_.Relative; Hash = $_.Hash }
})
Assert-UpdatePackageResourceBoundaries $recoveryEntries $recoveryInstallation
Copy-UpdatePackageFiles $recoveryEntries ([Collections.Generic.List[object]]::new())
Assert-UpdateInstalledResourcesPreserved $recoveredSnapshot
Assert-True (Test-Path -LiteralPath (Join-Path $recoveryInstallation 'WinDock.exe')) 'An installation with only recovered shortcuts could not be completed.'
$missingInstallation = Join-Path $fixtureRootFull 'previously-missing-installation'
$missingSnapshot = Get-UpdateInstalledResources $missingInstallation
$missingEntries = @($packageEntries | ForEach-Object {
    [pscustomobject]@{ Relative = $_.Relative; Source = $_.Source; Target = Join-Path $missingInstallation $_.Relative; Hash = $_.Hash }
})
Assert-UpdatePackageResourceBoundaries $missingEntries $missingInstallation
Copy-UpdatePackageFiles $missingEntries ([Collections.Generic.List[object]]::new())
Assert-UpdateInstalledResourcesPreserved $missingSnapshot
Assert-True (Test-Path -LiteralPath (Join-Path $missingInstallation 'WinDock.exe')) 'A missing fixed installation directory could not be recreated.'
$results.Add('Recovered shortcuts survive reinstallation and a missing fixed directory is recreated')

$installedExe = 'D:\WinDock\WinDock.exe'
$currentCommand = '"D:\WinDock\WinDock.exe" --tray'
$oldCommand = '"C:\Downloads\WinDock-preview\WinDock.exe" --tray'
foreach ($approval in @('enabled', 'disabled', 'unrecorded', 'unknown')) {
    foreach ($policy in @('unconfigured', 'disabled')) {
        $before = New-StartupFixture $oldCommand $true $approval $policy
        $after = New-StartupFixture $oldCommand $true $approval $policy
        $approvalBefore = ConvertTo-Json -InputObject $after.approvalRecord -Compress
        $plan = Get-UpdateStartupPlan $before $installedExe
        $writes = [Collections.Generic.List[string]]::new()
        Invoke-UpdateStartupPlan $plan {
            param($command, $kind)
            $writes.Add($command)
            $after.runValue = $command
            $after.runKind = $kind
        }
        Assert-True ($writes.Count -eq 1 -and $after.runValue -ceq $currentCommand) 'An existing startup entry was not relocated to the installed executable.'
        Assert-True ((ConvertTo-Json -InputObject $after.approvalRecord -Compress) -ceq $approvalBefore) 'A Windows startup approval record changed.'
        Assert-UpdateStartupPreserved $plan $after
    }
}
$results.Add('Existing startup paths relocate while enabled, disabled, unknown and policy states stay intact')

foreach ($approval in @('enabled', 'disabled', 'unrecorded')) {
    $before = New-StartupFixture '' $false $approval
    $after = New-StartupFixture '' $false $approval
    $plan = Get-UpdateStartupPlan $before $installedExe
    Invoke-UpdateStartupPlan $plan { throw 'A missing startup entry must not be created.' }
    Assert-UpdateStartupPreserved $plan $after
    Assert-True (-not $plan.Write -and $plan.Action -eq 'keepAbsent') 'An absent startup entry was enabled.'
}
$results.Add('A missing startup entry remains absent')

foreach ($command in @($currentCommand, '"C:\Custom\launcher.exe" --windock', 'unexpected value')) {
    $before = New-StartupFixture $command
    $plan = Get-UpdateStartupPlan $before $installedExe
    Invoke-UpdateStartupPlan $plan { throw 'A current or custom startup entry must not be rewritten.' }
    Assert-UpdateStartupPreserved $plan (New-StartupFixture $command)
}
$results.Add('Current and custom startup entries remain unchanged')

$before = New-StartupFixture $oldCommand
$before.runKind = 'ExpandString'
$after = New-StartupFixture $currentCommand
$after.runKind = 'ExpandString'
$plan = Get-UpdateStartupPlan $before $installedExe
Assert-True ($plan.Kind -eq 'ExpandString') 'The registered value type was changed.'
Assert-UpdateStartupPreserved $plan $after
$after.approvalRecord[0] = 3
Assert-Throws { Assert-UpdateStartupPreserved $plan $after } 'A concurrent Windows startup approval change was accepted.'
$deleted = New-StartupFixture '' $false
Assert-Throws { Assert-UpdateStartupUnchanged $before $deleted } 'A concurrently removed startup entry was accepted for rewriting.'
$changedCommand = New-StartupFixture '"C:\Custom\launcher.exe"'
Assert-Throws { Assert-UpdateStartupUnchanged $before $changedCommand } 'A concurrently changed startup command was accepted for rewriting.'
$before.readable = $false
$plan = Get-UpdateStartupPlan $before $installedExe
Invoke-UpdateStartupPlan $plan { throw 'An unreadable startup entry must not be written.' }
Assert-Throws { Assert-UpdateStartupPreserved $plan $before } 'An unreadable startup state was claimed as verified.'
$results.Add('Registry value types remain intact; unreadable and concurrent state is not claimed as verified')

$installer = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Install-LocalBuild.ps1'))
$wrapper = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Update-InstalledBuild.ps1'))
Assert-True ($installer.Contains("[string]`$DestinationPath = 'D:\WinDock'")) 'The default installation directory is not fixed.'
Assert-True ($wrapper.Contains("`$destination = 'D:\WinDock'")) 'The update entry does not target the existing installation.'
Assert-True (-not $wrapper.Contains('Test-Path')) 'The update entry rejects a missing executable before recovery installation can run.'
Assert-True (-not $installer.Contains('Save-SettingsWithNativeTaskbar') -and -not $installer.Contains('[switch]$EnableAnimationStartup = $true')) 'A forced settings or animation default returned.'
Assert-True ($installer.IndexOf('Assert-UpdateUserDataUnchanged $userData') -lt $installer.IndexOf('$launched = Start-Process')) 'User data must be checked before the updated executable starts.'
Assert-True ($installer.Contains('Assert-UpdateUserDataPreserved $userData')) 'The running application settings are not checked.'
Assert-True ($installer.Contains('$nativeTaskbarVisible -ne $expectedNativeTaskbarVisible')) 'Taskbar visibility is not checked against the saved choice.'
$results.Add('The installer uses the fixed destination and settings checks without forced preferences')

[ordered]@{ passed = $results.Count; checks = $results; fixtureRoot = $fixtureRootFull
    registryWrites = $false; liveProcessesStopped = $false; nativeProbesExecuted = $false } | ConvertTo-Json -Depth 5
