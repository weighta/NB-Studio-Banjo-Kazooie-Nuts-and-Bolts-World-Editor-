using NB.Core.Compression;
using NB.Core.Formats;
using NB.Core.IO;
using NB.Core.Project;

namespace NB.Core.World;

/// <summary>
/// The values of one light setup (colours are 0xRRGGBB). Verified in Xenia for Showdown Town (snow/research/light/REPORT.md):
/// ambient, sun colour, sun intensity, fog start / end / max opacity and fog colour. Sun elevation / azimuth (radians):
/// direction towards the sun = (-cos e sin a, sin e, -cos e cos a), the game's sun shader constant c51 (render research);
/// see work/agent_src/studio19/MERGE.md for the in-game check of a changed sun direction. The fog switch is read back only.
/// The fill light is the light setup's op 0x7E (a second directional light; off in Showdown Town, where its direction is
/// the sun's mirrored: same elevation, azimuth + pi). HasFill is false when the setup has no op 0x7E.
/// </summary>
public sealed record LightValues(uint Ambient, uint Sun, float Intensity, float FogStart, float FogEnd, float FogMax, uint FogColour,
    float SunElevation, float SunAzimuth, bool FogOn, bool HasFill = false, bool FillOn = false, uint FillColour = 0,
    float FillIntensity = 0, float FillElevation = 0, float FillAzimuth = 0)
{
    public static string Hex(uint rgb) => (rgb & 0xFFFFFF).ToString("X6");
    public override string ToString() =>
        $"ambient {Hex(Ambient)}, sun {Hex(Sun)} x{Intensity:G4}, fog {Hex(FogColour)} {FogStart:G5}..{FogEnd:G5} max {FogMax:G3}";
}

/// <summary>
/// A light-setup script (aid_script_banjox_lightsetup_*, run by a level or time-of-day script with op 0x52): a 0x24-byte
/// header (+0x08 ambient RGB0, +0x0C sun colour RGB0, +0x10 / +0x14 sun elevation / azimuth in radians, +0x1C sun
/// intensity) followed by script commands; command 0x53 is the fog (+0x08 on, +0x0C start, +0x10 end, +0x14 max opacity
/// 0..1, +0x24 colour RGB0). Edits are same-size writes into the bundle's CAFF part.
/// </summary>
public sealed class LightSetup
{
    public string Name = "";
    public uint Bundle, Id;
    public int Symbol;
    /// <summary>The asset's .data part (edited in place).</summary>
    public byte[] Data = Array.Empty<byte>();
    public int FogCommand;

    public const int Ambient = 0x08, Sun = 0x0C, SunElevation = 0x10, SunAzimuth = 0x14, Intensity = 0x1C;
    public const int FogOn = 0x08, FogStart = 0x0C, FogEnd = 0x10, FogMax = 0x14, FogColour = 0x24;
    /// <summary>Fill light command (op 0x7E): +0x08 flag (0 = on), +0x0C colour RGB0, +0x10 / +0x14 elevation / azimuth,
    /// +0x1C intensity (render research: the game's shader constants c81 / c82).</summary>
    public const int FillFlag = 0x08, FillColour = 0x0C, FillElevation = 0x10, FillAzimuth = 0x14, FillIntensity = 0x1C;

    /// <summary>Offset of the first command <paramref name="op"/> of at least <paramref name="minSize"/> bytes, or -1.</summary>
    public static int FindOp(byte[] d, int op, int minSize)
    {
        if (d.Length < 0x24 + 8 || BE.S32(d, 0) != 0x24) return -1;
        for (int o = 0x24; o + 8 <= d.Length;)
        {
            int size = BE.S32(d, o), k = BE.S32(d, o + 4);
            if (size < 8 || o + size > d.Length || k == 0) return -1;
            if (k == op && size >= minSize) return o;
            o += size;
        }
        return -1;
    }

    /// <summary>Offset of the fill light command (op 0x7E), or -1.</summary>
    public int FillCommand => FindOp(Data, 0x7E, 0x20);

    /// <summary>Offset of the fog command (op 0x53) or -1 when <paramref name="d"/> is not a light setup.</summary>
    public static int FindFog(byte[] d)
    {
        if (d.Length < 0x24 + 8 || BE.S32(d, 0) != 0x24) return -1;
        int o = 0x24;
        while (o + 8 <= d.Length)
        {
            int size = BE.S32(d, o), op = BE.S32(d, o + 4);
            if (size < 8 || o + size > d.Length) return -1;
            if (op == 0x53 && size >= 0x28) return o;
            if (op == 0) break;
            o += size;
        }
        return -1;
    }

    public LightValues Values
    {
        get
        {
            int f = FillCommand;
            return new(BE.U32(Data, Ambient) >> 8, BE.U32(Data, Sun) >> 8, BE.F32(Data, Intensity),
                BE.F32(Data, FogCommand + FogStart), BE.F32(Data, FogCommand + FogEnd), BE.F32(Data, FogCommand + FogMax),
                BE.U32(Data, FogCommand + FogColour) >> 8, BE.F32(Data, SunElevation), BE.F32(Data, SunAzimuth), BE.U32(Data, FogCommand + FogOn) != 0,
                f >= 0, f >= 0 && BE.U32(Data, f + FillFlag) == 0, f >= 0 ? BE.U32(Data, f + FillColour) >> 8 : 0,
                f >= 0 ? BE.F32(Data, f + FillIntensity) : 0, f >= 0 ? BE.F32(Data, f + FillElevation) : 0, f >= 0 ? BE.F32(Data, f + FillAzimuth) : 0);
        }
        set
        {
            BE.W32(Data, Ambient, (value.Ambient & 0xFFFFFF) << 8);
            BE.W32(Data, Sun, (value.Sun & 0xFFFFFF) << 8);
            BE.WF32(Data, Intensity, value.Intensity);
            BE.WF32(Data, SunElevation, value.SunElevation);
            BE.WF32(Data, SunAzimuth, value.SunAzimuth);
            BE.W32(Data, FogCommand + FogOn, value.FogOn ? 1u : 0u);
            BE.WF32(Data, FogCommand + FogStart, value.FogStart);
            BE.WF32(Data, FogCommand + FogEnd, value.FogEnd);
            BE.WF32(Data, FogCommand + FogMax, value.FogMax);
            BE.W32(Data, FogCommand + FogColour, (value.FogColour & 0xFFFFFF) << 8);
            int f = FillCommand;
            if (f >= 0 && value.HasFill)
            {
                // the flag word keeps its "off" value (1 in the game data) unless the light is switched
                bool wasOn = BE.U32(Data, f + FillFlag) == 0;
                if (value.FillOn != wasOn) BE.W32(Data, f + FillFlag, value.FillOn ? 0u : 1u);
                // the colour word's low byte is kept (Showdown Town's night setup has 0x3EC18300 there while the light is off)
                BE.W32(Data, f + FillColour, (value.FillColour & 0xFFFFFF) << 8 | (BE.U32(Data, f + FillColour) & 0xFF));
                BE.WF32(Data, f + FillIntensity, value.FillIntensity);
                BE.WF32(Data, f + FillElevation, value.FillElevation);
                BE.WF32(Data, f + FillAzimuth, value.FillAzimuth);
            }
        }
    }

    /// <summary>"main", "morning", "act1 main" … (the name after the world's name).</summary>
    public string ShortName
    {
        get
        {
            var n = Name.StartsWith("aid_script_banjox_lightsetup_") ? Name["aid_script_banjox_lightsetup_".Length..] : Name;
            int u = n.IndexOf('_');
            return (u > 0 ? n[(u + 1)..] : n).Replace('_', ' ');
        }
    }
}

/// <summary>A skydome model of the world and the textures it draws.</summary>
public sealed record SkyDome(string Name, uint Id, List<string> Textures)
{
    /// <summary>"morning", "night" … (the part after "skydomes_").</summary>
    public string ShortName
    {
        get
        {
            int i = Name.LastIndexOf("skydome", StringComparison.Ordinal);
            var s = i >= 0 ? Name[(i + "skydome".Length)..].TrimStart('s', '_') : Name;
            return s.Length > 0 ? s : Name;
        }
    }
}

/// <summary>
/// One time of day (or level light) of a world: its light setup and, when a time-of-day script runs it, that script's
/// skydome command (op 0x2C, first argument = skydome model id). Showdown Town: aid_script_banjox_showdowntown_{morning,
/// midday,afternoon,night} in the common bundle 685374 run lightsetup_showdowntown_{morning,main,afternoon,night}.
/// </summary>
public sealed class TimeOfDay
{
    public string Display = "";
    public LightSetup Light = null!;
    public string? PhaseScript;
    public uint PhaseBundle;
    public int PhaseSymbol;
    public byte[]? PhaseData;
    /// <summary>Offset of the skydome model id in <see cref="PhaseData"/> (op 0x2C + 8), or -1.</summary>
    public int DomeOffset = -1;
    public override string ToString() => Display;   // list items (accessibility names)
    public uint DomeId
    {
        get => PhaseData != null && DomeOffset >= 0 ? BE.U32(PhaseData, DomeOffset) : 0;
        set { if (PhaseData != null && DomeOffset >= 0) BE.W32(PhaseData, DomeOffset, value); }
    }
}

/// <summary>
/// Sky, light and fog of one world, editable in place: light setups of the world bundle, the time-of-day scripts that run
/// them (world bundle and the common bundle 685374) with their skydome, and the world's skydome models. Same edits as
/// NB.Cli obj-set on the light-setup / time-of-day scripts (snow/research/light/apply_winter.py).
/// </summary>
public sealed class WorldAtmosphere
{
    public const uint CommonBundle = 0x685374;
    public uint WorldBundle;
    public string WorldName = "";
    public List<TimeOfDay> Times = new();
    public List<SkyDome> Domes = new();
    readonly Dictionary<uint, CaffFile> _caffs = new();
    readonly Workspace _ws;

    WorldAtmosphere(Workspace ws) { _ws = ws; }

    static readonly string[] PhaseOrder = { "morning", "midday", "main", "day", "afternoon", "evening", "dusk", "sunset", "night" };

    public static WorldAtmosphere Load(Workspace ws, AssetIndex index, uint worldBundle)
    {
        worldBundle &= 0xFFFFFF;
        var a = new WorldAtmosphere(ws) { WorldBundle = worldBundle };
        var world = a.Caff(worldBundle);
        // light setups stored in the world bundle
        var lights = new List<LightSetup>();
        for (int s = 1; s <= world.Symbols.Count; s++)
        {
            var name = AssetIds.DisplayName(world.Symbols[s - 1]);
            if (!name.StartsWith("aid_script_banjox_lightsetup_")) continue;
            var part = DataPart(world, s);
            if (part == null) continue;
            int fog = LightSetup.FindFog(part.Data);
            if (fog < 0) continue;
            lights.Add(new LightSetup { Name = name, Bundle = worldBundle, Symbol = s, Id = AssetIds.IdOf(world.Symbols[s - 1]) ?? 0, Data = part.Data, FogCommand = fog });
        }
        if (lights.Count > 0)
        {
            var parts = lights[0].Name["aid_script_banjox_lightsetup_".Length..].Split('_');
            a.WorldName = parts[0];
        }
        // time-of-day scripts: op 0x52 runs a light setup, op 0x2C sets the skydome
        var byId = lights.ToDictionary(l => l.Id);
        var phaseOf = new Dictionary<LightSetup, TimeOfDay>();
        foreach (var bundle in new[] { worldBundle, CommonBundle }.Distinct())
        {
            if (bundle != worldBundle && !index.Entries.Any(e => e.Bundle == bundle && !e.Streamed)) continue;
            CaffFile c;
            try { c = a.Caff(bundle); } catch (Exception) { continue; }
            for (int s = 1; s <= c.Symbols.Count; s++)
            {
                var name = AssetIds.DisplayName(c.Symbols[s - 1]);
                if (!name.StartsWith("aid_script_") || name.StartsWith("aid_script_banjox_lightsetup_")) continue;
                if (a.WorldName.Length > 0 && !name.Contains(a.WorldName)) continue;
                var part = DataPart(c, s);
                if (part == null || part.Data.Length < 8) continue;
                ScriptAsset sc;
                try { sc = ScriptAsset.Parse(part.Data); } catch (Exception) { continue; }
                var run = sc.Commands.Where(k => k.Op == 0x52 && k.Data.Length >= 12).Select(k => k.Arg(0)).FirstOrDefault(id => byId.ContainsKey(id));
                if (run == 0) continue;
                var light = byId[run];
                var dome = sc.Commands.FirstOrDefault(k => k.Op == 0x2C && k.Data.Length >= 12);
                string tail = a.WorldName.Length > 0 && name.Contains(a.WorldName + "_") ? name[(name.IndexOf(a.WorldName + "_", StringComparison.Ordinal) + a.WorldName.Length + 1)..] : name;
                var cand = new TimeOfDay
                {
                    Display = Title(tail), Light = light, PhaseScript = name, PhaseBundle = bundle, PhaseSymbol = s, PhaseData = part.Data,
                    DomeOffset = dome != null ? dome.Offset + 8 : -1,
                };
                // several scripts can run the same light setup (Showdown Town: ..._midday and the older ..._main both run
                // lightsetup_main): keep the time-of-day script (a phase word other than "main", with a skydome command)
                if (!phaseOf.TryGetValue(light, out var had) || Score(cand) > Score(had)) phaseOf[light] = cand;
            }
        }
        foreach (var l in lights)
            a.Times.Add(phaseOf.TryGetValue(l, out var t) ? t : new TimeOfDay { Display = Title(l.ShortName), Light = l });
        a.Times = a.Times.OrderBy(t => Rank(t)).ThenBy(t => t.Display).ToList();
        a.Snapshot();
        // skydome models of the world
        for (int s = 1; s <= world.Symbols.Count; s++)
        {
            var name = AssetIds.DisplayName(world.Symbols[s - 1]);
            if (!name.StartsWith("aid_model_") || !name.Contains("skydome")) continue;
            var tex = new List<string>();
            try
            {
                var m = NB.Core.Models.ModelAsset.Parse(world, s);
                foreach (var d in m.Draws) foreach (var (_, t) in d.Textures)
                {
                    var st = NB.Core.Models.ObjExporter.TextureFileStem(t);
                    if (!tex.Contains(st)) tex.Add(st);
                }
            }
            catch (Exception) { }
            a.Domes.Add(new SkyDome(name, AssetIds.IdOf(world.Symbols[s - 1]) ?? 0, tex));
        }
        // domes used by a time-of-day script but stored elsewhere still get a name
        foreach (var t in a.Times.Where(t => t.DomeOffset >= 0))
            if (!a.Domes.Any(d => d.Id == t.DomeId))
            {
                var e = index.Entries.FirstOrDefault(x => x.Id == t.DomeId);
                a.Domes.Add(new SkyDome(e?.Name ?? $"0x{t.DomeId:X8}", t.DomeId, new()));
            }
        return a;
    }

    static int Score(TimeOfDay t)
    {
        var key = (t.PhaseScript ?? "").ToLowerInvariant();
        int phase = Array.FindIndex(PhaseOrder, p => p != "main" && key.EndsWith(p));
        return (t.DomeOffset >= 0 ? 2 : 0) + (phase >= 0 ? 4 : 0);
    }

    static int Rank(TimeOfDay t)
    {
        var key = (t.PhaseScript ?? t.Light.Name).ToLowerInvariant();
        for (int i = 0; i < PhaseOrder.Length; i++) if (key.EndsWith(PhaseOrder[i])) return i;
        return PhaseOrder.Length;
    }

    static string Title(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].Replace('_', ' ');

    static CaffPart? DataPart(CaffFile c, int sym) => c.PartsOf(sym).FirstOrDefault(p => c.SectionOf(p).Name == ".data");

    CaffFile Caff(uint bundle)
    {
        if (!_caffs.TryGetValue(bundle, out var c)) _caffs[bundle] = c = _ws.LoadResident(bundle);
        return c;
    }

    /// <summary>True while the bundles this editor holds are still the workspace's current copies (false after Revert or
    /// Undo Last Bundle Save reloaded them from disk: then load the atmosphere again).</summary>
    public bool IsCurrent => _caffs.All(kv => ReferenceEquals(kv.Value, _ws.LoadResident(kv.Key)));

    readonly HashSet<uint> _dirty = new();
    /// <summary>Marks a bundle as edited (light setups live in the world bundle, time-of-day scripts maybe in 685374).</summary>
    public void MarkDirty(uint bundle) => _dirty.Add(bundle & 0xFFFFFF);
    public bool Dirty => _dirty.Count > 0;

    /// <summary>Writes the edited bundles into the workspace (one history snapshot + change-log entry each).</summary>
    public List<string> Save(string description)
    {
        var saved = new List<string>();
        foreach (var bundle in _dirty.OrderBy(b => b))
        {
            _ws.SaveResident(bundle, Caff(bundle), description);
            saved.Add(bundle.ToString("x6"));
        }
        _dirty.Clear();
        Snapshot();
        return saved;
    }

    // the edited parts live in the workspace's cached bundle objects (shared with the open world), so discarding must put
    // the saved bytes back rather than load the bundle again
    readonly Dictionary<byte[], byte[]> _saved = new(ReferenceEqualityComparer.Instance);
    void Snapshot()
    {
        _saved.Clear();
        foreach (var t in Times)
        {
            _saved[t.Light.Data] = (byte[])t.Light.Data.Clone();
            if (t.PhaseData != null) _saved[t.PhaseData] = (byte[])t.PhaseData.Clone();
        }
    }

    /// <summary>Discards unsaved edits: every light setup and time-of-day script gets its last saved bytes back.</summary>
    public void Revert()
    {
        foreach (var (live, saved) in _saved) Buffer.BlockCopy(saved, 0, live, 0, saved.Length);
        _dirty.Clear();
    }

    /// <summary>Values (and skydome) of the untouched game for one time of day, read from the workspace's original folder.</summary>
    public (LightValues Light, uint Dome) Original(TimeOfDay t)
    {
        var lc = OriginalCaff(t.Light.Bundle);
        int ls = lc.Symbols.FindIndex(s => AssetIds.DisplayName(s) == t.Light.Name) + 1;
        var ld = DataPart(lc, ls)!.Data;
        var light = new LightSetup { Data = (byte[])ld.Clone(), FogCommand = LightSetup.FindFog(ld) }.Values;
        uint dome = 0;
        if (t.PhaseScript != null && t.DomeOffset >= 0)
        {
            var pc = OriginalCaff(t.PhaseBundle);
            int ps = pc.Symbols.FindIndex(s => AssetIds.DisplayName(s) == t.PhaseScript) + 1;
            var pd = DataPart(pc, ps)!.Data;
            if (t.DomeOffset + 4 <= pd.Length) dome = BE.U32(pd, t.DomeOffset);
        }
        return (light, dome);
    }

    readonly Dictionary<uint, CaffFile> _originals = new();
    CaffFile OriginalCaff(uint bundle)
    {
        if (_originals.TryGetValue(bundle, out var c)) return c;
        var raw = File.ReadAllBytes(_ws.Original.ResidentPath(bundle));
        if (XCompressFile.IsCompressed(raw))
        {
            var cache = Path.Combine(_ws.CacheDir, "original", "4f", bundle.ToString("x6"));
            if (File.Exists(cache)) raw = File.ReadAllBytes(cache);
            else { raw = XCompressFile.Decompress(raw); Directory.CreateDirectory(Path.GetDirectoryName(cache)!); File.WriteAllBytes(cache, raw); }
        }
        return _originals[bundle] = CaffFile.Read(raw);
    }

    /// <summary>
    /// Winter values of Snowy Showdown Town (snow/research/light/apply_winter.py, morning fog 0.45 / B4BECE from snow/build.sh),
    /// by light-setup short name; the sun direction and fog switch are kept from the setup itself.
    /// </summary>
    public static readonly Dictionary<string, (uint Amb, uint Sun, float Inten, uint Fog, float Start, float End, float Max)> WinterPreset = new()
    {
        ["main"] = (0x8A94A8, 0xCCDBFA, 0.65f, 0xCED8E6, 25f, 400f, 0.60f),
        ["morning"] = (0x6E7387, 0xF5DBCC, 0.85f, 0xB4BECE, 20f, 380f, 0.45f),
        ["afternoon"] = (0x6B6B85, 0xFACC9E, 0.90f, 0xD1C4C7, 25f, 380f, 0.60f),
        ["night"] = (0x333D5C, 0x7387B8, 1.30f, 0x3D4A6B, 10f, 320f, 0.75f),
    };
}
