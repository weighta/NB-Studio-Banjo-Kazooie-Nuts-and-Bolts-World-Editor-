using NB.Core.Formats;
using NB.Core.Project;

namespace NB.Core.Textures;

/// <summary>
/// Finds and decodes a texture by name anywhere in a workspace, not only in one bundle. Models name their textures
/// "…mip" (resident, levels 1..n), "…top" (streamed, full size) or without a suffix. The same texture can live in the
/// world's own bundle, in the common or shared bundles, or only in a Bundle/50 stream archive.
/// Search order: the preferred CAFF (e.g. the open world), every resident bundle holding the name, then stream archives.
/// Every lookup is recorded (source or failure reason) so the editor can report missing textures.
/// </summary>
public sealed class TextureResolver
{
    readonly Workspace _ws;
    readonly CaffFile? _preferred;
    readonly uint? _preferredBundle;
    readonly Dictionary<string, List<AssetEntry>> _byName = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (byte[] Rgba, int W, int H)?> _cache = new();
    readonly Dictionary<uint, BundleArchive> _streams = new();

    /// <summary>Where each looked-up texture came from ("world", "resident xxxxxx", "streamed xxxxxx") or why it failed.</summary>
    public readonly Dictionary<string, string> Sources = new();
    public IEnumerable<string> Missing => Sources.Where(kv => kv.Value.StartsWith("missing") || kv.Value.StartsWith("error")).Select(kv => kv.Key);

    /// <param name="preferredBundle">The bundle <paramref name="preferred"/> was loaded from: full-size loads then take its own
    /// streamed top level (Bundle/50/&lt;bundle&gt;) first; without it the resident half-size level won over the top level.</param>
    public TextureResolver(Workspace ws, AssetIndex? index, CaffFile? preferred, uint? preferredBundle = null)
    {
        _ws = ws; _preferred = preferred; _preferredBundle = preferredBundle & 0xFFFFFF;
        if (index != null)
            foreach (var e in index.Entries.Where(e => e.Type == "texture"))
            {
                if (!_byName.TryGetValue(e.Name, out var l)) _byName[e.Name] = l = new();
                l.Add(e);
            }
    }

    public static string Stem(string name) => name.EndsWith("top") || name.EndsWith("mip") ? name[..^3] : name;

    /// <summary>Decodes a texture for display: the resident "…mip" asset first (half resolution, fast).</summary>
    public (byte[] Rgba, int W, int H)? Load(string name) => Load(name, full: false);

    /// <summary>Decodes a texture at its largest stored size: the streamed "…top" (full resolution) first, then the
    /// complete texture, then "…mip" (texture library export).</summary>
    public (byte[] Rgba, int W, int H)? LoadFull(string name) => Load(name, full: true);

    /// <summary>Where a texture is stored: the world (preferred) bundle when it holds any of stem/mip/top, otherwise every
    /// indexed bundle holding it (resident or streamed).</summary>
    public List<(uint? Bundle, bool Streamed, string Asset)> Locate(string name)
    {
        string stem = Stem(name);
        var res = new List<(uint?, bool, string)>();
        var cands = new[] { stem + "mip", stem, stem + "top" };
        if (_preferred != null)
            foreach (var c in cands)
                if (_preferred.Symbols.Any(x => AssetIds.DisplayName(x).Equals(c, StringComparison.OrdinalIgnoreCase))) res.Add((null, false, c));
        foreach (var c in cands)
            if (_byName.TryGetValue(c, out var entries))
                foreach (var e in entries) res.Add((e.Bundle, e.Streamed, c));
        return res;
    }

    (byte[] Rgba, int W, int H)? Load(string name, bool full)
    {
        string key = full ? name + "|full" : name;
        if (_cache.TryGetValue(key, out var hit)) return hit;
        (byte[], int, int)? result = null;
        string stem = Stem(name);
        var cands = full ? new[] { stem + "top", stem, stem + "mip" } : new[] { stem + "mip", stem, stem + "top" };
        try
        {
            // 0. full size: the preferred bundle's own streamed top level (this world's copy, e.g. a replaced texture)
            if (full && _preferredBundle is uint pb && _byName.TryGetValue(stem + "top", out var tops) && tops.Any(e => e.Streamed && e.Bundle == pb))
            {
                var e = tops.First(e => e.Streamed && e.Bundle == pb);
                if (!_streams.TryGetValue(pb, out var arc)) _streams[pb] = arc = _ws.LoadStream(pb);
                var se = arc.Entries.FirstOrDefault(x => x.Id == e.Id && x.Data != null);
                if (se != null && TryCaff(CaffFile.Read(se.Data!), stem + "top", out var r0)) { result = r0; Sources[name] = $"streamed {pb:x6} ({stem}top)"; }
            }
            // 1. the preferred (world) bundle
            if (_preferred != null && result == null)
                foreach (var cand in cands)
                    if (TryCaff(_preferred, cand, out var r)) { result = r; Sources[name] = "world bundle (" + cand + ")"; break; }
            // 2. any resident bundle, 3. stream archives
            if (result == null)
                foreach (var cand in cands)
                {
                    if (!_byName.TryGetValue(cand, out var entries)) continue;
                    foreach (var e in entries.OrderBy(e => e.Streamed))
                    {
                        CaffFile caff;
                        if (e.Streamed)
                        {
                            if (!_streams.TryGetValue(e.Bundle, out var arc)) _streams[e.Bundle] = arc = _ws.LoadStream(e.Bundle);
                            var se = arc.Entries.FirstOrDefault(x => x.Id == e.Id && x.Data != null);
                            if (se == null) continue;
                            caff = CaffFile.Read(se.Data!);
                        }
                        else caff = _ws.LoadResident(e.Bundle);
                        if (TryCaff(caff, cand, out var r)) { result = r; Sources[name] = (e.Streamed ? "streamed " : "resident ") + e.Bundle.ToString("x6") + " (" + cand + ")"; break; }
                    }
                    if (result != null) break;
                }
            if (result == null) Sources[name] = _byName.Keys.Any(k => Stem(k) == stem) ? "missing: found in the index but could not be decoded" : "missing: not in any bundle";
        }
        catch (Exception ex) { Sources[name] = "error: " + ex.Message; }
        _cache[key] = result;
        return result;
    }

    static bool TryCaff(CaffFile caff, string cand, out (byte[], int, int) r)
    {
        r = default;
        int s = caff.Symbols.FindIndex(x => AssetIds.DisplayName(x).Equals(cand, StringComparison.OrdinalIgnoreCase)) + 1;
        if (s == 0) return false;
        var parts = caff.PartsOf(s).ToList();
        var cpu = parts.FirstOrDefault(p => caff.SectionOf(p).Name == ".data");
        // most textures keep their pixels in ".texturegpu"; textures stored beside a model (Banjoland N64 exhibits) use ".gpu"
        var gpu = parts.FirstOrDefault(p => caff.SectionOf(p).Name == ".texturegpu") ?? parts.FirstOrDefault(p => caff.SectionOf(p).Name == ".gpu");
        if (cpu == null || gpu == null || !TextureHeader.IsTexture(cpu.Data)) return false;
        r = new TextureAsset(cpu.Data, gpu.Data).Decode(0);
        return true;
    }
}
