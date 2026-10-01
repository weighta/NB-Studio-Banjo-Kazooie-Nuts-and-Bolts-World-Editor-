using System.Numerics;
using System.Text;
using NB.Core.Formats;
using NB.Core.IO;

namespace NB.Core.World;

/// <summary>
/// Scripted AI vehicle routes: appends path-node loops (marker type 22) and a vehicle spawn (marker type 21) to a
/// world's marker asset, the way the title screen and the Loco-coco train are built (work/ai_research/REPORT.md):
/// <list type="bullet">
/// <item>type 22 (104 bytes): +8 u16 record index of the next node, +0x0C marker-set mask (0 = always), +0x34 width,
/// +0x38 0.1, +0x48 route tag (non-zero on the start node).</item>
/// <item>type 21 (92 bytes): +8 u16 start path node, +0x0C mask, +0x34 grid slot (−1), +0x38 vehicle blueprint id,
/// +0x3C driver actor, +0x40 driver strategy (Jogger = follow the path), +0x44..+0x54 gunner/passenger/requirement.</item>
/// </list>
/// Marker records carry no pointers, so records can be appended to the asset's .data freely.
/// </summary>
public static class AiRoute
{
    public const int NodeSize = 104, SpawnSize = 92;

    static byte[] Data(CaffFile caff, int symbol) => caff.PartsOf(symbol).First(p => caff.SectionOf(p).Name == ".data").Data;

    /// <summary>
    /// Adds a record before the asset's closing type-0 record: the game's loader (0x82397AA8) copies records until the
    /// first type-0 one, so anything after it is never loaded (shipped marker assets end with a 52-byte type-0 record).
    /// </summary>
    static void Append(CaffFile caff, int symbol, byte[] rec)
    {
        var part = caff.PartsOf(symbol).First(p => caff.SectionOf(p).Name == ".data");
        var last = MarkerAsset.Parse(caff, symbol).Records.LastOrDefault();
        int at = last != null && last.Type == 0 ? last.Offset : part.Data.Length;
        var nd = new byte[part.Data.Length + rec.Length];
        Buffer.BlockCopy(part.Data, 0, nd, 0, at);
        Buffer.BlockCopy(rec, 0, nd, at, rec.Length);
        Buffer.BlockCopy(part.Data, at, nd, at + rec.Length, part.Data.Length - at);
        part.Data = nd; part.Size = nd.Length;
    }

    /// <summary>Moves a type-0 record that is not last (left there by an older append) to the end. Returns true if moved.</summary>
    public static bool FixTerminator(CaffFile caff, int symbol)
    {
        var part = caff.PartsOf(symbol).First(p => caff.SectionOf(p).Name == ".data");
        var recs = MarkerAsset.Parse(caff, symbol).Records;
        var term = recs.FirstOrDefault(r => r.Type == 0);
        if (term == null || term == recs[^1]) return false;
        var o = new MemoryStream();
        foreach (var r in recs.Where(r => r != term)) o.Write(part.Data, r.Offset, r.Size);
        o.Write(part.Data, term.Offset, term.Size);
        part.Data = o.ToArray(); part.Size = part.Data.Length;
        return true;
    }

    /// <summary>Highest record index in the asset.</summary>
    public static int MaxIndex(CaffFile caff, int symbol) => MarkerAsset.Parse(caff, symbol).Records.Max(r => r.Index);

    /// <summary>Removes records with index ≥ <paramref name="fromIndex"/> of the given types (undo of earlier appends).</summary>
    public static int RemoveFrom(CaffFile caff, int symbol, int fromIndex, params int[] types)
    {
        var part = caff.PartsOf(symbol).First(p => caff.SectionOf(p).Name == ".data");
        var m = MarkerAsset.Parse(caff, symbol);
        var keep = new MemoryStream(); int n = 0;
        foreach (var r in m.Records)
        {
            if (r.Index >= fromIndex && types.Contains(r.Type)) { n++; continue; }
            keep.Write(part.Data, r.Offset, r.Size);
        }
        part.Data = keep.ToArray(); part.Size = part.Data.Length;
        return n;
    }

    /// <summary>
    /// Appends a closed loop of path nodes through <paramref name="points"/> (each node faces the next one) and returns
    /// the record index of the first node. <paramref name="template"/> = any 104-byte type 22 record.
    /// </summary>
    public static int AddLoop(CaffFile caff, int symbol, IReadOnlyList<Vector3> points, float width, int tag, byte[] template, int firstIndex, int loopFrom = 0)
    {
        if (template.Length != NodeSize || BE.U16(template, 4) != 22) throw new ArgumentException("template must be a type 22 record");
        if (points.Count < 3) throw new ArgumentException("a loop needs at least 3 points");
        if (firstIndex + points.Count > 10000) throw new ArgumentException("marker indices above 10000 belong to the second marker set");
        for (int i = 0; i < points.Count; i++)
        {
            var r = (byte[])template.Clone();
            var p = points[i]; var q = points[(i + 1) % points.Count];
            BE.W16(r, 6, (ushort)(firstIndex + i));
            BE.W16(r, 8, (ushort)(firstIndex + (i + 1 < points.Count ? i + 1 : loopFrom)));   // loopFrom > 0: nodes before it are a one-way run-up
            BE.W32(r, 0x0C, 0u);
            BE.WF32(r, 0x14, p.X); BE.WF32(r, 0x18, p.Y); BE.WF32(r, 0x1C, p.Z);
            BE.WF32(r, 0x20, 0); BE.WF32(r, 0x24, MathF.Atan2(q.X - p.X, q.Z - p.Z)); BE.WF32(r, 0x28, 0); BE.WF32(r, 0x2C, 1);
            BE.WF32(r, 0x34, width);
            BE.W32(r, 0x48, i == 0 ? (uint)tag : 0u);
            Append(caff, symbol, r);
        }
        return firstIndex;
    }

    /// <summary>Appends a vehicle spawn (type 21). <paramref name="template"/> = any 92-byte type 21 record.</summary>
    public static int AddVehicleSpawn(CaffFile caff, int symbol, byte[] template, int index, Vector3 pos, float yaw, int startNode,
        uint vehicle, uint driver, uint strategy, int gridSlot = -1, uint mask = 0)
    {
        if (template.Length != SpawnSize || BE.U16(template, 4) != 21) throw new ArgumentException("template must be a type 21 record");
        var r = (byte[])template.Clone();
        BE.W16(r, 6, (ushort)index); BE.W16(r, 8, (ushort)startNode);
        BE.W32(r, 0x0C, mask); BE.W32(r, 0x10, 0u);
        BE.WF32(r, 0x14, pos.X); BE.WF32(r, 0x18, pos.Y); BE.WF32(r, 0x1C, pos.Z);
        BE.WF32(r, 0x20, 0); BE.WF32(r, 0x24, yaw); BE.WF32(r, 0x28, 0); BE.WF32(r, 0x2C, 1);
        BE.W32(r, 0x34, unchecked((uint)gridSlot));   // −1 = AI vehicle; 0 = the player's own vehicle (race grids)
        BE.W32(r, 0x38, vehicle); BE.W32(r, 0x3C, driver); BE.W32(r, 0x40, strategy);
        for (int o = 0x44; o + 4 <= 0x58; o += 4) BE.W32(r, o, 0u);
        Append(caff, symbol, r);
        return index;
    }

    /// <summary>
    /// The player's start position as a type-21 grid record (slot 0, no vehicle/driver/strategy), as race acts have it.
    /// Needed once a level has any type-21 record: the grid spawn (0x8251F630) then places the player's vehicle only at a
    /// record whose slot matches — without one the player started Showdown Town on foot (seen in Xenia).
    /// </summary>
    public static int AddPlayerSlot(CaffFile caff, int symbol, byte[] template, int index, Vector3 pos, float yaw) =>
        AddVehicleSpawn(caff, symbol, template, index, pos, yaw, 0, 0, 0, 0, gridSlot: 0);

    /// <summary>First record of the given type in a marker asset (template for new records).</summary>
    public static byte[]? FirstRecord(CaffFile caff, int symbol, int type)
    {
        var d = Data(caff, symbol);
        var r = MarkerAsset.Parse(caff, symbol).Records.FirstOrDefault(x => x.Type == type);
        return r == null ? null : d.AsSpan(r.Offset, r.Size).ToArray();
    }

    /// <summary>Oval through <paramref name="n"/> points (centre, radii along x and z, height y), counter-clockwise seen from above.</summary>
    public static List<Vector3> Oval(Vector3 centre, float rx, float rz, int n) =>
        Enumerable.Range(0, n).Select(i => { float a = 2 * MathF.PI * i / n; return centre + new Vector3(rx * MathF.Cos(a), 0, rz * MathF.Sin(a)); }).ToList();

    /// <summary>
    /// A vehicle Jogger strategy cloned from <paramref name="templateSymbol"/> (an entityStrategyJogger such as
    /// actorstrategy_showdowntown_mrfit) with the fields vehicle Joggers use (actorstrategy_frontend_fast): +0x224 = 4,
    /// no goals (+0x280), dialog (+0x2C8) or script (+0x2CC), flags +0x2B8..+0x2C4 = 0,1,1,0, speed +0x2AC.
    /// </summary>
    public static int MakeVehicleStrategy(CaffFile caff, int templateSymbol, string newName, float speed)
    {
        int s = caff.Symbols.FindIndex(x => AssetIds.DisplayName(x) == newName) + 1;
        if (s == 0) s = CaffEdit.CloneAsset(caff, templateSymbol, newName);
        var d = Data(caff, s);
        BE.W32(d, 0x224, 4u); BE.W32(d, 0x280, 0u);
        BE.WF32(d, 0x2AC, speed);
        BE.W32(d, 0x2B8, 0u); BE.W32(d, 0x2BC, 1u); BE.W32(d, 0x2C0, 1u); BE.W32(d, 0x2C4, 0u);
        BE.W32(d, 0x2C8, 0u); BE.W32(d, 0x2CC, 0u);
        return s;
    }
}
