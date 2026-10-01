"""Dump chunk 12 (scenery instances) header, index list and node correspondence of a background model.
python tools/probe/chunk12.py <caff> <background model name>"""
import sys, os, struct
sys.path.insert(0, os.path.dirname(__file__))
from caff import Caff
from asset import Asset

c = Caff(open(sys.argv[1], 'rb').read())
sym = [i for i, s in enumerate(c.syms, 1) if s.split(',')[0] == sys.argv[2]][0]
a = Asset(c, sym); d = a.data(); dp = a.pid()
u = lambda o: struct.unpack_from('>I', d, o)[0]
f = lambda o: struct.unpack_from('>f', d, o)[0]
ct, cn = u(0), u(4)
chunks = {u(ct + 8 * i): u(ct + 8 * i + 4) for i in range(cn)}
h = chunks[12]
print('chunk12 @0x%X' % h)
for k in range(0, 0x30, 4):
    tp = a.ptrs.get((dp, h + k))
    print(f'  +{k:02X}: {u(h + k):08X} {"PTR" if tp else ""}')
nref, n = u(h), u(h + 4)
pIds, pRec, pMat, pPos = u(h + 0x18), u(h + 0x1C), u(h + 0x20), u(h + 0x24)
pIdx = u(h + 0x2C)
print('nref', nref, 'n', n, 'ids@%X rec@%X mat@%X pos@%X idx@%X' % (pIds, pRec, pMat, pPos, pIdx))
print('arrays end: rec', hex(pRec + 0x144 * n), 'mat', hex(pMat + 64 * n), 'pos', hex(pPos + 12 * n))
print('index list first 40:', [u(pIdx + 4 * i) for i in range(min(40, n))])
# pointers from elsewhere into the arrays
for (pp, o), tp in a.ptrs.items():
    if pp != dp or tp != dp: continue
    v = u(o)
    for nm, base, sz in (('rec', pRec, 0x144), ('mat', pMat, 64), ('pos', pPos, 12), ('idx', pIdx, 4)):
        if base <= v < base + sz * n and o not in (h + 0x18, h + 0x1C, h + 0x20, h + 0x24, h + 0x2C):
            print(f'  pointer at 0x{o:X} -> {nm}[{(v - base) // sz}] +{(v - base) % sz}')
# record layout of first 2 instances
for i in range(2):
    r = pRec + 0x144 * i
    words = [u(r + 4 * k) for k in range(15)]
    print(f'rec[{i}] words0-14:', ' '.join(f'{w:08X}' for w in words), 'name', d[r + 0x3C:r + 0x80].split(b'\0')[0])
    print(f'   tail words:', ' '.join(f'{u(r + 0x13C + 4*k):08X}' for k in range(2)))
# chunk 2 nodes count
if 2 in chunks:
    print('chunk2 nodes:', u(chunks[2]))
