using System.Numerics;
using NB.Core.IO;

namespace NB.Core.Live;

/// <summary>
/// Town sessions, mirrored body states and the other players on the minimap.
/// <list type="bullet">
/// <item>Every Showdown Town load is a new town: the puppets, their AI drivers and the driver objparams (character select)
/// are all new, often at the very same addresses as before (the level object L too). Whatever the app knew about the
/// last town is dropped when the local player leaves town (a world, the garage, the title screen): their characters are
/// written and respawned again as soon as the player is back.</item>
/// <item>Body states: only plain movement / reaction states are mirrored onto an on-foot puppet. States that work on an
/// object of the sender's game (a Jig-O-Vend's bolt, a ledge, a pole, a rope, a dialog, a collected Jiggy...) have no
/// such object in the receiver's game: set on a puppet they crashed or froze the receiving game.</item>
/// <item>Minimap: every other player in town is a coloured player marker on the local minimap (the game's own LIVE
/// multiplayer colours, sceneIndicatorType_LiveColourOne..., map-only icons) through the coop-onfoot indicator slots.</item>
/// </list>
/// </summary>
public sealed partial class CoopSync
{
    bool _away = true;

    /// <summary>The local player is not in Showdown Town gameplay right now (called by <see cref="ReadLocal"/>). On the way
    /// out the coop-onfoot slots are emptied at once: its cave runs in the local avatar update of EVERY level, and a driver
    /// slot still naming a Banjo of the town just left would be driven in the next level (freed memory - seen as a slot
    /// pointing at a dead avatar while the player was in Nutty Acres).</summary>
    void NoteAway()
    {
        if (_away) return;
        _away = true;
        try { WithdrawVehicleRequest(); } catch (InvalidOperationException) { }   // (coopA: vehicle mailbox too)
        if (!HasFootMod) return;
        try { ClearFootMailbox(); } catch (InvalidOperationException) { }
    }

    void ClearFootMailbox()
    {
        _fop = null;
        uint seq = _x.U32(FMailbox);
        if (_x.U32(FMailbox + 4) != seq) { var b = new byte[4]; BE.W32(b, 0, seq); _x.Write(FMailbox + 4, b); }
        for (uint i = 0; i < 4; i++) if (_x.U32(FSlots + 0x40 * i) != 0) _x.Write(FSlots + 0x40 * i, new byte[4]);
        for (uint i = 0; i < (uint)IndicatorSlots; i++) if (_x.U32(FIndicators + 0x20 * i) != 0) _x.Write(FIndicators + 0x20 * i, new byte[4]);
        Array.Clear(_indShown); Array.Clear(_indPos);
    }

    /// <summary>Diagnostics: town loads seen (the app's town state was reset for each).</summary>
    public int TownSessions { get; private set; }

    /// <summary>The local player is in town again: everything known about the last town's objects is dropped.</summary>
    void EnterTown()
    {
        if (!_away) return;
        _away = false;
        TownSessions++;
        // puppets and their drivers of the last town
        _assigned.Clear(); _free.Clear(); _vehicles.Clear(); _walkers.Clear(); _lastPuppet.Clear(); _respawnWanted = false; _orphans.Clear();
        _held.Clear(); _hidden.Clear(); _apart.Clear(); _offSince.Clear(); _noBody.Clear();
        _designOf.Clear(); _applied.Clear(); _blockMaps.Clear(); _detachTries.Clear(); _quietUntil.Clear(); _designWait.Clear();
        foreach (var v in _origBlocks.Keys.Where(v => v != _designVeh).ToList()) _origBlocks.Remove(v);
        _lastScan = DateTime.MinValue;
        ResetVehicleTown();                                   // (coopA) rebuilds, called-back parts, entry locks
        // character select: the driver objparams are reloaded with the town bundle (Banjo again)
        _charLevel = 0xFFFFFFFF;
        _respawnPending = false;
        // an on-foot command the last town did not run any more (it would run with that town's pointers): cancelled
        if (HasFootMod) ClearFootMailbox();
    }

    // ------------------------------------------------------------------ body states mirrored on foot

    /// <summary>Body states an on-foot puppet may show: movement, jumps and falls, the wrench spin and the reactions to
    /// hits (coop/research/onfoot: Stand, Walk, Jump, WrenchAround, Hit / KnockDown / GetUp verified on puppets).</summary>
    static readonly HashSet<ushort> SafeBodyStates = new()
    {
        3,                                  // Anim2Stand
        15, 16, 17,                         // CatchBreath, Cheer, CheerAir
        20, 24,                             // Coast, Crouch
        30, 31,                             // Dizzy, DizzyAir
        43, 51, 52, 53, 54, 55,             // Drop, Fall, FallCrashLand(+Dizzy, GetUp, Recover)
        63, 64, 68, 70, 71, 89,             // Flyback(Air), GetUp, Hit, HitFreeze, KnockDown
        79, 84, 85, 86, 88, 90,             // Jump, JumpFromWater, JumpLand, JumpLandSoft, JumpToRun, Land
        95, 100, 104, 111, 113, 115,        // Push, Roll, SaveBlueprint, Shuffle, Skid, Slide
        118, 119, 120, 125,                 // Stand, StandAlert, StandIdle, SuddenStop
        129, 130, 131, 134, 138, 139, 140, 141, 142, 143, 144,   // Swim..., Taunt, Tired..., TreadWater, Turn, TurnSkid, TurnTreadWater
        146, 147, 148,                      // Walk, WalkAlert, WalkPanic
        150, 151, 152, 153,                 // WrenchAround (Into, OutOf, ToRun): the X-button wrench spin
        77, 78,                             // IntoTreadWater, IntoWater
    };

    /// <summary>
    /// The body state a puppet shows for a remote player's state <paramref name="id"/> (0 = keep the one it has).
    /// Hanging from a ledge (LedgeGrab 92, DropLedge 44) shows as a jump, turning a bolt (WrenchIt 154/155: Jig-O-Vends)
    /// and the other object states as standing.
    /// </summary>
    public static ushort PuppetBodyState(ushort id) => id switch
    {
        0 => 0,
        _ when SafeBodyStates.Contains(id) => id,
        92 or 44 => 79,                     // LedgeGrab / DropLedge -> Jump (the ledge is in the other game only)
        _ => 118,                           // WrenchIt (Jig-O-Vend bolt), Collect*, Dialog, Talk, ClimbPole, TightRope... -> Stand
    };

    // ------------------------------------------------------------------ minimap

    /// <summary>Indicator slots the coop-onfoot cave draws each frame: 8 since co-op 1.7 (word "li r29,8" at 0x82D62F44), 4 before.</summary>
    int IndicatorSlots => _indicatorSlots ??= _x.U32(IndicatorCountWord) == 0x3BA00008 ? 8 : 4;
    int? _indicatorSlots;
    const uint IndicatorCountWord = 0x82D62F44;

    /// <summary>The game's LIVE player colours (map-only icons): one per remote player, by their order in the room.</summary>
    static readonly uint[] PlayerMarkers = { 0x37, 0x3B, 0x3A, 0x36, 0x35, 0x39, 0x38, 0x3C };   // LiveColourOne, Two, Three, Four, Five, Six, Seven, Zero

    /// <summary>Where remote <paramref name="id"/> is shown in this game right now (their walker or puppet), else their state.</summary>
    Vector3 ShownPosition(long id, CoopState st)
    {
        uint w = WalkerOf(id);
        if (w != 0) { var p = _x.V3(w + 0x50); if (float.IsFinite(p.X + p.Y + p.Z) && p != Vector3.Zero) return p; }
        uint v = PuppetOf(id);
        if (v != 0) { uint b = BodyOf(_x, v); if (b != 0) { var p = _x.V3(b); if (float.IsFinite(p.X + p.Y + p.Z) && p != Vector3.Zero) return p; } }
        return st.Position;
    }

    /// <summary>The marker icon type of the <paramref name="order"/>-th remote player.</summary>
    public static uint PlayerMarker(int order) => PlayerMarkers[order % PlayerMarkers.Length];
}
