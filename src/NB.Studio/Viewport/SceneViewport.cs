using System.Numerics;
using NB.Core.Models;
using NB.Core.World;
using OpenTK.GLControl;
using OpenTK.Graphics.OpenGL4;
using PixelFormat = OpenTK.Graphics.OpenGL4.PixelFormat;
using OpenTK.Windowing.Common;

namespace NB.Studio.Viewport;

public enum GizmoMode { Select, Move, Rotate, Scale }

/// <summary>
/// 3D viewport for a <see cref="WorldScene"/> (or a single model). Fly camera: right-drag to look, WASD/QE (or the arrow
/// keys) to move (Shift = fast), wheel to dolly, middle-drag to pan, F to focus. Left click selects; left-drag on the
/// selection moves/rotates/scales it according to <see cref="Mode"/>; dragging one of the gizmo's axis handles (or holding
/// X, Y or Z) constrains the drag to that axis.
/// Blender-style keys on the selection: G grab (move on the camera plane), R rotate, S scale; then X / Y / Z constrain to
/// that world axis (press again: the object's own axis, again: free), digits type an exact value, Ctrl snaps, left click /
/// Enter confirms, right click / Esc cancels. One undo step per confirmed transform.
/// View modes (the bar in the top-right corner): Wireframe, Solid, Textured, Rendered (the level's light setup, normal and
/// specular maps, reflections, fog and sky dome).
/// Game space is right-handed with Y up (verified by rendering from the game camera's pose: docs/FORMATS.md §17), so
/// the view is a plain right-handed look-at, with no mirroring.
/// </summary>
public sealed partial class SceneViewport : UserControl
{
    readonly GLControl _gl;
    readonly Renderer _r = new();
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    bool _ready;

    public WorldScene? Scene { get; private set; }
    public SceneObject? Selected { get; private set; }
    public GizmoMode Mode = GizmoMode.Move;
    public bool ShowTerrain = true, ShowScenery = true, ShowMarkers = true;
    /// <summary>Draw Havok collision wireframes (terrain: cyan, scenery: yellow). The scene's collision must be loaded.</summary>
    public bool ShowCollision;
    /// <summary>Draw path-node links (marker type 22: record +8 = next node index).</summary>
    public bool ShowPaths = true;
    /// <summary>Debug: draw everything (no frustum / size culling), to compare against the culled frame.</summary>
    public bool NoCull;
    readonly Dictionary<string, Renderer.LineBatch> _collision = new();
    public event Action<SceneObject?>? SelectionChanged;
    public event Action<SceneObject>? ObjectEdited;   // after a transform was confirmed (for undo)
    public event Action<SceneObject, Matrix4x4>? EditStarted;
    public event Action<SceneObject?, Point>? ContextMenuRequested;
    public event Action<ViewMode>? ViewModeChanged;
    public Func<string, (byte[] Rgba, int W, int H)?>? TextureSource { set => _r.TextureSource = value; }

    // camera (game space)
    Vector3 _camPos = new(0, 60, -150);
    float _yaw = 0, _pitch = -0.3f;
    float _fov = 60f;
    readonly HashSet<Keys> _keys = new();
    Point _lastMouse, _mouse; bool _looking, _panning, _dragMoved, _suppressRightUp;

    public SceneViewport()
    {
        _gl = new GLControl(new GLControlSettings { API = ContextAPI.OpenGL, APIVersion = new Version(3, 3), Profile = ContextProfile.Core, Flags = ContextFlags.Default })
        { Dock = DockStyle.Fill };
        Controls.Add(_gl);
        _gl.Load += (_, _) => { _gl.MakeCurrent(); _r.Init(); _ready = true; };
        _gl.Paint += (_, _) => Render();
        _gl.Resize += (_, _) => _gl.Invalidate();
        _gl.MouseDown += OnMouseDown; _gl.MouseUp += OnMouseUp; _gl.MouseMove += OnMouseMove; _gl.MouseWheel += OnWheel;
        _gl.MouseLeave += (_, _) => { if (_hoverBar != -1 || _hoverHandle != -1) { _hoverBar = -1; _hoverHandle = -1; _gl.Invalidate(); } };
        _gl.KeyDown += (_, e) => { _keys.Add(e.KeyCode); OnKey(e); };
        _gl.KeyUp += (_, e) => { _keys.Remove(e.KeyCode); if (_xf != XfKind.None && _xfDrag) UpdateTransform(); };
        _gl.PreviewKeyDown += (_, e) => e.IsInputKey = true;
        _gl.LostFocus += (_, _) => _keys.Clear();
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        _viewMode = LoadViewMode();
        _r.Mode = _viewMode;
    }

    // ------------------------------------------------------------------ view modes

    ViewMode _viewMode;
    /// <summary>Shading of the 3D view (remembered between sessions).</summary>
    public ViewMode ViewMode
    {
        get => _viewMode;
        set
        {
            if (_viewMode == value) return;
            _viewMode = value; _r.Mode = value;
            if (value == ViewMode.Rendered) EnsureLighting();
            SaveViewMode(value);
            ViewModeChanged?.Invoke(value);
            _gl.Invalidate();
        }
    }

    /// <summary>Compatibility with the View menu: textures on = Textured, off = Solid.</summary>
    public bool Textured { get => _viewMode is ViewMode.Textured or ViewMode.Rendered; set => ViewMode = value ? (_viewMode == ViewMode.Rendered ? ViewMode.Rendered : ViewMode.Textured) : ViewMode.Solid; }
    public bool Wireframe { get => _viewMode == ViewMode.Wireframe; set => ViewMode = value ? ViewMode.Wireframe : ViewMode.Textured; }

    static string ViewSettingsPath => Path.Combine(NB.Core.Project.ProjectRegistry.DataDir, "viewport.json");
    static ViewMode LoadViewMode()
    {
        try
        {
            if (File.Exists(ViewSettingsPath))
            {
                var d = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ViewSettingsPath));
                if (d != null && d.TryGetValue("ViewMode", out var s) && Enum.TryParse<ViewMode>(s, out var m)) return m;
            }
        }
        catch { }
        return ViewMode.Textured;
    }
    static void SaveViewMode(ViewMode m)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ViewSettingsPath)!);
            File.WriteAllText(ViewSettingsPath, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["ViewMode"] = m.ToString() }));
        }
        catch { }
    }

    // ------------------------------------------------------------------ level lighting and sky

    List<LevelLighting>? _lights;
    int _lightIndex;
    ModelAsset? _sky;
    readonly List<(string Name, ModelAsset? Model)> _skies = new();
    public string LightingName => _lights is { Count: > 0 } ? _lights[_lightIndex].Name : "default";

    void EnsureLighting()
    {
        if (_lights != null || Scene == null) return;
        _lights = new();
        var caffs = new List<NB.Core.Formats.CaffFile> { Scene.Caff };
        foreach (var b in Scene.MarkerBundles) try { caffs.Add(Scene.Workspace.LoadResident(b)); } catch { }
        foreach (var c in caffs)
        {
            try { _lights.AddRange(LevelLighting.All(c).Where(l => !_lights.Any(x => x.Name == l.Name))); } catch { }
            for (int s = 1; s <= c.Symbols.Count; s++)
            {
                var n = c.Symbols[s - 1];
                if (!n.StartsWith("aid_model_") || !n.Contains("skydome")) continue;
                try { var m = ModelAsset.Parse(c, s); if (m.Draws.Count > 0) _skies.Add((NB.Core.Formats.AssetIds.DisplayName(n), m)); } catch { }
            }
        }
        _lights = _lights.OrderBy(l => l.Name.EndsWith("_main") ? 0 : 1).ToList();
        _lightIndex = 0;
        ApplyLight();
    }

    void ApplyLight()
    {
        if (_lights is { Count: > 0 })
        {
            var l = _lights[_lightIndex];
            _r.Lighting = SceneLighting.From(l);
            // sky dome of the phase: midday ("main") uses the afternoon dome model in Showdown Town
            string phase = l.Name[(l.Name.LastIndexOf('_') + 1)..];
            string[] prefs = phase switch
            {
                "main" => new[] { "afternoon", "day", "bluesky", "blue", "sunrise", "morning" },
                "afternoon" => new[] { "evening", "sunset", "afternoon" },
                _ => new[] { phase },
            };
            _sky = prefs.Select(p => _skies.FirstOrDefault(s => s.Name.Contains(p)).Model).FirstOrDefault(m => m != null) ?? _skies.FirstOrDefault().Model;
        }
        else { _r.Lighting = new SceneLighting(); _sky = _skies.FirstOrDefault().Model; }
        _gl.Invalidate();
    }

    /// <summary>Cycles through the level's light setups (morning, midday, afternoon, night in Showdown Town).</summary>
    public void NextLight(string? name = null)
    {
        EnsureLighting();
        if (_lights is not { Count: > 0 }) return;
        if (name != null) { int i = _lights.FindIndex(l => l.Name.Contains(name, StringComparison.OrdinalIgnoreCase)); if (i >= 0) _lightIndex = i; }
        else _lightIndex = (_lightIndex + 1) % _lights.Count;
        ApplyLight();
    }

    // ------------------------------------------------------------------ water (chunk 38 of the background model)

    ModelAsset? _water;

    /// <summary>The world's water surfaces as a drawable model: translucent blue-green with the sky reflected (the game's
    /// water shader is not translated; colour and opacity were matched by eye to Showdown Town's harbour).</summary>
    ModelAsset? BuildWater(WorldScene scene)
    {
        try
        {
            var regions = WaterEditor.Read(scene.Caff, scene.Background.View.Symbol);
            if (regions.Count == 0) return null;
            var model = new ModelAsset();
            string? skyTex = _skies.Select(sk => sk.Model?.Draws.SelectMany(d => d.Textures).Select(t => t.Texture).FirstOrDefault()).FirstOrDefault(t => t != null);
            foreach (var r in regions)
            {
                if (r.Triangles.Count < 3) continue;
                int n = r.Triangles.Count - r.Triangles.Count % 3;
                var d = new MeshDraw
                {
                    Positions = r.Triangles.Take(n).ToArray(),
                    Normals = Enumerable.Repeat(Vector3.UnitY, n).ToArray(),
                    UVs = r.Triangles.Take(n).Select(p => new Vector2(p.X, p.Z) * 0.02f).ToArray(),
                    Indices = Enumerable.Range(0, n).ToArray(),
                    SectionFlags = 0x4502,
                };
                model.Draws.Add(d);
                _r.MaterialOverrides[d] = new MaterialInfo
                {
                    Blend = BlendKind.Blend, Tint = new Vector3(0.10f, 0.21f, 0.24f), Opacity = 0.85f,
                    SpecPower = 140, SpecColour = new Vector3(0.9f), Reflect = skyTex, ReflectStrength = 0.38f,
                };
            }
            return model.Draws.Count > 0 ? model : null;
        }
        catch (Exception e) { scene.Log.Add("water: " + e.Message); return null; }
    }

    // ------------------------------------------------------------------ scene

    public void SetScene(WorldScene? scene)
    {
        CancelTransform();
        if (_ready) { _gl.MakeCurrent(); _r.Clear(); foreach (var b in _collision.Values) _r.DeleteLineBatch(b); }
        _collision.Clear();
        if (_ready && _staticLines != null) _r.DeleteLineBatch(_staticLines);
        _staticLines = null; _linesVersion++;
        Scene = scene; Selected = null;
        _lights = null; _skies.Clear(); _sky = null; _r.Lighting = new SceneLighting();
        _water = null; _r.MaterialOverrides.Clear();
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
            EnsureLighting();
            _water = BuildWater(scene);
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
        if (o != Selected) CancelTransform();
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

    /// <summary>Renders a frame and reads it back (for screenshots / automated checks). Includes the overlays.</summary>
    public Bitmap Capture()
    {
        int w = Math.Max(1, _gl.Width), h = Math.Max(1, _gl.Height);
        var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        if (!_ready || _gl.Width < 1 || _gl.Height < 1) return bmp;
        Render(swap: false);
        _gl.MakeCurrent();
        var px = new byte[w * h * 4];
        GL.ReadBuffer(ReadBufferMode.Back);
        GL.ReadPixels(0, 0, w, h, PixelFormat.Bgra, PixelType.UnsignedByte, px);
        _gl.SwapBuffers();
        for (int i = 3; i < px.Length; i += 4) px[i] = 255;
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
        for (int y = 0; y < h; y++) System.Runtime.InteropServices.Marshal.Copy(px, (h - 1 - y) * w * 4, bd.Scan0 + y * bd.Stride, w * 4);
        bmp.UnlockBits(bd);
        return bmp;
    }

    /// <summary>Places the camera (used by scripted runs).</summary>
    public Vector3 CameraPosition => _camPos;
    public void SetCamera(Vector3 pos, float yawDeg, float pitchDeg) { _camPos = pos; _yaw = yawDeg * MathF.PI / 180; _pitch = pitchDeg * MathF.PI / 180; _gl.Invalidate(); }
    public float FieldOfView { get => _fov; set { _fov = Math.Clamp(value, 10, 120); _gl.Invalidate(); } }

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

    static Vector4 ClearColour(ViewMode m) => m switch
    {
        ViewMode.Wireframe => new(0.16f, 0.17f, 0.19f, 1),
        ViewMode.Solid => new(0.24f, 0.25f, 0.27f, 1),
        _ => new(0.42f, 0.55f, 0.72f, 1),
    };

    void Render(bool swap = true)
    {
        if (!_ready) return;
        _gl.MakeCurrent();
        int W = _gl.Width, H = _gl.Height;
        if (W < 1 || H < 1) return;   // minimized
        GL.Viewport(0, 0, W, H);
        var cc = ClearColour(_viewMode);
        if (_viewMode == ViewMode.Rendered) { var f = _r.Lighting.FogColour; cc = new Vector4(f.X, f.Y, f.Z, 1); }
        GL.ClearColor(cc.X, cc.Y, cc.Z, cc.W);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        var view = View(); var vp = view * Proj();
        if (Scene != null)
        {
            if (_viewMode == ViewMode.Rendered && _r.Shadows) RenderShadowMap(W, H);
            _r.Begin(vp, _camPos);
            if (_viewMode == ViewMode.Rendered && _sky != null)
            {
                // sky dome around the camera: unlit, behind everything
                GL.DepthMask(false); GL.Disable(EnableCap.DepthTest);
                _r.DrawModel(_sky, Matrix4x4.CreateTranslation(_camPos), Vector4.Zero, null, unlit: true);
                GL.Enable(EnableCap.DepthTest); GL.DepthMask(true);
            }
            // culling: whole objects and model pieces outside the view, or smaller than ~a pixel; scenery the tool hid
            // (sunk below the level / shrunk to nothing) is skipped unless selected
            float minSize = MathF.Tan(_fov * MathF.PI / 360) / Math.Max(1, H) * 0.75f;
            var fr = new Renderer.Frustum(vp, _camPos, NoCull ? 0 : minSize);
            foreach (var o in Scene.Objects)
            {
                if (!o.Visible || o.Model == null) continue;
                if (o.Kind == SceneObjectKind.Terrain && !ShowTerrain) continue;
                if (o.Kind == SceneObjectKind.Scenery && (!ShowScenery || (o != Selected && IsHidden(o)))) continue;
                if (!NoCull && o.Kind != SceneObjectKind.Terrain) { var (wc, wr) = WorldBounds(o); if (!fr.Visible(wc, wr)) continue; }
                var tint = o == Selected ? new Vector4(1f, 0.55f, 0.1f, _viewMode == ViewMode.Wireframe ? 1f : 0.35f) : o.Dirty ? new Vector4(0.2f, 0.9f, 0.3f, 0.15f) : Vector4.Zero;
                _r.DrawModel(o.Model, o.Transform, tint, NoCull ? null : fr);
                foreach (var (cm, cl) in o.Children) _r.DrawModel(cm, cl * o.Transform, tint, NoCull ? null : fr);
            }
            if (_water != null && ShowTerrain && _viewMode is ViewMode.Textured or ViewMode.Rendered) _r.DrawModel(_water, Matrix4x4.Identity, Vector4.Zero, NoCull ? null : fr);
            _r.FlushTransparent();
            GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
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
            if (Selected != null) DrawGizmo(vp);
        }
        DrawOverlays(W, H);
        if (swap) _gl.SwapBuffers();
    }

    /// <summary>Sun shadow map around the part of the level in front of the camera (orthographic, 2 × 260 units).</summary>
    void RenderShadowMap(int W, int H)
    {
        const float R = 260;
        var sun = Vector3.Normalize(_r.Lighting.SunDirection);
        var fwd = Forward(); var flat = new Vector3(fwd.X, 0, fwd.Z);
        var centre = _camPos + (flat.LengthSquared() > 1e-4f ? Vector3.Normalize(flat) : Vector3.Zero) * (R * 0.55f);
        centre.Y = _camPos.Y - Math.Min(40, Math.Max(0, _camPos.Y * 0.2f));
        var eye = centre + sun * 1500;
        var lv = Matrix4x4.CreateLookAt(eye, centre, MathF.Abs(sun.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY);
        // snap to whole shadow texels so the shadows do not shimmer while the camera moves
        float texel = 2 * R / Renderer.ShadowSize;
        var c = Vector3.Transform(centre, lv);
        lv.M41 -= c.X - MathF.Round(c.X / texel) * texel; lv.M42 -= c.Y - MathF.Round(c.Y / texel) * texel;
        var lvp = lv * Matrix4x4.CreateOrthographic(2 * R, 2 * R, 1, 3500);
        var fr = new Renderer.Frustum(lvp, eye, 0);
        _r.BeginShadow(lvp);
        foreach (var o in Scene!.Objects)
        {
            if (!o.Visible || o.Model == null) continue;
            if (o.Kind == SceneObjectKind.Terrain && !ShowTerrain) continue;
            if (o.Kind == SceneObjectKind.Scenery && (!ShowScenery || IsHidden(o))) continue;
            if (o.Kind != SceneObjectKind.Terrain) { var (wc, wr) = WorldBounds(o); if (!fr.Visible(wc, wr)) continue; }
            _r.DrawShadow(o.Model, o.Transform, fr);
            foreach (var (cm, cl) in o.Children) _r.DrawShadow(cm, cl * o.Transform, fr);
        }
        _r.EndShadow(W, H);
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

    // ------------------------------------------------------------------ gizmo

    static readonly Vector3[] AxisColour = { new(1f, 0.22f, 0.22f), new(0.35f, 0.95f, 0.25f), new(0.25f, 0.5f, 1f) };
    static readonly string[] AxisName = { "X", "Y", "Z" };
    int _hoverHandle = -1;

    /// <summary>Gizmo axes at the selection: world axes for moving (the axes a move is constrained to), the object's own
    /// axes for rotating and scaling.</summary>
    Vector3[] GizmoAxes()
    {
        if (Selected == null) return new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
        if (Mode == GizmoMode.Move) return new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
        var (x, y, z) = Axes(Selected.Transform);
        return new[] { x, y, z };
    }

    float GizmoLength() => Selected == null ? 3 : Math.Max(3, WorldBounds(Selected).Radius * 0.8f);

    void DrawGizmo(Matrix4x4 vp)
    {
        var lines = new List<(Vector3, Vector3, Vector3)>();
        AddBox(lines, Selected!, new Vector3(1, 0.6f, 0.1f));
        var p = Selected!.Transform.Translation;
        if (_xf != XfKind.None)
        {
            var piv = _xfStart.Translation;
            if (_xfAxis >= 0)
            {
                // the constraint axis, drawn as an infinite line through the start position
                var a = XfAxisVector();
                lines.Add((piv - a * 20000, piv + a * 20000, AxisColour[_xfAxis]));
            }
            else if (_xf == XfKind.Grab && _xfDrag)
            {
                // free drag on the ground plane: a square grid around the start position
                float s = Math.Max(4, GizmoLength());
                for (int i = -2; i <= 2; i++)
                {
                    lines.Add((piv + new Vector3(-s, 0, i * s / 2), piv + new Vector3(s, 0, i * s / 2), new Vector3(0.75f, 0.75f, 0.3f)));
                    lines.Add((piv + new Vector3(i * s / 2, 0, -s), piv + new Vector3(i * s / 2, 0, s), new Vector3(0.75f, 0.75f, 0.3f)));
                }
            }
            if (_xf == XfKind.Grab) lines.Add((piv, p, new Vector3(1, 1, 1)));
            _r.Lines(lines, vp, true, 2f);
            return;
        }
        _r.Lines(lines, vp, true, 2f);
        if (Mode == GizmoMode.Select) return;
        var ax = GizmoAxes(); float len = GizmoLength();
        var right = Right(); var up = Vector3.Normalize(Vector3.Cross(right, Forward()));
        for (int i = 0; i < 3; i++)
        {
            var g = new List<(Vector3, Vector3, Vector3)>();
            var c = i == _hoverHandle ? new Vector3(1f, 0.95f, 0.3f) : AxisColour[i];
            var tip = p + ax[i] * len;
            g.Add((p, tip, c));
            // arrow head (move), box (scale) or ring arc marker (rotate)
            var side = Vector3.Cross(ax[i], Forward()); if (side.LengthSquared() < 1e-6f) side = right; side = Vector3.Normalize(side);
            float h = len * 0.12f;
            if (Mode == GizmoMode.Move) { g.Add((tip, tip - ax[i] * h * 1.6f + side * h * 0.6f, c)); g.Add((tip, tip - ax[i] * h * 1.6f - side * h * 0.6f, c)); }
            else if (Mode == GizmoMode.Scale) { var o2 = Vector3.Cross(side, ax[i]); var q = new[] { side + o2, side - o2, -side - o2, -side + o2 }; for (int k = 0; k < 4; k++) g.Add((tip + q[k] * h * 0.5f, tip + q[(k + 1) % 4] * h * 0.5f, c)); }
            else { for (int k = 0; k < 8; k++) { float a0 = k * MathF.PI / 4, a1 = (k + 1) * MathF.PI / 4; var o2 = Vector3.Cross(side, ax[i]); g.Add((tip + (side * MathF.Cos(a0) + o2 * MathF.Sin(a0)) * h * 0.6f, tip + (side * MathF.Cos(a1) + o2 * MathF.Sin(a1)) * h * 0.6f, c)); } }
            _r.Lines(g, vp, true, i == _hoverHandle ? 4f : 2.5f);
        }
    }

    /// <summary>The gizmo handle (0..2) under the mouse, or -1: within 9 pixels of an axis segment's screen projection.</summary>
    int HandleAt(Point m)
    {
        if (Selected == null || Selected.Kind == SceneObjectKind.Terrain || Mode == GizmoMode.Select) return -1;
        var p = Selected.Transform.Translation; var ax = GizmoAxes(); float len = GizmoLength();
        var s0 = ToScreen(p); if (s0 == null) return -1;
        int best = -1; float bestD = 9;
        for (int i = 0; i < 3; i++)
        {
            var s1 = ToScreen(p + ax[i] * len); if (s1 == null) continue;
            var a = s0.Value; var b = s1.Value; var ab = b - a; float l2 = ab.LengthSquared(); if (l2 < 4) continue;
            float t = Math.Clamp(Vector2.Dot(new Vector2(m.X, m.Y) - a, ab) / l2, 0.15f, 1f);
            float d = Vector2.Distance(new Vector2(m.X, m.Y), a + ab * t);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    Vector2? ToScreen(Vector3 p)
    {
        var c = Vector4.Transform(new Vector4(p, 1), View() * Proj());
        if (c.W <= 1e-4f) return null;
        return new Vector2((c.X / c.W * 0.5f + 0.5f) * _gl.Width, (0.5f - c.Y / c.W * 0.5f) * _gl.Height);
    }

    // ------------------------------------------------------------------ overlays (view-mode bar, HUD)

    readonly Renderer.Overlay _barOv = new(), _hudOv = new();
    static readonly string[] ModeLabels = { "Wire", "Solid", "Texture", "Render" };
    int _hoverBar = -1;
    const int BarSeg = 74, BarH = 26, BarMargin = 10, LightH = 22;
    Rectangle BarRect => new(_gl.Width - BarMargin - BarSeg * 4, BarMargin, BarSeg * 4, BarH);
    Rectangle LightRect => new(_gl.Width - BarMargin - BarSeg * 4, BarMargin + BarH + 4, BarSeg * 4, LightH);

    void DrawOverlays(int W, int H)
    {
        var br = BarRect;
        bool showLight = _viewMode == ViewMode.Rendered;
        string barKey = $"{_viewMode}|{_hoverBar}|{(showLight ? LightingName : "")}";
        if (_barOv.Key != barKey)
            using (var bmp = DrawBar(showLight)) _r.UpdateOverlay(_barOv, bmp, barKey);
        _r.DrawOverlay(_barOv, br.X, br.Y, W, H);
        var hud = HudText();
        if (hud != null)
        {
            string k = hud.Value.Main + "|" + hud.Value.Hint;
            if (_hudOv.Key != k) using (var bmp = DrawHud(hud.Value.Main, hud.Value.Hint, hud.Value.Colour)) _r.UpdateOverlay(_hudOv, bmp, k);
            _r.DrawOverlay(_hudOv, 12, H - _hudOv.H - 12, W, H);
        }
    }

    Bitmap DrawBar(bool showLight)
    {
        int h = showLight ? BarH + 4 + LightH : BarH;
        var bmp = new Bitmap(BarSeg * 4, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);
        using var bg = new SolidBrush(Color.FromArgb(215, 32, 34, 38));
        using var path = Rounded(new Rectangle(0, 0, BarSeg * 4 - 1, BarH - 1), 6);
        g.FillPath(bg, path);
        using var font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        for (int i = 0; i < 4; i++)
        {
            var r = new Rectangle(i * BarSeg + 2, 2, BarSeg - 4, BarH - 5);
            bool on = (int)_viewMode == i, hover = _hoverBar == i;
            if (on) { using var p2 = Rounded(r, 5); using var b2 = new SolidBrush(Color.FromArgb(255, 66, 118, 205)); g.FillPath(b2, p2); }
            else if (hover) { using var p2 = Rounded(r, 5); using var b2 = new SolidBrush(Color.FromArgb(255, 62, 64, 70)); g.FillPath(b2, p2); }
            DrawModeIcon(g, i, new Rectangle(r.X + 5, r.Y + 3, 15, 15), on);
            using var tb = new SolidBrush(on ? Color.White : Color.FromArgb(220, 222, 226));
            g.DrawString(ModeLabels[i], font, tb, r.X + 22, r.Y + 3);
        }
        if (showLight)
        {
            var lr = new Rectangle(0, BarH + 4, BarSeg * 4 - 1, LightH - 1);
            using var lp = Rounded(lr, 6); g.FillPath(bg, lp);
            using var f2 = new Font("Segoe UI", 8f);
            using var tb = new SolidBrush(Color.FromArgb(235, 236, 240));
            string text = $"☀ Light: {LightingName}" + (_lights is { Count: > 1 } ? "   ▸ click for next" : "");
            g.DrawString(text, f2, tb, 8, BarH + 7);
        }
        return bmp;
    }

    static void DrawModeIcon(Graphics g, int mode, Rectangle r, bool on)
    {
        var fg = on ? Color.White : Color.FromArgb(205, 208, 214);
        using var pen = new Pen(fg, 1.3f);
        switch (mode)
        {
            case 0:
                g.DrawEllipse(pen, r); g.DrawEllipse(pen, r.X + r.Width / 4f, r.Y, r.Width / 2f, r.Height);
                g.DrawLine(pen, r.X, r.Y + r.Height / 2f, r.Right, r.Y + r.Height / 2f); break;
            case 1:
            {
                using var br = new System.Drawing.Drawing2D.LinearGradientBrush(r, Color.FromArgb(240, 240, 240), Color.FromArgb(110, 112, 118), 45f);
                g.FillEllipse(br, r); break;
            }
            case 2:
            {
                using var clip = new System.Drawing.Drawing2D.GraphicsPath(); clip.AddEllipse(r);
                var old = g.Clip; g.SetClip(clip);
                for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++)
                {
                    using var b = new SolidBrush((x + y) % 2 == 0 ? Color.FromArgb(230, 180, 90) : Color.FromArgb(120, 80, 50));
                    g.FillRectangle(b, r.X + x * r.Width / 4f, r.Y + y * r.Height / 4f, r.Width / 4f + 1, r.Height / 4f + 1);
                }
                g.Clip = old; g.DrawEllipse(pen, r); break;
            }
            default:
            {
                using var br = new System.Drawing.Drawing2D.PathGradientBrush(new[] { new PointF(r.X, r.Y), new PointF(r.Right, r.Y), new PointF(r.Right, r.Bottom), new PointF(r.X, r.Bottom) })
                { CenterPoint = new PointF(r.X + r.Width * 0.35f, r.Y + r.Height * 0.3f), CenterColor = Color.FromArgb(255, 250, 200), SurroundColors = new[] { Color.FromArgb(200, 120, 40) } };
                g.FillEllipse(br, r); break;
            }
        }
    }

    static System.Drawing.Drawing2D.GraphicsPath Rounded(Rectangle r, int rad)
    {
        var p = new System.Drawing.Drawing2D.GraphicsPath(); int d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure();
        return p;
    }

    static Bitmap DrawHud(string main, string hint, Color accent)
    {
        using var f1 = new Font("Consolas", 13f, FontStyle.Bold); using var f2 = new Font("Segoe UI", 8.5f);
        using var probe = new Bitmap(1, 1); using var pg = Graphics.FromImage(probe);
        var s1 = pg.MeasureString(main, f1); var s2 = pg.MeasureString(hint, f2);
        int w = (int)Math.Ceiling(Math.Max(s1.Width, s2.Width)) + 24, h = (int)Math.Ceiling(s1.Height + s2.Height) + 14;
        var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);
        using (var p = Rounded(new Rectangle(0, 0, w - 1, h - 1), 7)) using (var b = new SolidBrush(Color.FromArgb(205, 24, 25, 29))) g.FillPath(b, p);
        using (var ab = new SolidBrush(accent)) g.FillRectangle(ab, 0, 6, 4, h - 12);
        using (var b = new SolidBrush(Color.White)) g.DrawString(main, f1, b, 12, 5);
        using (var b = new SolidBrush(Color.FromArgb(190, 196, 206))) g.DrawString(hint, f2, b, 12, 7 + s1.Height);
        return bmp;
    }

    (string Main, string Hint, Color Colour)? HudText()
    {
        if (_xf == XfKind.None || Selected == null) return null;
        var o = Selected; var start = _xfStart; var now = o.Transform;
        string axis = _xfAxis < 0 ? "" : (_xfSpace == AxisSpace.Local ? "local " : "") + AxisName[_xfAxis];
        string typed = _xfTyped.Length > 0 ? $"  [{_xfTyped}]" : "";
        string main;
        switch (_xf)
        {
            case XfKind.Grab:
            {
                var d = now.Translation - start.Translation;
                main = _xfAxis >= 0 ? $"Move  {axis}  {Vector3.Dot(d, XfAxisVector()):0.00}{typed}"
                    : $"Move  {(_xfDrag ? "ground plane  " : "")}dx {d.X:0.00}  dy {d.Y:0.00}  dz {d.Z:0.00}";
                break;
            }
            case XfKind.Scale: main = $"Scale  {axis}{(axis.Length > 0 ? "  " : "")}{_xfValue:0.000}{typed}"; break;
            default: main = $"Rotate  {(axis.Length > 0 ? axis : "view")}  {_xfValue:0.0}°{typed}"; break;
        }
        string hint = _xfDrag
            ? "hold X / Y / Z or drag an axis handle to constrain · Esc cancels"
            : "X / Y / Z constrain (again: world ↔ local, then free) · type a value · Ctrl snap · LMB / Enter confirm · RMB / Esc cancel";
        var col = _xfAxis >= 0 ? Color.FromArgb(255, (int)(AxisColour[_xfAxis].X * 255), (int)(AxisColour[_xfAxis].Y * 255), (int)(AxisColour[_xfAxis].Z * 255)) : Color.FromArgb(255, 240, 170, 40);
        return (main, hint, col);
    }

    // ------------------------------------------------------------------ transforms (gizmo drags and Blender-style G / R / S)

    enum XfKind { None, Grab, Rotate, Scale }
    enum AxisSpace { World, Local }
    XfKind _xf;
    int _xfAxis = -1; AxisSpace _xfSpace;
    Matrix4x4 _xfStart; Point _xfMouse0; string _xfTyped = ""; float _xfValue;
    /// <summary>The transform is a left-button drag (confirmed on release) rather than a modal key transform.</summary>
    bool _xfDrag; bool _xfDragKeyAxis;

    public bool Transforming => _xf != XfKind.None;

    Vector3 XfAxisVector()
    {
        if (_xfAxis < 0) return Vector3.Zero;
        if (_xfSpace == AxisSpace.World) return _xfAxis switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ };
        var (x, y, z) = Axes(_xfStart);
        return _xfAxis switch { 0 => x, 1 => y, _ => z };
    }

    void BeginTransform(XfKind kind, bool drag, int axis = -1, AxisSpace space = AxisSpace.World)
    {
        if (Selected == null || Selected.Kind == SceneObjectKind.Terrain) return;
        if (_xf != XfKind.None) { if (!drag) { _xf = kind; _xfTyped = ""; UpdateTransform(); } return; }
        _xf = kind; _xfDrag = drag; _xfAxis = axis; _xfSpace = space; _xfTyped = ""; _xfDragKeyAxis = false;
        _xfStart = Selected.Transform; _xfMouse0 = _mouse;
        _xfValue = kind == XfKind.Scale ? 1 : 0;
        EditStarted?.Invoke(Selected, _xfStart);
        _gl.Focus();
        _gl.Invalidate();
    }

    void ConfirmTransform()
    {
        if (_xf == XfKind.None) return;
        var o = Selected; var start = _xfStart;
        _xf = XfKind.None; _xfAxis = -1; _xfTyped = "";
        if (o != null && o.Transform != start) { ObjectEdited?.Invoke(o); SelectionChanged?.Invoke(o); }
        _gl.Invalidate();
    }

    public void CancelTransform()
    {
        if (_xf == XfKind.None) return;
        if (Selected != null) { Selected.Transform = _xfStart; SelectionChanged?.Invoke(Selected); }
        _xf = XfKind.None; _xfAxis = -1; _xfTyped = "";
        _gl.Invalidate();
    }

    void SetAxis(int axis)
    {
        // X / Y / Z: world axis, the same key again: the object's own axis, again: free. Scaling a turned object starts with
        // its own axis (scaling along a world axis would shear it), then the world axis.
        bool aligned = IsAxisAligned(_xfStart);
        var first = _xf == XfKind.Scale && !aligned ? AxisSpace.Local : AxisSpace.World;
        if (_xfAxis != axis) { _xfAxis = axis; _xfSpace = first; }
        else if (_xfSpace == first && !aligned) _xfSpace = first == AxisSpace.World ? AxisSpace.Local : AxisSpace.World;
        else _xfAxis = -1;
        UpdateTransform();
    }

    static bool IsAxisAligned(Matrix4x4 m)
    {
        var (x, y, z) = Axes(m);
        return MathF.Abs(x.X) > 0.9999f && MathF.Abs(y.Y) > 0.9999f && MathF.Abs(z.Z) > 0.9999f;
    }

    float? TypedValue => float.TryParse(_xfTyped == "-" ? "" : _xfTyped, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>Recomputes the selection's transform from the mouse (or the typed value).</summary>
    void UpdateTransform()
    {
        if (_xf == XfKind.None || Selected == null) return;
        var o = Selected; var start = _xfStart; var piv = start.Translation;
        bool snap = _keys.Contains(Keys.ControlKey);
        // a drag constrained by holding X / Y / Z
        if (_xfDrag && (_xfAxis < 0 || _xfDragKeyAxis))
        {
            var held = _keys.Contains(Keys.X) ? 0 : _keys.Contains(Keys.Y) ? 1 : _keys.Contains(Keys.Z) ? 2 : -1;
            if (held >= 0 || _xfDragKeyAxis) { _xfAxis = held; _xfDragKeyAxis = held >= 0; _xfSpace = _xf == XfKind.Grab ? AxisSpace.World : AxisSpace.Local; }
        }
        var a = XfAxisVector();
        switch (_xf)
        {
            case XfKind.Grab:
            {
                Vector3 delta;
                if (_xfAxis >= 0)
                {
                    float s = TypedValue ?? (AxisParam(_mouse, piv, a) - AxisParam(_xfMouse0, piv, a));
                    if (snap && TypedValue == null) s = MathF.Round(s);
                    delta = a * s;
                }
                else
                {
                    // modal G: the plane through the object facing the camera; drag: the ground plane
                    var n = _xfDrag ? Vector3.UnitY : Forward();
                    var h0 = RayPlane(_xfMouse0, piv, n); var h1 = RayPlane(_mouse, piv, n);
                    if (h0 == null || h1 == null) return;
                    delta = h1.Value - h0.Value;
                    if (_xfDrag) delta.Y = 0;
                    if (snap) delta = new Vector3(MathF.Round(delta.X), MathF.Round(delta.Y), MathF.Round(delta.Z));
                }
                var m = start; m.Translation = piv + delta; o.Transform = m;
                break;
            }
            case XfKind.Scale:
            {
                float f;
                if (TypedValue is { } tv) f = tv;
                else if (_xfDrag) f = 1 + (_mouse.X - _xfMouse0.X) * 0.01f;
                else
                {
                    var ps = ToScreen(piv) ?? new Vector2(_gl.Width / 2f, _gl.Height / 2f);
                    float d0 = Math.Max(4, Vector2.Distance(new Vector2(_xfMouse0.X, _xfMouse0.Y), ps));
                    f = Vector2.Distance(new Vector2(_mouse.X, _mouse.Y), ps) / d0;
                }
                if (snap && TypedValue == null) f = MathF.Round(f * 10) / 10;
                if (MathF.Abs(f) < 0.001f) f = 0.001f;
                _xfValue = f;
                Matrix4x4 m;
                if (_xfAxis < 0) m = Matrix4x4.CreateScale(f) * start;
                else if (_xfSpace == AxisSpace.Local) m = Matrix4x4.CreateScale(Vector3.One + (f - 1) * (_xfAxis switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ })) * start;
                else
                {
                    // scale along a world axis about the object's origin: I + (f - 1) a aᵀ
                    var S = Matrix4x4.Identity;
                    S.M11 += (f - 1) * a.X * a.X; S.M12 += (f - 1) * a.X * a.Y; S.M13 += (f - 1) * a.X * a.Z;
                    S.M21 += (f - 1) * a.Y * a.X; S.M22 += (f - 1) * a.Y * a.Y; S.M23 += (f - 1) * a.Y * a.Z;
                    S.M31 += (f - 1) * a.Z * a.X; S.M32 += (f - 1) * a.Z * a.Y; S.M33 += (f - 1) * a.Z * a.Z;
                    m = start * Matrix4x4.CreateTranslation(-piv) * S * Matrix4x4.CreateTranslation(piv);
                }
                m.Translation = piv; o.Transform = m;
                break;
            }
            case XfKind.Rotate:
            {
                float deg;
                if (TypedValue is { } tv) deg = tv;
                else if (_xfDrag) deg = (_mouse.X - _xfMouse0.X) * 0.6f;
                else
                {
                    var ps = ToScreen(piv) ?? new Vector2(_gl.Width / 2f, _gl.Height / 2f);
                    float a0 = MathF.Atan2(_xfMouse0.Y - ps.Y, _xfMouse0.X - ps.X), a1 = MathF.Atan2(_mouse.Y - ps.Y, _mouse.X - ps.X);
                    deg = (a1 - a0) * 180 / MathF.PI;
                    while (deg > 180) deg -= 360; while (deg < -180) deg += 360;
                }
                if (snap && TypedValue == null) deg = MathF.Round(deg / 15) * 15;
                _xfValue = deg;
                // no axis: about the view direction; modal angles follow the mouse around the object on screen (a clockwise
                // circle turns the object clockwise as seen from the camera); drags turn by the horizontal mouse movement
                var axis = Vector3.Normalize(_xfAxis >= 0 ? a : _xfDrag ? Vector3.UnitY : -Forward());
                float rad = deg * MathF.PI / 180;
                if (!_xfDrag && Vector3.Dot(axis, Forward()) < 0) rad = -rad;
                var m = start * Matrix4x4.CreateTranslation(-piv) * Matrix4x4.CreateFromAxisAngle(axis, rad) * Matrix4x4.CreateTranslation(piv);
                m.Translation = piv; o.Transform = m;
                break;
            }
        }
        SelectionChanged?.Invoke(o);
        _gl.Invalidate();
    }

    /// <summary>Position along the line (origin, axis) closest to the mouse ray; when the axis points at the camera, the
    /// mouse movement projected on the axis' screen direction.</summary>
    float AxisParam(Point mouse, Vector3 origin, Vector3 axis)
    {
        var (o, d) = Ray(mouse);
        float b = Vector3.Dot(axis, d), den = 1 - b * b;
        var w0 = origin - o;
        if (den > 0.02f) return (b * Vector3.Dot(d, w0) - Vector3.Dot(axis, w0)) / den;
        var s0 = ToScreen(origin); var s1 = ToScreen(origin + axis);
        if (s0 == null || s1 == null) return 0;
        var sd = s1.Value - s0.Value; float l2 = Math.Max(1e-3f, sd.LengthSquared());
        return Vector2.Dot(new Vector2(mouse.X, mouse.Y) - s0.Value, sd) / l2;
    }

    // ------------------------------------------------------------------ input

    void Tick()
    {
        if (!_gl.Focused || _keys.Count == 0 || _xf != XfKind.None) return;
        float speed = (_keys.Contains(Keys.ShiftKey) ? 4f : 1f) * 1.2f;
        if (_keys.Contains(Keys.ControlKey)) return;
        var f = Forward(); var r = Right(); var d = Vector3.Zero;
        // S is Scale while something is selected (Blender); fly backwards with S while looking (right button) or with Down
        bool sFlies = _looking || Selected == null || Selected.Kind == SceneObjectKind.Terrain;
        if (_keys.Contains(Keys.W) || _keys.Contains(Keys.Up)) d += f;
        if ((_keys.Contains(Keys.S) && sFlies) || _keys.Contains(Keys.Down)) d -= f;
        if (_keys.Contains(Keys.D) || _keys.Contains(Keys.Right)) d += r;
        if (_keys.Contains(Keys.A) || _keys.Contains(Keys.Left)) d -= r;
        if (_keys.Contains(Keys.E) || _keys.Contains(Keys.PageUp)) d += Vector3.UnitY;
        if (_keys.Contains(Keys.Q) || _keys.Contains(Keys.PageDown)) d -= Vector3.UnitY;
        if (d != Vector3.Zero) { _camPos += d * speed; _gl.Invalidate(); }
    }

    void OnKey(KeyEventArgs e)
    {
        if (_xf != XfKind.None)
        {
            e.Handled = true;
            switch (e.KeyCode)
            {
                case Keys.X: if (!_xfDrag) SetAxis(0); else UpdateTransform(); return;
                case Keys.Y: if (!_xfDrag) SetAxis(1); else UpdateTransform(); return;
                case Keys.Z: if (!_xfDrag) SetAxis(2); else UpdateTransform(); return;
                case Keys.Escape: CancelTransform(); return;
                case Keys.Return: if (!_xfDrag) ConfirmTransform(); return;
                case Keys.G: if (!_xfDrag) BeginTransform(XfKind.Grab, false); return;
                case Keys.R: if (!_xfDrag) BeginTransform(XfKind.Rotate, false); return;
                case Keys.S: if (!_xfDrag) BeginTransform(XfKind.Scale, false); return;
                case Keys.Back: if (_xfTyped.Length > 0) _xfTyped = _xfTyped[..^1]; UpdateTransform(); return;
                case Keys.OemMinus: case Keys.Subtract: _xfTyped = _xfTyped.StartsWith('-') ? _xfTyped[1..] : "-" + _xfTyped; UpdateTransform(); return;
                case Keys.OemPeriod: case Keys.Decimal: if (!_xfTyped.Contains('.')) _xfTyped += "."; UpdateTransform(); return;
                case Keys.ControlKey: UpdateTransform(); return;
            }
            if (!_xfDrag && e.KeyCode is >= Keys.D0 and <= Keys.D9) { _xfTyped += (char)('0' + (e.KeyCode - Keys.D0)); UpdateTransform(); return; }
            if (!_xfDrag && e.KeyCode is >= Keys.NumPad0 and <= Keys.NumPad9) { _xfTyped += (char)('0' + (e.KeyCode - Keys.NumPad0)); UpdateTransform(); return; }
            return;
        }
        if (e.Control || e.Alt) return;
        bool canEdit = Selected != null && Selected.Kind != SceneObjectKind.Terrain && !_looking;
        switch (e.KeyCode)
        {
            case Keys.F when Selected != null: Focus(Selected); break;
            case Keys.D1: Mode = GizmoMode.Move; _gl.Invalidate(); break;
            case Keys.D2: Mode = GizmoMode.Rotate; _gl.Invalidate(); break;
            case Keys.D3: Mode = GizmoMode.Scale; _gl.Invalidate(); break;
            case Keys.G when canEdit: BeginTransform(XfKind.Grab, false); e.Handled = true; break;
            case Keys.R when canEdit: BeginTransform(XfKind.Rotate, false); e.Handled = true; break;
            case Keys.S when canEdit: BeginTransform(XfKind.Scale, false); e.Handled = true; break;
            case Keys.Z when e.Shift: ViewMode = (ViewMode)(((int)_viewMode + 1) % 4); break;
            case Keys.Escape: Select(null); break;
        }
    }

    void OnWheel(object? s, MouseEventArgs e)
    {
        if (_xf != XfKind.None) return;
        _camPos += Forward() * (e.Delta / 120f) * 8f * (_keys.Contains(Keys.ShiftKey) ? 4 : 1); _gl.Invalidate();
    }

    void OnMouseDown(object? s, MouseEventArgs e)
    {
        _gl.Focus();
        _lastMouse = _mouse = e.Location;
        if (_xf != XfKind.None && !_xfDrag)
        {
            // modal transform: left confirms, right cancels
            if (e.Button == MouseButtons.Left) ConfirmTransform();
            else if (e.Button == MouseButtons.Right) { CancelTransform(); _suppressRightUp = true; }
            return;
        }
        if (e.Button == MouseButtons.Left)
        {
            var br = BarRect;
            if (br.Contains(e.Location)) { ViewMode = (ViewMode)Math.Clamp((e.X - br.X) / BarSeg, 0, 3); return; }
            if (_viewMode == ViewMode.Rendered && LightRect.Contains(e.Location)) { NextLight(); return; }
        }
        if (e.Button == MouseButtons.Right) { _looking = true; _dragMoved = false; }
        else if (e.Button == MouseButtons.Middle) _panning = true;
        else if (e.Button == MouseButtons.Left)
        {
            int handle = HandleAt(e.Location);
            var kind = Mode switch { GizmoMode.Move => XfKind.Grab, GizmoMode.Rotate => XfKind.Rotate, GizmoMode.Scale => XfKind.Scale, _ => XfKind.None };
            if (handle >= 0 && kind != XfKind.None)
            {
                BeginTransform(kind, drag: true, handle, Mode == GizmoMode.Move ? AxisSpace.World : AxisSpace.Local);
                _dragMoved = false;
                return;
            }
            var hit = Pick(e.Location);
            if (hit.Obj != null && hit.Obj == Selected && Selected.Kind != SceneObjectKind.Terrain && kind != XfKind.None)
            {
                BeginTransform(kind, drag: true);
                _dragMoved = false;
                UpdateTransform();
            }
            else Select(hit.Obj);
        }
    }

    void OnMouseUp(object? s, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            _looking = false;
            if (_suppressRightUp) { _suppressRightUp = false; return; }
            if (!_dragMoved) { var hit = Pick(e.Location); if (hit.Obj != null) Select(hit.Obj); ContextMenuRequested?.Invoke(hit.Obj ?? Selected, e.Location); }
        }
        if (e.Button == MouseButtons.Middle) _panning = false;
        if (e.Button == MouseButtons.Left && _xf != XfKind.None && _xfDrag)
        {
            if (_dragMoved) ConfirmTransform(); else CancelTransform();
        }
    }

    void OnMouseMove(object? s, MouseEventArgs e)
    {
        int dx = e.X - _lastMouse.X, dy = e.Y - _lastMouse.Y;
        _lastMouse = _mouse = e.Location;
        if (_looking)
        {
            if (Math.Abs(dx) + Math.Abs(dy) > 1) _dragMoved = true;
            _yaw -= dx * 0.006f;   // yaw grows to the left in right-handed space
            _pitch = Math.Clamp(_pitch - dy * 0.006f, -1.55f, 1.55f);
            _gl.Invalidate();
        }
        else if (_panning) { _camPos += (-Right() * dx + Vector3.UnitY * dy) * 0.25f; _gl.Invalidate(); }
        else if (_xf != XfKind.None)
        {
            if (_xfDrag && (Math.Abs(e.X - _xfMouse0.X) + Math.Abs(e.Y - _xfMouse0.Y) > 2)) _dragMoved = true;
            UpdateTransform();
        }
        else
        {
            var br = BarRect;
            int hb = br.Contains(e.Location) ? Math.Clamp((e.X - br.X) / BarSeg, 0, 3) : -1;
            int hh = hb < 0 ? HandleAt(e.Location) : -1;
            if (hb != _hoverBar || hh != _hoverHandle) { _hoverBar = hb; _hoverHandle = hh; _gl.Invalidate(); }
        }
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

    /// <summary>Intersects the mouse ray with the plane through <paramref name="through"/> with normal <paramref name="n"/>.</summary>
    Vector3? RayPlane(Point p, Vector3 through, Vector3 n)
    {
        var (o, d) = Ray(p);
        float den = Vector3.Dot(d, n);
        if (MathF.Abs(den) < 1e-4f) return null;
        float t = Vector3.Dot(through - o, n) / den;
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
        if (disposing) { _timer.Dispose(); if (_ready) { _gl.MakeCurrent(); _r.DeleteOverlay(_barOv); _r.DeleteOverlay(_hudOv); _r.Dispose(); } }
        base.Dispose(disposing);
    }
}
