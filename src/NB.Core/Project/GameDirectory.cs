using NB.Core.Compression;
using NB.Core.Formats;
using NB.Core.IO;

namespace NB.Core.Project;

/// <summary>An (original or working) Nuts &amp; Bolts game directory.</summary>
public sealed class GameDirectory
{
    public readonly string Root;
    public GameDirectory(string root) { Root = Path.GetFullPath(root); }

    public string Xex => Path.Combine(Root, "default.xex");
    public string ResidentPath(uint bundle) => Path.Combine(Root, "Bundle", "4f", (bundle & 0xFFFFFF).ToString("x6"));
    public string StreamPath(uint bundle) => Path.Combine(Root, "Bundle", "50", (bundle & 0xFFFFFF).ToString("x6"));

    public IEnumerable<uint> Bundles() =>
        Directory.Exists(Path.Combine(Root, "Bundle", "4f"))
            ? Directory.GetFiles(Path.Combine(Root, "Bundle", "4f")).Select(f => Convert.ToUInt32(Path.GetFileName(f), 16)).OrderBy(x => x)
            : Enumerable.Empty<uint>();

    public sealed record ValidationReport(bool Ok, List<string> Errors, List<string> Info);

    /// <summary>Checks that the directory looks like an extracted Nuts &amp; Bolts disc.</summary>
    public ValidationReport Validate()
    {
        var err = new List<string>(); var info = new List<string>();
        if (!File.Exists(Xex)) err.Add("default.xex not found");
        else
        {
            try
            {
                var x = XexFile.Read(File.ReadAllBytes(Xex));
                info.Add($"default.xex: title {x.TitleId:X8}, entry 0x{x.EntryPoint:X8}");
                if (x.TitleId != 0x4D5307ED) err.Add($"title id {x.TitleId:X8} is not Banjo-Kazooie: Nuts & Bolts (4D5307ED)");
            }
            catch (Exception e) { err.Add("default.xex unreadable: " + e.Message); }
        }
        foreach (var sub in new[] { "Bundle/4f", "Bundle/50", "Debug", "loctext" })
            if (!Directory.Exists(Path.Combine(Root, sub))) err.Add($"missing folder {sub}");
        if (err.Count == 0)
        {
            int res = 0, comp = 0, raw = 0, str = 0;
            foreach (var f in Directory.GetFiles(Path.Combine(Root, "Bundle", "4f")))
            {
                res++;
                using var fs = File.OpenRead(f);
                Span<byte> h = stackalloc byte[4];
                fs.ReadExactly(h);
                if (BE.U32(h, 0) == XCompressFile.MagicTDecode) comp++; else if (CaffFile.IsCaff(h)) raw++;
            }
            str = Directory.GetFiles(Path.Combine(Root, "Bundle", "50")).Length;
            info.Add($"resident bundles: {res} ({comp} xcompress-compressed, {raw} raw CAFF); stream bundles: {str}");
            if (res == 0) err.Add("no bundles in Bundle/4f");
            int videos = Directory.Exists(Path.Combine(Root, "Debug", "36")) ? Directory.GetFiles(Path.Combine(Root, "Debug", "36"), "*", SearchOption.AllDirectories).Length : 0;
            info.Add($"videos (Debug/36): {videos}; languages: {Directory.GetDirectories(Path.Combine(Root, "loctext")).Length}");
        }
        return new ValidationReport(err.Count == 0, err, info);
    }

    /// <summary>Reads a resident bundle, decompressing it if needed.</summary>
    public byte[] ReadResidentRaw(uint bundle)
    {
        var d = File.ReadAllBytes(ResidentPath(bundle));
        return XCompressFile.IsCompressed(d) ? XCompressFile.Decompress(d) : d;
    }
}
