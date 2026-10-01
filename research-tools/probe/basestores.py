"""Find stores into a global struct accessed through a base register.
python tools/probe/basestores.py <base va hex> [maxoff hex] [all]
For each lis/addi building <base>, follow the base register linearly (until redefinition / function end)
and print stores (stb/sth/stw/stfs) through it with offset <= maxoff (default 0x40). 'all' also prints loads."""
import sys, os
sys.path.insert(0, os.path.dirname(__file__))
import ppc
base = int(sys.argv[1], 16)
maxoff = int(sys.argv[2], 16) if len(sys.argv) > 2 else 0x40
show_all = 'all' in sys.argv
ST = {36: 'stw', 38: 'stb', 44: 'sth', 52: 'stfs', 37: 'stwu'}
LD = {32: 'lwz', 34: 'lbz', 40: 'lhz', 48: 'lfs'}
seen = set()
for h in ppc.xref(base):
    w = ppc.u32(h)
    if w >> 26 != 14: continue
    rb = (w >> 21) & 31
    f = ppc.func_of(h)
    end = f[0] + 4 * f[1] if f else h + 0x400
    va = h + 4
    while va < end:
        w = ppc.u32(va); op = w >> 26; rD = (w >> 21) & 31; rA = (w >> 16) & 31; d = ppc.s16(w & 0xFFFF)
        if op in ST and rA == rb and 0 <= d <= maxoff:
            if va not in seen:
                seen.add(va); print(f'{va:#010x} {ppc.dis1(va):28s} -> {base + d:#010x}  func {f[0] if f else 0:#010x}')
        elif show_all and op in LD and rA == rb and 0 <= d <= maxoff and va not in seen:
            seen.add(va); print(f'{va:#010x} {ppc.dis1(va):28s} -> {base + d:#010x}  func {f[0] if f else 0:#010x}')
        # redefinition of rb (not by store)
        if op not in ST and op not in (16, 18, 19, 10, 11) and rD == rb and op != 31: break
        if op == 31 and ((w >> 1) & 0x3FF) in (444, 266, 40, 23) and (((w >> 1) & 0x3FF) != 444 and rD == rb or ((w >> 1) & 0x3FF) == 444 and rA == rb): break
        if op == 18 and not (w & 1) and False: break
        va += 4
