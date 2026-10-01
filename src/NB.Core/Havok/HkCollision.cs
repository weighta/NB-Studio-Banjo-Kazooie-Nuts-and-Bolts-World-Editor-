using System.Numerics;
using NB.Core.IO;

namespace NB.Core.Havok;

/// <summary>Collision geometry of one shape, in the asset's local space.</summary>
public sealed class CollisionMesh
{
    public string Kind = "";
    public readonly List<Vector3> Positions = new();
    public readonly List<int> Triangles = new();
    public void AddTri(int a, int b, int c) { Triangles.Add(a); Triangles.Add(b); Triangles.Add(c); }
}

/// <summary>
/// Converts the shapes of a Havok packfile into triangle meshes for display/export. Handles every shape class found in
/// the game's aid_havok assets (survey over 4,667 assets): MOPP BV tree, extended mesh (triangle and shape subparts),
/// convex vertices (faces rebuilt from the plane equations), convex translate/transform, list, box, sphere, cylinder,
/// plus rigid bodies (shape + motion transform). Unknown shape classes are reported in <see cref="Notes"/>.
/// </summary>
public sealed class HkCollision
{
    public readonly List<CollisionMesh> Meshes = new();
    public readonly List<string> Notes = new();
    readonly HkPackfile _pf;
    int _depth;

    HkCollision(HkPackfile pf) { _pf = pf; }

    // external buffers of the aid_havok wrapper (see ExternalBlobs): the game plugs them into triangle subparts whose
    // vertexBase/indexBase are null in the packfile
    byte[]? _asset;
    readonly List<(int Offset, int Count)> _blobs = new();
    readonly HashSet<int> _usedBlobs = new();

    /// <summary>
    /// aid_havok .data wrapper: +0 → table of (u32 type, u32 → entry), +4 entry count. Entry type 1 is a list of
    /// (u32 offset, u32 count) pairs: the packfile (count = size in bytes), then the index buffer (count = number of
    /// 16-bit indices) and vertex buffer (count = number of float3 vertices) of the extended mesh — verified on
    /// Showdown Town's world collision (67,576 triangles, 41,141 vertices).
    /// </summary>
    public static List<(int Offset, int Count)> ExternalBlobs(byte[] asset)
    {
        var list = new List<(int, int)>();
        int tab = BE.S32(asset, 0), n = BE.S32(asset, 4);
        for (int i = 0; i < n && tab + 8 * i + 8 <= asset.Length; i++)
        {
            if (BE.S32(asset, tab + 8 * i) != 1) continue;
            int p = BE.S32(asset, tab + 8 * i + 4);
            for (int k = 0; p + 8 * k + 8 <= asset.Length; k++)
            {
                int off = BE.S32(asset, p + 8 * k), cnt = BE.S32(asset, p + 8 * k + 4);
                if (off <= 0 || cnt <= 0 || off >= asset.Length) break;
                list.Add((off, cnt));
            }
        }
        return list;
    }

    public static HkCollision ExtractAsset(byte[] asset)
    {
        var pf = HkPackfile.FromAsset(asset);
        return Extract(pf, asset);
    }

    public static HkCollision Extract(HkPackfile pf, byte[]? asset = null)
    {
        var c = new HkCollision(pf);
        if (asset != null) { c._asset = asset; c._blobs.AddRange(ExternalBlobs(asset).Skip(1)); }
        var data = pf.Data; int di = pf.Sections.IndexOf(data);
        var bodies = data.Objects.Where(o => o.Class == "hkpRigidBody").ToList();
        var referenced = new HashSet<int>();
        // an object is "referenced" if any pointer inside another object's byte range targets it
        var starts = data.Objects.Select(o => o.Offset).ToList();
        foreach (var (src, dst) in data.Pointers)
            if (dst.Section == di) referenced.Add(dst.Offset);
        if (bodies.Count > 0)
        {
            foreach (var (off, _) in bodies)
            {
                var rb = pf.Object(off, "hkpRigidBody");
                try
                {
                    var shape = rb.Struct("collidable").Ref("shape");
                    var xf = c.MotionTransform(rb);
                    if (shape != null) c.Emit(shape, xf);
                }
                catch (Exception e) { c.Notes.Add("rigid body: " + e.Message); }
            }
        }
        else
        {
            foreach (var (off, cls) in data.Objects)
                if (cls.StartsWith("hkp") && cls.EndsWith("Shape") && !referenced.Contains(off))
                    c.Emit(pf.Object(off, cls), Matrix4x4.Identity);
        }
        return c;
    }

    Matrix4x4 MotionTransform(HkObject rb)
    {
        try
        {
            var ms = rb.Struct("motion").Struct("motionState");
            var t = ms.Struct("transform");
            return ReadTransform(t, 0);
        }
        catch { return Matrix4x4.Identity; }
    }

    /// <summary>hkTransform: rotation as 3 column vectors (float4 each) then translation (float4).</summary>
    static Matrix4x4 ReadTransform(HkObject o, int rel)
    {
        var b = o.File.SectionBytes(o.Section); int p = o.Offset + rel;
        float F(int k) => BE.F32(b, p + 4 * k);
        // columns c0,c1,c2 → row-vector matrix rows are the columns' components
        return new Matrix4x4(F(0), F(1), F(2), 0, F(4), F(5), F(6), 0, F(8), F(9), F(10), 0, F(12), F(13), F(14), 1);
    }

    void Emit(HkObject s, Matrix4x4 xf)
    {
        if (++_depth > 32) { Notes.Add("shape nesting too deep"); _depth--; return; }
        try
        {
            switch (s.Class?.Name)
            {
                case "hkpMoppBvTreeShape":
                    if (s.Struct("child").Ref("childShape") is { } ch) Emit(ch, xf);
                    break;
                case "hkpExtendedMeshShape": ExtendedMesh(s, xf); break;
                case "hkpConvexVerticesShape": ConvexVertices(s, xf); break;
                case "hkpConvexTranslateShape":
                {
                    var t = s.V4("translation");
                    if (ChildOf(s) is { } ch2) Emit(ch2, Matrix4x4.CreateTranslation(t.X, t.Y, t.Z) * xf);
                    break;
                }
                case "hkpConvexTransformShape":
                {
                    var m = ReadTransform(s, s.Class!.Find("transform")!.Offset);
                    if (ChildOf(s) is { } ch3) Emit(ch3, m * xf);
                    break;
                }
                case "hkpListShape":
                {
                    var (ptr, n) = s.Array("childInfo");
                    var info = s.Class!.Find("childInfo")!.Class;
                    int size = info?.Size ?? 16;
                    for (int i = 0; ptr != null && i < n; i++)
                    {
                        var ci = s.At(ptr.Value.Section, ptr.Value.Offset + size * i, info?.Name ?? "hkpListShapeChildInfo");
                        if (ci.Ref("shape") is { } cs) Emit(cs, xf);
                    }
                    break;
                }
                case "hkpBoxShape": Box(s.V4("halfExtents"), xf); break;
                case "hkpSphereShape": Sphere(Vector3.Zero, s.F("radius"), xf); break;
                case "hkpCylinderShape": Cylinder(s.V4("vertexA"), s.V4("vertexB"), s.F("cylRadius"), xf); break;
                case "hkpCapsuleShape": Cylinder(s.V4("vertexA"), s.V4("vertexB"), s.F("radius"), xf); break;
                default: Notes.Add("shape class not handled: " + s.Class?.Name); break;
            }
        }
        catch (Exception e) { Notes.Add($"{s.Class?.Name}: {e.Message}"); }
        _depth--;
    }

    static HkObject? ChildOf(HkObject s)
    {
        foreach (var name in new[] { "childShape" })
        {
            var m = s.Class?.Find(name);
            if (m == null) continue;
            if (m.Type == HkType.Struct) return s.Struct(name).Ref("childShape");
            return s.Ref(name);
        }
        return null;
    }

    void ExtendedMesh(HkObject s, Matrix4x4 xf)
    {
        var scale = s.V4("scaling");
        var sc = new Vector3(scale.X == 0 ? 1 : scale.X, scale.Y == 0 ? 1 : scale.Y, scale.Z == 0 ? 1 : scale.Z);
        var tsub = s.Class!.Find("trianglesSubparts")!.Class!;
        var (tp, tn) = s.Array("trianglesSubparts");
        for (int i = 0; tp != null && i < tn; i++) TriangleSubpart(s.At(tp.Value.Section, tp.Value.Offset + tsub.Size * i, tsub.Name), sc, xf);
        // the embedded subpart duplicates the first triangles subpart when that array is used; only read it on its own
        if (tn == 0 && s.Has("embeddedTrianglesSubpart")) TriangleSubpart(s.Struct("embeddedTrianglesSubpart"), sc, xf);
        var ssub = s.Class!.Find("shapesSubparts")!.Class!;
        var (sp, sn) = s.Array("shapesSubparts");
        for (int i = 0; sp != null && i < sn; i++)
        {
            var part = s.At(sp.Value.Section, sp.Value.Offset + ssub.Size * i, ssub.Name);
            var q = part.V4("rotation"); var t = part.V4("translation");
            var m = Matrix4x4.Identity;
            if (part.U8("rotationSet") != 0) m = Matrix4x4.CreateFromQuaternion(new Quaternion(q.X, q.Y, q.Z, q.W));
            if (part.U8("offsetSet") != 0) m.Translation = new Vector3(t.X, t.Y, t.Z);
            var (cp, cn) = part.Array("childShapes");
            var bytes = _pf.SectionBytes(cp?.Section ?? 0);
            for (int k = 0; cp != null && k < cn; k++)
                if (part.File.Sections[cp.Value.Section].Pointers.TryGetValue(cp.Value.Offset + 4 * k, out var target))
                {
                    var cls = _pf.Sections[target.Section].Objects.FirstOrDefault(o => o.Offset == target.Offset).Class;
                    Emit(new HkObject(_pf, target.Section, target.Offset, cls == null ? null : _pf.Classes.GetValueOrDefault(cls)), m * xf);
                }
        }
    }

    void TriangleSubpart(HkObject p, Vector3 scale, Matrix4x4 xf)
    {
        int nTri = p.I("numTriangleShapes"), nv = p.I("numVertices");
        if (nTri <= 0 || nv <= 0) return;
        var vb = p.Ptr("vertexBase"); var ib = p.Ptr("indexBase");
        int vs = p.I("vertexStriding"), istr = p.I("indexStriding"), type = p.U8("stridingType");
        byte[] vbytes, ibytes; int vOff, iOff;
        if (vb != null && ib != null)
        {
            vbytes = _pf.SectionBytes(vb.Value.Section); vOff = vb.Value.Offset;
            ibytes = _pf.SectionBytes(ib.Value.Section); iOff = ib.Value.Offset;
        }
        else
        {
            // external buffers: index buffer has 3 × triangles entries, vertex buffer has numVertices entries
            // the game plugs type-1 entry +0x08 (indices) / +0x10 (vertices) into subpart 0 (resolver 0x82307C70)
            int ii, vi;
            if (_blobs.Count >= 2 && _blobs[0].Count == nTri * 3 && _blobs[1].Count == nv && !_usedBlobs.Contains(_blobs[0].Offset)) { ii = 0; vi = 1; }
            else
            {
                ii = _blobs.FindIndex(b => b.Count == nTri * 3 && !_usedBlobs.Contains(b.Offset));
                int skip = ii;
                vi = _blobs.FindIndex(b => b.Count == nv && !_usedBlobs.Contains(b.Offset) && b.Offset != (skip >= 0 ? _blobs[skip].Offset : -1));
            }
            if (_asset == null || ii < 0 || vi < 0) { Notes.Add("triangle subpart without vertex/index data"); return; }
            _usedBlobs.Add(_blobs[ii].Offset); _usedBlobs.Add(_blobs[vi].Offset);
            vbytes = ibytes = _asset; vOff = _blobs[vi].Offset; iOff = _blobs[ii].Offset;
            if (istr == 0) istr = 6;
            if (vs == 0) vs = 12;
        }
        var mesh = new CollisionMesh { Kind = "mesh" };
        for (int v = 0; v < nv; v++)
        {
            int o = vOff + v * vs;
            var pos = new Vector3(BE.F32(vbytes, o), BE.F32(vbytes, o + 4), BE.F32(vbytes, o + 8)) * scale;
            mesh.Positions.Add(Vector3.Transform(pos, xf));
        }
        // index striding type: 1 = 16-bit, 2 = 32-bit (Havok 5.5 INDICES_INT16 / INDICES_INT32)
        bool i16 = type == 1 || (type != 2 && istr < 12);
        for (int t = 0; t < nTri; t++)
        {
            int o = iOff + t * istr;
            int a, b, c;
            if (i16) { a = BE.U16(ibytes, o); b = BE.U16(ibytes, o + 2); c = BE.U16(ibytes, o + 4); }
            else { a = BE.S32(ibytes, o); b = BE.S32(ibytes, o + 4); c = BE.S32(ibytes, o + 8); }
            if (a < nv && b < nv && c < nv) mesh.AddTri(a, b, c);
        }
        Meshes.Add(mesh);
    }

    void ConvexVertices(HkObject s, Matrix4x4 xf)
    {
        int n = s.I("numVertices");
        var (vp, vn) = s.Array("rotatedVertices");
        if (vp == null || n <= 0) return;
        var b = _pf.SectionBytes(vp.Value.Section);
        var verts = new List<Vector3>();
        for (int i = 0; i < n; i++)
        {
            int blk = vp.Value.Offset + 0x30 * (i / 4), k = i % 4;
            verts.Add(new Vector3(BE.F32(b, blk + 4 * k), BE.F32(b, blk + 16 + 4 * k), BE.F32(b, blk + 32 + 4 * k)));
        }
        var mesh = new CollisionMesh { Kind = "convex" };
        foreach (var v in verts) mesh.Positions.Add(Vector3.Transform(v, xf));
        var (pp, pn) = s.Array("planeEquations");
        var size = verts.Aggregate(Vector3.Zero, (m, v) => Vector3.Max(m, Vector3.Abs(v)));
        float eps = Math.Max(1e-3f, size.Length() * 1e-3f);
        // plane equations include the convex radius (the collision shell): vertices lie at distance -radius
        float radius = s.Has("radius") ? s.F("radius") : 0;
        for (int f = 0; pp != null && f < pn; f++)
        {
            var pb = _pf.SectionBytes(pp.Value.Section); int o = pp.Value.Offset + 16 * f;
            var nrm = new Vector3(BE.F32(pb, o), BE.F32(pb, o + 4), BE.F32(pb, o + 8)); float d = BE.F32(pb, o + 12) + radius;
            var on = Enumerable.Range(0, verts.Count).Where(i => MathF.Abs(Vector3.Dot(nrm, verts[i]) + d) < eps).ToList();
            if (on.Count < 3) continue;
            var ctr = on.Aggregate(Vector3.Zero, (acc, i) => acc + verts[i]) / on.Count;
            var ax = Vector3.Normalize(verts[on[0]] - ctr); var ay = Vector3.Cross(nrm, ax);
            on.Sort((i, j) => MathF.Atan2(Vector3.Dot(verts[i] - ctr, ay), Vector3.Dot(verts[i] - ctr, ax))
                .CompareTo(MathF.Atan2(Vector3.Dot(verts[j] - ctr, ay), Vector3.Dot(verts[j] - ctr, ax))));
            for (int t = 1; t + 1 < on.Count; t++) mesh.AddTri(on[0], on[t], on[t + 1]);
        }
        if (mesh.Triangles.Count == 0)
        {
            mesh.Triangles.AddRange(ConvexHull.Build(mesh.Positions));   // plane equations unusable: hull of the vertices
            if (mesh.Triangles.Count == 0) Notes.Add("convex shape with degenerate vertices (no hull)");
        }
        Meshes.Add(mesh);
    }

    void Box(Vector4 he, Matrix4x4 xf)
    {
        var m = new CollisionMesh { Kind = "box" };
        for (int i = 0; i < 8; i++)
            m.Positions.Add(Vector3.Transform(new Vector3((i & 1) != 0 ? he.X : -he.X, (i & 2) != 0 ? he.Y : -he.Y, (i & 4) != 0 ? he.Z : -he.Z), xf));
        int[] f = { 0, 2, 3, 0, 3, 1, 4, 5, 7, 4, 7, 6, 0, 1, 5, 0, 5, 4, 2, 6, 7, 2, 7, 3, 0, 4, 6, 0, 6, 2, 1, 3, 7, 1, 7, 5 };
        m.Triangles.AddRange(f); Meshes.Add(m);
    }

    void Sphere(Vector3 c, float r, Matrix4x4 xf)
    {
        var m = new CollisionMesh { Kind = "sphere" };
        const int seg = 12, rings = 6;
        for (int j = 0; j <= rings; j++)
            for (int i = 0; i <= seg; i++)
            {
                float th = MathF.PI * j / rings, ph = 2 * MathF.PI * i / seg;
                m.Positions.Add(Vector3.Transform(c + r * new Vector3(MathF.Sin(th) * MathF.Cos(ph), MathF.Cos(th), MathF.Sin(th) * MathF.Sin(ph)), xf));
            }
        for (int j = 0; j < rings; j++) for (int i = 0; i < seg; i++) { int a = j * (seg + 1) + i, b = a + seg + 1; m.AddTri(a, b, a + 1); m.AddTri(a + 1, b, b + 1); }
        Meshes.Add(m);
    }

    void Cylinder(Vector4 a4, Vector4 b4, float r, Matrix4x4 xf)
    {
        var a = new Vector3(a4.X, a4.Y, a4.Z); var b = new Vector3(b4.X, b4.Y, b4.Z);
        var axis = b - a; if (axis.LengthSquared() < 1e-12f) { Sphere(a, r, xf); return; }
        var z = Vector3.Normalize(axis); var x = Vector3.Normalize(Vector3.Cross(z, MathF.Abs(z.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX)); var y = Vector3.Cross(z, x);
        var m = new CollisionMesh { Kind = "cylinder" };
        const int seg = 12;
        for (int i = 0; i < seg; i++)
        {
            float t = 2 * MathF.PI * i / seg; var o = r * (MathF.Cos(t) * x + MathF.Sin(t) * y);
            m.Positions.Add(Vector3.Transform(a + o, xf)); m.Positions.Add(Vector3.Transform(b + o, xf));
        }
        for (int i = 0; i < seg; i++) { int i0 = 2 * i, i1 = 2 * ((i + 1) % seg); m.AddTri(i0, i1, i0 + 1); m.AddTri(i0 + 1, i1, i1 + 1); }
        for (int i = 1; i + 1 < seg; i++) { m.AddTri(0, 2 * (i + 1), 2 * i); m.AddTri(1, 2 * i + 1, 2 * (i + 1) + 1); }
        Meshes.Add(m);
    }

    /// <summary>Writes all meshes as one OBJ (same axis conventions as the model exporter: game space unchanged).</summary>
    public void WriteObj(string path)
    {
        var sb = new System.Text.StringBuilder("# collision exported by NB Mod Tool\n");
        int baseIndex = 1, n = 0;
        foreach (var m in Meshes)
        {
            sb.Append($"g collision_{n++}_{m.Kind}\n");
            foreach (var p in m.Positions) sb.Append(System.FormattableString.Invariant($"v {p.X} {p.Y} {p.Z}\n"));
            for (int i = 0; i + 2 < m.Triangles.Count; i += 3)
                sb.Append($"f {m.Triangles[i] + baseIndex} {m.Triangles[i + 1] + baseIndex} {m.Triangles[i + 2] + baseIndex}\n");
            baseIndex += m.Positions.Count;
        }
        File.WriteAllText(path, sb.ToString());
    }
}
