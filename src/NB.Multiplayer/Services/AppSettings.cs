using System.IO;
using System.Text.Json;

namespace NB.Multiplayer.Services;

/// <summary>
/// Settings and data folder of NB Multiplayer: %LOCALAPPDATA%\NB-Multiplayer (settings.json, data\ = Xenia profile and
/// saves, editions\ = patched game copies). The same file the first PowerShell launcher used, so players keep their
/// game folder, name and profile when they switch to this app.
/// </summary>
public sealed class AppSettings
{
    public string GameDir { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public long Instance { get; set; }
    public string LastJoin { get; set; } = "";
    public string Edition { get; set; } = Editions.VanillaName;
    public bool CheckForUpdates { get; set; } = true;
    public string SkippedVersion { get; set; } = "";
    public string HostAddress { get; set; } = "";
    public bool ShortcutOffered { get; set; }
    /// <summary>The player's own NBModStudio.exe ("" = the copy NB Multiplayer installs).</summary>
    public string StudioPath { get; set; } = "";
    /// <summary>Where new NB Studio projects are created ("" = Documents\NB Studio Projects).</summary>
    public string ProjectsDir { get; set; } = "";
    /// <summary>Put the bundled all-unlocked save into the profile before every game (Settings; see Saves).</summary>
    public bool UseAllUnlockedSave { get; set; }
    /// <summary>Showdown Town co-op: the time of day the host's room plays in (0 = random each session, 1..4 = morning..night).</summary>
    public int CoopTimeOfDay { get; set; }

    /// <summary>%LOCALAPPDATA%\NB-Multiplayer, or NB_MP_ROOT (tests: a separate settings/profile folder).</summary>
    public static string Root => Environment.GetEnvironmentVariable("NB_MP_ROOT") is { Length: > 0 } r ? r
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NB-Multiplayer");
    public static string DataDir => Path.Combine(Root, "data");
    public static string EditionsDir => Path.Combine(Root, "editions");

    /// <summary>Edition folders on other drives (next to the game folder), so the editions there are found again.</summary>
    public List<string> EditionRoots { get; set; } = new();

    static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Where editions of <paramref name="gameDir"/> are built. Editions are hard links to the game's files, which only works
    /// on the same drive: when the game is on another drive than %LOCALAPPDATA%, every edition would be a full copy of the
    /// game (6.6 GB). Then they go next to the game folder instead ("NB-Multiplayer Editions" beside it), if that is
    /// writable.
    /// </summary>
    public string EditionsDirFor(string gameDir)
    {
        if (string.IsNullOrWhiteSpace(gameDir) || SameVolume(gameDir, Root)) return EditionsDir;
        var full = Path.GetFullPath(gameDir).TrimEnd('\\', '/');
        var parent = Path.GetDirectoryName(full) ?? Path.GetPathRoot(full)!;
        var dir = Path.Combine(parent, "NB-Multiplayer Editions");
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-test");
            File.WriteAllText(probe, "ok"); File.Delete(probe);
            if (!EditionRoots.Contains(dir, StringComparer.OrdinalIgnoreCase)) { EditionRoots.Add(dir); Save(); }
            return dir;
        }
        catch (Exception) { return EditionsDir; }   // not writable: copies in %LOCALAPPDATA% as before
    }

    /// <summary>Every folder that may hold editions.</summary>
    public IEnumerable<string> AllEditionDirs() =>
        new[] { EditionsDir }.Concat(EditionRoots).Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists);
    static string FilePath => Path.Combine(Root, "settings.json");

    public static AppSettings Load()
    {
        AppSettings s = new();
        try
        {
            if (File.Exists(FilePath))
                s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch (Exception) { }
        if (s.Instance == 0) s.Instance = Random.Shared.NextInt64(1000, 2_000_000_000);
        return s;
    }

    public void Save()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static bool IsGameDir(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "default.xex")) && Directory.Exists(Path.Combine(dir, "Bundle"));

    public static bool IsValidName(string? n) =>
        n != null && System.Text.RegularExpressions.Regex.IsMatch(n, "^[A-Za-z][A-Za-z0-9 ]{0,14}$");
}
