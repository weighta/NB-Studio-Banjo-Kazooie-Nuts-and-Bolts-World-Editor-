import sys, os, struct, collections
sys.path.insert(0, 'tools/probe')
from caff import Caff
stats = []
for n in sorted(os.listdir('work/decomp/4f')):
    c = Caff(open('work/decomp/4f/' + n, 'rb').read())
    d = c.d
    b = c.infoend; nt = c.h[6]; no = c.h[7]
    o = b + 12 * nt + 4 * no
    rem = b + c.unksize - o
    stats.append((n, rem, c.h[12], d[0x4a], d[0x4b], c.h[14], c.nsym, c.nparts, c.nsec))
for s in stats:
    print('%s rem=%6d h44=%5d b4a=%d b4b=%d h4c=%4d nsym=%5d nparts=%5d nsec=%d  rem/16=%.2f' % (s + (s[1] / 16,)))
