using System.Diagnostics;
using System.Drawing.Drawing2D;
using NB.Core.Formats;
using NB.Core.Live;
using NB.Core.Project;
using NB.Core.Textures;
using NB.Core.World;
using NB.Studio.Viewport;

namespace NB.Studio.Panels;

/// <summary>
/// "Atmosphere" tab: the sky, light, fog and weather of the open world, in one place.
///  - Times of day (Showdown Town: morning, midday, afternoon, night): skydome, sky textures (this world's copy), ambient
///    and sun colour, sun intensity, fog colour / start / end / max opacity. Light-setup scripts of the world bundle and the
///    time-of-day scripts that run them (NB.Core.World.WorldAtmosphere; same bytes as NB.Cli obj-set in
///    snow/research/light/apply_winter.py). Edits are held until "Save to Workspace".
///  - Weather: falling snow (NB.Core.World.Weather, the Snowy Showdown Town recipe) with the snow-follows-camera mod.
///  - Live preview: writes the light and fog into the game running in Xenia while you tune (NB.Core.Live.LiveLight).
///  - 3D preview: while the tab is shown, the 3D view moves into it (left of / above the settings, resizable, can be
///    hidden) in Rendered mode with the time of day being edited; every edit shows at once, shadows follow the sun
///    (<see cref="SceneViewport.PreviewAtmosphere"/>). Leaving the tab gives the view back to the 3D View tab, with the
///    camera kept and the view mode it had. Ctrl+Z / Ctrl+Y undo and redo the edits of this tab.
/// </summary>
public sealed class AtmospherePanel : UserControl
{
    readonly Label _title = new() { Font = Ui.Title, AutoSize = true, Margin = new Padding(0, 0, 0, 2), UseMnemonic = false };
    readonly Label _subtitle = new() { Font = Ui.Body, ForeColor = Ui.Subtle, AutoSize = true, MaximumSize = new Size(900, 0), UseMnemonic = false };
    readonly Button _save = Ui.Primary("Save to Workspace");
    readonly Button _discard = Ui.Plain("Discard Changes");
    readonly Button _presets = Ui.Plain("Presets ▾");
    readonly ContextMenuStrip _presetMenu = new();
    readonly ListBox _list = new() { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 64, IntegralHeight = false, BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(246, 246, 248) };
    readonly Panel _detail = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(18, 8, 12, 12) };
    readonly FlowLayoutPanel _timeEditor = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    readonly FlowLayoutPanel _weatherEditor = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    readonly Label _empty = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 11f), ForeColor = Ui.Subtle, UseMnemonic = false };

    // time-of-day editor
    readonly Label _timeTitle = new() { Font = Ui.Title, AutoSize = true, UseMnemonic = false };
    readonly Label _timeInfo = Ui.Note("");
    readonly ComboBox _dome = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    readonly FlowLayoutPanel _skyTextures = new() { AutoSize = true, WrapContents = true, MaximumSize = new Size(760, 0), Margin = new Padding(0, 4, 0, 0) };
    readonly ColourField _ambient = new() { Title = "Ambient" }, _sun = new() { Title = "Sun" }, _fogColour = new() { Title = "Fog" };
    readonly SliderField _intensity = new(0, 5, 3, "×", true, 3);
    readonly SliderField _fogStart = new(0, 10000, 1, "units", true, 1000), _fogEnd = new(0, 20000, 1, "units", true, 3000);
    readonly SliderField _fogMax = new(0, 1, 3, "0 = no fog, 1 = solid");
    readonly SliderField _sunElev = new(-10, 90, 1, "°", true), _sunAzim = new(-180, 180, 1, "°", true);
    readonly SunDial _sunDial = new();
    Label _sunNote = null!;
    readonly HemiSwatch _hemi = new();
    readonly FlowLayoutPanel _fillBox = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0) };
    readonly CheckBox _fillOn = new() { Text = "Fill light on", AutoSize = true };
    readonly ColourField _fillColour = new() { Title = "Fill" };
    readonly SliderField _fillIntensity = new(0, 5, 3, "×", true, 3);
    readonly SliderField _fillElev = new(-90, 90, 1, "°", true), _fillAzim = new(-180, 180, 1, "°", true);
    readonly CheckBox _fogOn = new() { Text = "Fog enabled", AutoSize = true };
    readonly FogPreview _fogPreview = new() { Width = 560, Height = 64, Margin = new Padding(0, 6, 0, 0) };
    readonly Label _original = Ui.Note("");

    // weather editor
    readonly Label _snowState = new() { Font = Ui.Body, AutoSize = true, MaximumSize = new Size(640, 0), UseMnemonic = false, Margin = new Padding(0, 4, 0, 6) };
    readonly SliderField _emitLarge = new(0, 5000, 0, "flakes / s", true, 3000), _emitSmall = new(0, 5000, 0, "flakes / s", true, 3000);
    readonly SliderField _sizeLarge = new(0.05, 10, 2, "× Banjoland", true, 4), _sizeSmall = new(0.05, 10, 2, "× Banjoland", true, 4);
    readonly SliderField _lifeMin = new(0.5, 60, 1, "s", false), _lifeMax = new(0.5, 60, 1, "s", false);
    readonly SliderField _half = new(5, 400, 0, "units around the camera", true), _bottom = new(-100, 300, 0, "", false), _top = new(-100, 300, 0, "units above", false);
    readonly SliderField _buffer = new(1000, 60000, 0, "particles", false);
    readonly CheckBox _follow = new() { Text = "Snow follows the camera (falls everywhere)", AutoSize = true, Checked = true };
    readonly Button _applySnow = Ui.Primary("Add Snow to the World");
    readonly Button _removeSnow = Ui.Plain("Remove Snow");

    // live preview
    readonly CheckBox _liveOn = new() { Text = "Live preview in the running game", AutoSize = true, Margin = new Padding(0, 6, 6, 0) };
    readonly ComboBox _liveProc = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
    readonly Label _liveState = new() { AutoSize = true, Font = Ui.Small, ForeColor = Ui.Subtle, Margin = new Padding(6, 8, 0, 0), UseMnemonic = false };
    readonly System.Windows.Forms.Timer _livePush = new() { Interval = 120 };

    // 3D preview (the 3D view, moved into this tab while it is shown) and undo
    readonly SplitContainer _outer = new() { Dock = DockStyle.Fill, SplitterWidth = 5, BackColor = Color.FromArgb(222, 222, 228) };
    readonly Panel _previewHost = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(38, 40, 46) };
    readonly Panel _previewBar = new() { Dock = DockStyle.Top, Height = 24, BackColor = Color.FromArgb(48, 50, 58) };
    readonly CheckBox _previewMarkers = new() { Text = "Markers", Dock = DockStyle.Right, AutoSize = true, ForeColor = Color.FromArgb(225, 226, 232), Font = Ui.Small, Padding = new Padding(0, 0, 8, 0), Checked = false };
    readonly Label _previewCaption = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0), BackColor = Color.FromArgb(48, 50, 58), ForeColor = Color.FromArgb(225, 226, 232), Font = Ui.Small, UseMnemonic = false, AutoEllipsis = true };
    readonly Label _previewEmpty = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.FromArgb(170, 172, 180), Font = Ui.Body, UseMnemonic = false };
    readonly CheckBox _previewOn = new() { Text = "3D preview", AutoSize = true, Checked = true, Margin = new Padding(14, 7, 0, 0) };
    readonly Button _undo = Ui.Plain("↶ Undo"), _redo = Ui.Plain("↷ Redo");
    readonly System.Windows.Forms.Timer _previewThrottle = new() { Interval = 10 };
    readonly Stopwatch _sinceFrame = Stopwatch.StartNew();
    SceneViewport? _view; TabControl? _tabs; Control? _viewHome; ViewMode? _modeBefore; WorldScene? _previewScene;
    bool _previewPending, _pushing, _layingOut, _userSplit, _hidMarkers, _hidPaths;
    double _sideRatio = 0.56, _stackRatio = 0.5;

    Workspace? _ws; AssetIndex? _index; uint _world; string _worldName = "";
    WorldAtmosphere? _at;
    TimeOfDay? _cur;
    bool _binding;
    readonly Dictionary<string, Bitmap?> _thumbs = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<TimeOfDay> _edited = new();
    XeniaLive? _x; uint _lightObj;
    readonly Func<byte[]?> _probe;
    const string WeatherItem = "Weather";

    public Action<string>? Log;
    /// <summary>The world bundle changed on disk (snow added, sky texture replaced): reopen it if it is open.</summary>
    public event Func<uint, Task>? WorldChanged;
    /// <summary>The workspace's After-Party mods changed (rewrite the Xenia test patch file).</summary>
    public event Action? ExeModsChanged;
    public event Action? Changed;
    public bool HasUnsaved => _at?.Dirty == true;

    public AtmospherePanel(Func<byte[]?> textProbe)
    {
        _probe = textProbe;
        BackColor = Color.White;
        // header
        var head = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(14, 8, 10, 2) };
        head.Controls.Add(_title); head.Controls.Add(_subtitle);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(12, 2, 10, 2) };
        // one wrapping row: edit buttons, the 3D preview switch and the running-game preview
        _liveProc.Margin = new Padding(0, 4, 0, 0); _liveOn.Margin = new Padding(14, 7, 6, 0);
        bar.Controls.AddRange(new Control[] { _save, _discard, _presets, _undo, _redo, _previewOn, _liveOn, _liveProc, _liveState });
        var line = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Color.FromArgb(222, 222, 228) };
        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterWidth = 1, BackColor = Color.FromArgb(222, 222, 228) };
        split.Panel1.BackColor = _list.BackColor; split.Panel2.BackColor = Color.White;
        split.Panel1.Controls.Add(_list);
        split.Panel2.Controls.Add(_detail);
        // preview (left, or above when the tab is narrow) | settings
        _previewHost.Controls.Add(_previewEmpty);
        _previewBar.Controls.Add(_previewCaption); _previewBar.Controls.Add(_previewMarkers);
        _outer.Panel1.Controls.Add(_previewHost); _outer.Panel1.Controls.Add(_previewBar);
        _previewMarkers.CheckedChanged += (_, _) =>
        {
            if (_binding || !PreviewShown) return;
            _view!.ShowMarkers = _view.ShowPaths = _previewMarkers.Checked;
            _view.Refresh3D();
        };
        new ToolTip().SetToolTip(_previewMarkers, "Show marker boxes and path lines in the preview (hidden for a clean picture; the 3D View tab keeps its own setting).");
        _outer.Panel1.BackColor = _previewHost.BackColor; _outer.Panel2.BackColor = Color.White;
        _outer.Panel2.Controls.Add(split);
        _previewEmpty.Text = "The 3D view shows here, in Rendered mode, while you edit a time of day.";
        Controls.Add(_outer); Controls.Add(_empty); Controls.Add(line); Controls.Add(bar); Controls.Add(head);
        LoadPrefs();
        _outer.Panel1Collapsed = !_previewOn.Checked;
        Resize += (_, _) => LayoutPreview();
        _outer.SizeChanged += (_, _) => LayoutPreview();
        // only a drag of the bar by the user changes the remembered split (the container also moves it while it is laid out)
        _outer.SplitterMoving += (_, _) => _userSplit = true;
        _outer.SplitterMoved += (_, _) =>
        {
            if (!_userSplit || _layingOut || _outer.Panel1Collapsed) return;
            _userSplit = false;
            double total = _outer.Orientation == Orientation.Vertical ? _outer.Width : _outer.Height;
            if (total < 200) return;
            if (_outer.Orientation == Orientation.Vertical) _sideRatio = _outer.SplitterDistance / total; else _stackRatio = _outer.SplitterDistance / total;
            SavePrefs();
        };
        _previewOn.CheckedChanged += (_, _) => { _outer.Panel1Collapsed = !_previewOn.Checked; SavePrefs(); LayoutPreview(); SyncPreview(); };
        _previewThrottle.Tick += (_, _) => { _previewThrottle.Stop(); if (_previewPending) PushPreview(true); };
        _undo.Click += (_, _) => Undo();
        _redo.Click += (_, _) => Redo();
        new ToolTip().SetToolTip(_previewOn, "Show the 3D view in this tab while you edit (Rendered mode, the time of day you are editing, your camera). Drag the bar between the view and the settings to resize it.");
        new ToolTip().SetToolTip(_undo, "Undo the last change in this tab (Ctrl+Z)");
        new ToolTip().SetToolTip(_redo, "Redo (Ctrl+Y)");
        _detail.Resize += (_, _) => FitWidth();
        Load += (_, _) => { try { split.SplitterDistance = 236; } catch (Exception) { } };
        HandleCreated += (_, _) => { try { split.SplitterDistance = 236; } catch (Exception) { } };

        BuildTimeEditor();
        BuildWeatherEditor();
        _detail.Controls.Add(_timeEditor); _detail.Controls.Add(_weatherEditor);

        _list.DrawItem += DrawItem;
        _list.SelectedIndexChanged += (_, _) => ShowSelected();
        _save.Click += (_, _) => Save();
        _discard.Click += (_, _) => { if (_at?.Dirty == true && MessageBox.Show(this, "Discard the unsaved sky, light and fog changes?", "Atmosphere", MessageBoxButtons.OKCancel) == DialogResult.OK) Discard(); };
        _presets.Click += (_, _) => { BuildPresetMenu(); _presetMenu.Show(_presets, new Point(0, _presets.Height)); };
        _liveOn.CheckedChanged += (_, _) => { if (_binding) return; if (_liveOn.Checked) AttachLive(); else DetachLive(); };
        _liveProc.DropDown += (_, _) => FillProcesses();
        _livePush.Tick += (_, _) => { _livePush.Stop(); PushLive(); };
        new ToolTip().SetToolTip(_liveOn, "Writes the light and fog of the selected time of day into the game running in Xenia while you edit (lasts until the time of day or level reloads). Save to Workspace to keep them. The sun direction is not written live (the game's shadows only follow it after a reload).");
        new ToolTip().SetToolTip(_presets, "Reset to the original game's values, or apply the Snowy Showdown Town winter values.");
        SetWorld(0, "");
    }

    // ------------------------------------------------------------------ layout

    void BuildTimeEditor()
    {
        var t = _timeEditor;
        t.Controls.Add(_timeTitle); t.Controls.Add(_timeInfo);
        t.Controls.Add(Ui.Header("Light"));
        t.Controls.Add(Ui.Row(Ui.Caption("Ambient colour"), _ambient, _hemi));
        t.Controls.Add(Ui.Row(Ui.Caption("Sun colour"), _sun));
        t.Controls.Add(Ui.Row(Ui.Caption("Sun intensity"), _intensity));
        var sunCol = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0) };
        sunCol.Controls.Add(Ui.Row(Ui.Caption("Elevation", 70), _sunElev));
        sunCol.Controls.Add(Ui.Row(Ui.Caption("Azimuth", 70), _sunAzim));
        sunCol.Controls.Add(_sunNote = Ui.Note("Drag the sun on the dial or use the sliders: the 3D preview's shading and shadows follow at once. In the game the saved direction moves the shading and the shadows the same way (checked in Xenia); the running-game live preview does not change it (save, then reload the level).", 340));
        t.Controls.Add(Ui.Row(Ui.Caption("Sun direction"), _sunDial, sunCol));
        _fillBox.Controls.Add(Ui.Header("Fill light"));
        _fillBox.Controls.Add(Ui.Note("A second directional light without shadows (light-setup command 0x7E). Banjoland, LOGBOX 720, Terrarium of Terror and Spiral Mountain use one; Showdown Town keeps it off.", 640));
        _fillBox.Controls.Add(Ui.Row(Ui.Caption(""), _fillOn));
        _fillBox.Controls.Add(Ui.Row(Ui.Caption("Colour"), _fillColour));
        _fillBox.Controls.Add(Ui.Row(Ui.Caption("Intensity"), _fillIntensity));
        _fillBox.Controls.Add(Ui.Row(Ui.Caption("Elevation"), _fillElev));
        _fillBox.Controls.Add(Ui.Row(Ui.Caption("Azimuth"), _fillAzim));
        t.Controls.Add(_fillBox);
        t.Controls.Add(Ui.Header("Fog"));
        t.Controls.Add(Ui.Row(Ui.Caption("Fog colour"), _fogColour));
        t.Controls.Add(Ui.Row(Ui.Caption("Starts at"), _fogStart));
        t.Controls.Add(Ui.Row(Ui.Caption("Full at"), _fogEnd));
        t.Controls.Add(Ui.Row(Ui.Caption("Maximum opacity"), _fogMax));
        t.Controls.Add(_fogPreview);
        // light first (what the 3D preview is for), the sky with its large texture cards after the fog
        t.Controls.Add(Ui.Header("Sky"));
        t.Controls.Add(Ui.Row(Ui.Caption("Skydome"), _dome));
        t.Controls.Add(Ui.Note("The sky model the game draws at this time of day. Its textures are below: Replace changes this world's copy only (other levels that share a texture keep theirs). The skydome is not fogged: give the bottom of the sky the fog colour so the horizon blends.", 640));
        t.Controls.Add(_skyTextures);
        var adv = Ui.Header("Advanced (read from the game data, effect not verified in game)");
        adv.Font = new Font("Segoe UI", 9f, FontStyle.Bold); adv.ForeColor = Ui.Subtle;
        t.Controls.Add(adv);
        t.Controls.Add(Ui.Row(Ui.Caption(""), _fogOn));
        t.Controls.Add(_original);
        var reset = Ui.Plain("Reset This Time of Day to the Original");
        reset.Click += (_, _) => ResetToOriginal(_cur);
        t.Controls.Add(Ui.Row(reset));

        _ambient.Changed += () => Edit("ambient", v => v with { Ambient = _ambient.Value });
        // the colour picker: every change previews at once; one undo step per pick, none when it was cancelled
        foreach (var cf in new[] { _ambient, _sun, _fogColour, _fillColour })
        {
            cf.PickStarted += () => { _picking = true; _gestureKey = null; _undoBeforePick = _undoStack.Count; _redoBeforePick = new List<Snap>(_redoStack); };
            cf.PickEnded += ok =>
            {
                _picking = false; _gestureKey = null;
                if (!ok && _undoStack.Count > _undoBeforePick)
                {
                    // cancelled: the picker put the first colour back; the step it opened changes nothing
                    var first = _undoStack[_undoBeforePick];
                    _undoStack.RemoveRange(_undoBeforePick, _undoStack.Count - _undoBeforePick);
                    _redoStack.Clear(); _redoStack.AddRange(_redoBeforePick);
                    _edited.Clear(); _edited.UnionWith(first.Edited);
                    _at?.RefreshDirty();
                    _list.Invalidate(); UpdateButtons();
                }
            };
        }
        _sun.Changed += () => Edit("sun", v => v with { Sun = _sun.Value });
        _intensity.Changed += () => Edit("intensity", v => v with { Intensity = _intensity.Value });
        _fogColour.Changed += () => Edit("fogcolour", v => v with { FogColour = _fogColour.Value });
        _fogStart.Changed += () => Edit("fogstart", v => v with { FogStart = _fogStart.Value });
        _fogEnd.Changed += () => Edit("fogend", v => v with { FogEnd = _fogEnd.Value });
        _fogMax.Changed += () => Edit("fogmax", v => v with { FogMax = _fogMax.Value });
        _sunElev.Changed += () => Edit("sundir", v => v with { SunElevation = Rad(_sunElev.Value) });
        _sunAzim.Changed += () => Edit("sundir", v => v with { SunAzimuth = Rad(_sunAzim.Value) });
        _sunDial.Changed += () => Edit("sundir", v => v with { SunElevation = _sunDial.Value.Elevation, SunAzimuth = _sunDial.Value.Azimuth });
        _sunDial.DragEnded += () => _gestureKey = null;   // the next drag is its own undo step
        _fogOn.CheckedChanged += (_, _) => { if (!_binding) Edit("fogon", v => v with { FogOn = _fogOn.Checked }); };
        _fillOn.CheckedChanged += (_, _) => { if (!_binding) Edit("fillon", v => v with { FillOn = _fillOn.Checked }); };
        _fillColour.Changed += () => Edit("fillcolour", v => v with { FillColour = _fillColour.Value });
        _fillIntensity.Changed += () => Edit("fillintensity", v => v with { FillIntensity = _fillIntensity.Value });
        _fillElev.Changed += () => Edit("filldir", v => v with { FillElevation = Rad(_fillElev.Value) });
        _fillAzim.Changed += () => Edit("filldir", v => v with { FillAzimuth = Rad(_fillAzim.Value) });
        _dome.SelectedIndexChanged += (_, _) => { if (!_binding) SetDome(_dome.SelectedIndex); };
    }

    void BuildWeatherEditor()
    {
        var w = _weatherEditor;
        w.Controls.Add(new Label { Text = "Weather: falling snow", Font = Ui.Title, AutoSize = true });
        w.Controls.Add(_snowState);
        var presets = Ui.Row(Ui.Caption("Presets"));
        presets.WrapContents = true; presets.MaximumSize = new Size(640, 0);
        foreach (var (name, s) in new[] { ("Light flurries", SnowSettings.Light), ("Snowy Showdown Town", SnowSettings.SnowyShowdownTown), ("Heavy snowfall", SnowSettings.Heavy) })
        {
            var b = Ui.Plain(name); var set = s;
            b.Click += (_, _) => BindSnow(set);
            presets.Controls.Add(b);
        }
        w.Controls.Add(presets);
        w.Controls.Add(Ui.Header("Flakes"));
        w.Controls.Add(Ui.Row(Ui.Caption("Big flakes"), _emitLarge));
        w.Controls.Add(Ui.Row(Ui.Caption("  size"), _sizeLarge));
        w.Controls.Add(Ui.Row(Ui.Caption("Small flakes"), _emitSmall));
        w.Controls.Add(Ui.Row(Ui.Caption("  size"), _sizeSmall));
        w.Controls.Add(Ui.Row(Ui.Caption("Lifetime"), _lifeMin, new Label { Text = "to", AutoSize = true, Margin = new Padding(0, 7, 6, 0) }, _lifeMax));
        w.Controls.Add(Ui.Header("Where it snows"));
        w.Controls.Add(Ui.Row(Ui.Caption("Area (half width)"), _half));
        w.Controls.Add(Ui.Row(Ui.Caption("Falls from"), _bottom, new Label { Text = "to", AutoSize = true, Margin = new Padding(0, 7, 6, 0) }, _top));
        w.Controls.Add(Ui.Row(Ui.Caption("Particle buffer"), _buffer));
        w.Controls.Add(Ui.Row(Ui.Caption(""), _follow));
        w.Controls.Add(Ui.Row(Ui.Caption(""), Ui.Note("Uses the After-Party mod \"snow-follows-camera\" (Mods menu); it is switched on for you.", 460)));
        w.Controls.Add(Ui.Note("The snow is Banjoland's (two particle effects copied into this world, plus one effect marker). Without \"follows the camera\" it only falls in a box around the marker in the town square. Flakes have no collision: they pass through roofs. Applying saves the world bundle (Edit > Undo Last Bundle Save restores it).", 640));
        _applySnow.Margin = new Padding(0, 10, 6, 0); _removeSnow.Margin = new Padding(0, 10, 0, 0);
        w.Controls.Add(Ui.Row(_applySnow, _removeSnow));
        _applySnow.Click += async (_, _) => await ApplySnow();
        _removeSnow.Click += async (_, _) => await RemoveSnow();
    }

    /// <summary>Wraps notes and sizes track bars to the width the detail area has (no horizontal scrolling).</summary>
    void FitWidth()
    {
        int avail = Math.Max(320, _detail.ClientSize.Width - _detail.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 4);
        int bar = Math.Clamp(avail - 130 - 84 - 100, 80, 240);
        void Walk(Control c)
        {
            foreach (Control k in c.Controls)
            {
                if (k is Label l && l.AutoSize && l.MaximumSize.Width > 0) l.MaximumSize = new Size(avail - (l.Parent is FlowLayoutPanel f && f.FlowDirection == FlowDirection.LeftToRight ? 140 : 0), 0);
                else if (k is CheckBox cb && cb.MaximumSize.Width > 0) cb.MaximumSize = new Size(avail - 140, 0);
                if (k is SliderField sf && sf.HasBar) sf.BarWidth = bar;
                else if (k is FlowLayoutPanel fp && fp.WrapContents && fp.MaximumSize.Width > 0) fp.MaximumSize = new Size(avail, 0);
                if (k is not SliderField) Walk(k);
            }
        }
        _detail.SuspendLayout(); Walk(_timeEditor); Walk(_weatherEditor);
        // the sun direction row also holds the dial (and a shorter caption): narrower bars
        _sunElev.BarWidth = _sunAzim.BarWidth = Math.Clamp(avail - 130 - _sunDial.Width - _sunDial.Margin.Horizontal - 76 - 84 - 30, 60, 240);
        _sunNote.MaximumSize = new Size(Math.Max(160, avail - 130 - _sunDial.Width - _sunDial.Margin.Horizontal - 10), 0);
        _detail.ResumeLayout();
        _fogPreview.Width = Math.Min(560, avail);
        _subtitle.MaximumSize = new Size(Math.Max(300, Width - 40), 0);
    }

    // ------------------------------------------------------------------ data

    public void SetWorkspace(Workspace? ws, AssetIndex? index)
    {
        _ws = ws; _index = index;
        SetWorld(0, "");
    }

    /// <summary>Called when a world opens in the 3D view: the tab edits that world.</summary>
    /// <param name="act">The act bundle opened with the world (0: the world alone): its light setup is listed first and
    /// its bundle's copy is the one edited.</param>
    public void SetWorld(uint bundle, string display, uint act = 0)
    {
        bundle &= 0xFFFFFF; act &= 0xFFFFFF;
        // the same world reopened (after a texture replace, snow, a world save …): keep the editor, its selection and its
        // unsaved edits, unless the bundle was reloaded from disk (Revert / Undo Last Bundle Save)
        if (_at != null && bundle == _world && act == _act && _ws != null && _at.IsCurrent) { _list.Invalidate(); if (_list.SelectedItem is string) BindWeather(); return; }
        if (_at?.Dirty == true && (bundle != _world || act != _act) && _ws != null)
        {
            if (MessageBox.Show(this, $"Save the unsaved sky, light and fog changes of {_worldName} first?", "Atmosphere", MessageBoxButtons.YesNo) == DialogResult.Yes) Save();
        }
        _world = bundle; _act = act; _worldName = display;
        Reload();
    }
    uint _act;
    (uint, uint) _loadedFor;

    /// <summary>Puts the saved values back (and into the running game when live preview is on).</summary>
    public void Discard()
    {
        if (_at == null) return;
        _at.Revert();
        _edited.Clear();
        _undoStack.Clear(); _redoStack.Clear(); _gestureKey = null;
        Log?.Invoke("Atmosphere: unsaved changes discarded.");
        if (_cur != null) { Bind(_cur); if (_liveOn.Checked) PushLive(); }
        PushPreview(true);
        _list.Invalidate(); UpdateButtons();
    }

    public void Reload()
    {
        // another world or act: start at its own (first) entry rather than the selection of the previous one
        string? keep = (_world, _act) != _loadedFor ? null : _list.SelectedItem is TimeOfDay st ? st.Light.Name : _list.SelectedItem as string;
        _loadedFor = (_world, _act);
        if (_at?.Dirty == true) _at.Revert();   // never leave unsaved edits behind in the shared bundle objects
        _at = null; _cur = null; _edited.Clear();
        _undoStack.Clear(); _redoStack.Clear(); _gestureKey = null;
        foreach (var b in _thumbs.Values) b?.Dispose();
        _thumbs.Clear();
        _list.Items.Clear();
        if (_ws == null || _index == null || _world == 0)
        {
            ShowEmpty(_ws == null ? "Open a workspace to edit sky, light, fog and weather." : "Open a world (Worlds tab, double-click Showdown Town) to edit its sky, light, fog and weather.");
            return;
        }
        try { _at = WorldAtmosphere.Load(_ws, _index, _world, _act); }
        catch (Exception e) { ShowEmpty("The atmosphere of this world could not be read: " + e.Message); return; }
        _empty.Visible = false;
        _title.Text = $"Atmosphere — {_worldName}";
        _subtitle.Text = _at.Times.Count == 0
            ? "No level script of this world runs a light setup (only weather can be edited here)."
            : $"Sky, light and fog of {_at.Times.Count} light setup(s) used by this world ({(_at.WorldName == "showdowntown" ? "times of day" : "one per act")}{(_act != 0 ? ", the open act first" : "")}), and weather. Changes are kept until you press Save to Workspace; Build > Create Distributable Patch then includes them.";
        foreach (var t in _at.Times) _list.Items.Add(t);
        _list.Items.Add(WeatherItem);
        int sel = _list.Items.Cast<object>().ToList().FindIndex(i => i is TimeOfDay t ? t.Light.Name == keep : Equals(i, keep));
        _list.SelectedIndex = Math.Max(0, sel);
        UpdateButtons();
        _ = LoadThumbsAsync();
        SyncPreview();
    }

    void ShowEmpty(string text)
    {
        _title.Text = "Atmosphere"; _subtitle.Text = "Sky, light, fog and weather of the open world.";
        _empty.Text = text; _empty.Visible = true; _empty.BringToFront();
        UpdateButtons();
    }

    void UpdateButtons()
    {
        bool dirty = _at?.Dirty == true;
        _save.Enabled = dirty; _discard.Enabled = dirty; _presets.Enabled = _at != null && _at.Times.Count > 0;
        _save.Text = dirty ? $"Save to Workspace ({_edited.Count} edited)" : "Save to Workspace";
        _undo.Enabled = _undoStack.Count > 0; _redo.Enabled = _redoStack.Count > 0;
        Changed?.Invoke();
    }

    LightValues Values(TimeOfDay t) => t.Light.Values;

    void Edit(string key, Func<LightValues, LightValues> f)
    {
        if (_binding || _cur == null || _at == null) return;
        var before = _cur.Light.Values;
        var after = f(before);
        if (after == before) return;
        BeginEdit(key);
        _cur.Light.Values = after;
        _at.MarkDirty(_cur.Light.Bundle);
        _edited.Add(_cur);
        _fogPreview.Set(after);
        SyncDerived(after);
        // only the edited entry's row (repainting every sky thumbnail on each slider step slowed dragging down)
        int row = _list.Items.IndexOf(_cur);
        if (row >= 0) _list.Invalidate(_list.GetItemRectangle(row)); else _list.Invalidate();
        UpdateButtons();
        if (_liveOn.Checked) { _livePush.Stop(); _livePush.Start(); }
        PushPreview();
    }

    static float Rad(float deg) => deg * MathF.PI / 180;
    static float Deg(float rad) { float d = rad * 180 / MathF.PI; d %= 360; if (d > 180) d -= 360; if (d <= -180) d += 360; return d; }

    /// <summary>Controls that show the same value another way (dial and sliders, ambient swatches), without raising edits.</summary>
    void SyncDerived(LightValues v)
    {
        bool was = _binding; _binding = true;
        try
        {
            _sunDial.Value = (v.SunElevation, v.SunAzimuth);
            _sunDial.SunColour = Ui.ToColor(v.Sun);
            if (Math.Abs(_sunElev.Value - Deg(v.SunElevation)) > 0.051) _sunElev.Value = Deg(v.SunElevation);
            if (Math.Abs(_sunAzim.Value - Deg(v.SunAzimuth)) > 0.051) _sunAzim.Value = Deg(v.SunAzimuth);
            _hemi.Ambient = v.Ambient;
        }
        finally { _binding = was; }
    }

    void SetDome(int i)
    {
        if (_cur == null || _at == null || i < 0 || i >= _at.Domes.Count || _cur.DomeOffset < 0) return;
        var d = _at.Domes[i];
        if (_cur.DomeId == d.Id) return;
        BeginEdit("dome " + DateTime.UtcNow.Ticks);
        _cur.DomeId = d.Id;
        _at.MarkDirty(_cur.PhaseBundle);
        _edited.Add(_cur);
        Log?.Invoke($"Atmosphere: {_cur.Display} sky → {d.ShortName} ({d.Id:X8}) (unsaved)");
        FillSkyTextures();
        _list.Invalidate();
        UpdateButtons();
        PushPreview(true);
    }

    public void Save() { if (ConfirmShared()) Save(null); }

    static readonly bool Scripted = Environment.GetCommandLineArgs().Length > 1;   // script runs: no questions

    /// <summary>Before saving: says which other levels use an edited light setup and which bundles get the copies.</summary>
    bool ConfirmShared()
    {
        if (_at == null || !_at.Dirty || Scripted) return true;
        var lines = new List<string>();
        foreach (var t in _edited)
        {
            if (t.SharedWith.Count > 0)
                lines.Add($"• {t.Display}: {t.Light.Name.Replace("aid_script_banjox_lightsetup_", "")} is also used by {string.Join(", ", t.SharedWith.Select(x => x.Replace("aid_script_banjox_", "")))}.");
            if (t.Light.Copies.Count > 0)
                lines.Add($"• {t.Display}: the game keeps {t.Light.Copies.Count + 1} copies of this setup (bundles {t.Light.Bundle:x6}, {string.Join(", ", t.Light.Copies.Select(c => c.Bundle.ToString("x6")).Distinct())}): all of them are updated.");
        }
        if (lines.Count == 0) return true;
        return MessageBox.Show(this, "Saving changes more than this level:\n\n" + string.Join("\n", lines) + $"\n\nBundles written: {string.Join(", ", _at.BundlesToSave().Select(b => b.ToString("x6")))}. Save?",
            "Atmosphere", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == DialogResult.OK;
    }

    /// <param name="also">Another change already made in the same bundle object (a sky texture), saved with these edits.</param>
    void Save(string? also)
    {
        if (_at == null || !_at.Dirty) return;
        try
        {
            string what = string.Join(", ", _edited.Select(t => t.Display));
            var bundles = _at.Save(what.Length == 0 ? also ?? "atmosphere" : $"atmosphere: sky / light / fog of {what}" + (also != null ? "; " + also : ""));
            foreach (var t in _edited) Log?.Invoke($"Atmosphere: {t.Display}: {t.Light.Values}" + (t.PhaseScript != null ? $", sky {DomeName(t.DomeId)}" : ""));
            Log?.Invoke($"Atmosphere saved ({what}) → bundle(s) {string.Join(", ", bundles)}.");
            _edited.Clear();
        }
        catch (Exception e) { Log?.Invoke("ERROR: saving the atmosphere failed: " + e.Message); MessageBox.Show(this, "Saving failed: " + e.Message, "Atmosphere"); }
        _list.Invalidate();
        UpdateButtons();
    }

    string DomeName(uint id) => _at?.Domes.FirstOrDefault(d => d.Id == id)?.ShortName ?? $"0x{id:X8}";

    void ResetToOriginal(TimeOfDay? t, bool log = true)
    {
        if (t == null || _at == null) return;
        if (log) BeginEdit("reset " + DateTime.UtcNow.Ticks);
        try
        {
            var (light, dome) = _at.Original(t);
            if (t.Light.Values != light) { t.Light.Values = light; _at.MarkDirty(t.Light.Bundle); _edited.Add(t); }
            if (t.DomeOffset >= 0 && dome != 0 && t.DomeId != dome) { t.DomeId = dome; _at.MarkDirty(t.PhaseBundle); _edited.Add(t); }
            if (log) Log?.Invoke($"Atmosphere: {t.Display} reset to the original game ({light}) (unsaved)");
        }
        catch (Exception e) { Log?.Invoke("Atmosphere: original values unavailable: " + e.Message); }
        if (t == _cur) { Bind(t); PushPreview(true); }
        _list.Invalidate(); UpdateButtons();
    }

    void BuildPresetMenu()
    {
        _presetMenu.Items.Clear();
        _presetMenu.Items.Add($"Reset {_cur?.Display ?? "this time of day"} to the original game", null, (_, _) => ResetToOriginal(_cur)).Enabled = _cur != null;
        _presetMenu.Items.Add("Reset every time of day to the original game", null, (_, _) => { BeginEdit("reset all " + DateTime.UtcNow.Ticks); foreach (var t in _at!.Times) ResetToOriginal(t, false); Log?.Invoke("Atmosphere: every time of day reset to the original game (unsaved)"); });
        _presetMenu.Items.Add(new ToolStripSeparator());
        var w = _presetMenu.Items.Add("Winter (Snowy Showdown Town): cold light, white fog, midday under the morning sky", null, (_, _) => ApplyWinter());
        w.Enabled = _at != null && _at.Times.Any(t => WorldAtmosphere.WinterPreset.ContainsKey(t.Light.ShortName));
        w.ToolTipText = "The light and fog values of the official Snowy Showdown Town mod (snow/research/light). Sky textures are separate (Sky > Replace).";
    }

    /// <summary>Snowy Showdown Town's winter light and fog for every time of day, and the midday sky → morning dome.</summary>
    public void ApplyWinter()
    {
        if (_at == null) return;
        BeginEdit("winter " + DateTime.UtcNow.Ticks);
        foreach (var t in _at.Times)
        {
            if (!WorldAtmosphere.WinterPreset.TryGetValue(t.Light.ShortName, out var p)) continue;
            var v = t.Light.Values with { Ambient = p.Amb, Sun = p.Sun, Intensity = p.Inten, FogColour = p.Fog, FogStart = p.Start, FogEnd = p.End, FogMax = p.Max };
            if (v != t.Light.Values) { t.Light.Values = v; _at.MarkDirty(t.Light.Bundle); _edited.Add(t); }
            if (t.Light.ShortName == "main" && t.DomeOffset >= 0 && _at.Domes.FirstOrDefault(d => d.ShortName == "morning") is { } morning && t.DomeId != morning.Id)
            { t.DomeId = morning.Id; _at.MarkDirty(t.PhaseBundle); _edited.Add(t); }
        }
        Log?.Invoke("Atmosphere: winter preset applied to every time of day (unsaved)");
        if (_cur != null) Bind(_cur);
        PushPreview(true);
        _list.Invalidate(); UpdateButtons();
    }

    // ------------------------------------------------------------------ selection / binding

    void ShowSelected()
    {
        _livePush.Stop();
        if (_list.SelectedItem is TimeOfDay t)
        {
            _cur = t; _weatherEditor.Visible = false; _timeEditor.Visible = true;
            Bind(t);
            if (_liveOn.Checked) PushLive();
            if (PreviewShown || _at?.Dirty == true) PushPreview(true);
        }
        else if (_list.SelectedItem is string)
        {
            _cur = null; _timeEditor.Visible = false; _weatherEditor.Visible = true;
            BindWeather();
        }
        _detail.AutoScrollPosition = Point.Empty;
        FitWidth();
    }

    void Bind(TimeOfDay t)
    {
        _binding = true;
        try
        {
            var v = t.Light.Values;
            _timeTitle.Text = t.Display;
            _timeInfo.Text = $"Light setup {t.Light.Name}: {t.Where}." + (t.CurrentAct ? " Used by the act you opened." : "")
                + (t.SharedWith.Count > 0 ? $" Also used by other levels: {string.Join(", ", t.SharedWith.Select(x => x.Replace("aid_script_banjox_", "")))}." : "")
                + (t.DomeOffset >= 0 && t.PhaseScript != null ? $" Its skydome is set by {t.PhaseScript} ({t.PhaseBundle:x6})." : "");
            _ambient.Value = v.Ambient; _sun.Value = v.Sun; _intensity.Value = v.Intensity;
            _fogColour.Value = v.FogColour; _fogStart.Value = v.FogStart; _fogEnd.Value = v.FogEnd; _fogMax.Value = v.FogMax;
            _sunElev.Value = Deg(v.SunElevation); _sunAzim.Value = Deg(v.SunAzimuth); _fogOn.Checked = v.FogOn;
            _sunDial.Value = (v.SunElevation, v.SunAzimuth); _sunDial.SunColour = Ui.ToColor(v.Sun); _hemi.Ambient = v.Ambient;
            _fillBox.Visible = v.HasFill;
            _fillOn.Checked = v.FillOn; _fillColour.Value = v.FillColour; _fillIntensity.Value = v.FillIntensity;
            _fillElev.Value = Deg(v.FillElevation); _fillAzim.Value = Deg(v.FillAzimuth);
            _fogPreview.Set(v);
            _dome.Items.Clear();
            foreach (var d in _at!.Domes) _dome.Items.Add(DomeLabel(d));
            _dome.Enabled = t.DomeOffset >= 0;
            _dome.SelectedIndex = t.DomeOffset >= 0 ? _at.Domes.FindIndex(d => d.Id == t.DomeId) : -1;
            try { var (o, od) = _at.Original(t); _original.Text = $"Original game: {o}" + (t.DomeOffset >= 0 ? $", sky {DomeName(od)}" : ""); }
            catch (Exception) { _original.Text = ""; }
            FillSkyTextures();
        }
        finally { _binding = false; }
    }

    static string DomeLabel(SkyDome d)
    {
        var sky = d.Textures.FirstOrDefault(x => x.Contains("sky"));
        string extra = sky == null ? "" : sky.Contains("spiralmountain") ? " (blue sky, shared with Spiral Mountain)" : "";
        return d.ShortName + extra;
    }

    void FillSkyTextures()
    {
        foreach (Control c in _skyTextures.Controls.Cast<Control>().ToList()) c.Dispose();
        _skyTextures.Controls.Clear();
        if (_cur == null || _at == null) return;
        var dome = _at.Domes.FirstOrDefault(d => d.Id == _cur.DomeId);
        if (_cur.DomeOffset < 0 || dome == null) { _skyTextures.Controls.Add(Ui.Note("This light setup has no time-of-day script with a skydome.")); return; }
        foreach (var stem in dome.Textures.OrderBy(s => s.Contains("sky") ? 0 : 1))
            _skyTextures.Controls.Add(SkyCard(stem));
    }

    Control SkyCard(string stem)
    {
        bool sky = stem.Contains("sky");
        var card = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 12, 8), Padding = new Padding(6), BackColor = Color.FromArgb(246, 246, 248) };
        var pic = new PictureBox { Width = sky ? 300 : 80, Height = 75, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(60, 62, 70), Tag = stem };
        if (_thumbs.TryGetValue(stem, out var bmp) && bmp != null) pic.Image = bmp;
        var name = stem.Replace("aid_texture_banjox_shared_", "").Replace("aid_texture_banjox_", "");
        var label = new Label { Text = name, AutoSize = true, MaximumSize = new Size(sky ? 300 : 160, 0), Font = Ui.Small, UseMnemonic = false };
        var rep = Ui.Plain("Replace…"); var exp = Ui.Plain("Export…");
        rep.Click += async (_, _) => await ReplaceSky(stem, null);
        exp.Click += (_, _) => ExportSky(stem);
        card.Controls.Add(pic); card.Controls.Add(label); card.Controls.Add(Ui.Row(rep, exp));
        new ToolTip().SetToolTip(pic, stem + (sky ? "" : "\n(drawn on the dome too, not a sky panorama)"));
        return card;
    }

    async Task LoadThumbsAsync()
    {
        if (_at == null || _ws == null || _index == null) return;
        var at = _at; var ws = _ws; var idx = _index; uint wb = _world;
        var stems = at.Domes.SelectMany(d => d.Textures).Distinct().ToList();
        var res = await Task.Run(() =>
        {
            var r = new TextureResolver(ws, idx, ws.LoadResident(wb), wb);
            var l = new List<(string, Bitmap?)>();
            foreach (var s in stems)
            {
                try
                {
                    var img = r.Load(s);
                    if (img == null) { l.Add((s, null)); continue; }
                    var (rgba, w, h) = img.Value;
                    while (w > 1024) (rgba, w, h) = ImageIO.Half(rgba, w, h);
                    l.Add((s, ImageIO.ToBitmap(rgba, w, h, keepAlpha: false)));
                }
                catch (Exception) { l.Add((s, null)); }
            }
            return l;
        });
        if (at != _at) { foreach (var (_, b) in res) b?.Dispose(); return; }
        foreach (var (s, b) in res) { if (_thumbs.TryGetValue(s, out var old)) old?.Dispose(); _thumbs[s] = b; }
        foreach (var pb in _skyTextures.Controls.OfType<FlowLayoutPanel>().SelectMany(c => c.Controls.OfType<PictureBox>()))
            if (pb.Tag is string st && _thumbs.TryGetValue(st, out var bm)) pb.Image = bm;
        _list.Invalidate();
    }

    // ------------------------------------------------------------------ sky textures

    void ExportSky(string stem)
    {
        if (_ws == null || _index == null) return;
        using var d = new SaveFileDialog { Filter = "PNG image|*.png", FileName = stem.Replace("aid_texture_banjox_", "") + ".png", Title = "Export sky texture" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        var img = new TextureResolver(_ws, _index, _ws.LoadResident(_world), _world).LoadFull(stem);
        if (img == null) { MessageBox.Show(this, "The texture could not be decoded.", "Atmosphere"); return; }
        ImageIO.Save(d.FileName, img.Value.Rgba, img.Value.W, img.Value.H);
        Log?.Invoke($"Atmosphere: exported {stem} ({img.Value.W}x{img.Value.H}) → {d.FileName}");
    }

    /// <summary>Replaces a sky texture in this world's bundle (resident levels + streamed top level) with an image file.</summary>
    public async Task<bool> ReplaceSky(string stem, string? file, bool confirm = true)
    {
        if (_ws == null || _index == null || _at == null) return false;
        if (file == null)
        {
            using var d = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.tga;*.tif;*.tiff|All files|*.*", Title = "Replace " + stem.Replace("aid_texture_banjox_", "") };
            if (d.ShowDialog(this) != DialogResult.OK) return false;
            file = d.FileName;
        }
        var world = _ws.LoadResident(_world);
        bool inWorld = world.Symbols.Any(s => TextureReplacer.Stem(AssetIds.DisplayName(s)).Equals(stem, StringComparison.OrdinalIgnoreCase));
        var others = _index.Entries.Where(e => e.Type == "texture" && TextureReplacer.Stem(e.Name).Equals(stem, StringComparison.OrdinalIgnoreCase) && e.Bundle != _world).Select(e => e.Bundle).Distinct().ToList();
        var img = ImageIO.Load(file);
        if (confirm)
        {
            string scope = inWorld ? $"in this world's bundle {_world:x6} only" + (others.Count > 0 ? $" ({others.Count} other bundle(s) keep their copy)" : "")
                                   : $"in the shared bundle(s) {string.Join(", ", others.Select(b => b.ToString("x6")))}: every level using them changes";
            if (MessageBox.Show(this, $"Replace {stem.Replace("aid_texture_banjox_", "")} with {Path.GetFileName(file)} ({img.W}x{img.H}) {scope}?\n\nThe image is fitted to the stored size. Edit > Undo Last Bundle Save restores the world bundle.",
                    "Replace sky texture", MessageBoxButtons.OKCancel) != DialogResult.OK) return false;
        }
        UseWaitCursor = true;
        try
        {
            var ws = _ws; uint wb = _world; var at = _at;
            var targets = inWorld ? new List<uint> { wb } : others;
            if (!inWorld && _at.Dirty) Save();
            // this world's copy: replaced in the bundle object the editor holds, then one save with any pending light edits
            var results = await Task.Run(() => targets.Select(b => (b, inWorld ? TextureReplacer.ReplaceLoaded(ws, b, ws.LoadResident(b), stem, img.Rgba, img.W, img.H)
                                                                               : TextureReplacer.Replace(ws, b, stem, img.Rgba, img.W, img.H))).ToList());
            if (inWorld && results[0].Item2.ResidentAssets > 0) { at.MarkDirty(wb); Save($"replaced sky texture {stem}"); }
            foreach (var (b, r) in results)
            {
                foreach (var n in r.Notes) Log?.Invoke("  " + n);
                Log?.Invoke($"Atmosphere: sky texture {stem} replaced in {b:x6} ({r.ResidentAssets} resident, {r.StreamedAssets} streamed) with {Path.GetFileName(file)}");
            }
            if (results.Sum(x => x.Item2.ResidentAssets + x.Item2.StreamedAssets) == 0) throw new InvalidDataException($"{stem} could not be replaced");
            if (_thumbs.TryGetValue(stem, out var old)) { old?.Dispose(); _thumbs.Remove(stem); }
            var (rgba, w, h) = img;
            while (w > 1024) (rgba, w, h) = ImageIO.Half(rgba, w, h);
            _thumbs[stem] = ImageIO.ToBitmap(rgba, w, h, keepAlpha: false);
            FillSkyTextures(); _list.Invalidate();
            if (_view?.Scene != null && (_view.Scene.Bundle & 0xFFFFFF) == wb)
            {
                int n = _view.ReloadTexture(stem);
                Log?.Invoke($"Atmosphere: {stem} refreshed in the 3D view ({n} texture(s), no world reload).");
                if (PreviewShown) _view.RenderNow();
            }
            else if (WorldChanged != null) await WorldChanged(wb);
            return true;
        }
        catch (Exception e) { Log?.Invoke("ERROR: sky texture: " + e.Message); MessageBox.Show(this, e.Message, "Replace sky texture"); return false; }
        finally { UseWaitCursor = false; }
    }

    // ------------------------------------------------------------------ weather

    void BindWeather()
    {
        if (_ws == null || _index == null) return;
        SnowSettings? s = null;
        try { s = Weather.Read(_ws, _index, _ws.LoadResident(_world)); } catch (Exception e) { Log?.Invoke("Weather: " + e.Message); }
        bool mod = _ws.Manifest.ExeMods.Contains(Weather.ExeModId);
        _snowState.Text = s == null
            ? "No falling snow in this world yet. Pick a preset or set the values, then Add Snow to the World."
            : $"It snows in this world: {s.EmitLarge:G5} big + {s.EmitSmall:G5} small flakes per second{(s.FollowCamera ? ", around the camera" : ", around the marker")}." +
              (s.FollowCamera && !mod ? "\n⚠ The After-Party mod \"snow-follows-camera\" is off in this workspace: switch it on (Mods menu) or apply again." : "");
        _snowState.ForeColor = s != null && s.FollowCamera && !mod ? Color.Firebrick : SystemColors.ControlText;
        _applySnow.Text = s == null ? "Add Snow to the World" : "Update the Snow";
        _removeSnow.Enabled = s != null;
        BindSnow(s ?? SnowSettings.SnowyShowdownTown);
    }

    void BindSnow(SnowSettings s)
    {
        _emitLarge.Value = s.EmitLarge; _emitSmall.Value = s.EmitSmall; _sizeLarge.Value = s.SizeLarge; _sizeSmall.Value = s.SizeSmall;
        _lifeMin.Value = s.LifeMin; _lifeMax.Value = s.LifeMax; _half.Value = s.HalfWidth; _bottom.Value = s.Bottom; _top.Value = s.Top;
        _buffer.Value = s.Buffer; _follow.Checked = s.FollowCamera;
        _snowPos = s.Position;
    }
    System.Numerics.Vector3 _snowPos = new(-10, 40, 306);

    SnowSettings CurrentSnow() => new()
    {
        EmitLarge = _emitLarge.Value, EmitSmall = _emitSmall.Value, SizeLarge = _sizeLarge.Value, SizeSmall = _sizeSmall.Value,
        LifeMin = _lifeMin.Value, LifeMax = _lifeMax.Value, HalfWidth = _half.Value, Bottom = _bottom.Value, Top = _top.Value,
        Buffer = (int)_buffer.Value, FollowCamera = _follow.Checked, Position = _snowPos,
    };

    public async Task ApplySnow()
    {
        if (_ws == null || _index == null) return;
        var s = CurrentSnow();
        if (s.LifeMax < s.LifeMin || s.Top < s.Bottom) { MessageBox.Show(this, "The lifetime and height ranges must go from low to high.", "Weather"); return; }
        if (_at?.Dirty == true) Save();
        var ws = _ws; var idx = _index; uint wb = _world;
        UseWaitCursor = true; _applySnow.Enabled = false;
        try
        {
            var notes = await Task.Run(() =>
            {
                var world = ws.LoadResident(wb);
                var n = Weather.Apply(ws, idx, world, s);
                ws.SaveResident(wb, world, $"weather: falling snow ({s.EmitLarge:G5} + {s.EmitSmall:G5} flakes/s{(s.FollowCamera ? ", follows the camera" : "")})");
                return n;
            });
            foreach (var n in notes) Log?.Invoke("  " + n);
            bool modChanged = false;
            if (s.FollowCamera && !ws.Manifest.ExeMods.Contains(Weather.ExeModId)) { ws.Manifest.ExeMods.Add(Weather.ExeModId); ws.SaveManifest(); modChanged = true; Log?.Invoke("Weather: After-Party mod snow-follows-camera switched on (Mods menu)."); }
            Log?.Invoke($"Weather: falling snow saved into world bundle {wb:x6}.");
            if (modChanged) ExeModsChanged?.Invoke();
            if (WorldChanged != null) await WorldChanged(wb);
        }
        catch (Exception e) { Log?.Invoke("ERROR: weather: " + e.Message); MessageBox.Show(this, e.Message, "Weather"); }
        finally { UseWaitCursor = false; _applySnow.Enabled = true; if (_list.SelectedItem is string) BindWeather(); }
    }

    public async Task RemoveSnow(bool confirm = true)
    {
        if (_ws == null) return;
        if (confirm && MessageBox.Show(this, "Take the falling snow out of this world (particle effects and marker)?", "Weather", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        if (_at?.Dirty == true) Save();
        var ws = _ws; uint wb = _world;
        try
        {
            var notes = await Task.Run(() => { var world = ws.LoadResident(wb); var n = Weather.Remove(world); ws.SaveResident(wb, world, "weather: falling snow removed"); return n; });
            foreach (var n in notes) Log?.Invoke("  " + n);
            if (ws.Manifest.ExeMods.Contains(Weather.ExeModId) && (!confirm || MessageBox.Show(this, "Also switch off the After-Party mod snow-follows-camera?", "Weather", MessageBoxButtons.YesNo) == DialogResult.Yes))
            { ws.Manifest.ExeMods.Remove(Weather.ExeModId); ws.SaveManifest(); ExeModsChanged?.Invoke(); }
            Log?.Invoke($"Weather: falling snow removed from {wb:x6}.");
            if (WorldChanged != null) await WorldChanged(wb);
        }
        catch (Exception e) { Log?.Invoke("ERROR: weather: " + e.Message); MessageBox.Show(this, e.Message, "Weather"); }
        finally { if (_list.SelectedItem is string) BindWeather(); }
    }

    // ------------------------------------------------------------------ live preview

    void FillProcesses()
    {
        var sel = _liveProc.SelectedItem as ProcItem;
        _liveProc.Items.Clear();
        foreach (var p in XeniaLive.GameProcessNames.SelectMany(n => Process.GetProcessesByName(n)).OrderBy(p => p.Id))   // Xenia builds and reNut
        {
            string title = ""; try { title = p.MainWindowTitle; } catch (Exception) { }
            _liveProc.Items.Add(new ProcItem(p.Id, $"{p.ProcessName} ({p.Id}){(title.Length > 0 ? " — " + title : "")}"));
        }
        if (sel != null) _liveProc.SelectedItem = _liveProc.Items.OfType<ProcItem>().FirstOrDefault(i => i.Pid == sel.Pid);
        if (_liveProc.SelectedIndex < 0 && _liveProc.Items.Count == 1) _liveProc.SelectedIndex = 0;
    }

    sealed record ProcItem(int Pid, string Text) { public override string ToString() => Text; }

    /// <summary>Attaches to the Xenia process <paramref name="pid"/> (or the one picked in the list) for live preview.</summary>
    public string AttachLive(int? pid = null)
    {
        DetachLive(false);
        try
        {
            if (pid == null)
            {
                if (_liveProc.Items.Count == 0) FillProcesses();
                if (_liveProc.Items.Count == 0)
                {
                    _liveState.Text = "Xenia is not running (Build > Launch, F5).";
                    _liveOn.Checked = false; return _liveState.Text;
                }
            }
            var probe = _probe() ?? throw new InvalidOperationException("open a workspace first");
            // the picked window, else whichever running game process has the game loaded (XeniaLive.Attach)
            _x = pid != null ? XeniaLive.AttachPid(pid.Value, probe) : XeniaLive.Attach(probe, (_liveProc.SelectedItem as ProcItem)?.Pid);
            pid = _x.Pid;
            _lightObj = LiveLight.Find(_x);
            if (_lightObj == 0) { _liveState.Text = $"attached to {pid}; no level light found yet (load Showdown Town)"; }
            else
            {
                var live = LiveLight.Read(_x, _lightObj);
                var match = _at?.Times.OrderBy(t => Distance(t.Light.Values, live)).FirstOrDefault();
                _liveState.Text = $"attached to {pid}" + (match != null ? $": the game shows {match.Display}" : "");
                Log?.Invoke($"Atmosphere live: Xenia {pid}, light object 0x{_lightObj:X8}: {live}");
                if (_cur != null) PushLive();
            }
            if (!_liveOn.Checked) { _binding = true; _liveOn.Checked = true; _binding = false; }
        }
        catch (Exception e) { _liveState.Text = e.Message; _x?.Dispose(); _x = null; }
        return _liveState.Text;
    }

    static float Distance(LightValues a, LightValues b)
    {
        float C(uint x, uint y) => Math.Abs((int)(x >> 16 & 255) - (int)(y >> 16 & 255)) + Math.Abs((int)(x >> 8 & 255) - (int)(y >> 8 & 255)) + Math.Abs((int)(x & 255) - (int)(y & 255));
        return C(a.Ambient, b.Ambient) + C(a.Sun, b.Sun) + C(a.FogColour, b.FogColour) + Math.Abs(a.FogStart - b.FogStart) + Math.Abs(a.FogEnd - b.FogEnd) / 10 + 100 * Math.Abs(a.FogMax - b.FogMax);
    }

    void DetachLive(bool uncheck = true)
    {
        _livePush.Stop();
        _x?.Dispose(); _x = null; _lightObj = 0;
        if (uncheck && _liveOn.Checked) { _liveOn.Checked = false; }
        if (uncheck) _liveState.Text = "";
    }

    /// <summary>Writes the selected time of day into the running game's light (re-finds the light after a reload).</summary>
    public string PushLive()
    {
        if (_x == null || _cur == null) return "not attached";
        try
        {
            var key = _x.Read(LiveLight.FogGlobals, 8);
            if (_lightObj == 0 || !_x.Read(_lightObj + 0xD0, 8).AsSpan().SequenceEqual(key)) _lightObj = LiveLight.Find(_x);
            if (_lightObj == 0) { _liveState.Text = "no level light in the game right now"; return _liveState.Text; }
            LiveLight.Write(_x, _lightObj, _cur.Light.Values);
            _liveState.Text = $"live: {_cur.Display} values written to the game";
        }
        catch (Exception e) { _liveState.Text = "live preview failed: " + e.Message; }
        return _liveState.Text;
    }

    /// <summary>The values currently in the game (for scripts / checks).</summary>
    public LightValues? ReadLive() => _x != null && _lightObj != 0 ? LiveLight.Read(_x, _lightObj) : null;

    // ------------------------------------------------------------------ list drawing

    void DrawItem(object? s, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        bool sel = (e.State & DrawItemState.Selected) != 0;
        var r = e.Bounds;
        using (var bg = new SolidBrush(sel ? Color.FromArgb(255, 236, 214) : _list.BackColor)) g.FillRectangle(bg, r);
        if (sel) using (var acc = new SolidBrush(Ui.Accent)) g.FillRectangle(acc, r.X, r.Y, 4, r.Height);
        var item = _list.Items[e.Index];
        var swatch = new Rectangle(r.X + 12, r.Y + 8, 64, r.Height - 16);
        string title, sub; bool dirty = false;
        if (item is TimeOfDay t)
        {
            var v = t.Light.Values;
            var dome = _at?.Domes.FirstOrDefault(d => d.Id == t.DomeId);
            var skyStem = dome?.Textures.FirstOrDefault(x => x.Contains("sky"));
            if (skyStem != null && _thumbs.TryGetValue(skyStem, out var bmp) && bmp != null)
            {
                // the band above the horizon (t ~ 0.55..0.9 of the panorama)
                var src = new Rectangle(0, (int)(bmp.Height * 0.45), bmp.Width / 4, (int)(bmp.Height * 0.45));
                g.DrawImage(bmp, swatch, src, GraphicsUnit.Pixel);
            }
            else using (var lg = new LinearGradientBrush(swatch, Ui.ToColor(v.Sun), Ui.ToColor(v.FogColour), 90f)) g.FillRectangle(lg, swatch);
            // fog band at the bottom, sun and ambient dots
            using (var fog = new LinearGradientBrush(new Rectangle(swatch.X, swatch.Bottom - 18, swatch.Width, 18), Color.FromArgb(0, Ui.ToColor(v.FogColour)), Color.FromArgb((int)(255 * Math.Clamp(v.FogMax, 0, 1)), Ui.ToColor(v.FogColour)), 90f))
                g.FillRectangle(fog, swatch.X, swatch.Bottom - 18, swatch.Width, 18);
            using (var sun = new SolidBrush(Ui.ToColor(v.Sun))) g.FillEllipse(sun, swatch.Right - 18, swatch.Y + 4, 12, 12);
            using (var amb = new SolidBrush(Ui.ToColor(v.Ambient))) g.FillEllipse(amb, swatch.X + 4, swatch.Bottom - 14, 10, 10);
            using (var pen = new Pen(Color.FromArgb(140, 255, 255, 255))) { g.DrawEllipse(pen, swatch.Right - 18, swatch.Y + 4, 12, 12); g.DrawEllipse(pen, swatch.X + 4, swatch.Bottom - 14, 10, 10); }
            title = t.Display;
            sub = $"fog {v.FogStart:0}–{v.FogEnd:0} · {v.FogMax * 100:0}%" + (dome != null ? $"\nsky {dome.ShortName}" : "") 
                + (t.CurrentAct ? " · open act" : "") + (t.SharedWith.Count > 0 ? " · shared" : "");
            dirty = _edited.Contains(t);
        }
        else
        {
            using (var lg = new LinearGradientBrush(swatch, Color.FromArgb(120, 140, 170), Color.FromArgb(220, 228, 238), 90f)) g.FillRectangle(lg, swatch);
            var rnd = new Random(7);
            using var flake = new SolidBrush(Color.White);
            for (int i = 0; i < 26; i++) { int sz = rnd.Next(2, 5); g.FillEllipse(flake, swatch.X + rnd.Next(swatch.Width - 4), swatch.Y + rnd.Next(swatch.Height - 4), sz, sz); }
            title = "Weather";
            sub = "falling snow";
        }
        using (var pen = new Pen(Color.FromArgb(70, 0, 0, 0))) g.DrawRectangle(pen, swatch);
        TextRenderer.DrawText(g, title + (dirty ? "  •" : ""), new Font("Segoe UI Semibold", 10f), new Point(swatch.Right + 10, r.Y + 8), dirty ? Color.FromArgb(190, 95, 10) : Color.FromArgb(30, 30, 36));
        TextRenderer.DrawText(g, sub, Ui.Small, new Rectangle(swatch.Right + 10, r.Y + 28, r.Width - swatch.Right - 12, r.Height - 28), Ui.Subtle, TextFormatFlags.WordBreak);
    }

    // ------------------------------------------------------------------ scripts (NBModStudio --atmos-*), same paths as the controls

    public string ScriptSelect(string q)
    {
        for (int i = 0; i < _list.Items.Count; i++)
        {
            var it = _list.Items[i];
            string n = it is TimeOfDay t ? t.Display : WeatherItem;
            if (n.Equals(q, StringComparison.OrdinalIgnoreCase) || (it is TimeOfDay tt && tt.Light.ShortName.Equals(q, StringComparison.OrdinalIgnoreCase))) { _list.SelectedIndex = i; return n; }
        }
        throw new InvalidOperationException("no time of day / weather entry " + q);
    }

    /// <summary>Sets one field of the selected entry like typing into it: ambient, sun, intensity, fogcolour, fogstart, fogend, fogmax, sky,
    /// or (weather) biglarge… see the switch.</summary>
    public void ScriptSet(string field, string value)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        float F() => float.Parse(value, inv);
        uint H() => Convert.ToUInt32(value.TrimStart('#'), 16);
        switch (field.ToLowerInvariant())
        {
            case "ambient": _ambient.SetByUser(H()); break;
            case "sun": _sun.SetByUser(H()); break;
            case "intensity": _intensity.SetByUser(F()); break;
            case "fogcolour": case "fogcolor": _fogColour.SetByUser(H()); break;
            case "fogstart": _fogStart.SetByUser(F()); break;
            case "fogend": _fogEnd.SetByUser(F()); break;
            case "fogmax": _fogMax.SetByUser(F()); break;
            case "sunelev": _sunElev.SetByUser(F()); break;
            case "sunazim": _sunAzim.SetByUser(F()); break;
            case "sundial": { var xy = value.Split(','); _sunDial.DragTo(new Point(int.Parse(xy[0]), int.Parse(xy[1]))); _gestureKey = null; break; }
            case "fillon": _fillOn.Checked = value is "on" or "1" or "true"; break;
            case "fillcolour": case "fillcolor": _fillColour.SetByUser(H()); break;
            case "fillintensity": _fillIntensity.SetByUser(F()); break;
            case "fillelev": _fillElev.SetByUser(F()); break;
            case "fillazim": _fillAzim.SetByUser(F()); break;
            case "pickambient": case "picksun": case "pickfog": case "pickfill":
            {
                // "C1,C2,…;ok|cancel[;shot.png]": a pick through the picker window, previewing every step
                var parts = value.Split(';');
                var cf = field.ToLowerInvariant() switch { "picksun" => _sun, "pickfog" => _fogColour, "pickfill" => _fillColour, _ => _ambient };
                var steps = parts[0].Split(',').Select(x => Convert.ToUInt32(x.Trim().TrimStart('#'), 16)).ToList();
                int frames0 = _framesPushed, k = 0;
                string? stepShots = parts.Length > 3 ? parts[3] : null;   // prefix: the 3D preview after every step
                cf.ScriptPick(steps, parts.Length > 1 && parts[1] == "ok", parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null, () =>
                {
                    System.Threading.Thread.Sleep(40); Application.DoEvents();
                    if (stepShots != null && PreviewShown) using (var b = _view!.Capture()) b.Save($"{stepShots}{k++}.png");
                });
                _lastPick = $"pick of {steps.Count} colours drew {_framesPushed - frames0} preview frames";
                break;
            }
            case "undo": Undo(); break;
            case "redo": Redo(); break;
            case "preview": _previewOn.Checked = value is "on" or "1" or "true"; break;
            case "state": break;   // only logs the state (--atmos itself selects the tab)
            case "sky":
            {
                int i = _at!.Domes.FindIndex(d => d.ShortName.Equals(value, StringComparison.OrdinalIgnoreCase));
                if (i < 0) throw new InvalidOperationException("no skydome " + value);
                _dome.SelectedIndex = i; break;
            }
            case "emitlarge": _emitLarge.SetByUser(F()); break;
            case "emitsmall": _emitSmall.SetByUser(F()); break;
            case "sizelarge": _sizeLarge.SetByUser(F()); break;
            case "sizesmall": _sizeSmall.SetByUser(F()); break;
            case "follow": _follow.Checked = value is "on" or "1" or "true"; break;
            default: throw new InvalidOperationException("unknown field " + field);
        }
    }

    public string ScriptState() => _cur != null ? $"{_cur.Display}: {_cur.Light.Values}, sun elevation {Deg(_cur.Light.Values.SunElevation):0.#}° azimuth {Deg(_cur.Light.Values.SunAzimuth):0.#}°, sky {DomeName(_cur.DomeId)}{(_edited.Contains(_cur) ? " (edited)" : "")}"
        + $"; undo {_undoStack.Count} redo {_redoStack.Count}{(_lastPick != null ? "; " + _lastPick : "")}; preview {(PreviewShown ? $"shown, view light {_view!.LightingName}, sun dir {_view.CurrentLighting.SunDirection}, mode {_view.ViewMode}" : "hidden")} (tab {_tabs?.SelectedTab?.Text})" : "weather: " + _snowState.Text;

    /// <summary>Sky texture stems of the selected time of day (for scripts).</summary>
    public IEnumerable<string> SkyStems => _at?.Domes.FirstOrDefault(d => d.Id == _cur?.DomeId)?.Textures ?? Enumerable.Empty<string>();

    public void SaveShot(string file)
    {
        Application.DoEvents();
        using var b = new Bitmap(Width, Height); DrawToBitmap(b, new Rectangle(0, 0, Width, Height));
        // the GL view does not draw itself into DrawToBitmap: paste a capture of it where it sits
        if (PreviewShown && _view!.Width > 0 && _view.Height > 0)
            using (var g = Graphics.FromImage(b)) using (var v = _view.Capture())
                g.DrawImage(v, PointToClient(_view.PointToScreen(Point.Empty)));
        b.Save(file);
    }

    /// <summary>Scripts: moves a slider from <paramref name="from"/> to <paramref name="to"/> in <paramref name="steps"/>
    /// steps the way a drag does (each step one edit through the throttled preview, the UI pumped in between) and reports
    /// the time per step and the frames drawn.</summary>
    public string ScriptDrag(string field, float from, float to, int steps)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        int frames0 = _framesPushed;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i <= steps; i++)
        {
            ScriptSet(field, (from + (to - from) * i / steps).ToString(inv));
            Application.DoEvents();
        }
        System.Threading.Thread.Sleep(30); Application.DoEvents();
        double ms = sw.Elapsed.TotalMilliseconds;
        _gestureKey = null;
        return $"{field} {from}->{to} in {steps} steps: {ms / (steps + 1):F1} ms per step, {_framesPushed - frames0} preview frames ({1000.0 * (_framesPushed - frames0) / ms:F0} per second)";
    }

    // ------------------------------------------------------------------ 3D preview

    /// <summary>Lets the tab show the 3D view <paramref name="view"/> while it is the selected page of <paramref name="tabs"/>
    /// (the view goes back to its own parent when another tab is selected).</summary>
    public void AttachViewport(SceneViewport view, TabControl tabs)
    {
        _view = view; _tabs = tabs; _viewHome = view.Parent;
        tabs.SelectedIndexChanged += (_, _) => SyncPreview();
        view.LightApplied += OnViewLight;
        view.ViewModeChanged += _ => UpdatePreviewCaption();
    }

    bool PreviewShown => _view != null && _view.Parent == _previewHost;

    bool PreviewWanted => _view != null && _tabs != null && _previewOn.Checked && _at != null && _at.Times.Count > 0 && _view.Scene != null
        && (_view.Scene.Bundle & 0xFFFFFF) == _world && _tabs.SelectedTab != null && _tabs.SelectedTab.Contains(this);

    /// <summary>Moves the 3D view into the preview area or back to its tab.</summary>
    void SyncPreview()
    {
        if (_view == null || _viewHome == null) return;
        bool want = PreviewWanted;
        if (want && _view.Parent != _previewHost)
        {
            LayoutPreview();
            _previewHost.Controls.Add(_view);
            _view.BringToFront();
            _modeBefore = _view.ViewMode;
            if (_view.ViewMode != ViewMode.Rendered) _view.ViewMode = ViewMode.Rendered;
            // a clean picture: marker boxes and path lines off while previewing (the Markers box above the view)
            _hidMarkers = _view.ShowMarkers; _hidPaths = _view.ShowPaths;
            _view.ShowMarkers = _view.ShowPaths = _previewMarkers.Checked;
            PushPreview(true);
        }
        else if (!want && _view.Parent == _previewHost)
        {
            _viewHome.Controls.Add(_view);
            // back to the shading the user had in the 3D View tab, unless they switched it in the preview
            if (_modeBefore is { } m && _view.ViewMode == ViewMode.Rendered) _view.ViewMode = m;
            _modeBefore = null;
            _view.ShowMarkers = _hidMarkers; _view.ShowPaths = _hidPaths;   // the 3D View tab's own setting
            _view.Refresh3D();
        }
        _previewEmpty.Visible = !PreviewShown;
        _subtitle.Visible = !PreviewShown;   // room for the view
        UpdatePreviewCaption();
    }

    /// <summary>Shows the edited time of day in the 3D view (at most one frame per 15 ms while a slider is dragged; the last
    /// value always gets drawn).</summary>
    void PushPreview(bool now = false)
    {
        if (_view?.Scene == null || _cur == null || (_view.Scene.Bundle & 0xFFFFFF) != _world) return;
        // at most one frame per 15 ms: a value that arrives sooner waits for the timer (the last one is always drawn)
        if (!now && _sinceFrame.ElapsedMilliseconds < 15) { _previewPending = true; if (!_previewThrottle.Enabled) _previewThrottle.Start(); return; }
        _previewPending = false;
        _pushing = true;
        try { _view.PreviewAtmosphere(_cur.Light.Name, _cur.Light.Data, _cur.DomeOffset >= 0 ? _cur.DomeId : 0); _previewScene = _view.Scene; }
        catch (Exception e) { Log?.Invoke("Atmosphere preview: " + e.Message); }
        finally { _pushing = false; }
        _previewThrottle.Stop();
        _sinceFrame.Restart();
        if (PreviewShown) { _view.RenderNow(); _framesPushed++; }
        UpdatePreviewCaption();
    }
    int _framesPushed;

    /// <summary>The 3D view changed its light by itself: a world (re)loaded (show the time of day being edited again) or
    /// the light bar was clicked in the preview (select that time of day here).</summary>
    void OnViewLight(string name)
    {
        if (_pushing || _at == null || !IsHandleCreated) return;
        BeginInvoke(() =>
        {
            if (_view == null || _at == null) return;
            if (_view.Scene != _previewScene) { if (PreviewShown || _at.Dirty) PushPreview(true); return; }
            if (!PreviewShown) return;
            var t = _at.Times.FirstOrDefault(x => x.Light.Name == "aid_script_banjox_lightsetup_" + name);
            if (t != null && t != _cur) _list.SelectedItem = t;
        });
    }

    void UpdatePreviewCaption()
    {
        if (!PreviewShown || _cur == null) { _previewCaption.Text = "3D preview"; return; }
        var v = _cur.Light.Values;
        _previewCaption.Text = $"{_cur.Display}{(_edited.Contains(_cur) ? " (unsaved edits)" : "")}  ·  {_view!.ViewMode}  ·  sun {Deg(v.SunElevation):0}° up, azimuth {Deg(v.SunAzimuth):0}°  ·  camera of the 3D View tab (right-drag + WASD to look around)";
    }

    /// <summary>Preview beside the settings when the tab is wide, above them when it is narrow; keeps the user's split.</summary>
    void LayoutPreview()
    {
        if (_outer.Panel1Collapsed || Width < 50 || Height < 50) return;
        _layingOut = true;
        try
        {
            bool side = Width >= 1000;
            var o = side ? Orientation.Vertical : Orientation.Horizontal;
            if (_outer.Orientation != o) _outer.Orientation = o;
            int total = side ? _outer.Width : _outer.Height;
            int min1 = side ? 240 : 140, min2 = side ? 520 : 220;
            if (total < min1 + min2 + _outer.SplitterWidth) return;
            int want = Math.Clamp((int)(total * (side ? _sideRatio : _stackRatio)), min1, total - min2 - _outer.SplitterWidth);
            if (Math.Abs(_outer.SplitterDistance - want) > 2) _outer.SplitterDistance = want;
        }
        catch (Exception) { }
        finally { _layingOut = false; }
        _sunDial.CameraYaw = _view?.Scene != null ? _view.LookAngles.Yaw : null;
    }

    static string PrefsPath => Path.Combine(NB.Core.Project.ProjectRegistry.DataDir, "atmosphere.json");
    void LoadPrefs()
    {
        try
        {
            if (!File.Exists(PrefsPath)) return;
            var d = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(PrefsPath));
            if (d == null) return;
            if (d.TryGetValue("Preview", out var on)) _previewOn.Checked = on != 0;
            if (d.TryGetValue("SideRatio", out var sr) && sr is > 0.1 and < 0.9) _sideRatio = sr;
            if (d.TryGetValue("StackRatio", out var st) && st is > 0.1 and < 0.9) _stackRatio = st;
        }
        catch (Exception) { }
    }
    void SavePrefs()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PrefsPath)!);
            File.WriteAllText(PrefsPath, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, double> { ["Preview"] = _previewOn.Checked ? 1 : 0, ["SideRatio"] = _sideRatio, ["StackRatio"] = _stackRatio }));
        }
        catch (Exception) { }
    }

    // ------------------------------------------------------------------ undo (this tab's unsaved edits)

    /// <summary>The bytes of every light setup and time-of-day script before a change, and which entries were edited.</summary>
    sealed record Snap(TimeOfDay? Select, List<(byte[] Live, byte[] Copy)> Bytes, HashSet<TimeOfDay> Edited);
    readonly List<Snap> _undoStack = new(), _redoStack = new();
    string? _gestureKey; DateTime _gestureAt;
    bool _picking; int _undoBeforePick; List<Snap> _redoBeforePick = new();
    string? _lastPick;

    Snap TakeSnap()
    {
        var arrays = new List<(byte[], byte[])>();
        var seen = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
        foreach (var t in _at!.Times)
            foreach (var b in new[] { t.Light.Data, t.PhaseData })
                if (b != null && seen.Add(b)) arrays.Add((b, (byte[])b.Clone()));
        return new Snap(_cur, arrays, new HashSet<TimeOfDay>(_edited));
    }

    /// <summary>Records the state before an edit; a run of edits of the same control (a slider drag, typing) within 0.8 s
    /// is one undo step.</summary>
    void BeginEdit(string key)
    {
        if (_at == null) return;
        var now = DateTime.UtcNow;
        // a colour pick holds its step open until the picker closes, however long it takes
        bool same = key == _gestureKey && ((now - _gestureAt).TotalMilliseconds < 800 || _picking);
        _gestureKey = key; _gestureAt = now;
        if (same) return;
        _undoStack.Add(TakeSnap());
        if (_undoStack.Count > 200) _undoStack.RemoveAt(0);
        _redoStack.Clear();
    }

    /// <summary>Another editor saved bytes inside a script this tab snapshots (the music tables of the time-of-day scripts,
    /// MainForm.Music.cs): the saved state and this tab's undo snapshots take them, so Discard or Ctrl+Z here keeps them.</summary>
    public void NoteExternalWrite(byte[] live, int offset, int length)
    {
        _at?.NoteExternalWrite(live, offset, length);
        foreach (var s in _undoStack.Concat(_redoStack))
            foreach (var (l, copy) in s.Bytes)
                if (ReferenceEquals(l, live) && offset >= 0 && offset + length <= copy.Length && copy.Length == live.Length) Buffer.BlockCopy(live, offset, copy, offset, length);
    }

    public void Undo() => Step(_undoStack, _redoStack, "undone");
    public void Redo() => Step(_redoStack, _undoStack, "redone");

    void Step(List<Snap> from, List<Snap> to, string what)
    {
        if (_at == null || from.Count == 0) return;
        var s = from[^1]; from.RemoveAt(from.Count - 1);
        to.Add(TakeSnap());
        foreach (var (live, copy) in s.Bytes)
        {
            if (live.AsSpan().SequenceEqual(copy)) continue;
            Buffer.BlockCopy(copy, 0, live, 0, copy.Length);
            foreach (var t in _at.Times)
            {
                if (ReferenceEquals(t.Light.Data, live)) _at.MarkDirty(t.Light.Bundle);
                if (ReferenceEquals(t.PhaseData, live)) _at.MarkDirty(t.PhaseBundle);
            }
        }
        _edited.Clear(); _edited.UnionWith(s.Edited);
        _at.RefreshDirty();   // back at the saved state: nothing to save
        _gestureKey = null;
        Log?.Invoke($"Atmosphere: change {what} (unsaved)");
        if (s.Select != null && s.Select != _cur && _list.Items.Contains(s.Select)) _list.SelectedItem = s.Select;   // binds and previews
        else if (_cur != null) { Bind(_cur); PushPreview(true); if (_liveOn.Checked) PushLive(); }
        _list.Invalidate(); UpdateButtons();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Ctrl+Z / Ctrl+Y undo this tab's edits while the focus is in the settings (in the 3D preview they stay the
        // editor's world undo; a text box being typed in keeps its own)
        if ((msg.Msg == 0x100 || msg.Msg == 0x104) && !_previewHost.ContainsFocus && !Typing())
        {
            if (keyData == (Keys.Control | Keys.Z) && _undoStack.Count > 0) { Undo(); return true; }
            if ((keyData == (Keys.Control | Keys.Y) || keyData == (Keys.Control | Keys.Shift | Keys.Z)) && _redoStack.Count > 0) { Redo(); return true; }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    bool Typing()
    {
        Control? c = ActiveControl;
        while (c is ContainerControl cc && cc.ActiveControl != null) c = cc.ActiveControl;
        return c is TextBoxBase tb && tb.Modified && tb.CanUndo;
    }

    protected override void Dispose(bool disposing)
    {
        // closing while the preview shows: the 3D view keeps the shading it had in its own tab next time
        if (disposing && _modeBefore is { } m && _view != null && _view.ViewMode == ViewMode.Rendered) try { _view.ViewMode = m; } catch (Exception) { }
        if (disposing) { _livePush.Dispose(); _previewThrottle.Dispose(); _x?.Dispose(); foreach (var b in _thumbs.Values) b?.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>Fog at a glance: distance from the camera (left) outwards, the fog's opacity rising from "starts at" to
/// "full at", drawn over a neutral scene grey.</summary>
sealed class FogPreview : Control
{
    LightValues? _v;
    public FogPreview() { DoubleBuffered = true; ResizeRedraw = true; }
    public void Set(LightValues v) { _v = v; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.White);
        if (_v == null) return;
        var bar = new Rectangle(0, 4, Width - 1, Height - 26);
        float range = Math.Max(100, Math.Max(_v.FogEnd, _v.FogStart) * 1.25f);
        // scene: ambient-lit grey ground tinted by the sun
        var amb = Ui.ToColor(_v.Ambient); var sun = Ui.ToColor(_v.Sun);
        var scene = Color.FromArgb(Math.Min(255, amb.R + sun.R / 3), Math.Min(255, amb.G + sun.G / 3), Math.Min(255, amb.B + sun.B / 3));
        using (var sb = new SolidBrush(scene)) g.FillRectangle(sb, bar);
        var fog = Ui.ToColor(_v.FogColour);
        for (int x = 0; x < bar.Width; x++)
        {
            float d = range * x / bar.Width;
            float t = _v.FogEnd > _v.FogStart ? Math.Clamp((d - _v.FogStart) / (_v.FogEnd - _v.FogStart), 0, 1) : d >= _v.FogStart ? 1 : 0;
            int a = (int)(255 * t * Math.Clamp(_v.FogMax, 0, 1));
            using var p = new Pen(Color.FromArgb(a, fog));
            g.DrawLine(p, bar.X + x, bar.Y, bar.X + x, bar.Bottom);
        }
        using (var pen = new Pen(Color.FromArgb(90, 0, 0, 0))) g.DrawRectangle(pen, bar);
        void Tick(float d, string label)
        {
            int x = bar.X + (int)(bar.Width * d / range);
            using var pen = new Pen(Color.FromArgb(160, 30, 30, 36)) { DashStyle = DashStyle.Dash };
            g.DrawLine(pen, x, bar.Y, x, bar.Bottom + 3);
            TextRenderer.DrawText(g, label, Ui.Small, new Point(Math.Min(x - 2, Width - 90), bar.Bottom + 4), Ui.Subtle);
        }
        Tick(_v.FogStart, $"starts {_v.FogStart:0}");
        Tick(_v.FogEnd, $"full {_v.FogEnd:0} ({_v.FogMax * 100:0}%)");
        TextRenderer.DrawText(g, "camera", Ui.Small, new Point(0, bar.Bottom + 4), Ui.Subtle);
    }
}
