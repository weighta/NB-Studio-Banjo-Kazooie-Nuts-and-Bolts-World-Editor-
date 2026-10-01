"""Find code building a 32-bit constant (lis+ori / lis+addi) and raw data words equal to it.
python const32.py <hex> [...]"""
import sys, os, struct
sys.path.insert(0, os.path.dirname(__file__))
import ppc

for a in sys.argv[1:]:
    t = int(a, 16)
    for va in ppc.xref(t):
        f = ppc.func_of(va)
        print(f'{a}: code at {va:#x}' + (f' in {f[0]:#x}' if f else ''))
    # lis/ori form (xref handles addi-style hi); also check ori with hi = t>>16
    hi, lo = t >> 16, t & 0xFFFF
    for va in range(ppc.TEXT[0], ppc.TEXT[1], 4):
        w = ppc.u32(va)
        if w >> 26 == 15 and (w >> 16) & 31 == 0 and w & 0xFFFF == hi:
            rd = (w >> 21) & 31
            for k in range(1, 10):
                w2 = ppc.u32(va + 4 * k)
                if w2 >> 26 == 24 and (w2 >> 21) & 31 == rd and w2 & 0xFFFF == lo:
                    f = ppc.func_of(va)
                    print(f'{a}: lis/ori at {va + 4 * k:#x}' + (f' in {f[0]:#x}' if f else ''))
                    break
    b = struct.pack('>I', t); o = ppc.img.find(b)
    while o >= 0:
        if o % 4 == 0: print(f'{a}: data word at {ppc.BASE + o:#x}')
        o = ppc.img.find(b, o + 1)
