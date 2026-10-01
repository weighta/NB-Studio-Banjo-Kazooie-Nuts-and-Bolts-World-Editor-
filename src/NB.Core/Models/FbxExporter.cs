using System.Numerics;
using System.Text;

namespace NB.Core.Models;

/// <summary>
/// Binary FBX 7.4 writer for static model draws (Blender, Maya, 3ds Max read it; Blender cannot read ASCII FBX).
/// One Model + Geometry + Material per draw; the diffuse texture is referenced as textures/&lt;stem&gt;.png (the same
/// files the OBJ export writes). Same axis conventions as <see cref="ObjExporter"/>: Z negated, winding reversed, V flipped.
/// </summary>
public static class FbxExporter
{
    sealed class Node
    {
        public string Name;
        public readonly List<object> Props = new();
        public readonly List<Node> Children = new();
        public Node(string name, params object[] props) { Name = name; Props.AddRange(props); }
        public Node Add(string name, params object[] props) { var n = new Node(name, props); Children.Add(n); return n; }
    }

    static long _nextId = 1_000_000;

    public static void Write(string path, string name, IEnumerable<(MeshDraw Draw, Matrix4x4 Transform)> draws, string textureExt = ".png",
        IReadOnlyList<Joint>? skeleton = null, AnimAsset? animation = null, string animationName = "Take 001")
    {
        var objects = new Node("Objects");
        var conns = new Node("Connections");
        long[]? jointIds = skeleton != null ? WriteSkeleton(objects, conns, skeleton) : null;
        Matrix4x4[]? jointBind = skeleton != null ? BindMatrices(skeleton) : null;
        if (skeleton != null && animation != null) WriteAnimation(objects, conns, skeleton, jointIds!, animation, animationName);
        var materials = new Dictionary<string, long>();
        var textures = new Dictionary<string, long>();
        int di = 0;
        foreach (var (d, xf) in draws)
        {
            if (d.Positions.Length == 0 || d.Indices.Length < 3) continue;
            long geomId = _nextId++, modelId = _nextId++;
            string mname = $"draw{di++}";
            // positions
            var pos = new double[d.Positions.Length * 3];
            for (int i = 0; i < d.Positions.Length; i++)
            {
                var p = Vector3.Transform(d.Positions[i], xf);
                pos[3 * i] = p.X; pos[3 * i + 1] = p.Y; pos[3 * i + 2] = p.Z;
            }
            var poly = new List<int>(); var nrm = new List<double>(); var uvIdx = new List<int>();
            for (int k = 0; k + 2 < d.Indices.Length; k += 3)
            {
                int a = d.Indices[k], b = d.Indices[k + 1], c = d.Indices[k + 2];   // stored winding (counter-clockwise, like FBX)
                if (a >= d.Positions.Length || b >= d.Positions.Length || c >= d.Positions.Length) continue;
                poly.Add(a); poly.Add(b); poly.Add(~c);
                foreach (int v in new[] { a, b, c })
                {
                    if (d.Normals != null)
                    {
                        var n = Vector3.Normalize(Vector3.TransformNormal(d.Normals[v], xf) + new Vector3(1e-9f));
                        nrm.Add(n.X); nrm.Add(n.Y); nrm.Add(n.Z);
                    }
                    uvIdx.Add(v);
                }
            }
            var geom = new Node("Geometry", geomId, $"{mname}\0\u0001Geometry", "Mesh");
            geom.Add("Vertices", pos);
            geom.Add("PolygonVertexIndex", poly.ToArray());
            geom.Add("GeometryVersion", 124);
            if (d.Normals != null)
            {
                var ln = geom.Add("LayerElementNormal", 0);
                ln.Add("Version", 101); ln.Add("Name", ""); ln.Add("MappingInformationType", "ByPolygonVertex");
                ln.Add("ReferenceInformationType", "Direct"); ln.Add("Normals", nrm.ToArray());
            }
            if (d.UVs != null)
            {
                var uv = new double[d.UVs.Length * 2];
                for (int i = 0; i < d.UVs.Length; i++) { uv[2 * i] = d.UVs[i].X; uv[2 * i + 1] = 1 - d.UVs[i].Y; }
                var lu = geom.Add("LayerElementUV", 0);
                lu.Add("Version", 101); lu.Add("Name", "UVMap"); lu.Add("MappingInformationType", "ByPolygonVertex");
                lu.Add("ReferenceInformationType", "IndexToDirect"); lu.Add("UV", uv); lu.Add("UVIndex", uvIdx.ToArray());
            }
            var lm = geom.Add("LayerElementMaterial", 0);
            lm.Add("Version", 101); lm.Add("Name", ""); lm.Add("MappingInformationType", "AllSame");
            lm.Add("ReferenceInformationType", "IndexToDirect"); lm.Add("Materials", new[] { 0 });
            var layer = geom.Add("Layer", 0); layer.Add("Version", 100);
            foreach (var t in new[] { d.Normals != null ? "LayerElementNormal" : null, d.UVs != null ? "LayerElementUV" : null, "LayerElementMaterial" })
                if (t != null) { var le = layer.Add("LayerElement"); le.Add("Type", t); le.Add("TypedIndex", 0); }
            objects.Children.Add(geom);
            if (jointIds != null && d.BlendIndices != null && d.BlendWeights != null)
                WriteSkin(objects, conns, geomId, d, jointIds, jointBind!);

            var model = new Node("Model", modelId, $"{mname}\0\u0001Model", "Mesh");
            model.Add("Version", 232);
            var p70 = model.Add("Properties70");
            p70.Add("P", "DefaultAttributeIndex", "int", "Integer", "", 0);
            model.Add("Shading", true); model.Add("Culling", "CullingOff");
            objects.Children.Add(model);
            conns.Add("C", "OO", geomId, modelId);
            conns.Add("C", "OO", modelId, 0L);

            string? tex = ObjExporter.DiffuseTexture(d);
            string mat = tex != null ? ObjExporter.TextureFileStem(tex) : "untextured";
            if (!materials.TryGetValue(mat, out long matId))
            {
                matId = _nextId++; materials[mat] = matId;
                var m = new Node("Material", matId, $"{mat}\0\u0001Material", "");
                m.Add("Version", 102); m.Add("ShadingModel", "phong"); m.Add("MultiLayer", 0);
                var mp = m.Add("Properties70");
                mp.Add("P", "DiffuseColor", "Color", "", "A", 1.0, 1.0, 1.0);
                objects.Children.Add(m);
                if (tex != null)
                {
                    long texId = _nextId++; textures[mat] = texId;
                    string rel = $"textures/{ObjExporter.TextureFileStem(tex)}{textureExt}";
                    var t = new Node("Texture", texId, $"{mat}\0\u0001Texture", "");
                    t.Add("Type", "TextureVideoClip"); t.Add("Version", 202); t.Add("TextureName", $"{mat}\0\u0001Texture");
                    t.Add("Media", $"{mat}\0\u0001Video");
                    t.Add("FileName", Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, rel)));
                    t.Add("RelativeFilename", rel);
                    objects.Children.Add(t);
                    conns.Add("C", "OP", texId, matId, "DiffuseColor");
                }
            }
            conns.Add("C", "OO", matId, modelId);
        }

        var root = new List<Node>();
        var hdr = new Node("FBXHeaderExtension");
        hdr.Add("FBXHeaderVersion", 1003); hdr.Add("FBXVersion", 7400); hdr.Add("Creator", "NB Mod Tool");
        root.Add(hdr);
        var gs = new Node("GlobalSettings"); gs.Add("Version", 1000);
        var g70 = gs.Add("Properties70");
        g70.Add("P", "UpAxis", "int", "Integer", "", 1); g70.Add("P", "UpAxisSign", "int", "Integer", "", 1);
        g70.Add("P", "FrontAxis", "int", "Integer", "", 2); g70.Add("P", "FrontAxisSign", "int", "Integer", "", 1);
        g70.Add("P", "CoordAxis", "int", "Integer", "", 0); g70.Add("P", "CoordAxisSign", "int", "Integer", "", 1);
        g70.Add("P", "UnitScaleFactor", "double", "Number", "", 100.0);   // game units are metres
        g70.Add("P", "TimeMode", "enum", "", "", 6);                        // 30 fps (the game's animation rate)
        g70.Add("P", "CustomFrameRate", "double", "Number", "", 30.0);
        root.Add(gs);
        root.Add(objects); root.Add(conns);

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write(Encoding.ASCII.GetBytes("Kaydara FBX Binary  ")); w.Write((byte)0); w.Write((byte)0x1A); w.Write((byte)0);
        w.Write(7400u);
        foreach (var n in root) WriteNode(w, n);
        w.Write(new byte[13]);                      // end of top-level records
        w.Write(new byte[] { 0xfa, 0xbc, 0xab, 0x09, 0xd0, 0xc8, 0xd4, 0x66, 0xb1, 0x76, 0xfb, 0x83, 0x1c, 0xf7, 0x26, 0x7e });
        w.Write(new byte[4]);
        int pad = 16 - (int)(fs.Position % 16); w.Write(new byte[pad == 0 ? 16 : pad]);
        w.Write(7400u); w.Write(new byte[120]);
        w.Write(new byte[] { 0xf8, 0x5a, 0x8c, 0x6a, 0xde, 0xf5, 0xd9, 0x7e, 0xec, 0xe9, 0x0c, 0xe3, 0x75, 0x8f, 0x29, 0x0b });
    }

    /// <summary>
    /// Joints as FBX LimbNode models (with a Skeleton node attribute) chained by parent; Lcl Translation is the
    /// parent-relative translation, Lcl Rotation the joint quaternion as XYZ Euler degrees (game axes, like the mesh).
    /// Bones only: skin weights are not exported, so the mesh is not bound to them.
    /// </summary>
    static long[] WriteSkeleton(Node objects, Node conns, IReadOnlyList<Joint> joints)
    {
        var ids = new long[joints.Count];
        for (int k = 0; k < joints.Count; k++)
        {
            var j = joints[k];
            long attrId = _nextId++, modelId = ids[k] = _nextId++;
            var attr = new Node("NodeAttribute", attrId, $"{j.Name}\0\u0001NodeAttribute", "LimbNode");
            attr.Add("TypeFlags", "Skeleton");
            var ap = attr.Add("Properties70"); ap.Add("P", "Size", "double", "Number", "", 3.0);
            objects.Children.Add(attr);
            var m = new Node("Model", modelId, $"{j.Name}\0\u0001Model", "LimbNode");
            m.Add("Version", 232);
            var p70 = m.Add("Properties70");
            var t = j.LocalTranslation;
            p70.Add("P", "Lcl Translation", "Lcl Translation", "", "A", (double)t.X, (double)t.Y, (double)t.Z);
            var e = QuatToEulerXyzDegrees(j.Rotation);
            if (e != Vector3.Zero) p70.Add("P", "Lcl Rotation", "Lcl Rotation", "", "A", (double)e.X, (double)e.Y, (double)e.Z);
            objects.Children.Add(m);
            conns.Add("C", "OO", attrId, modelId);
        }
        for (int k = 0; k < joints.Count; k++)
        {
            int p = joints[k].Parent;
            conns.Add("C", "OO", ids[k], p >= 0 && p < joints.Count ? ids[p] : 0L);
        }
        return ids;
    }

    /// <summary>Global bind matrices of the joints in export space (= game space), built from the parent chain.</summary>
    static Matrix4x4[] BindMatrices(IReadOnlyList<Joint> joints)
    {
        var g = new Matrix4x4[joints.Count]; var done = new bool[joints.Count];
        Matrix4x4 Get(int k, int depth)
        {
            if (done[k]) return g[k];
            var j = joints[k];
            var local = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(j.Rotation))
                        * Matrix4x4.CreateTranslation(j.LocalTranslation.X, j.LocalTranslation.Y, j.LocalTranslation.Z);
            g[k] = j.Parent >= 0 && j.Parent < joints.Count && depth < 256 ? local * Get(j.Parent, depth + 1) : local;
            done[k] = true; return g[k];
        }
        for (int k = 0; k < joints.Count; k++) Get(k, 0);
        return g;
    }

    /// <summary>FBX Skin deformer for one mesh: a Cluster per referenced joint with control-point indexes/weights.</summary>
    static void WriteSkin(Node objects, Node conns, long geomId, MeshDraw d, long[] jointIds, Matrix4x4[] bind)
    {
        var per = new SortedDictionary<int, (List<int> Idx, List<double> W)>();
        for (int v = 0; v < d.Positions.Length && 4 * v + 3 < d.BlendIndices!.Length; v++)
            for (int k = 0; k < 4; k++)
            {
                int j = d.BlendIndices[4 * v + k]; double w = d.BlendWeights![4 * v + k];
                if (w <= 0 || j < 0 || j >= jointIds.Length) continue;
                if (!per.TryGetValue(j, out var l)) per[j] = l = (new List<int>(), new List<double>());
                l.Idx.Add(v); l.W.Add(w);
            }
        if (per.Count == 0) return;
        long skinId = _nextId++;
        var skin = new Node("Deformer", skinId, "Skin\0\u0001Deformer", "Skin");
        skin.Add("Version", 101); skin.Add("Link_DeformAcuracy", 50.0);
        objects.Children.Add(skin);
        conns.Add("C", "OO", skinId, geomId);
        var mesh = Matrix4x4.Identity;   // positions are written already transformed (game axes)
        foreach (var (j, l) in per)
        {
            long cid = _nextId++;
            var c = new Node("Deformer", cid, $"Cluster{j}\0\u0001SubDeformer", "Cluster");
            c.Add("Version", 100); c.Add("UserData", "", "");
            c.Add("Indexes", l.Idx.ToArray()); c.Add("Weights", l.W.ToArray());
            Matrix4x4.Invert(bind[j], out var inv);
            c.Add("Transform", Flat(mesh * inv)); c.Add("TransformLink", Flat(bind[j]));
            objects.Children.Add(c);
            conns.Add("C", "OO", cid, skinId);
            conns.Add("C", "OO", jointIds[j], cid);
        }
    }

    static double[] Flat(Matrix4x4 m) => new double[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 };

    const long KTimePerSecond = 46186158000L;

    /// <summary>
    /// One FBX take: every joint gets Lcl Rotation / Lcl Translation (and Lcl Scaling when animated) curves sampled at
    /// every frame (30 fps). Rotation = key quaternion (slerp between stored keys, like the game), translation = bind-pose
    /// local translation + key offset (lerp), both in game axes like the mesh.
    /// </summary>
    static void WriteAnimation(Node objects, Node conns, IReadOnlyList<Joint> joints, long[] jointIds, AnimAsset anim, string name)
    {
        int frames = Math.Max(1, anim.Frames);
        long stackId = _nextId++, layerId = _nextId++;
        long stop = (long)((frames - 1) * KTimePerSecond / AnimAsset.Fps);
        var stack = new Node("AnimationStack", stackId, $"{name}\0\u0001AnimStack", "");
        var sp = stack.Add("Properties70");
        sp.Add("P", "LocalStop", "KTime", "Time", "", stop); sp.Add("P", "ReferenceStop", "KTime", "Time", "", stop);
        objects.Children.Add(stack);
        objects.Children.Add(new Node("AnimationLayer", layerId, "BaseLayer\0\u0001AnimLayer", ""));
        conns.Add("C", "OO", layerId, stackId);
        var times = Enumerable.Range(0, frames).Select(f => (long)(f * KTimePerSecond / AnimAsset.Fps)).ToArray();
        int n = Math.Min(joints.Count, anim.Tracks);
        for (int j = 0; j < n; j++)
        {
            var rx = new float[frames]; var ry = new float[frames]; var rz = new float[frames];
            var tx = new float[frames]; var ty = new float[frames]; var tz = new float[frames];
            var sx = new float[frames]; var sy = new float[frames]; var sz = new float[frames];
            Vector3 prev = default;
            for (int f = 0; f < frames; f++)
            {
                var (q, t, s) = Sample(anim, j, f);
                var e = QuatToEulerXyzDegrees(q, allowZero: true);
                if (f > 0) e = Unwrap(e, prev);
                prev = e;
                rx[f] = e.X; ry[f] = e.Y; rz[f] = e.Z;
                var lt = joints[j].LocalTranslation + t;
                tx[f] = lt.X; ty[f] = lt.Y; tz[f] = lt.Z;
                sx[f] = s.X; sy[f] = s.Y; sz[f] = s.Z;
            }
            AddCurveNode(objects, conns, layerId, jointIds[j], "R", "Lcl Rotation", times, rx, ry, rz);
            AddCurveNode(objects, conns, layerId, jointIds[j], "T", "Lcl Translation", times, tx, ty, tz);
            if (sx.Concat(sy).Concat(sz).Any(v => Math.Abs(v - 1f) > 1e-4f))
                AddCurveNode(objects, conns, layerId, jointIds[j], "S", "Lcl Scaling", times, sx, sy, sz);
        }
    }

    static (Quaternion Q, Vector3 T, Vector3 S) Sample(AnimAsset a, int track, int frame)
    {
        var keys = a.Keys[track]; var kf = a.KeyFrames;
        for (int k = 0; k + 1 < kf.Length; k++)
            if (frame >= kf[k] && frame <= kf[k + 1])
            {
                float u = kf[k + 1] == kf[k] ? 0f : (frame - kf[k]) / (float)(kf[k + 1] - kf[k]);
                return (Quaternion.Slerp(keys[k].Rotation, keys[k + 1].Rotation, u),
                        Vector3.Lerp(keys[k].Translation, keys[k + 1].Translation, u),
                        Vector3.Lerp(keys[k].Scale, keys[k + 1].Scale, u));
            }
        var last = keys[^1];
        return (last.Rotation, last.Translation, last.Scale);
    }

    static Vector3 Unwrap(Vector3 e, Vector3 prev)
    {
        static float U(float a, float p) { while (a - p > 180) a -= 360; while (a - p < -180) a += 360; return a; }
        return new Vector3(U(e.X, prev.X), U(e.Y, prev.Y), U(e.Z, prev.Z));
    }

    static void AddCurveNode(Node objects, Node conns, long layerId, long modelId, string tag, string prop, long[] times, float[] x, float[] y, float[] z)
    {
        long cn = _nextId++;
        var node = new Node("AnimationCurveNode", cn, $"{tag}\0\u0001AnimCurveNode", "");
        var p70 = node.Add("Properties70");
        p70.Add("P", "d|X", "Number", "", "A", (double)x[0]); p70.Add("P", "d|Y", "Number", "", "A", (double)y[0]); p70.Add("P", "d|Z", "Number", "", "A", (double)z[0]);
        objects.Children.Add(node);
        conns.Add("C", "OO", cn, layerId);
        conns.Add("C", "OP", cn, modelId, prop);
        foreach (var (axis, v) in new[] { ("d|X", x), ("d|Y", y), ("d|Z", z) })
        {
            long cid = _nextId++;
            var c = new Node("AnimationCurve", cid, "\0\u0001AnimCurve", "");
            c.Add("Default", (double)v[0]); c.Add("KeyVer", 4009);
            c.Add("KeyTime", times); c.Add("KeyValueFloat", v);
            c.Add("KeyAttrFlags", new[] { 0x00000108 });                 // linear interpolation
            c.Add("KeyAttrDataFloat", new[] { 0f, 0f, 0f, 0f });
            c.Add("KeyAttrRefCount", new[] { times.Length });
            objects.Children.Add(c);
            conns.Add("C", "OP", cid, cn, axis);
        }
    }

    static Vector3 QuatToEulerXyzDegrees(Quaternion q, bool allowZero = false)
    {
        q = Quaternion.Normalize(q);
        if (Math.Abs(q.X) < 1e-6 && Math.Abs(q.Y) < 1e-6 && Math.Abs(q.Z) < 1e-6) return Vector3.Zero;   // (allowZero: same result)
        // FBX XYZ order: R = Rz * Ry * Rx
        double sinr = 2 * (q.W * q.X + q.Y * q.Z), cosr = 1 - 2 * (q.X * q.X + q.Y * q.Y);
        double sinp = Math.Clamp(2 * (q.W * q.Y - q.Z * q.X), -1, 1);
        double siny = 2 * (q.W * q.Z + q.X * q.Y), cosy = 1 - 2 * (q.Y * q.Y + q.Z * q.Z);
        const double d = 180 / Math.PI;
        return new Vector3((float)(Math.Atan2(sinr, cosr) * d), (float)(Math.Asin(sinp) * d), (float)(Math.Atan2(siny, cosy) * d));
    }

    static void WriteNode(BinaryWriter w, Node n)
    {
        long start = w.BaseStream.Position;
        w.Write(0u); w.Write((uint)n.Props.Count); w.Write(0u);
        var nb = Encoding.ASCII.GetBytes(n.Name); w.Write((byte)nb.Length); w.Write(nb);
        long propStart = w.BaseStream.Position;
        foreach (var p in n.Props) WriteProp(w, p);
        long propEnd = w.BaseStream.Position;
        foreach (var c in n.Children) WriteNode(w, c);
        if (n.Children.Count > 0 || n.Props.Count == 0) w.Write(new byte[13]);
        long end = w.BaseStream.Position;
        w.BaseStream.Position = start; w.Write((uint)end); w.BaseStream.Position = start + 8; w.Write((uint)(propEnd - propStart));
        w.BaseStream.Position = end;
    }

    static void WriteProp(BinaryWriter w, object p)
    {
        switch (p)
        {
            case bool b: w.Write((byte)'C'); w.Write((byte)(b ? 1 : 0)); break;
            case int i: w.Write((byte)'I'); w.Write(i); break;
            case long l: w.Write((byte)'L'); w.Write(l); break;
            case double d: w.Write((byte)'D'); w.Write(d); break;
            case float f: w.Write((byte)'D'); w.Write((double)f); break;
            case string s: { var b = Encoding.UTF8.GetBytes(s); w.Write((byte)'S'); w.Write((uint)b.Length); w.Write(b); break; }
            case double[] da: w.Write((byte)'d'); w.Write((uint)da.Length); w.Write(0u); w.Write((uint)(da.Length * 8)); foreach (var x in da) w.Write(x); break;
            case int[] ia: w.Write((byte)'i'); w.Write((uint)ia.Length); w.Write(0u); w.Write((uint)(ia.Length * 4)); foreach (var x in ia) w.Write(x); break;
            case long[] la: w.Write((byte)'l'); w.Write((uint)la.Length); w.Write(0u); w.Write((uint)(la.Length * 8)); foreach (var x in la) w.Write(x); break;
            case float[] fa: w.Write((byte)'f'); w.Write((uint)fa.Length); w.Write(0u); w.Write((uint)(fa.Length * 4)); foreach (var x in fa) w.Write(x); break;
            default: throw new ArgumentException("unsupported FBX property " + p.GetType());
        }
    }
}
