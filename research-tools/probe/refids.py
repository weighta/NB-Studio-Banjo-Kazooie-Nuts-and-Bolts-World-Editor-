import sys, struct, zlib, re
sys.path.insert(0, 'tools/probe')
from caff import Caff, u32
from asset import Asset
def h(s): return zlib.crc32(s.encode()) ^ 0xffffffff
c = Caff(open(sys.argv[1], 'rb').read())
name = sys.argv[2]
a = Asset(c, c.syms.index(name) + 1); d = a.data()
t = u32(d, 0); n = u32(d, 4)
ch = {u32(d, t + 8 * i): u32(d, t + 8 * i + 4) for i in range(n)}
hh = ch[12]; nref = u32(d, hh); pids = u32(d, hh + 0x18)
refs = [u32(d, pids + 4 * i) for i in range(nref)]
models = {}
for s in c.syms:
    m = re.match(r'aid_model_(.*)', s)
    if m: models[(0x04 << 24) | (h(m.group(1)) & 0xffffff)] = s
hit = 0
for r in refs:
    nm = models.get(r)
    if nm: hit += 1
    print(hex(r), nm)
print(hit, 'of', len(refs))
