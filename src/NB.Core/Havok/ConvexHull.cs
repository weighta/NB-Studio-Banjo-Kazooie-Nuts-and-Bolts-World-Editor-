using System.Numerics;

namespace NB.Core.Havok;

/// <summary>Small incremental 3D convex hull (for convex collision shapes whose plane equations are unusable).</summary>
public static class ConvexHull
{
    public static List<int> Build(IReadOnlyList<Vector3> p)
    {
        var tris = new List<int>();
        int n = p.Count;
        if (n < 4) return tris;
        float scale = 0; foreach (var v in p) scale = MathF.Max(scale, v.Length());
        float eps = MathF.Max(1e-6f, scale * 1e-5f);
        // initial tetrahedron
        int a = 0, b = -1, c = -1, d = -1;
        for (int i = 1; i < n && b < 0; i++) if (Vector3.Distance(p[i], p[a]) > eps) b = i;
        if (b < 0) return tris;
        for (int i = 0; i < n && c < 0; i++) if (Vector3.Cross(p[b] - p[a], p[i] - p[a]).Length() > eps) c = i;
        if (c < 0) return tris;
        var nrm = Vector3.Cross(p[b] - p[a], p[c] - p[a]);
        for (int i = 0; i < n && d < 0; i++) if (MathF.Abs(Vector3.Dot(nrm, p[i] - p[a])) > eps * nrm.Length()) d = i;
        if (d < 0) return tris;
        var faces = new List<(int A, int B, int C)>();
        void AddFace(int x, int y, int z, Vector3 inside)
        {
            var fn = Vector3.Cross(p[y] - p[x], p[z] - p[x]);
            if (Vector3.Dot(fn, inside - p[x]) > 0) faces.Add((x, z, y)); else faces.Add((x, y, z));
        }
        var centre = (p[a] + p[b] + p[c] + p[d]) / 4;
        AddFace(a, b, c, centre); AddFace(a, b, d, centre); AddFace(a, c, d, centre); AddFace(b, c, d, centre);
        for (int i = 0; i < n; i++)
        {
            if (i == a || i == b || i == c || i == d) continue;
            var visible = new List<int>();
            for (int f = 0; f < faces.Count; f++)
            {
                var (x, y, z) = faces[f];
                var fn = Vector3.Cross(p[y] - p[x], p[z] - p[x]);
                float len = fn.Length(); if (len < 1e-12f) continue;
                if (Vector3.Dot(fn / len, p[i] - p[x]) > eps) visible.Add(f);
            }
            if (visible.Count == 0) continue;
            // horizon: edges of visible faces not shared with another visible face
            var edges = new Dictionary<(int, int), int>();
            foreach (int f in visible)
            {
                var (x, y, z) = faces[f];
                foreach (var e in new[] { (x, y), (y, z), (z, x) })
                {
                    if (edges.ContainsKey((e.Item2, e.Item1))) edges.Remove((e.Item2, e.Item1));
                    else edges[e] = 1;
                }
            }
            foreach (int f in visible.OrderByDescending(v => v)) faces.RemoveAt(f);
            foreach (var (e, _) in edges) faces.Add((e.Item1, e.Item2, i));
        }
        foreach (var (x, y, z) in faces) { tris.Add(x); tris.Add(y); tris.Add(z); }
        return tris;
    }
}
