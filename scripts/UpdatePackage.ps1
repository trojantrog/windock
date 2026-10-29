function Assert-UpdatePackageLocation([string]$RepositoryDirectory, [string]$PackageDirectory) {
    $repository = [IO.Path]::GetFullPath($RepositoryDirectory).TrimEnd('\', '/')
    $package = [IO.Path]::GetFullPath($PackageDirectory).TrimEnd('\', '/')
    $separator = [IO.Path]::DirectorySeparatorChar
    if ($package -eq [IO.Path]::GetPathRoot($package).TrimEnd('\', '/')) {
        throw 'The package cannot use a drive root.'
    }
    if ($package.Equals($repository, [StringComparison]::OrdinalIgnoreCase) -or
        $package.StartsWith($repository + $separator, [StringComparison]::OrdinalIgnoreCase) -or
        $repository.StartsWith($package + $separator, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Publish and package files must be outside the source repository.'
    }
    if (-not (Test-Path -LiteralPath $package -PathType Container)) { throw 'The publish directory was not found.' }
    foreach ($directory in @($repository, $package)) {
        for ($part = $directory; $part; $part = Split-Path -Parent $part) {
            if ((Test-Path -LiteralPath $part) -and
                ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw ('A package path contains a linked file or directory: ' + $part)
            }
        }
    }
    if (Test-Path -LiteralPath (Join-Path $repository '.git')) {
        $worktrees = @(& git --no-optional-locks -C $repository worktree list --porcelain -z)
        if ($LASTEXITCODE -ne 0) { throw 'The source repository checkout list could not be read.' }
        foreach ($record in (($worktrees -join "`n") -split "`0")) {
            if (-not $record.StartsWith('worktree ')) { continue }
            $checkout = [IO.Path]::GetFullPath($record.Substring(9)).TrimEnd('\', '/')
            if ($package.Equals($checkout, [StringComparison]::OrdinalIgnoreCase) -or
                $package.StartsWith($checkout + $separator, [StringComparison]::OrdinalIgnoreCase) -or
                $checkout.StartsWith($package + $separator, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Publish and package files must be outside every checkout of the source repository.'
            }
        }
    }
}

function Get-UpdatePackageFiles([string]$PackageDirectory) {
    $package = [IO.Path]::GetFullPath($PackageDirectory).TrimEnd('\', '/')
    $allowed = @('WinDock.Motion.manifest.json', 'WinDock.deps.json', 'WinDock.runtimeconfig.json', 'build-info.json', 'SHA256SUMS.txt',
        '托盘与菜单说明.md', '托盘兼容说明.md', '托盘输入法说明.md', '窗口预览说明.md', '整窗动画说明.md', '系统通知说明.md',
        'native-tray-actions.md', 'native-tray-inventory.md', 'licenses/WinDock-LICENSE.txt', 'licenses/THIRD_PARTY_NOTICES.txt',
        'licenses/MinHook-LICENSE.txt', 'licenses/YASB-LICENSE.txt', 'Install-Update.cmd',
        'updater/Install-LocalBuild.ps1', 'updater/Update-InstalledBuild.ps1', 'updater/UpdateSettings.ps1')
    foreach ($item in Get-ChildItem -LiteralPath $package -Recurse -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The package contains a linked file or directory.' }
        if ($item.PSIsContainer) { continue }
        $relative = [IO.Path]::GetRelativePath($package, $item.FullName).Replace('\', '/')
        if ($relative -notin $allowed -and $relative -notmatch '^WinDock(?:\.[A-Za-z0-9_-]+)*\.(?:exe|dll|pdb)(?:\.sha256)?$') {
            throw ('The package contains an unsupported installed file: ' + $relative)
        }
        [pscustomobject]@{ Relative = $relative; FullName = $item.FullName }
    }
}

function Add-UpdatePackageEntryPoints([string]$RepositoryDirectory, [string]$PackageDirectory) {
    Assert-UpdatePackageLocation $RepositoryDirectory $PackageDirectory
    $powerShell = [Environment]::ProcessPath
    if ($PSVersionTable.PSVersion.Major -lt 7 -or
        [IO.Path]::GetFileName($powerShell) -ine 'pwsh.exe' -or
        -not (Test-Path -LiteralPath $powerShell -PathType Leaf) -or
        (Get-Item -LiteralPath $powerShell).VersionInfo.FileMajorPart -lt 7) {
        throw 'Run packaging from an installed PowerShell 7 or later executable.'
    }
    $package = [IO.Path]::GetFullPath($PackageDirectory).TrimEnd('\', '/')
    $scripts = Join-Path ([IO.Path]::GetFullPath($RepositoryDirectory)) 'scripts'
    $null = @(Get-UpdatePackageFiles $package)
    if ((Get-Item -LiteralPath $scripts -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'The update source directory cannot be a directory link.'
    }
    $entries = @(foreach ($name in @('Install-LocalBuild.ps1', 'Update-InstalledBuild.ps1', 'UpdateSettings.ps1', 'Install-Update.cmd')) {
        $source = Join-Path $scripts $name
        $relative = if ($name -eq 'Install-Update.cmd') { $name } else { 'updater/' + $name }
        $target = Join-Path $package $relative
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw ('An update source file is missing: ' + $name) }
        if ((Get-Item -LiteralPath $source -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw ('An update source file is a link: ' + $name)
        }
        if ((Test-Path -LiteralPath $target) -and -not (Test-Path -LiteralPath $target -PathType Leaf)) {
            throw ('An update entry point is an existing directory: ' + $relative)
        }
        $content = $null
        $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
        if ($name -eq 'Install-Update.cmd') {
            $content = [IO.File]::ReadAllText($source)
            if (-not $content.Contains('__WINDOCK_POWERSHELL7_PATH__')) { throw 'The update launcher has no PowerShell path placeholder.' }
            $content = $content.Replace('__WINDOCK_POWERSHELL7_PATH__', $powerShell.Replace('%', '%%'))
            $bytes = [Text.UTF8Encoding]::new($false).GetBytes($content)
            $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
        }
        [pscustomobject]@{ Source = $source; Relative = $relative; Target = $target; Content = $content; Hash = $hash }
    })
    foreach ($entry in $entries) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $entry.Target) -Force | Out-Null
        if ($null -ne $entry.Content) { [IO.File]::WriteAllText($entry.Target, $entry.Content, [Text.UTF8Encoding]::new($false)) }
        else { Copy-Item -LiteralPath $entry.Source -Destination $entry.Target -Force }
        if ((Get-FileHash -LiteralPath $entry.Target -Algorithm SHA256).Hash -ne $entry.Hash) {
            throw ('An update entry point checksum does not match its source: ' + $entry.Relative)
        }
    }
}

function Complete-UpdatePackage([string]$RepositoryDirectory, [string]$PackageDirectory) {
    Assert-UpdatePackageLocation $RepositoryDirectory $PackageDirectory
    $package = [IO.Path]::GetFullPath($PackageDirectory).TrimEnd('\', '/')
    $archive = Join-Path (Split-Path -Parent $package) ((Split-Path -Leaf $package) + '-win-x64.zip')
    if ((Test-Path -LiteralPath $archive) -or (Test-Path -LiteralPath ($archive + '.sha256'))) {
        throw 'The archive or its checksum already exists; refusing to overwrite it.'
    }
    $files = @(Get-UpdatePackageFiles $package)
    foreach ($required in @('WinDock.exe', 'build-info.json', 'Install-Update.cmd', 'updater/Install-LocalBuild.ps1',
        'updater/Update-InstalledBuild.ps1', 'updater/UpdateSettings.ps1')) {
        if ($required -notin $files.Relative) { throw ('A required package file is missing: ' + $required) }
    }
    $checks = @($files | Where-Object Relative -ne 'SHA256SUMS.txt' | ForEach-Object {
        (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Relative
    } | Sort-Object)
    $manifest = Join-Path $package 'SHA256SUMS.txt'
    [IO.File]::WriteAllText($manifest, (($checks -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
    [IO.Compression.ZipFile]::CreateFromDirectory($package, $archive, [IO.Compression.CompressionLevel]::Optimal, $true)
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $prefix = (Split-Path -Leaf $package) + '/'
        $zipEntries = @{}
        foreach ($entry in $zip.Entries) {
            if (-not $entry.Name) { continue }
            $name = $entry.FullName.Replace('\', '/')
            if ($zipEntries.ContainsKey($name)) { throw 'Duplicate archive entry.' }
            $zipEntries[$name] = $entry
        }
        $verified = @($checks) + ((Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash.ToLowerInvariant() + '  SHA256SUMS.txt')
        if ($zipEntries.Count -ne $verified.Count) { throw 'Archive file count mismatch.' }
        foreach ($line in $verified) {
            $name = $line.Substring(66)
            $entry = $zipEntries[$prefix + $name]
            if (-not $entry) { throw ('Archive entry missing: ' + $name) }
            $stream = $entry.Open()
            try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
            finally { $stream.Dispose() }
            if ($hash -ine $line.Substring(0, 64)) { throw ('Archive checksum mismatch: ' + $name) }
        }
    }
    finally { $zip.Dispose() }
    $archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(($archive + '.sha256'), $archiveHash + "`n", [Text.UTF8Encoding]::new($false))
    return [pscustomobject]@{ archive = $archive; filesVerified = $verified.Count; sha256 = $archiveHash }
}
