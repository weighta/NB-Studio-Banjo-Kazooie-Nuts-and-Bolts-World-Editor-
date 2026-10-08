using System.Numerics;
using NB.Core.Models;

namespace NB.Core.World;

public sealed partial class SceneObject
{
    /// <summary>Water objects (<see cref="SceneObjectKind.Water"/>): index of the water region (background model chunk 38).</summary>
    public int WaterRegion = -1;
    /// <summary>The region's surface triangles relative to the object's origin (the transform places them).</summary>
    public Vector3[] WaterTriangles = Array.Empty<Vector3>();
    /// <summary>The region record's plane corners relative to the origin, when they reach far beyond the triangles (the open
    /// sea around Nutty Acres): drawn and moved with this object; empty otherwise (the plane is then the triangles' box).</summary>
    public Vector3[] WaterPlane = Array.Empty<Vector3>();
    /// <summary>Other regions carrying the same sea plane (they get this object's plane when it is saved).</summary>
    public List<int> WaterPlaneShared = new();
    /// <summary>The sky dome the 3D view draws (a <see cref="SceneObjectKind.Terrain"/>-kind stand-in added by the viewport:
    /// selectable, its textures editable; it has no stored transform).</summary>
    public bool IsSkyDome;
    /// <summary>Sky dome: true when the level script centres it on the camera (Showdown Town), false when it stands at the
    /// world origin (Nutty Acres' bluesky, Spiral Mountain).</summary>
    public bool SkyFollowsCamera;
    /// <summary>Markers: the class of the objparams the marker places (objparams +0x42, e.g. "objDefId_entityAvatarBall"),
    /// or null.</summary>
    public string? ObjClass;
}

/// <summary>
/// Water as editable objects: every region of the background model's water chunk (38) is a
/// <see cref="SceneObjectKind.Water"/> object whose model is its surface. Moving, scaling or turning it about the vertical
/// axis and saving rewrites the chunk with the transformed triangles (<see cref="WaterEditor.Write"/>). The game's water
/// is flat: only the rotation about Y is kept (the record holds horizontal triangles grouped by height per cell).
/// The sea plane of Nutty Acres (a ±943-unit square shared by its three regions, at y −35 while the sea surface is at the
/// lowest region's −30.36) belongs to the region with the lowest surface; that object draws the plane and carries it.
/// </summary>
public sealed partial class WorldScene
{
    public List<WaterEditor.Region> WaterRegions = new();

    void AddWater(ref int id)
    {
        try { WaterRegions = WaterEditor.Read(Caff, Background.View.Symbol); }
        catch (Exception e) { Log.Add("water: " + e.Message); WaterRegions = new(); return; }
        // a sea plane: reaches far beyond the region's own triangles
        static bool Big(WaterEditor.Region r)
        {
            if (r.Plane.Length != 4 || r.Triangles.Count < 3) return false;
            var pmn = r.Plane.Aggregate(Vector3.Min); var pmx = r.Plane.Aggregate(Vector3.Max);
            var tmn = r.Triangles.Aggregate(Vector3.Min); var tmx = r.Triangles.Aggregate(Vector3.Max);
            return (pmx.X - pmn.X) * (pmx.Z - pmn.Z) >= 2 * Math.Max(1, (tmx.X - tmn.X) * (tmx.Z - tmn.Z));
        }
        static (float, float, float, float) PlaneKey(WaterEditor.Region r) { var mn = r.Plane.Aggregate(Vector3.Min); var mx = r.Plane.Aggregate(Vector3.Max); return (mn.X, mn.Z, mx.X, mx.Z); }
        var seaOwner = new Dictionary<(float, float, float, float), int>();
        for (int k = 0; k < WaterRegions.Count; k++)
            if (Big(WaterRegions[k]))
            {
                var key = PlaneKey(WaterRegions[k]);
                if (!seaOwner.TryGetValue(key, out int o) || WaterRegions[k].Triangles.Min(p => p.Y) < WaterRegions[o].Triangles.Min(p => p.Y)) seaOwner[key] = k;
            }
        for (int k = 0; k < WaterRegions.Count; k++)
        {
            var r = WaterRegions[k];
            if (r.Triangles.Count < 3) continue;
            var mn = r.Triangles.Aggregate(Vector3.Min); var mx = r.Triangles.Aggregate(Vector3.Max);
            var origin = new Vector3((mn.X + mx.X) / 2, mn.Y, (mn.Z + mx.Z) / 2);
            bool sea = Big(r) && seaOwner[PlaneKey(r)] == k;
            var obj = new SceneObject
            {
                Id = id++, Kind = SceneObjectKind.Water, WaterRegion = k, ModelBundle = Bundle & 0xFFFFFF,
                // a stable name (undo steps find the object again by it after a save reloads the world)
                Name = $"Water {k + 1}{(sea ? " (open sea)" : "")}",
                ModelName = $"water region {k + 1} (chunk 38): y {mn.Y:0.##}{(mx.Y - mn.Y > 0.01f ? $"..{mx.Y:0.##}" : "")}, {r.Triangles.Count / 3} triangles, kind {r.Kind}",
                Transform = Matrix4x4.CreateTranslation(origin),
                WaterTriangles = r.Triangles.Select(p => p - origin).ToArray(),
            };
            obj.OriginalTransform = obj.Transform;
            if (sea)
            {
                obj.WaterPlane = r.Plane.Select(p => p - origin).ToArray();
                for (int j = 0; j < WaterRegions.Count; j++) if (j != k && Big(WaterRegions[j]) && PlaneKey(WaterRegions[j]) == PlaneKey(r)) obj.WaterPlaneShared.Add(j);
            }
            obj.Model = WaterModel(obj, sea);
            obj.BoundsMin = obj.Model.Draws.SelectMany(d => d.Positions).Aggregate(Vector3.Min) - new Vector3(0, 0.5f, 0);
            obj.BoundsMax = obj.Model.Draws.SelectMany(d => d.Positions).Aggregate(Vector3.Max) + new Vector3(0, 0.5f, 0);
            Objects.Add(obj);
        }
    }

    /// <summary>The drawn surface of a water object, in its own space: the region's triangles, or for the sea the plane as
    /// a 24 × 24 grid at the surface height (the region's own triangles at that height are part of it).</summary>
    static ModelAsset WaterModel(SceneObject o, bool sea)
    {
        var m = new ModelAsset();
        Vector3[] pos; int[] idx;
        if (sea)
        {
            var pmn = o.WaterPlane.Aggregate(Vector3.Min); var pmx = o.WaterPlane.Aggregate(Vector3.Max);
            const int N = 24;
            var p = new List<Vector3>(); var ix = new List<int>();
            for (int j = 0; j <= N; j++)
                for (int i = 0; i <= N; i++) p.Add(new Vector3(pmn.X + (pmx.X - pmn.X) * i / N, 0, pmn.Z + (pmx.Z - pmn.Z) * j / N));
            for (int j = 0; j < N; j++)
                for (int i = 0; i < N; i++) { int a = j * (N + 1) + i, b = a + 1, c = a + N + 1, d = c + 1; ix.AddRange(new[] { a, c, b, b, c, d }); }
            pos = p.ToArray(); idx = ix.ToArray();
        }
        else
        {
            int n = o.WaterTriangles.Length - o.WaterTriangles.Length % 3;
            pos = o.WaterTriangles.Take(n).ToArray(); idx = Enumerable.Range(0, n).ToArray();
        }
        m.Draws.Add(new MeshDraw
        {
            Positions = pos, Normals = Enumerable.Repeat(Vector3.UnitY, pos.Length).ToArray(),
            UVs = pos.Select(q => new Vector2(q.X, q.Z) * 0.02f).ToArray(), Indices = idx, SectionFlags = 0x4502,
        });
        return m;
    }

    /// <summary>A water object's transform as the game can hold it: scale and position, rotation about Y only.</summary>
    public static Matrix4x4 WaterTransform(Matrix4x4 t)
    {
        if (!Matrix4x4.Decompose(t, out var s, out var q, out var tr)) return t;
        var fwd = Vector3.Transform(Vector3.UnitZ, q);
        float yaw = MathF.Atan2(fwd.X, fwd.Z);
        return Matrix4x4.CreateScale(s) * Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(tr);
    }

    /// <summary>Writes the water regions again when a water object was moved (the whole chunk 38 is rebuilt).</summary>
    int ApplyWaterEdits()
    {
        var water = Objects.Where(o => o.Kind == SceneObjectKind.Water && o.WaterRegion >= 0).ToList();
        if (!water.Any(o => o.Dirty)) return 0;
        var regions = WaterRegions.Select(r => new WaterEditor.Region { Kind = r.Kind, Triangles = new List<Vector3>(r.Triangles), Plane = r.Plane }).ToList();
        int moved = 0;
        foreach (var o in water)
        {
            var t = WaterTransform(o.Transform);
            var reg = regions[o.WaterRegion];
            reg.Triangles = o.WaterTriangles.Select(p => Vector3.Transform(p, t)).ToList();
            // each triangle stays horizontal (scaling Y spreads heights apart: they keep one height per triangle)
            for (int i = 0; i + 2 < reg.Triangles.Count; i += 3)
            {
                float y = (reg.Triangles[i].Y + reg.Triangles[i + 1].Y + reg.Triangles[i + 2].Y) / 3;
                for (int k = 0; k < 3; k++) reg.Triangles[i + k] = reg.Triangles[i + k] with { Y = y };
            }
            if (o.WaterPlane.Length == 4)
            {
                var plane = o.WaterPlane.Select(p => Vector3.Transform(p, t)).ToArray();
                reg.Plane = plane;
                foreach (int j in o.WaterPlaneShared) regions[j].Plane = plane;
            }
            else if (o.Dirty) reg.Plane = Array.Empty<Vector3>();   // the writer uses the moved triangles' box
            if (o.Dirty) moved++;
        }
        WaterEditor.Write(Caff, Background.View.Symbol, regions);
        WaterRegions = regions;
        foreach (var o in water) { o.Transform = WaterTransform(o.Transform); o.OriginalTransform = o.Transform; }
        DirtyBundles.Add(Bundle);
        Log.Add($"water: {moved} region(s) moved, chunk 38 rewritten ({regions.Count} regions)");
        return moved;
    }
}
