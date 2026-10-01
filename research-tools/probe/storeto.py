"""Heuristic: find stores (stb/sth/stw) whose displacement d makes base = target - d a global address that the code
builds anywhere with lis+addi. python tools/probe/storeto.py <target va hex> [width=1]"""
import sys, os, collections
sys.path.insert(0, os.path.dirname(__file__))
import ppc
T = int(sys.argv[1], 16)
lo, hi = ppc.TEXT
consts = collections.Counter()
for va in range(lo, hi, 4):
    w = ppc.u32(va)
    if w >> 26 == 15 and (w >> 16) & 31 == 0:
        rd = (w >> 21) & 31; hv = (w & 0xFFFF) << 16
        for k in range(1, 12):
            w2 = ppc.u32(va + 4 * k)
            if w2 >> 26 == 14 and (w2 >> 16) & 31 == rd:
                consts[(hv + ppc.s16(w2 & 0xFFFF)) & 0xFFFFFFFF] += 1; break
ST = {38: 'stb'}
for va in range(lo, hi, 4):
    w = ppc.u32(va); op = w >> 26
    if op in ST:
        d = ppc.s16(w & 0xFFFF); base = (T - d) & 0xFFFFFFFF
        if base in consts and d != 0 and (w >> 16) & 31 != 1 and (d < 0 or consts[base] >= 3):
            f = ppc.func_of(va)
            print(f'{va:#x} {ppc.dis1(va):28s} base? {base:#010x} (built {consts[base]}x)' + (f' in {f[0]:#x}' if f else ''))
