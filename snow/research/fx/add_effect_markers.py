"""add_effect_markers.py <bundle file (raw or decompressed CAFF)> <marker asset name> [options] POS [POS ...]
Insert type-30 "effect" marker records (0x38 bytes) before the asset's closing type-0 record and write the CAFF back
(uncompressed). POS = x,y,z[,rotX,rotY,rotZ[,scale]] (radians).

Record layout (from aid_marker_banjox_banjoland_global, snowmachine1):
  +00 u32 size 0x38  +04 u16 type 30  +06 u16 index (asset-global, sequential)  +08 u32 0  +0C u32 marker-set mask (0 = always)
  +10 u32 0  +14 float3 pos  +20 float3 rot (rad)  +2C float scale  +30 u32 0  +34 u32 effect id (compositeeffect 4B... or
  gpuparticleeffect 45...)
Options: --effect <hex id> (default 4B97312C = aid_compositeeffect_banjox_snowfall1)  --mask <hex>  --strip (remove all
type-30 records first)  --grid x0,x1,z0,z1,step,y  (adds a grid of positions)  --out <file> (default: in place)
"""
import sys, os, struct
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from caffio import Caff, u32, f32

def main(a):
    path, asset = a[0], a[1]
    eff, mask, strip, out, pos = 0x4B97312C, 0, False, path, []
    i = 2
    while i < len(a):
        x = a[i]
        if x == '--effect': eff = int(a[i+1], 16); i += 2
        elif x == '--mask': mask = int(a[i+1], 16); i += 2
        elif x == '--strip': strip = True; i += 1
        elif x == '--out': out = a[i+1]; i += 2
        elif x == '--grid':
            x0, x1, z0, z1, st, y = map(float, a[i+1].split(',')); i += 2
            xx = x0
            while xx <= x1 + 1e-3:
                zz = z0
                while zz <= z1 + 1e-3: pos.append([xx, y, zz]); zz += st
                xx += st
        else: pos.append([float(v) for v in x.split(',')]); i += 1
    c = Caff.read(open(path, 'rb').read())
    sym = [k+1 for k, s in enumerate(c.symbols) if s.split(',')[0] == asset]
    if not sym: sys.exit(f'{asset} not found')
    pid, p = [(q, pp) for q, pp in c.parts_of(sym[0]) if c.secname(pp) == '.data'][0]
    assert not c.relocs_from(pid), 'marker asset with relocations?'
    d = bytes(p.data); recs = []; o = 0
    while o + 0x30 <= len(d):
        sz = u32(d, o)
        if sz < 0x30: break
        recs.append(d[o:o+sz]); o += sz
    assert o == len(d), 'marker parse'
    assert u32(recs[-1], 4) >> 16 == 0, 'last record is not the closing type-0 record'
    if strip:
        n0 = len(recs); recs = [r for r in recs if u32(r, 4) >> 16 != 30]; print('stripped', n0 - len(recs), 'type-30 records')
    nxt = max(u32(r, 4) & 0xFFFF for r in recs[:-1]) + 1
    new = []
    for q in pos:
        x, y, z = q[:3]; rx, ry, rz = (q[3:6] + [0, 0, 0])[:3] if len(q) > 3 else (0, 0, 0); sc = q[6] if len(q) > 6 else 1.0
        r = struct.pack('>IHHIII3f3ffII', 0x38, 30, nxt, 0, mask, 0, x, y, z, rx, ry, rz, sc, 0, eff)
        assert len(r) == 0x38
        new.append(r); nxt += 1
    recs = recs[:-1] + new + recs[-1:]
    p.data = bytearray(b''.join(recs))
    open(out, 'wb').write(c.write())
    print(f'{asset}: +{len(new)} type-30 records (effect {eff:08X}, mask {mask:08X}); {len(recs)} records; wrote {out}')

if __name__ == '__main__': main(sys.argv[1:])
