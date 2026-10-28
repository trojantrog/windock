#requires -Version 7.0
[CmdletBinding(SupportsShouldProcess=$true, ConfirmImpact='Medium')]
param([Parameter(Mandatory=$true)][string]$Publisher)
. (Join-Path $PSScriptRoot 'Packaging.ps1')
$packages = @(Get-AppxPackage -Name 'WinDock.NotificationAccess' | Where-Object { $_.Name -eq 'WinDock.NotificationAccess' -and $_.Publisher -ceq $Publisher })
foreach ($package in $packages) {
    if ($PSCmdlet.ShouldProcess($package.PackageFullName, 'Remove WinDock notification package identity for the current user')) {
        Remove-AppxPackage -Package $package.PackageFullName -ErrorAction Stop
    }
}
# The external application, its settings, native helpers and other packages are
# not removed. No certificate-store entry is imported, changed or deleted.
