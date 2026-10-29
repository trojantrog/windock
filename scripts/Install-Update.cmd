@echo off
setlocal DisableDelayedExpansion
set "WinDockPwsh=__WINDOCK_POWERSHELL7_PATH__"
if not exist "%WinDockPwsh%" set "WinDockPwsh="
if not defined WinDockPwsh if exist "%ProgramFiles%\PowerShell\7\pwsh.exe" set "WinDockPwsh=%ProgramFiles%\PowerShell\7\pwsh.exe"
if not defined WinDockPwsh for /f "delims=" %%P in ('where pwsh.exe 2^>nul') do if not defined WinDockPwsh set "WinDockPwsh=%%P"
if not defined WinDockPwsh goto RequirePowerShell7
"%WinDockPwsh%" -NoLogo -NoProfile -NonInteractive -Command "if ($PSVersionTable.PSVersion.Major -lt 7) { exit 1 }"
if errorlevel 1 goto RequirePowerShell7
if not exist "%~dp0updater\Update-InstalledBuild.ps1" goto MissingUpdater
"%WinDockPwsh%" -NoLogo -NoProfile -File "%~dp0updater\Update-InstalledBuild.ps1" -PackagePath "%~dp0." %*
set "WinDockUpdateExit=%ERRORLEVEL%"
if not "%WinDockUpdateExit%"=="0" (
    echo WinDock update failed. Review the message above before trying again.
    pause
)
exit /b %WinDockUpdateExit%

:RequirePowerShell7
echo PowerShell 7 or later is required. Install it, then run Install-Update.cmd again.
pause
exit /b 1

:MissingUpdater
echo updater\Update-InstalledBuild.ps1 is missing. Extract the complete update package first.
pause
exit /b 1
