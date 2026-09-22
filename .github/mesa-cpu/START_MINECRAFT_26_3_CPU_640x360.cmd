@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-cpu-llvmpipe.ps1"
if errorlevel 1 (
  echo.
  echo CPU llvmpipe launcher failed. Error code: %errorlevel%
  pause
)
