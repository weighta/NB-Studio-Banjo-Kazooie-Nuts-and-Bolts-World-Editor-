using System.Diagnostics;
using System.Net.Sockets;
using System.Numerics;
using NB.Core.IO;
using NB.Core.Project;

namespace NB.Studio;

/// <summary>
/// Build > Test in Xenia (F5): plays the world (or Act) open in NB Studio without the title screen, the menus and the
/// intro. The workspace itself is never changed:
/// <list type="bullet">
/// <item>&lt;workspace&gt;\quicktest\game is a hard-linked mirror of the workspace's game folder (no extra disk space; it is
/// re-synced on every launch, so it always shows the last saved state). Only its common bundle (685374) is a real file:
/// the start-of-game script with the intro and tutorial removed, the Showdown Town intro steps preset and its final
/// "go to" pointing at the world's script (the same edits as Tools > Test Mode, but on the copy).</item>
/// <item>NB's Xenia build (NB Multiplayer's xenia_canary_netplay.exe, which has a virtual gamepad) runs it with its own
/// storage folder &lt;workspace&gt;\quicktest\xenia: its own profile and an empty save, so SINGLE PLAYER always starts a new
/// game (the user's saves are never touched). NB Studio then presses A (title, house menu) and Y (act intro cut-scenes)
/// through the virtual pad until Banjo is in the world, and hands the controller back.</item>
/// <item>Any other Xenia: the same prepared copy with a separate content folder; the player presses Start / A twice.</item>
/// </list>
/// </summary>
public static class QuickTest
{
    public sealed record Target(string Script, string Display, bool IsAct, string Note = "");

    public const string TownScript = "aid_script_banjox_showdowntown_midday";

    /// <summary>NB's Xenia build (virtual gamepad): the configured one when it is that build, else the one installed with
    /// NB Multiplayer (next to NB Studio) or in the project's mp\xenia folder.</summary>
    public static string? FindForkXenia(string? configured)
    {
        if (Environment.GetEnvironmentVariable("NB_STUDIO_NO_FORK") == "1") return null;   // testing: the plain-Xenia path
        if (configured != null && File.Exists(configured) && IsFork(configured)) return configured;
        foreach (var rel in new[] { Path.Combine("xenia", "xenia_canary_netplay.exe"), Path.Combine("mp", "xenia", "xenia_canary_netplay.exe") })
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null) { var p = Path.Combine(d.FullName, rel); if (File.Exists(p)) return p; d = d.Parent; }
        }
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NB-Multiplayer", "xenia", "xenia_canary_netplay.exe");
        return File.Exists(local) ? local : null;
    }

    /// <summary>NB Multiplayer's Xenia settings file (its data folder next to the xenia folder, or the installed app's), so
    /// the test game has the player's controls and graphics settings. Null when there is none.</summary>
    public static string? UserConfig(string forkExe)
    {
        const string name = "xenia-canary-netplay.config.toml";
        var app = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(forkExe)));
        foreach (var d in new[] { app != null ? Path.Combine(app, "data") : null,
                                  Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NB-Multiplayer", "data") })
            if (d != null && File.Exists(Path.Combine(d, name))) return Path.Combine(d, name);
        return null;
    }

    public static bool IsFork(string exe) => Path.GetFileName(exe).Contains("netplay", StringComparison.OrdinalIgnoreCase);

    public static string Folder(Workspace ws) => Path.Combine(ws.Root, "quicktest");

    // ------------------------------------------------------------------ the prepared copy

    /// <summary>Mirrors the workspace's game folder as hard links (changed files are re-linked) and writes the test-mode
    /// common bundle for <paramref name="t"/>. Returns the copy's default.xex.</summary>
    public static string Prepare(Workspace ws, Target t, Action<string> log, Action<string, double>? progress = null)
    {
        string dir = Folder(ws), dst = Path.Combine(dir, "game"), src = ws.Game.Root;
        Directory.CreateDirectory(dst);
        string commonRel = Path.GetRelativePath(src, ws.Game.ResidentPath(NB.Core.World.TestMode.CommonBundle));
        var files = Directory.GetFiles(src, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) && !f.Contains(PatchPackage.BackupDirName)).ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool sameVolume = string.Equals(Path.GetPathRoot(Path.GetFullPath(src)), Path.GetPathRoot(Path.GetFullPath(dst)), StringComparison.OrdinalIgnoreCase);
        int linked = 0, copied = 0;
        for (int i = 0; i < files.Count; i++)
        {
            var rel = Path.GetRelativePath(src, files[i]);
            seen.Add(rel);
            if (rel.Equals(commonRel, StringComparison.OrdinalIgnoreCase)) continue;
            var to = Path.Combine(dst, rel);
            var fi = new FileInfo(files[i]); var ti = new FileInfo(to);
            // a hard link shares size and time with its file; the workspace replaces files when it saves (new size / time)
            if (ti.Exists && ti.Length == fi.Length && ti.LastWriteTimeUtc == fi.LastWriteTimeUtc) continue;
            if (i % 50 == 0) progress?.Invoke($"Preparing the test copy ({i + 1}/{files.Count})", 0.6 * i / files.Count);
            if (ti.Exists) FileLinks.DeleteIgnoringReadOnly(to);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (sameVolume && FileLinks.TryLink(to, files[i])) linked++;
            else { File.Copy(files[i], to, true); File.SetAttributes(to, FileAttributes.Normal); copied++; }
        }
        foreach (var f in Directory.GetFiles(dst, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(dst, f);
            if (!seen.Contains(rel)) FileLinks.DeleteIgnoringReadOnly(f);   // deleted from the workspace (or a leftover temp file)
        }
        if (linked + copied > 0) log($"  test copy: {linked} file(s) linked{(copied > 0 ? $", {copied} copied (other drive)" : "")} from the workspace");

        // the start-of-game script of the copy: rebuilt only when the workspace's common bundle or the target changed
        var srcCommon = new FileInfo(Path.Combine(src, commonRel));
        string key = $"v1|{srcCommon.Length}|{srcCommon.LastWriteTimeUtc.Ticks}|{t.Script}";
        string keyFile = Path.Combine(dir, "common.key"), dstCommon = Path.Combine(dst, commonRel);
        if (File.Exists(keyFile) && File.ReadAllText(keyFile) == key && File.Exists(dstCommon) && FileLinks.LinkCount(dstCommon) == 1)
            return Path.Combine(dst, "default.xex");
        progress?.Invoke("Writing the test start script…", 0.7);
        if (File.Exists(dstCommon)) FileLinks.DeleteIgnoringReadOnly(dstCommon);
        File.Copy(srcCommon.FullName, dstCommon);
        File.SetAttributes(dstCommon, FileAttributes.Normal);
        var fw = Workspace.OnFolder(dst);
        try
        {
            using (fw.Batch())
            {
                log("  " + NB.Core.World.TestMode.SkipStartOfGame(fw, keepShowdownTownIntro: false));
                log("  " + NB.Core.World.TestMode.PresetTownIntro(fw));
                log("  start script " + NB.Core.World.TestMode.StartIn(fw, t.Script));
            }
        }
        finally { fw.DeleteCache(); }
        File.WriteAllText(keyFile, key);
        return Path.Combine(dst, "default.xex");
    }

    /// <summary>Empties the test storage's save games (the profile stays), so SINGLE PLAYER starts a new game.</summary>
    public static void ClearSaves(string contentRoot)
    {
        if (!Directory.Exists(contentRoot)) return;
        foreach (var user in Directory.GetDirectories(contentRoot))
        {
            var game = Path.Combine(user, NB.Core.Mods.ExePatches.TitleId.ToString("X8"));
            if (Directory.Exists(game)) Directory.Delete(game, true);
        }
    }

    public static int FreeUdpPort()
    {
        using var u = new UdpClient(0);
        return ((System.Net.IPEndPoint)u.Client.LocalEndPoint!).Port;
    }

    // ------------------------------------------------------------------ getting into the world

    /// <summary>The player avatar exists and no menu is open (the same test as the co-op harness).</summary>
    public static bool InGame(NB.Core.Live.XeniaLive x)
    {
        uint l = x.U32(NB.Core.Live.XeniaLive.PlayerPtr);
        return l != 0 && x.U32(l + 0xA44) != 0 && x.U32(0x82FACBA4) == 0xFFFFFFFF;
    }

    /// <summary>
    /// Presses through the title screen and the house menu (A) and act intro cut-scenes (Y) with the virtual pad until
    /// Banjo is in the world, then (Act) skips the act's opening dialogue with one Y, optionally moves Banjo to
    /// <paramref name="spawn"/>, and unplugs the virtual pad (the keyboard / real pads work again). Returns a status line.
    /// </summary>
    public static async Task<string> AutoBoot(Process p, int port, byte[] probe, Target t, Vector3? spawn, Action<string> log, CancellationToken ct)
    {
        using var pad = new VirtualPad(port);
        NB.Core.Live.XeniaLive? x = null;
        var sw = Stopwatch.StartNew();
        int presses = 0; bool inGame = false;
        try
        {
            while (!ct.IsCancellationRequested && !p.HasExited && sw.Elapsed.TotalSeconds < 300)
            {
                try { x ??= NB.Core.Live.XeniaLive.AttachPid(p.Id, probe); if (InGame(x)) { inGame = true; break; } }
                catch (Exception) { x?.Dispose(); x = null; }
                if (sw.Elapsed.TotalSeconds > 7)
                {
                    // A: title screen, house menu, "start a new game?"; Y: skips cut-scenes (and does nothing in the menus)
                    await pad.Press(presses % 2 == 0 ? VirtualPad.A : VirtualPad.Y, 150, ct);
                    presses++;
                }
                await Task.Delay(1200, ct);
            }
            if (!inGame) return p.HasExited ? "Xenia was closed before the world loaded." : $"the world did not load within {sw.Elapsed.TotalSeconds:F0} s (finish by hand).";
            if (t.IsAct)
            {
                // the act's opening dialogue: Y skips it (a second Y would make Banjo leave the vehicle)
                await Task.Delay(1500, ct);
                await pad.Press(VirtualPad.Y, 150, ct);
            }
            string where = "";
            if (spawn is { } target && x != null)
            {
                await Task.Delay(2500, ct);
                where = await MoveTo(x, pad, target, ct);
            }
            return $"in the world after {sw.Elapsed.TotalSeconds:F0} s ({presses} button presses){where}.";
        }
        catch (OperationCanceledException) { return "stopped."; }
        finally
        {
            try { pad.Unplug(); } catch (Exception) { }
            x?.Dispose();
        }
    }

    /// <summary>Shift+F5: moves the player's vehicle, or Banjo on foot, to the 3D view's camera spot.</summary>
    static async Task<string> MoveTo(NB.Core.Live.XeniaLive x, VirtualPad pad, Vector3 target, CancellationToken ct)
    {
        try
        {
            var veh = x.PlayerPosition; var cam = x.CameraPosition;
            // in a vehicle the camera follows it closely; on foot the player position is the parked vehicle (far away)
            bool driving = Vector3.Distance(veh, cam) < 25 && x.BodiesNear(veh).Any(b => Vector3.Distance(x.V3(b), veh) < 3f);
            int n = 0;
            if (driving) n = x.TeleportVehicle(target + new Vector3(0, 2, 0));
            if (n == 0)
            {
                n = x.TeleportFoot(target + new Vector3(0, 1, 0), () =>
                {
                    pad.Hold(0, ly: 0.6f, ms: 250).Wait(ct);
                    pad.Neutral();
                    Thread.Sleep(300);
                });
                if (n > 0) return $"; Banjo moved to {Fmt(target)}";
                return $"; could not move Banjo to the camera spot (body not found): start from the world's spawn";
            }
            return $"; vehicle moved to {Fmt(target)}";
        }
        catch (Exception e) { return "; moving to the camera spot failed: " + e.Message; }
        finally { await Task.CompletedTask; }
    }

    static string Fmt(Vector3 v) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"({v.X:0.#}, {v.Y:0.#}, {v.Z:0.#})");

    /// <summary>NB Xenia's virtual gamepad (cvar nb_remote_input_port): 18-byte "NBPD" UDP packets with the controller state.</summary>
    public sealed class VirtualPad : IDisposable
    {
        public const ushort A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000, Start = 0x0010;
        readonly UdpClient _u = new();
        readonly int _port;
        public VirtualPad(int port) { _port = port; }

        void Send(ushort buttons, bool connected = true, float lx = 0, float ly = 0)
        {
            short S(float v) => (short)Math.Clamp((int)MathF.Round(v * 32767), -32768, 32767);
            var p = new byte[18];
            "NBPD"u8.CopyTo(p);
            p[4] = 0; p[5] = (byte)(connected ? 1 : 0); p[6] = 0; p[7] = 0;
            BitConverter.TryWriteBytes(p.AsSpan(8), buttons);
            BitConverter.TryWriteBytes(p.AsSpan(10), S(lx)); BitConverter.TryWriteBytes(p.AsSpan(12), S(ly));
            _u.Send(p, p.Length, "127.0.0.1", _port);
        }

        public void Neutral() => Send(0);
        public void Unplug() => Send(0, connected: false);

        /// <summary>Holds buttons / the left stick for <paramref name="ms"/> (sent at 60 Hz, like a real pad).</summary>
        public async Task Hold(ushort buttons, float lx = 0, float ly = 0, int ms = 150, CancellationToken ct = default)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end) { Send(buttons, true, lx, ly); await Task.Delay(16, ct); }
        }

        public async Task Press(ushort b, int ms, CancellationToken ct)
        {
            await Hold(b, ms: ms, ct: ct);
            await Hold(0, ms: 60, ct: ct);
        }

        public void Dispose() => _u.Dispose();
    }
}
