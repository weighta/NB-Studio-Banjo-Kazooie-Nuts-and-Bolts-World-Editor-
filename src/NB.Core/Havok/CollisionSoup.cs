using System.Numerics;
using NB.Core.Formats;
using NB.Core.IO;

namespace NB.Core.Havok;

/// <summary>
/// An editable copy of one mesh collision asset (aid_havok_* with a wrapper type-1 entry: the world/terrain collision and
/// the collision of scenery models): every shape of it as one triangle list in the asset's space, with a material index
/// per triangle. Triangles can be deleted, moved, duplicated and added; <see cref="WriteTo"/> rebuilds the asset with
/// <see cref="HkCollisionImport.Replace"/> (new extended-mesh buffers + MOPP) and verifies it by decoding it again.
/// Shape subparts (boxes, convex pieces) become triangles of the mesh when the asset is written (the game treats both
/// as solid surfaces; this is what Import Collision already does).
/// </summary>
public sealed class CollisionSoup
{
    public string Asset = "";
    public List<Vector3> P = new();
    public List<int> T = new();
    /// <summary>Material index (into the asset's material table) per triangle.</summary>
    public List<byte> Mat = new();
    /// <summary>Per triangle: which decoded shape it came from ("mesh 0", "convex 3", "added box" …).</summary>
    public List<string> Shape = new();
    /// <summary>Changed since loaded / last written.</summary>
    public bool Dirty;
    public int OriginalTriangles;
    public long AssetBytes;

    public int Triangles => T.Count / 3;

    /// <summary>Why the asset cannot be edited (null: it can).</summary>
    public static string? Problem(byte[] asset)
    {
        if (!HkPackfile.IsPackfileAsset(asset)) return "no Havok data (characters and other actors get their physics body at run time)";
        if (HkCollisionImport.TypeOneEntry(asset) < 0) return "not a mesh collision asset (rigid bodies / physics objects)";
        if (HkCollisionImport.IsBreakable(asset)) return "breakable scenery (its pieces are a physics system; rewriting it froze the game)";
        try { HkCollisionImport.ReplaceData(asset, new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitZ }, new[] { 0, 1, 2 }); }
        catch (Exception e) { return e.Message; }
        return null;
    }

    /// <summary>Reads asset <paramref name="symbol"/> of <paramref name="caff"/>. Null (with the reason) when it cannot be edited.</summary>
    public static CollisionSoup? Load(CaffFile caff, int symbol, out string? why)
    {
        var d = new AssetView(caff, symbol).Data(".data");
        why = Problem(d);
        if (why != null) return null;
        var hc = HkCollision.ExtractAsset(d);
        var s = new CollisionSoup { Asset = AssetIds.DisplayName(caff.Symbols[symbol - 1]), AssetBytes = d.Length };
        // per-triangle materials of triangle subpart 0 (the game plugs E+0x18 / E+0x1C into it)
        int e = HkCollisionImport.TypeOneEntry(d);
        int matIdx = BE.S32(d, e + 0x18), numMat = BE.S32(d, e + 0x20), sub0 = BE.S32(d, e + 0xC) / 3;
        byte common = 0;
        byte[]? mats = matIdx > 0 && BE.S32(d, e + 0x1C) > 0 && sub0 > 0 && matIdx + sub0 <= d.Length ? d.AsSpan(matIdx, sub0).ToArray() : null;
        if (mats != null) common = mats.GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key;
        for (int mi = 0; mi < hc.Meshes.Count; mi++)
        {
            var m = hc.Meshes[mi];
            int b = s.P.Count;
            s.P.AddRange(m.Positions);
            bool first = mi == 0 && m.Kind == "mesh" && mats != null && m.Triangles.Count / 3 == sub0;
            for (int t = 0; t + 2 < m.Triangles.Count; t += 3)
            {
                s.T.Add(b + m.Triangles[t]); s.T.Add(b + m.Triangles[t + 1]); s.T.Add(b + m.Triangles[t + 2]);
                s.Mat.Add(first ? mats![t / 3] : common);
                s.Shape.Add($"{m.Kind} {mi}");
            }
        }
        s.OriginalTriangles = s.Triangles;
        return s;
    }

    public CollisionMesh ToMesh()
    {
        var m = new CollisionMesh { Kind = "mesh" };
        m.Positions.AddRange(P); m.Triangles.AddRange(T);
        return m;
    }

    // ------------------------------------------------------------------ undo snapshots

    public sealed record Snapshot(Vector3[] P, int[] T, byte[] Mat, string[] Shape, bool Dirty);
    public Snapshot Save() => new(P.ToArray(), T.ToArray(), Mat.ToArray(), Shape.ToArray(), Dirty);
    public void Restore(Snapshot s) { P = s.P.ToList(); T = s.T.ToList(); Mat = s.Mat.ToList(); Shape = s.Shape.ToList(); Dirty = s.Dirty; }

    // ------------------------------------------------------------------ selection helpers

    Dictionary<Vector3, int>? _weld;
    int[] Welded()
    {
        // vertices at the same position count as one (shapes and subparts do not share indices)
        _weld = new Dictionary<Vector3, int>();
        var id = new int[P.Count];
        for (int i = 0; i < P.Count; i++) { if (!_weld.TryGetValue(P[i], out int k)) _weld[P[i]] = k = i; id[i] = k; }
        return id;
    }

    /// <summary>Every triangle connected to <paramref name="tri"/> through shared corners (one shape / piece).</summary>
    public HashSet<int> ConnectedPiece(int tri)
    {
        var w = Welded();
        var byVert = new Dictionary<int, List<int>>();
        for (int t = 0; t < Triangles; t++) for (int k = 0; k < 3; k++) { int v = w[T[3 * t + k]]; if (!byVert.TryGetValue(v, out var l)) byVert[v] = l = new(); l.Add(t); }
        var res = new HashSet<int> { tri }; var q = new Queue<int>(); q.Enqueue(tri);
        while (q.Count > 0)
        {
            int t = q.Dequeue();
            for (int k = 0; k < 3; k++) foreach (var n in byVert[w[T[3 * t + k]]]) if (res.Add(n)) q.Enqueue(n);
        }
        return res;
    }

    public Vector3 Normal(int t)
    {
        var a = P[T[3 * t]]; var n = Vector3.Cross(P[T[3 * t + 1]] - a, P[T[3 * t + 2]] - a);
        return n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.Zero;
    }

    /// <summary>The flat face around <paramref name="tri"/>: edge-neighbours in the same plane (normals within
    /// <paramref name="degrees"/>, plane distance within a small tolerance), flood-filled.</summary>
    public HashSet<int> CoplanarRegion(int tri, float degrees = 5f)
    {
        var w = Welded();
        var byEdge = new Dictionary<(int, int), List<int>>();
        for (int t = 0; t < Triangles; t++)
            for (int k = 0; k < 3; k++)
            {
                int a = w[T[3 * t + k]], b = w[T[3 * t + (k + 1) % 3]];
                var key = (Math.Min(a, b), Math.Max(a, b));
                if (!byEdge.TryGetValue(key, out var l)) byEdge[key] = l = new(); l.Add(t);
            }
        var n0 = Normal(tri); float d0 = Vector3.Dot(n0, P[T[3 * tri]]); float cos = MathF.Cos(degrees * MathF.PI / 180);
        var (mn, mx) = Bounds(new[] { tri });
        float tol = MathF.Max(0.02f, (mx - mn).Length() * 0.01f);
        var res = new HashSet<int> { tri }; var q = new Queue<int>(); q.Enqueue(tri);
        while (q.Count > 0)
        {
            int t = q.Dequeue();
            for (int k = 0; k < 3; k++)
            {
                int a = w[T[3 * t + k]], b = w[T[3 * t + (k + 1) % 3]];
                foreach (var n in byEdge[(Math.Min(a, b), Math.Max(a, b))])
                    if (!res.Contains(n) && Vector3.Dot(Normal(n), n0) >= cos && MathF.Abs(Vector3.Dot(n0, P[T[3 * n]]) - d0) < tol + 0.05f)
                    { res.Add(n); q.Enqueue(n); }
            }
        }
        return res;
    }

    public (Vector3 Min, Vector3 Max) Bounds(IEnumerable<int> tris)
    {
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (int t in tris) for (int k = 0; k < 3; k++) { var p = P[T[3 * t + k]]; mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
        return (mn, mx);
    }

    // ------------------------------------------------------------------ edits

    /// <summary>Removes the triangles (unused vertices are dropped too).</summary>
    public void Delete(ICollection<int> tris)
    {
        var del = tris as HashSet<int> ?? tris.ToHashSet();
        var nt = new List<int>(); var nm = new List<byte>(); var ns = new List<string>();
        for (int t = 0; t < Triangles; t++)
            if (!del.Contains(t)) { nt.Add(T[3 * t]); nt.Add(T[3 * t + 1]); nt.Add(T[3 * t + 2]); nm.Add(Mat[t]); ns.Add(Shape[t]); }
        T = nt; Mat = nm; Shape = ns;
        Compact();
        Dirty = true;
    }

    void Compact()
    {
        var used = new int[P.Count]; Array.Fill(used, -1);
        var np = new List<Vector3>();
        for (int i = 0; i < T.Count; i++) { int v = T[i]; if (used[v] < 0) { used[v] = np.Count; np.Add(P[v]); } T[i] = used[v]; }
        P = np;
    }

    /// <summary>Applies <paramref name="m"/> (asset space) to the triangles. Corners they share with other triangles are
    /// split off first, so the rest stays where it is.</summary>
    public void Transform(ICollection<int> tris, Matrix4x4 m)
    {
        var sel = tris as HashSet<int> ?? tris.ToHashSet();
        var usedOutside = new HashSet<int>();
        for (int t = 0; t < Triangles; t++) if (!sel.Contains(t)) for (int k = 0; k < 3; k++) usedOutside.Add(T[3 * t + k]);
        var moved = new Dictionary<int, int>();
        foreach (int t in sel)
            for (int k = 0; k < 3; k++)
            {
                int v = T[3 * t + k];
                if (!moved.TryGetValue(v, out int nv))
                {
                    if (usedOutside.Contains(v)) { nv = P.Count; P.Add(P[v]); } else nv = v;
                    moved[v] = nv;
                    P[nv] = Vector3.Transform(P[v], m);
                }
                T[3 * t + k] = nv;
            }
        Dirty = true;
    }

    /// <summary>Copies the triangles, transformed by <paramref name="m"/>; returns the new triangle indices.</summary>
    public HashSet<int> Duplicate(ICollection<int> tris, Matrix4x4 m)
    {
        var res = new HashSet<int>(); var map = new Dictionary<int, int>();
        foreach (int t in tris.OrderBy(x => x).ToList())
        {
            for (int k = 0; k < 3; k++)
            {
                int v = T[3 * t + k];
                if (!map.TryGetValue(v, out int nv)) { nv = P.Count; P.Add(Vector3.Transform(P[v], m)); map[v] = nv; }
                T.Add(nv);
            }
            Mat.Add(Mat[t]); Shape.Add(Shape[t] + " (copy)");
            res.Add(Triangles - 1);
        }
        Dirty = true;
        return res;
    }

    /// <summary>Adds triangles (asset space); returns their indices.</summary>
    public HashSet<int> Add(IReadOnlyList<Vector3> pos, IReadOnlyList<int> tris, string shape, byte? material = null)
    {
        byte mat = material ?? (Mat.Count > 0 ? Mat.GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key : (byte)0);
        int b = P.Count; P.AddRange(pos);
        var res = new HashSet<int>();
        for (int i = 0; i + 2 < tris.Count; i += 3)
        {
            T.Add(b + tris[i]); T.Add(b + tris[i + 1]); T.Add(b + tris[i + 2]);
            Mat.Add(mat); Shape.Add(shape); res.Add(Triangles - 1);
        }
        Dirty = true;
        return res;
    }

    /// <summary>Replaces everything with these triangles (e.g. a model's render mesh).</summary>
    public void ReplaceAll(IReadOnlyList<Vector3> pos, IReadOnlyList<int> tris, string shape)
    {
        byte mat = Mat.Count > 0 ? Mat.GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key : (byte)0;
        P = pos.ToList(); T = tris.ToList();
        Mat = Enumerable.Repeat(mat, Triangles).ToList(); Shape = Enumerable.Repeat(shape, Triangles).ToList();
        Dirty = true;
    }

    // ------------------------------------------------------------------ primitives (game winding, as HkCollisionImport.Box)

    public static (List<Vector3> P, List<int> T) BoxMesh(Vector3 half) => HkCollisionImport.Box(-half, half);

    /// <summary>A wedge: floor <paramref name="half"/>.X × .Z, rising from 0 at −Z to 2·half.Y at +Z (centred on the floor).</summary>
    public static (List<Vector3> P, List<int> T) RampMesh(Vector3 half)
    {
        float x = half.X, z = half.Z, h = 2 * half.Y;
        var p = new List<Vector3> { new(-x, 0, -z), new(x, 0, -z), new(x, 0, z), new(-x, 0, z), new(x, h, z), new(-x, h, z) };
        // floor, back wall, slope, two sides (both windings: a solid seen from every side)
        int[] t = { 0, 1, 2, 0, 2, 3, 3, 2, 4, 3, 4, 5, 0, 5, 4, 0, 4, 1, 0, 3, 5, 1, 4, 2 };
        return (p, DoubleSided(t));
    }

    /// <summary>A flat square of size 2·half.X × 2·half.Z (both sides solid).</summary>
    public static (List<Vector3> P, List<int> T) PlaneMesh(Vector3 half)
    {
        var p = new List<Vector3> { new(-half.X, 0, -half.Z), new(half.X, 0, -half.Z), new(half.X, 0, half.Z), new(-half.X, 0, half.Z) };
        return (p, DoubleSided(new[] { 0, 2, 1, 0, 3, 2 }));
    }

    static List<int> DoubleSided(int[] t)
    {
        var l = t.ToList();
        for (int i = 0; i + 2 < t.Length; i += 3) { l.Add(t[i]); l.Add(t[i + 2]); l.Add(t[i + 1]); }
        return l;
    }

    // ------------------------------------------------------------------ writing

    /// <summary>
    /// Rebuilds the asset in <paramref name="caff"/> from the triangles (new buffers and MOPP; the per-triangle materials are
    /// kept) and verifies it by decoding it again. An empty collision keeps one tiny triangle 30,000 units below the asset's
    /// origin (a Havok mesh needs at least one triangle). Returns a one-line summary.
    /// </summary>
    public string WriteTo(CaffFile caff, int symbol)
    {
        var p = P; var t = T; var mat = Mat;
        if (Triangles == 0)
        {
            p = new() { new(0, -30000, 0), new(0.01f, -30000, 0), new(0, -30000, 0.01f) }; t = new() { 0, 2, 1 }; mat = new() { 0 };
        }
        var res = HkCollisionImport.Replace(caff, symbol, p, t, null, false, mat.ToArray());
        var check = HkCollisionImport.Verify(new AssetView(caff, symbol).Data(".data"), p, t);
        AssetBytes = new AssetView(caff, symbol).Data(".data").Length;
        Dirty = false;
        return $"{Asset}: {Triangles:N0} triangles ({OriginalTriangles:N0} before), MOPP {res.MoppBytes:N0} bytes; {check}";
    }
}
