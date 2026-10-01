using System.IO;
using NB.Core.Project;

namespace NB.Multiplayer.Services;

/// <summary>
/// The player's mods: every .nbpatch added to NB Multiplayer (or downloaded from a host) is kept once in
/// %LOCALAPPDATA%\NB-Multiplayer\mods\&lt;sha256&gt;.nbpatch. Editions are recipes of these mods.
/// </summary>
public static class ModLibrary
{
    public static string Dir => Path.Combine(AppSettings.Root, "mods");

    static readonly Dictionary<string, ModStack.Mod> _cache = new(StringComparer.OrdinalIgnoreCase);

    public static string PathOf(string sha256) => Path.Combine(Dir, sha256.ToLowerInvariant() + ".nbpatch");

    /// <summary>The library file of this mod, or null when the player does not have it.</summary>
    public static string? Find(string sha256) => File.Exists(PathOf(sha256)) ? PathOf(sha256) : null;

    /// <summary>Copies a .nbpatch into the library (once) and returns it.</summary>
    public static ModStack.Mod Add(string patchPath)
    {
        var m = ModStack.Load(patchPath);
        var dst = PathOf(m.Sha256);
        if (!File.Exists(dst))
        {
            Directory.CreateDirectory(Dir);
            File.Copy(patchPath, dst + ".tmp", true);
            File.Move(dst + ".tmp", dst, true);
        }
        return Get(m.Sha256)!;
    }

    /// <summary>Adds downloaded bytes after checking that they are exactly the mod the recipe names.</summary>
    public static ModStack.Mod AddDownloaded(byte[] data, string sha256)
    {
        if (PatchPackage.Sha(data) != sha256.ToLowerInvariant()) throw new InvalidDataException("the downloaded mod is damaged (SHA-256 does not match)");
        Directory.CreateDirectory(Dir);
        var dst = PathOf(sha256);
        File.WriteAllBytes(dst + ".tmp", data);
        File.Move(dst + ".tmp", dst, true);
        return Get(sha256) ?? throw new InvalidDataException("the downloaded file is not an .nbpatch");
    }

    public static ModStack.Mod? Get(string sha256)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(sha256, out var m)) return m;
            var p = Find(sha256);
            if (p == null) return null;
            try { m = new ModStack.Mod(p, PatchPackage.ReadManifest(p), sha256.ToLowerInvariant(), new FileInfo(p).Length); }
            catch (Exception) { return null; }
            return _cache[sha256] = m;
        }
    }

    /// <summary>Every mod in the library, oldest first.</summary>
    public static List<ModStack.Mod> List()
    {
        if (!Directory.Exists(Dir)) return new();
        return new DirectoryInfo(Dir).GetFiles("*.nbpatch").OrderBy(f => f.CreationTimeUtc)
            .Select(f => Get(Path.GetFileNameWithoutExtension(f.Name))).OfType<ModStack.Mod>().ToList();
    }

    public static void Remove(string sha256)
    {
        lock (_cache) _cache.Remove(sha256);
        var p = Find(sha256);
        if (p != null) File.Delete(p);
    }

    /// <summary>A built-in tweak (made by NB Multiplayer from the player's own default.xex), not a downloaded mod.</summary>
    public static bool IsTweak(ModStack.Mod m) => m.Id.StartsWith("tweak-") && m.Manifest.Author == "NB Studio" && m.Manifest.Category == "tweak";

    /// <summary>
    /// Makes the tick-box tweaks (one small mod per executable tweak, <see cref="NB.Core.Mods.ExePatches.Tweaks"/>) from the
    /// player's default.xex. They are byte-identical on every PC with the retail game, so a host's tweaks never need
    /// downloading. Done once per game executable (mods/tweaks.txt remembers it). Returns how many were made.
    /// </summary>
    public static int EnsureTweaks(string gameDir)
    {
        var xex = Path.Combine(gameDir, "default.xex");
        if (!File.Exists(xex)) return 0;
        var fi = new FileInfo(xex);
        string stamp = $"2|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{string.Join(",", NB.Core.Mods.ExePatches.Tweaks.Select(t => t.Name))}";
        var stampFile = Path.Combine(Dir, "tweaks.txt");
        if (File.Exists(stampFile) && File.ReadAllText(stampFile) == stamp) return 0;
        Directory.CreateDirectory(Dir);
        int n = 0;
        var made = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (mod, name, blurb) in NB.Core.Mods.ExePatches.Tweaks)
        {
            var tmp = Path.Combine(Dir, "tweak.tmp");
            try
            {
                PatchPackage.BuildTweak(xex, mod, name, blurb, tmp);
                made.Add(Add(tmp).Sha256);
                n++;
            }
            catch (Exception) { /* not the retail executable: this tweak does not fit */ }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
        // tweaks of an older NB Multiplayer (other names) are replaced, unless an edition still uses them
        var used = Editions.List(new AppSettings()).SelectMany(e => e.Mods).Select(m => m.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var old in List().Where(m => IsTweak(m) && !made.Contains(m.Sha256) && !used.Contains(m.Sha256)).ToList()) Remove(old.Sha256);
        File.WriteAllText(stampFile, stamp);
        return n;
    }

    /// <summary>"cosmetic" / "world" / "coop" in words for the UI.</summary>
    public static string MultiplayerText(PatchPackage.PatchManifest m) => (m.Multiplayer.Length > 0 ? m.Multiplayer : m.Extra.GetValueOrDefault("mode", "")) switch
    {
        "cosmetic" => "Looks and sounds only",
        "coop" => "Showdown Town co-op",
        _ => "Changes the game world: everyone in a room needs it",
    };
}
