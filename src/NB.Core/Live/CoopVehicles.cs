using NB.Core.IO;

namespace NB.Core.Live;

/// <summary>
/// A player's vehicle design as the game serializes it (exe mod coop-remote-vehicle, SERIALIZE): the saved-blueprint
/// format, 0x7C header (u16 block count, UTF-16 name at +0x20) + 0x24 bytes per block. Other players' games rebuild
/// their puppet from it, so they see the vehicle that player really drives.
/// </summary>
public sealed record CoopDesign(uint Hash, byte[] Bytes)
{
    public const int HeaderSize = 0x7C, BlockSize = 0x24, MaxBlocks = 2000;

    public static CoopDesign? From(byte[] bytes)
    {
        if (bytes.Length < HeaderSize) return null;
        int n = BE.U16(bytes, 0);
        if (n < 1 || n > MaxBlocks || bytes.Length != HeaderSize + BlockSize * n) return null;
        return new CoopDesign(HashOf(bytes), bytes);
    }

    /// <summary>FNV-1a of the bytes (never 0: 0 means "no design").</summary>
    public static uint HashOf(byte[] b)
    {
        uint h = 2166136261;
        foreach (var x in b) { h ^= x; h *= 16777619; }
        return h == 0 ? 1 : h;
    }

    public int Blocks => BE.U16(Bytes, 0);

    /// <summary>The vehicle's name (big-endian UTF-16 at +0x20).</summary>
    public string Name
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            for (int o = 0x20; o + 1 < 0x60; o += 2) { char c = (char)BE.U16(Bytes, o); if (c == 0) break; sb.Append(c); }
            return sb.ToString().Trim();
        }
    }
}

/// <summary>
/// The damage of a player's vehicle (co-op packet type 10): the health of every block below full and the blocks that
/// broke off, by block grid word (block +0xEC, the same in the rebuilt puppets: they are built from the same design).
/// </summary>
public sealed class CoopDamage
{
    public uint Design;
    public uint Seq;
    /// <summary>Grid word -> health fraction (only blocks below full health).</summary>
    public Dictionary<uint, float> Health = new();
    /// <summary>Blocks of the design that are no longer on the vehicle.</summary>
    public HashSet<uint> Missing = new();
    public const int MaxEntries = 200;

    static byte Q(float f) => (byte)Math.Clamp(MathF.Round(f * 255f), 0, 255);

    /// <summary>u32 design, u32 seq, u16 damaged count, u16 missing count, then damaged (u32 grid, u8 health x 255) and
    /// missing (u32 grid). At most <see cref="MaxEntries"/> entries (the missing first, then the most damaged).</summary>
    public byte[] Write()
    {
        var miss = Missing.Take(MaxEntries).ToList();
        var dmg = Health.OrderBy(kv => kv.Value).Take(MaxEntries - miss.Count).ToList();
        var b = new byte[12 + 5 * dmg.Count + 4 * miss.Count];
        BE.W32(b, 0, Design); BE.W32(b, 4, Seq); BE.W16(b, 8, (ushort)dmg.Count); BE.W16(b, 10, (ushort)miss.Count);
        int o = 12;
        foreach (var (g, f) in dmg) { BE.W32(b, o, g); b[o + 4] = Q(f); o += 5; }
        foreach (var g in miss) { BE.W32(b, o, g); o += 4; }
        return b;
    }

    public static CoopDamage? Read(byte[] b, int at)
    {
        if (b.Length < at + 12) return null;
        var d = new CoopDamage { Design = BE.U32(b, at), Seq = BE.U32(b, at + 4) };
        int nd = BE.U16(b, at + 8), nm = BE.U16(b, at + 10);
        if (nd + nm > MaxEntries || b.Length < at + 12 + 5 * nd + 4 * nm) return null;
        int o = at + 12;
        for (int i = 0; i < nd; i++, o += 5) d.Health[BE.U32(b, o)] = b[o + 4] / 255f;
        for (int i = 0; i < nm; i++, o += 4) d.Missing.Add(BE.U32(b, o));
        return d;
    }

    /// <summary>Same damage as <paramref name="o"/> (at the precision that is sent)?</summary>
    public bool SameAs(CoopDamage? o) =>
        o != null && o.Design == Design && o.Missing.SetEquals(Missing) && o.Health.Count == Health.Count
        && Health.All(kv => o.Health.TryGetValue(kv.Key, out var f) && Q(f) == Q(kv.Value));
}
