using System.Numerics;
using NB.Core.IO;

namespace NB.Core.Live;

/// <summary>
/// Other players on foot, and where the others are while they change vehicle or build in Mumbo's garage (exe mod
/// coop-onfoot, coop/research/onfoot/REPORT.txt). A remote player on foot is shown by the AI Banjo of their puppet
/// vehicle: EJECTed from it (the puppet vehicle stays where they left it), marked non-local and driven every frame
/// through a driver slot (position, heading, velocity, body state: the game's own walk / run / jump / wrench
/// animations); back in their vehicle he is SEATed again. Wrench hits between players happen natively (the mirrored
/// wrench spin's attack hits the local Banjo in the local game). Change Vehicle open: the vehicle-edit icon (0x6E) over
/// their vehicle; in the garage: the Mumbo pad icon (0x79) where they left town.
/// </summary>
public sealed partial class CoopSync
{
    const uint FMailbox = 0x82FBF420, FSlots = 0x82FBF700, FIndicators = 0x82FBF800;
    const uint FHook = 0x8225126C, FHookWord = 0x48B11A5C, FCave = 0x82D62CC8, FCaveFirstWord = 0x3D8082FB;
    enum FOp : uint { Eject = 1, Seat = 2 }
    public const uint IndicatorVehicleEdit = 0x6E, IndicatorMumboPad = 0x79;

    bool? _footMod;
    /// <summary>This game has the coop-onfoot exe mod.</summary>
    public bool HasFootMod => _footMod ??= _x.U32(FHook) == FHookWord && _x.U32(FCave) == FCaveFirstWord;

    sealed class Walker { public uint R, Puppet; public int Slot = -1, Tries = 1; public bool Out; public DateTime Since = DateTime.UtcNow; }
    readonly Dictionary<long, Walker> _walkers = new();          // remote player -> their Banjo (ejected or being ejected)
    sealed class FootOp { public FOp Op; public long Id; public uint Veh, R, Seq; public DateTime At; public bool Done; }
    FootOp? _fop;
    readonly uint[] _indShown = new uint[4];
    readonly Vector3[] _indPos = new Vector3[4];

    /// <summary>Diagnostics: players shown on foot right now, and on-foot commands run.</summary>
    public int Walking => _walkers.Values.Count(w => w.Out);
    public readonly Dictionary<string, int> FootRequests = new();

    /// <summary>One command at a time; true while one is under way. The cave marks a request done before it runs it
    /// and writes the result last, so the result is read one tick after "done".</summary>
    bool FootBusy()
    {
        if (_fop is not { } op) return _x.U32(FMailbox) != _x.U32(FMailbox + 4);
        if (_x.U32(FMailbox + 4) != op.Seq)
        {
            if (DateTime.UtcNow - op.At > TimeSpan.FromSeconds(2)) _fop = null;   // no local avatar update (menu, load): drop it
            return true;
        }
        if (!op.Done) { op.Done = true; return true; }
        _fop = null;
        uint res = _x.U32(FMailbox + 0x14);
        FootRequests[op.Op + (res != 0 ? "" : " refused")] = FootRequests.GetValueOrDefault(op.Op + (res != 0 ? "" : " refused")) + 1;
        if (!_walkers.TryGetValue(op.Id, out var w) || w.R != op.R) return false;
        if (op.Op == FOp.Eject) { if (res != 0) w.Out = true; else _walkers.Remove(op.Id); }
        else if (op.Op == FOp.Seat)
        {
            if (res == 0) { SetActorHidden(w.R, true); }                // could not get back in: out of sight
            _walkers.Remove(op.Id);
        }
        return false;
    }

    void PostFoot(FOp op, long id, uint veh, uint r)
    {
        var b = new byte[12]; BE.W32(b, 0, (uint)op); BE.W32(b, 4, veh); BE.W32(b, 8, r);
        _x.Write(FMailbox + 8, b);
        uint seq = _x.U32(FMailbox) + 1;
        var s = new byte[4]; BE.W32(s, 0, seq);
        _x.Write(FMailbox, s);
        _fop = new FootOp { Op = op, Id = id, Veh = veh, R = r, Seq = seq, At = DateTime.UtcNow };
    }

    bool AvatarAlive(uint a) => Ptr(a) && _x.U32(a) == AvatarVtable && _x.U32(a + 0x4C) != 0;

    void SetActorHidden(uint a, bool hidden)
    {
        if (!AvatarAlive(a)) return;
        uint flag = hidden ? 1u : 0u;
        if (_x.U32(a + DrawOff) != flag) { var b = new byte[4]; BE.W32(b, 0, flag); _x.Write(a + DrawOff, b); }
        SetTargetable(a, !hidden);
    }

    /// <summary>
    /// Homing weapons lock onto an avatar's targetable ([A+0xA80], [T] = A, type [T+4] = 0): a hidden puppet's driver
    /// stays targetable where the puppet is parked, and torpedoes went for those invisible puppets (the most aligned
    /// target wins, at any distance). Type 32 matches no target mask (1 &lt;&lt; 32 = 0 on the PowerPC), 0 again when shown.
    /// </summary>
    void SetTargetable(uint avatar, bool on)
    {
        uint t = _x.U32(avatar + 0xA80);
        if (!Ptr(t) || _x.U32(t) != avatar) return;
        uint type = _x.U32(t + 4), want = on ? 0u : 32u;
        if (type != want && type is 0 or 32) { var b = new byte[4]; BE.W32(b, 0, want); _x.Write(t + 4, b); }
    }

    int FreeSlot()
    {
        for (int i = 0; i < 4; i++) if (!_walkers.Values.Any(w => w.Slot == i)) return i;
        return -1;
    }

    void ClearSlot(Walker w)
    {
        if (w.Slot < 0) return;
        _x.Write(FSlots + 0x40 * (uint)w.Slot, new byte[4]);
        w.Slot = -1;
    }

    /// <summary>
    /// Remote <paramref name="id"/> is on foot: his Banjo walks where he is. Returns false when the puppet vehicle must be
    /// hidden instead of held (it has another driver: a local Change Vehicle made new puppets while he was out).
    /// </summary>
    bool FootTick(long id, uint puppet, CoopState st, float lead)
    {
        if (!HasFootMod) return true;
        if (!_walkers.TryGetValue(id, out var w))
        {
            uint r = DriverOf(puppet);
            if (r == 0 || _x.U32(r + 0xC3C) != puppet || FootBusy()) return true;
            _walkers[id] = w = new Walker { R = r, Puppet = puppet };
            PostFoot(FOp.Eject, id, puppet, r);
            return true;
        }
        if (!AvatarAlive(w.R)) { ClearSlot(w); _walkers.Remove(id); return true; }
        if (!w.Out) { FootBusy(); return true; }                         // eject under way
        if (_x.U32(w.R + 0xC3C) != 0)
        {
            // still (or again) in a vehicle: the game did not let him out (a vehicle in deep water: seen in tests) - try
            // again after a second, three times at most; then the vehicle just stands there until the player is back in
            if (w.Tries < 3 && DateTime.UtcNow - w.Since > TimeSpan.FromSeconds(1) && !FootBusy())
            { w.Since = DateTime.UtcNow; w.Tries++; PostFoot(FOp.Eject, id, _x.U32(w.R + 0xC3C), w.R); w.Out = false; }
            return true;
        }
        if (w.Slot < 0) w.Slot = FreeSlot();
        if (w.Slot < 0) return true;
        float t = Math.Clamp(lead, 0f, 0.25f);
        var pos = st.Position + st.Velocity * t;
        var fw = Vector3.Transform(Vector3.UnitZ, st.Rotation);
        float yaw = MathF.Atan2(fw.X, fw.Z);
        if (!float.IsFinite(yaw + pos.X + pos.Y + pos.Z)) return true;
        var b = new byte[0x40];
        uint flags = 1 | 4 | (st.BodyState != 0 ? 2u : 0u);
        BE.W32(b, 0, w.R); BE.W32(b, 4, flags); BE.W32(b, 8, st.BodyState);
        float[] f = { pos.X, pos.Y, pos.Z, 1, 0, yaw, 0, 0, st.Velocity.X, st.Velocity.Y, st.Velocity.Z, 0 };
        for (int i = 0; i < f.Length; i++) BE.WF32(b, 0x10 + 4 * i, f[i]);
        uint a = FSlots + 0x40 * (uint)w.Slot;
        _x.Write(a + 0x10, b[0x10..]);                                    // the data, then who it is for
        _x.Write(a, b[..0x10]);
        SetActorHidden(w.R, false);
        // the vehicle he left: held where he left it - unless a local Change Vehicle replaced it by a puppet with its own
        // driver (that one would show a second Banjo): hidden until he is back in a vehicle
        uint d = DriverOf(puppet);
        return d == 0 || d == w.R;
    }

    /// <summary>Remote <paramref name="id"/> is no longer on foot (back in a vehicle, gone, hidden): his Banjo gets back
    /// into his puppet <paramref name="puppet"/> (0 = none), or out of sight when it has another driver.</summary>
    void FootEnd(long id, uint puppet)
    {
        if (!_walkers.TryGetValue(id, out var w)) return;
        ClearSlot(w);
        if (FootBusy()) return;                                          // also finishes a pending eject/seat
        if (!_walkers.TryGetValue(id, out w)) return;
        if (!AvatarAlive(w.R)) { _walkers.Remove(id); return; }
        if (!w.Out) { _walkers.Remove(id); return; }
        if (_x.U32(w.R + 0xC3C) != 0) { _walkers.Remove(id); return; }   // already in a vehicle
        if (puppet != 0 && Alive(puppet) && !Quiet(puppet) && DriverOf(puppet) == 0)
        {
            PostFoot(FOp.Seat, id, puppet, w.R);
            return;
        }
        if (puppet != 0 && Alive(puppet) && DriverOf(puppet) == 0) return;   // puppet being changed: next tick
        // no seat for him (the puppet has a new driver or is gone): out of sight, local again (idle)
        SetActorHidden(w.R, true);
        var one = new byte[4]; BE.W32(one, 0, 1); _x.Write(w.R + 0x24, one);
        _walkers.Remove(id);
    }

    /// <summary>The game's indicator icons for remote players who change vehicle (0x6E over their vehicle) or build in
    /// the garage (0x79 where they left town). The game redraws them every frame from the slots.</summary>
    void Indicators(IReadOnlyDictionary<long, CoopRemote> remotes)
    {
        if (!HasFootMod) return;
        var want = new List<(uint Type, Vector3 Pos)>();
        foreach (var (id, r) in remotes.OrderBy(kv => kv.Key))
        {
            var st = r.State;
            if (!Finite(st) || st.Position == Vector3.Zero) continue;
            if (st.Mode == CoopMode.Building) want.Add((IndicatorVehicleEdit, st.Position + new Vector3(0, 2.5f, 0)));
            else if (st.Mode == CoopMode.Garage) want.Add((IndicatorMumboPad, st.Position + new Vector3(0, 2.5f, 0)));
        }
        for (int i = 0; i < 4; i++)
        {
            var (type, pos) = i < want.Count ? want[i] : (0u, Vector3.Zero);
            if (_indShown[i] == type && Vector3.Distance(_indPos[i], pos) < 0.05f) continue;
            uint a = FIndicators + 0x20 * (uint)i;
            if (type == 0) _x.Write(a, new byte[4]);
            else
            {
                var p = new byte[16]; BE.WF32(p, 0, pos.X); BE.WF32(p, 4, pos.Y); BE.WF32(p, 8, pos.Z); BE.WF32(p, 12, 1);
                _x.Write(a + 0x10, p);
                var h = new byte[8]; BE.W32(h, 0, type); BE.W32(h, 4, 1);
                _x.Write(a, h);
            }
            _indShown[i] = type; _indPos[i] = pos;
        }
    }

    /// <summary>The Banjo who shows remote <paramref name="id"/> on foot (0 = none).</summary>
    uint WalkerOf(long id) => _walkers.TryGetValue(id, out var w) && w.Out && AvatarAlive(w.R) ? w.R : 0;
}
