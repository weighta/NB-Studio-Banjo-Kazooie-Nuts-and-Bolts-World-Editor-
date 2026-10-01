# 15.3 Vehicle damage and break-up: static trace (2026-09-27)

Static analysis of `work/default.exe` only. Nothing here was run in Xenia. Addresses are VAs.
Confidence: **H** = read directly from the code, **M** = strong inference, **L** = guess.

## TL;DR

* **Single-player damage never touches vehicle health `+0xD10`.** In SP (`[0x82FAC650]==0`) and with an empty damage-receiver
  list (`+0xCE4`), every hit is applied to the **health of the block that was hit** (`block+0x270`, max `block+0x26C`).
  `+0xD10` is the multiplayer health model. The earlier Xenia checks read `+0xD10`, so they could not have seen any SP
  damage (H).
* **Collision damage exists.** It is driven by the damage table (`aid_misc_banjox_statsheet_damage`) and scaled by impact speed (H).
* **The break-up** happens when a block's health reaches 0: the block is detached, its joints are cut, the island list is
  rebuilt and every island after the first is split into a new vehicle, which sends `Scene_Vehicle_BreakUp` (270) (H).
  `+0xD10 == 0` does **not** break the vehicle up. It only puts it into the "disabled" smoke state (H).
* **Town:** the only town-specific check on these paths is still the spawn branch at `0x82569B1C`. That branch also does
  **`+0xCEC += 1`**, which was not documented before. A non-zero `+0xCEC` switches off impact damage, the connectivity
  rebuild and the island split. NOP-ing `0x82569B1C` removes this too (H).
* **Easiest in-game test:** keep town-vehicles-normal-rules. Then either write the block-damage multiplier live
  (guest memory) or patch one word so that any damaging hit destroys a block (see section 5).

## 1. Damage model (SP)

| Step | Address | What it does |
|---|---|---|
| Hit entry | `0x825F17E8 (veh r3, f1 amount, block r4, r6, r7 spread, r8 source)` | 1) Returns if `+0x18D0 & 0x10` (immunity). 2) Returns if `[+0xD90]==1`. 3) Returns if the source has `+0x9C & 1` and `veh+0x36C != 0` (source filter; impacts only, explosions pass `r8=0`). 4) If `+0xCE4` count != 0 **or** MP, calls `0x825F0A70` (vehicle health). 5) Otherwise calls `0x82203D00(block, amount)` (block health). If the block is destroyed: when `r7 != 0`, `0x825F1C60` spreads `max(excess, 0.01)` to neighbour blocks (recursive, depth ≤ 10); then `0x8262D558(block,1,0)`. |
| Block damage | `0x82203D00` | Needs authority (`+0x20/+0x24`, `0x8269F150`) and `block+0x26C > 0.001`. `dmg = amount * vehicleBlockGlobals[+4]`. Destroyed when `dmg >= block+0x270`. Health is clamped to 0. Also sets `+0x278/+0x27C` (hit-flash timers) and `+0x280=1`. Returns 1 if destroyed. |
| Detach | `0x8262D558` → `0x8262D228`, `0x8262D3D8` per joint (`block+0x34..+0x38`, stride 0x70) | Cuts the joints, then sets `veh+0xCE8 = 1` (connectivity dirty). |
| Per-frame | `0x82270568` (vehicle post-physics) | At `0x822709A0`: if `+0xCE8`, calls `0x825F0628` (rebuilds the island list `+0xCCC`, count u16 `+0xCDC`) and clears the flag. **All of this is skipped when `+0xCEC != 0` (`0x82270788`).** |
| Split | `0x8221D998` (vehicle update, first call) → `0x82280E68` | Returns if `+0xCEC != 0` (`0x82280E78`), there is no authority, `+0x7C0 == 0`, `+0x1198 != 0` or `+0x14C0 != 0`. Otherwise, for islands 1..n-1, calls `0x826049E0`, which builds a new vehicle, sends `Actor_OccupiedBlockDetaching` (174, `0x82604AC0`) and `Scene_Vehicle_BreakUp` (270, `0x8260517C`). |

Block health comes from the block objparams (block init `0x8261EC20`): `+0x194` = health (for example: wheels_standard 20,
seat 20, engine_small 50, fuel_small 50, lowloader 60, horn 5, laser 15, grenadeegggun 20, selfdestruct 20).
It is raised by `+0x268 × 0.25` per connected joint (`0x8262D070`). `+0x288` is the receiver row from objparams
`+0x368` (all standard blocks use "metalblock"). `+0x28C` is the block's own damage type (what it deals when it hits
something else) from `+0x328` (all use "vehicledefault"). No SP block-health regeneration was found; only the RoboFix
gadget path `0x82601B20` refills `+0x270`.

`aid_misc_banjox_vehicleblockglobals_default` (common bundle 685374, 124 bytes; loaded by name "vehicleBlockGlobals" in
`0x8254EA10` and read through `[[veh+0x4C]+0x15B0]+0x17E0`):
+0 1.0 = spawn-protection time (`+0xD00`), **+4 1.0 = block damage multiplier**, +8 4.0, +0x2C 4.0 = disabled time,
+0x30 4.0 = post-disable invulnerability, +0x34 33.9 = MP health regeneration.

### Vehicle health `+0xD10` (MP model)
* `0x825F0A70 (veh, f1 dmg, r5 source, r6 nonLethal)` returns early on `dmg <= 0` or on immunity bit 0x10. Without
  authority it applies the damage directly. With a receiver list (`+0xCE4`) it sends `Vehicle_Damaged` 303 to the
  first receiver. Otherwise, in MP, `dmg *= [0x82FAE530]*mpMult(0x824D4530)`. Then `health -= dmg`; when `r6` is set
  the hit may not kill. Finally it calls `0x825F0D90`.
* `0x825F0D90 SetHealth`: when health goes from >0 to 0 it sets `+0xD18=0`, `+0xD20=1`, `+0x1060++`,
  `+0xCF8 = globals+0x2C`, calls `0x822A3A80` (notify), `0x825F0098` (per-block `0x82627160`) and spawns
  "fxDisabledVehicleSmoke" on random blocks. **No split.** In any other case it calls `0x825F1F08`, which sets
  `+0xD18` = fraction and `+0xD20` = damage-level index (vehicleblockdamagelevels).
* The disabled timer `0x825F2100` runs only in MP (`0x822707CC`). At 0 it clears the smoke, sets 0x10 and branches to
  `0x825F1940` (invulnerable for globals+0x30 s, "fxInvulnerableVehicleEffect").
* Callers of `0x825F0A70`: `0x825F1928` (MP or receiver list only); `0x82612904` (rust effect, only when
  `[0x82FAC650]` is set; in SP `0x826127D8` damages each block instead); `0x82608E84` (in `0x82608D58`: outside a volume
  for longer than `+0x1C8` s → `+0x1C4`% of max per tick); `0x8240D52C` (in `0x8240D3D8`: vehicle leaves a tracked volume →
  full-health kill, block `+0x338=1`); `0x821E7500` (in `0x821E72A0`, the in-world part edit/pickup object,
  "enter_parts/exiteditor/pickup/drop"; its siblings `0x82645CF8`/`0x82647DE0` are the split callers
  `0x82646630`/`0x826467A0`/`0x826489C8`); `0x82607F38` (network-client replication branch).

## 2. Collision / impact damage (answer: yes, it exists)

`0x82270568` → `0x825F1AE0` at `0x82270814`. It runs only when: `+0xCEC == 0`, there is authority, `+0xDF7 == 0`, and
the replay flag is clear (`[[0x82FAB0FC]+4]+0x15B0+0x1D0 +0xA5A`, set from `[0x82E51C64]` by the replay_scene code).
`+0xDF7` counts blocks whose objparams `+0x224 != 0` (only base_attachpoint/leakpoint/gameplaycreator, so normally 0).

For each contact record (pool `+0x380`, stride u16 `+0x38C`; refs `+0x394..+0x398`, 12 bytes each):
record `+0xAC` = own block, `+0x94` = other actor, `+0xA0` = impact speed, `+0xB0` = the **other body's damage type**
(from `0x822D0DB0`: body `+0xC8`, entity `+0x448`, or `-3` → ask the entity with msg 23 `Avatar_FindDamageType`;
blocks answer with their `+0x28C` in `0x82213F04`), `+0xB4` = factor. A type of `-2` means no damage. Records with
`+0x9C & 0x8000` are applied immediately. Otherwise only the strongest hit per other actor is applied, through
`0x825F17E8(..., r7=1 spread, r8=record)`.

Damage = `0x823B8890(table=[veh+0x4C]+0xEA0, type, block+0x288)`:
* Table = `aid_misc_banjox_statsheet_damage` (gameassetref "damageTable" 0x0B32195A). Layout: u32 rows 53 (receivers),
  u32 cols 109 (damage types), u32 40, f32 3.5, f32 200, then `[col][row]` pairs (SP f32, MP f32), then 64-byte names.
* For types < 40 (impact types) the value is scaled by the speed factor `clamp((speed×2.237 − 3.5)/(200 − 3.5), 0, 1)`
  (m/s → mph; below about 1.6 m/s there is no damage). Explosion types (≥ 40) are not speed-scaled.
* SP values against the "metalblock" row: default 23, vehicledefault 10, spike 5, ram 10, banjo 0, nodamage 0,
  buggy 100, mrpatch 100, gruntbot_* 2..50, eggimpact 2, grenadeimpact 8, laserimpact 12, clockworkimpact 15,
  torpedileimpact 15, mumbobomboimpact 20, policegrenadeimpact 10, selfdestruct 100, nuttyacresbomb 100.
* Example: a 40 mph hit against a "default" body is 23 × 0.186 ≈ 4 HP, applied to the one block that made contact.
  A 20-HP wheel needs about five such crashes. Which type the static world/terrain returns was **not** resolved (M/L).
  Read `+0xB0` live, or watch `block+0x270`.

## 3. Explosions and the owner

Chain: damage area (embedded object; `+0` source, `+0x10` pos, `+0x40/+0x44` damage types, `+0x48` falloff factor, `+0x4C` radius,
`+0x50` already-hit actors, `+0x78` already-hit blocks, `+0x8C/+0x90` optional callbacks).
`0x8255EEB0` computes the radius and factor and calls `0x8255F180`. `0x8255F180` queries bodies in the radius
(`0x82273C68`). For each actor that is not yet in `+0x50` it sends **`Banjo_Avatar_HitByDamageArea` (195)** (built at
`0x8255F438`), unless a `+0x8C` callback replaces the message. If the message is unhandled it calls `0x823DC1D8`. It then
adds the actor to `+0x50`. The vehicle handler `0x8221A7F8` case 195 (`0x8221B2B8`) calls `0x822709D0`. That function does
nothing in MP or when a receiver list exists. Otherwise it collects the blocks inside the radius and skips blocks already
in the area's block list and blocks shielded by closer blocks. For each remaining block it calls
`0x825F17E8(block, table(type, block+0x288) × factor, r7=0, r8=0 source)`.

Owner exclusion:
* The only explicit case is `0x8255EE20` (damage-area start with an owner vehicle). It pre-fills the hit-block list with
  **all blocks of that vehicle**. Its only caller is the Self-Destruct block (`0x82633884`) (H).
* The projectile explosions (`0x82433D58` state 3 and others calling `0x8255EEB0`) clear both lists at start
  (`0x824348B4`, `0x82433E94`). No owner check was found in `0x8255F180` or `0x822709D0`, and the source filter in
  `0x825F17E8` is not used (`r8=0`). So own grenade/egg explosions **should** damage the player's own blocks in SP once
  bit 0x10 is gone (M; per-area `+0x8C` callbacks were not traced). Egg projectile explosions use eggimpact
  (2 HP/blast), grenadestraight uses grenadeimpact (8). The earlier test watched `+0xD10`, so it could not see this.

## 4. Showdown-Town-specific checks (question 2)

Every `lwz rX,0x58(rY); cmpwi rX,1` in the executable (13 sites) and every access to `0x82FAC650`/`0x82FAC5D0` inside the
~40 functions above:

| Address | Check | On a damage/break path? |
|---|---|---|
| `0x82569B14/B1C` + `0x82569B24` | owner `+0x58==1` or `[0x82FAC5D0]` → town branch `0x82569B50` | **Yes, the only one.** It sets `+0x18D0 |= 0x410` (0x10 immunity: `0x825F0AA8`, `0x825F1818`, `0x82608E3C`), `+0xEB4/+0xEB8=1`, and **`+0xCEC += 1` (`0x82569B50..B64`)**. Non-zero `+0xCEC` skips impact damage, VehicleImpact 181, the MP disabled timer and the `+0xCE8` rebuild (`0x82270788`), and the island split (`0x82280E78`). Also read by `0x82236CE8`, `0x82460D30`, `0x823F6320` (collision/wrench). Other code changes `+0xCEC` via `0x825FF210` (paired inc/dec with `+0x1194`). |
| `0x82626494` | block init: in town, blocks of category 5/44/21 (wheels, fuel, …) get `+0x144` = const | No (not health) |
| `0x822717F8` | vehicle block update: in town it needs `veh+0x624 != 0` | No (drive/sfx code, no damage calls) |
| `0x821E88DC`, `0x8225D430` (Spec-o-spy), `0x824322FC` (enter portal), `0x824AE254`/`0x8255EB3C` (town AI evade/panic), `0x825648B4`, `0x82577DFC` (Change Vehicle menu), `0x825A8C44`, `0x825A9978` | various | No |
| `[0x82FAC650]` (MP) at `0x825F0B4C`, `0x825F1884`, `0x822707D0`, `0x82270A24`, `0x823B88C0`, `0x8261285C/2880`, `0x825F2218`, `0x826080A8` | SP/MP model selection | Mode, not town |
| `[0x82FAC5D0]` (demo) at `0x82604EC0` | skips msg 324 BeingEditedChange after a split | Not town |
| level byte `+0xD4 & 8` (`0x824A9B6C`, `0x824317E0`) | not reached from any function above | No |

Conclusion: with `0x82569B1C` NOP-ed, nothing else on the damage or break-up path depends on Showdown Town (H). It is
still worth confirming `+0xCEC == 0` live on the town vehicle.

## 5. Making a town vehicle break apart (question 3)

Prerequisite for all options: the town-vehicles-normal-rules mod (NOP `0x82569B1C`). Check live that `v+0x18D0` lacks
0x10 and that `v+0xCEC == 0`.

**How to observe it:** do not watch `+0xD10`. Watch the block list: `p` from `[v+0x1488]` to `[v+0x148C]` in steps of
0xB0, `blk=[p+4]`, then `blk+0x270` (health) and `blk+0x26C` (max). A break-up shows as `blk+0x270 == 0`, the u16
`v+0x1498` shrinking, `v+0xCDC > 1`, and new vehicle objects.

1. **Live guest-memory write (no new patch; recommended first).** Write the float at
   `[[[v+0x4C]+0x15B0]+0x17E0] + 4` (vehicleBlockGlobals block-damage multiplier): 1.0 `3F800000` → 50.0 `42480000`.
   Then fire the grenade-egg gun at point blank, or crash. A grenade blast becomes 8×50 = 400 per block in the radius,
   so every nearby block (20–60 HP) is destroyed. They detach, and the vehicle splits with BreakUp 270. Effect: global,
   all vehicles. Confidence M-H (the multiplier read at `0x82203D74` is certain; own-blast reachability is M).
   Alternative: write `0.01` to one block's `+0x270` (keep `+0x26C > 0.001`), then hit it once.
2. **Data-only mod:** `aid_misc_banjox_vehicleblockglobals_default` (685374, resident, `.data` 0x7C) `.data+0x4`:
   `3F800000` → `42480000`. Same effect as option 1, permanent, all worlds.
3. **One-word exe patch, "blocks die on any hit":** `0x82203D84` `3BA00000` (li r29,0) → `3BA00001` (li r29,1).
   `0x82203D00` then reports every hit that passes its checks as destroyed. For impacts (`r7=1`) the excess (at least
   0.01) spreads through neighbours up to depth 10 (`0x825F1C60`), so a single crash above about 1.6 m/s against
   anything with a non-zero table value tears the trolley apart. Own grenade blasts remove every block in the radius.
   Confidence H for the mechanism; affects all vehicles.
4. **Built-in part: Self-Destruct gadget** (`objDefId_vehicleBlockSelfDestruct`). Activate `0x826338F0` (state 1),
   countdown `0x82633650` (cancelled by `v+0x18D0 & 1`), then `0x826337D0` state 2 → `0x825F15B0(v, n=blk+0x488)`
   splits the vehicle into n groups. `0x825F15B0` does **not** check the immunity bit. It also starts a damage area that
   excludes the owner's blocks and damages others with "selfdestruct" (100). Objparams and avatarhavokdata are in 685374,
   but the model and havok are only in 4e967e, which is **not** a dependency of 234cec. Best verified in a normal world
   (for example a Nutty Acres vehicle built in the garage). In town it risks missing assets (L).
5. **Town-native damage source (not verified):** the Showdown Town police (`aid_objparams_banjox_avatar_projectile_police`
   in 234cec, explosion type policegrenadeimpact = 10 SP to metal blocks; the dialogs list offences such as
   lawspeeding and lawposessionoffirearms). It could hit the trolley once bit 0x10 is cleared. The trigger conditions
   were not traced. `props_misc_bomb` objparams exist only in 04dd5f/90aff1/eaaac4 (not town). Its "nuttyacresbomb"
   type is 100 but needs its explosion trigger. Just touching it does nothing, as observed.

Not useful: setting `+0xD10` to a small value. The next `0x825F0A70` hit takes it to 0, which only triggers the
disabled/smoke state in `0x825F0D90` and never a split. In SP nothing but the special callers above lowers `+0xD10`.
