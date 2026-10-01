"""List functions in a VA range with float-constant loads (lfs from lis base), sqrt and compare counts.
python floatscan.py <start> <end> [--consts]"""
import sys, os, struct
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ppc import u32, dis1, func_of, BASE, img
a0 = int(sys.argv[1], 16); a1 = int(sys.argv[2], 16); show = '--consts' in sys.argv
va = a0
while va < a1:
    f = func_of(va)
    if not f: va += 4; continue
    b, n = f
    regs = {}; consts = []; sq = cmp = 0
    for i in range(n):
        x = b + 4 * i; w = u32(x); op = w >> 26; rD = (w >> 21) & 31; rA = (w >> 16) & 31; imm = w & 0xFFFF
        if op == 15 and rA == 0: regs[rD] = imm << 16
        elif op == 48 and rA in regs:
            c = (regs[rA] + (imm - 0x10000 if imm & 0x8000 else imm)) & 0xFFFFFFFF
            fv = struct.unpack('>f', img[c - BASE:c - BASE + 4])[0] if BASE <= c < BASE + len(img) else None
            consts.append((x, c, fv))
        t = dis1(x)
        if 'sqrt' in t: sq += 1
        if t.startswith('fcmp') or t.startswith('vcmpg'): cmp += 1
    print(f'{b:#010x} n={n:4d} sqrt={sq} cmp={cmp} consts={len(consts)}' + ((' ' + ' '.join(f'{c:#x}={fv:g}' for _, c, fv in consts)) if show else ''))
    va = b + 4 * n
