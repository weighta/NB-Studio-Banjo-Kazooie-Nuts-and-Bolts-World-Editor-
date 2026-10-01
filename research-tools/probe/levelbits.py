"""List code that reads level-settings bytes via [[x]+0xF4]+8 -> lbz off, and the bit mask tested next.
python levelbits.py"""
import sys, os
sys.path.insert(0, os.path.dirname(__file__))
import ppc
from collections import defaultdict
lo, hi = ppc.TEXT
res = defaultdict(list)
for va in range(lo, hi, 4):
    w = ppc.u32(va)
    if w >> 26 == 32 and w & 0xFFFF == 0xF4:
        r1 = (w >> 21) & 31
        w2 = ppc.u32(va + 4)
        if w2 >> 26 == 32 and w2 & 0xFFFF == 8 and (w2 >> 16) & 31 == r1:
            r2 = (w2 >> 21) & 31
            for k in range(2, 6):
                w3 = ppc.u32(va + 4 * k)
                if w3 >> 26 in (34, 32, 40) and (w3 >> 16) & 31 == r2:
                    off = w3 & 0xFFFF; rd = (w3 >> 21) & 31; mask = None
                    for j in range(1, 4):
                        w4 = ppc.u32(va + 4 * (k + j))
                        if w4 >> 26 == 21 and (w4 >> 21) & 31 == rd and ((w4 >> 6) & 31) == ((w4 >> 1) & 31) and (w4 >> 11) & 31 == 0:
                            mask = 1 << (31 - ((w4 >> 6) & 31)); break
                        if w4 >> 26 == 28 and (w4 >> 21) & 31 == rd: mask = w4 & 0xFFFF; break
                    f = ppc.func_of(va)
                    res[(off, mask)].append(f[0] if f else va)
                    break
for (off, mask), fs in sorted(res.items(), key=lambda x: (x[0][0], x[0][1] or 0)):
    print(f'+{off:#x} mask {mask if mask is None else hex(mask)}: {len(fs)}  ' + ' '.join(f'{x:#x}' for x in sorted(set(fs))[:10]))
