"""Assembles the co-op remote-damage exe mod and prints it as C# ExeWord lines (NB.Core/Mods/ExePatches.cs).

The vehicle contact-damage pass 0x825F1AE0(vehicle) runs for every vehicle on every update (from the vehicle update
0x82270568). Its first instruction (mflr r12) becomes a branch to a routine in the padding after .text (0x82D09230):
when NB Multiplayer posted a request in the mailbox (unused tail of .data, 0x82FBCB00) for THIS vehicle, it marks it done,
checks that the block still belongs to the vehicle (its block list +0x1488..+0x148C, 0xB0 per entry, block = entry+4)
and calls the game's own block damage 0x825F17E8(vehicle, block, f1 damage, r6 = &contact point, r7 = 1, r8 = 0),
which subtracts the damage, and breaks the block off (0x825F1C60) when its health runs out. Then the original function
continues.

Mailbox (big-endian): +0 u32 request seq, +4 u32 done seq, +8 vehicle, +0xC block, +0x10 f32 damage,
+0x20 vec4 contact point (16-byte aligned: the break-off code loads it with lvx).
"""
HOOK, CAVE, MAILBOX = 0x825F1AE0, 0x82D09230, 0x82FBCB00   # cave: zero words 0x82D0922C..0x82D09300 of the real image (below: import thunks)
DAMAGE_BLOCK = 0x825F17E8

def lis(d, imm): return (15 << 26) | (d << 21) | (imm & 0xFFFF)
def addi(d, a, s): return (14 << 26) | (d << 21) | (a << 16) | (s & 0xFFFF)
def li(d, s): return addi(d, 0, s)
def lwz(d, off, a): return (32 << 26) | (d << 21) | (a << 16) | (off & 0xFFFF)
def stw(s, off, a): return (36 << 26) | (s << 21) | (a << 16) | (off & 0xFFFF)
def stwu(s, off, a): return (37 << 26) | (s << 21) | (a << 16) | (off & 0xFFFF)
def lfs(d, off, a): return (48 << 26) | (d << 21) | (a << 16) | (off & 0xFFFF)
def cmpw(cr, a, b): return (31 << 26) | (cr << 23) | (a << 16) | (b << 11)
def cmplw(cr, a, b): return (31 << 26) | (cr << 23) | (a << 16) | (b << 11) | (32 << 1)
MFLR12, MTLR12 = 0x7D8802A6, 0x7D8803A6

prog = []   # (word or (kind, target label/address), comment)
labels = {}
def emit(w, c): prog.append([w, c])
def label(n): labels[n] = len(prog)

def ori(a, sreg, imm): return (24 << 26) | (sreg << 21) | (a << 16) | (imm & 0xFFFF)
PUPPET = 0x00EA74B4   # blueprint id of the co-op puppet vehicles (aid_vehicle_banjox_coop_puppet)
ma_hi = (MAILBOX + 0x8000) >> 16; ma_lo = MAILBOX - (ma_hi << 16)
emit(lis(11, ma_hi), f"lis r11,{ma_hi:#x}")
emit(addi(11, 11, ma_lo), f"addi r11,r11,{ma_lo:#x}  r11 = mailbox {MAILBOX:#x}")
emit(lwz(12, 0, 11), "lwz r12,0(r11)  request seq")
emit(lwz(0, 4, 11), "lwz r0,4(r11)  done seq")
emit(cmpw(6, 12, 0), "cmpw cr6,r12,r0")
emit(('beq', 'done'), "beq cr6 -> nothing pending")
emit(lwz(0, 8, 11), "lwz r0,8(r11)  vehicle")
emit(cmpw(6, 0, 3), "cmpw cr6,r0,r3")
emit(('bne', 'done'), "bne cr6 -> not this vehicle")
emit(stw(12, 4, 11), "stw r12,4(r11)  done seq = request seq")
emit(lwz(4, 0xC, 11), "lwz r4,0xC(r11)  block")
emit(lwz(9, 0x1488, 3), "lwz r9,0x1488(r3)  block entries")
emit(lwz(10, 0x148C, 3), "lwz r10,0x148C(r3)  end")
label('loop')
emit(cmplw(6, 9, 10), "cmplw cr6,r9,r10")
emit(('bge', 'done'), "bge cr6 -> block no longer on this vehicle")
emit(lwz(0, 4, 9), "lwz r0,4(r9)  entry block")
emit(cmplw(6, 0, 4), "cmplw cr6,r0,r4")
emit(('beq', 'found'), "beq cr6 -> found")
emit(addi(9, 9, 0xB0), "addi r9,r9,0xB0")
emit(('b', 'loop'), "b loop")
label('found')
emit(MFLR12, "mflr r12")
emit(stw(12, -8, 1), "stw r12,-0x8(r1)")
emit(stwu(1, -0x60, 1), "stwu r1,-0x60(r1)")
emit(stw(3, 0x50, 1), "stw r3,0x50(r1)  keep the vehicle for the original function")
emit(lfs(1, 0x10, 11), "lfs f1,0x10(r11)  damage")
emit(addi(6, 11, 0x20), "addi r6,r11,0x20  contact point")
emit(li(5, 0), "li r5,0")
emit(li(7, 1), "li r7,1")
emit(li(8, 0), "li r8,0  no contact record")
emit(('bl', DAMAGE_BLOCK), f"bl {DAMAGE_BLOCK:#x}  the game's block damage (breaks the block off when its health runs out)")
emit(lwz(3, 0x50, 1), "lwz r3,0x50(r1)")
emit(addi(1, 1, 0x60), "addi r1,r1,0x60")
emit(lwz(12, -8, 1), "lwz r12,-0x8(r1)")
emit(MTLR12, "mtlr r12")
label('done')
emit(MFLR12, "mflr r12  (the instruction the hook replaced)")
emit(('b', HOOK + 4), f"b {HOOK + 4:#x}  back into the contact-damage pass")


def br(kind, at, target):
    d = target - at
    if kind in ('b', 'bl'): return (18 << 26) | (d & 0x03FFFFFC) | (1 if kind == 'bl' else 0)
    bo, bi = {'beq': (12, 26), 'bne': (4, 26), 'bge': (4, 24), 'blt': (12, 24)}[kind]
    assert -0x8000 <= d < 0x8000
    return (16 << 26) | (bo << 21) | (bi << 16) | (d & 0xFFFC)

words = []
for i, (w, c) in enumerate(prog):
    at = CAVE + 4 * i
    if isinstance(w, tuple):
        kind, t = w
        target = CAVE + 4 * labels[t] if isinstance(t, str) else t
        w = br(kind, at, target)
    words.append((at, w, c))
hooks = [(HOOK, 0x7D8802A6, br('b', HOOK, CAVE), f"b {CAVE:#x} (was mflr r12): co-op remote damage")]
for at, was, new, c in hooks:
    print(f'            new ExeWord({at:#010X}, {was:#010X}, {new:#010X}, "{c}"),'.replace('0X', '0x'))
for at, w, c in words:
    print(f'            new ExeWord({at:#010X}, 0x00000000, {w:#010X}, "{c}"),'.replace('0X', '0x'))
print(f'// {len(words)} words, cave {CAVE:#x}..{CAVE + 4 * len(words):#x}')

# ---------------------------------------------------------------------------------------------------------------------
# Puppet hit log. Block damage reaches the health routine 0x82203D00 two ways: per contact through 0x825F17E8(vehicle r3,
# block r4, damage f1, ...) and per block of an explosion through the loop in 0x826127D8 (vehicle r30, block r3, damage f1;
# the call at 0x82612870). For a puppet vehicle both are logged in a 16-entry ring in the mailbox area (+0x40 u32 count,
# +0x50 + 8*(count & 15): u32 vehicle, f32 damage) instead of applied, so puppets never break; NB Multiplayer reads the ring
# and sends the damage to the puppet's player. (Collisions never reach 0x825F17E8 for puppets: see the filter above.)
HOOK2, HOOK3, EXPLOSION_CALL, HEALTH = DAMAGE_BLOCK, 0x82612870, 0x826127D8, 0x82203D00
LOG = 0x82D21270          # zero padding after the first .embsec_ section (0x82D21264..0x82D21400)
CAVE2 = LOG + 4 * 11
def rlwinm(a, s_, sh, mb, me): return (21 << 26) | (s_ << 21) | (a << 16) | (sh << 11) | (mb << 6) | (me << 1)
def add(d, a, b): return (31 << 26) | (d << 21) | (a << 16) | (b << 11) | (266 << 1)
def stfs(s_, off, a): return (52 << 26) | (s_ << 21) | (a << 16) | (off & 0xFFFF)
def mr(a, s_): return (31 << 26) | (s_ << 21) | (a << 16) | (s_ << 11) | (444 << 1)
log = [   # r3 = puppet vehicle, f1 = damage, r0 = the contact's other material (-1: explosion)
    (lis(11, ma_hi), f"lis r11,{ma_hi:#x}"),
    (addi(11, 11, ma_lo), f"addi r11,r11,{ma_lo:#x}  mailbox"),
    (lwz(9, 0x40, 11), "lwz r9,0x40(r11)  hit count"),
    (rlwinm(10, 9, 4, 25, 27), "rlwinm r10,r9,4,25,27  (count & 7) * 16"),
    (add(10, 10, 11), "add r10,r10,r11"),
    (stw(3, 0x50, 10), "stw r3,0x50(r10)  puppet vehicle"),
    (stfs(1, 0x54, 10), "stfs f1,0x54(r10)  damage"),
    (stw(0, 0x58, 10), "stw r0,0x58(r10)  other material of the contact"),
    (addi(9, 9, 1), "addi r9,r9,1"),
    (stw(9, 0x40, 11), "stw r9,0x40(r11)"),
    (0x4E800020, "blr"),
]
def is_puppet(reg):
    return [(lwz(0, 0x18A4, reg), f"lwz r0,0x18A4(r{reg})  vehicle blueprint id"),
            (lis(12, PUPPET >> 16), f"lis r12,{PUPPET >> 16:#x}"),
            (ori(12, 12, PUPPET & 0xFFFF), f"ori r12,r12,{PUPPET & 0xFFFF:#x}  co-op puppet blueprint"),
            (cmpw(6, 0, 12), "cmpw cr6,r0,r12")]
h2 = is_puppet(3) + [(('bne', 'h2_real'), "bne cr6 -> not a puppet"),
                     (li(0, -1), "li r0,-1"),
                     ((10 << 26) | (6 << 23) | (8 << 16), "cmplwi cr6,r8,0  contact record?"),
                     (('beq', LOG), f"beq cr6 -> {LOG:#x}"),
                     (lwz(0, 0xB0, 8), "lwz r0,0xB0(r8)  the contact's other material"),
                     (('b', LOG), f"b {LOG:#x}  puppet: log the hit (returns to the caller), no damage"),
                     (MFLR12, "mflr r12  (the instruction the hook replaced)"), (('b', HOOK2 + 4), f"b {HOOK2 + 4:#x}")]
h3 = is_puppet(30) + [(('bne', 'h3_real'), "bne cr6 -> not a puppet"), (mr(3, 30), "mr r3,r30  the puppet"), (li(0, -1), "li r0,-1  explosion"),
                      (('b', LOG), f"b {LOG:#x}  log the explosion damage of this block, no damage"),
                      (('b', HEALTH), f"b {HEALTH:#x}  block health as before")]
def assemble(base, items, labels):
    out = []
    for i, (w, c) in enumerate(items):
        at = base + 4 * i
        if isinstance(w, tuple):
            kind, t = w
            w = br(kind, at, labels[t] if isinstance(t, str) else t)
        out.append((at, w, c))
    return out
H3 = CAVE2 + 4 * len(h2)
labels = {'h2_real': CAVE2 + 4 * 10, 'h3_real': H3 + 4 * 8}
words2 = assemble(LOG, log, labels) + assemble(CAVE2, h2, labels) + assemble(H3, h3, labels)
assert LOG + 4 * len(log) == CAVE2 and CAVE2 + 4 * (len(h2) + len(h3)) <= 0x82D21400
hooks2 = [(HOOK2, 0x7D8802A6, br('b', HOOK2, CAVE2), f"b {CAVE2:#x} (was mflr r12): puppet hits are logged, not applied"),
          (HOOK3, br('bl', HOOK3, HEALTH), br('bl', HOOK3, H3), f"bl {H3:#x} (was bl {HEALTH:#x}): explosion damage on puppets is logged, not applied")]
for at, was, new, c in hooks2:
    print(f'            new ExeWord({at:#010X}, {was:#010X}, {new:#010X}, "{c}"),'.replace('0X', '0x'))
for at, w, c in words2:
    print(f'            new ExeWord({at:#010X}, 0x00000000, {w:#010X}, "{c}"),'.replace('0X', '0x'))
print(f'// log {LOG:#x}..{LOG + 4 * len(log):#x}, cave2 {CAVE2:#x}..{CAVE2 + 4 * (len(h2) + len(h3)):#x}')
