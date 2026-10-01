"""Contact sheet of textures matching a regex: python snow/sheet.py <regex> <out.png> [dir]"""
import os, re, sys
from PIL import Image, ImageDraw
rx = re.compile(sys.argv[1]); out = sys.argv[2]; d = sys.argv[3] if len(sys.argv) > 3 else 'snow/tex/orig'
names = sorted(f for f in os.listdir(d) if rx.search(f))
T = 160; cols = 8; rows = (len(names) + cols - 1) // cols
sheet = Image.new('RGB', (cols * T, rows * (T + 24)), (40, 40, 40)); dr = ImageDraw.Draw(sheet)
for i, n in enumerate(names):
    im = Image.open(os.path.join(d, n)).convert('RGBA'); w, h = im.size
    bg = Image.new('RGBA', im.size, (255, 0, 255, 255)); bg.alpha_composite(im)
    im = bg.convert('RGB'); im.thumbnail((T, T))
    x, y = (i % cols) * T, (i // cols) * (T + 24)
    sheet.paste(im, (x, y))
    s = n.replace('aid_texture_banjox_', '').replace('shared_showdowntown_', '').replace('.png', '')
    dr.text((x + 2, y + T + 1), s[:26], fill=(255, 255, 255)); dr.text((x + 2, y + T + 11), (s[26:52] + f' {w}x{h}'), fill=(200, 200, 200))
sheet.save(out); print(len(names), 'textures ->', out)
