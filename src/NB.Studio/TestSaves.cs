using System.Diagnostics;
using NB.Core.Project;

namespace NB.Studio;

/// <summary>
/// The saves of Test in Xenia (F5 / Shift+F5), all inside the workspace's own test storage (never the player's own Xenia
/// or NB Multiplayer saves):
/// <list type="bullet">
/// <item>Full save (Settings / Build menu): the two save slots get the all-unlocked save (NB Multiplayer's
/// AllUnlocked.sav) before every test, so the game resumes it: everything unlocked, straight into Showdown Town.</item>
/// <item>Vehicles: blueprints saved in Mumbo's garage during a test go into one shared folder (<see cref="VehicleVault"/>,
/// by default NB Multiplayer's blueprint folder) as soon as they appear and when the game closes, and every test storage
/// gets the folder's vehicles before the game starts — so every workspace (and NB Multiplayer) has the same vehicles.</item>
/// </list>
/// </summary>
public static class TestSaves
{
    static readonly string[] Slots = { "0b0a5c5c", "0b0d6cca" };
    const int NameField = 0x971A, ProfileField = 0x371;

    /// <summary>The shared vehicle folder chosen in Settings, or null for "per workspace" (no sharing).</summary>
    public static string? VaultDir(Settings s) => s.VehicleSaves switch
    {
        null or "" => VehicleVault.DefaultVault(ProjectRegistry.DataDir),
        "studio" => Path.Combine(ProjectRegistry.DataDir, "blueprint_vault"),
        "workspace" => null,
        var custom => custom,
    };

    /// <summary>How the vault's owner key names this workspace's test storage (deletions are tracked per storage).</summary>
    public static string Owner(Workspace ws) => "nbstudio:" + Path.GetFullPath(QuickTest.Folder(ws)).ToLowerInvariant();

    /// <summary>The folder with the all-unlocked save (AllUnlocked.sav + savegame.header.template): bundled with NB Studio,
    /// in the project's coop\saves, or NB Multiplayer's next to its Xenia. Null when there is none.</summary>
    public static string? FullSaveDir(string? forkExe)
    {
        static bool Ok(string d) => File.Exists(Path.Combine(d, "AllUnlocked.sav")) && File.Exists(Path.Combine(d, "savegame.header.template"));
        var c = new List<string> { Path.Combine(AppContext.BaseDirectory, "saves") };
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent) c.Add(Path.Combine(d.FullName, "coop", "saves"));
        if (forkExe != null && Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(forkExe))) is { } app) c.Add(Path.Combine(app, "saves"));
        return c.FirstOrDefault(Ok);
    }

    /// <summary>Writes the all-unlocked save into both save slots of a profile, with headers owned by that profile.</summary>
    static void InstallFullSave(string profile, string savesDir)
    {
        var save = File.ReadAllBytes(Path.Combine(savesDir, "AllUnlocked.sav"));
        var template = File.ReadAllBytes(Path.Combine(savesDir, "savegame.header.template"));
        ulong xuid = ulong.TryParse(Path.GetFileName(profile), System.Globalization.NumberStyles.HexNumber, null, out var x) ? x : 0;
        foreach (var slot in Slots)
        {
            var f = Path.Combine(profile, VehicleVault.Title, VehicleVault.SaveType, "0x" + slot, slot);
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllBytes(f, save);
            var h = (byte[])template.Clone();
            Array.Clear(h, NameField, 42);
            System.Text.Encoding.ASCII.GetBytes("0x" + slot).CopyTo(h, NameField);
            if (xuid != 0) for (int i = 0; i < 8; i++) h[ProfileField + i] = (byte)(xuid >> (56 - 8 * i));
            var hf = Path.Combine(profile, VehicleVault.Title, "Headers", VehicleVault.SaveType, "0x" + slot + ".header");
            Directory.CreateDirectory(Path.GetDirectoryName(hf)!);
            File.WriteAllBytes(hf, h);
        }
    }

    /// <summary>
    /// Before a test, in its content folder: collects vehicles the last test saved, empties the save slots (and with
    /// "Fresh save" the vehicles), puts the full save in (option) and the shared vehicles. A storage without a profile yet
    /// (the first test of a workspace: Xenia creates it when it starts) is watched for a minute and served then.
    /// </summary>
    public static void Prepare(Workspace ws, string content, Settings s, string? forkExe, bool act, Action<string> log)
    {
        var vault = VaultDir(s);
        string owner = Owner(ws);
        if (vault != null)
            try { var got = VehicleVault.Harvest(vault, content, owner); if (got.Count > 0) log($"vehicles from the last test added to the shared vehicle saves: {string.Join(", ", got)}"); }
            catch (Exception e) { log("collecting vehicles failed: " + e.Message); }
        if (s.QuickTestFreshSave && vault != null) VehicleVault.Forget(vault, owner);
        QuickTest.ClearSaves(content, keepBlueprints: !s.QuickTestFreshSave);
        string? saves = null;
        if (s.QuickTestFullSave && act)
            log("full save: an Act starts through a new game (a resumed save always starts in Showdown Town); the full save is used when the test starts in Showdown Town");
        else if (s.QuickTestFullSave)
        {
            saves = FullSaveDir(forkExe);
            if (saves == null) log("full save: AllUnlocked.sav not found (it comes with NB Multiplayer); starting with a new game");
        }
        void Serve(string profile)
        {
            if (saves != null) { InstallFullSave(profile, saves); log("full (all-unlocked) save: the game resumes it in Showdown Town"); }
            if (vault != null && !s.QuickTestFreshSave)
            {
                // a vehicle without a kept header (none so far) gets the save header template with its name
                var t = saves ?? FullSaveDir(forkExe);
                var put = VehicleVault.RestoreInto(vault, profile, owner, t != null ? File.ReadAllBytes(Path.Combine(t, "savegame.header.template")) : null);
                int total = VehicleVault.Vehicles(vault).Count;
                log(put.Count > 0 ? $"shared vehicle saves: {put.Count} added to this test ({string.Join(", ", put.Take(8))}{(put.Count > 8 ? " …" : "")}); {total} in {vault}"
                                  : $"shared vehicle saves: {total} vehicle(s), all in this test already ({vault})");
            }
        }
        var profiles = VehicleVault.Profiles(content);
        if (s.QuickTestFreshSave) log("fresh test save (File > Settings)");
        else if (vault == null) { var bps = QuickTest.TestBlueprints(content); log(bps.Count > 0 ? $"vehicles saved in earlier tests of this workspace: {string.Join(", ", bps)}" : "no vehicles saved in earlier tests of this workspace yet"); }
        foreach (var p in profiles) { try { Serve(p); } catch (Exception e) { log("preparing the save failed: " + e.Message); } }
        if (profiles.Count == 0 && (saves != null || vault != null))
        {
            // the first test: the profile appears once Xenia has started (well before the title screen)
            var before = Directory.Exists(content) ? Directory.GetDirectories(content).Select(Path.GetFileName).ToHashSet() : new HashSet<string?>();
            _ = Task.Run(async () =>
            {
                for (int i = 0; i < 120; i++)
                {
                    await Task.Delay(500);
                    var p = VehicleVault.Profiles(content).FirstOrDefault();
                    if (p == null) continue;
                    await Task.Delay(1500);   // let Xenia finish writing the new profile
                    try { Serve(p); } catch (Exception e) { log("preparing the save failed: " + e.Message); }
                    return;
                }
            });
        }
    }

    /// <summary>While the test runs: a vehicle saved in the garage goes into the shared folder a few seconds later, and
    /// everything once more when the game closes.</summary>
    public static void Watch(Process p, Workspace ws, string content, Settings s, Action<string> log)
    {
        var vault = VaultDir(s);
        if (vault == null) return;
        string owner = Owner(ws);
        Directory.CreateDirectory(content);
        var w = new FileSystemWatcher(content) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
        var gate = new object(); System.Threading.Timer? timer = null;
        void Collect()
        {
            lock (gate)
            {
                try { var got = VehicleVault.Harvest(vault, content, owner); if (got.Count > 0) log($"vehicle saved: {string.Join(", ", got)} → shared vehicle saves ({vault})"); }
                catch (Exception e) { log("collecting vehicles failed: " + e.Message); }
            }
        }
        void Changed(object? _, FileSystemEventArgs e)
        {
            // blueprint packages are content\<xuid>\4D5307ED\00000001\0x0000000N\...; the save slots are not vehicles
            if (!e.FullPath.Contains(Path.DirectorySeparatorChar + "0x00", StringComparison.OrdinalIgnoreCase)) return;
            lock (gate) { timer?.Dispose(); timer = new System.Threading.Timer(_ => Collect(), null, 4000, Timeout.Infinite); }
        }
        w.Created += Changed; w.Changed += Changed; w.Renamed += (o, e) => Changed(o, e);
        w.EnableRaisingEvents = true;
        p.EnableRaisingEvents = true;
        p.Exited += (_, _) =>
        {
            w.EnableRaisingEvents = false; w.Dispose();
            lock (gate) { timer?.Dispose(); timer = null; }
            Thread.Sleep(500);
            Collect();
        };
    }

    /// <summary>Build > Import Xbox 360 Vehicles: blueprint packages copied from a console into the shared folder.</summary>
    public static string Import(Settings s, IEnumerable<string> files)
    {
        var vault = VaultDir(s) ?? VehicleVault.DefaultVault(ProjectRegistry.DataDir);
        var (added, known, skipped) = VehicleVault.ImportPackages(vault, files);
        return $"Import vehicles: {added.Count} added{(added.Count > 0 ? " (" + string.Join(", ", added.Take(12)) + (added.Count > 12 ? " …" : "") + ")" : "")}, " +
               $"{known} already there{(skipped.Count > 0 ? $", {skipped.Count} skipped: " + string.Join("; ", skipped.Take(6)) : "")} → {vault}";
    }
}
