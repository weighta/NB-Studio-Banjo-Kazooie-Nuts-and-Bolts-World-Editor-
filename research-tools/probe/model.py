"""Model RE helper: python model.py <caff> <exact model name> [chunk] [len]"""
import sys, struct
sys.path.insert(0, 'tools/probe')
from caff import Caff, u32
from asset import Asset

def load(caffpath, name):
    c = Caff(open(caffpath, 'rb').read())
    sym = [i + 1 for i, s in enumerate(c.syms) if s == name][0]
    return Asset(c, sym)

def chunks(a):
    d = a.data(); t = u32(d, 0); n = u32(d, 4)
    return {u32(d, t + 8 * i): u32(d, t + 8 * i + 4) for i in range(n)}

def dump(a, o, n, sec='.data'):
    d = a.data(sec)
    for x in range(o, min(o + n, len(d)), 4):
        p = a.ptr(x, sec); v = u32(d, x); f = struct.unpack_from('>f', d, x)[0]
        s = '-> %s+0x%x' % p if p else ('%g' % f if 1e-4 < abs(f) < 1e7 else '')
        print('%06x: %08x %s' % (x, v, s))

if __name__ == '__main__':
    a = load(sys.argv[1], sys.argv[2])
    print(a.name, {k: (v[0], hex(len(v[1]))) for k, v in a.parts.items()})
    ch = chunks(a); print({k: hex(v) for k, v in ch.items()})
    if len(sys.argv) > 3:
        dump(a, ch[int(sys.argv[3])], int(sys.argv[4], 16) if len(sys.argv) > 4 else 0x100)
