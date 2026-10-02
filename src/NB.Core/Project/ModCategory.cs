namespace NB.Core.Project;

/// <summary>What kind of mod a .nbpatch is (like the project types of a mod site): shown as a badge and used to filter.</summary>
public sealed record ModCategory(string Id, string Name, string Description);

public static class ModCategories
{
    public static readonly ModCategory Map = new("map", "Maps & worlds", "Changes or adds a level: its layout, objects, AI vehicles and challenges.");
    public static readonly ModCategory Parts = new("parts", "Vehicle parts", "New or changed vehicle parts for Mumbo's Motors.");
    public static readonly ModCategory Gameplay = new("gameplay", "Gameplay & scripts", "Acts, rules, AI behaviour, story and menus.");
    public static readonly ModCategory Visual = new("visual", "Textures & visuals", "Textures, models and looks.");
    public static readonly ModCategory Audio = new("audio", "Sounds & music", "Music, voices and sound effects.");
    public static readonly ModCategory Tweak = new("tweak", "Tweaks", "Small changes to the game itself, such as a bigger world or more vehicle parts.");
    public static readonly ModCategory Coop = new("coop", "Co-op & multiplayer", "What Showdown Town co-op and other multiplayer modes need.");

    public static readonly IReadOnlyList<ModCategory> All = new[] { Map, Parts, Gameplay, Visual, Audio, Tweak, Coop };

    public static ModCategory? Find(string? id) => All.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The mod's category: the one its author chose, otherwise a guess from what it changes.</summary>
    public static ModCategory Of(PatchPackage.PatchManifest m) => Find(m.Category) ?? Suggest(m);

    /// <summary>
    /// A guess from the files a mod changes (for patches made before categories, and as the default in NB Studio).
    /// The well-known bundles: 4f/234cec = the Showdown Town world, 685374 / 4e967e = the shared bundle with the
    /// vehicle-parts library (it also holds scripts), default.xex alone = executable tweaks.
    /// </summary>
    public static ModCategory Suggest(PatchPackage.PatchManifest m)
    {
        if (m.Multiplayer == "coop" || m.Extra.GetValueOrDefault("mode") == "coop") return Coop;
        var files = m.Files.Where(f => f.Kind != "xexmods").Select(f => f.Path.Replace('\\', '/').ToLowerInvariant()).ToList();
        if (files.Count == 0) return Tweak;
        if (files.Any(f => f.EndsWith("/234cec"))) return Map;
        if (files.Any(f => f.EndsWith("/685374") || f.EndsWith("/4e967e"))) return Parts;
        if (files.Any(f => f.EndsWith(".xwb") || f.Contains("audio") || f.Contains("sound"))) return Audio;
        if (files.All(f => f.Contains("loctext") || f.EndsWith(".xml"))) return Gameplay;
        return Visual;
    }
}

/// <summary>
/// NB Studio projects (workspaces) known on this PC, shared by NB Studio (adds every workspace it opens or creates) and
/// NB Multiplayer (Projects page): %APPDATA%\NBModTool\projects.json.
/// </summary>
public static class ProjectRegistry
{
    public sealed class Entry
    {
        public string Path { get; set; } = "";
        public DateTime LastOpened { get; set; }
    }

    public static string FilePath => System.IO.Path.Combine(DataDir, "projects.json");

    /// <summary>NB Studio's settings folder (%APPDATA%/NBModTool); the NB_STUDIO_DATA environment variable points it
    /// elsewhere, so scripted tests and side-by-side runs never touch the player's own settings.</summary>
    public static string DataDir => Environment.GetEnvironmentVariable("NB_STUDIO_DATA") is { Length: > 0 } d ? d
        : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NBModTool");

    public static List<Entry> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return System.Text.Json.JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception) { }
        return new();
    }

    static void Save(List<Entry> list)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(list, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception) { /* the list is a convenience: never fail the caller */ }
    }

    static string Norm(string p) => System.IO.Path.GetFullPath(p).TrimEnd('\\', '/');

    /// <summary>Adds the workspace (or marks it as just opened, or as opened at <paramref name="when"/>).</summary>
    public static void Touch(string root, DateTime? when = null)
    {
        var list = Load();
        list.RemoveAll(e => string.Equals(Norm(e.Path), Norm(root), StringComparison.OrdinalIgnoreCase));
        list.Add(new Entry { Path = Norm(root), LastOpened = when ?? DateTime.Now });
        Save(list.OrderByDescending(e => e.LastOpened).ToList());
    }

    public static void Remove(string root)
    {
        var list = Load();
        if (list.RemoveAll(e => string.Equals(Norm(e.Path), Norm(root), StringComparison.OrdinalIgnoreCase)) > 0) Save(list);
    }
}
