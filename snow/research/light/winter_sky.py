"""Procedural winter skies for the Showdown Town skydomes (overcast snowy day, cold winter dusk, snowy night).

python winter_sky.py [out_dir]   -> sky_day.png (4096x1024), sky_dusk.png (4096x1028), sky_night.png (1024x1024),
                                    sky_stars.png (1024x1024) + a preview sheet

Panorama mapping of the day/dusk domes (measured for the Seattle skies, seattle/BUGLOG.md F3): texture u = 0.357 +
azimuth/360 (azimuth = atan2(x, z) in game space); row t -> elevation from the table below (horizon at t ~ 0.91).
The horizon colours are chosen to match the winter fog colours in REPORT.md, so distant buildings fade into the sky.
The night texture is a plain vertical gradient on its dome (like the original), the stars a separate layer.
Everything is generated: no original sky pixels are used."""
import math, os, sys
import numpy as np
from PIL import Image

_V = [0.033, 0.094, 0.172, 0.253, 0.341, 0.43, 0.52, 0.61, 0.715, 0.832, 0.993]
_E = [-6, 0, 6, 11, 17, 24, 31, 40, 50, 62, 75]

def elevation_of_t(t):
    v = 1.0 - np.asarray(t, np.float64)
    e = np.interp(v, _V, _E)
    e = np.where(v < _V[0], -6 - (_V[0] - v) * 100, e)
    return np.where(v > _V[-1], 75 + (v - _V[-1]) * 1500, e)

def azimuth_of_u(u): return (u - 0.357) * 360.0

def _noise(w, h, cx, cy, rng):
    """Value noise, periodic in x (the dome wraps around)."""
    g = rng.random((cy + 1, cx))
    xs = np.linspace(0, cx, w, endpoint=False); ys = np.linspace(0, cy, h)
    x0 = np.floor(xs).astype(int); fx = xs - x0; x1 = (x0 + 1) % cx
    y0 = np.minimum(np.floor(ys).astype(int), cy - 1); fy = ys - y0; y1 = y0 + 1
    sx = fx * fx * (3 - 2 * fx); sy = (fy * fy * (3 - 2 * fy))[:, None]
    a = g[y0][:, x0] * (1 - sx) + g[y0][:, x1] * sx
    b = g[y1][:, x0] * (1 - sx) + g[y1][:, x1] * sx
    return a * (1 - sy) + b * sy

def fbm(w, h, seed, base=8, octaves=5, aspect=4):
    rng = np.random.default_rng(seed); out = np.zeros((h, w)); amp = 1.0; tot = 0.0
    for o in range(octaves):
        c = base * 2 ** o
        out += amp * _noise(w, h, c, max(2, c // aspect), rng); tot += amp; amp *= 0.55
    return out / tot

# colours 0..255. 'horizon' ~ the phase's winter fog colour (REPORT.md table).
PRESETS = {
    # overcast snowy day (used by the morning AND midday phases: the midday script is pointed at the morning dome)
    'day': dict(size=(4096, 1024), zenith=(150, 160, 178), horizon=(206, 216, 230), below=(196, 204, 216),
                cloud=(232, 236, 242), shade=(158, 166, 182), sun=(0.520, 0.719), sun_col=(255, 250, 240), glow=0.30, seed=21),
    # cold winter dusk: grey-violet overcast, peach glow low in the sun's direction (afternoon phase = evening dome)
    'dusk': dict(size=(4096, 1028), zenith=(104, 112, 138), horizon=(209, 198, 200), below=(172, 166, 174),
                 cloud=(218, 208, 210), shade=(120, 120, 146), sun=(0.406, 0.578), sun_col=(255, 214, 176), glow=0.45, seed=23),
}

def render_pano(P):
    w, h = P['size']
    t = (np.arange(h) + 0.5) / h; el = elevation_of_t(t)[:, None] * np.ones((1, w))
    u = (np.arange(w) + 0.5) / w; az = azimuth_of_u(u)[None, :] * np.ones((h, 1))
    Z, Hc, B = (np.array(P[k], np.float32) for k in ('zenith', 'horizon', 'below'))
    k = np.clip(el / 55.0, 0, 1) ** 0.7
    img = Hc * (1 - k[..., None]) + Z * k[..., None]
    # thick, low-contrast cloud deck: large soft billows + finer streaks, denser toward the horizon
    n = fbm(w, h, P['seed'], base=6, octaves=6)
    streak = fbm(w, h, P['seed'] + 5, base=24, octaves=4, aspect=12)
    cov = np.clip((n - 0.30) / 0.35, 0, 1) * 0.85 + 0.15
    cov = np.clip(cov + 0.25 * (1 - np.clip(el / 25.0, 0, 1)), 0, 1)
    shade = np.clip((n - np.roll(n, -6, axis=0)) * 9 + 0.55 + 0.25 * (streak - 0.5), 0, 1)
    CC, CS = np.array(P['cloud'], np.float32), np.array(P['shade'], np.float32)
    ccol = CS * (1 - shade[..., None]) + CC * shade[..., None]
    ccol = ccol * (1 - k[..., None] * 0.15)                          # a little darker overhead
    img = img * (1 - 0.8 * cov[..., None]) + ccol * 0.8 * cov[..., None]
    # sun: no disc, only a diffuse bright patch behind the clouds
    su, st = P['sun']; s_az = azimuth_of_u(su); s_el = float(elevation_of_t(st))
    daz = (az - s_az + 180) % 360 - 180
    ang = np.degrees(np.arccos(np.clip(np.sin(np.radians(el)) * math.sin(math.radians(s_el)) +
                                       np.cos(np.radians(el)) * math.cos(math.radians(s_el)) * np.cos(np.radians(daz)), -1, 1)))
    glow = np.clip(np.exp(-ang / 30.0) * P['glow'] + np.exp(-ang / 6.0) * 0.35 * (1 - 0.6 * cov), 0, 1)
    SC = np.array(P['sun_col'], np.float32)
    img = img * (1 - glow[..., None]) + SC * glow[..., None]
    # haze band at the horizon (= fog colour) and snowy ground below it
    hz = np.exp(-np.maximum(el, 0) / 4.0)[..., None]
    img = img * (1 - 0.7 * hz) + Hc * 0.7 * hz
    kb = np.clip(-el / 8.0, 0, 1)[..., None]
    img = np.where((el < 0)[..., None], Hc * (1 - kb) + B * kb, img)
    # fine grain so DXT1 does not band
    img += (np.random.default_rng(P['seed'] + 9).random(img.shape[:2])[..., None] - 0.5) * 2.0
    return Image.fromarray(np.clip(img, 0, 255).astype(np.uint8), 'RGB')

def render_night(w=1024, h=1024):
    """Vertical gradient like the original night texture (top = zenith): deep navy to a blue-grey snowy haze that
    matches the night fog colour, with faint moonlit cloud banks."""
    t = ((np.arange(h) + 0.5) / h)[:, None] * np.ones((1, w))
    top, mid, bot = np.array([10, 15, 34], np.float32), np.array([28, 38, 70], np.float32), np.array([66, 80, 112], np.float32)
    a = np.clip(t / 0.6, 0, 1)[..., None]; b = np.clip((t - 0.6) / 0.4, 0, 1)[..., None]
    img = np.where(t[..., None] < 0.6, top * (1 - a) + mid * a, mid * (1 - b) + bot * b)
    n = fbm(w, h, 31, base=6, octaves=5, aspect=1)
    cl = np.clip((n - 0.45) / 0.3, 0, 1)[..., None] * (0.25 + 0.5 * t[..., None])
    img = img * (1 - cl) + np.array([70, 84, 116], np.float32) * cl
    img += (np.random.default_rng(32).random(img.shape[:2])[..., None] - 0.5) * 2.0
    return Image.fromarray(np.clip(img, 0, 255).astype(np.uint8), 'RGB')

def render_stars(w=1024, h=1024):
    """Sparse, dimmer stars (snow clouds hide many): random points, a few brighter, masked by cloud noise."""
    rng = np.random.default_rng(41)
    img = np.zeros((h, w), np.float32)
    n = 900
    xs, ys = rng.integers(0, w, n), rng.integers(0, h, n)
    br = rng.random(n) ** 3 * 200 + 40
    img[ys, xs] = br
    big = br > 170
    for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
        img[(ys[big] + dy) % h, (xs[big] + dx) % w] = br[big] * 0.45
    mask = np.clip((fbm(w, h, 31, base=6, octaves=5, aspect=1) - 0.40) / 0.3, 0, 1)
    img *= (1 - 0.85 * mask)
    rgb = np.stack([img * 0.92, img * 0.96, img], -1)
    return Image.fromarray(np.clip(rgb, 0, 255).astype(np.uint8), 'RGB')

if __name__ == '__main__':
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), 'sky_winter')
    os.makedirs(out, exist_ok=True)
    ims = {'sky_day': render_pano(PRESETS['day']), 'sky_dusk': render_pano(PRESETS['dusk']),
           'sky_night': render_night(), 'sky_stars': render_stars()}
    for k, im in ims.items(): im.save(os.path.join(out, k + '.png'))
    sheet = Image.new('RGB', (1024, 256 + 10 + 257 + 10 + 256), (255, 0, 255))
    sheet.paste(ims['sky_day'].resize((1024, 256)), (0, 0))
    sheet.paste(ims['sky_dusk'].resize((1024, 257)), (0, 266))
    sheet.paste(ims['sky_night'].resize((256, 256)), (0, 533)); sheet.paste(ims['sky_stars'].resize((256, 256)), (266, 533))
    sheet.save(os.path.join(out, 'preview.png'))
    print('wrote', out)
