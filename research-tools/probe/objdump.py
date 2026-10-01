"""Dump any asset's parts as words with pointers/strings marked.
python tools/probe/objdump.py <workspace> <asset name substring> [max assets] [max words]"""
import sys, os, json, struct
sys.path.insert(0, os.path.dirname(__file__))
from caff import Caff
from asset import Asset

ws = sys.argv[1]; q = sys.argv[2]
maxn = int(sys.argv[3]) if len(sys.argv) > 3 else 1
maxw = int(sys.argv[4]) if len(sys.argv) > 4 else 256
idx = json.load(open(os.path.join(ws, 'cache', 'assetindex.json')))
E = idx['Entries'] if 'Entries' in idx else idx['entries']
hits = [e for e in E if q in e['Name'] and e['Symbol'] > 0]
_c = {}
def caff(b):
    if b not in _c:
        p = os.path.join(ws, 'cache', '4f', '%06x' % b)
        if not os.path.exists(p): p = os.path.join(ws, 'game', 'Bundle', '4f', '%06x' % b)
        _c[b] = Caff(open(p, 'rb').read())
    return _c[b]

def printable(b):
    return all(32 <= x < 127 for x in b)

for e in hits[:maxn]:
    c = caff(e['Bundle']); a = Asset(c, e['Symbol'])
    print('=' * 100); print(a.name, 'bundle %06x' % e['Bundle'])
    for pid, (sec, d) in a.parts.items():
        print(f'-- part {pid} {sec} {len(d)} bytes')
        if sec not in ('.data',): continue
        for o in range(0, min(len(d), maxw * 4), 4):
            w = struct.unpack_from('>I', d, o)[0]
            f = struct.unpack_from('>f', d, o)[0]
            tp = a.ptrs.get((pid, o))
            note = ''
            if tp is not None:
                tsec = a.parts[tp][0] if tp in a.parts else f'part{tp}'
                note = f'-> {tsec}+0x{w:X}'
                if tp in a.parts:
                    td = a.parts[tp][1]
                    end = td.find(b'\0', w)
                    if 0 < end - w < 80 and printable(td[w:end]): note += f' "{td[w:end].decode()}"'
            elif 1e-4 < abs(f) < 1e6: note = f'{f:g}'
            raw = d[o:o + 4]
            s = raw.decode('latin1') if printable(raw) else ''
            print(f'{o:05X}: {w:08X} {s:4} {note}')
