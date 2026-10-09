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
        string key = $"v4|{srcCommon.Length}|{srcCommon.LastWriteTimeUtc.Ticks}|{t.Script}|{FirstTimeFlags.Length}|{(KeepWorldIntros ? "intros" : "")}";
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
                log("  " + NB.Core.World.TestMode.SkipStartOfGame(fw, keepShowdownTownIntro: KeepWorldIntros));
                if (!KeepWorldIntros) log("  " + NB.Core.World.TestMode.PresetTownIntro(fw));
                log("  first-visit tutorials and cut-scenes: " + NB.Core.World.TestMode.PresetFlags(fw, FirstTimeFlags.Where(f => !KeepWorldIntros || !f.StartsWith("gameFlag_Normal_Cutscene_Intro_")).Concat(PartSeenFlags(ws.Game.Xex)).Distinct()));
                log("  start script " + NB.Core.World.TestMode.StartIn(fw, t.Script));
            }
        }
        finally { fw.DeleteCache(); }
        File.WriteAllText(keyFile, key);
        return Path.Combine(dst, "default.xex");
    }

    /// <summary>
    /// Game flags every test game starts with (set by the test start script, like the Showdown Town intro steps), so the
    /// first-visit tutorials and cut-scenes don't block quick testing: Mumbo's garage tutorial (the 7
    /// Garage_Tutorial_* steps, docs/FORMATS.md §14) and its first-entry talk, the workshop / blueprint bench guides,
    /// the world intro cut-scenes of the Acts, the first meetings with the town's characters and the first-time
    /// explanations in town. Not set: story progress (game globe, world doors, SphereDocked flags: one froze the game,
    /// Seattle B18), unlocks and the garage warnings that help building (too heavy, no fuel …).
    /// </summary>
    /// <summary>Test games keep the intro cut-scenes: Showdown Town's (Mumbo and the golf cart) and the worlds' first-visit
    /// intros (script option --quicktest-intros on: camera tests). The boot then presses A only, so Y does not skip them.</summary>
    public static bool KeepWorldIntros;

    public static readonly string[] FirstTimeFlags =
    {
        "gameFlag_Normal_Garage_Tutorial_AdvancedSettings", "gameFlag_Normal_Garage_Tutorial_BuildPart1", "gameFlag_Normal_Garage_Tutorial_BuildPart2",
        "gameFlag_Normal_Garage_Tutorial_BuildShoppingTrolley", "gameFlag_Normal_Garage_Tutorial_BuildTips", "gameFlag_Normal_Garage_Tutorial_VehicleInfo",
        "gameFlag_Normal_Garage_Tutorial_VehicleMaintenance", "gameFlag_Normal_Garage_EnteringGarageForFirstTime", "gameFlag_Normal_Garage_EnteringGarageForSecondTime",
        "gameFlag_Normal_Garage_Instruction_Guides_Advanced", "gameFlag_Normal_Garage_Instruction_Guides_BuildPart1", "gameFlag_Normal_Garage_Instruction_Guides_BuildPart2",
        "gameFlag_Normal_Garage_Instruction_Guides_BuildTips", "gameFlag_Normal_Garage_Instruction_Guides_VehicleInfo", "gameFlag_Normal_Garage_Instruction_Guides_VehicleMaintenance",
        "gameFlag_Normal_Garage_Instruction_ChooseBench_Workshop", "gameFlag_Normal_Garage_Instruction_ChooseBench_Paints", "gameFlag_Normal_Garage_Instruction_ChooseBench_Database",
        "gameFlag_Normal_Garage_Instruction_ChooseBench_TestTrack", "gameFlag_Normal_Garage_Instruction_ChooseBench_Exit",
        "gameFlag_Normal_Garage_Instruction_Blueprints_Load", "gameFlag_Normal_Garage_Instruction_Blueprints_Save", "gameFlag_Normal_Garage_Instruction_Blueprints_New",
        "gameFlag_Normal_Garage_Instruction_Blueprints_Delete", "gameFlag_Normal_Garage_Instruction_Blueprints_Templates", "gameFlag_Normal_Garage_Instruction_Blueprints_Guides",
        "gameFlag_Normal_Garage_Instruction_Paint_EnteredPaintShop", "gameFlag_Normal_Garage_Instruction_Edit_EnteredPickup", "gameFlag_Normal_Garage_Instruction_PartsShop_EnterModifyMode",
        "gameFlag_Normal_Cutscene_Intro_NuttyAcres", "gameFlag_Normal_Cutscene_Intro_Banjoland", "gameFlag_Normal_Cutscene_Intro_CPU",
        "gameFlag_Normal_Cutscene_Intro_Terrorium", "gameFlag_Normal_Cutscene_Intro_WorldOfSport", "gameFlag_Normal_Cutscene_Intro_WeirdWest",
        "gameFlag_Normal_Cutscene_First_JiggyBanked",
        "gameFlag_Normal_ShowdownTown_MetMumbo", "gameFlag_Normal_ShowdownTown_MetHumba", "gameFlag_Normal_ShowdownTown_MetBottles", "gameFlag_Normal_ShowdownTown_MetJolly",
        "gameFlag_Normal_ShowdownTown_MetMrFit", "gameFlag_Normal_ShowdownTown_MetKlungo", "gameFlag_Normal_ShowdownTown_MetPikelet", "gameFlag_Normal_ShowdownTown_MetBoggy",
        "gameFlag_Normal_ShowdownTown_MetBlubber", "gameFlag_Normal_ShowdownTown_MetKingJingaling", "gameFlag_Normal_ShowdownTown_MetThomas",
        "gameFlag_Normal_ShowdownTown_Jigovend_FirstTimeLockedOnDlg", "gameFlag_Normal_ShowdownTown_Jigovend_FirstTimeWithinRangeDlg",
        "gameFlag_Normal_PlayerInstructions_FirstComponentsInCrate", "gameFlag_Normal_PlayerInstructions_InGameEditorFirstUse", "gameFlag_Normal_PlayerInstructions_InVehicleRepair",
    };

    /// <summary>
    /// The garage's "first time you see this part / part group" flags (gameFlag_Normal_Garage_Group_Seen_* and
    /// _PartsShop_Seen_*: Mumbo explains every part the first time the cursor reaches it), read from the executable's
    /// flag-name table (about 200 names), so the test garage is quiet.
    /// </summary>
    static IEnumerable<string> PartSeenFlags(string xexPath)
    {
        byte[] img;
        try { img = NB.Core.Formats.XexFile.Read(File.ReadAllBytes(xexPath)).GetImage(); }
        catch (Exception) { yield break; }
        var text = System.Text.Encoding.Latin1.GetString(img);
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"gameFlag_Normal_Garage_(?:Group_Seen|PartsShop_Seen)_[A-Za-z0-9_]+(?=\0)"))
            if (m.Value.Length < 64) yield return m.Value;
    }

    /// <summary>The two game save slots (the blueprints are separate packages 0x00000001…: docs, nb-garage-research).</summary>
    static readonly string[] SaveSlots = { "0b0a5c5c", "0b0d6cca" };

    /// <summary>
    /// Before a test: removes the test storage's game save (the two save slots), so SINGLE PLAYER starts a NEW game that
    /// the test start script sends to the open world. Blueprints saved in Mumbo's garage during earlier tests are their
    /// own content packages and stay (<paramref name="keepBlueprints"/> false, Settings "Fresh save for every test",
    /// removes them too). Only the test storage is touched, never NB Multiplayer's or Xenia's normal saves.
    /// </summary>
    public static void ClearSaves(string contentRoot, bool keepBlueprints = true)
    {
        if (!Directory.Exists(contentRoot)) return;
        foreach (var user in Directory.GetDirectories(contentRoot))
        {
            var game = Path.Combine(user, NB.Core.Mods.ExePatches.TitleId.ToString("X8"));
            if (!Directory.Exists(game)) continue;
            if (!keepBlueprints) { Directory.Delete(game, true); continue; }
            foreach (var slot in SaveSlots)
            {
                var pkg = Path.Combine(game, "00000001", "0x" + slot);
                if (Directory.Exists(pkg)) Directory.Delete(pkg, true);
                var hdr = Path.Combine(game, "Headers", "00000001", "0x" + slot + ".header");
                if (File.Exists(hdr)) File.Delete(hdr);
            }
        }
    }

    /// <summary>Blueprints saved in the test storage (their display names, from the package headers: "VEHICLE: NAME").</summary>
    public static List<string> TestBlueprints(string contentRoot)
    {
        var res = new List<string>();
        if (!Directory.Exists(contentRoot)) return res;
        foreach (var user in Directory.GetDirectories(contentRoot))
        {
            var root = Path.Combine(user, NB.Core.Mods.ExePatches.TitleId.ToString("X8"), "00000001");
            if (!Directory.Exists(root)) continue;
            foreach (var pkg in Directory.GetDirectories(root))
            {
                var name = Path.GetFileName(pkg);
                if (name.Length != 10 || SaveSlots.Contains(name[2..]) || !File.Exists(Path.Combine(pkg, name[2..]))) continue;
                var hdr = Path.Combine(user, NB.Core.Mods.ExePatches.TitleId.ToString("X8"), "Headers", "00000001", name + ".header");
                string display = name;
                try
                {
                    if (File.Exists(hdr)) { var h = File.ReadAllBytes(hdr); if (h.Length > 0x411 + 0x80) display = System.Text.Encoding.BigEndianUnicode.GetString(h, 0x411, 0x80).TrimEnd('\0'); }
                }
                catch (IOException) { }
                res.Add(display);
            }
        }
        return res;
    }

    /// <summary>Build > Reset Test Save: removes everything the test games saved for this workspace (save and blueprints);
    /// the test profile and Xenia settings stay.</summary>
    public static void ResetTestSave(Workspace ws)
    {
        ClearSaves(Path.Combine(Folder(ws), "xenia", "content"), keepBlueprints: false);
        ClearSaves(Path.Combine(Folder(ws), "content"), keepBlueprints: false);
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
                    await pad.Press(presses % 2 == 0 || KeepWorldIntros ? VirtualPad.A : VirtualPad.Y, 150, ct);
                    presses++;
                }
                await Task.Delay(1200, ct);
            }
            if (!inGame) return p.HasExited ? "Xenia was closed before the world loaded." : $"the world did not load within {sw.Elapsed.TotalSeconds:F0} s (finish by hand).";
            if (t.IsAct && !KeepWorldIntros)
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
