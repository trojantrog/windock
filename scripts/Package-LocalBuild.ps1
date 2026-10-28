param([Parameter(Mandatory = $true)][string]$PublishPath)
$ErrorActionPreference = 'Stop'
$release = (Resolve-Path -LiteralPath $PublishPath).ProviderPath
$repository = Split-Path -Parent $PSScriptRoot
$commit = (git -C $repository rev-parse HEAD).Trim()
$version = ([xml](Get-Content -LiteralPath (Join-Path $repository 'src\WinDock\WinDock.csproj') -Raw)).Project.PropertyGroup.Version |
    Where-Object { $_ }
$product = (Get-Item -LiteralPath (Join-Path $release 'WinDock.exe')).VersionInfo.ProductVersion
if ($product -ne ($version + '+' + $commit)) { throw 'Published executable does not match the current source commit.' }
$motion = Get-Content -LiteralPath (Join-Path $release 'WinDock.Motion.manifest.json') -Raw | ConvertFrom-Json
foreach ($part in @($motion.payload, $motion.host)) {
    if ((Get-FileHash -LiteralPath (Join-Path $release $part.name)).Hash -ne $part.sha256) { throw 'Motion component checksum mismatch.' }
}
$tray = Join-Path $release 'WinDock.TrayHook.v7.x64.dll'
if ((Get-FileHash -LiteralPath $tray).Hash -ne ([IO.File]::ReadAllText($tray + '.sha256')).Trim()) {
    throw 'Tray component checksum mismatch.'
}
$signatures = @(foreach ($name in @('WinDock.exe', 'WinDock.Motion.x64.dll', 'WinDock.MotionHost.exe', 'WinDock.TrayHook.v7.x64.dll')) {
    $file = Join-Path $release $name
    $bytes = [IO.File]::ReadAllBytes($file)
    $header = [BitConverter]::ToInt32($bytes, 0x3c)
    if ([BitConverter]::ToUInt16($bytes, $header + 4) -ne 0x8664) { throw ('Unexpected executable architecture: ' + $name) }
    [ordered]@{ file = $name; status = [string](Get-AuthenticodeSignature -LiteralPath $file).Status }
})
$info = [ordered]@{
    version = $version; sourceCommit = $commit; productVersion = $product
    createdAt = [DateTimeOffset]::Now.ToString('o'); nativeMotionBuild = $motion.build
    sourceRepository = 'https://github.com/trojantrog/windock'
    validation = [ordered]@{ componentHashes = 'matched'; functionalTests = 'not run'; desktopTests = 'not run' }
    signatures = $signatures
}
[IO.File]::WriteAllText((Join-Path $release 'build-info.json'), ($info | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
$checks = @(Get-ChildItem -LiteralPath $release -Recurse -File | Where-Object Name -ne 'SHA256SUMS.txt' | ForEach-Object {
    (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetRelativePath($release, $_.FullName).Replace('\', '/')
} | Sort-Object)
[IO.File]::WriteAllText((Join-Path $release 'SHA256SUMS.txt'), (($checks -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
$archive = Join-Path (Split-Path -Parent $release) ((Split-Path -Leaf $release) + '-win-x64.zip')
if (Test-Path -LiteralPath $archive) { throw 'Archive already exists; refusing to overwrite it.' }
Compress-Archive -LiteralPath $release -DestinationPath $archive -CompressionLevel Optimal
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    $prefix = (Split-Path -Leaf $release) + '/'
    $zipEntries = @{}
    foreach ($entry in $zip.Entries) {
        if (-not $entry.Name) { continue }
        $name = $entry.FullName.Replace('\', '/')
        if ($zipEntries.ContainsKey($name)) { throw 'Duplicate archive entry.' }
        $zipEntries[$name] = $entry
    }
    if ($zipEntries.Count -ne $checks.Count + 1) { throw 'Archive file count mismatch.' }
    $checks += (Get-FileHash -LiteralPath (Join-Path $release 'SHA256SUMS.txt')).Hash.ToLowerInvariant() + '  SHA256SUMS.txt'
    foreach ($line in $checks) {
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
$archiveHash = (Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(($archive + '.sha256'), $archiveHash + "`n", [Text.UTF8Encoding]::new($false))
[ordered]@{ archive = $archive; version = $product; filesVerified = $checks.Count; sha256 = $archiveHash } | ConvertTo-Json
