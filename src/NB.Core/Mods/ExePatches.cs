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

    public static string FamilyOf(string id) => id.StartsWith("vehicle-part-limit") ? "vehicle-part-limit" : id.StartsWith("garage-build-area") ? "garage-build-area" : "";

    public static readonly IReadOnlyList<ExeMod> All = new[] { ChangeVehicleInTown, TownVehiclesNormalRules, AiSpringTimer, TownAiRestartOnChangeVehicle, DeveloperMainMenu, DeveloperAllParts, WorldBounds2048, NoEscapeReset, TownNpcPathGuard, DrawDistanceX4, PhotoCameraUnlimited, PauseOpensPhotos, GarageBuildArea31, VehiclePartLimit400 };

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
