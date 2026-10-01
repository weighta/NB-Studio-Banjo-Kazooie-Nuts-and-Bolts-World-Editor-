"""Find where objMsgs are constructed: calls to the msg ctor 0x82241888 (r4 = msg id) and li rX,<id> near them.
python msgsites.py <id> [<id>...]   (ids decimal or 0x..)"""
import struct, sys, os
sys.path.insert(0, os.path.dirname(__file__))
import ppc
img = ppc.img; B = ppc.BASE
CTOR = 0x82241888
ids = [int(a, 0) for a in sys.argv[1:]]
lo, hi = ppc.TEXT
calls = []
for va in range(lo, hi, 4):
    w = ppc.u32(va)
    if w >> 26 == 18 and w & 3 == 1:
        d = w & 0x3FFFFFC
        if d & 0x2000000: d -= 0x4000000
        if va + d == CTOR: calls.append(va)
print(len(calls), 'ctor calls')
for c in calls:
    # scan back up to 12 instrs for li r4,imm
    for k in range(1, 16):
        w = ppc.u32(c - 4 * k)
        if w >> 26 == 14 and (w >> 16) & 31 == 0 and (w >> 21) & 31 == 4:
            v = ppc.s16(w & 0xFFFF)
            if v in ids:
                f = ppc.func_of(c)
                print(f'id {v} at {c:#x} in func {f[0]:#x}' if f else f'id {v} at {c:#x}')
            break
