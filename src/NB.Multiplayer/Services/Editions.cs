using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using NB.Core.Project;

namespace NB.Multiplayer.Services;

/// <summary>A playable version of the game: the player's own game ("Vanilla") or that game with an .nbpatch applied.</summary>
public sealed class Edition
{
    public string Name { get; set; } = "";
    public string GameDir { get; set; } = "";
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
    [System.Text.Json.Serialization.JsonIgnore] public bool IsVanilla => PatchName.Length == 0;
    [System.Text.Json.Serialization.JsonIgnore] public string Subtitle => IsVanilla ? "The game as released - plays with everyone on Vanilla"
        : (IsCoop ? "Showdown Town co-op - " : "") + $"{PatchName} {PatchVersion}" + (Author.Length > 0 ? $" by {Author}" : "");
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
        if (!Directory.Exists(AppSettings.EditionsDir)) return list;
        foreach (var d in Directory.GetDirectories(AppSettings.EditionsDir))
        {
            var f = Path.Combine(d, "edition.json");
            if (!File.Exists(f)) continue;
            try
            {
                var e = JsonSerializer.Deserialize<Edition>(File.ReadAllText(f));
                if (e != null && AppSettings.IsGameDir(e.GameDir)) list.Add(e);
            }
            catch (Exception) { }
        }
        return list;
    }

    public static Edition? Find(AppSettings s, string name) =>
        List(s).FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    static string Safe(string n) => string.Concat(n.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' ? c : '_')).Trim();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    /// <summary>Builds an edition from <paramref name="patchPath"/> on top of the player's game. Returns it.</summary>
    public static Edition Create(AppSettings s, string patchPath, IProgress<(string Text, double Fraction)> progress)
    {
        if (!AppSettings.IsGameDir(s.GameDir)) throw new InvalidOperationException("Choose your game folder first (Settings).");
        var man = PatchPackage.ReadManifest(patchPath);
        string name = man.Name.Length > 0 ? man.Name : Path.GetFileNameWithoutExtension(patchPath);
        if (string.Equals(name, VanillaName, StringComparison.OrdinalIgnoreCase)) name += " (patch)";
        var root = Path.Combine(AppSettings.EditionsDir, Safe(name));
        var game = Path.Combine(root, "game");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(game);

        var changed = new HashSet<string>(man.Files.Select(f => f.Path.Replace('/', '\\')), StringComparer.OrdinalIgnoreCase);
        var files = Directory.GetFiles(s.GameDir, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(PatchPackage.BackupDirName)).ToList();
        bool sameVolume = string.Equals(Path.GetPathRoot(Path.GetFullPath(s.GameDir)), Path.GetPathRoot(Path.GetFullPath(game)), StringComparison.OrdinalIgnoreCase);
        for (int i = 0; i < files.Count; i++)
        {
            var rel = Path.GetRelativePath(s.GameDir, files[i]);
            var dst = Path.Combine(game, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            progress.Report(($"Preparing game files ({i + 1}/{files.Count})", 0.4 * i / files.Count));
            // patched files are real copies; everything else a hard link (or a copy on another drive)
            if (changed.Contains(rel) || !sameVolume || !CreateHardLink(dst, files[i], IntPtr.Zero))
            {
                File.Copy(files[i], dst, true);
                File.SetAttributes(dst, FileAttributes.Normal);
            }
        }
        progress.Report(($"Applying {name}", 0.45));
        PatchPackage.Apply(patchPath, game, null, null,
            new Progress<(string File, double Fraction)>(p => progress.Report(($"Applying {name}: {p.File}", 0.45 + 0.55 * p.Fraction))));
        var e = new Edition
        {
            Name = name, GameDir = game, PatchName = name, PatchVersion = man.Version, Author = man.Author,
            Description = man.Description, Created = DateTime.Now,
            Mode = man.Extra.GetValueOrDefault("mode", ""), PuppetBlueprint = man.Extra.GetValueOrDefault("puppetBlueprint", ""),
            ParkSpot = man.Extra.GetValueOrDefault("parkSpot", ""),
        };
        File.WriteAllText(Path.Combine(root, "edition.json"), JsonSerializer.Serialize(e, new JsonSerializerOptions { WriteIndented = true }));
        File.Copy(patchPath, Path.Combine(root, Path.GetFileName(patchPath)), true);
        progress.Report(("Done", 1));
        return e;
    }

    public static void Delete(Edition e)
    {
        if (e.IsVanilla) return;
        var root = Path.GetDirectoryName(e.GameDir)!;
        if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(AppSettings.EditionsDir), StringComparison.OrdinalIgnoreCase)) return;
        // Hard links share the original's (read-only) attributes; clearing them would change the player's game
        // folder, so each link is deleted with "ignore read-only" instead.
        foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) DeleteIgnoringReadOnly(f);
        Directory.Delete(root, true);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle h, int infoClass, ref uint info, uint size);

    static void DeleteIgnoringReadOnly(string path)
    {
        const uint DELETE = 0x00010000, SHARE_ALL = 7, OPEN_EXISTING = 3, OPEN_REPARSE_POINT = 0x00200000;
        const int FileDispositionInfoEx = 21;
        uint flags = 0x1 | 0x2 | 0x10;   // DELETE | POSIX_SEMANTICS | IGNORE_READONLY_ATTRIBUTE
        using var h = CreateFileW(path, DELETE, SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, OPEN_REPARSE_POINT, IntPtr.Zero);
        if (h.IsInvalid || !SetFileInformationByHandle(h, FileDispositionInfoEx, ref flags, 4))
            throw new IOException($"cannot delete {path} (error {Marshal.GetLastWin32Error()})");
    }
}
