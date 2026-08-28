# etodpx - uygulama simgesi ureteci
#
# Bagimliligi yoktur: PNG ve ICO kodlamasi elle yapilir, cizim 4x supersampling
# ile yumusatilir. Boylece Windows'ta ek bir sey kurmadan calisir.
#
# Kullanim:
#   python make_icon.py                 -> secili stille assets/etodpx.ico
#   python make_icon.py --style B       -> B stiliyle uret
#   python make_icon.py --preview 256   -> her stil icin PNG onizleme
#   python make_icon.py --all           -> assets/etodpx-A.ico ... etodpx-F.ico
#   python make_icon.py --list          -> stilleri listele
import math
import os
import struct
import sys
import zlib

ICO_SIZES = [16, 24, 32, 48, 64, 128, 256]
PNG_FROM = 128          # bu boyuttan itibaren ICO icine PNG gomulur
SS = 4                  # supersampling katsayisi

DEFAULT_STYLE = 'A'

# Arayuzun paletiyle ayni (src/ui/Theme.cs). Duz renk: gradyan yok.
MINT = (0x2B, 0xE0, 0xA8)
DARK = (0x0B, 0x0F, 0x16)

STYLES = {
    'A': 'kaydirilmis iki cizgi - nane / koyu zemin',
    'B': 'kaydirilmis iki cizgi - koyu / nane zemin',
    'C': 'kesilip kaydirilmis kare',
    'D': 'e harfi - nane / koyu zemin',
    'E': 'duz guc simgesi - nane / koyu zemin',
    'F': 'e harfi - koyu / nane zemin',
}

PLATE_HALF = 0.470
PLATE_R = 0.135

# --- kaydirilmis cizgiler (A, B) ---------------------------------------------
LINE_W = 0.100          # cizgi kalinligi
LINE_LEN = 0.400        # uc uca uzunluk
LINE_DY = 0.115         # merkeze gore dikey ofset
LINE_DX = 0.080         # iki cizgi arasindaki yatay kayma

# --- kesilmis kare (C) -------------------------------------------------------
CUT_HALF = 0.400
CUT_R = 0.115
CUT_SHIFT = 0.072       # yarilarin dikey kaymasi
CUT_GAP = 0.048         # aradaki bosluk (yarim genislik)

# --- e harfi (D) -------------------------------------------------------------
E_R = 0.255
E_W = 0.090
E_GAP = (0.0, 62.0)      # sag altta acik kalan yay araligi

# --- guc simgesi (E) ---------------------------------------------------------
RING_R = 0.235
STROKE = 0.090
GAP_DEG = 34.0
BAR_TOP = -0.330
BAR_BOT = -0.070

def rrect_sdf(px, py, half, r):
    qx = abs(px) - (half - r)
    qy = abs(py) - (half - r)
    ax = qx if qx > 0 else 0.0
    ay = qy if qy > 0 else 0.0
    return math.sqrt(ax * ax + ay * ay) + min(max(qx, qy), 0.0) - r


def seg_dist(px, py, ax, ay, bx, by):
    vx, vy = bx - ax, by - ay
    wx, wy = px - ax, py - ay
    l2 = vx * vx + vy * vy
    t = 0.0 if l2 == 0.0 else max(0.0, min(1.0, (wx * vx + wy * vy) / l2))
    dx = wx - t * vx
    dy = wy - t * vy
    return math.sqrt(dx * dx + dy * dy)


def capsule(px, py, ax, ay, bx, by, w):
    return seg_dist(px, py, ax, ay, bx, by) <= w * 0.5


def offset_lines(px, py):
    """Iki cizgi: ustteki sola, alttaki saga kaymis. Bolunmus akis."""
    half = LINE_LEN * 0.5
    return (capsule(px, py, -half - LINE_DX, -LINE_DY, half - LINE_DX, -LINE_DY, LINE_W)
            or capsule(px, py, -half + LINE_DX, LINE_DY, half + LINE_DX, LINE_DY, LINE_W))


def letter_e(px, py):
    """Geometrik kucuk e: sag altta acik halka + yatay orta cizgi."""
    if abs(math.hypot(px, py) - E_R) <= E_W * 0.5:
        ang = math.degrees(math.atan2(py, px)) % 360.0
        if not (E_GAP[0] <= ang <= E_GAP[1]):
            return True
    return capsule(px, py, -E_R, 0.0, E_R, 0.0, E_W)


def power_glyph(px, py):
    """Standart guc simgesi: ustte bosluklu halka + dikey cizgi."""
    if capsule(px, py, 0.0, BAR_TOP, 0.0, BAR_BOT, STROKE):
        return True

    if abs(math.hypot(px, py) - RING_R) <= STROKE * 0.5:
        delta = abs((math.degrees(math.atan2(py, px)) + 90.0 + 180.0) % 360.0 - 180.0)
        if delta > GAP_DEG:
            return True

    for sign in (-1.0, 1.0):     # yayin uclarini yuvarlat
        a = math.radians(-90.0 + sign * GAP_DEG)
        cx, cy = RING_R * math.cos(a), RING_R * math.sin(a)
        if (px - cx) ** 2 + (py - cy) ** 2 <= (STROKE * 0.5) ** 2:
            return True
    return False


def cut_plate(px, py):
    """Dikey kesilip iki yarisi ters yonde kaydirilmis yuvarlak kare."""
    if px < 0.0:
        d = rrect_sdf(px + CUT_GAP, py - CUT_SHIFT, CUT_HALF, CUT_R)
        return max(d, px + CUT_GAP)
    d = rrect_sdf(px - CUT_GAP, py + CUT_SHIFT, CUT_HALF, CUT_R)
    return max(d, CUT_GAP - px)


def plain_plate(px, py):
    return rrect_sdf(px, py, PLATE_HALF, PLATE_R)


def no_glyph(px, py):
    return False


# stil -> (plaka sdf, plaka rengi, glif, glif rengi)
LAYOUT = {
    'A': (plain_plate, DARK, offset_lines, MINT),
    'B': (plain_plate, MINT, offset_lines, DARK),
    'C': (cut_plate,   MINT, no_glyph,     DARK),
    'D': (plain_plate, DARK, letter_e,     MINT),
    'E': (plain_plate, DARK, power_glyph,  MINT),
    'F': (plain_plate, MINT, letter_e,     DARK),
}


def render(size, style):
    """RGBA bayt dizisi (ust satirdan alta)."""
    style = style.upper()
    if style not in LAYOUT:
        raise SystemExit('bilinmeyen stil: %s  (secenekler: %s)'
                         % (style, ', '.join(sorted(STYLES))))
    plate_sdf, plate_col, glyph, glyph_col = LAYOUT[style]

    n = size * SS
    inv = 1.0 / n
    center = 0.5 * inv
    acc = [[0, 0, 0, 0] for _ in range(size * size)]

    for sy in range(n):
        py = (sy * inv) - 0.5 + center
        row = (sy // SS) * size
        for sx in range(n):
            px = (sx * inv) - 0.5 + center
            if plate_sdf(px, py) > 0.0:
                continue
            c = glyph_col if glyph(px, py) else plate_col
            cell = acc[row + (sx // SS)]
            cell[0] += c[0]
            cell[1] += c[1]
            cell[2] += c[2]
            cell[3] += 255

    out = bytearray(size * size * 4)
    total = SS * SS
    for i, cell in enumerate(acc):
        a = cell[3] // total
        if a == 0:
            continue
        cover = cell[3] / 255.0
        out[i * 4 + 0] = int(round(cell[0] / cover))
        out[i * 4 + 1] = int(round(cell[1] / cover))
        out[i * 4 + 2] = int(round(cell[2] / cover))
        out[i * 4 + 3] = a
    return bytes(out)


# --- PNG ---------------------------------------------------------------------
def png_chunk(tag, data):
    return (struct.pack('>I', len(data)) + tag + data
            + struct.pack('>I', zlib.crc32(tag + data) & 0xFFFFFFFF))


def encode_png(rgba, size):
    raw = bytearray()
    stride = size * 4
    for y in range(size):
        raw.append(0)
        raw += rgba[y * stride:(y + 1) * stride]
    ihdr = struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0)
    return (b'\x89PNG\r\n\x1a\n'
            + png_chunk(b'IHDR', ihdr)
            + png_chunk(b'IDAT', zlib.compress(bytes(raw), 9))
            + png_chunk(b'IEND', b''))


# --- ICO icindeki BMP --------------------------------------------------------
def encode_bmp(rgba, size):
    header = struct.pack('<IiiHHIIiiII', 40, size, size * 2, 1, 32, 0,
                         size * size * 4, 0, 0, 0, 0)
    body = bytearray()
    for y in range(size - 1, -1, -1):
        row = rgba[y * size * 4:(y + 1) * size * 4]
        for x in range(size):
            r, g, b, a = row[x * 4:x * 4 + 4]
            body += bytes((b, g, r, a))
    mask_stride = ((size + 31) // 32) * 4
    body += bytes(mask_stride * size)
    return header + bytes(body)


def build_ico(path, style):
    images = []
    for s in ICO_SIZES:
        rgba = render(s, style)
        blob = encode_png(rgba, s) if s >= PNG_FROM else encode_bmp(rgba, s)
        images.append((s, blob))
        sys.stdout.write('  %d' % s)
        sys.stdout.flush()
    print('')

    out = bytearray(struct.pack('<HHH', 0, 1, len(images)))
    offset = 6 + 16 * len(images)
    for s, blob in images:
        dim = 0 if s >= 256 else s
        out += struct.pack('<BBBBHHII', dim, dim, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
    for _s, blob in images:
        out += blob

    with open(path, 'wb') as fh:
        fh.write(bytes(out))
    return len(out)


def main():
    root = os.path.dirname(os.path.abspath(__file__))
    assets = os.path.join(root, '..', 'assets')

    args = sys.argv[1:]
    style = DEFAULT_STYLE
    preview = 0
    make_all = False

    i = 0
    while i < len(args):
        if args[i] == '--list':
            for k in sorted(STYLES):
                print('  %s : %s' % (k, STYLES[k]))
            return 0
        if args[i] == '--style' and i + 1 < len(args):
            style = args[i + 1].upper(); i += 2; continue
        if args[i] == '--all':
            make_all = True; i += 1; continue
        if args[i] == '--preview':
            preview = int(args[i + 1]) if i + 1 < len(args) else 256
            i += 2; continue
        i += 1

    if make_all:
        os.makedirs(assets, exist_ok=True)
        print('butun stiller uretiliyor (Explorer icinde Buyuk simgeler ile bak):')
        for k in sorted(STYLES):
            dest = os.path.abspath(os.path.join(assets, 'etodpx-%s.ico' % k))
            print('  %s : %s' % (k, STYLES[k]))
            sys.stdout.write('     ')
            build_ico(dest, k)
        print('')
        print('Begendigini secmek icin:  python make_icon.py --style <harf>')
        return 0

    if preview:
        os.makedirs(assets, exist_ok=True)
        for k in sorted(STYLES):
            rgba = render(preview, k)
            p = os.path.join(assets, 'preview-%s.png' % k)
            with open(p, 'wb') as fh:
                fh.write(encode_png(rgba, preview))
            print('  %s -> %s' % (k, os.path.basename(p)))
        return 0

    if style not in STYLES:
        print('bilinmeyen stil: %s' % style, file=sys.stderr)
        return 1

    os.makedirs(assets, exist_ok=True)
    dest = os.path.abspath(os.path.join(assets, 'etodpx.ico'))
    print('simge uretiliyor (stil %s: %s)' % (style, STYLES[style]))
    n = build_ico(dest, style)
    print('yazildi: %s (%d bayt)' % (dest, n))
    return 0


if __name__ == '__main__':
    sys.exit(main())
