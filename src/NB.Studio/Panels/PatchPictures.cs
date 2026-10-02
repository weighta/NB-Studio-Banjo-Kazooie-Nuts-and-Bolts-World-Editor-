using System.Drawing.Imaging;
using System.IO.Compression;

namespace NB.Studio.Panels;

/// <summary>
/// Screenshots inside a .nbpatch: pictures/01.jpg … (at most 1920 px wide, JPEG quality 90), listed in patch.json as
/// Extra["pictures"] = "pictures/01.jpg;pictures/02.jpg". Tools that do not know them ignore the entries (patches are
/// verified per game file, not per zip).
/// </summary>
public static class PatchPictures
{
    public const string ExtraKey = "pictures";

    /// <summary>The entry names the pictures will get (set this as Extra["pictures"] before the manifest is written).</summary>
    public static string EntryList(IReadOnlyList<string> files) => string.Join(";", files.Select((_, i) => $"pictures/{i + 1:00}.jpg"));

    /// <summary>Adds the pictures to a written patch; returns how many were stored.</summary>
    public static int AddTo(string patchPath, IReadOnlyList<string> files)
    {
        if (files.Count == 0) return 0;
        var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var enc = new EncoderParameters(1);
        enc.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L);
        using var zip = ZipFile.Open(patchPath, ZipArchiveMode.Update);
        for (int i = 0; i < files.Count; i++)
        {
            using var img = Image.FromFile(files[i]);
            float k = Math.Min(1f, 1920f / img.Width);
            using var bmp = new Bitmap(img, Math.Max(1, (int)(img.Width * k)), Math.Max(1, (int)(img.Height * k)));
            var e = zip.CreateEntry($"pictures/{i + 1:00}.jpg", CompressionLevel.NoCompression);
            using var s = e.Open();
            bmp.Save(s, jpeg, enc);
        }
        return files.Count;
    }
}
