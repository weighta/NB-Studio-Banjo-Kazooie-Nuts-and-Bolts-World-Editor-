using System.IO.Hashing;
using System.Text;
using NB.Core.Formats;
using NB.Core.IO;

namespace NB.Core.Mods;

/// <summary>One 32-bit instruction/data patch in default.xex's memory image.</summary>
public sealed record ExeWord(uint Address, uint Original, uint Patched, string Comment);

/// <summary>An executable-level mod (a set of word patches), with how far it has been verified.</summary>
public sealed record ExeMod(string Id, string Name, string Description, string Verified, IReadOnlyList<ExeWord> Words);

/// <summary>
/// Executable patches for the retail default.xex (title 4D5307ED). They are applied as a Xenia Canary patch file
/// (patches/4D5307ED*.patch.toml, keyed by the module hash Xenia computes) — the original XEX is never modified.
/// Every patch checks the original instruction words against the decrypted image before it is written.
/// </summary>
public static class ExePatches
{
    public const uint TitleId = 0x4D5307ED;

    public static readonly ExeMod ChangeVehicleInTown = new(
        "change-vehicle-town",
        "Change Vehicle / Build Vehicle in Showdown Town",
        "The pause menu adds 'Change Vehicle' and 'Build Vehicle' only when function 0x82577DA8 returns true. In Showdown " +
        "Town it returns false because (a) the current level's settings byte +0xD4 lacks bit 0x08 and (b) the game object's " +
        "+0x58 field is 1. Both checks are skipped; the jiggy-challenge, multiplayer and 'has a vehicle' checks are kept.",
        "Verified in Xenia: both items appear in Showdown Town's pause menu; Change Vehicle opens the Load Blueprint screen and " +
        "the selected vehicle is delivered; Build Vehicle opens Mumbo's garage. With the trolley upgrades unlocked (7 Town Blueprints) " +
        "selecting Trolley Mk. 2 in Showdown Town replaced the upgraded trolley with the Mk. 2 on the spot.",
        new[]
        {
            new ExeWord(0x82577DF8, 0x4182006C, 0x60000000, "beq -> nop: level settings +0xD4 bit 0x08 (vehicles allowed in this level)"),
            new ExeWord(0x82577E04, 0x419A0060, 0x60000000, "beq -> nop: game object +0x58 == 1 (Showdown Town mode)"),
        });

    public static readonly ExeMod TownVehiclesNormalRules = new(
        "town-vehicles-normal-rules",
        "Destructible vehicles in Showdown Town (town vehicles use normal-world rules)",
        "When a vehicle is spawned for the player (0x825697C8), Showdown Town (game object +0x58 == 1) takes a special branch that " +
        "sets vehicle flags 0x410 (0x10 = damage immunity that never expires, 0x400 = no wrench interaction), increments +0xCEC " +
        "(turns off impact damage and the break-up rebuild) and sets +0xEB4/+0xEB8 (fuel/ammo gauges hidden, weapons off). The " +
        "branch is skipped, so town vehicles take block damage and break apart like in the other worlds. Optional: the block " +
        "damage multiplier is aid_misc_banjox_vehicleblockglobals_default .data+0x4 (1.0; e.g. 50 makes crashes tear vehicles apart).",
        "Verified in Xenia: in Showdown Town the trolley's blocks lose health in wall crashes (block health +0x270 read from guest " +
        "memory) and, with the multiplier raised, repeated rams break the vehicle apart (14 blocks -> 2 -> 1, wheels/fuel tank lying " +
        "in the street, Mumbo's 'Vehicle take damage and lose parts' tutorial). Fuel/ammo gauges show and weapons fire in town.",
        new[]
        {
            new ExeWord(0x82569B1C, 0x419A0034, 0x60000000, "beq -> nop: owner +0x58 == 1 (Showdown Town) vehicle spawn branch"),
        });

    public static readonly ExeMod DeveloperMainMenu = new(
        "developer-main-menu",
        "Developer main menu at the title screen",
        "The game still ships the developer main menu (scene MainMenu, script aid_script_banjox_ui_frontend_main, bundle 5906c3) " +
        "but only the retail entry (Bottles' house, 0x1936DC87) is used. The title-screen Start handler init (0x824735C0) is " +
        "changed to store the debug script id 0x195906C3 instead of its objparams value, so pressing Start opens the developer menu: " +
        "Enter Garage, Start New Game, Resume Save, Multiplayer, Play Demo Level, Demo Messing Around, Unlocked / Auto Progression.",
        "Verified in Xenia: the title screen opens the developer MAIN MENU; Enter Garage loads Mumbo's Motors directly. " +
        "The Unlocked / Auto Progression checkboxes have no code behind them. Do NOT use Play Demo Level / Demo Messing: their " +
        "bundles (a9cb7b, 4ee58d) are not on the disc.",
        new[]
        {
            new ExeWord(0x824735C0, 0x814400A4, 0x3D401959, "lis r10,0x1959   (was lwz r10,0xA4(r4): retail main-menu script id)"),
            new ExeWord(0x824735C4, 0x7C6B1B78, 0x614A06C3, "ori r10,r10,0x06C3 -> 0x195906C3 aid_script_banjox_ui_frontend_main"),
            new ExeWord(0x824735C8, 0x38600001, 0x91430070, "stw r10,0x70(r3)"),
            new ExeWord(0x824735CC, 0x914B0070, 0x38600001, "li r3,1"),
        });

    public static readonly ExeMod WorldBounds2048 = new(
        "world-bounds-2048",
        "Larger world boundary (+/-2048 around the level on X/Z)",
        "Leaving the Havok broadphase box sends objMsgId_Avatar_EscapedBackground and Banjo is reset (the 'map reset'). " +
        "The box is built at level load by 0x822EB698 as the level collision AABB ± 100. This mod keeps ±100 on Y (so falling " +
        "out of the world is still caught) and uses ±2048 on X and Z.",
        "Verified in Xenia (guest memory): Showdown Town box grows from (-556,-116,-622)..(525,314,757) to " +
        "(-2504,-116,-2570)..(2473,314,2705) in both the level object and the hkpWorld. The reset mechanism was verified by " +
        "the inverse test (box shrunk so the spawn lies outside: the level reloads in a loop). Beyond the old edge: a duplicated " +
        "bridge placed at z = 826 with the player spawn on it - with the mod Banjo stands and moves there (z = 831) with no reset; " +
        "without it the same setup reloads the level endlessly.",
        new[]
        {
            new ExeWord(0x822EB8A0, 0x38E00001, 0xC0E80AE0, "lfs f7,0xAE0(r8): f7 = 100.0 for the Y margin (was li r7,1)"),
            new ExeWord(0x822EB8AC, 0xC0080AE0, 0xC0080C00, "lfs f0,0xC00(r8): X/Z margin 2048.0 (was 100.0)"),
            new ExeWord(0x822EB8B4, 0x98E30011, 0x99230011, "stb r9,0x11(r3): 'box valid' flag from r9 (non-zero) since r7 is reused"),
            new ExeWord(0x822EB918, 0xED080028, 0xED083828, "fsubs f8,f8,f7: min.y - 100"),
            new ExeWord(0x822EB92C, 0xEDAB002A, 0xEDAB382A, "fadds f13,f11,f7: max.y + 100"),
        });

    public static readonly ExeMod NoEscapeReset = new(
        "no-escape-reset",
        "Unlimited world: no reset when leaving the world box",
        "Leaving the Havok broadphase box (flying past the edge or above the sky, or falling below the map) queues the object " +
        "in the game's border callback 0x823C1680; the per-frame flush 0x823C17B8 then sends objMsgId_Avatar_EscapedBackground " +
        "(22), and Banjo's handler kills and respawns him (0x8225802C -> 0x823D6640). Here the flush never sends the message " +
        "(the queue is still cleared), so the player keeps flying or falling. Havok keeps simulating bodies outside the box " +
        "with their broadphase AABBs clamped to the border, which stays conservative (collisions are still found). Combine " +
        "with world-bounds-2048 for exact broadphase over a wide area. Falling below the map continues forever (use the pause " +
        "menu to leave).",
        "Verified in Xenia 2026-09-28 (Seattle patch 4 with world-bounds-2048, baked default.xex): vehicle parked at y 300 " +
        "(ceiling 121.7), at x 3600 and at (-3500, 40, -3000) (box x/z +/-2808 / -2508..3468) and at y -400 (floor -130.75): " +
        "no respawn at any of them; with gravity restored the vehicle kept falling from -400 to -854. Without the mod (patch 3) " +
        "parking the vehicle at y 120, just under the 121.7 ceiling, already respawned the player at the spawn.",
        new[]
        {
            new ExeWord(0x823C17F4, 0x419A002C, 0x4800002C, "b 0x823C1820: never send EscapedBackground from the border flush (was beq)"),
        });

    public static readonly ExeMod AiSpringTimer = new(
        "ai-spring-timer",
        "AI drivers fire their vehicle's springs on a timer (jumping AI vehicles)",
        "Vehicles spawned from AI vehicle markers never get button assignments (only the player-vehicle spawn 0x825697C8 " +
        "copies blueprint +0x1C/+0x20 into the blocks' active masks, block+0x3CC), so an AI can't fire gadgets through its " +
        "virtual pad. The Jogger path-following strategy's helper 0x824AB9C0 (one caller, state 'buggy round course', " +
        "grunty_galleon) is replaced by a routine called from the Jogger update's common tail (0x824AB328), used by every " +
        "state: when the strategy's objparams +0x288 is > 1, it presses every spring block (block type 26 at +0x464) of the " +
        "driver's vehicle directly (0x826270E8, like Grunty's SpringBreak strategy) while game tick bit 0x20 is set and " +
        "releases them (0x825F0198) while it is clear: about 1 s held, 1 s released. Other strategies (+0x288 <= 1) are " +
        "unaffected; grunty_galleon loses its target-based button press.",
        "Traced statically and read live in Xenia (sub-agent, 2026-09-29); in-game result: ultra/TESTLOG.md.",
        new[]
        {
            new ExeWord(0x824AB328, 0x917F0090, 0x48000699, "bl 0x824AB9C0 (was stw r11,0x90(r31), moved into the helper)"),
            new ExeWord(0x824AB3D4, 0x480005ED, 0x60000000, "nop: old target-based call of the helper"),
            new ExeWord(0x824AB9C0, 0x7D8802A6, 0x7D8802A6, "prologue (save r27-r31, lr)"),
            new ExeWord(0x824AB9C4, 0x48708A39, 0x48708A31, "call save helper"),
            new ExeWord(0x824AB9C8, 0x9421FF30, 0x9421FF90, "stwu r1,-0x70(r1)"),
            new ExeWord(0x824AB9CC, 0x81640AC4, 0x917F0090, "stw r11,0x90(r31) (moved from the hook site)"),
            new ExeWord(0x824AB9D0, 0x816B0038, 0x837F00AC, "lwz r27,0xAC(r31)  objparams +0x288"),
            new ExeWord(0x824AB9D4, 0x2B0B0000, 0x2B1B0001, "cmplwi cr6,r27,1"),
            new ExeWord(0x824AB9D8, 0x419A013C, 0x40990070, "ble -> return (not enabled)"),
            new ExeWord(0x824AB9DC, 0x394000D0, 0x83DC0C3C, "lwz r30,0xC3C(r28)  driver's vehicle"),
            new ExeWord(0x824AB9E0, 0xC1A408BC, 0x2B1E0000, "cmplwi cr6,r30,0"),
            new ExeWord(0x824AB9E4, 0x39210060, 0x419A0064, "beq -> return"),
            new ExeWord(0x824AB9E8, 0xC18400A8, 0x3D6082F8, "lis r11,0x82F8"),
            new ExeWord(0x824AB9EC, 0x3D008200, 0x816BB7D0, "lwz r11,-0x4830(r11)  tick counter 0x82F7B7D0"),
            new ExeWord(0x824AB9F0, 0x1001038C, 0x556B06B5, "rlwinm. r11,r11,0,26,26  bit 0x20"),
            new ExeWord(0x824AB9F4, 0x38E00050, 0x40820014, "bne -> press"),
            new ExeWord(0x824AB9F8, 0xED8D0332, 0x7FC3F378, "mr r3,r30"),
            new ExeWord(0x824AB9FC, 0x38C10060, 0x3880001A, "li r4,26  block type spring"),
            new ExeWord(0x824ABA00, 0xC00B00E4, 0x48144799, "bl 0x825F0198  release all springs"),
            new ExeWord(0x824ABA04, 0x118B50C3, 0x48000044, "b -> return"),
            new ExeWord(0x824ABA08, 0x396400F0, 0x83BE1488, "lwz r29,0x1488(r30)  block list"),
            new ExeWord(0x824ABA0C, 0x118049C3, 0x837E148C, "lwz r27,0x148C(r30)"),
            new ExeWord(0x824ABA10, 0x11A1034A, 0x7F1DD840, "cmplw cr6,r29,r27"),
            new ExeWord(0x824ABA14, 0xC1A80C74, 0x40980034, "bge -> return"),
            new ExeWord(0x824ABA18, 0x39400020, 0x817D0000, "lwz r11,0(r29)  entry flags"),
            new ExeWord(0x824ABA1C, 0xC1610064, 0x556B077B, "rlwinm. r11,r11,0,29,29  0x4 = gadget"),
            new ExeWord(0x824ABA20, 0xEC005B7A, 0x41820020, "beq -> next"),
            new ExeWord(0x824ABA24, 0x100438C3, 0x807D0004, "lwz r3,4(r29)  block"),
            new ExeWord(0x824ABA28, 0x39210090, 0xA1630464, "lhz r11,0x464(r3)  block type"),
            new ExeWord(0x824ABA2C, 0xD0010064, 0x2B0B001A, "cmplwi cr6,r11,26"),
            new ExeWord(0x824ABA30, 0x39010090, 0x409A0010, "bne -> next"),
            new ExeWord(0x824ABA34, 0x118030C3, 0x38A00000, "li r5,0"),
            new ExeWord(0x824ABA38, 0x118C004A, 0x7FC4F378, "mr r4,r30"),
            new ExeWord(0x824ABA3C, 0x156C6190, 0x4817B6AD, "bl 0x826270E8  press (fires the spring)"),
            new ExeWord(0x824ABA40, 0x100B50C3, 0x3BBD00B0, "addi r29,r29,0xB0"),
            new ExeWord(0x824ABA44, 0x100049C3, 0x4BFFFFCC, "b -> loop"),
            new ExeWord(0x824ABA48, 0x1120038C, 0x38210070, "addi r1,r1,0x70"),
            new ExeWord(0x824ABA4C, 0x114040C3, 0x487089F8, "b 0x82BB4444  restore r27-r31, lr, return"),
        });

    public static readonly ExeMod TownAiRestartOnChangeVehicle = new(
        "town-ai-restart-on-change-vehicle",
        "Change Vehicle restarts the town's AI vehicles (instead of leaving their drivers on the ground)",
        "Pause -> Change Vehicle runs in the player update 0x82251040: after the fade it purges EVERY vehicle in the level " +
        "(0x8255EC20(g, 0, 1, 0) at 0x822513AC: seat occupants are ejected, vehicles deleted) and spawns the new player " +
        "vehicle (bl 0x82569D00 at 0x8225142C). The AI fleet's drivers were left standing and their vehicles gone. The " +
        "spawn call is redirected to a routine in the padding after .text (0x82D09340) that makes the call and then, in " +
        "Showdown Town (game object +0x58 == 1) and when the purge ran ([0x82FAC5D0] == 0), despawns marker set 0x00010000 " +
        "(0x823DEC80, what script op 0x66 does: deletes the ejected AI drivers) and spawns it again (0x823C6458, what op " +
        "0x65 does), so every AI vehicle and driver restarts at its spawn point. The same pair the game uses when a " +
        "challenge ends (0x8251F208). Requires the AI spawn records to be in marker set 0x00010000 (NB.Cli ai-route --mask).",
        "Traced statically and live (sub-agent, 2026-09-29); in-game result: ultra/TESTLOG.md.",
        new[]
        {
            new ExeWord(0x8225142C, 0x483188D5, 0x48AB7F15, "bl 0x82D09340 (was bl 0x82569D00: spawn the new player vehicle)"),
            new ExeWord(0x82D09340, 0x00000000, 0x7D8802A6, "mflr r12"),
            new ExeWord(0x82D09344, 0x00000000, 0x9181FFF8, "stw r12,-0x8(r1)"),
            new ExeWord(0x82D09348, 0x00000000, 0x9421FFA0, "stwu r1,-0x60(r1)"),
            new ExeWord(0x82D0934C, 0x00000000, 0x4B8609B5, "bl 0x82569D00  original spawn of the new player vehicle"),
            new ExeWord(0x82D09350, 0x00000000, 0x81780058, "lwz r11,0x58(r24)  game mode"),
            new ExeWord(0x82D09354, 0x00000000, 0x2F0B0001, "cmpwi cr6,r11,1  Showdown Town only"),
            new ExeWord(0x82D09358, 0x00000000, 0x409A004C, "bne -> return"),
            new ExeWord(0x82D0935C, 0x00000000, 0x3D6082FB, "lis r11,0x82FB"),
            new ExeWord(0x82D09360, 0x00000000, 0x816BC5D0, "lwz r11,-0x3A30(r11)  [0x82FAC5D0] != 0: purge was skipped"),
            new ExeWord(0x82D09364, 0x00000000, 0x2F0B0000, "cmpwi cr6,r11,0"),
            new ExeWord(0x82D09368, 0x00000000, 0x409A003C, "bne -> return"),
            new ExeWord(0x82D0936C, 0x00000000, 0x7F03C378, "mr r3,r24"),
            new ExeWord(0x82D09370, 0x00000000, 0x3C800001, "lis r4,1  marker set 0x00010000"),
            new ExeWord(0x82D09374, 0x00000000, 0x38A00000, "li r5,0"),
            new ExeWord(0x82D09378, 0x00000000, 0x38C00000, "li r6,0"),
            new ExeWord(0x82D0937C, 0x00000000, 0x4B6D5905, "bl 0x823DEC80  despawn marker set (script op 0x66): removes the ejected AI drivers"),
            new ExeWord(0x82D09380, 0x00000000, 0x807807D4, "lwz r3,0x7D4(r24)  marker manager"),
            new ExeWord(0x82D09384, 0x00000000, 0x7F04C378, "mr r4,r24"),
            new ExeWord(0x82D09388, 0x00000000, 0x80A30000, "lwz r5,0x0(r3)  marker set 0"),
            new ExeWord(0x82D0938C, 0x00000000, 0x80C30004, "lwz r6,0x4(r3)  marker set 1"),
            new ExeWord(0x82D09390, 0x00000000, 0x3CE00001, "lis r7,1  mask 0x00010000"),
            new ExeWord(0x82D09394, 0x00000000, 0x39000000, "li r8,0"),
            new ExeWord(0x82D09398, 0x00000000, 0x39200000, "li r9,0"),
            new ExeWord(0x82D0939C, 0x00000000, 0x39400000, "li r10,0"),
            new ExeWord(0x82D093A0, 0x00000000, 0x4B6BD0B9, "bl 0x823C6458  spawn marker set (script op 0x65): fresh AI vehicles + drivers at their spawns"),
            new ExeWord(0x82D093A4, 0x00000000, 0x38210060, "addi r1,r1,0x60"),
            new ExeWord(0x82D093A8, 0x00000000, 0x8181FFF8, "lwz r12,-0x8(r1)"),
            new ExeWord(0x82D093AC, 0x00000000, 0x7D8803A6, "mtlr r12"),
            new ExeWord(0x82D093B0, 0x00000000, 0x4E800020, "blr"),
        });

    public static readonly ExeMod TownNpcPathGuard = new(
        "town-npc-path-guard",
        "Fix: Change Vehicle crash in town (NPC pathfinding during the vehicle swap)",
        "Found on a real console with XBDM (crash watcher, 2026-09-29): an access violation reading address 0 at 0x82236028 on " +
        "the main thread. A town NPC's wander update (0x824B1440, random target) asks the pathfinding query 0x823D9CE8 for a " +
        "valid spot; that query takes the mesh from [[[obj+0x9B0]+8]+4] (a PathEngine-style query: position x 100 as " +
        "integers, +-1,500,000 range) and calls into it through 0x82235F98. While Change Vehicle swaps the player's vehicle " +
        "the mesh pointer is briefly null, so an NPC update that lands in that window crashes (roughly 4 swaps in 5 on the " +
        "console, never in Xenia). The query now returns 'no result' (its own failure exit 0x823D9E20, which writes no " +
        "outputs) when the context, its sub-object or the mesh is null; every caller already handles that result. The " +
        "check lives in the zero padding after .text (0x82D09300, 40 bytes, no function there).",
        "",
        new[]
        {
            new ExeWord(0x823D9D2C, 0x83840008, 0x4892F5D4, "b 0x82D09300 (was lwz r28,0x8(r4))"),
            new ExeWord(0x82D09300, 0x00000000, 0x2B040000, "cmplwi cr6,r4,0 (path context)"),
            new ExeWord(0x82D09304, 0x00000000, 0x419A0020, "beq -> fail"),
            new ExeWord(0x82D09308, 0x00000000, 0x83840008, "lwz r28,0x8(r4) (moved)"),
            new ExeWord(0x82D0930C, 0x00000000, 0x2B1C0000, "cmplwi cr6,r28,0"),
            new ExeWord(0x82D09310, 0x00000000, 0x419A0014, "beq -> fail"),
            new ExeWord(0x82D09314, 0x00000000, 0x83BC0004, "lwz r29,0x4(r28) (the pathfinding mesh)"),
            new ExeWord(0x82D09318, 0x00000000, 0x2B1D0000, "cmplwi cr6,r29,0"),
            new ExeWord(0x82D0931C, 0x00000000, 0x419A0008, "beq -> fail"),
            new ExeWord(0x82D09320, 0x00000000, 0x4B6D0A10, "b 0x823D9D30 (continue)"),
            new ExeWord(0x82D09324, 0x00000000, 0x4B6D0AFC, "fail: b 0x823D9E20 (li r3,0: no result)"),
        });

    public static readonly ExeMod CoopRemoteDamage = new(
        "coop-remote-damage",
        "Co-op: damage from other players (NB Multiplayer applies hits on your vehicle through the game's own block damage)",
        "Showdown Town co-op runs each player's own game; the other players are puppet vehicles (blueprint 0x00EA74B4). Block " +
        "damage reaches the health routine 0x82203D00 per contact through 0x825F17E8(vehicle, block, f1 damage, ..., r8 contact) and " +
        "per block of an explosion through 0x826127D8 (call at 0x82612870). Both are hooked (0x82D2129C, 0x82D212CC): for a puppet " +
        "the hit is logged by 0x82D21270 in an 8-entry ring in the unused tail of .data (0x82FBCB00 +0x40 count, +0x50 + 16 x (count " +
        "& 7): vehicle, f32 damage, the contact's other material at +0xB0 or -1 for explosions) and nothing is damaged, so puppets " +
        "never break; NB Multiplayer sends the damage to the puppet's player. The contact-damage pass 0x825F1AE0(vehicle) starts " +
        "with a branch to 0x82D09230: when the mailbox (+0 request seq, +4 done seq, +8 vehicle, +0xC block, +0x10 f32 damage, " +
        "+0x20 vec4 contact point) holds a new request for this vehicle, it is marked done and 0x825F17E8(vehicle, block, damage, " +
        "&point, 1, 0) applies it: the game's own block damage (at zero health 0x825F1C60 breaks the block off). Code caves: " +
        "padding after .text and after the first .embsec_ section. Assembled by coop/remote_damage_asm.py.",
        "Static analysis 2026-10-01 (block damage path traced from the health field +0x270); in-game result: coop/TESTLOG.md.",
        new[]
        {
            new ExeWord(0x825F1AE0, 0x7D8802A6, 0x48717750, "b 0x82d09230 (was mflr r12): co-op remote damage"),
            new ExeWord(0x82D09230, 0x00000000, 0x3D6082FC, "lis r11,0x82fc"),
            new ExeWord(0x82D09234, 0x00000000, 0x396BCB00, "addi r11,r11,-0x3500  r11 = mailbox 0x82fbcb00"),
            new ExeWord(0x82D09238, 0x00000000, 0x818B0000, "lwz r12,0(r11)  request seq"),
            new ExeWord(0x82D0923C, 0x00000000, 0x800B0004, "lwz r0,4(r11)  done seq"),
            new ExeWord(0x82D09240, 0x00000000, 0x7F0C0000, "cmpw cr6,r12,r0"),
            new ExeWord(0x82D09244, 0x00000000, 0x419A0074, "beq cr6 -> nothing pending"),
            new ExeWord(0x82D09248, 0x00000000, 0x800B0008, "lwz r0,8(r11)  vehicle"),
            new ExeWord(0x82D0924C, 0x00000000, 0x7F001800, "cmpw cr6,r0,r3"),
            new ExeWord(0x82D09250, 0x00000000, 0x409A0068, "bne cr6 -> not this vehicle"),
            new ExeWord(0x82D09254, 0x00000000, 0x918B0004, "stw r12,4(r11)  done seq = request seq"),
            new ExeWord(0x82D09258, 0x00000000, 0x808B000C, "lwz r4,0xC(r11)  block"),
            new ExeWord(0x82D0925C, 0x00000000, 0x81231488, "lwz r9,0x1488(r3)  block entries"),
            new ExeWord(0x82D09260, 0x00000000, 0x8143148C, "lwz r10,0x148C(r3)  end"),
            new ExeWord(0x82D09264, 0x00000000, 0x7F095040, "cmplw cr6,r9,r10"),
            new ExeWord(0x82D09268, 0x00000000, 0x40980050, "bge cr6 -> block no longer on this vehicle"),
            new ExeWord(0x82D0926C, 0x00000000, 0x80090004, "lwz r0,4(r9)  entry block"),
            new ExeWord(0x82D09270, 0x00000000, 0x7F002040, "cmplw cr6,r0,r4"),
            new ExeWord(0x82D09274, 0x00000000, 0x419A000C, "beq cr6 -> found"),
            new ExeWord(0x82D09278, 0x00000000, 0x392900B0, "addi r9,r9,0xB0"),
            new ExeWord(0x82D0927C, 0x00000000, 0x4BFFFFE8, "b loop"),
            new ExeWord(0x82D09280, 0x00000000, 0x7D8802A6, "mflr r12"),
            new ExeWord(0x82D09284, 0x00000000, 0x9181FFF8, "stw r12,-0x8(r1)"),
            new ExeWord(0x82D09288, 0x00000000, 0x9421FFA0, "stwu r1,-0x60(r1)"),
            new ExeWord(0x82D0928C, 0x00000000, 0x90610050, "stw r3,0x50(r1)  keep the vehicle for the original function"),
            new ExeWord(0x82D09290, 0x00000000, 0xC02B0010, "lfs f1,0x10(r11)  damage"),
            new ExeWord(0x82D09294, 0x00000000, 0x38CB0020, "addi r6,r11,0x20  contact point"),
            new ExeWord(0x82D09298, 0x00000000, 0x38A00000, "li r5,0"),
            new ExeWord(0x82D0929C, 0x00000000, 0x38E00001, "li r7,1"),
            new ExeWord(0x82D092A0, 0x00000000, 0x39000000, "li r8,0  no contact record"),
            new ExeWord(0x82D092A4, 0x00000000, 0x4B8E8545, "bl 0x825f17e8  the game's block damage (breaks the block off when its health runs out)"),
            new ExeWord(0x82D092A8, 0x00000000, 0x80610050, "lwz r3,0x50(r1)"),
            new ExeWord(0x82D092AC, 0x00000000, 0x38210060, "addi r1,r1,0x60"),
            new ExeWord(0x82D092B0, 0x00000000, 0x8181FFF8, "lwz r12,-0x8(r1)"),
            new ExeWord(0x82D092B4, 0x00000000, 0x7D8803A6, "mtlr r12"),
            new ExeWord(0x82D092B8, 0x00000000, 0x7D8802A6, "mflr r12  (the instruction the hook replaced)"),
            new ExeWord(0x82D092BC, 0x00000000, 0x4B8E8828, "b 0x825f1ae4  back into the contact-damage pass"),
            new ExeWord(0x825F17E8, 0x7D8802A6, 0x4872FAB4, "b 0x82d2129c (was mflr r12): puppet hits are logged, not applied"),
            new ExeWord(0x82612870, 0x4BBF1491, 0x4870EA5D, "bl 0x82d212cc (was bl 0x82203d00): explosion damage on puppets is logged, not applied"),
            new ExeWord(0x82D21270, 0x00000000, 0x3D6082FC, "lis r11,0x82fc"),
            new ExeWord(0x82D21274, 0x00000000, 0x396BCB00, "addi r11,r11,-0x3500  mailbox"),
            new ExeWord(0x82D21278, 0x00000000, 0x812B0040, "lwz r9,0x40(r11)  hit count"),
            new ExeWord(0x82D2127C, 0x00000000, 0x552A2676, "rlwinm r10,r9,4,25,27  (count & 7) * 16"),
            new ExeWord(0x82D21280, 0x00000000, 0x7D4A5A14, "add r10,r10,r11"),
            new ExeWord(0x82D21284, 0x00000000, 0x906A0050, "stw r3,0x50(r10)  puppet vehicle"),
            new ExeWord(0x82D21288, 0x00000000, 0xD02A0054, "stfs f1,0x54(r10)  damage"),
            new ExeWord(0x82D2128C, 0x00000000, 0x900A0058, "stw r0,0x58(r10)  other material of the contact"),
            new ExeWord(0x82D21290, 0x00000000, 0x39290001, "addi r9,r9,1"),
            new ExeWord(0x82D21294, 0x00000000, 0x912B0040, "stw r9,0x40(r11)"),
            new ExeWord(0x82D21298, 0x00000000, 0x4E800020, "blr"),
            new ExeWord(0x82D2129C, 0x00000000, 0x800318A4, "lwz r0,0x18A4(r3)  vehicle blueprint id"),
            new ExeWord(0x82D212A0, 0x00000000, 0x3D8000EA, "lis r12,0xea"),
            new ExeWord(0x82D212A4, 0x00000000, 0x618C74B4, "ori r12,r12,0x74b4  co-op puppet blueprint"),
            new ExeWord(0x82D212A8, 0x00000000, 0x7F006000, "cmpw cr6,r0,r12"),
            new ExeWord(0x82D212AC, 0x00000000, 0x409A0018, "bne cr6 -> not a puppet"),
            new ExeWord(0x82D212B0, 0x00000000, 0x3800FFFF, "li r0,-1"),
            new ExeWord(0x82D212B4, 0x00000000, 0x2B080000, "cmplwi cr6,r8,0  contact record?"),
            new ExeWord(0x82D212B8, 0x00000000, 0x419AFFB8, "beq cr6 -> 0x82d21270"),
            new ExeWord(0x82D212BC, 0x00000000, 0x800800B0, "lwz r0,0xB0(r8)  the contact's other material"),
            new ExeWord(0x82D212C0, 0x00000000, 0x4BFFFFB0, "b 0x82d21270  puppet: log the hit (returns to the caller), no damage"),
            new ExeWord(0x82D212C4, 0x00000000, 0x7D8802A6, "mflr r12  (the instruction the hook replaced)"),
            new ExeWord(0x82D212C8, 0x00000000, 0x4B8D0524, "b 0x825f17ec"),
            new ExeWord(0x82D212CC, 0x00000000, 0x801E18A4, "lwz r0,0x18A4(r30)  vehicle blueprint id"),
            new ExeWord(0x82D212D0, 0x00000000, 0x3D8000EA, "lis r12,0xea"),
            new ExeWord(0x82D212D4, 0x00000000, 0x618C74B4, "ori r12,r12,0x74b4  co-op puppet blueprint"),
            new ExeWord(0x82D212D8, 0x00000000, 0x7F006000, "cmpw cr6,r0,r12"),
            new ExeWord(0x82D212DC, 0x00000000, 0x409A0010, "bne cr6 -> not a puppet"),
            new ExeWord(0x82D212E0, 0x00000000, 0x7FC3F378, "mr r3,r30  the puppet"),
            new ExeWord(0x82D212E4, 0x00000000, 0x3800FFFF, "li r0,-1  explosion"),
            new ExeWord(0x82D212E8, 0x00000000, 0x4BFFFF88, "b 0x82d21270  log the explosion damage of this block, no damage"),
            new ExeWord(0x82D212EC, 0x00000000, 0x4B4E2A14, "b 0x82203d00  block health as before"),
        });

    public static readonly ExeMod CoopSharedTime = new(
        "coop-shared-time",
        "Co-op: the room decides Showdown Town's time of day",
        "Showdown Town's time of day is picked each time the town loads by 0x824104C0 from aid_misc_banjox_showdowntownsettings_" +
        "timeofday (4 entries of 0x50 bytes: morning, midday, afternoon, night = script id, f32 weight, ..., game-flag name): an " +
        "entry whose game flag is not set yet wins (the intro forces midday), otherwise a weighted random pick with the game's LCG " +
        "(0x82F9DFE4), so every player's game rolled its own. The random pick (0x82410538) branches to 0x82D21300: when the word " +
        "0x82FBCB30 (co-op mailbox) is 1..4, that entry is returned; 0 keeps the random pick. NB Multiplayer writes the room's " +
        "time of day there before the town loads. Assembled by coop/shared_time_asm.py.",
        "Static analysis 2026-10-01; in-game result: coop/TESTLOG.md.",
        new[]
        {
            new ExeWord(0x82410538, 0x393D0004, 0x48910DC8, "b 0x82d21300 (was addi r9,r29,4): the room decides the time of day"),
            new ExeWord(0x82D21300, 0x00000000, 0x3D6082FC, "lis r11,0x82fc"),
            new ExeWord(0x82D21304, 0x00000000, 0x814BCB30, "lwz r10,0xcb30(r11)  room time of day [0x82FBCB30]: 0 = random, 1..4 = morning..night"),
            new ExeWord(0x82D21308, 0x00000000, 0x2B0A0000, "cmplwi cr6,r10,0"),
            new ExeWord(0x82D2130C, 0x00000000, 0x419A001C, "beq cr6 -> random pick as usual"),
            new ExeWord(0x82D21310, 0x00000000, 0x2B0A0004, "cmplwi cr6,r10,4"),
            new ExeWord(0x82D21314, 0x00000000, 0x41990014, "bgt cr6 -> out of range: random pick"),
            new ExeWord(0x82D21318, 0x00000000, 0x394AFFFF, "addi r10,r10,-1"),
            new ExeWord(0x82D2131C, 0x00000000, 0x1D6A0050, "mulli r11,r10,0x50"),
            new ExeWord(0x82D21320, 0x00000000, 0x7C6BEA14, "add r3,r11,r29  the room's entry of the settings record"),
            new ExeWord(0x82D21324, 0x00000000, 0x4B6EF2A0, "b 0x824105c4  return it"),
            new ExeWord(0x82D21328, 0x00000000, 0x393D0004, "addi r9,r29,0x4  (the instruction the hook replaced)"),
            new ExeWord(0x82D2132C, 0x00000000, 0x4B6EF210, "b 0x8241053c  continue with the random pick"),
        });

    public static readonly ExeMod CoopWorldRuns = new(
        "coop-world-runs",
        "Co-op: the world keeps running while a player is in a menu",
        "In single player the pause menu, Change Vehicle and photo mode take the global pause counter [0x82FAC760], which puts " +
        "the level object [0x82FAC7AC] (state +0x10) into its paused state: physics, AI and the other players' puppets freeze " +
        "in that player's game. A LIVE game's pause menu skips it (0x825A74A0, all players local), so its world runs. This " +
        "mod makes the single-player menus do the same: the pause menu (0x825A7538) and the Change Vehicle screen (factory " +
        "0x825DA8F4, destructor 0x825DA9C4) no longer pause, Change Vehicle no longer takes the level action lock " +
        "([0x82FAC7AC]+0x60, 0x825768A8 / scene+0x17C) that stops AI drivers, and Take Photo raises the UI input count " +
        "[0x82FAC70C] instead of the pause (cave 0x82D3F2EC; released by the photo exit callback), so the pad moves only " +
        "the photo camera. Menu input never reaches the player's vehicle ([0x82FAC70C] clears the game input devices). " +
        "Build Vehicle is a level change (Mumbo's garage) and still leaves the town. Research: coop/research/pause/REPORT.txt.",
        "Xenia 2026-10-02: AI trolleys and steered puppets keep moving with the pause menu, Change Vehicle and photo mode open; the player's vehicle does not react to menu input.",
        new[]
        {
            new ExeWord(0x825a7538, 0x4be394e9, 0x4800000c, "b 0x825A7544 (was bl 0x823E0A20): single-player pause menu does not take the global pause [0x82FAC760] (as in a LIVE game); +0x1FC stays 0 so its close releases none"),
            new ExeWord(0x825da8f4, 0x4be0612d, 0x60000000, "nop (was bl 0x823E0A20): Change Vehicle screen (scene 32, factory 0x825DA8A8) does not take the global pause"),
            new ExeWord(0x825da9c4, 0x40990014, 0x48000014, "b 0x825DA9D8 (was ble cr6): ...and its destructor 0x825DA990 releases none"),
            new ExeWord(0x82576884, 0x39600001, 0x39600000, "li r11,0 (was li r11,1): Change Vehicle opener 0x82576778 stores scene+0x17C = 0, so the scene close (0x825DAF20) releases no action lock"),
            new ExeWord(0x825768a8, 0x4be40aa9, 0x60000000, "nop (was bl 0x823B7350): Change Vehicle does not take the level action lock [0x82FAC7AC]+0x60 (AI vehicles and NPCs keep acting)"),
            new ExeWord(0x825aa10c, 0x4be36915, 0x487951e1, "bl 0x82d3f2ec (was bl 0x823E0A20): Take Photo raises the UI input count instead of the global pause (world runs, pad goes to the photo camera only)"),
            new ExeWord(0x82d3f2ec, 0x00000000, 0x3d6082fb, "lis r11,0x82fb"),
            new ExeWord(0x82d3f2f0, 0x00000000, 0x814bc70c, "lwz r10,0xc70c(r11)  [0x82FAC70C] open input-capturing UI scenes"),
            new ExeWord(0x82d3f2f4, 0x00000000, 0x394a0001, "addi r10,r10,1"),
            new ExeWord(0x82d3f2f8, 0x00000000, 0x914bc70c, "stw r10,0xc70c(r11)  game input devices are cleared while it is non-zero"),
            new ExeWord(0x82d3f2fc, 0x00000000, 0x4e800020, "blr"),
            new ExeWord(0x825aa4b8, 0x817f1728, 0x817f16d4, "lwz r11,0x16d4(r31) (was 0x1728 = [0x82FAC760]): photo exit callback 0x825AA4A0 releases the UI input count [0x82FAC70C] instead"),
            new ExeWord(0x825aa4c8, 0x917f1728, 0x917f16d4, "stw r11,0x16d4(r31) (was 0x1728): ...count - 1 (only when > 0, ble at 0x825AA4C0 kept)"),
            new ExeWord(0x825aa4d0, 0x4bd12641, 0x60000000, "nop (was bl 0x822BCB10 resume): nothing to resume"),
        });

    public static readonly ExeMod CoopRemoteVehicle = new(
        "coop-remote-vehicle",
        "Co-op: other players' real vehicles, damage and parts breaking off",
        "A request mailbox at 0x82FBCBD0 (seq, done, command, vehicle, argument, fraction, result, flash, regen delay) that NB " +
        "Multiplayer fills and the local player's avatar update (hook 0x82251060, where the game swaps vehicles itself) " +
        "runs once on the game thread: 1 SERIALIZE a vehicle into the game's blueprint buffer [0x82FAD1F8] (0x82610400), " +
        "2 ALLOC, 3 REBUILD a puppet from a blueprint (eject 0x8260D8B0, destroy 0x823B7B08, spawn 0x825697C8 at the puppet's " +
        "place with its AI driver seated), 4 HEALTH of one block (health, hit flash, regen delay: the game's own green / " +
        "orange / red damage tint), 5 DETACH one block (0x8262D558: the next update splits it off as a loose piece), 6 FREE. " +
        "Vehicles are validated (vtable, level link) and blocks must be in the vehicle's block list. Caves 0x82D3F300.., " +
        "0x82D5FA78.., 0x82D5FE48... Research: coop/research/vehicles/REPORT.txt.",
        "Xenia 2026-10-02: puppets rebuilt as the player's Trolley, a Jinjo taxi (59 blocks) and helicopter (61 blocks); 45 rebuilds in a row; block damage levels 1/2/3 tinted; 12/12 blocks detached.",
        new[]
        {
            new ExeWord(0x82251060, 0x817F0DB0, 0x48AEE2A0, "b 0x82d3f300 (was lwz r11,0xDB0(r31)): co-op remote vehicle requests run in the local player's avatar update"),
            new ExeWord(0x82D3F300, 0x00000000, 0x3D6082FC, "lis r11,mailbox@ha"),
            new ExeWord(0x82D3F304, 0x00000000, 0x396BCBD0, "addi r11,r11,mailbox@l  r11 = 0x82fbcbd0"),
            new ExeWord(0x82D3F308, 0x00000000, 0x818B0000, "lwz r12,0(r11)  request seq"),
            new ExeWord(0x82D3F30C, 0x00000000, 0x800B0004, "lwz r0,4(r11)  done seq"),
            new ExeWord(0x82D3F310, 0x00000000, 0x7F0C0000, "cmpw cr6,r12,r0"),
            new ExeWord(0x82D3F314, 0x00000000, 0x419A0024, "beq cr6 -> nothing pending"),
            new ExeWord(0x82D3F318, 0x00000000, 0x3D8082FB, "lis r12,0x82FB"),
            new ExeWord(0x82D3F31C, 0x00000000, 0x818CC7AC, "lwz r12,[0x82FAC7AC]  level object L"),
            new ExeWord(0x82D3F320, 0x00000000, 0x2B0C0000, "cmplwi cr6,r12,0"),
            new ExeWord(0x82D3F324, 0x00000000, 0x419A0014, "beq cr6 -> no level"),
            new ExeWord(0x82D3F328, 0x00000000, 0x818C0A44, "lwz r12,0xA44(r12)  local player avatar"),
            new ExeWord(0x82D3F32C, 0x00000000, 0x7F0CF800, "cmpw cr6,r12,r31"),
            new ExeWord(0x82D3F330, 0x00000000, 0x409A0008, "bne cr6 -> another avatar (AI drivers run this update too)"),
            new ExeWord(0x82D3F334, 0x00000000, 0x4800000D, "bl dispatch"),
            new ExeWord(0x82D3F338, 0x00000000, 0x817F0DB0, "lwz r11,0xDB0(r31)  (the instruction the hook replaced)"),
            new ExeWord(0x82D3F33C, 0x00000000, 0x4B511D28, "b 0x82251064"),
            new ExeWord(0x82D3F340, 0x00000000, 0x7D8802A6, "mflr r12"),
            new ExeWord(0x82D3F344, 0x00000000, 0x9181FFF8, "stw r12,-0x8(r1)"),
            new ExeWord(0x82D3F348, 0x00000000, 0xFBE1FFF0, "std r31,-0x10(r1)"),
            new ExeWord(0x82D3F34C, 0x00000000, 0xFBC1FFE8, "std r30,-0x18(r1)"),
            new ExeWord(0x82D3F350, 0x00000000, 0xFBA1FFE0, "std r29,-0x20(r1)"),
            new ExeWord(0x82D3F354, 0x00000000, 0xFB81FFD8, "std r28,-0x28(r1)"),
            new ExeWord(0x82D3F358, 0x00000000, 0xFB61FFD0, "std r27,-0x30(r1)"),
            new ExeWord(0x82D3F35C, 0x00000000, 0x9421FEC0, "stwu r1,-0x140(r1)"),
            new ExeWord(0x82D3F360, 0x00000000, 0x3FC082FC, "lis r30,mailbox@ha"),
            new ExeWord(0x82D3F364, 0x00000000, 0x3BDECBD0, "addi r30,r30,mailbox@l"),
            new ExeWord(0x82D3F368, 0x00000000, 0x819E0000, "lwz r12,0(r30)"),
            new ExeWord(0x82D3F36C, 0x00000000, 0x919E0004, "stw r12,4(r30)  done = request (never runs twice)"),
            new ExeWord(0x82D3F370, 0x00000000, 0x38000000, "li r0,0"),
            new ExeWord(0x82D3F374, 0x00000000, 0x901E0018, "stw r0,0x18(r30)  result = 0"),
            new ExeWord(0x82D3F378, 0x00000000, 0x801E0008, "lwz r0,8(r30)  command"),
            new ExeWord(0x82D3F37C, 0x00000000, 0x2B000002, "cmplwi cr6,r0,2"),
            new ExeWord(0x82D3F380, 0x00000000, 0x419A0040, "beq cr6 -> ALLOC"),
            new ExeWord(0x82D3F384, 0x00000000, 0x2B000006, "cmplwi cr6,r0,6"),
            new ExeWord(0x82D3F388, 0x00000000, 0x409A0008, "bne cr6 -> vehicle commands"),
            new ExeWord(0x82D3F38C, 0x00000000, 0x48020BDC, "b FREE"),
            new ExeWord(0x82D3F390, 0x00000000, 0x83FE000C, "lwz r31,0xC(r30)  vehicle"),
            new ExeWord(0x82D3F394, 0x00000000, 0x2B1F0000, "cmplwi cr6,r31,0"),
            new ExeWord(0x82D3F398, 0x00000000, 0x419A0034, "beq cr6 -> exit"),
            new ExeWord(0x82D3F39C, 0x00000000, 0x817F0000, "lwz r11,0(r31)  vtable"),
            new ExeWord(0x82D3F3A0, 0x00000000, 0x3D8082FB, "lis r12,0x82FB"),
            new ExeWord(0x82D3F3A4, 0x00000000, 0x618C7F78, "ori r12,r12,0x7F78"),
            new ExeWord(0x82D3F3A8, 0x00000000, 0x7F0B6040, "cmplw cr6,r11,r12"),
            new ExeWord(0x82D3F3AC, 0x00000000, 0x409A0020, "bne cr6 -> not a vehicle"),
            new ExeWord(0x82D3F3B0, 0x00000000, 0x839F004C, "lwz r28,0x4C(r31)  level link (0 = freed)"),
            new ExeWord(0x82D3F3B4, 0x00000000, 0x2B1C0000, "cmplwi cr6,r28,0"),
            new ExeWord(0x82D3F3B8, 0x00000000, 0x419A0014, "beq cr6 -> freed"),
            new ExeWord(0x82D3F3BC, 0x00000000, 0x48020A90, "b vehicle commands"),
            new ExeWord(0x82D3F3C0, 0x00000000, 0x807E0010, "lwz r3,0x10(r30)  size"),
            new ExeWord(0x82D3F3C4, 0x00000000, 0x4B501215, "bl 0x822405d8  allocate + zero"),
            new ExeWord(0x82D3F3C8, 0x00000000, 0x907E0018, "stw r3,0x18(r30)  result"),
            new ExeWord(0x82D3F3CC, 0x00000000, 0x38210140, "addi r1,r1,0x140"),
            new ExeWord(0x82D3F3D0, 0x00000000, 0x8181FFF8, "lwz r12,-0x8(r1)"),
            new ExeWord(0x82D3F3D4, 0x00000000, 0x7D8803A6, "mtlr r12"),
            new ExeWord(0x82D3F3D8, 0x00000000, 0xEBE1FFF0, "ld r31,-0x10(r1)"),
            new ExeWord(0x82D3F3DC, 0x00000000, 0xEBC1FFE8, "ld r30,-0x18(r1)"),
            new ExeWord(0x82D3F3E0, 0x00000000, 0xEBA1FFE0, "ld r29,-0x20(r1)"),
            new ExeWord(0x82D3F3E4, 0x00000000, 0xEB81FFD8, "ld r28,-0x28(r1)"),
            new ExeWord(0x82D3F3E8, 0x00000000, 0xEB61FFD0, "ld r27,-0x30(r1)"),
            new ExeWord(0x82D3F3EC, 0x00000000, 0x4E800020, "blr"),
            new ExeWord(0x82D5FE48, 0x00000000, 0x4BFDF584, "b exit (trampoline: region A is out of conditional-branch range)"),
            new ExeWord(0x82D5FE4C, 0x00000000, 0x2B000001, "cmplwi cr6,r0,1"),
            new ExeWord(0x82D5FE50, 0x00000000, 0x419A0100, "beq cr6 -> SERIALIZE"),
            new ExeWord(0x82D5FE54, 0x00000000, 0x2B000003, "cmplwi cr6,r0,3"),
            new ExeWord(0x82D5FE58, 0x00000000, 0x419A0008, "beq cr6 -> REBUILD"),
            new ExeWord(0x82D5FE5C, 0x00000000, 0x4BFFFC20, "b block commands (5 DETACH, any other: HEALTH)"),
            new ExeWord(0x82D5FE60, 0x00000000, 0x837E0010, "lwz r27,0x10(r30)  blueprint buffer (0x7C header + 0x24 per block)"),
            new ExeWord(0x82D5FE64, 0x00000000, 0x2B1B0000, "cmplwi cr6,r27,0"),
            new ExeWord(0x82D5FE68, 0x00000000, 0x419AFFE0, "beq cr6 -> exit"),
            new ExeWord(0x82D5FE6C, 0x00000000, 0x83BF0048, "lwz r29,0x48(r31)  first child link"),
            new ExeWord(0x82D5FE70, 0x00000000, 0x2B1D0000, "cmplwi cr6,r29,0"),
            new ExeWord(0x82D5FE74, 0x00000000, 0x419A0028, "beq cr6 -> no driver"),
            new ExeWord(0x82D5FE78, 0x00000000, 0x3BBDFFC0, "addi r29,r29,-0x40  child object"),
            new ExeWord(0x82D5FE7C, 0x00000000, 0x817D0000, "lwz r11,0(r29)"),
            new ExeWord(0x82D5FE80, 0x00000000, 0x3D8082FB, "lis r12,0x82FB"),
            new ExeWord(0x82D5FE84, 0x00000000, 0x618C7EDC, "ori r12,r12,0x7EDC  avatar vtable"),
            new ExeWord(0x82D5FE88, 0x00000000, 0x7F0B6040, "cmplw cr6,r11,r12"),
            new ExeWord(0x82D5FE8C, 0x00000000, 0x409A0010, "bne cr6 -> not an avatar"),
            new ExeWord(0x82D5FE90, 0x00000000, 0x817D0C3C, "lwz r11,0xC3C(r29)  its vehicle"),
            new ExeWord(0x82D5FE94, 0x00000000, 0x7F0BF840, "cmplw cr6,r11,r31"),
            new ExeWord(0x82D5FE98, 0x00000000, 0x419A0008, "beq cr6 -> the seated driver"),
            new ExeWord(0x82D5FE9C, 0x00000000, 0x3BA00000, "li r29,0  no driver"),
            new ExeWord(0x82D5FEA0, 0x00000000, 0x39600050, "li r11,0x50"),
            new ExeWord(0x82D5FEA4, 0x00000000, 0x7C1F58CE, "lvx v0,r31,r11  position"),
            new ExeWord(0x82D5FEA8, 0x00000000, 0x39800060, "li r12,0x60"),
            new ExeWord(0x82D5FEAC, 0x00000000, 0x7C0161CE, "stvx v0,r1,r12"),
            new ExeWord(0x82D5FEB0, 0x00000000, 0x396000C0, "li r11,0xC0"),
            new ExeWord(0x82D5FEB4, 0x00000000, 0x7C1F58CE, "lvx v0,r31,r11  orientation quaternion"),
            new ExeWord(0x82D5FEB8, 0x00000000, 0x39800070, "li r12,0x70"),
            new ExeWord(0x82D5FEBC, 0x00000000, 0x7C0161CE, "stvx v0,r1,r12"),
            new ExeWord(0x82D5FEC0, 0x00000000, 0x817F0A90, "lwz r11,0xA90(r31)  marker-set mask (0x10000 for the co-op puppets)"),
            new ExeWord(0x82D5FEC4, 0x00000000, 0x91610058, "stw r11,0x58(r1)"),
            new ExeWord(0x82D5FEC8, 0x00000000, 0x817F18A0, "lwz r11,0x18A0(r31)"),
            new ExeWord(0x82D5FECC, 0x00000000, 0x9161005C, "stw r11,0x5C(r1)"),
            new ExeWord(0x82D5FED0, 0x00000000, 0x7FE3FB78, "mr r3,r31"),
            new ExeWord(0x82D5FED4, 0x00000000, 0x4B8AD9DD, "bl 0x8260d8b0  eject the occupants (as the Change Vehicle purge does)"),
            new ExeWord(0x82D5FED8, 0x00000000, 0x7FE3FB78, "mr r3,r31"),
            new ExeWord(0x82D5FEDC, 0x00000000, 0x38800000, "li r4,0"),
            new ExeWord(0x82D5FEE0, 0x00000000, 0x4B657C29, "bl 0x823b7b08  destroy the old puppet"),
            new ExeWord(0x82D5FEE4, 0x00000000, 0x38610080, "addi r3,r1,0x80  spawn params"),
            new ExeWord(0x82D5FEE8, 0x00000000, 0x7F84E378, "mr r4,r28  owner = level"),
            new ExeWord(0x82D5FEEC, 0x00000000, 0x38A10060, "addi r5,r1,0x60  position"),
            new ExeWord(0x82D5FEF0, 0x00000000, 0x38C10070, "addi r6,r1,0x70  orientation"),
            new ExeWord(0x82D5FEF4, 0x00000000, 0x4B8094A5, "bl 0x82569398  spawn params ctor"),
            new ExeWord(0x82D5FEF8, 0x00000000, 0x936100E0, "stw r27,0xE0(r1)  +0x60 blueprint data"),
            new ExeWord(0x82D5FEFC, 0x00000000, 0x39600000, "li r11,0"),
            new ExeWord(0x82D5FF00, 0x00000000, 0x916100E4, "stw r11,0xE4(r1)  +0x64 no missing-block list"),
            new ExeWord(0x82D5FF04, 0x00000000, 0x39600001, "li r11,1"),
            new ExeWord(0x82D5FF08, 0x00000000, 0x916100E8, "stw r11,0xE8(r1)  +0x68 = 1 (all blocks)"),
            new ExeWord(0x82D5FF0C, 0x00000000, 0x996100B5, "stb r11,0xB5(r1)  +0x35 = 1 lift onto the ground (as summon does)"),
            new ExeWord(0x82D5FF10, 0x00000000, 0x93A100CC, "stw r29,0xCC(r1)  +0x4C driver to seat"),
            new ExeWord(0x82D5FF14, 0x00000000, 0x81610058, "lwz r11,0x58(r1)"),
            new ExeWord(0x82D5FF18, 0x00000000, 0x916100B8, "stw r11,0xB8(r1)  +0x38 marker-set mask"),
            new ExeWord(0x82D5FF1C, 0x00000000, 0x8161005C, "lwz r11,0x5C(r1)"),
            new ExeWord(0x82D5FF20, 0x00000000, 0x916100C0, "stw r11,0xC0(r1)  +0x40"),
            new ExeWord(0x82D5FF24, 0x00000000, 0x38610080, "addi r3,r1,0x80"),
            new ExeWord(0x82D5FF28, 0x00000000, 0x4B8098A1, "bl 0x825697c8  build the vehicle from the blueprint"),
            new ExeWord(0x82D5FF2C, 0x00000000, 0x907E0018, "stw r3,0x18(r30)  result = new vehicle"),
            new ExeWord(0x82D5FF30, 0x00000000, 0x2B030000, "cmplwi cr6,r3,0"),
            new ExeWord(0x82D5FF34, 0x00000000, 0x419A0010, "beq cr6 -> failed"),
            new ExeWord(0x82D5FF38, 0x00000000, 0x3D6000EA, "lis r11,0x00EA"),
            new ExeWord(0x82D5FF3C, 0x00000000, 0x616B74B4, "ori r11,r11,0x74B4"),
            new ExeWord(0x82D5FF40, 0x00000000, 0x916318A4, "stw r11,0x18A4(r3)  keep the co-op puppet blueprint id"),
            new ExeWord(0x82D5FF44, 0x00000000, 0x7F63DB78, "mr r3,r27"),
            new ExeWord(0x82D5FF48, 0x00000000, 0x4B604309, "bl 0x82364250  free the blueprint buffer (the vehicle keeps no pointer to it)"),
            new ExeWord(0x82D5FF4C, 0x00000000, 0x4BFDF480, "b exit"),
            new ExeWord(0x82D5FF50, 0x00000000, 0x7FE3FB78, "mr r3,r31"),
            new ExeWord(0x82D5FF54, 0x00000000, 0x38800000, "li r4,0  -> the game buffer [0x82FAD1F8] (re)allocated 0x7C + 0x24 x blocks"),
            new ExeWord(0x82D5FF58, 0x00000000, 0x4B8B04A9, "bl 0x82610400  serialize vehicle (0x8260FF20: the format of aid_vehicle / saved blueprints)"),
            new ExeWord(0x82D5FF5C, 0x00000000, 0x3D6082FB, "lis r11,0x82FB"),
            new ExeWord(0x82D5FF60, 0x00000000, 0x806BD1F8, "lwz r3,[0x82FAD1F8]"),
            new ExeWord(0x82D5FF64, 0x00000000, 0x4BFDF464, "b result"),
            new ExeWord(0x82D5FF68, 0x00000000, 0x807E0010, "lwz r3,0x10(r30)  address"),
            new ExeWord(0x82D5FF6C, 0x00000000, 0x2B030000, "cmplwi cr6,r3,0"),
            new ExeWord(0x82D5FF70, 0x00000000, 0x419AFED8, "beq cr6 -> exit"),
            new ExeWord(0x82D5FF74, 0x00000000, 0x4B6042DD, "bl 0x82364250  free"),
            new ExeWord(0x82D5FF78, 0x00000000, 0x4BFDF454, "b exit"),
            new ExeWord(0x82D5FA78, 0x00000000, 0x4BFDF954, "b exit (trampoline)"),
            new ExeWord(0x82D5FA7C, 0x00000000, 0x83BE0010, "lwz r29,0x10(r30)  block"),
            new ExeWord(0x82D5FA80, 0x00000000, 0x813F1488, "lwz r9,0x1488(r31)"),
            new ExeWord(0x82D5FA84, 0x00000000, 0x815F148C, "lwz r10,0x148C(r31)"),
            new ExeWord(0x82D5FA88, 0x00000000, 0x7F095040, "cmplw cr6,r9,r10"),
            new ExeWord(0x82D5FA8C, 0x00000000, 0x4098FFEC, "bge cr6 -> not a block of this vehicle"),
            new ExeWord(0x82D5FA90, 0x00000000, 0x81690004, "lwz r11,4(r9)"),
            new ExeWord(0x82D5FA94, 0x00000000, 0x7F0BE840, "cmplw cr6,r11,r29"),
            new ExeWord(0x82D5FA98, 0x00000000, 0x419A000C, "beq cr6 -> found"),
            new ExeWord(0x82D5FA9C, 0x00000000, 0x392900B0, "addi r9,r9,0xB0"),
            new ExeWord(0x82D5FAA0, 0x00000000, 0x4BFFFFE8, "b find"),
            new ExeWord(0x82D5FAA4, 0x00000000, 0x39600001, "li r11,1"),
            new ExeWord(0x82D5FAA8, 0x00000000, 0x917D0280, "stw r11,0x280(r29)  dirty: the block update recomputes fraction +0x274 and damage level +0x284"),
            new ExeWord(0x82D5FAAC, 0x00000000, 0x2B000005, "cmplwi cr6,r0,5"),
            new ExeWord(0x82D5FAB0, 0x00000000, 0x419A0028, "beq cr6 -> DETACH"),
            new ExeWord(0x82D5FAB4, 0x00000000, 0xC01E0014, "lfs f0,0x14(r30)  health fraction"),
            new ExeWord(0x82D5FAB8, 0x00000000, 0xC1BD026C, "lfs f13,0x26C(r29)  max health"),
            new ExeWord(0x82D5FABC, 0x00000000, 0xEC000372, "fmuls f0,f0,f13"),
            new ExeWord(0x82D5FAC0, 0x00000000, 0xD01D0270, "stfs f0,0x270(r29)  health"),
            new ExeWord(0x82D5FAC4, 0x00000000, 0xC01E001C, "lfs f0,0x1C(r30)  hit flash seconds"),
            new ExeWord(0x82D5FAC8, 0x00000000, 0xD01D027C, "stfs f0,0x27C(r29)  hit flash timer"),
            new ExeWord(0x82D5FACC, 0x00000000, 0xC01E0020, "lfs f0,0x20(r30)  regeneration delay seconds"),
            new ExeWord(0x82D5FAD0, 0x00000000, 0xD01D0278, "stfs f0,0x278(r29)  regeneration delay (0x822431B0 regenerates 0.4 HP/s once it runs out)"),
            new ExeWord(0x82D5FAD4, 0x00000000, 0x48000028, "b ok"),
            new ExeWord(0x82D5FAD8, 0x00000000, 0x817F0D90, "lwz r11,0xD90(r31)  block count of the connectivity graph"),
            new ExeWord(0x82D5FADC, 0x00000000, 0x2B0B0001, "cmplwi cr6,r11,1"),
            new ExeWord(0x82D5FAE0, 0x00000000, 0x4099FF98, "ble cr6 -> never detach the last block"),
            new ExeWord(0x82D5FAE4, 0x00000000, 0x39600000, "li r11,0"),
            new ExeWord(0x82D5FAE8, 0x00000000, 0x917D0270, "stw r11,0x270(r29)  health 0 (destroyed)"),
            new ExeWord(0x82D5FAEC, 0x00000000, 0x7FA3EB78, "mr r3,r29"),
            new ExeWord(0x82D5FAF0, 0x00000000, 0x38800001, "li r4,1"),
            new ExeWord(0x82D5FAF4, 0x00000000, 0x38A00000, "li r5,0"),
            new ExeWord(0x82D5FAF8, 0x00000000, 0x4B8CDA61, "bl 0x8262d558  detach (cut joints, vehicle +0xCE8 = 1: next update splits the islands into new vehicles)"),
            new ExeWord(0x82D5FAFC, 0x00000000, 0x38600001, "li r3,1"),
            new ExeWord(0x82D5FB00, 0x00000000, 0x4BFDF8C8, "b result")
        });

    public static readonly ExeMod CoopProjectiles = new(
        "coop-projectiles",
        "Co-op: other players' shots fly in your game (eggs, grenades, torpedoes, lasers)",
        "Weapons fire projectiles from per-weapon pools (block +0x700); NB Multiplayer sees the local player's shots by " +
        "polling them and replays other players' shots from their puppets through a mailbox at 0x82FBCAA0 that the vehicle " +
        "update (hook 0x82270568) runs on the game thread: 2 LAUNCH an idle projectile with the exact position, velocity and " +
        "orientation (the game's own MP-client launch mode 0x82434B80, class checked), 1 CREATE a block-less projectile " +
        "(flags bit 0: damage types -2, harmless). Replays use the puppet's own weapon pools and are made harmless, since " +
        "the real shot's hits are forwarded by coop-remote-damage. 0x8243D95C: a torpedo/grenade without a weapon block " +
        "takes its hits. Caves 0x82D47C60.., 0x82D21380... Research: coop/research/projectiles/REPORT.txt.",
        "Xenia 2026-10-01: egg, grenade, torpedo and laser replays fly, hit and explode; harmless replays do 0 damage; 104 launches in 25 s without a freeze.",
        new[]
        {
            new ExeWord(0x82270568, 0x7D8802A6, 0x48AD76F8, "b 0x82d47c60 (was mflr r12): co-op projectile replay mailbox 0x82fbcaa0"),
            new ExeWord(0x8243D95C, 0x419A0010, 0x419A0028, "beq cr6,0x8243d984 (was beq 0x8243d96c): a torpedo/grenade without a weapon block takes its hits"),
            new ExeWord(0x82D47C60, 0x00000000, 0x3D6082FC, "lis r11,0x82fc"),
            new ExeWord(0x82D47C64, 0x00000000, 0x396BCAA0, "addi r11,r11,-0x3560  r11 = mailbox 0x82fbcaa0"),
            new ExeWord(0x82D47C68, 0x00000000, 0x818B0000, "lwz r12,0(r11)  request seq"),
            new ExeWord(0x82D47C6C, 0x00000000, 0x800B0004, "lwz r0,4(r11)  done seq"),
            new ExeWord(0x82D47C70, 0x00000000, 0x7F0C0000, "cmpw cr6,r12,r0"),
            new ExeWord(0x82D47C74, 0x00000000, 0x419A017C, "beq cr6 -> nothing pending"),
            new ExeWord(0x82D47C78, 0x00000000, 0x7D8802A6, "mflr r12"),
            new ExeWord(0x82D47C7C, 0x00000000, 0x9181FFF8, "stw r12,-0x8(r1)"),
            new ExeWord(0x82D47C80, 0x00000000, 0x9421FF60, "stwu r1,-0xA0(r1)"),
            new ExeWord(0x82D47C84, 0x00000000, 0x90610060, "stw r3,0x60(r1)  keep the vehicle update's arguments (r3 vehicle, f1 dt)"),
            new ExeWord(0x82D47C88, 0x00000000, 0xD8210068, "stfd f1,0x68(r1)"),
            new ExeWord(0x82D47C8C, 0x00000000, 0x93E10070, "stw r31,0x70(r1)"),
            new ExeWord(0x82D47C90, 0x00000000, 0x93C10074, "stw r30,0x74(r1)"),
            new ExeWord(0x82D47C94, 0x00000000, 0x7D7F5B78, "mr r31,r11  mailbox"),
            new ExeWord(0x82D47C98, 0x00000000, 0x38000000, "li r0,0"),
            new ExeWord(0x82D47C9C, 0x00000000, 0x901F0014, "stw r0,0x14(r31)  result = 0"),
            new ExeWord(0x82D47CA0, 0x00000000, 0x83DF000C, "lwz r30,0xC(r31)  owner vehicle"),
            new ExeWord(0x82D47CA4, 0x00000000, 0x2F1E0000, "cmpwi cr6,r30,0"),
            new ExeWord(0x82D47CA8, 0x00000000, 0x419A0124, "beq cr6 -> no owner"),
            new ExeWord(0x82D47CAC, 0x00000000, 0x801E0000, "lwz r0,0(r30)  owner descriptor"),
            new ExeWord(0x82D47CB0, 0x00000000, 0x3D8082FB, "lis r12,0x82fb"),
            new ExeWord(0x82D47CB4, 0x00000000, 0x618C7F78, "ori r12,r12,0x7f78  vehicle descriptor"),
            new ExeWord(0x82D47CB8, 0x00000000, 0x7F006000, "cmpw cr6,r0,r12"),
            new ExeWord(0x82D47CBC, 0x00000000, 0x409A0110, "bne cr6 -> not a vehicle"),
            new ExeWord(0x82D47CC0, 0x00000000, 0x801E004C, "lwz r0,0x4C(r30)  level/player link"),
            new ExeWord(0x82D47CC4, 0x00000000, 0x2F000000, "cmpwi cr6,r0,0"),
            new ExeWord(0x82D47CC8, 0x00000000, 0x419A0104, "beq cr6 -> vehicle not alive"),
            new ExeWord(0x82D47CCC, 0x00000000, 0x801F0008, "lwz r0,8(r31)  command"),
            new ExeWord(0x82D47CD0, 0x00000000, 0x2F000002, "cmpwi cr6,r0,2"),
            new ExeWord(0x82D47CD4, 0x00000000, 0x419A008C, "beq cr6 -> LAUNCH"),
            new ExeWord(0x82D47CD8, 0x00000000, 0x2F000001, "cmpwi cr6,r0,1"),
            new ExeWord(0x82D47CDC, 0x00000000, 0x409A00F0, "bne cr6 -> unknown command"),
            new ExeWord(0x82D47CE0, 0x00000000, 0x387F0010, "addi r3,r31,0x10  &asset id"),
            new ExeWord(0x82D47CE4, 0x00000000, 0x4B534D2D, "bl 0x8227ca10  heap copy of the loaded objparams (0 = not loaded)"),
            new ExeWord(0x82D47CE8, 0x00000000, 0x2F030000, "cmpwi cr6,r3,0"),
            new ExeWord(0x82D47CEC, 0x00000000, 0x419A00E0, "beq cr6 -> objparams not loaded"),
            new ExeWord(0x82D47CF0, 0x00000000, 0x90610078, "stw r3,0x78(r1)  p"),
            new ExeWord(0x82D47CF4, 0x00000000, 0x38000001, "li r0,1"),
            new ExeWord(0x82D47CF8, 0x00000000, 0x9003008C, "stw r0,0x8C(r3)  as the weapon pool builder 0x8263E9C0"),
            new ExeWord(0x82D47CFC, 0x00000000, 0x90030090, "stw r0,0x90(r3)  local object (no netobj)"),
            new ExeWord(0x82D47D00, 0x00000000, 0x801E004C, "lwz r0,0x4C(r30)"),
            new ExeWord(0x82D47D04, 0x00000000, 0x90030084, "stw r0,0x84(r3)  player/level object (damage table +0xEA0)"),
            new ExeWord(0x82D47D08, 0x00000000, 0x801E0050, "lwz r0,0x50(r30)"),
            new ExeWord(0x82D47D0C, 0x00000000, 0x90030098, "stw r0,0x98(r3)  start position = owner position"),
            new ExeWord(0x82D47D10, 0x00000000, 0x801E0054, "lwz r0,0x54(r30)"),
            new ExeWord(0x82D47D14, 0x00000000, 0x9003009C, "stw r0,0x9c(r3)  start position = owner position"),
            new ExeWord(0x82D47D18, 0x00000000, 0x801E0058, "lwz r0,0x58(r30)"),
            new ExeWord(0x82D47D1C, 0x00000000, 0x900300A0, "stw r0,0xa0(r3)  start position = owner position"),
            new ExeWord(0x82D47D20, 0x00000000, 0x93C30240, "stw r30,0x240(r3)  owner vehicle -> proj+0xAC4"),
            new ExeWord(0x82D47D24, 0x00000000, 0x38000000, "li r0,0"),
            new ExeWord(0x82D47D28, 0x00000000, 0x90030244, "stw r0,0x244(r3)  no weapon block -> proj+0xAC8 = 0 (in no block's pool)"),
            new ExeWord(0x82D47D2C, 0x00000000, 0x3863004B, "addi r3,r3,0x4B  class name 'entityAvatarProjectile...'"),
            new ExeWord(0x82D47D30, 0x00000000, 0x4B4CE229, "bl 0x82215f58  class registry lookup"),
            new ExeWord(0x82D47D34, 0x00000000, 0x2F030000, "cmpwi cr6,r3,0"),
            new ExeWord(0x82D47D38, 0x00000000, 0x419A001C, "beq cr6 -> unknown class"),
            new ExeWord(0x82D47D3C, 0x00000000, 0x80810078, "lwz r4,0x78(r1)  p"),
            new ExeWord(0x82D47D40, 0x00000000, 0x38A00000, "li r5,0"),
            new ExeWord(0x82D47D44, 0x00000000, 0x38C00000, "li r6,0  no netobj"),
            new ExeWord(0x82D47D48, 0x00000000, 0x4B655CA9, "bl 0x8239d9f0  create the object (class init 0x82433960 reads p)"),
            new ExeWord(0x82D47D4C, 0x00000000, 0x907F0014, "stw r3,0x14(r31)  result = projectile"),
            new ExeWord(0x82D47D50, 0x00000000, 0x4BFD9630, "b 0x82d21380  harmless option, returns to free"),
            new ExeWord(0x82D47D54, 0x00000000, 0x80610078, "lwz r3,0x78(r1)"),
            new ExeWord(0x82D47D58, 0x00000000, 0x4B61C4F9, "bl 0x82364250  free the objparams copy"),
            new ExeWord(0x82D47D5C, 0x00000000, 0x48000070, "b out"),
            new ExeWord(0x82D47D60, 0x00000000, 0x807F0010, "lwz r3,0x10(r31)  projectile"),
            new ExeWord(0x82D47D64, 0x00000000, 0x2F030000, "cmpwi cr6,r3,0"),
            new ExeWord(0x82D47D68, 0x00000000, 0x419A0064, "beq cr6 -> none"),
            new ExeWord(0x82D47D6C, 0x00000000, 0x81830000, "lwz r12,0(r3)  class descriptor"),
            new ExeWord(0x82D47D70, 0x00000000, 0x818C0018, "lwz r12,0x18(r12)  its update function"),
            new ExeWord(0x82D47D74, 0x00000000, 0x3C008243, "lis r0,0x8243"),
            new ExeWord(0x82D47D78, 0x00000000, 0x60003D58, "ori r0,r0,0x3d58  shared projectile update (entityAvatarProjectile* classes)"),
            new ExeWord(0x82D47D7C, 0x00000000, 0x7F0C0000, "cmpw cr6,r12,r0"),
            new ExeWord(0x82D47D80, 0x00000000, 0x409A004C, "bne cr6 -> not a projectile object"),
            new ExeWord(0x82D47D84, 0x00000000, 0x80030AC0, "lwz r0,0xAC0(r3)  state"),
            new ExeWord(0x82D47D88, 0x00000000, 0x2F000000, "cmpwi cr6,r0,0"),
            new ExeWord(0x82D47D8C, 0x00000000, 0x409A0040, "bne cr6 -> still flying / exploding"),
            new ExeWord(0x82D47D90, 0x00000000, 0x93C30AC4, "stw r30,0xAC4(r3)  owner (refreshed every launch)"),
            new ExeWord(0x82D47D94, 0x00000000, 0x38000000, "li r0,0"),
            new ExeWord(0x82D47D98, 0x00000000, 0x90010054, "stw r0,0x54(r1)  launch arg [sp+0x54] -> proj+0xC58"),
            new ExeWord(0x82D47D9C, 0x00000000, 0x38000001, "li r0,1"),
            new ExeWord(0x82D47DA0, 0x00000000, 0x9801005F, "stb r0,0x5F(r1)  launch arg: use the velocity vector as is"),
            new ExeWord(0x82D47DA4, 0x00000000, 0x389F0020, "addi r4,r31,0x20  &position"),
            new ExeWord(0x82D47DA8, 0x00000000, 0x38BF0040, "addi r5,r31,0x40  &quaternion"),
            new ExeWord(0x82D47DAC, 0x00000000, 0x38DF0050, "addi r6,r31,0x50  &unit direction"),
            new ExeWord(0x82D47DB0, 0x00000000, 0x38FF0030, "addi r7,r31,0x30  &velocity"),
            new ExeWord(0x82D47DB4, 0x00000000, 0x39000000, "li r8,0"),
            new ExeWord(0x82D47DB8, 0x00000000, 0x39200000, "li r9,0"),
            new ExeWord(0x82D47DBC, 0x00000000, 0x39400000, "li r10,0  no driver actor"),
            new ExeWord(0x82D47DC0, 0x00000000, 0x4B6ECDC1, "bl 0x82434b80  launch (state 1)"),
            new ExeWord(0x82D47DC4, 0x00000000, 0x38000001, "li r0,1"),
            new ExeWord(0x82D47DC8, 0x00000000, 0x901F0014, "stw r0,0x14(r31)  result = 1"),
            new ExeWord(0x82D47DCC, 0x00000000, 0x801F0000, "lwz r0,0(r31)"),
            new ExeWord(0x82D47DD0, 0x00000000, 0x901F0004, "stw r0,4(r31)  done seq = request seq (after the result)"),
            new ExeWord(0x82D47DD4, 0x00000000, 0x80610060, "lwz r3,0x60(r1)"),
            new ExeWord(0x82D47DD8, 0x00000000, 0xC8210068, "lfd f1,0x68(r1)"),
            new ExeWord(0x82D47DDC, 0x00000000, 0x83E10070, "lwz r31,0x70(r1)"),
            new ExeWord(0x82D47DE0, 0x00000000, 0x83C10074, "lwz r30,0x74(r1)"),
            new ExeWord(0x82D47DE4, 0x00000000, 0x382100A0, "addi r1,r1,0xA0"),
            new ExeWord(0x82D47DE8, 0x00000000, 0x8181FFF8, "lwz r12,-0x8(r1)"),
            new ExeWord(0x82D47DEC, 0x00000000, 0x7D8803A6, "mtlr r12"),
            new ExeWord(0x82D47DF0, 0x00000000, 0x7D8802A6, "mflr r12  (the instruction the hook replaced)"),
            new ExeWord(0x82D47DF4, 0x00000000, 0x4B528778, "b 0x8227056c  back into the vehicle update"),
            new ExeWord(0x82D21380, 0x00000000, 0x2F030000, "cmpwi cr6,r3,0"),
            new ExeWord(0x82D21384, 0x00000000, 0x419A0020, "beq cr6 -> nothing created"),
            new ExeWord(0x82D21388, 0x00000000, 0x801F0018, "lwz r0,0x18(r31)  flags"),
            new ExeWord(0x82D2138C, 0x00000000, 0x70000001, "andi. r0,r0,1  harmless?"),
            new ExeWord(0x82D21390, 0x00000000, 0x41820014, "beq -> keep the game's damage"),
            new ExeWord(0x82D21394, 0x00000000, 0x3800FFFE, "li r0,-2  damage type 'none' (0x823B8890 returns 0)"),
            new ExeWord(0x82D21398, 0x00000000, 0x90030448, "stw r0,0x448(r3)  contact/body damage type (eggs, lasers)"),
            new ExeWord(0x82D2139C, 0x00000000, 0x90030BB4, "stw r0,0xBB4(r3)  explosion damage area (+0xB70) type +0x44 (grenades, torpedoes)"),
            new ExeWord(0x82D213A0, 0x00000000, 0x90030C24, "stw r0,0xC24(r3)  explosion type"),
            new ExeWord(0x82D213A4, 0x00000000, 0x480269B0, "b free")
        });

    public static readonly ExeMod TownGarageReturnVehicle = new(
        "town-garage-return-vehicle",
        "Leaving Mumbo's garage into Showdown Town keeps the vehicle you built",
        "Exit Garage turns the garage vehicle into a temporary custom blueprint and writes it into the return record " +
        "0x82FAF220 (+0xC flag, +0x14 asset id); worlds spawn it, but the town return (0x82564898) cleared the record and the " +
        "town's trolley command (script op 0x8D, 0x823CE6A0) always spawned the trolley. The record is kept across the town " +
        "return (0x82511550) and op 0x8D spawns the garage vehicle instead (once, only right after the garage) and makes it the " +
        "avatar's current vehicle (+0xC60/+0xC64), so Build Vehicle reopens it. Cave 0x82D66000..0x82D660D0. " +
        "Research: coop/research/garage/REPORT.txt.",
        "Xenia 2026-10-02: an unsaved garage build comes back to town with Banjo seated and drives; Build Vehicle reopens it.",
        new[]
        {
            new ExeWord(0x82511550, 0x480532B9, 0x48854AB1, "bl keep (was bl 0x82564808): garage exit to town keeps the custom-vehicle record"),
            new ExeWord(0x823CE6A0, 0x48199F69, 0x489979A9, "bl pick (was bl 0x82568608): script op 0x8D spawns the garage vehicle after a garage exit"),
            new ExeWord(0x82D66000, 0x00000000, 0x7C0802A6, "mflr r0"),
            new ExeWord(0x82D66004, 0x00000000, 0x9001FFF8, "stw r0,-0x8(r1)"),
            new ExeWord(0x82D66008, 0x00000000, 0x9421FFA0, "stwu r1,-0x60(r1)"),
            new ExeWord(0x82D6600C, 0x00000000, 0x3D6082FB, "lis r11,0x82FB"),
            new ExeWord(0x82D66010, 0x00000000, 0x818BF22C, "lwz r12,[0x82FAF22C]  custom-vehicle flag"),
            new ExeWord(0x82D66014, 0x00000000, 0x91810050, "stw r12,0x50(r1)"),
            new ExeWord(0x82D66018, 0x00000000, 0x818BF234, "lwz r12,[0x82FAF234]  custom asset id"),
            new ExeWord(0x82D6601C, 0x00000000, 0x91810054, "stw r12,0x54(r1)"),
            new ExeWord(0x82D66020, 0x00000000, 0x4B7FE7E9, "bl 0x82564808  town return (clears the record)"),
            new ExeWord(0x82D66024, 0x00000000, 0x3D6082FB, "lis r11,0x82FB"),
            new ExeWord(0x82D66028, 0x00000000, 0x81810050, "lwz r12,0x50(r1)"),
            new ExeWord(0x82D6602C, 0x00000000, 0x918BF22C, "stw r12,[0x82FAF22C]  restored"),
            new ExeWord(0x82D66030, 0x00000000, 0x81810054, "lwz r12,0x54(r1)"),
            new ExeWord(0x82D66034, 0x00000000, 0x918BF234, "stw r12,[0x82FAF234]  restored"),
            new ExeWord(0x82D66038, 0x00000000, 0x38210060, "addi r1,r1,0x60"),
            new ExeWord(0x82D6603C, 0x00000000, 0x8001FFF8, "lwz r0,-0x8(r1)"),
            new ExeWord(0x82D66040, 0x00000000, 0x7C0803A6, "mtlr r0"),
            new ExeWord(0x82D66044, 0x00000000, 0x4E800020, "blr"),
            new ExeWord(0x82D66048, 0x00000000, 0x7C0802A6, "mflr r0"),
            new ExeWord(0x82D6604C, 0x00000000, 0x9001FFF8, "stw r0,-0x8(r1)"),
            new ExeWord(0x82D66050, 0x00000000, 0x9421FFA0, "stwu r1,-0x60(r1)"),
            new ExeWord(0x82D66054, 0x00000000, 0x90610050, "stw r3,0x50(r1)  out: asset id"),
            new ExeWord(0x82D66058, 0x00000000, 0x4B8025B1, "bl 0x82568608  best unlocked trolley (original call)"),
            new ExeWord(0x82D6605C, 0x00000000, 0x80610050, "lwz r3,0x50(r1)"),
            new ExeWord(0x82D66060, 0x00000000, 0x815C0058, "lwz r10,0x58(r28)  level mode"),
            new ExeWord(0x82D66064, 0x00000000, 0x2F0A0001, "cmpwi cr6,r10,1  Showdown Town"),
            new ExeWord(0x82D66068, 0x00000000, 0x409A0058, "bne done"),
            new ExeWord(0x82D6606C, 0x00000000, 0x3D6082FB, "lis r11,0x82FB"),
            new ExeWord(0x82D66070, 0x00000000, 0x818BF22C, "lwz r12,[0x82FAF22C]"),
            new ExeWord(0x82D66074, 0x00000000, 0x2F0C0000, "cmpwi cr6,r12,0"),
            new ExeWord(0x82D66078, 0x00000000, 0x419A0048, "beq done"),
            new ExeWord(0x82D6607C, 0x00000000, 0x814BF220, "lwz r10,[0x82FAF220]  level we came from"),
            new ExeWord(0x82D66080, 0x00000000, 0x3D201920, "lis r9,0x1920"),
            new ExeWord(0x82D66084, 0x00000000, 0x6129ABF9, "ori r9,r9,0xABF9  garage script"),
            new ExeWord(0x82D66088, 0x00000000, 0x7F0A4800, "cmpw cr6,r10,r9"),
            new ExeWord(0x82D6608C, 0x00000000, 0x409A0034, "bne done"),
            new ExeWord(0x82D66090, 0x00000000, 0x818BF234, "lwz r12,[0x82FAF234]  custom asset id"),
            new ExeWord(0x82D66094, 0x00000000, 0x2F0C0000, "cmpwi cr6,r12,0"),
            new ExeWord(0x82D66098, 0x00000000, 0x419A0028, "beq done"),
            new ExeWord(0x82D6609C, 0x00000000, 0x91830000, "stw r12,0(r3)  spawn the garage vehicle instead of the trolley"),
            new ExeWord(0x82D660A0, 0x00000000, 0x815C0A44, "lwz r10,0xA44(r28)  player avatar"),
            new ExeWord(0x82D660A4, 0x00000000, 0x2B0A0000, "cmplwi cr6,r10,0"),
            new ExeWord(0x82D660A8, 0x00000000, 0x419A000C, "beq clr"),
            new ExeWord(0x82D660AC, 0x00000000, 0x918A0C60, "stw r12,0xC60(r10)  avatar current vehicle id (Build Vehicle loads it again)"),
            new ExeWord(0x82D660B0, 0x00000000, 0x918A0C64, "stw r12,0xC64(r10)"),
            new ExeWord(0x82D660B4, 0x00000000, 0x39800000, "li r12,0"),
            new ExeWord(0x82D660B8, 0x00000000, 0x918BF22C, "stw r12,[0x82FAF22C]  consumed"),
            new ExeWord(0x82D660BC, 0x00000000, 0x918BF234, "stw r12,[0x82FAF234]"),
            new ExeWord(0x82D660C0, 0x00000000, 0x38210060, "addi r1,r1,0x60"),
            new ExeWord(0x82D660C4, 0x00000000, 0x8001FFF8, "lwz r0,-0x8(r1)"),
            new ExeWord(0x82D660C8, 0x00000000, 0x7C0803A6, "mtlr r0"),
            new ExeWord(0x82D660CC, 0x00000000, 0x4E800020, "blr")
        });

    public static readonly ExeMod UnknownPartsSafe = new(
        "unknown-parts-safe",
        "Blueprints with parts this game doesn't have never crash it",
        "A blueprint with parts from a mod this game lacks (e.g. ULTRA Parts) made Your Blueprints, Change Vehicle, the garage " +
        "and co-op vehicle rebuilds raise the fatal dirty-disc error. A missing objparams load (type 0x1F) is now a soft " +
        "failure (request context +0x138, 0x8225FBF0), the parts check treats an unknown part as missing (0x8251D654) and " +
        "the spawner drops unknown parts instead of leaving a half-built ghost entry (0x8256996C). The game then shows its " +
        "own warning triangle and Mumbo's missing-parts lines. Cave 0x82D67000..0x82D67068. Research: coop/research/garage/REPORT.txt.",
        "Xenia 2026-10-02: ULTRA blueprints in a non-ULTRA edition are listed with a warning and spawn without the unknown parts; the ULTRA edition is unchanged.",
        new[]
        {
            new ExeWord(0x8225FBF0, 0x39610060, 0x48B07410, "b soft (was addi r11,r1,0x60): soft-request flag for get-asset"),
            new ExeWord(0x8251D654, 0x7C771B78, 0x488499CC, "b guard (was mr r23,r3): parts check treats an unknown part as missing"),
            new ExeWord(0x8256996C, 0x7C7B1B79, 0x487FD6CC, "b ghost (was mr. r27,r3): a missing part unknown to this game gets no ghost block"),
            new ExeWord(0x82D67000, 0x00000000, 0x817F0000, "lwz r11,0(r31)  requested asset id"),
            new ExeWord(0x82D67004, 0x00000000, 0x556B463E, "rlwinm r11,r11,8,24,31  type byte"),
            new ExeWord(0x82D67008, 0x00000000, 0x2B0B001F, "cmplwi cr6,r11,0x1F  objparams (vehicle parts, actors...)"),
            new ExeWord(0x82D6700C, 0x00000000, 0x409A000C, "bne -> other asset types stay as they are"),
            new ExeWord(0x82D67010, 0x00000000, 0x39600001, "li r11,1"),
            new ExeWord(0x82D67014, 0x00000000, 0x916101A8, "stw r11,0x1A8(r1)  request context (r1+0x70) +0x138 = 1: a missing objparams is not fatal"),
            new ExeWord(0x82D67018, 0x00000000, 0x39610060, "addi r11,r1,0x60  (moved)"),
            new ExeWord(0x82D6701C, 0x00000000, 0x4B4F8BD8, "b 0x8225FBF4"),
            new ExeWord(0x82D67020, 0x00000000, 0x7C771B79, "mr. r23,r3  objparams copy (0 = part unknown to this game)"),
            new ExeWord(0x82D67024, 0x00000000, 0x4182000C, "beq cr0 -> not available"),
            new ExeWord(0x82D67028, 0x00000000, 0x81770098, "lwz r11,0x98(r23)  (moved)"),
            new ExeWord(0x82D6702C, 0x00000000, 0x4B7B6630, "b 0x8251D65C"),
            new ExeWord(0x82D67030, 0x00000000, 0x3B000000, "li r24,0  result: not enough parts"),
            new ExeWord(0x82D67034, 0x00000000, 0x4B7B6688, "b 0x8251D6BC  return r24"),
            new ExeWord(0x82D67038, 0x00000000, 0x7C7B1B79, "mr. r27,r3  part data for the ghost (0 = part unknown to this game)"),
            new ExeWord(0x82D6703C, 0x00000000, 0x41820008, "beq cr0 -> drop the ghost entry"),
            new ExeWord(0x82D67040, 0x00000000, 0x4B802934, "b 0x82569974"),
            new ExeWord(0x82D67044, 0x00000000, 0x397F14A0, "addi r11,r31,0x14A0  vehicle ghost vector"),
            new ExeWord(0x82D67048, 0x00000000, 0x814B0004, "lwz r10,4(r11)  end"),
            new ExeWord(0x82D6704C, 0x00000000, 0xA12B000C, "lhz r9,0xC(r11)  element size"),
            new ExeWord(0x82D67050, 0x00000000, 0x7D495050, "subf r10,r9,r10"),
            new ExeWord(0x82D67054, 0x00000000, 0x914B0004, "stw r10,4(r11)  pop the entry pushed at 0x82569954"),
            new ExeWord(0x82D67058, 0x00000000, 0xA14B0010, "lhz r10,0x10(r11)  count"),
            new ExeWord(0x82D6705C, 0x00000000, 0x394AFFFF, "addi r10,r10,-1"),
            new ExeWord(0x82D67060, 0x00000000, 0xB14B0010, "sth r10,0x10(r11)"),
            new ExeWord(0x82D67064, 0x00000000, 0x4B802998, "b 0x825699FC  next block")
        });

    public static readonly ExeMod SmallRoomMatchmaking = new(
        "small-room-matchmaking",
        "Xbox LIVE Ranked / Player Match start with 2 or more players",
        "Matchmaking (Ranked Match, Player Match) ends in the session owner's STATE_WAITING_FOR_OTHERS (0x824FA280): the match " +
        "starts when the players still missing (8 slots - players, match session object +0xE4 - +0xDC) are <= an allowance " +
        "that is 0, then 2 after T = 40 s (set on entering the state, 0x824F406C), then 4 after T + 15 s; at T + 30 s the " +
        "owner kills the session ('We've timed out waiting for others') and searches again. A room of 2-3 friends therefore " +
        "never got a match (the lobby shows 'Opponent 1' and starts over every ~70 s). With this tweak T = 20 s and the " +
        "allowance is 6: a lobby of 2+ starts 20 s after it was created (a full lobby still starts at once; a lone player " +
        "never starts). The team-parity rule (odd missing count in team games) is unchanged. NB's Xenia build applies the " +
        "same words itself in rooms (cvar nb_room_matchmaking, kernel/nb_overlay.cc ApplyTitleFixes).",
        "Xenia 2026-10-05 (two instances, one NB room): Ranked Match Football and Player Match Short Circuit started with " +
        "2 players and both loaded the same match (before: endless 'Opponent 1' lobby). Raising +0xE4 live had the same " +
        "effect (Ranked Banjo-Brawl).",
        new[]
        {
            new ExeWord(0x824F406C, 0x616B9C40, 0x616B4E20, "ori r11,r11,0x4E20: T = 20 s (was 40 s) in STATE_WAITING_FOR_OTHERS"),
            new ExeWord(0x824FA4F8, 0x3BA00002, 0x3BA00006, "li r29,6: allowance after T (was 2) - 2 of 8 players start"),
            new ExeWord(0x824FA434, 0x3BA00004, 0x3BA00006, "li r29,6: allowance after T + 15 s (was 4)"),
        });

    public static readonly ExeMod CoopOnFoot = new(
        "coop-onfoot",
        "Co-op: other players on foot, wrench hits, Change Vehicle / garage indicators",
        "NB Multiplayer shows a remote player who is on foot by the AI Banjo of their puppet vehicle: a mailbox at 0x82FBF420 " +
        "run from the local avatar update (hook 0x8225126C, only for the local avatar) does 1 EJECT(vehicle) / 2 SEAT(vehicle, " +
        "avatar) / 3 KNOCK(reaction: the game's own hit reaction on the local Banjo) / 4 RESPAWN (the co-op puppets' AI " +
        "marker set 0x00010000 despawned 0x823DEC80 and spawned again 0x823C6458: their drivers are re-created from their " +
        "objparams - character select); 4 driver slots at 0x82FBF700 drive " +
        "non-local Banjos every frame (transform 0x821FDC30, velocity, body state 0x8227B548: real walk / run / jump / wrench " +
        "animations); 8 indicator slots at 0x82FBF800 draw any scene indicator (0x823F79C0: 0x6E vehicle edit, 0x79 Mumbo " +
        "pad, LIVE player colours on the minimap...) at any position. Hook 0x822AE340 logs the local player's wrench hits on other Banjos (0x82FBF540). Caves " +
        "0x82D62CC8.., 0x82D40EA8.. Research: coop/research/onfoot/REPORT.txt.",
        "Xenia 2026-10-02 (two games): ejected puppet Banjos walk, run, jump and spin the wrench where the remote player is; " +
        "wrench hits knock the other player down natively; the vehicle-edit icon shows while the other player has Change Vehicle open.",
        new[]
        {
            new ExeWord(0x8225126C, 0x817F0D08, 0x48B11A5C, "b 0x82d62cc8 (was lwz r11,0xD08(r31)): co-op on-foot puppets, indicators and commands in the local avatar update"),
            new ExeWord(0x822AE340, 0x93810110, 0x48A92B68, "b 0x82d40ea8 (was stw r28,0x110(r1)): co-op wrench hit log"),
            new ExeWord(0x82D62CC8, 0x00000000, 0x3D8082FB, "lis r12,0x82FB"),
            new ExeWord(0x82D62CCC, 0x00000000, 0x818CC7AC, "lwz r12,[0x82FAC7AC]  level"),
            new ExeWord(0x82D62CD0, 0x00000000, 0x2B0C0000, "cmplwi cr6,r12,0"),
            new ExeWord(0x82D62CD4, 0x00000000, 0x419A0014, "beq -> no level"),
            new ExeWord(0x82D62CD8, 0x00000000, 0x818C0A44, "lwz r12,0xA44(r12)  local avatar"),
            new ExeWord(0x82D62CDC, 0x00000000, 0x7F0CF800, "cmpw cr6,r12,r31"),
            new ExeWord(0x82D62CE0, 0x00000000, 0x409A0008, "bne -> another avatar (AI) runs this update"),
            new ExeWord(0x82D62CE4, 0x00000000, 0x4800000D, "bl main"),
            new ExeWord(0x82D62CE8, 0x00000000, 0x817F0D08, "lwz r11,0xD08(r31)  (replaced instruction)"),
            new ExeWord(0x82D62CEC, 0x00000000, 0x4B4EE584, "b 0x82251270"),
            new ExeWord(0x82D62CF0, 0x00000000, 0x7D8802A6, "mflr r12"),
            new ExeWord(0x82D62CF4, 0x00000000, 0x9181FFF8, "stw r12,-8(r1)"),
            new ExeWord(0x82D62CF8, 0x00000000, 0xFBE1FFF0, "std r31"),
            new ExeWord(0x82D62CFC, 0x00000000, 0xFBC1FFE8, "std r30"),
            new ExeWord(0x82D62D00, 0x00000000, 0xFBA1FFE0, "std r29"),
            new ExeWord(0x82D62D04, 0x00000000, 0xFB81FFD8, "std r28"),
            new ExeWord(0x82D62D08, 0x00000000, 0x9421FF70, "stwu r1,-0x90(r1)"),
            new ExeWord(0x82D62D0C, 0x00000000, 0x7FFCFB78, "r28 = local avatar A"),
            new ExeWord(0x82D62D10, 0x00000000, 0x3FC082FC, "lis r30,cmd@ha"),
            new ExeWord(0x82D62D14, 0x00000000, 0x3BDEF420, "r30 = command mailbox 0x82FBF420"),
            new ExeWord(0x82D62D18, 0x00000000, 0x819E0000, "request seq"),
            new ExeWord(0x82D62D1C, 0x00000000, 0x801E0004, "done seq"),
            new ExeWord(0x82D62D20, 0x00000000, 0x7F0C0000, "cmpw cr6,r12,r0"),
            new ExeWord(0x82D62D24, 0x00000000, 0x419A0150, "nothing pending"),
            new ExeWord(0x82D62D28, 0x00000000, 0x919E0004, "done = seq (never runs twice)"),
            new ExeWord(0x82D62D2C, 0x00000000, 0x38000000, "li r0,0"),
            new ExeWord(0x82D62D30, 0x00000000, 0x901E0014, "result = 0"),
            new ExeWord(0x82D62D34, 0x00000000, 0x801E0008, "command"),
            new ExeWord(0x82D62D38, 0x00000000, 0x2B000003, "cmplwi cr6,r0,3"),
            new ExeWord(0x82D62D3C, 0x00000000, 0x419A00B4, "KNOCK"),
            new ExeWord(0x82D62D40, 0x00000000, 0x2B000004, "cmplwi cr6,r0,4"),
            new ExeWord(0x82D62D44, 0x00000000, 0x419A00DC, "RESPAWN"),
            new ExeWord(0x82D62D48, 0x00000000, 0x83FE000C, "arg0 vehicle"),
            new ExeWord(0x82D62D4C, 0x00000000, 0x2B1F0000, "cmplwi cr6,r31,0"),
            new ExeWord(0x82D62D50, 0x00000000, 0x419A0124, "no vehicle"),
            new ExeWord(0x82D62D54, 0x00000000, 0x817F0000, "vtable"),
            new ExeWord(0x82D62D58, 0x00000000, 0x3D8082FB, "lis r12,0x82FB"),
            new ExeWord(0x82D62D5C, 0x00000000, 0x618C7F78, "ori r12,r12,0x7F78"),
            new ExeWord(0x82D62D60, 0x00000000, 0x7F0B6040, "cmplw cr6,r11,r12"),
            new ExeWord(0x82D62D64, 0x00000000, 0x409A0110, "not a vehicle"),
            new ExeWord(0x82D62D68, 0x00000000, 0x817F004C, "level link"),
            new ExeWord(0x82D62D6C, 0x00000000, 0x2B0B0000, "cmplwi cr6,r11,0"),
            new ExeWord(0x82D62D70, 0x00000000, 0x419A0104, "freed vehicle"),
            new ExeWord(0x82D62D74, 0x00000000, 0x2B000001, "cmplwi cr6,r0,1"),
            new ExeWord(0x82D62D78, 0x00000000, 0x409A0010, "not EJECT"),
            new ExeWord(0x82D62D7C, 0x00000000, 0x7FE3FB78, "mr r3,vehicle"),
            new ExeWord(0x82D62D80, 0x00000000, 0x4B8AAB31, "bl 0x8260d8b0  eject the occupants"),
            new ExeWord(0x82D62D84, 0x00000000, 0x48000060, "b ok"),
            new ExeWord(0x82D62D88, 0x00000000, 0x2B000002, "cmplwi cr6,r0,2"),
            new ExeWord(0x82D62D8C, 0x00000000, 0x409A00E8, "unknown command"),
            new ExeWord(0x82D62D90, 0x00000000, 0x809E0010, "arg1 avatar"),
            new ExeWord(0x82D62D94, 0x00000000, 0x2B040000, "cmplwi cr6,r4,0"),
            new ExeWord(0x82D62D98, 0x00000000, 0x419A00DC, "no avatar"),
            new ExeWord(0x82D62D9C, 0x00000000, 0x81640000, "vtable"),
            new ExeWord(0x82D62DA0, 0x00000000, 0x3D8082FB, "lis r12,0x82FB"),
            new ExeWord(0x82D62DA4, 0x00000000, 0x618C7EDC, "ori r12,r12,0x7EDC"),
            new ExeWord(0x82D62DA8, 0x00000000, 0x7F0B6040, "cmplw cr6,r11,r12"),
            new ExeWord(0x82D62DAC, 0x00000000, 0x409A00C8, "not an avatar"),
            new ExeWord(0x82D62DB0, 0x00000000, 0x8164004C, "level link"),
            new ExeWord(0x82D62DB4, 0x00000000, 0x2B0B0000, "cmplwi cr6,r11,0"),
            new ExeWord(0x82D62DB8, 0x00000000, 0x419A00BC, "dead avatar"),
            new ExeWord(0x82D62DBC, 0x00000000, 0x81640C3C, "its vehicle"),
            new ExeWord(0x82D62DC0, 0x00000000, 0x2B0B0000, "cmplwi cr6,r11,0"),
            new ExeWord(0x82D62DC4, 0x00000000, 0x409A00B0, "already seated"),
            new ExeWord(0x82D62DC8, 0x00000000, 0x39600001, "li r11,1"),
            new ExeWord(0x82D62DCC, 0x00000000, 0x91640024, "[avatar+0x24] = 1 (local again)"),
            new ExeWord(0x82D62DD0, 0x00000000, 0x7FE3FB78, "mr r3,vehicle"),
            new ExeWord(0x82D62DD4, 0x00000000, 0x38A00002, "li r5,2  driver seat"),
            new ExeWord(0x82D62DD8, 0x00000000, 0x38C00000, "li r6,0"),
            new ExeWord(0x82D62DDC, 0x00000000, 0x38E00001, "li r7,1"),
            new ExeWord(0x82D62DE0, 0x00000000, 0x4B8AA9E1, "bl 0x8260d7c0  seat the avatar in the vehicle"),
            new ExeWord(0x82D62DE4, 0x00000000, 0x38000001, "li r0,1"),
            new ExeWord(0x82D62DE8, 0x00000000, 0x901E0014, "result = 1"),
            new ExeWord(0x82D62DEC, 0x00000000, 0x48000088, "b driver"),
            new ExeWord(0x82D62DF0, 0x00000000, 0x817C0C3C, "local vehicle"),
            new ExeWord(0x82D62DF4, 0x00000000, 0x2B0B0000, "cmplwi cr6,r11,0"),
            new ExeWord(0x82D62DF8, 0x00000000, 0x409A007C, "in a vehicle: no knock"),
            new ExeWord(0x82D62DFC, 0x00000000, 0x807C0AC8, "local body"),
            new ExeWord(0x82D62E00, 0x00000000, 0x2B030000, "cmplwi cr6,r3,0"),
            new ExeWord(0x82D62E04, 0x00000000, 0x419A0070, "no body"),
            new ExeWord(0x82D62E08, 0x00000000, 0x3C8082FC, "lis r4,hit@ha"),
            new ExeWord(0x82D62E0C, 0x00000000, 0x3884F480, "r4 = hit block 0x82FBF480"),
            new ExeWord(0x82D62E10, 0x00000000, 0x80BE0010, "r5 = reaction"),
            new ExeWord(0x82D62E14, 0x00000000, 0x4B6913A5, "bl 0x823f41b8  apply a hit with reaction (knockdown)"),
            new ExeWord(0x82D62E18, 0x00000000, 0x907E0014, "result"),
            new ExeWord(0x82D62E1C, 0x00000000, 0x48000058, "b driver"),
            new ExeWord(0x82D62E20, 0x00000000, 0x3D8082FB, "lis r12,0x82FB"),
            new ExeWord(0x82D62E24, 0x00000000, 0x83ECC7AC, "lwz r31,[0x82FAC7AC]  level L"),
            new ExeWord(0x82D62E28, 0x00000000, 0x2B1F0000, "cmplwi cr6,r31,0"),
            new ExeWord(0x82D62E2C, 0x00000000, 0x419A0048, "no level"),
            new ExeWord(0x82D62E30, 0x00000000, 0x7FE3FB78, "mr r3,L"),
            new ExeWord(0x82D62E34, 0x00000000, 0x3C800001, "lis r4,1  marker set mask 0x00010000"),
            new ExeWord(0x82D62E38, 0x00000000, 0x38A00000, "li r5,0"),
            new ExeWord(0x82D62E3C, 0x00000000, 0x38C00000, "li r6,0"),
            new ExeWord(0x82D62E40, 0x00000000, 0x4B67BE41, "bl 0x823dec80  despawn the marker set"),
            new ExeWord(0x82D62E44, 0x00000000, 0x807F07D4, "lwz r3,0x7D4(L)  marker manager"),
            new ExeWord(0x82D62E48, 0x00000000, 0x2B030000, "cmplwi cr6,r3,0"),
            new ExeWord(0x82D62E4C, 0x00000000, 0x419A0028, "no marker manager"),
            new ExeWord(0x82D62E50, 0x00000000, 0x7FE4FB78, "mr r4,L"),
            new ExeWord(0x82D62E54, 0x00000000, 0x80A30000, "lwz r5,[mm]"),
            new ExeWord(0x82D62E58, 0x00000000, 0x80C30004, "lwz r6,[mm+4]"),
            new ExeWord(0x82D62E5C, 0x00000000, 0x3CE00001, "lis r7,1  0x00010000"),
            new ExeWord(0x82D62E60, 0x00000000, 0x39000000, "li r8,0"),
            new ExeWord(0x82D62E64, 0x00000000, 0x39200000, "li r9,0"),
            new ExeWord(0x82D62E68, 0x00000000, 0x39400000, "li r10,0"),
            new ExeWord(0x82D62E6C, 0x00000000, 0x4B6635ED, "bl 0x823c6458  spawn the marker set again"),
            new ExeWord(0x82D62E70, 0x00000000, 0x4BFFFF74, "b ok"),
            new ExeWord(0x82D62E74, 0x00000000, 0x3FC082FC, "lis r30,slots@ha"),
            new ExeWord(0x82D62E78, 0x00000000, 0x3BDEF700, "r30 = driver slot 0x82FBF700"),
            new ExeWord(0x82D62E7C, 0x00000000, 0x3BA00004, "r29 = 4 slots"),
            new ExeWord(0x82D62E80, 0x00000000, 0x83FE0000, "R"),
            new ExeWord(0x82D62E84, 0x00000000, 0x2B1F0000, "cmplwi cr6,r31,0"),
            new ExeWord(0x82D62E88, 0x00000000, 0x419A00A4, "unused slot"),
            new ExeWord(0x82D62E8C, 0x00000000, 0x7F1FE040, "cmplw cr6,r31,r28"),
            new ExeWord(0x82D62E90, 0x00000000, 0x419A009C, "never the local avatar"),
            new ExeWord(0x82D62E94, 0x00000000, 0x817F0000, "vtable"),
            new ExeWord(0x82D62E98, 0x00000000, 0x3D8082FB, "lis r12,0x82FB"),
            new ExeWord(0x82D62E9C, 0x00000000, 0x618C7EDC, "ori r12,r12,0x7EDC"),
            new ExeWord(0x82D62EA0, 0x00000000, 0x7F0B6040, "cmplw cr6,r11,r12"),
            new ExeWord(0x82D62EA4, 0x00000000, 0x409A0088, "not an avatar"),
            new ExeWord(0x82D62EA8, 0x00000000, 0x817F004C, "level link"),
            new ExeWord(0x82D62EAC, 0x00000000, 0x2B0B0000, "cmplwi cr6,r11,0"),
            new ExeWord(0x82D62EB0, 0x00000000, 0x419A007C, "dead"),
            new ExeWord(0x82D62EB4, 0x00000000, 0x801E0004, "flags"),
            new ExeWord(0x82D62EB8, 0x00000000, 0x700B0004, "andi. r11,r0,4"),
            new ExeWord(0x82D62EBC, 0x00000000, 0x4182000C, "beq cr0"),
            new ExeWord(0x82D62EC0, 0x00000000, 0x39600000, "li r11,0"),
            new ExeWord(0x82D62EC4, 0x00000000, 0x917F0024, "[R+0x24] = 0 (not local)"),
            new ExeWord(0x82D62EC8, 0x00000000, 0x801E0004, "flags"),
            new ExeWord(0x82D62ECC, 0x00000000, 0x700B0001, "andi. r11,r0,1"),
            new ExeWord(0x82D62ED0, 0x00000000, 0x41820030, "beq cr0"),
            new ExeWord(0x82D62ED4, 0x00000000, 0x7FE3FB78, "mr r3,R"),
            new ExeWord(0x82D62ED8, 0x00000000, 0x389E0010, "r4 = &pos"),
            new ExeWord(0x82D62EDC, 0x00000000, 0x38BE0020, "r5 = &euler"),
            new ExeWord(0x82D62EE0, 0x00000000, 0x4B49AD51, "bl 0x821fdc30  set actor transform (pos, euler)"),
            new ExeWord(0x82D62EE4, 0x00000000, 0x39600030, "li r11,0x30"),
            new ExeWord(0x82D62EE8, 0x00000000, 0x7C1E58CE, "lvx v0,r30,r11  velocity"),
            new ExeWord(0x82D62EEC, 0x00000000, 0x398000D0, "li r12,0xD0"),
            new ExeWord(0x82D62EF0, 0x00000000, 0x7C1F61CE, "R+0xD0 = velocity"),
            new ExeWord(0x82D62EF4, 0x00000000, 0x817F07D0, "lwz r11,0x7D0(r31)"),
            new ExeWord(0x82D62EF8, 0x00000000, 0x616B0004, "ori r11,r11,4"),
            new ExeWord(0x82D62EFC, 0x00000000, 0x917F07D0, "R+0x7D0 |= 4 (push the transform into physics)"),
            new ExeWord(0x82D62F00, 0x00000000, 0x801E0004, "flags"),
            new ExeWord(0x82D62F04, 0x00000000, 0x700B0002, "andi. r11,r0,2"),
            new ExeWord(0x82D62F08, 0x00000000, 0x41820024, "beq cr0"),
            new ExeWord(0x82D62F0C, 0x00000000, 0x807F0AC8, "r3 = body"),
            new ExeWord(0x82D62F10, 0x00000000, 0x2B030000, "cmplwi cr6,r3,0"),
            new ExeWord(0x82D62F14, 0x00000000, 0x419A0018, "no body"),
            new ExeWord(0x82D62F18, 0x00000000, 0x809E0008, "wanted state"),
            new ExeWord(0x82D62F1C, 0x00000000, 0x81630670, "current state"),
            new ExeWord(0x82D62F20, 0x00000000, 0x7F045800, "cmpw cr6,r4,r11"),
            new ExeWord(0x82D62F24, 0x00000000, 0x419A0008, "same state"),
            new ExeWord(0x82D62F28, 0x00000000, 0x4B518621, "bl 0x8227b548  setState(body, wanted)"),
            new ExeWord(0x82D62F2C, 0x00000000, 0x3BDE0040, "next slot"),
            new ExeWord(0x82D62F30, 0x00000000, 0x3BBDFFFF, ""),
            new ExeWord(0x82D62F34, 0x00000000, 0x2F1D0000, ""),
            new ExeWord(0x82D62F38, 0x00000000, 0x409AFF48, "loop"),
            new ExeWord(0x82D62F3C, 0x00000000, 0x3FC082FC, "lis r30,ind@ha"),
            new ExeWord(0x82D62F40, 0x00000000, 0x3BDEF800, "r30 = indicator slot 0x82FBF800"),
            new ExeWord(0x82D62F44, 0x00000000, 0x3BA00008, "r29 = 8 slots (0x82FBF800..0x82FBF900: other players' minimap markers + icons)"),
            new ExeWord(0x82D62F48, 0x00000000, 0x809E0000, "type"),
            new ExeWord(0x82D62F4C, 0x00000000, 0x2B040000, "cmplwi cr6,r4,0"),
            new ExeWord(0x82D62F50, 0x00000000, 0x419A0018, "none"),
            new ExeWord(0x82D62F54, 0x00000000, 0x7F83E378, "r3 = local avatar"),
            new ExeWord(0x82D62F58, 0x00000000, 0x38A00000, "r5 = 0 (no object)"),
            new ExeWord(0x82D62F5C, 0x00000000, 0x38DE0010, "r6 = &pos"),
            new ExeWord(0x82D62F60, 0x00000000, 0x80FE0004, "r7 = map flag"),
            new ExeWord(0x82D62F64, 0x00000000, 0x4B694A5D, "bl 0x823f79c0  add a player indicator for this frame"),
            new ExeWord(0x82D62F68, 0x00000000, 0x3BDE0020, "next slot"),
            new ExeWord(0x82D62F6C, 0x00000000, 0x3BBDFFFF, ""),
            new ExeWord(0x82D62F70, 0x00000000, 0x2F1D0000, ""),
            new ExeWord(0x82D62F74, 0x00000000, 0x409AFFD4, "loop"),
            new ExeWord(0x82D62F78, 0x00000000, 0x38210090, "addi r1,r1,0x90"),
            new ExeWord(0x82D62F7C, 0x00000000, 0x8181FFF8, "lwz r12,-8(r1)"),
            new ExeWord(0x82D62F80, 0x00000000, 0x7D8803A6, "mtlr r12"),
            new ExeWord(0x82D62F84, 0x00000000, 0xEBE1FFF0, "ld r31"),
            new ExeWord(0x82D62F88, 0x00000000, 0xEBC1FFE8, "ld r30"),
            new ExeWord(0x82D62F8C, 0x00000000, 0xEBA1FFE0, "ld r29"),
            new ExeWord(0x82D62F90, 0x00000000, 0xEB81FFD8, "ld r28"),
            new ExeWord(0x82D62F94, 0x00000000, 0x4E800020, "blr"),
            new ExeWord(0x82D40EA8, 0x00000000, 0x93810110, "stw r28,0x110(r1)  (replaced instruction)"),
            new ExeWord(0x82D40EAC, 0x00000000, 0x3D8082FB, "lis r12,0x82FB"),
            new ExeWord(0x82D40EB0, 0x00000000, 0x818CC7AC, "level"),
            new ExeWord(0x82D40EB4, 0x00000000, 0x2B0C0000, "cmplwi cr6,r12,0"),
            new ExeWord(0x82D40EB8, 0x00000000, 0x419A0088, "no level"),
            new ExeWord(0x82D40EBC, 0x00000000, 0x818C0A44, "local avatar"),
            new ExeWord(0x82D40EC0, 0x00000000, 0x7F0CE040, "attacker r28"),
            new ExeWord(0x82D40EC4, 0x00000000, 0x409A007C, "not the local player"),
            new ExeWord(0x82D40EC8, 0x00000000, 0x819D0000, "victim r29 vtable"),
            new ExeWord(0x82D40ECC, 0x00000000, 0x3D4082FB, "lis r10,0x82FB"),
            new ExeWord(0x82D40ED0, 0x00000000, 0x614A7EDC, "ori r10,r10,0x7EDC"),
            new ExeWord(0x82D40ED4, 0x00000000, 0x7F0C5040, "cmplw cr6,r12,r10"),
            new ExeWord(0x82D40ED8, 0x00000000, 0x409A0068, "not a banjoactor"),
            new ExeWord(0x82D40EDC, 0x00000000, 0x3D2082FC, "lis r9,ring@ha"),
            new ExeWord(0x82D40EE0, 0x00000000, 0x3929F540, "r9 = ring 0x82FBF540"),
            new ExeWord(0x82D40EE4, 0x00000000, 0x81090000, "count"),
            new ExeWord(0x82D40EE8, 0x00000000, 0x550A2E34, "r10 = (count & 7) * 0x20"),
            new ExeWord(0x82D40EEC, 0x00000000, 0x7D4A4A14, "add r10,r10,r9"),
            new ExeWord(0x82D40EF0, 0x00000000, 0x394A0010, "entry"),
            new ExeWord(0x82D40EF4, 0x00000000, 0x93AA0000, "victim"),
            new ExeWord(0x82D40EF8, 0x00000000, 0x38000000, "li r0,0"),
            new ExeWord(0x82D40EFC, 0x00000000, 0x81930090, "attack descriptor (r19 = attacker body)"),
            new ExeWord(0x82D40F00, 0x00000000, 0x2B0C0000, "cmplwi cr6,r12,0"),
            new ExeWord(0x82D40F04, 0x00000000, 0x419A0008, "no descriptor"),
            new ExeWord(0x82D40F08, 0x00000000, 0x880C0040, "reaction byte"),
            new ExeWord(0x82D40F0C, 0x00000000, 0x900A0004, "reaction"),
            new ExeWord(0x82D40F10, 0x00000000, 0x819B0000, "hit point +0"),
            new ExeWord(0x82D40F14, 0x00000000, 0x918A0008, "store"),
            new ExeWord(0x82D40F18, 0x00000000, 0x819B0004, "hit point +4"),
            new ExeWord(0x82D40F1C, 0x00000000, 0x918A000C, "store"),
            new ExeWord(0x82D40F20, 0x00000000, 0x819B0008, "hit point +8"),
            new ExeWord(0x82D40F24, 0x00000000, 0x918A0010, "store"),
            new ExeWord(0x82D40F28, 0x00000000, 0x819C0050, "attacker x"),
            new ExeWord(0x82D40F2C, 0x00000000, 0x918A0014, "store"),
            new ExeWord(0x82D40F30, 0x00000000, 0x819C0058, "attacker z"),
            new ExeWord(0x82D40F34, 0x00000000, 0x918A0018, "store"),
            new ExeWord(0x82D40F38, 0x00000000, 0x39080001, "count + 1"),
            new ExeWord(0x82D40F3C, 0x00000000, 0x91090000, "store count (last: readers poll it)"),
            new ExeWord(0x82D40F40, 0x00000000, 0x4B56D404, "b 0x822ae344")
        });

    public static readonly ExeMod CharacterSelect = new(
        "charsel",
        "Character select: play as Mumbo, Grunty, Kazooie, Thomas, Piddles and more",
        "The player's actor comes from getPlayerActorId 0x8251CA98 (the gameassetref name \"banjo_actor\"). Hooked: it returns " +
        "the actor id NB Multiplayer writes into the mailbox 0x82FBFD00 (+0 town-only characters, used only in Showdown Town; " +
        "+4 characters available in every level), or Banjo when both are 0. Applies at the next player spawn (new game / " +
        "continue, garage -> town, entering a world). The characters themselves are the Character Select mod's world edits " +
        "(clones of actor_banjo with the character's model and a hybrid animation table). Cave 0x82D65800..0x82D65898. " +
        "Research: coop/research/charsel/REPORT.txt.",
        "Xenia 2026-10-02: 17 characters spawned as the player; mid-game change through garage -> town; town-only ids ignored outside town.",
        new[]
        {
            new ExeWord(0x8251CA98, 0x3D6082FB, 0x48848D68, "b 0x82d65800 (was lis r11,0x82FB): character select - player actor id from mailbox 0x82FBFD00"),
            new ExeWord(0x82D65800, 0x00000000, 0x7D8802A6, "mflr r12"),
            new ExeWord(0x82D65804, 0x00000000, 0x3D608251, "lis r11,0x8251"),
            new ExeWord(0x82D65808, 0x00000000, 0x616B6ED4, "ori r11,r11,0x6ED4  r11 = 0x82516ED4 (return address in markerPlayerExecute)"),
            new ExeWord(0x82D6580C, 0x00000000, 0x7F0C5840, "cmplw cr6,r12,r11"),
            new ExeWord(0x82D65810, 0x00000000, 0x409A0064, "bne -> other caller: original"),
            new ExeWord(0x82D65814, 0x00000000, 0x81980000, "lwz r12,0(r24)  level script id"),
            new ExeWord(0x82D65818, 0x00000000, 0x3D601901, "lis r11,0x1901"),
            new ExeWord(0x82D6581C, 0x00000000, 0x616BD1B6, "ori r11,r11,0xD1B6  Showdown Town script 0x1901D1B6"),
            new ExeWord(0x82D65820, 0x00000000, 0x7F0C5840, "cmplw cr6,r12,r11"),
            new ExeWord(0x82D65824, 0x00000000, 0x419A0058, "beq -> town"),
            new ExeWord(0x82D65828, 0x00000000, 0x3D6019E0, "lis r11,0x19E0"),
            new ExeWord(0x82D6582C, 0x00000000, 0x616B0470, "ori r11,r11,0x0470  Showdown Town script 0x19E00470"),
            new ExeWord(0x82D65830, 0x00000000, 0x7F0C5840, "cmplw cr6,r12,r11"),
            new ExeWord(0x82D65834, 0x00000000, 0x419A0048, "beq -> town"),
            new ExeWord(0x82D65838, 0x00000000, 0x3D60196B, "lis r11,0x196B"),
            new ExeWord(0x82D6583C, 0x00000000, 0x616BD4A7, "ori r11,r11,0xD4A7  Showdown Town script 0x196BD4A7"),
            new ExeWord(0x82D65840, 0x00000000, 0x7F0C5840, "cmplw cr6,r12,r11"),
            new ExeWord(0x82D65844, 0x00000000, 0x419A0038, "beq -> town"),
            new ExeWord(0x82D65848, 0x00000000, 0x3D60193F, "lis r11,0x193F"),
            new ExeWord(0x82D6584C, 0x00000000, 0x616B0052, "ori r11,r11,0x0052  Showdown Town script 0x193F0052"),
            new ExeWord(0x82D65850, 0x00000000, 0x7F0C5840, "cmplw cr6,r12,r11"),
            new ExeWord(0x82D65854, 0x00000000, 0x419A0028, "beq -> town"),
            new ExeWord(0x82D65858, 0x00000000, 0x3D6082FC, "lis r11,mailbox@ha"),
            new ExeWord(0x82D6585C, 0x00000000, 0x818BFD04, "lwz r12,[0x82FBFD04]  id for every level"),
            new ExeWord(0x82D65860, 0x00000000, 0x2B0C0000, "cmplwi cr6,r12,0"),
            new ExeWord(0x82D65864, 0x00000000, 0x419A0010, "beq -> none: original"),
            new ExeWord(0x82D65868, 0x00000000, 0x91830000, "stw r12,0(r3)  *out = id"),
            new ExeWord(0x82D6586C, 0x00000000, 0x38600001, "li r3,1"),
            new ExeWord(0x82D65870, 0x00000000, 0x4E800020, "blr"),
            new ExeWord(0x82D65874, 0x00000000, 0x3D6082FB, "lis r11,0x82FB  (replaced instruction)"),
            new ExeWord(0x82D65878, 0x00000000, 0x4B7B7224, "b 0x8251CA9C"),
            new ExeWord(0x82D6587C, 0x00000000, 0x3D6082FC, "lis r11,mailbox@ha"),
            new ExeWord(0x82D65880, 0x00000000, 0x818BFD00, "lwz r12,[0x82FBFD00]  id for Showdown Town"),
            new ExeWord(0x82D65884, 0x00000000, 0x2B0C0000, "cmplwi cr6,r12,0"),
            new ExeWord(0x82D65888, 0x00000000, 0x419AFFD0, "beq -> no town id: try the every-level id"),
            new ExeWord(0x82D6588C, 0x00000000, 0x91830000, "stw r12,0(r3)  *out = id"),
            new ExeWord(0x82D65890, 0x00000000, 0x38600001, "li r3,1"),
            new ExeWord(0x82D65894, 0x00000000, 0x4E800020, "blr")
        });

    public static readonly ExeMod SnowFollowsCamera = new(
        "snow-follows-camera",
        "Weather: tagged particle emitters follow the camera (falling snow everywhere)",
        "The GPU particle update 0x82226BD0 copies the owning node's position (node +0xA0) into the emitter every frame " +
        "(0x82226EC4..0x82226ED0, r10 = node, r8 = particle record). The load at 0x82226ECC branches to 0x82D21340: when the " +
        "record's unused word +0x180 is 0x534E ('SN'), the camera position (0x82FADC30) is written to node +0xA0 first, so " +
        "the emitter box moves with the camera. Untagged effects (every record of the retail game has 0 there) are unchanged. " +
        "Used by Snowy Showdown Town (aid_gpuparticleeffect_banjox_snowtown1/2). Research: snow/research/fx/REPORT.md.",
        "Xenia 2026-10-01: snow around the camera in the whole town, driving and on foot.",
        new[]
        {
            new ExeWord(0x82226ECC, 0x100A58C3, 0x48AFA474, "b 0x82d21340 (was lvx128 v0,r10,r11): snow emitters follow the camera"),
            new ExeWord(0x82D21340, 0x00000000, 0x81680180, "lwz r11,0x180(r8)        particle record +0x180"),
            new ExeWord(0x82D21344, 0x00000000, 0x2B0B534E, "cmplwi cr6,r11,0x534e  follow-camera marker?"),
            new ExeWord(0x82D21348, 0x00000000, 0x409A0024, "bne cr6 -> original"),
            new ExeWord(0x82D2134C, 0x00000000, 0x3D6082FB, "lis r11,0x82fb"),
            new ExeWord(0x82D21350, 0x00000000, 0x812BDC30, "lwz r9,-0x23d0(r11)        camera x [0x82fadc30]"),
            new ExeWord(0x82D21354, 0x00000000, 0x912A00A0, "stw r9,0xa0(r10)          emitter node position x"),
            new ExeWord(0x82D21358, 0x00000000, 0x812BDC34, "lwz r9  camera y"),
            new ExeWord(0x82D2135C, 0x00000000, 0x912A00A4, "stw r9,0xa4(r10)"),
            new ExeWord(0x82D21360, 0x00000000, 0x812BDC38, "lwz r9  camera z"),
            new ExeWord(0x82D21364, 0x00000000, 0x912A00A8, "stw r9,0xa8(r10)"),
            new ExeWord(0x82D21368, 0x00000000, 0x393D0040, "addi r9,r29,0x40          restore r9 (instance +0x40)"),
            new ExeWord(0x82D2136C, 0x00000000, 0x396000A0, "li r11,160                restore r11"),
            new ExeWord(0x82D21370, 0x00000000, 0x100A58C3, "lvx128 v0,r10,r11         (the hooked instruction: load node +0xA0)"),
            new ExeWord(0x82D21374, 0x00000000, 0x4B505B5C, "b 0x82226ed0"),
        });

    public static readonly ExeMod PhotoCameraUnlimited = new(
        "photo-camera-unlimited",
        "Unlimited photo camera range",
        "Photo mode keeps the free camera inside a 30-unit sphere around the point where photo mode started (per-frame " +
        "controller 0x8229A8D0: |pos - origin| > 30 -> pulled back onto the sphere). The branch that skips the clamp is made " +
        "unconditional. Wall/ground collision of the camera (0x822EFF20) is unchanged.",
        "Verified in Xenia (guest memory, photo object [[0x82FAC7AC]+0x15B0]+0x40): unpatched the camera stops at exactly " +
        "30.00 from its origin; patched it flew to 34.3 and 57.7 units, stopping only against scenery. Far away, streaming and " +
        "LOD still follow the player.",
        new[] { new ExeWord(0x8229AB98, 0x40990044, 0x48000044, "ble -> b: never clamp the photo camera to 30 units") });

    public static readonly ExeMod PauseOpensPhotos = new(
        "pause-opens-photos",
        "Pause menu opens on Photos & Videos (helper)",
        "Pause tabs switch only with left-stick left/right keystrokes; on setups where that does not work this makes the pause " +
        "menu open with the Photos & Videos list (Take Photo / Photo Album / Cinema). The tab title still says Game Options. " +
        "Game Options (Change Vehicle etc.) is then not reachable - use only while taking photos.",
        "Verified in Xenia: Start shows Take Photo / Open Photo Album / Open Cinema; Take Photo enters photo mode.",
        new[] { new ExeWord(0x825A6B1C, 0x93DF01B4, 0x92BF01B4, "stw r21 (=1) instead of r30 (=0): initial pause tab = Photos & Videos") });

    public static readonly ExeMod GarageBuildArea31 = new(
        "garage-build-area-31",
        "Larger garage build area (31 cells per axis instead of 19)",
        "Mumbo's Motors limits a vehicle plus the part being placed to 19 cells per axis (0x8264AA58: three cmpwi 19, a packed " +
        "(19,19,19) constant and two subfic 19). All eight are raised to 31. Blueprints store cell coordinates as bytes, so " +
        "the format allows up to 256; the blueprint preview picture only draws cells below 19. The 250-part limit is unchanged.",
        "Verified in Xenia (editor cursor read from guest memory): with the Trolley Mk.7 (6 cells long) a held cube stops at " +
        "cell -13 without the mod (total extent 19) and at -25 with it (total extent 31), and parts attach beyond the old edge. " +
        "Not yet tested: saving/loading and driving a vehicle larger than 19 cells.",
        new[]
        {
            new ExeWord(0x8264AC14, 0x2F090013, 0x2F09001F, "cmpwi r9,31 (extent A)"),
            new ExeWord(0x8264AC30, 0x2F090013, 0x2F09001F, "cmpwi r9,31 (extent B)"),
            new ExeWord(0x8264AC4C, 0x2F0B0013, 0x2F0B001F, "cmpwi r11,31 (extent C)"),
            new ExeWord(0x8264AD04, 0x3D000260, 0x3D0003E0, "lis r8,0x3E0 (packed 31,31,31 high)"),
            new ExeWord(0x8264AD0C, 0x616A9813, 0x616AF81F, "ori (packed 0,31,31)"),
            new ExeWord(0x8264AD18, 0x610B9813, 0x610BF81F, "ori (packed 31,31,31)"),
            new ExeWord(0x8264AD38, 0x21290013, 0x2129001F, "subfic r9,r9,31 (clamp)"),
            new ExeWord(0x8264ADA8, 0x216B0013, 0x216B001F, "subfic r11,r11,31 (clamp)"),
        });

    public static readonly ExeMod DrawDistanceX4 = new(
        "draw-distance-x4",
        "Longer draw distance (all LOD / cull distances x4)",
        "Every model carries LOD switch / cull distances in its rendergraph (props in Showdown Town disappear at 83-800 units). " +
        "The LOD selector 0x82B67FE0 compares them with view depth x scale; both callers load the scale 1.0 from a shared literal. " +
        "They now load 0.25, so every LOD step and cull distance is four times farther (objects stay detailed and do not " +
        "disappear prematurely). Costs GPU time.",
        "Verified in Xenia by the inverse test: loading 10.0 instead (distances / 10) makes buildings drop to their lowest LOD and " +
        "props vanish right next to Banjo, proving these two loads scale every LOD/cull distance; the x4 build (0.25, both words read back " +
        "live from guest memory) and an x10 build (0.1) boot and play normally in Showdown Town.",
        new[]
        {
            new ExeWord(0x82318E4C, 0xC02A0D0C, 0xC02A0CA0, "lfs f1,0xCA0(r10): LOD depth scale 0.25 (was 1.0)"),
            new ExeWord(0x823AB3A0, 0xC02A0D0C, 0xC02A0CA0, "lfs f1,0xCA0(r10): LOD depth scale 0.25 (was 1.0)"),
        });

    public static readonly ExeMod VehiclePartLimit400 = new(
        "vehicle-part-limit-400",
        "Vehicle part limit 400 (instead of 250)",
        "The garage, the blueprint checks and the part counter stop at 250 parts (three cmpwi 250). They now allow 400. Code " +
        "that kept one u32 per block in a 250-entry stack array gets bigger stack frames: the vehicle spawner 0x825697C8 " +
        "(0x4B0 -> 0x710) and the break-up routine 0x825F15B0 (0x4D0 -> 0x720, memset 1000 -> 1600 bytes). The challenge " +
        "restriction stripper 0x8252FD50 (fixed [250] arrays) is skipped for vehicles above 250 parts, so part restrictions " +
        "are not enforced on them. The blueprint preview object (0xC40 bytes, three 250-entry arrays) is " +
        "protected by clamping the loader's (0x824BA700) block count to 250, so previews draw the first 250 blocks.",
        "Verified in Xenia: stock garage stops at exactly 250 parts; with the mod a vehicle was built to 294 parts and saved; a " +
        "295-part data blueprint spawns at game start, opens in the garage, shows a (250-block) preview in Change Vehicle > Town " +
        "Blueprints and loads through Change Vehicle while the game keeps running; swapping back to a normal vehicle also works " +
        "(tested with the patch file written by the tool, together with the build-area and Showdown Town mods). Not yet " +
        "tested: challenges and multiplayer with vehicles above 250 parts.",
        new[]
        {
            new ExeWord(0x82604054, 0x2F0B00FA, 0x2F0B0190, "cmpwi r11,400 (part limit)"),
            new ExeWord(0x826041C0, 0x2F0B00FA, 0x2F0B0190, "cmpwi r11,400 (part limit)"),
            new ExeWord(0x82648150, 0x2F0B00FA, 0x2F0B0190, "cmpwi r11,400 (garage part limit)"),
            new ExeWord(0x824BA7EC, 0x7C6B1B78, 0x7C6B1B79, "preview loader: mr. r11,r3"),
            new ExeWord(0x824BA7F4, 0x2B0B0000, 0x41820CE8, "beq 0x824BB4DC (no blueprint)"),
            new ExeWord(0x824BA7F8, 0x419A0CE4, 0xA14B0000, "lhz r10,0(r11) (block count)"),
            new ExeWord(0x824BA7FC, 0xA14B0000, 0x2B0A00FA, "cmplwi cr6,r10,250"),
            new ExeWord(0x824BA800, 0x2C0A0000, 0x40990008, "ble cr6,+8"),
            new ExeWord(0x824BA804, 0x915F007C, 0x394000FA, "li r10,250 (clamp)"),
            new ExeWord(0x824BA808, 0x40820018, 0x915F007C, "stw r10,0x7C(r31)"),
            new ExeWord(0x824BA80C, 0x7E9FA378, 0x48000014, "b 0x824BA820 (shared exit at 0x824BA810 unchanged)"),
            new ExeWord(0x825697D0, 0x9421FB50, 0x9421F8F0, "spawner: stwu r1,-0x710(r1)"),
            new ExeWord(0x82569B94, 0x382104B0, 0x38210710, "spawner: addi r1,r1,0x710"),
            new ExeWord(0x8252FD88, 0x2F170000, 0x2B1700FA, "restrictions: cmplwi r23,250"),
            new ExeWord(0x8252FD8C, 0x419A0330, 0x41990330, "restrictions: bgt skip (> 250 parts)"),
            new ExeWord(0x825F15B8, 0x9421FB30, 0x9421F8E0, "break-up: stwu r1,-0x720(r1)"),
            new ExeWord(0x825F1654, 0x38A003E8, 0x38A00640, "break-up: li r5,1600 (memset u32[400])"),
            new ExeWord(0x825F17E0, 0x382104D0, 0x38210720, "break-up: addi r1,r1,0x720"),
        });

    public static readonly ExeMod DeveloperAllParts = new(
        "developer-all-parts",
        "Developer part list: every vehicle part unlocked (garage_allblocks)",
        "The parts inventory is built at game start by 0x8251CB20 from the unlockable block sets (progress flags). A " +
        "demo-build branch instead loads one fixed list by name (\"garage_demoblocks\", only registered in demo builds). " +
        "The retail gameassetref also lists the developer asset \"garage_allblocks\", which no code uses. The mod renames " +
        "the branch's string to garage_allblocks and always takes that branch, so a new game starts with every part " +
        "(200 of each). Progress no longer unlocks parts because every part is already there.",
        "Verified in Xenia: parts inventory [0x82FACA44] has 23 entries at a stock new game and 118 (200 each) with the mod; " +
        "the Mumbo's Motors parts store shows 12 categories instead of 10 (Accessories, Protection added) and Body offers " +
        "Light, Heavy and Super at the start of the game (stock: Light only).",
        new[]
        {
            new ExeWord(0x8216B7B8, 0x67655F64, 0x67655F61, "\"ge_d\" -> \"ge_a\""),
            new ExeWord(0x8216B7BC, 0x656D6F62, 0x6C6C626C, "\"emob\" -> \"llbl\""),
            new ExeWord(0x8216B7C0, 0x6C6F636B, 0x6F636B73, "\"lock\" -> \"ocks\" (string now garage_allblocks)"),
            new ExeWord(0x8216B7C4, 0x73000000, 0x00000000, "terminator"),
            new ExeWord(0x8251CC24, 0x419A0028, 0x60000000, "always use the fixed block list (was: only in demo builds)"),
        });

    // ---- mods agent 2026-10-05: begin ----
    public static readonly ExeMod TownFlightThrust = new(
        "town-flight-thrust",
        "Propellers and jets push in Showdown Town (planes fly in town)",
        "Propellers and jets are one block class (objDefId_vehicleBlockJetEngine). Its per-frame force routine 0x822717B0 skips applying the thrust when the vehicle belongs to Showdown Town (owner +0x58 == 1) and vehicle +0x624 is 0, which it is for the player's vehicles. The thrust is still computed (block +0x5A0, e.g. 2940 for a small propeller at full throttle) but never reaches the body, so in town planes only roll on their wheels. The town branch is skipped, so propellers and jets push exactly as in the Act worlds (thrust still fades out at the vehicle's top speed, block top-speed rule 0x8227C158). Combine with Change Vehicle in town.",
        "Xenia 2026-10-05 (Humba Plane 1 from Humba's blueprints, Change Vehicle in town): placed at rest 60 units above the lake with RT held it accelerated to 17.8 u/s in the air and held its height (lift) with the mod; see MERGE.md for the unpatched comparison.",
        new[]
        {
            new ExeWord(0x82271800, 0x409A0010, 0x48000010, "b 0x82271810"),
        });

    public static readonly ExeMod UnlimitedPartQuantity = new(
        "unlimited-part-quantity",
        "Unlimited part quantities (9999 of every part you own)",
        "The parts inventory ([0x82FACA44], 0x40-byte entries) is rebuilt at every game load by 0x8251CDD8 from the unlocked part sets: each set entry adds its quantity (e.g. 4 small engines) to the part's owned/available counts (+0x24/+0x28, +0x30/+0x34). The quantity is replaced by 9999 and repeated sets set the count instead of adding to it, so every part you have unlocked can be used 9999 times in Mumbo's garage (no 4-engine or 2-torpedo limits). Locked parts stay locked (combine with All parts unlocked for every part). The 250 parts-per-vehicle limit is a separate tweak.",
        "Xenia 2026-10-05: with the all-unlocked save every one of the 118 inventory entries reads 9999/9999/9999/9999 (tools/xenia/inventory.py); garage use: see MERGE.md.",
        new[]
        {
            new ExeWord(0x8251CE5C, 0x83370000, 0x3B20270F, "li r25,9999"),
            new ExeWord(0x8251CEAC, 0x7D08CA14, 0x7F28CB78, "mr r8,r25"),
            new ExeWord(0x8251CEB0, 0x7D29CA14, 0x7F29CB78, "mr r9,r25"),
        });

    public static readonly ExeMod LogsChoiceUnlock = new(
        "logs-choice-unlock",
        "L.O.G.'s Choice: Choose Vehicle unlocks after the TT trophy",
        "L.O.G.'s Choice challenges force the challenge's own vehicle: their challenge record has +0x88 = 0, which greys out Choose Vehicle and Create Vehicle on the challenge start screen (0x825C7FF8) and makes the challenge spawn the record's blueprint (+0x78) instead of the player's vehicle (0x82522FA0). Both reads go to a routine at 0x82D64000 that keeps +0x88 when it is set and otherwise returns whether the challenge's TT trophy is earned (the record's ..._BeatenCPlus game flag, +0x1D8, the same flag the start screen uses for the trophy icon). So the first time, and until the TT trophy is won with L.O.G.'s vehicle, the challenge is played as usual; afterwards Choose Vehicle (and Create Vehicle) stay unlocked for that challenge for the whole save. Single player only (multiplayer keeps its own rule).",
        "See MERGE.md (Xenia test in Nutty Acres Act 1, game 1).",
        new[]
        {
            new ExeWord(0x825C80A0, 0x838B0088, 0x4879BFE1, "bl 0x82d64080"),
            new ExeWord(0x82522FFC, 0x816B0088, 0x488410B1, "bl 0x82d640ac"),
            new ExeWord(0x8251FE28, 0x81730000, 0x488442DD, "bl 0x82d64104"),
            new ExeWord(0x8251FF4C, 0x81730000, 0x488441B9, "bl 0x82d64104"),
            new ExeWord(0x82D64000, 0x00000000, 0x2B030000, "cmplwi cr6,r3,0"),
            new ExeWord(0x82D64004, 0x00000000, 0x419A006C, "beq cr6,no"),
            new ExeWord(0x82D64008, 0x00000000, 0x80830088, "lwz r4,0x88(r3)  record +0x88: own vehicle allowed (0 = L.O.G.'s Choice)"),
            new ExeWord(0x82D6400C, 0x00000000, 0x2F040000, "cmpwi cr6,r4,0"),
            new ExeWord(0x82D64010, 0x00000000, 0x409A0068, "bne cr6,yes"),
            new ExeWord(0x82D64014, 0x00000000, 0x7C0802A6, "mflr r0"),
            new ExeWord(0x82D64018, 0x00000000, 0x9001FFF8, "stw r0,-0x8(r1)"),
            new ExeWord(0x82D6401C, 0x00000000, 0x9421FFA0, "stwu r1,-0x60(r1)"),
            new ExeWord(0x82D64020, 0x00000000, 0x388301D8, "addi r4,r3,0x1D8  '..._BeatenCPlus' flag name (TT trophy)"),
            new ExeWord(0x82D64024, 0x00000000, 0x38600000, "li r3,0"),
            new ExeWord(0x82D64028, 0x00000000, 0x4B5FFE59, "bl 0x82363E80  flag index by name (0 = unknown)"),
            new ExeWord(0x82D6402C, 0x00000000, 0x2B030000, "cmplwi cr6,r3,0"),
            new ExeWord(0x82D64030, 0x00000000, 0x419A002C, "beq cr6,unknown"),
            new ExeWord(0x82D64034, 0x00000000, 0x3D6082FB, "lis r11,0x82FB"),
            new ExeWord(0x82D64038, 0x00000000, 0x816BD9F0, "lwz r11,-0x2610(r11)  [0x82FAD9F0] game flags"),
            new ExeWord(0x82D6403C, 0x00000000, 0x816B00F4, "lwz r11,0xF4(r11)"),
            new ExeWord(0x82D64040, 0x00000000, 0x816B0008, "lwz r11,0x8(r11)  flag bit array"),
            new ExeWord(0x82D64044, 0x00000000, 0x546AE8FE, "rlwinm r10,r3,29,3,31  index >> 3"),
            new ExeWord(0x82D64048, 0x00000000, 0x7D6A58AE, "lbzx r11,r10,r11"),
            new ExeWord(0x82D6404C, 0x00000000, 0x546A077E, "rlwinm r10,r3,0,29,31  index & 7"),
            new ExeWord(0x82D64050, 0x00000000, 0x7D6B5430, "srw r11,r11,r10"),
            new ExeWord(0x82D64054, 0x00000000, 0x556307FE, "rlwinm r3,r11,0,31,31  TT trophy earned?"),
            new ExeWord(0x82D64058, 0x00000000, 0x48000008, "b out"),
            new ExeWord(0x82D6405C, 0x00000000, 0x38600000, "li r3,0"),
            new ExeWord(0x82D64060, 0x00000000, 0x38210060, "addi r1,r1,0x60"),
            new ExeWord(0x82D64064, 0x00000000, 0x8001FFF8, "lwz r0,-0x8(r1)"),
            new ExeWord(0x82D64068, 0x00000000, 0x7C0803A6, "mtlr r0"),
            new ExeWord(0x82D6406C, 0x00000000, 0x4E800020, "blr"),
            new ExeWord(0x82D64070, 0x00000000, 0x38600000, "li r3,0"),
            new ExeWord(0x82D64074, 0x00000000, 0x4E800020, "blr"),
            new ExeWord(0x82D64078, 0x00000000, 0x38600001, "li r3,1"),
            new ExeWord(0x82D6407C, 0x00000000, 0x4E800020, "blr"),
            new ExeWord(0x82D64080, 0x00000000, 0x7C0802A6, "mflr r0"),
            new ExeWord(0x82D64084, 0x00000000, 0x9001FFF8, "stw r0,-0x8(r1)"),
            new ExeWord(0x82D64088, 0x00000000, 0x9421FFA0, "stwu r1,-0x60(r1)"),
            new ExeWord(0x82D6408C, 0x00000000, 0x7D635B78, "mr r3,r11"),
            new ExeWord(0x82D64090, 0x00000000, 0x4BFFFF71, "bl allowed"),
            new ExeWord(0x82D64094, 0x00000000, 0x7C7C1B78, "mr r28,r3"),
            new ExeWord(0x82D64098, 0x00000000, 0x38210060, "addi r1,r1,0x60"),
            new ExeWord(0x82D6409C, 0x00000000, 0x8001FFF8, "lwz r0,-0x8(r1)"),
            new ExeWord(0x82D640A0, 0x00000000, 0x7C0803A6, "mtlr r0"),
            new ExeWord(0x82D640A4, 0x00000000, 0x893D0080, "lbz r9,0x80(r29)  r9 = [0x82FAC650] (r29 = 0x82FAC5D0), as before the hook"),
            new ExeWord(0x82D640A8, 0x00000000, 0x4E800020, "blr"),
            new ExeWord(0x82D640AC, 0x00000000, 0x7C0802A6, "mflr r0"),
            new ExeWord(0x82D640B0, 0x00000000, 0x9001FFF8, "stw r0,-0x8(r1)"),
            new ExeWord(0x82D640B4, 0x00000000, 0x9421FFA0, "stwu r1,-0x60(r1)"),
            new ExeWord(0x82D640B8, 0x00000000, 0x91610050, "stw r11,0x50(r1)"),
            new ExeWord(0x82D640BC, 0x00000000, 0x7D635B78, "mr r3,r11"),
            new ExeWord(0x82D640C0, 0x00000000, 0x4BFFFF41, "bl allowed"),
            new ExeWord(0x82D640C4, 0x00000000, 0x7C6B1B78, "mr r11,r3"),
            new ExeWord(0x82D640C8, 0x00000000, 0x2F030000, "cmpwi cr6,r3,0"),
            new ExeWord(0x82D640CC, 0x00000000, 0x419A0028, "beq cr6,sret  still locked: L.O.G.'s vehicle (original)"),
            new ExeWord(0x82D640D0, 0x00000000, 0x81410050, "lwz r10,0x50(r1)"),
            new ExeWord(0x82D640D4, 0x00000000, 0x806A0088, "lwz r3,0x88(r10)"),
            new ExeWord(0x82D640D8, 0x00000000, 0x2F030000, "cmpwi cr6,r3,0"),
            new ExeWord(0x82D640DC, 0x00000000, 0x409A0018, "bne cr6,sret  ordinary challenge: unchanged (1)"),
            new ExeWord(0x82D640E0, 0x00000000, 0x806A0078, "lwz r3,0x78(r10)  L.O.G.'s blueprint"),
            new ExeWord(0x82D640E4, 0x00000000, 0x809F0000, "lwz r4,0(r31)"),
            new ExeWord(0x82D640E8, 0x00000000, 0x7F032000, "cmpw cr6,r3,r4"),
            new ExeWord(0x82D640EC, 0x00000000, 0x409A0008, "bne cr6,sret  a vehicle the player chose: spawn it like a chosen vehicle"),
            new ExeWord(0x82D640F0, 0x00000000, 0x39600000, "li r11,0  L.O.G.'s own vehicle: spawn it the original way (no parts-inventory check)"),
            new ExeWord(0x82D640F4, 0x00000000, 0x38210060, "addi r1,r1,0x60"),
            new ExeWord(0x82D640F8, 0x00000000, 0x8001FFF8, "lwz r0,-0x8(r1)"),
            new ExeWord(0x82D640FC, 0x00000000, 0x7C0803A6, "mtlr r0"),
            new ExeWord(0x82D64100, 0x00000000, 0x4E800020, "blr"),
            new ExeWord(0x82D64104, 0x00000000, 0x81730000, "lwz r11,0(r19)"),
            new ExeWord(0x82D64108, 0x00000000, 0x2B0B0000, "cmplwi cr6,r11,0"),
            new ExeWord(0x82D6410C, 0x00000000, 0x4D9A0020, "beqlr cr6  marker without blueprint: the game uses the chosen vehicle anyway"),
            new ExeWord(0x82D64110, 0x00000000, 0x7C0802A6, "mflr r0"),
            new ExeWord(0x82D64114, 0x00000000, 0x9001FFF8, "stw r0,-0x8(r1)"),
            new ExeWord(0x82D64118, 0x00000000, 0x9421FFA0, "stwu r1,-0x60(r1)"),
            new ExeWord(0x82D6411C, 0x00000000, 0x80770004, "lwz r3,0x4(r23)  challenge record (first type-1 entry of the challenge's list)"),
            new ExeWord(0x82D64120, 0x00000000, 0x2B030000, "cmplwi cr6,r3,0"),
            new ExeWord(0x82D64124, 0x00000000, 0x419A0058, "beq cr6,keep"),
            new ExeWord(0x82D64128, 0x00000000, 0x81430000, "lwz r10,0(r3)"),
            new ExeWord(0x82D6412C, 0x00000000, 0x2F0A0000, "cmpwi cr6,r10,0"),
            new ExeWord(0x82D64130, 0x00000000, 0x419A004C, "beq cr6,keep"),
            new ExeWord(0x82D64134, 0x00000000, 0x2F0A0001, "cmpwi cr6,r10,1"),
            new ExeWord(0x82D64138, 0x00000000, 0x419A0010, "beq cr6,found"),
            new ExeWord(0x82D6413C, 0x00000000, 0x8143000C, "lwz r10,0xC(r3)"),
            new ExeWord(0x82D64140, 0x00000000, 0x7C6A1A14, "add r3,r10,r3"),
            new ExeWord(0x82D64144, 0x00000000, 0x4BFFFFDC, "b walk"),
            new ExeWord(0x82D64148, 0x00000000, 0x81430088, "lwz r10,0x88(r3)"),
            new ExeWord(0x82D6414C, 0x00000000, 0x2F0A0000, "cmpwi cr6,r10,0"),
            new ExeWord(0x82D64150, 0x00000000, 0x409A002C, "bne cr6,keep  not a L.O.G.'s Choice challenge"),
            new ExeWord(0x82D64154, 0x00000000, 0x4BFFFEAD, "bl allowed  TT trophy earned?"),
            new ExeWord(0x82D64158, 0x00000000, 0x2F030000, "cmpwi cr6,r3,0"),
            new ExeWord(0x82D6415C, 0x00000000, 0x419A0020, "beq cr6,keep"),
            new ExeWord(0x82D64160, 0x00000000, 0x1D7A0060, "mulli r11,r26,96"),
            new ExeWord(0x82D64164, 0x00000000, 0x7D6BBA14, "add r11,r11,r23"),
            new ExeWord(0x82D64168, 0x00000000, 0x396B05D0, "addi r11,r11,0x5D0  the player's chosen vehicle (Choose Vehicle)"),
            new ExeWord(0x82D6416C, 0x00000000, 0x814B0000, "lwz r10,0(r11)"),
            new ExeWord(0x82D64170, 0x00000000, 0x2B0A0000, "cmplwi cr6,r10,0"),
            new ExeWord(0x82D64174, 0x00000000, 0x419A0008, "beq cr6,keep"),
            new ExeWord(0x82D64178, 0x00000000, 0x7D735B78, "mr r19,r11  spawn the chosen vehicle instead of the marker's"),
            new ExeWord(0x82D6417C, 0x00000000, 0x81730000, "lwz r11,0(r19)"),
            new ExeWord(0x82D64180, 0x00000000, 0x38210060, "addi r1,r1,0x60"),
            new ExeWord(0x82D64184, 0x00000000, 0x8001FFF8, "lwz r0,-0x8(r1)"),
            new ExeWord(0x82D64188, 0x00000000, 0x7C0803A6, "mtlr r0"),
            new ExeWord(0x82D6418C, 0x00000000, 0x4E800020, "blr"),
        });

    public static readonly ExeMod VehicleSpinLimit = new(
        "vehicle-spin-limit",
        "Stable fast vehicles (spin limit 6.85 rad/s)",
        "Every vehicle is one Havok rigid body whose motion state allows up to 202.8 rad/s of angular velocity (hkUFloat8 0x7F at body +0x110 +0x7D). With strong engines (Super engine) the wheel contact forces at high speed (kerbs, ramps, landings, other vehicles) give the body impulses that start it spinning and hopping; in the air nothing slows a spin down except the small angular damping. The wheel force routine 0x82231A80 now sets that limit to 6.85 rad/s (0x4C, a little over one turn per second) on the vehicle body every frame, so vehicles can still roll, flip and turn sharply but no longer whirl. Havok clamps the angular velocity itself. Vehicles without wheels are not changed.",
        "Xenia 2026-10-05: see MERGE.md (body byte read back 0x4C each frame; a 12 rad/s spin written into the body is clamped to <= 6.85 rad/s in the next frame).",
        new[]
        {
            new ExeWord(0x82231AAC, 0x815C07C0, 0x48B326F5, "bl 0x82d641a0"),
            new ExeWord(0x82D641A0, 0x00000000, 0x815C07C0, "lwz r10,0x7C0(r28)  the vehicle's rigid body (the replaced instruction)"),
            new ExeWord(0x82D641A4, 0x00000000, 0x2B0A0000, "cmplwi cr6,r10,0"),
            new ExeWord(0x82D641A8, 0x00000000, 0x4D9A0020, "beqlr cr6"),
            new ExeWord(0x82D641AC, 0x00000000, 0x3800004C, "li r0,0x4C  hkUFloat8 0x4C = 6.85 rad/s (retail 0x7F = 202.8)"),
            new ExeWord(0x82D641B0, 0x00000000, 0x980A018D, "stb r0,0x18D(r10)  body +0x110 motion state +0x7D m_maxAngularVelocity"),
            new ExeWord(0x82D641B4, 0x00000000, 0x4E800020, "blr"),
        });

    public static readonly ExeMod TownNoCeiling = new(
        "town-no-ceiling",
        "No ceiling: fly as high as you want (Showdown Town lid removed, world top raised)",
        "Two parts. (1) Showdown Town's world collision (aid_havok_banjox_background_showdowntown_default, bundle 234cec) has an invisible lid over the town: 24 triangles, flat at y 106.8 over most of the town and rising like a tent to y 214 above the centre; helicopters and planes hit it and slide up to its peak. The tweak carries a world edit (asset-patch op) that shrinks those 24 triangles to a speck at the peak (only when the asset is the retail one, so a replaced town collision is never damaged). (2) The world box top (the reset ceiling) is level collision top + 100; the box builder 0x822EB698 now adds another 1948 on Y max only (top + 2048; the bottom and X/Z are unchanged, so it combines with world-bounds-2048), so flying high no longer resets Banjo in any world.",
        "Xenia 2026-10-05: see MERGE.md.",
        new[]
        {
            new ExeWord(0x822EB938, 0xD1A300D4, 0x48A78888, "b 0x82d641c0"),
            new ExeWord(0x82D641C0, 0x00000000, 0x3D0082D6, "lis r8,0x82D6"),
            new ExeWord(0x82D641C4, 0x00000000, 0xC18841D4, "lfs f12,0x41D4(r8)  1948.0 (below)"),
            new ExeWord(0x82D641C8, 0x00000000, 0xEDAD602A, "fadds f13,f13,f12  max.y + 100 + 1948"),
            new ExeWord(0x82D641CC, 0x00000000, 0xD1A300D4, "stfs f13,0xD4(r3)  W+0x624 world box max.y (the replaced instruction, raised)"),
            new ExeWord(0x82D641D0, 0x00000000, 0x4B58776C, "b 0x822EB93C"),
            new ExeWord(0x82D641D4, 0x00000000, 0x44F38000, ".float 1948.0"),
        });

    /// <summary>World edit of the "No ceiling" tweak: the Showdown Town lid triangles shrunk to a speck at their peak (vertices 41100..41116 of the town collision mesh, apex 41113 kept; guarded by the retail values).</summary>
    public static readonly IReadOnlyList<IReadOnlyList<string>> TownNoCeilingOps = new[]
    {
        new[] { "asset-patch", "234cec", "aid_havok_banjox_background_showdowntown_default",
            "DB700=C3E41B59:BFB13BFA", "DB704=42D5B2B2:4356011B", "DB708=441BD0E6:43196C32", "DB70C=C3E41B59:BFB13BFA", "DB710=42D5B2B2:4356011B", "DB714=43214682:43193110",
            "DB718=3EC61FD5:BF940309", "DB71C=42D5B2B2:4356011B", "DB720=441BD0E6:43196C32", "DB724=C39C8FCE:BFA81397", "DB728=42D5B2B2:4356011B", "DB72C=431D3B44:4319308C",
            "DB730=BEC52859:BF940FAF", "DB734=42D5B2B2:4356011B", "DB738=43EA3B59:43195862", "DB73C=43A5727C:BF7DB800", "DB740=42D5B2B2:4356011B", "DB744=441BD0E6:43196C32",
            "DB748=43A5727C:BF7DB800", "DB74C=42D5B2B2:4356011B", "DB750=431D8D5E:43193096", "DB754=438220C7:BF836157", "DB758=42D5B2B2:4356011B", "DB75C=431B5EB3:4319304F",
            "DB760=C3E41B59:BFB13BFA", "DB764=42D5B2B2:4356011B", "DB768=C402996D:4318D98D", "DB76C=BEC52859:BF940FAF", "DB770=42D5B2B2:4356011B", "DB774=C34534D5:4319032D",
            "DB778=3EC61FD5:BF940309", "DB77C=42D5B2B2:4356011B", "DB780=C402996D:4318D98D", "DB784=43A5727C:BF7DB800", "DB788=42D5B2B2:4356011B", "DB78C=C402996D:4318D98D",
            "DB790=BF941C55:BF941C55", "DB794=43082BDB:435604DC", "DB798=43C28F01:43194E3A", "DB7A8=43439EB2:BF878456", "DB7AC=43082BDB:435604DC", "DB7B0=43193007:43193007",
            "DB7B4=BF941C55:BF941C55", "DB7B8=43082BDB:435604DC", "DB7BC=C2A57BE7:431911D4", "DB7C0=C345EF24:BFA0B454", "DB7C4=43082BDB:435604DC", "DB7C8=43193007:43193007" },
    };

    /// <summary>World edits that belong to a tweak (by mod id): NB Multiplayer puts them into the tweak mod (Ops).</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<string>>> TweakOps => new Dictionary<string, IReadOnlyList<IReadOnlyList<string>>>
    {
        [TownNoCeiling.Id] = TownNoCeilingOps,
    };

    // ---- mods agent 2026-10-05: end ----
    /// <summary>
    /// Vehicle part limit P (251..2000) — the same patch as <see cref="VehiclePartLimit400"/> with P in the three limit
    /// compares, the preview loader clamp, and the two stack frames that hold one u32 per block grown by align16(4·(P−250))
    /// (the break-up routine's memset becomes 4·P). P = 400 returns the verified fixed mod unchanged.
    /// Id: "vehicle-part-limit:P".
    /// </summary>
    public static ExeMod VehiclePartLimit(int p)
    {
        if (p == 400) return VehiclePartLimit400;
        if (p < 251 || p > 2000) throw new ArgumentOutOfRangeException(nameof(p), "part limit must be 251..2000");
        uint grow = (uint)((4 * (p - 250) + 15) & ~15);
        uint spawn = 0x4B0 + grow, brk = 0x4D0 + grow;
        uint P = (uint)p;
        var w = VehiclePartLimit400.Words.Select(x => x.Address switch
        {
            0x82604054 or 0x826041C0 or 0x82648150 => x with { Patched = 0x2F0B0000 | P, Comment = $"cmpwi r11,{p} (part limit)" },
            0x825697D0 => x with { Patched = 0x94210000 | ((0x10000 - spawn) & 0xFFFF), Comment = $"spawner: stwu r1,-0x{spawn:X}(r1)" },
            0x82569B94 => x with { Patched = 0x38210000 | spawn, Comment = $"spawner: addi r1,r1,0x{spawn:X}" },
            0x825F15B8 => x with { Patched = 0x94210000 | ((0x10000 - brk) & 0xFFFF), Comment = $"break-up: stwu r1,-0x{brk:X}(r1)" },
            0x825F1654 => x with { Patched = 0x38A00000 | (4 * P), Comment = $"break-up: li r5,{4 * p} (memset u32[{p}])" },
            0x825F17E0 => x with { Patched = 0x38210000 | brk, Comment = $"break-up: addi r1,r1,0x{brk:X}" },
            _ => x,
        }).ToList();
        return new ExeMod($"vehicle-part-limit:{p}", $"Vehicle part limit {p} (instead of 250)",
            VehiclePartLimit400.Description.Replace("They now allow 400", $"They now allow {p}"),
            $"Generated for {p} parts from the verified 400-part mod (same code sites; limits and stack frames scaled). " +
            "Boot and garage use verified for 600 (docs/research/152_garage.md §8); larger values are untested.", w);
    }

    /// <summary>Garage build area N cells per axis (20..60; 31 returns the verified fixed mod). Id: "garage-build-area:N".</summary>
    public static ExeMod GarageBuildArea(int n)
    {
        if (n == 31) return GarageBuildArea31;
        if (n < 20 || n > 60) throw new ArgumentOutOfRangeException(nameof(n), "build area must be 20..60 cells (the garage room fits about 60)");
        static uint Packed(int a, int b, int c) => (uint)(((a & 0x7FF) << 21) | ((b & 0x3FF) << 11) | (c & 0x7FF));
        uint k0 = Packed(0, n, n), kN = Packed(n, n, n), N = (uint)n;
        var w = new List<ExeWord>
        {
            new(0x8264AC14, 0x2F090013, 0x2F090000 | N, $"cmpwi r9,{n} (extent A)"),
            new(0x8264AC30, 0x2F090013, 0x2F090000 | N, $"cmpwi r9,{n} (extent B)"),
            new(0x8264AC4C, 0x2F0B0013, 0x2F0B0000 | N, $"cmpwi r11,{n} (extent C)"),
            new(0x8264AD00, 0x3D600000, 0x3D600000 | (k0 >> 16), $"lis r11,0x{k0 >> 16:X} (packed 0,{n},{n} high)"),
            new(0x8264AD04, 0x3D000260, 0x3D000000 | (kN >> 16), $"lis r8,0x{kN >> 16:X} (packed {n},{n},{n} high)"),
            new(0x8264AD0C, 0x616A9813, 0x616A0000 | (k0 & 0xFFFF), $"ori (packed 0,{n},{n})"),
            new(0x8264AD18, 0x610B9813, 0x610B0000 | (kN & 0xFFFF), $"ori (packed {n},{n},{n})"),
            new(0x8264AD38, 0x21290013, 0x21290000 | N, $"subfic r9,r9,{n} (clamp)"),
            new(0x8264ADA8, 0x216B0013, 0x216B0000 | N, $"subfic r11,r11,{n} (clamp)"),
        };
        w.RemoveAll(x => x.Patched == x.Original);
        return new ExeMod($"garage-build-area:{n}", $"Larger garage build area ({n} cells per axis instead of 19)",
            GarageBuildArea31.Description.Replace("raised to 31", $"raised to {n}"),
            $"Generated from the verified 31-cell mod (same code sites). Values above 31 are untested in game.", w);
    }

    /// <summary>Mod by id, including parameterised ids ("vehicle-part-limit:600", "garage-build-area:40"); null if unknown.</summary>
    public static ExeMod? Resolve(string id)
    {
        var fixedMod = All.FirstOrDefault(m => m.Id == id);
        if (fixedMod != null) return fixedMod;
        int c = id.IndexOf(':');
        if (c < 0 || !int.TryParse(id[(c + 1)..], out int v)) return null;
        return id[..c] switch { "vehicle-part-limit" => VehiclePartLimit(v), "garage-build-area" => GarageBuildArea(v), _ => null };
    }

    /// <summary>
    /// The mods for a list of enabled ids. At most one part-limit and one build-area mod (they patch the same words):
    /// the last one listed wins.
    /// </summary>
    public static List<ExeMod> ResolveAll(IEnumerable<string> ids)
    {
        var list = new List<ExeMod>();
        foreach (var id in ids)
        {
            var m = Resolve(id); if (m == null) continue;
            string fam = FamilyOf(m.Id);
            if (fam.Length > 0) list.RemoveAll(x => FamilyOf(x.Id) == fam);
            list.RemoveAll(x => x.Id == m.Id);
            list.Add(m);
        }
        return list;
    }

    /// <summary>The tweaks NB Multiplayer offers as tick-box mods: a short name and a one-line description for players.</summary>
    public static IReadOnlyList<(ExeMod Mod, string Name, string Blurb)> Tweaks => new (ExeMod, string, string)[]
    {
        (VehiclePartLimit(2000), "Unlimited parts (2000)", "Build vehicles with up to 2000 parts instead of 250."),
        (WorldBounds2048, "Bigger world edge", "Pushes the invisible edge of every world out to 2048 units."),
        (NoEscapeReset, "No world-edge reset", "No reset when you fly past the world edge, above the sky or below the map."),
        (GarageBuildArea31, "Bigger garage", "A bigger build area in Mumbo's Motors (31 cells per side instead of 19)."),
        (DrawDistanceX4, "Longer draw distance", "Objects stay visible four times farther away."),
        (ChangeVehicleInTown, "Change vehicles in town", "Change Vehicle and Build Vehicle in Showdown Town's pause menu (add the town crash fix too)."),
        (TownNpcPathGuard, "Town crash fix", "Fixes a crash when changing vehicles in Showdown Town."),
        (TownVehiclesNormalRules, "Breakable town vehicles", "Vehicles in Showdown Town take damage and break apart like in the other worlds."),
        (TownAiRestartOnChangeVehicle, "Town AI keeps driving", "After Change Vehicle, the town's AI vehicles keep driving."),
        (AiSpringTimer, "Jumping AI vehicles", "AI drivers fire their vehicles' springs now and then."),
        (PhotoCameraUnlimited, "Free photo camera", "The photo-mode camera can fly anywhere."),
        (DeveloperAllParts, "All parts unlocked", "A new game starts with every vehicle part unlocked (the developers' part list)."),
        (DeveloperMainMenu, "Developer main menu", "The title screen opens the developers' hidden main menu."),
        (UnlimitedPartQuantity, "Unlimited part quantities", "9999 of every part you own in Mumbo's garage (no more 4-engine limit)."),
        (TownFlightThrust, "Planes fly in town", "Propellers and jets push in Showdown Town like in the other worlds."),
        (TownNoCeiling, "No ceiling", "Removes Showdown Town's invisible roof and lets you fly far higher in every world without a reset."),
        (VehicleSpinLimit, "Stable fast vehicles", "Vehicles with strong engines no longer spin wildly in the air (spin speed limit)."),
        (LogsChoiceUnlock, "L.O.G.'s Choice unlock", "After you win the TT trophy of a L.O.G.'s Choice game, Choose Vehicle unlocks there."),
    };

    public static string FamilyOf(string id) => id.StartsWith("vehicle-part-limit") ? "vehicle-part-limit" : id.StartsWith("garage-build-area") ? "garage-build-area" : "";

    public static readonly IReadOnlyList<ExeMod> All = new[] { ChangeVehicleInTown, TownVehiclesNormalRules, AiSpringTimer, TownAiRestartOnChangeVehicle, DeveloperMainMenu, DeveloperAllParts, WorldBounds2048, NoEscapeReset, TownNpcPathGuard, CoopRemoteDamage, CoopSharedTime, CoopWorldRuns, CoopRemoteVehicle, CoopProjectiles, CoopOnFoot, TownGarageReturnVehicle, UnknownPartsSafe, SmallRoomMatchmaking, CharacterSelect, SnowFollowsCamera, DrawDistanceX4, PhotoCameraUnlimited, PauseOpensPhotos, GarageBuildArea31, VehiclePartLimit400, TownFlightThrust, UnlimitedPartQuantity, LogsChoiceUnlock, VehicleSpinLimit, TownNoCeiling };

    /// <summary>Checks that every patched word currently holds its original value in the decrypted image.</summary>
    public static List<string> Check(byte[] image, uint imageBase, ExeMod mod)
    {
        var problems = new List<string>();
        foreach (var w in mod.Words)
        {
            long o = w.Address - (long)imageBase;
            if (o < 0 || o + 4 > image.Length) { problems.Add($"0x{w.Address:X8} outside the image"); continue; }
            uint cur = BE.U32(image, (int)o);
            if (cur != w.Original) problems.Add($"0x{w.Address:X8} is 0x{cur:X8}, expected 0x{w.Original:X8} (different executable version?)");
        }
        return problems;
    }

    /// <summary>
    /// The module hash Xenia Canary uses to match patch files: XXH3-64 over the loaded image from the first to the last
    /// page descriptor of type "code". (Xenia multiplies descriptor indices by the page size; this reproduces that.)
    /// </summary>
    public static ulong XeniaModuleHash(byte[] xex, byte[] image, uint pageSize = 0x10000)
    {
        uint sec = BE.U32(xex, 0x10);
        uint count = BE.U32(xex, (int)sec + 0x180);
        int first = -1, last = -1;
        for (int i = 0; i < count; i++)
        {
            uint v = BE.U32(xex, (int)sec + 0x184 + 0x18 * i);
            if ((v & 0xF) != 1) continue;   // section type 1 = code
            if (first < 0) first = i;
            last = i;
        }
        if (first < 0) throw new InvalidDataException("no code pages in the XEX security info");
        int start = (int)(first * pageSize), end = (int)Math.Min(image.Length, (last + 1) * (long)pageSize);
        return XxHash3.HashToUInt64(image.AsSpan(start, end - start));
    }

    /// <summary>
    /// Xenia hashes the code pages after its loader has rewritten import thunks, so the hash of the file image differs.
    /// Known executables: file-image hash (<see cref="XeniaModuleHash"/>) → hash Xenia reports ("Module Hash:" in xenia.log).
    /// </summary>
    public static readonly Dictionary<ulong, ulong> KnownXeniaHashes = new()
    {
        [0x66DC9721DEB966A8] = 0xC03916823ADAC91B,   // retail default.xex (verified: Xenia Canary c332733 log)
    };

    /// <summary>The module hash to put in a Xenia patch file: known table first, then the last "Module Hash:" in xenia.log.</summary>
    public static ulong? ResolveXeniaHash(ulong imageHash, string? xeniaDir)
    {
        if (KnownXeniaHashes.TryGetValue(imageHash, out var h)) return h;
        var log = xeniaDir == null ? null : Path.Combine(xeniaDir, "xenia.log");
        if (log != null && File.Exists(log))
            foreach (var line in File.ReadLines(log).Reverse())
                if (line.Contains("Module Hash:") && ulong.TryParse(line[(line.IndexOf("Module Hash:") + 12)..].Trim(), System.Globalization.NumberStyles.HexNumber, null, out var lh)) return lh;
        return null;
    }

    /// <summary>Writes (or removes) the Xenia patch file for the enabled mods. Returns the file path.</summary>
    public static string WriteXeniaPatchFile(string xeniaDir, ulong moduleHash, IEnumerable<ExeMod> enabled)
    {
        var dir = Path.Combine(xeniaDir, "patches");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{TitleId:X8} - Banjo-Kazooie Nuts and Bolts (NB Mod Tool).patch.toml");
        var mods = enabled.ToList();
        if (mods.Count == 0) { if (File.Exists(path)) File.Delete(path); return path; }
        var sb = new StringBuilder();
        sb.AppendLine("# Written by NB Mod Tool. Executable patches are applied in memory by Xenia; default.xex is not modified.");
        sb.AppendLine("title_name = \"Banjo-Kazooie: Nuts & Bolts\"");
        sb.AppendLine($"title_id = \"{TitleId:X8}\"");
        sb.AppendLine($"hash = \"{moduleHash:X16}\"");
        foreach (var m in mods)
        {
            sb.AppendLine();
            sb.AppendLine("[[patch]]");
            sb.AppendLine($"    name = \"{m.Name}\"");
            sb.AppendLine($"    desc = \"{m.Description.Replace("\"", "'")}\"");
            sb.AppendLine("    author = \"NB Mod Tool\"");
            sb.AppendLine("    is_enabled = true");
            foreach (var w in m.Words)
            {
                sb.AppendLine();
                sb.AppendLine("    [[patch.be32]]");
                sb.AppendLine($"        address = 0x{w.Address:X8}");
                sb.AppendLine($"        value = 0x{w.Patched:X8} # was 0x{w.Original:X8}: {w.Comment}");
            }
        }
        File.WriteAllText(path, sb.ToString());
        return path;
    }
}
