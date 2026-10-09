using System.Numerics;
using NB.Core.World;

namespace NB.Studio.Viewport;

/// <summary>
/// Music in the 3D view (Show menu "Music"): Showdown Town's music regions as rings (the game tests the distance in X/Z
/// only, so each region is an endless upright cylinder; the rings mark its edge at the marker's height) in the region's
/// colour, and a speaker label "♪ Music: Seaside" at each region's centre and at the world's own music speaker (the
/// music that plays everywhere else). Labels follow the camera-label rule: the full label near the camera, hovered or
/// selected, a small speaker glyph farther away.
/// </summary>
public sealed partial class SceneViewport
{
    public bool ShowMusic { get => _showMusic; set { if (_showMusic != value) { _showMusic = value; _gl.Invalidate(); } } }
    bool _showMusic = true;

    /// <summary>The label of a music object ("Music: Park" …), supplied by the main form (it knows the level scripts).</summary>
    public Func<SceneObject, string?>? MusicLabel;

    public static bool IsMusic(SceneObject? o) => o != null && (o.IsMusicRegion || o.IsWorldMusic);

    /// <summary>One colour per region number (1 Market … 6 L.O.G.); the world speaker uses the first.</summary>
    static readonly Vector3[] RegionColours =
    {
        new(0.95f, 0.75f, 0.20f), new(0.30f, 0.60f, 1.00f), new(0.35f, 0.85f, 0.35f), new(0.85f, 0.45f, 1.00f),
        new(0.20f, 0.85f, 0.85f), new(1.00f, 0.45f, 0.35f),
    };
    public static Vector3 RegionColour(int region) => RegionColours[Math.Clamp(region - 1, 0, RegionColours.Length - 1)];
    static Color RegionColor(int region) { var c = RegionColour(region); return Color.FromArgb(255, (int)(c.X * 200), (int)(c.Y * 200), (int)(c.Z * 200)); }

    void DrawMusicFigures(Matrix4x4 vp)
    {
        if (!_showMusic || Scene == null) return;
        var back = new List<(Vector3, Vector3, Vector3)>(); var top = new List<(Vector3, Vector3, Vector3)>();
        foreach (var o in Scene.Objects)
        {
            if (!o.Visible || !o.IsMusicRegion) continue;
            bool sel = IsSelected(o);
            var c = o.Transform.Translation; float r = MathF.Max(0.5f, o.MusicRadius);
            var col = RegionColour(o.MusicRegionId) * (sel ? 1.15f : 0.8f);
            var l = sel ? top : back;
            const int N = 72; float h = sel ? 18 : 6;
            for (int k = 0; k < N; k++)
            {
                float a0 = k * MathF.Tau / N, a1 = (k + 1) * MathF.Tau / N;
                var p0 = c + new Vector3(MathF.Cos(a0) * r, 0, MathF.Sin(a0) * r); var p1 = c + new Vector3(MathF.Cos(a1) * r, 0, MathF.Sin(a1) * r);
                l.Add((p0, p1, col)); l.Add((p0 + Vector3.UnitY * h, p1 + Vector3.UnitY * h, col));
                if (k % 6 == 0) l.Add((p0, p0 + Vector3.UnitY * h, col));
            }
            if (sel) { l.Add((c, c + Vector3.UnitX * r, col)); l.Add((c, c + Vector3.UnitZ * r, col)); }
        }
        if (back.Count > 0) _r.Lines(back, vp, false, 2f);
        if (top.Count > 0) _r.Lines(top, vp, true, 2.5f);
    }

    readonly Dictionary<string, Renderer.Overlay> _musicLabels = new();
    readonly Dictionary<int, Renderer.Overlay> _musicIcons = new();
    const float MusicLabelDistance = 220, MusicIconDistance = 1500;
    /// <summary>The world-music speaker's label (or icon) as last drawn: its click area. The speaker has no body in the
    /// 3D view, so nothing invisible covers the player start under it.</summary>
    (SceneObject Obj, Rectangle Rect)? _worldMusicHit;

    /// <summary>The world-music speaker when <paramref name="p"/> is on its drawn label, else null.</summary>
    SceneObject? WorldMusicAt(Point p) =>
        _showMusic && _worldMusicHit is { } h && h.Obj.Visible && h.Rect.Contains(p) && Scene != null && Scene.Objects.Contains(h.Obj) ? h.Obj : null;

    /// <summary>Script check: where the speaker's label was drawn, and a world point on the screen.</summary>
    public Rectangle? WorldMusicLabelRect => _worldMusicHit?.Rect;
    public Vector2? ScreenOf(Vector3 p) => ToScreen(p);
    /// <summary>Script check: a left click at <paramref name="p"/> (mouse down and up, as --click); what is selected then.</summary>
    public string ScriptClick(Point p)
    {
        OnMouseDown(null, new MouseEventArgs(MouseButtons.Left, 1, p.X, p.Y, 0));
        OnMouseUp(null, new MouseEventArgs(MouseButtons.Left, 1, p.X, p.Y, 0));
        return "selected " + (Selected?.Name ?? "-");
    }

    void DrawMusicLabels(int W, int H)
    {
        _worldMusicHit = null;
        if (!_showMusic || Scene == null) return;
        foreach (var o in Scene.Objects)
        {
            if (!o.Visible || !IsMusic(o)) continue;
            int region = o.IsMusicRegion ? o.MusicRegionId : 1;
            var at = o.Transform.Translation + Vector3.UnitY * 2.5f;
            float dist = Vector3.Distance(at, _camPos);
            if (Vector3.Dot(at - _camPos, Forward()) < 0.5f || dist > MusicIconDistance) continue;
            if (ToScreen(at) is not { } sp) continue;
            bool focus = IsSelected(o) || o == _tipFor;
            Renderer.Overlay ov;
            if (focus || dist < MusicLabelDistance)
            {
                string text = MusicLabel?.Invoke(o) ?? o.Name;
                string key = region + "|" + text;
                if (!_musicLabels.TryGetValue(key, out ov!))
                {
                    _musicLabels[key] = ov = new Renderer.Overlay();
                    using var bmp = DrawSpeakerLabel(text, RegionColor(region));
                    _r.UpdateOverlay(ov, bmp, key);
                }
            }
            else if (!_musicIcons.TryGetValue(region, out ov!))
            {
                _musicIcons[region] = ov = new Renderer.Overlay();
                using var bmp = DrawSpeakerIcon(RegionColor(region));
                _r.UpdateOverlay(ov, bmp, "speaker" + region);
            }
            int x2 = (int)sp.X - ov.W / 2, y2 = (int)sp.Y - ov.H / 2;
            if (x2 > W || y2 > H || x2 + ov.W < 0 || y2 + ov.H < 0) continue;
            _r.DrawOverlay(ov, x2, y2, W, H);
            if (o.IsWorldMusic) _worldMusicHit = (o, new Rectangle(x2, y2, ov.W, ov.H));
        }
    }

    /// <summary>Forgets cached music labels (a track was changed).</summary>
    public void InvalidateMusicLabels()
    {
        if (_ready) { _gl.MakeCurrent(); foreach (var ov in _musicLabels.Values) _r.DeleteOverlay(ov); }
        _musicLabels.Clear(); _gl.Invalidate();
    }

    static void DrawSpeaker(Graphics g, float x, float y, float s, Color c)
    {
        using var b = new SolidBrush(c); using var p = new Pen(c, MathF.Max(1f, s / 7));
        g.FillRectangle(b, x, y + s * 0.32f, s * 0.22f, s * 0.36f);
        g.FillPolygon(b, new[] { new PointF(x + s * 0.2f, y + s * 0.32f), new PointF(x + s * 0.48f, y + s * 0.1f), new PointF(x + s * 0.48f, y + s * 0.9f), new PointF(x + s * 0.2f, y + s * 0.68f) });
        g.DrawArc(p, x + s * 0.38f, y + s * 0.3f, s * 0.3f, s * 0.4f, -50, 100);
        g.DrawArc(p, x + s * 0.38f, y + s * 0.12f, s * 0.55f, s * 0.76f, -50, 100);
    }

    /// <summary>"🔊 Music: Seaside" on a light chip edged in the region's colour.</summary>
    static Bitmap DrawSpeakerLabel(string text, Color edge)
    {
        using var font = new Font("Segoe UI", 8f);
        using var probe = new Bitmap(1, 1); using var pg = Graphics.FromImage(probe);
        var sz = pg.MeasureString(text, font);
        int w = (int)Math.Ceiling(sz.Width) + 24, h = Math.Max(18, (int)Math.Ceiling(sz.Height) + 4);
        var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);
        using (var p = Rounded(new Rectangle(0, 0, w - 1, h - 1), 4)) using (var b = new SolidBrush(Color.FromArgb(225, 250, 249, 244))) using (var e = new Pen(edge, 1.5f)) { g.FillPath(b, p); g.DrawPath(e, p); }
        DrawSpeaker(g, 3, (h - 15) / 2f, 15, edge);
        using (var tb = new SolidBrush(Color.FromArgb(255, 32, 35, 42))) g.DrawString(text, font, tb, 20, (h - sz.Height) / 2);
        return bmp;
    }

    /// <summary>The far-away marker: a speaker on a small round chip.</summary>
    static Bitmap DrawSpeakerIcon(Color c)
    {
        var bmp = new Bitmap(18, 18, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using (var b = new SolidBrush(Color.FromArgb(215, 250, 249, 244))) g.FillEllipse(b, 0, 0, 17, 17);
        using (var e = new Pen(c, 1.2f)) g.DrawEllipse(e, 0.5f, 0.5f, 16, 16);
        DrawSpeaker(g, 2.5f, 2.5f, 12, c);
        return bmp;
    }
}
