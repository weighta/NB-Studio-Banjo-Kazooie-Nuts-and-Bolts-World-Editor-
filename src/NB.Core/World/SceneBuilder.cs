using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using NB.Core.Formats;
using NB.Core.Models;
using NB.Core.Project;
using NB.Core.Textures;

namespace NB.Core.World;

/// <summary>
/// Assembles a custom world from a scene file (JSON) into a world bundle: new textures, new models (from templates),
/// terrain geometry and collision, hiding of the original scenery, new scenery instances and marker moves — all
/// through the tool's own editing APIs (TextureFactory, ModelFactory, ModelImporter, HkCollisionImport,
/// InstanceEditor, MarkerAsset). Used for the Seattle conversion of Showdown Town. File paths in the scene are
/// relative to the scene file. See docs/SCENE_FORMAT.md.
/// </summary>
public static class SceneBuilder
{
    public sealed class Scene
    {
        public string World { get; set; } = "";
        /// <summary>Start from the unmodified original bundle (recommended: the build is then reproducible).</summary>
        public bool FromOriginal { get; set; } = true;
        public Dictionary<string, TemplateDef> Templates { get; set; } = new();
        public List<TextureDef> Textures { get; set; } = new();
        /// <summary>Existing textures whose pixels are replaced (same size/format; resident mip + streamed top).</summary>
        public List<TextureDef> ReplaceTextures { get; set; } = new();
        public List<ModelDef> Models { get; set; } = new();
        public TerrainDef? Terrain { get; set; }
        public HideDef? Hide { get; set; }
        public List<InstanceDef> Instances { get; set; } = new();
        public List<MoveDef> Move { get; set; } = new();
        /// <summary>Reset the terrain culling tree to the whole city (B13). false = keep the original tree (diagnostics).</summary>
        public bool OpenCullingTree { get; set; } = true;
        /// <summary>Split terrain materials into spatial tiles and refit each culling cell to its block's geometry (B22,
        /// default). Takes precedence over <see cref="OpenCullingTree"/>.</summary>
        public bool FitCullingTree { get; set; } = true;
        /// <summary>Free resident memory (B24): models used only by hidden scenery keep their draws but hold no geometry,
        /// and every model's unreferenced vertex/index bytes are dropped (<see cref="CompactModels"/>).</summary>
        public bool CompactGpu { get; set; } = true;
        /// <summary>0 (default) = hidden instances are sunk 20,000 units at full scale (B26); &gt; 0 = old hide: this scale at
        /// y -20000 with the rotation reset.</summary>
        public float HideScale { get; set; } = 0f;
        public List<MarkerDef> Markers { get; set; } = new();
        /// <summary>Replaces the world's water surfaces (chunk 38). Each entry: kind (2 = sea, 1 = pool) + OBJ of
        /// horizontal triangles.</summary>
        public List<WaterDef>? Water { get; set; }
        /// <summary>Chunk-12 instance whose record is copied for new instances (index in the original world).</summary>
        public int InstanceTemplate { get; set; }
        /// <summary>Sun, ambient and fog written into the world's light setups (Source import: light_environment /
        /// env_fog_controller).</summary>
        public LightDef? Light { get; set; }
        /// <summary>Executable mods switched on in the workspace (e.g. world-bounds-2048, no-escape-reset).</summary>
        public List<string> ExeMods { get; set; } = new();
        /// <summary>Removes the world's grass layers (background chunk 17 count = 0): the game scatters grass over the old
        /// terrain's height tiles even when the terrain is gone.</summary>
        public bool NoGrass { get; set; }
        /// <summary>Moves every record of marker assets by an offset (keeping their layout), e.g. the old town's objects
        /// below an imported map onto a hidden floor.</summary>
        public List<MarkerShiftDef> MarkerShift { get; set; } = new();
    }
    public sealed class MarkerShiftDef
    {
        public string Asset { get; set; } = "";
        public float[] Offset { get; set; } = new float[3];
        /// <summary>Records kept in place: "type" or "type:index" (e.g. "4:84" = the player spawn).</summary>
        public List<string> Except { get; set; } = new();
    }
    /// <summary>
    /// Light-setup values (Atmosphere.cs LightSetup). Colours "RRGGBB"; sun elevation / azimuth in degrees (game sun
    /// direction = (-cos e sin a, sin e, -cos e cos a)). Null fields keep the setup's own value. <see cref="Setups"/>:
    /// short names ("main", "morning" ...) to change; empty = every light setup of the world.
    /// </summary>
    public sealed class LightDef
    {
        public string? Ambient { get; set; }
        public string? Sun { get; set; }
        public float? Intensity { get; set; }
        public float? Elevation { get; set; }
        public float? Azimuth { get; set; }
        public bool? FogOn { get; set; }
        public float? FogStart { get; set; }
        public float? FogEnd { get; set; }
        public float? FogMax { get; set; }
        public string? FogColour { get; set; }
        public List<string> Setups { get; set; } = new();
    }
    public sealed class TemplateDef
    {
        public string Model { get; set; } = "";
        /// <summary>The template's colour texture stem (retargeted to each model's texture).</summary>
        public string Colour { get; set; } = "";
        public string? Ao { get; set; }
        public string? Spec { get; set; }
        public string? FlatNormal { get; set; }
    }
    public sealed class TextureDef { public string Name { get; set; } = ""; public string Image { get; set; } = ""; public int[]? Size { get; set; } }
    public sealed class ModelDef
    {
        public string Name { get; set; } = "";
        public string Template { get; set; } = "";
        public string Obj { get; set; } = "";
        public string Texture { get; set; } = "";
        /// <summary>"box", "none", "mesh" (the model's own geometry) or "mesh:path.obj".</summary>
        public string Collision { get; set; } = "box";
        public float Cull { get; set; } = 1e6f;
        /// <summary>This model's ambient-occlusion texture (e.g. a baked lightmap page; sampled with the second UV set),
        /// instead of the template's neutral AO.</summary>
        public string? Ao { get; set; }
    }
    public sealed class TerrainDef
    {
        /// <summary>Terrain geometry; "" or "none" = the world's terrain draws nothing (geometry comes from models).</summary>
        public string Obj { get; set; } = "";
        /// <summary>OBJ material name → (existing terrain material stem to reuse, new texture to show).</summary>
        public Dictionary<string, TerrainMaterial> Materials { get; set; } = new();
        public string? Collision { get; set; }
        public string? FlatNormal { get; set; }
        public string? NeutralParallax { get; set; }
    }
    public sealed class TerrainMaterial { public string Target { get; set; } = ""; public string Texture { get; set; } = ""; }
    public sealed class HideDef { public bool All { get; set; } public List<string> Keep { get; set; } = new(); public List<string> Names { get; set; } = new(); }
    public sealed class InstanceDef
    {
        public string Model { get; set; } = "";
        public float[] Pos { get; set; } = new float[3];
        public float Yaw { get; set; }
        public float Scale { get; set; } = 1f;
        public bool Collision { get; set; } = true;
        /// <summary>Optional full transform (row-major 3x4: three rotation/scale rows, then the translation); replaces
        /// pos / yaw / scale when given (props with pitch and roll).</summary>
        public float[]? Matrix { get; set; }
    }
    /// <summary>Moves a kept original scenery instance (exact instance name): new position, and yaw in degrees if given.</summary>
    public sealed class MoveDef { public string Name { get; set; } = ""; public float[] Pos { get; set; } = new float[3]; public float? Yaw { get; set; } public float Scale { get; set; } = 1f; }
    public sealed class WaterDef { public int Kind { get; set; } = 1; public string Obj { get; set; } = ""; }
    public sealed class MarkerDef
    {
        public string Asset { get; set; } = "";
        /// <summary>Record type (indices are numbered per type, e.g. 6 actor spawn, 14 pickup, 22 path node).</summary>
        public int? Type { get; set; }
        public int Index { get; set; }
        public float[] Pos { get; set; } = new float[3];
        public float? Yaw { get; set; }
        /// <summary>Bundle holding the marker asset (hex); default: the world bundle.</summary>
        public string? Bundle { get; set; }
    }

    public sealed class Report
    {
        public int Textures, Models, Instances, Hidden, Markers, TerrainTriangles, CollisionTriangles, WaterTriangles, LightSetups;
        /// <summary>Size of the saved world bundle (bytes).</summary>
        public long BundleBytes;
        public readonly List<string> Notes = new();
        public readonly List<string> Errors = new();
    }

    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static Scene Load(string path) => JsonSerializer.Deserialize<Scene>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("empty scene file");

    public static Report Build(Workspace ws, AssetIndex index, string scenePath, IProgress<(string, double)>? progress = null)
    {
        var sc = Load(scenePath);
        string dir = Path.GetDirectoryName(Path.GetFullPath(scenePath))!;
        string P(string rel) => Path.IsPathRooted(rel) ? rel : Path.Combine(dir, rel);
        var rep = new Report();
        uint bundle = Convert.ToUInt32(sc.World, 16);
        var we = WorldCatalog.FromIndex(index).First(w => w.Bundle == bundle);
        if (sc.FromOriginal) ws.Revert($"Bundle/4f/{bundle:x6}");
        ws.ForgetCache(bundle);
        var caff = ws.LoadResident(bundle);
        int Sym(string name) => caff.Symbols.FindIndex(s => AssetIds.DisplayName(s) == name) + 1;
        int bg() => Sym(AssetIds.DisplayName(we.BackgroundModel));
        int originalCount = ModelAsset.Parse(caff, bg(), geometry: false).Instances.Count;
        int total = sc.Textures.Count + sc.Models.Count + sc.Instances.Count + 3, done = 0;
        void Step(string what) => progress?.Report((what, (double)done++ / Math.Max(1, total)));

        // 1. textures
        foreach (var t in sc.Textures)
        {
            Step("texture " + t.Name);
            try
            {
                var (rgba, iw, ih) = ImageIO.Load(P(t.Image));
                int w = t.Size?[0] ?? iw, h = t.Size?[1] ?? ih;
                TextureFactory.Create(caff, Tex(t.Name), rgba, iw, ih, w, h);
                rep.Textures++;
            }
            catch (Exception e) { rep.Errors.Add($"texture {t.Name}: {e.Message}"); }
        }
        if (sc.ReplaceTextures.Count > 0)
        {
            // names may end in '*' (every resident texture whose stem starts with the prefix, e.g. all grass map tiles)
            try
            {
                var stems = caff.Symbols.Select(x => TextureReplacer.Stem(x)).Where(x => x.StartsWith("aid_texture_")).Distinct().ToList();
                var items = new List<(string, byte[], int, int)>();
                foreach (var t in sc.ReplaceTextures)
                {
                    var (rgba, iw, ih) = ImageIO.Load(P(t.Image));
                    var names = t.Name.EndsWith("*") ? stems.Where(x => x.StartsWith(t.Name[..^1])).ToList() : new List<string> { t.Name };
                    if (names.Count == 0) rep.Errors.Add($"replace texture {t.Name}: no match");
                    foreach (var n in names) items.Add((n, rgba, iw, ih));
                    rep.Notes.Add($"replace {t.Name}: {names.Count} texture(s)");
                }
                var r = TextureReplacer.ReplaceMany(ws, bundle, items);
                // TextureReplacer saved the bundle; the workspace cache holds the re-read copy. Continue on that copy, or a
                // later save of the old instance would drop the replacements (B12).
                caff = ws.LoadResident(bundle);
                rep.Notes.Add($"replaced textures: {r.ResidentAssets} resident, {r.StreamedAssets} streamed asset(s)");
                foreach (var n in r.Notes.Where(n => n.Contains("skipped") || n.StartsWith("no texture"))) rep.Notes.Add("  " + n);
            }
            catch (Exception e) { rep.Errors.Add($"replace textures: {e.Message}"); }
        }
        // 2. models
        foreach (var m in sc.Models)
        {
            Step("model " + m.Name);
            try
            {
                if (!sc.Templates.TryGetValue(m.Template, out var td)) throw new InvalidDataException($"unknown template '{m.Template}'");
                var o = new ModelFactory.Options
                {
                    Template = td.Model, Name = m.Name, Meshes = ObjReader.ReadAny(P(m.Obj)), SingleMaterial = Tex(m.Texture),
                    NeutralAo = m.Ao != null ? Tex(m.Ao) : td.Ao != null ? Tex(td.Ao) : null, Specular = td.Spec != null ? Tex(td.Spec) : null, FlatNormal = td.FlatNormal,
                    CullDistance = m.Cull,
                };
                o.Retarget[td.Colour] = Tex(m.Texture);
                if (m.Collision.StartsWith("mesh:")) { o.Collision = "mesh"; o.CollisionMeshes = ObjReader.ReadAny(P(m.Collision[5..])); }
                else o.Collision = m.Collision;
                var r = ModelFactory.Create(caff, o);
                rep.Models++;
                foreach (var n in r.Notes.Where(n => n.StartsWith("warning") || n.Contains("not ")).Take(3)) rep.Notes.Add($"{m.Name}: {n}");
            }
            catch (Exception e) { rep.Errors.Add($"model {m.Name}: {e.Message}"); }
        }
        // 3. terrain geometry + collision
        if (sc.Terrain != null)
        {
            Step("terrain");
            try
            {
                int b = bg();
                foreach (var (_, tm) in sc.Terrain.Materials)
                    rep.Notes.Add($"terrain {tm.Target} -> {tm.Texture}: {ModelEdit.RetargetTexture(caff, b, tm.Target, Tex(tm.Texture), exact: true)} entries");
                if (sc.Terrain.FlatNormal != null || sc.Terrain.NeutralParallax != null)
                    foreach (var st in ModelEdit.TextureStems(caff, b))
                    {
                        if (sc.Terrain.NeutralParallax != null && st.Contains("parallax")) ModelEdit.RetargetTexture(caff, b, st, Tex(sc.Terrain.NeutralParallax), exact: true);
                        else if (sc.Terrain.FlatNormal != null && (st.Contains("normal") || st.EndsWith("_norm") || st.Contains("_norm_") || st.Contains("bump"))) ModelEdit.RetargetTexture(caff, b, st, sc.Terrain.FlatNormal);
                    }
                bool noTerrain = string.IsNullOrEmpty(sc.Terrain.Obj) || sc.Terrain.Obj.Equals("none", StringComparison.OrdinalIgnoreCase);
                var meshes = noTerrain ? new List<ImportMesh>() : ObjReader.ReadAny(P(sc.Terrain.Obj));
                foreach (var mesh in meshes)
                    mesh.Name = sc.Terrain.Materials.TryGetValue(mesh.Name, out var tm) ? Tex(tm.Texture) : mesh.Name;
                var ir = ModelImporter.Replace(caff, b, meshes, spatialTiles: sc.FitCullingTree && !noTerrain);
                rep.TerrainTriangles = ir.Triangles;
                rep.Notes.AddRange(ir.Notes.Where(n => n.StartsWith("mapping") || n.StartsWith("warning") || n.StartsWith("material")).Select(n => "terrain: " + n));
                ModelEdit.SetLodDistances(caff, b, 1e6f, 1f, keepLod0: true);
                // the culling tree still holds the original town's boxes: cover the new terrain everywhere
                var tmin = new System.Numerics.Vector3(float.MaxValue); var tmax = new System.Numerics.Vector3(float.MinValue);
                foreach (var mesh in meshes) foreach (var p in mesh.Positions) { tmin = System.Numerics.Vector3.Min(tmin, p); tmax = System.Numerics.Vector3.Max(tmax, p); }
                if (noTerrain) rep.Notes.Add("terrain: geometry removed (every terrain draw is empty)");
                else if (sc.FitCullingTree)
                {
                    var (filled, emptied) = ModelEdit.FitCullCells(caff, b);
                    rep.Notes.Add($"terrain culling cells refitted: {filled} with geometry, {emptied} emptied (spatial tiles of {ModelImporter.TileTriangles} triangles)");
                }
                else if (sc.OpenCullingTree) rep.Notes.Add($"terrain culling tree: {ModelEdit.SetCullBounds(caff, b, tmin - new System.Numerics.Vector3(50), tmax + new System.Numerics.Vector3(50))} node(s) set to {tmin} .. {tmax}");
                if (sc.Terrain.Collision != null)
                {
                    int hk = Sym(AssetIds.DisplayName(we.BackgroundModel).Replace("aid_model_", "aid_havok_"));
                    var (Pp, T) = NB.Core.Havok.HkCollisionImport.Merge(ObjReader.ReadAny(P(sc.Terrain.Collision)));
                    var cr = NB.Core.Havok.HkCollisionImport.Replace(caff, hk, Pp, T, null);
                    rep.CollisionTriangles = cr.Triangles;
                    rep.Notes.Add($"terrain collision: {cr.Triangles} triangles, {cr.Vertices} vertices, MOPP {cr.MoppBytes} bytes, AABB {cr.Min} .. {cr.Max}");
                }
            }
            catch (Exception e) { rep.Errors.Add($"terrain: {e.Message}"); }
        }
        // 3b. water
        if (sc.Water != null)
        {
            try
            {
                var regs = sc.Water.Select(w => new WaterEditor.Region { Kind = w.Kind, Triangles = WaterEditor.FromMesh(ObjReader.ReadAny(P(w.Obj))) }).ToList();
                var (nr, nt) = WaterEditor.Write(caff, bg(), regs);
                rep.WaterTriangles = nt;
                rep.Notes.Add($"water: {nr} region(s), {nt} triangles");
            }
            catch (Exception e) { rep.Errors.Add($"water: {e.Message}"); }
        }
        // 4. hide original scenery (before adding new instances so indices stay those of the original world)
        if (sc.Hide != null)
        {
            Step("hide");
            var inst = ModelAsset.Parse(caff, bg(), geometry: false).Instances;
            var hiddenIdx = new HashSet<int>();
            foreach (var i in inst.Where(i => i.Index < originalCount))
            {
                string nm = ModelAsset.CleanInstanceName(i.Name);
                if (sc.Move.Any(mv => mv.Name.Equals(nm, StringComparison.OrdinalIgnoreCase))) continue;   // moved instances are kept
                bool hide = sc.Hide.All ? !sc.Hide.Keep.Any(k => nm.Contains(k, StringComparison.OrdinalIgnoreCase))
                                        : sc.Hide.Names.Any(k => nm.Contains(k, StringComparison.OrdinalIgnoreCase));
                if (!hide) continue;
                // Hide = zero scale, moved to y -10000; its collision stays in its list (and moves with it). Clearing
                // the lists hangs the level: game objects look up their scenery's physics instances by name
                // (e.g. 0x82472330 finds "|REFERENCE_flapbridge|" in the runtime instance list, which is built from the
                // collision lists) and read past an empty result.
                // tiny but non-zero scale: 760 zero-scale bodies crashed a Havok worker (null read in 0x82963A40)
                // sunk 20,000 units at full scale and orientation (B26): shrinking left attached moving parts jammed in
                // full-size collision, which the console could not keep up with. Far below: a falling player never
                // reaches them (B25). "hideScale" > 0 restores the old shrink-and-move hide (diagnostics).
                if (sc.HideScale > 0) InstanceEditor.Hide(caff, bg(), i.Index, sc.HideScale, -20000f);
                else InstanceEditor.Sink(caff, bg(), i.Index, -20000f);
                hiddenIdx.Add(i.Index);
                rep.Hidden++;
            }
            // hidden instances always select their last LOD level (tiny scale): make it a cull level for models that only
            // hidden instances use, or they keep costing draw calls everywhere (B22)
            var (culled, noTable) = ModelEdit.CullHiddenOnlyModels(caff, bg(), hiddenIdx);
            rep.Notes.Add($"hidden models: {culled} given a cull level; {noTable} without a LOD table (still drawn at 0.001 scale)");
        }
        // 4b. move kept original instances
        if (sc.Move.Count > 0)
        {
            Step("move");
            var inst = ModelAsset.Parse(caff, bg(), geometry: false).Instances;
            foreach (var mv in sc.Move)
            {
                var hits = inst.Where(i => i.Index < originalCount && ModelAsset.CleanInstanceName(i.Name).Equals(mv.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (hits.Count == 0) { rep.Errors.Add($"move {mv.Name}: no such instance"); continue; }
                foreach (var i in hits)
                {
                    var m = InstanceEditor.GetMatrix(caff, bg(), i.Index);
                    if (mv.Yaw is float yaw)
                    {
                        float sx = new Vector3(m.M11, m.M12, m.M13).Length(), sy = new Vector3(m.M21, m.M22, m.M23).Length(), sz = new Vector3(m.M31, m.M32, m.M33).Length();
                        m = Matrix4x4.CreateScale(sx, sy, sz) * Matrix4x4.CreateRotationY(yaw * MathF.PI / 180);
                    }
                    if (mv.Scale != 1f) { m.M11 *= mv.Scale; m.M12 *= mv.Scale; m.M13 *= mv.Scale; m.M21 *= mv.Scale; m.M22 *= mv.Scale; m.M23 *= mv.Scale; m.M31 *= mv.Scale; m.M32 *= mv.Scale; m.M33 *= mv.Scale; }
                    m.M41 = mv.Pos[0]; m.M42 = mv.Pos[1]; m.M43 = mv.Pos[2];
                    InstanceEditor.SetMatrix(caff, bg(), i.Index, m);
                    rep.Notes.Add($"moved {mv.Name} (#{i.Index}) to ({mv.Pos[0]}, {mv.Pos[1]}, {mv.Pos[2]})");
                }
            }
        }
        // 5. new instances: one batch, so the instance tables are re-created once (B21)
        {
            var batch = new List<InstanceEditor.NewInstance>();
            foreach (var d in sc.Instances)
            {
                try
                {
                    string model = ModelFactory.ModelName(d.Model), havok = ModelFactory.HavokName(d.Model);
                    if (Sym(model) == 0) throw new InvalidDataException($"model {model} does not exist");
                    uint? hid = d.Collision && Sym(havok) > 0 ? AssetIds.IdOf(havok) : null;
                    var world = Matrix4x4.CreateScale(d.Scale) * Matrix4x4.CreateRotationY(d.Yaw * MathF.PI / 180) * Matrix4x4.CreateTranslation(d.Pos[0], d.Pos[1], d.Pos[2]);
                    if (d.Matrix is { Length: 12 } x)
                        world = new Matrix4x4(x[0], x[1], x[2], 0, x[3], x[4], x[5], 0, x[6], x[7], x[8], 0, x[9], x[10], x[11], 1);
                    batch.Add(new InstanceEditor.NewInstance(AssetIds.IdOf(model)!.Value, hid, world, d.Model));
                }
                catch (Exception e) { rep.Errors.Add($"instance {d.Model}: {e.Message}"); }
            }
            if (batch.Count > 0)
            {
                Step($"instances ({batch.Count})");
                try { InstanceEditor.AddModelInstances(caff, bg(), sc.InstanceTemplate, batch); rep.Instances += batch.Count; }
                catch (Exception e) { rep.Errors.Add($"instances: {e.Message}"); }
            }
        }
        // 6. markers (world bundle): shifts first, then the explicit moves (a moved record wins)
        foreach (var ms in sc.MarkerShift)
        {
            try
            {
                int s = caff.Symbols.FindIndex(x => AssetIds.DisplayName(x) == ms.Asset) + 1;
                if (s == 0) throw new InvalidDataException($"marker asset {ms.Asset} not in bundle {bundle:x6}");
                var ma = MarkerAsset.Parse(caff, s);
                var off = new Vector3(ms.Offset[0], ms.Offset[1], ms.Offset[2]);
                int moved = 0;
                foreach (var r in ma.Records)
                {
                    if (ms.Except.Any(e => e == r.Type.ToString() || e == $"{r.Type}:{r.Index}")) continue;
                    r.Position += off;
                    MarkerAsset.WriteTransform(caff, s, r);
                    moved++;
                }
                rep.Notes.Add($"markers {ms.Asset}: {moved} of {ma.Records.Count} record(s) moved by {off}");
            }
            catch (Exception e) { rep.Errors.Add($"marker shift {ms.Asset}: {e.Message}"); }
        }
        foreach (var mk in sc.Markers)
        {
            try
            {
                uint mb = mk.Bundle != null ? Convert.ToUInt32(mk.Bundle, 16) : bundle;
                var mc = mb == bundle ? caff : ws.LoadResident(mb);
                int s = mc.Symbols.FindIndex(x => AssetIds.DisplayName(x) == mk.Asset) + 1;
                if (s == 0) throw new InvalidDataException($"marker asset {mk.Asset} not in bundle {mb:x6}");
                var ma = MarkerAsset.Parse(mc, s);
                var r = ma.Records.First(x => x.Index == mk.Index && (mk.Type == null || x.Type == mk.Type));
                r.Position = new Vector3(mk.Pos[0], mk.Pos[1], mk.Pos[2]);
                if (mk.Yaw is float y) r.Rotation = new Vector3(r.Rotation.X, y * MathF.PI / 180, r.Rotation.Z);
                MarkerAsset.WriteTransform(mc, s, r);
                if (mb != bundle) ws.SaveResident(mb, mc, $"scene marker {mk.Asset} #{mk.Index} -> {string.Join(",", mk.Pos)}");
                rep.Markers++;
            }
            catch (Exception e) { rep.Errors.Add($"marker {mk.Asset} #{mk.Index}: {e.Message}"); }
        }
        if (sc.NoGrass)
        {
            try
            {
                var m = ModelAsset.Parse(caff, bg(), geometry: false);
                if (m.Chunks.TryGetValue(17, out int c17))
                {
                    var d = m.View.Data(".data");
                    int n = NB.Core.IO.BE.S32(d, c17);
                    NB.Core.IO.BE.W32(d, c17, 0u);
                    rep.Notes.Add($"grass: {n} layer(s) removed (chunk 17 count 0)");
                }
            }
            catch (Exception e) { rep.Errors.Add($"grass: {e.Message}"); }
        }
        // 7. light setups
        if (sc.Light != null)
        {
            try { rep.LightSetups = ApplyLight(caff, sc.Light, rep.Notes); }
            catch (Exception e) { rep.Errors.Add($"light: {e.Message}"); }
        }
        if (sc.ExeMods.Count > 0)
        {
            var added = sc.ExeMods.Where(m => !ws.Manifest.ExeMods.Contains(m)).ToList();
            ws.Manifest.ExeMods.AddRange(added);
            if (added.Count > 0) ws.SaveManifest();
            rep.Notes.Add($"exe mods: {string.Join(", ", sc.ExeMods)}{(added.Count > 0 ? $" ({added.Count} switched on)" : " (already on)")}");
        }
        if (sc.CompactGpu)
        {
            Step("compact");
            rep.Notes.AddRange(CompactModels(caff, bundle, ws, stripHidden: true));
        }
        Step("saving");
        ws.SaveResident(bundle, caff, $"scene build {Path.GetFileName(scenePath)}: {rep.Textures} textures, {rep.Models} models, {rep.Instances} instances, {rep.Hidden} hidden, terrain {rep.TerrainTriangles} tris, collision {rep.CollisionTriangles} tris");
        try { rep.BundleBytes = new FileInfo(ws.Game.ResidentPath(bundle)).Length; } catch (IOException) { }
        progress?.Report(("done", 1));
        return rep;
    }

    /// <summary>
    /// Frees model vertex/index memory in a resident bundle (B24). With <paramref name="stripHidden"/>, models reached only
    /// from hidden background instances (y below -10000) and referenced by no other asset get their geometry replaced by
    /// a block of zeros (<see cref="GpuCompactor.StripGeometry"/>). Then every model drops its unreferenced .gpu bytes
    /// (<see cref="GpuCompactor.Compact"/>). Returns report lines.
    /// </summary>
    public static List<string> CompactModels(CaffFile caff, uint bundle, Workspace ws, bool stripHidden)
    {
        var notes = new List<string>();
        var index = AssetIndex.LoadOrBuild(ws);
        var we = WorldCatalog.FromIndex(index).First(w => w.Bundle == bundle);
        int bgSym = caff.Symbols.FindIndex(s => AssetIds.DisplayName(s) == AssetIds.DisplayName(we.BackgroundModel)) + 1;
        long before = caff.Parts.Where(p => caff.SectionOf(p).Name == ".gpu").Sum(p => (long)p.Data.Length);
        int stripped = 0, stripFail = 0;
        if (stripHidden)
        {
            // hidden = sunk by 20,000 (or the old hide at y -20000); imported maps can reach far below y -200 (Source import)
            var hidden = ModelAsset.Parse(caff, bgSym, geometry: false).Instances.Where(i => i.World.M42 < -10000).Select(i => i.Index).ToHashSet();
            var only = ModelEdit.HiddenOnlyModels(caff, bgSym, hidden);
            // the background and its collision asset (which holds the per-instance collision lists) own the hidden instances
            int bgHavok = caff.Symbols.FindIndex(s => AssetIds.DisplayName(s) == AssetIds.DisplayName(we.BackgroundModel).Replace("aid_model_", "aid_havok_")) + 1;
            var owners = new HashSet<int> { bgSym, bgHavok };
            var safe = GpuCompactor.Unreferenced(caff, only, owners);
            notes.Add($"compact: {hidden.Count} hidden instances; {only.Count} models only they use, {safe.Count} referenced by no other asset");
            if (Environment.GetEnvironmentVariable("NB_COMPACT_DEBUG") == "1")
            {
                var onlyNames = only.Select(x => AssetIds.DisplayName(caff.Symbols[x - 1])).ToHashSet();
                foreach (var g in GpuCompactor.Referrers(caff, only.Except(safe), owners)
                             .Select(x => (x.Model, Who: x.Who.Split(' ')[0] + " " + x.Who.Split(' ')[1].Split('+')[0]))
                             .Where(x => !onlyNames.Contains(x.Who.Split(' ')[0].Replace("aid_havok_", "aid_model_")))
                             .GroupBy(x => x.Who).OrderByDescending(g => g.Count()).Take(30))
                    notes.Add($"  {g.Key}: {g.Select(x => x.Model).Distinct().Count()} models");
            }
            foreach (int s in safe)
            {
                if (GpuCompactor.StripGeometry(caff, s, out var why) >= 0) stripped++;
                else { stripFail++; notes.Add($"compact: {AssetIds.DisplayName(caff.Symbols[s - 1])} not stripped: {why}"); }
            }
        }
        int compacted = 0, skipped = 0;
        for (int s = 1; s <= caff.Symbols.Count; s++)
        {
            if (!AssetIds.DisplayName(caff.Symbols[s - 1]).StartsWith("aid_model_")) continue;
            if (!caff.PartsOf(s).Any(p => caff.SectionOf(p).Name == ".gpu")) continue;
            int f;
            try { f = GpuCompactor.Compact(caff, s, out _); } catch { f = -1; }
            if (f > 0) compacted++; else if (f < 0) skipped++;
        }
        long after = caff.Parts.Where(p => caff.SectionOf(p).Name == ".gpu").Sum(p => (long)p.Data.Length);
        notes.Add($"compact: {stripped} models stripped ({stripFail} could not be), {compacted} compacted ({skipped} skipped); .gpu {before / 1048576.0:F1} MB -> {after / 1048576.0:F1} MB");
        return notes;
    }

    /// <summary>Writes <paramref name="l"/> into the world bundle's light setups (all, or those named in Setups). Returns
    /// the number of setups changed.</summary>
    public static int ApplyLight(CaffFile caff, LightDef l, List<string> notes)
    {
        static uint Rgb(string s) => Convert.ToUInt32(s.TrimStart('#'), 16) & 0xFFFFFF;
        int n = 0;
        for (int s = 1; s <= caff.Symbols.Count; s++)
        {
            var name = AssetIds.DisplayName(caff.Symbols[s - 1]);
            if (!name.StartsWith("aid_script_banjox_lightsetup_")) continue;
            var part = caff.PartsOf(s).FirstOrDefault(p => caff.SectionOf(p).Name == ".data");
            if (part == null) continue;
            int fog = LightSetup.FindFog(part.Data);
            if (fog < 0) continue;
            var ls = new LightSetup { Name = name, Symbol = s, Data = part.Data, FogCommand = fog };
            if (l.Setups.Count > 0 && !l.Setups.Any(x => x.Equals(ls.ShortName, StringComparison.OrdinalIgnoreCase))) continue;
            var v = ls.Values;
            const float Deg = MathF.PI / 180;
            ls.Values = v with
            {
                Ambient = l.Ambient != null ? Rgb(l.Ambient) : v.Ambient,
                Sun = l.Sun != null ? Rgb(l.Sun) : v.Sun,
                Intensity = l.Intensity ?? v.Intensity,
                SunElevation = l.Elevation is float e ? e * Deg : v.SunElevation,
                SunAzimuth = l.Azimuth is float a ? a * Deg : v.SunAzimuth,
                FogOn = l.FogOn ?? v.FogOn,
                FogStart = l.FogStart ?? v.FogStart,
                FogEnd = l.FogEnd ?? v.FogEnd,
                FogMax = l.FogMax ?? v.FogMax,
                FogColour = l.FogColour != null ? Rgb(l.FogColour) : v.FogColour,
            };
            notes.Add($"light {ls.ShortName}: {v} -> {ls.Values}");
            n++;
        }
        return n;
    }

    /// <summary>Texture names in scene files may omit the "aid_texture_banjox_" prefix.</summary>
    public static string Tex(string n) => n.StartsWith("aid_texture_") ? n : "aid_texture_banjox_" + n;
}
