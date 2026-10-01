# 15.7 Recovered (unused) vehicle parts — 2026-09-28

## Inventory of unused parts

`tools/probe/partsets.py` decodes every blockset: `aid_misc_banjox_blockset_*`, which are the crates, log crates,
start packs and `blockset_all`. Blockset data is a list of (u32 count, u32 objparams id) pairs, and the id is
0x1F << 24 | ~crc32(name without "aid_objparams_") & 0xFFFFFF.

Of the 153 `vehicleblock` objparams, **35 are in no blockset at all**, including `blockset_all` (the developer list
`garage_allblocks`, id 0x0BE788E4, bundles 20abf9, 4bf033 and streamed 685374). The player can never get them:

| Group | Parts |
|---|---|
| Requested | **Olympic Torch** (`miscellaneous_logolympictorch`), **Grunty's Seat** (`secondaryseats_grunty`, `…gruntyairtight`), **Remote Control** (`gadgets_remotecontrol`), **Auto-Pilot** (`gadgets_autopilot`) |
| Other gadgets | `gadgets_blower`, `gadgets_sucker`, `gadgets_springai`, `gadgets_springtrolley`, `gadgets_variants_spotlightalwayson`, `gadgets_variants_energyshieldnobghits` |
| Seats | `secondaryseats_small`, `…large`, `…pikelet`, `…pikeletpassenger`, `seats_standardcutscene` |
| Misc | `miscellaneous_storage_eggnspoontray`, `miscellaneous_weights_floatergrunty`, `wheels_highgripheavy`, `weapon_eggturretfixed`, `weapon_piddlesturret`, `body_light_poleconnector`, `propulsion_propellers_unpowered`, AI engines and jets (`propulsion_engines_ai_*`, `propulsion_jets_ai_*`), and editor-only parts (`base_attachpoint`, `base_leakpoint`, `base_gameplaycreatorloactor`, `miscellaneous_gameplaycreatorpart*`) |

Every one has a complete objparams record (class, model and stats). Shipped vehicles use the Grunty seat
(22 blueprints) and the Olympic Torch (2); Remote Control and Auto-Pilot are used by no shipped vehicle. The game also
ships store description dialogs for Remote Control, Auto-Pilot, Blower, Sucker and the high-grip wheel
(`aid_dialog_banjox_garage_description_block_*`).

## What was done and verified

1. **Into the parts inventory.** New `NB.Cli blockset-add <ws> aid_misc_banjox_blockset_all <count> <parts…>` appends
   (count, id) pairs to every resident and streamed copy. With the `developer-all-parts` mod, a new game's inventory
   [0x82FACA44] has **129 entries (118 + 11)**, and the Olympic Torch, Grunty's Seat, Remote Control, Auto-Pilot, Blower,
   Sucker, Spotlight (always on), Heavy high-grip wheel, Egg'n'spoon tray and small/large secondary seats are all in
   it. *Verified by reading guest memory.*
2. **In a vehicle.** A data blueprint "recovered" was written into the Trolley Mk.7 slot: Mk.7 with the Remote Control
   (in place of the spring, button kept), the Auto-Pilot (in place of the horn), the Olympic Torch and a Grunty seat.
   - The game spawns it in Showdown Town. Its name shows in the parts overlay.
   - Change Vehicle → Town Blueprints lists "recovered" (7/7) and draws its preview: seat at the back, gadgets on top.
   - The recovered part ids are live in guest memory: Torch ×8, Grunty seat ×8, Remote Control ×13, Auto-Pilot ×13.
   - *Verified in Xenia.*

## Not solved: the parts-store listing

The Mumbo's Motors store does not list the recovered parts, even when they are in the inventory. Tried:

- Filling the empty store-description dialog id at objparams **+0x120** (new `NB.Cli objparams-set`) with the
  shipped descriptions. No change.

The store groups parts by the category string (+0x228, e.g. "seat") and the type/size strings (+0x268/+0x2A8, e.g.
"passenger"/"small"). It appears to show only type names it has a UI entry for. The unused parts carry types that
appear nowhere else ("grunty", "passengersmallai", …). Making them buyable needs that UI table (probably an xui/loctext
entry per type), which was not located.

**Workaround.** Recovered parts can be used today through blueprints (data blueprints, or Change Vehicle / Town
Blueprints as above).

## Tools

- `tools/probe/partsets.py [part …]`: which blocksets contain a part.
- `NB.Cli blockset-add` and `NB.Cli objparams-set`.
- A recovered-parts blueprint builder is shown in this file's history (`work/recovered_trolley.bin`).
