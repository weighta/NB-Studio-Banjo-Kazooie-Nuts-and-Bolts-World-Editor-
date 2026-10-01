import sys
from PIL import Image
pre = sys.argv[1]
fs = [f'{pre}_play.png', f'{pre}_play_b.png'] + [f'{pre}_cam{k}.png' for k in range(7)]
ims = [Image.open(f).crop((8, 52, 1288, 772)) for f in fs]
w, h = 640, 360; s = Image.new('RGB', (w * 3, h * 3))
for i, im in enumerate(ims): s.paste(im.resize((w, h)), ((i % 3) * w, (i // 3) * h))
s.save(f'{pre}_sheet.png')
