import sys, os, struct, collections, re
sys.path.insert(0, 'tools/probe')
from caff import Caff
from asset import Asset
def records(d):
    o = 0
    while o + 8 <= len(d):
        size = struct.unpack_from('>I', d, o)[0]
        if size < 8 or o + size > len(d): return o
        typ, idx = struct.unpack_from('>HH', d, o + 4)
        yield o, size, typ, idx
        o += size
    return o
types = collections.Counter(); sizes = collections.defaultdict(collections.Counter); strs = collections.defaultdict(collections.Counter)
bad = []; total = 0; ptrs = 0
for n in sorted(os.listdir('work/decomp/4f')):
    c = Caff(open('work/decomp/4f/' + n, 'rb').read())
    for i, s in enumerate(c.syms):
        if not s.startswith('aid_marker'): continue
        a = Asset(c, i + 1); d = a.data(); ptrs += len([1 for (p, o) in a.ptrs if p == a.pid()])
        end = 0; cnt = 0
        for o, size, typ, idx in records(d):
            end = o + size; cnt += 1
            types[typ] += 1; sizes[typ][size] += 1
            for m in re.finditer(rb'[a-zA-Z_]{8,}', d[o:o + size]): strs[typ][re.sub(rb'_[A-Z][A-Za-z0-9]+$', b'', m.group()).decode()[:40]] += 1
        total += cnt
        if end != len(d): bad.append((n, s[:50], hex(end), hex(len(d))))
print('records', total, 'pointer relocs in markers', ptrs, 'bad ends', bad[:5])
for t, v in sorted(types.items()):
    print('type %3d x%5d sizes %s strings %s' % (t, v, dict(sizes[t].most_common(3)), dict(strs[t].most_common(3))))
