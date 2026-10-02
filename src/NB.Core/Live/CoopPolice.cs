using System.Numerics;
using NB.Core.IO;

namespace NB.Core.Live;

/// <summary>One Showdown Town police unit as the host's game has it (index = spawner order, the same in every game).</summary>
public readonly record struct PoliceUnit(byte Index, bool Responding, Vector3 Position, Vector3 Velocity, Quaternion Rotation);

/// <summary>The host's police: which of the 13 units exist and where (co-op packet type 8).</summary>
public sealed class PoliceSnapshot
{
    public ushort Seq;
    public List<PoliceUnit> Units = new();
    public const int UnitSize = 28;

    /// <summary>u16 seq, u16 present mask, then per unit: u8 index, u8 flags (1 = responding), 3 x f32 position,
    /// 3 x i16 velocity x 100, 4 x i16 quaternion x 32767.</summary>
    public byte[] Write()
    {
        var b = new byte[4 + UnitSize * Units.Count];
        BE.W16(b, 0, Seq);
        ushort mask = 0; foreach (var u in Units) mask |= (ushort)(1 << u.Index);
        BE.W16(b, 2, mask);
        int o = 4;
        static short S(float v, float k) => (short)Math.Clamp(MathF.Round(v * k), short.MinValue, short.MaxValue);
        foreach (var u in Units)
        {
            b[o] = u.Index; b[o + 1] = (byte)(u.Responding ? 1 : 0);
            BE.WF32(b, o + 2, u.Position.X); BE.WF32(b, o + 6, u.Position.Y); BE.WF32(b, o + 10, u.Position.Z);
            BE.W16(b, o + 14, (ushort)S(u.Velocity.X, 100)); BE.W16(b, o + 16, (ushort)S(u.Velocity.Y, 100)); BE.W16(b, o + 18, (ushort)S(u.Velocity.Z, 100));
            BE.W16(b, o + 20, (ushort)S(u.Rotation.X, 32767)); BE.W16(b, o + 22, (ushort)S(u.Rotation.Y, 32767));
            BE.W16(b, o + 24, (ushort)S(u.Rotation.Z, 32767)); BE.W16(b, o + 26, (ushort)S(u.Rotation.W, 32767));
            o += UnitSize;
        }
        return b;
    }

    public static PoliceSnapshot? Read(byte[] b, int at)
    {
        if (b.Length < at + 4) return null;
        var s = new PoliceSnapshot { Seq = BE.U16(b, at) };
        for (int o = at + 4; o + UnitSize <= b.Length; o += UnitSize)
        {
            float S(int k, float div) => (short)BE.U16(b, o + k) / div;
            if (b[o] > 15) continue;
            var q = new Quaternion(S(20, 32767), S(22, 32767), S(24, 32767), S(26, 32767));
            if (q.LengthSquared() < 0.5f) continue;
            s.Units.Add(new PoliceUnit(b[o], (b[o + 1] & 1) != 0, new Vector3(BE.F32(b, o + 2), BE.F32(b, o + 6), BE.F32(b, o + 10)),
                new Vector3(S(14, 100), S(16, 100), S(18, 100)), Quaternion.Normalize(q)));
        }
        return s;
    }
}

/// <summary>
/// Showdown Town police in co-op (coop/research/npcs/REPORT.txt): pig officers in one-seat hover police cars. The police
/// controller (vtable 0x82FB58D8, [C+0x30] = the level) lists 13 spawners at [C+0x70]..[C+0x74] in marker order, so the
/// spawner index names the same unit in every game. Each spawner's unit: driver = [[S+0xB24]] (vtable 0x82FB7EDC,
/// +0x4C = level), car = [driver+0xC54] (vtable 0x82FB82B0), rigid body [car+0x7C0] (motion state +0x110, as vehicles).
/// The host's game is the reference: joiners spawn the units the host has (spawner enable +0xC94 = 1, threshold
/// +0xB1C = 0, whatever their own Jiggy count), hide the ones it has not, and steer theirs to the host's positions by
/// velocity like the player puppets.
/// </summary>
public sealed class CoopPolice
{
    public const uint ControllerVtable = 0x82FB58D8, CarVtable = 0x82FB82B0, DriverVtable = 0x82FB7EDC;
    readonly XeniaLive _x;
    uint _ctl;
    DateTime _nextScan = DateTime.MinValue;
    readonly HashSet<int> _hidden = new();

    public CoopPolice(XeniaLive x) { _x = x; }

    static bool Ptr(uint a) => a is >= 0x40000000 and < 0xA0000000;
    uint Level => _x.U32(0x82FAC7AC);

    /// <summary>The 13 spawners of the running town (empty when there is no police controller, e.g. not in town).</summary>
    List<uint> Spawners()
    {
        uint level = Level;
        if (!Ptr(_ctl) || _x.U32(_ctl) != ControllerVtable || _x.U32(_ctl + 0x30) != level)
        {
            _ctl = 0;
            if (DateTime.UtcNow < _nextScan) return new();
            _nextScan = DateTime.UtcNow.AddSeconds(4);
            _ctl = _x.FindU32(ControllerVtable).FirstOrDefault(c => _x.U32(c + 0x30) == level);
            if (_ctl == 0) return new();
            _hidden.Clear();
        }
        uint a = _x.U32(_ctl + 0x70), b = _x.U32(_ctl + 0x74);
        if (!Ptr(a) || b < a || b - a > 4 * 32) return new();
        var res = new List<uint>();
        for (uint p = a; p < b; p += 4) res.Add(_x.U32(p));
        return res;
    }

    /// <summary>A spawner's live unit (driver, car, car motion state), or zeros.</summary>
    (uint Driver, uint Car, uint Body) Unit(uint spawner)
    {
        uint a = _x.U32(spawner + 0xB24), b = _x.U32(spawner + 0xB28);
        if (!Ptr(a) || b <= a || b - a >= 64) return default;
        uint d = _x.U32(a);
        if (!Ptr(d) || _x.U32(d) != DriverVtable || _x.U32(d + 0x4C) != Level) return default;
        uint car = _x.U32(d + 0xC54);
        if (!Ptr(car) || _x.U32(car) != CarVtable) return default;
        uint body = _x.U32(car + 0x7C0);
        return Ptr(body) ? (d, car, body + 0x110) : default;
    }

    Quaternion Q(uint a) { var b = _x.Read(a, 16); return b.Length == 16 ? new(BE.F32(b, 0), BE.F32(b, 4), BE.F32(b, 8), BE.F32(b, 12)) : Quaternion.Identity; }
    void W32(uint a, uint v) { var b = new byte[4]; BE.W32(b, 0, v); _x.Write(a, b); }

    /// <summary>Host: the police units its game has now.</summary>
    public PoliceSnapshot Read(ushort seq)
    {
        var snap = new PoliceSnapshot { Seq = seq };
        var sp = Spawners();
        for (int i = 0; i < sp.Count && i < 16; i++)
        {
            var (_, car, body) = Unit(sp[i]);
            if (body == 0) continue;
            var pos = _x.V3(body); var vel = _x.V3(body + 0x90); var q = Q(body + 0x40);
            if (!float.IsFinite(pos.X) || !float.IsFinite(vel.X) || !float.IsFinite(q.W)) continue;
            snap.Units.Add(new PoliceUnit((byte)i, _x.U32(car + 0x3AC) != 0, pos, vel, q));
        }
        return snap;
    }

    /// <summary>Joiner: makes its police match the host's (<paramref name="age"/>: seconds since the host read them).</summary>
    public void Apply(PoliceSnapshot snap, float age)
    {
        var sp = Spawners();
        if (sp.Count == 0) return;
        var host = snap.Units.ToDictionary(u => (int)u.Index);
        for (int i = 0; i < sp.Count; i++)
        {
            uint s = sp[i];
            var (drv, car, body) = Unit(s);
            if (host.TryGetValue(i, out var u))
            {
                // the host has this unit: spawn ours too (whatever this player's Jiggy count), show it and drive it there
                if (_x.U32(s + 0xC94) != 1) W32(s + 0xC94, 1);
                if (_x.U32(s + 0xB1C) != 0) W32(s + 0xB1C, 0);
                if (body == 0)
                {
                    // not spawned here yet: a unit that despawned waits out a 30 s respawn cooldown (+0xACC); the host's
                    // game has it now, so skip the wait (only a sane timer value is touched)
                    float cd = BitConverter.Int32BitsToSingle((int)_x.U32(s + 0xACC));
                    if (cd > 0 && cd <= 60) W32(s + 0xACC, 0);
                    continue;
                }
                if (_hidden.Remove(i)) SetHidden(drv, car, false);
                Steer(body, u.Position + u.Velocity * Math.Clamp(age, 0, 0.35f), u.Velocity, u.Rotation);
            }
            else
            {
                // not in the host's game: none here either (no new spawns; an existing unit is hidden and held)
                if (_x.U32(s + 0xC94) != 0) W32(s + 0xC94, 0);
                if (body == 0) continue;
                SetHidden(drv, car, true); _hidden.Add(i);
                _x.WV3(body + 0x90, Vector3.Zero); _x.WV3(body + 0xA0, Vector3.Zero);
            }
        }
    }

    const uint CarLayer = 2, HiddenLayer = 9;

    void SetHidden(uint drv, uint car, bool hidden)
    {
        uint f = hidden ? 1u : 0u;
        if (_x.U32(car + 0x1C0) != f) W32(car + 0x1C0, f);
        if (_x.U32(drv + 0x1C0) != f) W32(drv + 0x1C0, f);
        uint raw = _x.U32(car + 0x7C0);
        if (Ptr(raw))
        {
            uint w = _x.U32(raw + 0x2C), want = (w & ~0x3Fu) | (hidden ? HiddenLayer : CarLayer);
            if (w != want && (w & 0x3F) is CarLayer or HiddenLayer) W32(raw + 0x2C, want);
        }
    }

    /// <summary>The puppet controller: velocity toward the target (target velocity + 4 x error), angular velocity toward
    /// the target rotation, teleport when more than 25 units away.</summary>
    void Steer(uint body, Vector3 target, Vector3 vel, Quaternion rot)
    {
        var cur = _x.V3(body);
        if (!float.IsFinite(cur.X)) return;
        var err = target - cur;
        if (err.Length() > 25)
        {
            var d = target + new Vector3(0, 0.5f, 0) - cur;
            foreach (uint o in new uint[] { 0, 0x10, 0x20 }) _x.WV3(body + o, _x.V3(body + o) + d);
            return;
        }
        var v = vel + 4f * err;
        if (v.Length() > 80) v = Vector3.Normalize(v) * 80;
        _x.WV3(body + 0x90, v);
        var qe = rot * Quaternion.Conjugate(Q(body + 0x40));
        if (qe.W < 0) qe = Quaternion.Negate(qe);
        float ang = 2 * MathF.Acos(Math.Min(1f, qe.W)), sn = MathF.Sqrt(MathF.Max(1e-9f, 1 - qe.W * qe.W));
        var axis = ang > 1e-3f ? new Vector3(qe.X, qe.Y, qe.Z) / sn : Vector3.Zero;
        _x.WV3(body + 0xA0, axis * MathF.Min(ang * 5f, 8f));
    }
}
