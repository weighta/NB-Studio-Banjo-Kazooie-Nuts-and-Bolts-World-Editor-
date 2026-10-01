import sys,struct
sys.path.insert(0,'tools/probe')
from xmem_hdr import parse
d,h,sizes=parse(sys.argv[1])
cbs=h['cbs']
for s in range(h['segs']):
    p = h['tbl_end'] if s==0 else s*cbs
    end=min(len(d),(s+1)*cbs); rem=sizes[s]; fr=[]
    while rem>0 and p<end:
        if d[p]==0xFF: u,c=struct.unpack('>HH',d[p+1:p+5]); p+=5
        else: c=struct.unpack('>H',d[p:p+2])[0]; u=32768; p+=2
        fr.append((u,c,d[p:p+4].hex())); p+=c; rem-=min(u,rem)
    odd=[f for f in fr if f[0]!=32768]
    print(s, sizes[s], 'frames',len(fr),'left',rem,'pad',end-p, 'nonstd',odd[:3])
