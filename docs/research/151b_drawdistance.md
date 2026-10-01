# 15.1b Draw distance, LOD culling, "repeating terrain", LOGBOX world source (static RE, 2026-09-27)

Static analysis only. Xenia was not run, so none of the patches below are verified in game.
Image: `work/default.exe` (VA = file offset + 0x82000000). New helper: `tools/probe/lodtables.py <decompressed bundle> [filter]`
(it prints every model's LOD groups and a histogram of cull distances).

## 1. Summary

* **Why scenery disappears:** each model carries its own LOD table in the rendergraph chunk (chunk 30). The table is a
  list of switch distances. When the **last level has no nodes**, the model is **not drawn** past that distance.
  Showdown Town props cull at 83–800 units. The most common values are 200 (50 models), 300 (41) and 400 (30).
  The world's base mesh (`*_default`), the hinterlands and the skydomes have **no LOD table**, so they are never
  distance-culled.
* **Where the engine uses the table:** `0x82B67FE0` is the LOD selector. Its two callers pass a hard-coded LOD scale
  of 1.0 and a bias of 0.0. **One global patch (2 words) multiplies every LOD switch and cull distance by 4.**
  See section 4.1.
* **"Terrain repeats" far out:** no code or data tiles or wraps geometry. The best explanation is this: the hinterland
  mesh is finite (Showdown Town: at most ±2152, flat sea plane on the −z side). Its ground uses textures that tile
  every 40–90 units, and 1352 identical billboard trees are scattered over it. Behind it is the skydome, whose sky
  texture has a painted strip of fields and treeline along the bottom edge. Section 5 has the details and a test.
* **LOGBOX world:** the act bundle `0b6509` lists its dependent bundles in its manifest, and **`b4a7c4` (with
  `aid_model_banjox_background_cpu_default`) is dependency #6**. Most dependency opens show in xenia.log only as a
  bare `ResolvePath(\bundle\4f)` line, so the log does not name every bundle. Also, `Workspaces\dev\game\Bundle\4f\b4a7c4`
  and `b837e8` are currently **byte-identical to the FRESH originals**, so the "sink by 500" edit is not present in
  the dev game folder. Section 6 has the details.

## 2. LOD data format (verified on many models, parser in `tools/probe/lodtables.py`)

Chunk 30 is `u32 ptr -> rendergraph header` (starts with the string `rendergraph 26.09.07.0039`). Relative to that
header:

| Offset | Meaning |
|---|---|
| +0x140 | u32 number of LOD groups (0 = model has no LOD, always drawn) |
| +0x144 | ptr -> groups, 0x14 bytes each: `u32 nLevels, ptr levels, f32 centre[3]` |
| level (16 B) | `f32 switchDistance, u32 0, u32 nodeCount, ptr -> nodeCount pointers to rendergraph nodes` |

The rendergraph nodes are the Maya `LODBIT_|transform1|<mesh>_<lod list>_` groups. `window_0_` is LOD 0 only and
`window_0_1_2_` is in every LOD. The `+16` word of draw op 0x30 is **not** a LOD level. It is a node or draw index
(values up to 263 in Showdown Town). LOD selection works on nodes, not on draws.

Examples (Showdown Town bundle `234cec`, offsets in the model's `.data`, floats are big-endian):

| Model | Level array | Levels (distance → nodes) | Cull word |
|---|---|---|---|
| `..._building_windows_1window1` | 0x8458 | 0→1, 15→1, 70→1, **200→0** | `.data+0x8488` = 43480000 (200.0) |
| `..._props_cafeparasole` | 0x7E7C | 0→1, 15→1, 40→1, **150→0** | `.data+0x7EAC` = 43160000 (150.0) |
| `..._props_cafetable` | 0x5298 | 0,15,30,60 → 1; **250→0** | `.data+0x52D8` |
| `..._props_cafechair` | 0x74D4 | 0,15,30,60 → 1; **200→0** | `.data+0x7514` |

Survey (`lodtables.py`):

| World bundle | Models | No LOD table | Groups that cull | Cull distances |
|---|---|---|---|---|
| Showdown Town `234cec` | 330 | 46 | 223 | 83.5–800 (most common 200/300/400) |
| Nutty Acres `13b0ff` | 74 | 12 | 14 | 35–1500 |
| LOGBOX `b4a7c4` | 52 | 10 | **0** | none. Every group keeps its last level (e.g. wirebunch 0/150/260) |

## 3. Engine side (evidence)

* Model load `0x82308078` looks up chunks by id (`0x82308028`). It stores ch30 at M+0x54 and runs the rendergraph
  setup `0x823086B0`.
* Scenery instances (chunk 12) are built into runtime records of 0x160 bytes by `0x823174A0` / `0x82318A60` (all n
  instances at load, none streamed). Record fields +0x2C and +0x34 are physics floats, not LOD values.
* **LOD selector `0x82B67FE0(rg, ..., r7 = min level, r8 = max level, f1 = scale, f2 = bias)`**, for each group
  (`rg+0x140` / `rg+0x144`, stride 0x14):
  * `0x8224CB10`: worldView = world × view. `0x829A04E8` transforms the group centre (+8).
  * `metric = |viewSpace.z| * f1 / |worldView column 2| + f2`. The value is view **depth**, not radial distance. It
    is divided by the instance scale, so enlarged copies switch later.
  * It picks the highest level whose `switchDistance <= metric`, clamped to [min, max]. If that level has 0 nodes,
    nothing is drawn (`0x82B67D88` switches the node set, and `0x82B67E98` sets the cross-fade byte).
* Callers:
  * `0x82318E18` (used by the scenery/viewport pass `0x823B2C58` and by 7 other sites).
  * `0x823AB368` (called by `0x823BF888`).
  * **Both load f1 = 1.0 (`lfs f1,0xD0C(r10)`) and f2 = 0.0 as literals.**
* `0x823B2C58` also forces **min level 1** in split screen (viewport count > 1, or flag at viewport+0xE1C). It forces
  LOD 0 (max = min = 0) when a node flag is set or when `[vp+0xCE4]` is set. The viewport float `vp+0xE40`
  (initialised to 26.0 at `0x821FF86C`) is passed on but only reaches `0x823AB3E0` (a clamped 0..12 value, probably
  an animation or update LOD). It does not affect the LOD choice.
* Far clip plane: **not identified.** No large far-plane literal is used by the projection code I found. The skydome
  (radius ≈ 2152–2257 around the origin) is drawn, so the far plane is at least that far from the start position.
  Candidate for a later test: `cameraModeGame` objparams +0xC4 = 10000.0 in all 20 instances (unconfirmed).
* Actor/marker activation radius: not found in this pass. Scenery and background never stream in or out by
  distance. Chunk-12 instances all exist from level load, and only the per-model LOD table hides them.

## 4. Proposals

### 4.1 Global: scale all LOD and cull distances (exe patch, recommended first test)

| VA | Original | New | Effect |
|---|---|---|---|
| 0x82318E4C | C02A0D0C `lfs f1,0xD0C(r10)` (1.0) | **C02A0CA0** `lfs f1,0xCA0(r10)` (0.25) | scenery LOD/cull distances ×4 |
| 0x823AB3A0 | C02A0D0C | **C02A0CA0** | same for the second path (0x823AB368) |

`r10 = 0x82000000` at both sites (`lis r10,0x8200` just before). Other literals in reach: 0.5 at 0x82000C74
(`C02A0C74`, ×2), 0.1 at 0x82000C08 (`C02A0C08`, ×10), 0.0625 at 0x82000B58 (`C02A0B58`, ×16). Do **not** edit the
shared literal 0x82000D0C.

Expected result: Showdown Town props stay visible to 800–3200 units (from 200–800). The cost is more draw calls and
higher detail far away. Low-detail props also switch to lower LODs later, so GPU load rises. ×4 is a good first test,
and ×2 (`C02A0C74`) is the cautious choice. Confidence that it changes culling: **high** (single selector, literal
argument). Performance in Xenia: unknown.

To make culled props never disappear (not just later), a hack is possible but not recommended: min level =
nLevels − 2. Prefer the data edit in 4.2.

### 4.2 Per model: data edit

Change the cull level's distance (the level with nodeCount 0) to 1e6 (`49742400`), or to any larger value. The last
real LOD then stays forever. Example: `1window1` `.data+0x8488` 43480000 → 49742400. `lodtables.py` prints every
level array. The distance of level k is at `levels + 16*k`. This can be done for a whole bundle in one pass and needs
no exe change. The tool's CAFF writer already round-trips these files.

### 4.3 LOGBOX needs nothing

No LOGBOX model culls (every last level has nodes). Distant LOGBOX geometry is only lowered in detail.

## 5. "Terrain repeats" far away

Facts:

* **No wrap or tile code.** The Havok broadphase and the world grid clamp positions (see 151_bounds.md §3.2). No
  chunk-12 grid exists: the hinterlands is one instance `|REFERENCE_outerarea|` at the origin, and the Nutty Acres
  "INSTANCE_Group" lists are small prop groups. No code moves scenery with the camera.
* Showdown Town hinterlands (`..._showdowntownreferences_hinterlands`, 34 draws, no collision: its havok asset is 0x38
  bytes) is a finite ring around the town:
  * a hilly land mass mainly on +z / ±x, up to about ±1330 x and +1527 z;
  * a **flat plane at y = 12** covering x ±2152, z −2152..72 (sea side);
  * grass/dirt/mud draws with UVs that **tile every 40–90 units** (e.g. `grasssurface_color` 43–80 units per repeat);
  * **5408 vertices = about 1352 billboard trees** (`showdowntown_grassfoliage_bgtree`, two textures).
  * Top view: `work/tex151b/sdt_topdown_hinterlands.png` (white box = collision/playable area).
* Skydomes (`..._skydomes_morning/afternoon/evening/night`, ±2152, y −205..1312) are **not** placed by chunk 12. The
  time-of-day scripts select them, for example `aid_script_banjox_showdowntown_afternoon` command op 0x2C with the
  model id 0x04CCBFAA. The afternoon/midday sky `spiralmountain_materials_skys_blue03` (2048×512) has a **painted
  strip of fields and treeline along its bottom edge** (see `work/tex151b/sheet.png`).

Best explanation (medium confidence):

1. With world-bounds-2048 the player can reach about ±2500. Past the collision mesh the ground under the player is
   only the flat plane or hinterland (no collision, so the vehicle is normally falling).
2. The hinterland's short texture tiling and the mass of identical tree billboards look the same everywhere at speed.
3. Beyond about 2152 there is no geometry. The only "land" left is the skydome's painted horizon strip, which
   **always stays at the horizon if the skydome is drawn around the camera**. So the same fields and treeline stay
   in front however far you drive.

Whether the skydome follows the camera is **not proven statically**, because its render code was not found.

Test in Xenia with the bounds mod:

* Drive toward +x/+z and watch whether the sky's clouds show parallax.
* Or temporarily replace the skydome's `blue03` texture with a plain colour (`tex-replace`). If the "repeating
  terrain" vanishes, it came from the sky strip. If it stays, it is the hinterland's tiled ground and trees.

For other worlds, check the same things: the size of `*_references_skydomes_*` / outer-cloud models and the extent of
the world mesh (Nutty Acres chunk models reach about ±982, and its cloud domes are 600–730 across).

## 6. LOGBOX 720 (act 1): where the world geometry comes from

* Act bundle `0b6509` has no background base mesh, only cpu reference props, cut-scene props (streamed `caff_04…`/
  `caff_05…` in `Bundle/50/0b6509`) and scripts.
* Its `manifest` (.data, 2580 bytes) holds 310 `(assetId, ordinal)` pairs, then at **.data+0x9D0** a list of 17
  bundle ids (type byte 0x4F):

```
685374 4e967e c35ced b6289d 5d5c16 b4a7c4 685374 400edc 4caba7 cb853a d6a8b6 bff707 e71727 1d6a33 3cabc1 446d0c 6409b6
```

* **`b4a7c4` is the cpu world** (`aid_model_banjox_background_cpu_default`, 14.5 MB, chunk 12 with 417 instances,
  plus 52 cpu reference models). `b837e8` (57 MB) is another copy, presumably for other acts or modes. No other bundle
  on that list contains a background model.
* xenia.log: after one named open, a burst of plain `HostPathDevice::ResolvePath(\bundle\4f)` lines follows, each
  with an XCTD compression query (e.g. `source-build/xenia.log` lines 1958–1975). These look like handle-relative
  opens whose file name is not logged. So "not in xenia.log" does not mean "not opened" (medium confidence).
* **The sink edit is not in the game folder:** md5 of `Workspaces\dev\game\Bundle\4f\b4a7c4` = `1e2a7210…` and of
  `b837e8` = `79733ffc…`, identical to `Banjo Kazooie Nuts & Bolts (FRESH)\Bundle\4f\…`. Both still have their 2008
  timestamps. (`0b6509` itself was modified 2026-09-27 12:36.)
* Next step: reapply the chunk-12 sink to `b4a7c4` in the workspace Xenia actually boots, and check the file hash
  before launching.

## 7. Confidence

| Claim | Confidence |
|---|---|
| LOD table layout and cull semantics | high (format consistent across worlds, and the selector reads exactly this layout) |
| Patch 4.1 changes LOD/cull distances | high |
| Visual and performance result of 4.1 | untested |
| Far clip value | unknown |
| "Repeat" = tiled hinterland plus the painted sky horizon strip | medium (camera-following skydome not proven) |
| LOGBOX world = `b4a7c4` through the manifest dependency list | high |
| The earlier sink test used an unmodified file | high for the dev workspace copy |
