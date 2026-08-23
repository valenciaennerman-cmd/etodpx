# etodpx - paylasilabilir paket hazirlar
#
# Klasoru oldugu gibi RAR'lamak iki hata dogurur:
#   1) bin\etodpx.conf SENIN hattina ayarlidir. Karsi tarafta yanlis TTL ile
#      sessizce calismaz - uygulama "KORUMA ACIK" der ama site acilmaz.
#   2) Derleme artiklari (.old.exe), gunlukler ve kaynak kodu gereksiz yere
#      pakete girer.
#
# Bu betik yalnizca calismak icin gerekenleri duz bir klasore koyar ve
# ayar dosyasi olarak GUVENLI sablonu kullanir.
#
# Yonetici gerekmez.
#   powershell -ExecutionPolicy Bypass -File tools\make-release.ps1

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

$Out = Join-Path $Root 'release\etodpx'

Write-Host ''
Write-Host '  etodpx paket hazirlama'
Write-Host '  ======================'

# --- 1) taze derleme -------------------------------------------------------
Write-Host '  derleniyor...'
cmd /c ".\build.cmd" | Out-Null
if (-not (Test-Path 'bin\etodpx.exe')) { throw 'derleme basarisiz' }

cmd /c "tools\tune\build-tune.cmd" | Out-Null
if (-not (Test-Path 'bin\etodpx-tune.exe')) { throw 'ayarlayici derlenemedi' }

# --- 2) surucu -------------------------------------------------------------
if (-not (Test-Path 'bin\WinDivert.dll') -or -not (Test-Path 'bin\WinDivert64.sys')) {
    Write-Host '  WinDivert eksik, indiriliyor...'
    & powershell -NoProfile -ExecutionPolicy Bypass -File 'tools\setup-windivert.ps1' | Out-Null
}
if (-not (Test-Path 'bin\WinDivert64.sys')) { throw 'WinDivert kurulamadi' }

# --- 3) temiz klasor -------------------------------------------------------
if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
New-Item -ItemType Directory -Force $Out | Out-Null

$files = @(
    'bin\etodpx.exe',
    'bin\etodpx.exe.config',
    'bin\etodpx-tune.exe',
    'bin\WinDivert.dll',
    'bin\WinDivert64.sys'
)
foreach ($f in $files) { Copy-Item $f $Out -Force }

# Ayar: bin\ icindeki DEGIL, depodaki guvenli sablon.
Copy-Item 'etodpx.conf' (Join-Path $Out 'etodpx.conf') -Force

# --- 4) kullanim notu ------------------------------------------------------
$oku = @'
etodpx - kurulum yok, tek dosya calistir
========================================

1) Bu klasoru masaustune (ya da istedigin bir yere) cikar.
   Zip/rar icinden DOGRUDAN calistirma, once cikar.

2) etodpx.exe dosyasina cift tikla.

   "Windows bilgisayarinizi korudu" uyarisi cikarsa:
   Daha fazla bilgi  ->  Yine de calistir
   (Uygulama imzali degil, o yuzden cikiyor.)

   Ardindan yonetici izni ister. Evet de.
   WinDivert bir cekirdek surucusu, onsuz paket yakalanamaz.

3) Ortadaki dugmeye bas. "KORUMA ACIK" yazacak.

4) Siteyi dene.


Site hala acilmiyorsa
---------------------

Bu klasorde yonetici komut istemi ac ve sunu calistir:

    etodpx-tune.exe discord.com

("discord.com" yerine acilmayan sitenin adresini yaz.)

Arac ne yapman gerektigini soyler:

  "zaten aciliyor"        -> o sitede engel yok
  "DNS ZEHIRLI"           -> DNS'ini 1.1.1.1 / 1.0.0.1 yap, adimlari yazar
  "SNI engeli"            -> kendisi dogru ayari bulup yazar, sonra
                             etodpx.exe'yi yeniden baslat
  "IP seviyesinde engel"  -> etodpx bunu asamaz, VPN gerekir

Onemli: ayar dosyasi (etodpx.conf) makineye ozeldir. Baskasindan aldigin
bir ayari kopyalama - her bilgisayarda bir kez etodpx-tune.exe calistir.


Kaldirma
--------

Uygulamayi kapat, klasoru sil. Bu kadar.
Kayit defterine, hosts dosyasina, DNS ayarlarina dokunmaz; surucu de
uygulama kapaninca kaldirilir.
'@
[IO.File]::WriteAllText((Join-Path $Out 'OKU.txt'), $oku, (New-Object Text.UTF8Encoding($false)))

# --- 5) ozet ---------------------------------------------------------------
$size = (Get-ChildItem $Out -File | Measure-Object -Property Length -Sum).Sum
Write-Host ''
Write-Host '  HAZIR' -ForegroundColor Green
Write-Host "    $Out"
Write-Host ("    {0} dosya, {1:N1} MB" -f (Get-ChildItem $Out -File).Count, ($size / 1MB))
Write-Host ''
Get-ChildItem $Out -File | ForEach-Object { Write-Host ("      " + $_.Name) }
Write-Host ''
Write-Host '  Bu klasoru RAR/ZIP yapip paylasabilirsin.'
Write-Host '  Ayar dosyasi guvenli sablon - karsi taraf kendi ayarini olcecek.'
Write-Host ''
