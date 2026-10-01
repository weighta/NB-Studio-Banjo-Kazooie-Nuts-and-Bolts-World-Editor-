using NB.Core.Formats;
using NB.Core.IO;

namespace NB.Core.Models;

/// <summary>
/// Frees resident memory held by model vertex/index data (the ".gpu" part), which is most of a bundle's size.
///
/// A model reaches its .gpu part only through three kinds of (pointer, byte size) pairs in .data (FORMATS §6):
///   VB record:          +8 → .gpu, +0xC size
///   GPU buffer table:   R+0x48 → entries (→ VB record+4, +4 → .gpu, +8 size), count R+0x50
///   IB table:           R+0x54 → entries (→ IB object, +4 → .gpu, +8 byte size, +0xC format), count R+0x58
/// Both operations first check that every relocated pointer into .gpu is one of these; otherwise the model is left alone.
/// <list type="bullet">
/// <item><see cref="Compact"/>: drops bytes no pair references (e.g. the original data an import left in place) and
/// repoints the pairs. What the model draws is unchanged.</item>
/// <item><see cref="StripGeometry"/>: every vertex and index buffer points at one block of zeros (all triangles
/// degenerate), so the model keeps its draws, materials, nodes and name but holds almost no geometry. For models only
/// hidden scenery uses (B24): the objects and their collision stay, as game code looks them up by name (B10).</item>
/// </list>
/// </summary>
public static class GpuCompactor
{
    readonly record struct Ref(int PtrOff, int SizeOff);

    /// <summary>The (pointer, size) pairs into .gpu, or null (with the reason) when the model has other .gpu pointers.</summary>
    static List<Ref>? Refs(ModelAsset m, out string why)
    {
        why = "";
        var v = m.View;
        if (!v.Has(".gpu") || !v.Has(".data")) { why = "no .gpu/.data part"; return null; }
        if (m.ResourceHeader < 0) { why = "no resource header"; return null; }
        var d = v.Data(".data");
        int R = m.ResourceHeader, dataPid = v.PartId(".data"), gpuPid = v.PartId(".gpu");
        if (v.PtrAt(".data", R + 0x48) is not { } vbt || v.PtrAt(".data", R + 0x54) is not { } ibt) { why = "no buffer tables"; return null; }
        int nVb = BE.S32(d, R + 0x50), nIb = BE.S32(d, R + 0x58);
        var refs = new List<Ref>();
        for (int i = 0; i < nVb; i++)
        {
            int e = vbt.Offset + 12 * i;
            refs.Add(new(e + 4, e + 8));
            if (v.PtrAt(".data", e) is not { } rec) { why = $"VB table entry {i} has no record"; return null; }
            refs.Add(new(rec.Offset - 4 + 8, rec.Offset - 4 + 12));
        }
        for (int i = 0; i < nIb; i++) { int e = ibt.Offset + 16 * i; refs.Add(new(e + 4, e + 8)); }
        var known = refs.Select(r => r.PtrOff).ToHashSet();
        foreach (var ((part, off), to) in v.Pointers)
            if (to == gpuPid && (part != dataPid || !known.Contains(off))) { why = $"other pointer into .gpu at {v.SectionOfPart(part)}+0x{off:X}"; return null; }
        foreach (var r in refs)
            if (v.PtrAt(".data", r.PtrOff)?.Part != gpuPid) { why = $"pair at .data+0x{r.PtrOff:X} does not point into .gpu"; return null; }
        return refs.DistinctBy(r => r.PtrOff).ToList();
    }

    /// <summary>Removes unreferenced .gpu bytes. Returns the bytes freed (0 when nothing to do, -1 when not possible).</summary>
    public static int Compact(CaffFile caff, int symbol, out string why)
    {
        var m = ModelAsset.Parse(caff, symbol);
        var refs = Refs(m, out why);
        if (refs == null) return -1;
        var d = m.View.Data(".data");
        var gpuPart = m.View.Part(m.View.PartId(".gpu"));
        var g = gpuPart.Data;
        // referenced ranges, merged (gaps under 128 bytes are kept to avoid needless splits)
        var ranges = refs.Select(r => (Start: BE.S32(d, r.PtrOff), End: BE.S32(d, r.PtrOff) + Math.Max(0, BE.S32(d, r.SizeOff))))
                         .Where(x => x.Start >= 0 && x.End <= g.Length).OrderBy(x => x.Start).ToList();
        if (ranges.Count != refs.Count) { why = "a pair points outside .gpu"; return -1; }
        var merged = new List<(int Start, int End)>();
        foreach (var r in ranges)
            if (merged.Count > 0 && r.Start <= merged[^1].End + 128) merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, r.End));
            else merged.Add(r);
        // new layout: each range keeps its offset modulo 128 (vertex/index alignment and cache-line position)
        var map = new List<(int Start, int End, int To)>();
        int at = 0;
        foreach (var (s, e) in merged)
        {
            int to = at + ((s % 128 - at % 128 + 128) % 128);
            map.Add((s, e, to)); at = to + (e - s);
        }
        int size = (at + 127) & ~127;
        if (size >= g.Length) return 0;
        var ng = new byte[size];
        foreach (var (s, e, to) in map) Buffer.BlockCopy(g, s, ng, to, e - s);
        foreach (var r in refs)
        {
            int p = BE.S32(d, r.PtrOff);
            var (s, _, to) = map.Last(x => x.Start <= p);
            BE.W32(d, r.PtrOff, to + (p - s));
        }
        int freed = g.Length - size;
        gpuPart.Data = ng; gpuPart.Size = ng.Length;
        return freed;
    }

    /// <summary>Points every vertex and index buffer of the model at one block of zeros. Returns the bytes freed, or -1.</summary>
    public static int StripGeometry(CaffFile caff, int symbol, out string why)
    {
        var m = ModelAsset.Parse(caff, symbol);
        var refs = Refs(m, out why);
        if (refs == null) return -1;
        var d = m.View.Data(".data");
        var gpuPart = m.View.Part(m.View.PartId(".gpu"));
        int R = m.ResourceHeader;
        var vbt = m.View.PtrAt(".data", R + 0x48)!.Value.Offset; int nVb = BE.S32(d, R + 0x50);
        var ibt = m.View.PtrAt(".data", R + 0x54)!.Value.Offset; int nIb = BE.S32(d, R + 0x58);
        // block size: the largest index buffer (their draw counts stay valid) and at least one vertex of any stride
        int z = 256;
        for (int i = 0; i < nIb; i++) z = Math.Max(z, BE.S32(d, ibt + 16 * i + 8));
        for (int i = 0; i < nVb; i++) z = Math.Max(z, BE.S32(d, m.View.PtrAt(".data", vbt + 12 * i)!.Value.Offset - 4));
        z = (z + 127) & ~127;
        if (z >= gpuPart.Data.Length) return 0;
        for (int i = 0; i < nVb; i++)
        {
            int e = vbt + 12 * i, rec = m.View.PtrAt(".data", e)!.Value.Offset - 4;
            int stride = Math.Max(1, BE.S32(d, rec));
            int vsize = z - z % stride;
            BE.W32(d, rec + 8, 0); BE.W32(d, rec + 12, vsize);
            BE.W32(d, e + 4, 0); BE.W32(d, e + 8, vsize);
        }
        for (int i = 0; i < nIb; i++) BE.W32(d, ibt + 16 * i + 4, 0);   // byte size kept (≤ z): counts stay consistent
        int freed = gpuPart.Data.Length - z;
        gpuPart.Data = new byte[z]; gpuPart.Size = z;
        return freed;
    }

    /// <summary>
    /// Of the candidate model symbols, those no other asset refers to by id (any part except .gpu/.texturegpu of every
    /// symbol outside the set, the given owner symbols and the manifest). A model's collision asset (the aid_havok_ of the
    /// same name, which lists the models nested in it) counts as part of the model. Iterated until stable, so a model
    /// nested in a candidate that must stay also stays.
    /// </summary>
    public static HashSet<int> Unreferenced(CaffFile caff, IEnumerable<int> candidates, ISet<int> owners)
    {
        var set = candidates.ToHashSet();
        int manifest = caff.Symbols.IndexOf("manifest") + 1;
        var symOf = new Dictionary<string, int>();
        for (int s = 1; s <= caff.Symbols.Count; s++) symOf.TryAdd(AssetIds.DisplayName(caff.Symbols[s - 1]), s);
        int HavokOf(int model) => symOf.GetValueOrDefault(AssetIds.DisplayName(caff.Symbols[model - 1]).Replace("aid_model_", "aid_havok_"));
        while (true)
        {
            var own = set.Select(HavokOf).Where(h => h > 0).ToHashSet();
            var ids = new Dictionary<uint, int>();
            foreach (int s in set) if (AssetIds.IdOf(caff.Symbols[s - 1]) is uint id) ids[id] = s;
            var hit = new HashSet<int>();
            foreach (var p in caff.Parts)
            {
                if (set.Contains(p.Symbol) || own.Contains(p.Symbol) || owners.Contains(p.Symbol) || p.Symbol == manifest) continue;
                var sec = caff.SectionOf(p).Name;
                if (sec == ".gpu" || sec == ".texturegpu") continue;
                var b = p.Data;
                for (int o = 0; o + 4 <= b.Length; o += 4)
                    if (ids.TryGetValue(BE.U32(b, o), out int s)) hit.Add(s);
            }
            if (hit.Count == 0) return set;
            set.ExceptWith(hit);
        }
    }

    /// <summary>Diagnostics for <see cref="Unreferenced"/>: which symbol/section refers to each model.</summary>
    public static List<(int Model, string Who)> Referrers(CaffFile caff, IEnumerable<int> models, ISet<int> owners)
    {
        var ids = new Dictionary<uint, int>();
        foreach (int s in models) if (AssetIds.IdOf(caff.Symbols[s - 1]) is uint id) ids[id] = s;
        int manifest = caff.Symbols.IndexOf("manifest") + 1;
        var res = new List<(int, string)>();
        foreach (var p in caff.Parts)
        {
            if (owners.Contains(p.Symbol) || p.Symbol == manifest || ids.ContainsValue(p.Symbol)) continue;
            var sec = caff.SectionOf(p).Name;
            if (sec == ".gpu" || sec == ".texturegpu") continue;
            for (int o = 0; o + 4 <= p.Data.Length; o += 4)
                if (ids.TryGetValue(BE.U32(p.Data, o), out int s) && s != p.Symbol) res.Add((s, $"{AssetIds.DisplayName(caff.Symbols[p.Symbol - 1])} {sec}+0x{o:X}"));
        }
        return res;
    }
}
