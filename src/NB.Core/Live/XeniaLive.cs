using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using NB.Core.IO;

namespace NB.Core.Live;

/// <summary>
/// Runtime access to a running Xenia Canary (Windows): guest memory is mapped at a fixed host base, big-endian. This is
/// the engine of NB Studio's Live panel (runtime "mod menu"). Every operation here was verified in Xenia on
/// 2026-09-28 (seattle/TESTLOG.md T13, T28, T36):
///   player position [[0x82FAC7AC]+0xCB0]; teleport = shift the player's dynamic hkMotionStates; gravity = hkpWorld+0x10;
///   photo camera (free-fly "noclip" camera) [[0x82FAC7AC]+0x15B0]+0x40 (+0x60 position, +0xA0 pitch/yaw radians).
/// </summary>
public sealed class XeniaLive : IDisposable
{
    const uint PROCESS_VM_READ = 0x10, PROCESS_VM_WRITE = 0x20, PROCESS_VM_OPERATION = 0x08, PROCESS_QUERY_INFORMATION = 0x400;
    [DllImport("kernel32", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32", SetLastError = true)] static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
    [DllImport("kernel32", SetLastError = true)] static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr written);
    [DllImport("kernel32", SetLastError = true)] static extern IntPtr VirtualQueryEx(IntPtr h, IntPtr addr, out MemInfo info, IntPtr len);
    [DllImport("kernel32", SetLastError = true)] static extern bool VirtualProtectEx(IntPtr h, IntPtr addr, IntPtr size, uint newProtect, out uint oldProtect);
    [StructLayout(LayoutKind.Sequential)]
    struct MemInfo { public IntPtr BaseAddress, AllocationBase; public uint AllocationProtect, _a; public IntPtr RegionSize; public uint State, Protect, Type, _b; }

    public const uint PlayerPtr = 0x82FAC7AC, FrameCounter = 0x82FAC644, CameraMatrix = 0x82FADC30, TextStart = 0x821E0000;
    readonly IntPtr _h;
    public long Base { get; }
    public int Pid { get; }

    XeniaLive(IntPtr h, long b, int pid) { _h = h; Base = b; Pid = pid; }

    /// <summary>Process names of the emulators/ports that run the game: NB's Xenia build (NB Studio's F5 test and NB
    /// Multiplayer), plain Xenia Canary, Xenia, and reNut.</summary>
    public static readonly string[] GameProcessNames = { "xenia_canary_netplay", "xenia_canary", "xenia", "renut" };

    /// <summary>
    /// Attaches to a running game: <paramref name="preferPid"/> first (e.g. the Xenia NB Studio started), then the newest
    /// process of <see cref="GameProcessNames"/> whose guest memory holds the game. <paramref name="textProbe"/> = start
    /// of the decrypted .text (for locating the guest base).
    /// </summary>
    public static XeniaLive Attach(byte[] textProbe, int? preferPid = null)
    {
        var procs = GameProcessNames.SelectMany(n => Process.GetProcessesByName(n))
            .OrderByDescending(p => p.Id == preferPid).ThenByDescending(p => { try { return p.StartTime; } catch (Exception) { return DateTime.MinValue; } }).ToList();
        if (procs.Count == 0) throw new InvalidOperationException("The game is not running: start it with Test in Xenia (F5) or Build > Launch Workspace in Xenia.");
        Exception? last = null;
        foreach (var p in procs)
        {
            try { return AttachPid(p.Id, textProbe); }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException) { last = e; }
        }
        throw new InvalidOperationException($"Xenia is running ({string.Join(", ", procs.Select(p => p.ProcessName + ".exe").Distinct())}) but the game isn't loaded in it yet" +
            (last != null ? $" ({last.Message})" : "") + ".");
    }

    /// <summary>Attaches to one Xenia process (any build, e.g. the NB netplay build that NB Multiplayer starts).</summary>
    public static XeniaLive AttachPid(int pid, byte[] textProbe)
    {
        var p = Process.GetProcessById(pid);
        var h = OpenProcess(PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION | PROCESS_QUERY_INFORMATION, false, p.Id);
        if (h == IntPtr.Zero) throw new InvalidOperationException("cannot open the Xenia process (run as the same user)");
        foreach (long b in new long[] { 0x100000000, 0x200000000, 0x300000000, 0x400000000, 0x10000000000 })
        {
            var buf = new byte[textProbe.Length];
            if (ReadProcessMemory(h, (IntPtr)(b + TextStart), buf, buf.Length, out _) && buf.AsSpan().SequenceEqual(textProbe)) return new XeniaLive(h, b, p.Id);
        }
        CloseHandle(h);
        throw new InvalidOperationException("guest memory base not found (is the game running?)");
    }

    public void Dispose() => CloseHandle(_h);

    public byte[] Read(uint va, int n)
    {
        var b = new byte[n];
        return ReadProcessMemory(_h, (IntPtr)(Base + va), b, n, out var got) ? b[..(int)got] : Array.Empty<byte>();
    }
    public void Write(uint va, byte[] d) { if (!WriteProcessMemory(_h, (IntPtr)(Base + va), d, d.Length, out _)) throw new InvalidOperationException($"write 0x{va:X8} failed"); }

    /// <summary>Writes into guest code (reNut keeps the game's code pages read-only): the page is made writable for the
    /// write and its protection put back.</summary>
    public void WriteCode(uint va, byte[] d)
    {
        var at = (IntPtr)(Base + va);
        if (WriteProcessMemory(_h, at, d, d.Length, out _)) return;
        if (!VirtualProtectEx(_h, at, d.Length, 0x04 /* PAGE_READWRITE */, out uint old)) throw new InvalidOperationException($"unprotect 0x{va:X8} failed");
        try { if (!WriteProcessMemory(_h, at, d, d.Length, out _)) throw new InvalidOperationException($"write 0x{va:X8} failed"); }
        finally { VirtualProtectEx(_h, at, d.Length, old, out _); }
    }
    public uint U32(uint va) { var b = Read(va, 4); return b.Length == 4 ? BE.U32(b, 0) : 0; }
    public Vector3 V3(uint va) { var b = Read(va, 12); return b.Length == 12 ? new(BE.F32(b, 0), BE.F32(b, 4), BE.F32(b, 8)) : default; }
    public void WV3(uint va, Vector3 v) { var b = new byte[12]; BE.WF32(b, 0, v.X); BE.WF32(b, 4, v.Y); BE.WF32(b, 8, v.Z); Write(va, b); }

    public bool Running() { uint a = U32(FrameCounter); Thread.Sleep(120); return U32(FrameCounter) != a; }
    public uint Player => U32(PlayerPtr);
    /// <summary>Position of the player's vehicle (or of Banjo when no vehicle is spawned).</summary>
    public Vector3 PlayerPosition => V3(Player + 0xCB0);
    public Vector3 CameraPosition => V3(CameraMatrix);

    /// <summary>Guest addresses (4-byte aligned, in RW regions) holding the big-endian value <paramref name="value"/>.</summary>
    public List<uint> FindU32(uint value, uint lo = 0x40000000, ulong hi = 0x60000000)
    {
        var res = new List<uint>();
        byte b0 = (byte)(value >> 24), b1 = (byte)(value >> 16), b2 = (byte)(value >> 8), b3 = (byte)value;
        foreach (var (va, size) in Regions(lo, hi))
            for (long off = 0; off < size; off += 1 << 24)
            {
                int n = (int)Math.Min(1 << 24, size - off);
                var d = Read((uint)(va + off), n);
                for (int i = d.AsSpan().IndexOf(b0); i >= 0 && i + 3 < d.Length;)
                {
                    if ((i & 3) == 0 && d[i + 1] == b1 && d[i + 2] == b2 && d[i + 3] == b3) res.Add((uint)(va + off + i));
                    int next = d.AsSpan(i + 1).IndexOf(b0);
                    if (next < 0) break;
                    i += next + 1;
                }
            }
        return res;
    }

    /// <summary>Guest RW regions in [lo, hi) (for scans).</summary>
    public IEnumerable<(uint Va, int Size)> Regions(uint lo = 0x40000000, ulong hi = 0x100000000)
    {
        long a = Base + lo;
        while (a < Base + (long)hi)
        {
            if (VirtualQueryEx(_h, (IntPtr)a, out var mi, Marshal.SizeOf<MemInfo>()) == IntPtr.Zero) break;
            long rb = (long)mi.BaseAddress, rs = (long)mi.RegionSize;
            if (mi.State == 0x1000 && (mi.Protect is 0x04 or 0x40 or 0x02 or 0x20)) yield return ((uint)(rb - Base), (int)Math.Min(rs, int.MaxValue));
            a = rb + rs;
        }
    }

    /// <summary>
    /// Dynamic Havok motion states whose translation is within <paramref name="r"/> of <paramref name="p"/>: translation
    /// with a unit 3x3 rotation before it and centre-of-mass copies after it, dynamic (non-zero com0.w).
    /// </summary>
    public List<uint> BodiesNear(Vector3 p, float r = 8f)
    {
        var res = new List<uint>();
        foreach (var (va, size) in Regions())
            for (int off = 0; off < size; off += 1 << 24)
            {
                int n = Math.Min(1 << 24, size - off);
                var d = Read((uint)(va + off), n);
                for (int o = 0x30; o + 0x50 <= d.Length; o += 16)
                {
                    float x = BE.F32(d, o), y = BE.F32(d, o + 4), z = BE.F32(d, o + 8);
                    if (!(MathF.Abs(x - p.X) < r && MathF.Abs(y - p.Y) < r && MathF.Abs(z - p.Z) < r)) continue;
                    bool unit = true;
                    for (int k = 0; k < 3 && unit; k++)
                    {
                        float a = BE.F32(d, o - 0x30 + 16 * k), b = BE.F32(d, o - 0x2C + 16 * k), c = BE.F32(d, o - 0x28 + 16 * k);
                        unit = MathF.Abs(MathF.Sqrt(a * a + b * b + c * c) - 1) < 1e-3f;
                    }
                    var t = new Vector3(x, y, z);
                    var c0 = new Vector3(BE.F32(d, o + 0x10), BE.F32(d, o + 0x14), BE.F32(d, o + 0x18));
                    var c1 = new Vector3(BE.F32(d, o + 0x20), BE.F32(d, o + 0x24), BE.F32(d, o + 0x28));
                    if (unit && Vector3.Distance(t, c0) < 4 && Vector3.Distance(t, c1) < 4 && BE.F32(d, o + 0x1C) != 0) res.Add((uint)(va + off + o));
                }
            }
        return res;
    }

    /// <summary>Moves the player's vehicle (its bodies within 3 units of the player position) to <paramref name="target"/>.</summary>
    public int TeleportVehicle(Vector3 target)
    {
        var p = PlayerPosition;
        var bs = BodiesNear(p).Where(b => Vector3.Distance(V3(b), p) < 3f).ToList();
        var d = target - p;
        foreach (var b in bs) foreach (uint o in new uint[] { 0, 0x10, 0x20 }) WV3(b + o, V3(b + o) + d);
        return bs.Count;
    }

    /// <summary>
    /// Moves Banjo on foot: his character body is the dynamic body near the camera that is not the vehicle; it is found
    /// by nudging the stick (<paramref name="nudge"/> must press and release the stick) and caching the address.
    /// </summary>
    public int TeleportFoot(Vector3 target, Action nudge)
    {
        var cam = CameraPosition; var veh = PlayerPosition;
        uint body = 0;
        if (_footBody != 0 && BodiesNear(cam, 10f).Contains(_footBody)) body = _footBody;
        else
        {
            var bs = BodiesNear(cam, 14f).Where(b => Vector3.Distance(V3(b), veh) > 3f).ToList();
            var before = bs.ToDictionary(b => b, b => V3(b));
            nudge();
            var moved = bs.Where(b => { float m = Vector3.Distance(V3(b), before[b]); return m > 0.3f && m < 3f; }).ToList();
            if (moved.Count != 1) return 0;
            body = _footBody = moved[0];
        }
        var d = target - V3(body + 0x20);
        foreach (uint o in new uint[] { 0, 0x10, 0x20 }) WV3(body + o, V3(body + o) + d);
        return 1;
    }
    uint _footBody;

    /// <summary>The hkpWorld: found from the world-box copy in the level object (see docs/FORMATS §13.5); gravity at +0x10.</summary>
    /// <summary>The hkpWorld class's vtable in the game image (every hkpWorld starts with it).</summary>
    public const uint HkpWorldVtable = 0x82198D50;

    /// <summary>The level's physics world through the game's pointer chain (cheap: five reads), 0 while no level is
    /// loaded. W = [[[[0x82FAB0FC]+4]+0x15B0]+0x1D0+0x15B4], hkpWorld = [W+0x6D8] (research 151 §7).</summary>
    public uint LevelHavokWorld()
    {
        uint a = U32(0x82FAB0FC), b = a != 0 ? U32(a + 4) : 0, c = b != 0 ? U32(b + 0x15B0) : 0, w = c != 0 ? U32(c + 0x1D0 + 0x15B4) : 0;
        uint hw = w != 0 ? U32(w + 0x6D8) : 0;
        return hw != 0 && IsHavokWorld(hw) ? hw : 0;
    }

    /// <summary>An hkpWorld: its vtable, gravity (0, g, 0) with -100 &lt; g ≤ 0... or any g the Live tab may have set, and a
    /// sensible broadphase box at +0x2D0 / +0x2E0.</summary>
    public bool IsHavokWorld(uint w)
    {
        if (w == 0) return false;
        var d = Read(w, 0x2F0);
        if (d.Length < 0x2F0 || BE.U32(d, 0) != HkpWorldVtable) return false;
        if (BE.F32(d, 0x10) != 0 || BE.F32(d, 0x18) != 0 || !(MathF.Abs(BE.F32(d, 0x14)) < 1000)) return false;
        float x0 = BE.F32(d, 0x2D0), z0 = BE.F32(d, 0x2D8), x1 = BE.F32(d, 0x2E0), z1 = BE.F32(d, 0x2E8);
        return x1 - x0 > 50 && z1 - z0 > 50 && x1 - x0 < 100000 && z1 - z0 < 100000;
    }

    public uint FindHavokWorld()
    {
        uint hw = LevelHavokWorld();
        if (hw != 0) return hw;
        // fallback scan
        // the hkpWorld: its vtable at +0, gravity (0, -g, 0) at +0x10, broadphase box at +0x2D0/+0x2E0 (min < max). 1.16 and
        // older took any vtable in the image here and could pick a wrong object (gravity 0, writes without effect)
        foreach (var (va, size) in Regions())
            for (int off = 0; off < size; off += 1 << 24)
            {
                int n = Math.Min(1 << 24, size - off);
                var d = Read((uint)(va + off), n);
                for (int o = 0; o + 0x2F0 <= d.Length; o += 16)
                {
                    uint vt = BE.U32(d, o);
                    if (vt != HkpWorldVtable) continue;
                    if (BE.F32(d, o + 0x10) != 0 || BE.F32(d, o + 0x18) != 0) continue;
                    float g = BE.F32(d, o + 0x14);
                    if (!(g <= 0 && g > -100)) continue;   // 0 allowed: the Live tab can set zero gravity
                    float x0 = BE.F32(d, o + 0x2D0), y0 = BE.F32(d, o + 0x2D4), z0 = BE.F32(d, o + 0x2D8), x1 = BE.F32(d, o + 0x2E0), y1 = BE.F32(d, o + 0x2E4), z1 = BE.F32(d, o + 0x2E8);
                    if (x1 - x0 > 50 && y1 - y0 > 20 && z1 - z0 > 50 && x1 - x0 < 100000 && z1 - z0 < 100000) return (uint)(va + off + o);
                }
            }
        return 0;
    }

    /// <summary>Diagnostics: the pointer chain to the hkpWorld and what the scan finds.</summary>
    public string DescribeHavokWorld()
    {
        uint a = U32(0x82FAB0FC), b = a != 0 ? U32(a + 4) : 0, c = b != 0 ? U32(b + 0x15B0) : 0, w = c != 0 ? U32(c + 0x1D0 + 0x15B4) : 0;
        uint hw = w != 0 ? U32(w + 0x6D8) : 0;
        string s = $"chain {a:X8} > {b:X8} > {c:X8} > {w:X8} > hkpWorld {hw:X8}";
        if (hw != 0) s += $": vtable {U32(hw):X8} gravity {V3(hw + 0x10)} box {V3(hw + 0x2D0)}..{V3(hw + 0x2E0)}";
        return s;
    }

    public float GetGravity(uint world) => V3(world + 0x10).Y;
    public void SetGravity(uint world, float g) => WV3(world + 0x10, new Vector3(0, g, 0));

    /// <summary>Photo-mode camera (free flight through walls). Photo mode must be open (state 1 at +0x124).</summary>
    public uint PhotoCamera => U32(Player + 0x15B0) + 0x40;
    public bool PhotoModeOpen => U32(PhotoCamera + 0x124) == 1;
    public void SetPhotoCamera(Vector3 pos, float? pitch = null, float? yaw = null)
    {
        uint c = PhotoCamera;
        WV3(c + 0x60, pos); WV3(c + 0x130, pos);   // +0x130 = range-clamp origin
        if (pitch is float p && yaw is float y) { var b = new byte[8]; BE.WF32(b, 0, p); BE.WF32(b, 4, y); Write(c + 0xA0, b); }
    }
}
