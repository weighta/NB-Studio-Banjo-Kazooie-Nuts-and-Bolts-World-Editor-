"""Find and print the 'pose' skeleton in a model/anim .data. python skeleton.py <caff> <asset name> [--all]"""
import sys, struct, os
sys.path.insert(0, os.path.dirname(__file__))
from caff import Caff
from asset import Asset

def cstr(d, o):
    e = d.index(b'\0', o); return d[o:e].decode('latin1')

def parse(d):
    i = d.find(b'pose\0\0\0\0')
    while i >= 0:
        if d[i + 10:i + 12] in (b'19', b'20', b'21', b'18'):
            break
        i = d.find(b'pose\0\0\0\0', i + 1)
    if i < 0: return None
    n = struct.unpack_from('>I', d, i + 0x18)[0]; fps = struct.unpack_from('>H', d, i + 0x1C)[0]
    namehdr, extra, _, joints = struct.unpack_from('>4I', d, i + 0x20)
    cnt, recs = struct.unpack_from('>II', d, namehdr)
    J = []
    for k in range(n):
        r = joints + k * 0x34
        lt = struct.unpack_from('>3f', d, r); wt = struct.unpack_from('>3f', d, r + 12); q = struct.unpack_from('>4f', d, r + 24)
        parent, child, sib, idx, mirror, pad = struct.unpack_from('>6H', d, r + 40)
        np_ = struct.unpack_from('>I', d, recs + 12 * k)[0] if k < cnt else 0
        J.append(dict(name=cstr(d, np_) if np_ else f'joint{k}', parent=parent, child=child, sib=sib, idx=idx, mirror=mirror, lt=lt, wt=wt, q=q))
    return dict(offset=i, count=n, fps=fps, joints=J)

if __name__ == '__main__':
    c = Caff(open(sys.argv[1], 'rb').read())
    sym = [k for k, s in enumerate(c.syms, 1) if s.split(',')[0] == sys.argv[2]][0]
    s = parse(Asset(c, sym).data())
    if not s: sys.exit('no pose object')
    print(f"pose at {s['offset']:#x}: {s['count']} joints, {s['fps']} fps")
    for k, j in enumerate(s['joints'] if '--all' in sys.argv else s['joints'][:6] + s['joints'][-3:]):
        print(j['idx'], j['name'], 'parent', j['parent'] if j['parent'] != 0xFFFF else '-', 'mirror', j['mirror'] if j['mirror'] != 0xFFFF else '-', ['%.3f' % v for v in j['lt']])
