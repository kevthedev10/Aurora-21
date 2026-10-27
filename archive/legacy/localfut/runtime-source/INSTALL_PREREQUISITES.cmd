@echo off
setlocal EnableExtensions
rem Builds Project21-LocalFUT-Server.exe and the launcher. Requires the .NET 8 (or newer) SDK.
where dotnet >nul 2>nul
if errorlevel 1 (
    echo ERROR: The .NET SDK was not found. Install it from https://dotnet.microsoft.com/download
    pause
    exit /b 1
)
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\Build-FIFA21-Launcher.ps1" -IfNeeded
pause
