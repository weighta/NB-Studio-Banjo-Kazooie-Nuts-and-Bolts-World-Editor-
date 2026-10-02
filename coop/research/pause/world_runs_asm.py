"""Exe mod "world keeps running in menus" (Showdown Town co-op): assembles the words, prints them as C# ExeWord lines
(NB.Core/Mods/ExePatches.cs style) and writes world_runs.patch.toml for Xenia tests.

python world_runs_asm.py            all words (pause menu, Change Vehicle, photo mode)
NO_PHOTO=1 python world_runs_asm.py without the photo-mode part
XEX_HASH=...                        module hash for the TOML (default: Workspaces/coopA test edition)

How the single-player game freezes (see notes.txt):
* ONE global pause counter [0x82FAC760]. 0x823E0A20 increments it; on 0 -> 1 it queues state 3 into every game object of
  the app list 0x82FAB060 (the level object [0x82FAC7AC], state word +0x10: 1 = running -> full update 0x823C5BD8;
  0/3 = paused -> 0x82533F48, camera + HUD only). Decrement sites call 0x822BCB10 on 1 -> 0 (queues state 1 again).
* Pause menu (scene 24, init 0x825A6DB8): increments only in single player (0x825A7538). In multiplayer
  ([0x82FAC650] != 0) it increments only when every player is local (0x825A74E8), so a LIVE game keeps running.
* Change Vehicle (scene 32 frontend_loadvehicle, factory 0x825DA8A8) increments it always, and its opener 0x82576778
  also takes the level-wide action lock 0x823B7350([0x82FAC7AC]+0x60, 0xFFFF): AI drivers/NPCs stop acting and the
  vehicles' Havok islands go to sleep, so puppets pushed by velocity writes no longer move.
* Take Photo (0x825A9A78 case 0) closes the pause menu (player input released) and increments it (0x825AA10C); the
  photo-exit callback 0x825AA4A0 decrements it.
The player's own vehicle never sees menu input as long as [0x82FAC70C] (number of open input-capturing UI scenes) is
non-zero: 0x823C5DE0 then clears the game input devices instead of updating them. The photo camera reads the pad on its
own, so photo mode gets that counter (+1 at Take Photo, -1 in the exit callback) instead of the global pause.
"""
import os, struct, sys

CAVE = 0x82D3F2EC          # free zero padding 0x82D3F2EC..0x82D3F400 (reserved for this mod)
UI_COUNT = 0x82FAC70C      # open input-capturing UI scenes (0x823EC930 ++, 0x823ECA58 --)

def lis(d, imm): return (15 << 26) | (d << 21) | (imm & 0xFFFF)
def addi(d, a, s): return (14 << 26) | (d << 21) | (a << 16) | (s & 0xFFFF)
def lwz(d, off, a): return (32 << 26) | (d << 21) | (a << 16) | (off & 0xFFFF)
def stw(s, off, a): return (36 << 26) | (s << 21) | (a << 16) | (off & 0xFFFF)
def b(at, target, link=False): return (18 << 26) | ((target - at) & 0x03FFFFFC) | (1 if link else 0)
NOP, BLR = 0x60000000, 0x4E800020
hi = (UI_COUNT + 0x8000) >> 16; lo = UI_COUNT - (hi << 16)

WORDS = [
    # (address, original, new, comment)
    # --- pause menu
    (0x825A7538, 0x4BE394E9, b(0x825A7538, 0x825A7544),
     "b 0x825A7544 (was bl 0x823E0A20): single-player pause menu does not take the global pause [0x82FAC760] (as in a LIVE game); +0x1FC stays 0 so its close releases none"),
    # --- Change Vehicle
    (0x825DA8F4, 0x4BE0612D, NOP,
     "nop (was bl 0x823E0A20): Change Vehicle screen (scene 32, factory 0x825DA8A8) does not take the global pause"),
    (0x825DA9C4, 0x40990014, b(0x825DA9C4, 0x825DA9D8),
     "b 0x825DA9D8 (was ble cr6): ...and its destructor 0x825DA990 releases none"),
    (0x82576884, 0x39600001, 0x39600000,
     "li r11,0 (was li r11,1): Change Vehicle opener 0x82576778 stores scene+0x17C = 0, so the scene close (0x825DAF20) releases no action lock"),
    (0x825768A8, 0x4BE40AA9, NOP,
     "nop (was bl 0x823B7350): Change Vehicle does not take the level action lock [0x82FAC7AC]+0x60 (AI vehicles and NPCs keep acting)"),
]
PHOTO_WORDS = [
    (0x825AA10C, 0x4BE36915, b(0x825AA10C, CAVE, True),
     f"bl {CAVE:#x} (was bl 0x823E0A20): Take Photo raises the UI input count instead of the global pause (world runs, pad goes to the photo camera only)"),
    (CAVE + 0x0, 0, lis(11, hi), f"lis r11,{hi:#x}"),
    (CAVE + 0x4, 0, lwz(10, lo, 11), f"lwz r10,{lo & 0xFFFF:#x}(r11)  [0x82FAC70C] open input-capturing UI scenes"),
    (CAVE + 0x8, 0, addi(10, 10, 1), "addi r10,r10,1"),
    (CAVE + 0xC, 0, stw(10, lo, 11), f"stw r10,{lo & 0xFFFF:#x}(r11)  game input devices are cleared while it is non-zero"),
    (CAVE + 0x10, 0, BLR, "blr"),
    (0x825AA4B8, 0x817F1728, lwz(11, 0x16D4, 31),
     "lwz r11,0x16d4(r31) (was 0x1728 = [0x82FAC760]): photo exit callback 0x825AA4A0 releases the UI input count [0x82FAC70C] instead"),
    (0x825AA4C8, 0x917F1728, stw(11, 0x16D4, 31), "stw r11,0x16d4(r31) (was 0x1728): ...count - 1 (only when > 0, ble at 0x825AA4C0 kept)"),
    (0x825AA4D0, 0x4BD12641, NOP, "nop (was bl 0x822BCB10 resume): nothing to resume"),
]
if os.environ.get('NO_PHOTO') != '1': WORDS = WORDS + PHOTO_WORDS
HASH = os.environ.get('XEX_HASH', '99C889C487AB83DB')   # Workspaces/coopA game/default.xex (co-op TEST edition)

def toml():
    out = ['title_name = "Banjo-Kazooie: Nuts & Bolts"', 'title_id = "4D5307ED"', f'hash = "{HASH}"', '',
           '[[patch]]', '    name = "RESEARCH: world keeps running in menus"',
           '    desc = "Pause menu, Change Vehicle and photo mode do not freeze the single-player world (pause counter 0x82FAC760, action lock)"',
           '    author = "NB Mod Tool"', '    is_enabled = true', '']
    for a, o, n, c in WORDS:
        out += ['    [[patch.be32]]', f'        address = {a:#010x}', f'        value = {n:#010x} # was {o:#010x}: {c}', '']
    return '\n'.join(out)

if __name__ == '__main__':
    img = open(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..', 'work', 'default.exe'), 'rb').read()
    for a, o, n, c in WORDS:
        cur = struct.unpack_from('>I', img, a - 0x82000000)[0]
        assert cur == o, f'{a:#x}: image has {cur:#010x}, expected {o:#010x}'
        print(f'            new ExeWord({a:#010x}, {o:#010x}, {n:#010x}, "{c}"),')
    print(f'// {len(WORDS)} words')
    open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'world_runs.patch.toml'), 'w').write(toml())
