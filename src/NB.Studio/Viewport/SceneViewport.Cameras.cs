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
        if (_cutPaths.Count > 0)
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

    /// <summary>"Warp camera: Theater District" (etc.) above every visible camera within 600 units.</summary>
    void DrawCameraLabels(int W, int H)
    {
        if (Scene == null || !_showCameras) return;
        foreach (var o in Scene.Objects)
        {
            if (!o.Visible || CameraPoints.Label(o) is not { } text) continue;
            if (o.Kind == SceneObjectKind.CutsceneKey)
            {
                // a cut-scene's keys: the selected one and every fifth, not all 30 of a path
                if (!IsSelected(o) && o.CutsceneFrame % (5 * CutsceneKeys.Spacing) != 0) continue;
                text = o.Name;   // "animatedsequence1_shot3 key 4 (4.0 s)"
            }
            var top = o.Transform.Translation + Vector3.UnitY * 1.6f;
            if (Vector3.Dot(top - _camPos, Forward()) < 0.5f || Vector3.Distance(top, _camPos) > 400) continue;
            if (ToScreen(top) is not { } sp) continue;
            if (!_cameraLabels.TryGetValue(text, out var ov))
            {
                _cameraLabels[text] = ov = new Renderer.Overlay();
                using var bmp = DrawCameraLabel(text);
                _r.UpdateOverlay(ov, bmp, text);
            }
            int x2 = (int)sp.X - ov.W / 2, y2 = (int)sp.Y - ov.H - 2;
            if (x2 > W || y2 > H || x2 + ov.W < 0 || y2 + ov.H < 0) continue;
            _r.DrawOverlay(ov, x2, y2, W, H);
        }
    }

    static Bitmap DrawCameraLabel(string text)
    {
        using var font = new Font("Segoe UI Semibold", 9f);
        using var probe = new Bitmap(1, 1); using var pg = Graphics.FromImage(probe);
        var sz = pg.MeasureString(text, font);
        int w = (int)Math.Ceiling(sz.Width) + 28, h = (int)Math.Ceiling(sz.Height) + 6;
        var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);
        var edge = Color.FromArgb(255, 40, 150, 200);
        using (var p = Rounded(new Rectangle(0, 0, w - 1, h - 1), 5)) using (var b = new SolidBrush(Color.FromArgb(235, 246, 248, 250))) using (var e = new Pen(edge, 1.5f)) { g.FillPath(b, p); g.DrawPath(e, p); }
        // a little camera
        using (var cb = new SolidBrush(edge)) { g.FillRectangle(cb, 5, h / 2 - 4, 10, 8); g.FillPolygon(cb, new[] { new PointF(15, h / 2f), new PointF(21, h / 2f - 4), new PointF(21, h / 2f + 4) }); }
        using (var tb = new SolidBrush(Color.FromArgb(255, 32, 35, 42))) g.DrawString(text, font, tb, 23, 2);
        return bmp;
    }
}
