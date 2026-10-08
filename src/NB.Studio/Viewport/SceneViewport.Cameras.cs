using System.Numerics;
using NB.Core.World;

namespace NB.Studio.Viewport;

/// <summary>
/// Fixed cameras (marker points the game uses as cameras, see <see cref="CameraPoints"/>): drawn as a camera with its view
/// frustum and labelled ("Warp camera: Theater District"); selected, moved and turned like any marker (pitch = rotation X,
/// yaw = rotation Y). "Look Through Camera" puts the 3D view at the camera; "Set Camera from 3D View" places the camera
/// where the 3D view is. The Show menu's "Cameras" group hides them.
/// </summary>
public sealed partial class SceneViewport
{
    public bool ShowCameras { get => _showCameras; set { if (_showCameras != value) { _showCameras = value; _gl.Invalidate(); } } }
    bool _showCameras = true;

    /// <summary>Picking / selection box of a camera without a model (model space: the body behind, the frustum ahead).</summary>
    static readonly (Vector3 Min, Vector3 Max) CameraBounds = (new(-1.3f, -1.0f, -1.3f), new(1.3f, 1.0f, 4.6f));
    static readonly Vector3 CameraColour = new(0.25f, 0.85f, 1f), CameraSelColour = new(1f, 0.85f, 0.3f);

    void InitCameras(WorldScene? scene)
    {
        CameraPoints.Build(scene);
        if (scene == null) return;
        foreach (var o in scene.Objects)
            if (CameraPoints.Is(o) && o.Model == null) { o.BoundsMin = CameraBounds.Min; o.BoundsMax = CameraBounds.Max; }
    }

    /// <summary>The 3D view looks through a camera (its position, pitch and yaw).</summary>
    public void LookThrough(SceneObject cam)
    {
        var (_, yaw, pitch) = CameraPoints.View(cam.Transform);
        _camPos = cam.Transform.Translation; _yaw = yaw; _pitch = pitch;
        _gl.Invalidate();
    }

    /// <summary>A camera transform at the 3D view's position and direction.</summary>
    public Matrix4x4 CameraFromView() => CameraPoints.FromView(_camPos, _yaw, _pitch);

    /// <summary>A camera: body and lens behind the point, the view frustum (60° wide, 16:9) ahead with a mark on its top.</summary>
    static void AddCameraFigure(List<(Vector3, Vector3, Vector3)> l, SceneObject o, Vector3 c)
    {
        var m = o.Transform; var p = m.Translation;
        var (x, y, z) = Axes(m);
        Vector3 P(float a, float b, float d) => p + x * a + y * b + z * d;
        // body (a box behind the point) and lens
        float bw = 0.55f, bh = 0.42f, bd = 1.1f;
        var bc = new[] { P(-bw, -bh, -bd), P(bw, -bh, -bd), P(bw, bh, -bd), P(-bw, bh, -bd), P(-bw, -bh, -0.15f), P(bw, -bh, -0.15f), P(bw, bh, -0.15f), P(-bw, bh, -0.15f) };
        int[] e = { 0, 1, 1, 2, 2, 3, 3, 0, 4, 5, 5, 6, 6, 7, 7, 4, 0, 4, 1, 5, 2, 6, 3, 7 };
        for (int i = 0; i < e.Length; i += 2) l.Add((bc[e[i]], bc[e[i + 1]], c));
        const int N = 10;
        for (int i = 0; i < N; i++)
        {
            float a0 = i * MathF.Tau / N, a1 = (i + 1) * MathF.Tau / N;
            l.Add((P(MathF.Cos(a0) * 0.3f, MathF.Sin(a0) * 0.3f, 0.1f), P(MathF.Cos(a1) * 0.3f, MathF.Sin(a1) * 0.3f, 0.1f), c));
        }
        // frustum
        const float D = 4.5f; float hw = D * MathF.Tan(MathF.PI / 6), hh = hw * 9 / 16;
        var f = new[] { P(-hw, -hh, D), P(hw, -hh, D), P(hw, hh, D), P(-hw, hh, D) };
        for (int i = 0; i < 4; i++) { l.Add((p, f[i], c)); l.Add((f[i], f[(i + 1) % 4], c)); }
        l.Add((P(-hw * 0.3f, hh, D), P(0, hh * 1.45f, D), c)); l.Add((P(hw * 0.3f, hh, D), P(0, hh * 1.45f, D), c));   // up
    }

    // ------------------------------------------------------------------ cut-scene camera paths

    List<CutsceneCamera> _cutPaths = new();
    Renderer.LineBatch? _cutBatch;
    int _cutVersion, _cutBatchVersion = -1;
    CutsceneCamera? _cutActive;

    /// <summary>The cut-scene cameras of the open world: every path is drawn as a thin violet line (Cameras group).</summary>
    public void SetCutscenePaths(IEnumerable<CutsceneCamera> cams) { _cutPaths = cams.ToList(); _cutVersion++; _gl.Invalidate(); }

    /// <summary>The cut-scene being edited: its path is drawn on top, bright, as it will be once saved.</summary>
    public CutsceneCamera? ActiveCutscene { get => _cutActive; set { _cutActive = value; _gl.Invalidate(); } }

    /// <summary>Picking box of a camera object added after the scene was set (cut-scene keys).</summary>
    public void PrepareCameraObject(SceneObject o) { if (o.Model == null) { o.BoundsMin = CameraBounds.Min; o.BoundsMax = CameraBounds.Max; } }

    void DrawCutscenePaths(Matrix4x4 vp)
    {
        if (!_showCameras || Scene == null) return;
        if (_cutPaths.Count > 0 && _showAllCut)
        {
            if (_cutBatch == null || _cutBatchVersion != _cutVersion)
            {
                if (_cutBatch != null) _r.DeleteLineBatch(_cutBatch);
                var l = new List<(Vector3, Vector3, Vector3)>();
                var col = new Vector3(0.72f, 0.45f, 1f);
                foreach (var c in _cutPaths)
                    for (int f = 2; f < c.Positions.Length; f += 2)
                        if (Vector3.DistanceSquared(c.Positions[f - 2], c.Positions[f]) < 400) l.Add((c.Positions[f - 2], c.Positions[f], col));   // a cut between shots: no line
                _cutBatch = _r.CreateColoredLineBatch(l);
                _cutBatchVersion = _cutVersion;
            }
            _r.DrawLineBatch(_cutBatch, vp, onTop: false);
        }
        // paths with keys (open for editing): bright, on top, as they will be saved
        var open = Scene.Objects.Where(o => o.Kind == SceneObjectKind.CutsceneKey && o.Cutscene != null).GroupBy(o => o.Cutscene!).ToList();
        if (open.Count == 0) return;
        var lines = new List<(Vector3, Vector3, Vector3)>();
        foreach (var g in open)
        {
            var pos = CutsceneKeys.Preview(g.Key, g);
            var col = g.Key == _cutActive ? new Vector3(1f, 0.82f, 0.25f) : new Vector3(0.9f, 0.6f, 1f);
            for (int f = 1; f < pos.Length; f++)
                if (Vector3.DistanceSquared(pos[f - 1], pos[f]) < 100) lines.Add((pos[f - 1], pos[f], col));
        }
        _r.Lines(lines, vp, true, 2f);
    }

    void DrawCameraFigures(Matrix4x4 vp)
    {
        if (!_showCameras || Scene == null) return;
        var l = new List<(Vector3, Vector3, Vector3)>();
        foreach (var o in Scene.Objects)
            if (o.Visible && CameraPoints.Is(o) && Vector3.Distance(o.Transform.Translation, _camPos) is > 0.3f and < 3000)   // not the one we look through
                AddCameraFigure(l, o, IsSelected(o) ? CameraSelColour : CameraColour);
        if (l.Count > 0) _r.Lines(l, vp, true, 2.5f);
    }

    readonly Dictionary<string, Renderer.Overlay> _cameraLabels = new();
    readonly Renderer.Overlay _cameraIcon = new();

    /// <summary>Full camera labels within this distance (or when the camera is selected or hovered); beyond it only a small
    /// camera icon, up to <see cref="CameraIconDistance"/>. 1.18 drew every label within 400 units, which cluttered the town.</summary>
    const float CameraLabelDistance = 70, CameraIconDistance = 450;

    /// <summary>Camera labels: a small icon from far away, the name ("Warp camera: Theater District") when near, hovered or
    /// selected. Cut-scene keys: only the selected key and every fifth one get a name.</summary>
    void DrawCameraLabels(int W, int H)
    {
        if (Scene == null || !_showCameras) return;
        if (_cameraIcon.Key != "camicon") using (var ib = DrawCameraIcon()) _r.UpdateOverlay(_cameraIcon, ib, "camicon");
        foreach (var o in Scene.Objects)
        {
            if (!o.Visible || CameraPoints.Label(o) is not { } text) continue;
            bool focus = IsSelected(o) || o == _tipFor;
            if (o.Kind == SceneObjectKind.CutsceneKey)
            {
                if (!focus && o.CutsceneFrame % (5 * CutsceneKeys.Spacing) != 0) continue;
                text = o.Name;   // "animatedsequence1_shot3 key 4 (4.0 s)"
            }
            var top = o.Transform.Translation + Vector3.UnitY * 1.4f;
            float dist = Vector3.Distance(top, _camPos);
            if (Vector3.Dot(top - _camPos, Forward()) < 0.5f || dist > CameraIconDistance) continue;
            if (ToScreen(top) is not { } sp) continue;
            Renderer.Overlay ov;
            if (focus || dist < CameraLabelDistance)
            {
                if (!_cameraLabels.TryGetValue(text, out ov!))
                {
                    _cameraLabels[text] = ov = new Renderer.Overlay();
                    using var bmp = DrawCameraLabel(text);
                    _r.UpdateOverlay(ov, bmp, text);
                }
            }
            else ov = _cameraIcon;
            int x2 = (int)sp.X - ov.W / 2, y2 = (int)sp.Y - ov.H - 2;
            if (x2 > W || y2 > H || x2 + ov.W < 0 || y2 + ov.H < 0) continue;
            _r.DrawOverlay(ov, x2, y2, W, H);
        }
    }

    static readonly Color CameraEdge = Color.FromArgb(255, 40, 150, 200);

    /// <summary>A compact label: 7.5 pt text on a light chip with a tiny camera.</summary>
    static Bitmap DrawCameraLabel(string text)
    {
        using var font = new Font("Segoe UI", 7.5f);
        using var probe = new Bitmap(1, 1); using var pg = Graphics.FromImage(probe);
        var sz = pg.MeasureString(text, font);
        int w = (int)Math.Ceiling(sz.Width) + 17, h = (int)Math.Ceiling(sz.Height) + 2;
        var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);
        using (var p = Rounded(new Rectangle(0, 0, w - 1, h - 1), 3)) using (var b = new SolidBrush(Color.FromArgb(215, 246, 248, 250))) using (var e = new Pen(CameraEdge, 1f)) { g.FillPath(b, p); g.DrawPath(e, p); }
        using (var cb = new SolidBrush(CameraEdge)) { g.FillRectangle(cb, 3, h / 2 - 3, 7, 6); g.FillPolygon(cb, new[] { new PointF(10, h / 2f), new PointF(14, h / 2f - 3), new PointF(14, h / 2f + 3) }); }
        using (var tb = new SolidBrush(Color.FromArgb(255, 32, 35, 42))) g.DrawString(text, font, tb, 15, 1);
        return bmp;
    }

    /// <summary>The far-away marker of a camera: a tiny camera glyph, no text.</summary>
    static Bitmap DrawCameraIcon()
    {
        var bmp = new Bitmap(16, 11, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using (var b = new SolidBrush(Color.FromArgb(200, 246, 248, 250))) g.FillRectangle(b, 0, 0, 16, 11);
        using (var cb = new SolidBrush(CameraEdge)) { g.FillRectangle(cb, 2, 2, 8, 7); g.FillPolygon(cb, new[] { new PointF(10, 5.5f), new PointF(15, 2), new PointF(15, 9) }); }
        return bmp;
    }

    // ------------------------------------------------------------------ cut-scene paths: which are shown, hover, click

    /// <summary>Show menu "Cut-scene camera paths (all)": every cut-scene path of the world (off by default; 1.18 always drew
    /// all 125 of Showdown Town). Without it only the cut-scenes opened for editing are drawn.</summary>
    public bool ShowAllCutscenePaths { get => _showAllCut; set { if (_showAllCut != value) { _showAllCut = value; _gl.Invalidate(); } } }
    bool _showAllCut;

    /// <summary>A click on a cut-scene path (when all paths are shown): MainForm opens its keys.</summary>
    public event Action<CutsceneCamera>? CutscenePathClicked;
    /// <summary>Esc in the 3D view with no transform running (the view takes Esc itself, so the form never sees it).</summary>
    public event Action? EscapePressed;
    CutsceneCamera? _tipPath;

    /// <summary>The cut-scene path within 6 pixels of a view point (every path when all are shown, else the open ones).</summary>
    CutsceneCamera? CutscenePathAt(Point p)
    {
        if (!_showCameras || Scene == null) return null;
        var open = Scene.Objects.Where(o => o.Cutscene != null).Select(o => o.Cutscene!).ToHashSet();
        var cams = _showAllCut ? _cutPaths : _cutPaths.Where(open.Contains).ToList();
        CutsceneCamera? best = null; float bestD = 6;
        var m = new Vector2(p.X, p.Y);
        foreach (var c in cams)
        {
            Vector2? prev = null;
            for (int f = 0; f < c.Positions.Length; f += 3)
            {
                var s = ToScreen(c.Positions[f]);
                if (s is { } b && prev is { } a2 && Vector2.DistanceSquared(a2, b) < 250000)
                {
                    var ab = b - a2; float t = Math.Clamp(Vector2.Dot(m - a2, ab) / Math.Max(1e-3f, ab.LengthSquared()), 0, 1);
                    float d = Vector2.Distance(m, a2 + ab * t);
                    if (d < bestD) { bestD = d; best = c; }
                }
                prev = s;
            }
        }
        return best;
    }

    /// <summary>Hover over a cut-scene path: its name (drawn like the other tooltips).</summary>
    void HoverCutscenePath()
    {
        if (CutscenePathAt(_mouse) is not { } c) return;
        _tipPath = c; _tipAt = new Point(_mouse.X + 14, _mouse.Y + 18);
        _gl.Invalidate();
    }

    void DrawPathTip(int W, int H)
    {
        if (_tipPath == null || _tipFor != null) return;
        string key = "path|" + _tipPath.Asset + "|" + _tipPath.CameraName;
        if (_tipOv.Key != key)
            using (var bmp = DrawHud("Cut-scene camera path", Wrap($"{_tipPath.Asset} ({_tipPath.Duration:0.0} s, camera {_tipPath.CameraName}). The camera of an in-game cut-scene: click the path to show its keys and edit it.", 70), CameraEdge))
                _r.UpdateOverlay(_tipOv, bmp, key);
        int x = Math.Min(_tipAt.X, Math.Max(0, W - _tipOv.W - 4)), y = Math.Min(_tipAt.Y, Math.Max(0, H - _tipOv.H - 4));
        _r.DrawOverlay(_tipOv, x, y, W, H);
    }
}
