"""Find all loads/stores that can hit a global address (direct lis+op or via lis+addi base register).
python tools/probe/gaccess.py <va hex> [size]   (env GWIN=n widens the tracking window)"""
import sys, os
sys.path.insert(0, os.path.dirname(__file__))
import ppc
t = int(sys.argv[1], 16); size = int(sys.argv[2]) if len(sys.argv) > 2 else 1
ST = {36: "stw", 38: "stb", 44: "sth", 52: "stfs", 37: "stwu", 54: "stfd", 32: "lwz", 34: "lbz", 40: "lhz", 48: "lfs"}
W = {36: 4, 38: 1, 44: 2, 52: 4, 37: 4, 54: 8, 32: 4, 34: 1, 40: 2, 48: 4}
hi = None
T0, T1 = ppc.TEXT
for va in range(T0, T1, 4):
    w = ppc.u32(va)
    if w >> 26 != 15 or (w >> 16) & 31: continue
    rd = (w >> 21) & 31; hv = (w & 0xFFFF) << 16
    if not (hv - 0x8000 <= t < hv + 0x8000 + 0x2000): continue
    regs = {rd: hv}
    for k in range(1, int(os.environ.get("GWIN", "40"))):
        a = va + 4 * k
        w2 = ppc.u32(a); op = w2 >> 26; rD = (w2 >> 21) & 31; rA = (w2 >> 16) & 31; d = ppc.s16(w2 & 0xFFFF)
        if op in ST and rA in regs:
            addr = (regs[rA] + d) & 0xFFFFFFFF
            if addr <= t < addr + W[op] or t <= addr < t + size:
                f = ppc.func_of(a)
                print(f'{a:#010x} {ppc.dis1(a):28s} addr {addr:#010x} func {f[0] if f else 0:#010x}')
        if op == 14 and rA in regs:
            regs[rD] = (regs[rA] + d) & 0xFFFFFFFF; continue
        if (op == 18 and os.environ.get("GWIN") is None) or (op == 19 and ((w2 >> 1) & 0x3FF) == 16): break
        if op not in (36,38,44,52,37,54) and op not in (16, 10, 11) and rD in regs: regs.pop(rD)
        if not regs: break
