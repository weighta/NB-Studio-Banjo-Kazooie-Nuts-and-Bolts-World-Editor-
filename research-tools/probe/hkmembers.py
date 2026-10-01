"""Dump hkClassMember arrays (Havok 5.5, 24-byte records) around a VA in work/default.exe.
python tools/probe/hkmembers.py <va of a member record containing name ptr> [count before] [count after]
Also: python tools/probe/hkmembers.py find <member name>  -> find member records naming this string."""
import sys, os, struct
sys.path.insert(0, os.path.dirname(__file__))
import ppc
def rec(va):
    n, c, e, t, st, ca, fl, off, at = struct.unpack_from('>IIIBBhHHI', ppc.img, va - ppc.BASE)
    return n, c, e, t, st, ca, fl, off
def show(va):
    n, c, e, t, st, ca, fl, off = rec(va)
    nm = ppc.cstr(n) if 0x82000000 <= n < 0x84000000 else None
    cn = None
    if 0x82000000 <= c < 0x84000000:
        cn = ppc.cstr(ppc.u32(c)) if 0x82000000 <= ppc.u32(c) < 0x84000000 else None
    print(f'{va:#x} off={off:#06x} type={t:3d} sub={st:3d} arr={ca} flags={fl:#x} {nm} class={cn}')
if sys.argv[1] == 'find':
    s = sys.argv[2].encode() + b'\0'
    i = ppc.img.find(s)
    while i >= 0:
        va = i + ppc.BASE
        j = ppc.img.find(struct.pack('>I', va))
        while j >= 0:
            if j % 4 == 0: show(j + ppc.BASE)
            j = ppc.img.find(struct.pack('>I', va), j + 1)
        i = ppc.img.find(s, i + 1)
else:
    va = int(sys.argv[1], 16); b = int(sys.argv[2]) if len(sys.argv) > 2 else 5; a = int(sys.argv[3]) if len(sys.argv) > 3 else 5
    for k in range(-b, a + 1): show(va + 24 * k)
