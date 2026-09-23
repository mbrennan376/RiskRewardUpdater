@echo off
setlocal
title Install Risk/Reward Price Service
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-RiskRewardService.ps1"
set "INSTALL_EXIT=%ERRORLEVEL%"
echo.
if not "%INSTALL_EXIT%"=="0" (
  echo Installation failed with exit code %INSTALL_EXIT%.
) else (
  echo Installation and the initial live price update completed successfully.
)
pause
exit /b %INSTALL_EXIT%
