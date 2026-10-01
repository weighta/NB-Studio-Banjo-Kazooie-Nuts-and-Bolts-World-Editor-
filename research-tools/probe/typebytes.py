import sys, os, struct, zlib, collections, re
sys.path.insert(0, 'tools/probe')
from caff import Caff, u32
from asset import Asset
PFX = 'D:\\LocalLibrary\\BanjoX\\'
def h(s): return zlib.crc32(s.encode()) ^ 0xffffffff
tb = collections.defaultdict(collections.Counter)
unmatched = collections.Counter()
for n in sorted(os.listdir('work/decomp/4f')):
    c = Caff(open('work/decomp/4f/' + n, 'rb').read())
    man = [i + 1 for i, s in enumerate(c.syms) if s == 'manifest']
    if not man: continue
    m = Asset(c, man[0]).data(); cnt = u32(m, 12)
    ids = {}
    for i in range(cnt):
        aid = u32(m, 0x20 + 8 * i); ids.setdefault(aid & 0xffffff, []).append(aid >> 24)
    for s in c.syms:
        t = s[len(PFX):] if s.startswith(PFX) else s
        stem = t.split('\\')[0].split(',')[0]
        mm = re.match(r'aid_([a-z0-9]+)_(.*)', stem)
        if not mm: continue
        typ, rest = mm.group(1), mm.group(2)
        k = h(rest) & 0xffffff
        if k in ids:
            for b in ids[k]: tb[typ][b] += 1
        else: unmatched[typ] += 1
for typ, cnt in sorted(tb.items(), key=lambda x: -sum(x[1].values())):
    print('%-24s %s   unmatched=%d' % (typ, ', '.join('0x%02x:%d' % kv for kv in cnt.most_common(4)), unmatched[typ]))
