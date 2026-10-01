using System.Security.Cryptography;
using System.Text;
using NB.Core.Compression;
using NB.Core.IO;

namespace NB.Core.Formats;

/// <summary>
/// XEX2 executable reader: optional headers, security info, AES-128 decryption (retail/devkit key) and
/// basic / LZX ("normal") decompression to the loaded PE image. Equivalent to "xextool -b".
/// </summary>
public sealed class XexFile
{
    static readonly byte[] RetailKey = Convert.FromHexString("20B185A59D28FDC340583FBB0896BF91");
    static readonly byte[] DevkitKey = new byte[16];

    public byte[] Raw = Array.Empty<byte>();
    public uint ModuleFlags, PeDataOffset, SecurityOffset;
    public Dictionary<uint, uint> OptionalHeaders = new();
    public uint ImageSize, LoadAddress, ImageFlags;
    public byte[] FileKey = new byte[16];
    public ushort EncryptionType, CompressionType;
    public uint EntryPoint, ImageBase, TitleId;
    public string OriginalName = "";
    public List<string> ImportLibraries = new();
    public string KeyUsed = "";

    public static XexFile Read(byte[] d)
    {
        if (BE.U32(d, 0) != 0x58455832) throw new InvalidDataException("Not an XEX2 file");
        var x = new XexFile { Raw = d, ModuleFlags = BE.U32(d, 4), PeDataOffset = BE.U32(d, 8), SecurityOffset = BE.U32(d, 16) };
        int n = BE.S32(d, 20);
        for (int i = 0; i < n; i++) x.OptionalHeaders[BE.U32(d, 24 + i * 8)] = BE.U32(d, 28 + i * 8);
        int so = (int)x.SecurityOffset;
        x.ImageSize = BE.U32(d, so + 4);
        x.ImageFlags = BE.U32(d, so + 0x10C);
        x.LoadAddress = BE.U32(d, so + 0x110);
        x.FileKey = d.AsSpan(so + 0x150, 16).ToArray();
        if (x.OptionalHeaders.TryGetValue(0x3FF, out var ffi))
        {
            x.EncryptionType = BE.U16(d, (int)ffi + 4);
            x.CompressionType = BE.U16(d, (int)ffi + 6);
        }
        x.OptionalHeaders.TryGetValue(0x10100, out x.EntryPoint);
        x.OptionalHeaders.TryGetValue(0x10201, out x.ImageBase);
        if (x.ImageBase == 0) x.ImageBase = x.LoadAddress;
        if (x.OptionalHeaders.TryGetValue(0x183FF, out var nameOff)) x.OriginalName = BE.CStr(d, (int)nameOff + 4);
        if (x.OptionalHeaders.TryGetValue(0x40006, out var exec)) x.TitleId = BE.U32(d, (int)exec + 12);
        if (x.OptionalHeaders.TryGetValue(0x103FF, out var imp))
        {
            int o = (int)imp;
            int strSize = BE.S32(d, o + 4), count = BE.S32(d, o + 8);
            int p = o + 12, end = p + strSize;
            while (p < end && x.ImportLibraries.Count < count)
            {
                var s = BE.CStr(d, p); if (s.Length > 0) x.ImportLibraries.Add(s);
                p += s.Length + 1; while (p < end && d[p] == 0) p++;
            }
        }
        return x;
    }

    byte[] SessionKey(byte[] root)
    {
        using var aes = Aes.Create();
        aes.Key = root;
        return aes.DecryptEcb(FileKey, PaddingMode.None);
    }

    byte[] DecryptPayload(byte[] key)
    {
        int len = Raw.Length - (int)PeDataOffset;
        len -= len % 16;
        var payload = Raw.AsSpan((int)PeDataOffset, len).ToArray();
        if (EncryptionType == 0) return Raw.AsSpan((int)PeDataOffset).ToArray();
        using var aes = Aes.Create();
        aes.Key = SessionKey(key);
        var dec = aes.DecryptCbc(payload, new byte[16], PaddingMode.None);
        var tail = Raw.AsSpan((int)PeDataOffset + len).ToArray();
        return tail.Length == 0 ? dec : dec.Concat(tail).ToArray();
    }

    /// <summary>Returns the decrypted, decompressed PE image as loaded at <see cref="ImageBase"/>.</summary>
    public byte[] GetImage()
    {
        foreach (var (name, key) in new[] { ("retail", RetailKey), ("devkit", DevkitKey) })
        {
            try
            {
                var img = Decode(DecryptPayload(key));
                if (img.Length >= 2 && img[0] == 'M' && img[1] == 'Z') { KeyUsed = EncryptionType == 0 ? "none" : name; return img; }
            }
            catch (InvalidDataException) { }
        }
        throw new InvalidDataException("XEX: could not decrypt/decompress (unknown key or format)");
    }

    byte[] Decode(byte[] data)
    {
        uint ffi = OptionalHeaders[0x3FF];
        int ffiSize = BE.S32(Raw, (int)ffi);
        switch (CompressionType)
        {
            case 0: return data.AsSpan(0, (int)Math.Min(ImageSize, (uint)data.Length)).ToArray();
            case 1:
            {
                // basic: (data size, zero size) pairs
                var outp = new byte[ImageSize];
                int blocks = (ffiSize - 8) / 8, src = 0, dst = 0;
                for (int i = 0; i < blocks; i++)
                {
                    int ds = BE.S32(Raw, (int)ffi + 8 + i * 8), zs = BE.S32(Raw, (int)ffi + 12 + i * 8);
                    Buffer.BlockCopy(data, src, outp, dst, Math.Min(ds, outp.Length - dst));
                    src += ds; dst += ds + zs;
                }
                return outp;
            }
            case 2:
            {
                int windowSize = BE.S32(Raw, (int)ffi + 8);
                int blockSize = BE.S32(Raw, (int)ffi + 12);
                var deblocked = new MemoryStream();
                int p = 0;
                while (blockSize != 0)
                {
                    int next = p + blockSize;
                    int nextSize = BE.S32(data, p);
                    p += 4 + 20;
                    while (true)
                    {
                        int chunk = data[p] << 8 | data[p + 1]; p += 2;
                        if (chunk == 0) break;
                        deblocked.Write(data, p, chunk); p += chunk;
                    }
                    p = next; blockSize = nextSize;
                }
                var comp = deblocked.ToArray();
                var outp = new byte[ImageSize];
                int bits = System.Numerics.BitOperations.Log2((uint)windowSize);
                new LzxDecoder(bits).DecodeStream(comp, 0, comp.Length, outp);
                return outp;
            }
            default: throw new NotSupportedException($"XEX compression type {CompressionType}");
        }
    }

    /// <summary>
    /// Writes a copy of this XEX with 32-bit words of the loaded image replaced (VA → value) — for consoles, where Xenia
    /// patch files do not apply. The file keeps its format: same encryption (the payload is re-encrypted with the file's
    /// own session key) and compression (basic or none). Every hash the loader can check is recomputed:
    ///   page descriptor chain: digest[i] = SHA1(image pages of block i+1 + descriptor i+1), the last digest is zero;
    ///   image digest (security info +0x114) = SHA1(pages of block 0 + descriptor 0);
    ///   header digest (+0x164) = SHA1(file[securityInfo+0x17C .. PE data] + file[0 .. securityInfo+8]).
    /// (All three verified against the retail default.xex.) Only the RSA signature over the security info becomes
    /// invalid, which RGH/JTAG consoles do not check. (B23: the first version wrote the payload decrypted, flipped the
    /// encryption flag and left every hash stale; Xenia ran it, a real RGH 360 refused to start it.)
    /// </summary>
    public byte[] WritePatched(IEnumerable<(uint Va, uint Value)> words)
    {
        if (CompressionType > 1) throw new NotSupportedException("only basic or uncompressed XEX files can be patched");
        byte[]? payload = null, rootKey = null;
        foreach (var key in new[] { RetailKey, DevkitKey })
        {
            var p = DecryptPayload(key);
            try { if (Decode(p) is var img && img.Length > 1 && img[0] == 'M' && img[1] == 'Z') { payload = p; rootKey = key; break; } } catch (InvalidDataException) { }
        }
        if (payload == null) throw new InvalidDataException("XEX: could not decrypt");
        uint ffi = OptionalHeaders[0x3FF];
        // image offset -> payload offset map
        var blocks = new List<(int ImgStart, int Len, int Src)>();
        if (CompressionType == 0) blocks.Add((0, payload.Length, 0));
        else
        {
            int ffiSize = BE.S32(Raw, (int)ffi), src = 0, dst = 0;
            for (int i = 0; i < (ffiSize - 8) / 8; i++)
            {
                int ds = BE.S32(Raw, (int)ffi + 8 + i * 8), zs = BE.S32(Raw, (int)ffi + 12 + i * 8);
                blocks.Add((dst, ds, src)); src += ds; dst += ds + zs;
            }
        }
        foreach (var (va, value) in words)
        {
            int o = (int)(va - ImageBase);
            var blk = blocks.FirstOrDefault(b => o >= b.ImgStart && o + 4 <= b.ImgStart + b.Len);
            if (blk.Len == 0) throw new InvalidDataException($"0x{va:X8} is not stored in the XEX payload");
            BE.W32(payload, blk.Src + (o - blk.ImgStart), value);
        }
        var outp = (byte[])Raw.Clone();
        // payload: re-encrypted with the file's own key when the original was encrypted (the unaligned tail stays plain)
        if (EncryptionType != 0)
        {
            int len = Raw.Length - (int)PeDataOffset; len -= len % 16;
            using var aes = Aes.Create();
            aes.Key = SessionKey(rootKey!);
            var enc = aes.EncryptCbc(payload.AsSpan(0, len), new byte[16], PaddingMode.None);
            Buffer.BlockCopy(enc, 0, outp, (int)PeDataOffset, len);
            Buffer.BlockCopy(payload, len, outp, (int)PeDataOffset + len, payload.Length - len);
        }
        else Buffer.BlockCopy(payload, 0, outp, (int)PeDataOffset, Math.Min(payload.Length, outp.Length - (int)PeDataOffset));
        RehashImage(outp, Decode(payload));
        return outp;
    }

    /// <summary>Recomputes the page-descriptor chain, the image digest and the header digest of <paramref name="x"/>
    /// for the decoded image <paramref name="img"/> (see <see cref="WritePatched"/>).</summary>
    void RehashImage(byte[] x, byte[] img)
    {
        int so = (int)SecurityOffset, cnt = BE.S32(x, so + 0x180), pe = (int)PeDataOffset;
        int pageSize = (ImageFlags & 0x10000000) != 0 ? 0x1000 : 0x10000;
        var start = new int[cnt + 1];
        for (int i = 0; i < cnt; i++) start[i + 1] = start[i] + (int)(BE.U32(x, so + 0x184 + 0x18 * i) >> 4) * pageSize;
        byte[] Hash(int block)
        {
            using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            int a = Math.Min(start[block], img.Length), b = Math.Min(start[block + 1], img.Length);
            h.AppendData(img, a, b - a);
            h.AppendData(x, so + 0x184 + 0x18 * block, 0x18);
            return h.GetHashAndReset();
        }
        Array.Clear(x, so + 0x184 + 0x18 * (cnt - 1) + 4, 20);
        for (int i = cnt - 2; i >= 0; i--) Hash(i + 1).CopyTo(x, so + 0x184 + 0x18 * i + 4);
        Hash(0).CopyTo(x, so + 0x114);
        using var hh = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hh.AppendData(x, so + 0x17C, pe - (so + 0x17C));
        hh.AppendData(x, 0, so + 8);
        hh.GetHashAndReset().CopyTo(x, so + 0x164);
    }

    /// <summary>Checks the page chain, image digest and header digest of an XEX file (true when all match).</summary>
    public static bool VerifyHashes(byte[] raw, out string detail)
    {
        var x = Read(raw);
        var copy = (byte[])raw.Clone();
        x.RehashImage(copy, x.GetImage());
        int so = (int)x.SecurityOffset;
        bool ok = copy.AsSpan(so + 0x114, 20).SequenceEqual(raw.AsSpan(so + 0x114, 20)) && copy.AsSpan(so + 0x164, 20).SequenceEqual(raw.AsSpan(so + 0x164, 20))
                  && copy.AsSpan(so + 0x184, 0x18 * BE.S32(raw, so + 0x180)).SequenceEqual(raw.AsSpan(so + 0x184, 0x18 * BE.S32(raw, so + 0x180)));
        detail = ok ? "page chain, image digest and header digest match" : "hashes do not match the image";
        return ok;
    }

    /// <summary>PE sections of the decoded image (name, virtual address, virtual size, raw offset).</summary>
    public static List<(string Name, uint Va, uint VSize, uint RawOff, uint RawSize, uint Flags)> Sections(byte[] img)
    {
        var list = new List<(string, uint, uint, uint, uint, uint)>();
        int pe = BitConverter.ToInt32(img, 0x3C);
        int nsec = BitConverter.ToUInt16(img, pe + 6), optSize = BitConverter.ToUInt16(img, pe + 20);
        int sec = pe + 24 + optSize;
        for (int i = 0; i < nsec; i++, sec += 40)
            list.Add((Encoding.ASCII.GetString(img, sec, 8).TrimEnd('\0'), BitConverter.ToUInt32(img, sec + 12), BitConverter.ToUInt32(img, sec + 8),
                      BitConverter.ToUInt32(img, sec + 20), BitConverter.ToUInt32(img, sec + 16), BitConverter.ToUInt32(img, sec + 36)));
        return list;
    }
}
