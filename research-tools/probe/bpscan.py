"""Heuristic: find functions that walk 0x24-byte blueprint block records (addi rX,rY,0x24 / mulli 36)
together with a 0x7C header offset (addi rX,rY,0x7c) - candidates for blueprint (aid_vehicle) read/write code.
python tools/probe/bpscan.py [lo hi]"""
import sys, os, struct
sys.path.insert(0, os.path.dirname(__file__))
import ppc

lo, hi = (int(sys.argv[1], 16), int(sys.argv[2], 16)) if len(sys.argv) > 2 else ppc.TEXT
p, pe = 0x821A2E00, 0x821A2E00 + 0x3A478
for o in range(p, pe, 8):
    b, x = struct.unpack_from('>II', ppc.img, o - ppc.BASE)
    n = (x >> 8) & 0x3FFFFF
    if not (lo <= b < hi): continue
    s24 = s7c = stb = lhz0 = sth0 = 0
    for i in range(n):
        w = ppc.u32(b + 4 * i); op = w >> 26; imm = w & 0xFFFF
        if (op == 14 and imm == 0x24 and (w >> 16) & 31) or (op == 7 and imm == 36): s24 += 1
        if op == 14 and imm == 0x7C and (w >> 16) & 31 != 1: s7c += 1
        if op == 38: stb += 1
        if op == 44 and imm == 0: sth0 += 1
        if op == 40 and imm == 0: lhz0 += 1
    if s24 and s7c:
        print(f'{b:#010x} n={n:5d} 0x24:{s24} 0x7c:{s7c} stb:{stb} sth0:{sth0} lhz0:{lhz0}')
