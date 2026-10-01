# 15.2 Garage limits: build area and part count (static RE)

Status: **static analysis only, nothing verified in Xenia.** Image: `work/default.exe` (VA = file offset + 0x82000000).
Helper scripts added in `tools/probe/`: `immsearch.py` (immediate search), `funcmap.py` (function map with strings/compare
immediates), `argsites.py` (argument value at every call site), `garageinstr.py` (dump of the garage instruction asset),
`const32.py`, `bpscan.py`, `gridscan.py`, and **`garagepatch.py`** (prints and checks the patch words below for any N/P;
`--toml` prints Xenia `[[patch.be32]]` entries).

## Summary

| Limit | Retail value | Where it is enforced | Confidence |
|---|---|---|---|
| Build area | bounding box of *vehicle + held part* must span **< 19 cells per axis** (coordinates 0..18, 19 cells, not 16) | `0x8264AA58` (cursor validate/clamp), 3 × `cmpwi …,19` + 2 × `subfic …,19` + packed constant (19,19,19) | high (code read end to end; matches the 0..18 range of all 275 shipped vehicles) |
| Part count | `blocks(vehicle) + blocks(part) + loose parts in the garage <= 250` | vehicle merge `0x82604118` (`0x826041C0`) and attach test `0x82603FE0` (`0x82604054`); UI message choice at `0x82648150` | high |
| Structures sized by 250 | blueprint picture renderer object (0xC40 bytes, three `[250]` pointer arrays) | `0x824BA700`, allocated `li r3,3136` at `0x825C70E4` | high |
| Structures sized by 19/20 | 20×20×20 byte grid on the stack of the blueprint picture renderer (coordinates ≥ 19 skipped) | `0x824BA700` | high |

## 1. How the garage reports limits (instruction enum)

The garage shows its hint boxes through **`0x82512D80(garage, player, instr)`** (`mulli r11,r5,72` into the loaded
`aid_misc_banjox_garageinstructions_default` asset). That asset is 58 records of 0x48 bytes (u32 id, char[64] game-flag
name, f32 display time) (`tools/probe/garageinstr.py`). The in-code enum is the **asset order**, not the order of the
flag-name table, so the earlier guess (42/44) was wrong:

* 29 = `..._Select_CantMoveCursorFurther`
* 33 = `..._Place_Invalid`
* 34 = `..._Place_ReachedMaxComponents`

`argsites.py 82512d80 5` gives every call site. Relevant ones: `0x8264AE20` (r5=29, in 0x8264AA58), `0x82649518` (r5=34,
in 0x826493F8), `0x8264815C` (r5=33 or 34, in 0x82647DE0).

The garage editor object (r31 in 0x8264xxxx): `+0x1A8` packed cursor cell, `+0x1B4` vehicle being built,
`+0x28C` held part (a vehicle object of its own), `+0x290` held-part orientation index (table 0x82F0FCD0, 16 bytes each),
`+0x2A0` packed position of the held part, `+0x244` nonzero = in the garage (the same editor runs outside it),
`+0x1C` garage instance, `+0x190` player.

**Packed grid vector** (one u32): field A = bits 31..21 (11-bit signed, `srawi 21`), field B = bits 20..11 (10-bit
signed, `rlwinm 11,0,20; srawi 22`), field C = bits 10..0 (11-bit signed). Helpers: `0x8239A1F0` = per-field max,
`0x8239A278` = per-field min.

## 2. Build area (cursor bound)

**`0x8264AA58`** runs after every cursor step (`0x8264AE98` moves `+0x1A8` one cell in one of 6 camera-relative
directions, then calls it; 10 callers in total). Logic:

1. `vmax = max(vehicleSize - (1,1,1), (0,0,0))`; vehicleSize comes from the block container (`0x82602A90` packs
   container `+0x2C/+0x30/+0x34`; `0x826030F0` recomputes them as max block corner + 1 over the vehicle's 0xB0-byte
   block list `+0x1488..+0x148C`, and also sets `+0x40` = block count, `+0x38` = volume). The vehicle grid always
   starts at 0.
2. No held part: cursor is clamped into [0, vmax] (branch to 0x8264AE28) — no 19 check.
3. Held part: part box = cursor(+0x2A0) + rotated part extent → `pMin/pMax`; `min = min(0, pMin)`,
   `max = max(vmax, pMax)`. Then:

```
8264AC08: 7D69AE70  srawi r9,r11,21
8264AC0C: 7D48AE70  srawi r8,r10,21
8264AC10: 7D284850  subf r9,r8,r9          ; max.A - min.A
8264AC14: 2F090013  cmpwi cr6,r9,19
8264AC18: 4098003C  bge cr6,0x8264ac54     ; too big -> clamp
...                                        ; same for field B
8264AC30: 2F090013  cmpwi cr6,r9,19
...                                        ; same for field C
8264AC4C: 2F0B0013  cmpwi cr6,r11,19
8264AC50: 419801D8  blt cr6,0x8264ae28     ; all three < 19 -> OK
```

4. Too big (0x8264AC54..0x8264AE04): the cursor is clamped so that the part stays inside [size-19, 18] per axis,
   using the packed constants `(0,19,19)` = 0x00009813 and `(19,19,19)` = 0x02609813 and two `subfic …,19`;
   then instruction 29 (CantMoveCursorFurther) is shown (`0x8264AE14 li r5,29` → `bl 0x82512D80`).

So the build area is **19 cells per axis, relative** (the vehicle may grow in any direction until its total extent is 19).
All shipped vehicles fit this (grid 0..18). There is no other `19` bound in the vehicle/editor code
(`immsearch.py 19 825f0000 82660000` only finds these five plus unrelated switch tables).

### Patch words (grid N; `garagepatch.py N`)

| Address | Original | For N (≤ 31) | N = 31 example |
|---|---|---|---|
| 0x8264AC14 | 2F090013 `cmpwi cr6,r9,19` | 2F0900NN | 2F09001F |
| 0x8264AC30 | 2F090013 `cmpwi cr6,r9,19` | 2F0900NN | 2F09001F |
| 0x8264AC4C | 2F0B0013 `cmpwi cr6,r11,19` | 2F0B00NN | 2F0B001F |
| 0x8264AD00 | 3D600000 `lis r11,0` | unchanged for N ≤ 31 (else hi16 of (0,N,N)) | 3D600000 |
| 0x8264AD04 | 3D000260 `lis r8,0x260` | hi16 of (N,N,N) = N<<5 | 3D0003E0 |
| 0x8264AD0C | 616A9813 `ori r10,r11,0x9813` | lo16 = N<<11 \| N | 616AF81F |
| 0x8264AD18 | 610B9813 `ori r11,r8,0x9813` | lo16 = N<<11 \| N | 610BF81F |
| 0x8264AD38 | 21290013 `subfic r9,r9,19` | 212900NN | 2129001F |
| 0x8264ADA8 | 216B0013 `subfic r11,r11,19` | 216B00NN | 216B001F |

For N ≥ 32 the packed constants spill into the `lis` halves (the script handles it, e.g. N = 40 → 3D600001 /
3D000501 / 616A4028 / 610B4028).

**Upper bounds for N**
* Field B is 10-bit signed → ≤ 511 in the packed math; A/C ≤ 1023.
* Blueprint coordinates are **u8** (serializer `0x8260FF20`: `fctiwz` of the block position, low byte stored at block +0/+1/+2)
  → **N ≤ 256** or coordinates wrap.
* Blueprint picture renderer `0x824BA700` uses a fixed 20×20×20 byte grid on its stack (`li r5,8000` memset at
  0x824BA820; index `(C*20+B)*20+A`, `mulli …,20` at 0x824BB0EC/0x824BB0F8/0x824BB308/0x824BB318). Cells with a coordinate
  ≥ 19 are skipped (`cmpwi r25/r26/r28,19` at 0x824BB2D0/0x824BB2E4/0x824BB2F8) and the first index is range-checked
  `< 8000` (0x824BB104) → **no memory overflow for any N**, but the blueprint drawing (neighbour/joint marks) is
  wrong/missing for blocks beyond 18 — cosmetic.
* Practical: 19 → 24..31 is the low-risk range (single-word immediates, only the drawing glitch). Physical space in Mumbo's
  garage / camera framing for larger vehicles is **not checked** (no float 19/9.5 constant was traced).

## 3. Part count (250)

Placing a part = merging the held part vehicle into the built vehicle:

* `0x826493F8` (place): `r9 = 0x8250E098(garage, player)` (sum of block counts of the loose parts lying in the garage,
  vector at garage-player `+0x358`) → `bl 0x82568290` (add blocks) → `bl 0x82604118` (merge). On failure and in the garage
  it shows instruction 34 (`0x8264950C li r5,34`).
* Merge `0x82604118`:

```
826041A0: 83B70040  lwz r29,0x40(r23)      ; blocks(target)   (container +0x40)
826041B4: 817F0040  lwz r11,0x40(r31)      ; blocks(part)
826041B8: 7D6BEA14  add r11,r11,r29
826041BC: 7D6BF214  add r11,r11,r30        ; + loose parts (arg 8)
826041C0: 2F0B00FA  cmpwi cr6,r11,250
826041C4: 4099000C  ble cr6,0x826041d0
826041C8: 38600000  li r3,0                ; refuse
```

* Attach test `0x82603FE0` (same sum, loose count from stack arg `0x134(r1)`): `0x82604054 2F0B00FA cmpwi cr6,r11,250`.
  Used by the editor's position search `0x82649528` / `0x826497B0` and by `0x825FF7C0` (outside the garage).
* UI: `0x82647DE0` picks the message when a placement is invalid:
  `0x82648150 2F0B00FA cmpwi cr6,r11,250 ; bgt → keep r5=34 (ReachedMax) else li r5,33 (Place_Invalid)`.
* Other merge callers (same limit applies): `0x8253DD90`, `0x826074C8`.

### Patch words (part limit P; `garagepatch.py 19 P`)

| Address | Original | Proposed (P = 500 example) |
|---|---|---|
| 0x82604054 | 2F0B00FA `cmpwi cr6,r11,250` | 2F0B01F4 |
| 0x826041C0 | 2F0B00FA `cmpwi cr6,r11,250` | 2F0B01F4 |
| 0x82648150 | 2F0B00FA `cmpwi cr6,r11,250` (message only) | 2F0B01F4 |

The immediate is signed 16-bit → P ≤ 32767.

### What is (and is not) sized by 250

Dynamic / not limiting:
* Vehicle block list: std-style vector `+0x1488` (begin) / `+0x148C` (end), element 0xB0, **u16 count at `+0x1498`**
  (→ 65535).
* Blueprint (aid_vehicle) format: u16 count (`sth` at `0x8260FF58`), 0x7C header + 0x24 per block; the save/clone buffers are
  allocated as `0x7C + 36*count` (`0x826102E8`, `0x82610428`, `0x82511FF4`, `0x825CFF78`) — no fixed 250-block buffer
  (250 blocks = 9124 bytes).
* Editor array `+0x1B8` is created with `0x82213AB8(arr, 20, 250, 0)` — 250 is only the grow step; `0x82247EC0` grows it.
* Blueprint loader/vehicle builder paths: no count check found (only the 250 compares listed above exist in the
  vehicle/garage code).

**Fixed — must be handled before raising P:**
* **Blueprint picture renderer** (`0x824BA700`, constructed by `0x824B9540`, used by the blueprint lists
  `0x825C6F30`, `0x825C7B18`, `0x825DB878`): object allocated with `li r3,3136` (0xC40) at `0x825C70E4`; the ctor
  memsets three arrays of 1000 bytes at `+0x84`, `+0x46C`, `+0x854` (0x824B9600..0x824B9634), and the loader writes one
  pointer per block `stw …,0x0/0x3E8/0x7D0(r22)` (0x824BA97C/0x824BA998/0x824BA9B8) with the count taken unchecked from
  the blueprint's u16 (`0x824BA7FC lhz r10,0(r11)`). **A blueprint with more than 250 blocks overflows this heap
  block** as soon as it is shown in a Load/Save/Humba blueprint list.
  Candidate guard (skip the picture for > 250 blocks; `garagepatch.py` adds it automatically when P > 250):

  | Address | Original | Proposed |
  |---|---|---|
  | 0x824BA800 | 2C0A0000 `cmpwi cr0,r10,0` | 280A00FA `cmplwi cr0,r10,250` |
  | 0x824BA808 | 40820018 `bne cr0,0x824BA820` | 40810018 `ble cr0,0x824BA820` |
  | 0x824BA80C | 7E9FA378 `mr r31,r20` | 929F007C `stw r20,0x7C(r31)` (count := 0) |

  Effect: > 250 blocks → the asset is released, count stored as 0, function returns non-zero, so the list draws an empty
  picture (loops over `+0x78/+0x7C` in `0x824BB508` and `0x824B9660` see count 0). Side effect: a 0-block blueprint now
  takes the normal path (none ship). Untested.

## 4. Risks

* Part limit > 250: per-block game objects, models, Havok bodies and constraints grow linearly — object-pool,
  physics or memory limits elsewhere were **not** analysed; test 300/500 before going higher.
* Multiplayer: vehicles are exchanged as blueprints; the host "maximum vehicle part limits" option and network message
  sizes were not traced. Keep patched vehicles out of online play.
* Blueprints saved with > 250 blocks or coordinates > 18 would break the retail game (renderer overflow) if loaded
  without the patch.
* The garage/test-track room and camera were not checked for bigger vehicles (clipping into walls possible).
* The 0x8264AA58 clamp branch is only partly decoded field by field; all 19s are changed consistently, which keeps its
  meaning (window [size−N, N−1]).

## 5. Unresolved

* Save-game side: where user blueprints are stored (`0x82576320` → `0x825DC1C0` UI save path; earlier leads 0x825D0510,
  0x82610498) and whether the save container limits blueprint size.
* Whether any runtime system assumes ≤ 250 blocks per vehicle (no other 250/1000-byte arrays found by
  `immsearch`/`fieldref 3e8`, but not proven).
* Garage camera / floor-guide sizing for larger build areas.

## 6. In-game test of > 250 parts (2026-09-27)

Tested in Xenia with the part-limit compares at 0x82604054 / 0x826041C0 / 0x82648150 set to 400 (`cmpwi …,0x190`) and
a renderer guard at 0x824BA800..0x824BA81C (picture skipped for > 250 blocks):

* Garage: a vehicle grew to 294 blocks (stock stops at 250 with "maximum number of parts"). **OK**
* A 253-block data blueprint (initial trolley) spawns and drives in town. **OK**
* The 294-block blueprint saves ("Blueprint saved") and is listed in Load Blueprint with an empty picture (guard works). **OK**
* Change Vehicle (Showdown Town, pause → Change Vehicle → Your Blueprints → A, A) with the 294-block blueprint:
  **freezes** on the white vehicle-swap flash, Mumbo's "LOADED VEHICLE R…" (= `dialog__loadvehicle_mayhaveproblems1`
  with `ingame__loadvehicle_notenoughparts` "requires more parts than you have") stops mid-typing, the frame-time
  global [0x82FAC644] stops changing, Xenia stays alive.

### Cause (static analysis): fixed `[250]` stack array in the vehicle spawner

Every vehicle built from a blueprint goes through **`0x825697C8`** (player-vehicle spawn; 15 callers, incl.
`0x8251D458` = spawn with parts-inventory check). It keeps a per-block "drop this block" array **on its stack at
`r1+0x70`, exactly 1000 bytes = `u32[250]`**: frame `stwu r1,-0x4B0` at 0x825697D0, saved r22..r31 + LR start at
`r1+0x458` (`bl 0x82BB43E0` = std r22,-0x58 of the old SP). It passes that array to **`0x8252FD50`** (its only caller;
challenge "Limited Choice"/weight restriction stripper), which first does

```
8252FD78: A2F40000  lhz r23,0x0(r20)       ; block count from the blueprint (u16)
8252FD7C: 56FF103E  rlwinm r31,r23,2,0,31  ; count*4
8252FD84: 4BD1B8D5  bl 0x8224b658          ; memset(callerArray, 0, count*4)   <- unconditional
8252FD88: 2F170000  cmpwi cr6,r23,0
8252FD8C: 419A0330  beq cr6,exit
8252FD94: 896BC650  lbz r11,[0x82FAC650] ; != 0 -> exit (town), [r30+0x34]==0 -> exit
```

So for `count > 250` the memset runs past the array into 0x825697C8's register save area:

| blocks | bytes zeroed past the array | effect |
|---|---|---|
| 251..252 | saved r22 | harmless for 0x8251D458 (r22 not used after the call) |
| **253** (tested) | saved r22, high half of r23 | harmless → matches "253-block vehicle spawns fine" |
| 254..270 | saved r23..r31 | caller continues with zeroed r23..r31 (e.g. `release(0)`, `free(0)` in 0x8251D458) |
| **≥ 271** | **saved LR (`r1+0x4A8`) = 0**, then the caller's frame (back chain, locals) | epilogue `0x82BB4430` loads LR = 0 → `blr` to **0x00000000** |
| 294 (tested) | 0x70..0x508 | the swap never returns → game loop stops |

The spawn happens a few frames after the swap dialog starts, which is why the text stops mid-typing. The loop in
0x825697C8 (`0x8256985C`, `lwz r11,0(r22); bne skip`) also reads that array for every block, so it must really hold
`count` entries (making 0x8252FD50 skip the memset would leave garbage and drop blocks).

The "requires more parts than you have" message is a separate warning. `0x825DD2E0` → `0x8251DBB0` (inventory check)
failed and set flag 0x1 in `+0x102C` (a warning in town because byte [0x82FAC650] != 0). Most likely the old vehicle's
253 cubes still count as used, but this was not verified. `0x8251D458` then builds the list of missing blocks
(dynamic vector from `0x8231E3D0(4,16)`, grown by `0x822069C8`, no fixed size), and the spawner places them as
missing blocks through `+0x64`. There is no fixed limit on that path.

### Other `[250]` per-block arrays found (whole-image scan for 1000-byte stack gaps)

* **`0x8252FD50`** itself: `float[250]` at `r1+0x1E0` and `{int,float}[250]` at `r1+0x5D0` (frame 0xE30, top = 0xDA0 =
  end of the pair array). Used only when a challenge restriction set is active (`[r30+0x34] != 0` and
  [0x82FAC650] == 0). With > 250 blocks it overflows its own saved FPRs, GPRs and LR.
* **`0x825F15B0`** (vehicle break-up into n groups, used by the self-destruct path `0x826337D0` state 2 in 153_destruct):
  `u32 visited[250]` at `r1+0x80` (`li r5,1000` memset at 0x825F1654, frame 0x4D0, save area at 0x470). Indexed by
  block index up to `count` here and in the recursive flood fill `0x825F1458` (`stwx r24,r30,r25`). With > 250
  blocks, blocks 250+ are never grouped because they read saved registers as "visited", and with ≥ 275 blocks the
  saved LR is set to 1, which crashes.
* No other per-block `[250]` stack or heap arrays turned up in the vehicle code (0x825xxxxx–0x826xxxxx). The remaining
  1000-byte stack gaps (0x8265BEE0, 0x826683D0, 0x823E4638) are network/UI code that does not touch blocks. None of the
  loops reachable by `bl` from the spawn and load paths (`0x825697C8`, `0x8251D458`, `0x8250E188`, `0x8250E338`,
  `0x825D2140`, `0x825D26B0`, depth 6, 885 functions) has a byte-sized (`clrlwi 24`) loop counter or a 250 bound. The
  scan does not follow virtual (`bctrl`) calls.

### Patch words (P = part limit; values for P = 400)

Frame sizes are `align16(...)` of the formula. All stay below 4 KB (no stack probe needed) except the optional B2.

**A. Required: spawner frame big enough for P blocks** (`F = align16(0x70 + 4P + 0x58)`, P=400 → 0x710; P ≤ 974 for < 4 KB)

| Address | Original | New (P=400) |
|---|---|---|
| 0x825697D0 | 9421FB50 `stwu r1,-0x4B0(r1)` | 9421F8F0 `stwu r1,-0x710(r1)` |
| 0x82569B94 | 382104B0 `addi r1,r1,0x4B0` (only epilogue) | 38210710 `addi r1,r1,0x710` |

(The only other stack offsets in 0x825697C8 are 0x50/0x60/0x70, below the array, and it has no stack arguments.)

**B1. Recommended: 0x8252FD50 skips the restriction stripper for > 250 blocks** (after the memset, so the
caller's array is still zeroed = no block dropped). Count 0 then runs the stripper, and every loop there is guarded
by `count > 0`.

| Address | Original | New |
|---|---|---|
| 0x8252FD88 | 2F170000 `cmpwi cr6,r23,0` | 2B1700FA `cmplwi cr6,r23,250` |
| 0x8252FD8C | 419A0330 `beq cr6,0x825300BC` | 41990330 `bgt cr6,0x825300BC` |

**B2. Alternative to B1: enforce restrictions up to P** (float array stays at 0x1E0, pair array moves to
`pa = 0x1E0 + 4P` rounded up to 8, frame `align16(pa + 8P + 0x90)`. For P=400: pa = 0x820, frame 0x1530, above 4 KB,
no stack probe, 256 KB thread stacks)

| Address | Original | New (P=400) |
|---|---|---|
| 0x8252FD60 | 9421F1D0 `stwu r1,-0xE30(r1)` | 9421EAD0 |
| 0x8253001C | 3BE105D4 `addi r31,r1,0x5D4` | 3BE10824 |
| 0x8253006C | 386105D0 `addi r3,r1,0x5D0` | 38610820 |
| 0x82530080 | 396105D0 `addi r11,r1,0x5D0` | 39610820 |
| 0x825300BC | 38210E30 `addi r1,r1,0xE30` | 38211530 |

**C. Break-up flags** (`F = align16(0x80 + 4P + 0x60)`, P=400 → 0x720)

| Address | Original | New (P=400) |
|---|---|---|
| 0x825F15B8 | 9421FB30 `stwu r1,-0x4D0(r1)` | 9421F8E0 `stwu r1,-0x720(r1)` |
| 0x825F1654 | 38A003E8 `li r5,1000` | 38A00640 `li r5,1600` (4P) |
| 0x825F17E0 | 382104D0 `addi r1,r1,0x4D0` | 38210720 |

Confidence:
* **Very high** that the overflow exists. It is plain code, and the numbers match both tests: 253 blocks only
  touch saved r22/r23, 294 blocks zero the saved LR.
* **High** that it causes this hang. Only the way it shows up (a freeze instead of a crash dialog) depends on what
  Xenia does with a branch to 0x00000000.
* Not tested in game. A + B1 (+ C) should let the 294-block blueprint load through Change Vehicle. After that, per-frame
  systems with more than 250 blocks, and swapping away from a big vehicle, still need an in-game test (virtual calls
  were not scanned).

### 6.1 Follow-up: A + B1 + C applied, freeze when the Town Blueprints list opens (2026-09-27)

In-game result with A + B1 + C, limit 400 and the renderer guard:

* A 295-block data blueprint (Trolley Mk.7 = `golfcartcomplete`: 7x6x7 light cubes + seat, `work/bigcube295.bin`)
  spawns at game start in Showdown Town, and Build Vehicle (garage) works with it. **Spawner fix confirmed.**
* **New freeze:** pause → Change Vehicle → *Town Blueprints* (7 entries, the 7th is the 295-block Mk.7).
  The list opens with every entry name blank and an empty preview, then the picture stops changing and input is
  ignored. Before the patches, the same list with a 253-block Mk.7 worked. A 294-block saved blueprint in *Your
  Blueprints* showed its name and an empty preview.

**Not found statically.** The code the list runs was read and found safe for 295 blocks:

* **Category and entry setup**
  * `0x825DE560`: golfcart category from `unlockable_golfcarts`, entry vector `0x822C0888`.
  * `0x825DD010`: per-entry setup. Data blueprints (id > 1000) start the async load `0x822C0CE8` immediately, so all
    7 load when the list opens.
  * `0x825DD8E8`: load queue, at most 10 cached.
* **Per-frame update `0x825DB5B0`**
  * It runs the validator `0x825DD2E0` for every loaded entry: name (`0x825680A8`), stats copied from the header
    (`0x82568170`), parts check `0x8251DBB0` → `0x8251D3E8` / `0x8251D630` / `0x8251DC60` / `0x8251D828` (no fixed
    arrays; the loops are bounded by the u16 count and the inventory vector), restriction check `0x8252FBB0` (`[45]`
    category array, challenges only).
  * Then the selected entry: `0x825DB878` → parts list `0x825DDCA0` (growable vector, `vehicleBlockOrder` sort),
    stat bars `0x825DC3B0` → `0x82288230` / `0x822912E8` (threshold search, bounded), picture object `0x824B9540`
    (the loader `0x824BA700` is called only from this constructor, so the guard covers it).
* **Settings shared by both lists.** Town and custom lists get the same check flags. The category descriptors differ
  only at +0x44 (custom = 1), which maps to validator +0x68 and matters only for entries that failed to load. So the
  parts and restriction checks ran for the 294-block *Your Blueprints* entry too.
* **Whole-image scans (no new hits)**
  * 250/256-sized stack arrays in every function that walks a vehicle block list (`+0x1488` / `+0x1498`) or a
    blueprint (0x24 stride).
  * Byte-sized loop counters (`addi …,1; clrlwi …,24; cmpw` against a register) in 0x824B0000–0x82670000.
  * `memset` sizes that are multiples of 250 or 256.

  Nothing new turned up on the list path. The only per-block u8 values are the blueprint coordinates.
* **Difference between the two blueprint files.** The 253- and 295-block files differ only in the count and one more
  z layer: same header, same part ids, coordinates 0..6.

**Most likely mechanism (unproven): a synchronous asset load that fails or never finishes.**

* `0x8225FB98` ("get asset", used by the validator for data blueprints, by `0x825DDCA0` and by the picture
  constructor) sends a request (`0x82221020`). If the asset is not resident it waits:
  `while (0x82224908(req,1) == 258) 0x8223C650();` (0x8225FC70..0x8225FC84). While waiting, `0x8223C650` only keeps
  the display alive, so the picture freezes.
* If the load fails, its completion callback **`0x82273078` sets the fatal flag [0x82FAB13A] = 1 and calls
  `0x82714718` → `XamShowDirtyDiscErrorUI` (import thunk 0x82D0853C)**. With the flag set, `0x8223C650` spins forever
  at 0x8223C694..0x8223C6B8. Xenia Canary answers the import with a modal ImGui "Disc Read Error" box, then `exit(1)`
  (`xam_ui.cc`). If that box was not visible, the game looks frozen with no crash.
* A limit between 253 and 295 would then come from memory or loader limits (the 295-block vehicle is alive in the
  world), not from a fixed array. **Not verified.**

**Next step (dynamic, read-only): new `tools/xenia/xstack.py`.**

* Reproduce the freeze, then run `python tools/xenia/xstack.py`.
* It reads every guest thread stack listed in `xenia.log`, follows the PowerPC back chain (SP → caller SP, saved LR
  at caller SP − 8, LR must follow a `bl`) and prints the return addresses with their functions.
* How to read the result:
  * A main-thread chain through `0x8225FB98` / `0x8223C650` confirms the load wait or the disc-error loop.
  * Any other chain names the looping function directly.
* Also look at the real Xenia window for a "Disc Read Error" box, and at `xenia.log` after closing it.

**Diagnostic patch (only for this test, do not ship):** make a failed synchronous load return NULL instead of
entering the fatal loop. All three callers in the list path already handle NULL.

| Address | Original | Test value |
|---|---|---|
| 0x82273090 | 409A0040 `bne cr6,0x822730D4` (fatal unless request +0x138 != 0) | 48000044 `b 0x822730D4` (never fatal) |

**Confidence**
* Low (about 30 %) that the load-failure/wait path is the cause.
* High that the list code listed above is not the cause.
* A + B1 + C stay correct and needed: the game-start spawn of 295 blocks works.

### 6.2 Resolved: the freeze was caused by the first renderer guard (2026-09-27)

**Cause.** The first guard (0x824BA800..0x824BA81C) overwrote 0x824BA810..0x824BA81C. That range is the loader's
**shared exit** (`mr r3,r11; bl 0x82235E68; mr r3,r31; b 0x824BB4E0`), and the success path jumps there from
0x824BB4D8 with r31 = 1. With the guard in place, every successful preview load ran `stw r20,0x7C(r31)` (a store to guest
address 0x7D) and then returned 0. That is why previews were empty for every blueprint, and why the Town list stalled.
Found dynamically:

* `tools/xenia/xstackdown.py` walks the main-thread stack from its outermost frame. The walk ends in the picture loader
  0x824BA700, called from the list update 0x825DB5B0.
* A host-memory scan for the guest PPC context (`tools/xenia/xctx.py`) found r1 = 0x7018C610 (the loader's own frame) and r31 = 1.
* The frame timer [0x82FAC644] was frozen, and the disc-error flag [0x82FAB13A] was 0, so the §6.1 load-failure theory is
  ruled out.

**Fix.** The count is clamped to 250 and the exit tail is left untouched. `mr` and the null test are merged into `or.`,
which frees one slot:

| Address | Original | New |
|---|---|---|
| 0x824BA7EC | 7C6B1B78 `mr r11,r3` | 7C6B1B79 `mr. r11,r3` |
| 0x824BA7F0 | 91610064 `stw r11,0x64(r1)` | unchanged |
| 0x824BA7F4 | 2B0B0000 `cmplwi cr6,r11,0` | 41820CE8 `beq 0x824BB4DC` |
| 0x824BA7F8 | 419A0CE4 `beq cr6,0x824BB4DC` | A14B0000 `lhz r10,0(r11)` |
| 0x824BA7FC | A14B0000 `lhz r10,0(r11)` | 2B0A00FA `cmplwi cr6,r10,250` |
| 0x824BA800 | 2C0A0000 `cmpwi r10,0` | 40990008 `ble cr6,+8` |
| 0x824BA804 | 915F007C `stw r10,0x7C(r31)` | 394000FA `li r10,250` |
| 0x824BA808 | 40820018 `bne 0x824BA820` | 915F007C `stw r10,0x7C(r31)` |
| 0x824BA80C | 7E9FA378 `mr r31,r20` | 48000014 `b 0x824BA820` |

Why clamping is safe:

* The loader reads the blueprint count only once (0x824BA7FC). Every later loop uses +0x7C.
* The block records are walked by r19 (+0x24 per block), so a larger blueprint simply draws its first 250 blocks.
* The three u32[250] arrays of the picture object (+0x84/+0x46C/+0x854) are indexed below +0x7C, so they stay in bounds.
  The 8000-byte stack buffer is the 20×20×20 cell grid (§2) and is not affected by the count.
* A blueprint with 0 blocks now takes the build path with an empty loop; no such blueprint exists.

**In-game result.** Tested with the patch file written by `NB.Cli exe-mods`, together with the build-area and Showdown
Town mods. Registered as `ExePatches.VehiclePartLimit400`.

* Previews work again in *Your Blueprints*: the 253-block cube is drawn.
* The *Town Blueprints* list opens with all 7 names, and the 295-block entry previews its first 250 blocks.
* Selecting it swaps the player into the 295-block cube, and the game keeps running.
* Changing back to the Trolley Mk. 1 works.

## 7. Practical maximum of the build area (in-game measurement, 2026-09-27)

**Test.** `garagepatch.py 100 250` (build-area limit 100), trolley Mk.1 in Mumbo's Motors, a light cube held, pushed with
`tools/xenia/garagepush.py` until the held cell stops moving (editor +0x2A0 read from guest memory).

**Result.** The editor's compare no longer limits the cursor. A second, spatial bound does:

| Direction | Stops at |
|---|---|
| C | −27 … +32 (a 60-cell span) |
| B | up to +35 |
| A | down to −30 |

The cube is then next to the garage ceiling and walls. With disconnected cubes placed, the vehicle container reached 33
cells in C (container +0x34), so the editor accepts vehicles longer than 31 cells.

**Limits for the build area:**
1. Code limit: 19 in stock, raised by the mod.
2. Garage room: about 60 cells per axis around the vehicle. This bound was not located in the code; raising it would
   put parts outside the room.
3. Blueprint coordinates are u8, so at most 256 cells.
4. The blueprint picture only draws cells below 19. This is cosmetic.

**What ships.**
* The shipped mod keeps 31. Only that value was verified with parts attached beyond the old edge.
* Values up to about 60 can be generated with `garagepatch.py N`, but saving or driving a vehicle longer than 31 cells
  was not tested.
* The garage does not require connected parts while building. It warns "part not connected", and the behaviour of
  disconnected groups on exit was not tested.

**Limits for the part count:**
* The fixed 250-entry arrays are the constraint (§5–6.2). 400 is the tested value.
* Going higher only needs larger stack frames in the spawner and break-up code, the same patch shape as A and C.
* Physics and render cost grow with the part count. The game kept running with the 295-block vehicle; frame rate was not measured.

## 8. Configurable part limit (2026-09-28)

`ExePatches.VehiclePartLimit(P)` (id `vehicle-part-limit:P`, P = 251..2000) generates the part-limit mod for any
limit, from the verified 400-part mod:
- the three `cmpwi …,P` compares;
- the same preview-loader clamp (previews still draw 250 blocks);
- the spawner and break-up stack frames grown by align16(4·(P−250)), with the break-up memset set to 4·P.

`ExePatches.GarageBuildArea(N)` (id `garage-build-area:N`, 20..60) does the same for the build area. In NB Studio,
Mods → "Vehicle part limit…" / "Garage build area…" take a number and rewrite the Xenia patch file. A workspace keeps
only one mod of each family (`ExePatches.ResolveAll`).

**Verified in Xenia (P = 600).** The Trolley Mk.7 blueprint (`general_golfcartcomplete`, bundle 685374) was replaced
by a 577-block data blueprint (9×8×8 light cubes + seat), with the six trolley-upgrade flags preset. The game spawns
it in Showdown Town and keeps running; moving its rigid body (teleport) shows it is a live physics object.

**Not tested for P = 600.** Garage editing, Change Vehicle with it, and driving (the cube has no wheels).
