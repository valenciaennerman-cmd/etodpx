@echo off
rem etodpx - tek tikla: gerekirse derle, WinDivert eksikse kur, sonra calistir.
setlocal
cd /d "%~dp0"

if not exist "bin\etodpx.exe" (
  call "build.cmd" || exit /b 1
)

if not exist "bin\WinDivert.dll" (
  echo   WinDivert eksik, kuruluyor...
  powershell -NoProfile -ExecutionPolicy Bypass -File "tools\setup-windivert.ps1"
  if errorlevel 1 exit /b 1
)

start "" "bin\etodpx.exe"
endlocal
