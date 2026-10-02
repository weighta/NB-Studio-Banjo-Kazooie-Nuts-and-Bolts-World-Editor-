using System.Numerics;
using NB.Core.IO;

namespace NB.Core.Live;

/// <summary>One shot a player's vehicle fired: projectile objparams id, launch point relative to the shooter vehicle's
/// rigid body IN THE VEHICLE'S OWN FRAME (so a puppet turned slightly differently still fires from its muzzle), launch
/// velocity and orientation (world: the aim), the shooter's shot number and the player its projectile homes on (0 = none).</summary>
public readonly record struct CoopShot(uint Projectile, Vector3 Offset, Vector3 Velocity, Quaternion Rotation, byte Id = 0, long Target = 0);

/// <summary>A homing projectile already flying locked onto another target (shooter's shot number, player id; 0 = none).</summary>
public readonly record struct CoopRetarget(byte Id, long Target);

/// <summary>
/// Weapons fire in co-op (coop/research/projectiles/REPORT.txt). Every weapon block (descriptor 0x82FB5688) owns a pool
/// of up to 30 projectile objects at block+0x700; a shot is a pool object whose state +0xAC0 goes from 0 to non-zero
/// (launch point +0xB00, velocity +0xB30, orientation +0xB0, projectile objparams id = block+0x480). The local player's
/// shots are read that way and sent; other players' shots are replayed from their puppet with the exe mod
/// coop-projectiles (mailbox 0x82FBCAA0: LAUNCH a projectile with the exact position, velocity and orientation), so
/// torpedoes, eggs, grenades and lasers fly in every game. Their damage is not doubled: the real shot in the shooter's
/// game is what hits (forwarded by coop-remote-damage); replays are made harmless (damage types -2) and only seen.
/// </summary>
public sealed class CoopProjectiles
{
    public const uint WeaponVtable = 0x82FB5688, VehicleVtable = 0x82FB7F78;
    /// <summary>Class descriptor of torpedile-class projectiles (torpedo, grenade egg gun): the homing ones.</summary>
    public const uint HomingClass = 0x82FB79C0;
    /// <summary>Laser objparams id: a beam, not a flying body (never moved ahead).</summary>
    public const uint LaserId = 0x1FBEB58D;
    const uint Mailbox = 0x82FBCAA0, Hook = 0x82270568, HookWord = 0x48AD76F8, Cave = 0x82D47C60, CaveFirstWord = 0x3D6082FC;
    // homing (torpedile update 0x8243D490 / steering 0x8243DB20): +0xC58 target = a targetable object ([T+0] = its avatar;
    // a player's or AI driver's is [avatar+0xA80]) whose position +0xD0 the projectile turns to; +0xD48 re-acquire timer
    // (when it runs out with no target the projectile picks one by itself, 0x8243DDF0; 0 = never). Launch (0x82434B80)
    // takes the target from its stack argument [sp+0x54]: the mailbox LAUNCH passes 0.
    const uint TargetOff = 0xC58, AcquireTimerOff = 0xD48;
    readonly XeniaLive _x;
    public CoopProjectiles(XeniaLive x) { _x = x; }

    static bool Ptr(uint a) => a is >= 0x40000000 and < 0xA0000000;
    bool? _mod;
    public bool HasMod => _mod ??= _x.U32(Hook) == HookWord && _x.U32(Cave) == CaveFirstWord;

    static Quaternion QAt(byte[] b, int o) => b.Length >= o + 16 ? new Quaternion(BE.F32(b, o), BE.F32(b, o + 4), BE.F32(b, o + 8), BE.F32(b, o + 12)) : Quaternion.Identity;

    /// <summary>A vehicle's rigid body: position, orientation, velocity ([veh+0x7C0]+0x110 motion state).</summary>
    (Vector3 Pos, Quaternion Rot, Vector3 Vel) BodyOf(uint veh)
    {
        uint raw = _x.U32(veh + 0x7C0);
        var none = (_x.V3(veh + 0x50), Quaternion.Identity, Vector3.Zero);
        if (!Ptr(raw)) return none;
        var b = _x.Read(raw + 0x110, 0xA0);
        if (b.Length < 0xA0) return none;
        var q = QAt(b, 0x40);
        float l = q.LengthSquared();
        if (!float.IsFinite(l) || l < 0.5f) q = Quaternion.Identity;
        var p = new Vector3(BE.F32(b, 0), BE.F32(b, 4), BE.F32(b, 8));
        var v = new Vector3(BE.F32(b, 0x90), BE.F32(b, 0x94), BE.F32(b, 0x98));
        if (!float.IsFinite(p.X + p.Y + p.Z)) return none;
        return (p, Quaternion.Normalize(q), float.IsFinite(v.X + v.Y + v.Z) ? v : Vector3.Zero);
    }

    // ------------------------------------------------------------------ the local player's shots
    uint _veh;
    DateTime _poolsAt;
    readonly List<(uint Block, uint Projectile, uint[] Pool)> _pools = new();
    readonly Dictionary<uint, uint> _state = new();
    readonly Dictionary<uint, (byte Id, long Target)> _homing = new();   // local homing shots in flight
    byte _shotId;
    /// <summary>Retargets found by the last <see cref="ReadShots"/> (homing shots that locked onto someone else).</summary>
    public List<CoopRetarget> Retargets { get; } = new();

    /// <summary>Shots the local vehicle fired since the last call. <paramref name="playerOfTarget"/> names the player a
    /// homing projectile's target object belongs to (0 = none / not a player).</summary>
    public List<CoopShot> ReadShots(uint veh, Func<uint, long>? playerOfTarget = null)
    {
        var shots = new List<CoopShot>();
        Retargets.Clear();
        if (!Ptr(veh) || _x.U32(veh) != VehicleVtable) { _veh = 0; _pools.Clear(); _state.Clear(); _homing.Clear(); return shots; }
        if (veh != _veh || DateTime.UtcNow - _poolsAt > TimeSpan.FromSeconds(2))
        {
            // weapon blocks and their pools (pools are built once; parts breaking off change the block list)
            bool fresh = veh != _veh;
            _veh = veh; _poolsAt = DateTime.UtcNow; _pools.Clear();
            uint lo = _x.U32(veh + 0x1488), hi = _x.U32(veh + 0x148C);
            if (Ptr(lo) && hi >= lo && hi - lo <= 0xB0u * CoopDesign.MaxBlocks)
            {
                var raw = _x.Read(lo, (int)(hi - lo));
                for (int o = 0; o + 0xB0 <= raw.Length; o += 0xB0)
                {
                    uint b = BE.U32(raw, o + 4);
                    if (!Ptr(b) || _x.U32(b) != WeaponVtable) continue;
                    var pr = _x.Read(b + 0x700, 120);
                    if (pr.Length < 120) continue;
                    var pool = Enumerable.Range(0, 30).Select(i => BE.U32(pr, 4 * i)).Where(Ptr).ToArray();
                    if (pool.Length > 0) _pools.Add((b, _x.U32(b + 0x480), pool));
                }
            }
            if (fresh) { _state.Clear(); _homing.Clear(); foreach (var p in _pools.SelectMany(t => t.Pool)) _state[p] = _x.U32(p + 0xAC0); }   // shots already flying are not new
        }
        if (_pools.Count == 0) return shots;
        var (vp, vq, _) = BodyOf(veh);
        var inv = Quaternion.Conjugate(vq);
        foreach (var (_, proj, pool) in _pools)
            foreach (var p in pool)
            {
                uint s = _x.U32(p + 0xAC0);
                if (_state.TryGetValue(p, out var old) && old == 0 && s != 0)
                {
                    var b = _x.Read(p + 0xB0, 16); var lp = _x.V3(p + 0xB00); var vel = _x.V3(p + 0xB30);
                    var q = QAt(b, 0);
                    var off = Vector3.Transform(lp - vp, inv);                 // in the vehicle's own frame
                    if (float.IsFinite(off.X + off.Y + off.Z + vel.X + vel.Y + vel.Z + q.X + q.Y + q.Z + q.W) && off.Length() < 50 && vel.Length() < 2000)
                    {
                        byte id = ++_shotId;
                        long target = 0;
                        if (_x.U32(p) == HomingClass)
                        {
                            uint t = _x.U32(p + TargetOff);
                            target = t != 0 && playerOfTarget != null ? playerOfTarget(t) : 0;
                            _homing[p] = (id, target);
                        }
                        shots.Add(new CoopShot(proj, off, vel, q, id, target));
                    }
                }
                else if (s is 1 or 2 && _homing.TryGetValue(p, out var h))
                {
                    // a homing shot in flight locked onto a target (they acquire one a frame after launch and again
                    // 0.25 s after losing it): tell the others which player it is after now
                    uint t = _x.U32(p + TargetOff);
                    long target = t != 0 && playerOfTarget != null ? playerOfTarget(t) : 0;
                    if (target != h.Target) { _homing[p] = (h.Id, target); Retargets.Add(new CoopRetarget(h.Id, target)); }
                }
                else if (s is 0 or >= 3) _homing.Remove(p);
                _state[p] = s;
            }
        return shots;
    }

    public const int ShotSize = 46;

    /// <summary>Shots packet body: u8 count, then per shot u32 projectile, 3 f32 offset (vehicle frame), 3 f32 velocity,
    /// 4 i16 quaternion, u8 shot number, u8 0, i64 target player; then u8 retarget count, per retarget u8 shot number,
    /// i64 target player.</summary>
    public static byte[] Write(IReadOnlyList<CoopShot> shots, IReadOnlyList<CoopRetarget>? retargets = null)
    {
        int n = Math.Min(shots.Count, 24), r = Math.Min(retargets?.Count ?? 0, 32);
        var b = new byte[1 + ShotSize * n + 1 + 9 * r];
        b[0] = (byte)n;
        int o = 1;
        static ushort S(float v) => (ushort)(short)Math.Clamp(MathF.Round(v * 32767), -32767, 32767);
        foreach (var s in shots.Take(n))
        {
            BE.W32(b, o, s.Projectile);
            BE.WF32(b, o + 4, s.Offset.X); BE.WF32(b, o + 8, s.Offset.Y); BE.WF32(b, o + 12, s.Offset.Z);
            BE.WF32(b, o + 16, s.Velocity.X); BE.WF32(b, o + 20, s.Velocity.Y); BE.WF32(b, o + 24, s.Velocity.Z);
            BE.W16(b, o + 28, S(s.Rotation.X)); BE.W16(b, o + 30, S(s.Rotation.Y)); BE.W16(b, o + 32, S(s.Rotation.Z)); BE.W16(b, o + 34, S(s.Rotation.W));
            b[o + 36] = s.Id; BE.W64(b, o + 38, (ulong)s.Target);
            o += ShotSize;
        }
        b[o++] = (byte)r;
        if (retargets != null)
            foreach (var t in retargets.Take(r)) { b[o] = t.Id; BE.W64(b, o + 1, (ulong)t.Target); o += 9; }
        return b;
    }

    public static (List<CoopShot> Shots, List<CoopRetarget> Retargets) Read(byte[] b, int at)
    {
        var res = new List<CoopShot>(); var rt = new List<CoopRetarget>();
        if (b.Length < at + 1) return (res, rt);
        int n = b[at], o = at + 1;
        for (int i = 0; i < n && o + ShotSize <= b.Length; i++, o += ShotSize)
        {
            int k0 = o;
            float Q(int k) => (short)BE.U16(b, k0 + k) / 32767f;
            var q = new Quaternion(Q(28), Q(30), Q(32), Q(34));
            var s = new CoopShot(BE.U32(b, o), new(BE.F32(b, o + 4), BE.F32(b, o + 8), BE.F32(b, o + 12)),
                new(BE.F32(b, o + 16), BE.F32(b, o + 20), BE.F32(b, o + 24)), q.LengthSquared() > 0.5f ? Quaternion.Normalize(q) : Quaternion.Identity,
                b[o + 36], (long)BE.U64(b, o + 38));
            if (float.IsFinite(s.Offset.X + s.Offset.Y + s.Offset.Z + s.Velocity.X + s.Velocity.Y + s.Velocity.Z) && s.Offset.Length() < 50 && s.Velocity.Length() < 2000)
                res.Add(s);
        }
        if (n * ShotSize + at + 1 < b.Length && o < b.Length)
        {
            int r = b[o++];
            for (int i = 0; i < r && o + 9 <= b.Length; i++, o += 9) rt.Add(new CoopRetarget(b[o], (long)BE.U64(b, o + 1)));
        }
        return (res, rt);
    }

    // ------------------------------------------------------------------ other players' shots, replayed from their puppet
    // The puppet is rebuilt from the shooter's own design (coop-remote-vehicle), so it has the same weapon blocks and the
    // game built their projectile pools: a shot is replayed by launching an idle projectile of the puppet's weapon with
    // the same projectile id (no new objects: creating projectiles mid-game froze the test game). A puppet without that
    // weapon (design not rebuilt yet) skips the shot.
    // Where and when: the puppet is driven to where the shooter is NOW (its state predicted ahead by the network age), so
    // the shot leaves the puppet's muzzle (the offset turned with the puppet) already as far along as it has flown since
    // it was fired (age, at most 0.25 s; lasers are beams and are not moved).
    // Homing: a torpedo replica homes on what the shooter's torpedo homes on - the target player's own vehicle in that
    // player's game, their puppet elsewhere - and never picks a target of its own (it picked whatever was nearest in this
    // game, or nothing: "aiming at two different places", "not locking on").
    readonly Queue<(uint Puppet, long Owner, CoopShot Shot, DateTime At, float Age)> _queue = new();
    readonly Queue<(long Owner, CoopRetarget R, DateTime At)> _retargets = new();
    readonly Dictionary<uint, (DateTime At, Dictionary<uint, uint[]> Pools)> _puppetPools = new();
    readonly Dictionary<(long Owner, byte Id), (uint Proj, uint Puppet)> _replicas = new();
    (uint Puppet, uint Proj, long Owner, CoopShot Shot)? _op;
    uint _opSeq;
    public int Replayed { get; private set; }
    public int Skipped { get; private set; }
    /// <summary>Diagnostics: replicas launched with the shooter's target / retargeted in flight.</summary>
    public int Locked { get; private set; }
    public int Retargeted { get; private set; }
    const uint Harmless = 0xFFFFFFFE;   // damage type -2 = "no damage"
    public const float MaxAdvance = 0.25f;

    /// <summary>Queues another player's shots, fired from their puppet <paramref name="puppet"/>; <paramref name="age"/> =
    /// seconds since they were fired (network delay so far).</summary>
    public void Replay(uint puppet, long owner, IEnumerable<CoopShot> shots, float age)
    {
        if (!HasMod) return;
        foreach (var s in shots) if (_queue.Count < 64) _queue.Enqueue((puppet, owner, s, DateTime.UtcNow, age));
    }

    /// <summary>Homing shots of <paramref name="owner"/> that changed target.</summary>
    public void Retarget(long owner, IEnumerable<CoopRetarget> rts)
    {
        if (!HasMod) return;
        foreach (var r in rts) if (_retargets.Count < 64) _retargets.Enqueue((owner, r, DateTime.UtcNow));
    }

    /// <summary>Projectile id -> pool of the puppet's weapon blocks (re-read every 2 s: pools are built lazily).</summary>
    Dictionary<uint, uint[]> PoolsOf(uint puppet)
    {
        if (_puppetPools.TryGetValue(puppet, out var c) && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(2)) return c.Pools;
        var pools = new Dictionary<uint, uint[]>();
        uint lo = _x.U32(puppet + 0x1488), hi = _x.U32(puppet + 0x148C);
        if (Ptr(lo) && hi >= lo && hi - lo <= 0xB0u * CoopDesign.MaxBlocks)
        {
            var raw = _x.Read(lo, (int)(hi - lo));
            for (int o = 0; o + 0xB0 <= raw.Length; o += 0xB0)
            {
                uint b = BE.U32(raw, o + 4);
                if (!Ptr(b) || _x.U32(b) != WeaponVtable) continue;
                var pr = _x.Read(b + 0x700, 120);
                if (pr.Length < 120) continue;
                uint id = _x.U32(b + 0x480);
                var pool = Enumerable.Range(0, 30).Select(i => BE.U32(pr, 4 * i)).Where(Ptr);
                pools[id] = (pools.TryGetValue(id, out var have) ? have.Concat(pool) : pool).ToArray();
            }
        }
        _puppetPools[puppet] = (DateTime.UtcNow, pools);
        return pools;
    }

    /// <summary>One mailbox step per call (the game runs one request per frame). <paramref name="targetObject"/> = the
    /// targetable object of a player in this game (0 = none).</summary>
    public void Pump(Func<uint, bool> puppetAlive, Func<long, uint>? targetObject = null)
    {
        if (!HasMod) return;
        ApplyRetargets(puppetAlive, targetObject);
        if (_op is { } op)
        {
            if (_x.U32(Mailbox + 4) != _opSeq) return;                 // the cave writes "done" after the result
            if (_x.U32(Mailbox + 0x14) != 0)
            {
                Replayed++;
                if (_x.U32(op.Proj) == HomingClass && puppetAlive(op.Puppet))
                {
                    SetTarget(op.Proj, op.Shot.Target != 0 && targetObject != null ? targetObject(op.Shot.Target) : 0);
                    _replicas[(op.Owner, op.Shot.Id)] = (op.Proj, op.Puppet);
                    if (op.Shot.Target != 0) Locked++;
                }
            }
            else Skipped++;
            _op = null;
        }
        if (_x.U32(Mailbox) != _x.U32(Mailbox + 4)) return;
        while (_queue.Count > 0)
        {
            var (pup, owner, shot, at, age0) = _queue.Dequeue();
            if (DateTime.UtcNow - at > TimeSpan.FromSeconds(0.5) || !puppetAlive(pup)) { Skipped++; continue; }   // too late / puppet gone
            uint idle = PoolsOf(pup).TryGetValue(shot.Projectile, out var pool) ? pool.FirstOrDefault(p => _x.U32(p + 0xAC0) == 0) : 0;
            if (idle == 0) { Skipped++; continue; }                     // no such weapon on the puppet, or all in flight
            // seen, not felt: the real shot in the shooter's game does the damage (forwarded by coop-remote-damage)
            foreach (uint off in new uint[] { 0x448, 0xBB4, 0xC24 }) if (_x.U32(idle + off) != Harmless) W32(idle + off, Harmless);
            var (pp, pq, pv) = BodyOf(pup);
            float age = Math.Clamp(age0 + (float)(DateTime.UtcNow - at).TotalSeconds, 0f, MaxAdvance);
            var pos = pp + Vector3.Transform(shot.Offset, pq);
            if (shot.Projectile != LaserId) pos += (shot.Velocity - pv) * age;
            Post(2, pup, idle, pos, shot.Velocity, shot.Rotation);
            _op = (pup, idle, owner, shot);
            return;
        }
    }

    void SetTarget(uint proj, uint target)
    {
        W32(proj + TargetOff, target);
        W32(proj + AcquireTimerOff, 0);                                  // never pick a target of its own
    }

    void ApplyRetargets(Func<uint, bool> puppetAlive, Func<long, uint>? targetObject)
    {
        // a retarget can arrive before its shot was launched here: it waits up to 0.5 s
        int n = _retargets.Count;
        for (int i = 0; i < n; i++)
        {
            var (owner, r, at) = _retargets.Dequeue();
            if (!_replicas.TryGetValue((owner, r.Id), out var rep))
            {
                if (DateTime.UtcNow - at < TimeSpan.FromSeconds(0.5)) _retargets.Enqueue((owner, r, at));
                continue;
            }
            if (!puppetAlive(rep.Puppet) || _x.U32(rep.Proj) != HomingClass || _x.U32(rep.Proj + 0xAC0) is not (1 or 2)) { _replicas.Remove((owner, r.Id)); continue; }
            SetTarget(rep.Proj, r.Target != 0 && targetObject != null ? targetObject(r.Target) : 0);
            Retargeted++;
        }
        if (_replicas.Count > 64)
            foreach (var k in _replicas.Where(kv => _x.U32(kv.Value.Proj + 0xAC0) is not (1 or 2)).Select(kv => kv.Key).ToList()) _replicas.Remove(k);
    }

    void W32(uint a, uint v) { var b = new byte[4]; BE.W32(b, 0, v); _x.Write(a, b); }

    void Post(uint cmd, uint owner, uint arg, Vector3 pos, Vector3 vel, Quaternion q)
    {
        var a = new byte[12]; BE.W32(a, 0, cmd); BE.W32(a, 4, owner); BE.W32(a, 8, arg);
        _x.Write(Mailbox + 8, a);
        var z = new byte[8]; _x.Write(Mailbox + 0x14, z);                          // result, flags
        float sp = vel.Length(); var dir = sp > 1e-4f ? vel / sp : Vector3.UnitZ;
        var v = new byte[64];
        float[] f = { pos.X, pos.Y, pos.Z, 1, vel.X, vel.Y, vel.Z, 0, q.X, q.Y, q.Z, q.W, dir.X, dir.Y, dir.Z, 0 };
        for (int i = 0; i < 16; i++) BE.WF32(v, 4 * i, f[i]);
        _x.Write(Mailbox + 0x20, v);
        _opSeq = _x.U32(Mailbox) + 1;
        var s = new byte[4]; BE.W32(s, 0, _opSeq);
        _x.Write(Mailbox, s);
    }

    /// <summary>Forgets the pools of puppets that no longer exist (Change Vehicle and rebuilds free every vehicle and its blocks).</summary>
    public void Forget(Func<uint, bool> puppetAlive)
    {
        foreach (var k in _puppetPools.Keys.Where(k => !puppetAlive(k)).ToList()) _puppetPools.Remove(k);
        foreach (var k in _replicas.Where(kv => !puppetAlive(kv.Value.Puppet)).Select(kv => kv.Key).ToList()) _replicas.Remove(k);
    }
}
