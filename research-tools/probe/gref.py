"""Classify references to a global address: python tools/probe/gref.py <va hex> [st]
Lists every lis/(op) pair hitting the address and the access kind (lbz/stb/lwz/stw/addi...)."""
import sys, os
sys.path.insert(0, os.path.dirname(__file__))
import ppc
t = int(sys.argv[1], 16)
only_st = len(sys.argv) > 2 and sys.argv[2] == 'st'
names = {14: 'addi', 32: 'lwz', 34: 'lbz', 40: 'lhz', 48: 'lfs', 36: 'stw', 38: 'stb', 44: 'sth'}
for h in ppc.xref(t):
    op = ppc.u32(h) >> 26
    if only_st and op not in (36, 38, 44): continue
    f = ppc.func_of(h)
    print(f'{h:#010x} {names.get(op, op):5s} {ppc.dis1(h):28s} func {f[0]:#010x}' if f else f'{h:#010x} {names.get(op, op)} {ppc.dis1(h)}')
