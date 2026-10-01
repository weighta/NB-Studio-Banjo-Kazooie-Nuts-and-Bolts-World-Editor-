using System.Text.Json;
using NB.Core.Formats;

namespace NB.Core.Project;

/// <summary>One asset occurrence in a bundle.</summary>
public sealed class AssetEntry
{
    public uint Bundle { get; set; }
    public int Symbol { get; set; }            // 1-based symbol index in the resident CAFF; 0 for stream-only
    public string Name { get; set; } = "";     // display name
    public string Type { get; set; } = "";
    public uint Id { get; set; }
    public bool Streamed { get; set; }         // lives in Bundle/50
    public string Parts { get; set; } = "";    // e.g. ".data:70,.texturegpu:20000"
    public long Size { get; set; }
}

/// <summary>Searchable index of all assets in the game (built once, cached as JSON in the workspace).</summary>
public sealed class AssetIndex
{
    public List<AssetEntry> Entries { get; set; } = new();
    public Dictionary<uint, string> BundleSummary { get; set; } = new();

    public static string PathFor(Workspace ws) => Path.Combine(ws.CacheDir, "assetindex.json");

    public static AssetIndex LoadOrBuild(Workspace ws, IProgress<(string, double)>? progress = null, bool rebuild = false)
    {
        var p = PathFor(ws);
        if (!rebuild && File.Exists(p))
        {
            var idx = JsonSerializer.Deserialize<AssetIndex>(File.ReadAllText(p));
            if (idx != null && idx.Entries.Count > 0) return idx;
        }
        var built = Build(ws, progress);
        Directory.CreateDirectory(ws.CacheDir);
        File.WriteAllText(p, JsonSerializer.Serialize(built));
        return built;
    }

    public static AssetIndex Build(Workspace ws, IProgress<(string, double)>? progress = null)
    {
        var idx = new AssetIndex();
        var bundles = ws.Game.Bundles().ToList();
        int n = 0;
        foreach (var b in bundles)
        {
            progress?.Report(($"indexing {b:x6}", (double)n++ / bundles.Count));
            var names = new Dictionary<uint, string>();
            try
            {
                var c = ws.LoadResident(b);
                var byType = new Dictionary<string, int>();
                for (int s = 1; s <= c.Symbols.Count; s++)
                {
                    var sym = c.Symbols[s - 1];
                    var parts = c.PartsOf(s).ToList();
                    var p = AssetIds.Parse(sym);
                    var e = new AssetEntry
                    {
                        Bundle = b, Symbol = s, Name = AssetIds.DisplayName(sym), Type = p?.Type ?? (sym == "pool" || sym == "manifest" ? sym : "other"),
                        Id = AssetIds.IdOf(sym) ?? 0, Parts = string.Join(",", parts.Select(x => $"{c.SectionOf(x).Name}:{x.Data.Length:x}")),
                        Size = parts.Sum(x => (long)x.Data.Length),
                    };
                    idx.Entries.Add(e);
                    if (e.Id != 0) names[e.Id] = e.Name;
                    byType[e.Type] = byType.GetValueOrDefault(e.Type) + 1;
                }
                idx.BundleSummary[b] = Describe(c);
                ws.ForgetCache(b); // keep memory bounded while indexing
            }
            catch (Exception ex) { idx.BundleSummary[b] = "error: " + ex.Message; }
            try
            {
                var a = ws.LoadStream(b);
                foreach (var e in a.Entries)
                {
                    if (e.Data == null) continue;
                    string name = "", type = e.Kind;
                    if (e.Kind == "caff")
                    {
                        try
                        {
                            var sc = CaffFile.Read(e.Data);
                            var main = sc.Symbols.FirstOrDefault(x => AssetIds.IdOf(x) == e.Id) ?? sc.Symbols.FirstOrDefault(x => x.Contains("aid_")) ?? "";
                            name = AssetIds.DisplayName(main); type = AssetIds.Parse(main)?.Type ?? "caff";
                        }
                        catch { }
                    }
                    else if (e.Kind == "xwb") { type = "wavebank"; name = $"wavebank_{e.Id:X8}"; }
                    if (name == "") name = names.GetValueOrDefault(e.Id, $"{e.Kind}_{e.Id:X8}");
                    idx.Entries.Add(new AssetEntry { Bundle = b, Name = name, Type = type, Id = e.Id, Streamed = true, Size = e.Data.Length });
                }
            }
            catch { }
        }
        progress?.Report(("done", 1));
        return idx;
    }

    /// <summary>Short human description of a bundle from its dominant world/background content.</summary>
    public static string Describe(CaffFile c)
    {
        var bg = c.Symbols.Where(s => s.StartsWith("aid_model_banjox_background_") && s.EndsWith("_default")).Select(s => s["aid_model_banjox_background_".Length..^"_default".Length]).ToList();
        if (bg.Count > 0) return "world: " + string.Join(", ", bg);
        var words = c.Symbols.Select(s => AssetIds.Parse(s)?.Name).Where(r => r != null).Select(r => r!.Split('_')).Where(p => p.Length > 1).Select(p => p[1]).GroupBy(x => x).OrderByDescending(g => g.Count()).Take(3).Select(g => g.Key);
        return string.Join(", ", words);
    }
}

/// <summary>A loadable world scene: a bundle containing a background (level) model.</summary>
public sealed record WorldEntry(string World, string Display, uint Bundle, string BackgroundModel);

public static class WorldCatalog
{
    public static readonly Dictionary<string, string> DisplayNames = new()
    {
        ["showdowntown"] = "Showdown Town", ["nuttyacres"] = "Nutty Acres", ["cpu"] = "LOGBOX 720", ["banjoland"] = "Banjoland",
        ["worldofsport"] = "World of Sports", ["terrorium"] = "Terrarium of Terror", ["spiralmountain"] = "Spiral Mountain",
        ["carpark"] = "Car Park (test track)", ["banjoshouseinterior"] = "Banjo's House",
    };

    public static List<WorldEntry> FromIndex(AssetIndex idx) =>
        idx.Entries.Where(e => !e.Streamed && e.Type == "model" && e.Name.StartsWith("aid_model_banjox_background_") && e.Name.EndsWith("_default"))
            .Select(e =>
            {
                var w = e.Name["aid_model_banjox_background_".Length..^"_default".Length];
                return new WorldEntry(w, DisplayNames.GetValueOrDefault(w, w), e.Bundle, e.Name);
            })
            .OrderBy(w => w.Display).ThenBy(w => w.Bundle).ToList();
}
