using System.Numerics;
using System.Text;
using NB.Core.IO;

namespace NB.Core.Vehicles;

/// <summary>
/// A Nuts &amp; Bolts vehicle blueprint: the data of an <c>aid_vehicle_*</c> asset and of a saved vehicle (the content file of
/// a "VEHICLE: name" package after its 8-byte prefix). 0x7C-byte header, then one 0x24-byte record per part (block).
/// <para>Header (written by the game's serializer 0x8260FF20): +0 u16 part count; +2 u8 1 = the vehicle is one piece;
/// +3 u8 (not written by the game); +4 f32 power (max of ground / air power), +8 f32, +0xC f32, +0x10 f32 weight
/// (sum of the parts' objparams +0x190, verified on all samples), +0x14 f32 (stat bars of the blueprint lists, copied by
/// 0x82568170); +0x18 u32 ability mask (OR of the parts' runtime masks); +0x1C u32 1 = any part has a runtime flag;
/// +0x20 name (saves: UTF-16BE, at most 31 characters; the game's own assets: an ASCII creator tag); +0x60/+0x64/+0x68
/// u32 the part type (objparams id) shown for action buttons 1-3, +0x6C/+0x70/+0x74 u32 per button; +0x78 u8 1 = the
/// player named the vehicle (0: the game wrote "-- gamertag --").</para>
/// <para>Part record: +0 u8 x, y, z garage cell; +3 u8 group (the spawner skips parts with a group unless the
/// spawn asks for them); +4 u8 1 = painted (the paint at +0x18 is used; 0: the part's default colour from objparams
/// +0x130); +5 u8 setting, the index into the garage's list (wheels: 0 automatic, 1 driven, 2 steering, 3 driven &amp;
/// steering, 4 freewheeling — 1 and 2 verified by driving; propellers: 0 automatic, 1 push, 2 pull); +6 u8 category = objparams +0x98 + 1; +7 u8 0; +8 u32 objparams id of the
/// part; +0xC/+0x10/+0x14 f32 rotation X, Y, Z in radians (multiples of π/2, the game's 24 orientations; the rotation
/// is Rz, then Rx, then Ry: System.Numerics <c>CreateFromYawPitchRoll(Y, X, Z)</c>); +0x18 u32 paint RGBA; +0x1C u32
/// action buttons (bit 12 = A, 13 = B, 14 = X: the trolley's spring 0x1000 / horn 0x2000 / laser 0x4000 show on the
/// HUD as A / B / X); +0x20 u32 action buttons of the part's second action.</para>
/// Unknown or unused bytes are kept, so an unchanged blueprint writes back byte for byte (bytes after the last part are
/// kept as <see cref="Trailing"/>).
/// </summary>
public sealed class Blueprint
{
    public const int HeaderSize = 0x7C, BlockSize = 0x24, NameOffset = 0x20, NameBytes = 0x40, MaxNameChars = 31;

    /// <summary>The raw header (count at +0 is rewritten from <see cref="Blocks"/> on <see cref="Write"/>).</summary>
    public byte[] Header = new byte[HeaderSize];
    public List<BlueprintBlock> Blocks = new();
    public byte[] Trailing = Array.Empty<byte>();
    /// <summary>The count field when read (it can differ from <see cref="Blocks"/>.Count for damaged files).</summary>
    public int DeclaredCount;
    int _readCount = -1;

    /// <summary>Forgets the declared count of a damaged file (the next write stores the real part count).</summary>
    public void Normalize() { _readCount = -1; DeclaredCount = Blocks.Count; }

    public static Blueprint New(string name)
    {
        var b = new Blueprint();
        b.Header[2] = 1;
        BE.W32(b.Header, 0x1C, 1u);
        b.Name = name;
        b.Header[0x78] = 1;
        return b;
    }

    /// <summary>Is <paramref name="d"/> (from <paramref name="offset"/>) a blueprint: a header and the declared number of part
    /// records, every part id an objparams id (type byte 0x1F)?</summary>
    public static bool LooksLike(ReadOnlySpan<byte> d, int offset = 0)
    {
        if (d.Length - offset < HeaderSize) return false;
        int n = BE.U16(d, offset);
        if (offset + HeaderSize + (long)n * BlockSize > d.Length) return false;
        for (int i = 0; i < Math.Min(n, 64); i++)
            if (d[offset + HeaderSize + i * BlockSize + 8] != 0x1F) return false;
        return true;
    }

    public static Blueprint Parse(ReadOnlySpan<byte> d, int offset = 0, int length = -1)
    {
        if (length < 0) length = d.Length - offset;
        if (length < HeaderSize) throw new InvalidDataException($"a blueprint needs at least {HeaderSize} bytes, this has {length}");
        var bp = new Blueprint { Header = d.Slice(offset, HeaderSize).ToArray() };
        int n = bp.DeclaredCount = BE.U16(d, offset);
        int avail = (length - HeaderSize) / BlockSize;
        if (n > avail) n = avail;                       // damaged / truncated file: read what is there
        for (int i = 0; i < n; i++) bp.Blocks.Add(BlueprintBlock.Read(d.Slice(offset + HeaderSize + i * BlockSize, BlockSize)));
        bp._readCount = n;
        int used = HeaderSize + n * BlockSize;
        bp.Trailing = d.Slice(offset + used, length - used).ToArray();
        return bp;
    }

    public byte[] Write(bool keepTrailing = true)
    {
        var o = new byte[HeaderSize + Blocks.Count * BlockSize + (keepTrailing ? Trailing.Length : 0)];
        Header.CopyTo(o, 0);
        // a truncated file keeps its declared count while its parts are untouched (byte-identical round trip)
        int count = Blocks.Count == _readCount && DeclaredCount > Blocks.Count ? DeclaredCount : Blocks.Count;
        BE.W16(o, 0, (ushort)Math.Min(count, ushort.MaxValue));
        for (int i = 0; i < Blocks.Count; i++) Blocks[i].Write(o.AsSpan(HeaderSize + i * BlockSize, BlockSize));
        if (keepTrailing) Trailing.CopyTo(o, HeaderSize + Blocks.Count * BlockSize);
        return o;
    }

    public Blueprint Clone() => new()
    {
        Header = (byte[])Header.Clone(), Blocks = Blocks.Select(b => b.Clone()).ToList(), Trailing = (byte[])Trailing.Clone(), DeclaredCount = DeclaredCount,
        _readCount = _readCount,
    };

    // ------------------------------------------------------------------ header fields

    public bool OnePiece { get => Header[2] != 0; set => Header[2] = (byte)(value ? 1 : 0); }
    public float Power { get => BE.F32(Header, 4); set => BE.WF32(Header, 4, value); }
    public float Stat8 { get => BE.F32(Header, 8); set => BE.WF32(Header, 8, value); }
    public float StatC { get => BE.F32(Header, 0xC); set => BE.WF32(Header, 0xC, value); }
    public float Weight { get => BE.F32(Header, 0x10); set => BE.WF32(Header, 0x10, value); }
    public float Stat14 { get => BE.F32(Header, 0x14); set => BE.WF32(Header, 0x14, value); }
    public uint AbilityMask { get => BE.U32(Header, 0x18); set => BE.W32(Header, 0x18, value); }
    public uint Flag1C { get => BE.U32(Header, 0x1C); set => BE.W32(Header, 0x1C, value); }
    public bool PlayerNamed { get => Header[0x78] != 0; set => Header[0x78] = (byte)(value ? 1 : 0); }
    /// <summary>The part type shown for action button <paramref name="i"/> (0..2) and its per-button word.</summary>
    public uint ButtonPart(int i) => BE.U32(Header, 0x60 + 4 * i);
    public void SetButtonPart(int i, uint part) => BE.W32(Header, 0x60 + 4 * i, part);
    public uint ButtonWord(int i) => BE.U32(Header, 0x6C + 4 * i);

    /// <summary>True when the name field holds 8-bit text (the game's own assets carry an ASCII creator tag such as
    /// "SalvyBob"); saved vehicles hold UTF-16BE.</summary>
    public bool NameIsAscii => Header[NameOffset] != 0 && Header[NameOffset + 1] != 0;

    /// <summary>
    /// The text of a blueprint name field (0x40 bytes): 8-bit text when its first two bytes are both set (the game's own
    /// vehicles carry an ASCII creator tag such as "SalvyBob", and the game shows it as such in Your Blueprints), else
    /// UTF-16BE (vehicles saved in the game). Ends at the first NUL or control character (some saves have junk after the
    /// name). Every reader of vehicle names uses this: read as UTF-16BE, "SalvyBob" became "卡汶祂潢".
    /// </summary>
    public static string DecodeName(ReadOnlySpan<byte> f)
    {
        var sb = new StringBuilder();
        if (f.Length >= 2 && f[0] != 0 && f[1] != 0)
        {
            foreach (byte b in f) { if (b < 0x20) break; sb.Append((char)b); }
            return sb.ToString().TrimEnd();
        }
        for (int o = 0; o + 1 < f.Length; o += 2)
        {
            char c = (char)(f[o] << 8 | f[o + 1]);
            if (c < 0x20 || c >= 0xD800) break;
            sb.Append(c);
        }
        return sb.ToString().TrimEnd();
    }

    public string Name
    {
        get => DecodeName(Header.AsSpan(NameOffset, NameBytes));
        set
        {
            Array.Clear(Header, NameOffset, NameBytes);
            var s = (value ?? "").Length > MaxNameChars ? value![..MaxNameChars] : value ?? "";
            var b = Encoding.BigEndianUnicode.GetBytes(s);
            b.CopyTo(Header, NameOffset);
            PlayerNamed = s.Length > 0;
        }
    }

    /// <summary>The cells the parts' origins span (the part footprints are not included).</summary>
    public (Vector3 Min, Vector3 Max) CellBounds()
    {
        if (Blocks.Count == 0) return (Vector3.Zero, Vector3.Zero);
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var b in Blocks) { var p = new Vector3(b.X, b.Y, b.Z); mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
        return (mn, mx);
    }

    /// <summary>
    /// Updates the header from the parts like the game's serializer: count, weight (sum of objparams +0x190, from
    /// <paramref name="weightOf"/>), the part types of the action buttons (the first part on each button, when the
    /// buttons' parts changed). Power and the other stat-bar values stay as they were (the game recomputes them from
    /// the built vehicle the next time it saves).
    /// </summary>
    public void UpdateHeader(Func<uint, float?> weightOf)
    {
        float w = 0; bool all = true;
        foreach (var b in Blocks) { if (weightOf(b.Part) is float x) w += x; else all = false; }
        if (all || Blocks.Count == 0) Weight = w;
        for (int i = 0; i < 3; i++)
        {
            uint bit = 0x1000u << i;
            var on = Blocks.Where(b => ((b.Action1 | b.Action2) & bit) != 0).Select(b => b.Part).ToList();
            uint cur = ButtonPart(i);
            if (on.Count == 0) { if (cur != 0) SetButtonPart(i, 0); }
            else if (!on.Contains(cur)) SetButtonPart(i, on[0]);
        }
    }
}

/// <summary>One part of a <see cref="Blueprint"/> (0x24 bytes, layout in the <see cref="Blueprint"/> summary).</summary>
public sealed class BlueprintBlock
{
    public byte X, Y, Z, Group, Painted, Setting, Category, Pad7;
    public uint Part;
    /// <summary>Raw IEEE bits of the rotation angles (kept exactly; use <see cref="Rotation"/> / <see cref="Orientation"/>).</summary>
    public uint RotXBits, RotYBits, RotZBits;
    public uint Paint, Action1, Action2;

    public static BlueprintBlock Read(ReadOnlySpan<byte> d) => new()
    {
        X = d[0], Y = d[1], Z = d[2], Group = d[3], Painted = d[4], Setting = d[5], Category = d[6], Pad7 = d[7],
        Part = BE.U32(d, 8), RotXBits = BE.U32(d, 0xC), RotYBits = BE.U32(d, 0x10), RotZBits = BE.U32(d, 0x14),
        Paint = BE.U32(d, 0x18), Action1 = BE.U32(d, 0x1C), Action2 = BE.U32(d, 0x20),
    };

    public void Write(Span<byte> d)
    {
        d[0] = X; d[1] = Y; d[2] = Z; d[3] = Group; d[4] = Painted; d[5] = Setting; d[6] = Category; d[7] = Pad7;
        BE.W32(d, 8, Part); BE.W32(d, 0xC, RotXBits); BE.W32(d, 0x10, RotYBits); BE.W32(d, 0x14, RotZBits);
        BE.W32(d, 0x18, Paint); BE.W32(d, 0x1C, Action1); BE.W32(d, 0x20, Action2);
    }

    public BlueprintBlock Clone() => (BlueprintBlock)MemberwiseClone();

    public Vector3 Rotation
    {
        get => new(BitConverter.UInt32BitsToSingle(RotXBits), BitConverter.UInt32BitsToSingle(RotYBits), BitConverter.UInt32BitsToSingle(RotZBits));
        set { RotXBits = BitConverter.SingleToUInt32Bits(value.X); RotYBits = BitConverter.SingleToUInt32Bits(value.Y); RotZBits = BitConverter.SingleToUInt32Bits(value.Z); }
    }

    /// <summary>The part's rotation as a matrix (System.Numerics row-vector convention).</summary>
    public Matrix4x4 RotationMatrix { get { var r = Rotation; return Matrix4x4.CreateFromYawPitchRoll(r.Y, r.X, r.Z); } }

    /// <summary>The nearest of the 24 axis-aligned orientations (index into <see cref="Orientations.All"/>).</summary>
    public int Orientation
    {
        get => Orientations.Nearest(RotationMatrix);
        set => Rotation = Orientations.Euler(value);
    }

    /// <summary>RGBA paint as a colour (alpha ignored by the game's shaders).</summary>
    public System.Drawing.Color PaintColor
    {
        get => System.Drawing.Color.FromArgb(255, (int)(Paint >> 24), (int)(Paint >> 16 & 0xFF), (int)(Paint >> 8 & 0xFF));
        set => Paint = (uint)value.R << 24 | (uint)value.G << 16 | (uint)value.B << 8 | 0xFF;
    }
}

/// <summary>
/// The 24 orientations of a cube (the game keeps one index per part, 0x82F0FCD0 holds their quaternions) and the
/// Euler angles the game writes for them (its serializer decomposes the rotation as R = Ry·Rx·Rz with atan2).
/// </summary>
public static class Orientations
{
    /// <summary>Integer rotation matrices (System.Numerics row-vector convention: v' = v·M).</summary>
    public static readonly Matrix4x4[] All = Build();

    static Matrix4x4[] Build()
    {
        var list = new List<Matrix4x4>();
        // every signed permutation matrix with determinant +1, ordered: identity first, then by Euler angles
        var axes = new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitX, -Vector3.UnitY, -Vector3.UnitZ };
        foreach (var a in axes)
            foreach (var b in axes)
            {
                if (MathF.Abs(Vector3.Dot(a, b)) > 0.5f) continue;
                var c = Vector3.Cross(a, b);
                var m = new Matrix4x4(a.X, a.Y, a.Z, 0, b.X, b.Y, b.Z, 0, c.X, c.Y, c.Z, 0, 0, 0, 0, 1);
                list.Add(m);
            }
        var id = list.FindIndex(m => m.IsIdentity);
        (list[0], list[id]) = (list[id], list[0]);
        return list.ToArray();
    }

    static float Snap(float v) => MathF.Abs(v) < 0.5f ? 0f : MathF.Sign(v);

    /// <summary>Index of the orientation nearest to <paramref name="m"/> (its 3x3 part).</summary>
    public static int Nearest(Matrix4x4 m)
    {
        int best = 0; float bd = float.MaxValue;
        for (int i = 0; i < All.Length; i++)
        {
            var o = All[i];
            float d = Sq(o.M11 - m.M11) + Sq(o.M12 - m.M12) + Sq(o.M13 - m.M13) + Sq(o.M21 - m.M21) + Sq(o.M22 - m.M22) + Sq(o.M23 - m.M23)
                    + Sq(o.M31 - m.M31) + Sq(o.M32 - m.M32) + Sq(o.M33 - m.M33);
            if (d < bd) { bd = d; best = i; }
        }
        return best;
        static float Sq(float v) => v * v;
    }

    /// <summary>The Euler angles (X, Y, Z radians) the game's serializer writes for orientation <paramref name="index"/>:
    /// the exact float bits it produces (one pattern per orientation in all 3 182 part records of the 53 readable sample
    /// saves, e.g. 90° about X is 0x3FC90FD6 but about Y 0x3FC90FE0: float rounding of its quaternion math).</summary>
    public static Vector3 Euler(int index)
    {
        var m = All[index];
        foreach (var (k, x, y, z) in GameBits)
            if (k[0] == m.M11 && k[1] == m.M12 && k[2] == m.M13 && k[3] == m.M21 && k[4] == m.M22 && k[5] == m.M23 && k[6] == m.M31 && k[7] == m.M32 && k[8] == m.M33)
                return new(BitConverter.UInt32BitsToSingle(x), BitConverter.UInt32BitsToSingle(y), BitConverter.UInt32BitsToSingle(z));
        return EulerOf(m);
    }

    /// <summary>(System.Numerics matrix M11..M33, Euler X, Y, Z bits as the game writes them).</summary>
    static readonly (sbyte[] M, uint X, uint Y, uint Z)[] GameBits =
    {
        (new sbyte[] {1, 0, 0, 0, 1, 0, 0, 0, 1}, 0x00000000u, 0x00000000u, 0x00000000u),
        (new sbyte[] {-1, 0, 0, 0, 0, 1, 0, 1, 0}, 0xBFC90FD6u, 0x00000000u, 0x40490FDBu),
        (new sbyte[] {1, 0, 0, 0, 0, -1, 0, 1, 0}, 0xBFC90FD6u, 0x00000000u, 0x00000000u),
        (new sbyte[] {1, 0, 0, 0, -1, 0, 0, 0, -1}, 0x00000000u, 0x40490FDBu, 0x40490FDBu),
        (new sbyte[] {0, 1, 0, -1, 0, 0, 0, 0, 1}, 0x00000000u, 0x00000000u, 0x3FC90FE0u),
        (new sbyte[] {-1, 0, 0, 0, -1, 0, 0, 0, 1}, 0x00000000u, 0x00000000u, 0x40490FDBu),
        (new sbyte[] {-1, 0, 0, 0, 1, 0, 0, 0, -1}, 0x00000000u, 0x40490FDBu, 0x00000000u),
        (new sbyte[] {1, 0, 0, 0, 0, 1, 0, -1, 0}, 0x3FC90FD6u, 0x00000000u, 0x00000000u),
        (new sbyte[] {0, 0, 1, 0, 1, 0, -1, 0, 0}, 0x00000000u, 0xBFC90FE0u, 0x00000000u),
        (new sbyte[] {0, 0, -1, 0, 1, 0, 1, 0, 0}, 0x00000000u, 0x3FC90FE0u, 0x00000000u),
        (new sbyte[] {0, 1, 0, 0, 0, 1, 1, 0, 0}, 0x00000000u, 0x3FC90FDBu, 0x3FC90FDBu),
        (new sbyte[] {0, -1, 0, 0, 0, 1, -1, 0, 0}, 0x00000000u, 0xBFC90FDBu, 0xBFC90FDBu),
        (new sbyte[] {0, 0, 1, -1, 0, 0, 0, -1, 0}, 0x3FC90FDBu, 0x00000000u, 0x3FC90FDBu),
        (new sbyte[] {0, 1, 0, 0, 0, -1, -1, 0, 0}, 0x00000000u, 0xBFC90FDBu, 0x3FC90FDBu),
        (new sbyte[] {0, 0, 1, 1, 0, 0, 0, 1, 0}, 0xBFC90FDBu, 0x00000000u, 0xBFC90FDBu),
        (new sbyte[] {-1, 0, 0, 0, 0, -1, 0, -1, 0}, 0x3FC90FD6u, 0x00000000u, 0x40490FDBu),
        (new sbyte[] {0, -1, 0, 1, 0, 0, 0, 0, 1}, 0x00000000u, 0x00000000u, 0xBFC90FE0u),
        (new sbyte[] {0, -1, 0, -1, 0, 0, 0, 0, -1}, 0x00000000u, 0x40490FDBu, 0xBFC90FE0u),
        (new sbyte[] {0, 0, 1, 0, -1, 0, 1, 0, 0}, 0x00000000u, 0x3FC90FE0u, 0x40490FDBu),
        (new sbyte[] {0, 0, -1, 1, 0, 0, 0, -1, 0}, 0x3FC90FDBu, 0x00000000u, 0xBFC90FDBu),
        (new sbyte[] {0, 1, 0, 1, 0, 0, 0, 0, -1}, 0x00000000u, 0x40490FDBu, 0x3FC90FE0u),
        (new sbyte[] {0, -1, 0, 0, 0, -1, 1, 0, 0}, 0x00000000u, 0x3FC90FDBu, 0xBFC90FDBu),
        (new sbyte[] {0, 0, -1, -1, 0, 0, 0, 1, 0}, 0xBFC90FDBu, 0x00000000u, 0x3FC90FDBu),
        (new sbyte[] {0, 0, -1, 0, -1, 0, -1, 0, 0}, 0x00000000u, 0xBFC90FE0u, 0x40490FDBu),
    };

    /// <summary>Decomposes a rotation like the game (0x8260FF20): with R the column-vector matrix (= transpose of the
    /// System.Numerics one): X = atan2(-R12, sqrt(R02² + R22²)), Y = atan2(R02, R22), Z = atan2(R10, R11); at X = ±90°:
    /// Y = 0, Z = atan2(-R01, R00).</summary>
    public static Vector3 EulerOf(Matrix4x4 m)
    {
        // column-vector R[i][j] = numerics M[j][i]
        float r00 = Snap(m.M11), r01 = Snap(m.M21), r02 = Snap(m.M31);
        float r10 = Snap(m.M12), r11 = Snap(m.M22), r12 = Snap(m.M32);
        float r22 = Snap(m.M33);
        float cx = MathF.Sqrt(r02 * r02 + r22 * r22);
        float x = MathF.Atan2(-r12, cx);
        if (cx > 1e-4f) return Clean(new Vector3(x, MathF.Atan2(r02, r22), MathF.Atan2(r10, r11)));
        return Clean(new Vector3(x, 0, MathF.Atan2(-r01, r00)));
    }

    /// <summary>-0 → +0 and -π → +π (the game's files never hold -π).</summary>
    static Vector3 Clean(Vector3 v)
    {
        static float C(float a) => a == 0 ? 0f : a < -3.1f ? MathF.PI : a;
        return new(C(v.X), C(v.Y), C(v.Z));
    }

    /// <summary>Orientation after turning <paramref name="index"/> by ±90° about a world axis (0 X, 1 Y, 2 Z).</summary>
    public static int Turn(int index, int axis, int quarterTurns)
    {
        var r = axis switch
        {
            0 => Matrix4x4.CreateRotationX(MathF.PI / 2 * quarterTurns),
            1 => Matrix4x4.CreateRotationY(MathF.PI / 2 * quarterTurns),
            _ => Matrix4x4.CreateRotationZ(MathF.PI / 2 * quarterTurns),
        };
        return Nearest(All[index] * r);
    }

    /// <summary>Rotates an integer cell offset by orientation <paramref name="index"/>.</summary>
    public static (int X, int Y, int Z) Apply(int index, int x, int y, int z)
    {
        var v = Vector3.Transform(new Vector3(x, y, z), All[index]);
        return ((int)MathF.Round(v.X), (int)MathF.Round(v.Y), (int)MathF.Round(v.Z));
    }
}
