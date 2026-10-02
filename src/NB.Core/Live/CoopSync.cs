using System.Numerics;
using NB.Core.IO;

namespace NB.Core.Live;

/// <summary>Where a co-op player is.</summary>
public enum CoopMode : byte
{
    /// <summary>Not in Showdown Town (title screen, loading, another world, the garage) or the game is not running.</summary>
    Absent = 0,
    Vehicle = 1,
    OnFoot = 2,
    /// <summary>Choosing another vehicle (Change Vehicle): hidden from everyone until done.</summary>
    Building = 3,
    /// <summary>(Protocol 2 senders use the Menu / Photo flags instead.)</summary>
    Paused = 4,
}

/// <summary>Extra details of a co-op state (shown in the room panel).</summary>
[Flags]
public enum CoopFlags : byte { None = 0, Menu = 1, Photo = 2 }

/// <summary>One player's state, as sent 30 times a second in Showdown Town co-op (protocol 3).</summary>
public struct CoopState
{
    public CoopMode Mode;
    public CoopFlags Flags;
    public Vector3 Position, Velocity, AngularVelocity;
    public Quaternion Rotation;
    public uint Blueprint;
    /// <summary>Sender's packet counter (older packets that arrive late are dropped).</summary>
    public uint Seq;
    /// <summary>The sender's one-way delay to the host in milliseconds (0 for the host): receivers add their own and
    /// predict the position that far ahead.</summary>
    public ushort DelayMs;
    /// <summary>Hash of the vehicle design the player drives (<see cref="CoopDesign"/>; 0 = not known / no exe mod).</summary>
    public uint Design;

    public bool InVehicle => Mode == CoopMode.Vehicle;
    /// <summary>In Showdown Town and visible to the others.</summary>
    public bool Shown => Mode is CoopMode.Vehicle or CoopMode.OnFoot or CoopMode.Paused;

    public const int Size = 4 + 13 * 4 + 4 + 4 + 4;
    public byte[] Write()
    {
        var b = new byte[Size];
        b[0] = (byte)Mode; b[1] = (byte)Flags;
        BE.W16(b, 2, DelayMs);
        int o = 4;
        foreach (var f in new[] { Position.X, Position.Y, Position.Z, Rotation.X, Rotation.Y, Rotation.Z, Rotation.W,
                                  Velocity.X, Velocity.Y, Velocity.Z, AngularVelocity.X, AngularVelocity.Y, AngularVelocity.Z })
        { BE.WF32(b, o, f); o += 4; }
        BE.W32(b, o, Blueprint); BE.W32(b, o + 4, Seq); BE.W32(b, o + 8, Design);
        return b;
    }
    public static CoopState Read(byte[] b)
    {
        float F(int i) => BE.F32(b, 4 + 4 * i);
        return new CoopState
        {
            Mode = b[0] <= 4 ? (CoopMode)b[0] : CoopMode.Absent, Flags = (CoopFlags)(b[1] & 3), DelayMs = BE.U16(b, 2),
            Position = new(F(0), F(1), F(2)), Rotation = new(F(3), F(4), F(5), F(6)),
            Velocity = new(F(7), F(8), F(9)), AngularVelocity = new(F(10), F(11), F(12)),
            Blueprint = BE.U32(b, 4 + 13 * 4), Seq = BE.U32(b, 4 + 14 * 4), Design = BE.U32(b, 4 + 15 * 4),
        };
    }
}

/// <summary>A remote player's latest state and how old it is (seconds since it left that player's game), with the
/// vehicle design and damage they last sent (null = not received yet).</summary>
public readonly record struct CoopRemote(CoopState State, float AgeSeconds, CoopDesign? Design = null, CoopDamage? Damage = null);

/// <summary>
/// Showdown Town co-op inside one running game: reads the local player's vehicle and drives "puppet" vehicles (AI
/// vehicles of the co-op edition) to where the other players are. Verified layout (docs: nb-town-coop research):
/// vehicle objects have vtable 0x82FB7F78, blueprint id +0x18A4, spawn link +0x8B8, position +0x50; a whole vehicle is one
/// Havok rigid body, [vehicle+0x7C0]; its motion state position is at body+0x110 with the 3x3 rotation (columns) 0x30
/// before it, centres of mass +0x10/+0x20, quaternions (x,y,z,w) +0x30/+0x40, linear velocity +0x90 and angular velocity +0xA0.
/// Puppets are steered by velocity (target velocity + 4 × position error), so collisions with them behave physically.
/// </summary>
public sealed partial class CoopSync
{
    public const uint VehicleVtable = 0x82FB7F78;
    readonly XeniaLive _x;
    readonly uint _puppetBlueprint;
    readonly Vector3 _parkSpot;
    List<uint> _vehicles = new();                         // puppet vehicle objects
    DateTime _lastScan = DateTime.MinValue;
    uint _localBody, _localVeh;
    // remote player id -> puppet vehicle object (not its body: a part breaking off gives the vehicle a new body)
    readonly Dictionary<long, uint> _assigned = new();

    public int PuppetCount { get; private set; }
    public string Status { get; private set; } = "";

    /// <param name="parkSpot">Where unused puppets wait (out of sight).</param>
    public CoopSync(XeniaLive x, uint puppetBlueprint, Vector3 parkSpot) { _x = x; _puppetBlueprint = puppetBlueprint; _parkSpot = parkSpot; }

    static bool Ptr(uint a) => a is >= 0x40000000 and < 0xA0000000;
    static uint BodyOf(XeniaLive x, uint veh) { uint b = x.U32(veh + 0x7C0); return Ptr(b) ? b + 0x110 : 0; }

    // Game state (coop/research/state/REPORT.txt): L = [0x82FAC7AC] the running level; [L+0] its script id;
    // [L+0x58] = 1 in town; the Banjo avatar A = [L+0xA44] (vtable 0x82FB7EDC) and his vehicle [A+0xC3C] (0 on foot)
    public const uint AvatarVtable = 0x82FB7EDC;
    /// <summary>Script ids of Showdown Town's four times of day (the running level, [L+0]).</summary>
    static readonly HashSet<uint> TownLevels = new() { 0x1901D1B6, 0x19E00470, 0x196BD4A7, 0x193F0052 };
    const uint LoadingScene = 0x82FACBA4, LoadingTips = 0x005D0000;

    void Rescan(bool force = false)
    {
        if (!force && DateTime.UtcNow - _lastScan < TimeSpan.FromSeconds(3) && _vehicles.Count > 0) return;
        _lastScan = DateTime.UtcNow;
        // only the puppets need a scan (the player's own vehicle is a pointer away); freed vehicles keep their vtable and
        // blueprint but lose their level (+0x4C)
        // (a puppet the mailbox is rebuilding right now is kept: it is replaced when the rebuild returns)
        _vehicles = _x.FindU32(VehicleVtable).Where(v => _x.U32(v + 0x18A4) == _puppetBlueprint && _x.U32(v + 0x4C) != 0 && BodyOf(_x, v) != 0)
            .Union(_rebuilding).ToList();
        var puppets = _vehicles.ToHashSet();
        PuppetCount = puppets.Count;
        foreach (var k in _assigned.Where(kv => !puppets.Contains(kv.Value)).Select(kv => kv.Key).ToList()) _assigned.Remove(k);
        _free = puppets.Except(_assigned.Values).ToList();
        foreach (var v in _designOf.Keys.Where(v => !puppets.Contains(v)).ToList()) { _designOf.Remove(v); _applied.Remove(v); _blockMaps.Remove(v); }
        foreach (var v in _held.Keys.Where(v => !puppets.Contains(v)).ToList()) _held.Remove(v);
        foreach (var v in _hidden.Keys.Where(v => !puppets.Contains(v)).ToList()) _hidden.Remove(v);
        foreach (var k in _detachTries.Keys.Where(k => !puppets.Contains(k.Veh)).ToList()) _detachTries.Remove(k);
        foreach (var v in _quietUntil.Keys.Where(v => !puppets.Contains(v)).ToList()) _quietUntil.Remove(v);
    }
    List<uint> _free = new();

    /// <summary>
    /// Is the puppet still the live vehicle it was? Change Vehicle (and break-ups) destroy every vehicle in the level; the
    /// destroyed object loses its body pointer (+0x7C0). Writing into a destroyed body would corrupt the game's memory, so
    /// every write is checked first and a rescan picks up the new vehicles.
    /// </summary>
    bool Alive(uint veh) =>
        !_rebuilding.Contains(veh) && _x.U32(veh) == VehicleVtable && _x.U32(veh + 0x18A4) == _puppetBlueprint
        && _x.U32(veh + 0x4C) != 0 && BodyOf(_x, veh) != 0;

    /// <summary>Puppets the game is changing right now (rebuild, a part breaking off): no writes into them until it is done.</summary>
    bool Quiet(uint veh) => _rebuilding.Contains(veh) || (_quietUntil.TryGetValue(veh, out var t) && DateTime.UtcNow < t);

    Quaternion Q(uint a) { var b = _x.Read(a, 16); return b.Length == 16 ? new(BE.F32(b, 0), BE.F32(b, 4), BE.F32(b, 8), BE.F32(b, 12)) : Quaternion.Identity; }

    /// <summary>
    /// The local player's state: Absent unless this game is in Showdown Town gameplay (the running level is one of the
    /// town's four time-of-day scripts, no loading screen, Banjo exists). Elsewhere (title screen, house menus, loading,
    /// another world, Mumbo's garage) nothing is sent: on the title screen front-end vehicles drive around. The player's
    /// vehicle is the avatar's vehicle pointer (no guessing by distance); its body is read every time (a break-up gives
    /// the vehicle a new body).
    /// </summary>
    public CoopState ReadLocal()
    {
        uint level = _x.Player;
        _localVeh = 0; _localBody = 0;
        if (!Ptr(level) || !TownLevels.Contains(_x.U32(level)) || _x.U32(level + 0x58) != 1 || _x.U32(LoadingScene) == LoadingTips) return default;
        uint avatar = _x.U32(level + 0xA44);
        if (!Ptr(avatar) || _x.U32(avatar) != AvatarVtable) return default;
        Rescan();
        uint veh = _x.U32(avatar + 0xC3C);
        if (Ptr(veh) && _x.U32(veh) == VehicleVtable && _x.U32(veh + 0x4C) != 0) { _localVeh = veh; _localBody = BodyOf(_x, veh); }
        if (_localVeh != _localVehSeen) { _localVehSeen = _localVeh; _localVehSince = DateTime.UtcNow; }
        var foot = new CoopState { Mode = CoopMode.OnFoot, Position = _x.PlayerPosition, Rotation = Quaternion.Identity };
        if (_localBody == 0) return WithMenus(foot);
        var st = new CoopState
        {
            Mode = CoopMode.Vehicle, Position = _x.V3(_localBody), Rotation = Q(_localBody + 0x40),
            Velocity = _x.V3(_localBody + 0x90), AngularVelocity = _x.V3(_localBody + 0xA0),
        };
        st.Blueprint = _localVeh != 0 ? _x.U32(_localVeh + 0x18A4) : 0;
        st.Design = LocalDesign != null && _designVeh == _localVeh ? LocalDesign.Hash : 0;
        var res = Finite(st) ? st : Finite(foot) ? foot : default;
        return WithMenus(res);
    }

    // Menu scenes (coop/research/pause/REPORT.txt): handles are 0xFFFFFFFF while closed
    const uint PauseScene = 0x82E51BD0, ChangeVehicleScene = 0x82E51BD4;

    /// <summary>Change Vehicle open = building (hidden from the others); pause menu / photo mode = flags only (with the
    /// coop-world-runs exe mod the player's world keeps running, so their vehicle is still shown where it is).</summary>
    CoopState WithMenus(CoopState st)
    {
        if (st.Mode == CoopMode.Absent) return st;
        if (_x.U32(ChangeVehicleScene) != 0xFFFFFFFF) { st.Mode = CoopMode.Building; return st; }
        if (_x.U32(PauseScene) != 0xFFFFFFFF) st.Flags |= CoopFlags.Menu;
        uint cam = _x.U32(_x.Player + 0x15B0);
        if (cam is >= 0x40000000 and < 0xA0000000 && _x.U32(cam + 0x164) == 1) st.Flags |= CoopFlags.Photo;
        return st;
    }

    /// <summary>Only finite numbers ever reach a game: a NaN velocity written into a puppet would spread through its physics.</summary>
    static bool Finite(CoopState s) =>
        float.IsFinite(s.Position.X) && float.IsFinite(s.Position.Y) && float.IsFinite(s.Position.Z) &&
        float.IsFinite(s.Velocity.X) && float.IsFinite(s.Velocity.Y) && float.IsFinite(s.Velocity.Z) &&
        float.IsFinite(s.AngularVelocity.X) && float.IsFinite(s.AngularVelocity.Y) && float.IsFinite(s.AngularVelocity.Z) &&
        float.IsFinite(s.Rotation.X) && float.IsFinite(s.Rotation.Y) && float.IsFinite(s.Rotation.Z) && float.IsFinite(s.Rotation.W) &&
        s.Position.Length() < 1e5f && s.Velocity.Length() < 1e4f && s.AngularVelocity.Length() < 1e3f;

    /// <summary>
    /// Drives one puppet per remote player who is in town toward where that player is now (their last state, predicted
    /// ahead by its age); players who are absent or building have no puppet, and every puppet without a player is hidden.
    /// </summary>
    public void Apply(IReadOnlyDictionary<long, CoopRemote> remotes)
    {
        Rescan();
        foreach (var gone in _assigned.Keys.Where(k => !remotes.TryGetValue(k, out var r) || !r.State.Shown).ToList())
        {
            _free.Add(_assigned[gone]); _assigned.Remove(gone);
        }
        int shown = 0;
        foreach (var (id, r) in remotes.OrderBy(kv => kv.Key))
        {
            var st = r.State;
            if (!st.Shown) continue;
            if (!_assigned.TryGetValue(id, out var veh))
            {
                if (_free.Count == 0) continue;
                veh = _free[0]; _free.RemoveAt(0); _assigned[id] = veh;
                _hidden.Remove(veh);
                if (!Quiet(veh) && Alive(veh) && Finite(st)) Teleport(BodyOf(_x, veh), Apart(st.Position) + new Vector3(0, 0.5f, 0));   // appears where the player is (beside us if that is here)
            }
            if (Quiet(veh)) { shown++; continue; }                                  // being rebuilt / splitting: hands off
            if (!Alive(veh)) { _assigned.Remove(id); _lastScan = DateTime.MinValue; continue; }   // destroyed: rescan next tick
            uint body = BodyOf(_x, veh);
            // the player's new vehicle design is on its way (or being built here): keep the old one out of sight meanwhile
            if (WaitingForDesign(id, veh, r)) { Hide(veh, body); continue; }
            SetHidden(veh, false);
            _hidden.Remove(veh);
            if (st.Mode != CoopMode.Vehicle || !Finite(st)) { Hold(veh, body); shown++; continue; }   // on foot / paused: the vehicle stands
            _held.Remove(veh);
            Drive(body, st, Math.Clamp(r.AgeSeconds, 0f, MaxLead));
            shown++;
        }
        foreach (var v in _free) if (!Quiet(v) && Alive(v)) Hide(v, BodyOf(_x, v));
        PostDamage();
        PumpVehicles(remotes);
        Status = $"{shown} player(s) shown, {PuppetCount} puppet vehicle(s) available";
    }

    /// <summary>The furthest ahead a remote state is predicted (seconds): beyond that a guess is worse than a late position.</summary>
    public const float MaxLead = 0.35f;

    void Teleport(uint body, Vector3 target)
    {
        var d = target - _x.V3(body);
        foreach (uint o in new uint[] { 0, 0x10, 0x20 }) _x.WV3(body + o, _x.V3(body + o) + d);
    }

    void Drive(uint body, CoopState st, float lead)
    {
        var cur = _x.V3(body);
        // where the player is now: their last state is <lead> seconds old
        var target = st.Position + st.Velocity * lead;
        var rot = st.Rotation;
        if (lead > 0 && st.AngularVelocity.LengthSquared() > 1e-6f)
        {
            var w = st.AngularVelocity;
            var dq = new Quaternion(w.X, w.Y, w.Z, 0) * rot;
            rot = Quaternion.Normalize(new Quaternion(rot.X + 0.5f * lead * dq.X, rot.Y + 0.5f * lead * dq.Y, rot.Z + 0.5f * lead * dq.Z, rot.W + 0.5f * lead * dq.W));
        }
        if (!float.IsFinite(cur.X) || !float.IsFinite(cur.Y) || !float.IsFinite(cur.Z))
        {
            // the puppet's own physics went bad: put it back in one piece at the target (absolute, a delta would stay NaN)
            foreach (uint o in new uint[] { 0, 0x10, 0x20 }) _x.WV3(body + o, target + new Vector3(0, 0.5f, 0));
            _x.WV3(body + 0x90, Vector3.Zero); _x.WV3(body + 0xA0, Vector3.Zero);
            return;
        }
        target = Apart(target);
        var err = target - cur;
        if (err.Length() > 25) { Teleport(body, target + new Vector3(0, 0.5f, 0)); return; }
        var v = st.Velocity + 4f * err;
        if (v.Length() > 80) v = Vector3.Normalize(v) * 80;
        _x.WV3(body + 0x90, v);
        var qe = rot * Quaternion.Conjugate(Q(body + 0x40));
        if (qe.W < 0) qe = Quaternion.Negate(qe);
        float ang = 2 * MathF.Acos(Math.Min(1f, qe.W)), s = MathF.Sqrt(MathF.Max(1e-9f, 1 - qe.W * qe.W));
        var axis = ang > 1e-3f ? new Vector3(qe.X, qe.Y, qe.Z) / s : Vector3.Zero;
        _x.WV3(body + 0xA0, st.AngularVelocity + axis * MathF.Min(ang * 5f, 8f));
    }

    readonly Dictionary<uint, Vector3> _held = new();

    /// <summary>
    /// Players start on the same spot (the same save resumes at the same place) and drive through each other's position:
    /// a puppet target closer than 5 units to the local player is moved 5 units beside them, on the side it comes from,
    /// so a puppet never lands inside or on top of the local vehicle (that flips it).
    /// </summary>
    Vector3 Apart(Vector3 target)
    {
        var me = _localBody != 0 ? _x.V3(_localBody) : _x.PlayerPosition;
        if (!float.IsFinite(me.X) || me == Vector3.Zero) return target;
        var away = new Vector3(target.X - me.X, 0, target.Z - me.Z);
        if (away.Length() >= 5f) return target;
        away = away.Length() < 0.5f ? Vector3.UnitX : Vector3.Normalize(away);
        return new Vector3(me.X, Math.Max(me.Y, target.Y), me.Z) + away * 5f;
    }

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
    /// <summary>
    /// The room's time of day for the next Showdown Town load (exe mod coop-shared-time): 0 = the game's own random pick,
    /// 1..4 = morning, midday, afternoon, night. Written to the co-op mailbox word 0x82FBCB30.
    /// </summary>
    public void SetTimeOfDay(int phase)
    {
        if (_x.U32(TimeCave) != TimeCaveFirstWord) return;   // edition without the exe mod
        if (_x.U32(Mailbox + 0x30) == (uint)phase) return;
        var b = new byte[4]; BE.W32(b, 0, (uint)phase); _x.Write(Mailbox + 0x30, b);
    }
    const uint TimeCave = 0x82D21300, TimeCaveFirstWord = 0x3D6082FC;

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
        var byVeh = _assigned.ToDictionary(kv => kv.Value, kv => kv.Key);
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
    void Hold(uint veh, uint body)
    {
        var cur = _x.V3(body);
        if (!_held.TryGetValue(veh, out var spot)) _held[veh] = spot = cur;
        else if (Vector3.Distance(cur, spot) > 1.5f) Teleport(body, spot);
        var v = _x.V3(body + 0x90);
        _x.WV3(body + 0x90, new Vector3(0, Math.Min(v.Y, 0), 0));   // keep falling, so it settles on the ground
        _x.WV3(body + 0xA0, Vector3.Zero);
    }

    readonly Dictionary<uint, Vector3> _hidden = new();

    /// <summary>
    /// A puppet without a player (nobody there, or that player is changing vehicle, loading or elsewhere): invisible and
    /// out of everyone's way. The vehicle and its AI driver stop being drawn (+0x1C0 = 1; draw function 0x8224EE90), and its
    /// rigid body moves to collision layer 9, which only collides with the fixed town (layers 0, 1, 31): other vehicles
    /// and characters pass through it, its wheels still stand on the ground. Re-applied every tick (a break-up gives it a
    /// new body; new puppets after Change Vehicle start visible). It also stands still, so it is where it was.
    /// </summary>
    void Hide(uint veh, uint body)
    {
        SetHidden(veh, true);
        if (!_hidden.TryGetValue(veh, out var spot)) _hidden[veh] = spot = _x.V3(body);
        var cur = _x.V3(body);
        if (new Vector2(cur.X - spot.X, cur.Z - spot.Z).Length() > 1.5f) Teleport(body, spot);
        var v = _x.V3(body + 0x90);
        _x.WV3(body + 0x90, new Vector3(0, Math.Min(v.Y, 0), 0));
        _x.WV3(body + 0xA0, Vector3.Zero);
    }

    const uint DrawOff = 0x1C0, LayerMask = 0x3F, HiddenLayer = 9, VehicleLayer = 7;

    void SetHidden(uint veh, bool hidden)
    {
        void W32(uint a, uint v) { var b = new byte[4]; BE.W32(b, 0, v); _x.Write(a, b); }
        uint flag = hidden ? 1u : 0u;
        if (_x.U32(veh + DrawOff) != flag) W32(veh + DrawOff, flag);
        uint drv = _x.U32(veh + 0x48) - 0x40;                                 // the AI driver (Banjo) sitting in it
        if (Ptr(drv) && _x.U32(drv) == AvatarVtable && _x.U32(drv + 0xC3C) == veh && _x.U32(drv + DrawOff) != flag) W32(drv + DrawOff, flag);
        uint raw = _x.U32(veh + 0x7C0);
        if (Ptr(raw))
        {
            uint f = _x.U32(raw + 0x2C), want = (f & ~LayerMask) | (hidden ? HiddenLayer : VehicleLayer);
            if (f != want && (f & LayerMask) is HiddenLayer or VehicleLayer) W32(raw + 0x2C, want);
        }
    }


}
