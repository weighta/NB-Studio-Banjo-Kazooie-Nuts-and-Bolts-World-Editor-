using System.Numerics;
using NB.Core.IO;

namespace NB.Core.Live;

/// <summary>
/// Other players' real vehicles on the puppets, their block damage and the parts that broke off
/// (exe mod coop-remote-vehicle, coop/research/vehicles/REPORT.txt). The exe mod runs one request at a time from a
/// mailbox at 0x82FBCBD0, on the game thread, in the local player's avatar update (where the game swaps vehicles
/// itself): SERIALIZE the local vehicle into the saved-blueprint format; ALLOC + REBUILD a puppet from another player's
/// design (its AI driver stays seated); HEALTH of one puppet block (the game's own green / orange / red damage tint and
/// hit flash); DETACH one puppet block (the game splits it off as a loose piece). Blocks are named by their grid word
/// (block +0xEC), which is the same in a vehicle and in a puppet rebuilt from its design.
/// </summary>
public sealed partial class CoopSync
{
    const uint VMailbox = 0x82FBCBD0, VHook = 0x82251060, VHookWord = 0x48AEE2A0, VCave = 0x82D3F300, VCaveFirstWord = 0x3D6082FC;
    enum VOp : uint { Serialize = 1, Alloc = 2, Rebuild = 3, Health = 4, Detach = 5, Free = 6 }

    sealed class VRequest
    {
        public VOp Op;
        public uint Veh, MbVeh, MbArg, Grid;
        public float Frac, Flash;
        public CoopDesign? Design;
        public DateTime At = DateTime.UtcNow;
    }

    bool? _vehicleMod;
    /// <summary>This game has the coop-remote-vehicle exe mod (co-op 1.4 and later).</summary>
    public bool HasVehicleMod => _vehicleMod ??= _x.U32(VHook) == VHookWord && _x.U32(VCave) == VCaveFirstWord;

    VRequest? _op;
    uint _opSeq;
    DateTime? _opDoneAt;

    readonly HashSet<uint> _rebuilding = new();                    // puppets with an ALLOC / REBUILD under way
    readonly Dictionary<uint, DateTime> _quietUntil = new();       // puppets that just split / were rebuilt
    readonly Dictionary<uint, uint> _designOf = new();             // puppet -> design it was rebuilt with (none = the trolley)
    readonly Dictionary<uint, Dictionary<uint, float>> _applied = new();   // puppet -> grid -> health fraction set
    readonly Dictionary<uint, (DateTime At, uint Lo, uint Hi, Dictionary<uint, uint> Map)> _blockMaps = new();
    readonly Dictionary<(uint Veh, uint Grid), (int Tries, DateTime At)> _detachTries = new();
    readonly Dictionary<uint, int> _designFails = new();           // design -> failed rebuilds here
    readonly Dictionary<long, (uint Design, DateTime Since)> _designWait = new();

    uint _localVehSeen, _designVeh;
    DateTime _localVehSince, _serializeRetryAt;
    HashSet<uint> _localFull = new();

    /// <summary>The local player's vehicle (0 on foot / not in town).</summary>
    public uint LocalVehicle => _localVeh;

    /// <summary>The puppet that shows remote player <paramref name="id"/> right now (0 = none, hidden or being changed).</summary>
    public uint PuppetOf(long id) => _assigned.TryGetValue(id, out var v) && !Quiet(v) && Alive(v) && _x.U32(v + 0x1C0) == 0 ? v : 0;

    /// <summary>Is <paramref name="veh"/> still one of this game's live puppets?</summary>
    public bool IsLivePuppet(uint veh) => _vehicles.Contains(veh) && Alive(veh);

    /// <summary>The local player's vehicle design (null until the game serialized it).</summary>
    public CoopDesign? LocalDesign { get; private set; }

    /// <summary>Diagnostics: requests the mailbox ran (by command) and the last rebuild result.</summary>
    public readonly Dictionary<string, int> VehicleRequests = new();
    public string LastVehicleEvent { get; private set; } = "";

    /// <summary>
    /// A remote player whose current design is not on their puppet yet (on its way over the network, or being built):
    /// the puppet stays hidden for up to 4 seconds instead of showing their previous vehicle. Designs this game could not
    /// build are shown as they are.
    /// </summary>
    bool WaitingForDesign(long id, uint veh, CoopRemote r)
    {
        uint want = r.State.Design;
        if (!HasVehicleMod || want == 0 || _designOf.GetValueOrDefault(veh) == want || _designFails.GetValueOrDefault(want) >= 2)
        {
            _designWait.Remove(id);
            return false;
        }
        var now = DateTime.UtcNow;
        if (!_designWait.TryGetValue(id, out var w) || w.Design != want) _designWait[id] = w = (want, now);
        return now - w.Since < TimeSpan.FromSeconds(4);
    }

    /// <summary>One mailbox step per call (the game runs one request per frame).</summary>
    void PumpVehicles(IReadOnlyDictionary<long, CoopRemote> remotes)
    {
        if (!HasVehicleMod) return;
        if (_op != null && !TryFinish()) return;
        if (_op != null) return;                                                // the step that finished posted the next one
        if (_x.U32(VMailbox) != _x.U32(VMailbox + 4)) return;                   // a request from before (re-attached): wait
        var now = DateTime.UtcNow;

        // 1. the local player's own design, once their vehicle has settled (Change Vehicle, summon)
        if (_localVeh != 0 && _localVeh != _designVeh && now - _localVehSince > TimeSpan.FromSeconds(0.4) && now >= _serializeRetryAt)
        {
            Post(new VRequest { Op = VOp.Serialize, Veh = _localVeh, MbVeh = _localVeh });
            return;
        }

        // 2. a puppet whose player drives another design: rebuild it
        foreach (var (id, veh) in _assigned)
        {
            if (!remotes.TryGetValue(id, out var r)) continue;
            uint want = r.State.Design;
            if (want == 0 || _designOf.GetValueOrDefault(veh) == want || r.Design?.Hash != want || _designFails.GetValueOrDefault(want) >= 2) continue;
            if (Quiet(veh) || !Alive(veh)) continue;
            _rebuilding.Add(veh);
            Post(new VRequest { Op = VOp.Alloc, Veh = veh, MbArg = (uint)r.Design.Bytes.Length, Design = r.Design });
            return;
        }

        // 3. parts that broke off that player's vehicle, then 4. block damage
        VRequest? best = null; float bestDiff = 0.03f;
        foreach (var (id, veh) in _assigned)
        {
            if (!remotes.TryGetValue(id, out var r) || r.Damage is not { } dmg || dmg.Design == 0) continue;
            if (_designOf.GetValueOrDefault(veh) != dmg.Design || dmg.Design != r.State.Design || Quiet(veh) || !Alive(veh)) continue;
            var map = BlockMap(veh);
            if (map.Count == 0) continue;
            if (dmg.Missing.Count > 0)
            {
                foreach (var g in CutBlocks(map, dmg.Missing))
                {
                    var t = _detachTries.GetValueOrDefault((veh, g));
                    if (t.Tries >= 2 || now - t.At < TimeSpan.FromSeconds(0.6)) continue;   // re-check once after 0.6 s
                    _detachTries[(veh, g)] = (t.Tries + 1, now);
                    Post(new VRequest { Op = VOp.Detach, Veh = veh, MbVeh = veh, MbArg = map[g], Grid = g });
                    return;
                }
            }
            var applied = _applied.TryGetValue(veh, out var a) ? a : _applied[veh] = new();
            foreach (var (g, block) in map)
            {
                if (dmg.Missing.Contains(g)) continue;
                float want = dmg.Health.GetValueOrDefault(g, 1f), have = applied.GetValueOrDefault(g, 1f);
                float diff = MathF.Abs(want - have);
                if (diff <= bestDiff) continue;
                bestDiff = diff;
                // a fresh hit flashes the block (the game's hit flash), a regenerating block just changes colour
                best = new VRequest { Op = VOp.Health, Veh = veh, MbVeh = veh, MbArg = block, Grid = g, Frac = want, Flash = want < have ? 0.4f : 0f };
            }
        }
        if (best != null) Post(best);
    }

    void Post(VRequest q)
    {
        var b = new byte[0x1C];                                                 // +0x08 .. +0x23
        BE.W32(b, 0x00, (uint)q.Op); BE.W32(b, 0x04, q.MbVeh); BE.W32(b, 0x08, q.MbArg); BE.WF32(b, 0x0C, q.Frac);
        BE.W32(b, 0x10, 0); BE.WF32(b, 0x14, q.Flash); BE.WF32(b, 0x18, 1e9f);  // result 0; regen delay: never (puppet blocks)
        _x.Write(VMailbox + 8, b);
        _opSeq = _x.U32(VMailbox) + 1;
        var s = new byte[4]; BE.W32(s, 0, _opSeq);
        _x.Write(VMailbox, s);                                                  // arguments first, then the sequence number
        _op = q; _opDoneAt = null;
    }

    /// <summary>
    /// Has the game run the request? The cave marks it taken (done = seq) before it acts and writes the result last, so a
    /// result of 0 is only final a moment after "done".
    /// </summary>
    bool TryFinish()
    {
        var q = _op!;
        if (_x.U32(VMailbox + 4) != _opSeq) return false;
        uint res = _x.U32(VMailbox + 0x18);
        if (res == 0)
        {
            _opDoneAt ??= DateTime.UtcNow;
            if (DateTime.UtcNow - _opDoneAt < TimeSpan.FromMilliseconds(100)) return false;
        }
        _op = null;
        VehicleRequests[q.Op.ToString()] = VehicleRequests.GetValueOrDefault(q.Op.ToString()) + 1;
        Finish(q, res);
        return true;
    }

    void Finish(VRequest q, uint res)
    {
        var now = DateTime.UtcNow;
        switch (q.Op)
        {
            case VOp.Serialize:
                CoopDesign? d = null;
                if (Ptr(res) && _x.Read(res, 2) is { Length: 2 } h) { int n = BE.U16(h, 0); if (n is >= 1 and <= CoopDesign.MaxBlocks) d = CoopDesign.From(_x.Read(res, CoopDesign.HeaderSize + CoopDesign.BlockSize * n)); }
                if (d == null) { _serializeRetryAt = now.AddSeconds(2); LastVehicleEvent = "serialize failed"; break; }
                LocalDesign = d; _designVeh = q.Veh;
                _localFull = BlockMap(q.Veh, force: true).Keys.ToHashSet();
                LastVehicleEvent = $"own design {d.Name} ({d.Blocks} blocks, {d.Hash:X8})";
                break;
            case VOp.Alloc:
                bool stillOurs = _assigned.ContainsValue(q.Veh) && _x.U32(q.Veh) == VehicleVtable && _x.U32(q.Veh + 0x4C) != 0;
                if (Ptr(res) && stillOurs)
                {
                    _x.Write(res, q.Design!.Bytes);
                    Post(new VRequest { Op = VOp.Rebuild, Veh = q.Veh, MbVeh = q.Veh, MbArg = res, Design = q.Design });
                    break;
                }
                _rebuilding.Remove(q.Veh);
                if (Ptr(res)) Post(new VRequest { Op = VOp.Free, MbArg = res });
                else Fail(q.Design!.Hash, "alloc failed");
                break;
            case VOp.Rebuild:
                _rebuilding.Remove(q.Veh);
                _designOf.Remove(q.Veh); _applied.Remove(q.Veh); _blockMaps.Remove(q.Veh); _held.Remove(q.Veh); _hidden.Remove(q.Veh);
                if (Ptr(res) && _x.U32(res) == VehicleVtable)
                {
                    // the old puppet is gone; the new vehicle (same puppet blueprint id) takes its place
                    int i = _vehicles.IndexOf(q.Veh);
                    if (i >= 0) _vehicles[i] = res; else _vehicles.Add(res);
                    foreach (var k in _assigned.Where(kv => kv.Value == q.Veh).Select(kv => kv.Key).ToList()) _assigned[k] = res;
                    _free.Remove(q.Veh);
                    _designOf[res] = q.Design!.Hash;
                    _quietUntil[res] = now.AddMilliseconds(150);
                    LastVehicleEvent = $"puppet rebuilt as {q.Design.Name} ({q.Design.Blocks} blocks)";
                }
                else { Fail(q.Design!.Hash, "rebuild failed"); _lastScan = DateTime.MinValue; }
                break;
            case VOp.Health:
                if (res != 0) (_applied.TryGetValue(q.Veh, out var a) ? a : _applied[q.Veh] = new())[q.Grid] = q.Frac;
                break;
            case VOp.Detach:
                // the next update splits the islands: the puppet gets a new body (no writes until then)
                _quietUntil[q.Veh] = now.AddMilliseconds(150);
                _blockMaps.Remove(q.Veh);
                if (res != 0) LastVehicleEvent = $"part {q.Grid:X8} broke off a puppet";
                break;
        }
    }

    void Fail(uint design, string what)
    {
        _designFails[design] = _designFails.GetValueOrDefault(design) + 1;
        LastVehicleEvent = what;
    }

    /// <summary>Grid word -> block of a vehicle (block list +0x1488..+0x148C, 0xB0 per entry, block at entry +4).</summary>
    Dictionary<uint, uint> BlockMap(uint veh, bool force = false)
    {
        uint lo = _x.U32(veh + 0x1488), hi = _x.U32(veh + 0x148C);
        var now = DateTime.UtcNow;
        if (!force && _blockMaps.TryGetValue(veh, out var c) && c.Lo == lo && c.Hi == hi && now - c.At < TimeSpan.FromSeconds(0.5)) return c.Map;
        var map = new Dictionary<uint, uint>();
        if (Ptr(lo) && hi >= lo && hi - lo <= 0xB0u * CoopDesign.MaxBlocks)
        {
            var raw = _x.Read(lo, (int)(hi - lo));
            for (int o = 0; o + 0xB0 <= raw.Length; o += 0xB0)
            {
                uint b = BE.U32(raw, o + 4);
                if (Ptr(b)) map.TryAdd(_x.U32(b + 0xEC), b);
            }
        }
        _blockMaps[veh] = (now, lo, hi, map);
        return map;
    }

    /// <summary>
    /// Which missing blocks to break off a puppet: those joined to a block that stays (block joints +0x34..+0x38, 0x70
    /// each, other block at +0x54). In the player's game the destroyed block was cut from everything, and the blocks
    /// behind it fell off with it as one piece; cutting the same block here gives the same pieces. If the joints cannot
    /// be read, every missing block is cut.
    /// </summary>
    List<uint> CutBlocks(Dictionary<uint, uint> map, HashSet<uint> missing)
    {
        var m = missing.Where(map.ContainsKey).ToList();
        if (m.Count == 0) return m;
        var gridOf = map.ToDictionary(kv => kv.Value, kv => kv.Key);
        var cut = new List<uint>();
        bool readable = false;
        foreach (var g in m)
        {
            uint b = map[g], lo = _x.U32(b + 0x34), hi = _x.U32(b + 0x38);
            if (!Ptr(lo) || hi < lo || hi - lo > 0x70u * 64) continue;
            readable = true;
            for (uint j = lo; j + 0x70 <= hi; j += 0x70)
                if (gridOf.TryGetValue(_x.U32(j + 0x54), out var og) && !missing.Contains(og)) { cut.Add(g); break; }
        }
        return readable && cut.Count > 0 ? cut : m;
    }

    /// <summary>The local vehicle's damage: blocks below full health and the blocks of its design that broke off.</summary>
    public CoopDamage? ReadLocalDamage()
    {
        if (LocalDesign == null || _localVeh == 0 || _designVeh != _localVeh) return null;
        var map = BlockMap(_localVeh, force: true);
        if (map.Count == 0) return null;
        var d = new CoopDamage { Design = LocalDesign.Hash };
        foreach (var (g, b) in map)
        {
            var raw = _x.Read(b + 0x26C, 8);                                    // max health, health
            if (raw.Length < 8) continue;
            float max = BE.F32(raw, 0), hp = BE.F32(raw, 4);
            if (!(max > 0) || !float.IsFinite(hp)) continue;
            float f = Math.Clamp(hp / max, 0f, 1f);
            if (f < 0.98f) d.Health[g] = f;
        }
        foreach (var g in _localFull) if (!map.ContainsKey(g)) d.Missing.Add(g);
        return d;
    }
}
