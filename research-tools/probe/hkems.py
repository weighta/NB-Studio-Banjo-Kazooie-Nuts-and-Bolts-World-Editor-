"""Print hkpExtendedMeshShape triangle subparts of a havok asset (counts/strides) and the wrapper blobs.
python tools/probe/hkems.py <caff> <havok asset name>"""
import sys, os, struct
sys.path.insert(0, os.path.dirname(__file__))
from caff import Caff
from asset import Asset

c = Caff(open(sys.argv[1], 'rb').read())
sym = [i for i, s in enumerate(c.syms, 1) if s.split(',')[0] == sys.argv[2]][0]
d = Asset(c, sym).data()
u = lambda b, o: struct.unpack_from('>I', b, o)[0]
s32 = lambda b, o: struct.unpack_from('>i', b, o)[0]
pfo = u(d, 0x20); pf = d[pfo:pfo + u(d, 0x24)]
secs = []
for i in range(u(pf, 20)):
    o = 0x40 + 0x30 * i
    secs.append((pf[o:o + 19].split(b'\0')[0].decode(), struct.unpack_from('>7I', pf, o + 20)))
cn = secs[0][1]; cnb = pf[cn[0]:cn[0] + cn[1]]
ds = secs[2][1]; db = pf[ds[0]:ds[0] + ds[6]]
local = {}
for k in range(ds[1], ds[2], 8):
    s_, t_ = struct.unpack_from('>II', db, k)
    if s_ == 0xFFFFFFFF: break
    local[s_] = t_
objs = []
for k in range(ds[3], ds[4], 12):
    s_, se, t_ = struct.unpack_from('>III', db, k)
    if s_ == 0xFFFFFFFF: break
    objs.append((s_, cnb[t_:cnb.index(b'\0', t_)].decode()))
print('packfile at', hex(pfo), 'data section at', hex(pfo + ds[0]))
for off, cls in objs:
    if cls != 'hkpExtendedMeshShape': continue
    tp = local.get(off + 0x50); tn = s32(db, off + 0x54)
    print(f'EMS @{off:#x}: trianglesSubparts ptr {tp} n {tn}; shapesSubparts n {s32(db, off + 0x5C)}')
    subs = [(tp + 0x40 * i) for i in range(tn)] if tp is not None else []
    subs.append(off + 0x70)   # embedded
    for so in subs:
        f = lambda r: s32(db, so + r)
        vb = local.get(so + 0x14); ib = local.get(so + 0x30)
        print(f'  subpart @{so:#x}: type {db[so]} nTri {f(0x10)} vertexBase {vb} raw {u(db, so + 0x14):#x} vStride {f(0x18)} nVert {f(0x1C)} '
              f'indexBase {ib} raw {u(db, so + 0x30):#x} iStride {f(0x34)} stridingType {db[so + 0x38]} triOffset {f(0x3C)}')
