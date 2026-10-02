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

    /// <summary>Objects placed by markers drawn with their own model (props such as L.O.G.'s palace, the Jiggy bank and
    /// world doors, characters, collectables — <see cref="SceneObject.ModelSource"/>). Off: they are marker boxes.</summary>
    public bool ShowObjects { get => _showObjects; set { if (_showObjects != value) { _showObjects = value; _linesVersion++; _gl.Invalidate(); } } }
    bool _showObjects = true;

    /// <summary>Distance culling as in the game: an object whose models all end their LOD tables with an empty level is not
    /// drawn when its view depth divided by its scale exceeds that distance (<see cref="SceneObject.CullDistance"/>). The
    /// selection is always drawn. Off with --no-cull.</summary>
    public bool LodCulling = true;

    bool BeyondCullDistance(SceneObject o, Vector3 centre)
    {
        if (!LodCulling || float.IsPositiveInfinity(o.CullDistance) || o == Selected) return false;
        var t = o.Transform;
        float scale = MathF.Max(new Vector3(t.M11, t.M12, t.M13).Length(), 1e-3f);
        return Vector3.Dot(centre - _camPos, Forward()) / scale > o.CullDistance;
    }

    /// <summary>Whether an object is drawn with its model this frame (terrain / scenery / marker objects toggles, scenery
    /// hidden by the tool unless selected).</summary>
    bool DrawsModel(SceneObject o, bool keepSelected = true) => o.Visible && o.Model != null && o.Kind switch
    {
        SceneObjectKind.Terrain => ShowTerrain,
        SceneObjectKind.Scenery => ShowScenery && ((keepSelected && o == Selected) || !IsHidden(o)),
        _ => _showObjects,
    };
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
    /// <summary>S scales the selection (Blender). Off: S always flies backwards (Settings).</summary>
    public bool SScales = true;
    /// <summary>The S key held now was pressed while flying, so it keeps flying backwards until released.</summary>
    bool _sFlies;
    /// <summary>When the camera last flew (keys) or looked around (right button): G / R / S right after that are
    /// still part of flying, not the start of a transform.</summary>
    DateTime _lastFly = DateTime.MinValue;
    static readonly Keys[] FlyKeys = { Keys.W, Keys.A, Keys.D, Keys.Q, Keys.E, Keys.Up, Keys.Down, Keys.Left, Keys.Right, Keys.PageUp, Keys.PageDown };
    bool Flying => _looking || FlyKeys.Any(_keys.Contains) || (DateTime.UtcNow - _lastFly).TotalMilliseconds < 600;

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
        _gl.KeyDown += (_, e) => { bool fresh = _keys.Add(e.KeyCode); OnKey(e, fresh); };
        _gl.KeyUp += (_, e) => { _keys.Remove(e.KeyCode); if (e.KeyCode == Keys.S) _sFlies = false; if (_xf != XfKind.None && _xfDrag) UpdateTransform(); };
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
    /// <summary>The level scripts' light setups and sky domes (see <see cref="WorldLooks"/>), parallel to <see cref="_lights"/>
    /// (null entries: a light setup found only by name).</summary>
    List<WorldLook?> _looks = new();
    public string LightingName => _lights is { Count: > 0 } ? _lights[_lightIndex].Name : "default";
    /// <summary>Name of the sky dome drawn (null: none).</summary>
    public string? SkyName { get; private set; }
    bool _skyFollows = true;

    void EnsureLighting()
    {
        if (_lights != null || Scene == null) return;
        _lights = new(); _looks = new();
        // 1. the level scripts: light setup + skydome per act / time of day (domes may live in other bundles or Bundle/50)
        try
        {
            var idx = NB.Core.Project.AssetIndex.LoadOrBuild(Scene.Workspace);
            foreach (var look in WorldLooks.Find(Scene.Workspace, idx, Scene, m => Scene.Log.Add(m)))
            {
                if (look.Light == null && look.Dome == null) continue;
                _lights.Add(look.Light ?? new LevelLighting { Name = look.Script.Replace("aid_script_banjox_", ""), Ambient = new Vector3(0.35f), Sun = Vector3.One, Elevation = 0.8f, Azimuth = -2f, Intensity = 1.1f });
                _looks.Add(look);
                if (look.Dome != null && !_skies.Any(k => k.Model == look.Dome)) _skies.Add((look.DomeName ?? "sky", look.Dome));
            }
        }
        catch (Exception e) { Scene.Log.Add("lighting: " + e.Message); }
        // 2. light setups and *skydome* models stored in the world / act bundles (worlds without level scripts)
        var caffs = new List<NB.Core.Formats.CaffFile> { Scene.Caff };
        foreach (var b in Scene.MarkerBundles) try { caffs.Add(Scene.Workspace.LoadResident(b)); } catch { }
        foreach (var c in caffs)
        {
            try
            {
                foreach (var l in LevelLighting.All(c).Where(l => !_lights.Any(x => x.Name == l.Name))) { _lights.Add(l); _looks.Add(null); }
            }
            catch { }
            for (int s = 1; s <= c.Symbols.Count; s++)
            {
                var n = c.Symbols[s - 1];
                if (!n.StartsWith("aid_model_") || !n.Contains("skydome")) continue;
                var dn = NB.Core.Formats.AssetIds.DisplayName(n);
                if (_skies.Any(k => k.Name == dn)) continue;
                try { var m = ModelAsset.Parse(c, s); if (m.Draws.Count > 0) _skies.Add((dn, m)); } catch { }
            }
        }
        _lightIndex = 0;
        ApplyLight();
    }

    void ApplyLight()
    {
        if (_lights is { Count: > 0 })
        {
            var l = _lights[_lightIndex];
            _r.Lighting = SceneLighting.From(l);
            var look = _lightIndex < _looks.Count ? _looks[_lightIndex] : null;
            _skyFollows = look?.DomeFollowsCamera ?? true;
            if (look?.Dome != null) { _sky = look.Dome; SkyName = look.DomeName; }
            else if (look != null && look.DomeId == 0 && look.Script.Length > 0 && _skies.Count == 0) { _sky = null; SkyName = null; }
            else
            {
                // light setups found by name only: the dome of the phase by name (midday uses the afternoon dome model in Showdown Town)
                string phase = l.Name[(l.Name.LastIndexOf('_') + 1)..];
                string[] prefs = phase switch
                {
                    "main" => new[] { "afternoon", "day", "bluesky", "blue", "sunrise", "morning" },
                    "afternoon" => new[] { "evening", "sunset", "afternoon" },
                    _ => new[] { phase },
                };
                var pick = prefs.Select(p => _skies.FirstOrDefault(s => s.Name.Contains(p))).FirstOrDefault(s => s.Model != null);
                if (pick.Model == null) pick = _skies.FirstOrDefault();
                _sky = pick.Model; SkyName = pick.Model != null ? pick.Name : null;
            }
        }
        else { _r.Lighting = new SceneLighting(); var pick = _skies.FirstOrDefault(); _sky = pick.Model; SkyName = pick.Model != null ? pick.Name : null; }
        _r.InvalidateShadow();
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
            string? skyTex = (_sky != null ? new[] { _sky } : _skies.Select(sk => sk.Model)).Select(m => m?.Draws.SelectMany(d => d.Textures).Select(t => t.Texture).FirstOrDefault(t => !t.StartsWith('#'))).FirstOrDefault(t => t != null);
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

    public void SetScene(WorldScene? scene, bool keepCamera = false)
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
            if (!keepCamera) { _camPos = c + new Vector3(0, 120, -250); _yaw = 0; _pitch = -0.4f; }
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
            _r.Begin(vp, _camPos, Right(), Vector3.Normalize(Vector3.Cross(Right(), Forward())));
            if (_viewMode is ViewMode.Rendered or ViewMode.Textured && _sky != null)
            {
                if (_skyFollows)
                {
                    // sky dome around the camera: unlit, behind everything
                    GL.DepthMask(false); GL.Disable(EnableCap.DepthTest);
                    _r.DrawModel(_sky, Matrix4x4.CreateTranslation(_camPos), Vector4.Zero, null, unlit: true);
                    GL.Enable(EnableCap.DepthTest); GL.DepthMask(true);
                }
                else _r.DrawModel(_sky, Matrix4x4.Identity, Vector4.Zero, null, unlit: true);   // a world-size dome: depth-tested, hides what lies beyond it
            }
            // culling: whole objects and model pieces outside the view, or smaller than ~a pixel; scenery the tool hid
            // (sunk below the level / shrunk to nothing) is skipped unless selected
            float minSize = MathF.Tan(_fov * MathF.PI / 360) / Math.Max(1, H) * 0.75f;
            var fr = new Renderer.Frustum(vp, _camPos, NoCull ? 0 : minSize);
            foreach (var o in Scene.Objects)
            {
                if (!DrawsModel(o)) continue;
                if (!NoCull && o.Kind != SceneObjectKind.Terrain) { var (wc, wr) = WorldBounds(o); if (!fr.Visible(wc, wr) || BeyondCullDistance(o, wc)) continue; }
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
        // casters further than ShadowReach towards the sun from the view's ground level are clipped
        var lvp = lv * Matrix4x4.CreateOrthographic(2 * R, 2 * R, Math.Max(1, 1500 - ShadowReach), 3500);
        var fr = new Renderer.Frustum(lvp, eye, 0);
        _r.BeginShadow(lvp);
        foreach (var o in Scene!.Objects)
        {
            if (!DrawsModel(o, keepSelected: false)) continue;
            if (o.Kind != SceneObjectKind.Terrain) { var (wc, wr) = WorldBounds(o); if (!fr.Visible(wc, wr) || (!NoCull && BeyondCullDistance(o, wc))) continue; }
            if (ShadowCasters == 1 && o.Kind == SceneObjectKind.Terrain || ShadowCasters == 2 && o.Kind != SceneObjectKind.Terrain) continue;
            _r.DrawShadow(o.Model, o.Transform, fr);
            foreach (var (cm, cl) in o.Children) _r.DrawShadow(cm, cl * o.Transform, fr);
        }
        _r.EndShadow(W, H);
    }

    /// <summary>Debug: 0 every object casts, 1 scenery only, 2 terrain only.</summary>
    public int ShadowCasters;
    /// <summary>How far towards the sun (from the view's ground level) shadow casters are taken: the game's cascades do not
    /// include high geometry (Nutty Acres' cloud rings 250+ units up cast no shadow in the game).</summary>
    public float ShadowReach = 250;

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
            foreach (var o in Scene.Objects.Where(o => o.Visible && (o.Model == null || (o.Kind == SceneObjectKind.Marker && !_showObjects)) && (o.Kind != SceneObjectKind.Marker || ShowMarkers)))
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

    // NB Studio's own look (the start page and tour): charcoal panels, orange accent, nut-and-bolt shapes
    static readonly Color BarBg = Color.FromArgb(232, 28, 30, 38), BarEdge = Color.FromArgb(255, 74, 78, 92),
        BarHover = Color.FromArgb(255, 50, 53, 66), Accent = Color.FromArgb(255, 242, 140, 40), AccentHi = Color.FromArgb(255, 255, 186, 102),
        BarText = Color.FromArgb(255, 222, 224, 232), AccentText = Color.FromArgb(255, 28, 20, 12);

    Bitmap DrawBar(bool showLight)
    {
        int h = showLight ? BarH + 4 + LightH : BarH;
        var bmp = new Bitmap(BarSeg * 4, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);
        using var bg = new SolidBrush(BarBg);
        using var edge = new Pen(BarEdge, 1f);
        using var path = Rounded(new Rectangle(0, 0, BarSeg * 4 - 1, BarH - 1), 4);
        g.FillPath(bg, path); g.DrawPath(edge, path);
        using var font = new Font("Segoe UI Semibold", 8.5f);
        using var sep = new Pen(Color.FromArgb(255, 56, 59, 72), 1f);
        for (int i = 0; i < 4; i++)
        {
            var r = new Rectangle(i * BarSeg + 3, 3, BarSeg - 6, BarH - 7);
            bool on = (int)_viewMode == i, hover = _hoverBar == i;
            if (i > 0 && !on && (int)_viewMode != i - 1) g.DrawLine(sep, i * BarSeg, 6, i * BarSeg, BarH - 7);
            if (on)
            {
                using var p2 = Rounded(r, 3);
                using var b2 = new System.Drawing.Drawing2D.LinearGradientBrush(r, AccentHi, Accent, 90f);
                g.FillPath(b2, p2);
            }
            else if (hover) { using var p2 = Rounded(r, 3); using var b2 = new SolidBrush(BarHover); g.FillPath(b2, p2); }
            DrawModeIcon(g, i, new RectangleF(r.X + 5, r.Y + 2.5f, 15, 15), on);
            using var tb = new SolidBrush(on ? AccentText : BarText);
            g.DrawString(ModeLabels[i], font, tb, r.X + 23, r.Y + 2);
        }
        if (showLight)
        {
            var lr = new Rectangle(0, BarH + 4, BarSeg * 4 - 1, LightH - 1);
            using var lp = Rounded(lr, 4); g.FillPath(bg, lp); g.DrawPath(edge, lp);
            using var f2 = new Font("Segoe UI", 8f);
            using var ab = new SolidBrush(Accent);
            using var tb = new SolidBrush(BarText);
            g.DrawString("☀", f2, ab, 7, BarH + 7);
            string text = $"Light: {LightingName}" + (_lights is { Count: > 1 } ? "   ›  click for next" : "");
            g.DrawString(text, f2, tb, 22, BarH + 7);
        }
        return bmp;
    }

    /// <summary>The view-mode icons: a wire cube, a shaded cube, a picture, and a sun.</summary>
    static void DrawModeIcon(Graphics g, int mode, RectangleF r, bool on)
    {
        var fg = on ? AccentText : Color.FromArgb(255, 214, 217, 226);
        float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, s = r.Width / 2;
        // an isometric cube: top, left and right faces around the centre corner
        PointF P(float x, float y) => new(cx + x * s, cy + y * s);
        PointF top = P(0, -1), tl = P(-0.87f, -0.5f), tr = P(0.87f, -0.5f), mid = P(0, 0), bl = P(-0.87f, 0.5f), br = P(0.87f, 0.5f), bot = P(0, 1);
        switch (mode)
        {
            case 0:
            {
                using var pen = new Pen(fg, 1.25f) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
                g.DrawPolygon(pen, new[] { top, tr, br, bot, bl, tl });
                g.DrawLine(pen, tl, mid); g.DrawLine(pen, tr, mid); g.DrawLine(pen, mid, bot);
                using var dash = new Pen(Color.FromArgb(150, fg), 1f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
                // the hidden corner sits behind the front one: its three edges dotted
                g.DrawLine(dash, mid, bl); g.DrawLine(dash, mid, br); g.DrawLine(dash, mid, top); break;
            }
            case 1:
            {
                using var t = new SolidBrush(on ? Color.FromArgb(255, 250, 238, 222) : Color.FromArgb(255, 226, 228, 234));
                using var lft = new SolidBrush(on ? Color.FromArgb(255, 150, 96, 48) : Color.FromArgb(255, 150, 154, 166));
                using var rgt = new SolidBrush(on ? Color.FromArgb(255, 96, 58, 26) : Color.FromArgb(255, 100, 104, 118));
                g.FillPolygon(t, new[] { top, tr, mid, tl });
                g.FillPolygon(lft, new[] { tl, mid, bot, bl });
                g.FillPolygon(rgt, new[] { mid, tr, br, bot });
                break;
            }
            case 2:
            {
                // a picture: frame, a sun and two hills
                var fr = new RectangleF(r.X + 0.5f, r.Y + 2, r.Width - 1, r.Height - 4);
                using var sky = new SolidBrush(on ? Color.FromArgb(255, 255, 236, 200) : Color.FromArgb(255, 92, 140, 196));
                using (var p = Rounded(Rectangle.Round(fr), 2)) g.FillPath(sky, p);
                using var hill = new SolidBrush(on ? Color.FromArgb(255, 120, 70, 28) : Color.FromArgb(255, 108, 168, 82));
                g.FillPolygon(hill, new[] { new PointF(fr.X, fr.Bottom), new PointF(fr.X + fr.Width * 0.38f, fr.Y + fr.Height * 0.42f), new PointF(fr.X + fr.Width * 0.62f, fr.Y + fr.Height * 0.72f),
                    new PointF(fr.X + fr.Width * 0.78f, fr.Y + fr.Height * 0.55f), new PointF(fr.Right, fr.Y + fr.Height * 0.8f), new PointF(fr.Right, fr.Bottom) });
                using var sun = new SolidBrush(on ? Color.FromArgb(255, 200, 90, 20) : Color.FromArgb(255, 255, 214, 110));
                g.FillEllipse(sun, fr.Right - 5.5f, fr.Y + 1.5f, 3.5f, 3.5f);
                using var pen = new Pen(fg, 1.1f);
                using (var p = Rounded(Rectangle.Round(fr), 2)) g.DrawPath(pen, p);
                break;
            }
            default:
            {
                // a sun: disc and rays
                using var pen = new Pen(on ? AccentText : Color.FromArgb(255, 255, 196, 92), 1.4f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
                for (int k = 0; k < 8; k++)
                {
                    double a = k * Math.PI / 4;
                    g.DrawLine(pen, cx + (float)Math.Cos(a) * s * 0.62f, cy + (float)Math.Sin(a) * s * 0.62f, cx + (float)Math.Cos(a) * s * 0.98f, cy + (float)Math.Sin(a) * s * 0.98f);
                }
                using var disc = new SolidBrush(on ? AccentText : Color.FromArgb(255, 255, 176, 64));
                g.FillEllipse(disc, cx - s * 0.42f, cy - s * 0.42f, s * 0.84f, s * 0.84f);
                break;
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

    void Tick() { if (_gl.Focused) TickCore(); }

    void TickCore()
    {
        if (_keys.Count == 0 || _xf != XfKind.None) return;
        float speed = (_keys.Contains(Keys.ShiftKey) ? 4f : 1f) * 1.2f;
        if (_keys.Contains(Keys.ControlKey)) return;
        var f = Forward(); var r = Right(); var d = Vector3.Zero;
        // S is Scale while something is selected (Blender), but while flying (right button held, other fly keys held,
        // or flying a moment ago) S flies backwards
        bool sFlies = _sFlies || !SScales || _looking || Selected == null || Selected.Kind == SceneObjectKind.Terrain;
        if (_keys.Contains(Keys.W) || _keys.Contains(Keys.Up)) d += f;
        if ((_keys.Contains(Keys.S) && sFlies) || _keys.Contains(Keys.Down)) d -= f;
        if (_keys.Contains(Keys.D) || _keys.Contains(Keys.Right)) d += r;
        if (_keys.Contains(Keys.A) || _keys.Contains(Keys.Left)) d -= r;
        if (_keys.Contains(Keys.E) || _keys.Contains(Keys.PageUp)) d += Vector3.UnitY;
        if (_keys.Contains(Keys.Q) || _keys.Contains(Keys.PageDown)) d -= Vector3.UnitY;
        if (d != Vector3.Zero) { _camPos += d * speed; _lastFly = DateTime.UtcNow; _gl.Invalidate(); }
    }

    void OnKey(KeyEventArgs e, bool fresh = true)
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
        // G / R / S start a transform only on a fresh key press (not key repeat) and never while flying, so holding S to
        // fly back and letting go of the right button can't turn into a scale
        bool flying = Flying;
        if (e.KeyCode == Keys.S && fresh && (flying || !SScales)) _sFlies = true;
        bool canEdit = Selected != null && Selected.Kind != SceneObjectKind.Terrain && fresh && !flying && !_sFlies;
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
            if (_dragMoved) _lastFly = DateTime.UtcNow;
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
            bool asModel = o.Model != null && (o.Kind != SceneObjectKind.Marker || _showObjects);   // marker objects: their mesh
            if (o.Kind == SceneObjectKind.Marker && !asModel && !ShowMarkers) continue;
            if (!Matrix4x4.Invert(o.Transform, out var inv)) continue;
            var lo = Vector3.Transform(ro, inv); var ld = Vector3.TransformNormal(rd, inv);
            if (!RayBox(lo, ld, o.BoundsMin, o.BoundsMax, out float tb) || tb > bestT) continue;
            float t = !asModel ? tb : RayMesh(lo, ld, o.Model!, bestT);
            if (asModel)
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
