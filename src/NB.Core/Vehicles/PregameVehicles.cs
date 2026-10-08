using System.Text.Json;
using NB.Core.Compression;
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

    /// <summary>Where the game uses it: world, Act and challenge (from the markers that place it, else the bundles that
    /// hold it, else its name); several when more than one Act uses it.</summary>
    public List<VehiclePlace> Places = new();
    /// <summary>For vehicles outside the worlds: "Shop blueprints", "Demo vehicles", "Test vehicles", …</summary>
    public string Section = "";
    /// <summary>The game's name for it, when it has one: loctext "vehicle__" + the blueprint's name field
    /// (humba_truck1 → "Humba Truck 1", mpsumo → "Rikishi", trolley4 → "Trolley Mk. 4: Spring"); else a readable name
    /// field ("Red Baron"); empty for creator tags (SalvyBob, y0mper, …).</summary>
    public string Friendly = "";

    public string Short => Asset.Replace("aid_vehicle_banjox_", "");
    public string Label => Owner.Length > 0 ? $"{Owner}'s vehicle ({Short})" : Short;
    /// <summary>"Mr. Fit's vehicle" for an AI vehicle, else the asset's short name.</summary>
    public string Title => Owner.Length > 0 ? $"{Owner}{(Owner.EndsWith('s') ? "'" : "'s")} vehicle" : Friendly.Length > 0 ? Friendly : Short;
    /// <summary>"World of Sports › Act 2 Burnin' Rubber" (the first place), or the section.</summary>
    public string Where => Places.Count > 0 ? Places[0].ToString() : Section;
    /// <summary>"World of Sports › Act 2 Burnin' Rubber › Mr. Fit's vehicle".</summary>
    public string Path => Where.Length > 0 ? $"{Where} › {Title}" : Title;
    public override string ToString() => Label;
}

/// <summary>A world / Act / challenge a game vehicle belongs to.</summary>
public sealed record VehiclePlace(string World, string Act, string Challenge)
{
    public string WorldName => WorldCatalog.DisplayNames.GetValueOrDefault(World, World);
    /// <summary>"Act 2", "Act WW", "" (world only), "Live".</summary>
    public string ActName => Act.Length == 0 ? "" : Act == "actww" ? "Act WW" : Act == "live" ? "Live" : Act.StartsWith("act") ? "Act " + Act[3..] : Act;
    /// <summary>"Act 2 Burnin' Rubber".</summary>
    public string ActLabel => (ActName + " " + Challenge).Trim();
    public override string ToString() => ActLabel.Length > 0 ? $"{WorldName} › {ActLabel}" : WorldName;
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
        var res = new Dictionary<uint, PregameVehicle>();
        ScanMarkers(ws.LoadResident, bundles, all, Names(idx), res);
        foreach (var v in res.Values)
            try { v.Parts = Load(ws, v).Blocks.Count; } catch { }
        return res.Values.OrderBy(v => v.Owner.Length > 0 ? 0 : 1).ThenBy(v => v.Owner).ThenBy(v => v.Asset).ToList();
    }

    static Dictionary<uint, string> Names(AssetIndex idx)
    {
        var names = new Dictionary<uint, string>();
        foreach (var e in idx.Entries) if (e.Id != 0) names.TryAdd(e.Id, e.Name);
        return names;
    }

    /// <summary>The vehicles resident in <paramref name="bundles"/> and those their markers (type 21) place, with drivers
    /// and the world / Act of the marker asset (aid_marker_banjox_&lt;world&gt;_&lt;act&gt;_…).</summary>
    static void ScanMarkers(Func<uint, CaffFile> load, IEnumerable<uint> bundles, Dictionary<uint, PregameVehicle> all, Dictionary<uint, string> names, Dictionary<uint, PregameVehicle> res)
    {
        foreach (var b in bundles.Select(b => b & 0xFFFFFF).Distinct())
        {
            foreach (var v in all.Values.Where(v => v.Bundles.Contains(b))) res.TryAdd(v.Id, v);
            CaffFile caff;
            try { caff = load(b); } catch { continue; }
            for (int s = 1; s <= caff.Symbols.Count; s++)
            {
                if (!caff.Symbols[s - 1].StartsWith("aid_marker_")) continue;
                MarkerAsset ma;
                try { ma = MarkerAsset.Parse(caff, s); } catch { continue; }
                var d = caff.PartsOf(s).First(p => caff.SectionOf(p).Name == ".data").Data;
                string marker = AssetIds.DisplayName(ma.Name).Replace("aid_marker_banjox_", "");
                var mp = marker.Split('_');
                foreach (var r in ma.Records.Where(r => r.Type == 21 && r.Size >= 0x44))
                {
                    uint bp = BE.U32(d, r.Offset + 0x38), driver = BE.U32(d, r.Offset + 0x3C);
                    if (bp == 0 || !all.TryGetValue(bp, out var v)) continue;
                    res.TryAdd(bp, v);
                    string who = driver != 0 && names.TryGetValue(driver, out var dn) ? DriverName(dn) : "";
                    if (who.Length > 0 && v.Owner.Length == 0) v.Owner = who;
                    v.Users.Add($"{(who.Length > 0 ? who + ", AI driver" : "placed")} — marker #{r.Index} of {marker}");
                    if (WorldCatalog.DisplayNames.ContainsKey(mp[0]))
                        AddPlace(v, mp[0], mp.Length > 1 && mp[1].StartsWith("act") ? mp[1] : "");
                }
            }
        }
    }

    static void AddPlace(PregameVehicle v, string world, string act)
    {
        if (v.Places.Any(p => p.World == world && p.Act == act)) return;
        if (act.Length > 0) v.Places.RemoveAll(p => p.World == world && p.Act.Length == 0);
        else if (v.Places.Any(p => p.World == world)) return;
        v.Places.Add(new VehiclePlace(world, act, ""));
    }

    /// <summary>
    /// Every game vehicle with where it belongs: the Acts whose markers place it (with its AI driver), else the Act or
    /// world bundle holding it, else the world its name starts with; the challenge from the game's text
    /// (challenge__&lt;world&gt;&lt;act&gt;game&lt;n&gt;, matched to the asset name: worldofsport_burninrubber_racer1 →
    /// "Burnin' Rubber"). Vehicles outside the worlds get a <see cref="PregameVehicle.Section"/> (shop blueprints, demo, …).
    /// Parts are counted for the vehicles the Acts use.
    /// </summary>
    public static List<PregameVehicle> Catalog(Workspace ws, AssetIndex idx, List<ActEntry>? acts = null, Func<uint, CaffFile>? load = null)
    {
        load ??= ws.LoadResident;
        var list = All(idx);
        var all = list.ToDictionary(v => v.Id);
        if (acts == null) try { acts = ActCatalog.Build(ws, idx); } catch { acts = new(); }
        var used = new Dictionary<uint, PregameVehicle>();
        ScanMarkers(load, acts.Select(a => a.ActBundle).Concat(acts.Select(a => a.WorldBundle)).Where(b => b != 0), all, Names(idx), used);
        var actOf = acts.GroupBy(a => a.ActBundle & 0xFFFFFF).ToDictionary(g => g.Key, g => g.First());
        var worldOf = WorldCatalog.FromIndex(idx).GroupBy(w => w.Bundle & 0xFFFFFF).ToDictionary(g => g.Key, g => g.First().World);
        var text = PartCatalog.LoadText(ws, "challenge__", "vehicle__");
        var challenges = Challenges(text.Where(t => t.Key.StartsWith("challenge__")).ToDictionary(t => t.Key, t => t.Value));
        foreach (var v in list)
        {
            if (v.Places.Count == 0) foreach (var b in v.Bundles) if (actOf.TryGetValue(b, out var a)) AddPlace(v, a.World, a.Act);
            string first = v.Short.Split('_')[0];
            if (v.Places.Count == 0 && WorldCatalog.DisplayNames.ContainsKey(first)) AddPlace(v, first, "");
            // a world bundle holding it (not the garage / front-end "car park" bundles; not shop, demo, … vehicles)
            if (v.Places.Count == 0 && first is "general" or "test" or "battlefield" or "tt" or "artisttestvehicle")
                foreach (var b in v.Bundles) if (worldOf.TryGetValue(b, out var w) && w != "carpark" && WorldCatalog.DisplayNames.ContainsKey(w)) AddPlace(v, w, "");
            for (int i = 0; i < v.Places.Count; i++)
                if (FindChallenge(challenges, v.Short, v.Places[i].World, v.Places[i].Act) is { } c)
                    v.Places[i] = v.Places[i] with { Act = v.Places[i].Act.Length > 0 ? v.Places[i].Act : c.Act, Challenge = c.Name };
            if (v.Places.Count == 0)
            {
                v.Section = SectionOf(first);
                if (first == "live" && FindChallenge(challenges, v.Short, null, "live") is { } lc)
                    v.Places.Add(new VehiclePlace(lc.World, "live", lc.Name));
            }
        }
        // every blueprint: its parts and the game's name for it
        foreach (var v in list)
            try
            {
                var bp = Load(load, v);
                v.Parts = bp.Blocks.Count;
                var n = bp.Name;
                if (text.TryGetValue("vehicle__" + n.ToLowerInvariant(), out var f)) v.Friendly = f;
                else if (n.Length > 2 && char.IsUpper(n[0]) && n.Contains(' ')) v.Friendly = n;   // "Red Baron", "Boat Chassis"
            }
            catch { }
        return list;
    }

    /// <summary>Reads resident bundles without touching the workspace's caches (for a build on another thread): the
    /// decompressed copy NB Studio keeps when it is there and complete, else the bundle file decompressed in memory. Each
    /// bundle is read once per loader.</summary>
    public static Func<uint, CaffFile> ReadOnlyLoader(Workspace ws)
    {
        var seen = new Dictionary<uint, CaffFile>();
        return b =>
        {
            b &= 0xFFFFFF;
            if (seen.TryGetValue(b, out var c)) return c;
            var path = ws.Game.ResidentPath(b);
            var raw = File.ReadAllBytes(path);
            if (XCompressFile.IsCompressed(raw))
            {
                var cache = System.IO.Path.Combine(ws.CacheDir, "4f", b.ToString("x6"));
                c = null;
                try { if (File.Exists(cache) && File.GetLastWriteTimeUtc(cache) >= File.GetLastWriteTimeUtc(path)) c = CaffFile.Read(File.ReadAllBytes(cache)); }
                catch { c = null; }   // being written by NB Studio right now: decompress it ourselves
                c ??= CaffFile.Read(XCompressFile.Decompress(raw));
            }
            else c = CaffFile.Read(raw);
            return seen[b] = c;
        };
    }

    const int CacheVersion = 1;
    static readonly JsonSerializerOptions CacheJson = new() { IncludeFields = true };
    sealed class CacheFile { public int Version; public string Key = ""; public List<PregameVehicle> Vehicles = new(); }

    static string CachePath(Workspace ws) => System.IO.Path.Combine(ws.CacheDir, "vehicle-catalog.json");

    /// <summary>The cache key of <see cref="Catalog"/>: size and time of every resident bundle that holds a vehicle or a
    /// marker, the common bundle (Act scripts) and the game text — any edit to them builds the list again.</summary>
    public static string CacheKey(Workspace ws, AssetIndex idx)
    {
        var sb = new System.Text.StringBuilder($"v{CacheVersion};");
        var bundles = idx.Entries.Where(e => !e.Streamed && (e.Type == "vehicle" || e.Name.StartsWith("aid_marker_"))).Select(e => e.Bundle & 0xFFFFFF)
            .Append(TestMode.CommonBundle & 0xFFFFFF).Distinct().OrderBy(b => b);
        foreach (var b in bundles)
        {
            var fi = new FileInfo(ws.Game.ResidentPath(b));
            sb.Append(b.ToString("x6")).Append(':').Append(fi.Exists ? fi.Length : -1).Append(':').Append(fi.Exists ? fi.LastWriteTimeUtc.Ticks : 0).Append(';');
        }
        var text = System.IO.Path.Combine(ws.Game.Root, "Debug", "11");
        if (Directory.Exists(text))
            foreach (var f in Directory.EnumerateFiles(text, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            {
                var fi = new FileInfo(f);
                sb.Append(fi.Length).Append(':').Append(fi.LastWriteTimeUtc.Ticks).Append(';');
            }
        return Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>The list saved by <see cref="SaveCache"/> when its key still matches, else null.</summary>
    public static List<PregameVehicle>? LoadCache(Workspace ws, string key)
    {
        try
        {
            var p = CachePath(ws);
            if (!File.Exists(p)) return null;
            var c = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(p), CacheJson);
            return c != null && c.Version == CacheVersion && c.Key == key && c.Vehicles.Count > 0 ? c.Vehicles : null;
        }
        catch { return null; }
    }

    public static void SaveCache(Workspace ws, string key, List<PregameVehicle> list)
    {
        try
        {
            Directory.CreateDirectory(ws.CacheDir);
            var p = CachePath(ws);
            File.WriteAllText(p + ".tmp", JsonSerializer.Serialize(new CacheFile { Version = CacheVersion, Key = key, Vehicles = list }, CacheJson));
            File.Move(p + ".tmp", p, true);
        }
        catch { }
    }

    static string SectionOf(string prefix) => prefix switch
    {
        "shop" => "Shop blueprints", "demo" => "Demo vehicles", "test" or "artisttestvehicle" => "Test vehicles",
        "credits" => "Credits vehicles", "live" => "Live challenges", "general" => "General vehicles",
        "custom" => "Reserved slots", _ => "Other vehicles",
    };

    sealed record Challenge(string World, string Act, string Name, string Norm);

    /// <summary>The game's challenges: challenge__&lt;world&gt;act&lt;n|ww&gt;game&lt;m&gt; and challenge__&lt;world&gt;live&lt;name&gt;.</summary>
    static List<Challenge> Challenges(Dictionary<string, string> text)
    {
        var res = new List<Challenge>();
        var worlds = WorldCatalog.DisplayNames.Keys.OrderByDescending(k => k.Length).ToList();
        var rx = new System.Text.RegularExpressions.Regex("^(act[0-9]+|actww)game[0-9]+$");
        foreach (var (key, name) in text)
        {
            var k = key["challenge__".Length..].ToLowerInvariant();
            var w = worlds.FirstOrDefault(k.StartsWith);
            if (w == null) continue;
            var rest = k[w.Length..];
            if (rx.IsMatch(rest)) res.Add(new Challenge(w, rest[..rest.IndexOf("game", StringComparison.Ordinal)], name, Norm(name)));
            else if (rest.StartsWith("live") && rest.Length > 4) res.Add(new Challenge(w, "live", name, Norm(name)));
        }
        return res.OrderBy(c => c.World).ThenBy(c => c.Act).ToList();
    }

    static string Norm(string s) => new string(s.ToLowerInvariant().Where(char.IsAsciiLetterLower).ToArray());

    /// <summary>The challenge a vehicle asset belongs to: a word of its name (digits dropped) inside the challenge's name
    /// (or the other way round), or sharing its first 7 letters (cpu_redbearracer → "Red Bear Racing").</summary>
    static Challenge? FindChallenge(List<Challenge> all, string shortName, string? world, string act)
    {
        var tokens = shortName.Split('_').Skip(1).Select(t => Norm(t)).Where(t => t.Length >= 5).ToList();
        if (tokens.Count == 0) return null;
        static int Common(string a, string b) { int i = 0; while (i < a.Length && i < b.Length && a[i] == b[i]) i++; return i; }
        bool Match(Challenge c) => tokens.Any(t => c.Norm.Contains(t) || (c.Norm.Length >= 6 && t.Contains(c.Norm)) || Common(t, c.Norm) >= 7);
        var pool = all.Where(c => (world == null || c.World == world) && (act == "live" ? c.Act == "live" : c.Act != "live")).ToList();
        return pool.FirstOrDefault(c => act.Length > 0 && c.Act == act && Match(c)) ?? pool.FirstOrDefault(c => Match(c) && (act.Length == 0 || act == "live"));
    }

    /// <summary>A display name for a driver objparams (actor_npc_worldofsport_mrfit → "Mr. Fit").</summary>
    public static string DriverName(string objparams)
    {
        var key = CharacterText.KeyOf(objparams);
        if (key == null) return AssetIds.DisplayName(objparams).Replace("aid_objparams_banjox_", "");
        var c = NB.Core.Mods.Characters.All.FirstOrDefault(c => c.Key == key);
        if (c != null) return c.Key == "thomas" ? "Thomas" : c.Key == "blubber" ? "Blubber" : c.Name;
        if (key.EndsWith("standing") && key.Length > 8) key = key[..^8];       // pikeletstanding: Pikelet
        var k = NB.Core.Mods.Characters.All.FirstOrDefault(c => c.Key == key);
        if (k != null) return k.Name;
        return char.ToUpperInvariant(key[0]) + key[1..];
    }

    static byte[] DataOf(CaffFile c, int sym) => c.PartsOf(sym).First(p => c.SectionOf(p).Name == ".data").Data;
    static int SymbolOf(CaffFile c, string asset) => c.Symbols.FindIndex(s => s == asset || AssetIds.DisplayName(s) == asset) + 1;

    /// <summary>Reads the blueprint (from the first bundle that holds it).</summary>
    public static Blueprint Load(Workspace ws, PregameVehicle v) => Load(ws.LoadResident, v);

    public static Blueprint Load(Func<uint, CaffFile> load, PregameVehicle v)
    {
        foreach (var b in v.Bundles)
        {
            var c = load(b);
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
