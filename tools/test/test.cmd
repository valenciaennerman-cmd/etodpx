@echo off
rem etodpx - kapsamli test paketi (67 kontrol). etodpx ACIK iken calistir.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-tests.ps1" %*
echo.
pause
