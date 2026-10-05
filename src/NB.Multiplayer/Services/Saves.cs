using System.IO;

namespace NB.Multiplayer.Services;

/// <summary>
/// The all-unlocked save (Settings): before a game starts, the player's Nuts &amp; Bolts save slots in the Xenia profile
/// (data\content\&lt;profile&gt;\4D5307ED\00000001\0x0b0a5c5c and 0x0b0d6cca) get the bundled all-unlocked save, so
/// "Resume Saved Game" goes straight to Showdown Town with everything unlocked. The player's own save is copied to
/// data\save_backup\original once and put back when the option is turned off. A slot without a header (a new profile)
/// gets one from a template with no profile id or icon (Xenia reads the slot through its header). With reNut the same
/// happens in its profile folder (data\renut\B13EBABEBABEBABE), with reNut's own short headers.
/// </summary>
public static class Saves
{
    const string Title = "4D5307ED", SaveType = "00000001";
    static readonly string[] Slots = { "0b0a5c5c", "0b0d6cca" };
    static string Bundled => Path.Combine(AppContext.BaseDirectory, "saves");
    public static bool Available => File.Exists(Path.Combine(Bundled, "AllUnlocked.sav")) && File.Exists(Path.Combine(Bundled, "savegame.header.template"));
    static string BackupDir => Path.Combine(AppSettings.DataDir, "save_backup", "original");

    /// <summary>The Xenia profile folders (data\content\&lt;16 hex digits&gt; with an account), newest first; with reNut its one
    /// profile folder (data\renut\B13EBABEBABEBABE).</summary>
    public static List<string> Profiles(bool renut = false)
    {
        if (renut) { Directory.CreateDirectory(Renut.ProfileDir); return new() { Renut.ProfileDir }; }
        var root = Path.Combine(AppSettings.DataDir, "content");
        if (!Directory.Exists(root)) return new();
        return Directory.GetDirectories(root)
            .Where(d => Path.GetFileName(d).Length == 16 && Path.GetFileName(d).All(Uri.IsHexDigit) && Path.GetFileName(d).Trim('0').Length > 0)
            .Where(d => Directory.Exists(Path.Combine(d, "FFFE07D1")))
            .OrderByDescending(Directory.GetLastWriteTimeUtc).ToList();
    }

    /// <summary>The profile the game signs in: Xenia picks the lowest XUID among the profiles.</summary>
    public static string? ActiveProfile() =>
        Profiles().OrderBy(p => ulong.TryParse(Path.GetFileName(p), System.Globalization.NumberStyles.HexNumber, null, out var x) ? x : ulong.MaxValue).FirstOrDefault();

    /// <summary>A content header for a blueprint package (the bundled save header with the display name "VEHICLE: name").</summary>
    public static byte[] BlueprintHeaderTemplate(string name)
    {
        var hdr = File.ReadAllBytes(Path.Combine(Bundled, "savegame.header.template"));
        Array.Clear(hdr, 0x411, 0x80);
        var display = System.Text.Encoding.BigEndianUnicode.GetBytes(("VEHICLE: " + name).ToUpperInvariant());
        Array.Copy(display, 0, hdr, 0x411, Math.Min(display.Length, 0x7E));
        return hdr;
    }

    static string SlotFile(string profile, string slot) => Path.Combine(profile, Title, SaveType, "0x" + slot, slot);
    static string HeaderFile(string profile, string slot) => Path.Combine(profile, Title, "Headers", SaveType, "0x" + slot + ".header");

    /// <summary>Puts the all-unlocked save into every profile's slots (backing up the player's own save once). Returns the profile count.</summary>
    public static int Install(bool renut = false)
    {
        if (!Available) return 0;
        var save = File.ReadAllBytes(Path.Combine(Bundled, "AllUnlocked.sav"));
        var template = File.ReadAllBytes(Path.Combine(Bundled, "savegame.header.template"));
        int n = 0;
        foreach (var profile in Profiles(renut))
        {
            BackupOnce(profile);
            foreach (var slot in Slots)
            {
                var f = SlotFile(profile, slot);
                Directory.CreateDirectory(Path.GetDirectoryName(f)!);
                File.WriteAllBytes(f, save);
                var h = HeaderFile(profile, slot);
                if (!File.Exists(h) && renut)
                {
                    // reNut reads a slot through its short content header
                    Directory.CreateDirectory(Path.GetDirectoryName(h)!);
                    File.WriteAllBytes(h, Renut.ContentHeader("0x" + slot, "BANJO SAVE GAME"));
                }
                else if (!File.Exists(h))
                {
                    var hdr = (byte[])template.Clone();
                    var name = System.Text.Encoding.ASCII.GetBytes(slot);
                    Array.Clear(hdr, 0x971C, 42); name.CopyTo(hdr, 0x971C);   // Xenia's file-name field
                    Directory.CreateDirectory(Path.GetDirectoryName(h)!);
                    File.WriteAllBytes(h, hdr);
                }
            }
            n++;
        }
        return n;
    }

    /// <summary>The player's own save before the first install (per profile; slots that did not exist are recorded as absent).</summary>
    static void BackupOnce(string profile)
    {
        var dir = Path.Combine(BackupDir, Path.GetFileName(profile));
        if (Directory.Exists(dir)) return;
        Directory.CreateDirectory(dir);
        foreach (var slot in Slots)
        {
            var f = SlotFile(profile, slot); var h = HeaderFile(profile, slot);
            if (File.Exists(f)) File.Copy(f, Path.Combine(dir, slot));
            if (File.Exists(h)) File.Copy(h, Path.Combine(dir, slot + ".header"));
        }
        File.WriteAllText(Path.Combine(dir, "done.txt"), DateTime.Now.ToString("u"));
    }

    /// <summary>Puts every backed-up save back (and removes slots the player did not have). Returns the profile count.</summary>
    public static int Restore()
    {
        if (!Directory.Exists(BackupDir)) return 0;
        int n = 0;
        foreach (var dir in Directory.GetDirectories(BackupDir))
        {
            if (!File.Exists(Path.Combine(dir, "done.txt"))) continue;
            var name = Path.GetFileName(dir);
            var profile = name == Renut.ProfileXuid ? Renut.ProfileDir : Path.Combine(AppSettings.DataDir, "content", name);
            foreach (var slot in Slots)
            {
                var f = SlotFile(profile, slot); var h = HeaderFile(profile, slot);
                var bf = Path.Combine(dir, slot); var bh = Path.Combine(dir, slot + ".header");
                if (File.Exists(bf)) { Directory.CreateDirectory(Path.GetDirectoryName(f)!); File.Copy(bf, f, true); }
                else if (File.Exists(f)) Directory.Delete(Path.GetDirectoryName(f)!, true);
                if (File.Exists(bh)) { Directory.CreateDirectory(Path.GetDirectoryName(h)!); File.Copy(bh, h, true); }
                else if (File.Exists(h)) File.Delete(h);
            }
            Directory.Delete(dir, true);
            n++;
        }
        return n;
    }

    /// <summary>
    /// Before a launch: installs into existing profiles. A brand-new profile only appears once Xenia has started, so the
    /// folder is watched for a minute and the save installed as soon as it exists (well before the title menu).
    /// </summary>
    /// <param name="useAllUnlocked">Install the all-unlocked save for this launch. A co-op room can ask for it although the
    /// player's own setting is off: then it is installed for that game only, and the player's own save comes back at the
    /// next launch without it (data\save_backup\room.txt marks such an install).</param>
    public static void PrepareLaunch(bool useAllUnlocked, bool forRoom = false, bool renut = false)
    {
        var marker = Path.Combine(AppSettings.DataDir, "save_backup", "room.txt");
        if (!useAllUnlocked)
        {
            if (File.Exists(marker)) { try { Restore(); File.Delete(marker); } catch (Exception) { } }
            return;
        }
        if (!Available) return;
        if (forRoom) { Directory.CreateDirectory(Path.GetDirectoryName(marker)!); File.WriteAllText(marker, "installed for a co-op room"); }
        else if (File.Exists(marker)) File.Delete(marker);
        if (renut) { try { Install(renut: true); } catch (Exception) { } return; }   // reNut's profile folder is fixed
        var before = Profiles().Select(Path.GetFileName).ToHashSet();
        try { Install(); } catch (Exception) { }
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 120; i++)
            {
                await Task.Delay(500);
                if (Profiles().Any(p => !before.Contains(Path.GetFileName(p))))
                {
                    await Task.Delay(1000);   // let Xenia finish writing the new profile
                    try { Install(); } catch (Exception) { }
                    return;
                }
            }
        });
    }
}
