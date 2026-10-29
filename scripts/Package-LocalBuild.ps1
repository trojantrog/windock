param([Parameter(Mandatory = $true)][string]$PublishPath)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'UpdatePackage.ps1')
$release = (Resolve-Path -LiteralPath $PublishPath).ProviderPath
$repository = Split-Path -Parent $PSScriptRoot
Assert-UpdatePackageLocation $repository $release
$sourceChanges = @(git --no-optional-locks -C $repository status --porcelain --untracked-files=no)
if ($LASTEXITCODE -ne 0 -or $sourceChanges.Count) { throw 'Commit tracked source changes before packaging a local build.' }
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
Add-UpdatePackageEntryPoints $repository $release
[IO.File]::WriteAllText((Join-Path $release 'build-info.json'), ($info | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
$package = Complete-UpdatePackage $repository $release
[ordered]@{ archive = $package.archive; version = $product; filesVerified = $package.filesVerified; sha256 = $package.sha256 } | ConvertTo-Json
