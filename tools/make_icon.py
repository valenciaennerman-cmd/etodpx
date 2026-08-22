# dpetoos - uygulama simgesi ureteci
#
# Bagimliligi yoktur: PNG ve ICO kodlamasi elle yapilir, cizim 4x supersampling
# ile yumusatilir. Boylece Windows'ta ek bir sey kurmadan calisir.
#
# Kullanim:
#   python make_icon.py                 -> secili stille assets/dpetoos.ico
#   python make_icon.py --style B       -> B stiliyle uret
#   python make_icon.py --preview 256   -> her stil icin tek tek PNG onizleme
#   python make_icon.py --all           -> assets/dpetoos-A.ico ... dpetoos-F.ico
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

# --- renkler -----------------------------------------------------------------
MINT = (0x2B, 0xE0, 0xA8)
TEAL = (0x10, 0xB4, 0xD8)
WHITE = (0xFF, 0xFF, 0xFF)
DARK = (0x12, 0x18, 0x22)
DARK2 = (0x0A, 0x0E, 0x15)

STYLES = {
    'A': 'gradyan kare + beyaz guc simgesi',
    'B': 'koyu kare + nane guc simgesi',
    'C': 'koyu kare + nane halka ve guc simgesi (uygulamadaki dugme)',
    'D': 'gradyan kare + bolunmus paket cizgileri',
    'E': 'koyu kare + nane bolunmus paket cizgileri',
    'F': 'gradyan daire + beyaz guc simgesi',
}

# --- plaka geometrisi --------------------------------------------------------
PLATE_HALF = 0.470
PLATE_R = 0.135
CIRCLE_R = 0.470

# --- guc simgesi -------------------------------------------------------------
RING_R = 0.212
STROKE = 0.078
GAP_DEG = 33.0
BAR_TOP = -0.305
BAR_BOT = -0.060

# --- "bolunmus paket" motifi -------------------------------------------------
BAR_H = 0.062
BAR_GAP = 0.105
BAR_LEN = 0.30
SPLIT_SHIFT = 0.085

# --- halka (C stili) ---------------------------------------------------------
OUTER_RING_R = 0.335
OUTER_RING_W = 0.040


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


def power_hit(px, py):
    """Standart guc simgesi: ustte bosluklu halka + dikey cizgi."""
    half = STROKE * 0.5
    if seg_dist(px, py, 0.0, BAR_TOP, 0.0, BAR_BOT) <= half:
        return True

    rr = math.sqrt(px * px + py * py)
    if abs(rr - RING_R) <= half:
        ang = math.degrees(math.atan2(py, px))
        delta = abs(ang - (-90.0))
        if delta > 180.0:
            delta = 360.0 - delta
        if delta > GAP_DEG:
            return True

    for sign in (-1.0, 1.0):
        a = math.radians(-90.0 + sign * GAP_DEG)
        cx, cy = RING_R * math.cos(a), RING_R * math.sin(a)
        if (px - cx) ** 2 + (py - cy) ** 2 <= half * half:
            return True
    return False


def split_hit(px, py):
    """Bolunmus paket: uc yatay cubuk, ortadaki kaydirilmis."""
    half = BAR_H * 0.5
    rows = (-BAR_GAP, 0.0, BAR_GAP)
    for i, cy in enumerate(rows):
        shift = SPLIT_SHIFT if i == 1 else 0.0
        x0 = -BAR_LEN * 0.5 + shift
        x1 = BAR_LEN * 0.5 + shift
        if seg_dist(px, py, x0, cy, x1, cy) <= half:
            return True
    return False


def ring_hit(px, py):
    rr = math.sqrt(px * px + py * py)
    return abs(rr - OUTER_RING_R) <= OUTER_RING_W * 0.5


def lerp_color(a, b, t):
    t = 0.0 if t < 0.0 else (1.0 if t > 1.0 else t)
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


def diagonal_t(px, py, half):
    return (px + py + 2 * half) / (4 * half)


def make_style(style):
    """(plate_sdf, plate_color, glyph_hit, glyph_color) dondurur."""
    style = style.upper()

    if style == 'F':
        plate = lambda px, py: math.sqrt(px * px + py * py) - CIRCLE_R
        half = CIRCLE_R
    else:
        plate = lambda px, py: rrect_sdf(px, py, PLATE_HALF, PLATE_R)
        half = PLATE_HALF

    gradient = lambda px, py: lerp_color(MINT, TEAL, diagonal_t(px, py, half))
    dark_plate = lambda px, py: lerp_color(DARK, DARK2, diagonal_t(px, py, half))

    if style == 'A':
        return plate, gradient, power_hit, lambda px, py: WHITE
    if style == 'B':
        return plate, dark_plate, power_hit, lambda px, py: MINT
    if style == 'C':
        both = lambda px, py: power_hit(px, py) or ring_hit(px, py)
        return plate, dark_plate, both, lambda px, py: MINT
    if style == 'D':
        return plate, gradient, split_hit, lambda px, py: WHITE
    if style == 'E':
        return plate, dark_plate, split_hit, lambda px, py: MINT
    if style == 'F':
        return plate, gradient, power_hit, lambda px, py: WHITE

    raise SystemExit('bilinmeyen stil: %s  (secenekler: %s)'
                     % (style, ', '.join(sorted(STYLES))))


def render(size, style):
    """RGBA bayt dizisi (ust satirdan alta)."""
    plate_sdf, plate_col, glyph, glyph_col = make_style(style)

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
            c = glyph_col(px, py) if glyph(px, py) else plate_col(px, py)
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
        print('butun stiller uretiliyor (Explorer icinde "Buyuk simgeler" ile bak):')
        for k in sorted(STYLES):
            dest = os.path.abspath(os.path.join(assets, 'dpetoos-%s.ico' % k))
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
    dest = os.path.abspath(os.path.join(assets, 'dpetoos.ico'))
    print('simge uretiliyor (stil %s: %s)' % (style, STYLES[style]))
    n = build_ico(dest, style)
    print('yazildi: %s (%d bayt)' % (dest, n))
    return 0


if __name__ == '__main__':
    sys.exit(main())
