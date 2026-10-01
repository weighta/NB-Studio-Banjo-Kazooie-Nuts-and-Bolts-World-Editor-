using NB.Core.Formats;
using NB.Core.Project;

namespace NB.Core.Textures;

/// <summary>
/// Replaces a texture's pixels everywhere it is stored in one bundle: the resident "…mip" asset (Bundle/4f)
/// and the streamed "…top" asset (Bundle/50). Format, dimensions and level count are kept, so no size or
/// offset changes are needed; the image is resized to the stored dimensions if necessary.
/// </summary>
public static class TextureReplacer
{
    public sealed record Result(int ResidentAssets, int StreamedAssets, List<string> Notes);

    public static string Stem(string name)
    {
        var n = AssetIds.DisplayName(name);
        return n.EndsWith("mip") || n.EndsWith("top") ? n[..^3] : n;
    }

    public static Result Replace(Workspace ws, uint bundle, string textureName, byte[] rgba, int w, int h)
    {
        var caff = ws.LoadResident(bundle);
        var r = ReplaceLoaded(ws, bundle, caff, textureName, rgba, w, h);
        if (r.ResidentAssets > 0) ws.SaveResident(bundle, caff, $"replaced texture {Stem(textureName)} ({r.ResidentAssets} resident asset(s))");
        return r;
    }

    /// <summary>
    /// Like <see cref="Replace"/>, but the resident assets are replaced in <paramref name="caff"/> — the bundle already
    /// loaded by the caller (e.g. the open world), which the caller saves. The streamed top level is saved here.
    /// </summary>
    public static Result ReplaceLoaded(Workspace ws, uint bundle, CaffFile caff, string textureName, byte[] rgba, int w, int h)
    {
        var notes = new List<string>();
        string stem = Stem(textureName);
        int resident = 0, streamed = 0;
        foreach (var cand in new[] { stem + "mip", stem + "top", stem })
        {
            int s = caff.Symbols.FindIndex(x => AssetIds.DisplayName(x) == cand) + 1;
            if (s == 0) continue;
            if (ReplaceIn(caff, s, rgba, w, h, notes)) resident++;
        }

        // streamed top level
        uint topId = AssetIds.Make(0x01, (stem + "top")["aid_texture_".Length..]);
        var arch = ws.LoadStream(bundle);
        bool archChanged = false;
        foreach (var e in arch.Entries.Where(e => e.Id == topId && e.Kind == "caff"))
        {
            var sc = CaffFile.Read(e.Data!);
            for (int s = 1; s <= sc.Symbols.Count; s++)
                if (AssetIds.DisplayName(sc.Symbols[s - 1]) == stem + "top" && ReplaceIn(sc, s, rgba, w, h, notes)) { streamed++; archChanged = true; }
            if (archChanged) e.Data = sc.Write();
        }
        if (archChanged) ws.SaveStream(bundle, arch, $"replaced streamed top level of {stem}");
        if (resident + streamed == 0) notes.Add($"no texture named {stem} in bundle {bundle:x6}");
        return new Result(resident, streamed, notes);
    }

    /// <summary>
    /// Replaces many textures of one bundle with a single save of the resident bundle and of the stream archive.
    /// Returns (resident assets, streamed assets) replaced in total.
    /// </summary>
    public static Result ReplaceMany(Workspace ws, uint bundle, IEnumerable<(string Name, byte[] Rgba, int W, int H)> items)
    {
        var notes = new List<string>();
        var list = items.ToList();
        int resident = 0, streamed = 0;
        var caff = ws.LoadResident(bundle);
        var bySym = new Dictionary<string, int>();
        for (int s = 1; s <= caff.Symbols.Count; s++) bySym[AssetIds.DisplayName(caff.Symbols[s - 1])] = s;
        foreach (var (name, rgba, w, h) in list)
        {
            string stem = Stem(name);
            foreach (var cand in new[] { stem + "mip", stem + "top", stem })
                if (bySym.TryGetValue(cand, out int s) && ReplaceIn(caff, s, rgba, w, h, notes)) resident++;
        }
        if (resident > 0) ws.SaveResident(bundle, caff, $"replaced {list.Count} texture(s) ({resident} resident asset(s))");
        var tops = list.GroupBy(i => AssetIds.Make(0x01, (Stem(i.Name) + "top")["aid_texture_".Length..])).ToDictionary(g => g.Key, g => g.Last());
        var arch = ws.LoadStream(bundle);
        bool archChanged = false;
        foreach (var e in arch.Entries.Where(e => tops.ContainsKey(e.Id) && e.Kind == "caff"))
        {
            var it = tops[e.Id];
            var sc = CaffFile.Read(e.Data!);
            bool changed = false;
            for (int s = 1; s <= sc.Symbols.Count; s++)
                if (AssetIds.DisplayName(sc.Symbols[s - 1]) == Stem(it.Name) + "top" && ReplaceIn(sc, s, it.Rgba, it.W, it.H, notes)) { streamed++; changed = true; }
            if (changed) { e.Data = sc.Write(); archChanged = true; }
        }
        if (archChanged) ws.SaveStream(bundle, arch, $"replaced streamed top levels of {streamed} texture(s)");
        return new Result(resident, streamed, notes);
    }

    static bool ReplaceIn(CaffFile caff, int symbol, byte[] rgba, int w, int h, List<string> notes)
    {
        var parts = caff.PartsOf(symbol).ToList();
        var cpu = parts.FirstOrDefault(p => caff.SectionOf(p).Name == ".data");
        var gpu = parts.FirstOrDefault(p => caff.SectionOf(p).Name == ".texturegpu");
        if (cpu == null || gpu == null || !TextureHeader.IsTexture(cpu.Data)) return false;
        var t = new TextureAsset(cpu.Data, gpu.Data);
        if (t.Header.Kind != 0) { notes.Add($"{caff.Symbols[symbol - 1]}: {t.Header.KindName} textures cannot be replaced yet, skipped"); return false; }
        if (t.ExpectedGpuSize != gpu.Data.Length) { notes.Add($"{caff.Symbols[symbol - 1]}: layout mismatch, skipped"); return false; }
        var blob = TextureAsset.EncodeBlob(t.Header, t.Levels, rgba, w, h);
        if (blob.Length != gpu.Data.Length) { notes.Add("encoded size differs, skipped"); return false; }
        gpu.Data = blob;
        notes.Add($"{AssetIds.DisplayName(caff.Symbols[symbol - 1])}: {t.Header} re-encoded ({t.Levels.Count} level(s))");
        return true;
    }
}
