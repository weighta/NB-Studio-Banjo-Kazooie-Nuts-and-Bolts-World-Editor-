using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace NB.Multiplayer.Services;

/// <summary>
/// Updates from the GitHub releases of <see cref="Repo"/>: the latest release's tag (v1.2.3) is compared with this
/// build's version; its zip asset holds a complete app folder. Applying: download + extract to %TEMP%, start a copy of
/// this exe with --apply-update (it waits for the app to exit, copies the new files over the install folder and starts
/// the new version). Settings, the Xenia profile/saves and editions live in %LOCALAPPDATA% and are never touched.
/// </summary>
public static class Updater
{
    public const string Repo = "weighta/NB-Multiplayer";
    public static string ReleasesPage => $"https://github.com/{Repo}/releases";

    public sealed record Release(Version Version, string Tag, string Name, string Notes, string ZipUrl, string PageUrl);

    public static Version Current =>
        typeof(Updater).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : new Version(0, 0, 0);

    static HttpClient Http()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd($"NBMultiplayer/{Current}");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    /// <summary>The latest release, or null when there is none (or GitHub is unreachable).</summary>
    public static async Task<Release?> LatestAsync()
    {
        try
        {
            using var http = Http();
            var json = JsonNode.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"))!;
            string tag = json["tag_name"]?.GetValue<string>() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) return null;
            string? zip = null;
            if (json["assets"] is JsonArray assets)
                foreach (var a in assets)
                {
                    var n = a?["name"]?.GetValue<string>() ?? "";
                    if (n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) { zip = a!["browser_download_url"]?.GetValue<string>(); break; }
                }
            if (zip == null) return null;
            return new Release(v, tag, json["name"]?.GetValue<string>() ?? tag, json["body"]?.GetValue<string>() ?? "", zip,
                json["html_url"]?.GetValue<string>() ?? ReleasesPage);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Downloads and stages <paramref name="r"/>, then starts the update helper. The caller must exit the app.</summary>
    public static async Task StageAndLaunchAsync(Release r, IProgress<double> progress)
    {
        var work = Path.Combine(Path.GetTempPath(), "NBMultiplayer-update");
        Directory.CreateDirectory(work);
        var zipPath = Path.Combine(work, $"{r.Tag}.zip");
        using (var http = Http())
        {
            http.Timeout = TimeSpan.FromMinutes(20);
            using var resp = await http.GetAsync(r.ZipUrl, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? 0, done = 0;
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = File.Create(zipPath);
            var buf = new byte[1 << 16];
            int n;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n));
                done += n;
                if (total > 0) progress.Report((double)done / total);
            }
        }
        var staging = Path.Combine(work, r.Tag);
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        ZipFile.ExtractToDirectory(zipPath, staging);
        // the zip may hold the app folder itself or one folder containing it
        string appDir = staging;
        if (!File.Exists(Path.Combine(appDir, "NBMultiplayer.exe")))
            appDir = Directory.GetDirectories(staging).FirstOrDefault(d => File.Exists(Path.Combine(d, "NBMultiplayer.exe")))
                     ?? throw new InvalidDataException("the update has no NBMultiplayer.exe");
        var helper = Path.Combine(work, "NBMultiplayer-updater.exe");
        File.Copy(Environment.ProcessPath!, helper, true);
        Process.Start(new ProcessStartInfo(helper)
        {
            ArgumentList = { "--apply-update", appDir, AppContext.BaseDirectory, Environment.ProcessId.ToString() },
            UseShellExecute = false,
        });
    }

    /// <summary>Update helper mode (runs from %TEMP%): wait for the app, copy the new files, restart it.</summary>
    public static void ApplyUpdate(string from, string to, int pid)
    {
        try { Process.GetProcessById(pid).WaitForExit(60_000); } catch (Exception) { }
        Thread.Sleep(500);
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var dst = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            for (int attempt = 0; ; attempt++)
            {
                try { File.Copy(f, dst, true); break; }
                catch (IOException) when (attempt < 20) { Thread.Sleep(500); }   // a game or server still closing
            }
        }
        Process.Start(new ProcessStartInfo(Path.Combine(to, "NBMultiplayer.exe")) { UseShellExecute = true, WorkingDirectory = to });
    }
}
