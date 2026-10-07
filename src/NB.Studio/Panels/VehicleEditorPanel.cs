using System.Globalization;
using NB.Core.Project;
using NB.Core.Textures;
using NB.Core.Vehicles;
using NB.Studio.Viewport;

namespace NB.Studio.Panels;

/// <summary>
/// The Vehicle Editor (center tab): builds and edits Nuts &amp; Bolts vehicles.
/// <list type="bullet">
/// <item>Opens Xbox 360 vehicle saves (packages 0x0000000N or the content file extracted from one — told apart by their
/// first bytes), bare blueprints, and the game's own vehicles (aid_vehicle_* of the open act / world, listed with their
/// AI drivers, or any vehicle of the workspace).</item>
/// <item>A file dropped on the editor replaces the vehicle shown; when a game vehicle is open it stays the target, so the
/// dropped vehicle can be finished and saved into the game in its place (Save to Game keeps the asset id).</item>
/// <item>Parts library of every part of the workspace (shipped and modded), place / move / rotate (24 orientations) /
/// delete / copy / paste / multi-select, undo / redo, paint per part (the game's 13 garage colours or any RGB),
/// wheel and propeller settings, action buttons; no part limit, cells 0..255 on every axis.</item>
/// <item>Saves back to the source package (header kept, hashes recomputed), as a new package, a content file, a bare
/// blueprint, into the shared vehicle saves (Test in Xenia / NB Multiplayer), or into the game (workspace bundles,
/// undoable).</item>
/// </list>
/// </summary>
public sealed class VehicleEditorPanel : UserControl
{
    public Action<string>? Log;
    /// <summary>Bundles loaded by the open world / act (act, world, companions, common), for the pregame list and validation.</summary>
    public Func<IReadOnlyList<uint>?>? WorldBundles;
    public Func<string?>? WorldLabel;
    /// <summary>The shared vehicle saves folder (TestSaves.VaultDir), null when vehicles are kept per workspace.</summary>
    public Func<string?>? VehicleSavesDir;

    Workspace? _ws; AssetIndex? _idx; PartCatalog? _cat; TextureResolver? _tex;
    VehicleDocument _doc = new();
    enum Target { None, File, Game }
    Target _target;
    VehicleFile? _file;
    PregameVehicle? _game;
    static List<VehicleDocument.Part> _clip = new();

    readonly VehicleViewport _view = new() { Dock = DockStyle.Fill };
    readonly ToolStrip _bar = new() { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
    readonly StatusStrip _statusStrip = new();
    readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly ToolStripStatusLabel _source = new() { TextAlign = ContentAlignment.MiddleRight };
    // library
    readonly TextBox _search = new() { Dock = DockStyle.Top, PlaceholderText = "Search parts…" };
    readonly ComboBox _group = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ListView _lib = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
    readonly ImageList _swatches = new() { ImageSize = new Size(14, 14), ColorDepth = ColorDepth.Depth32Bit };
    readonly Label _libInfo = new() { Dock = DockStyle.Bottom, Height = 64, Padding = new Padding(4), BorderStyle = BorderStyle.FixedSingle };
    // properties
    readonly Label _selInfo = new() { AutoSize = true, MaximumSize = new Size(255, 0) };
    readonly ComboBox _partType = new() { Width = 250, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly NumericUpDown _px = Num(), _py = Num(), _pz = Num();
    readonly ComboBox _orient = new() { Width = 170, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly CheckBox _painted = new() { Text = "Painted (off: the part's own colour)", AutoSize = true };
    readonly Button _colourBtn = new() { Text = "Colour…", Width = 90 };
    readonly Panel _colourShow = new() { Width = 40, Height = 23, BorderStyle = BorderStyle.FixedSingle };
    readonly FlowLayoutPanel _palette = new() { Width = 255, Height = 50, FlowDirection = FlowDirection.LeftToRight };
    readonly ComboBox _setting = new() { Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly CheckBox[] _act1 = new CheckBox[3], _act2 = new CheckBox[3];
    readonly NumericUpDown _groupByte = new() { Width = 60, Minimum = 0, Maximum = 255 };
    readonly TextBox _name = new() { Width = 200, MaxLength = Blueprint.MaxNameChars };
    readonly Label _vehInfo = new() { AutoSize = true, MaximumSize = new Size(255, 0) };
    readonly ListBox _issues = new() { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true };
    readonly ToolStripButton _tSel, _tPlace, _tPaint, _undoBtn, _redoBtn, _saveGame;
    readonly ToolStripDropDownButton _pregameBtn;
    bool _syncing;
    SplitContainer _inner = null!, _outer = null!;

    static NumericUpDown Num() => new() { Width = 50, Minimum = -255, Maximum = 510 };

    public VehicleEditorPanel()
    {
        // ---- toolbar
        _bar.Items.Add(new ToolStripButton("New", null, (_, _) => New()) { ToolTipText = "Start an empty vehicle" });
        _bar.Items.Add(new ToolStripButton("Open…", null, (_, _) => OpenDialog()) { ToolTipText = "Open an Xbox 360 vehicle save (package 0x0000000N or its extracted content file) or a blueprint .bin. You can also drop files here." });
        _pregameBtn = new ToolStripDropDownButton("Game Vehicles") { ToolTipText = "The game's own vehicles: those of the open world / Act (AI racers with their drivers, challenge vehicles) or any vehicle of the workspace" };
        _pregameBtn.DropDownOpening += (_, _) => FillPregameMenu();
        _pregameBtn.DropDownItems.Add("(open a workspace)");
        _bar.Items.Add(_pregameBtn);
        _bar.Items.Add(new ToolStripSeparator());
        _bar.Items.Add(new ToolStripButton("Save", null, (_, _) => Save()) { ToolTipText = "Save back where the vehicle came from: its 360 package / content file / blueprint, or into the game (game vehicles)" });
        var saveAs = new ToolStripDropDownButton("Save As");
        saveAs.DropDownItems.Add("Xbox 360 Package (0x0000000N)…", null, (_, _) => SaveAsPackage());
        saveAs.DropDownItems.Add("Content File (as Xenia keeps it)…", null, (_, _) => SaveAsFile(VehicleFileKind.Content));
        saveAs.DropDownItems.Add("Blueprint (.bin, game asset data)…", null, (_, _) => SaveAsFile(VehicleFileKind.Blueprint));
        saveAs.DropDownItems.Add(new ToolStripSeparator());
        saveAs.DropDownItems.Add("To My Vehicle Saves (Test in Xenia / NB Multiplayer)", null, (_, _) => SaveToVault());
        saveAs.DropDownItems.Add("Into Xenia Content Folder (a profile's saves)…", null, (_, _) => SaveIntoXenia());
        _bar.Items.Add(saveAs);
        _saveGame = new ToolStripButton("Save to Game", null, (_, _) => SaveToGame()) { Enabled = false, ToolTipText = "Write this vehicle into the game in place of the open game vehicle (every bundle that holds it; Edit > Undo restores it)" };
        _bar.Items.Add(_saveGame);
        _bar.Items.Add(new ToolStripSeparator());
        _undoBtn = new ToolStripButton("Undo", null, (_, _) => DoUndo()) { ToolTipText = "Ctrl+Z" };
        _redoBtn = new ToolStripButton("Redo", null, (_, _) => DoRedo()) { ToolTipText = "Ctrl+Y" };
        _bar.Items.Add(_undoBtn); _bar.Items.Add(_redoBtn);
        _bar.Items.Add(new ToolStripSeparator());
        _tSel = new ToolStripButton("Select", null, (_, _) => SetTool(VehicleTool.Select)) { CheckOnClick = false, ToolTipText = "Click parts to select (Ctrl/Shift: add); drag selected parts to move them (Shift: up/down). S" };
        _tPlace = new ToolStripButton("Place", null, (_, _) => SetTool(VehicleTool.Place)) { ToolTipText = "Place the part chosen in the library on the face under the mouse. P / Esc" };
        _tPaint = new ToolStripButton("Paint", null, (_, _) => SetTool(VehicleTool.Paint)) { ToolTipText = "Click parts to paint them (Alt+click: pick a part's colour). B" };
        _bar.Items.Add(_tSel); _bar.Items.Add(_tPlace); _bar.Items.Add(_tPaint);
        _bar.Items.Add(new ToolStripSeparator());
        foreach (var (t, ax) in new[] { ("⟲X", 0), ("⟲Y", 1), ("⟲Z", 2) })
            _bar.Items.Add(new ToolStripButton(t, null, (_, _) => Rotate(ax, 1)) { ToolTipText = $"Rotate 90° about {"XYZ"[ax]} (key {"XYZ"[ax]}; Shift: the other way)" });
        _bar.Items.Add(new ToolStripButton("Delete", null, (_, _) => DeleteSel()) { ToolTipText = "Del" });
        _bar.Items.Add(new ToolStripButton("Copy", null, (_, _) => CopySel()) { ToolTipText = "Ctrl+C" });
        _bar.Items.Add(new ToolStripButton("Paste", null, (_, _) => Paste()) { ToolTipText = "Ctrl+V: pastes next to the copied parts and selects them" });
        _bar.Items.Add(new ToolStripSeparator());
        _bar.Items.Add(new ToolStripButton("Frame", null, (_, _) => _view.FrameAll()) { ToolTipText = "F: show the whole vehicle" });
        var panels = new ToolStripButton("Panels") { Checked = true, CheckOnClick = true, ToolTipText = "Show the parts library and the properties (off: the 3D view takes the whole tab)" };
        panels.CheckedChanged += (_, _) => _outer.Panel1Collapsed = !panels.Checked;
        _bar.Items.Add(panels);
        var hz = new ToolStripButton("Hazards") { Checked = true, CheckOnClick = true, ToolTipText = "Mark parts that are not attached to the vehicle (orange, with the garage's hazard sign); selection and the part being placed show green = attached, orange = floating, red = blocked" };
        hz.CheckedChanged += (_, _) => { _view.ShowHazards = hz.Checked; _view.Redraw(); };
        _bar.Items.Add(hz);
        var grid = new ToolStripButton("Grid") { Checked = true, CheckOnClick = true };
        grid.CheckedChanged += (_, _) => { _view.ShowGrid = grid.Checked; _view.Redraw(); };
        _bar.Items.Add(grid);

        // ---- library (left)
        var left = new Panel { Dock = DockStyle.Fill };
        _lib.Columns.Add("Part", 125); _lib.Columns.Add("Group", 60); _lib.Columns.Add("kg", 35);
        _lib.SmallImageList = _swatches;
        left.Controls.Add(_lib); left.Controls.Add(_libInfo); left.Controls.Add(_group); left.Controls.Add(_search);
        _search.TextChanged += (_, _) => FillLibrary();
        _group.SelectedIndexChanged += (_, _) => FillLibrary();
        _lib.SelectedIndexChanged += (_, _) => LibrarySelected();
        _lib.DoubleClick += (_, _) => { if (LibPart() != null) SetTool(VehicleTool.Place); };

        // ---- properties (right)
        var props = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(4) };
        Label H(string t) => new() { Text = t, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 8, 0, 2) };
        FlowLayoutPanel Row(params Control[] cs) { var r = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0) }; r.Controls.AddRange(cs); return r; }
        props.Controls.Add(H("Vehicle"));
        props.Controls.Add(Row(new Label { Text = "Name", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, _name));
        props.Controls.Add(_vehInfo);
        props.Controls.Add(H("Selected parts"));
        props.Controls.Add(_selInfo);
        props.Controls.Add(new Label { Text = "Part type", AutoSize = true });
        props.Controls.Add(_partType);
        props.Controls.Add(Row(new Label { Text = "Cell X", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, _px, new Label { Text = "Y", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, _py, new Label { Text = "Z", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, _pz));
        props.Controls.Add(Row(new Label { Text = "Orientation", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, _orient));
        props.Controls.Add(H("Paint"));
        props.Controls.Add(_painted);
        props.Controls.Add(Row(_colourShow, _colourBtn, MkBtn("Default", () => PaintSel(null), "Back to the part's own colour (unpainted)")));
        props.Controls.Add(_palette);
        props.Controls.Add(H("Settings"));
        props.Controls.Add(Row(new Label { Text = "Wheels / props", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, _setting));
        var a1 = new FlowLayoutPanel { AutoSize = true }; var a2 = new FlowLayoutPanel { AutoSize = true };
        for (int i = 0; i < 3; i++)
        {
            int k = i;
            _act1[i] = new CheckBox { Text = "ABX"[i].ToString(), AutoSize = true }; _act2[i] = new CheckBox { Text = "ABX"[i].ToString(), AutoSize = true };
            _act1[i].CheckedChanged += (_, _) => { if (!_syncing) SetAction(false, k, _act1[k].Checked); };
            _act2[i].CheckedChanged += (_, _) => { if (!_syncing) SetAction(true, k, _act2[k].Checked); };
            a1.Controls.Add(_act1[i]); a2.Controls.Add(_act2[i]);
        }
        props.Controls.Add(Row(new Label { Text = "Buttons", AutoSize = false, Padding = new Padding(0, 5, 0, 0), Width = 80 }, a1));
        props.Controls.Add(Row(new Label { Text = "2nd action", AutoSize = false, Padding = new Padding(0, 5, 0, 0), Width = 80 }, a2));
        props.Controls.Add(Row(new Label { Text = "Group byte (+3)", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, _groupByte));
        // layout: one side column (parts library above, properties below) | the 3D view with the checks under it
        var side = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        side.Panel1.Controls.Add(left);
        side.Panel2.Controls.Add(props);
        var issuesBox = new GroupBox { Text = "Checks", Dock = DockStyle.Bottom, Height = 92 };
        issuesBox.Controls.Add(_issues);
        var viewHost = new Panel { Dock = DockStyle.Fill };
        viewHost.Controls.Add(_view); viewHost.Controls.Add(issuesBox);
        var outer = _outer = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
        outer.Panel1.Controls.Add(side);
        outer.Panel2.Controls.Add(viewHost);
        _inner = side;
        _statusStrip.Items.Add(_status); _statusStrip.Items.Add(_source);
        Controls.Add(outer); Controls.Add(_bar); Controls.Add(_statusStrip);
        HandleCreated += (_, _) => BeginInvoke(() =>
        {
            try { outer.SplitterDistance = 270; side.SplitterDistance = Math.Max(150, side.Height * 2 / 5); } catch { }
        });

        // ---- events
        // the name is one undo step when the box is left (or Enter), not one per letter
        void CommitName() { if (_syncing || _name.Text == _doc.Name) return; _doc.Begin("rename"); _doc.Name = _name.Text; _doc.Commit(); }
        _name.Leave += (_, _) => CommitName();
        _name.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { CommitName(); e.SuppressKeyPress = true; } };
        _partType.SelectedIndexChanged += (_, _) => { if (!_syncing && _partType.SelectedItem is PartInfo pi) Edit("change part type", p => { p.B.Part = pi.Id; p.B.Category = (byte)(pi.Category + 1); if (p.B.Painted == 0) p.B.Paint = pi.DefaultPaint; if (pi.Settings.All(s => s.Value != p.B.Setting)) p.B.Setting = 0; }); };
        foreach (var (n, k) in new[] { (_px, 0), (_py, 1), (_pz, 2) })
            n.ValueChanged += (_, _) => { if (_syncing || _doc.Selection.Count != 1) return; int v = (int)n.Value; Edit("move part", p => { if (k == 0) p.X = v; else if (k == 1) p.Y = v; else p.Z = v; }); };
        for (int i = 0; i < Orientations.All.Length; i++) _orient.Items.Add(OrientName(i));
        _orient.SelectedIndexChanged += (_, _) => { if (!_syncing && _orient.SelectedIndex >= 0) { int o = _orient.SelectedIndex; Edit("rotate part", p => p.B.Orientation = o); } };
        _painted.CheckedChanged += (_, _) => { if (!_syncing) Edit(_painted.Checked ? "paint" : "unpaint", p => { if (_painted.Checked) { p.B.Painted = 1; } else { p.B.Painted = 0; p.B.Paint = _cat?[p.B.Part]?.DefaultPaint ?? p.B.Paint; } }); };
        _colourBtn.Click += (_, _) =>
        {
            using var cd = new ColorDialog { FullOpen = true, Color = Rgb(_view.PaintColour), AnyColor = true };
            if (cd.ShowDialog(this) == DialogResult.OK) { _view.PaintColour = Rgba(cd.Color); _colourShow.BackColor = cd.Color; if (_doc.Selection.Count > 0) PaintSel(_view.PaintColour); }
        };
        _colourShow.BackColor = Rgb(_view.PaintColour);
        _setting.SelectedIndexChanged += (_, _) => { if (!_syncing && _setting.SelectedItem is SettingItem si) Edit("part setting", p => { if (_cat?[p.B.Part]?.Settings.Count > 0) p.B.Setting = si.Value; }); };
        _groupByte.ValueChanged += (_, _) => { if (!_syncing) { byte g = (byte)_groupByte.Value; Edit("group byte", p => p.B.Group = g); } };
        _issues.SelectedIndexChanged += (_, _) => { if (_issues.SelectedItem is IssueItem { Issue.Part: { } p }) { _doc.Selection.Clear(); _doc.Selection.Add(p); SyncSelection(); _view.FocusSelection(); } };

        _view.SelectionChanged += SyncSelection;
        _view.Edited += _ => { SyncSelection(); UpdateVehicleInfo(); };
        _view.Status += s => _status.Text = s;
        _view.KeyPressed += e => { if (HandleKey(e.KeyData, false)) e.Handled = true; };
        _view.PlaceRequested += (x, y, z) => PlaceAt(x, y, z);
        _view.PaintClicked += (p, pick) =>
        {
            if (pick)
            {
                uint c = p.B.Painted != 0 ? p.B.Paint : _cat?[p.B.Part]?.DefaultPaint ?? p.B.Paint;
                _view.PaintColour = c; _colourShow.BackColor = Rgb(c); _status.Text = $"Colour picked: #{c >> 8:X6}"; return;
            }
            _doc.Begin("paint part");
            p.B.Painted = 1; p.B.Paint = _view.PaintColour;
            _doc.Commit();
        };

        // drag & drop of vehicle files anywhere on the editor
        foreach (Control c in new Control[] { this, _view, _lib })
        {
            c.AllowDrop = true;
            c.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
            c.DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] f) BeginInvoke(() => Drop(f)); };
        }
        Attach(new VehicleDocument());
        SetTool(VehicleTool.Select);
        UpdateSourceLabel();
    }

    static Button MkBtn(string t, Action a, string tip)
    {
        var b = new Button { Text = t, AutoSize = true };
        b.Click += (_, _) => a();
        new ToolTip().SetToolTip(b, tip);
        return b;
    }

    sealed record SettingItem(byte Value, string Name) { public override string ToString() => Name; }
    sealed record IssueItem(VehicleDocument.Issue Issue) { public override string ToString() => (Issue.Error ? "✖ " : "⚠ ") + Issue.Text; }

    static string OrientName(int i)
    {
        var e = Orientations.Euler(i);
        static int D(float r) => (int)MathF.Round(r * 180 / MathF.PI);
        return $"#{i}  X {D(e.X)}°, Y {D(e.Y)}°, Z {D(e.Z)}°";
    }

    static Color Rgb(uint rgba) => Color.FromArgb(255, (int)(rgba >> 24), (int)(rgba >> 16 & 0xFF), (int)(rgba >> 8 & 0xFF));
    static uint Rgba(Color c) => (uint)c.R << 24 | (uint)c.G << 16 | (uint)c.B << 8 | 0xFF;

    // ------------------------------------------------------------------ workspace

    public void SetWorkspace(Workspace? ws, AssetIndex? idx)
    {
        if (ReferenceEquals(ws, _ws)) return;
        _ws = ws; _idx = idx; _cat = null; _tex = null;
        _view.Catalog = null; _view.ResetGpu();
        _lib.Items.Clear(); _group.Items.Clear();
        if (_target == Target.Game) { _target = Target.None; _game = null; }
        UpdateSourceLabel();
    }

    /// <summary>Loads the part catalog (first use after a workspace opened).</summary>
    bool EnsureCatalog()
    {
        if (_cat != null) return true;
        if (_ws == null) { _status.Text = "Open a workspace first: the parts' models and colours come from the game files."; return false; }
        UseWaitCursor = true;
        try
        {
            _idx ??= AssetIndex.LoadOrBuild(_ws);
            _cat = PartCatalog.Load(_ws, _idx);
            _tex = new TextureResolver(_ws, _idx, _ws.LoadResident(0x234CEC), 0x234CEC);
            _view.Catalog = _cat;
            _view.TextureSource = n => _tex?.Load(n);
            _palette.Controls.Clear();
            foreach (var (name, rgba) in _cat.Palette)
            {
                var b = new Button { Width = 20, Height = 20, BackColor = Rgb(rgba), FlatStyle = FlatStyle.Flat, Margin = new Padding(1), Tag = rgba };
                new ToolTip().SetToolTip(b, name.Replace("colour_", "") + $" #{rgba >> 8:X6} (garage paint)");
                b.Click += (_, _) => { _view.PaintColour = (uint)b.Tag!; _colourShow.BackColor = b.BackColor; if (_doc.Selection.Count > 0) PaintSel(_view.PaintColour); };
                _palette.Controls.Add(b);
            }
            _swatches.Images.Clear();
            _group.Items.Clear();
            _group.Items.Add("All parts");
            foreach (var g in _cat.Parts.Values.Select(p => p.Group.Length > 0 ? p.Group : "other").Distinct().OrderBy(x => x)) _group.Items.Add(g);
            if (_cat.Parts.Values.Any(p => p.Modded)) _group.Items.Add("Modded parts");
            _group.SelectedIndex = 0;
            _partType.Items.Clear();
            foreach (var p in _cat.Parts.Values.OrderBy(p => p.Name)) _partType.Items.Add(p);
            FillLibrary();
            Log?.Invoke($"Vehicle Editor: {_cat.Parts.Count} parts ({_cat.Parts.Values.Count(p => p.Modded)} modded), {_cat.Palette.Count} garage colours.");
            return true;
        }
        catch (Exception e) { Log?.Invoke("Vehicle Editor: parts could not be loaded: " + e.Message); return false; }
        finally { UseWaitCursor = false; }
    }

    // ------------------------------------------------------------------ library

    PartInfo? LibPart() => _lib.SelectedItems.Count > 0 ? _lib.SelectedItems[0].Tag as PartInfo : null;

    void FillLibrary()
    {
        if (_cat == null) return;
        string q = _search.Text.Trim();
        string g = _group.SelectedItem as string ?? "All parts";
        _lib.BeginUpdate();
        _lib.Items.Clear();
        foreach (var p in _cat.Parts.Values.OrderBy(p => p.Group).ThenBy(p => p.Name))
        {
            if (g == "Modded parts" ? !p.Modded : g != "All parts" && (p.Group.Length > 0 ? p.Group : "other") != g) continue;
            if (q.Length > 0 && !(p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || p.Key.Contains(q, StringComparison.OrdinalIgnoreCase) || p.Class.Contains(q, StringComparison.OrdinalIgnoreCase))) continue;
            string key = p.DefaultPaint.ToString("X8");
            if (!_swatches.Images.ContainsKey(key))
            {
                var bmp = new Bitmap(14, 14);
                using (var gr = Graphics.FromImage(bmp)) { gr.Clear(Rgb(p.DefaultPaint)); gr.DrawRectangle(Pens.Black, 0, 0, 13, 13); }
                _swatches.Images.Add(key, bmp);
            }
            var it = new ListViewItem(p.Name + (p.Modded ? " ★" : "")) { Tag = p, ImageKey = key, ToolTipText = p.Description };
            it.SubItems.Add(p.Group); it.SubItems.Add(p.Weight.ToString("0.#", CultureInfo.InvariantCulture));
            _lib.Items.Add(it);
        }
        _lib.EndUpdate();
    }

    void LibrarySelected()
    {
        var p = LibPart();
        if (p == null) return;
        var (sx, sy, sz) = p.Size;
        _libInfo.Text = $"{p.Name}{(p.Modded ? " (modded)" : "")}: {p.Key}\n{sx}×{sy}×{sz} cells, {p.Weight:0.#} kg, colour {p.ColourName.Replace("colour_", "")}\n{p.Description}";
        _view.PlacePart = p;
        if (_view.Tool == VehicleTool.Place) _view.Redraw();
    }

    // ------------------------------------------------------------------ document

    void Attach(VehicleDocument d)
    {
        _doc = d;
        _view.Document = d;
        d.Changed += () =>
        {
            _view.Redraw(); SyncSelection(); UpdateVehicleInfo(); UpdateUndo(); UpdateSourceLabel();
            if (_name.Text != d.Name && !_name.Focused) { _syncing = true; _name.Text = d.Name; _syncing = false; }
        };
        _syncing = true; _name.Text = d.Name; _syncing = false;
        SyncSelection(); UpdateVehicleInfo(); UpdateUndo();
        _view.FrameAll(false);
    }

    void UpdateUndo()
    {
        _undoBtn.Enabled = _doc.UndoLabel != null; _redoBtn.Enabled = _doc.RedoLabel != null;
        _undoBtn.ToolTipText = _doc.UndoLabel is { } u ? $"Undo {u} (Ctrl+Z)" : "Ctrl+Z";
        _redoBtn.ToolTipText = _doc.RedoLabel is { } r ? $"Redo {r} (Ctrl+Y)" : "Ctrl+Y";
    }

    void DoUndo() { if (_doc.Undo()) _status.Text = "Undone."; }
    void DoRedo() { if (_doc.Redo()) _status.Text = "Redone."; }

    /// <summary>One undoable edit applied to every selected part.</summary>
    void Edit(string label, Action<VehicleDocument.Part> f)
    {
        if (_doc.Selection.Count == 0) return;
        _doc.Begin(label);
        foreach (var p in _doc.Selection) f(p);
        _doc.Commit();
    }

    void SetAction(bool second, int button, bool on)
    {
        uint bit = 0x1000u << button;
        Edit("action buttons", p => { if (second) p.B.Action2 = on ? p.B.Action2 | bit : p.B.Action2 & ~bit; else p.B.Action1 = on ? p.B.Action1 | bit : p.B.Action1 & ~bit; });
    }

    void PaintSel(uint? rgba) =>
        Edit(rgba == null ? "default colour" : "paint", p =>
        {
            if (rgba is uint c) { p.B.Painted = 1; p.B.Paint = c; }
            else { p.B.Painted = 0; p.B.Paint = _cat?[p.B.Part]?.DefaultPaint ?? p.B.Paint; }
        });

    void SyncSelection()
    {
        _syncing = true;
        try
        {
            var sel = _doc.Selection.ToList();
            bool any = sel.Count > 0, one = sel.Count == 1;
            foreach (Control c in new Control[] { _partType, _orient, _painted, _colourBtn, _setting, _groupByte }) c.Enabled = any;
            foreach (var n in new[] { _px, _py, _pz }) n.Enabled = one;
            foreach (var c in _act1.Concat(_act2)) c.Enabled = any;
            if (!any)
            {
                _selInfo.Text = "Nothing selected. Click a part (Select tool), or pick a part in the library and Place it.";
                _partType.SelectedIndex = -1; _orient.SelectedIndex = -1; _setting.Items.Clear(); _painted.Checked = false;
                foreach (var c in _act1.Concat(_act2)) c.Checked = false;
                return;
            }
            var f = sel[0];
            var info = _cat?[f.B.Part];
            _selInfo.Text = one ? $"{info?.Name ?? $"unknown part 0x{f.B.Part:X8}"} ({info?.Key})" : $"{sel.Count} parts selected ({sel.Select(p => p.B.Part).Distinct().Count()} kinds)";
            _partType.SelectedItem = sel.All(p => p.B.Part == f.B.Part) ? info : null;
            if (one) { _px.Value = Math.Clamp(f.X, -255, 510); _py.Value = Math.Clamp(f.Y, -255, 510); _pz.Value = Math.Clamp(f.Z, -255, 510); }
            _orient.SelectedIndex = sel.All(p => p.Orientation == f.Orientation) ? f.Orientation : -1;
            _painted.Checked = sel.All(p => p.B.Painted != 0);
            _colourShow.BackColor = Rgb(f.B.Painted != 0 ? f.B.Paint : info?.DefaultPaint ?? f.B.Paint);
            var settings = info?.Settings ?? Array.Empty<(byte, string)>();
            _setting.Items.Clear();
            foreach (var (v, n) in settings) _setting.Items.Add(new SettingItem(v, n));
            _setting.Enabled = settings.Count > 0 && sel.All(p => _cat?[p.B.Part]?.Settings.Count == settings.Count);
            foreach (SettingItem si in _setting.Items) if (si.Value == f.B.Setting) _setting.SelectedItem = si;
            for (int i = 0; i < 3; i++)
            {
                uint bit = 0x1000u << i;
                _act1[i].Checked = sel.All(p => (p.B.Action1 & bit) != 0);
                _act2[i].Checked = sel.All(p => (p.B.Action2 & bit) != 0);
            }
            _groupByte.Value = f.B.Group;
        }
        finally { _syncing = false; }
    }

    void UpdateVehicleInfo()
    {
        var parts = _doc.Parts;
        float w = parts.Sum(p => _cat?.WeightOf(p.B.Part) ?? 0);
        string ext = "";
        if (parts.Count > 0)
            ext = $", {parts.Max(p => p.X) - parts.Min(p => p.X) + 1}×{parts.Max(p => p.Y) - parts.Min(p => p.Y) + 1}×{parts.Max(p => p.Z) - parts.Min(p => p.Z) + 1} cells";
        _vehInfo.Text = $"{parts.Count} parts{ext}, weight {w:0.#}" + (_doc.Dirty ? " — unsaved changes" : "");
        _issues.BeginUpdate();
        _issues.Items.Clear();
        IEnumerable<uint>? load = _target == Target.Game && _game != null ? (WorldBundles?.Invoke() is { Count: > 0 } wb && _game.Bundles.Any(wb.Contains) ? wb : _game.Bundles.Append(0x685374u)) : null;
        foreach (var i in _doc.Validate(_cat, load)) _issues.Items.Add(new IssueItem(i));
        if (_issues.Items.Count == 0) _issues.Items.Add("✔ No problems found.");
        _issues.EndUpdate();
    }

    void UpdateSourceLabel()
    {
        _source.Text = _target switch
        {
            Target.File when _file != null => $"{_file.Kind}: {(_file.Path != null ? Path.GetFileName(_file.Path) : "")}{(_doc.Dirty ? " *" : "")}",
            Target.Game when _game != null => $"Game vehicle {_game.Short}{(_game.Owner.Length > 0 ? $" ({_game.Owner})" : "")} in {string.Join(", ", _game.Bundles.Select(b => b.ToString("x6")))}{(_doc.Dirty ? " *" : "")}",
            _ => "New vehicle" + (_doc.Dirty ? " *" : ""),
        };
        _saveGame.Enabled = _target == Target.Game && _game != null;
    }

    void SetTool(VehicleTool t)
    {
        _view.Tool = t;
        _tSel.Checked = t == VehicleTool.Select; _tPlace.Checked = t == VehicleTool.Place; _tPaint.Checked = t == VehicleTool.Paint;
        if (t == VehicleTool.Place && LibPart() == null) _status.Text = "Choose a part in the library on the left, then click in the view to place it (X/Y/Z rotate the ghost).";
        _view.Redraw();
    }

    // ------------------------------------------------------------------ editing

    void PlaceAt(int x, int y, int z)
    {
        var info = _view.PlacePart;
        if (info == null) return;
        _doc.Begin("place " + info.Name);
        var b = new BlueprintBlock { Part = info.Id, Category = (byte)(info.Category + 1), Paint = info.DefaultPaint };
        b.Orientation = _view.PlaceOrientation;
        // the same part type elsewhere on the vehicle: take its buttons and setting (weapons fire on the same button)
        if (_doc.Parts.FirstOrDefault(p => p.B.Part == info.Id) is { } same) { b.Action1 = same.B.Action1; b.Action2 = same.B.Action2; b.Setting = same.B.Setting; }
        var part = new VehicleDocument.Part { X = x, Y = y, Z = z, B = b };
        _doc.Parts.Add(part);
        _doc.Selection.Clear(); _doc.Selection.Add(part);
        _doc.Commit();
        _status.Text = $"Placed {info.Name} at ({x}, {y}, {z}).";
    }

    void Rotate(int axis, int dir)
    {
        if (_view.Tool == VehicleTool.Place && _doc.Selection.Count == 0)
        {
            _view.PlaceOrientation = Orientations.Turn(_view.PlaceOrientation, axis, dir);
            _view.Redraw();
            return;
        }
        if (_doc.Selection.Count == 0) { _view.PlaceOrientation = Orientations.Turn(_view.PlaceOrientation, axis, dir); _view.Redraw(); return; }
        _doc.Begin($"rotate about {"XYZ"[axis]}");
        var sel = _doc.Selection.ToList();
        // rotate positions about the selection's centre cell (a single part turns in place)
        int cx = (int)Math.Round(sel.Average(p => p.X)), cy = (int)Math.Round(sel.Average(p => p.Y)), cz = (int)Math.Round(sel.Average(p => p.Z));
        var turn = Orientations.Turn(0, axis, dir);
        foreach (var p in sel)
        {
            var (rx, ry, rz) = Orientations.Apply(turn, p.X - cx, p.Y - cy, p.Z - cz);
            p.X = cx + rx; p.Y = cy + ry; p.Z = cz + rz;
            p.B.Orientation = Orientations.Turn(p.Orientation, axis, dir);
        }
        _doc.Commit();
    }

    void Move(int dx, int dy, int dz)
    {
        if (_doc.Selection.Count == 0) return;
        _doc.Begin("move parts");
        foreach (var p in _doc.Selection) { p.X += dx; p.Y += dy; p.Z += dz; }
        _doc.Commit();
    }

    void DeleteSel()
    {
        if (_doc.Selection.Count == 0) return;
        _doc.Begin($"delete {_doc.Selection.Count} part(s)");
        _doc.Parts.RemoveAll(_doc.Selection.Contains);
        _doc.Selection.Clear();
        _doc.Commit();
    }

    void CopySel()
    {
        if (_doc.Selection.Count == 0) return;
        _clip = _doc.Selection.Select(p => p.Clone()).ToList();
        _status.Text = $"{_clip.Count} part(s) copied.";
    }

    void Paste()
    {
        if (_clip.Count == 0) return;
        _doc.Begin($"paste {_clip.Count} part(s)");
        int w = _clip.Max(p => p.X) - _clip.Min(p => p.X) + 1;
        _doc.Selection.Clear();
        foreach (var c in _clip)
        {
            var p = c.Clone(); p.X += w;
            _doc.Parts.Add(p); _doc.Selection.Add(p);
        }
        _doc.Commit();
        _status.Text = $"Pasted {_clip.Count} part(s) beside the copied ones: move them with the arrow keys or by dragging.";
    }

    /// <summary>The edit keys while the editor is shown (called by the main window before its own Ctrl+Z / Ctrl+Y / Ctrl+C /
    /// Ctrl+X / Ctrl+V / Ctrl+A / Del handling, unless a text box has the focus): the editor's undo, clipboard and delete.</summary>
    public bool HandleEditKey(Keys k)
    {
        switch (k)
        {
            case Keys.Control | Keys.Z: DoUndo(); return true;
            case Keys.Control | Keys.Y: case Keys.Control | Keys.Shift | Keys.Z: DoRedo(); return true;
            case Keys.Control | Keys.C: CopySel(); return true;
            case Keys.Control | Keys.X: CopySel(); DeleteSel(); return true;
            case Keys.Control | Keys.V: Paste(); return true;
            case Keys.Control | Keys.A: _doc.Selection.Clear(); foreach (var p in _doc.Parts) _doc.Selection.Add(p); SyncSelection(); _view.Redraw(); return true;
            case Keys.Control | Keys.S: Save(); return true;
            case Keys.Delete: DeleteSel(); return true;
        }
        return false;
    }

    /// <summary>Keys of the 3D view (tools, rotate, move, frame) plus the edit keys. Returns true when the key was used.</summary>
    public bool HandleKey(Keys k, bool typing)
    {
        if (typing) return false;
        bool shift = (k & Keys.Shift) != 0;
        if (HandleEditKey(k)) return true;
        if (k == Keys.Back) { DeleteSel(); return true; }
        switch (k & ~Keys.Shift)
        {
            case Keys.X: Rotate(0, shift ? -1 : 1); return true;
            case Keys.Y: case Keys.R: Rotate(1, shift ? -1 : 1); return true;
            case Keys.Z: Rotate(2, shift ? -1 : 1); return true;
            case Keys.Left: Move(-1, 0, 0); return true;
            case Keys.Right: Move(1, 0, 0); return true;
            case Keys.Up: Move(0, 0, 1); return true;
            case Keys.Down: Move(0, 0, -1); return true;
            case Keys.PageUp: Move(0, 1, 0); return true;
            case Keys.PageDown: Move(0, -1, 0); return true;
            case Keys.F: _view.FocusSelection(); return true;
            case Keys.Home: _view.FrameAll(false); return true;
            case Keys.Escape: if (_view.Tool != VehicleTool.Select) SetTool(VehicleTool.Select); else { _doc.Selection.Clear(); SyncSelection(); _view.Redraw(); } return true;
            case Keys.P: SetTool(VehicleTool.Place); return true;
            case Keys.B: SetTool(VehicleTool.Paint); return true;
            case Keys.S: SetTool(VehicleTool.Select); return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ open

    void New()
    {
        if (!ConfirmDiscard()) return;
        _target = Target.None; _file = null; _game = null;
        Attach(new VehicleDocument());
        EnsureCatalog();
        UpdateSourceLabel();
    }

    /// <summary>Scripted test runs: no questions.</summary>
    bool _scripted;

    bool ConfirmDiscard() =>
        _scripted || !_doc.Dirty || MessageBox.Show(this, "The vehicle has unsaved changes. Discard them?", "Vehicle Editor", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;

    void OpenDialog()
    {
        using var d = new OpenFileDialog { Title = "Open a vehicle (Xbox 360 package 0x0000000N, its content file, or a blueprint .bin)", Filter = "Vehicle files|*.*" };
        if (d.ShowDialog(this) == DialogResult.OK) OpenFiles(new[] { d.FileName });
    }

    /// <summary>Opens a vehicle file as the editor's source (Save writes back to it).</summary>
    public void OpenFiles(IEnumerable<string> files)
    {
        var f = files.FirstOrDefault();
        if (f == null || !ConfirmDiscard()) return;
        try
        {
            var v = VehicleFile.Open(f);
            EnsureCatalog();
            _target = Target.File; _file = v; _game = null;
            Attach(VehicleDocument.From(v.Blueprint));
            Log?.Invoke($"Vehicle Editor: opened {v.Kind.ToString().ToLowerInvariant()} \"{v.Blueprint.Name}\" ({v.Blueprint.Blocks.Count} parts) from {f}" + (v.Problems.Count > 0 ? " — " + string.Join("; ", v.Problems) : ""));
            UpdateSourceLabel();
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"{Path.GetFileName(f)}: {e.Message}", "Vehicle Editor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// Files dropped on the editor. With a game vehicle open, the dropped vehicle replaces what is shown and the game vehicle
    /// stays the target (Save to Game writes it there, keeping the asset id); otherwise the dropped file is opened.
    /// </summary>
    public void Drop(IEnumerable<string> files)
    {
        var f = files.FirstOrDefault();
        if (f == null) return;
        if (_target != Target.Game) { OpenFiles(new[] { f }); return; }
        try
        {
            var v = VehicleFile.Open(f);
            EnsureCatalog();
            _doc.Begin($"replace with {Path.GetFileName(f)}");
            var src = VehicleDocument.From(v.Blueprint);
            _doc.Parts = src.Parts; _doc.Selection.Clear();
            // stats and buttons of the dropped vehicle; the game asset's name field stays
            var keepName = _doc.Source.Header.AsSpan(Blueprint.NameOffset, Blueprint.NameBytes).ToArray();
            var original = _doc.Source;
            _doc.Source = v.Blueprint.Clone();
            keepName.CopyTo(_doc.Source.Header, Blueprint.NameOffset);
            string seatNote = AiSeats(original);
            _doc.Commit();
            if (seatNote.Length > 0) Log?.Invoke("Vehicle Editor: " + seatNote);
            _view.FrameAll();
            Log?.Invoke($"Vehicle Editor: {_game!.Short} now shows \"{v.Blueprint.Name}\" ({v.Blueprint.Blocks.Count} parts) from {Path.GetFileName(f)} — Save to Game writes it in place of {_game.Label}.");
            _status.Text = $"Replaced with \"{v.Blueprint.Name}\": finish it, then Save to Game (or Save As a 360 package).";
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            MessageBox.Show(this, $"{Path.GetFileName(f)}: {e.Message}", "Vehicle Editor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// AI drivers sit in AI seats: the game's AI racers carry secondaryseats_small / secondaryseats_large (variants
    /// "passengersmallai" / "passengerlargeai") instead of a driver seat. A player's vehicle dropped onto an AI vehicle gets
    /// the original's AI seat in place of its driver seats (same cell and orientation), so the AI can take the wheel.
    /// </summary>
    public bool AiSeatSwap = true;

    string AiSeats(Blueprint original)
    {
        if (!AiSeatSwap || _cat == null) return "";
        var ai = original.Blocks.Select(b => _cat[b.Part]).FirstOrDefault(p => p != null && p.Class == "objDefId_vehicleBlockSeat" && p.Variant.EndsWith("ai"));
        if (ai == null || _doc.Parts.Any(p => _cat[p.B.Part]?.Variant.EndsWith("ai") == true)) return "";
        var seats = _doc.Parts.Where(p => _cat[p.B.Part] is { } i && i.Class == "objDefId_vehicleBlockSeat" && i.Key.StartsWith("seats_")).ToList();
        int raised = 0;
        foreach (var p in seats)
        {
            p.B.Part = ai.Id; p.B.Category = (byte)(ai.Category + 1);
            if (p.B.Painted == 0) p.B.Paint = ai.DefaultPaint;
            // the AI seat can be bigger (the large one is 3x2x2): move it up until it overlaps nothing (it then sits on the parts
            // below it; a seat inside other parts was not usable: Mr. Fit stayed out of an overlapped seat in Xenia)
            var others = _doc.Parts.Where(q => q != p).SelectMany(q => VehicleDocument.Cells(q, _cat[q.B.Part])).ToHashSet();
            int y0 = p.Y;
            while (VehicleDocument.Cells(p, ai).Any(others.Contains) && p.Y < y0 + 8) p.Y++;
            if (p.Y != y0) raised++;
        }
        return seats.Count == 0 ? "" : $"{seats.Count} driver seat(s) replaced by the AI seat of the game vehicle ({ai.Name}, {ai.Key}): the AI driver drives from it" +
            (raised > 0 ? $"; {raised} moved up to make room for it" : "") + " (Ctrl+Z undoes the drop and the seat swap together).";
    }

    void FillPregameMenu()
    {
        _pregameBtn.DropDownItems.Clear();
        if (_ws == null || _idx == null) { _pregameBtn.DropDownItems.Add("(open a workspace)"); return; }
        var wb = WorldBundles?.Invoke();
        if (wb is { Count: > 0 })
        {
            _pregameBtn.DropDownItems.Add(new ToolStripMenuItem($"Vehicles of {WorldLabel?.Invoke() ?? "the open world"}:") { Enabled = false });
            try
            {
                foreach (var v in PregameVehicles.ForBundles(_ws, _idx, wb))
                {
                    var it = new ToolStripMenuItem($"{v.Label} — {v.Parts} parts", null, (_, _) => OpenPregame(v)) { ToolTipText = string.Join("\n", v.Users.DefaultIfEmpty("resident in " + string.Join(", ", v.Bundles.Select(b => b.ToString("x6"))))) };
                    _pregameBtn.DropDownItems.Add(it);
                }
            }
            catch (Exception e) { _pregameBtn.DropDownItems.Add("error: " + e.Message); }
            _pregameBtn.DropDownItems.Add(new ToolStripSeparator());
        }
        else _pregameBtn.DropDownItems.Add(new ToolStripMenuItem("(open a world / Act to list its vehicles and their drivers)") { Enabled = false });
        var all = new ToolStripMenuItem("All game vehicles");
        foreach (var g in PregameVehicles.All(_idx).GroupBy(v => v.Short.Split('_')[0]))
        {
            var sub = new ToolStripMenuItem(g.Key);
            foreach (var v in g) { var vv = v; sub.DropDownItems.Add(v.Short, null, (_, _) => OpenPregame(vv)); }
            all.DropDownItems.Add(sub);
        }
        _pregameBtn.DropDownItems.Add(all);
    }

    /// <summary>Opens one of the game's vehicles (Save to Game writes it back into every bundle holding it).</summary>
    public void OpenPregame(PregameVehicle v)
    {
        if (_ws == null || !ConfirmDiscard()) return;
        try
        {
            EnsureCatalog();
            var bp = PregameVehicles.Load(_ws, v);
            _target = Target.Game; _game = v; _file = null;
            Attach(VehicleDocument.From(bp));
            Log?.Invoke($"Vehicle Editor: game vehicle {v.Label}, {bp.Blocks.Count} parts (in {string.Join(", ", v.Bundles.Select(b => b.ToString("x6")))}). Drop an Xbox 360 vehicle here to replace it.");
            _status.Text = "Game vehicle: drop an Xbox 360 vehicle (package or content file) onto the editor to replace it, edit, then Save to Game.";
            UpdateSourceLabel();
        }
        catch (Exception e) { MessageBox.Show(this, e.Message, "Vehicle Editor", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    /// <summary>Opens a game vehicle by asset name (Assets tab, script hooks).</summary>
    public bool OpenPregameAsset(string name)
    {
        if (_idx == null) return false;
        var bundles = WorldBundles?.Invoke();
        PregameVehicle? v = null;
        if (bundles is { Count: > 0 } && _ws != null)
            try { v = PregameVehicles.ForBundles(_ws, _idx, bundles).FirstOrDefault(x => x.Asset == name || x.Short == name); } catch { }
        v ??= PregameVehicles.All(_idx).FirstOrDefault(x => x.Asset == name || x.Short == name);
        if (v == null) return false;
        OpenPregame(v);
        return true;
    }

    // ------------------------------------------------------------------ save

    Blueprint? CurrentBlueprint()
    {
        if (!_syncing && _name.Text != _doc.Name) { _doc.Begin("rename"); _doc.Name = _name.Text; _doc.Commit(); }
        try { return _doc.ToBlueprint(_cat); }
        catch (InvalidDataException e) { MessageBox.Show(this, e.Message, "Vehicle Editor", MessageBoxButtons.OK, MessageBoxIcon.Warning); return null; }
    }

    bool ConfirmIssues(bool forGame)
    {
        if (_scripted) return true;
        var errs = _doc.Validate(_cat, forGame ? WorldBundles?.Invoke() : null).Where(i => i.Error).ToList();
        if (errs.Count == 0) return true;
        return MessageBox.Show(this, "The vehicle has problems the game may not survive:\n\n" + string.Join("\n", errs.Take(8).Select(e => "• " + e.Text)) + "\n\nSave anyway?",
            "Vehicle Editor", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
    }

    void Saved()
    {
        _doc.Dirty = false;
        UpdateVehicleInfo(); UpdateSourceLabel();
    }

    void Save()
    {
        switch (_target)
        {
            case Target.Game: SaveToGame(); return;
            case Target.File when _file?.Path != null:
                if (!ConfirmIssues(false)) return;
                var bp = CurrentBlueprint(); if (bp == null) return;
                try
                {
                    byte[] bytes = _file.Kind switch
                    {
                        VehicleFileKind.Package => _file.ToPackage(bp),
                        VehicleFileKind.Content => _file.ContentBytes(bp),
                        _ => bp.Write(false),
                    };
                    WriteFile(_file.Path, bytes, backup: true);
                    var re = VehicleFile.Open(_file.Path);
                    _file = re; _doc.Source = re.Blueprint.Clone();
                    Saved();
                    Log?.Invoke($"Vehicle Editor: saved \"{bp.Name}\" ({bp.Blocks.Count} parts) to {_file.Path} ({_file.Kind}{(_file.Kind == VehicleFileKind.Package ? ", hashes recomputed; a real Xbox 360 also needs the package resigned (Horizon / Velocity: Rehash and Resign)" : "")}).");
                }
                catch (Exception e) { MessageBox.Show(this, e.Message, "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                return;
            default: SaveAsPackage(); return;
        }
    }

    static void WriteFile(string path, byte[] bytes, bool backup)
    {
        if (backup && File.Exists(path))
        {
            var bak = path + ".bak";
            if (!File.Exists(bak)) File.Copy(path, bak);
        }
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, true);
    }

    void SaveAsPackage()
    {
        if (!ConfirmIssues(false)) return;
        var bp = CurrentBlueprint(); if (bp == null) return;
        string dir = _file?.Path != null ? Path.GetDirectoryName(_file.Path)! : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string suggested = _file?.Kind == VehicleFileKind.Package && _file.Path != null ? Path.GetFileName(_file.Path) : VehicleFile.NextFreePackageName(dir);
        using var d = new SaveFileDialog { Title = "Save as an Xbox 360 vehicle package (name it 0x0000000N, the lowest number not used on the console)", InitialDirectory = dir, FileName = suggested, Filter = "Xbox 360 package|*.*" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        byte[]? template = null;
        if (_file?.Kind != VehicleFileKind.Package)
        {
            // a new package: offer to take the header (profile, console) from one of the player's own packages
            if (MessageBox.Show(this, "Use one of your own vehicle packages from the console as the template (keeps your profile and console ids so the Xbox 360 lists it after Rehash and Resign)?\n\nNo: a neutral package (fine for Xenia).",
                "Vehicle Editor", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                using var o = new OpenFileDialog { Title = "One of your vehicle packages (0x0000000N)", Filter = "Xbox 360 package|*.*" };
                if (o.ShowDialog(this) == DialogResult.OK) template = File.ReadAllBytes(o.FileName);
            }
        }
        try
        {
            var src = _file ?? new VehicleFile { Kind = VehicleFileKind.Blueprint, Blueprint = bp };
            byte[] thumb = _file?.Package?.Thumbnail != null ? null! : _view.Thumbnail();
            var bytes = src.ToPackage(bp, Path.GetFileName(d.FileName), template, thumbnailPng: _file?.Package?.Thumbnail == null ? thumb : null);
            WriteFile(d.FileName, bytes, backup: true);
            Log?.Invoke($"Vehicle Editor: wrote package {d.FileName} (\"VEHICLE: {bp.Name}\", {bp.Blocks.Count} parts). Xenia loads it as is; a real Xbox 360 needs it resigned (Horizon / Velocity: Rehash and Resign).");
            _target = Target.File; _file = VehicleFile.Open(d.FileName); _game = null; _doc.Source = _file.Blueprint.Clone();
            Saved();
        }
        catch (Exception e) { MessageBox.Show(this, e.Message, "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    void SaveAsFile(VehicleFileKind kind)
    {
        if (!ConfirmIssues(false)) return;
        var bp = CurrentBlueprint(); if (bp == null) return;
        using var d = new SaveFileDialog { Title = kind == VehicleFileKind.Content ? "Save the vehicle's content file (what a package holds / what Xenia keeps)" : "Save the bare blueprint (aid_vehicle .data)", FileName = kind == VehicleFileKind.Content ? "00000001" : bp.Name + ".bin", Filter = "All files|*.*" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        var src = _file ?? new VehicleFile { Blueprint = bp };
        WriteFile(d.FileName, kind == VehicleFileKind.Content ? src.ContentBytes(bp) : bp.Write(false), backup: true);
        Log?.Invoke($"Vehicle Editor: wrote {kind.ToString().ToLowerInvariant()} {d.FileName}");
    }

    /// <summary>Into the shared vehicle saves: every Test in Xenia (and NB Multiplayer) gets it in Your Blueprints.</summary>
    void SaveToVault()
    {
        var dir = VehicleSavesDir?.Invoke();
        if (dir == null) { MessageBox.Show(this, "Vehicle saves are kept per workspace (File > Settings > vehicle saves): choose a shared folder first.", "Vehicle Editor"); return; }
        if (!ConfirmIssues(false)) return;
        var bp = CurrentBlueprint(); if (bp == null) return;
        var src = _file ?? new VehicleFile { Blueprint = bp };
        var tmp = Path.Combine(Path.GetTempPath(), "nbvehicle_" + Environment.ProcessId, "0x00000001");
        Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);
        File.WriteAllBytes(tmp, src.ToPackage(bp, "0x00000001", thumbnailPng: src.Package?.Thumbnail == null ? _view.Thumbnail() : null));
        var (added, known, skipped) = VehicleVault.ImportPackages(dir, new[] { tmp });
        try { File.Delete(tmp); } catch { }
        Log?.Invoke(added.Count > 0 ? $"Vehicle Editor: \"{bp.Name}\" added to the vehicle saves ({dir}); the next Test in Xenia lists it in Your Blueprints."
            : known > 0 ? "Vehicle Editor: this exact vehicle is already in the vehicle saves." : "Vehicle Editor: not added: " + string.Join("; ", skipped));
    }

    /// <summary>Installs the vehicle into a Xenia content folder (a profile folder content\&lt;xuid&gt;): next free 0x0000000N.</summary>
    void SaveIntoXenia()
    {
        if (!ConfirmIssues(false)) return;
        var bp = CurrentBlueprint(); if (bp == null) return;
        using var d = new FolderBrowserDialog { Description = "A Xenia profile folder: …\\content\\<profile id> (16 hex digits)", UseDescriptionForTitle = true };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        var prof = d.SelectedPath;
        ulong xuid = ulong.TryParse(Path.GetFileName(prof), NumberStyles.HexNumber, null, out var x) ? x : 0;
        var src = _file ?? new VehicleFile { Blueprint = bp };
        var saves = Path.Combine(prof, VehicleFile.TitleId.ToString("X8"), "00000001");
        string name = VehicleFile.NextFreePackageName(saves);
        var pkg = src.ToPackage(bp, name, profileId: xuid != 0 ? xuid : null, thumbnailPng: src.Package?.Thumbnail == null ? _view.Thumbnail() : null);
        var where = VehicleFile.InstallToXenia(pkg, prof, name);
        Log?.Invoke($"Vehicle Editor: installed \"{bp.Name}\" as {name} in {where}");
    }

    void SaveToGame()
    {
        if (_ws == null || _game == null) return;
        if (!ConfirmIssues(true)) return;
        var bp = CurrentBlueprint(); if (bp == null) return;
        try
        {
            var done = PregameVehicles.Save(_ws, _game, bp, $"vehicle {_game.Short}{(_game.Owner.Length > 0 ? $" ({_game.Owner})" : "")}: {bp.Blocks.Count} parts (Vehicle Editor)");
            _doc.Source = bp.Clone();
            Saved();
            Log?.Invoke(done.Count > 0 ? $"Vehicle Editor: {_game.Label} written into {string.Join(", ", done.Select(b => b.ToString("x6")))} ({bp.Blocks.Count} parts). Edit > Undo restores it."
                : $"Vehicle Editor: {_game.Label} unchanged.");
        }
        catch (Exception e) { MessageBox.Show(this, e.Message, "Save to game failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    // ------------------------------------------------------------------ script hooks (tests)

    public Bitmap CaptureView() => _view.Capture();
    public void SetCamera(float yaw, float pitch, float dist) => _view.SetCamera(yaw, pitch, dist);
    public void FrameAll() => _view.FrameAll();
    public VehicleDocument Document => _doc;
    /// <summary>Saves to the current target without dialogs (scripted tests): package/content/blueprint file or game.</summary>
    public string SaveQuiet(string? packagePath = null)
    {
        var bp = _doc.ToBlueprint(_cat);
        if (packagePath != null)
        {
            var src = _file ?? new VehicleFile { Blueprint = bp };
            File.WriteAllBytes(packagePath, src.ToPackage(bp, Path.GetFileName(packagePath), thumbnailPng: src.Package?.Thumbnail == null ? _view.Thumbnail() : null));
            return "package " + packagePath;
        }
        if (_target == Target.Game && _game != null && _ws != null) { var d = PregameVehicles.Save(_ws, _game, bp); _doc.Source = bp.Clone(); Saved(); return "game " + string.Join(",", d.Select(b => b.ToString("x6"))); }
        Save(); return "saved";
    }
    public void SelectAll() { _doc.Selection.Clear(); foreach (var p in _doc.Parts) _doc.Selection.Add(p); SyncSelection(); _view.Redraw(); }
    public void PaintSelection(uint rgba) => PaintSel(rgba);
    public void SelectWhere(Func<VehicleDocument.Part, PartInfo?, bool> f) { _doc.Selection.Clear(); foreach (var p in _doc.Parts) if (f(p, _cat?[p.B.Part])) _doc.Selection.Add(p); SyncSelection(); _view.Redraw(); }
    public void SetSetting(byte v) => Edit("part setting", p => p.B.Setting = v);
    public bool EnsureParts() => EnsureCatalog();

    /// <summary>
    /// Script options for automated tests (NBModStudio.exe --workspace W --vehicle-open F --vehicle-shot out.png --exit):
    /// --vehicle-open FILE, --vehicle-game ASSET, --vehicle-drop FILE, --vehicle-cam YAW PITCH DIST, --vehicle-shot PNG,
    /// --vehicle-paint-all RRGGBB, --vehicle-paint-where KEY RRGGBB, --vehicle-setting KEY VALUE, --vehicle-place KEY X Y Z [ORIENT],
    /// --vehicle-rotate-all AXIS, --vehicle-delete-where KEY, --vehicle-undo, --vehicle-redo, --vehicle-save-game,
    /// --vehicle-save-package PATH, --vehicle-save-vault, --vehicle-info.
    /// </summary>
    public async Task<bool> RunScriptCommand(string arg, Func<string> next, Action<string> log, Action show)
    {
        if (!arg.StartsWith("--vehicle-")) return false;
        _scripted = true;
        show();
        await Task.Delay(100); Application.DoEvents();
        static uint Col(string hex) => uint.Parse(hex.TrimStart('#'), NumberStyles.HexNumber) << 8 | 0xFF;
        bool Match(VehicleDocument.Part p, PartInfo? i, string key) => i != null ? i.Key.Contains(key, StringComparison.OrdinalIgnoreCase) : p.B.Part.ToString("X8") == key;
        switch (arg)
        {
            case "--vehicle-open": { var f = next(); OpenFiles(new[] { f }); log($"script: vehicle opened {f}: {_doc.Parts.Count} parts"); break; }
            case "--vehicle-game": { var n = next(); bool ok = OpenPregameAsset(n); log($"script: game vehicle {n}: {(ok ? _doc.Parts.Count + " parts" : "not found")}"); break; }
            case "--vehicle-drop": { var f = next(); Drop(new[] { f }); log($"script: dropped {f}: {_doc.Parts.Count} parts, target {_source.Text}"); break; }
            case "--vehicle-cam": { float y = float.Parse(next(), CultureInfo.InvariantCulture), pi = float.Parse(next(), CultureInfo.InvariantCulture), d = float.Parse(next(), CultureInfo.InvariantCulture); _view.FrameAll(); _view.SetCamera(y, pi, d); break; }
            case "--vehicle-shot":
            {
                var f = next();
                _doc.Selection.Clear(); SyncSelection();
                await Task.Delay(300); Application.DoEvents();
                using (var b = _view.Capture()) b.Save(f);
                var form = FindForm();
                if (form != null) { using var whole = new Bitmap(form.Width, form.Height); form.DrawToBitmap(whole, new Rectangle(0, 0, form.Width, form.Height)); using var g = Graphics.FromImage(whole); using var v = _view.Capture(); g.DrawImage(v, form.RectangleToClient(_view.RectangleToScreen(_view.ClientRectangle)).Location + new Size(form.Width - form.ClientSize.Width - 8, form.Height - form.ClientSize.Height - 8)); whole.Save(Path.ChangeExtension(f, null) + "_ui.png"); }
                log($"script: vehicle shot {f}"); break;
            }
            case "--vehicle-paint-all": { var c = Col(next()); SelectAll(); PaintSel(c); log($"script: painted {_doc.Parts.Count} parts #{c >> 8:X6}"); break; }
            case "--vehicle-paint-where": { var k = next(); var c = Col(next()); SelectWhere((p, i) => Match(p, i, k)); PaintSel(c); log($"script: painted {_doc.Selection.Count} '{k}' parts #{c >> 8:X6}"); break; }
            case "--vehicle-setting": { var k = next(); byte v = byte.Parse(next()); SelectWhere((p, i) => Match(p, i, k)); SetSetting(v); log($"script: setting {v} on {_doc.Selection.Count} '{k}' parts"); break; }
            case "--vehicle-delete-where": { var k = next(); SelectWhere((p, i) => Match(p, i, k)); int n = _doc.Selection.Count; DeleteSel(); log($"script: deleted {n} '{k}' parts"); break; }
            case "--vehicle-place":
            {
                var k = next(); int x = int.Parse(next()), y = int.Parse(next()), z = int.Parse(next());
                var info = _cat?.Parts.Values.FirstOrDefault(p => p.Key == k) ?? _cat?.Parts.Values.FirstOrDefault(p => p.Key.Contains(k));
                if (info == null) { log("script: no part " + k); break; }
                _view.PlacePart = info; _view.PlaceOrientation = 0;
                PlaceAt(x, y, z); log($"script: placed {info.Key} at {x},{y},{z}"); break;
            }
            case "--vehicle-rotate-all": { int ax = int.Parse(next()); SelectAll(); Rotate(ax, 1); log("script: rotated all about " + "XYZ"[ax]); break; }
            case "--vehicle-wide": { bool w = next() == "1"; _outer.Panel1Collapsed = w; break; }
            case "--vehicle-switch-rule": { PartModelView.Rule = int.Parse(next()); _cat = null; EnsureCatalog(); _view.ResetGpu(); log("script: switch rule " + PartModelView.Rule); break; }
            case "--vehicle-new": { _doc.Dirty = false; New(); log("script: new vehicle"); break; }
            case "--vehicle-click":
            {
                // --vehicle-click FX FY: click at that fraction of the view (0.5 0.5 = centre) with the current tool
                float fx = float.Parse(next(), CultureInfo.InvariantCulture), fy = float.Parse(next(), CultureInfo.InvariantCulture);
                var sz = _view.ViewSize;
                _view.ClickAt((int)(sz.Width * fx), (int)(sz.Height * fy));
                log($"script: click {fx},{fy}: {_doc.Selection.Count} selected ({string.Join(", ", _doc.Selection.Select(p => (_cat?[p.B.Part]?.Key ?? "?") + $"@{p.X},{p.Y},{p.Z} o{p.Orientation}"))}), {_doc.Parts.Count} parts");
                break;
            }
            case "--vehicle-key": { var k = (Keys)Enum.Parse(typeof(Keys), next().Replace("+", ", "), true); bool used = HandleKey(k, false); log($"script: key {k} -> {(used ? "used" : "ignored")}; selection {string.Join(", ", _doc.Selection.Select(p => $"{_cat?[p.B.Part]?.Key}@{p.X},{p.Y},{p.Z} o{p.Orientation}"))}"); break; }
            case "--vehicle-hover":
            {
                float fx = float.Parse(next(), CultureInfo.InvariantCulture), fy = float.Parse(next(), CultureInfo.InvariantCulture);
                var sz = _view.ViewSize;
                var (c, st) = _view.HoverAt((int)(sz.Width * fx), (int)(sz.Height * fy));
                log($"script: ghost {_view.PlacePart?.Key} o{_view.PlaceOrientation} at {c}: {(st == 0 ? "attached (green)" : st == 1 ? "floating (orange)" : st == 2 ? "blocked (red)" : "-")}");
                break;
            }
            case "--vehicle-place-o":
            {
                // --vehicle-place-o KEY X Y Z ORIENTATION
                var k = next(); int x = int.Parse(next()), y = int.Parse(next()), z = int.Parse(next()), o = int.Parse(next());
                var info = _cat?.Parts.Values.FirstOrDefault(p => p.Key == k);
                if (info == null) { log("script: no part " + k); break; }
                _view.PlacePart = info; _view.PlaceOrientation = o; PlaceAt(x, y, z); _view.PlaceOrientation = 0;
                log($"script: placed {k} at {x},{y},{z} o{o}"); break;
            }
            case "--vehicle-name": { _doc.Begin("rename"); _doc.Name = next(); _doc.Commit(); _syncing = true; _name.Text = _doc.Name; _syncing = false; break; }
            case "--vehicle-tool": { SetTool(next() switch { "place" => VehicleTool.Place, "paint" => VehicleTool.Paint, _ => VehicleTool.Select }); break; }
            case "--vehicle-lib": { var key = next(); foreach (ListViewItem it in _lib.Items) if (it.Tag is PartInfo pi && pi.Key == key) { it.Selected = true; it.EnsureVisible(); } log("script: library part " + (_view.PlacePart?.Key ?? "none")); break; }
            case "--vehicle-undo": DoUndo(); log($"script: undo -> {_doc.Parts.Count} parts"); break;
            case "--vehicle-redo": DoRedo(); log($"script: redo -> {_doc.Parts.Count} parts"); break;
            case "--vehicle-save-game": log("script: " + SaveQuiet()); break;
            case "--vehicle-save-package": { var f = next(); log("script: " + SaveQuiet(f)); break; }
            case "--vehicle-save-vault": SaveToVault(); break;
            case "--vehicle-info":
                var cn = _view.Connectivity;
                log($"script: vehicle \"{_doc.Name}\" {_doc.Parts.Count} parts, source {_source.Text}; pieces {cn?.PieceCount}, floating {cn?.Floating.Count}: {string.Join(", ", cn?.Floating.Select(p => $"{_cat?[p.B.Part]?.Key}@{p.X},{p.Y},{p.Z}") ?? Array.Empty<string>())}; checks: {string.Join(" | ", _issues.Items.Cast<object>().Select(o => o.ToString()))}");
                break;
            default: return false;
        }
        Application.DoEvents();
        return true;
    }
}
