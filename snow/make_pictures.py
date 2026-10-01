"""Gallery pictures of Snowy Showdown Town (snow/pictures/*.jpg) from the photo-mode captures in snow/shots."""
import os, sys
from PIL import Image, ImageDraw, ImageFont
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from clean_shot import clean
SHOTS, OUT = os.path.join(HERE, 'shots'), os.path.join(HERE, 'pictures')
os.makedirs(OUT, exist_ok=True)
FONT = lambda size, bold=True: ImageFont.truetype(r'C:\Windows\Fonts\segoeuib.ttf' if bold else r'C:\Windows\Fonts\segoeui.ttf', size)

def shot(name):
    """Cleaned 1280x720 picture of a photo-mode capture (cached as snow/shots/clean_<name>.png)."""
    out = os.path.join(SHOTS, f'clean_{name}.png')
    if not os.path.exists(out): clean(os.path.join(SHOTS, f'{name}.png'), out)
    return Image.open(out).convert('RGB')

def game(name):
    return Image.open(os.path.join(SHOTS, f'{name}.png')).convert('RGB').crop((8, 52, 1288, 772))

def label(im, text, corner='bl', size=26):
    d = ImageDraw.Draw(im, 'RGBA'); f = FONT(size)
    l, t, r, b = d.textbbox((0, 0), text, font=f); w, h = r - l, b - t
    pad = size // 2
    x = 18 if corner.endswith('l') else im.width - w - 2 * pad - 18
    y = im.height - h - 2 * pad - 18 if corner.startswith('b') else 18
    d.rounded_rectangle((x, y, x + w + 2 * pad, y + h + 2 * pad), radius=pad, fill=(12, 18, 32, 170))
    d.text((x + pad - l, y + pad - t), text, font=f, fill=(255, 255, 255, 255))
    return im

def save(im, name, q=90):
    im.save(os.path.join(OUT, name), quality=q, optimize=True, progressive=True)
    print(name, im.size)

# hero: Mumbo's Motors under a starry snowy night
save(label(shot('s5_night_cam6'), 'Snowy Showdown Town'), 'hero_night.jpg')

# before / after (same camera): left half vanilla, right half snowy
for pre, ph in (('mid', 'Midday'), ('night', 'Night')):
    a = label(shot(f'v0_{pre}_cam1').resize((640, 360), Image.LANCZOS), f'{ph}: original', 'bl', 18)
    b = label(shot(f's5_{pre}_cam1').resize((640, 360), Image.LANCZOS), f'{ph}: snowy', 'bl', 18)
    im = Image.new('RGB', (1280, 360)); im.paste(a, (0, 0)); im.paste(b, (640, 0))
    ImageDraw.Draw(im).line((640, 0, 640, 360), fill=(255, 255, 255), width=2)
    save(im, f'before_after_{pre}.jpg')

# the four times of day
grid = Image.new('RGB', (1280, 720))
for i, (n, t) in enumerate((('s6_morn_cam1', 'Morning'), ('s5_mid_cam1', 'Midday'), ('s5_aft_cam1', 'Dusk'), ('s5_night_cam1', 'Night'))):
    im = label(shot(n).resize((640, 360), Image.LANCZOS), t, 'bl', 18)
    grid.paste(im, ((i % 2) * 640, (i // 2) * 360))
d = ImageDraw.Draw(grid); d.line((640, 0, 640, 720), fill=(255, 255, 255), width=2); d.line((0, 360, 1280, 360), fill=(255, 255, 255), width=2)
save(grid, 'times_of_day.jpg')

# vistas and details
save(shot('s6_morn_cam5'), 'vista_morning.jpg')
save(shot('s5_aft_cam5'), 'vista_dusk.jpg')
save(shot('s6_morn_cam2'), 'clocktower_morning.jpg')
save(shot('s5_night_cam2'), 'clocktower_night.jpg')
save(shot('s5_mid_cam4'), 'rooftops_midday.jpg')
save(game('s5_mid_play_b'), 'gameplay_midday.jpg')
save(game('coop_host_sees'), 'coop.jpg')
