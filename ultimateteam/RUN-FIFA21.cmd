@echo off
setlocal EnableExtensions

set "SCRIPT_DIR=%~dp0"
set "LAUNCHER=%SCRIPT_DIR%Aurora21-UltimateTeam-Launcher.exe"
set "BUILDER=%SCRIPT_DIR%Build-FIFA21-Launcher.ps1"

rem Normal launcher Play enables the combined leaderboard and Aurora Tokens mod.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%BUILDER%" -IfNeeded
if errorlevel 1 (
    echo.
    echo ERROR: The launcher could not be built.
    pause
    exit /b 1
)

start "" "%LAUNCHER%"
exit /b 0
