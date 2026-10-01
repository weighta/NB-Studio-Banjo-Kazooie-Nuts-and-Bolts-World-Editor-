"""Experiment: offset instance transforms in a background model, in place.
python patch_instances.py <caff in> <caff out> <model substr> <name substr> <dx> <dy> <dz> [--only-mat|--only-pl]"""
import sys, struct
sys.path.insert(0, 'tools/probe')
from caff import Caff, u32
from asset import Asset, find
src, dst, model, sub = sys.argv[1:5]; print(repr(sub))
dx, dy, dz = map(float, sys.argv[5:8])
mode = sys.argv[8] if len(sys.argv) > 8 else ''
raw = bytearray(open(src, 'rb').read())
c = Caff(bytes(raw))
sym = find(c, model)[0]
a = Asset(c, sym); d = a.data()
pid = a.pid()
p = c.parts[pid - 1]; base = c.secs[p[3] - 1]['start'] + p[1]
t = u32(d, 0); n = u32(d, 4)
ch = {u32(d, t + 8 * i): u32(d, t + 8 * i + 4) for i in range(n)}
h = ch[12]; ninst = u32(d, h + 4)
p_names, p_mats, p_bnd = u32(d, h + 0x1c), u32(d, h + 0x20), u32(d, h + 0x24)
npl = u32(d, ch[2])
def rf(o): return struct.unpack_from('>f', raw, base + o)[0]
def wf(o, v): struct.pack_into('>f', raw, base + o, v)
count = 0
for i in range(ninst):
    o = p_names + 0x144 * i
    nm = d[o + 0x3c:o + 0x3c + 80].split(b'\0')[0].decode()
    if sub not in nm: continue
    m = p_mats + 64 * i
    old = (rf(m + 48), rf(m + 52), rf(m + 56))
    if mode != '--only-pl':
        wf(m + 48, old[0] + dx); wf(m + 52, old[1] + dy); wf(m + 56, old[2] + dz)
        b = p_bnd + 12 * i
        wf(b, rf(b) + dx); wf(b + 4, rf(b + 4) + dy); wf(b + 8, rf(b + 8) + dz)
    if mode != '--only-mat':
        for k in range(npl):
            q = ch[2] + 4 + 68 * k
            tr = (rf(q + 16), rf(q + 32), rf(q + 48))
            if all(abs(tr[j] - old[j]) < 1e-3 for j in range(3)):
                wf(q + 16, tr[0] + dx); wf(q + 32, tr[1] + dy); wf(q + 48, tr[2] + dz)
                print('   placement', k)
    count += 1
    print('moved', i, nm, old)
open(dst, 'wb').write(raw)
print(count, 'instances')
