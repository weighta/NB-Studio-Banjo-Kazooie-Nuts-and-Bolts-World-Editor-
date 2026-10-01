"""Find code that loads the game object ([0x82FAC7AC] = 0x82FAC650+0x15C) and compares its +0x58 field."""
import sys, os, collections
sys.path.insert(0, os.path.dirname(__file__))
import ppc

def F(w): return w >> 26, (w >> 21) & 31, (w >> 16) & 31, w & 0xFFFF

res = collections.defaultdict(list)
for va in range(ppc.TEXT[0], ppc.TEXT[1], 4):
    op, rd, ra, imm = F(ppc.u32(va))
    if op != 32 or imm != 0x58: continue                      # lwz rd,0x58(ra)
    # was ra loaded from 0x82FAC7AC (lis 0x82FB + lwz -0x3854) or (addi 0x82FAC650 then lwz 0x15C)?
    src = False
    for k in range(1, 14):
        o2, d2, a2, i2 = F(ppc.u32(va - 4 * k))
        if o2 == 32 and d2 == ra and (i2 == 0xC7AC or i2 == 0x15C): src = True; break
        if d2 == ra and o2 not in (36, 38, 44, 16, 11, 10): break
    if not src: continue
    for k in range(1, 5):
        o3, d3, a3, i3 = F(ppc.u32(va + 4 * k))
        if o3 in (11, 10) and a3 == rd:
            f = ppc.func_of(va); res[ppc.s16(i3)].append((va, f[0] if f else 0)); break

for val, L in sorted(res.items()):
    fs = sorted(set(f for _, f in L))
    print(f'+0x58 compared with {val}: {len(L)} site(s) in {len(fs)} function(s): ' + ', '.join(f'{f:#x}' for f in fs))
