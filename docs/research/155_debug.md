# 15.5 Debug / developer functionality — static RE report

Image: `work/default.exe` (VA = file offset + 0x82000000). All addresses VA. Nothing was run in Xenia; every
"works" statement below is a static prediction unless marked otherwise.

New helper scripts (tools/probe): `gref.py` (classify lis-based refs to a global), `basestores.py` (stores/loads
through a lis+addi base register), `gstore.py` / `gaccess.py` (stores / accesses that can hit a global),
`storeto.py` (noisy heuristic), `callargs.py` (call sites of a function + constant loaded into rN),
`strat.py` (print ASCII / UTF-16BE string at VA), `caffwhere.py` (which asset of a CAFF holds a file offset),
`gameassetref.py` (dump the name→asset-id records of the gameassetref assets).

---

## 0. Correction to FORMATS §13.3

`0x822385DC li r4,71 ; bl 0x82241888 ; bl 0x823B7478` does **not** open XUI scene 71. 0x82241888 is the message
constructor (r4 = message id, table 0x8212A988) and 0x823B7478 broadcasts it to the actor lists. Message 71 =
`objMsgId_Actor_ActionBegin`. Changing it to 68 sent `objMsgId_Avatar_ResetBreakable` — which is why "nothing
showed". The scene table 0x82E5E038 and the message table merely have overlapping indices.

Real XUI plumbing:
* `0x823E9BA8(mgr=[0x82FAC758], sceneIndex, ...)` → `0x823E9AB0` opens a scene; per-scene descriptors are at
  `0x82E56A90 + index*0x120` (u32 1, u32 5, char name[] e.g. "frontend_mainmenu").
* Direct constant opens: 71 StartScreen (0x82473178, 0x82473408), 70 StartMenu (0x8246AB68 — house interior entity),
  75 MainMenuMultiPlayer (0x82582FBC — from the dev main menu), 72, 74, 84, 86–96 ... **never 68, 69 or 73**.
* Scene handler objects: base ctor `0x823E4F48(this, sceneIndex)`. Handler for 68 = class "MainMenu"
  (ctor 0x825829E0, factory 0x825828D8 registered by XuiRegisterClass in 0x825799C8, vtable 0x82170AE0).
  **No handler exists for 69 (MainMenuDemo) or 73 (DebugMenu).**
* Scenes can also be opened **by name** by the objparams entity class `entitySceneControlUI`
  (registration 0x8212B5C4 → descriptor 0x82323AB0, create 0x82323B38 → 0x82323C00): its params +0xA4 point to an
  `aid_xuiloadlist_*` whose 0x108-byte records are (type, size, name): type 3 = XUI package (table 0x82E5E1DC,
  loaded as `assetMan://aid_xuipackage_<name>` by 0x823E7D30), type 2 = scene name looked up in 0x82E5E038 and opened
  with 0x823E9BA8.

## 1. Developer main menu (scene 68) — how it is reached

### Chain (all verified statically)
1. `aid_misc_banjox_gameassetref_default` (common bundle 685374) has records `name[64] + u32 id`:
   * `retail_mainmenu_scene` → **0x1936DC87** = `aid_script_banjox_ui_frontend_houseinterior` (Bottles' house) —
     record id at asset `.data+0x274C`
   * `debug_mainmenu_scene` → **0x195906C3** = `aid_script_banjox_ui_frontend_main` — record id at `.data+0x2790`
2. `aid_script_banjox_ui_frontend_main` (80 bytes): load bundles 4f/50 **5906c3**, load 685374, spawn objparams
   `aid_objparams_banjox_scenecontrol_ui_frontend` (0x1F23A12B, class `entitySceneControlUI`).
3. Bundle **5906c3** ships (Bundle/4f/5906c3, 430 bytes): the objparams + `aid_xuiloadlist_banjox_frontend` =
   { type 3 `XuiPackage_BanjoX_Frontend`, type 2 `XuiScene_BanjoX_Frontend_MainMenu` } → scene 68 → class
   "MainMenu" (handler 0x825829E0 family).
4. The executable only ever looks up `retail_mainmenu_scene` (string 0x82001654, lookup at 0x822B78F8 into the
   level-ref table 0x82FA3090+4). The string `debug_mainmenu_scene` is **absent from the exe** — the dev menu was
   disconnected in data/code, not by a runtime flag. There is no button combo, launch-data or global byte that
   selects it.

Level-ref table 0x82FA3090 (filled in 0x822B7468 via gameassetref name lookup 0x82254D10):
+0 frontend, +4 retail_mainmenu_scene, +8 klungosarcade script, +0xC start_intro, +0x10 garage,
+0x14 timeofdaysettings, +0x18 multiplayer_frontend, +0x1C hubworld, +0x20 warning_no_parts.

Who loads the retail main menu:
* Title screen: `entitySceneControlBanjoXStartScreenButtonHandler` (registration 0x82126E3C, descriptor 0x821E13A0).
  Init `0x824735C0`: `this+0x70 = params+0xA4`; update 0x82473600 on Start → `0x8247396C bl 0x822BC0D8(this+0x70)`.
  Its params (`aid_objparams_banjox_scenecontrol_ui_startscreenbuttonhandler`, bundle 757c4b) have **+0xA4 = 0x1936DC87**.
* `GoToMainMenu` = `0x82564630` → `0x822BC0D8(&0x82FA3094)`. Callers: pause "quit to title screen" confirm
  callback 0x825A9418 (set up in 0x825A916C), MP leave 0x825A04AC, 0x824438E4, 0x82511580, 0x82538274, and the dev
  menu's own `rightShoulderButton` (0x82583064).

### Patch A (recommended): Title screen → developer main menu
Rewrite the button-handler init so the "next script" is ui_frontend_main:

| VA | original | new | meaning |
|---|---|---|---|
| 0x824735C0 | 814400A4 | 3D401959 | lis r10,0x1959 |
| 0x824735C4 | 7C6B1B78 | 614A06C3 | ori r10,r10,0x06C3 |
| 0x824735C8 | 38600001 | 91430070 | stw r10,0x70(r3) |
| 0x824735CC | 914B0070 | 38600001 | li r3,1 |
| 0x824735D0 | 4E800020 | (unchanged) | blr |

Data-only equivalent (no exe patch): in bundle 757c4b, asset
`aid_objparams_banjox_scenecontrol_ui_startscreenbuttonhandler` `.data+0xA4`: 0x1936DC87 → 0x195906C3.
Pause → "Return to title" still goes to the retail house, and the dev menu's RB button (`rightShoulderButton`)
goes to the retail house too, so both menus stay reachable.

### Patch B (optional): every "go to main menu" → developer menu
Store the debug id instead of looking up `retail_mainmenu_scene`:

| VA | original | new |
|---|---|---|
| 0x822B78F0 | 3D408200 | 3D401959 (lis r10,0x1959) |
| 0x822B78F4 | 38A00000 | 614A06C3 (ori r10,r10,0x06C3) |
| 0x822B78F8 | 386A1654 | 914B0004 (stw r10,4(r11)) — r11 = 0x82FA3090 from 0x822B78EC |
| 0x822B78FC | 388B0004 | 60000000 |
| 0x822B7900 | 4BF9D411 | 60000000 |

r3/r4/r5/r10 are reloaded before the next call (0x822B7904…). Data equivalent: gameassetref_default `.data+0x274C`
0x1936DC87 → 0x195906C3. Side effects: the house becomes unreachable (RB just reloads the dev menu), and the
checks "is current level the main menu" (0x82296FB8, 0x82511570, 0x82515390, 0x825155E4, 0x82545648) apply to the
dev menu instead.

### What the developer menu does (press handler 0x82582DA8, vtable slot 12; fields bound by 0x82582B40)
Presses are ignored unless byte [0x82FADA66] (0x82FAD9D8+0x8E, a signed-in profile/controller flag) is non-zero.
| element (field) | action |
|---|---|
| garageButton (+0xC8) | load gameassetref "garage" (0x1920ABF9, bundle 20abf9 ships): the stand-alone garage |
| newGameButton (+0xCC) | [0x82FAC748]+0x118 = 1, 0x8250BEE0 / 0x8250BB40 (new game), [0x82F9E43C]=1, then start_intro (0x82FA309C) or hub (0x82564808) depending on settings bits |
| resumeGameButton (+0xD0) | if 0x8250BD08 (has save data) → continue to hub, else message box "ok" |
| multiplayerButton (+0xD4) | open scene 75 (MainMenuMultiPlayer), else 0x827146D8(1,0) |
| demoLevelButton (+0xD8) | load "demo_level" = aid_script_banjox_showdowntown_demo (0x19A9CB7B) — **bundle a9cb7b does not ship** |
| demoMessingButton (+0xDC) | load "nuttyacresdemo_level" = aid_script_banjox_nuttyacres_demo (0x194EE58D) — **bundle 4ee58d does not ship** |
| rightShoulderButton (+0xC4) | GoToMainMenu 0x82564630 (retail house) |
| yButton (+0xC0) | nothing |
| unlockAllCheckbox (+0xE0), autoProgressionCheckbox (+0xE4) | **read only by 0x82583090, which just sets their label text**; no code reads their checked state. Vestigial. |

`gameFlag_Normal_EnableAutoProgression` is game-flag index 426 in the flag-name table 0x82E52CC0; no code or
script data consumes it (no `li rX,426` flag access; the name occurs nowhere in the bundles).

Risks / open points: (a) the `XuiPackage_BanjoX_Frontend` package asset lives only in bundle 757c4b (start
screen). It is loaded through the package cache 0x82E568B8 (0x823E7D30 returns early if already loaded); the
retail house (bundle 36dc87, no XUI package) opens scene 70 from the same package, so the package evidently stays
resident — medium confidence the dev menu renders. (b) demo buttons will try to load missing bundles — expect a
hang; don't press them. (c) scenecontrol_ui_frontend may not set a camera/background, so the menu may appear over
black. Confidence: chain and patch correctness **high**; menu fully usable in retail **medium**.

## 2. DebugMenu (scene 73)
* Package entry `aid_xuiscene_banjox_frontend_debugmenu.xur` (757c4b, xuipackage_banjox_frontend .data): root
  element `DebugMenu` of class **XuiScene** (plain, not a custom class), `titleText` "DEBUG OPTIONS",
  `checkBox1..12` (XuiCheckbox, no labels), `newGameButton`, `backButton` (XuiClass_Button_c).
* No handler object (no 0x823E4F48 ctor with 73), no XuiRegisterClass for it, no constant open, and no
  xuiloadlist references it. The 12 options were never implemented in this build (or compiled out).
* Only way to see it: rename the type-2 record in `aid_xuiloadlist_banjox_frontend` (bundle 5906c3; decompressed
  offsets 0x370 and 0x590, two copies/parts) from `XuiScene_BanjoX_Frontend_MainMenu` to
  `XuiScene_BanjoX_Frontend_DebugMenu` (field is 0x100 bytes). Result: a static screen whose widgets do nothing.
  Not worth it. Confidence high.

## 3. Global toggles

### [0x82FAC650] (byte) = multiplayer mode flag — NOT a cheat/debug switch
* Only write: `0x82587EA0 stb r11,-0x2D8(r9)` (r9 = 0x82FAC928, r11 = 1) in 0x825879C0 = init of
  `MultiPlayerMenu_MainMenu_c` (binds networkMenuList, matchTypeMenuList, partyList, friendsList ...).
* Structure at 0x82FAC650: +0 MP flag, +4 network session object (written 0x8238A244, 0x82392C54, 0x824D3710,
  0x824DEBF4, 0x824E3930; queried with 0x8238A418 = session state), +8..+0xC session sub-state. The typical test is
  `[0x82FAC650] && (0x824E6F70()->+0x62)` = "networked MP game running" (e.g. 0x821E4E90, 0x824D4548, 0x825A0484).
  That explains the MP damage multiplier, the part-limit warning and the MP pause items.
* No clearing store was found statically (180 lbz reads, one store). Treat it as "entered MP menus this session".
* Setting it by hand would push single-player code into MP branches with no session (+4 = 0) → not useful.
  Confidence high.

### [0x82FAC5D0] (u32) = demo-build flag
* 0x82363BB0 (called at boot from 0x822B78D4): memcpy 12 bytes from asset `aid_misc_common_gameassetref_demoflag`
  (all zero in retail) to 0x82FAC5D0; then `fopen("GAME:\isdemobuild","r")` (0x82363D08/0x82BB2A10) — **if the file
  exists, [0x82FAC5D0] = 1** (0x82363D20). When set, `aid_misc_common_gameassetref_demo` is registered first, so
  `frontend`/`first_scene` resolve to 0x19901667 = `aid_script_banjox_banjoland_e3demo_main` (E3 Banjoland demo) and
  `garage_demoblocks` replaces the block list; demo gamma params too.
* Readers (26 functions) include storage-device selection 0x822BB0F0, live demo gametype list 0x824CEA48,
  garage blocks 0x8251CB20, awards 0x825C3D00, the town vehicle rule 0x825697C8 (0x82569B1C), E3Demo scene control
  0x82321BE8.
* The E3 demo script loads bundle **901667, which does not ship** → creating `isdemobuild` in the game folder would
  most likely hang at boot. Not practical. Confidence high (mechanism), medium (hang prediction).

### Other debug remnants (no practical switch found)
* `cameraMode_Debug` (enum index 11 in 0x82E919E0) ↔ `aid_objparams_banjox_camera_debug` via
  `aid_misc_banjox_camera_statetable` (.data+0x200). No code path found that enters it (only enum-parsing loops use
  the constant). Low confidence it is reachable.
* Script opcodes `opCode_Debug_Tester_*` (draw RT cube/sphere/lines), `animEvent_DebugPrint`,
  `opCode_Particle_OnHit_DebugPrint`, `actorBodyStateId_Debug`, `gameLiveGameType_Debug` exist as names only.
* Debug-only gameassetref entries whose bundles do not ship: twoplayertest (38df7b), create_and_test =
  ui_frontend_garage (0fae93), overnight_scene / overnighttest_challengeloader, hiscores, editavatarhavokdata,
  dev_blueprints (misc asset, present), garage_allblocks (present).
* `d:\DebugOptions.ini` / `[DebugOptions]` belong to the XTS/XLSP client library (0x82B34280), not the game.
* `debug\` path (0x821334C0, used by 0x822BE6D0/0x822BE7F0) is the known loose-asset override folder
  (`Debug\<type>\xx\yy\zz`).

## 4. Summary of actionable items
1. **Dev main menu from the title screen**: Patch A (4 words at 0x824735C0) or the one-word data edit in 757c4b.
   Test in Xenia: boot, press Start on the title → expect the MainMenu scene (Garage / New Game / Resume /
   Multiplayer / Demo Level / Demo Messing, "Unlock All" and "Auto Progression" checkboxes). Avoid the two demo buttons.
2. Patch B only if the house should be replaced everywhere.
3. unlockAll/autoProgression, DebugMenu checkboxes: vestigial, nothing to enable.
4. 0x82FAC650 = MP flag, 0x82FAC5D0 = demo-build flag (`GAME:\isdemobuild`) — neither is a usable cheat switch.

## 5. Developer part list `garage_allblocks` — enabled and verified (2026-09-27)

**Mechanism.** The parts inventory [0x82FACA44] (0x40-byte entries) is built at game start by **0x8251CB20**:
* Normal path: every record of `unlockable_blocksets` (0x90 bytes each) whose unlock flag is set (0x8251DD20) is added
  with 0x8251CDD8.
* If the demo-build flag [0x82FAC5D0] is set, one fixed list is added instead: `garage_demoblocks`, looked up by name at
  0x8251CC30. That name exists only in `gameassetref_demo`.
* The retail `aid_misc_banjox_gameassetref_default` (+0x1C28) also names `garage_allblocks` (id 0x0BE788E4, resident).
  No code references this entry. `dev_blueprints` (+0x1650) is unreferenced as well.

**Mod `developer-all-parts`** (5 words):
* The string at 0x8216B7B4 (single reference) is rewritten from `garage_demoblocks` to `garage_allblocks`.
* The demo-flag test at 0x8251CC24 (`beq` → `nop`) is removed, so the fixed list is always used. The global demo flag
  itself is not set, so none of its other 25 readers change behaviour.

**Verified in Xenia.** Tested with the research patch and again with the patch file written by `NB.Cli exe-mods`.

| New game in Showdown Town | Stock | With the mod |
|---|---|---|
| Inventory entries | 23 | 118, 200 of each |
| Parts store categories | 10 | 12 (Accessories and Protection added) |
| Body at the start of the game | Light only | Light, Heavy and Super |

`xex-patch` bakes the .rdata words for consoles; the output verifies. Because every part is present from the start,
progress no longer unlocks parts.
