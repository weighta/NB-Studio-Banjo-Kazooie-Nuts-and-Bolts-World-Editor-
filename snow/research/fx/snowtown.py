"""snowtown.py <workspace dir> [--mode follow|grid] [--xenia-patch <dir>] [--src <decompressed 1592bd>] [options]

Adds falling snow to Showdown Town (world bundle Bundle/4f/234cec) in an NB Mod Tool workspace.

What it does (idempotent: re-running replaces what an earlier run added):
 1. takes the two Banjoland snow particle records aid_gpuparticleeffect_common_snowfall1/2 (gpuparticleeffect, type
    0x45, 0x184 bytes, self-contained: only reference = texture aid_texture_banjox_particles_glowoffset, which lives in
    the always-loaded common bundle 685374) from Banjoland's resident bundle 1592bd,
 2. adds tuned copies to 234cec as aid_gpuparticleeffect_banjox_snowtown1/2 and a composite effect
    aid_compositeeffect_banjox_snowtown (type 0x4B, 16 bytes: {u32 4, u32 gpu id} x 2) and registers them in the manifest,
 3. adds type-30 "effect" marker records (0x38 bytes) to aid_marker_banjox_showdowntown_main (before the closing
    type-0 record),
 4. --mode follow (default): marks the particle records with 0x534E at +0x180 and writes a Xenia patch file
    (--xenia-patch <dir>) whose hook copies the camera position (0x82FADC30) into the emitter every frame, so one emitter
    snows around the camera anywhere in town.
    --mode grid: no exe patch; a grid of large static emitters covers the town (much sparser, see REPORT.md).

Options: --emit1 N --emit2 N (particles/s, record +0x13C) --max N (+0x10) --life a,b --size1 k --size2 k --box R --ylo Y --yhi Y (box relative to the emitter)
         --markers N (follow mode: number of overlapping emitters, default 1)  --grid-step S --grid-y Y (grid mode)
Needs: Python 3, this folder's caffio.py; NB.Cli xdecomp when --src is not given.
"""
import sys, os, struct, subprocess, tempfile, argparse
HERE = os.path.dirname(os.path.abspath(__file__)); ROOT = os.path.abspath(os.path.join(HERE, '..', '..', '..'))
sys.path.insert(0, HERE)
from caffio import Caff, asset_id, u32, f32

TOWN = '234cec'; BANJOLAND = '1592bd'
MARKERS = 'aid_marker_banjox_showdowntown_main'
SRC1, SRC2 = 'aid_gpuparticleeffect_common_snowfall1', 'aid_gpuparticleeffect_common_snowfall2'
GPU1, GPU2 = 'aid_gpuparticleeffect_banjox_snowtown1', 'aid_gpuparticleeffect_banjox_snowtown2'
COMP = 'aid_compositeeffect_banjox_snowtown'
MAGIC = 0x534E          # record +0x180 (always 0 in all 507 shipped particle records): "follow the camera"

# ---- exe patch (camera-follow) -------------------------------------------------------------------------------------
HOOK, BACK, CAVE = 0x82226ECC, 0x82226ED0, 0x82D21340   # cave: zero padding after the first .embsec_ (free 0x82D21330..0x82D21400)
CAM = 0x82FADC30                                        # camera matrix translation row (x, y, z, 1)
def _b(at, tgt): return (18 << 26) | ((tgt - at) & 0x03FFFFFC)
def _bne6(at, tgt): return (16 << 26) | (4 << 21) | (26 << 16) | ((tgt - at) & 0xFFFC)
def _lwz(d, off, a): return (32 << 26) | (d << 21) | (a << 16) | (off & 0xFFFF)
def _stw(s, off, a): return (36 << 26) | (s << 21) | (a << 16) | (off & 0xFFFF)
def _addi(d, a, s): return (14 << 26) | (d << 21) | (a << 16) | (s & 0xFFFF)
def _lis(d, imm): return (15 << 26) | (d << 21) | (imm & 0xFFFF)
def _cmplwi6(a, imm): return (10 << 26) | (6 << 23) | (a << 16) | (imm & 0xFFFF)
def patch_words():
    hi = (CAM + 0x8000) >> 16; lo = CAM - (hi << 16)
    prog = [
        (_lwz(11, 0x180, 8), 'lwz r11,0x180(r8)        particle record +0x180'),
        (_cmplwi6(11, MAGIC), f'cmplwi cr6,r11,{MAGIC:#x}  follow-camera marker?'),
        ('bne', 'bne cr6 -> original'),
        (_lis(11, hi), f'lis r11,{hi:#x}'),
        (_lwz(9, lo, 11), f'lwz r9,{lo:#x}(r11)        camera x [{CAM:#x}]'),
        (_stw(9, 0xA0, 10), 'stw r9,0xa0(r10)          emitter node position x'),
        (_lwz(9, lo + 4, 11), 'lwz r9  camera y'),
        (_stw(9, 0xA4, 10), 'stw r9,0xa4(r10)'),
        (_lwz(9, lo + 8, 11), 'lwz r9  camera z'),
        (_stw(9, 0xA8, 10), 'stw r9,0xa8(r10)'),
        (_addi(9, 29, 0x40), 'addi r9,r29,0x40          restore r9 (instance +0x40)'),
        (_addi(11, 0, 160), 'li r11,160                restore r11'),
        (0x100A58C3, 'lvx128 v0,r10,r11         (the hooked instruction: load node +0xA0)'),
        ('b', f'b {BACK:#x}'),
    ]
    words = []
    for i, (w, c) in enumerate(prog):
        at = CAVE + 4 * i
        if w == 'bne': w = _bne6(at, CAVE + 4 * 11)
        elif w == 'b': w = _b(at, BACK)
        words.append((at, 0, w, c))
    assert CAVE + 4 * len(words) <= 0x82D21400
    return [(HOOK, 0x100A58C3, _b(HOOK, CAVE), f'b {CAVE:#x} (was lvx128 v0,r10,r11): snow emitters follow the camera')] + words

def write_xenia_patch(d):
    os.makedirs(d, exist_ok=True)
    p = os.path.join(d, '4D5307ED - Snowy Showdown Town.patch.toml')
    s = ('# Written by snow/research/fx/snowtown.py\ntitle_name = "Banjo-Kazooie: Nuts & Bolts"\ntitle_id = "4D5307ED"\n'
         'hash = "C03916823ADAC91B"\n\n[[patch]]\n    name = "Snow follows the camera"\n'
         '    desc = "GPU particle update 0x82226bd0 copies the emitter node position (node +0xA0) into the instance (+0x40) every frame (0x82226EC4..ED0). For particle records marked 0x534E at +0x180 the node position is first set to the camera position (0x82FADC30)."\n'
         '    author = "NB Mod Tool"\n    is_enabled = true\n')
    for a, orig, w, c in patch_words():
        s += f'\n    [[patch.be32]]\n        address = {a:#010x}\n        value = {w:#010x} # was {orig:#010x}: {c}\n'
    open(p, 'w', encoding='utf-8').write(s)
    return p

# ---- CAFF edits ----------------------------------------------------------------------------------------------------
def sym_of(c, name):
    for i, s in enumerate(c.symbols):
        if s.split(',')[0] == name: return i + 1
    return 0
def data_part(c, sym):
    return [(pid, p) for pid, p in c.parts_of(sym) if c.secname(p) == '.data'][0]
def manifest_add(c, sym):
    ms = c.symbols.index('manifest') + 1
    pid, part = data_part(c, ms); d = bytes(part.data)
    assert u32(d, 0) == 0x438CB47C
    ep, cnt, dp, dc = (struct.unpack_from('>i', d, o)[0] for o in (8, 12, 16, 20))
    aid = asset_id(c.symbols[sym - 1])
    for i in range(cnt):
        if u32(d, ep + 8 * i) == aid: return
    nd = bytearray(d[:0x20]) + d[ep:ep + 8 * cnt] + struct.pack('>II', aid, sym - 1) + d[dp:dp + 4 * dc]
    struct.pack_into('>iii', nd, 8, 0x20, cnt + 1, 0x20 + 8 * (cnt + 1))
    part.data = bytearray(nd)
def add_or_replace(c, name, data):
    """add a self-contained .data-only asset (inserted before 'manifest'), or replace its data when it exists"""
    s = sym_of(c, name)
    if s:
        data_part(c, s)[1].data = bytearray(data); return s
    ms = c.symbols.index('manifest')
    assert ms == len(c.symbols) - 1
    c.symbols.insert(ms, name); ns = ms + 1
    for p in c.parts:
        if p.symbol >= ns: p.symbol += 1
    from caffio import Part
    dsec = [i + 1 for i, s in enumerate(c.sections) if s['name'] == '.data'][0]
    p = Part(); p.symbol = ns; p.section = dsec; p.alignlog2 = 4; p.off = 0; p.size = len(data); p.data = bytearray(data)
    c.parts.append(p)
    manifest_add(c, ns)
    return ns

def tune(rec, emit, life, size_k, box, ylo, yhi, magic, maxp=None):
    d = bytearray(rec)
    if maxp: struct.pack_into('>I', d, 0x10, maxp)            # particle buffer size (vanilla 8000)
    struct.pack_into('>f', d, 0x13C, emit)                    # emission rate, particles per second (vanilla 20 / 30)
    struct.pack_into('>ff', d, 0x124, *life)                  # lifetime min/max (s)
    for o in (0xB8, 0xBC, 0xC0, 0xC4): struct.pack_into('>f', d, o, f32(d, o) * size_k)   # size start/end min/max
    struct.pack_into('>3f', d, 0x78, -box, ylo, -box)         # emitter box min (relative to the emitter)
    struct.pack_into('>3f', d, 0x84, box, yhi, box)           # emitter box max
    struct.pack_into('>I', d, 0x180, MAGIC if magic else 0)
    return bytes(d)

def set_markers(c, positions, eff_id):
    s = sym_of(c, MARKERS); pid, p = data_part(c, s)
    d = bytes(p.data); recs = []; o = 0
    while o + 0x30 <= len(d):
        sz = u32(d, o)
        if sz < 0x30: break
        recs.append(d[o:o + sz]); o += sz
    assert o == len(d) and u32(recs[-1], 4) >> 16 == 0
    ids = {asset_id(COMP), 0x4B97312C}
    recs = [r for r in recs if not (u32(r, 4) >> 16 == 30 and u32(r, 0x34) in ids)]   # drop earlier snow markers
    nxt = max(u32(r, 4) & 0xFFFF for r in recs[:-1]) + 1
    new = []
    for (x, y, z) in positions:
        new.append(struct.pack('>IHHIII3f3ffII', 0x38, 30, nxt, 0, 0, 0, x, y, z, 0, 0, 0, 1.0, 0, eff_id)); nxt += 1
    p.data = bytearray(b''.join(recs[:-1] + new + recs[-1:]))
    return len(new)

LIGHTSETUPS = ['aid_script_banjox_lightsetup_showdowntown_' + t for t in ('main', 'morning', 'afternoon', 'night')]
def set_fog(c, start, end, dens, rgb):
    """town light-setup scripts, command 0x53 (76 bytes; = fog): +8 u32 1, +0xC start, +0x10 end, +0x14 max density,
    +0x24 colour RGBx. One per time of day (main = midday)."""
    n = 0
    for name in LIGHTSETUPS:
        s = sym_of(c, name)
        if not s: continue
        pid, p = data_part(c, s); d = p.data; o = u32(d, 0)
        while o + 8 <= len(d):
            sz, op = u32(d, o), u32(d, o + 4)
            if op == 0x53:
                struct.pack_into('>fff', d, o + 0xC, start, end, dens)
                struct.pack_into('>I', d, o + 0x24, (rgb << 8) & 0xFFFFFF00); n += 1
            if op == 0 or sz < 8: break
            o += sz
    return n

def main():
    if '--exewords' in sys.argv:   # C# lines for NB.Core/Mods/ExePatches.cs (ExeWord(address, original, value, comment))
        for a, orig, w, c in patch_words():
            print(f'            new ExeWord(0x{a:08X}, 0x{orig:08X}, 0x{w:08X}, "{c.strip()}"),')
        return
    ap = argparse.ArgumentParser()
    ap.add_argument('ws'); ap.add_argument('--mode', default='follow', choices=['follow', 'grid'])
    ap.add_argument('--xenia-patch'); ap.add_argument('--src')
    ap.add_argument('--emit1', type=float, default=None); ap.add_argument('--emit2', type=float, default=None)
    ap.add_argument('--max', type=int, default=None)
    ap.add_argument('--life', default=None); ap.add_argument('--size1', type=float, default=2.5); ap.add_argument('--size2', type=float, default=2.5)
    ap.add_argument('--box', type=float, default=None); ap.add_argument('--ylo', type=float, default=None); ap.add_argument('--yhi', type=float, default=None)
    ap.add_argument('--fog', default=None, help='start,end,maxdensity,RRGGBB: snowy fog in all 4 town light setups')
    ap.add_argument('--rawbox', default=None, help='x0,y0,z0,x1,y1,z1 written verbatim to record +0x78/+0x84')
    ap.add_argument('--markers', type=int, default=1); ap.add_argument('--grid-step', type=float, default=200); ap.add_argument('--grid-y', type=float, default=40)
    a = ap.parse_args()
    follow = a.mode == 'follow'
    # defaults per mode: follow = dense volume around the camera; grid = 200x200 emitters 70 up
    # follow defaults = run D (verified heavy snowfall): 1200 + 250 particles/s in a 120 x 120 box 15..35 above the camera
    box = a.box if a.box is not None else (60.0 if follow else a.grid_step / 2)
    ylo = a.ylo if a.ylo is not None else (15.0 if follow else -2.0)
    yhi = a.yhi if a.yhi is not None else (35.0 if follow else 2.0)
    life = tuple(map(float, a.life.split(','))) if a.life else ((5.0, 7.0) if follow else (14.0, 18.0))
    rate1 = a.emit1 or (1200.0 if follow else 450.0); rate2 = a.emit2 or (250.0 if follow else 100.0)
    if a.max is None: a.max = 10000 if follow else 9000

    town = os.path.join(a.ws, 'game', 'Bundle', '4f', TOWN)
    d = open(town, 'rb').read()
    if d[:4] != b'CAFF': sys.exit(f'{town} is not an uncompressed CAFF (decompress it with NB.Cli xdecomp first)')
    src = a.src
    if not src:
        src = os.path.join(tempfile.gettempdir(), 'snowtown_1592bd.caff')
        if not os.path.exists(src):
            subprocess.run([os.path.join(ROOT, 'NBModTool', 'src', 'NB.Cli', 'bin', 'Release', 'net9.0-windows', 'NB.Cli.exe'), 'xdecomp',
                            os.path.join(a.ws, 'game', 'Bundle', '4f', BANJOLAND), src], check=True)
    b = Caff.read(open(src, 'rb').read())
    r1 = bytes(data_part(b, sym_of(b, SRC1))[1].data); r2 = bytes(data_part(b, sym_of(b, SRC2))[1].data)
    assert len(r1) == len(r2) == 0x184
    c = Caff.read(d)
    g1 = tune(r1, rate1, life, a.size1, box, ylo, yhi, follow, a.max)
    g2 = tune(r2, rate2, life, a.size2, box, ylo, yhi, follow, a.max)
    if a.rawbox:
        bb = struct.pack('>6f', *map(float, a.rawbox.split(',')))
        g1 = g1[:0x78] + bb + g1[0x90:]; g2 = g2[:0x78] + bb + g2[0x90:]
    for g in (g1, g2):
        if struct.unpack_from('>f', g, 0x13C)[0] * life[1] > struct.unpack_from('>I', g, 0x10)[0]:
            print('  WARNING: emission x max lifetime exceeds the particle buffer (+0x10); raise --max')
    add_or_replace(c, GPU1, g1); add_or_replace(c, GPU2, g2)
    comp = struct.pack('>IIII', 4, asset_id(GPU1), 4, asset_id(GPU2))
    add_or_replace(c, COMP, comp)
    if follow:
        pos = [(-10.0, 40.0, 306.0)] * a.markers
    else:
        st = a.grid_step; pos = []
        x = -400 + st / 2
        while x < 400:
            z = -400 + st / 2
            while z < 500: pos.append((x, a.grid_y, z)); z += st
            x += st
    n = set_markers(c, pos, asset_id(COMP))
    if a.fog:
        f = a.fog.split(','); print('  fog: patched', set_fog(c, float(f[0]), float(f[1]), float(f[2]), int(f[3], 16)), 'light setups')
    open(town, 'wb').write(c.write())
    print(f'{town}: {GPU1} {asset_id(GPU1):08X}, {GPU2} {asset_id(GPU2):08X}, {COMP} {asset_id(COMP):08X}; {n} marker(s); mode {a.mode}')
    print(f'  emit {rate1}/{rate2}/s life {life} size x{a.size1}/x{a.size2} box +-{box} y {ylo}..{yhi}')
    if follow:
        if a.xenia_patch: print('  xenia patch:', write_xenia_patch(a.xenia_patch))
        else: print('  NOTE: follow mode needs the exe patch (--xenia-patch <xenia patches dir>)')

if __name__ == '__main__': main()
