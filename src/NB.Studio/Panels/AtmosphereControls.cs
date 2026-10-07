using System.Drawing.Drawing2D;
using System.Globalization;

namespace NB.Studio.Panels;

/// <summary>Small editor widgets shared by the Atmosphere tab: colour (swatch + hex) and slider (track bar + number).</summary>
static class Ui
{
    public static readonly Color Accent = Color.FromArgb(230, 130, 30), AccentHover = Color.FromArgb(250, 160, 50);
    public static readonly Color Subtle = Color.FromArgb(105, 108, 118);
    public static readonly Font Title = new("Segoe UI Semibold", 13f), Section = new("Segoe UI Semibold", 10.5f), Body = new("Segoe UI", 9f), Small = new("Segoe UI", 8.25f);

    public static Color ToColor(uint rgb) => Color.FromArgb((int)((rgb >> 16) & 0xFF), (int)((rgb >> 8) & 0xFF), (int)(rgb & 0xFF));
    public static uint ToRgb(Color c) => (uint)(c.R << 16 | c.G << 8 | c.B);

    public static Label Header(string text) => new() { Text = text, Font = Section, AutoSize = true, Margin = new Padding(0, 14, 0, 4), UseMnemonic = false };
    public static Label Note(string text, int width = 560) => new() { Text = text, Font = Small, ForeColor = Subtle, AutoSize = true, MaximumSize = new Size(width, 0), Margin = new Padding(0, 0, 0, 4), UseMnemonic = false };
    public static Label Caption(string text, int width = 130) => new() { Text = text, Font = Body, AutoSize = false, Width = width, Height = 26, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 2, 6, 2), UseMnemonic = false };

    public static Button Primary(string text)
    {
        var b = new Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Accent, ForeColor = Color.FromArgb(25, 20, 15), Font = new Font("Segoe UI Semibold", 9f), Padding = new Padding(8, 2, 8, 2), Cursor = Cursors.Hand };
        b.FlatAppearance.BorderColor = Color.FromArgb(200, 110, 20); b.FlatAppearance.MouseOverBackColor = AccentHover;
        b.EnabledChanged += (_, _) => b.BackColor = b.Enabled ? Accent : Color.FromArgb(225, 225, 228);
        return b;
    }

    public static Button Plain(string text) => new() { Text = text, AutoSize = true, Padding = new Padding(4, 1, 4, 1), Cursor = Cursors.Hand };

    public static FlowLayoutPanel Row(params Control[] cs)
    {
        var r = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 1, 0, 1) };
        r.Controls.AddRange(cs);
        return r;
    }
}

/// <summary>Colour field: a swatch (opens the colour picker) and an RRGGBB box. The picker (<see cref="ColourPicker"/>)
/// reports every change at once (Changed), between PickStarted and PickEnded(ok); Cancel puts the first colour back.</summary>
sealed class ColourField : FlowLayoutPanel
{
    readonly Button _swatch = new() { Width = 46, Height = 24, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Margin = new Padding(0, 2, 6, 0) };
    readonly TextBox _hex = new() { Width = 70, Font = new Font("Consolas", 9.5f), CharacterCasing = CharacterCasing.Upper, MaxLength = 6, Margin = new Padding(0, 3, 0, 0) };
    uint _value;
    bool _quiet;
    public event Action? Changed;
    /// <summary>The picker opened / closed (true: OK). Changes in between are one pick (one undo step).</summary>
    public event Action? PickStarted;
    public event Action<bool>? PickEnded;
    public string Title = "Colour";

    public ColourField()
    {
        AutoSize = true; WrapContents = false; Margin = new Padding(0);
        _swatch.FlatAppearance.BorderColor = Color.FromArgb(120, 120, 128);
        Controls.Add(_swatch); Controls.Add(_hex);
        _swatch.Click += (_, _) => Pick();
        _hex.Leave += (_, _) => CommitHex();
        _hex.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { CommitHex(); e.SuppressKeyPress = true; } };
        new ToolTip().SetToolTip(_swatch, "Click to pick a colour");
        new ToolTip().SetToolTip(_hex, "Colour as RRGGBB (hex), e.g. CED8E6");
    }

    public uint Value
    {
        get => _value;
        set { _value = value & 0xFFFFFF; _quiet = true; _swatch.BackColor = Ui.ToColor(_value); _hex.Text = _value.ToString("X6"); _quiet = false; }
    }

    /// <summary>Sets the value as if the user typed it (scripts).</summary>
    public void SetByUser(uint v) { Value = v; Changed?.Invoke(); }

    void Pick()
    {
        bool ok = false;
        PickStarted?.Invoke();
        try
        {
            using var p = new ColourPicker(_value, Title + " colour");
            p.Picking += v => { if (v != _value) SetByUser(v); };
            ok = p.ShowAt(_swatch) == DialogResult.OK;
        }
        finally { PickEnded?.Invoke(ok); }
    }

    /// <summary>Scripts: a pick through the picker window (shown, not modal) going through <paramref name="steps"/>, closed with
    /// OK or Cancel; <paramref name="shot"/> saves an image of the picker after the last step.</summary>
    public void ScriptPick(IReadOnlyList<uint> steps, bool ok, string? shot, Action? afterEachStep = null)
    {
        PickStarted?.Invoke();
        try
        {
            using var p = new ColourPicker(_value, Title + " colour");
            p.Picking += v => { if (v != _value) SetByUser(v); };
            var at = _swatch.PointToScreen(new Point(0, _swatch.Height + 2));
            p.Location = at;
            p.Show(FindForm());
            foreach (var v in steps) { p.Set(v); Application.DoEvents(); afterEachStep?.Invoke(); }
            if (shot != null)
            {
                Application.DoEvents();
                using var b = new Bitmap(p.Width, p.Height); p.DrawToBitmap(b, new Rectangle(0, 0, p.Width, p.Height)); b.Save(shot);
            }
            p.DialogResult = ok ? DialogResult.OK : DialogResult.Cancel;
            p.Close();
        }
        finally { PickEnded?.Invoke(ok); }
    }

    void CommitHex()
    {
        if (_quiet) return;
        var t = _hex.Text.Trim().TrimStart('#');
        if (t.Length == 6 && uint.TryParse(t, NumberStyles.HexNumber, null, out uint v)) { if (v != _value) SetByUser(v); }
        else Value = _value;
    }
}

/// <summary>Number field with an optional track bar (same value, two ways to set it).</summary>
sealed class SliderField : FlowLayoutPanel
{
    readonly TrackBar _bar = new() { Width = 200, TickStyle = TickStyle.None, AutoSize = false, Height = 26, Margin = new Padding(0, 0, 6, 0) };
    readonly NumericUpDown _num = new() { Width = 84, Margin = new Padding(0, 2, 6, 0), TextAlign = HorizontalAlignment.Right };
    readonly Label _unit = new() { AutoSize = true, Font = Ui.Small, ForeColor = Ui.Subtle, Margin = new Padding(0, 7, 0, 0) };
    readonly double _scale;
    bool _quiet;
    public event Action? Changed;

    public SliderField(double min, double max, int decimals, string unit = "", bool slider = true, double? sliderMax = null)
    {
        AutoSize = true; WrapContents = false; Margin = new Padding(0);
        _scale = Math.Pow(10, decimals);
        _num.DecimalPlaces = decimals; _num.Minimum = (decimal)min; _num.Maximum = (decimal)max; _num.Increment = (decimal)Math.Pow(10, -decimals) * (decimals > 1 ? 5 : 1);
        _bar.Minimum = (int)(min * _scale); _bar.Maximum = (int)((sliderMax ?? max) * _scale); _bar.SmallChange = 1; _bar.LargeChange = Math.Max(1, (_bar.Maximum - _bar.Minimum) / 20);
        _unit.Text = unit;
        if (slider) Controls.Add(_bar);
        Controls.Add(_num); Controls.Add(_unit);
        _bar.Scroll += (_, _) => { if (_quiet) return; _quiet = true; _num.Value = Math.Clamp((decimal)(_bar.Value / _scale), _num.Minimum, _num.Maximum); _quiet = false; Changed?.Invoke(); };
        _num.ValueChanged += (_, _) => { if (_quiet) return; _quiet = true; SyncBar(); _quiet = false; Changed?.Invoke(); };
    }

    /// <summary>Width of the track bar (the Atmosphere tab fits it to the space it has).</summary>
    public int BarWidth { get => _bar.Width; set => _bar.Width = value; }
    public bool HasBar => Controls.Contains(_bar);

    void SyncBar() => _bar.Value = Math.Clamp((int)Math.Round((double)_num.Value * _scale), _bar.Minimum, _bar.Maximum);

    public float Value
    {
        get => (float)_num.Value;
        set { _quiet = true; _num.Value = Math.Clamp((decimal)Math.Round(value, _num.DecimalPlaces), _num.Minimum, _num.Maximum); SyncBar(); _quiet = false; }
    }

    /// <summary>The exact value to store: the shown number unless it only rounds the stored float (then the float is kept).</summary>
    public float Exact(float stored) => Math.Round(stored, _num.DecimalPlaces) == (double)_num.Value ? stored : (float)_num.Value;

    /// <summary>Sets the value as if the user typed it (scripts).</summary>
    public void SetByUser(float v) { _num.Value = Math.Clamp((decimal)v, _num.Minimum, _num.Maximum); }
}

/// <summary>
/// The sky seen from above, for the sun direction: the centre is straight overhead, the rim the horizon (elevation 0°,
/// the dashed rings are 30° and 60°). Drag the sun to move it; its shadow points the other way (dark line). The wedge shows
/// where the 3D view's camera looks. Top-down map of the world: +X right, +Z down (the light setup's azimuth 0 puts the sun
/// towards -Z: the direction towards the sun is (-cos e sin a, sin e, -cos e cos a)).
/// </summary>
sealed class SunDial : Control
{
    float _elev = 0.8f, _azim;
    bool _drag;
    public event Action? Changed;
    public event Action? DragEnded;
    /// <summary>Colour of the sun dot.</summary>
    public Color SunColour { get => _sunColour; set { _sunColour = value; Invalidate(); } }
    Color _sunColour = Color.FromArgb(255, 220, 120);
    /// <summary>Yaw of the 3D view's camera (radians, forward = (sin yaw, ·, cos yaw)), or null when there is no view.</summary>
    public float? CameraYaw { get => _camYaw; set { if (_camYaw != value) { _camYaw = value; Invalidate(); } } }
    float? _camYaw;
    public const float MinElevation = -10 * MathF.PI / 180;

    public SunDial()
    {
        DoubleBuffered = true; ResizeRedraw = true; Cursor = Cursors.Hand;
        Width = 150; Height = 150; Margin = new Padding(0, 4, 12, 4);
        new ToolTip().SetToolTip(this, "Drag the sun. Centre = overhead, rim = horizon. The dark line is the way shadows fall; the grey wedge is where the 3D view looks.");
    }

    /// <summary>Sun elevation / azimuth in radians (the light setup's values).</summary>
    public (float Elevation, float Azimuth) Value
    {
        get => (_elev, _azim);
        set { _elev = value.Elevation; _azim = value.Azimuth; Invalidate(); }
    }

    float R => Math.Max(10, Math.Min(Width, Height) / 2f - 14);
    PointF C => new(Width / 2f, Height / 2f);

    PointF SunPoint()
    {
        float r = R * (1 - Math.Clamp(_elev, MinElevation, MathF.PI / 2) / (MathF.PI / 2));
        return new PointF(C.X - MathF.Sin(_azim) * r, C.Y - MathF.Cos(_azim) * r);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Color.White);
        var c = C; float r = R;
        var disc = new RectangleF(c.X - r, c.Y - r, 2 * r, 2 * r);
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(disc);
            using var pg = new PathGradientBrush(path) { CenterColor = Color.FromArgb(120, 170, 225), SurroundColors = new[] { Color.FromArgb(215, 230, 245) } };
            g.FillPath(pg, path);
        }
        using (var ring = new Pen(Color.FromArgb(70, 40, 60, 90)) { DashStyle = DashStyle.Dot })
            foreach (float k in new[] { 2 / 3f, 1 / 3f }) g.DrawEllipse(ring, c.X - r * k, c.Y - r * k, 2 * r * k, 2 * r * k);
        using (var axis = new Pen(Color.FromArgb(45, 40, 60, 90)))
        { g.DrawLine(axis, c.X - r, c.Y, c.X + r, c.Y); g.DrawLine(axis, c.X, c.Y - r, c.X, c.Y + r); }
        using (var rim = new Pen(Color.FromArgb(150, 90, 100, 120), 1.2f)) g.DrawEllipse(rim, disc);
        // camera wedge
        if (_camYaw is float yaw)
        {
            float deg = 90 - yaw * 180 / MathF.PI;   // screen angle of (sin yaw, cos yaw) with +Z down
            using var wb = new SolidBrush(Color.FromArgb(55, 30, 30, 40));
            g.FillPie(wb, c.X - r * 0.9f, c.Y - r * 0.9f, r * 1.8f, r * 1.8f, deg - 27, 54);
        }
        // shadow direction and sun
        var sp = SunPoint();
        float dx = c.X - sp.X, dy = c.Y - sp.Y, len = MathF.Sqrt(dx * dx + dy * dy);
        if (len > 1)
        {
            float k = r * 0.55f * Math.Clamp(1 - _elev / (MathF.PI / 2), 0.15f, 1) / len;
            using var sh = new Pen(Color.FromArgb(170, 25, 25, 35), 4f) { EndCap = LineCap.Round, StartCap = LineCap.Round };
            g.DrawLine(sh, c.X, c.Y, c.X + dx * k, c.Y + dy * k);
        }
        using (var cb = new SolidBrush(Color.FromArgb(60, 60, 70))) g.FillEllipse(cb, c.X - 3, c.Y - 3, 6, 6);
        using (var glow = new SolidBrush(Color.FromArgb(90, _sunColour))) g.FillEllipse(glow, sp.X - 11, sp.Y - 11, 22, 22);
        using (var sun = new SolidBrush(_sunColour)) g.FillEllipse(sun, sp.X - 7, sp.Y - 7, 14, 14);
        using (var edge = new Pen(Color.FromArgb(200, 120, 70, 10), 1.5f)) g.DrawEllipse(edge, sp.X - 7, sp.Y - 7, 14, 14);
        // labels: world axes
        TextRenderer.DrawText(g, "−Z", Ui.Small, new Point((int)c.X - 8, (int)(c.Y - r) - 14), Ui.Subtle);
        TextRenderer.DrawText(g, "+X", Ui.Small, new Point((int)(c.X + r) + 1, (int)c.Y - 7), Ui.Subtle);
    }

    void SetFrom(Point p)
    {
        var c = C; float r = R;
        float px = (p.X - c.X) / r, pz = (p.Y - c.Y) / r;
        float d = MathF.Sqrt(px * px + pz * pz);
        float elev = Math.Clamp((1 - d) * MathF.PI / 2, MinElevation, MathF.PI / 2);
        float azim = d < 1e-4f ? _azim : MathF.Atan2(-px, -pz);
        if (elev == _elev && azim == _azim) return;
        _elev = elev; _azim = azim; Invalidate();
        Changed?.Invoke();
    }

    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _drag = true; Capture = true; SetFrom(e.Location); } base.OnMouseDown(e); }
    protected override void OnMouseMove(MouseEventArgs e) { if (_drag) SetFrom(e.Location); base.OnMouseMove(e); }
    protected override void OnMouseUp(MouseEventArgs e) { if (_drag) { _drag = false; Capture = false; DragEnded?.Invoke(); } base.OnMouseUp(e); }

    /// <summary>Simulates a drag to a point (scripts): same path as the mouse.</summary>
    public void DragTo(Point p) => SetFrom(p);
}

/// <summary>What the game makes of the ambient colour: its ambient is brighter on surfaces facing up and darker facing down
/// (colour × (1.107 + 0.519 N.y), read from the game's shader constants), shown as two swatches.</summary>
sealed class HemiSwatch : Control
{
    uint _amb;
    public HemiSwatch() { DoubleBuffered = true; Width = 230; Height = 26; Margin = new Padding(10, 2, 0, 0); }
    public uint Ambient { get => _amb; set { _amb = value; Invalidate(); } }

    static Color Scale(uint rgb, float k)
    {
        var c = Ui.ToColor(rgb);
        return Color.FromArgb(Math.Min(255, (int)(c.R * k)), Math.Min(255, (int)(c.G * k)), Math.Min(255, (int)(c.B * k)));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Color.White);
        int x = 0;
        foreach (var (label, k) in new[] { ("sky-facing", 1.626f), ("ground-facing", 0.588f) })
        {
            using (var b = new SolidBrush(Scale(_amb, k))) g.FillRectangle(b, x, 4, 18, 18);
            using (var p = new Pen(Color.FromArgb(110, 0, 0, 0))) g.DrawRectangle(p, x, 4, 18, 18);
            TextRenderer.DrawText(g, label, Ui.Small, new Point(x + 22, 6), Ui.Subtle);
            x += 22 + TextRenderer.MeasureText(label, Ui.Small).Width + 10;
        }
    }
}
