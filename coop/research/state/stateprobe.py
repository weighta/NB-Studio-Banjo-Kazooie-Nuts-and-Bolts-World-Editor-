"""Game-state probe for NB Multiplayer (a handful of u32 reads, no scanning). python stateprobe.py [secs]
Verified in Xenia 2026-10-01 (Workspaces/coopB, see NOTES.txt)."""
import os, sys, time, struct
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..', 'tools', 'xenia'))
from xmem import XMem
TOWN_SCRIPTS = {0x1901D1B6: 'town-morning', 0x19E00470: 'town-midday', 0x196BD4A7: 'town-afternoon', 0x193F0052: 'town-night'}
OTHER = {0x19757C4B: 'title (Spiral Mountain)', 0x1936DC87: 'house menus', 0x1920ABF9: 'garage (Mumbo\'s Motors)'}
def probe(m):
    g = m.u32(0x82FAC7AC)                       # current level/script object (0 while switching)
    s = {'level': g}
    if not g: s['state'] = 'loading'; return s
    sid = m.u32(g + 0x00); s['script'] = sid; s['g58'] = m.u32(g + 0x58)
    s['where'] = TOWN_SCRIPTS.get(sid) or OTHER.get(sid) or f'other world/act {sid:08x}'
    s['loading'] = m.u32(0x82FACBA4) != 0xFFFFFFFF   # loading-screen scene handle (0x005D0000 = LoadingTips)
    s['paused'] = m.u32(0x82FAC760)                   # game pause count (pause menu, change vehicle, photo mode)
    s['pause_menu'] = m.u32(0x82E51BD0) != 0xFFFFFFFF  # Frontend_Pause handle 0x00180000
    s['load_blueprint'] = m.u32(0x82E51BD4) != 0xFFFFFFFF  # Change Vehicle list (Frontend_LoadVehicleDisplay 0x00200000)
    s['cutscene'] = m.u32(g + 0x64)                   # 1 during scripted cutscenes (also g+0xBC4 == 3)
    ph = m.u32(g + 0x15B0); s['photo'] = m.u32(ph + 0x164) if ph else 0   # photo camera state, 1 = photo mode
    av = m.u32(g + 0xA44); s['avatar'] = av
    s['vehicle'] = m.u32(av + 0xC3C) if av else 0      # vehicle the player sits in, 0 on foot / no avatar
    in_town = sid in TOWN_SCRIPTS and s['g58'] == 1
    s['state'] = ('loading' if s['loading'] else 'photo' if s['photo'] == 1 else 'paused-menu' if s['paused'] else
                  'cutscene' if s['cutscene'] else 'town' if in_town else s['where'])
    return s
if __name__ == '__main__':
    m = XMem(); end = time.time() + (float(sys.argv[1]) if len(sys.argv) > 1 else 0); prev = None
    while True:
        s = probe(m); line = ' '.join(f'{k}={v:x}' if isinstance(v, int) and not isinstance(v, bool) else f'{k}={v}' for k, v in s.items())
        if line != prev: print(time.strftime('%H:%M:%S'), line, flush=True); prev = line
        if time.time() >= end: break
        time.sleep(0.2)
