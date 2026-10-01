"""Find instructions using a 16-bit immediate (li/addi/cmpwi/cmplwi/subfic/mulli/addic/ori/andi.).
python immsearch.py <value> [lo hi] [--ops li,cmp,...]   value decimal or 0x..; lo/hi hex VA range (default .text)"""
import sys, os
sys.path.insert(0, os.path.dirname(__file__))
import ppc

args = [a for a in sys.argv[1:] if not a.startswith('--')]
val = int(args[0], 0)
lo, hi = (int(args[1], 16), int(args[2], 16)) if len(args) >= 3 else ppc.TEXT
OPS = {14: 'addi', 11: 'cmpi', 10: 'cmpli', 8: 'subfic', 7: 'mulli', 12: 'addic', 13: 'addic.', 24: 'ori', 28: 'andi.'}
for va in range(lo, hi, 4):
    w = ppc.u32(va); op = w >> 26
    if op not in OPS: continue
    imm = w & 0xFFFF
    v = imm if op in (10, 24, 28) else ppc.s16(imm)
    if v == val:
        f = ppc.func_of(va)
        print(f'{va:#x} {ppc.dis1(va):34s}' + (f' in {f[0]:#x}' if f else ''))
