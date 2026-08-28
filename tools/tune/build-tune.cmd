@echo off
rem etodpx - strateji bulucuyu derler
setlocal
cd /d "%~dp0..\.."

set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"

rem Arac hala aciksa exe kilitlidir; Windows calisan bir dosyayi yeniden
rem adlandirmaya izin verir, boylece derleme engellenmez.
del /q "bin\etodpx-tune.old.exe" >nul 2>nul
if exist "bin\etodpx-tune.exe" ren "bin\etodpx-tune.exe" "etodpx-tune.old.exe" >nul 2>nul

"%FW%\csc.exe" /nologo /target:exe /platform:x64 /unsafe+ /optimize+ /debug- ^
  /warn:3 /nowarn:1701,1702 ^
  /out:"bin\etodpx-tune.exe" ^
  /main:Etodpx.Tune.Tuner ^
  /win32manifest:"src\app.manifest" ^
  "src\AssemblyInfo.cs" "src\Config.cs" "src\engine\*.cs" "tools\tune\Tune.cs"

if errorlevel 1 (
  echo   DERLEME BASARISIZ
  exit /b 1
)
echo   TAMAM  -^>  bin\etodpx-tune.exe
endlocal
