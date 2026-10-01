using System.Numerics;
using System.Text.Json;
using NB.Core.Formats;
using NB.Core.Models;
using NB.Core.Project;
using NB.Core.Textures;

namespace NB.Studio.Panels;

/// <summary>Exports one indexed asset to a folder in the most useful format available.</summary>
public static class AssetExporter
{
    public static string Export(Workspace ws, AssetEntry a, string dir)
    {
        Directory.CreateDirectory(dir);
        if (a.Streamed)
        {
            var arch = ws.LoadStream(a.Bundle);
            var e = arch.Entries.First(x => x.Id == a.Id && x.Data != null);
            if (e.Kind == "xwb") { var p = Path.Combine(dir, a.Name + ".xwb"); File.WriteAllBytes(p, e.Data!); return p; }
            if (e.Kind == "caff")
            {
                var sc = CaffFile.Read(e.Data!);
                for (int s = 1; s <= sc.Symbols.Count; s++)
                {
                    if (AssetIds.DisplayName(sc.Symbols[s - 1]) != a.Name) continue;
                    var png = TryTexture(sc, s, dir, a.Name); if (png != null) return png;
                }
                var raw = Path.Combine(dir, a.Name + ".caff"); File.WriteAllBytes(raw, e.Data!); return raw;
            }
            var bin = Path.Combine(dir, a.Name + ".bin"); File.WriteAllBytes(bin, e.Data!); return bin;
        }
        var caff = ws.LoadResident(a.Bundle);
        if (a.Type == "texture") { var png = TryTexture(caff, a.Symbol, dir, a.Name); if (png != null) return png; }
        if (a.Type == "model")
        {
            var m = ModelAsset.Parse(caff, a.Symbol);
            var p = Path.Combine(dir, a.Name + ".obj");
            ObjExporter.Write(p, a.Name, m.Draws.Select(d => (d, Matrix4x4.Identity)));
            // Characters: also an FBX with the skeleton as an armature and the skin weights bound to it.
            var skel = Skeleton.Parse(caff.PartsOf(a.Symbol).First(x => caff.SectionOf(x).Name == ".data").Data);
            if (skel != null)
            {
                var fbx = Path.Combine(dir, a.Name + ".fbx");
                FbxExporter.Write(fbx, a.Name, m.Draws.Select(d => (d, Matrix4x4.Identity)), skeleton: skel);
                return fbx;
            }
            return p;
        }
        return ExportRaw(caff, a.Symbol, dir, a.Name);
    }

    public static string? TryTexture(CaffFile caff, int symbol, string dir, string name)
    {
        var parts = caff.PartsOf(symbol).ToList();
        var cpu = parts.FirstOrDefault(p => caff.SectionOf(p).Name == ".data");
        var gpu = parts.FirstOrDefault(p => caff.SectionOf(p).Name == ".texturegpu");
        if (cpu == null || gpu == null || !TextureHeader.IsTexture(cpu.Data)) return null;
        var t = new TextureAsset(cpu.Data, gpu.Data);
        var (rgba, w, h) = t.Decode(0);
        var p = Path.Combine(dir, name + ".png");
        ImageIO.Save(p, rgba, w, h);
        return p;
    }

    /// <summary>Writes each part as &lt;name&gt;.&lt;section&gt;.bin plus a JSON list of its pointers.</summary>
    public static string ExportRaw(CaffFile caff, int symbol, string dir, string name)
    {
        var view = new AssetView(caff, symbol);
        var info = new Dictionary<string, object>();
        foreach (var (sec, pid) in view.PartBySection)
        {
            File.WriteAllBytes(Path.Combine(dir, $"{name}{sec}.bin"), view.Part(pid).Data);
            info[sec] = view.Pointers.Where(kv => kv.Key.Part == pid).OrderBy(kv => kv.Key.Offset)
                .Select(kv => new { offset = kv.Key.Offset, target = view.IsOwnPart(kv.Value) ? view.SectionOfPart(kv.Value) : $"{AssetIds.DisplayName(caff.Symbols[view.Part(kv.Value).Symbol - 1])}{view.SectionOfPart(kv.Value)}" }).ToList();
        }
        var j = Path.Combine(dir, name + ".pointers.json");
        File.WriteAllText(j, JsonSerializer.Serialize(new { asset = view.Name, pointers = info }, new JsonSerializerOptions { WriteIndented = true }));
        return j;
    }
}
