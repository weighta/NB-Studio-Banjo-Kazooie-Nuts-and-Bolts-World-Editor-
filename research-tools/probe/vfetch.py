"""Scan a byte range for Xenos vfetch instructions matching a vertex stride (dwords)."""
import sys, struct
sys.path.insert(0, 'tools/probe')
from model import load
FMT = {6: '8_8_8_8', 7: '2_10_10_10', 16: '10_11_11', 17: '11_11_10', 25: '16_16', 26: '16_16_16_16', 31: '16_16_FLOAT',
       32: '16_16_16_16_FLOAT', 33: '32', 34: '32_32', 35: '32_32_32_32', 36: '32_FLOAT', 37: '32_32_FLOAT', 38: '32_32_32_32_FLOAT', 57: '32_32_32_FLOAT'}
def scan(d, start, end, stride_dw):
    out = []
    for o in range(start, end - 12, 2):
        w0, w1, w2 = struct.unpack_from('>III', d, o)
        if w0 & 0x1f != 0 or not (w0 >> 19) & 1: continue
        if w2 & 0xff != stride_dw: continue
        fmt = (w1 >> 16) & 0x3f
        if fmt not in FMT: continue
        off = (w2 >> 8) & 0x7fffff
        if off >= stride_dw: continue
        dst = (w0 >> 7) & 0x3f
        swz = w1 & 0xfff
        signed = (w1 >> 12) & 1; normalized = ((w1 >> 13) & 1) == 0
        out.append((o, dst, FMT[fmt], off * 4, 'swz=%03x' % swz, 's' if signed else 'u', 'n' if normalized else 'i', 'const%d' % ((w0 >> 20) & 0x1f)))
    return out
if __name__ == '__main__':
    a = load(sys.argv[1], sys.argv[2]); d = a.data()
    s, e, st = int(sys.argv[3], 16), int(sys.argv[4], 16), int(sys.argv[5])
    for r in scan(d, s, e, st): print(hex(r[0]), r[1:])
