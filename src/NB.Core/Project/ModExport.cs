using System.IO.Compression;
using System.Text;

namespace NB.Core.Project;

/// <summary>
/// Exports mods of a mod library (built-in tweaks, bundled mods such as ULTRA Parts, downloaded mods) as ONE standalone
/// .nbpatch that installs on any other copy of the game: NB Multiplayer or NB Studio of another PC, NB.Cli, a game folder
/// played in Xenia, or the "mods" folder of a reNut build that loads .nbpatch files.
/// <para>
/// One mod without world edits is exported as it is (it already is a self-contained patch made against the retail game,
/// and the same bytes keep it the same mod for rooms). Anything else is "flattened": the mods are applied in order to a
/// hard-linked copy of the clean game, exactly as an edition is built (asset-level merge of shared files, world edits,
/// executable mods), and the result is diffed against the clean game. The export then has only file deltas, new files
/// and executable mods: no world edits (ops) to replay and no merging left for the receiver, so it also works where
/// those are not supported (a reNut mods folder applies file changes only and never combines two mods' changes to one file).
/// </para>
/// </summary>
public static class ModExport
{
    public sealed record Result(string Path, PatchPackage.PatchManifest Manifest, bool Flattened, List<string> Notes);

    /// <summary>Extra keys written into exported manifests (Extra is ignored by readers that do not know them).</summary>
    public const string ExtraSources = "exportedFrom", ExtraNeedsExe = "needsExecutable";

    /// <summary>
    /// Exports <paramref name="mods"/> (in recipe order) to <paramref name="outPath"/>. <paramref name="cleanGame"/> is an
    /// unmodified game folder (the player's own game); <paramref name="workDir"/> a scratch folder on the same drive (hard
    /// links; it is created and removed again).
    /// </summary>
    public static Result Export(IReadOnlyList<ModStack.Mod> mods, string cleanGame, string outPath, string workDir, string? name = null,
        IProgress<(string Text, double Fraction)>? progress = null, Action<string>? log = null)
    {
        if (mods.Count == 0) throw new InvalidOperationException("Choose at least one mod.");
        var problems = ModStack.Problems(mods);
        if (problems.Count > 0) throw new InvalidOperationException(string.Join("\n", problems));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outPath))!);
        name = string.IsNullOrWhiteSpace(name) ? DefaultName(mods) : name.Trim();

        if (mods.Count == 1 && mods[0].Manifest.Ops.Count == 0)
        {
            progress?.Report(($"Copying {mods[0].Manifest.Name}", 0.5));
            if (!System.IO.Path.GetFullPath(mods[0].Path).Equals(System.IO.Path.GetFullPath(outPath), StringComparison.OrdinalIgnoreCase))
                File.Copy(mods[0].Path, outPath, true);
            progress?.Report(("Done", 1));
            return new Result(outPath, mods[0].Manifest, false, Notes(mods[0].Manifest));
        }

        if (!File.Exists(System.IO.Path.Combine(cleanGame, "default.xex"))) throw new InvalidOperationException("Choose your game folder first.");
        var notClean = GameDiff.NotRetail(cleanGame, ModStack.ChangedFiles(mods).Append("default.xex"));
        if (notClean.Count > 0)
            throw new InvalidOperationException("Your game folder is not the unmodified game (" + string.Join(", ", notClean.Take(4)) +
                                                "): exports are made against the original files.");
        var tmp = System.IO.Path.Combine(workDir, "export-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // the files the mods name are real copies, the rest hard links (world edits make their own files private)
            var copies = new HashSet<string>(ModStack.ChangedFiles(mods).Select(f => f.Replace('/', System.IO.Path.DirectorySeparatorChar)), StringComparer.OrdinalIgnoreCase);
            IO.FileLinks.LinkCopy(cleanGame, tmp, copies, new Progress<(string Text, double Fraction)>(p => progress?.Report((p.Text, 0.25 * p.Fraction))));
            progress?.Report(("Applying the mods", 0.25));
            ModStack.Apply(mods, tmp, new Progress<(string Text, double Fraction)>(p => progress?.Report(($"Applying {p.Text}", 0.25 + 0.35 * p.Fraction))), log);
            progress?.Report(("Finding the changed files", 0.6));
            var changed = ChangedAgainstRetail(tmp, cleanGame);
            log?.Invoke($"changed: {string.Join(", ", changed.Select(c => c.Path))}");
            var rep = GameDiff.Analyze(tmp, cleanGame, null, default, changed);
            if (rep.Exe?.Problem is { } xp) throw new InvalidDataException("default.xex: " + xp);
            progress?.Report(("Writing the patch", 0.7));
            var man = PatchPackage.BuildFromFolders(rep, outPath, name, Author(mods), Description(mods, name),
                new Progress<(string File, double Fraction)>(p => progress?.Report(($"Writing {p.File}", 0.7 + 0.28 * p.Fraction))),
                m => Configure(m, mods, name));
            WriteReadme(outPath, man, mods);
            progress?.Report(("Done", 1));
            return new Result(outPath, man, true, Notes(man));
        }
        finally
        {
            try { if (Directory.Exists(tmp)) DeleteTree(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>"A + B" (tweaks last), like an edition's default name.</summary>
    public static string DefaultName(IReadOnlyList<ModStack.Mod> mods)
    {
        var names = mods.Select(m => m.Manifest.Name.Length > 0 ? m.Manifest.Name : m.Id).ToList();
        var joined = string.Join(" + ", names);
        return names.Count == 1 || joined.Length <= 60 ? joined : $"{names[0]} + {names.Count - 1} more";
    }

    /// <summary>What the receiving side has to know (shown after an export and written into README.txt).</summary>
    public static List<string> Notes(PatchPackage.PatchManifest m)
    {
        var n = new List<string>();
        if (m.ExeMods.Count > 0)
            n.Add("Game-code tweaks (" + string.Join(", ", m.ExeMods.Select(e => e.Name)) + ") are written into default.xex when the patch is " +
                  "applied to a game folder: they work in Xenia, on RGH/JTAG consoles and in reNut built with NB's mod layer (NB Multiplayer's " +
                  "\"Launch with reNut\"). A reNut mods folder (reNut releases that load .nbpatch files) applies only the game files and skips them.");
        if (m.Ops.Count > 0)
            n.Add($"{m.Ops.Count} world edit(s) are replayed by NB Multiplayer / NB Studio / NB.Cli; a reNut mods folder does not apply them " +
                  "(export it together with another mod, or on its own from NB Multiplayer, to get them as file changes).");
        if (m.Multiplayer == "coop" || m.Extra.GetValueOrDefault("mode") == "coop")
            n.Add("Co-op needs NB Multiplayer on every PC (it syncs the players); the patch alone only prepares the game.");
        if (m.Files.Count(f => f.Kind != "xexmods") > 0 && m.ExeMods.Count == 0)
            n.Add("Only game files: works everywhere, including a reNut mods folder.");
        return n;
    }

    static string Author(IReadOnlyList<ModStack.Mod> mods)
    {
        var authors = mods.Select(m => m.Manifest.Author).Where(a => a.Length > 0).Distinct().ToList();
        return authors.Count == 1 ? authors[0] : "NB Multiplayer export";
    }

    static string Description(IReadOnlyList<ModStack.Mod> mods, string name)
    {
        if (mods.Count == 1) return mods[0].Manifest.Description;
        var sb = new StringBuilder($"{name}: ");
        sb.Append(string.Join(" ", mods.Select(m => $"[{m.Manifest.Name}] {m.Manifest.Description}".Trim())));
        return sb.ToString();
    }

    static void Configure(PatchPackage.PatchManifest man, IReadOnlyList<ModStack.Mod> mods, string name)
    {
        var ids = mods.Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        man.Id = mods.Count == 1 ? mods[0].Id : PatchPackage.Slug(name);
        man.Version = mods.Count == 1 ? mods[0].Manifest.Version : "1";
        var named = mods.Where(m => !(m.Id.StartsWith("tweak-") && m.Manifest.Category == "tweak")).ToList();
        man.Category = (named.Count > 0 ? named : mods.ToList()).Select(m => ModCategories.Of(m.Manifest).Id)
            .OrderBy(c => c == "coop" ? 0 : c == "map" ? 1 : c == "parts" ? 2 : 3).First();
        man.Multiplayer = mods.Any(m => m.Manifest.Multiplayer == "coop" || m.Manifest.Extra.GetValueOrDefault("mode") == "coop") ? "coop"
            : mods.All(m => m.Manifest.Multiplayer == "cosmetic") ? "cosmetic" : "world";
        man.Tags = mods.SelectMany(m => m.Manifest.Tags).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
        foreach (var m in mods) foreach (var kv in m.Manifest.Extra) man.Extra[kv.Key] = kv.Value;   // co-op settings etc., later mods win
        man.Requires = mods.SelectMany(m => m.Manifest.Requires).Where(r => !ids.Contains(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        man.Conflicts = mods.SelectMany(m => m.Manifest.Conflicts).Where(c => !ids.Contains(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // the executable mods by the sources' own ids (the diff alone cannot name generated ones such as vehicle-part-limit:2000);
        // the same words, so default.xex bakes to the same bytes
        var exe = mods.SelectMany(m => m.Manifest.ExeMods).GroupBy(e => e.Id).Select(g => g.First()).ToList();
        if (exe.Count > 0 && man.ExeMods.Count > 0 && SameWords(exe, man.ExeMods)) man.ExeMods = exe;
        man.Extra[ExtraSources] = string.Join("; ", mods.Select(m => $"{m.Id} {m.Manifest.Version}"));
        if (man.ExeMods.Count > 0) man.Extra[ExtraNeedsExe] = string.Join(", ", man.ExeMods.Select(e => e.Id));
    }

    static bool SameWords(List<PatchPackage.PatchExeMod> a, List<PatchPackage.PatchExeMod> b)
    {
        static Dictionary<uint, uint> W(List<PatchPackage.PatchExeMod> l)
        {
            var d = new Dictionary<uint, uint>();
            foreach (var w in l.SelectMany(e => e.Words)) if (w[1] != w[2]) d[w[0]] = w[2];
            return d;
        }
        var x = W(a); var y = W(b);
        return x.Count == y.Count && x.All(kv => y.TryGetValue(kv.Key, out var v) && v == kv.Value);
    }

    /// <summary>Game files of <paramref name="dir"/> that differ from the retail game. Hard links to the clean game are skipped
    /// without reading them (they are the clean files).</summary>
    static List<GameDiff.FolderState> ChangedAgainstRetail(string dir, string cleanGame)
    {
        var res = new List<GameDiff.FolderState>();
        foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        {
            var rel = System.IO.Path.GetRelativePath(dir, f).Replace('\\', '/');
            if (GameDiff.IsToolFile(rel)) continue;
            if (!GameDiff.Retail.TryGetValue(rel, out var fp)) { res.Add(new(rel, "new")); continue; }
            if (IO.FileLinks.LinkCount(f) > 1 && File.Exists(System.IO.Path.Combine(cleanGame, rel))) continue;   // a link to the clean game
            var len = new FileInfo(f).Length;
            if (len == fp.Size && GameDiff.Sha(f) == fp.Sha256) continue;
            res.Add(new(rel, "changed"));
        }
        return res;
    }

    static void WriteReadme(string patch, PatchPackage.PatchManifest man, IReadOnlyList<ModStack.Mod> mods)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{man.Name} (version {man.Version})");
        sb.AppendLine(man.Description);
        sb.AppendLine();
        sb.AppendLine("Exported from NB Multiplayer. Made of: " + string.Join(", ", mods.Select(m => $"{m.Manifest.Name} {m.Manifest.Version}")));
        sb.AppendLine("It contains no game files: only the differences to the original game, which every file is checked against.");
        sb.AppendLine();
        sb.AppendLine("Install it on another copy or PC:");
        sb.AppendLine("  NB Multiplayer: Mods & editions > Add a mod (.nbpatch), tick it, build an edition.");
        sb.AppendLine("  NB Studio:      Build > Apply Patch... on a workspace or a game folder.");
        sb.AppendLine("  NB.Cli:         NB.Cli patch-apply <this file> <copy of the game folder>   (patch-rollback undoes it)");
        sb.AppendLine("  reNut:          copy it into reNut's mods folder (reNut builds that load .nbpatch files) - see the notes.");
        sb.AppendLine();
        foreach (var n in Notes(man)) sb.AppendLine("Note: " + n);
        sb.AppendLine();
        sb.AppendLine($"Files changed: {man.Files.Count}");
        foreach (var f in man.Files) sb.AppendLine($"  {f.Path}  ({f.Kind}, {f.LiteralBytes:N0} new bytes)");
        using var zip = ZipFile.Open(patch, ZipArchiveMode.Update);
        zip.GetEntry("README.txt")?.Delete();
        using var w = new StreamWriter(zip.CreateEntry("README.txt", CompressionLevel.Optimal).Open());
        w.Write(sb.ToString());
    }

    /// <summary>Removes a scratch game copy: every file name is deleted without touching attributes a hard link shares
    /// with the player's game.</summary>
    static void DeleteTree(string root)
    {
        foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) IO.FileLinks.DeleteIgnoringReadOnly(f);
        Directory.Delete(root, true);
    }
}
