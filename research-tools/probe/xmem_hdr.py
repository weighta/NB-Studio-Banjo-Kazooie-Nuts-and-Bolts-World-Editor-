import struct,sys,os
def parse(path):
    d=open(path,'rb').read()
    magic,ver,res,crc,flags=struct.unpack('>IHHII',d[:16])
    segs=(flags>>6)&0xffff; bps=[20,32,0,0][(flags>>22)&3]; cbs=0x8000<<((flags>>4)&3); win=1<<((flags&0xf)+15)
    nbits=20+(12 if flags&0xc00000 else 0); twords=(nbits*segs+31)>>5
    tbl=d[16:16+twords*4]
    # read sizes as MSB-first bitstream
    bits=int.from_bytes(tbl,'big'); tot=twords*32
    sizes=[(bits>>(tot-(i+1)*bps))&((1<<bps)-1) for i in range(segs)]
    return d,dict(ver=ver,crc=crc,flags=flags,segs=segs,bps=bps,cbs=cbs,win=win,tbl_end=16+twords*4),sizes
if __name__=='__main__':
    for p in sys.argv[1:]:
        d,h,s=parse(p)
        print(os.path.basename(p),len(d),{k:hex(v) for k,v in h.items()},'sum',sum(s),'max',max(s),'expected_file_segs',(len(d)+h['cbs']-1)//h['cbs'])
        print('  sizes',s[:8])
        te=h['tbl_end']; print('  seg0 data:',d[te:te+24].hex()); print('  seg1 data:',d[h['cbs']:h['cbs']+24].hex())
