"""List call sites of a function with the last constant loaded into given arg register before the call.
python tools/probe/callargs.py <func va hex> [reg=4]"""
import sys, os
sys.path.insert(0, os.path.dirname(__file__))
import ppc
tgt = int(sys.argv[1], 16); reg = int(sys.argv[2]) if len(sys.argv) > 2 else 4
lo, hi = ppc.TEXT
for va in range(lo, hi, 4):
    w = ppc.u32(va)
    if w >> 26 == 18 and w & 3 == 1:
        d = w & 0x3FFFFFC
        if d & 0x2000000: d -= 0x4000000
        if va + d != tgt: continue
        val = '?'
        for k in range(1, 25):
            a = va - 4 * k; w2 = ppc.u32(a); op = w2 >> 26
            if (w2 >> 21) & 31 == reg and op in (14, 32, 34, 31, 21):
                val = ppc.dis1(a) + f' @{a:#x}'; break
            if op == 18 or (op == 19 and ((w2 >> 1) & 0x3FF) == 16): break
        f = ppc.func_of(va)
        print(f'{va:#x} in {f[0] if f else 0:#x}: r{reg} <- {val}')
