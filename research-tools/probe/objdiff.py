"""Compare all objparams of one class word by word: shows which words vary and their values.
python tools/probe/objdiff.py <ws> <objDefId_x> [highlight asset substring]"""
import sys, os, json, struct
sys.path.insert(0, os.path.dirname(__file__))
from caff import Caff
from asset import Asset

ws, cls = sys.argv[1], sys.argv[2]; hl = sys.argv[3] if len(sys.argv) > 3 else None
idx = json.load(open(os.path.join(ws, 'cache', 'assetindex.json')))
E = idx['Entries'] if 'Entries' in idx else idx['entries']
_c = {}
def caff(b):
    if b not in _c:
        p = os.path.join(ws, 'cache', '4f', '%06x' % b)
        if not os.path.exists(p): p = os.path.join(ws, 'game', 'Bundle', '4f', '%06x' % b)
        _c[b] = Caff(open(p, 'rb').read())
    return _c[b]
seen = set(); items = []
for e in E:
    if e['Type'] != 'objparams' or e['Symbol'] <= 0 or e['Name'] in seen: continue
    a = Asset(caff(e['Bundle']), e['Symbol']); d = a.data()
    if d[0x42:0x42 + len(cls)] != cls.encode() or d[0x42 + len(cls)] != 0: continue
    seen.add(e['Name']); items.append((e['Name'], d, a))
print(len(items), 'instances of', cls)
ref = next((x for x in items if hl and hl in x[0]), items[0])
print('highlight:', ref[0])
n = len(ref[1])
o = 0
while o < n:
    # 64-byte string field?
    if all(32 <= ref[1][o + i] < 127 or ref[1][o + i] == 0 for i in range(0, min(64, n - o))) and 65 <= ref[1][o] <= 122 and o % 4 == 0 and ref[1][o+1] != 0:
        e = ref[1].find(b'\0', o)
        vals = set(x[1][o:x[1].find(b'\0', o)].decode('latin1', 'replace') for x in items)
        print(f'{o:04X} str  "{ref[1][o:e].decode()}"  ({len(vals)} distinct)')
        o = (e + 4) & ~3 if e - o < 60 else o + 64
        # skip zero padding up to next non-zero word
        while o < n and struct.unpack_from('>I', ref[1], o)[0] == 0 and all(struct.unpack_from('>I', x[1], o)[0] == 0 for x in items): o += 4
        continue
    ws_ = [struct.unpack_from('>I', x[1], o)[0] for x in items]
    w = struct.unpack_from('>I', ref[1], o)[0]
    ptr = ref[2].ptrs.get((ref[2].pid(), o))
    distinct = len(set(ws_))
    f = struct.unpack_from('>f', ref[1], o)[0]
    fs = sorted(set(struct.unpack('>f', struct.pack('>I', v))[0] for v in ws_))
    looks_float = all(v == 0 or 0x30000000 <= (v & 0x7FFFFFFF) <= 0x4B000000 for v in ws_)
    if ptr: desc = 'PTR'
    elif looks_float and any(ws_): desc = f'f {f:g}  range [{fs[0]:g} .. {fs[-1]:g}]'
    elif all(v < 0x10000 for v in ws_): desc = f'i {w}  values {sorted(set(ws_))[:8]}'
    else: desc = f'x {w:08X}'
    if any(ws_) or ptr:
        print(f'{o:04X} {"*" if distinct > 1 else " "} {desc}')
    o += 4
