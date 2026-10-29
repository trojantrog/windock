$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'UpdatePackage.ps1')
$repository = Split-Path -Parent $PSScriptRoot
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$sandbox = Join-Path $temporaryRoot ('windock-package-tests-' + [Guid]::NewGuid().ToString('N'))
$groups = [Collections.Generic.List[string]]::new()

function Assert-PackageTest([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-PackageFailure([scriptblock]$Action, [string]$Expected) {
    $message = $null
    try { & $Action | Out-Null }
    catch { $message = $_.Exception.Message }
    Assert-PackageTest ($null -ne $message) ('Expected failure: ' + $Expected)
    Assert-PackageTest ($message.Contains($Expected)) ('Unexpected failure: ' + $message)
}

function Write-PackageFixtureFile([string]$Path, [string]$Content) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function New-PackageFixture([string]$Name) {
    $root = Join-Path $sandbox $Name
    $source = Join-Path $root 'source'
    $package = Join-Path $root 'WinDock fixture'
    New-Item -ItemType Directory -Path $package | Out-Null
    Write-PackageFixtureFile (Join-Path $source 'src/WinDock/source.cs') 'Source must remain byte-for-byte unchanged.'
    Write-PackageFixtureFile (Join-Path $source 'README.md') 'Source documentation.'
    Write-PackageFixtureFile (Join-Path $source 'scripts/Install-LocalBuild.ps1') @'
param([Parameter(Mandatory = $true)][string]$PackagePath)
. (Join-Path $PSScriptRoot 'UpdateSettings.ps1')
'@
    Write-PackageFixtureFile (Join-Path $source 'scripts/Update-InstalledBuild.ps1') @'
param([Parameter(Mandatory = $true)][string]$PackagePath, [string]$BackupRoot, [switch]$EnableAnimationStartup)
if ($env:WINDOCK_PACKAGE_FIXTURE_RECEIPT) {
    [ordered]@{ packagePath = [IO.Path]::GetFullPath($PackagePath).TrimEnd('\'); backupRoot = $BackupRoot
        animation = [bool]$EnableAnimationStartup; powerShellMajor = $PSVersionTable.PSVersion.Major } |
        ConvertTo-Json | Set-Content -LiteralPath $env:WINDOCK_PACKAGE_FIXTURE_RECEIPT -Encoding utf8
    exit 0
}
& (Join-Path $PSScriptRoot 'Install-LocalBuild.ps1') -PackagePath $PackagePath
'@
    Write-PackageFixtureFile (Join-Path $source 'scripts/UpdateSettings.ps1') "function Get-FixtureSettings { return 'fixture' }"
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-Update.cmd') -Destination (Join-Path $source 'scripts/Install-Update.cmd')
    Write-PackageFixtureFile (Join-Path $package 'WinDock.exe') 'An inert payload fixture; never executed.'
    Write-PackageFixtureFile (Join-Path $package 'WinDock.TrayHook.v7.x64.dll') 'An inert native fixture; never loaded.'
    Write-PackageFixtureFile (Join-Path $package 'build-info.json') '{"productVersion":"fixture"}'
    Write-PackageFixtureFile (Join-Path $package 'licenses/WinDock-LICENSE.txt') 'Fixture license.'
    Write-PackageFixtureFile (Join-Path $package '整窗动画说明.md') 'Fixture release documentation.'
    return [pscustomobject]@{ Root = $root; Source = $source; Package = $package }
}

function Get-PackageFixtureSnapshot([string]$Directory) {
    return @(Get-ChildItem -LiteralPath $Directory -Recurse -Force -File | ForEach-Object {
        [IO.Path]::GetRelativePath($Directory, $_.FullName).Replace('\', '/') + '  ' +
            (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    } | Sort-Object) -join "`n"
}

New-Item -ItemType Directory -Path $sandbox | Out-Null
try {
    foreach ($name in @('Package-LocalBuild.ps1', 'UpdatePackage.ps1', 'Test-UpdatePackage.ps1')) {
        $tokens = $null
        $errors = $null
        $null = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $name), [ref]$tokens, [ref]$errors)
        Assert-PackageTest ($errors.Count -eq 0) ('PowerShell syntax errors in ' + $name)
        Assert-PackageTest (@($tokens | Where-Object Kind -eq Comment).Count -eq 0) ('Unexpected code comment in ' + $name)
    }
    $groups.Add('PowerShell syntax and comment policy')

    $launcher = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Install-Update.cmd'))
    Assert-PackageTest ($launcher.Contains('__WINDOCK_POWERSHELL7_PATH__')) 'The launcher must accept a verified local PowerShell path.'
    Assert-PackageTest ($launcher.Contains('%ProgramFiles%\PowerShell\7\pwsh.exe')) 'The launcher must prefer installed PowerShell 7.'
    Assert-PackageTest ($launcher.IndexOf('%ProgramFiles%\PowerShell\7\pwsh.exe') -lt $launcher.IndexOf('where pwsh.exe')) 'The installed PowerShell path must be checked before PATH.'
    Assert-PackageTest ($launcher.Contains('$PSVersionTable.PSVersion.Major -lt 7')) 'The launcher must check the PowerShell version.'
    Assert-PackageTest ($launcher.Contains('-File "%~dp0updater\Update-InstalledBuild.ps1" -PackagePath "%~dp0."')) 'The launcher must use its extracted package directory.'
    Assert-PackageTest ($launcher.Contains('PowerShell 7 or later is required.')) 'The missing-runtime message must state the requirement.'
    Assert-PackageTest ($launcher -notmatch '(?i)powershell\.exe|-ExecutionPolicy|Set-ExecutionPolicy|Unblock-File|runas') 'The launcher must retain normal Windows execution controls.'
    $groups.Add('Launcher references, version requirement and execution controls')

    $fixture = New-PackageFixture 'complete'
    $sourceBefore = Get-PackageFixtureSnapshot $fixture.Source
    Add-UpdatePackageEntryPoints $fixture.Source $fixture.Package
    $entryNames = @('Install-Update.cmd', 'updater/Install-LocalBuild.ps1', 'updater/Update-InstalledBuild.ps1', 'updater/UpdateSettings.ps1')
    foreach ($relative in @($entryNames | Where-Object { $_ -ne 'Install-Update.cmd' })) {
        $source = Join-Path $fixture.Source ('scripts/' + (Split-Path -Leaf $relative))
        Assert-PackageTest ((Get-FileHash -LiteralPath $source).Hash -ceq (Get-FileHash -LiteralPath (Join-Path $fixture.Package $relative)).Hash) ('Copied entry point differs: ' + $relative)
    }
    $renderedLauncher = [IO.File]::ReadAllText((Join-Path $fixture.Package 'Install-Update.cmd'))
    Assert-PackageTest ($renderedLauncher.Contains([Environment]::ProcessPath.Replace('%', '%%'))) 'The verified current PowerShell path is missing from the launcher.'
    Assert-PackageTest (-not $renderedLauncher.Contains('__WINDOCK_POWERSHELL7_PATH__')) 'The launcher contains an unresolved PowerShell path.'
    $groups.Add('Three update scripts preserved and verified local PowerShell path injected')

    $receipt = Join-Path $fixture.Root 'launcher-receipt.json'
    $backupArgument = Join-Path $fixture.Root 'backup with spaces & symbols!'
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $env:ComSpec
    $start.Arguments = '/d /c ""' + (Join-Path $fixture.Package 'Install-Update.cmd') + '" -BackupRoot "' + $backupArgument + '" -EnableAnimationStartup"'
    $start.WorkingDirectory = $fixture.Package
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.RedirectStandardInput = $true
    $start.Environment['WINDOCK_PACKAGE_FIXTURE_RECEIPT'] = $receipt
    $start.Environment['PATH'] = [Environment]::SystemDirectory
    $start.Environment['ProgramFiles'] = Join-Path $fixture.Root 'no-installed-powershell'
    $child = [Diagnostics.Process]::Start($start)
    try {
        $child.StandardInput.Close()
        $stdout = $child.StandardOutput.ReadToEndAsync()
        $stderr = $child.StandardError.ReadToEndAsync()
        if (-not $child.WaitForExit(20000)) { $child.Kill($true); throw 'The isolated launcher did not finish.' }
        Assert-PackageTest ($child.ExitCode -eq 0) ('The isolated launcher failed: ' + $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult())
    }
    finally { $child.Dispose() }
    $forwarded = Get-Content -LiteralPath $receipt -Raw | ConvertFrom-Json
    Assert-PackageTest ($forwarded.packagePath -ceq $fixture.Package) 'The launcher passed the wrong package directory.'
    Assert-PackageTest ($forwarded.backupRoot -ceq $backupArgument) 'The launcher changed the quoted argument.'
    Assert-PackageTest ($forwarded.animation -eq $true -and $forwarded.powerShellMajor -ge 7) 'The launcher did not forward the switch through PowerShell 7.'
    $groups.Add('Isolated launcher executes fake updater with quoted arguments and no PowerShell PATH')

    $result = Complete-UpdatePackage $fixture.Source $fixture.Package
    $manifest = [IO.File]::ReadAllLines((Join-Path $fixture.Package 'SHA256SUMS.txt'))
    $manifestNames = @($manifest | ForEach-Object { $_.Substring(66) })
    foreach ($relative in $entryNames) { Assert-PackageTest ($relative -cin $manifestNames) ('Missing update checksum: ' + $relative) }
    Assert-PackageTest ('SHA256SUMS.txt' -cnotin $manifestNames) 'The manifest cannot hash itself.'
    foreach ($line in $manifest) {
        Assert-PackageTest ($line -cmatch '^[a-f0-9]{64}  [^\r\n]+$') 'A manifest line is malformed.'
        Assert-PackageTest ((Get-FileHash -LiteralPath (Join-Path $fixture.Package $line.Substring(66))).Hash -ieq $line.Substring(0, 64)) 'A manifest checksum does not match.'
    }
    Assert-PackageTest ($result.filesVerified -eq $manifest.Count + 1) 'The verified file count must include the manifest.'
    $groups.Add('Manifest includes update entry points and matches every payload')

    $zip = [IO.Compression.ZipFile]::OpenRead($result.archive)
    try {
        $names = @($zip.Entries | Where-Object Name | ForEach-Object { $_.FullName.Replace('\', '/') })
        $prefix = (Split-Path -Leaf $fixture.Package) + '/'
        foreach ($relative in @($manifestNames) + 'SHA256SUMS.txt') {
            Assert-PackageTest (($prefix + $relative) -cin $names) ('An archive entry is missing: ' + $relative)
        }
        Assert-PackageTest ($names.Count -eq $result.filesVerified) 'The archive contains unexpected files.'
        Assert-PackageTest (@($names | Where-Object { $_ -match '/(?:src|scripts|docs|\.git)/' }).Count -eq 0) 'Source files must not enter the archive.'
    }
    finally { $zip.Dispose() }
    Assert-PackageTest (([IO.File]::ReadAllText($result.archive + '.sha256')).Trim() -ceq (Get-FileHash -LiteralPath $result.archive).Hash.ToLowerInvariant()) 'The archive checksum is wrong.'
    $groups.Add('Archive entries, Unicode paths and archive checksum')

    Add-UpdatePackageEntryPoints $fixture.Source $fixture.Package
    Assert-PackageTest ($sourceBefore -ceq (Get-PackageFixtureSnapshot $fixture.Source)) 'Packaging changed the source repository.'
    Assert-PackageTest (-not (Test-Path -LiteralPath (Join-Path $fixture.Source 'publish'))) 'Packaging must not create a source publish directory.'
    Assert-PackageFailure { Complete-UpdatePackage $fixture.Source $fixture.Package } 'refusing to overwrite'
    $groups.Add('Source unchanged, idempotent entry copy and archive overwrite rejected')

    Assert-PackageFailure { Add-UpdatePackageEntryPoints $fixture.Source $fixture.Source } 'outside the source repository'
    $nested = Join-Path $fixture.Source 'publish/new-version'
    New-Item -ItemType Directory -Path $nested -Force | Out-Null
    Assert-PackageFailure { Add-UpdatePackageEntryPoints $fixture.Source $nested } 'outside the source repository'
    Assert-PackageFailure { Add-UpdatePackageEntryPoints $fixture.Source $fixture.Root } 'outside the source repository'
    Assert-PackageFailure { & (Join-Path $PSScriptRoot 'Package-LocalBuild.ps1') -PublishPath $repository } 'outside the source repository'
    Assert-PackageTest ($sourceBefore -ceq (Get-PackageFixtureSnapshot $fixture.Source)) 'Rejected source destinations changed source files.'
    $groups.Add('Repository, nested publish and repository parent destinations rejected')

    $worktrees = @(& git --no-optional-locks -C $repository worktree list --porcelain -z)
    Assert-PackageTest ($LASTEXITCODE -eq 0) 'The source checkout list could not be read.'
    foreach ($record in (($worktrees -join "`n") -split "`0")) {
        if (-not $record.StartsWith('worktree ')) { continue }
        $checkout = $record.Substring(9)
        if (-not (Test-Path -LiteralPath $checkout -PathType Container)) { continue }
        Assert-PackageFailure { Assert-UpdatePackageLocation $repository $checkout } 'outside'
    }
    $groups.Add('Registered source checkouts rejected as output directories')

    $fixture = New-PackageFixture 'missing-source'
    Remove-Item -LiteralPath (Join-Path $fixture.Source 'scripts/UpdateSettings.ps1')
    Assert-PackageFailure { Add-UpdatePackageEntryPoints $fixture.Source $fixture.Package } 'An update source file is missing'
    Assert-PackageTest (-not (Test-Path -LiteralPath (Join-Path $fixture.Package 'updater'))) 'Missing source validation wrote a partial updater.'
    Assert-PackageTest (-not (Test-Path -LiteralPath (Join-Path $fixture.Package 'Install-Update.cmd'))) 'Missing source validation wrote a partial launcher.'
    $groups.Add('Missing source rejected before copying entry points')

    $fixture = New-PackageFixture 'missing-entry'
    Add-UpdatePackageEntryPoints $fixture.Source $fixture.Package
    Remove-Item -LiteralPath (Join-Path $fixture.Package 'updater/UpdateSettings.ps1')
    Assert-PackageFailure { Complete-UpdatePackage $fixture.Source $fixture.Package } 'A required package file is missing'
    Assert-PackageTest (@(Get-ChildItem -LiteralPath $fixture.Root -Filter '*.zip').Count -eq 0) 'An incomplete package produced an archive.'
    $groups.Add('Incomplete updater cannot be archived')

    $fixture = New-PackageFixture 'unexpected-source'
    foreach ($relative in @('src/source.cs', 'scripts/Install-LocalBuild.ps1', 'docs/readme.md', '.git/config', 'README.md', 'updater/unknown.ps1')) {
        $path = Join-Path $fixture.Package $relative
        Write-PackageFixtureFile $path 'Unexpected source payload.'
        Assert-PackageFailure { Add-UpdatePackageEntryPoints $fixture.Source $fixture.Package } 'unsupported installed file'
        Remove-Item -LiteralPath $path
    }
    $groups.Add('Source, repository metadata and unknown scripts rejected')

    $fixture = New-PackageFixture 'directory-collision'
    New-Item -ItemType Directory -Path (Join-Path $fixture.Package 'updater/UpdateSettings.ps1') -Force | Out-Null
    Assert-PackageFailure { Add-UpdatePackageEntryPoints $fixture.Source $fixture.Package } 'an existing directory'
    Assert-PackageTest (-not (Test-Path -LiteralPath (Join-Path $fixture.Package 'Install-Update.cmd'))) 'A directory collision wrote partial entry points.'
    $groups.Add('Directory collisions rejected before copying entry points')

    $installedFiles = @('WinDock.exe', 'WinDock.pdb', 'WinDock.dll', 'WinDock.Motion.x64.dll', 'WinDock.TrayHook.v7.x64.dll.sha256',
        'WinDock.MotionHost.exe', 'WinDock.Motion.manifest.json', 'WinDock.deps.json', 'WinDock.runtimeconfig.json',
        'build-info.json', 'SHA256SUMS.txt', 'licenses/WinDock-LICENSE.txt', 'updater/Install-LocalBuild.ps1', 'Install-Update.cmd',
        '托盘与菜单说明.md', '托盘兼容说明.md', '托盘输入法说明.md', '窗口预览说明.md', '整窗动画说明.md', '系统通知说明.md',
        'native-tray-actions.md', 'native-tray-inventory.md')
    $ignored = @(((($installedFiles -join "`0") + "`0") | & git -C $repository check-ignore --no-index -z --stdin) -split "`0" | Where-Object { $_ })
    Assert-PackageTest ($LASTEXITCODE -eq 0 -and $ignored.Count -eq $installedFiles.Count) 'Root installation files are not all ignored.'
    $sourceExamples = @('scripts/Install-Update.cmd', 'scripts/UpdatePackage.ps1', 'scripts/Test-UpdatePackage.ps1',
        'src/WinDock/WinDock.exe', 'src/WinDock/WinDock.Motion.x64.dll', 'docs/native-tray-actions.md', 'docs/整窗动画说明.md', 'README.zh-CN.md')
    $ignoredSource = @(((($sourceExamples -join "`0") + "`0") | & git -C $repository check-ignore --no-index -z --stdin) -split "`0" | Where-Object { $_ })
    Assert-PackageTest ($LASTEXITCODE -eq 1 -and $ignoredSource.Count -eq 0) 'Installation ignore rules conceal source changes.'
    $groups.Add('Root install artifacts ignored while source paths remain visible')

    [ordered]@{ groupsPassed = $groups.Count; groups = @($groups); launcherFixtureExecuted = $true; winDockExecuted = $false
        realInstallerExecuted = $false; registryWrites = $false; nativeComponentsLoaded = $false } |
        ConvertTo-Json -Depth 4
}
finally {
    $resolved = [IO.Path]::GetFullPath($sandbox)
    if (-not $resolved.StartsWith($temporaryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $resolved) -notmatch '^windock-package-tests-[a-f0-9]{32}$') { throw 'The fixture cleanup path is outside its temporary directory.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
