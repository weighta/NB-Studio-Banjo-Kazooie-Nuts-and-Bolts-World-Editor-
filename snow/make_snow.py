"""Snowy Showdown Town textures: python snow/make_snow.py [out_dir]

Reads the town's original textures (snow/tex/top = streamed full resolution, snow/tex/orig = resident mips) and writes
winter versions named after the texture (stem + "top", so NB.Cli tex-batch replaces the resident mip chain and the
streamed top level of the town bundle 234cec only; other worlds keep their textures).

Looks:
  ground   grass, mud, sand, dirt          -> deep snow (the game's own snow1 texture, shaded by the original's relief)
  paving   cobbles, flagstones, jigsaw     -> frosted stone, snow packed into the gaps, drifts by tileable noise
  roofs    tiles, slate, asphalt, LOD roofs -> snow-covered, ridges of tiles showing through
  foliage  trees, hedges, grass sprites    -> hoar frost on the lit side
  wood     benches, planks                 -> light frost
  bunting  party bunting                   -> Christmas red / green / gold
All masks are made in the frequency domain, so they tile exactly like the originals.
"""
import os, re, sys
import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
TOP, MIP = os.path.join(HERE, 'tex', 'top'), os.path.join(HERE, 'tex', 'orig')
OUT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, 'tex', 'out')
os.makedirs(OUT, exist_ok=True)
rng = np.random.default_rng(1225)

def find(stem_rx):
    """All textures (prefer the streamed top level) whose name matches; returns [(stem, path)]."""
    res = {}
    for d, suf in ((MIP, 'mip'), (TOP, 'top')):          # top overwrites mip
        for f in os.listdir(d):
            n = f[:-4]
            if not n.endswith(suf) or not re.search(stem_rx, n): continue
            res[n[:-3]] = os.path.join(d, f)
    return sorted(res.items())

def load(p):
    im = np.asarray(Image.open(p).convert('RGBA')).astype(np.float32) / 255
    return im[..., :3], im[..., 3]

def save(stem, rgb, a):
    im = np.dstack([np.clip(rgb, 0, 1), np.clip(a, 0, 1)])
    Image.fromarray((im * 255 + 0.5).astype(np.uint8), 'RGBA').save(os.path.join(OUT, stem + 'top.png'))

def lum(rgb): return rgb @ np.array([0.299, 0.587, 0.114], np.float32)

def blur(x, sigma):
    """Gaussian blur with wrap-around (tileable), via FFT; sigma in pixels."""
    h, w = x.shape[:2]
    fy = np.fft.fftfreq(h)[:, None]; fx = np.fft.fftfreq(w)[None, :]
    g = np.exp(-2 * (np.pi * sigma) ** 2 * (fx ** 2 + fy ** 2))
    if x.ndim == 2: return np.real(np.fft.ifft2(np.fft.fft2(x) * g))
    return np.dstack([np.real(np.fft.ifft2(np.fft.fft2(x[..., c]) * g)) for c in range(x.shape[2])])

def noise(h, w, scale, octaves=4):
    """Tileable fractal noise in 0..1 (scale = feature size in pixels)."""
    n = np.zeros((h, w), np.float32); amp = 1.0; tot = 0
    for o in range(octaves):
        n += amp * blur(rng.standard_normal((h, w)).astype(np.float32), max(1.0, scale / 2 ** o)) * (scale / 2 ** o)
        tot += amp; amp *= 0.5
    n -= n.min(); n /= max(1e-6, n.max())
    return n

def smooth(e0, e1, x):
    t = np.clip((x - e0) / (e1 - e0), 0, 1)
    return t * t * (3 - 2 * t)

SNOW_SRC, _ = load(find(r'materials_snow_snow1_colour')[0][1])
SNOW_TINT = np.array([0.93, 0.96, 1.0], np.float32)

def snow(h, w):
    """The game's snow texture tiled to h x w (two scales blended, so it never shows a repeat) and brightened."""
    src = Image.fromarray((SNOW_SRC * 255).astype(np.uint8))
    a = np.asarray(src.resize((w, h), Image.LANCZOS)).astype(np.float32) / 255
    b = np.asarray(src.resize((w // 2, h // 2), Image.LANCZOS)).astype(np.float32) / 255
    b = np.tile(b, (2, 2, 1))[:h, :w]
    s = 0.55 * a + 0.45 * b
    s = (s - s.mean((0, 1))) * 0.8 + np.array([0.80, 0.84, 0.90], np.float32)   # soft, slightly blue; not paper-white (midday sun would clip it)
    return s

def cool(rgb, desat=0.45, lift=0.06):
    g = lum(rgb)[..., None]
    c = rgb * (1 - desat) + g * desat
    return (c * np.array([0.92, 0.97, 1.06], np.float32) + lift)

def relief(rgb, sigma):
    L = lum(rgb)
    return L - blur(L, sigma)          # + = raised / lit, - = gaps

def ground(rgb, a, keep=0.08):
    h, w = L_shape = rgb.shape[:2]
    s = snow(h, w)
    r = relief(rgb, w / 48)
    low = blur(lum(rgb), w / 10); low = (low - low.mean()) / (low.std() + 1e-6)
    shade = 1 + 0.55 * r[..., None] + 0.035 * low[..., None]
    out = s * shade
    # a hint of what is underneath where the snow is thin (trodden), by tileable noise
    thin = smooth(0.62, 0.9, noise(h, w, w / 6))[..., None] * keep
    return out * (1 - thin) + cool(rgb, 0.3, 0.12) * thin, a

def paving(rgb, a, amount, gaps=1.6, drift=0.45):
    h, w = rgb.shape[:2]
    s = snow(h, w)
    r = relief(rgb, w / 96)
    crev = smooth(0.0, 0.12, -r)                         # dark gaps between stones
    n = noise(h, w, w / 5)
    m = np.clip(amount + gaps * crev * 0.5 + drift * (n - 0.5), 0, 1)
    m = smooth(0.15, 0.85, m)[..., None]
    stone = cool(rgb, 0.35, 0.05) * (1 + 0.3 * r[..., None])
    return stone * (1 - m) + s * (1 + 0.4 * r[..., None]) * m, a

def roof(rgb, a, amount=0.7):
    h, w = rgb.shape[:2]
    s = snow(h, w)
    r = relief(rgb, max(2, w / 64))
    n = noise(h, w, w / 4)
    m = np.clip(amount + 0.5 * (n - 0.5) + 0.8 * r, 0, 1)   # snow sits on the raised tile bodies, edges show
    m = smooth(0.25, 0.75, m)[..., None]
    return cool(rgb, 0.25, 0.0) * 0.85 * (1 - m) + s * (0.95 + 0.5 * r[..., None]) * m, a

def foliage(rgb, a, amount=0.5):
    L = lum(rgb)[..., None]
    frost = np.array([0.88, 0.93, 1.0], np.float32)
    m = np.clip(amount * (0.35 + 0.9 * L), 0, 0.85)
    return cool(rgb, 0.35, 0.02) * (1 - m) + frost * m, a

def holly(rgb, a):
    """Wall ivy -> holly garlands: deep glossy green leaves, clusters of red berries, a touch of frost."""
    h, w = rgb.shape[:2]
    L = lum(rgb)[..., None]
    leaf = np.array([0.05, 0.30, 0.10], np.float32) * (0.5 + 1.1 * L) + np.array([0.0, 0.05, 0.02], np.float32)
    out = leaf.copy()
    yy, xx = np.mgrid[0:h, 0:w]
    r = max(2.0, w / 160)
    for _ in range(int(w * h / 900)):
        cy, cx = rng.integers(0, h), rng.integers(0, w)
        if a[cy, cx] < 0.7: continue
        for dy, dx in ((0, 0), (r * 1.6, r * 0.9), (r * 0.3, -r * 1.7)):
            dyy = (yy - cy - dy + h / 2) % h - h / 2; dxx = (xx - cx - dx + w / 2) % w - w / 2   # wrap: stays tileable
            d = np.sqrt(dyy ** 2 + dxx ** 2) / r
            m = np.clip(1.2 - d, 0, 1)[..., None]
            berry = np.array([0.78, 0.04, 0.06], np.float32) * (1.0 + 0.6 * np.clip(0.6 - np.hypot(dyy + r * 0.35, dxx + r * 0.35) / r, 0, 1)[..., None])
            out = out * (1 - m) + berry * m
    frost = smooth(0.55, 0.95, L[..., 0])[..., None] * 0.35
    return out * (1 - frost) + np.array([0.86, 0.92, 1.0], np.float32) * frost, a

def wood(rgb, a, amount=0.16):
    h, w = rgb.shape[:2]
    r = relief(rgb, w / 128)
    m = np.clip(amount + 0.25 * smooth(0.0, 0.1, r) + 0.12 * (noise(h, w, w / 3) - 0.5), 0, 0.6)[..., None]   # frost on the grain ridges
    return cool(rgb, 0.25, 0.01) * (1 - m) + snow(h, w) * m, a

def christmas(rgb, a):
    """Party bunting -> Christmas: yellow -> berry red, blue -> holly green, pink/red/purple -> gold."""
    im = Image.fromarray((np.clip(rgb, 0, 1) * 255).astype(np.uint8)).convert('HSV')
    hsv = np.asarray(im).astype(np.float32)
    hue, sat, val = hsv[..., 0] * 360 / 255, hsv[..., 1] / 255, hsv[..., 2] / 255
    out = rgb.copy()
    gold, green, red = np.array([1.0, 0.78, 0.22]), np.array([0.05, 0.50, 0.20]), np.array([0.80, 0.06, 0.10])
    col = sat > 0.25
    for lo, hi, c in ((35, 75, red), (160, 270, green), (270, 361, gold), (0, 35, gold)):
        m = col & (hue >= lo) & (hue < hi)
        out[m] = c * (0.55 + 0.6 * val[m, None])
    return out, a

JOBS = [
    # ground: deep snow
    (r'materials_floors_(mud1|sand1|slippy1)_colour', lambda c, a: ground(c, a)),
    (r'spiralmountain_materials_(dirt_color|foliage_grasssurface_color)', lambda c, a: ground(c, a)),
    # paving: frosted, snow in the gaps
    (r'materials_stone_(path1|path2)_colour', lambda c, a: paving(c, a, 0.10, gaps=1.4, drift=0.55)),
    (r'materials_stone_flagstones1_colour', lambda c, a: paving(c, a, 0.25)),
    (r'materials_stone_jiggypattern1_colour', lambda c, a: paving(c, a, 0.22, gaps=1.2, drift=0.35)),
    (r'materials_stone_(stone1|slate1|brokenrock1)_colour', lambda c, a: paving(c, a, 0.30)),
    (r'materials_metal_pavementedge1_colour', lambda c, a: paving(c, a, 0.25)),
    # roofs
    (r'materials_roof_(colour2|colour3|roofingasphalt_colour|slate_colour)', lambda c, a: roof(c, a)),
    (r'materials_roof_lod_.*_colour', lambda c, a: roof(c, a, 0.75)),
    (r'billboards_roofslope', lambda c, a: roof(c, a, 0.75)),
    # foliage
    (r'grassfoliage_(bgtree_0|bgtreee2_0|sdtunifiedgrass)', lambda c, a: foliage(c, a)),
    (r'showdowntown_trees_colour|trees_leaves_pottedtreegreen_colour|spiralmountain_materials_trees_leaf1_color', lambda c, a: foliage(c, a, 0.45)),
    (r'decals_ivy1_colourandtrans', holly),
    (r'signs_theatrebackdrop_colour', lambda c, a: foliage(c, a, 0.85)),   # the theatre's painted backdrop: snowy trees
    # wood
    (r'materials_wood_(plain1|planks_plainplank3|planks_multipleplank8)_colour|materials_wood_plainwood1_colour', lambda c, a: wood(c, a)),
    # Christmas
    (r'materials_misc_bunting1_colour|billboards_sign[34]_', christmas),
]

if __name__ == '__main__':
    done = set()
    for rx, fn in JOBS:
        for stem, p in find(rx):
            if stem in done: continue
            rgb, a = load(p)
            out, oa = fn(rgb, a)
            save(stem, out, oa); done.add(stem)
            print(f'{os.path.basename(p)[:-4]:90s} {rgb.shape[1]}x{rgb.shape[0]}')
    print(len(done), 'textures ->', OUT)
