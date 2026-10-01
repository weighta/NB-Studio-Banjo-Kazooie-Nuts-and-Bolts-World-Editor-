"""Dump a script asset with pointer annotations and resolved asset ids: python script.py <caff> <name prefix>"""
import sys, struct, re, zlib
sys.path.insert(0, 'tools/probe')
from caff import Caff
from asset import Asset

def h(s): return (zlib.crc32(s.encode()) ^ 0xffffffff) & 0xffffff
TB = {'cutscene': 8, 'cutsceneevents': 9, 'dialog': 0x43, 'script': 0x19, 'misc': 0x0b, 'cutcam': 0x46, 'marker': 0x0d,
      'objparams': 0x1f, 'anim': 2, 'callout': 0x0e, 'actorgoals': 0x0c, 'challenge': 0x3d, 'model': 4, 'loctext': 0x11,
      'scripttable': 0x23, 'statetable': 0x24, 'vehicle': 0, 'aidlist': 0x0f}
names = {}
for line in open('work/symindex.tsv', encoding='utf-8'):
    f = line.split('\t')
    m = re.match(r'(?:.*\\)?aid_([a-z0-9]+)_([^,\\]+)', f[2])
    if m and m.group(1) in TB: names[(TB[m.group(1)] << 24) | h(m.group(2))] = 'aid_%s_%s' % (m.group(1), m.group(2))

def dump(caffpath, prefix):
    c = Caff(open(caffpath, 'rb').read())
    i = [k + 1 for k, s in enumerate(c.syms) if s.startswith(prefix)][0]
    a = Asset(c, i); d = a.data()
    print(c.syms[i - 1], len(d), 'ptrs', len([1 for (p, o) in a.ptrs if p == a.pid()]))
    for o in range(0, len(d) - 3, 4):
        v = struct.unpack_from('>I', d, o)[0]; p = a.ptr(o)
        txt = ''.join(chr(x) if 32 <= x < 127 else '.' for x in d[o:o + 4])
        f = struct.unpack_from('>f', d, o)[0]
        fs = ('%g' % f) if 1e-3 < abs(f) < 1e6 else ''
        if v or p: print('%04x %08x %s %-10s %s %s' % (o, v, txt, fs, ('-> %s+%x' % p) if p else '', names.get(v, '')))

if __name__ == '__main__':
    dump(sys.argv[1], sys.argv[2])
