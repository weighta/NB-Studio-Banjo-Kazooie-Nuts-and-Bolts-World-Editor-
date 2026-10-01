using System.Globalization;
using System.Numerics;
using NB.Core.Formats;
using NB.Core.IO;
using NB.Core.World;

namespace NB.Core.Project;

/// <summary>
/// World edits that a mod can carry as instructions instead of file differences ("ops"), replayed onto the player's game
/// after every mod's file differences. A mod made of ops combines with mods that change the same files: for example
/// Showdown Town co-op adds its puppet trolleys to whatever Showdown Town an edition has (vanilla, snowy, ...).
///
/// An op is a list of strings, the NB.Cli command without its workspace argument:
/// <code>
///   objparams-copy &lt;src bundle&gt; &lt;src asset&gt; &lt;dst bundle&gt; &lt;dst template asset&gt; [new name]
///   objparams-set  &lt;asset&gt; &lt;offset hex&gt; &lt;u32 hex | asset name&gt; --bundle &lt;bundle&gt;
///   asset-copy     &lt;src bundle&gt; &lt;src asset&gt; &lt;dst bundle&gt; [new name]
///   ai-route       &lt;world bundle&gt; &lt;marker asset&gt; [options, see NB.Cli]
///   script-insert  &lt;bundle&gt; &lt;script asset&gt; &lt;after&gt; &lt;command hex words...&gt;
/// </code>
/// script-insert's "after" is a hex offset, or <c>op:8D</c> = after the first command with that opcode (found by
/// content, so it still works when another mod changed the script before it).
/// </summary>
public static class WorldOps
{
    public static readonly string[] Names = { "objparams-copy", "objparams-set", "asset-copy", "ai-route", "script-insert" };

    static string Disp(string s) => AssetIds.DisplayName(s);
    static int Sym(CaffFile c, string name) => c.Symbols.FindIndex(s => Disp(s) == name) + 1;
    static uint Hex(string s) => Convert.ToUInt32(s, 16);
    static float Fl(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>Checks an op's name and argument count (mods are checked when they are built and loaded).</summary>
    public static string? Problem(IReadOnlyList<string> op)
    {
        if (op.Count == 0) return "empty op";
        int min = op[0] switch { "objparams-copy" => 5, "objparams-set" => 4, "asset-copy" => 4, "ai-route" => 3, "script-insert" => 5, _ => -1 };
        if (min < 0) return $"unknown op \"{op[0]}\" (a newer NB Studio / NB Multiplayer may be needed)";
        return op.Count < min ? $"{op[0]}: {min - 1} or more arguments expected" : null;
    }

    /// <summary>Runs one op on a workspace (or a game folder opened with <see cref="Workspace.OnFolder"/>).</summary>
    /// <param name="index">Only for objparams-set without --bundle: every resident and streamed copy through the asset index.</param>
    public static void Run(Workspace ws, IReadOnlyList<string> op, Action<string>? log = null, Func<AssetIndex>? index = null)
    {
        if (Problem(op) is { } p) throw new ArgumentException(p);
        var a = op.ToArray();
        log ??= _ => { };
        switch (a[0])
        {
            case "objparams-copy": ObjparamsCopy(ws, a, log); break;
            case "objparams-set": ObjparamsSet(ws, a, log, index); break;
            case "asset-copy": AssetCopy(ws, a, log); break;
            case "ai-route": AiRouteOp(ws, a, log); break;
            case "script-insert": ScriptInsert(ws, a, log); break;
        }
    }

    static string Opt(string[] a, string k, string def) { int i = Array.LastIndexOf(a, k); return i > 0 && i + 1 < a.Length ? a[i + 1] : def; }   // last one wins

    // objparams-copy <src bundle> <src asset> <dst bundle> <dst template asset> [new name]: clone a same-class, same-size
    // record in the destination under the source's name (or new name) and overwrite its .data (objparams carry no pointers)
    static void ObjparamsCopy(Workspace ws, string[] a, Action<string> log)
    {
        uint sb = Hex(a[1]), db = Hex(a[3]);
        static string N(string n) => n.StartsWith("aid_") ? n : "aid_objparams_banjox_" + n;
        string src = N(a[2]), tpl = N(a[4]), nn = a.Length > 5 && !a[5].StartsWith("--") ? N(a[5]) : src;
        var sc = ws.LoadResident(sb);
        int ss = Sym(sc, src);
        if (ss == 0) throw new InvalidDataException($"{src} not in {sb:x6}");
        if (sc.Relocs.Any(r => sc.Parts[r.FromPart - 1].Symbol == ss)) throw new InvalidDataException($"{src} has pointers; not a plain objparams record");
        var sd = sc.PartsOf(ss).First(p => sc.SectionOf(p).Name == ".data").Data;
        var dc = ws.LoadResident(db);
        int ns = Sym(dc, nn);
        if (ns == 0)
        {
            int ts = Sym(dc, tpl);
            if (ts == 0) throw new InvalidDataException($"{tpl} not in {db:x6}");
            ns = CaffEdit.CloneAsset(dc, ts, nn);
        }
        var part = dc.PartsOf(ns).First(p => dc.SectionOf(p).Name == ".data");
        if (part.Data.Length != sd.Length) throw new InvalidDataException($"size differs: {part.Data.Length} vs {sd.Length} (use a template of the same class)");
        if (part.Data.AsSpan().SequenceEqual(sd)) { log($"{nn} in {db:x6} is already {src}"); return; }
        part.Data = (byte[])sd.Clone();
        ws.SaveResident(db, dc, $"{nn}: copied from {src} ({sb:x6})");
        log($"{nn} ({AssetIds.IdOf(nn):X8}) in {db:x6} = {src} from {sb:x6}");
    }

    // objparams-set <asset> <offset hex> <u32 hex | asset name> [--bundle <bundle>]
    static void ObjparamsSet(Workspace ws, string[] a, Action<string> log, Func<AssetIndex>? index)
    {
        string name = a[1].StartsWith("aid_") ? a[1] : "aid_objparams_banjox_vehicleblock_" + a[1];
        int off = Convert.ToInt32(a[2], 16);
        uint val = a[3].StartsWith("aid_") ? AssetIds.IdOf(a[3]) ?? throw new InvalidDataException($"no asset {a[3]}") : Hex(a[3]);
        int n = 0;
        void Patch(byte[] d, string where)
        {
            if (off < 0 || off + 4 > d.Length) throw new ArgumentException($"offset outside {name} ({d.Length} bytes)");
            uint old = BE.U32(d, off); BE.W32(d, off, val); n++;
            log($"{name} {where}: +0x{off:X} 0x{old:X8} -> 0x{val:X8}");
        }
        var residents = new List<(uint Bundle, int Symbol)>();
        var streamed = new List<uint>();
        if (Opt(a, "--bundle", "") is { Length: > 0 } bs)
        {
            uint b = Hex(bs);
            int s = Sym(ws.LoadResident(b), name);
            if (s == 0) throw new InvalidDataException($"{name} not in {b:x6}");
            residents.Add((b, s));
        }
        else
        {
            var idx = index?.Invoke() ?? throw new ArgumentException("objparams-set needs --bundle here");
            residents.AddRange(idx.Entries.Where(x => x.Name == name && !x.Streamed).GroupBy(x => x.Bundle).Select(g => (g.Key, g.First().Symbol)));
            streamed.AddRange(idx.Entries.Where(x => x.Name == name && x.Streamed).Select(x => x.Bundle).Distinct());
        }
        foreach (var (b, s) in residents)
        {
            var caff = ws.LoadResident(b);
            Patch(caff.PartsOf(s).First(p => caff.SectionOf(p).Name == ".data").Data, $"{b:x6}");
            ws.SaveResident(b, caff, $"{name} +0x{off:X} = 0x{val:X8}");
        }
        uint id = AssetIds.IdOf(name) ?? 0;
        foreach (var b in streamed)
        {
            var arch = ws.LoadStream(b); bool changed = false;
            foreach (var en in arch.Entries.Where(x => x.Id == id && x.Kind == "caff"))
            {
                var sc = CaffFile.Read(en.Data!);
                int sym = Sym(sc, name);
                if (sym == 0) continue;
                Patch(sc.PartsOf(sym).First(p => sc.SectionOf(p).Name == ".data").Data, $"{b:x6} streamed");
                en.Data = sc.Write(); changed = true;
            }
            if (changed) ws.SaveStream(b, arch, $"{name} +0x{off:X} = 0x{val:X8}");
        }
        if (n == 0) throw new InvalidDataException($"{name} not found");
    }

    // asset-copy <src bundle> <src asset> <dst bundle> [new name]: a self-contained asset (e.g. a vehicle blueprint)
    static void AssetCopy(Workspace ws, string[] a, Action<string> log)
    {
        uint sb = Hex(a[1]), db = Hex(a[3]);
        string src = a[2], nn = a.Length > 4 && !a[4].StartsWith("--") ? a[4] : src;
        var sc = ws.LoadResident(sb);
        int ss = Sym(sc, src);
        if (ss == 0) throw new InvalidDataException($"{src} not in {sb:x6}");
        var dc = ws.LoadResident(db);
        if (Sym(dc, nn) != 0) { log($"{nn} is already in {db:x6}"); return; }
        int ns = CaffEdit.CopyAsset(sc, ss, dc, nn);
        ws.SaveResident(db, dc, $"{nn}: copied from {src} ({sb:x6})");
        log($"{nn} ({AssetIds.IdOf(nn):X8}) in {db:x6} = {src} from {sb:x6}: {dc.PartsOf(ns).Count()} part(s)");
    }

    // script-insert <bundle> <script asset> <after: hex offset | op:XX> <command hex...>: insert one command after another.
    // Only safe where no relative skip (e.g. op 0x1C) spans the insertion point.
    static void ScriptInsert(Workspace ws, string[] a, Action<string> log)
    {
        uint b = Hex(a[1]);
        var caff = ws.LoadResident(b);
        int sym = Sym(caff, a[2]);
        if (sym == 0) throw new InvalidDataException($"{a[2]} not in {b:x6}");
        if (caff.Relocs.Any(r => caff.Parts[r.FromPart - 1].Symbol == sym)) throw new InvalidDataException("script has pointers");
        var part = caff.PartsOf(sym).First(p => caff.SectionOf(p).Name == ".data");
        var sc = ScriptAsset.Parse(part.Data);
        int ci;
        if (a[3].StartsWith("op:", StringComparison.OrdinalIgnoreCase))
        {
            int opc = Convert.ToInt32(a[3][3..], 16);
            ci = sc.Commands.FindIndex(c => c.Op == opc);
            if (ci < 0) throw new InvalidDataException($"{a[2]} has no command with opcode 0x{opc:X2}");
        }
        else
        {
            int after = Convert.ToInt32(a[3], 16);
            ci = sc.Commands.FindIndex(c => c.Offset == after);
            if (ci < 0) throw new ArgumentException($"no command at 0x{after:X}");
        }
        var cmd = Convert.FromHexString(string.Concat(a.Skip(4)).Replace(" ", ""));
        if (BE.S32(cmd, 0) != cmd.Length) throw new ArgumentException("command size field must equal its length");
        if (ci + 1 < sc.Commands.Count && sc.Commands[ci + 1].Data.AsSpan().SequenceEqual(cmd)) { log("script command already inserted"); return; }
        int at = sc.Commands[ci].Offset;
        sc.Commands.Insert(ci + 1, new ScriptCommand { Data = cmd });
        part.Data = sc.Write(); part.Size = part.Data.Length;
        ws.SaveResident(b, caff, $"{a[2]}: command {Convert.ToHexString(cmd)} inserted after 0x{at:X}");
        log($"inserted {cmd.Length}-byte command into {a[2]} after 0x{at:X}");
    }

    // ai-route <world bundle> <marker asset> [options]: a looping AI vehicle route
    //   --oval cx cy cz rx rz n | --points "x,y,z;x,y,z;…"   route (closed loop)   --width w (8)   --tag t (9)
    //   --vehicle <aid_vehicle_…> [--vehicle-template <aid_vehicle_…>]  (clone the template in 4f/685374 when missing)
    //   --driver <objparams actor> (actor_npc_thomas)  --strategy <new objparams name> --strategy-template <jogger> --speed s (40)
    //   --spawn-node k (0)  --remove-from <index>  --remove-only  --player-slot x y z yaw  --loop-from k  --nodes-only
    //   --keep-strategy  --spawn-at x y z  --mask hex
    static void AiRouteOp(Workspace ws, string[] a, Action<string> log)
    {
        uint wb = Hex(a[1]);
        static string Obj(string n) => n.StartsWith("aid_") ? n : "aid_objparams_banjox_" + n;
        var caff = ws.LoadResident(wb);
        int ms = Sym(caff, a[2]);
        if (ms == 0) throw new InvalidDataException($"{a[2]} not in {wb:x6}");
        if (a.Contains("--remove-from"))
            log($"removed {AiRoute.RemoveFrom(caff, ms, int.Parse(Opt(a, "--remove-from", "0")), 21, 22)} appended record(s)");
        if (AiRoute.FixTerminator(caff, ms)) log("closing type-0 record moved back to the end");
        if (a.Contains("--remove-only")) { ws.SaveResident(wb, caff, $"AI route removed from {a[2]}"); return; }
        var pts = new List<Vector3>();
        int oi = Array.IndexOf(a, "--oval");
        if (oi > 0) pts = AiRoute.Oval(new(Fl(a[oi + 1]), Fl(a[oi + 2]), Fl(a[oi + 3])), Fl(a[oi + 4]), Fl(a[oi + 5]), int.Parse(a[oi + 6]));
        else foreach (var t in Opt(a, "--points", "").Split(';', StringSplitOptions.RemoveEmptyEntries)) { var c = t.Split(',').Select(Fl).ToArray(); pts.Add(new(c[0], c[1], c[2])); }
        if (pts.Count < 3) throw new ArgumentException("give --oval or --points");
        var nodeTpl = AiRoute.FirstRecord(caff, ms, 22) ?? throw new InvalidDataException("no path node in the marker asset to copy");
        var title = ws.LoadResident(0x757c4b);
        var spawnTpl = AiRoute.FirstRecord(title, Sym(title, "aid_marker_banjox_ui_frontend_startscreen"), 21)!;
        int first = AiRoute.MaxIndex(caff, ms) + 1;
        // --player-slot x y z yaw: a vehicle-spawn record with no vehicle (slot 0) placed BEFORE the AI spawn. The town
        // script's trolley command (opcode 0x8D, marker 0) places Banjo's trolley at the level's first type-21 record;
        // without this, the first one is the AI spawn and every player vehicle appeared there (live trace).
        int pi = Array.IndexOf(a, "--player-slot");
        if (pi > 0)
        {
            AiRoute.AddPlayerSlot(caff, ms, spawnTpl, first, new(Fl(a[pi + 1]), Fl(a[pi + 2]), Fl(a[pi + 3])), Fl(a[pi + 4]));
            log($"player vehicle marker #{first} at ({a[pi + 1]}, {a[pi + 2]}, {a[pi + 3]})");
            first++;
        }
        AiRoute.AddLoop(caff, ms, pts, Fl(Opt(a, "--width", "8")), int.Parse(Opt(a, "--tag", "9")), nodeTpl, first, int.Parse(Opt(a, "--loop-from", "0")));
        log($"path: {pts.Count} nodes, indices {first}..{first + pts.Count - 1}, tag {Opt(a, "--tag", "9")}");
        if (a.Contains("--nodes-only")) { ws.SaveResident(wb, caff, $"AI route nodes only in {a[2]}"); return; }
        string stratName = Obj(Opt(a, "--strategy", "actorstrategy_ultra_ai"));
        if (!a.Contains("--keep-strategy"))   // --keep-strategy: use an existing strategy record as is
        {
            int tplS = Sym(caff, Obj(Opt(a, "--strategy-template", "actorstrategy_showdowntown_mrfit")));
            if (tplS == 0) throw new InvalidDataException("strategy template not in the world bundle");
            AiRoute.MakeVehicleStrategy(caff, tplS, stratName, Fl(Opt(a, "--speed", "40")));
        }
        string veh = Opt(a, "--vehicle", "aid_vehicle_banjox_ultra_ai1");
        if (Sym(caff, veh) == 0)
        {   // (a vehicle already in the world bundle, e.g. from asset-copy, is used as is)
            var common = ws.LoadResident(0x685374);
            if (Sym(common, veh) == 0)
            {
                string vt = Opt(a, "--vehicle-template", "aid_vehicle_banjox_test_gm_sdt1");
                int vs = Sym(common, vt);
                if (vs == 0) throw new InvalidDataException($"vehicle template {vt} not in 685374");
                CaffEdit.CloneAsset(common, vs, veh);
                ws.SaveResident(0x685374, common, $"AI vehicle {veh} cloned from {vt}");
                log($"vehicle {veh} cloned from {vt} (replace its blocks with vehicle-replace)");
            }
        }
        uint vehId = AssetIds.IdOf(veh)!.Value;
        uint drvId = Opt(a, "--driver", "actor_npc_thomas") == "none" ? 0u : AssetIds.IdOf(Obj(Opt(a, "--driver", "actor_npc_thomas")))!.Value;
        uint stId = AssetIds.IdOf(stratName)!.Value;
        int k = int.Parse(Opt(a, "--spawn-node", "0"));
        var sp = pts[k]; var nx = pts[(k + 1) % pts.Count];
        int sa = Array.LastIndexOf(a, "--spawn-at");   // --spawn-at x y z: spawn elsewhere (e.g. a runway), heading for the start node
        if (sa > 0) { nx = pts[k]; sp = new(Fl(a[sa + 1]), Fl(a[sa + 2]), Fl(a[sa + 3])); }
        int si = first + pts.Count;
        AiRoute.AddVehicleSpawn(caff, ms, spawnTpl, si, sp + new Vector3(0, 1.5f, 0), MathF.Atan2(nx.X - sp.X, nx.Z - sp.Z), first + k, vehId, drvId, stId, mask: Hex(Opt(a, "--mask", "0")));
        log($"spawn #{si} at {sp} → node {first + k}: vehicle {vehId:X8}, driver {drvId:X8}, strategy {stId:X8}");
        ws.SaveResident(wb, caff, $"AI route: {pts.Count} nodes + vehicle spawn in {a[2]}");
    }
}
