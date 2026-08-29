@echo off
rem etodpx - butun testler. GitHub'a push etmeden once bunu calistir.
rem
rem   1) Oz test            (119 kontrol, yonetici gerekmez)
rem   2) Yayin denetimi     (25 kontrol, sifirdan klon derlemesi)
rem   3) Ag test paketi     (67 kontrol, etodpx acik olmali)
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
tasklist /FI "IMAGENAME eq etodpx.exe" 2>nul | find /I "etodpx.exe" >nul
if errorlevel 1 (
  echo.
  echo   ATLANDI: etodpx calismiyor.
  echo   Once etodpx.cmd ile baslat, sonra bu testi tekrar calistir.
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
