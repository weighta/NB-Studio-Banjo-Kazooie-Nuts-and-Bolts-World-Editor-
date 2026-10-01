# Falling snow in Showdown Town (research report, 2026-10-01)

Verified in Xenia: snow falls around the camera everywhere in town, in every time-of-day phase, while driving, on foot
and 450 units from the plaza. It needs three small assets copied into the town bundle 234cec, one effect marker, and a
16-word exe patch that makes the emitter follow the camera. A white snow fog (light-setup command 0x53) was verified too.

## Recipe (about 5 s, safe to re-run)
```
work/cli_snapshot/NB.Cli.exe ws-create "Banjo Kazooie Nuts & Bolts (FRESH)" Workspaces/snowfx
work/cli_snapshot/NB.Cli.exe skip-intro Workspaces/snowfx --no-town-intro --preset-town   # test mode only
python snow/research/fx/snowtown.py Workspaces/snowfx --xenia-patch mp/c5/patches [--fog 15,420,0.6,E6EAF2]
python mp/coop.py 5 --ws snowfx      # no --fresh: it wipes mp/c5/patches
```
To ship: `python snow/research/fx/snowtown.py --exewords` prints the C# ExeWord lines for NB.Core/Mods/ExePatches.cs.

## 1. How Banjoland makes it snow
Assets (.data only, no relocations, no .gpu or streamed parts):

| Asset | Id | Bundles | Contents |
|---|---|---|---|
| aid_compositeeffect_banjox_snowfall1 | 4B97312C | 077143, 1592bd | 16 bytes: {u32 4, 45DD95C3}{u32 4, 45D4C479} |
| aid_gpuparticleeffect_common_snowfall1 | 45DD95C3 | 077143, 1592bd | 0x184-byte particle record |
| aid_gpuparticleeffect_common_snowfall2 | 45D4C479 | 077143, 1592bd | 0x184-byte particle record |
| aid_texture_banjox_particles_glowoffset | 0163A657 | 685374 (common, always loaded) | flake texture (record +0x34) |

Particle shaders are global, so these assets can be copied into any bundle.

What spawns them:
- snowfall1 has no marker or script: chunk 32 ("effect locators") of the Banjoland background model
  aid_model_banjox_background_banjoland_default (.data +0x1BA88): u32 count, then 0x148-byte records (+0 name[0x40],
  +0x80 effect id, +0x104 axis-angle rotation (-1,0,0,pi/2), +0x114 position, +0x120 radius x3 (778.7), +0x12C -1,
  +0x130 flags 0x01010000 = auto-start, +0x134 = 3, +0x140 = 2). Banjoland has 4 snow locators at y~148. The town
  background model has no chunk 32. survey.txt lists every chunk-32 record and type-30 marker in the game.
- snowmachine1 uses marker type 30, the generic effect marker (~400 uses). Layout (0x38 bytes): +0 size 0x38, +4 u16
  type 30, +6 u16 index (sequential), +8 0, +0xC marker-set mask (0 = always), +0x10 0, +0x14 position, +0x20 rotation
  (radians), +0x2C scale, +0x30 0, +0x34 effect id. New records go before the closing type-0 record.

Particle record fields (from all 507 records plus Xenia tests):

| Offset | snow1 / snow2 | Meaning |
|---|---|---|
| +0x10 | 8000 | particle buffer size (max alive) |
| +0x14 | 200 | not the rate (no visible effect) |
| +0x34 | | texture id |
| +0x78 / +0x84 | +-50, +-0.096, +-50 | emitter box min/max relative to the emitter (y up for rotation 0) |
| +0xB8..+0xC4 | 4 / 3 | particle sizes (verified) |
| +0xCC/+0xD0 | -15 / -80 | fall term |
| +0x124/+0x128 | 30-40 / 5-8 | lifetime min/max (s) |
| +0x13C | 20 / 30 | emission rate per second (verified; 600 = dense) |
| +0x180 | 0 everywhere | unused; the mod writes 0x534E = "follow the camera" |

Do not change +0x13C in live memory: the running emitter stays broken until the level reloads.

Runtime: GPU particle instance vtable 0x82001020, constructor 0x82277358 (+4 owning node, +0xC record, +0x10 time,
+0x14 = 1/rate, +0x40 emitter position, +0xB0 previous position). The per-frame update 0x82226BD0 copies node+0xA0 into
instance+0x40 at 0x82226EC4..0x82226ED0 (r29 = instance, r10 = node, r8 = record). Camera position: 0x82FADC30.

## 2. Exe patch (camera follow)
0x82226ECC `lvx128 v0,r10,r11` (0x100A58C3) -> `b 0x82D21340` (free padding after the first .embsec_):
```
lwz r11,0x180(r8); cmplwi cr6,r11,0x534E; bne orig
lis r11,0x82FB
lwz r9,-0x23D0(r11); stw r9,0xA0(r10)    (x)
lwz r9,-0x23CC(r11); stw r9,0xA4(r10)    (y)
lwz r9,-0x23C8(r11); stw r9,0xA8(r10)    (z)
addi r9,r29,0x40
orig: li r11,160; lvx128 v0,r10,r11; b 0x82226ED0
```
Only tagged records are affected. Snow kept following the camera 400+ units from the marker (not culled).

## 3. What snowtown.py writes
- 234cec: new assets aid_gpuparticleeffect_banjox_snowtown1 (452DACEE), _snowtown2 (4524FD54),
  aid_compositeeffect_banjox_snowtown (4BCB25A4), inserted before the manifest and registered. Copies of snowfall1/2
  with emission 1200/250 per second, lifetime 5-7 s, buffer 10000, size x2.5, box +-60 wide 15..35 above the camera,
  tag 0x534E. One type-30 marker (index 1462, (-10,40,306), mask 0) in aid_marker_banjox_showdowntown_main.
- --fog: command 0x53 in the four town light-setup scripts.
- Xenia patch file `<dir>/4D5307ED - Snowy Showdown Town.patch.toml`.

## 4. Tests (shots/)
Static vanilla markers: few flakes (t1_b, t1_far). Live size/position edits (live1_size5, live9_pa0). Python camera
follow (follow5_far). Exe hook: run_A (wrong rate field), run_C, final run_D (heavy), run_F (defaults), D_far_beach,
F_drive2/3, F_onfoot, run_H (fog). Grid mode without exe patch (run_G1/G2): weak fallback.
Limitations: no collision (flakes pass through roofs, show under water). Not tested: console, garage round trip,
Change Vehicle with the patch on.

## 5. Weather, rain, fog
- aid_script_banjox_common_weather_default (e66c1c): op 0x69 volumetric clouds, 0x79 wind field, 0x7A x5 wind-animated
  leaf materials. No snow/rain/fog commands.
- Rain looks cut: renderer at render manager +0xE40 (init 0x8235EC18, draw 0x821EB8C8) draws only when manager+0xE68 > 0;
  nothing writes it.
- Fog = command 0x53 in aid_script_banjox_lightsetup_showdowntown_{main,morning,afternoon,night}: +0xC start, +0x10 end,
  +0x14 max density, +0x24 RGB. Midday 80 / 902 / 0.33 / CECFEC. `--fog 15,420,0.6,E6EAF2` = white snowy haze.
