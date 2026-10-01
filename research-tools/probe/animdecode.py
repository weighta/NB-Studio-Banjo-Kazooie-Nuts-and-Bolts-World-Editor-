"""Decode Banjo-Kazooie: Nuts & Bolts 'animation' assets (QUAT_BITSTREAM / BITSTREAM keyframe codec).

Format and evidence: docs/research/16_anim_codec.md.

Usage
  python tools/probe/animdecode.py <anim.bin> [--track N] [--frames] [--stats]
  python tools/probe/animdecode.py --bundle 685374 --name wrencharoundinto [--track N] [--stats]
  python tools/probe/animdecode.py --bundle 685374 --all            (stats over every anim in the bundle)
  python tools/probe/animdecode.py --bundle all                     (every anim in every cached bundle)
  python tools/probe/animdecode.py --bundle 685374 --chain attacks_wrencharoundinto attacks_wrencharound

<anim.bin> is the raw .data section of an aid_anim_* asset (e.g. work/anim_banjo_wrencharoundinto.bin).
--bundle takes the 6-hex bundle id of Workspaces/dev/cache/4f/<id> (a raw CAFF).
"""
import sys, os, struct, math, argparse

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))


def u8(d, o): return d[o]
def u16(d, o): return struct.unpack_from('>H', d, o)[0]
def s16(d, o): return struct.unpack_from('>h', d, o)[0]
def u32(d, o): return struct.unpack_from('>I', d, o)[0]
def s32(d, o): return struct.unpack_from('>i', d, o)[0]
def f32(d, o): return struct.unpack_from('>f', d, o)[0]
def cstr(d, o, n=0x16): return d[o:o + n].split(b'\0')[0].decode('latin1')


class BitReader:
    """Mirror of 0x829a1538: 32-bit big-endian words, bits consumed LSB-first.
    A byte address a starts at word (a & ~3), bit (a & 3) * 8 (the stream is a little-endian
    byte bitstream that was byte-swapped per 32-bit word for the Xbox 360)."""
    def __init__(self, d, addr):
        self.d = d; self.w = addr & ~3; self.off = (addr & 3) * 8

    def read(self, n):
        d = self.d
        w0 = u32(d, self.w) if self.w + 4 <= len(d) else 0
        w1 = u32(d, self.w + 4) if self.w + 8 <= len(d) else 0
        v = ((w0 >> self.off) | ((w1 << 1) << (31 - self.off))) & ((1 << n) - 1)
        self.off += n
        self.w += (self.off >> 5) * 4
        self.off &= 31
        return v


class Codec:
    """One codec block: char name[0x16]; u16 type (runtime); u32 data -> descriptor D."""
    def __init__(self, d, off):
        self.off = off
        self.name = cstr(d, off)
        self.data = u32(d, off + 0x18)
        self.tracks = []          # [track][key][channel] floats (decoded)
        self.info = {}
        if not self.data:
            return
        D = self.data
        i = self.info
        i['f0'] = f32(d, D)                      # unknown (1.0 in all samples)
        i['scales'] = u32(d, D + 4)               # f32[nch]  quantisation step per channel
        i['defaults'] = u32(d, D + 8)             # f32[nch]  value when the channel is not stored
        i['flags'] = u32(d, D + 0xC)              # u8 per 3-channel group
        i['bases'] = u32(d, D + 0x10)             # s16 (s32 if wide) per stored channel
        i['widths'] = u32(d, D + 0x14)            # 4-bit (8-bit if wide) width-1 per animated channel
        i['base_bytes'] = u16(d, D + 0x18)
        i['width_bytes'] = u16(d, D + 0x1A)
        i['key_stride'] = u16(d, D + 0x1C)
        i['nch'] = u16(d, D + 0x1E)
        i['out_stride'] = u16(d, D + 0x20)
        i['ntracks'] = u16(d, D + 0x22)
        i['nkeys'] = u16(d, D + 0x24)
        i['wide'] = d[D + 0x26]
        self.decode(d)

    def decode(self, d):
        i = self.info
        nch, nk, wide = i['nch'], i['nkeys'], i['wide']
        sc = [f32(d, i['scales'] + 4 * c) for c in range(nch)]
        df = [f32(d, i['defaults'] + 4 * c) for c in range(nch)]
        kbase = i['bases'] + i['base_bytes'] + i['width_bytes']
        readers = [BitReader(d, kbase + k * i['key_stride']) for k in range(nk)]
        fp = i['flags']; bp = i['bases']; wp = i['widths']; nib = 0
        self.kinds = []
        self.nbits_total = 0
        for t in range(i['ntracks']):
            vals = [[None] * nch for _ in range(nk)]
            kinds = [None] * nch
            ch = 0
            while ch < nch:
                # group of 9 channels: 3 via flag byte F0, then F0&3 selects the next two triples
                f0 = d[fp]
                mode = f0 & 3
                triples = [f0]
                if mode == 1: triples += [None, d[fp + 1]]
                elif mode == 2: triples += [d[fp + 1], None]
                elif mode == 3: triples += [d[fp + 1], d[fp + 2]]
                else: triples += [None, None]
                fp += 1 + (0, 1, 1, 2)[mode]
                for fb in triples:
                    for bit in range(3):
                        if ch >= nch: break
                        a = fb is not None and (fb >> (7 - bit)) & 1
                        s = fb is not None and (fb >> (4 - bit)) & 1
                        if not a:                                  # not stored -> per-channel default
                            kinds[ch] = 'default'
                            for k in range(nk): vals[k][ch] = df[ch]
                        else:
                            if wide: base = s32(d, bp); bp += 4
                            else: base = s16(d, bp); bp += 2
                            if s:                                  # static: base only
                                kinds[ch] = 'static'
                                for k in range(nk): vals[k][ch] = base * sc[ch]
                            else:                                  # animated: base + n-bit delta per key
                                if wide: n = d[wp] + 1; wp += 1
                                else:
                                    n = ((d[wp] >> (4 * nib)) & 15) + 1
                                    wp += nib; nib ^= 1
                                kinds[ch] = 'anim%d' % n
                                self.nbits_total += n
                                for k in range(nk):
                                    vals[k][ch] = (readers[k].read(n) + base) * sc[ch]
                        ch += 1
            self.tracks.append(vals)
            self.kinds.append(kinds)
        self.end = dict(flags=fp, bases=bp, widths=wp + nib,
                        bits_per_key=self.nbits_total)

    def layout(self):
        """Cross-check the decoded stream sizes against the descriptor (all regions 4-byte padded).
        Key budget: sum of widths must fill key_stride bytes, except that trailing width-1 channels may
        run past it (encoder wrote 0 bits for constant channels; the runtime reads 1 bit of the next
        key, i.e. at most 1 quantum of error)."""
        if not self.data: return dict(ok=True)
        i = self.info; e = self.end
        pad = lambda n: (n + 3) // 4 * 4
        r = dict(flags_ok=pad(e['flags'] - i['flags']) == i['bases'] - i['flags'] or i['base_bytes'] == 0,
                 bases_ok=pad(e['bases'] - i['bases']) == i['base_bytes'],
                 widths_ok=pad(e['widths'] - i['widths']) == i['width_bytes'])
        stride_bits = i['key_stride'] * 8; pos = 0; over = 0; tail_ok = True
        for t in range(i['ntracks']):
            for k in self.kinds[t]:
                if k.startswith('anim'):
                    w = int(k[4:])
                    if pos + w > stride_bits:
                        over += w
                        if w != 1: tail_ok = False
                    pos += w
        r['bits_exact'] = (pos + 7) // 8 == i['key_stride']
        r['bits_overrun'] = over
        r['bits_ok'] = r['bits_exact'] or (tail_ok and (pos - over + 7) // 8 <= i['key_stride'])
        r['ok'] = r['bases_ok'] and r['widths_ok'] and r['bits_ok']
        return r


class Anim:
    def __init__(self, d):
        assert d[:9] == b'animation', 'not an animation .data'
        self.d = d
        self.version = cstr(d, 10, 14)
        self.duration = f32(d, 0x1C)
        self.ntracks = u16(d, 0x20)
        self.nframes = u16(d, 0x22)
        self.keyrate = f32(d, 0x5C)                       # keys per frame (0.5 -> every 2nd frame)
        self.codecs = [Codec(d, u32(d, o)) if u32(d, o) else None for o in (0x24, 0x28, 0x2C)]
        self.pose_off = u32(d, 0x48)

    def key_frames(self, nkeys):
        """Frame number of each stored key (0x82240dc8): keys every round(1/keyrate) frames, last key =
        last frame."""
        step = int(1.0 / self.keyrate + 0.5) if self.keyrate else 1
        return [min(k * step, self.nframes - 1) for k in range(nkeys)]

    def rot(self, track, key):
        """Rotation quaternion (x, y, z, w) of a track at a stored key; w = sqrt(1-|xyz|^2) >= 0."""
        x, y, z = self.codecs[0].tracks[track][key][:3]
        w2 = 1.0 - (x * x + y * y + z * z)
        return (x, y, z, math.sqrt(w2) if w2 > 0 else 0.0), w2

    def trans(self, track, key): return tuple(self.codecs[0].tracks[track][key][3:6])
    def scale(self, track, key): return tuple(self.codecs[0].tracks[track][key][6:9])

    def sample(self, track, frame):
        """Rotation at an integer frame: slerp between the two surrounding keys (the game's linear mode:
        0x8299f520 rebuilds w for both keys, 0x829a0d30 = slerp, shortest path, lerp if dot >= 0.99999)."""
        c = self.codecs[0]; kf = self.key_frames(c.info['nkeys'])
        for k in range(len(kf) - 1):
            if kf[k] <= frame <= kf[k + 1]:
                t = 0.0 if kf[k + 1] == kf[k] else (frame - kf[k]) / (kf[k + 1] - kf[k])
                return slerp(self.rot(track, k)[0], self.rot(track, k + 1)[0], t)
        return self.rot(track, len(kf) - 1)[0]


def slerp(a, b, t):
    """Mirror of 0x829a0d30."""
    dot = sum(p * q for p, q in zip(a, b)); tb = t
    if dot < 0: dot = -dot; tb = -t
    ta = 1.0 - t
    if dot < 0.99999:
        th = math.acos(dot); s = math.sqrt(1.0 - dot * dot)
        ta = math.sin(th * ta) / s; tb = math.sin(th * tb) / s
    return tuple(p * ta + q * tb for p, q in zip(a, b))


def pose_joints(d, pose_off):
    """Bind pose joints from the embedded 'pose' object (docs/FORMATS.md s16)."""
    n = u32(d, pose_off + 0x18); jo = u32(d, pose_off + 0x2C); J = []
    for k in range(n):
        r = jo + 52 * k
        J.append(dict(name='joint%d' % k, lt=struct.unpack_from('>3f', d, r), q=struct.unpack_from('>4f', d, r + 24)))
    return J


def qangle(a, b):
    dot = abs(sum(p * q for p, q in zip(a, b)))
    return math.degrees(2 * math.acos(min(1.0, dot)))


def stats(a, verbose=True):
    c = a.codecs[0]
    nk = c.info['nkeys']
    worst_w2 = 1.0; bad = 0; maxstep = 0.0; steps = []; bind = []
    J = pose_joints(a.d, a.pose_off) if a.pose_off else None
    for t in range(c.info['ntracks']):
        prev = None
        for k in range(nk):
            q, w2 = a.rot(t, k)
            worst_w2 = min(worst_w2, w2)
            if w2 < -1e-3: bad += 1
            if prev is not None:
                s = qangle(prev, q); steps.append(s); maxstep = max(maxstep, s)
            prev = q
        if J and t < len(J):
            bind.append(qangle(a.rot(t, 0)[0], J[t]['q']))
    steps.sort(); bind_s = sorted(bind)
    med = steps[len(steps) // 2] if steps else 0
    r = dict(tracks=c.info['ntracks'], keys=nk, frames=a.nframes, min_w2=worst_w2, bad_norm=bad,
             max_key_step_deg=maxstep, median_key_step_deg=med,
             p95_key_step_deg=steps[int(len(steps) * 0.95)] if steps else 0,
             bind_median_deg=bind_s[len(bind_s) // 2] if bind_s else None,
             bind_within_5deg=sum(1 for x in bind if x < 5) if bind else None,
             nkeys_ok=nk == math.ceil((a.nframes - 1) * a.keyrate + 1 - 1e-6),
             layout=[cc.layout() if cc else None for cc in a.codecs])
    r['layout_ok'] = all(l is None or l['ok'] for l in r['layout'])
    if verbose:
        for k, v in r.items(): print(f'  {k:22s} {v}')
    return r


def load_from_bundle(bundle, name):
    sys.path.insert(0, HERE)
    from caff import Caff
    from asset import Asset
    path = os.path.join(ROOT, 'Workspaces', 'dev', 'cache', '4f', bundle)
    c = Caff(open(path, 'rb').read())
    out = []
    for i, s in enumerate(c.syms, 1):
        if s.startswith('aid_anim_') and (name is None or name in s):
            try:
                out.append((s, Asset(c, i).data('.data')))
            except Exception:
                pass
    return out


def continuity(a, b):
    """Compare the last key of anim a with the first key of anim b (e.g. X_into -> X): an into/loop/outof
    chain must meet, so this validates the decode end-to-end."""
    ca, cb = a.codecs[0], b.codecs[0]
    la = ca.info['nkeys'] - 1
    rot = []; tr = []
    for t in range(min(ca.info['ntracks'], cb.info['ntracks'])):
        rot.append(qangle(a.rot(t, la)[0], b.rot(t, 0)[0]))
        tr.append(max(abs(x - y) for x, y in zip(a.trans(t, la), b.trans(t, 0))))
    rs = sorted(rot)
    return dict(tracks=len(rot), rot_median_deg=rs[len(rs) // 2], rot_max_deg=rs[-1],
                rot_within_1deg=sum(1 for x in rot if x < 1.0), trans_max=max(tr))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('file', nargs='?')
    ap.add_argument('--bundle'); ap.add_argument('--name'); ap.add_argument('--all', action='store_true')
    ap.add_argument('--track', type=int); ap.add_argument('--frames', action='store_true')
    ap.add_argument('--stats', action='store_true'); ap.add_argument('--layout', action='store_true')
    ap.add_argument('--chain', nargs=2, metavar=('FIRST', 'NEXT'),
                    help='with --bundle: compare last key of FIRST with first key of NEXT (exact name suffix)')
    o = ap.parse_args()
    if o.chain:
        allit = dict((nm.split(',')[0], d) for nm, d in load_from_bundle(o.bundle, None))
        pick = lambda s: next(v for k, v in allit.items() if k.endswith(s))
        print(o.chain[0], '->', o.chain[1], continuity(Anim(pick(o.chain[0])), Anim(pick(o.chain[1]))))
        return
    if o.file:
        items = [(os.path.basename(o.file), open(o.file, 'rb').read())]
    elif o.bundle == 'all':
        import json
        idx = json.load(open(os.path.join(ROOT, 'Workspaces', 'dev', 'cache', 'assetindex.json')))['Entries']
        bl = sorted({'%06x' % e['Bundle'] for e in idx if e['Type'] == 'anim'})
        items = (it for b in bl if os.path.exists(os.path.join(ROOT, 'Workspaces', 'dev', 'cache', '4f', b))
                 for it in load_from_bundle(b, o.name))
        o.all = True
    else:
        items = load_from_bundle(o.bundle, None if o.all else o.name)
        if not o.all: items = items[:1]
    if o.all:
        agg = dict(n=0, fail=0, bad=0, maxstep=0.0, bind=[], layout_bad=0, overrun=0)
        for nm, d in items:
            try:
                a = Anim(d); r = stats(a, verbose=False)
            except Exception as e:
                agg['fail'] += 1; print('FAIL', nm, e); continue
            agg['n'] += 1; agg['bad'] += r['bad_norm']; agg['maxstep'] = max(agg['maxstep'], r['max_key_step_deg'])
            if r['bind_median_deg'] is not None: agg['bind'].append(r['bind_median_deg'])
            if not (r['layout_ok'] and r['nkeys_ok']): agg['layout_bad'] += 1; print('LAYOUT', nm, r)
            agg['overrun'] += any(l and l.get('bits_overrun') for l in r['layout'])
            if r['bad_norm'] or r['max_key_step_deg'] > 90:
                print(f"  {nm}: bad_norm={r['bad_norm']} max_step={r['max_key_step_deg']:.1f} p95={r['p95_key_step_deg']:.1f}")
        b = sorted(agg['bind'])
        print(f"anims={agg['n']} failed={agg['fail']} layout_mismatch={agg['layout_bad']} "
              f"tail_1bit_overruns={agg['overrun']} "
              f"keys_with_|xyz|>1={agg['bad']} max_key_step={agg['maxstep']:.1f}deg "
              f"median(bind-vs-key0 median)={b[len(b)//2] if b else 0:.2f}deg")
        return
    for nm, d in items:
        a = Anim(d); c = a.codecs[0]
        print(f'{nm}: version {a.version} tracks {a.ntracks} frames {a.nframes} dur {a.duration:.4f}s '
              f'keyrate {a.keyrate} keys {c.info["nkeys"]} at frames {a.key_frames(c.info["nkeys"])}')
        for i, cc in enumerate(a.codecs):
            if cc: print(f'  codec[{i}] {cc.name} @0x{cc.off:x} ' + (str(cc.info) if cc.data else '(empty)'))
        if o.layout:
            for i, cc in enumerate(a.codecs):
                if cc and cc.data:
                    print(f'  codec[{i}] end', {k: hex(v) if k != 'bits_per_key' else v for k, v in cc.end.items()},
                          cc.layout())
        if o.track is not None:
            t = o.track
            J = pose_joints(d, a.pose_off) if a.pose_off else None
            if J: print(f'  track {t} = joint {J[t]["name"]} bind q {tuple(round(x, 4) for x in J[t]["q"])} '
                        f'bind t {tuple(round(x, 4) for x in J[t]["lt"])}')
            print('  channel kinds', c.kinds[t])
            kf = a.key_frames(c.info['nkeys'])
            for k in range(c.info['nkeys']):
                q, w2 = a.rot(t, k)
                print(f'  key {k} (frame {kf[k]}): q=({q[0]:+.5f},{q[1]:+.5f},{q[2]:+.5f},{q[3]:+.5f}) '
                      f'|q|={math.sqrt(sum(x*x for x in q)):.5f} t=({",".join("%+.4f" % x for x in a.trans(t, k))}) '
                      f's=({",".join("%.4f" % x for x in a.scale(t, k))})')
            if o.frames:
                for f in range(a.nframes):
                    q = a.sample(t, f)
                    print(f'  frame {f}: q=({q[0]:+.5f},{q[1]:+.5f},{q[2]:+.5f},{q[3]:+.5f})')
        if o.stats or o.track is None:
            stats(a)


if __name__ == '__main__':
    main()
