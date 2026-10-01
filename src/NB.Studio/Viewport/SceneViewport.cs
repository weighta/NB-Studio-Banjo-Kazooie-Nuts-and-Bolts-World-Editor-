using System.Numerics;
using NB.Core.World;
using OpenTK.GLControl;
using OpenTK.Graphics.OpenGL4;
using PixelFormat = OpenTK.Graphics.OpenGL4.PixelFormat;
using OpenTK.Windowing.Common;

namespace NB.Studio.Viewport;

public enum GizmoMode { Select, Move, Rotate, Scale }

/// <summary>
/// 3D viewport for a <see cref="WorldScene"/> (or a single model). Fly camera: right-drag to look, WASD/QE to move
/// (Shift = fast), wheel to dolly, middle-drag to pan, F to focus. Left click selects; left-drag on the selection
/// moves/rotates/scales it according to <see cref="Mode"/>, constrained to an axis while X, Y or Z is held.
/// Game space is right-handed with Y up (verified by rendering from the game camera's pose: docs/FORMATS.md §17), so
/// the view is a plain right-handed look-at, with no mirroring.
/// </summary>
public sealed class SceneViewport : UserControl
{
    readonly GLControl _gl;
    readonly Renderer _r = new();
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    bool _ready;

    public WorldScene? Scene { get; private set; }
    public SceneObject? Selected { get; private set; }
    public GizmoMode Mode = GizmoMode.Move;
    public bool ShowTerrain = true, ShowScenery = true, ShowMarkers = true, Textured = true;
    /// <summary>Draw Havok collision wireframes (terrain: cyan, scenery: yellow). The scene's collision must be loaded.</summary>
    public bool ShowCollision;
    /// <summary>Draw path-node links (marker type 22: record +8 = next node index).</summary>
    public bool ShowPaths = true;
    /// <summary>Debug: draw everything (no frustum / size culling), to compare against the culled frame.</summary>
    public bool NoCull;
    readonly Dictionary<string, Renderer.LineBatch> _collision = new();
    public event Action<SceneObject?>? SelectionChanged;
    public event Action<SceneObject>? ObjectEdited;   // after a drag finished (for undo)
    public event Action<SceneObject, Matrix4x4>? EditStarted;
    public event Action<SceneObject?, Point>? ContextMenuRequested;
    public Func<string, (byte[] Rgba, int W, int H)?>? TextureSource { set => _r.TextureSource = value; }

    // camera (game space)
    Vector3 _camPos = new(0, 60, -150);
    float _yaw = 0, _pitch = -0.3f;
    float _fov = 60f;
    readonly HashSet<Keys> _keys = new();
    Point _lastMouse; bool _looking, _panning, _dragging, _dragMoved;
    Matrix4x4 _dragStartXf; Vector3 _dragStartHit; Point _dragStartMouse;

    public SceneViewport()
    {
        _gl = new GLControl(new GLControlSettings { API = ContextAPI.OpenGL, APIVersion = new Version(3, 3), Profile = ContextProfile.Core, Flags = ContextFlags.Default })
        { Dock = DockStyle.Fill };
        Controls.Add(_gl);
        _gl.Load += (_, _) => { _gl.MakeCurrent(); _r.Init(); _ready = true; };
        _gl.Paint += (_, _) => Render();
        _gl.Resize += (_, _) => _gl.Invalidate();
        _gl.MouseDown += OnMouseDown; _gl.MouseUp += OnMouseUp; _gl.MouseMove += OnMouseMove; _gl.MouseWheel += OnWheel;
        _gl.KeyDown += (_, e) => { _keys.Add(e.KeyCode); OnKey(e); };
        _gl.KeyUp += (_, e) => _keys.Remove(e.KeyCode);
        _gl.PreviewKeyDown += (_, e) => e.IsInputKey = true;
        _gl.LostFocus += (_, _) => _keys.Clear();
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public bool Wireframe { get => _r.Wireframe; set { _r.Wireframe = value; _gl.Invalidate(); } }

    public void SetScene(WorldScene? scene)
    {
        if (_ready) { _gl.MakeCurrent(); _r.Clear(); foreach (var b in _collision.Values) _r.DeleteLineBatch(b); }
        _collision.Clear();
        if (_ready && _staticLines != null) _r.DeleteLineBatch(_staticLines);
        _staticLines = null; _linesVersion++;
        Scene = scene; Selected = null;
        if (scene != null)
        {
            var terrain = scene.Objects.FirstOrDefault(o => o.Kind == SceneObjectKind.Terrain);
            var c = terrain != null ? (terrain.BoundsMin + terrain.BoundsMax) / 2 : Vector3.Zero;
            // start above the centre of the scenery if there is any. Hidden scenery (moved far below the level, or shrunk to
            // ~0) is left out: modified worlds sink hundreds of originals to y -20000, which put the start camera deep
            // underground and showed an empty viewport (Seattle).
            var sc = scene.Objects.Where(o => o.Kind == SceneObjectKind.Scenery && !IsHidden(o)).Select(o => o.Transform.Translation).ToList();
            if (sc.Count > 0) c = new Vector3(sc.Average(v => v.X), sc.Average(v => v.Y), sc.Average(v => v.Z));
            _camPos = c + new Vector3(0, 120, -250); _yaw = 0; _pitch = -0.4f;
        }
        SelectionChanged?.Invoke(null);
        _gl.Invalidate();
    }

    /// <summary>Scripted mouse look: a right-button drag by (dx, dy) pixels through the normal mouse handlers.</summary>
    public void SimulateLook(int dx, int dy)
    {
        OnMouseDown(null, new MouseEventArgs(MouseButtons.Right, 1, 300, 300, 0));
        for (int i = 1; i <= 10; i++) OnMouseMove(null, new MouseEventArgs(MouseButtons.Right, 0, 300 + dx * i / 10, 300 + dy * i / 10, 0));
        OnMouseUp(null, new MouseEventArgs(MouseButtons.Right, 1, 300 + dx, 300 + dy, 0));
    }

    public (float Yaw, float Pitch) LookAngles => (_yaw, _pitch);

    /// <summary>Scenery hidden by the tool: below y -200, or scaled to (almost) nothing.</summary>
    public static bool IsHidden(SceneObject o)
    {
        var t = o.Transform;
        float scale = new Vector3(t.M11, t.M12, t.M13).Length();
        return t.Translation.Y < -200 || scale < 0.01f;
    }

    public void Select(SceneObject? o, bool focus = false)
    {
        Selected = o;
        if (focus && o != null) Focus(o);
        SelectionChanged?.Invoke(o);
        _gl.Invalidate();
    }

    public void Focus(SceneObject o)
    {
        var (c, r) = WorldBounds(o);
        var fwd = Forward();
        _camPos = c - fwd * Math.Max(8, r * 2.2f);
        _gl.Invalidate();
    }

    public void Refresh3D() { _linesVersion++; _gl.Invalidate(); }

    /// <summary>Renders <paramref name="frames"/> frames from the current camera (GPU finished each frame) and returns
    /// the mean frame time and what the last frame drew (scripted performance checks).</summary>
    public (double Ms, int Objects, int Draws) Benchmark(int frames)
    {
        Render(); _gl.MakeCurrent(); GL.Finish();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < frames; i++) { Render(); GL.Finish(); }
        return (sw.Elapsed.TotalMilliseconds / frames, _r.ObjectsDrawn, _r.DrawsIssued);
    }

    /// <summary>Renders a frame and reads it back (for screenshots / automated checks).</summary>
    public Bitmap Capture()
    {
        Render();
        _gl.MakeCurrent();
        int w = _gl.Width, h = _gl.Height;
        var px = new byte[w * h * 4];
        GL.ReadBuffer(ReadBufferMode.Back);
        GL.ReadPixels(0, 0, w, h, PixelFormat.Bgra, PixelType.UnsignedByte, px);
        var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
        for (int y = 0; y < h; y++) System.Runtime.InteropServices.Marshal.Copy(px, (h - 1 - y) * w * 4, bd.Scan0 + y * bd.Stride, w * 4);
        bmp.UnlockBits(bd);
        return bmp;
    }

    /// <summary>Places the camera (used by scripted runs).</summary>
    public Vector3 CameraPosition => _camPos;
    public void SetCamera(Vector3 pos, float yawDeg, float pitchDeg) { _camPos = pos; _yaw = yawDeg * MathF.PI / 180; _pitch = pitchDeg * MathF.PI / 180; _gl.Invalidate(); }


    Vector3 Forward() => new(MathF.Sin(_yaw) * MathF.Cos(_pitch), MathF.Sin(_pitch), MathF.Cos(_yaw) * MathF.Cos(_pitch));
    Vector3 Right() => Vector3.Normalize(Vector3.Cross(Forward(), Vector3.UnitY));

    Matrix4x4 View() => Matrix4x4.CreateLookAt(_camPos, _camPos + Forward(), Vector3.UnitY);
    Matrix4x4 Proj() => Matrix4x4.CreatePerspectiveFieldOfView(_fov * MathF.PI / 180, Math.Max(1, _gl.Width) / (float)Math.Max(1, _gl.Height), 0.5f, 20000f);

    (Vector3 Center, float Radius) WorldBounds(SceneObject o)
    {
        var c = Vector3.Transform((o.BoundsMin + o.BoundsMax) / 2, o.Transform);
        var e = (o.BoundsMax - o.BoundsMin) / 2;
        float s = MathF.Max(o.Transform.M11 * o.Transform.M11 + o.Transform.M12 * o.Transform.M12 + o.Transform.M13 * o.Transform.M13, 1e-6f);
        return (c, e.Length() * MathF.Sqrt(s));
    }

    void Render()
    {
        if (!_ready) return;
        _gl.MakeCurrent();
        GL.Viewport(0, 0, _gl.Width, _gl.Height);
        GL.ClearColor(0.42f, 0.55f, 0.72f, 1);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        var vp = View() * Proj();
        if (Scene != null)
        {
            _r.Begin(new Vector3(0.4f, 1f, 0.3f));
            // culling: whole objects and model pieces outside the view, or smaller than ~a pixel; scenery the tool hid
            // (sunk below the level / shrunk to nothing) is skipped unless selected
            float minSize = MathF.Tan(_fov * MathF.PI / 360) / Math.Max(1, _gl.Height) * 0.75f;
            var fr = new Renderer.Frustum(vp, _camPos, NoCull ? 0 : minSize);
            foreach (var o in Scene.Objects)
            {
                if (!o.Visible || o.Model == null) continue;
                if (o.Kind == SceneObjectKind.Terrain && !ShowTerrain) continue;
                if (o.Kind == SceneObjectKind.Scenery && (!ShowScenery || (o != Selected && IsHidden(o)))) continue;
                if (!NoCull && o.Kind != SceneObjectKind.Terrain) { var (wc, wr) = WorldBounds(o); if (!fr.Visible(wc, wr)) continue; }
                var tint = o == Selected ? new Vector4(1f, 0.55f, 0.1f, 0.35f) : o.Dirty ? new Vector4(0.2f, 0.9f, 0.3f, 0.15f) : Vector4.Zero;
                _r.DrawModel(o.Model, o.Transform, vp, tint, Textured, NoCull ? null : fr);
                foreach (var (cm, cl) in o.Children) _r.DrawModel(cm, cl * o.Transform, vp, tint, Textured, NoCull ? null : fr);
            }
            if (ShowCollision && Scene.CollisionByModel != null)
            {
                foreach (var o in Scene.Objects)
                {
                    if (!o.Visible || o.Model == null || !Scene.CollisionByModel.TryGetValue(o.ModelName, out var meshes)) continue;
                    if (!_collision.TryGetValue(o.ModelName, out var batch))
                    {
                        var segs = new List<(Vector3, Vector3)>(); var seen = new HashSet<(int, int, int)>();
                        for (int mi = 0; mi < meshes.Count; mi++)
                        {
                            var m = meshes[mi];
                            for (int t = 0; t + 2 < m.Triangles.Count; t += 3)
                                for (int e = 0; e < 3; e++)
                                {
                                    int a = m.Triangles[t + e], b = m.Triangles[t + (e + 1) % 3];
                                    if (seen.Add((mi, Math.Min(a, b), Math.Max(a, b)))) segs.Add((m.Positions[a], m.Positions[b]));
                                }
                        }
                        batch = _r.CreateLineBatch(segs, o.Kind == SceneObjectKind.Terrain ? new Vector3(0.1f, 0.95f, 1f) : new Vector3(1f, 0.9f, 0.15f));
                        _collision[o.ModelName] = batch;
                    }
                    _r.DrawLineBatch(batch, o.Transform * vp);
                }
            }
            var key = (_linesVersion, ShowMarkers, ShowPaths);
            if (_staticLines == null || _staticKey != key)
            {
                if (_staticLines != null) _r.DeleteLineBatch(_staticLines);
                _staticLines = _r.CreateColoredLineBatch(StaticLines());
                _staticKey = key;
            }
            _r.DrawLineBatch(_staticLines, vp, onTop: true);
            var lines = new List<(Vector3, Vector3, Vector3)>();
            if (Selected != null)
            {
                AddBox(lines, Selected, new Vector3(1, 0.6f, 0.1f));
                var p = Selected.Transform.Translation;
                float len = Math.Max(3, WorldBounds(Selected).Radius * 0.8f);
                var ax = Axes(Selected.Transform);
                lines.Add((p, p + ax.X * len, new Vector3(1, 0.2f, 0.2f)));
                lines.Add((p, p + ax.Y * len, new Vector3(0.2f, 1, 0.2f)));
                lines.Add((p, p + ax.Z * len, new Vector3(0.3f, 0.5f, 1)));
            }
            _r.Lines(lines, vp, true);
        }
        _gl.SwapBuffers();
    }

    Renderer.LineBatch? _staticLines;
    (int, bool, bool) _staticKey;
    int _linesVersion;

    /// <summary>Marker boxes, scenery crosses and path links: rebuilt only when the scene or its markers change
    /// (<see cref="Refresh3D"/>), not every frame.</summary>
    List<(Vector3, Vector3, Vector3)> StaticLines()
    {
        var lines = new List<(Vector3, Vector3, Vector3)>();
        if (Scene == null) return lines;
        {
            foreach (var o in Scene.Objects.Where(o => o.Visible && o.Model == null && (o.Kind != SceneObjectKind.Marker || ShowMarkers)))
            {
                if (o.Kind == SceneObjectKind.Marker) AddBox(lines, o, MarkerColor(o.Marker!.Type));
                else AddCross(lines, o.Transform.Translation, 2, new Vector3(1, 0, 1));
            }
            if (ShowPaths)
            {
                var nodes = Scene.Objects.Where(o => o.Visible && o.Marker is { Type: 22 } && o.MarkerSet != null).ToList();
                var byKey = new Dictionary<(MarkerAsset, int), SceneObject>();
                foreach (var n in nodes) byKey.TryAdd((n.MarkerSet!, n.Marker!.Index), n);
                foreach (var n in nodes)
                    if (n.Marker!.Link != n.Marker.Index && byKey.TryGetValue((n.MarkerSet!, n.Marker.Link), out var next))
                    {
                        var a = n.Transform.Translation + Vector3.UnitY * 0.5f; var b = next.Transform.Translation + Vector3.UnitY * 0.5f;
                        lines.Add((a, b, new Vector3(1f, 0.45f, 0.05f)));
                        var dir = b - a; if (dir.LengthSquared() > 1e-4f)
                        {   // arrow head showing the direction of travel
                            var f = Vector3.Normalize(dir); var side = Vector3.Normalize(Vector3.Cross(f, Vector3.UnitY) + new Vector3(1e-6f));
                            var tip = a + dir * 0.6f;
                            lines.Add((tip, tip - f * 1.5f + side * 0.8f, new Vector3(1f, 0.45f, 0.05f)));
                            lines.Add((tip, tip - f * 1.5f - side * 0.8f, new Vector3(1f, 0.45f, 0.05f)));
                        }
                    }
            }
        }
        return lines;
    }

    static (Vector3 X, Vector3 Y, Vector3 Z) Axes(Matrix4x4 m) =>
        (Vector3.Normalize(new Vector3(m.M11, m.M12, m.M13) + new Vector3(1e-9f)), Vector3.Normalize(new Vector3(m.M21, m.M22, m.M23) + new Vector3(1e-9f)), Vector3.Normalize(new Vector3(m.M31, m.M32, m.M33) + new Vector3(1e-9f)));

    static Vector3 MarkerColor(int t)
    {
        uint h = (uint)t * 2654435761u;
        return new Vector3(0.35f + (h & 0xFF) / 400f, 0.35f + ((h >> 8) & 0xFF) / 400f, 0.35f + ((h >> 16) & 0xFF) / 400f);
    }

    static void AddCross(List<(Vector3, Vector3, Vector3)> l, Vector3 p, float s, Vector3 c)
    {
        l.Add((p - Vector3.UnitX * s, p + Vector3.UnitX * s, c)); l.Add((p - Vector3.UnitY * s, p + Vector3.UnitY * s, c)); l.Add((p - Vector3.UnitZ * s, p + Vector3.UnitZ * s, c));
    }

    static void AddBox(List<(Vector3, Vector3, Vector3)> l, SceneObject o, Vector3 c)
    {
        var a = o.BoundsMin; var b = o.BoundsMax;
        var pts = new Vector3[8];
        for (int i = 0; i < 8; i++) pts[i] = Vector3.Transform(new Vector3((i & 1) != 0 ? b.X : a.X, (i & 2) != 0 ? b.Y : a.Y, (i & 4) != 0 ? b.Z : a.Z), o.Transform);
        int[] e = { 0, 1, 1, 3, 3, 2, 2, 0, 4, 5, 5, 7, 7, 6, 6, 4, 0, 4, 1, 5, 2, 6, 3, 7 };
        for (int i = 0; i < e.Length; i += 2) l.Add((pts[e[i]], pts[e[i + 1]], c));
    }

    // ------------------------------------------------------------------ input

    void Tick()
    {
        if (!_gl.Focused || _keys.Count == 0) return;
        float speed = (_keys.Contains(Keys.ShiftKey) ? 4f : 1f) * 1.2f;
        var f = Forward(); var r = Right(); var d = Vector3.Zero;
        if (_keys.Contains(Keys.W)) d += f; if (_keys.Contains(Keys.S)) d -= f;
        if (_keys.Contains(Keys.D)) d += r; if (_keys.Contains(Keys.A)) d -= r;
        if (_keys.Contains(Keys.E)) d += Vector3.UnitY; if (_keys.Contains(Keys.Q)) d -= Vector3.UnitY;
        if (d != Vector3.Zero && !_dragging) { _camPos += d * speed; _gl.Invalidate(); }
    }

    void OnKey(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F && Selected != null) Focus(Selected);
        if (e.KeyCode == Keys.D1) Mode = GizmoMode.Move;
        if (e.KeyCode == Keys.D2) Mode = GizmoMode.Rotate;
        if (e.KeyCode == Keys.D3) Mode = GizmoMode.Scale;
        if (e.KeyCode == Keys.Escape) Select(null);
    }

    void OnWheel(object? s, MouseEventArgs e) { _camPos += Forward() * (e.Delta / 120f) * 8f * (_keys.Contains(Keys.ShiftKey) ? 4 : 1); _gl.Invalidate(); }

    void OnMouseDown(object? s, MouseEventArgs e)
    {
        _gl.Focus();
        _lastMouse = e.Location;
        if (e.Button == MouseButtons.Right) { _looking = true; _dragMoved = false; }
        else if (e.Button == MouseButtons.Middle) _panning = true;
        else if (e.Button == MouseButtons.Left)
        {
            var hit = Pick(e.Location);
            if (hit.Obj != null && hit.Obj == Selected && Selected.Kind != SceneObjectKind.Terrain && Mode != GizmoMode.Select)
            {
                _dragging = true; _dragMoved = false;
                _dragStartXf = Selected.Transform; _dragStartMouse = e.Location;
                _dragStartHit = RayPlane(e.Location, Selected.Transform.Translation) ?? Selected.Transform.Translation;
                EditStarted?.Invoke(Selected, Selected.Transform);
            }
            else Select(hit.Obj);
        }
    }

    void OnMouseUp(object? s, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            _looking = false;
            if (!_dragMoved) { var hit = Pick(e.Location); if (hit.Obj != null) Select(hit.Obj); ContextMenuRequested?.Invoke(hit.Obj ?? Selected, e.Location); }
        }
        if (e.Button == MouseButtons.Middle) _panning = false;
        if (e.Button == MouseButtons.Left && _dragging)
        {
            _dragging = false;
            if (_dragMoved && Selected != null) ObjectEdited?.Invoke(Selected);
        }
    }

    void OnMouseMove(object? s, MouseEventArgs e)
    {
        int dx = e.X - _lastMouse.X, dy = e.Y - _lastMouse.Y;
        _lastMouse = e.Location;
        if (_looking)
        {
            if (Math.Abs(dx) + Math.Abs(dy) > 1) _dragMoved = true;
            _yaw -= dx * 0.006f;   // yaw grows to the left in right-handed space
            _pitch = Math.Clamp(_pitch - dy * 0.006f, -1.55f, 1.55f);
            _gl.Invalidate();
        }
        else if (_panning) { _camPos += (-Right() * dx + Vector3.UnitY * dy) * 0.25f; _gl.Invalidate(); }
        else if (_dragging && Selected != null) { DragEdit(e.Location); _dragMoved = true; }
    }

    Vector3? AxisConstraint() =>
        _keys.Contains(Keys.X) ? Vector3.UnitX : _keys.Contains(Keys.Y) ? Vector3.UnitY : _keys.Contains(Keys.Z) ? Vector3.UnitZ : null;

    void DragEdit(Point mouse)
    {
        var o = Selected!;
        var axis = AxisConstraint();
        var start = _dragStartXf;
        var pos = start.Translation;
        switch (Mode)
        {
            case GizmoMode.Move:
            {
                Vector3 delta;
                if (axis == Vector3.UnitY)
                {
                    float dist = Vector3.Distance(_camPos, pos);
                    delta = Vector3.UnitY * (-(mouse.Y - _dragStartMouse.Y) * dist * 0.0022f);
                }
                else
                {
                    var hit = RayPlane(mouse, pos);
                    if (hit == null) return;
                    delta = hit.Value - _dragStartHit;
                    delta.Y = 0;
                    if (axis != null) delta = axis.Value * Vector3.Dot(delta, axis.Value);
                }
                var m = start; m.Translation = pos + delta; o.Transform = m;
                break;
            }
            case GizmoMode.Rotate:
            {
                float ang = (mouse.X - _dragStartMouse.X) * 0.01f;
                var ax = axis ?? Vector3.UnitY;
                var noT = start; noT.Translation = Vector3.Zero;
                var m = noT * Matrix4x4.CreateFromAxisAngle(ax, ang); m.Translation = pos; o.Transform = m;
                break;
            }
            case GizmoMode.Scale:
            {
                float f = MathF.Max(0.01f, 1 + (mouse.X - _dragStartMouse.X) * 0.01f);
                var sv = axis == null ? new Vector3(f) : Vector3.One + axis.Value * (f - 1);
                var noT = start; noT.Translation = Vector3.Zero;
                var m = Matrix4x4.CreateScale(sv) * noT; m.Translation = pos; o.Transform = m;
                break;
            }
        }
        SelectionChanged?.Invoke(o);
        _gl.Invalidate();
    }

    // ------------------------------------------------------------------ picking

    (Vector3 Origin, Vector3 Dir) Ray(Point p)
    {
        var vp = View() * Proj();
        Matrix4x4.Invert(vp, out var inv);
        float x = 2f * p.X / Math.Max(1, _gl.Width) - 1, y = 1 - 2f * p.Y / Math.Max(1, _gl.Height);
        var a = Vector4.Transform(new Vector4(x, y, 0, 1), inv); var b = Vector4.Transform(new Vector4(x, y, 1, 1), inv);
        var pa = new Vector3(a.X, a.Y, a.Z) / a.W; var pb = new Vector3(b.X, b.Y, b.Z) / b.W;
        return (pa, Vector3.Normalize(pb - pa));
    }

    /// <summary>Intersects the mouse ray with the horizontal plane through <paramref name="through"/>.</summary>
    Vector3? RayPlane(Point p, Vector3 through)
    {
        var (o, d) = Ray(p);
        if (MathF.Abs(d.Y) < 1e-4f) return null;
        float t = (through.Y - o.Y) / d.Y;
        return t > 0 ? o + d * t : null;
    }

    public (SceneObject? Obj, float Dist) Pick(Point p)
    {
        if (Scene == null) return (null, 0);
        var (ro, rd) = Ray(p);
        SceneObject? best = null; float bestT = float.MaxValue;
        foreach (var o in Scene.Objects)
        {
            if (!o.Visible) continue;
            if (o.Kind == SceneObjectKind.Terrain && !ShowTerrain) continue;
            if (o.Kind == SceneObjectKind.Scenery && !ShowScenery) continue;
            if (o.Kind == SceneObjectKind.Marker && !ShowMarkers) continue;
            if (!Matrix4x4.Invert(o.Transform, out var inv)) continue;
            var lo = Vector3.Transform(ro, inv); var ld = Vector3.TransformNormal(rd, inv);
            if (!RayBox(lo, ld, o.BoundsMin, o.BoundsMax, out float tb) || tb > bestT) continue;
            float t = o.Model == null ? tb : RayMesh(lo, ld, o.Model, bestT);
            foreach (var (cm, cl) in o.Children)
                if (Matrix4x4.Invert(cl, out var ci)) t = MathF.Min(t, RayMesh(Vector3.Transform(lo, ci), Vector3.TransformNormal(ld, ci), cm, bestT));
            if (t < bestT) { bestT = t; best = o; }
        }
        return (best, bestT);
    }

    static bool RayBox(Vector3 o, Vector3 d, Vector3 mn, Vector3 mx, out float t)
    {
        float t0 = 0, t1 = float.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            float oi = i == 0 ? o.X : i == 1 ? o.Y : o.Z, di = i == 0 ? d.X : i == 1 ? d.Y : d.Z;
            float a = i == 0 ? mn.X : i == 1 ? mn.Y : mn.Z, b = i == 0 ? mx.X : i == 1 ? mx.Y : mx.Z;
            if (MathF.Abs(di) < 1e-9f) { if (oi < a || oi > b) { t = 0; return false; } continue; }
            float ta = (a - oi) / di, tb = (b - oi) / di;
            if (ta > tb) (ta, tb) = (tb, ta);
            t0 = MathF.Max(t0, ta); t1 = MathF.Min(t1, tb);
            if (t0 > t1) { t = 0; return false; }
        }
        t = t0; return true;
    }

    static float RayMesh(Vector3 o, Vector3 d, NB.Core.Models.ModelAsset model, float limit)
    {
        float best = float.MaxValue;
        foreach (var dr in model.Draws)
        {
            var P = dr.Positions; var I = dr.Indices;
            for (int k = 0; k + 2 < I.Length; k += 3)
            {
                int a = I[k], b = I[k + 1], c = I[k + 2];
                if (a >= P.Length || b >= P.Length || c >= P.Length) continue;
                var e1 = P[b] - P[a]; var e2 = P[c] - P[a];
                var pv = Vector3.Cross(d, e2); float det = Vector3.Dot(e1, pv);
                if (MathF.Abs(det) < 1e-9f) continue;
                float inv = 1 / det; var tv = o - P[a];
                float u = Vector3.Dot(tv, pv) * inv; if (u < 0 || u > 1) continue;
                var qv = Vector3.Cross(tv, e1); float v = Vector3.Dot(d, qv) * inv; if (v < 0 || u + v > 1) continue;
                float t = Vector3.Dot(e2, qv) * inv;
                if (t > 0 && t < best) best = t;
            }
        }
        return best;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); if (_ready) { _gl.MakeCurrent(); _r.Dispose(); } }
        base.Dispose(disposing);
    }
}
