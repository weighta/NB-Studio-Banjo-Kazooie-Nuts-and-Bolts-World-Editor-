using System.Numerics;
using NB.Core.IO;

namespace NB.Core.Live;

/// <summary>One shot a player's vehicle fired: projectile objparams id, launch point relative to the shooter vehicle's
/// position, launch velocity and orientation.</summary>
public readonly record struct CoopShot(uint Projectile, Vector3 Offset, Vector3 Velocity, Quaternion Rotation);

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
    const uint Mailbox = 0x82FBCAA0, Hook = 0x82270568, HookWord = 0x48AD76F8, Cave = 0x82D47C60, CaveFirstWord = 0x3D6082FC;
    readonly XeniaLive _x;
    public CoopProjectiles(XeniaLive x) { _x = x; }

    static bool Ptr(uint a) => a is >= 0x40000000 and < 0xA0000000;
    bool? _mod;
    public bool HasMod => _mod ??= _x.U32(Hook) == HookWord && _x.U32(Cave) == CaveFirstWord;

    // ------------------------------------------------------------------ the local player's shots
    uint _veh;
    DateTime _poolsAt;
    readonly List<(uint Block, uint Projectile, uint[] Pool)> _pools = new();
    readonly Dictionary<uint, uint> _state = new();

    /// <summary>Shots the local vehicle fired since the last call.</summary>
    public List<CoopShot> ReadShots(uint veh)
    {
        var shots = new List<CoopShot>();
        if (!Ptr(veh) || _x.U32(veh) != VehicleVtable) { _veh = 0; _pools.Clear(); _state.Clear(); return shots; }
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
            if (fresh) { _state.Clear(); foreach (var p in _pools.SelectMany(t => t.Pool)) _state[p] = _x.U32(p + 0xAC0); }   // shots already flying are not new
        }
        if (_pools.Count == 0) return shots;
        var vp = _x.V3(veh + 0x50);
        foreach (var (_, proj, pool) in _pools)
            foreach (var p in pool)
            {
                uint s = _x.U32(p + 0xAC0);
                if (_state.TryGetValue(p, out var old) && old == 0 && s != 0)
                {
                    var b = _x.Read(p + 0xB0, 16); var lp = _x.V3(p + 0xB00); var vel = _x.V3(p + 0xB30);
                    var q = b.Length == 16 ? new Quaternion(BE.F32(b, 0), BE.F32(b, 4), BE.F32(b, 8), BE.F32(b, 12)) : Quaternion.Identity;
                    var off = lp - vp;
                    if (float.IsFinite(off.X + off.Y + off.Z + vel.X + vel.Y + vel.Z + q.X + q.Y + q.Z + q.W) && off.Length() < 50 && vel.Length() < 2000)
                        shots.Add(new CoopShot(proj, off, vel, q));
                }
                _state[p] = s;
            }
        return shots;
    }

    /// <summary>Shots packet body: u8 count, then per shot u32 projectile, 3 f32 offset, 3 f32 velocity, 4 i16 quaternion.</summary>
    public static byte[] Write(IReadOnlyList<CoopShot> shots)
    {
        int n = Math.Min(shots.Count, 32);
        var b = new byte[1 + 36 * n];
        b[0] = (byte)n;
        int o = 1;
        static ushort S(float v) => (ushort)(short)Math.Clamp(MathF.Round(v * 32767), -32767, 32767);
        foreach (var s in shots.Take(n))
        {
            BE.W32(b, o, s.Projectile);
            BE.WF32(b, o + 4, s.Offset.X); BE.WF32(b, o + 8, s.Offset.Y); BE.WF32(b, o + 12, s.Offset.Z);
            BE.WF32(b, o + 16, s.Velocity.X); BE.WF32(b, o + 20, s.Velocity.Y); BE.WF32(b, o + 24, s.Velocity.Z);
            BE.W16(b, o + 28, S(s.Rotation.X)); BE.W16(b, o + 30, S(s.Rotation.Y)); BE.W16(b, o + 32, S(s.Rotation.Z)); BE.W16(b, o + 34, S(s.Rotation.W));
            o += 36;
        }
        return b;
    }

    public static List<CoopShot> Read(byte[] b, int at)
    {
        var res = new List<CoopShot>();
        if (b.Length < at + 1) return res;
        int n = b[at];
        for (int i = 0, o = at + 1; i < n && o + 36 <= b.Length; i++, o += 36)
        {
            float Q(int k) => (short)BE.U16(b, o + k) / 32767f;
            var q = new Quaternion(Q(28), Q(30), Q(32), Q(34));
            var s = new CoopShot(BE.U32(b, o), new(BE.F32(b, o + 4), BE.F32(b, o + 8), BE.F32(b, o + 12)),
                new(BE.F32(b, o + 16), BE.F32(b, o + 20), BE.F32(b, o + 24)), q.LengthSquared() > 0.5f ? Quaternion.Normalize(q) : Quaternion.Identity);
            if (float.IsFinite(s.Offset.X + s.Offset.Y + s.Offset.Z + s.Velocity.X + s.Velocity.Y + s.Velocity.Z) && s.Offset.Length() < 50 && s.Velocity.Length() < 2000)
                res.Add(s);
        }
        return res;
    }

    // ------------------------------------------------------------------ other players' shots, replayed from their puppet
    // The puppet is rebuilt from the shooter's own design (coop-remote-vehicle), so it has the same weapon blocks and the
    // game built their projectile pools: a shot is replayed by launching an idle projectile of the puppet's weapon with
    // the same projectile id (no new objects: creating projectiles mid-game froze the test game). A puppet without that
    // weapon (design not rebuilt yet) skips the shot.
    readonly Queue<(uint Puppet, CoopShot Shot, DateTime At)> _queue = new();
    readonly Dictionary<uint, (DateTime At, Dictionary<uint, uint[]> Pools)> _puppetPools = new();
    (uint Puppet, uint Proj)? _op;
    uint _opSeq;
    DateTime? _doneAt;
    public int Replayed { get; private set; }
    public int Skipped { get; private set; }
    const uint Harmless = 0xFFFFFFFE;   // damage type -2 = "no damage"

    /// <summary>Queues another player's shots, fired from their puppet <paramref name="puppet"/>.</summary>
    public void Replay(uint puppet, IEnumerable<CoopShot> shots)
    {
        if (!HasMod) return;
        foreach (var s in shots) if (_queue.Count < 64) _queue.Enqueue((puppet, s, DateTime.UtcNow));
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

    /// <summary>One mailbox step per call (the game runs one request per frame).</summary>
    public void Pump(Func<uint, bool> puppetAlive)
    {
        if (!HasMod) return;
        if (_op != null)
        {
            if (_x.U32(Mailbox + 4) != _opSeq) return;                 // the cave writes "done" after the result
            if (_x.U32(Mailbox + 0x14) != 0) Replayed++; else Skipped++;
            _op = null;
        }
        if (_x.U32(Mailbox) != _x.U32(Mailbox + 4)) return;
        while (_queue.Count > 0)
        {
            var (pup, shot, at) = _queue.Dequeue();
            if (DateTime.UtcNow - at > TimeSpan.FromSeconds(0.5) || !puppetAlive(pup)) { Skipped++; continue; }   // too late / puppet gone
            uint idle = PoolsOf(pup).TryGetValue(shot.Projectile, out var pool) ? pool.FirstOrDefault(p => _x.U32(p + 0xAC0) == 0) : 0;
            if (idle == 0) { Skipped++; continue; }                     // no such weapon on the puppet, or all in flight
            // seen, not felt: the real shot in the shooter's game does the damage (forwarded by coop-remote-damage)
            foreach (uint off in new uint[] { 0x448, 0xBB4, 0xC24 }) if (_x.U32(idle + off) != Harmless) W32(idle + off, Harmless);
            // the puppet stands where the shooter's vehicle is (predicted to now): the shot leaves the same muzzle
            Post(2, pup, idle, _x.V3(pup + 0x50) + shot.Offset, shot.Velocity, shot.Rotation);
            _op = (pup, idle);
            return;
        }
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
        _doneAt = null;
    }

    /// <summary>Forgets the pools of puppets that no longer exist (Change Vehicle and rebuilds free every vehicle and its blocks).</summary>
    public void Forget(Func<uint, bool> puppetAlive)
    {
        foreach (var k in _puppetPools.Keys.Where(k => !puppetAlive(k)).ToList()) _puppetPools.Remove(k);
    }
}
