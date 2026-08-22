@echo off
rem dpetoos - butun testler. GitHub'a push etmeden once bunu calistir.
rem
rem   1) Oz test            (79 kontrol, yonetici gerekmez)
rem   2) Yayin denetimi     (22 kontrol, sifirdan klon derlemesi)
rem   3) Ag test paketi     (67 kontrol, dpetoos ACIK olmali)
setlocal
cd /d "%~dp0..\.."

set FAILED=0

echo.
echo ================================================================
echo   1/3  OZ TEST  -  motor mantigi, yonetici gerekmez
echo ================================================================
call "tools\selftest\build-selftest.cmd"
if errorlevel 1 set FAILED=1

echo.
echo ================================================================
echo   2/3  YAYIN DENETIMI  -  temiz klon sifirdan derleniyor
echo ================================================================
powershell -NoProfile -ExecutionPolicy Bypass -File "tools\test\verify-release.ps1"
if errorlevel 1 set FAILED=1

echo.
echo ================================================================
echo   3/3  AG TEST PAKETI  -  gercek baglantilar
echo ================================================================
tasklist /FI "IMAGENAME eq dpetoos.exe" 2>nul | find /I "dpetoos.exe" >nul
if errorlevel 1 (
  echo.
  echo   ATLANDI: dpetoos calismiyor.
  echo   Once dpetoos.cmd ile baslat, sonra bu testi tekrar calistir.
  echo.
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File "tools\test\run-tests.ps1"
  if errorlevel 1 set FAILED=1
)

echo.
echo ================================================================
if "%FAILED%"=="0" (
  echo   HEPSI GECTI  -  push edebilirsin
) else (
  echo   BASARISIZ TEST VAR  -  yukariyi kontrol et
)
echo ================================================================
echo.
pause
exit /b %FAILED%
