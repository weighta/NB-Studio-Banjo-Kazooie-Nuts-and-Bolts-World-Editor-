using NB.Core.Audio;
using NB.Core.World;

namespace NB.Studio.Panels;

/// <summary>
/// The Properties tab's music section (under the transform fields) for a music region or the world's music speaker:
/// the region number and radius (regions), then one row per level script command that chooses music here (time of day,
/// Act, world music) with the track it plays (pick another game track to swap), ▶ preview and Replace… (import a WAV /
/// MP3 / OGG / FLAC … over that track's wave), and ■ Stop.
/// </summary>
public sealed class MusicPanel : UserControl
{
    public sealed record Row(string When, MusicCommand Cmd, int Slot, uint Hash);

    readonly Label _title = new() { Dock = DockStyle.Top, AutoSize = false, Height = 22, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Padding = new Padding(2, 4, 0, 0) };
    readonly FlowLayoutPanel _regionRow = new() { Dock = DockStyle.Top, Height = 30, WrapContents = false };
    readonly NumericUpDown _region = new() { Minimum = 1, Maximum = 6, Width = 44 };
    readonly Label _regionName = new() { AutoSize = true, Padding = new Padding(0, 6, 8, 0) };
    readonly NumericUpDown _radius = new() { Minimum = 0.5M, Maximum = 5000, DecimalPlaces = 1, Increment = 5, Width = 70 };
    readonly TableLayoutPanel _rows = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, Padding = new Padding(0, 2, 0, 2) };
    readonly FlowLayoutPanel _buttons = new() { Dock = DockStyle.Top, Height = 32 };
    readonly CheckBox _fit = new() { Text = "Fit to the original length", AutoSize = true, Checked = true, Padding = new Padding(6, 6, 0, 0) };
    /// <summary>Replace… options (see <see cref="MusicAudio.ReplaceOptions"/>).</summary>
    readonly FlowLayoutPanel _opts = new() { Dock = DockStyle.Top, Height = 30, WrapContents = false };
    readonly CheckBox _all = new() { Text = "All six districts", AutoSize = true, Padding = new Padding(6, 6, 0, 0) };
    readonly CheckBox _mono = new() { Text = "Mono", AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
    readonly CheckBox _half = new() { Text = "22 kHz", AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
    readonly CheckBox _loop = new() { Text = "Loop", AutoSize = true, ThreeState = true, CheckState = CheckState.Indeterminate, Padding = new Padding(0, 6, 0, 0) };
    readonly NumericUpDown _loopFrom = new() { Minimum = 0, Maximum = 3600, DecimalPlaces = 2, Increment = 0.5M, Width = 64 };
    readonly Label _note = new() { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(90, 94, 104), Padding = new Padding(2, 4, 2, 0) };
    bool _filling;
    MusicCatalog? _cat;
    readonly List<Button> _actions = new();
    bool _busy;

    /// <summary>A replacement runs: the rows' ▶ and Replace… wait (■ Stop stays).</summary>
    public void SetBusy(bool busy) { _busy = busy; foreach (var b in _actions) b.Enabled = !busy; }

    public event Action<int>? RegionChanged;
    public event Action<float>? RadiusChanged;
    public event Action<Row, uint>? CueChanged;
    public event Action<MusicCue>? Play;
    public event Action? Stop;
    public event Action<MusicCue, MusicAudio.ReplaceOptions>? Replace;

    /// <summary>The Replace… options as set (for a cue: "all districts" only for Showdown Town's six stems).</summary>
    public MusicAudio.ReplaceOptions Options(MusicCue c) => new()
    {
        FitLength = _fit.Checked && c.Variable != null, Mono = _mono.Checked, HalfRate = _half.Checked,
        Loop = _loop.CheckState == CheckState.Indeterminate ? null : _loop.Checked, LoopFromSeconds = (double)_loopFrom.Value,
        AlsoWaves = _all.Checked && c.Bank == "MusicShowdownTown" && c.Wave is >= 0 and <= 5 ? new[] { 0, 1, 2, 3, 4, 5 } : Array.Empty<int>(),
    };

    /// <summary>The rows shown now (script checks).</summary>
    public IReadOnlyList<Row> Rows => _shown;
    readonly List<Row> _shown = new();

    public MusicPanel()
    {
        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        _regionRow.Controls.Add(new Label { Text = "Region", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        _regionRow.Controls.Add(_region); _regionRow.Controls.Add(_regionName);
        _regionRow.Controls.Add(new Label { Text = "Radius", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        _regionRow.Controls.Add(_radius);
        _region.ValueChanged += (_, _) => { _regionName.Text = MusicRegion.DistrictName((int)_region.Value); if (!_filling) RegionChanged?.Invoke((int)_region.Value); };
        _radius.ValueChanged += (_, _) => { if (!_filling) RadiusChanged?.Invoke((float)_radius.Value); };
        var stop = new Button { Text = "■ Stop", AutoSize = true };
        stop.Click += (_, _) => Stop?.Invoke();
        _buttons.Controls.Add(stop); _buttons.Controls.Add(_fit); _buttons.Controls.Add(_all);
        _opts.Controls.Add(new Label { Text = "Replace as:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        _opts.Controls.Add(_mono); _opts.Controls.Add(_half); _opts.Controls.Add(_loop);
        _opts.Controls.Add(new Label { Text = "from", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }); _opts.Controls.Add(_loopFrom);
        _opts.Controls.Add(new Label { Text = "s", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        var tip = new ToolTip();
        tip.SetToolTip(_loop, "Grey: loop like the original (its intro is replaced too: the new track loops from 'from' to its end); ticked: loop; clear: play once.");
        tip.SetToolTip(_all, "Showdown Town's music is six versions of one song, one per district (Market, Seaside, Docks, Park, Posh, L.O.G.): put your track in all six so it plays everywhere in town.");
        tip.SetToolTip(_mono, "Store one channel instead of two (half the size).");
        tip.SetToolTip(_half, "Store at half the sample rate (half the size, duller sound).");
        Controls.Add(_note); Controls.Add(_opts); Controls.Add(_buttons); Controls.Add(_rows); Controls.Add(_regionRow); Controls.Add(_title);
    }

    /// <summary>Shows a music region (<paramref name="o"/>) or the world's music (<paramref name="o"/> null).</summary>
    public void ShowMusic(string title, SceneObject? o, IReadOnlyList<Row> rows, MusicCatalog cat, string note)
    {
        _filling = true;
        try
        {
            _cat = cat;
            _title.Text = title;
            _regionRow.Visible = o?.IsMusicRegion == true;
            if (o?.IsMusicRegion == true)
            {
                _region.Value = Math.Clamp(o.MusicRegionId, 1, 6);
                _regionName.Text = MusicRegion.DistrictName(o.MusicRegionId);
                _radius.Value = (decimal)Math.Clamp(o.MusicRadius, 0.5f, 5000f);
            }
            _rows.SuspendLayout();
            _rows.Controls.Clear(); _rows.RowStyles.Clear(); _shown.Clear(); _actions.Clear();
            var items = cat.Cues.Where(c => c.Wave >= 0).GroupBy(c => c.Hash).Select(g => g.First()).OrderBy(c => c.Display).ToArray();
            int r = 0;
            foreach (var row in rows)
            {
                _shown.Add(row);
                var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, DropDownWidth = 320 };
                combo.Items.AddRange(items);
                var cur = cat.Find(row.Hash);
                if (cur != null) combo.SelectedItem = items.FirstOrDefault(c => c.Hash == cur.Hash);
                else { combo.Items.Insert(0, $"unknown cue {row.Hash:X8}"); combo.SelectedIndex = 0; }
                var thisRow = row;
                combo.SelectedIndexChanged += (_, _) => { if (!_filling && combo.SelectedItem is MusicCue c) CueChanged?.Invoke(thisRow, c.Hash); };
                var play = new Button { Text = "▶", Width = 28, Height = 23, Margin = new Padding(1) };
                play.Click += (_, _) => { if (combo.SelectedItem is MusicCue c) Play?.Invoke(c); };
                var rep = new Button { Text = "Replace…", Width = 72, Height = 23, Margin = new Padding(1) };
                _actions.Add(play); _actions.Add(rep); play.Enabled = rep.Enabled = !_busy;
                rep.Click += (_, _) => { if (combo.SelectedItem is MusicCue c) Replace?.Invoke(c, Options(c)); };
                _rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
                _rows.Controls.Add(new Label { Text = row.When, AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, r);
                _rows.Controls.Add(combo, 1, r); _rows.Controls.Add(play, 2, r); _rows.Controls.Add(rep, 3, r);
                r++;
            }
            _rows.RowCount = r;
            _rows.ResumeLayout();
            _note.Text = note;
            Visible = true;
        }
        finally { _filling = false; }
    }

    public void HideMusic() { Visible = false; _shown.Clear(); }

    /// <summary>Script check: picks another cue in row <paramref name="row"/> like the user would.</summary>
    public string ScriptPick(int row, string cueName)
    {
        int k = 0;
        foreach (Control c in _rows.Controls)
        {
            if (c is not ComboBox cb || _rows.GetRow(c) != row) continue;
            var item = cb.Items.OfType<MusicCue>().FirstOrDefault(x => x.Display.Contains(cueName, StringComparison.OrdinalIgnoreCase) || x.Label.Contains(cueName, StringComparison.OrdinalIgnoreCase));
            if (item == null) return $"no cue like {cueName}";
            cb.SelectedItem = item; k++;
            return $"row {row} ({_shown[row].When}) now {item.Display}";
        }
        return k == 0 ? $"no row {row}" : "";
    }
}
