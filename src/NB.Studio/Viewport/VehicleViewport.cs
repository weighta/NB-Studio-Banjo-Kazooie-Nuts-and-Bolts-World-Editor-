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
/// Select: click (Ctrl/Shift adds), drag a selected part to move it on the ground plane (Shift: up/down).
/// Place: the ghost follows the face under the mouse; click places it. Paint: click paints a part (Alt+click picks its
/// colour). Keys: X / Y / Z (or R) rotate 90° (Shift: the other way), arrows / PgUp / PgDn move, Del deletes.
/// </summary>
public sealed class VehicleViewport : UserControl
{
    readonly GLControl _gl;
    readonly VehicleRenderer _r = new();
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

    /// <summary>A part was clicked with the Paint tool (Alt: pick its colour instead).</summary>
    public event Action<VehicleDocument.Part, bool>? PaintClicked;
    /// <summary>The Place tool wants a part at this cell.</summary>
    public event Action<int, int, int>? PlaceRequested;
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
    (int X, int Y, int Z) _dragCell; bool _dragBegun;

    void OnDown(object? s, MouseEventArgs e)
    {
        _gl.Focus();
        _last = _downAt = e.Location; _btn = e.Button; _dragging = false; _dragBegun = false;
        if (e.Button != MouseButtons.Left || Document == null) return;
        if (Tool == VehicleTool.Select && Pick(e.Location) is { } h && Document.Selection.Contains(h.Part))
        {
            _dragCell = GroundCell(e.Location, h.Part.Y) is { } g ? g : (h.Part.X, h.Part.Y, h.Part.Z);
            _dragging = true;
        }
    }

    /// <summary>A left click at view coordinates (scripted tests): what the mouse would do with the current tool.</summary>
    public void ClickAt(int x, int y)
    {
        _downAt = new Point(x, y); _dragBegun = false; _dragging = false;
        if (Tool == VehicleTool.Place) _hover = PlaceCell(_downAt);
        OnUp(this, new MouseEventArgs(MouseButtons.Left, 1, x, y, 0));
    }

    /// <summary>View size (scripted tests aim at its centre).</summary>
    public Size ViewSize => _gl.ClientSize;

    void OnUp(object? s, MouseEventArgs e)
    {
        bool click = Math.Abs(e.X - _downAt.X) + Math.Abs(e.Y - _downAt.Y) < 5;
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
            var any = Document.Selection.First();
            (int X, int Y, int Z) now;
            if ((ModifierKeys & Keys.Shift) != 0)
            {
                // vertical: one cell per 20 pixels
                int steps = -(e.Y - _downAt.Y) / 20;
                now = (_dragCell.X, _dragCell.Y + steps, _dragCell.Z);
            }
            else if (GroundCell(e.Location, any.Y) is { } g) now = g; else return;
            int mx = now.X - _dragCell.X, my = now.Y - _dragCell.Y, mz = now.Z - _dragCell.Z;
            if (mx != 0 || my != 0 || mz != 0)
            {
                if (!_dragBegun) { Document.Begin("move parts"); _dragBegun = true; }
                foreach (var p in Document.Selection) { p.X += mx; p.Y += my; p.Z += mz; }
                _dragCell = now;
                Document.Commit();
                Edited?.Invoke("move");
            }
            return;
        }
        if (Tool == VehicleTool.Place && PlacePart != null) { var c = PlaceCell(e.Location); if (c != _hover) { _hover = c; _gl.Invalidate(); } }
        else if (Tool is VehicleTool.Select or VehicleTool.Paint)
        {
            var h = Pick(e.Location);
            var c = h?.Cell;
            if (c != _hover) { _hover = c; _gl.Invalidate(); if (h != null) Status?.Invoke(Describe(h.Value.Part)); }
        }
    }

    string Describe(VehicleDocument.Part p)
    {
        var info = Catalog?[p.B.Part];
        return $"{info?.Name ?? $"unknown part 0x{p.B.Part:X8}"} at ({p.X}, {p.Y}, {p.Z}), orientation {p.Orientation}" +
               (p.B.Painted != 0 ? $", painted #{p.B.Paint >> 8:X6}" : ", default colour") + (p.B.Setting != 0 ? ", " + VehicleSettings.NameOf(p.B.Setting, info?.IsPropeller == true) : "");
    }

    void OnWheel(object? s, MouseEventArgs e)
    {
        _dist = Math.Clamp(_dist * MathF.Pow(0.88f, e.Delta / 120f), 1.2f, 600f);
        _gl.Invalidate();
    }

    // ------------------------------------------------------------------ drawing

    static readonly Vector4 SelOverlay = new(1f, 0.65f, 0.1f, 0.38f);

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
        if (Document != null)
        {
            foreach (var p in Document.Parts)
            {
                var info = Catalog?[p.B.Part];
                var model = info != null ? Catalog!.Model(info) : null;
                bool sel = Document.Selection.Contains(p);
                if (model != null) _r.DrawModel(model, PartMatrix(p.Orientation, p.X, p.Y, p.Z), PaintOf(p), sel ? SelOverlay : Vector4.Zero);
                else
                {
                    // unknown part: a red box
                    VehicleRenderer.Box(lines, new Vector3(p.X - 0.45f, p.Y - 0.45f, p.Z - 0.45f), new Vector3(p.X + 0.45f, p.Y + 0.45f, p.Z + 0.45f), new Vector3(1, 0.15f, 0.1f));
                }
                if (sel)
                {
                    var (a, b) = VehicleDocument.Box(p, info);
                    VehicleRenderer.Box(lines, new Vector3(a.X, a.Y, a.Z) - new Vector3(0.5f), new Vector3(b.X, b.Y, b.Z) + new Vector3(0.5f), new Vector3(1f, 0.7f, 0.15f));
                }
            }
            // ghost of the part being placed
            if (Tool == VehicleTool.Place && PlacePart != null && _hover is { } hc)
            {
                var model = Catalog?.Model(PlacePart);
                var probe = new VehicleDocument.Part { X = hc.X, Y = hc.Y, Z = hc.Z, B = new BlueprintBlock { Part = PlacePart.Id } };
                probe.B.Orientation = PlaceOrientation;
                if (model != null)
                {
                    uint c = PlacePart.DefaultPaint;
                    _r.DrawModel(model, PartMatrix(PlaceOrientation, hc.X, hc.Y, hc.Z), new Vector3((c >> 24) / 255f, (c >> 16 & 0xFF) / 255f, (c >> 8 & 0xFF) / 255f), new Vector4(0.3f, 0.9f, 0.4f, 0.35f), 0.55f);
                }
                var (a, b) = VehicleDocument.Box(probe, PlacePart);
                VehicleRenderer.Box(lines, new Vector3(a.X, a.Y, a.Z) - new Vector3(0.5f), new Vector3(b.X, b.Y, b.Z) + new Vector3(0.5f), new Vector3(0.3f, 1f, 0.4f));
            }
            else if (_hover is { } hv && Tool != VehicleTool.Place)
                VehicleRenderer.Box(lines, new Vector3(hv.X, hv.Y, hv.Z) - new Vector3(0.5f), new Vector3(hv.X, hv.Y, hv.Z) + new Vector3(0.5f), Tool == VehicleTool.Paint ? new Vector3(0.9f, 0.3f, 0.9f) : new Vector3(0.9f, 0.9f, 0.9f));
            if (ShowGrid) Grid(lines);
        }
        _r.Lines(lines);
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
        if (disposing && _ready) { _gl.MakeCurrent(); _r.Dispose(); }
        base.Dispose(disposing);
    }
}
