"""Dump a model's resource header, GPU buffer table, IB table, VB records and bounds chunk.
python tools/probe/modelres.py <caff> <model name>"""
import sys, os, struct
sys.path.insert(0, os.path.dirname(__file__))
from caff import Caff
from asset import Asset

c = Caff(open(sys.argv[1], 'rb').read())
sym = c.syms.index(next(s for s in c.syms if s.split(',')[0] == sys.argv[2])) + 1
a = Asset(c, sym)
d = a.data(); dp = a.pid('.data'); gp = a.pid('.gpu'); sp = a.pid('.stream')
u = lambda o: struct.unpack_from('>I', d, o)[0]
f = lambda o: struct.unpack_from('>f', d, o)[0]
def P(o, part=None):
    tp = a.ptrs.get((part or dp, o))
    if tp is None: return None
    return (a.parts[tp][0] if tp in a.parts else f'part{tp}', u(o) if (part or dp) == dp else struct.unpack_from('>I', a.parts[part][1], o)[0])
print('sizes: data', len(d), 'gpu', len(a.parts[gp][1]), 'stream', len(a.parts[sp][1]))
ct, cn = u(0), u(4)
chunks = {u(ct + 8 * i): u(ct + 8 * i + 4) for i in range(cn)}
print('chunks', {k: hex(v) for k, v in chunks.items()})
# resource header: find R with R+0x48 ptr to data whose +4 is a gpu ptr
for (part, off), tp in a.ptrs.items():
    if part != dp or tp != dp or off < 0x48: continue
    R = off - 0x48
    t = u(off)
    if (dp, t + 4) in a.ptrs and a.ptrs[(dp, t + 4)] == gp and (dp, R + 0x54) in a.ptrs:
        break
print(f'R = 0x{R:X}')
for k in range(0, 0x70, 4):
    p = P(R + k)
    print(f'  R+{k:02X}: {u(R + k):08X} {"-> " + p[0] + "+0x%X" % p[1] if p else ""}')
# GPU buffer table
gt = u(R + 0x48)
print('GPU buffer table @0x%X' % gt)
i = 0
while True:
    e = gt + 12 * i
    if (dp, e + 4) not in a.ptrs or a.ptrs[(dp, e + 4)] != gp: break
    obj = P(e)
    print(f'  [{i}] obj {obj} gpu 0x{u(e + 4):X} size 0x{u(e + 8):X}')
    i += 1
print('count guesses: R+0x44', u(R + 0x44), 'R+0x4C', u(R + 0x4C), 'entries seen', i)
ibt, nib = u(R + 0x54), u(R + 0x58)
print(f'IB table @0x{ibt:X} n={nib}')
for k in range(nib):
    e = ibt + 16 * k
    print(f'  ib obj 0x{u(e):X} gpu 0x{u(e+4):X} size 0x{u(e+8):X} fmt {u(e+12):08X}  ptrs {[P(e+j) for j in (0,4,8,12)]}')
# vb records referenced by the GPU table
if 5 in chunks:
    b = chunks[5]
    print('chunk 5 @0x%X:' % b, [round(f(b + 4 * k), 3) for k in range(12)])
if 8 in chunks:
    b = chunks[8]; print('chunk 8 @0x%X:' % b, [hex(u(b + 4 * k)) for k in range(8)])
