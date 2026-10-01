"""Print raw vertex attributes of a model's vertex buffers (for identifying unknown channels).
python tools/probe/vattr.py <caff> <model name> [vb index] [count]"""
import sys, os, struct, collections
sys.path.insert(0, os.path.dirname(__file__))
from caff import Caff
from asset import Asset

c = Caff(open(sys.argv[1], 'rb').read())
sym = [i for i, s in enumerate(c.syms, 1) if s.split(',')[0] == sys.argv[2]][0]
a = Asset(c, sym); d = a.data(); g = a.parts[a.pid('.gpu')][1]
R = None
for (part, off), tp in a.ptrs.items():
    if part == a.pid() and tp == a.pid() and off >= 0x48:
        t = struct.unpack_from('>I', d, off)[0]
        if (a.pid(), t + 4) in a.ptrs and a.parts.get(a.ptrs[(a.pid(), t + 4)], ('',))[0] == '.gpu' and (a.pid(), off - 0x48 + 0x54) in a.ptrs:
            R = off - 0x48; break
vt = struct.unpack_from('>I', d, R + 0x48)[0]; nvb = struct.unpack_from('>I', d, R + 0x50)[0]
which = int(sys.argv[3]) if len(sys.argv) > 3 else 0
cnt = int(sys.argv[4]) if len(sys.argv) > 4 else 12
for vi in range(nvb):
    e = vt + 12 * vi
    rec = struct.unpack_from('>I', d, e)[0] - 4
    stride = struct.unpack_from('>I', d, rec)[0]; go, size = struct.unpack_from('>II', d, e + 4)
    n = size // stride
    h = lambda o: struct.unpack('>e', g[o:o + 2])[0]
    c8 = collections.Counter()
    for k in range(n):
        o = go + k * stride
        c8[struct.unpack_from('>4h', g, o + 8)] += 1
    print(f'vb {vi}: rec 0x{rec:X} stride {stride} verts {n}; distinct +8 values: {len(c8)} most common {c8.most_common(4)}')
    if vi == which:
        for k in range(min(cnt, n)):
            o = go + k * stride
            pos = [round(h(o + 2 * j), 3) for j in range(4)]
            print(f'   v{k}: pos {pos} +8 {struct.unpack_from(">4h", g, o + 8)} col {g[o+16:o+20].hex()} n {g[o+20:o+24].hex()} t {g[o+24:o+28].hex()} uv {[round(h(o+28),3), round(h(o+30),3)]}')
