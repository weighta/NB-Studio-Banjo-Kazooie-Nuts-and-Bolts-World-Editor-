"""Functions that compare some +0x58 field with 1 and also reference the game object globals (0x82FAC650 / 0x82FAC7AC)."""
import sys, os, struct
sys.path.insert(0, os.path.dirname(__file__))
import ppc

def F(w): return w >> 26, (w >> 21) & 31, (w >> 16) & 31, w & 0xFFFF
# iterate functions from .pdata
funcs = []
for o in range(0x821A2E00, 0x821A2E00 + 0x3A478, 8):
    b, x = struct.unpack_from('>II', ppc.img, o - ppc.BASE)
    n = (x >> 8) & 0x3FFFFF
    if ppc.TEXT[0] <= b < ppc.TEXT[1] and n: funcs.append((b, n))
out = []
for b, n in funcs:
    has58 = []; hasG = False; regs = {}
    for i in range(n):
        va = b + 4 * i; w = ppc.u32(va); op, rd, ra, imm = F(w)
        if op == 15 and ra == 0: regs[rd] = imm << 16
        elif op in (14, 32) and ra in regs and ra:
            v = (regs[ra] + ppc.s16(imm)) & 0xFFFFFFFF
            if v in (0x82FAC650, 0x82FAC7AC): hasG = True
        if op == 32 and imm == 0x58:
            for k in range(1, 4):
                o3, d3, a3, i3 = F(ppc.u32(va + 4 * k))
                if o3 == 11 and a3 == rd and i3 == 1: has58.append(va); break
    if has58 and hasG: out.append((b, n, has58))
for b, n, h in out:
    print(f'{b:#010x} ({n} instr): +0x58==1 at ' + ', '.join(f'{x:#x}' for x in h))
print(len(out), 'functions')
