using System.Numerics;
using NB.Core.Formats;
using NB.Core.Models;

namespace NB.Core.World;

/// <summary>
/// Builds a new, unique scenery model inside a world bundle from a template model:
/// clone (model + its aid_havok_ collision) → retarget textures → import geometry by material → LOD/cull distances →
/// collision (none, bounding box or mesh). The result can be placed with <see cref="InstanceEditor.AddModelInstance"/>.
/// </summary>
public static class ModelFactory
{
    public sealed class Options
    {
        public string Template = "";            // display name of the template model in the bundle
        public string Name = "";                // new model = aid_model_banjox_<Name>, collision = aid_havok_banjox_<Name>
        public List<ImportMesh> Meshes = new();
        /// <summary>Template texture stem → replacement stem (a texture already in the bundle).</summary>
        public Dictionary<string, string> Retarget = new();
        /// <summary>Retarget targets that are complete resident textures (TextureFactory): used by exact name for both
        /// the mip and top entries.</summary>
        public HashSet<string> ExactTextures = new();
        /// <summary>When set, every imported mesh is assigned to this material (texture stem after retargeting).
        /// Otherwise mesh names must equal texture stems.</summary>
        public string? SingleMaterial;
        /// <summary>When set, every normal / bump / parallax texture of the clone is retargeted to this texture
        /// (e.g. the game's flat "nuttyacres_plainnormal"), so the template's surface detail does not show on new geometry.</summary>
        public string? FlatNormal;
        /// <summary>When set, ambient-occlusion textures (baked for the template's shape) are retargeted to this one
        /// (a white texture), and specular maps to <see cref="Specular"/>.</summary>
        public string? NeutralAo, Specular;
        public float CullDistance = 1e6f;
        public float LodScale = 1f;
        /// <summary>Always draw LOD 0 (the imported geometry goes to every LOD, and a template's lower LODs may use
        /// untextured shaders or vertex formats without UVs).</summary>
        public bool KeepLod0 = true;
        /// <summary>"none", "box" (bounding box of the meshes) or "mesh" (the meshes, or <see cref="CollisionMeshes"/>).</summary>
        public string Collision = "box";
        public List<ImportMesh>? CollisionMeshes;
        /// <summary>Havok asset cloned for the new model's collision. Must be a mesh collision asset (wrapper type-1 entry);
        /// the template's own aid_havok_ asset is used when it is one, otherwise this.</summary>
        public string CollisionTemplate = DefaultCollisionTemplate;
    }

    /// <summary>Showdown Town's telegraph pole: a plain mesh collision asset, verified with collision import in Xenia.</summary>
    public const string DefaultCollisionTemplate = "aid_havok_banjox_background_showdowntown_showdowntownreferences_props_telegraphpole";

    public sealed record Result(int Symbol, uint ModelId, uint? HavokId, List<string> Notes);

    public static string ModelName(string name) => "aid_model_banjox_" + name;
    public static string HavokName(string name) => "aid_havok_banjox_" + name;

    public static Result Create(CaffFile caff, Options o)
    {
        var notes = new List<string>();
        int tsym = caff.Symbols.FindIndex(s => AssetIds.DisplayName(s) == o.Template) + 1;
        if (tsym == 0) throw new InvalidDataException($"template {o.Template} not in this bundle");
        string mName = ModelName(o.Name), hName = HavokName(o.Name);
        int sym = CaffEdit.CloneAsset(caff, tsym, mName);
        uint modelId = AssetIds.IdOf(mName)!.Value;
        // collision asset
        uint? havokId = null;
        string tHavok = "aid_havok_" + o.Template["aid_model_".Length..];
        int hsym = caff.Symbols.FindIndex(s => AssetIds.DisplayName(s) == tHavok) + 1;
        int newH = 0;
        if (o.Collision != "none")
        {
            bool IsMesh(int s) { var v = new AssetView(caff, s); return v.Has(".data") && NB.Core.Havok.HkCollisionImport.TypeOneEntry(v.Data(".data")) >= 0 && !NB.Core.Havok.HkCollisionImport.IsBreakable(v.Data(".data")); }
            if (hsym == 0 || !IsMesh(hsym))
            {
                int alt = caff.Symbols.FindIndex(s => AssetIds.DisplayName(s) == o.CollisionTemplate) + 1;
                notes.Add(hsym == 0 ? $"template has no {tHavok}; collision cloned from {o.CollisionTemplate}" : $"{tHavok} is not a plain mesh collision asset; collision cloned from {o.CollisionTemplate}");
                hsym = alt;
            }
            if (hsym == 0) notes.Add("no collision template in this bundle: no collision");
            else { newH = CaffEdit.CloneAsset(caff, hsym, hName); havokId = AssetIds.IdOf(hName); }
        }
        sym = caff.Symbols.FindIndex(s => s == mName) + 1;   // symbol ids shift when a later clone is inserted before the manifest
        // textures
        foreach (var (from, to) in o.Retarget)
        {
            int n = ModelEdit.RetargetTexture(caff, sym, from, to, o.ExactTextures.Contains(to) ? true : null);
            notes.Add($"texture {from} -> {to}: {n} entr{(n == 1 ? "y" : "ies")}");
        }
        foreach (var st in ModelEdit.TextureStems(caff, sym))
        {
            if (o.NeutralAo != null && st.Contains("ambientocclusion") && st != o.NeutralAo) notes.Add($"AO {st} -> neutral: {ModelEdit.RetargetTexture(caff, sym, st, o.NeutralAo)} entries");
            if (o.Specular != null && st.Contains("specular") && st != o.Specular) notes.Add($"specular {st} -> {o.Specular}: {ModelEdit.RetargetTexture(caff, sym, st, o.Specular)} entries");
        }
        if (o.FlatNormal != null)
            foreach (var st in ModelEdit.TextureStems(caff, sym).Where(t => t.Contains("normal") || t.Contains("_nm") || t.Contains("bump") || t.Contains("parallax") || t.EndsWith("_norm", StringComparison.Ordinal) || t.Contains("_norm_")))
                if (st != o.FlatNormal) notes.Add($"normal map {st} -> flat: {ModelEdit.RetargetTexture(caff, sym, st, o.FlatNormal)} entries");
        // geometry
        if (o.Meshes.Count > 0)
        {
            var meshes = o.Meshes;
            if (o.SingleMaterial != null) foreach (var m in meshes) m.Name = o.SingleMaterial;
            var r = ModelImporter.Replace(caff, sym, meshes);
            notes.AddRange(r.Notes);
            notes.Add($"geometry: {r.Vertices} vertices, {r.Triangles} triangles in {r.VertexBuffers} vertex buffer(s)");
        }
        var lod = ModelEdit.SetLodDistances(caff, sym, o.CullDistance, o.LodScale, o.KeepLod0);
        notes.Add($"LOD: {lod.Groups} group(s), {lod.Levels} level(s) changed (cull {o.CullDistance:G4}, scale {o.LodScale:G3})");
        // collision shape
        if (newH > 0 && havokId != null && o.Collision != "clone")
        {
            newH = caff.Symbols.FindIndex(s => s == hName) + 1;
            var src = o.Collision == "mesh" ? (o.CollisionMeshes ?? o.Meshes) : o.Meshes;
            var (P, T) = NB.Core.Havok.HkCollisionImport.Merge(src);
            if (P.Count == 0) notes.Add("collision: no geometry");
            else
            {
                if (o.Collision == "box") (P, T) = NB.Core.Havok.HkCollisionImport.Box(P.Aggregate(Vector3.Min), P.Aggregate(Vector3.Max));
                var cr = NB.Core.Havok.HkCollisionImport.Replace(caff, newH, P, T, null, allowBreakable: true);
                notes.Add($"collision ({o.Collision}): {T.Count / 3} triangles");
            }
        }
        return new Result(caff.Symbols.FindIndex(s => s == mName) + 1, modelId, havokId, notes);
    }
}
