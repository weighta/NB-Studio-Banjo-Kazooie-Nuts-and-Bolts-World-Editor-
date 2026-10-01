"""Assembles the co-op shared time-of-day exe mod and prints it as C# ExeWord lines (NB.Core/Mods/ExePatches.cs).

Showdown Town's time of day is chosen each time the town loads by 0x824104C0(settings): the record
aid_misc_banjox_showdowntownsettings_timeofday holds 4 entries of 0x50 bytes (morning, midday, afternoon, night: script
id, f32 weight, ..., game-flag name). A first loop returns an entry whose game flag is not set yet (the intro forces
midday); otherwise a weighted random pick (the game's LCG at 0x82F9DFE4, every weight 0.25). So two players' games pick
independently.

The random pick starts at 0x82410538 (addi r9,r29,4). It becomes a branch to a cave: when the co-op mailbox word
0x82FBCB30 holds 1..4 (set by NB Multiplayer: the room's time of day), the function returns that entry (r3 = settings +
0x50 x (n - 1)) through its own epilogue at 0x824105C4; otherwise the original instruction runs and the random pick
continues.
"""
HOOK, BACK, EPILOGUE, CAVE, PHASE = 0x82410538, 0x8241053C, 0x824105C4, 0x82D21300, 0x82FBCB30

def lis(d, imm): return (15 << 26) | (d << 21) | (imm & 0xFFFF)
def addi(d, a, s): return (14 << 26) | (d << 21) | (a << 16) | (s & 0xFFFF)
def lwz(d, off, a): return (32 << 26) | (d << 21) | (a << 16) | (off & 0xFFFF)
def cmplwi(cr, a, imm): return (10 << 26) | (cr << 23) | (a << 16) | (imm & 0xFFFF)
def mulli(d, a, s): return (7 << 26) | (d << 21) | (a << 16) | (s & 0xFFFF)
def add(d, a, b): return (31 << 26) | (d << 21) | (a << 16) | (b << 11) | (266 << 1)
def br(kind, at, target):
    d = target - at
    if kind == 'b': return (18 << 26) | (d & 0x03FFFFFC)
    bo, bi = {'beq': (12, 26), 'bgt': (12, 25)}[kind]
    return (16 << 26) | (bo << 21) | (bi << 16) | (d & 0xFFFC)

hi = (PHASE + 0x8000) >> 16; lo = PHASE - (hi << 16)
prog = [
    (lis(11, hi), f"lis r11,{hi:#x}"),
    (lwz(10, lo, 11), f"lwz r10,{lo & 0xFFFF:#x}(r11)  room time of day [0x82FBCB30]: 0 = random, 1..4 = morning..night"),
    (cmplwi(6, 10, 0), "cmplwi cr6,r10,0"),
    (('beq', 'orig'), "beq cr6 -> random pick as usual"),
    (cmplwi(6, 10, 4), "cmplwi cr6,r10,4"),
    (('bgt', 'orig'), "bgt cr6 -> out of range: random pick"),
    (addi(10, 10, -1), "addi r10,r10,-1"),
    (mulli(11, 10, 0x50), "mulli r11,r10,0x50"),
    (add(3, 11, 29), "add r3,r11,r29  the room's entry of the settings record"),
    (('b', EPILOGUE), f"b {EPILOGUE:#x}  return it"),
    (addi(9, 29, 4), "addi r9,r29,0x4  (the instruction the hook replaced)"),
    (('b', BACK), f"b {BACK:#x}  continue with the random pick"),
]
labels = {'orig': CAVE + 4 * 10}
print(f'            new ExeWord({HOOK:#010X}, 0x393D0004, {br("b", HOOK, CAVE):#010X}, "b {CAVE:#x} (was addi r9,r29,4): the room decides the time of day"),'.replace('0X', '0x'))
for i, (w, c) in enumerate(prog):
    at = CAVE + 4 * i
    if isinstance(w, tuple): w = br(w[0], at, labels[w[1]] if isinstance(w[1], str) else w[1])
    print(f'            new ExeWord({at:#010X}, 0x00000000, {w:#010X}, "{c}"),'.replace('0X', '0x'))
print(f'// {len(prog)} words, {CAVE:#x}..{CAVE + 4 * len(prog):#x}')
