using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Steamworks;
using Steamworks.Data;
using System.IO;

namespace NB.Multiplayer.Services;

/// <summary>
/// Room connections through Steam (Steam Datagram Relay): no port forwarding. The joiner's NB Multiplayer listens on
/// 127.0.0.1:36000 (TCP, room server API) and 127.0.0.1:36001 (UDP, game traffic relay) - exactly what its Xenia
/// expects - and carries everything over one Steam connection to the host's NB Multiplayer, which replays it to the
/// host's room server and relay from 127.0.0.2 (so the room server sees a remote player, not the host's own PC).
///
/// Messages: [1][u16 stream][payload] UDP datagram (unreliable); [2][u32 id] TCP open; [3][u32 id][bytes] TCP data;
/// [4][u32 id] TCP close (TCP ones reliable, in order). Steam app id 480 (Valve's public "Spacewar" test app).
/// Test switch NB_STEAM_DIRECT=port: a direct Steam socket on this PC instead of the relay (one Steam account cannot
/// relay-connect to itself).
/// </summary>
public static class SteamNet
{
    public const uint AppId = 480;
    const int RoomPort = Net.Port, RelayPort = Net.Port + 1;
    /// <summary>Joiner's local tunnel ports (TCP = this, UDP = this + 1); NB_TUNNEL_PORT for testing on one PC.</summary>
    public static int TunnelPort => int.TryParse(Environment.GetEnvironmentVariable("NB_TUNNEL_PORT"), out var p) ? p : Net.Port;
    static readonly IPAddress TunnelSource = IPAddress.Parse("127.0.0.2");
    public static string? Error { get; private set; }

    /// <summary>Steam networking messages and connection changes (steam.log in the NB Multiplayer data root).</summary>
    public static void Log(string text)
    {
        try { File.AppendAllText(Path.Combine(AppSettings.Root, "steam.log"), $"{DateTime.Now:HH:mm:ss.fff} {text}{Environment.NewLine}"); } catch (Exception) { }
    }
    static int? DirectPort => int.TryParse(Environment.GetEnvironmentVariable("NB_STEAM_DIRECT"), out var p) ? p : null;

    /// <summary>Starts the Steam API (Steam must be running and logged in). False + <see cref="Error"/> otherwise.</summary>
    public static bool Init()
    {
        if (SteamClient.IsValid) return true;
        try
        {
            Environment.SetEnvironmentVariable("SteamAppId", AppId.ToString());
            Environment.SetEnvironmentVariable("SteamGameId", AppId.ToString());
            SteamClient.Init(AppId, true);
            if (!SteamClient.IsLoggedOn) { Error = "Steam is running but not logged in."; return false; }
            SteamNetworkingUtils.DebugLevel = NetDebugOutput.Msg;
            SteamNetworkingUtils.OnDebugOutput += (level, text) => Log($"[{level}] {text}");
            Log($"Steam started: {SteamClient.Name} ({SteamClient.SteamId.Value}), app {AppId}" + (DirectPort is int dp ? $", direct test port {dp}" : ""));
            SteamNetworkingUtils.InitRelayNetworkAccess();
            return true;
        }
        catch (Exception e)
        {
            Error = "Steam is not available: start Steam and log in. (" + e.Message + ")";
            return false;
        }
    }

    public static ulong MySteamId => SteamClient.IsValid ? SteamClient.SteamId.Value : 0;

    public static void Shutdown() { try { if (SteamClient.IsValid) SteamClient.Shutdown(); } catch (Exception) { } }

    internal static byte[] Msg(byte type, uint id, ReadOnlySpan<byte> payload, bool u16 = false)
    {
        int hl = u16 ? 3 : 5;
        var m = new byte[hl + payload.Length];
        m[0] = type;
        if (u16) { m[1] = (byte)(id >> 8); m[2] = (byte)id; }
        else { m[1] = (byte)(id >> 24); m[2] = (byte)(id >> 16); m[3] = (byte)(id >> 8); m[4] = (byte)id; }
        payload.CopyTo(m.AsSpan(hl));
        return m;
    }

    internal static void Pump(Action receive, CancellationToken stop)
    {
        new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try { receive(); } catch (Exception) { }
                Thread.Sleep(1);
            }
        }) { IsBackground = true, Name = "NB Steam pump" }.Start();
    }

    // ================================================================== host

    public sealed class Host : SocketManager
    {
        sealed class Peer
        {
            public readonly ConcurrentDictionary<uint, UdpClient> Udp = new();
            public readonly ConcurrentDictionary<uint, TcpClient> Tcp = new();
        }
        readonly ConcurrentDictionary<uint, Peer> _peers = new();
        readonly CancellationTokenSource _stop = new();
        public int PeerCount => _peers.Count;
        public event Action? PeersChanged;

        public static Host? Start()
        {
            Host h = DirectPort is int p
                ? SteamNetworkingSockets.CreateNormalSocket<Host>(NetAddress.AnyIp((ushort)p))
                : SteamNetworkingSockets.CreateRelaySocket<Host>(0);
            if (h == null) return null;
            Pump(() => h.Receive(64), h._stop.Token);
            return h;
        }

        public void Stop()
        {
            _stop.Cancel();
            foreach (var c in Connected.ToList()) c.Close();
            foreach (var p in _peers.Values) Drop(p);
            _peers.Clear();
            Close();
        }

        public override void OnConnecting(Connection c, ConnectionInfo info) { Log($"host: {info.Identity} connecting"); c.Accept(); }
        public override void OnConnectionChanged(Connection c, ConnectionInfo info) { Log($"host: {info.Identity} {info.State} {info.EndReason}"); base.OnConnectionChanged(c, info); }

        public override void OnConnected(Connection c, ConnectionInfo info)
        {
            base.OnConnected(c, info);
            _peers[c.Id] = new Peer();
            PeersChanged?.Invoke();
        }

        public override void OnDisconnected(Connection c, ConnectionInfo info)
        {
            base.OnDisconnected(c, info);
            if (_peers.TryRemove(c.Id, out var p)) Drop(p);
            PeersChanged?.Invoke();
        }

        static void Drop(Peer p)
        {
            foreach (var u in p.Udp.Values) u.Dispose();
            foreach (var t in p.Tcp.Values) t.Dispose();
        }

        public override void OnMessage(Connection c, NetIdentity id, IntPtr data, int size, long num, long time, int channel)
        {
            if (size < 1 || !_peers.TryGetValue(c.Id, out var peer)) return;
            var arr = new byte[size];
            System.Runtime.InteropServices.Marshal.Copy(data, arr, 0, size);
            ReadOnlySpan<byte> m = arr;
            switch (m[0])
            {
                case 1 when size >= 3:
                {
                    uint sid = (uint)(m[1] << 8 | m[2]);
                    var udp = peer.Udp.GetOrAdd(sid, s =>
                    {
                        var u = new UdpClient(new IPEndPoint(TunnelSource, 0));
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                while (true)
                                {
                                    var r = await u.ReceiveAsync();
                                    c.SendMessage(Msg(1, s, r.Buffer, true), SendType.Unreliable | SendType.NoNagle);
                                }
                            }
                            catch (Exception) { }
                        });
                        return u;
                    });
                    try { udp.Send(m[3..].ToArray(), size - 3, new IPEndPoint(IPAddress.Loopback, RelayPort)); } catch (Exception) { }
                    break;
                }
                case 2 when size >= 5:
                {
                    uint cid = (uint)(m[1] << 24 | m[2] << 16 | m[3] << 8 | m[4]);
                    var t = new TcpClient(new IPEndPoint(TunnelSource, 0)) { NoDelay = true };
                    peer.Tcp[cid] = t;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await t.ConnectAsync(IPAddress.Loopback, RoomPort);
                            var s = t.GetStream();
                            var buf = new byte[32768];
                            int n;
                            while ((n = await s.ReadAsync(buf)) > 0) c.SendMessage(Msg(3, cid, buf.AsSpan(0, n)), SendType.Reliable);
                        }
                        catch (Exception) { }
                        c.SendMessage(Msg(4, cid, default), SendType.Reliable);
                        if (peer.Tcp.TryRemove(cid, out var x)) x.Dispose();
                    });
                    break;
                }
                case 3 when size >= 5:
                {
                    uint cid = (uint)(m[1] << 24 | m[2] << 16 | m[3] << 8 | m[4]);
                    if (!peer.Tcp.TryGetValue(cid, out var t)) break;
                    var bytes = m[5..].ToArray();
                    // the connect may still be in progress: wait for it, writes stay in order (one pump thread)
                    for (int i = 0; i < 200 && !t.Connected; i++) Thread.Sleep(5);
                    try { t.GetStream().Write(bytes); } catch (Exception) { }
                    break;
                }
                case 4 when size >= 5:
                {
                    uint cid = (uint)(m[1] << 24 | m[2] << 16 | m[3] << 8 | m[4]);
                    if (peer.Tcp.TryRemove(cid, out var t)) { try { t.Client.Shutdown(SocketShutdown.Send); } catch (Exception) { } t.Dispose(); }
                    break;
                }
            }
        }
    }

    // ================================================================== joiner

    public sealed class Client : ConnectionManager
    {
        readonly CancellationTokenSource _stop = new();
        UdpClient? _udp;
        TcpListener? _tcp;
        readonly ConcurrentDictionary<IPEndPoint, uint> _sidOf = new();
        readonly ConcurrentDictionary<uint, IPEndPoint> _epOf = new();
        readonly ConcurrentDictionary<uint, TcpClient> _conns = new();
        int _nextSid, _nextCid;
        public bool IsConnected => Connected;
        public bool Failed { get; private set; }
        public event Action? Lost;

        /// <summary>Connects to the host and opens the local ports. Null + <paramref name="error"/> on failure.</summary>
        public static async Task<Client?> ConnectAsync(ulong hostSteamId, TimeSpan timeout)
        {
            Client c = DirectPort is int p
                ? SteamNetworkingSockets.ConnectNormal<Client>(NetAddress.From("127.0.0.1", (ushort)p))
                : SteamNetworkingSockets.ConnectRelay<Client>(hostSteamId, 0);
            Pump(() => c.Receive(64), c._stop.Token);
            var until = DateTime.UtcNow + timeout;
            while (!c.Connected && !c.Failed && DateTime.UtcNow < until) await Task.Delay(100);
            if (!c.Connected) { c.Stop(); return null; }
            try { c.OpenLocalPorts(); }
            catch (Exception) { c.Stop(); throw new InvalidOperationException("Ports 36000/36001 on this PC are in use (is a room hosted here, or another NB Multiplayer running?)."); }
            return c;
        }

        public override void OnConnectionChanged(ConnectionInfo info) { Log($"client: {info.State} {info.EndReason}"); base.OnConnectionChanged(info); }

        public override void OnDisconnected(ConnectionInfo info)
        {
            base.OnDisconnected(info);
            Failed = true;
            Lost?.Invoke();
        }

        void OpenLocalPorts()
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, TunnelPort + 1));
            _tcp = new TcpListener(IPAddress.Loopback, TunnelPort);
            _tcp.Start();
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        var r = await _udp.ReceiveAsync();
                        uint sid = _sidOf.GetOrAdd(r.RemoteEndPoint, ep => { uint s = (uint)Interlocked.Increment(ref _nextSid) & 0xFFFF; _epOf[s] = ep; return s; });
                        Connection.SendMessage(Msg(1, sid, r.Buffer, true), SendType.Unreliable | SendType.NoNagle);
                    }
                }
                catch (Exception) { }
            });
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        var t = await _tcp.AcceptTcpClientAsync();
                        t.NoDelay = true;
                        uint cid = (uint)Interlocked.Increment(ref _nextCid);
                        _conns[cid] = t;
                        Connection.SendMessage(Msg(2, cid, default), SendType.Reliable);
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var s = t.GetStream();
                                var buf = new byte[32768];
                                int n;
                                while ((n = await s.ReadAsync(buf)) > 0) Connection.SendMessage(Msg(3, cid, buf.AsSpan(0, n)), SendType.Reliable);
                            }
                            catch (Exception) { }
                            Connection.SendMessage(Msg(4, cid, default), SendType.Reliable);
                        });
                    }
                }
                catch (Exception) { }
            });
        }

        public override void OnMessage(IntPtr data, int size, long num, long time, int channel)
        {
            if (size < 1) return;
            var arr = new byte[size];
            System.Runtime.InteropServices.Marshal.Copy(data, arr, 0, size);
            ReadOnlySpan<byte> m = arr;
            switch (m[0])
            {
                case 1 when size >= 3:
                {
                    uint sid = (uint)(m[1] << 8 | m[2]);
                    if (_udp != null && _epOf.TryGetValue(sid, out var ep)) try { _udp.Send(m[3..].ToArray(), size - 3, ep); } catch (Exception) { }
                    break;
                }
                case 3 when size >= 5:
                {
                    uint cid = (uint)(m[1] << 24 | m[2] << 16 | m[3] << 8 | m[4]);
                    if (_conns.TryGetValue(cid, out var t)) try { t.GetStream().Write(m[5..]); } catch (Exception) { }
                    break;
                }
                case 4 when size >= 5:
                {
                    uint cid = (uint)(m[1] << 24 | m[2] << 16 | m[3] << 8 | m[4]);
                    if (_conns.TryRemove(cid, out var t)) { try { t.Client.Shutdown(SocketShutdown.Send); } catch (Exception) { } t.Dispose(); }
                    break;
                }
            }
        }

        public void Stop()
        {
            _stop.Cancel();
            try { _udp?.Dispose(); } catch (Exception) { }
            try { _tcp?.Stop(); } catch (Exception) { }
            foreach (var t in _conns.Values) t.Dispose();
            try { Close(); } catch (Exception) { }
        }
    }
}
