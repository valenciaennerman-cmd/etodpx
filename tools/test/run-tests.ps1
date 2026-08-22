# dpetoos - kapsamli test paketi
#
# 67 kontrol: motor sagligi, gecikme ve paket kaybi, TLS el sikismalari,
# Discord uclari, yan hasar (yerel ag / 443 disi portlar / UDP) ve kararlilik.
#
# Yonetici gerekmez. dpetoos ACIK iken calistir.
#   powershell -ExecutionPolicy Bypass -File tools\test\run-tests.ps1
param([switch]$Quick)

$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'

$script:n = 0
$script:pass = 0
$script:fail = 0
$script:warn = 0
$script:log = New-Object Text.StringBuilder

function Out2([string]$s) {
    Write-Host $s
    [void]$script:log.AppendLine($s)
}

function T([string]$name, [scriptblock]$body) {
    $script:n++
    $detail = ''
    $state = 'HATA'
    try {
        $r = & $body
        if ($r -is [array]) { $state = $r[0]; $detail = $r[1] }
        elseif ($r -is [bool]) { $state = $(if ($r) { 'ok' } else { 'HATA' }) }
        else { $state = 'ok'; $detail = [string]$r }
    } catch {
        $state = 'HATA'; $detail = $_.Exception.Message.Split([char]10)[0]
    }

    switch ($state) {
        'ok'   { $script:pass++; $tag = '[ok]  '; $c = 'Green' }
        'uyari'{ $script:warn++; $tag = '[!]   '; $c = 'Yellow' }
        default{ $script:fail++; $tag = '[HATA]'; $c = 'Red' }
    }
    $line = ('{0,3}. {1} {2}' -f $script:n, $tag, $name.PadRight(46)) + $detail
    Write-Host $line -ForegroundColor $c
    [void]$script:log.AppendLine($line)
}

function Section([string]$t) {
    Out2 ''
    Out2 "--- $t ---"
}

# ---------------------------------------------------------------- yardimcilar

function TlsHandshake([string]$hostName, [int]$timeoutMs = 6000) {
    $c = $null; $ssl = $null
    try {
        $c = New-Object Net.Sockets.TcpClient
        $ar = $c.BeginConnect($hostName, 443, $null, $null)
        if (-not $ar.AsyncWaitHandle.WaitOne($timeoutMs)) { return $false }
        $c.EndConnect($ar)
        $ns = $c.GetStream(); $ns.ReadTimeout = $timeoutMs; $ns.WriteTimeout = $timeoutMs
        # Sertifika ADI ve ZINCIRI dogrulanir: engel sayfasi basari sayilmaz.
        $cb = [Net.Security.RemoteCertificateValidationCallback] {
            param($a, $b, $d, $e) return $e -eq [Net.Security.SslPolicyErrors]::None
        }
        $ssl = New-Object Net.Security.SslStream($ns, $false, $cb)
        $ssl.AuthenticateAsClient($hostName)
        return $ssl.IsAuthenticated
    } catch { return $false }
    finally {
        if ($ssl) { try { $ssl.Dispose() } catch {} }
        if ($c) { try { $c.Close() } catch {} }
    }
}

function TcpOpen([string]$hostName, [int]$port, [int]$timeoutMs = 5000) {
    $c = $null
    try {
        $c = New-Object Net.Sockets.TcpClient
        $ar = $c.BeginConnect($hostName, $port, $null, $null)
        if (-not $ar.AsyncWaitHandle.WaitOne($timeoutMs)) { return $false }
        $c.EndConnect($ar)
        return $true
    } catch { return $false }
    finally { if ($c) { try { $c.Close() } catch {} } }
}

function PingStats([string]$target, [int]$count, [int]$size = 32, [switch]$DF) {
    $args = @('-n', $count, '-l', $size)
    if ($DF) { $args += '-f' }
    $out = & ping @args $target 2>&1 | Out-String
    $loss = 100; $avg = -1; $min = -1; $max = -1
    if ($out -match '\((\d+)%') { $loss = [int]$Matches[1] }
    $m = [regex]::Match($out, '=\s*(\d+)ms.*?=\s*(\d+)ms.*?=\s*(\d+)ms')
    if ($m.Success) { $min = [int]$m.Groups[1].Value; $max = [int]$m.Groups[2].Value; $avg = [int]$m.Groups[3].Value }
    return [pscustomobject]@{ Loss = $loss; Avg = $avg; Min = $min; Max = $max; Raw = $out }
}

$pingCount = if ($Quick) { 8 } else { 20 }

Out2 ''
Out2 '  dpetoos test paketi'
Out2 '  ================='
Out2 ("  tarih : " + (Get-Date -Format 'yyyy-MM-dd HH:mm'))

# =========================================================== A. motor sagligi
Section 'A. motor ve surucu'

$dpetoos = Get-Process dpetoos -ErrorAction SilentlyContinue
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$bin = Join-Path $root 'bin'

T 'dpetoos sureci calisiyor' {
    if ($dpetoos) { @('ok', "PID $($dpetoos.Id)") } else { @('HATA', 'dpetoos acik degil - once baslat') }
}
T 'WinDivert servisi yuklu' {
    if (sc.exe query WinDivert 2>$null | Select-String 'RUNNING') { @('ok', 'RUNNING') }
    else { @('HATA', 'servis yok') }
}
T 'WinDivert dosyalari yerinde' {
    $a = Test-Path (Join-Path $bin 'WinDivert.dll')
    $b = Test-Path (Join-Path $bin 'WinDivert64.sys')
    if ($a -and $b) { @('ok', 'dll + sys') } else { @('HATA', "dll=$a sys=$b") }
}
T 'dpetoos.conf okunabiliyor' {
    $p = Join-Path $bin 'dpetoos.conf'
    if (-not (Test-Path $p)) { return @('HATA', 'dosya yok') }
    $ttl = (Select-String -Path $p -Pattern '^\s*fake_ttl_value\s*=\s*(\d+)' | Select-Object -Last 1).Matches.Groups[1].Value
    $sp = (Select-String -Path $p -Pattern '^\s*split_pos\s*=\s*(\S+)' | Select-Object -Last 1).Matches.Groups[1].Value
    @('ok', "split_pos=$sp ttl=$ttl")
}
T 'dpetoos bellek kullanimi makul' {
    if (-not $dpetoos) { return @('HATA', 'surec yok') }
    $mb = [math]::Round($dpetoos.WorkingSet64 / 1MB)
    if ($mb -lt 250) { @('ok', "$mb MB") } else { @('uyari', "$mb MB - yuksek") }
}
T 'dpetoos is parcacigi sayisi makul' {
    if (-not $dpetoos) { return @('HATA', 'surec yok') }
    $t = $dpetoos.Threads.Count
    if ($t -lt 40) { @('ok', "$t thread") } else { @('uyari', "$t thread") }
}

$cpuBefore = if ($dpetoos) { $dpetoos.TotalProcessorTime.TotalSeconds } else { 0 }
$testStart = Get-Date

# ================================================= B. gecikme ve paket kaybi
Section 'B. gecikme ve paket kaybi'

$gw = (Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
       Sort-Object RouteMetric | Select-Object -First 1).NextHop

$p1 = PingStats '1.1.1.1' $pingCount
T 'ping 1.1.1.1 - paket kaybi yok' {
    if ($p1.Loss -eq 0) { @('ok', "$pingCount/$pingCount, %0 kayip") } else { @('HATA', "%$($p1.Loss) kayip") }
}
T 'ping 1.1.1.1 - ortalama makul' {
    if ($p1.Avg -ge 0 -and $p1.Avg -lt 60) { @('ok', "$($p1.Avg) ms") } else { @('uyari', "$($p1.Avg) ms") }
}
T 'ping 1.1.1.1 - jitter dusuk' {
    $j = $p1.Max - $p1.Min
    if ($j -lt 40) { @('ok', "$j ms yayilim ($($p1.Min)-$($p1.Max))") } else { @('uyari', "$j ms yayilim") }
}

$p2 = PingStats '8.8.8.8' $pingCount
T 'ping 8.8.8.8 - paket kaybi yok' {
    if ($p2.Loss -eq 0) { @('ok', "%0 kayip") } else { @('HATA', "%$($p2.Loss) kayip") }
}
T 'ping 8.8.8.8 - ortalama makul' {
    if ($p2.Avg -ge 0 -and $p2.Avg -lt 80) { @('ok', "$($p2.Avg) ms") } else { @('uyari', "$($p2.Avg) ms") }
}

if ($gw) {
    $p3 = PingStats $gw 10
    T 'ping ag gecidi - paket kaybi yok' {
        if ($p3.Loss -eq 0) { @('ok', "$gw, %0 kayip") } else { @('HATA', "%$($p3.Loss) kayip") }
    }
    T 'ping ag gecidi - yerel gecikme dusuk' {
        if ($p3.Avg -ge 0 -and $p3.Avg -lt 15) { @('ok', "$($p3.Avg) ms") } else { @('uyari', "$($p3.Avg) ms") }
    }
} else {
    T 'ping ag gecidi' { @('uyari', 'ag gecidi bulunamadi') }
    T 'ping ag gecidi gecikme' { @('uyari', 'atlandi') }
}

$p4 = PingStats '1.1.1.1' 10 1400
T 'buyuk paket ping (1400 bayt)' {
    if ($p4.Loss -eq 0) { @('ok', "%0 kayip, $($p4.Avg) ms") } else { @('HATA', "%$($p4.Loss) kayip") }
}
T 'MTU testi (1472 bayt, parcalama yok)' {
    $p = PingStats '1.1.1.1' 4 1472 -DF
    if ($p.Loss -eq 0) { @('ok', '1500 MTU saglam') } else { @('uyari', 'parcalanma gerekiyor') }
}

$dip = $null
try { $dip = (Resolve-DnsName discord.com -Type A -ErrorAction Stop | Where-Object IPAddress | Select-Object -First 1).IPAddress } catch {}
T 'ping Discord sunucusu' {
    if (-not $dip) { return @('uyari', 'IP cozulemedi') }
    $p = PingStats $dip 10
    if ($p.Loss -le 20) { @('ok', "$dip, %$($p.Loss) kayip, $($p.Avg) ms") } else { @('uyari', "%$($p.Loss) kayip (ICMP kisitli olabilir)") }
}

# ================================================== C. genel siteler (TLS)
Section 'C. TLS el sikismasi - genel siteler (tam sertifika dogrulamasi)'

$general = @(
    'www.google.com','www.youtube.com','github.com','www.wikipedia.org',
    'www.microsoft.com','www.cloudflare.com','www.instagram.com','steamcommunity.com',
    'www.reddit.com','outlook.office.com','x.com','twitter.com',
    'api.telegram.org','web.whatsapp.com','www.netflix.com','open.spotify.com',
    'www.amazon.com','stackoverflow.com','www.apple.com','login.live.com'
)
foreach ($h in $general) {
    T "TLS $h" { if (TlsHandshake $h) { @('ok', '') } else { @('HATA', 'el sikisma basarisiz') } }.GetNewClosure()
}

# ==================================================== D. Discord uc noktalari
Section 'D. Discord uc noktalari'

$discord = @(
    'discord.com','discordapp.com','cdn.discordapp.com','media.discordapp.net',
    'updates.discord.com','gateway.discord.gg','discord.gg','status.discord.com'
)
foreach ($h in $discord) {
    T "TLS $h" { if (TlsHandshake $h) { @('ok', '') } else { @('HATA', 'el sikisma basarisiz') } }.GetNewClosure()
}

# ================================================== E. HTTP (port 80) ve API
Section 'E. HTTP ve uygulama katmani'

T 'HTTP port 80 - example.com' {
    try { $r = Invoke-WebRequest 'http://example.com' -UseBasicParsing -TimeoutSec 15; @('ok', "HTTP $($r.StatusCode)") }
    catch { @('HATA', $_.Exception.Message.Split([char]10)[0]) }
}
T 'HTTP port 80 - msftconnecttest' {
    # Microsoft'un baglanti denetim ucu: duz HTTP ve her zaman ayakta.
    try {
        $r = Invoke-WebRequest 'http://www.msftconnecttest.com/connecttest.txt' -UseBasicParsing -TimeoutSec 15
        if ($r.Content.Trim() -eq 'Microsoft Connect Test') { @('ok', "HTTP $($r.StatusCode), icerik dogru") }
        else { @('uyari', 'beklenmeyen icerik') }
    } catch { @('HATA', $_.Exception.Message.Split([char]10)[0]) }
}
T 'Discord guncelleme ucu (HTTP 200)' {
    try {
        $u = 'https://updates.discord.com/distributions/app/manifests/latest?channel=stable&platform=win&arch=x64'
        $r = Invoke-WebRequest $u -UseBasicParsing -TimeoutSec 20
        @('ok', "HTTP $($r.StatusCode), $($r.RawContentLength) bayt")
    } catch { @('HATA', $_.Exception.Message.Split([char]10)[0]) }
}
T 'Discord API gateway' {
    try { $r = Invoke-WebRequest 'https://discord.com/api/v9/gateway' -UseBasicParsing -TimeoutSec 20; @('ok', "HTTP $($r.StatusCode)") }
    catch { @('HATA', $_.Exception.Message.Split([char]10)[0]) }
}
T 'Google API (JSON)' {
    try { $r = Invoke-WebRequest 'https://www.googleapis.com/discovery/v1/apis' -UseBasicParsing -TimeoutSec 20; @('ok', "HTTP $($r.StatusCode)") }
    catch { @('uyari', 'ulasilamadi') }
}

# =============================================== F. yan hasar / regresyon
Section 'F. yan hasar kontrolu'

T 'DNS cozumleme calisiyor' {
    try { $r = Resolve-DnsName 'www.google.com' -Type A -ErrorAction Stop; @('ok', ($r | Where-Object IPAddress | Select-Object -First 1).IPAddress) }
    catch { @('HATA', 'cozulemedi') }
}
T 'DNS - engelli alan adi dogru cozuluyor' {
    try {
        $ip = (Resolve-DnsName 'discord.com' -Type A -ErrorAction Stop | Where-Object IPAddress | Select-Object -First 1).IPAddress
        if ($ip -eq '195.175.254.2') { @('HATA', 'engel sayfasi IP - DNS zehirli') } else { @('ok', $ip) }
    } catch { @('HATA', 'cozulemedi') }
}
T 'localhost TCP baglantisi bozulmamis' {
    $l = $null
    try {
        $l = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0)
        $l.Start()
        $port = $l.LocalEndpoint.Port
        $ok = TcpOpen '127.0.0.1' $port 3000
        if ($ok) { @('ok', "port $port") } else { @('HATA', 'baglanilamadi') }
    } catch { @('HATA', $_.Exception.Message.Split([char]10)[0]) }
    finally { if ($l) { try { $l.Stop() } catch {} } }
}
T 'yerel ag gecidine erisim bozulmamis' {
    if (-not $gw) { return @('uyari', 'ag gecidi yok') }
    if ((TcpOpen $gw 80 2500) -or (TcpOpen $gw 443 2500) -or (TcpOpen $gw 53 2500)) { @('ok', "$gw yanit veriyor") }
    else { @('uyari', 'modem web arayuzu kapali olabilir') }
}
T '443/80 disi port - SMTP 587' {
    if (TcpOpen 'smtp.gmail.com' 587 6000) { @('ok', 'acildi') } else { @('uyari', 'ISS engelliyor olabilir') }
}
T '443/80 disi port - IMAP 993' {
    if (TcpOpen 'imap.gmail.com' 993 6000) { @('ok', 'acildi') } else { @('uyari', 'ulasilamadi') }
}
T '443/80 disi port - DNS over TCP 53' {
    if (TcpOpen '1.1.1.1' 53 5000) { @('ok', 'acildi') } else { @('HATA', 'kapali') }
}
T '443/80 disi port - SSH 22 (github)' {
    if (TcpOpen 'github.com' 22 6000) { @('ok', 'acildi') } else { @('uyari', 'ulasilamadi') }
}
T 'UDP calisiyor (DNS sorgusu)' {
    $u = $null
    try {
        $u = New-Object Net.Sockets.UdpClient
        $u.Client.ReceiveTimeout = 3000
        # minimal DNS sorgusu: example.com A
        $q = [byte[]](0xAB,0xCD,0x01,0x00,0,1,0,0,0,0,0,0,
                      7,101,120,97,109,112,108,101,3,99,111,109,0,0,1,0,1)
        [void]$u.Send($q, $q.Length, '1.1.1.1', 53)
        $ep = New-Object Net.IPEndPoint([Net.IPAddress]::Any, 0)
        $r = $u.Receive([ref]$ep)
        if ($r.Length -gt 12) { @('ok', "$($r.Length) bayt yanit") } else { @('HATA', 'kisa yanit') }
    } catch { @('HATA', 'UDP yaniti yok') }
    finally { if ($u) { try { $u.Close() } catch {} } }
}
T 'IPv6 cozumleme' {
    try {
        $r = Resolve-DnsName 'www.google.com' -Type AAAA -ErrorAction Stop | Where-Object IPAddress
        if ($r) { @('ok', ($r | Select-Object -First 1).IPAddress) } else { @('uyari', 'AAAA kaydi yok') }
    } catch { @('uyari', 'IPv6 yok') }
}
T 'sertifika zinciri dogrulaniyor (negatif test)' {
    # Kasitli olarak gecersiz bir sertifika: BASARISIZ olmasi DOGRU sonuctur.
    if (TlsHandshake 'expired.badssl.com' 8000) { @('HATA', 'gecersiz sertifika kabul edildi!') }
    else { @('ok', 'gecersiz sertifika reddedildi') }
}

# ============================================================ G. hiz
Section 'G. aktarim hizi'

function DownloadMbps([int]$bytes) {
    # Diske yazilmaz; veri bir tampona okunup atilir, amac yalnizca sure olcmek.
    $req = [Net.HttpWebRequest]::Create("https://speed.cloudflare.com/__down?bytes=$bytes")
    $req.Timeout = 40000
    $req.ReadWriteTimeout = 40000
    $resp = $req.GetResponse()
    $st = $resp.GetResponseStream()
    $buf = New-Object byte[] 131072
    $total = 0
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while (($read = $st.Read($buf, 0, $buf.Length)) -gt 0) { $total += $read }
    $sw.Stop()
    $st.Close(); $resp.Close()
    if ($total -lt $bytes * 0.9) { return -1 }
    return [math]::Round(($total * 8) / $sw.Elapsed.TotalSeconds / 1MB, 1)
}

T 'indirme hizi (3 x 25 MB, ortanca)' {
    # Tek ve kisa bir olcum ag gurultusunden ayirt edilemez; uc olcumun
    # ORTANCASI aliniyor ve transfer suresi anlamli olacak kadar buyuk.
    $runs = @()
    for ($i = 0; $i -lt 3; $i++) {
        try { $m = DownloadMbps 25000000; if ($m -gt 0) { $runs += $m } } catch {}
        Start-Sleep -Milliseconds 400
    }
    if ($runs.Count -eq 0) { return @('uyari', 'hiz testi ulasilamadi') }
    $sorted = $runs | Sort-Object
    $median = $sorted[[int]($sorted.Count / 2)]
    @('ok', "$median Mbit/s  (olcumler: $($runs -join ', '))")
}

# ====================================================== H. kararlilik
Section 'H. kararlilik'

T '40 ardisik TLS el sikismasi' {
    $hosts = @('www.google.com','github.com','discord.com','x.com','www.cloudflare.com')
    $ok = 0; $tot = 0
    for ($i = 0; $i -lt 8; $i++) {
        foreach ($h in $hosts) { $tot++; if (TlsHandshake $h 6000) { $ok++ } }
    }
    if ($ok -eq $tot) { @('ok', "$ok/$tot") }
    elseif ($ok -ge $tot * 0.9) { @('uyari', "$ok/$tot") }
    else { @('HATA', "$ok/$tot") }
}
T 'esZamanli 10 baglanti' {
    $jobs = 1..10 | ForEach-Object {
        Start-Job -ScriptBlock {
            param($h)
            try {
                $c = New-Object Net.Sockets.TcpClient
                $c.Connect($h, 443)
                $ns = $c.GetStream(); $ns.ReadTimeout = 8000; $ns.WriteTimeout = 8000
                $ssl = New-Object Net.Security.SslStream($ns, $false, { param($a,$b,$d,$e) $e -eq 0 })
                $ssl.AuthenticateAsClient($h)
                $r = $ssl.IsAuthenticated
                $ssl.Dispose(); $c.Close()
                return $r
            } catch { return $false }
        } -ArgumentList 'discord.com'
    }
    $res = $jobs | Wait-Job -Timeout 40 | Receive-Job
    $jobs | Remove-Job -Force -ErrorAction SilentlyContinue
    $ok = @($res | Where-Object { $_ -eq $true }).Count
    if ($ok -ge 9) { @('ok', "$ok/10") } elseif ($ok -ge 7) { @('uyari', "$ok/10") } else { @('HATA', "$ok/10") }
}
T 'testler sonrasi dpetoos hala calisiyor' {
    if (Get-Process dpetoos -ErrorAction SilentlyContinue) { @('ok', 'ayakta') } else { @('HATA', 'surec olmus') }
}
T 'testler sonrasi surucu hala yuklu' {
    if (sc.exe query WinDivert 2>$null | Select-String 'RUNNING') { @('ok', 'RUNNING') } else { @('HATA', 'servis dustu') }
}
T 'dpetoos CPU tuketimi dusuk kaldi' {
    $d2 = Get-Process dpetoos -ErrorAction SilentlyContinue
    if (-not $d2) { return @('HATA', 'surec yok') }
    $used = $d2.TotalProcessorTime.TotalSeconds - $cpuBefore
    $wall = ((Get-Date) - $testStart).TotalSeconds
    $pct = if ($wall -gt 0) { [math]::Round(100 * $used / $wall, 1) } else { 0 }
    if ($pct -lt 10) { @('ok', "test boyunca %$pct CPU ($([math]::Round($used,1)) sn)") }
    else { @('uyari', "%$pct CPU") }
}
T 'ping son kontrol - hala kayipsiz' {
    $p = PingStats '1.1.1.1' 10
    if ($p.Loss -eq 0) { @('ok', "%0 kayip, $($p.Avg) ms") } else { @('HATA', "%$($p.Loss) kayip") }
}

# ================================================================== ozet
Out2 ''
Out2 '================================================================'
Out2 ("  TOPLAM {0} test   |   gecen {1}   |   uyari {2}   |   HATA {3}" -f $script:n, $script:pass, $script:warn, $script:fail)
Out2 '================================================================'
Out2 ''
if ($script:fail -eq 0) {
    Out2 '  Hicbir test basarisiz olmadi.'
} else {
    Out2 "  $($script:fail) test basarisiz - yukaridaki [HATA] satirlarina bak."
}
Out2 ''

$logPath = Join-Path $bin 'dpetoos-test.log'
try {
    [IO.File]::WriteAllText($logPath, $script:log.ToString(), (New-Object Text.UTF8Encoding($false)))
    Out2 "  gunluk: $logPath"
} catch {}

exit $(if ($script:fail -eq 0) { 0 } else { 1 })
