using System.Buffers.Binary;
using System.Text;

namespace NB.Core.Formats;

/// <summary>
/// Localised text table (aid_loctext_* assets; English in Debug/11/xx/yy/zz, other languages in loctext/&lt;lang&gt;/).
/// The asset's .data part is: u32 0x0C, u32 0, u32 0, then an "LSBL" block in LITTLE-endian:
///   +0x00 "LSBL"  +0x04 u32 0x1C  +0x08 u32 4  +0x0C u32 offA (0x1C)  +0x10 u32 offB  +0x14 u32 0  +0x18 u32 offC
///   A (at LSBL+offA): u32 size, u32 count, count × (u16 key, u32 offset in UTF-16 units), sentinel (0xFFFF, u32 total UTF-16 units),
///                     UTF-16LE strings (NUL terminated) — the text.
///   B (at LSBL+offB): u32 size, u32 count, count × (u16 key, u16 index, u32 name offset), ASCII names.
///   C (at LSBL+offC): u32 size, u32 count, count × u16 key (order).
/// Only the text in section A is edited; B and C are copied verbatim with their offsets moved.
/// </summary>
public sealed class LocText
{
    public byte[] Prefix = Array.Empty<byte>();          // bytes before LSBL
    public uint HeaderField4 = 0x1C, HeaderField8 = 4, HeaderField14;
    public List<(ushort Key, string Text)> Strings = new();
    public byte[] SectionAPadding = Array.Empty<byte>(); // bytes between the last string and section B (alignment)
    public byte[] SectionB = Array.Empty<byte>();
    public byte[] SectionC = Array.Empty<byte>();
    public byte[] Tail = Array.Empty<byte>();
    public Dictionary<ushort, string> Names = new();
    /// <summary>False for tables whose prefix before "LSBL" is not the standard 12 bytes (layout not yet understood): read-only.</summary>
    public bool Editable = true;
    int _lsbl = 0xC;

    static uint U32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d[o..]);
    static ushort U16(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d[o..]);

    public static bool Is(ReadOnlySpan<byte> data) => data.IndexOf("LSBL"u8) >= 0;

    public static LocText Parse(byte[] data)
    {
        int L = data.AsSpan().IndexOf("LSBL"u8);
        if (L < 0) throw new InvalidDataException("not an LSBL text table");
        // a standard table has 12 bytes before LSBL; some (aid_loctext_banjox_blocks) have a self-describing header
        // whose first big-endian u32 is the LSBL offset — kept verbatim, and nothing after LSBL depends on it
        var t = new LocText { Prefix = data[..L], _lsbl = L, Editable = L == 0xC || (L >= 4 && (data[0] << 24 | data[1] << 16 | data[2] << 8 | data[3]) == L) };
        t.HeaderField4 = U32(data, L + 4); t.HeaderField8 = U32(data, L + 8); t.HeaderField14 = U32(data, L + 0x14);
        int offA = (int)U32(data, L + 0x0C), offB = (int)U32(data, L + 0x10), offC = (int)U32(data, L + 0x18);
        int a = L + offA;
        int count = (int)U32(data, a + 4);
        int entries = a + 8;
        int strBase = entries + 6 * (count + 1);
        int strSize = 2 * (int)U32(data, entries + 6 * count + 2); // stored in UTF-16 units
        for (int i = 0; i < count; i++)
        {
            ushort key = U16(data, entries + 6 * i);
            int off = 2 * (int)U32(data, entries + 6 * i + 2); // UTF-16 units
            int p = strBase + off, e = p;
            while (e + 1 < data.Length && (data[e] | data[e + 1]) != 0) e += 2;
            t.Strings.Add((key, Encoding.Unicode.GetString(data, p, e - p)));
        }
        int strEnd = strBase + strSize;
        int b = L + offB;
        t.SectionAPadding = data[strEnd..b];
        if (offC != 0)
        {
            int c = L + offC;
            t.SectionB = data[b..c];
            int cEnd = Math.Min(data.Length, c + (int)U32(data, c));
            t.SectionC = data[c..cEnd];
            t.Tail = data[cEnd..];
        }
        else
        {
            int bEnd = Math.Min(data.Length, b + (int)U32(data, b));
            t.SectionB = data[b..bEnd];
            t.Tail = data[bEnd..];
            t._noC = true;
        }
        // names (section B)
        int bn = (int)U32(t.SectionB, 4);
        int nameBase = 8 + 8 * bn;
        for (int i = 0; i < bn && 8 + 8 * i + 8 <= t.SectionB.Length; i++)
        {
            ushort key = U16(t.SectionB, 8 + 8 * i);
            int off = (int)U32(t.SectionB, 8 + 8 * i + 4);
            if (nameBase + off < t.SectionB.Length) t.Names[key] = BE_CStr(t.SectionB, nameBase + off);
        }
        return t;
    }

    /// <summary>
    /// The table key of a string name: h = h * 33 + c from h = 0 (djb2 without the 5381 seed), low 16 bits. Verified on
    /// 400 dialog names and 240 of 242 block names; the other two collide and own keys 0 and 1, listed with their hashes
    /// in the blocks table's header (the bytes before LSBL).
    /// </summary>
    public static ushort KeyOf(string name)
    {
        uint h = 0;
        foreach (byte ch in Encoding.Latin1.GetBytes(name)) h = (h << 5) + h + ch;
        return (ushort)h;
    }

    /// <summary>
    /// Adds a named string (or replaces the text of an existing one). Section A stays sorted by key (the game searches
    /// it), section B gets (key, index into C, name) in key order, section C gets the key appended. Returns the key.
    /// </summary>
    public ushort AddOrSet(string name, string text)
    {
        if (!Editable) throw new InvalidOperationException("text table is read-only");
        ushort key = KeyOf(name);
        if (Names.TryGetValue(key, out var existing))
        {
            if (existing != name) throw new InvalidOperationException($"key {key:X4} of '{name}' is already used by '{existing}'");
            int i = Strings.FindIndex(x => x.Key == key); Strings[i] = (key, text); return key;
        }
        if (Strings.Any(x => x.Key == key)) throw new InvalidOperationException($"key {key:X4} is already used by an unnamed string");
        int at = Strings.FindIndex(x => x.Key > key); if (at < 0) at = Strings.Count;
        Strings.Insert(at, (key, text));
        // section B: entries (key, index, name offset) sorted by key, then the names
        int bn = (int)U32(SectionB, 4), nameBase = 8 + 8 * bn;
        var ents = new List<(ushort Key, ushort Index, string Name)>();
        for (int i = 0; i < bn; i++)
            ents.Add((U16(SectionB, 8 + 8 * i), U16(SectionB, 10 + 8 * i), BE_CStr(SectionB, nameBase + (int)U32(SectionB, 12 + 8 * i))));
        int cCount = _noC ? bn : (int)U32(SectionC, 4);
        ents.Add((key, (ushort)cCount, name));
        ents.Sort((x, y) => x.Key.CompareTo(y.Key));
        var names = new MemoryStream(); var offs = new List<int>();
        foreach (var e in ents) { offs.Add((int)names.Length); names.Write(Encoding.Latin1.GetBytes(e.Name)); names.WriteByte(0); }
        var nb = new byte[8 + 8 * ents.Count + names.Length];
        WL32(nb, 0, (uint)nb.Length); WL32(nb, 4, (uint)ents.Count);
        for (int i = 0; i < ents.Count; i++) { WL16(nb, 8 + 8 * i, ents[i].Key); WL16(nb, 10 + 8 * i, ents[i].Index); WL32(nb, 12 + 8 * i, (uint)offs[i]); }
        names.ToArray().CopyTo(nb, 8 + 8 * ents.Count);
        SectionB = nb;
        if (!_noC)
        {
            var nc = new byte[SectionC.Length + 2];
            SectionC.CopyTo(nc, 0);
            int cn = (int)U32(SectionC, 4);
            // keys start at +8; append after the last key (keep any bytes after the key list at the end)
            int keysEnd = 8 + 2 * cn;
            Buffer.BlockCopy(SectionC, keysEnd, nc, keysEnd + 2, SectionC.Length - keysEnd);
            WL16(nc, keysEnd, key); WL32(nc, 0, (uint)nc.Length); WL32(nc, 4, (uint)(cn + 1));
            SectionC = nc;
        }
        Names[key] = name;
        return key;
    }

    static void WL32(byte[] d, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o), v);
    static void WL16(byte[] d, int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(o), v);

    static string BE_CStr(byte[] d, int o) { int e = o; while (e < d.Length && d[e] != 0) e++; return Encoding.Latin1.GetString(d, o, e - o); }

    bool _noC;

    public byte[] Write()
    {
        if (!Editable) throw new InvalidOperationException("This text table has a non-standard prefix and is read-only for now.");
        var ms = new MemoryStream();
        ms.Write(Prefix);
        void W32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); ms.Write(b); }
        void W16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); ms.Write(b); }
        // strings blob
        var blob = new MemoryStream();
        var offs = new List<int>();
        foreach (var (_, text) in Strings) { offs.Add((int)blob.Length / 2); blob.Write(Encoding.Unicode.GetBytes(text)); blob.Write(new byte[2]); }
        int count = Strings.Count;
        int aSize = 8 + 6 * (count + 1) + (int)blob.Length + SectionAPadding.Length; // size includes the trailing pad
        int offA = 0x1C;
        int offB = offA + aSize;
        int offC = _noC ? 0 : offB + SectionB.Length;
        ms.Write("LSBL"u8); W32(HeaderField4); W32(HeaderField8); W32((uint)offA); W32((uint)offB); W32(HeaderField14); W32((uint)offC);
        W32((uint)aSize); W32((uint)count);
        for (int i = 0; i < count; i++) { W16(Strings[i].Key); W32((uint)offs[i]); }
        W16(0xFFFF); W32((uint)blob.Length / 2);
        ms.Write(blob.ToArray());
        ms.Write(SectionAPadding);
        ms.Write(SectionB);
        ms.Write(SectionC);
        ms.Write(Tail);
        return ms.ToArray();
    }
}
