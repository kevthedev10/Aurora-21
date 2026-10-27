@echo off
setlocal EnableExtensions
cd /d "%~dp0"

echo.
echo Building the Aurora 21 (Alpha v1) iOS Glass launcher...
echo.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-FIFA21-Launcher.ps1" -LauncherOnly
if errorlevel 1 (
  echo.
  echo Build failed. Copy the full error above and send it to ChatGPT.
  pause
  exit /b 1
)

echo.
echo Build complete. Starting Aurora 21 (Alpha v1)...
start "" "%~dp0Aurora21-Launcher.exe"
exit /b 0
