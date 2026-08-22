@echo off
setlocal
cd /d "%~dp0"

echo.
echo   dpetoos derleniyor
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

rem --- simge: assets\dpetoos.ico depoda hazir gelir, yoksa Python ile uretilir -
if not exist "assets\dpetoos.ico" if exist "tools\make_icon.py" (
  where python >nul 2>nul && python "tools\make_icon.py" >nul 2>nul
)
set "ICONOPT="
if exist "assets\dpetoos.ico" set "ICONOPT=/win32icon:assets\dpetoos.ico"

rem --- kaynaklari UTF-8 BOM ile normalize et (Turkce karakterler icin) -------
if exist "tools\localize.py" (
  where python >nul 2>nul && python "tools\localize.py" >nul 2>nul
)

rem --- calisan bir surum varsa exe kilitlidir. Windows calisan bir dosyayi
rem     yeniden adlandirmaya izin verir; yeni derleme boylece engellenmez.
del /q "bin\dpetoos.old.exe" >nul 2>nul
if exist "bin\dpetoos.exe" ren "bin\dpetoos.exe" "dpetoos.old.exe" >nul 2>nul

echo   derleyici   : %CSC%
echo.

"%CSC%" /nologo /target:winexe /platform:x64 /unsafe+ /optimize+ /debug- ^
  /warn:3 /nowarn:1701,1702 ^
  /out:"bin\dpetoos.exe" ^
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
  if exist "bin\dpetoos.old.exe" if not exist "bin\dpetoos.exe" ren "bin\dpetoos.old.exe" "dpetoos.exe"
  exit /b 1
)

rem Eski surum hala calisiyorsa silinemez; sorun degil, sonraki derlemede gider.
del /q "bin\dpetoos.old.exe" >nul 2>nul

rem --- yan dosyalar ----------------------------------------------------------
copy /y "src\dpetoos.exe.config" "bin\dpetoos.exe.config" >nul
if not exist "bin\dpetoos.conf" copy /y "dpetoos.conf" "bin\dpetoos.conf" >nul

echo.
echo   TAMAM  ^-^>  bin\dpetoos.exe
if not exist "bin\WinDivert.dll" (
  echo.
  echo   NOT: bin\WinDivert.dll eksik. Calistirmadan once
  echo        tools\setup-windivert.cmd dosyasini calistirin.
)
echo.
endlocal
