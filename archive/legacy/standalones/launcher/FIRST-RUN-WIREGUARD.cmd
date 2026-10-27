@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0First-Run-WireGuard.ps1"
set "code=%errorlevel%"
if not "%code%"=="0" pause
exit /b %code%
