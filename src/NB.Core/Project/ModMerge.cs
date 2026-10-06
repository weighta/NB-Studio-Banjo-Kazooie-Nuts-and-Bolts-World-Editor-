using System.IO.Compression;
using System.Security.Cryptography;
using NB.Core.Compression;
using NB.Core.Formats;
using NB.Core.IO;

namespace NB.Core.Project;

/// <summary>
/// Combines several mods that change the same game file, asset by asset (three-way, against the original file):
/// <list type="bullet">
/// <item>CAFF bundles (Bundle/4f, and CAFF entries of stream archives): every asset (symbol) one mod changes is taken from
///   that mod; assets a mod adds are copied in; an asset two mods change differently is a conflict. A changed asset is
///   taken over in place when its layout (parts and pointers) is unchanged; otherwise only from the first mod.</item>
/// <item>Stream archives (Bundle/50): entry by entry, CAFF entries merged as above.</item>
/// </list>
/// Anything else that two mods both change is a conflict. Conflict messages name the mods, the file and the asset.
/// </summary>
public static class ModMerge
{
    public sealed record Variant(string ModName, byte[] Data);

    public sealed class MergeException : InvalidOperationException
    {
        public readonly List<string> Conflicts;
        public MergeException(List<string> c) : base(string.Join("\n", c)) { Conflicts = c; }
    }

    /// <summary>The file a mod's delta produces from the original (expanded) file.</summary>
    public static byte[] Target(string patchPath, PatchPackage.PatchFile f, byte[] originalRaw)
    {
        using var zip = ZipFile.OpenRead(patchPath);
        using var s = (zip.GetEntry(f.Entry) ?? throw new InvalidDataException($"{f.Path}: missing in {Path.GetFileName(patchPath)}")).Open();
        var ms = new MemoryStream(); s.CopyTo(ms);
        if (f.Kind == "new") return ms.ToArray();
        if (PatchPackage.Sha(originalRaw) != f.SourceSha256) throw new InvalidDataException($"{f.Path} is not the original file this mod was made for");
        return Delta.Apply(PatchPackage.Expand(originalRaw), ms.ToArray());
    }

    /// <summary>Merges the mods' versions of one file. Throws <see cref="MergeException"/> with every conflict.</summary>
    public static byte[] Merge(string path, byte[]? originalRaw, IReadOnlyList<Variant> variants)
    {
        var conflicts = new List<string>();
        byte[] result = Merge(path, originalRaw, variants, conflicts);
        if (conflicts.Count > 0) throw new MergeException(conflicts.Distinct().ToList());
        return result;
    }

    static byte[] Merge(string path, byte[]? originalRaw, IReadOnlyList<Variant> variants, List<string> conflicts)
    {
        if (variants.Skip(1).All(v => v.Data.AsSpan().SequenceEqual(variants[0].Data))) return variants[0].Data;
        if (originalRaw == null)
        {
            conflicts.Add($"{Names(variants)} both add {path} with different contents.");
            return variants[0].Data;
        }
        var orig = Normal(originalRaw);
        if (CaffFile.IsCaff(orig) && variants.All(v => CaffFile.IsCaff(Normal(v.Data))))
            return MergeCaff(path, orig, variants.Select(v => v with { Data = Normal(v.Data) }).ToList(), conflicts);
        if (BundleArchive.IsArchive(orig) && variants.All(v => BundleArchive.IsArchive(v.Data)))
            return MergeArchive(path, orig, variants, conflicts);
        conflicts.Add($"{Names(variants)} both change {path} (only bundles can be combined).");
        return variants[0].Data;
    }

    static string Names(IEnumerable<Variant> v) => string.Join(" and ", v.Select(x => $"\"{x.ModName}\"").Distinct());
    static byte[] Normal(byte[] d) => XCompressFile.IsCompressed(d) ? XCompressFile.Decompress(d) : d;

    // ------------------------------------------------------------------ stream archives

    static byte[] MergeArchive(string path, byte[] origRaw, IReadOnlyList<Variant> variants, List<string> conflicts)
    {
        var o = BundleArchive.Read(origRaw);
        var vs = variants.Select(v => (v.ModName, A: BundleArchive.Read(v.Data))).ToList();
        if (vs.Any(v => v.A.Entries.Count < o.Entries.Count || !v.A.Entries.Take(o.Entries.Count).Select(e => e.Id).SequenceEqual(o.Entries.Select(e => e.Id))))
        {
            conflicts.Add($"{Names(variants)} both change {path} and one of them reorders its entries.");
            return variants[0].Data;
        }
        var res = BundleArchive.Read(variants[0].Data);
        for (int i = 0; i < o.Entries.Count; i++)
        {
            var od = o.Entries[i].Data;
            var changed = vs.Where(v => !SameEntry(v.A.Entries[i].Data, od)).ToList();
            if (changed.Count == 0) continue;
            string where = $"{path} entry {o.Entries[i].Id:X8}";
            if (changed.Count == 1) { res.Entries[i].Data = changed[0].A.Entries[i].Data; continue; }
            var cv = changed.Select(c => new Variant(c.ModName, c.A.Entries[i].Data ?? Array.Empty<byte>())).ToList();
            res.Entries[i].Data = od == null ? cv[0].Data : Merge(where, od, cv, conflicts);
        }
        // entries the mods append
        var added = new Dictionary<uint, Variant>();
        foreach (var (name, a) in vs)
            foreach (var e in a.Entries.Skip(o.Entries.Count))
            {
                if (added.TryGetValue(e.Id, out var had))
                {
                    if (!SameEntry(had.Data, e.Data)) conflicts.Add($"\"{had.ModName}\" and \"{name}\" both add entry {e.Id:X8} to {path}.");
                    continue;
                }
                added[e.Id] = new Variant(name, e.Data ?? Array.Empty<byte>());
            }
        res.Entries.RemoveRange(o.Entries.Count, res.Entries.Count - o.Entries.Count);
        foreach (var (id, v) in added) res.Entries.Add(new BundleEntry { Id = id, Data = v.Data });
        foreach (var (_, a) in vs)
            foreach (var d in a.Dependencies) if (!res.Dependencies.Contains(d)) res.Dependencies.Add(d);
        return res.Write();
    }

    static bool SameEntry(byte[]? a, byte[]? b)
    {
        if (a == null || b == null) return a == b;
        if (a.AsSpan().SequenceEqual(b)) return true;
        return XCompressFile.IsCompressed(a) != XCompressFile.IsCompressed(b) && Normal(a).AsSpan().SequenceEqual(Normal(b));
    }

    // ------------------------------------------------------------------ CAFF

    /// <summary>One asset's parts and pointers, comparable between versions of a bundle.</summary>
    internal sealed record Shape(string Layout, string Content);

    internal static Dictionary<string, Shape> Shapes(CaffFile c)
    {
        var local = new Dictionary<int, (int Sym, int Local)>();
        var count = new Dictionary<int, int>();
        for (int i = 0; i < c.Parts.Count; i++)
        {
            int s = c.Parts[i].Symbol;
            count[s] = count.GetValueOrDefault(s);
            local[i + 1] = (s, count[s]++);
        }
        string SymName(int s) => s >= 1 && s <= c.Symbols.Count ? AssetIds.DisplayName(c.Symbols[s - 1]) : "#" + s;
        var layout = new Dictionary<int, System.Text.StringBuilder>();
        var content = new Dictionary<int, IncrementalHash>();
        System.Text.StringBuilder L(int s) => layout.TryGetValue(s, out var b) ? b : layout[s] = new();
        IncrementalHash H(int s) => content.TryGetValue(s, out var h) ? h : content[s] = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int i = 0; i < c.Parts.Count; i++)
        {
            var p = c.Parts[i];
            L(p.Symbol).Append(c.SectionOf(p).Name).Append('/').Append(p.AlignLog2).Append(';');
            H(p.Symbol).AppendData(BitConverter.GetBytes(p.Size));
            H(p.Symbol).AppendData(p.Data);
        }
        foreach (var r in c.Relocs.OrderBy(r => r.FromPart).ThenBy(r => r.ToPart))
        {
            var (fs, fl) = local[r.FromPart]; var (ts, tl) = local[r.ToPart];
            L(fs).Append($"R{fl}>{(ts == fs ? "self" : SymName(ts))}:{tl}@{string.Join(',', r.Offsets)}t{r.Table};");
        }
        var res = new Dictionary<string, Shape>();
        foreach (var s in layout.Keys)
            res[s == 0 ? "(shared data)" : SymName(s)] = new Shape(layout[s].ToString(), Convert.ToHexString(H(s).GetHashAndReset()));
        return res;
    }

    static byte[] MergeCaff(string path, byte[] origBytes, IReadOnlyList<Variant> variants, List<string> conflicts)
    {
        var o = CaffFile.Read(origBytes);
        var so = Shapes(o);
        var res = CaffFile.Read(variants[0].Data);
        var owner = Shapes(res).ToDictionary(kv => kv.Key, kv => so.TryGetValue(kv.Key, out var x) && x == kv.Value ? null : variants[0].ModName);
        foreach (var v in variants.Skip(1))
        {
            var c = CaffFile.Read(v.Data);
            var sc = Shapes(c);
            var sr = Shapes(res);
            foreach (var (name, shape) in sc)
            {
                if (name == "manifest") continue;   // rebuilt by CopyAsset for added assets
                bool inO = so.TryGetValue(name, out var os);
                if (inO && os == shape) continue;                       // this mod leaves it alone
                if (sr.TryGetValue(name, out var rs) && rs == shape) continue;   // same change as an earlier mod
                string who = owner.GetValueOrDefault(name) ?? "";
                if (inO && who.Length > 0 && RecordList(name) is int rec && MergeRecords(o, res, c, name, rec)) { owner[name] = who + "\" and \"" + v.ModName; continue; }
                if (inO && who.Length > 0) { conflicts.Add($"\"{who}\" and \"{v.ModName}\" both change {name} in {path}."); continue; }
                if (!inO && sr.ContainsKey(name)) { conflicts.Add($"\"{who}\" and \"{v.ModName}\" both add {name} to {path}."); continue; }
                if (name == "(shared data)") { conflicts.Add($"\"{v.ModName}\" changes shared (unnamed) data in {path}, which another mod also changes."); continue; }
                if (!inO)
                {
                    int s = c.Symbols.FindIndex(x => AssetIds.DisplayName(x) == name) + 1;
                    try { CaffEdit.CopyAsset(c, s, res, name); owner[name] = v.ModName; }
                    catch (Exception e) when (e is InvalidDataException or InvalidOperationException) { conflicts.Add($"\"{v.ModName}\" adds {name} to {path}, which cannot be combined: {e.Message}"); }
                    continue;
                }
                if (rs!.Layout != shape.Layout)
                {
                    conflicts.Add($"\"{v.ModName}\" rebuilds {name} in {path} (its parts or pointers change), which cannot be combined with another mod's changes to the same file.");
                    continue;
                }
                int rsym = res.Symbols.FindIndex(x => AssetIds.DisplayName(x) == name) + 1, csym = c.Symbols.FindIndex(x => AssetIds.DisplayName(x) == name) + 1;
                var rp = res.PartsOf(rsym).ToList(); var cp = c.PartsOf(csym).ToList();
                for (int i = 0; i < rp.Count; i++) { rp[i].Data = cp[i].Data; rp[i].Size = cp[i].Size; }
                owner[name] = v.ModName;
            }
            foreach (var name in so.Keys.Where(n => n != "manifest" && !sc.ContainsKey(n)))
                conflicts.Add($"\"{v.ModName}\" removes {name} from {path}; removals cannot be combined with another mod's changes to the same file.");
        }
        var bytes = res.Write();
        CaffFile.Read(bytes);
        return bytes;
    }

    // ------------------------------------------------------------------ record lists

    /// <summary>
    /// Assets that are flat lists of records keyed by a part id, which mods that add vehicle parts (ULTRA Parts, Seattle's
    /// recovered parts) all extend: the garage inventories (aid_misc_banjox_blockset_*: 8-byte {u32 count, u32 part id})
    /// and the network list (aid_misc_banjox_live_networkenum: 20-byte {u32 part id, 4, 0, 0, 0}). Returns the record size.
    /// </summary>
    static int? RecordList(string name) =>
        name.StartsWith("aid_misc_banjox_blockset_", StringComparison.Ordinal) ? 8 :
        name == "aid_misc_banjox_live_networkenum" ? 20 : null;

    /// <summary>
    /// Three-way merge of a record list (see <see cref="RecordList"/>): <paramref name="res"/> holds the earlier mods'
    /// version, <paramref name="c"/> this mod's. A record only one side changed takes that change; records both add or
    /// change keep the larger count (and a record one side removed stays if the other side changed it); added records
    /// are appended in order. False when the asset is not a single .data part in all three versions.
    /// </summary>
    static bool MergeRecords(CaffFile o, CaffFile res, CaffFile c, string name, int size)
    {
        static CaffPart? Data(CaffFile f, string name)
        {
            int s = f.Symbols.FindIndex(x => AssetIds.DisplayName(x) == name) + 1;
            var parts = s == 0 ? new List<CaffPart>() : f.PartsOf(s).ToList();
            return parts.Count == 1 && f.SectionOf(parts[0]).Name == ".data" ? parts[0] : null;
        }
        var po = Data(o, name); var pr = Data(res, name); var pc = Data(c, name);
        if (po == null || pr == null || pc == null) return false;
        if (po.Data.Length % size != 0 || pr.Data.Length % size != 0 || pc.Data.Length % size != 0) return false;
        int key = size == 8 ? 4 : 0;   // offset of the part id in a record
        List<(uint Id, byte[] Rec)> Read(byte[] d)
        {
            var l = new List<(uint, byte[])>();
            for (int i = 0; i + size <= d.Length; i += size) l.Add((BE.U32(d, i + key), d.AsSpan(i, size).ToArray()));
            return l;
        }
        var lo = Read(po.Data); var lr = Read(pr.Data); var lc = Read(pc.Data);
        if (lo.Select(x => x.Id).Distinct().Count() != lo.Count) return false;   // ids must be unique keys
        var mo = lo.ToDictionary(x => x.Id, x => x.Rec);
        var mr = lr.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().Rec);
        var mc = lc.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().Rec);
        static bool Same(byte[]? a, byte[]? b) => a == null ? b == null : b != null && a.AsSpan().SequenceEqual(b);
        byte[]? Larger(byte[]? a, byte[]? b) => a == null ? b : b == null ? a : size == 8 && BE.U32(b, 0) > BE.U32(a, 0) ? b : a;
        var outList = new List<byte[]>();
        foreach (var (id, rec) in lo)
        {
            var a = mr.GetValueOrDefault(id); var b = mc.GetValueOrDefault(id);
            var pick = Same(b, rec) ? a : Same(a, rec) ? b : Larger(a, b);
            if (pick != null) outList.Add(pick);
        }
        var added = new Dictionary<uint, byte[]>(); var order = new List<uint>();
        foreach (var (id, rec) in lr.Concat(lc))
        {
            if (mo.ContainsKey(id)) continue;
            if (added.TryGetValue(id, out var had)) { added[id] = Larger(had, rec)!; continue; }
            added[id] = rec; order.Add(id);
        }
        foreach (var id in order) outList.Add(added[id]);
        var nd = outList.SelectMany(x => x).ToArray();
        pr.Data = nd; pr.Size = nd.Length;
        return true;
    }
}
