import zlib, sys
sys.path.insert(0, 'tools/probe')
from caff import checksum
def fnv1(s, h=0x811c9dc5):
    for ch in s: h = (h * 0x01000193) & 0xffffffff; h ^= ch
    return h
def fnv1a(s, h=0x811c9dc5):
    for ch in s: h ^= ch; h = (h * 0x01000193) & 0xffffffff
    return h
def djb2(s):
    h = 5381
    for ch in s: h = (h * 33 + ch) & 0xffffffff
    return h
def djb2x(s):
    h = 5381
    for ch in s: h = ((h * 33) ^ ch) & 0xffffffff
    return h
def sdbm(s):
    h = 0
    for ch in s: h = (ch + (h << 6) + (h << 16) - h) & 0xffffffff
    return h
def x31(s):
    h = 0
    for ch in s: h = (h * 31 + ch) & 0xffffffff
    return h
def x65599(s):
    h = 0
    for ch in s: h = (h * 65599 + ch) & 0xffffffff
    return h
def oat(s):
    h = 0
    for ch in s:
        h = (h + ch) & 0xffffffff; h = (h + (h << 10)) & 0xffffffff; h ^= h >> 6
    h = (h + (h << 3)) & 0xffffffff; h ^= h >> 11; h = (h + (h << 15)) & 0xffffffff
    return h
def crc32b(s): return zlib.crc32(s)
def crc32n(s): return zlib.crc32(s) ^ 0xffffffff
def adler(s): return zlib.adler32(s)
F = dict(elf=checksum, fnv1=fnv1, fnv1a=fnv1a, djb2=djb2, djb2x=djb2x, sdbm=sdbm, x31=x31, x65599=x65599, oat=oat, crc32=crc32b, crc32n=crc32n, adler=adler)
targets = {0x685374: 'common?', 0x757c4b: 'spiralmountain?', 0x36dc87: 'banjoshouse?', 0xe00470: '?', 0x234cec: 'showdowntown?'}
names = ['aid_bundle_banjox_common', 'aid_bundle_banjox_ui_frontend_startscreen', 'aid_bundle_banjox_levelcommon_garageonly']
variants = lambda n: [n, n.upper(), n.replace('aid_bundle_', ''), n.replace('aid_bundle_banjox_', ''), 'banjox\\' + n, n + '.bundle']
for n in names:
    for v in variants(n):
        b = v.encode()
        for fn, f in F.items():
            h = f(b)
            for sh in range(0, 9):
                if ((h >> sh) & 0xffffff) in targets: print('HIT', fn, repr(v), hex(h), 'shift', sh, targets[(h >> sh) & 0xffffff])
            if (h & 0xffffff) in targets or (h >> 8) in targets: pass
print('done')
