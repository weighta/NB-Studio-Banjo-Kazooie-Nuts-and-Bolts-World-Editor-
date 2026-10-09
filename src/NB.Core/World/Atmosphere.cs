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
    /// <summary>Other resident copies of the same asset (same id) in other bundles: an act's light setup is copied into
    /// its challenge bundles (World of Sports act 4: 8 bundles). Saving writes the edited bytes into every copy.</summary>
    public List<(uint Bundle, int Symbol)> Copies = new();
    /// <summary>Level scripts (op 0x52) that run this light setup, in the scanned bundles.</summary>
    public List<string> UsedBy = new();

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
            var s = i >= 0 ? Name[(i + "skydome".Length)..] : Name;
            if (i >= 0) { if (s.StartsWith("s_")) s = s[2..]; s = s.TrimStart('_'); }   // "skydomes_sunrise01" -> "sunrise01"
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
    /// <summary>The act whose script runs this setup is the open one (it is listed first).</summary>
    public bool CurrentAct;
    /// <summary>Scripts of other worlds / levels that also run this light setup (shown before saving).</summary>
    public List<string> SharedWith = new();
    /// <summary>"stored in 01ede7 (Nutty Acres act 6), run by aid_script_banjox_nuttyacres_act6_main" …</summary>
    public string Where = "";
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

    public uint ActBundle;

    /// <summary>
    /// The sky, light and fog of a world (and of the act opened with it). Where a level's light setup lives:
    /// <list type="bullet">
    /// <item>Showdown Town: in the world bundle 234cec, run by the time-of-day scripts in the common bundle 685374.</item>
    /// <item>Every other world: one setup per act (aid_script_banjox_lightsetup_&lt;world&gt;_act&lt;N&gt;_main, in the act's
    /// own bundle, e.g. Nutty Acres act 6 = 01ede7), run (op 0x52) by the act's main script in 685374 that also loads the
    /// world (op 0x01 = the background model), with the act's skydome (op 0x2C). Challenge bundles of an act carry
    /// identical copies (<see cref="LightSetup.Copies"/>, written too on save).</item>
    /// <item>Spiral Mountain, Banjo's house, LOGBOX, the multiplayer ("live_") levels: scripts named after the world or
    /// loading its background, in the world / act / common bundles.</item>
    /// </list>
    /// Same scan as the 3D view's Rendered mode (<see cref="WorldLooks"/>). <paramref name="actBundle"/> (0 = none): the
    /// act opened with the world; its setup comes first and its bundle's copy is the one edited.
    /// </summary>
    public static WorldAtmosphere Load(Workspace ws, AssetIndex index, uint worldBundle, uint actBundle = 0)
    {
        worldBundle &= 0xFFFFFF; actBundle &= 0xFFFFFF;
        var a = new WorldAtmosphere(ws) { WorldBundle = worldBundle, ActBundle = actBundle };
        var world = a.Caff(worldBundle);
        // the world's key ("nuttyacres") and background model (what level scripts load with op 0x01)
        uint bgId = 0;
        for (int s = 1; s <= world.Symbols.Count && bgId == 0; s++)
        {
            var n = AssetIds.DisplayName(world.Symbols[s - 1]);
            if (n.StartsWith("aid_model_banjox_background_") && n.EndsWith("_default"))
            { a.WorldName = n["aid_model_banjox_background_".Length..^"_default".Length]; bgId = AssetIds.IdOf(world.Symbols[s - 1]) ?? 0; }
        }
        var names = new Dictionary<uint, string>();
        foreach (var e in index.Entries) if (e.Id != 0 && !e.Streamed) names.TryAdd(e.Id, e.Name);
        bool IsLight(uint id) => names.TryGetValue(id, out var n) && n.StartsWith("aid_script_banjox_lightsetup_");
        // background models of every world: a script that loads another world's background is that world's (the Car Park's
        // showdowntown_carpark script, the title screen's Spiral Mountain …)
        var backgrounds = index.Entries.Where(e => !e.Streamed && e.Type == "model" && e.Name.StartsWith("aid_model_banjox_background_") && e.Name.EndsWith("_default"))
            .Select(e => e.Id).ToHashSet();

        // light setups by id: the act's copy first, then the world's, then any resident copy
        var lights = new Dictionary<uint, LightSetup?>();
        LightSetup? Light(uint id)
        {
            if (lights.TryGetValue(id, out var have)) return have;
            var entries = index.Entries.Where(e => e.Id == id && !e.Streamed && e.Symbol > 0)
                .OrderBy(e => e.Bundle == actBundle && actBundle != 0 ? 0 : e.Bundle == worldBundle ? 1 : 2).ThenBy(e => e.Bundle).ToList();
            LightSetup? l = null;
            foreach (var e in entries)
            {
                try
                {
                    var c = a.Caff(e.Bundle);
                    var part = DataPart(c, e.Symbol);
                    int fog = part == null ? -1 : LightSetup.FindFog(part.Data);
                    if (fog < 0) continue;
                    l = new LightSetup { Name = e.Name, Bundle = e.Bundle, Symbol = e.Symbol, Id = id, Data = part!.Data, FogCommand = fog };
                    l.Copies = entries.Where(x => x != e).Select(x => (x.Bundle, x.Symbol)).Distinct().ToList();
                    break;
                }
                catch (Exception) { }
            }
            return lights[id] = l;
        }

        // level scripts in the world, act and common bundles: op 0x52 runs a light setup, op 0x2C sets the skydome
        var phaseOf = new Dictionary<LightSetup, TimeOfDay>();
        var runners = new Dictionary<uint, List<(string Script, bool Ours)>>();
        foreach (var bundle in new[] { worldBundle, actBundle, CommonBundle }.Where(b => b != 0).Distinct())
        {
            if (bundle != worldBundle && !index.Entries.Any(e => e.Bundle == bundle && !e.Streamed)) continue;
            CaffFile c;
            try { c = a.Caff(bundle); } catch (Exception) { continue; }
            for (int s = 1; s <= c.Symbols.Count; s++)
            {
                var name = AssetIds.DisplayName(c.Symbols[s - 1]);
                if (!name.StartsWith("aid_script_") || name.StartsWith("aid_script_banjox_lightsetup_")) continue;
                var part = DataPart(c, s);
                if (part == null || part.Data.Length < 8) continue;
                ScriptAsset sc;
                try { sc = ScriptAsset.Parse(part.Data); } catch (Exception) { continue; }
                var runs = sc.Commands.Where(k => k.Op == 0x52 && k.Data.Length >= 12).Select(k => k.Arg(0)).Where(IsLight).Distinct().ToList();
                if (runs.Count == 0) continue;
                bool loadsWorld = bgId != 0 && sc.Commands.Any(k => k.Op == 0x01 && k.Data.Length >= 12 && k.Arg(0) == bgId);
                bool loadsOther = sc.Commands.Any(k => k.Op == 0x01 && k.Data.Length >= 12 && k.Arg(0) != bgId && backgrounds.Contains(k.Arg(0)));
                bool named = a.WorldName.Length > 0 && (name.Contains("_" + a.WorldName + "_") || name.EndsWith("_" + a.WorldName))
                    && runs.All(id => names[id].Contains(a.WorldName)) && !loadsOther;
                bool ours = loadsWorld || named;
                foreach (var id in runs)
                {
                    if (!runners.TryGetValue(id, out var rl)) runners[id] = rl = new();
                    rl.Add((name, ours));
                }
                if (!ours) continue;
                var dome = sc.Commands.FirstOrDefault(k => k.Op == 0x2C && k.Data.Length >= 12);
                foreach (var id in runs)
                {
                    var light = Light(id);
                    if (light == null) continue;
                    var cand = new TimeOfDay
                    {
                        Display = DisplayOf(name, a.WorldName), Light = light, PhaseScript = name, PhaseBundle = bundle, PhaseSymbol = s, PhaseData = part.Data,
                        DomeOffset = dome != null ? dome.Offset + 8 : -1,
                        CurrentAct = actBundle != 0 && (light.Bundle == actBundle || light.Copies.Any(x => x.Bundle == actBundle)),
                    };
                    // several scripts can run the same light setup (Showdown Town: ..._midday and the older ..._main both run
                    // lightsetup_main): keep the time-of-day / act script (a phase word other than "main", with a skydome)
                    if (!phaseOf.TryGetValue(light, out var had) || Score(cand) > Score(had)) phaseOf[light] = cand;
                }
            }
        }
        // light setups stored in the world / act bundle that no script was found for (listed by name)
        foreach (var b in new[] { worldBundle, actBundle }.Where(b => b != 0).Distinct())
        {
            CaffFile c;
            try { c = a.Caff(b); } catch (Exception) { continue; }
            for (int s = 1; s <= c.Symbols.Count; s++)
            {
                var name = AssetIds.DisplayName(c.Symbols[s - 1]);
                if (!name.StartsWith("aid_script_banjox_lightsetup_")) continue;
                var l = Light(AssetIds.IdOf(c.Symbols[s - 1]) ?? 0);
                if (l == null || phaseOf.ContainsKey(l)) continue;
                phaseOf[l] = new TimeOfDay { Display = Title(l.ShortName), Light = l, CurrentAct = b == actBundle };
            }
        }
        foreach (var (l, t) in phaseOf)
        {
            if (runners.TryGetValue(l.Id, out var rl))
            {
                l.UsedBy = rl.Select(r => r.Script).Distinct().ToList();
                var ours = rl.Where(r => r.Ours).Select(r => r.Script).ToHashSet();
                // (a setup found only by name in the world / act bundle has no script of ours to compare with)
                if (t.PhaseScript != null) t.SharedWith = rl.Where(r => !r.Ours && !ours.Contains(r.Script)).Select(r => r.Script).Distinct().ToList();
                // one setup run by several acts (LOGBOX 720 acts 1-5 + WW, World of Sports acts 1 and 2): "Acts 1, 2"
                var acts = ours.Select(x => System.Text.RegularExpressions.Regex.Match(x, "_" + a.WorldName + @"_act(\d+|ww)_main$"))
                    .Where(m => m.Success).Select(m => m.Groups[1].Value == "ww" ? "WW" : m.Groups[1].Value).Distinct()
                    .OrderBy(x => x == "WW" ? 99 : int.Parse(x)).ToList();
                if (acts.Count > 1) t.Display = "Acts " + string.Join(", ", acts);
            }
            t.Where = $"stored in {l.Bundle:x6}" + (l.Copies.Count > 0 ? $" (+ {l.Copies.Count} cop{(l.Copies.Count == 1 ? "y" : "ies")}: {string.Join(", ", l.Copies.Select(x => x.Bundle.ToString("x6")).Distinct())})" : "")
                + (l.UsedBy.Count > 0 ? $", run by {string.Join(", ", l.UsedBy.Select(u => u.Replace("aid_script_banjox_", "")))}" : "");
            a.Times.Add(t);
        }
        // the open act first; multiplayer ("live_") levels and setups named after another world (Banjo's house holds a copy
        // of Spiral Mountain's sunrise) last
        bool Foreign(TimeOfDay t)
        {
            var n = t.Light.Name.Replace("aid_script_banjox_lightsetup_", "");
            return WorldCatalog.DisplayNames.Keys.Any(k => k != a.WorldName && n.StartsWith(k + "_"));
        }
        a.Times = a.Times.OrderBy(t => t.CurrentAct ? 0 : 1).ThenBy(t => (t.PhaseScript ?? "").Contains("_live_") ? 1 : 0).ThenBy(t => Foreign(t) ? 1 : 0)
            .ThenBy(t => Rank(t)).ThenBy(t => t.Display, StringComparer.OrdinalIgnoreCase).ToList();
        a.Snapshot();
        // skydome models of the world
        for (int s = 1; s <= world.Symbols.Count; s++)
        {
            var name = AssetIds.DisplayName(world.Symbols[s - 1]);
            if (!name.StartsWith("aid_model_") || !name.Contains("skydome")) continue;
            var tex = new List<string>();
            try { tex = DomeTextures(NB.Core.Models.ModelAsset.Parse(world, s)); } catch (Exception) { }
            a.Domes.Add(new SkyDome(name, AssetIds.IdOf(world.Symbols[s - 1]) ?? 0, tex));
        }
        // domes used by a time-of-day / act script but stored elsewhere (act bundles, Bundle/50): name and textures
        foreach (var t in a.Times.Where(t => t.DomeOffset >= 0))
            if (t.DomeId != 0 && !a.Domes.Any(d => d.Id == t.DomeId))
            {
                var tex = new List<string>();
                try { if (WorldLooks.LoadModel(ws, index, t.DomeId, null) is { } m) tex = DomeTextures(m); } catch (Exception) { }
                a.Domes.Add(new SkyDome(names.GetValueOrDefault(t.DomeId) ?? $"0x{t.DomeId:X8}", t.DomeId, tex));
            }
        return a;
    }

    static List<string> DomeTextures(NB.Core.Models.ModelAsset m)
    {
        var tex = new List<string>();
        foreach (var d in m.Draws) foreach (var (_, t) in d.Textures)
        {
            var st = NB.Core.Models.ObjExporter.TextureFileStem(t);
            if (!tex.Contains(st)) tex.Add(st);
        }
        return tex;
    }

    /// <summary>"Act 6", "Morning", "Startofgame" … from a level script's name.</summary>
    static string DisplayOf(string script, string world)
    {
        string tail = world.Length > 0 && script.Contains(world + "_") ? script[(script.IndexOf(world + "_", StringComparison.Ordinal) + world.Length + 1)..] : script.Replace("aid_script_banjox_", "");
        var m = System.Text.RegularExpressions.Regex.Match(tail, @"^act(\d+|ww)_main$");
        if (m.Success) return "Act " + (m.Groups[1].Value == "ww" ? "WW" : m.Groups[1].Value);
        if (script.Contains("_live_")) return "Multiplayer: " + Title(tail);
        return Title(tail);
    }

    static int Score(TimeOfDay t)
    {
        var key = (t.PhaseScript ?? "").ToLowerInvariant();
        int phase = Array.FindIndex(PhaseOrder, p => p != "main" && key.EndsWith(p));
        return (t.CurrentAct ? 16 : 0) + (System.Text.RegularExpressions.Regex.IsMatch(key, @"_act(\d+|ww)_main$") ? 8 : 0) + (t.DomeOffset >= 0 ? 2 : 0) + (phase >= 0 ? 4 : 0)
            - (key.Contains("_live_") ? 8 : 0) - (key.Contains("_ui_frontend") || key.Contains("_demo") ? 6 : 0);
    }

    static int Rank(TimeOfDay t)
    {
        var key = (t.PhaseScript ?? t.Light.Name).ToLowerInvariant();
        if (key.EndsWith("startofgame")) return -1;   // Spiral Mountain: the start of the game before its ending
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
        // identical copies of an edited light setup in other bundles (challenge bundles of an act) get the same bytes
        foreach (var l in Times.Select(t => t.Light).Distinct())
        {
            if (l.Copies.Count == 0 || (_saved.TryGetValue(l.Data, out var before) && before.AsSpan().SequenceEqual(l.Data))) continue;
            foreach (var (b, sym) in l.Copies)
            {
                var part = DataPart(Caff(b), sym);
                if (part == null || part.Data.Length != l.Data.Length || part.Data.AsSpan().SequenceEqual(l.Data)) continue;
                Buffer.BlockCopy(l.Data, 0, part.Data, 0, l.Data.Length);
                MarkDirty(b);
            }
        }
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

    /// <summary>Forgets edits that were taken back (undone, a cancelled colour pick): a bundle whose light setups and
    /// time-of-day scripts all hold their saved bytes again is no longer unsaved.</summary>
    public void RefreshDirty()
    {
        foreach (var b in _dirty.ToList())
        {
            bool same = true;
            foreach (var t in Times)
            {
                if (t.Light.Bundle == b && _saved.TryGetValue(t.Light.Data, out var l) && !l.AsSpan().SequenceEqual(t.Light.Data)) same = false;
                if (t.PhaseData != null && t.PhaseBundle == b && _saved.TryGetValue(t.PhaseData, out var ph) && !ph.AsSpan().SequenceEqual(t.PhaseData)) same = false;
            }
            if (same) _dirty.Remove(b);
        }
    }

    /// <summary>Bundles a save would write for the edits so far (the light setups' copies included).</summary>
    public List<uint> BundlesToSave()
    {
        var set = new HashSet<uint>(_dirty);
        foreach (var t in Times)
            if (_saved.TryGetValue(t.Light.Data, out var before) && !before.AsSpan().SequenceEqual(t.Light.Data))
                foreach (var (b, _) in t.Light.Copies) set.Add(b);
        return set.OrderBy(b => b).ToList();
    }

    /// <summary>Another editor saved bytes inside an array this one snapshots (the music tables of the time-of-day
    /// scripts): they become part of the saved state, so Discard / Revert keeps them.</summary>
    public void NoteExternalWrite(byte[] live, int offset, int length)
    {
        if (_saved.TryGetValue(live, out var saved) && offset >= 0 && offset + length <= saved.Length && saved.Length == live.Length)
            Buffer.BlockCopy(live, offset, saved, offset, length);
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
