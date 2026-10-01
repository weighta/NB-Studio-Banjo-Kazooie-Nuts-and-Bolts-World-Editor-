"""Find lfs/lwz/lfd loads that resolve to a given data address, tracking lis and lis+addi base registers per function.
python constref.py <va hex> [...]"""
import sys, os, struct
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ppc import u32, dis1, img, BASE
targets = {int(a, 16) for a in sys.argv[1:]}
P0, P1 = 0x821A2E00, 0x821A2E00 + 0x3A478
def s16(x): return x - 0x10000 if x & 0x8000 else x
hits = []
for o in range(P0, P1, 8):
    b, x = struct.unpack_from('>II', img, o - BASE); n = (x >> 8) & 0x3FFFFF
    regs = {}
    for i in range(n):
        va = b + 4 * i; w = u32(va); op = w >> 26; rD = (w >> 21) & 31; rA = (w >> 16) & 31; imm = w & 0xFFFF
        if op == 15 and rA == 0: regs[rD] = imm << 16; continue
        if op == 14 and rA in regs and rA != 0:
            regs[rD] = (regs[rA] + s16(imm)) & 0xFFFFFFFF; continue
        if op in (48, 50, 32) and rA in regs and rA != 0:
            a = (regs[rA] + s16(imm)) & 0xFFFFFFFF
            if a in targets: hits.append((va, b, dis1(va), a))
            if op == 32 and rD in regs: regs.pop(rD)
            continue
        # do not invalidate on most ops (base regs are usually callee-saved); invalidate if written by other ALU ops
        if op in (14, 15, 24, 31, 21, 32, 34, 40, 7, 12, 13) and rD in regs and op not in (31,):
            regs.pop(rD, None)
for va, b, t, a in hits: print(f'{va:#010x} in {b:#010x}: {t}  -> {a:#010x}')
print(len(hits), 'hits')
