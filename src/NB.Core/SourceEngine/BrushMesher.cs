namespace NB.Core.SourceEngine;

/// <summary>
/// Triangles of one brush side (or displacement) in Source coordinates: positions, normals and texture coordinates in
/// texels (divide by the texture size for UVs, as Source does with the VTF size).
/// </summary>
public sealed class BrushSurface
{
    public string Material = "";
    public VmfSolid Solid = null!;
    public VmfSide Side = null!;
    public bool Displacement;
    public List<DVec3> Positions = new();
    public List<DVec3> Normals = new();
    public List<(double U, double V)> Texels = new();
    /// <summary>Counter-clockwise seen from the front (outside of the brush).</summary>
    public List<int> Triangles = new();
}

/// <summary>
/// Turns VMF brushes into polygons: every side's plane is cut by all other planes of the brush (a brush is the
/// intersection of the half-spaces dot(n, x) &lt;= d), the convex polygon is fanned into triangles, and texture
/// coordinates come from the side's u/v axes. Brushes with a displacement side give only their displacement surfaces
/// (Source does not draw or collide the other sides).
/// </summary>
public static class BrushMesher
{
    const double Huge = 131072, Eps = 0.01;

    public static List<BrushSurface> Mesh(VmfSolid solid, out int degenerate)
    {
        degenerate = 0;
        var res = new List<BrushSurface>();
        var sides = solid.Sides;
        var normals = sides.Select(s => s.Normal).ToArray();
        var dists = sides.Select((s, i) => DVec3.Dot(normals[i], s.P0)).ToArray();
        bool hasDisp = sides.Any(s => s.Disp != null);
        for (int i = 0; i < sides.Count; i++)
        {
            var side = sides[i];
            if (hasDisp && side.Disp == null) continue;
            if (normals[i].Length < 0.5) { degenerate++; continue; }
            var w = BaseWinding(normals[i], dists[i]);
            for (int j = 0; j < sides.Count && w.Count >= 3; j++)
            {
                if (j == i || normals[j].Length < 0.5) continue;
                // a duplicate plane (same normal and distance) would cut the face away entirely
                if (DVec3.Dot(normals[i], normals[j]) > 0.99999 && Math.Abs(dists[i] - dists[j]) < Eps) { if (j < i) w.Clear(); continue; }
                w = Clip(w, normals[j], dists[j]);
            }
            w = Clean(w);
            if (w.Count < 3) { degenerate++; continue; }
            if (side.Disp != null)
            {
                var ds = Displace(side, w, normals[i]);
                if (ds != null) { ds.Solid = solid; res.Add(ds); }
                else degenerate++;
                continue;
            }
            var s = new BrushSurface { Material = side.Material, Solid = solid, Side = side };
            foreach (var p in w)
            {
                s.Positions.Add(p); s.Normals.Add(normals[i]); s.Texels.Add(Texel(side, p));
            }
            for (int k = 1; k + 1 < w.Count; k++) { s.Triangles.Add(0); s.Triangles.Add(k); s.Triangles.Add(k + 1); }
            res.Add(s);
        }
        return res;
    }

    public static (double, double) Texel(VmfSide s, DVec3 p) =>
        (DVec3.Dot(p, s.UAxis) / s.UScale + s.UShift, DVec3.Dot(p, s.VAxis) / s.VScale + s.VShift);

    /// <summary>A big square on the plane, counter-clockwise around its normal.</summary>
    static List<DVec3> BaseWinding(DVec3 n, double d)
    {
        var refAxis = Math.Abs(n.Z) < 0.9 ? new DVec3(0, 0, 1) : new DVec3(1, 0, 0);
        var u = DVec3.Cross(n, refAxis).Normalized();
        var v = DVec3.Cross(n, u);
        var c = n * d;
        return new() { c - u * Huge - v * Huge, c + u * Huge - v * Huge, c + u * Huge + v * Huge, c - u * Huge + v * Huge };
    }

    /// <summary>Keeps the part of a convex polygon behind the plane (dot(n, x) &lt;= d).</summary>
    static List<DVec3> Clip(List<DVec3> w, DVec3 n, double d)
    {
        var dist = w.Select(p => DVec3.Dot(n, p) - d).ToArray();
        if (dist.All(x => x <= Eps)) return w;
        if (dist.All(x => x >= -Eps)) return new();
        var o = new List<DVec3>(w.Count + 2);
        for (int k = 0; k < w.Count; k++)
        {
            var a = w[k]; var b = w[(k + 1) % w.Count];
            double da = dist[k], db = dist[(k + 1) % w.Count];
            if (da <= Eps) o.Add(a);
            if ((da < -Eps && db > Eps) || (da > Eps && db < -Eps))
                o.Add(DVec3.Lerp(a, b, da / (da - db)));
        }
        return o;
    }

    /// <summary>Drops repeated points and snaps coordinates within 0.001 of a 1/64 grid (Hammer's own grid).</summary>
    static List<DVec3> Clean(List<DVec3> w)
    {
        static double Snap(double x) { double r = Math.Round(x * 64) / 64; return Math.Abs(r - x) < 1e-3 ? r : x; }
        var o = new List<DVec3>();
        foreach (var p0 in w)
        {
            var p = new DVec3(Snap(p0.X), Snap(p0.Y), Snap(p0.Z));
            if (o.Count == 0 || (p - o[^1]).Length > 1e-3) o.Add(p);
        }
        while (o.Count > 1 && (o[0] - o[^1]).Length <= 1e-3) o.RemoveAt(o.Count - 1);
        return o;
    }

    /// <summary>
    /// Displacement grid (Source CCoreDispInfo order): the face's corners in Source winding order (clockwise seen from
    /// the front) starting at the corner nearest "startposition"; vertex [row r, column c] = lerp(lerp(p0, p1, r/n),
    /// lerp(p3, p2, r/n), c/n) + normal*distance + offset + faceNormal*elevation. Texture coordinates come from the flat
    /// (undisplaced) position.
    /// </summary>
    static BrushSurface? Displace(VmfSide side, List<DVec3> ccw, DVec3 faceNormal)
    {
        var d = side.Disp!;
        if (ccw.Count != 4) return null;
        var cw = new List<DVec3> { ccw[0], ccw[3], ccw[2], ccw[1] };
        int start = 0; double best = double.MaxValue;
        for (int k = 0; k < 4; k++) { double dd = (cw[k] - d.StartPosition).Length; if (dd < best) { best = dd; start = k; } }
        var p = Enumerable.Range(0, 4).Select(k => cw[(start + k) % 4]).ToArray();
        int n = d.Size;
        var s = new BrushSurface { Material = side.Material, Side = side, Displacement = true };
        for (int r = 0; r < n; r++)
        {
            double t = r / (double)(n - 1);
            var left = DVec3.Lerp(p[0], p[1], t); var right = DVec3.Lerp(p[3], p[2], t);
            for (int c = 0; c < n; c++)
            {
                var flat = DVec3.Lerp(left, right, c / (double)(n - 1));
                var pos = flat + d.Normals[r, c] * d.Distances[r, c] + d.Offsets[r, c] + faceNormal * d.Elevation;
                s.Positions.Add(pos); s.Texels.Add(Texel(side, flat));
            }
        }
        // two triangles per cell, alternating diagonals like Source; the winding comes from the flat grid (same for
        // every cell) so the surface faces the side's front even where the displacement folds over
        bool flip = DVec3.Dot(DVec3.Cross(p[1] - p[0], p[3] - p[0]), faceNormal) < 0;
        void Tri(int a, int b, int c)
        {
            if (flip) (b, c) = (c, b);
            s.Triangles.Add(a); s.Triangles.Add(b); s.Triangles.Add(c);
        }
        for (int r = 0; r + 1 < n; r++)
            for (int c = 0; c + 1 < n; c++)
            {
                int a = r * n + c, b = (r + 1) * n + c, cc = (r + 1) * n + c + 1, dd = r * n + c + 1;
                if (((r + c) & 1) == 0) { Tri(a, b, cc); Tri(a, cc, dd); }
                else { Tri(a, b, dd); Tri(b, cc, dd); }
            }
        // smooth normals from the displaced triangles
        var acc = new DVec3[s.Positions.Count];
        for (int k = 0; k + 2 < s.Triangles.Count; k += 3)
        {
            int a = s.Triangles[k], b = s.Triangles[k + 1], c = s.Triangles[k + 2];
            var fn = DVec3.Cross(s.Positions[b] - s.Positions[a], s.Positions[c] - s.Positions[a]);
            acc[a] += fn; acc[b] += fn; acc[c] += fn;
        }
        s.Normals = acc.Select(v => v.Length > 1e-9 ? v.Normalized() : faceNormal).ToList();
        return s;
    }
}
