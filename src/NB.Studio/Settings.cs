using System.Text.Json;

namespace NB.Studio;

public sealed class Settings
{
    public string? LastWorkspace { get; set; }
    public string? XeniaPath { get; set; }
    /// <summary>Open <see cref="LastWorkspace"/> at startup instead of showing the start page.</summary>
    public bool AutoOpenLast { get; set; }
    /// <summary>The first-start "would you like a tour?" question was asked.</summary>
    public bool TourOffered { get; set; }
    /// <summary>The tour's second part (the editor) runs when the next workspace opens.</summary>
    public bool TourPending { get; set; }
    /// <summary>Per workspace (folder): the world scene last open in the 3D view ("bundle|act bundle", hex), reopened
    /// when the workspace opens.</summary>
    public Dictionary<string, string> LastWorlds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Executable (After-Party) mods ticked in every NEW workspace (File > Settings > Mods for new workspaces);
    /// null = the recommended set <see cref="NB.Core.Mods.ExePatches.RecommendedForNewWorkspaces"/>.</summary>
    public List<string>? NewWorkspaceMods { get; set; }
    public IReadOnlyList<string> NewWorkspaceModsOrDefault => NewWorkspaceMods ?? NB.Core.Mods.ExePatches.RecommendedForNewWorkspaces;

    /// <summary>Test in Xenia (F5): start every test with an empty test save, blueprints included. Off (default): vehicles
    /// saved in Mumbo's garage during a test are there in the next test of the same workspace.</summary>
    public bool QuickTestFreshSave { get; set; }

    /// <summary>How many steps Ctrl+Z can go back.</summary>
    public int UndoSteps { get; set; } = 100;
    /// <summary>3D view: S scales the selection (Blender style). Off: S always flies backwards.</summary>
    public bool SScales { get; set; } = true;
    /// <summary>Workspaces opened most recently first (File > Open Recent).</summary>
    public List<string> RecentWorkspaces { get; set; } = new();
    public const int RecentMax = 12;
    /// <summary>The recent list was started from the workspaces opened before it existed.</summary>
    public bool RecentSeeded { get; set; }

    public void AddRecent(string root)
    {
        root = Norm(root);
        RecentWorkspaces.RemoveAll(r => string.Equals(Norm(r), root, StringComparison.OrdinalIgnoreCase));
        RecentWorkspaces.Insert(0, root);
        if (RecentWorkspaces.Count > RecentMax) RecentWorkspaces.RemoveRange(RecentMax, RecentWorkspaces.Count - RecentMax);
    }

    static string Norm(string p) { try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)); } catch (Exception) { return p; } }

    static string PathOf => Path.Combine(NB.Core.Project.ProjectRegistry.DataDir, "settings.json");

    public static Settings Load()
    {
        try { if (File.Exists(PathOf)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(PathOf)) ?? new(); } catch { }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathOf)!);
        File.WriteAllText(PathOf, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
