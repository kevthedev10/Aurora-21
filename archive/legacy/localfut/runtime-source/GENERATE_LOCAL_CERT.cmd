@echo off
setlocal EnableExtensions
set "SERVER=%~dp0..\Project21-LocalFUT-Server.exe"
if not exist "%SERVER%" (
    echo ERROR: Project21-LocalFUT-Server.exe was not found. Run INSTALL_PREREQUISITES.cmd first.
    pause
    exit /b 2
)
"%SERVER%" generate-certs --data-root "%~dp0localfut21"
pause
