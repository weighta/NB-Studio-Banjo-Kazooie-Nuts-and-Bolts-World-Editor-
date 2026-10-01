"""Print C strings (ASCII or UTF-16BE) at given VAs. python strat.py va [va...]"""
import sys, os
img = open(os.path.join(os.path.dirname(__file__), '..', '..', 'work', 'default.exe'), 'rb').read()
def s_at(va):
    o = va - 0x82000000
    if img[o] == 0 and 32 <= img[o + 1] < 127:
        e = o
        while img[e:e + 2] != b'\0\0': e += 2
        return 'u"' + img[o:e].decode('utf-16-be') + '"'
    e = img.find(b'\0', o, o + 200)
    return repr(img[o:e].decode('latin1'))
if __name__ == '__main__':
    for a in sys.argv[1:]: print(a, s_at(int(a, 16)))
