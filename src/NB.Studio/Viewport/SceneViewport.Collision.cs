using System.Numerics;
using NB.Core.Havok;
using NB.Core.World;

namespace NB.Studio.Viewport;

/// <summary>
/// Collision editing in the 3D view (Edit Collision mode): clicks pick collision triangles instead of objects (ray against
/// the decoded Havok collision of every object, as drawn by the Collision overlay), a left-drag selects every collision
/// triangle inside the rectangle, the selection is drawn in orange on top. What is selected and what an edit does is
/// decided by MainForm (CollisionSoup per Havok asset); the view only picks, draws and shows edited assets
/// (<see cref="CollisionOverride"/>).
/// </summary>
public sealed partial class SceneViewport
{
    /// <summary>One collision triangle under the mouse: the object whose collision it is, its Havok asset, the placement of
    /// the asset in the world (asset space → world), and the triangle index in the asset (decode order, as CollisionSoup).</summary>
    public readonly record struct CollisionHit(SceneObject Obj, string Asset, Matrix4x4 ToWorld, int Tri, Vector3 Point, float Distance);

    bool _collMode;
    /// <summary>Edit Collision: left click / drag select collision triangles (the Collision overlay is switched on).</summary>
    public bool CollisionMode
    {
        get => _collMode;
        set
        {
            if (_collMode == value) return;
            _collMode = value;
            if (value) ShowCollision = true;
            CollisionModeChanged?.Invoke(value);
            _gl.Invalidate();
        }
    }
    public event Action<bool>? CollisionModeChanged;
    /// <summary>Click in Edit Collision mode: the hit (null: nothing), Shift and Alt held.</summary>
    public event Action<CollisionHit?, bool, bool>? CollisionPicked;
    /// <summary>Rectangle drag in Edit Collision mode: the triangles inside (per object and asset), Shift held.</summary>
    public event Action<List<(SceneObject Obj, string Asset, Matrix4x4 ToWorld, List<int> Tris)>, bool>? CollisionRectSelected;

    /// <summary>Edited assets: the mesh to show and pick instead of the decoded one (null: not edited).</summary>
    public Func<string, CollisionMesh?>? CollisionOverride;
    /// <summary>The selected triangles per asset (drawn orange).</summary>
    public Func<IReadOnlyDictionary<string, HashSet<int>>>? CollisionSelection;
    /// <summary>While the selection proxy is being moved (G / R / T, gizmo): the world-space change to preview, for the
    /// object it was picked on.</summary>
    public Func<(SceneObject Owner, Matrix4x4 Delta)?>? CollisionPreview;

    readonly Dictionary<string, int> _collVer = new();
    int _collSelVer;
    Renderer.LineBatch? _collSelBatch; string _collSelKey = "";

    /// <summary>An edited asset changed: its wireframes are rebuilt.</summary>
    public void InvalidateCollisionAsset(string asset)
    {
        _collVer[asset] = _collVer.GetValueOrDefault(asset) + 1;
        foreach (var k in _collision.Keys.Where(k => k.EndsWith("|" + asset)).ToList()) { if (_ready) _r.DeleteLineBatch(_collision[k]); _collision.Remove(k); }
        _collSelVer++;
        _gl.Invalidate();
    }

    /// <summary>The selection changed: its highlight is rebuilt.</summary>
    public void InvalidateCollisionSelection() { _collSelVer++; _gl.Invalidate(); }

    /// <summary>The collision meshes of an object, edited ones replaced (asset, meshes, placement in the object).</summary>
    List<(string Asset, List<CollisionMesh> Meshes, Matrix4x4 Local)> CollisionParts(SceneObject o)
    {
        var res = new List<(string, List<CollisionMesh>, Matrix4x4)>();
        if (_allColl == null || !_allColl.TryGetValue(o, out var parts)) return res;
        foreach (var (asset, meshes, local) in parts)
            res.Add(CollisionOverride?.Invoke(asset) is { } ov ? (asset, new List<CollisionMesh> { ov }, local) : (asset, meshes, local));
        return res;
    }

    /// <summary>Every object with collision and its parts (for MainForm: which objects use an asset).</summary>
    public IEnumerable<(SceneObject Obj, string Asset, Matrix4x4 ToWorld)> CollisionInstances()
    {
        if (_allColl == null) yield break;
        foreach (var (o, parts) in _allColl)
            foreach (var (asset, _, local) in parts) yield return (o, asset, local * o.Transform);
    }

    bool CollisionVisible(SceneObject o) => o.Visible && !(o.Kind == SceneObjectKind.Scenery && IsHidden(o));

    /// <summary>The nearest collision triangle under a view pixel (null: none, or the collision is still being decoded).</summary>
    public CollisionHit? PickCollision(Point p)
    {
        if (_allColl == null) return null;
        var (ro, rd) = Ray(p);
        CollisionHit? best = null; float bestT = float.MaxValue;
        foreach (var o in _allColl.Keys)
        {
            if (!CollisionVisible(o)) continue;
            foreach (var (asset, meshes, local) in CollisionParts(o))
            {
                var toWorld = local * o.Transform;
                if (!Matrix4x4.Invert(toWorld, out var inv)) continue;
                var lo = Vector3.Transform(ro, inv); var ld = Vector3.TransformNormal(rd, inv);
                int baseTri = 0;
                foreach (var m in meshes)
                {
                    var P = m.Positions; var I = m.Triangles;
                    for (int k = 0; k + 2 < I.Count; k += 3)
                    {
                        var a = P[I[k]]; var e1 = P[I[k + 1]] - a; var e2 = P[I[k + 2]] - a;
                        var pv = Vector3.Cross(ld, e2); float det = Vector3.Dot(e1, pv);
                        if (MathF.Abs(det) < 1e-12f) continue;
                        float inv2 = 1 / det; var tv = lo - a;
                        float u = Vector3.Dot(tv, pv) * inv2; if (u < 0 || u > 1) continue;
                        var qv = Vector3.Cross(tv, e1); float v = Vector3.Dot(ld, qv) * inv2; if (v < 0 || u + v > 1) continue;
                        float t = Vector3.Dot(e2, qv) * inv2;
                        if (t <= 0) continue;
                        var wp = Vector3.Transform(lo + ld * t, toWorld); float dist = Vector3.Distance(wp, ro);
                        if (dist < bestT) { bestT = dist; best = new CollisionHit(o, asset, toWorld, baseTri + k / 3, wp, dist); }
                    }
                    baseTri += I.Count / 3;
                }
            }
        }
        return best;
    }

    /// <summary>Collision triangles whose three corners project inside the rectangle (view pixels), in front of the camera.</summary>
    List<(SceneObject Obj, string Asset, Matrix4x4 ToWorld, List<int> Tris)> CollisionInRect(Rectangle r)
    {
        var res = new List<(SceneObject, string, Matrix4x4, List<int>)>();
        if (_allColl == null) return res;
        var vp = View() * Proj(); float W = _gl.Width, H = _gl.Height;
        foreach (var o in _allColl.Keys)
        {
            if (!CollisionVisible(o)) continue;
            foreach (var (asset, meshes, local) in CollisionParts(o))
            {
                var toWorld = local * o.Transform; var m4 = toWorld * vp;
                var list = new List<int>(); int baseTri = 0;
                foreach (var m in meshes)
                {
                    var clip = new Vector4[m.Positions.Count];
                    for (int i = 0; i < m.Positions.Count; i++) clip[i] = Vector4.Transform(new Vector4(m.Positions[i], 1), m4);
                    // every triangle whose projected area or edges touch the rectangle (1.12: all three corners inside)
                    for (int k = 0; k + 2 < m.Triangles.Count; k += 3)
                        if (TriInRect(clip[m.Triangles[k]], clip[m.Triangles[k + 1]], clip[m.Triangles[k + 2]], r, W, H)) list.Add(baseTri + k / 3);
                    baseTri += m.Triangles.Count / 3;
                }
                if (list.Count > 0) res.Add((o, asset, toWorld, list));
            }
        }
        return res;
    }

    /// <summary>The selected triangles, orange, on top (moved by the preview while the selection is being transformed).</summary>
    void DrawCollisionSelection(Matrix4x4 vp)
    {
        var sel = CollisionSelection?.Invoke();
        if (sel == null || sel.Count == 0 || _allColl == null) return;
        var preview = CollisionPreview?.Invoke();
        string key = $"{_collSelVer}|{string.Join(",", sel.Select(kv => kv.Key + ":" + kv.Value.Count))}";
        if (_collSelBatch == null || _collSelKey != key)
        {
            if (_collSelBatch != null) _r.DeleteLineBatch(_collSelBatch);
            var segs = new List<(Vector3, Vector3)>();
            foreach (var o in _allColl.Keys)
            {
                if (!CollisionVisible(o)) continue;
                foreach (var (asset, meshes, local) in CollisionParts(o))
                {
                    if (!sel.TryGetValue(asset, out var tris) || tris.Count == 0) continue;
                    var toWorld = local * o.Transform; int baseTri = 0;
                    foreach (var m in meshes)
                    {
                        int n = m.Triangles.Count / 3;
                        foreach (int t in tris)
                        {
                            int lt = t - baseTri; if (lt < 0 || lt >= n) continue;
                            for (int e = 0; e < 3; e++)
                                segs.Add((Vector3.Transform(m.Positions[m.Triangles[3 * lt + e]], toWorld), Vector3.Transform(m.Positions[m.Triangles[3 * lt + (e + 1) % 3]], toWorld)));
                        }
                        baseTri += n;
                    }
                    if (segs.Count > 600000) break;
                }
            }
            _collSelBatch = segs.Count > 0 ? _r.CreateLineBatch(segs, new Vector3(1f, 0.45f, 0.05f)) : null;
            _collSelKey = key;
        }
        if (_collSelBatch != null) _r.DrawLineBatch(_collSelBatch, (preview?.Delta ?? Matrix4x4.Identity) * vp, onTop: true);
    }

    // ------------------------------------------------------------------ rectangle select

    Point? _rectFrom; Point _rectTo;
    readonly Renderer.Overlay _rectOv = new();

    /// <summary>Left button down in Edit Collision mode (not on a gizmo handle): starts a click / rectangle.</summary>
    bool CollisionMouseDown(MouseEventArgs e)
    {
        if (!_collMode || e.Button != MouseButtons.Left) return false;
        _rectFrom = e.Location; _rectTo = e.Location;
        return true;
    }

    bool CollisionMouseMove(MouseEventArgs e)
    {
        if (_rectFrom == null) return false;
        _rectTo = e.Location; _gl.Invalidate();
        return true;
    }

    bool CollisionMouseUp(MouseEventArgs e)
    {
        if (_rectObjects || _rectFrom is not { } a || e.Button != MouseButtons.Left) return false;
        _rectFrom = null; _gl.Invalidate();
        bool shift = (ModifierKeys & Keys.Shift) != 0, alt = (ModifierKeys & Keys.Alt) != 0;
        var r = Rectangle.FromLTRB(Math.Min(a.X, e.X), Math.Min(a.Y, e.Y), Math.Max(a.X, e.X), Math.Max(a.Y, e.Y));
        if (r.Width < 4 && r.Height < 4) CollisionPicked?.Invoke(PickCollision(e.Location), shift, alt);
        else CollisionRectSelected?.Invoke(CollisionInRect(r), shift);
        return true;
    }

    void DrawCollisionRect(int W, int H)
    {
        if (_rectFrom is not { } a) return;
        var r = Rectangle.FromLTRB(Math.Min(a.X, _rectTo.X), Math.Min(a.Y, _rectTo.Y), Math.Max(a.X, _rectTo.X), Math.Max(a.Y, _rectTo.Y));
        if (r.Width < 4 && r.Height < 4) return;
        string key = $"{r.Width}x{r.Height}";
        if (_rectOv.Key != key)
        {
            using var bmp = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height), System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(40, 242, 140, 40));
                using var pen = new Pen(Color.FromArgb(255, 242, 140, 40), 1) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
                g.DrawRectangle(pen, 0, 0, r.Width - 1, r.Height - 1);
            }
            _r.UpdateOverlay(_rectOv, bmp, key);
        }
        _r.DrawOverlay(_rectOv, r.X, r.Y, W, H);
    }

    /// <summary>Where the mouse ray meets collision or a drawn surface (for new collision shapes); null: nothing.</summary>
    public (Vector3 Point, Vector3 Normal)? CollisionSurfaceAt(Point? pixel)
    {
        var mp = _gl.PointToClient(Control.MousePosition);
        var pt = pixel ?? (_gl.ClientRectangle.Contains(mp) ? mp : new Point(_gl.Width / 2, _gl.Height / 2));
        if (PickCollision(pt) is { } h) return (h.Point, Vector3.UnitY);
        var (p, hit, _) = SurfaceAt(pt, 30);
        return (p, Vector3.UnitY);
    }

    /// <summary>Camera yaw (radians, 0 = facing +Z), for new shapes facing away from the camera.</summary>
    public float CameraYaw => _yaw;
}
