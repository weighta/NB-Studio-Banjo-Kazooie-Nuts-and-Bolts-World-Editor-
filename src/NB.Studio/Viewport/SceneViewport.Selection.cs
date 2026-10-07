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
            if (o.Kind == SceneObjectKind.Marker) _linesVersion++;
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
        foreach (var o in Scene.Objects)
        {
            if (!o.Visible || o.Kind == SceneObjectKind.Terrain) continue;
            if (o.Kind == SceneObjectKind.Scenery && (!ShowScenery || IsHidden(o))) continue;
            if (o.Kind == SceneObjectKind.Marker && !ShowMarkers && (o.Model == null || !_showObjects)) continue;
            var (c, _) = WorldBounds(o);
            if (ToScreen(c) is { } s && s.X >= r.Left && s.X <= r.Right && s.Y >= r.Top && s.Y <= r.Bottom && Vector3.Dot(c - _camPos, Forward()) > 0) res.Add(o);
        }
        return res;
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
