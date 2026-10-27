@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Stop-UltimateTeam.ps1"
exit /b %errorlevel%
