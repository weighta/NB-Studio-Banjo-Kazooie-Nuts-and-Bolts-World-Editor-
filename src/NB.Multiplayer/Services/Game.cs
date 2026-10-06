using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using NB.Core.Net;

namespace NB.Multiplayer.Services;

/// <summary>Starts the NB Xenia build for a room.</summary>
public static class GameLauncher
{
    public static string XeniaExe => Path.Combine(AppContext.BaseDirectory, "xenia", "xenia_canary_netplay.exe");

    /// <summary>Starts the game for a room; co-op editions run single-player (NB Multiplayer syncs the players).</summary>
    /// <param name="allUnlocked">Co-op rooms: use the all-unlocked save for this game (the room's setting), whatever the
    /// player's own setting says; null = the player's setting.</param>
    public static Process Start(AppSettings s, Edition edition, string apiHostPort, string roomCode, bool? allUnlocked = null)
    {
        Notice = null;
        // reNut (Settings > Game engine) runs co-op rooms; the game's own Xbox LIVE modes need Xenia's networking
        if (s.UseRenut && edition.IsCoop)
        {
            if (Renut.Problem(s) is { } why) throw new InvalidOperationException(why);
            Directory.CreateDirectory(AppSettings.DataDir);
            KeepBlueprints(renut: true);
            Editions.EnsureSafety(edition.GameDir);
            Saves.PrepareLaunch(allUnlocked ?? s.UseAllUnlockedSave, forRoom: allUnlocked == true && !s.UseAllUnlockedSave, renut: true);
            return Renut.Start(s, edition.GameDir, AlwaysOn);
        }
        if (s.UseRenut) Notice = "This room plays the game's own Xbox LIVE modes, which need Xenia's networking: it runs in Xenia (reNut is used for co-op rooms and solo games).";
        if (!File.Exists(XeniaExe)) throw new FileNotFoundException("The Xenia build is missing next to NB Multiplayer (xenia folder). Reinstall.", XeniaExe);
        Directory.CreateDirectory(AppSettings.DataDir);
        KeepBlueprints();
        // editions carry their tweaks in their own default.xex (plus the parts safety, added here to older editions); the
        // player's own game folder (Vanilla) is never changed: Xenia applies the safety in memory
        if (edition.IsVanilla) SetProjectTweaks(Path.Combine(edition.GameDir, "default.xex"), AlwaysOn);
        else { Editions.EnsureSafety(edition.GameDir); SetProjectTweaks(null, null); }
        Saves.PrepareLaunch(allUnlocked ?? s.UseAllUnlockedSave, forRoom: allUnlocked == true && !s.UseAllUnlockedSave);
        var args = new List<string>(XeniaGameOptions);
        var extra = Environment.GetEnvironmentVariable("NB_XENIA_EXTRA");   // testing: extra Xenia options
        if (!string.IsNullOrWhiteSpace(extra)) args.AddRange(extra.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        int apiPort = int.TryParse(apiHostPort.Split(':').ElementAtOrDefault(1), out var ap) ? ap : Net.Port;
        if (edition.IsCoop)
            args.AddRange(new[]
            {
                $"--storage_root={AppSettings.DataDir}", $"--log_file={Path.Combine(AppSettings.DataDir, "xenia.log")}",
                "--network_mode=0", $"--nb_create_profile={s.PlayerName}", Path.Combine(edition.GameDir, "default.xex"),
            });
        else args.AddRange(new[]
        {
            $"--storage_root={AppSettings.DataDir}", $"--log_file={Path.Combine(AppSettings.DataDir, "xenia.log")}",
            "--network_mode=2", $"--api_address=http://{apiHostPort}/", "--nb_overlay=true", $"--nb_instance={s.Instance}",
            $"--nb_relay={apiHostPort.Split(':')[0]}:{apiPort + 1}",
            $"--nb_create_profile={s.PlayerName}", $"--nb_room_code={roomCode}", Path.Combine(edition.GameDir, "default.xex"),
        });
        return StartDetached(XeniaExe, args, Path.GetDirectoryName(XeniaExe)!);
    }

    /// <summary>
    /// Plays a game folder on its own (no room): an edition, or an NB Studio project whose executable tweaks
    /// (<paramref name="projectExeMods"/>) are applied by Xenia in memory, as NB Studio does when it launches a workspace.
    /// </summary>
    public static Process StartSolo(AppSettings s, string gameDir, IReadOnlyList<string>? projectExeMods = null)
    {
        Notice = null;
        if (s.UseRenut)
        {
            if (Renut.Problem(s) is { } why) throw new InvalidOperationException(why);
            Directory.CreateDirectory(AppSettings.DataDir);
            KeepBlueprints(renut: true);
            Editions.EnsureSafety(gameDir);   // only NB Multiplayer's own editions are changed
            Saves.PrepareLaunch(s.UseAllUnlockedSave, renut: true);
            // a project's tweaks (and the always-on fixes) go into the running game; editions already carry theirs
            return Renut.Start(s, gameDir, (projectExeMods ?? Array.Empty<string>()).Concat(AlwaysOn).Distinct().ToList());
        }
        if (!File.Exists(XeniaExe)) throw new FileNotFoundException("The Xenia build is missing next to NB Multiplayer (xenia folder). Reinstall.", XeniaExe);
        Directory.CreateDirectory(AppSettings.DataDir);
        var xex = Path.Combine(gameDir, "default.xex");
        KeepBlueprints();
        Editions.EnsureSafety(gameDir);   // only NB Multiplayer's own editions are changed
        SetProjectTweaks(xex, (projectExeMods ?? Array.Empty<string>()).Concat(AlwaysOn).Distinct().ToList());
        Saves.PrepareLaunch(s.UseAllUnlockedSave);
        var args = new List<string>(XeniaGameOptions);
        var extra = Environment.GetEnvironmentVariable("NB_XENIA_EXTRA");
        if (!string.IsNullOrWhiteSpace(extra)) args.AddRange(extra.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        args.AddRange(new[]
        {
            $"--storage_root={AppSettings.DataDir}", $"--log_file={Path.Combine(AppSettings.DataDir, "xenia.log")}",
            "--network_mode=0", $"--nb_create_profile={s.PlayerName}", xex,
        });
        return StartDetached(XeniaExe, args, Path.GetDirectoryName(XeniaExe)!);
    }

    /// <summary>
    /// Xenia options every game gets. readback_resolve: the game's Take Photo (pause menu > Photos &amp; Videos) renders the
    /// picture, resolves it into a texture and reads that texture on the CPU to make the JPEG and the preview. The NB Xenia
    /// build defaults to no readback ("none"), so photos came out black; "fast" copies resolves back to game memory one
    /// frame late, without stalling the GPU (Xenia Canary's own default since December 2025).
    /// </summary>
    public static readonly string[] XeniaGameOptions = { "--readback_resolve=fast" };

    /// <summary>
    /// Xenia loads patch files from &lt;storage root&gt;\patches for every game with a matching executable. A project's
    /// tweaks are written there only for that launch; any other launch removes them again (so Vanilla stays vanilla).
    /// </summary>
    /// <summary>Game-code fixes every game gets: blueprints with parts the game lacks (e.g. ULTRA Parts) never crash it.</summary>
    public static readonly string[] AlwaysOn = { "unknown-parts-safe" };

    /// <summary>
    /// The player's blueprint library is kept safe (BlueprintVault): every blueprint seen is copied into the vault, and
    /// ones missing from the profile the game will sign in are put back. Never blocks a launch.
    /// </summary>
    public static void KeepBlueprints(bool renut = false)
    {
        try
        {
            BlueprintVault.Harvest(StudioContentRoots());
            if (renut) { Directory.CreateDirectory(Renut.ProfileDir); BlueprintVault.RestoreInto(Renut.ProfileDir); }
            else if (Saves.ActiveProfile() is { } p) BlueprintVault.RestoreInto(p);
        }
        catch (Exception) { }
    }

    /// <summary>A note for the player about the last launch (e.g. a room that had to use Xenia), or null.</summary>
    public static string? Notice { get; private set; }

    /// <summary>NB Studio's Xenia content folder (its F5 / Try it launches use that Xenia's own storage), read only.</summary>
    public static IEnumerable<string> StudioContentRoots()
    {
        string? xenia = null;
        try
        {
            var f = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NBModTool", "settings.json");
            if (File.Exists(f)) xenia = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(f))?["XeniaPath"]?.GetValue<string>();
        }
        catch (Exception) { }
        var dir = xenia != null && File.Exists(xenia) ? Path.GetDirectoryName(xenia) : null;
        if (dir != null && File.Exists(Path.Combine(dir, "portable.txt"))) yield return Path.Combine(dir, "content");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Xenia", "content");
    }

    static void SetProjectTweaks(string? xex, IReadOnlyList<string>? ids)
    {
        var mods = ids == null ? new List<NB.Core.Mods.ExeMod>() : NB.Core.Mods.ExePatches.ResolveAll(ids);
        ulong hash = 0;
        if (mods.Count > 0 && xex != null)
        {
            var xb = File.ReadAllBytes(xex);
            var img = NB.Core.Formats.XexFile.Read(xb).GetImage();
            hash = NB.Core.Mods.ExePatches.ResolveXeniaHash(NB.Core.Mods.ExePatches.XeniaModuleHash(xb, img), AppSettings.DataDir) ?? 0;
            if (hash == 0) mods.Clear();
        }
        NB.Core.Mods.ExePatches.WriteXeniaPatchFile(AppSettings.DataDir, hash, mods);
    }

    /// <summary>
    /// Starts a program through Windows (WMI), not as a child of this app: once this app uses the Steam API, the Steam
    /// overlay hooks the processes it starts, and the overlay in Xenia leaves the game window black.
    /// </summary>
    public static Process StartDetached(string exe, IEnumerable<string> args, string workingDir)
    {
        string cmd = string.Join(" ", new[] { exe }.Concat(args).Select(Quote));
        using var cls = new System.Management.ManagementClass("Win32_Process");
        var inParams = cls.GetMethodParameters("Create");
        inParams["CommandLine"] = cmd;
        inParams["CurrentDirectory"] = workingDir;
        var result = cls.InvokeMethod("Create", inParams, null);
        uint rc = (uint)result["ReturnValue"];
        if (rc != 0) throw new InvalidOperationException($"Windows could not start {Path.GetFileName(exe)} (WMI error {rc}).");
        return Process.GetProcessById((int)(uint)result["ProcessId"]);
    }

    /// <summary>Windows command-line quoting for one argument.</summary>
    public static string QuoteArg(string a) => Quote(a);

    static string Quote(string a)
    {
        if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
        var sb = new System.Text.StringBuilder("\"");
        int slashes = 0;
        foreach (char c in a)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { sb.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
            sb.Append('\\', slashes).Append(c); slashes = 0;
        }
        return sb.Append('\\', slashes * 2).Append('"').ToString();
    }

    public static bool IsRunning(AppSettings? s = null) => Process.GetProcessesByName("xenia_canary_netplay").Any(p =>
    {
        try { return string.Equals(p.MainModule?.FileName, XeniaExe, StringComparison.OrdinalIgnoreCase); } catch (Exception) { return false; }
    }) || (s != null && Renut.IsRunning(s));
}

/// <summary>What a room server reports (GET /nb/room).</summary>
/// <param name="Recipe">The mods of the host's edition (empty = Vanilla); null from hosts older than NB Multiplayer 1.2.</param>
/// <param name="Coop">Co-op room settings (null: not a co-op room, or a host older than NB Multiplayer 1.7).</param>
public sealed record RoomInfo(string Name, string HostXuid, string Edition, List<(string Xuid, string Name)> Players, CompatProfile? Compat,
    List<NB.Core.Project.RecipeMod>? Recipe = null, CoopRoomSettings? Coop = null, List<string>? HostBaseModified = null);

public static class Net
{
    /// <summary>Room port (TCP; relay UDP = +1, co-op UDP = +2). NB_ROOM_PORT overrides it (tests next to a running room).</summary>
    public static readonly int Port = int.TryParse(Environment.GetEnvironmentVariable("NB_ROOM_PORT"), out var p) ? p : 36000;

    public static bool PortInUse(int port) =>
        System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port);

    /// <summary>IPv4 addresses of network adapters that lead somewhere (have a default gateway): the home network.
    /// Virtual adapters (Hyper-V, VirtualBox, VPN host-only) are left out unless nothing else exists.</summary>
    public static List<string> LocalIPv4()
    {
        var up = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback).ToList();
        static IEnumerable<string> V4(NetworkInterface n) => n.GetIPProperties().UnicastAddresses
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254."))
            .Select(a => a.Address.ToString());
        var routed = up.Where(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
            .SelectMany(V4).Distinct().ToList();
        return routed.Count > 0 ? routed : up.SelectMany(V4).Distinct().ToList();
    }

    /// <summary>The room code in <paramref name="text"/> (upper case), or "".</summary>
    public static string ExtractCode(string text)
    {
        var st = System.Text.RegularExpressions.Regex.Match(text, @"NBS-?[0-9A-Za-z]{4}-?[0-9A-Za-z]{4}-?[0-9A-Za-z]{4}-?[0-9A-Za-z]{5}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (st.Success && RoomCode.TryDecodeSteam(st.Value, out _, out _)) return st.Value.ToUpperInvariant();
        var m = System.Text.RegularExpressions.Regex.Match(text, @"NB-?[0-9A-Za-z]{4}-?[0-9A-Za-z]{4}-?[0-9A-Za-z]{6}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success && RoomCode.TryDecode(m.Value, out _, out _, out _) ? m.Value.ToUpperInvariant() : "";
    }

    /// <summary>Accepts "NB-XXXX-XXXX-XXXXX", "1.2.3.4", "1.2.3.4:36000" or "host.name:port".</summary>
    public static bool TryParseTarget(string text, out string host, out int port)
    {
        host = ""; port = Port;
        text = text.Trim();
        if (text.Length == 0) return false;
        // a room code anywhere in what was pasted ("Join me: NB-C1A7-YP26-A13D4W!")
        var m = System.Text.RegularExpressions.Regex.Match(text, @"NB-?[0-9A-Za-z]{4}-?[0-9A-Za-z]{4}-?[0-9A-Za-z]{6}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success && RoomCode.TryDecode(m.Value, out var ip, out port, out _)) { host = ip.ToString(); return true; }
        port = Port;
        var parts = text.Split(':');
        if (parts.Length > 2) return false;
        if (parts.Length == 2 && !int.TryParse(parts[1], out port)) return false;
        host = parts[0];
        return IPAddress.TryParse(host, out _) || Uri.CheckHostName(host) == UriHostNameType.Dns;
    }

    public static async Task<RoomInfo?> GetRoomAsync(string host, int port, TimeSpan timeout)
    {
        try
        {
            using var http = new HttpClient { Timeout = timeout };
            var j = JsonNode.Parse(await http.GetStringAsync($"http://{host}:{port}/nb/room"))!;
            var players = new List<(string, string)>();
            if (j["players"] is JsonArray a)
                foreach (var p in a) players.Add((p?["xuid"]?.GetValue<string>() ?? "", p?["gamertag"]?.GetValue<string>() ?? "?"));
            CompatProfile? compat = null;
            if ((j["fingerprint"]?.GetValue<string>() ?? "") is { Length: > 0 } fp)
            {
                compat = new CompatProfile { Fingerprint = fp };
                if (j["files"] is JsonObject fo) foreach (var (k, v) in fo) compat.Files[k] = v!.GetValue<string>();
            }
            List<NB.Core.Project.RecipeMod>? recipe = null;
            if (j["recipe"] is JsonArray ra)
                recipe = ra.Select(m => new NB.Core.Project.RecipeMod(m?["id"]?.GetValue<string>() ?? "", m?["name"]?.GetValue<string>() ?? "",
                    m?["version"]?.GetValue<string>() ?? "", (m?["sha256"]?.GetValue<string>() ?? "").ToLowerInvariant(), m?["size"]?.GetValue<long>() ?? 0)).ToList();
            CoopRoomSettings? coop = null;
            if (j["coop"] is JsonObject co)
                coop = new CoopRoomSettings { Protocol = co["protocol"]?.GetValue<int>() ?? 0, TimeOfDay = co["timeOfDay"]?.GetValue<int>() ?? 0,
                                              AllUnlockedSave = co["allUnlocked"]?.GetValue<bool>() ?? false };
            List<string>? baseMod = j["baseModified"] is JsonArray bm ? bm.Select(x => x?.GetValue<string>() ?? "").ToList() : null;
            return new RoomInfo(j["name"]?.GetValue<string>() ?? "", j["host"]?.GetValue<string>() ?? "",
                j["edition"]?.GetValue<string>() ?? Editions.VanillaName, players, compat, recipe, coop, baseMod);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Downloads one mod of the host's edition from the room (checked against its SHA-256) into the mod library.</summary>
    public static async Task<NB.Core.Project.ModStack.Mod> DownloadModAsync(string host, int port, NB.Core.Project.RecipeMod mod, IProgress<double>? progress = null)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        using var resp = await http.GetAsync($"http://{host}:{port}/nb/mods/{mod.Sha256}", HttpCompletionOption.ResponseHeadersRead);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"the host could not send \"{mod.Name}\" ({(int)resp.StatusCode})");
        long total = resp.Content.Headers.ContentLength ?? mod.Size;
        await using var src = await resp.Content.ReadAsStreamAsync();
        var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await src.ReadAsync(buf)) > 0) { ms.Write(buf, 0, n); if (total > 0) progress?.Report((double)ms.Length / total); }
        return ModLibrary.AddDownloaded(ms.ToArray(), mod.Sha256);
    }

    /// <summary>Can this PC connect to its own room server through <paramref name="ip"/>? (Fails when a firewall such as
    /// Portmaster or Windows Firewall blocks incoming connections.)</summary>
    public static async Task<bool> CanReachSelfAsync(string ip, int port)
    {
        try
        {
            using var c = new TcpClient();
            var t = c.ConnectAsync(ip, port);
            return await Task.WhenAny(t, Task.Delay(3000)) == t && c.Connected;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Asks api.ipify.org for this network's public address (only when the player clicks for it).</summary>
    public static async Task<string?> PublicIpAsync()
    {
        try { using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) }; return (await http.GetStringAsync("https://api.ipify.org")).Trim(); }
        catch (Exception) { return null; }
    }

    /// <summary>Asks canyouseeme.org whether <paramref name="ip"/>:<paramref name="port"/> is reachable from the internet.</summary>
    public static async Task<bool?> InternetCheckAsync(string ip, int port)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var resp = await http.PostAsync("https://canyouseeme.org/", new FormUrlEncodedContent(new Dictionary<string, string> { ["IP"] = ip, ["port"] = port.ToString() }));
            var html = await resp.Content.ReadAsStringAsync();
            if (html.Contains("Success:", StringComparison.OrdinalIgnoreCase)) return true;
            if (html.Contains("Error:", StringComparison.OrdinalIgnoreCase)) return false;
            return null;
        }
        catch (Exception) { return null; }
    }
}
