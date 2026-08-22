@echo off
rem dpetoos - oz testi derler ve calistirir (yonetici gerekmez)
setlocal
cd /d "%~dp0..\.."

set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
set "CSC=%FW%\csc.exe"

"%CSC%" /nologo /target:exe /platform:x64 /unsafe+ /optimize+ /debug- ^
  /warn:3 /nowarn:1701,1702 ^
  /out:"bin\dpetoos-selftest.exe" ^
  /main:Dpetoos.Test.SelfTest ^
  "src\Config.cs" "src\engine\*.cs" "tools\selftest\SelfTest.cs"

if errorlevel 1 exit /b 1

pushd "bin"
call ".\dpetoos-selftest.exe"
set RC=%ERRORLEVEL%
popd
exit /b %RC%
