"""List string constants referenced by functions (lis/addi pairs) — quick semantic hints.
python tools/probe/funcstrings.py <func va hex> [...]"""
import sys, os
sys.path.insert(0, os.path.dirname(__file__))
import ppc

for a in sys.argv[1:]:
    f = ppc.func_of(int(a, 16))
    if not f: print(a, 'not a function'); continue
    start, n = f
    regs = {}; strs = []; calls = []
    for i in range(n):
        va = start + 4 * i; w = ppc.u32(va); op = w >> 26
        rD = (w >> 21) & 31; rA = (w >> 16) & 31; imm = w & 0xFFFF
        if op == 15 and rA == 0: regs[rD] = imm << 16
        elif op == 14 and rA in regs and rA:
            v = (regs[rA] + ppc.s16(imm)) & 0xFFFFFFFF
            s = ppc.cstr(v) if 0x82000000 <= v < 0x82000000 + len(ppc.img) else None
            if s and len(s) > 3: strs.append(s)
        elif op == 18 and w & 1:
            li = w & 0x3FFFFFC
            if li & 0x2000000: li -= 0x4000000
            calls.append((va + li) & 0xFFFFFFFF)
    print(f'{start:#010x} ({n} instr): strings {strs[:12]}  calls {len(calls)}')
