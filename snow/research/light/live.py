"""live.py [field=value ...] [--show] : live-tune Showdown Town's light/fog state in the running instance (XENIA_PID).
The light object is found by its fog block; fields are written to both fog copies. Colours as r,g,b in 0..1.
Fields: amb sun sundir intensity fogcol fogcol2 fogstart fogend fogmax f70 f1428 f8c f02 f1000 f0995 f20 half
        p6c0..p6c4 (op 0x6C values) raw:+OFF (float at light object + OFF)"""
import os, sys, struct
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..', 'tools', 'xenia'))
from xmem import XMem
from xscan import regions
m = XMem()
cache = os.path.join(os.path.dirname(os.path.abspath(__file__)), f'_lightobj_{os.environ.get("XENIA_PID")}.txt')
def find():
    """The active light object: its fog start/end (+0xD0) equal the renderer globals at 0x82F9DF6C."""
    key = m.read(0x82F9DF6C, 8)
    if os.path.exists(cache):
        L = int(open(cache).read(), 16)
        if m.read(L + 0xD0, 8) == key: return L
    for base, size in regions(m):
        if base < 0x40000000 or base >= 0x50000000: continue
        try: buf = m.read(base, size)
        except Exception: continue
        i = buf.find(key)
        while i >= 0:
            L = base + i - 0xD0
            if i >= 0xD0 and buf[i - 0x38:i - 0x34] == bytes.fromhex('3f7eb852') and buf[i + 0xC0:i + 0xC8] == key:
                open(cache, 'w').write(hex(L)); return L
            i = buf.find(key, i + 1)
    raise SystemExit('light object not found')
L = find()
F = {'amb': (0x10, 3), 'sun': (0x20, 3), 'sundir': (0x30, 3), 'shadowdir': (0x50, 3), 'intensity': (0x68, 1),
     'f70': (0x84, 1), 'f1428': (0x88, 1), 'f8c': (0x8C, 1), 'f02': (0x90, 1), 'f1000': (0x94, 1), 'f0995': (0x98, 1), 'f20': (0x9C, 1),
     'fogcol': (0xA0, 3), 'fogcol2': (0xB0, 3), 'half': (0xC0, 4), 'fogstart': (0xD0, 1), 'fogend': (0xD4, 1), 'fogmax': (0xD8, 1)}
for k in range(5): F[f'p6c{k}'] = (0x1B0 + 4 * k, 1)
FOG2 = 0xC0   # second fog copy = +0x140 - 0x80
for a in sys.argv[1:]:
    if a.startswith('--'): continue
    k, v = a.split('=')
    vals = [float(x) for x in v.split(',')]
    if k.startswith('raw:'): off, n = int(k[4:], 16), len(vals)
    else: off, n = F[k]
    data = struct.pack('>' + 'f' * len(vals), *vals)
    m.write(L + off, data)
    if 0x80 <= off < 0xE0: m.write(L + off + FOG2, data)
print(f'light object {L:#x}')
for k, (off, n) in F.items():
    print(f'  {k:10s} +{off:03X} ' + ', '.join(f'{x:.4g}' for x in struct.unpack('>' + 'f' * n, m.read(L + off, 4 * n))))
