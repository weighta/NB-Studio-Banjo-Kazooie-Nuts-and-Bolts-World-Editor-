"""Which asset/section of a CAFF contains a file offset: python tools/probe/caffwhere.py <caff file> <offset hex>..."""
import sys
sys.path.insert(0,'tools/probe')
from caff import Caff
c=Caff(open(sys.argv[1],'rb').read())
for off in [int(x,16) for x in sys.argv[2:]]:
    for pid,p in enumerate(c.parts,1):
        sec=c.secs[p[3]-1]
        a=sec['start']+p[1]
        if a<=off<a+p[2]:
            print(hex(off), c.syms[p[0]-1], sec['name'], 'rel', hex(off-a), 'size', hex(p[2]))
