using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;

namespace NB.Studio.Panels;

/// <summary>
/// A small colour picker in the app's style: saturation / brightness square, hue bar, R G B and hex, the old and the new
/// colour. Every change is reported at once through <see cref="Picking"/> (the Atmosphere tab updates the 3D preview
/// while you drag); OK keeps the colour, Cancel / Esc reports the original colour again.
/// </summary>
sealed class ColourPicker : Form
{
    readonly uint _original;
    float _h, _s, _v;
    uint _rgb;
    bool _quiet;
    readonly Panel _sq = new DoubleBufferedPanel { Location = new Point(12, 12), Size = new Size(220, 170), Cursor = Cursors.Cross };
    readonly Panel _hue = new DoubleBufferedPanel { Location = new Point(242, 12), Size = new Size(22, 170), Cursor = Cursors.Hand };
    readonly Panel _swatch = new DoubleBufferedPanel { Location = new Point(274, 12), Size = new Size(60, 60) };
    readonly NumericUpDown _r = Num(), _g = Num(), _b = Num();
    readonly TextBox _hex = new() { Width = 70, Font = new Font("Consolas", 9.5f), CharacterCasing = CharacterCasing.Upper, MaxLength = 7 };
    Bitmap? _sqBmp; float _sqHue = -1;
    /// <summary>The colour while it changes (0xRRGGBB).</summary>
    public event Action<uint>? Picking;
    public uint Value => _rgb;

    static NumericUpDown Num() => new() { Minimum = 0, Maximum = 255, Width = 50, TextAlign = HorizontalAlignment.Right };

    public ColourPicker(uint rgb, string title)
    {
        _original = _rgb = rgb & 0xFFFFFF;
        (_h, _s, _v) = ToHsv(_rgb);
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedToolWindow; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
        MaximizeBox = MinimizeBox = false; KeyPreview = true;
        BackColor = Color.White; Font = Ui.Body;
        ClientSize = new Size(344, 262);
        Controls.AddRange(new Control[] { _sq, _hue, _swatch });
        var lbl = new Label { Text = "now  before", Font = Ui.Small, ForeColor = Ui.Subtle, AutoSize = true, Location = new Point(273, 74) };
        Controls.Add(lbl);
        // R G B and hex
        int y = 194;
        foreach (var (l, n, x) in new[] { ("R", _r, 12), ("G", _g, 86), ("B", _b, 160) })
        {
            Controls.Add(new Label { Text = l, AutoSize = true, Location = new Point(x, y + 3), ForeColor = Ui.Subtle });
            n.Location = new Point(x + 16, y); Controls.Add(n);
            n.ValueChanged += (_, _) => { if (!_quiet) SetRgb((uint)((int)_r.Value << 16 | (int)_g.Value << 8 | (int)_b.Value), true); };
        }
        Controls.Add(new Label { Text = "#", AutoSize = true, Location = new Point(240, y + 3), ForeColor = Ui.Subtle });
        _hex.Location = new Point(254, y); Controls.Add(_hex);
        _hex.TextChanged += (_, _) =>
        {
            if (_quiet) return;
            var t = _hex.Text.Trim().TrimStart('#');
            if (t.Length == 6 && uint.TryParse(t, NumberStyles.HexNumber, null, out uint v)) SetRgb(v, true);
        };
        var ok = Ui.Primary("OK"); var cancel = Ui.Plain("Cancel");
        ok.Location = new Point(ClientSize.Width - 162, 226); cancel.Location = new Point(ClientSize.Width - 84, 226);
        ok.Width = 70; cancel.Width = 72; ok.AutoSize = cancel.AutoSize = false; ok.Height = cancel.Height = 26;
        Controls.Add(ok); Controls.Add(cancel);
        AcceptButton = ok; CancelButton = cancel;
        ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        new ToolTip().SetToolTip(_sq, "Drag: saturation (left to right) and brightness (bottom to top)");
        new ToolTip().SetToolTip(_hue, "Drag: hue");

        _sq.Paint += (_, e) => PaintSquare(e.Graphics);
        _hue.Paint += (_, e) => PaintHue(e.Graphics);
        _swatch.Paint += (_, e) =>
        {
            var g = e.Graphics; int w = _swatch.Width / 2;   // left: now, right: before
            using (var n = new SolidBrush(Ui.ToColor(_rgb))) g.FillRectangle(n, 0, 0, w, _swatch.Height);
            using (var o = new SolidBrush(Ui.ToColor(_original))) g.FillRectangle(o, w, 0, _swatch.Width - w, _swatch.Height);
            using var p = new Pen(Color.FromArgb(120, 120, 128)); g.DrawRectangle(p, 0, 0, _swatch.Width - 1, _swatch.Height - 1);
        };
        _swatch.Cursor = Cursors.Hand;
        _swatch.MouseDown += (_, e) => { if (e.X >= _swatch.Width / 2) SetRgb(_original, true); };   // click "before": back to it
        MouseHandlers(_sq, p => { _s = Math.Clamp(p.X / (float)(_sq.Width - 1), 0, 1); _v = 1 - Math.Clamp(p.Y / (float)(_sq.Height - 1), 0, 1); FromHsv(); });
        MouseHandlers(_hue, p => { _h = Math.Clamp(p.Y / (float)(_hue.Height - 1), 0, 1) * 360; FromHsv(); });
        SyncFields();
    }

    /// <summary>Sets the colour as the controls do (scripts).</summary>
    public void Set(uint rgb) => SetRgb(rgb, true);

    /// <summary>Shows the picker under <paramref name="anchor"/> (kept on its screen).</summary>
    public DialogResult ShowAt(Control anchor)
    {
        var at = anchor.PointToScreen(new Point(0, anchor.Height + 2));
        var wa = Screen.FromControl(anchor).WorkingArea;
        var size = SizeFromClientSize(ClientSize);
        Location = new Point(Math.Clamp(at.X, wa.Left, wa.Right - size.Width), at.Y + size.Height > wa.Bottom ? Math.Max(wa.Top, at.Y - anchor.Height - 4 - size.Height) : at.Y);
        return ShowDialog(anchor.FindForm());
    }

    void MouseHandlers(Control c, Action<Point> set)
    {
        bool down = false;
        c.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { down = true; c.Capture = true; set(e.Location); } };
        c.MouseMove += (_, e) => { if (down) set(e.Location); };
        c.MouseUp += (_, _) => { down = false; c.Capture = false; };
    }

    void FromHsv() => SetRgb(ToRgb(_h, _s, _v), false);

    void SetRgb(uint rgb, bool updateHsv)
    {
        rgb &= 0xFFFFFF;
        if (updateHsv) { var (h, s, v) = ToHsv(rgb); if (s > 0 && v > 0) _h = h; if (v > 0) _s = s; _v = v; }
        bool changed = rgb != _rgb;
        _rgb = rgb;
        SyncFields();
        _sq.Invalidate(); _hue.Invalidate(); _swatch.Invalidate();
        if (changed) Picking?.Invoke(_rgb);
    }

    void SyncFields()
    {
        _quiet = true;
        _r.Value = _rgb >> 16 & 255; _g.Value = _rgb >> 8 & 255; _b.Value = _rgb & 255;
        if (!_hex.Focused) _hex.Text = _rgb.ToString("X6");
        _quiet = false;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (DialogResult != DialogResult.OK && _rgb != _original) { _rgb = _original; Picking?.Invoke(_original); }   // Cancel / Esc / closed: back
        base.OnFormClosed(e);
    }

    // ------------------------------------------------------------------ drawing

    void PaintSquare(Graphics g)
    {
        int w = _sq.Width, h = _sq.Height;
        if (_sqBmp == null || _sqHue != _h)
        {
            _sqBmp?.Dispose();
            _sqBmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
            var bd = _sqBmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            var row = new int[w];
            for (int y = 0; y < h; y++)
            {
                float v = 1 - y / (float)(h - 1);
                for (int x = 0; x < w; x++) row[x] = (int)ToRgb(_h, x / (float)(w - 1), v);
                System.Runtime.InteropServices.Marshal.Copy(row, 0, bd.Scan0 + y * bd.Stride, w);
            }
            _sqBmp.UnlockBits(bd);
            _sqHue = _h;
        }
        g.DrawImageUnscaled(_sqBmp, 0, 0);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float cx = _s * (w - 1), cy = (1 - _v) * (h - 1);
        using (var o = new Pen(Color.Black, 1.5f)) g.DrawEllipse(o, cx - 6, cy - 6, 12, 12);
        using (var i = new Pen(Color.White, 1.5f)) g.DrawEllipse(i, cx - 4.5f, cy - 4.5f, 9, 9);
    }

    void PaintHue(Graphics g)
    {
        int w = _hue.Width, h = _hue.Height;
        for (int y = 0; y < h; y++)
            using (var p = new Pen(Ui.ToColor(ToRgb(360f * y / (h - 1), 1, 1)))) g.DrawLine(p, 0, y, w, y);
        int cy = (int)(_h / 360 * (h - 1));
        using var o = new Pen(Color.Black, 2); g.DrawRectangle(o, 1, cy - 3, w - 3, 6);
        using var i = new Pen(Color.White, 1); g.DrawRectangle(i, 2, cy - 2, w - 5, 4);
    }

    // ------------------------------------------------------------------ HSV

    public static (float H, float S, float V) ToHsv(uint rgb)
    {
        float r = (rgb >> 16 & 255) / 255f, g = (rgb >> 8 & 255) / 255f, b = (rgb & 255) / 255f;
        float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        float h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    public static uint ToRgb(float h, float s, float v)
    {
        h = ((h % 360) + 360) % 360;
        float c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (float r, float g, float b) = h < 60 ? (c, x, 0f) : h < 120 ? (x, c, 0f) : h < 180 ? (0f, c, x) : h < 240 ? (0f, x, c) : h < 300 ? (x, 0f, c) : (c, 0f, x);
        static uint B(float f) => (uint)Math.Clamp((int)MathF.Round(f * 255), 0, 255);
        return B(r + m) << 16 | B(g + m) << 8 | B(b + m);
    }

    protected override void Dispose(bool disposing) { if (disposing) _sqBmp?.Dispose(); base.Dispose(disposing); }

    sealed class DoubleBufferedPanel : Panel { public DoubleBufferedPanel() { DoubleBuffered = true; ResizeRedraw = true; } }
}
