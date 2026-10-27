@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0First-Run-Cloudflare.ps1"
if errorlevel 1 (
  echo.
  echo Cloudflare first-run setup did not complete. Read STANDALONE-README.md.
)
pause
