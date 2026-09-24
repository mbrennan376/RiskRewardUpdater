@echo off
setlocal EnableExtensions
title Install Risk/Reward HomeSeer Monitor

cd /d "%~dp0"

echo.
echo Risk/Reward HomeSeer Monitor Installer
echo ======================================
echo.
echo Before continuing, disable the "Risk Reward Site Status" plugin in HS4
echo if an earlier copy is currently running.
echo.

set "HSROOT=C:\Program Files (x86)\HomeSeer HS4"

if not exist "%HSROOT%\" (
  echo.
  echo ERROR: The folder "%HSROOT%" does not exist.
  goto :failed
)

if not exist "%~dp0package\HSPI_RiskRewardStatus.exe" (
  echo.
  echo ERROR: The plugin package is missing from "%~dp0package".
  goto :failed
)

echo.
echo Installing the plugin in "%HSROOT%"...

copy /y "%~dp0package\HSPI_RiskRewardStatus.exe" "%HSROOT%\HSPI_RiskRewardStatus.exe" >nul
if errorlevel 1 goto :copyfailed

copy /y "%~dp0package\HSPI_RiskRewardStatus.exe.config" "%HSROOT%\HSPI_RiskRewardStatus.exe.config" >nul
if errorlevel 1 goto :copyfailed

if not exist "%HSROOT%\bin\RiskRewardStatus\" mkdir "%HSROOT%\bin\RiskRewardStatus"
if errorlevel 1 goto :copyfailed

xcopy "%~dp0package\bin\RiskRewardStatus\*" "%HSROOT%\bin\RiskRewardStatus\" /E /I /Y /Q >nul
if errorlevel 1 goto :copyfailed

echo.
echo INSTALLATION COMPLETE
echo.
echo In HS4, open Plugins, enable "Risk Reward Site Status", and wait up to
echo five minutes. The plugin creates these two read-only status features:
echo.
echo   Risk/Reward Last Updated
echo   Risk/Reward Current Status
echo.
echo Press any key to close.
pause >nul
exit /b 0

:copyfailed
echo.
echo ERROR: Files could not be copied to "%HSROOT%".
echo Disable the plugin if it is running, then run Install.cmd as Administrator.

:failed
echo.
echo INSTALLATION FAILED
echo Press any key to close.
pause >nul
exit /b 1
