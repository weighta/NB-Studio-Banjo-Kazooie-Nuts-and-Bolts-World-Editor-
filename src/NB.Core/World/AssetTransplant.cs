using System.Numerics;
using NB.Core.Formats;
using NB.Core.Havok;
using NB.Core.IO;
using NB.Core.Models;
using NB.Core.Textures;

namespace NB.Core.World;

/// <summary>
/// What a copied scenery object needs to be pasted into ANOTHER workspace or world: its model's geometry per material,
/// the textures those materials show (decoded), and its collision. Built by NB Studio when objects are copied (Ctrl+C),
/// so the paste does not need the source workspace any more.
/// <para>Before 1.23 the clipboard held only the instance index in the source world bundle: pasted into another
/// workspace with the same world bundle (two Showdown Town workspaces), it duplicated whatever instance had that index
/// THERE (the user's oil rig became an unrelated untextured model).</para>
/// </summary>
public sealed class TransplantPackage
{
    public string SourceWorkspace = "";
    public uint SourceBundle;
    /// <summary>The copied models by source model name; <see cref="CarrierSuffix"/> marks the copy of the first object's
    /// model that also carries the level collision taken with the copy.</summary>
    public readonly Dictionary<string, TransplantModel> Models = new();
    public const string CarrierSuffix = "|with-collision";
    public readonly Dictionary<string, TransplantTexture> Textures = new();
    /// <summary>Reasons something could not be carried (the paste reports them and does not place a wrong model).</summary>
    public readonly List<string> Problems = new();
    /// <summary>What was changed to make a part buildable here (e.g. a blend cut out instead), reported with the paste.</summary>
    public readonly List<string> Notes = new();
}

/// <summary>
/// Puts a bundle built on a copy (<c>CaffFile.Read(shared.Write())</c> plus a paste) into the shared CaffFile object without
/// replacing what did not change: a part with the same bytes keeps its CaffPart AND its byte[] (the Atmosphere editor keeps
/// its light setups as references to those arrays, the music commands and the tag editor likewise); a changed part gets
/// the new bytes in its existing CaffPart; new parts are appended. <see cref="Undo"/> puts everything back (the save
/// failed). Moving the whole copy in (the first version of the paste) changed every array, so later Atmosphere edits
/// went into orphaned arrays and were saved as "nothing changed".
/// </summary>
public sealed class CaffMerge
{
    readonly CaffFile _target;
    readonly (byte[] Header, string Version, List<CaffSection> Sections, List<string> Symbols, byte[] ExtraName, List<CaffPart> Parts,
        List<CaffReloc> Relocs, List<(int Part, int Count)> PoolParts, byte[] PoolRecords) _before;
    readonly List<(CaffPart P, int Symbol, byte Align, int Offset, int Size, byte[] Data)> _changed = new();
    /// <summary>Parts kept as they were, parts that got new bytes or a new symbol number, parts added.</summary>
    public int Kept { get; private set; }
    public int Changed => _changed.Count;
    public int Added { get; private set; }

    CaffMerge(CaffFile t)
    {
        _target = t;
        _before = (t.Header, t.Version, t.Sections, t.Symbols, t.ExtraName, t.Parts, t.Relocs, t.PoolParts, t.PoolRecords);
    }

    public static CaffMerge Into(CaffFile target, CaffFile work)
    {
        // the paste only appends parts (CaffEdit.CloneAsset, TextureFactory) and rewrites some (instance tables, manifest)
        if (work.Parts.Count < target.Parts.Count) throw new InvalidDataException("the bundle copy has fewer parts than the open bundle");
        for (int i = 0; i < target.Parts.Count; i++)
            if (target.Parts[i].Section != work.Parts[i].Section) throw new InvalidDataException($"part {i + 1} changed its section in the bundle copy");
        var m = new CaffMerge(target);
        var parts = new List<CaffPart>(work.Parts.Count);
        for (int i = 0; i < work.Parts.Count; i++)
        {
            var w = work.Parts[i];
            if (i >= target.Parts.Count) { parts.Add(w); m.Added++; continue; }
            var s = target.Parts[i];
            bool sameData = s.Data.AsSpan().SequenceEqual(w.Data);
            if (sameData && s.Symbol == w.Symbol && s.AlignLog2 == w.AlignLog2) { m.Kept++; parts.Add(s); continue; }
            m._changed.Add((s, s.Symbol, s.AlignLog2, s.OriginalOffset, s.Size, s.Data));
            s.Symbol = w.Symbol; s.AlignLog2 = w.AlignLog2;   // e.g. the manifest's parts: renumbered, same bytes
            if (!sameData) { s.Data = w.Data; s.Size = w.Size; s.OriginalOffset = w.OriginalOffset; }
            parts.Add(s);
        }
        target.Header = work.Header; target.Version = work.Version; target.Sections = work.Sections; target.Symbols = work.Symbols;
        target.ExtraName = work.ExtraName; target.Parts = parts; target.Relocs = work.Relocs; target.PoolParts = work.PoolParts;
        target.PoolRecords = work.PoolRecords;
        return m;
    }

    public void Undo()
    {
        foreach (var (p, sym, al, off, size, data) in _changed) { p.Symbol = sym; p.AlignLog2 = al; p.OriginalOffset = off; p.Size = size; p.Data = data; }
        var b = _before; var t = _target;
        t.Header = b.Header; t.Version = b.Version; t.Sections = b.Sections; t.Symbols = b.Symbols; t.ExtraName = b.ExtraName;
        t.Parts = b.Parts; t.Relocs = b.Relocs; t.PoolParts = b.PoolParts; t.PoolRecords = b.PoolRecords;
    }
}

public sealed class TransplantModel
{
    /// <summary>Display name of the source model asset (aid_model_…).</summary>
    public string Name = "";
    /// <summary>Checksum of the source asset's parts (an identical asset in the target is reused).</summary>
    public ulong Signature;
    /// <summary>The model's own collision asset (aid_havok_ of the same name), when it has one.</summary>
    public string? Havok;
    public ulong HavokSignature;
    public readonly List<TransplantPart> Parts = new();
    /// <summary>The model's own collision (model space).</summary>
    public List<Vector3> CollisionPositions = new();
    public List<int> CollisionTriangles = new();
    public int Triangles => Parts.Sum(p => p.Mesh.Triangles.Count / 3);

    /// <summary>A checksum of what the rebuilt model is made from (geometry, materials, textures, collision): the same
    /// copy pasted again finds the model it was rebuilt as (<see cref="TransplantMemory"/>).</summary>
    public ulong ContentKey(TransplantPackage pkg)
    {
        ulong h = 1469598103934665603UL;
        void Bytes<T>(List<T>? l) where T : struct
        {
            if (l == null) { h = (h ^ 0xFF) * 1099511628211UL; return; }
            h = FastHash.Of(System.Runtime.InteropServices.MemoryMarshal.AsBytes(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(l)), (h ^ (ulong)l.Count) * 1099511628211UL);
        }
        void Text(string? t) { h = FastHash.Of(System.Text.Encoding.UTF8.GetBytes((t ?? "-") + "|" + (t != null && pkg.Textures.TryGetValue(t, out var tx) ? tx.Signature : 0)), h); }
        foreach (var p in Parts)
        {
            Text(p.Kind.ToString()); Text(p.Colour); Text(p.Mask); Text(p.Ao);
            Bytes(p.Mesh.Positions); Bytes(p.Mesh.Normals); Bytes(p.Mesh.UVs); Bytes(p.Mesh.UVs2); Bytes(p.Mesh.Triangles);
        }
        Bytes(CollisionPositions); Bytes(CollisionTriangles);
        return h;
    }
}

public sealed class TransplantPart
{
    public enum Kinds { Opaque, Cutout, Blend }
    public Kinds Kind;
    /// <summary>Texture names (display names of aid_texture_ assets) of the colour, the cut-out mask and the
    /// ambient-occlusion / lightmap (second UV set); null when the material has none.</summary>
    public string? Colour, Mask, Ao;
    public ImportMesh Mesh = new();
}

public sealed class TransplantTexture
{
    public string Name = "";
    public ulong Signature;
    /// <summary>The texture's assets in the source bundle (the stem, or the game's stem+"mip" / stem+"top" entries) with
    /// their checksums: when the target has all of them with the same bytes, the target's own texture is used.</summary>
    public readonly Dictionary<string, ulong> SourceAssets = new();
    public byte[] Rgba = Array.Empty<byte>();
    public int W, H;
    public XenosFormat Format = XenosFormat.DXT1;
}

/// <summary>
/// What earlier pastes into a workspace rebuilt: per world bundle, a copied model (by its content key) or texture (by name
/// and checksum) and the asset(s) it became there, with their checksums. A later paste of the same thing reuses those
/// assets while they are still there unchanged (after an undo they are not, and the paste builds them again). Kept in the
/// workspace's cache folder: losing it only means the next paste builds new copies.
/// </summary>
public sealed class TransplantMemory
{
    public sealed class Entry
    {
        public List<string> Names { get; set; } = new();
        public List<ulong> Signatures { get; set; } = new();
        public string? Havok { get; set; }
    }
    public Dictionary<string, Entry> Entries { get; set; } = new();

    public static TransplantMemory Load(string path)
    {
        try { if (File.Exists(path)) return System.Text.Json.JsonSerializer.Deserialize<TransplantMemory>(File.ReadAllText(path)) ?? new(); }
        catch { }
        return new();
    }

    public void Save(string path)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(this)); }
        catch { }
    }

    /// <summary>The assets an entry names, when every one is still in <paramref name="caff"/> with its checksum.</summary>
    public Entry? Valid(CaffFile caff, string key)
    {
        if (!Entries.TryGetValue(key, out var e) || e.Names.Count == 0 || e.Names.Count != e.Signatures.Count) return null;
        for (int i = 0; i < e.Names.Count; i++)
        {
            int s = AssetTransplant.Find(caff, e.Names[i]);
            if (s == 0 || AssetTransplant.Signature(caff, s) != e.Signatures[i]) return null;
        }
        if (e.Havok != null && AssetTransplant.Find(caff, e.Havok) == 0) return null;
        return e;
    }
}

/// <summary>
/// Adds the models of a <see cref="TransplantPackage"/> to a world bundle: an asset that is already there with identical
/// bytes (same name, same checksum) is reused; otherwise the model is rebuilt the way the VMF importer builds imported
/// models (clone of a town template + geometry per material + textures created from the decoded pixels), under the
/// source name or, when that name is taken by different content, "_copyN". Textures likewise (rebuilt textures are
/// DXT-encoded again from the decoded pixels, power-of-two size). What an earlier paste rebuilt is reused
/// (<see cref="TransplantMemory"/>). The rebuilt models' unreferenced template vertex memory is dropped
/// (<see cref="GpuCompactor"/>). Needs Showdown Town's template models in the target bundle (any Showdown Town workspace
/// has them); elsewhere the paste is refused with that reason (identical models are still reused anywhere).
/// </summary>
public static class AssetTransplant
{
    public const string WhiteTexture = "aid_texture_banjox_vmf_white", SpecTexture = "aid_texture_banjox_vmf_spec";
    const string TemplateColour = "aid_texture_banjox_shared_materials_metal_brass1_colour_0x00b19ac5";
    const string CutoutColour = "aid_texture_banjox_shared_nuttyacres_grill1_colour_0x088078a5";
    const string CutoutMask = "aid_texture_banjox_shared_nuttyacres_grill1_transparency_0x02939385";
    const string BlendColour = "aid_texture_banjox_shared_showdowntown_crack2_colourandtrans_0x086d7b15";
    const string FlatNormal = "aid_texture_banjox_shared_nuttyacres_plainnormal_0x0c670e45";

    /// <summary>A checksum of every part of an asset (the same test the world's model cache uses).</summary>
    public static ulong Signature(CaffFile caff, int sym)
    {
        ulong h = 1469598103934665603UL;
        foreach (var p in caff.PartsOf(sym)) h = FastHash.Of(p.Data, (h ^ (ulong)p.Data.Length) * 1099511628211UL);
        return h;
    }

    /// <summary>A checksum of a whole bundle in memory (every part, the symbol and relocation counts): tells whether
    /// anything changed it while a paste was being built on a copy.</summary>
    public static ulong ContentHash(CaffFile caff)
    {
        ulong h = ((ulong)caff.Symbols.Count << 32) ^ (ulong)caff.Parts.Count ^ ((ulong)caff.Relocs.Count << 16);
        foreach (var p in caff.Parts) h = FastHash.Of(p.Data, (h ^ (ulong)p.Data.Length) * 1099511628211UL);
        return h;
    }

    public static int Find(CaffFile caff, string name) => caff.Symbols.FindIndex(s => AssetIds.DisplayName(s).Equals(name, StringComparison.OrdinalIgnoreCase)) + 1;

    /// <summary>Why the package can't go into this bundle (missing templates), or null.</summary>
    public static string? CannotPlace(CaffFile caff, TransplantPackage pkg)
    {
        bool needBuild = pkg.Models.Values.Any(m => !Identical(caff, m));
        if (!needBuild) return null;
        foreach (var t in new[] { SourceEngine.VmfImporter.Template, SourceEngine.VmfImporter.CutoutTemplate, SourceEngine.VmfImporter.BlendTemplate })
            if (Find(caff, t) == 0) return $"this world has no {t} (the model the copies are rebuilt from); pasting copied models into other worlds works in Showdown Town";
        return null;
    }

    public static bool Identical(CaffFile caff, TransplantModel m) { int s = Find(caff, m.Name); return s > 0 && Signature(caff, s) == m.Signature; }

    /// <summary>Largest vertex count of one rebuilt part (a template vertex buffer holds at most 60,000).</summary>
    public const int MaxPartVertices = 50000;
    /// <summary>The blend template stores half-float positions: ModelImporter refuses a part reaching 256 units from the
    /// model's origin, so such parts are cut out instead (as the VMF importer does).</summary>
    const float MaxBlendReach = 250f;
    public const string MaskSuffix = "_mask";

    /// <summary>
    /// Makes packed parts buildable on the VMF templates: a blend part reaching too far from the origin becomes a cut-out;
    /// a cut-out without a mask texture gets one made from its colour's alpha (otherwise the template's grill pattern would
    /// cut it); see-through parts drop the second UV set (their templates have no lightmap slot) and are cut at the
    /// texture's repeats (their samplers clamp: VmfImporter.TileSplit); parts over <see cref="MaxPartVertices"/> are split.
    /// Returns notes.
    /// </summary>
    public static List<string> Finish(TransplantPackage pkg, TransplantModel m)
    {
        var notes = new List<string>();
        var done = new List<TransplantPart>();
        foreach (var p in m.Parts)
        {
            var mesh = p.Mesh;
            if (mesh.Triangles.Count == 0) continue;
            if (p.Kind == TransplantPart.Kinds.Blend && mesh.Positions.Max(v => MathF.Max(MathF.Abs(v.X), MathF.Max(MathF.Abs(v.Y), MathF.Abs(v.Z)))) >= MaxBlendReach)
            {
                p.Kind = TransplantPart.Kinds.Cutout; p.Mask = null;
                notes.Add($"{m.Name}: a blended material reaches {MaxBlendReach:0} units or more from the model's origin; cut out instead");
            }
            if (p.Kind == TransplantPart.Kinds.Cutout && p.Mask == null && (p.Mask = AlphaMask(pkg, p.Colour)) == null)
            {
                p.Kind = TransplantPart.Kinds.Opaque;
                notes.Add($"{m.Name}: a cut-out material without a readable texture is pasted solid");
            }
            if (p.Kind != TransplantPart.Kinds.Opaque)
            {
                p.Ao = null; mesh.UVs2 = null;
                if (mesh.UVs != null && mesh.UVs.Any(t => t.X < -0.01f || t.Y < -0.01f || t.X > 1.01f || t.Y > 1.01f)) TileSplitMesh(mesh);
            }
            done.AddRange(Split(p));
        }
        m.Parts.Clear(); m.Parts.AddRange(done);
        return notes;
    }

    /// <summary>A cut-out mask from the colour texture's alpha (the cut-out template reads its mask's blue channel), added
    /// to the package as colour + "_mask"; null without the colour texture.</summary>
    static string? AlphaMask(TransplantPackage pkg, string? colour)
    {
        if (colour == null || !pkg.Textures.TryGetValue(colour, out var c)) return null;
        string name = colour + MaskSuffix;
        if (!pkg.Textures.ContainsKey(name))
        {
            var px = new byte[c.Rgba.Length];
            for (int i = 0; i + 3 < px.Length; i += 4) { px[i] = px[i + 1] = px[i + 2] = c.Rgba[i + 3]; px[i + 3] = 255; }
            pkg.Textures[name] = new TransplantTexture { Name = name, Signature = FastHash.Of(px), Rgba = px, W = c.W, H = c.H };
        }
        return name;
    }

    /// <summary>Cuts every triangle at the texture's repeat boundaries, each piece mapped onto 0..1 (no index sharing).</summary>
    static void TileSplitMesh(ImportMesh mesh)
    {
        var tris = new List<(Vector3 P, Vector3 N, Vector2 T)>();
        var nrm = mesh.Normals; var uv = mesh.UVs!;
        for (int t = 0; t + 2 < mesh.Triangles.Count; t += 3)
        {
            int a = mesh.Triangles[t], b = mesh.Triangles[t + 1], c = mesh.Triangles[t + 2];
            (Vector3, Vector3, Vector2) V(int i) => (mesh.Positions[i], nrm != null ? nrm[i] : Vector3.UnitY, uv[i]);
            SourceEngine.VmfImporter.TileSplit(V(a), V(b), V(c), tris);
        }
        // shared corners again (a triangle that needed no cut keeps its vertices; only cut edges add new ones)
        var at = new Dictionary<(Vector3, Vector3, Vector2), int>();
        mesh.Positions = new(); mesh.Normals = new(); mesh.UVs = new(); mesh.Triangles = new(tris.Count);
        foreach (var v in tris)
        {
            if (!at.TryGetValue(v, out int i))
            {
                at[v] = i = mesh.Positions.Count;
                mesh.Positions.Add(v.P); mesh.Normals.Add(v.N); mesh.UVs.Add(v.T);
            }
            mesh.Triangles.Add(i);
        }
    }

    /// <summary>The part, or pieces of it with at most <see cref="MaxPartVertices"/> vertices each.</summary>
    static IEnumerable<TransplantPart> Split(TransplantPart p)
    {
        var mesh = p.Mesh;
        if (mesh.Positions.Count <= MaxPartVertices) { yield return p; yield break; }
        TransplantPart? cur = null; Dictionary<int, int>? map = null;
        for (int t = 0; t + 2 < mesh.Triangles.Count; t += 3)
        {
            if (cur == null || map!.Count + 3 > MaxPartVertices)
            {
                if (cur != null) yield return cur;
                cur = new TransplantPart { Kind = p.Kind, Colour = p.Colour, Mask = p.Mask, Ao = p.Ao,
                    Mesh = new ImportMesh { Name = mesh.Name, Normals = mesh.Normals != null ? new() : null, UVs = mesh.UVs != null ? new() : null, UVs2 = mesh.UVs2 != null ? new() : null } };
                map = new Dictionary<int, int>();
            }
            for (int k = 0; k < 3; k++)
            {
                int i = mesh.Triangles[t + k];
                if (!map!.TryGetValue(i, out int j))
                {
                    map[i] = j = cur.Mesh.Positions.Count;
                    cur.Mesh.Positions.Add(mesh.Positions[i]);
                    cur.Mesh.Normals?.Add(mesh.Normals![i]); cur.Mesh.UVs?.Add(mesh.UVs![i]); cur.Mesh.UVs2?.Add(mesh.UVs2![i]);
                }
                cur.Mesh.Triangles.Add(j);
            }
        }
        if (cur != null) yield return cur;
    }

    public sealed record Placed(uint ModelId, uint? HavokId, string ModelName, bool Reused);

    /// <summary>
    /// Makes every model of the package available in <paramref name="caff"/>; returns, per package key (source model name), the models
    /// to place (one per material for a rebuilt model; the first one carries the collision).
    /// </summary>
    public static Dictionary<string, List<Placed>> Apply(CaffFile caff, TransplantPackage pkg, List<string> notes, TransplantMemory? memory = null, uint bundle = 0,
        Action<string, double>? progress = null)
    {
        var res = new Dictionary<string, List<Placed>>();
        var texNames = new Dictionary<string, string>();   // source texture name -> name in this bundle
        memory ??= new TransplantMemory();
        double done01 = 0;
        string Tex(string? src)
        {
            if (src == null || !pkg.Textures.TryGetValue(src, out var t)) return WhiteTexture;
            if (texNames.TryGetValue(src, out var have)) return have;
            // the same texture is here: every source asset of it (the stem, or the game's mip / top entries) with the same bytes
            if (t.SourceAssets.Count > 0 && t.SourceAssets.All(kv => Find(caff, kv.Key) is int s1 && s1 > 0 && Signature(caff, s1) == kv.Value))
            { notes.Add($"texture {t.Name}: already here (same bytes)"); return texNames[src] = t.Name; }
            string key = $"{bundle:x6}|texture|{t.Name}|{t.Signature:x16}";
            if (memory.Valid(caff, key) is { } prev) { notes.Add($"texture {t.Name}: already pasted here as {prev.Names[0]}: reused"); return texNames[src] = prev.Names[0]; }
            // a name in use here (also as stem+"mip" / stem+"top", which a retarget to the stem would pick) gets "_copyN"
            bool taken = TextureTaken(caff, t.Name);
            string name = taken ? UniqueTexture(caff, t.Name) : t.Name;
            progress?.Invoke($"Pasting: texture {name}", done01);
            int w = Pow2(t.W), h = Pow2(t.H);
            TextureFactory.Create(caff, name, t.Rgba, t.W, t.H, w, h, t.Format);
            notes.Add(taken ? $"texture {t.Name}: a different texture has that name here; added as {name} ({w}×{h}, {t.Format})" : $"texture {name}: added ({w}×{h}, {t.Format})");
            memory.Entries[key] = new TransplantMemory.Entry { Names = { name }, Signatures = { Signature(caff, Find(caff, name)) } };
            return texNames[src] = name;
        }
        int nModels = Math.Max(1, pkg.Models.Count), mi = 0;
        foreach (var (key, m) in pkg.Models)
        {
            done01 = (double)mi++ / nModels;
            int s = Find(caff, m.Name);
            if (s > 0 && Signature(caff, s) == m.Signature)
            {
                uint? hid = null;
                if (m.Havok != null && Find(caff, m.Havok) is int hs && hs > 0) hid = AssetIds.IdOf(m.Havok);
                res[key] = new() { new Placed(AssetIds.IdOf(m.Name)!.Value, hid, m.Name, true) };
                notes.Add($"{m.Name}: already in this world (same bytes): reused");
                continue;
            }
            string mkey = $"{bundle:x6}|model|{m.Name}|{m.ContentKey(pkg):x16}";
            if (memory.Valid(caff, mkey) is { } prev)
            {
                res[key] = prev.Names.Select((n, i) => new Placed(AssetIds.IdOf(n)!.Value, i == 0 && prev.Havok != null ? AssetIds.IdOf(prev.Havok) : null, n, true)).ToList();
                notes.Add($"{m.Name}: already pasted here as {string.Join(", ", prev.Names)}: reused");
                continue;
            }
            EnsureHelpers(caff);
            var list = new List<Placed>();
            string stem = AssetIds.DisplayName(m.Name).Replace("aid_model_banjox_", "");
            if (s > 0) notes.Add($"{m.Name}: a different model has that name here; the copy gets a new name");
            bool first = true;
            int k = 0;
            foreach (var part in m.Parts)
            {
                string baseName = m.Parts.Count == 1 ? stem : $"{stem}_m{k}";
                k++;
                string name = UniqueModel(caff, baseName);
                progress?.Invoke($"Pasting: model {name} ({mi} of {nModels})", done01);
                var (template, colourSlot) = part.Kind switch
                {
                    TransplantPart.Kinds.Cutout => (SourceEngine.VmfImporter.CutoutTemplate, CutoutColour),
                    TransplantPart.Kinds.Blend => (SourceEngine.VmfImporter.BlendTemplate, BlendColour),
                    _ => (SourceEngine.VmfImporter.Template, TemplateColour),
                };
                string colour = Tex(part.Colour);
                var o = new ModelFactory.Options
                {
                    Template = template, Name = name, Meshes = new List<ImportMesh> { part.Mesh }, SingleMaterial = colour,
                    NeutralAo = part.Ao != null ? Tex(part.Ao) : (part.Kind == TransplantPart.Kinds.Opaque ? WhiteTexture : null),
                    Specular = SpecTexture, FlatNormal = FlatNormal, CullDistance = 1e6f, TrimHidden = part.Kind != TransplantPart.Kinds.Opaque,
                    Collision = first && m.CollisionTriangles.Count > 0 ? "mesh" : "none",
                };
                o.Retarget[colourSlot] = colour;   // as SceneBuilder does for the VMF importer's models
                if (part.Kind == TransplantPart.Kinds.Cutout && part.Mask != null) o.Retarget[CutoutMask] = Tex(part.Mask);
                if (o.Collision == "mesh")
                {
                    var cm = new ImportMesh { Name = "collision", Positions = m.CollisionPositions.ToList(), Triangles = m.CollisionTriangles.ToList() };
                    o.CollisionMeshes = new List<ImportMesh> { cm };
                }
                var r = ModelFactory.Create(caff, o);
                // drop the template's unreferenced vertex memory, as the scene builder does after an import (a rebuilt rig
                // took 15 MB of .gpu instead of 2 MB without it)
                try { GpuCompactor.Compact(caff, Find(caff, ModelFactory.ModelName(name)), out _); } catch { }
                list.Add(new Placed(r.ModelId, o.Collision == "mesh" ? r.HavokId : null, ModelFactory.ModelName(name), false));
                notes.Add($"{m.Name} → {ModelFactory.ModelName(name)}: {part.Mesh.Triangles.Count / 3} triangles, {part.Kind}, colour {colour}{(part.Ao != null ? ", lightmap " + Tex(part.Ao) : "")}{(o.Collision == "mesh" ? $", collision {m.CollisionTriangles.Count / 3} triangles" : "")}");
                first = false;
            }
            res[key] = list;
            string? havok = list.FirstOrDefault(p => p.HavokId != null) is { HavokId: uint hid2 } ? caff.Symbols.Select(AssetIds.DisplayName).FirstOrDefault(n => AssetIds.IdOf(n) == hid2) : null;
            memory.Entries[mkey] = new TransplantMemory.Entry { Names = list.Select(p => p.ModelName).ToList(), Signatures = list.Select(p => Signature(caff, Find(caff, p.ModelName))).ToList(), Havok = havok };
        }
        return res;
    }

    /// <summary>The white AO and the specular texture the rebuilt models use (as the VMF importer makes them).</summary>
    static void EnsureHelpers(CaffFile caff)
    {
        void Solid(string name, byte v)
        {
            if (Find(caff, name) > 0) return;
            var px = new byte[16 * 16 * 4];
            for (int i = 0; i < px.Length; i += 4) { px[i] = px[i + 1] = px[i + 2] = v; px[i + 3] = 255; }
            TextureFactory.Create(caff, name, px, 16, 16, 16, 16);
        }
        Solid(WhiteTexture, 255); Solid(SpecTexture, 64);
    }

    static int Pow2(int v) { int p = 4; while (p < v && p < 4096) p <<= 1; return p; }

    static bool TextureTaken(CaffFile caff, string stem) => Find(caff, stem) > 0 || Find(caff, stem + "mip") > 0 || Find(caff, stem + "top") > 0;

    static string UniqueTexture(CaffFile caff, string stem)
    {
        for (int i = 2; ; i++) { var n = $"{stem}_copy{i}"; if (!TextureTaken(caff, n)) return n; }
    }

    static string UniqueModel(CaffFile caff, string stem)
    {
        if (Find(caff, ModelFactory.ModelName(stem)) == 0 && Find(caff, ModelFactory.HavokName(stem)) == 0) return stem;
        for (int i = 2; ; i++) { var n = $"{stem}_copy{i}"; if (Find(caff, ModelFactory.ModelName(n)) == 0 && Find(caff, ModelFactory.HavokName(n)) == 0) return n; }
    }
}
