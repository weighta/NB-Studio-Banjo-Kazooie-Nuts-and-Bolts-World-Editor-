"""Find loads/stores with a given displacement. python fieldref.py <disp hex> [st|ld|all]"""
import sys, os
sys.path.insert(0, os.path.dirname(__file__))
import ppc
disp = int(sys.argv[1], 16); mode = sys.argv[2] if len(sys.argv) > 2 else 'all'
ST = {36, 38, 44, 52, 54, 37}; LD = {32, 34, 40, 42, 48, 50, 33}
lo, hi = ppc.TEXT
for va in range(lo, hi, 4):
    w = ppc.u32(va); op = w >> 26
    if (op in ST and mode in ('st', 'all')) or (op in LD and mode in ('ld', 'all')):
        if w & 0xFFFF == disp:
            f = ppc.func_of(va)
            print(f'{va:#x} {ppc.dis1(va):32s}' + (f' in {f[0]:#x}' if f else ''))
