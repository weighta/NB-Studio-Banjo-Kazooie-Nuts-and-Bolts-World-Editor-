# NB Mod Tool — status

Last updated: 2026-09-28 (Seattle Overhaul additions: see the section at the end and seattle/REPORT.md). Legend:
**VERIFIED** = tested against every relevant game file and/or in Xenia as stated ·
**WORKS (not in-game tested)** = verified structurally / off-line only ·
**EXPERIMENTAL** = partial or unconfirmed semantics · **NOT DONE** = not implemented yet.

## Application layout

| Path | What |
|---|---|
| `NBModTool/src/NB.Core` | Format library (C#/.NET 9): decompression, CAFF, bundles, textures, models, markers, text, audio, XEX, workspace, validator |
| `NBModTool/src/NB.Studio` | Desktop app (WinForms + OpenGL 3.3): worlds, scene tree, asset browser, 3D editor, properties, tag editor, text, audio, video |
| `NBModTool/src/NB.Cli` | Command-line tools and self-tests (round-trips, extraction) |
| `docs/FORMATS.md` | File-format reference |
| `tools/probe/*.py` | Reverse-engineering scripts used to establish the formats |
| `tools/xenia/*.ps1` | Xenia window capture / message-based input helpers |
| `thirdparty/vgmstream` | XMA decoder used for audio extraction (vgmstream r2117) |
| `Workspaces/dev` | Working copy used for testing (the FRESH directory is never written) |

## Phase 1 — formats, decompression, executable

| Feature | Status | Evidence |
|---|---|---|
| xcompress 0x0FF512ED decompression (native LZX) | **VERIFIED** | all 142 compressed bundles decompress to CAFFs with valid checksums and exact size accounting; 21 s for 1.9 GB |
| Game accepts uncompressed CAFF where it shipped compressed | **VERIFIED in Xenia** | decompressed `685374` (common bundle) boots to the title screen |
| Recompression to 0x0FF512ED | NOT DONE | not required (see above) |
| CAFF read/write + checksum | **VERIFIED** | 1756/1756 CAFFs round-trip byte-identically |
| Stream archive (Bundle/50) read/write | **VERIFIED** | 151/151 round-trip |
| Asset/bundle id hashing | **VERIFIED** | reproduces every manifest id; bundle file names |
| XEX decrypt/decompress (xextool-equivalent) | **VERIFIED** | retail-key AES + basic compression → valid PE with all sections |
| XEX modification (console builds) | **VERIFIED in Xenia** (console itself not available) | `NB.Cli xex-patch` / Build → "Export for Console (RGH/JTAG)" / `ws-export --console`: writes default.xex with the enabled executable mods applied (payload decrypted, basic compression kept, headers unchanged). Test: with no Xenia patch file loaded ("PatchDB: Loaded patches for 0 titles"), the baked XEX showed the Change Vehicle items and town weapons/gauges. Needs a console that ignores XEX signatures (RGH/JTAG); not tested on hardware |

## Phase 2 — assets

| Feature | Status | Evidence / notes |
|---|---|---|
| Asset index / browser (68 906 assets) | **WORKS** | search by name/id/type/bundle; preview; export |
| Texture decode (2D, all formats) | **VERIFIED** | layout sizes match all 14 049 textures; visual checks |
| Cube / multi-frame textures | WORKS | face/frame 0 shown |
| Volume textures | EXPERIMENTAL | shown as 2D slices; 3D tiling not implemented |
| Texture import/replace (2D) | **VERIFIED in Xenia** | `wood_painted1_colour` → checkerboard: Mumbo's Motors shows the checkerboard in Showdown Town (work/t0.png); updates resident `mip` + streamed `top`; CLI `tex-replace` |
| Model geometry decode + OBJ export | **WORKS** | exact vertex layouts from shader vfetch; Showdown Town + props render correctly |
| FBX export | **WORKS — verified in Blender 3.2** | binary FBX 7.4 (meshes, normals, UVs, materials, diffuse textures); parasol and Mumbo's Motors import with all triangles/materials/textures; studio right-click → Export Model as FBX; CLI model-fbx |
| Skeletal meshes / animations | **VERIFIED in Blender** | skeleton ("pose" object: Banjo 175 joints = the 175 anim tracks, Mumbo 127, Grunty 119, Bottles 77, Jinjo 66), skin weights and animations all decoded. `NB.Cli skeleton` prints the hierarchy; `model-fbx` exports armature + skin (meshes deform correctly when posed); `anim-fbx <model caff> <model> <anim caff> <anim> out.fbx` adds one animation (30 fps, game slerp sampled per frame). Check: Banjo `run` in Blender 3.2 is a clean run cycle — feet in antiphase, planted at constant height during stance, loop closes (frame 31 = frame 1). Studio asset export writes FBX with skeleton for character models. **Animation import VERIFIED in Xenia**: encoder re-encodes all 5,911 animations (`anim-verify`); `anim-rotate` edits a joint track and re-encodes — Banjo's `stand` with the `LF_H` track rotated 60° shows his leg visibly lifted in game vs stock; `anim-import <ws> <anim> <file.fbx>` imports an FBX animation through the same encoder (its in-game check is the anim-rotate test; FBX re-import itself only checked off-line). Limits: same joint set as the original (no new bones), rest pose/skin weights are not re-imported |
| Model import / replacement (OBJ / FBX → model geometry) | **VERIFIED in Xenia** | generated sphere replaces the café parasol and renders correctly; exported models re-import exactly (all LODs); studio: scenery right-click → Import Model; CLI `model-import`; FBX import verified with a Blender 3.2 export (identical bounds, 1,379 triangles). Limits: keeps the model's materials/vertex format, ≤ 65 535 vertices per buffer, skinned/animated meshes not handled |
| Text (loctext) view/edit | **VERIFIED in Xenia** | `buttonmenu__resumegame` → "NB MOD TEXT TEST" shows on the pause menu (work/p0.png); English lives in `Debug/11`; 1545 of 1605 tables round-trip, 4 per language read-only; CLI `text-set` |
| Audio (XWB) list/play/export | **WORKS** | XMA via vgmstream, PCM direct |
| Audio replace (WAV → PCM16) | **VERIFIED in Xenia** | MusicPause (XMA2) → 1 kHz PCM16 tone: loopback capture while paused is the tone, unpaused is normal music (CLI `audio-replace`, `audio-capture`) |
| Video (WMV) play/export/replace | **VERIFIED in Xenia** | the first boot video (Debug/36/a7/b1/d6, Microsoft Game Studios logo) replaced with another game video: the replacement plays at boot; reverted file shows the logo again |
| Bulk extraction | WORKS | Tools > Bulk Export (current filter) |

## Phase 3/4 — world editor

| Feature | Status | Evidence / notes |
|---|---|---|
| World/act catalogue | **WORKS** | 19 world scenes; acts identified via `aid_marker_<world>_act<N>_main` bundles (see FORMATS.md) |
| Load world: terrain + scenery + markers | **WORKS** | Showdown Town: 707 terrain draws, 760 scenery, 1452 markers in 2.5 s |
| Select / move / rotate / scale, numeric editing, undo/redo | **WORKS** | viewport gizmo + properties panel |
| Scenery transform edits persist in game | **VERIFIED in Xenia** (title screen + Showdown Town gameplay) | Showdown Town: cafetableparasol1_1 + cafetable1_1 raised +6 in the studio, visible floating in gameplay vs unmodified baseline (work/cafe_zoom.png). Collision data is not touched (havok not decoded yet) |
| Marker (actor/pickup/spawn) transform edits | **VERIFIED in Xenia** | Humba's actor spawn moved next to the warp pad: she stands there in game (work/humba_marker.png) |
| Scene hierarchy, search, visibility | WORKS | |
| Context menu (focus, properties, export OBJ, reset, import model, duplicate, delete) | **WORKS** | duplicate + delete verified in Xenia (see below) |
| Duplicate scenery instance | **VERIFIED in Xenia (incl. collision)** | new chunk-12 instance renders (second Mumbo's Motors) and now also gets collision: Duplicate extends the world havok asset's per-instance collision table (type-7 entry) — the trolley is stopped by the copy's wall, before this it drove through |
| Delete scenery instance | **VERIFIED in Xenia** (as hide) | zero scale + moved below the level; true removal hangs the game (index references), so it is not offered |
| Workspace history / undo of bundle saves | **WORKS** | every save keeps the previous file (last 8) in `<workspace>/history`; Edit > Undo Last Bundle Save; CLI `ws-undo`, `ws-revert` |
| Collision decode + display + OBJ export | **WORKS** | Havok 5.5 packfiles with reflection; every shape class used by the game; Showdown Town 367k / Nutty Acres 321k triangles, 0 failures; View → Collision |
| Collision of moved / duplicated scenery | **VERIFIED in Xenia** | moved scenery keeps its collision (building sunk 40 units: trolley drives across its old footprint; shifted +20: stops at the new wall); duplicates get collision via the type-7 table (see FORMATS §14) |
| Collision import (OBJ/FBX → Havok shape, new MOPP) | **VERIFIED in Xenia (one asset)** | `collision-import <ws> <bundle> <havok asset> <mesh> [--offset --scale --box --material --dry]`; studio right-click → Import Collision. Writes a new packfile with a chunked MOPP like the shipped assets. Test: the telegraph pole's collision replaced by a 20×4×20 box — no freeze, and the auto-steered trolley (`tools/xenia/driveto.py`) stopped 15.4 / 18.4 units from the pole vs 3.8 units without the import. Breakable scenery is refused (freezes the game; `--force-breakable` to override). Only tested on one prop |
| World editing in every world | **VERIFIED in Xenia** | a scenery instance duplicated above the player spawn and compared with an unmodified baseline in: Showdown Town, Nutty Acres, LOGBOX 720, Banjoland (raised display case), Terrarium of Terror (revolving door), Jiggoseum/World of Sports; Spiral Mountain via the title screen (its `startofgame` start keeps loading, so it was checked through the title-screen scene). `tools/xenia/worldtest2.py` |
| Paths / navigation display + editing | **WORKS** (editing not in-game tested) | path-node markers (type 22, record +8 = next node index) drawn as linked lines with direction arrows (View → Paths); Showdown Town: one 331-node road route. Editing: nodes move like any marker (marker edits are verified in Xenia); the next-node link is editable in the properties panel ("Next path node", saved with World > Save) and with `NB.Cli path-link <ws> <bundle> <marker asset> <node> <next>` — checked off-line (re-routing node 893 gives 2 path starts, restoring gives 1). Which game object follows this route was not identified, so the effect of path edits in game is not tested. PathEngine navmesh assets (pathenginepreprocess) not decoded |

## Phase 5 — tag editor

| Feature | Status | Notes |
|---|---|---|
| Field editor for any asset's CPU data | WORKS | u32/s32/float/hex per word; pointers read-only (from relocations); unknown fields labelled |
| Structured schemas | **WORKS** | textures, model chunks, scene instances, markers (header), **objparams** (all 273 classes: layouts inferred from 1810 assets — strings with known-value menus, floats, ints, asset references by name), **vehicles** (header + block records) |
| objparams edit in game | **VERIFIED in Xenia** | engine power (`vehicleBlockEngine` +0x3FC/+0x3D0) changes the trolley's acceleration: 0 → 4.3 s to the warp pad vs 0.27 s (drive test) |
| Vehicle assets | **WORKS** | 275/275 parse exactly; 12 455 blocks, every part id resolves; max 250 blocks, grid 0..18 |
| havok / scripts (full) schemas | NOT DONE | scripts: known opcodes only (see FORMATS §9) |

## Phase 7/8 — build and testing

| Feature | Status | Notes |
|---|---|---|
| Non-destructive workspace (copy, change log, revert, modified-file list) | **WORKS** | |
| Validator (checksums, canonical layout, texture layouts, archives) | **WORKS** | |
| Launch in Xenia | WORKS | |
| Export playable game directory / mod package | **WORKS** | Build → Export…; full copy or changed files only, NBMOD_CHANGES.txt (files + change log), Xenia patch file for enabled executable mods; CLI ws-export |
| Automated in-game testing | **WORKS** | source-built Xenia Canary accepts posted input; `tools/xenia/quickstart.py` boots a workspace into Showdown Town gameplay in ~43 s (with the test-mode skip-intro script patch) and captures screenshots |

## Phase 12 — after-party mods

| Mod | Status | Notes |
|---|---|---|
| 15.6 Change Vehicle in Showdown Town | **VERIFIED in Xenia** | Mods menu → "Change Vehicle / Build Vehicle in Showdown Town" (2-instruction patch via a Xenia patch file). Both pause items appear in town; Change Vehicle lists the Town Blueprints and swaps the vehicle in place (upgraded trolley → Trolley Mk. 2 verified); Build Vehicle opens the garage. Trolley upgrades are unlocked by the *_Act*_1_Beaten flags in aid_misc_banjox_unlockablelist_golfcarts. See FORMATS §13.1 |
| 15.1 World boundaries | **VERIFIED in Xenia** | The "map reset" is the Havok broadphase border: leaving the world box sends objMsgId_Avatar_EscapedBackground and Banjo is reset. The box = level collision AABB ± 100, built at load by 0x822EB698. Mods menu → "Larger world boundary": box grows to ±2048 on X/Z (read live). Travel beyond the old edge verified: a duplicated bridge (with collision) at z = 826 and the player spawn moved onto it — with the mod Banjo stands/moves at z = 831 for 20+ s; without it the same setup reloads the level endlessly. Draw distance: LOD/cull distances come from each model's rendergraph, scaled by one literal in the LOD selector — Mods menu → "Longer draw distance" (×4); the mechanism is verified by the inverse test (÷10 makes buildings drop LOD and props vanish next to Banjo) and the ×4 build (words read back live) and a ×10 build boot and play normally. The far "repeating terrain" is the tiled hinterland meshes and the sky strip (medium confidence). docs/research/151_bounds.md, 151b_drawdistance.md |
| 15.2 Garage limits | **VERIFIED in Xenia** (build area and part limit) | Build area is 19 cells per axis (vehicle + held part, relative), not 16; enforced in code (0x8264AA58). Mods menu → "Larger garage build area": measured via the editor cursor in guest memory — the Mk.7 + a held cube stops at total extent 19 stock and 31 patched; parts attach beyond the old edge. Part limit: Mods menu → "Vehicle part limit 400" (18 words: three limit compares, larger stack frames in the vehicle spawner and break-up code, restriction stripper skipped above 250, blueprint preview clamped to 250 blocks). Stock garage stops at exactly 250; with the mod a vehicle was built to 294 parts and saved, and a 295-part blueprint spawns, opens in the garage, previews in Change Vehicle and loads through it, and swapping back works. Practical maximum (measured with a limit of 100): the cursor then stops about 60 cells around the vehicle (C −27…+32, up to +35), at the garage room's walls and ceiling; blueprint cells are u8 (≤ 256). Limits: challenge part restrictions are not enforced above 250 parts; previews draw the first 250 blocks; challenges/multiplayer with >250 parts, and saving/driving a vehicle longer than 19 cells, not tested. docs/research/152_garage.md §6–7 |
| 15.3 Destructible vehicles in Showdown Town | **VERIFIED in Xenia** | Mods menu → "Destructible vehicles in Showdown Town": the town spawn branch (permanent damage immunity, impact damage off, gauges/weapons off) is skipped. Town vehicles then take per-block damage (block health +0x270 read live) and break apart: with the block damage multiplier at 50 (Mods → Data settings → "Vehicle block damage multiplier", = aid_misc_banjox_vehicleblockglobals_default +4) repeated wall rams split the trolley 14 → 2 → 1 blocks with parts lying in the street and Mumbo's "Vehicle take damage and lose parts" tutorial. At the default multiplier damage accumulates (−5 HP per crash on the 225-HP lowloader) but takes many hits. docs/research/153_destruct.md |
| 15.4 Camera tool range | **VERIFIED in Xenia** | Photo camera limited to a 30-unit sphere around its start (0x8229A8D0). Mods menu → "Unlimited photo camera range" (1 word): measured in guest memory — stock stops at exactly 30.00, patched flew to 57.7 units (stopped only by scenery). Helper mod "Pause menu opens on Photos & Videos" because pause tabs switch only via left-stick keystrokes. docs/research/154_camera.md, pause_tabs.md |
| 15.5 Debug features | **VERIFIED in Xenia** (developer main menu, developer part list; the other remnants are vestigial) | Mods menu → "Developer main menu at the title screen" (4-word patch of the title-screen Start handler): pressing Start opens the shipped developer MAIN MENU (Enter Garage / Start New Game / Resume Save / Multiplayer / Demo Level / Demo Messing / Unlocked / Auto Progression); Enter Garage loads Mumbo's Motors directly (verified). Mods menu → "Developer part list" (5 words): the garage part list comes from the unused developer asset `garage_allblocks` via the demo-build code path — a new game has 118 part types × 200 instead of 23, 12 store categories instead of 10, Body offers Light/Heavy/Super at the start (verified in Xenia). The menu checkboxes have no code behind them; the two demo buttons load bundles that are not on the disc. The "DEBUG OPTIONS" scene (73) has no handler at all. Global 0x82FAC650 = multiplayer mode, 0x82FAC5D0 = demo build (E3 demo bundle missing). Details: docs/research/155_debug.md, FORMATS §13.3 |

Test-mode helpers: `start-in` (start a new game in any act), `preset-flags`, `skip-intro`; world list shows acts.

## Summary — verified, experimental, limitations (2026-09-27)

**Verified in Xenia.** Each item was changed with the tool and then observed in game.
* **Game files**
  * Uncompressed bundles are accepted by the game.
  * A console XEX with the mods baked in boots with no patch file.
* **Asset replacement**
  * Texture, model (OBJ/FBX), text, audio (PCM) and video.
  * Animation: a re-encoded, edited track (Banjo's `stand`).
* **World editing**
  * Scenery move, duplicate (with collision) and hide.
  * Marker move.
  * Tested in every world: Showdown Town, Nutty Acres, LOGBOX 720, Banjoland, Terrarium of Terror, Jiggoseum, and
    Spiral Mountain through the title-screen scene.
* **Collision**
  * Moved and duplicated scenery keeps its collision.
  * Imported collision box: one prop tested.
* **Tag editing**
  * An objparams edit (engine power) changes the vehicle's acceleration.
  * The vehicle block damage multiplier is used by the game.
* **Test mode**
  * Start in any act, preset game flags, skip the intro.
* **After-Party mods**, each a checkbox in the Mods menu:
  * 15.1 World boundary: ±2048 box; travel beyond the old edge; ×4 draw distance.
  * 15.2 Garage: build area 31; part limit 400, loading a 295-part blueprint.
  * 15.3 Destructible vehicles in Showdown Town, plus the damage multiplier.
  * 15.4 Unlimited photo camera range.
  * 15.5 Developer main menu and developer part list.
  * 15.6 Change / Build Vehicle in Showdown Town.

**Works, not tested in game.** Verified structurally or off-line.
* Asset index and browser, and every decoder with byte-exact round trips (CAFF, archives, 275 vehicles, 5,911 animations).
* FBX export: skeleton, skin and animation checked in Blender.
* Path-link editing.
* Validator and export for Xenia or console.

**Experimental.**
* Volume textures are shown as 2D slices.
* Script editing covers known opcodes only.
* Havok assets can be decoded, displayed and replaced by an imported mesh, but have no field-level tag schema.
* The animation import route from FBX was checked off-line; the in-game test used `anim-rotate`.

**Limitations.**
* **Formats**
  * No recompression to 0x0FF512ED (not needed; the game reads uncompressed bundles).
  * Model import keeps the original materials and vertex format, with at most 65,535 vertices per buffer; skinned
    meshes cannot be imported.
  * Animations must keep the original joint set.
* **World editing**
  * Deleting scenery hides it (zero scale, moved below the level); true removal hangs the game.
* **Collision import**
  * Refuses breakable scenery, which freezes the game.
  * Only one prop was tested in game.
* **Garage mods**
  * Build area: about 60 cells per axis is the practical maximum (the garage room); only 31 was verified.
  * Vehicles above 250 parts skip challenge part restrictions; previews draw 250 blocks; not tested in challenges or
    multiplayer.
* **Debug features**
  * The DEBUG OPTIONS scene and the developer-menu checkboxes have no code behind them.
  * The E3 demo bundle is not on the disc.
* **Consoles**
  * Console builds need a console that ignores XEX signatures (RGH/JTAG); not tested on hardware.
* **Paths**
  * PathEngine navmesh assets are not decoded.
  * The game object that follows the Showdown Town road path was not identified.


## Seattle Overhaul additions (2026-09-28)

| Feature | Status | Evidence |
|---|---|---|
| Scene builder (JSON → world: textures, wildcard replacements, models, terrain + collision, water, hide/move, markers) | **VERIFIED in Xenia** | seattle/TESTLOG T9–T40; CLI `scene-build`, Studio Build → Build Scene (T39) |
| Culling-tree reset for imported terrain (chunk 0) | **VERIFIED in Xenia** | B13, T15 |
| Differential patch (.nbpatch): build / verify / apply / rollback | **VERIFIED** (clean copy + Xenia boot) | T16, T25, T40; CLI `patch-*`, Studio Build menu |
| Configurable part limit / build area (`vehicle-part-limit:P`, `garage-build-area:N`) | **VERIFIED** for P = 600 (spawn) | T33; Studio Mods menu |
| Blockset / objparams / marker record editing | **WORKS** (inventory change verified in memory) | `blockset-add`, `objparams-set`, `marker-set`, `marker-dump` |
| Recovered parts in inventory and blueprints | **VERIFIED**; parts-store listing **NOT DONE** | T34, T35, docs/research/156_recovered_parts.md |
| Live (game) tab: coordinates, teleport (vehicle and on foot), gravity, photo camera, show player in 3D view | **VERIFIED in Xenia** | T13, T28, T36–T38; `NB.Cli live` |
| Exe mods baked into `default.xex` by patches (XEX2 re-encryption + page-chain / image / header digests) | **VERIFIED on the console** | B23, T49, T50 |
| Model memory compaction (strip hidden-only geometry, drop unreferenced .gpu bytes; `gpu-compact`, `bundle-sizes`) | **VERIFIED in Xenia** (loads, city complete); console effect **awaiting test** | B24, T54, T55 |
| Unlimited world (`no-escape-reset` + `world-bounds-2048`) | **VERIFIED in Xenia** | F5, T56 |
| Console performance of the Seattle city | **OPEN** | B22, B24 |

