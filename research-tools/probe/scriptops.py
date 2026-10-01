"""Survey the command-list script format across all aid_script assets."""
import sys, os, struct, collections
sys.path.insert(0, 'tools/probe')
from caff import Caff
from asset import Asset

def commands(d):
    hs = struct.unpack_from('>I', d, 0)[0]          # header size (0x14 + 4*n?)
    o = hs
    out = []
    while o + 8 <= len(d):
        size, op = struct.unpack_from('>II', d, o)
        if size < 8 or o + size > len(d): return out, o
        out.append((o, size, op)); o += size
    return out, o

if __name__ == '__main__':
    sizes = collections.defaultdict(collections.Counter); ok = bad = 0; badlist = []
    for n in sorted(os.listdir('work/decomp/4f')):
        c = Caff(open('work/decomp/4f/' + n, 'rb').read())
        for i, s in enumerate(c.syms):
            if not s.startswith('aid_script_'): continue
            a = Asset(c, i + 1)
            if '.data' not in a.bysec: continue
            d = a.data()
            cmds, end = commands(d)
            if end == len(d): ok += 1
            else: bad += 1; badlist.append((n, s[:60], hex(end), hex(len(d)), d[:0x18].hex()))
            for o, size, op in cmds: sizes[op][size] += 1
    print('scripts parsed exactly', ok, 'not', bad)
    for b in badlist[:8]: print('  ', b)
    for op in sorted(sizes): print('op %3d (0x%02x): %s' % (op, op, dict(sizes[op].most_common(4))))
