function Read-UpdateSettings([string]$Path) {
    $options = [Text.Json.JsonDocumentOptions]::new()
    $options.AllowTrailingCommas = $true
    $options.CommentHandling = [Text.Json.JsonCommentHandling]::Skip
    $document = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($Path), $options)
    try {
        if ($document.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object) {
            throw 'The existing settings must contain a JSON object.'
        }
        return $document.RootElement.Clone()
    }
    finally { $document.Dispose() }
}

function Assert-UpdateSettingsPreserved([Text.Json.JsonElement]$Before, [Text.Json.JsonElement]$After, [string]$Path = 'settings') {
    if ($Before.ValueKind -ne $After.ValueKind) { throw ('A saved setting changed type: ' + $Path) }
    switch ($Before.ValueKind) {
        Object {
            foreach ($property in $Before.EnumerateObject()) {
                $current = [Text.Json.JsonElement]::new()
                if (-not $After.TryGetProperty($property.Name, [ref]$current)) { throw ('A saved setting is missing: ' + $Path + '.' + $property.Name) }
                Assert-UpdateSettingsPreserved $property.Value $current ($Path + '.' + $property.Name)
            }
        }
        Array {
            if ($Before.GetArrayLength() -ne $After.GetArrayLength()) { throw ('A saved list changed length: ' + $Path) }
            for ($index = 0; $index -lt $Before.GetArrayLength(); $index++) {
                Assert-UpdateSettingsPreserved $Before[$index] $After[$index] ($Path + '[' + $index + ']')
            }
        }
        Number {
            $oldNumber = [decimal]0
            $newNumber = [decimal]0
            $same = if ($Before.TryGetDecimal([ref]$oldNumber) -and $After.TryGetDecimal([ref]$newNumber)) {
                $oldNumber -eq $newNumber
            } else { $Before.GetDouble() -eq $After.GetDouble() }
            if (-not $same) { throw ('A saved number changed: ' + $Path) }
        }
        String {
            if ($Before.GetString() -cne $After.GetString()) { throw ('A saved value changed: ' + $Path) }
        }
        { $_ -in @([Text.Json.JsonValueKind]::True, [Text.Json.JsonValueKind]::False) } {
            if ($Before.GetBoolean() -ne $After.GetBoolean()) { throw ('A saved option changed: ' + $Path) }
        }
        Null { }
        default { throw ('An unsupported saved value was found: ' + $Path) }
    }
}

function Get-UpdateUserDataFiles([string]$Directory) {
    $root = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw 'The existing WinDock user directory was not found.' }
    for ($part = $root; $part; $part = Split-Path -Parent $part) {
        if ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'The user data path contains a directory link.'
        }
    }
    foreach ($item in Get-ChildItem -LiteralPath $root -Recurse -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The user data contains a linked file or directory.' }
        if ($item.PSIsContainer) { continue }
        [pscustomobject]@{
            Relative = [IO.Path]::GetRelativePath($root, $item.FullName)
            Hash = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
        }
    }
}

function Backup-UpdateUserData([string]$Directory, [string]$BackupDirectory) {
    $root = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
    $backup = [IO.Path]::GetFullPath($BackupDirectory).TrimEnd('\', '/')
    if ($root.Equals($backup, [StringComparison]::OrdinalIgnoreCase) -or
        $backup.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        $root.StartsWith($backup + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The user data and its backup must be separate directories.'
    }
    if (Test-Path -LiteralPath $backup) { throw 'The user data backup destination already exists.' }
    $files = @(Get-UpdateUserDataFiles $root)
    $settingsPath = Join-Path $root 'settings.json'
    try { $settings = Read-UpdateSettings $settingsPath }
    catch {
        $settingsPath += '.bak'
        $settings = Read-UpdateSettings $settingsPath
    }
    New-Item -ItemType Directory -Path $backup | Out-Null
    foreach ($file in $files) {
        $target = Join-Path $backup $file.Relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $root $file.Relative) -Destination $target
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -cne $file.Hash) {
            throw ('User data backup checksum mismatch: ' + $file.Relative)
        }
    }
    return [pscustomobject]@{ Directory = $root; BackupDirectory = $backup; Files = $files; Settings = $settings }
}

function Assert-UpdateUserDataUnchanged($Snapshot) {
    $current = @(Get-UpdateUserDataFiles $Snapshot.Directory)
    if ($current.Count -ne $Snapshot.Files.Count) { throw 'The user data changed during the update.' }
    $hashes = @{}
    foreach ($file in $current) { $hashes[$file.Relative] = $file.Hash }
    foreach ($file in $Snapshot.Files) {
        if ($hashes[$file.Relative] -cne $file.Hash) { throw ('User data changed during the update: ' + $file.Relative) }
    }
}

function Assert-UpdateUserDataPreserved($Snapshot) {
    $settingsPath = Join-Path $Snapshot.Directory 'settings.json'
    try { $settings = Read-UpdateSettings $settingsPath }
    catch { $settings = Read-UpdateSettings ($settingsPath + '.bak') }
    Assert-UpdateSettingsPreserved $Snapshot.Settings $settings
    foreach ($file in $Snapshot.Files) {
        if (-not $file.Relative.StartsWith('presets' + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { continue }
        $path = Join-Path $Snapshot.Directory $file.Relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $file.Hash) {
            throw ('A saved environment changed during startup: ' + $file.Relative)
        }
    }
}

function Assert-UpdatePackageResourceBoundaries($Entries, [string]$Directory) {
    $root = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
    $tracked = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    if (Test-Path -LiteralPath (Join-Path $root '.git')) {
        $git = @(Get-Command git -CommandType Application -ErrorAction Stop)[0].Source
        $trackedOutput = & $git --no-optional-locks -C $root ls-files --cached -z
        if ($LASTEXITCODE -ne 0) { throw 'The source repository file list could not be read.' }
        foreach ($name in (($trackedOutput -join "`n") -split "`0")) {
            if ($name) { [void]$tracked.Add($name.Replace('\', '/')) }
        }
    }
    $allowedNames = @('WinDock.Motion.manifest.json', 'WinDock.deps.json', 'WinDock.runtimeconfig.json', 'build-info.json', 'SHA256SUMS.txt',
        '托盘与菜单说明.md', '托盘兼容说明.md', '托盘输入法说明.md', '窗口预览说明.md', '整窗动画说明.md', '系统通知说明.md',
        'native-tray-actions.md', 'native-tray-inventory.md', 'licenses/WinDock-LICENSE.txt', 'licenses/THIRD_PARTY_NOTICES.txt',
        'licenses/MinHook-LICENSE.txt', 'licenses/YASB-LICENSE.txt')
    foreach ($entry in $Entries) {
        $target = [IO.Path]::GetFullPath($entry.Target)
        if (-not $target.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'A package file is outside the installation directory.'
        }
        $relative = [IO.Path]::GetRelativePath($root, $target).Replace('\', '/')
        if (-not $relative.Equals($entry.Relative.Replace('\', '/'), [StringComparison]::OrdinalIgnoreCase)) {
            throw 'A package path must use its direct relative location.'
        }
        $topDirectory = ($relative -split '/', 2)[0]
        if ($topDirectory -in @('shortcuts', 'icons')) {
            throw ('The package would overwrite an installed user resource: ' + $relative)
        }
        if ($topDirectory -in @('.git', '.github', 'src', 'scripts', 'docs', 'tests', 'native', 'third_party', 'publish', 'obj', 'bin') -or
            $tracked.Contains($relative)) {
            throw ('The package would overwrite a source repository file: ' + $relative)
        }
        if ($relative -notin $allowedNames -and $relative -notmatch '^WinDock(?:\.[A-Za-z0-9_-]+)*\.(?:exe|dll|pdb)(?:\.sha256)?$') {
            throw ('The package contains an unsupported installed file: ' + $relative)
        }
        if ((Test-Path -LiteralPath $target) -and -not (Test-Path -LiteralPath $target -PathType Leaf)) {
            throw ('A package target is an existing directory: ' + $relative)
        }
    }
}

function Backup-UpdateInstalledFiles($Entries, [string]$Directory, [string]$BackupDirectory, $Resources) {
    $root = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
    $backup = [IO.Path]::GetFullPath($BackupDirectory).TrimEnd('\', '/')
    if ($root.Equals($backup, [StringComparison]::OrdinalIgnoreCase) -or
        $backup.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        $root.StartsWith($backup + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The installation and its backup must be separate directories.'
    }
    Assert-UpdatePackageResourceBoundaries $Entries $root
    if (Test-Path -LiteralPath $backup) { throw 'The installation backup destination already exists.' }
    $files = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $Entries) {
        if (-not (Test-Path -LiteralPath $entry.Target -PathType Leaf)) { continue }
        $relative = [IO.Path]::GetRelativePath($root, $entry.Target)
        $hash = (Get-FileHash -LiteralPath $entry.Target -Algorithm SHA256).Hash
        if ($hash -eq $entry.Hash) { continue }
        $files[$relative] = [pscustomobject]@{ Relative = $relative; Hash = $hash }
    }
    foreach ($file in $Resources.Files) { $files[$file.Relative] = $file }
    New-Item -ItemType Directory -Path $backup | Out-Null
    foreach ($file in $files.Values) {
        $source = [IO.Path]::GetFullPath((Join-Path $root $file.Relative))
        $saved = [IO.Path]::GetFullPath((Join-Path $backup $file.Relative))
        if (-not $source.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
            -not $saved.StartsWith($backup + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'An installation backup file is outside its directory.'
        }
        for ($part = $source; $part; $part = Split-Path -Parent $part) {
            if ((Test-Path -LiteralPath $part) -and ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw 'An installation backup path contains a linked file or directory.'
            }
        }
        New-Item -ItemType Directory -Path (Split-Path -Parent $saved) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $saved
        if ((Get-FileHash -LiteralPath $saved -Algorithm SHA256).Hash -cne $file.Hash) {
            throw ('Installation backup checksum mismatch: ' + $file.Relative)
        }
    }
    return @($files.Values)
}

function Get-UpdateInstalledResources([string]$Directory) {
    $root = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
    $files = @(foreach ($name in @('shortcuts', 'icons')) {
        $folder = Join-Path $root $name
        if (-not (Test-Path -LiteralPath $folder)) { continue }
        if (-not (Test-Path -LiteralPath $folder -PathType Container)) { throw ('An installed resource directory is a file: ' + $name) }
        foreach ($file in Get-UpdateUserDataFiles $folder) {
            [pscustomobject]@{ Relative = Join-Path $name $file.Relative; Hash = $file.Hash }
        }
    })
    return [pscustomobject]@{ Directory = $root; Files = $files }
}

function Assert-UpdateInstalledResourcesPreserved($Snapshot) {
    $current = Get-UpdateInstalledResources $Snapshot.Directory
    $hashes = @{}
    foreach ($file in $current.Files) { $hashes[$file.Relative] = $file.Hash }
    foreach ($file in $Snapshot.Files) {
        if ($hashes[$file.Relative] -cne $file.Hash) {
            throw ('An installed user resource changed: ' + $file.Relative)
        }
    }
}

function Copy-UpdatePackageFiles($Entries, [Collections.Generic.List[object]]$Copied) {
    foreach ($entry in $Entries) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $entry.Target) -Force | Out-Null
        if ((Test-Path -LiteralPath $entry.Target) -and
            (Get-FileHash -LiteralPath $entry.Target -Algorithm SHA256).Hash -eq $entry.Hash) { continue }
        $Copied.Add($entry)
        Copy-Item -LiteralPath $entry.Source -Destination $entry.Target -Force
        if ((Get-FileHash -LiteralPath $entry.Target -Algorithm SHA256).Hash -ne $entry.Hash) {
            throw ('Installed file checksum mismatch: ' + $entry.Relative)
        }
    }
}

function Get-UpdateStartupPlan($Before, [string]$Executable) {
    $command = '"' + [IO.Path]::GetFullPath($Executable) + '" --tray'
    $recognized = $Before.registered -eq $true -and $Before.runValue -is [string] -and
        $Before.runKind -in @('String', 'ExpandString') -and
        $Before.runValue -match '^\s*(?:"[^"\r\n]*[\\/]WinDock\.exe"|[^"\s]+[\\/]WinDock\.exe)(?:\s+--tray)?\s*$'
    $write = $Before.readable -eq $true -and $recognized -and $Before.runValue -ine $command
    return [pscustomobject]@{
        Before = $Before; Write = $write; Kind = $Before.runKind
        Command = if ($write) { $command } else { $Before.runValue }
        Action = if ($write) { 'relocateExisting' } elseif ($Before.registered -eq $false) { 'keepAbsent' }
            elseif (-not $Before.readable) { 'keepUnreadable' } elseif (-not $recognized) { 'keepUnrecognized' } else { 'keepCurrent' }
    }
}

function Invoke-UpdateStartupPlan($Plan, [scriptblock]$WriteCommand) {
    if ($Plan.Write) { & $WriteCommand $Plan.Command $Plan.Kind }
}

function Assert-UpdateStartupUnchanged($Before, $Current) {
    $plan = [pscustomobject]@{ Before = $Before; Command = $Before.runValue; Kind = $Before.runKind }
    Assert-UpdateStartupPreserved $plan $Current
}

function Assert-UpdateStartupPreserved($Plan, $After) {
    foreach ($field in @('readable', 'registered', 'approvalReadable', 'approvalPresent', 'approvalRecord', 'windowsApproval', 'runPolicy')) {
        $beforeJson = ConvertTo-Json -InputObject $Plan.Before.$field -Depth 10 -Compress
        $afterJson = ConvertTo-Json -InputObject $After.$field -Depth 10 -Compress
        if ($beforeJson -cne $afterJson) { throw ('The sign-in startup state changed: ' + $field) }
    }
    if (-not $After.readable -or -not $After.approvalReadable -or $After.runPolicy -eq 'unknown') {
        throw 'The sign-in startup choice could not be confirmed.'
    }
    if ((ConvertTo-Json -InputObject $Plan.Command -Depth 10 -Compress) -cne
        (ConvertTo-Json -InputObject $After.runValue -Depth 10 -Compress) -or $Plan.Kind -cne $After.runKind) {
        throw 'The sign-in startup command was not preserved.'
    }
}
