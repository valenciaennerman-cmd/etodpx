@echo off
rem etodpx - WinDivert surucusunu indirir (bkz. setup-windivert.ps1)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup-windivert.ps1"
if errorlevel 1 (
  echo.
  pause
  exit /b 1
)
pause
