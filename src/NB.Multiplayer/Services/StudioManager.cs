using System.Diagnostics;
using System.IO;
using System.IO.Compression;

namespace NB.Multiplayer.Services;

/// <summary>
/// NB Studio, the editor that makes mods: NB Multiplayer finds it (a copy it downloaded into its data folder, or one the
/// player chose), installs or updates it from the GitHub releases of <see cref="Repo"/>, and opens projects in it
/// (NBModStudio.exe --workspace &lt;folder&gt;).
/// </summary>
public static class StudioManager
{
    public const string Repo = "weighta/NB-Studio-Banjo-Kazooie-Nuts-and-Bolts-World-Editor-";
    public static string ReleasesPage => $"https://github.com/{Repo}/releases";
    public static string ManagedDir => Path.Combine(AppSettings.Root, "NB-Studio");

    /// <summary>NBModStudio.exe: the player's own copy (Settings) or the one NB Multiplayer installed; null = none.</summary>
    public static string? Exe(AppSettings s)
    {
        if (s.StudioPath.Length > 0 && File.Exists(s.StudioPath)) return s.StudioPath;
        if (!Directory.Exists(ManagedDir)) return null;
        return Directory.GetFiles(ManagedDir, "NBModStudio.exe", SearchOption.AllDirectories).FirstOrDefault();
    }

    public static bool IsManaged(string exe) =>
        Path.GetFullPath(exe).StartsWith(Path.GetFullPath(ManagedDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static Version VersionOf(string exe)
    {
        var v = FileVersionInfo.GetVersionInfo(exe);
        return new Version(Math.Max(v.FileMajorPart, 0), Math.Max(v.FileMinorPart, 0), Math.Max(v.FileBuildPart, 0));
    }

    public static bool IsRunning(string exe) =>
        Process.GetProcessesByName("NBModStudio").Any(p =>
        {
            try { return string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return true; }   // cannot tell: assume it is ours
        });

    /// <summary>Downloads <paramref name="r"/> and installs it as NB Multiplayer's copy of NB Studio (replacing an older one).</summary>
    public static async Task<string> InstallAsync(Updater.Release r, IProgress<(string Text, double Fraction)> progress)
    {
        var old = Directory.Exists(ManagedDir) ? Directory.GetFiles(ManagedDir, "NBModStudio.exe", SearchOption.AllDirectories).FirstOrDefault() : null;
        if (old != null && IsRunning(old)) throw new InvalidOperationException("NB Studio is open. Close it first, then update.");
        var work = Path.Combine(Path.GetTempPath(), "NBMultiplayer-studio");
        Directory.CreateDirectory(work);
        var zip = Path.Combine(work, $"NB-Studio-{r.Tag}.zip");
        await Updater.DownloadAsync(r.ZipUrl, zip, new Progress<double>(f => progress.Report(($"Downloading NB Studio {r.Version}", 0.9 * f))));
        progress.Report(("Installing NB Studio", 0.92));
        var staging = ManagedDir + ".new";
        await Task.Run(() =>
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            ZipFile.ExtractToDirectory(zip, staging);
            if (!Directory.GetFiles(staging, "NBModStudio.exe", SearchOption.AllDirectories).Any())
                throw new InvalidDataException("the download has no NBModStudio.exe");
            if (Directory.Exists(ManagedDir)) Directory.Delete(ManagedDir, true);
            Directory.Move(staging, ManagedDir);
            File.Delete(zip);
        });
        progress.Report(("Done", 1));
        return Exe(new AppSettings())!;
    }

    /// <summary>Starts NB Studio, optionally opening a project (workspace folder).</summary>
    public static void Launch(string exe, string? workspace) =>
        GameLauncher.StartDetached(exe, workspace != null ? new[] { "--workspace", workspace } : Array.Empty<string>(), Path.GetDirectoryName(exe)!);
}
