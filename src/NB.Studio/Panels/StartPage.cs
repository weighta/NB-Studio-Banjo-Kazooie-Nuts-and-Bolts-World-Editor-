using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace NB.Studio.Panels;

/// <summary>
/// Start page shown until a workspace is open: a random in-game photo (Assets/Backgrounds, shots of mods made with the
/// tool) behind a dark gradient, the NB Studio title, the workspace actions as cards (continue with the last workspace,
/// open, new), other workspaces next to the last one, and a link to the beginner's tour. Everything is drawn by this
/// control (no child controls), so nothing flickers or goes missing when the window is minimized and restored.
/// </summary>
public sealed class StartPage : Control
{
    public event Action? OpenRequested, NewRequested, TourRequested;
    public event Action<string>? WorkspaceRequested;
    public event Action<bool>? AutoOpenChanged;

    sealed record Hit(Rectangle Box, Action Click, string Kind, int Index);

    readonly string? _last;
    readonly List<string> _others = new();
    readonly List<string> _backgrounds;
    readonly Bitmap? _nut;
    Image? _bg; Bitmap? _bgScaled; Size _bgScaledFor;
    int _bgIndex = -1;
    bool _autoOpen;
    string _status = "";
    readonly List<Hit> _hits = new();
    int _hover = -1;
    static readonly Random Rng = new();

    static readonly Color Accent = Color.FromArgb(242, 140, 40), AccentHi = Color.FromArgb(255, 170, 70), Ink = Color.FromArgb(22, 18, 14),
        Text1 = Color.FromArgb(244, 244, 248), Text2 = Color.FromArgb(190, 194, 206), Text3 = Color.FromArgb(140, 146, 160),
        Card = Color.FromArgb(200, 22, 24, 32), CardHi = Color.FromArgb(225, 40, 44, 58), Edge = Color.FromArgb(60, 255, 255, 255);

    public StartPage(string? lastWorkspace, bool autoOpen)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(16, 17, 22); Dock = DockStyle.Fill;
        _autoOpen = autoOpen;
        _last = lastWorkspace != null && File.Exists(Path.Combine(lastWorkspace, "workspace.json")) ? lastWorkspace : null;
        if (_last != null)
            _others = SafeDirs(Path.GetDirectoryName(_last.TrimEnd('\\', '/'))!)
                .Where(d => File.Exists(Path.Combine(d, "workspace.json")) && !string.Equals(Path.GetFullPath(d).TrimEnd('\\'), Path.GetFullPath(_last).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => File.GetLastWriteTime(Path.Combine(d, "workspace.json"))).Take(6).ToList();
        var dir = Path.Combine(AppContext.BaseDirectory, "Assets", "Backgrounds");
        _backgrounds = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.jpg").OrderBy(f => f).ToList() : new();
        var nut = Path.Combine(AppContext.BaseDirectory, "Assets", "nut_gray.png");
        if (File.Exists(nut)) { using var b = new Bitmap(nut); _nut = new Bitmap(b, 56, 56); }
        NextBackground();
        Cursor = Cursors.Default;
    }

    static IEnumerable<string> SafeDirs(string dir) { try { return Directory.GetDirectories(dir); } catch (Exception) { return Array.Empty<string>(); } }

    /// <summary>A different random background photo.</summary>
    public void NextBackground()
    {
        if (_backgrounds.Count == 0) return;
        int i = _backgrounds.Count == 1 ? 0 : Rng.Next(_backgrounds.Count - (_bgIndex >= 0 ? 1 : 0));
        if (_bgIndex >= 0 && i >= _bgIndex) i++;
        _bgIndex = i;
        try
        {
            using var fs = File.OpenRead(_backgrounds[i]);
            var img = Image.FromStream(new MemoryStream(ReadAll(fs)));
            _bg?.Dispose(); _bg = img;
            _bgScaled?.Dispose(); _bgScaled = null;
        }
        catch (Exception) { }
        Invalidate();
    }

    /// <summary>Shows a given background (file name in Assets/Backgrounds; scripted screenshots).</summary>
    public void SetBackground(string fileName)
    {
        int i = _backgrounds.FindIndex(f => Path.GetFileName(f).Equals(fileName, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return;
        _bgIndex = i;
        try { var img = Image.FromStream(new MemoryStream(File.ReadAllBytes(_backgrounds[i]))); _bg?.Dispose(); _bg = img; _bgScaled?.Dispose(); _bgScaled = null; }
        catch (Exception) { }
        Invalidate();
    }

    static byte[] ReadAll(Stream s) { var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray(); }

    /// <summary>Text under the cards while a workspace opens.</summary>
    public void SetStatus(string s) { _status = s; Invalidate(); }
    public void SetAutoOpen(bool on) { _autoOpen = on; Invalidate(); }

    /// <summary>The background scaled to cover the page (cached per size: scaling a photo on every paint is slow).</summary>
    Bitmap? Background(Size size)
    {
        if (_bg == null || size.Width <= 0 || size.Height <= 0) return null;
        if (_bgScaled != null && _bgScaledFor == size) return _bgScaled;
        _bgScaled?.Dispose();
        var bmp = new Bitmap(size.Width, size.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            float s = Math.Max((float)size.Width / _bg.Width, (float)size.Height / _bg.Height);
            float w = _bg.Width * s, h = _bg.Height * s;
            g.DrawImage(_bg, (size.Width - w) / 2, (size.Height - h) / 2, w, h);
        }
        _bgScaled = bmp; _bgScaledFor = size;
        return bmp;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // minimized (0 x 0): nothing to draw. GDI+ brushes throw on empty rectangles, and an exception here would leave
        // WinForms' red cross on the page and an error dialog.
        if (ClientSize.Width < 2 || ClientSize.Height < 2) return;
        try { Draw(e.Graphics); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("StartPage paint: " + ex.Message); }
    }

    void Draw(Graphics g)
    {
        int W = ClientSize.Width, H = ClientSize.Height;
        g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(BackColor);
        if (Background(ClientSize) is { } bg) g.DrawImageUnscaled(bg, 0, 0);
        // readability: dark from the left (where the text is) and from the bottom
        using (var lg = new LinearGradientBrush(new Rectangle(0, 0, W, H), Color.FromArgb(238, 10, 11, 16), Color.FromArgb(40, 10, 11, 16), 0f))
        {
            lg.InterpolationColors = new ColorBlend
            {
                Colors = new[] { Color.FromArgb(240, 10, 11, 16), Color.FromArgb(215, 10, 11, 16), Color.FromArgb(90, 10, 11, 16), Color.FromArgb(20, 10, 11, 16) },
                Positions = new[] { 0f, 0.32f, 0.62f, 1f },
            };
            g.FillRectangle(lg, 0, 0, W, H);
        }
        using (var bg2 = new LinearGradientBrush(new Rectangle(0, H - 160, W, 160), Color.FromArgb(0, 0, 0, 0), Color.FromArgb(200, 6, 7, 10), 90f))
            g.FillRectangle(bg2, 0, H - 159, W, 159);

        _hits.Clear();
        int x = Math.Max(40, Math.Min(96, W / 14)), y = Math.Max(36, Math.Min(110, (H - 640) / 2));
        // title block
        if (_nut != null) g.DrawImage(_nut, x, y + 6, 52, 52);
        using var fTitle = new Font("Segoe UI Semibold", 34f);
        using var fKicker = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        using (var b = new SolidBrush(Accent)) g.DrawString("BANJO-KAZOOIE: NUTS & BOLTS  ·  MODDING SDK", fKicker, b, x + 66, y + 4);
        using (var b = new SolidBrush(Text1)) g.DrawString("NB Studio", fTitle, b, x + 60, y + 18);
        using var fTag = new Font("Segoe UI", 12f);
        using (var b = new SolidBrush(Text2))
            g.DrawString("Build worlds, vehicles, textures, sounds and co-op mods,\nthen test them in Xenia and share them as one small patch.", fTag, b, new RectangleF(x + 2, y + 92, 560, 60));
        y += 168;

        // action cards
        int cw = Math.Min(500, W - x - 40), ch = 66;
        void CardAt(string glyph, string title, string sub, bool primary, Action a)
        {
            var r = new Rectangle(x, y, cw, ch);
            int idx = _hits.Count; bool hot = _hover == idx;
            _hits.Add(new Hit(r, a, "card", idx));
            using (var path = Rounded(r, 10))
            {
                using var fill = new SolidBrush(primary ? (hot ? AccentHi : Accent) : (hot ? CardHi : Card));
                g.FillPath(fill, path);
                using var pen = new Pen(primary ? Color.FromArgb(120, 255, 230, 180) : (hot ? Color.FromArgb(120, Accent) : Edge), 1f);
                g.DrawPath(pen, path);
            }
            var fg = primary ? Ink : Text1; var fg2 = primary ? Color.FromArgb(70, 40, 10) : Text3;
            using (var fIcon = new Font("Segoe MDL2 Assets", 18f))
            using (var b = new SolidBrush(primary ? Ink : Accent)) g.DrawString(glyph, fIcon, b, r.X + 18, r.Y + 18);
            using (var fT = new Font("Segoe UI Semibold", 12.5f))
            using (var b = new SolidBrush(fg)) g.DrawString(title, fT, b, new RectangleF(r.X + 62, r.Y + 10, r.Width - 74, 24), new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
            using (var fS = new Font("Segoe UI", 9.5f))
            using (var b = new SolidBrush(fg2)) g.DrawString(sub, fS, b, new RectangleF(r.X + 63, r.Y + 36, r.Width - 74, 20), new StringFormat { Trimming = primary && _last != null ? StringTrimming.EllipsisPath : StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
            y += ch + 12;
        }
        if (_last != null)
            CardAt("", "Continue: " + Path.GetFileName(_last.TrimEnd('\\', '/')), "Pick up where you left off  ·  " + _last, true, () => WorkspaceRequested?.Invoke(_last));
        CardAt("", "Open Workspace…", "Open a workspace folder you made before", _last == null, () => OpenRequested?.Invoke());
        CardAt("", "New Workspace from Game Directory…", "A safe copy of your game to mod (the game itself is never changed)", false, () => NewRequested?.Invoke());

        // status while opening
        using var fSmall = new Font("Segoe UI", 9.5f);
        if (_status.Length > 0)
        {
            using var b = new SolidBrush(AccentHi); using var fSt = new Font("Segoe UI Semibold", 11f);
            g.DrawString(_status, fSt, b, x + 2, y + 2); y += 30;
        }

        // other workspaces
        if (_others.Count > 0)
        {
            y += 6;
            using (var b = new SolidBrush(Text3)) g.DrawString("OTHER WORKSPACES", fKicker, b, x + 2, y); y += 22;
            int cx = x;
            foreach (var d in _others)
            {
                var name = Path.GetFileName(d);
                var sz = g.MeasureString(name, fSmall);
                var r = new Rectangle(cx, y, (int)sz.Width + 22, 28);
                if (r.Right > x + cw) { cx = x; y += 34; r.X = cx; }
                int idx = _hits.Count; bool hot = _hover == idx;
                _hits.Add(new Hit(r, () => WorkspaceRequested?.Invoke(d), "chip", idx));
                using (var path = Rounded(r, 14))
                {
                    using var fill = new SolidBrush(hot ? CardHi : Color.FromArgb(150, 22, 24, 32)); g.FillPath(fill, path);
                    using var pen = new Pen(hot ? Accent : Edge); g.DrawPath(pen, path);
                }
                using (var b = new SolidBrush(hot ? Text1 : Text2)) g.DrawString(name, fSmall, b, r.X + 11, r.Y + 5);
                cx = r.Right + 8;
            }
            y += 40;
        }

        // auto-open switch
        {
            var r = new Rectangle(x, y + 4, 38, 20);
            int idx = _hits.Count;
            var whole = new Rectangle(x, y, 360, 28);
            _hits.Add(new Hit(whole, () => { _autoOpen = !_autoOpen; AutoOpenChanged?.Invoke(_autoOpen); Invalidate(); }, "switch", idx));
            using (var path = Rounded(r, 10)) using (var b = new SolidBrush(_autoOpen ? Accent : Color.FromArgb(90, 255, 255, 255))) g.FillPath(b, path);
            using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, _autoOpen ? r.Right - 18 : r.X + 2, r.Y + 2, 16, 16);
            using (var b = new SolidBrush(_hover == idx ? Text1 : Text2)) g.DrawString("Open the last workspace automatically at startup", fSmall, b, r.Right + 10, r.Y + 1);
            y += 40;
        }

        // tour link
        {
            using var fLink = new Font("Segoe UI Semibold", 10.5f, FontStyle.Underline);
            var text = "New to modding? Take the 2-minute tour";
            var sz = g.MeasureString(text, fLink);
            var r = new Rectangle(x, y, (int)sz.Width + 30, 26);
            int idx = _hits.Count;
            _hits.Add(new Hit(r, () => TourRequested?.Invoke(), "link", idx));
            using (var fIcon = new Font("Segoe MDL2 Assets", 12f)) using (var b = new SolidBrush(Accent)) g.DrawString("", fIcon, b, x, y + 3);
            using (var b = new SolidBrush(_hover == idx ? AccentHi : Accent)) g.DrawString(text, fLink, b, x + 24, y);
        }

        // footer: what the tool covers, and a new background
        using (var b = new SolidBrush(Text3))
            g.DrawString("Worlds  ·  Models  ·  Textures  ·  Collision  ·  Markers & AI paths  ·  Vehicle parts  ·  Text, audio, video  ·  Game-code mods  ·  Patches  ·  Co-op",
                fSmall, b, x + 2, H - 34);
        {
            var text = "";
            var r = new Rectangle(W - 54, H - 50, 36, 36);
            int idx = _hits.Count;
            _hits.Add(new Hit(r, NextBackground, "shuffle", idx));
            using (var path = Rounded(r, 18)) using (var fill = new SolidBrush(_hover == idx ? CardHi : Card)) g.FillPath(fill, path);
            using (var fIcon = new Font("Segoe MDL2 Assets", 12f)) using (var b = new SolidBrush(Text1)) g.DrawString(text, fIcon, b, r.X + 10, r.Y + 10);
            if (_hover == idx) using (var b2 = new SolidBrush(Text2)) { var t = "New background (photos taken in the game)"; var s2 = g.MeasureString(t, fSmall); g.DrawString(t, fSmall, b2, r.X - s2.Width - 8, r.Y + 9); }
        }
    }

    static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var p = new GraphicsPath(); int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = _hits.FindIndex(t => t.Box.Contains(e.Location));
        if (h != _hover) { _hover = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (_hover != -1) { _hover = -1; Invalidate(); } }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        var h = _hits.FirstOrDefault(t => t.Box.Contains(e.Location));
        h?.Click();
    }

    /// <summary>Where a card is on screen (for the tour's spotlight): 0 = first card.</summary>
    public Rectangle CardBounds(int i) => _hits.Where(h => h.Kind == "card").Skip(i).Select(h => h.Box).FirstOrDefault();

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _bg?.Dispose(); _bgScaled?.Dispose(); _nut?.Dispose(); }
        base.Dispose(disposing);
    }
}
