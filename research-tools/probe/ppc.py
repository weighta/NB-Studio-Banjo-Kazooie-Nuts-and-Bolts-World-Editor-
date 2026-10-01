"""Minimal PowerPC (Xenon) disassembler + cross-reference finder for the decrypted default.xex image
(work/default.exe, memory layout: file offset = VA - 0x82000000).

python tools/probe/ppc.py xref <va hex | "string">      find code that builds this address (lis/addi, lis/ori, lis/lwz...)
python tools/probe/ppc.py dis <va hex> [count]          disassemble
python tools/probe/ppc.py func <va hex>                 disassemble the function containing va (from .pdata)
"""
import struct, sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import vmx as _vmx

IMG = os.path.join(os.path.dirname(__file__), '..', '..', 'work', 'default.exe')
BASE = 0x82000000
img = open(IMG, 'rb').read()
TEXT = (0x821E0000, 0x821E0000 + 0xB2922C)

def u32(va): return struct.unpack_from('>I', img, va - BASE)[0]
def s16(x): return x - 0x10000 if x & 0x8000 else x
def cstr(va, n=80):
    o = va - BASE; e = img.find(b'\0', o, o + n)
    s = img[o:e if e >= 0 else o + n]
    return s.decode('latin1') if s and all(32 <= c < 127 for c in s) else None

SPR = {8: 'lr', 9: 'ctr', 1: 'xer'}
def r(n): return f'r{n}'

def dis1(va):
    w = u32(va); op = w >> 26
    rD = (w >> 21) & 31; rA = (w >> 16) & 31; rB = (w >> 11) & 31; imm = w & 0xFFFF; si = s16(imm)
    if op == 14: return f'li {r(rD)},{si}' if rA == 0 else f'addi {r(rD)},{r(rA)},{si:#x}'
    if op == 15: return f'lis {r(rD)},{imm:#x}' if rA == 0 else f'addis {r(rD)},{r(rA)},{imm:#x}'
    if op == 24: return f'ori {r(rA)},{r(rD)},{imm:#x}' if w else 'nop'
    if op == 25: return f'oris {r(rA)},{r(rD)},{imm:#x}'
    if op == 28: return f'andi. {r(rA)},{r(rD)},{imm:#x}'
    if op == 26: return f'xori {r(rA)},{r(rD)},{imm:#x}'
    if op == 7: return f'mulli {r(rD)},{r(rA)},{si}'
    if op == 8: return f'subfic {r(rD)},{r(rA)},{si}'
    if op == 12: return f'addic {r(rD)},{r(rA)},{si}'
    if op == 11: return f'cmp{"d" if (w >> 21) & 1 else "w"}i cr{rD >> 2},{r(rA)},{si}'
    if op == 10: return f'cmpl{"d" if (w >> 21) & 1 else "w"}i cr{rD >> 2},{r(rA)},{imm}'
    loads = {32: 'lwz', 33: 'lwzu', 34: 'lbz', 35: 'lbzu', 40: 'lhz', 42: 'lha', 36: 'stw', 37: 'stwu', 38: 'stb', 44: 'sth', 48: 'lfs', 50: 'lfd', 52: 'stfs', 54: 'stfd', 46: 'lmw', 47: 'stmw'}
    if op in loads: return f'{loads[op]} {("f" if op >= 48 and op < 56 else "r")}{rD},{si:#x}({r(rA)})'
    if op == 58: return f'{["ld","ldu","lwa"][w & 3]} {r(rD)},{s16(imm & ~3):#x}({r(rA)})'
    if op == 62: return f'{["std","stdu"][w & 1]} {r(rD)},{s16(imm & ~3):#x}({r(rA)})'
    if op == 18:
        li = w & 0x3FFFFFC
        if li & 0x2000000: li -= 0x4000000
        t = (li if w & 2 else va + li) & 0xFFFFFFFF
        return f'b{"l" if w & 1 else ""} {t:#010x}'
    if op == 16:
        bo, bi = rD, rA; bd = w & 0xFFFC
        if bd & 0x8000: bd -= 0x10000
        t = va + bd
        cond = ['lt', 'gt', 'eq', 'so'][bi & 3]
        cr = bi >> 2
        if bo & 0x14 == 0x14: m = 'b'
        elif bo & 0x10: m = 'bdnz' if not bo & 2 else 'bdz'
        elif bo & 8: m = 'b' + cond
        else: m = 'b' + {'lt': 'ge', 'gt': 'le', 'eq': 'ne', 'so': 'ns'}[cond]
        return f'{m}{"l" if w & 1 else ""} cr{cr},{t:#010x}'
    if op == 19:
        xo = (w >> 1) & 0x3FF
        if xo == 16: return 'blr' if rD == 20 else f'bclr {rD},{rA}'
        if xo == 528: return 'bctr' + ('l' if w & 1 else '') if rD == 20 else f'bcctr {rD},{rA}'
        return f'op19.{xo}'
    if op == 21:
        sh, mb, me = rB, (w >> 6) & 31, (w >> 1) & 31
        return f'rlwinm {r(rA)},{r(rD)},{sh},{mb},{me}'
    if op == 20: return f'rlwimi {r(rA)},{r(rD)},{rB},{(w >> 6) & 31},{(w >> 1) & 31}'
    if op == 30: return f'rld* {r(rA)},{r(rD)}'
    if op == 31:
        xo = (w >> 1) & 0x3FF
        if xo == 444: return f'mr {r(rA)},{r(rD)}' if rD == rB else f'or {r(rA)},{r(rD)},{r(rB)}'
        if xo == 0: return f'cmp{"d" if (w >> 21) & 1 else "w"} cr{rD >> 2},{r(rA)},{r(rB)}'
        if xo == 32: return f'cmpl{"d" if (w >> 21) & 1 else "w"} cr{rD >> 2},{r(rA)},{r(rB)}'
        if xo == 266: return f'add {r(rD)},{r(rA)},{r(rB)}'
        if xo == 40: return f'subf {r(rD)},{r(rA)},{r(rB)}'
        if xo == 28: return f'and {r(rA)},{r(rD)},{r(rB)}'
        if xo == 339: return f'mfspr {r(rD)},{SPR.get(((w >> 16) & 31) | (((w >> 11) & 31) << 5), "?")}'
        if xo == 467: return f'mtspr {SPR.get(((w >> 16) & 31) | (((w >> 11) & 31) << 5), "?")},{r(rD)}'
        if xo == 23: return f'lwzx {r(rD)},{r(rA)},{r(rB)}'
        if xo == 151: return f'stwx {r(rD)},{r(rA)},{r(rB)}'
        if xo == 87: return f'lbzx {r(rD)},{r(rA)},{r(rB)}'
        if xo == 104: return f'neg {r(rD)},{r(rA)}'
        if xo == 26: return f'cntlzw {r(rA)},{r(rD)}'
        if xo == 954: return f'extsb {r(rA)},{r(rD)}'
        if xo == 922: return f'extsh {r(rA)},{r(rD)}'
        if xo == 986: return f'extsw {r(rA)},{r(rD)}'
        if xo == 235: return f'mullw {r(rD)},{r(rA)},{r(rB)}'
        if xo == 24: return f'slw {r(rA)},{r(rD)},{r(rB)}'
        if xo == 536: return f'srw {r(rA)},{r(rD)},{r(rB)}'
        if xo == 824: return f'srawi {r(rA)},{r(rD)},{rB}'
        if xo == 19: return f'mfcr {r(rD)}'
        if xo == 491: return f'divw {r(rD)},{r(rA)},{r(rB)}'
        if xo == 459: return f'divwu {r(rD)},{r(rA)},{r(rB)}'
        if xo == 535: return f'lfsx f{rD},{r(rA)},{r(rB)}'
        if xo == 311: return f'lhzx {r(rD)},{r(rA)},{r(rB)}'
        return _vmx.dis(w) or f'op31.{xo}'
    if op in (59, 63):
        s = 's' if op == 59 else ''
        xa = (w >> 1) & 31; rC = (w >> 6) & 31
        A = {18: 'fdiv', 20: 'fsub', 21: 'fadd'}
        if xa in A: return f'{A[xa]}{s} f{rD},f{rA},f{rB}'
        if xa == 25: return f'fmul{s} f{rD},f{rA},f{rC}'
        if xa in (28, 29, 30, 31): return f'{["fmsub","fmadd","fnmsub","fnmadd"][xa - 28]}{s} f{rD},f{rA},f{rC},f{rB}'
        if xa == 23: return f'fsel f{rD},f{rA},f{rC},f{rB}'
        if xa == 26: return f'frsqrte f{rD},f{rB}'
        if xa == 22: return f'fsqrt{s} f{rD},f{rB}'
        if xa == 24: return f'fres f{rD},f{rB}'
        xo = (w >> 1) & 0x3FF
        X = {72: 'fmr', 40: 'fneg', 264: 'fabs', 136: 'fnabs', 12: 'frsp', 14: 'fctiw', 15: 'fctiwz', 846: 'fcfid', 815: 'fctidz', 814: 'fctid', 583: 'mffs', 711: 'mtfsf'}
        if xo in X: return f'{X[xo]} f{rD},f{rB}'
        if xo in (0, 32): return f'fcmp{"u" if xo == 0 else "o"} cr{rD >> 2},f{rA},f{rB}'
        return f'fop{op}.{xo}'
    if op == 13: return f'addic. {r(rD)},{r(rA)},{s16(imm)}'
    return _vmx.dis(w) or f'.word {w:#010x}'

def annotate(va, text, regs):
    """track lis/addi constants to show string/const targets"""
    w = u32(va); op = w >> 26; rD = (w >> 21) & 31; rA = (w >> 16) & 31; imm = w & 0xFFFF
    note = ''
    if op == 15 and rA == 0: regs[rD] = imm << 16
    elif op in (14, 32, 34, 40, 48, 36, 38, 44) and rA in regs and rA != 0:
        v = (regs[rA] + s16(imm)) & 0xFFFFFFFF
        s = cstr(v) if BASE <= v < BASE + len(img) else None
        note = f'   ; {v:#010x}' + (f' "{s}"' if s else '')
        if op == 14: regs[rD] = v
        elif rD in regs: regs.pop(rD)
    elif op == 24 and rD in regs:
        regs[rA] = regs[rD] | imm
    else:
        if op not in (16, 18, 19, 11, 10) and rD in regs and op not in (36, 37, 38, 44, 52, 54, 62): regs.pop(rD, None)
    return text + note

def disasm(start, count):
    regs = {}
    for i in range(count):
        va = start + 4 * i
        print(f'{va:08X}: {u32(va):08X}  {annotate(va, dis1(va), regs)}')

def xref(target):
    hi, lo = (target + 0x8000) >> 16, target & 0xFFFF
    hits = []
    for va in range(TEXT[0], TEXT[1], 4):
        w = u32(va)
        if w >> 26 == 15 and (w >> 16) & 31 == 0 and w & 0xFFFF == hi:
            rd = (w >> 21) & 31
            for k in range(1, 12):
                w2 = u32(va + 4 * k)
                if (w2 >> 16) & 31 == rd and (w2 & 0xFFFF) == lo and (w2 >> 26) in (14, 32, 34, 40, 48, 36, 38):
                    hits.append(va + 4 * k); break
    return hits

def func_of(va):
    # .pdata: (begin va, packed) pairs; length in words = (packed >> 8) & 0x3FFFFF
    p, pe = 0x821A2E00, 0x821A2E00 + 0x3A478
    best = None
    for o in range(p, pe, 8):
        b, x = struct.unpack_from('>II', img, o - BASE)
        n = (x >> 8) & 0x3FFFFF
        if b <= va < b + 4 * n: return b, n
    return None

if __name__ == '__main__':
    cmd = sys.argv[1]
    if cmd == 'xref':
        a = sys.argv[2]
        if a.startswith('0x') or all(c in '0123456789abcdefABCDEF' for c in a) and len(a) == 8: t = int(a, 16)
        else:
            o = img.find(a.encode() + b'\0'); t = BASE + o
            print(f'string at {t:#010x}')
        for h in xref(t):
            f = func_of(h)
            print(f'  ref at {h:#010x} in function {f[0]:#010x} (+{h - f[0]:#x}, {f[1]} words)' if f else f'  ref at {h:#010x}')
    elif cmd == 'dis':
        disasm(int(sys.argv[2], 16), int(sys.argv[3]) if len(sys.argv) > 3 else 40)
    elif cmd == 'func':
        f = func_of(int(sys.argv[2], 16))
        print(f'function {f[0]:#010x}, {f[1]} instructions'); disasm(f[0], f[1])
