@echo off
setlocal EnableExtensions

set "SCRIPT_DIR=%~dp0"
set "LAUNCHER=%SCRIPT_DIR%Project21-LocalFUT-Launcher.exe"

if not exist "%LAUNCHER%" (
    echo.
    echo ERROR: The launcher is missing. Extract the complete ZIP again.
    pause
    exit /b 1
)

start "" "%LAUNCHER%"
exit /b 0
