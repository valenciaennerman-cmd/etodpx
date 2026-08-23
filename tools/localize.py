# etodpx - kaynaktaki kullanici metinlerini duzgun Turkceye cevirir
# ve butun .cs dosyalarini UTF-8 BOM ile kaydeder (csc.exe BOM'u algilar).
import io
import os
import sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'src')

PHRASES = [
    # --- MainWindow ---
    ('"Aydinlik / karanlik"',            u'"Aydınlık / karanlık"'),
    ('"Simge durumuna kucult"',          u'"Simge durumuna küçült"'),
    ('"Korumayi baslatmak icin dokun"',  u'"Korumayı başlatmak için dokun"'),
    ('"BASLATILIYOR"',                   u'"BAŞLATILIYOR"'),
    ('"Surucu hazirlaniyor"',            u'"Sürücü hazırlanıyor"'),
    ('"Baglantilar birakiliyor"',        u'"Bağlantılar bırakılıyor"'),
    ('"Koruma baslatilamadi"',           u'"Koruma başlatılamadı"'),
    ('"KORUMA ACIK"',                    u'"KORUMA AÇIK"'),
    ('"DPI atlatma calisiyor"',          u'"DPI atlatma çalışıyor"'),
    ('"BAGLANTI"',                       u'"BAĞLANTI"'),
    ('"SURE"',                           u'"SÜRE"'),
    ('"QUIC: TCP\'ye dusur"',            u'"QUIC: TCP\'ye düşür"'),
    ('"QUIC: dokunma"',                  u'"QUIC: dokunma"'),
    ('"QUIC: sahte"',                    u'"QUIC: sahte"'),

    # --- Engine hata mesajlari ---
    ('"TCP filtresi derlenemedi. WinDivert surumu cok eski olabilir (2.2+ gerekir)."',
     u'"TCP filtresi derlenemedi. WinDivert sürümü çok eski olabilir (2.2+ gerekir)."'),
    ('"WinDivert.dll bulunamadi.\\n\\nbin klasorune WinDivert.dll, WinDivert64.sys " +\n                    "dosyalarini koyun (kurulum icin tools\\\\setup-windivert.cmd)."',
     u'"WinDivert.dll bulunamadı.\\n\\nbin klasörüne WinDivert.dll ve WinDivert64.sys " +\n                    "dosyalarını koyun (kurulum: tools\\\\setup-windivert.cmd)."'),
    ('"WinDivert.dll mimarisi uyumsuz. 64-bit surumu gerekli."',
     u'"WinDivert.dll mimarisi uyumsuz. 64 bit sürüm gerekli."'),
    ('"WinDivert surucu dosyasi (WinDivert64.sys) bulunamadi.\\n" +\n                           "DLL ile .sys dosyasi ayni klasorde olmali."',
     u'"WinDivert sürücü dosyası (WinDivert64.sys) bulunamadı.\\n" +\n                           "DLL ile .sys dosyası aynı klasörde olmalı."'),
    ('"Yonetici izni gerekiyor. etodpx yonetici olarak calistirilmali."',
     u'"Yönetici izni gerekiyor. etodpx yönetici olarak çalıştırılmalı."'),
    ('"Filtre ifadesi surucu tarafindan reddedildi (WinDivert 2.2+ gerekir)."',
     u'"Filtre ifadesi sürücü tarafından reddedildi (WinDivert 2.2+ gerekir)."'),
    ('"WinDivert surucusunun imzasi Windows tarafindan kabul edilmedi.\\n" +\n                           "Resmi WinDivert 2.2.2 paketini kullanin."',
     u'"WinDivert sürücüsünün imzası Windows tarafından kabul edilmedi.\\n" +\n                           "Resmi WinDivert 2.2.2 paketini kullanın."'),
    ('"WinDivert servisi kurulamadi. Bir guvenlik yazilimi engelliyor olabilir."',
     u'"WinDivert servisi kurulamadı. Bir güvenlik yazılımı engelliyor olabilir."'),
    ('"Surucu Windows tarafindan engellendi (Memory Integrity / Cekirdek Yalitimi acik olabilir)."',
     u'"Sürücü Windows tarafından engellendi (Bellek Bütünlüğü / Çekirdek Yalıtımı açık olabilir)."'),
    ('"WinDivert servisi devre disi birakilmis."',
     u'"WinDivert servisi devre dışı bırakılmış."'),
    ('"WinDivert acilamadi (hata kodu "',
     u'"WinDivert açılamadı (hata kodu "'),
    ('"Paket alimi surekli basarisiz (hata "',
     u'"Paket alımı sürekli başarısız (hata "'),
    ('"). Koruma durduruldu."',
     u'"). Koruma durduruldu."'),
    ('"Motor hatasi: "',
     u'"Motor hatası: "'),

    # --- Config ---
    ('"Ayar dosyasi okunamadi: "', u'"Ayar dosyası okunamadı: "'),
]


def walk():
    for base, _dirs, files in os.walk(ROOT):
        for f in files:
            if f.endswith('.cs'):
                yield os.path.join(base, f)


def main():
    hits = 0
    for path in walk():
        raw = io.open(path, 'rb').read()
        if raw.startswith(b'\xef\xbb\xbf'):
            raw = raw[3:]
        text = raw.decode('utf-8')

        for a, b in PHRASES:
            if a in text:
                text = text.replace(a, b)
                hits += 1

        with io.open(path, 'wb') as fh:
            fh.write(b'\xef\xbb\xbf')
            fh.write(text.encode('utf-8'))

    print('degistirilen ifade: %d' % hits)
    missing = []
    joined = ''
    for path in walk():
        joined += io.open(path, encoding='utf-8-sig').read()
    for a, b in PHRASES:
        if b not in joined:
            missing.append(a[:50])
    if missing:
        print('UYARI - eslesmeyen:', file=sys.stderr)
        for m in missing:
            print('  ' + m, file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
