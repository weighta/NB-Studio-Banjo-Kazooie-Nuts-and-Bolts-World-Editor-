using System.Numerics;
using NB.Core.IO;
using NB.Core.Models;

namespace NB.Core.World;

/// <summary>
/// One grass layer of a background (or reference) model's chunk 17: a box of the level that the game covers with grass
/// tiles at run time. Decoded from the game (2026-10-05, Spiral Mountain title screen, tile records read from guest memory
/// and the grass vertex shader; docs/FORMATS.md "Grass layers"):
/// <list type="bullet">
/// <item>Record (0xC4 bytes): +0x00 grass model id, +0x04 shadow texture id (the name without the _0/_1/… lighting suffix),
/// +0x08 box min, +0x14 box max, +0x20 tile spacing, +0x24 ?, +0x28 ?, +0x2C ?, +0x30 draw range in tiles (range =
/// value × spacing), +0x34 position jitter (fraction of the spacing, either way), +0x38 height texture id, +0x3C / +0x40
/// tile height scale min / max, +0x44 shadow texture name (stale bytes after its terminator).</item>
/// <item>Tiles: one per grid cell (spacing × spacing from the box min corner) whose shadow-texture alpha is non-zero
/// somewhere in the cell (1035 of 1035 cells checked); position = cell centre + jitter, y = box min y; turned by a random
/// multiple of 90° about Y; heights scaled by a random factor in [+0x3C, +0x40].</item>
/// <item>Vertex shader: the tile mesh is drawn 30 tiles per draw (index = vertex × 30 + tile slot); world xz → texture
/// coordinates u = (x − min.x) / size.x, v = (z − min.z) / size.z; y += height texture (red) × (max.y − min.y); colour =
/// vertex colour × shadow texture rgb; a blade is dropped where its vertex alpha exceeds the shadow alpha (density).</item>
/// <item>LOD: the grass model's LOD table selects the rendergraph nodes (density levels) by tile distance.</item>
/// </list>
/// </summary>
public sealed class GrassLayer
{
    public const int RecordSize = 0xC4;
    /// <summary>Tiles per draw in the grass vertex shader (c132 = 1/30, −30): index = vertex × 30 + slot.</summary>
    public const int TilesPerDraw = 30;

    public int Index;
    public uint ModelId, ShadowId, HeightId;
    public Vector3 Min, Max;
    public float Spacing, Range, Jitter, ScaleMin, ScaleMax;
    public float F24, F28, F2C;
    /// <summary>Shadow texture name stored in the record (aid_texture_…_shadowN); the files carry a lighting suffix _0, _1, ….</summary>
    public string ShadowName = "";
    /// <summary>Resolved names (set by the scene loader).</summary>
    public string ModelName = "", HeightName = "";
    /// <summary>Where the layer was found (background model or the reference model instance holding it).</summary>
    public string Source = "";
    public ModelAsset? Model;
    public GrassMesh? Mesh;
    /// <summary>Tiles laid out by <see cref="BuildTiles"/> (replaced as a whole, so a viewer can lay out in the background).</summary>
    public List<GrassTile> Tiles = new();

    public Vector3 Size => Max - Min;
    public float MaxDistance => Range > 0 ? Range * Spacing : 150;

    /// <summary>Reads the layers of a model's chunk 17 (u32 count, then records), moved by <paramref name="placement"/>
    /// (the instance that places a reference model holding the layers; translation and uniform scale).</summary>
    public static List<GrassLayer> Read(byte[] data, int chunk, Matrix4x4 placement, string source)
    {
        var list = new List<GrassLayer>();
        int n = BE.S32(data, chunk);
        if (n < 0 || n > 4096 || chunk + 4 + n * RecordSize > data.Length) return list;
        float sc = new Vector3(placement.M11, placement.M12, placement.M13).Length();
        if (sc <= 0) sc = 1;
        var t = placement.Translation;
        for (int i = 0; i < n; i++)
        {
            int r = chunk + 4 + RecordSize * i;
            var mn = new Vector3(BE.F32(data, r + 8), BE.F32(data, r + 12), BE.F32(data, r + 16)) * sc + t;
            var mx = new Vector3(BE.F32(data, r + 20), BE.F32(data, r + 24), BE.F32(data, r + 28)) * sc + t;
            var l = new GrassLayer
            {
                Index = i, Source = source, ModelId = BE.U32(data, r), ShadowId = BE.U32(data, r + 4), HeightId = BE.U32(data, r + 0x38),
                Min = Vector3.Min(mn, mx), Max = Vector3.Max(mn, mx),
                Spacing = BE.F32(data, r + 0x20) * sc, F24 = BE.F32(data, r + 0x24), F28 = BE.F32(data, r + 0x28), F2C = BE.F32(data, r + 0x2C),
                Range = BE.F32(data, r + 0x30), Jitter = BE.F32(data, r + 0x34), ScaleMin = BE.F32(data, r + 0x3C), ScaleMax = BE.F32(data, r + 0x40),
                ShadowName = BE.CStr(data, r + 0x44, RecordSize - 0x44),
            };
            if (!(l.Spacing > 0.05f) || !float.IsFinite(l.Spacing)) l.Spacing = 6;
            if (!(l.ScaleMax >= l.ScaleMin)) l.ScaleMax = l.ScaleMin;
            if (!(l.ScaleMin > 0)) { l.ScaleMin = 1; l.ScaleMax = Math.Max(1, l.ScaleMax); }
            if (!float.IsFinite(l.Jitter) || l.Jitter < 0 || l.Jitter > 0.5f) l.Jitter = 0.2f;
            list.Add(l);
        }
        return list;
    }

    /// <summary>Lays the tiles out from the shadow texture's alpha (density): one tile in every cell with any alpha.
    /// Jitter, turn and height scale are pseudo-random per cell (the game's own random sequence is not reproduced).</summary>
    public void BuildTiles(byte[] rgba, int w, int h) => Tiles = LayOut(rgba, w, h);

    /// <summary>The tiles <see cref="BuildTiles"/> would set, without touching <see cref="Tiles"/>.</summary>
    public List<GrassTile> LayOut(byte[] rgba, int w, int h)
    {
        var tiles = new List<GrassTile>();
        var size = Size;
        if (w <= 0 || h <= 0 || size.X <= 0 || size.Z <= 0) return tiles;
        int nx = (int)MathF.Ceiling(size.X / Spacing), nz = (int)MathF.Ceiling(size.Z / Spacing);
        if ((long)nx * nz > 400_000) return tiles;
        for (int j = 0; j < nz; j++)
        {
            int y0 = Math.Clamp((int)(j * Spacing / size.Z * h), 0, h - 1), y1 = Math.Clamp((int)((j + 1) * Spacing / size.Z * h), 0, h - 1);
            for (int i = 0; i < nx; i++)
            {
                int x0 = Math.Clamp((int)(i * Spacing / size.X * w), 0, w - 1), x1 = Math.Clamp((int)((i + 1) * Spacing / size.X * w), 0, w - 1);
                bool any = false;
                for (int y = y0; y <= y1 && !any; y++)
                    for (int x = x0; x <= x1; x++) if (rgba[(y * w + x) * 4 + 3] > 0) { any = true; break; }
                if (!any) continue;
                uint s = Hash((uint)(i * 73856093) ^ (uint)(j * 19349663) ^ (uint)(Index * 83492791));
                float jx = (Rand(ref s) * 2 - 1) * Jitter, jz = (Rand(ref s) * 2 - 1) * Jitter;
                int rot = (int)(Rand(ref s) * 4) & 3;
                float sy = ScaleMin + (ScaleMax - ScaleMin) * Rand(ref s);
                tiles.Add(new GrassTile(new Vector3(Min.X + (i + 0.5f + jx) * Spacing, Min.Y, Min.Z + (j + 0.5f + jz) * Spacing), rot, sy));
            }
        }
        return tiles;
    }

    static uint Hash(uint x) { x ^= x >> 16; x *= 0x7feb352d; x ^= x >> 15; x *= 0x846ca68b; x ^= x >> 16; return x; }
    static float Rand(ref uint s) { s = Hash(s + 0x9E3779B9); return (s & 0xFFFFFF) / 16777216f; }
}

/// <summary>A grass tile: position (y = the layer's min y; the height texture lifts each vertex), turn (multiples of 90°
/// about Y) and height scale.</summary>
public readonly record struct GrassTile(Vector3 Position, int Rotation, float ScaleY);

/// <summary>
/// The blades of a grass model as one tile: vertices (position, uv, colour) and, per rendergraph node (density level), the
/// triangles of one tile slot. The model's draws pack 30 tiles per index buffer (index = vertex × 30 + slot); every slot
/// holds the same triangles, so slot 0 is kept.
/// </summary>
public sealed class GrassMesh
{
    public Vector3[] Positions = Array.Empty<Vector3>();
    public Vector2[] UVs = Array.Empty<Vector2>();
    public uint[] Colors = Array.Empty<uint>();
    /// <summary>Per node: diffuse texture and triangle indices into the vertex arrays.</summary>
    public readonly List<(int Node, string? Texture, int[] Indices)> Parts = new();
    /// <summary>LOD levels of the model (switch distance, nodes drawn from there on).</summary>
    public List<(float Distance, HashSet<int> Nodes)> Levels = new();

    public static GrassMesh? From(ModelAsset m)
    {
        var g = new GrassMesh();
        var pos = new List<Vector3>(); var uv = new List<Vector2>(); var col = new List<uint>();
        var baseOf = new Dictionary<MeshDraw, int>();
        foreach (var d in m.Draws)
        {
            if (d.Positions.Length == 0 || d.Indices.Length < 3) continue;
            // the raw indices: vertex × K + slot as decoded by the model parser, re-split with the grass shader's 30
            var raw = new int[d.Indices.Length];
            for (int i = 0; i < raw.Length; i++) raw[i] = d.Indices[i] * d.InstanceStride + (d.Slots != null && i < d.Slots.Length ? d.Slots[i] : 0);
            var tris = new List<int>();
            bool fits = raw.Max() / GrassLayer.TilesPerDraw < d.Positions.Length;
            for (int t = 0; t + 2 < raw.Length; t += 3)
            {
                int a = raw[t], b = raw[t + 1], c = raw[t + 2];
                if (fits)
                {
                    if (a % GrassLayer.TilesPerDraw != 0 || b % GrassLayer.TilesPerDraw != 0 || c % GrassLayer.TilesPerDraw != 0) continue;   // slot 0 only
                    a /= GrassLayer.TilesPerDraw; b /= GrassLayer.TilesPerDraw; c /= GrassLayer.TilesPerDraw;
                }
                else { a = d.Indices[t]; b = d.Indices[t + 1]; c = d.Indices[t + 2]; }
                if (a >= d.Positions.Length || b >= d.Positions.Length || c >= d.Positions.Length) continue;
                if (!baseOf.TryGetValue(d, out int bse))
                {
                    bse = pos.Count; baseOf[d] = bse;
                    pos.AddRange(d.Positions);
                    uv.AddRange(d.UVs ?? new Vector2[d.Positions.Length]);
                    col.AddRange(d.Colors ?? Enumerable.Repeat(0xFFFFFFFFu, d.Positions.Length));
                }
                tris.Add(bse + a); tris.Add(bse + b); tris.Add(bse + c);
            }
            if (tris.Count == 0) continue;
            string? tex = d.Textures.Where(t => t.Slot == 0).Select(t => t.Texture).FirstOrDefault() ?? d.Textures.Select(t => t.Texture).FirstOrDefault();
            g.Parts.Add((d.Node, tex, tris.ToArray()));
        }
        if (g.Parts.Count == 0) return null;
        g.Positions = pos.ToArray(); g.UVs = uv.ToArray(); g.Colors = col.ToArray();
        g.Levels = m.LodLevels.Count > 0 ? m.LodLevels[0] : new() { (0f, g.Parts.Select(p => p.Node).ToHashSet()) };
        return g;
    }

    /// <summary>Nodes drawn at a view distance: the last LOD level whose switch distance is not beyond it (an empty level
    /// culls the tile).</summary>
    public HashSet<int>? NodesAt(float distance)
    {
        HashSet<int>? set = null;
        foreach (var (d, nodes) in Levels) if (d <= distance) set = nodes;
        return set ?? Levels.FirstOrDefault().Nodes;
    }
}
