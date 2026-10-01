import struct, os, collections, sys
G = 'Banjo Kazooie Nuts & Bolts (FRESH)/Bundle/50/'
def banks():
    for n in sorted(os.listdir(G)):
        d = open(G + n, 'rb').read()
        u = lambda o: struct.unpack_from('>I', d, o)[0]
        cnt = u(8); nd = u(16); o = 20 + 4 * nd
        for i in range(cnt):
            aid, off, size = struct.unpack_from('>III', d, o + 12 * i)
            if size and d[off:off + 4] == b'DNBW': yield n, aid, d[off:off + size]
def parse(w):
    u = lambda o: struct.unpack_from('>I', w, o)[0]
    ver, hver = u(4), u(8)
    segs = [(u(12 + 8 * k), u(16 + 8 * k)) for k in range(5)]
    bd = segs[0][0]
    flags, count = u(bd), u(bd + 4)
    name = w[bd + 8:bd + 72].split(b'\0')[0].decode('latin1')
    esize, nsize, align, compact = u(bd + 72), u(bd + 76), u(bd + 80), u(bd + 84)
    ents = []
    for i in range(count):
        e = segs[1][0] + esize * i
        fd, fmt, poff, plen, ls, ll = struct.unpack_from('>6I', w, e)
        tag, ch, rate, ba, bits = fmt & 3, (fmt >> 2) & 7, (fmt >> 5) & 0x3FFFF, (fmt >> 23) & 0xFF, fmt >> 31
        ents.append(dict(flags=fd & 0xF, dur=fd >> 4, tag=tag, ch=ch, rate=rate, ba=ba, bits=bits, off=poff, len=plen, loop=(ls, ll)))
    return dict(ver=ver, hver=hver, segs=segs, flags=flags, name=name, esize=esize, align=align, compact=compact, entries=ents)
if __name__ == '__main__':
    tags = collections.Counter(); rates = collections.Counter(); flags = collections.Counter(); n = 0; names = []
    for bundle, aid, w in banks():
        b = parse(w); n += 1
        flags[(b['flags'], b['esize'], b['align'], b['compact'])] += 1
        names.append((bundle, hex(aid), b['name'], len(b['entries'])))
        for e in b['entries']: tags[(e['tag'], e['ch'])] += 1; rates[e['rate']] += 1
    print(n, 'banks'); print('tag,ch', tags); print('rates', rates.most_common(8)); print('bank flags/esize/align/compact', flags)
    for x in names[:25]: print(x)
