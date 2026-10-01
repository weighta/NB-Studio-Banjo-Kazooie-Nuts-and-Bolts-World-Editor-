import sys, struct
sys.path.insert(0, 'tools/probe')
from caff import Caff
from asset import Asset
def show(bundle, sub):
    c = Caff(open('work/decomp/4f/' + bundle, 'rb').read())
    for i, s in enumerate(c.syms):
        if sub in s:
            a = Asset(c, i + 1); d = a.data()
            print(s[-70:], 'len', hex(len(d)), 'gpu', hex(len(a.data('.texturegpu'))))
            for o in range(0x18, len(d), 4):
                p = a.ptr(o)
                print('   %04x: %08x %s' % (o, struct.unpack_from('>I', d, o)[0], ('-> %s+%x' % p) if p else ''))
            return
show('685374', 'actionbuttons_talk')
show('1533cd', 'reflectionmaps_sky1_front_0x0981a870mip')
show('0d0735', 'showdowntown_ocean_animatedwave_displacementfoamcolour')
