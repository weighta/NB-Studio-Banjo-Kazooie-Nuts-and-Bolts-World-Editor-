using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using NB.Core.IO;
using NB.Core.Live;

namespace NB.Multiplayer.Services;

/// <summary>
/// Showdown Town co-op network: every player sends its state ~30 times a second to the host, which forwards it to all
/// other players (and uses it itself). Transport = the Steam connection (message type 6) or UDP to the host's port
/// 36002. Packet: "NBCO", u8 version 1, u8 0, u16 name length, i64 player id, CoopState, name (UTF-8).
/// </summary>
public sealed class CoopNet : IDisposable
{
    public static readonly int UdpPort = Net.Port + 2;
    public readonly long MyId;
    readonly string _myName;
    readonly bool _isHost;
    readonly UdpClient? _udp;
    readonly IPEndPoint? _hostUdp;
    readonly SteamNet.Host? _steamHost;
    readonly SteamNet.Client? _steamClient;
    readonly CancellationTokenSource _stop = new();
    // host: peers by key ("udp:ip:port" / "steam:<connection>") -> sender
    readonly ConcurrentDictionary<string, (Action<byte[]> Send, DateTime Seen)> _peers = new();
    public readonly ConcurrentDictionary<long, (CoopState State, DateTime Seen, string Name)> Remotes = new();
    /// <summary>The room's Showdown Town time of day: 0 = not known yet, 1..4 = morning, midday, afternoon, night. The host
    /// decides it; joiners get it with the host's packets.</summary>
    public volatile int TimeOfDay;
    public static readonly string[] TimeNames = { "Random", "Morning", "Midday", "Afternoon", "Night" };

    CoopNet(long myId, string myName, bool host, UdpClient? udp, IPEndPoint? hostUdp, SteamNet.Host? sh, SteamNet.Client? sc)
    {
        MyId = myId; _myName = myName; _isHost = host; _udp = udp; _hostUdp = hostUdp; _steamHost = sh; _steamClient = sc;
        if (_udp != null) _ = Task.Run(ReceiveUdp);
        if (sh != null) sh.CoopReceived += (conn, p) => OnPacket(p, "steam:" + conn, b => sh.SendCoop(conn, b));
        if (sc != null) sc.CoopReceived += p => OnPacket(p, "host", null);
    }

    public static CoopNet StartHost(long myId, string myName, SteamNet.Host? steam)
    {
        UdpClient? udp = null;
        try { udp = new UdpClient(new IPEndPoint(IPAddress.Any, UdpPort)); } catch (SocketException) { }
        return new CoopNet(myId, myName, true, udp, null, steam, null);
    }

    public static CoopNet StartClient(long myId, string myName, SteamNet.Client? steam, string host)
    {
        if (steam != null) return new CoopNet(myId, myName, false, null, null, null, steam);
        var ep = new IPEndPoint(Dns.GetHostAddresses(host).First(a => a.AddressFamily == AddressFamily.InterNetwork), UdpPort);
        return new CoopNet(myId, myName, false, new UdpClient(0), ep, null, null);
    }

    byte[] Packet(long id, CoopState st, string name)
    {
        var nb = System.Text.Encoding.UTF8.GetBytes(name);
        var p = new byte[16 + CoopState.Size + nb.Length];
        p[0] = (byte)'N'; p[1] = (byte)'B'; p[2] = (byte)'C'; p[3] = (byte)'O'; p[4] = 1;
        BE.W16(p, 6, (ushort)nb.Length); BE.W64(p, 8, (ulong)id);
        st.Write().CopyTo(p, 16); nb.CopyTo(p, 16 + CoopState.Size);
        return p;
    }

    /// <summary>Weapon damage the local player dealt to <paramref name="target"/> (by player id), from position <paramref name="from"/>.</summary>
    public void SendDamage(long target, float amount, System.Numerics.Vector3 from)
    {
        var p = new byte[32];
        p[0] = (byte)'N'; p[1] = (byte)'B'; p[2] = (byte)'C'; p[3] = (byte)'O'; p[4] = 2;
        BE.W64(p, 8, (ulong)target); BE.WF32(p, 16, amount); BE.WF32(p, 20, from.X); BE.WF32(p, 24, from.Y); BE.WF32(p, 28, from.Z);
        Send(p);
    }

    /// <summary>Damage other players dealt to the local player (amount, attacker position).</summary>
    public readonly ConcurrentQueue<(float Amount, System.Numerics.Vector3 From)> Damage = new();

    void Send(byte[] p)
    {
        if (_isHost) { foreach (var peer in _peers.Values) Try(() => peer.Send(p)); }
        else if (_steamClient != null) Try(() => _steamClient.SendCoop(p));
        else if (_udp != null && _hostUdp != null) Try(() => _udp.Send(p, p.Length, _hostUdp));
    }

    int _sent;
    byte[] SettingsPacket()
    {
        var p = new byte[32]; p[0] = (byte)'N'; p[1] = (byte)'B'; p[2] = (byte)'C'; p[3] = (byte)'O'; p[4] = 3; p[8] = (byte)TimeOfDay;
        return p;
    }

    /// <summary>Joiner, before gameplay: tells the host this game exists (the host answers with the room settings).</summary>
    public void Hello()
    {
        if (_isHost) return;
        var p = new byte[32]; p[0] = (byte)'N'; p[1] = (byte)'B'; p[2] = (byte)'C'; p[3] = (byte)'O'; p[4] = 4; BE.W64(p, 8, (ulong)MyId);
        Send(p);
    }
    public void SendLocal(CoopState st)
    {
        Send(Packet(MyId, st, _myName));
        if (_isHost && TimeOfDay > 0 && _sent++ % 15 == 0) Send(SettingsPacket());   // twice a second: the room's time of day
    }

    static void Try(Action a) { try { a(); } catch (Exception) { } }

    async Task ReceiveUdp()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var r = await _udp!.ReceiveAsync(_stop.Token);
                var ep = r.RemoteEndPoint;
                OnPacket(r.Buffer, "udp:" + ep, _isHost ? b => _udp.Send(b, b.Length, ep) : null);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception) { }
        }
    }

    void OnPacket(byte[] p, string from, Action<byte[]>? reply)
    {
        if (p.Length < 32 || p[0] != 'N' || p[1] != 'B' || p[2] != 'C' || p[3] != 'O') return;
        if (p[4] == 3) { if (!_isHost && p[8] is >= 1 and <= 4) TimeOfDay = p[8]; return; }   // room settings from the host
        if (p[4] == 4)
        {
            // a joiner's game has started: register it and answer with the room's time of day at once (before its town loads)
            if (_isHost && reply != null) { _peers[from] = (reply, DateTime.UtcNow); if (TimeOfDay > 0) Try(() => reply(SettingsPacket())); }
            return;
        }
        if (p[4] == 2)
        {
            // damage: for the local player, or forwarded by the host to the player it is for
            if ((long)BE.U64(p, 8) == MyId) Damage.Enqueue((BE.F32(p, 16), new System.Numerics.Vector3(BE.F32(p, 20), BE.F32(p, 24), BE.F32(p, 28))));
            if (_isHost && reply != null)
            {
                _peers[from] = (reply, DateTime.UtcNow);
                foreach (var (key, peer) in _peers) if (key != from) Try(() => peer.Send(p));
            }
            return;
        }
        if (p.Length < 16 + CoopState.Size) return;
        long id = (long)BE.U64(p, 8);
        int nl = BE.U16(p, 6);
        string name = 16 + CoopState.Size + nl <= p.Length ? System.Text.Encoding.UTF8.GetString(p, 16 + CoopState.Size, nl) : "?";
        if (id != MyId) Remotes[id] = (CoopState.Read(p[16..(16 + CoopState.Size)]), DateTime.UtcNow, name);
        if (_isHost && reply != null)
        {
            _peers[from] = (reply, DateTime.UtcNow);
            foreach (var (key, peer) in _peers) if (key != from) Try(() => peer.Send(p));   // forward to the others
        }
    }

    /// <summary>Players heard from in the last 3 seconds.</summary>
    public Dictionary<long, CoopState> Live()
    {
        var cut = DateTime.UtcNow - TimeSpan.FromSeconds(3);
        foreach (var k in Remotes.Where(kv => kv.Value.Seen < cut).Select(kv => kv.Key).ToList()) Remotes.TryRemove(k, out _);
        foreach (var k in _peers.Where(kv => kv.Value.Seen < cut - TimeSpan.FromSeconds(30)).Select(kv => kv.Key).ToList()) _peers.TryRemove(k, out _);
        return Remotes.ToDictionary(kv => kv.Key, kv => kv.Value.State);
    }

    public void Dispose() { _stop.Cancel(); try { _udp?.Dispose(); } catch (Exception) { } }
}

/// <summary>Runs Showdown Town co-op for the game NB Multiplayer started: attach, then 30 Hz read / send / apply.</summary>
public sealed class CoopService : IDisposable
{
    readonly CoopNet _net;
    readonly int _pid;
    readonly string _xex;
    readonly uint _puppetBlueprint;
    readonly System.Numerics.Vector3 _park;
    readonly CancellationTokenSource _stop = new();
    public string Status { get; private set; } = "Waiting for the game to start...";

    public CoopService(CoopNet net, int gamePid, string xexPath, uint puppetBlueprint, System.Numerics.Vector3 park)
    {
        _net = net; _pid = gamePid; _xex = xexPath; _puppetBlueprint = puppetBlueprint; _park = park;
        new Thread(Run) { IsBackground = true, Name = "NB co-op" }.Start();
    }

    void Run()
    {
        byte[] probe;
        try
        {
            var img = NB.Core.Formats.XexFile.Read(File.ReadAllBytes(_xex)).GetImage();
            probe = img.AsSpan((int)(XeniaLive.TextStart - 0x82000000), 64).ToArray();
        }
        catch (Exception e) { Status = "Co-op could not read the game executable: " + e.Message; return; }
        XeniaLive? x = null; CoopSync? sync = null;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                if (x == null)
                {
                    x = XeniaLive.AttachPid(_pid, probe);
                    sync = new CoopSync(x, _puppetBlueprint, _park);
                }
                // before the town loads: every game in the room uses the room's time of day
                if (_net.TimeOfDay > 0) sync!.SetTimeOfDay(_net.TimeOfDay);
                else _net.Hello();
                if (x.Player == 0) { Status = "Waiting for gameplay (load a save or start a new game)..."; Thread.Sleep(500); continue; }
                var local = sync!.ReadLocal();
                _net.SendLocal(local);
                var remotes = _net.Live();
                while (_net.Damage.TryDequeue(out var hit)) sync.QueueDamage(hit.Amount, hit.From);
                sync.Apply(remotes);
                foreach (var (id, dmg) in sync.TakePuppetDamage()) _net.SendDamage(id, dmg, local.Position);
                Status = remotes.Count == 0 ? "In Showdown Town, waiting for other players..."
                    : $"Co-op: {string.Join(", ", _net.Remotes.Values.Select(r => r.Name))} - {sync.Status}";
                Thread.Sleep(33);
            }
            catch (ArgumentException) { Status = "The game has closed."; return; }      // process gone
            catch (Exception e) { Status = "Co-op: " + e.Message; x?.Dispose(); x = null; Thread.Sleep(2000); }
        }
        x?.Dispose();
    }

    public void Dispose() { _stop.Cancel(); _net.Dispose(); }
}
