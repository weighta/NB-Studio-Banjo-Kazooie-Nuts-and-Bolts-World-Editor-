"""Which blocksets (aid_misc_banjox_blockset_*, garage_* lists) contain each vehicle part.
Blockset .data = pairs (u32 objparams id, u32 count). python tools/probe/partsets.py [part substring ...]"""
import sys, json, zlib, struct, os
sys.path.insert(0, 'tools/probe')
from caff import Caff
from asset import Asset
def cid(name, t=0x1F): return (t << 24) | (~zlib.crc32(name[len('aid_objparams_'):].encode()) & 0xFFFFFF)
blocks = [l.strip() for l in open('work/allblocks.txt')]
by24 = {cid(b) & 0xFFFFFF: b for b in blocks}
idx = json.load(open('Workspaces/dev/cache/assetindex.json')); idx = idx['Entries'] if isinstance(idx, dict) else idx
sets = {}
cache = {}
for e in idx:
    n = e['Name']
    if e['Streamed'] or not ('blockset' in n or 'garage_' in n and 'blocks' in n): continue
    b = e['Bundle']
    p = os.path.join('work/decomp/4f', '%06x' % b)
    if not os.path.exists(p): continue
    if b not in cache: cache[b] = Caff(open(p, 'rb').read())
    try: d = Asset(cache[b], e['Symbol']).data()
    except Exception: continue
    items = []
    for o in range(0, len(d) - 7, 8):
        c, v = struct.unpack_from('>II', d, o)   # (count, objparams id)
        if (v & 0xFFFFFF) in by24: items.append((by24[v & 0xFFFFFF], c))
    sets[n] = items
want = sys.argv[1:] or ['olympictorch', 'secondaryseats_grunty', 'remotecontrol', 'autopilot']
for w in want:
    for x in [b for b in blocks if w in b]:
        where = [(n.replace('aid_misc_banjox_', ''), c) for n, it in sets.items() for (bb, c) in it if bb == x]
        print(x.replace('aid_objparams_banjox_vehicleblock_', ''), '->', where[:8] or 'IN NO BLOCKSET', f'({len(where)} sets)')
print(len(sets), 'blocksets scanned;', sum(1 for v in sets.values() if v), 'with parts')
