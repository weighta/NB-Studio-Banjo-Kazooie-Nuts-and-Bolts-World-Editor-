using System.Numerics;
using System.Text;
using NB.Core.Formats;
using NB.Core.IO;
using NB.Core.Models;

namespace NB.Core.Havok;

/// <summary>
/// Replaces the triangle mesh of a mesh collision asset (aid_havok_* whose wrapper has a type-1 entry: scenery and
/// world collision) with new triangles, e.g. from an OBJ/FBX.
///
/// How the game loads these assets (default.exe, havok asset resolver 0x82306F78 → 0x82307C70): it finds the wrapper's
/// type-1 entry E, loads the packfile at (E+0, E+4) with hkBinaryPackfileReader, takes the contents as
/// getContents(getContentsClassName()), then unconditionally reads contents+0x34 (hkpMoppBvTreeShape child shape) as
/// the hkpExtendedMeshShape and plugs E's external buffers into trianglesSubparts[0]: vertexBase = E+0x10,
/// indexBase = E+0x08, and — when E+0x1C ≠ 0 — materialIndexBase = E+0x18 (u8 per triangle), materialBase = E+0x1C
/// (12-byte records), numMaterials = E+0x20. Counts, strides and the MOPP come from the packfile.
///
/// Consequently the root must stay an hkpMoppBvTreeShape (making the extended mesh shape the contents would make the
/// resolver read a float of aabbHalfExtents as a pointer), and the MOPP code is regenerated for the new triangles
/// (<see cref="MoppBuilder"/>). Edits:
/// <list type="bullet">
/// <item>packfile: subpart counts/strides, extended mesh AABB, welding info emptied, new MOPP code + code info
/// (in place when it fits, else appended to __data__ with a new local fixup; the section layout is rewritten by
/// <see cref="HkPackfileWriter"/>);</item>
/// <item>asset .data: new packfile, index buffer (u16, or u32 above 65 535 vertices), float3 vertex buffer and material
/// index buffer are appended (16-aligned) and E's pointers/counts repointed. These pointers already carry CAFF
/// relocations to the asset's own part, so no relocation changes; the material table and everything else stays.
/// A previous import's appended tail is dropped first, so re-importing does not grow the asset.</item>
/// </list>
/// </summary>
public static class HkCollisionImport
{
    public sealed class Result
    {
        public int Triangles, Vertices, MoppBytes, OldTriangles, OldVertices;
        public Vector3 Min, Max;
        public readonly List<string> Notes = new();
        /// <summary>Offsets in the asset .data holding pointers to the asset's own part (CAFF relocations required).</summary>
        public readonly List<int> SelfPointers = new();
    }

    /// <summary>Wrapper type-1 entry offset in the asset .data (−1 if none).</summary>
    /// <summary>
    /// Breakable scenery (cafe tables, benches, lamp posts, fences...): the wrapper has a type-6 entry with two more
    /// packfiles (the pieces as a physics system; rigid bodies with property 0x2001 make the resolver 0x823075B8 set
    /// kind 5, and the world builds such instances through 0x82307DF0). Their static collision is a shape subpart of
    /// the pieces. Replacing it with triangles froze the game in Xenia (cafetable, 2026-09-27), so it is refused
    /// unless forced.
    /// </summary>
    public static bool IsBreakable(byte[] d)
    {
        if (d.Length < 8) return false;
        int tab = BE.S32(d, 0), n = BE.S32(d, 4);
        for (int i = 0; i < n && tab >= 0 && tab + 8 * i + 8 <= d.Length; i++)
            if (BE.S32(d, tab + 8 * i) == 6) return true;
        return false;
    }

    public static int TypeOneEntry(byte[] d)
    {
        if (d.Length < 8) return -1;
        int tab = BE.S32(d, 0), n = BE.S32(d, 4);
        for (int i = 0; i < n && tab >= 0 && tab + 8 * i + 8 <= d.Length; i++)
            if (BE.S32(d, tab + 8 * i) == 1) return BE.S32(d, tab + 8 * i + 4);
        return -1;
    }

    /// <summary>Merges import meshes into one triangle list (game space), applying scale then offset.</summary>
    public static (List<Vector3> Positions, List<int> Triangles) Merge(IEnumerable<ImportMesh> meshes, float scale = 1, Vector3 offset = default)
    {
        var p = new List<Vector3>(); var t = new List<int>();
        foreach (var m in meshes)
        {
            int b = p.Count;
            p.AddRange(m.Positions.Select(v => v * scale + offset));
            t.AddRange(m.Triangles.Select(i => i + b));
        }
        // weld identical positions (OBJ/FBX split vertices by uv/normal; collision only needs positions)
        var map = new Dictionary<Vector3, int>(); var np = new List<Vector3>(); var remap = new int[p.Count];
        for (int i = 0; i < p.Count; i++)
        {
            if (!map.TryGetValue(p[i], out int k)) { k = np.Count; map[p[i]] = k; np.Add(p[i]); }
            remap[i] = k;
        }
        var nt = new List<int>();
        for (int i = 0; i + 2 < t.Count; i += 3)
        {
            int a = remap[t[i]], b = remap[t[i + 1]], c = remap[t[i + 2]];
            if (a != b && b != c && a != c) { nt.Add(a); nt.Add(b); nt.Add(c); }   // drop degenerate triangles
        }
        return (np, nt);
    }

    /// <summary>12 triangles of the box (min, max), game winding (clockwise seen from outside, like ObjReader output).</summary>
    public static (List<Vector3> Positions, List<int> Triangles) Box(Vector3 mn, Vector3 mx)
    {
        var p = new List<Vector3>();
        for (int i = 0; i < 8; i++) p.Add(new Vector3((i & 1) != 0 ? mx.X : mn.X, (i & 2) != 0 ? mx.Y : mn.Y, (i & 4) != 0 ? mx.Z : mn.Z));
        // same faces as HkCollision.Box (which reproduces the game's own boxes)
        int[] f = { 0, 2, 3, 0, 3, 1, 4, 5, 7, 4, 7, 6, 0, 1, 5, 0, 5, 4, 2, 6, 7, 2, 7, 3, 0, 4, 6, 0, 6, 2, 1, 3, 7, 1, 7, 5 };
        return (p, f.ToList());
    }

    /// <summary>Replaces the collision mesh of asset <paramref name="symbol"/> in <paramref name="caff"/>.</summary>
    public static Result Replace(CaffFile caff, int symbol, IReadOnlyList<Vector3> positions, IReadOnlyList<int> triangles, byte[]? material = null, bool allowBreakable = false)
    {
        var view = new AssetView(caff, symbol);
        if (!view.Has(".data")) throw new InvalidDataException("asset has no .data part");
        int pid = view.PartId(".data");
        var part = view.Part(pid);
        var (data, res) = ReplaceData(part.Data, positions, triangles, material, allowBreakable);
        // pointer fields that were null before (assets whose mesh had only shape subparts) need relocations
        var reloc = caff.Relocs.FirstOrDefault(x => x.FromPart == pid && x.ToPart == pid);
        var missing = res.SelfPointers.Where(o => !caff.Relocs.Any(x => x.FromPart == pid && x.Offsets.Contains(o))).ToList();
        if (missing.Count > 0)
        {
            if (reloc == null) throw new InvalidDataException("asset has no self relocation group");
            reloc.Offsets = reloc.Offsets.Concat(missing).OrderBy(x => x).ToArray();
            res.Notes.Add($"{missing.Count} relocation(s) added for new buffer pointers");
        }
        part.Data = data;
        return res;
    }

    /// <summary>Material record used when the asset has no material table (the most common record of the shipped assets).</summary>
    public static readonly byte[] DefaultMaterial = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4 };

    /// <param name="material">12-byte material record for assets without a material table (default <see cref="DefaultMaterial"/>).</param>
    public static (byte[] Data, Result Result) ReplaceData(byte[] asset, IReadOnlyList<Vector3> positions, IReadOnlyList<int> triangles, byte[]? material = null, bool allowBreakable = false)
    {
        if (IsBreakable(asset) && !allowBreakable)
            throw new InvalidDataException("breakable scenery (the asset carries physics pieces, wrapper entry type 6): replacing its collision froze the game; not supported");
        int nTri = triangles.Count / 3, nv = positions.Count;
        if (nTri == 0 || nv == 0) throw new ArgumentException("the mesh has no triangles");
        if (triangles.Any(i => i < 0 || i >= nv)) throw new ArgumentException("triangle index out of range");
        int e = TypeOneEntry(asset);
        if (e < 0 || e + 0x24 > asset.Length) throw new InvalidDataException("not a mesh collision asset (no wrapper type-1 entry)");
        int pfOff = BE.S32(asset, e), pfSize = BE.S32(asset, e + 4);
        int idxOff = BE.S32(asset, e + 8), vtxOff = BE.S32(asset, e + 0x10), matIdxOff = BE.S32(asset, e + 0x18), matBase = BE.S32(asset, e + 0x1C);
        if (pfOff <= 0 || pfSize <= 0 || pfOff + pfSize > asset.Length || BE.U32(asset, pfOff) != 0x57E0E057)
            throw new InvalidDataException("type-1 entry does not point at a Havok packfile");
        var r = new Result();
        // materials: every shipped triangle mesh has a material table (E+0x1C) and a u8 index per triangle (E+0x18);
        // the game plugs them into subpart 0 - a triangle subpart without materials never occurs in the game data
        int numMat = BE.S32(asset, e + 0x20);
        byte[] matTable;
        if (matBase > 0 && numMat > 0 && matBase + 12 * numMat <= asset.Length) matTable = asset.AsSpan(matBase, 12 * numMat).ToArray();
        else
        {
            if (material != null && material.Length != 12) throw new ArgumentException("material record must be 12 bytes");
            matTable = (byte[])(material ?? DefaultMaterial).Clone(); numMat = 1;
            r.Notes.Add($"material table created (1 record {Convert.ToHexString(matTable)}); the old collision had none");
        }

        var pfBytes = asset.AsSpan(pfOff, pfSize).ToArray();
        var pf = HkPackfile.Parse(pfBytes);
        var w = HkPackfileWriter.Read(pfBytes);
        int di = w.DataIndex;
        if (di != pf.Sections.IndexOf(pf.Data)) throw new InvalidDataException("section mismatch");
        int contentsSec = BE.S32(pfBytes, 0x18), contentsOff = BE.S32(pfBytes, 0x1C);
        if (contentsSec != di) throw new InvalidDataException("packfile contents are not in __data__");
        string rootClass = pf.Data.Objects.FirstOrDefault(o => o.Offset == contentsOff).Class ?? "?";
        if (rootClass != "hkpMoppBvTreeShape") throw new InvalidDataException($"collision root is {rootClass}, expected hkpMoppBvTreeShape");
        var mopp = pf.Object(contentsOff, rootClass);
        var ems = mopp.Struct("child").Ref("childShape") ?? throw new InvalidDataException("MOPP has no child shape");
        if (ems.Class?.Name != "hkpExtendedMeshShape") throw new InvalidDataException($"MOPP child is {ems.Class?.Name}, expected hkpExtendedMeshShape");
        var code = mopp.Ref("code") ?? throw new InvalidDataException("MOPP has no code");
        if (code.Section != di || ems.Section != di) throw new InvalidDataException("objects outside __data__");
        var (sp, sn) = ems.Array("trianglesSubparts");
        var subCls = ems.Class!.Find("trianglesSubparts")!.Class!;
        if (sn > 1) r.Notes.Add($"{sn} triangle subparts: the game plugs the buffers into subpart 0 only; the others are emptied");
        HkObject? created = null;
        if (sp == null || sn < 1)
        {
            // mesh made of shape subparts only (boxes, convex pieces): add a triangles subpart array of one element
            // pointed to by a new local fixup
            var template = new byte[subCls.Size];   // all fields are written below
            int at = w.Append(di, template);
            w.SetLocalPointer(di, Off(ems, "trianglesSubparts"), at);
            BE.W32(w.PayloadSpan(di), Off(ems, "trianglesSubparts") + 4, 1);
            created = new HkObject(pf, di, at, subCls);
            r.Notes.Add("triangles subpart added (the old collision had none)");
        }
        var (shp, shn) = ems.Array("shapesSubparts");
        if (shn > 0)
        {
            BE.W32(w.PayloadSpan(di), Off(ems, "shapesSubparts") + 4, 0);
            r.Notes.Add($"{shn} shape subpart(s) of the old collision removed");
        }
        var pay = w.Data.Payload.ToArray();   // edited in place, committed before the layout changes

        // extended mesh scaling: stored vertices are multiplied by it
        var scaling = ems.Has("scaling") ? ems.V4("scaling") : Vector4.One;
        var sc = new Vector3(scaling.X == 0 ? 1 : scaling.X, scaling.Y == 0 ? 1 : scaling.Y, scaling.Z == 0 ? 1 : scaling.Z);
        var stored = positions.Select(p => p / sc).ToList();
        float radius = ems.Has("triangleRadius") ? ems.F("triangleRadius") : ems.Has("radius") ? ems.F("radius") : 0.05f;
        bool i32 = nv > 0xFFFF;

        void WriteI(HkObject o, string f, int v) => BE.W32(pay, Off(o, f), v);
        void WriteU16(HkObject o, string f, int v) => BE.W16(pay, Off(o, f), (ushort)v);
        void WriteU8(HkObject o, string f, int v) => pay[Off(o, f)] = (byte)v;
        void WriteV4(HkObject o, string f, Vector4 v) { int a = Off(o, f); BE.WF32(pay, a, v.X); BE.WF32(pay, a + 4, v.Y); BE.WF32(pay, a + 8, v.Z); BE.WF32(pay, a + 12, v.W); }
        void WriteSized(HkObject o, string f, int v)
        {
            var m = o.Class!.Find(f)!;
            var t = m.Type is HkType.Enum or HkType.Flags ? m.SubType : m.Type;
            switch (t)
            {
                case HkType.Bool or HkType.Char or HkType.Int8 or HkType.UInt8: WriteU8(o, f, v); break;
                case HkType.Int16 or HkType.UInt16: WriteU16(o, f, v); break;
                default: WriteI(o, f, v); break;
            }
        }

        void EditSubpart(HkObject s, bool active, bool countOld)
        {
            if (countOld) { r.OldTriangles += s.I("numTriangleShapes"); r.OldVertices += s.I("numVertices"); }
            foreach (var f in new[] { "vertexBase", "indexBase" })
                if (s.Ptr(f) != null) throw new InvalidDataException($"subpart {f} is a packfile pointer (unsupported layout)");
            WriteI(s, "numTriangleShapes", active ? nTri : 0);
            WriteI(s, "numVertices", active ? nv : 0);
            WriteSized(s, "vertexStriding", 12);
            WriteSized(s, "indexStriding", i32 ? 12 : 6);
            WriteSized(s, "stridingType", i32 ? 2 : 1);   // INDICES_INT16 = 1, INDICES_INT32 = 2
            // the remaining fields exactly as in every shipped triangle subpart (the game overwrites the material fields)
            if (s.Has("type")) WriteSized(s, "type", 0);                                   // SUBPART_TRIANGLES
            if (s.Has("materialIndexStridingType")) WriteSized(s, "materialIndexStridingType", 1);   // u8 indices
            if (s.Has("materialStriding")) WriteSized(s, "materialStriding", 12);
            if (s.Has("materialIndexStriding")) WriteSized(s, "materialIndexStriding", 1);
            if (s.Has("numMaterials")) WriteSized(s, "numMaterials", numMat);
            if (s.Has("flipAlternateTriangles")) WriteSized(s, "flipAlternateTriangles", 0);
            if (s.Has("triangleOffset")) WriteI(s, "triangleOffset", 0);
            if (s.Has("extrusion")) WriteV4(s, "extrusion", Vector4.Zero);
            foreach (var f in new[] { "materialIndexBase", "materialBase", "vertexBase", "indexBase" })
                if (s.Has(f) && s.Ptr(f) == null) WriteI(s, f, 0);
        }
        if (created != null) EditSubpart(created, true, false);
        else for (int i = 0; i < sn; i++) EditSubpart(ems.At(sp!.Value.Section, sp.Value.Offset + subCls.Size * i, subCls.Name), i == 0, true);
        // the embedded subpart duplicates subpart 0 (the game only uses the array; kept consistent)
        if (ems.Has("embeddedTrianglesSubpart")) EditSubpart(ems.Struct("embeddedTrianglesSubpart"), true, false);

        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var p in positions) { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
        r.Min = mn; r.Max = mx; r.Triangles = nTri; r.Vertices = nv;
        if (ems.Has("aabbHalfExtents") && ems.Has("aabbCenter"))
        {
            var he = ems.V4("aabbHalfExtents"); var ce = ems.V4("aabbCenter");
            var h = (mx - mn) / 2 + new Vector3(radius); var c = (mx + mn) / 2;
            WriteV4(ems, "aabbHalfExtents", new Vector4(h, he.W));
            WriteV4(ems, "aabbCenter", new Vector4(c, ce.W));
        }
        // welding: every shipped mesh has disableWelding = 1; the per-triangle welding info of the old mesh is dropped
        // and the welding type set to NONE, so the triangle shapes get no welding data
        if (ems.Has("disableWelding")) WriteSized(ems, "disableWelding", 1);
        if (ems.Has("weldingInfo"))
        {
            var (wp, wn) = ems.Array("weldingInfo");
            BE.W32(pay, Off(ems, "weldingInfo") + 4, 0);
            if (wn > 0) r.Notes.Add($"welding info of {wn} old triangles removed");
        }
        if (ems.Has("weldingType")) WriteSized(ems, "weldingType", WeldingNone(pf));

        // MOPP code for the new triangles
        var built = MoppBuilder.Build(positions, triangles, radius);
        var keys = MoppBuilder.FindAll(built.Code);
        if (keys.Count != nTri || keys.Distinct().Count() != nTri || keys.Min() != 0 || keys.Max() != nTri - 1)
            throw new InvalidOperationException("internal error: MOPP does not reach every triangle exactly once");
        r.MoppBytes = built.Code.Length;
        var info = code.Struct("info");
        // code info is in the stored (unscaled) space of the extended mesh; the MOPP is built on the scaled positions,
        // which equal the stored ones unless the shape has a non-unit scaling (then the MOPP is still conservative only if 1)
        if (sc != Vector3.One) r.Notes.Add("extended mesh scaling is not 1: MOPP built in scaled space");
        WriteV4(info, "offset", built.CodeInfo);
        // chunked like every shipped MOPP (512-byte chunks joined by opcode 0x0C)
        if (code.Has("buildType")) WriteSized(code, "buildType", EnumValue(pf, "hkpMoppCode", "buildType", "BUILT_WITH_CHUNK_SUBDIVISION") ?? 0);
        int dataField = Off(code, "data");
        var (cp, cn) = code.Array("data");
        int cap = BE.S32(pay, dataField + 8);
        if (cp != null && cp.Value.Section == di && built.Code.Length <= cn)
        {
            built.Code.CopyTo(pay, cp.Value.Offset);
            pay.AsSpan(cp.Value.Offset + built.Code.Length, cn - built.Code.Length).Fill(0xCD);
            BE.W32(pay, dataField + 4, built.Code.Length);
            w.Data.Payload = pay.ToList();
        }
        else
        {
            BE.W32(pay, dataField + 4, built.Code.Length);
            BE.W32(pay, dataField + 8, (int)((uint)cap & 0xC0000000u) | built.Code.Length);
            w.Data.Payload = pay.ToList();
            int at = w.Append(di, built.Code);
            while (w.Data.Payload.Count % 16 != 0) w.Data.Payload.Add(0xCD);
            w.SetLocalPointer(di, dataField, at);
        }
        var newPf = w.Write();

        // external buffers
        var ib = new byte[nTri * (i32 ? 12 : 6)];
        for (int t = 0; t < triangles.Count; t++)
            if (i32) BE.W32(ib, 4 * t, triangles[t]); else BE.W16(ib, 2 * t, (ushort)triangles[t]);
        var vb = new byte[nv * 12];
        for (int v = 0; v < nv; v++) { BE.WF32(vb, 12 * v, stored[v].X); BE.WF32(vb, 12 * v + 4, stored[v].Y); BE.WF32(vb, 12 * v + 8, stored[v].Z); }
        byte mat = 0;
        if (matIdxOff > 0 && matBase > 0 && r.OldTriangles > 0 && matIdxOff + r.OldTriangles <= asset.Length)
            mat = asset.AsSpan(matIdxOff, r.OldTriangles).ToArray().GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key;
        if (mat >= numMat) mat = 0;
        var mb = Enumerable.Repeat(mat, nTri).ToArray();
        r.Notes.Add($"material index {mat} for every triangle ({numMat} material(s))");

        // drop a tail appended by a previous import (nothing else points into it), then append
        int keep = asset.Length;
        // (a material table before the rewritten buffers is the asset's own and stays; one after them was appended by us)
        int tail = new[] { pfOff, idxOff, vtxOff, matBase > 0 ? matIdxOff : 0 }.Where(x => x > 0).Min();
        if (tail > e + 0x24 && tail < asset.Length && IsFreeTail(asset, e, tail)) keep = tail;
        bool putTable = !(matBase > 0 && matBase < keep);   // table missing, or it lived in the dropped tail
        var o = new MemoryStream();
        o.Write(asset, 0, keep);
        int Put(byte[] b) { while (o.Length % 16 != 0) o.WriteByte(0); int at = (int)o.Length; o.Write(b); return at; }
        // the game reads E+0x18 (material index per triangle) only when E+0x1C (material table) is set
        int nPf = Put(newPf), nIb = Put(ib), nVb = Put(vb), nMb = Put(mb), nMt = putTable ? Put(matTable) : matBase;
        while (o.Length % 16 != 0) o.WriteByte(0);
        var nd = o.ToArray();
        BE.W32(nd, e, nPf); BE.W32(nd, e + 4, newPf.Length);
        BE.W32(nd, e + 8, nIb); BE.W32(nd, e + 0xC, nTri * 3);
        BE.W32(nd, e + 0x10, nVb); BE.W32(nd, e + 0x14, nv);
        BE.W32(nd, e + 0x18, nMb); BE.W32(nd, e + 0x1C, nMt); BE.W32(nd, e + 0x20, numMat);
        r.SelfPointers.AddRange(new[] { e, e + 8, e + 0x10, e + 0x18, e + 0x1C });
        return (nd, r);
    }

    /// <summary>
    /// Reads an OBJ/FBX (game space, as the model importer), applies scale then offset (or builds the box of its AABB)
    /// and replaces the collision of <paramref name="symbol"/>. The result is verified by decoding the new asset.
    /// </summary>
    public static Result ImportFile(CaffFile caff, int symbol, string meshPath, float scale = 1, Vector3 offset = default, bool box = false, bool allowBreakable = false)
    {
        var meshes = ObjReader.ReadAny(meshPath);
        var (p, t) = Merge(meshes, scale, offset);
        if (box)
        {
            if (p.Count == 0) throw new InvalidDataException("mesh has no vertices");
            (p, t) = Box(p.Aggregate(Vector3.Min), p.Aggregate(Vector3.Max));
        }
        var res = Replace(caff, symbol, p, t, null, allowBreakable);
        var view = new AssetView(caff, symbol);
        res.Notes.Add(Verify(view.Data(".data"), p, t));
        return res;
    }

    /// <summary>
    /// Decodes the asset like the display/export path (HkCollision) and checks that exactly the given triangles come
    /// back (same vertex positions per triangle corner) and that the MOPP reaches each triangle once.
    /// Throws on mismatch; returns a one-line summary.
    /// </summary>
    public static string Verify(byte[] asset, IReadOnlyList<Vector3> positions, IReadOnlyList<int> triangles)
    {
        var hc = HkCollision.ExtractAsset(asset);
        if (hc.Notes.Count > 0) throw new InvalidDataException("decode notes: " + string.Join("; ", hc.Notes));
        var mesh = hc.Meshes.Where(m => m.Kind == "mesh").ToList();
        if (mesh.Count != 1) throw new InvalidDataException($"expected one decoded mesh, got {mesh.Count}");
        var m0 = mesh[0];
        if (m0.Triangles.Count != triangles.Count) throw new InvalidDataException($"decoded {m0.Triangles.Count / 3} triangles, expected {triangles.Count / 3}");
        float worst = 0;
        for (int i = 0; i < triangles.Count; i++) worst = MathF.Max(worst, Vector3.Distance(m0.Positions[m0.Triangles[i]], positions[triangles[i]]));
        if (worst > 1e-4f) throw new InvalidDataException($"decoded positions differ (max {worst})");
        var pf = HkPackfile.FromAsset(asset);
        var mopp = pf.ObjectsOf("hkpMoppCode").First();
        var (cp, cn) = mopp.Array("data");
        var code = pf.SectionBytes(cp!.Value.Section).AsSpan(cp.Value.Offset, cn).ToArray();
        var keys = MoppBuilder.FindAll(code);
        if (keys.Count != triangles.Count / 3 || keys.Distinct().Count() != keys.Count) throw new InvalidDataException("MOPP terminals do not match the triangles");
        var mn = m0.Positions.Aggregate(Vector3.Min); var mx = m0.Positions.Aggregate(Vector3.Max);
        return $"verified: {triangles.Count / 3} triangles / {m0.Positions.Count} vertices decode back (max error {worst:G3}), bounds {mn} .. {mx}, MOPP {cn} bytes reaches all {keys.Count}";
    }

    /// <summary>
    /// Checks a mesh collision asset against the invariants every shipped one satisfies (survey of the 390 Showdown Town
    /// triangle meshes): wrapper buffers and counts, material table and indices, subpart fields, welding flags, chunked
    /// MOPP whose every path terminates and reaches each triangle once. Returns the problems (empty = like the game data).
    /// </summary>
    public static List<string> CheckStructure(byte[] asset)
    {
        var p = new List<string>();
        int e = TypeOneEntry(asset);
        if (e < 0) { p.Add("no type-1 entry"); return p; }
        int S(int o) => BE.S32(asset, e + o);
        var pf = HkPackfile.FromAsset(asset);
        int contentsOff = BE.S32(asset.AsSpan(S(0)), 0x1C);
        var root = pf.Data.Objects.FirstOrDefault(o => o.Offset == contentsOff);
        if (root.Class != "hkpMoppBvTreeShape") { p.Add($"root {root.Class}"); return p; }
        var mopp = pf.Object(root.Offset, root.Class);
        var ems = mopp.Struct("child").Ref("childShape");
        if (ems?.Class?.Name != "hkpExtendedMeshShape") { p.Add("MOPP child is not an hkpExtendedMeshShape"); return p; }
        var code = mopp.Ref("code")!;
        var (cp, cn) = code.Array("data");
        var bytes = pf.SectionBytes(cp!.Value.Section).AsSpan(cp.Value.Offset, cn).ToArray();
        if (code.Has("buildType") && code.U8("buildType") != 0) p.Add($"MOPP buildType {code.U8("buildType")} (shipped: 0 = chunked)");
        if (cn % MoppBuilder.ChunkSize != 0) p.Add($"MOPP size {cn} not a multiple of 512");
        if (!(code.Struct("info").V4("offset").W > 0)) p.Add("MOPP scale not positive");
        var (sp, sn) = ems.Array("trianglesSubparts");
        List<int> keys;
        try { keys = MoppBuilder.FindAll(bytes); } catch (Exception x) { p.Add("MOPP: " + x.Message); return p; }
        if (sn == 0) return p;   // shape subparts only
        if (ems.Has("disableWelding") && ems.U8("disableWelding") != 1) p.Add("disableWelding != 1");
        var sub = ems.At(sp!.Value.Section, sp.Value.Offset, ems.Class!.Find("trianglesSubparts")!.Class!.Name);
        int nTri = sub.I("numTriangleShapes"), nv = sub.I("numVertices");
        var (wp, wn) = ems.Array("weldingInfo");
        if (wn != 0 && wn != nTri) p.Add($"welding info {wn} entries for {nTri} triangles");
        if (keys.Count != nTri || keys.Distinct().Count() != nTri || keys.Min() != 0 || keys.Max() != nTri - 1) p.Add("MOPP terminals are not the triangles 0..n-1 once each");
        void Eq(string what, long v, long want) { if (v != want) p.Add($"{what} = {v}, expected {want}"); }
        Eq("subpart type", sub.U8("type"), 0);
        Eq("materialIndexStridingType", sub.U8("materialIndexStridingType"), 1);
        Eq("materialStriding", BE.S16(pf.SectionBytes(sub.Section), Off(sub, "materialStriding")), 12);
        Eq("materialIndexStriding", BE.U16(pf.SectionBytes(sub.Section), Off(sub, "materialIndexStriding")), 1);
        Eq("numMaterials", BE.U16(pf.SectionBytes(sub.Section), Off(sub, "numMaterials")), S(0x20));
        Eq("vertexStriding", sub.I("vertexStriding"), 12);
        int st = sub.U8("stridingType"), istr = sub.I("indexStriding");
        if (!(st == 1 && istr == 6 || st == 2 && istr == 12)) p.Add($"index striding {st}/{istr}");
        Eq("triangleOffset", sub.I("triangleOffset"), 0);
        Eq("flipAlternateTriangles", sub.U8("flipAlternateTriangles"), 0);
        foreach (var f in new[] { "vertexBase", "indexBase", "materialBase", "materialIndexBase" })
            if (sub.Ptr(f) != null || BE.S32(pf.SectionBytes(sub.Section), Off(sub, f)) != 0) p.Add($"subpart {f} not null in the file");
        Eq("E+0x0C index count", S(0xC), 3L * nTri);
        Eq("E+0x14 vertex count", S(0x14), nv);
        if (S(0x20) < 1) p.Add("no materials (E+0x20)");
        int ib = S(8), vb = S(0x10), mi = S(0x18), mt = S(0x1C);
        if (ib <= 0 || ib + nTri * istr > asset.Length) p.Add("index buffer outside the asset");
        else
            for (int t = 0; t < 3 * nTri; t++)
            {
                long v = st == 1 ? BE.U16(asset, ib + 2 * t) : BE.S32(asset, ib + 4 * t);
                if (v < 0 || v >= nv) { p.Add($"index {t} = {v} out of range"); break; }
            }
        if (vb <= 0 || vb + 12 * nv > asset.Length) p.Add("vertex buffer outside the asset");
        if (vb % 4 != 0 || ib % 2 != 0) p.Add("buffer misaligned");
        if (mt <= 0 || mt + 12 * S(0x20) > asset.Length) p.Add("material table outside the asset");
        if (mi <= 0 || mi + nTri > asset.Length) p.Add("material index buffer outside the asset");
        else if (asset.AsSpan(mi, nTri).ToArray().Any(x => x >= S(0x20))) p.Add("material index >= number of materials");
        return p;
    }

    static int Off(HkObject o, string field) => o.Offset + (o.Class?.Find(field)?.Offset ?? throw new KeyNotFoundException($"{o.Class?.Name}.{field}"));

    /// <summary>
    /// True when every structure other than the type-1 buffers lies before <paramref name="tail"/>: the wrapper table,
    /// its entries and E itself (entries are small fixed records found through the table).
    /// </summary>
    static bool IsFreeTail(byte[] d, int e, int tail)
    {
        int tab = BE.S32(d, 0), n = BE.S32(d, 4);
        if (tab < 0 || tab + 8 * n > tail) return false;
        for (int i = 0; i < n; i++)
        {
            int p = BE.S32(d, tab + 8 * i + 4);
            if (p >= tail) return false;
        }
        return true;
    }

    /// <summary>Value of hkpWeldingUtility WELDING_TYPE_NONE from the packfile's enum reflection (6 in Havok 5.5).</summary>
    static int WeldingNone(HkPackfile pf) => EnumValue(pf, "hkpExtendedMeshShape", "weldingType", "WELDING_TYPE_NONE") ?? 6;

    /// <summary>Reads an enum item value from the __types__ reflection (hkClassMember +8 → hkClassEnum).</summary>
    public static int? EnumValue(HkPackfile pf, string cls, string member, string item)
    {
        foreach (var (name, value) in EnumItems(pf, cls, member)) if (name == item) return value;
        return null;
    }

    public static List<(string Name, int Value)> EnumItems(HkPackfile pf, string cls, string member)
    {
        var list = new List<(string, int)>();
        var t = pf.Types; if (t == null) return list;
        // find the hkClass object and its member record
        foreach (var (off, c) in t.Objects)
        {
            if (c != "hkClass" || !t.Pointers.TryGetValue(off, out var np) || BE.CStr(t.Bytes, np.Offset, 256) != cls) continue;
            if (!t.Pointers.TryGetValue(off + 24, out var mp)) continue;
            int nm = BE.S32(t.Bytes, off + 28);
            for (int m = 0; m < nm; m++)
            {
                int mo = mp.Offset + 0x18 * m;
                if (!t.Pointers.TryGetValue(mo, out var mn) || BE.CStr(t.Bytes, mn.Offset, 256) != member) continue;
                if (!t.Pointers.TryGetValue(mo + 8, out var ep)) return list;
                if (!t.Pointers.TryGetValue(ep.Offset + 4, out var ip)) return list;
                int ni = BE.S32(t.Bytes, ep.Offset + 8);
                for (int k = 0; k < ni; k++)
                {
                    int io = ip.Offset + 8 * k;
                    string iname = t.Pointers.TryGetValue(io + 4, out var inp) ? BE.CStr(t.Bytes, inp.Offset, 256) : "?";
                    list.Add((iname, BE.S32(t.Bytes, io)));
                }
                return list;
            }
        }
        return list;
    }

    /// <summary>Reflection dump of the objects of a packfile (class members with offsets and current values).</summary>
    public static string Describe(HkPackfile pf)
    {
        var sb = new StringBuilder();
        var d = pf.Data; int di = pf.Sections.IndexOf(d);
        foreach (var (off, cls) in d.Objects)
        {
            var c = pf.Classes.GetValueOrDefault(cls);
            sb.AppendLine($"[{off:X4}] {cls} (size {c?.Size ?? -1:X})");
            if (c != null) DescribeMembers(sb, pf, di, off, c, "  ");
        }
        return sb.ToString();
    }

    static void DescribeMembers(StringBuilder sb, HkPackfile pf, int si, int baseOff, HkClass c, string indent)
    {
        if (c.Parent != null) DescribeMembers(sb, pf, si, baseOff, c.Parent, indent);
        var b = pf.SectionBytes(si);
        foreach (var m in c.Members)
        {
            int a = baseOff + m.Offset;
            string v = m.Type switch
            {
                HkType.Real => BE.F32(b, a).ToString("G6"),
                HkType.Vector4 => $"({BE.F32(b, a):G5}, {BE.F32(b, a + 4):G5}, {BE.F32(b, a + 8):G5}, {BE.F32(b, a + 12):G5})",
                HkType.Pointer or HkType.Array or HkType.SimpleArray => pf.Sections[si].Pointers.TryGetValue(a, out var p)
                    ? $"→ {p.Section}:{p.Offset:X}" + (m.Type != HkType.Pointer ? $" n={BE.S32(b, a + 4)}" : "") : $"null" + (m.Type != HkType.Pointer ? $" n={BE.S32(b, a + 4)}" : ""),
                HkType.Bool or HkType.Int8 or HkType.UInt8 or HkType.Char => b[a].ToString(),
                HkType.Int16 or HkType.UInt16 => BE.U16(b, a).ToString(),
                HkType.Enum or HkType.Flags => m.SubType is HkType.Int8 or HkType.UInt8 ? b[a].ToString() : m.SubType is HkType.Int16 or HkType.UInt16 ? BE.U16(b, a).ToString() : BE.S32(b, a).ToString(),
                HkType.Struct => "{struct}",
                _ => $"0x{BE.U32(b, a):X8}",
            };
            sb.AppendLine($"{indent}+{m.Offset:X2} {m.Name} : {m.Type}/{m.SubType}{(m.Class != null ? " " + m.Class.Name : "")} = {v}");
            if (m.Type == HkType.Struct && m.Class != null && m.CArraySize == 0) DescribeMembers(sb, pf, si, a, m.Class, indent + "    ");
        }
    }
}
