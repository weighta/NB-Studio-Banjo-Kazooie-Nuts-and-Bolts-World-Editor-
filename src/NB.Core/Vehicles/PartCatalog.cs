using NB.Core.Formats;
using NB.Core.IO;
using NB.Core.Models;
using NB.Core.Parts;
using NB.Core.Project;

namespace NB.Core.Vehicles;

/// <summary>One vehicle part type: an <c>aid_objparams_banjox_vehicleblock_*</c> record (objDefId_vehicleBlock* classes).</summary>
public sealed class PartInfo
{
    public uint Id;
    public string Asset = "", Key = "", Class = "", Name = "", Description = "";
    /// <summary>objparams +0x98 (a blueprint part record stores this + 1).</summary>
    public int Category;
    /// <summary>objparams +0x228 / +0x268: garage group ("wheel", "engine", "body", …) and variant.</summary>
    public string Group = "", Variant = "";
    /// <summary>Default colour (objparams +0x130, a paint_colours name) and its RGBA.</summary>
    public string ColourName = "";
    public uint DefaultPaint = 0xC0C0C0FF;
    public float Weight, Health;
    public uint ModelId, AttachId;
    public uint Bundle;
    public AttachData? Attach;
    /// <summary>Not one of the game's shipped parts (ULTRA / modded parts made with the Part Importer).</summary>
    public bool Modded;
    /// <summary>Model switches the part turns on (see <see cref="PartModelView"/>).</summary>
    public Dictionary<int, int> Switches = new();

    /// <summary>In the game's Parts Store: listed in at least one blockset (the crates, keys, start pack and
    /// blockset_all that fill the garage inventory). Parts outside every blockset are the game's internal / AI variants.</summary>
    public bool InStore;
    /// <summary>Parts Store category (<see cref="PartCatalog.StoreCategories"/>), "Modded / ULTRA" or "Other".</summary>
    public string StoreCategory = "";
    /// <summary>Sort position of <see cref="StoreCategory"/>, then of the part inside it.</summary>
    public int StoreOrder, Tier;
    /// <summary>A seat the game's AI drivers use (secondaryseats_large / _small: variants passengerlargeai / passengersmallai).</summary>
    public bool IsAiSeat => Class == "objDefId_vehicleBlockSeat" && Variant.EndsWith("ai");
    /// <summary>One of the versions the game's AI vehicles use: *_ai_* engines and jets, the AI spring, the AI seats.</summary>
    public bool IsAiVariant => IsAiSeat || Key.Contains("_ai_") || Key == "gadgets_springai";
    /// <summary>The AI version of a player part (Medium Engine → Medium Engine (AI)), 0 when there is none.</summary>
    public uint AiVersion;
    /// <summary>Wheels: how far the wheel hangs below its rest pose when the suspension is fully extended (objparams +0x400:
    /// standard 0.5, high grip 0.64, super 0.54, monster 0.945; the editor shows <see cref="PartCatalog.WheelTravel"/> of it).</summary>
    public float SuspensionTravel;
    /// <summary>The orientation a new part of this kind gets: the one the game's own vehicles use for parts with a "bottom"
    /// (springs: 85 of the 105 springs in the 274 game blueprints face down, o2 / o14 / o23; o2 is the most common).</summary>
    public int DefaultOrientation;
    public string SizeText { get { var (x, y, z) = Size; return $"{x}×{y}×{z}"; } }

    public bool IsWheel => Class == "objDefId_vehicleBlockWheel";
    public bool IsPropeller => Class == "objDefId_vehicleBlockJetEngine" && Key.Contains("propeller");
    /// <summary>Settings the garage offers (blueprint part +5): wheels and propellers.</summary>
    public IReadOnlyList<(byte Value, string Name)> Settings =>
        IsWheel ? VehicleSettings.Wheel : IsPropeller ? VehicleSettings.Propeller : Array.Empty<(byte, string)>();
    public (int X, int Y, int Z) Size => Attach?.Size ?? (1, 1, 1);
    public override string ToString() => Name.Length > 0 ? Name : Key;
}

/// <summary>Settings of a part (blueprint part +5): the index into the garage's setting list (tables 0x821242A0 wheels /
/// 0x82124598 propellers: char[32] text id + u32). Verified in Xenia (Showdown Town, a Super-engine vehicle, RT 3 s):
/// wheels set to 1 drove 88 units, set to 2 did not move (steering only, no drive).</summary>
public static class VehicleSettings
{
    public static readonly (byte, string)[] Wheel = { (0, "Automatic"), (1, "Driven"), (2, "Steering"), (3, "Driven & Steering"), (4, "Freewheeling") };
    public static readonly (byte, string)[] Propeller = { (0, "Automatic"), (1, "Push"), (2, "Pull") };

    public static string NameOf(byte v, bool propeller = false) => (propeller ? Propeller : Wheel).FirstOrDefault(x => x.Item1 == v).Item2 ?? $"setting {v}";
}

/// <summary>
/// Every vehicle part of a workspace (shipped and modded), the garage paint palette (aid_misc_banjox_paintcolours_*:
/// 0x28-byte records char[32] name, u32 hash, u32 RGBA) and the part models.
/// </summary>
public sealed class PartCatalog
{
    public readonly Workspace Workspace;
    public readonly AssetIndex Index;
    public readonly Dictionary<uint, PartInfo> Parts = new();
    public readonly List<(string Name, uint Rgba)> Palette = new();
    readonly Dictionary<uint, List<AssetEntry>> _byId = new();
    readonly Dictionary<uint, CaffFile?> _caffs = new();
    readonly Dictionary<uint, ModelAsset?> _models = new();

    /// <summary>How bundles are read: the workspace's loader, or one that leaves NB Studio's caches alone (a catalog built on
    /// another thread, <see cref="PregameVehicles.ReadOnlyLoader"/>).</summary>
    readonly Func<uint, CaffFile> _load;

    PartCatalog(Workspace ws, AssetIndex idx, Func<uint, CaffFile>? load) { Workspace = ws; Index = idx; _load = load ?? ws.LoadResident; }

    /// <summary>Every part of the workspace. With <paramref name="load"/> (a read-only loader) it can be built on another
    /// thread while NB Studio works on: it then only reads files.</summary>
    public static PartCatalog Load(Workspace ws, AssetIndex? idx = null, Func<uint, CaffFile>? load = null)
    {
        idx ??= AssetIndex.LoadOrBuild(ws);
        var cat = new PartCatalog(ws, idx, load);
        foreach (var e in idx.Entries)
            if (!e.Streamed && e.Symbol > 0 && e.Id != 0) { if (!cat._byId.TryGetValue(e.Id, out var l)) cat._byId[e.Id] = l = new(); l.Add(e); }
        cat.LoadPalette();
        var names = cat.LoadNames();
        var shipped = ShippedParts(ws);
        foreach (var e in idx.Entries.Where(e => !e.Streamed && e.Symbol > 0 && e.Name.StartsWith("aid_objparams_banjox_vehicleblock_")))
        {
            if (cat.Parts.ContainsKey(e.Id)) continue;
            var d = cat.Data(e);
            if (d == null || d.Length < 0x2A8 || !BE.CStr(d, 0x42, 62).StartsWith("objDefId_vehicleBlock")) continue;
            var p = new PartInfo
            {
                Id = e.Id, Asset = e.Name, Key = e.Name["aid_objparams_banjox_vehicleblock_".Length..], Bundle = e.Bundle,
                Class = BE.CStr(d, 0x42, 62), Category = (int)BE.U32(d, 0x98),
                Group = BE.CStr(d, 0x228, 64), Variant = BE.CStr(d, 0x268, 64), ColourName = BE.CStr(d, 0x130, 32),
                Weight = BE.F32(d, 0x190), Health = BE.F32(d, 0x194), ModelId = BE.U32(d, 0x124), AttachId = BE.U32(d, 0x12C),
            };
            string tag = BE.CStr(d, 0xA0, 64);
            p.Name = names.GetValueOrDefault("block__" + tag) ?? Pretty(p.Key);
            p.Description = names.GetValueOrDefault("dialog__desc_block_" + tag) ?? BE.CStr(d, 0xE0, 64);
            p.DefaultPaint = cat.Palette.FirstOrDefault(c => c.Name == p.ColourName).Rgba is uint rgba and not 0 ? rgba : 0xC0C0C0FF;
            p.Modded = shipped != null && !shipped.Contains(p.Id);
            if (cat.FirstData(p.AttachId) is { } ad) try { p.Attach = AttachData.Parse(ad); } catch { }
            p.Switches = PartModelView.SwitchesOf(p, d);
            if (p.IsWheel) { float t = BE.F32(d, 0x400); if (t is > 0 and < 5) p.SuspensionTravel = t; }
            if (p.Class == "objDefId_vehicleBlockSpring") p.DefaultOrientation = 2;
            cat.Parts[p.Id] = p;
        }
        // player part → AI version (the game's AI racers use these: engines and jets +25 % power, the AI spring 6× stiffer)
        var byKey = cat.Parts.Values.GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First());
        foreach (var p in cat.Parts.Values)
        {
            string? ai = p.Key.StartsWith("propulsion_engines_") && !p.Key.Contains("_ai_") ? p.Key.Replace("propulsion_engines_", "propulsion_engines_ai_")
                : p.Key.StartsWith("propulsion_jets_") && !p.Key.Contains("_ai_") ? p.Key.Replace("propulsion_jets_", "propulsion_jets_ai_")
                : p.Key == "gadgets_spring" ? "gadgets_springai" : null;
            if (ai != null && byKey.TryGetValue(ai, out var v)) p.AiVersion = v.Id;
        }
        var store = cat.StoreParts();
        foreach (var p in cat.Parts.Values)
        {
            p.InStore = store.Contains(p.Id);
            if (DisplayNames.TryGetValue(p.Key, out var dn) && !p.Modded) p.Name = dn;
            else if (p.IsAiSeat && !p.Name.Contains("(AI")) p.Name += " (AI driver)";
            p.Tier = TierOf(p.Key + "_" + p.Variant);
            int i = Array.FindIndex(StoreCategories, c => c.Group == p.Group);
            // the AI driver seats are not sold, but they are the seats of the game's AI vehicles: listed with the seats
            (p.StoreCategory, p.StoreOrder) = p.Modded ? (ModdedCategory, StoreCategories.Length)
                : (p.InStore || p.IsAiSeat) && i >= 0 ? (StoreCategories[i].Name, i) : (OtherCategory, StoreCategories.Length + 1);
        }
        return cat;
    }

    /// <summary>The Parts Store categories in the garage's order: name and the objparams group (+0x228) they hold
    /// (loctext garage__grouping_* / block__group_*).</summary>
    public static readonly (string Name, string Group)[] StoreCategories =
    {
        ("Seats", "seat"), ("Wheels", "wheel"), ("Power", "engine"), ("Fuel", "fuel"), ("Storage", "storage"), ("Ammo", "ammo"),
        ("Body", "body"), ("Gadgets", "gadget"), ("Protection", "protection"), ("Fly and Float", "flyandfloat"),
        ("Weapons", "weapon"), ("Accessories", "accessory"),
    };
    public const string ModdedCategory = "Modded / ULTRA", OtherCategory = "Other (not in the store)";

    /// <summary>Clear names for the shipped parts whose loctext name is missing or shared with a Parts Store part
    /// (internal, AI and special variants: they sort into <see cref="OtherCategory"/>).</summary>
    public static readonly Dictionary<string, string> DisplayNames = new()
    {
        ["base_attachpoint"] = "Attach Point (internal)", ["base_gameplaycreatorloactor"] = "Gameplay Creator Actor (internal)",
        ["base_leakpoint"] = "Leak Point (internal)", ["miscellaneous_logolympictorch"] = "L.O.G.'s Olympic Torch",
        ["miscellaneous_gameplaycreatorpart"] = "Gameplay Creator Part 1 (Critics Say No)",
        ["miscellaneous_gameplaycreatorpart2"] = "Gameplay Creator Part 2 (Critics Say No)",
        ["miscellaneous_gameplaycreatorpart3"] = "Gameplay Creator Part 3 (Critics Say No)",
        ["miscellaneous_storage_eggnspoontray"] = "Egg 'N' Spoon Tray",
        ["propulsion_engines_ai_smallpower"] = "Small Engine (AI)", ["propulsion_engines_ai_mediumpower"] = "Medium Engine (AI)",
        ["propulsion_engines_ai_largepower"] = "Large Engine (AI)", ["propulsion_engines_ai_superpower"] = "Super Engine (AI)",
        ["propulsion_jets_ai_small"] = "Small Jet (AI)", ["propulsion_jets_ai_large"] = "Large Jet (AI)",
        ["body_light_poleconnector"] = "Light Pole Connector",
        ["seats_standardcutscene"] = "Standard Seat (cutscene)",
        ["secondaryseats_large"] = "Large Taxi Seat (AI driver)", ["secondaryseats_small"] = "Small Taxi Seat (AI driver)",
        ["secondaryseats_grunty"] = "Grunty's Seat", ["secondaryseats_gruntyairtight"] = "Grunty's Seat (airtight)",
        ["secondaryseats_pikelet"] = "Pikelet's Seat", ["secondaryseats_pikeletpassenger"] = "Pikelet's Passenger Seat",
        ["wheels_highgripheavy"] = "High Grip Wheel (heavy)", ["weapon_eggturretfixed"] = "Egg Turret (fixed)",
        ["gadgets_variants_energyshieldnobghits"] = "Energy Shield (variant)", ["gadgets_variants_spotlightalwayson"] = "Spotlight (always on)",
        ["gadgets_springai"] = "Spring (AI)", ["gadgets_springtrolley"] = "Spring (trolley)",
        ["miscellaneous_weights_floatergrunty"] = "Floater (Grunty)",
    };

    /// <summary>Parts in the Parts Store's order: category (Seats … Accessories, Modded / ULTRA, Other), then the kind
    /// (standard / light first, then strong / heavy / high grip, super, special; trays before boxes; engines, jets, sail),
    /// size (small, medium, large, super) and shape (cube, wedge, corner, panels, poles), then name.</summary>
    public static IEnumerable<PartInfo> StoreSorted(IEnumerable<PartInfo> parts) =>
        parts.OrderBy(p => p.StoreOrder).ThenBy(p => p.StoreCategory == OtherCategory ? p.Name : "").ThenBy(p => KindRank.GetValueOrDefault(p.Variant, 10)).ThenBy(p => KindRank.ContainsKey(p.Variant) ? "" : p.Name)
             .ThenBy(p => p.Tier).ThenBy(p => ShapeRank(p.Key)).ThenBy(p => p.Name).ThenBy(p => p.Key);

    static readonly Dictionary<string, int> KindRank = new()
    {
        ["standard"] = 0, ["light"] = 0, ["engines"] = 0, ["small"] = 0, ["lowloader"] = 0,
        ["strong"] = 1, ["heavy"] = 1, ["medium"] = 1, ["highgrip"] = 1, ["jets"] = 1, ["largelowloader"] = 1,
        ["large"] = 2, ["box"] = 2, ["super"] = 3, ["largebox"] = 3, ["airtight"] = 4, ["monster"] = 4, ["fuelfree"] = 5,
        ["passenger"] = 6, ["passengersmallai"] = 7, ["passengerlargeai"] = 7, ["passengerlarge"] = 8,
    };

    static int ShapeRank(string key)
    {
        foreach (var (k, r) in new[] { ("90degreepanel", 4), ("tpanel", 5), ("panel", 3), ("90degreepole", 7), ("tpole", 8), ("poleconnector", 9), ("pole", 6), ("cube", 0), ("wedge", 1), ("corner", 2) })
            if (key.EndsWith(k)) return r;
        return 0;
    }

    /// <summary>Order inside a category, like the store: small / standard / light first, then medium / heavy, large, super.</summary>
    static int TierOf(string k) =>
        k.Contains("super") ? 3 : k.Contains("large") ? 2 : k.Contains("medium") || k.Contains("heavy") || k.Contains("strong") ? 1 : 0;

    /// <summary>Parts listed in any blockset (aid_misc_banjox_blockset_*: big-endian u32 pairs count, part objparams id).</summary>
    HashSet<uint> StoreParts()
    {
        var res = new HashSet<uint>();
        foreach (var g in Index.Entries.Where(e => !e.Streamed && e.Symbol > 0 && e.Name.StartsWith("aid_misc_banjox_blockset_")).GroupBy(e => e.Name))
        {
            byte[]? d = null;
            foreach (var e in g) if ((d = Data(e)) != null) break;
            if (d == null) continue;
            for (int o = 0; o + 8 <= d.Length; o += 8) res.Add(BE.U32(d, o + 4));
        }
        return res;
    }

    /// <summary>English game text by key prefix (loctext of Debug/11; first language found wins, as for part names).</summary>
    public static Dictionary<string, string> LoadText(Workspace ws, params string[] prefixes)
    {
        var res = new Dictionary<string, string>();
        try
        {
            var dir = Path.Combine(ws.Game.Root, "Debug", "11");
            if (!Directory.Exists(dir)) return res;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var c = CaffFile.Read(File.ReadAllBytes(f));
                    var part = c.Parts.FirstOrDefault(p => c.SectionOf(p).Name == ".data");
                    if (part == null || !LocText.Is(part.Data)) continue;
                    var t = LocText.Parse(part.Data);
                    foreach (var (k, s) in t.Strings)
                        if (t.Names.TryGetValue(k, out var key) && prefixes.Any(key.StartsWith) && s.Length > 0)
                            res.TryAdd(key, s.Replace("{PAGEBREAK}", " ").Trim());
                }
                catch { }
            }
        }
        catch { }
        return res;
    }

    static string Pretty(string key) =>
        string.Join(' ', key.Split('_').Skip(key.Contains('_') ? 1 : 0).Select(w => w.Length > 0 ? char.ToUpperInvariant(w[0]) + w[1..] : w));

    /// <summary>The 153 parts the game ships (objparams ids of every aid_objparams_banjox_vehicleblock_* in the original
    /// Bundle/4f/685374): parts of a workspace that are not among them are modded (ULTRA parts, Part Importer).</summary>
    public static readonly HashSet<uint> Shipped = new()
    {
        0x1F0002C7, 0x1F0141E8, 0x1F022830, 0x1F029162, 0x1F042115, 0x1F079766, 0x1F09D39A, 0x1F0A9F36, 0x1F0C59FD, 0x1F0EB2AB,
        0x1F0FD73C, 0x1F1064AF, 0x1F10C777, 0x1F123ED4, 0x1F127A1B, 0x1F130448, 0x1F133769, 0x1F1AE81C, 0x1F1B3EEE, 0x1F1C34C3,
        0x1F1D6CAD, 0x1F207106, 0x1F242A59, 0x1F287929, 0x1F29AE62, 0x1F2AE2B8, 0x1F2B227B, 0x1F2BF215, 0x1F2CAD9B, 0x1F2E95E7,
        0x1F31CA4C, 0x1F31EFAB, 0x1F331128, 0x1F33AF00, 0x1F3644E5, 0x1F37131C, 0x1F37BB7E, 0x1F390610, 0x1F3BA23B, 0x1F3E8E01,
        0x1F4178C9, 0x1F41AAC0, 0x1F4633B3, 0x1F48B278, 0x1F4BB5E1, 0x1F4BC61E, 0x1F4D875E, 0x1F4E68C7, 0x1F4F0110, 0x1F4FA2EC,
        0x1F506D0F, 0x1F51E31C, 0x1F54E704, 0x1F5591E5, 0x1F583430, 0x1F585A51, 0x1F5B8D3C, 0x1F5D961C, 0x1F5F9098, 0x1F603CC3,
        0x1F62B405, 0x1F636B72, 0x1F63BDE6, 0x1F646AE2, 0x1F658ECD, 0x1F6671D5, 0x1F6885C3, 0x1F6889CD, 0x1F6D416D, 0x1F6EFC34,
        0x1F7056FB, 0x1F72D497, 0x1F73F0D7, 0x1F74119D, 0x1F749757, 0x1F780CED, 0x1F7A892D, 0x1F7CFEF4, 0x1F80AC26, 0x1F817BC9,
        0x1F841B45, 0x1F84FD7F, 0x1F8517A9, 0x1F85D4CE, 0x1F8A6690, 0x1F8B1BB8, 0x1F8D1F16, 0x1F8DF274, 0x1F941C9B, 0x1F948B1B,
        0x1F953740, 0x1F95D087, 0x1F978E81, 0x1F97C327, 0x1F982C42, 0x1F98FC81, 0x1F9BF8B5, 0x1F9E77F8, 0x1FA0396E, 0x1FA336A3,
        0x1FAA7271, 0x1FAE2C77, 0x1FAF05C6, 0x1FB27042, 0x1FB56B94, 0x1FB58382, 0x1FB5EC7B, 0x1FB798FD, 0x1FB94FC8, 0x1FBB5062,
        0x1FBBAF2A, 0x1FBC60F4, 0x1FBC6E21, 0x1FBE5755, 0x1FBEA986, 0x1FBF354B, 0x1FC071F0, 0x1FC180AF, 0x1FC1B68A, 0x1FC39E27,
        0x1FC597FB, 0x1FC6C417, 0x1FC89446, 0x1FCC7461, 0x1FD1698C, 0x1FD1984E, 0x1FD199A6, 0x1FD546B8, 0x1FD5DC3E, 0x1FD61511,
        0x1FD9EB7F, 0x1FE2C10E, 0x1FE338DD, 0x1FE80FCB, 0x1FEA444A, 0x1FEA640E, 0x1FEAC889, 0x1FEB371C, 0x1FEF20AD, 0x1FF03006,
        0x1FF1B874, 0x1FF35BB8, 0x1FF360D6, 0x1FF3E64A, 0x1FF6B387, 0x1FF7BB6C, 0x1FFC7505, 0x1FFC8DF8, 0x1FFD800C, 0x1FFEC0B5,
        0x1FFEE6EE, 0x1FFF3B45, 0x1FFFCF5F
    };

    static HashSet<uint> ShippedParts(Workspace ws) => Shipped;

    void LoadPalette()
    {
        foreach (var e in Index.Entries.Where(e => !e.Streamed && e.Symbol > 0 && e.Name.StartsWith("aid_misc_banjox_paintcolours_")).GroupBy(e => e.Name).Select(g => g.First()))
        {
            var d = Data(e);
            if (d == null) continue;
            for (int o = 0; o + 0x28 <= d.Length; o += 0x28)
            {
                var n = BE.CStr(d, o, 32);
                if (n.Length == 0 || Palette.Any(p => p.Name == n)) continue;
                Palette.Add((n, BE.U32(d, o + 0x24)));
            }
        }
    }

    /// <summary>English part names and descriptions (loctext "block__&lt;tag&gt;", "dialog__desc_block_&lt;tag&gt;").</summary>
    Dictionary<string, string> LoadNames() => LoadText(Workspace, "block__", "dialog__desc_block_");

    CaffFile? Caff(uint bundle)
    {
        bundle &= 0xFFFFFF;
        if (_caffs.TryGetValue(bundle, out var c)) return c;
        try { c = _load(bundle); } catch { c = null; }
        return _caffs[bundle] = c;
    }

    byte[]? Data(AssetEntry e) => Caff(e.Bundle) is { } c ? c.PartsOf(e.Symbol).FirstOrDefault(p => c.SectionOf(p).Name == ".data")?.Data : null;

    byte[]? FirstData(uint id)
    {
        if (id == 0 || !_byId.TryGetValue(id, out var l)) return null;
        foreach (var e in l.OrderBy(x => x.Bundle == 0x685374 ? 0 : 1)) if (Data(e) is { } d) return d;
        return null;
    }

    /// <summary>Bundles that hold an asset resident (for validation: is a part loadable in a world?).</summary>
    public IEnumerable<uint> HoldersOf(uint id) => _byId.TryGetValue(id, out var l) ? l.Select(e => e.Bundle & 0xFFFFFF).Distinct() : Enumerable.Empty<uint>();

    /// <summary>The part's model as stored (from any bundle that holds it resident; the town bundle first), parsed once.</summary>
    /// <summary>Models are parsed under this lock: a catalog handed over by a background build may still be reading the
    /// rest of its models while the editor asks for one.</summary>
    readonly object _modelLock = new();

    public ModelAsset? RawModel(uint modelId)
    {
        lock (_modelLock) return RawModelLocked(modelId);
    }

    ModelAsset? RawModelLocked(uint modelId)
    {
        if (_models.TryGetValue(modelId, out var m)) return m;
        m = null;
        if (_byId.TryGetValue(modelId, out var l))
            foreach (var e in l.OrderBy(x => x.Bundle == 0x234CEC ? 0 : x.Bundle == 0x685374 ? 1 : 2))
            {
                if (Caff(e.Bundle) is not { } c) continue;
                try { m = ModelAsset.Parse(c, e.Symbol); break; } catch { }
            }
        return _models[modelId] = m;
    }

    readonly Dictionary<string, ModelAsset?> _shown = new();
    readonly Dictionary<uint, PartModelView> _views = new();

    /// <summary>The part's model as the game shows it on a vehicle: level-0 detail, the part's switch options only
    /// (<see cref="PartModelView"/>). A filtered copy of the stored model (same draws), cached per model and switches.</summary>
    /// <summary>How far the wheels hang out of their bind pose (fork pushed up), as a share of their suspension travel: 0.6
    /// matches Mumbo's garage by eye (a gap of about a third of the tyre under the part above); 0 = the bind pose.</summary>
    public static float WheelTravel = 0.6f;

    public ModelAsset? Model(PartInfo p)
    {
        lock (_modelLock) return ModelLocked(p);
    }

    ModelAsset? ModelLocked(PartInfo p)
    {
        string key = p.ModelId.ToString("X8") + "|" + string.Join(",", p.Switches.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")) + "|" + p.SuspensionTravel * WheelTravel;
        if (_shown.TryGetValue(key, out var shown)) return shown;
        var m = RawModel(p.ModelId);
        if (m == null) return _shown[key] = null;
        if (!_views.TryGetValue(p.ModelId, out var view)) _views[p.ModelId] = view = PartModelView.Of(m);
        var draws = m.Draws.Where((d, i) => view.Visible(i, p.Switches)).ToList();
        if (draws.Count == 0) draws = m.Draws.Where(d => !m.LodOnlyNodes.Contains(d.Node)).ToList();
        if (WheelTravel > 0 && p.SuspensionTravel > 0) draws = PartModelView.Extended(m, draws, p.SuspensionTravel * WheelTravel);
        shown = new ModelAsset { View = m.View, Chunks = m.Chunks, Nodes = m.Nodes, Draws = draws, TextureTable = m.TextureTable, TextureByIndex = m.TextureByIndex };
        return _shown[key] = shown;
    }

    public PartInfo? this[uint id] => Parts.GetValueOrDefault(id);

    /// <summary>Blueprint category byte for a part (objparams +0x98 + 1).</summary>
    public byte CategoryByte(uint id) => Parts.TryGetValue(id, out var p) ? (byte)(p.Category + 1) : (byte)0;

    /// <summary>Weight of a part (objparams +0x190), null when unknown.</summary>
    public float? WeightOf(uint id) => Parts.TryGetValue(id, out var p) ? p.Weight : null;
}
