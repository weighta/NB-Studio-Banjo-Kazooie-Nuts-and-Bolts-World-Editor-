using System.Numerics;
using NB.Core.Formats;
using NB.Core.IO;

namespace NB.Core.World;

/// <summary>
/// Adds and removes scenery instances in a background model's chunk 12. The renderer places scenery from chunk 12
/// (verified in Xenia: editing only an instance's chunk-12 matrix/position moves it), so chunk 2 nodes are left alone
/// (their count differs from the instance count in several worlds, so they are not a per-instance table).
///
/// Chunk 12 header: +0 ref model count, +4/+8/+0xC/+0x14 instance count (×4), +0x18 → ref model ids,
/// +0x1C → records (0x144 each; +0 ref model index, +0x10/+0x14/+0x18 own index, +0x3C name), +0x20 → world
/// matrices (64 each), +0x24 → positions (12 each), +0x2C → index list (u32 each, identity). The arrays are
/// contiguous and nothing else points into them, so:
///  * Duplicate appends larger copies of the four arrays at the end of .data and repoints the header;
///  * Remove moves the last instance into the removed slot and shrinks the counts (arrays stay in place).
/// </summary>
public static class InstanceEditor
{
    const int RecSize = 0x144, MatSize = 64, PosSize = 12;

    sealed class C12
    {
        public byte[] D = null!; public AssetView V = null!; public CaffPart Part = null!;
        public int H, N, Rec, Mat, Pos, Idx;
    }

    static C12 Open(CaffFile caff, int symbol)
    {
        var v = new AssetView(caff, symbol);
        var part = v.Part(v.PartId(".data"));
        var d = part.Data;
        int t = BE.S32(d, 0), cn = BE.S32(d, 4), h = -1;
        for (int i = 0; i < cn; i++) if (BE.S32(d, t + 8 * i) == 12) h = BE.S32(d, t + 8 * i + 4);
        if (h < 0) throw new InvalidDataException("model has no scenery instances (chunk 12)");
        int n = BE.S32(d, h + 4);
        if (BE.S32(d, h + 8) != n || BE.S32(d, h + 0xC) != n || BE.S32(d, h + 0x14) != n) throw new InvalidDataException("chunk 12 counts disagree; not editing");
        int P(int o) => v.PtrAt(".data", h + o)?.Offset ?? throw new InvalidDataException($"chunk 12 +0x{o:X} is not a pointer");
        return new C12 { D = d, V = v, Part = part, H = h, N = n, Rec = P(0x1C), Mat = P(0x20), Pos = P(0x24), Idx = P(0x2C) };
    }

    static void SetCount(byte[] d, int h, int n) { foreach (int o in new[] { 4, 8, 0xC, 0x14 }) BE.W32(d, h + o, n); }

    static void FixRecordIndex(byte[] d, int rec, int from, int to)
    {
        foreach (int o in new[] { 0x10, 0x14, 0x18 }) if (BE.S32(d, rec + o) == from) BE.W32(d, rec + o, to);
    }

    /// <summary>Adds a copy of instance <paramref name="src"/> with a new world matrix; returns the new index.</summary>
    public static int Duplicate(CaffFile caff, int symbol, int src, Matrix4x4 world) => DuplicateMany(caff, symbol, src, new[] { world });

    /// <summary>
    /// Adds one copy of instance <paramref name="src"/> per matrix, re-creating the chunk-12 arrays (and chunk 2 and the
    /// collision table) only ONCE for the whole batch. Each re-creation appends the grown arrays to .data and leaves the
    /// old copy unused, so adding instances one by one grows the world bundle by the whole table every time (B21:
    /// 66 single adds left ~24 MB of dead copies in Showdown Town). Returns the index of the first new instance.
    /// </summary>
    public static int DuplicateMany(CaffFile caff, int symbol, int src, IReadOnlyList<Matrix4x4> worlds)
    {
        var c = Open(caff, symbol);
        if (src < 0 || src >= c.N) throw new ArgumentOutOfRangeException(nameof(src));
        int k = worlds.Count; if (k == 0) return c.N;
        int n = c.N, n1 = n + k;
        static int A16(int x) => (x + 15) & ~15;
        int baseOff = A16(c.D.Length);
        int rec = baseOff, mat = A16(rec + RecSize * n1), pos = A16(mat + MatSize * n1), idx = A16(pos + PosSize * n1), end = A16(idx + 4 * n1);
        var nd = new byte[end];
        Buffer.BlockCopy(c.D, 0, nd, 0, c.D.Length);
        Buffer.BlockCopy(c.D, c.Rec, nd, rec, RecSize * n);
        Buffer.BlockCopy(c.D, c.Mat, nd, mat, MatSize * n);
        Buffer.BlockCopy(c.D, c.Pos, nd, pos, PosSize * n);
        Buffer.BlockCopy(c.D, c.Idx, nd, idx, 4 * n);
        for (int j = 0; j < k; j++)
        {
            int ni = n + j; var world = worlds[j];
            // the new instance: copy of the source record with its own index, new transform
            Buffer.BlockCopy(c.D, c.Rec + RecSize * src, nd, rec + RecSize * ni, RecSize);
            FixRecordIndex(nd, rec + RecSize * ni, src, ni);
            float[] m = { world.M11, world.M12, world.M13, world.M14, world.M21, world.M22, world.M23, world.M24, world.M31, world.M32, world.M33, world.M34, world.M41, world.M42, world.M43, world.M44 };
            for (int q = 0; q < 16; q++) BE.WF32(nd, mat + MatSize * ni + 4 * q, m[q]);
            BE.WF32(nd, pos + PosSize * ni, world.M41); BE.WF32(nd, pos + PosSize * ni + 4, world.M42); BE.WF32(nd, pos + PosSize * ni + 8, world.M43);
            BE.W32(nd, idx + 4 * ni, ni);
        }
        // repoint the header (the pointer locations already carry relocations) and grow the counts
        BE.W32(nd, c.H + 0x1C, rec); BE.W32(nd, c.H + 0x20, mat); BE.W32(nd, c.H + 0x24, pos); BE.W32(nd, c.H + 0x2C, idx);
        SetCount(nd, c.H, n1);
        c.Part.Data = AddPlacementNodes(nd, n, src, worlds);
        AddCollisionEntries(caff, c.V.Name, n, src, k);
        return n;
    }

    /// <summary>Index of <paramref name="modelId"/> in the chunk-12 reference model list, appending it if needed
    /// (the list is re-created one entry longer at the end of .data; nothing else points into it).</summary>
    public static int AddReferenceModel(CaffFile caff, int symbol, uint modelId)
    {
        var c = Open(caff, symbol);
        var d = c.D;
        int nref = BE.S32(d, c.H), ids = c.V.PtrAt(".data", c.H + 0x18)?.Offset ?? throw new InvalidDataException("chunk 12 +0x18 is not a pointer");
        for (int i = 0; i < nref; i++) if (BE.U32(d, ids + 4 * i) == modelId) return i;
        int at = (d.Length + 15) & ~15;
        var nd = new byte[at + ((4 * (nref + 1) + 15) & ~15)];
        Buffer.BlockCopy(d, 0, nd, 0, d.Length);
        Buffer.BlockCopy(d, ids, nd, at, 4 * nref);
        BE.W32(nd, at + 4 * nref, modelId);
        BE.W32(nd, c.H + 0x18, at); BE.W32(nd, c.H, nref + 1);
        c.Part.Data = nd;
        return nref;
    }

    /// <summary>
    /// Places a new instance of reference model <paramref name="modelId"/> (e.g. a cloned model): a copy of instance
    /// <paramref name="template"/> (record layout, chunk-2 node, collision slot) with the new reference index, name and
    /// matrix. Collision: when <paramref name="havokId"/> is given the instance gets its own collision list
    /// {model id, havok id, 0, 0}; otherwise it has no collision. Returns the new instance index.
    /// For several instances use <see cref="AddModelInstances"/> (one table re-creation for all of them, B21).
    /// </summary>
    public static int AddModelInstance(CaffFile caff, int symbol, int template, uint modelId, uint? havokId, Matrix4x4 world, string name)
        => AddModelInstances(caff, symbol, template, new[] { new NewInstance(modelId, havokId, world, name) });

    public sealed record NewInstance(uint ModelId, uint? HavokId, Matrix4x4 World, string Name);

    /// <summary>Batch version of <see cref="AddModelInstance"/>; returns the index of the first new instance.</summary>
    public static int AddModelInstances(CaffFile caff, int symbol, int template, IReadOnlyList<NewInstance> items)
    {
        if (items.Count == 0) return Open(caff, symbol).N;
        var refIndex = AddReferenceModels(caff, symbol, items.Select(i => i.ModelId).Distinct().ToList());
        int first = DuplicateMany(caff, symbol, template, items.Select(i => i.World).ToList());
        var c = Open(caff, symbol);
        for (int j = 0; j < items.Count; j++)
        {
            int rec = c.Rec + RecSize * (first + j);
            BE.W32(c.D, rec, refIndex[items[j].ModelId]);
            var nameBytes = System.Text.Encoding.Latin1.GetBytes("|REFERENCE_" + items[j].Name + "|");
            Array.Clear(c.D, rec + 0x3C, 0x100);
            Buffer.BlockCopy(nameBytes, 0, c.D, rec + 0x3C, Math.Min(nameBytes.Length, 0xFF));
        }
        for (int j = 0; j < items.Count; j++)
            SetCollisionList(caff, c.V.Name, first + j, items[j].HavokId is uint h ? new[] { (items[j].ModelId, h) } : Array.Empty<(uint, uint)>());
        return first;
    }

    /// <summary>Indices of the given models in the reference list, appending the missing ones in one re-creation.</summary>
    public static Dictionary<uint, int> AddReferenceModels(CaffFile caff, int symbol, IReadOnlyList<uint> modelIds)
    {
        var c = Open(caff, symbol);
        var d = c.D;
        int nref = BE.S32(d, c.H), ids = c.V.PtrAt(".data", c.H + 0x18)?.Offset ?? throw new InvalidDataException("chunk 12 +0x18 is not a pointer");
        var res = new Dictionary<uint, int>();
        for (int i = 0; i < nref; i++) res.TryAdd(BE.U32(d, ids + 4 * i), i);
        var missing = modelIds.Where(m => !res.ContainsKey(m)).Distinct().ToList();
        if (missing.Count == 0) return res;
        int at = (d.Length + 15) & ~15, n2 = nref + missing.Count;
        var nd = new byte[at + ((4 * n2 + 15) & ~15)];
        Buffer.BlockCopy(d, 0, nd, 0, d.Length);
        Buffer.BlockCopy(d, ids, nd, at, 4 * nref);
        for (int i = 0; i < missing.Count; i++) { BE.W32(nd, at + 4 * (nref + i), missing[i]); res[missing[i]] = nref + i; }
        BE.W32(nd, c.H + 0x18, at); BE.W32(nd, c.H, n2);
        c.Part.Data = nd;
        return res;
    }

    /// <summary>Gives instance <paramref name="index"/> its own collision list (records {model id, havok id, 0, 0}) in
    /// the world havok asset's type-7 table; an empty list removes its collision.</summary>
    public static bool SetCollisionList(CaffFile caff, string backgroundName, int index, IReadOnlyList<(uint Model, uint Havok)> entries)
    {
        string havokName = AssetIds.DisplayName(backgroundName).Replace("aid_model_", "aid_havok_");
        int sym = caff.Symbols.FindIndex(s => AssetIds.DisplayName(s) == havokName) + 1;
        if (sym == 0) return false;
        var v = new AssetView(caff, sym);
        int pid = v.PartId(".data");
        var part = v.Part(pid);
        var d = part.Data;
        int t = BE.S32(d, 0), cn = BE.S32(d, 4), e7 = -1;
        for (int i = 0; i < cn; i++) if (BE.S32(d, t + 8 * i) == 7) e7 = BE.S32(d, t + 8 * i + 4);
        if (e7 < 0 || index >= BE.S32(d, e7 + 4)) return false;
        int table = BE.S32(d, e7);
        var reloc = caff.Relocs.FirstOrDefault(r => r.FromPart == pid && r.ToPart == pid && r.Offsets.Contains(e7));
        if (reloc == null) return false;
        int at = (d.Length + 15) & ~15;
        var nd = new byte[at + Math.Max(16, 16 * entries.Count)];
        Buffer.BlockCopy(d, 0, nd, 0, d.Length);
        for (int i = 0; i < entries.Count; i++) { BE.W32(nd, at + 16 * i, entries[i].Model); BE.W32(nd, at + 16 * i + 4, entries[i].Havok); }
        BE.W32(nd, table + 8 * index, at); BE.W32(nd, table + 8 * index + 4, entries.Count);
        if (!reloc.Offsets.Contains(table + 8 * index)) { var offs = reloc.Offsets.Append(table + 8 * index).ToList(); offs.Sort(); reloc.Offsets = offs.ToArray(); }
        part.Data = nd;
        return true;
    }

    /// <summary>
    /// Scenery collision comes from the world havok asset (aid_havok_…_background_&lt;world&gt;_default): its wrapper
    /// entry of type 7 is {→ table, count, 0, 0} with one (→ list, count) pair per chunk-12 instance, in instance
    /// order; each list holds 16-byte records (model asset id, havok asset id, 0, 0). The game builds each instance's
    /// collision from its list and the instance matrix (verified in Xenia: moving an instance moves its collision; a
    /// duplicate beyond the table had none). The new instance gets a copy of the source's pair (sharing its list): the
    /// table is re-created one entry longer at the end of .data, with relocations for every pointer slot.
    /// Returns false when the world has no such table or it does not match the instance count.
    /// </summary>
    static bool AddCollisionEntries(CaffFile caff, string backgroundName, int n, int src, int k)
    {
        string havokName = backgroundName.Replace("aid_model_", "aid_havok_");
        int sym = caff.Symbols.FindIndex(s => AssetIds.DisplayName(s) == havokName) + 1;
        if (sym == 0) return false;
        var v = new AssetView(caff, sym);
        if (!v.PartBySection.ContainsKey(".data")) return false;
        int pid = v.PartId(".data");
        var part = v.Part(pid);
        var d = part.Data;
        int t = BE.S32(d, 0), cn = BE.S32(d, 4), e7 = -1;
        for (int i = 0; i < cn; i++) if (BE.S32(d, t + 8 * i) == 7) e7 = BE.S32(d, t + 8 * i + 4);
        if (e7 < 0 || BE.S32(d, e7 + 4) != n) return false;
        int table = BE.S32(d, e7);
        var reloc = caff.Relocs.FirstOrDefault(r => r.FromPart == pid && r.ToPart == pid && r.Offsets.Contains(e7));
        if (reloc == null || !reloc.Offsets.Contains(table)) return false;   // e7 and the table's first slot are pointers
        int at = (d.Length + 15) & ~15;
        var nd = new byte[at + 8 * (n + k)];
        Buffer.BlockCopy(d, 0, nd, 0, d.Length);
        Buffer.BlockCopy(d, table, nd, at, 8 * n);
        for (int j = 0; j < k; j++) Buffer.BlockCopy(d, table + 8 * src, nd, at + 8 * (n + j), 8);
        BE.W32(nd, e7, at); BE.W32(nd, e7 + 4, n + k);
        var offs = new List<int>(reloc.Offsets);
        for (int i = 0; i < n + k; i++) offs.Add(at + 8 * i);
        offs.Sort();
        reloc.Offsets = offs.ToArray();
        part.Data = nd;
        return true;
    }

    const int NodeSize = 68;

    /// <summary>
    /// Keeps chunk 2 consistent with chunk 12: where the nodes are the per-instance table in reverse order (node k =
    /// instance n-1-k, e.g. Showdown Town), the new instance n gets a node at index 0 (chunk 2 is rebuilt as
    /// (count+1, new node, old nodes) at the end of .data and the chunk table is repointed; nothing else points into
    /// chunk 2). Collision does NOT come from these nodes (verified: moving only a node leaves the collision in place);
    /// see <see cref="AddCollisionEntry"/>. Other layouts are left unchanged.
    /// </summary>
    static byte[] AddPlacementNodes(byte[] d, int n, int src, IReadOnlyList<Matrix4x4> worlds)
    {
        int t = BE.S32(d, 0), cn = BE.S32(d, 4), entry = -1;
        for (int i = 0; i < cn; i++) if (BE.S32(d, t + 8 * i) == 2) entry = t + 8 * i + 4;
        if (entry < 0) return d;
        int c2 = BE.S32(d, entry), count = BE.S32(d, c2);
        if (count != n) return d;
        // check the reverse mapping on the source instance: node (n-1-src) carries its translation
        int srcNode = c2 + 4 + NodeSize * (n - 1 - src);
        var h12 = FindChunk(d, 12);
        int mats = BE.S32(d, h12 + 0x20);
        for (int k = 0; k < 3; k++)
            if (Math.Abs(BE.F32(d, srcNode + 4 + 16 * k + 12) - BE.F32(d, mats + MatSize * src + 48 + 4 * k)) > 1e-3f) return d;
        int kk = worlds.Count;
        int at = (d.Length + 15) & ~15, size = 4 + NodeSize * (count + kk);
        var nd = new byte[at + size];
        Buffer.BlockCopy(d, 0, nd, 0, d.Length);
        BE.W32(nd, at, count + kk);
        for (int j = 0; j < kk; j++)
        {
            int node = at + 4 + NodeSize * (kk - 1 - j);   // node q = instance (count + kk - 1 - q): newest first
            var world = worlds[j];
            Buffer.BlockCopy(d, srcNode, nd, node, NodeSize);                           // flags, parent, extra from the source
            float[] rows = { world.M11, world.M21, world.M31, world.M41, world.M12, world.M22, world.M32, world.M42, world.M13, world.M23, world.M33, world.M43 };
            for (int q = 0; q < 12; q++) BE.WF32(nd, node + 4 + 4 * q, rows[q]);
        }
        Buffer.BlockCopy(d, c2 + 4, nd, at + 4 + NodeSize * kk, NodeSize * count);
        BE.W32(nd, entry, at);                                                       // the table entry already carries a relocation
        return nd;
    }

    static int FindChunk(byte[] d, int id)
    {
        int t = BE.S32(d, 0), cn = BE.S32(d, 4);
        for (int i = 0; i < cn; i++) if (BE.S32(d, t + 8 * i) == id) return BE.S32(d, t + 8 * i + 4);
        return -1;
    }

    /// <summary>
    /// Deletes an instance from the playable world without changing any index: its matrix is set to zero scale and
    /// it is moved far below the level. (Shrinking chunk 12 — <see cref="Remove"/> — makes Showdown Town hang on the
    /// loading screen: other data refers to instances by index. Verified in Xenia.)
    /// </summary>
    public static void Hide(CaffFile caff, int symbol, int index, float scale = 0f, float y = HiddenY)
    {
        var c = Open(caff, symbol);
        if (index < 0 || index >= c.N) throw new ArgumentOutOfRangeException(nameof(index));
        int m = c.Mat + MatSize * index, p = c.Pos + PosSize * index;
        for (int k = 0; k < 12; k++) BE.WF32(c.D, m + 4 * k, 0f);          // rotation/scale rows → 0
        if (scale != 0) { BE.WF32(c.D, m, scale); BE.WF32(c.D, m + 20, scale); BE.WF32(c.D, m + 40, scale); }
        BE.WF32(c.D, m + 52, y);                                             // translation y
        BE.WF32(c.D, p + 4, y);
    }

    public const float HiddenY = -10000f;

    /// <summary>
    /// Hides an instance by moving it straight down by <paramref name="dy"/>, keeping its rotation and scale (B26).
    /// Unlike <see cref="Hide"/>, its collision stays consistent with its model: Havok shapes do not scale, so a shrunk or
    /// re-oriented instance leaves attached moving parts jammed inside full-size collision (console slowdown).
    /// Sinking the whole town by the same amount keeps every object's physics exactly as in the original, only lower.
    /// </summary>
    public static void Sink(CaffFile caff, int symbol, int index, float dy)
    {
        var c = Open(caff, symbol);
        if (index < 0 || index >= c.N) throw new ArgumentOutOfRangeException(nameof(index));
        int m = c.Mat + MatSize * index, p = c.Pos + PosSize * index;
        BE.WF32(c.D, m + 52, BE.F32(c.D, m + 52) + dy);
        BE.WF32(c.D, p + 4, BE.F32(c.D, p + 4) + dy);
    }

    /// <summary>The world matrix of an instance (row-major, translation in row 4).</summary>
    public static Matrix4x4 GetMatrix(CaffFile caff, int symbol, int index)
    {
        var c = Open(caff, symbol);
        if (index < 0 || index >= c.N) throw new ArgumentOutOfRangeException(nameof(index));
        int m = c.Mat + MatSize * index;
        var f = new float[16];
        for (int k = 0; k < 16; k++) f[k] = BE.F32(c.D, m + 4 * k);
        return new Matrix4x4(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
    }

    /// <summary>Moves an existing instance: writes its world matrix and position record (its collision follows).</summary>
    public static void SetMatrix(CaffFile caff, int symbol, int index, Matrix4x4 world)
    {
        var c = Open(caff, symbol);
        if (index < 0 || index >= c.N) throw new ArgumentOutOfRangeException(nameof(index));
        int m = c.Mat + MatSize * index, p = c.Pos + PosSize * index;
        float[] f = { world.M11, world.M12, world.M13, world.M14, world.M21, world.M22, world.M23, world.M24,
                      world.M31, world.M32, world.M33, world.M34, world.M41, world.M42, world.M43, world.M44 };
        for (int k = 0; k < 16; k++) BE.WF32(c.D, m + 4 * k, f[k]);
        BE.WF32(c.D, p, world.M41); BE.WF32(c.D, p + 4, world.M42); BE.WF32(c.D, p + 8, world.M43);
    }

    /// <summary>
    /// Removes instance <paramref name="index"/> from chunk 12 (the last instance takes its slot). NOT SAFE: the game
    /// hangs loading a world whose instance count shrank. Kept for research; use <see cref="Hide"/>.
    /// </summary>
    public static int Remove(CaffFile caff, int symbol, int index)
    {
        var c = Open(caff, symbol);
        if (index < 0 || index >= c.N) throw new ArgumentOutOfRangeException(nameof(index));
        if (c.N <= 1) throw new InvalidOperationException("cannot remove the only instance");
        int last = c.N - 1, moved = -1;
        if (index != last)
        {
            Buffer.BlockCopy(c.D, c.Rec + RecSize * last, c.D, c.Rec + RecSize * index, RecSize);
            FixRecordIndex(c.D, c.Rec + RecSize * index, last, index);
            Buffer.BlockCopy(c.D, c.Mat + MatSize * last, c.D, c.Mat + MatSize * index, MatSize);
            Buffer.BlockCopy(c.D, c.Pos + PosSize * last, c.D, c.Pos + PosSize * index, PosSize);
            moved = last;
        }
        Array.Clear(c.D, c.Rec + RecSize * last, RecSize);
        Array.Clear(c.D, c.Mat + MatSize * last, MatSize);
        Array.Clear(c.D, c.Pos + PosSize * last, PosSize);
        BE.W32(c.D, c.Idx + 4 * last, 0);
        SetCount(c.D, c.H, last);
        return moved;
    }
}
