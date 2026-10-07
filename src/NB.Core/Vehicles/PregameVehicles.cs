using NB.Core.Formats;
using NB.Core.IO;
using NB.Core.Project;
using NB.Core.World;

namespace NB.Core.Vehicles;

/// <summary>A vehicle the game ships (aid_vehicle_* asset): AI racers, challenge and prize vehicles, the trolley stages.</summary>
public sealed class PregameVehicle
{
    public string Asset = "";
    public uint Id;
    /// <summary>Bundles that hold the asset resident (all of them are written on save).</summary>
    public List<uint> Bundles = new();
    /// <summary>Who uses it in the act / world: "Mr. Fit (AI driver, marker #42 of worldofsport_act2_main)".</summary>
    public List<string> Users = new();
    /// <summary>The driver's name for an AI vehicle (marker type 21 +0x3C), empty otherwise.</summary>
    public string Owner = "";
    public int Parts;

    public string Short => Asset.Replace("aid_vehicle_banjox_", "");
    public string Label => Owner.Length > 0 ? $"{Owner}'s vehicle ({Short})" : Short;
    public override string ToString() => Label;
}

/// <summary>
/// The game's own vehicles in a workspace: listing per act / world with their drivers, reading and writing the
/// blueprints. AI vehicles are placed by marker records of type 21 (+0x38 blueprint id, +0x3C driver objparams,
/// +0x40 strategy, +0x54 vehicle requirements); blueprint assets carry no pointers, so they can change size.
/// </summary>
public static class PregameVehicles
{
    public const string Prefix = "aid_vehicle_banjox_";

    /// <summary>Every vehicle asset of the workspace (one entry per name).</summary>
    public static List<PregameVehicle> All(AssetIndex idx) =>
        idx.Entries.Where(e => e.Type == "vehicle" && e.Symbol > 0 && !e.Streamed && e.Name.StartsWith(Prefix))
            .GroupBy(e => e.Name)
            .Select(g => new PregameVehicle { Asset = g.Key, Id = g.First().Id, Bundles = g.Select(e => e.Bundle & 0xFFFFFF).Distinct().ToList() })
            .OrderBy(v => v.Asset).ToList();

    /// <summary>
    /// The vehicles an act (or a world without act) uses: every aid_vehicle asset resident in the act bundle and the world
    /// bundle, plus the vehicles the act's and world's markers place (type 21), with their drivers.
    /// </summary>
    public static List<PregameVehicle> ForBundles(Workspace ws, AssetIndex idx, IEnumerable<uint> bundles)
    {
        var all = All(idx).ToDictionary(v => v.Id);
        var names = new Dictionary<uint, string>();
        foreach (var e in idx.Entries) if (e.Id != 0) names.TryAdd(e.Id, e.Name);
        var res = new Dictionary<uint, PregameVehicle>();
        var list = bundles.Select(b => b & 0xFFFFFF).Distinct().ToList();
        foreach (var b in list)
        {
            foreach (var v in all.Values.Where(v => v.Bundles.Contains(b))) res.TryAdd(v.Id, v);
            CaffFile caff;
            try { caff = ws.LoadResident(b); } catch { continue; }
            for (int s = 1; s <= caff.Symbols.Count; s++)
            {
                if (!caff.Symbols[s - 1].StartsWith("aid_marker_")) continue;
                MarkerAsset ma;
                try { ma = MarkerAsset.Parse(caff, s); } catch { continue; }
                var d = caff.PartsOf(s).First(p => caff.SectionOf(p).Name == ".data").Data;
                foreach (var r in ma.Records.Where(r => r.Type == 21 && r.Size >= 0x44))
                {
                    uint bp = BE.U32(d, r.Offset + 0x38), driver = BE.U32(d, r.Offset + 0x3C);
                    if (bp == 0 || !all.TryGetValue(bp, out var v)) continue;
                    res.TryAdd(bp, v);
                    string who = driver != 0 && names.TryGetValue(driver, out var dn) ? DriverName(dn) : "";
                    if (who.Length > 0 && v.Owner.Length == 0) v.Owner = who;
                    v.Users.Add($"{(who.Length > 0 ? who + ", AI driver" : "placed")} — marker #{r.Index} of {AssetIds.DisplayName(ma.Name).Replace("aid_marker_banjox_", "")}");
                }
            }
        }
        foreach (var v in res.Values)
            try { v.Parts = Load(ws, v).Blocks.Count; } catch { }
        return res.Values.OrderBy(v => v.Owner.Length > 0 ? 0 : 1).ThenBy(v => v.Owner).ThenBy(v => v.Asset).ToList();
    }

    /// <summary>A display name for a driver objparams (actor_npc_worldofsport_mrfit → "Mr. Fit").</summary>
    public static string DriverName(string objparams)
    {
        var key = CharacterText.KeyOf(objparams);
        if (key == null) return AssetIds.DisplayName(objparams).Replace("aid_objparams_banjox_", "");
        var c = NB.Core.Mods.Characters.All.FirstOrDefault(c => c.Key == key);
        if (c != null) return c.Key == "thomas" ? "Thomas" : c.Key == "blubber" ? "Blubber" : c.Name;
        return char.ToUpperInvariant(key[0]) + key[1..];
    }

    static byte[] DataOf(CaffFile c, int sym) => c.PartsOf(sym).First(p => c.SectionOf(p).Name == ".data").Data;
    static int SymbolOf(CaffFile c, string asset) => c.Symbols.FindIndex(s => s == asset || AssetIds.DisplayName(s) == asset) + 1;

    /// <summary>Reads the blueprint (from the first bundle that holds it).</summary>
    public static Blueprint Load(Workspace ws, PregameVehicle v)
    {
        foreach (var b in v.Bundles)
        {
            var c = ws.LoadResident(b);
            int s = SymbolOf(c, v.Asset);
            if (s > 0) return Blueprint.Parse(DataOf(c, s));
        }
        throw new InvalidDataException($"{v.Asset} not found");
    }

    /// <summary>Writes the blueprint into every bundle that holds the asset (the asset id stays; it may change size: blueprints
    /// have no pointers). One workspace save per bundle (each is an undoable file step in NB Studio). Returns the bundles
    /// written.</summary>
    public static List<uint> Save(Workspace ws, PregameVehicle v, Blueprint bp, string? description = null)
    {
        var data = bp.Write(keepTrailing: false);
        var done = new List<uint>();
        using (ws.Batch())
            foreach (var b in v.Bundles)
            {
                var c = ws.LoadResident(b);
                int s = SymbolOf(c, v.Asset);
                if (s == 0) continue;
                var part = c.PartsOf(s).First(p => c.SectionOf(p).Name == ".data");
                int pid = c.Parts.IndexOf(part) + 1;
                if (c.Relocs.Any(r => r.FromPart == pid)) throw new InvalidDataException($"{v.Asset} in {b:x6} has pointers; not a plain blueprint");
                if (part.Data.AsSpan().SequenceEqual(data)) continue;
                part.Data = data;
                ws.SaveResident(b, c, description ?? $"vehicle {v.Short}: {bp.Blocks.Count} parts");
                done.Add(b);
            }
        return done;
    }
}
