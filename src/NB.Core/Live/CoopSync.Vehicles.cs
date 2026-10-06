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
    /// <summary>
    /// Vehicle -> block object -> its grid word in the DESIGN. A split re-bases the grid words of every island (the piece
    /// AND what stays: after heavy damage the remaining seat block of a Zapper read 0x0, its puppet's 0x1 / 0x400002...),
    /// so grid words read later no longer name the same block in two games. Block objects survive splits (they move to
    /// the new piece vehicles, block +0x46C = owner), so blocks are named by the grid word they had when the vehicle was
    /// built from / serialized as its design.
    /// </summary>
    readonly Dictionary<uint, Dictionary<uint, uint>> _origBlocks = new();

    // ---- in-place changes of the local vehicle (2026-10-05)
    // The game changes a vehicle without making a new one in two cases: B beside it on foot (the in-field editor: blocks
    // detached, moved, attached - same vehicle object, same block objects, new grid words) and RB (the parts magnet: the
    // pieces' block objects rejoin the vehicle). Change Vehicle / the garage make a new vehicle (serialized as before).
    bool _editing, _reserialize;
    DateTime _holdSince = DateTime.MinValue;
    DateTime _reserializeAt, _shapeCheckAt;
    /// <summary>Vehicle +0x1194: the game's hold count (0x825FF210 adds / removes one; the in-field editor holds the vehicle
    /// while it is open, the vehicle update copies it to +0x11A0 every frame).</summary>
    const uint HoldCount = 0x1194;

    bool DesignVehicleAlive() => _designVeh != 0 && _x.U32(_designVeh) == VehicleVtable && _x.U32(_designVeh + 0x4C) != 0;

    /// <summary>Is the player editing their vehicle right now (it is held)? When an edit ends the vehicle is serialized
    /// again (0.3 s later, once it is rebuilt).</summary>
    bool WatchEdits()
    {
        // The editor is used on foot. The RB magnet also holds the vehicle (2-3 s while it pulls pieces in, measured
        // 2026-10-05) but the player sits in it then: a hold while seated is no edit (counting it made every recall
        // serialize a new, partial design).
        uint v = _localVeh == 0 && DesignVehicleAlive() ? _designVeh : 0;
        uint hold = v != 0 ? _x.U32(v + HoldCount) : 0;
        var t = DateTime.UtcNow;
        if (hold is > 0 and < 64) { if (_holdSince == DateTime.MinValue) _holdSince = t; }
        else _holdSince = DateTime.MinValue;
        bool now = _holdSince != DateTime.MinValue && t - _holdSince > TimeSpan.FromSeconds(0.5);
        if (_editing && !now) { _reserialize = true; _reserializeAt = t.AddSeconds(0.3); }
        _editing = now;
        return now;
    }

    /// <summary>Blocks on the local vehicle that its design does not know (called back after the design was taken from
    /// a damaged vehicle, or added): serialize it again so the others get them.</summary>
    void CheckLocalShape(DateTime now)
    {
        if (now < _shapeCheckAt || _localVeh == 0 || _designVeh != _localVeh || _reserialize) return;
        _shapeCheckAt = now.AddSeconds(0.5);
        if (!_origBlocks.TryGetValue(_localVeh, out var orig)) return;
        uint lo = _x.U32(_localVeh + 0x1488), hi = _x.U32(_localVeh + 0x148C);
        if (!Ptr(lo) || hi < lo || hi - lo > 0xB0u * CoopDesign.MaxBlocks) return;
        var raw = _x.Read(lo, (int)(hi - lo));
        for (int o = 0; o + 0xB0 <= raw.Length; o += 0xB0)
        {
            uint b = BE.U32(raw, o + 4);
            if (Ptr(b) && !orig.ContainsKey(b)) { _reserialize = true; _reserializeAt = now.AddSeconds(0.5); return; }
        }
    }

    // ---- receiver: parts called back on another player's vehicle
    readonly Dictionary<uint, HashSet<uint>> _everMissing = new();       // puppet -> grids its player reported missing
    readonly Dictionary<uint, (int Missing, DateTime Since)> _backSince = new();   // puppet -> parts back since
    readonly Dictionary<uint, DateTime> _reattachAt = new();              // puppet -> last reattach rebuild
    sealed class Reattach { public long Owner; public bool CalledBack; public List<uint> Pieces = new(); public List<CoopPiece> Keep = new(); }
    readonly Dictionary<uint, Reattach> _reattach = new();                // puppet being rebuilt whole
    readonly Dictionary<uint, DateTime> _buried = new();                  // old puppet pieces, hidden
    /// <summary>Diagnostics: puppets rebuilt because their player called parts back.</summary>
    public int Reattached { get; private set; }

    /// <summary>Leaving town (<see cref="NoteAway"/>): a vehicle request the game has not run yet names an object of this
    /// town - withdrawn (marked done), so the cave never runs it in the next level (the garage has a local avatar too).</summary>
    void WithdrawVehicleRequest()
    {
        if (!HasVehicleMod) return;
        uint seq = _x.U32(VMailbox);
        if (_x.U32(VMailbox + 4) != seq) { var b = new byte[4]; BE.W32(b, 0, seq); _x.Write(VMailbox + 4, b); }
        _op = null; _opDoneAt = null;
    }

    /// <summary>A new town (<see cref="EnterTown"/>): the vehicle work of the last one is dropped - puppets being rebuilt
    /// (a stale "rebuilding" puppet stayed in the scan results and could be handed to a player who then never showed),
    /// parts called back, hidden pieces, entry locks, an edit in progress.</summary>
    void ResetVehicleTown()
    {
        WithdrawVehicleRequest();
        _rebuilding.Clear(); _everMissing.Clear(); _reattachAt.Clear(); _reattach.Clear(); _backSince.Clear(); _buried.Clear();
        _lockedEmpty.Clear(); _pieceTargets.Clear(); _pieceVeh.Clear(); _pieceSettled.Clear();
        _editing = false; _reserialize = false; _holdSince = DateTime.MinValue;
        _scan = null;
        if (_designVeh != _localVeh) { _origBlocks.Remove(_designVeh); _designVeh = 0; }   // the design's vehicle was the last town's
    }

    /// <summary>
    /// Parts that broke off a puppet stay loose in this game; when their player calls them back (RB), the design's
    /// "missing" list shrinks. The puppet is then rebuilt whole from the design (one mailbox REBUILD, its driver stays
    /// seated), its old loose pieces are hidden (draw flag + collision layer 9, like unused puppets), and the parts still
    /// missing are cut again - and put where their old pieces lay.
    /// </summary>
    bool TryReattach(long id, uint veh, CoopRemote r, CoopDamage dmg, Dictionary<uint, uint> map, DateTime now)
    {
        var ever = _everMissing.TryGetValue(veh, out var e) ? e : _everMissing[veh] = new();
        foreach (var g in dmg.Missing) ever.Add(g);
        if (!_origBlocks.TryGetValue(veh, out var orig) || r.Design is not { } design || design.Hash != dmg.Design) return false;
        if (_reattachAt.TryGetValue(veh, out var last) && now - last < TimeSpan.FromSeconds(1.5)) return false;
        bool back = false;
        foreach (var g in ever) if (!dmg.Missing.Contains(g) && !map.ContainsKey(g) && orig.ContainsValue(g)) { back = true; break; }
        if (!back) { _backSince.Remove(veh); return false; }
        // the magnet pulls the pieces in one after another for 2-3 s: one rebuild when the player's list has stopped
        // shrinking for 0.8 s (a rebuild per piece made the puppet flicker and re-cut the pieces still on their way)
        if (!_backSince.TryGetValue(veh, out var bs) || bs.Missing != dmg.Missing.Count) { _backSince[veh] = (dmg.Missing.Count, now); return false; }
        if (now - bs.Since < TimeSpan.FromSeconds(0.8)) return false;
        _backSince.Remove(veh);
        var info = new Reattach { Owner = id, CalledBack = true };
        foreach (var (blk, g) in orig)
        {
            if (map.ContainsKey(g)) continue;
            uint pv = _x.U32(blk + 0x46C);
            if (pv == veh || !IsLoosePiece(pv, blk)) continue;
            if (!info.Pieces.Contains(pv)) info.Pieces.Add(pv);
            uint body = BodyOf(_x, pv);
            if (dmg.Missing.Contains(g) && body != 0) info.Keep.Add(new CoopPiece(g, _x.V3(body), Q(body + 0x40), Vector3.Zero, Vector3.Zero));
        }
        _reattachAt[veh] = now;
        _reattach[veh] = info;
        _rebuilding.Add(veh);
        Post(new VRequest { Op = VOp.Alloc, Veh = veh, MbArg = (uint)design.Bytes.Length, Design = design });
        LastVehicleEvent = $"parts called back: rebuilding a puppet ({info.Pieces.Count} old pieces)";
        return true;
    }

    void Bury(uint pv)
    {
        if (_x.U32(pv) != VehicleVtable || _x.U32(pv + 0x4C) == 0 || _x.U32(pv + 0x18A4) == _puppetBlueprint) return;
        _buried[pv] = DateTime.UtcNow.AddSeconds(3);
        BuryOne(pv);
    }

    void BuryOne(uint pv)
    {
        uint body = BodyOf(_x, pv);
        if (body == 0) return;
        SetHidden(pv, true);
        _x.WV3(body + 0x90, Vector3.Zero); _x.WV3(body + 0xA0, Vector3.Zero);
    }

    /// <summary>Keeps buried pieces hidden for a few seconds (a piece settling may get a new body).</summary>
    void BuryTick()
    {
        if (_buried.Count == 0) return;
        var now = DateTime.UtcNow;
        foreach (var (pv, until) in _buried.ToList())
        {
            if (now > until || _x.U32(pv) != VehicleVtable || _x.U32(pv + 0x4C) == 0) { _buried.Remove(pv); continue; }
            BuryOne(pv);
        }
    }

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

        // 1. the local player's own design, once their vehicle has settled (Change Vehicle, summon), again after an
        // in-place edit (also on foot: the parked vehicle) and when blocks the design does not know joined it
        CheckLocalShape(now);
        uint ser = _localVeh != 0 ? _localVeh : _reserialize && DesignVehicleAlive() ? _designVeh : 0;
        if (ser != 0 && !_editing && now >= _serializeRetryAt
            && ((ser != _designVeh && now - _localVehSince > TimeSpan.FromSeconds(0.4)) || (_reserialize && now >= _reserializeAt)))
        {
            Post(new VRequest { Op = VOp.Serialize, Veh = ser, MbVeh = ser });
            return;
        }

        // 2. a puppet whose player drives another design: rebuild it
        foreach (var (id, veh) in _assigned)
        {
            if (!remotes.TryGetValue(id, out var r)) continue;
            uint want = r.State.Design;
            if (want == 0 || _designOf.GetValueOrDefault(veh) == want || r.Design?.Hash != want || _designFails.GetValueOrDefault(want) >= 2) continue;
            if (Quiet(veh) || !Alive(veh)) continue;
            // the old shape's loose pieces go with it (a changed design: the player's game no longer has them either)
            var old = new Reattach { Owner = id };
            if (_origBlocks.TryGetValue(veh, out var ob))
                foreach (var (blk, g) in ob) { uint pv = _x.U32(blk + 0x46C); if (pv != veh && !old.Pieces.Contains(pv) && IsLoosePiece(pv, blk)) old.Pieces.Add(pv); }
            if (old.Pieces.Count > 0) _reattach[veh] = old;
            _rebuilding.Add(veh);
            Post(new VRequest { Op = VOp.Alloc, Veh = veh, MbArg = (uint)r.Design.Bytes.Length, Design = r.Design });
            return;
        }

        // 3. parts that broke off that player's vehicle, then 4. block damage
        VRequest? best = null; float bestDiff = 0.03f;
        foreach (var (id, veh) in _assigned)
        {
            if (!remotes.TryGetValue(id, out var r) || r.Damage is not { } dmg || dmg.Design == 0) continue;
            // (not held back by the 150 ms after a split: DETACH / HEALTH go through the mailbox and the cave checks the
            // block is still on the vehicle - a part breaking into several pieces is cut a frame at a time, not 0.17 s)
            if (_designOf.GetValueOrDefault(veh) != dmg.Design || dmg.Design != r.State.Design || _rebuilding.Contains(veh) || Gone(veh) || BodyOf(_x, veh) == 0) continue;
            var map = BlockMap(veh);
            if (map.Count == 0) continue;
            if (TryReattach(id, veh, r, dmg, map, now)) return;
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
                _reserialize = false;
                if (_designVeh != 0 && _designVeh != q.Veh) _origBlocks.Remove(_designVeh);
                LocalDesign = d; _designVeh = q.Veh;
                _origBlocks.Remove(q.Veh);
                _localFull = BlockMap(q.Veh, force: true).Keys.ToHashSet();
                _localPieces.Clear(); _localMissingSeen.Clear();
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
                _reattach.Remove(q.Veh);
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
                    _origBlocks.Remove(res);                                    // named from its first block read
                    _quietUntil[res] = now.AddMilliseconds(150);
                    LastVehicleEvent = $"puppet rebuilt as {q.Design.Name} ({q.Design.Blocks} blocks)";
                    if (_reattach.Remove(q.Veh, out var ra))
                    {
                        foreach (var pv in ra.Pieces) Bury(pv);
                        // parts still missing are cut again by the damage step; their pieces are put where the old ones lay
                        foreach (var p in ra.Keep) _pieceTargets[(ra.Owner, p.Grid)] = (p, now.AddSeconds(3), 0f);
                        if (ra.CalledBack)
                        {
                            _everMissing[res] = ra.Keep.Select(p => p.Grid).ToHashSet();
                            Reattached++;
                            LastVehicleEvent = $"puppet rebuilt whole (parts called back), {ra.Pieces.Count} old pieces hidden";
                        }
                    }
                    _everMissing.Remove(q.Veh); _reattachAt.Remove(q.Veh);
                }
                else { Fail(q.Design!.Hash, "rebuild failed"); _reattach.Remove(q.Veh); _lastScan = DateTime.MinValue; }
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

    /// <summary>
    /// Design grid word -> block of a vehicle (block list +0x1488..+0x148C, 0xB0 per entry, block at entry +4). The first
    /// read of a vehicle (right after it was built from or serialized as its design) records every block's grid word; later
    /// reads name the blocks still on the vehicle by those (see <see cref="_origBlocks"/>).
    /// </summary>
    Dictionary<uint, uint> BlockMap(uint veh, bool force = false)
    {
        uint lo = _x.U32(veh + 0x1488), hi = _x.U32(veh + 0x148C);
        var now = DateTime.UtcNow;
        if (!force && _blockMaps.TryGetValue(veh, out var c) && c.Lo == lo && c.Hi == hi && now - c.At < TimeSpan.FromSeconds(0.5)) return c.Map;
        var map = new Dictionary<uint, uint>();
        if (Ptr(lo) && hi >= lo && hi - lo <= 0xB0u * CoopDesign.MaxBlocks)
        {
            var raw = _x.Read(lo, (int)(hi - lo));
            bool first = !_origBlocks.TryGetValue(veh, out var orig);
            if (first) orig = new Dictionary<uint, uint>();
            for (int o = 0; o + 0xB0 <= raw.Length; o += 0xB0)
            {
                uint b = BE.U32(raw, o + 4);
                if (!Ptr(b)) continue;
                if (first) { uint g = _x.U32(b + 0xEC); if (orig!.TryAdd(b, g)) map.TryAdd(g, b); }
                else if (orig!.TryGetValue(b, out var g)) map.TryAdd(g, b);
            }
            if (first && map.Count > 0) _origBlocks[veh] = orig!;
        }
        _blockMaps[veh] = (now, lo, hi, map);
        return map;
    }

    /// <summary>Block object of design grid word <paramref name="grid"/> of vehicle <paramref name="veh"/> (0 = unknown),
    /// wherever that block is now (on the vehicle or on a piece that broke off).</summary>
    uint OrigBlock(uint veh, uint grid)
    {
        if (!_origBlocks.TryGetValue(veh, out var orig)) return 0;
        foreach (var (b, g) in orig) if (g == grid) return b;
        return 0;
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
        // (not while the vehicle is edited in place: a block held in the editor is no damage)
        if (LocalDesign == null || _localVeh == 0 || _designVeh != _localVeh || _editing || _reserialize) return null;
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
