import sys, struct, os
sys.path.insert(0, 'tools/probe')
from caff import Caff
from asset import Asset
def parse(d, verbose=False):
    # .data: u32 0xC (ptr to LSBL?), u32 0, u32 0, then LSBL table (little-endian)
    b = 0xC
    assert d[b:b + 4] == b'LSBL', d[b:b + 4]
    h = struct.unpack_from('<7I', d, b + 4)
    if verbose: print('hdr', [hex(x) for x in h])
    cnt = struct.unpack_from('<I', d, b + 0x1c)[0]
    ents = [struct.unpack_from('<HI', d, b + 0x20 + 6 * i) for i in range(cnt)]
    return h, cnt, ents
root = 'Banjo Kazooie Nuts & Bolts (FRESH)/Debug/11'
n = 0
for dp, ds, fs in os.walk(root):
    for f in fs:
        c = Caff(open(os.path.join(dp, f), 'rb').read())
        a = Asset(c, 1); d = a.data()
        h, cnt, ents = parse(d, n < 3)
        strbase = 0xC + 0x20 + 6 * cnt
        if n < 3:
            print(c.syms[0], len(d), 'cnt', cnt, 'strbase', hex(strbase), ents[:4])
            print('  at strbase', d[strbase:strbase + 24].hex(' '))
            for idx, off in ents[:3]:
                for base in (strbase,):
                    o = base + off
                    e = d.find(b'\0\0', o)
                    while e != -1 and (e - o) % 2: e = d.find(b'\0\0', e + 1)
                    print('   ', hex(idx), hex(off), d[o:e].decode('utf-16le', 'replace')[:70])
        n += 1
print(n, 'files')
