using System.Security.Cryptography;
using System.Text;

namespace NB.Core.Project;

/// <summary>One mod of an edition's recipe, identified by the SHA-256 of its .nbpatch file.</summary>
public sealed record RecipeMod(string Id, string Name, string Version, string Sha256, long Size);

/// <summary>
/// Editions as recipes: an ordered list of mods (.nbpatch files) applied to the player's own game.
///
/// Mods are per-file differences against the original game. When several mods change the same bundle, their changes are
/// combined asset by asset (<see cref="ModMerge"/>); two mods changing the same asset differently is a conflict.
/// World edits (<see cref="PatchPackage.PatchManifest.Ops"/>) are replayed after every mod's files, in recipe order.
/// Executable mods are lists of word changes and are merged: they are written into default.xex together after the files,
/// and a word two mods change must get the same value from both.
/// </summary>
public static class ModStack
{
    public sealed record Mod(string Path, PatchPackage.PatchManifest Manifest, string Sha256, long Size)
    {
        public string Id => PatchPackage.IdOf(Manifest);
        public RecipeMod Entry => new(Id, Manifest.Name, Manifest.Version, Sha256, Size);
    }

    public static Mod Load(string path) =>
        new(path, PatchPackage.ReadManifest(path), PatchPackage.FileSha(path), new FileInfo(path).Length);

    /// <summary>Short key of an ordered recipe (same mods in the same order = same key); "" for the unmodified game.</summary>
    public static string Key(IEnumerable<string> sha256s)
    {
        var list = sha256s.ToList();
        return list.Count == 0 ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", list)))).ToLowerInvariant()[..16];
    }

    /// <summary>Everything that stops these mods from being applied together, in this order (empty = fine).</summary>
    public static List<string> Problems(IReadOnlyList<Mod> mods)
    {
        var res = new List<string>();
        var seen = new Dictionary<string, Mod>(StringComparer.OrdinalIgnoreCase);
        var words = new Dictionary<uint, (uint Value, Mod Mod, string ExeMod)>();
        string N(Mod m) => $"\"{m.Manifest.Name}\"";
        foreach (var m in mods)
        {
            if (seen.TryGetValue(m.Id, out var twice)) { res.Add($"{N(m)} is in the list twice ({twice.Manifest.Version} and {m.Manifest.Version})."); continue; }
            foreach (var r in m.Manifest.Requires.Where(r => !seen.ContainsKey(r)))
                res.Add(mods.Any(x => x.Id.Equals(r, StringComparison.OrdinalIgnoreCase))
                    ? $"{N(m)} needs \"{r}\" before it in the list." : $"{N(m)} needs the mod \"{r}\".");
            foreach (var other in seen.Values)
                if (m.Manifest.Conflicts.Contains(other.Id, StringComparer.OrdinalIgnoreCase) || other.Manifest.Conflicts.Contains(m.Id, StringComparer.OrdinalIgnoreCase))
                    res.Add($"{N(m)} and {N(other)} cannot be combined (their authors say so).");
            if (m.Manifest.Format > PatchPackage.CurrentFormat) res.Add($"{N(m)} needs a newer version of this app.");
            foreach (var op in m.Manifest.Ops)
                if (WorldOps.Problem(op) is { } bad) { res.Add($"{N(m)}: {bad}."); break; }
            foreach (var e in m.Manifest.ExeMods)
                foreach (var w in e.Words)
                {
                    if (words.TryGetValue(w[0], out var had) && had.Value != w[2] && had.Mod != m)
                        res.Add($"{N(had.Mod)} ({had.ExeMod}) and {N(m)} ({e.Id}) change the game executable at {w[0]:X8} differently.");
                    else words[w[0]] = (w[2], m, e.Id);
                }
            seen[m.Id] = m;
        }
        return res.Distinct().ToList();
    }

    /// <summary>Files that several of the mods change: combined asset by asset when the edition is built (shown as a note).</summary>
    public static List<string> Notes(IReadOnlyList<Mod> mods) =>
        mods.SelectMany(m => m.Manifest.Files.Where(f => f.Kind != "xexmods").Select(f => (f.Path, m)))
            .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
            .Select(g => $"{string.Join(" and ", g.Select(x => $"\"{x.m.Manifest.Name}\""))} both change {g.Key}: their changes are combined asset by asset.")
            .ToList();

    /// <summary>
    /// Applies the mods to <paramref name="gameDir"/> (an unmodified game copy) in order. Throws with every problem when
    /// they cannot be combined. Returns the number of files written.
    /// </summary>
    public static int Apply(IReadOnlyList<Mod> mods, string gameDir, IProgress<(string Text, double Fraction)>? progress = null, Action<string>? log = null)
    {
        var problems = Problems(mods);
        if (problems.Count > 0) throw new InvalidOperationException(string.Join("\n", problems));
        if (mods.Count == 1)
            return PatchPackage.Apply(mods[0].Path, gameDir, null, log,
                new Progress<(string File, double Fraction)>(p => progress?.Report(($"{mods[0].Manifest.Name}: {p.File}", p.Fraction))));
        int n = 0;
        // files several mods change: merged from the original before anything is written
        var shared = mods.SelectMany(m => m.Manifest.Files.Where(f => f.Kind != "xexmods").Select(f => (F: f, M: m)))
            .GroupBy(x => x.F.Path, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList();
        var merged = new List<(string Path, byte[] Data)>();
        var conflicts = new List<string>();
        foreach (var g in shared)
        {
            progress?.Report(($"Combining {g.Key}", 0));
            var p = Path.Combine(gameDir, g.Key.Replace('/', Path.DirectorySeparatorChar));
            byte[]? orig = File.Exists(p) ? File.ReadAllBytes(p) : null;
            try
            {
                var variants = g.Select(x => new ModMerge.Variant(x.M.Manifest.Name, ModMerge.Target(x.M.Path, x.F, orig ?? Array.Empty<byte>()))).ToList();
                merged.Add((g.Key, ModMerge.Merge(g.Key, orig, variants)));
                log?.Invoke($"  {g.Key}: changes of {string.Join(" + ", g.Select(x => x.M.Manifest.Name))} combined");
            }
            catch (ModMerge.MergeException e) { conflicts.AddRange(e.Conflicts); }
        }
        if (conflicts.Count > 0) throw new InvalidOperationException("These mods change the same things:" + Environment.NewLine + string.Join(Environment.NewLine, conflicts));
        var skip = shared.Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < mods.Count; i++)
        {
            var m = mods[i];
            n += PatchPackage.Apply(m.Path, gameDir, null, log,
                new Progress<(string File, double Fraction)>(p => progress?.Report(($"{m.Manifest.Name}: {p.File}", (i + p.Fraction) / mods.Count))),
                withoutExecutable: true, withoutOps: true, skipFiles: skip);
        }
        foreach (var (path, data) in merged)
        {
            PatchPackage.WriteFile(gameDir, path, data, string.Join(" + ", mods.Select(m => m.Manifest.Name)), log);
            n++;
        }
        // world edits, in recipe order, on top of every mod's files
        foreach (var m in mods.Where(m => m.Manifest.Ops.Count > 0))
        {
            progress?.Report(($"{m.Manifest.Name}: world edits", 1));
            n += PatchPackage.ApplyOps(m.Manifest, gameDir, log);
        }
        progress?.Report(("Executable mods", 1));
        n += PatchPackage.ApplyExeMods(gameDir, mods.Select(m => m.Manifest).ToList(), string.Join(" + ", mods.Select(m => m.Manifest.Name)), log);
        return n;
    }

    /// <summary>Paths (relative, '/' separated) of every game file the mods change.</summary>
    public static HashSet<string> ChangedFiles(IEnumerable<Mod> mods) =>
        mods.SelectMany(m => m.Manifest.Files.Select(f => f.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
}
