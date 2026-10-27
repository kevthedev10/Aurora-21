@echo off
setlocal EnableExtensions
set "STOPPER=%~dp0..\Stop-LocalFUT.cmd"
if not exist "%STOPPER%" (
    echo Close the FIFA21 Local FUT console windows.
    echo This package deliberately stops only the processes the launcher started.
    pause
    exit /b 0
)
call "%STOPPER%" %*
exit /b %ERRORLEVEL%
