using NB.Core.IO;
using NB.Core.World;

namespace NB.Core.Live;

/// <summary>
/// Live light and fog of the running game (snow/research/light/live.py, verified in Xenia 2026-10-01). The active light
/// object is created from the level's light setup at load time and copied into the renderer every frame (fog start/end
/// globals at 0x82F9DF6C), so writes show immediately and last until the next time-of-day / level load. Layout (floats):
/// +0x10 ambient RGB, +0x20 sun RGB, +0x30 sun direction (the light's travel direction = minus the unit vector towards
/// the sun, from the light setup's elevation / azimuth), +0x50 the fill light's travel direction (op 0x7E), +0x68 sun
/// intensity,
/// +0xA0 fog RGB, +0xD0 / +0xD4 / +0xD8 fog start / end / max; the fog block +0x80..+0xE0 has a second copy 0xC0 further
/// on. Colours are byte / 255. The sun direction is read but not written: written live, the shading follows but the
/// shadows do not (the game sets its shadow casters up for the direction it loaded; checked 2026-10-06 in Xenia,
/// work/agent_src/studio19/shots/live): a changed sun direction is seen in the game after saving and reloading the level.
/// </summary>
public static class LiveLight
{
    public const uint FogGlobals = 0x82F9DF6C;

    /// <summary>Guest address of the active light object, or 0 when no level is loaded.</summary>
    public static uint Find(XeniaLive x)
    {
        var key = x.Read(FogGlobals, 8);
        if (key.Length != 8 || key.All(b => b == 0)) return 0;
        var marker = new byte[] { 0x3F, 0x7E, 0xB8, 0x52 };   // 0.995 at -0x38 from the fog start (light-setup default)
        foreach (var (va, size) in x.Regions(0x40000000, 0x50000000))
            for (long off = 0; off < size; off += 1 << 24)
            {
                int n = (int)Math.Min((1 << 24) + 0x100, size - off);
                var d = x.Read((uint)(va + off), n);
                for (int i = d.AsSpan().IndexOf(key); i >= 0;)
                {
                    if (i >= 0xD0 && i + 0xC8 <= d.Length && d.AsSpan(i - 0x38, 4).SequenceEqual(marker) && d.AsSpan(i + 0xC0, 8).SequenceEqual(key))
                        return (uint)(va + off + i - 0xD0);
                    int next = d.AsSpan(i + 1).IndexOf(key);
                    if (next < 0) break;
                    i += next + 1;
                }
            }
        return 0;
    }

    static float[] Rgb(uint c) => new[] { ((c >> 16) & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, (c & 0xFF) / 255f };
    static uint ToRgb(float r, float g, float b) => (uint)(Math.Clamp((int)MathF.Round(r * 255), 0, 255) << 16 | Math.Clamp((int)MathF.Round(g * 255), 0, 255) << 8 | Math.Clamp((int)MathF.Round(b * 255), 0, 255));

    static void W(XeniaLive x, uint light, int off, params float[] v)
    {
        var b = new byte[4 * v.Length];
        for (int i = 0; i < v.Length; i++) BE.WF32(b, 4 * i, v[i]);
        x.Write(light + (uint)off, b);
        if (off >= 0x80 && off < 0xE0) x.Write(light + (uint)off + 0xC0, b);
    }

    /// <summary>Writes light and fog values into the active light object.</summary>
    public static void Write(XeniaLive x, uint light, LightValues v)
    {
        W(x, light, 0x10, Rgb(v.Ambient));
        W(x, light, 0x20, Rgb(v.Sun));
        W(x, light, 0x68, v.Intensity);
        W(x, light, 0xA0, Rgb(v.FogColour));
        W(x, light, 0xD0, v.FogStart, v.FogEnd, v.FogMax);
    }

    /// <summary>Reads the active light object (the fog switch is not part of it: returned as true; the sun direction is
    /// converted back to elevation / azimuth).</summary>
    public static LightValues Read(XeniaLive x, uint light)
    {
        var d = x.Read(light, 0xE0);
        if (d.Length < 0xE0) throw new InvalidOperationException("light object not readable");
        float F(int o) => BE.F32(d, o);
        float dx = -F(0x30), dy = -F(0x34), dz = -F(0x38);
        float elev = MathF.Asin(Math.Clamp(dy, -1f, 1f)), azim = MathF.Atan2(-dx, -dz);
        return new LightValues(ToRgb(F(0x10), F(0x14), F(0x18)), ToRgb(F(0x20), F(0x24), F(0x28)), F(0x68), F(0xD0), F(0xD4), F(0xD8),
            ToRgb(F(0xA0), F(0xA4), F(0xA8)), elev, azim, true);
    }
}
