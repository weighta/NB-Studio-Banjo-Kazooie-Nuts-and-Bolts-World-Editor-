"""Model LOD/cull tables (rendergraph chunk 30: +0x140 group count, +0x144 -> groups of 0x14 bytes {u32 nLevels, ptr levels, f32 center[3]};
level = 16 bytes {f32 switch distance, u32 0, u32 nodeCount, ptr nodes}; nodeCount 0 = culled beyond that distance).
python tools/probe/lodtables.py <decompressed bundle> [model name filter]   (see docs/research/151b_drawdistance.md)"""
import sys, struct, collections
sys.path.insert(0,'tools/probe')
from caff import Caff
# fast relocation-free parse: pointers in .data are part-relative offsets; trust values
def run(path):
    c=Caff(open(path,'rb').read()); d=c.d
    out=[]
    for pid,(sym,off,size,sec,unk) in enumerate(c.parts,1):
        s=c.secs[sec-1]
        if s['name']!='.data': continue
        nm=c.syms[sym-1].split(',')[0]
        if not nm.startswith('aid_model'): continue
        b=s['start']+off; D=d[b:b+size]
        u=lambda o: struct.unpack_from('>I',D,o)[0]; f=lambda o: struct.unpack_from('>f',D,o)[0]
        try:
            ct,cn=u(0),u(4)
            if cn>64: continue
            ch={u(ct+8*i):u(ct+8*i+4) for i in range(cn)}
            if 30 not in ch: continue
            rg=u(ch[30])
            if D[rg:rg+11]!=b'rendergraph': continue
            ng=u(rg+0x140); gp=u(rg+0x144)
            groups=[]
            for g in range(ng):
                e=gp+0x14*g; nl=u(e); lp=u(e+4)
                levels=[(round(f(lp+16*k),1),u(lp+16*k+8)) for k in range(nl)]
                groups.append((hex(gp+0x14*g),hex(lp),levels))
            out.append((nm,ng,groups))
        except Exception as ex: out.append((nm,'err',str(ex)))
    return out
path=sys.argv[1]
res=run(path)
cull=collections.Counter(); nolod=0
for nm,ng,groups in res:
    if ng==0: nolod+=1; continue
    if ng=='err': continue
    for g in groups:
        lv=g[2]
        if lv and lv[-1][1]==0: cull[lv[-1][0]]+=1
        else: cull['never']+=1
print(path, 'models',len(res),'no LOD table',nolod)
print(' cull distance histogram', sorted(cull.items(), key=lambda x: (str(type(x[0])), x[0])))
if len(sys.argv)>2:
    for nm,ng,groups in res:
        if sys.argv[2] in nm: print(nm, ng, groups)
