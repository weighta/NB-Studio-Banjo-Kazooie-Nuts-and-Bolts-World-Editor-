# 15.1 World bounds / "map resets at the far edge" (static RE)

Status: mechanism identified statically (no Xenia run). Image: `work/default.exe` (VA = file offset + 0x82000000).
Helper scripts: `tools/probe/bounds151.py` (prints every patch site + constants), `tools/probe/scan_cinfo.py`
(finds the hkpWorldCinfo ctor), `tools/probe/hkmembers.py` (hkClassMember dumper), `tools/probe/msgcmp.py`
(functions that compare against a message id).

## 1. Summary

Crossing the edge is **not** a kill-plane float compare and not terrain streaming. It is the **Havok broadphase
border**:

1. At level load the game builds `hkpWorldCinfo` and sets `m_broadPhaseWorldAabb` from the level's world box
   (`W+0x610` min / `W+0x620` max). **The live box is built by `0x822EB698`**: the AABB of the level's collision
   shape (hkpShape::getAabb), merged with other level bodies, then **+/- 100 units** on every axis. This was
   revised after live Xenia readings, see 3.0. The command handler `0x8253DA70` described first does not produce
   the live box in Showdown Town. If neither writer runs, the Havok default **+/-500** stays.
2. The game installs its own `hkpBroadPhaseBorder` subclass. Its `maxPositionExceededCallback` does not fix/remove
   the body; it queues the owning game object.
3. Every frame the queue is flushed: each queued object with a non-null `+0x4C` gets
   **`objMsgId_Avatar_EscapedBackground` (message 22)**.
4. Banjo's actor-strategy handler reacts to message 22:
   * single player: `0x823D6640`, meaning the actor "dies" (`objMsgId_Scene_ActorDied` 112 is broadcast, then
     `objMsgId_Actor_DieDeleteNotification` 77). This is the respawn/reset the user sees.
   * multiplayer: `0x82386990(46)`, which leaves the session with reason 46 = **"Player left the broadphase"**
     (the string in the reason table at 0x824DE990).

The "terrain repeats" observation is not explained by this code (see section 6).

## 2. Havok world creation

### 2.1 hkpWorldCinfo constructor `0x82C9AD28` (found by `scan_cinfo.py`: only function with li 1024 and li 250)
```
82C9AD30  lis r11,0x821a ; addi r11,r11,-0xa20   -> 0x8219F5E0 (0,-9.8,0,0) gravity
82C9AD40  addi r5,..      -> 0x8219F5D0 (-500,-500,-500,0)
82C9AD44  addi r4,..      -> 0x8219F5C0 (+500,+500,+500,0)
82C9AD4C  lvx128 v0,r0,r11 / 82C9AD5C lvx128 v13,r0,r5 / 82C9AD64 lvx128 v12,r0,r4
82C9AD70  stvx128 v0,r3,r8(16)     m_gravity                 @+0x10
82C9ADAC  li r9,1024 ; stw r9,0x20(r3)   m_broadPhaseQuerySize @+0x20
82C9AE08  stb r11(0),0x28(r3)      m_broadPhaseBorderBehaviour = ASSERT (0)  @+0x28
82C9ADCC  stvx128 v13,r3,r29(48)   m_broadPhaseWorldAabb.min = -500 @+0x30
82C9ADD4  stvx128 v12,r3,r7(64)    m_broadPhaseWorldAabb.max = +500 @+0x40
82C9ADDC  li r30,250 ; stw r30,0x60(r3)  m_sizeOfToiEventQueue
```
Enum from reflection (0x820E6930): ASSERT 0, FIX_ENTITY 1, REMOVE_ENTITY 2, DO_NOTHING 3.
Callers: 0x823C1C60 (game) plus 0x82988D18 / 0x829891DC (Havok-side, no direct callers).

### 2.2 Game world builder `0x823C1C48(r3 = W+0x6D0 physics wrapper, r4 = W)`, called from 0x823C56D4
```
823C1C60  bl 0x82C9AD28                 cinfo at r1+0x70 (defaults)
823C1C64  lbz r10,0x561(r30)            W+0x561 = "level bounds valid"
823C1C70  beq 0x823C1C94                -> not set: keep +/-500
823C1C84  lvx128 v0,r11(W+0x550),r10(0xC0)   W+0x610  (min)
823C1C88  lvx128 v13,r11,r9(0xD0)            W+0x620  (max)
823C1C8C  stvx128 v0 -> r1+0xA0 = cinfo+0x30 ; 823C1C90 stvx128 v13 -> r1+0xB0 = cinfo+0x40
823C1C9C  bl 0x8237D120                 havokParams (see below)
823C1CF8  bl 0x82CAA390                 hkpWorld::hkpWorld(cinfo)  (alloc 0x310), world -> wrapper+0x8 (W+0x6D8)
823C1DB0  bl 0x82C99338                 new hkpBroadPhaseBorder(world, 0) (0x48 bytes)
823C1DC0..DCC  vtables 0x8214236C / 0x82142380 / 0x82142388 = game subclass
823C1DFC  stw r30,0x18(r31)             border -> wrapper+0x18 (W+0x6E8)
823C1E04  bl 0x82CA4858                 hkpWorld::setBroadPhaseBorder (writes world+0x18C)
```
`0x8237D120` loads `havokParams` and sets `stb 1,0x28(cinfo)` at **0x8237D1B4** (border behaviour FIX_ENTITY,
irrelevant because the border is replaced) and `stb 3,0xBD` (simulation type).

### 2.3 hkpWorld ctor `0x82CAA390` (confirms the cinfo layout)
* 0x82CAA570..580: cinfo+0x30/+0x40 copied to **world+0x2D0 / +0x2E0** (reflection member `broadPhaseExtents`).
* 0x82CAB5AC: `lbz r7,0x28(cinfo); cmplwi 3; beq` (no border for DO_NOTHING) otherwise
  `bl 0x82C99338` then `stw r3,0x18C(world)` (reflection member `broadPhaseBorder` @0x18C).
* Base `hkpBroadPhaseBorder::maxPositionExceededCallback` = 0x82C99740 (behaviour <=1: set motion type 7 = fixed via
  0x82CA2A78, 2: remove entity). The game's subclass overrides it.

## 3. Where the numbers come from

### 3.0 The live box: `0x822EB698(r3 = level-geometry owner G, r4 = W)` (revision after Xenia readings)
Live readings in Showdown Town (retail, coordinator): `W+0x561 = 1`, min (-556.26, -116.23, -622.45),
max (525.42, 314.11, 757.22). The hkpWorld extents `[W+0x6D8]+0x2D0/+0x2E0` are identical. With the first patch
(0x8253DAAC = C00A0C00) the box was **unchanged**, so 0x8253DA70 is not what produces it.
`fieldref.py 561/610/620` missed the real writer because it stores relative to `r3 = W+0x550` with displacements
+0x11 / +0xC0..+0xD8, not to W with +0x561 / +0x610.
Found by scanning `addi rX,rY,0x550`, then the callers of 0x823BF478 (0x822EB844, 0x823D3008, 0x8253DA94).
The only caller is 0x822EB56C in 0x822EB430 (which 0x822EB3A0 registers as a callback).
```
822EB6C4  lwz r3,0x7C0(G) ; bl 0x821F1F70        collision body -> [+0x10] shape -> vtbl+0x1C getAabb(identity
                                                  0x82FAF850, tolerance 0.0) -> min r1+0x80 (v126), max r1+0x70 (v127)
                                                  ([G+0x7C0] is set at 0x822EB560 from 0x8237D430 = level collision body)
822EB6D4..724  for each 16-byte entry in [G+0x744, G+0x748): 0x82248240(obj) AABB, merged (VMX128 min/max)
822EB738..834  records from [[G+0x8C0]]+0x10 -> +0x28 (byte type 2, vectors at +0x0C/+0x18) merged
822EB844  bl 0x823BF478(W+0x550, min, max)        inner play box (+/-5), W+0x560 = 1
822EB850  pad vectors: min -= ([G+0xAC4] x3), max += ([G+0xAC8] x3)   (vrlimi128 composition, w = 0)
822EB8A0  li r7,1
822EB8AC  lfs f0,0xAE0(r8)   ; 0x82000AE0 = 100.0
822EB8B4  stb r7,0x11(r3)    ; W+0x561 = 1
822EB8F4..93C  W+0x610 = min.x-100, +0x614 = min.y-100 (822EB918), +0x618 = min.z-100,
               W+0x620 = max.x+100, +0x624 = max.y+100 (822EB92C), +0x628 = max.z+100
```
**Numeric check (Showdown Town).** Collision mesh extents from `work/sdt_col.obj` (the OBJ export flips z) are
x -456.21..425.37, y -16.18..214.06, z -522.40..657.17. getAabb adds the 0.05 convex radius, then 100 is added:
-456.21-0.05-100 = **-556.26**, -16.18-0.05-100 = **-116.23**, -522.40-0.05-100 = **-622.45**,
425.37+0.05+100 = **525.42**, 214.06+0.05+100 = **314.11**, 657.17+0.05+100 = **757.22**. All six live values
match exactly, so `[G+0xAC4] = [G+0xAC8] = 0` in this level and the only margin is the 100 at 0x822EB8AC.
The box is therefore computed at load time, which is why the floats appear in no bundle.
The world is created after this runs (the hkpWorld extents equal the W box).

### 3.1 Level bounds setter (secondary path; does not produce the live Showdown Town box) `0x8253DA70` = entry 0x26 of the 110-entry handler table at 0x82126090
The table is dispatched by 0x82514E40 (level start, iterates the list at `L+0xA9C`, `L` = [[[0x82FAB0FC]+4]+0x15B0]+0x1D0)
and by the list walker 0x8253EC78. Handler (r3 = L, r5 = command data):
```
8253DA8C  lwz r11,0x15B4(r31)  ; W = [L+0x15B4]
8253DA94  bl 0x823BF478(W+0x550, data+0x00, data+0x10)   inner "play box": data0-5 / data1+5, 8 corners, W+0x560=1
8253DAA8  stb 1,0x561(W)
8253DAAC  lfs f0,0xAE0(r10)     ; 0x82000AE0 = 100.0
8253DAB0..DAD0  W+0x610..0x618 = data+0x20..0x28 - 100   (min x,y,z)
8253DAD4..DAF4  W+0x620..0x628 = data+0x30..0x38 + 100   (max x,y,z)
```
This handler writes the same fields (data AABB +/- 100 on all axes). The first report claimed that `W+0x561` has
no other writer (from fieldref). That was **wrong**: 0x822EB698 writes it through `W+0x550` +0x11 (3.0).
The asset that carries command 0x26 was not identified (not `aid_script` op 0x26, not marker type 38). In
Showdown Town this path does not determine the live box (patching it changed nothing).

### 3.2 Other users of the same box
* 0x823C8E20 (called by 0x823C8978): builds a clamped spatial grid over `W+0x610/+0x620` (fallback +/-100 box
  when +0x561 = 0). Cell size is set at 0x82533B84..BA0: **50 x 1000 x 50** (ints at 0x82E5E264). Allocation
  is `4 * cells` at 0x823C89AC, plus `0x821E47B0(.., 128, cells/4 + 16)` at 0x823C89C8, so roughly 36 bytes per cell.
  Out-of-range positions are **clamped** to edge cells (0x823C8A74..), not wrapped.
* The inner box (W+0x560/+0x570.., data +/-5) is used for visibility/overlap tests (0x82215440, 0x8227B428).

## 4. Reset path

### 4.1 Game border callback `0x823C1680` (vtable 0x8214236C slot 3; base slot 3 = 0x82C99740)
It gets the entity, then `r29 = entity+0xC` (hkpWorldObject userData = game object).
* If userData != 0, the object is appended uniquely to the array at border+0x30/+0x34.
* Otherwise the entity is appended to the array at border+0x3C/+0x40.
Nothing else happens inside the Havok step.

### 4.2 Flush `0x823C17B8(border, W)`, called from wrapper update 0x821FA090 (0x821FA0E0), per frame from 0x82261DD0
```
823C17EC  lwz r11,0x4C(obj) ; cmplwi ; beq skip      <- only objects with +0x4C
823C17F8  li r4,22 ; bl 0x82241888                   objMsgId_Avatar_EscapedBackground
823C1810  obj->vtbl[3](obj, msg)                      HandleMessage
823C185C  bare entities: 0x8235D048(W+0xB08, entity)  (remove from W's body lists)
```

### 4.3 Handlers of message 22 (`msgcmp.py 22`)
* **banjoactorStrategy** (class registration 0x821263E4: id 6, factory 0x823F3D58, which fills a descriptor with
  +0xC = 0x82257D58; also inherited by the factories 0x8245B180 / 0x8245B378):
```
82257D8C  cmpwi r11,22 ; 82257D90 beq 0x82257FEC
82257FEC  lbz r11,[0x82FAC650] ; beq single      (multiplayer flag)
82257FFC  lwz r3,0x20(r31) ; bl 0x8269F150 ; ...
82258018  li r3,46 ; 8225801C bl 0x82386990        leave session, reason 46 "Player left the broadphase"
82258028  single: mr r3,r31(actor=[this+0x30]) ; 8225802C bl 0x823D6640
```
  `0x823D6640` -> `0x823D66B8`: msg 112 `objMsgId_Scene_ActorDied` (actor, [ac8+0x44C]) sent to the scene via
  0x823C7230, then msg 77 `objMsgId_Actor_DieDeleteNotification` to itself. It then calls 0x82237D28 and
  0x823B7B08 (child cleanup). This is the respawn/"level reset".
* banjobg (id 5, handler 0x82256740 -> 0x82256488): msg 22 is forwarded to the components at +0xAC0/+0xAC4/+0xAC8
  (0x822B08C0).
* 0x821E3B68 ignores 22. 0x824340D0 switches a state machine (0x824344C0(this,0)).
* Vehicle handling of 22 was not traced (vehicles use jump-table handlers). The global patch in 5.2 covers them.

## 5. Patch proposals (big-endian words; check them with `python tools/probe/bounds151.py`)

### 5.0 Enlarge the LIVE box (recommended, replaces 5.1): X/Z margin 2048, Y margin stays 100
Patch the builder `0x822EB698`:
| VA | original | new | meaning |
|---|---|---|---|
| 0x822EB8A0 | 38E00001 `li r7,1` | C0E80AE0 `lfs f7,0xAE0(r8)` | f7 = 100.0 (r8 = 0x82000000 is set at 0x822EB898, before this) |
| 0x822EB8AC | C0080AE0 `lfs f0,0xAE0(r8)` | **C0080C00** `lfs f0,0xC00(r8)` | f0 = [0x82000C00] = 2048.0 (X/Z margin) |
| 0x822EB8B4 | 98E30011 `stb r7,0x11(r3)` | 99230011 `stb r9,0x11(r3)` | flag = low byte of r9 = r1+0x54 (never 0 because r1 is 16-aligned); both readers (0x823C1C64, 0x823C8E28) only test != 0 |
| 0x822EB918 | ED080028 `fsubs f8,f8,f0` | ED083828 `fsubs f8,f8,f7` | min.y keeps -100 |
| 0x822EB92C | EDAB002A `fadds f13,f11,f0` | EDAB382A `fadds f13,f11,f7` | max.y keeps +100 |

f7 is not used anywhere else in the function (0x822EB8A0..0x822EB954).
Expected Showdown Town box: min (-2504.26, -116.23, -2570.45), max (2473.42, 314.11, 2705.22).

Other margins (change only the 0x822EB8AC word): C0080C98 = 1200.0, C0080454 = 512.0, C0080AE8 = 5000.0.
**One-word variant**: only 0x822EB8AC -> C0080C00 gives +/-2048 on all axes. Falling then takes about 2 km
before the respawn, and the grid gets about 5 Y-cells instead of 2.

Grid memory (3.2, about 36 bytes per cell):
| Variant | Cells (x × y × z) | Memory |
|---|---|---|
| today | 23 × 2 × 29, about 1.3k | small |
| 5.0 with 2048 | 101 × 2 × 107, about 22k | about 0.8 MB |
| 5.0 with 1200 | 67 × 2 × 73, about 10k | about 0.35 MB |
| one-word 2048 | about 101 × 5 × 107 | about 2 MB |
| 5000 on all axes | about 500k | far too much |

Verify with `python tools/probe/bounds151.py`. In Xenia, read `W+0x610..0x62C` and `[W+0x6D8]+0x2D0..0x2EC`
after loading.

### 5.1 First proposal, now obsolete: patch the command handler 0x8253DA70
Verified live to have **no effect** in Showdown Town. It is kept only because some other level might use
command 0x26.
| VA | original | new | meaning |
|---|---|---|---|
| 0x8253DAA4 | 38600000 `li r3,0` | C18A0AE0 `lfs f12,0xAE0(r10)` | f12 = 100.0 (the handler's return value is ignored by both dispatchers 0x82514F8C / 0x8253ECBC) |
| 0x8253DAAC | C00A0AE0 | C00A0C00 | f0 = [0x82000C00] = 2048.0 (or C00A0AE8 = 5000.0) |
| 0x8253DAC0 | EDAD0028 | EDAD6028 | min.y uses f12 (100) |
| 0x8253DAE4 | EDAD002A | EDAD602A | max.y uses f12 (100) |

Simplest variant: only 0x8253DAAC -> C00A0C00, which gives +/-2048 on every axis. Falling out of the world then
takes about 2 km before the respawn fires.
**Do not patch the constant at 0x82000AE0**: it is shared (140 code references).

Risks (these apply to 5.0 as well):
* **Grid memory** (3.2): see the table in 5.0. Keep the Y margin small.
* hkp3AxisSweep quantizes positions over the world box (about 15-16 bits per axis). About 5 km per axis gives
  roughly 0.15 unit resolution: coarser broadphase and more narrowphase pairs, but no functional change expected.
* This only helps if the terrain/collision continues past the old edge. Past the collision mesh the player falls.
  Once below data min.y - 100 the same border fires, which is the intended respawn.

### 5.2 Fallback default (levels where neither writer sets W+0x561): X/Z only
| VA | original | new |
|---|---|---|
| 0x8219F5C0 (+x) / 0x8219F5C8 (+z) | 43FA0000 (500) | 45000000 (2048) |
| 0x8219F5D0 (-x) / 0x8219F5D8 (-z) | C3FA0000 (-500) | C5000000 (-2048) |

Shared by the hkpWorldCinfo ctor (also the Havok-side users 0x82988D00 / 0x82989198). Same grid caveat: without
+0x561 the grid uses its own +/-100 fallback, so the grid is unaffected.

### 5.3 Disable the reset (use with care)
| VA | original | new | effect |
|---|---|---|---|
| **0x823C17F4** | 419A002C `beq` | 4800002C `b 0x823C1820` | never send EscapedBackground (all classes, SP + MP); the queue is still cleared |
| 0x8225802C | 4817E615 `bl 0x823D6640` | 60000000 | single player: Banjo does not die (MP kick remains) |
| 0x8225801C | 4812E975 `bl 0x82386990` | 60000000 | multiplayer: no "left the broadphase" kick |
| 0x823C1680 | 7D8802A6 | 4E800020 `blr` | queue nothing; bare entities are also no longer removed |

Risks: EscapedBackground is very likely also the game's only "fell out of the world" rescue (the -Y face of
the same box; no separate kill-plane compare was found). With 5.3, falling through or off the map can soft-lock.
Havok keeps simulating bodies outside the broadphase (AABBs clamped at the border): broadphase cost grows and
collisions against geometry near the border can be missed. Prefer 5.0. Use 5.3 only for testing.

## 6. "Terrain repeats" before the reset
Not found in code. The broadphase/grid code clamps and does not wrap (3.2). Candidates to test in Xenia after
patch 5.0: the background model's far LOD / skydome references (`aid_model_banjox_background_*_references_skydomes_*`)
or a wrapping texture/terrain skirt. Havok's AABB clamping cannot cause visual repetition.

## 7. Open points / runtime checks (Xenia, guest reads)
* The real box per level: `W = [[[[0x82FAB0FC]+4]+0x15B0]+0x1D0+0x15B4]` (or r30 at 0x823C1C64). Read byte
  `W+0x561`, floats `W+0x610..0x61C` / `W+0x620..0x62C`. The value Havok actually uses is at
  `[[W+0x6D8]+0x2D0]` (min) / `+0x2E0` (max).
* Resolved by the live readings: Showdown Town's box comes from 0x822EB698 (3.0), and the world is created
  from it. Still open: whether 0x8253DA70 (command 0x26) ever runs in other levels, and in which order relative
  to 0x822EB698 (breakpoint 0x8253DA70, r5 = data; the list is at `L+0xA9C`).
* Other levels: check that `[G+0xAC4]` / `[G+0xAC8]` (per-level pads) are 0, or know their values. G is r29 in
  0x822EB698 (`0x822EB430` passes r31).
* `objMsgId_Avatar_HitLevelBoundary` (41) has no construction site found by `msgsites.py`. The visible boundary
  walls are a separate, unrelated feature.

## 8. Confidence
* hkpWorldCinfo ctor, layout, +/-500 default, game override of AABB from W+0x610/0x620: **high** (disassembly +
  reflection offsets 0x18C / 0x2D0 agree).
* Live box = collision-shape AABB (+0.05 radius) +/- 100, built by 0x822EB698: **high** (all six live floats
  reproduced to 0.01). Patch 5.0 targets the only instructions that apply the margin: **high** that it changes
  the live box. Not yet verified in Xenia.
* Handler 0x8253DA70 (command 0x26) is a second writer of the same fields. It does not produce the Showdown Town
  box (verified live).
* Border callback -> queue -> EscapedBackground (22) -> actor death / MP reason 46: **high** for the chain,
  **medium-high** that this is the reset the user observed.
* Terrain repetition: **not explained**.
