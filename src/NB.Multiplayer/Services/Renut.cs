using System.Diagnostics;
using System.IO;
using NB.Core.Live;
using NB.Core.Mods;

namespace NB.Multiplayer.Services;

/// <summary>
/// reNut: Banjo-Kazooie: Nuts &amp; Bolts recompiled to native PC code (ReXGlue), as an alternative to Xenia (Settings >
/// Game engine). NB Multiplayer runs the same editions, mods, co-op and Character Select with it:
/// <list type="bullet">
/// <item>Game files: reNut reads the edition's folder (--game_data_root), so world, texture and part mods work as is.</item>
/// <item>Executable mods: reNut built with NB's mod layer (renut-nb: NB.Core RenutLayer + its interpreter) runs the
/// patched code of a mod wherever the patched word in memory differs from the original. Editions carry their mods in
/// their own default.xex (loaded by reNut); for the player's own game folder (Vanilla) and NB Studio projects the words
/// are written into the running game instead (<see cref="LivePatch"/>), like Xenia's patch files do.</item>
/// <item>Co-op and Character Select attach to the reNut process: the game's memory is mapped at the same address as in
/// Xenia.</item>
/// <item>Saves live in data\renut\B13EBABEBABEBABE (reNut's profile), with reNut's short content headers.</item>
/// </list>
/// Xbox LIVE rooms (the game's own online races) need Xenia's networking and always use Xenia.
/// </summary>
public static class Renut
{
    /// <summary>reNut's signed-in profile (fixed XUID).</summary>
    public const string ProfileXuid = "B13EBABEBABEBABE";
    public static string UserRoot => Path.Combine(AppSettings.DataDir, "renut");
    public static string ProfileDir => Path.Combine(UserRoot, ProfileXuid);

    /// <summary>The renut.exe to use, or null when there is none.</summary>
    public static string? Exe(AppSettings s)
    {
        if (!string.IsNullOrWhiteSpace(s.RenutPath) && File.Exists(s.RenutPath)) return s.RenutPath;
        var bundled = Path.Combine(AppContext.BaseDirectory, "renut", "renut.exe");
        return File.Exists(bundled) ? bundled : null;
    }

    /// <summary>A reNut build with NB's mod layer (its virtual-controller option and mod interpreter are in the exe).</summary>
    public static bool HasNbLayer(string exe)
    {
        try
        {
            var probe = "nb_remote_input_port"u8.ToArray();
            using var f = File.OpenRead(exe);
            var buf = new byte[1 << 20]; int carry = 0; long read;
            while ((read = f.Read(buf, carry, buf.Length - carry)) > 0)
            {
                int n = (int)read + carry;
                if (buf.AsSpan(0, n).IndexOf(probe) >= 0) return true;
                carry = Math.Min(probe.Length - 1, n);
                Array.Copy(buf, n - carry, buf, 0, carry);
            }
        }
        catch (Exception) { }
        return false;
    }

    /// <summary>Why reNut can't be used now (null = it can).</summary>
    public static string? Problem(AppSettings s)
    {
        var exe = Exe(s);
        if (exe == null) return "reNut is not set up: choose renut.exe in Settings > Game engine.";
        if (!HasNbLayer(exe)) return $"{Path.GetFileName(exe)} was built without NB's mod layer, so executable mods (co-op, Character Select, tweaks) would not run. Build it with NB's renut kit (renut-nb) or switch to Xenia.";
        return null;
    }

    /// <summary>Starts reNut on a game folder. <paramref name="liveMods"/>: executable mods to write into the running game
    /// (for folders whose default.xex does not carry them).</summary>
    public static Process Start(AppSettings s, string gameDir, IReadOnlyList<string> liveMods)
    {
        var exe = Exe(s) ?? throw new FileNotFoundException("reNut is not set up: choose renut.exe in Settings > Game engine.");
        if (Problem(s) is { } why) throw new InvalidOperationException(why);
        Directory.CreateDirectory(ProfileDir);
        var args = new List<string> { "--game_data_root", gameDir, "--user_data_root", UserRoot };
        var extra = Environment.GetEnvironmentVariable("NB_RENUT_EXTRA");   // testing: extra reNut options
        if (!string.IsNullOrWhiteSpace(extra)) args.AddRange(extra.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var p = StartThroughShell(exe, args);
        if (liveMods.Count > 0) LivePatch(p, Path.Combine(gameDir, "default.xex"), liveMods);
        GuardStartup(p, exe, args, gameDir, liveMods, 2);
        return p;
    }

    /// <summary>A new reNut process replaced one that crashed while starting (the app updates its game process).</summary>
    public static event Action<Process>? Restarted;

    /// <summary>
    /// reNut can crash while the title screen loads (rarely: a timing race in the game's own code). A start that crashes
    /// within half a minute is started again (up to <paramref name="retries"/> times), before anyone has played.
    /// </summary>
    static void GuardStartup(Process p, string exe, List<string> args, string gameDir, IReadOnlyList<string> liveMods, int retries)
    {
        var started = DateTime.Now;
        new Thread(() =>
        {
            try
            {
                if (!p.WaitForExit(30000)) return;               // still running: it got past the start
                if (!CrashedRecently(exe, started)) return;       // closed by the player
                if (retries <= 0) { Log("reNut crashed while starting again; not retrying"); return; }
                Log("reNut crashed while starting (title screen); starting it again");
                var again = StartThroughShell(exe, args);
                if (liveMods.Count > 0) LivePatch(again, Path.Combine(gameDir, "default.xex"), liveMods);
                GuardStartup(again, exe, args, gameDir, liveMods, retries - 1);
                Restarted?.Invoke(again);
            }
            catch (Exception e) { Log("startup guard: " + e.Message); }
        }) { IsBackground = true, Name = "NB reNut startup guard" }.Start();
    }

    /// <summary>reNut wrote a crash dump (crashdumps next to renut.exe) since <paramref name="since"/>.</summary>
    static bool CrashedRecently(string exe, DateTime since)
    {
        var dir = Path.Combine(Path.GetDirectoryName(exe)!, "crashdumps");
        return Directory.Exists(dir) && Directory.GetFiles(dir, "*.dmp").Any(f => File.GetLastWriteTime(f) >= since.AddSeconds(-1));
    }

    /// <summary>
    /// Starts reNut from a hidden command prompt that WMI starts (cmd /s /c start ...), and returns the reNut process.
    /// WMI keeps the Steam overlay of this app out of the game (as for Xenia); reNut itself must not be the process WMI
    /// creates: started that way, the game's title-screen loading crashed in about one start in eight (a timing race in the
    /// game that reNut's threads expose), and never when reNut was started normally.
    /// </summary>
    static Process StartThroughShell(string exe, IReadOnlyList<string> args)
    {
        var since = DateTime.Now.AddSeconds(-2);
        string inner = "start \"\" /D " + GameLauncher.QuoteArg(Path.GetDirectoryName(exe)!) + " " + GameLauncher.QuoteArg(exe) + " " +
                       string.Join(" ", args.Select(GameLauncher.QuoteArg));
        using (var cls = new System.Management.ManagementClass("Win32_Process"))
        using (var startup = new System.Management.ManagementClass("Win32_ProcessStartup").CreateInstance())
        {
            startup["ShowWindow"] = (ushort)0;   // the prompt stays hidden
            var inParams = cls.GetMethodParameters("Create");
            inParams["CommandLine"] = "cmd.exe /s /c \"" + inner + "\"";
            inParams["CurrentDirectory"] = Path.GetDirectoryName(exe);
            inParams["ProcessStartupInformation"] = startup;
            var result = cls.InvokeMethod("Create", inParams, null);
            uint rc = (uint)result["ReturnValue"];
            if (rc != 0) throw new InvalidOperationException($"Windows could not start reNut (WMI error {rc}).");
        }
        // the prompt exits at once; find the reNut it started
        for (int i = 0; i < 100; i++)
        {
            var p = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)).Where(p =>
            {
                try { return string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase) && p.StartTime >= since; }
                catch (Exception) { return false; }
            }).OrderByDescending(p => p.StartTime).FirstOrDefault();
            if (p != null) return p;
            Thread.Sleep(100);
        }
        throw new InvalidOperationException("reNut did not start (no renut.exe process appeared).");
    }

    /// <summary>
    /// Writes the words of <paramref name="modIds"/> into the running game as soon as its image is in memory (each word
    /// only where the original instruction is still there). NB's mod layer in reNut then runs the patched code.
    /// </summary>
    public static void LivePatch(Process game, string xexPath, IReadOnlyList<string> modIds)
    {
        var mods = ExePatches.ResolveAll(modIds);
        if (mods.Count == 0) return;
        new Thread(() =>
        {
            try
            {
                var img = NB.Core.Formats.XexFile.Read(File.ReadAllBytes(xexPath)).GetImage();
                var probe = img.AsSpan((int)(XeniaLive.TextStart - 0x82000000), 64).ToArray();
                Log($"game {game.Id} started; waiting for its image to write {mods.Count} mod(s)");
                for (int i = 0; i < 240; i++)
                {
                    XeniaLive? x = null;
                    try { x = XeniaLive.AttachPid(game.Id, probe); }
                    catch (Exception) { Thread.Sleep(250); continue; }   // the image is not loaded yet
                    using (x)
                    {
                        int written = 0;
                        // the mod code and data first, the instructions that jump to it last: a site that went live before
                        // the code it jumps to would run empty padding
                        var words = mods.SelectMany(m => m.Words)
                            .OrderBy(w => w.Original != 0 && w.Address >= RenutLayer_CodeStart && w.Address < RenutLayer_CodeEnd ? 1 : 0).ToList();
                        foreach (var w in words)
                        {
                            uint now = x.U32(w.Address);
                            if (now == w.Patched) continue;
                            if (now != w.Original) continue;           // something else changed this word: leave it
                            var b = new byte[4]; NB.Core.IO.BE.W32(b, 0, w.Patched);
                            x.WriteCode(w.Address, b);
                            written++;
                        }
                        Log($"{written} executable-mod words written into the game ({string.Join(", ", mods.Select(m => m.Id))})");
                    }
                    return;
                }
            }
            catch (Exception e) { Log("live patch failed: " + e); }
        }) { IsBackground = true, Name = "NB reNut live patch" }.Start();
    }

    /// <summary>data\renut_launch.log: what the reNut launcher did (live patches, problems).</summary>
    public static void Log(string line)
    {
        try { File.AppendAllText(Path.Combine(AppSettings.DataDir, "renut_launch.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}"); }
        catch (Exception) { }
    }

    const uint RenutLayer_CodeStart = 0x821E0000, RenutLayer_CodeEnd = 0x821E0000 + 0xB82CC4;   // compiled code (NB.Core RenutLayer)

    public static bool IsRunning(AppSettings s)
    {
        var exe = Exe(s);
        if (exe == null) return false;
        return Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)).Any(p =>
        {
            try { return string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase); } catch (Exception) { return false; }
        });
    }

    /// <summary>A reNut content header (XCONTENT_AGGREGATE_DATA, 328 bytes): device 1 (HDD), type 1 (saved game),
    /// display name, package file name, XUID 0, title id.</summary>
    public static byte[] ContentHeader(string fileName, string displayName)
    {
        var h = new byte[0x148];
        NB.Core.IO.BE.W32(h, 0, 1u); NB.Core.IO.BE.W32(h, 4, 1u);
        var d = System.Text.Encoding.BigEndianUnicode.GetBytes(displayName);
        Array.Copy(d, 0, h, 8, Math.Min(d.Length, 254));
        var f = System.Text.Encoding.ASCII.GetBytes(fileName);
        Array.Copy(f, 0, h, 0x108, Math.Min(f.Length, 42));
        NB.Core.IO.BE.W32(h, 0x140, NB.Core.Mods.ExePatches.TitleId);
        return h;
    }
}
