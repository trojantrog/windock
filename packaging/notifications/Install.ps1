#requires -Version 7.0
[CmdletBinding(SupportsShouldProcess=$true, ConfirmImpact='Medium')]
param(
    [Parameter(Mandatory=$true)][string]$BundleDirectory,
    [Parameter(Mandatory=$true)][string]$ExternalLocation
)
. (Join-Path $PSScriptRoot 'Packaging.ps1')
$bundle = Get-NotificationBundle $BundleDirectory
if (-not $bundle.Record.signed) { throw 'This bundle is unsigned. Sign it with a trusted code-signing certificate before registration.' }
if ([Environment]::OSVersion.Version.Build -lt 19041) { throw 'Notification identity requires Windows 10 version 2004 or later.' }
$external = (Get-Item -LiteralPath $ExternalLocation -ErrorAction Stop).FullName.TrimEnd('\')
$executable = Join-Path $external 'WinDock.exe'
if ((Get-FileHash -LiteralPath $bundle.Package -Algorithm SHA256).Hash -ine $bundle.Record.packageSha256 -or
    (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash -ine $bundle.Record.executableSha256) {
    throw 'The signed package or installed WinDock.exe differs from the prepared bundle. Install the matching app directory first.'
}
$identity = Get-NotificationIdentity (Get-NotificationPackageManifest $bundle.Package)
if ($identity.Publisher -cne $bundle.Record.publisher -or $identity.Version -ne $bundle.Record.version) { throw 'Package identity differs from the bundle descriptor.' }
$packageSigner = Assert-NotificationTrustedSignature $bundle.Package $identity.Publisher
$appSigner = Assert-NotificationTrustedSignature $executable $identity.Publisher
if ($packageSigner -ne $appSigner) { throw 'Package and executable must use the same signing certificate.' }
Assert-NotificationExternalAssets $bundle.Package $external
$installed = @(Get-AppxPackage -Name 'WinDock.NotificationAccess' | Where-Object { $_.Name -eq 'WinDock.NotificationAccess' })
foreach ($package in $installed) {
    if ($package.Publisher -cne $identity.Publisher) { throw 'A WinDock identity from another publisher is already registered; review it before proceeding.' }
    if ([Version]$package.Version -ge [Version]$identity.Version) { throw 'This identity version or a newer one is already installed. Build a higher package version to update it.' }
}
if ($PSCmdlet.ShouldProcess($external, "Register signed WinDock notification identity $($identity.Version) for the current user")) {
    Add-AppxPackage -Path $bundle.Package -ExternalLocation $external -ErrorAction Stop
    $registered = @(Get-AppxPackage -Name $identity.Name | Where-Object { $_.Publisher -ceq $identity.Publisher -and [Version]$_.Version -eq [Version]$identity.Version })
    if ($registered.Count -ne 1 -or $registered[0].Status -ne 'Ok' -or $registered[0].IsDevelopmentMode) { throw 'Windows did not report a healthy signed package registration.' }
    [pscustomobject]@{ PackageFullName=$registered[0].PackageFullName; ExternalLocation=$external; NextWinDockStartRequired=$true; NotificationAccessRequested=$false }
}
