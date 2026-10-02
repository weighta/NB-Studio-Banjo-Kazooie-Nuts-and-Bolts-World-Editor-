"""Hide / show a co-op puppet vehicle (and its AI driver) without touching block data.
python hidepuppet.py hide|show|status <puppet hex>
  hide: vehicle +0x1C0 = 1 and driver +0x1C0 = 1 (render off), body collision layer (body+0x2C & 0x3F) 7 -> 9
  show: vehicle/driver +0x1C0 = 0, layer 9 -> 7
Driver = [vehicle+0x48] - 0x40 (first child in the object tree), accepted only if its vtable is 0x82FB7EDC and
[driver+0xC3C] == vehicle. Alive check before every write: vtable 0x82FB7F78, +0x4C (level) != 0, blueprint 0x00EA74B4."""
import os, sys
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..', 'tools', 'xenia'))
from xmem import XMem
VEH_VT, AVATAR_VT, PUPPET_BP = 0x82FB7F78, 0x82FB7EDC, 0x00EA74B4
HIDE_LAYER, NORMAL_LAYER = 9, 7
m = XMem()
def alive(v): return m.u32(v) == VEH_VT and m.u32(v + 0x4C) != 0 and m.u32(v + 0x18A4) == PUPPET_BP and m.u32(v + 0x7C0) != 0
def driver(v):
    d = m.u32(v + 0x48)
    if not d: return 0
    d -= 0x40
    return d if m.u32(d) == AVATAR_VT and m.u32(d + 0xC3C) == v else 0
def set_hidden(v, hide):
    if not alive(v): return 'not alive'
    body = m.u32(v + 0x7C0); fi = m.u32(body + 0x2C); d = driver(v)
    m.w32(v + 0x1C0, 1 if hide else 0)
    if d: m.w32(d + 0x1C0, 1 if hide else 0)
    m.w32(body + 0x2C, (fi & ~0x3F) | (HIDE_LAYER if hide else NORMAL_LAYER))
    return status(v)
def status(v):
    body = m.u32(v + 0x7C0); d = driver(v)
    return (f'veh {v:08x} alive={alive(v)} +1C0={m.u32(v + 0x1C0)} vis={m.u32(v + 0x1C4)},{m.u32(v + 0x1C8)} body {body:08x} '
            f'layer={m.u32(body + 0x2C) & 0x3F} driver {d:08x} drv+1C0={m.u32(d + 0x1C0) if d else "-"}')
if __name__ == '__main__':
    c, v = sys.argv[1], int(sys.argv[2], 16)
    print(set_hidden(v, True) if c == 'hide' else set_hidden(v, False) if c == 'show' else status(v))
