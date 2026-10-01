"""VMX / VMX128 decoding helper for ppc.py (Xenon). dis(w) -> str or None."""

def _vx128(w):
    vd = ((w >> 21) & 31) | (((w >> 2) & 3) << 5)
    va = ((w >> 16) & 31) | (((w >> 5) & 1) << 5) | (((w >> 10) & 1) << 6)
    vb = ((w >> 11) & 31) | ((w & 3) << 5)
    return vd, va, vb

def dis(w):
    op = w >> 26
    rD = (w >> 21) & 31; rA = (w >> 16) & 31; rB = (w >> 11) & 31
    if op == 4:
        # VMX128 loads/stores (VX128_1)
        m = w & 0x7F3
        L = {0x003: 'lvsl128', 0x043: 'lvsr128', 0x083: 'lvewx128', 0x0C3: 'lvx128', 0x183: 'stvewx128', 0x1C3: 'stvx128',
             0x2C3: 'lvxl128', 0x3C3: 'stvxl128', 0x403: 'lvlx128', 0x443: 'lvrx128', 0x503: 'stvlx128', 0x543: 'stvrx128',
             0x603: 'lvlxl128', 0x643: 'lvrxl128', 0x703: 'stvlxl128', 0x743: 'stvrxl128'}
        if m in L:
            vd = rD | (((w >> 2) & 3) << 5)
            return f'{L[m]} v{vd},r{rA},r{rB}'
        # standard VMX
        vc = (w >> 6) & 31
        A = {46: 'vmaddfp', 47: 'vnmsubfp', 42: 'vsel', 43: 'vperm'}
        if (w & 0x3F) in A:
            n = A[w & 0x3F]
            if n in ('vmaddfp', 'vnmsubfp'): return f'{n} v{rD},v{rA},v{vc},v{rB}'
            return f'{n} v{rD},v{rA},v{rB},v{vc}'
        if (w & 0x3F) == 44: return f'vsldoi v{rD},v{rA},v{rB},{vc & 15}'
        xo = w & 0x7FF
        X = {10: 'vaddfp', 74: 'vsubfp', 1034: 'vmaxfp', 1098: 'vminfp', 1156: 'vor', 1028: 'vand', 1092: 'vandc', 1220: 'vxor',
             1284: 'vnor', 140: 'vmrghw', 396: 'vmrglw', 128: 'vadduwm', 1152: 'vsubuwm', 388: 'vslw', 644: 'vsrw', 900: 'vsraw'}
        if xo in X: return f'{X[xo]} v{rD},v{rA},v{rB}'
        X1 = {330: 'vrsqrtefp', 266: 'vrefp', 394: 'vexptefp', 458: 'vlogefp', 522: 'vrfin', 586: 'vrfiz', 650: 'vrfip', 714: 'vrfim'}
        if xo in X1: return f'{X1[xo]} v{rD},v{rB}'
        if xo == 652: return f'vspltw v{rD},v{rB},{rA & 3}'
        if xo == 908: return f'vspltisw v{rD},{rA - 32 if rA & 16 else rA}'
        if xo in (842, 906, 970, 778): return f'{ {842:"vcfux",906:"vcfsx",970:"vctsxs",778:"vctuxs"}[xo]} v{rD},v{rB},{rA}'
        C = {198: 'vcmpeqfp', 454: 'vcmpgefp', 710: 'vcmpgtfp', 966: 'vcmpbfp', 134: 'vcmpequw'}
        if (xo & 0x3FF) in C: return f'{C[xo & 0x3FF]}{"." if xo & 0x400 else ""} v{rD},v{rA},v{rB}'
        if xo == 1540: return f'mfvscr v{rD}'
        if xo == 1604: return f'mtvscr v{rB}'
        return None
    if op == 5:
        vd, va, vb = _vx128(w)
        if w & 0x210 == 0:
            return f'vperm128 v{vd},v{va},v{vb},v{(w >> 6) & 7}'
        X = {0x010: 'vaddfp128', 0x050: 'vsubfp128', 0x090: 'vmulfp128', 0x0D0: 'vmaddfp128', 0x110: 'vmaddcfp128',
             0x150: 'vnmsubfp128', 0x190: 'vmsum3fp128', 0x1D0: 'vmsum4fp128', 0x200: 'vpkshss128', 0x240: 'vpkshus128',
             0x280: 'vpkswss128', 0x2C0: 'vpkswus128', 0x300: 'vpkuhum128', 0x340: 'vpkuhus128', 0x380: 'vpkuwum128', 0x3C0: 'vpkuwus128'}
        # note: logical ops live at the 0x200 range in some tables; use Xenia's: vand128=(5,528) etc.
        L = {0x210: 'vand128', 0x250: 'vandc128', 0x290: 'vnor128', 0x2D0: 'vor128', 0x310: 'vxor128', 0x350: 'vsel128',
             0x390: 'vslo128', 0x3D0: 'vsro128'}
        m = w & 0x3D0
        if m in L: return f'{L[m]} v{vd},v{va},v{vb}'
        if m in X: return f'{X[m]} v{vd},v{va},v{vb}'
        return None
    if op == 6:
        vd, va, vb = _vx128(w)
        if w & 0x730 == 0x710: return f'vrlimi128 v{vd},v{vb},{rA:#x},{(w >> 6) & 3}'
        if w & 0x730 == 0x610: return f'vpkd3d128 v{vd},v{vb},{(w >> 18) & 7},{(w >> 16) & 3},{(w >> 6) & 3}'
        m4 = w & 0x7F0
        X3 = {0x230: 'vcfpsxws128', 0x270: 'vcfpuxws128', 0x2B0: 'vcsxwfp128', 0x2F0: 'vcuxwfp128', 0x330: 'vrfim128',
              0x370: 'vrfin128', 0x3B0: 'vrfip128', 0x3F0: 'vrfiz128', 0x630: 'vrefp128', 0x670: 'vrsqrtefp128',
              0x6B0: 'vexptefp128', 0x6F0: 'vlogefp128', 0x7F0: 'vupkd3d128', 0x770: 'vspltisw128'}
        if m4 in X3:
            if m4 in (0x230, 0x270, 0x2B0, 0x2F0, 0x7F0): return f'{X3[m4]} v{vd},v{vb},{rA}'
            if m4 == 0x770: return f'{X3[m4]} v{vd},{rA - 32 if rA & 16 else rA}'
            return f'{X3[m4]} v{vd},v{vb}'
        if m4 == 0x730: return f'vspltw128 v{vd},v{vb},{rA & 3}'
        if m4 == 0x610: return f'vpkd3d128 v{vd},v{vb},{(w >> 18) & 7},{(w >> 16) & 3},{(w >> 6) & 3}'
        if m4 == 0x710: return f'vrlimi128 v{vd},v{vb},{rA},{(w >> 6) & 3}'
        if w & 0x630 == 0x210: return f'vpermwi128 v{vd},v{vb},{((w >> 16) & 31) | (((w >> 6) & 7) << 5)}'
        m = w & 0x3D0
        X = {0x050: 'vrlw128', 0x0D0: 'vslw128', 0x150: 'vsraw128', 0x1D0: 'vsrw128', 0x280: 'vmaxfp128', 0x2C0: 'vminfp128',
             0x300: 'vmrghw128', 0x340: 'vmrglw128', 0x380: 'vupkhsb128', 0x3C0: 'vupklsb128'}
        if (w & 0x3F0) in X: return f'{X[w & 0x3F0]} v{vd},v{va},v{vb}'
        C = {0x000: 'vcmpeqfp128', 0x080: 'vcmpgefp128', 0x100: 'vcmpgtfp128', 0x180: 'vcmpbfp128', 0x200: 'vcmpequw128'}
        if (w & 0x3B0) in C: return f'{C[w & 0x3B0]}{"." if w & 0x40 else ""} v{vd},v{va},v{vb}'
        return None
    if op == 31:
        xo = (w >> 1) & 0x3FF
        L = {103: 'lvx', 359: 'lvxl', 231: 'stvx', 487: 'stvxl', 519: 'lvlx', 551: 'lvrx', 647: 'stvlx', 679: 'stvrx',
             71: 'lvewx', 199: 'stvewx', 6: 'lvsl', 38: 'lvsr', 7: 'lvebx', 39: 'lvehx'}
        if xo in L: return f'{L[xo]} v{rD},r{rA},r{rB}'
        L2 = {663: 'stfsx', 727: 'stfdx', 599: 'lfdx', 215: 'stbx', 407: 'sthx', 343: 'lhax', 21: 'ldx', 149: 'stdx',
              316: 'xor', 476: 'nand', 124: 'nor', 60: 'andc', 412: 'orc', 8: 'subfc', 136: 'subfe', 10: 'addc', 138: 'adde',
              234: 'addme', 202: 'addze', 75: 'mulhw', 11: 'mulhwu', 233: 'mulld', 792: 'sraw', 27: 'sld', 539: 'srd',
              144: 'mtcrf', 20: 'lwarx', 150: 'stwcx.', 83: 'mfmsr', 598: 'sync', 854: 'eieio', 1014: 'dcbz', 86: 'dcbf', 278: 'dcbt', 246: 'dcbtst',
              58: 'cntlzd', 413: 'sradi', 794: 'srad'}
        if xo in L2: return f'{L2[xo]} r{rD},r{rA},r{rB}'
    return None
