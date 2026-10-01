"""List scenery instances of a background model: python instances.py <caff> <model name substring>"""
import sys, struct, collections
sys.path.insert(0, 'tools/probe')
from caff import Caff, u32
from asset import Asset, find
c = Caff(open(sys.argv[1], 'rb').read())
a = Asset(c, find(c, sys.argv[2])[0]); d = a.data()
t = u32(d, 0); n = u32(d, 4)
chunks = {u32(d, t + 8 * i): u32(d, t + 8 * i + 4) for i in range(n)}
h = chunks[12]
nref, ninst = u32(d, h + 4 * 0), u32(d, h + 4 * 1)
p_ids, p_names, p_mats, p_bnd, _, p_idx = [u32(d, h + 0x18 + 4 * k) for k in range(6)]
print(a.name, 'refs', nref, 'instances', ninst, 'chunks', {k: hex(v) for k, v in chunks.items()})
for i in range(ninst):
    o = p_names + 0x144 * i
    nm = d[o + 0x3c:o + 0x3c + 80].split(b'\0')[0].decode().strip('|').replace('REFERENCE_', '')
    mat = struct.unpack_from('>16f', d, p_mats + 64 * i)
    print('%4d ref=%3d %-36s pos=(%8.2f %8.2f %8.2f)' % (i, u32(d, o), nm, mat[12], mat[13], mat[14]))
