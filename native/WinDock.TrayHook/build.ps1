param([Parameter(Mandatory=$true)][string]$Zig)
$ErrorActionPreference = 'Stop'
$nativeRoot = Split-Path -Parent $PSCommandPath
$nativeOutput = Join-Path $nativeRoot 'bin\win-x64'
New-Item -ItemType Directory -Force -Path $nativeOutput | Out-Null
$nativeDll = Join-Path $nativeOutput 'WinDock.TrayHook.v7.x64.dll'
$previousZigCache = [Environment]::GetEnvironmentVariable('ZIG_GLOBAL_CACHE_DIR', 'Process')
try {
    $env:ZIG_GLOBAL_CACHE_DIR = Join-Path $nativeOutput '.zig-cache'
    & $Zig cc -target x86_64-windows-gnu -std=c11 -O2 -Wall -Wextra -Werror -shared `
        (Join-Path $nativeRoot 'tray_hook.c') (Join-Path $nativeRoot 'tray_images.c') (Join-Path $nativeRoot 'tray_image_verify.c') `
        (Join-Path $nativeRoot 'tray_process_sequences.c') `
        -o $nativeDll -luser32 -lgdi32 -lkernel32 -lbcrypt -lapi-ms-win-core-winrt-l1-1-0 -lapi-ms-win-core-winrt-string-l1-1-0
    if ($LASTEXITCODE -ne 0) { throw 'Native tray component compilation failed.' }
}
finally {
    [Environment]::SetEnvironmentVariable('ZIG_GLOBAL_CACHE_DIR', $previousZigCache, 'Process')
}
$nativeHash = (Get-FileHash -LiteralPath $nativeDll -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText(($nativeDll + '.sha256'), $nativeHash, [System.Text.UTF8Encoding]::new($false))
Write-Output $nativeDll
Write-Output ('SHA256 ' + $nativeHash)
