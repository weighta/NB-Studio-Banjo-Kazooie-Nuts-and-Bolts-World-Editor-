"""Which vehicle parts appear in which part lists (garage_* / unlockable block sets), by objparams id.
python tools/probe/partlists.py [part substring ...]"""
import sys, glob, zlib, struct
sys.path.insert(0, 'tools/probe')
from caff import Caff
from asset import Asset
def crc(s): return zlib.crc32(s.encode()) & 0xFFFFFF
blocks = [l.strip() for l in open('work/allblocks.txt')]
ids = {crc(b[len('aid_objparams_'):]): b for b in blocks}
lists = {}
for f in glob.glob('work/decomp/4f/*'):
    d = open(f, 'rb').read()
    if b'garage_' not in d and b'unlockable' not in d and b'blockset' not in d: continue
    try: c = Caff(d)
    except Exception: continue
    for k, s in enumerate(c.syms):
        if not any(t in s for t in ('garage_', 'unlockablelist_', 'blockset', 'blocks_')): continue
        try: dd = Asset(c, k + 1).data()
        except Exception: continue
        found = set()
        for o in range(0, len(dd) - 3, 4):
            v = struct.unpack_from('>I', dd, o)[0]
            if (v >> 24) in (0x09, 0x0A, 0x0B, 0x08, 0x19, 0x1A) and (v & 0xFFFFFF) in ids: found.add(ids[v & 0xFFFFFF])
        if found: lists.setdefault(s.split(chr(92))[-1][:70], set()).update(found)
want = sys.argv[1:] or ['olympictorch', 'secondaryseats_grunty', 'remotecontrol', 'autopilot']
for w in want:
    b = [x for x in blocks if w in x]
    for x in b:
        where = [n for n, s in lists.items() if x in s]
        print(x.replace('aid_objparams_banjox_vehicleblock_', ''), '->', where or 'IN NO PART LIST')
print(len(lists), 'lists:', sorted(lists)[:40])
