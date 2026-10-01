import struct,sys
def u32(d,o): return struct.unpack_from('>I',d,o)[0]
def cstr(d,o):
    e=d.index(b'\0',o); return d[o:e].decode('latin1')
def checksum(data):
    h=0
    for b in data:
        v = (0xFFFFFF80|b) if b&0x80 else b
        h=(v + ((h<<4)&0xffffffff))&0xffffffff
        t=h&0xF0000000
        if t: h ^= (t | (t>>24))
    return h
class Caff:
    def __init__(s,d):
        s.d=d; assert d[:4]==b'CAFF'
        s.ver=d[4:0x14].rstrip(b'\0').decode()
        s.hdrsize=u32(d,0x14); s.cksum=u32(d,0x18); s.nsym=u32(d,0x1c); s.nparts=u32(d,0x20)
        s.h=[u32(d,o) for o in range(0x14,0x78,4)]
        s.type=d[0x48]; s.nsec=d[0x49]; s.b4a=d[0x4a]; s.b4b=d[0x4b]
        s.infosize=u32(d,0x50); s.unksize=u32(d,0x64)
        o=s.hdrsize; s.secs=[]
        for i in range(s.nsec):
            r=d[o:o+0x21]; s.secs.append(dict(nameoff=u32(r,0),align=r[4],flag=u32(r,5),size=u32(r,9),mid=r[13:29],size2=u32(r,29))); o+=0x21
        nb=o
        for sec in s.secs: sec['name']=cstr(d,nb+sec['nameoff'])
        o=nb+sum(len(x['name'])+1 for x in s.secs)
        s.symbufsize=u32(d,o); o+=4
        offs=[u32(d,o+4*i) for i in range(s.nsym)]; o+=4*s.nsym
        s.symstart=o
        s.syms=[cstr(d,o+x) for x in offs]
        o+=s.symbufsize
        s.after_syms=o
        n=u32(d,o); s.extraname=d[o+4:o+4+n]; o+=4+n
        s.parts=[]
        # entries: id, off, size, sec, unk
        for i in range(s.nparts):
            pid,off,size=struct.unpack_from('>III',d,o); sec=d[o+12]; unk=d[o+13]; s.parts.append((pid,off,size,sec,unk)); o+=14
        s.after_parts=o
        s.infoend=s.hdrsize+s.infosize
        s.dataStart=s.infoend+s.unksize
        pos=s.dataStart
        for sec in s.secs: sec['start']=pos; pos+=sec['size']
        s.end=pos
    def hdr_checksum_ok(s):
        h=bytearray(s.d[:s.hdrsize]); h[0x18:0x1c]=b'\0\0\0\0'; return checksum(h)==s.cksum
if __name__=='__main__':
    d=open(sys.argv[1],'rb').read(); c=Caff(d)
    print('ver',c.ver,'hdr',hex(c.hdrsize),'nsym',c.nsym,'nparts',c.nparts,'type',c.type,'nsec',c.nsec,'b4a',c.b4a,'b4b',c.b4b,'cksum_ok',c.hdr_checksum_ok())
    print('hdr words', [hex(x) for x in c.h])
    for s in c.secs: print('  sec',s['name'],'align',s['align'],'flag',s['flag'],'size',hex(s['size']),'size2',hex(s['size2']),'start',hex(s['start']),s['mid'].hex())
    print('after_syms',hex(c.after_syms),'after_parts',hex(c.after_parts),'infoend',hex(c.infoend),'dataStart',hex(c.dataStart),'end',hex(c.end),'filelen',hex(len(d)))
    for i,x in enumerate(c.syms[:int(sys.argv[2]) if len(sys.argv)>2 else 10]): print('  sym',i+1,x)
    for p in c.parts[:int(sys.argv[3]) if len(sys.argv)>3 else 10]: print('  part id=%d off=%x size=%x sec=%d unk=%d'%p)
