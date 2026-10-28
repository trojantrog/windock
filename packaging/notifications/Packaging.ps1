#requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-NotificationSdkTool([string]$Directory, [string]$Name) {
    $path = Join-Path ([IO.Path]::GetFullPath($Directory)) $Name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing Windows SDK tool: $path" }
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(?:^|,\s*)O=Microsoft Corporation(?:,|$)') {
        throw "Windows SDK tool does not have a valid Microsoft signature: $path"
    }
    return $path
}

function Invoke-NotificationTool([string]$Path, [string[]]$Arguments) {
    & $Path @Arguments | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "$([IO.Path]::GetFileName($Path)) failed with exit code $LASTEXITCODE." }
}

function Read-NotificationXml([string]$Path) {
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($Path, $settings)
    try {
        $document = [Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load($reader)
        return ,$document
    }
    finally { $reader.Dispose() }
}

function Write-NotificationXml([Xml.XmlDocument]$Document, [string]$Path) {
    $settings = [Xml.XmlWriterSettings]::new()
    $settings.Encoding = [Text.UTF8Encoding]::new($false)
    $settings.Indent = $true
    $writer = [Xml.XmlWriter]::Create($Path, $settings)
    try { $Document.Save($writer) }
    finally { $writer.Dispose() }
}

function Get-NotificationIdentity([Xml.XmlDocument]$Manifest) {
    $identity = $Manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']")
    $application = $Manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Applications']/*[local-name()='Application']")
    $external = $Manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Properties']/*[local-name()='AllowExternalContent']")
    $listener = $Manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Capabilities']/*[@Name='userNotificationListener']")
    if ($null -eq $identity -or $null -eq $application -or $null -eq $external -or $null -eq $listener -or
        $identity.GetAttribute('Name') -ne 'WinDock.NotificationAccess' -or $identity.GetAttribute('ProcessorArchitecture') -ne 'neutral' -or
        $application.GetAttribute('Id') -ne 'WinDock' -or $application.GetAttribute('Executable') -ne 'WinDock.exe' -or
        $external.InnerText -ne 'true' -or
        $listener.NamespaceURI -ne 'http://schemas.microsoft.com/appx/manifest/uap/windows10/3') {
        throw 'Unexpected WinDock notification package identity or capability.'
    }
    return [pscustomobject]@{ Name=$identity.GetAttribute('Name'); Publisher=$identity.GetAttribute('Publisher');
        Version=$identity.GetAttribute('Version'); ApplicationId=$application.GetAttribute('Id') }
}

function Assert-NotificationExeIdentity([string]$Executable, [string]$Mt, $Identity, [string]$OutputManifest) {
    Invoke-NotificationTool $Mt @('-nologo', "-inputresource:$Executable;#1", "-out:$OutputManifest")
    $manifest = Read-NotificationXml $OutputManifest
    $node = $manifest.SelectSingleNode("/*[local-name()='assembly']/*[local-name()='msix' and namespace-uri()='urn:schemas-microsoft-com:msix.v1']")
    $level = $manifest.SelectSingleNode("//*[local-name()='requestedExecutionLevel']")
    if ($null -eq $node -or $node.GetAttribute('packageName') -cne $Identity.Name -or $node.GetAttribute('publisher') -cne $Identity.Publisher -or
        $node.GetAttribute('applicationId') -cne $Identity.ApplicationId -or $null -eq $level -or
        $level.GetAttribute('level') -ne 'asInvoker' -or $level.GetAttribute('uiAccess') -ne 'false') {
        throw 'The executable identity or privilege declaration does not match the notification package.'
    }
}

function Get-NotificationPackageManifest([string]$Package) {
    $zip = [IO.Compression.ZipFile]::OpenRead($Package)
    try {
        $entry = $zip.GetEntry('AppxManifest.xml')
        if ($null -eq $entry -or $entry.Length -gt 1048576) { throw 'Missing or invalid package manifest.' }
        $stream = $entry.Open()
        try {
            $settings = [Xml.XmlReaderSettings]::new()
            $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
            $settings.XmlResolver = $null
            $reader = [Xml.XmlReader]::Create($stream, $settings)
            try {
                $xml = [Xml.XmlDocument]::new()
                $xml.XmlResolver = $null
                $xml.Load($reader)
                return ,$xml
            }
            finally { $reader.Dispose() }
        }
        finally { $stream.Dispose() }
    }
    finally { $zip.Dispose() }
}

function Get-NotificationBundle([string]$Directory) {
    $root = [IO.Path]::GetFullPath($Directory)
    $record = Get-Content -LiteralPath (Join-Path $root 'notification-package.json') -Raw | ConvertFrom-Json
    if ($record.schema -ne 1 -or $record.name -ne 'WinDock.NotificationAccess' -or $record.applicationId -ne 'WinDock') {
        throw 'Unsupported notification bundle descriptor.'
    }
    foreach ($path in @([string]$record.package, [string]$record.executable)) {
        $resolved = [IO.Path]::GetFullPath((Join-Path $root $path))
        if (-not $resolved.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The bundle descriptor references a path outside its directory.'
        }
    }
    return [pscustomobject]@{ Root=$root; Record=$record; Package=(Join-Path $root $record.package); Executable=(Join-Path $root $record.executable) }
}

function Write-NotificationBundleRecord($Bundle) {
    $Bundle.Record.packageSha256 = (Get-FileHash -LiteralPath $Bundle.Package -Algorithm SHA256).Hash.ToLowerInvariant()
    $Bundle.Record.executableSha256 = (Get-FileHash -LiteralPath $Bundle.Executable -Algorithm SHA256).Hash.ToLowerInvariant()
    $target = Join-Path $Bundle.Root 'notification-package.json'
    $temporary = Join-Path $Bundle.Root ([Guid]::NewGuid().ToString('N') + '.json.tmp')
    [IO.File]::WriteAllText($temporary, ($Bundle.Record | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    if (Test-Path -LiteralPath $target) { [IO.File]::Replace($temporary, $target, ($target + '.bak')) }
    else { [IO.File]::Move($temporary, $target) }
}

function Assert-NotificationTrustedSignature([string]$Path, [string]$Publisher) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or $signature.SignerCertificate.Subject -cne $Publisher) {
        throw "A valid trusted signature from '$Publisher' is required: $Path ($($signature.Status))."
    }
    return $signature.SignerCertificate.Thumbprint
}

function Assert-NotificationExternalAssets([string]$Package, [string]$ExternalLocation) {
    $zip = [IO.Compression.ZipFile]::OpenRead($Package)
    try {
        foreach ($name in @('Assets/StoreLogo.png', 'Assets/Square44x44Logo.png', 'Assets/Square150x150Logo.png')) {
            $entry = $zip.GetEntry($name)
            if ($null -eq $entry -or $entry.Length -gt 1048576) { throw "Missing package visual asset: $name" }
            $stream = $entry.Open()
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $expected = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
            finally { $sha.Dispose(); $stream.Dispose() }
            if ((Get-FileHash -LiteralPath (Join-Path $ExternalLocation $name) -Algorithm SHA256).Hash -ne $expected) {
                throw "The external visual asset differs from the signed package: $name"
            }
        }
    }
    finally { $zip.Dispose() }
}
