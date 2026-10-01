import sys, os, struct
sys.path.insert(0, 'tools/probe')
from caff import Caff
PFX = 'D:\\LocalLibrary\\BanjoX\\'
out = open('work/symindex.tsv', 'w', encoding='utf-8')
for n in sorted(os.listdir('work/decomp/4f')):
    c = Caff(open('work/decomp/4f/' + n, 'rb').read())
    for i, s in enumerate(c.syms):
        parts = [p for p in c.parts if p[0] == i + 1]
        secs = ','.join('%s:%x' % (c.secs[p[3] - 1]['name'], p[2]) for p in parts)
        out.write('%s\t%d\t%s\t%s\n' % (n, i + 1, s[len(PFX):] if s.startswith(PFX) else s, secs))
