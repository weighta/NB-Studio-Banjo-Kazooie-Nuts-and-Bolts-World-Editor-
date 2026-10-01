"""Find functions containing both li rX,1024 and li rX,250 (hkpWorldCinfo ctor candidates)."""
import sys, os
sys.path.insert(0, os.path.dirname(__file__))
import ppc
from collections import defaultdict
lo, hi = ppc.TEXT
hits = defaultdict(set)
for va in range(lo, hi, 4):
    w = ppc.u32(va)
    if w >> 26 == 14 and (w >> 16) & 31 == 0:
        v = w & 0xFFFF
        if v in (0x400, 0xfa):
            f = ppc.func_of(va)
            if f: hits[f[0]].add(v)
for f, s in hits.items():
    if len(s) == 2: print(hex(f))
