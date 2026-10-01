"""List instructions whose 16-bit immediate equals one of the given values (li/addi/ori/D-form), grouped by function.
python tools/probe/immscan.py 0x610 0x620 ..."""
import sys, os, struct, bisect
sys.path.insert(0, os.path.dirname(__file__))
import ppc
vals = set(int(a, 0) for a in sys.argv[1:])
P = 0x821A2E00; PE = P + 0x3A478
funcs = sorted((b, b + 4 * ((x >> 8) & 0x3FFFFF)) for b, x in (struct.unpack_from('>II', ppc.img, o - ppc.BASE) for o in range(P, PE, 8)))
starts = [f[0] for f in funcs]
def fof(va):
    i = bisect.bisect_right(starts, va) - 1
    return funcs[i][0] if i >= 0 and va < funcs[i][1] else 0
lo, hi = ppc.TEXT
for va in range(lo, hi, 4):
    w = ppc.u32(va); op = w >> 26
    if op in (14, 24, 32, 34, 36, 38, 40, 44, 48, 52) and (w & 0xFFFF) in vals:
        print(f'{va:#x} in {fof(va):#x}: {ppc.dis1(va)}')
