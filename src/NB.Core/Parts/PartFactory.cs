using System.Numerics;
using System.Text;
using System.Text.Json;
using NB.Core.Formats;
using NB.Core.IO;
using NB.Core.Models;
using NB.Core.Project;
using NB.Core.Tags;
using NB.Core.Textures;
using NB.Core.World;

namespace NB.Core.Parts;

/// <summary>
/// A new vehicle part (garage block), built from an existing part used as template. What the game needs, and where
/// (ultra/PLAN.md §1, verified in Xenia for the ULTRA Engine):
/// <list type="number">
/// <item>Part record aid_objparams_banjox_vehicleblock_&lt;Id&gt; (resident 4f/685374): class and stats; +0xA0 part key
/// (names it through the blocks text table), a tier/size string (+0x268 or +0x2A8 depending on the class), +0x124 model.</item>
/// <item>Model aid_model_banjox_vehicleparts_&lt;ModelId&gt; in every resident bundle that holds the template's model, with
/// its collision aid_havok_banjox_vehicleparts_&lt;ModelId&gt; next to it (the game derives the collision from the model name).</item>
/// <item>Name: loctext aid_loctext_banjox_blocks "block__&lt;key&gt;"; tier label "block__group_&lt;tier&gt;".</item>
/// <item>Tier: a record in the part group's aid_misc_banjox_garagegrouping_* asset (the store lists one entry per tier).</item>
/// <item>Inventory: (count, id) pairs in blocksets (crates, start pack, blockset_all); network list: aid_misc_banjox_live_networkenum.</item>
/// </list>
/// </summary>
public static class PartFactory
{
    public sealed class PartSpec
    {
        /// <summary>New part id suffix: aid_objparams_banjox_vehicleblock_&lt;Id&gt;.</summary>
        public string Id { get; set; } = "";
        /// <summary>Template part id suffix, e.g. propulsion_engines_superpower.</summary>
        public string Template { get; set; } = "";
        /// <summary>Part key (+0xA0), e.g. engineultra; names the part through block__&lt;key&gt;.</summary>
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>Tier / size string and the text shown for it; Offset of the tier string (0x2A8 engines, 0x268 fuel/ammo/wheels).</summary>
        public string? Tier { get; set; }
        public string? TierLabel { get; set; }
        public string TierOffset { get; set; } = "2A8";
        /// <summary>Grouping asset suffix (aid_misc_banjox_garagegrouping_&lt;x&gt;) to register the tier in; copy the record of this tier.</summary>
        public string? Grouping { get; set; }
        public string? GroupingCopyFrom { get; set; }
        /// <summary>Developer / shop description (+0xE0, up to 63 characters).</summary>
        public string? Description { get; set; }
        /// <summary>Paint colour key (+0x130), e.g. colour_orange.</summary>
        public string? Colour { get; set; }
        /// <summary>Field edits: offset (hex) → "f:1.5" | "u:12" | "s:text" | "h:1F2421CB".</summary>
        public Dictionary<string, string> Set { get; set; } = new();
        public ModelSpec? Model { get; set; }
        /// <summary>Garage description spoken by Mumbo (own dialog asset + text); null keeps the template's.</summary>
        public string? Dialog { get; set; }
        /// <summary>Footprint and attachment points (default: the template part's).</summary>
        public AttachSpec? Attach { get; set; }
        /// <summary>Blockset name (suffix of aid_misc_banjox_blockset_) → count.</summary>
        public Dictionary<string, int> Blocksets { get; set; } = new();
        public bool NetworkEnum { get; set; } = true;
    }

    public sealed class AttachSpec
    {
        /// <summary>Footprint template: suffix of an aid_avatarhavokdata_banjox_vehicleblock_* asset (e.g. gadgets_energyshield = 3×1×3).</summary>
        public string Template { get; set; } = "";
        /// <summary>New asset suffix.</summary>
        public string Id { get; set; } = "";
        /// <summary>"all", "none", "bottom", "top", "sides", "bottom+sides" or face centres "x,y,z;…" (null: keep the template's).</summary>
        public string? Attachable { get; set; }
    }

    public sealed class ModelSpec
    {
        /// <summary>New model id suffix: aid_model_banjox_vehicleparts_&lt;Id&gt;.</summary>
        public string Id { get; set; } = "";
        public string Obj { get; set; } = "";
        /// <summary>Template model suffix (aid_model_banjox_vehicleparts_&lt;x&gt;) when it is not the template part's own model.</summary>
        public string? Template { get; set; }
        /// <summary>Rotation applied to the OBJ before import, "x:90" (degrees about x, y or z; several separated by ';'). The
        /// game shows some templates turned (the Super Fuel model: model (x, y, z) appears at (x, z, −y), so "x:90").</summary>
        public string? PreRotate { get; set; }
        /// <summary>Collision: null = the template's convex hull unchanged; "model" = scaled to the OBJ's bounds;
        /// "footprint" = scaled to the part's cells (HkConvexEdit).</summary>
        public string? CollisionFit { get; set; }
        /// <summary>Texture stem → "r,g,b": recolour the pixel constants of the draws using it (ModelEdit.TintConstants).</summary>
        public Dictionary<string, string> Tint { get; set; } = new();
        /// <summary>OBJ material → template texture stem it replaces (e.g. "shared_materials_metal_components_plaindetailed_colour_0x01ede525").</summary>
        public Dictionary<string, string> Materials { get; set; } = new();
        /// <summary>Template texture stem → new texture name (created from <see cref="Textures"/> or an existing one).</summary>
        public Dictionary<string, string> Retarget { get; set; } = new();
        /// <summary>New resident textures: name (aid_texture_banjox_…) → PNG path, created in every bundle that gets the model.</summary>
        public Dictionary<string, string> Textures { get; set; } = new();
        /// <summary>Vertex buffers drawn only on non-LOD nodes (the template's animated moving parts): true = left untouched
        /// (they keep moving), false = hidden (the new geometry never goes there).</summary>
        public bool KeepAnimated { get; set; } = false;
        /// <summary>Resident bundles to add the model to (hex); default: every bundle holding the template's model.</summary>
        public List<string>? Bundles { get; set; }
    }

    public const string Common = "685374";
    public static string Record(string id) => "aid_objparams_banjox_vehicleblock_" + id;
    public static string ModelAsset(string id) => "aid_model_banjox_vehicleparts_" + id;
    public static string HavokAsset(string id) => "aid_havok_banjox_vehicleparts_" + id;

    public static List<string> Build(Workspace ws, AssetIndex idx, PartSpec p, string baseDir)
    {
        var log = new List<string>();
        uint common = Convert.ToUInt32(Common, 16);
        string rec = Record(p.Id), tpl = Record(p.Template);
        uint recId = AssetIds.IdOf(rec) ?? throw new InvalidDataException("bad part id " + p.Id);

        // 1. part record
        var cc = ws.LoadResident(common);
        int tsym = cc.Symbols.FindIndex(s => AssetIds.DisplayName(s) == tpl) + 1;
        if (tsym == 0) throw new InvalidDataException($"template {tpl} is not in {Common}");
        int rsym = cc.Symbols.FindIndex(s => AssetIds.DisplayName(s) == rec) + 1;
        if (rsym == 0) { rsym = CaffEdit.CloneAsset(cc, tsym, rec); log.Add($"record {rec} (0x{recId:X8}) cloned from {tpl}"); }
        var d = cc.PartsOf(rsym).First(x => cc.SectionOf(x).Name == ".data").Data;
        void Str(int off, string v, int max = 64)
        {
            var b = Encoding.ASCII.GetBytes(v); if (b.Length >= max) throw new ArgumentException($"'{v}' is longer than {max - 1} characters");
            Array.Clear(d, off, max); b.CopyTo(d, off);
        }
        Str(0xA0, p.Key);
        if (p.Tier != null) Str(Convert.ToInt32(p.TierOffset, 16), p.Tier);
        if (p.Description != null) Str(0xE0, p.Description);
        if (p.Colour != null) Str(0x130, p.Colour, 32);
        foreach (var (k, v) in p.Set)
        {
            int off = Convert.ToInt32(k, 16);
            var (kind, val) = (v[..1], v[2..]);
            switch (kind)
            {
                case "f": BE.WF32(d, off, float.Parse(val, System.Globalization.CultureInfo.InvariantCulture)); break;
                case "u": BE.W32(d, off, uint.Parse(val)); break;
                case "h": BE.W32(d, off, Convert.ToUInt32(val, 16)); break;
                case "s": Str(off, val); break;
                default: throw new ArgumentException("field edit " + v);
            }
        }
        log.Add($"record fields: key {p.Key}, tier {p.Tier ?? "(template)"}, {p.Set.Count} edit(s)");

        // 2. model (+ collision, textures) in every bundle holding the template's model
        if (p.Model != null)
        {
            uint tplModelId = BE.U32(cc.PartsOf(tsym).First(x => cc.SectionOf(x).Name == ".data").Data, 0x124);
            string tplModel = p.Model.Template != null ? ModelAsset(p.Model.Template) : idx.Entries.First(e => e.Id == tplModelId).Name;
            string newModel = ModelAsset(p.Model.Id);
            BE.W32(d, 0x124, AssetIds.IdOf(newModel)!.Value);
            ws.SaveResident(common, cc, $"part {p.Id}: record");
            var bundles = p.Model.Bundles?.Select(x => Convert.ToUInt32(x, 16)).ToList()
                          ?? idx.Entries.Where(e => e.Name == tplModel && !e.Streamed).Select(e => e.Bundle).Distinct().ToList();
            string P(string rel) => Path.IsPathRooted(rel) ? rel : Path.Combine(baseDir, rel);
            var meshes = string.IsNullOrEmpty(p.Model.Obj) ? null : ObjReader.ReadAny(P(p.Model.Obj));   // none: retexture only
            if (meshes != null && !string.IsNullOrEmpty(p.Model.PreRotate))
            {
                var rot = Matrix4x4.Identity;
                foreach (var r0 in p.Model.PreRotate.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = r0.Split(':'); float a = float.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture) * MathF.PI / 180;
                    rot *= kv[0].Trim().ToLowerInvariant() switch { "x" => Matrix4x4.CreateRotationX(a), "y" => Matrix4x4.CreateRotationY(a), _ => Matrix4x4.CreateRotationZ(a) };
                }
                foreach (var m in meshes)
                {
                    for (int i = 0; i < m.Positions.Count; i++) m.Positions[i] = Vector3.Transform(m.Positions[i], rot);
                    if (m.Normals != null) for (int i = 0; i < m.Normals.Count; i++) m.Normals[i] = Vector3.TransformNormal(m.Normals[i], rot);
                }
            }
            string tplHavok = "aid_havok_" + tplModel["aid_model_".Length..], newHavok = HavokAsset(p.Model.Id);
            (Vector3 Min, Vector3 Max)? fit = null;
            if (p.Model.CollisionFit == "model" && meshes != null)
                fit = (new Vector3(meshes.Min(m => m.Bounds().Min.X), meshes.Min(m => m.Bounds().Min.Y), meshes.Min(m => m.Bounds().Min.Z)),
                       new Vector3(meshes.Max(m => m.Bounds().Max.X), meshes.Max(m => m.Bounds().Max.Y), meshes.Max(m => m.Bounds().Max.Z)));
            else if (p.Model.CollisionFit == "footprint")
            {
                string an = p.Attach != null ? AttachPrefix + p.Attach.Template : idx.Entries.FirstOrDefault(e => e.Id == BE.U32(cc.PartsOf(tsym).First(x => cc.SectionOf(x).Name == ".data").Data, 0x12C))?.Name ?? "";
                var ae = idx.Entries.FirstOrDefault(e => e.Name == an && !e.Streamed);
                if (ae != null)
                {
                    var ac = ws.LoadResident(ae.Bundle);
                    var fp = AttachData.Parse(ac.PartsOf(ae.Symbol).First(x => ac.SectionOf(x).Name == ".data").Data);
                    fit = (new Vector3(fp.XMin - 0.5f, fp.YMin - 0.5f, fp.ZMin - 0.5f) * 0.98f, new Vector3(fp.XMax + 0.5f, fp.YMax + 0.5f, fp.ZMax + 0.5f) * 0.98f);
                }
            }
            string FitCollision(CaffFile caff, int hs)
            {
                if (fit == null) return "template collision";
                var d = caff.PartsOf(hs).First(x => caff.SectionOf(x).Name == ".data").Data;
                int n = NB.Core.Havok.HkConvexEdit.FitTo(d, fit.Value.Min, fit.Value.Max);
                return n > 0 ? $"collision fitted to {fit.Value.Min:0.00}..{fit.Value.Max:0.00}" : "collision: no convex shape to fit";
            }
            void AddTextures(CaffFile caff)
            {
                foreach (var (tn, png) in p.Model.Textures)
                {
                    if (caff.Symbols.Any(s => AssetIds.DisplayName(s) == tn)) continue;
                    var (rgba, w, h) = ImageIO.Load(P(png));
                    TextureFactory.Create(caff, tn, rgba, w, h, w, h);
                }
            }
            foreach (uint b in bundles)
            {
                var caff = ws.LoadResident(b);
                int ms = caff.Symbols.FindIndex(s => AssetIds.DisplayName(s) == tplModel) + 1;
                if (ms == 0) { log.Add($"{b:x6}: no {tplModel}, skipped"); continue; }
                AddTextures(caff);
                if (!caff.Symbols.Any(s => AssetIds.DisplayName(s) == newModel))
                {
                    CaffEdit.CloneAsset(caff, ms, newModel);
                    int hs = caff.Symbols.FindIndex(s => AssetIds.DisplayName(s) == tplHavok) + 1;
                    if (hs > 0) CaffEdit.CloneAsset(caff, hs, newHavok); else log.Add($"{b:x6}: template has no {tplHavok} (no collision clone)");
                }
                int nm = caff.Symbols.FindIndex(s => AssetIds.DisplayName(s) == newModel) + 1;
                string res = ImportInto(caff, nm, true);
                int nh = caff.Symbols.FindIndex(s => AssetIds.DisplayName(s) == newHavok) + 1;
                if (nh > 0) res += "; " + FitCollision(caff, nh);
                ws.SaveResident(b, caff, $"part {p.Id}: model {newModel}");
                log.Add($"{b:x6}: {newModel} + {newHavok}: {res}");
            }
            // The textures also go into the common bundle: the streamed model below is drawn in any level and resolves
            // its textures by name among the loaded bundles.
            if (p.Model.Textures.Count > 0 && !bundles.Contains(common))
            {
                var c2 = ws.LoadResident(common); AddTextures(c2);
                ws.SaveResident(common, c2, $"part {p.Id}: textures"); log.Add($"{Common}: {p.Model.Textures.Count} texture(s)");
            }
            // Streamed copies (Bundle/50/685374): vehicles stream their part models by id from there, one single-model
            // CAFF (model + pool) per model id and one per collision id. A part whose model id is missing there stops the
            // vehicle spawn with "Disc Read Error" (seen in Xenia).
            log.AddRange(AddStreamed(ws, common, tplModel, newModel, (sc, sym) => ImportInto(sc, sym, false)));
            log.AddRange(AddStreamed(ws, common, tplHavok, newHavok, fit == null ? null : FitCollision));
            cc = ws.LoadResident(common);

            void Tint(CaffFile caff, int nm)
            {
                foreach (var (stem, rgb) in p.Model.Tint)
                {
                    var c = rgb.Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    ModelEdit.TintConstants(caff, nm, stem.Replace("aid_texture_banjox_", ""), new Vector3(c[0], c[1], c[2]));
                }
            }
            // retarget + OBJ import into model symbol nm of caff (a resident bundle or a streamed copy)
            string ImportInto(CaffFile caff, int nm, bool resident)
            {
                foreach (var (from, to) in p.Model.Retarget)
                    ModelEdit.RetargetTexture(caff, nm, from, to, p.Model.Textures.ContainsKey(to) ? true : null, requireInBundle: resident);
                if (meshes == null) { Tint(caff, nm); return $"template geometry, {p.Model.Retarget.Count} texture(s) retargeted"; }
                // OBJ materials → the (retargeted) diffuse stems of the draws that held the template texture
                var ma = Models.ModelAsset.Parse(caff, nm);
                string Final(string tplStem) => p.Model.Retarget.TryGetValue(tplStem, out var t) ? ObjExporter.TextureFileStem(t.Replace("aid_texture_banjox_", "")) : tplStem;
                var stems = ma.Draws.Select(x => ObjExporter.DiffuseTexture(x)).Where(x => x != null).Select(x => ObjExporter.TextureFileStem(x!)).Distinct().ToList();
                var named = new List<ImportMesh>();
                foreach (var m in meshes)
                {
                    if (!p.Model.Materials.TryGetValue(m.Name, out var tstem)) throw new InvalidDataException($"OBJ material '{m.Name}' has no template material mapping");
                    string want = Final(tstem);
                    string? hit = stems.FirstOrDefault(s => s.Equals(want, StringComparison.OrdinalIgnoreCase) || s.EndsWith(want, StringComparison.OrdinalIgnoreCase) || want.EndsWith(s, StringComparison.OrdinalIgnoreCase));
                    if (hit == null) throw new InvalidDataException($"material '{m.Name}' → '{want}' is not a diffuse texture of the model ({string.Join(", ", stems)})");
                    named.Add(new ImportMesh { Name = hit, Positions = m.Positions, Normals = m.Normals, UVs = m.UVs, Triangles = m.Triangles });
                }
                var lodNodes = ma.LodLevels.SelectMany(g => g.SelectMany(l => l.Nodes)).ToHashSet();
                var animated = ma.Draws.GroupBy(x => x.VbRecord).Where(g => g.Any(x => !lodNodes.Contains(x.Node) && x.Node > 3)).Select(g => g.Key).ToHashSet();
                var keep = p.Model.KeepAnimated ? animated : null;
                var r = ModelImporter.Replace(caff, nm, named, false, keep, p.Model.KeepAnimated ? null : animated);
                ModelEdit.SetLodDistances(caff, nm, 1e6f, 1f, keepLod0: true);
                Tint(caff, nm);
                return $"{r.Vertices} vertices, {r.Triangles} triangles; {animated.Count} animated buffer(s) {(p.Model.KeepAnimated ? "kept" : "hidden")}";
            }
        }
        else ws.SaveResident(common, cc, $"part {p.Id}: record");

        // 2b. footprint / attachment points: a copy of a shipped part's avatarhavokdata with edited attach flags
        if (p.Attach != null) log.AddRange(BuildAttach(ws, idx, p, rec));

        // 3. names
        AddText(ws, "block__" + p.Key, p.Name, log);
        if (p.Tier != null && p.TierLabel != null) AddText(ws, "block__group_" + p.Tier, p.TierLabel, log);
        if (p.Dialog != null) log.AddRange(BuildDialog(ws, idx, p, rec));
        // 4. tier record
        if (p.Tier != null && p.Grouping != null)
        {
            log.Add(AddTier(ws, idx, "aid_misc_banjox_garagegrouping_" + p.Grouping, p.Tier, "group_" + p.Tier, p.GroupingCopyFrom));
            // the parts store shows the description dialog named by the tier record (+0x84), not the part record's +0x120
            if (p.Dialog != null) log.Add(SetTierField(ws, idx, "aid_misc_banjox_garagegrouping_" + p.Grouping, p.Tier, 0x84, AssetIds.IdOf(DialogPrefix + p.Key)!.Value));
        }
        // 5. inventory + network list
        foreach (var (set, n) in p.Blocksets) log.Add(AddToBlockset(ws, idx, "aid_misc_banjox_blockset_" + set, recId, (uint)n));
        if (p.NetworkEnum) log.Add(AddNetworkEnum(ws, recId));
        return log;
    }

    /// <summary>
    /// Adds (or rebuilds) the streamed entry of <paramref name="newAsset"/> in stream archive <paramref name="bundle"/>:
    /// a copy of the template asset's entry with the asset renamed, then <paramref name="edit"/> applied to it.
    /// </summary>
    /// <summary>
    /// Reverses <see cref="Build"/>: removes the part from every blockset and the network list, deletes its record,
    /// model, collision and attach assets (resident and streamed) and, when <paramref name="removeTier"/>, its tier record.
    /// Names (loctext) and created textures stay (harmless, possibly shared). Blueprints that still use the part must be
    /// changed separately.
    /// </summary>
    public static List<string> Uninstall(Workspace ws, AssetIndex idx, PartSpec p, bool removeTier = true)
    {
        var log = new List<string>();
        uint common = Convert.ToUInt32(Common, 16);
        string rec = Record(p.Id);
        uint recId = AssetIds.IdOf(rec)!.Value;
        // inventory + network list
        byte[] Strip(byte[] d, int stride, int idOff)
        {
            var o = new MemoryStream();
            for (int i = 0; i + stride <= d.Length; i += stride) if (BE.U32(d, i + idOff) != recId) o.Write(d, i, stride);
            return o.ToArray();
        }
        foreach (var set in idx.Entries.Where(e => e.Name.StartsWith("aid_misc_banjox_blockset_")).Select(e => e.Name).Distinct().ToList())
        {
            foreach (var e in idx.Entries.Where(x => x.Name == set && !x.Streamed).GroupBy(x => x.Bundle).Select(g => g.First()))
            {
                var caff = ws.LoadResident(e.Bundle);
                var part = caff.PartsOf(e.Symbol).First(x => caff.SectionOf(x).Name == ".data");
                var nd = Strip(part.Data, 8, 4);
                if (nd.Length == part.Data.Length) continue;
                part.Data = nd; part.Size = nd.Length; ws.SaveResident(e.Bundle, caff, $"{set}: part 0x{recId:X8} removed");
                log.Add($"{e.Bundle:x6} {set}: removed");
            }
        }
        foreach (uint sb in idx.Entries.Where(x => x.Streamed && x.Name.StartsWith("aid_misc_banjox_blockset_")).Select(x => x.Bundle).Distinct())
        {
            var arch = ws.LoadStream(sb); bool any = false;
            var setIds = idx.Entries.Where(x => x.Streamed && x.Bundle == sb && x.Name.StartsWith("aid_misc_banjox_blockset_")).Select(x => x.Id).ToHashSet();
            foreach (var en in arch.Entries.Where(x => setIds.Contains(x.Id) && x.Kind == "caff"))
            {
                var sc = CaffFile.Read(en.Data!);
                int sym = sc.Symbols.FindIndex(s => AssetIds.DisplayName(s).StartsWith("aid_misc_banjox_blockset_")) + 1; if (sym == 0) continue;
                var part = sc.PartsOf(sym).First(x => sc.SectionOf(x).Name == ".data");
                var nd = Strip(part.Data, 8, 4); if (nd.Length == part.Data.Length) continue;
                part.Data = nd; part.Size = nd.Length; en.Data = sc.Write(); any = true;
            }
            if (any) { ws.SaveStream(sb, arch, $"blocksets: part 0x{recId:X8} removed"); log.Add($"stream {sb:x6} blocksets: removed"); }
        }
        {
            var caff = ws.LoadResident(common);
            int s = caff.Symbols.FindIndex(x => AssetIds.DisplayName(x) == "aid_misc_banjox_live_networkenum") + 1;
            if (s > 0)
            {
                var part = caff.PartsOf(s).First(x => caff.SectionOf(x).Name == ".data");
                var nd = Strip(part.Data, 20, 0);
                if (nd.Length != part.Data.Length) { part.Data = nd; part.Size = nd.Length; ws.SaveResident(common, caff, $"network list: part 0x{recId:X8} removed"); log.Add("network list: removed"); }
            }
        }
        // assets
        var names = new List<string> { rec };
        if (p.Model != null) { names.Add(ModelAsset(p.Model.Id)); names.Add(HavokAsset(p.Model.Id)); }
        if (p.Attach != null) names.Add(AttachPrefix + p.Attach.Id);
        if (p.Dialog != null) names.Add(DialogPrefix + p.Key);
        foreach (uint b in idx.Entries.Where(e => !e.Streamed && names.Contains(e.Name)).Select(e => e.Bundle).Distinct().ToList())
        {
            var caff = ws.LoadResident(b); var gone = new List<string>();
            foreach (var n in names)
            {
                int s = caff.Symbols.FindIndex(x => AssetIds.DisplayName(x) == n) + 1;
                if (s > 0) { CaffEdit.RemoveAsset(caff, s); gone.Add(n); }
            }
            if (gone.Count > 0) { ws.SaveResident(b, caff, $"part {p.Id}: removed {string.Join(", ", gone)}"); log.Add($"{b:x6}: removed {gone.Count} asset(s)"); }
        }
        {
            var ids = names.Skip(1).Select(n => AssetIds.IdOf(n)!.Value).ToHashSet();
            var arch = ws.LoadStream(common);
            int n0 = arch.Entries.Count; arch.Entries.RemoveAll(e => ids.Contains(e.Id));
            if (arch.Entries.Count != n0) { ws.SaveStream(common, arch, $"part {p.Id}: streamed assets removed"); log.Add($"stream {Common}: {n0 - arch.Entries.Count} entr(ies) removed"); }
        }
        // tier record
        bool tierShared = false;
        if (removeTier && p.Tier != null)
        {
            // keep the tier when another part record still uses the same tier string (e.g. two parts in tier "ultra")
            var cc = ws.LoadResident(common); int toff = Convert.ToInt32(p.TierOffset, 16);
            for (int s = 1; s <= cc.Symbols.Count && !tierShared; s++)
            {
                string n = AssetIds.DisplayName(cc.Symbols[s - 1]);
                if (!n.StartsWith("aid_objparams_banjox_vehicleblock_") || n == rec) continue;
                var d = cc.PartsOf(s).FirstOrDefault(x => cc.SectionOf(x).Name == ".data")?.Data;
                if (d != null && d.Length >= toff + 64 && BE.CStr(d, toff, 64) == p.Tier) tierShared = true;
            }
            if (tierShared) log.Add($"tier {p.Tier} kept: other parts use it");
        }
        if (removeTier && !tierShared && p.Tier != null && p.Grouping != null)
        {
            string g = "aid_misc_banjox_garagegrouping_" + p.Grouping;
            var e = idx.Entries.FirstOrDefault(x => x.Name == g && !x.Streamed);
            if (e != null)
            {
                var caff = ws.LoadResident(e.Bundle);
                var part = caff.PartsOf(e.Symbol).First(x => caff.SectionOf(x).Name == ".data");
                var o = new MemoryStream(); int n = part.Data.Length / TierRecord, kept = 0;
                for (int i = 0; i < n; i++) if (BE.CStr(part.Data, i * TierRecord, 64) != p.Tier) { o.Write(part.Data, i * TierRecord, TierRecord); kept++; }
                if (kept != n) { part.Data = o.ToArray(); part.Size = part.Data.Length; ws.SaveResident(e.Bundle, caff, $"{g}: tier {p.Tier} removed"); log.Add($"{g}: tier {p.Tier} removed"); }
            }
        }
        return log;
    }

    public const string DialogPrefix = "aid_dialog_banjox_garage_description_block_";

    /// <summary>
    /// Garage description (record +0x120): a copy of the template's dialog asset (+8 loctext table id, a 64-byte key
    /// "desc_block_…"; resident only) keyed "desc_block_&lt;Key&gt;", its text "dialog__desc_block_&lt;Key&gt;" added to that table.
    /// </summary>
    static List<string> BuildDialog(Workspace ws, AssetIndex idx, PartSpec p, string rec)
    {
        var log = new List<string>();
        uint common = Convert.ToUInt32(Common, 16);
        var cc = ws.LoadResident(common);
        int ts = cc.Symbols.FindIndex(s => AssetIds.DisplayName(s) == Record(p.Template)) + 1;
        uint tplDialogId = BE.U32(cc.PartsOf(ts).First(x => cc.SectionOf(x).Name == ".data").Data, 0x120);
        var tplE = idx.Entries.FirstOrDefault(e => e.Id == tplDialogId && !e.Streamed) ?? throw new InvalidDataException($"template dialog {tplDialogId:X8} not found");
        string tpl = tplE.Name, nn = DialogPrefix + p.Key, key = "desc_block_" + p.Key;
        if (key.Length > 63) throw new ArgumentException("dialog key too long: " + key);
        uint table = 0;
        foreach (uint b in idx.Entries.Where(e => e.Name == tpl && !e.Streamed).Select(e => e.Bundle).Distinct())
        {
            var caff = ws.LoadResident(b);
            int s0 = caff.Symbols.FindIndex(s => AssetIds.DisplayName(s) == tpl) + 1;
            int ns = caff.Symbols.FindIndex(s => AssetIds.DisplayName(s) == nn) + 1;
            if (ns == 0) ns = CaffEdit.CloneAsset(caff, s0, nn);
            var d = caff.PartsOf(ns).First(x => caff.SectionOf(x).Name == ".data").Data;
            table = BE.U32(d, 8);
            int at = -1;
            for (int o = 0x10; o + 64 <= d.Length && at < 0; o += 4)
                if (d.AsSpan(o, 11).SequenceEqual("desc_block_"u8)) at = o;
            if (at < 0) throw new InvalidDataException($"{tpl}: no desc_block_ key found");
            Array.Clear(d, at, 64); Encoding.ASCII.GetBytes(key).CopyTo(d, at);
            ws.SaveResident(b, caff, $"part {p.Id}: dialog {nn}");
            log.Add($"{b:x6}: dialog {nn} (key {key}, table {table:X8})");
        }
        AddText(ws, "dialog__" + key, p.Dialog!, log, table);
        cc = ws.LoadResident(common);
        int rs = cc.Symbols.FindIndex(s => AssetIds.DisplayName(s) == rec) + 1;
        BE.W32(cc.PartsOf(rs).First(x => cc.SectionOf(x).Name == ".data").Data, 0x120, AssetIds.IdOf(nn)!.Value);
        ws.SaveResident(common, cc, $"part {p.Id}: record +0x120 = {nn}");
        return log;
    }

    public const string AttachPrefix = "aid_avatarhavokdata_banjox_vehicleblock_";

    /// <summary>
    /// Clones the footprint/attach asset <see cref="AttachSpec.Template"/> as <see cref="AttachSpec.Id"/> in every resident
    /// bundle holding it and in the stream archive, applies the attachable rule (<see cref="AttachData.SetAttachable"/>)
    /// and points the part record (+0x12C) at it.
    /// </summary>
    static List<string> BuildAttach(Workspace ws, AssetIndex idx, PartSpec p, string rec)
    {
        var log = new List<string>();
        uint common = Convert.ToUInt32(Common, 16);
        string tpl = AttachPrefix + p.Attach!.Template, nn = AttachPrefix + p.Attach.Id;
        string summary = "";
        void Edit(byte[] d)
        {
            var a = AttachData.Parse(d);
            if (p.Attach.Attachable != null) { a.SetAttachable(p.Attach.Attachable); a.WriteFlags(d); }
            summary = a.ToString();
        }
        foreach (uint b in idx.Entries.Where(e => e.Name == tpl && !e.Streamed).Select(e => e.Bundle).Distinct())
        {
            var caff = ws.LoadResident(b);
            int ts = caff.Symbols.FindIndex(s => AssetIds.DisplayName(s) == tpl) + 1;
            int ns = caff.Symbols.FindIndex(s => AssetIds.DisplayName(s) == nn) + 1;
            if (ns == 0) ns = CaffEdit.CloneAsset(caff, ts, nn);
            else caff.PartsOf(ns).First(x => caff.SectionOf(x).Name == ".data").Data = (byte[])caff.PartsOf(ts).First(x => caff.SectionOf(x).Name == ".data").Data.Clone();
            Edit(caff.PartsOf(ns).First(x => caff.SectionOf(x).Name == ".data").Data);
            ws.SaveResident(b, caff, $"part {p.Id}: attach data {nn}");
            log.Add($"{b:x6}: {nn}: {summary}");
        }
        log.AddRange(AddStreamed(ws, common, tpl, nn, (sc, sym) => { Edit(sc.PartsOf(sym).First(x => sc.SectionOf(x).Name == ".data").Data); return summary; }));
        var cc = ws.LoadResident(common);
        int rs = cc.Symbols.FindIndex(s => AssetIds.DisplayName(s) == rec) + 1;
        BE.W32(cc.PartsOf(rs).First(x => cc.SectionOf(x).Name == ".data").Data, 0x12C, AssetIds.IdOf(nn)!.Value);
        ws.SaveResident(common, cc, $"part {p.Id}: record +0x12C = {nn}");
        return log;
    }

    public static List<string> AddStreamed(Workspace ws, uint bundle, string tplAsset, string newAsset, Func<CaffFile, int, string>? edit)
    {
        var log = new List<string>();
        uint tplId = AssetIds.IdOf(tplAsset) ?? throw new InvalidDataException("no id for " + tplAsset);
        uint newId = AssetIds.IdOf(newAsset) ?? throw new InvalidDataException("no id for " + newAsset);
        var arch = ws.LoadStream(bundle);
        var te = arch.Entries.FirstOrDefault(e => e.Id == tplId && e.Kind == "caff");
        if (te == null) { log.Add($"stream {bundle:x6}: no entry for {tplAsset}, nothing streamed"); return log; }
        var sc = CaffFile.Read(te.Data!);
        int sym = sc.Symbols.FindIndex(s => AssetIds.DisplayName(s) == tplAsset) + 1;
        if (sym == 0) throw new InvalidDataException($"stream entry {tplId:X8} does not hold {tplAsset}");
        CaffEdit.RenameAsset(sc, sym, newAsset);
        string note = edit?.Invoke(sc, sym) ?? "copied";
        var data = sc.Write();
        var ne = arch.Entries.FirstOrDefault(e => e.Id == newId);
        if (ne != null) ne.Data = data; else arch.Entries.Add(new BundleEntry { Id = newId, Data = data });
        ws.SaveStream(bundle, arch, $"streamed {newAsset}");
        log.Add($"stream {bundle:x6}: {newAsset} (0x{newId:X8}, {data.Length} bytes) {(ne != null ? "replaced" : "added")}: {note}");
        return log;
    }

    /// <summary>Loose loctext asset file of id 0x11XXYYZZ: Debug/11/xx/yy/zz.</summary>
    public static string LocTextFile(Workspace ws, uint id) =>
        Path.Combine(ws.Game.Root, "Debug", "11", $"{(id >> 16) & 0xFF:x2}", $"{(id >> 8) & 0xFF:x2}", $"{id & 0xFF:x2}");

    static void AddText(Workspace ws, string name, string text, List<string> log, uint? table = null)
    {
        var files = table != null ? new[] { LocTextFile(ws, table.Value) } : Directory.GetFiles(Path.Combine(ws.Game.Root, "Debug", "11"), "*", SearchOption.AllDirectories);
        foreach (var f in files)
        {
            CaffFile c;
            try { c = CaffFile.Read(File.ReadAllBytes(f)); } catch { continue; }
            if (table == null && !c.Symbols.Any(s => s.Contains("loctext_banjox_blocks"))) continue;
            ws.Snapshot(f);
            var part = c.Parts.First(x => c.SectionOf(x).Name == ".data");
            var t = LocText.Parse(part.Data);
            ushort key = t.AddOrSet(name, text);
            part.Data = t.Write(); part.Size = part.Data.Length;
            File.WriteAllBytes(f, c.Write());
            ws.Log(Path.GetRelativePath(ws.Game.Root, f), $"text {name} = \"{text}\" (key {key:X4})");
            log.Add($"text {name} = \"{text}\" (key {key:X4})");
        }
    }

    const int TierRecord = 204;
    public static string AddTier(Workspace ws, AssetIndex idx, string asset, string tier, string label, string? copyFrom)
    {
        var e = idx.Entries.FirstOrDefault(x => x.Name == asset && !x.Streamed) ?? throw new InvalidDataException(asset + " not found");
        var caff = ws.LoadResident(e.Bundle);
        var part = caff.PartsOf(e.Symbol).First(x => caff.SectionOf(x).Name == ".data");
        var d = part.Data; int n = d.Length / TierRecord;
        for (int i = 0; i < n; i++) if (BE.CStr(d, i * TierRecord, 64) == tier) return $"{asset}: tier {tier} already present";
        int src = copyFrom == null ? n - 1 : Enumerable.Range(0, n).First(i => BE.CStr(d, i * TierRecord, 64) == copyFrom);
        var nd = new byte[d.Length + TierRecord]; Buffer.BlockCopy(d, 0, nd, 0, d.Length); Buffer.BlockCopy(d, src * TierRecord, nd, d.Length, TierRecord);
        Array.Clear(nd, d.Length, 64); Encoding.ASCII.GetBytes(tier).CopyTo(nd, d.Length);
        Array.Clear(nd, d.Length + 0x40, 64); Encoding.ASCII.GetBytes(label).CopyTo(nd, d.Length + 0x40);
        part.Data = nd; part.Size = nd.Length;
        ws.SaveResident(e.Bundle, caff, $"{asset}: tier {tier}");
        return $"{asset}: tier {tier} added ({n + 1} tiers)";
    }

    /// <summary>Sets a u32 of the tier record <paramref name="tier"/> (204-byte records: +0 size key, +0x40 label key,
    /// +0x80 ?, +0x84 description dialog id shown by the parts store, +0x88 "seen" flag name, +0xC8 f32).</summary>
    public static string SetTierField(Workspace ws, AssetIndex idx, string asset, string tier, int off, uint value)
    {
        var e = idx.Entries.FirstOrDefault(x => x.Name == asset && !x.Streamed) ?? throw new InvalidDataException(asset + " not found");
        var caff = ws.LoadResident(e.Bundle);
        var part = caff.PartsOf(e.Symbol).First(x => caff.SectionOf(x).Name == ".data");
        for (int i = 0; i < part.Data.Length / TierRecord; i++)
            if (BE.CStr(part.Data, i * TierRecord, 64) == tier)
            {
                BE.W32(part.Data, i * TierRecord + off, value);
                ws.SaveResident(e.Bundle, caff, $"{asset}: tier {tier} +0x{off:X} = 0x{value:X8}");
                return $"{asset}: tier {tier} +0x{off:X} = 0x{value:X8}";
            }
        return $"{asset}: tier {tier} not found";
    }

    public static string AddToBlockset(Workspace ws, AssetIndex idx, string set, uint partId, uint count)
    {
        int res = 0, str = 0;
        byte[] Add(byte[] d)
        {
            for (int o = 0; o + 8 <= d.Length; o += 8) if (BE.U32(d, o + 4) == partId) { BE.W32(d, o, count); return d; }
            var nd = new byte[d.Length + 8]; Buffer.BlockCopy(d, 0, nd, 0, d.Length); BE.W32(nd, d.Length, count); BE.W32(nd, d.Length + 4, partId); return nd;
        }
        foreach (var e in idx.Entries.Where(x => x.Name == set && !x.Streamed).GroupBy(x => x.Bundle).Select(g => g.First()))
        {
            var caff = ws.LoadResident(e.Bundle);
            var part = caff.PartsOf(e.Symbol).First(x => caff.SectionOf(x).Name == ".data");
            part.Data = Add(part.Data); part.Size = part.Data.Length;
            ws.SaveResident(e.Bundle, caff, $"{set}: part 0x{partId:X8} x{count}"); res++;
        }
        foreach (var b in idx.Entries.Where(x => x.Name == set && x.Streamed).Select(x => x.Bundle).Distinct())
        {
            var arch = ws.LoadStream(b); uint id = idx.Entries.First(x => x.Name == set).Id; bool any = false;
            foreach (var en in arch.Entries.Where(x => x.Id == id && x.Kind == "caff"))
            {
                var sc = CaffFile.Read(en.Data!);
                int sym = sc.Symbols.FindIndex(s => AssetIds.DisplayName(s) == set) + 1; if (sym == 0) continue;
                var part = sc.PartsOf(sym).First(x => sc.SectionOf(x).Name == ".data");
                part.Data = Add(part.Data); part.Size = part.Data.Length; en.Data = sc.Write(); any = true;
            }
            if (any) { ws.SaveStream(b, arch, $"{set}: part 0x{partId:X8}"); str++; }
        }
        return $"{set}: x{count} ({res} resident, {str} streamed copies)";
    }

    /// <summary>aid_misc_banjox_live_networkenum (4f/685374): flat 20-byte records {u32 objparams id, 4, 0, 0, 0}.</summary>
    public static string AddNetworkEnum(Workspace ws, uint partId)
    {
        uint common = Convert.ToUInt32(Common, 16);
        var caff = ws.LoadResident(common);
        int s = caff.Symbols.FindIndex(x => AssetIds.DisplayName(x) == "aid_misc_banjox_live_networkenum") + 1;
        if (s == 0) return "network list not found";
        var part = caff.PartsOf(s).First(x => caff.SectionOf(x).Name == ".data");
        for (int o = 0; o + 20 <= part.Data.Length; o += 20) if (BE.U32(part.Data, o) == partId) return "network list: already listed";
        var nd = new byte[part.Data.Length + 20]; Buffer.BlockCopy(part.Data, 0, nd, 0, part.Data.Length);
        BE.W32(nd, part.Data.Length, partId); BE.W32(nd, part.Data.Length + 4, 4);
        part.Data = nd; part.Size = nd.Length;
        ws.SaveResident(common, caff, $"network list: part 0x{partId:X8}");
        return $"network list: +1 ({nd.Length / 20} records)";
    }

    public static PartSpec[] LoadSpecs(string path)
    {
        var opt = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        var txt = File.ReadAllText(path);
        return txt.TrimStart().StartsWith("[") ? JsonSerializer.Deserialize<PartSpec[]>(txt, opt)! : new[] { JsonSerializer.Deserialize<PartSpec>(txt, opt)! };
    }
}
