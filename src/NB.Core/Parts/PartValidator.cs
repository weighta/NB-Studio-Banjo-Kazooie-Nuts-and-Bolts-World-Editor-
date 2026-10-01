using System.Text.RegularExpressions;
using NB.Core.Formats;
using NB.Core.IO;
using NB.Core.Models;
using NB.Core.Project;
using NB.Core.Textures;

namespace NB.Core.Parts;

/// <summary>Checks a <see cref="PartFactory.PartSpec"/> against the workspace before it is built.</summary>
public static class PartValidator
{
    public sealed class Report
    {
        public readonly List<string> Errors = new(), Warnings = new(), Info = new();
        public bool Ok => Errors.Count == 0;
        public IEnumerable<string> Lines => Errors.Select(e => "ERROR: " + e).Concat(Warnings.Select(w => "warning: " + w)).Concat(Info);
    }

    /// <summary>What a template part brings: record class/size, model, the model's diffuse texture stems, footprint.</summary>
    public sealed class TemplateInfo
    {
        public string Record = "", Model = "", Class = "";
        public int RecordSize;
        public byte[] Data = Array.Empty<byte>();
        public List<string> DiffuseStems = new();
        public AttachData? Attach;
        public string AttachAsset = "";
    }

    static readonly Regex Ident = new("^[a-z0-9_]+$");

    public static TemplateInfo? Template(Workspace ws, AssetIndex idx, string templateSuffix, string? modelTemplate = null)
    {
        string rec = PartFactory.Record(templateSuffix);
        var e = idx.Entries.FirstOrDefault(x => x.Name == rec && !x.Streamed);
        if (e == null) return null;
        var caff = ws.LoadResident(e.Bundle);
        var d = caff.PartsOf(e.Symbol).First(p => caff.SectionOf(p).Name == ".data").Data;
        var t = new TemplateInfo { Record = rec, RecordSize = d.Length, Data = d };
        var ahd = idx.Entries.FirstOrDefault(x => x.Id == BE.U32(d, 0x12C) && !x.Streamed);
        if (ahd != null)
        {
            var ac = ws.LoadResident(ahd.Bundle);
            t.AttachAsset = ahd.Name;
            try { t.Attach = AttachData.Parse(ac.PartsOf(ahd.Symbol).First(p => ac.SectionOf(p).Name == ".data").Data); } catch { }
        }
        string? model = modelTemplate != null ? PartFactory.ModelAsset(modelTemplate) : idx.Entries.FirstOrDefault(x => x.Id == BE.U32(d, 0x124))?.Name;
        var me = model == null ? null : idx.Entries.FirstOrDefault(x => x.Name == model && !x.Streamed);
        if (me != null)
        {
            t.Model = me.Name;
            var mc = ws.LoadResident(me.Bundle);
            var ma = ModelAsset.Parse(mc, me.Symbol);
            t.DiffuseStems = ma.Draws.Select(x => ObjExporter.DiffuseTexture(x)).Where(x => x != null)
                .Select(x => ObjExporter.TextureFileStem(x!)).Distinct().ToList();
        }
        return t;
    }

    public static Report Validate(Workspace ws, AssetIndex idx, PartFactory.PartSpec p, string baseDir)
    {
        var r = new Report();
        string P(string rel) => Path.IsPathRooted(rel) ? rel : Path.Combine(baseDir, rel);
        void Id(string what, string? v, int max = 63)
        {
            if (string.IsNullOrEmpty(v)) { r.Errors.Add($"{what} is empty"); return; }
            if (!Ident.IsMatch(v)) r.Errors.Add($"{what} '{v}': use lower-case letters, digits and '_' only");
            if (v.Length > max) r.Errors.Add($"{what} '{v}' is longer than {max} characters");
        }
        Id("part id", p.Id, 63 - "vehicleblock_".Length);
        Id("key", p.Key, 63 - "desc_block_".Length);
        if (p.Tier != null) Id("tier", p.Tier);
        if (string.IsNullOrWhiteSpace(p.Name)) r.Errors.Add("display name is empty");
        if (p.Description?.Length > 63) r.Errors.Add("developer description is longer than 63 characters");
        if (p.Dialog?.Length > 240) r.Warnings.Add("garage description is long (over 240 characters); the store box shows about two lines");

        var t = Template(ws, idx, p.Template, p.Model?.Template);
        if (t == null) { r.Errors.Add($"template part '{p.Template}' not found (aid_objparams_banjox_vehicleblock_{p.Template})"); return r; }
        r.Info.Add($"template {t.Record}: {t.RecordSize} bytes, model {t.Model}, footprint {t.Attach?.ToString() ?? "?"}");
        if (idx.Entries.Any(x => x.Name == PartFactory.Record(p.Id) && !x.Streamed)) r.Info.Add("the part already exists in this workspace: building updates it");

        // part name keys (the blocks text table resolves names by a 16-bit hash)
        foreach (var name in new[] { "block__" + p.Key }.Concat(p.Tier != null ? new[] { "block__group_" + p.Tier } : Array.Empty<string>()))
        {
            ushort k = LocText.KeyOf(name);
            foreach (var f in Directory.GetFiles(Path.Combine(ws.Game.Root, "Debug", "11"), "*", SearchOption.AllDirectories))
            {
                CaffFile c; try { c = CaffFile.Read(File.ReadAllBytes(f)); } catch { continue; }
                if (!c.Symbols.Any(s => s.Contains("loctext_banjox_blocks"))) continue;
                var lt = LocText.Parse(c.Parts.First(x => c.SectionOf(x).Name == ".data").Data);
                if (lt.Names.TryGetValue(k, out var other) && other != name) r.Errors.Add($"text key of '{name}' (0x{k:X4}) collides with '{other}': choose another key/tier");
            }
        }
        // stats
        foreach (var (k, v) in p.Set)
        {
            int off; try { off = Convert.ToInt32(k, 16); } catch { r.Errors.Add($"field offset '{k}' is not hex"); continue; }
            if (off < 0 || off + 4 > t.RecordSize) r.Errors.Add($"field +0x{off:X} is outside the {t.RecordSize}-byte record");
            if (v.Length < 3 || v[1] != ':' || "fuhs".IndexOf(v[0]) < 0) r.Errors.Add($"field +0x{k}: value '{v}' must be f:/u:/h:/s:");
        }
        // garage registration
        if (p.Grouping != null)
        {
            string g = "aid_misc_banjox_garagegrouping_" + p.Grouping;
            var ge = idx.Entries.FirstOrDefault(x => x.Name == g && !x.Streamed);
            if (ge == null) r.Errors.Add($"grouping '{p.Grouping}' not found");
            else if (p.GroupingCopyFrom != null)
            {
                var gc = ws.LoadResident(ge.Bundle); var gd = gc.PartsOf(ge.Symbol).First(x => gc.SectionOf(x).Name == ".data").Data;
                var tiers = Enumerable.Range(0, gd.Length / 204).Select(i => BE.CStr(gd, i * 204, 64)).ToList();
                if (!tiers.Contains(p.GroupingCopyFrom) && !tiers.Contains(p.Tier ?? "")) r.Errors.Add($"grouping '{p.Grouping}' has no tier '{p.GroupingCopyFrom}' (tiers: {string.Join(", ", tiers)})");
            }
        }
        else if (p.Tier != null) r.Warnings.Add("no grouping: a new tier string that is not registered is not listed by the parts store");
        foreach (var set in p.Blocksets.Keys)
            if (!idx.Entries.Any(x => x.Name == "aid_misc_banjox_blockset_" + set)) r.Errors.Add($"blockset '{set}' not found");
        if (p.Blocksets.Count == 0) r.Warnings.Add("no blocksets: the part is in nobody's inventory");

        // footprint
        AttachData? fp = t.Attach;
        if (p.Attach != null)
        {
            var ae = idx.Entries.FirstOrDefault(x => x.Name == PartFactory.AttachPrefix + p.Attach.Template && !x.Streamed);
            if (ae == null) r.Errors.Add($"footprint template '{p.Attach.Template}' not found");
            else
            {
                var ac = ws.LoadResident(ae.Bundle);
                fp = AttachData.Parse(ac.PartsOf(ae.Symbol).First(x => ac.SectionOf(x).Name == ".data").Data);
                try { if (p.Attach.Attachable != null) fp.SetAttachable(p.Attach.Attachable); } catch (Exception e) { r.Errors.Add("attach rule: " + e.Message); }
                if (fp.Points.All(x => !x.Attachable)) r.Errors.Add("no attachable face: the part cannot be connected to a vehicle");
                Id("footprint id", p.Attach.Id, 63 - "vehicleblock_".Length);
            }
        }
        // model
        if (p.Model != null)
        {
            Id("model id", p.Model.Id, 63 - "vehicleparts_".Length);
            foreach (var (name, png) in p.Model.Textures)
            {
                if (!File.Exists(P(png))) { r.Errors.Add($"texture file {png} not found"); continue; }
                try
                {
                    var (_, w, h) = ImageIO.Load(P(png));
                    if ((w & (w - 1)) != 0 || (h & (h - 1)) != 0) r.Errors.Add($"texture {png}: {w}x{h} is not a power of two");
                    if (w > 1024 || h > 1024) r.Warnings.Add($"texture {png}: {w}x{h} is large for console memory");
                }
                catch (Exception e) { r.Errors.Add($"texture {png}: {e.Message}"); }
            }
            foreach (var to in p.Model.Retarget.Values)
                if (!p.Model.Textures.ContainsKey(to) && !idx.Entries.Any(x => x.Name.StartsWith(to))) r.Errors.Add($"retarget target {to} is neither a new texture nor an existing one");
            foreach (var from in p.Model.Retarget.Keys)
                if (!t.DiffuseStems.Contains(from)) r.Warnings.Add($"retarget source {from} is not a diffuse texture of {t.Model}");
            if (!string.IsNullOrEmpty(p.Model.Obj))
            {
                if (!File.Exists(P(p.Model.Obj))) r.Errors.Add($"model file {p.Model.Obj} not found");
                else
                {
                    List<ImportMesh> meshes;
                    try { meshes = ObjReader.ReadAny(P(p.Model.Obj)); }
                    catch (Exception e) { r.Errors.Add($"model {p.Model.Obj}: {e.Message}"); meshes = new(); }
                    int tris = meshes.Sum(m => m.Triangles.Count / 3);
                    r.Info.Add($"model {Path.GetFileName(p.Model.Obj)}: {meshes.Count} material(s), {tris} triangles");
                    if (tris > 20000) r.Warnings.Add($"{tris} triangles: heavy for a vehicle part (shipped parts use 1k–10k)");
                    foreach (var m in meshes)
                    {
                        if (!p.Model.Materials.TryGetValue(m.Name, out var stem)) { r.Errors.Add($"OBJ material '{m.Name}' is not mapped to a template material"); continue; }
                        if (!t.DiffuseStems.Contains(stem)) r.Errors.Add($"material '{m.Name}' → {stem}: not a diffuse texture of {t.Model} ({string.Join(", ", t.DiffuseStems.Select(s => s.Replace("aid_texture_banjox_", "")))})");
                    }
                    if (fp != null && meshes.Count > 0)
                    {
                        var mn = new System.Numerics.Vector3(meshes.Min(m => m.Bounds().Min.X), meshes.Min(m => m.Bounds().Min.Y), meshes.Min(m => m.Bounds().Min.Z));
                        var mx = new System.Numerics.Vector3(meshes.Max(m => m.Bounds().Max.X), meshes.Max(m => m.Bounds().Max.Y), meshes.Max(m => m.Bounds().Max.Z));
                        var lo = new System.Numerics.Vector3(fp.XMin - 0.5f, fp.YMin - 0.5f, fp.ZMin - 0.5f);
                        var hi = new System.Numerics.Vector3(fp.XMax + 0.5f, fp.YMax + 0.5f, fp.ZMax + 0.5f);
                        const float tol = 0.02f;
                        if (mn.X < lo.X - tol || mn.Y < lo.Y - tol || mn.Z < lo.Z - tol || mx.X > hi.X + tol || mx.Y > hi.Y + tol || mx.Z > hi.Z + tol)
                            r.Warnings.Add($"model bounds {mn:0.00}..{mx:0.00} exceed the {fp.Size.X}x{fp.Size.Y}x{fp.Size.Z} footprint {lo}..{hi} (it will overlap neighbouring parts)");
                    }
                }
            }
        }
        return r;
    }
}
