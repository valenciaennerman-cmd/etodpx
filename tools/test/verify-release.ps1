# etodpx - yayin oncesi denetim
#
# Depoyu GitHub'a atmadan once calistir. Yaptigi sey su: git'in gercekten
# takip edecegi dosyalari BOS bir klasore kopyalar ve orada sifirdan derler.
# Yani "bende calisiyordu" hatalarini yakalar - eksik dosya, .gitignore'a
# yanlislikla giren bir kaynak, sabit kodlanmis yol.
#
# Yonetici gerekmez.
#   powershell -ExecutionPolicy Bypass -File tools\test\verify-release.ps1

$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'

$script:n = 0; $script:pass = 0; $script:fail = 0; $script:warn = 0

function T([string]$name, [scriptblock]$body) {
    $script:n++
    $state = 'HATA'; $detail = ''
    try {
        $r = & $body
        if ($r -is [array]) { $state = $r[0]; $detail = $r[1] }
        elseif ($r -is [bool]) { $state = $(if ($r) { 'ok' } else { 'HATA' }) }
        else { $state = 'ok'; $detail = [string]$r }
    } catch { $state = 'HATA'; $detail = $_.Exception.Message.Split([char]10)[0] }

    switch ($state) {
        'ok'    { $script:pass++; $tag = '[ok]  '; $c = 'Green' }
        'uyari' { $script:warn++; $tag = '[!]   '; $c = 'Yellow' }
        default { $script:fail++; $tag = '[HATA]'; $c = 'Red' }
    }
    Write-Host (('{0,3}. {1} {2}' -f $script:n, $tag, $name.PadRight(48)) + $detail) -ForegroundColor $c
}

function Section([string]$t) { Write-Host ''; Write-Host "--- $t ---" }

$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location $Root

Write-Host ''
Write-Host '  etodpx yayin denetimi'
Write-Host '  ===================='
Write-Host "  depo: $Root"

# ===================================================== A. depo hijyeni
Section 'A. depo hijyeni'

$hasGit = $null -ne (Get-Command git -ErrorAction SilentlyContinue)

T 'git bulundu' {
    if ($hasGit) { @('ok', (git --version)) } else { @('HATA', 'git yok - klon testi yapilamaz') }
}
T '.gitignore var' { if (Test-Path '.gitignore') { $true } else { @('HATA', 'yok') } }
T 'LICENSE var' { if (Test-Path 'LICENSE') { @('ok', (Get-Content LICENSE -TotalCount 1)) } else { @('HATA', 'yok') } }
T 'README var' { if (Test-Path 'README.md') { @('ok', "$([math]::Round((Get-Item README.md).Length/1KB,1)) KB") } else { @('HATA', 'yok') } }
T 'uygulama simgesi depoda' {
    if (Test-Path 'assets\etodpx.ico') { @('ok', "$([math]::Round((Get-Item assets\etodpx.ico).Length/1KB,1)) KB") }
    else { @('HATA', 'assets\etodpx.ico yok - yeni klonda simge olmaz') }
}
# Takip edilmeyen bir .cs dosyasi burada derlenir ama temiz klonda YOKTUR.
# Eksigi derleme hatasi vermiyorsa (ornegin yalnizca nitelik iceriyorsa)
# fark hic goze carpmaz; surumsuz exe boyle cikti.
T 'butun kaynak dosyalari git takibinde' {
    if (-not $hasGit) { return @('uyari', 'git yok') }
    $tracked = @(git ls-files 'src/*.cs' 'tools/*.cs') | ForEach-Object { $_.Replace('/', '\') }
    $onDisk = @(Get-ChildItem -Recurse -Filter *.cs -Path 'src', 'tools' |
                ForEach-Object { $_.FullName.Substring($Root.Length + 1) })
    $missing = @($onDisk | Where-Object { $tracked -notcontains $_ })
    if ($missing.Count -eq 0) { @('ok', "$($onDisk.Count) dosya takipte") }
    else { @('HATA', 'takip edilmiyor: ' + ($missing -join ', ')) }
}
T 'surum numaralari tutarli' {
    $ai = (Select-String -Path 'src\AssemblyInfo.cs' -Pattern 'AssemblyFileVersion\("([^"]+)"' -ErrorAction SilentlyContinue).Matches.Groups[1].Value
    $mf = (Select-String -Path 'src\app.manifest' -Pattern 'assemblyIdentity version="([^"]+)"').Matches.Groups[1].Value
    if (-not $ai) { return @('HATA', 'AssemblyInfo.cs okunamadi') }
    if ($ai -ne $mf) { @('HATA', "AssemblyInfo $ai <> manifest $mf") } else { @('ok', $ai) }
}

# git'in takip edecegi dosya listesini cikar (gecici depo, mevcut .git'e dokunmaz)
$tracked = @()
if ($hasGit) {
    $tmpGit = Join-Path ([IO.Path]::GetTempPath()) ("etodpx-verify-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force $tmpGit | Out-Null
    try {
        git --git-dir="$tmpGit\.git" --work-tree="$Root" init -q 2>$null | Out-Null
        git --git-dir="$tmpGit\.git" --work-tree="$Root" add -A 2>$null | Out-Null
        $tracked = git --git-dir="$tmpGit\.git" --work-tree="$Root" ls-files 2>$null
    } catch {}
    Remove-Item -Recurse -Force $tmpGit -ErrorAction SilentlyContinue
}

T 'takip edilen dosya sayisi makul' {
    if ($tracked.Count -eq 0) { return @('HATA', 'liste alinamadi') }
    if ($tracked.Count -lt 15) { return @('HATA', "$($tracked.Count) dosya - cok az, bir seyler eksik") }
    @('ok', "$($tracked.Count) dosya")
}
T 'gunluk dosyasi SIZMIYOR' {
    $bad = $tracked | Where-Object { $_ -like '*.log' }
    if ($bad) { @('HATA', "SIZINTI: $($bad -join ', ')") } else { @('ok', 'temiz') }
}
T 'derlenmis ikili SIZMIYOR' {
    $bad = $tracked | Where-Object { $_ -match '\.(exe|dll|sys|pdb)$' }
    if ($bad) { @('HATA', "SIZINTI: $($bad -join ', ')") } else { @('ok', 'temiz') }
}
T 'bin klasoru SIZMIYOR' {
    $bad = $tracked | Where-Object { $_ -like 'bin/*' -or $_ -like 'bin\*' }
    if ($bad) { @('HATA', "SIZINTI: $($bad.Count) dosya") } else { @('ok', 'temiz') }
}
T 'kisisel bilgi SIZMIYOR' {
    # Desenler parcalardan kuruluyor: aksi halde bu betik kendi arama desenini
    # bulup kendisini sizinti olarak raporlar (ilk denemede aynen oyle oldu).
    $needles = @(
        ('C:' + [char]92 + 'Users'),
        ('One' + 'Drive'),
        ('@gm' + 'ail.com'),
        ('@hot' + 'mail.com')
    )
    $hits = @()
    foreach ($f in $tracked) {
        if (-not (Test-Path $f)) { continue }
        if ((Get-Item $f).Length -gt 500KB) { continue }
        foreach ($needle in $needles) {
            $m = Select-String -Path $f -Pattern $needle -SimpleMatch -ErrorAction SilentlyContinue
            if ($m) { $hits += "$f`:$($m[0].LineNumber)"; break }
        }
    }
    if ($hits) { @('HATA', "SIZINTI: $($hits -join ', ')") } else { @('ok', 'temiz') }
}
T 'kaynak dosyalari UTF-8 BOM ile' {
    $bad = @()
    foreach ($f in ($tracked | Where-Object { $_ -like '*.cs' })) {
        if (-not (Test-Path $f)) { continue }
        $b = [IO.File]::ReadAllBytes($f)
        if ($b.Length -lt 3 -or $b[0] -ne 0xEF -or $b[1] -ne 0xBB -or $b[2] -ne 0xBF) { $bad += $f }
    }
    if ($bad) { @('uyari', "$($bad.Count) dosyada BOM yok (Turkce karakter bozulabilir)") }
    else { @('ok', 'hepsi BOM ile') }
}

# ============================================ B. sifirdan klon derlemesi
Section 'B. sifirdan klon derlemesi (yeni kullanicinin gordugu)'

$clone = Join-Path ([IO.Path]::GetTempPath()) ("etodpx-clone-" + [guid]::NewGuid().ToString('N'))
$script:cloneOk = $false

T 'temiz kopya olusturuldu' {
    if ($tracked.Count -eq 0) { return @('HATA', 'takip listesi yok') }
    New-Item -ItemType Directory -Force $clone | Out-Null
    $copied = 0
    foreach ($f in $tracked) {
        $src = Join-Path $Root $f
        if (-not (Test-Path $src)) { continue }
        $dst = Join-Path $clone $f
        $dir = Split-Path -Parent $dst
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
        Copy-Item $src $dst -Force
        $copied++
    }
    $script:cloneOk = $true
    @('ok', "$copied dosya kopyalandi")
}

T 'temiz kopyada bin/ YOK (dogru baslangic)' {
    if (Test-Path (Join-Path $clone 'bin')) { @('HATA', 'bin klasoru kopyaya girmis') } else { @('ok', 'yok') }
}

T 'build.cmd temiz kopyada calisiyor' {
    if (-not $script:cloneOk) { return @('HATA', 'kopya yok') }
    Push-Location $clone
    try {
        $out = cmd /c ".\build.cmd" 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) { return @('HATA', ($out -split "`n" | Select-Object -Last 3) -join ' ') }
        @('ok', 'derlendi')
    } finally { Pop-Location }
}
T 'etodpx.exe uretildi' {
    $p = Join-Path $clone 'bin\etodpx.exe'
    if (Test-Path $p) { @('ok', "$([math]::Round((Get-Item $p).Length/1KB)) KB") } else { @('HATA', 'yok') }
}
T 'simge exe icine gomuldu' {
    $p = Join-Path $clone 'bin\etodpx.exe'
    if (-not (Test-Path $p)) { return @('HATA', 'exe yok') }
    Add-Type -AssemblyName System.Drawing
    try { $i = [Drawing.Icon]::ExtractAssociatedIcon($p); if ($i) { @('ok', 'var') } else { @('HATA', 'yok') } }
    catch { @('HATA', 'okunamadi') }
}
# Surum bilgisi yalnizca src\AssemblyInfo.cs'ten gelir. O dosya takipte
# degilse temiz klon derlenir ama exe 0.0.0.0 cikar: kullanici Ozellikler >
# Ayrintilar penceresinde hangi surumu indirdigini goremez.
T 'exe surum bilgisi tasiyor' {
    $p = Join-Path $clone 'bin\etodpx.exe'
    if (-not (Test-Path $p)) { return @('HATA', 'exe yok') }
    $v = (Get-Item $p).VersionInfo.FileVersion
    if (-not $v -or $v -eq '0.0.0.0') { @('HATA', "surum = '$v' (AssemblyInfo.cs klona girmemis)") }
    else { @('ok', $v) }
}
T 'ayarlayici temiz kopyada derleniyor' {
    Push-Location $clone
    try {
        cmd /c "tools\tune\build-tune.cmd" 2>&1 | Out-Null
        if (Test-Path 'bin\etodpx-tune.exe') { @('ok', 'derlendi') } else { @('HATA', 'derlenemedi') }
    } finally { Pop-Location }
}
T 'oz test temiz kopyada gecti' {
    Push-Location $clone
    try {
        # WinDivert.dll filtre derleyicisi icin gerekli; kaynak depoda yok,
        # yeni kullanici setup-windivert ile indirir. Testte odunc aliyoruz.
        $wd = Join-Path $Root 'bin\WinDivert.dll'
        if (Test-Path $wd) { Copy-Item $wd 'bin\' -Force }
        $out = cmd /c "tools\selftest\build-selftest.cmd" 2>&1 | Out-String
        $line = ($out -split "`n" | Where-Object { $_ -match 'gecen:' } | Select-Object -Last 1)
        if ($line -match 'kalan:\s*0') { @('ok', $line.Trim()) }
        else { @('HATA', ($line + '').Trim()) }
    } finally { Pop-Location }
}

# ================================================ C. yeni kullanici akisi
Section 'C. yeni kullanicinin bagimliliklari'

T 'C# derleyicisi Windows ile geliyor' {
    $csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
    if (Test-Path $csc) { @('ok', 'var (ek kurulum gerekmiyor)') } else { @('HATA', 'yok') }
}
T 'WPF derlemeleri yerinde' {
    $wpf = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\WPF\PresentationFramework.dll"
    if (Test-Path $wpf) { @('ok', 'var') } else { @('HATA', 'yok') }
}
T 'WinDivert indirme adresi ulasilabilir' {
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $url = 'https://github.com/basil00/WinDivert/releases/download/v2.2.2/WinDivert-2.2.2-A.zip'
        $r = Invoke-WebRequest -Uri $url -Method Head -UseBasicParsing -TimeoutSec 20
        @('ok', "HTTP $($r.StatusCode)")
    } catch { @('HATA', 'indirme adresi yanit vermiyor - yeni kullanici kuramaz') }
}
T 'README icindeki dosya yollari gercek' {
    $missing = @()
    foreach ($m in (Select-String -Path 'README.md' -Pattern '(?<![\w/\\])(tools|src|assets)[\\/][\w\\/.\-]+' -AllMatches).Matches) {
        $rel = $m.Value -replace '/', '\'
        if ($rel -match '\*') { continue }
        if (-not (Test-Path $rel)) { $missing += $rel }
    }
    if ($missing) { @('uyari', "bulunamayan: $(($missing | Select-Object -Unique) -join ', ')") }
    else { @('ok', 'hepsi var') }
}

Remove-Item -Recurse -Force $clone -ErrorAction SilentlyContinue

# ================================================================ ozet
Write-Host ''
Write-Host '================================================================'
Write-Host ("  TOPLAM {0}   |   gecen {1}   |   uyari {2}   |   HATA {3}" -f $script:n, $script:pass, $script:warn, $script:fail)
Write-Host '================================================================'
Write-Host ''
if ($script:fail -eq 0) {
    Write-Host '  Depo yayina hazir.' -ForegroundColor Green
} else {
    Write-Host "  $($script:fail) sorun var - push etmeden once duzelt." -ForegroundColor Red
}
Write-Host ''
exit $(if ($script:fail -eq 0) { 0 } else { 1 })
