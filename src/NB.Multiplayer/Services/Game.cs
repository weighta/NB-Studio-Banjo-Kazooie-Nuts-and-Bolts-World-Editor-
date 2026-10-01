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
    public static Process Start(AppSettings s, Edition edition, string apiHostPort, string roomCode)
    {
        if (!File.Exists(XeniaExe)) throw new FileNotFoundException("The Xenia build is missing next to NB Multiplayer (xenia folder). Reinstall.", XeniaExe);
        Directory.CreateDirectory(AppSettings.DataDir);
        var args = new List<string>();
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
        string cmd = string.Join(" ", new[] { XeniaExe }.Concat(args).Select(Quote));
        // Started by Windows (WMI), not as a child of this app: once this app uses the Steam API, the Steam overlay hooks
        // the processes it starts, and the overlay in Xenia leaves the game window black.
        using var cls = new System.Management.ManagementClass("Win32_Process");
        var inParams = cls.GetMethodParameters("Create");
        inParams["CommandLine"] = cmd;
        inParams["CurrentDirectory"] = Path.GetDirectoryName(XeniaExe);
        var result = cls.InvokeMethod("Create", inParams, null);
        uint rc = (uint)result["ReturnValue"];
        if (rc != 0) throw new InvalidOperationException($"Windows could not start Xenia (WMI error {rc}).");
        return Process.GetProcessById((int)(uint)result["ProcessId"]);
    }

    /// <summary>Windows command-line quoting for one argument.</summary>
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

    public static bool IsRunning() => Process.GetProcessesByName("xenia_canary_netplay").Any(p =>
    {
        try { return string.Equals(p.MainModule?.FileName, XeniaExe, StringComparison.OrdinalIgnoreCase); } catch (Exception) { return false; }
    });
}

/// <summary>What a room server reports (GET /nb/room).</summary>
public sealed record RoomInfo(string Name, string HostXuid, string Edition, List<(string Xuid, string Name)> Players, CompatProfile? Compat);

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
            return new RoomInfo(j["name"]?.GetValue<string>() ?? "", j["host"]?.GetValue<string>() ?? "",
                j["edition"]?.GetValue<string>() ?? Editions.VanillaName, players, compat);
        }
        catch (Exception) { return null; }
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
