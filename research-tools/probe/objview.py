"""Quick OBJ preview: flat-shaded software render from 3 angles -> PNG.  python objview.py in.obj out.png"""
import sys, math
import numpy as np
from PIL import Image, ImageDraw
V = []; F = []
for line in open(sys.argv[1]):
    if line.startswith('v '): V.append([float(x) for x in line.split()[1:4]])
    elif line.startswith('f '):
        idx = [int(t.split('/')[0]) - 1 for t in line.split()[1:4]]
        F.append(idx)
V = np.array(V); F = np.array(F)
c = (V.min(0) + V.max(0)) / 2; r = np.abs(V - c).max() or 1
S = 420
out = Image.new('RGB', (S * 3, S), (30, 30, 36))
for k, (yaw, pitch) in enumerate([(30, 25), (150, 25), (0, 80)]):
    y, p = math.radians(yaw), math.radians(pitch)
    Ry = np.array([[math.cos(y), 0, math.sin(y)], [0, 1, 0], [-math.sin(y), 0, math.cos(y)]])
    Rx = np.array([[1, 0, 0], [0, math.cos(p), -math.sin(p)], [0, math.sin(p), math.cos(p)]])
    P = (V - c) @ Ry.T @ Rx.T / r
    img = Image.new('RGB', (S, S), (30, 30, 36)); dr = ImageDraw.Draw(img)
    tri = P[F]
    n = np.cross(tri[:, 1] - tri[:, 0], tri[:, 2] - tri[:, 0]); ln = np.linalg.norm(n, axis=1) + 1e-9
    shade = np.abs(n[:, 2] / ln)
    order = np.argsort(tri[:, :, 2].mean(1))
    for i in order:
        pts = [(S / 2 + q[0] * S * 0.27, S / 2 - q[1] * S * 0.27) for q in tri[i]]
        g = int(60 + 180 * shade[i]); dr.polygon(pts, fill=(g, g, int(g * 0.9)))
    out.paste(img, (k * S, 0))
out.save(sys.argv[2]); print(len(V), 'verts', len(F), 'tris')
