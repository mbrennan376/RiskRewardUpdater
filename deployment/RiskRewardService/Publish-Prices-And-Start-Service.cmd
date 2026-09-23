@echo off
setlocal
title Publish Risk/Reward Prices and Start Service
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Publish-Prices-And-Start-Service.ps1"
set "UPDATE_EXIT=%ERRORLEVEL%"
echo.
if not "%UPDATE_EXIT%"=="0" (
  echo Live price update failed with exit code %UPDATE_EXIT%.
) else (
  echo Live prices were published and the service is running.
)
pause
exit /b %UPDATE_EXIT%
