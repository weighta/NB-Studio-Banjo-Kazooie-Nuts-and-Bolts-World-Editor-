using System.Security.Cryptography;
using System.Text.Json;
using NB.Core.Compression;
using NB.Core.Formats;

namespace NB.Core.Project;

/// <summary>
/// Turns a game folder someone modded by hand (replaced textures, hex-edited default.xex, ...) into a mod: compares it
/// with the retail game, using a list of the size and SHA-256 of every retail file shipped with the tools
/// (Data/retail_fingerprints.json, no game data), and with a clean copy for the original bytes of the changed files.
/// The analysis reports what changed per file down to assets (textures, models, markers, scripts, vehicle parts), the
/// executable as word changes (known executable mods recognised), and suggests the mod's category, tags and whether it
/// matters online.
/// </summary>
public static class GameDiff
{
    // ------------------------------------------------------------------ retail fingerprints

    public sealed record Fingerprint(long Size, string Sha256);

    static Dictionary<string, Fingerprint>? _retail;

    /// <summary>Relative path ('/' separated, as on the disc) → size and SHA-256 of the retail game's files.</summary>
    public static IReadOnlyDictionary<string, Fingerprint> Retail
    {
        get
        {
            if (_retail != null) return _retail;
            using var s = typeof(GameDiff).Assembly.GetManifestResourceStream("retail_fingerprints.json") ?? throw new InvalidOperationException("retail fingerprints missing");
            using var doc = JsonDocument.Parse(s);
            var d = new Dictionary<string, Fingerprint>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in doc.RootElement.GetProperty("files").EnumerateObject())
                d[p.Name] = new Fingerprint(p.Value[0].GetInt64(), p.Value[1].GetString()!);
            return _retail = d;
        }
    }

    /// <summary>Files a folder may have that are not game files (patch backups, logs, NB tools' own files).</summary>
    public static bool IsToolFile(string rel)
    {
        var r = rel.Replace('\\', '/');
        return r.StartsWith(PatchPackage.BackupDirName + "/", StringComparison.OrdinalIgnoreCase) || r.Equals(PatchPackage.LogFileName, StringComparison.OrdinalIgnoreCase)
            || r.EndsWith(".nbtmp", StringComparison.OrdinalIgnoreCase) || r.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || r.Equals("NBMOD_CHANGES.txt", StringComparison.OrdinalIgnoreCase) || r.Equals("edition.json", StringComparison.OrdinalIgnoreCase)
            || r.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || r.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase);
    }

    public static string Sha(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    public sealed record FolderState(string Path, string State);   // State: changed | new | missing

    /// <summary>
    /// Every file of a game folder that differs from the retail game (size first, then SHA-256), plus retail files it
    /// lacks. Unchanged files are hashed too, so this reads the whole folder (6.6 GB) once.
    /// </summary>
    public static List<FolderState> CompareWithRetail(string dir, IProgress<(string Text, double Fraction)>? progress = null, CancellationToken ct = default)
    {
        dir = Path.GetFullPath(dir);
        var res = new List<FolderState>();
        var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/')).Where(r => !IsToolFile(r)).ToList();
        var have = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        long total = files.Sum(f => new FileInfo(Path.Combine(dir, f)).Length), done = 0;
        var toHash = new List<string>();
        foreach (var rel in files)
        {
            if (!Retail.TryGetValue(rel, out var fp)) { res.Add(new(rel, "new")); continue; }
            long len = new FileInfo(Path.Combine(dir, rel)).Length;
            if (len != fp.Size) { res.Add(new(rel, "changed")); done += len; continue; }
            toHash.Add(rel);
        }
        object gate = new();
        Parallel.ForEach(toHash, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = ct }, rel =>
        {
            var p = Path.Combine(dir, rel);
            bool same = Sha(p) == Retail[rel].Sha256;
            lock (gate)
            {
                if (!same) res.Add(new(rel, "changed"));
                done += new FileInfo(p).Length;
                progress?.Report(($"Comparing {rel}", (double)done / Math.Max(1, total)));
            }
        });
        foreach (var rel in Retail.Keys.Where(k => !have.Contains(k))) res.Add(new(rel, "missing"));
        return res.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The files of a game folder's compatibility profile (default.xex and every bundle, hashed and cached by
    /// <see cref="Net.CompatProfile"/>) that are not the retail game's: a quick "is this the original game?" check.
    /// </summary>
    public static List<string> NotRetail(Net.CompatProfile profile) =>
        profile.Files.Where(kv => !Retail.TryGetValue(kv.Key, out var fp) || !fp.Sha256.Equals(kv.Value, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Whether <paramref name="dir"/> has the retail version of every file in <paramref name="paths"/> (empty list = yes).</summary>
    public static List<string> NotRetail(string dir, IEnumerable<string> paths)
    {
        var bad = new List<string>();
        foreach (var rel in paths)
        {
            var p = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!Retail.TryGetValue(rel, out var fp)) continue;
            if (!File.Exists(p)) bad.Add(rel + " (missing)");
            else if (new FileInfo(p).Length != fp.Size || Sha(p) != fp.Sha256) bad.Add(rel);
        }
        return bad;
    }

    /// <summary>
    /// The first candidate folder that is a game folder with the retail version of every changed file (the clean reference
    /// the mod's differences are taken against), or null.
    /// </summary>
    public static string? FindReference(IEnumerable<string?> candidates, IEnumerable<string> changed, string modDir)
    {
        var need = changed.Where(Retail.ContainsKey).ToList();
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c) || !Directory.Exists(c) || !File.Exists(Path.Combine(c, "default.xex"))) continue;
            if (Path.GetFullPath(c).TrimEnd('\\').Equals(Path.GetFullPath(modDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
            if (NotRetail(c, need).Count == 0) return Path.GetFullPath(c);
        }
        return null;
    }

    // ------------------------------------------------------------------ analysis

    public sealed class FileChange
    {
        public string Path { get; set; } = "";
        public string State { get; set; } = "changed";   // changed | new | missing
        /// <summary>What the file is: "Showdown Town (world bundle)", "streamed assets", "text (english)", "video", "game executable".</summary>
        public string Area { get; set; } = "";
        public List<string> Changed { get; set; } = new();
        public List<string> Added { get; set; } = new();
        public List<string> Removed { get; set; } = new();
        /// <summary>Asset kinds touched (texture, model, marker, script, objparams, vehicle, audio, text, ...), for the category.</summary>
        public HashSet<string> Kinds { get; set; } = new();
        public string Summary => State switch
        {
            "new" => "new file",
            "missing" => "deleted (a mod cannot delete game files: ignored)",
            _ => Changed.Count + Added.Count + Removed.Count == 0 ? "changed" :
                 string.Join(", ", new[] { Count(Changed, "changed"), Count(Added, "added"), Count(Removed, "removed") }.Where(x => x.Length > 0)),
        };
        static string Count(List<string> l, string what) => l.Count == 0 ? "" : $"{l.Count} asset{(l.Count == 1 ? "" : "s")} {what}";
    }

    public sealed class ExeChange
    {
        /// <summary>Known executable mods found complete in the modified executable.</summary>
        public List<Mods.ExeMod> Known { get; set; } = new();
        /// <summary>Other changed words (address, original, modified).</summary>
        public List<Mods.ExeWord> Other { get; set; } = new();
        public string? Problem { get; set; }
    }

    public sealed class Report
    {
        public string ModDir { get; set; } = "";
        public string? ReferenceDir { get; set; }
        public List<FileChange> Files { get; set; } = new();
        public ExeChange? Exe { get; set; }
        public string Category { get; set; } = "tweak";
        public string Multiplayer { get; set; } = "world";
        public List<string> Tags { get; set; } = new();
        public string SuggestedName { get; set; } = "";
        public string SuggestedDescription { get; set; } = "";
        public List<string> Warnings { get; set; } = new();
        /// <summary>Files the mod will carry (changed and new, not the executable).</summary>
        public IEnumerable<FileChange> Carried => Files.Where(f => f.State != "missing" && !f.Path.Equals("default.xex", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Analyses a modified game folder against the retail fingerprints and a clean reference copy (needed for the asset-level
    /// view and the executable; without it only the file list is reported).
    /// </summary>
    public static Report Analyze(string modDir, string? referenceDir, IProgress<(string Text, double Fraction)>? progress = null, CancellationToken ct = default,
        List<FolderState>? compared = null)
    {
        modDir = Path.GetFullPath(modDir);
        var rep = new Report { ModDir = modDir, ReferenceDir = referenceDir == null ? null : Path.GetFullPath(referenceDir) };
        compared ??= CompareWithRetail(modDir, new Progress<(string Text, double Fraction)>(p => progress?.Report((p.Text, p.Fraction * 0.7))), ct);
        int k = 0;
        foreach (var st in compared)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(($"Looking inside {st.Path}", 0.7 + 0.3 * k++ / Math.Max(1, compared.Count)));
            var fc = new FileChange { Path = st.Path, State = st.State, Area = Area(st.Path) };
            rep.Files.Add(fc);
            if (st.State == "missing") continue;
            if (st.Path.Equals("default.xex", StringComparison.OrdinalIgnoreCase))
            {
                if (rep.ReferenceDir != null) rep.Exe = ExeDiff(Path.Combine(rep.ReferenceDir, "default.xex"), Path.Combine(modDir, "default.xex"));
                fc.Kinds.Add("executable");
                continue;
            }
            try { if (st.State == "changed" && rep.ReferenceDir != null) Inspect(fc, Path.Combine(rep.ReferenceDir, st.Path), Path.Combine(modDir, st.Path)); }
            catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException) { fc.Kinds.Add("data"); rep.Warnings.Add($"{st.Path}: could not look inside ({e.Message})"); }
            if (fc.Kinds.Count == 0) fc.Kinds.Add(KindOfPath(st.Path));
        }
        if (rep.Files.Any(f => f.State == "missing"))
            rep.Warnings.Add($"{rep.Files.Count(f => f.State == "missing")} game file(s) are missing from the modified folder; a mod cannot delete files, so they stay as they are.");
        if (rep.Exe?.Problem is { } xp) rep.Warnings.Add("default.xex: " + xp);
        if (rep.ReferenceDir == null && rep.Files.Any(f => f.State == "changed"))
            rep.Warnings.Add("No clean copy of the game was found: choose one to see what changed inside the files and to build the mod.");
        Suggest(rep);
        return rep;
    }

    static string Area(string rel)
    {
        var r = rel.Replace('\\', '/').ToLowerInvariant();
        if (r == "default.xex") return "game executable";
        if (r.StartsWith("bundle/4f/")) return KnownBundles.TryGetValue(r[^6..], out var n) ? n : "resident bundle";
        if (r.StartsWith("bundle/50/")) return KnownBundles.TryGetValue(r[^6..], out var n) ? n + " (streamed assets)" : "streamed assets";
        if (r.StartsWith("loctext/")) return $"text ({r.Split('/')[1]})";
        if (r.StartsWith("debug/11/")) return "text (English)";
        if (r.StartsWith("debug/36/")) return "video";
        return "other game file";
    }

    /// <summary>Well-known bundles (24-bit file names).</summary>
    public static readonly Dictionary<string, string> KnownBundles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["234cec"] = "Showdown Town", ["685374"] = "shared bundle (vehicle parts, scripts)", ["4e967e"] = "vehicle parts library",
        ["757c4b"] = "title screen", ["36dc87"] = "Banjo's House",
    };

    static string KindOfPath(string rel)
    {
        var r = rel.Replace('\\', '/').ToLowerInvariant();
        if (r.StartsWith("loctext/") || r.StartsWith("debug/11/")) return "text";
        if (r.StartsWith("debug/36/")) return "video";
        if (r.EndsWith(".xwb") || r.Contains("audio") || r.Contains("sound")) return "audio";
        return "data";
    }

    static void Inspect(FileChange fc, string origPath, string modPath)
    {
        var o = Normal(File.ReadAllBytes(origPath));
        var m = Normal(File.ReadAllBytes(modPath));
        if (CaffFile.IsCaff(o) && CaffFile.IsCaff(m)) { DiffCaff(fc, CaffFile.Read(o), CaffFile.Read(m), ""); return; }
        if (BundleArchive.IsArchive(o) && BundleArchive.IsArchive(m))
        {
            var oa = BundleArchive.Read(o); var ma = BundleArchive.Read(m);
            var oe = oa.Entries.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
            foreach (var e in ma.Entries)
            {
                if (!oe.TryGetValue(e.Id, out var x)) { fc.Added.Add($"entry {e.Id:X8}"); fc.Kinds.Add(EntryKind(e)); continue; }
                if (x.Data == null || e.Data == null || x.Data.AsSpan().SequenceEqual(e.Data)) continue;
                var od = Normal(x.Data); var md = Normal(e.Data);
                if (od.AsSpan().SequenceEqual(md)) continue;
                if (CaffFile.IsCaff(od) && CaffFile.IsCaff(md)) DiffCaff(fc, CaffFile.Read(od), CaffFile.Read(md), "");
                else { fc.Changed.Add($"entry {e.Id:X8} ({e.Kind})"); fc.Kinds.Add(EntryKind(e)); }
            }
            var me = ma.Entries.Select(e => e.Id).ToHashSet();
            foreach (var e in oa.Entries.Where(e => !me.Contains(e.Id))) fc.Removed.Add($"entry {e.Id:X8}");
            return;
        }
        fc.Kinds.Add(KindOfPath(fc.Path));
    }

    static string EntryKind(BundleEntry e) => e.Kind == "xwb" ? "audio" : "data";
    static byte[] Normal(byte[] d) => XCompressFile.IsCompressed(d) ? XCompressFile.Decompress(d) : d;

    static void DiffCaff(FileChange fc, CaffFile o, CaffFile m, string prefix)
    {
        var so = ModMerge.Shapes(o); var sm = ModMerge.Shapes(m);
        foreach (var (name, shape) in sm)
        {
            if (name is "manifest" or "(shared data)") continue;
            if (!so.TryGetValue(name, out var x)) { fc.Added.Add(prefix + name); fc.Kinds.Add(KindOf(name)); }
            else if (x != shape) { fc.Changed.Add(prefix + name); fc.Kinds.Add(KindOf(name)); }
        }
        foreach (var name in so.Keys.Where(n => n is not ("manifest" or "(shared data)") && !sm.ContainsKey(n))) { fc.Removed.Add(prefix + name); fc.Kinds.Add(KindOf(name)); }
        if (so.TryGetValue("(shared data)", out var a) && (!sm.TryGetValue("(shared data)", out var b) || a != b)) fc.Kinds.Add("data");
        // a world bundle: name the world
        var bg = m.Symbols.Select(AssetIds.DisplayName).FirstOrDefault(s => s.StartsWith("aid_model_banjox_background_") && s.EndsWith("_default"));
        if (bg != null && fc.Path.StartsWith("Bundle/4f/", StringComparison.OrdinalIgnoreCase))
        {
            var w = bg["aid_model_banjox_background_".Length..^"_default".Length];
            fc.Area = WorldCatalog.DisplayNames.GetValueOrDefault(w, w) + " (world bundle)";
        }
    }

    /// <summary>Asset kind from its name: texture, model, marker, script, part (vehicle part record), vehicle, objparams, effect, audio, text, ...</summary>
    public static string KindOf(string name)
    {
        var p = AssetIds.Parse(name);
        if (p == null) return "data";
        var (type, n) = p.Value;
        if (type == "objparams" && n.StartsWith("banjox_vehicleblock_")) return "part";
        return type switch
        {
            "texture" or "ddstexture" => "texture",
            "model" or "anim" or "animevents" or "animtable" => "model",
            "vertexshader" or "pixelshader" or "fxemitter" or "fxparticle" or "gpuparticleeffect" or "3dgpuparticleeffect" or "compositeeffect"
                or "explosioneffect" or "blobsdropleteffect" or "fxcamshake" or "fxrumble" => "effect",
            "marker" or "pathenginepreprocess" => "marker",
            "script" or "scripttable" or "statetable" or "challenge" or "actorgoals" => "script",
            "havok" or "avatarhavokdata" => "collision",
            "xcuelist" => "audio",
            "loctext" or "dialog" or "callout" or "font" or "xuipackage" or "xuicachefile" or "xuiloadlist" => "text",
            "cutscene" or "cutsceneevents" or "cutcam" or "video" => "video",
            "vehicle" => "vehicle",
            "objparams" => "objparams",
            "misc" => "misc",
            _ => "data",
        };
    }

    // ------------------------------------------------------------------ executable

    /// <summary>The words a modified default.xex changes in the executable image, with known executable mods recognised.</summary>
    public static ExeChange ExeDiff(string originalXex, string modifiedXex)
    {
        var res = new ExeChange();
        byte[] oi, mi; uint ob, mb;
        try
        {
            var ox = XexFile.Read(File.ReadAllBytes(originalXex)); oi = ox.GetImage(); ob = ox.ImageBase;
            var mx = XexFile.Read(File.ReadAllBytes(modifiedXex)); mi = mx.GetImage(); mb = mx.ImageBase;
        }
        catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException)
        {
            res.Problem = "could not read the executable (" + e.Message + ")"; return res;
        }
        if (ob != mb || oi.Length != mi.Length) { res.Problem = "the executable's layout changed (not a word edit); it cannot be turned into a mod"; return res; }
        var diff = new Dictionary<uint, (uint O, uint M)>();
        for (int i = 0; i + 4 <= oi.Length; i += 4)
        {
            uint a = IO.BE.U32(oi, i), b = IO.BE.U32(mi, i);
            if (a != b) diff[ob + (uint)i] = (a, b);
        }
        foreach (var mod in Mods.ExePatches.All)
        {
            if (mod.Words.Count == 0 || !mod.Words.All(w => diff.TryGetValue(w.Address, out var d) && d.O == w.Original && d.M == w.Patched)) continue;
            res.Known.Add(mod);
            foreach (var w in mod.Words) diff.Remove(w.Address);
        }
        foreach (var (addr, (o, m)) in diff.OrderBy(kv => kv.Key)) res.Other.Add(new Mods.ExeWord(addr, o, m, ""));
        if (res.Other.Count > 4096) res.Problem = $"{res.Other.Count} changed words: too many for an executable mod (was the file rebuilt or re-signed?)";
        return res;
    }

    // ------------------------------------------------------------------ suggestions

    static void Suggest(Report r)
    {
        var kinds = r.Files.Where(f => f.State != "missing").SelectMany(f => f.Kinds).ToHashSet();
        bool exe = r.Exe != null && (r.Exe.Known.Count > 0 || r.Exe.Other.Count > 0);
        if (exe) kinds.Add("executable");
        bool worldBundle = r.Files.Any(f => f.Area.Contains("(world bundle)"));
        var gameplayKinds = new[] { "marker", "script", "collision", "vehicle", "objparams", "misc", "data" };
        if (kinds.Contains("part") && !kinds.Overlaps(gameplayKinds.Except(new[] { "data" }))) r.Category = "parts";
        else if (worldBundle && kinds.Overlaps(gameplayKinds)) r.Category = "map";
        else if (kinds.Overlaps(gameplayKinds) || kinds.Contains("part")) r.Category = kinds.Contains("part") ? "parts" : "gameplay";
        else if (kinds.Overlaps(new[] { "texture", "model", "effect", "video" })) r.Category = "visual";
        else if (kinds.Contains("audio")) r.Category = "audio";
        else if (kinds.Contains("text")) r.Category = "gameplay";
        else r.Category = "tweak";
        // online: looks, sounds and text only = cosmetic; anything else (layout, physics, parts, executable) = every player needs it
        r.Multiplayer = kinds.Count > 0 && kinds.IsSubsetOf(new[] { "texture", "model", "effect", "audio", "text", "video" }) ? "cosmetic" : "world";
        foreach (var f in r.Files.Where(f => f.Area.EndsWith("(world bundle)")))
        {
            var w = f.Area[..^" (world bundle)".Length];
            if (!r.Tags.Contains(w)) r.Tags.Add(w);
        }
        if (kinds.Contains("texture")) r.Tags.Add("Textures");
        if (kinds.Contains("model")) r.Tags.Add("Models");
        if (kinds.Contains("audio")) r.Tags.Add("Audio");
        if (kinds.Contains("text")) r.Tags.Add("Text");
        if (exe) r.Tags.Add("Executable");
        r.SuggestedName = Path.GetFileName(r.ModDir.TrimEnd('\\', '/'));
        var parts = new List<string>();
        foreach (var f in r.Carried.Take(6)) parts.Add($"{f.Area}: {f.Summary}");
        if (exe) parts.Add("game executable: " + string.Join(", ", r.Exe!.Known.Select(m => m.Name).Concat(r.Exe.Other.Count > 0 ? new[] { $"{r.Exe.Other.Count} other word(s)" } : Array.Empty<string>())));
        r.SuggestedDescription = "Made from a modified game folder. Changes " + string.Join("; ", parts) + ".";
    }
}
