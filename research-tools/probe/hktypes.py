"""Decode the __types__ section (hkClass / hkClassMember reflection, Havok 5.5, 32-bit big-endian) of a packfile.
python tools/probe/hktypes.py <caff> <havok asset name> [class filter]"""
import sys, os, struct
sys.path.insert(0, os.path.dirname(__file__))
from caff import Caff
from asset import Asset

c = Caff(open(sys.argv[1], 'rb').read())
sym = [i for i, s in enumerate(c.syms, 1) if s.split(',')[0] == sys.argv[2]][0]
d = Asset(c, sym).data()
u = lambda b, o: struct.unpack_from('>I', b, o)[0]
pf = d[u(d, 0x20):u(d, 0x20) + u(d, 0x24)]
secs = []
for i in range(u(pf, 20)):
    o = 0x40 + 0x30 * i
    secs.append(dict(tag=pf[o:o + 19].split(b'\0')[0].decode(), **dict(zip(['start', 'lf', 'gf', 'vf', 'ex', 'im', 'end'], struct.unpack_from('>7I', pf, o + 20)))))
T = secs[1]; tb = pf[T['start']:T['start'] + T['end']]
local = {}
for k in range(T['lf'], T['gf'], 8):
    s_, t_ = struct.unpack_from('>II', tb, k)
    if s_ == 0xFFFFFFFF: break
    local[s_] = t_
glob = {}
for k in range(T['gf'], T['vf'], 12):
    s_, se, t_ = struct.unpack_from('>III', tb, k)
    if s_ == 0xFFFFFFFF: break
    glob[s_] = (se, t_)
virt = []
for k in range(T['vf'], T['ex'], 12):
    s_, se, t_ = struct.unpack_from('>III', tb, k)
    if s_ == 0xFFFFFFFF: break
    virt.append((s_, t_))
cn = secs[0]; cnb = pf[cn['start']:cn['start'] + cn['lf']]
def cname(off): e = cnb.index(b'\0', off); return cnb[off:e].decode()
def cstr(off):
    e = tb.index(b'\0', off); return tb[off:e].decode('latin1')
def ptr(o):
    if o in local: return ('T', local[o])
    if o in glob: return ('S%d' % glob[o][0], glob[o][1])
    return None

TYPES = ['VOID', 'BOOL', 'CHAR', 'INT8', 'UINT8', 'INT16', 'UINT16', 'INT32', 'UINT32', 'INT64', 'UINT64', 'REAL', 'VECTOR4',
         'QUATERNION', 'MATRIX3', 'ROTATION', 'QSTRANSFORM', 'MATRIX4', 'TRANSFORM', 'ZERO', 'POINTER', 'FUNCTIONPOINTER', 'ARRAY',
         'INPLACEARRAY', 'ENUM', 'STRUCT', 'SIMPLEARRAY', 'HOMOGENEOUSARRAY', 'VARIANT', 'CSTRING', 'ULONG', 'FLAGS']
classes = {}
for off, noff in virt:
    n = cname(noff)
    if n != 'hkClass': continue
    # hkClass (5.5): name*, parent*, objectSize, numImplementedInterfaces, declaredEnums*, numDeclaredEnums,
    #                declaredMembers*, numDeclaredMembers, defaults*, attributes*, flags, describedVersion
    np_ = ptr(off); name = cstr(np_[1]) if np_ else '?'
    parent = ptr(off + 4)
    size = u(tb, off + 8)
    mem = ptr(off + 24); nmem = u(tb, off + 28)
    members = []
    if mem:
        for m in range(nmem):
            mo = mem[1] + 0x18 * m   # hkClassMember: name*, class*, enum*, type u8, subtype u8, cArraySize i16, flags u16, offset u16, attributes*
            mn = ptr(mo); mname = cstr(mn[1]) if mn else '?'
            mc = ptr(mo + 4)
            t, st = tb[mo + 12], tb[mo + 13]
            carr, flags, moff = struct.unpack_from('>hHH', tb, mo + 14)
            members.append((moff, mname, TYPES[t] if t < len(TYPES) else t, TYPES[st] if st < len(TYPES) else st, carr, mc))
    classes[off] = (name, parent, size, members)
byoff = {o: v[0] for o, v in classes.items()}
flt = sys.argv[3] if len(sys.argv) > 3 else ''
for off, (name, parent, size, members) in sorted(classes.items(), key=lambda kv: kv[1][0]):
    if flt and flt not in name: continue
    pn = byoff.get(parent[1]) if parent else None
    print(f'{name} : {pn} size {size:#x}')
    for moff, mname, t, st, carr, mc in members:
        print(f'   +{moff:03X} {mname:28} {t}{"<" + str(st) + ">" if st not in ("VOID", 0) else ""}{"[" + str(carr) + "]" if carr else ""} {("class " + byoff.get(mc[1], "?")) if mc and mc[0] == "T" else ""}')
