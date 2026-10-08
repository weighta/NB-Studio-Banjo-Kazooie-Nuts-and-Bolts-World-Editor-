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
/// <item>Parts library like the garage's Parts Store (its categories and order, part pictures, sizes, search) with every
/// part of the workspace (shipped, modded, and the game's internal / AI parts under Other), place / move / rotate (24 orientations) /
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
    /// <summary>The open world and Act ("worldofsport", "act2"; Act "" for a world without Act), null when none is open.</summary>
    public Func<(string World, string Act)?>? WorldAct;
    /// <summary>The shared vehicle saves folder (TestSaves.VaultDir), null when vehicles are kept per workspace.</summary>
    public Func<string?>? VehicleSavesDir;

    Workspace? _ws; AssetIndex? _idx; PartCatalog? _cat; TextureResolver? _tex;
    VehicleDocument _doc = new();
    enum Target { None, File, Game }
    Target _target;
    VehicleFile? _file;
    PregameVehicle? _game;
    VehiclePlace? _gamePlace;
    /// <summary>Every game vehicle with its world / Act / challenge: built in the background when the workspace opens (or read
    /// from the workspace cache when nothing changed), so the Game Vehicles window opens at once.</summary>
    List<PregameVehicle>? _gameCat;
    Task? _gameTask;
    Task<List<PregameVehicle>>? _gameBuild;   // the background build itself (_gameTask = its continuation on the UI thread)
    string _gameState = "";
    readonly GameVehiclePicker _picker = new();
    static List<VehicleDocument.Part> _clip = new();

    readonly VehicleViewport _view = new() { Dock = DockStyle.Fill };
    readonly ToolStrip _bar = new() { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top, LayoutStyle = ToolStripLayoutStyle.Flow };
    readonly StatusStrip _statusStrip = new();
    readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly ToolStripStatusLabel _source = new() { TextAlign = ContentAlignment.MiddleRight };
    /// <summary>Header over the 3D view: where the open vehicle comes from ("World of Sports › Act 2 Burnin' Rubber › Mr. Fit's
    /// vehicle", the file name, or "New vehicle").</summary>
    readonly Label _where = new() { Dock = DockStyle.Top, Height = 24, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 0, 0), AutoEllipsis = true,
        BackColor = Color.FromArgb(58, 64, 74), ForeColor = Color.White, Font = new Font(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont, FontStyle.Bold) };
    readonly ToolTip _whereTip = new();
    // library: the garage's Parts Store categories, part pictures, sizes
    const int ThumbSize = 48;
    readonly TextBox _search = new() { Dock = DockStyle.Top, PlaceholderText = "Search parts (name, category, internal name)…" };
    readonly ComboBox _category = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ListView _lib = new() { Dock = DockStyle.Fill, View = View.Tile, ShowGroups = true, FullRowSelect = true, HideSelection = false, MultiSelect = false, ShowItemToolTips = true };
    readonly ImageList _thumbs = new() { ImageSize = new Size(ThumbSize, ThumbSize), ColorDepth = ColorDepth.Depth32Bit };
    readonly Label _libInfo = new() { Dock = DockStyle.Bottom, Height = 62, Padding = new Padding(4), BorderStyle = BorderStyle.FixedSingle, AutoEllipsis = true };
    readonly System.Windows.Forms.Timer _thumbTimer = new() { Interval = 15 };
    readonly Queue<PartInfo> _thumbQueue = new();
    sealed record CategoryItem(string Name, int Count) { public override string ToString() => $"{Name}  ({Count})"; }
    // properties
    readonly Label _selInfo = new() { AutoSize = true, MaximumSize = new Size(255, 0) };
    readonly ComboBox _partType = new() { Width = 250, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly NumericUpDown _px = Num(), _py = Num(), _pz = Num();
    readonly ComboBox _orient = new() { Width = 170, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly CheckBox _painted = new() { Text = "Painted (off: the part's own colour)", AutoSize = true };
    readonly Button _colourBtn = new() { Text = "Colour…", Width = 90 };
    readonly Panel _colourShow = new() { Width = 40, Height = 23, BorderStyle = BorderStyle.FixedSingle };
    readonly FlowLayoutPanel _palette = new() { Width = 255, Height = 50, FlowDirection = FlowDirection.LeftToRight };
    /// <summary>Recent colours picked with Colour… (last 8, kept in NB Studio's data folder).</summary>
    readonly FlowLayoutPanel _recent = new() { Width = 255, Height = 26, FlowDirection = FlowDirection.LeftToRight };
    readonly List<uint> _recentColours = new();
    const int RecentMax = 8;
    /// <summary>Corner note over the 3D view when the vehicle is an AI driver's vehicle.</summary>
    readonly Panel _aiNote = new() { Visible = false, BackColor = Color.FromArgb(255, 244, 214), BorderStyle = BorderStyle.FixedSingle, Width = 360, Height = 96, Padding = new Padding(6) };
    readonly Label _aiText = new() { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(90, 60, 0) };
    readonly LinkLabel _aiSwap = new() { Dock = DockStyle.Bottom, Height = 18, Text = "" };
    /// <summary>The "Opened … — the last vehicle you edited" banner after an automatic open (bottom of the view; hides by
    /// itself after 12 s or on a click).</summary>
    readonly Label _toast = new() { Visible = false, AutoSize = false, Height = 30, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 10, 0),
        BackColor = Color.FromArgb(40, 120, 70), ForeColor = Color.White, Font = new Font(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont, FontStyle.Bold), Cursor = Cursors.Hand };
    readonly System.Windows.Forms.Timer _toastTimer = new() { Interval = 12000 };
    readonly ComboBox _setting = new() { Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly CheckBox[] _act1 = new CheckBox[3], _act2 = new CheckBox[3];
    readonly NumericUpDown _groupByte = new() { Width = 60, Minimum = 0, Maximum = 255 };
    readonly TextBox _name = new() { Width = 200, MaxLength = Blueprint.MaxNameChars };
    readonly Label _vehInfo = new() { AutoSize = true, MaximumSize = new Size(255, 0) };
    readonly ListBox _issues = new() { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true };
    readonly ToolStripButton _tSel, _tPlace, _tPaint, _undoBtn, _redoBtn, _saveGame, _each;
    readonly ToolStripButton _pregameBtn;
    bool _syncing;
    SplitContainer _inner = null!, _outer = null!;

    static NumericUpDown Num() => new() { Width = 50, Minimum = -255, Maximum = 510 };

    public VehicleEditorPanel()
    {
        // ---- toolbar
        _bar.Items.Add(new ToolStripButton("New", null, (_, _) => New()) { ToolTipText = "Start an empty vehicle" });
        _bar.Items.Add(new ToolStripButton("Open…", null, (_, _) => OpenDialog()) { ToolTipText = "Open an Xbox 360 vehicle save (package 0x0000000N or its extracted content file) or a blueprint .bin. You can also drop files here." });
        _pregameBtn = new ToolStripButton("Game Vehicles ▾") { ToolTipText = "The game's own vehicles: the open world's first, then World › Act › challenge (AI racers with their drivers, challenge and prize vehicles), then Other / templates (shop blueprints, demo, test, live). Search box, part counts." };
        _pregameBtn.Click += (_, _) => ShowPicker();
        // double-click / Enter / Open: opens (after Save / Don't save / Cancel when there are unsaved changes)
        _picker.Picked += (v, p) => { if (!(IsOpen(v, p) && !_doc.Dirty)) OpenPregame(v, p); };
        // single click / arrow keys with no unsaved changes: open at once, the picker stays open for the next one
        _picker.CanBrowse = () => !_doc.Dirty;
        _picker.Browsed += (v, p) => { if (!_doc.Dirty && !IsOpen(v, p)) OpenPregame(v, p); };
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
        _tSel = new ToolStripButton("Select (1)", null, (_, _) => SetTool(VehicleTool.Select)) { CheckOnClick = false, ToolTipText = "1 (or S): click parts to select (Ctrl/Shift: add); drag selected parts to move them in the plane facing you, cell by cell (looking down: across the ground; from the side or front: up / down and sideways; Shift: up / down only)" };
        _tPlace = new ToolStripButton("Place (2)", null, (_, _) => SetTool(VehicleTool.Place)) { ToolTipText = "2 (or P): place the part chosen in the library on the face under the mouse (R or a right click turns it first). You can also drag a part from the library into the view. Esc: back to Select" };
        _tPaint = new ToolStripButton("Paint (3)", null, (_, _) => SetTool(VehicleTool.Paint)) { ToolTipText = "3 (or B): click parts to paint them (Alt+click: pick a part's colour)" };
        _bar.Items.Add(_tSel); _bar.Items.Add(_tPlace); _bar.Items.Add(_tPaint);
        _bar.Items.Add(new ToolStripSeparator());
        _bar.Items.Add(new ToolStripButton("⟳R", null, (_, _) => RotateView(false)) { ToolTipText = "R: rotate 90° clockwise as you see it, about the world axis closest to the view direction (looking down: turns left / right; from the side or front: rolls). Shift+R: the other way" });
        foreach (var (t, ax) in new[] { ("⟲X", 0), ("⟲Y", 1), ("⟲Z", 2) })
            _bar.Items.Add(new ToolStripButton(t, null, (_, _) => Rotate(ax, 1)) { ToolTipText = $"Rotate 90° about the world {"XYZ"[ax]} axis (key {"XYZ"[ax]}; Shift: the other way)" });
        _each = new ToolStripButton("Each") { CheckOnClick = true, ToolTipText = "Rotate each selected part about its own cell (on), or the whole selection about its centre (off: \"Group\")" };
        _each.CheckedChanged += (_, _) => { _each.Text = _each.Checked ? "Each" : "Group"; SaveEditorSettings(); };
        _each.Text = "Group";
        _bar.Items.Add(_each);
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
        _lib.Columns.Add("Part", 180); _lib.Columns.Add("Size", 120); _lib.Columns.Add("Colour", 120);
        _lib.LargeImageList = _thumbs;
        _lib.TileSize = new Size(240, ThumbSize + 8);
        _lib.Resize += (_, _) => { int w = Math.Max(160, _lib.ClientSize.Width - 4); if (_lib.TileSize.Width != w) _lib.TileSize = new Size(w, ThumbSize + 8); };
        left.Controls.Add(_lib); left.Controls.Add(_libInfo); left.Controls.Add(_category); left.Controls.Add(_search);
        _search.TextChanged += (_, _) => FillLibrary();
        _category.SelectedIndexChanged += (_, _) => FillLibrary();
        _thumbTimer.Tick += (_, _) => ThumbStep();
        _lib.SelectedIndexChanged += (_, _) => LibrarySelected();
        _lib.DoubleClick += (_, _) => { if (LibPart() != null) SetTool(VehicleTool.Place); };
        // drag a part into the 3D view: ghost on the face under the mouse, placed on drop
        _lib.ItemDrag += (_, e) =>
        {
            if (e.Item is ListViewItem { Tag: PartInfo p })
            {
                _lib.SelectedItems.Clear(); ((ListViewItem)e.Item).Selected = true;
                _lib.DoDragDrop(new DataObject(VehicleViewport.PartFormat, p.Id.ToString("X8")), DragDropEffects.Copy);
            }
        };
        // the view's keys work while the library has the focus (after picking a part: R turns it, 1 / 2 / 3 switch tools)
        _lib.KeyDown += (_, e) =>
        {
            var k = e.KeyCode;
            if (k is Keys.R or Keys.X or Keys.Y or Keys.Z or Keys.D1 or Keys.D2 or Keys.D3 or Keys.NumPad1 or Keys.NumPad2 or Keys.NumPad3 or Keys.Escape && !e.Control && !e.Alt)
                if (HandleKey(e.KeyData, false)) { e.Handled = true; e.SuppressKeyPress = true; }
        };

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
        _partType.FormattingEnabled = true; _partType.DropDownWidth = 380; _partType.MaxDropDownItems = 24;
        _partType.Format += (_, e) => { if (e.ListItem is PartInfo pi) e.Value = $"{pi.Name}{(pi.Modded ? " ★" : "")}  · {pi.StoreCategory}"; };
        props.Controls.Add(Row(new Label { Text = "Cell X", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, _px, new Label { Text = "Y", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, _py, new Label { Text = "Z", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, _pz));
        props.Controls.Add(Row(new Label { Text = "Orientation", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, _orient));
        props.Controls.Add(H("Paint"));
        props.Controls.Add(_painted);
        props.Controls.Add(Row(_colourShow, _colourBtn, MkBtn("Default", () => PaintSel(null), "Back to the part's own colour (unpainted)")));
        props.Controls.Add(_palette);
        props.Controls.Add(new Label { Text = "Recent", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 2, 0, 0) });
        props.Controls.Add(_recent);
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
        viewHost.Controls.Add(_view); viewHost.Controls.Add(_where); viewHost.Controls.Add(issuesBox);
        _aiNote.Controls.Add(_aiText); _aiNote.Controls.Add(_aiSwap);
        viewHost.Controls.Add(_aiNote);
        _aiNote.BringToFront();
        viewHost.Controls.Add(_toast);
        _toast.BringToFront();
        void PlaceToast()
        {
            int tw = TextRenderer.MeasureText(_toast.Text, _toast.Font).Width + 24;
            _toast.Width = Math.Max(200, Math.Min(_view.Width - 16, tw));
            _toast.Height = tw > _toast.Width ? 48 : 30;
            _toast.Location = new Point(8, _view.Bottom - _toast.Height - 8);
        }
        viewHost.Layout += (_, _) => PlaceToast();
        _toast.TextChanged += (_, _) => PlaceToast();
        _toast.Click += (_, _) => _toast.Visible = false;
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); _toast.Visible = false; };
        // the tab shown for the first time in a workspace with nothing open: open a vehicle for the user
        VisibleChanged += (_, _) => { if (Visible) BeginAutoOpen(); };
        void PlaceNote() { _aiNote.Location = new Point(8, _where.Bottom + 8); }
        viewHost.Layout += (_, _) => PlaceNote();
        _aiSwap.LinkClicked += (_, _) => SwapToAi();
        // a click on the note hides it until another vehicle opens
        _aiText.Click += (_, _) => { _aiNoteHidden = true; _aiNote.Visible = false; };
        new ToolTip().SetToolTip(_aiText, "Click to hide this note (it comes back with the next AI vehicle)");
        var outer = _outer = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
        outer.Panel1.Controls.Add(side);
        outer.Panel2.Controls.Add(viewHost);
        _inner = side;
        _statusStrip.Items.Add(_status); _statusStrip.Items.Add(_source);
        Controls.Add(outer); Controls.Add(_bar); Controls.Add(_statusStrip);
        HandleCreated += (_, _) => BeginInvoke(() =>
        {
            try { outer.SplitterDistance = 320; side.SplitterDistance = Math.Max(220, side.Height * 62 / 100); } catch { }
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
            // the dialog's Custom colours start with the recent ones (0x00BBGGRR); whatever the user adds there comes back too
            using var cd = new ColorDialog { FullOpen = true, Color = Rgb(_view.PaintColour), AnyColor = true, CustomColors = _recentColours.Select(c => (int)((c >> 8 & 0xFF) << 16 | (c >> 16 & 0xFF) << 8 | c >> 24)).ToArray() };
            if (cd.ShowDialog(this) == DialogResult.OK)
            {
                _view.PaintColour = Rgba(cd.Color); _colourShow.BackColor = cd.Color;
                AddRecent(_view.PaintColour);
                if (_doc.Selection.Count > 0) PaintSel(_view.PaintColour);
            }
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
        _view.RotateGhostRequested += () => RotateView((ModifierKeys & Keys.Shift) != 0);
        _view.FilesDropped += f => BeginInvoke(() => Drop(f));
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
        LoadEditorSettings();
    }

    // ------------------------------------------------------------------ editor settings (recent colours, rotate mode)

    sealed class EditorSettings
    {
        public List<uint> RecentColours { get; set; } = new();
        public bool RotateEach { get; set; }
        /// <summary>Per workspace folder: the vehicle last opened in the editor (reopened when the tab is first shown).</summary>
        public Dictionary<string, LastVehicle> LastVehicles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A game vehicle (asset + the World / Act it was opened under) or a vehicle file (360 package, content file,
    /// blueprint, a vehicle in the vehicle saves).</summary>
    public sealed class LastVehicle { public string Asset { get; set; } = ""; public string World { get; set; } = ""; public string Act { get; set; } = ""; public string File { get; set; } = ""; }
    readonly Dictionary<string, LastVehicle> _lastVehicles = new(StringComparer.OrdinalIgnoreCase);
    static string SettingsPath => Path.Combine(NB.Core.Project.ProjectRegistry.DataDir, "vehicle-editor.json");
    bool _loadingSettings;

    void LoadEditorSettings()
    {
        _loadingSettings = true;
        try
        {
            if (File.Exists(SettingsPath) && System.Text.Json.JsonSerializer.Deserialize<EditorSettings>(File.ReadAllText(SettingsPath)) is { } s)
            {
                _recentColours.Clear(); _recentColours.AddRange(s.RecentColours.Take(RecentMax));
                _each.Checked = s.RotateEach;
                _lastVehicles.Clear(); foreach (var (k, v) in s.LastVehicles) _lastVehicles[k] = v;
            }
        }
        catch { }
        finally { _loadingSettings = false; }
        FillRecent();
    }

    void SaveEditorSettings()
    {
        if (_loadingSettings) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var es = new EditorSettings { RecentColours = _recentColours.ToList(), RotateEach = _each.Checked };
            foreach (var (k, v) in _lastVehicles) es.LastVehicles[k] = v;
            File.WriteAllText(SettingsPath, System.Text.Json.JsonSerializer.Serialize(es));
        }
        catch { }
    }

    static string WsKey(Workspace ws) { try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(ws.Root)); } catch { return ws.Root; } }

    /// <summary>Remembers the vehicle now open as the workspace's last one (kept in NB Studio's data folder, not in the
    /// workspace).</summary>
    void RememberLast()
    {
        if (_ws == null) return;
        LastVehicle? l = _target switch
        {
            Target.Game when _game != null => new LastVehicle { Asset = _game.Asset, World = _gamePlace?.World ?? "", Act = _gamePlace?.Act ?? "" },
            Target.File when _file?.Path != null => new LastVehicle { File = _file.Path },
            _ => null,
        };
        if (l == null) return;
        _lastVehicles[WsKey(_ws)] = l;
        SaveEditorSettings();
    }

    // ------------------------------------------------------------------ auto-open (first show of the tab in a workspace)

    bool _autoOpenTried, _autoOpenPending;
    /// <summary>Open game vehicles from the blueprints the background build read (timing tests switch it off).</summary>
    bool _useBpCache = true;
    /// <summary>Scripted runs (command-line options) do not open a vehicle by themselves unless --vehicle-autoopen asks.</summary>
    static readonly bool ScriptRun = Environment.GetCommandLineArgs().Skip(1).Any(a => a.StartsWith("--") && a != "--log");
    bool _autoOpenScript;

    /// <summary>
    /// The first time the tab is shown in a workspace with nothing open (and nothing unsaved): opens the vehicle last edited
    /// in this workspace if it still exists, else the first vehicle of the open world / Act (the picker's "Open world" order),
    /// else the first vehicle the picker lists, and says so in a banner over the view and in the log. Waits for the Game
    /// Vehicles list without blocking (it is being built in the background).
    /// </summary>
    void BeginAutoOpen()
    {
        if (_autoOpenTried || _ws == null || !Visible || (ScriptRun && !_autoOpenScript)) return;
        if (_target != Target.None || _doc.Dirty || _doc.Parts.Count > 0) { _autoOpenTried = true; return; }
        if (_gameCat == null)
        {
            _autoOpenPending = true;
            StartGameCatalog();
            if (_gameCat == null) { _status.Text = "Finding a vehicle to show you…"; return; }   // the list's continuation comes back here
        }
        _autoOpenTried = true; _autoOpenPending = false;
        string why;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // 1. the last vehicle edited in this workspace
        if (_lastVehicles.TryGetValue(WsKey(_ws), out var last))
        {
            if (last.File.Length > 0 && File.Exists(last.File))
            {
                OpenFiles(new[] { last.File });
                if (_target == Target.File) { Toast($"Opened {_doc.Name} ({Path.GetFileName(last.File)}) — the last vehicle you edited", sw); return; }
            }
            else if (last.Asset.Length > 0 && _gameCat!.FirstOrDefault(v => v.Asset == last.Asset) is { } lv)
            {
                var pl = lv.Places.FirstOrDefault(p => p.World == last.World && p.Act == last.Act) ?? lv.Places.FirstOrDefault();
                OpenPregame(lv, pl);
                if (_target == Target.Game) { Toast($"Opened {GameTitleWhere()} — the last vehicle you edited", sw); return; }
            }
        }
        // 2. the first vehicle of the open world / Act, 3. else the first one the picker lists
        (PregameVehicle V, VehiclePlace? P)? pick = null;
        if (WorldAct?.Invoke() is { } here && GameVehiclePicker.OpenWorldVehicles(_gameCat!, here) is { Count: > 0 } mine) { pick = (mine[0].V, mine[0].P); why = "the first vehicle of this world"; }
        else { pick = GameVehiclePicker.FirstListed(_gameCat!); why = "the first vehicle in Game Vehicles"; }
        if (pick is not { } p) return;
        OpenPregame(p.V, p.P);
        if (_target == Target.Game) Toast($"Opened {GameTitleWhere()} — {why}", sw);
    }

    /// <summary>"Thomas' vehicle (World of Sports › Act 2 Burnin' Rubber)".</summary>
    string GameTitleWhere()
    {
        var path = GamePath();
        int i = path.LastIndexOf(" › ", StringComparison.Ordinal);
        return i < 0 ? path : $"{path[(i + 3)..]} ({path[..i]})";
    }

    void Toast(string text, System.Diagnostics.Stopwatch sw)
    {
        _toast.Text = text + "   (click to hide)";
        _toast.Visible = true; _toast.BringToFront();
        _toastTimer.Stop(); _toastTimer.Start();
        Log?.Invoke($"Vehicle Editor: {text} (opened by itself when the tab was first shown, {sw.ElapsedMilliseconds} ms).");
        _status.Text = text + ". Game Vehicles ▾ lists them all.";
    }

    bool IsOpen(PregameVehicle v, VehiclePlace? p) => _target == Target.Game && _game?.Id == v.Id;

    /// <summary>A colour picked with Colour… goes to the front of the Recent row (last 8, no duplicates).</summary>
    void AddRecent(uint rgba)
    {
        rgba |= 0xFF;
        _recentColours.Remove(rgba);
        _recentColours.Insert(0, rgba);
        if (_recentColours.Count > RecentMax) _recentColours.RemoveRange(RecentMax, _recentColours.Count - RecentMax);
        FillRecent();
        SaveEditorSettings();
    }

    void FillRecent()
    {
        _recent.Controls.Clear();
        if (_recentColours.Count == 0) { _recent.Controls.Add(new Label { Text = "(colours you pick with Colour… show here)", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 4, 0, 0) }); return; }
        foreach (var c in _recentColours)
        {
            var b = new Button { Width = 20, Height = 20, BackColor = Rgb(c), FlatStyle = FlatStyle.Flat, Margin = new Padding(1), Tag = c };
            new ToolTip().SetToolTip(b, $"#{c >> 8:X6} (recent colour)");
            b.Click += (_, _) => { _view.PaintColour = (uint)b.Tag!; _colourShow.BackColor = b.BackColor; if (_doc.Selection.Count > 0) PaintSel(_view.PaintColour); };
            _recent.Controls.Add(b);
        }
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
        _lib.Items.Clear(); _lib.Groups.Clear(); _category.Items.Clear(); _thumbQueue.Clear(); _thumbTimer.Stop(); _gameCat = null; _gameTask = null;
        if (_target == Target.Game) { _target = Target.None; _game = null; _gamePlace = null; }
        UpdateSourceLabel();
        _autoOpenTried = false; _autoOpenPending = false; _toast.Visible = false;
        StartGameCatalog();
        StartPartCatalog();
        if (Visible) BeginAutoOpen();
    }

    /// <summary>
    /// The Game Vehicles list of the workspace: read from its cache when the bundles and the game text are unchanged (the
    /// key is their sizes and times), else built on another thread — it reads the bundles without touching NB Studio's
    /// caches (<see cref="PregameVehicles.ReadOnlyLoader"/>) — and cached for the next time.
    /// </summary>
    void StartGameCatalog()
    {
        var ws = _ws;
        if (ws == null || _gameCat != null || _gameTask != null) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _idx ??= AssetIndex.LoadOrBuild(ws);
            var idx = _idx;
            string key = PregameVehicles.CacheKey(ws, idx);
            if (PregameVehicles.LoadCache(ws, key) is { } cached)
            {
                _gameCat = cached; _gameState = $"{cached.Count} vehicles (from the workspace cache, {sw.ElapsedMilliseconds} ms)";
                RefreshPicker();
                // the cache has no blueprints: read them in the background too, so opening one does not read its bundle
                var pl = PregameVehicles.ReadOnlyLoader(ws, keep: 2);
                Task.Run(() => PregameVehicles.Prefetch(ws, cached, pl));
                return;
            }
            List<NB.Core.Project.ActEntry> acts;
            try { acts = NB.Core.Project.ActCatalog.Build(ws, idx); } catch { acts = new(); }
            var load = PregameVehicles.ReadOnlyLoader(ws);
            _gameState = "Listing the game's vehicles…";
            // back on the UI thread through its synchronization context, not through this panel's handle: the panel has
            // no handle until the Vehicle Editor tab is first shown, and the finished list was dropped before (the picker
            // then said "Listing the game's vehicles…" for ever)
            var ui = SynchronizationContext.Current != null ? TaskScheduler.FromCurrentSynchronizationContext() : TaskScheduler.Default;
            _gameBuild = Task.Run(() => PregameVehicles.Catalog(ws, idx, acts, load));
            _gameTask = _gameBuild.ContinueWith(t =>
            {
                if (IsDisposed || !ReferenceEquals(ws, _ws)) return;
                _gameTask = null;
                if (t.Status == TaskStatus.RanToCompletion)
                {
                    _gameCat = t.Result;
                    _gameState = $"{t.Result.Count} vehicles (listed in {sw.ElapsedMilliseconds} ms, cached for next time)";
                    try { PregameVehicles.SaveCache(ws, key, t.Result); } catch (Exception) { }
                }
                else _gameState = "The list could not be built in the background: " + t.Exception?.GetBaseException().Message;
                RefreshPicker();
                UpdateSourceLabel(); UpdateVehicleInfo();
                if (_autoOpenPending) BeginAutoOpen();
            }, ui);
        }
        catch (Exception e) { _gameState = "Game vehicles: " + e.Message; }
    }

    /// <summary>The list now (built here, with a wait cursor, when the background build could not run).</summary>
    List<PregameVehicle>? GameCatalog()
    {
        if (_gameCat != null || _ws == null) return _gameCat;
        if (_gameTask != null && _gameBuild != null)
        {
            // wait for the build itself (its continuation needs this thread, so waiting on that would block)
            try { if (_gameBuild.Wait(60000) && _gameBuild.Status == TaskStatus.RanToCompletion) { _gameCat = _gameBuild.Result; _gameState = $"{_gameCat.Count} vehicles"; return _gameCat; } } catch { }
        }
        var cur = Cursor.Current;
        Cursor.Current = Cursors.WaitCursor;
        try { _idx ??= AssetIndex.LoadOrBuild(_ws); _gameCat = PregameVehicles.Catalog(_ws, _idx); _gameState = $"{_gameCat.Count} vehicles"; }
        catch (Exception e) { Log?.Invoke("Vehicle Editor: game vehicles could not be listed: " + e.Message); }
        finally { Cursor.Current = cur; }
        return _gameCat;
    }

    void RefreshPicker()
    {
        var here = WorldAct?.Invoke();
        _picker.SetData(_gameCat, here, WorldLabel?.Invoke() ?? "", _gameState);
        _picker.SetHint(_gameState, _doc.Dirty);
    }

    /// <summary>Game Vehicles: the navigator window under the button.</summary>
    void ShowPicker()
    {
        if (_ws == null) { _status.Text = "Open a workspace first."; return; }
        StartGameCatalog();
        RefreshPicker();
        var f = FindForm();
        if (f != null && _picker.Owner != f) _picker.Owner = f;
        var b = _pregameBtn.Bounds;
        var at = _bar.PointToScreen(new Point(b.Left, b.Bottom));
        var scr = Screen.FromPoint(at).WorkingArea;
        _picker.Location = new Point(Math.Min(at.X, scr.Right - _picker.Width), Math.Min(at.Y, scr.Bottom - _picker.Height));
        _picker.Show();
        _picker.Activate();
    }

    Task<(PartCatalog Cat, NB.Core.Formats.CaffFile? Town)>? _catBuild;
    string _catTiming = "";

    /// <summary>The part catalog and every part's model, read on another thread when the workspace opens (read-only loader:
    /// NB Studio's caches are left alone), so the first vehicle opens without the second of loading them.</summary>
    void StartPartCatalog()
    {
        var ws = _ws;
        _catBuild = null;
        if (ws == null) return;
        try
        {
            _idx ??= AssetIndex.LoadOrBuild(ws);
            var idx = _idx;
            var load = PregameVehicles.ReadOnlyLoader(ws);
            _catBuild = Task.Run(() =>
            {
                var c = PartCatalog.Load(ws, idx, load);
                // the town bundle the texture lookup starts from
                NB.Core.Formats.CaffFile? town = null;
                try { town = load(0x234CEC); } catch { }
                return (c, town);
            });
            // then every part's model and its batches ready for the GPU (the first vehicle then only uploads them); the
            // editor can already use the catalog meanwhile (models are parsed under the catalog's lock)
            _catBuild.ContinueWith(t =>
            {
                if (t.Status != TaskStatus.RanToCompletion) return;
                var c = t.Result.Cat;
                foreach (var p in c.Parts.Values) try { if (c.Model(p) is { } m) VehicleRenderer.Prepare(m); } catch { }
            }, TaskScheduler.Default);
        }
        catch { _catBuild = null; }
    }

    /// <summary>Loads the part catalog (first use after a workspace opened): the background one when it is there (waiting
    /// for it if needed), else here.</summary>
    bool EnsureCatalog()
    {
        if (_cat != null) return true;
        if (_ws == null) { _status.Text = "Open a workspace first: the parts' models and colours come from the game files."; return false; }
        UseWaitCursor = true;
        try
        {
            var esw = System.Diagnostics.Stopwatch.StartNew();
            _idx ??= AssetIndex.LoadOrBuild(_ws);
            PartCatalog? pre = null; NB.Core.Formats.CaffFile? town = null;
            if (_catBuild != null)
            {
                try { if (_catBuild.Wait(120000) && _catBuild.Status == TaskStatus.RanToCompletion && ReferenceEquals(_catBuild.Result.Cat.Workspace, _ws)) (pre, town) = _catBuild.Result; } catch { }
                _catBuild = null;
            }
            _cat = pre ?? PartCatalog.Load(_ws, _idx);
            double tCat = esw.Elapsed.TotalMilliseconds;
            _tex = new TextureResolver(_ws, _idx, town ?? _ws.LoadResident(0x234CEC), 0x234CEC);
            double tTex = esw.Elapsed.TotalMilliseconds;
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
            // the library (part pictures, categories, part types) right after the vehicle shows, not before it
            if (IsHandleCreated) BeginInvoke(FillLibraryUi); else FillLibraryUi();
            Log?.Invoke(_catTiming = $"Vehicle Editor: {_cat.Parts.Count} parts ({_cat.Parts.Values.Count(p => p.Modded)} modded), {_cat.Palette.Count} garage colours " +
                        $"(ready in {esw.ElapsedMilliseconds} ms: parts {tCat:0}{(pre != null ? " from the background" : "")}, textures {tTex - tCat:0}).");
            return true;
        }
        catch (Exception e) { Log?.Invoke("Vehicle Editor: parts could not be loaded: " + e.Message); return false; }
        finally { UseWaitCursor = false; }
    }

    /// <summary>The parts library, the categories and the part-type list of the loaded catalog.</summary>
    void FillLibraryUi()
    {
        if (_cat == null) return;
        var lsw = System.Diagnostics.Stopwatch.StartNew();
        {
            // part pictures: a colour chip first, the rendered part as soon as the 3D view can draw it
            _thumbs.Images.Clear(); _thumbQueue.Clear();
            foreach (var p in Ordered(_cat.Parts.Values))
            {
                _thumbs.Images.Add(p.Id.ToString("X8"), Chip(p.DefaultPaint));
                _thumbQueue.Enqueue(p);
            }
            _thumbTimer.Start();
            _category.Items.Clear();
            _category.Items.Add(new CategoryItem("All categories", _cat.Parts.Count));
            foreach (var g in Ordered(_cat.Parts.Values).GroupBy(p => p.StoreCategory)) _category.Items.Add(new CategoryItem(g.Key, g.Count()));
            _category.Items.Add(new CategoryItem(AiPartsCategory, _cat.Parts.Values.Count(p => p.IsAiVariant)));
            _category.SelectedIndex = 0;
            _partType.Items.Clear();
            foreach (var p in Ordered(_cat.Parts.Values)) _partType.Items.Add(p);
            FillLibrary();
        }
        _libTiming = lsw.ElapsedMilliseconds;
    }
    long _libTiming;

    // ------------------------------------------------------------------ library

    PartInfo? LibPart() => _lib.SelectedItems.Count > 0 ? _lib.SelectedItems[0].Tag as PartInfo : null;

    /// <summary>Parts in the Parts Store's order: category (Seats … Accessories, Modded / ULTRA, Other), then small / standard
    /// before medium, large and super, then name.</summary>
    static IEnumerable<PartInfo> Ordered(IEnumerable<PartInfo> parts) => PartCatalog.StoreSorted(parts);

    static Bitmap Chip(uint rgba)
    {
        var bmp = new Bitmap(ThumbSize, ThumbSize, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(219, 224, 232));
        using var b = new SolidBrush(Rgb(rgba));
        g.FillRectangle(b, ThumbSize / 4, ThumbSize / 4, ThumbSize / 2, ThumbSize / 2);
        g.DrawRectangle(Pens.DimGray, ThumbSize / 4, ThumbSize / 4, ThumbSize / 2, ThumbSize / 2);
        return bmp;
    }

    /// <summary>Renders a few queued part pictures per tick (the 3D view's GL context, offscreen), so the library fills in
    /// without blocking the editor.</summary>
    void ThumbStep()
    {
        if (_cat == null || _thumbQueue.Count == 0) { _thumbTimer.Stop(); return; }
        if (!_view.GlReady || !_view.Visible) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool changed = false;
        while (_thumbQueue.Count > 0 && sw.ElapsedMilliseconds < 30)
        {
            var p = _thumbQueue.Dequeue();
            Bitmap? bmp = null;
            try { bmp = _view.PartThumbnail(p, ThumbSize); } catch (Exception e) { Log?.Invoke($"Vehicle Editor: picture of {p.Key}: {e.Message}"); }
            if (bmp == null) continue;
            int k = _thumbs.Images.IndexOfKey(p.Id.ToString("X8"));
            if (k >= 0) { _thumbs.Images[k] = bmp; changed = true; }
        }
        if (changed) _lib.Invalidate();
        if (_thumbQueue.Count == 0) _thumbTimer.Stop();
    }

    static string Cap(string s) => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s;
    const string AiPartsCategory = "AI parts (for AI drivers' vehicles)";

    void FillLibrary()
    {
        if (_cat == null) return;
        string q = _search.Text.Trim();
        string cat = (_category.SelectedItem as CategoryItem)?.Name ?? "All categories";
        var keep = LibPart();
        _lib.BeginUpdate();
        _lib.Items.Clear(); _lib.Groups.Clear();
        var groups = new Dictionary<string, ListViewGroup>();
        foreach (var p in Ordered(_cat.Parts.Values))
        {
            if (cat == AiPartsCategory ? !p.IsAiVariant : cat != "All categories" && p.StoreCategory != cat) continue;
            if (q.Length > 0 && !(p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || p.Key.Contains(q, StringComparison.OrdinalIgnoreCase)
                || p.StoreCategory.Contains(q, StringComparison.OrdinalIgnoreCase) || p.Class.Contains(q, StringComparison.OrdinalIgnoreCase))) continue;
            if (!groups.TryGetValue(p.StoreCategory, out var grp)) _lib.Groups.Add(groups[p.StoreCategory] = grp = new ListViewGroup(p.StoreCategory, p.StoreCategory));
            bool other = p.StoreCategory == PartCatalog.OtherCategory;
            var it = new ListViewItem(p.Name + (p.Modded ? " ★" : ""), p.Id.ToString("X8"), grp) { Tag = p, ToolTipText = $"{p.Name}\n{p.Description}\n({p.Key})" };
            it.SubItems.Add($"Size: {p.SizeText}   {p.Weight:0.#} kg");
            it.SubItems.Add(p.IsAiSeat ? "AI driver seat (not in the store)" : other || p.Modded ? p.Key : Cap(p.ColourName.Replace("colour_", "")));
            _lib.Items.Add(it);
            if (keep == p) it.Selected = true;
        }
        _lib.EndUpdate();
    }

    void LibrarySelected()
    {
        var p = LibPart();
        if (p == null) return;
        _libInfo.Text = $"{p.Name}{(p.Modded ? " (modded)" : "")} — {p.StoreCategory}\nSize: {p.SizeText} cells, {p.Weight:0.#} kg, colour {(p.ColourName.Length > 0 ? p.ColourName.Replace("colour_", "") : "none")}" +
            (p.IsAiSeat ? "\nAI DRIVER SEAT: the seat the game's AI racers drive from (players use the other seats)." : p.IsAiVariant ? "\nAI version: what the game's AI vehicles use (stronger than the player's)." : "") +
            (p.Description.Length > 0 ? $"\n{p.Description}" : "") + $"\n{p.Key}";
        // a new part starts the way the game's vehicles use it (springs face down)
        if (_view.PlacePart != p) _view.PlaceOrientation = p.DefaultOrientation;
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
            if (_picker.Visible) _picker.SetHint(_gameState, _doc.Dirty);
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
        bool ai = IsAiVehicle();
        if (ai && _cat != null) foreach (var i in _doc.ValidateAi(_cat)) _issues.Items.Add(new IssueItem(i));
        if (_issues.Items.Count == 0) _issues.Items.Add("✔ No problems found.");
        _issues.EndUpdate();
        UpdateAiNote(ai);
    }

    /// <summary>An AI driver's vehicle: a game vehicle an AI drives (a marker gives it a driver) or one with an AI seat.</summary>
    bool IsAiVehicle()
    {
        if (_cat != null && _doc.Parts.Any(p => _cat[p.B.Part]?.IsAiSeat == true)) return true;
        if (_target != Target.Game || _game == null) return false;
        var owner = _game.Owner.Length > 0 ? _game.Owner : _gameCat?.FirstOrDefault(x => x.Id == _game.Id)?.Owner ?? "";
        return owner.Length > 0;
    }

    /// <summary>The corner note over the view for AI drivers' vehicles, with "Use AI parts" when player parts that have an AI
    /// version are on it.</summary>
    bool _aiNoteHidden;

    void UpdateAiNote(bool ai)
    {
        if (!ai || _cat == null || _aiNoteHidden) { _aiNote.Visible = false; return; }
        var owner = _game?.Owner is { Length: > 0 } o ? o : _game != null ? _gameCat?.FirstOrDefault(x => x.Id == _game.Id)?.Owner ?? "" : "";
        int swaps = _doc.Parts.Count(p => _cat[p.B.Part]?.AiVersion is > 0);
        _aiText.Text = $"AI driver's vehicle{(owner.Length > 0 ? $" ({owner})" : "")}: the AI drives from the AI seat. The game's racers mostly use " +
                       "AI parts — Engine (AI), Jet (AI), Spring (AI) — stronger than the player's (+25 % power). Player parts are driven too.";
        _aiSwap.Text = swaps > 0 ? $"Use AI parts: swap {swaps} player part(s) for their AI versions" : "All parts with an AI version use it.";
        _aiSwap.Enabled = swaps > 0;
        _aiNote.Visible = true;
        _aiNote.BringToFront();
    }

    /// <summary>"Use AI parts": every player part that has an AI version gets it (one undo step).</summary>
    void SwapToAi()
    {
        if (_cat == null) return;
        var todo = _doc.Parts.Where(p => _cat[p.B.Part]?.AiVersion is > 0).ToList();
        if (todo.Count == 0) return;
        _doc.Begin($"use AI parts ({todo.Count})");
        foreach (var p in todo)
        {
            var ai = _cat[_cat[p.B.Part]!.AiVersion]!;
            p.B.Part = ai.Id; p.B.Category = (byte)(ai.Category + 1);
            if (p.B.Painted == 0) p.B.Paint = ai.DefaultPaint;
        }
        _doc.Commit();
        _status.Text = $"{todo.Count} part(s) now use their AI versions (Ctrl+Z undoes).";
    }

    // ------------------------------------------------------------------ Test in Xenia (MainForm.QuickTestPending)

    /// <summary>Unsaved edits of a game vehicle: the test plays the workspace, so F5 lists them in its "Save all and test /
    /// Test the last saved state / Cancel" question. (A 360 package or a new vehicle is not part of the workspace: Save /
    /// Save As.)</summary>
    public bool HasUnsaved => _target == Target.Game && _game != null && _doc.Dirty;
    public string UnsavedLabel => $"Vehicle Editor: {(_game != null ? GamePath() : "game vehicle")}";

    /// <summary>"Save all and test": the open game vehicle into the game (every bundle holding it). False when it was not
    /// saved (errors the user did not want to save, or the write failed).</summary>
    public bool SaveForTest()
    {
        if (!HasUnsaved) return true;
        SaveToGame();
        return !_doc.Dirty;
    }

    void UpdateSourceLabel()
    {
        _source.Text = _target switch
        {
            Target.File when _file != null => $"{_file.Kind}: {(_file.Path != null ? Path.GetFileName(_file.Path) : "")}{(_doc.Dirty ? " *" : "")}",
            Target.Game when _game != null => $"Game vehicle {GamePath()} ({_game.Short} in {string.Join(", ", _game.Bundles.Select(b => b.ToString("x6")))}){(_doc.Dirty ? " *" : "")}",
            _ => "New vehicle" + (_doc.Dirty ? " *" : ""),
        };
        _where.Text = _target switch
        {
            Target.Game when _game != null => GamePath() + (_doc.Dirty ? " *" : ""),
            Target.File when _file != null => (_file.Path != null ? Path.GetFileName(_file.Path) : _file.Kind.ToString()) + (_doc.Dirty ? " *" : ""),
            _ => "New vehicle" + (_doc.Dirty ? " *" : ""),
        };
        _whereTip.SetToolTip(_where, _target == Target.Game && _game != null ? $"{_game.Asset}\n{string.Join("\n", _game.Users)}" : _file?.Path ?? "");
        _saveGame.Enabled = _target == Target.Game && _game != null;
    }

    /// <summary>"World of Sports › Act 2 Burnin' Rubber › Mr. Fit's vehicle" for the open game vehicle.</summary>
    string GamePath()
    {
        if (_game == null) return "";
        var full = _gameCat?.FirstOrDefault(x => x.Id == _game.Id);
        var place = _gamePlace ?? _game.Places.FirstOrDefault() ?? full?.Places.FirstOrDefault();
        string where = place?.ToString() ?? (_game.Section.Length > 0 ? _game.Section : full?.Section ?? "");
        string owner = _game.Owner.Length > 0 ? _game.Owner : full?.Owner ?? "";
        string title = owner.Length > 0 ? $"{owner}{(owner.EndsWith('s') ? "'" : "'s")} vehicle" : _game.Short;
        return where.Length > 0 ? $"{where} › {title}" : title;
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
        // the Place tool turns the part being placed (its ghost), whatever is selected
        if (_view.Tool == VehicleTool.Place)
        {
            _view.PlaceOrientation = Orientations.Turn(_view.PlaceOrientation, axis, dir);
            _view.Redraw();
            return;
        }
        if (_doc.Selection.Count == 0) { _view.PlaceOrientation = Orientations.Turn(_view.PlaceOrientation, axis, dir); _view.Redraw(); return; }
        bool each = _each.Checked;
        _doc.Begin($"rotate {(each ? "each part" : "")} about {"XYZ"[axis]}".Replace("  ", " "));
        var sel = _doc.Selection.ToList();
        // Group: positions turn about the selection's centre cell (a single part turns in place); Each: every part turns
        // about its own cell
        int cx = (int)Math.Round(sel.Average(p => p.X)), cy = (int)Math.Round(sel.Average(p => p.Y)), cz = (int)Math.Round(sel.Average(p => p.Z));
        var turn = Orientations.Turn(0, axis, dir);
        foreach (var p in sel)
        {
            if (!each)
            {
                var (rx, ry, rz) = Orientations.Apply(turn, p.X - cx, p.Y - cy, p.Z - cz);
                p.X = cx + rx; p.Y = cy + ry; p.Z = cz + rz;
            }
            p.B.Orientation = Orientations.Turn(p.Orientation, axis, dir);
        }
        _doc.Commit();
    }

    /// <summary>R: 90° about the world axis closest to the view direction, clockwise as seen (Shift: anticlockwise).</summary>
    void RotateView(bool reverse)
    {
        var (ax, cw) = _view.ViewAxis();
        Rotate(ax, reverse ? -cw : cw);
        string how = ax == 1 ? "turned left / right (about the vertical Y axis: you look down or up)" : $"rolled about the {"XYZ"[ax]} axis (the one you look along)";
        _status.Text = $"{(_doc.Selection.Count > 0 && _view.Tool != VehicleTool.Place ? (_each.Checked && _doc.Selection.Count > 1 ? "Each selected part" : "Selection") : "Part to place")} {how}, 90° {(reverse ? "anticlockwise" : "clockwise")} as you see it. Shift+R turns the other way; X / Y / Z turn about a fixed axis.";
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
            case Keys.Y: Rotate(1, shift ? -1 : 1); return true;
            case Keys.R: RotateView(shift); return true;
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
            case Keys.P: case Keys.D2: case Keys.NumPad2: SetTool(VehicleTool.Place); return true;
            case Keys.B: case Keys.D3: case Keys.NumPad3: SetTool(VehicleTool.Paint); return true;
            case Keys.S: case Keys.D1: case Keys.NumPad1: SetTool(VehicleTool.Select); return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ open

    void New()
    {
        if (!ConfirmDiscard()) return;
        _target = Target.None; _file = null; _game = null; _gamePlace = null;
        Attach(new VehicleDocument());
        EnsureCatalog();
        UpdateSourceLabel();
    }

    /// <summary>Scripted test runs: no questions.</summary>
    bool _scripted;

    /// <summary>Scripted runs that want the question (--vehicle-prompts on): tests answer it with window messages.</summary>
    bool _scriptPrompts;

    /// <summary>Before something replaces the vehicle shown: with unsaved changes, Save / Don't save / Cancel (as NB Studio
    /// asks elsewhere). Yes saves where the vehicle came from (Save; a new vehicle asks where), No drops the changes.
    /// False: stay (Cancel, or the save did not happen).</summary>
    bool ConfirmDiscard(string? what = null)
    {
        if ((_scripted && !_scriptPrompts) || !_doc.Dirty) return true;
        string name = _target == Target.Game && _game != null ? GameTitleWhere() : _target == Target.File && _file?.Path != null ? Path.GetFileName(_file.Path) : "the new vehicle";
        var ans = MessageBox.Show(FindForm() ?? (IWin32Window)this, $"Save your changes to {name} before {what ?? "opening another vehicle"}?\n\nYes: save them\nNo: don't save (they are dropped)\nCancel: go back",
            "Vehicle Editor", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
        if (ans == DialogResult.Cancel) { _status.Text = "Cancelled: the vehicle with your changes stays open."; return false; }
        if (ans == DialogResult.Yes) { Save(); return !_doc.Dirty; }
        return true;
    }

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
            _target = Target.File; _file = v; _game = null; _gamePlace = null; _toast.Visible = false;
            Attach(VehicleDocument.From(v.Blueprint));
            RememberLast();
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
        if (!_scripted && _doc.Dirty && MessageBox.Show(this, $"Replace the vehicle shown with {Path.GetFileName(f)}?\n\nIt has unsaved changes. (Ctrl+Z after the drop brings them back.)",
                "Vehicle Editor", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
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

    /// <summary>Opens one of the game's vehicles (Save to Game writes it back into every bundle holding it).</summary>
    public void OpenPregame(PregameVehicle v, VehiclePlace? place = null)
    {
        if (_ws == null || !ConfirmDiscard($"opening {v.Title}")) return;
        try
        {
            EnsureCatalog();
            // the blueprint the background build read, while its bundle file is unchanged; else from the bundle
            var listed = _gameCat?.FirstOrDefault(x => x.Id == v.Id) ?? v;
            var bp = (_useBpCache ? PregameVehicles.Cached(_ws, listed) : null) ?? PregameVehicles.Load(_ws, v);
            _target = Target.Game; _game = v; _gamePlace = place; _file = null; _aiNoteHidden = false;
            _toast.Visible = false;
            Attach(VehicleDocument.From(bp));
            RememberLast();
            _picker.MarkOpen(v.Id);
            Log?.Invoke($"Vehicle Editor: game vehicle {GamePath()} ({v.Short}), {bp.Blocks.Count} parts (in {string.Join(", ", v.Bundles.Select(b => b.ToString("x6")))}). Drop an Xbox 360 vehicle here to replace it.");
            _status.Text = "Game vehicle: drop an Xbox 360 vehicle (package or content file) onto the editor to replace it, edit, then Save to Game.";
            UpdateSourceLabel();
        }
        catch (Exception e) { MessageBox.Show(this, e.Message, "Vehicle Editor", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    /// <summary>Opens a game vehicle by asset name (Assets tab, script hooks).</summary>
    public bool OpenPregameAsset(string name)
    {
        if (_idx == null) return false;
        // the listed vehicle (driver, World / Act) when the list is ready; else the bare asset (the header fills in later)
        PregameVehicle? v = _gameCat?.FirstOrDefault(x => x.Asset == name || x.Short == name);
        v ??= PregameVehicles.All(_idx).FirstOrDefault(x => x.Asset == name || x.Short == name);
        if (v == null) return false;
        OpenPregame(v);
        return true;
    }

    // ------------------------------------------------------------------ save

    /// <summary>The blueprint to save. <paramref name="player"/>: for a player's save (360 package, content file, vehicle
    /// saves, Xenia): a name field holding a game asset's 8-bit creator tag ("SalvyBob") is written as UTF-16BE, the way
    /// the game saves vehicles (readers that assumed UTF-16 showed "卡汝…"; Blueprint.DecodeName now reads both).</summary>
    Blueprint? CurrentBlueprint(bool player = false)
    {
        if (!_syncing && _name.Text != _doc.Name) { _doc.Begin("rename"); _doc.Name = _name.Text; _doc.Commit(); }
        try
        {
            var bp = _doc.ToBlueprint(_cat);
            if (player && bp.NameIsAscii) { bp = bp.Clone(); bp.Name = bp.Name; }
            return bp;
        }
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
                var bp = CurrentBlueprint(player: _file?.Kind != VehicleFileKind.Blueprint); if (bp == null) return;
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
        var bp = CurrentBlueprint(player: true); if (bp == null) return;
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
            _target = Target.File; _file = VehicleFile.Open(d.FileName); _game = null; _gamePlace = null; _doc.Source = _file.Blueprint.Clone();
            RememberLast();
            Saved();
        }
        catch (Exception e) { MessageBox.Show(this, e.Message, "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    void SaveAsFile(VehicleFileKind kind)
    {
        if (!ConfirmIssues(false)) return;
        var bp = CurrentBlueprint(player: kind == VehicleFileKind.Content); if (bp == null) return;
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
        var bp = CurrentBlueprint(player: true); if (bp == null) return;
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
        var bp = CurrentBlueprint(player: true); if (bp == null) return;
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
            // the listed copy follows (the next open does not read the bundle again)
            if (_gameCat?.FirstOrDefault(x => x.Id == _game.Id) is { } lv) { lv.Blueprint = bp.Clone(); lv.BlueprintStamp = PregameVehicles.Stamp(_ws, lv); lv.Parts = bp.Blocks.Count; }
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
            if (bp.NameIsAscii) { bp = bp.Clone(); bp.Name = bp.Name; }
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
            case "--vehicle-shot-sel":
            {
                var f = next();
                if (arg == "--vehicle-shot") { _doc.Selection.Clear(); SyncSelection(); }
                await Task.Delay(300); Application.DoEvents();
                using (var b = _view.Capture()) b.Save(f);
                var form = FindForm();
                if (form != null) { using var whole = new Bitmap(form.Width, form.Height); form.DrawToBitmap(whole, new Rectangle(0, 0, form.Width, form.Height)); using var g = Graphics.FromImage(whole); using var v = _view.Capture(); var off = new Size(form.Width - form.ClientSize.Width - 8, form.Height - form.ClientSize.Height - 8); g.DrawImage(v, form.RectangleToClient(_view.RectangleToScreen(_view.ClientRectangle)).Location + off); foreach (var ov in new Control[] { _aiNote, _toast }.Where(c => c.Visible)) { using var nb = new Bitmap(ov.Width, ov.Height); ov.DrawToBitmap(nb, new Rectangle(0, 0, nb.Width, nb.Height)); g.DrawImage(nb, form.RectangleToClient(ov.RectangleToScreen(ov.ClientRectangle)).Location + off); } whole.Save(Path.ChangeExtension(f, null) + "_ui.png"); }
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
            case "--vehicle-select": { var k = next(); SelectWhere((p, i) => Match(p, i, k)); log($"script: selected {_doc.Selection.Count} '{k}': {string.Join(", ", _doc.Selection.Select(p => $"{_cat?[p.B.Part]?.Key}@{p.X},{p.Y},{p.Z} o{p.Orientation}"))}"); break; }
            case "--vehicle-describe": { var k = next(); var p = _doc.Parts.FirstOrDefault(q => Match(q, _cat?[q.B.Part], k)); log("script: describe " + (p == null ? "none" : _view.Describe(p) + " || tooltip: " + _view.TipText(p).Replace("\n", " / "))); break; }
            case "--vehicle-mouse-hover":
            {
                // --vehicle-mouse-hover KEY: window-message mouse move onto the first KEY part; logs the status line and the tooltip
                var k = next(); var p = _doc.Parts.FirstOrDefault(q => Match(q, _cat?[q.B.Part], k));
                if (p == null) { log("script: no part " + k); break; }
                var (a, b) = VehicleDocument.Box(p, _cat?[p.B.Part]);
                var at = _view.ToScreen(new System.Numerics.Vector3((a.X + b.X) / 2f, (a.Y + b.Y) / 2f, (a.Z + b.Z) / 2f));
                if (at != null) _view.PostMove(at.Value);
                await Task.Delay(100); Application.DoEvents();
                log($"script: hover {k} at {at}: status: {_status.Text}"); break;
            }
            case "--vehicle-viewaxis": { var (ax, cw) = _view.ViewAxis(); log($"script: view axis {"XYZ"[ax]} clockwise {cw:+0;-0}, drag plane {_view.DragPlaneName()}"); break; }
            case "--vehicle-mouse-drag":
            {
                // --vehicle-mouse-drag KEY DX DY: window-message drag (left button) from the centre of the first KEY part to DX, DY pixels away
                var k = next(); int dx = int.Parse(next()), dy = int.Parse(next());
                var p = _doc.Parts.FirstOrDefault(q => Match(q, _cat?[q.B.Part], k));
                if (p == null) { log("script: no part " + k); break; }
                var (a, b) = VehicleDocument.Box(p, _cat?[p.B.Part]);
                var at = _view.ToScreen(new System.Numerics.Vector3((a.X + b.X) / 2f, (a.Y + b.Y) / 2f, (a.Z + b.Z) / 2f));
                if (at == null) { log("script: part off screen"); break; }
                var before = (p.X, p.Y, p.Z);
                if (!_doc.Selection.Contains(p)) { _view.PostClick(at.Value); }
                _view.PostDrag(at.Value, new Point(at.Value.X + dx, at.Value.Y + dy), 10);
                await Task.Delay(50); Application.DoEvents();
                log($"script: mouse drag {k} from {at.Value} by {dx},{dy} ({_view.DragPlaneName()}): {before} -> ({p.X}, {p.Y}, {p.Z}); status: {_status.Text}");
                break;
            }
            case "--vehicle-mouse-key": { var key = (Keys)Enum.Parse(typeof(Keys), next(), true); _view.PostKey(key); await Task.Delay(50); Application.DoEvents(); log($"script: key message {key}: {string.Join(", ", _doc.Selection.Select(p => $"{_cat?[p.B.Part]?.Key}@{p.X},{p.Y},{p.Z} o{p.Orientation} {OrientName(p.Orientation)}"))}; status: {_status.Text}"); break; }
            case "--vehicle-thumbs-wait":
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (_thumbQueue.Count > 0 && sw.ElapsedMilliseconds < 120000) { ThumbStep(); await Task.Delay(1); Application.DoEvents(); }
                log($"script: part pictures done in {sw.ElapsedMilliseconds} ms ({_thumbQueue.Count} left)"); break;
            }
            case "--vehicle-lib-cat":
            {
                var n = next();
                foreach (var o in _category.Items) if (o is CategoryItem c && c.Name.StartsWith(n, StringComparison.OrdinalIgnoreCase)) { _category.SelectedItem = c; break; }
                log($"script: library category {(_category.SelectedItem as CategoryItem)?.Name}: {_lib.Items.Count} parts: {string.Join(", ", _lib.Items.Cast<ListViewItem>().Select(i => i.Text))}"); break;
            }
            case "--vehicle-lib-search": { _search.Text = next(); log($"script: search '{_search.Text}': {string.Join(", ", _lib.Items.Cast<ListViewItem>().Select(i => $"{i.Group?.Header}: {i.Text}"))}"); break; }
            case "--vehicle-lib-dump":
                foreach (ListViewGroup g in _lib.Groups) log($"script: library {g.Header}: {string.Join(" | ", g.Items.Cast<ListViewItem>().Select(i => $"{i.Text} [{i.SubItems[1].Text}; {i.SubItems[2].Text}]"))}");
                break;
            case "--vehicle-parttypes": log("script: part types: " + string.Join(" | ", _partType.Items.Cast<PartInfo>().Select(pi => _partType.GetItemText(pi)))); break;
            case "--vehicle-picker":
            {
                // --vehicle-picker [SEARCH|-]: waits for the game vehicle list, opens the navigator, logs its tree
                var q = next();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (_gameCat == null && _gameTask != null && sw.ElapsedMilliseconds < 120000) { await Task.Delay(50); Application.DoEvents(); }
                ShowPicker(); _picker.SearchText = q == "-" ? "" : q; await Task.Delay(200); Application.DoEvents();
                log($"script: picker ({_gameState}; waited {sw.ElapsedMilliseconds} ms)");
                foreach (var l in _picker.Dump().Take(400)) log("script: picker " + l);
                break;
            }
            case "--vehicle-picker-shot":
            {
                // --vehicle-picker-shot PNG [VEHICLE]: selects VEHICLE (asset / title) in the open navigator and saves a picture of it
                var f = next(); var sel = next();
                if (sel != "-") log("script: picker select " + sel + " -> " + _picker.SelectVehicle(sel));
                await Task.Delay(200); Application.DoEvents();
                using (var b = new Bitmap(_picker.Width, _picker.Height)) { _picker.DrawToBitmap(b, new Rectangle(0, 0, _picker.Width, _picker.Height)); b.Save(f); }
                log("script: picker shot " + f); break;
            }
            case "--vehicle-picker-open": { var sel = next(); bool ok = _picker.SelectVehicle(sel); if (ok) _picker.ChooseSelected(); await Task.Delay(100); Application.DoEvents(); log($"script: picker open {sel}: {ok}; header {_where.Text}; ai note {(_aiNote.Visible ? _aiText.Text + " | " + _aiSwap.Text : "-")}"); break; }
            case "--vehicle-catalog-reset": { _gameCat = null; _gameTask = null; var sw = System.Diagnostics.Stopwatch.StartNew(); StartGameCatalog(); log($"script: catalog restart: {_gameState} ({sw.ElapsedMilliseconds} ms on the UI thread)"); break; }
            case "--vehicle-part-drag":
            {
                // --vehicle-part-drag KEY FX FY [drop]: a library part dragged over the view at that fraction of it (dropped with "drop")
                var k = next(); float fx = float.Parse(next(), CultureInfo.InvariantCulture), fy = float.Parse(next(), CultureInfo.InvariantCulture); bool drop = next() == "drop";
                var info = _cat?.Parts.Values.FirstOrDefault(p => p.Key == k);
                if (info == null) { log("script: no part " + k); break; }
                foreach (ListViewItem it in _lib.Items) if (it.Tag == info) it.Selected = true;
                var sz = _view.ViewSize;
                var (c, o, st) = _view.SimulatePartDrag(info, new Point((int)(sz.Width * fx), (int)(sz.Height * fy)), drop);
                log($"script: part drag {k} at {fx},{fy}: cell {c} orientation {o} {OrientName(o)} status {st}{(drop ? $"; placed -> {_doc.Parts.Count} parts, undo: {_doc.UndoLabel}" : "")}");
                break;
            }
            case "--vehicle-recent": { var c = uint.Parse(next().TrimStart('#'), NumberStyles.HexNumber) << 8 | 0xFF; AddRecent(c); log($"script: recent colours {string.Join(" ", _recentColours.Select(x => "#" + (x >> 8).ToString("X6")))} (saved in {SettingsPath})"); break; }
            case "--vehicle-each": { _each.Checked = next() == "1"; log("script: rotate mode " + _each.Text); break; }
            case "--vehicle-ai-swap": { SwapToAi(); log($"script: {_status.Text}; checks: {string.Join(" | ", _issues.Items.Cast<object>().Select(o => o.ToString()))}"); break; }
            case "--vehicle-batches": { var k = next(); var info = _cat?.Parts.Values.FirstOrDefault(p => p.Key == k); if (info != null) foreach (var l in _view.DescribePart(info)) log($"script: batch {k}: {l}"); break; }
            case "--vehicle-wheel-travel": { PartCatalog.WheelTravel = float.Parse(next(), CultureInfo.InvariantCulture); _view.ResetGpu(); log("script: wheel travel " + PartCatalog.WheelTravel); break; }
            case "--vehicle-mouse-rclick": { float fx = float.Parse(next(), CultureInfo.InvariantCulture), fy = float.Parse(next(), CultureInfo.InvariantCulture); var sz = _view.ViewSize; int o0 = _view.PlaceOrientation; _view.PostRightClick(new Point((int)(sz.Width * fx), (int)(sz.Height * fy))); await Task.Delay(50); Application.DoEvents(); log($"script: right click: ghost orientation {o0} -> {_view.PlaceOrientation} ({OrientName(_view.PlaceOrientation)}); status: {_status.Text}"); break; }
            case "--vehicle-lib-key":
            {
                // --vehicle-lib-key KEY: a key message to the parts library (the focus after picking a part there)
                [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
                var key = (Keys)Enum.Parse(typeof(Keys), next(), true); int o0 = _view.PlaceOrientation; var t0 = _view.Tool;
                _lib.Focus(); PostMessage(_lib.Handle, 0x100, (IntPtr)(int)key, (IntPtr)1); Application.DoEvents(); PostMessage(_lib.Handle, 0x101, (IntPtr)(int)key, unchecked((IntPtr)(int)0xC0000001)); Application.DoEvents();
                await Task.Delay(50); Application.DoEvents();
                log($"script: library key {key}: tool {t0} -> {_view.Tool}, ghost orientation {o0} -> {_view.PlaceOrientation}; library selection {LibPart()?.Key}");
                break;
            }
            case "--vehicle-state": log($"script: tool {_view.Tool}, place part {_view.PlacePart?.Key} o{_view.PlaceOrientation} ({OrientName(_view.PlaceOrientation)}), rotate {_each.Text}, selection {string.Join(", ", _doc.Selection.Select(p => $"{_cat?[p.B.Part]?.Key}@{p.X},{p.Y},{p.Z} o{p.Orientation}"))}, buttons: {_tSel.Text} / {_tPlace.Text} / {_tPaint.Text}"); break;
            case "--vehicle-open-timing":
            {
                // --vehicle-open-timing A,B,C: opens each game vehicle (asset short names) and logs the time to load, show and draw it
                foreach (var n in next().Split(','))
                {
                    var v = GameCatalog()?.FirstOrDefault(x => x.Short == n || x.Asset == n);
                    if (v == null) { log("script: no vehicle " + n); continue; }
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    OpenPregame(v, v.Places.FirstOrDefault());
                    double open = sw.Elapsed.TotalMilliseconds;
                    var msw = System.Diagnostics.Stopwatch.StartNew();
                    foreach (var pi in _doc.Parts.Select(p => _cat?[p.B.Part]).Where(p => p != null).Distinct()) _cat!.Model(pi!);
                    double models = msw.Elapsed.TotalMilliseconds, tex0 = VehicleRenderer.TextureLoadMs, bat0 = VehicleRenderer.BatchBuildMs;
                    double frame = _view.RenderNowMs();
                    if (_catTiming.Length > 0) { log("script: " + _catTiming); _catTiming = ""; }
                    if (_libTiming > 0) { log($"script: library filled right after (not part of the open): {_libTiming} ms"); _libTiming = 0; }
                    log($"script: open timing {n}: {_doc.Parts.Count} parts, open {open:0} ms, models {models:0} ms, first frame {frame:0} ms (textures {VehicleRenderer.TextureLoadMs - tex0:0} ms, buffers {VehicleRenderer.BatchBuildMs - bat0:0} ms), total {open + models + frame:0} ms");
                    Application.DoEvents();
                }
                break;
            }
            case "--vehicle-part-cache": { if (next() == "off") _catBuild = null; log("script: background part catalog " + (_catBuild == null ? "off" : "on")); break; }
            case "--vehicle-bp-cache": _useBpCache = next() == "on"; log("script: blueprint cache " + (_useBpCache ? "on" : "off (reads the bundles, as before round 5)")); break;
            case "--vehicle-autoopen":
            {
                // --vehicle-autoopen on: lets this scripted run open a vehicle by itself like a user's first show of the tab
                _autoOpenScript = next() == "on"; _autoOpenTried = false;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                BeginAutoOpen();
                while (_autoOpenPending && sw.ElapsedMilliseconds < 120000) { await Task.Delay(50); Application.DoEvents(); }
                log($"script: auto-open -> {(_toast.Visible ? _toast.Text : "(nothing opened)")}; target {_where.Text} ({sw.ElapsedMilliseconds} ms)");
                break;
            }
            case "--vehicle-forget-last": { if (_ws != null) _lastVehicles.Remove(WsKey(_ws)); SaveEditorSettings(); log("script: last vehicle forgotten"); break; }
            case "--vehicle-prompts": _scriptPrompts = next() == "on"; log("script: vehicle prompts " + (_scriptPrompts ? "on" : "off")); break;
            case "--vehicle-picker-click":
            {
                // --vehicle-picker-click VEHICLE single|double: a mouse click message on the vehicle's node in the open picker
                var q = next(); bool dbl = next() == "double";
                var before = _where.Text;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool ok = _picker.PostClick(q, dbl);
                await Task.Delay(400); Application.DoEvents();
                log($"script: picker {(dbl ? "double-" : "")}click {q}: {ok}; selected '{_picker.SelectedText}'; open: {before} -> {_where.Text} ({sw.ElapsedMilliseconds} ms incl. 400 ms wait); dirty {_doc.Dirty}; picker shown {_picker.Visible}");
                break;
            }
            case "--vehicle-picker-key":
            {
                // --vehicle-picker-key KEY N: N key messages to the picker's tree, 60 ms apart (holding a key), then waits
                var k = (Keys)Enum.Parse(typeof(Keys), next(), true); int n = int.Parse(next());
                int opens = 0; void Count(PregameVehicle a, VehiclePlace? b) => opens++;
                _picker.Browsed += Count;
                for (int i = 0; i < n; i++) { _picker.PostKey(k); await Task.Delay(60); Application.DoEvents(); }
                await Task.Delay(500); Application.DoEvents();
                _picker.Browsed -= Count;
                log($"script: picker key {k} x{n}: selected '{_picker.SelectedText}', opened {opens} time(s): {_where.Text}");
                break;
            }
            case "--vehicle-move-part": { var k = next(); SelectWhere((p, i) => Match(p, i, k)); Move(0, 1, 0); log($"script: moved '{k}' up: dirty {_doc.Dirty}"); break; }
            case "--vehicle-unsaved": log($"script: unsaved for Save All: {HasUnsaved} {(HasUnsaved ? UnsavedLabel : "")}; dirty {_doc.Dirty}"); break;
            case "--vehicle-game-path": { var n = next(); var v = GameCatalog()?.FirstOrDefault(x => x.Short == n || x.Asset == n); if (v != null) OpenPregame(v, v.Places.FirstOrDefault()); log($"script: game vehicle {n}: {(v == null ? "not found" : _doc.Parts.Count + " parts; header: " + _where.Text + "; status: " + _source.Text)}"); break; }
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
