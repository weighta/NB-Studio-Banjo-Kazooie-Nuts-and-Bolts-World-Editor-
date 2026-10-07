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

    PartCatalog(Workspace ws, AssetIndex idx) { Workspace = ws; Index = idx; }

    public static PartCatalog Load(Workspace ws, AssetIndex? idx = null)
    {
        idx ??= AssetIndex.LoadOrBuild(ws);
        var cat = new PartCatalog(ws, idx);
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
            cat.Parts[p.Id] = p;
        }
        return cat;
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
    Dictionary<string, string> LoadNames()
    {
        var res = new Dictionary<string, string>();
        try
        {
            var dir = Path.Combine(Workspace.Game.Root, "Debug", "11");
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
                        if (t.Names.TryGetValue(k, out var key) && (key.StartsWith("block__") || key.StartsWith("dialog__desc_block_")) && s.Length > 0)
                            res.TryAdd(key, s.Replace("{PAGEBREAK}", " ").Trim());
                }
                catch { }
            }
        }
        catch { }
        return res;
    }

    CaffFile? Caff(uint bundle)
    {
        bundle &= 0xFFFFFF;
        if (_caffs.TryGetValue(bundle, out var c)) return c;
        try { c = Workspace.LoadResident(bundle); } catch { c = null; }
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
    public ModelAsset? RawModel(uint modelId)
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
    public ModelAsset? Model(PartInfo p)
    {
        string key = p.ModelId.ToString("X8") + "|" + string.Join(",", p.Switches.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}"));
        if (_shown.TryGetValue(key, out var shown)) return shown;
        var m = RawModel(p.ModelId);
        if (m == null) return _shown[key] = null;
        if (!_views.TryGetValue(p.ModelId, out var view)) _views[p.ModelId] = view = PartModelView.Of(m);
        var draws = m.Draws.Where((d, i) => view.Visible(i, p.Switches)).ToList();
        if (draws.Count == 0) draws = m.Draws.Where(d => !m.LodOnlyNodes.Contains(d.Node)).ToList();
        shown = new ModelAsset { View = m.View, Chunks = m.Chunks, Nodes = m.Nodes, Draws = draws, TextureTable = m.TextureTable, TextureByIndex = m.TextureByIndex };
        return _shown[key] = shown;
    }

    public PartInfo? this[uint id] => Parts.GetValueOrDefault(id);

    /// <summary>Blueprint category byte for a part (objparams +0x98 + 1).</summary>
    public byte CategoryByte(uint id) => Parts.TryGetValue(id, out var p) ? (byte)(p.Category + 1) : (byte)0;

    /// <summary>Weight of a part (objparams +0x190), null when unknown.</summary>
    public float? WeightOf(uint id) => Parts.TryGetValue(id, out var p) ? p.Weight : null;
}
