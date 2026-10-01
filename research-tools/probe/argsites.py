"""For each bl call to <target>, report the last 'li rN,imm' for a given arg register before the call.
python argsites.py <target hex> <reg number> [lookback]"""
import sys, os
sys.path.insert(0, os.path.dirname(__file__))
import ppc

tgt = int(sys.argv[1], 16); reg = int(sys.argv[2]); back = int(sys.argv[3]) if len(sys.argv) > 3 else 12
lo, hi = ppc.TEXT
for va in range(lo, hi, 4):
    w = ppc.u32(va)
    if w >> 26 == 18 and w & 3 == 1:
        d = w & 0x3FFFFFC
        if d & 0x2000000: d -= 0x4000000
        if va + d != tgt: continue
        val = '?'
        for k in range(1, back + 1):
            p = va - 4 * k; w2 = ppc.u32(p)
            if w2 >> 26 == 14 and (w2 >> 16) & 31 == 0 and (w2 >> 21) & 31 == reg:
                val = ppc.s16(w2 & 0xFFFF); break
            if w2 >> 26 == 18 or (w2 >> 26 == 19 and ((w2 >> 1) & 0x3FF) in (16, 528)): break
            if (w2 >> 21) & 31 == reg and w2 >> 26 not in (36, 37, 38, 44, 52, 54, 62, 16, 10, 11): val = 'reg:' + ppc.dis1(p); break
        f = ppc.func_of(va)
        print(f'{va:#x} r{reg}={val}' + (f'  in {f[0]:#x}' if f else ''))
