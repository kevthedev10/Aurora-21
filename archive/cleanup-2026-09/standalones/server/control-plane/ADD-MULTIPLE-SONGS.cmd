@echo off
setlocal
title Project 21 - Upload Cloud Music

if "%~1"=="" (
    echo Drag one or more MP3 files onto this script to upload them.
    echo.
    pause
    exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Publish-CloudMusicBatch.ps1" %*
set "UploadExitCode=%ERRORLEVEL%"

echo.
if not "%UploadExitCode%"=="0" (
    echo One or more songs could not be published. Review the messages above.
) else (
    echo All songs were published successfully.
)
pause
exit /b %UploadExitCode%
