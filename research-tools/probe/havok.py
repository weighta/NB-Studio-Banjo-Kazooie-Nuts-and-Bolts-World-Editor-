"""Havok binary packfile inside aid_havok assets: header, sections, fixups, objects (class names).
python tools/probe/havok.py <caff> <havok asset name> [dump object index]"""
import sys, os, struct
sys.path.insert(0, os.path.dirname(__file__))
from caff import Caff
from asset import Asset

c = Caff(open(sys.argv[1], 'rb').read())
sym = [i for i, s in enumerate(c.syms, 1) if s.split(',')[0] == sys.argv[2]][0]
a = Asset(c, sym); d = a.data()
u = lambda b, o: struct.unpack_from('>I', b, o)[0]
print('wrapper words:', [hex(u(d, 4 * k)) for k in range(10)])
pf_off, pf_size = u(d, 0x20), u(d, 0x24)
p = d[pf_off:pf_off + pf_size]
assert u(p, 0) == 0x57E0E057 and u(p, 4) == 0x10C0C010, 'not a packfile'
user_tag, ver, layout = u(p, 8), u(p, 12), p[16:20]
nsec, cont_sec, cont_off, cname_sec, cname_off = struct.unpack_from('>iiiii', p, 20)
version = p[40:56].split(b'\0')[0].decode()
print(f'packfile v{ver} layout {layout.hex()} sections {nsec} contents ({cont_sec},{cont_off:#x}) classname ({cname_sec},{cname_off:#x}) "{version}"')
secs = []
for i in range(nsec):
    o = 0x40 + 0x30 * i
    tag = p[o:o + 19].split(b'\0')[0].decode()
    start, lf, gf, vf, ex, im, end = struct.unpack_from('>7I', p, o + 20)
    secs.append(dict(tag=tag, start=start, lf=lf, gf=gf, vf=vf, ex=ex, im=im, end=end))
    print(f'  section {i} {tag:16} start {start:#x} local {lf:#x} global {gf:#x} virtual {vf:#x} exports {ex:#x} imports {im:#x} end {end:#x}')

def sec_bytes(s, a, b): return p[s['start'] + a: s['start'] + b]
cn = secs[0]
# class names section: (u32 signature, u8 0x09, name\0) entries
names = {}
o = 0; cnb = sec_bytes(cn, 0, cn['lf'])
while o + 5 < len(cnb):
    sig = u(cnb, o)
    if sig == 0xFFFFFFFF: break
    e = cnb.index(b'\0', o + 5)
    names[o + 5] = cnb[o + 5:e].decode(); o = e + 1
data = next(s for s in secs if s['tag'] == '__data__')
dsec = sec_bytes(data, 0, data['end'])
# virtual fixups: (u32 objOffset, u32 classNameSection, u32 classNameOffset)
objs = []
vfb = sec_bytes(data, data['vf'], data['ex'])
for k in range(0, len(vfb) - 11, 12):
    oo, sidx, noff = struct.unpack_from('>III', vfb, k)
    if oo == 0xFFFFFFFF: break
    objs.append((oo, names.get(noff, f'?{noff:#x}')))
local = []
lfb = sec_bytes(data, data['lf'], data['gf'])
for k in range(0, len(lfb) - 7, 8):
    s_, t_ = struct.unpack_from('>II', lfb, k)
    if s_ == 0xFFFFFFFF: break
    local.append((s_, t_))
glob = []
gfb = sec_bytes(data, data['gf'], data['vf'])
for k in range(0, len(gfb) - 11, 12):
    s_, sec_, t_ = struct.unpack_from('>III', gfb, k)
    if s_ == 0xFFFFFFFF: break
    glob.append((s_, sec_, t_))
print(f'objects {len(objs)}, local fixups {len(local)}, global fixups {len(glob)}')
objs.sort()
for i, (oo, name) in enumerate(objs):
    nxt = objs[i + 1][0] if i + 1 < len(objs) else data['lf']
    print(f'  [{i}] {oo:#07x} {name} ({nxt - oo} bytes incl. trailing data)')
if len(sys.argv) > 3:
    i = int(sys.argv[3]); oo = objs[i][0]; end = objs[i + 1][0] if i + 1 < len(objs) else data['lf']
    lmap = dict(local); gmap = {s_: (se, t_) for s_, se, t_ in glob}
    for o in range(oo, min(end, oo + 0x200), 4):
        v = u(dsec, o); f = struct.unpack_from('>f', dsec, o)[0]
        tag = f'-> local {lmap[o]:#x}' if o in lmap else f'-> global {gmap[o]}' if o in gmap else (f'{f:g}' if 1e-4 < abs(f) < 1e6 else '')
        print(f'    +{o - oo:03X} {v:08X} {tag}')
