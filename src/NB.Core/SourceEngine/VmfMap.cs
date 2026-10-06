using System.Globalization;

namespace NB.Core.SourceEngine;

/// <summary>Double-precision 3D vector (brush clipping needs more than float precision at Source map sizes).</summary>
public readonly record struct DVec3(double X, double Y, double Z)
{
    public static DVec3 operator +(DVec3 a, DVec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static DVec3 operator -(DVec3 a, DVec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static DVec3 operator -(DVec3 a) => new(-a.X, -a.Y, -a.Z);
    public static DVec3 operator *(DVec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static DVec3 operator /(DVec3 a, double s) => new(a.X / s, a.Y / s, a.Z / s);
    public static double Dot(DVec3 a, DVec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static DVec3 Cross(DVec3 a, DVec3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public static DVec3 Lerp(DVec3 a, DVec3 b, double t) => a + (b - a) * t;
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public DVec3 Normalized() { double l = Length; return l > 1e-12 ? this / l : this; }
    public static readonly DVec3 Zero = new(0, 0, 0);
    public override string ToString() => FormattableString.Invariant($"({X:G6} {Y:G6} {Z:G6})");
}

/// <summary>A displacement on a 4-sided brush face (VMF "dispinfo"): a (2^power+1)² grid of vertices.</summary>
public sealed class VmfDisp
{
    public int Power;
    public DVec3 StartPosition;
    public double Elevation;
    public int Size => (1 << Power) + 1;
    /// <summary>[row, column] per vertex.</summary>
    public DVec3[,] Normals = new DVec3[0, 0];
    public double[,] Distances = new double[0, 0];
    public DVec3[,] Offsets = new DVec3[0, 0];
}

/// <summary>One brush side: its plane (three points, Hammer order), material and texture axes.</summary>
public sealed class VmfSide
{
    public int Id;
    public DVec3 P0, P1, P2;
    public string Material = "";
    public DVec3 UAxis, VAxis;
    public double UShift, VShift, UScale = 0.25, VScale = 0.25;
    public VmfDisp? Disp;

    /// <summary>Outward plane normal (Source convention: the brush is the intersection of dot(n, x) &lt;= d).</summary>
    public DVec3 Normal => DVec3.Cross(P0 - P1, P2 - P1).Normalized();
    public double Distance => DVec3.Dot(Normal, P0);
}

/// <summary>A brush (VMF "solid") and the entity it belongs to.</summary>
public sealed class VmfSolid
{
    public int Id;
    public List<VmfSide> Sides = new();
    /// <summary>"worldspawn" or the brush entity's class (func_detail, func_brush ...).</summary>
    public string Owner = "worldspawn";
    public VmfEntity? Entity;
}

public sealed class VmfEntity
{
    public int Id;
    public string ClassName = "";
    public readonly Dictionary<string, string> Keys = new(StringComparer.OrdinalIgnoreCase);
    public List<VmfSolid> Solids = new();

    public string Get(string key, string fallback = "") => Keys.TryGetValue(key, out var v) ? v : fallback;
    public DVec3? Origin => Keys.TryGetValue("origin", out var o) ? VmfMap.Vec(o) : null;
    /// <summary>"angles" = pitch yaw roll in degrees.</summary>
    public DVec3 Angles => Keys.TryGetValue("angles", out var a) ? VmfMap.Vec(a) : DVec3.Zero;
    public override string ToString() => $"{ClassName} #{Id}";
}

/// <summary>
/// A Hammer map (.vmf, KeyValues text): the world's brushes and every entity (brush entities with their solids,
/// point entities with their keys). Brushes inside "hidden" blocks are included.
/// </summary>
public sealed class VmfMap
{
    public string Path = "";
    public VmfEntity World = new() { ClassName = "worldspawn" };
    public List<VmfEntity> Entities = new();
    public int MapVersion;

    public IEnumerable<VmfSolid> AllSolids => World.Solids.Concat(Entities.SelectMany(e => e.Solids));

    public static VmfMap Load(string path)
    {
        var m = Parse(File.ReadAllText(path));
        m.Path = path;
        return m;
    }

    public static VmfMap Parse(string text)
    {
        var root = KeyValues.Parse(text);
        var map = new VmfMap();
        if (root.First("versioninfo") is KvNode vi) int.TryParse(vi.Get("mapversion"), out map.MapVersion);
        var world = root.First("world") ?? throw new InvalidDataException("not a VMF file: no 'world' block");
        map.World = ReadEntity(world);
        map.World.ClassName = "worldspawn";
        foreach (var e in root.All("entity")) map.Entities.Add(ReadEntity(e));
        // "hidden" blocks at the top level wrap whole entities
        foreach (var h in root.All("hidden")) foreach (var e in h.All("entity")) map.Entities.Add(ReadEntity(e));
        return map;
    }

    static VmfEntity ReadEntity(KvNode n)
    {
        var e = new VmfEntity { ClassName = n.Get("classname") };
        int.TryParse(n.Get("id"), out e.Id);
        foreach (var kv in n.Values) e.Keys.TryAdd(kv.Key, kv.Value);
        var solids = n.All("solid").Concat(n.All("hidden").SelectMany(h => h.All("solid")));
        foreach (var s in solids)
        {
            var solid = new VmfSolid { Owner = e.ClassName.Length > 0 ? e.ClassName : "worldspawn", Entity = e };
            int.TryParse(s.Get("id"), out solid.Id);
            foreach (var side in s.All("side"))
            {
                try { solid.Sides.Add(ReadSide(side)); }
                catch (FormatException) { /* malformed side: the brush is clipped by its other planes */ }
            }
            if (solid.Sides.Count >= 4) e.Solids.Add(solid);
        }
        return e;
    }

    static VmfSide ReadSide(KvNode n)
    {
        var s = new VmfSide { Material = n.Get("material").Replace('\\', '/').ToUpperInvariant() };
        int.TryParse(n.Get("id"), out s.Id);
        var pts = Numbers(n.Get("plane"));
        if (pts.Length < 9) throw new FormatException("plane");
        s.P0 = new(pts[0], pts[1], pts[2]); s.P1 = new(pts[3], pts[4], pts[5]); s.P2 = new(pts[6], pts[7], pts[8]);
        (s.UAxis, s.UShift, s.UScale) = Axis(n.Get("uaxis"), new DVec3(1, 0, 0));
        (s.VAxis, s.VShift, s.VScale) = Axis(n.Get("vaxis"), new DVec3(0, -1, 0));
        if (n.First("dispinfo") is KvNode d) s.Disp = ReadDisp(d);
        return s;
    }

    /// <summary>"[x y z shift] scale" (Source) or the older "x y z shift scale" forms.</summary>
    static (DVec3, double, double) Axis(string text, DVec3 fallback)
    {
        var f = Numbers(text);
        if (f.Length < 4) return (fallback, 0, 0.25);
        double scale = f.Length >= 5 ? f[4] : 0.25;
        if (Math.Abs(scale) < 1e-6) scale = 0.25;
        return (new DVec3(f[0], f[1], f[2]), f[3], scale);
    }

    static VmfDisp ReadDisp(KvNode n)
    {
        var d = new VmfDisp();
        int.TryParse(n.Get("power"), NumberStyles.Integer, CultureInfo.InvariantCulture, out d.Power);
        d.Power = Math.Clamp(d.Power, 1, 4);
        var sp = Numbers(n.Get("startposition"));
        if (sp.Length >= 3) d.StartPosition = new(sp[0], sp[1], sp[2]);
        double.TryParse(n.Get("elevation"), NumberStyles.Float, CultureInfo.InvariantCulture, out d.Elevation);
        int size = d.Size;
        d.Normals = new DVec3[size, size]; d.Distances = new double[size, size]; d.Offsets = new DVec3[size, size];
        for (int r = 0; r < size; r++) for (int c = 0; c < size; c++) d.Normals[r, c] = new DVec3(0, 0, 1);
        void Rows(string block, int per, Action<int, int, double[]> set)
        {
            if (n.First(block) is not KvNode b) return;
            for (int r = 0; r < size; r++)
            {
                var f = Numbers(b.Get("row" + r));
                for (int c = 0; c < size && (c + 1) * per <= f.Length; c++) set(r, c, f[(c * per)..((c + 1) * per)]);
            }
        }
        Rows("normals", 3, (r, c, f) => d.Normals[r, c] = new DVec3(f[0], f[1], f[2]));
        Rows("distances", 1, (r, c, f) => d.Distances[r, c] = f[0]);
        Rows("offsets", 3, (r, c, f) => d.Offsets[r, c] = new DVec3(f[0], f[1], f[2]));
        return d;
    }

    /// <summary>Every number in a string such as "(1 2 3) (4 5 6)" or "[1 0 0 0] 0.25".</summary>
    public static double[] Numbers(string s)
    {
        var res = new List<double>();
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsDigit(c) || c == '-' || c == '+' || c == '.')
            {
                int st = i; i++;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || ((s[i] == '-' || s[i] == '+') && (s[i - 1] == 'e' || s[i - 1] == 'E')))) i++;
                if (double.TryParse(s.AsSpan(st, i - st), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) res.Add(v);
            }
            else i++;
        }
        return res.ToArray();
    }

    public static DVec3 Vec(string s)
    {
        var f = Numbers(s);
        return f.Length >= 3 ? new DVec3(f[0], f[1], f[2]) : DVec3.Zero;
    }

    /// <summary>"r g b [brightness]" colour keys (_light, _ambient, fogcolor, rendercolor).</summary>
    public static (double R, double G, double B, double Brightness) Colour(string s, double defaultBrightness = 200)
    {
        var f = Numbers(s);
        if (f.Length < 3) return (255, 255, 255, defaultBrightness);
        return (f[0], f[1], f[2], f.Length >= 4 ? f[3] : defaultBrightness);
    }
}
