using System.Numerics;
using NB.Core.IO;

namespace NB.Core.Tags;

/// <summary>
/// aid_vehicle_* assets (garage blueprints / built vehicles): a 0x7C-byte header followed by one 0x24-byte record per
/// block. Layout established from 372 vehicle assets (every record's part id resolves to a vehicleblock objparams):
/// header +0 u16 block count, +2 u16 flags, +4..+0x1C floats (unknown), +0x20 name text (e.g. "trolley7");
/// block +0 u8 grid x,y,z (+3 pad), +4 u8 flag, +6 u8 part category, +8 u32 objparams id of the part,
/// +0xC/+0x10/+0x14 f32 rotation (radians, multiples of π/2), +0x18 u32 paint (RGBA), +0x1C u32 action-button mask, +0x20 u32.
/// </summary>
public sealed class VehicleAsset
{
    public const int HeaderSize = 0x7C, BlockSize = 0x24;
    public int Count;
    public ushort Flags;
    public string Name = "";
    public readonly List<VehicleBlock> Blocks = new();

    public static VehicleAsset? TryParse(byte[] d)
    {
        if (d.Length < HeaderSize) return null;
        int n = BE.U16(d, 0);
        if (HeaderSize + n * BlockSize > d.Length) return null;   // n == 0: empty slot (e.g. custom_reservedslot2)
        var v = new VehicleAsset { Count = n, Flags = BE.U16(d, 2), Name = BE.CStr(d, 0x20, 0x4C) };
        for (int i = 0; i < n; i++)
        {
            int o = HeaderSize + i * BlockSize;
            v.Blocks.Add(new VehicleBlock
            {
                Offset = o, X = d[o], Y = d[o + 1], Z = d[o + 2], Flag = d[o + 4], Category = d[o + 6], Part = BE.U32(d, o + 8),
                Rotation = new Vector3(BE.F32(d, o + 0xC), BE.F32(d, o + 0x10), BE.F32(d, o + 0x14)),
                Paint = BE.U32(d, o + 0x18), Buttons = BE.U32(d, o + 0x1C),
            });
        }
        return v;
    }

    public IEnumerable<(int Offset, string Label)> Labels(Func<uint, string?> name)
    {
        yield return (0, "block count (u16) | flags (u16)");
        for (int o = 4; o < 0x20; o += 4) yield return (o, "header float (unknown)");
        yield return (0x20, $"name text \"{Name}\"");
        foreach (var (b, i) in Blocks.Select((b, i) => (b, i)))
        {
            string part = name(b.Part)?.Replace("aid_objparams_banjox_vehicleblock_", "") ?? $"0x{b.Part:X8}";
            yield return (b.Offset, $"block {i}: grid x,y,z bytes ({b.X},{b.Y},{b.Z})");
            yield return (b.Offset + 4, $"block {i}: flag / part category ({b.Category})");
            yield return (b.Offset + 8, $"block {i}: part → {part}");
            yield return (b.Offset + 0xC, $"block {i}: rotation x (rad)");
            yield return (b.Offset + 0x10, $"block {i}: rotation y (rad)");
            yield return (b.Offset + 0x14, $"block {i}: rotation z (rad)");
            yield return (b.Offset + 0x18, $"block {i}: paint");
            yield return (b.Offset + 0x1C, $"block {i}: action button mask");
            yield return (b.Offset + 0x20, $"block {i}: unknown");
        }
    }
}

public sealed class VehicleBlock
{
    public int Offset;
    public byte X, Y, Z, Flag, Category;
    public uint Part, Paint, Buttons;
    public Vector3 Rotation;
}
