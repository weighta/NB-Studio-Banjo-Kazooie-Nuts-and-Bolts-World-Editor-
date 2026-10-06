using System.Globalization;
using System.Numerics;

namespace NB.Core.SourceEngine;

/// <summary>Options of the light bake (Source lights into lightmaps that the game shows through the template's
/// ambient-occlusion slot, which multiplies the colour texture with UV set 2).</summary>
public sealed class LightBakeOptions
{
    public bool Enabled = true;
    /// <summary>Luxel size in Source units (Source's default lightmapscale is 16).</summary>
    public float LuxelSize = 16f;
    public int PageSize = 1024;
    /// <summary>Most luxels along one side of one face (larger faces get coarser luxels).</summary>
    public int MaxFaceLuxels = 160;
    /// <summary>Brightness of the baked light (1 = Source's linear light value 1.0 shows as full texture colour).</summary>
    public float Exposure = 1.0f;
    /// <summary>Source's LDR overbright factor: lightmapped surfaces show twice the stored light (vrad light 0.5 = full
    /// texture colour).</summary>
    public float Overbright = 2f;
    /// <summary>Display gamma applied to the linear Source light (2.2 like a monitor; 1 = linear).</summary>
    public float Gamma = 2.2f;
    /// <summary>Hemisphere rays per luxel for the sky light (light_environment _ambient).</summary>
    public int SkySamples = 24;
    /// <summary>Smallest light any luxel gets (linear), so unlit corners are not pure black.</summary>
    public float MinLight = 0.015f;
    /// <summary>Crude one-bounce term: this fraction of the average direct light of the surrounding area is added.</summary>
    public float Bounce = 0.5f;
    /// <summary>Game light the bake divides by (the light setups get these values): ambient grey level 0..1 and sun
    /// intensity. Lightmap = exposure * gamma(light) / (ambient * (1.107 + 0.519 n.y) + sun * max(0, n.sun)).</summary>
    public float GameAmbient = 0.9f, GameSun = 0.3f;
    /// <summary>Lights of these classes are baked.</summary>
    public bool PointLights = true, SpotLights = true, Sun = true, Sky = true;
}

/// <summary>A light of the map in Source coordinates with vrad's falloff (see <see cref="LightBaker.LightsFrom"/>).</summary>
public sealed class BakeLight
{
    public string Source = "";
    public Vector3 Pos, Dir;
    public Vector3 Colour;   // linear intensity (vrad: pow(c/255, 2.2) * brightness / 255, scaled to 1 at 100 units)
    public float C, L, Q, Cutoff;
    public bool Spot;
    public float CosInner, CosOuter, Exponent;
    public float Radius;     // beyond this the light adds less than 1/500
}

/// <summary>Bounding-volume hierarchy over triangles for shadow / visibility rays (any hit).</summary>
public sealed class TriangleBvh
{
    readonly float[] _v;     // per triangle: 9 floats (a, b, c)
    readonly int[] _tri;     // triangle order
    readonly float[] _bmin, _bmax; readonly int[] _left, _start, _count;
    int _nodes;
    public int Triangles => _tri.Length;

    readonly bool[] _sky;    // per triangle: a sky face (rays that reach it see the sky)

    public TriangleBvh(IReadOnlyList<Vector3> tris, IReadOnlyList<bool>? sky = null)
    {
        int n = tris.Count / 3;
        _sky = new bool[n];
        if (sky != null) for (int i = 0; i < n && i < sky.Count; i++) _sky[i] = sky[i];
        _v = new float[n * 9];
        for (int i = 0; i < n; i++) for (int k = 0; k < 3; k++) { var p = tris[3 * i + k]; _v[9 * i + 3 * k] = p.X; _v[9 * i + 3 * k + 1] = p.Y; _v[9 * i + 3 * k + 2] = p.Z; }
        _tri = Enumerable.Range(0, n).ToArray();
        int cap = Math.Max(1, 2 * n);
        _bmin = new float[cap * 3]; _bmax = new float[cap * 3]; _left = new int[cap]; _start = new int[cap]; _count = new int[cap];
        var cen = new float[n * 3];
        for (int i = 0; i < n; i++) for (int a = 0; a < 3; a++) cen[3 * i + a] = (_v[9 * i + a] + _v[9 * i + 3 + a] + _v[9 * i + 6 + a]) / 3;
        _nodes = 1;
        if (n > 0) Build(0, 0, n, cen);
        else { _count[0] = 0; }
    }

    void Build(int node, int start, int count, float[] cen)
    {
        float mnx = float.MaxValue, mny = float.MaxValue, mnz = float.MaxValue, mxx = float.MinValue, mxy = float.MinValue, mxz = float.MinValue;
        float cmnx = float.MaxValue, cmny = float.MaxValue, cmnz = float.MaxValue, cmxx = float.MinValue, cmxy = float.MinValue, cmxz = float.MinValue;
        for (int i = start; i < start + count; i++)
        {
            int t = _tri[i];
            for (int k = 0; k < 3; k++)
            {
                float x = _v[9 * t + 3 * k], y = _v[9 * t + 3 * k + 1], z = _v[9 * t + 3 * k + 2];
                if (x < mnx) mnx = x; if (y < mny) mny = y; if (z < mnz) mnz = z; if (x > mxx) mxx = x; if (y > mxy) mxy = y; if (z > mxz) mxz = z;
            }
            float cx = cen[3 * t], cy = cen[3 * t + 1], cz = cen[3 * t + 2];
            if (cx < cmnx) cmnx = cx; if (cy < cmny) cmny = cy; if (cz < cmnz) cmnz = cz; if (cx > cmxx) cmxx = cx; if (cy > cmxy) cmxy = cy; if (cz > cmxz) cmxz = cz;
        }
        _bmin[3 * node] = mnx; _bmin[3 * node + 1] = mny; _bmin[3 * node + 2] = mnz; _bmax[3 * node] = mxx; _bmax[3 * node + 1] = mxy; _bmax[3 * node + 2] = mxz;
        if (count <= 4) { _left[node] = -1; _start[node] = start; _count[node] = count; return; }
        float ex = cmxx - cmnx, ey = cmxy - cmny, ez = cmxz - cmnz;
        int axis = ex >= ey && ex >= ez ? 0 : ey >= ez ? 1 : 2;
        float split = axis == 0 ? (cmnx + cmxx) / 2 : axis == 1 ? (cmny + cmxy) / 2 : (cmnz + cmxz) / 2;
        int i0 = start, i1 = start + count - 1;
        while (i0 <= i1)
        {
            if (cen[3 * _tri[i0] + axis] < split) i0++;
            else { (_tri[i0], _tri[i1]) = (_tri[i1], _tri[i0]); i1--; }
        }
        int nl = i0 - start;
        if (nl == 0 || nl == count) nl = count / 2;   // all centroids equal: split by count
        int l = _nodes, r = _nodes + 1; _nodes += 2;
        _left[node] = l; _count[node] = 0;
        Build(l, start, nl, cen); Build(r, start + nl, count - nl, cen);
    }

    /// <summary>True when the segment o + d*t (0 &lt; t &lt; tmax) hits any triangle.</summary>
    public bool Occluded(Vector3 o, Vector3 d, float tmax)
    {
        if (_tri.Length == 0) return false;
        float ix = 1 / (MathF.Abs(d.X) < 1e-12f ? 1e-12f : d.X), iy = 1 / (MathF.Abs(d.Y) < 1e-12f ? 1e-12f : d.Y), iz = 1 / (MathF.Abs(d.Z) < 1e-12f ? 1e-12f : d.Z);
        Span<int> stack = stackalloc int[256];
        int sp = 0; stack[sp++] = 0;
        while (sp > 0)
        {
            int nd = stack[--sp];
            float t1 = (_bmin[3 * nd] - o.X) * ix, t2 = (_bmax[3 * nd] - o.X) * ix;
            float tmin = MathF.Min(t1, t2), tmx = MathF.Max(t1, t2);
            t1 = (_bmin[3 * nd + 1] - o.Y) * iy; t2 = (_bmax[3 * nd + 1] - o.Y) * iy;
            tmin = MathF.Max(tmin, MathF.Min(t1, t2)); tmx = MathF.Min(tmx, MathF.Max(t1, t2));
            t1 = (_bmin[3 * nd + 2] - o.Z) * iz; t2 = (_bmax[3 * nd + 2] - o.Z) * iz;
            tmin = MathF.Max(tmin, MathF.Min(t1, t2)); tmx = MathF.Min(tmx, MathF.Max(t1, t2));
            if (tmx < MathF.Max(tmin, 0) || tmin > tmax) continue;
            if (_left[nd] < 0)
            {
                for (int i = _start[nd]; i < _start[nd] + _count[nd]; i++)
                    if (Hit(_tri[i], o, d, tmax)) return true;
            }
            else if (sp + 2 <= stack.Length) { stack[sp++] = _left[nd]; stack[sp++] = _left[nd] + 1; }
        }
        return false;
    }

    /// <summary>True when the ray o + d*t reaches the sky: it hits nothing, or its nearest hit is a sky face.</summary>
    public bool SeesSky(Vector3 o, Vector3 d)
    {
        if (_tri.Length == 0) return true;
        float ix = 1 / (MathF.Abs(d.X) < 1e-12f ? 1e-12f : d.X), iy = 1 / (MathF.Abs(d.Y) < 1e-12f ? 1e-12f : d.Y), iz = 1 / (MathF.Abs(d.Z) < 1e-12f ? 1e-12f : d.Z);
        Span<int> stack = stackalloc int[256];
        int sp = 0; stack[sp++] = 0;
        float best = 1e9f; int bestTri = -1;
        while (sp > 0)
        {
            int nd = stack[--sp];
            float t1 = (_bmin[3 * nd] - o.X) * ix, t2 = (_bmax[3 * nd] - o.X) * ix;
            float tmin = MathF.Min(t1, t2), tmx = MathF.Max(t1, t2);
            t1 = (_bmin[3 * nd + 1] - o.Y) * iy; t2 = (_bmax[3 * nd + 1] - o.Y) * iy;
            tmin = MathF.Max(tmin, MathF.Min(t1, t2)); tmx = MathF.Min(tmx, MathF.Max(t1, t2));
            t1 = (_bmin[3 * nd + 2] - o.Z) * iz; t2 = (_bmax[3 * nd + 2] - o.Z) * iz;
            tmin = MathF.Max(tmin, MathF.Min(t1, t2)); tmx = MathF.Min(tmx, MathF.Max(t1, t2));
            if (tmx < MathF.Max(tmin, 0) || tmin > best) continue;
            if (_left[nd] < 0)
            {
                for (int i = _start[nd]; i < _start[nd] + _count[nd]; i++)
                {
                    float t = HitT(_tri[i], o, d);
                    if (t > 0 && t < best) { best = t; bestTri = _tri[i]; }
                }
            }
            else if (sp + 2 <= stack.Length) { stack[sp++] = _left[nd]; stack[sp++] = _left[nd] + 1; }
        }
        return bestTri < 0 || _sky[bestTri];
    }

    bool Hit(int t, Vector3 o, Vector3 d, float tmax) { float h = HitT(t, o, d); return h > 0 && h < tmax; }

    float HitT(int t, Vector3 o, Vector3 d)
    {
        int b = 9 * t;
        var a = new Vector3(_v[b], _v[b + 1], _v[b + 2]);
        var e1 = new Vector3(_v[b + 3], _v[b + 4], _v[b + 5]) - a;
        var e2 = new Vector3(_v[b + 6], _v[b + 7], _v[b + 8]) - a;
        var p = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-9f) return -1;
        float inv = 1 / det;
        var s = o - a;
        float u = Vector3.Dot(s, p) * inv;
        if (u < 0 || u > 1) return -1;
        var q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(d, q) * inv;
        if (v < 0 || u + v > 1) return -1;
        float t2 = Vector3.Dot(e2, q) * inv;
        return t2 > 1e-3f ? t2 : -1;
    }
}

/// <summary>
/// Bakes the lights of a Source map into lightmap pages (vrad-like direct light: point and spot lights with their
/// falloff, the sun of light_environment and its sky ambient over the hemisphere, ray-traced shadows against the world
/// brushes, plus a crude bounce term). One rectangle of luxels per face, packed per model chunk into square pages.
/// </summary>
public sealed class LightBaker
{
    readonly LightBakeOptions _o;
    readonly TriangleBvh _bvh;
    readonly List<BakeLight> _lights;
    readonly Vector3 _toSun, _sunColour, _skyColour;
    readonly bool _hasSun;
    readonly Vector3 _gameSunDir, _gameSunColour;
    public int Rays;

    public LightBaker(LightBakeOptions o, TriangleBvh occluders, List<BakeLight> lights, Vector3? toSun, Vector3 sunColour, Vector3 skyColour, Vector3 gameSunDir, Vector3 gameSunColour)
    {
        _o = o; _bvh = occluders; _lights = lights;
        _hasSun = toSun != null && o.Sun; _toSun = toSun is Vector3 s ? Vector3.Normalize(s) : Vector3.UnitZ;
        _sunColour = sunColour; _skyColour = o.Sky ? skyColour : Vector3.Zero;
        _gameSunDir = gameSunDir; _gameSunColour = gameSunColour;
    }

    /// <summary>Lights of the map (vrad semantics) in Source coordinates.</summary>
    public static List<BakeLight> LightsFrom(VmfMap map, LightBakeOptions o, Func<VmfEntity, bool> keep, List<string> notes)
    {
        var res = new List<BakeLight>();
        int skipped = 0;
        foreach (var e in map.Entities)
        {
            bool spot = e.ClassName.Equals("light_spot", StringComparison.OrdinalIgnoreCase);
            bool point = e.ClassName.Equals("light", StringComparison.OrdinalIgnoreCase);
            if (!(spot && o.SpotLights) && !(point && o.PointLights)) continue;
            if (!keep(e) || e.Origin == null) { skipped++; continue; }
            var org = e.Origin.Value;
            var (r, g, b, br) = VmfMap.Colour(e.Get("_light", "255 255 255 200"));
            var col = new Vector3(Lin(r), Lin(g), Lin(b)) * (float)(br / 255.0);
            double F(string k, double d) => double.TryParse(e.Get(k), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : d;
            float c = (float)F("_constant_attn", 0), l = (float)F("_linear_attn", 0), q = (float)F("_quadratic_attn", 0);
            double fifty = F("_fifty_percent_distance", 0), zero = F("_zero_percent_distance", 0);
            if (fifty > 0)
            {
                // vrad: falloff with 50% at fifty and (nearly) 0 at zero: modelled as quadratic through the 50% point
                c = 0; l = 0; q = (float)(1.0 / (fifty * fifty));
                col *= 1;   // brightness is the value at the light (scaled below to 1 at distance 0..fifty)
            }
            else if (c == 0 && l == 0 && q == 0) q = 1;
            float scale = fifty > 0 ? 1 : c + 100 * l + 10000 * q;   // vrad: intensity is the value at 100 units
            var bl = new BakeLight { Source = $"{e.ClassName} #{e.Id}", Pos = V(org), Colour = col * scale, C = c, L = l, Q = q, Cutoff = (float)F("_distance", 0) };
            if (fifty > 0) { bl.C = 1; bl.Q = (float)(1.0 / (fifty * fifty)); bl.Colour = col; if (zero > fifty) bl.Cutoff = (float)zero; }
            if (spot)
            {
                double yaw = e.Angles.Y;
                // vrad SetupLightNormalFromProps: the "pitch" key when non-zero, else the angles' pitch; direction
                // (cos yaw cos pitch, sin yaw cos pitch, sin pitch) (pitch -90 = straight down)
                double pitch = double.TryParse(e.Get("pitch"), NumberStyles.Float, CultureInfo.InvariantCulture, out double pk) && pk != 0 ? pk : e.Angles.X;
                double py = pitch * Math.PI / 180, yy = yaw * Math.PI / 180;
                bl.Dir = Vector3.Normalize(new Vector3((float)(Math.Cos(yy) * Math.Cos(py)), (float)(Math.Sin(yy) * Math.Cos(py)), (float)Math.Sin(py)));
                bl.Spot = true;
                double inner = F("_inner_cone", 30), outer = F("_cone", 45);
                if (inner > outer) inner = outer;
                bl.CosInner = (float)Math.Cos(inner * Math.PI / 180); bl.CosOuter = (float)Math.Cos(outer * Math.PI / 180);
                bl.Exponent = (float)F("_exponent", 1);
            }
            float maxc = MathF.Max(bl.Colour.X, MathF.Max(bl.Colour.Y, bl.Colour.Z));
            // distance where the light falls below 1/500
            const float Thr = 0.002f;
            float rad;
            if (bl.Q > 0) rad = MathF.Sqrt(maxc / (bl.Q * Thr));
            else if (bl.L > 0) rad = maxc / (bl.L * Thr);
            else rad = 1e5f;
            if (bl.Cutoff > 0) rad = MathF.Min(rad, bl.Cutoff);
            bl.Radius = MathF.Min(rad, 1e5f);
            if (maxc > 0) res.Add(bl);
        }
        notes.Add($"lights: {res.Count(x => !x.Spot)} point, {res.Count(x => x.Spot)} spot{(skipped > 0 ? $" ({skipped} outside the map / 3D skybox skipped)" : "")}");
        return res;
    }

    public static float Lin(double c) => (float)Math.Pow(Math.Clamp(c, 0, 255) / 255.0, 2.2);
    static Vector3 V(DVec3 p) => new((float)p.X, (float)p.Y, (float)p.Z);

    /// <summary>Linear light (RGB) at a point with a normal (Source coordinates).</summary>
    public Vector3 Direct(Vector3 p, Vector3 n, int seed)
    {
        var sum = Vector3.Zero;
        var o = p + n * 1f;
        foreach (var l in _lights)
        {
            var dl = l.Pos - p;
            float d2 = dl.LengthSquared();
            if (d2 > l.Radius * l.Radius) continue;
            float d = MathF.Sqrt(d2);
            if (d < 1e-3f) continue;
            var L = dl / d;
            float ndl = Vector3.Dot(n, L);
            if (ndl <= 0) continue;
            float att = 1 / MathF.Max(1e-6f, l.C + l.L * d + l.Q * d2);
            if (l.Spot)
            {
                float dot = -Vector3.Dot(L, l.Dir);
                if (dot <= l.CosOuter) continue;
                float sf = MathF.Pow(MathF.Max(dot, 0), l.Exponent);
                if (dot < l.CosInner) sf *= (dot - l.CosOuter) / MathF.Max(1e-4f, l.CosInner - l.CosOuter);
                att *= sf;
            }
            var contrib = l.Colour * (att * ndl);
            if (contrib.X + contrib.Y + contrib.Z < 1e-4f) continue;
            Interlocked.Increment(ref Rays);
            if (_bvh.Occluded(o, L, d - 1f)) continue;
            sum += contrib;
        }
        if (_hasSun)
        {
            float ndl = Vector3.Dot(n, _toSun);
            if (ndl > 0) { Interlocked.Increment(ref Rays); if (_bvh.SeesSky(o, _toSun)) sum += _sunColour * ndl; }
        }
        if (_skyColour != Vector3.Zero && _o.SkySamples > 0)
        {
            // cosine-weighted hemisphere: fraction of rays that leave the map
            var t1 = Vector3.Normalize(Vector3.Cross(n, MathF.Abs(n.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX));
            var t2 = Vector3.Cross(n, t1);
            int k = _o.SkySamples, open = 0;
            uint h = (uint)seed * 2654435761u;
            float rot = (h >> 8) / 16777216f * MathF.PI * 2;
            for (int i = 0; i < k; i++)
            {
                float u = (i + 0.5f) / k, phi = rot + i * 2.39996323f;   // golden-angle spiral
                float r = MathF.Sqrt(u), z = MathF.Sqrt(1 - u);
                var dir = t1 * (r * MathF.Cos(phi)) + t2 * (r * MathF.Sin(phi)) + n * z;
                if (_bvh.SeesSky(o, dir)) open++;
            }
            Interlocked.Add(ref Rays, k);
            sum += _skyColour * ((float)open / k);
        }
        return sum;
    }

    /// <summary>Lightmap value: exposure * gamma(light) divided by the game's own light for this normal (game axes).</summary>
    public Vector3 Encode(Vector3 linear, Vector3 gameNormal)
    {
        float inv = 1 / MathF.Max(0.5f, _o.Gamma);
        linear *= _o.Overbright;
        var disp = new Vector3(MathF.Pow(MathF.Max(0, linear.X), inv), MathF.Pow(MathF.Max(0, linear.Y), inv), MathF.Pow(MathF.Max(0, linear.Z), inv)) * _o.Exposure;
        var game = new Vector3(_o.GameAmbient * (1.107f + 0.519f * gameNormal.Y)) + _gameSunColour * (_o.GameSun * MathF.Max(0, Vector3.Dot(gameNormal, _gameSunDir)));
        return Vector3.Clamp(disp / Vector3.Max(game, new Vector3(0.05f)), Vector3.Zero, Vector3.One);
    }
}
