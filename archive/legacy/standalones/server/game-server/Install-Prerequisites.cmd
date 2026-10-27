@echo off
setlocal
cd /d "%~dp0"
py -3 -m pip install -r "%~dp0runtime-source\requirements.txt"
if errorlevel 1 python -m pip install -r "%~dp0runtime-source\requirements.txt"
pause
