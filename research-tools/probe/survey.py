import sys,os,re,collections
sys.path.insert(0,'tools/probe'); from caff import Caff
G='Banjo Kazooie Nuts & Bolts (FRESH)'
types=collections.Counter(); bad=[]; secnames=collections.Counter(); vers=collections.Counter(); hdrvals=collections.Counter()
where=collections.defaultdict(set)
files=[('4f',os.path.join('work/decomp/4f',f)) for f in os.listdir('work/decomp/4f')]
for root,ds,fs in os.walk(G+'/Debug/11'):
    for f in fs: files.append(('d11',os.path.join(root,f)))
for f in os.listdir(G+'/loctext/french'): files.append(('loc',os.path.join(G,'loctext/french',f)))
for kind,p in files:
    d=open(p,'rb').read()
    if d[:4]!=b'CAFF': bad.append(p); continue
    try: c=Caff(d)
    except Exception as e: bad.append((p,str(e))); continue
    ok = c.hdr_checksum_ok() and c.end==len(d)
    if not ok: bad.append((p,'cksum' if not c.hdr_checksum_ok() else 'len %x vs %x'%(c.end,len(d))))
    vers[c.ver]+=1; hdrvals[(kind,c.type,c.nsec,tuple(x['name'] for x in c.secs))]+=1
    for s in c.syms:
        m=re.search(r'(aid_[a-z0-9]+)',s)
        t=m.group(1) if m else s.split(',')[0][:30]
        types[(kind,t)]+=1
        where[t].add(os.path.basename(p))
print('bad',bad[:20]); print(vers)
for k,v in sorted(hdrvals.items()): print(v,k)
for k,v in sorted(types.items(), key=lambda x:-x[1]): print('%6d %s %s'%(v,k[0],k[1]))
