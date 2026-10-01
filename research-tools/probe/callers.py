"""List bl call sites to a function. python callers.py <va> [...]"""
import sys, os
sys.path.insert(0, os.path.dirname(__file__))
import ppc
lo, hi = ppc.TEXT
targets = {int(a, 16): a for a in sys.argv[1:]}
for va in range(lo, hi, 4):
    w = ppc.u32(va)
    if w >> 26 == 18 and w & 3 == 1:
        d = w & 0x3FFFFFC
        if d & 0x2000000: d -= 0x4000000
        if va + d in targets:
            f = ppc.func_of(va)
            print(f'{targets[va + d]} <- {va:#x}' + (f' in {f[0]:#x} (+{va - f[0]:#x})' if f else ''))
