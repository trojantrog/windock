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
