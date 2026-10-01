"""Dump name[64]+u32 id records of the gameassetref assets in the common bundle: python tools/probe/gameassetref.py"""
import sys,struct,re
sys.path.insert(0,'tools/probe')
from caff import Caff
from asset import Asset, find
c=Caff(open('work/decomp/4f/685374','rb').read())
for s in find(c,'gameassetref_default')+find(c,'gameassetref_demo'):
    a=Asset(c,s); d=a.data()
    print('==', a.name, len(d), d[:16].hex())
    # find records name[64]+id
    for m in re.finditer(rb'[a-z0-9_]{3,63}\x00', d):
        o=m.start()
        if o>=4 and (o-0)%4==0:
            pass
    # assume header then records of 0x44
    if len(d) < 0x44: print('  raw', d.hex()); continue
    o=16 if len(d)>16 else 0
    # scan records aligned 0x44 starting where first name begins
    first=re.search(rb'[a-z_]{3}',d).start()
    for o in range(first, len(d)-0x43, 0x44):
        name=d[o:o+64].split(b'\0')[0].decode('latin1'); i=struct.unpack_from('>I',d,o+64)[0]
        print(f'  {o:#06x} {name:45s} {i:#010x}')
