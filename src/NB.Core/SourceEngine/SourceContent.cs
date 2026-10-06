using System.Text;

namespace NB.Core.SourceEngine;

/// <summary>
/// Read-only access to a Source game's content: loose folders (e.g. garrysmod\, garrysmod\addons\*\) and VPK archives
/// (version 1 and 2 directory files, "*_dir.vpk", with their numbered data archives). Paths are relative to the game
/// root ("materials/brick/brickwall001a.vmt"), forward slashes, case-insensitive. The first source that has a file wins.
/// Nothing is ever written.
/// </summary>
public sealed class SourceContent : IDisposable
{
    readonly List<string> _folders = new();
    readonly List<Vpk> _vpks = new();
    public IReadOnlyList<string> Folders => _folders;
    public IEnumerable<string> Archives => _vpks.Select(v => v.DirPath);

    /// <summary>Adds a loose content root (a folder holding materials\, models\ ...).</summary>
    public void AddFolder(string dir) { if (Directory.Exists(dir)) _folders.Add(dir); }

    /// <summary>Adds a VPK directory file (name_dir.vpk).</summary>
    public void AddVpk(string dirVpk) { if (File.Exists(dirVpk)) _vpks.Add(new Vpk(dirVpk)); }

    /// <summary>
    /// Content of a Garry's Mod install (or a folder inside one): garrysmod loose files, extracted addons, garrysmod_dir.vpk,
    /// fallbacks, then the mounted Source games in sourceengine\ (cstrike, hl2) and any other *_dir.vpk there.
    /// </summary>
    public static SourceContent ForGarrysMod(string gmodRoot)
    {
        var c = new SourceContent();
        var gm = Path.Combine(gmodRoot, "garrysmod");
        if (!Directory.Exists(gm) && Path.GetFileName(gmodRoot).Equals("garrysmod", StringComparison.OrdinalIgnoreCase)) { gm = gmodRoot; gmodRoot = Path.GetDirectoryName(gmodRoot)!; }
        c.AddFolder(gm);
        var addons = Path.Combine(gm, "addons");
        if (Directory.Exists(addons))
            foreach (var d in Directory.GetDirectories(addons).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) c.AddFolder(d);
        c.AddVpk(Path.Combine(gm, "garrysmod_dir.vpk"));
        c.AddVpk(Path.Combine(gm, "fallbacks_dir.vpk"));
        var se = Path.Combine(gmodRoot, "sourceengine");
        if (Directory.Exists(se))
        {
            // texture archives before the rest (hl2_textures holds most world materials)
            foreach (var f in Directory.GetFiles(se, "*_dir.vpk").OrderBy(f => f.Contains("sound") ? 2 : f.Contains("textures") || f.Contains("content") ? 0 : 1).ThenBy(f => f))
                if (!Path.GetFileName(f).Contains("sound")) c.AddVpk(f);
        }
        return c;
    }

    /// <summary>A generic Source game folder (e.g. ...\Half-Life 2\hl2): the folder, its custom\*, and its *_dir.vpk files.</summary>
    public static SourceContent ForGameFolder(string dir)
    {
        if (File.Exists(Path.Combine(dir, "gameinfo.txt")) == false && Directory.Exists(Path.Combine(dir, "garrysmod"))) return ForGarrysMod(dir);
        if (Directory.Exists(Path.Combine(dir, "sourceengine")) || Path.GetFileName(dir).Equals("garrysmod", StringComparison.OrdinalIgnoreCase)) return ForGarrysMod(dir);
        var c = new SourceContent();
        c.AddFolder(dir);
        var custom = Path.Combine(dir, "custom");
        if (Directory.Exists(custom)) foreach (var d in Directory.GetDirectories(custom)) c.AddFolder(d);
        foreach (var f in Directory.GetFiles(dir, "*_dir.vpk").Where(f => !f.Contains("sound"))) c.AddVpk(f);
        return c;
    }

    public bool Exists(string path) => Find(path) != null;

    /// <summary>Where a file comes from ("folder:..." / "vpk:...") or null.</summary>
    public string? Find(string path)
    {
        path = Norm(path);
        foreach (var f in _folders) { var p = Path.Combine(f, path.Replace('/', Path.DirectorySeparatorChar)); if (File.Exists(p)) return p; }
        foreach (var v in _vpks) if (v.Has(path)) return v.DirPath + "|" + path;
        return null;
    }

    public byte[]? Read(string path)
    {
        path = Norm(path);
        foreach (var f in _folders)
        {
            var p = Path.Combine(f, path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(p)) { try { return File.ReadAllBytes(p); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
        foreach (var v in _vpks) { var b = v.Read(path); if (b != null) return b; }
        return null;
    }

    public static string Norm(string p) => p.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

    public void Dispose() { foreach (var v in _vpks) v.Dispose(); }

    /// <summary>One VPK directory file and its archives.</summary>
    sealed class Vpk : IDisposable
    {
        public readonly string DirPath;
        readonly Dictionary<string, Entry> _entries = new();
        readonly Dictionary<int, FileStream> _archives = new();
        long _dataStart;
        record struct Entry(ushort Archive, uint Offset, uint Length, long PreloadOffset, ushort PreloadLength);

        public Vpk(string dirPath)
        {
            DirPath = dirPath;
            using var fs = File.OpenRead(dirPath);
            using var r = new BinaryReader(fs);
            uint sig = r.ReadUInt32();
            if (sig != 0x55AA1234) throw new InvalidDataException($"{dirPath}: not a VPK directory");
            uint ver = r.ReadUInt32(); uint tree = r.ReadUInt32();
            if (ver == 2) { r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32(); }
            long treeStart = fs.Position;
            _dataStart = treeStart + tree;
            while (true)
            {
                string ext = Str(r); if (ext.Length == 0) break;
                while (true)
                {
                    string dir = Str(r); if (dir.Length == 0) break;
                    while (true)
                    {
                        string name = Str(r); if (name.Length == 0) break;
                        r.ReadUInt32();   // crc
                        ushort pre = r.ReadUInt16(); ushort arch = r.ReadUInt16(); uint off = r.ReadUInt32(); uint len = r.ReadUInt32();
                        r.ReadUInt16();   // 0xFFFF terminator
                        long preOff = fs.Position;
                        fs.Seek(pre, SeekOrigin.Current);
                        string full = (dir == " " ? "" : dir + "/") + name + (ext == " " ? "" : "." + ext);
                        _entries[full.ToLowerInvariant()] = new Entry(arch, off, len, preOff, pre);
                    }
                }
            }
        }

        static string Str(BinaryReader r)
        {
            var sb = new List<byte>();
            byte b;
            while ((b = r.ReadByte()) != 0) sb.Add(b);
            return Encoding.UTF8.GetString(sb.ToArray());
        }

        public bool Has(string path) => _entries.ContainsKey(path);

        public byte[]? Read(string path)
        {
            if (!_entries.TryGetValue(path, out var e)) return null;
            var res = new byte[e.PreloadLength + e.Length];
            lock (_archives)
            {
                if (e.PreloadLength > 0)
                {
                    using var fs = File.OpenRead(DirPath);
                    fs.Seek(e.PreloadOffset, SeekOrigin.Begin); fs.ReadExactly(res, 0, e.PreloadLength);
                }
                if (e.Length > 0)
                {
                    FileStream a;
                    long off = e.Offset;
                    if (e.Archive == 0x7FFF)
                    {
                        if (!_archives.TryGetValue(-1, out a!)) _archives[-1] = a = File.OpenRead(DirPath);
                        off += _dataStart;
                    }
                    else if (!_archives.TryGetValue(e.Archive, out a!))
                    {
                        var p = DirPath[..^"dir.vpk".Length] + e.Archive.ToString("D3") + ".vpk";
                        if (!File.Exists(p)) return null;
                        _archives[e.Archive] = a = File.OpenRead(p);
                    }
                    a.Seek(off, SeekOrigin.Begin); a.ReadExactly(res, e.PreloadLength, (int)e.Length);
                }
            }
            return res;
        }

        public void Dispose() { foreach (var a in _archives.Values) a.Dispose(); _archives.Clear(); }
    }
}

/// <summary>
/// Valve Texture Format reader (7.0 - 7.5): the image of one mip level, decoded to RGBA8. Formats: RGBA8888, ABGR8888,
/// RGB888, BGR888, RGB565, BGR565, I8, IA88, A8, ARGB8888, BGRA8888, BGRX8888, DXT1 (+one-bit alpha), DXT3, DXT5,
/// BGRA4444, BGRX5551, BGRA5551, RGBA16161616F (tone-mapped), UV88.
/// </summary>
public static class Vtf
{
    public sealed record Info(int Width, int Height, int Format, int Mips, int Frames, int Faces, int Depth, uint Flags);

    public static Info ReadInfo(byte[] d)
    {
        if (d.Length < 64 || d[0] != 'V' || d[1] != 'T' || d[2] != 'F' || d[3] != 0) throw new InvalidDataException("not a VTF file");
        int minor = BitConverter.ToInt32(d, 8);
        int w = BitConverter.ToUInt16(d, 16), h = BitConverter.ToUInt16(d, 18);
        uint flags = BitConverter.ToUInt32(d, 20);
        int frames = BitConverter.ToUInt16(d, 24);
        int fmt = BitConverter.ToInt32(d, 52);
        int mips = d[56];
        int depth = minor >= 2 ? Math.Max(1, (int)BitConverter.ToUInt16(d, 63)) : 1;
        int faces = (flags & 0x4000) != 0 ? (minor >= 1 && minor <= 4 ? 7 : 6) : 1;   // envmap: 6 (+ sphere map before 7.5)
        return new Info(w, h, fmt, Math.Max(1, mips), Math.Max(1, frames), faces, depth, flags);
    }

    /// <summary>Decodes mip level <paramref name="mip"/> (0 = full size) of frame 0, face 0.</summary>
    public static (byte[] Rgba, int W, int H) Decode(byte[] d, int mip = 0)
    {
        var info = ReadInfo(d);
        int minor = BitConverter.ToInt32(d, 8);
        int headerSize = BitConverter.ToInt32(d, 12);
        int lowFmt = BitConverter.ToInt32(d, 57);
        int lowW = d[61], lowH = d[62];
        long hiStart;
        if (minor >= 3)
        {
            int nres = BitConverter.ToInt32(d, 68);
            hiStart = -1;
            for (int i = 0; i < nres; i++)
            {
                int o = 80 + 8 * i;
                uint tag = (uint)(d[o] | d[o + 1] << 8 | d[o + 2] << 16);
                if (tag == 0x30) hiStart = BitConverter.ToUInt32(d, o + 4);
            }
            if (hiStart < 0) throw new InvalidDataException("VTF without image data");
        }
        else hiStart = headerSize + (lowFmt >= 0 && lowW > 0 && lowH > 0 ? SizeOf(lowFmt, lowW, lowH) : 0);
        mip = Math.Clamp(mip, 0, info.Mips - 1);
        // mips are stored smallest first; each mip holds frames x faces x slices
        long off = hiStart;
        for (int m = info.Mips - 1; m > mip; m--)
        {
            int mw = Math.Max(1, info.Width >> m), mh = Math.Max(1, info.Height >> m), md = Math.Max(1, info.Depth >> m);
            off += (long)SizeOf(info.Format, mw, mh) * info.Frames * info.Faces * md;
        }
        int W = Math.Max(1, info.Width >> mip), H = Math.Max(1, info.Height >> mip);
        int size = SizeOf(info.Format, W, H);
        if (off + size > d.Length) throw new InvalidDataException("VTF truncated");
        return (DecodeImage(d, (int)off, info.Format, W, H), W, H);
    }

    public static int SizeOf(int fmt, int w, int h) => fmt switch
    {
        13 or 20 => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 8,
        14 or 15 => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 16,
        0 or 1 or 11 or 12 or 16 or 23 or 26 => w * h * 4,
        2 or 3 or 9 or 10 => w * h * 3,
        4 or 6 or 17 or 18 or 19 or 21 or 22 => w * h * 2,
        5 or 7 or 8 => w * h,
        24 or 25 => w * h * 8,
        _ => throw new NotSupportedException($"VTF image format {fmt}"),
    };

    static byte[] DecodeImage(byte[] d, int o, int fmt, int w, int h)
    {
        var px = new byte[w * h * 4];
        void Put(int i, int r, int g, int b, int a) { px[4 * i] = (byte)r; px[4 * i + 1] = (byte)g; px[4 * i + 2] = (byte)b; px[4 * i + 3] = (byte)a; }
        int n = w * h;
        switch (fmt)
        {
            case 0: for (int i = 0; i < n; i++) Put(i, d[o + 4 * i], d[o + 4 * i + 1], d[o + 4 * i + 2], d[o + 4 * i + 3]); break;
            case 1: for (int i = 0; i < n; i++) Put(i, d[o + 4 * i + 3], d[o + 4 * i + 2], d[o + 4 * i + 1], d[o + 4 * i]); break;
            case 2: case 9: for (int i = 0; i < n; i++) Put(i, d[o + 3 * i], d[o + 3 * i + 1], d[o + 3 * i + 2], 255); break;
            case 3: case 10: for (int i = 0; i < n; i++) Put(i, d[o + 3 * i + 2], d[o + 3 * i + 1], d[o + 3 * i], 255); break;
            case 4: case 17:
                for (int i = 0; i < n; i++)
                {
                    int v = d[o + 2 * i] | d[o + 2 * i + 1] << 8;
                    int a = (v >> 11) & 31, b = (v >> 5) & 63, c = v & 31;
                    if (fmt == 4) Put(i, c * 255 / 31, b * 255 / 63, a * 255 / 31, 255); else Put(i, a * 255 / 31, b * 255 / 63, c * 255 / 31, 255);
                }
                break;
            case 5: for (int i = 0; i < n; i++) Put(i, d[o + i], d[o + i], d[o + i], 255); break;
            case 6: for (int i = 0; i < n; i++) Put(i, d[o + 2 * i], d[o + 2 * i], d[o + 2 * i], d[o + 2 * i + 1]); break;
            case 8: for (int i = 0; i < n; i++) Put(i, 255, 255, 255, d[o + i]); break;
            case 11: for (int i = 0; i < n; i++) Put(i, d[o + 4 * i + 1], d[o + 4 * i + 2], d[o + 4 * i + 3], d[o + 4 * i]); break;
            case 12: for (int i = 0; i < n; i++) Put(i, d[o + 4 * i + 2], d[o + 4 * i + 1], d[o + 4 * i], d[o + 4 * i + 3]); break;
            case 16: for (int i = 0; i < n; i++) Put(i, d[o + 4 * i + 2], d[o + 4 * i + 1], d[o + 4 * i], 255); break;
            case 19:
                for (int i = 0; i < n; i++) { int v = d[o + 2 * i] | d[o + 2 * i + 1] << 8; Put(i, ((v >> 8) & 15) * 17, ((v >> 4) & 15) * 17, (v & 15) * 17, ((v >> 12) & 15) * 17); }
                break;
            case 18: case 21:
                for (int i = 0; i < n; i++) { int v = d[o + 2 * i] | d[o + 2 * i + 1] << 8; Put(i, ((v >> 10) & 31) * 255 / 31, ((v >> 5) & 31) * 255 / 31, (v & 31) * 255 / 31, fmt == 21 ? ((v >> 15) & 1) * 255 : 255); }
                break;
            case 22: for (int i = 0; i < n; i++) Put(i, d[o + 2 * i], d[o + 2 * i + 1], 255, 255); break;
            case 24:
                for (int i = 0; i < n; i++)
                {
                    float F(int k) => (float)BitConverter.UInt16BitsToHalf((ushort)(d[o + 8 * i + 2 * k] | d[o + 8 * i + 2 * k + 1] << 8));
                    int T(float x) => (int)Math.Clamp(Math.Pow(Math.Max(0, x) / (1 + Math.Max(0, x)) * 1.6, 1 / 2.2) * 255, 0, 255);
                    Put(i, T(F(0)), T(F(1)), T(F(2)), (int)Math.Clamp(F(3) * 255, 0, 255));
                }
                break;
            case 25:
                for (int i = 0; i < n; i++) Put(i, d[o + 8 * i + 1], d[o + 8 * i + 3], d[o + 8 * i + 5], d[o + 8 * i + 7]);
                break;
            case 13: case 14: case 15: case 20: DecodeDxt(d, o, fmt, w, h, px); break;
            default: throw new NotSupportedException($"VTF image format {fmt}");
        }
        return px;
    }

    static void DecodeDxt(byte[] d, int o, int fmt, int w, int h, byte[] px)
    {
        int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4), bs = fmt is 13 or 20 ? 8 : 16;
        var col = new int[4 * 4];
        var alpha = new int[16];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int b = o + (by * bw + bx) * bs;
                int cb = bs == 16 ? b + 8 : b;
                for (int k = 0; k < 16; k++) alpha[k] = 255;
                if (fmt == 14) for (int k = 0; k < 16; k++) alpha[k] = ((d[b + k / 2] >> (4 * (k & 1))) & 15) * 17;
                if (fmt == 15)
                {
                    int a0 = d[b], a1 = d[b + 1];
                    ulong bits = 0; for (int k = 0; k < 6; k++) bits |= (ulong)d[b + 2 + k] << (8 * k);
                    var pal = new int[8]; pal[0] = a0; pal[1] = a1;
                    if (a0 > a1) for (int k = 1; k < 7; k++) pal[k + 1] = ((7 - k) * a0 + k * a1) / 7;
                    else { for (int k = 1; k < 5; k++) pal[k + 1] = ((5 - k) * a0 + k * a1) / 5; pal[6] = 0; pal[7] = 255; }
                    for (int k = 0; k < 16; k++) alpha[k] = pal[(int)((bits >> (3 * k)) & 7)];
                }
                int c0 = d[cb] | d[cb + 1] << 8, c1 = d[cb + 2] | d[cb + 3] << 8;
                void Rgb(int c, int i) { col[4 * i] = ((c >> 11) & 31) * 255 / 31; col[4 * i + 1] = ((c >> 5) & 63) * 255 / 63; col[4 * i + 2] = (c & 31) * 255 / 31; col[4 * i + 3] = 255; }
                Rgb(c0, 0); Rgb(c1, 1);
                bool four = c0 > c1 || bs == 16;
                for (int k = 0; k < 3; k++)
                {
                    col[8 + k] = four ? (2 * col[k] + col[4 + k]) / 3 : (col[k] + col[4 + k]) / 2;
                    col[12 + k] = four ? (col[k] + 2 * col[4 + k]) / 3 : 0;
                }
                col[11] = 255; col[15] = four ? 255 : 0;
                uint idx = (uint)(d[cb + 4] | d[cb + 5] << 8 | d[cb + 6] << 16 | d[cb + 7] << 24);
                for (int k = 0; k < 16; k++)
                {
                    int x = bx * 4 + (k & 3), y = by * 4 + (k >> 2);
                    if (x >= w || y >= h) continue;
                    int c = (int)((idx >> (2 * k)) & 3);
                    int p = 4 * (y * w + x);
                    px[p] = (byte)col[4 * c]; px[p + 1] = (byte)col[4 * c + 1]; px[p + 2] = (byte)col[4 * c + 2];
                    px[p + 3] = (byte)(bs == 16 ? alpha[k] : col[4 * c + 3]);
                }
            }
    }
}

/// <summary>
/// A Source material (.vmt) resolved for import: its shader, base texture path and the flags that decide whether it is
/// drawn as an opaque surface (translucent, additive, alpha-tested, decal, nodraw, sky / water shaders).
/// "patch" materials are followed through their "include".
/// </summary>
public sealed class VmtInfo
{
    public string Shader = "";
    public string? BaseTexture, BaseTexture2;
    public bool Translucent, Additive, AlphaTest, Decal, NoDraw;
    public string? SurfaceProp;
    public readonly Dictionary<string, string> Keys = new(StringComparer.OrdinalIgnoreCase);

    public bool IsWater => Shader.Equals("water", StringComparison.OrdinalIgnoreCase) || Keys.ContainsKey("$bottommaterial");
    public bool IsSky => Shader.Contains("sky", StringComparison.OrdinalIgnoreCase);
    /// <summary>Drawn as an opaque surface by the import (the template material is opaque).</summary>
    public bool Opaque => !Translucent && !Additive && !Decal && !NoDraw && !IsSky && !Shader.Equals("sprite", StringComparison.OrdinalIgnoreCase);

    public static VmtInfo? Load(SourceContent c, string material, int depth = 0)
    {
        var bytes = c.Read("materials/" + SourceContent.Norm(material) + ".vmt");
        if (bytes == null || depth > 4) return null;
        KvNode root;
        try { root = KeyValues.Parse(Encoding.UTF8.GetString(bytes).Replace("\0", "")); } catch (Exception) { return null; }
        var shader = root.Children.FirstOrDefault();
        if (shader == null) return null;
        var v = new VmtInfo { Shader = shader.Name };
        if (shader.Name.Equals("patch", StringComparison.OrdinalIgnoreCase))
        {
            var inc = shader["include"];
            var baseInfo = inc != null ? Load(c, inc.Replace('\\', '/').TrimStart('/').Replace("materials/", "", StringComparison.OrdinalIgnoreCase).Replace(".vmt", "", StringComparison.OrdinalIgnoreCase), depth + 1) : null;
            v = baseInfo ?? new VmtInfo { Shader = "patch" };
            foreach (var blk in shader.Children) foreach (var kv in blk.Values) v.Keys[kv.Key] = kv.Value;
        }
        else
        {
            foreach (var kv in shader.Values) v.Keys[kv.Key] = kv.Value;
            // the shader fallback blocks ("LightmappedGeneric_DX9" ...) are ignored; the main block decides
        }
        string? K(string k) => v.Keys.TryGetValue(k, out var x) ? x : null;
        bool B(string k) => K(k) is string s && s.Trim() != "0" && s.Trim().Length > 0;
        v.BaseTexture = K("$basetexture") ?? v.BaseTexture;
        v.BaseTexture2 = K("$basetexture2") ?? v.BaseTexture2;
        v.Translucent = B("$translucent"); v.Additive = B("$additive"); v.AlphaTest = B("$alphatest");
        v.Decal = B("$decal"); v.NoDraw = B("%compilenodraw") || B("$no_draw"); v.SurfaceProp = K("$surfaceprop");
        if (B("%compilewater") || B("%compilesky") || B("%compile2dsky")) { if (B("%compilewater")) v.Keys["$bottommaterial"] = K("$bottommaterial") ?? ""; else v.Shader = "sky"; }
        return v;
    }
}
