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
    uint _localBody, _localVeh;
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
        _localBody = 0; _localVeh = 0;
        float best = 4f;
        foreach (var v in _vehicles)
        {
            float d = Vector3.Distance(_x.V3(v + 0x50), player);
            if (d < best && _x.U32(v + 0x18A4) != _puppetBlueprint) { best = d; _localBody = BodyOf(_x, v); _localVeh = v; }
        }
        _vehOf.Clear();
        foreach (var v in _vehicles.Where(v => _x.U32(v + 0x18A4) == _puppetBlueprint)) { var b = BodyOf(_x, v); if (b != 0) _vehOf[b] = v; }
        var puppets = _vehOf.Keys.ToHashSet();
        PuppetCount = puppets.Count;
        foreach (var k in _assigned.Where(kv => !puppets.Contains(kv.Value)).Select(kv => kv.Key).ToList()) _assigned.Remove(k);
        _free = puppets.Except(_assigned.Values).ToList();
    }
    List<uint> _free = new();
    readonly Dictionary<uint, uint> _vehOf = new();   // puppet body -> its vehicle object

    /// <summary>
    /// Is the puppet still the live vehicle it was? Change Vehicle (and break-ups) destroy every vehicle in the level; the
    /// destroyed object loses its body pointer (+0x7C0). Writing into a destroyed body would corrupt the game's memory, so
    /// every write is checked first and a rescan picks up the new vehicles.
    /// </summary>
    bool Alive(uint body) =>
        _vehOf.TryGetValue(body, out var v) && _x.U32(v) == VehicleVtable && _x.U32(v + 0x18A4) == _puppetBlueprint && BodyOf(_x, v) == body;

    Quaternion Q(uint a) { var b = _x.Read(a, 16); return b.Length == 16 ? new(BE.F32(b, 0), BE.F32(b, 4), BE.F32(b, 8), BE.F32(b, 12)) : Quaternion.Identity; }

    /// <summary>The local player's state (vehicle, or on foot at the player position).</summary>
    public CoopState ReadLocal()
    {
        Rescan();
        // the player's vehicle changed (or they got out): look again, at most twice a second (a scan reads all of memory)
        if ((_localBody == 0 || Vector3.Distance(_x.V3(_localBody), _x.PlayerPosition) > 6) && DateTime.UtcNow - _lastScan > TimeSpan.FromSeconds(0.5))
            Rescan(force: true);
        var foot = new CoopState { InVehicle = false, Position = _x.PlayerPosition, Rotation = Quaternion.Identity };
        if (_localBody == 0) return foot;
        var st = new CoopState
        {
            InVehicle = true, Position = _x.V3(_localBody), Rotation = Q(_localBody + 0x40),
            Velocity = _x.V3(_localBody + 0x90), AngularVelocity = _x.V3(_localBody + 0xA0),
        };
        return Finite(st) ? st : foot;
    }

    /// <summary>Only finite numbers ever reach a game: a NaN velocity written into a puppet would spread through its physics.</summary>
    static bool Finite(CoopState s) =>
        float.IsFinite(s.Position.X) && float.IsFinite(s.Position.Y) && float.IsFinite(s.Position.Z) &&
        float.IsFinite(s.Velocity.X) && float.IsFinite(s.Velocity.Y) && float.IsFinite(s.Velocity.Z) &&
        float.IsFinite(s.AngularVelocity.X) && float.IsFinite(s.AngularVelocity.Y) && float.IsFinite(s.AngularVelocity.Z) &&
        float.IsFinite(s.Rotation.X) && float.IsFinite(s.Rotation.Y) && float.IsFinite(s.Rotation.Z) && float.IsFinite(s.Rotation.W) &&
        s.Position.Length() < 1e5f && s.Velocity.Length() < 1e4f && s.AngularVelocity.Length() < 1e3f;

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
            if (!Alive(body)) { _assigned.Remove(id); _lastScan = DateTime.MinValue; continue; }   // destroyed: rescan next tick
            if (!st.InVehicle || !Finite(st)) { Hold(body); continue; }   // on foot (or a broken state): stays where it was   // on foot (not shown yet): the vehicle stays where they got out
            _held.Remove(body);
            Drive(body, st);
            shown++;
        }
        foreach (var b in _free) if (Alive(b)) Park(b);
        PostDamage();
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
        if (!float.IsFinite(cur.X) || !float.IsFinite(cur.Y) || !float.IsFinite(cur.Z))
        {
            // the puppet's own physics went bad: put it back in one piece at the target (absolute, a delta would stay NaN)
            foreach (uint o in new uint[] { 0, 0x10, 0x20 }) _x.WV3(body + o, target + new Vector3(0, 0.5f, 0));
            _x.WV3(body + 0x90, Vector3.Zero); _x.WV3(body + 0xA0, Vector3.Zero);
            return;
        }
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

    // ------------------------------------------------------------------ damage between players
    // Exe mod coop-remote-damage: puppets take no collision damage (their player takes those collisions in their own game) but
    // do take weapon hits, which are sent to their player; a mailbox lets NB Multiplayer apply that damage to the local
    // vehicle through the game's own block damage (blocks break off naturally).

    public const uint Mailbox = 0x82FBCB00, CaveStart = 0x82D09230, CaveFirstWord = 0x3D6082FC;
    const uint HitCount = Mailbox + 0x40, HitRing = Mailbox + 0x50;
    bool? _damageMod;
    uint _hitsRead = uint.MaxValue;
    float _pendingDamage;
    Vector3 _pendingFrom;

    IEnumerable<uint> BlocksOf(uint veh)
    {
        uint lo = _x.U32(veh + 0x1488), hi = _x.U32(veh + 0x148C);
        if (hi < lo || hi - lo > 0xB0 * 2000) yield break;
        for (uint e = lo; e < hi; e += 0xB0) yield return e;
    }

    /// <summary>
    /// Damage each remote player's puppet took since the last call: the exe mod logs every hit on a puppet (weapons,
    /// explosions; collisions are left to the players' own games) in a ring instead of applying it.
    /// </summary>
    /// <summary>The "other material" of the last logged puppet hit (diagnostics; 0xFFFFFFFF = explosion).</summary>
    public uint LastHitMaterial { get; private set; }

    public Dictionary<long, float> TakePuppetDamage()
    {
        var res = new Dictionary<long, float>();
        if (!HasDamageMod) return res;
        uint count = _x.U32(HitCount);
        if (_hitsRead == uint.MaxValue) _hitsRead = count;              // first look: older hits are not ours to send
        else if (count - _hitsRead > 8) _hitsRead = count - 8;            // fell behind: the ring keeps the last 8
        var ring = _hitsRead == count ? Array.Empty<byte>() : _x.Read(HitRing, 8 * 16);
        var byVeh = _assigned.Where(kv => _vehOf.ContainsKey(kv.Value)).ToDictionary(kv => _vehOf[kv.Value], kv => kv.Key);
        for (uint i = _hitsRead; i != count && ring.Length == 128; i++)
        {
            int o = (int)(i & 7) * 16;                                    // vehicle, damage, other material (-1: explosion)
            uint veh = BE.U32(ring, o);
            float dmg = BE.F32(ring, o + 4);
            LastHitMaterial = BE.U32(ring, o + 8);
            // collisions are already real in both games (each player's own vehicle hits the other's puppet there): the
            // world (material 0) and vehicle blocks (2) are not sent; weapons (laser 0x2F, projectiles) and explosions (-1) are
            if (LastHitMaterial is 0 or 2) continue;
            if (byVeh.TryGetValue(veh, out var id) && dmg > 0 && dmg < 1e6f) res[id] = res.GetValueOrDefault(id) + dmg;
        }
        _hitsRead = count;
        return res;
    }

    // (No writes into puppet blocks: Change Vehicle frees every vehicle, and a write into a freed block corrupted the game's
    // memory in testing. The exe mod keeps puppets intact by logging their hits instead of applying them.)

    bool HasDamageMod => _damageMod ??= _x.U32(CaveStart) == CaveFirstWord;

    /// <summary>Damage another player dealt to the local vehicle (from <paramref name="from"/>: their position).</summary>
    public void QueueDamage(float amount, Vector3 from) { _pendingDamage += amount; _pendingFrom = from; }

    public float DamageTaken { get; private set; }

    /// <summary>Hands pending damage to the game: the block of the local vehicle nearest the attacker, through the mailbox.</summary>
    void PostDamage()
    {
        if (_pendingDamage <= 0 || _localVeh == 0) return;
        if (!HasDamageMod) { _pendingDamage = 0; return; }
        if (_x.U32(Mailbox) != _x.U32(Mailbox + 4)) return;          // the game has not taken the last one yet
        if (_x.U32(_localVeh) != VehicleVtable) return;
        uint bestBlock = 0; Vector3 bestPos = default; float best = float.MaxValue;
        foreach (var e in BlocksOf(_localVeh))
        {
            uint b = _x.U32(e + 4);
            if (b < 0x40000000) continue;
            var pos = _x.V3(e + 0x50);
            float d = Vector3.DistanceSquared(pos, _pendingFrom);
            if (d < best) { best = d; bestBlock = b; bestPos = pos; }
        }
        if (bestBlock == 0) return;
        var mb = new byte[0x30];
        BE.W32(mb, 0x08, _localVeh); BE.W32(mb, 0x0C, bestBlock); BE.WF32(mb, 0x10, _pendingDamage);
        BE.WF32(mb, 0x20, bestPos.X); BE.WF32(mb, 0x24, bestPos.Y); BE.WF32(mb, 0x28, bestPos.Z); BE.WF32(mb, 0x2C, 1f);
        _x.Write(Mailbox + 8, mb[8..]);                                  // the request itself, then its sequence number
        var seq = new byte[4]; BE.W32(seq, 0, _x.U32(Mailbox) + 1);
        _x.Write(Mailbox, seq);
        DamageTaken += _pendingDamage;
        _pendingDamage = 0;
    }

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
