using System.Text.Json;

namespace NB.Studio;

public sealed class Settings
{
    public string? LastWorkspace { get; set; }
    public string? XeniaPath { get; set; }
    /// <summary>Open <see cref="LastWorkspace"/> at startup instead of showing the start page.</summary>
    public bool AutoOpenLast { get; set; }

    static string PathOf => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NBModTool", "settings.json");

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
