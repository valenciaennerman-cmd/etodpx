# etodpx

Windows için DPI atlatma aracı. GoodbyeDPI ve zapret ile aynı işi yapar, ama
tek bir tasarım hedefi var: **ping'e ve hıza dokunmamak.**

Arayüzde tek bir düğme var: başlat/durdur. Başka hiçbir şey yok.

---

## Hızlı başlangıç

```bat
etodpx.cmd
```

Bu kadar. Betik gerekirse derler, WinDivert eksikse indirir, sonra uygulamayı
yönetici olarak açar. Aç, düğmeye bas, siteyi dene.

**DNS'ini şimdiden değiştirme.** Gerekip gerekmediğini araç sana söyleyecek.

Gereken tek şey Windows'un kendisi — .NET SDK, Visual Studio, Node, hiçbiri
gerekmiyor. Derleyici (`csc.exe`) Windows'la birlikte geliyor, ayrıca bir şey
kurmuyorsun.

### Hangi dosyayı ne zaman çalıştıracaksın

Depoda derlenmiş `.exe` yok — kaynak kodu var, ilk çalıştırmada kendisi
derleniyor. Karışmasın diye:

| Dosya | Ne yapar | Ne zaman |
|---|---|---|
| **`etodpx.cmd`** | Gerekirse derler, WinDivert eksikse indirir, uygulamayı açar | **Normalde sadece buna çift tıkla.** Tek ihtiyacın olan bu. |
| `build.cmd` | Yalnızca derler, hiçbir şey açmaz | Kaynak kodu değiştirdiysen |
| `tools\setup-windivert.cmd` | Sürücüyü indirir | `etodpx.cmd` zaten çağırıyor; elle kurmak istersen |
| `bin\etodpx-tune.exe` | Hattına uyan ayarı ölçerek bulur | Bir site açılmıyorsa |
| `tools\test\test-all.cmd` | 174 kontrolün hepsini çalıştırır | Kod değiştirdiysen, göndermeden önce |

Yani sıradan bir kullanıcı için tek adım var:

```bat
etodpx.cmd
```

İlk çalıştırma birkaç saniye sürer (derleme + sürücü indirme), sonrakiler anında
açılır. Uygulama yönetici izni ister — WinDivert bir çekirdek sürücüsü, onsuz
paket yakalanamaz.


### Site hâlâ açılmıyorsa

```bat
bin\etodpx-tune.exe acilmayansite.com
```

Bu araç **tahmin etmez, ölçer.** Önce engelin cinsini bulur:

| Ne diyorsa | Ne yapacaksın |
|---|---|
| `zaten aciliyor` | Bir şey yapma, o sitede engel yok |
| `DNS ZEHIRLI` | DNS'ini **1.1.1.1 / 1.0.0.1** yap — araç adım adım söyler |
| `SNI engeli` | Hiçbir şey; araç 31 stratejiyi dener ve doğrusunu `etodpx.conf`'a yazar |
| `IP seviyesinde engel` | etodpx bunu aşamaz, VPN gerekir |
| `alan adi cozulemiyor` | Yazım hatası |

Sonra etodpx'i yeniden başlat.

> **Önemli:** Depodaki `fake_ttl_value = 4` yalnızca örnek başlangıç değeridir.
> Sahte TCP paketi varsayılan olarak kapalıdır; bu değer ancak `fake_ttl = true`
> yapıldığında kullanılır. Doğru değer operatöre ve hatta göre değişir — onu
> güvenle bulan araç `etodpx-tune.exe`'dir.

### Ayarı başka cihaza taşıma — çalışmaz

`bin\etodpx.conf` dosyasını kopyalayıp başka bir makinede kullanmak **işe
yaramaz**, aynı evdeki başka bir bilgisayarda bile. Sebebi `fake_ttl_value`:
bu sayı, sahte paketin DPI kutusuna ulaşması için kaç hop yaşayacağını söyler
ve makineden makineye değişir.

En sık karşılaşılan hali: **sanal makine.** NAT arkasındaki bir VM'de trafik
önce sanal ağ geçidine gider, yani ana makineye göre **bir hop fazladır**. Ana
makinede DPI 4. hop'taysa VM'de 5. hop'tadır; `fake_ttl_value = 4` ile sahte
paket DPI'a hiç ulaşmaz ve hiçbir şey atlatılmaz. Uygulama "KORUMA AÇIK" der
ama site açılmaz.

Wi-Fi ile kablolu bağlantı arasında geçmek, modem değiştirmek, farklı ISS —
hepsi aynı sonucu verir.

**Doğrusu:** her yeni cihazda bir kez ayarlayıcıyı çalıştır.

```bat
bin\etodpx-tune.exe discord.com
```

Klasörü paylaşırken `bin\etodpx.conf` dosyasını **hiç koyma** — uygulama ilk
açılışta güvenli şablondan kendisi üretir.

### DNS ne zaman gerekir?

Sadece ISS'in o alan adı için sahte adres döndürüyorsa. Türkiye'de bazı siteler
DNS'ten, bazıları SNI'dan engelli — ikisi ayrı şeyler. Örneğin bu satırların
yazıldığı hatta `discord.com` DNS'ten de SNI'dan da engelliyken `x.com` sadece
SNI'dan engelliydi. etodpx paketleri parçalar; DNS seni baştan yanlış sunucuya
yollarsa yapabileceği bir şey yoktur. Kendin bakmak istersen:

```bat
nslookup discord.com
nslookup discord.com 1.1.1.1
```

İki cevap tamamen farklıysa DNS'in zehirli.

**Türkiye'de büyük ihtimalle gerekecek.** Test ettiğim hatta ISS'in DNS
sunucuları `discord.com` için engel sayfasını (`195.175.254.2`) döndürüyor.
Elle ayarlanan `1.1.1.1 / 1.0.0.1` kaldırılıp otomatiğe dönüldüğünde Discord
tekrar açılmaz hale geldi — yani DHCP ile gelen çözümleyici zehirli.

Yine de bunu peşinen yapma: `etodpx-tune.exe` DNS'i sistem çözümleyicisiyle
iki bağımsız kaynağı karşılaştırarak ölçüyor ve gerekiyorsa `DNS ZEHIRLI` deyip
adımları yazıyor. Gerekmiyorsa boşuna uğraşmamış olursun.

---

## Ping ve hız neden etkilenmiyor

Bu tür araçların çoğu 80/443 üzerindeki **bütün** paketleri kullanıcı alanına
çeker, inceler ve geri enjekte eder. Her paket için kernel↔userspace gidiş
dönüşü demek bu — indirme sırasında saniyede on binlerce kez.

etodpx bunu yapmaz. Filtre WinDivert sürücüsünün içinde, çekirdekte değerlendirilir
ve yalnızca şunları geçirir:

| Yakalanan | Yakalanmayan |
|---|---|
| TLS ClientHello (443, ilk paket) | Tüm TCP veri paketleri |
| HTTP istek satırı (80, ilk paket) | ICMP / ping |
| QUIC Initial (UDP 443, ilk paket) | Oyun trafiği, UDP |
| | İndirme, yükleme, video akışı |
| | Yerel ağ, modem arayüzü, loopback |

Yani bir bağlantı başına **bir** paket işlenir; gerisi çekirdeği hiç terk etmez.
Bir sayfa açarken bu 20–50 paket eder. Aktarımın kendisi sıfır ek yük görür,
`ping` çıktısı hiç değişmez.

Motor tarafında da gecikme oynamasın diye:

- Paket yolunda **hiç bellek tahsisi yok** — bütün tamponlar açılışta alınır,
  böylece çöp toplayıcı duraklaması paket işlemeye karışmaz.
- Paket iş parçacıkları `Highest` öncelikte ve neredeyse hep bloke bekliyor;
  sürecin tamamı normal öncelikte kalır, böylece tam ekran bir oyunla
  işlemci yarışına girmez.
- Sürücü kuyruğu tavana ayarlı (16384 paket / 32 MB) — ani yoğunlukta
  paket düşmez.
- Filtre önce ucuz alanlara bakar (yön, protokol, port), payload kontrolü en sonda.

---

## Nasıl çalışıyor

Engelleme, DPI kutusunun TLS ClientHello içindeki alan adını (SNI) ya da HTTP
`Host:` başlığını okumasıyla oluyor. etodpx bunu üç katmanda bozuyor:

**1. Sahte paket (decoy).** Gerçek veriden hemen önce, aynı sıra numarasıyla
zararsız bir alan adı taşıyan sahte bir ClientHello gönderilir. DPI ilk gördüğü
bunu işler. Sahte paket sunucuya asla ulaşmaz:

- *düşük TTL* ile gider — DPI kutusunu geçer ama sunucuya varmadan yolda ölür;
- *bozuk TCP checksum* ile gider — checksum doğrulamayan DPI paketi yutar,
  sunucunun TCP yığını atar;
- *pencere dışı sıra numarası* ile gider — sunucu "eski veri" deyip atar.

Üçü de aynı işi farklı yoldan yapar ve **hangisinin çalıştığı hatta göre
değişir**. Test ettiğim Türk Telekom hattında yalnızca TTL yöntemi işe yaradı;
diğer ikisi hiçbir şey açmadı. Bu yüzden `etodpx-tune.exe` var — doğru olanı
tahminle değil ölçerek buluyor.

**2. Bölme (split).** Gerçek istek, tam olarak alan adının ana etiketinin
ortasından ikiye ayrılır — `you|tube.com` gibi. Hiçbir pakette imzanın tamamı
bulunmaz.

**3. Ters sıra (disorder).** İkinci parça önce gönderilir. Akışı düzgün
birleştirmeyen DPI'lar alan adını hiç göremez. Sunucunun TCP yığını ise
sıra dışı segmentleri zaten normal şekilde toparlar — bu standart TCP davranışı,
kayıp veya yeniden gönderim üretmez.

QUIC (HTTP/3) tarafında da aynı mantık: sahte bir Initial paketi düşük TTL ile
önden gider, gerçeği arkasından. QUIC kapatılmadığı için Google/YouTube hızı
olduğu gibi kalır.

### Protokol tanıma neden yapısal doğrulanıyor

Bir paketi "bu TLS ClientHello" ya da "bu QUIC Initial" diye tanımak için birkaç
bayta bakmak yetmez. Şifreli bir akışın ortasındaki rastgele veri, kısa desenlere
tesadüfen uyar: `16 03 .. .. .. 01` deseni **16 milyonda bir** tutar, ki 100 GB'lık
bir indirmede birkaç kez gerçekleşir. Yanlış tanınan pakete geçerli checksum'lı
sahte paket enjekte edilir ve TTL fazla yüksekse bu sunucuya ulaşıp veriyi bozar.

Bu yüzden üç protokolde de yapı doğrulanır:

| Protokol | Ek doğrulama |
|---|---|
| TLS | Kayıt uzunluğu ile el sıkışma uzunluğu tutarlı olmalı |
| HTTP | İstek satırı gerçekten ` HTTP/1.x` ile bitmeli |
| QUIC | Sürüm alanı (v1/v2) ve bağlantı kimliği uzunluğu geçerli olmalı |

Öz test bunu 20.000 rastgele paketle sınıyor: eski gevşek kontrolü geçen
paketlerin tamamı eleniyor, gerçek olanlar geçmeye devam ediyor.

### Oyun oynuyorsan

etodpx oyun trafiğine dokunmaz, ama iki noktaya dikkat edildi:

- **UDP/443'te QUIC olmayan trafik.** Steam Datagram Relay (CS2 ve diğer Valve
  oyunları) kısıtlı ağlarda oyun trafiğini UDP/443 üzerine düşürebilir. Sadece
  paketin ilk baytına bakmak yetmez — o aralık rastgele bir baytın %6'sına denk
  gelir ve oyun paketi yanlışlıkla QUIC sanılıp araya sahte paket girebilir.
  Bu yüzden QUIC **sürüm alanı** ve bağlantı kimliği uzunluğu da doğrulanır.
- **Süreç önceliği.** `high_priority` varsayılan olarak **kapalı**. Paket iş
  parçacıkları zaten `Highest` önceliğinde ve neredeyse hep bloke bekliyor;
  sürecin tamamını yükseltmek arayüzü de yükseltir ve tam ekran bir oyunla
  işlemci yarışına sokar.

Yine de tedirginsen `quic = off` yap — QUIC yolunu tamamen kapatır, TLS/HTTP
atlatma çalışmaya devam eder.

---

## Ayarlar

Arayüzde ayar yok; hepsi `bin\etodpx.conf` içinde. Değiştirdikten sonra
etodpx'i yeniden başlat.

| Anahtar | Varsayılan | Ne yapar |
|---|---|---|
| `tls` | `disorder` | TLS için kip: `fake+disorder`, `fake+split`, `fake`, `disorder`, `split`, `off` |
| `http` | `disorder` | HTTP için aynı seçenekler |
| `quic` | `fake` | `fake` (hızı korur), `drop` (TCP'ye düşürür, en uyumlu), `off` |
| `split_pos` | `host` | `host`, `midsld` veya sabit bayt sayısı |
| `fake_ttl` | `false` | Sahte TCP paketini düşük TTL ile yolla; önce ayarlayıcıyla ölçülmeli |
| `fake_ttl_value` | `4` | **Hatta göre değişir** — `etodpx-tune.exe` ile bul |
| `fake_badsum` | `false` | Sahteyi bozuk checksum ile yolla |
| `fake_wrongseq` | `false` | Sahteyi pencere dışı sıra numarasıyla yolla |
| `http_host_case` | `false` | HTTP `Host:` başlığını RFC uyumlu `hOSt:` biçimine dönüştürür |
| `skip_private` | `true` | Yerel ağ hedeflerine hiç dokunma |
| `high_priority` | `false` | Süreç önceliğini yükseltir — oyun oynarken kapalı kalsın |
| `threads` | `0` | 0 = otomatik |
| `auto_start` | `true` | Uygulama açılınca korumayı başlat |

### Bir site açılmıyorsa: tahmin etme, ölçtür

DPI kutuları operatöre, şehre, hatta abonelik tipine göre farklı davranır.
Evrensel doğru ayar yok — sadece **senin hattında** çalışan ayar var. Onu bulan
bir araç var:

```bat
bin\etodpx-tune.exe acilmayansite.com
```

Buraya **açılmayan sitenin gerçek adresini** yaz — `discord.com`, `x.com` gibi.

Araç önce engelin cinsini teşhis eder:

| Teşhis | Anlamı |
|---|---|
| `alan adi cozulemiyor` | Yazım hatası ya da DNS engeli — etodpx'in konusu değil |
| `IP seviyesinde engel` | TCP bağlantısı bile kurulmuyor, etodpx aşamaz |
| `zaten aciliyor` | Burada engel yok |
| `SNI engeli` | **etodpx bunu aşabilir** — tarama başlar |

Sadece son durumda 31 stratejiyi tek tek dener. Her biriyle gerçek TLS el
sıkışması yapar (sertifika doğrulaması **açık** — engel sayfası başarı
sayılmaz), sıralamayı gösterir ve kazananı `etodpx.conf` dosyasına yazar.
Çalışan etodpx varsa ölçüm için durdurur; bitince yeniden başlat.

Gerçek bir çıktı (Türk Telekom hattı, hedef discord.com):

```
temel durum (koruma yok)   0/3
badsum / wrongseq / bölme  0/3   <- hiçbiri yetmiyor
ttl 1..3                   0/3   <- sahte paket DPI'a varmadan ölüyor
ttl 4..7                   3/3   <- DPI kutusu 4. hop'ta
```

Bu hatta DPI 4. hop'ta, sunucu 8. hop'ta. `fake_ttl_value = 4` sahte paketi
DPI'a ulaştırıp sunucudan önce öldürüyor. Değer **işe yarayan en düşük** olmalı:
ne kadar düşükse sunucuya ulaşıp bağlantı bozma riski o kadar az.

### Manuel denemek istersen

1. `fake_ttl_value` değerini 3 → 5 → 7 diye artır (8 ve üstü genelde riskli).
2. `quic = drop` yap. Site HTTP/3 üzerinden takılıyorsa bu çözer.
3. `split_pos = midsld` ya da `1` dene.
4. `fake_badsum = true` dene — bazı hatlarda TTL yerine bu tutar.

---

## Dosya düzeni

```
etodpx/
  etodpx.cmd              tek tıkla derle + kur + çalıştır
  build.cmd              yalnızca derler
  etodpx.conf             varsayılan ayarlar (şablon)
  assets/etodpx.ico       uygulama simgesi
  src/
    Program.cs           giriş noktası, yönetici kontrolü, tek örnek kilidi
    Config.cs            ayar dosyası okuyucu
    engine/
      WinDivert.cs       sürücü interop katmanı
      Engine.cs          filtre kurulumu, iş parçacıkları, yaşam döngüsü
      Desync.cs          stratejiler (sıcak yol, tahsissiz)
      Hostname.cs        SNI ve HTTP Host konumlandırma
      Fakes.cs           sahte ClientHello / HTTP / QUIC üretimi
      Packet.cs          IP/TCP/UDP başlık işlemleri
      Stats.cs           sayaçlar
    ui/                  pencere, tema, güç düğmesi, tepsi simgesi
  tools/
    setup-windivert.cmd  sürücüyü indirir
    make_icon.py         uygulama simgesini üretir
    localize.py          kaynakları UTF-8 BOM ile normalize eder
    tune/                otomatik strateji bulucu
    test/                67 kontrollük ağ test paketi
    selftest/            öz test (yönetici gerekmez)
    uipreview/           arayüz önizleme + ekran görüntüsü aracı
  bin/                   çıktı — tamamen üretilir, depoya girmez
```

`bin/` klasörü `.gitignore` içinde. Derlenmiş dosyalar, WinDivert sürücüsü ve
**test günlükleri** oraya yazılır; günlükler hangi siteleri denediğini içerdiği
için depoya konmamalı.

Simgeyi değiştirmek istersen altı hazır stil var:

```bat
python tools\make_icon.py --list
python tools\make_icon.py --preview 256
python tools\make_icon.py --style C
```

---

## Doğrulama

Üç ayrı test paketi var. Hepsini birden çalıştırmak için:

```bat
tools	est	est-all.cmd
```

| Paket | Kontrol | Yönetici | Ne yapar |
|---|---|---|---|
| Öz test | 79 | hayır | Motor mantığı, ayar doğrulama, ayrıştırıcılar, filtre söz dizimi |
| Yayın denetimi | 22 | hayır | Temiz klonu sıfırdan derler, sızıntı arar |
| Ağ paketi | 67 | etodpx açık olmalı | Gerçek bağlantılar, ping, hız, yan hasar |

**Yayın denetimi** GitHub'a atmadan önce en önemlisi: git'in gerçekten takip
edeceği dosyaları boş bir klasöre kopyalar ve orada sıfırdan derler. "Bende
çalışıyordu" hatalarını yakalar — depoya girmemiş bir kaynak, sabit kodlanmış
yol, eksik simge. Ayrıca günlük dosyası, derlenmiş ikili veya kişisel bilgi
sızıp sızmadığına bakar.

```bat
powershell -File tools	esterify-release.ps1
```

### Öz test

Yönetici gerekmez:

```bat
tools\selftest\build-selftest.cmd
```

64 kontrol yapar: WinDivert interop yapısının bayt boyutunu ve bit
konumlarını doğrular (yanlış olsa sürücü belleği sessizce bozardı), filtre söz
dizimini **gerçek WinDivert kütüphanesine** derletir, sahte ClientHello'yu kendi
ayrıştırıcısına okutur, SNI ve Host ayrıştırıcılarını kırpılmış ve bozuk verilere
karşı sınar, IPv4/IPv6 paket yolunu ve bölme aritmetiğini doğrular, SYN/RST ve
akış-ortası verinin es geçildiğini kanıtlar.

Bu sürüm çalışır durumda ölçüldü:

| Ölçüm | etodpx kapalı | etodpx açık |
|---|---|---|
| ping 1.1.1.1 paket kaybı | 0/12 | 0/20 |
| ping ortalama | 9 ms | 8 ms |
| 3 site HTTPS (toplam) | 3611 ms | 3375 ms |
| 16 site TLS el sıkışması | discord.com engelli | **16/16 başarılı** |
| discord.com (0/3 → ) | 0/3 | 3/3 |

16 site testi **tam sertifika doğrulamasıyla** yapıldı: sertifika adı ve zinciri
kontrol edildi, yani engel sayfası ya da araya giren bir sunucu başarı
sayılmadı. Listede Discord'un dört ucu, x.com, Google, YouTube, GitHub,
Instagram, Steam, Reddit ve Outlook vardı; hepsi geçti. Ping ortalaması ve
kayıp oranı değişmedi.

### Kapsamlı test paketi

etodpx **açıkken** çalıştır — 67 kontrol, yönetici gerekmez:

```bat
tools	est	est.cmd
```

Neyi ölçtüğü:

| Bölüm | Kontrol | Ne bakar |
|---|---|---|
| A | 6 | Süreç, sürücü, ayar dosyası, bellek, iş parçacığı |
| B | 10 | Ping kaybı/ortalama/jitter, ağ geçidi, 1400 bayt, MTU, Discord IP |
| C | 20 | Genel sitelerde TLS el sıkışması |
| D | 8 | Discord uçları |
| E | 5 | HTTP port 80, Discord API, güncelleme ucu |
| F | 11 | DNS, localhost, yerel ağ, 443/80 dışı portlar, UDP, IPv6 |
| G | 1 | İndirme hızı |
| H | 6 | 40 ardışık + 10 eşzamanlı el sıkışma, CPU, kararlılık |

Bütün TLS testleri **tam sertifika doğrulamasıyla** yapılır — engel sayfası
başarı sayılmaz. İçlerinde bir de negatif test var: `expired.badssl.com`
bilerek reddedilmeli, edilmiyorsa doğrulama çalışmıyor demektir.

Sonuç `bin\etodpx-test.log` dosyasına da yazılır.

### Arayüzü tasarımla oynarken görmek için (yönetici gerekmez):

```bat
powershell -File tools\uipreview\shoot.ps1 -State on
```

---

## Sorun giderme

**Site hâlâ engel sayfasına gidiyor** — etodpx DNS'e dokunmaz. ISS'inin DNS'i
engelli alan adı için sahte IP döndürüyorsa, etodpx ne yaparsa yapsın engel
sayfasına bağlanırsın (belirtisi: sertifika adı uyuşmazlığı hatası). Kontrol et:

```bat
nslookup discord.com
nslookup discord.com 1.1.1.1
```

İkisi farklı çıkıyorsa DNS'in zehirli. Doğru adaptöre 1.1.1.1 yaz — birden fazla
ağ adaptörü varsa yanlışına yazmış olabilirsin, `Get-DnsClientServerAddress`
ile hangisinin aktif olduğunu gör.

**"WinDivert servisi devre dışı bırakılmış"** — makinede eski bir WinDivert
servisi kayıtlı ve başlatma tipi *Devre Dışı* yapılmış. Genelde daha önce
kurulmuş bir GoodbyeDPI / zapret / ByeDPI kalıntısı ya da antivirüs
müdahalesidir. Yönetici komut isteminde:

```bat
sc stop WinDivert
sc delete WinDivert
```

Sonra etodpx'i yeniden aç — WinDivert servisi kendini sıfırdan kurar.

**"Yönetici izni gerekiyor"** — WinDivert bir çekirdek sürücüsü, yönetici
olmadan yüklenemez. `bin\etodpx.exe` zaten yönetici isteyecek şekilde
imzalanmış manifest taşıyor; UAC penceresini onayla.

**"Sürücü Windows tarafından engellendi"** — Windows Güvenliği → Cihaz
güvenliği → Çekirdek yalıtımı → *Bellek bütünlüğü* açıksa WinDivert
yüklenmeyebilir.

**Antivirüs uyarısı** — WinDivert paket yakalama sürücüsü olduğu için bazı
antivirüsler onu "hacktool" diye etiketler. GoodbyeDPI, zapret ve benzeri
bütün araçlar aynı sürücüyü kullanır.

**Koruma birden durduysa** — motor kendi kendine durursa (sürücü hatası, güvenlik
yazılımı müdahalesi) pencere hatayı gösterir, tepsideyken de baloncuk uyarısı
çıkar. Ayrıca `bin\etodpx-fault.log` dosyasına zaman damgalı olarak yazılır.
Dosya yoksa hiç arıza olmamış demektir — sorunu başka yerde aramak lazım.

**Bir şey bozulduysa** — etodpx'i kapat. Tüm değişiklikler süreç ömrüyle
sınırlı; kapanınca sürücü kaldırılır, ağ yapılandırmasında kalıcı hiçbir
iz bırakmaz. Kayıt defterine, hosts dosyasına, DNS ayarlarına dokunmaz.

---

## Notlar

- WinDivert (LGPLv3 / GPLv2) ayrı bir projedir, etodpx onu yalnızca kullanır.
  `tools\setup-windivert.cmd`, dosyayı `github.com/basil00/WinDivert` resmi
  sürüm sayfasından indirir ve SHA256 özetini ekrana yazar — GitHub'daki
  değerle karşılaştırabilirsin.
- Sürücünün Authenticode imzası geçerli, ancak imzalayan Microsoft değil;
  WinDivert 2.2.2 sürümü üçüncü taraf bir sertifikayla imzalanmış (Chengdu
  Misi Ting Technology). Bu, upstream'in yayımladığı resmi ikilinin kendisidir —
  GoodbyeDPI ve zapret de aynı dosyayı kullanır. Bilerek kurmuş ol.
- etodpx trafiği şifrelemez ve yeniden yönlendirmez. VPN değildir; sadece
  gönderdiğin paketleri DPI'ın okuyamayacağı şekilde parçalar.
