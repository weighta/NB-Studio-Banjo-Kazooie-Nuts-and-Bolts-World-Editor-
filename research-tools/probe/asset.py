"""Load one asset (all its parts) from a CAFF with resolved pointer map, for format RE."""
import sys, struct
sys.path.insert(0, 'tools/probe')
from caff import Caff, u32

class Asset:
    def __init__(self, caff, sym):
        self.c = caff; self.sym = sym; self.name = caff.syms[sym - 1]
        self.parts = {}   # part id -> (secname, bytes)
        for pid, p in enumerate(caff.parts, 1):
            if p[0] == sym:
                sec = caff.secs[p[3] - 1]
                self.parts[pid] = (sec['name'], caff.d[sec['start'] + p[1]: sec['start'] + p[1] + p[2]])
        # relocations: parse groups
        d = caff.d; b = caff.infoend; nt = caff.h[6]
        groups = [struct.unpack_from('>III', d, b + 12 * i) for i in range(nt)]
        o = b + 12 * nt
        self.ptrs = {}  # (frompart, offset) -> (topart)
        for (fp, tp, n) in groups:
            offs = struct.unpack_from('>%dI' % n, d, o); o += 4 * n
            if fp in self.parts:
                for x in offs: self.ptrs[(fp, x)] = tp
        self.bysec = {v[0]: k for k, v in self.parts.items()}

    def data(self, sec='.data'): return self.parts[self.bysec[sec]][1]
    def pid(self, sec='.data'): return self.bysec[sec]
    def ptr(self, off, sec='.data'):
        """returns (target section name, target offset) if the u32 at off is a pointer"""
        pid = self.bysec[sec]
        tp = self.ptrs.get((pid, off))
        if tp is None: return None
        tsec = self.c.secs[self.c.parts[tp - 1][3] - 1]['name'] if tp in self.parts else 'EXT:%d:%s' % (tp, self.c.syms[self.c.parts[tp - 1][0] - 1][:50])
        return (tsec, u32(self.parts[pid][1], off))

def find(caff, substr):
    return [i + 1 for i, s in enumerate(caff.syms) if substr in s]

if __name__ == '__main__':
    c = Caff(open(sys.argv[1], 'rb').read())
    for sym in find(c, sys.argv[2])[:1]:
        a = Asset(c, sym)
        print(a.name, {k: (v[0], hex(len(v[1]))) for k, v in a.parts.items()})
        d = a.data()
        lim = int(sys.argv[3], 16) if len(sys.argv) > 3 else 0x200
        for o in range(0, min(len(d), lim), 4):
            p = a.ptr(o)
            v = u32(d, o)
            f = struct.unpack_from('>f', d, o)[0]
            s = ''
            if p: s = '-> %s+0x%x' % p
            elif 0.0001 < abs(f) < 100000: s = '%g' % f
            print('%06x: %08x %s' % (o, v, s))
