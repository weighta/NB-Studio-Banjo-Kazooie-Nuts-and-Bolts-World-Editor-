using System.Text;

namespace NB.Core.Formats;

/// <summary>
/// Read-only Xbox 360 STFS package ("CON " signed by a console, "LIVE" / "PIRS" by Microsoft): the container the
/// console stores saves, photos and downloads in. Metadata at fixed header offsets (header size 0x340, content type
/// 0x344, title id 0x360, profile id 0x371, volume descriptor 0x379, display name 0x411 and description 0xD11 as
/// UTF-16BE, thumbnail size 0x1712 / image 0x171A). Files live in 4 KB blocks with hash tables between every 0xAA
/// blocks; a file's blocks are consecutive or chained through the hash table entries (next block at +0x15).
/// </summary>
public sealed class StfsPackage
{
    public sealed record Entry(string Name, bool IsDirectory, bool Consecutive, int Blocks, int StartBlock, short Parent, uint Size);

    readonly byte[] _d;
    readonly int _base, _sex, _separation;
    public string Magic { get; }
    public uint ContentType { get; }
    public uint TitleId { get; }
    public ulong ProfileId { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public byte[]? Thumbnail { get; }
    public List<Entry> Files { get; } = new();

    public static bool IsStfs(ReadOnlySpan<byte> head) =>
        head.Length >= 4 && (head[..4].SequenceEqual("CON "u8) || head[..4].SequenceEqual("LIVE"u8) || head[..4].SequenceEqual("PIRS"u8));

    public StfsPackage(byte[] data)
    {
        if (data.Length < 0xB000 || !IsStfs(data)) throw new InvalidDataException("not an Xbox 360 STFS package (CON/LIVE/PIRS)");
        _d = data;
        Magic = Encoding.ASCII.GetString(data, 0, 4).Trim();
        uint headerSize = U32(0x340);
        ContentType = U32(0x344);
        TitleId = U32(0x360);
        ProfileId = (ulong)U32(0x371) << 32 | U32(0x375);
        DisplayName = Utf16(0x411, 0x80);
        Description = Utf16(0xD11, 0x80);
        uint thumb = U32(0x1712);
        if (thumb is > 0 and <= 0x4000) Thumbnail = data.AsSpan(0x171A, (int)thumb).ToArray();
        // volume descriptor: +2 block separation, +3 u16 LE file table block count, +5 int24 LE file table block
        _separation = data[0x379 + 2];
        _sex = ~_separation & 1;                                 // 1: two hash table copies per level
        _base = (int)((headerSize + 0xFFF) & ~0xFFFu);           // first hash table
        int ftCount = data[0x379 + 3] | data[0x379 + 4] << 8;
        int ftBlock = Int24Le(0x379 + 5);
        var table = ReadBlocks(ftBlock, ftCount, consecutive: false, ftCount * 0x1000);
        for (int o = 0; o + 0x40 <= table.Length; o += 0x40)
        {
            if (table[o] == 0) break;
            int flags = table[o + 0x28];
            var name = Encoding.ASCII.GetString(table, o, flags & 0x3F);
            Files.Add(new Entry(name, (flags & 0x80) != 0, (flags & 0x40) != 0, Int24Le(table, o + 0x29), Int24Le(table, o + 0x2F),
                (short)(table[o + 0x32] << 8 | table[o + 0x33]), (uint)(table[o + 0x34] << 24 | table[o + 0x35] << 16 | table[o + 0x36] << 8 | table[o + 0x37])));
        }
    }

    public static StfsPackage Open(string path) => new(File.ReadAllBytes(path));

    public byte[] Extract(Entry e) => e.IsDirectory ? Array.Empty<byte>() : ReadBlocks(e.StartBlock, e.Blocks, e.Consecutive, (int)e.Size);

    /// <summary>Data block number -> file offset (hash tables sit before every 0xAA data blocks, and before every 0x70E4).</summary>
    long BlockOffset(int block)
    {
        long n = (((long)block + 0xAA) / 0xAA << _sex) + block;
        if (block >= 0x70E4) n += ((long)block + 0x70E4) / 0x70E4 << _sex;
        if (block >= 0x4AF768) n += 1 << _sex;
        return (n << 12) + _base;
    }

    /// <summary>The level-0 hash entry of a block (holds the next block of a chain). Small packages (one level) only need
    /// the first table; the active copy of a two-copy package is chosen by block separation bit 1.</summary>
    long HashEntryOffset(int block)
    {
        long n = 0;
        if (block >= 0xAA)
        {
            n = block / 0xAA * (_sex == 0 ? 0xABL : 0xACL);
            n += (block / 0x70E4 + 1L) << _sex;
            if (block / 0x70E4 != 0) n += 1 << _sex;
        }
        return (n << 12) + _base + block % 0xAA * 0x18 + ((_separation & 2) << 0xB);
    }

    byte[] ReadBlocks(int start, int count, bool consecutive, int size)
    {
        var res = new byte[Math.Max(0, size)];
        int b = start, done = 0;
        for (int i = 0; i < count && done < res.Length; i++)
        {
            long off = BlockOffset(b);
            if (off < 0 || off >= _d.Length) throw new InvalidDataException($"block {b} is outside the package");
            int n = (int)Math.Min(Math.Min(0x1000, res.Length - done), _d.Length - off);
            Array.Copy(_d, off, res, done, n);
            done += n;
            if (consecutive) b++;
            else
            {
                long h = HashEntryOffset(b);
                b = h + 0x18 <= _d.Length ? _d[h + 0x15] << 16 | _d[h + 0x16] << 8 | _d[h + 0x17] : b + 1;
            }
        }
        return res;
    }

    /// <summary>
    /// A copy of this package with new contents for some files (by name) and optionally a new thumbnail (PNG, at most
    /// 0x4000 bytes). The data area is laid out again (file table, then every file in consecutive blocks), then the
    /// integrity data is recomputed: each block's SHA-1 and next-block link in the hash table, the top hash (volume
    /// descriptor +8 = SHA-1 of the active hash table), the block counts and the header hash (0x32C = SHA-1 of
    /// 0x344 .. the first hash table). Single-level packages only (up to 0xAA blocks = 680 KB of data), which covers
    /// saves and photos. The console signature (0x1AC) cannot be recomputed without the console's private key: Xenia
    /// does not check it; a real console needs the package resigned (Horizon / Velocity "Rehash and Resign").
    /// </summary>
    public byte[] WithFiles(IReadOnlyDictionary<string, byte[]> replace, byte[]? thumbnailPng = null, ulong? profileId = null)
    {
        var contents = Files.Select(f => f.IsDirectory ? Array.Empty<byte>()
            : replace.TryGetValue(f.Name, out var nd) ? nd : Extract(f)).ToList();
        int ftBlocks = Math.Max(1, (Files.Count * 0x40 + 0xFFF) / 0x1000);
        var starts = new int[Files.Count]; var counts = new int[Files.Count];
        int next = ftBlocks;
        for (int i = 0; i < Files.Count; i++)
        {
            counts[i] = Files[i].IsDirectory ? 0 : (contents[i].Length + 0xFFF) / 0x1000;
            starts[i] = counts[i] > 0 ? next : 0;
            next += counts[i];
        }
        int total = next;
        if (total > 0xAA) throw new InvalidDataException($"the new contents need {total} blocks ({total * 4} KB); this writer handles up to 170 blocks (680 KB)");

        int tables = 1 << _sex;
        var o = new byte[_base + tables * 0x1000 + total * 0x1000];
        Array.Copy(_d, o, _base);
        // data: file table, then the files
        var table = new byte[ftBlocks * 0x1000];
        int oldFtCount = _d[0x379 + 3] | _d[0x379 + 4] << 8;
        var oldTable = ReadBlocks(Int24Le(0x379 + 5), oldFtCount, false, oldFtCount * 0x1000);
        for (int i = 0; i < Files.Count; i++)
        {
            int at = i * 0x40;
            var e = Files[i];
            Array.Copy(oldTable, at, table, at, 0x40);                       // name, timestamps, parent as they were
            int flags = table[at + 0x28] & 0xBF;
            if (!e.IsDirectory) flags |= 0x40;                               // consecutive blocks
            table[at + 0x28] = (byte)flags;
            foreach (int k in new[] { 0x29, 0x2C }) { table[at + k] = (byte)counts[i]; table[at + k + 1] = (byte)(counts[i] >> 8); table[at + k + 2] = (byte)(counts[i] >> 16); }
            table[at + 0x2F] = (byte)starts[i]; table[at + 0x30] = (byte)(starts[i] >> 8); table[at + 0x31] = (byte)(starts[i] >> 16);
            uint size = (uint)contents[i].Length;
            table[at + 0x34] = (byte)(size >> 24); table[at + 0x35] = (byte)(size >> 16); table[at + 0x36] = (byte)(size >> 8); table[at + 0x37] = (byte)size;
        }
        long Data(int b) => _base + ((long)tables + b) * 0x1000;
        Array.Copy(table, 0, o, Data(0), table.Length);
        for (int i = 0; i < Files.Count; i++) if (counts[i] > 0) Array.Copy(contents[i], 0, o, Data(starts[i]), contents[i].Length);

        // level-0 hash table: SHA-1 of every block, status 0xC0 (in use), next block of its chain (0xFFFFFF = last)
        var ht = new byte[0x1000];
        var lastOfChain = new HashSet<int> { ftBlocks - 1 };
        for (int i = 0; i < Files.Count; i++) if (counts[i] > 0) lastOfChain.Add(starts[i] + counts[i] - 1);
        for (int b = 0; b < total; b++)
        {
            var h = System.Security.Cryptography.SHA1.HashData(o.AsSpan((int)Data(b), 0x1000));
            h.CopyTo(ht, b * 0x18);
            ht[b * 0x18 + 0x14] = 0xC0;
            int nb = lastOfChain.Contains(b) ? 0xFFFFFF : b + 1;
            ht[b * 0x18 + 0x15] = (byte)(nb >> 16); ht[b * 0x18 + 0x16] = (byte)(nb >> 8); ht[b * 0x18 + 0x17] = (byte)nb;
        }
        for (int t = 0; t < tables; t++) Array.Copy(ht, 0, o, _base + t * 0x1000, 0x1000);   // both copies alike

        // volume descriptor: file table at block 0, top hash, allocated / free block counts
        const int vd = 0x379;
        o[vd + 3] = (byte)ftBlocks; o[vd + 4] = (byte)(ftBlocks >> 8);
        o[vd + 5] = o[vd + 6] = o[vd + 7] = 0;
        System.Security.Cryptography.SHA1.HashData(ht).CopyTo(o, vd + 8);
        o[vd + 0x1C] = (byte)(total >> 24); o[vd + 0x1D] = (byte)(total >> 16); o[vd + 0x1E] = (byte)(total >> 8); o[vd + 0x1F] = (byte)total;
        o[vd + 0x20] = o[vd + 0x21] = o[vd + 0x22] = o[vd + 0x23] = 0;
        if (thumbnailPng != null)
        {
            if (thumbnailPng.Length > 0x4000) throw new InvalidDataException("thumbnail larger than 16 KB");
            Array.Clear(o, 0x171A, 0x4000);
            thumbnailPng.CopyTo(o, 0x171A);
            o[0x1712] = (byte)(thumbnailPng.Length >> 24); o[0x1713] = (byte)(thumbnailPng.Length >> 16); o[0x1714] = (byte)(thumbnailPng.Length >> 8); o[0x1715] = (byte)thumbnailPng.Length;
        }
        if (profileId is ulong pid) System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(o.AsSpan(0x371), pid);
        // header hash over the metadata (0x344 .. first hash table)
        System.Security.Cryptography.SHA1.HashData(o.AsSpan(0x344, _base - 0x344)).CopyTo(o, 0x32C);
        return o;
    }

    uint U32(int o) => (uint)(_d[o] << 24 | _d[o + 1] << 16 | _d[o + 2] << 8 | _d[o + 3]);
    int Int24Le(int o) => Int24Le(_d, o);
    static int Int24Le(byte[] d, int o) => d[o] | d[o + 1] << 8 | d[o + 2] << 16;
    string Utf16(int o, int bytes) => Encoding.BigEndianUnicode.GetString(_d, o, bytes).Split('\0')[0].Trim();
}

/// <summary>
/// Photos Banjo-Kazooie: Nuts &amp; Bolts saves on the console (Take Photo): an STFS package (title 4D5307ED, display name
/// "date time - PHOTO") holding one file = a 32-byte game header + a 1280x720 JPEG. Also accepts the extracted game file
/// or a plain JPEG.
/// </summary>
public static class NbPhoto
{
    public const uint TitleId = 0x4D5307ED;

    public sealed record Photo(string Name, byte[] Jpeg, StfsPackage? Package, string FileInside)
    {
        /// <summary>The game's 32-byte photo header when the file is a photo's content extracted from a package (no package around it).</summary>
        public byte[]? ContentHeader { get; init; }
        /// <summary>What the file is: a 360 package, a content file extracted from one, or a plain picture.</summary>
        public string Kind => Package != null ? $"{Package.Magic} package" : ContentHeader != null ? "content file (extracted from a package)" : "picture file";
    }

    /// <summary>
    /// A photo's content as the game writes it inside its package (the "content" file a package extractor gives): a
    /// 32-byte game header (f32, f32, u32 1, u32, u64 owner XUID at +0x10, u32, u32) and the JPEG at +0x20.
    /// </summary>
    public static bool IsPhotoContent(ReadOnlySpan<byte> d) =>
        d.Length > 0x24 && d[0x20] == 0xFF && d[0x21] == 0xD8 && d[0x22] == 0xFF && System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(d[8..]) == 1;

    /// <summary>Every photo in <paramref name="path"/> (usually one).</summary>
    public static List<Photo> Read(string path)
    {
        byte[] data;   // shared read: the file may be open in another program (Explorer preview, an extractor)
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { data = new byte[fs.Length]; fs.ReadExactly(data); }
        var res = new List<Photo>();
        string fallback = Path.GetFileNameWithoutExtension(path);
        if (StfsPackage.IsStfs(data))
        {
            var pkg = new StfsPackage(data);
            foreach (var f in pkg.Files.Where(f => !f.IsDirectory))
                if (FindJpeg(pkg.Extract(f)) is { } jpg)
                    res.Add(new Photo(pkg.DisplayName.Length > 0 ? pkg.DisplayName : fallback, jpg, pkg, f.Name));
            if (res.Count == 0) throw new InvalidDataException($"the package \"{pkg.DisplayName}\" holds no photo ({pkg.Files.Count} file(s)){(pkg.TitleId == TitleId ? " - probably a vehicle save (blueprint): open it in the Vehicle Editor" : "")}");
        }
        else if (IsPhotoContent(data) && FindJpeg(data) is { } cj) res.Add(new Photo(fallback, cj, null, "") { ContentHeader = data[..0x20] });
        else if (FindJpeg(data) is { } jpg) res.Add(new Photo(fallback, jpg, null, ""));
        else throw new InvalidDataException("not an Xbox 360 package and no JPEG inside");
        return res;
    }

    /// <summary>The profile (XUID) that owns a photo: game header +0x10 (the album lists only the playing profile's photos).</summary>
    public static ulong Owner(Photo photo)
    {
        var pkg = photo.Package;
        if (pkg == null) return photo.ContentHeader is { } h ? System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(h.AsSpan(0x10)) : 0;
        var d = pkg.Extract(pkg.Files.First(f => f.Name == photo.FileInside));
        return d.Length >= 0x18 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(0x10)) : pkg.ProfileId;
    }

    /// <summary>
    /// A new package with <paramref name="jpeg"/> as the photo in <paramref name="photo"/>'s file: the game's 32-byte
    /// header in front of the JPEG is kept (game header layout: f32, f32, u32 1, u32, u64 owner XUID at +0x10, u32, u32),
    /// the package thumbnail is replaced (PNG, the console's is 90x50). <paramref name="owner"/> (optional) gives the
    /// photo to another profile: the game's photo album only lists photos whose owner is the profile playing.
    /// </summary>
    public static byte[] Import(Photo photo, byte[] jpeg, byte[]? thumbnailPng, ulong? owner = null)
    {
        var pkg = photo.Package ?? throw new InvalidOperationException("this photo is not in an Xbox 360 package");
        var entry = pkg.Files.First(f => f.Name == photo.FileInside);
        var old = pkg.Extract(entry);
        int prefix = 0;
        for (int i = 0; i + 2 < old.Length && i < 0x10000; i++) if (old[i] == 0xFF && old[i + 1] == 0xD8 && old[i + 2] == 0xFF) { prefix = i; break; }
        var data = new byte[prefix + jpeg.Length];
        Array.Copy(old, data, prefix);
        jpeg.CopyTo(data, prefix);
        if (owner is ulong x && prefix >= 0x18) System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(0x10), x);
        return pkg.WithFiles(new Dictionary<string, byte[]> { [entry.Name] = data }, thumbnailPng, owner);
    }

    public const int Width = 1280, Height = 720;

    /// <summary>
    /// Any picture (PNG, JPEG, BMP, GIF, TIFF) -> what the game stores: a 1280x720 baseline JPEG (re-encoded, quality
    /// lowered if needed to fit the package) and the package's 90x50 PNG thumbnail. Other aspect ratios are cropped to
    /// 16:9 around the centre (<paramref name="crop"/>) or fitted with black bars. A 1280x720 baseline JPEG is kept
    /// byte for byte.
    /// </summary>
    public static (byte[] Jpeg, byte[] Thumbnail) PrepareImage(byte[] file, bool crop = true)
    {
        using var src = System.Drawing.Image.FromStream(new MemoryStream(file));
        byte[] jpeg;
        bool baseline = file.Length > 3 && file[0] == 0xFF && file[1] == 0xD8 && HasMarker(file, 0xC0) && !HasMarker(file, 0xC2);
        if (src.Width == Width && src.Height == Height && baseline) jpeg = file;
        else
        {
            using var bmp = new System.Drawing.Bitmap(Width, Height);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.Clear(System.Drawing.Color.Black);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                float sx = (float)Width / src.Width, sy = (float)Height / src.Height;
                float s = crop ? Math.Max(sx, sy) : Math.Min(sx, sy);
                float w = src.Width * s, h = src.Height * s;
                g.DrawImage(src, (Width - w) / 2, (Height - h) / 2, w, h);
            }
            jpeg = Array.Empty<byte>();
            foreach (long q in new long[] { 92, 85, 75, 65, 50 })
            {
                jpeg = EncodeJpeg(bmp, q);
                if (jpeg.Length <= MaxJpeg) break;
            }
        }
        if (jpeg.Length > MaxJpeg) throw new InvalidDataException($"the JPEG is {jpeg.Length / 1024} KB; a photo package holds at most {MaxJpeg / 1024} KB");
        using var full = System.Drawing.Image.FromStream(new MemoryStream(jpeg));
        using var thumb = new System.Drawing.Bitmap(90, 50);
        using (var g = System.Drawing.Graphics.FromImage(thumb))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(full, 0, 0, 90, 50);
        }
        var ms = new MemoryStream();
        thumb.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return (jpeg, ms.ToArray());
    }

    /// <summary>Largest JPEG a single-level photo package can hold: 170 blocks minus the file table, minus the game header.</summary>
    public const int MaxJpeg = 169 * 0x1000 - 0x20;

    static byte[] EncodeJpeg(System.Drawing.Bitmap bmp, long quality)
    {
        var codec = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
        using var p = new System.Drawing.Imaging.EncoderParameters(1);
        p.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        var ms = new MemoryStream();
        bmp.Save(ms, codec, p);
        return ms.ToArray();
    }

    /// <summary>Does the JPEG have a segment with this marker (0xC0 baseline, 0xC2 progressive) before its scan?</summary>
    static bool HasMarker(byte[] d, byte marker)
    {
        int i = 2;
        while (i + 4 <= d.Length && d[i] == 0xFF)
        {
            byte m = d[i + 1];
            if (m == marker) return true;
            if (m == 0xDA) return false;
            i += 2 + (d[i + 2] << 8 | d[i + 3]);
        }
        return false;
    }

    /// <summary>The JPEG in a game photo file: from its SOI marker (after the 32-byte header) to the end.</summary>
    public static byte[]? FindJpeg(byte[] d)
    {
        for (int i = 0; i + 3 < d.Length && i < 0x10000; i++)
            if (d[i] == 0xFF && d[i + 1] == 0xD8 && d[i + 2] == 0xFF)
            {
                int end = d.Length;
                while (end > i + 2 && !(d[end - 2] == 0xFF && d[end - 1] == 0xD9)) end--;   // trailing padding
                return d.AsSpan(i, end > i + 2 ? end - i : d.Length - i).ToArray();
            }
        return null;
    }
}
