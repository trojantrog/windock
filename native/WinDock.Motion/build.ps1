param([Parameter(Mandatory=$true)][string]$Zig)
$ErrorActionPreference = 'Stop'
function Assert-NativeImage([string]$Path, [bool]$Dll) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 64 -or $bytes[0] -ne 0x4d -or $bytes[1] -ne 0x5a) { throw ('Not a Windows PE image: ' + $Path) }
    $offset = [BitConverter]::ToInt32($bytes, 0x3c)
    if ($offset -lt 64 -or $offset -gt $bytes.Length - 26 -or [BitConverter]::ToUInt32($bytes, $offset) -ne 0x4550) { throw ('Invalid PE header: ' + $Path) }
    $machine = [BitConverter]::ToUInt16($bytes, $offset + 4)
    $characteristics = [BitConverter]::ToUInt16($bytes, $offset + 22)
    $isDll = ($characteristics -band 0x2000) -ne 0
    if ($machine -ne 0x8664 -or [BitConverter]::ToUInt16($bytes, $offset + 24) -ne 0x20b -or $isDll -ne $Dll) { throw ('Unexpected native image type: ' + $Path) }
}
$motionRoot = Split-Path -Parent $PSCommandPath
$repoRoot = Split-Path -Parent (Split-Path -Parent $motionRoot)
$motionOutput = Join-Path $motionRoot 'bin\win-x64'
$hookRoot = Join-Path $repoRoot 'third_party\minhook'
New-Item -ItemType Directory -Force -Path $motionOutput | Out-Null
$manifestPath = Join-Path $motionOutput 'WinDock.Motion.manifest.json'
if (Test-Path -LiteralPath $manifestPath) { Remove-Item -LiteralPath $manifestPath }
$oldCache = [Environment]::GetEnvironmentVariable('ZIG_GLOBAL_CACHE_DIR', 'Process')
try {
    $env:ZIG_GLOBAL_CACHE_DIR = Join-Path $motionOutput '.zig-cache'
    $objects = @()
    foreach ($source in @('buffer.c', 'hook.c', 'trampoline.c', 'hde\hde64.c')) {
        $object = Join-Path $motionOutput (([IO.Path]::GetFileNameWithoutExtension($source)) + '.obj')
        & $Zig cc -target x86_64-windows-gnu -O2 -c (Join-Path $hookRoot ('src\' + $source)) -o $object
        if ($LASTEXITCODE -ne 0) { throw ('MinHook compilation failed: ' + $source) }
        $objects += $object
    }
    $nativeDll = Join-Path $motionOutput 'WinDock.Motion.x64.dll'
    & $Zig c++ -target x86_64-windows-gnu -std=c++17 -O2 -Wall -Wextra -Werror -Wno-missing-field-initializers -DUNICODE -D_UNICODE -shared -I (Join-Path $hookRoot 'include') (Join-Path $motionRoot 'motion.cpp') @objects -o $nativeDll -luser32 -lkernel32 -lbcrypt
    if ($LASTEXITCODE -ne 0) { throw 'Native motion component compilation failed.' }
    Assert-NativeImage $nativeDll $true
    $payloadHash = (Get-FileHash -LiteralPath $nativeDll -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $motionOutput 'payload_hash.h'), ('#define WIN_DOCK_MOTION_PAYLOAD_SHA256 "' + $payloadHash + '"' + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
    $loginExe = Join-Path $motionOutput 'WinDock.MotionLogin.exe'
    & $Zig c++ -target x86_64-windows-gnu -std=c++17 -O2 -Wall -Wextra -Werror -Wno-missing-field-initializers -DUNICODE -D_UNICODE -DWIN_DOCK_STARTUP_ONLY -static -municode '-Wl,--subsystem,windows' -I $motionOutput (Join-Path $motionRoot 'host.cpp') -o $loginExe -luser32 -lkernel32 -ladvapi32 -lbcrypt -lshell32 -lole32 -loleaut32 -luuid
    if ($LASTEXITCODE -ne 0) { throw 'Motion login helper compilation failed.' }
    Assert-NativeImage $loginExe $false
    $loginHash = (Get-FileHash -LiteralPath $loginExe -Algorithm SHA256).Hash.ToLowerInvariant()
    $loginBytes = [IO.File]::ReadAllBytes($loginExe)
    $loginHeader = [Text.StringBuilder]::new()
    [void]$loginHeader.AppendLine('#pragma once')
    [void]$loginHeader.AppendLine('namespace motion {')
    [void]$loginHeader.AppendLine(('inline constexpr char LoginHostSha256[] = "' + $loginHash + '";'))
    [void]$loginHeader.AppendLine('inline constexpr unsigned char LoginHostBytes[] = {')
    for ($index = 0; $index -lt $loginBytes.Length; $index++) {
        [void]$loginHeader.Append(('0x{0:x2},' -f $loginBytes[$index]))
        if (($index % 24) -eq 23) { [void]$loginHeader.AppendLine() }
    }
    [void]$loginHeader.AppendLine('};')
    [void]$loginHeader.AppendLine('}')
    [IO.File]::WriteAllText((Join-Path $motionOutput 'login_host_payload.h'), $loginHeader.ToString(), [Text.UTF8Encoding]::new($false))
    $hostExe = Join-Path $motionOutput 'WinDock.MotionHost.exe'
    & $Zig c++ -target x86_64-windows-gnu -std=c++17 -O2 -Wall -Wextra -Werror -Wno-missing-field-initializers -DUNICODE -D_UNICODE -static -municode '-Wl,--subsystem,windows' -I $motionOutput (Join-Path $motionRoot 'host.cpp') -o $hostExe -luser32 -lkernel32 -ladvapi32 -lbcrypt -lshell32 -lole32 -loleaut32 -luuid
    if ($LASTEXITCODE -ne 0) { throw 'Motion activation helper compilation failed.' }
    Assert-NativeImage $hostExe $false
    $manifest = [ordered]@{
        protocol = 1
        build = 12
        supportedImage = '10.0.26100.9949'
        supportedImageSha256 = 'de3a7bacdcbb49a98bfff41010e772d18a329b77d9bc6b2416e1bc9f5b415863'
        payload = [ordered]@{ name = [IO.Path]::GetFileName($nativeDll); sha256 = $payloadHash }
        host = [ordered]@{ name = [IO.Path]::GetFileName($hostExe); sha256 = (Get-FileHash -LiteralPath $hostExe -Algorithm SHA256).Hash.ToLowerInvariant() }
        loginHost = [ordered]@{ name = [IO.Path]::GetFileName($loginExe); sha256 = $loginHash; embedded = $true }
        minhook = [ordered]@{ version = '1.3.4'; commit = 'c3fcafdc10146beb5919319d0683e44e3c30d537' }
    }
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    Write-Output $nativeDll
    Write-Output $hostExe
}
finally {
    [Environment]::SetEnvironmentVariable('ZIG_GLOBAL_CACHE_DIR', $oldCache, 'Process')
}
