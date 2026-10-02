using System.Numerics;
using NB.Core.Formats;
using NB.Core.IO;

namespace NB.Core.World;

/// <summary>
/// Read-only view of a level light setup for the 3D viewer: the .data of an aid_script_banjox_lightsetup_* asset
/// (snow/research/light/REPORT.md, values verified live in Xenia): a 0x24-byte header (+0x08 ambient RGB0, +0x0C sun
/// colour RGB0, +0x10 / +0x14 sun elevation / azimuth in radians, +0x1C sun intensity) followed by script commands
/// ({u32 size, u32 op, …}); op 0x53 is the fog (+0x08 on, +0x0C start, +0x10 end, +0x14 max 0..1, +0x24 colour RGB0).
/// (NB.Core.World.LightSetup in Atmosphere.cs is the editable counterpart.)
/// </summary>
public sealed class LevelLighting
{
    public string Name = "";
    public Vector3 Ambient, Sun, FogColour;
    public float Elevation, Azimuth, Intensity = 1, FogStart, FogEnd, FogMax;
    public bool Fog;
    /// <summary>Second fog band of op 0x53 (+0x18 max, +0x1C start, +0x20 end): 0 in Showdown Town (why it showed no change
    /// there), 0.486 from 33 units in Nutty Acres, 0.25 from 152 in Jiggoseum, 0.15 from 70 in Banjoland — the near haze.</summary>
    public float Fog2Max, Fog2Start, Fog2End;
    /// <summary>Second directional light (script op 0x7E: +8 flag (0 = on), +0x0C colour RGB0, +0x10 / +0x14 elevation /
    /// azimuth, +0x1C intensity): the game's pixel shaders add it as N·L × c81 with direction c82. Black when off.</summary>
    public Vector3 FillColour;
    public float FillElevation, FillAzimuth;
    /// <summary>Op 0x6C +0x10: the most the game's exposure may rise to (1 in bright worlds, 2.33 for Showdown Town at night,
    /// 2.78 in Terrarium of Terror). The values of +8 / +0x0C / +0x14 / +0x18 are not understood.</summary>
    public float ExposureMax = 1;
    public Vector3 FillDirection => Vector3.Normalize(new Vector3(-MathF.Cos(FillElevation) * MathF.Sin(FillAzimuth), MathF.Sin(FillElevation), -MathF.Cos(FillElevation) * MathF.Cos(FillAzimuth)));

    /// <summary>Unit vector from the scene towards the sun (game space, right-handed, Y up). The azimuth convention was
    /// fitted against Showdown Town game screenshots (shading of 7 views, 8 candidate conventions): -(sin az, cos az).</summary>
    public Vector3 SunDirection => Vector3.Normalize(new Vector3(-MathF.Cos(Elevation) * MathF.Sin(Azimuth), MathF.Sin(Elevation), -MathF.Cos(Elevation) * MathF.Cos(Azimuth)));

    static int FindFog(byte[] d)
    {
        if (d.Length < 0x24 + 8 || BE.S32(d, 0) != 0x24) return -1;
        for (int o = 0x24; o + 8 <= d.Length;)
        {
            int size = BE.S32(d, o), op = BE.S32(d, o + 4);
            if (size < 8 || o + size > d.Length) return -1;
            if (op == 0x53 && size >= 0x28) return o;
            if (op == 0) break;
            o += size;
        }
        return -1;
    }

    public static LevelLighting? Parse(CaffFile caff, int symbol)
    {
        var view = new AssetView(caff, symbol);
        if (!view.Has(".data")) return null;
        var d = view.Data(".data");
        if (d.Length < 0x24) return null;
        static Vector3 Rgb(byte[] d, int o) => new(d[o] / 255f, d[o + 1] / 255f, d[o + 2] / 255f);
        var l = new LevelLighting
        {
            Name = AssetIds.DisplayName(caff.Symbols[symbol - 1]).Replace("aid_script_banjox_lightsetup_", ""),
            Ambient = Rgb(d, 0x08), Sun = Rgb(d, 0x0C),
            Elevation = BE.F32(d, 0x10), Azimuth = BE.F32(d, 0x14), Intensity = BE.F32(d, 0x1C),
        };
        // other commands: fill light (0x7E) and exposure range (0x6C)
        if (d.Length >= 0x24 + 8 && BE.S32(d, 0) == 0x24)
            for (int o = 0x24; o + 8 <= d.Length;)
            {
                int size = BE.S32(d, o), op = BE.S32(d, o + 4);
                if (size < 8 || o + size > d.Length || op == 0) break;
                if (op == 0x7E && size >= 32 && BE.U32(d, o + 8) == 0)
                {
                    l.FillColour = Rgb(d, o + 0x0C) * BE.F32(d, o + 0x1C);
                    l.FillElevation = BE.F32(d, o + 0x10); l.FillAzimuth = BE.F32(d, o + 0x14);
                }
                if (op == 0x6C && size >= 28) { float e = BE.F32(d, o + 0x10); if (e > 0.1f && e < 10) l.ExposureMax = e; }
                o += size;
            }
        int f = FindFog(d);
        if (f >= 0)
        {
            l.Fog = BE.U32(d, f + 0x08) != 0; l.FogStart = BE.F32(d, f + 0x0C); l.FogEnd = BE.F32(d, f + 0x10); l.FogMax = BE.F32(d, f + 0x14);
            l.FogColour = Rgb(d, f + 0x24);
            l.Fog2Max = BE.F32(d, f + 0x18); l.Fog2Start = BE.F32(d, f + 0x1C); l.Fog2End = BE.F32(d, f + 0x20);
        }
        return l;
    }

    /// <summary>Every light setup in a bundle, the "…_main" one first.</summary>
    public static List<LevelLighting> All(CaffFile caff)
    {
        var list = new List<LevelLighting>();
        for (int s = 1; s <= caff.Symbols.Count; s++)
        {
            if (!caff.Symbols[s - 1].StartsWith("aid_script_banjox_lightsetup_")) continue;
            try { if (Parse(caff, s) is { } l) list.Add(l); } catch { }
        }
        return list.OrderBy(l => l.Name.EndsWith("_main") ? 0 : 1).ThenBy(l => l.Name).ToList();
    }

    public override string ToString() =>
        $"{Name}: ambient {Ambient}, sun {Sun} x{Intensity:F2} (elevation {Elevation:F2}, azimuth {Azimuth:F2}), fog {(Fog ? "on" : "off")} {FogStart:F0}..{FogEnd:F0} max {FogMax:F2} colour {FogColour}";
}
