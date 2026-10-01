import sys, struct, zlib
sys.path.insert(0, 'tools/probe')
from caff import Caff, u32
from asset import Asset
c = Caff(open(sys.argv[1] if len(sys.argv) > 1 else 'work/decomp/4f/01d1b6', 'rb').read())
man = [i + 1 for i, s in enumerate(c.syms) if s == 'manifest'][0]
m = Asset(c, man).data(); cnt = u32(m, 12)
ents = [struct.unpack_from('>II', m, 0x20 + 8 * i) for i in range(cnt)]
PFX = 'D:\\LocalLibrary\\BanjoX\\'
def h(s): return zlib.crc32(s.encode()) ^ 0xffffffff
ok = 0
for aid, idx in ents:
    n = c.syms[idx - 1]
    s = n[len(PFX):] if n.startswith(PFX) else n
    stem = s.split('\\')[0].split(',')[0]
    cands = [n, s, stem, stem.replace('aid_', '', 1), '_'.join(stem.split('_')[2:])]
    hit = [v for v in cands if h(v) == aid or (h(v) & 0xffffff) == (aid & 0xffffff)]
    if hit: ok += 1
    elif ok < 3 or len(sys.argv) > 2: print('miss', hex(aid), [hex(h(v)) for v in cands], stem[:60])
    if hit and ok <= 3: print('hit', hex(aid), hit, hex(h(hit[0])))
print(ok, 'of', len(ents))
