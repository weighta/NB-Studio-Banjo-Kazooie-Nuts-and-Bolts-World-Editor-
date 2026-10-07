using System.Security.Cryptography;
using System.Text;
using NB.Core.Formats;
using NB.Core.IO;

namespace NB.Core.Vehicles;

/// <summary>What a vehicle file is: an Xbox 360 package, the content file extracted from one, or a bare blueprint
/// (the .data of an aid_vehicle asset).</summary>
public enum VehicleFileKind { Package, Content, Blueprint }

/// <summary>
/// A vehicle the player saved on the console or in Xenia, or a bare blueprint.
/// <para><b>Package</b>: STFS "CON " content package (title 4D5307ED, content type 1 = saved game, display name
/// "VEHICLE: name"), one file inside named like the package without "0x" (package 0x00000082 holds file 00000082). The
/// console names them 0x00000001, 0x00000002, … (the lowest free index); they sit next to the save slots 0x0b0a5c5c /
/// 0x0b0d6cca in Content\&lt;profile&gt;\4D5307ED\00000001. The package thumbnail is the game icon.</para>
/// <para><b>Content file</b> (what the package holds, also what Xenia keeps in content\&lt;xuid&gt;\4D5307ED\00000001\0x0000000N\):
/// an 8-byte prefix (f32 1.21, f32 3.32 — photo files start with f32 1.21, f32 9.73) and the blueprint.</para>
/// <para><b>Blueprint</b>: the bare blueprint (no prefix), as in the game's own aid_vehicle assets.</para>
/// </summary>
public sealed class VehicleFile
{
    public static readonly byte[] DefaultPrefix = { 0x3F, 0x9A, 0xE1, 0x48, 0x40, 0x54, 0x7A, 0xE1 };
    public const uint TitleId = 0x4D5307ED;

    public VehicleFileKind Kind;
    public string? Path;
    public byte[] Original = Array.Empty<byte>();
    public StfsPackage? Package;
    /// <summary>The package's file that holds the vehicle.</summary>
    public string InnerName = "";
    public byte[] Prefix = (byte[])DefaultPrefix.Clone();
    public Blueprint Blueprint = new();
    /// <summary>Problems found while reading (damaged package, truncated content, …); empty when the file is sound.</summary>
    public readonly List<string> Problems = new();

    public string DisplayName => Package?.DisplayName is { Length: > 0 } d ? d : "VEHICLE: " + Blueprint.Name;

    /// <summary>Content file prefix check: f32 1.21 then f32 3.32.</summary>
    public static bool IsContent(ReadOnlySpan<byte> d) =>
        d.Length >= 8 + Blueprint.HeaderSize && d[..8].SequenceEqual(DefaultPrefix) && Blueprint.LooksLike(d, 8);

    /// <summary>The vehicle prefix with a header that does not fit (damaged / hand-edited content): readable with problems.</summary>
    public static bool IsDamagedContent(ReadOnlySpan<byte> d) =>
        d.Length >= 8 + Blueprint.HeaderSize && d[..8].SequenceEqual(DefaultPrefix) && !Blueprint.LooksLike(d, 8);

    /// <summary>The kind of a file from its first bytes (null: not a vehicle file — e.g. a photo or a save slot).</summary>
    public static VehicleFileKind? Detect(ReadOnlySpan<byte> d)
    {
        if (StfsPackage.IsStfs(d)) return VehicleFileKind.Package;
        if (IsContent(d)) return VehicleFileKind.Content;
        // a bare blueprint: header + exactly the declared parts (photo content starts f32 1.21, f32 9.73: never taken for one)
        if (d.Length >= 4 && d[..4].SequenceEqual(DefaultPrefix.AsSpan(0, 4))) return null;
        if (Blueprint.LooksLike(d) && BE.U16(d, 0) > 0 && d.Length == Blueprint.HeaderSize + BE.U16(d, 0) * Blueprint.BlockSize) return VehicleFileKind.Blueprint;
        if (IsDamagedContent(d)) return VehicleFileKind.Content;
        return null;
    }

    /// <summary>Is this STFS package a N&amp;B vehicle (title, content type 1, display name or the file inside)?</summary>
    public static bool IsVehiclePackage(StfsPackage p)
    {
        if (p.TitleId != TitleId) return false;
        if (p.DisplayName.StartsWith("VEHICLE:", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var f in p.Files.Where(f => !f.IsDirectory && f.Size < 4 << 20))
            try { if (IsContent(p.Extract(f))) return true; } catch { }
        return false;
    }

    public static VehicleFile Open(string path)
    {
        byte[] d;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            d = new byte[fs.Length];
            fs.ReadExactly(d);
        }
        var v = Read(d);
        v.Path = path;
        return v;
    }

    public static VehicleFile Read(byte[] d)
    {
        var kind = Detect(d) ?? throw new InvalidDataException("not a Nuts & Bolts vehicle: neither an Xbox 360 package, a vehicle content file nor a blueprint");
        var v = new VehicleFile { Kind = kind, Original = d };
        switch (kind)
        {
            case VehicleFileKind.Package:
                var p = v.Package = new StfsPackage(d);
                if (p.TitleId != TitleId) throw new InvalidDataException($"this package belongs to another game (title {p.TitleId:X8}, \"{p.DisplayName}\")");
                v.CheckPackage(d);
                StfsPackage.Entry? entry = null; byte[]? data = null;
                foreach (var f in p.Files.Where(f => !f.IsDirectory && f.Size < 16 << 20))
                {
                    byte[] x;
                    try { x = p.Extract(f); } catch { continue; }
                    if (Detect(x) == VehicleFileKind.Content) { entry = f; data = x; break; }
                    if (x.Length >= 8 && x.AsSpan(0, 8).SequenceEqual(DefaultPrefix)) { entry = f; data = x; }
                }
                if (entry == null || data == null)
                {
                    if (NbPhotoLike(p)) throw new InvalidDataException($"\"{p.DisplayName}\" is a photo, not a vehicle");
                    throw new InvalidDataException(v.Problems.Count > 0
                        ? $"\"{p.DisplayName}\": the package is damaged and its vehicle cannot be read ({string.Join("; ", v.Problems)})"
                        : $"\"{p.DisplayName}\" holds no vehicle ({p.Files.Count} file(s))");
                }
                v.InnerName = entry.Name;
                v.ReadContent(data);
                break;
            case VehicleFileKind.Content:
                v.ReadContent(d);
                break;
            default:
                v.Blueprint = Blueprint.Parse(d);
                break;
        }
        return v;
    }

    static bool NbPhotoLike(StfsPackage p) => p.DisplayName.EndsWith("PHOTO", StringComparison.OrdinalIgnoreCase);

    /// <summary>The vehicle file as read (package: the file inside).</summary>
    public byte[]? OriginalContent;

    void ReadContent(byte[] d)
    {
        OriginalContent = d;
        Prefix = d.AsSpan(0, 8).ToArray();
        Blueprint = Blueprint.Parse(d, 8);
        if (Blueprint.DeclaredCount != Blueprint.Blocks.Count)
            Problems.Add($"the file declares {Blueprint.DeclaredCount} parts but holds only {Blueprint.Blocks.Count}");
        if (Blueprint.Trailing.Length > 0) Problems.Add($"{Blueprint.Trailing.Length} extra bytes after the last part (kept)");
        int odd = Blueprint.Blocks.Count(b => b.Part >> 24 != 0x1F);
        if (odd > 0) Problems.Add($"{odd} part record(s) do not hold a part id (the file looks damaged or hand-edited)");
    }

    /// <summary>Integrity of a package: header hash (0x32C), top hash of the active level-0 hash table, block hashes.</summary>
    void CheckPackage(byte[] d)
    {
        var s = StfsIntegrity.Check(d);
        if (!s.HeaderHashOk) Problems.Add("header hash mismatch (the package was damaged or edited without rehashing)");
        if (!s.TopHashOk) Problems.Add("top hash mismatch");
        if (s.BadBlocks > 0) Problems.Add($"{s.BadBlocks} data block(s) do not match their hashes");
    }

    /// <summary>Content file bytes (prefix + blueprint).</summary>
    public byte[] ContentBytes(Blueprint? bp = null)
    {
        var b = (bp ?? Blueprint).Write();
        var o = new byte[8 + b.Length];
        Prefix.CopyTo(o, 0);
        b.CopyTo(o, 8);
        return o;
    }

    /// <summary>
    /// This vehicle as an Xbox 360 package. When the vehicle came from a package its header is kept (console
    /// certificate, profile and console ids, thumbnails) and only the vehicle file, the display name and the hashes
    /// change — an unchanged vehicle gives back the original bytes. Otherwise the header comes from
    /// <paramref name="templatePackage"/> (another vehicle package of the player) or is made from scratch (no console
    /// certificate, profile id <paramref name="profileId"/> or 0). <paramref name="packageName"/> (0x0000000N) names the
    /// file inside; default: the original's.
    /// </summary>
    public byte[] ToPackage(Blueprint? bp = null, string? packageName = null, byte[]? templatePackage = null, ulong? profileId = null,
        byte[]? thumbnailPng = null)
    {
        bp ??= Blueprint;
        var content = ContentBytes(bp);
        string inner = packageName != null ? InnerFileName(packageName) : InnerName.Length > 0 ? InnerName : "00000001";
        string display = "VEHICLE: " + (bp.Name.Length > 0 ? bp.Name : "NEW BLUEPRINT");
        if (Package != null && Kind == VehicleFileKind.Package)
        {
            bool sameName = bp.Name == Blueprint.Name;
            if (sameName) display = Package.DisplayName;                                  // keep the console's text when the name did not change
            if (sameName && inner == InnerName && thumbnailPng == null && profileId == null && OriginalContent != null
                && content.AsSpan().SequenceEqual(OriginalContent))
                return Original;                                                        // unchanged: byte for byte
        }
        byte[] header = Kind == VehicleFileKind.Package && Package != null ? StfsIntegrity.HeaderOf(Original)
            : templatePackage != null ? StfsIntegrity.HeaderOf(templatePackage)
            : StfsBuilder.NewHeader(TitleId, "Banjo Kazooie: N&B");
        return StfsBuilder.Build(header, new[] { new StfsBuilder.File(inner, content, StfsBuilder.Now()) }, display, thumbnailPng, profileId);
    }

    /// <summary>"0x00000082" → "00000082" (the file inside a vehicle package).</summary>
    public static string InnerFileName(string packageName)
    {
        var n = System.IO.Path.GetFileName(packageName);
        if (n.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) n = n[2..];
        return n.Length == 8 && uint.TryParse(n, System.Globalization.NumberStyles.HexNumber, null, out _) ? n.ToLowerInvariant() : "00000001";
    }

    /// <summary>The lowest free package name 0x0000000N in <paramref name="folder"/> (the console's rule).</summary>
    public static string NextFreePackageName(string folder)
    {
        var used = new HashSet<uint>();
        if (Directory.Exists(folder))
            foreach (var f in Directory.EnumerateFileSystemEntries(folder))
            {
                var n = System.IO.Path.GetFileName(f);
                if (n.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && uint.TryParse(n[2..], System.Globalization.NumberStyles.HexNumber, null, out var x)) used.Add(x);
            }
        uint i = 1;
        while (used.Contains(i)) i++;
        return $"0x{i:x8}";
    }

    /// <summary>
    /// Installs a vehicle package into a Xenia content folder the way Xenia keeps content: the file extracted to
    /// &lt;root&gt;\&lt;xuid&gt;\4D5307ED\00000001\&lt;name&gt;\&lt;file&gt; and the package header (0..0x971A, then the name) in
    /// Headers\00000001\&lt;name&gt;.header. Returns the content folder.
    /// </summary>
    public static string InstallToXenia(byte[] package, string profileDir, string? packageName = null)
    {
        var p = new StfsPackage(package);
        var saves = System.IO.Path.Combine(profileDir, "4D5307ED", "00000001");
        packageName ??= NextFreePackageName(saves);
        var dir = System.IO.Path.Combine(saves, packageName);
        Directory.CreateDirectory(dir);
        // the game opens the file named like the content (0x00000082 -> 00000082): a vehicle package's single file is written
        // under that name whatever it was called inside (a package installed under another number showed "CORRUPT VEHICLE!")
        var files = p.Files.Where(f => !f.IsDirectory).ToList();
        foreach (var f in files)
            File.WriteAllBytes(System.IO.Path.Combine(dir, files.Count == 1 ? InnerFileName(packageName) : f.Name), p.Extract(f));
        var hdrDir = System.IO.Path.Combine(profileDir, "4D5307ED", "Headers", "00000001");
        Directory.CreateDirectory(hdrDir);
        var h = new byte[0xA000];
        Array.Copy(package, h, Math.Min(0x971A, package.Length));
        Encoding.ASCII.GetBytes(packageName).CopyTo(h, 0x971A);
        File.WriteAllBytes(System.IO.Path.Combine(hdrDir, packageName + ".header"), h);
        return dir;
    }
}

/// <summary>STFS integrity checks (hashes as the console writes them).</summary>
public static class StfsIntegrity
{
    public sealed record Status(bool HeaderHashOk, bool TopHashOk, int BadBlocks, int ActiveTable);

    public static int Base(ReadOnlySpan<byte> d) => (int)((BE.U32(d, 0x340) + 0xFFF) & ~0xFFFu);

    /// <summary>Checks the header hash, the top hash (active level-0 table) and every allocated block's hash. Single-level
    /// packages (up to 170 blocks) are fully checked; bigger ones only their first table.</summary>
    public static Status Check(ReadOnlySpan<byte> d)
    {
        int b = Base(d);
        if (d.Length < b + 0x1000) return new(false, false, 0, 0);
        bool hdr = SHA1.HashData(d[0x344..b]).AsSpan().SequenceEqual(d.Slice(0x32C, 20));
        int sep = d[0x379 + 2];
        int tables = (~sep & 1) == 1 ? 2 : 1;
        int active = tables == 2 && (sep & 2) != 0 ? 1 : 0;
        var top = d.Slice(0x379 + 8, 20);
        bool topOk = SHA1.HashData(d.Slice(b + active * 0x1000, 0x1000)).AsSpan().SequenceEqual(top);
        int alloc = (int)BE.U32(d, 0x379 + 0x1C);
        int bad = 0;
        var ht = d.Slice(b + active * 0x1000, 0x1000);
        for (int i = 0; i < Math.Min(alloc, 0xAA); i++)
        {
            long off = b + ((long)tables + i) * 0x1000;
            if (off + 0x1000 > d.Length) { bad++; continue; }
            if ((ht[i * 0x18 + 0x14] & 0x80) == 0) continue;                // free block (0x00 unused, 0x40 freed): its hash is stale by design
            if (!SHA1.HashData(d.Slice((int)off, 0x1000)).AsSpan().SequenceEqual(ht.Slice(i * 0x18, 20))) bad++;
        }
        return new(hdr, topOk, bad, active);
    }

    /// <summary>The metadata part of a package (everything before the first hash table).</summary>
    public static byte[] HeaderOf(byte[] package) => package.AsSpan(0, Base(package)).ToArray();
}

/// <summary>
/// Builds single-level STFS packages (up to 170 data blocks = 680 KB, plenty for saves): file table, files in consecutive
/// blocks, two identical level-0 hash tables (block separation 0 like the console; SHA-1 per block, status 0x80 in use,
/// next-block links), top hash in the volume descriptor, block counts, header hash 0x32C = SHA-1(0x344 .. first table).
/// The console signature (0x1AC, over the header hash) needs the console's private key and is left as it was: Xenia
/// does not check it; a real console needs the package rehashed and resigned (Horizon / Velocity / Le Fluffie).
/// </summary>
public static class StfsBuilder
{
    public sealed record File(string Name, byte[] Data, (uint Updated, uint Accessed) Times);

    /// <summary>FAT-style timestamp (as STFS file entries keep them) for now.</summary>
    public static (uint, uint) Now()
    {
        var t = DateTime.Now;
        uint v = (uint)((t.Year - 1980) << 25 | t.Month << 21 | t.Day << 16 | t.Hour << 11 | t.Minute << 5 | t.Second / 2);
        return (v, v);
    }

    /// <summary>A fresh "CON " header for a saved-game package of <paramref name="titleId"/> (no certificate, no
    /// thumbnail, profile id 0, console id 0).</summary>
    public static byte[] NewHeader(uint titleId, string titleName)
    {
        var h = new byte[0xA000];
        "CON "u8.CopyTo(h);
        for (int i = 0; i < 8; i++) h[0x22C + i] = 0xFF;          // license: everyone
        BE.W32(h, 0x340, 0x971Au);                                // header size
        BE.W32(h, 0x344, 1u);                                     // content type: saved game
        BE.W32(h, 0x348, 2u);                                     // metadata version
        BE.W32(h, 0x354, 0x3E567DFFu);                            // media id of the game disc (as in every sample)
        BE.W32(h, 0x358, 1u); BE.W32(h, 0x35C, 1u);               // version / base version
        BE.W32(h, 0x360, titleId);
        h[0x366] = 1; h[0x367] = 1;                               // disc 1 of 1
        h[0x379] = 0x24;                                          // volume descriptor size
        var tn = Encoding.BigEndianUnicode.GetBytes(titleName);
        tn.AsSpan(0, Math.Min(tn.Length, 0x7E)).CopyTo(h.AsSpan(0x1691));
        h[0x1711] = 0x40;                                         // transfer flags as the console writes them
        return h;
    }

    public static byte[] Build(byte[] header, IReadOnlyList<File> files, string? displayName = null, byte[]? thumbnailPng = null, ulong? profileId = null)
    {
        int baseOff = Math.Max(StfsIntegrity.Base(header), 0xA000);
        const int tables = 2;
        int ftBlocks = Math.Max(1, (files.Count * 0x40 + 0xFFF) / 0x1000);
        var starts = new int[files.Count]; var counts = new int[files.Count];
        int next = ftBlocks;
        for (int i = 0; i < files.Count; i++)
        {
            counts[i] = (files[i].Data.Length + 0xFFF) / 0x1000;
            starts[i] = counts[i] > 0 ? next : 0;
            next += counts[i];
        }
        int total = next;
        if (total > 0xAA) throw new InvalidDataException($"the package needs {total} blocks ({total * 4} KB); single-level packages hold up to 170 blocks (680 KB)");

        var o = new byte[baseOff + (tables + total) * 0x1000];
        Array.Copy(header, o, Math.Min(header.Length, baseOff));
        long Data(int b) => baseOff + ((long)tables + b) * 0x1000;

        // file table
        for (int i = 0; i < files.Count; i++)
        {
            int at = (int)Data(0) + i * 0x40;
            var name = Encoding.ASCII.GetBytes(files[i].Name);
            if (name.Length > 0x28) throw new InvalidDataException("file name longer than 40 characters: " + files[i].Name);
            name.CopyTo(o, at);
            o[at + 0x28] = (byte)(name.Length | 0x40);                                  // consecutive blocks
            foreach (int k in new[] { 0x29, 0x2C }) { o[at + k] = (byte)counts[i]; o[at + k + 1] = (byte)(counts[i] >> 8); o[at + k + 2] = (byte)(counts[i] >> 16); }
            o[at + 0x2F] = (byte)starts[i]; o[at + 0x30] = (byte)(starts[i] >> 8); o[at + 0x31] = (byte)(starts[i] >> 16);
            o[at + 0x32] = 0xFF; o[at + 0x33] = 0xFF;                                   // parent: root
            BE.W32(o, at + 0x34, (uint)files[i].Data.Length);
            BE.W32(o, at + 0x38, files[i].Times.Updated);
            BE.W32(o, at + 0x3C, files[i].Times.Accessed);
        }
        for (int i = 0; i < files.Count; i++) if (counts[i] > 0) Array.Copy(files[i].Data, 0, o, Data(starts[i]), files[i].Data.Length);

        // level-0 hash table
        var ht = new byte[0x1000];
        var last = new HashSet<int> { ftBlocks - 1 };
        for (int i = 0; i < files.Count; i++) if (counts[i] > 0) last.Add(starts[i] + counts[i] - 1);
        for (int b = 0; b < total; b++)
        {
            SHA1.HashData(o.AsSpan((int)Data(b), 0x1000)).CopyTo(ht, b * 0x18);
            ht[b * 0x18 + 0x14] = 0x80;
            int nb = last.Contains(b) ? 0xFFFFFF : b + 1;
            ht[b * 0x18 + 0x15] = (byte)(nb >> 16); ht[b * 0x18 + 0x16] = (byte)(nb >> 8); ht[b * 0x18 + 0x17] = (byte)nb;
        }
        for (int t = 0; t < tables; t++) ht.CopyTo(o, baseOff + t * 0x1000);

        // volume descriptor
        const int vd = 0x379;
        o[vd] = 0x24; o[vd + 1] = 0; o[vd + 2] = 0;                                     // block separation 0: two tables, first active
        o[vd + 3] = (byte)ftBlocks; o[vd + 4] = (byte)(ftBlocks >> 8);
        o[vd + 5] = o[vd + 6] = o[vd + 7] = 0;                                         // file table at block 0
        SHA1.HashData(ht).CopyTo(o, vd + 8);
        BE.W32(o, vd + 0x1C, (uint)total);
        BE.W32(o, vd + 0x20, 0u);

        if (displayName != null)
        {
            Array.Clear(o, 0x411, 0x80);
            var dn = Encoding.BigEndianUnicode.GetBytes(displayName);
            dn.AsSpan(0, Math.Min(dn.Length, 0x7E)).CopyTo(o.AsSpan(0x411));
        }
        if (thumbnailPng != null)
        {
            if (thumbnailPng.Length > 0x4000) throw new InvalidDataException("thumbnail larger than 16 KB");
            Array.Clear(o, 0x171A, 0x4000);
            thumbnailPng.CopyTo(o, 0x171A);
            BE.W32(o, 0x1712, (uint)thumbnailPng.Length);
            if (BE.U32(o, 0x1716) == 0)
            {
                Array.Clear(o, 0x571A, 0x4000);
                thumbnailPng.CopyTo(o, 0x571A);
                BE.W32(o, 0x1716, (uint)thumbnailPng.Length);
            }
        }
        if (profileId is ulong pid) BE.W64(o, 0x371, pid);
        SHA1.HashData(o.AsSpan(0x344, baseOff - 0x344)).CopyTo(o, 0x32C);
        return o;
    }
}
