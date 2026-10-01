"""clean_shot.py in.png out.png [--width W] : a Xenia window capture of the game's photo mode -> a clean picture.
Crops the 1280x720 game area, removes the viewfinder brackets (filled from their surroundings) and the semi-transparent
focus circle (its constant overlay is divided out: circle at (640.5, 360), radius 39, white at 26% opacity), then
optionally resizes."""
import sys
import numpy as np
from PIL import Image

MASK = None

def brackets():
    """The viewfinder brackets in game-area coordinates (photo_overlay_mask.png: pixels bright in every night frame)."""
    import os
    return np.asarray(Image.open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'photo_overlay_mask.png'))) > 0

def fill(img, mask, iters=60):
    """Diffuse surrounding colours into the masked pixels."""
    out = img.copy(); known = ~mask
    for _ in range(iters):
        acc = np.zeros_like(out); cnt = np.zeros(mask.shape, np.float32)
        for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            sh = np.roll(out, (dy, dx), (0, 1)); k = np.roll(known, (dy, dx), (0, 1))
            acc += sh * k[..., None]; cnt += k
        upd = mask & (cnt > 0)
        out[upd] = acc[upd] / cnt[upd][:, None]
        known = known | upd
    return out

def clean(path, out, width=None):
    im = np.asarray(Image.open(path).convert('RGB')).astype(np.float32)[52:772, 8:1288]
    yy, xx = np.mgrid[0:720, 0:1280]
    d = np.hypot(yy - 360.0, xx - 640.5)
    a = (0.26 * np.clip(39.5 - d, 0, 1))[..., None]
    im = (im - 255 * a) / (1 - a)
    m = brackets()
    m = m | np.roll(m, 1, 0) | np.roll(m, -1, 0) | np.roll(m, 1, 1) | np.roll(m, -1, 1)   # include the anti-aliased edge
    im = fill(np.clip(im, 0, 255), m)
    res = Image.fromarray(np.clip(im, 0, 255).astype(np.uint8))
    if width: res = res.resize((width, round(width * 720 / 1280)), Image.LANCZOS)
    res.save(out)

if __name__ == '__main__':
    w = int(sys.argv[sys.argv.index('--width') + 1]) if '--width' in sys.argv else None
    clean(sys.argv[1], sys.argv[2], w)
