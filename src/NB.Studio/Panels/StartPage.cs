using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace NB.Studio.Panels;

/// <summary>
/// Start page shown until a workspace is open: the trolley logo (Assets/trolley.png, a dark silhouette) redrawn as a
/// glowing amber-to-gold silhouette on a dark gradient, the title, and the workspace actions — continue with the last
/// workspace, open, new — plus workspaces found next to the last one.
/// </summary>
public sealed class StartPage : Control
{
    public event Action? OpenRequested, NewRequested;
    public event Action<string>? WorkspaceRequested;
    public event Action<bool>? AutoOpenChanged;

    readonly Bitmap? _logo;
    Bitmap? _tinted; int _tintedSize;
    readonly FlowLayoutPanel _actions = new() { FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, WrapContents = false };
    readonly CheckBox _auto = new() { Text = "Open the last workspace automatically at startup", AutoSize = true, ForeColor = Color.FromArgb(170, 170, 180), BackColor = Color.Transparent };
    string _status = "";

    static readonly Color Bg1 = Color.FromArgb(24, 26, 32), Bg2 = Color.FromArgb(44, 40, 38), Amber = Color.FromArgb(255, 150, 40), Gold = Color.FromArgb(255, 214, 90);

    public StartPage(string? lastWorkspace, bool autoOpen)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Bg1; Dock = DockStyle.Fill;
        var p = Path.Combine(AppContext.BaseDirectory, "Assets", "trolley.png");
        if (File.Exists(p)) { using var b = new Bitmap(p); _logo = new Bitmap(b); }

        bool hasLast = lastWorkspace != null && File.Exists(Path.Combine(lastWorkspace, "workspace.json"));
        if (hasLast) _actions.Controls.Add(Btn($"Continue:  {Path.GetFileName(lastWorkspace!.TrimEnd('\\', '/'))}", true, () => WorkspaceRequested?.Invoke(lastWorkspace!)));
        _actions.Controls.Add(Btn("Open Workspace…", !hasLast, () => OpenRequested?.Invoke()));
        _actions.Controls.Add(Btn("New Workspace from Game Directory…", false, () => NewRequested?.Invoke()));
        // other workspaces beside the last one (same parent folder)
        if (hasLast)
        {
            var others = SafeDirs(Path.GetDirectoryName(lastWorkspace!.TrimEnd('\\', '/'))!)
                .Where(d => File.Exists(Path.Combine(d, "workspace.json")) && !string.Equals(Path.GetFullPath(d).TrimEnd('\\'), Path.GetFullPath(lastWorkspace!).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => File.GetLastWriteTime(Path.Combine(d, "workspace.json"))).Take(8).ToList();
            if (others.Count > 0)
            {
                _actions.Controls.Add(new Label { Text = "Other workspaces", AutoSize = true, ForeColor = Color.FromArgb(150, 150, 160), Font = new Font("Segoe UI", 9f), Margin = new Padding(3, 14, 3, 2), BackColor = Color.Transparent });
                foreach (var d in others)
                {
                    var l = new LinkLabel { Text = Path.GetFileName(d), AutoSize = true, LinkColor = Gold, ActiveLinkColor = Color.White, VisitedLinkColor = Gold, Font = new Font("Segoe UI", 10f), BackColor = Color.Transparent, Margin = new Padding(6, 2, 3, 2) };
                    l.LinkClicked += (_, _) => WorkspaceRequested?.Invoke(d);
                    new ToolTip().SetToolTip(l, d);
                    _actions.Controls.Add(l);
                }
            }
        }
        _auto.Checked = autoOpen; _auto.Margin = new Padding(3, 16, 3, 3);
        _auto.CheckedChanged += (_, _) => AutoOpenChanged?.Invoke(_auto.Checked);
        _actions.Controls.Add(_auto);
        Controls.Add(_actions);
        Resize += (_, _) => Layout2();
        Layout2();
    }

    static IEnumerable<string> SafeDirs(string dir) { try { return Directory.GetDirectories(dir); } catch (Exception) { return Array.Empty<string>(); } }

    Button Btn(string text, bool primary, Action a)
    {
        var b = new Button
        {
            Text = text, AutoSize = false, Width = 330, Height = 40, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 10.5f, primary ? FontStyle.Bold : FontStyle.Regular), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 0, 0),
            BackColor = primary ? Color.FromArgb(230, 130, 30) : Color.FromArgb(52, 54, 62), ForeColor = primary ? Color.FromArgb(25, 20, 15) : Color.FromArgb(235, 235, 240), Margin = new Padding(3, 4, 3, 4),
        };
        b.FlatAppearance.BorderColor = primary ? Gold : Color.FromArgb(80, 82, 92);
        b.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(250, 160, 50) : Color.FromArgb(70, 72, 84);
        b.Click += (_, _) => a();
        return b;
    }

    /// <summary>Text under the buttons while a workspace opens.</summary>
    public void SetStatus(string s) { _status = s; Invalidate(); }

    (Rectangle Logo, Point Title) Geometry()
    {
        int h = ClientSize.Height, w = ClientSize.Width;
        int size = Math.Clamp(Math.Min(h - 120, w / 2 - 80), 120, 420);
        int x = Math.Max(30, w / 2 - size - 40), y = Math.Max(40, (h - size) / 2 - 20);
        return (new Rectangle(x, y, size, size), new Point(x + size + 60, y + 10));
    }

    void Layout2()
    {
        var (logo, title) = Geometry();
        _actions.Location = new Point(title.X, title.Y + 120);
        Invalidate();
    }

    /// <summary>The silhouette recoloured: vertical amber→gold gradient through its alpha, with a thin light rim.</summary>
    Bitmap Tinted(int size)
    {
        if (_tinted != null && _tintedSize == size) return _tinted;
        _tinted?.Dispose();
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(_logo!, 0, 0, size, size);
        }
        var bd = bmp.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        var px = new byte[size * bd.Stride];
        System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, px, 0, px.Length);
        var alpha = new byte[size * size];
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) alpha[y * size + x] = px[y * bd.Stride + x * 4 + 3];
        for (int y = 0; y < size; y++)
        {
            float t = y / (float)size;
            for (int x = 0; x < size; x++)
            {
                int o = y * bd.Stride + x * 4; byte a = alpha[y * size + x];
                // rim: opaque pixel next to a transparent one (upper-left light)
                bool rim = a > 128 && (x < 2 || y < 2 || alpha[(y - 2) * size + x] < 64 || alpha[y * size + x - 2] < 64);
                float r = Gold.R + (Amber.R - Gold.R) * t, gg = Gold.G + (Amber.G - Gold.G) * t, b = Gold.B + (Amber.B - Gold.B) * t;
                if (rim) { r = 255; gg = 245; b = 210; }
                px[o] = (byte)b; px[o + 1] = (byte)gg; px[o + 2] = (byte)r; px[o + 3] = a;
            }
        }
        System.Runtime.InteropServices.Marshal.Copy(px, 0, bd.Scan0, px.Length);
        bmp.UnlockBits(bd);
        _tinted = bmp; _tintedSize = size;
        return bmp;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using (var bg = new LinearGradientBrush(ClientRectangle, Bg1, Bg2, 35f)) g.FillRectangle(bg, ClientRectangle);
        var (logo, title) = Geometry();
        // soft glow behind the trolley
        var glow = Rectangle.Inflate(logo, logo.Width / 3, logo.Height / 3);
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(glow);
            using var pg = new PathGradientBrush(path) { CenterColor = Color.FromArgb(90, Amber), SurroundColors = new[] { Color.FromArgb(0, Amber) } };
            g.FillEllipse(pg, glow);
        }
        // ground line and shadow
        using (var sh = new SolidBrush(Color.FromArgb(70, 0, 0, 0))) g.FillEllipse(sh, logo.X + logo.Width / 8, logo.Bottom - logo.Height / 14, logo.Width * 3 / 4, logo.Height / 10);
        if (_logo != null) g.DrawImage(Tinted(logo.Width), logo);
        // title
        using var f1 = new Font("Segoe UI Black", 40f, FontStyle.Bold);
        using var f2 = new Font("Segoe UI", 13f);
        using (var tb = new LinearGradientBrush(new Rectangle(title.X, title.Y, 400, 70), Gold, Amber, 90f)) g.DrawString("NB Studio", f1, tb, title);
        using (var sb = new SolidBrush(Color.FromArgb(185, 185, 195))) g.DrawString("Banjo-Kazooie: Nuts & Bolts mod tool", f2, sb, title.X + 6, title.Y + 76);
        if (_status.Length > 0)
            using (var st = new SolidBrush(Gold)) g.DrawString(_status, f2, st, _actions.Left + 4, _actions.Bottom + 12);
        using var f3 = new Font("Segoe UI", 8.5f);
        using (var ft = new SolidBrush(Color.FromArgb(110, 110, 120))) g.DrawString("File menu: workspaces, patches and exports  ·  Worlds tab: double-click a world to open it in the 3D view", f3, ft, 16, ClientSize.Height - 26);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _logo?.Dispose(); _tinted?.Dispose(); }
        base.Dispose(disposing);
    }
}
