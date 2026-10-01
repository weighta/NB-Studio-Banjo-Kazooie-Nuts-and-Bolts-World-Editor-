using System.Numerics;
using System.Text.RegularExpressions;
using NB.Core.Formats;
using NB.Core.IO;

namespace NB.Core.World;

/// <summary>
/// One record of an aid_marker asset (placed actors, pickups, spawn points, paths, volumes…).
/// Record layout (big-endian, no pointers):
///   +0x00 u32 size  +0x04 u16 type  +0x06 u16 index  +0x08 u32 [unknown]  +0x0C u32 flags [unknown]  +0x10 u32 [unknown]
///   +0x14 float3 position  +0x20 float rotX  +0x24 float rotY (yaw)  +0x28 float rotZ  (radians)  +0x2C float scale
///   +0x30… type-specific payload (asset ids such as objparams 0x1F…, scripts 0x19…, enum strings) — preserved verbatim.
/// Every record type has a fixed size (26 943 records across the game parse exactly).
/// </summary>
public sealed class MarkerRecord
{
    public int Offset, Size, Type, Index;
    public Vector3 Position, Rotation;
    public float Scale;
    /// <summary>Path nodes: index of the next node (same marker asset), or -1.</summary>
    public int Link = -1;
    /// <summary>Link value currently stored in the asset (Link != SavedLink = unsaved edit).</summary>
    public int SavedLink = -1;
    public List<uint> AssetIds = new();
    /// <summary>Names for <see cref="AssetIds"/> resolved from the bundle (same order; "?" when not in this bundle).</summary>
    public List<string> AssetNames = new();
    public List<string> Strings = new();

    /// <summary>Tentative names from the strings and asset ids found in each record type (not confirmed from code).</summary>
    public static string TypeName(int t) => t switch
    {
        1 => "point", 6 => "actor spawn", 7 => "volume / height", 14 => "indicator / pickup", 18 => "node", 22 => "path node",
        13 => "object", 8 => "actor (alt)", 36 => "gated collectable", 28 => "script trigger", 4 => "drone action",
        _ => "type " + t,
    };

    public Matrix4x4 Matrix => Matrix4x4.CreateScale(Scale == 0 ? 1 : Scale) * Matrix4x4.CreateRotationX(Rotation.X) * Matrix4x4.CreateRotationY(Rotation.Y) * Matrix4x4.CreateRotationZ(Rotation.Z) * Matrix4x4.CreateTranslation(Position);
}

public sealed class MarkerAsset
{
    public string Name = "";
    public int Symbol;
    /// <summary>Bundle and CAFF the asset was read from (world bundle or an act bundle).</summary>
    public uint Bundle;
    public CaffFile? Caff;
    public List<MarkerRecord> Records = new();

    static readonly HashSet<byte> IdTypes = new() { 0x00, 0x04, 0x08, 0x0B, 0x0D, 0x0E, 0x19, 0x1F, 0x43 };

    public static MarkerAsset Parse(CaffFile caff, int symbol)
    {
        var d = caff.PartsOf(symbol).First(p => caff.SectionOf(p).Name == ".data").Data;
        var m = new MarkerAsset { Name = caff.Symbols[symbol - 1], Symbol = symbol };
        int o = 0;
        while (o + 0x30 <= d.Length)
        {
            int size = BE.S32(d, o);
            if (size < 0x30 || o + size > d.Length) break;
            var r = new MarkerRecord
            {
                Offset = o, Size = size, Type = BE.U16(d, o + 4), Index = BE.U16(d, o + 6),
                Position = new Vector3(BE.F32(d, o + 0x14), BE.F32(d, o + 0x18), BE.F32(d, o + 0x1C)),
                Rotation = new Vector3(BE.F32(d, o + 0x20), BE.F32(d, o + 0x24), BE.F32(d, o + 0x28)),
                Scale = BE.F32(d, o + 0x2C),
            };
            // path nodes (type 22): +8 u16 = index of the next node of the path
            if (r.Type == 22) r.Link = r.SavedLink = BE.U16(d, o + 8);
            for (int k = 0x30; k + 4 <= size; k += 4)
            {
                uint v = BE.U32(d, o + k);
                if (v != 0 && IdTypes.Contains((byte)(v >> 24)) && (v & 0xFFFFFF) != 0 && !(d[o + k] >= 0x20 && d[o + k + 1] >= 0x20 && d[o + k + 2] >= 0x20)) r.AssetIds.Add(v);
            }
            foreach (Match s in Regex.Matches(System.Text.Encoding.Latin1.GetString(d, o + 0x30, size - 0x30), "[A-Za-z][A-Za-z0-9_]{7,}")) r.Strings.Add(s.Value);
            m.Records.Add(r);
            o += size;
        }
        if (o != d.Length) throw new InvalidDataException($"{m.Name}: marker records end at 0x{o:X}, data is 0x{d.Length:X}");
        return m;
    }

    /// <summary>Writes position / rotation / scale of a record back into the asset (same size, in place).</summary>
    public static void WriteTransform(CaffFile caff, int symbol, MarkerRecord r)
    {
        var d = caff.PartsOf(symbol).First(p => caff.SectionOf(p).Name == ".data").Data;
        BE.WF32(d, r.Offset + 0x14, r.Position.X); BE.WF32(d, r.Offset + 0x18, r.Position.Y); BE.WF32(d, r.Offset + 0x1C, r.Position.Z);
        BE.WF32(d, r.Offset + 0x20, r.Rotation.X); BE.WF32(d, r.Offset + 0x24, r.Rotation.Y); BE.WF32(d, r.Offset + 0x28, r.Rotation.Z);
        BE.WF32(d, r.Offset + 0x2C, r.Scale);
    }

    /// <summary>Path nodes (type 22): sets the index of the next node (+8, u16; a node linking to itself ends the path).</summary>
    public static void WriteLink(CaffFile caff, int symbol, MarkerRecord r)
    {
        if (r.Type != 22) throw new InvalidOperationException("only path nodes (type 22) have a next-node link");
        if (r.Link < 0 || r.Link > 0xFFFF) throw new ArgumentOutOfRangeException(nameof(r), "next-node index must be 0..65535");
        var d = caff.PartsOf(symbol).First(p => caff.SectionOf(p).Name == ".data").Data;
        BE.W16(d, r.Offset + 8, (ushort)r.Link);
        r.SavedLink = r.Link;
    }
}
