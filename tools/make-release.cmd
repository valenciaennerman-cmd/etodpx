@echo off
rem etodpx - paylasilabilir paket hazirlar (bkz. make-release.ps1)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0make-release.ps1" %*
echo.
pause
