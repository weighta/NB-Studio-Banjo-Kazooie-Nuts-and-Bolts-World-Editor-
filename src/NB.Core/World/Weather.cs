using System.Numerics;
using NB.Core.Formats;
using NB.Core.IO;
using NB.Core.Project;

namespace NB.Core.World;

/// <summary>Falling-snow settings (particles per second, size factors relative to Banjoland's snow, emitter box).</summary>
public sealed record SnowSettings
{
    /// <summary>Emission rate of the big / small flakes (particle record +0x13C; Banjoland 20 / 30).</summary>
    public float EmitLarge { get; init; } = 1200;
    public float EmitSmall { get; init; } = 250;
    /// <summary>Flake size factors applied to Banjoland's sizes (record +0xB8..+0xC4).</summary>
    public float SizeLarge { get; init; } = 2.5f;
    public float SizeSmall { get; init; } = 2.5f;
    /// <summary>Lifetime range in seconds (+0x124 / +0x128).</summary>
    public float LifeMin { get; init; } = 5;
    public float LifeMax { get; init; } = 7;
    /// <summary>Emitter box: half width around the emitter and height range above it (+0x78 / +0x84).</summary>
    public float HalfWidth { get; init; } = 60;
    public float Bottom { get; init; } = 15;
    public float Top { get; init; } = 35;
    /// <summary>Particle buffer size (+0x10; Banjoland 8000).</summary>
    public int Buffer { get; init; } = 10000;
    /// <summary>Tag the records (+0x180 = 0x534E) so the exe mod snow-follows-camera moves the emitter with the camera.</summary>
    public bool FollowCamera { get; init; } = true;
    /// <summary>Where the effect marker stands (follow mode: only the start point).</summary>
    public Vector3 Position { get; init; } = new(-10, 40, 306);

    /// <summary>Snowy Showdown Town (snow/build.sh: snowtown.py --size1 0.55 --size2 1.0 --emit1 1500 --emit2 80).</summary>
    public static SnowSettings SnowyShowdownTown => new() { EmitLarge = 1500, EmitSmall = 80, SizeLarge = 0.55f, SizeSmall = 1.0f };
    /// <summary>Light flurries.</summary>
    public static SnowSettings Light => new() { EmitLarge = 400, EmitSmall = 60, SizeLarge = 0.6f, SizeSmall = 1.0f };
    /// <summary>Heavy snowfall (snowtown.py run D defaults).</summary>
    public static SnowSettings Heavy => new();
}

/// <summary>
/// Falling snow for any world (the Snowy Showdown Town recipe, snow/research/fx/snowtown.py and REPORT.md): Banjoland's two
/// snow particle records (aid_gpuparticleeffect_common_snowfall1/2, self-contained .data records whose only reference is
/// the flake texture in the always-loaded common bundle) are copied into the world bundle as
/// aid_gpuparticleeffect_banjox_snowtown1/2 with tuned values, a composite effect aid_compositeeffect_banjox_snowtown
/// ({u32 4, id} x 2) plays both, and one type-30 effect marker in the world's main marker asset spawns it. With
/// <see cref="SnowSettings.FollowCamera"/> the exe mod snow-follows-camera keeps the emitter around the camera.
/// Re-applying replaces the earlier values; <see cref="Remove"/> takes everything out again.
/// </summary>
public static class Weather
{
    public const string Source1 = "aid_gpuparticleeffect_common_snowfall1", Source2 = "aid_gpuparticleeffect_common_snowfall2", SourceComposite = "aid_compositeeffect_banjox_snowfall1";
    public const string Gpu1 = "aid_gpuparticleeffect_banjox_snowtown1", Gpu2 = "aid_gpuparticleeffect_banjox_snowtown2", Composite = "aid_compositeeffect_banjox_snowtown";
    public const uint FollowTag = 0x534E;
    public const string ExeModId = "snow-follows-camera";
    const int RecordSize = 0x184;

    /// <summary>The world's main marker asset (…_main, else the one with the most records).</summary>
    public static int MarkerAsset(CaffFile world)
    {
        int best = 0, bestCount = -1;
        for (int s = 1; s <= world.Symbols.Count; s++)
        {
            var n = AssetIds.DisplayName(world.Symbols[s - 1]);
            if (!n.StartsWith("aid_marker_")) continue;
            if (n.EndsWith("_main")) return s;
            var d = Data(world, s);
            if (d != null && d.Length > bestCount) { best = s; bestCount = d.Length; }
        }
        return best;
    }

    static byte[]? Data(CaffFile c, int sym) => c.PartsOf(sym).FirstOrDefault(p => c.SectionOf(p).Name == ".data")?.Data;
    static int Sym(CaffFile c, string name) => c.Symbols.FindIndex(s => AssetIds.DisplayName(s) == name) + 1;

    /// <summary>Bundle holding Banjoland's snow records (1592bd in the retail game).</summary>
    public static uint SourceBundle(AssetIndex index) =>
        index.Entries.Where(e => e.Name == Source1 && !e.Streamed && e.Symbol > 0).Select(e => e.Bundle)
            .Where(b => index.Entries.Any(e => e.Bundle == b && e.Name == Source2) && index.Entries.Any(e => e.Bundle == b && e.Name == SourceComposite))
            .OrderByDescending(b => b == 0x1592bd).FirstOrDefault();

    /// <summary>Current snow of the world, or null when it has none (sizes relative to Banjoland's records).</summary>
    public static SnowSettings? Read(Workspace ws, AssetIndex index, CaffFile world)
    {
        int s1 = Sym(world, Gpu1), s2 = Sym(world, Gpu2);
        if (s1 == 0 || s2 == 0) return null;
        var g1 = Data(world, s1)!; var g2 = Data(world, s2)!;
        float k1 = 1, k2 = 1;
        uint src = SourceBundle(index);
        if (src != 0)
        {
            var b = ws.LoadResident(src);
            var r1 = Data(b, Sym(b, Source1)); var r2 = Data(b, Sym(b, Source2));
            if (r1 != null && BE.F32(r1, 0xB8) != 0) k1 = BE.F32(g1, 0xB8) / BE.F32(r1, 0xB8);
            if (r2 != null && BE.F32(r2, 0xB8) != 0) k2 = BE.F32(g2, 0xB8) / BE.F32(r2, 0xB8);
        }
        var pos = new Vector3(-10, 40, 306);
        int ms = MarkerAsset(world);
        uint comp = AssetIds.IdOf(Composite) ?? 0;
        if (ms > 0)
        {
            var d = Data(world, ms)!;
            for (int o = 0; o + 0x38 <= d.Length;)
            {
                int size = BE.S32(d, o); if (size < 0x30) break;
                if (BE.U16(d, o + 4) == 30 && size >= 0x38 && BE.U32(d, o + 0x34) == comp) { pos = new(BE.F32(d, o + 0x14), BE.F32(d, o + 0x18), BE.F32(d, o + 0x1C)); break; }
                o += size;
            }
        }
        return new SnowSettings
        {
            EmitLarge = BE.F32(g1, 0x13C), EmitSmall = BE.F32(g2, 0x13C), SizeLarge = MathF.Round(k1, 4), SizeSmall = MathF.Round(k2, 4),
            LifeMin = BE.F32(g1, 0x124), LifeMax = BE.F32(g1, 0x128), HalfWidth = BE.F32(g1, 0x84), Bottom = BE.F32(g1, 0x7C), Top = BE.F32(g1, 0x88),
            Buffer = (int)BE.U32(g1, 0x10), FollowCamera = BE.U32(g1, 0x180) == FollowTag, Position = pos,
        };
    }

    static byte[] Tune(byte[] rec, float emit, SnowSettings s, float sizeK)
    {
        var d = (byte[])rec.Clone();
        BE.W32(d, 0x10, (uint)s.Buffer);
        BE.WF32(d, 0x13C, emit);
        BE.WF32(d, 0x124, s.LifeMin); BE.WF32(d, 0x128, s.LifeMax);
        foreach (int o in new[] { 0xB8, 0xBC, 0xC0, 0xC4 }) BE.WF32(d, o, BE.F32(d, o) * sizeK);
        BE.WF32(d, 0x78, -s.HalfWidth); BE.WF32(d, 0x7C, s.Bottom); BE.WF32(d, 0x80, -s.HalfWidth);
        BE.WF32(d, 0x84, s.HalfWidth); BE.WF32(d, 0x88, s.Top); BE.WF32(d, 0x8C, s.HalfWidth);
        BE.W32(d, 0x180, s.FollowCamera ? FollowTag : 0u);
        return d;
    }

    /// <summary>Adds or updates the snow in <paramref name="world"/> (the caller saves the bundle). Returns notes for the log.</summary>
    public static List<string> Apply(Workspace ws, AssetIndex index, CaffFile world, SnowSettings s)
    {
        var notes = new List<string>();
        uint src = SourceBundle(index);
        if (src == 0) throw new InvalidDataException($"Banjoland's snow ({Source1}, {Source2}, {SourceComposite}) was not found in the asset index");
        var b = ws.LoadResident(src);
        var r1 = Data(b, Sym(b, Source1)) ?? throw new InvalidDataException(Source1 + " has no .data");
        var r2 = Data(b, Sym(b, Source2)) ?? throw new InvalidDataException(Source2 + " has no .data");
        if (r1.Length != RecordSize || r2.Length != RecordSize) throw new InvalidDataException("unexpected snow record size");
        var g1 = Tune(r1, s.EmitLarge, s, s.SizeLarge);
        var g2 = Tune(r2, s.EmitSmall, s, s.SizeSmall);
        foreach (var (g, n) in new[] { (g1, "big flakes"), (g2, "small flakes") })
            if (BE.F32(g, 0x13C) * s.LifeMax > BE.U32(g, 0x10)) notes.Add($"warning: {n}: emission x lifetime ({BE.F32(g, 0x13C) * s.LifeMax:F0}) exceeds the particle buffer ({BE.U32(g, 0x10)}), raise the buffer");
        uint id1 = AssetIds.IdOf(Gpu1)!.Value, id2 = AssetIds.IdOf(Gpu2)!.Value, comp = AssetIds.IdOf(Composite)!.Value;
        var compData = new byte[16];
        BE.W32(compData, 0, 4u); BE.W32(compData, 4, id1); BE.W32(compData, 8, 4u); BE.W32(compData, 12, id2);
        foreach (var (name, source, data) in new[] { (Gpu1, Source1, g1), (Gpu2, Source2, g2), (Composite, SourceComposite, compData) })
        {
            int sym = Sym(world, name);
            if (sym == 0) { sym = CaffEdit.CopyAsset(b, Sym(b, source), world, name); notes.Add($"added {name} ({AssetIds.IdOf(name):X8}), a copy of {source} from bundle {src:x6}"); }
            var part = world.PartsOf(sym).First(p => world.SectionOf(p).Name == ".data");
            part.Data = data; part.Size = data.Length;
        }
        // one effect marker (type 30) before the closing record; earlier snow markers are dropped
        int ms = MarkerAsset(world);
        if (ms == 0) throw new InvalidDataException("the world bundle has no marker asset");
        var mp = world.PartsOf(ms).First(p => world.SectionOf(p).Name == ".data");
        var recs = Records(mp.Data);
        if (recs.Count == 0 || BE.U16(recs[^1], 4) != 0) throw new InvalidDataException("marker asset does not end with the closing record");
        recs = recs.Where(r => !(BE.U16(r, 4) == 30 && r.Length >= 0x38 && (BE.U32(r, 0x34) == comp || BE.U32(r, 0x34) == 0x4B97312C))).ToList();
        int next = recs.Take(recs.Count - 1).Select(r => (int)BE.U16(r, 6)).DefaultIfEmpty(-1).Max() + 1;
        var m = new byte[0x38];
        BE.W32(m, 0, 0x38u); BE.W16(m, 4, 30); BE.W16(m, 6, (ushort)next);
        BE.WF32(m, 0x14, s.Position.X); BE.WF32(m, 0x18, s.Position.Y); BE.WF32(m, 0x1C, s.Position.Z);
        BE.WF32(m, 0x2C, 1f); BE.W32(m, 0x34, comp);
        recs.Insert(recs.Count - 1, m);
        mp.Data = recs.SelectMany(r => r).ToArray(); mp.Size = mp.Data.Length;
        notes.Add($"effect marker #{next} at {s.Position} in {AssetIds.DisplayName(world.Symbols[ms - 1])}");
        notes.Add($"snow: {s.EmitLarge:G5} + {s.EmitSmall:G5} flakes/s, size x{s.SizeLarge:G3} / x{s.SizeSmall:G3}, life {s.LifeMin:G3}-{s.LifeMax:G3} s, box ±{s.HalfWidth:G4} from {s.Bottom:G4} to {s.Top:G4} above the {(s.FollowCamera ? "camera" : "marker")}");
        return notes;
    }

    /// <summary>Takes the snow out of <paramref name="world"/> again (assets, manifest entries, effect marker).</summary>
    public static List<string> Remove(CaffFile world)
    {
        var notes = new List<string>();
        uint comp = AssetIds.IdOf(Composite)!.Value;
        int ms = MarkerAsset(world);
        if (ms > 0)
        {
            var mp = world.PartsOf(ms).First(p => world.SectionOf(p).Name == ".data");
            var recs = Records(mp.Data);
            int before = recs.Count;
            recs = recs.Where(r => !(BE.U16(r, 4) == 30 && r.Length >= 0x38 && BE.U32(r, 0x34) == comp)).ToList();
            if (recs.Count != before) { mp.Data = recs.SelectMany(r => r).ToArray(); mp.Size = mp.Data.Length; notes.Add($"removed {before - recs.Count} snow marker(s)"); }
        }
        foreach (var name in new[] { Composite, Gpu2, Gpu1 })
        {
            int sym = Sym(world, name);
            if (sym > 0) { CaffEdit.RemoveAsset(world, sym); notes.Add("removed " + name); }
        }
        return notes;
    }

    static List<byte[]> Records(byte[] d)
    {
        var l = new List<byte[]>();
        int o = 0;
        while (o + 0x30 <= d.Length)
        {
            int size = BE.S32(d, o);
            if (size < 0x30 || o + size > d.Length) break;
            l.Add(d[o..(o + size)]); o += size;
        }
        if (o != d.Length) throw new InvalidDataException("marker records do not cover the asset");
        return l;
    }
}
