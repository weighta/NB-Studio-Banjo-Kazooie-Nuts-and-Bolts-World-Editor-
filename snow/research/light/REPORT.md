# Showdown Town lighting, fog and sky (research report, 2026-10-01)

Winter recipe applied in Workspaces/snowlight, every phase booted in Xenia (instance 7, muted) and compared with the
untouched Workspaces/snowlight0. Files changed: Bundle/4f/234cec, Bundle/4f/685374, Bundle/50/234cec.

## 1. Where the time of day lives
- Phase bundles 4f/01d1b6, e00470, 6bd4a7, 3f0052 hold no lighting: a manifest plus 78 grass shadow tiles
  (aid_texture_banjox_grass_showdowntown_shadow<N>_1, DXT4_5 128x128), baked grass shadows per sun direction. Their
  Bundle/50 twins are empty 52-byte stubs.
- Phase scripts in 685374 (aid_script_banjox_showdowntown_<phase>):

| op | what it does | morning | midday | afternoon | night |
|---|---|---|---|---|---|
| header | loads the phase bundles 4f/<id> and 50/<id> | 01d1b6 | e00470 | 6bd4a7 | 3f0052 |
| 0x2C (.data+0x1C) | skydome model | skydomes_morning 043AE90C | skydomes_afternoon 04439830 | skydomes_evening 04CCBFAA | skydomes_night 047F6D82 |
| 0x52 | runs the light setup in 234cec | lightsetup_showdowntown_morning | _main | _afternoon | _night |

  They also run showdowntown_general (0x52), ambience (0x2D / 0xA1 "quiet_night"), lighthouse beams (0x88, not at
  night), and op 0x73 (butterfly texture, 10, 1.5, 60, 180).
- Correction to seattle/BUGLOG.md F3: 234cec has main, morning, afternoon and night light setups (228 bytes each) plus
  carpark. Midday uses main.
- A new game ignores the phase picker: aid_script_banjox_spiralmountain_startofgame calls "go to showdowntown_midday"
  directly. The picker 0x824104C0 (and the co-op mailbox 0x82FBCB30) is used on later town loads. Test-only edits:
  startofgame target .data+0x534 in a skip-intro workspace (+0x57C in the original); midday entry flag in
  ..._timeofday (+0x60) = gameFlag_Normal_Null. Never ship them.

## 2. Light-setup record (.data, big-endian)

| off | field | main | morning | afternoon | night | verified |
|---|---|---|---|---|---|---|
| 0x08 | ambient RGB0 | 585858 | 515151 | 585858 | 2E2E4B | yes |
| 0x0C | sun colour RGB0 | FFFFFF | DCDBC0 | F1C169 | 5E607B | yes |
| 0x10/0x14 | sun elevation/azimuth (rad) | 0.764/-1.989 | 0.476/-1.989 | 0.504/1.009 | 0.764/-1.989 | read back |
| 0x1C | sun intensity f32 | 1.233 | 1.183 | 1.033 | 1.431 | yes |
| 0x34/0x38 | shadow direction (op 0x7E) | | | | | read back |
| 0x4C | fog on (op 0x53 at 0x44) | 1 | 1 | 1 | 1 | read back |
| 0x50 | fog start | 80.16 | 64 | 58 | 17.16 | yes |
| 0x54 | fog end | 901.9 | 1540.7 | 901.9 | 1240.1 | yes |
| 0x58 | fog max (0..1) | 0.33 | 0.394 | 0.404 | 0.615 | yes |
| 0x68 | fog colour RGB0 | CECFEC | C4C9DF | E0AD61 | 282D3E | yes |

Unknown (no visible change live): 0x5C; 0x60/0x64 (70/1428; 351/4096 morning); 0x74-0x80 (0.2/1000/0.995/20) and
309CB0 at 0x84 (probably underwater fog); 0x8C (0.5, 0.2 at night); op 0x6C at 0x98-0xA8 (probably HDR/bloom, copied at
load); op 0x54 at 0xB4 (0.5/800/1/400); op 0x94 at 0xD8 (phase index 0/1/2/4).

Runtime: the live light object moves each boot (find it: its +0xD0 equals the 8 bytes at 0x82F9DF6C). Ambient +0x10,
sun +0x20 (float RGB), intensity +0x68, fog colour +0xA0, fog start/end/max +0xD0/+0xD4/+0xD8 (second copy at +0x140).
Copied every frame into renderer globals (fog colour 0x82F9DF20, start/end 0x82F9DF6C): write the object, not the
globals. The skydome is not fogged: the fog colour must match the bottom of the sky texture.

## 3. Skydomes

| phase | texture | format | shared with |
|---|---|---|---|
| morning | skydomes_showdown_morning_0x0f8c0fa5 | DXT1 4096x1024 (top streamed in 50/234cec) | town only |
| midday | spiralmountain_materials_skys_blue03_0x06814795 | DXT1 4096x1024 | Spiral Mountain (01ca49), title (757c4b) |
| afternoon | skydomes_showdown_evening_0x065c0aa5 | DXT1 4096x1028 | town only |
| night | skydomes_showdown_night_0x001489a5 (vertical gradient) | DXT1 1024x1024 | 10 other bundles |
| night | skydomes_showdown_stars_0x04b61f75 | DXT1 1024x1024 | 3 other bundles |

Panorama mapping u = 0.357 + azimuth/360; horizon at t ~ 0.91 (row table in winter_sky.py). The moon is separate.

## 4. Recipe (snow/research/light/apply_winter.py <ws>, ~2 min)
1. Sky first: `python snow/research/light/winter_sky.py` (procedural, sky_winter/); point midday at the morning dome
   (`obj-set <ws> aid_script_banjox_showdowntown_midday 1c h:043AE90C`) so blue03 stays untouched; `tex-replace` the
   morning (sky_day), evening (sky_dusk), night (sky_night) and stars (sky_stars) textures.
2. Town only: ws-revert Bundle/4f/<b> and Bundle/50/<b> for the night texture's other bundles 181575, 455e80, 45a42d,
   67b0d3, 68f1b8, 9cedca, c016b4, cc7c54, cd43ee, e37086 and the stars' 45a42d, 67b0d3, cc7c54.
3. Light and fog: obj-set each lightsetup_showdowntown_{main,morning,afternoon,night}: 8 h:ambient, c h:sun,
   1c f:intensity, 50/54/58 f:fog start/end/max, 68 h:fog colour.
4. Live tuning: `XENIA_PID=<pid> python snow/research/light/live.py amb=r,g,b sun=r,g,b intensity=x fogcol=r,g,b
   fogstart=x fogend=x fogmax=x`, then bake with step 3.

| setup | ambient | sun | intensity | fog colour | start | end | max |
|---|---|---|---|---|---|---|---|
| main (midday) | 8A94A8 | CCDBFA | 0.65 | CED8E6 | 25 | 400 | 0.60 |
| morning | 6E7387 | F5DBCC | 0.85 | BEC8D8 | 20 | 380 | 0.55 |
| afternoon | 6B6B85 | FACC9E | 0.90 | D1C4C7 | 25 | 380 | 0.60 |
| night | 333D5C | 7387B8 | 1.30 | 3D4A6B | 10 | 320 | 0.75 |

## 5. Verification
shots/compare_p1..p4.png (morning, midday, afternoon, night) and compare_all.png: top = original, bottom = winter.
Morning pale warm sun, midday grey-white overcast, afternoon lilac-peach dusk, night navy haze with sparse stars and
glowing windows; far hills fade into mist from ~300 units. At night live.py read back the baked values exactly.

## 6. Limits
Textures still summer (separate work). Afternoon sky a bit more lilac in game. Morning vistas quite white (fog max ~0.45
or fog ~B4BECE if too bright). Butterflies: op 0x73 texture/count edits showed no change (reverted). Light-op handler not
among the 13 handlers at 0x822EBDD0 (table 0x82F9E478, dispatcher 0x82291530).
