using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NB.Core.Net;

/// <summary>
/// Self-hosted, in-memory replacement for the "Xenia Web Services" REST API (xenia-webservices, NestJS + MongoDB) that the
/// Xenia Canary netplay fork talks to (XLiveAPI.cpp). The session host runs this; every Xenia (host included) sets
/// <c>api_address = "http://&lt;host&gt;:36000/"</c>. Routes, JSON field names, hex-id formats and status codes mirror the
/// reference controllers: POST answers 201, GET 200, DELETE 200, errors are Nest-style <c>{message,error,statusCode}</c>.
/// Differences: no database (sessions expire 1 h, players 24 h after their last update, like the Mongo TTLs), no XStorage
/// (the client then falls back to its local xstorage folder), no stats persistence (leaderboard reads return the
/// reference's "no data" placeholder rows), and callers on this machine are reported with a LAN / advertised address
/// instead of 127.0.0.1 so the host's sessions are reachable by friends.
/// </summary>
public sealed class RoomServer : IDisposable
{
    /// <summary>Receives one line per request (method, path, status, body size) plus notes on what changed.</summary>
    public Action<string>? Log { get; set; }
    /// <summary>Raised (on a server thread) after any request that may have changed players or sessions, and after expiry.</summary>
    public event Action? Changed;
    /// <summary>Address reported to callers on this machine (loopback or any local interface) by /whoami etc.; null = auto (loopback -> primary LAN IPv4).</summary>
    public string? AdvertiseAddress { get; set; }
    /// <summary>Skip the property/XLast filters in POST sessions/search (slots / matchmaking flag checks still apply).</summary>
    public bool RelaxedSearch { get; set; }
    public bool IsRunning => _app != null;
    public int Port { get; private set; }
    public string BindAddress { get; private set; } = "";
    /// <summary>What a caller on this machine is told its address is.</summary>
    public string LocalCallerAddress => AdvertiseAddress is { Length: > 0 } a ? a : _lanAddress;

    static readonly TimeSpan SessionTtl = TimeSpan.FromHours(1), PlayerTtl = TimeSpan.FromDays(1);
    static readonly JsonSerializerOptions J = new() { PropertyNamingPolicy = null, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly object _gate = new();
    readonly OrderedDictionary<string, PlayerState> _players = new(StringComparer.OrdinalIgnoreCase);
    readonly OrderedDictionary<string, SessionState> _sessions = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, byte[]> _qos = new(StringComparer.OrdinalIgnoreCase);
    WebApplication? _app;
    Timer? _expiry;
    RoomRelay? _relay;
    // NB overlay: virtual addresses 10.77.0.N handed out per (real caller IP, X-NB-Instance)
    readonly Dictionary<string, uint> _vipByCaller = new();
    readonly Dictionary<uint, IPAddress> _vipOwner = new();
    uint _nextVip = 0x0A4D0001;

    /// <summary>Shown to joining players (GET /nb/room) with <see cref="HostCompat"/>.</summary>
    public string RoomName { get; set; } = "NB Studio room";
    /// <summary>Game edition the host plays ("Vanilla" or a patch name); joiners pick the same edition.</summary>
    public string Edition { get; set; } = "Vanilla";
    /// <summary>The host's game files; joining NB Studios compare theirs before launching (GET /nb/room).</summary>
    public CompatProfile? HostCompat { get; set; }
    /// <summary>The mods of the host's edition, in order (empty = Vanilla; null = not stated, older hosts). Joiners
    /// build the same edition and download mods they lack from <see cref="ModFile"/> (GET /nb/mods/&lt;sha256&gt;).</summary>
    public List<NB.Core.Project.RecipeMod>? Recipe { get; set; }
    /// <summary>The host's .nbpatch file for a SHA-256 of the recipe (null = not available).</summary>
    public Func<string, string?>? ModFile { get; set; }
    /// <summary>Showdown Town co-op rooms: the settings every player's game uses (null = not a co-op room).</summary>
    public CoopRoomSettings? Coop { get; set; }

    /// <summary>The UDP relay of the overlay (port + 1), null when not running.</summary>
    public RoomRelay? Relay => _relay;
    /// <summary>Virtual address -> the real IP that owns it.</summary>
    public IReadOnlyDictionary<string, string> VirtualAddresses { get { lock (_gate) return _vipOwner.ToDictionary(kv => RoomRelay.VipText(kv.Key), kv => kv.Value.ToString()); } }
    public IPAddress? VirtualAddressOwner(uint vip) { lock (_gate) return _vipOwner.TryGetValue(vip, out var a) ? a : null; }
    HashSet<IPAddress> _ownAddresses = new();
    string _lanAddress = "127.0.0.1";

    public IReadOnlyList<RoomPlayer> Players { get { lock (_gate) return _players.Values.Select(p => p.Snapshot()).ToList(); } }
    public IReadOnlyList<RoomSession> Sessions { get { lock (_gate) return _sessions.Values.Select(s => s.Snapshot(_qos.ContainsKey(s.Key))).ToList(); } }

    /// <summary>Starts Kestrel on <paramref name="bindAddress"/>:<paramref name="port"/> (throws if the port is taken).</summary>
    public void Start(int port = 36000, string bindAddress = "0.0.0.0")
    {
        if (_app != null) throw new InvalidOperationException("room server is already running");
        var ip = bindAddress is "" or "*" ? IPAddress.Any : bindAddress == "localhost" ? IPAddress.Loopback : IPAddress.Parse(bindAddress);
        DetectLocalAddresses();

        var b = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ApplicationName = "NB.RoomServer", ContentRootPath = AppContext.BaseDirectory, Args = [] });
        b.Logging.ClearProviders();
        b.Logging.AddProvider(new LogForwarder(this));
        b.Logging.SetMinimumLevel(LogLevel.Warning);
        b.Services.AddSingleton<IHostLifetime, NoLifetime>();   // Start/Stop are ours; don't hook Ctrl+C / process exit
        b.WebHost.ConfigureKestrel(k => { k.Listen(ip, port); k.Limits.MaxRequestBodySize = 16 << 20; k.AddServerHeader = false; });
        var app = b.Build();
        app.Use(Pipeline);
        MapRoutes(app);
        app.StartAsync().GetAwaiter().GetResult();
        _app = app; Port = port; BindAddress = ip.ToString();
        _relay = new RoomRelay(VirtualAddressOwner, a => _ownAddresses.Contains(a)) { Log = Emit };
        try { _relay.Start(port + 1, ip); }
        catch (SocketException e) { Emit($"relay: cannot listen on udp {port + 1}: {e.Message}"); _relay = null; }
        _expiry = new Timer(_ => Expire(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        Emit($"room server listening on http://{BindAddress}:{port}/ - Xenia: api_address = \"http://<this PC>:{port}/\"");
        Emit($"callers on this machine are reported as {LocalCallerAddress}" + (AdvertiseAddress == null ? " (auto; override with an advertise address)" : ""));
    }

    public void Stop()
    {
        var app = _app; if (app == null) return;
        _app = null;
        _expiry?.Dispose(); _expiry = null;
        _relay?.Stop(); _relay = null;
        try { app.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(); } catch (Exception e) { Emit($"stop: {e.Message}"); }
        ((IDisposable)app).Dispose();
        Emit("room server stopped");
    }

    public void Dispose() => Stop();

    /// <summary>Forgets every player, session and QoS blob.</summary>
    public void Clear() { lock (_gate) { _players.Clear(); _sessions.Clear(); _qos.Clear(); _vipByCaller.Clear(); _vipOwner.Clear(); _nextVip = 0x0A4D0001; } RaiseChanged(); }

    void Emit(string s) => Log?.Invoke($"{DateTime.Now:HH:mm:ss.fff} {s}");

    // ---------------------------------------------------------------- pipeline

    async Task Pipeline(HttpContext c, RequestDelegate next)
    {
        var sw = Stopwatch.StartNew();
        string method = c.Request.Method, path = c.Request.Path + c.Request.QueryString;
        try
        {
            await next(c);
            if ((c.GetEndpoint() == null || c.Response.StatusCode == 405) && !c.Response.HasStarted)
            {
                var body = await ReadBody(c);
                Emit($"  ? unhandled {method} {path} body {body.Length} B" + (body.Length > 0 ? $": {Preview(body)}" : ""));
                await Error(c, 404, $"Cannot {method} {c.Request.Path}");
            }
        }
        catch (ApiException e) { if (!c.Response.HasStarted) await Error(c, e.Status, e.Message); Emit($"  ! {e.Message}"); }
        catch (JsonException e) { if (!c.Response.HasStarted) await Error(c, 400, e.Message); Emit($"  ! bad JSON: {e.Message}"); }
        catch (Exception e)
        {
            Emit($"  ! {e.GetType().Name}: {e.Message}");
            if (!c.Response.HasStarted) await Json(c, 500, new { statusCode = 500, message = "Internal server error" });
        }
        long inLen = c.Items.TryGetValue("in", out var n) && n is int len ? len : c.Request.ContentLength ?? 0;
        Emit($"{ClientAddressRaw(c)} {method} {path} -> {c.Response.StatusCode} ({inLen} B in, {sw.ElapsedMilliseconds} ms)");
        if (method is not ("GET" or "HEAD")) RaiseChanged();
    }

    void RaiseChanged() { try { Changed?.Invoke(); } catch (Exception e) { Emit($"Changed handler: {e.Message}"); } }

    sealed class ApiException(int status, string message) : Exception(message) { public int Status { get; } = status; }
    static ApiException NotFound(string m) => new(404, m);

    static string ErrorName(int s) => s switch { 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 413 => "Payload Too Large", _ => "Error" };
    static Task Error(HttpContext c, int status, string message) => Json(c, status, new { message, error = ErrorName(status), statusCode = status });

    static async Task Json(HttpContext c, int status, object body)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), J);
        c.Response.StatusCode = status;
        c.Response.ContentType = "application/json; charset=utf-8";
        c.Response.ContentLength = bytes.Length;
        if (!HttpMethods.IsHead(c.Request.Method)) await c.Response.Body.WriteAsync(bytes);
    }

    static Task Empty(HttpContext c, int status) { c.Response.StatusCode = status; if (status != 204) c.Response.ContentLength = 0; return Task.CompletedTask; }

    static async Task<byte[]> ReadBody(HttpContext c)
    {
        using var ms = new MemoryStream();
        await c.Request.Body.CopyToAsync(ms);
        var b = ms.ToArray();
        c.Items["in"] = b.Length;
        return b;
    }

    static async Task<JsonNode> JsonBody(HttpContext c)
    {
        var b = await ReadBody(c);
        int end = b.Length; while (end > 0 && b[end - 1] == 0) end--;   // tolerate a trailing NUL from C strings
        return (end == 0 ? null : JsonNode.Parse(b.AsSpan(0, end))) ?? new JsonObject();
    }

    static string Preview(byte[] b)
    {
        bool text = b.Take(256).All(x => x >= 0x20 || x is 9 or 10 or 13);
        return text ? Encoding.UTF8.GetString(b, 0, Math.Min(b.Length, 300)).ReplaceLineEndings(" ") : Convert.ToHexString(b, 0, Math.Min(b.Length, 48)) + (b.Length > 48 ? "..." : "");
    }

    // ---------------------------------------------------------------- value helpers (reference value objects)

    static string Route(HttpContext c, string k) => c.Request.RouteValues[k]?.ToString() ?? "";
    static uint TitleOf(HttpContext c) => Convert.ToUInt32(Route(c, "titleId"), 16);
    static string SessionIdOf(HttpContext c) => SessionId(Route(c, "id"));

    static bool IsHex(string? s, int len) => s != null && s.Length == len && s.All(Uri.IsHexDigit);
    static string SessionId(string? v) => IsHex(v, 16) ? v!.ToLowerInvariant() : throw new FormatException($"Invalid SessionId {v}");
    static string Xuid(string? v) => IsHex(v, 16) ? v!.ToUpperInvariant() : throw new FormatException($"Invalid Xuid: {v}");
    static string Mac(string? v) => IsHex(v, 12) ? v!.ToUpperInvariant() : throw new FormatException($"Invalid MAC Address {v}");
    static bool Truthy(string? s) => !string.IsNullOrEmpty(s);

    static string? AsStr(JsonNode? n) => n is JsonValue v ? (v.TryGetValue(out string? s) ? s : v.ToJsonString()) : null;
    static string? Str(JsonNode? n, string k) => AsStr(n?[k]);
    static long? Num(JsonNode? n, string k)
    {
        if (n?[k] is not JsonValue v) return null;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out double d)) return (long)d;
        if (v.TryGetValue(out bool b)) return b ? 1 : 0;
        return v.TryGetValue(out string? s) && long.TryParse(s, out l) ? l : null;
    }
    static JsonArray Arr(JsonNode? n, string k) => n?[k] as JsonArray ?? new JsonArray();
    static IEnumerable<string> Strings(JsonArray a) => a.Select(x => AsStr(x) ?? "");

    // ---------------------------------------------------------------- addresses

    void DetectLocalAddresses()
    {
        var own = new HashSet<IPAddress>();
        IPAddress? best = null, any = null;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                var props = ni.GetIPProperties();
                foreach (var ua in props.UnicastAddresses) own.Add(ua.Address);
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                bool gateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (var ua in props.UnicastAddresses)
                {
                    var a = ua.Address;
                    if (a.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(a) || a.GetAddressBytes()[0] == 169) continue;
                    any ??= a;
                    if (gateway) best ??= a;
                }
            }
        }
        catch (NetworkInformationException) { }
        _ownAddresses = own;
        _lanAddress = (best ?? any ?? IPAddress.Loopback).ToString();
    }

    /// <summary>Reference ProcessClientAddress: IPv4-mapped IPv6 -> IPv4. Additionally, this machine's own callers get <see cref="LocalCallerAddress"/>.</summary>
    string ProcessAddress(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a)) return LocalCallerAddress;
        if (AdvertiseAddress is { Length: > 0 } adv && _ownAddresses.Contains(a)) return adv;
        return a.ToString();
    }

    string ClientAddress(HttpContext c)
    {
        var real = c.Connection.RemoteIpAddress ?? IPAddress.Loopback;
        if (real.IsIPv4MappedToIPv6) real = real.MapToIPv4();
        // NB overlay instances identify themselves; each gets a stable virtual address
        if (c.Request.Headers.TryGetValue("X-NB-Instance", out var inst) && inst.Count > 0)
        {
            string key = real + "#" + inst[0];
            lock (_gate)
            {
                if (!_vipByCaller.TryGetValue(key, out var vip))
                {
                    vip = _nextVip++;
                    _vipByCaller[key] = vip; _vipOwner[vip] = real;
                    Emit($"overlay: {real} instance {inst[0]} is {RoomRelay.VipText(vip)}");
                }
                return RoomRelay.VipText(vip);
            }
        }
        return ProcessAddress(real);
    }
    static string ClientAddressRaw(HttpContext c) { var a = c.Connection.RemoteIpAddress; return a == null ? "?" : a.IsIPv4MappedToIPv6 ? a.MapToIPv4().ToString() : a.ToString(); }

    /// <summary>XUID of the room's host player (explicit, else the lowest virtual address owned by this PC,
    /// else the first player that registered).</summary>
    public string? HostXuid { get; set; }

    string? CurrentHostXuid()
    {
        if (HostXuid is { Length: > 0 }) return HostXuid;
        lock (_gate)
        {
            PlayerState? best = null; uint bestVip = uint.MaxValue;
            foreach (var p in _players.Values)
            {
                if (!IPAddress.TryParse(p.HostAddress, out var a)) continue;
                var b = a.GetAddressBytes();
                uint vip = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
                if (!_vipOwner.TryGetValue(vip, out var owner) || !IsThisPc(owner)) continue;
                if (vip < bestVip) { bestVip = vip; best = p; }
            }
            return (best ?? _players.Values.FirstOrDefault())?.Xuid;
        }
    }

    static bool IsThisPc(IPAddress a)
    {
        // 127.0.0.1 = this PC; other loopback addresses (127.0.0.2) are the Steam tunnel's remote players
        if (a.Equals(IPAddress.Loopback)) return true;
        if (IPAddress.IsLoopback(a)) return false;
        try { return Dns.GetHostAddresses(Dns.GetHostName()).Any(x => x.Equals(a)); } catch (Exception) { return false; }
    }

    /// <summary>GET /nb/room: room name, host player, members and the host's compatibility profile (NB Xenia builds
    /// poll this to befriend the other players and to join the host's party automatically).</summary>
    Task RoomInfo(HttpContext c)
    {
        object dto;
        string? host = CurrentHostXuid();
        lock (_gate)
            dto = new
            {
                name = RoomName, host = host ?? "", edition = Edition,
                players = _players.Values.Select(p => new { xuid = p.Xuid, gamertag = p.Gamertag, address = p.HostAddress, session = p.SessionId }).ToList(),
                fingerprint = HostCompat?.Fingerprint ?? "", files = HostCompat?.Files ?? new Dictionary<string, string>(),
                recipe = Recipe?.Select(m => new { id = m.Id, name = m.Name, version = m.Version, sha256 = m.Sha256, size = m.Size }).ToList(),
                coop = Coop == null ? null : new { protocol = Coop.Protocol, timeOfDay = Coop.TimeOfDay, allUnlocked = Coop.AllUnlockedSave },
            };
        return Json(c, 200, dto);
    }

    /// <summary>GET /nb/mods/{sha256}: a mod of the host's edition, so joiners can build the same edition. Only the
    /// mods named in <see cref="Recipe"/> are served.</summary>
    async Task ModDownload(HttpContext c)
    {
        var sha = (c.Request.RouteValues["sha"] as string ?? "").ToLowerInvariant();
        var path = Recipe?.Any(m => m.Sha256 == sha) == true ? ModFile?.Invoke(sha) : null;
        if (path == null || !File.Exists(path)) { await Json(c, 404, new { error = "no such mod in this room" }); return; }
        c.Response.StatusCode = 200;
        c.Response.ContentType = "application/octet-stream";
        c.Response.ContentLength = new FileInfo(path).Length;
        await c.Response.SendFileAsync(path);
    }

    // ---------------------------------------------------------------- routes

    void MapRoutes(WebApplication app)
    {
        app.MapMethods("/", ["GET", "HEAD"], Index);
        app.MapGet("/sessions", SessionsOverview);
        app.MapGet("/nb/room", RoomInfo);
        app.MapGet("/nb/mods/{sha}", ModDownload);
        app.MapGet("/whoami", c => Json(c, 200, new { address = ClientAddress(c) }));
        app.MapDelete("/DeleteSessions", DeleteSessions);
        app.MapDelete("/DeleteSessions/{macAddress}", DeleteSessions);

        app.MapPost("/players", RegisterPlayer);
        app.MapPost("/players/find", FindPlayer);
        app.MapPost("/players/setpresence", SetPresence);
        app.MapPost("/players/presence", Presence);
        app.MapPost("/players/findusers", FindUsers);
        app.MapPost("/players/setsettings", SetSettings);
        app.MapPost("/players/getsettings", GetSettings);
        app.MapGet("/players/deletemyprofiles", DeleteMyProfiles);

        const string S = "/title/{titleId}/sessions";
        app.MapPost(S, CreateSession);
        app.MapGet(S + "/search", SearchAll);
        app.MapPost(S + "/search", Search);
        app.MapGet(S + "/{id}", GetSession);
        app.MapPost(S + "/{id}/migrate", Migrate);
        app.MapDelete(S + "/{id}", DeleteSession);
        app.MapGet(S + "/{id}/details", Details);
        app.MapGet(S + "/{id}/arbitration", Arbitration);
        app.MapPost(S + "/{id}/modify", Modify);
        app.MapPost(S + "/{id}/join", Join);
        app.MapPost(S + "/{id}/prejoin", PreJoin);
        app.MapPost(S + "/{id}/leave", Leave);
        app.MapPost(S + "/{id}/qos", QosUpload);
        app.MapGet(S + "/{id}/qos", QosDownload);
        app.MapPost(S + "/{id}/context", SetContext);
        app.MapGet(S + "/{id}/context", GetContext);
        app.MapPost(S + "/{id}/properties", SetProperties);
        app.MapGet(S + "/{id}/properties", GetProperties);
        app.MapGet(S + "/{id}/properties/{queryId}", GetPropertiesOrdered);
        app.MapPost(S + "/{id}/leaderboards", WriteStats);

        // No per-title XLSP config: reference answers [] / [] / {} when titles/<id>/*.json is absent.
        app.MapGet("/title/{titleId}/servers", c => Json(c, 200, Array.Empty<object>()));
        app.MapGet("/title/{titleId}/services", c => Json(c, 200, Array.Empty<object>()));
        app.MapGet("/title/{titleId}/ports", c => Json(c, 200, new { }));
        app.MapPost("/leaderboards/find", FindLeaderboards);

        // XStorage disabled (reference with XSTORAGE=false): build-path/upload/delete/enumerate 403, download 204.
        // XLiveAPI then falls back to the local xstorage folder for every operation.
        // answer at once without reading the body: some XStorage uploads announce a body that never arrives,
        // and waiting for it stalled the game at boot (Rare logo)
        app.MapPost("/xstorage/{**rest}", c => Error(c, 403, "XStorage support is disabled on backend!"));
        app.MapDelete("/xstorage/{**rest}", c => Error(c, 403, "XStorage support is disabled on backend!"));
        app.MapGet("/xstorage/{**rest}", c => Empty(c, 204));
    }

    Task Index(HttpContext c)
    {
        if (HttpMethods.IsHead(c.Request.Method)) return Empty(c, 200);
        int np, ns; lock (_gate) { np = _players.Count; ns = _sessions.Values.Count(s => !s.Deleted); }
        var text = Encoding.UTF8.GetBytes($"NB Room Server (Xenia Web Services compatible)\nplayers: {np}\nsessions: {ns}\n");
        c.Response.StatusCode = 200; c.Response.ContentType = "text/plain; charset=utf-8"; c.Response.ContentLength = text.Length;
        return c.Response.Body.WriteAsync(text).AsTask();
    }

    /// <summary>index.controller /sessions (web front-end aggregate), without the title-art downloads.</summary>
    Task SessionsOverview(HttpContext c)
    {
        var titles = new List<Dictionary<string, object?>>();
        lock (_gate)
        {
            var picked = _sessions.Values.Where(s => s.Listed && s.Xuid != null).GroupBy(s => s.Xuid!)
                .Select(g => g.Aggregate((a, b) => b.Players.Count > a.Players.Count ? b : a));
            foreach (var s in picked)
            {
                var host = s.HostXuid();
                if (host == null || !s.Players.ContainsKey(host) || !_players.TryGetValue(host, out var hp)) continue;
                var hostNameProp = s.Properties.FirstOrDefault(p => XData.TryParseProperty(p, out uint id, out _, out _) && id == XData.GamerHostname);
                string hostName = (hostNameProp != null ? XData.WString(hostNameProp) : null) is { Length: > 0 } hn ? hn : hp.Gamertag;
                var infos = new List<object> { new { gamertag = hostName, gamerpic = "" } };
                int local = 0;
                if (s.Players.Count > 1)
                    foreach (var x in s.Players.Keys)
                    {
                        if (x.Equals(host, StringComparison.OrdinalIgnoreCase)) continue;
                        infos.Add(new { gamertag = _players.TryGetValue(x, out var p) ? p.Gamertag : $"Local Player {++local}", gamerpic = "" });
                    }
                var t = titles.FirstOrDefault(e => (string)e["titleId"]! == s.TitleHex);
                if (t == null) titles.Add(t = new() { ["titleId"] = s.TitleHex, ["name"] = s.Title, ["icon"] = "", ["info"] = new { }, ["sessions"] = new List<object>() });
                ((List<object>)t["sessions"]!).Add(new
                {
                    mediaId = s.MediaId, version = s.Version, players = infos, total = s.PublicSlots + s.PrivateSlots,
                    host_presence = hp.RichPresence is { Length: > 0 } rp ? rp : $"Playing {s.Title}", host_gamertag = hostName, host_xuid = host,
                });
            }
        }
        return Json(c, 200, new { Titles = titles });
    }

    Task DeleteSessions(HttpContext c)
    {
        string? q = c.Request.Query["hostAddress"];
        string ip = Truthy(q) ? ProcessAddress(IPAddress.Parse(q!)) : ClientAddress(c);
        string? mac = IsHex(Route(c, "macAddress"), 12) ? Route(c, "macAddress").ToUpperInvariant() : null;
        lock (_gate)
        {
            var doomed = _sessions.Values.Where(s => s.HostAddress == ip && (mac == null || s.MacAddress == mac)).ToList();
            foreach (var s in doomed) { _sessions.Remove(s.Key); _qos.Remove(s.Key); }
            if (doomed.Count > 0) Emit($"  deleted {doomed.Count} session(s) of {ip}{(mac != null ? " / " + mac : "")}");
            var p = _players.Values.FirstOrDefault(p => p.HostAddress == ip);
            if (p != null) { p.SessionId = PlayerState.NoSession; p.TitleId = "0"; p.State = PlayerState.DefaultState; p.RichPresence = ""; p.LastSeen = DateTime.UtcNow; }
        }
        return Empty(c, 200);
    }

    // ---------------------------------------------------------------- players

    async Task RegisterPlayer(HttpContext c)
    {
        var j = await JsonBody(c);
        var p = new PlayerState
        {
            Xuid = Xuid(Str(j, "xuid")), MachineId = Xuid(Str(j, "machineId")), HostAddress = Str(j, "hostAddress") ?? throw new FormatException("hostAddress missing"),
            MacAddress = Mac(Str(j, "macAddress")),
        };
        if (Str(j, "gamertag") is { Length: > 0 } gt) p.Gamertag = gt.Length <= 15 ? gt : throw new FormatException($"Invalid Gamertag: {gt}");
        if (j["settings"] is JsonObject so)
            foreach (var (title, list) in so)
                if (list is JsonArray a) p.Settings[title.ToUpperInvariant()] = Strings(a).Select(s => { XData.SettingId(s); return s; }).ToList();
        if (j["settings"] is not JsonObject) p.Settings["FFFE07D1"] = [XData.DefaultGamerpic];
        lock (_gate) _players[p.Xuid] = p;   // upsert, keeps registration order
        Emit($"  player {p.Xuid} \"{p.Gamertag}\" {p.HostAddress} mac {p.MacAddress} machine {p.MachineId} ({p.Settings.Values.Sum(v => v.Count)} settings)");
        await Empty(c, 201);
    }

    async Task FindPlayer(HttpContext c)
    {
        var j = await JsonBody(c);
        string? ip = Str(j, "hostAddress");
        PlayerState? p; lock (_gate) p = _players.Values.FirstOrDefault(x => x.HostAddress == ip);
        if (p == null) throw NotFound("Player not found.");
        await Json(c, 201, new { xuid = p.Xuid, gamertag = p.Gamertag, hostAddress = p.HostAddress, machineId = p.MachineId, port = p.Port, macAddress = p.MacAddress, sessionId = p.SessionId });
    }

    async Task SetPresence(HttpContext c)
    {
        var j = await JsonBody(c);
        lock (_gate)
            foreach (var e in Arr(j, "presence"))
                if (_players.TryGetValue(Xuid(Str(e, "xuid")), out var p)) { p.RichPresence = Str(e, "richPresence") ?? ""; p.LastSeen = DateTime.UtcNow; }
        await Empty(c, 201);
    }

    async Task Presence(HttpContext c)
    {
        var j = await JsonBody(c);
        var res = new List<object>();
        lock (_gate)
            foreach (var x in Strings(Arr(j, "xuids")).Where(x => IsHex(x, 16)).Distinct(StringComparer.OrdinalIgnoreCase))
                if (_players.TryGetValue(x, out var p))
                    res.Add(new { xuid = p.Xuid, gamertag = p.Gamertag, state = p.State, sessionId = p.SessionId, titleId = p.TitleId, stateChangeTime = 0, richPresence = p.RichPresence });
        await Json(c, 201, res);
    }

    async Task FindUsers(HttpContext c)
    {
        var j = await JsonBody(c);
        var res = new List<object>();
        lock (_gate)
            foreach (var n in Arr(j, "UsersInfo"))
            {
                var t = n as JsonArray ?? new JsonArray();
                string? xuid = t.Count > 0 ? AsStr(t[0]) : null, gamertag = t.Count > 1 ? AsStr(t[1]) : null;
                if (Truthy(xuid) && _players.TryGetValue(Xuid(xuid), out var p) && !Truthy(gamertag)) gamertag = p.Gamertag;
                if (Truthy(gamertag) && !Truthy(xuid)) xuid = _players.Values.FirstOrDefault(q => q.Gamertag == gamertag)?.Xuid ?? xuid;
                res.Add(new { xuid, gamertag });
            }
        await Json(c, 201, res);
    }

    /// <summary>settings: [ { "XUID": [ { "TITLEID": [ items ] } ] } ]</summary>
    static IEnumerable<(string Xuid, string Title, List<string> Items)> SettingsRequest(JsonNode j)
    {
        foreach (var xo in Arr(j, "settings").OfType<JsonObject>())
            foreach (var (xuid, titles) in xo)
                foreach (var to in (titles as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    foreach (var (title, items) in to)
                        yield return (xuid, title, Strings(items as JsonArray ?? new JsonArray()).ToList());
    }

    async Task SetSettings(HttpContext c)
    {
        var j = await JsonBody(c);
        int n = 0;
        lock (_gate)
            foreach (var (x, title, items) in SettingsRequest(j))
            {
                if (!_players.TryGetValue(Xuid(x), out var p)) continue;
                var list = p.Settings.TryGetValue(title, out var l) ? l : (p.Settings[title.ToUpperInvariant()] = new());
                foreach (var b64 in items)
                {
                    uint id = XData.SettingId(b64);
                    int i = list.FindIndex(s => XData.SettingId(s) == id);
                    if (i >= 0) list[i] = b64; else list.Add(b64);
                    n++;
                }
                p.LastSeen = DateTime.UtcNow;
            }
        if (n > 0) Emit($"  stored {n} profile setting(s)");
        await Empty(c, 201);
    }

    async Task GetSettings(HttpContext c)
    {
        var j = await JsonBody(c);
        var byXuid = new List<(string Xuid, List<(string Title, List<string> Items)> Titles)>();
        lock (_gate)
            foreach (var (x, title, ids) in SettingsRequest(j))
            {
                _players.TryGetValue(x, out var p);
                if (p == null) Emit($"  getsettings: unregistered profile {x}");
                var found = new List<string>();
                foreach (var idHex in ids)
                {
                    uint id = Convert.ToUInt32(idHex, 16);
                    string? s = p != null && p.Settings.TryGetValue(title, out var l) ? l.FirstOrDefault(b => XData.SettingId(b) == id) : null;
                    if (s == null && id == XData.GamercardPictureKey) s = XData.DefaultGamerpic;   // title keeps asking otherwise
                    if (s != null) found.Add(s);
                }
                var entry = byXuid.FirstOrDefault(e => e.Xuid == x);
                if (entry.Xuid == null) byXuid.Add(entry = (x, new()));
                entry.Titles.Add((title, found));
            }
        var body = new JsonObject { ["settings"] = new JsonArray(byXuid.Select(e => (JsonNode)new JsonObject
        {
            [e.Xuid] = new JsonArray(e.Titles.Select(t => (JsonNode)new JsonObject { [t.Title] = new JsonArray(t.Items.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()) }).ToArray()),
        }).ToArray()) };
        await Json(c, 201, body);
    }

    Task DeleteMyProfiles(HttpContext c)
    {
        string ip = ClientAddress(c);
        List<string[]> gone;
        lock (_gate)
        {
            var doomed = _players.Values.Where(p => p.HostAddress == ip).ToList();
            foreach (var p in doomed) _players.Remove(p.Xuid);
            gone = doomed.Select(p => new[] { p.Gamertag, p.Xuid }).ToList();
        }
        if (gone.Count > 0) Emit($"  deleted {gone.Count} profile(s) of {ip}");
        return Json(c, 200, gone);
    }

    // ---------------------------------------------------------------- sessions

    SessionState? Find(uint title, string id) => _sessions.TryGetValue(SessionState.KeyOf(title, id), out var s) ? s : null;
    SessionState Need(uint title, string id, string? message = null) => Find(title, id) ?? throw NotFound(message ?? $"Session {id} was not found.");

    static object Dto(SessionState s) => new
    {
        id = s.Id, flags = s.Flags, hostAddress = s.HostAddress, macAddress = s.MacAddress, publicSlotsCount = s.PublicSlots, privateSlotsCount = s.PrivateSlots,
        openPublicSlotsCount = s.OpenPublic, openPrivateSlotsCount = s.OpenPrivate, filledPublicSlotsCount = s.FilledPublic, filledPrivateSlotsCount = s.FilledPrivate, port = s.Port,
    };

    static object DetailsDto(SessionState s) => new
    {
        title = s.Title, version = s.Version, mediaId = s.MediaId, xuid = s.Xuid, id = s.Id, flags = s.Flags, hostAddress = s.HostAddress, macAddress = s.MacAddress,
        publicSlotsCount = s.PublicSlots, privateSlotsCount = s.PrivateSlots, openPublicSlotsCount = s.OpenPublic, openPrivateSlotsCount = s.OpenPrivate,
        filledPublicSlotsCount = s.FilledPublic, filledPrivateSlotsCount = s.FilledPrivate, port = s.Port,
    };

    async Task CreateSession(HttpContext c)
    {
        uint title = TitleOf(c);
        var j = await JsonBody(c);
        string id = SessionId(Str(j, "sessionId"));
        int flags = (int)(Num(j, "flags") ?? 0);
        string? xuid = Str(j, "xuid"), host = Str(j, "hostAddress");
        lock (_gate)
        {
            if (!SessionFlag.IsHost(flags))
            {
                Emit($"  peer create for session {id} (flags 0x{flags:X})" + (Find(title, id) == null ? " - session not found" : ""));
            }
            else
            {
                var player = Truthy(xuid) ? (_players.TryGetValue(Xuid(xuid), out var px) ? px : null) : _players.Values.FirstOrDefault(p => p.HostAddress == host);
                if (player == null) throw new ApiException(403, $"Player not found: {(Truthy(xuid) ? xuid : host)}");
                var s = new SessionState
                {
                    TitleId = title, Id = id, Xuid = Truthy(xuid) ? Xuid(xuid) : null, Title = Str(j, "title") ?? "", MediaId = Str(j, "mediaId") ?? "", Version = Str(j, "version") ?? "",
                    Flags = flags, HostAddress = host ?? "", MacAddress = Mac(Str(j, "macAddress")), PublicSlots = (int)(Num(j, "publicSlotsCount") ?? 0),
                    PrivateSlots = (int)(Num(j, "privateSlotsCount") ?? 0), Port = (int)(Num(j, "port") ?? 36000),
                };
                s.SetXLast(Str(j, "xlast_src"));
                _sessions[s.Key] = s;   // upsert: re-creating an id resets members/properties like the reference
                if (s.Advertised)
                {
                    if ((flags & SessionFlag.FriendsOnly) != 0) player.State |= SessionFlag.StateFriendsOnly;
                    player.SessionId = id; player.LastSeen = DateTime.UtcNow;
                }
                Emit($"  session {id} created by {player.Xuid} \"{player.Gamertag}\" at {s.HostAddress}:{s.Port} flags 0x{flags:X} slots {s.PublicSlots}+{s.PrivateSlots}" +
                     (s.XLastSrc != null ? $" xlast {s.XLastSrc.Length} chars" : ""));
            }
        }
        await Empty(c, 201);
    }

    Task SearchAll(HttpContext c)
    {
        uint title = TitleOf(c);
        List<object> res; lock (_gate) res = _sessions.Values.Where(s => s.TitleId == title && s.Listed).Select(DetailsDto).ToList();
        return Json(c, 200, res);
    }

    async Task Search(HttpContext c)
    {
        uint title = TitleOf(c);
        var j = await JsonBody(c);
        string? searcher = Truthy(Str(j, "searcher_xuid")) ? Xuid(Str(j, "searcher_xuid")) : null;
        long queryId = Num(j, "searchIndex") ?? 0, results = Num(j, "resultsCount") ?? 0, numUsers = Num(j, "numUsers") ?? 0;
        var filters = j["filters"] is JsonArray fa ? Strings(fa).ToList() : null;
        var res = new List<object>();
        lock (_gate)
        {
            var candidates = _sessions.Values.Where(s => s.TitleId == title && s.Listed && !string.Equals(s.Xuid, searcher, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var s in candidates)
            {
                string? why = s.PublicSlots == 0 ? "private" : s.IsFull ? "full" : !s.Matchmaking ? "not matchmaking"
                    : numUsers > 0 && s.OpenPublic < numUsers && s.OpenPrivate < numUsers ? "not enough slots"
                    : RelaxedSearch || filters == null ? null : FilterMismatch(s, filters, queryId);
                if (why != null) { Emit($"  search: skip {s.Id} ({why})"); continue; }
                if (results > 0 && res.Count >= results) break;   // reference limits before filtering; limiting after is what it intends
                res.Add(Dto(s));
            }
            Emit($"  search query {queryId}: {res.Count} of {candidates.Count} listed session(s) match ({filters?.Count ?? 0} filter props)");
        }
        await Json(c, 201, res);
    }

    /// <summary>Reference SessionRepository.findAdvertisedSessions property filter; null = match, else the reason.</summary>
    string? FilterMismatch(SessionState s, List<string> filters, long queryId)
    {
        var props = s.AllProperties();
        string? Prop(IEnumerable<string> list, uint id) => list.FirstOrDefault(p => XData.TryParseProperty(p, out uint i, out _, out _) && i == id);
        string? sMode = Prop(props, XData.GameMode), sType = Prop(props, XData.GameType), fMode = Prop(filters, XData.GameMode), fType = Prop(filters, XData.GameType);
        if (sMode == null || sType == null) return "session has no GAME_MODE/GAME_TYPE context";
        if (fMode == null || fType == null) return "search has no GAME_MODE/GAME_TYPE context";
        bool okMode = XData.Compare(XData.Value(fMode), XData.Value(sMode)) == 0, okType = XData.Compare(XData.Value(fType), XData.Value(sType)) == 0;
        var q = s.GetXLast(Emit) is { } x && x.Queries.TryGetValue(queryId, out var qq) ? qq : null;
        if (q?.Filters == null)
            return okMode && okType ? null : $"game mode {XData.Value(sMode)} vs {XData.Value(fMode)}, type {XData.Value(sType)} vs {XData.Value(fType)}";
        var fails = new List<string>();
        if (!q.Filters.Any(f => f.Left == XData.GameMode) && !okMode) fails.Add("game mode");
        if (!q.Filters.Any(f => f.Left == XData.GameType) && !okType) fails.Add("game type");
        foreach (var f in q.Filters)
        {
            object? left = f.LeftType == "Attribute" && Prop(props, f.Left) is { } lp ? XData.Value(lp) : null;
            object? right = f.RightType switch
            {
                "ContextValue" or "Parameter" => Prop(filters, f.Right) is { } rp ? XData.Value(rp) : null,
                "Constant" => s.GetXLast(Emit)!.Constants.TryGetValue(f.Right, out long k) ? k : null,
                _ => null,
            };
            if (XData.Compare(left, right) is not int cmp) continue;   // unsupported / missing operand: optional, counts as true
            bool ok = f.Op switch { "==" => cmp == 0, "!=" => cmp != 0, ">" => cmp > 0, "<" => cmp < 0, ">=" => cmp >= 0, "<=" => cmp <= 0, _ => false };
            if (!ok) fails.Add($"0x{f.Left:X8} {left} {f.Op} {right}");
        }
        return fails.Count == 0 ? null : $"query \"{q.Name}\": " + string.Join(", ", fails);
    }

    Task GetSession(HttpContext c)
    {
        object dto; lock (_gate) dto = Dto(Need(TitleOf(c), SessionIdOf(c)));
        return Json(c, 200, dto);
    }

    async Task Migrate(HttpContext c)
    {
        uint title = TitleOf(c); string id = SessionIdOf(c);
        var j = await JsonBody(c);
        object dto;
        lock (_gate)
        {
            var old = Need(title, id);
            SessionState ns;
            if (old.Migration != null) ns = Find(title, old.Migration) ?? throw NotFound($"Session {old.Migration} was not found.");
            else
            {
                if (old.Xuid != null) old.Players.Remove(old.Xuid);
                string newId;
                do newId = (0x8000000000000000UL | ((ulong)Random.Shared.NextInt64() & 0x00FFFFFFFFFFFFFFUL)).ToString("x16"); while (Find(title, newId) != null);
                string? xuid = Str(j, "xuid");
                ns = old.CloneForMigration(newId);
                ns.Xuid = Truthy(xuid) ? Xuid(xuid) : null; ns.HostAddress = Str(j, "hostAddress") ?? ""; ns.MacAddress = Mac(Str(j, "macAddress")); ns.Port = (int)(Num(j, "port") ?? 36000);
                old.Deleted = true; old.Migration = newId; old.Updated = DateTime.UtcNow;
                _sessions[ns.Key] = ns;
                if (ns.Xuid != null && _players.TryGetValue(ns.Xuid, out var p) && old.Advertised) { p.SessionId = newId; p.LastSeen = DateTime.UtcNow; }
                Emit($"  session {id} migrated to {newId}, new host {ns.Xuid ?? "(remote)"} at {ns.HostAddress}:{ns.Port}");
            }
            dto = Dto(ns);
        }
        await Json(c, 201, dto);
    }

    Task DeleteSession(HttpContext c)
    {
        uint title = TitleOf(c); string id = SessionIdOf(c), ip = ClientAddress(c);
        lock (_gate)
        {
            var s = Find(title, id);
            if (s == null) Emit($"  session {id} is already deleted");
            else if (s.HostAddress != ip) Emit($"  {ip} may not delete session {id} created by {s.HostAddress}");
            else
            {
                s.Deleted = true; s.Updated = DateTime.UtcNow; _qos.Remove(s.Key);   // soft delete like the reference (GET still finds it until expiry)
                if (s.Properties.Count > 0 && s.HostXuid() is { } hx && _players.TryGetValue(hx, out var host)) host.State &= ~SessionFlag.StateFriendsOnly;
                Emit($"  session {id} deleted");
            }
        }
        return Empty(c, 200);
    }

    Task Details(HttpContext c)
    {
        object dto;
        lock (_gate)
        {
            var s = Need(TitleOf(c), SessionIdOf(c));
            dto = new
            {
                id = s.Id, flags = s.Flags, hostAddress = s.HostAddress, port = s.Port, macAddress = s.MacAddress, publicSlotsCount = s.PublicSlots, privateSlotsCount = s.PrivateSlots,
                openPublicSlotsCount = s.OpenPublic, openPrivateSlotsCount = s.OpenPrivate, filledPublicSlotsCount = s.FilledPublic, filledPrivateSlotsCount = s.FilledPrivate,
                players = s.Players.Keys.Select(x => new { xuid = x }).ToList(),
            };
        }
        return Json(c, 200, dto);
    }

    Task Arbitration(HttpContext c)
    {
        object dto;
        lock (_gate)
        {
            var s = Need(TitleOf(c), SessionIdOf(c));
            var machines = s.Players.Keys.Select(x => _players.TryGetValue(x, out var p) ? p : null).OfType<PlayerState>().GroupBy(p => p.MachineId)
                .Select(g => new { id = g.Key, players = g.Select(p => new { xuid = p.Xuid }).ToList() }).ToList();
            dto = new { totalPlayers = s.Players.Count, machines };
        }
        return Json(c, 200, dto);
    }

    async Task Modify(HttpContext c)
    {
        uint title = TitleOf(c); string id = SessionIdOf(c);
        var j = await JsonBody(c);
        lock (_gate)
        {
            var s = Need(title, id, $"Failed to modify session {id} was not found.");
            s.Flags = (int)(Num(j, "flags") ?? s.Flags);
            s.PublicSlots = (int)(Num(j, "publicSlotsCount") ?? s.PublicSlots); s.PrivateSlots = (int)(Num(j, "privateSlotsCount") ?? s.PrivateSlots);
            s.Updated = DateTime.UtcNow;
            if (s.Properties.Count > 0 && s.HostXuid() is { } hx && _players.TryGetValue(hx, out var host))
            {
                if ((s.Flags & SessionFlag.FriendsOnly) != 0) host.State |= SessionFlag.StateFriendsOnly; else host.State &= ~SessionFlag.StateFriendsOnly;
            }
            Emit($"  session {id} modified: flags 0x{s.Flags:X} slots {s.PublicSlots}+{s.PrivateSlots}");
        }
        await Empty(c, 201);
    }

    async Task Join(HttpContext c)
    {
        uint title = TitleOf(c); string id = SessionIdOf(c);
        var j = await JsonBody(c);
        var xuids = Strings(Arr(j, "xuids")).Select(Xuid).ToList();
        var priv = j["privateSlots"] as JsonArray;
        lock (_gate)
        {
            var s = Need(title, id, $"Failed to join session {id} was not found.");
            for (int i = 0; i < xuids.Count; i++) s.Players[xuids[i]] = priv != null && i < priv.Count && priv[i] is JsonValue v && v.TryGetValue(out bool b) && b;
            s.Updated = DateTime.UtcNow;
            PreJoinPlayers(title, id, xuids);
            Emit($"  session {id}: joined {string.Join(", ", xuids.Select(Describe))} -> {s.Players.Count} member(s), open {s.OpenPublic}+{s.OpenPrivate}");
        }
        await Empty(c, 201);
    }

    string Describe(string xuid) => _players.TryGetValue(xuid, out var p) ? $"{xuid} \"{p.Gamertag}\"" : xuid;

    void PreJoinPlayers(uint title, string id, IEnumerable<string> xuids)
    {
        foreach (var x in xuids)
            if (_players.TryGetValue(x, out var p)) { p.SessionId = id; p.TitleId = title.ToString("X"); p.LastSeen = DateTime.UtcNow; }
    }

    async Task PreJoin(HttpContext c)
    {
        uint title = TitleOf(c); string id = SessionIdOf(c);
        var j = await JsonBody(c);
        var xuids = Strings(Arr(j, "xuids")).Select(Xuid).ToList();
        lock (_gate) PreJoinPlayers(title, id, xuids);
        await Empty(c, 201);
    }

    async Task Leave(HttpContext c)
    {
        uint title = TitleOf(c); string id = SessionIdOf(c);
        var j = await JsonBody(c);
        var xuids = Strings(Arr(j, "xuids")).Select(Xuid).ToList();
        lock (_gate)
        {
            var s = Need(title, id, $"Failed to leave session {id} was not found.");
            foreach (var x in xuids)
            {
                s.Players.Remove(x);
                if (s.Advertised && _players.TryGetValue(x, out var p)) { p.SessionId = PlayerState.NoSession; p.LastSeen = DateTime.UtcNow; }
            }
            s.Updated = DateTime.UtcNow;
            Emit($"  session {id}: left {string.Join(", ", xuids.Select(Describe))} -> {s.Players.Count} member(s)");
        }
        await Empty(c, 201);
    }

    async Task QosUpload(HttpContext c)
    {
        string key = SessionState.KeyOf(TitleOf(c), SessionIdOf(c));
        var data = await ReadBody(c);
        lock (_gate) _qos[key] = data;   // stored even for sessions the server doesn't know (system link), like the reference
        await Empty(c, 201);
    }

    async Task QosDownload(HttpContext c)
    {
        byte[]? data; lock (_gate) _qos.TryGetValue(SessionState.KeyOf(TitleOf(c), SessionIdOf(c)), out data);
        if (data == null) { await Empty(c, 204); return; }
        c.Response.StatusCode = 200; c.Response.ContentType = "application/octet-stream"; c.Response.ContentLength = data.Length;
        await c.Response.Body.WriteAsync(data);
    }

    async Task SetContext(HttpContext c)
    {
        uint title = TitleOf(c); string id = SessionIdOf(c);
        var j = await JsonBody(c);
        lock (_gate)
        {
            var s = Need(title, id);
            foreach (var e in Arr(j, "contexts")) s.Context[((uint)(Num(e, "contextId") ?? 0)).ToString("x")] = (uint)(Num(e, "value") ?? 0);
            s.Updated = DateTime.UtcNow;
        }
        await Empty(c, 201);
    }

    Task GetContext(HttpContext c)
    {
        Dictionary<string, uint> ctx; lock (_gate) ctx = Need(TitleOf(c), SessionIdOf(c)).Context.ToDictionary(kv => kv.Key, kv => kv.Value);
        return Json(c, 200, new { context = ctx });
    }

    async Task SetProperties(HttpContext c)
    {
        uint title = TitleOf(c); string id = SessionIdOf(c);
        var j = await JsonBody(c);
        var blobs = Strings(Arr(j, "properties")).ToList();
        lock (_gate)
        {
            var s = Need(title, id);
            int props = 0, ctxs = 0;
            foreach (var b64 in blobs)
            {
                if (!XData.TryParseProperty(b64, out uint pid, out byte type, out var data)) throw new FormatException("Invalid base64");
                if (type == XData.TypeContext)
                {
                    s.Context[pid.ToString("x")] = data.Length >= 4 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data) : 0; ctxs++;
                    continue;
                }
                int i = s.Properties.FindIndex(p => XData.TryParseProperty(p, out uint q, out _, out _) && q == pid);
                if (i >= 0) s.Properties[i] = b64; else s.Properties.Add(b64);
                props++;
            }
            s.Updated = DateTime.UtcNow;
            Emit($"  session {id}: {props} propert(ies), {ctxs} context(s) set -> {s.Properties.Count} properties, contexts " +
                 string.Join(" ", s.Context.Select(kv => $"{kv.Key}={kv.Value}")));
        }
        await Empty(c, 201);
    }

    Task GetProperties(HttpContext c)
    {
        List<string> props; lock (_gate) props = Need(TitleOf(c), SessionIdOf(c)).AllProperties();
        return Json(c, 200, new { properties = props });
    }

    Task GetPropertiesOrdered(HttpContext c)
    {
        List<string> res;
        lock (_gate)
        {
            var s = Need(TitleOf(c), SessionIdOf(c));
            var all = s.AllProperties();
            long.TryParse(Route(c, "queryId"), out long qid);
            var q = s.GetXLast(Emit) is { } x && x.Queries.TryGetValue(qid, out var qq) ? qq : null;
            if (q?.Returns == null) res = all;
            else
            {
                string? Prop(uint id) => all.FirstOrDefault(p => XData.TryParseProperty(p, out uint i, out _, out _) && i == id);
                res = new();
                foreach (var r in q.Returns)
                    if (Prop(r) is { } p) res.Add(p); else Emit($"  missing property in returns: {r:X8}");
                foreach (var sys in new[] { XData.GamerPuid, XData.GamerHostname })   // system properties last
                    if (!q.Returns.Contains(sys) && Prop(sys) is { } p) res.Add(p);
            }
        }
        return Json(c, 200, new { properties = res });
    }

    async Task WriteStats(HttpContext c)
    {
        var b = await ReadBody(c);
        Emit($"  stats write for session {Route(c, "id")} ignored ({b.Length} B): {Preview(b)}");
        await Empty(c, 201);
    }

    /// <summary>No stats are stored, so every requested column comes back as the reference's "missing" placeholder (type 255 = UNSET, value 0).</summary>
    async Task FindLeaderboards(HttpContext c)
    {
        var j = await JsonBody(c);
        var players = Strings(Arr(j, "players")).Select(Xuid).ToList();
        var res = new List<object>();
        lock (_gate)
            foreach (var q in Arr(j, "queries"))
            {
                if (players.Count == 0) break;
                var stats = Strings(Arr(q, "statisticIds")).Select(s => new { id = long.TryParse(s, out long v) ? v : 0, type = 255, value = 0 }).ToList();
                res.Add(new
                {
                    id = Num(q, "id") ?? 0,
                    players = players.Select(x => new { xuid = x, gamertag = _players.TryGetValue(x, out var p) ? p.Gamertag : "Xenia User", stats }).ToList(),
                });
            }
        await Json(c, 201, res);
    }

    // ---------------------------------------------------------------- expiry

    void Expire()
    {
        int s = 0, p = 0;
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            foreach (var x in _sessions.Values.Where(x => now - x.Updated > SessionTtl).ToList()) { _sessions.Remove(x.Key); _qos.Remove(x.Key); s++; }
            foreach (var x in _players.Values.Where(x => now - x.LastSeen > PlayerTtl).ToList()) { _players.Remove(x.Xuid); p++; }
        }
        if (s + p == 0) return;
        Emit($"expired {s} session(s), {p} player(s)");
        RaiseChanged();
    }

    sealed class NoLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    sealed class LogForwarder(RoomServer owner) : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) owner.Emit($"[{logLevel}] {formatter(state, exception)}{(exception != null ? " " + exception.Message : "")}");
        }
    }
}

/// <summary>A co-op room's settings, reflected to every player who joins (GET /nb/room "coop").</summary>
public sealed class CoopRoomSettings
{
    /// <summary>Co-op packet protocol: joiners with another one cannot see each other (they need the same NB Multiplayer).</summary>
    public int Protocol { get; set; }
    /// <summary>Showdown Town time of day, 1..4 = morning, midday, afternoon, night.</summary>
    public int TimeOfDay { get; set; }
    /// <summary>Everyone starts with the all-unlocked save (no intro, everything unlocked).</summary>
    public bool AllUnlockedSave { get; set; }
}
