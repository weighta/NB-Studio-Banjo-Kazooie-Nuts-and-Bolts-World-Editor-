"""Standalone CAFF read/write (port of NB.Core/Formats/Caff.cs) for the snow FX research.
Caff.read(bytes) -> Caff; c.write() -> bytes (byte-identical round trip expected)."""
import struct, zlib

def u32(d, o): return struct.unpack_from('>I', d, o)[0]
def s32(d, o): return struct.unpack_from('>i', d, o)[0]
def f32(d, o): return struct.unpack_from('>f', d, o)[0]
def cstr(d, o):
    e = d.index(b'\0', o); return d[o:e].decode('latin1')
def align(x, a): return (x + a - 1) // a * a

def crc_noinv(s):
    return (~zlib.crc32(s.encode('latin1')) & 0xFFFFFFFF) ^ 0  # placeholder replaced below

def hash24(name):
    # standard reflected CRC-32, init 0xFFFFFFFF, no final xor
    c = zlib.crc32(name.encode('latin1')) ^ 0xFFFFFFFF
    return c & 0xFFFFFF

TYPES = {'vehicle':0x00,'texture':0x01,'anim':0x02,'model':0x04,'havok':0x05,'animevents':0x06,'misc':0x0B,'marker':0x0D,
         'script':0x19,'fxemitter':0x1B,'fxparticle':0x1C,'objparams':0x1F,'vertexshader':0x41,'pixelshader':0x42,
         'gpuparticleeffect':0x45,'3dgpuparticleeffect':0x4A,'compositeeffect':0x4B,'ddstexture':0x4D,'fxcamshake':0x1E,'fxrumble':0x1D}

def asset_id(sym):
    """id of a symbol like aid_<type>_<rest> (textures use D:\\LocalLibrary\\BanjoX\\<name>\\default.rtx)"""
    s = sym.split(',')[0]
    if '(' in s and s.endswith(')'): s = s[:s.rindex('(')]
    if s.lower().startswith('d:\\locallibrary'):
        nm = s.split('\\')[-2]
        return 0x01 << 24 | hash24(nm)
    if not s.startswith('aid_'): return None
    rest = s[4:]
    for t in sorted(TYPES, key=len, reverse=True):
        if rest.startswith(t + '_'):
            return TYPES[t] << 24 | hash24(rest[len(t)+1:])
    return None

class Part:
    __slots__ = ('symbol','section','alignlog2','off','size','data')
class Reloc:
    def __init__(s, frm, to, offs, table=0): s.frm, s.to, s.offs, s.table = frm, to, list(offs), table

class Caff:
    HS = 0x78
    @staticmethod
    def read(d):
        assert d[:4] == b'CAFF'
        c = Caff(); c.header = bytearray(d[:0x78]); c.version = cstr(d, 4)
        nsym, nparts = s32(d,0x1c), s32(d,0x20)
        ng, no, npool = s32(d,0x2c), s32(d,0x30), s32(d,0x44)
        ng2, no2 = s32(d,0x3c), s32(d,0x40)
        nsec, npp = d[0x49], d[0x4b]
        infosize, relocsize = s32(d,0x50), s32(d,0x64)
        p = 0x78; c.sections = []; nameoffs = []
        for i in range(nsec):
            nameoffs.append(s32(d,p))
            c.sections.append(dict(align=d[p+4], flags=u32(d,p+5), size=s32(d,p+9), mid=bytes(d[p+13:p+29]), size2=s32(d,p+29)))
            p += 0x21
        ns = p
        for i,s in enumerate(c.sections): s['name'] = cstr(d, ns+nameoffs[i])
        p = ns + s32(d,0x4c)
        sbs = s32(d,p); p += 4
        offs = [s32(d,p+4*i) for i in range(nsym)]; p += 4*nsym
        c.symbols = [cstr(d,p+o) for o in offs]; p += sbs
        n = s32(d,p); c.extra = bytes(d[p+4:p+4+n]); p += 4+n
        c.parts = []
        for i in range(nparts):
            pt = Part(); pt.symbol, pt.off, pt.size = struct.unpack_from('>iii', d, p); pt.section = d[p+12]; pt.alignlog2 = d[p+13]
            c.parts.append(pt); p += 14
        p = 0x78 + infosize; rs = p
        c.relocs = []
        for n_, tb in ((ng,0),(ng2,1)):
            gs = [struct.unpack_from('>iii', d, p+12*i) for i in range(n_)]; p += 12*n_
            for f,t,cnt in gs:
                c.relocs.append(Reloc(f,t,[s32(d,p+4*k) for k in range(cnt)],tb)); p += 4*cnt
        c.poolparts = [struct.unpack_from('>ii', d, p+8*i) for i in range(npp)]; p += 8*npp
        c.poolrecs = bytes(d[p:p+16*npool]); p += 16*npool
        assert p == rs + relocsize, 'reloc size'
        pos = p; ss = []
        for s in c.sections: ss.append(pos); pos += s['size']
        assert pos == len(d), 'section sizes'
        for pt in c.parts: pt.data = bytearray(d[ss[pt.section-1]+pt.off: ss[pt.section-1]+pt.off+pt.size])
        return c

    def sym_index(self, name):
        """1-based symbol index of a display name (exact symbol or aid name)"""
        for i,s in enumerate(self.symbols):
            if s == name: return i+1
        return 0
    def parts_of(self, sym): return [(i+1,p) for i,p in enumerate(self.parts) if p.symbol == sym]
    def secname(self, p): return self.sections[p.section-1]['name']
    def relocs_from(self, pid): return [r for r in self.relocs if r.frm == pid]
    def relocs_to(self, pid): return [r for r in self.relocs if r.to == pid]

    def write(self):
        nsec = len(self.sections); secsize = [0]*nsec; offs = []
        for p in self.parts:
            s = p.section-1; o = align(secsize[s], 1 << p.alignlog2); offs.append(o); secsize[s] = o + len(p.data)
        secsize = [align(x,4) for x in secsize]
        out = bytearray(0x78)
        names = bytearray(); nameoffs = []
        for s in self.sections: nameoffs.append(len(names)); names += s['name'].encode('latin1') + b'\0'
        for i,s in enumerate(self.sections):
            out += struct.pack('>iBIi', nameoffs[i], s['align'], s['flags'], secsize[i]) + s['mid'] + struct.pack('>i', secsize[i])
        out += names
        sb = bytearray(); so = []
        for s in self.symbols: so.append(len(sb)); sb += s.encode('latin1') + b'\0'
        out += struct.pack('>i', len(sb)) + b''.join(struct.pack('>i',o) for o in so) + sb
        out += struct.pack('>i', len(self.extra)) + self.extra
        for i,p in enumerate(self.parts):
            out += struct.pack('>iiiBB', p.symbol, offs[i], len(p.data), p.section, p.alignlog2)
        while len(out) % 4: out += b'\0'
        infosize = len(out) - 0x78; rs = len(out)
        for t in (0,1):
            for g in self.relocs:
                if g.table == t: out += struct.pack('>iii', g.frm, g.to, len(g.offs))
            for g in self.relocs:
                if g.table == t: out += b''.join(struct.pack('>i',o) for o in g.offs)
        for a,b in self.poolparts: out += struct.pack('>ii', a, b)
        out += self.poolrecs
        relocsize = len(out) - rs
        ds = len(out); sstart = []; pos = ds
        for i in range(nsec): sstart.append(pos); pos += secsize[i]
        out += bytes(pos - len(out))
        for i,p in enumerate(self.parts):
            o = sstart[p.section-1] + offs[i]; out[o:o+len(p.data)] = p.data
        h = bytearray(self.header)
        h[0:4] = b'CAFF'; h[4:0x14] = bytes(16); v = self.version.encode(); h[4:4+len(v)] = v
        def w(o,v): struct.pack_into('>I', h, o, v & 0xFFFFFFFF)
        w(0x14,0x78); w(0x1c,len(self.symbols)); w(0x20,len(self.parts))
        w(0x2c, sum(1 for r in self.relocs if r.table==0)); w(0x30, sum(len(r.offs) for r in self.relocs if r.table==0))
        w(0x3c, sum(1 for r in self.relocs if r.table==1)); w(0x40, sum(len(r.offs) for r in self.relocs if r.table==1))
        w(0x44, len(self.poolrecs)//16); h[0x49] = nsec; h[0x4b] = len(self.poolparts)
        w(0x4c, len(names)); w(0x50, infosize); w(0x60, infosize); w(0x64, relocsize); w(0x74, relocsize)
        w(0x18, 0); w(0x18, checksum(h))
        out[0:0x78] = h
        for i,p in enumerate(self.parts): p.off = offs[i]; p.size = len(p.data)
        for i,s in enumerate(self.sections): s['size'] = s['size2'] = secsize[i]
        self.header = h
        return bytes(out)

def checksum(data):
    h = 0
    for b in data:
        v = (0xFFFFFF80 | b) if b & 0x80 else b
        h = (v + ((h << 4) & 0xffffffff)) & 0xffffffff
        t = h & 0xF0000000
        if t: h ^= (t | (t >> 24))
    return h

if __name__ == '__main__':
    import sys, time
    t = time.time(); d = open(sys.argv[1],'rb').read(); c = Caff.read(d)
    print('read', len(c.symbols), 'syms', len(c.parts), 'parts', len(c.relocs), 'relocs', '%.1fs'%(time.time()-t))
    w = c.write(); print('roundtrip identical:', w == d)
