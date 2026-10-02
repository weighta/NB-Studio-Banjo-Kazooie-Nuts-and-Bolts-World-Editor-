using System.Numerics;
using NB.Core.IO;

namespace NB.Core.Live;

/// <summary>
/// A part that broke off a player's vehicle, while it flies / tumbles (co-op packet type 12): named by the lowest design
/// grid word of its blocks, with its rigid body's position, orientation and velocities.
/// </summary>
public readonly record struct CoopPiece(uint Grid, Vector3 Position, Quaternion Rotation, Vector3 Velocity, Vector3 AngularVelocity)
{
    public const int Size = 4 + 12 + 8 + 12 + 6;
    static ushort S(float v, float scale) => (ushort)(short)Math.Clamp(MathF.Round(v * scale), -32767, 32767);

    /// <summary>u8 count, then per piece: u32 grid, 3 f32 position, 4 i16 quaternion, 3 f32 velocity, 3 i16 angular
    /// velocity (1/100 rad/s).</summary>
    public static byte[] Write(IReadOnlyList<CoopPiece> pieces)
    {
        int n = Math.Min(pieces.Count, 24);
        var b = new byte[1 + Size * n];
        b[0] = (byte)n;
        int o = 1;
        foreach (var p in pieces.Take(n))
        {
            BE.W32(b, o, p.Grid);
            BE.WF32(b, o + 4, p.Position.X); BE.WF32(b, o + 8, p.Position.Y); BE.WF32(b, o + 12, p.Position.Z);
            BE.W16(b, o + 16, S(p.Rotation.X, 32767)); BE.W16(b, o + 18, S(p.Rotation.Y, 32767)); BE.W16(b, o + 20, S(p.Rotation.Z, 32767)); BE.W16(b, o + 22, S(p.Rotation.W, 32767));
            BE.WF32(b, o + 24, p.Velocity.X); BE.WF32(b, o + 28, p.Velocity.Y); BE.WF32(b, o + 32, p.Velocity.Z);
            BE.W16(b, o + 36, S(p.AngularVelocity.X, 100)); BE.W16(b, o + 38, S(p.AngularVelocity.Y, 100)); BE.W16(b, o + 40, S(p.AngularVelocity.Z, 100));
            o += Size;
        }
        return b;
    }

    public static List<CoopPiece> Read(byte[] b, int at)
    {
        var res = new List<CoopPiece>();
        if (b.Length < at + 1) return res;
        int n = b[at];
        for (int i = 0, o = at + 1; i < n && o + Size <= b.Length; i++, o += Size)
        {
            float F(int k) => BE.F32(b, o + k);
            float H(int k, float scale) => (short)BE.U16(b, o + k) / scale;
            var q = new Quaternion(H(16, 32767), H(18, 32767), H(20, 32767), H(22, 32767));
            var p = new CoopPiece(BE.U32(b, o), new(F(4), F(8), F(12)), q.LengthSquared() > 0.5f ? Quaternion.Normalize(q) : Quaternion.Identity,
                new(F(24), F(28), F(32)), new(H(36, 100), H(38, 100), H(40, 100)));
            if (float.IsFinite(p.Position.X + p.Position.Y + p.Position.Z + p.Velocity.X + p.Velocity.Y + p.Velocity.Z)
                && p.Position.Length() < 1e5f && p.Velocity.Length() < 1e3f) res.Add(p);
        }
        return res;
    }
}

/// <summary>
/// Parts breaking off, placed exactly: the receiving game breaks the same blocks off the puppet (DETACH) and its loose
/// piece would then fall by its own physics, from a puppet that is a little behind - "close, not exact". Now the player
/// whose vehicle broke sends each loose piece's rigid body for as long as it moves (at most 4 s, 15 times a second) and
/// the other games steer their copy of that piece onto it like a puppet (velocity + 4 x error, teleport when far).
/// </summary>
public sealed partial class CoopSync
{
    // ---- sender: the local vehicle's pieces
    sealed class LocalPiece { public uint Veh; public DateTime Since, LastMove, LastSent; }
    readonly Dictionary<uint, LocalPiece> _localPieces = new();     // key grid -> piece
    readonly HashSet<uint> _localMissingSeen = new();                 // design grids already seen missing
    const double PieceMaxSeconds = 4, PieceRestSeconds = 0.4;

    /// <summary>Did blocks break off the local vehicle since the last call (send its damage now, not at the next 4 Hz tick)?</summary>
    public bool LocalBlocksLost { get; private set; }

    /// <summary>
    /// The local vehicle's loose pieces that still move (call ~15 times a second). A piece is found through a block that
    /// left the vehicle: block +0x46C is the vehicle that owns it now (blocks keep their objects through a split).
    /// </summary>
    public List<CoopPiece> ReadLocalPieces()
    {
        var res = new List<CoopPiece>();
        LocalBlocksLost = false;
        if (LocalDesign == null || _localVeh == 0 || _designVeh != _localVeh || !_origBlocks.TryGetValue(_localVeh, out var orig)) return res;
        var now = DateTime.UtcNow;
        var map = BlockMap(_localVeh, force: true);
        if (map.Count == 0) return res;
        // newly missing blocks -> the pieces they are on now
        foreach (var (blk, g) in orig)
        {
            if (map.ContainsKey(g) || !_localMissingSeen.Add(g)) continue;
            LocalBlocksLost = true;
            uint pv = _x.U32(blk + 0x46C);
            if (pv == _localVeh || !IsLoosePiece(pv, blk)) continue;
            if (_localPieces.Values.Any(p => p.Veh == pv)) continue;
            // the piece is named by the lowest design grid word of its blocks (the same in every game)
            uint key = PieceBlocks(pv).Select(b => orig.TryGetValue(b, out var gg) ? gg : uint.MaxValue).DefaultIfEmpty(uint.MaxValue).Min();
            if (key == uint.MaxValue) continue;
            _localPieces[key] = new LocalPiece { Veh = pv, Since = now, LastMove = now };
        }
        foreach (var (key, p) in _localPieces.ToList())
        {
            uint body = BodyOf(_x, p.Veh);
            if (now - p.Since > TimeSpan.FromSeconds(PieceMaxSeconds) || body == 0 || _x.U32(p.Veh) != VehicleVtable || _x.U32(p.Veh + 0x4C) == 0)
            { _localPieces.Remove(key); continue; }
            var pc = new CoopPiece(key, _x.V3(body), Q(body + 0x40), _x.V3(body + 0x90), _x.V3(body + 0xA0));
            if (!float.IsFinite(pc.Position.X + pc.Position.Y + pc.Position.Z + pc.Velocity.X + pc.Velocity.Y + pc.Velocity.Z + pc.AngularVelocity.X + pc.AngularVelocity.Y + pc.AngularVelocity.Z)) continue;
            if (pc.Velocity.Length() > 0.25f || pc.AngularVelocity.Length() > 0.4f) p.LastMove = now;
            // moving: 15 times a second; at rest: twice a second (the others may break the same part off a moment later
            // and put their piece straight where this one lies), for 4 s after the break
            bool resting = now - p.LastMove > TimeSpan.FromSeconds(PieceRestSeconds);
            if (resting && now - p.LastSent < TimeSpan.FromSeconds(0.5)) continue;
            p.LastSent = now;
            res.Add(resting ? pc with { Velocity = Vector3.Zero, AngularVelocity = Vector3.Zero } : pc);
        }
        return res;
    }

    bool IsLoosePiece(uint pv, uint blk) =>
        Ptr(pv) && _x.U32(pv) == VehicleVtable && _x.U32(pv + 0x4C) != 0 && _x.U32(pv + 0x18A4) != _puppetBlueprint && BodyOf(_x, pv) != 0
        && PieceBlocks(pv).Contains(blk);

    List<uint> PieceBlocks(uint pv)
    {
        var res = new List<uint>();
        uint lo = _x.U32(pv + 0x1488), hi = _x.U32(pv + 0x148C);
        if (!Ptr(lo) || hi < lo || hi - lo > 0xB0u * CoopDesign.MaxBlocks) return res;
        var raw = _x.Read(lo, (int)(hi - lo));
        for (int o = 0; o + 0xB0 <= raw.Length; o += 0xB0) { uint b = BE.U32(raw, o + 4); if (Ptr(b)) res.Add(b); }
        return res;
    }

    // ---- receiver: other players' pieces
    readonly Dictionary<(long Owner, uint Grid), (CoopPiece Piece, DateTime At, float Age)> _pieceTargets = new();
    readonly HashSet<(long Owner, uint Grid)> _pieceSettled = new();
    /// <summary>Diagnostics: piece steering ticks since the start, pieces put in place.</summary>
    public int PieceTicks { get; private set; }
    public int PiecesPlaced { get; private set; }
    readonly Dictionary<(long Owner, uint Grid), uint> _pieceVeh = new();

    /// <summary>Diagnostics: pieces steered in the last call.</summary>
    public int PiecesSteered { get; private set; }

    /// <summary>Another player's moving pieces (<paramref name="age"/>: seconds since they left that game).</summary>
    public void QueuePieces(long owner, IEnumerable<CoopPiece> pieces, float age)
    {
        var now = DateTime.UtcNow;
        foreach (var p in pieces) _pieceTargets[(owner, p.Grid)] = (p, now, age);
    }

    void SteerPieces()
    {
        PiecesSteered = 0;
        if (_pieceTargets.Count == 0) return;
        var now = DateTime.UtcNow;
        foreach (var (k, t) in _pieceTargets.ToList())
        {
            // the sender sends a resting piece twice a second for 4 s after the break; a moving one 15 times a second
            // (a moving piece not heard from for 0.35 s: lost packets - left to its own physics)
            var p0 = t.Piece;
            bool rest = p0.Velocity == Vector3.Zero && p0.AngularVelocity == Vector3.Zero;
            if (now - t.At > TimeSpan.FromSeconds(rest ? 1.2 : 0.35)) { _pieceTargets.Remove(k); _pieceVeh.Remove(k); _pieceSettled.Remove(k); continue; }
            if (!_assigned.TryGetValue(k.Owner, out var puppet)) continue;
            uint blk = OrigBlock(puppet, k.Grid);
            if (blk == 0) continue;                                          // not built from that design here
            uint pv = _x.U32(blk + 0x46C);
            if (pv == puppet) continue;                                      // not broken off here yet (DETACH pending)
            if (!_pieceVeh.TryGetValue(k, out var known) || known != pv)
            {
                if (!IsLoosePiece(pv, blk)) continue;
                _pieceVeh[k] = pv;
            }
            else if (_x.U32(pv) != VehicleVtable || _x.U32(pv + 0x4C) == 0) { _pieceVeh.Remove(k); continue; }
            uint body = BodyOf(_x, pv);
            if (body == 0) continue;
            var p = t.Piece;
            float lead = Math.Clamp(t.Age + (float)(now - t.At).TotalSeconds, 0f, MaxLead);
            var target = p.Position + p.Velocity * lead;
            var cur = _x.V3(body);
            if (!float.IsFinite(cur.X + cur.Y + cur.Z)) continue;
            var err = target - cur;
            bool resting = p.Velocity.Length() < 0.25f && p.AngularVelocity.Length() < 0.4f;
            if (resting && err.Length() < 0.15f) { if (_pieceSettled.Add(k)) PiecesPlaced++; continue; }   // lies where it lies there
            PieceTicks++;
            if (err.Length() > 3f)
            {
                // first sight (detached late, from where the puppet was) or far off: put it where it is in that game
                // (position only: the orientation is steered by angular velocity - writing a rotation into the motion
                // state without its centre of mass would move the body)
                Teleport(body, target);
                _x.WV3(body + 0x90, p.Velocity); _x.WV3(body + 0xA0, p.AngularVelocity);
                PiecesSteered++;
                continue;
            }
            var v = (resting ? Vector3.Zero : p.Velocity) + 4f * err;
            if (v.Length() > 60) v = Vector3.Normalize(v) * 60;
            _x.WV3(body + 0x90, v);
            var rot = p.Rotation;
            var qe = rot * Quaternion.Conjugate(Q(body + 0x40));
            if (qe.W < 0) qe = Quaternion.Negate(qe);
            float ang = 2 * MathF.Acos(Math.Min(1f, qe.W)), s = MathF.Sqrt(MathF.Max(1e-9f, 1 - qe.W * qe.W));
            var axis = ang > 1e-3f ? new Vector3(qe.X, qe.Y, qe.Z) / s : Vector3.Zero;
            _x.WV3(body + 0xA0, (resting ? Vector3.Zero : p.AngularVelocity) + axis * MathF.Min(ang * 5f, 8f));
            PiecesSteered++;
        }
    }

    void ForgetPieces(HashSet<uint> puppets)
    {
        foreach (var k in _pieceVeh.Keys.Where(k => !_assigned.TryGetValue(k.Owner, out var v) || !puppets.Contains(v)).ToList()) _pieceVeh.Remove(k);
    }
}
