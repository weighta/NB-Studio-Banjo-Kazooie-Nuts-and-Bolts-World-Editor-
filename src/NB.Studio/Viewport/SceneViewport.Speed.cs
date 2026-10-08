namespace NB.Studio.Viewport;

/// <summary>
/// Camera speed: one factor (0.1× … 8×) for the fly keys and the wheel dolly, set with the "Speed" button on the view
/// bar or the mouse wheel while flying (right button held), shown for a moment in the middle of the view and remembered
/// between sessions (camera.json next to viewport.json). Shift = ×4, Ctrl (while flying with the right button) = ×0.25.
/// </summary>
public sealed partial class SceneViewport
{
    public static readonly float[] SpeedSteps = { 0.1f, 0.15f, 0.25f, 0.35f, 0.5f, 0.7f, 1f, 1.4f, 2f, 3f, 4f, 6f, 8f };
    float _flySpeed = LoadFlySpeed();
    DateTime _speedShownAt = DateTime.MinValue;
    readonly Renderer.Overlay _speedOv = new();
    readonly System.Windows.Forms.Timer _speedHide = new() { Interval = 1300 };
    readonly ContextMenuStrip _speedMenu = new() { ShowCheckMargin = true, ShowImageMargin = false };

    /// <summary>The camera speed factor (1 = the speed of earlier versions).</summary>
    public float FlySpeed
    {
        get => _flySpeed;
        set
        {
            float v = Math.Clamp(value, SpeedSteps[0], SpeedSteps[^1]);
            if (v == _flySpeed) return;
            _flySpeed = v; SaveFlySpeed(v);
            FlySpeedChanged?.Invoke(v);
            _gl.Invalidate();
        }
    }
    public event Action<float>? FlySpeedChanged;

    /// <summary>Speed of one step right now: the factor, Shift ×4, Ctrl ×0.25 (Ctrl only while right-dragging).</summary>
    float FlySpeedMultiplier()
    {
        float m = _flySpeed;
        if (_keys.Contains(Keys.ShiftKey)) m *= 4;
        if (_looking && _keys.Contains(Keys.ControlKey)) m *= 0.25f;
        return m;
    }

    /// <summary>True while the right button is held (the 3D view keeps Ctrl + fly keys for itself then).</summary>
    public bool MouseFlying => _looking;

    string SpeedLabel => _flySpeed >= 1 ? $"{_flySpeed:0.#}×" : $"{_flySpeed:0.##}×";

    /// <summary>Wheel while flying: one notch = the next speed step, shown in the middle of the view.</summary>
    void WheelSpeed(int delta)
    {
        int i = Array.FindIndex(SpeedSteps, s => s >= _flySpeed - 1e-4f);
        if (i < 0) i = SpeedSteps.Length - 1;
        int notches = Math.Sign(delta) * Math.Max(1, Math.Abs(delta) / 120);
        FlySpeed = SpeedSteps[Math.Clamp(i + notches, 0, SpeedSteps.Length - 1)];
        ShowSpeedReadout();
    }

    void ShowSpeedReadout()
    {
        _speedShownAt = DateTime.UtcNow;
        _speedHide.Stop(); _speedHide.Tick -= HideReadout; _speedHide.Tick += HideReadout; _speedHide.Start();
        _gl.Invalidate();
    }
    void HideReadout(object? s, EventArgs e) { _speedHide.Stop(); _gl.Invalidate(); }

    /// <summary>"Camera speed 2×" in the middle of the view for a moment after a change.</summary>
    void DrawSpeedReadout(int W, int H)
    {
        if ((DateTime.UtcNow - _speedShownAt).TotalMilliseconds > 1200) return;
        string k = "speed|" + SpeedLabel;
        if (_speedOv.Key != k) using (var bmp = DrawHud($"Camera speed {SpeedLabel}", "wheel while flying · Shift ×4 · Ctrl ×¼", Color.FromArgb(255, 242, 140, 40))) _r.UpdateOverlay(_speedOv, bmp, k);
        _r.DrawOverlay(_speedOv, (W - _speedOv.W) / 2, (H - _speedOv.H) / 2, W, H);
    }

    // ------------------------------------------------------------------ the bar button and its menu

    const int SpeedW = 76;
    bool _hoverSpeed => _hoverBar == VisHit && _mouse.X < VisRect.X + SpeedW;

    /// <summary>The "Speed" button (left part of the speed / show bar element).</summary>
    void DrawSpeedButton(Graphics g, Font font, Brush bg, Pen edge)
    {
        using var cp = Rounded(new Rectangle(0, 0, SpeedW - 1, BarH - 1), 4);
        g.FillPath(bg, cp); g.DrawPath(edge, cp);
        var r = new Rectangle(3, 3, SpeedW - 6, BarH - 7);
        if (_hoverSpeed || _speedMenu.Visible) { using var p2 = Rounded(r, 3); using var b2 = new SolidBrush(BarHover); g.FillPath(b2, p2); }
        // a speedometer
        using (var pen = new Pen(IconFg, 1.3f))
        {
            var e = new RectangleF(r.X + 5, r.Y + 3, 14, 14);
            g.DrawArc(pen, e, 160, 220);
            float a = (float)(Math.PI * (200 + 140 * Math.Clamp(Math.Log(_flySpeed / SpeedSteps[0]) / Math.Log(SpeedSteps[^1] / SpeedSteps[0]), 0, 1)) / 180);
            var c = new PointF(e.X + 7, e.Y + 7);
            g.DrawLine(pen, c, new PointF(c.X + 6 * MathF.Cos(a), c.Y + 6 * MathF.Sin(a)));
        }
        using var tb = new SolidBrush(BarText);
        g.DrawString(SpeedLabel + " ▾", font, tb, r.X + 23, r.Y + 2);
    }

    public void ShowSpeedMenu()
    {
        _speedMenu.Items.Clear();
        _speedMenu.Items.Add(new ToolStripLabel("Camera speed (fly keys, wheel)") { ForeColor = Color.FromArgb(105, 108, 118) });
        foreach (var s in SpeedSteps)
        {
            float v = s;
            var mi = new ToolStripMenuItem(v >= 1 ? $"{v:0.#}×" : $"{v:0.##}×") { Checked = Math.Abs(v - _flySpeed) < 1e-4f };
            mi.Click += (_, _) => { FlySpeed = v; ShowSpeedReadout(); };
            _speedMenu.Items.Add(mi);
        }
        _speedMenu.Items.Add(new ToolStripSeparator());
        _speedMenu.Items.Add(new ToolStripLabel("Wheel while right-dragging changes it;\nShift ×4, Ctrl ×¼ while flying") { ForeColor = Color.FromArgb(105, 108, 118) });
        _speedMenu.Show(_gl, new Point(VisRect.X, VisRect.Bottom + 2));
    }

    // ------------------------------------------------------------------ remembered between sessions

    static string CameraSettingsPath => Path.Combine(NB.Core.Project.ProjectRegistry.DataDir, "camera.json");
    static float LoadFlySpeed()
    {
        try
        {
            if (File.Exists(CameraSettingsPath) && System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, float>>(File.ReadAllText(CameraSettingsPath)) is { } d
                && d.TryGetValue("FlySpeed", out var v) && v > 0) return Math.Clamp(v, SpeedSteps[0], SpeedSteps[^1]);
        }
        catch { }
        return 1f;
    }
    static void SaveFlySpeed(float v)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CameraSettingsPath)!);
            File.WriteAllText(CameraSettingsPath, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, float> { ["FlySpeed"] = v }));
        }
        catch { }
    }
}
