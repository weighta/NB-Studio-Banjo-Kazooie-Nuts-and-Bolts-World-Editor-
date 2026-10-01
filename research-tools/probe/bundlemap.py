import sys, os, re, collections, struct
sys.path.insert(0, 'tools/probe')
from caff import Caff
G = 'Banjo Kazooie Nuts & Bolts (FRESH)/Bundle/50'
def u32(d, o): return struct.unpack_from('>I', d, o)[0]
PFX = 'D:\\LocalLibrary\\BanjoX\\'
rows = []
for n in sorted(os.listdir('work/decomp/4f')):
    c = Caff(open('work/decomp/4f/' + n, 'rb').read())
    names = [s[len(PFX):] if s.startswith(PFX) else s for s in c.syms]
    kinds = collections.Counter(re.match(r'(aid_[a-z0-9]+)', x).group(1) if x.startswith('aid_') else 'other' for x in names)
    words = collections.Counter()
    for x in names:
        m = re.match(r'aid_[a-z0-9]+_banjox_([a-z0-9]+)_?([a-z0-9]*)', x)
        if m:
            words[m.group(1) + ('_' + m.group(2) if m.group(1) in ('background', 'script', 'level', 'misc', 'marker', 'callout', 'challenge', 'dialog') else '')] += 1
    d = open(os.path.join(G, n), 'rb').read()
    ndep = u32(d, 16)
    deps = [('%08x' % u32(d, 20 + 4 * i))[2:] for i in range(ndep)]
    cnt = u32(d, 8)
    rows.append((n, len(c.d), c.nsym, cnt, deps, words.most_common(4), dict(kinds.most_common(6))))
for r in rows:
    print(r[0], '%6.1fMB' % (r[1] / 1e6), 'nsym', r[2], 'str', r[3], 'deps', ' '.join(r[4]), '|', ', '.join('%s:%d' % kv for kv in r[5]), '|', r[6])
