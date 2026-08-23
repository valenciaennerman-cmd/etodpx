@echo off
setlocal
cd /d "%~dp0"

echo.
echo   etodpx derleniyor
echo   ----------------

rem --- C# derleyicisi: Windows ile birlikte gelir, kurulum gerekmez -----------
set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
set "CSC=%FW%\csc.exe"
if not exist "%CSC%" (
  echo   HATA: .NET Framework C# derleyicisi bulunamadi.
  echo         Beklenen konum: %CSC%
  exit /b 1
)

rem csc.rsp zaten System, System.Core, System.Xml, System.Windows.Forms ve
rem System.Drawing referanslarini ekliyor. Biz yalnizca WPF tarafini ekliyoruz.
set "WPF=%FW%\WPF"
if not exist "%WPF%\PresentationFramework.dll" (
  echo   HATA: WPF derlemeleri bulunamadi: %WPF%
  exit /b 1
)

if not exist "bin" mkdir "bin"

rem --- simge: assets\etodpx.ico depoda hazir gelir, yoksa Python ile uretilir -
if not exist "assets\etodpx.ico" if exist "tools\make_icon.py" (
  where python >nul 2>nul && python "tools\make_icon.py" >nul 2>nul
)
set "ICONOPT="
if exist "assets\etodpx.ico" set "ICONOPT=/win32icon:assets\etodpx.ico"

rem --- kaynaklari UTF-8 BOM ile normalize et (Turkce karakterler icin) -------
if exist "tools\localize.py" (
  where python >nul 2>nul && python "tools\localize.py" >nul 2>nul
)

rem --- calisan bir surum varsa exe kilitlidir. Windows calisan bir dosyayi
rem     yeniden adlandirmaya izin verir; yeni derleme boylece engellenmez.
del /q "bin\etodpx.old.exe" >nul 2>nul
if exist "bin\etodpx.exe" ren "bin\etodpx.exe" "etodpx.old.exe" >nul 2>nul

echo   derleyici   : %CSC%
echo.

"%CSC%" /nologo /target:winexe /platform:x64 /unsafe+ /optimize+ /debug- ^
  /warn:3 /nowarn:1701,1702 ^
  /out:"bin\etodpx.exe" ^
  %ICONOPT% ^
  /win32manifest:"src\app.manifest" ^
  /r:"%FW%\System.Xaml.dll" ^
  /r:"%WPF%\WindowsBase.dll" ^
  /r:"%WPF%\PresentationCore.dll" ^
  /r:"%WPF%\PresentationFramework.dll" ^
  "src\*.cs" "src\engine\*.cs" "src\ui\*.cs"

if errorlevel 1 (
  echo.
  echo   DERLEME BASARISIZ
  if exist "bin\etodpx.old.exe" if not exist "bin\etodpx.exe" ren "bin\etodpx.old.exe" "etodpx.exe"
  exit /b 1
)

rem Eski surum hala calisiyorsa silinemez; sorun degil, sonraki derlemede gider.
del /q "bin\etodpx.old.exe" >nul 2>nul

rem --- yan dosyalar ----------------------------------------------------------
copy /y "src\etodpx.exe.config" "bin\etodpx.exe.config" >nul
if not exist "bin\etodpx.conf" copy /y "etodpx.conf" "bin\etodpx.conf" >nul

echo.
echo   TAMAM  ^-^>  bin\etodpx.exe
if not exist "bin\WinDivert.dll" (
  echo.
  echo   NOT: bin\WinDivert.dll eksik. Calistirmadan once
  echo        tools\setup-windivert.cmd dosyasini calistirin.
)
echo.
endlocal
