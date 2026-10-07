using NB.Core.IO;

namespace NB.Core.Havok;

/// <summary>
/// Editable form of a Havok 5.5 binary packfile (32-bit big-endian) that re-serializes the section layout.
/// Each section is split into its payload (bytes before the local fixups) and its fixup tables; the payload can be
/// patched or grown and fixups added, then <see cref="Write"/> lays the sections out again:
/// payload (16-aligned) · local fixups (src,dst) · global fixups (src,section,dst) · virtual fixups
/// (obj,section,classname offset) — each table padded to 16 bytes with 0xFF — · exports · imports (raw, kept as is).
/// The 0x40-byte file header and 0x30-byte section headers keep every unknown byte; only the offsets change.
/// </summary>
public sealed class HkPackfileWriter
{
    public sealed class Section
    {
        public byte[] Header = new byte[0x30];                    // original section header (tag + offsets)
        public List<byte> Payload = new();
        public List<(int Src, int Dst)> Local = new();
        public List<(int Src, int Section, int Dst)> Global = new();
        public List<(int Obj, int Section, int ClassName)> Virtual = new();
        public byte[] Exports = Array.Empty<byte>(), Imports = Array.Empty<byte>();
        public string Tag => BE.CStr(Header, 0, 19);
    }

    public byte[] FileHeader = new byte[0x40];
    /// <summary>Bytes after the last section (some assets pad the packfile, e.g. to 0x2000); kept after the sections.</summary>
    public byte[] Trailer = Array.Empty<byte>();
    public readonly List<Section> Sections = new();
    public int DataIndex => Sections.FindIndex(s => s.Tag == "__data__");
    public Section Data => Sections[DataIndex];

    public static HkPackfileWriter Read(byte[] p)
    {
        if (BE.U32(p, 0) != 0x57E0E057 || BE.U32(p, 4) != 0x10C0C010) throw new InvalidDataException("not a Havok packfile");
        var w = new HkPackfileWriter();
        Array.Copy(p, w.FileHeader, 0x40);
        int n = BE.S32(p, 20), last = 0x40 + 0x30 * n;
        for (int i = 0; i < n; i++)
        {
            int h = 0x40 + 0x30 * i;
            var s = new Section();
            Array.Copy(p, h, s.Header, 0, 0x30);
            int start = BE.S32(p, h + 20), local = BE.S32(p, h + 24), global = BE.S32(p, h + 28), virt = BE.S32(p, h + 32),
                exports = BE.S32(p, h + 36), imports = BE.S32(p, h + 40), end = BE.S32(p, h + 44);
            s.Payload.AddRange(p.AsSpan(start, local).ToArray());
            for (int k = local; k + 8 <= global; k += 8)
            {
                int src = BE.S32(p, start + k); if (src == -1) break;
                s.Local.Add((src, BE.S32(p, start + k + 4)));
            }
            for (int k = global; k + 12 <= virt; k += 12)
            {
                int src = BE.S32(p, start + k); if (src == -1) break;
                s.Global.Add((src, BE.S32(p, start + k + 4), BE.S32(p, start + k + 8)));
            }
            for (int k = virt; k + 12 <= exports; k += 12)
            {
                int obj = BE.S32(p, start + k); if (obj == -1) break;
                s.Virtual.Add((obj, BE.S32(p, start + k + 4), BE.S32(p, start + k + 8)));
            }
            s.Exports = p.AsSpan(start + exports, imports - exports).ToArray();
            s.Imports = p.AsSpan(start + imports, end - imports).ToArray();
            w.Sections.Add(s);
            last = Math.Max(last, start + end);
        }
        if (last < p.Length) w.Trailer = p.AsSpan(last).ToArray();
        return w;
    }

    /// <summary>Appends bytes to a section's payload at a 16-byte boundary (padding with <paramref name="pad"/>); returns their offset.</summary>
    public int Append(int section, ReadOnlySpan<byte> bytes, byte pad = 0)
    {
        var pl = Sections[section].Payload;
        while (pl.Count % 16 != 0) pl.Add(pad);
        int at = pl.Count;
        pl.AddRange(bytes.ToArray());
        return at;
    }

    /// <summary>Points the pointer at <paramref name="src"/> (in <paramref name="section"/>) at <paramref name="dst"/> of the same section.</summary>
    public void SetLocalPointer(int section, int src, int dst)
    {
        var s = Sections[section];
        s.Global.RemoveAll(g => g.Src == src);
        int i = s.Local.FindIndex(l => l.Src == src);
        if (i >= 0) s.Local[i] = (src, dst); else s.Local.Add((src, dst));
        BE.W32(PayloadSpan(section), src, 0);   // pointers are stored as 0 in the file; the loader writes them
    }

    public Span<byte> PayloadSpan(int section) => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(Sections[section].Payload);

    /// <summary>
    /// Drops the payload bytes of a section that lie outside <paramref name="live"/> (whole 16-byte blocks, so every kept
    /// byte keeps its alignment) and moves every fixup that lives in or points into the section, and the file's contents
    /// offset. Fixups living in a dropped block are removed; a pointer into a dropped block (an empty array) is moved to
    /// where that block was. Returns the number of bytes removed; 0 (nothing changed) when the section has exports or
    /// imports, which are kept as they are.
    /// </summary>
    public int Compact(int section, IEnumerable<(int Start, int Length)> live)
    {
        var s = Sections[section];
        if (s.Exports.Length > 0 || s.Imports.Length > 0) return 0;
        int n = s.Payload.Count, blocks = (n + 15) / 16;
        var keep = new bool[blocks];
        foreach (var (a, l) in live)
        {
            if (l <= 0) continue;
            for (int b = Math.Max(0, a / 16); b < Math.Min(blocks, (a + l + 15) / 16); b++) keep[b] = true;
        }
        var start = new int[blocks + 1]; int acc = 0;
        for (int b = 0; b < blocks; b++) { start[b] = acc; if (keep[b]) acc += 16; }
        start[blocks] = acc;
        if (keep.All(k => k)) return 0;
        int Map(int o) { int b = o / 16; return b >= blocks ? acc + (o - blocks * 16) : start[b] + (keep[b] ? o % 16 : 0); }
        bool Live(int o) { int b = o / 16; return b < blocks && keep[b]; }
        var np = new List<byte>(acc);
        for (int b = 0; b < blocks; b++)
            if (keep[b]) for (int k = 16 * b; k < Math.Min(n, 16 * b + 16); k++) np.Add(s.Payload[k]);
        s.Local = s.Local.Where(l => Live(l.Src)).Select(l => (Map(l.Src), Map(l.Dst))).ToList();
        s.Global = s.Global.Where(g => Live(g.Src)).Select(g => (Map(g.Src), g.Section, g.Section == section ? Map(g.Dst) : g.Dst)).ToList();
        s.Virtual = s.Virtual.Where(v => Live(v.Obj)).Select(v => (Map(v.Obj), v.Section, v.ClassName)).ToList();
        for (int i = 0; i < Sections.Count; i++)
            if (i != section) Sections[i].Global = Sections[i].Global.Select(g => (g.Src, g.Section, g.Section == section ? Map(g.Dst) : g.Dst)).ToList();
        if (BE.S32(FileHeader, 0x18) == section) BE.W32(FileHeader, 0x1C, Map(BE.S32(FileHeader, 0x1C)));
        int removed = n - np.Count;
        s.Payload = np;
        return removed;
    }

    public byte[] Write()
    {
        var ms = new MemoryStream();
        ms.Write(FileHeader);
        ms.Write(new byte[0x30 * Sections.Count]);
        var headers = new List<byte[]>();
        foreach (var s in Sections)
        {
            int start = (int)ms.Length;
            var b = new List<byte>(s.Payload);
            void Pad() { while (b.Count % 16 != 0) b.Add(0xFF); }
            void W(int v) { b.Add((byte)(v >> 24)); b.Add((byte)(v >> 16)); b.Add((byte)(v >> 8)); b.Add((byte)v); }
            while (b.Count % 16 != 0) b.Add(0);
            int local = b.Count;
            foreach (var (src, dst) in s.Local) { W(src); W(dst); }
            Pad();
            int global = b.Count;
            foreach (var (src, sec, dst) in s.Global) { W(src); W(sec); W(dst); }
            Pad();
            int virt = b.Count;
            foreach (var (obj, sec, cn) in s.Virtual) { W(obj); W(sec); W(cn); }
            Pad();
            int exports = b.Count; b.AddRange(s.Exports);
            int imports = b.Count; b.AddRange(s.Imports);
            int end = b.Count;
            ms.Write(b.ToArray());
            var h = (byte[])s.Header.Clone();
            BE.W32(h, 20, start); BE.W32(h, 24, local); BE.W32(h, 28, global); BE.W32(h, 32, virt);
            BE.W32(h, 36, exports); BE.W32(h, 40, imports); BE.W32(h, 44, end);
            headers.Add(h);
        }
        ms.Write(Trailer);
        var result = ms.ToArray();
        for (int i = 0; i < headers.Count; i++) Array.Copy(headers[i], 0, result, 0x40 + 0x30 * i, 0x30);
        return result;
    }
}
