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
/// Blender-style keys on the selection: G grab (move on the camera plane), R rotate, T scale (S always flies backwards:
/// scaling on S kept catching people who flew backwards, NB Studio 1.12); then X / Y / Z constrain to
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
    /// <summary>
    /// Collision of every object (the toggle next to the view-mode bar, View > Collision): the Havok collision of the terrain,
    /// of every scenery object with its nested models and of the objects placed by markers (props, buildings such as
    /// L.O.G.'s palace), as wireframes: terrain cyan, scenery yellow, marker objects green. It is decoded in the background
    /// the first time (<see cref="Scene"/>'s CollisionOf, cached per model); line batches are built a few per frame and
    /// only objects inside the view (and within their game draw distance) are drawn, at most <see cref="CollisionBudget"/>
    /// objects per frame, nearest first.
    /// </summary>
    public bool ShowCollision
    {
        get => _showColl;
        set
        {
            if (_showColl == value) return;
            _showColl = value;
            if (value) StartCollisionDecode();
            ShowCollisionChanged?.Invoke(value);
            _gl.Invalidate();
        }
    }
    bool _showColl;
    public event Action<bool>? ShowCollisionChanged;
    /// <summary>Progress / result of decoding the scene's collision (for the log).</summary>
    public event Action<string>? CollisionInfo;
    /// <summary>Most objects whose collision is drawn in one frame.</summary>
    public int CollisionBudget = 2500;
    /// <summary>Per object: its collision shapes (Havok asset, meshes in model space, placement in the object). Null while decoding.</summary>
    Dictionary<SceneObject, List<(string Asset, List<NB.Core.Havok.CollisionMesh> Meshes, Matrix4x4 Local)>>? _allColl;
    Task? _collTask; WorldScene? _collFor;
    public bool CollisionReady => _allColl != null;
    public string CollisionSummary { get; private set; } = "";
    int _collObjectsDrawn;

    void StartCollisionDecode()
    {
        var scene = Scene;
        if (scene == null || _collFor == scene) return;
        _collFor = scene; _allColl = null;
        _collTask = Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = new Dictionary<SceneObject, List<(string, List<NB.Core.Havok.CollisionMesh>, Matrix4x4)>>();
            long tris = 0; int shapes = 0;
            foreach (var o in scene.Objects)
            {
                if (o.Model == null) continue;
                List<(string Asset, List<NB.Core.Havok.CollisionMesh> Meshes, Matrix4x4 Local)> parts;
                lock (scene) parts = scene.CollisionOf(o);   // the selection overlay decodes on the UI thread too
                parts.RemoveAll(p => p.Meshes.Count == 0);
                if (parts.Count == 0) continue;
                res[o] = parts;
                shapes += parts.Count; tris += parts.Sum(p => p.Meshes.Sum(m => m.Triangles.Count / 3L));
            }
            return (res, $"collision of {res.Count:N0} objects ({shapes:N0} shapes, {tris:N0} triangles) decoded in {sw.Elapsed.TotalSeconds:F1} s");
        }).ContinueWith(t =>
        {
            if (t.IsFaulted) { BeginInvoke(() => CollisionInfo?.Invoke("collision decoding failed: " + t.Exception?.GetBaseException().Message)); return; }
            BeginInvoke(() =>
            {
                if (Scene != scene) return;
                _allColl = t.Result.res;
                CollisionSummary = t.Result.Item2;
                CollisionInfo?.Invoke(CollisionSummary);
                _barOv.Key = ""; _gl.Invalidate();
            });
        });
    }

    static Vector3 CollisionColour(SceneObjectKind k) => k switch
    {
        SceneObjectKind.Terrain => new(0.1f, 0.95f, 1f),
        SceneObjectKind.Scenery => new(1f, 0.9f, 0.15f),
        _ => new(0.35f, 1f, 0.45f),
    };

    void DrawAllCollision(Matrix4x4 vp, Renderer.Frustum fr)
    {
        if (_allColl == null) { if (_collFor != Scene) StartCollisionDecode(); return; }
        var fwd = Forward();
        var list = new List<(SceneObject O, float D)>();
        foreach (var (o, _) in _allColl)
        {
            if (!o.Visible || (o.Kind == SceneObjectKind.Scenery && IsHidden(o) && o != Selected)) continue;
            if (o.Kind != SceneObjectKind.Terrain && !NoCull)
            {
                var (wc, wr) = WorldBounds(o);
                if (!fr.Visible(wc, wr) || BeyondCullDistance(o, wc)) continue;
                list.Add((o, Vector3.Dot(wc - _camPos, fwd)));
            }
            else list.Add((o, -1));
        }
        list.Sort((a, b) => a.D.CompareTo(b.D));
        int drawn = 0; bool waiting = false;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var (o, _) in list)
        {
            if (drawn >= CollisionBudget) break;
            foreach (var (asset, meshes, local) in CollisionParts(o))
            {
                string key = (int)o.Kind + "|" + _collVer.GetValueOrDefault(asset) + "|" + asset;
                if (!_collision.TryGetValue(key, out var batch))
                {
                    // the wireframe of a collision mesh is worked out on a worker thread and uploaded when ready, a few
                    // milliseconds of uploads per frame: backing away (hundreds of new pieces in range) no longer stalls
                    // the frames that follow — before, up to 24 meshes were built per frame on the UI thread (up to
                    // 130 ms frames, which made the first moments of a drag lag)
                    if (!_collPending.TryGetValue(key, out var task)) _collPending[key] = task = Task.Run(() => CollisionSegments(meshes));
                    if (!task.IsCompleted || System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds > 3) { waiting = true; continue; }
                    _collPending.Remove(key);
                    batch = _r.CreateLineBatch(task.Result, CollisionColour(o.Kind));
                    _collision[key] = batch;
                }
                _r.DrawLineBatch(batch, local * o.Transform * vp);
            }
            drawn++;
        }
        if (waiting) RedrawSoon();
        _collObjectsDrawn = drawn;
    }
    /// <summary>Draw path-node links (marker type 22: record +8 = next node index).</summary>
    public bool ShowPaths = true;
    static readonly bool DebugClicks = Environment.GetEnvironmentVariable("NB_STUDIO_DEBUG_CLICKS") == "1";
    /// <summary>Debug: draw everything (no frustum / size culling), to compare against the culled frame.</summary>
    public bool NoCull;
    readonly Dictionary<string, Renderer.LineBatch> _collision = new();
    /// <summary>Collision wireframes being worked out on worker threads (key as in <see cref="_collision"/>).</summary>
    readonly Dictionary<string, Task<List<(Vector3, Vector3)>>> _collPending = new();

    /// <summary>The edges of collision meshes, each once.</summary>
    static List<(Vector3, Vector3)> CollisionSegments(List<NB.Core.Havok.CollisionMesh> meshes)
    {
        var segs = new List<(Vector3, Vector3)>(); var seen = new HashSet<(int, int, int)>();
        for (int mi = 0; mi < meshes.Count; mi++)
        {
            var m = meshes[mi];
            for (int t = 0; t + 2 < m.Triangles.Count; t += 3)
                for (int e = 0; e < 3; e++)
                {
                    int a = m.Triangles[t + e], b = m.Triangles[t + (e + 1) % 3];
                    if (a < m.Positions.Count && b < m.Positions.Count && seen.Add((mi, Math.Min(a, b), Math.Max(a, b)))) segs.Add((m.Positions[a], m.Positions[b]));
                }
        }
        return segs;
    }

    System.Windows.Forms.Timer? _redrawSoon;
    /// <summary>One more frame in ~40 ms (work finishing on worker threads), without rendering back to back meanwhile.</summary>
    void RedrawSoon()
    {
        if (_redrawSoon == null) { _redrawSoon = new() { Interval = 40 }; _redrawSoon.Tick += (_, _) => { _redrawSoon.Stop(); _gl.Invalidate(); }; }
        if (!_redrawSoon.Enabled) _redrawSoon.Start();
    }
    /// <summary>Draw the selected object's own Havok collision (magenta, on top), independent of View > Collision.</summary>
    public bool ShowSelectionCollision { get => _showSelColl; set { if (_showSelColl != value) { _showSelColl = value; _gl.Invalidate(); } } }
    bool _showSelColl;
    Renderer.LineBatch? _selCollBatch; SceneObject? _selCollFor;
    /// <summary>What the selection-collision overlay shows (asset names, triangles), for the status bar / scripts.</summary>
    public string SelectionCollisionInfo { get; private set; } = "";
    public event Action<string>? SelectionCollisionChanged;

    void DrawSelectionCollision(Matrix4x4 vp)
    {
        if (!_showSelColl || Selected == null || Scene == null) return;
        if (_selCollFor != Selected)
        {
            if (_selCollBatch != null) _r.DeleteLineBatch(_selCollBatch);
            _selCollBatch = null; _selCollFor = Selected;
            List<(string Asset, List<NB.Core.Havok.CollisionMesh> Meshes, Matrix4x4 Local)> parts;
            lock (Scene) parts = Scene.CollisionOf(Selected);   // the all-objects overlay decodes in the background
            var segs = new List<(Vector3, Vector3)>();
            int tris = 0;
            foreach (var (_, meshes, local) in parts)
                for (int mi = 0; mi < meshes.Count; mi++)
                {
                    var m = meshes[mi]; var seen = new HashSet<(int, int)>();
                    tris += m.Triangles.Count / 3;
                    for (int t = 0; t + 2 < m.Triangles.Count; t += 3)
                        for (int e = 0; e < 3; e++)
                        {
                            int a = m.Triangles[t + e], b = m.Triangles[t + (e + 1) % 3];
                            if (seen.Add((Math.Min(a, b), Math.Max(a, b)))) segs.Add((Vector3.Transform(m.Positions[a], local), Vector3.Transform(m.Positions[b], local)));
                        }
                }
            if (segs.Count > 0) _selCollBatch = _r.CreateLineBatch(segs, new Vector3(1f, 0.2f, 0.85f));
            SelectionCollisionInfo = parts.Count == 0 ? $"{Selected.Name}: no collision (no aid_havok asset for its model)"
                : tris == 0 ? $"{Selected.Name}: {string.Join(", ", parts.Select(p => p.Asset.Replace("aid_havok_banjox_", "")).Distinct().Take(3))} holds no shapes (characters and other actors get their physics body at run time)"
                : $"{Selected.Name}: {tris:N0} collision triangles in {parts.Count} shape(s): {string.Join(", ", parts.Select(p => p.Asset.Replace("aid_havok_banjox_", "")).Distinct().Take(4))}";
            SelectionCollisionChanged?.Invoke(SelectionCollisionInfo);
        }
        if (_selCollBatch != null) _r.DrawLineBatch(_selCollBatch, Selected.Transform * vp, onTop: true);
    }
    public event Action<SceneObject?>? SelectionChanged;
    public event Action<SceneObject>? ObjectEdited;   // after a transform was confirmed (for undo)
    public event Action<SceneObject, Matrix4x4>? EditStarted;
    public event Action<SceneObject?, Point>? ContextMenuRequested;
    /// <summary>Right click in Edit Collision mode: the collision under the mouse (or null) and the view pixel.</summary>
    public event Action<CollisionHit?, Point>? CollisionContextMenuRequested;
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
        // flying moves on every frame by the real elapsed time: the 16 ms timer alone starves while the mouse looks
        // around (WM_TIMER only arrives when no mouse / paint messages wait), so looking stopped the flight
        _gl.Paint += (_, _) => { if (_gl.Focused) TickCore(FlySteps()); Render(); };
        _gl.Resize += (_, _) => _gl.Invalidate();
        _gl.MouseDown += OnMouseDown; _gl.MouseUp += OnMouseUp; _gl.MouseMove += OnMouseMove; _gl.MouseWheel += OnWheel;
        _gl.MouseLeave += (_, _) => { if (_hoverBar != -1 || _hoverHandle != -1) { _hoverBar = -1; _hoverHandle = -1; _gl.Invalidate(); } };
        _gl.KeyDown += (_, e) => { bool fresh = _keys.Add(e.KeyCode); OnKey(e, fresh); };
        _gl.KeyUp += (_, e) => { _keys.Remove(e.KeyCode); if (e.KeyCode == Keys.S) _sFlies = false; if (_xf != XfKind.None && _xfDrag) UpdateTransform(); };
        // the view takes plain keys (arrows, Esc, Enter, letters, digits) itself. Keys with Ctrl / Alt, the F keys and Del are
        // NOT input keys: an input key skips the form's ProcessCmdKey, so with the 3D view focused Ctrl+Z / Ctrl+Y / Ctrl+S /
        // F5 / Del (the menu and edit shortcuts) did nothing (NB Studio 1.6–1.7: "undo doesn't work" after clicking the view)
        _gl.PreviewKeyDown += (_, e) => e.IsInputKey = !e.Control && !e.Alt && e.KeyCode is not (>= Keys.F1 and <= Keys.F24) and not Keys.Delete and not Keys.Apps;
        _gl.LostFocus += (_, _) => _keys.Clear();
        _timer.Tick += (_, _) => Tick();
        _hoverTimer.Tick += (_, _) => HoverTick();
        _gl.MouseLeave += (_, _) => { _hoverTimer.Stop(); if (_tipFor != null) { _tipFor = null; _gl.Invalidate(); } };
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
        if (Scene != null) StartGrass();   // the grass shadow textures follow the time of day
        _gl.Invalidate();
        LightApplied?.Invoke(LightingName);
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
            AddSea(model, regions, skyTex);   // Nutty Acres' sea (SceneViewport.Visgroups.cs)
            return model.Draws.Count > 0 ? model : null;
        }
        catch (Exception e) { scene.Log.Add("water: " + e.Message); return null; }
    }

    // ------------------------------------------------------------------ scene

    public void SetScene(WorldScene? scene, bool keepCamera = false)
    {
        CancelTransform();
        if (_ready) { _gl.MakeCurrent(); _r.Clear(); foreach (var b in _collision.Values) _r.DeleteLineBatch(b); }
        _collision.Clear(); _collPending.Clear();
        if (_ready && _selCollBatch != null) _r.DeleteLineBatch(_selCollBatch);
        _selCollBatch = null; _selCollFor = null;
        if (_ready && _staticLines != null) _r.DeleteLineBatch(_staticLines);
        _staticLines = null; _linesVersion++;
        Scene = scene; Selected = null;
        if (scene != null)
            foreach (var o in scene.Objects)
                if (SpawnPoints.Is(o) && o.Model == null) { o.BoundsMin = SpawnBounds.Min; o.BoundsMax = SpawnBounds.Max; }

        _allColl = null; _collFor = null; CollisionSummary = "";
        _collVer.Clear(); _collSelVer++; _rectFrom = null;
        if (_showColl && scene != null) StartCollisionDecode();
        _lights = null; _skies.Clear(); _sky = null; _r.Lighting = new SceneLighting();
        _water = null; _r.MaterialOverrides.Clear();
        ResetGrass();
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

    /// <summary>Objects whose scale is not edited: markers (actors, pickups, path nodes, spawns…). The game places them by
    /// position and rotation; the record's scale field is kept as stored (path nodes use it as a node value, not a size),
    /// so the gizmo, S and the Properties scale fields leave it alone.</summary>
    public static bool ScaleLocked(SceneObject? o) => o != null && o.Kind == SceneObjectKind.Marker;

    /// <summary>Scenery hidden by the tool: sunk below y -10000, or scaled to (almost) nothing.</summary>
    public static bool IsHidden(SceneObject o)
    {
        var t = o.Transform;
        float scale = new Vector3(t.M11, t.M12, t.M13).Length();
        return t.Translation.Y < -10000 || scale < 0.01f;   // sunk by 20,000 (imported Source maps reach below y -200)
    }

    public void Select(SceneObject? o, bool focus = false)
    {
        if (o != Selected) CancelTransform();
        bool many = _extra.Count > 0;
        _extra.Clear();
        Selected = o;
        if (focus && o != null) Focus(o);
        SelectionChanged?.Invoke(o);
        if (many || o != null) SelectionSetChanged?.Invoke(SelectedObjects);
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

    long _lastFrame;
    int _frameCacheFbo, _frameCacheRb, _frameCacheW, _frameCacheH;
    (Vector3, float, float, int, int, ViewMode, int, bool, int, bool, bool, float, Vector3) _frameCacheKey;

    /// <summary>Copies the colour of the view to the frame cache (after the scene is drawn) or back.</summary>
    void CopyFrame(bool fromCache, int W, int H)
    {
        if (_frameCacheFbo == 0 || _frameCacheW != W || _frameCacheH != H)
        {
            if (_frameCacheFbo == 0) { _frameCacheFbo = GL.GenFramebuffer(); _frameCacheRb = GL.GenRenderbuffer(); }
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _frameCacheRb);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.Rgba8, W, H);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _frameCacheFbo);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, _frameCacheRb);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            _frameCacheW = W; _frameCacheH = H;
        }
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, fromCache ? _frameCacheFbo : 0);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, fromCache ? 0 : _frameCacheFbo);
        GL.BlitFramebuffer(0, 0, W, H, 0, 0, W, H, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    void Render(bool swap = true)
    {
        if (!_ready) return;
        _lastFrame = System.Diagnostics.Stopwatch.GetTimestamp();
        using var _p = Prof.Time(_xf != XfKind.None ? "render (transforming)" : "render");
        _gl.MakeCurrent();
        int W = _gl.Width, H = _gl.Height;
        if (W < 1 || H < 1) return;   // minimized
        GL.Viewport(0, 0, W, H);
        var cc = ClearColour(_viewMode);
        if (_viewMode == ViewMode.Rendered) { var f = _r.Lighting.FogColour; cc = new Vector4(f.X, f.Y, f.Z, 1); }
        GL.ClearColor(cc.X, cc.Y, cc.Z, cc.W);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        var view = View(); var vp = view * Proj();
        // dragging something that is drawn as lines only (a player start, a path node, any marker box): the rest of the
        // frame does not change, so it is drawn once and copied back for every mouse move (the far view of Showdown Town
        // takes 20-35 ms to draw; the copy well under 1 ms)
        bool cacheable = Scene != null && _xf != XfKind.None && !SelectedObjects.Any(o => DrawsModel(o));
        var ck = (_camPos, _yaw, _pitch, W, H, _viewMode, _linesVersion, ShowCollision, _collision.Count, ShowMarkers, ShowPaths, _fov, _r.Lighting.SunDirection);
        bool cacheHit = cacheable && _frameCacheKey.Equals(ck) && _frameCacheFbo != 0;
        if (cacheHit) using (Prof.Time("  render: cached scene")) CopyFrame(fromCache: true, W, H);
        if (Scene != null && !cacheHit)
        {
            if (_viewMode == ViewMode.Rendered && _r.Shadows)
            {
                // the sun's shadow map only depends on the camera, the sun and what casts shadows: dragging a marker
                // (a player start, a path node …) keeps the one of the frame before (the far view's shadow pass took ~15 ms)
                var sk = (_camPos, _yaw, _pitch, _r.Lighting.SunDirection, ShadowReach, ShadowCasters, NoCull, _r.ShadowCull);
                if (!(_xf != XfKind.None && _shadowKey == sk && !SelectedObjects.Any(o => DrawsModel(o))))
                    using (Prof.Time("  render: shadow map")) RenderShadowMap(W, H);
                _shadowKey = sk;
            }
            _r.Begin(vp, _camPos, Right(), Vector3.Normalize(Vector3.Cross(Right(), Forward())));
            if (_viewMode is ViewMode.Rendered or ViewMode.Textured && _sky != null && ShowSky)
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
            var _pobj = Prof.Time("  render: objects");
            foreach (var o in Scene.Objects)
            {
                if (!DrawsModel(o)) continue;
                if (!NoCull && o.Kind != SceneObjectKind.Terrain) { var (wc, wr) = WorldBounds(o); if (!fr.Visible(wc, wr) || BeyondCullDistance(o, wc)) continue; }
                var tint = IsSelected(o) ? new Vector4(1f, 0.55f, 0.1f, _viewMode == ViewMode.Wireframe ? 1f : 0.35f) : o.Dirty ? new Vector4(0.2f, 0.9f, 0.3f, 0.15f) : Vector4.Zero;
                _r.DrawModel(o.Model, o.Transform, tint, NoCull ? null : fr);
                foreach (var (cm, cl) in o.Children) _r.DrawModel(cm, cl * o.Transform, tint, NoCull ? null : fr);
            }
            if (_water != null && ShowWater && ShowTerrain && _viewMode is ViewMode.Textured or ViewMode.Rendered) _r.DrawModel(_water, Matrix4x4.Identity, Vector4.Zero, NoCull ? null : fr);
            _pobj.Dispose();
            _r.GrassTilesDrawn = 0;
            using (Prof.Time("  render: grass")) DrawGrassLayers(NoCull ? null : fr);
            using (Prof.Time("  render: transparent")) _r.FlushTransparent();
            GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
            if (ShowCollision) using (Prof.Time("collision overlay")) DrawAllCollision(vp, fr);
            var key = (_linesVersion, ShowMarkers, ShowPaths);
            if (_staticLines == null || _staticKey != key)
            {
                using var _pl = Prof.Time("marker line batch rebuild");
                if (_staticLines != null) _r.DeleteLineBatch(_staticLines);
                _staticLines = _r.CreateColoredLineBatch(StaticLines());
                _staticKey = key;
            }
            _r.DrawLineBatch(_staticLines, vp, onTop: true);
            if (cacheable) { CopyFrame(fromCache: false, W, H); _frameCacheKey = ck; } else _frameCacheKey = default;
        }
        if (Scene != null)
        {
            if (_movingLines.Count > 0) _r.Lines(MovingLines(), vp, true);
            DrawSpawnFigures(vp);
            DrawSelectionCollision(vp);
            if (ShowCollision) DrawCollisionSelection(vp);
            if (Selected != null) DrawGizmo(vp);
        }
        using (Prof.Time("  render: overlays")) DrawOverlays(W, H);
        if (swap) using (Prof.Time("  render: swap")) _gl.SwapBuffers();
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

    (Vector3, float, float, Vector3, float, int, bool, int)? _shadowKey;
    Renderer.LineBatch? _staticLines;
    (int, bool, bool) _staticKey;
    int _linesVersion;

    /// <summary>Marker boxes, scenery crosses and path links: rebuilt only when the scene or its markers change
    /// (<see cref="Refresh3D"/>), not every frame.</summary>
    List<(Vector3, Vector3, Vector3)> StaticLines() => MarkerLines(o => !_movingLines.Contains(o), (a, b) => !_movingLines.Contains(a) && !_movingLines.Contains(b));

    /// <summary>Markers being moved (with the path links that touch them): drawn every frame, so a drag does not rebuild
    /// the line batch of all markers on every mouse move (2–36 ms each in Showdown Town's 1,452 markers).</summary>
    readonly HashSet<SceneObject> _movingLines = new();

    List<(Vector3, Vector3, Vector3)> MovingLines() => MarkerLines(_movingLines.Contains, (a, b) => _movingLines.Contains(a) || _movingLines.Contains(b));

    List<(Vector3, Vector3, Vector3)> MarkerLines(Func<SceneObject, bool> box, Func<SceneObject, SceneObject, bool> link)
    {
        var lines = new List<(Vector3, Vector3, Vector3)>();
        if (Scene == null) return lines;
        {
            foreach (var o in Scene.Objects.Where(o => o.Visible && (o.Model == null || (o.Kind == SceneObjectKind.Marker && !_showObjects)) && (o.Kind != SceneObjectKind.Marker || ShowMarkers)))
            {
                if (!box(o)) continue;
                if (SpawnPoints.Is(o)) continue;   // drawn every frame, thicker: DrawSpawnFigures
                if (o.Kind == SceneObjectKind.Marker) AddBox(lines, o, MarkerColor(o.Marker!.Type));
                else AddCross(lines, o.Transform.Translation, 2, new Vector3(1, 0, 1));
            }
            if (ShowPaths)
            {
                var nodes = Scene.Objects.Where(o => o.Visible && o.Marker is { Type: 22 } && o.MarkerSet != null).ToList();
                var byKey = new Dictionary<(MarkerAsset, int), SceneObject>();
                foreach (var n in nodes) byKey.TryAdd((n.MarkerSet!, n.Marker!.Index), n);
                foreach (var n in nodes)
                    if (n.Marker!.Link != n.Marker.Index && byKey.TryGetValue((n.MarkerSet!, n.Marker.Link), out var next) && link(n, next))
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

    /// <summary>Colour of player start points (markers of type 4): bright green, unlike any other marker.</summary>
    public static readonly Vector3 SpawnColour = new(0.15f, 1f, 0.35f);

    /// <summary>The facing direction of a marker on the ground: its local +Z turned by its yaw (verified against the game
    /// camera behind the player at three starts, see <see cref="SpawnPoints"/>).</summary>
    static Vector3 Facing(SceneObject o)
    {
        var f = new Vector3(o.Transform.M31, 0, o.Transform.M33);
        return f.LengthSquared() > 1e-8f ? Vector3.Normalize(f) : Vector3.UnitZ;
    }

    /// <summary>A player start: a ring on the ground, an arrow the way Banjo faces, and a flag pole (see SpawnBounds).</summary>
    static void AddSpawnFigure(List<(Vector3, Vector3, Vector3)> l, SceneObject o)
    {
        var c = SpawnColour; var p = o.Transform.Translation; var f = Facing(o); var s = Vector3.Cross(Vector3.UnitY, f);
        const int N = 24; const float R = 1.6f;
        for (int i = 0; i < N; i++)
        {
            float a0 = i * MathF.Tau / N, a1 = (i + 1) * MathF.Tau / N;
            l.Add((p + (f * MathF.Cos(a0) + s * MathF.Sin(a0)) * R, p + (f * MathF.Cos(a1) + s * MathF.Sin(a1)) * R, c));
        }
        var tip = p + f * 5f + Vector3.UnitY * 0.05f; var b0 = p + Vector3.UnitY * 0.05f;
        l.Add((b0, tip, c)); l.Add((tip, tip - f * 1.4f + s * 0.9f, c)); l.Add((tip, tip - f * 1.4f - s * 0.9f, c));
        l.Add((tip - f * 1.4f + s * 0.9f, tip - f * 1.4f - s * 0.9f, c));
        var top = p + Vector3.UnitY * 3.4f;
        l.Add((p, top, c));
        var flag = top - Vector3.UnitY * 0.9f;
        l.Add((top, top - Vector3.UnitY * 0.45f + f * 1.4f, c)); l.Add((top - Vector3.UnitY * 0.45f + f * 1.4f, flag, c));
        l.Add((top - Vector3.UnitY * 0.2f, top - Vector3.UnitY * 0.45f + f * 1.1f, c)); l.Add((top - Vector3.UnitY * 0.7f, top - Vector3.UnitY * 0.45f + f * 1.1f, c));
    }

    /// <summary>Player start figures (a few dozen segments), 3 pixels wide on top of everything.</summary>
    void DrawSpawnFigures(Matrix4x4 vp)
    {
        if (!ShowMarkers || Scene == null) return;
        var l = new List<(Vector3, Vector3, Vector3)>();
        foreach (var o in Scene.Objects) if (o.Visible && SpawnPoints.Is(o) && Vector3.Distance(o.Transform.Translation, _camPos) < 3000) AddSpawnFigure(l, o);
        if (l.Count > 0) _r.Lines(l, vp, true, 3f);
    }

    /// <summary>Picking / selection box of a start figure (model space of the marker).</summary>
    static readonly (Vector3 Min, Vector3 Max) SpawnBounds = (new(-1.7f, -0.1f, -1.7f), new(1.7f, 3.5f, 5.1f));

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
        foreach (var x in _extra) AddBox(lines, x, new Vector3(1, 0.75f, 0.35f));
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
                // free drag: a square grid around the start position in the drag plane (XZ ground, or the upright XY / ZY plane)
                float s = Math.Max(4, GizmoLength());
                var pn = DragPlaneNormal();
                var u = pn == Vector3.UnitX ? Vector3.UnitZ : Vector3.UnitX;
                var v = pn == Vector3.UnitY ? Vector3.UnitZ : Vector3.UnitY;
                for (int i = -2; i <= 2; i++)
                {
                    lines.Add((piv - u * s + v * (i * s / 2), piv + u * s + v * (i * s / 2), new Vector3(0.75f, 0.75f, 0.3f)));
                    lines.Add((piv + u * (i * s / 2) - v * s, piv + u * (i * s / 2) + v * s, new Vector3(0.75f, 0.75f, 0.3f)));
                }
            }
            if (_xf == XfKind.Grab) lines.Add((piv, p, new Vector3(1, 1, 1)));
            _r.Lines(lines, vp, true, 2f);
            return;
        }
        _r.Lines(lines, vp, true, 2f);
        if (Mode == GizmoMode.Select || (Mode == GizmoMode.Scale && ScaleLocked(Selected))) return;
        var ax = GizmoAxes(); float len = GizmoLength();
        var right = Right(); var up = Vector3.Normalize(Vector3.Cross(right, Forward()));
        bool yawOnly = Mode == GizmoMode.Rotate && SpawnPoints.Is(Selected);   // a player start turns about Y only
        for (int i = 0; i < 3; i++)
        {
            if (yawOnly && i != 1) continue;
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
        if (Selected == null || Selected.Kind == SceneObjectKind.Terrain || Mode == GizmoMode.Select || (Mode == GizmoMode.Scale && ScaleLocked(Selected))) return -1;
        var p = Selected.Transform.Translation; var ax = GizmoAxes(); float len = GizmoLength();
        var s0 = ToScreen(p); if (s0 == null) return -1;
        int best = -1; float bestD = 9;
        for (int i = 0; i < 3; i++)
        {
            if (Mode == GizmoMode.Rotate && i != 1 && SpawnPoints.Is(Selected)) continue;   // a player start: the Y handle only
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
    /// <summary>Hovered element of the bar: 0..3 view modes, 4 the collision toggle, -1 none.</summary>
    int _hoverBar = -1;
    const int BarSeg = 74, BarH = 26, BarMargin = 10, LightH = 22, CollW = 92, CollGap = 6;
    Rectangle BarRect => new(_gl.Width - BarMargin - BarSeg * 4, BarMargin, BarSeg * 4, BarH);
    Rectangle LightRect => new(_gl.Width - BarMargin - BarSeg * 4, BarMargin + BarH + 4, BarSeg * 4, LightH);
    /// <summary>The collision toggle, left of the view-mode bar.</summary>
    Rectangle CollRect => new(_gl.Width - BarMargin - BarSeg * 4 - CollGap - CollW, BarMargin, CollW, BarH);

    /// <summary>Bar element under a view pixel (see <see cref="_hoverBar"/>).</summary>
    int BarHit(Point p)
    {
        var br = BarRect;
        if (br.Contains(p)) return Math.Clamp((p.X - br.X) / BarSeg, 0, 3);
        return CollRect.Contains(p) ? 4 : VisRect.Contains(p) ? VisHit : -1;
    }

    void DrawOverlays(int W, int H)
    {
        var cr = VisRect;   // the bar bitmap starts with the "Show" button (SceneViewport.Visgroups.cs)
        bool showLight = _viewMode == ViewMode.Rendered;
        string barKey = $"{_viewMode}|{_hoverBar}|{(showLight ? LightingName : "")}|{_showColl}|{_showColl && _allColl == null}";
        if (_barOv.Key != barKey)
            using (var bmp = DrawBar(showLight)) _r.UpdateOverlay(_barOv, bmp, barKey);
        _r.DrawOverlay(_barOv, cr.X, cr.Y, W, H);
        DrawSpawnLabels(W, H);
        DrawCollisionRect(W, H);
        DrawHiddenNote(W, H);
        DrawTip(W, H);
        var hud = HudText();
        if (hud != null)
        {
            string k = hud.Value.Main + "|" + hud.Value.Hint;
            if (_hudOv.Key != k) using (var bmp = DrawHud(hud.Value.Main, hud.Value.Hint, hud.Value.Colour)) _r.UpdateOverlay(_hudOv, bmp, k);
            _r.DrawOverlay(_hudOv, 12, H - _hudOv.H - 12, W, H);
        }
    }

    readonly Dictionary<string, Renderer.Overlay> _spawnLabels = new();

    /// <summary>"Player start (Banjo)" (etc.) above every visible start point within 2000 units, drawn on top.</summary>
    void DrawSpawnLabels(int W, int H)
    {
        if (Scene == null || !ShowMarkers) return;
        foreach (var o in Scene.Objects)
        {
            if (!o.Visible || !SpawnPoints.Is(o) || SpawnPoints.Label(o) is not { } text) continue;
            var top = o.Transform.Translation + Vector3.UnitY * 3.7f;
            if (Vector3.Dot(top - _camPos, Forward()) < 0.5f || Vector3.Distance(top, _camPos) > 2000) continue;
            if (ToScreen(top) is not { } sp) continue;
            if (!_spawnLabels.TryGetValue(text, out var ov))
            {
                _spawnLabels[text] = ov = new Renderer.Overlay();
                using var bmp = DrawLabel(text, o == Selected);
                _r.UpdateOverlay(ov, bmp, text);
            }
            int x = (int)sp.X - ov.W / 2, y = (int)sp.Y - ov.H - 2;
            if (x > W || y > H || x + ov.W < 0 || y + ov.H < 0) continue;
            _r.DrawOverlay(ov, x, y, W, H);
        }
    }

    static Bitmap DrawLabel(string text, bool selected)
    {
        using var font = new Font("Segoe UI Semibold", 9f);
        using var probe = new Bitmap(1, 1); using var pg = Graphics.FromImage(probe);
        var sz = pg.MeasureString(text, font);
        int w = (int)Math.Ceiling(sz.Width) + 26, h = (int)Math.Ceiling(sz.Height) + 6;
        var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);
        using (var p = Rounded(new Rectangle(0, 0, w - 1, h - 1), 5)) using (var b = new SolidBrush(Color.FromArgb(235, 246, 247, 249))) using (var e = new Pen(Color.FromArgb(255, 30, 160, 70), 1.5f)) { g.FillPath(b, p); g.DrawPath(e, p); }
        // a little flag in the start points' green
        using (var fb = new SolidBrush(Color.FromArgb(255, 30, 190, 80))) g.FillPolygon(fb, new[] { new PointF(8, 4), new PointF(18, 8), new PointF(8, 12) });
        using (var pen = new Pen(Color.FromArgb(255, 32, 35, 42), 1.4f)) g.DrawLine(pen, 8, 4, 8, h - 4);
        using (var tb = new SolidBrush(Color.FromArgb(255, 32, 35, 42))) g.DrawString(text, font, tb, 20, 2);
        return bmp;
    }

    // ---- hover tooltip (start points)
    // drawn in the view like the HUD: a WinForms ToolTip is not shown over the GL window (its native child takes the
    // activation, so ToolTip.Show's "form is active" test fails)
    readonly Renderer.Overlay _tipOv = new();
    Point _tipAt;
    readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 450 };
    SceneObject? _tipFor;

    void HoverMoved()
    {
        _hoverTimer.Stop();
        if (_tipFor != null) { _tipFor = null; _gl.Invalidate(); }
        _hoverTimer.Start();
    }

    void HoverTick()
    {
        _hoverTimer.Stop();
        using var _p = Prof.Time("hover pick");
        if (Scene == null || _looking || _panning || _xf != XfKind.None || !_gl.ClientRectangle.Contains(_mouse)) return;
        var o = Pick(_mouse).Obj;
        if (!SpawnPoints.Is(o)) return;
        _tipFor = o;
        _tipAt = new Point(_mouse.X + 14, _mouse.Y + 18);
        _gl.Invalidate();
    }

    void DrawTip(int W, int H)
    {
        if (_tipFor == null) return;
        string key = $"tip|{_tipFor.Id}|{_tipFor.Name}|{_tipFor.ModelName}";
        if (_tipOv.Key != key)
            using (var bmp = DrawHud(SpawnPoints.Label(_tipFor) ?? _tipFor.Name, Wrap($"{SpawnPoints.Detail(_tipFor)}\n{_tipFor.Name} in {_tipFor.ModelName}", 70), Color.FromArgb(255, 30, 190, 80)))
                _r.UpdateOverlay(_tipOv, bmp, key);
        int x = Math.Min(_tipAt.X, Math.Max(0, W - _tipOv.W - 4)), y = Math.Min(_tipAt.Y, Math.Max(0, H - _tipOv.H - 4));
        _r.DrawOverlay(_tipOv, x, y, W, H);
    }

    static string Wrap(string s, int width)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var para in s.Split('\n'))
        {
            int col = 0;
            foreach (var w in para.Split(' '))
            {
                if (col > 0 && col + w.Length + 1 > width) { sb.Append('\n'); col = 0; }
                else if (col > 0) { sb.Append(' '); col++; }
                sb.Append(w); col += w.Length;
            }
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    // NB Studio's own look: the light panels of the menu bar, toolbar and tabs (SystemColors.Control 240,240,240 with a
    // grey edge) and the orange accent of the start page for the active mode. (Studio 1.6–1.7 drew a charcoal bar.)
    static readonly Color BarBg = Color.FromArgb(238, 246, 247, 249), BarEdge = Color.FromArgb(255, 160, 166, 178),
        BarHover = Color.FromArgb(255, 226, 230, 238), Accent = Color.FromArgb(255, 242, 140, 40), AccentHi = Color.FromArgb(255, 255, 186, 102),
        BarText = Color.FromArgb(255, 32, 35, 42), AccentText = Color.FromArgb(255, 28, 20, 12), BarSep = Color.FromArgb(255, 204, 208, 216),
        IconFg = Color.FromArgb(255, 62, 66, 78);

    Bitmap DrawBar(bool showLight)
    {
        int h = showLight ? BarH + 4 + LightH : BarH, x0 = CollW + CollGap;
        var bmp = new Bitmap(VisW + CollGap + x0 + BarSeg * 4, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);
        using var bg = new SolidBrush(BarBg);
        using var edge = new Pen(BarEdge, 1f);
        using var font = new Font("Segoe UI Semibold", 8.5f);
        DrawVisButton(g, font, bg, edge);
        g.TranslateTransform(VisW + CollGap, 0);
        // the collision toggle
        {
            using var cp = Rounded(new Rectangle(0, 0, CollW - 1, BarH - 1), 4);
            g.FillPath(bg, cp); g.DrawPath(edge, cp);
            var r = new Rectangle(3, 3, CollW - 6, BarH - 7);
            bool on = _showColl, hover = _hoverBar == 4;
            if (on) { using var p2 = Rounded(r, 3); using var b2 = new System.Drawing.Drawing2D.LinearGradientBrush(r, AccentHi, Accent, 90f); g.FillPath(b2, p2); }
            else if (hover) { using var p2 = Rounded(r, 3); using var b2 = new SolidBrush(BarHover); g.FillPath(b2, p2); }
            DrawCollisionIcon(g, new RectangleF(r.X + 5, r.Y + 2.5f, 15, 15), on);
            using var tb = new SolidBrush(on ? AccentText : BarText);
            g.DrawString(on && _allColl == null ? "Collision…" : "Collision", font, tb, r.X + 23, r.Y + 2);
        }
        using var path = Rounded(new Rectangle(x0, 0, BarSeg * 4 - 1, BarH - 1), 4);
        g.FillPath(bg, path); g.DrawPath(edge, path);
        using var sep = new Pen(BarSep, 1f);
        for (int i = 0; i < 4; i++)
        {
            var r = new Rectangle(x0 + i * BarSeg + 3, 3, BarSeg - 6, BarH - 7);
            bool on = (int)_viewMode == i, hover = _hoverBar == i;
            if (i > 0 && !on && (int)_viewMode != i - 1) g.DrawLine(sep, x0 + i * BarSeg, 6, x0 + i * BarSeg, BarH - 7);
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
            var lr = new Rectangle(x0, BarH + 4, BarSeg * 4 - 1, LightH - 1);
            using var lp = Rounded(lr, 4); g.FillPath(bg, lp); g.DrawPath(edge, lp);
            using var f2 = new Font("Segoe UI", 8f);
            using var ab = new SolidBrush(Color.FromArgb(255, 214, 112, 16));
            using var tb = new SolidBrush(BarText);
            g.DrawString("☀", f2, ab, x0 + 7, BarH + 7);
            string text = $"Light: {LightingName}" + (_lights is { Count: > 1 } ? "   ›  click for next" : "");
            g.DrawString(text, f2, tb, x0 + 22, BarH + 7);
        }
        return bmp;
    }

    /// <summary>The collision toggle's icon: a wireframe triangle mesh.</summary>
    static void DrawCollisionIcon(Graphics g, RectangleF r, bool on)
    {
        var fg = on ? AccentText : IconFg;
        using var pen = new Pen(fg, 1.2f) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
        float x = r.X, y = r.Y + 1, w = r.Width, h = r.Height - 2;
        PointF P(float u, float v) => new(x + u * w, y + v * h);
        var a = P(0, 1); var b = P(0.5f, 1); var c = P(1, 1); var d = P(0.25f, 0.45f); var e = P(0.75f, 0.45f); var f = P(0.5f, 0);
        g.DrawPolygon(pen, new[] { a, c, f });
        g.DrawLine(pen, d, e); g.DrawLine(pen, d, b); g.DrawLine(pen, e, b);
    }

    /// <summary>The view-mode icons: a wire cube, a shaded cube, a picture, and a sun.</summary>
    static void DrawModeIcon(Graphics g, int mode, RectangleF r, bool on)
    {
        var fg = on ? AccentText : IconFg;
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
                using var t = new SolidBrush(on ? Color.FromArgb(255, 250, 238, 222) : Color.FromArgb(255, 206, 210, 220));
                using var lft = new SolidBrush(on ? Color.FromArgb(255, 150, 96, 48) : Color.FromArgb(255, 132, 138, 152));
                using var rgt = new SolidBrush(on ? Color.FromArgb(255, 96, 58, 26) : Color.FromArgb(255, 86, 92, 106));
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
                using var pen = new Pen(on ? AccentText : Color.FromArgb(255, 226, 150, 30), 1.4f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
                for (int k = 0; k < 8; k++)
                {
                    double a = k * Math.PI / 4;
                    g.DrawLine(pen, cx + (float)Math.Cos(a) * s * 0.62f, cy + (float)Math.Sin(a) * s * 0.62f, cx + (float)Math.Cos(a) * s * 0.98f, cy + (float)Math.Sin(a) * s * 0.98f);
                }
                using var disc = new SolidBrush(on ? AccentText : Color.FromArgb(255, 236, 140, 24));
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
                    : $"Move  {(_xfDrag ? DragPlaneName() + "  " : "")}dx {d.X:0.00}  dy {d.Y:0.00}  dz {d.Z:0.00}";
                break;
            }
            case XfKind.Scale: main = $"Scale  {axis}{(axis.Length > 0 ? "  " : "")}{_xfValue:0.000}{typed}"; break;
            default: main = $"Rotate  {(axis.Length > 0 ? axis : _xfDrag ? "Y (drag)" : "view")}  {_xfValue:0.0}°{typed}"; break;
        }
        string hint = _xfDrag
            ? "hold X / Y / Z or drag an axis handle to constrain · Esc cancels"
            : "X / Y / Z constrain (again: world ↔ local, then free) · type a value · Ctrl snap · LMB / Enter confirm · RMB / Esc cancel" + (_xfStarts.Count > 1 ? $" · {_xfStarts.Count} objects" : "");
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
        if (kind == XfKind.Scale && ScaleLocked(Selected)) return;   // markers keep their scale
        if (kind == XfKind.Rotate && SpawnPoints.Is(Selected)) { axis = 1; space = AxisSpace.World; }   // a player start turns about Y only (its yaw is Banjo's facing)
        if (_xf != XfKind.None) { if (!drag) { _xf = kind; _xfTyped = ""; if (kind == XfKind.Rotate && SpawnPoints.Is(Selected)) { _xfAxis = 1; _xfSpace = AxisSpace.World; } UpdateTransform(); } return; }
        _xf = kind; _xfDrag = drag; _xfAxis = axis; _xfSpace = space; _xfTyped = ""; _xfDragKeyAxis = false;
        _xfStart = Selected.Transform; _xfMouse0 = _mouse;
        BeginMulti();
        _movingLines.Clear();
        foreach (var m in SelectedObjects) if (m.Kind == SceneObjectKind.Marker) _movingLines.Add(m);
        if (_movingLines.Count > 0) _linesVersion++;
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
        if (_movingLines.Count > 0) { _movingLines.Clear(); _linesVersion++; }
        var changed = (_xfStarts.Count > 0 ? _xfStarts : new List<(SceneObject, Matrix4x4)> { (o!, start) }).Where(x => x.Item1 != null && x.Item1.Transform != x.Item2).ToList();
        _xfStarts = new();
        if (changed.Count > 0) ObjectsEdited?.Invoke(changed);
        if (o != null && o.Transform != start) { ObjectEdited?.Invoke(o); SelectionChanged?.Invoke(o); }
        if (o?.Kind == SceneObjectKind.Marker) _linesVersion++;
        _gl.Invalidate();
    }

    public void CancelTransform()
    {
        if (_movingLines.Count > 0) { _movingLines.Clear(); _linesVersion++; }
        if (_xf == XfKind.None) return;
        CancelMulti(); _xfStarts = new();
        if (Selected != null) { Selected.Transform = _xfStart; SelectionChanged?.Invoke(Selected); if (Selected.Kind == SceneObjectKind.Marker) _linesVersion++; }
        _xf = XfKind.None; _xfAxis = -1; _xfTyped = "";
        _gl.Invalidate();
    }

    void SetAxis(int axis)
    {
        if (_xf == XfKind.Rotate && Selected != null && SpawnPoints.Is(Selected)) { _xfAxis = 1; _xfSpace = AxisSpace.World; UpdateTransform(); return; }   // yaw only
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
        using var _p = Prof.Time("UpdateTransform (incl. SelectionChanged)");
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
                    // modal G: the plane through the object facing the camera; drag: the XZ ground plane when the camera looks
                    // mostly down (or up), else the upright world plane closest to facing the camera (XY or ZY)
                    var n = _xfDrag ? DragPlaneNormal() : Forward();
                    var h0 = RayPlane(_xfMouse0, piv, n); var h1 = RayPlane(_mouse, piv, n);
                    if (h0 == null || h1 == null) return;
                    delta = h1.Value - h0.Value;
                    if (_xfDrag) delta -= n * Vector3.Dot(delta, n);   // exactly in the plane
                    if (snap) delta = new Vector3(MathF.Round(delta.X), MathF.Round(delta.Y), MathF.Round(delta.Z));
                }
                var m = start; m.Translation = piv + delta; o.Transform = m;
                break;
            }
            case XfKind.Scale:
            {
                float f;
                if (TypedValue is { } tv) f = tv;
                else if (_xfAxis >= 0 && AxisOnScreen(piv, a) is { } ax)
                {
                    // constrained: the mouse movement along the axis as drawn on screen, so moving towards where the axis
                    // points always grows it, from any side of the object (1.12: the horizontal mouse movement for drags and
                    // the distance from the centre for G / T, which inverted from some camera sides)
                    var mv = new Vector2(_mouse.X - _xfMouse0.X, _mouse.Y - _xfMouse0.Y);
                    f = 1 + Vector2.Dot(mv, ax.Dir) / MathF.Max(80, ax.Length);
                }
                else if (_xfDrag) f = 1 + (_mouse.X - _xfMouse0.X - (_mouse.Y - _xfMouse0.Y)) * 0.01f;   // uniform: right / up grows
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
                // no axis: about the view direction (modal) or Y (drag)
                var axis = Vector3.Normalize(_xfAxis >= 0 ? a : _xfDrag ? Vector3.UnitY : -Forward());
                bool flip = Vector3.Dot(axis, Forward()) < 0;
                // drag: the near side of the object follows the mouse (its screen direction of motion for a positive turn)
                var fr = Vector3.Cross(axis, -Forward());
                var fdir = new Vector2(Vector3.Dot(fr, Right()), -Vector3.Dot(fr, Vector3.Cross(Right(), Forward())));
                if (TypedValue is { } tv) deg = tv;
                else if (_xfDrag && fdir.Length() > 0.3f)
                {
                    deg = Vector2.Dot(new Vector2(_mouse.X - _xfMouse0.X, _mouse.Y - _xfMouse0.Y), Vector2.Normalize(fdir)) * 0.6f;
                    flip = false;
                }
                else
                {
                    var ps = ToScreen(piv) ?? new Vector2(_gl.Width / 2f, _gl.Height / 2f);
                    float a0 = MathF.Atan2(_xfMouse0.Y - ps.Y, _xfMouse0.X - ps.X), a1 = MathF.Atan2(_mouse.Y - ps.Y, _mouse.X - ps.X);
                    deg = (a1 - a0) * 180 / MathF.PI;
                    while (deg > 180) deg -= 360; while (deg < -180) deg += 360;
                }
                if (snap && TypedValue == null) deg = MathF.Round(deg / 15) * 15;
                _xfValue = deg;
                // modal angles (and drags about an axis pointing at the camera) follow the mouse around the object on screen
                // (a clockwise circle turns it clockwise as seen from the camera, from either side)
                float rad = deg * MathF.PI / 180;
                if (flip) rad = -rad;
                var m = start * Matrix4x4.CreateTranslation(-piv) * Matrix4x4.CreateFromAxisAngle(axis, rad) * Matrix4x4.CreateTranslation(piv);
                m.Translation = piv; o.Transform = m;
                break;
            }
        }
        // markers (boxes, path-node links) are in the static line batch: rebuild it so the path follows the node live
        if (o.Kind == SceneObjectKind.Marker && !_movingLines.Contains(o)) _linesVersion++;
        UpdateMulti();   // the other selected objects follow
        SelectionChanged?.Invoke(o);
        _gl.Invalidate();
    }

    /// <summary>The axis as drawn on screen at <paramref name="piv"/>: unit direction (towards +axis) and length in pixels of
    /// the gizmo's arm; null when the axis points at the camera.</summary>
    (Vector2 Dir, float Length)? AxisOnScreen(Vector3 piv, Vector3 axis)
    {
        float len = GizmoLength();
        var s0 = ToScreen(piv); var s1 = ToScreen(piv + axis * len);
        if (s0 == null || s1 == null) return null;
        var d = s1.Value - s0.Value; float l = d.Length();
        return l < 3 ? null : (d / l, l);
    }

    /// <summary>Normal of the plane of a free (unconstrained) drag: Y (the XZ ground plane) when the camera looks more than
    /// 45° down or up, else the upright world plane closest to facing the camera (yaw snapped to 90°): Z (the XY plane)
    /// when it looks mostly along Z, X (the ZY plane) when it looks mostly along X.</summary>
    Vector3 DragPlaneNormal()
    {
        if (MathF.Abs(_pitch) > MathF.PI / 4) return Vector3.UnitY;
        var f = Forward();
        return MathF.Abs(f.X) > MathF.Abs(f.Z) ? Vector3.UnitX : Vector3.UnitZ;
    }

    string DragPlaneName() { var n = DragPlaneNormal(); return n == Vector3.UnitY ? "XZ plane" : n == Vector3.UnitZ ? "XY plane" : "ZY plane"; }

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

    void Tick() { if (_gl.Focused) TickCore(FlySteps()); }

    readonly System.Diagnostics.Stopwatch _flyClock = System.Diagnostics.Stopwatch.StartNew();
    double _flyLastMs;

    /// <summary>Elapsed time since the last fly step, in 16.7 ms steps (capped, so a pause doesn't jump the camera).</summary>
    float FlySteps()
    {
        double now = _flyClock.Elapsed.TotalMilliseconds, dt = now - _flyLastMs;
        _flyLastMs = now;
        return (float)Math.Clamp(dt / 16.667, 0, 3);
    }

    void TickCore(float steps = 1f)
    {
        if (_keys.Count == 0 || _xf != XfKind.None) return;
        float speed = (_keys.Contains(Keys.ShiftKey) ? 4f : 1f) * 1.2f * steps;
        if (_keys.Contains(Keys.ControlKey)) return;
        var f = Forward(); var r = Right(); var d = Vector3.Zero;
        // S is Scale while something is selected (Blender), but while flying (right button held, other fly keys held,
        // or flying a moment ago) S flies backwards
        const bool sFlies = true;   // S always flies backwards (T scales)
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
                case Keys.T: if (!_xfDrag) BeginTransform(XfKind.Scale, false); return;
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
        bool canEdit = Selected != null && Selected.Kind != SceneObjectKind.Terrain && fresh && !flying;
        switch (e.KeyCode)
        {
            case Keys.F when Selected != null: Focus(Selected); break;
            case Keys.D1: Mode = GizmoMode.Move; _gl.Invalidate(); break;
            case Keys.D2: Mode = GizmoMode.Rotate; _gl.Invalidate(); break;
            case Keys.D3: Mode = GizmoMode.Scale; _gl.Invalidate(); break;
            case Keys.G when canEdit: BeginTransform(XfKind.Grab, false); e.Handled = true; break;
            case Keys.R when canEdit: BeginTransform(XfKind.Rotate, false); e.Handled = true; break;
            case Keys.T when canEdit && !ScaleLocked(Selected): BeginTransform(XfKind.Scale, false); e.Handled = true; break;
            case Keys.H when fresh && !e.Shift: HideSelection(); e.Handled = true; break;
            case Keys.U when fresh: UnhideAll(); e.Handled = true; break;
            case Keys.B when fresh: ArmBoxSelect(); e.Handled = true; break;
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
        if (Prof.On && e.Button == MouseButtons.Left) Prof.Flush("before mouse down");
        using var _p = Prof.Time("mouse down");
        OnMouseDownCore(e);
    }

    void OnMouseDownCore(MouseEventArgs e)
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
            int bh = BarHit(e.Location);
            if (bh is >= 0 and <= 3) { ViewMode = (ViewMode)bh; return; }
            if (bh == 4) { ShowCollision = !ShowCollision; return; }
            if (bh == VisHit) { ShowVisgroupsMenu(); return; }
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
            if (Selected != null && kind != XfKind.None && !(Scene?.Objects.Contains(Selected) ?? true) && HitsBox(Selected, e.Location))
            {
                // the collision selection: drag inside its box like an object (Move: free move, Scale: uniform, Rotate: about Y)
                BeginTransform(kind, drag: true);
                _dragMoved = false;
                _proxyBoxDown = _collMode;
                UpdateTransform();
                return;
            }
            if (CollisionMouseDown(e)) return;   // Edit Collision: click / rectangle picks collision, not objects
            if (ObjectRectDown(e)) return;       // B or Ctrl: rectangle / Ctrl+click toggles
            var hit = Pick(e.Location);
            if (DebugClicks) CollisionInfo?.Invoke($"click {e.Location} mods {ModifierKeys} hit {hit.Obj?.Name ?? "-"} selected {SelectedObjects.Count}");
            if ((ModifierKeys & Keys.Shift) != 0 && hit.Obj != null) { ToggleSelect(hit.Obj); return; }
            if (hit.Obj != null && IsSelected(hit.Obj) && hit.Obj.Kind != SceneObjectKind.Terrain && kind != XfKind.None)
            {
                if (hit.Obj != Selected) SelectMany(SelectedObjects, hit.Obj);   // drag a selected object: it leads, the others follow
                BeginTransform(kind, drag: true);
                _dragMoved = false;
                UpdateTransform();
            }
            else Select(hit.Obj);
        }
    }

    void OnMouseUp(object? s, MouseEventArgs e)
    {
        try { OnMouseUpCore(e); }
        finally { if (Prof.On && e.Button == MouseButtons.Left) Prof.Flush($"mouse up at {Fmt(_camPos)} (view {_viewMode}, collision {(ShowCollision ? "on" : "off")}, edit collision {(_collMode ? "on" : "off")})"); }
    }

    void OnMouseUpCore(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            _looking = false;
            if (_dragMoved) _lastFly = DateTime.UtcNow;
            if (_suppressRightUp) { _suppressRightUp = false; return; }
            if (!_dragMoved && _collMode) { CollisionContextMenuRequested?.Invoke(PickCollision(e.Location), e.Location); return; }
            if (!_dragMoved) { var hit = Pick(e.Location); if (hit.Obj != null) Select(hit.Obj); ContextMenuRequested?.Invoke(hit.Obj ?? Selected, e.Location); }
        }
        if (e.Button == MouseButtons.Middle) _panning = false;
        if (ObjectRectUp(e)) return;
        if (CollisionMouseUp(e)) return;
        if (e.Button == MouseButtons.Left && _xf != XfKind.None && _xfDrag)
        {
            if (_dragMoved) ConfirmTransform(); else CancelTransform();
            // a click (no drag) inside the collision selection's box still picks collision (another piece, Shift / Alt)
            if (!_dragMoved && _proxyBoxDown && _collMode)
                CollisionPicked?.Invoke(PickCollision(e.Location), (ModifierKeys & Keys.Shift) != 0, (ModifierKeys & Keys.Alt) != 0);
        }
        _proxyBoxDown = false;
    }
    bool _proxyBoxDown;

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
            // paint now when the last frame is older than a frame time: WM_PAINT is only handled when no input is
            // waiting, so a quickly moved mouse kept the view from updating until it stopped ("lags, then jumps")
            if (System.Diagnostics.Stopwatch.GetElapsedTime(_lastFrame).TotalMilliseconds >= 15) _gl.Update();
        }
        else if (CollisionMouseMove(e)) { }
        else
        {
            int hb = BarHit(e.Location);
            int hh = hb < 0 ? HandleAt(e.Location) : -1;
            HoverMoved();
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
        using var _p = Prof.Time("Pick (objects)");
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

    /// <summary>
    /// The first drawn surface along a view pixel's ray (Ctrl+V pastes there): the mesh of the terrain, a scenery object or
    /// an object placed by a marker, as drawn now (marker boxes, hidden scenery and switched-off kinds are ignored).
    /// <paramref name="pixel"/> null: the mouse position, or the view's centre when the mouse is not over the view.
    /// Returns the point (or, when nothing is hit, the point <paramref name="fallback"/> units along the ray).
    /// </summary>
    public (Vector3 Point, SceneObject? Hit, bool UnderMouse) SurfaceAt(Point? pixel, float fallback)
    {
        var mp = _gl.PointToClient(Control.MousePosition);
        bool mouse = pixel == null && _gl.ClientRectangle.Contains(mp) && BarHit(mp) < 0;
        var pt = pixel ?? (mouse ? mp : new Point(_gl.Width / 2, _gl.Height / 2));
        var (ro, rd) = Ray(pt);
        var (o, t) = MeshHit(ro, rd);
        return o != null ? (ro + rd * t, o, mouse) : (ro + rd * fallback, null, mouse);
    }

    /// <summary>The surface straight below a point (or null), e.g. the ground under the 3D view's camera.</summary>
    public Vector3? GroundBelow(Vector3 p)
    {
        var (o, t) = MeshHit(p, -Vector3.UnitY);
        return o != null ? p - Vector3.UnitY * t : null;
    }

    (SceneObject? Obj, float T) MeshHit(Vector3 ro, Vector3 rd)
    {
        SceneObject? best = null; float bestT = float.MaxValue;
        if (Scene == null) return (null, 0);
        foreach (var o in Scene.Objects)
        {
            if (!DrawsModel(o, keepSelected: false) || !Matrix4x4.Invert(o.Transform, out var inv)) continue;
            var lo = Vector3.Transform(ro, inv); var ld = Vector3.TransformNormal(rd, inv);
            if (o.Kind != SceneObjectKind.Terrain && (!RayBox(lo, ld, o.BoundsMin, o.BoundsMax, out float tb) || tb > bestT)) continue;
            float t = RayMesh(lo, ld, o.Model!, bestT);
            foreach (var (cm, cl) in o.Children)
                if (Matrix4x4.Invert(cl, out var ci)) t = MathF.Min(t, RayMesh(Vector3.Transform(lo, ci), Vector3.TransformNormal(ld, ci), cm, bestT));
            if (t < bestT) { bestT = t; best = o; }
        }
        return (best, bestT);
    }

    /// <summary>A transform for a copy placed on a surface point: the rotation and scale of <paramref name="m"/>, its origin
    /// above the point and the bottom of its box (<paramref name="bmin"/>..<paramref name="bmax"/>, model space) on it.</summary>
    public static Matrix4x4 PlaceOn(Matrix4x4 m, Vector3 bmin, Vector3 bmax, Vector3 point)
    {
        var rs = m; rs.Translation = Vector3.Zero;
        float minY = float.MaxValue;
        for (int i = 0; i < 8; i++)
            minY = MathF.Min(minY, Vector3.Transform(new Vector3((i & 1) != 0 ? bmax.X : bmin.X, (i & 2) != 0 ? bmax.Y : bmin.Y, (i & 4) != 0 ? bmax.Z : bmin.Z), rs).Y);
        if (!float.IsFinite(minY)) minY = 0;
        var r = m; r.Translation = point - new Vector3(0, minY, 0);
        return r;
    }

    /// <summary>The mouse ray hits the object's box (model space bounds).</summary>
    bool HitsBox(SceneObject o, Point p)
    {
        if (!Matrix4x4.Invert(o.Transform, out var inv)) return false;
        var (ro, rd) = Ray(p);
        return RayBox(Vector3.Transform(ro, inv), Vector3.TransformNormal(rd, inv), o.BoundsMin, o.BoundsMax, out _);
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
        if (disposing) { _timer.Dispose(); _redrawSoon?.Dispose(); if (_ready) { _gl.MakeCurrent(); if (_frameCacheFbo != 0) { GL.DeleteFramebuffer(_frameCacheFbo); GL.DeleteRenderbuffer(_frameCacheRb); } _r.DeleteOverlay(_barOv); _r.DeleteOverlay(_hudOv); _r.Dispose(); } }
        base.Dispose(disposing);
    }
}
