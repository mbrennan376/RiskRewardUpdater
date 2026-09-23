@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Update-RiskRewardService.ps1"
set "updateExit=%errorlevel%"
echo.
if exist "%~dp0Update-RiskRewardService-last-run.log" type "%~dp0Update-RiskRewardService-last-run.log"
echo.
if not "%updateExit%"=="0" echo UPDATE FAILED with exit code %updateExit%.
pause
exit /b %updateExit%
