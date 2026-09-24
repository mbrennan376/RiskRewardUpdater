@echo off
setlocal EnableExtensions
title Diagnose Risk/Reward HomeSeer Monitor
cd /d "%~dp0"

echo Collecting HomeSeer plugin diagnostics...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Diagnose.ps1"
set "RESULT=%ERRORLEVEL%"

echo.
if "%RESULT%"=="0" (
  echo Diagnostic report written to:
  echo   %~dp0diagnostic-report.txt
) else (
  echo Diagnostics failed with exit code %RESULT%.
)
echo.
echo Press any key to close.
pause >nul
exit /b %RESULT%
