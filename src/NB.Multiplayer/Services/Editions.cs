using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using NB.Core.Project;

namespace NB.Multiplayer.Services;

/// <summary>A playable version of the game: the player's own game ("Vanilla") or that game with a recipe of mods applied.</summary>
public sealed class Edition
{
    public string Name { get; set; } = "";
    public string GameDir { get; set; } = "";
    /// <summary>The recipe: mods applied to the player's game, in order (empty = Vanilla).</summary>
    public List<RecipeMod> Mods { get; set; } = new();
    [System.Text.Json.Serialization.JsonIgnore] public string RecipeKey => ModStack.Key(Mods.Select(m => m.Sha256));
    public string PatchName { get; set; } = "";
    public string PatchVersion { get; set; } = "";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime Created { get; set; }
    /// <summary>"coop" = Showdown Town co-op edition (single-player games synced by NB Multiplayer).</summary>
    public string Mode { get; set; } = "";
    public string PuppetBlueprint { get; set; } = "";
    public string ParkSpot { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public bool IsCoop => Mode == "coop";
    [System.Text.Json.Serialization.JsonIgnore] public uint PuppetBlueprintId => uint.TryParse(PuppetBlueprint, System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : 0;
    [System.Text.Json.Serialization.JsonIgnore] public System.Numerics.Vector3 ParkVector
    {
        get
        {
            var p = ParkSpot.Split(',').Select(x => float.TryParse(x, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 0f).ToArray();
            return p.Length == 3 ? new(p[0], p[1], p[2]) : new(0, -200, 0);
        }
    }
    [System.Text.Json.Serialization.JsonIgnore] public bool IsVanilla => PatchName.Length == 0 && Mods.Count == 0;
    [System.Text.Json.Serialization.JsonIgnore] public string Subtitle => IsVanilla ? "The game as released - plays with everyone on Vanilla"
        : (IsCoop ? "Showdown Town co-op - " : "") + (Mods.Count > 0 ? string.Join(" + ", Mods.Select(m => $"{m.Name} {m.Version}")) : $"{PatchName} {PatchVersion}")
          + (Author.Length > 0 ? $" by {Author}" : "");
}

/// <summary>
/// Patched editions live in %LOCALAPPDATA%\NB-Multiplayer\editions\&lt;name&gt;\game. Unchanged game files are hard
/// links to the player's own game (no extra disk space, the original folder is never written); the files a patch
/// changes are copied first and then patched, so the original game stays untouched.
/// </summary>
public static class Editions
{
    public const string VanillaName = "Vanilla";

    public static List<Edition> List(AppSettings s)
    {
        var list = new List<Edition> { new() { Name = VanillaName, GameDir = s.GameDir } };
        foreach (var d in s.AllEditionDirs().SelectMany(Directory.GetDirectories))
        {
            var f = Path.Combine(d, "edition.json");
            if (!File.Exists(f)) continue;
            try
            {
                var e = JsonSerializer.Deserialize<Edition>(File.ReadAllText(f));
                if (e == null || !AppSettings.IsGameDir(e.GameDir)) continue;
                if (e.Mods.Count == 0 && Directory.GetFiles(d, "*.nbpatch").FirstOrDefault() is { } own)
                {
                    // editions from NB Multiplayer 1.1 and older: one patch copied next to the edition -> recipe of that mod
                    e.Mods.Add(ModLibrary.Add(own).Entry);
                    File.WriteAllText(f, JsonSerializer.Serialize(e, JsonOpts));
                    File.Delete(own);
                }
                list.Add(e);
            }
            catch (Exception) { }
        }
        return list;
    }

    public static Edition? Find(AppSettings s, string name) =>
        List(s).FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The edition built from exactly this recipe, if the player has it.</summary>
    public static Edition? FindRecipe(AppSettings s, IEnumerable<RecipeMod> recipe)
    {
        var key = ModStack.Key(recipe.Select(m => m.Sha256));
        return List(s).FirstOrDefault(e => e.RecipeKey == key);
    }

    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>A free edition name: <paramref name="name"/>, or "name (2)", "name (3)", ...</summary>
    public static string FreeName(AppSettings s, string name)
    {
        var taken = List(s).Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Free(string n) => !taken.Contains(n) && !s.AllEditionDirs().Append(s.EditionsDirFor(s.GameDir)).Any(d => Directory.Exists(Path.Combine(d, Safe(n))));
        if (Free(name)) return name;
        for (int i = 2; ; i++) if (Free($"{name} ({i})")) return $"{name} ({i})";
    }

    static string Safe(string n) => string.Concat(n.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' ? c : '_')).Trim();

    /// <summary>Adds <paramref name="patchPath"/> to the mod library and builds an edition of just that mod.</summary>
    public static Edition Create(AppSettings s, string patchPath, IProgress<(string Text, double Fraction)> progress) =>
        Create(s, new[] { ModLibrary.Add(patchPath) }, null, progress);

    /// <summary>
    /// Builds an edition from a recipe of library mods (in order) on top of the player's game. The name defaults to the
    /// mods' names; an edition of the same name is replaced only when it has the same recipe. Returns it.
    /// </summary>
    public static Edition Create(AppSettings s, IReadOnlyList<ModStack.Mod> mods, string? name, IProgress<(string Text, double Fraction)> progress)
    {
        if (!AppSettings.IsGameDir(s.GameDir)) throw new InvalidOperationException("Choose your game folder first (Settings).");
        if (mods.Count == 0) throw new InvalidOperationException("Choose at least one mod.");
        var problems = ModStack.Problems(mods);
        if (problems.Count > 0) throw new InvalidOperationException(string.Join("\n", problems));
        name = (name ?? "").Trim();
        if (name.Length == 0)
        {
            // "Snowy Showdown Town + Co-op": tick-box tweaks are left out of the name, a co-op layer becomes "+ Co-op"
            var named = mods.Where(m => !ModLibrary.IsTweak(m)).ToList();
            if (named.Count == 0) named = mods.ToList();
            var coopLayer = named.Where(m => ModCategories.Of(m.Manifest) == ModCategories.Coop).ToList();
            var rest = named.Except(coopLayer).ToList();
            var names = rest.Select(m => m.Manifest.Name.Length > 0 ? m.Manifest.Name : m.Id).ToList();
            if (coopLayer.Count > 0) { if (names.Count == 0) names.AddRange(coopLayer.Select(m => m.Manifest.Name)); else names.Add("Co-op"); }
            name = names.Count == 1 || string.Join(" + ", names).Length <= 40 ? string.Join(" + ", names) : $"{names[0]} + {names.Count - 1} more";
        }
        if (string.Equals(name, VanillaName, StringComparison.OrdinalIgnoreCase)) name += " (mod)";
        var key = ModStack.Key(mods.Select(m => m.Sha256));
        var existing = Find(s, name);
        if (existing != null && existing.RecipeKey == key) Delete(existing);       // rebuilding the same recipe
        else if (existing != null) name = FreeName(s, name);
        var root = Path.Combine(s.EditionsDirFor(s.GameDir), Safe(name));   // same drive as the game: hard links, no copies
        var game = Path.Combine(root, "game");
        if (Directory.Exists(root)) DeleteTree(root);
        Directory.CreateDirectory(game);

        // patched files are real copies; everything else a hard link to the player's game (or a copy on another drive)
        var changed = new HashSet<string>(ModStack.ChangedFiles(mods).Select(f => f.Replace('/', Path.DirectorySeparatorChar)), StringComparer.OrdinalIgnoreCase);
        NB.Core.IO.FileLinks.LinkCopy(s.GameDir, game, changed, new Progress<(string Text, double Fraction)>(p => progress.Report((p.Text, 0.4 * p.Fraction))));
        progress.Report(($"Applying {name}", 0.45));
        ModStack.Apply(mods, game, new Progress<(string Text, double Fraction)>(p => progress.Report(($"Applying {p.Text}", 0.45 + 0.55 * p.Fraction))));
        // co-op settings come from the mod that provides them (the co-op layer)
        var coop = mods.LastOrDefault(m => m.Manifest.Extra.GetValueOrDefault("mode") == "coop" || m.Manifest.Multiplayer == "coop")?.Manifest;
        var e = new Edition
        {
            Name = name, GameDir = game, Mods = mods.Select(m => m.Entry).ToList(), Created = DateTime.Now,
            PatchName = string.Join(" + ", mods.Select(m => m.Manifest.Name)), PatchVersion = mods.Count == 1 ? mods[0].Manifest.Version : "",
            Author = string.Join(", ", mods.Select(m => m.Manifest.Author).Where(a => a.Length > 0).Distinct()),
            Description = mods.Count == 1 ? mods[0].Manifest.Description : "",
            Mode = coop != null ? "coop" : "", PuppetBlueprint = coop?.Extra.GetValueOrDefault("puppetBlueprint", "") ?? "",
            ParkSpot = coop?.Extra.GetValueOrDefault("parkSpot", "") ?? "",
        };
        File.WriteAllText(Path.Combine(root, "edition.json"), JsonSerializer.Serialize(e, JsonOpts));
        progress.Report(("Done", 1));
        return e;
    }

    /// <summary>Renames an edition (its folder moves; hard links stay links). Returns the renamed edition.</summary>
    public static Edition Rename(AppSettings s, Edition e, string name)
    {
        var oldRoot = Path.GetDirectoryName(e.GameDir)!;
        name = FreeName(s, name);
        var newRoot = Path.Combine(Path.GetDirectoryName(oldRoot)!, Safe(name));
        Directory.Move(oldRoot, newRoot);
        e.Name = name;
        e.GameDir = Path.Combine(newRoot, "game");
        File.WriteAllText(Path.Combine(newRoot, "edition.json"), JsonSerializer.Serialize(e, JsonOpts));
        return e;
    }

    /// <summary>Disk space an edition really uses: its files that are not hard links to the game (bytes).</summary>
    public static long OwnSize(Edition e)
    {
        long n = 0;
        if (e.IsVanilla || !Directory.Exists(e.GameDir)) return 0;
        foreach (var f in Directory.EnumerateFiles(e.GameDir, "*", SearchOption.AllDirectories))
            try { if (NB.Core.IO.FileLinks.LinkCount(f) <= 1) n += new FileInfo(f).Length; } catch (Exception) { }
        return n;
    }

    /// <summary>
    /// Editions that are full copies because they were built on another drive than the game (NB Multiplayer 1.7 and
    /// older put every edition in %LOCALAPPDATA%): rebuilt next to the game as hard links. Returns the bytes freed.
    /// </summary>
    public static long Compact(AppSettings s, IProgress<(string Text, double Fraction)> progress)
    {
        var target = s.EditionsDirFor(s.GameDir);
        var todo = List(s).Where(e => !e.IsVanilla && !Path.GetFullPath(e.GameDir).StartsWith(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)).ToList();
        long freed = 0;
        for (int i = 0; i < todo.Count; i++)
        {
            var e = todo[i];
            var mods = e.Mods.Select(m => ModLibrary.Get(m.Sha256)).ToList();
            if (mods.Any(m => m == null)) continue;                 // a mod is missing: leave this edition alone
            long before = OwnSize(e);
            progress.Report(($"Rebuilding \"{e.Name}\" next to your game ({i + 1}/{todo.Count})", (double)i / todo.Count));
            var name = e.Name;
            Delete(e);
            var built = Create(s, mods!, name, new Progress<(string Text, double Fraction)>(p => progress.Report((p.Text, (i + p.Fraction) / todo.Count))));
            freed += before - OwnSize(built);
        }
        return freed;
    }

    public static void Delete(Edition e)
    {
        if (e.IsVanilla) return;
        var root = Path.GetDirectoryName(e.GameDir)!;
        var parent = Path.GetDirectoryName(Path.GetFullPath(root))!;
        bool ours = string.Equals(parent, Path.GetFullPath(AppSettings.EditionsDir), StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(parent).Equals("NB-Multiplayer Editions", StringComparison.OrdinalIgnoreCase);
        if (!ours || !File.Exists(Path.Combine(root, "edition.json"))) return;   // only folders NB Multiplayer made
        // Hard links share the original's (read-only) attributes; clearing them would change the player's game
        // folder, so each link is deleted with "ignore read-only" instead.
        DeleteTree(root);
    }

    static void DeleteTree(string root)
    {
        foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) DeleteIgnoringReadOnly(f);
        Directory.Delete(root, true);
    }

    static void DeleteIgnoringReadOnly(string path) => NB.Core.IO.FileLinks.DeleteIgnoringReadOnly(path);
}
