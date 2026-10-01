"""Find instructions with a given 16-bit immediate (addi/li/lwz/stw/lfs/...) in a VA range.
python immfind.py <imm hex> [start end]"""
import sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ppc import u32, dis1, func_of, TEXT
imm = int(sys.argv[1], 16) & 0xFFFF
a0 = int(sys.argv[2], 16) if len(sys.argv) > 2 else TEXT[0]; a1 = int(sys.argv[3], 16) if len(sys.argv) > 3 else TEXT[1]
for va in range(a0, a1, 4):
    w = u32(va); op = w >> 26
    if op in (14, 32, 34, 36, 38, 40, 44, 48, 50, 52, 54, 24) and (w & 0xFFFF) == imm:
        f = func_of(va)
        print(f'{va:#010x} {dis1(va):40s} in {f[0]:#010x}' if f else f'{va:#010x} {dis1(va)}')
