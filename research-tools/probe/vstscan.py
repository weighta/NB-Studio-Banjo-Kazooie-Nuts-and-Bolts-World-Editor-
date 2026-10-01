"""Find vector/indexed stores (stvx128/stvx/stvlx/stvrx/stwx/stfsx) whose index or base register was set by
li/addi to one of the given immediates within the previous N instructions.
python tools/probe/vstscan.py <imm> [<imm> ...]"""
import sys, os
sys.path.insert(0, os.path.dirname(__file__))
import ppc
vals = set(int(a, 0) for a in sys.argv[1:])
lo, hi = ppc.TEXT
N = 24
def is_vstore(w):
    d = ppc.dis1(0) if False else None
    return None
for va in range(lo, hi, 4):
    w = ppc.u32(va)
    t = ppc.dis1(va)
    if not t.split(' ')[0] in ('stvx128', 'stvx', 'stvlx', 'stvrx', 'stvlx128', 'stvrx128', 'stwx', 'stfsx', 'stvewx', 'stvewx128'): continue
    rA = (w >> 16) & 31; rB = (w >> 11) & 31
    for k in range(1, N):
        p = ppc.u32(va - 4 * k)
        if p >> 26 == 14 and (p >> 21) & 31 in (rA, rB) and (p & 0xFFFF) in vals:
            print(f'{va:#x}: {t:30s} <- {va - 4 * k:#x} {ppc.dis1(va - 4 * k)}')
            break
