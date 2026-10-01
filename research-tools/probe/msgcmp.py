"""Find compares against message ids (cmpwi/cmplwi rX,<id>) and report the functions, with the other ids compared
in the same function. python msgcmp.py <id> [<id2> ...]  (functions comparing ALL given ids are listed)"""
import sys, os, struct, bisect
sys.path.insert(0, os.path.dirname(__file__))
import ppc
ids = [int(a, 0) for a in sys.argv[1:]]
P = 0x821A2E00; PE = P + 0x3A478
funcs = []
for o in range(P, PE, 8):
    b, x = struct.unpack_from('>II', ppc.img, o - ppc.BASE)
    funcs.append((b, b + 4 * ((x >> 8) & 0x3FFFFF)))
funcs.sort(); starts = [f[0] for f in funcs]
def fof(va):
    i = bisect.bisect_right(starts, va) - 1
    return funcs[i][0] if i >= 0 and va < funcs[i][1] else None
from collections import defaultdict
cmps = defaultdict(list)
lo, hi = ppc.TEXT
for va in range(lo, hi, 4):
    w = ppc.u32(va); op = w >> 26
    if op in (10, 11):
        v = ppc.s16(w & 0xFFFF) if op == 11 else w & 0xFFFF
        f = fof(va)
        if f: cmps[f].append((va, v))
for f, lst in sorted(cmps.items()):
    vals = set(v for va, v in lst)
    if all(i in vals for i in ids):
        hits = [va for va, v in lst if v == ids[0]]
        others = sorted(set(v for va, v in lst if v not in ids and 0 < v < 338))
        print(f'{f:#x}: cmp {ids[0]} at ' + ' '.join(f'{h:#x}' for h in hits[:6]) + f'  other: {others[:25]}')
