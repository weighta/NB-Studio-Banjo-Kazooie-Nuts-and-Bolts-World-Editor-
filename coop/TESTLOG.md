# Showdown Town co-op – test log

Two NB Multiplayer copies on one PC (`mp/uitest` host, `mp/uitest2` joiner), Steam relay (direct test socket), two muted
Xenia games with an all-unlocked save copied into both test profiles (their own saves backed up first). Harness: `mp/coop_flow.sh`, `ultra_flow.sh`, `cv_stress.sh`,
`mk6.sh`, `zapper.sh`, `faceoff.py`, `fight.py`, `coop_where.py`, `blocks.py`, `hurt.py`, `mailbox.py`, `alive.py`.

| # | Date | Scenario | Result |
|---|---|---|---|
| C1 | 2026-09-30 | Join + town, puppets follow (release 1.1.0) | median 0.3 u, max 3.1 u while driving |
| C2 | 2026-10-01 | **All parts unlocked** (tweak `developer-all-parts` in the co-op edition) with an existing save and with a test save that only had the Mk. 1 trolley | Parts inventory 118 entries in both games (stock save ≈ 23); Mumbo's Motors shows all 12 categories incl. Accessories/Protection, every weapon available |
| C3 | 2026-10-01 | **Change Vehicle in both games at the same moment** (Trolley Mk. 6 ↔ L.O.G. Tractor/Chip Racer), 7 rounds | Both games keep running; after every round each puppet is 0.1–2 u from the other player's new vehicle (5 u when both stand on the same spot: the keep-apart rule). Before the fix: a write into a destroyed puppet could corrupt the game (one freeze, one GPU crash) – fixed by liveness checks and no writes into puppet blocks |
| C4 | 2026-10-01 | **Mumbo's Motors**: one player builds while the other drives; both in the garage at once; Vehicle Database loads (L.O.G. Tractor), Workshop part placement (egg turret on the tractor), Exit Garage | Town sync resumes after leaving the garage in both games; the garage is per player (no interference) |
| C5 | 2026-10-01 | **ULTRA Parts** mod + co-op + all parts (`Add co-op + ULTRA Parts`) | Edition builds, joiner builds the same recipe; both inventories have ULTRA Engine/Fuel/Ammo/Wheel, Plane Hull, Tiki, Fusion Reactor (125 entries); ULTRA listed under Power > Engines |
| C6 | 2026-10-01 | **Weapons between players**: game 1 (Trolley Mk. 6 laser) fires at game 2's puppet | Puppet takes no damage in game 1 (hits logged, 10 laser hits = 120 damage, material 0x2F); NB Multiplayer forwards them; game 2's own block damage applies them: game 2's L.O.G. Tractor 56 → 53 blocks, 3 parts broke off and lie in the street as loose parts (with the game's RB magnet prompt). Ground contacts (material 0) and vehicle collisions (material 2) are not forwarded |
| C7 | 2026-10-01 | Rockets (L.O.G. Lunar Zapper) | Rockets arc over a target 10–20 u away in this harness: no hit landed, so explosion forwarding (hook at 0x82612870) is in place but **not verified in game** |
| C8 | 2026-10-01 | Update path: a 1.1 co-op edition → `Update co-op edition` | Rebuilt as Co-op 1.1 + All parts unlocked |

Exe mod `coop-remote-damage` (assembled by `coop/remote_damage_asm.py`): hooks 0x825F1AE0 (contact pass → mailbox
apply), 0x825F17E8 (block damage → puppet hit log) and 0x82612870 (explosion damage → puppet hit log). Code caves: the
zero padding after `.text` (0x82D09230…) and after the first `.embsec_` section (0x82D21270…); mailbox / hit ring in the
unused tail of `.data` (0x82FBCB00…0x82FBCBD0, below `.tls` at 0x82FBCC00).

Known limits: players on foot are not shown; everyone appears as the standard trolley; spikes/rams/boot-in-a-box hits
on a puppet are collisions (not forwarded), the real collision happens in both games anyway.
