#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$BundleDirectory,
    [Parameter(Mandatory=$true)][ValidatePattern('^[a-fA-F0-9]{40}$')][string]$CertificateThumbprint,
    [Parameter(Mandatory=$true)][string]$SdkToolsDirectory,
    [ValidateSet('CurrentUser', 'LocalMachine')][string]$CertificateStore = 'CurrentUser',
    [Uri]$TimestampServer
)
. (Join-Path $PSScriptRoot 'Packaging.ps1')
$bundle = Get-NotificationBundle $BundleDirectory
if ($bundle.Record.signed) { throw 'This bundle is already marked as signed. Build a new bundle for re-signing.' }
if ((Get-FileHash -LiteralPath $bundle.Package -Algorithm SHA256).Hash -ine $bundle.Record.packageSha256 -or
    (Get-FileHash -LiteralPath $bundle.Executable -Algorithm SHA256).Hash -ine $bundle.Record.executableSha256) { throw 'Unsigned bundle files have changed.' }
$identity = Get-NotificationIdentity (Get-NotificationPackageManifest $bundle.Package)
$certificate = Get-Item -LiteralPath "Cert:\$CertificateStore\My\$CertificateThumbprint"
$purposes = @($certificate.Extensions | Where-Object { $_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] } |
    ForEach-Object { $_.EnhancedKeyUsages } | ForEach-Object { $_.Value })
$keyUsage = @($certificate.Extensions | Where-Object { $_ -is [Security.Cryptography.X509Certificates.X509KeyUsageExtension] })
$constraints = @($certificate.Extensions | Where-Object { $_ -is [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension] })
if (-not $certificate.HasPrivateKey -or $certificate.Subject -cne $identity.Publisher -or
    $certificate.NotBefore -gt [DateTime]::Now -or $certificate.NotAfter -le [DateTime]::Now -or
    -not $purposes.Contains('1.3.6.1.5.5.7.3.3') -or $keyUsage.Count -ne 1 -or
    -not ($keyUsage[0].KeyUsages -band [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature) -or
    @($constraints | Where-Object { $_.CertificateAuthority }).Count -gt 0) {
    throw 'Select a current code-signing certificate with a private key and a Subject exactly matching the package Publisher.'
}
$signtool = Resolve-NotificationSdkTool $SdkToolsDirectory 'signtool.exe'
$mt = Resolve-NotificationSdkTool $SdkToolsDirectory 'mt.exe'
Assert-NotificationExeIdentity $bundle.Executable $mt $identity (Join-Path $bundle.Root 'manifests\before-sign.manifest')
$signedRelative = 'signed-' + [Guid]::NewGuid().ToString('N')
$signedRoot = Join-Path $bundle.Root $signedRelative
New-Item -ItemType Directory -Path $signedRoot | Out-Null
$signedApp = Join-Path $signedRoot 'app'
Copy-Item -LiteralPath (Split-Path -Parent $bundle.Executable) -Destination $signedApp -Recurse
$signedName = [IO.Path]::GetFileName($bundle.Package).Replace('.unsigned.msix', '.msix')
$signedPackage = Join-Path $signedRoot $signedName
Copy-Item -LiteralPath $bundle.Package -Destination $signedPackage
$signedExecutable = Join-Path $signedApp 'WinDock.exe'
$arguments = @('sign', '/fd', 'SHA256', '/sha1', $CertificateThumbprint, '/s', 'My')
if ($CertificateStore -eq 'LocalMachine') { $arguments += '/sm' }
if ($null -ne $TimestampServer) {
    if ($TimestampServer.Scheme -notin @('http', 'https')) { throw 'TimestampServer must be an HTTP or HTTPS RFC 3161 endpoint.' }
    $arguments += @('/tr', $TimestampServer.AbsoluteUri, '/td', 'SHA256')
}
Invoke-NotificationTool $signtool ($arguments + $signedExecutable)
Invoke-NotificationTool $signtool ($arguments + $signedPackage)
foreach ($file in @($signedExecutable, $signedPackage)) {
    Invoke-NotificationTool $signtool @('verify', '/pa', '/all', $file)
    $null = Assert-NotificationTrustedSignature $file $identity.Publisher
}
$bundle.Package = $signedPackage
$bundle.Executable = $signedExecutable
$bundle.Record.package = Join-Path $signedRelative $signedName
$bundle.Record.executable = Join-Path $signedRelative 'app\WinDock.exe'
$bundle.Record.signed = $true
Write-NotificationBundleRecord $bundle
Write-Output (Join-Path $bundle.Root 'notification-package.json')
