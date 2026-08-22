# dpetoos - WinDivert surucusunu indirip bin klasorune yerlestirir.
#
# WinDivert, GoodbyeDPI ve zapret dahil butun bu tur araclarin kullandigi
# acik kaynakli paket yakalama surucusudur (LGPLv3 / GPLv2).
# Surucu Microsoft tarafindan imzalidir, Secure Boot ile calisir.

$ErrorActionPreference = 'Stop'

$Version = '2.2.2'
$ZipName = "WinDivert-$Version-A.zip"
$Url     = "https://github.com/basil00/WinDivert/releases/download/v$Version/$ZipName"
$ExpectedDllSha256 = 'C1E060EE19444A259B2162F8AF0F3FE8C4428A1C6F694DCE20DE194AC8D7D9A2'
$ExpectedSysSha256 = '8DA085332782708D8767BCACE5327A6EC7283C17CFB85E40B03CD2323A90DDC2'

$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$Bin  = Join-Path $Root 'bin'
$Tmp  = Join-Path $env:TEMP ("dpetoos-windivert-" + [guid]::NewGuid().ToString('N'))

Write-Host ''
Write-Host '  WinDivert kurulumu' -ForegroundColor Cyan
Write-Host '  ------------------'

$InstalledDll = Join-Path $Bin 'WinDivert.dll'
$InstalledSys = Join-Path $Bin 'WinDivert64.sys'
if ((Test-Path $InstalledDll) -and (Test-Path $InstalledSys)) {
    $dllHash = (Get-FileHash -Path $InstalledDll -Algorithm SHA256).Hash
    $sysHash = (Get-FileHash -Path $InstalledSys -Algorithm SHA256).Hash
    if ($dllHash -eq $ExpectedDllSha256 -and $sysHash -eq $ExpectedSysSha256) {
        Write-Host '  Zaten kurulu ve butunluk dogrulamasi basarili.'
        Write-Host ''
        exit 0
    }
    Write-Host '  Mevcut WinDivert dosyalari beklenen surumle eslesmiyor; yenilenecek.' -ForegroundColor Yellow
}

New-Item -ItemType Directory -Force -Path $Bin, $Tmp | Out-Null
$Zip = Join-Path $Tmp $ZipName

Write-Host "  kaynak : $Url"
Write-Host '  indiriliyor...'

try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $Url -OutFile $Zip -UseBasicParsing
} catch {
    Write-Host ''
    Write-Host '  INDIRME BASARISIZ' -ForegroundColor Red
    Write-Host "  $($_.Exception.Message)"
    Write-Host ''
    Write-Host '  Elle kurulum:'
    Write-Host "    1) $Url adresinden zip dosyasini indirin"
    Write-Host '    2) Icindeki x64 klasorunden WinDivert.dll ve WinDivert64.sys'
    Write-Host "       dosyalarini $Bin klasorune kopyalayin"
    Write-Host ''
    exit 1
}

$size = (Get-Item $Zip).Length
$hash = (Get-FileHash -Path $Zip -Algorithm SHA256).Hash
Write-Host ("  indirildi : {0:N0} bayt" -f $size)
Write-Host "  SHA256    : $hash"
Write-Host '  (bu ozeti GitHub surum sayfasindaki degerle karsilastirabilirsiniz)'

$Ext = Join-Path $Tmp 'x'
Expand-Archive -Path $Zip -DestinationPath $Ext -Force

$dll = Get-ChildItem -Path $Ext -Recurse -Filter 'WinDivert.dll' |
       Where-Object { $_.DirectoryName -match '(\\|/)x64$' } | Select-Object -First 1
$sys = Get-ChildItem -Path $Ext -Recurse -Filter 'WinDivert64.sys' | Select-Object -First 1

if (-not $dll -or -not $sys) {
    Write-Host '  HATA: arsiv icinde x64 dosyalari bulunamadi.' -ForegroundColor Red
    Remove-Item -Recurse -Force $Tmp -ErrorAction SilentlyContinue
    exit 1
}

$dllHash = (Get-FileHash -Path $dll.FullName -Algorithm SHA256).Hash
$sysHash = (Get-FileHash -Path $sys.FullName -Algorithm SHA256).Hash
if ($dllHash -ne $ExpectedDllSha256 -or $sysHash -ne $ExpectedSysSha256) {
    Write-Host ''
    Write-Host '  HATA: indirilen WinDivert dosyalarinin SHA256 ozeti beklenen degerle eslesmiyor.' -ForegroundColor Red
    Write-Host "  DLL: $dllHash"
    Write-Host "  SYS: $sysHash"
    Remove-Item -Recurse -Force $Tmp -ErrorAction SilentlyContinue
    exit 1
}

$signature = Get-AuthenticodeSignature -FilePath $sys.FullName
if ($signature.Status -ne 'Valid') {
    Write-Host ''
    Write-Host "  HATA: WinDivert64.sys imzasi gecersiz: $($signature.Status)" -ForegroundColor Red
    Remove-Item -Recurse -Force $Tmp -ErrorAction SilentlyContinue
    exit 1
}

Write-Host '  butunluk : DLL ve surucu SHA256 dogrulandi'
Write-Host '  imza     : WinDivert64.sys gecerli'

Copy-Item $dll.FullName (Join-Path $Bin 'WinDivert.dll') -Force
Copy-Item $sys.FullName (Join-Path $Bin 'WinDivert64.sys') -Force
Remove-Item -Recurse -Force $Tmp -ErrorAction SilentlyContinue

Write-Host ''
Write-Host '  TAMAM' -ForegroundColor Green
Write-Host "    $Bin\WinDivert.dll"
Write-Host "    $Bin\WinDivert64.sys"
Write-Host ''
