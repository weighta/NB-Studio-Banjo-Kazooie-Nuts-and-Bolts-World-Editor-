import sys, os, struct, collections
sys.path.insert(0, 'tools/probe')
from caff import Caff
cnt = collections.Counter(); ex = {}
hdrs = collections.Counter()
for n in sorted(os.listdir('work/decomp/4f')):
    c = Caff(open('work/decomp/4f/' + n, 'rb').read()); d = c.d
    for i, s in enumerate(c.syms):
        if 'aid_texture' not in s and 'aid_ddstexture' not in s: continue
        ps = [p for p in c.parts if p[0] == i + 1]
        dp = [p for p in ps if c.secs[p[3] - 1]['name'] == '.data']
        if not dp: continue
        dp = dp[0]
        b = c.secs[dp[3] - 1]['start'] + dp[1]
        h = d[b:b + dp[2]]
        tag = h[:8]
        hdrs[(tag, h[8:0x18].rstrip(b'\0'), dp[2])] += 1
        if tag != b'texture\0': continue
        fmt = struct.unpack_from('>I', h, 0x18)[0]
        w, hh = struct.unpack_from('>HH', h, 0x24)
        cnt[hex(fmt)] += 1
        ex.setdefault(hex(fmt), (n, s[-70:], w, hh, h[0x30]))
for k, v in hdrs.most_common(): print(v, k)
for k, v in cnt.most_common(): print(v, k, ex[k])
