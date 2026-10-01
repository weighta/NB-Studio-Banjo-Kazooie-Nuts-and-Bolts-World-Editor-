"""Object classes in each aid_havok asset of one bundle. python havokclasses.py <ws> <bundle hex> [name filter]"""
import sys, os, struct, collections
sys.path.insert(0, os.path.dirname(__file__))
from caff import Caff
from asset import Asset
ws, b = sys.argv[1], sys.argv[2]; flt = sys.argv[3] if len(sys.argv) > 3 else ''
p = os.path.join(ws, 'game', 'Bundle', '4f', b)
c = Caff(open(p, 'rb').read())
u = lambda d, o: struct.unpack_from('>I', d, o)[0]
for i, s in enumerate(c.syms, 1):
    name = s.split(',')[0]
    if not name.startswith('aid_havok_') or flt not in name: continue
    d = Asset(c, i).data()
    if len(d) < 0x28 or u(d, 0x20) + 8 > len(d): continue
    pf = d[u(d, 0x20):u(d, 0x20) + u(d, 0x24)]
    if len(pf) < 8 or u(pf, 0) != 0x57E0E057: continue
    secs = []
    for k in range(u(pf, 20)):
        o = 0x40 + 0x30 * k
        secs.append((pf[o:o + 19].split(b'\0')[0].decode(), struct.unpack_from('>7I', pf, o + 20)))
    cn = next(x for x in secs if x[0] == '__classnames__'); dt = next(x for x in secs if x[0] == '__data__')
    cnb = pf[cn[1][0]:cn[1][0] + cn[1][1]]; db = pf[dt[1][0]:dt[1][0] + dt[1][6]]
    cl = collections.Counter()
    for k in range(dt[1][3], dt[1][4], 12):
        s_, se, t_ = struct.unpack_from('>III', db, k)
        if s_ == 0xFFFFFFFF: break
        cl[cnb[t_:cnb.index(b'\0', t_)].decode()] += 1
    print(name.replace('aid_havok_banjox_background_showdowntown_showdowntownreferences_', ''), len(d), dict(cl))
