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
