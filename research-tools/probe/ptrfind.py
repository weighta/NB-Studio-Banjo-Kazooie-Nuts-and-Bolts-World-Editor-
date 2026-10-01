"""Find 32-bit big-endian pointers to given VAs in work/default.exe. python ptrfind.py va [va...]"""
import struct, sys, os
img = open(os.path.join(os.path.dirname(__file__), '..', '..', 'work', 'default.exe'), 'rb').read()
for a in sys.argv[1:]:
    t = struct.pack('>I', int(a, 16)); i = img.find(t); hits = []
    while i >= 0:
        if i % 4 == 0: hits.append(i + 0x82000000)
        i = img.find(t, i + 1)
    print(a, ' '.join(f'{h:#010x}' for h in hits[:20]))
