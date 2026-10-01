using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace NB.Core.Textures;

/// <summary>RGBA8 image load/save (PNG, BMP, JPG, GIF, TIFF) through GDI+.</summary>
public static class ImageIO
{
    public static void Save(string path, byte[] rgba, int w, int h, bool keepAlpha = true)
    {
        using var bmp = ToBitmap(rgba, w, h, keepAlpha);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var fmt = ext switch { ".bmp" => ImageFormat.Bmp, ".jpg" or ".jpeg" => ImageFormat.Jpeg, ".tif" or ".tiff" => ImageFormat.Tiff, _ => ImageFormat.Png };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        bmp.Save(path, fmt);
    }

    public static Bitmap ToBitmap(byte[] rgba, int w, int h, bool keepAlpha = true)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        var row = new byte[w * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int s = (y * w + x) * 4;
                row[x * 4] = rgba[s + 2]; row[x * 4 + 1] = rgba[s + 1]; row[x * 4 + 2] = rgba[s]; row[x * 4 + 3] = keepAlpha ? rgba[s + 3] : (byte)255;
            }
            Marshal.Copy(row, 0, bd.Scan0 + y * bd.Stride, w * 4);
        }
        bmp.UnlockBits(bd);
        return bmp;
    }

    public static (byte[] Rgba, int W, int H) Load(string path) =>
        Path.GetExtension(path).Equals(".tga", StringComparison.OrdinalIgnoreCase) ? DecodeTga(File.ReadAllBytes(path)) : LoadGdi(path);

    static (byte[] Rgba, int W, int H) LoadGdi(string path)
    {
        using var src = new Bitmap(path);
        return FromBitmap(src);
    }

    /// <summary>Decodes an image held in memory (e.g. a texture embedded in an FBX); <paramref name="nameHint"/>'s
    /// extension selects TGA, anything else goes through GDI+.</summary>
    public static (byte[] Rgba, int W, int H) Load(byte[] data, string nameHint)
    {
        if (Path.GetExtension(nameHint).Equals(".tga", StringComparison.OrdinalIgnoreCase)) return DecodeTga(data);
        using var ms = new MemoryStream(data);
        using var src = new Bitmap(ms);
        return FromBitmap(src);
    }

    /// <summary>Truevision TGA: uncompressed or RLE, 8-bit grey, 15/16/24/32-bit colour, 8-bit colour-mapped; either origin.</summary>
    public static (byte[] Rgba, int W, int H) DecodeTga(byte[] d)
    {
        if (d.Length < 18) throw new InvalidDataException("TGA file too short");
        int idLen = d[0], cmType = d[1], type = d[2];
        int cmFirst = d[3] | d[4] << 8, cmLen = d[5] | d[6] << 8, cmBits = d[7];
        int w = d[12] | d[13] << 8, h = d[14] | d[15] << 8, bpp = d[16], desc = d[17];
        bool rle = type >= 9, grey = type is 3 or 11, mapped = type is 1 or 9;
        if (type is not (1 or 2 or 3 or 9 or 10 or 11) || w == 0 || h == 0) throw new InvalidDataException($"unsupported TGA image type {type}");
        int p = 18 + idLen;
        byte[]? palette = null;
        int cmBytes = (cmBits + 7) / 8;
        if (cmType == 1) { palette = d[p..(p + cmLen * cmBytes)]; p += cmLen * cmBytes; }
        int px = (bpp + 7) / 8;
        var outp = new byte[w * h * 4];
        void Colour(ReadOnlySpan<byte> s, int bytes, bool isGrey, Span<byte> o)
        {
            if (isGrey) { o[0] = o[1] = o[2] = s[0]; o[3] = bytes > 1 ? s[1] : (byte)255; return; }
            if (bytes == 2) { int v = s[0] | s[1] << 8; o[0] = (byte)((v >> 10 & 31) * 255 / 31); o[1] = (byte)((v >> 5 & 31) * 255 / 31); o[2] = (byte)((v & 31) * 255 / 31); o[3] = 255; return; }
            o[0] = s[2]; o[1] = s[1]; o[2] = s[0]; o[3] = bytes == 4 ? s[3] : (byte)255;
        }
        void Pixel(ReadOnlySpan<byte> s, Span<byte> o)
        {
            if (mapped && palette != null) { int idx = (px == 2 ? s[0] | s[1] << 8 : s[0]) - cmFirst; Colour(palette.AsSpan(Math.Clamp(idx, 0, cmLen - 1) * cmBytes), cmBytes, false, o); }
            else Colour(s, px, grey, o);
        }
        int n = w * h, i = 0;
        var tmp = new byte[4];
        while (i < n)
        {
            if (!rle) { Pixel(d.AsSpan(p, px), outp.AsSpan(i * 4, 4)); p += px; i++; continue; }
            int hdr = d[p++], count = (hdr & 0x7F) + 1;
            if ((hdr & 0x80) != 0)
            {
                Pixel(d.AsSpan(p, px), tmp); p += px;
                for (int k = 0; k < count && i < n; k++, i++) tmp.CopyTo(outp, i * 4);
            }
            else for (int k = 0; k < count && i < n; k++, i++) { Pixel(d.AsSpan(p, px), outp.AsSpan(i * 4, 4)); p += px; }
        }
        bool topDown = (desc & 0x20) != 0, rightLeft = (desc & 0x10) != 0;
        if (!topDown || rightLeft)
        {
            var f = new byte[outp.Length];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int sy = topDown ? y : h - 1 - y, sx = rightLeft ? w - 1 - x : x;
                    Buffer.BlockCopy(outp, (sy * w + sx) * 4, f, (y * w + x) * 4, 4);
                }
            outp = f;
        }
        return (outp, w, h);
    }

    public static (byte[] Rgba, int W, int H) FromBitmap(Bitmap src)
    {
        int w = src.Width, h = src.Height;
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.DrawImage(src, 0, 0, w, h);
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var outp = new byte[w * h * 4];
        var row = new byte[w * 4];
        for (int y = 0; y < h; y++)
        {
            Marshal.Copy(bd.Scan0 + y * bd.Stride, row, 0, w * 4);
            for (int x = 0; x < w; x++)
            {
                int d = (y * w + x) * 4;
                outp[d] = row[x * 4 + 2]; outp[d + 1] = row[x * 4 + 1]; outp[d + 2] = row[x * 4]; outp[d + 3] = row[x * 4 + 3];
            }
        }
        bmp.UnlockBits(bd);
        return (outp, w, h);
    }

    /// <summary>Box-filter downsample by 2 (used to rebuild mip chains).</summary>
    public static (byte[] Rgba, int W, int H) Half(byte[] src, int w, int h)
    {
        int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
        var o = new byte[nw * nh * 4];
        for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++)
                for (int c = 0; c < 4; c++)
                {
                    int x0 = Math.Min(x * 2, w - 1), x1 = Math.Min(x * 2 + 1, w - 1), y0 = Math.Min(y * 2, h - 1), y1 = Math.Min(y * 2 + 1, h - 1);
                    int s = src[(y0 * w + x0) * 4 + c] + src[(y0 * w + x1) * 4 + c] + src[(y1 * w + x0) * 4 + c] + src[(y1 * w + x1) * 4 + c];
                    o[(y * nw + x) * 4 + c] = (byte)((s + 2) / 4);
                }
        return (o, nw, nh);
    }

    public static (byte[] Rgba, int W, int H) Resize(byte[] src, int w, int h, int nw, int nh)
    {
        using var b = ToBitmap(src, w, h);
        using var r = new Bitmap(nw, nh, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(r))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            // edge pixels are clamped (GDI+ otherwise blends transparent black in from outside the image)
            using var ia = new System.Drawing.Imaging.ImageAttributes();
            ia.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY);
            g.DrawImage(b, new Rectangle(0, 0, nw, nh), 0, 0, w, h, GraphicsUnit.Pixel, ia);
        }
        return FromBitmap(r);
    }
}
