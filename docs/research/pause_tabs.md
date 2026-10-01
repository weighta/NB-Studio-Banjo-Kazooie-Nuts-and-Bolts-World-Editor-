# Pause menu tab switching (static RE, 2026-09-27)

Goal: get from the pause menu to Photos & Videos → Take Photo in Xenia (keyboard HID). The menu opens on
"Game Options" and the tab bar does not change with any input we tried.

## 1. What changes the tab

The tab bar (`toolBarList`, pause object `+0xC0`) does not change tabs on its own. Tabs are changed by two hidden
buttons of the game's custom class `XuiClass_Button_c`: `leftButton` (`+0x108`) and `rightButton` (`+0x10C`).
The element names are bound in the menu init 0x825A6DB8 (for example `0x825A6E48 addi r4,…,0x8217011C` = L"leftButton"
→ `+0x108`, and 0x82170134 = L"rightButton" → `+0x10C`).

**Press keys (from the Pause XUR, bundle 685374 at +0x220957, scene id "Pause"):**

| button | PressKey | meaning |
|---|---|---|
| leftButton | 0x5823 | **VK_PAD_LTHUMB_LEFT** (previous tab) |
| rightButton | 0x5822 | **VK_PAD_LTHUMB_RIGHT** (next tab) |
| backButton | 0x5801 | B |
| aButton | 0x5800 | A |
| xButton | 0x5802 | X |
| playReplayButton | 0x5803 | Y |
| startButton | 0x5814 | START |
| dummyButton | 0x5817 | RTHUMB press |

The same leftButton/rightButton keys are used in the StartMenu XUR (757c4b) and TrophyThomasScene XUR (234cec).
The only input that switches tabs is the **left stick left/right, delivered as an XInput keystroke**. D-pad left/right,
LB/RB, LT/RT, the right stick, X and Y are not bound to the tab bar, so those not working is expected.
(Each button record in the XUR DATA ends in `01 01 00 00 58 xx 00`, and `58xx` is the press key.)

### Input path (all static, verified by disassembly)
1. UI manager 0x82279868 calls 0x82279D80 three times per frame. That reads one keystroke with
   `XamInputGetKeystrokeEx` (thunk 0x82D0823C through wrapper 0x821FD318). The user is the top scene's `+0x68`
   (255 = any), with flags `XINPUT_FLAG_GAMEPAD`.
2. If the keystroke is not a REPEAT, 0x823E7C90 builds game UI message **0x08000003** (key down; 0x08000002 = key up),
   and 0x823EC828 sends it to every registered listener.
3. The button class dispatcher 0x823E1A68 sends 0x08000003 → 0x823E20E0, 0x08000002 → 0x823E2218,
   0x08000001 (per-frame) → 0x823E2348 and 0x08000004 → 0x823E27C8. The PressKey is read at init 0x823E1C78 into `btn+0x208`.
4. 0x823E20E0 starts a press only when all of these hold:
   - `[0x82FAC72C] == 0`. This is a **global "a button press is in progress" lock**. It is set to the button in
     0x823E2010 and cleared by that button's own per-frame update after 0.25 s (float 0x8212DE88; 0x823E26A8).
   - The button element's XUI message 2002 is non-zero (bit0 of element flags, most likely *enabled*).
     The pause init enables left and right with message 2003(1) at 0x825A7378 and 0x825A7384.
   - `btn+0x258` (UnfocusedInput) != 0, or the message's focus object is the button.
   - The key equals PressKey (`btn+0x208`), and the per-key state slot is idle (state 3).
   - The scene's can-press check (vtable+8 = 0x823E5ED0) passes: `scene+0xC != 0 && scene+0x24 == 0`.
   - The gate at 0x823E20FC is not blocking: `msg+0x1C != 0` together with `[[mgr+0x2C]+0x114] != 0` rejects the press.
5. 0x823E1EF8 then immediately (on key **down**, KEYUP not needed) calls the scene's vtable+0x34. For the pause
   object (vtable 0x82178B08) that is **0x825A76E0** with r4 = button element.
6. 0x825A76E0:
   - `[0x82E514DC] != 0` (confirm dialog open) → ignore.
   - r4 == leftButton: needs `GetEnable(toolBarList)` (0x823E1A10, msg 2002) != 0 **and `[this+0x1C0] == 0`**.
     Then sel = GetCurSel(toolBarList) (0x8257AD00); if sel > 0, then sel-1.
   - r4 == rightButton: same checks; sel+1 if it is below the count (0x823E3998).
   - Then `0x8258CC78(toolBarList, sel)` (set selection) and `0x825A7D90(this, tabIds[sel])`
     (tabIds = 0x82FA32C4; shows the tab content and sets `this+0x1B8`).
   - The selection-changed notify 0x825A7BD8 only refreshes the title (0x825A7CF0). It never changes the content.

### Conditions that disable tab switching
- `this+0x1C0 != 0`: set by 0x825A8518 / 0x825A87B0 (Display / Audio tabs) when A enters the slider edit sub-mode.
  Those also disable left and right with msg 2003(0). B leaves the sub-mode. It is 0 when the menu opens (ctor 0x825A682C).
- The toolBarList "enabled" flag (msg 2002) is 0.
- A confirm dialog is open (`[0x82E514DC]`).
- The global button lock `[0x82FAC72C]` is still set by some other custom button. **Every** XuiClass_Button_c press is
  ignored until that button's update clears it.
- The scene is inactive or in transition (`scene+0xC == 0` or `scene+0x24 != 0`).
- There is no tutorial, demo or multiplayer check on the tab switch itself. Multiplayer and profile state only
  change which tabs are in the list (0x825A6708; see §2).

### Why it may fail in Xenia (not verified at runtime)
- Xenia's keyboard driver (`keyboard_input_driver.cc` GetKeystroke) maps an event only while the binding is still
  `is_pressed`. So **KEYUP keystrokes are never delivered**. The tab buttons fire on key down, so this alone
  should not block them. However, anything that waits for 0x08000002 (a button in "hold" state 2) never finishes.
- With the current config, left stick = keys `A` / `D` (xpad names `LS_LEFT` / `LS_RIGHT`). Nothing else changes tabs.
- If LS_LEFT/LS_RIGHT still do nothing, read these live with tools\xenia\xmem.py while the menu is open:
  `u32 [0x82FAC72C]` (must be 0) and `[0x82E514DC]` (must be 0). The pause object's `+0x1C0` / `+0x24` / `+0x0C`
  are other suspects.
- Diagnostic patch to ignore the global lock: 0x823E2124 `409A00E8` → `60000000`. It is not recommended as a permanent change.

## 2. Fallback: open the pause menu directly on Photos & Videos (recommended)

The tab list and the **initial tab id `this+0x1B4`** are built in 0x825A6708. In single player, whenever Game Options
is present (the path taken in town), it stores 0:

```
825A6AFC: 92BF01CC  stw r21,0x1cc(r31)      ; r21 = 1 (li r21,1 at 0x825A68DC, never changed)
825A6B00: 817F01CC  lwz r11,0x1cc(r31)
825A6B08: 2F0B0000  cmpwi cr6,r11,0
825A6B0C: 419A0018  beq ...                 ; (no game-settings tab → 0x1B4 = photos(1) or controls(2))
825A6B10: 576B103A  ...                     ; tabs[count++] = 0 (gamesettings)
825A6B1C: 93DF01B4  stw r30,0x1b4(r31)      ; initial tab = 0   <-- patch
```
0x825A6DB8 then ends with `0x825A7498 lwz r4,0x1b4(r31)` → `bl 0x825A7D90`, which shows the tab.
Id 1 dispatches (jump table 0x8212E600) to the photos builder 0x825A98E0. That builder shows `photosAndVideosList`,
gives it focus (0x829CB4A8) and sets `this+0x1B8 = 1`. A on the list then goes through press-switch case 1
(table 0x8212EC08) → **0x825A9A78** → item id 0 (Take Photo, 0x825A9FA8) → `0x825AA0D8 bl 0x82535A58` (photo-mode enter).

**Patch (1 word):**

| VA | original | new | meaning |
|---|---|---|---|
| 0x825A6B1C | 93DF01B4 | 92BF01B4 | `stw r21,0x1b4(r31)`: initial tab id 1 (photosandvideos) instead of 0 |

```toml
[[patch]]
    name = "RESEARCH: pause menu opens on Photos & Videos"
    desc = "Initial pause tab id (this+0x1B4, set in 0x825A6708) = 1 instead of 0."
    author = "NB Mod Tool"
    is_enabled = true

    [[patch.be32]]
        address = 0x825A6B1C
        value = 0x92BF01B4 # was 0x93DF01B4: stw r30 -> stw r21 (r21 == 1)
```

Side effects:
- The tab bar highlight and title still say "Game Options", because the toolBarList selection stays 0 and the
  title comes from 0x825A7CF0 with sel 0. The content, focus and press handler are Photos & Videos. This is cosmetic.
- Game Options becomes unreachable except through tab switching. Disable the patch when it is not needed.
- Photos & Videos must be in the list (`profile+0x70 == 0 && profile+0xCC == 0`). The observed list [0,4,1,2,5,6] shows it is.
- Take Photo (0x825A9FA8) also checks: a signed-in profile, `0x822BA478(profile)` (profile `+0xD4`, most likely the storage
  device) != 0, and `[[this+0x28]+0x54]+0x68 == 0`. If the storage check fails it calls 0x825AA8B0
  (probably a storage-device prompt) instead of entering photo mode.
  Only if that happens: 0x825AA030 `418200E4` → `60000000` skips it. Untested; saving a photo could then fail.

Not chosen: rewiring a Game Options item to 0x82535A58. It needs `r3 = [app+0x15B0]+0x40`, r4 = profile and a
callback 0x825AA4A0 (see 0x825AA0A0..0x825AA0D8), which is several words. The one-word initial-tab patch reaches
the same code unchanged.

## Confidence
- The tab switch is only leftButton/rightButton with PressKey LTHUMB_LEFT/RIGHT, handled in 0x825A76E0: **high**
  (XUR bytes plus disassembly of the whole chain).
- The list of blocking conditions: **high** for the code paths. Which one blocks it in Xenia: **unknown**, and the
  addresses above are for a live check.
- Message 2002/2003 = get/set *enabled*: **medium** (reads and writes bit0 of element flags; the init enables the two buttons).
- The initial-tab patch reaches Photos & Videos → Take Photo → 0x82535A58: **high** for the menu flow, **medium** for the
  Take Photo precondition (storage `profile+0xD4`) under Xenia.
