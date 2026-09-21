@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-mobilegl.ps1"
set "EC=%ERRORLEVEL%"
echo.
if not "%EC%"=="0" (
  echo START FAILED. Error code: %EC%
  pause
)
exit /b %EC%
