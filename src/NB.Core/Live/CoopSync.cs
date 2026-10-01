using System.Numerics;
using NB.Core.IO;

namespace NB.Core.Live;

/// <summary>One player's vehicle (or on-foot) state, as sent 30 times a second in Showdown Town co-op.</summary>
public struct CoopState
{
    public bool InVehicle;
    public Vector3 Position, Velocity, AngularVelocity;
    public Quaternion Rotation;
    public uint Blueprint;

    public const int Size = 4 + 13 * 4 + 4;
    public byte[] Write()
    {
        var b = new byte[Size];
        b[0] = (byte)(InVehicle ? 1 : 0);
        int o = 4;
        foreach (var f in new[] { Position.X, Position.Y, Position.Z, Rotation.X, Rotation.Y, Rotation.Z, Rotation.W,
                                  Velocity.X, Velocity.Y, Velocity.Z, AngularVelocity.X, AngularVelocity.Y, AngularVelocity.Z })
        { BE.WF32(b, o, f); o += 4; }
        BE.W32(b, o, Blueprint);
        return b;
    }
    public static CoopState Read(byte[] b)
    {
        float F(int i) => BE.F32(b, 4 + 4 * i);
        return new CoopState
        {
            InVehicle = b[0] == 1,
            Position = new(F(0), F(1), F(2)), Rotation = new(F(3), F(4), F(5), F(6)),
            Velocity = new(F(7), F(8), F(9)), AngularVelocity = new(F(10), F(11), F(12)),
            Blueprint = BE.U32(b, 4 + 13 * 4),
        };
    }
}

/// <summary>
/// Showdown Town co-op inside one running game: reads the local player's vehicle and drives "puppet" vehicles (AI
/// vehicles of the co-op edition) to where the other players are. Verified layout (docs: nb-town-coop research):
/// vehicle objects have vtable 0x82FB7F78, blueprint id +0x18A4, spawn link +0x8B8, position +0x50; a whole vehicle is one
/// Havok rigid body, [vehicle+0x7C0]; its motion state position is at body+0x110 with the 3x3 rotation (columns) 0x30
/// before it, centres of mass +0x10/+0x20, quaternions (x,y,z,w) +0x30/+0x40, linear velocity +0x90 and angular velocity +0xA0.
/// Puppets are steered by velocity (target velocity + 4 × position error), so collisions with them behave physically.
/// </summary>
public sealed class CoopSync
{
    public const uint VehicleVtable = 0x82FB7F78;
    readonly XeniaLive _x;
    readonly uint _puppetBlueprint;
    readonly Vector3 _parkSpot;
    List<uint> _vehicles = new();
    DateTime _lastScan = DateTime.MinValue;
    uint _localBody;
    readonly Dictionary<long, uint> _assigned = new();   // remote player id -> puppet body (motion-state position address)

    public int PuppetCount { get; private set; }
    public string Status { get; private set; } = "";

    /// <param name="parkSpot">Where unused puppets wait (out of sight).</param>
    public CoopSync(XeniaLive x, uint puppetBlueprint, Vector3 parkSpot) { _x = x; _puppetBlueprint = puppetBlueprint; _parkSpot = parkSpot; }

    static uint BodyOf(XeniaLive x, uint veh) { uint b = x.U32(veh + 0x7C0); return b is >= 0x40000000 and < 0xA0000000 ? b + 0x110 : 0; }

    void Rescan(bool force = false)
    {
        if (!force && DateTime.UtcNow - _lastScan < TimeSpan.FromSeconds(3) && _vehicles.Count > 0) return;
        _lastScan = DateTime.UtcNow;
        _vehicles = _x.FindU32(VehicleVtable).Where(v => _x.U32(v + 0x18A4) != 0 || _x.U32(v + 0x8B8) != 0).ToList();
        var player = _x.PlayerPosition;
        _localBody = 0;
        float best = 4f;
        foreach (var v in _vehicles)
        {
            float d = Vector3.Distance(_x.V3(v + 0x50), player);
            if (d < best && _x.U32(v + 0x18A4) != _puppetBlueprint) { best = d; _localBody = BodyOf(_x, v); }
        }
        var puppets = _vehicles.Where(v => _x.U32(v + 0x18A4) == _puppetBlueprint).Select(v => BodyOf(_x, v)).Where(b => b != 0).ToHashSet();
        PuppetCount = puppets.Count;
        foreach (var k in _assigned.Where(kv => !puppets.Contains(kv.Value)).Select(kv => kv.Key).ToList()) _assigned.Remove(k);
        _free = puppets.Except(_assigned.Values).ToList();
    }
    List<uint> _free = new();

    Quaternion Q(uint a) { var b = _x.Read(a, 16); return b.Length == 16 ? new(BE.F32(b, 0), BE.F32(b, 4), BE.F32(b, 8), BE.F32(b, 12)) : Quaternion.Identity; }

    /// <summary>The local player's state (vehicle, or on foot at the player position).</summary>
    public CoopState ReadLocal()
    {
        Rescan();
        if (_localBody == 0 || Vector3.Distance(_x.V3(_localBody), _x.PlayerPosition) > 6) { Rescan(force: true); }
        if (_localBody == 0) return new CoopState { InVehicle = false, Position = _x.PlayerPosition, Rotation = Quaternion.Identity };
        return new CoopState
        {
            InVehicle = true, Position = _x.V3(_localBody), Rotation = Q(_localBody + 0x40),
            Velocity = _x.V3(_localBody + 0x90), AngularVelocity = _x.V3(_localBody + 0xA0),
        };
    }

    /// <summary>Drives one puppet per remote player toward its latest state; parks the unused ones.</summary>
    public void Apply(IReadOnlyDictionary<long, CoopState> remotes)
    {
        Rescan();
        foreach (var gone in _assigned.Keys.Where(k => !remotes.ContainsKey(k)).ToList()) { _free.Add(_assigned[gone]); _assigned.Remove(gone); }
        int shown = 0;
        foreach (var (id, st) in remotes)
        {
            if (!_assigned.TryGetValue(id, out var body))
            {
                if (_free.Count == 0) continue;
                body = _free[0]; _free.RemoveAt(0); _assigned[id] = body;
            }
            if (!st.InVehicle) { Hold(body); continue; }   // on foot (not shown yet): the vehicle stays where they got out
            _held.Remove(body);
            Drive(body, st);
            shown++;
        }
        foreach (var b in _free) Park(b);
        Status = $"{shown} player(s) shown, {PuppetCount} puppet vehicle(s) available";
    }

    void Teleport(uint body, Vector3 target)
    {
        var d = target - _x.V3(body);
        foreach (uint o in new uint[] { 0, 0x10, 0x20 }) _x.WV3(body + o, _x.V3(body + o) + d);
    }

    void Drive(uint body, CoopState st)
    {
        var cur = _x.V3(body);
        var target = st.Position;
        // players start on the same spot (and drive through each other's position): a puppet closer than 5 units to the
        // local vehicle is kept 5 units beside it, on the side it comes from, so it never lands inside or on top of it
        if (_localBody != 0)
        {
            var me = _x.V3(_localBody);
            var away = new Vector3(target.X - me.X, 0, target.Z - me.Z);
            if (away.Length() < 5f)
            {
                away = away.Length() < 0.5f ? Vector3.UnitX : Vector3.Normalize(away);
                target = new Vector3(me.X, Math.Max(me.Y, target.Y), me.Z) + away * 5f;
            }
        }
        var err = target - cur;
        if (err.Length() > 25) { Teleport(body, target + new Vector3(0, 0.5f, 0)); return; }
        var v = st.Velocity + 4f * err;
        if (v.Length() > 80) v = Vector3.Normalize(v) * 80;
        _x.WV3(body + 0x90, v);
        var qe = st.Rotation * Quaternion.Conjugate(Q(body + 0x40));
        if (qe.W < 0) qe = Quaternion.Negate(qe);
        float ang = 2 * MathF.Acos(Math.Min(1f, qe.W)), s = MathF.Sqrt(MathF.Max(1e-9f, 1 - qe.W * qe.W));
        var axis = ang > 1e-3f ? new Vector3(qe.X, qe.Y, qe.Z) / s : Vector3.Zero;
        _x.WV3(body + 0xA0, st.AngularVelocity + axis * MathF.Min(ang * 5f, 8f));
    }

    readonly Dictionary<uint, Vector3> _held = new();

    /// <summary>Keeps a puppet standing where it is (its AI driver would otherwise drive it off).</summary>
    void Hold(uint body)
    {
        var cur = _x.V3(body);
        if (!_held.TryGetValue(body, out var spot)) _held[body] = spot = cur;
        else if (Vector3.Distance(cur, spot) > 1.5f) Teleport(body, spot);
        var v = _x.V3(body + 0x90);
        _x.WV3(body + 0x90, new Vector3(0, Math.Min(v.Y, 0), 0));   // keep falling, so it settles on the ground
        _x.WV3(body + 0xA0, Vector3.Zero);
    }

    /// <summary>An unused puppet: with no park spot (0,0,0) it is left to its AI driver (town traffic on its road loop).</summary>
    void Park(uint body)
    {
        if (_parkSpot == Vector3.Zero) return;
        if (Vector3.Distance(_x.V3(body), _parkSpot) > 5) Teleport(body, _parkSpot);
        _x.WV3(body + 0x90, Vector3.Zero);
        _x.WV3(body + 0xA0, Vector3.Zero);
    }
}
