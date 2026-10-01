"""dump words at VA: python dumpw.py <va> <count>  (shows u32, float, string if pointer)"""
import struct, sys, os
sys.path.insert(0, os.path.dirname(__file__))
from ppc import u32, cstr, BASE, img
va = int(sys.argv[1], 16); n = int(sys.argv[2]) if len(sys.argv) > 2 else 16
for i in range(n):
    a = va + 4 * i; w = u32(a); f = struct.unpack('>f', struct.pack('>I', w))[0]
    s = cstr(w) if BASE <= w < BASE + len(img) else None
    print(f'{a:08X}: {w:08X}  {f:<14.6g} {s or ""}')
