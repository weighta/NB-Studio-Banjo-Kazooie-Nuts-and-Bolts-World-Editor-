"""List functions (.pdata) in a VA range with referenced strings and interesting compare immediates.
python funcmap.py <lo hex> <hi hex>"""
import sys, os, struct
sys.path.insert(0, os.path.dirname(__file__))
import ppc

lo, hi = int(sys.argv[1], 16), int(sys.argv[2], 16)
p, pe = 0x821A2E00, 0x821A2E00 + 0x3A478
funcs = []
for o in range(p, pe, 8):
    b, x = struct.unpack_from('>II', ppc.img, o - ppc.BASE)
    n = (x >> 8) & 0x3FFFFF
    if lo <= b < hi: funcs.append((b, n))
funcs.sort()
for start, n in funcs:
    regs = {}; strs = []; cmps = []
    for i in range(n):
        va = start + 4 * i; w = ppc.u32(va); op = w >> 26
        rD = (w >> 21) & 31; rA = (w >> 16) & 31; imm = w & 0xFFFF
        if op == 15 and rA == 0: regs[rD] = imm << 16
        elif op == 14 and rA in regs and rA:
            v = (regs[rA] + ppc.s16(imm)) & 0xFFFFFFFF
            s = ppc.cstr(v) if 0x82000000 <= v < 0x82000000 + len(ppc.img) else None
            if s and len(s) > 3: strs.append(s[:50])
        elif op in (10, 11):
            v = imm if op == 10 else ppc.s16(imm)
            if v not in (0, 1, -1): cmps.append(v)
    print(f'{start:#010x} {n:5d}  cmp{sorted(set(cmps))[:14]} {strs[:6]}')
