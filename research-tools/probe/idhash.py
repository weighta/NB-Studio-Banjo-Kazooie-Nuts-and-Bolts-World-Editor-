import sys, struct, zlib
sys.path.insert(0, 'tools/probe')
from caff import Caff, checksum
c = Caff(open('work/decomp/4f/01d1b6', 'rb').read())
d = c.d
man = [p for p in c.parts if p[0] == c.nsym][0]
base = c.secs[man[3] - 1]['start'] + man[1]
cnt = struct.unpack_from('>I', d, base + 12)[0]
ents = [struct.unpack_from('>II', d, base + 0x20 + 8 * i) for i in range(cnt)]
PFX = 'D:\\LocalLibrary\\BanjoX\\'
def fnv1(s):
    h = 0x811c9dc5
    for ch in s: h = (h * 0x01000193) & 0xffffffff; h ^= ch
    return h
def fnv1a(s):
    h = 0x811c9dc5
    for ch in s: h ^= ch; h = (h * 0x01000193) & 0xffffffff
    return h
def djb2(s):
    h = 5381
    for ch in s: h = (h * 33 + ch) & 0xffffffff
    return h
def sdbm(s):
    h = 0
    for ch in s: h = (ch + (h << 6) + (h << 16) - h) & 0xffffffff
    return h
funcs = dict(elf=checksum, crc32=lambda s: zlib.crc32(s), fnv1=fnv1, fnv1a=fnv1a, djb2=djb2, sdbm=sdbm)
for (aid, idx) in ents[:3]:
    name = c.syms[idx - 1]
    short = name[len(PFX):] if name.startswith(PFX) else name
    stem = short.split('\\')[0]
    print(hex(aid), idx, stem)
    for cand in [name, short, stem, stem.upper(), name.lower(), stem + '\\default.rtx', 'banjox\\' + stem]:
        b = cand.encode()
        for fn, f in funcs.items():
            h = f(b)
            for mask in (0xffffff, 0xfffffff):
                if (h & mask) == (aid & mask) or ((h >> 4) & 0xffffff) == (aid & 0xffffff) or ((h >> 8) & 0xffffff) == (aid & 0xffffff):
                    print('   MATCH', fn, repr(cand), hex(h))
