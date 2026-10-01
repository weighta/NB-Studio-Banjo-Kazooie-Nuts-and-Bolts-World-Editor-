"""Compute candidate patch words for the garage limits (research 15.2, docs/research/152_garage.md).
python tools/probe/garagepatch.py [grid N (default 19)] [part limit P (default 250)] [--toml]

Grid: the garage editor (0x8264AA58) requires the bounding box of vehicle + held part to span < N cells per axis
(retail N = 19, i.e. coordinates 0..18). Packed grid vectors are (A:11 bits signed | B:10 bits signed | C:11 bits signed).
Parts: vehicle merge refuses when blocks(a)+blocks(b)+loose parts > P (retail P = 250)."""
import sys, os
sys.path.insert(0, os.path.dirname(__file__))
import ppc

args = [a for a in sys.argv[1:] if not a.startswith('--')]
N = int(args[0], 0) if args else 19
P = int(args[1], 0) if len(args) > 1 else 250
toml = '--toml' in sys.argv
assert 1 <= N <= 255, 'grid N must stay <= 255 (blueprint coordinates are u8; field B is 10-bit signed)'
assert 1 <= P <= 0x7FFF, 'part limit is a signed 16-bit compare immediate'

def packed(a, b, c): return ((a & 0x7FF) << 21) | ((b & 0x3FF) << 11) | (c & 0x7FF)

k0 = packed(0, N, N); kN = packed(N, N, N)
patches = [
    # grid / cursor bound (function 0x8264AA58)
    (0x8264AC14, 0x2F090013, 0x2F090000 | N, f'cmpwi cr6,r9,{N}   extent A < N'),
    (0x8264AC30, 0x2F090013, 0x2F090000 | N, f'cmpwi cr6,r9,{N}   extent B < N'),
    (0x8264AC4C, 0x2F0B0013, 0x2F0B0000 | N, f'cmpwi cr6,r11,{N}  extent C < N'),
    (0x8264AD00, 0x3D600000, 0x3D600000 | (k0 >> 16), f'lis r11,{k0 >> 16:#x}   hi of packed (0,N,N)'),
    (0x8264AD04, 0x3D000260, 0x3D000000 | (kN >> 16), f'lis r8,{kN >> 16:#x}    hi of packed (N,N,N)'),
    (0x8264AD0C, 0x616A9813, 0x616A0000 | (k0 & 0xFFFF), f'ori r10,r11,{k0 & 0xFFFF:#x}'),
    (0x8264AD18, 0x610B9813, 0x610B0000 | (kN & 0xFFFF), f'ori r11,r8,{kN & 0xFFFF:#x}'),
    (0x8264AD38, 0x21290013, 0x21290000 | N, f'subfic r9,r9,{N}   clamp C'),
    (0x8264ADA8, 0x216B0013, 0x216B0000 | N, f'subfic r11,r11,{N} clamp C'),
    # part limit
    (0x82604054, 0x2F0B00FA, 0x2F0B0000 | P, f'cmpwi cr6,r11,{P}  can-attach test (0x82603FE0)'),
    (0x826041C0, 0x2F0B00FA, 0x2F0B0000 | P, f'cmpwi cr6,r11,{P}  vehicle merge (0x82604118)'),
    (0x82648150, 0x2F0B00FA, 0x2F0B0000 | P, f'cmpwi cr6,r11,{P}  garage message 34 vs 33 (UI only)'),
]
if P > 250:
    # blueprint preview object (0xC40 bytes, three u32[250] arrays) filled by the loader 0x824BA700: clamp its block count to 250.
    # 0x824BA810..81C is the shared exit (success path jumps there with r31=1) and must stay untouched.
    # The full mod (with the spawner / break-up stack fixes) is ExePatches.VehiclePartLimit400 (P = 400).
    patches += [
        (0x824BA7EC, 0x7C6B1B78, 0x7C6B1B79, 'mr. r11,r3'),
        (0x824BA7F4, 0x2B0B0000, 0x41820CE8, 'beq 0x824BB4DC'),
        (0x824BA7F8, 0x419A0CE4, 0xA14B0000, 'lhz r10,0(r11)'),
        (0x824BA7FC, 0xA14B0000, 0x2B0A00FA, 'cmplwi cr6,r10,250'),
        (0x824BA800, 0x2C0A0000, 0x40990008, 'ble cr6,+8'),
        (0x824BA804, 0x915F007C, 0x394000FA, 'li r10,250'),
        (0x824BA808, 0x40820018, 0x915F007C, 'stw r10,0x7C(r31)'),
        (0x824BA80C, 0x7E9FA378, 0x48000014, 'b 0x824BA820'),
    ]

bad = False
for va, orig, new, what in patches:
    cur = ppc.u32(va)
    flag = '' if cur == orig else f'   !! image has {cur:08X}'
    bad |= cur != orig
    if toml:
        if new != orig:
            print(f'[[patch.be32]]\n    address = 0x{va:08X}\n    value = 0x{new:08X}   # {what}')
    else:
        print(f'{va:08X}  {orig:08X} -> {new:08X}  {"(unchanged) " if new == orig else ""}{what}{flag}')
if bad: sys.exit('original words do not match the image')
