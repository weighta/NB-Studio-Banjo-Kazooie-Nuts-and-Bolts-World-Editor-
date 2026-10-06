using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using NB.Core.IO;
using NB.Core.Live;

namespace NB.Multiplayer.Services;

/// <summary>
/// Showdown Town co-op network: every player sends its state ~30 times a second to the host, which forwards it to all
/// other players (and uses it itself). Transport = the Steam connection (message type 6) or UDP to the host's port
/// 36002. Packets start with "NBCO" and a type byte:
/// <list type="bullet">
/// <item>1 state: u8 protocol (<see cref="Protocol"/>), u16 name length, i64 player id, <see cref="CoopState"/>, name (UTF-8)</item>
/// <item>2 damage: i64 target, f32 amount, f32 x/y/z of the attacker</item>
/// <item>3 room settings (host): u8 time of day, u8 all-unlocked save</item>
/// <item>4 hello (joiner, before gameplay): i64 id; the host answers with the room settings</item>
/// <item>5 ping / 6 pong: u32 token (joiner &lt;-&gt; host round trip, for the latency estimate)</item>
/// <item>7 leave: i64 id (the player's game closed)</item>
/// <item>8 police (host): <see cref="PoliceSnapshot"/> from byte 8</item>
/// <item>9 vehicle design chunk: i64 owner, u32 design hash, u32 total length, u32 offset, u16 length, data from byte 32
/// (designs are a few KB: sent in pieces that fit one unreliable message, again every 2 seconds)</item>
/// <item>10 vehicle damage: i64 owner, <see cref="CoopDamage"/> from byte 16</item>
/// <item>11 shots: i64 owner, <see cref="CoopProjectiles.Write"/> from byte 16 (torpedoes, eggs, grenades, rockets...;
/// protocol 4: offsets in the vehicle's frame, shot numbers, homing targets and retargets)</item>
/// <item>12 loose pieces (protocol 4): i64 owner, <see cref="CoopPiece.Write"/> from byte 16 (parts that broke off, while
/// they move: at most 4 s, 15 times a second)</item>
/// </list>
/// </summary>
public sealed class CoopNet : IDisposable
{
    /// <summary>State packet layout version. Players with another version are not shown (and are reported).</summary>
    public const byte Protocol = 5;
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
    /// <summary>Remote players: latest state, when it arrived, name.</summary>
    public readonly ConcurrentDictionary<long, (CoopState State, DateTime Seen, string Name)> Remotes = new();
    /// <summary>Names of players whose NB Multiplayer speaks another co-op protocol (they need the same version).</summary>
    public readonly ConcurrentDictionary<string, byte> OtherVersions = new();
    /// <summary>The room's Showdown Town time of day: 0 = not known yet, 1..4 = morning, midday, afternoon, night. The host
    /// decides it; joiners get it with the room info and the host's packets.</summary>
    public volatile int TimeOfDay;
    /// <summary>The room plays with the all-unlocked save (host setting, reflected to joiners when they start the game).</summary>
    public volatile bool AllUnlocked;

    public static readonly string[] TimeNames = { "Random", "Morning", "Midday", "Afternoon", "Night" };
    /// <summary>Joiner: round trip to the host in milliseconds (smoothed; 0 = not measured yet, and always 0 on the host).</summary>
    public double RoundTripMs { get; private set; }
    /// <summary>Joiner: the host's latest police snapshot and when it arrived.</summary>
    public (PoliceSnapshot Snap, DateTime At)? Police { get; private set; }
    public bool IsHost => _isHost;
    /// <summary>Remote players' vehicle designs and damage (latest of each).</summary>
    public readonly ConcurrentDictionary<long, CoopDesign> Designs = new();
    public readonly ConcurrentDictionary<long, CoopDamage> Damages = new();
    readonly ConcurrentDictionary<(long Owner, uint Hash), DesignParts> _parts = new();
    sealed class DesignParts { public byte[] Data = Array.Empty<byte>(); public bool[] Got = Array.Empty<bool>(); public DateTime At; }
    const int DesignChunk = 1024;
    /// <summary>Shots other players fired (owner, shots, retargets of their homing shots in flight), replayed from their puppets.</summary>
    public readonly ConcurrentQueue<(long Owner, List<CoopShot> Shots, List<CoopRetarget> Retargets)> Shots = new();
    /// <summary>Other players' loose pieces (owner, pieces, when they arrived).</summary>
    public readonly ConcurrentQueue<(long Owner, List<CoopPiece> Pieces, DateTime At)> Pieces = new();
    /// <summary>Other players' measured accelerations (from their consecutive states).</summary>
    readonly ConcurrentDictionary<long, System.Numerics.Vector3> _accel = new();

    public void SendShots(IReadOnlyList<CoopShot> shots, IReadOnlyList<CoopRetarget>? retargets = null)
    {
        if (shots.Count == 0 && (retargets == null || retargets.Count == 0)) return;
        var body = CoopProjectiles.Write(shots, retargets);
        var p = Header(11, 16 + body.Length);
        BE.W64(p, 8, (ulong)MyId); body.CopyTo(p, 16);
        Send(p);
    }

    /// <summary>The local vehicle's loose pieces that still move.</summary>
    public void SendPieces(IReadOnlyList<CoopPiece> pieces)
    {
        if (pieces.Count == 0) return;
        var body = CoopPiece.Write(pieces);
        var p = Header(12, 16 + body.Length);
        BE.W64(p, 8, (ulong)MyId); body.CopyTo(p, 16);
        Send(p);
    }

    /// <summary>How old a packet of player <paramref name="id"/> is when it arrives here (their delay to the host + ours), seconds.</summary>
    public float DelayOf(long id) =>
        (float)(((Remotes.TryGetValue(id, out var r) ? r.State.DelayMs : 0) + RoundTripMs / 2) / 1000.0);

    /// <summary>Diagnostics: bytes sent by packet type since the start.</summary>
    public readonly long[] SentBytes = new long[16];

    /// <summary>The local player's vehicle design, in pieces.</summary>
    public void SendDesign(CoopDesign d)
    {
        for (int off = 0; off < d.Bytes.Length; off += DesignChunk)
        {
            int n = Math.Min(DesignChunk, d.Bytes.Length - off);
            var p = Header(9, 32 + n);
            BE.W64(p, 8, (ulong)MyId); BE.W32(p, 16, d.Hash); BE.W32(p, 20, (uint)d.Bytes.Length); BE.W32(p, 24, (uint)off); BE.W16(p, 28, (ushort)n);
            Array.Copy(d.Bytes, off, p, 32, n);
            Send(p);
        }
    }

    /// <summary>The local vehicle's damage (blocks below full health, parts that broke off).</summary>
    public void SendVehicleDamage(CoopDamage d)
    {
        var body = d.Write();
        var p = Header(10, 16 + body.Length);
        BE.W64(p, 8, (ulong)MyId); body.CopyTo(p, 16);
        Send(p);
    }

    void OnDesignChunk(byte[] p)
    {
        long owner = (long)BE.U64(p, 8);
        uint hash = BE.U32(p, 16), total = BE.U32(p, 20), off = BE.U32(p, 24), n = BE.U16(p, 28);
        if (owner == MyId || total < CoopDesign.HeaderSize || total > CoopDesign.HeaderSize + CoopDesign.BlockSize * CoopDesign.MaxBlocks
            || off % DesignChunk != 0 || off + n > total || 32 + n > p.Length || n == 0) return;
        if (Designs.TryGetValue(owner, out var have) && have.Hash == hash) return;   // already complete
        var parts = _parts.GetOrAdd((owner, hash), _ => new DesignParts { Data = new byte[total], Got = new bool[(total + DesignChunk - 1) / DesignChunk] });
        lock (parts)
        {
            if (parts.Data.Length != total) return;
            Array.Copy(p, 32, parts.Data, off, n);
            parts.Got[off / DesignChunk] = true;
            parts.At = DateTime.UtcNow;
            if (!parts.Got.All(g => g)) return;
        }
        _parts.TryRemove((owner, hash), out _);
        if (CoopDesign.From(parts.Data) is { } d && d.Hash == hash) Designs[owner] = d;
        foreach (var k in _parts.Where(kv => DateTime.UtcNow - kv.Value.At > TimeSpan.FromSeconds(30)).Select(kv => kv.Key).ToList()) _parts.TryRemove(k, out _);
    }

    /// <summary>Host: sends its police to every joiner.</summary>
    public void SendPolice(PoliceSnapshot snap)
    {
        if (!_isHost) return;
        var body = snap.Write();
        var p = Header(8, 8 + body.Length);
        body.CopyTo(p, 8);
        Send(p);
    }
    uint _seq;

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

    static byte[] Header(byte type, int length)
    {
        var p = new byte[Math.Max(32, length)];
        p[0] = (byte)'N'; p[1] = (byte)'B'; p[2] = (byte)'C'; p[3] = (byte)'O'; p[4] = type;
        return p;
    }

    byte[] Packet(long id, CoopState st, string name)
    {
        var nb = System.Text.Encoding.UTF8.GetBytes(name);
        var p = Header(1, 16 + CoopState.Size + nb.Length);
        p[5] = Protocol;
        BE.W16(p, 6, (ushort)nb.Length); BE.W64(p, 8, (ulong)id);
        st.Write().CopyTo(p, 16); nb.CopyTo(p, 16 + CoopState.Size);
        return p;
    }

    /// <summary>Weapon damage the local player dealt to <paramref name="target"/> (by player id), from position <paramref name="from"/>.</summary>
    public void SendDamage(long target, float amount, System.Numerics.Vector3 from)
    {
        var p = Header(2, 32);
        BE.W64(p, 8, (ulong)target); BE.WF32(p, 16, amount); BE.WF32(p, 20, from.X); BE.WF32(p, 24, from.Y); BE.WF32(p, 28, from.Z);
        Send(p);
    }

    /// <summary>Damage other players dealt to the local player (amount, attacker position).</summary>
    public readonly ConcurrentQueue<(float Amount, System.Numerics.Vector3 From)> Damage = new();

    void Send(byte[] p)
    {
        if (p.Length > 4 && p[4] < 16) Interlocked.Add(ref SentBytes[p[4]], p.Length * (_isHost ? Math.Max(1, _peers.Count) : 1));
        if (_isHost) { foreach (var peer in _peers.Values) Try(() => peer.Send(p)); }
        else if (_steamClient != null) Try(() => _steamClient.SendCoop(p));
        else if (_udp != null && _hostUdp != null) Try(() => _udp.Send(p, p.Length, _hostUdp));
    }

    int _sent;
    byte[] SettingsPacket()
    {
        var p = Header(3, 32); p[8] = (byte)TimeOfDay; p[9] = (byte)(AllUnlocked ? 1 : 0);
        return p;
    }

    /// <summary>Joiner, before gameplay: tells the host this game exists (the host answers with the room settings).</summary>
    public void Hello()
    {
        if (_isHost) return;
        var p = Header(4, 32); BE.W64(p, 8, (ulong)MyId);
        Send(p);
    }

    /// <summary>Joiner: the host said it closed the room.</summary>
    public volatile bool HostClosed;
    /// <summary>Joiner: when the host last answered a ping (MinValue = not yet: pings run while the game is attached).</summary>
    public DateTime LastHostReply = DateTime.MinValue;

    /// <summary>Host: tells every joiner the room is closing (sent a few times: UDP can drop one).</summary>
    public void CloseRoom()
    {
        if (!_isHost) return;
        var p = Header(13, 32);
        for (int i = 0; i < 3; i++) { Send(p); Thread.Sleep(30); }
    }

    /// <summary>The local game closed (or the player left): the others hide this player's puppet at once.</summary>
    public void Leave()
    {
        var p = Header(7, 32); BE.W64(p, 8, (ulong)MyId);
        Send(p);
    }

    readonly Stopwatch _clock = Stopwatch.StartNew();

    public void SendLocal(CoopState st)
    {
        st.Seq = ++_seq;
        st.DelayMs = (ushort)Math.Clamp(RoundTripMs / 2, 0, 2000);
        Send(Packet(MyId, st, _myName));
        _sent++;
        if (_isHost && TimeOfDay > 0 && _sent % 15 == 0) Send(SettingsPacket());   // twice a second: the room's settings
        if (!_isHost && _sent % 15 == 0)                                             // twice a second: latency to the host
        {
            var p = Header(5, 32); BE.W32(p, 8, (uint)_clock.ElapsedMilliseconds);
            Send(p);
        }
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
        switch (p[4])
        {
            case 3:   // room settings from the host
                if (!_isHost) { if (p[8] is >= 1 and <= 4) TimeOfDay = p[8]; AllUnlocked = p[9] == 1; }
                return;
            case 4:   // a joiner's game has started: register it and answer with the room settings at once (before its town loads)
                if (_isHost && reply != null) { _peers[from] = (reply, DateTime.UtcNow); if (TimeOfDay > 0) Try(() => reply(SettingsPacket())); }
                return;
            case 5:   // ping: the host answers at once
                if (_isHost && reply != null) { _peers[from] = (reply, DateTime.UtcNow); var q = (byte[])p.Clone(); q[4] = 6; Try(() => reply(q)); }
                return;
            case 13:  // the host closed the room
                if (!_isHost) HostClosed = true;
                return;
            case 6:   // pong: round trip to the host
                if (!_isHost)
                {
                    LastHostReply = DateTime.UtcNow;
                    double rtt = (uint)_clock.ElapsedMilliseconds - BE.U32(p, 8);
                    if (rtt >= 0 && rtt < 5000) RoundTripMs = RoundTripMs == 0 ? rtt : RoundTripMs * 0.8 + rtt * 0.2;
                }
                return;
            case 7:   // a player left
                Remotes.TryRemove((long)BE.U64(p, 8), out _);
                if (_isHost && reply != null) foreach (var (key, peer) in _peers) if (key != from) Try(() => peer.Send(p));
                return;
            case 2:
                // damage: for the local player, or forwarded by the host to the player it is for
                if ((long)BE.U64(p, 8) == MyId) Damage.Enqueue((BE.F32(p, 16), new System.Numerics.Vector3(BE.F32(p, 20), BE.F32(p, 24), BE.F32(p, 28))));
                if (_isHost && reply != null)
                {
                    _peers[from] = (reply, DateTime.UtcNow);
                    foreach (var (key, peer) in _peers) if (key != from) Try(() => peer.Send(p));
                }
                return;
            case 9:   // a vehicle design piece
            case 10:  // vehicle damage
            case 11:  // shots
            case 12:  // loose pieces
                if (p[4] == 9) OnDesignChunk(p);
                else if (p[4] == 11) { if ((long)BE.U64(p, 8) != MyId && Shots.Count < 256) { var (sh, rt) = CoopProjectiles.Read(p, 16); Shots.Enqueue(((long)BE.U64(p, 8), sh, rt)); } }
                else if (p[4] == 12) { if ((long)BE.U64(p, 8) != MyId && Pieces.Count < 256) Pieces.Enqueue(((long)BE.U64(p, 8), CoopPiece.Read(p, 16), DateTime.UtcNow)); }
                else if ((long)BE.U64(p, 8) != MyId && CoopDamage.Read(p, 16) is { } dm
                         && (!Damages.TryGetValue((long)BE.U64(p, 8), out var od) || dm.Seq > od.Seq || od.Seq - dm.Seq > 1000))
                    Damages[(long)BE.U64(p, 8)] = dm;
                if (_isHost && reply != null)
                {
                    _peers[from] = (reply, DateTime.UtcNow);
                    foreach (var (key, peer) in _peers) if (key != from) Try(() => peer.Send(p));
                }
                return;
            case 8:   // the host's police
                if (!_isHost && PoliceSnapshot.Read(p, 8) is { } ps
                    && (Police is not { } old || (ushort)(ps.Seq - old.Snap.Seq) < 0x8000 || DateTime.UtcNow - old.At > TimeSpan.FromSeconds(2)))
                    Police = (ps, DateTime.UtcNow);
                return;
            case 1:
                break;
            default:
                return;
        }
        long id = (long)BE.U64(p, 8);
        int nl = BE.U16(p, 6);
        if (p[5] != Protocol || p.Length < 16 + CoopState.Size)
        {
            // another NB Multiplayer version: its packets mean something else (shown in the room panel instead)
            int at = p.Length - nl;
            string other = nl > 0 && at >= 16 ? System.Text.Encoding.UTF8.GetString(p, at, nl) : "a player";
            OtherVersions[other] = p[5];
            if (_isHost && reply != null) _peers[from] = (reply, DateTime.UtcNow);
            return;
        }
        string name = 16 + CoopState.Size + nl <= p.Length ? System.Text.Encoding.UTF8.GetString(p, 16 + CoopState.Size, nl) : "?";
        var st = CoopState.Read(p[16..(16 + CoopState.Size)]);
        if (id != MyId)
        {
            // packets can arrive out of order: keep the newest (a restarted game starts counting again: accept a big step back)
            if (!Remotes.TryGetValue(id, out var old) || st.Seq > old.State.Seq || old.State.Seq - st.Seq > 300)
            {
                // acceleration from the last two states (sent 30 times a second): vertical flight is predicted with it
                var a = System.Numerics.Vector3.Zero;
                if (old.State.Mode == CoopMode.Vehicle && st.Mode == CoopMode.Vehicle && st.Seq > old.State.Seq && st.Seq - old.State.Seq <= 6)
                {
                    a = (st.Velocity - old.State.Velocity) / ((st.Seq - old.State.Seq) / 30f);
                    if (!float.IsFinite(a.X + a.Y + a.Z) || a.Length() > 200) a = System.Numerics.Vector3.Zero;
                    a = _accel.GetValueOrDefault(id) * 0.5f + a * 0.5f;     // smoothed: contacts make single samples spiky
                }
                _accel[id] = a;
                Remotes[id] = (st, DateTime.UtcNow, name);
            }
        }
        if (_isHost && reply != null)
        {
            _peers[from] = (reply, DateTime.UtcNow);
            foreach (var (key, peer) in _peers) if (key != from) Try(() => peer.Send(p));   // forward to the others
        }
    }

    /// <summary>
    /// Players heard from in the last 3 seconds, each with the age of their state: the time since it arrived plus the
    /// network delay it took to get here (sender to host, host to this player).
    /// </summary>
    public Dictionary<long, CoopRemote> Live()
    {
        var now = DateTime.UtcNow;
        var cut = now - TimeSpan.FromSeconds(3);
        foreach (var k in Remotes.Where(kv => kv.Value.Seen < cut).Select(kv => kv.Key).ToList()) Remotes.TryRemove(k, out _);
        foreach (var k in _peers.Where(kv => kv.Value.Seen < cut - TimeSpan.FromSeconds(30)).Select(kv => kv.Key).ToList()) _peers.TryRemove(k, out _);
        double own = RoundTripMs / 2;
        return Remotes.ToDictionary(kv => kv.Key, kv => new CoopRemote(kv.Value.State,
            (float)((now - kv.Value.Seen).TotalSeconds + (kv.Value.State.DelayMs + own) / 1000.0),
            Designs.GetValueOrDefault(kv.Key), Damages.GetValueOrDefault(kv.Key), _accel.GetValueOrDefault(kv.Key)));
    }

    public void Dispose() { _stop.Cancel(); try { _udp?.Dispose(); } catch (Exception) { } }
}

/// <summary>
/// Runs Showdown Town co-op for the room's game: follows the game NB Multiplayer started (and any restart of it), then
/// 30 Hz read / send / apply. Between games, and before the town loads, the player is reported absent.
/// </summary>
public sealed class CoopService : IDisposable
{
    readonly CoopNet _net;
    readonly Func<Process?> _game;
    readonly string _xex;
    readonly uint _puppetBlueprint;
    readonly System.Numerics.Vector3 _park;
    readonly CancellationTokenSource _stop = new();
    public string Status { get; private set; } = "Waiting for the game to start...";
    /// <summary>The local player's character (<see cref="NB.Core.Mods.Characters"/> index, 0 = Banjo), set by the UI:
    /// sent to the others (protocol 5; 0 while the local game has no Character Select mod). The local game's mailbox is
    /// written by the character service, not here.</summary>
    public int Character { get => _character; set => _character = value; }
    volatile int _character;
    /// <summary>What the local game reports (for the room panel).</summary>
    public CoopMode LocalMode => _local.Mode;
    /// <summary>The local player's last state (for the room panel).</summary>
    public CoopState Local => _local;
    /// <summary>The name of the local player's vehicle design (null until the game serialized it).</summary>
    public string? LocalVehicle { get; private set; }
    readonly Stopwatch _tick = Stopwatch.StartNew();
    long _nextSend, _nextPolice, _nextDesign, _nextDamage, _forceDamage, _nextShots, _nextPieces, _nextLog;
    uint _sentDesign, _damageSeq;
    int _shotTicks;
    CoopDamage? _sentDamage;
    ushort _policeSeq;
    int _policeTicks;
    CoopState _local;
    /// <summary>How often the puppets are steered (ms); NB_COOP_APPLY_MS overrides it (tests).</summary>
    static readonly int ApplyIntervalMs = int.TryParse(Environment.GetEnvironmentVariable("NB_COOP_APPLY_MS"), out var ms) ? ms : 8;

    /// <param name="game">The game process NB Multiplayer started for this room (it may be closed and started again).</param>
    public CoopService(CoopNet net, Func<Process?> game, string xexPath, uint puppetBlueprint, System.Numerics.Vector3 park)
    {
        _net = net; _game = game; _xex = xexPath; _puppetBlueprint = puppetBlueprint; _park = park;
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
        XeniaLive? x = null; CoopSync? sync = null; CoopPolice? police = null; CoopProjectiles? shots = null; int pid = 0;
        void Detach() { try { x?.Dispose(); } catch (Exception) { } x = null; sync = null; police = null; shots = null; pid = 0; }
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var game = _game();
                bool running = game != null && !SafeExited(game);
                if (!running || game!.Id != pid)
                {
                    if (pid != 0) _net.Leave();                     // the game closed (or a new one started): others hide us now
                    Detach();
                    _local = default;
                    if (!running)
                    {
                        Status = "The game is not running.";
                        _net.SendLocal(default);                    // still in the room: absent
                        Thread.Sleep(500); continue;
                    }
                    pid = game!.Id;
                    x = XeniaLive.AttachPid(pid, probe);
                    sync = new CoopSync(x, _puppetBlueprint, _park);
                }
                // before the town loads: every game in the room uses the room's time of day
                if (_net.TimeOfDay > 0) sync!.SetTimeOfDay(_net.TimeOfDay);
                else _net.Hello();
                // 30 times a second: read and send the local player
                if (_tick.ElapsedMilliseconds >= _nextSend)
                {
                    _nextSend = _tick.ElapsedMilliseconds + 33;
                    _local = sync!.ReadLocal();
                    TestCharacter(sync, x!);
                    _local.Character = (byte)(sync.HasCharselMod && _character is > 0 and < 256 ? _character : 0);
                    _net.SendLocal(_local);
                }
                // the local vehicle's design (at once when it changes, again every 2 s for players who join later or lost a
                // piece) and its damage (4 times a second when it changes, every second anyway)
                long now = _tick.ElapsedMilliseconds;
                LocalVehicle = _local.Design != 0 ? sync.LocalDesign?.Name : null;
                if (sync!.LocalDesign is { } design && _local.Design == design.Hash && (design.Hash != _sentDesign || now >= _nextDesign))
                {
                    _net.SendDesign(design); _sentDesign = design.Hash; _nextDesign = now + 2000;
                }
                // parts that broke off the local vehicle: their flight, 15 times a second while they move; a break sends
                // the damage at once (the others break the same blocks off their puppet without waiting for the 4 Hz tick)
                if (now >= _nextPieces && _local.Design != 0)
                {
                    _nextPieces = now + 66;
                    _net.SendPieces(sync.ReadLocalPieces());
                    if (sync.LocalBlocksLost) _nextDamage = _forceDamage = 0;
                }
                if (now >= _nextDamage && _local.Design != 0)
                {
                    _nextDamage = now + 250;
                    if (sync.ReadLocalDamage() is { } dmg && (!dmg.SameAs(_sentDamage) || now >= _forceDamage))
                    {
                        dmg.Seq = ++_damageSeq;
                        _net.SendVehicleDamage(dmg); _sentDamage = dmg; _forceDamage = now + 1000;
                    }
                }
                if (_local.Mode is CoopMode.Absent or CoopMode.Garage)
                {
                    var ch = NB.Core.Mods.Characters.ByIndex(_character);
                    Status = x!.Player == 0 ? "Waiting for gameplay (load a save or start a new game)..."
                        : ch.TownOnly && sync.HasCharselMod
                            ? $"Waiting for Showdown Town... (outside the town you are Banjo: {ch.Name} is a Showdown Town character and is back as soon as you return to town)"
                            : "Waiting for Showdown Town...";
                    Thread.Sleep(100); continue;
                }
                // steering the puppets runs faster than the network (the game simulates at 60 Hz; between two writes the
                // puppet's AI driver brakes it): every ~8 ms
                var remotes = _net.Live();
                while (_net.Damage.TryDequeue(out var hit)) sync!.QueueDamage(hit.Amount, hit.From);
                sync!.Apply(remotes);
                // Showdown Town police: the host's game is the reference (15 times a second); joiners follow it
                police ??= new CoopPolice(x!);
                if (_net.IsHost)
                {
                    if (_tick.ElapsedMilliseconds >= _nextPolice) { _nextPolice = _tick.ElapsedMilliseconds + 66; _net.SendPolice(police.Read(++_policeSeq)); }
                }
                else if (++_policeTicks % 2 == 0 && _net.Police is { } ps && DateTime.UtcNow - ps.At < TimeSpan.FromSeconds(3))
                    police.Apply(ps.Snap, (float)((DateTime.UtcNow - ps.At).TotalSeconds + _net.RoundTripMs / 2000));
                foreach (var (id, dmg) in sync.TakePuppetDamage()) _net.SendDamage(id, dmg, _local.Position);
                // weapons: the local vehicle's shots out (60 times a second), other players' shots replayed from their puppets
                shots ??= new CoopProjectiles(x!);
                if (shots.HasMod)
                {
                    if (_tick.ElapsedMilliseconds >= _nextShots)
                    {
                        _nextShots = _tick.ElapsedMilliseconds + 16;
                        var mine = shots.ReadShots(sync.LocalVehicle, sync.PlayerOfTarget);
                        _net.SendShots(mine, shots.Retargets);
                    }
                    while (_net.Shots.TryDequeue(out var fired))
                    {
                        uint pup = sync.PuppetOf(fired.Owner);
                        if (pup != 0) shots.Replay(pup, fired.Owner, fired.Shots, _net.DelayOf(fired.Owner));
                        shots.Retarget(fired.Owner, fired.Retargets);
                    }
                    shots.Pump(sync.IsLivePuppet, p => sync.TargetObjectOf(p, _net.MyId));
                    if (++_shotTicks % 250 == 0) shots.Forget(sync.IsLivePuppet);
                }
                else _net.Shots.Clear();
                while (_net.Pieces.TryDequeue(out var pc))
                    sync.QueuePieces(pc.Owner, pc.Pieces, _net.DelayOf(pc.Owner) + (float)(DateTime.UtcNow - pc.At).TotalSeconds);
                Diagnostics(sync, shots, remotes);
                Status = remotes.Count == 0 ? "In Showdown Town, waiting for other players..."
                    : $"Co-op: {string.Join(", ", _net.Remotes.Values.Select(r => r.Name))} - {sync.Status}";
                Thread.Sleep(ApplyIntervalMs);
            }
            catch (ArgumentException) { Status = "The game has closed."; Detach(); Thread.Sleep(500); }      // process gone: wait for a new one
            catch (Exception e) { Status = "Co-op: " + e.Message; Detach(); Thread.Sleep(2000); }
        }
        if (pid != 0) _net.Leave();
        Detach();
    }

    static bool SafeExited(Process p) { try { return p.HasExited; } catch (Exception) { return true; } }

    // ---- tests only: NB_COOP_TEST_CHARACTER=<file with a character key> sets Character from that file (checked every
    // second) and writes the local mailbox like the UI's character service would (test copies have no picker)
    static readonly string? TestCharacterFile = Environment.GetEnvironmentVariable("NB_COOP_TEST_CHARACTER");
    long _testCharAt;
    void TestCharacter(CoopSync sync, XeniaLive x)
    {
        if (TestCharacterFile == null || _tick.ElapsedMilliseconds < _testCharAt) return;
        _testCharAt = _tick.ElapsedMilliseconds + 1000;
        try
        {
            var c = NB.Core.Mods.Characters.Get(File.Exists(TestCharacterFile) ? File.ReadAllText(TestCharacterFile).Trim() : "banjo");
            _character = c.Index;
            if (!sync.HasCharselMod) return;
            var (town, any) = NB.Core.Mods.Characters.MailboxWords(c);
            if (x.U32(NB.Core.Mods.Characters.MailboxTown) != town || x.U32(NB.Core.Mods.Characters.MailboxAny) != any)
            {
                var b = new byte[8]; BE.W32(b, 0, town); BE.W32(b, 4, any);
                x.Write(NB.Core.Mods.Characters.MailboxTown, b);
            }
        }
        catch (IOException) { }
    }

    // ---- diagnostics (tests): NB_COOP_LOG=<file> appends a line every 2 s
    static readonly string? LogPath = Environment.GetEnvironmentVariable("NB_COOP_LOG");
    long _lastLoop, _maxGap;
    long[] _sentAt = new long[16];
    void Diagnostics(CoopSync sync, CoopProjectiles? shots, Dictionary<long, CoopRemote> remotes)
    {
        long t = _tick.ElapsedMilliseconds;
        if (_lastLoop != 0) _maxGap = Math.Max(_maxGap, t - _lastLoop);
        _lastLoop = t;
        if (LogPath == null || t < _nextLog) return;
        double secs = _nextLog == 0 ? Math.Max(1, t / 1000.0) : (t - _nextLog + 2000) / 1000.0;
        _nextLog = t + 2000;
        var sent = _net.SentBytes.Select(v => Interlocked.Read(ref v)).ToArray();
        string rate = string.Join(" ", Enumerable.Range(0, 16).Where(i => sent[i] != _sentAt[i]).Select(i => $"t{i}={(sent[i] - _sentAt[i]) / secs:F0}B/s"));
        _sentAt = sent;
        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} gap {_maxGap}ms scan {sync.LastScanMs:F0}ms remotes {remotes.Count} " +
                $"shots replayed {shots?.Replayed} skipped {shots?.Skipped} locked {shots?.Locked} retargeted {shots?.Retargeted} pieces {sync.PieceTicks}/{sync.PiecesPlaced} reattached {sync.Reattached} towns {sync.TownSessions} teleports {sync.Teleports} walking {sync.Walking} chars {sync.CharacterStatus} foot {string.Join(",", sync.FootRequests.Select(kv => kv.Key + ":" + kv.Value))} " +
                $"vreq {string.Join(",", sync.VehicleRequests.Select(kv => kv.Key + ":" + kv.Value))} sent {rate} | {sync.Assignments} | {sync.LastVehicleEvent}" + Environment.NewLine);
        }
        catch (IOException) { }
        _maxGap = 0;
    }

    public void Dispose() { _stop.Cancel(); try { _net.Leave(); } catch (Exception) { } _net.Dispose(); }
}
