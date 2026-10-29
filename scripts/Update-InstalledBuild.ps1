param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [switch]$EnableAnimationStartup,
    [string]$BackupRoot = (Join-Path $env:LOCALAPPDATA 'WinDock\Backups')
)
$ErrorActionPreference = 'Stop'
$destination = 'D:\Projects\windock'
& (Join-Path $PSScriptRoot 'Install-LocalBuild.ps1') -PackagePath $PackagePath -DestinationPath $destination `
    -BackupRoot $BackupRoot -EnableAnimationStartup:$EnableAnimationStartup
exit $LASTEXITCODE
