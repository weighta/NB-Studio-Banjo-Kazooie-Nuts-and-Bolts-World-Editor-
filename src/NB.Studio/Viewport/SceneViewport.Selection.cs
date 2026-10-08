using System.Numerics;
using NB.Core.World;

namespace NB.Studio.Viewport;

/// <summary>
/// Several selected objects (Ctrl / Shift + click toggles, B or Ctrl + left-drag selects a rectangle, Ctrl+A everything
/// shown): <see cref="Selected"/> is the primary one (gizmo, Properties), the others follow every transform — the same
/// world-space change (moving, turning and scaling about the primary object's origin; markers keep their scale). And
/// hiding in the 3D view for this session (H hides the selection, U or Alt+H shows everything again).
/// </summary>
public sealed partial class SceneViewport
{
    readonly List<SceneObject> _extra = new();
    /// <summary>Every selected object, the primary one first.</summary>
    public IReadOnlyList<SceneObject> SelectedObjects => Selected == null ? Array.Empty<SceneObject>() : _extra.Prepend(Selected).ToList();
    public bool IsSelected(SceneObject o) => o == Selected || _extra.Contains(o);
    /// <summary>The set of selected objects changed (primary or others).</summary>
    public event Action<IReadOnlyList<SceneObject>>? SelectionSetChanged;
    /// <summary>A confirmed transform of the selection: every object that changed with its transform before.</summary>
    public event Action<IReadOnlyList<(SceneObject Obj, Matrix4x4 Before)>>? ObjectsEdited;
    /// <summary>H / U changed which objects are shown (number hidden by H now).</summary>
    public event Action<int>? HiddenChanged;

    /// <summary>Selects several objects (<paramref name="primary"/> gets the gizmo; null: the first).</summary>
    public void SelectMany(IEnumerable<SceneObject> objs, SceneObject? primary = null)
    {
        var list = objs.Where(o => o.Kind != SceneObjectKind.Terrain).Distinct().ToList();
        CancelTransform();
        primary ??= list.FirstOrDefault();
        _extra.Clear(); _extra.AddRange(list.Where(o => o != primary));
        Selected = primary;
        SelectionChanged?.Invoke(primary);
        SelectionSetChanged?.Invoke(SelectedObjects);
        _gl.Invalidate();
    }

    /// <summary>Ctrl / Shift + click: adds the object, or removes it when it is selected.</summary>
    public void ToggleSelect(SceneObject o)
    {
        if (o.Kind == SceneObjectKind.Terrain) return;
        var all = SelectedObjects.ToList();
        if (all.Contains(o)) { all.Remove(o); SelectMany(all, all.FirstOrDefault()); }
        else { all.Add(o); SelectMany(all, o); }
    }

    // ------------------------------------------------------------------ the box around a multi-selection

    /// <summary>World-space box around every selected object (their own boxes, as placed); null without a selection.</summary>
    (Vector3 Min, Vector3 Max)? SelectionBox()
    {
        if (Selected == null) return null;
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var o in _extra.Prepend(Selected))
        {
            var a = o.BoundsMin; var b = o.BoundsMax;
            if (!(a.X <= b.X)) { a = b = Vector3.Zero; }
            for (int i = 0; i < 8; i++)
            {
                var p = Vector3.Transform(new Vector3((i & 1) != 0 ? b.X : a.X, (i & 2) != 0 ? b.Y : a.Y, (i & 4) != 0 ? b.Z : a.Z), o.Transform);
                mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p);
            }
        }
        return float.IsFinite(mn.X) && float.IsFinite(mx.X) ? (mn, mx) : null;
    }

    /// <summary>Where the gizmo sits: the primary object's origin, or the centre of the box around several objects.</summary>
    Vector3 GizmoPoint() => _extra.Count > 0 && SelectionBox() is { } b ? (b.Min + b.Max) / 2 : Selected?.Transform.Translation ?? Vector3.Zero;

    /// <summary>The mouse ray hits the box around the selection (several objects).</summary>
    bool HitsSelectionBox(Point p)
    {
        if (SelectionBox() is not { } b) return false;
        var (ro, rd) = Ray(p);
        return RayBox(ro, rd, b.Min, b.Max, out _);
    }

    static void AddAabb(List<(Vector3, Vector3, Vector3)> l, Vector3 a, Vector3 b, Vector3 c)
    {
        var pts = new Vector3[8];
        for (int i = 0; i < 8; i++) pts[i] = new Vector3((i & 1) != 0 ? b.X : a.X, (i & 2) != 0 ? b.Y : a.Y, (i & 4) != 0 ? b.Z : a.Z);
        int[] e = { 0, 1, 1, 3, 3, 2, 2, 0, 4, 5, 5, 7, 7, 6, 6, 4, 0, 4, 1, 5, 2, 6, 3, 7 };
        for (int i = 0; i < e.Length; i += 2) l.Add((pts[e[i]], pts[e[i + 1]], c));
    }

    /// <summary>A turn or scale of the primary object computed about its own origin (<paramref name="m"/>), made about
    /// <see cref="_xfPivot"/> instead (the centre of a multi-selection; the same for one object).</summary>
    Matrix4x4 AboutPivot(Matrix4x4 m, Matrix4x4 start)
    {
        var p = start.Translation;
        if (Vector3.DistanceSquared(p, _xfPivot) < 1e-10f) { m.Translation = p; return m; }
        if (!Matrix4x4.Invert(start, out var inv)) return m;
        var d0 = inv * m;   // the world-space change about p
        return start * Matrix4x4.CreateTranslation(p - _xfPivot) * d0 * Matrix4x4.CreateTranslation(_xfPivot - p);
    }

    // ------------------------------------------------------------------ transforms of the other selected objects

    List<(SceneObject O, Matrix4x4 Start)> _xfStarts = new();

    void BeginMulti() => _xfStarts = SelectedObjects.Select(o => (o, o.Transform)).ToList();

    /// <summary>The primary object's change (world space) applied to the others.</summary>
    void UpdateMulti()
    {
        if (_xfStarts.Count < 2 || Selected == null || !Matrix4x4.Invert(_xfStart, out var inv)) return;
        var x = inv * Selected.Transform;
        bool scales = !Matrix4x4.Decompose(x, out var sc, out _, out _) || MathF.Abs(sc.X - 1) + MathF.Abs(sc.Y - 1) + MathF.Abs(sc.Z - 1) > 1e-4f;
        foreach (var (o, start) in _xfStarts)
        {
            if (o == Selected) continue;
            if (ScaleLocked(o) && scales) { var m = start; m.Translation = Vector3.Transform(start.Translation, x); o.Transform = m; }
            else o.Transform = start * x;
            if (ActorMarkers.YawOnly(o)) o.Transform = ActorMarkers.Upright(o.Transform);   // characters in a turned group stay upright
            if (o.Kind == SceneObjectKind.Marker && !_movingLines.Contains(o)) _linesVersion++;
        }
    }

    void CancelMulti()
    {
        foreach (var (o, start) in _xfStarts) if (o != Selected) { o.Transform = start; if (o.Kind == SceneObjectKind.Marker) _linesVersion++; }
    }

    /// <summary>A typed change in Properties (the primary object from <paramref name="before"/> to now): the others follow.
    /// Returns every changed object with its transform before.</summary>
    public List<(SceneObject Obj, Matrix4x4 Before)> ApplyDeltaToOthers(Matrix4x4 before)
    {
        var res = new List<(SceneObject, Matrix4x4)>();
        if (Selected == null || !Matrix4x4.Invert(before, out var inv)) return res;
        var starts = _extra.Select(o => (o, o.Transform)).ToList();
        _xfStart = before; _xfStarts = starts.Prepend((Selected, before)).ToList();
        UpdateMulti();
        res.Add((Selected, before));
        foreach (var (o, s) in starts) if (o.Transform != s) res.Add((o, s));
        _xfStarts = new();
        _gl.Invalidate();
        return res;
    }

    // ------------------------------------------------------------------ hide / unhide (this session only)

    readonly HashSet<SceneObject> _hiddenByH = new();
    public int HiddenCount => _hiddenByH.Count(o => !o.Visible);

    /// <summary>H: hides the selected objects in the 3D view (not deleted, not saved).</summary>
    public void HideSelection()
    {
        var sel = SelectedObjects.Where(o => o.Kind != SceneObjectKind.Terrain).ToList();
        if (sel.Count == 0) return;
        foreach (var o in sel) { o.Visible = false; _hiddenByH.Add(o); }
        Select(null);
        _linesVersion++; _hudOv.Key = ""; _gl.Invalidate();
        HiddenChanged?.Invoke(HiddenCount);
    }

    /// <summary>U / Alt+H: shows every object again (also those unticked in the Scene list).</summary>
    public void UnhideAll()
    {
        if (Scene == null) return;
        foreach (var o in Scene.Objects) o.Visible = true;
        _hiddenByH.Clear();
        _linesVersion++; _gl.Invalidate();
        HiddenChanged?.Invoke(0);
    }

    readonly Renderer.Overlay _hiddenOv = new();

    void DrawHiddenNote(int W, int H)
    {
        int n = HiddenCount;
        if (n == 0) return;
        string text = $"{n} hidden — U to show";
        if (_hiddenOv.Key != text)
        {
            using var font = new Font("Segoe UI Semibold", 9f);
            using var probe = new Bitmap(1, 1); using var pg = Graphics.FromImage(probe);
            var sz = pg.MeasureString(text, font);
            using var bmp = new Bitmap((int)sz.Width + 16, (int)sz.Height + 6, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);
                using (var p = Rounded(new Rectangle(0, 0, bmp.Width - 1, bmp.Height - 1), 5)) using (var b = new SolidBrush(BarBg)) using (var e = new Pen(BarEdge)) { g.FillPath(b, p); g.DrawPath(e, p); }
                using var tb = new SolidBrush(BarText); g.DrawString(text, font, tb, 8, 3);
            }
            _r.UpdateOverlay(_hiddenOv, bmp, text);
        }
        _r.DrawOverlay(_hiddenOv, BarMargin, BarMargin, W, H);
    }

    // ------------------------------------------------------------------ rectangle select of objects

    bool _boxArmed, _rectObjects;

    /// <summary>B: the next left-drag selects a rectangle.</summary>
    public void ArmBoxSelect() { _boxArmed = true; Cursor = Cursors.Cross; _gl.Cursor = Cursors.Cross; }

    /// <summary>Objects whose centre projects inside the rectangle (shown and in front of the camera).</summary>
    List<SceneObject> ObjectsInRect(Rectangle r)
    {
        var res = new List<SceneObject>();
        if (Scene == null) return res;
        var vp = View() * Proj(); float W = _gl.Width, H = _gl.Height; var rf = (RectangleF)r;
        foreach (var o in Scene.Objects)
        {
            if (!o.Visible || o.Kind == SceneObjectKind.Terrain) continue;
            if (o.Kind == SceneObjectKind.Scenery && (!ShowScenery || IsHidden(o))) continue;
            if (o.Kind == SceneObjectKind.Marker && !ShowMarkers && (o.Model == null || !_showObjects)) continue;
            if (!_showCameras && CameraPoints.Is(o)) continue;
            bool asModel = o.Model != null && (o.Kind != SceneObjectKind.Marker || _showObjects);
            // the box first: off the rectangle -> no; entirely inside -> yes; else the drawn triangles (any touching)
            var m4 = o.Transform * vp;
            var bb = BoxOnScreen(o.BoundsMin, o.BoundsMax, m4, W, H);
            if (bb is { } b)
            {
                if (!b.IntersectsWith(rf)) continue;
                if (rf.Contains(b)) { res.Add(o); continue; }
            }
            bool hit = asModel ? ModelInRect(ModelFor(o)!, m4, rf, W, H) : BoxInRect(o.BoundsMin, o.BoundsMax, m4, rf, W, H);
            if (!hit && asModel)
                foreach (var (cm, cl) in o.Children)
                    if (ModelInRect(cm, cl * m4, rf, W, H)) { hit = true; break; }
            if (hit) res.Add(o);
        }
        return res;
    }

    /// <summary>Screen rectangle of a box's corners, or null when part of it is behind the camera.</summary>
    static RectangleF? BoxOnScreen(Vector3 mn, Vector3 mx, Matrix4x4 m4, float W, float H)
    {
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        for (int i = 0; i < 8; i++)
        {
            var c = Vector4.Transform(new Vector4((i & 1) == 0 ? mn.X : mx.X, (i & 2) == 0 ? mn.Y : mx.Y, (i & 4) == 0 ? mn.Z : mx.Z, 1), m4);
            if (c.W <= 1e-3f) return null;
            var s = ClipToScreen(c, W, H);
            x0 = MathF.Min(x0, s.X); y0 = MathF.Min(y0, s.Y); x1 = MathF.Max(x1, s.X); y1 = MathF.Max(y1, s.Y);
        }
        return RectangleF.FromLTRB(x0, y0, x1, y1);
    }

    static bool ModelInRect(NB.Core.Models.ModelAsset model, Matrix4x4 m4, RectangleF r, float W, float H)
    {
        foreach (var dr in model.Draws)
        {
            var P = dr.Positions; var I = dr.Indices;
            var clip = new Vector4[P.Length];
            for (int i = 0; i < P.Length; i++) clip[i] = Vector4.Transform(new Vector4(P[i], 1), m4);
            for (int k = 0; k + 2 < I.Length; k += 3)
            {
                int a = I[k], b = I[k + 1], c = I[k + 2];
                if (a >= P.Length || b >= P.Length || c >= P.Length) continue;
                if (TriInRect(clip[a], clip[b], clip[c], r, W, H)) return true;
            }
        }
        return false;
    }

    static readonly int[] BoxTris = { 0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3 };

    static bool BoxInRect(Vector3 mn, Vector3 mx, Matrix4x4 m4, RectangleF r, float W, float H)
    {
        var c = new Vector4[8];
        for (int i = 0; i < 8; i++) c[i] = Vector4.Transform(new Vector4((i & 1) == 0 ? mn.X : mx.X, (i & 2) == 0 ? mn.Y : mx.Y, (i & 4) == 0 ? mn.Z : mx.Z, 1), m4);
        for (int k = 0; k < BoxTris.Length; k += 3) if (TriInRect(c[BoxTris[k]], c[BoxTris[k + 1]], c[BoxTris[k + 2]], r, W, H)) return true;
        return false;
    }

    static Vector2 ClipToScreen(Vector4 c, float W, float H) => new((c.X / c.W * 0.5f + 0.5f) * W, (0.5f - c.Y / c.W * 0.5f) * H);

    /// <summary>
    /// A triangle (clip-space corners) touches the screen rectangle: clipped at the near plane, then any corner inside the
    /// rectangle, any edge crossing it, or the rectangle lying inside the triangle counts.
    /// </summary>
    static bool TriInRect(Vector4 c0, Vector4 c1, Vector4 c2, RectangleF r, float W, float H)
    {
        const float E = 1e-3f;
        if (c0.W <= E && c1.W <= E && c2.W <= E) return false;
        Span<Vector2> p = stackalloc Vector2[4]; int n = 0;
        for (int i = 0; i < 3; i++)
        {
            var a = i == 0 ? c0 : i == 1 ? c1 : c2; var b = i == 0 ? c1 : i == 1 ? c2 : c0;
            bool ain = a.W > E, bin = b.W > E;
            if (ain) p[n++] = ClipToScreen(a, W, H);
            if (ain != bin) { float t = (E - a.W) / (b.W - a.W); p[n++] = ClipToScreen(a + (b - a) * t, W, H); }
        }
        if (n < 2) return false;
        float x0 = p[0].X, x1 = p[0].X, y0 = p[0].Y, y1 = p[0].Y;
        for (int i = 1; i < n; i++) { x0 = MathF.Min(x0, p[i].X); x1 = MathF.Max(x1, p[i].X); y0 = MathF.Min(y0, p[i].Y); y1 = MathF.Max(y1, p[i].Y); }
        if (x1 < r.Left || x0 > r.Right || y1 < r.Top || y0 > r.Bottom) return false;
        for (int i = 0; i < n; i++) if (p[i].X >= r.Left && p[i].X <= r.Right && p[i].Y >= r.Top && p[i].Y <= r.Bottom) return true;
        for (int i = 0; i < n; i++) if (SegInRect(p[i], p[(i + 1) % n], r)) return true;
        if (n < 3) return false;
        // no corner inside and no edge crossing: overlapping only when the rectangle lies inside the polygon
        var q = new Vector2(r.Left + r.Width / 2, r.Top + r.Height / 2);
        int sgn = 0;
        for (int i = 0; i < n; i++)
        {
            var e = p[(i + 1) % n] - p[i]; float cr = e.X * (q.Y - p[i].Y) - e.Y * (q.X - p[i].X);
            int s = cr > 0 ? 1 : cr < 0 ? -1 : 0;
            if (s == 0) continue;
            if (sgn == 0) sgn = s; else if (s != sgn) return false;
        }
        return sgn != 0;
    }

    /// <summary>A screen segment crosses or touches the rectangle (Liang-Barsky).</summary>
    static bool SegInRect(Vector2 a, Vector2 b, RectangleF r)
    {
        float t0 = 0, t1 = 1, dx = b.X - a.X, dy = b.Y - a.Y;
        bool Clip(float pp, float qq)
        {
            if (MathF.Abs(pp) < 1e-9f) return qq >= 0;
            float t = qq / pp;
            if (pp < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
            else { if (t < t0) return false; if (t < t1) t1 = t; }
            return true;
        }
        return Clip(-dx, a.X - r.Left) && Clip(dx, r.Right - a.X) && Clip(-dy, a.Y - r.Top) && Clip(dy, r.Bottom - a.Y);
    }

    bool ObjectRectDown(MouseEventArgs e)
    {
        if (_collMode || e.Button != MouseButtons.Left) return false;
        if (!_boxArmed && (ModifierKeys & Keys.Control) == 0) return false;
        _rectFrom = e.Location; _rectTo = e.Location; _rectObjects = true;
        return true;
    }

    bool ObjectRectUp(MouseEventArgs e)
    {
        if (!_rectObjects || _rectFrom is not { } a || e.Button != MouseButtons.Left) return false;
        _rectFrom = null; _rectObjects = false; _boxArmed = false; Cursor = Cursors.Default; _gl.Cursor = Cursors.Default;
        var r = Rectangle.FromLTRB(Math.Min(a.X, e.X), Math.Min(a.Y, e.Y), Math.Max(a.X, e.X), Math.Max(a.Y, e.Y));
        bool add = (ModifierKeys & (Keys.Shift | Keys.Control)) != 0;
        if (r.Width < 4 && r.Height < 4)
        {
            // Ctrl + click: toggle the object under the mouse
            if (Pick(e.Location).Obj is { } o) ToggleSelect(o);
        }
        else
        {
            var found = ObjectsInRect(r);
            SelectMany(add ? SelectedObjects.Concat(found) : found);
        }
        _gl.Invalidate();
        return true;
    }
}
