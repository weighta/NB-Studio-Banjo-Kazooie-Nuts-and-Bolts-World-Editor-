using System.Numerics;
using NB.Core.Models;
using NB.Core.Vehicles;
using OpenTK.GLControl;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using PixelFormat = OpenTK.Graphics.OpenGL4.PixelFormat;

namespace NB.Studio.Viewport;

public enum VehicleTool { Select, Place, Paint }

/// <summary>
/// 3D build view of the Vehicle Editor: the parts of a <see cref="VehicleDocument"/> on the garage grid (one cell = one
/// unit; a part's model is drawn at its cell, rotated by its orientation), an orbit camera (right-drag orbits,
/// middle-drag pans, wheel zooms, F frames the vehicle), a ghost of the part being placed and the selection.
/// Select: click (Ctrl/Shift adds), drag a selected part to move it in the plane facing the camera, snapped to cells (the
/// 3D View's rule: looking down / up more than 45° the ground plane XZ, else the upright plane XY or ZY closest to facing
/// the camera; Shift: up/down only). Place: the ghost follows the face under the mouse; click places it. Paint: click
/// paints a part (Alt+click picks its colour). Keys: R rotates 90° about the world axis closest to the view direction
/// (clockwise as seen; Shift: the other way), X / Y / Z about that axis, arrows / PgUp / PgDn move, Del deletes.
/// </summary>
public sealed class VehicleViewport : UserControl
{
    readonly GLControl _gl;
    readonly VehicleRenderer _r = new();
    readonly ToolTip _tip = new() { InitialDelay = 400, ReshowDelay = 100, AutoPopDelay = 15000 };
    VehicleDocument.Part? _tipPart;
    bool _ready;

    public VehicleDocument? Document;
    public PartCatalog? Catalog;
    public VehicleTool Tool = VehicleTool.Select;
    /// <summary>The library part placed by the Place tool, and the ghost's orientation.</summary>
    public PartInfo? PlacePart;
    public int PlaceOrientation;
    /// <summary>Paint of the Paint tool (RGBA).</summary>
    public uint PaintColour = 0xD10903FF;
    public bool ShowGrid = true;
    /// <summary>Hazard signs over floating parts.</summary>
    public bool ShowHazards = true;
    /// <summary>Status of the Place tool's ghost at its last spot (0 attached, 1 floating, 2 blocked; -1 none).</summary>
    public int GhostStatus = -1;

    /// <summary>A part was clicked with the Paint tool (Alt: pick its colour instead).</summary>
    public event Action<VehicleDocument.Part, bool>? PaintClicked;
    /// <summary>The Place tool (or a part dragged from the library and dropped) wants a part at this cell.</summary>
    public event Action<int, int, int>? PlaceRequested;
    /// <summary>A right click (no drag) with the Place tool: turn the part being placed (as R does).</summary>
    public event Action? RotateGhostRequested;
    /// <summary>Vehicle files dropped on the view.</summary>
    public event Action<string[]>? FilesDropped;
    /// <summary>Drag-and-drop format of a library part (its objparams id as hex text).</summary>
    public const string PartFormat = "NB.VehiclePart";
    /// <summary>Selection changed by a click.</summary>
    public event Action? SelectionChanged;
    /// <summary>Selected parts moved by a drag (cell delta, applied already; one undo step was begun).</summary>
    public event Action<string>? Edited;
    /// <summary>A key the view does not handle itself (the panel maps it).</summary>
    public event Action<KeyEventArgs>? KeyPressed;
    public event Action<string>? Status;

    Vector3 _target = new(1, 1, 2);
    float _yaw = 0.8f, _pitch = 0.45f, _dist = 9f, _fov = 50f;

    public VehicleViewport()
    {
        _gl = new GLControl(new GLControlSettings { API = ContextAPI.OpenGL, APIVersion = new Version(3, 3), Profile = ContextProfile.Core, Flags = ContextFlags.Default })
        { Dock = DockStyle.Fill };
        Controls.Add(_gl);
        _gl.Load += (_, _) => { _gl.MakeCurrent(); _r.Init(); _ready = true; };
        _gl.Paint += (_, _) => Render();
        _gl.Resize += (_, _) => _gl.Invalidate();
        _gl.MouseDown += OnDown; _gl.MouseUp += OnUp; _gl.MouseMove += OnMove; _gl.MouseWheel += OnWheel;
        _gl.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left && Tool == VehicleTool.Select) FocusSelection(); };
        _gl.PreviewKeyDown += (_, e) => e.IsInputKey = !e.Control && !e.Alt && e.KeyCode is not (>= Keys.F1 and <= Keys.F24) and not Keys.Delete;
        _gl.KeyDown += (_, e) => KeyPressed?.Invoke(e);
        _gl.MouseLeave += (_, _) => { _hover = null; _gl.Invalidate(); };
        _gl.AllowDrop = true;
        _gl.DragEnter += (_, e) => PartDragOver(e, true);
        _gl.DragOver += (_, e) => PartDragOver(e, false);
        _gl.DragLeave += (_, _) => { if (_partDrag) { _partDrag = false; _hover = null; _gl.Invalidate(); } };
        _gl.DragDrop += (_, e) => PartDragDrop(e);
    }

    public Func<string, (byte[] Rgba, int W, int H)?>? TextureSource { get => _r.TextureSource; set => _r.TextureSource = value; }

    /// <summary>Drops cached GPU data (other workspace).</summary>
    public void ResetGpu() { if (_ready) { _gl.MakeCurrent(); _r.Clear(); } _gl.Invalidate(); }

    public void Redraw() => _gl.Invalidate();
    public new void Focus() => _gl.Focus();

    // ------------------------------------------------------------------ camera

    Vector3 Eye() => _target + _dist * new Vector3(MathF.Cos(_pitch) * MathF.Sin(_yaw), MathF.Sin(_pitch), MathF.Cos(_pitch) * MathF.Cos(_yaw));
    Matrix4x4 View() => Matrix4x4.CreateLookAt(Eye(), _target, Vector3.UnitY);
    Matrix4x4 Proj() => Matrix4x4.CreatePerspectiveFieldOfView(_fov * MathF.PI / 180, Math.Max(1, _gl.Width) / (float)Math.Max(1, _gl.Height), 0.05f, 2000f);

    /// <summary>Frames the whole vehicle.</summary>
    public void FrameAll(bool keepAngles = true)
    {
        if (Document == null || Document.Parts.Count == 0) { _target = new(0, 0.5f, 0); _dist = 8; _gl.Invalidate(); return; }
        var (mn, mx) = Bounds(Document.Parts);
        _target = (mn + mx) / 2;
        _dist = Math.Max(4f, (mx - mn).Length() * 1.1f + 2);
        if (!keepAngles) { _yaw = 0.8f; _pitch = 0.45f; }
        _gl.Invalidate();
    }

    public void FocusSelection()
    {
        if (Document == null || Document.Selection.Count == 0) { FrameAll(); return; }
        var (mn, mx) = Bounds(Document.Selection);
        _target = (mn + mx) / 2; _dist = Math.Max(3f, (mx - mn).Length() * 1.4f + 2);
        _gl.Invalidate();
    }

    public void SetCamera(float yawDeg, float pitchDeg, float dist = -1)
    {
        _yaw = yawDeg * MathF.PI / 180; _pitch = pitchDeg * MathF.PI / 180;
        if (dist > 0) _dist = dist;
        _gl.Invalidate();
    }

    (Vector3, Vector3) Bounds(IEnumerable<VehicleDocument.Part> parts)
    {
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var p in parts)
        {
            var (a, b) = VehicleDocument.Box(p, Catalog?[p.B.Part]);
            mn = Vector3.Min(mn, new Vector3(a.X, a.Y, a.Z) - new Vector3(0.5f)); mx = Vector3.Max(mx, new Vector3(b.X, b.Y, b.Z) + new Vector3(0.5f));
        }
        if (mn.X > mx.X) { mn = Vector3.Zero; mx = Vector3.One; }
        return (mn, mx);
    }

    // ------------------------------------------------------------------ picking

    (Vector3 O, Vector3 D) Ray(Point p)
    {
        float x = 2f * p.X / Math.Max(1, _gl.Width) - 1, y = 1 - 2f * p.Y / Math.Max(1, _gl.Height);
        Matrix4x4.Invert(View() * Proj(), out var inv);
        var a = Vector4.Transform(new Vector4(x, y, 0, 1), inv); var b = Vector4.Transform(new Vector4(x, y, 1, 1), inv);
        var o = new Vector3(a.X, a.Y, a.Z) / a.W; var e = new Vector3(b.X, b.Y, b.Z) / b.W;
        return (o, Vector3.Normalize(e - o));
    }

    /// <summary>Nearest part cell under the mouse: the part, the cell and the face normal (unit axis).</summary>
    (VehicleDocument.Part Part, (int X, int Y, int Z) Cell, (int X, int Y, int Z) Normal, float T)? Pick(Point p)
    {
        if (Document == null) return null;
        var (o, d) = Ray(p);
        (VehicleDocument.Part, (int, int, int), (int, int, int), float)? best = null;
        foreach (var part in Document.Parts)
            foreach (var c in VehicleDocument.Cells(part, Catalog?[part.B.Part]))
            {
                var mn = new Vector3(c.X - 0.5f, c.Y - 0.5f, c.Z - 0.5f); var mx = mn + Vector3.One;
                if (RayBox(o, d, mn, mx, out float t, out var n) && (best == null || t < best.Value.Item4)) best = (part, c, n, t);
            }
        return best;
    }

    static bool RayBox(Vector3 o, Vector3 d, Vector3 mn, Vector3 mx, out float t, out (int, int, int) normal)
    {
        t = 0; normal = (0, 1, 0);
        float tmin = float.NegativeInfinity, tmax = float.PositiveInfinity; int axis = 1; float sign = 1;
        for (int i = 0; i < 3; i++)
        {
            float oi = i == 0 ? o.X : i == 1 ? o.Y : o.Z, di = i == 0 ? d.X : i == 1 ? d.Y : d.Z;
            float a = i == 0 ? mn.X : i == 1 ? mn.Y : mn.Z, b = i == 0 ? mx.X : i == 1 ? mx.Y : mx.Z;
            if (MathF.Abs(di) < 1e-8f) { if (oi < a || oi > b) return false; continue; }
            float t1 = (a - oi) / di, t2 = (b - oi) / di;
            float s = -1;
            if (t1 > t2) { (t1, t2) = (t2, t1); s = 1; }
            if (t1 > tmin) { tmin = t1; axis = i; sign = s; }
            tmax = Math.Min(tmax, t2);
            if (tmin > tmax) return false;
        }
        if (tmax < 0) return false;
        t = tmin;
        normal = axis == 0 ? ((int)sign, 0, 0) : axis == 1 ? (0, (int)sign, 0) : (0, 0, (int)sign);
        return true;
    }

    /// <summary>Lowest part cell (the ground plane is half a cell below it).</summary>
    int GroundY => Document == null || Document.Parts.Count == 0 ? 0 : Document.Parts.Min(p => VehicleDocument.Box(p, Catalog?[p.B.Part]).Min.Y);

    (int X, int Y, int Z)? GroundCell(Point p, int y)
    {
        var (o, d) = Ray(p);
        float plane = y - 0.5f;
        if (MathF.Abs(d.Y) < 1e-6f) return null;
        float t = (plane - o.Y) / d.Y;
        if (t < 0) return null;
        var h = o + d * t;
        return ((int)MathF.Round(h.X), y, (int)MathF.Round(h.Z));
    }

    /// <summary>Where the Place tool would put the part's origin cell for the mouse at <paramref name="p"/>.</summary>
    (int X, int Y, int Z)? PlaceCell(Point p)
    {
        if (Document == null) return null;
        var hit = Pick(p);
        (int X, int Y, int Z) cell;
        (int X, int Y, int Z) n;
        if (hit is { } h) { n = h.Normal; cell = (h.Cell.X + n.X, h.Cell.Y + n.Y, h.Cell.Z + n.Z); }
        else if (GroundCell(p, GroundY) is { } g) { cell = g; n = (0, 1, 0); }
        else return null;
        if (PlacePart == null) return cell;
        // a multi-cell part: shift its origin along the face normal until its footprint is free
        var occ = Document.Occupancy(Catalog);
        var probe = new VehicleDocument.Part { B = new BlueprintBlock { Part = PlacePart.Id } };
        probe.B.Orientation = PlaceOrientation;
        for (int step = 0; step < 12; step++)
        {
            probe.X = cell.X; probe.Y = cell.Y; probe.Z = cell.Z;
            if (!VehicleDocument.Cells(probe, PlacePart).Any(occ.ContainsKey)) return cell;
            cell = (cell.X + n.X, cell.Y + n.Y, cell.Z + n.Z);
        }
        return cell;
    }

    // ------------------------------------------------------------------ mouse

    Point _last, _downAt; MouseButtons _btn; bool _dragging; (int X, int Y, int Z)? _hover;
    bool _dragBegun;
    // drag of the selection: the plane (through the clicked point) and the cells moved so far
    Vector3 _dragOrigin, _dragNormal; (int X, int Y, int Z) _dragApplied;

    Vector3 Forward() => Vector3.Normalize(_target - Eye());
    /// <summary>Screen position of a world point (null behind the camera).</summary>
    public Point? ToScreen(Vector3 w)
    {
        var c = Vector4.Transform(new Vector4(w, 1), View() * Proj());
        if (c.W <= 1e-4f) return null;
        return new Point((int)((c.X / c.W * 0.5f + 0.5f) * _gl.Width), (int)((0.5f - c.Y / c.W * 0.5f) * _gl.Height));
    }

    /// <summary>Normal of the drag plane, as in the 3D View: Y (the XZ ground plane) when the camera looks more than 45°
    /// down or up, else the upright world plane closest to facing the camera (yaw snapped to 90°): Z (the XY plane) when
    /// it looks mostly along Z, X (the ZY plane) when it looks mostly along X.</summary>
    public Vector3 DragPlaneNormal()
    {
        if (MathF.Abs(_pitch) > MathF.PI / 4) return Vector3.UnitY;
        var f = Forward();
        return MathF.Abs(f.X) > MathF.Abs(f.Z) ? Vector3.UnitX : Vector3.UnitZ;
    }

    public string DragPlaneName() { var n = DragPlaneNormal(); return n == Vector3.UnitY ? "XZ plane" : n == Vector3.UnitZ ? "XY plane" : "ZY plane"; }

    /// <summary>The world axis closest to the view direction (same 45° rule as the drag plane) and the quarter-turn sign
    /// that turns a part clockwise as the camera sees it (+90° about an axis pointing away from the viewer).</summary>
    public (int Axis, int Clockwise) ViewAxis()
    {
        var f = Forward();
        if (MathF.Abs(_pitch) > MathF.PI / 4) return (1, f.Y < 0 ? -1 : 1);
        return MathF.Abs(f.X) > MathF.Abs(f.Z) ? (0, f.X < 0 ? -1 : 1) : (2, f.Z < 0 ? -1 : 1);
    }

    /// <summary>Intersects the mouse ray with the plane through <paramref name="through"/> with normal <paramref name="n"/>.</summary>
    Vector3? RayPlane(Point p, Vector3 through, Vector3 n)
    {
        var (o, d) = Ray(p);
        float dn = Vector3.Dot(d, n);
        if (MathF.Abs(dn) < 1e-5f) return null;
        float t = Vector3.Dot(through - o, n) / dn;
        return t < 0 ? null : o + d * t;
    }

    void OnDown(object? s, MouseEventArgs e)
    {
        _gl.Focus();
        _last = _downAt = e.Location; _btn = e.Button; _dragging = false; _dragBegun = false;
        if (e.Button != MouseButtons.Left || Document == null) return;
        if (Tool == VehicleTool.Select && Pick(e.Location) is { } h && Document.Selection.Contains(h.Part)) BeginDrag(e.Location, h.T);
    }

    void BeginDrag(Point at, float t)
    {
        var (o, d) = Ray(at);
        _dragOrigin = o + d * t; _dragNormal = DragPlaneNormal(); _dragApplied = (0, 0, 0);
        _dragging = true; _dragBegun = false;
    }

    /// <summary>Moves the selection for the mouse at <paramref name="at"/> (cells from the drag start, in the drag plane;
    /// <paramref name="vertical"/>: up / down only). Returns the total cell offset.</summary>
    (int X, int Y, int Z) DragTo(Point at, bool vertical)
    {
        if (Document == null) return _dragApplied;
        Vector3 delta;
        if (vertical)
        {
            // an upright plane facing the camera; only its height counts
            var f = Forward(); var hn = new Vector3(f.X, 0, f.Z);
            var n = hn.LengthSquared() < 1e-6f ? Vector3.UnitZ : Vector3.Normalize(hn);
            if (RayPlane(at, _dragOrigin, n) is not { } h) return _dragApplied;
            delta = new Vector3(0, h.Y - _dragOrigin.Y, 0);
        }
        else
        {
            if (RayPlane(at, _dragOrigin, _dragNormal) is not { } h) return _dragApplied;
            delta = h - _dragOrigin;
            delta -= _dragNormal * Vector3.Dot(delta, _dragNormal);   // exactly in the plane
        }
        var now = ((int)MathF.Round(delta.X), (int)MathF.Round(delta.Y), (int)MathF.Round(delta.Z));
        int mx = now.Item1 - _dragApplied.X, my = now.Item2 - _dragApplied.Y, mz = now.Item3 - _dragApplied.Z;
        if (mx != 0 || my != 0 || mz != 0)
        {
            if (!_dragBegun) { Document.Begin("move parts"); _dragBegun = true; }
            foreach (var p in Document.Selection) { p.X += mx; p.Y += my; p.Z += mz; }
            _dragApplied = now;
            Document.Commit();
            Edited?.Invoke("move");
        }
        return _dragApplied;
    }

    /// <summary>A drag of the selected part under view point <paramref name="from"/> to <paramref name="to"/> (scripted
    /// tests: the same code as a mouse drag). Returns the cells moved, or null when no selected part is under the start.</summary>
    public (int X, int Y, int Z)? DragSelection(Point from, Point to, bool vertical = false)
    {
        if (Document == null || Pick(from) is not { } h || !Document.Selection.Contains(h.Part)) return null;
        BeginDrag(from, h.T);
        var r = DragTo(to, vertical);
        _dragging = false;
        return r;
    }

    /// <summary>A left click at view coordinates (scripted tests): what the mouse would do with the current tool.</summary>
    public void ClickAt(int x, int y)
    {
        _downAt = new Point(x, y); _dragBegun = false; _dragging = false;
        if (Tool == VehicleTool.Place) _hover = PlaceCell(_downAt);
        OnUp(this, new MouseEventArgs(MouseButtons.Left, 1, x, y, 0));
    }

    /// <summary>The Place tool's ghost for the mouse at view coordinates (scripted tests): its cell and attach status.</summary>
    public ((int X, int Y, int Z)? Cell, int Status) HoverAt(int x, int y)
    {
        _hover = PlaceCell(new Point(x, y));
        _gl.Invalidate();
        if (_hover is not { } c || PlacePart == null || Document == null) return (null, -1);
        var probe = new VehicleDocument.Part { X = c.X, Y = c.Y, Z = c.Z, B = new BlueprintBlock { Part = PlacePart.Id } };
        probe.B.Orientation = PlaceOrientation;
        return (c, VehicleConnectivity.PlacementStatus(Document, probe, Catalog, Connectivity));
    }

    // ------------------------------------------------------------------ parts dragged from the library

    bool _partDrag; int _dragNatural; PartInfo? _dragPart;

    /// <summary>A part from the library is dragged over the view: the ghost follows the face under the mouse, turned so an
    /// attachable face meets it (<see cref="PredictOrientation"/>), coloured by its attachment status.</summary>
    void PartDragOver(DragEventArgs e, bool enter)
    {
        if (e.Data?.GetDataPresent(PartFormat) == true && Catalog != null && Document != null
            && uint.TryParse(e.Data.GetData(PartFormat) as string, System.Globalization.NumberStyles.HexNumber, null, out var id) && Catalog[id] is { } part)
        {
            // the part's own orientation (springs face down), or the one R gave it when it was already the part to place
            if (enter || !_partDrag || _dragPart != part) { _dragNatural = PlacePart == part ? PlaceOrientation : part.DefaultOrientation; _dragPart = part; PlacePart = part; }
            _partDrag = true;
            var pt = _gl.PointToClient(new Point(e.X, e.Y));
            PlaceOrientation = PredictOrientation(pt, _dragNatural);
            var c = PlaceCell(pt);
            e.Effect = c != null ? DragDropEffects.Copy : DragDropEffects.None;
            if (c != _hover)
            {
                _hover = c;
                if (c is { } pc) Status?.Invoke($"{part.Name} at ({pc.X}, {pc.Y}, {pc.Z}), orientation {PlaceOrientation}: " + StatusWords(GhostAt(pc)) + " — drop to place it");
            }
            _gl.Invalidate();
        }
        else e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
    }

    void PartDragDrop(DragEventArgs e)
    {
        if (_partDrag)
        {
            _partDrag = false;
            var pt = _gl.PointToClient(new Point(e.X, e.Y));
            PlaceOrientation = PredictOrientation(pt, _dragNatural);
            if (PlaceCell(pt) is { } c) PlaceRequested?.Invoke(c.X, c.Y, c.Z);
            _hover = null; _gl.Invalidate();
            return;
        }
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] f) FilesDropped?.Invoke(f);
    }

    int GhostAt((int X, int Y, int Z) c)
    {
        if (Document == null || PlacePart == null) return -1;
        var probe = new VehicleDocument.Part { X = c.X, Y = c.Y, Z = c.Z, B = new BlueprintBlock { Part = PlacePart.Id } };
        probe.B.Orientation = PlaceOrientation;
        return VehicleConnectivity.PlacementStatus(Document, probe, Catalog, Connectivity);
    }

    static string StatusWords(int st) => st == 0 ? "attaches here (green)" : st == 1 ? "floating here (orange)" : st == 2 ? "blocked (red)" : "";

    /// <summary>
    /// The orientation a part dropped at view point <paramref name="p"/> should get: <paramref name="natural"/> (the part's
    /// own default, e.g. springs facing down, or what R made it) when an attachable face of it then meets the surface under
    /// the mouse; else the orientation closest to it (same "up" first, then same front) that attaches. Nothing attaches (or
    /// an empty vehicle): <paramref name="natural"/>.
    /// </summary>
    public int PredictOrientation(Point p, int natural)
    {
        if (Document == null || PlacePart == null || Document.Parts.Count == 0 || PlacePart.Attach == null) return natural;
        int keep = PlaceOrientation;
        try
        {
            var up = Vector3.TransformNormal(Vector3.UnitY, Orientations.All[natural]);
            var fw = Vector3.TransformNormal(Vector3.UnitZ, Orientations.All[natural]);
            // wheels and springs keep their "down" (the game's vehicles only turn them about the vertical): only their yaw varies
            bool keepUp = PlacePart.IsWheel || PlacePart.Class == "objDefId_vehicleBlockSpring";
            var order = Enumerable.Range(0, Orientations.All.Length)
                .Where(i => !keepUp || Vector3.Dot(Vector3.TransformNormal(Vector3.UnitY, Orientations.All[i]), up) > 0.99f)
                .OrderByDescending(i => 2 * Vector3.Dot(Vector3.TransformNormal(Vector3.UnitY, Orientations.All[i]), up) + Vector3.Dot(Vector3.TransformNormal(Vector3.UnitZ, Orientations.All[i]), fw));
            foreach (int o in order)
            {
                PlaceOrientation = o;
                if (PlaceCell(p) is not { } c) return natural;
                if (GhostAt(c) == 0) return o;
            }
            return natural;
        }
        finally { PlaceOrientation = keep; }
    }

    /// <summary>Scripted tests: the same code as a part dragged from the library over view point <paramref name="at"/>
    /// (and dropped there when <paramref name="drop"/>). Returns the ghost's cell, orientation and status.</summary>
    public ((int X, int Y, int Z)? Cell, int Orientation, int Status) SimulatePartDrag(PartInfo part, Point at, bool drop)
    {
        var data = new DataObject(PartFormat, part.Id.ToString("X8"));
        var sp = _gl.PointToScreen(at);
        if (_partDrag && _dragPart != part) _partDrag = false;   // another part: a new drag
        PartDragOver(new DragEventArgs(data, 1, sp.X, sp.Y, DragDropEffects.Copy, DragDropEffects.None), !_partDrag);
        var r = (_hover, PlaceOrientation, _hover is { } h ? GhostAt(h) : -1);
        if (drop) PartDragDrop(new DragEventArgs(data, 0, sp.X, sp.Y, DragDropEffects.Copy, DragDropEffects.Copy));
        return r;
    }

    /// <summary>Scripted tests: the renderer's batches of a part's model.</summary>
    public IEnumerable<string> DescribePart(PartInfo p) => Catalog?.Model(p) is { } m ? _r.DescribeBatches(m) : Enumerable.Empty<string>();

    /// <summary>The GL context exists (part thumbnails can be rendered).</summary>
    public bool GlReady => _ready && _gl.IsHandleCreated;

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    static IntPtr XY(Point p) => (IntPtr)((p.Y & 0xFFFF) << 16 | (p.X & 0xFFFF));

    /// <summary>Scripted tests with real window messages to the view (no global input): a left-button drag from
    /// <paramref name="a"/> to <paramref name="b"/> in <paramref name="steps"/> moves, through the view's own mouse handlers.</summary>
    public void PostDrag(Point a, Point b, int steps = 8)
    {
        const int WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, MK_LBUTTON = 1;
        var h = _gl.Handle;
        PostMessage(h, WM_MOUSEMOVE, IntPtr.Zero, XY(a)); Application.DoEvents();
        PostMessage(h, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, XY(a)); Application.DoEvents();
        for (int i = 1; i <= steps; i++)
        {
            var q = new Point(a.X + (b.X - a.X) * i / steps, a.Y + (b.Y - a.Y) * i / steps);
            PostMessage(h, WM_MOUSEMOVE, (IntPtr)MK_LBUTTON, XY(q)); Application.DoEvents();
        }
        PostMessage(h, WM_LBUTTONUP, IntPtr.Zero, XY(b)); Application.DoEvents();
    }

    /// <summary>A mouse move (no button) by window messages to view point <paramref name="a"/>.</summary>
    public void PostMove(Point a)
    {
        PostMessage(_gl.Handle, 0x200, IntPtr.Zero, XY(new Point(a.X + 1, a.Y))); Application.DoEvents();
        PostMessage(_gl.Handle, 0x200, IntPtr.Zero, XY(a)); Application.DoEvents();
    }

    /// <summary>A left click by window messages at view point <paramref name="a"/>.</summary>
    public void PostClick(Point a)
    {
        var h = _gl.Handle;
        PostMessage(h, 0x200, IntPtr.Zero, XY(a)); Application.DoEvents();
        PostMessage(h, 0x201, (IntPtr)1, XY(a)); Application.DoEvents();
        PostMessage(h, 0x202, IntPtr.Zero, XY(a)); Application.DoEvents();
    }

    /// <summary>A right click by window messages at view point <paramref name="a"/>.</summary>
    public void PostRightClick(Point a)
    {
        var h = _gl.Handle;
        PostMessage(h, 0x200, IntPtr.Zero, XY(a)); Application.DoEvents();
        PostMessage(h, 0x204, (IntPtr)2, XY(a)); Application.DoEvents();
        PostMessage(h, 0x205, IntPtr.Zero, XY(a)); Application.DoEvents();
    }

    /// <summary>A key press by window messages to the focused view (WM_KEYDOWN / WM_KEYUP through the message loop).</summary>
    public void PostKey(Keys k)
    {
        _gl.Focus();
        PostMessage(_gl.Handle, 0x100, (IntPtr)(int)k, (IntPtr)1); Application.DoEvents();
        PostMessage(_gl.Handle, 0x101, (IntPtr)(int)k, unchecked((IntPtr)(int)0xC0000001)); Application.DoEvents();
    }

    /// <summary>View size (scripted tests aim at its centre).</summary>
    public Size ViewSize => _gl.ClientSize;

    void OnUp(object? s, MouseEventArgs e)
    {
        bool click = Math.Abs(e.X - _downAt.X) + Math.Abs(e.Y - _downAt.Y) < 5;
        if (e.Button == MouseButtons.Right && click && Tool == VehicleTool.Place && PlacePart != null) RotateGhostRequested?.Invoke();
        if (e.Button == MouseButtons.Left && click && Document != null && !_dragBegun)
        {
            switch (Tool)
            {
                case VehicleTool.Select:
                    var h = Pick(e.Location);
                    bool add = (ModifierKeys & (Keys.Control | Keys.Shift)) != 0;
                    if (!add) Document.Selection.Clear();
                    if (h != null) { if (add && Document.Selection.Contains(h.Value.Part)) Document.Selection.Remove(h.Value.Part); else Document.Selection.Add(h.Value.Part); }
                    SelectionChanged?.Invoke();
                    break;
                case VehicleTool.Place:
                    if (PlaceCell(e.Location) is { } c) PlaceRequested?.Invoke(c.X, c.Y, c.Z);
                    break;
                case VehicleTool.Paint:
                    if (Pick(e.Location) is { } ph) PaintClicked?.Invoke(ph.Part, (ModifierKeys & Keys.Alt) != 0);
                    break;
            }
        }
        _dragging = false; _btn = MouseButtons.None;
        _gl.Invalidate();
    }

    void OnMove(object? s, MouseEventArgs e)
    {
        int dx = e.X - _last.X, dy = e.Y - _last.Y;
        _last = e.Location;
        if (_btn == MouseButtons.Right) { _yaw -= dx * 0.008f; _pitch = Math.Clamp(_pitch + dy * 0.008f, -1.5f, 1.5f); _gl.Invalidate(); return; }
        if (_btn == MouseButtons.Middle)
        {
            var f = Vector3.Normalize(_target - Eye()); var r = Vector3.Normalize(Vector3.Cross(f, Vector3.UnitY)); var u = Vector3.Cross(r, f);
            float k = _dist * 0.0018f;
            _target += -r * dx * k + u * dy * k; _gl.Invalidate(); return;
        }
        if (_btn == MouseButtons.Left && _dragging && Document != null && Document.Selection.Count > 0)
        {
            bool vertical = (ModifierKeys & Keys.Shift) != 0;
            var d = DragTo(e.Location, vertical);
            Status?.Invoke($"Move {(vertical ? "up / down" : "in the " + DragPlaneName())}: {d.X:+0;-0;0}, {d.Y:+0;-0;0}, {d.Z:+0;-0;0} cells (Shift: up / down only; Ctrl+Z undoes)");
            return;
        }
        if (Tool == VehicleTool.Place && PlacePart != null)
        {
            var c = PlaceCell(e.Location);
            if (c != _hover)
            {
                _hover = c; _gl.Invalidate();
                if (c is { } pc && Document != null)
                {
                    var probe = new VehicleDocument.Part { X = pc.X, Y = pc.Y, Z = pc.Z, B = new BlueprintBlock { Part = PlacePart.Id } };
                    probe.B.Orientation = PlaceOrientation;
                    int st = VehicleConnectivity.PlacementStatus(Document, probe, Catalog, Connectivity);
                    Status?.Invoke($"{PlacePart.Name} at ({pc.X}, {pc.Y}, {pc.Z}): " + (st == 0 ? "attaches here (green)" : st == 1
                        ? "floating here: no attachable face meets the vehicle (orange; the garage shows its hazard sign) — turn it with X / Y / Z"
                        : "blocked: it would fill a cell another part fills (red)"));
                }
            }
        }
        else if (Tool is VehicleTool.Select or VehicleTool.Paint)
        {
            var h = Pick(e.Location);
            var c = h?.Cell;
            if (c != _hover) { _hover = c; _gl.Invalidate(); if (h != null) Status?.Invoke(Describe(h.Value.Part)); }
            var hp = h?.Part;
            if (hp != _tipPart) { _tipPart = hp; _tip.SetToolTip(_gl, hp == null ? "" : TipText(hp)); }
        }
    }

    /// <summary>The tooltip of a part under the mouse: name, attachment, and whether it is the AI driver's seat.</summary>
    public string TipText(VehicleDocument.Part p)
    {
        var info = Catalog?[p.B.Part];
        var conn = Connectivity;
        string att = conn == null ? "" : conn.Overlapping.Contains(p) ? "Blocked: shares cells with another part" : conn.Floating.Contains(p) ? "Not attached: falls off (hazard)" : "Attached";
        return $"{info?.Name ?? $"unknown part 0x{p.B.Part:X8}"}{(info != null ? $"  ({info.StoreCategory})" : "")}\n{att}" +
               (info?.IsAiSeat == true ? "\nAI DRIVER SEAT: the game's AI racers drive from it (a player's vehicle needs a driver seat)" : "");
    }

    /// <summary>The hover text of a part: name, cell, orientation, attachment, AI seat, paint, setting.</summary>
    public string Describe(VehicleDocument.Part p)
    {
        var info = Catalog?[p.B.Part];
        var conn = Connectivity;
        string att = conn == null ? "" : conn.Overlapping.Contains(p) ? " — BLOCKED (shares cells)" : conn.Floating.Contains(p) ? " — NOT ATTACHED (hazard)" : " — attached";
        if (info?.IsAiSeat == true) att += " — AI DRIVER SEAT (the game's AI racers drive from it; a player's vehicle needs a driver seat)";
        return $"{info?.Name ?? $"unknown part 0x{p.B.Part:X8}"} at ({p.X}, {p.Y}, {p.Z}), orientation {p.Orientation}{att}" +
               (p.B.Painted != 0 ? $", painted #{p.B.Paint >> 8:X6}" : ", default colour") + (p.B.Setting != 0 ? ", " + VehicleSettings.NameOf(p.B.Setting, info?.IsPropeller == true) : "");
    }

    void OnWheel(object? s, MouseEventArgs e)
    {
        _dist = Math.Clamp(_dist * MathF.Pow(0.88f, e.Delta / 120f), 1.2f, 600f);
        _gl.Invalidate();
    }

    // ------------------------------------------------------------------ drawing

    // attachment status like the garage: green attached, orange floating (hazard), red blocked (fills a taken cell)
    static readonly Vector3 Green = new(0.25f, 0.95f, 0.3f), Orange = new(1f, 0.55f, 0.05f), Red = new(1f, 0.12f, 0.08f);
    static Vector3 StatusColour(int status) => status == 0 ? Green : status == 1 ? Orange : Red;

    VehicleConnectivity? _conn; string _connSig = "";

    /// <summary>Which parts hold together (recomputed when the parts change).</summary>
    public VehicleConnectivity? Connectivity
    {
        get
        {
            if (Document == null) return null;
            var sb = new System.Text.StringBuilder();
            foreach (var p in Document.Parts) sb.Append(p.X).Append(',').Append(p.Y).Append(',').Append(p.Z).Append(',').Append(p.B.RotXBits ^ p.B.RotYBits * 3 ^ p.B.RotZBits * 7).Append(',').Append(p.B.Part).Append(';');
            var sig = sb.ToString();
            if (_conn == null || sig != _connSig || !ReferenceEquals(_conn.Parts.FirstOrDefault(), Document.Parts.FirstOrDefault()))
            { _conn = new VehicleConnectivity(Document.Parts, Catalog); _connSig = sig; }
            return _conn;
        }
    }

    int StatusOf(VehicleDocument.Part p, VehicleConnectivity c) => c.Overlapping.Contains(p) ? 2 : c.Floating.Contains(p) ? 1 : 0;

    /// <summary>The garage's hazard sign over a floating part: a triangle with "!" facing the camera, drawn on top.</summary>
    void Hazard(List<(Vector3, Vector3, Vector3)> l, Vector3 at)
    {
        var f = Vector3.Normalize(_target - Eye()); var r = Vector3.Normalize(Vector3.Cross(f, Vector3.UnitY)); var u = Vector3.Cross(r, f);
        float s = 0.32f;
        var a = at + u * s; var b = at - u * s * 0.6f - r * s * 0.95f; var c = at - u * s * 0.6f + r * s * 0.95f;
        var col = new Vector3(1f, 0.85f, 0.1f);
        l.Add((a, b, col)); l.Add((b, c, col)); l.Add((c, a, col));
        l.Add((at + u * s * 0.45f, at - u * s * 0.12f, new Vector3(1f, 0.2f, 0.1f)));
        l.Add((at - u * s * 0.3f, at - u * s * 0.4f, new Vector3(1f, 0.2f, 0.1f)));
    }

    /// <summary>The colour a part shows: its paint when painted, else its default colour (objparams +0x130).</summary>
    Vector3 PaintOf(VehicleDocument.Part p)
    {
        uint c = p.B.Painted != 0 ? p.B.Paint : Catalog?[p.B.Part]?.DefaultPaint ?? p.B.Paint;
        return new Vector3((c >> 24) / 255f, (c >> 16 & 0xFF) / 255f, (c >> 8 & 0xFF) / 255f);
    }

    static Matrix4x4 PartMatrix(int orientation, int x, int y, int z) => Orientations.All[orientation] * Matrix4x4.CreateTranslation(x, y, z);

    void Render(bool swap = true)
    {
        if (!_ready) return;
        _gl.MakeCurrent();
        int W = _gl.Width, H = _gl.Height;
        if (W < 1 || H < 1) return;
        GL.Viewport(0, 0, W, H);
        GL.ClearColor(0.30f, 0.33f, 0.38f, 1);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        var vp = View() * Proj();
        _r.Begin(vp, Eye());
        var lines = new List<(Vector3, Vector3, Vector3)>();
        var top = new List<(Vector3, Vector3, Vector3)>();
        if (Document != null)
        {
            var conn = Connectivity!;
            foreach (var p in Document.Parts)
            {
                var info = Catalog?[p.B.Part];
                var model = info != null ? Catalog!.Model(info) : null;
                bool sel = Document.Selection.Contains(p);
                int st = StatusOf(p, conn);
                var over = sel ? new Vector4(StatusColour(st), 0.40f) : st == 1 ? new Vector4(Orange, 0.30f) : Vector4.Zero;
                if (model != null) _r.DrawModel(model, PartMatrix(p.Orientation, p.X, p.Y, p.Z), PaintOf(p), over);
                else
                {
                    // unknown part: a red box
                    VehicleRenderer.Box(lines, new Vector3(p.X - 0.45f, p.Y - 0.45f, p.Z - 0.45f), new Vector3(p.X + 0.45f, p.Y + 0.45f, p.Z + 0.45f), new Vector3(1, 0.15f, 0.1f));
                }
                if (st == 1 && ShowHazards)
                {
                    var (ha, hb) = VehicleDocument.Box(p, info);
                    Hazard(top, new Vector3((ha.X + hb.X) / 2f, hb.Y + 0.95f, (ha.Z + hb.Z) / 2f));
                }
                if (sel)
                {
                    var (a, b) = VehicleDocument.Box(p, info);
                    VehicleRenderer.Box(lines, new Vector3(a.X, a.Y, a.Z) - new Vector3(0.5f), new Vector3(b.X, b.Y, b.Z) + new Vector3(0.5f), StatusColour(st));
                }
            }
            _r.FlushTransparent();
            // ghost of the part being placed
            if ((Tool == VehicleTool.Place || _partDrag) && PlacePart != null && _hover is { } hc)
            {
                var model = Catalog?.Model(PlacePart);
                var probe = new VehicleDocument.Part { X = hc.X, Y = hc.Y, Z = hc.Z, B = new BlueprintBlock { Part = PlacePart.Id } };
                probe.B.Orientation = PlaceOrientation;
                int gst = VehicleConnectivity.PlacementStatus(Document, probe, Catalog, conn);
                GhostStatus = gst;
                if (model != null)
                {
                    uint c = PlacePart.DefaultPaint;
                    _r.DrawModel(model, PartMatrix(PlaceOrientation, hc.X, hc.Y, hc.Z), new Vector3((c >> 24) / 255f, (c >> 16 & 0xFF) / 255f, (c >> 8 & 0xFF) / 255f), new Vector4(StatusColour(gst), 0.45f), 0.55f);
                }
                var (a, b) = VehicleDocument.Box(probe, PlacePart);
                VehicleRenderer.Box(lines, new Vector3(a.X, a.Y, a.Z) - new Vector3(0.5f), new Vector3(b.X, b.Y, b.Z) + new Vector3(0.5f), StatusColour(gst));
            }
            else if (_hover is { } hv && Tool != VehicleTool.Place)
                VehicleRenderer.Box(lines, new Vector3(hv.X, hv.Y, hv.Z) - new Vector3(0.5f), new Vector3(hv.X, hv.Y, hv.Z) + new Vector3(0.5f), Tool == VehicleTool.Paint ? new Vector3(0.9f, 0.3f, 0.9f) : new Vector3(0.9f, 0.9f, 0.9f));
            if (ShowGrid) Grid(lines);
        }
        _r.FlushTransparent();
        _r.Lines(lines);
        _r.Lines(top, onTop: true, width: 2.5f);
        if (swap) _gl.SwapBuffers();
    }

    /// <summary>Ground grid under the vehicle (half a cell below its lowest cell), 8 cells around it, and the cell axes.</summary>
    void Grid(List<(Vector3, Vector3, Vector3)> l)
    {
        var parts = Document!.Parts;
        int x0 = -4, x1 = 4, z0 = -4, z1 = 4;
        if (parts.Count > 0)
        {
            var (mn, mx) = Bounds(parts);
            x0 = (int)MathF.Floor(mn.X) - 6; x1 = (int)MathF.Ceiling(mx.X) + 6; z0 = (int)MathF.Floor(mn.Z) - 6; z1 = (int)MathF.Ceiling(mx.Z) + 6;
        }
        float y = GroundY - 0.5f - 0.002f;
        var c = new Vector3(0.45f, 0.48f, 0.53f);
        for (int x = x0; x <= x1; x++) l.Add((new Vector3(x - 0.5f, y, z0 - 0.5f), new Vector3(x - 0.5f, y, z1 - 0.5f), c));
        for (int z = z0; z <= z1; z++) l.Add((new Vector3(x0 - 0.5f, y, z - 0.5f), new Vector3(x1 - 0.5f, y, z - 0.5f), c));
        // front of the vehicle: +Z (part models face +Z)
        l.Add((new Vector3(0, y, z1 - 0.5f), new Vector3(0, y, z1 + 1.5f), new Vector3(0.3f, 0.5f, 1f)));
        l.Add((new Vector3(x1 - 0.5f, y, 0), new Vector3(x1 + 1.5f, y, 0), new Vector3(1f, 0.3f, 0.3f)));
    }

    int _fbo, _fboCol, _fboDepth, _fboSize;

    /// <summary>A picture of one part as the Parts Store shows it (its default colour, from the front right, above), for
    /// the parts library. Null until the view's GL context exists or when the part has no model.</summary>
    public Bitmap? PartThumbnail(PartInfo p, int size)
    {
        if (!_ready || Catalog == null || !IsHandleCreated) return null;
        var model = Catalog.Model(p);
        if (model == null) return null;
        _gl.MakeCurrent();
        int S = size * 2;
        if (_fboSize != S)
        {
            if (_fbo != 0) { GL.DeleteFramebuffer(_fbo); GL.DeleteRenderbuffer(_fboCol); GL.DeleteRenderbuffer(_fboDepth); }
            _fbo = GL.GenFramebuffer(); _fboCol = GL.GenRenderbuffer(); _fboDepth = GL.GenRenderbuffer();
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _fboCol);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.Rgba8, S, S);
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _fboDepth);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, S, S);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, _fboCol);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, _fboDepth);
            _fboSize = S;
        }
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        try
        {
            GL.Viewport(0, 0, S, S);
            GL.ClearColor(0.86f, 0.88f, 0.91f, 1);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            var probe = new VehicleDocument.Part { B = new BlueprintBlock { Part = p.Id } };
            var (a, b) = VehicleDocument.Box(probe, p);
            var mn = new Vector3(a.X, a.Y, a.Z) - new Vector3(0.5f); var mx = new Vector3(b.X, b.Y, b.Z) + new Vector3(0.5f);
            var c = (mn + mx) / 2; float r = Math.Max(0.5f, (mx - mn).Length() / 2);
            float fov = 30 * MathF.PI / 180;
            var eye = c + Vector3.Normalize(new Vector3(0.8f, 0.6f, 1f)) * (r / MathF.Sin(fov / 2) * 0.95f);
            var vp = Matrix4x4.CreateLookAt(eye, c, Vector3.UnitY) * Matrix4x4.CreatePerspectiveFieldOfView(fov, 1, 0.05f, 500f);
            _r.Begin(vp, eye);
            uint col = p.DefaultPaint;
            _r.DrawModel(model, PartMatrix(0, 0, 0, 0), new Vector3((col >> 24) / 255f, (col >> 16 & 0xFF) / 255f, (col >> 8 & 0xFF) / 255f), Vector4.Zero);
            _r.FlushTransparent();
            var px = new byte[S * S * 4];
            GL.ReadPixels(0, 0, S, S, PixelFormat.Bgra, PixelType.UnsignedByte, px);
            using var big = new Bitmap(S, S, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var bd = big.LockBits(new Rectangle(0, 0, S, S), System.Drawing.Imaging.ImageLockMode.WriteOnly, big.PixelFormat);
            for (int i = 3; i < px.Length; i += 4) px[i] = 255;
            for (int y = 0; y < S; y++) System.Runtime.InteropServices.Marshal.Copy(px, (S - 1 - y) * S * 4, bd.Scan0 + y * bd.Stride, S * 4);
            big.UnlockBits(bd);
            var small = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(big, new Rectangle(0, 0, size, size));
            }
            return small;
        }
        finally
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            _gl.Invalidate();
        }
    }

    /// <summary>Renders a frame and reads it back (screenshots, package thumbnails).</summary>
    public Bitmap Capture()
    {
        int w = Math.Max(1, _gl.Width), h = Math.Max(1, _gl.Height);
        var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        if (!_ready) return bmp;
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

    /// <summary>A small square picture of the vehicle (package thumbnail: PNG, 64×64).</summary>
    public byte[] Thumbnail(int size = 64)
    {
        bool grid = ShowGrid; ShowGrid = false;
        var sel = Document?.Selection.ToList();
        Document?.Selection.Clear();
        using var full = Capture();
        ShowGrid = grid;
        if (sel != null) foreach (var p in sel) Document!.Selection.Add(p);
        int s = Math.Min(full.Width, full.Height);
        using var t = new Bitmap(size, size);
        using (var g = Graphics.FromImage(t))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(full, new Rectangle(0, 0, size, size), new Rectangle((full.Width - s) / 2, (full.Height - s) / 2, s, s), GraphicsUnit.Pixel);
        }
        var ms = new MemoryStream();
        t.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        _gl.Invalidate();
        return ms.ToArray();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        if (disposing && _ready)
        {
            _gl.MakeCurrent(); _r.Dispose();
            if (_fbo != 0) { GL.DeleteFramebuffer(_fbo); GL.DeleteRenderbuffer(_fboCol); GL.DeleteRenderbuffer(_fboDepth); }
        }
        base.Dispose(disposing);
    }
}
