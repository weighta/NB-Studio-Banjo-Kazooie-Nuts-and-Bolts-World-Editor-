using System.Numerics;

namespace NB.Core.Vehicles;

/// <summary>
/// Which parts of a vehicle hold together, from the parts' attach data (aid_avatarhavokdata_*: one record per outer face
/// of the footprint with its centre in half cells and an "attachable" flag, <see cref="NB.Core.Parts.AttachData"/>).
/// Two parts are joined where both have an attachable face at the same place with opposite normals (the rule the convoy
/// checker found: every one of the game's 274 blueprints comes out in one piece with it). Parts not joined (through other
/// parts) to the piece with the driver's seat fall off when the vehicle is built: the garage marks them with its hazard
/// triangle.
/// </summary>
public sealed class VehicleConnectivity
{
    public readonly List<VehicleDocument.Part> Parts;
    /// <summary>Piece index of each part (same order as <see cref="Parts"/>); the main piece is 0.</summary>
    public readonly int[] Piece;
    public int PieceCount;
    /// <summary>Parts not in the main piece.</summary>
    public readonly HashSet<VehicleDocument.Part> Floating = new();
    /// <summary>Parts whose footprint shares a cell with another part's (face cells, so the trolley's wheels that hang
    /// into the corners of its tray are not counted).</summary>
    public readonly HashSet<VehicleDocument.Part> Overlapping = new();
    /// <summary>Parts with no attach data in the workspace (unknown or modded without footprint).</summary>
    public readonly HashSet<VehicleDocument.Part> NoData = new();

    public readonly record struct Face(Vector3 Position, (int X, int Y, int Z) Normal, bool Attachable);

    /// <summary>A part's faces in vehicle space (cell units): footprint faces rotated by its orientation and moved to its cell.</summary>
    public static IEnumerable<Face> Faces(VehicleDocument.Part p, PartInfo? info)
    {
        if (info?.Attach is not { } a || a.Points.Count == 0) yield break;
        var m = Orientations.All[p.Orientation];
        foreach (var f in a.Points)
        {
            var pos = Vector3.Transform(f.Position, m) + new Vector3(p.X, p.Y, p.Z);
            var n = Vector3.TransformNormal(f.Normal, m);
            yield return new Face(pos, ((int)MathF.Round(n.X), (int)MathF.Round(n.Y), (int)MathF.Round(n.Z)), f.Attachable);
        }
    }

    static (int, int, int) HalfKey(Vector3 v) => ((int)MathF.Round(v.X * 2), (int)MathF.Round(v.Y * 2), (int)MathF.Round(v.Z * 2));

    /// <summary>Cells a part's faces enclose (the cell behind each face).</summary>
    public static IEnumerable<(int X, int Y, int Z)> FaceCells(VehicleDocument.Part p, PartInfo? info)
    {
        var seen = new HashSet<(int, int, int)>();
        foreach (var f in Faces(p, info))
        {
            var c = f.Position - new Vector3(f.Normal.X, f.Normal.Y, f.Normal.Z) * 0.5f;
            var k = ((int)MathF.Round(c.X), (int)MathF.Round(c.Y), (int)MathF.Round(c.Z));
            if (seen.Add(k)) yield return k;
        }
        if (seen.Count == 0) yield return (p.X, p.Y, p.Z);
    }

    /// <summary>Two tow bars facing each other (their ends are not attachable faces) hitch a trailer: towed, not floating.</summary>
    public readonly List<(VehicleDocument.Part A, VehicleDocument.Part B)> Hitches = new();

    static bool IsTowbar(PartInfo? i) => i != null && i.Class == "objDefId_vehicleBlockTowbar";

    public static bool IsDriverSeat(PartInfo? i) =>
        i != null && i.Class == "objDefId_vehicleBlockSeat" && (i.Key.StartsWith("seats_") || i.Variant.EndsWith("ai"));

    public VehicleConnectivity(IReadOnlyList<VehicleDocument.Part> parts, PartCatalog? cat)
    {
        Parts = parts.ToList();
        int n = Parts.Count;
        Piece = new int[n];
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }

        var faces = new Dictionary<(int, int, int), List<(int Part, (int, int, int) N, bool Ok)>>();
        var cells = new Dictionary<(int, int, int), int>();
        for (int i = 0; i < n; i++)
        {
            var info = cat?[Parts[i].B.Part];
            if (info?.Attach == null) NoData.Add(Parts[i]);
            foreach (var f in Faces(Parts[i], info))
            {
                var k = HalfKey(f.Position);
                if (!faces.TryGetValue(k, out var l)) faces[k] = l = new();
                l.Add((i, f.Normal, f.Attachable));
            }
            foreach (var c in FaceCells(Parts[i], info))
                if (cells.TryGetValue(c, out int other) && other != i) { Overlapping.Add(Parts[i]); Overlapping.Add(Parts[other]); }
                else cells[c] = i;
        }
        foreach (var l in faces.Values)
            for (int a = 0; a < l.Count; a++)
                for (int b = a + 1; b < l.Count; b++)
                {
                    var (i, ni, oki) = l[a]; var (j, nj, okj) = l[b];
                    if (i == j || ni.Item1 != -nj.Item1 || ni.Item2 != -nj.Item2 || ni.Item3 != -nj.Item3) continue;
                    if (oki && okj) Union(i, j);
                    else if (IsTowbar(cat?[Parts[i].B.Part]) && IsTowbar(cat?[Parts[j].B.Part])) { Union(i, j); Hitches.Add((Parts[i], Parts[j])); }
                }
        // parts without attach data cannot be judged: they join whatever they touch (no false hazards for modded parts)
        foreach (var p in NoData)
        {
            int i = Parts.IndexOf(p);
            for (int j = 0; j < n; j++)
                if (j != i && Math.Abs(Parts[j].X - p.X) + Math.Abs(Parts[j].Y - p.Y) + Math.Abs(Parts[j].Z - p.Z) <= 1) Union(i, j);
        }
        // main piece: the one with a driver's seat (else the biggest)
        var roots = Enumerable.Range(0, n).GroupBy(Find).ToList();
        var main = roots.OrderByDescending(g => g.Any(i => IsDriverSeat(cat?[Parts[i].B.Part])) ? 1 : 0).ThenByDescending(g => g.Count()).FirstOrDefault();
        PieceCount = roots.Count;
        int next = 1;
        foreach (var g in roots)
        {
            int id = g == main ? 0 : next++;
            foreach (int i in g) { Piece[i] = id; if (id != 0) Floating.Add(Parts[i]); }
        }
    }

    /// <summary>Would a part placed like <paramref name="probe"/> attach to <paramref name="doc"/>'s main piece (0 = attached,
    /// 1 = floating: touches nothing it can attach to, 2 = blocked: shares a cell with a part)?</summary>
    public static int PlacementStatus(VehicleDocument doc, VehicleDocument.Part probe, PartCatalog? cat, VehicleConnectivity? current = null)
    {
        var info = cat?[probe.B.Part];
        var occupied = new HashSet<(int, int, int)>();
        foreach (var p in doc.Parts) if (p != probe) foreach (var c in FaceCells(p, cat?[p.B.Part])) occupied.Add(c);
        if (FaceCells(probe, info).Any(occupied.Contains)) return 2;
        if (doc.Parts.Count == 0 || (doc.Parts.Count == 1 && doc.Parts[0] == probe)) return 0;
        var conn = current ?? new VehicleConnectivity(doc.Parts.Where(p => p != probe).ToList(), cat);
        var mine = Faces(probe, info).Where(f => f.Attachable).ToList();
        if (info?.Attach == null) return 0;
        for (int i = 0; i < conn.Parts.Count; i++)
        {
            if (conn.Piece[i] != 0 || conn.Parts[i] == probe) continue;
            foreach (var f in Faces(conn.Parts[i], cat?[conn.Parts[i].B.Part]))
            {
                if (!f.Attachable) continue;
                var k = HalfKey(f.Position);
                if (mine.Any(m => HalfKey(m.Position) == k && m.Normal.X == -f.Normal.X && m.Normal.Y == -f.Normal.Y && m.Normal.Z == -f.Normal.Z)) return 0;
            }
        }
        return 1;
    }
}
