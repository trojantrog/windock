param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [switch]$EnableAnimationStartup,
    [string]$BackupRoot = (Join-Path $env:LOCALAPPDATA 'WinDock\Backups')
)
$ErrorActionPreference = 'Stop'
$destination = 'D:\WinDock'
if (-not (Test-Path -LiteralPath (Join-Path $destination 'WinDock.exe') -PathType Leaf)) {
    throw 'The installed WinDock executable was not found at D:\WinDock\WinDock.exe.'
}
& (Join-Path $PSScriptRoot 'Install-LocalBuild.ps1') -PackagePath $PackagePath -DestinationPath $destination `
    -BackupRoot $BackupRoot -EnableAnimationStartup:$EnableAnimationStartup
exit $LASTEXITCODE
