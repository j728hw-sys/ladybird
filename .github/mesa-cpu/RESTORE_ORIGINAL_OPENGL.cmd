@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0restore-cpu-llvmpipe.ps1"
pause
