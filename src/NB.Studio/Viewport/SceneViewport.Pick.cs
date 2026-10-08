using System.Numerics;
using NB.Core.World;

namespace NB.Studio.Viewport;

/// <summary>
/// Click tolerance for objects placed by markers (characters, props, vehicles): far away a character is a few pixels
/// wide, and a click just beside an arm or a leg hit the ground behind it. When the clicked pixel itself hits no marker
/// object, rings of pixels around it (3 and 6 pixels out) are tried for marker objects in front of what the click hit.
/// </summary>
public sealed partial class SceneViewport
{
    static readonly int[] NearRadii = { 3, 6 };

    /// <summary>The nearest marker object within a few pixels of <paramref name="p"/> that is closer than
    /// <paramref name="behind"/> (what the pixel itself hit); (null, 0) when there is none.</summary>
    (SceneObject? Obj, float Dist) PickNear(Point p, float behind)
    {
        foreach (int r in NearRadii)
        {
            SceneObject? best = null; float bestT = behind;
            for (int k = 0; k < 8; k++)
            {
                double a = k * Math.PI / 4;
                var q = new Point(p.X + (int)Math.Round(r * Math.Cos(a)), p.Y + (int)Math.Round(r * Math.Sin(a)));
                var (o, t) = PickRay(q, markersOnly: true, bestT);
                if (o != null && t < bestT) { best = o; bestT = t; }
            }
            if (best != null) return (best, bestT);
        }
        return (null, 0);
    }

    /// <summary>Script check (--pick-audit NAME): every 2nd pixel of the object's screen box. "Shows" = the pixel's ray
    /// hits the object's mesh as drawn (posed); counts how many of those a click selects the object now, how many the
    /// earlier pick (bind-pose mesh inside the bind-pose box) hit, and how many pixels just beside it (up to 6 px off
    /// the drawn mesh) select it.</summary>
    void PickAudit(string arg, Action<string> log)
    {
        // NAME[|map.png]: the map is the view with the clicks marked (green: selects it now and before, orange: only
        // now, cyan: beside the mesh, selects it now; red: shows it, still not selected)
        var parts = arg.Split('|'); string query = parts[0]; string? map = parts.Length > 1 ? parts[1] : null;
        using var img = map != null ? Capture() : null;
        var o = Scene?.Objects.FirstOrDefault(x => x.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        if (o == null || !Matrix4x4.Invert(o.Transform, out var inv)) { log($"script: pick audit {query}: no such object"); return; }
        float W = _gl.Width, H = _gl.Height;
        if (BoxOnScreen(o.BoundsMin, o.BoundsMax, o.Transform * View() * Proj(), W, H) is not { } r) { log($"script: pick audit {o.Name}: off screen"); return; }
        var (bmn, bmx) = _posed.TryGetValue(o, out var ps) ? (ps.BindMin, ps.BindMax) : (o.BoundsMin, o.BoundsMax);
        var drawn = ModelFor(o)!;
        int x0 = Math.Max(0, (int)r.Left - 8), x1 = Math.Min((int)W - 1, (int)r.Right + 8), y0 = Math.Max(0, (int)r.Top - 8), y1 = Math.Min((int)H - 1, (int)r.Bottom + 8);
        var shows = new HashSet<(int, int)>();
        for (int y = y0; y <= y1; y += 2) for (int x = x0; x <= x1; x += 2)
        {
            var (ro, rd) = Ray(new Point(x, y));
            var lo = Vector3.Transform(ro, inv); var ld = Vector3.TransformNormal(rd, inv);
            bool hit = RayMesh(lo, ld, drawn, float.MaxValue) < float.MaxValue;
            foreach (var (cm, cl) in o.Children)
                if (!hit && Matrix4x4.Invert(cl, out var ci)) hit = RayMesh(Vector3.Transform(lo, ci), Vector3.TransformNormal(ld, ci), cm, float.MaxValue) < float.MaxValue;
            if (hit) shows.Add((x, y));
        }
        int now = 0, old = 0, near = 0, nearOk = 0;
        for (int y = y0; y <= y1; y += 2) for (int x = x0; x <= x1; x += 2)
        {
            bool s = shows.Contains((x, y));
            if (s)
            {
                bool n1 = Pick(new Point(x, y)).Obj == o; if (n1) now++;
                var (ro, rd) = Ray(new Point(x, y)); var lo = Vector3.Transform(ro, inv); var ld = Vector3.TransformNormal(rd, inv);
                bool o1 = RayBox(lo, ld, bmn, bmx, out _) && RayMesh(lo, ld, o.Model!, float.MaxValue) < float.MaxValue;
                foreach (var (cm, cl) in o.Children)
                    if (!o1 && RayBox(lo, ld, bmn, bmx, out _) && Matrix4x4.Invert(cl, out var ci)) o1 = RayMesh(Vector3.Transform(lo, ci), Vector3.TransformNormal(ld, ci), cm, float.MaxValue) < float.MaxValue;
                if (o1) old++;
                Mark(x, y, !n1 ? Color.Red : o1 ? Color.LimeGreen : Color.Orange);
            }
            else
            {
                bool close = false;
                for (int dy = -6; dy <= 6 && !close; dy += 2) for (int dx = -6; dx <= 6 && !close; dx += 2) close = shows.Contains((x + dx, y + dy));
                if (!close) continue;
                near++; if (Pick(new Point(x, y)).Obj == o) { nearOk++; Mark(x, y, Color.Cyan); }
            }
        }
        if (img != null && map != null) img.Save(map);
        void Mark(int x, int y, Color c) { if (img == null) return; for (int j = 0; j < 2; j++) for (int i = 0; i < 2; i++) if (x + i < img.Width && y + j < img.Height) img.SetPixel(x + i, y + j, c); }
        log($"script: pick audit {o.Name}: screen box {r.Width:0}x{r.Height:0} px; {shows.Count} sample pixels show it; a click there selects it now {now} ({100.0 * now / Math.Max(1, shows.Count):0.#}%), before (bind-pose mesh and box) {old} ({100.0 * old / Math.Max(1, shows.Count):0.#}%); {near} pixels beside it (up to 6 px): {nearOk} select it");
    }
}
