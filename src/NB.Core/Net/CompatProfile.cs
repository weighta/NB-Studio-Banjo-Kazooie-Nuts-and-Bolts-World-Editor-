using System.Security.Cryptography;
using System.Text.Json;

namespace NB.Core.Net;

/// <summary>
/// What must match between players of one multiplayer room: SHA-256 of default.xex (game version + baked Afterparty
/// exe mods) and of every bundle file (Bundle/4f resident, Bundle/50 streamed: worlds, vehicle parts, assets).
/// Loose loctext / video files are not compared (they do not affect game state). Hashes are cached by
/// (path, size, write time) in %LOCALAPPDATA%\NBModTool\hashcache.json, so only changed files are re-read.
/// </summary>
public sealed class CompatProfile
{
    public Dictionary<string, string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>SHA-256 over the sorted (path, hash) list.</summary>
    public string Fingerprint { get; set; } = "";
    public ushort Tag => Fingerprint.Length >= 4 ? Convert.ToUInt16(Fingerprint[..4], 16) : (ushort)0;

    public sealed record Difference(string File, string Kind, string Area);

    /// <summary>Well-known bundles (see docs/FORMATS.md): the vehicle-parts library and the town world.</summary>
    static string Area(string rel)
    {
        var r = rel.Replace('\\', '/').ToLowerInvariant();
        if (r == "default.xex") return "game executable (version or Afterparty exe mods)";
        if (r.EndsWith("/685374") || r.EndsWith("/4e967e")) return "vehicle parts library (custom / extra parts)";
        if (r.EndsWith("/234cec")) return "Showdown Town world (world edits / imported models)";
        return "game data bundle (worlds, vehicles or assets)";
    }

    public static CompatProfile FromGame(string gameDir, IProgress<(string, double)>? progress = null)
    {
        var files = new List<string> { Path.Combine(gameDir, "default.xex") };
        foreach (var sub in new[] { "4f", "50" })
        {
            var d = Path.Combine(gameDir, "Bundle", sub);
            if (Directory.Exists(d)) files.AddRange(Directory.GetFiles(d).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
        }
        var cache = LoadCache();
        var p = new CompatProfile();
        for (int i = 0; i < files.Count; i++)
        {
            var f = files[i];
            if (!File.Exists(f)) continue;
            var fi = new FileInfo(f);
            // hard links (NB Multiplayer editions link the unchanged game files) share the NTFS file id: hashed once
            string key = (FileId(f) is { } id ? "id:" + id : fi.FullName) + $"|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
            if (!cache.TryGetValue(key, out var h))
            {
                progress?.Report(($"hashing {Path.GetFileName(f)}", i / (double)files.Count));
                using var s = File.OpenRead(f);
                h = Convert.ToHexString(SHA256.HashData(s));
                cache[key] = h;
            }
            p.Files[Path.GetRelativePath(gameDir, f).Replace('\\', '/')] = h;
        }
        SaveCache(cache);
        var all = string.Join("\n", p.Files.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => kv.Key.ToLowerInvariant() + "=" + kv.Value));
        p.Fingerprint = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(all)));
        return p;
    }

    /// <summary>Everything that differs between the host's profile and a joining player's (empty = compatible).</summary>
    public List<Difference> CompareTo(CompatProfile host)
    {
        var res = new List<Difference>();
        foreach (var (f, h) in host.Files)
        {
            if (!Files.TryGetValue(f, out var mine)) res.Add(new(f, "missing on this PC", Area(f)));
            else if (!string.Equals(mine, h, StringComparison.OrdinalIgnoreCase)) res.Add(new(f, "different", Area(f)));
        }
        foreach (var f in Files.Keys.Where(f => !host.Files.ContainsKey(f))) res.Add(new(f, "not in the host's game", Area(f)));
        return res;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct ByHandleInfo { public uint Attr; public long C, A, W; public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle h, out ByHandleInfo info);

    /// <summary>Volume serial + file index (the same for every hard link of a file), or null.</summary>
    static string? FileId(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return GetFileInformationByHandle(fs.SafeFileHandle, out var i) ? $"{i.Volume:X8}:{i.IndexHigh:X8}{i.IndexLow:X8}" : null;
        }
        catch (Exception) { return null; }
    }

    public string ToJson() => JsonSerializer.Serialize(this);
    public static CompatProfile? FromJson(string json) => JsonSerializer.Deserialize<CompatProfile>(json);

    static string CachePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NBModTool", "hashcache.json");
    static Dictionary<string, string> LoadCache()
    {
        try { if (File.Exists(CachePath)) return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CachePath)) ?? new(); } catch (Exception) { }
        return new();
    }
    static void SaveCache(Dictionary<string, string> c)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!); File.WriteAllText(CachePath, JsonSerializer.Serialize(c)); } catch (Exception) { }
    }
}
