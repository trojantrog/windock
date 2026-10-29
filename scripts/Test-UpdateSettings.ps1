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

foreach ($pair in @(
    @('79228162514264337593543950336', '79228162514264337593543950337'),
    @('0.1234567890123456789012345678901', '0.1234567890123456789012345678902'),
    @('1.00000000000000000000000000001', '1'),
    @('1e-100', '2e-100'),
    @('1e1000', '2e1000'),
    @('1e1000000000000000000000000000000', '2e1000000000000000000000000000000'),
    @('-1', '1')
)) {
    $beforePath = Join-Path $fixtureRootFull 'number-before.json'
    $afterPath = Join-Path $fixtureRootFull 'number-after.json'
    [IO.File]::WriteAllText($beforePath, ('{"unknownNumber":' + $pair[0] + '}'))
    [IO.File]::WriteAllText($afterPath, ('{"unknownNumber":' + $pair[1] + '}'))
    Assert-Throws { Assert-UpdateSettingsPreserved (Read-UpdateSettings $beforePath) (Read-UpdateSettings $afterPath) } ('Distinct JSON numbers were treated as equal: ' + $pair[0] + ' and ' + $pair[1])
}
$results.Add('Adjacent large integers and high-precision decimals remain distinct without rounding or underflow')

foreach ($pair in @(
    @('57', '57.0'),
    @('1.230000e4', '12300'),
    @('79228162514264337593543950336', '79228162514264337593543950336.0'),
    @('0.1234567890123456789012345678901', '0.12345678901234567890123456789010'),
    @('1e-1000', '10e-1001'),
    @('1e1000000000000000000000000000000', '10e999999999999999999999999999999'),
    @('-0e999999999999999999999999999999', '0')
)) {
    $beforePath = Join-Path $fixtureRootFull 'number-before.json'
    $afterPath = Join-Path $fixtureRootFull 'number-after.json'
    [IO.File]::WriteAllText($beforePath, ('{"unknownNumber":' + $pair[0] + '}'))
    [IO.File]::WriteAllText($afterPath, ('{"unknownNumber":' + $pair[1] + '}'))
    Assert-UpdateSettingsPreserved (Read-UpdateSettings $beforePath) (Read-UpdateSettings $afterPath)
}
$results.Add('Equivalent numeric text, trailing zeros and exponents compare exactly at arbitrary precision')

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

$repositoryInstallation = Join-Path $fixtureRootFull 'repository-installation'
$repositoryBackup = Join-Path $fixtureRootFull 'repository-backup'
New-Item -ItemType Directory -Path $repositoryInstallation | Out-Null
$git = @(Get-Command git -CommandType Application -ErrorAction Stop)[0].Source
& $git --no-optional-locks -C $repositoryInstallation init --quiet
Assert-True ($LASTEXITCODE -eq 0) 'The isolated Git fixture could not be initialized.'
foreach ($relative in @('README.md', 'src/source.cs', 'scripts/maintenance.ps1', 'docs/source.md', 'tests/source.cs',
    'publish/previous/WinDock.exe', 'obj/build-cache.bin', 'user-note.txt', 'build-info.json')) {
    $path = Join-Path $repositoryInstallation $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    [IO.File]::WriteAllText($path, ('preserve repository data: ' + $relative))
}
& $git --no-optional-locks -C $repositoryInstallation add -- README.md src/source.cs scripts/maintenance.ps1 docs/source.md tests/source.cs build-info.json
Assert-True ($LASTEXITCODE -eq 0) 'The isolated source fixture could not be tracked.'
$untouched = @(foreach ($relative in @('README.md', 'src/source.cs', 'scripts/maintenance.ps1', 'docs/source.md', 'tests/source.cs',
    'publish/previous/WinDock.exe', 'obj/build-cache.bin', 'user-note.txt', 'build-info.json', '.git/HEAD', '.git/config', '.git/index')) {
    [pscustomobject]@{ Relative = $relative; Hash = (Get-FileHash -LiteralPath (Join-Path $repositoryInstallation $relative) -Algorithm SHA256).Hash }
})
New-Item -ItemType Directory -Path (Join-Path $repositoryInstallation 'shortcuts'), (Join-Path $repositoryInstallation 'icons') -Force | Out-Null
[IO.File]::WriteAllBytes((Join-Path $repositoryInstallation 'shortcuts/editor.lnk'), $shortcutBytes)
[IO.File]::WriteAllBytes((Join-Path $repositoryInstallation 'icons/editor.png'), $iconBytes)
$repositoryEntries = @($packageEntries | ForEach-Object {
    [IO.File]::WriteAllText((Join-Path $repositoryInstallation $_.Relative), ('old repository product: ' + $_.Relative))
    [pscustomobject]@{ Relative = $_.Relative; Source = $_.Source; Target = Join-Path $repositoryInstallation $_.Relative; Hash = $_.Hash }
})
$repositoryResources = Get-UpdateInstalledResources $repositoryInstallation
Assert-UpdatePackageResourceBoundaries $repositoryEntries $repositoryInstallation
$backedUp = @(Backup-UpdateInstalledFiles $repositoryEntries $repositoryInstallation $repositoryBackup $repositoryResources)
Assert-True ($backedUp.Count -eq 4) 'The bounded installation backup did not contain exactly the two product files and two user resources.'
$backupFiles = @(Get-ChildItem -LiteralPath $repositoryBackup -Recurse -File -Force)
Assert-True ($backupFiles.Count -eq 4) 'Source, Git, publish or other nonproduct files were backed up.'
foreach ($file in $backedUp) {
    Assert-True ($file.Relative -in @('WinDock.exe', 'WinDock.component.dll', 'shortcuts\editor.lnk', 'icons\editor.png')) 'An unrelated repository file was included in the installation backup.'
    Assert-True ((Get-FileHash -LiteralPath (Join-Path $repositoryBackup $file.Relative) -Algorithm SHA256).Hash -ceq $file.Hash) 'A bounded backup checksum changed.'
}
Copy-UpdatePackageFiles $repositoryEntries ([Collections.Generic.List[object]]::new())
Assert-UpdateInstalledResourcesPreserved $repositoryResources
$unchangedBackup = @(Backup-UpdateInstalledFiles $repositoryEntries $repositoryInstallation (Join-Path $fixtureRootFull 'unchanged-product-backup') $repositoryResources)
Assert-True ($unchangedBackup.Count -eq 2) 'An unchanged product file was unnecessarily included in the update backup.'
foreach ($file in $untouched) {
    Assert-True ((Get-FileHash -LiteralPath (Join-Path $repositoryInstallation $file.Relative) -Algorithm SHA256).Hash -ceq $file.Hash) ('A source repository or nonproduct file changed: ' + $file.Relative)
}
foreach ($relative in @('.git/config', 'src/source.cs', 'scripts/maintenance.ps1', 'docs/source.md', 'tests/source.cs',
    'publish/previous/WinDock.exe', 'README.md', 'build-info.json', 'user-note.txt', 'unrecognized.json')) {
    $entry = [pscustomobject]@{ Relative = $relative; Target = Join-Path $repositoryInstallation $relative }
    Assert-Throws { Assert-UpdatePackageResourceBoundaries @($entry) $repositoryInstallation } ('A package could overwrite repository or nonproduct data: ' + $relative)
}
Assert-Throws { Backup-UpdateInstalledFiles $repositoryEntries $repositoryInstallation (Join-Path $repositoryInstallation 'backup') $repositoryResources } 'An installation backup inside the source repository was accepted.'
$results.Add('Repository-root updates leave tracked source, Git, publish and other nonproduct bytes untouched and out of backup')

$releaseFiles = @('WinDock.exe', 'WinDock.pdb', 'WinDock.TrayHook.v7.x64.dll', 'WinDock.TrayHook.v7.x64.dll.sha256',
    'WinDock.Motion.x64.dll', 'WinDock.MotionHost.exe', 'WinDock.Motion.manifest.json', 'build-info.json', 'SHA256SUMS.txt',
    '托盘与菜单说明.md', '托盘兼容说明.md', '托盘输入法说明.md', '窗口预览说明.md', '整窗动画说明.md', '系统通知说明.md',
    'native-tray-actions.md', 'native-tray-inventory.md', 'licenses/WinDock-LICENSE.txt', 'licenses/THIRD_PARTY_NOTICES.txt',
    'licenses/MinHook-LICENSE.txt', 'licenses/YASB-LICENSE.txt')
$releaseEntries = @($releaseFiles | ForEach-Object {
    [pscustomobject]@{ Relative = $_; Target = Join-Path $oldInstallation $_ }
})
Assert-UpdatePackageResourceBoundaries $releaseEntries $oldInstallation
$results.Add('All 21 installed paths from the preview.27 manifest satisfy the product file boundary')

$updaterNames = @('Install-Update.cmd', 'updater/Install-LocalBuild.ps1', 'updater/Update-InstalledBuild.ps1', 'updater/UpdateSettings.ps1')
foreach ($relative in $updaterNames) {
    foreach ($spelling in @($relative, $relative.ToUpperInvariant())) {
        $entry = [pscustomobject]@{ Relative = $spelling; Target = Join-Path $oldInstallation $spelling }
        Assert-UpdatePackageResourceBoundaries @($entry) $oldInstallation
    }
}
foreach ($relative in @('updater/evil.ps1', 'updater/Install-LocalBuild.ps1.bak', 'updater/sub/UpdateSettings.ps1',
    'Install-Update.ps1', 'scripts/UpdateSettings.ps1')) {
    $entry = [pscustomobject]@{ Relative = $relative; Target = Join-Path $oldInstallation $relative }
    Assert-Throws { Assert-UpdatePackageResourceBoundaries @($entry) $oldInstallation } ('An unapproved updater entry was accepted: ' + $relative)
}
$results.Add('Only the exact command entry and three updater scripts are allowed, regardless of path casing')

$installedExe = 'D:\Projects\windock\WinDock.exe'
$currentCommand = '"D:\Projects\windock\WinDock.exe" --tray'
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
$helperDirectory = Join-Path $fixtureRootFull 'runtime-helper'
New-Item -ItemType Directory -Path $helperDirectory | Out-Null
$helperSource = Join-Path $helperDirectory 'UpdateFixture.cs'
$helperExe = Join-Path $helperDirectory 'UpdateFixture.exe'
[IO.File]::WriteAllText($helperSource, @'
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;
class UpdateFixture {
    [STAThread] static void Main(string[] args) {
        if (args[0] == "watchdog") {
            File.WriteAllText(Path.Combine(args[2], "watchdog.pid"), Process.GetCurrentProcess().Id.ToString());
            try { using (var owner = Process.GetProcessById(int.Parse(args[1]))) owner.WaitForExit(); }
            catch (ArgumentException) { }
            Thread.Sleep(int.Parse(args[3]));
            File.WriteAllText(Path.Combine(args[2], "watchdog-exited"), "done");
            return;
        }
        var directory = args[2];
        using (var signal = EventWaitHandle.OpenExisting(args[1])) {
            Thread.Sleep(int.Parse(args[3]));
            var ready = Path.Combine(directory, "ready");
            File.WriteAllText(ready, "ready");
            var timer = new System.Windows.Forms.Timer();
            var elapsed = Stopwatch.StartNew();
            var context = new ApplicationContext();
            timer.Interval = 25;
            timer.Tick += delegate {
                if (elapsed.ElapsedMilliseconds >= 150 && elapsed.ElapsedMilliseconds < 450) {
                    if (File.Exists(ready)) File.Delete(ready);
                } else if (!File.Exists(ready)) File.WriteAllText(ready, "ready");
                if (signal.WaitOne(0) || elapsed.ElapsedMilliseconds > 15000) {
                    var child = new ProcessStartInfo(Application.ExecutablePath,
                        "watchdog " + Process.GetCurrentProcess().Id + " \"" + directory + "\" " + args[4]);
                    child.UseShellExecute = false; child.CreateNoWindow = true;
                    using (var watchdog = Process.Start(child)) { }
                    context.ExitThread();
                }
            };
            timer.Start(); Application.Run(context); timer.Dispose();
        }
    }
}
'@)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $compiler /nologo /target:winexe /reference:System.Windows.Forms.dll ('/out:' + $helperExe) $helperSource
Assert-True ($LASTEXITCODE -eq 0) 'The isolated runtime helper could not be compiled.'
Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.IO;
public static class WinDockDeploymentProbe {
    public static string ReadyFile;
    public static string PathFor(int pid) {
        try { using (var process = Process.GetProcessById(pid)) return process.MainModule.FileName; }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }
    public static IntPtr FindWindow(string cls, string title) { return new IntPtr(1); }
    public static bool IsWindowVisible(IntPtr window) { return !File.Exists(ReadyFile); }
}
'@
$startupStart = $installer.IndexOf('    $launched = Start-Process')
$startupEndText = '    $deploymentVerified = $true'
$startupEnd = $installer.IndexOf($startupEndText, $startupStart)
Assert-True ($startupStart -ge 0 -and $startupEnd -gt $startupStart) 'The real installer startup block was not found.'
$startupBlock = [scriptblock]::Create($installer.Substring($startupStart, $startupEnd + $startupEndText.Length - $startupStart))
$runtimeDirectory = Join-Path $fixtureRootFull 'delayed-runtime'
New-Item -ItemType Directory -Path $runtimeDirectory | Out-Null
$eventName = 'WinDock.UpdateFixture.' + [Guid]::NewGuid().ToString('N')
$fixtureSignal = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::AutoReset, $eventName)
$helperArguments = @('main', $eventName, ('"' + $runtimeDirectory + '"'), '2500', '900')
$installedExe = $helperExe
$userData = $snapshot
$installedResources = Get-UpdateInstalledResources $runtimeDirectory
[WinDockDeploymentProbe]::ReadyFile = Join-Path $runtimeDirectory 'ready'
$helperProcess = $null
try {
    $startupTimer = [Diagnostics.Stopwatch]::StartNew()
    $startupResult = & {
        function Start-Process {
            param($FilePath, $ArgumentList, $WorkingDirectory, $WindowStyle, [switch]$PassThru)
            $script:helperProcess = Microsoft.PowerShell.Management\Start-Process -FilePath $FilePath -ArgumentList $helperArguments -WorkingDirectory $helperDirectory -WindowStyle Hidden -PassThru
            return $script:helperProcess
        }
        . $startupBlock
        [pscustomobject]@{ Verified = $deploymentVerified; State = $startupState }
    }
    Assert-True ($startupResult.Verified -and $startupTimer.ElapsedMilliseconds -ge 3900) 'Startup accepted a transient taskbar state or did not wait for delayed readiness.'
    Assert-UpdateUserDataUnchanged $snapshot
    $results.Add('The real installer startup block waits for a delayed message loop and a stable taskbar state')
    $session = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $readProcessPath = { param($processId) [WinDockDeploymentProbe]::PathFor($processId) }
    Assert-True (@(Get-UpdateProcesses $helperExe ($session + 1) $readProcessPath).Count -eq 0) 'A different Windows session was included in the update.'
    Assert-True (@(Get-UpdateProcesses (Join-Path $runtimeDirectory 'UpdateFixture.exe') $session $readProcessPath).Count -eq 0) 'A same-name executable at another path was included in the update.'
    Assert-Throws { Get-UpdateProcesses $helperExe $session { param($processId) $null } } 'An unreadable live process path was ignored.'
    $exitTimer = [Diagnostics.Stopwatch]::StartNew()
    Wait-UpdateProcessesExit $helperExe $session $readProcessPath { [void]$fixtureSignal.Set() }
    Assert-True ($exitTimer.ElapsedMilliseconds -ge 900 -and (Test-Path -LiteralPath (Join-Path $runtimeDirectory 'watchdog-exited'))) 'Normal shutdown finished before the late watchdog exited.'
    Assert-True (@(Get-UpdateProcesses $helperExe $session $readProcessPath).Count -eq 0) 'The helper installation still has a running process.'
    $results.Add('Normal shutdown discovers a late watchdog and waits for it; other paths and sessions are excluded')
}
finally {
    [void]$fixtureSignal.Set()
    if ($helperProcess) { [void]$helperProcess.WaitForExit(18000) }
    $watchdogPidFile = Join-Path $runtimeDirectory 'watchdog.pid'
    if (Test-Path -LiteralPath $watchdogPidFile) {
        $watchdog = Get-Process -Id ([int][IO.File]::ReadAllText($watchdogPidFile)) -ErrorAction SilentlyContinue
        if ($watchdog) { [void]$watchdog.WaitForExit(3000) }
    }
    $fixtureSignal.Dispose()
}
$timeoutTimer = [Diagnostics.Stopwatch]::StartNew()
Assert-Throws {
    Wait-UpdateStableState -TimeoutMilliseconds 350 -StableMilliseconds 100 -Probe {
        [pscustomobject]@{ Ready = $false; Reason = 'The Windows taskbar visibility does not match the saved Dock setting.' }
    }
} 'A taskbar mismatch was accepted after the readiness timeout.'
Assert-True ($timeoutTimer.ElapsedMilliseconds -lt 2000) 'A readiness timeout was not bounded.'
$transientTimer = [Diagnostics.Stopwatch]::StartNew()
Assert-Throws {
    Wait-UpdateStableState -TimeoutMilliseconds 450 -StableMilliseconds 200 -PollMilliseconds 25 -Probe {
        [pscustomobject]@{ Ready = $transientTimer.ElapsedMilliseconds -lt 100; Reason = 'The taskbar became mismatched again.' }
    }
} 'A short-lived matching taskbar state was accepted.'
$results.Add('Persistent and transient taskbar mismatches fail within the configured deadline')

$parseTokens = $null
$parseErrors = $null
$installerAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Install-LocalBuild.ps1'), [ref]$parseTokens, [ref]$parseErrors)
$rollbackAst = $installerAst.Find({ param($node)
    $node -is [Management.Automation.Language.CatchClauseAst] -and $node.Body.Extent.Text.Contains('Installation incomplete. Backup: ')
}, $true)
Assert-True ($null -ne $rollbackAst) 'The real installer rollback block was not found.'
$rollbackText = $rollbackAst.Body.Extent.Text
$rollbackBlock = [scriptblock]::Create($rollbackText.Substring(1, $rollbackText.Length - 2))
$waitExitImplementation = (Get-Command Wait-UpdateProcessesExit).ScriptBlock
foreach ($timeout in @($false, $true)) {
    & {
        $rollbackRoot = Join-Path $fixtureRootFull ('rollback-' + $timeout)
        $destination = Join-Path $rollbackRoot 'installed'
        $package = Join-Path $rollbackRoot 'package'
        $backup = Join-Path $rollbackRoot 'backup'
        $installedBefore = Join-Path $backup 'installed'
        New-Item -ItemType Directory -Path $destination, $package -Force | Out-Null
        $entries = @(foreach ($relative in @('WinDock.component.dll', 'WinDock.added.dll')) {
            $path = Join-Path $package $relative
            [IO.File]::WriteAllText($path, ('new ' + $relative))
            [pscustomobject]@{ Relative = $relative; Source = $path; Target = Join-Path $destination $relative
                Hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
        })
        [IO.File]::WriteAllText($entries[0].Target, 'original product bytes')
        $resources = Get-UpdateInstalledResources $destination
        $null = Backup-UpdateInstalledFiles $entries $destination $installedBefore $resources
        $copied = [Collections.Generic.List[object]]::new()
        Copy-UpdatePackageFiles $entries $copied
        $startupBefore = '"C:\Previous\WinDock.exe" --tray'
        $beforeStartup = New-StartupFixture $startupBefore $true 'disabled'
        $startupPlan = Get-UpdateStartupPlan $beforeStartup $helperExe
        $currentStartup = New-StartupFixture $startupPlan.Command $true 'disabled'
        $startupWrites = [Collections.Generic.List[string]]::new()
        function Get-OrdinaryStartupState([string]$Executable) { return $currentStartup }
        function New-ItemProperty {
            param($LiteralPath, $Name, $Value, $PropertyType, [switch]$Force)
            Assert-True (Test-Path -LiteralPath (Join-Path $destination 'watchdog-exited')) 'Startup restoration ran before watchdog shutdown.'
            $startupWrites.Add($Value)
            $currentStartup.runValue = $Value
            $currentStartup.runKind = $PropertyType
        }
        function Wait-UpdateProcessesExit {
            param($Executable, $Session, $ReadPath, $RequestExit)
            & $waitExitImplementation $Executable $Session $ReadPath $RequestExit 3000
        }
        $runKey = 'fixture-only:Run'
        $installedExe = $helperExe
        $session = [Diagnostics.Process]::GetCurrentProcess().SessionId
        $fixtureEventName = 'WinDock.UpdateFixture.' + [Guid]::NewGuid().ToString('N')
        $rollbackSignal = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::AutoReset, $fixtureEventName)
        $launched = Microsoft.PowerShell.Management\Start-Process -FilePath $helperExe -ArgumentList @('main', $fixtureEventName, ('"' + $destination + '"'), '0', '1100') -WindowStyle Hidden -PassThru
        $deploymentVerified = $false
        $stopped = $true
        $startupChanged = $true
        $previousExe = $null
        $requestNormalExit = if ($timeout) { { } } else { { [void]$rollbackSignal.Set() } }
        $readProcessPath = {
            param($processId)
            Assert-True ([IO.File]::ReadAllText($entries[0].Target) -ceq 'new WinDock.component.dll') 'Program restoration ran while a same-image process was alive.'
            [WinDockDeploymentProbe]::PathFor($processId)
        }
        $settingsAfterSave = $source.Replace('"iconSize": 57', '"iconSize": 59')
        [IO.File]::WriteAllText($settingsFile, $settingsAfterSave)
        $savedHash = (Get-FileHash -LiteralPath $settingsFile -Algorithm SHA256).Hash
        try {
            if (-not $timeout) {
                [void]$rollbackSignal.Set()
                Assert-True ($launched.WaitForExit(3000)) 'The rollback helper main process did not exit.'
            }
            $reportedFailure = $null
            try { throw 'Injected installation failure.' }
            catch {
                try { . $rollbackBlock }
                catch { $reportedFailure = $_.Exception.Message }
            }
            Assert-True ($reportedFailure -like '*Injected installation failure.*') 'Rollback lost the original installation failure.'
            Assert-True ((Get-FileHash -LiteralPath $settingsFile -Algorithm SHA256).Hash -ceq $savedHash) 'Rollback overwrote settings saved after the backup.'
            if ($timeout) {
                Assert-True ([IO.File]::ReadAllText($entries[0].Target) -ceq 'new WinDock.component.dll') 'An exit timeout restored files under a live process.'
                Assert-True ((Test-Path -LiteralPath $entries[1].Target) -and $startupWrites.Count -eq 0) 'An exit timeout changed files or startup state.'
                Assert-True ($currentStartup.runValue -ceq $startupPlan.Command) 'An exit timeout changed the startup command.'
            }
            else {
                Assert-True ([IO.File]::ReadAllText($entries[0].Target) -ceq 'original product bytes') 'Rollback did not restore the old product bytes.'
                Assert-True (-not (Test-Path -LiteralPath $entries[1].Target)) 'Rollback left a new product file behind.'
                Assert-True ($startupWrites.Count -eq 1) 'Rollback did not restore the startup path exactly once.'
                Assert-UpdateStartupUnchanged $beforeStartup $currentStartup
            }
        }
        finally {
            [void]$rollbackSignal.Set()
            [void]$launched.WaitForExit(18000)
            & $waitExitImplementation $helperExe $session { param($processId) [WinDockDeploymentProbe]::PathFor($processId) } { [void]$rollbackSignal.Set() } 5000
            $rollbackSignal.Dispose()
            [IO.File]::WriteAllText($settingsFile, $source, [Text.UTF8Encoding]::new($true))
        }
    }
}
$results.Add('The real rollback block waits for the remaining watchdog, restores files and disabled startup, and preserves newly saved settings')
$results.Add('A normal-exit timeout leaves program files and startup state untouched with the original failure reported')
Assert-True ($installer.Contains("[string]`$DestinationPath = 'D:\Projects\windock'")) 'The default installation directory is not fixed.'
Assert-True ($wrapper.Contains("`$destination = 'D:\Projects\windock'")) 'The update entry does not target the existing installation.'
Assert-True (-not $wrapper.Contains('Test-Path')) 'The update entry rejects a missing executable before recovery installation can run.'
Assert-True (-not $installer.Contains('Save-SettingsWithNativeTaskbar') -and -not $installer.Contains('[switch]$EnableAnimationStartup = $true')) 'A forced settings or animation default returned.'
Assert-True ($installer.IndexOf('Assert-UpdateUserDataUnchanged $userData') -lt $installer.IndexOf('$launched = Start-Process')) 'User data must be checked before the updated executable starts.'
Assert-True ($installer.Contains('Assert-UpdateUserDataPreserved $userData')) 'The running application settings are not checked.'
Assert-True ($installer.Contains('$nativeTaskbarVisible -ne $expectedNativeTaskbarVisible')) 'Taskbar visibility is not checked against the saved choice.'
Assert-True (-not $installer.Contains('Copy-Item -LiteralPath $destination -Destination $installedBefore -Recurse')) 'The installer would recursively copy the source repository.'
$results.Add('The installer uses the fixed destination and settings checks without forced preferences')

[ordered]@{ passed = $results.Count; checks = $results; fixtureRoot = $fixtureRootFull
    registryWrites = $false; liveProcessesStopped = $false; nativeProbesExecuted = $false
    isolatedRuntimeHelpers = $true; helperMessageLoopProbed = $true; taskbarProbe = 'isolated fixture state'
    forceTerminationUsed = $false } | ConvertTo-Json -Depth 5
