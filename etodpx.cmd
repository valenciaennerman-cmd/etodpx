@echo off
rem etodpx - tek tikla: gerekirse derle, WinDivert eksikse kur, sonra calistir.
setlocal
cd /d "%~dp0"

if not exist "bin\etodpx.exe" (
  call "build.cmd" || exit /b 1
)

rem Iki dosya da gerekli: .dll kullanici alani API'si, .sys cekirdek surucusu.
rem Yalnizca .dll'e bakmak, .sys eksikken kurulumu atlar ve uygulama calisma
rem aninda "surucu dosyasi bulunamadi" ile durur.
if not exist "bin\WinDivert.dll" goto :needwd
if not exist "bin\WinDivert64.sys" goto :needwd
goto :havewd

:needwd
(
  echo   WinDivert eksik, kuruluyor...
  powershell -NoProfile -ExecutionPolicy Bypass -File "tools\setup-windivert.ps1"
  if errorlevel 1 exit /b 1
)
:havewd

start "" "bin\etodpx.exe"
endlocal
