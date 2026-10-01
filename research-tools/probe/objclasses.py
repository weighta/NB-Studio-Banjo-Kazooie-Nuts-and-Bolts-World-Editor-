"""Survey objparams: class (objDefId) per asset, sizes per class. python tools/probe/objclasses.py <ws> [filter]"""
import sys, os, json, collections
sys.path.insert(0, os.path.dirname(__file__))
from caff import Caff
from asset import Asset

ws = sys.argv[1]; flt = sys.argv[2] if len(sys.argv) > 2 else ''
idx = json.load(open(os.path.join(ws, 'cache', 'assetindex.json')))
E = idx['Entries'] if 'Entries' in idx else idx['entries']
_c = {}
def caff(b):
    if b not in _c:
        p = os.path.join(ws, 'cache', '4f', '%06x' % b)
        if not os.path.exists(p): p = os.path.join(ws, 'game', 'Bundle', '4f', '%06x' % b)
        _c[b] = Caff(open(p, 'rb').read())
    return _c[b]

def cs(d, o, n=62):
    e = d.find(b'\0', o, o + n)
    return d[o:e if e >= 0 else o + n].decode('latin1')

seen = set(); byclass = collections.defaultdict(list)
for e in E:
    if e['Type'] != 'objparams' or e['Symbol'] <= 0 or e['Name'] in seen: continue
    seen.add(e['Name'])
    a = Asset(caff(e['Bundle']), e['Symbol'])
    d = a.data()
    tag, cls = cs(d, 2), cs(d, 0x42)
    byclass[cls].append((e['Name'], len(d), tag, e['Bundle']))
for cls, L in sorted(byclass.items(), key=lambda kv: -len(kv[1])):
    sizes = collections.Counter(x[1] for x in L)
    if flt and not any(flt in x[0] for x in L) and flt not in cls: continue
    print(f'{cls:50} n={len(L):4} sizes={dict(sizes)} tag={L[0][2]}')
    if flt:
        for x in L:
            if flt in x[0] or flt in cls: print(f'      {x[0]} ({x[1]} bytes, {x[3]:06x})')
