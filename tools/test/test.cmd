@echo off
rem dpetoos - kapsamli test paketi (67 kontrol). dpetoos ACIK iken calistir.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-tests.ps1" %*
echo.
pause
