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

param(
    [string]$Version = '2.4',
    [string]$SigningThumbprint = ''
)

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
if ($LASTEXITCODE -ne 0) { throw 'uygulama derlenemedi' }
if (-not (Test-Path 'bin\etodpx.exe')) { throw 'derleme basarisiz' }

cmd /c "tools\tune\build-tune.cmd" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'ayarlayici derlenemedi' }
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

# Kod imzalama sertifikasi varsa uygulama dosyalarini yayin klasorunde imzala.
# Sertifika olmadan SmartScreen itibari olusmaz; sahte/self-signed sertifika
# kullanmak kullaniciya guven kazandirmadigi icin otomatik uretilmiyor.
if ($SigningThumbprint) {
    $thumb = $SigningThumbprint.Replace(' ', '')
    $cert = Get-Item ("Cert:\CurrentUser\My\" + $thumb) -ErrorAction SilentlyContinue
    if (-not $cert -or -not $cert.HasPrivateKey) {
        throw 'Kod imzalama sertifikasi bulunamadi veya ozel anahtari yok'
    }
    foreach ($exe in @('etodpx.exe', 'etodpx-tune.exe')) {
        $signed = Set-AuthenticodeSignature -FilePath (Join-Path $Out $exe) `
            -Certificate $cert -TimestampServer 'http://timestamp.digicert.com'
        if ($signed.Status -ne 'Valid') {
            throw ("Kod imzalama basarisiz: " + $exe + " - " + $signed.StatusMessage)
        }
    }
} else {
    Write-Host '  UYARI: Kod imzalama sertifikasi verilmedi; EXE SmartScreen uyarisi gosterebilir.' `
        -ForegroundColor Yellow
}

# Ayar: bin\ icindeki DEGIL, depodaki guvenli sablon.
Copy-Item 'etodpx.conf' (Join-Path $Out 'etodpx.conf') -Force
Copy-Item 'hosts.txt'   (Join-Path $Out 'hosts.txt')   -Force

# --- 4) kullanim notu ------------------------------------------------------
$oku = @'
etodpx - kurulum yok, tek dugmeyle calisir
===========================================

Gereken sistem: 64 bit Windows 10 veya Windows 11.

1) Bu klasoru masaustune (ya da istedigin bir yere) cikar.
   Zip/rar icinden DOGRUDAN calistirma, once cikar.

2) etodpx.exe dosyasina cift tikla.

   "Windows bilgisayarinizi korudu" uyarisi cikarsa:
   Daha fazla bilgi  ->  Yine de calistir
   __SMARTSCREEN_NOTU__

   Ardindan yonetici izni ister. Evet de.
   WinDivert bir cekirdek surucusu, onsuz paket yakalanamaz.

3) Uygulama korumayi kendisi baslatir. "KORUMA ACIK" yazinca hazir.

   Baslatma hatasi olursa uygulamadaki dugme "Sorunu duzelt ve tekrar dene"
   haline gelir. Once bu dugmeye bas; eski WinDivert kalintisini temizleyip
   korumayi yeniden dener.

4) Siteyi dene.


Site hala acilmiyorsa
---------------------

Uygulamadaki "Discord / Roblox acilmiyor mu? Tek tikla duzelt" dugmesine bas.
Sirayla surucuyu, DNS'i ve SNI engelini test eder. Gerekirse aktif internet
baglantisinin eski DNS ayarini yedekleyip Cloudflare, Google ve Quad9 sifreli
DNS'lerini sirayla dener. Her birini hem Discord hem Roblox ile dogrular;
biri bile cozulemezse o saglayiciyi geri alir.
Sonra bu bilgisayara uygun ayari bulup korumayi yeniden baslatir.
Komut yazman gerekmez.

Sonucu uygulama soyler:

  "zaten aciliyor"        -> o sitede engel yok
  "DNS ZEHIRLI"           -> uc sifreli DNS saglayicisini sirayla dener
  "SNI engeli"            -> dogru ayari bulup hemen uygular
  "IP seviyesinde engel"  -> etodpx bunu asamaz, VPN gerekir

Uc sifreli DNS de calismazsa eski ayar geri gelir. Uygulama ayni testi tekrar
tekrar calistirmaz; alttaki dugme Ag ayarlarini ac secenegine donusur.

Hangi sitelere dokunuldugu hosts.txt icinde yazar; kutudan cikti gibi Discord
ve Roblox var. Baska bir engelli site eklemek istersen adini alt satira yaz.
Engelli OLMAYAN bir adi ekleme - o siteyi yavaslatir.

Onemli: ayar dosyasi (etodpx.conf) makineye ozeldir. Baskasindan aldigin
bir ayari kopyalama; her bilgisayarda otomatik duzeltmeyi bir kez calistir.


Kaldirma
--------

Uygulamayi kapat, klasoru sil. Surucu uygulama kapaninca kaldirilir.
Tek tikla duzelt sifreli DNS kurduysa ayar kalici olur; onceki DNS sunuculari
etodpx-dns-backup-<adaptor>.conf dosyasinda saklanir. Basarisiz DNS kurulumu
zaten otomatik geri alinir. Hosts dosyasina dokunulmaz.
'@
$smartscreenNote = if ($SigningThumbprint) {
    '(Paket kod imzasi tasiyor; yeni sertifikanin SmartScreen itibari zamanla olusur.)'
} else {
    '(Uygulama imzali degil, o yuzden cikiyor.)'
}
$oku = $oku.Replace('__SMARTSCREEN_NOTU__', $smartscreenNote)
[IO.File]::WriteAllText((Join-Path $Out 'OKU.txt'), $oku, (New-Object Text.UTF8Encoding($false)))

# --- 5) arsiv --------------------------------------------------------------
# ZIP, RAR degil: Windows ZIP'i cift tiklamayla acar. RAR icin karsi tarafin
# WinRAR ya da 7-Zip kurmus olmasi gerekir - "Discord acilsin" diye gelen
# birine once arsiv programi kurdurmak gereksiz engel.
$zip = Join-Path $Root ("release\etodpx-v" + $Version + ".zip")
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $Out -DestinationPath $zip -CompressionLevel Optimal

# --- 6) ozet ---------------------------------------------------------------
$size = (Get-ChildItem $Out -File | Measure-Object -Property Length -Sum).Sum
Write-Host ''
Write-Host '  HAZIR' -ForegroundColor Green
Write-Host "    $Out"
Write-Host ("    {0} dosya, {1:N1} MB" -f (Get-ChildItem $Out -File).Count, ($size / 1MB))
Write-Host ''
Get-ChildItem $Out -File | ForEach-Object { Write-Host ("      " + $_.Name) }
Write-Host ''
Write-Host ''
Write-Host ("  ARSIV: " + $zip) -ForegroundColor Green
Write-Host ("         {0:N0} KB - paylasmaya hazir" -f ((Get-Item $zip).Length / 1KB))
Write-Host ''
Write-Host '  Ayar dosyasi guvenli sablon - karsi taraf kendi ayarini olcecek.'
Write-Host '  Baska surum icin:  make-release.ps1 -Version 1.1'
Write-Host '  Imzali yayin:       make-release.ps1 -SigningThumbprint SERTIFIKA_PARMAK_IZI'
Write-Host ''
