"""Parse aid_vehicle assets: header + 0x24-byte block records. python tools/probe/vehicle.py <ws> <exact vehicle name>"""
import sys, os, json, struct
sys.path.insert(0, os.path.dirname(__file__))
from caff import Caff
from asset import Asset

ws, name = sys.argv[1], sys.argv[2]
idx = json.load(open(os.path.join(ws, 'cache', 'assetindex.json')))
E = idx['Entries']
names = {}
for e in E: names.setdefault(e['Id'], e['Name'])
e = next(x for x in E if x['Name'] == name and x['Symbol'] > 0)
p = os.path.join(ws, 'cache', '4f', '%06x' % e['Bundle'])
if not os.path.exists(p): p = os.path.join(ws, 'game', 'Bundle', '4f', '%06x' % e['Bundle'])
a = Asset(Caff(open(p, 'rb').read()), e['Symbol']); d = a.data()
n, x = struct.unpack_from('>HH', d, 0)
print(name, len(d), 'bytes; count', n, 'hdr u16', hex(x), 'floats', struct.unpack_from('>5f', d, 4)[:3], struct.unpack_from('>ff', d, 0x14))
print('grid string', d[0x20:0x6C].split(b'\0')[0])
for k in range(0x6C, 0x7C, 4):
    w = struct.unpack_from('>I', d, k)[0]; tp = a.ptrs.get((a.pid(), k))
    print(f'  {k:X}: {w:08X} {"PTR" if tp else ""}')
o = 0x7C; i = 0
while o + 0x24 <= len(d):
    r = d[o:o + 0x24]
    gx, gy, gz, g3 = r[0], r[1], r[2], r[3]
    f1, oid, f3 = struct.unpack_from('>III', r, 4)
    rot = struct.unpack_from('>3f', r, 16)
    col = struct.unpack_from('>II', r, 28)
    print(f'{i:3} @{o:04X} grid ({gx},{gy},{gz}) {g3:02X} f={f1:08X} {names.get(oid, hex(oid))} {f3:08X} rot=({rot[0]:.3f},{rot[1]:.3f},{rot[2]:.3f}) {col[0]:08X} {col[1]:08X}')
    o += 0x24; i += 1
print('tail', d[o:].hex())
