"""Dump aid_misc_banjox_garageinstructions_default: 58 x 0x48 records (u32 hash, char[64] flag name, f32).
python tools/probe/garageinstr.py"""
import sys, struct
sys.path.insert(0, 'tools/probe')
from caff import Caff
from asset import Asset, find

c = Caff(open('work/decomp/4f/685374', 'rb').read())
a = Asset(c, find(c, 'garageinstructions_default')[0])
d = a.data()
for i in range(len(d) // 0x48):
    r = d[i * 0x48:(i + 1) * 0x48]
    h = struct.unpack_from('>I', r, 0)[0]
    name = r[4:0x44].split(b'\0')[0].decode()
    f = struct.unpack_from('>f', r, 0x44)[0]
    print(f'{i:2d} {h:08x} {f:6.2f} {name}')
