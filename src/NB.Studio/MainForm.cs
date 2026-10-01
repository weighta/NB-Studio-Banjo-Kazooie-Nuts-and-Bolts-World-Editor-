using System.Diagnostics;
using System.Numerics;
using NB.Core.Formats;
using NB.Core.Models;
using NB.Core.Project;
using NB.Core.Textures;
using NB.Core.World;
using NB.Studio.Panels;
using NB.Studio.Viewport;

namespace NB.Studio;

public sealed class MainForm : Form
{
    Workspace? _ws;
    AssetIndex? _index;
    readonly Panels.StartPage _start;
    WorldScene? _scene;
    readonly Settings _settings = Settings.Load();

    readonly SceneViewport _view = new() { Dock = DockStyle.Fill };
    readonly ListBox _worlds = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    readonly TreeView _tree = new() { Dock = DockStyle.Fill, CheckBoxes = true, HideSelection = false };
    readonly TextBox _treeSearch = new() { Dock = DockStyle.Top, PlaceholderText = "Search objects…" };
    readonly AssetBrowserPanel _assets;
    readonly AssetPreviewPanel _preview;
    readonly TransformPanel _transform = new() { Dock = DockStyle.Fill };
    readonly TextPanel _text = new() { Dock = DockStyle.Fill };
    readonly AudioPanel _audio = new() { Dock = DockStyle.Fill };
    readonly VideoPanel _video = new() { Dock = DockStyle.Fill };
    readonly TagEditorPanel _tags = new() { Dock = DockStyle.Fill };
    readonly PartImporterPanel _parts = new() { Dock = DockStyle.Fill };
    readonly LivePanel _live;
    readonly TextBox _log = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, ReadOnly = true, WordWrap = false, Font = new Font("Consolas", 9) };
    readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly ToolStripProgressBar _progress = new() { Visible = false, Width = 200 };
    readonly TabControl _center = new() { Dock = DockStyle.Fill };
    readonly TabControl _right = new() { Dock = DockStyle.Fill };
    readonly ContextMenuStrip _objMenu = new();
    ToolStripMenuItem? _viewCollision;
    readonly Stack<(SceneObject Obj, Matrix4x4 Before, Matrix4x4 After)> _undo = new(), _redo = new();
    bool _syncingTree;

    public MainForm()
    {
        Text = "Nuts & Bolts Mod Tool";
        Width = 1600; Height = 950;
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = LoadIcon(); } catch { }
        KeyPreview = true;

        _assets = new AssetBrowserPanel { Dock = DockStyle.Fill };
        _preview = new AssetPreviewPanel { Dock = DockStyle.Fill };
        _live = new LivePanel(() =>
        {
            if (_ws == null) return null;
            var img = XexFile.Read(File.ReadAllBytes(_ws.Game.Xex)).GetImage();
            return img.AsSpan((int)(NB.Core.Live.XeniaLive.TextStart - 0x82000000), 64).ToArray();
        }) { Dock = DockStyle.Fill };
        _live.Log += s => BeginInvoke(() => Log(s));
        _live.ViewCameraPosition = () => _view.CameraPosition;
        _live.ShowInView += p => { _view.SetCamera(p + new Vector3(0, 45, -70), 0, -30); _center.SelectedIndex = 0; };

        // ---- layout
        var left = new TabControl { Dock = DockStyle.Fill };
        var tWorlds = new TabPage("Worlds"); tWorlds.Controls.Add(_worlds);
        var tScene = new TabPage("Scene"); tScene.Controls.Add(_tree); tScene.Controls.Add(_treeSearch);
        var tAssets = new TabPage("Assets"); tAssets.Controls.Add(_assets);
        left.TabPages.AddRange(new[] { tWorlds, tScene, tAssets });

        var c3d = new TabPage("3D View"); c3d.Controls.Add(_view);
        var cPrev = new TabPage("Asset Preview"); cPrev.Controls.Add(_preview);
        var cText = new TabPage("Text"); cText.Controls.Add(_text);
        var cAudio = new TabPage("Audio"); cAudio.Controls.Add(_audio);
        var cVideo = new TabPage("Video"); cVideo.Controls.Add(_video);
        var cParts = new TabPage("Part Importer"); cParts.Controls.Add(_parts);
        _center.TabPages.AddRange(new[] { c3d, cPrev, cText, cAudio, cVideo, cParts });

        var rProps = new TabPage("Properties"); rProps.Controls.Add(_transform);
        var rTags = new TabPage("Tag Editor"); rTags.Controls.Add(_tags);
        var rLive = new TabPage("Live (game)"); rLive.Controls.Add(_live);
        _right.TabPages.AddRange(new[] { rProps, rTags, rLive });

        var splitLR = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 320 };
        var splitCR = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 900 };
        var splitMain = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 720 };
        splitLR.Panel1.Controls.Add(left);
        splitLR.Panel2.Controls.Add(splitCR);
        splitCR.Panel1.Controls.Add(_center);
        splitCR.Panel2.Controls.Add(_right);
        splitMain.Panel1.Controls.Add(splitLR);
        splitMain.Panel2.Controls.Add(_log);

        var statusStrip = new StatusStrip();
        statusStrip.Items.AddRange(new ToolStripItem[] { _status, _progress });
        Controls.Add(splitMain);
        // start page over the work area until a workspace is open
        _start = new Panels.StartPage(_settings.LastWorkspace, _settings.AutoOpenLast);
        _start.OpenRequested += async () => { using var d = new FolderBrowserDialog { Description = "Workspace folder (contains workspace.json)" }; if (d.ShowDialog(this) == DialogResult.OK) await OpenWorkspace(d.SelectedPath); };
        _start.NewRequested += async () => await NewWorkspace();
        _start.WorkspaceRequested += async p => await OpenWorkspace(p);
        _start.AutoOpenChanged += on => { _settings.AutoOpenLast = on; _settings.Save(); };
        Controls.Add(_start); Controls.SetChildIndex(_start, 0);
        Controls.Add(BuildToolbar());
        var menu = BuildMenu(); MainMenuStrip = menu; Controls.Add(menu);
        Controls.Add(statusStrip);
        Load += (_, _) => { splitLR.SplitterDistance = 330; splitCR.FixedPanel = FixedPanel.Panel2; splitCR.SplitterDistance = Math.Max(400, splitCR.Width - 540); splitMain.SplitterDistance = Math.Max(300, splitMain.Height - 150); };

        // ---- wiring
        _worlds.DoubleClick += async (_, _) => { if (_worlds.SelectedItem is WorldItem wi) await OpenWorld(wi.Entry, wi.Act); };
        _view.SelectionChanged += OnSelection;
        _view.TextureSource = n => _scene?.LoadTexture(n);
        _view.EditStarted += (o, before) => _pendingBefore = before;
        _view.ObjectEdited += o => PushUndo(o, _pendingBefore, o.Transform);
        _view.ContextMenuRequested += (o, p) => { if (o != null) { BuildObjectMenu(o); _objMenu.Show(_view, p); } };
        _transform.TransformChanged += (o, before) => { PushUndo(o, before, o.Transform); _view.Refresh3D(); UpdateTitle(); };
        _transform.LinkChanged += (o, before) => { _view.Refresh3D(); UpdateTitle(); Log($"{o.Name}: next path node {before} -> {o.Marker!.Link} (World > Save to write it)"); };
        _tree.AfterSelect += (_, e) => { if (!_syncingTree && e.Node?.Tag is SceneObject o) _view.Select(o, focus: true); };
        _tree.AfterCheck += (_, e) => { if (e.Node?.Tag is SceneObject o) { o.Visible = e.Node.Checked; _view.Refresh3D(); } else if (e.Action != TreeViewAction.Unknown && e.Node != null) foreach (TreeNode c in e.Node.Nodes) c.Checked = e.Node.Checked; };
        _tree.NodeMouseClick += (_, e) => { if (e.Button == MouseButtons.Right && e.Node.Tag is SceneObject o) { _tree.SelectedNode = e.Node; BuildObjectMenu(o); _objMenu.Show(_tree, e.Location); } };
        _treeSearch.TextChanged += (_, _) => FillTree();
        _assets.AssetActivated += e => { _center.SelectedIndex = 1; _preview.Show(_ws!, e, Log); _tags.ShowAsset(_ws!, e); };
        _preview.Log = Log;
        _tags.Log = Log;
        _text.Log = Log; _audio.Log = Log; _video.Log = Log; _parts.Log = Log;
        _audio.VgmstreamPath = FindUp(Path.Combine("thirdparty", "vgmstream", "vgmstream-cli.exe"));
        _tags.Changed += () => UpdateTitle();

        Shown += async (_, _) =>
        {
            Log("Nuts & Bolts Mod Tool — open or create a workspace to begin (File menu).");
            var args = Environment.GetCommandLineArgs().Skip(1).ToList();
            if (args.Count > 0) { await RunScript(args); return; }
            if (_settings.AutoOpenLast && _settings.LastWorkspace != null && File.Exists(Path.Combine(_settings.LastWorkspace, "workspace.json")))
                await OpenWorkspace(_settings.LastWorkspace);
        };
        FormClosing += (_, e) =>
        {
            if (_scene != null && _scene.Objects.Any(o => o.Dirty) &&
                MessageBox.Show(this, "There are unsaved world edits. Quit anyway?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) e.Cancel = true;
        };
    }

    Matrix4x4 _pendingBefore;

    /// <summary>Finds a file relative to the executable or any parent folder (for bundled third-party tools).</summary>
    static string? FindUp(string rel)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null) { var p = Path.Combine(d.FullName, rel); if (File.Exists(p)) return p; d = d.Parent; }
        return null;
    }

    static Icon LoadIcon()
    {
        var p = Path.Combine(AppContext.BaseDirectory, "Assets", "nut.png");
        using var bmp = new Bitmap(p);
        return Icon.FromHandle(new Bitmap(bmp, 64, 64).GetHicon());
    }

    // ------------------------------------------------------------------ menus

    MenuStrip BuildMenu()
    {
        var ms = new MenuStrip();
        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add("&New Workspace from Game Directory…", null, async (_, _) => await NewWorkspace());
        file.DropDownItems.Add("&Open Workspace…", null, async (_, _) => { using var d = new FolderBrowserDialog { Description = "Workspace folder (contains workspace.json)" }; if (d.ShowDialog(this) == DialogResult.OK) await OpenWorkspace(d.SelectedPath); });
        file.DropDownItems.Add("Validate Original Game Directory…", null, (_, _) => ValidateOriginal());
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("E&xit", null, (_, _) => Close());

        var edit = new ToolStripMenuItem("&Edit");
        edit.DropDownItems.Add(new ToolStripMenuItem("&Undo", null, (_, _) => Undo(), Keys.Control | Keys.Z));
        edit.DropDownItems.Add(new ToolStripMenuItem("&Redo", null, (_, _) => Redo(), Keys.Control | Keys.Y));
        edit.DropDownItems.Add(new ToolStripMenuItem("Undo Last &Bundle Save (import / duplicate / delete)", null, async (_, _) => await UndoLastBundleSave()));
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add(new ToolStripMenuItem("Reset Selected Transform", null, (_, _) => { if (_view.Selected is { } o) ResetTransform(o); }));

        var world = new ToolStripMenuItem("&World");
        world.DropDownItems.Add(new ToolStripMenuItem("&Save World Changes to Workspace", null, (_, _) => SaveWorld(), Keys.Control | Keys.S));
        world.DropDownItems.Add("Texture &Library (all textures of this world)…", null, (_, _) => OpenTextureLibrary(null));
        world.DropDownItems.Add("Export Whole Scene as OBJ…", null, (_, _) => ExportScene());
        world.DropDownItems.Add("Export Scene Placement List (CSV)…", null, (_, _) => ExportPlacements());

        var view = new ToolStripMenuItem("&View");
        var vT = new ToolStripMenuItem("Terrain") { Checked = true, CheckOnClick = true }; vT.CheckedChanged += (_, _) => { _view.ShowTerrain = vT.Checked; _view.Refresh3D(); };
        var vS = new ToolStripMenuItem("Scenery") { Checked = true, CheckOnClick = true }; vS.CheckedChanged += (_, _) => { _view.ShowScenery = vS.Checked; _view.Refresh3D(); };
        var vX = new ToolStripMenuItem("Textures") { Checked = true, CheckOnClick = true }; vX.CheckedChanged += (_, _) => { _view.Textured = vX.Checked; _view.Refresh3D(); };
        var vW = new ToolStripMenuItem("Wireframe") { CheckOnClick = true }; vW.CheckedChanged += (_, _) => _view.Wireframe = vW.Checked;
        var vM = new ToolStripMenuItem("Markers (actors, pickups, paths)") { Checked = true, CheckOnClick = true }; vM.CheckedChanged += (_, _) => { _view.ShowMarkers = vM.Checked; _view.Refresh3D(); };
        var vC = new ToolStripMenuItem("Collision (Havok)") { CheckOnClick = true, ToolTipText = "Wireframe of the Havok collision: terrain cyan, scenery yellow" };
        vC.CheckedChanged += (_, _) => { _ = ToggleCollision(vC.Checked); };
        _viewCollision = vC;
        var vP = new ToolStripMenuItem("Paths (path-node links)") { Checked = true, CheckOnClick = true };
        vP.CheckedChanged += (_, _) => { _view.ShowPaths = vP.Checked; _view.Refresh3D(); };
        view.DropDownItems.AddRange(new ToolStripItem[] { vT, vS, vM, vP, vC, vX, vW });

        var build = new ToolStripMenuItem("&Build");
        build.DropDownItems.Add("&Validate Workspace", null, async (_, _) => await ValidateWorkspace());
        build.DropDownItems.Add("&Modified Files and Change Log…", null, (_, _) => ShowChanges());
        build.DropDownItems.Add("Revert a Modified File…", null, (_, _) => RevertFile());
        build.DropDownItems.Add(new ToolStripSeparator());
        build.DropDownItems.Add("Export Playable Game Directory…", null, async (_, _) => await ExportGame(false));
        build.DropDownItems.Add("Export Mod Package (changed files only)…", null, async (_, _) => await ExportGame(true));
        build.DropDownItems.Add("Export for Console (RGH/JTAG): game directory with mods built into default.xex…", null, async (_, _) => await ExportGame(false, console: true));
        build.DropDownItems.Add(new ToolStripSeparator());
        build.DropDownItems.Add("Build Scene from JSON… (custom world: terrain, models, textures, water, markers)", null, async (_, _) => await BuildScene(null));
        build.DropDownItems.Add("Create Distributable Patch (.nbpatch)…", null, async (_, _) => await CreatePatch());
        build.DropDownItems.Add("Apply Patch to a Game Directory…", null, async (_, _) => await ApplyPatch());
        build.DropDownItems.Add("Roll Back Patches in a Game Directory…", null, (_, _) => RollbackPatch());
        build.DropDownItems.Add("Show Patch History of a Game Directory…", null, (_, _) => ShowPatchHistory());
        build.DropDownItems.Add(new ToolStripSeparator());
        build.DropDownItems.Add(new ToolStripMenuItem("&Launch Workspace in Xenia", null, (_, _) => LaunchXenia(), Keys.F5));
        build.DropDownItems.Add("Set Xenia Executable…", null, (_, _) => PickXenia());

        var tools = new ToolStripMenuItem("&Tools");
        tools.DropDownItems.Add("Test Mode: Skip Intro (new game → Showdown Town)", null, (_, _) =>
        {
            if (_ws == null) return;
            if (MessageBox.Show(this, "This edits the start-of-game script in the WORKSPACE (common bundle 685374):\n• removes the opening cutscenes and the Spiral Mountain tutorial\n• pre-sets Showdown Town intro steps 1–7\n\nA NEW GAME then starts directly in Showdown Town. Use Build > Revert to undo.", "Test mode", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            try { Log(NB.Core.World.TestMode.SkipStartOfGame(_ws, keepShowdownTownIntro: false)); Log(NB.Core.World.TestMode.PresetTownIntro(_ws)); }
            catch (Exception e) { Error("Test mode patch failed", e); }
        });
        var startIn = new ToolStripMenuItem("Test Mode: Start New Game In");
        startIn.DropDownOpening += (_, _) =>
        {
            startIn.DropDownItems.Clear();
            void Add(string label, string script) => startIn.DropDownItems.Add(label, null, (_, _) =>
            {
                if (_ws == null) return;
                try { Log("Test mode: " + NB.Core.World.TestMode.StartIn(_ws, script) + " — start a NEW GAME in Xenia (intro cutscenes can be skipped with Y)."); }
                catch (Exception e) { Error("Test mode patch failed", e); }
            });
            Add("Showdown Town (default)", "aid_script_banjox_showdowntown_midday");
            foreach (var a in _acts) Add(a.Display, a.Script);
        };
        startIn.DropDownItems.Add("(open a workspace first)");
        tools.DropDownItems.Add(startIn);
        tools.DropDownItems.Add("Rebuild Asset Index", null, async (_, _) => { if (_ws != null) await LoadIndex(true); });
        tools.DropDownItems.Add("Decompress an xcompress (0FF512ED) File…", null, (_, _) => DecompressFile());
        tools.DropDownItems.Add("Executable (default.xex) Info / Extract PE…", null, (_, _) => XexInfo());
        tools.DropDownItems.Add("Bulk Export Assets (current Asset filter)…", null, async (_, _) => { _busy = true; try { await _assets.BulkExport(this, _ws, Log, SetProgress); } finally { _busy = false; SetProgress(null, 0); } });

        var mods = new ToolStripMenuItem("&Mods");
        mods.DropDownOpening += (_, _) =>
        {
            mods.DropDownItems.Clear();
            mods.DropDownItems.Add(new ToolStripLabel("After-Party mods (executable patches, saved in the workspace and baked into default.xex by Create Patch)") { Font = new Font(Font, FontStyle.Italic) });
            foreach (var m in NB.Core.Mods.ExePatches.All)
            {
                if (NB.Core.Mods.ExePatches.FamilyOf(m.Id).Length > 0) continue;   // part limit / build area: set by value below
                var item = new ToolStripMenuItem(m.Name.Replace("&", "&&")) { CheckOnClick = true, Checked = _ws?.Manifest.ExeMods.Contains(m.Id) == true, Enabled = _ws != null, ToolTipText = m.Description + "\n\n" + m.Verified };
                item.Click += (_, _) =>
                {
                    if (_ws == null) return;
                    _ws.Manifest.ExeMods.Remove(m.Id);
                    if (item.Checked) _ws.Manifest.ExeMods.Add(m.Id);
                    _ws.SaveManifest();
                    ApplyExeMods();
                };
                mods.DropDownItems.Add(item);
            }
            string? famNow(string fam) => _ws?.Manifest.ExeMods.LastOrDefault(i => NB.Core.Mods.ExePatches.FamilyOf(i) == fam);
            string modNow(string fam, string stock) { var i = famNow(fam); return i == null ? stock + " (stock)" : NB.Core.Mods.ExePatches.Resolve(i)?.Name ?? i; }
            mods.DropDownItems.Add(new ToolStripMenuItem($"Vehicle part limit… (now: {modNow("vehicle-part-limit", "250")})", null, (_, _) => EditExeLimit("vehicle-part-limit", "Vehicle part limit",
                "Maximum number of parts in one vehicle (stock 250). 400 is the fully verified setting; other values use the same patch with scaled limits and stack frames.", 250, 251, 2000, 400))
                { Enabled = _ws != null, ToolTipText = NB.Core.Mods.ExePatches.VehiclePartLimit400.Description });
            mods.DropDownItems.Add(new ToolStripMenuItem($"Garage build area… (now: {modNow("garage-build-area", "19 cells")})", null, (_, _) => EditExeLimit("garage-build-area", "Garage build area",
                "Cells per axis a vehicle may span in Mumbo's Motors (stock 19). 31 is verified; the garage room fits about 60.", 19, 20, 60, 31))
                { Enabled = _ws != null, ToolTipText = NB.Core.Mods.ExePatches.GarageBuildArea31.Description });
            mods.DropDownItems.Add(new ToolStripSeparator());
            mods.DropDownItems.Add(new ToolStripLabel("Data settings (written into the workspace bundles)") { Font = new Font(Font, FontStyle.Italic) });
            foreach (var s in NB.Core.Mods.DataMods.All)
            {
                float? cur = _ws != null && _index != null ? NB.Core.Mods.DataMods.Get(_ws, _index, s) : null;
                mods.DropDownItems.Add(new ToolStripMenuItem($"{s.Name}… (now {(cur?.ToString("G6") ?? "?")}, default {s.Default:G6})", null, (_, _) => EditDataSetting(s))
                    { Enabled = _ws != null && _index != null, ToolTipText = s.Description + "\n\n" + s.Verified });
            }
            mods.DropDownItems.Add(new ToolStripSeparator());
            mods.DropDownItems.Add("Show mod details…", null, (_, _) => MessageBox.Show(this, string.Join("\n\n", NB.Core.Mods.ExePatches.All.Select(m =>
                $"{m.Name}\n{m.Description}\n{m.Verified}\n" + string.Join("\n", m.Words.Select(w => $"  0x{w.Address:X8}: 0x{w.Original:X8} → 0x{w.Patched:X8}  {w.Comment}")))), "After-Party mods"));
        };
        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add("Controls", null, (_, _) => MessageBox.Show(this,
            "3D view:\n  Right-drag: look    WASD / Q E: fly (Shift = fast)    Wheel: dolly    Middle-drag: pan\n  Left-click: select    F: focus selection    Esc: deselect\n  1 / 2 / 3: move / rotate / scale mode; drag the selected object with the left button.\n  Hold X, Y or Z while dragging to constrain to that axis.\n  Right-click an object for its context menu.\n\nEdits are held in memory until World > Save (Ctrl+S) writes the bundle into the workspace.", "Controls"));
        help.DropDownItems.Add("File Format Notes (docs)", null, (_, _) => OpenDocs());
        ms.Items.AddRange(new ToolStripItem[] { file, edit, world, view, build, tools, mods, help });
        return ms;
    }

    ToolStrip BuildToolbar()
    {
        var ts = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
        ToolStripButton Mode(string t, GizmoMode m) { var b = new ToolStripButton(t) { CheckOnClick = true, Checked = m == _view.Mode }; b.Click += (_, _) => { _view.Mode = m; foreach (var i in ts.Items.OfType<ToolStripButton>().Where(x => x.Tag as string == "mode")) i.Checked = i == b; }; b.Tag = "mode"; return b; }
        ts.Items.Add(Mode("Select", GizmoMode.Select));
        ts.Items.Add(Mode("Move (1)", GizmoMode.Move));
        ts.Items.Add(Mode("Rotate (2)", GizmoMode.Rotate));
        ts.Items.Add(Mode("Scale (3)", GizmoMode.Scale));
        ts.Items.Add(new ToolStripSeparator());
        ts.Items.Add(new ToolStripButton("Save World (Ctrl+S)", null, (_, _) => SaveWorld()));
        ts.Items.Add(new ToolStripButton("Launch in Xenia (F5)", null, (_, _) => LaunchXenia()));
        return ts;
    }

    void BuildObjectMenu(SceneObject o)
    {
        _objMenu.Items.Clear();
        _objMenu.Items.Add(new ToolStripLabel(o.Name) { Font = new Font(Font, FontStyle.Bold) });
        _objMenu.Items.Add("Focus Camera on Object", null, (_, _) => _view.Focus(o));
        _objMenu.Items.Add("Edit Properties", null, (_, _) => { _view.Select(o); _right.SelectedIndex = 1; });
        _objMenu.Items.Add("Export Model as OBJ…", null, (_, _) => ExportObject(o, false));
        _objMenu.Items.Add("Export Model as OBJ (with world transform)…", null, (_, _) => ExportObject(o, true));
        _objMenu.Items.Add("Export Model as FBX…", null, (_, _) => ExportObject(o, false, fbx: true));
        _objMenu.Items.Add("Export Model as FBX (with world transform)…", null, (_, _) => ExportObject(o, true, fbx: true));
        _objMenu.Items.Add(new ToolStripSeparator());
        var reset = _objMenu.Items.Add("Reset Transform", null, (_, _) => ResetTransform(o)); reset.Enabled = o.Kind != SceneObjectKind.Terrain;
        _objMenu.Items.Add(new ToolStripMenuItem("Hide in Editor", null, (_, _) => { o.Visible = false; FillTree(); _view.Refresh3D(); }));
        _objMenu.Items.Add(new ToolStripSeparator());
        var imp = _objMenu.Items.Add("Import Model (replace geometry with OBJ/FBX)…", null, async (_, _) => await ImportModel(o));
        imp.Enabled = o.Kind == SceneObjectKind.Scenery;
        imp.ToolTipText = "Replaces this object's reference model (all its instances) with an OBJ/FBX, including its materials: each material's texture is imported into this world.";
        var texl = _objMenu.Items.Add("Textures… (view / export / replace)", null, (_, _) => OpenTextureLibrary(o));
        texl.Enabled = o.Model != null;
        texl.ToolTipText = "Texture library of this model: every texture it uses, export as PNG, open in another viewer, edit and apply back, replace (everywhere or for this model only).";
        var col = _objMenu.Items.Add("Import Collision (OBJ/FBX)…", null, async (_, _) => await ImportCollision(o));
        string? colAsset = o.Kind == SceneObjectKind.Scenery ? CollisionAssetOf(o) : null;
        col.Enabled = colAsset != null;
        col.ToolTipText = colAsset != null
            ? $"Replaces the Havok collision mesh {colAsset} (all instances of this model) with the triangles of an OBJ/FBX, or with the box around it; a new MOPP tree is built."
            : "This object's model has no replaceable mesh collision in this bundle (none, or breakable scenery with physics pieces, which is not supported).";
        var dup = _objMenu.Items.Add("Duplicate", null, async (_, _) => await DuplicateObject(o, new Vector3(2, 0, 0)));
        dup.Enabled = o.Kind == SceneObjectKind.Scenery;
        dup.ToolTipText = "Adds a new scenery instance (copy of this one, 2 units along X). Verified in Xenia.";
        var del = _objMenu.Items.Add("Delete", null, async (_, _) => await DeleteObject(o));
        del.Enabled = o.Kind == SceneObjectKind.Scenery;
        del.ToolTipText = "Removes the instance from the playable world (zero scale, moved far below the level). Instance indices are kept because the game refers to them.";
    }

    // ------------------------------------------------------------------ workspace

    async Task NewWorkspace()
    {
        using var src = new FolderBrowserDialog { Description = "Select the ORIGINAL (unmodified) game directory — it will only be read" };
        if (src.ShowDialog(this) != DialogResult.OK) return;
        var rep = new GameDirectory(src.SelectedPath).Validate();
        Log("Validating " + src.SelectedPath); foreach (var i in rep.Info) Log("  " + i); foreach (var e in rep.Errors) Log("  ERROR: " + e);
        if (!rep.Ok) { MessageBox.Show(this, "Not a valid Nuts & Bolts directory:\n" + string.Join("\n", rep.Errors), Text); return; }
        using var dst = new FolderBrowserDialog { Description = "Select an EMPTY folder for the new workspace (a full copy of the game goes into <folder>\\game)" };
        if (dst.ShowDialog(this) != DialogResult.OK) return;
        _busy = true; SetProgress("Copying game files…", 0);
        try
        {
            await Task.Run(() => Workspace.Create(src.SelectedPath, dst.SelectedPath, new Progress<(string F, double P)>(p => BeginInvoke(() => SetProgress("Copying " + p.F, p.P)))));
            await OpenWorkspace(dst.SelectedPath);
        }
        catch (Exception e) { Error("Creating workspace failed", e); }
        finally { _busy = false; SetProgress(null, 0); }
    }

    async Task OpenWorkspace(string root)
    {
        try
        {
            _start.SetStatus($"Opening {Path.GetFileName(root.TrimEnd('\\', '/'))}…"); Application.DoEvents();
            _ws = Workspace.Open(root);
            _settings.LastWorkspace = root; _settings.Save();
            Log($"Workspace: {_ws.Root}\n  original (read-only): {_ws.Original.Root}\n  changes logged: {_ws.Manifest.Changes.Count}");
            await LoadIndex(false);
            UpdateTitle();
            _start.Visible = false;
        }
        catch (Exception e) { _start.SetStatus(""); Error("Opening workspace failed", e); }
    }

    async Task LoadIndex(bool rebuild)
    {
        if (_ws == null) return;
        _busy = true;
        SetProgress(rebuild ? "Rebuilding asset index (decompresses every bundle once)…" : "Loading asset index…", 0);
        var ws = _ws;
        try { _index = await Task.Run(() => AssetIndex.LoadOrBuild(ws, new Progress<(string S, double P)>(p => BeginInvoke(() => SetProgress(p.S, p.P))), rebuild)); }
        finally { _busy = false; SetProgress(null, 0); }
        _worlds.Items.Clear();
        try { _acts = ActCatalog.Build(_ws!, _index); } catch (Exception e) { _acts = new(); Log("Act catalogue: " + e.Message); }
        foreach (var w in WorldCatalog.FromIndex(_index))
        {
            var mine = _acts.Where(a => a.WorldBundle == w.Bundle).ToList();
            bool worldHasActs = _acts.Any(a => a.World == w.World);
            string note = mine.Count > 0 ? $"  — loaded by {mine.Count} act(s)" : worldHasActs ? "  — copy not used by any act" : "";
            _worlds.Items.Add(new WorldItem(w, _index.BundleSummary.GetValueOrDefault(w.Bundle, ""), null, note));
            foreach (var a in mine) _worlds.Items.Add(new WorldItem(w, "", a));
        }
        _assets.SetIndex(_index);
        _tags.Index = _index;
        try { _text.SetWorkspace(_ws); _audio.SetWorkspace(_ws, _index); _video.SetWorkspace(_ws); } catch (Exception e) { Log("Media panels: " + e.Message); }
        try { _parts.SetWorkspace(_ws, _index); } catch (Exception e) { Log("Part importer: " + e.Message); }
        Log($"Asset index: {_index.Entries.Count} assets in {_index.BundleSummary.Count} bundles; {_worlds.Items.Count} world scenes (double-click one to open).");
    }

    WorldEntry? _sceneEntry;

    async Task ExportGame(bool changedOnly, bool console = false)
    {
        if (_ws == null) return;
        if (_scene != null && _scene.Objects.Any(o => o.Dirty)) Log("Note: unsaved world edits are not exported (World > Save first).");
        using var d = new FolderBrowserDialog { Description = changedOnly ? "Folder for the mod package (changed files + NBMOD_CHANGES.txt)" : "EMPTY folder for the complete modified game directory" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        var ws = _ws; var target = d.SelectedPath;
        _busy = true; SetProgress("Exporting…", 0);
        try
        {
            int n = await Task.Run(() => ws.Export(target, changedOnly, new Progress<(string F, double P)>(p => BeginInvoke(() => SetProgress("Exporting " + p.F, p.P))), bakeExeMods: console));
            if (ws.Manifest.ExeMods.Count > 0)
            {
                var xb = File.ReadAllBytes(ws.Game.Xex); var img = XexFile.Read(xb).GetImage();
                var h = NB.Core.Mods.ExePatches.ResolveXeniaHash(NB.Core.Mods.ExePatches.XeniaModuleHash(xb, img), _settings.XeniaPath != null ? Path.GetDirectoryName(_settings.XeniaPath) : null);
                if (h != null) NB.Core.Mods.ExePatches.WriteXeniaPatchFile(Path.Combine(target, "xenia_patches"), h.Value, NB.Core.Mods.ExePatches.ResolveAll(ws.Manifest.ExeMods));
            }
            Log($"Exported {n} file(s) to {target} (see NBMOD_CHANGES.txt{(ws.Manifest.ExeMods.Count > 0 ? "; executable mods: xenia_patches\\patches — copy into Xenia's folder" : "")}).");
        }
        catch (Exception e) { Error("Export failed", e); }
        finally { _busy = false; SetProgress(null, 0); }
    }

    /// <summary>Runs a scene file (NB.Core.World.SceneBuilder, docs/SCENE_FORMAT.md) into the workspace, then reopens the world.</summary>
    async Task BuildScene(string? path)
    {
        if (_ws == null || _index == null) return;
        if (path == null)
        {
            using var d = new OpenFileDialog { Filter = "Scene (*.json)|*.json", Title = "Scene file to build into the workspace" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            path = d.FileName;
        }
        if (_scene != null && _scene.Objects.Any(o => o.Dirty) && MessageBox.Show(this, "Unsaved world edits will be lost when the world is rebuilt. Continue?", "Build Scene", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        var ws = _ws; var idx = _index; var scenePath = path;
        _busy = true; SetProgress("Building scene…", 0);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var rep = await Task.Run(() => NB.Core.World.SceneBuilder.Build(ws, idx, scenePath, new Progress<(string S, double P)>(p => BeginInvoke(() => SetProgress("Scene: " + p.S, p.P)))));
            Log($"Scene {Path.GetFileName(scenePath)} built in {sw.Elapsed.TotalSeconds:F0}s: {rep.Textures} textures, {rep.Models} models, {rep.Instances} instances, {rep.Hidden} hidden, {rep.Markers} markers, terrain {rep.TerrainTriangles:N0} tris, collision {rep.CollisionTriangles:N0} tris.");
            foreach (var n in rep.Notes) Log("  " + n);
            foreach (var e in rep.Errors) Log("  ERROR " + e);
            // reopen the rebuilt world
            var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(scenePath));
            if (json.RootElement.TryGetProperty("world", out var wv) && uint.TryParse(wv.GetString(), System.Globalization.NumberStyles.HexNumber, null, out uint wb))
            {
                ws.ForgetCache(wb);
                var item = _worlds.Items.OfType<WorldItem>().FirstOrDefault(x => x.Entry.Bundle == wb);
                if (item != null) await OpenWorld(item.Entry);
            }
            if (rep.Errors.Count > 0) MessageBox.Show(this, $"{rep.Errors.Count} error(s) — see the log.", "Build Scene", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception e) { Error("Scene build failed", e); }
        finally { _busy = false; SetProgress(null, 0); }
    }

    async Task CreatePatch()
    {
        if (_ws == null) return;
        if (_scene != null && _scene.Objects.Any(o => o.Dirty)) Log("Note: unsaved world edits are not in the patch (World > Save first).");
        using var d = new SaveFileDialog { Filter = "NB patch (*.nbpatch)|*.nbpatch", FileName = Path.GetFileName(_ws.Root) + ".nbpatch", Title = "Save distributable patch" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        var ws = _ws; var target = d.FileName;
        _busy = true; SetProgress("Building patch…", 0);
        try
        {
            var man = await Task.Run(() => NB.Core.Project.PatchPackage.Build(ws, target, Path.GetFileNameWithoutExtension(target), Environment.UserName, "Made with NB Studio",
                true, new Progress<(string F, double P)>(p => BeginInvoke(() => SetProgress("Delta " + p.F, p.P)))));
            foreach (var f in man.Files) Log($"  {f.Kind} {f.Path}: {f.CopiedBytes:N0} bytes from the original + {f.LiteralBytes:N0} new");
            Log($"Patch written: {target} ({new FileInfo(target).Length:N0} bytes, {man.Files.Count} file(s){(man.ExeMods.Count > 0 ? ", executable mods: " + string.Join(", ", man.ExeMods.Select(m => m.Id)) : "")}). It contains no original game data.");
        }
        catch (Exception e) { Error("Patch build failed", e); }
        finally { _busy = false; SetProgress(null, 0); }
    }

    async Task ApplyPatch()
    {
        using var o = new OpenFileDialog { Filter = "NB patch (*.nbpatch)|*.nbpatch", Title = "Patch to apply" };
        if (o.ShowDialog(this) != DialogResult.OK) return;
        using var d = new FolderBrowserDialog { Description = "Game directory to patch (a COPY of the game: default.xex + Bundle folder). A backup is kept for rollback." };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        var patch = o.FileName; var dir = d.SelectedPath;
        if (_ws != null && Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(_ws.Original.Root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        { MessageBox.Show(this, "That is the workspace's original (read-only) game directory. Patch a copy instead.", "Apply Patch", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        var check = NB.Core.Project.PatchPackage.Verify(patch, dir);
        foreach (var c in check) Log($"  {c.State,-9} {c.Path}  {c.Detail}");
        if (check.Any(c => c.State is "missing" or "mismatch" or "exists")) { Log("Patch cannot be applied to this directory (see above). Nothing was changed."); return; }
        string? xdir = _settings.XeniaPath != null ? Path.GetDirectoryName(_settings.XeniaPath) : null;
        _busy = true; SetProgress("Applying patch…", 0);
        try
        {
            int n = await Task.Run(() => NB.Core.Project.PatchPackage.Apply(patch, dir, xdir, s => BeginInvoke(() => Log(s)), new Progress<(string F, double P)>(p => BeginInvoke(() => SetProgress("Patching " + p.F, p.P)))));
            Log($"Patched {n} file(s) in {dir}. Build > Roll Back Patches restores the originals.");
        }
        catch (Exception e) { Error("Patch failed (nothing replaced)", e); }
        finally { _busy = false; SetProgress(null, 0); }
    }

    void ShowPatchHistory()
    {
        using var d = new FolderBrowserDialog { Description = "Game directory whose patch history to show" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        var lines = NB.Core.Project.PatchPackage.ReadLog(d.SelectedPath);
        Log(lines.Count == 0 ? $"No patch history ({NB.Core.Project.PatchPackage.LogFileName}) in {d.SelectedPath}." : $"Patch history of {d.SelectedPath} ({lines.Count} entries, oldest first):");
        foreach (var l in lines) Log("  " + l);
    }

    void RollbackPatch()
    {
        using var d = new FolderBrowserDialog { Description = "Patched game directory to restore" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        try { Log($"Restored {NB.Core.Project.PatchPackage.Rollback(d.SelectedPath, s => Log(s))} file(s)."); }
        catch (Exception e) { Error("Rollback failed", e); }
    }

    async Task ToggleCollision(bool on)
    {
        _view.ShowCollision = on;
        if (on && _scene != null && _scene.CollisionByModel == null)
        {
            var scene = _scene;
            _busy = true; SetProgress("Decoding Havok collision…", 0.3);
            try
            {
                var (models, tris, notes) = await Task.Run(() => scene.LoadCollision());
                Log($"Collision: {tris:N0} triangles for {models} model(s) (terrain + scenery).");
                foreach (var n in notes.Distinct().Take(10)) Log("  " + n);
            }
            catch (Exception e) { Error("Collision decoding failed", e); }
            finally { _busy = false; SetProgress(null, 0); }
        }
        _view.Refresh3D();
    }

    bool CanEditInstances()
    {
        if (_ws == null || _scene == null || _sceneEntry == null) return false;
        if (_scene.Objects.Any(x => x.Dirty)) { MessageBox.Show(this, "Save or undo the transform edits in this world first (File > Save World).", Text); return false; }
        return true;
    }

    async Task DuplicateObject(SceneObject o, Vector3 offset, bool confirm = true)
    {
        if (!CanEditInstances() || o.Instance == null) return;
        try
        {
            var world = o.Transform; world.Translation += offset;
            int ni = NB.Core.World.InstanceEditor.Duplicate(_scene!.Caff, _scene.Background.View.Symbol, o.Instance.Index, world);
            _ws!.SaveResident(_scene.Bundle, _scene.Caff, $"duplicated {o.Name} as instance {ni} at {world.Translation}");
            Log($"Duplicated {o.Name} → instance {ni} at {world.Translation}. Reloading world…");
            await OpenWorld(_sceneEntry!, _sceneAct);
            var copy = _scene!.Objects.FirstOrDefault(x => x.Instance?.Index == ni);
            if (copy != null) { _view.Select(copy); _view.Focus(copy); }
        }
        catch (Exception e) { Error("Duplicate failed", e); }
    }

    async Task DeleteObject(SceneObject o, bool confirm = true)
    {
        if (!CanEditInstances() || o.Instance == null) return;
        if (confirm && MessageBox.Show(this, $"Delete {o.Name} from the world?\n\nIt is hidden (zero scale, moved far below the level) rather than removed from the tables, because the game refers to scenery by index. Edit > Undo Last Bundle Save restores it.", Text, MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        try
        {
            NB.Core.World.InstanceEditor.Hide(_scene!.Caff, _scene.Background.View.Symbol, o.Instance.Index);
            _ws!.SaveResident(_scene.Bundle, _scene.Caff, $"deleted {o.Name} (hidden)");
            Log($"Deleted {o.Name} (instance {o.Instance.Index} hidden). Reloading world…");
            await OpenWorld(_sceneEntry!, _sceneAct);
        }
        catch (Exception e) { Error("Delete failed", e); }
    }

    async Task UndoLastBundleSave()
    {
        if (_ws == null || _scene == null || _sceneEntry == null) return;
        var rel = Path.GetRelativePath(_ws.Game.Root, _ws.Game.ResidentPath(_scene.Bundle));
        var hist = _ws.History(rel);
        if (hist.Count == 0) { MessageBox.Show(this, "No earlier saved version of this world's bundle.", Text); return; }
        if (MessageBox.Show(this, $"Restore {rel} to the version saved before the last change ({Path.GetFileNameWithoutExtension(hist[0])})?", Text, MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        var v = _ws.UndoLastSave(rel);
        Log($"Restored {rel} to {v}. Reloading world…");
        _scene.Objects.ForEach(x => x.OriginalTransform = x.Transform);   // discard in-memory edits of the old scene
        await OpenWorld(_sceneEntry, _sceneAct);
    }

    /// <summary>
    /// Replaces the reference model of a scenery object with an OBJ (every instance of that model changes).
    /// The world must have no unsaved transform edits; the bundle is saved and the world reloaded afterwards.
    /// </summary>
    async Task ImportModel(SceneObject o, string? objPath = null, bool confirm = true, bool materials = true)
    {
        if (_ws == null || _scene == null || _sceneEntry == null) return;
        if (o.Kind != SceneObjectKind.Scenery) { MessageBox.Show(this, "Model import currently supports scenery objects (reference models).", Text); return; }
        if (_scene.Objects.Any(x => x.Dirty)) { MessageBox.Show(this, "Save or undo the transform edits in this world first (File > Save World).", Text); return; }
        if (objPath == null)
        {
            using var dlg = new OpenFileDialog { Filter = "3D models (*.obj;*.fbx)|*.obj;*.fbx|Wavefront OBJ (*.obj)|*.obj|FBX binary (*.fbx)|*.fbx", Title = $"Replace {AssetIds.DisplayName(o.ModelName)} with…" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            objPath = dlg.FileName;
        }
        try
        {
            var meshes = NB.Core.Models.ObjReader.ReadAny(objPath);
            int sym = _scene.Caff.Symbols.FindIndex(s => AssetIds.DisplayName(s) == AssetIds.DisplayName(o.ModelName)) + 1;
            if (sym == 0) throw new InvalidDataException($"{o.ModelName} is not stored in bundle {_scene.Bundle:x6}");
            int users = _scene.Objects.Count(x => x.ModelName == o.ModelName);
            var opts = new NB.Core.Models.MaterialImport.Options();
            if (confirm)
            {
                var caff = _scene.Caff;
                using var dlg = new Panels.ModelImportDialog(AssetIds.DisplayName(o.ModelName), users, objPath, meshes, op => NB.Core.Models.MaterialImport.MakePlan(caff, sym, meshes, op));
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                materials = dlg.ImportMaterials; opts = dlg.Options;
            }
            if (materials)
            {
                var mr = NB.Core.Models.MaterialImport.Apply(_scene.Caff, sym, meshes, opts);
                foreach (var m in mr.Plan.Materials) Log($"  material {m.Name}: {m.Mode} → {m.Slot.Replace("aid_texture_banjox_", "")}; {m.Source}{(m.Problem != null ? " [" + m.Problem + "]" : "")}");
                foreach (var n in mr.Notes.Concat(mr.Geometry.Notes)) Log("  " + n);
                _ws.SaveResident(_scene.Bundle, _scene.Caff, $"imported {Path.GetFileName(objPath)} with materials into {AssetIds.DisplayName(o.ModelName)}");
                Log($"Imported {Path.GetFileName(objPath)} into {AssetIds.DisplayName(o.ModelName)}: {mr.Geometry.Vertices} vertices, {mr.Geometry.Triangles} triangles, {mr.Plan.Materials.Count} material(s), {mr.Textures.Count} texture(s) created. Reloading world…");
            }
            else
            {
                var res = NB.Core.Models.ModelImporter.Replace(_scene.Caff, sym, meshes);
                foreach (var n in res.Notes) Log("  " + n);
                _ws.SaveResident(_scene.Bundle, _scene.Caff, $"imported {Path.GetFileName(objPath)} into {AssetIds.DisplayName(o.ModelName)}");
                Log($"Imported {Path.GetFileName(objPath)} (geometry only) into {AssetIds.DisplayName(o.ModelName)}: {res.Vertices} vertices, {res.Triangles} triangles. Reloading world…");
            }
            string model = o.ModelName;
            await OpenWorld(_sceneEntry, _sceneAct);
            var again = _scene?.Objects.FirstOrDefault(x => x.ModelName == model && x.Name == o.Name) ?? _scene?.Objects.FirstOrDefault(x => x.ModelName == model);
            if (again != null) _view.Select(again);
        }
        catch (Exception e) { Error("Model import failed", e); }
    }

    // ------------------------------------------------------------------ texture library

    Panels.TextureLibraryForm? _texLib;

    /// <summary>Opens the texture library of an object's model (and its nested models), or of the whole world.</summary>
    void OpenTextureLibrary(SceneObject? o)
    {
        if (_ws == null || _scene == null) return;
        _texLib?.Close();
        var host = new TextureHost(this, o?.ModelName);
        string title = o == null ? $"Texture Library — world {_scene.Bundle:x6}" : $"Textures — {AssetIds.DisplayName(o.ModelName)}";
        _texLib = new Panels.TextureLibraryForm(title, host, o != null) { Log = Log };
        _texLib.FormClosed += (_, _) => _texLib = null;
        _texLib.Show(this);
    }

    sealed class TextureHost : Panels.ITextureHost
    {
        readonly MainForm _f; readonly string? _model;
        public TextureHost(MainForm f, string? model) { _f = f; _model = model; }
        WorldScene Scene => _f._scene ?? throw new InvalidOperationException("no world open");
        public string EditFolder => Path.Combine(_f._ws!.Root, "texture_edits");

        public (byte[] Rgba, int W, int H)? LoadFull(string stem) => Scene.Textures?.LoadFull(stem) ?? Scene.LoadTexture(stem);

        public string Where(string stem)
        {
            var locs = Scene.Textures?.Locate(stem) ?? new();
            if (locs.Count == 0) return "not found in the workspace";
            return string.Join("; ", locs.Select(l => l.Bundle == null ? $"this world's bundle ({l.Asset.Replace("aid_texture_banjox_", "")})"
                : $"{(l.Streamed ? "Bundle/50/" : "Bundle/4f/")}{l.Bundle:x6} ({l.Asset.Replace("aid_texture_banjox_", "")})").Distinct());
        }

        static IEnumerable<NB.Core.Models.ModelAsset> ModelsOf(SceneObject o) => o.Children.Select(c => c.Model).Prepend(o.Model!);

        public List<Panels.TextureItem> Items()
        {
            var sc = Scene;
            var objs = sc.Objects.Where(x => x.Model != null).ToList();
            // stem → distinct models using it (whole world)
            var users = new Dictionary<string, HashSet<NB.Core.Models.ModelAsset>>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in objs.SelectMany(ModelsOf).Distinct())
                foreach (var t in m.Draws.SelectMany(d => d.Textures))
                {
                    var st = NB.Core.Models.ObjExporter.TextureFileStem(t.Texture);
                    if (!users.TryGetValue(st, out var set)) users[st] = set = new();
                    set.Add(m);
                }
            if (_model == null)
                return users.OrderBy(kv => kv.Key).Select(kv => new Panels.TextureItem(kv.Key, NB.Core.Models.MaterialImport.Classify(kv.Key), $"{kv.Value.Count} model(s)", kv.Value.Count)).ToList();
            var o = objs.FirstOrDefault(x => x.ModelName == _model);
            if (o == null) return new();
            var mine = ModelsOf(o).ToHashSet();
            var res = new List<Panels.TextureItem>();
            foreach (var g in mine.SelectMany(m => m.Draws.SelectMany(d => d.Textures.Select(t => (d, t)))).GroupBy(x => NB.Core.Models.ObjExporter.TextureFileStem(x.t.Texture), StringComparer.OrdinalIgnoreCase))
            {
                var slots = string.Join(",", g.Select(x => "s" + x.t.Slot).Distinct());
                int draws = g.Select(x => x.d).Distinct().Count();
                int others = users.TryGetValue(g.Key, out var u) ? u.Count(m => !mine.Contains(m)) : 0;
                res.Add(new Panels.TextureItem(g.Key, NB.Core.Models.MaterialImport.Classify(g.Key), $"{slots} in {draws} draw(s)", others));
            }
            return res.OrderBy(x => x.Role == "colour" ? 0 : x.Role == "other" ? 1 : 2).ThenBy(x => x.Stem).ToList();
        }

        public async Task<string?> ReplaceAsync(string stem, (byte[] Rgba, int W, int H) img, bool modelOnly)
        {
            var f = _f; var sc = Scene; var ws = f._ws!;
            if (sc.Objects.Any(x => x.Dirty)) throw new InvalidOperationException("Save or undo the transform edits in this world first (World > Save).");
            string result = stem;
            bool OwnImp(string st) => st.StartsWith("aid_texture_banjox_imp_") && sc.Caff.Symbols.Any(x => AssetIds.DisplayName(x) == st);
            // an import-made texture (exact, in this bundle) is recreated at the new image's size; others keep their size
            void Recreate(string name)
            {
                int w = Pow2(img.W), h = Pow2(img.H);
                var px = w == img.W && h == img.H ? img.Rgba : ImageIO.Resize(img.Rgba, img.W, img.H, w, h).Rgba;
                int old = sc.Caff.Symbols.FindIndex(x => AssetIds.DisplayName(x) == name) + 1;
                if (old > 0) NB.Core.Formats.CaffEdit.RemoveAsset(sc.Caff, old);
                TextureFactory.Create(sc.Caff, name, px, w, h, w, h, XenosFormat.DXT1);
            }
            if (modelOnly)
            {
                if (_model == null) throw new InvalidOperationException("no model");
                string model = AssetIds.DisplayName(_model);
                string tail = model.StartsWith("aid_model_banjox_") ? model["aid_model_banjox_".Length..] : model;
                string stail = stem.StartsWith("aid_texture_banjox_") ? stem["aid_texture_banjox_".Length..] : stem;
                if (stail.StartsWith("imp_")) stail = stail[4..];
                string name = "aid_texture_banjox_imp_" + Clip(tail, 32) + "_" + Clip(stail, 40);
                bool sharedImp = OwnImp(stem) && Items().Any(i => i.Stem == stem && i.OtherModels > 0);
                if (OwnImp(stem) && !sharedImp) name = stem;
                Recreate(name);
                if (name != stem)
                {
                    int sym = sc.Caff.Symbols.FindIndex(x => AssetIds.DisplayName(x) == model) + 1;
                    int n = NB.Core.Models.ModelEdit.RetargetTexture(sc.Caff, sym, stem, name, exact: true);
                    f.Log($"texture library: {model}: {stem} → {name} ({n} entr{(n == 1 ? "y" : "ies")}), other models keep the original");
                }
                ws.SaveResident(sc.Bundle, sc.Caff, $"texture {name} for {model} only");
                result = name;
            }
            else if (OwnImp(stem))
            {
                Recreate(stem);
                ws.SaveResident(sc.Bundle, sc.Caff, $"replaced texture {stem}");
            }
            else
            {
                var locs = sc.Textures?.Locate(stem) ?? new();
                bool inWorld = locs.Any(l => l.Bundle == null || l.Bundle == sc.Bundle);
                if (inWorld)
                {
                    var r = TextureReplacer.ReplaceLoaded(ws, sc.Bundle, sc.Caff, stem, img.Rgba, img.W, img.H);
                    foreach (var n in r.Notes) f.Log("  " + n);
                    if (r.ResidentAssets > 0) ws.SaveResident(sc.Bundle, sc.Caff, $"replaced texture {stem} ({r.ResidentAssets} resident asset(s))");
                    if (r.ResidentAssets + r.StreamedAssets == 0) throw new InvalidDataException($"{stem} could not be replaced: {string.Join("; ", r.Notes)}");
                }
                else
                {
                    var b = locs.Select(l => l.Bundle).FirstOrDefault(x => x != null) ?? throw new InvalidDataException($"{stem} is not stored in any bundle");
                    var r = TextureReplacer.Replace(ws, b, stem, img.Rgba, img.W, img.H);
                    foreach (var n in r.Notes) f.Log("  " + n);
                    if (r.ResidentAssets + r.StreamedAssets == 0) throw new InvalidDataException($"{stem} could not be replaced: {string.Join("; ", r.Notes)}");
                    f.Log($"texture library: {stem} lives in shared bundle {b:x6}: every level using that bundle shows the new image");
                }
            }
            f.Log($"texture library: replaced {stem}{(modelOnly ? " for this model only" : "")} ({img.W}x{img.H}). Reloading world…");
            await f.OpenWorld(f._sceneEntry!, f._sceneAct);
            return result;
        }

        static int Pow2(int v) { int p = 8; while (p < v && p < 2048) p *= 2; return p; }
        static string Clip(string s, int n) => s.Length <= n ? s : s[^n..];
    }

    /// <summary>Collision asset of a scenery object (aid_model_X → aid_havok_X) when it is a mesh collision in the scene bundle.</summary>
    string? CollisionAssetOf(SceneObject o)
    {
        if (_scene == null || string.IsNullOrEmpty(o.ModelName)) return null;
        var model = AssetIds.DisplayName(o.ModelName);
        if (!model.StartsWith("aid_model_")) return null;
        var hk = "aid_havok_" + model["aid_model_".Length..];
        int sym = _scene.Caff.Symbols.FindIndex(s => AssetIds.DisplayName(s) == hk) + 1;
        if (sym == 0) return null;
        var v = new AssetView(_scene.Caff, sym);
        var d = v.Has(".data") ? v.Data(".data") : Array.Empty<byte>();
        return NB.Core.Havok.HkCollisionImport.TypeOneEntry(d) >= 0 && !NB.Core.Havok.HkCollisionImport.IsBreakable(d) ? hk : null;
    }

    /// <summary>
    /// Replaces the collision mesh of a scenery object's model (aid_havok_X, every instance) with an OBJ/FBX, or with
    /// the box around it. Same core function as <c>NB.Cli collision-import</c>; the bundle is saved, the world reloaded
    /// and the collision overlay shown.
    /// </summary>
    async Task ImportCollision(SceneObject o, string? path = null, bool confirm = true, bool box = false)
    {
        if (_ws == null || _scene == null || _sceneEntry == null) return;
        if (_scene.Objects.Any(x => x.Dirty)) { MessageBox.Show(this, "Save or undo the transform edits in this world first (File > Save World).", Text); return; }
        var hk = CollisionAssetOf(o);
        if (hk == null) { MessageBox.Show(this, "This object's model has no mesh collision asset in this bundle.", Text); return; }
        if (path == null)
        {
            using var dlg = new OpenFileDialog { Filter = "3D models (*.obj;*.fbx)|*.obj;*.fbx|Wavefront OBJ (*.obj)|*.obj|FBX binary (*.fbx)|*.fbx", Title = $"Collision for {AssetIds.DisplayName(o.ModelName)}" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            path = dlg.FileName;
        }
        try
        {
            int sym = _scene.Caff.Symbols.FindIndex(s => AssetIds.DisplayName(s) == hk) + 1;
            var meshes = NB.Core.Models.ObjReader.ReadAny(path);
            int users = _scene.Objects.Count(x => x.ModelName == o.ModelName);
            if (confirm)
            {
                var ans = MessageBox.Show(this, $"Replace the collision {hk} ({users} instance(s) in this world) with {Path.GetFileName(path)}?\n\n" +
                    string.Join("\n", meshes.Select(m => $"  {m.Name}: {m.Positions.Count} vertices, {m.Triangles.Count / 3} triangles")) +
                    "\n\nYes = use the triangles (model space, like Import Model)\nNo = use the box around the mesh\n\nThe old collision (shapes and MOPP tree) is replaced; Edit > Undo Last Bundle Save restores it.",
                    Text, MessageBoxButtons.YesNoCancel);
                if (ans == DialogResult.Cancel) return;
                box = ans == DialogResult.No;
            }
            var res = NB.Core.Havok.HkCollisionImport.ImportFile(_scene.Caff, sym, path, box: box);
            foreach (var n in res.Notes) Log("  " + n);
            _ws.SaveResident(_scene.Bundle, _scene.Caff, $"imported collision {Path.GetFileName(path)}{(box ? " (box)" : "")} into {hk}");
            Log($"Imported collision {Path.GetFileName(path)}{(box ? " (box)" : "")} into {hk}: {res.Triangles} triangles, bounds {res.Min} .. {res.Max}. Reloading world…");
            await OpenWorld(_sceneEntry, _sceneAct);
            await ToggleCollision(true);   // decode the new collision and show the overlay
            if (_viewCollision != null && !_viewCollision.Checked) _viewCollision.Checked = true;   // keep View > Collision in sync (already decoded)
            var again = _scene?.Objects.FirstOrDefault(x => x.Name == o.Name);
            if (again != null) _view.Select(again);
        }
        catch (Exception e) { Error("Collision import failed", e); }
    }

    sealed record WorldItem(WorldEntry Entry, string Summary, ActEntry? Act = null, string Note = "")
    {
        public override string ToString() => Act != null ? $"    ↳ {Act.Display}  [{Act.ActBundle:x6}]" : $"{Entry.Display}  [{Entry.Bundle:x6}]{Note}";
    }

    ActEntry? _sceneAct;
    List<ActEntry> _acts = new();

    async Task OpenWorld(WorldEntry w, ActEntry? act = null)
    {
        if (_ws == null) return;
        if (_scene != null && _scene.Objects.Any(o => o.Dirty) &&
            MessageBox.Show(this, "Discard unsaved edits in the current world?", Text, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        _busy = true; SetProgress($"Loading {w.Display}…", 0);
        try
        {
            var ws = _ws;
            var index = _index;
            var scene = await Task.Run(() => new WorldScene(ws, w.Bundle, w.BackgroundModel, new Progress<(string S, double P)>(p => BeginInvoke(() => SetProgress(p.S, p.P))),
                act != null ? new[] { act.ActBundle } : null));
            // textures: resolve every diffuse texture up front across the whole workspace (world bundle, shared/common
            // bundles, stream archives) and report what could not be found
            scene.Textures = new NB.Core.Textures.TextureResolver(ws, index, scene.Caff);
            var texNames = scene.DiffuseTextureNames().ToList();
            await Task.Run(() =>
            {
                for (int i = 0; i < texNames.Count; i++)
                {
                    scene.Textures.Load(texNames[i]);
                    if (i % 8 == 0) { int k = i; BeginInvoke(() => SetProgress($"Loading textures {k}/{texNames.Count}…", k / (double)Math.Max(1, texNames.Count))); }
                }
            });
            _scene = scene; _sceneEntry = w; _sceneAct = act; _undo.Clear(); _redo.Clear();
            _view.SetScene(scene);
            FillTree();
            Log($"Opened {(act?.Display ?? w.Display)} (world bundle {w.Bundle:x6}{(act != null ? $", act bundle {act.ActBundle:x6} markers" : "")}): {scene.Objects.Count} objects, {scene.Models.Count} reference models.");
            {
                var src = scene.Textures.Sources.Values.Select(v => v.StartsWith("world") ? "world bundle" : v.StartsWith("resident") ? "other resident bundles" : v.StartsWith("streamed") ? "stream archives" : "missing").GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
                Log($"  Textures: {texNames.Count} used — " + string.Join(", ", src.Select(kv => $"{kv.Value} from {kv.Key}")));
                var missing = scene.Textures.Missing.ToList();
                if (missing.Count > 0) Log($"  Missing textures ({missing.Count}, drawn untextured): " + string.Join(", ", missing.Take(12)) + (missing.Count > 12 ? " …" : ""));
            }
            foreach (var l in scene.Log.Take(30)) Log("  " + l);
            if (scene.Log.Count > 30) Log($"  … {scene.Log.Count - 30} more notes");
            _center.SelectedIndex = 0;
            UpdateTitle();
        }
        catch (Exception e) { Error("Loading world failed", e); }
        finally { _busy = false; SetProgress(null, 0); }
    }

    void FillTree()
    {
        _tree.BeginUpdate(); _tree.Nodes.Clear();
        if (_scene != null)
        {
            string q = _treeSearch.Text.Trim().ToLowerInvariant();
            var terrain = _tree.Nodes.Add("Terrain");
            var scenery = _tree.Nodes.Add("Scenery (by model)");
            terrain.Checked = scenery.Checked = true;
            foreach (var o in _scene.Objects.Where(o => o.Kind == SceneObjectKind.Terrain))
                terrain.Nodes.Add(new TreeNode(o.Name) { Tag = o, Checked = o.Visible });
            foreach (var g in _scene.Objects.Where(o => o.Kind == SceneObjectKind.Scenery && (q == "" || o.Name.ToLowerInvariant().Contains(q) || o.ModelName.ToLowerInvariant().Contains(q)))
                         .GroupBy(o => AssetIds.DisplayName(o.ModelName).Replace("aid_model_banjox_background_", "")).OrderBy(g => g.Key))
            {
                var gn = scenery.Nodes.Add($"{g.Key} ({g.Count()})"); gn.Checked = true;
                foreach (var o in g) gn.Nodes.Add(new TreeNode(o.Name + (o.Dirty ? " *" : "")) { Tag = o, Checked = o.Visible });
            }
            var markers = _tree.Nodes.Add("Markers (by asset and type)"); markers.Checked = true;
            foreach (var ma in _scene.Objects.Where(o => o.Kind == SceneObjectKind.Marker && (q == "" || o.Name.ToLowerInvariant().Contains(q))).GroupBy(o => o.ModelName))
            {
                var an = markers.Nodes.Add($"{ma.Key.Replace("aid_marker_banjox_", "")} ({ma.Count()})"); an.Checked = true;
                foreach (var tg in ma.GroupBy(o => o.Marker!.Type).OrderBy(g => g.Key))
                {
                    var tn = an.Nodes.Add($"{NB.Core.World.MarkerRecord.TypeName(tg.Key)} ({tg.Count()})"); tn.Checked = true;
                    foreach (var o in tg) tn.Nodes.Add(new TreeNode(o.Name + (o.Dirty ? " *" : "")) { Tag = o, Checked = o.Visible });
                }
            }
            terrain.Expand(); scenery.Expand();
        }
        _tree.EndUpdate();
    }

    void OnSelection(SceneObject? o)
    {
        _transform.SetObject(o);
        _tags.ShowObject(_scene, o);
        _status.Text = o == null ? "" : $"{o.Name} — {AssetIds.DisplayName(o.ModelName)}  pos ({o.Transform.M41:F2}, {o.Transform.M42:F2}, {o.Transform.M43:F2})";
        if (o != null)
        {
            _syncingTree = true;
            var node = FindNode(_tree.Nodes, o);
            if (node != null) { _tree.SelectedNode = node; node.EnsureVisible(); }
            _syncingTree = false;
        }
        UpdateTitle();
    }

    static TreeNode? FindNode(TreeNodeCollection nodes, SceneObject o)
    {
        foreach (TreeNode n in nodes) { if (n.Tag == o) return n; var c = FindNode(n.Nodes, o); if (c != null) return c; }
        return null;
    }

    // ------------------------------------------------------------------ editing

    void PushUndo(SceneObject o, Matrix4x4 before, Matrix4x4 after)
    {
        if (before == after) return;
        _undo.Push((o, before, after)); _redo.Clear();
        UpdateTitle();
        var node = FindNode(_tree.Nodes, o); if (node != null) node.Text = o.Name + (o.Dirty ? " *" : "");
    }

    void Undo()
    {
        if (_undo.Count == 0) return;
        var u = _undo.Pop(); u.Obj.Transform = u.Before; _redo.Push(u);
        _view.Select(u.Obj); _view.Refresh3D(); UpdateTitle();
    }

    void Redo()
    {
        if (_redo.Count == 0) return;
        var u = _redo.Pop(); u.Obj.Transform = u.After; _undo.Push(u);
        _view.Select(u.Obj); _view.Refresh3D(); UpdateTitle();
    }

    void ResetTransform(SceneObject o)
    {
        if (o.Kind == SceneObjectKind.Terrain) return;
        var before = o.Transform;
        // back to the transform stored in the bundle when the world was opened / last saved
        o.Transform = o.OriginalTransform;
        PushUndo(o, before, o.Transform);
        _view.Select(o); _view.Refresh3D();
    }

    void SaveWorld()
    {
        if (_scene == null || _ws == null) return;
        try
        {
            int n = _scene.Save();
            Log(n == 0 ? "No world changes to save." : $"Saved {n} changed object(s) → {Path.GetRelativePath(_ws.Root, _ws.Game.ResidentPath(_scene.Bundle))} (uncompressed CAFF, checksum recomputed).");
            FillTree(); UpdateTitle();
        }
        catch (Exception e) { Error("Saving failed", e); }
    }

    void UpdateTitle()
    {
        int dirty = _scene?.Objects.Count(o => o.Dirty) ?? 0;
        Text = "Nuts & Bolts Mod Tool" + (_ws != null ? $" — {Path.GetFileName(_ws.Root)}" : "") + (_scene != null ? $" — {WorldCatalog.DisplayNames.GetValueOrDefault(_scene.Background.View.Name.Replace("aid_model_banjox_background_", "").Replace("_default", ""), "")} [{_scene.Bundle:x6}]" : "") + (dirty > 0 || _tags.HasUnsaved ? " *" : "");
    }

    // ------------------------------------------------------------------ export

    void ExportObject(SceneObject o, bool world, bool fbx = false, string? folder = null)
    {
        if (o.Model == null || _scene == null) return;
        if (folder == null)
        {
            using var d = new FolderBrowserDialog { Description = fbx ? "Export folder (FBX + textures/)" : "Export folder (OBJ + MTL + textures/)" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            folder = d.SelectedPath;
        }
        var name = AssetIds.DisplayName(o.ModelName);
        var path = Path.Combine(folder, name + (fbx ? ".fbx" : ".obj"));
        var items = o.Model.Draws.Select(x => (x, world ? o.Transform : Matrix4x4.Identity));
        if (fbx) FbxExporter.Write(path, name, items); else ObjExporter.Write(path, name, items);
        int tex = ExportTextures(o.Model.Draws, Path.Combine(folder, "textures"));
        Log($"Exported {name} → {path} ({o.Model.Draws.Count} draws, {tex} textures)");
    }

    int ExportTextures(IEnumerable<MeshDraw> draws, string dir)
    {
        int n = 0;
        foreach (var t in draws.Select(ObjExporter.DiffuseTexture).Where(t => t != null).Distinct())
        {
            var img = _scene!.LoadTexture(t!);
            if (img == null) continue;
            ImageIO.Save(Path.Combine(dir, ObjExporter.TextureFileStem(t!) + ".png"), img.Value.Rgba, img.Value.W, img.Value.H);
            n++;
        }
        return n;
    }

    void ExportScene()
    {
        if (_scene == null) return;
        using var d = new FolderBrowserDialog { Description = "Export folder for the whole scene" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        var name = AssetIds.DisplayName(_scene.Background.View.Name);
        var items = _scene.Objects.Where(o => o.Visible && o.Model != null).SelectMany(o => o.Model!.Draws.Select(x => (x, o.Transform))).ToList();
        ObjExporter.Write(Path.Combine(d.SelectedPath, name + ".obj"), name, items);
        int tex = ExportTextures(items.Select(i => i.x), Path.Combine(d.SelectedPath, "textures"));
        Log($"Exported scene {name}: {items.Count} draws, {tex} textures → {d.SelectedPath}");
    }

    void ExportPlacements()
    {
        if (_scene == null) return;
        using var d = new SaveFileDialog { Filter = "CSV|*.csv", FileName = "placements.csv" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        using var w = new StreamWriter(d.FileName);
        w.WriteLine("index,name,model,x,y,z,m11,m12,m13,m21,m22,m23,m31,m32,m33");
        foreach (var o in _scene.Objects.Where(o => o.Instance != null))
        {
            var m = o.Transform;
            w.WriteLine(string.Join(",", new object[] { o.Instance!.Index, o.Name, AssetIds.DisplayName(o.ModelName), m.M41, m.M42, m.M43, m.M11, m.M12, m.M13, m.M21, m.M22, m.M23, m.M31, m.M32, m.M33 }.Select(x => Convert.ToString(x, System.Globalization.CultureInfo.InvariantCulture))));
        }
        Log("Wrote " + d.FileName);
    }

    // ------------------------------------------------------------------ build / tools

    async Task ValidateWorkspace()
    {
        if (_ws == null) return;
        var ws = _ws;
        _busy = true; SetProgress("Validating modified files…", 0);
        List<string> report;
        try { report = await Task.Run(() => BuildValidator.Validate(ws)); }
        finally { _busy = false; SetProgress(null, 0); }
        foreach (var l in report) Log(l);
    }

    void ShowChanges()
    {
        if (_ws == null) return;
        var files = _ws.ModifiedFiles();
        Log($"Modified files ({files.Count}):"); foreach (var f in files) Log("  " + f);
        Log($"Change log ({_ws.Manifest.Changes.Count} entries):");
        foreach (var c in _ws.Manifest.Changes.TakeLast(50)) Log($"  {c.Time:yyyy-MM-dd HH:mm}  {c.File}: {c.Description}");
    }

    void RevertFile()
    {
        if (_ws == null) return;
        var files = _ws.ModifiedFiles();
        if (files.Count == 0) { Log("Nothing to revert."); return; }
        using var f = new Form { Text = "Revert file", Width = 600, Height = 400, StartPosition = FormStartPosition.CenterParent };
        var lb = new ListBox { Dock = DockStyle.Fill }; lb.Items.AddRange(files.ToArray());
        var ok = new Button { Text = "Revert selected to original", Dock = DockStyle.Bottom, DialogResult = DialogResult.OK };
        f.Controls.Add(lb); f.Controls.Add(ok); f.AcceptButton = ok;
        if (f.ShowDialog(this) == DialogResult.OK && lb.SelectedItem is string rel)
        {
            _ws.Revert(rel); Log("Reverted " + rel);
            if (_scene != null && rel.Contains((_scene.Bundle & 0xFFFFFF).ToString("x6"))) Log("  The open world was reverted on disk — reopen it to see the original data.");
        }
    }

    /// <summary>Writes the Xenia patch file for the workspace's enabled executable mods (removes it when none are enabled).</summary>
    void EditDataSetting(NB.Core.Mods.DataSetting s)
    {
        if (_ws == null || _index == null) return;
        float cur = NB.Core.Mods.DataMods.Get(_ws, _index, s) ?? s.Default;
        using var f = new Form { Text = s.Name, Width = 460, Height = 210, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false };
        var lbl = new Label { Text = s.Description, Left = 12, Top = 10, Width = 420, Height = 70 };
        var num = new NumericUpDown { Left = 12, Top = 88, Width = 120, DecimalPlaces = 2, Minimum = 0, Maximum = 1000, Value = (decimal)Math.Clamp(cur, 0, 1000) };
        var ok = new Button { Text = "Save", Left = 250, Top = 120, DialogResult = DialogResult.OK };
        var def = new Button { Text = "Default", Left = 150, Top = 86 };
        def.Click += (_, _) => num.Value = (decimal)s.Default;
        var cancel = new Button { Text = "Cancel", Left = 340, Top = 120, DialogResult = DialogResult.Cancel };
        f.Controls.AddRange(new Control[] { lbl, num, def, ok, cancel }); f.AcceptButton = ok; f.CancelButton = cancel;
        if (f.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            int n = NB.Core.Mods.DataMods.Set(_ws, _index, s, (float)num.Value);
            Log($"{s.Name} = {(float)num.Value:G6} ({n} bundle(s) updated)");
        }
        catch (Exception e) { Error("Saving the setting failed", e); }
    }

    /// <summary>Sets a parameterised executable mod (value = stock removes it) and rewrites the Xenia patch file.</summary>
    void EditExeLimit(string family, string title, string text, int stock, int min, int max, int suggested)
    {
        if (_ws == null) return;
        var now = _ws.Manifest.ExeMods.LastOrDefault(i => NB.Core.Mods.ExePatches.FamilyOf(i) == family);
        int value = now == null ? stock : now.Contains(':') ? int.Parse(now[(now.IndexOf(':') + 1)..]) : suggested;
        using var f = new Form { Text = title, Width = 460, Height = 210, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false };
        var lbl = new Label { Text = text, Left = 12, Top = 10, Width = 420, Height = 70 };
        var num = new NumericUpDown { Left = 12, Top = 88, Width = 120, Minimum = stock, Maximum = max, Value = Math.Clamp(value, stock, max) };
        var def = new Button { Text = "Stock", Left = 150, Top = 86 }; def.Click += (_, _) => num.Value = stock;
        var sug = new Button { Text = $"Verified ({suggested})", Left = 240, Top = 86, Width = 110 }; sug.Click += (_, _) => num.Value = suggested;
        var ok = new Button { Text = "Save", Left = 250, Top = 130, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 340, Top = 130, DialogResult = DialogResult.Cancel };
        f.Controls.AddRange(new Control[] { lbl, num, def, sug, ok, cancel }); f.AcceptButton = ok; f.CancelButton = cancel;
        if (f.ShowDialog(this) != DialogResult.OK) return;
        int v = (int)num.Value;
        if (v != stock && v < min) { MessageBox.Show(this, $"Minimum is {min}.", title); return; }
        _ws.Manifest.ExeMods.RemoveAll(i => NB.Core.Mods.ExePatches.FamilyOf(i) == family);
        if (v != stock)
            _ws.Manifest.ExeMods.Add(family == "vehicle-part-limit" && v == 400 ? NB.Core.Mods.ExePatches.VehiclePartLimit400.Id
                                   : family == "garage-build-area" && v == 31 ? NB.Core.Mods.ExePatches.GarageBuildArea31.Id : $"{family}:{v}");
        _ws.SaveManifest();
        Log($"{title}: {(v == stock ? "stock" : v.ToString())}");
        ApplyExeMods();
    }

    /// <summary>
    /// After-Party mods live in the workspace (workspace.json ExeMods): Build > Create Patch bakes them into default.xex.
    /// Xenia only needs its own patch file for testing the workspace in Xenia; that file is written when a Xenia
    /// executable is already set (or <paramref name="askForXenia"/>, i.e. when launching Xenia) — toggling a mod never
    /// asks for Xenia.
    /// </summary>
    bool ApplyExeMods(bool askForXenia = false)
    {
        if (_ws == null) return false;
        List<NB.Core.Mods.ExeMod> enabled;
        byte[] xexBytes; byte[] image;
        try
        {
            xexBytes = File.ReadAllBytes(_ws.Game.Xex);
            var xex = XexFile.Read(xexBytes);
            image = xex.GetImage();
            enabled = NB.Core.Mods.ExePatches.ResolveAll(_ws.Manifest.ExeMods);
            foreach (var m in enabled.ToList())
            {
                var problems = NB.Core.Mods.ExePatches.Check(image, xex.ImageBase, m);
                if (problems.Count > 0) { Log($"Mod '{m.Name}' does not match this default.xex: {string.Join("; ", problems)}"); enabled.Remove(m); }
            }
        }
        catch (Exception e) { Error("Checking the executable mods failed", e); return false; }
        Log(enabled.Count == 0 ? "After-Party mods: none enabled in this workspace."
            : $"After-Party mods saved in the workspace: {string.Join(", ", enabled.Select(m => m.Name))}. Build > Create Patch bakes them into default.xex.");
        var exe = _settings.XeniaPath;
        if ((exe == null || !File.Exists(exe)) && askForXenia && enabled.Count > 0) { PickXenia(); exe = _settings.XeniaPath; }
        if (exe == null || !File.Exists(exe)) return true;   // no Xenia set: nothing else to do
        try
        {
            ulong imageHash = NB.Core.Mods.ExePatches.XeniaModuleHash(xexBytes, image);
            var hash = NB.Core.Mods.ExePatches.ResolveXeniaHash(imageHash, Path.GetDirectoryName(exe));
            if (hash == null) { Log("Executable mods: unknown default.xex — launch it once in Xenia so xenia.log reports its module hash, then try again."); return false; }
            var path = NB.Core.Mods.ExePatches.WriteXeniaPatchFile(Path.GetDirectoryName(exe)!, hash.Value, enabled);
            Log(enabled.Count == 0 ? "Executable mods: none enabled (Xenia patch file removed)." : $"Xenia test patch file updated: {path}");
            return true;
        }
        catch (Exception e) { Error("Writing the Xenia patch file failed", e); return false; }
    }

    void LaunchXenia()
    {
        if (_ws == null) return;
        if (_scene != null && _scene.Objects.Any(o => o.Dirty)) Log("Note: unsaved world edits are not in the workspace yet (World > Save).");
        var exe = _settings.XeniaPath;
        if (exe == null || !File.Exists(exe)) { PickXenia(); exe = _settings.XeniaPath; }
        if (exe == null || !File.Exists(exe)) return;
        ApplyExeMods(askForXenia: true);
        Process.Start(new ProcessStartInfo(exe, $"\"{_ws.Game.Xex}\"") { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false });
        Log($"Launched {Path.GetFileName(exe)} with {_ws.Game.Xex}");
    }

    void PickXenia()
    {
        using var d = new OpenFileDialog { Filter = "Xenia|xenia*.exe|Executables|*.exe", Title = "Select xenia_canary.exe or xenia.exe" };
        var guess = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "Xenia (download xenia here)");
        if (Directory.Exists(guess)) d.InitialDirectory = Path.GetFullPath(guess);
        if (d.ShowDialog(this) == DialogResult.OK) { _settings.XeniaPath = d.FileName; _settings.Save(); }
    }

    void ValidateOriginal()
    {
        using var d = new FolderBrowserDialog { Description = "Game directory to validate" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        var rep = new GameDirectory(d.SelectedPath).Validate();
        Log($"Validation of {d.SelectedPath}: {(rep.Ok ? "OK" : "FAILED")}"); foreach (var i in rep.Info) Log("  " + i); foreach (var e in rep.Errors) Log("  ERROR: " + e);
    }

    void DecompressFile()
    {
        using var o = new OpenFileDialog { Title = "xcompress (0FF512ED) file" };
        if (o.ShowDialog(this) != DialogResult.OK) return;
        using var s = new SaveFileDialog { FileName = Path.GetFileName(o.FileName) + ".caff" };
        if (s.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var d = File.ReadAllBytes(o.FileName);
            if (!NB.Core.Compression.XCompressFile.IsCompressed(d)) { Log("File is not 0x0FF512ED-compressed."); return; }
            var outp = NB.Core.Compression.XCompressFile.Decompress(d);
            File.WriteAllBytes(s.FileName, outp);
            Log($"Decompressed {d.Length} → {outp.Length} bytes; CAFF: {CaffFile.IsCaff(outp)}, header checksum valid: {CaffFile.IsCaff(outp) && CaffFile.VerifyHeaderChecksum(outp)}");
        }
        catch (Exception e) { Error("Decompression failed", e); }
    }

    void XexInfo()
    {
        using var o = new OpenFileDialog { Filter = "XEX|*.xex", Title = "default.xex (read only)" };
        if (_ws != null) o.InitialDirectory = _ws.Game.Root;
        if (o.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var x = XexFile.Read(File.ReadAllBytes(o.FileName));
            Log($"XEX: title {x.TitleId:X8}, {x.OriginalName}, entry 0x{x.EntryPoint:X8}, base 0x{x.ImageBase:X8}, image 0x{x.ImageSize:X}, encryption {x.EncryptionType}, compression {x.CompressionType}");
            Log("  imports: " + string.Join(", ", x.ImportLibraries));
            var img = x.GetImage();
            Log($"  decrypted with the {x.KeyUsed} key; PE image {img.Length} bytes");
            foreach (var s in XexFile.Sections(img)) Log($"    {s.Name,-8} 0x{x.ImageBase + s.Va:X8} size 0x{s.VSize:X}");
            using var sv = new SaveFileDialog { FileName = "default.exe", Title = "Save decrypted PE image (optional)" };
            if (sv.ShowDialog(this) == DialogResult.OK) { File.WriteAllBytes(sv.FileName, img); Log("  wrote " + sv.FileName); }
        }
        catch (Exception e) { Error("XEX read failed", e); }
    }

    void OpenDocs()
    {
        var p = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "docs", "FORMATS.md"));
        if (File.Exists(p)) Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); else Log("docs/FORMATS.md not found next to the build");
    }

    // ------------------------------------------------------------------ scripted runs (automation / self-test)

    /// <summary>
    /// Command-line automation, e.g.:
    ///   NBModStudio --workspace W --world 234cec --select mumbosgarage --move 0,15,0 --save --focus --shot a.png --window b.png --exit
    /// Every step goes through the same code paths as the UI.
    /// </summary>
    async Task RunScript(List<string> a)
    {
        var logFile = a.Contains("--log") ? a[a.IndexOf("--log") + 1] : null;
        void L(string s) { Log(s); if (logFile != null) File.AppendAllText(logFile, s + Environment.NewLine); }
        try
        {
            for (int i = 0; i < a.Count; i++)
            {
                string Next() => a[++i];
                switch (a[i])
                {
                    case "--workspace": await OpenWorkspace(Next()); L("script: workspace open"); break;
                    case "--act":
                    {
                        uint ab = Convert.ToUInt32(Next(), 16);
                        var w = _worlds.Items.OfType<WorldItem>().First(x => x.Act?.ActBundle == ab);
                        await OpenWorld(w.Entry, w.Act); L($"script: act {w.Act!.Display} loaded, {_scene!.Objects.Count} objects"); break;
                    }
                    case "--world":
                    {
                        uint b = Convert.ToUInt32(Next(), 16);
                        var w = _worlds.Items.OfType<WorldItem>().First(x => x.Entry.Bundle == b);
                        await OpenWorld(w.Entry); L($"script: world {w.Entry.Display} loaded, {_scene!.Objects.Count} objects"); break;
                    }
                    case "--select":
                    {
                        var q = Next();
                        var o = _scene!.Objects.First(x => x.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
                        _view.Select(o); L($"script: selected {o.Name} at {o.Transform.Translation}"); break;
                    }
                    case "--move":
                    {
                        var v = Next().Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                        var o = _view.Selected!; var before = o.Transform; var m = before; m.Translation += new Vector3(v[0], v[1], v[2]); o.Transform = m;
                        PushUndo(o, before, o.Transform); _view.Select(o); L($"script: moved {o.Name} to {o.Transform.Translation}"); break;
                    }
                    case "--scale":
                    {
                        float f = float.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture);
                        var o = _view.Selected!; var before = o.Transform; var t = before.Translation; var m = before; m.Translation = Vector3.Zero; m = Matrix4x4.CreateScale(f) * m; m.Translation = t; o.Transform = m;
                        PushUndo(o, before, o.Transform); L($"script: scaled {o.Name} x{f}"); break;
                    }
                    case "--undo": Undo(); L("script: undo"); break;
                    case "--path-link":
                    {
                        var m = _view.Selected?.Marker ?? throw new InvalidOperationException("--path-link: select a path node first");
                        if (m.Type != 22) throw new InvalidOperationException("--path-link: selection is not a path node");
                        int before = m.Link; m.Link = int.Parse(Next()); _view.Refresh3D(); UpdateTitle();
                        L($"script: path node #{m.Index} next {before} -> {m.Link}"); break;
                    }
                    case "--save": SaveWorld(); L("script: saved"); break;
                    case "--build-scene": await BuildScene(Next()); L("script: scene built"); break;
                    case "--live-attach": _right.SelectedIndex = 2; _live.ScriptAttach(); L("script: live " + _live.StateText); break;
                    case "--live-tp":
                    {
                        var v = Next().Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                        _live.ScriptTeleport(new Vector3(v[0], v[1], v[2])); await Task.Delay(1200); L("script: live position " + _live.PositionText); break;
                    }
                    case "--live-gravity": { float g = float.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); _live.ScriptGravity(g); L($"script: live gravity {g}"); break; }
                    case "--wait": await Task.Delay(int.Parse(Next())); break;
                    case "--live-show": _live.ScriptShow(); await Task.Delay(1500); L("script: 3D view at the player " + _view.CameraPosition); break;
                    case "--export-fbx": { var dir = Next(); ExportObject(_view.Selected!, false, fbx: true, folder: dir); L($"script: exported FBX to {dir}"); break; }
                    case "--collision": await ToggleCollision(true); L("script: collision on"); break;
                    case "--xenia": _settings.XeniaPath = Next(); L($"script: xenia {_settings.XeniaPath}"); break;
                    case "--mod":
                    {
                        var id = Next(); bool on = Next() == "on";
                        _ws!.Manifest.ExeMods.Remove(id); if (on) _ws.Manifest.ExeMods.Add(id);
                        _ws.SaveManifest(); ApplyExeMods(); L($"script: mod {id} {(on ? "on" : "off")}"); break;
                    }
                    case "--data":
                    {
                        var id = Next(); float v = float.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture);
                        var ds = NB.Core.Mods.DataMods.All.First(x => x.Id == id);
                        int n = NB.Core.Mods.DataMods.Set(_ws!, _index!, ds, v);
                        L($"script: data {id} = {v} ({n} bundle(s)); read back {NB.Core.Mods.DataMods.Get(_ws!, _index!, ds)}"); break;
                    }
                    case "--menu-shot":
                    {
                        var name = Next(); var file = Next();
                        var mi = MainMenuStrip!.Items.OfType<ToolStripMenuItem>().First(x => x.Text.Replace("&", "") == name);
                        mi.ShowDropDown(); Application.DoEvents(); await Task.Delay(500); Application.DoEvents();
                        var dd = mi.DropDown;
                        using var bmp = new Bitmap(dd.Width, dd.Height); dd.DrawToBitmap(bmp, new Rectangle(0, 0, dd.Width, dd.Height));
                        bmp.Save(file); mi.HideDropDown();
                        foreach (ToolStripItem it in dd.Items) L("menu: " + it.Text + (it is ToolStripMenuItem t && t.Checked ? "  [x]" : ""));
                        break;
                    }
                    case "--duplicate":
                    {
                        var v = Next().Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                        await DuplicateObject(_view.Selected!, new Vector3(v[0], v[1], v[2]), confirm: false);
                        L($"script: duplicated, selected {_view.Selected?.Name} at {_view.Selected?.Transform.Translation}"); break;
                    }
                    case "--delete": { var n = _view.Selected!.Name; await DeleteObject(_view.Selected!, confirm: false); L($"script: deleted {n}"); break; }
                    case "--import-model": { var obj = Next(); await ImportModel(_view.Selected!, obj, confirm: false); L($"script: imported {obj}"); break; }
                    case "--import-model-geometry": { var obj = Next(); await ImportModel(_view.Selected!, obj, confirm: false, materials: false); L($"script: imported {obj} (geometry only)"); break; }
                    case "--import-dialog-shot":
                    {
                        // --import-dialog-shot <file.fbx> <png>: the import dialog of the selected object, captured, then cancelled
                        var obj = Next(); var png = Next(); var o = _view.Selected!;
                        var meshes = NB.Core.Models.ObjReader.ReadAny(obj);
                        int sym = _scene!.Caff.Symbols.FindIndex(s => AssetIds.DisplayName(s) == AssetIds.DisplayName(o.ModelName)) + 1;
                        var caff = _scene.Caff;
                        using var dlg = new Panels.ModelImportDialog(AssetIds.DisplayName(o.ModelName), 1, obj, meshes, op => NB.Core.Models.MaterialImport.MakePlan(caff, sym, meshes, op));
                        dlg.Show(this); Application.DoEvents(); await Task.Delay(400); Application.DoEvents();
                        using (var bmp = new Bitmap(dlg.Width, dlg.Height)) { dlg.DrawToBitmap(bmp, new Rectangle(0, 0, dlg.Width, dlg.Height)); bmp.Save(png); }
                        dlg.Close(); L($"script: import dialog captured {png}"); break;
                    }
                    case "--texlib": { OpenTextureLibrary(_view.Selected); await Task.Delay(200); await _texLib!.WaitForThumbnails(); L($"script: texture library: {string.Join(", ", _texLib.ListedStems.Select(x => x.Replace("aid_texture_banjox_", "")))}"); break; }
                    case "--texlib-world": { OpenTextureLibrary(null); await Task.Delay(200); await _texLib!.WaitForThumbnails(); L($"script: world texture library: {_texLib.ListedStems.Count()} texture(s)"); break; }
                    case "--texlib-select": { var q = Next(); L($"script: texture library select {q}: {_texLib!.SelectStem(q)}"); await Task.Delay(200); break; }
                    case "--texlib-shot": { var png = Next(); await Task.Delay(200); _texLib!.SaveShot(png); L($"script: texture library captured {png}"); break; }
                    case "--texlib-export": { var dir = Next(); int n = _texLib!.ExportTo(dir, _texLib.ListedStems.Select(x => _texLib.FindItem(x)!)); L($"script: texture library exported {n} to {dir}"); break; }
                    case "--texlib-replace":
                    case "--texlib-replace-model":
                    {
                        bool mo = a[i] == "--texlib-replace-model"; var q = Next(); var file = Next();
                        var item = _texLib!.FindItem(q) ?? throw new InvalidOperationException("--texlib-replace: no texture matches " + q);
                        var now = await _texLib.ReplaceWith(item, file, mo, confirm: false);
                        await _texLib.WaitForThumbnails();
                        L($"script: texture {item.Stem} replaced with {file}{(mo ? " (model only)" : "")}; model now uses {now}"); break;
                    }
                    case "--texlib-close": _texLib?.Close(); break;
                    case "--import-collision":
                    case "--import-collision-box":
                    {
                        bool bx = a[i] == "--import-collision-box"; var obj = Next();
                        await ImportCollision(_view.Selected!, obj, confirm: false, box: bx);
                        var sel = _view.Selected;
                        var cm = sel != null && _scene?.CollisionByModel != null && _scene.CollisionByModel.TryGetValue(sel.ModelName, out var ml) ? ml : null;
                        L($"script: imported collision {obj}{(bx ? " (box)" : "")}; overlay for {sel?.ModelName}: " +
                          (cm == null ? "none" : string.Join(", ", cm.Select(m => $"{m.Kind} {m.Positions.Count}v/{m.Triangles.Count / 3}t"))));
                        break;
                    }
                    case "--look":
                    {
                        int dx = int.Parse(Next()), dy = int.Parse(Next()); var b0 = _view.LookAngles;
                        _view.SimulateLook(dx, dy); var b1 = _view.LookAngles;
                        L($"script: look drag ({dx},{dy}): yaw {b0.Yaw:F3} -> {b1.Yaw:F3}, pitch {b0.Pitch:F3} -> {b1.Pitch:F3}"); break;
                    }
                    case "--focus": if (_view.Selected != null) _view.Focus(_view.Selected); break;
                    case "--camera":
                    {
                        var v = Next().Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                        _view.SetCamera(new Vector3(v[0], v[1], v[2]), v[3], v[4]); break;
                    }
                    case "--no-cull": _view.NoCull = Next() == "on"; L($"script: culling {(_view.NoCull ? "off" : "on")}"); break;
                    case "--bench":
                    {
                        int n = int.Parse(Next()); var (ms, objs, draws) = _view.Benchmark(n);
                        L($"script: bench {n} frames at {_view.CameraPosition}: {ms:F1} ms/frame ({1000 / ms:F0} fps), {objs} objects, {draws} draws"); break;
                    }
                    case "--shot": { using var b = _view.Capture(); b.Save(Next()); L("script: viewport screenshot saved"); break; }
                    case "--window":
                    {
                        Application.DoEvents();
                        using var b = new Bitmap(Width, Height); DrawToBitmap(b, new Rectangle(0, 0, Width, Height));
                        if (_center.SelectedIndex == 0 && !_start.Visible)
                        using (var g = Graphics.FromImage(b)) using (var v = _view.Capture())
                        {
                            var p = PointToClient(_view.PointToScreen(Point.Empty));
                            g.DrawImage(v, p.X + (Width - ClientSize.Width) / 2, p.Y + (Height - ClientSize.Height - (Width - ClientSize.Width) / 2));
                        }
                        b.Save(Next()); L("script: window screenshot saved"); break;
                    }
                    case "--screen":
                    {
                        // real screen pixels of the window (DrawToBitmap paints overlapping children in the wrong order)
                        Activate(); BringToFront(); Application.DoEvents(); await Task.Delay(600); Application.DoEvents();
                        using var b = new Bitmap(Width, Height); using (var g = Graphics.FromImage(b)) g.CopyFromScreen(Location, Point.Empty, Size);
                        b.Save(Next()); L($"script: screen captured (start page visible: {_start.Visible}, bounds {_start.Bounds})"); break;
                    }
                    case "--validate": { var r = BuildValidator.Validate(_ws!); foreach (var l in r) L(l); break; }
                    case "--audio-click": { int bk = int.Parse(Next()), sn = int.Parse(Next()); SelectCenter("Audio"); await Task.Delay(300); L("script: " + _audio.TestClick(bk, sn)); await Task.Delay(1500); break; }
                    case "--parts": { var f = Next(); _parts.LoadSpecs(f); SelectCenter("Part Importer"); L($"script: part specs {f}"); break; }
                    case "--part": { var id = Next(); _parts.SelectPart(id); L($"script: part {id}"); break; }
                    case "--importer-tab": { var t = Next(); _parts.SelectTab(t); L($"script: importer tab {t}"); break; }
                    case "--importer-validate": { bool ok = _parts.ValidateCurrent(); L($"script: importer validate -> {(ok ? "OK" : "errors")}"); break; }
                    case "--importer-build": { await _parts.BuildAsync(false); L("script: importer build finished; output: " + _parts.OutputText.Split('\n').LastOrDefault(x => x.Trim().Length > 0)?.Trim()); break; }
                    case "--importer-remove": { await _parts.RemoveAsync(false); L("script: importer remove finished"); break; }
                    case "--orbit": { int dx = int.Parse(Next()), dy = int.Parse(Next()); _parts.Orbit(dx, dy); L($"script: orbit {dx},{dy}"); break; }
                    case "--tab":
                    {
                        var q = Next();
                        foreach (var tc in new[] { _center, _right })
                            foreach (TabPage tp in tc.TabPages)
                                if (tp.Text.Equals(q, StringComparison.OrdinalIgnoreCase)) tc.SelectedTab = tp;
                        L($"script: tab {q}"); break;
                    }
                    case "--asset":
                    {
                        var q = Next();
                        var e = _index!.Entries.First(x => x.Name.Contains(q, StringComparison.OrdinalIgnoreCase) && !x.Streamed);
                        _center.SelectedIndex = 1; _preview.Show(_ws!, e, Log); _tags.ShowAsset(_ws!, e); L($"script: previewing {e.Name}"); break;
                    }
                    case "--log": i++; break;
                    case "--exit": L("script: exit"); Close(); return;
                    default: L("script: unknown argument " + a[i]); break;
                }
                await Task.Delay(50);
            }
        }
        catch (Exception e) { L("script error: " + e); }
    }

    // ------------------------------------------------------------------ helpers

    void SelectCenter(string name) { foreach (TabPage tp in _center.TabPages) if (tp.Text == name) _center.SelectedTab = tp; }

    void Log(string s)
    {
        if (InvokeRequired) { BeginInvoke(() => Log(s)); return; }
        _log.AppendText(s.Replace("\n", Environment.NewLine) + Environment.NewLine);
    }

    void Error(string what, Exception e) { Log($"ERROR: {what}: {e.Message}"); MessageBox.Show(this, $"{what}:\n{e.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }

    bool _busy;

    /// <summary>Shows progress. Progress&lt;T&gt; callbacks can arrive after an operation ended, so non-null
    /// updates are ignored unless an operation is running (see <see cref="Busy"/>).</summary>
    void SetProgress(string? text, double p)
    {
        if (InvokeRequired) { BeginInvoke(() => SetProgress(text, p)); return; }
        if (text != null && !_busy) return;
        _progress.Visible = text != null;
        _progress.Value = Math.Clamp((int)(p * 100), 0, 100);
        _status.Text = text ?? "";
    }
}
