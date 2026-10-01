"""Find every bit test on the current level's settings object ([[[0x82FAD9F0]+0xF4]+8]+0xD0..0xDF).
Pattern: lwz rA,0xF4(rX) ; lwz rB,0x8(rA) ; lbz rC,0xDx(rB) ; rlwinm(.) rD,rC,0,mb,me  (within a few instructions)."""
import sys, os, collections
sys.path.insert(0, os.path.dirname(__file__))
import ppc

def fields(w):
    return w >> 26, (w >> 21) & 31, (w >> 16) & 31, w & 0xFFFF

hits = collections.defaultdict(list)
for va in range(ppc.TEXT[0], ppc.TEXT[1], 4):
    w = ppc.u32(va); op, rd, ra, imm = fields(w)
    if op != 34 or not (0xD0 <= imm <= 0xDF): continue          # lbz rd, 0xD?(ra)
    # look back for lwz ra,0x8(rb) and lwz rb,0xF4(..)
    ok = False
    for k in range(1, 8):
        o2, d2, a2, i2 = fields(ppc.u32(va - 4 * k))
        if o2 == 32 and d2 == ra and i2 == 8:
            for j in range(1, 6):
                o3, d3, a3, i3 = fields(ppc.u32(va - 4 * k - 4 * j))
                if o3 == 32 and d3 == a2 and i3 == 0xF4: ok = True; break
            break
    if not ok: continue
    # look ahead for the mask
    mask = None
    for k in range(1, 5):
        w4 = ppc.u32(va + 4 * k); o4, s4, a4, _ = fields(w4)
        if o4 == 21 and s4 == rd:
            mb, me = (w4 >> 6) & 31, (w4 >> 1) & 31
            mask = sum(1 << (31 - b) for b in range(mb, me + 1)) & 0xFF if mb <= me else None
            break
        if o4 == 28 and s4 == rd: mask = w4 & 0xFF; break   # andi.
    f = ppc.func_of(va)
    hits[(imm, mask)].append((va, f[0] if f else 0))

for (off, mask), L in sorted(hits.items(), key=lambda kv: (kv[0][0], kv[0][1] or 0)):
    funcs = sorted(set(fn for _, fn in L))
    print(f'+0x{off:02X} mask {("0x%02X" % mask) if mask is not None else "?":>5}: {len(L)} use(s) in {len(funcs)} function(s): ' + ', '.join(f'{fn:#010x}' for fn in funcs[:8]))
