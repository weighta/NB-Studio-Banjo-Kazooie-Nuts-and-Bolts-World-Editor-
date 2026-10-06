using System.Numerics;

namespace NB.Core.Havok;

/// <summary>
/// Builds Havok MOPP byte code (hkpMoppCode) for a triangle list, so a hkpMoppBvTreeShape can wrap a new
/// hkpExtendedMeshShape, in the same form as the game's shipped MOPPs: chunked (BUILT_WITH_CHUNK_SUBDIVISION, 512-byte
/// chunks joined by opcode 0x0C, code size a multiple of 512, unused bytes 0xCD).
///
/// Opcode semantics come from the game's own MOPP virtual machines in default.exe. The three that support chunked code
/// are the ones used at run time: hkpMoppObbVirtualMachine 0x82948928 (AABB/OBB queries; the query box is set up by
/// 0x82949600), hkpMoppLongRayVirtualMachine 0x82949790 (ray casts) and hkpMoppAabbCastVirtualMachine 0x8294EF68
/// (linear casts). Instruction lengths as in hkpMoppFindAllVirtualMachine 0x8297A910.
/// <list type="bullet">
/// <item>0x10/0x11/0x12 a b j — split on X/Y/Z: the fallthrough child holds coordinates ≤ a, the child at ip+4+j
/// coordinates ≥ b. OBB machine: fallthrough if (qmin » s) &lt; a, jump if (qmax » s) + 1 &gt; b, with qmin/qmax the
/// truncated 24-bit query ∓ 1. Ray machine: the segment is clipped to ≤ a / ≥ b.</item>
/// <item>0x26/0x27/0x28 lo hi — bound on X/Y/Z. 0x05 b / 0x06 u16 / 0x07 u24 — jump ip+2+b / ip+3+u16 / ip+4+u24.
/// 0x0C u16 — jump to chunk: ip = code + u16 · 512. 0x51 u16 / 0x52 u24 — terminal (shape key = triangle index in
/// subpart 0); terminals end the branch.</item>
/// </list>
/// Byte operands are the top 8 bits of 24-bit quantized coordinates q = (p − offset) · scale ((offset, scale) = the code
/// info). Every triangle's quantized bounds (with the shape radius) lie in [0x10000, 0xFE0000], so a = (max » 16) + 1 and
/// b = (min » 16) − 1 never clamp, and the strict comparisons of the OBB machine cannot miss a triangle.
/// The tree is a median split along the longest axis of the triangle centroids.
/// </summary>
public static class MoppBuilder
{
    public const int ChunkSize = 512;

    public sealed class Result
    {
        public byte[] Code = Array.Empty<byte>();
        public Vector4 CodeInfo;   // offset xyz, scale w
        public int Chunks;
    }

    sealed class Node
    {
        public int Tri = -1, Axis;
        public byte A, B;
        public Node? Left, Right;
        public int Size;   // inline size
        public int[] Min = new int[3], Max = new int[3];   // quantized bounds of the subtree's triangles
        /// <summary>Rescale opcode (1..4, 0 = none) emitted before this node's split, and its three offset bytes.</summary>
        public int Rescale; public byte R0, R1, R2;
    }

    /// <summary>Emit rescale opcodes (0x01..0x04): deeper nodes compare at finer resolution. Without them every split
    /// byte is the top 8 bits of the 24-bit coordinate, i.e. extent / 256 for the whole tree: on a 5000-unit imported
    /// map that is 20 units, so a 1.5-unit physics box reached 2,250 triangles (touching none) in snowy_dream's corridor
    /// and the game dropped to 15 fps there (see work/agent_src/vmf/MERGE.md).</summary>
    public static bool UseRescale = Environment.GetEnvironmentVariable("NB_MOPP_LEGACY") != "1";   // NB_MOPP_LEGACY=1: old builder (A/B tests)

    public static Result Build(IReadOnlyList<Vector3> positions, IReadOnlyList<int> triangles, float radius)
    {
        int nTri = triangles.Count / 3;
        if (nTri == 0) throw new ArgumentException("no triangles");
        if (nTri > 0xFFFFFF) throw new ArgumentException("too many triangles for a MOPP");
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var p in positions) { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
        float margin = radius + 0.01f * MathF.Max(1e-3f, (mx - mn).Length()) + 1e-3f;
        float extent = MathF.Max(MathF.Max(mx.X - mn.X, mx.Y - mn.Y), mx.Z - mn.Z) + 2 * margin;
        // quantized coordinates of everything (incl. margin) in [0x10000, 0xFE0000]
        float scale = 0xFC0000 / extent;
        var offset = mn - new Vector3(margin) - new Vector3(0x10000 / scale);

        var tmin = new int[nTri, 3]; var tmax = new int[nTri, 3]; var ctr = new Vector3[nTri];
        for (int t = 0; t < nTri; t++)
        {
            var a = positions[triangles[3 * t]]; var b = positions[triangles[3 * t + 1]]; var c = positions[triangles[3 * t + 2]];
            var lo = Vector3.Min(a, Vector3.Min(b, c)) - new Vector3(radius); var hi = Vector3.Max(a, Vector3.Max(b, c)) + new Vector3(radius);
            ctr[t] = (a + b + c) / 3;
            for (int k = 0; k < 3; k++)
            {
                tmin[t, k] = Math.Clamp((int)MathF.Floor((Get(lo, k) - Get(offset, k)) * scale), 0x10000, 0xFE0000);
                tmax[t, k] = Math.Clamp((int)MathF.Ceiling((Get(hi, k) - Get(offset, k)) * scale), 0x10000, 0xFE0000);
            }
        }
        static byte Lo(int q) => (byte)Math.Max(0, (q >> 16) - 1);      // lower byte bound (≥ b semantics)
        static byte Hi(int q) => (byte)Math.Min(255, (q >> 16) + 1);    // upper byte bound (≤ a semantics)

        // tree (bounds per node; split bytes and rescales are assigned afterwards, in the frame each node runs in)
        Node Make(List<int> tris)
        {
            if (tris.Count == 1)
            {
                var leaf = new Node { Tri = tris[0], Size = tris[0] <= 0xFFFF ? 3 : 4 };
                for (int k = 0; k < 3; k++) { leaf.Min[k] = tmin[tris[0], k]; leaf.Max[k] = tmax[tris[0], k]; }
                return leaf;
            }
            int axis, half;
            if (UseRescale && tris.Count > 2)
            {
                // surface-area heuristic over the centroid order of each axis (bounds of whole triangles), so big and
                // small triangles end in different subtrees instead of a centroid median mixing them
                axis = 0; half = tris.Count / 2; double best = double.MaxValue;
                var order = new List<int>[3];
                for (int ax = 0; ax < 3; ax++)
                {
                    int axc = ax;
                    var o = new List<int>(tris); o.Sort((x, y) => Get(ctr[x], axc).CompareTo(Get(ctr[y], axc)));
                    order[ax] = o;
                    int m = o.Count; var suf = new double[m + 1];
                    double[] lo = { double.MaxValue, double.MaxValue, double.MaxValue }, hi = { double.MinValue, double.MinValue, double.MinValue };
                    double Area(double[] l, double[] h) { double x = h[0] - l[0], y = h[1] - l[1], z = h[2] - l[2]; return x * y + y * z + z * x; }
                    for (int i = m - 1; i >= 1; i--)
                    {
                        int t = o[i]; for (int k = 0; k < 3; k++) { lo[k] = Math.Min(lo[k], tmin[t, k]); hi[k] = Math.Max(hi[k], tmax[t, k]); }
                        suf[i] = Area(lo, hi) * (m - i);
                    }
                    lo = new[] { double.MaxValue, double.MaxValue, double.MaxValue }; hi = new[] { double.MinValue, double.MinValue, double.MinValue };
                    for (int i = 1; i < m; i++)
                    {
                        int t = o[i - 1]; for (int k = 0; k < 3; k++) { lo[k] = Math.Min(lo[k], tmin[t, k]); hi[k] = Math.Max(hi[k], tmax[t, k]); }
                        double c = Area(lo, hi) * i + suf[i];
                        if (c < best) { best = c; axis = ax; half = i; }
                    }
                }
                tris.Clear(); tris.AddRange(order[axis]);
            }
            else
            {
                var cmin = new Vector3(float.MaxValue); var cmax = new Vector3(float.MinValue);
                foreach (var t in tris) { cmin = Vector3.Min(cmin, ctr[t]); cmax = Vector3.Max(cmax, ctr[t]); }
                var d = cmax - cmin;
                axis = d.X >= d.Y && d.X >= d.Z ? 0 : d.Y >= d.Z ? 1 : 2;
                tris.Sort((x, y) => Get(ctr[x], axis).CompareTo(Get(ctr[y], axis)));
                half = tris.Count / 2;
            }
            var left = tris.GetRange(0, half); var right = tris.GetRange(half, tris.Count - half);
            var n = new Node { Axis = axis, Left = Make(left), Right = Make(right) };
            for (int k = 0; k < 3; k++) { n.Min[k] = Math.Min(n.Left.Min[k], n.Right.Min[k]); n.Max[k] = Math.Max(n.Left.Max[k], n.Right.Max[k]); }
            return n;
        }
        var all = Enumerable.Range(0, nTri).ToList();
        var root = Make(all);
        Assign(root, new long[3], 16);

        // Frames: a node runs with (off, shift) - byte(q) = (q - off) >> shift, as the game's machines compute it
        // (MoppBuilder.Query models them; rescale n: off += byte << shift, shift -= n). A node whose subtree fits 256 cells
        // of a finer shift gets a rescale; its split bytes are then relative to the new frame.
        void Assign(Node n, long[] off, int shift)
        {
            if (n.Tri >= 0) return;
            if (UseRescale)
                for (int r = Math.Min(4, shift); r >= 1; r--)
                {
                    int s2 = shift - r;
                    var b = new long[3]; var off2 = new long[3]; bool ok = true;
                    for (int k = 0; k < 3 && ok; k++)
                    {
                        b[k] = (n.Min[k] - off[k]) >> shift;
                        if (b[k] < 0 || b[k] > 255) { ok = false; break; }
                        off2[k] = off[k] + (b[k] << shift);
                        ok = n.Min[k] - off2[k] >= 0 && ((n.Max[k] - off2[k]) >> s2) <= 253;
                    }
                    if (!ok) continue;
                    n.Rescale = r; n.R0 = (byte)b[0]; n.R1 = (byte)b[1]; n.R2 = (byte)b[2];
                    off = off2; shift = s2;
                    break;
                }
            int ax = n.Axis;
            long la = ((n.Left!.Max[ax] - off[ax]) >> shift) + 1, rb = ((n.Right!.Min[ax] - off[ax]) >> shift) - 1;
            n.A = (byte)Math.Clamp(la, 0, 255);   // fallthrough child: coordinates <= a
            n.B = (byte)Math.Clamp(rb, 0, 255);   // jump child: coordinates >= b
            Assign(n.Left, off, shift); Assign(n.Right, off, shift);
            n.Size = (n.Rescale > 0 ? 4 : 0) + 4 + JumpSize(n.Right.Size) + n.Right.Size + n.Left.Size;
        }

        // chunks
        var chunks = new List<List<byte>?>();
        var queue = new Queue<(int Index, Node Node)>();
        List<byte> Ref(Node n)
        {
            int idx = chunks.Count;
            if (idx > 0xFFFF) throw new InvalidOperationException("collision mesh too large (more than 65536 MOPP chunks)");
            chunks.Add(null); queue.Enqueue((idx, n));
            return new List<byte> { 0x0C, (byte)(idx >> 8), (byte)idx };
        }
        List<byte> Prefix(Node n) => n.Rescale > 0 ? new List<byte> { (byte)n.Rescale, n.R0, n.R1, n.R2 } : new List<byte>();
        List<byte> Inline(Node n)
        {
            if (n.Tri >= 0) return Terminal(n.Tri);
            var r = Inline(n.Right!); var l = Inline(n.Left!);
            var o = Prefix(n);
            int sp = o.Count;
            o.AddRange(new byte[] { (byte)(0x10 + n.Axis), n.A, n.B, 0 });
            var j = Jump(r.Count); o[sp + 3] = (byte)j.Count;
            o.AddRange(j); o.AddRange(r); o.AddRange(l);
            return o;
        }
        List<byte> Emit(Node n, int budget)
        {
            if (n.Size <= budget) return Inline(n);
            int pre = n.Rescale > 0 ? 4 : 0;
            if (n.Tri >= 0 || budget < pre + 4 + 3 + 3 + 3) return Ref(n);
            int rem = budget - pre - 4 - 3;
            var r = Emit(n.Right!, rem - 3);
            var l = Emit(n.Left!, rem - r.Count);
            var o = Prefix(n);
            int sp = o.Count;
            o.AddRange(new byte[] { (byte)(0x10 + n.Axis), n.A, n.B, 0 });
            var j = Jump(r.Count); o[sp + 3] = (byte)j.Count;
            o.AddRange(j); o.AddRange(r); o.AddRange(l);
            return o;
        }

        var head = new List<byte>();
        for (int k = 0; k < 3; k++)
        {
            int lo = all.Min(t => tmin[t, k]), hi = all.Max(t => tmax[t, k]);
            head.Add((byte)(0x26 + k)); head.Add(Lo(lo)); head.Add(Hi(hi));
        }
        chunks.Add(null);
        head.AddRange(Emit(root, ChunkSize - head.Count));
        chunks[0] = head;
        while (queue.Count > 0) { var (idx, n) = queue.Dequeue(); chunks[idx] = Emit(n, ChunkSize); }

        var code = new byte[chunks.Count * ChunkSize];
        Array.Fill(code, (byte)0xCD);
        for (int i = 0; i < chunks.Count; i++)
        {
            if (chunks[i]!.Count > ChunkSize) throw new InvalidOperationException("internal error: MOPP chunk overflow");
            chunks[i]!.CopyTo(code, i * ChunkSize);
        }
        return new Result { Code = code, CodeInfo = new Vector4(offset, scale), Chunks = chunks.Count };

        static List<byte> Terminal(int t) => t <= 0xFFFF
            ? new List<byte> { 0x51, (byte)(t >> 8), (byte)t }
            : new List<byte> { 0x52, (byte)(t >> 16), (byte)(t >> 8), (byte)t };
        static int JumpSize(int rel) => rel <= 0xFF ? 2 : rel <= 0xFFFF ? 3 : 4;
        static List<byte> Jump(int rel) => rel <= 0xFF ? new List<byte> { 0x05, (byte)rel }
            : rel <= 0xFFFF ? new List<byte> { 0x06, (byte)(rel >> 8), (byte)rel }
            : new List<byte> { 0x07, (byte)(rel >> 16), (byte)(rel >> 8), (byte)rel };
    }

    static float Get(Vector3 v, int k) => k == 0 ? v.X : k == 1 ? v.Y : v.Z;

    // ------------------------------------------------------------------ interpreters (verification)

    /// <summary>Instruction decoder shared by the interpreters: returns false when ip is not a known opcode.</summary>
    static void Check(byte[] code, int ip)
    {
        if (ip < 0 || ip >= code.Length) throw new InvalidDataException($"MOPP ip {ip} outside the code ({code.Length} bytes)");
    }

    /// <summary>
    /// Every-branch walk (like hkpMoppFindAllVirtualMachine, plus the chunk opcodes 0x0C/0x0D): the terminal keys.
    /// Also checks that no instruction is executed twice on one path (a loop would hang the game).
    /// </summary>
    public static List<int> FindAll(byte[] code)
    {
        var keys = new List<int>();
        Run(0, 0, 0, 0);
        return keys;

        void Run(int ip, int offset, int depth, int steps)
        {
            if (depth > 4096) throw new InvalidDataException("MOPP recursion too deep");
            while (true)
            {
                Check(code, ip);
                if (++steps > 1 << 20) throw new InvalidDataException("MOPP path does not terminate");
                int op = code[ip];
                int U8(int k) => code[ip + k];
                int U16(int k) => code[ip + k] << 8 | code[ip + k + 1];
                switch (op)
                {
                    case 0x00: return;
                    case >= 0x01 and <= 0x04: ip += 4; break;
                    case 0x05: ip += 2 + U8(1); break;
                    case 0x06: ip += 3 + U16(1); break;
                    case 0x07: ip += 4 + (U16(1) << 8 | U8(3)); break;
                    case 0x09: offset += U8(1); ip += 2; break;
                    case 0x0A: offset += U16(1); ip += 3; break;
                    case 0x0B: offset = U16(1) << 16 | U16(3); ip += 5; break;
                    case 0x0C: ip = U16(1) * ChunkSize; break;
                    case 0x0D: ip += 5; break;
                    case >= 0x10 and <= 0x1C: Run(ip + 4, offset, depth + 1, steps); ip += 4 + U8(3); break;
                    case >= 0x20 and <= 0x22: Run(ip + 3, offset, depth + 1, steps); ip += 3 + U8(2); break;
                    case >= 0x23 and <= 0x25: Run(ip + 7 + U16(3), offset, depth + 1, steps); ip += 7 + U16(5); break;
                    case >= 0x26 and <= 0x28: ip += 3; break;
                    case >= 0x29 and <= 0x2B: ip += 7; break;
                    case >= 0x30 and <= 0x4F: keys.Add(offset + op - 0x30); return;
                    case 0x50: keys.Add(offset + U8(1)); return;
                    case 0x51: keys.Add(offset + U16(1)); return;
                    case 0x52: keys.Add(offset + (U16(1) << 8 | U8(3))); return;
                    case 0x53: keys.Add(offset + (U16(1) << 16 | U16(3))); return;
                    case >= 0x60 and <= 0x63: ip += 2; break;
                    case >= 0x64 and <= 0x67: ip += 3; break;
                    case >= 0x68 and <= 0x6B: ip += 5; break;
                    default: throw new InvalidDataException($"unknown MOPP opcode 0x{op:X2} at {ip} (the game's machines loop forever on it)");
                }
            }
        }
    }

    /// <summary>
    /// Query kinds modelled after the game's chunk-capable machines. Aabb: hkpMoppObbVirtualMachine with the box set up
    /// like 0x82949600 (24-bit bounds truncated ∓ 1, max byte + 1, strict byte compares). Ray: hkpMoppLongRayVirtualMachine
    /// (segment clipped at every split/bound). LinearCast: hkpMoppAabbCastVirtualMachine, modelled as the ray of the box
    /// centre against planes widened by the box half extents.
    /// </summary>
    public enum QueryKind { Aabb, Ray, LinearCast }

    /// <summary>
    /// Runs a query over MOPP code: for Aabb (from, to) are the box min/max; for Ray/LinearCast the segment end points,
    /// with <paramref name="halfExtents"/> the cast box. Positions in shape space. Returns the reached terminal keys.
    /// Opcodes the model does not prune on (diagonal splits 0x13–0x1C, 0x20–0x25) visit all children (conservative).
    /// </summary>
    public static HashSet<int> Query(byte[] code, Vector4 codeInfo, QueryKind kind, Vector3 from, Vector3 to, Vector3 halfExtents = default)
    {
        var keys = new HashSet<int>();
        var off0 = new Vector3(codeInfo.X, codeInfo.Y, codeInfo.Z); float s0 = codeInfo.W;
        if (kind == QueryKind.Aabb)
        {
            long T(float v, int k) => (long)MathF.Truncate((v - Get(off0, k)) * s0);
            var qmin = new long[3]; var qmax = new long[3];
            for (int k = 0; k < 3; k++) { qmin[k] = T(Get(from, k), k) - 1; qmax[k] = T(Get(to, k), k) + 1; }
            RunAabb(0, 0, new long[3], 16, 0);

            void RunAabb(int ip, int offset, long[] off, int shift, int depth)
            {
                off = (long[])off.Clone();
                while (true)
                {
                    Check(code, ip);
                    int op = code[ip];
                    int U8(int k) => code[ip + k];
                    int U16(int k) => code[ip + k] << 8 | code[ip + k + 1];
                    long MinB(int k) => (qmin[k] - off[k]) >> shift;
                    long MaxB(int k) => ((qmax[k] - off[k]) >> shift) + 1;
                    switch (op)
                    {
                        case >= 0x10 and <= 0x12:
                        {
                            int k = op - 0x10;
                            if (MinB(k) < U8(1)) RunAabb(ip + 4, offset, off, shift, depth + 1);
                            if (MaxB(k) <= U8(2)) return;
                            ip += 4 + U8(3); break;
                        }
                        case >= 0x26 and <= 0x28:
                        {
                            int k = op - 0x26;
                            if (MaxB(k) < U8(1) || MinB(k) >= U8(2)) return;
                            ip += 3; break;
                        }
                        case >= 0x01 and <= 0x04:
                            for (int k = 0; k < 3; k++) off[k] += (long)U8(1 + k) << shift;
                            shift -= op; ip += 4; break;
                        default:
                            if (!Common(ref ip, ref offset, op, U8, U16, next => RunAabb(next, offset, off, shift, depth + 1))) return;
                            break;
                    }
                }
            }
        }
        else
        {
            // segment in 24-bit units (floats), clipped as it descends; he = cast half extents in the same units
            var p0 = (from - off0) * s0; var p1 = (to - off0) * s0;
            var he = kind == QueryKind.LinearCast ? halfExtents * s0 : Vector3.Zero;
            RunRay(0, 0, new float[3], 16, 0f, 1f, 0);

            void RunRay(int ip, int offset, float[] off, int shift, float t0, float t1, int depth)
            {
                off = (float[])off.Clone();
                while (true)
                {
                    Check(code, ip);
                    int op = code[ip];
                    int U8(int k) => code[ip + k];
                    int U16(int k) => code[ip + k] << 8 | code[ip + k + 1];
                    float At(int b, int k) => b * MathF.Pow(2, shift) + off[k];
                    float V(float t, int k) => Get(p0, k) + (Get(p1, k) - Get(p0, k)) * t;
                    // sub-interval of [t0,t1] where coordinate k is ≤ limit (le) or ≥ limit (!le); false if empty
                    bool Clip(int k, float limit, bool le, ref float a, ref float b)
                    {
                        float va = V(a, k), vb = V(b, k), d = vb - va;
                        bool ina = le ? va <= limit : va >= limit, inb = le ? vb <= limit : vb >= limit;
                        if (ina && inb) return true;
                        if (!ina && !inb) return false;
                        float tc = a + (b - a) * ((limit - va) / d);
                        if (ina) b = MathF.Min(b, tc + 1e-6f); else a = MathF.Max(a, tc - 1e-6f);
                        return true;
                    }
                    switch (op)
                    {
                        case >= 0x10 and <= 0x12:
                        {
                            int k = op - 0x10;
                            float la = t0, lb = t1, ra = t0, rb = t1;
                            bool left = Clip(k, At(U8(1), k) + Get(he, k), true, ref la, ref lb);
                            bool right = Clip(k, At(U8(2), k) - Get(he, k), false, ref ra, ref rb);
                            if (left) RunRay(ip + 4, offset, off, shift, la, lb, depth + 1);
                            if (!right) return;
                            t0 = ra; t1 = rb; ip += 4 + U8(3); break;
                        }
                        case >= 0x26 and <= 0x28:
                        {
                            int k = op - 0x26;
                            if (!Clip(k, At(U8(1), k) - Get(he, k), false, ref t0, ref t1)) return;
                            if (!Clip(k, At(U8(2), k) + Get(he, k), true, ref t0, ref t1)) return;
                            ip += 3; break;
                        }
                        case >= 0x01 and <= 0x04:
                            for (int k = 0; k < 3; k++) off[k] += U8(1 + k) * MathF.Pow(2, shift);
                            shift -= op; ip += 4; break;
                        default:
                            if (!Common(ref ip, ref offset, op, U8, U16, next => RunRay(next, offset, off, shift, t0, t1, depth + 1))) return;
                            break;
                    }
                }
            }
        }
        return keys;

        // non-geometric opcodes; returns false when the branch ends
        bool Common(ref int ip, ref int offset, int op, Func<int, int> U8, Func<int, int> U16, Action<int> recurse)
        {
            switch (op)
            {
                case 0x00: return false;
                case 0x05: ip += 2 + U8(1); return true;
                case 0x06: ip += 3 + U16(1); return true;
                case 0x07: ip += 4 + (U16(1) << 8 | U8(3)); return true;
                case 0x09: offset += U8(1); ip += 2; return true;
                case 0x0A: offset += U16(1); ip += 3; return true;
                case 0x0B: offset = U16(1) << 16 | U16(3); ip += 5; return true;
                case 0x0C: ip = U16(1) * ChunkSize; return true;
                case 0x0D: ip += 5; return true;
                case >= 0x13 and <= 0x1C: recurse(ip + 4); ip += 4 + U8(3); return true;
                case >= 0x20 and <= 0x22: recurse(ip + 3); ip += 3 + U8(2); return true;
                case >= 0x23 and <= 0x25: recurse(ip + 7 + U16(3)); ip += 7 + U16(5); return true;
                case >= 0x29 and <= 0x2B: ip += 7; return true;
                case >= 0x30 and <= 0x4F: keys.Add(offset + op - 0x30); return false;
                case 0x50: keys.Add(offset + U8(1)); return false;
                case 0x51: keys.Add(offset + U16(1)); return false;
                case 0x52: keys.Add(offset + (U16(1) << 8 | U8(3))); return false;
                case 0x53: keys.Add(offset + (U16(1) << 16 | U16(3))); return false;
                case >= 0x60 and <= 0x63: ip += 2; return true;
                case >= 0x64 and <= 0x67: ip += 3; return true;
                case >= 0x68 and <= 0x6B: ip += 5; return true;
                default: throw new InvalidDataException($"unknown MOPP opcode 0x{op:X2} at {ip}");
            }
        }
    }

    /// <summary>
    /// Random AABB / ray / linear-cast queries around the given triangles: counts triangles a query must reach (box
    /// overlaps the triangle's bounds; segment hits the triangle; swept box hits the bounds) but the code misses.
    /// </summary>
    public static (int Queries, int Misses, long Reached) SelfTest(byte[] code, Vector4 codeInfo, IReadOnlyList<Vector3> P, IReadOnlyList<int> T, int seed, int perKind = 30)
    {
        int nt = T.Count / 3; if (nt == 0) return (0, 0, 0);
        var rng = new Random(seed);
        var tmn = new Vector3[nt]; var tmx = new Vector3[nt];
        for (int t = 0; t < nt; t++)
        {
            tmn[t] = Vector3.Min(P[T[3 * t]], Vector3.Min(P[T[3 * t + 1]], P[T[3 * t + 2]]));
            tmx[t] = Vector3.Max(P[T[3 * t]], Vector3.Max(P[T[3 * t + 1]], P[T[3 * t + 2]]));
        }
        var ext = tmx.Aggregate(Vector3.Max) - tmn.Aggregate(Vector3.Min);
        float R() => (float)rng.NextDouble();
        Vector3 Pt(int t) { float u = R(), v = R(); if (u + v > 1) { u = 1 - u; v = 1 - v; } return P[T[3 * t]] + u * (P[T[3 * t + 1]] - P[T[3 * t]]) + v * (P[T[3 * t + 2]] - P[T[3 * t]]); }
        Vector3 Dir() => Vector3.Normalize(new Vector3(R() - 0.5f, R() - 0.5f, R() - 0.5f) + new Vector3(1e-4f));
        int q = 0, miss = 0; long reached = 0;
        foreach (var kind in new[] { QueryKind.Aabb, QueryKind.Ray, QueryKind.LinearCast })
            for (int i = 0; i < perKind; i++)
            {
                var target = Pt(rng.Next(nt));
                var got = new HashSet<int>();
                if (kind == QueryKind.Aabb)
                {
                    var h = ext * (0.002f + 0.2f * R()) + new Vector3(0.01f);
                    var a = target - h; var b = target + h;
                    got = Query(code, codeInfo, kind, a, b);
                    for (int t = 0; t < nt; t++)
                        if (tmn[t].X <= b.X && tmx[t].X >= a.X && tmn[t].Y <= b.Y && tmx[t].Y >= a.Y && tmn[t].Z <= b.Z && tmx[t].Z >= a.Z && !got.Contains(t)) miss++;
                }
                else
                {
                    float len = ext.Length() * (0.05f + R());
                    var d = Dir(); var s = target - d * len * R(); var e = s + d * len;
                    var he = kind == QueryKind.LinearCast ? ext * 0.05f * R() + new Vector3(0.01f) : Vector3.Zero;
                    got = Query(code, codeInfo, kind, s, e, he);
                    for (int t = 0; t < nt; t++)
                    {
                        bool hit = kind == QueryKind.Ray ? SegTri(s, e, P[T[3 * t]], P[T[3 * t + 1]], P[T[3 * t + 2]]) : SegBox(s, e, tmn[t] - he, tmx[t] + he);
                        if (hit && !got.Contains(t)) miss++;
                    }
                }
                reached += got.Count; q++;
            }
        return (q, miss, reached);
    }

    static bool SegTri(Vector3 s, Vector3 e, Vector3 a, Vector3 b, Vector3 c)
    {
        var d = e - s; var e1 = b - a; var e2 = c - a; var p = Vector3.Cross(d, e2); float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-12f) return false;
        float inv = 1 / det; var tv = s - a; float u = Vector3.Dot(tv, p) * inv; if (u < 0 || u > 1) return false;
        var qv = Vector3.Cross(tv, e1); float v = Vector3.Dot(d, qv) * inv; if (v < 0 || u + v > 1) return false;
        float t = Vector3.Dot(e2, qv) * inv; return t >= 0 && t <= 1;
    }

    static bool SegBox(Vector3 s, Vector3 e, Vector3 mn, Vector3 mx)
    {
        float t0 = 0, t1 = 1; var d = e - s;
        for (int k = 0; k < 3; k++)
        {
            float o = Get(s, k), dd = Get(d, k), lo = Get(mn, k), hi = Get(mx, k);
            if (MathF.Abs(dd) < 1e-12f) { if (o < lo || o > hi) return false; continue; }
            float a = (lo - o) / dd, b = (hi - o) / dd; if (a > b) (a, b) = (b, a);
            t0 = MathF.Max(t0, a); t1 = MathF.Min(t1, b); if (t0 > t1) return false;
        }
        return true;
    }
}
