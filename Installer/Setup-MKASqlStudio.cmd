@echo off
setlocal
set "SCRIPT=%~dp0Setup-MKASqlStudio.ps1"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" -Package "%~1"
if errorlevel 1 pause
