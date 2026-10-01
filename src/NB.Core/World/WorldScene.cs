using System.Numerics;
using NB.Core.Formats;
using NB.Core.IO;
using NB.Core.Models;
using NB.Core.Project;
using NB.Core.Textures;

namespace NB.Core.World;

public enum SceneObjectKind { Terrain, Scenery, Marker }

/// <summary>An editable object in a loaded world.</summary>
public sealed class SceneObject
{
    public int Id;
    public SceneObjectKind Kind;
    public string Name = "";
    public string ModelName = "";          // reference model asset (scenery) or background model (terrain)
    public ModelAsset? Model;              // shared geometry
    public Matrix4x4 Transform = Matrix4x4.Identity;
    public Matrix4x4 OriginalTransform = Matrix4x4.Identity;
    public SceneInstance? Instance;        // backing record for scenery
    public MarkerAsset? MarkerSet;         // backing asset for markers
    public MarkerRecord? Marker;
    public bool Visible = true;
    public Vector3 BoundsMin, BoundsMax;   // model space (including nested reference models)
    /// <summary>Models placed inside this object's reference model (composite buildings), flattened, with transforms
    /// relative to this object. Drawn and picked together with it; they move with it.</summary>
    public List<(ModelAsset Model, Matrix4x4 Local)> Children = new();
    public bool Dirty => Transform != OriginalTransform || (Marker is { Type: 22 } m && m.Link != m.SavedLink);
}

/// <summary>
/// A world bundle opened for editing: the background (level) model's own draws as terrain, and every
/// scenery instance (chunk 12) with its reference model. Transform edits are written back into the
/// background model's .data (chunk-12 matrix + position, and the matching chunk-2 node), in place.
/// </summary>
public sealed class WorldScene
{
    public readonly Workspace Workspace;
    public readonly uint Bundle;
    public readonly CaffFile Caff;
    public readonly ModelAsset Background;
    public readonly List<SceneObject> Objects = new();
    public readonly Dictionary<string, ModelAsset> Models = new();
    public readonly List<string> Log = new();
    readonly Dictionary<int, List<(int, int)>> _relocs;

    /// <summary>Act bundles whose markers were loaded in addition to the world bundle's own.</summary>
    public readonly List<uint> MarkerBundles = new();

    public WorldScene(Workspace ws, uint bundle, string backgroundModel, IProgress<(string, double)>? progress = null, IEnumerable<uint>? actBundles = null)
    {
        Workspace = ws; Bundle = bundle;
        progress?.Report(("loading bundle", 0));
        Caff = ws.LoadResident(bundle);
        _relocs = AssetView.BuildRelocIndex(Caff);
        int bgSym = Caff.Symbols.IndexOf(backgroundModel) + 1;
        if (bgSym == 0) throw new InvalidDataException($"{backgroundModel} not in bundle {bundle:x6}");
        progress?.Report(("parsing terrain", 0.05));
        Background = ModelAsset.Parse(Caff, bgSym, _relocs);
        foreach (var w in Background.Warnings.Take(20)) Log.Add("terrain: " + w);

        int id = 0;
        var terrain = new SceneObject { Id = id++, Kind = SceneObjectKind.Terrain, Name = "Terrain (" + backgroundModel + ")", ModelName = backgroundModel, Model = Background };
        ComputeBounds(terrain);
        Objects.Add(terrain);

        // reference models by asset id (type 0x04)
        var modelById = new Dictionary<uint, int>();
        for (int s = 1; s <= Caff.Symbols.Count; s++)
            if (Caff.Symbols[s - 1].StartsWith("aid_model_") && AssetIds.IdOf(Caff.Symbols[s - 1]) is uint mid) modelById[mid] = s;

        int n = 0;
        foreach (var inst in Background.Instances)
        {
            if (n++ % 25 == 0) progress?.Report(($"scenery {n}/{Background.Instances.Count}", 0.1 + 0.9 * n / Math.Max(1, Background.Instances.Count)));
            var obj = new SceneObject
            {
                Id = id++, Kind = SceneObjectKind.Scenery, Name = ModelAsset.CleanInstanceName(inst.Name), Instance = inst,
                Transform = inst.World, OriginalTransform = inst.World,
            };
            uint refId = inst.RefModel >= 0 && inst.RefModel < Background.ReferenceIds.Count ? (uint)Background.ReferenceIds[inst.RefModel] : 0;
            if (modelById.TryGetValue(refId, out int msym))
            {
                obj.ModelName = Caff.Symbols[msym - 1];
                obj.Model = GetModel(msym);
                obj.Children = NestedModels(obj.Model, modelById);
            }
            else Log.Add($"{obj.Name}: reference model id {refId:X8} not found in this bundle (drawn as a marker)");
            ComputeBounds(obj);
            Objects.Add(obj);
        }
        // markers (actors, pickups, spawn points, paths...) from every marker asset in this bundle and the act bundles
        var markerSources = new List<(uint Bundle, CaffFile Caff)> { (bundle, Caff) };
        foreach (var ab in actBundles ?? Enumerable.Empty<uint>())
            try { markerSources.Add((ab, ws.LoadResident(ab))); MarkerBundles.Add(ab); }
            catch (Exception e) { Log.Add($"act bundle {ab:x6}: {e.Message}"); }
        foreach (var (_, mc) in markerSources)
            for (int s = 1; s <= mc.Symbols.Count; s++)
                if (AssetIds.IdOf(mc.Symbols[s - 1]) is uint aid) NameById.TryAdd(aid, AssetIds.DisplayName(mc.Symbols[s - 1]));
        foreach (var (mb, mc) in markerSources)
        for (int s = 1; s <= mc.Symbols.Count; s++)
        {
            if (!mc.Symbols[s - 1].StartsWith("aid_marker_")) continue;
            try
            {
                var ma = MarkerAsset.Parse(mc, s);
                ma.Bundle = mb; ma.Caff = mc;
                Markers.Add(ma);
                foreach (var r in ma.Records) r.AssetNames = r.AssetIds.Select(x => NameById.GetValueOrDefault(x, "?")).ToList();
                foreach (var r in ma.Records)
                {
                    var m = r.Matrix;
                    Objects.Add(new SceneObject
                    {
                        Id = id++, Kind = SceneObjectKind.Marker, MarkerSet = ma, Marker = r, Transform = m, OriginalTransform = m,
                        Name = $"{MarkerRecord.TypeName(r.Type)} #{r.Index}" + (r.AssetNames.FirstOrDefault(n => n.StartsWith("aid_objparams_")) is string op ? " " + op.Replace("aid_objparams_banjox_", "") : r.Strings.Count > 0 ? " " + r.Strings[0] : ""),
                        ModelName = AssetIds.DisplayName(ma.Name),
                        BoundsMin = new Vector3(-1), BoundsMax = new Vector3(1),
                    });
                }
            }
            catch (Exception e) { Log.Add($"{mc.Symbols[s - 1]}: {e.Message}"); }
        }
        progress?.Report(("done", 1));
    }

    /// <summary>Collision per model name (aid_model_X ↔ aid_havok_X), in model space. Filled by <see cref="LoadCollision"/>.</summary>
    public Dictionary<string, List<NB.Core.Havok.CollisionMesh>>? CollisionByModel;

    /// <summary>Decodes the Havok collision of the terrain and of every reference model present in the bundle.</summary>
    public (int Models, int Triangles, List<string> Notes) LoadCollision()
    {
        if (CollisionByModel != null) return (CollisionByModel.Count, CollisionByModel.Values.Sum(l => l.Sum(m => m.Triangles.Count / 3)), new());
        var result = new Dictionary<string, List<NB.Core.Havok.CollisionMesh>>();
        var notes = new List<string>();
        var bySym = new Dictionary<string, int>();
        for (int s = 1; s <= Caff.Symbols.Count; s++) bySym[AssetIds.DisplayName(Caff.Symbols[s - 1])] = s;
        foreach (var model in Objects.Where(o => o.Model != null).Select(o => o.ModelName).Distinct())
        {
            var hk = "aid_havok_" + AssetIds.DisplayName(model)["aid_model_".Length..];
            if (!bySym.TryGetValue(hk, out int sym)) continue;
            try
            {
                var d = new AssetView(Caff, sym, _relocs).Data(".data");
                if (!NB.Core.Havok.HkPackfile.IsPackfileAsset(d)) continue;
                var hc = NB.Core.Havok.HkCollision.ExtractAsset(d);
                result[model] = hc.Meshes;
                notes.AddRange(hc.Notes.Select(n => $"{hk}: {n}"));
            }
            catch (Exception e) { notes.Add($"{hk}: {e.Message}"); }
        }
        CollisionByModel = result;
        return (result.Count, result.Values.Sum(l => l.Sum(m => m.Triangles.Count / 3)), notes);
    }

    public readonly List<MarkerAsset> Markers = new();
    /// <summary>Asset id → display name for every asset in the bundle.</summary>
    public readonly Dictionary<uint, string> NameById = new();

    readonly Dictionary<ModelAsset, List<(ModelAsset, Matrix4x4)>> _nested = new();

    /// <summary>Flattens the reference-model instances inside <paramref name="m"/> (recursively, up to 6 levels) into
    /// (model, transform relative to m). Instance matrices inside a reference model are in that model's space.</summary>
    List<(ModelAsset Model, Matrix4x4 Local)> NestedModels(ModelAsset m, Dictionary<uint, int> modelById, int depth = 0)
    {
        if (_nested.TryGetValue(m, out var hit)) return hit;
        var list = new List<(ModelAsset, Matrix4x4)>();
        _nested[m] = list;   // guards against cycles
        if (depth < 6)
            foreach (var inst in m.Instances)
            {
                uint rid = inst.RefModel >= 0 && inst.RefModel < m.ReferenceIds.Count ? (uint)m.ReferenceIds[inst.RefModel] : 0;
                if (!modelById.TryGetValue(rid, out int cs)) { Log.Add($"{AssetIds.DisplayName(m.View.Name)}: nested model {rid:X8} not in this bundle"); continue; }
                var child = GetModel(cs);
                if (child == m) continue;
                list.Add((child, inst.World));
                foreach (var (gm, gl) in NestedModels(child, modelById, depth + 1)) list.Add((gm, gl * inst.World));
            }
        return list;
    }

    ModelAsset GetModel(int sym)
    {
        var name = Caff.Symbols[sym - 1];
        if (Models.TryGetValue(name, out var m)) return m;
        m = ModelAsset.Parse(Caff, sym, _relocs);
        foreach (var w in m.Warnings.Take(5)) Log.Add($"{AssetIds.DisplayName(name)}: {w}");
        Models[name] = m;
        return m;
    }

    static void ComputeBounds(SceneObject o)
    {
        if (o.Kind == SceneObjectKind.Marker) return;
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        void Add(ModelAsset m, Matrix4x4 xf)
        {
            foreach (var d in m.Draws)
                foreach (int i in d.Indices)   // only vertices that are drawn (imported models keep unused old vertices)
                    if (i < d.Positions.Length) { var p = Vector3.Transform(d.Positions[i], xf); mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
        }
        if (o.Model != null) Add(o.Model, Matrix4x4.Identity);
        foreach (var (cm, cl) in o.Children) Add(cm, cl);
        if (mn.X > mx.X) { mn = new Vector3(-1); mx = new Vector3(1); }
        o.BoundsMin = mn; o.BoundsMax = mx;
    }

    // ---------------------------------------------------------------- textures

    readonly Dictionary<string, (byte[] Rgba, int W, int H)?> _texCache = new();

    /// <summary>Decodes a texture referenced by a draw (uses the resident "mip" asset: half resolution).</summary>
    /// <summary>Workspace-wide texture lookup (world bundle, other resident bundles, stream archives). When null, only
    /// the world bundle is searched.</summary>
    public NB.Core.Textures.TextureResolver? Textures;

    /// <summary>Distinct diffuse textures used by the world's models (what the viewport draws).</summary>
    public IEnumerable<string> DiffuseTextureNames() =>
        Objects.Where(o => o.Model != null).SelectMany(o => o.Children.Select(c => c.Model).Prepend(o.Model!)).Distinct().SelectMany(m => m.Draws)
            .SelectMany(d => { var l = NB.Core.Models.ObjExporter.MaterialLayers(d); return new[] { l.Base, l.Overlay, l.Mask }; }).Where(n => n != null).Select(n => n!).Distinct();

    public (byte[] Rgba, int W, int H)? LoadTexture(string name)
    {
        if (Textures != null) return Textures.Load(name);
        if (_texCache.TryGetValue(name, out var t)) return t;
        t = null;
        try
        {
            string stem = name.EndsWith("top") || name.EndsWith("mip") ? name[..^3] : name;
            foreach (var cand in new[] { stem + "mip", stem, stem + "top" })
            {
                int s = Caff.Symbols.FindIndex(x => x.Contains(cand + "\\") || AssetIds.DisplayName(x) == cand) + 1;
                if (s == 0) continue;
                var parts = Caff.PartsOf(s).ToList();
                var cpu = parts.FirstOrDefault(p => Caff.SectionOf(p).Name == ".data");
                var gpu = parts.FirstOrDefault(p => Caff.SectionOf(p).Name == ".texturegpu");
                if (cpu == null || gpu == null || !TextureHeader.IsTexture(cpu.Data)) continue;
                var ta = new TextureAsset(cpu.Data, gpu.Data);
                t = ta.Decode(0);
                break;
            }
        }
        catch (Exception e) { Log.Add($"texture {name}: {e.Message}"); }
        _texCache[name] = t;
        return t;
    }

    // ---------------------------------------------------------------- editing

    /// <summary>Writes every changed scenery transform into the background model's .data part.</summary>
    /// <summary>Bundles changed by the last <see cref="ApplyEdits"/> (the world bundle and/or act bundles).</summary>
    public readonly HashSet<uint> DirtyBundles = new();

    public int ApplyEdits()
    {
        var part = Background.View.Part(Background.View.PartId(".data"));
        var d = part.Data;
        int changed = 0;
        foreach (var o in Objects)
        {
            if (o.Kind == SceneObjectKind.Marker && o.Marker != null && o.MarkerSet != null && o.Dirty)
            {
                if (o.Transform != o.OriginalTransform)
                {
                    Matrix4x4.Decompose(o.Transform, out var sc, out var q, out var t);
                    o.Marker.Position = t;
                    o.Marker.Rotation = MathUtil.EulerXYZ(Matrix4x4.CreateFromQuaternion(q));
                    o.Marker.Scale = (sc.X + sc.Y + sc.Z) / 3;
                    MarkerAsset.WriteTransform(o.MarkerSet.Caff ?? Caff, o.MarkerSet.Symbol, o.Marker);
                    o.Transform = o.Marker.Matrix; o.OriginalTransform = o.Transform;
                }
                if (o.Marker.Type == 22 && o.Marker.Link != o.Marker.SavedLink) MarkerAsset.WriteLink(o.MarkerSet.Caff ?? Caff, o.MarkerSet.Symbol, o.Marker);
                DirtyBundles.Add(o.MarkerSet.Caff != null ? o.MarkerSet.Bundle : Bundle);
                changed++;
                continue;
            }
            if (o.Kind != SceneObjectKind.Scenery || o.Instance == null || !o.Dirty) continue;
            var m = o.Transform;
            float[] f = { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 };
            for (int k = 0; k < 16; k++) BE.WF32(d, o.Instance.MatrixOffset + 4 * k, f[k]);
            BE.WF32(d, o.Instance.PositionOffset, m.M41); BE.WF32(d, o.Instance.PositionOffset + 4, m.M42); BE.WF32(d, o.Instance.PositionOffset + 8, m.M43);
            if (o.Instance.PlacementNode >= 0 && Background.Chunks.TryGetValue(2, out int c2))
            {
                // chunk-2 node: 3 rows (r0 r1 r2 t) — the transpose of the row-vector rotation plus translation
                int b = c2 + 4 + 68 * o.Instance.PlacementNode + 4;
                float[] rows = { m.M11, m.M21, m.M31, m.M41, m.M12, m.M22, m.M32, m.M42, m.M13, m.M23, m.M33, m.M43 };
                for (int k = 0; k < 12; k++) BE.WF32(d, b + 4 * k, rows[k]);
            }
            o.Instance.World = m;
            o.OriginalTransform = m;
            DirtyBundles.Add(Bundle);
            changed++;
        }
        return changed;
    }

    /// <summary>Applies edits and writes the bundle into the workspace game directory.</summary>
    public int Save()
    {
        var names = Objects.Where(o => o.Dirty).Select(o => o.Name).Take(8).ToList();
        DirtyBundles.Clear();
        int n = ApplyEdits();
        foreach (var b in DirtyBundles)
        {
            var caff = b == Bundle ? Caff : Markers.First(m => m.Bundle == b && m.Caff != null).Caff!;
            Workspace.SaveResident(b, caff, $"moved {n} object(s) (world {Bundle:x6}): {string.Join(", ", names)}{(n > 8 ? ", …" : "")}");
        }
        return n;
    }
}
