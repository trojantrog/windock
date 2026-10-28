#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$BaseExecutable,
    [Parameter(Mandatory=$true)][string]$Publisher,
    [Parameter(Mandatory=$true)][Version]$PackageVersion,
    [Parameter(Mandatory=$true)][string]$SdkToolsDirectory,
    [string]$PublisherDisplayName = 'WinDock',
    [string]$Dotnet = 'dotnet',
    [string]$OutputDirectory
)
. (Join-Path $PSScriptRoot 'Packaging.ps1')
if ($PackageVersion.Revision -lt 0 -or @($PackageVersion.Major, $PackageVersion.Minor, $PackageVersion.Build, $PackageVersion.Revision).Where({ $_ -gt 65535 }).Count) {
    throw 'PackageVersion must contain four numbers in the range 0..65535.'
}
if ([string]::IsNullOrWhiteSpace($Publisher) -or [string]::IsNullOrWhiteSpace($PublisherDisplayName)) { throw 'Publisher values cannot be empty.' }
$null = [Security.Cryptography.X509Certificates.X500DistinguishedName]::new($Publisher)
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$base = (Get-Item -LiteralPath $BaseExecutable -ErrorAction Stop).FullName
if ([IO.Path]::GetFileName($base) -ine 'WinDock.exe') { throw 'BaseExecutable must be an existing WinDock.exe build.' }
$mt = Resolve-NotificationSdkTool $SdkToolsDirectory 'mt.exe'
$makeappx = Resolve-NotificationSdkTool $SdkToolsDirectory 'makeappx.exe'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repo ('publish\notifications\' + $PackageVersion + '-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss'))
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw "Output directory already exists; choose a new directory: $output" }
$app = Join-Path $output 'app'
$manifestRoot = Join-Path $output 'manifests'
$packageRoot = Join-Path $output 'package-content'
foreach ($path in @($app, $manifestRoot, $packageRoot)) { New-Item -ItemType Directory -Path $path -Force | Out-Null }

$baseManifest = Join-Path $manifestRoot 'original-app.manifest'
Invoke-NotificationTool $mt @('-nologo', "-inputresource:$base;#1", "-out:$baseManifest")
$applicationManifest = Read-NotificationXml $baseManifest
$level = $applicationManifest.SelectSingleNode("//*[local-name()='requestedExecutionLevel']")
if ($null -eq $level -or $level.GetAttribute('level') -ne 'asInvoker' -or $level.GetAttribute('uiAccess') -ne 'false') {
    throw 'The base WinDock executable must use asInvoker with uiAccess=false.'
}
$msix = $applicationManifest.SelectSingleNode("/*[local-name()='assembly']/*[local-name()='msix' and namespace-uri()='urn:schemas-microsoft-com:msix.v1']")
if ($null -eq $msix) {
    $template = Read-NotificationXml (Join-Path $PSScriptRoot 'WinDock.manifest.template.xml')
    $node = $template.SelectSingleNode("/*[local-name()='assembly']/*[local-name()='msix']")
    $msix = $applicationManifest.ImportNode($node, $true)
    $null = $applicationManifest.DocumentElement.AppendChild($msix)
}
$msix.SetAttribute('publisher', $Publisher)
$msix.SetAttribute('packageName', 'WinDock.NotificationAccess')
$msix.SetAttribute('applicationId', 'WinDock')
$appManifestPath = Join-Path $manifestRoot 'WinDock.manifest'
Write-NotificationXml $applicationManifest $appManifestPath
Invoke-NotificationTool $mt @('-nologo', '-validate_manifest', '-manifest', $appManifestPath)

$manifest = Read-NotificationXml (Join-Path $PSScriptRoot 'AppxManifest.template.xml')
$identityNode = $manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']")
$identityNode.SetAttribute('Publisher', $Publisher)
$identityNode.SetAttribute('Version', $PackageVersion.ToString())
$manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Properties']/*[local-name()='PublisherDisplayName']").InnerText = $PublisherDisplayName
$manifestPath = Join-Path $packageRoot 'AppxManifest.xml'
Write-NotificationXml $manifest $manifestPath
$identity = Get-NotificationIdentity $manifest

# Embed the identity during compilation, before the .NET single-file bundle is
# created. Editing a published bundle with a PE resource editor is not supported here.
$project = Join-Path $repo 'src\WinDock\WinDock.csproj'
Invoke-NotificationTool $Dotnet @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--nologo',
    '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
    "-p:ApplicationManifest=$appManifestPath", "-p:IntermediateOutputPath=$(Join-Path $output 'build-obj')\",
    "-p:OutputPath=$(Join-Path $output 'build-bin')\", '-o', $app)
$executable = Join-Path $app 'WinDock.exe'
Assert-NotificationExeIdentity $executable $mt $identity (Join-Path $manifestRoot 'published-app.manifest')

# Use the existing WinDock artwork for the system consent prompt and package
# metadata. No new logo or application UI is introduced by the identity package.
Add-Type -AssemblyName System.Drawing.Common
$icon = [Drawing.Icon]::new((Join-Path $repo 'src\WinDock\Assets\app.ico'), 256, 256)
$source = $icon.ToBitmap()
try {
    foreach ($entry in @(@('StoreLogo.png', 50), @('Square44x44Logo.png', 44), @('Square150x150Logo.png', 150))) {
        $bitmap = [Drawing.Bitmap]::new([int]$entry[1], [int]$entry[1], [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([Drawing.Color]::Transparent)
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($source, 0, 0, $bitmap.Width, $bitmap.Height)
            foreach ($directory in @($app, $packageRoot)) {
                $assets = Join-Path $directory 'Assets'
                New-Item -ItemType Directory -Path $assets -Force | Out-Null
                $bitmap.Save((Join-Path $assets $entry[0]), [Drawing.Imaging.ImageFormat]::Png)
            }
        }
        finally { $graphics.Dispose(); $bitmap.Dispose() }
    }
}
finally { $source.Dispose(); $icon.Dispose() }

$packageName = 'WinDock.NotificationAccess_' + $PackageVersion + '_neutral.unsigned.msix'
$package = Join-Path $output $packageName
# /nv is the documented external-location packaging mode: WinDock.exe resides
# outside this MSIX. Package signing and Windows trust validation remain required.
Invoke-NotificationTool $makeappx @('pack', '/d', $packageRoot, '/p', $package, '/nv')
$packedIdentity = Get-NotificationIdentity (Get-NotificationPackageManifest $package)
if ($packedIdentity.Publisher -cne $Publisher -or $packedIdentity.Version -ne $PackageVersion.ToString()) { throw 'Packed identity does not match the build request.' }
$record = [ordered]@{
    schema=1; name=$identity.Name; publisher=$identity.Publisher; version=$identity.Version; applicationId=$identity.ApplicationId
    package=$packageName; executable='app\WinDock.exe'; packageSha256=''; executableSha256=''; signed=$false
    builtAt=[DateTimeOffset]::Now.ToString('o'); platform='win-x64'; sdkToolsVersion=(Get-Item -LiteralPath $makeappx).VersionInfo.FileVersion
}
$bundle = [pscustomobject]@{ Root=$output; Record=$record; Package=$package; Executable=$executable }
Write-NotificationBundleRecord $bundle
$setup = Join-Path $output 'setup'
New-Item -ItemType Directory -Path $setup | Out-Null
foreach ($name in @('Install.ps1', 'Remove.ps1', 'Packaging.ps1', 'README.md')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $setup $name)
}
Write-Output (Join-Path $output 'notification-package.json')
