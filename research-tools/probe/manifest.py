"""Dump a bundle's manifest asset: python tools/probe/manifest.py <decompressed caff> [n]
manifest .data: magic 0x438CB47C, u32 timestamp, u32 entries ptr, u32 count, u32 deps ptr, u32 dep count;
entries (u32 assetId, u32 ordinal) sorted by id; deps = bundle ids."""
import sys, struct
sys.path.insert(0, 'tools/probe')
from caff import Caff
from asset import Asset
PFX = 'D:' + chr(92) + 'LocalLibrary' + chr(92) + 'BanjoX' + chr(92)
def disp(s):
    if s.startswith(PFX): s = s[len(PFX):]
    return s.split(chr(92))[0].split(',')[0]
c = Caff(open(sys.argv[1], 'rb').read())
n = int(sys.argv[2]) if len(sys.argv) > 2 else 6
mi = c.syms.index('manifest'); a = Asset(c, mi + 1); d = a.data()
magic, ts, ep, cnt, dp, dc = struct.unpack_from('>6I', d, 0)
print(hex(magic), 'timestamp', ts, 'entries', cnt, 'deps', dc, 'symbols', len(c.syms), 'manifest symbol', mi + 1)
ents = [struct.unpack_from('>II', d, ep + 8 * i) for i in range(cnt)]
print('sorted by id:', ents == sorted(ents), 'max ordinal', max(o for _, o in ents))
for aid, o in ents[:n]:
    print(' ', hex(aid), o, '| sym[o]:', disp(c.syms[o]) if o < len(c.syms) else '-', '| sym[o-1]:', disp(c.syms[o - 1]) if 0 < o <= len(c.syms) else '-')
print('deps', [hex(x) for x in struct.unpack_from('>%dI' % dc, d, dp)])
