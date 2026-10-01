"""Walk the .stream command lists of a model: python cmds.py <caff> <model>"""
import sys, struct, collections
sys.path.insert(0, 'tools/probe')
from model import load
from caff import u32
a = load(sys.argv[1], sys.argv[2])
s = a.data('.stream'); sp = a.pid('.stream')
ops = collections.Counter()
o = 0
limit = int(sys.argv[3]) if len(sys.argv) > 3 else 200
lines = 0
while o + 4 <= len(s):
    w = u32(s, o)
    size, op, lo = w >> 16, (w >> 8) & 0xFF, w & 0xFF
    if lo != 0 or size < 4 or size > 0x400 or o + size > len(s):
        if lines < limit: print('%05x: raw %08x %s' % (o, w, a.ptr(o, '.stream') or ''))
        o += 4; lines += 1; continue
    args = []
    for k in range(4, size, 4):
        p = a.ptr(o + k, '.stream')
        v = u32(s, o + k)
        args.append(('%s+%x' % (p[0], p[1])) if p else ('%x' % v))
    ops[op] += 1
    if lines < limit: print('%05x: op %02x size %3x  %s' % (o, op, size, ' '.join(args[:14])))
    lines += 1
    o += size
print(ops)
