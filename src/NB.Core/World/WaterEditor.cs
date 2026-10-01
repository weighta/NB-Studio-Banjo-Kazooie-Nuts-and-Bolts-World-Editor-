using System.Numerics;
using NB.Core.Formats;
using NB.Core.IO;

namespace NB.Core.World;

/// <summary>
/// Water surfaces of a world: chunk 38 of the background model (docs/FORMATS.md §18).
/// <code>
/// chunk 38:  u32 regionCount, → regions (0xA0 each), 0, 0
/// region:    +0 u32 index, +4 u32 kind (2 = sea, 1 = pool), +8 → surface header, +0xC u32 vertex count,
///            +0x10 → vertices (.gpu, float3 triangle soup: the rendered surface), +0x14..+0x1F 0,
///            +0x20 4 × float4 corner points (AABB corners at min y), +0x60 float4 AABB min, +0x70 float4 AABB max,
///            +0x80 float4 cell size (x, 0, z, 0), +0x90 u32 nx, u32 nz, → cells, 0
/// surface:   u32 0, u32 triangleCount, u32 0, float3 AABB min, float3 AABB max, then the cells (nx*nz × (u32 count, → records))
/// record:    f32 height, u32 0, u32 triangleCount, → triangles (2D: 3 × (x, z) float pairs = 24 bytes)
/// </code>
/// A cell lists the triangles overlapping it, grouped by height. Water triangles are horizontal (one height each).
/// </summary>
public static class WaterEditor
{
    public sealed class Region
    {
        public int Kind = 2;
        /// <summary>Horizontal triangles (3 points each, same Y), counter-clockwise seen from above.</summary>
        public List<Vector3> Triangles = new();
    }

    const int RegionSize = 0xA0, Grid = 16;

    public static List<Region> Read(CaffFile caff, int symbol)
    {
        var v = new AssetView(caff, symbol);
        var d = v.Data(".data"); var g = v.Data(".gpu");
        int c38 = Chunk(d, 38);
        var list = new List<Region>();
        if (c38 < 0) return list;
        int n = BE.S32(d, c38), regs = BE.S32(d, c38 + 4);
        for (int i = 0; i < n; i++)
        {
            int r = regs + RegionSize * i;
            var reg = new Region { Kind = BE.S32(d, r + 4) };
            int nv = BE.S32(d, r + 0xC), vo = BE.S32(d, r + 0x10);
            for (int k = 0; k < nv; k++) reg.Triangles.Add(new Vector3(BE.F32(g, vo + 12 * k), BE.F32(g, vo + 12 * k + 4), BE.F32(g, vo + 12 * k + 8)));
            list.Add(reg);
        }
        return list;
    }

    static int Chunk(byte[] d, int id)
    {
        int t = BE.S32(d, 0), cn = BE.S32(d, 4);
        for (int i = 0; i < cn; i++) if (BE.S32(d, t + 8 * i) == id) return BE.S32(d, t + 8 * i + 4);
        return -1;
    }

    /// <summary>Replaces the world's water with <paramref name="regions"/> (new chunk 38 appended to .data, vertices
    /// appended to .gpu, chunk table repointed, relocations added for every new pointer).</summary>
    public static (int Regions, int Triangles) Write(CaffFile caff, int symbol, IReadOnlyList<Region> regions)
    {
        var v = new AssetView(caff, symbol);
        int dataPid = v.PartId(".data"), gpuPid = v.PartId(".gpu");
        var dataPart = v.Part(dataPid); var gpuPart = v.Part(gpuPid);
        var d = new List<byte>(dataPart.Data); var g = new List<byte>(gpuPart.Data);
        var selfPtrs = new List<int>(); var gpuPtrs = new List<int>();
        int t = BE.S32(dataPart.Data, 0), cn = BE.S32(dataPart.Data, 4), entry = -1;
        for (int i = 0; i < cn; i++) if (BE.S32(dataPart.Data, t + 8 * i) == 38) entry = t + 8 * i + 4;
        if (entry < 0) throw new InvalidDataException("this world has no water chunk (38)");

        void Align(List<byte> b, int a) { while (b.Count % a != 0) b.Add(0); }
        int Alloc(List<byte> b, int size, int align = 16) { Align(b, align); int at = b.Count; b.AddRange(new byte[size]); return at; }
        void W32(int o, uint val) { d[o] = (byte)(val >> 24); d[o + 1] = (byte)(val >> 16); d[o + 2] = (byte)(val >> 8); d[o + 3] = (byte)val; }
        void WF(int o, float f) => W32(o, BitConverter.SingleToUInt32Bits(f));
        void WF4(int o, Vector3 p, float w = 0) { WF(o, p.X); WF(o + 4, p.Y); WF(o + 8, p.Z); WF(o + 12, w); }

        int chunk = Alloc(d, 16);
        int regs = Alloc(d, RegionSize * regions.Count);
        W32(chunk, (uint)regions.Count); W32(chunk + 4, (uint)regs); selfPtrs.Add(chunk + 4);
        int totalTris = 0;
        for (int ri = 0; ri < regions.Count; ri++)
        {
            var reg = regions[ri];
            if (reg.Triangles.Count % 3 != 0 || reg.Triangles.Count == 0) throw new ArgumentException($"water region {ri}: triangle list is empty or not a multiple of 3");
            int nt = reg.Triangles.Count / 3; totalTris += nt;
            var mn = reg.Triangles.Aggregate(Vector3.Min); var mx = reg.Triangles.Aggregate(Vector3.Max);
            if (mx.X - mn.X < 1e-3f) mx.X = mn.X + 1; if (mx.Z - mn.Z < 1e-3f) mx.Z = mn.Z + 1;
            float cx = (mx.X - mn.X) / Grid, cz = (mx.Z - mn.Z) / Grid;
            // vertices (.gpu)
            Align(g, 16); int vo = g.Count;
            foreach (var p in reg.Triangles) { var b = new byte[12]; BE.WF32(b, 0, p.X); BE.WF32(b, 4, p.Y); BE.WF32(b, 8, p.Z); g.AddRange(b); }
            // surface header + cells
            int surf = Alloc(d, 0x24 + 8 * Grid * Grid, 16);
            W32(surf + 4, (uint)nt);
            WF(surf + 0xC, mn.X); WF(surf + 0x10, mn.Y); WF(surf + 0x14, mn.Z); WF(surf + 0x18, mx.X); WF(surf + 0x1C, mx.Y); WF(surf + 0x20, mx.Z);
            int cells = surf + 0x48;   // as in the shipped worlds (+0x24..+0x47: a second AABB in Showdown Town's sea, zero elsewhere)
            while (d.Count < cells + 8 * Grid * Grid) d.Add(0);
            for (int cz_ = 0; cz_ < Grid; cz_++)
                for (int cx_ = 0; cx_ < Grid; cx_++)
                {
                    float x0 = mn.X + cx * cx_, x1 = x0 + cx, z0 = mn.Z + cz * cz_, z1 = z0 + cz;
                    var inCell = new List<int>();
                    for (int k = 0; k < nt; k++)
                    {
                        var a = reg.Triangles[3 * k]; var b = reg.Triangles[3 * k + 1]; var c = reg.Triangles[3 * k + 2];
                        if (MathF.Max(a.X, MathF.Max(b.X, c.X)) < x0 || MathF.Min(a.X, MathF.Min(b.X, c.X)) > x1) continue;
                        if (MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) < z0 || MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) > z1) continue;
                        inCell.Add(k);
                    }
                    if (inCell.Count == 0) continue;
                    var byHeight = inCell.GroupBy(k => MathF.Round(reg.Triangles[3 * k].Y, 3)).ToList();
                    int recs = Alloc(d, 16 * byHeight.Count, 16);
                    int cellSlot = cells + 8 * (cz_ * Grid + cx_);
                    W32(cellSlot, (uint)byHeight.Count); W32(cellSlot + 4, (uint)recs); selfPtrs.Add(cellSlot + 4);
                    for (int h = 0; h < byHeight.Count; h++)
                    {
                        var ks = byHeight[h].ToList();
                        int tris = Alloc(d, 24 * ks.Count, 4);
                        int rec = recs + 16 * h;
                        WF(rec, byHeight[h].Key); W32(rec + 8, (uint)ks.Count); W32(rec + 12, (uint)tris); selfPtrs.Add(rec + 12);
                        for (int q = 0; q < ks.Count; q++)
                            for (int e = 0; e < 3; e++)
                            {
                                var p = reg.Triangles[3 * ks[q] + e];
                                WF(tris + 24 * q + 8 * e, p.X); WF(tris + 24 * q + 8 * e + 4, p.Z);
                            }
                    }
                }
            // region record
            int r = regs + RegionSize * ri;
            W32(r, (uint)ri); W32(r + 4, (uint)reg.Kind);
            W32(r + 8, (uint)surf); selfPtrs.Add(r + 8);
            W32(r + 0xC, (uint)reg.Triangles.Count);
            W32(r + 0x10, (uint)vo); gpuPtrs.Add(r + 0x10);
            var corners = new[] { new Vector3(mn.X, mn.Y, mn.Z), new Vector3(mx.X, mn.Y, mn.Z), new Vector3(mn.X, mn.Y, mx.Z), new Vector3(mx.X, mn.Y, mx.Z) };
            for (int k = 0; k < 4; k++) WF4(r + 0x20 + 16 * k, corners[k]);
            WF4(r + 0x60, mn); WF4(r + 0x70, mx);
            WF4(r + 0x80, new Vector3(cx, 0, cz));
            W32(r + 0x90, Grid); W32(r + 0x94, Grid); W32(r + 0x98, (uint)cells); selfPtrs.Add(r + 0x98);
        }
        Align(d, 16); Align(g, 32);
        var nd = d.ToArray();
        BE.W32(nd, entry, chunk);   // chunk table slot (already relocated)
        dataPart.Data = nd; dataPart.Size = nd.Length;
        gpuPart.Data = g.ToArray(); gpuPart.Size = gpuPart.Data.Length;
        AddRelocs(caff, dataPid, dataPid, selfPtrs);
        AddRelocs(caff, dataPid, gpuPid, gpuPtrs);
        return (regions.Count, totalTris);
    }

    static void AddRelocs(CaffFile caff, int from, int to, List<int> offs)
    {
        if (offs.Count == 0) return;
        var r = caff.Relocs.FirstOrDefault(x => x.FromPart == from && x.ToPart == to);
        if (r == null) { caff.Relocs.Add(new CaffReloc(from, to, offs.OrderBy(x => x).ToArray())); return; }
        r.Offsets = r.Offsets.Concat(offs).Distinct().OrderBy(x => x).ToArray();
    }

    /// <summary>Water triangles from a mesh (OBJ): every triangle is flattened to its average height.</summary>
    public static List<Vector3> FromMesh(IEnumerable<NB.Core.Models.ImportMesh> meshes)
    {
        var t = new List<Vector3>();
        foreach (var m in meshes)
            for (int i = 0; i + 2 < m.Triangles.Count; i += 3)
            {
                var a = m.Positions[m.Triangles[i]]; var b = m.Positions[m.Triangles[i + 1]]; var c = m.Positions[m.Triangles[i + 2]];
                float y = (a.Y + b.Y + c.Y) / 3;
                t.Add(a with { Y = y }); t.Add(b with { Y = y }); t.Add(c with { Y = y });
            }
        return t;
    }
}
