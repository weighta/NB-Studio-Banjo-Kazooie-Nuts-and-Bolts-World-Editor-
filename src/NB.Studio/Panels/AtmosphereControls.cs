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

/// <summary>Colour field: a swatch (opens the colour picker) and an RRGGBB box.</summary>
sealed class ColourField : FlowLayoutPanel
{
    readonly Button _swatch = new() { Width = 46, Height = 24, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Margin = new Padding(0, 2, 6, 0) };
    readonly TextBox _hex = new() { Width = 70, Font = new Font("Consolas", 9.5f), CharacterCasing = CharacterCasing.Upper, MaxLength = 6, Margin = new Padding(0, 3, 0, 0) };
    uint _value;
    bool _quiet;
    public event Action? Changed;
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
        using var d = new ColorDialog { FullOpen = true, Color = Ui.ToColor(_value), AnyColor = true };
        if (d.ShowDialog(FindForm()) == DialogResult.OK && Ui.ToRgb(d.Color) != _value) SetByUser(Ui.ToRgb(d.Color));
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
