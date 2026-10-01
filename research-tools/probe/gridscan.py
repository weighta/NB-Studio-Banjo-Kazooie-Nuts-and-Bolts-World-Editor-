"""Find functions that load all three of +0x2C/+0x30/+0x34 from the same base register (vehicle block
container size fields) - candidates for code that indexes/allocates a grid sized by the vehicle extents.
python tools/probe/gridscan.py [lo hi]"""
import sys, os, struct
sys.path.insert(0, os.path.dirname(__file__))
import ppc

lo, hi = (int(sys.argv[1], 16), int(sys.argv[2], 16)) if len(sys.argv) > 2 else ppc.TEXT
p, pe = 0x821A2E00, 0x821A2E00 + 0x3A478
for o in range(p, pe, 8):
    b, x = struct.unpack_from('>II', ppc.img, o - ppc.BASE)
    n = (x >> 8) & 0x3FFFFF
    if not (lo <= b < hi): continue
    seen = {}
    for i in range(n):
        w = ppc.u32(b + 4 * i); op = w >> 26
        if op == 32 and (w & 0xFFFF) in (0x2C, 0x30, 0x34, 0x38):
            ra = (w >> 16) & 31
            seen.setdefault(ra, set()).add(w & 0xFFFF)
    for ra, s in seen.items():
        if {0x2C, 0x30, 0x34} <= s:
            print(f'{b:#010x} n={n} base r{ra} fields {sorted(hex(v) for v in s)}')
