"""15.1 world bounds: print the original words at every proposed patch site and the constants involved.
python tools/probe/bounds151.py"""
import sys, os, struct
sys.path.insert(0, os.path.dirname(__file__))
import ppc
def f(va): return struct.unpack('>f', struct.pack('>I', ppc.u32(va)))[0]
SITES = [
    (0x822EB8A0, 'LIVE box builder 0x822EB698: li r7,1 (flag value)'),
    (0x822EB8AC, 'LIVE box builder: lfs f0,0xae0(r8) = margin 100.0'),
    (0x822EB8B4, 'LIVE box builder: stb r7,0x11(W+0x550) -> W+0x561'),
    (0x822EB918, 'LIVE box builder: min.y fsubs f8,f8,f0'),
    (0x822EB92C, 'LIVE box builder: max.y fadds f13,f11,f0'),
    (0x8253DA9C, 'bounds setter: lis r10,0x8200'),
    (0x8253DAA4, 'bounds setter: li r3,0 (return value, ignored by both dispatchers)'),
    (0x8253DAAC, 'bounds setter: lfs f0,0xae0(r10) = margin 100.0'),
    (0x8253DAC0, 'bounds setter: min.y fsubs f13,f13,f0'),
    (0x8253DAE4, 'bounds setter: max.y fadds f13,f13,f0'),
    (0x823C1C70, 'world create: beq (no level bounds -> keep hkpWorldCinfo default +-500)'),
    (0x8237D1B4, 'cinfo: stb r8(=1 FIX_ENTITY),0x28 border behaviour'),
    (0x823C1680, 'game border maxPositionExceededCallback (queues object)'),
    (0x823C17F4, 'border processor: beq skip-send (send objMsgId_Avatar_EscapedBackground=22)'),
    (0x82257D90, 'banjoactorStrategy handler: beq on msg 22'),
    (0x8225801C, 'msg 22 MP path: bl 0x82386990 (leave session, reason 46)'),
    (0x8225802C, 'msg 22 SP path: bl 0x823d6640 (actor dies -> Scene_ActorDied)'),
]
for va, what in SITES:
    print(f'{va:#010x}  {ppc.u32(va):08x}  {ppc.dis1(va):34s} {what}')
print()
for va in (0x82000AE0, 0x82000454, 0x82000C98, 0x82000C00, 0x82000AE8):
    print(f'const {va:#010x} = {ppc.u32(va):08x} = {f(va)}')
print('hkpWorldCinfo default AABB max', [f(0x8219F5C0 + 4 * k) for k in range(3)], 'min', [f(0x8219F5D0 + 4 * k) for k in range(3)])
