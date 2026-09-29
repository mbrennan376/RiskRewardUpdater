@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Set-Eodhd-Key.ps1"
set "exitCode=%ERRORLEVEL%"
if not "%exitCode%"=="0" echo EODHD configuration failed with exit code %exitCode%.
pause
exit /b %exitCode%
