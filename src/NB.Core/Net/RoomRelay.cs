using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace NB.Core.Net;

/// <summary>
/// UDP relay of the NB multiplayer overlay (Xenia side: src/xenia/kernel/nb_overlay.h). Every game socket of every
/// instance talks only to this relay, wrapping its datagrams in a 16-byte header
/// ('N' 'B', type, 0, src vip u32, src port u16, dst vip u32, dst port u16; big endian; type 0 = hello, 1 = data).
/// The relay remembers which real endpoint belongs to each (virtual address, game port) and forwards data packets to
/// the destination socket, or to every other instance's socket on that port for a broadcast (255.255.255.255).
/// Packets are only accepted when the sender's real IP owns the virtual address it claims (see
/// <see cref="RoomServer.VirtualAddressOwner"/>), so an instance cannot pose as another.
/// </summary>
public sealed class RoomRelay : IDisposable
{
    public Action<string>? Log { get; set; }
    public int Port { get; private set; }
    public long PacketsForwarded => Interlocked.Read(ref _forwarded);
    public long PacketsDropped => Interlocked.Read(ref _dropped);

    readonly Func<uint, IPAddress?> _owner;       // virtual address -> real IP that registered it (null = unknown)
    readonly Func<IPAddress, bool> _isLocal;      // is this real IP one of this PC's addresses?
    readonly ConcurrentDictionary<(uint Vip, ushort Port), IPEndPoint> _sockets = new();
    UdpClient? _udp;
    Thread? _thread;
    volatile bool _stop;
    long _forwarded, _dropped;
    readonly ConcurrentDictionary<string, DateTime> _warned = new();
    Timer? _status;
    long _lastForwarded = -1, _lastDropped = -1;
    readonly ConcurrentDictionary<(uint, uint), long> _flows = new();   // (src vip, dst vip) -> packets
    /// <summary>Diagnostics (env NB_RELAY_TRACE=1): per (src:port, dst:port, session key) packet/byte counts in the status line.</summary>
    static readonly bool Trace = Environment.GetEnvironmentVariable("NB_RELAY_TRACE") == "1";
    readonly ConcurrentDictionary<(uint, ushort, uint, ushort, ulong), (long Packets, long Bytes)> _detail = new();
    /// <summary>Diagnostics (env NB_RELAY_DUMP=N): hex dump of the first N data packets of every (src, dst, key) flow.</summary>
    static readonly int Dump = int.TryParse(Environment.GetEnvironmentVariable("NB_RELAY_DUMP"), out var nd) ? nd : 0;

    public RoomRelay(Func<uint, IPAddress?> owner, Func<IPAddress, bool> isLocal) { _owner = owner; _isLocal = isLocal; }

    /// <summary>Known game sockets: (virtual address, game port) -> real endpoint.</summary>
    public IReadOnlyList<(string Vip, ushort Port, IPEndPoint Endpoint)> Sockets =>
        _sockets.Select(kv => (VipText(kv.Key.Vip), kv.Key.Port, kv.Value)).OrderBy(x => x.Item1).ThenBy(x => x.Port).ToList();

    public static string VipText(uint v) => $"{v >> 24}.{(v >> 16) & 255}.{(v >> 8) & 255}.{v & 255}";

    public void Start(int port, IPAddress bind)
    {
        _udp = new UdpClient(new IPEndPoint(bind, port));
        // ignore ICMP "port unreachable" resets (Windows reports them on the next receive)
        try { _udp.Client.IOControl(unchecked((int)0x9800000C), new byte[] { 0 }, null); } catch (Exception) { }
        Port = port; _stop = false;
        _thread = new Thread(Run) { IsBackground = true, Name = "NB relay" };
        _thread.Start();
        _status = new Timer(_ => Status(), null, 10000, 10000);
        Log?.Invoke($"relay listening on udp {bind}:{port}");
    }

    public void Stop()
    {
        _stop = true;
        _status?.Dispose(); _status = null;
        _udp?.Dispose(); _udp = null;
        _thread?.Join(2000); _thread = null;
        _sockets.Clear();
    }

    public void Dispose() => Stop();

    /// <summary>Forgets every socket of a virtual address (player left / kicked).</summary>
    public void Forget(uint vip) { foreach (var k in _sockets.Keys.Where(k => k.Vip == vip).ToList()) _sockets.TryRemove(k, out _); }

    static uint U32(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
    static ushort U16(byte[] b, int o) => (ushort)(b[o] << 8 | b[o + 1]);

    void Run()
    {
        var any = new IPEndPoint(IPAddress.Any, 0);
        while (!_stop)
        {
            byte[] pkt; IPEndPoint from;
            try { from = any; pkt = _udp!.Receive(ref from); }
            catch (SocketException) { if (_stop) break; continue; }
            catch (ObjectDisposedException) { break; }
            catch (NullReferenceException) { break; }
            if (pkt.Length < 16 || pkt[0] != 'N' || pkt[1] != 'B') { Drop("not an NB packet", from); continue; }
            byte type = pkt[2];
            uint src = U32(pkt, 4); ushort sport = U16(pkt, 8);
            uint dst = U32(pkt, 10); ushort dport = U16(pkt, 14);
            if (!Owns(src, from.Address)) { Drop($"{VipText(src)} not owned by {from.Address}", from); continue; }
            _sockets[(src, sport)] = from;                    // hello or data: (re)learn this socket's endpoint
            if (type != 1) continue;
            _flows.AddOrUpdate((src, dst), 1, (_, n) => n + 1);
            if (Trace)
            {
                ulong key = pkt.Length >= 24 ? (ulong)U32(pkt, 16) << 32 | U32(pkt, 20) : 0;
                var d = _detail.AddOrUpdate((src, sport, dst, dport, key), (1, pkt.Length - 24), (_, v) => (v.Packets + 1, v.Bytes + pkt.Length - 24));
                if (d.Packets <= Dump && pkt.Length > 24)
                    Log?.Invoke($"{DateTime.Now:HH:mm:ss.fff} relay dump {VipText(src)}:{sport} -> {VipText(dst)}:{dport} key {key:X16} #{d.Packets} {pkt.Length - 24} B: {Convert.ToHexString(pkt, 24, Math.Min(pkt.Length - 24, 160))}");
            }
            if (dst == 0xFFFFFFFF || (dst & 0xFF) == 0xFF)
            {
                foreach (var (key, ep) in _sockets)
                    if (key.Port == dport && key.Vip != src) Send(pkt, ep);
            }
            else if (_sockets.TryGetValue((dst, dport), out var ep)) Send(pkt, ep);
            else Drop($"no socket {VipText(dst)}:{dport} yet", from);
        }
    }

    bool Owns(uint vip, IPAddress real)
    {
        if (real.IsIPv4MappedToIPv6) real = real.MapToIPv4();
        var owner = _owner(vip);
        if (owner == null) return false;
        if (owner.Equals(real)) return true;
        // instances on this PC register over loopback but may send from a LAN address, and vice versa
        return (IPAddress.IsLoopback(owner) || _isLocal(owner)) && (IPAddress.IsLoopback(real) || _isLocal(real));
    }

    void Send(byte[] pkt, IPEndPoint to)
    {
        try { _udp?.Send(pkt, pkt.Length, to); Interlocked.Increment(ref _forwarded); }
        catch (Exception) { Interlocked.Increment(ref _dropped); }
    }

    void Drop(string why, IPEndPoint from)
    {
        Interlocked.Increment(ref _dropped);
        // log each reason at most every 10 s
        if (_warned.TryGetValue(why, out var t) && DateTime.UtcNow - t < TimeSpan.FromSeconds(10)) return;
        _warned[why] = DateTime.UtcNow;
        Log?.Invoke($"relay: dropped packet from {from}: {why}");
    }

    /// <summary>Logs traffic every 10 s while it changes: totals, flows between virtual addresses, known sockets.</summary>
    void Status()
    {
        long f = PacketsForwarded, d = PacketsDropped;
        if (f == _lastForwarded && d == _lastDropped) return;
        _lastForwarded = f; _lastDropped = d;
        var flows = string.Join(", ", _flows.Select(kv => $"{VipText(kv.Key.Item1)}->{(kv.Key.Item2 == 0xFFFFFFFF ? "broadcast" : VipText(kv.Key.Item2))} {kv.Value}"));
        var socks = string.Join(", ", _sockets.Select(kv => $"{VipText(kv.Key.Vip)}:{kv.Key.Port}={kv.Value}"));
        Log?.Invoke($"relay: {f} forwarded, {d} dropped; flows [{flows}]; sockets [{socks}]");
        if (Trace)
            foreach (var kv in _detail.OrderBy(kv => kv.Key.Item5).ThenBy(kv => kv.Key.Item1))
                Log?.Invoke($"relay:   {VipText(kv.Key.Item1)}:{kv.Key.Item2} -> {VipText(kv.Key.Item3)}:{kv.Key.Item4} key {kv.Key.Item5:X16}: {kv.Value.Packets} pkts {kv.Value.Bytes} B");
    }
}
