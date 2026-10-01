"""Count object classes across every aid_havok asset (unique names). python tools/probe/havoksurvey.py <ws>"""
import sys, os, json, struct, collections
sys.path.insert(0, os.path.dirname(__file__))
from caff import Caff
from asset import Asset

ws = sys.argv[1]
E = json.load(open(os.path.join(ws, 'cache', 'assetindex.json')))['Entries']
u = lambda b, o: struct.unpack_from('>I', b, o)[0]
counts = collections.Counter(); versions = collections.Counter(); wrappers = collections.Counter(); seen = set(); bad = 0; n = 0
_c = {}
for e in E:
    if e['Type'] != 'havok' or e['Symbol'] <= 0 or e['Name'] in seen: continue
    seen.add(e['Name'])
    b = e['Bundle']
    if b not in _c:
        p = os.path.join(ws, 'cache', '4f', '%06x' % b)
        if not os.path.exists(p): p = os.path.join(ws, 'game', 'Bundle', '4f', '%06x' % b)
        _c = {b: Caff(open(p, 'rb').read())}
    try:
        d = Asset(_c[b], e['Symbol']).data()
        pf = d[u(d, 0x20):u(d, 0x20) + u(d, 0x24)]
        if u(pf, 0) != 0x57E0E057: wrappers['no packfile at +0x20'] += 1; continue
        versions[pf[40:56].split(b'\0')[0].decode()] += 1
        secs = []
        for i in range(u(pf, 20)):
            o = 0x40 + 0x30 * i
            secs.append((pf[o:o + 19].split(b'\0')[0].decode(), struct.unpack_from('>7I', pf, o + 20)))
        cn = next(s for s in secs if s[0] == '__classnames__'); dt = next(s for s in secs if s[0] == '__data__')
        cnb = pf[cn[1][0]:cn[1][0] + cn[1][1]]
        db = pf[dt[1][0]:dt[1][0] + dt[1][6]]
        for k in range(dt[1][3], dt[1][4], 12):
            s_, se, t_ = struct.unpack_from('>III', db, k)
            if s_ == 0xFFFFFFFF: break
            counts[cnb[t_:cnb.index(b'\0', t_)].decode()] += 1
        n += 1
    except Exception as ex:
        bad += 1
print(f'{n} havok assets parsed, {bad} failed; versions {dict(versions)}; {dict(wrappers)}')
for k, v in counts.most_common(): print(f'  {v:6} {k}')
