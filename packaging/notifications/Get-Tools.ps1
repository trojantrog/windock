#requires -Version 7.0
[CmdletBinding()]
param([string]$CacheDirectory = (Join-Path $PSScriptRoot 'bin\sdk-tools'))
. (Join-Path $PSScriptRoot 'Packaging.ps1')
$lock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'sdk-tools.lock.json') -Raw | ConvertFrom-Json
$cache = [IO.Path]::GetFullPath($CacheDirectory)
New-Item -ItemType Directory -Path $cache -Force | Out-Null
$archive = Join-Path $cache ("microsoft.windows.sdk.buildtools.$($lock.version).nupkg")
if (-not (Test-Path -LiteralPath $archive)) {
    $download = Join-Path $cache ([Guid]::NewGuid().ToString('N') + '.download')
    Invoke-WebRequest -Uri $lock.source -OutFile $download
    if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ine $lock.sha256) { throw 'Windows SDK package checksum mismatch.' }
    Move-Item -LiteralPath $download -Destination $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ine $lock.sha256) { throw 'Cached Windows SDK package checksum mismatch.' }
$destination = Join-Path $cache $lock.version
if (-not (Test-Path -LiteralPath $destination)) { [IO.Compression.ZipFile]::ExtractToDirectory($archive, $destination) }
$tools = Join-Path $destination $lock.tools
foreach ($name in @('makeappx.exe', 'signtool.exe', 'mt.exe')) { $null = Resolve-NotificationSdkTool $tools $name }
return $tools
