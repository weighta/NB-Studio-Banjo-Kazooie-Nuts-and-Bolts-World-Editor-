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
    readonly DialoguePanel _dialogue = new() { Dock = DockStyle.Fill };
    readonly AudioPanel _audio = new() { Dock = DockStyle.Fill };
    readonly VideoPanel _video = new() { Dock = DockStyle.Fill };
    readonly TagEditorPanel _tags = new() { Dock = DockStyle.Fill };
    readonly PartImporterPanel _parts = new() { Dock = DockStyle.Fill };
    readonly LivePanel _live;
    readonly AtmospherePanel _atmos;
    readonly TextBox _log = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, ReadOnly = true, WordWrap = false, Font = new Font("Consolas", 9) };
    readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly ToolStripProgressBar _progress = new() { Visible = false, Width = 200 };
    readonly TabControl _center = new() { Dock = DockStyle.Fill };
    readonly TabControl _right = new() { Dock = DockStyle.Fill };
    readonly ContextMenuStrip _objMenu = new();
    ToolStripMenuItem? _viewCollision, _viewSelColl;
    ToolStripButton _undoBtn = null!, _redoBtn = null!;
    /// <summary>Where the 3D view's context menu was opened (its Paste puts the copy there).</summary>
    Point? _menuPoint;

    void SetSelectionCollision(bool on)
    {
        _view.ShowSelectionCollision = on;
        if (_viewSelColl != null && _viewSelColl.Checked != on) _viewSelColl.Checked = on;
    }
    TabControl _leftTabs = null!;
    ToolStrip _toolbar = null!;
    MenuStrip _menu = null!;
    /// <summary>Ctrl+Z / Ctrl+Y: transforms, path links and every workspace file an action writes.</summary>
    readonly UndoHistory _history = new();
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
        _atmos = new AtmospherePanel(() =>
        {
            if (_ws == null) return null;
            var img = XexFile.Read(File.ReadAllBytes(_ws.Game.Xex)).GetImage();
            return img.AsSpan((int)(NB.Core.Live.XeniaLive.TextStart - 0x82000000), 64).ToArray();
        }) { Dock = DockStyle.Fill };

        // ---- layout
        var left = _leftTabs = new TabControl { Dock = DockStyle.Fill };
        var tWorlds = new TabPage("Worlds"); tWorlds.Controls.Add(_worlds);
        var tScene = new TabPage("Scene"); tScene.Controls.Add(_tree); tScene.Controls.Add(_treeSearch);
        var tAssets = new TabPage("Assets"); tAssets.Controls.Add(_assets);
        left.TabPages.AddRange(new[] { tWorlds, tScene, tAssets });

        var c3d = new TabPage("3D View"); c3d.Controls.Add(_view);
        var cPrev = new TabPage("Asset Preview"); cPrev.Controls.Add(_preview);
        var cAtmos = new TabPage("Atmosphere"); cAtmos.Controls.Add(_atmos);
        var cText = new TabPage("Text"); cText.Controls.Add(_text);
        var cAudio = new TabPage("Audio"); cAudio.Controls.Add(_audio);
        var cVideo = new TabPage("Video"); cVideo.Controls.Add(_video);
        var cParts = new TabPage("Part Importer"); cParts.Controls.Add(_parts);
        _center.TabPages.AddRange(new[] { c3d, cPrev, cAtmos, cText, cAudio, cVideo, cParts });

        var rProps = new TabPage("Properties"); rProps.Controls.Add(_transform);
        var rTags = new TabPage("Tag Editor"); rTags.Controls.Add(_tags);
        var rLive = new TabPage("Live (game)"); rLive.Controls.Add(_live);
        var rDlg = _dialogueTab = new TabPage("Dialogue"); rDlg.Controls.Add(_dialogue);
        _right.TabPages.AddRange(new[] { rProps, rTags, rLive, rDlg });

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
        _start.TourRequested += StartTour;
        Controls.Add(_start); Controls.SetChildIndex(_start, 0);
        Controls.Add(_toolbar = BuildToolbar());
        var menu = _menu = BuildMenu(); MainMenuStrip = menu; Controls.Add(menu);
        Controls.Add(statusStrip);
        Load += (_, _) => { splitLR.SplitterDistance = 330; splitCR.FixedPanel = FixedPanel.Panel2; splitCR.SplitterDistance = Math.Max(400, splitCR.Width - 540); splitMain.SplitterDistance = Math.Max(300, splitMain.Height - 150); };

        // ---- wiring
        _worlds.DoubleClick += async (_, _) => { if (_worlds.SelectedItem is WorldItem wi) await OpenWorld(wi.Entry, wi.Act); };
        _view.SelectionChanged += OnSelection;
        _view.GrassChanged += s => Log("  " + s);
        _view.SelectionCollisionChanged += s => Log("Collision: " + s);
        _view.TextureSource = n => _scene?.LoadTexture(n);
        _view.EditStarted += (o, before) => _pendingBefore = before;
        _view.ObjectEdited += o => PushUndo(o, _pendingBefore, o.Transform);
        _view.ContextMenuRequested += (o, p) => { if (o != null) { _menuPoint = p; BuildObjectMenu(o); _objMenu.Show(_view, p); } };
        _transform.TransformChanged += (o, before) => { PushUndo(o, before, o.Transform); _view.Refresh3D(); UpdateTitle(); };
        _transform.LinkChanged += (o, before) => { _history.PushLink(o, before, o.Marker!.Link); _view.Refresh3D(); UpdateTitle(); Log($"{o.Name}: next path node {before} -> {o.Marker!.Link} (World > Save to write it)"); };
        _history.Limit = _settings.UndoSteps;
        _history.Log = Log;
        _history.Changed += UpdateUndoUi;
        _view.CollisionInfo += s => Log("Collision: " + s);
        _view.SScales = _settings.SScales;
        FormClosed += (_, _) =>
        {
            _history.Detach();
            // a test game still being started: give its controller back (the virtual pad would otherwise stay plugged in)
            if (_qtBoot is { IsCompleted: false }) { _qtCts?.Cancel(); try { using var pad = new QuickTest.VirtualPad(_qtPort); pad.Unplug(); } catch (Exception) { } }
        };
        _tree.AfterSelect += (_, e) => { if (!_syncingTree && e.Node?.Tag is SceneObject o) _view.Select(o, focus: true); };
        _tree.ShowNodeToolTips = true;
        _tree.AfterCheck += (_, e) => { if (e.Node?.Tag is SceneObject o) { o.Visible = e.Node.Checked; _view.Refresh3D(); } else if (e.Action != TreeViewAction.Unknown && e.Node != null) foreach (TreeNode c in e.Node.Nodes) c.Checked = e.Node.Checked; };
        _tree.NodeMouseClick += (_, e) => { if (e.Button == MouseButtons.Right && e.Node.Tag is SceneObject o) { _menuPoint = null; _tree.SelectedNode = e.Node; BuildObjectMenu(o); _objMenu.Show(_tree, e.Location); } };
        _treeSearch.TextChanged += (_, _) => FillTree();
        _assets.AssetActivated += e => { _center.SelectedIndex = 1; _preview.Show(_ws!, e, Log); _tags.ShowAsset(_ws!, e); };
        _preview.Log = Log;
        _tags.Log = Log;
        _dialogue.Log = Log;
        _text.Log = Log; _audio.Log = Log; _video.Log = Log; _parts.Log = Log;
        _audio.VgmstreamPath = FindUp(Path.Combine("thirdparty", "vgmstream", "vgmstream-cli.exe"));
        _tags.Changed += () => UpdateTitle();
        _atmos.Log = Log;
        _atmos.Changed += () => UpdateTitle();
        _atmos.ExeModsChanged += () => ApplyExeMods();
        _atmos.WorldChanged += async b =>
        {
            if (_scene == null || _sceneEntry == null || _scene.Bundle != b) return;
            if (_scene.Objects.Any(o => o.Dirty)) Log("The open world changed on disk: save or undo its transform edits, then reopen it to see the change.");
            else { var tab = _center.SelectedTab; await OpenWorld(_sceneEntry, _sceneAct); _center.SelectedTab = tab; }   // stay on the Atmosphere tab
        };

        Shown += async (_, _) =>
        {
            Log("Nuts & Bolts Mod Tool — open or create a workspace to begin (File menu).");
            var args = Environment.GetCommandLineArgs().Skip(1).ToList();
            if (args.Count > 0) { _scripted = true; await RunScript(args); return; }
            // first start ever: offer the beginner's tour
            if (!_settings.TourOffered)
            {
                _settings.TourOffered = true; _settings.Save();
                if (TourOverlay.AskWelcome(this)) { StartTour(); return; }
            }
            if (_settings.AutoOpenLast && _settings.LastWorkspace != null && File.Exists(Path.Combine(_settings.LastWorkspace, "workspace.json")))
                await OpenWorkspace(_settings.LastWorkspace);
        };
        FormClosing += (_, e) =>
        {
            if (_scripted) return;   // test runs end without questions
            if (_scene != null && _scene.Objects.Any(o => o.Dirty) &&
                MessageBox.Show(this, "There are unsaved world edits. Quit anyway?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) e.Cancel = true;
            else if (_atmos.HasUnsaved && !e.Cancel &&
                MessageBox.Show(this, "There are unsaved sky, light and fog changes (Atmosphere tab). Quit anyway?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) e.Cancel = true;
        };
    }

    // ------------------------------------------------------------------ beginner's tour, last world

    TourOverlay? _tour;
    bool _scripted;   // started with script options: no tour, no automatic reopening of the last world

    static Rectangle? Scr(Control c) => c.Visible && c.IsHandleCreated && c.Width > 0 ? c.RectangleToScreen(c.ClientRectangle) : null;
    static Rectangle? TabHeader(TabControl t, int i) => t.IsHandleCreated && i >= 0 && i < t.TabCount ? t.RectangleToScreen(t.GetTabRect(i)) : null;
    static int TabIndex(TabControl t, string text) { for (int i = 0; i < t.TabCount; i++) if (t.TabPages[i].Text == text) return i; return -1; }

    static Rectangle? TabHeaders(TabControl t, int from, int to)
    {
        if (!t.IsHandleCreated || from < 0 || to < from || to >= t.TabCount) return null;
        var r = Rectangle.Union(t.GetTabRect(from), t.GetTabRect(to));
        return t.RectangleToScreen(r);
    }

    /// <summary>
    /// The tour for people who have never modded: on the start page it explains workspaces and the menus, then
    /// (when a workspace is open) every part of the editor in plain words, ending with "your first mod in 5 steps".
    /// </summary>
    void StartTour()
    {
        _tour?.Dispose();
        var steps = new List<TourStep>();
        if (_ws == null || _start.Visible)
        {
            bool hasLast = _settings.LastWorkspace != null && File.Exists(Path.Combine(_settings.LastWorkspace, "workspace.json"));
            steps.Add(new("Welcome to NB Studio!",
                "NB Studio lets you change Banjo-Kazooie: Nuts & Bolts: its worlds, objects, vehicles, textures (the pictures painted on everything), sounds, text, and even how the game behaves.\n\n" +
                "This short tour shows you around. Use Next (or the right arrow key), Back, or Skip whenever you like. You can take it again any time from Help > Take the Tour."));
            steps.Add(new("Step 1: make a workspace",
                "A workspace is your own private copy of the game that you can change freely. Your real game folder is never touched, so you can't break it.\n\n" +
                "Click here and choose the folder where your game is (the one with default.xex in it). NB Studio copies what it needs; this takes a minute the first time.",
                () => _start.CardBounds(hasLast ? 2 : 1) is { Width: > 0 } r ? _start.RectangleToScreen(r) : null));
            steps.Add(new(hasLast ? "Continue or open" : "Open a workspace",
                (hasLast ? "Continue opens the workspace you used last time. " : "") +
                "Open Workspace lets you pick any workspace folder you made before. You can have as many workspaces as you like, for example one per mod.",
                () => _start.CardBounds(hasLast ? 0 : 0) is { Width: > 0 } r ? _start.RectangleToScreen(r) : null));
            steps.Add(new("The menus",
                "Everything else lives up here:\n" +
                "• File: workspaces, exporting your mod.\n" +
                "• World: save the world you changed.\n" +
                "• Build: play your mod in Xenia (F5) and pack it into one small file to share.\n" +
                "• Mods: one-click changes to the game itself (for example, drive any vehicle in town).\n" +
                "• Tools: handy extras, like the Xbox 360 Photo Viewer.\n" +
                "• Help: controls and this tour.",
                () => Scr(_menu)));
            steps.Add(new("Now open a workspace",
                "Make or open a workspace now. As soon as it opens, the tour continues inside the editor and shows you every part of it.",
                null, () => { _settings.TourPending = _ws == null; _settings.Save(); }));
        }
        else
        {
            steps.Add(new("Your workspace is open",
                "The editor has four areas:\n• Left: lists of worlds, objects and game files.\n• Middle: the 3D view and other editors.\n• Right: details of what you selected.\n• Bottom: messages.\n\nLet's look at each one."));
            steps.Add(new("Worlds",
                "Every place in the game: Showdown Town, Nutty Acres, Banjoland and more. Double-click one to walk around it in 3D.\n\n" +
                "Lines that start with ↳ are Acts (the challenges). They use the same world with their own objects.",
                () => Scr(_leftTabs), () => _leftTabs.SelectedIndex = 0));
            steps.Add(new("The 3D view",
                "Your world, in 3D.\n• Look around: hold the right mouse button and move the mouse.\n• Fly: W A S D, Q and E for down and up, Shift to go faster. Tip: hold the right mouse button while you fly, then S always flies backwards (otherwise, with something selected, S scales it).\n" +
                "• The buttons in the top-right corner switch the view: Wireframe, Solid, Textured, or Rendered (lit like the game). Collision shows what Banjo and the vehicles bump into.\n" +
                "• Select: left-click an object.\n• Move it: press G and move the mouse, then click to drop it. Press X, Y or Z while moving to slide along one direction only.\n" +
                "• Scale it: press S (with X, Y or Z for one direction).\n• Made a mistake? Ctrl+Z undoes it.",
                () => Scr(_center), () => _center.SelectedIndex = 0));
            steps.Add(new("The toolbar",
                "Quick buttons for the tools: Select, Move, Rotate and Scale.\n\nUndo / Redo (Ctrl+Z / Ctrl+Y) take back any change. Save World (Ctrl+S) writes your changes into the workspace. Test in Xenia (F5) starts your modded game right in the world you have open, so you can try it at once.",
                () => Scr(_toolbar)));
            steps.Add(new("Scene",
                "A list of everything in the open world: buildings, trees, pickups, characters, AI paths and more. Click a name to jump to it in 3D. Untick a box to hide that object while you work. The search box finds things by name.",
                () => Scr(_leftTabs), () => _leftTabs.SelectedIndex = 1));
            steps.Add(new("Assets",
                "Every file inside the game: textures, 3D models, sounds, music, scripts and more. Use the filter to find things. Double-click one to look at it, right-click to export it or replace it with your own.",
                () => Scr(_leftTabs), () => _leftTabs.SelectedIndex = 2));
            steps.Add(new("Asset Preview",
                "Shows the asset you picked: a texture, a model or a sound. This is where you swap a texture for your own picture or a model for your own.\n\n" +
                "Tip: World > Texture Library shows every texture of the open world, and Replace from Folder swaps many at once (that's how Snowy Showdown Town got its snow).",
                () => TabHeader(_center, TabIndex(_center, "Asset Preview")), () => _center.SelectedIndex = TabIndex(_center, "Asset Preview")));
            steps.Add(new("Atmosphere",
                "The mood of a world, for each time of day: the sky, the sunlight (light) and the fog. Pick colours, slide the brightness, and watch it change live in a running game.\n\n" +
                "World > Weather adds falling snow. Together, these are how Snowy Showdown Town was made.",
                () => TabHeader(_center, TabIndex(_center, "Atmosphere")), () => { int i = TabIndex(_center, "Atmosphere"); if (i >= 0) _center.SelectedIndex = i; }));
            steps.Add(new("Text, Audio, Video, Part Importer",
                "• Text: change what characters say, in every language.\n• Audio: swap music and sound effects.\n• Video: replace the game's videos.\n• Part Importer: make brand-new vehicle parts for Mumbo's garage.",
                () => TabHeaders(_center, TabIndex(_center, "Text"), TabIndex(_center, "Part Importer"))));
            steps.Add(new("Properties",
                "The exact numbers for the object you selected: where it is (position), which way it faces (rotation) and how big it is (scale). Type a number to place things precisely.",
                () => Scr(_right), () => { _right.SelectedIndex = 0; _center.SelectedIndex = 0; }));
            steps.Add(new("Tag Editor",
                "An object's settings, called tags: how fast, how strong, how much health, which model it uses. Change a value and save. Many fun mods are only a few numbers here.",
                () => Scr(_right), () => _right.SelectedIndex = 1));
            steps.Add(new("Live (game)",
                "Connects to the game while it runs in Xenia: see where you are, teleport, change gravity, and jump the 3D view to where you are in the game.",
                () => Scr(_right), () => _right.SelectedIndex = 2));
            steps.Add(new("Messages",
                "What NB Studio did, and any warnings. If something doesn't work, the reason is usually written here.",
                () => Scr(_log), () => _right.SelectedIndex = 0));
            steps.Add(new("Your first mod in 5 steps",
                "1. In Worlds, double-click Showdown Town.\n2. Click any object, for example a lamp post.\n3. Press G, move the mouse, click to drop it.\n4. Save the world: World > Save (Ctrl+S).\n5. Press F5 to play it in Xenia.\n\n" +
                "Happy with it? Build > Create Distributable Patch packs your mod into one small file you can share, or play in NB Multiplayer. Have fun!",
                () => Scr(_menu), () => { _leftTabs.SelectedIndex = 0; _center.SelectedIndex = 0; }));
        }
        _tour = new TourOverlay(this, steps);
        _tour.Finished += () => BeginInvoke(() => { _tour?.Dispose(); _tour = null; });
        _tour.Start();
    }

    static string WorldKey(WorldEntry w, ActEntry? act) => $"{w.Bundle:x6}|{(act != null ? act.ActBundle.ToString("x6") : "")}";

    /// <summary>Reopens the world (or Act) that was last open in this workspace, so you're back where you were.</summary>
    async Task OpenLastWorld()
    {
        if (_ws == null || !_settings.LastWorlds.TryGetValue(_ws.Root, out var key)) return;
        var item = _worlds.Items.OfType<WorldItem>().FirstOrDefault(i => WorldKey(i.Entry, i.Act) == key);
        if (item == null) return;
        _worlds.SelectedItem = item;
        _center.SelectedIndex = 0;
        Log($"Reopening {(item.Act?.Display ?? item.Entry.Display)} (last world viewed in this workspace).");
        await OpenWorld(item.Entry, item.Act);
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
        // light gray nut: NB Studio's own colour (NB Multiplayer's nut is gold), visible on dark title bars and taskbars
        var p = Path.Combine(AppContext.BaseDirectory, "Assets", "nut_gray.png");
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
        var recent = new ToolStripMenuItem("Open &Recent");
        recent.DropDownItems.Add("(none)");   // filled when opened
        recent.DropDownOpening += (_, _) => FillRecent(recent);
        file.DropDownItems.Add(recent);
        file.DropDownItems.Add("Validate Original Game Directory…", null, (_, _) => ValidateOriginal());
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("&Settings…", null, (_, _) => ShowSettings(), Keys.Control | Keys.Oemcomma) { ShortcutKeyDisplayString = "Ctrl+," });
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("E&xit", null, (_, _) => Close());

        var edit = new ToolStripMenuItem("&Edit");
        // the keys (Ctrl+Z, Ctrl+Y, Ctrl+C, Ctrl+X, Ctrl+V, Del) are handled in ProcessCmdKey, so that text boxes keep theirs
        var undoItem = new ToolStripMenuItem("&Undo", null, async (_, _) => await Undo()) { ShortcutKeyDisplayString = "Ctrl+Z" };
        var redoItem = new ToolStripMenuItem("&Redo", null, async (_, _) => await Redo()) { ShortcutKeyDisplayString = "Ctrl+Y" };
        var histItem = new ToolStripMenuItem("(no changes yet)") { Enabled = false };
        var cutItem = new ToolStripMenuItem("Cu&t Object", null, async (_, _) => await CutSelection()) { ShortcutKeyDisplayString = "Ctrl+X" };
        var copyItem = new ToolStripMenuItem("&Copy Object", null, (_, _) => CopySelection()) { ShortcutKeyDisplayString = "Ctrl+C" };
        var pasteItem = new ToolStripMenuItem("&Paste Object", null, async (_, _) => await PasteClipboard()) { ShortcutKeyDisplayString = "Ctrl+V",
            ToolTipText = "Pastes a copy where the mouse points in the 3D view (on the ground or an object), keeping its rotation and size. From this menu: at the centre of the view." };
        var delItem = new ToolStripMenuItem("&Delete Object", null, async (_, _) => await DeleteSelection()) { ShortcutKeyDisplayString = "Del" };
        edit.DropDownItems.AddRange(new ToolStripItem[] { undoItem, redoItem, histItem, new ToolStripSeparator(), cutItem, copyItem, pasteItem, delItem, new ToolStripSeparator() });
        edit.DropDownOpening += (_, _) =>
        {
            undoItem.Text = _history.UndoLabel is { } u ? "&Undo " + MenuText(u) : "&Undo";
            redoItem.Text = _history.RedoLabel is { } r ? "&Redo " + MenuText(r) : "&Redo";
            undoItem.Enabled = _history.CanUndo; redoItem.Enabled = _history.CanRedo;
            histItem.Text = _history.Count == 0 ? "(no changes to undo yet)" : $"{_history.Count} step(s) can be undone (File > Settings: up to {_history.Limit})";
            var sel = _view.Selected;
            bool can = sel?.Kind == SceneObjectKind.Scenery && sel.Instance != null;
            cutItem.Enabled = copyItem.Enabled = delItem.Enabled = can;
            pasteItem.Enabled = _clip.Count > 0 && _scene != null;
            pasteItem.Text = _clip.Count > 0 ? "&Paste " + MenuText(_clip[0].Name) : "&Paste Object";
        };
        edit.DropDownClosed += (_, _) => { foreach (ToolStripItem i in edit.DropDownItems) if (i != histItem) i.Enabled = true; };
        edit.DropDownItems.Add(new ToolStripMenuItem("Undo Last &Bundle Save (import / duplicate / delete)", null, async (_, _) => await UndoLastBundleSave()));
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add(new ToolStripMenuItem("Reset Selected Transform", null, (_, _) => { if (_view.Selected is { } o) ResetTransform(o); }));

        var world = new ToolStripMenuItem("&World");
        world.DropDownItems.Add(new ToolStripMenuItem("&Save World Changes to Workspace", null, (_, _) => SaveWorld(), Keys.Control | Keys.S));
        world.DropDownItems.Add("Texture &Library (all textures of this world)…", null, (_, _) => OpenTextureLibrary(null));
        world.DropDownItems.Add("&Atmosphere: Sky, Light && Fog…", null, (_, _) => ShowAtmosphere(null));
        world.DropDownItems.Add("&Weather (Falling Snow)…", null, (_, _) => ShowAtmosphere("weather"));
        world.DropDownItems.Add(new ToolStripSeparator());
        world.DropDownItems.Add("Export Whole Scene as OBJ…", null, (_, _) => ExportScene());
        world.DropDownItems.Add("Export Scene Placement List (CSV)…", null, (_, _) => ExportPlacements());

        var view = new ToolStripMenuItem("&View");
        var vT = new ToolStripMenuItem("Terrain") { Checked = true, CheckOnClick = true }; vT.CheckedChanged += (_, _) => { _view.ShowTerrain = vT.Checked; _view.Refresh3D(); };
        var vS = new ToolStripMenuItem("Scenery") { Checked = true, CheckOnClick = true }; vS.CheckedChanged += (_, _) => { _view.ShowScenery = vS.Checked; _view.Refresh3D(); };
        // shading of the 3D view (also the bar in its top-right corner; Shift+Z cycles); remembered between sessions
        var vMode = new ToolStripMenuItem("View &Mode");
        foreach (var vm in Enum.GetValues<ViewMode>())
        {
            var mi = new ToolStripMenuItem(vm.ToString()) { Tag = vm, Checked = _view.ViewMode == vm };
            mi.Click += (_, _) => _view.ViewMode = vm;
            vMode.DropDownItems.Add(mi);
        }
        _view.ViewModeChanged += m => { foreach (ToolStripMenuItem mi in vMode.DropDownItems) mi.Checked = (ViewMode)mi.Tag! == m; };
        var vM = new ToolStripMenuItem("Markers (actors, pickups, paths)") { Checked = true, CheckOnClick = true }; vM.CheckedChanged += (_, _) => { _view.ShowMarkers = vM.Checked; _view.Refresh3D(); };
        var vC = new ToolStripMenuItem("Collision of All Objects (Havok)") { CheckOnClick = true,
            ToolTipText = "Wireframe of the Havok collision of everything in the world: terrain cyan, scenery yellow, objects placed by markers green (also the Collision button next to the view-mode bar)." };
        vC.CheckedChanged += (_, _) => { if (_view.ShowCollision != vC.Checked) _view.ShowCollision = vC.Checked; };
        _view.ShowCollisionChanged += on => { if (vC.Checked != on) vC.Checked = on; };
        _viewCollision = vC;
        var vP = new ToolStripMenuItem("Paths (path-node links)") { Checked = true, CheckOnClick = true };
        vP.CheckedChanged += (_, _) => { _view.ShowPaths = vP.Checked; _view.Refresh3D(); };
        var vO = new ToolStripMenuItem("Objects at Markers (buildings, characters, pickups)") { Checked = _view.ShowObjects, CheckOnClick = true,
            ToolTipText = "Models the game places with markers, like L.O.G.'s palace, Jiggy bank, characters and notes. Off: markers are small boxes (faster)." };
        vO.CheckedChanged += (_, _) => { _view.ShowObjects = vO.Checked; _view.Refresh3D(); };
        var vSC = _viewSelColl = new ToolStripMenuItem("Collision of Selection") { CheckOnClick = true, ToolTipText = "Magenta wireframe of the selected object's own Havok collision, on top of everything (also in the object's right-click menu)." };
        vSC.CheckedChanged += (_, _) => { if (_view.ShowSelectionCollision != vSC.Checked) SetSelectionCollision(vSC.Checked); };
        var vG = new ToolStripMenuItem("Grass (chunk-17 grass layers)") { Checked = _view.ShowGrass, CheckOnClick = true,
            ToolTipText = "Grass tiles laid out like the game: the grass model at every cell of each layer with density, lifted by the layer's height texture and coloured by its shadow texture (time-of-day variant of the current light)." };
        vG.CheckedChanged += (_, _) => _view.ShowGrass = vG.Checked;
        view.DropDownItems.AddRange(new ToolStripItem[] { vMode, new ToolStripSeparator(), vT, vS, vO, vG, vM, vP, vC, vSC });

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
        build.DropDownItems.Add("Create Patch from a Modified Game Folder…", null, async (_, _) => await CreatePatchFromFolder());
        build.DropDownItems.Add("Apply Patch to a Game Directory…", null, async (_, _) => await ApplyPatch());
        build.DropDownItems.Add("Roll Back Patches in a Game Directory…", null, (_, _) => RollbackPatch());
        build.DropDownItems.Add("Show Patch History of a Game Directory…", null, (_, _) => ShowPatchHistory());
        build.DropDownItems.Add(new ToolStripSeparator());
        build.DropDownItems.Add(new ToolStripMenuItem("&Test in Xenia: Play the Open World", null, async (_, _) => await QuickTestXenia(false), Keys.F5)
            { ToolTipText = "Starts the world (or Act) open in the 3D view in Xenia, skipping the title screen, the menus and the intro. Your workspace and your saves are not changed (a linked test copy and its own save are used)." });
        build.DropDownItems.Add(new ToolStripMenuItem("Test in Xenia from the 3D-View &Camera", null, async (_, _) => await QuickTestXenia(true), Keys.Shift | Keys.F5)
            { ToolTipText = "Like F5, then moves Banjo (or his vehicle) to the ground below the 3D view's camera." });
        build.DropDownItems.Add(new ToolStripMenuItem("&Launch Workspace in Xenia (title screen)", null, (_, _) => LaunchXenia(), Keys.Control | Keys.F5));
        build.DropDownItems.Add(new ToolStripMenuItem("Reset Test Save (vehicles saved during tests)…", null, async (_, _) => await ResetTestSave())
            { ToolTipText = "Test in Xenia keeps its own save per workspace: vehicles you save in Mumbo's garage during a test are there in the next test. This empties it (your NB Multiplayer and Xenia saves are never touched)." });
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
        tools.DropDownItems.Add("Import Source Map (.vmf)… (Hammer map → playable Showdown Town geometry)", null, async (_, _) => await ImportVmf(null, null));
        tools.DropDownItems.Add("Xbox 360 Photo Viewer (drop console photo packages)…", null, (_, _) => new Panels.PhotoViewerForm().Show(this));
        tools.DropDownItems.Add("Decompress an xcompress (0FF512ED) File…", null, (_, _) => DecompressFile());
        tools.DropDownItems.Add("Executable (default.xex) Info / Extract PE…", null, (_, _) => XexInfo());
        tools.DropDownItems.Add("Bulk Export Assets (current Asset filter)…", null, async (_, _) => { _busy = true; try { await _assets.BulkExport(this, _ws, Log, SetProgress); } finally { _busy = false; SetProgress(null, 0); } });

        var mods = new ToolStripMenuItem("&Mods");
        mods.DropDownOpening += (_, _) =>
        {
            mods.DropDownItems.Clear();
            mods.DropDownItems.Add(new ToolStripLabel("After-Party mods (executable patches, saved in the workspace and baked into default.xex by Create Patch)") { Font = new Font(Font, FontStyle.Italic) });
            var builtIn = BuiltInExeMods();
            foreach (var m in NB.Core.Mods.ExePatches.All)
            {
                if (NB.Core.Mods.ExePatches.FamilyOf(m.Id).Length > 0) continue;   // part limit / build area: set by value below
                // a mod already built into this workspace's default.xex (e.g. by an applied map mod): shown ticked, nothing to switch
                bool inGame = builtIn.Contains(m.Id);
                var item = new ToolStripMenuItem(m.Name.Replace("&", "&&") + (inGame ? "   (already in this game)" : "")) { CheckOnClick = !inGame, Checked = inGame || _ws?.Manifest.ExeMods.Contains(m.Id) == true, Enabled = _ws != null && !inGame,
                    ToolTipText = (inGame ? "This workspace's default.xex already has this mod built in (from a mod applied to the game files); it is always on.\n\n" : "") + m.Description + "\n\n" + m.Verified };
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
            "3D view:\n  Right-drag: look    WASD / Q E or the arrow keys: fly (Shift = fast; with a selection S scales it, except while you fly: right button held or W A D Q E just used; File > Settings)\n  Wheel: dolly    Middle-drag: pan    Left-click: select    F: focus selection    Esc: deselect\n  View modes: the bar in the top-right corner (Wire, Solid, Texture, Render) or Shift+Z. Render uses the level's light setup (click the light line to switch).\n\nTransforms (Blender style), with an object selected:\n  G move on the camera plane, R rotate, S scale; then X / Y / Z constrain to that world axis (drawn as a line in the axis colour;\n  press again for the object's own axis, again for free). Type a value (e.g. G Z 5 Enter); Ctrl snaps.\n  Left click / Enter confirms (one undo step), right click / Esc cancels.\n  1 / 2 / 3: move / rotate / scale gizmo; drag the selection with the left button (ground plane), or drag an axis handle\n  (or hold X, Y or Z) to constrain to that axis.\n  Right-click an object for its context menu.\n\nEdits are held in memory until World > Save (Ctrl+S) writes the bundle into the workspace.\nCtrl+Z / Ctrl+Y undo and redo any change, including imports, duplicates, deletes and saved tag or atmosphere edits (File > Settings: number of steps).\nCtrl+C / Ctrl+X copy / cut the selected object, Ctrl+V pastes a copy where the mouse points, Del deletes it (all undoable).\nF5 plays the open world in Xenia (no title screen or menus), Shift+F5 starts at the 3D view's camera, Ctrl+F5 starts at the title screen.", "Controls"));
        help.DropDownItems.Add("Take the Tour (for beginners)", null, (_, _) => StartTour());
        help.DropDownItems.Add("File Format Notes (docs)", null, (_, _) => OpenDocs());
        ms.Items.AddRange(new ToolStripItem[] { file, edit, world, view, build, tools, mods, help });
        return ms;
    }

    ToolStrip BuildToolbar()
    {
        var ts = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
        _undoBtn = new ToolStripButton("Undo", null, async (_, _) => await Undo()) { Enabled = false, ToolTipText = "Undo (Ctrl+Z)" };
        _redoBtn = new ToolStripButton("Redo", null, async (_, _) => await Redo()) { Enabled = false, ToolTipText = "Redo (Ctrl+Y)" };
        ts.Items.Add(_undoBtn); ts.Items.Add(_redoBtn); ts.Items.Add(new ToolStripSeparator());
        ToolStripButton Mode(string t, GizmoMode m) { var b = new ToolStripButton(t) { CheckOnClick = true, Checked = m == _view.Mode }; b.Click += (_, _) => { _view.Mode = m; foreach (var i in ts.Items.OfType<ToolStripButton>().Where(x => x.Tag as string == "mode")) i.Checked = i == b; }; b.Tag = "mode"; return b; }
        ts.Items.Add(Mode("Select", GizmoMode.Select));
        ts.Items.Add(Mode("Move (1)", GizmoMode.Move));
        ts.Items.Add(Mode("Rotate (2)", GizmoMode.Rotate));
        ts.Items.Add(Mode("Scale (3)", GizmoMode.Scale));
        ts.Items.Add(new ToolStripSeparator());
        ts.Items.Add(new ToolStripButton("Save World (Ctrl+S)", null, (_, _) => SaveWorld()));
        ts.Items.Add(new ToolStripButton("Test in Xenia (F5)", null, async (_, _) => await QuickTestXenia(false)) { ToolTipText = "Play the open world in Xenia: no title screen, menus or intro (Shift+F5: start at the 3D view's camera; Ctrl+F5: title screen)" });
        return ts;
    }

    void BuildObjectMenu(SceneObject o)
    {
        _objMenu.Items.Clear();
        _objMenu.Items.Add(new ToolStripLabel(o.Name) { Font = new Font(Font, FontStyle.Bold) });
        _objMenu.Items.Add("Focus Camera on Object", null, (_, _) => _view.Focus(o));
        _objMenu.Items.Add("Edit Properties", null, (_, _) => { _view.Select(o); _right.SelectedIndex = 1; });
        if (o.Kind == SceneObjectKind.Marker && o.Marker!.AssetIds.Any(a => a >> 24 == 0x1F))
            _objMenu.Items.Add("Dialogue…", null, (_, _) => { _view.Select(o); _right.SelectedTab = _dialogueTab; });
        _objMenu.Items.Add("Export Model as OBJ…", null, (_, _) => ExportObject(o, false));
        _objMenu.Items.Add("Export Model as OBJ (with world transform)…", null, (_, _) => ExportObject(o, true));
        _objMenu.Items.Add("Export Model as FBX…", null, (_, _) => ExportObject(o, false, fbx: true));
        _objMenu.Items.Add("Export Model as FBX (with world transform)…", null, (_, _) => ExportObject(o, true, fbx: true));
        _objMenu.Items.Add(new ToolStripSeparator());
        var reset = _objMenu.Items.Add("Reset Transform", null, (_, _) => ResetTransform(o)); reset.Enabled = o.Kind != SceneObjectKind.Terrain;
        var sc = new ToolStripMenuItem("Show Collision of Selection") { Checked = _view.ShowSelectionCollision, ToolTipText = "Magenta wireframe of this object's own Havok collision (the aid_havok asset of each of its models), drawn on top." };
        sc.Click += (_, _) => { _view.Select(o); SetSelectionCollision(!_view.ShowSelectionCollision); };
        _objMenu.Items.Add(sc);
        _objMenu.Items.Add(new ToolStripMenuItem("Hide in Editor", null, (_, _) => { o.Visible = false; FillTree(); _view.Refresh3D(); }));
        _objMenu.Items.Add(new ToolStripSeparator());
        var imp = _objMenu.Items.Add("Import Model (replace geometry with OBJ/FBX)…", null, async (_, _) => await ImportModel(o));
        imp.Enabled = o.Kind == SceneObjectKind.Scenery;
        imp.ToolTipText = "Replaces this object's reference model (all its instances) with an OBJ/FBX, including its materials: each material's texture is imported into this world.";
        var texl = _objMenu.Items.Add("Textures… (view / export / replace)", null, (_, _) => OpenTextureLibrary(o));
        texl.Enabled = o.Model != null && o.Kind != SceneObjectKind.Marker;   // objects placed by markers: their model may live in another bundle (World > Texture Library lists every texture)
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
        var cp = new ToolStripMenuItem("Copy", null, (_, _) => { _view.Select(o); CopySelection(); }) { ShortcutKeyDisplayString = "Ctrl+C", Enabled = o.Kind == SceneObjectKind.Scenery };
        var cu = new ToolStripMenuItem("Cut", null, async (_, _) => { _view.Select(o); await CutSelection(); }) { ShortcutKeyDisplayString = "Ctrl+X", Enabled = o.Kind == SceneObjectKind.Scenery };
        var pa = new ToolStripMenuItem(_clip.Count > 0 ? "Paste " + MenuText(_clip[0].Name) + " Here" : "Paste", null, async (_, _) => await PasteClipboard(_menuPoint)) { ShortcutKeyDisplayString = "Ctrl+V", Enabled = _clip.Count > 0 };
        _objMenu.Items.Add(cp); _objMenu.Items.Add(cu); _objMenu.Items.Add(pa);
        var del = new ToolStripMenuItem("Delete", null, async (_, _) => await DeleteObject(o, confirm: false)) { ShortcutKeyDisplayString = "Del" };
        _objMenu.Items.Add(del);
        del.Enabled = o.Kind == SceneObjectKind.Scenery;
        del.ToolTipText = "Removes the instance from the playable world (zero scale, moved far below the level). Instance indices are kept because the game refers to them. Ctrl+Z brings it back.";
    }

    // ------------------------------------------------------------------ workspace

    /// <summary>File > Open Recent: the workspaces opened last, newest first (missing folders are greyed out).</summary>
    void FillRecent(ToolStripMenuItem menu)
    {
        menu.DropDownItems.Clear();
        var list = RecentWorkspaces();
        if (list.Count == 0) { menu.DropDownItems.Add(new ToolStripMenuItem("(no workspaces opened yet)") { Enabled = false }); return; }
        int k = 0;
        foreach (var root in list)
        {
            bool ok = File.Exists(Path.Combine(root, "workspace.json"));
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
            bool current = _ws != null && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(_ws.Root)), root, StringComparison.OrdinalIgnoreCase);
            var item = new ToolStripMenuItem((++k <= 9 ? $"&{k}  " : "    ") + MenuText(name) + (ok ? "" : "  (missing)"))
            {
                ToolTipText = root, Enabled = ok, Checked = current,
                ShortcutKeyDisplayString = root.Length > 60 ? "…" + root[^57..] : root,
            };
            item.Click += async (_, _) => await OpenWorkspace(root);
            menu.DropDownItems.Add(item);
        }
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add("Remove Missing Folders", null, (_, _) => { _settings.RecentWorkspaces.RemoveAll(r => !File.Exists(Path.Combine(r, "workspace.json"))); _settings.Save(); });
        menu.DropDownItems.Add("Clear List", null, (_, _) =>
        {
            if (MessageBox.Show(this, "Clear the list of recent workspaces? The workspaces themselves are not touched.", Text, MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            _settings.RecentWorkspaces.Clear(); _settings.RecentSeeded = true; _settings.Save();
        });
    }

    /// <summary>The recent list; the first time, it starts from the workspaces NB Studio and NB Multiplayer opened before.</summary>
    List<string> RecentWorkspaces()
    {
        if (!_settings.RecentSeeded)
        {
            _settings.RecentSeeded = true;
            try
            {
                foreach (var e in ProjectRegistry.Load().OrderBy(e => e.LastOpened))
                    if (File.Exists(Path.Combine(e.Path, "workspace.json"))) _settings.AddRecent(e.Path);
            }
            catch (Exception) { }
            if (_settings.LastWorkspace != null && File.Exists(Path.Combine(_settings.LastWorkspace, "workspace.json"))) _settings.AddRecent(_settings.LastWorkspace);
            if (_ws != null) _settings.AddRecent(_ws.Root);
            _settings.Save();
        }
        return _settings.RecentWorkspaces.ToList();
    }

    /// <summary>File > Settings.</summary>
    void ShowSettings()
    {
        using var d = new Panels.SettingsDialog(_settings, _history.Count, _ws?.Manifest.ExeMods);
        if (d.ShowDialog(this) != DialogResult.OK) return;
        _settings.Save();
        _history.Limit = _settings.UndoSteps; _history.ApplyLimit();
        _view.SScales = _settings.SScales;
        _start.SetAutoOpen(_settings.AutoOpenLast);
        Log($"Settings saved: {_settings.UndoSteps} undo steps, S key {(_settings.SScales ? "scales the selection" : "flies backwards")}, " +
            $"{(_settings.AutoOpenLast ? "opens the last workspace at start" : "starts on the start page")}.");
    }

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
            var created = await Task.Run(() => Workspace.Create(src.SelectedPath, dst.SelectedPath, new Progress<(string F, double P)>(p => BeginInvoke(() => SetProgress("Copying " + p.F, p.P)))));
            ApplyNewWorkspaceMods(created);
            await OpenWorkspace(dst.SelectedPath);
        }
        catch (Exception e) { Error("Creating workspace failed", e); }
        finally { _busy = false; SetProgress(null, 0); }
    }

    /// <summary>A new workspace gets the mods of File > Settings > Mods for new workspaces (only those that fit its
    /// default.xex). Existing workspaces are never changed.</summary>
    void ApplyNewWorkspaceMods(Workspace ws)
    {
        try
        {
            var xex = XexFile.Read(File.ReadAllBytes(ws.Game.Xex)); var img = xex.GetImage();
            var ok = new List<string>(); var skipped = new List<string>();
            foreach (var id in _settings.NewWorkspaceModsOrDefault.Distinct())
            {
                var m = NB.Core.Mods.ExePatches.Resolve(id);
                if (m == null) { skipped.Add(id); continue; }
                if (NB.Core.Mods.ExePatches.Check(img, xex.ImageBase, m).Count > 0 && !NB.Core.Mods.ExePatches.IsApplied(img, xex.ImageBase, m)) { skipped.Add(m.Name); continue; }
                ok.Add(id);
            }
            ws.Manifest.ExeMods.Clear(); ws.Manifest.ExeMods.AddRange(ok);
            ws.SaveManifest();
            Log($"New workspace: {ok.Count} mod(s) ticked from File > Settings > Mods for new workspaces" + (skipped.Count > 0 ? $" (left out, they don't fit this default.xex: {string.Join(", ", skipped)})" : "") + ".");
        }
        catch (Exception e) { Log("New workspace: default mods not set: " + e.Message); }
    }

    async Task OpenWorkspace(string root)
    {
        try
        {
            _start.SetStatus($"Opening {Path.GetFileName(root.TrimEnd('\\', '/'))}…"); Application.DoEvents();
            _ws = Workspace.Open(root);
            _history.Attach(_ws);
            _settings.LastWorkspace = root; _settings.AddRecent(root); _settings.Save();
            ProjectRegistry.Touch(root);   // shared with NB Multiplayer's Projects page
            Log($"Workspace: {_ws.Root}\n  original (read-only): {_ws.Original.Root}\n  changes logged: {_ws.Manifest.Changes.Count}");
            await LoadIndex(false);
            UpdateTitle();
            _start.Visible = false;
            if (_settings.TourPending) { _settings.TourPending = false; _settings.Save(); StartTour(); }
            else if (!_scripted) await OpenLastWorld();
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
        try { _text.SetWorkspace(_ws); _audio.SetWorkspace(_ws, _index); _video.SetWorkspace(_ws); _dialogue.SetWorkspace(_ws, _index); } catch (Exception e) { Log("Media panels: " + e.Message); }
        try { _parts.SetWorkspace(_ws, _index); } catch (Exception e) { Log("Part importer: " + e.Message); }
        try { _atmos.SetWorkspace(_ws, _index); } catch (Exception e) { Log("Atmosphere: " + e.Message); }
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

    /// <summary>Tools > Import Source Map: the options dialog (with the plan and size estimate), then the import
    /// (NB.Core.SourceEngine.VmfImporter: scene folder in &lt;workspace&gt;\imports\&lt;map&gt;, built into Showdown Town), then
    /// the world is reopened. <paramref name="script"/> (automation): imports with these options without the dialog.</summary>
    async Task ImportVmf(string? file, NB.Core.SourceEngine.VmfImportOptions? script)
    {
        if (_ws == null || _index == null) { MessageBox.Show(this, "Open a workspace first.", "Import Source Map"); return; }
        var ws = _ws; var idx = _index;
        var o = script;
        if (o == null)
        {
            NB.Core.Formats.CaffFile? world = null;
            try { world = ws.LoadResident(NB.Core.SourceEngine.VmfImportOptions.ShowdownTown); } catch (Exception) { }
            using var d = new Panels.VmfImportDialog(file, world);
            if (d.ShowDialog(this) != DialogResult.OK) return;
            file = d.MapPath; o = d.Options;
        }
        if (file == null) return;
        if (_scene != null && _scene.Objects.Any(x => x.Dirty) && script == null &&
            MessageBox.Show(this, "Unsaved world edits will be lost when Showdown Town is rebuilt. Continue?", "Import Source Map", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        _busy = true; SetProgress("Importing " + Path.GetFileName(file) + "…", 0);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var res = await Task.Run(() => NB.Core.SourceEngine.VmfImporter.Import(ws, idx, file, o, null,
                new Progress<(string S, double P)>(p => BeginInvoke(() => SetProgress("Source map: " + p.S, p.P)))));
            Log($"Source map {Path.GetFileName(file)} imported into Showdown Town in {sw.Elapsed.TotalSeconds:F0}s (scene folder {res.SceneFolder}):");
            foreach (var l in NB.Core.SourceEngine.VmfImporter.Describe(res.Plan)) Log("  " + l);
            var rep = res.Report!;
            Log($"  world bundle {rep.BundleBytes / 1048576.0:F1} MB (original {res.Plan.BudgetBytes / 1048576.0:F1} MB); {rep.Models} models, {rep.Textures} textures, collision {rep.CollisionTriangles:N0} triangles, {rep.LightSetups} light setups");
            foreach (var e in rep.Errors) Log("  ERROR " + e);
            Log("  Play it: Build > Launch Workspace in Xenia, start a new game (Tools > Test Mode: Skip Intro starts it in Showdown Town). Undo: Build > Revert a Modified File (Bundle/4f/234cec).");
            ws.ForgetCache(o.World);
            var item = _worlds.Items.OfType<WorldItem>().FirstOrDefault(x => x.Entry.Bundle == o.World);
            if (item != null) await OpenWorld(item.Entry);
            if (rep.Errors.Count > 0) MessageBox.Show(this, $"{rep.Errors.Count} error(s) — see the log.", "Import Source Map", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception e) { Error("Source map import failed", e); }
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

    /// <param name="script">Automation (--patch-create): fills the shown dialog like a user and returns the target file.</param>
    async Task CreatePatch(Func<Panels.PatchInfoDialog, Task<string?>>? script = null)
    {
        if (_ws == null) return;
        if (_scene != null && _scene.Objects.Any(o => o.Dirty)) Log("Note: unsaved world edits are not in the patch (World > Save first).");
        if (_atmos.HasUnsaved) Log("Note: unsaved sky, light and fog changes are not in the patch (Atmosphere > Save to Workspace first).");
        var changed = _ws.ModifiedFiles(hash: false);
        var guess = ModCategories.Suggest(new PatchPackage.PatchManifest { Files = changed.Select(f => new PatchPackage.PatchFile { Path = f.Replace('\\', '/') }).ToList() });
        using var info = new Panels.PatchInfoDialog(Path.GetFileName(_ws.Root.TrimEnd('\\', '/')), Environment.UserName, guess, changed.Count, _ws.Manifest.ExeMods.Count);
        string target;
        if (script == null)
        {
            if (info.ShowDialog(this) != DialogResult.OK) return;
            using var d = new SaveFileDialog { Filter = "NB patch (*.nbpatch)|*.nbpatch", FileName = string.Concat(info.ModName.Where(c => !Path.GetInvalidFileNameChars().Contains(c))) + ".nbpatch", Title = "Save distributable patch" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            target = d.FileName;
        }
        else if (await script(info) is string t) target = t; else return;
        var ws = _ws;
        _busy = true; SetProgress("Building patch…", 0);
        try
        {
            var man = await Task.Run(() => NB.Core.Project.PatchPackage.Build(ws, target, info.ModName, info.Author, info.Description.Length > 0 ? info.Description : "Made with NB Studio",
                true, new Progress<(string F, double P)>(p => BeginInvoke(() => SetProgress("Delta " + p.F, p.P))), null,
                m => { m.Version = info.Version; m.Category = info.Category; m.Tags = info.Tags; m.Multiplayer = info.Multiplayer; if (info.Pictures.Count > 0) m.Extra[Panels.PatchPictures.ExtraKey] = Panels.PatchPictures.EntryList(info.Pictures); }));
            if (info.Pictures.Count > 0) Log($"  {Panels.PatchPictures.AddTo(target, info.Pictures)} picture(s) stored in the patch");
            foreach (var f in man.Files) Log($"  {f.Kind} {f.Path}: {f.CopiedBytes:N0} bytes from the original + {f.LiteralBytes:N0} new");
            Log($"Patch written: {target} ({new FileInfo(target).Length:N0} bytes, {man.Files.Count} file(s){(man.ExeMods.Count > 0 ? ", executable mods: " + string.Join(", ", man.ExeMods.Select(m => m.Id)) : "")}). It contains no original game data.");
        }
        catch (Exception e) { Error("Patch build failed", e); }
        finally { _busy = false; SetProgress(null, 0); }
    }

    /// <summary>Clean-copy candidates for a modded folder: this workspace's original, NB Multiplayer's game folder, the originals of known projects.</summary>
    IEnumerable<string?> CleanGameCandidates()
    {
        yield return _ws?.Original.Root;
        foreach (var settings in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NB-Multiplayer", "settings.json") })
        {
            string? dir = null;
            try { if (File.Exists(settings)) dir = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settings)).RootElement.TryGetProperty("GameDir", out var g) ? g.GetString() : null; }
            catch (Exception) { }
            yield return dir;
        }
        foreach (var p in ProjectRegistry.Load())
        {
            string? dir = null;
            try { var wj = Path.Combine(p.Path, "workspace.json"); if (File.Exists(wj)) dir = System.Text.Json.JsonDocument.Parse(File.ReadAllText(wj)).RootElement.GetProperty("OriginalPath").GetString(); }
            catch (Exception) { }
            yield return dir;
        }
    }

    async Task CreatePatchFromFolder()
    {
        using var dlg = new Panels.FolderPatchDialog(CleanGameCandidates());
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Report is not { } rep) return;
        var guess = ModCategories.Find(rep.Category) ?? ModCategories.Tweak;
        int exeWords = rep.Exe == null ? 0 : rep.Exe.Known.Count + (rep.Exe.Other.Count > 0 ? 1 : 0);
        using var info = new Panels.PatchInfoDialog(rep.SuggestedName, Environment.UserName, guess, rep.Carried.Count(), exeWords,
            $"The mod holds the differences between {rep.ModDir} and the clean game: {rep.Carried.Count()} file(s)" +
            (exeWords > 0 ? $" and {exeWords} executable mod(s)" : "") + ". It contains no original game data.",
            rep.SuggestedDescription, rep.Tags, rep.Multiplayer);
        if (info.ShowDialog(this) != DialogResult.OK) return;
        using var d = new SaveFileDialog { Filter = "NB patch (*.nbpatch)|*.nbpatch", FileName = string.Concat(info.ModName.Where(c => !Path.GetInvalidFileNameChars().Contains(c))) + ".nbpatch", Title = "Save the mod" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        var target = d.FileName;
        _busy = true; SetProgress("Building mod…", 0);
        try
        {
            var man = await Task.Run(() => PatchPackage.BuildFromFolders(rep, target, info.ModName, info.Author, info.Description,
                new Progress<(string F, double P)>(p => BeginInvoke(() => SetProgress("Delta " + p.F, p.P))),
                m => { m.Version = info.Version; m.Category = info.Category; m.Tags = info.Tags; m.Multiplayer = info.Multiplayer; if (info.Pictures.Count > 0) m.Extra[Panels.PatchPictures.ExtraKey] = Panels.PatchPictures.EntryList(info.Pictures); }));
            if (info.Pictures.Count > 0) Log($"  {Panels.PatchPictures.AddTo(target, info.Pictures)} picture(s) stored in the mod");
            foreach (var f in man.Files) Log($"  {f.Kind} {f.Path}: {f.CopiedBytes:N0} bytes from the original + {f.LiteralBytes:N0} new");
            Log($"Mod written: {target} ({new FileInfo(target).Length:N0} bytes, {man.Files.Count} file(s){(man.ExeMods.Count > 0 ? ", executable mods: " + string.Join(", ", man.ExeMods.Select(m => m.Id)) : "")}).");
            _busy = false; SetProgress(null, 0);
            if (MessageBox.Show(this, $"\"{man.Name}\" is ready.\n\nTry it now? NB Studio makes a test copy of the clean game (hard links: almost no disk space), applies the mod and starts it in Xenia.",
                    "Mod created", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                await TryMod(target, rep.ReferenceDir!);
        }
        catch (Exception e) { Error("Mod build failed", e); }
        finally { _busy = false; SetProgress(null, 0); }
    }

    /// <summary>"Try it": a hard-linked copy of the clean game with the mod applied, started in Xenia.</summary>
    async Task TryMod(string patch, string cleanDir)
    {
        var exe = _settings.XeniaPath;
        if (exe == null || !File.Exists(exe)) { PickXenia(); exe = _settings.XeniaPath; }
        if (exe == null || !File.Exists(exe)) return;
        var mod = ModStack.Load(patch);
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NBModTool", "try", PatchPackage.Slug(mod.Manifest.Name));
        _busy = true; SetProgress("Preparing a test copy…", 0);
        try
        {
            await Task.Run(() =>
            {
                if (Directory.Exists(dir))
                {   // links share the original's read-only attribute: delete names without touching attributes
                    foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) NB.Core.IO.FileLinks.DeleteIgnoringReadOnly(f);
                    Directory.Delete(dir, true);
                }
                var copies = new HashSet<string>(ModStack.ChangedFiles(new[] { mod }).Select(f => f.Replace('/', Path.DirectorySeparatorChar)), StringComparer.OrdinalIgnoreCase);
                NB.Core.IO.FileLinks.LinkCopy(cleanDir, dir, copies, new Progress<(string Text, double Fraction)>(p => BeginInvoke(() => SetProgress(p.Text, 0.4 * p.Fraction))));
                ModStack.Apply(new[] { mod }, dir, new Progress<(string Text, double Fraction)>(p => BeginInvoke(() => SetProgress(p.Text, 0.4 + 0.6 * p.Fraction))), s => BeginInvoke(() => Log(s)));
            });
            Process.Start(new ProcessStartInfo(exe, $"\"{Path.Combine(dir, "default.xex")}\"") { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false });
            Log($"Started {mod.Manifest.Name} in {Path.GetFileName(exe)} from the test copy {dir}.");
        }
        catch (Exception e) { Error("Could not start the test copy", e); }
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
        catch (InvalidOperationException e) when (e.Message.StartsWith("Nothing to roll back")) { MessageBox.Show(this, e.Message, "Roll Back Patches", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        catch (Exception e) { Error("Rollback failed", e); }
    }

    /// <summary>The collision of all objects (View menu, the Collision button of the 3D view): decoded in the background
    /// by the view.</summary>
    Task ToggleCollision(bool on)
    {
        _view.ShowCollision = on;
        _view.Refresh3D();
        return Task.CompletedTask;
    }

    /// <summary>Duplicate / paste / delete write the world bundle and reload the world (one undo step each). Unsaved
    /// transform edits of other objects are kept: the reload carries them over, like an undo does.</summary>
    bool CanAddOrRemove(SceneObject? o, string what)
    {
        if (_ws == null || _scene == null || _sceneEntry == null) return false;
        if (_busy || _undoing) { Log($"{what}: NB Studio is busy (loading or saving), try again in a moment."); return false; }
        if (o != null && (o.Kind != SceneObjectKind.Scenery || o.Instance == null))
        {
            Log($"{what}: {o.Name} is {(o.Kind == SceneObjectKind.Terrain ? "the terrain" : "a marker (actor, pickup, path node…)")}; only scenery objects can be {(what == "Delete" ? "deleted" : "copied")} so far.");
            return false;
        }
        return true;
    }

    async Task DuplicateObject(SceneObject o, Vector3 offset, bool confirm = true)
    {
        var world = o.Transform; world.Translation += offset;
        await AddCopy(o.Instance?.Index ?? -1, o, world, "duplicated");
    }

    /// <summary>Adds a copy of scenery instance <paramref name="src"/> with the world matrix <paramref name="world"/>, saves
    /// the world bundle (an undo step) and reloads the world; the copy is selected. Returns it.</summary>
    async Task<SceneObject?> AddCopy(int src, SceneObject? srcObj, Matrix4x4 world, string verb, string? name = null)
    {
        name ??= srcObj?.Name ?? "instance " + src;
        if (!CanAddOrRemove(srcObj, "Paste") || src < 0) return null;
        try
        {
            int ni = NB.Core.World.InstanceEditor.Duplicate(_scene!.Caff, _scene.Background.View.Symbol, src, world);
            _ws!.SaveResident(_scene.Bundle, _scene.Caff, $"{verb} {name} as instance {ni} at {Fmt(world.Translation)}");
            Log($"{Cap(verb)} {name} → instance {ni} at {Fmt(world.Translation)} (Ctrl+Z removes it). Reloading world…");
            await OpenWorld(_sceneEntry!, _sceneAct);
            var copy = _scene!.Objects.FirstOrDefault(x => x.Instance?.Index == ni);
            if (copy != null) _view.Select(copy);
            return copy;
        }
        catch (Exception e) { Error(Cap(verb) + " failed", e); return null; }
    }

    static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    static string Fmt(Vector3 v) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"({v.X:0.##}, {v.Y:0.##}, {v.Z:0.##})");

    async Task DeleteObject(SceneObject o, bool confirm = true, string verb = "Deleted")
    {
        if (!CanAddOrRemove(o, "Delete")) return;
        if (confirm && MessageBox.Show(this, $"Delete {o.Name} from the world?\n\nIt is hidden (zero scale, moved far below the level) rather than removed from the tables, because the game refers to scenery by index. Ctrl+Z brings it back.", Text, MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        try
        {
            NB.Core.World.InstanceEditor.Hide(_scene!.Caff, _scene.Background.View.Symbol, o.Instance!.Index);
            o.Transform = o.OriginalTransform;   // an unsaved move of the deleted object is not carried over the reload
            _ws!.SaveResident(_scene.Bundle, _scene.Caff, $"{verb.ToLowerInvariant()} {o.Name} (hidden)");
            _view.Select(null);
            Log($"{verb} {o.Name} (instance {o.Instance.Index} hidden; Ctrl+Z brings it back). Reloading world…");
            await OpenWorld(_sceneEntry!, _sceneAct);
        }
        catch (Exception e) { Error("Delete failed", e); }
    }

    // ------------------------------------------------------------------ copy / cut / paste

    /// <summary>A copied object: the scenery instance it was copied from (in its world bundle), its transform and box then.</summary>
    sealed record ClipItem(uint Bundle, int Instance, string Name, string ModelName, Matrix4x4 Transform, Vector3 BoundsMin, Vector3 BoundsMax);
    readonly List<ClipItem> _clip = new();

    bool CopySelection()
    {
        var o = _view.Selected;
        if (o == null || _scene == null) { Log("Copy: select an object first (click it in the 3D view)."); return false; }
        if (!CanAddOrRemove(o, "Copy")) return false;
        _clip.Clear();
        _clip.Add(new ClipItem(_scene.Bundle, o.Instance!.Index, o.Name, o.ModelName, o.Transform, o.BoundsMin, o.BoundsMax));
        Log($"Copied {o.Name}. Ctrl+V pastes a copy where the mouse points in the 3D view (again for more copies).");
        return true;
    }

    async Task CutSelection()
    {
        var o = _view.Selected;
        if (o == null) { Log("Cut: select an object first."); return; }
        if (CopySelection()) await DeleteObject(o, confirm: false, verb: "Cut");
    }

    async Task DeleteSelection()
    {
        var o = _view.Selected;
        if (o == null) { Log("Delete: select an object first."); return; }
        await DeleteObject(o, confirm: false);
    }

    /// <summary>Ctrl+V: a copy of the copied object on the surface under the mouse (or <paramref name="at"/>, a 3D-view
    /// pixel; with the mouse outside the view: under the view's centre), rotation and scale kept; in front of the camera
    /// when the ray hits nothing.</summary>
    async Task<SceneObject?> PasteClipboard(Point? at = null)
    {
        if (_clip.Count == 0) { Log("Paste: nothing copied yet (select an object and press Ctrl+C)."); return null; }
        if (_scene == null || _sceneEntry == null) return null;
        var c = _clip[0];
        if (c.Bundle != _scene.Bundle) { Log($"Paste: {c.Name} was copied in another world ({c.Bundle:x6}); it can only be pasted into that world."); return null; }
        if (!CanAddOrRemove(null, "Paste")) return null;
        float size = (c.BoundsMax - c.BoundsMin).Length();
        var (p, hit, mouse) = _view.SurfaceAt(at, Math.Clamp(size * 2, 10, 200));
        var world = SceneViewport.PlaceOn(c.Transform, c.BoundsMin, c.BoundsMax, p);
        Log($"Paste {c.Name} " + (hit != null ? $"on {hit.Name}{(mouse || at != null ? " under the mouse" : " at the centre of the view")}" : "in front of the camera (nothing under the mouse)"));
        return await AddCopy(c.Instance, null, world, "pasted", c.Name);
    }

    // ------------------------------------------------------------------ edit keys

    [System.Runtime.InteropServices.DllImport("user32")] static extern IntPtr GetFocus();

    /// <summary>The control with the keyboard focus (also the edit box inside a NumericUpDown, a property grid or a
    /// data grid cell), or null.</summary>
    static Control? FocusedControl() { var h = GetFocus(); return h == IntPtr.Zero ? null : Control.FromHandle(h) ?? Control.FromChildHandle(h); }

    /// <summary>Text is being edited there: an editable text box, or the text part of an editable combo box.</summary>
    static bool IsTextEntry(Control? c) => c is TextBoxBase { ReadOnly: false, Enabled: true } || c is ComboBox { DropDownStyle: not ComboBoxStyle.DropDownList };

    /// <summary>Copy / cut / paste / Del act on 3D objects only while the 3D view is shown and the focus is in the view,
    /// the Scene or Worlds list, the Properties panel or the toolbar (not in the Text, Audio, Tag Editor … pages).</summary>
    bool SceneKeysActive(Control? f)
    {
        if (_scene == null || _start.Visible || _center.SelectedIndex != 0) return false;
        if (f == null || f == this) return true;
        for (var c = f; c != null; c = c.Parent)
            if (c == _view || c == _tree || c == _worlds || c == _transform || c == _toolbar || c == _menu) return true;
        return false;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if ((msg.Msg == 0x100 || msg.Msg == 0x104) && HandleEditKey(keyData)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>
    /// Ctrl+Z / Ctrl+Y (also Ctrl+Shift+Z) undo and redo the editor's history wherever the focus is, except while text is
    /// being typed in a box (then the box undoes its own typing). Ctrl+C / Ctrl+X / Ctrl+V / Del copy, cut, paste and delete
    /// the selected object; in a text box they keep their normal meaning.
    /// </summary>
    bool HandleEditKey(Keys k)
    {
        var f = FocusedControl();
        bool typing = IsTextEntry(f);
        var tb = f as TextBoxBase;
        switch (k)
        {
            case Keys.Control | Keys.Z:
                // the box's own undo only for typing not yet committed (a box that was just clicked, or whose value was
                // applied, gives Ctrl+Z to the scene: that was the "undo does nothing" after typing a position in 1.6–1.7)
                if (typing && tb != null && tb.Modified && tb.CanUndo) return false;
                _ = Undo(); return true;
            case Keys.Control | Keys.Y:
            case Keys.Control | Keys.Shift | Keys.Z:
                if (typing && tb != null && tb.Modified) return false;
                _ = Redo(); return true;
            case Keys.Control | Keys.C:
                if (typing || (tb != null && tb.SelectionLength > 0) || !SceneKeysActive(f)) return false;   // e.g. copying log text
                CopySelection(); return true;
            case Keys.Control | Keys.X:
                if (typing || !SceneKeysActive(f)) return false;
                _ = CutSelection(); return true;
            case Keys.Control | Keys.V:
                if (typing || !SceneKeysActive(f)) return false;
                _ = PasteClipboard(); return true;
            case Keys.Delete:
                if (typing || !SceneKeysActive(f)) return false;
                if (_view.Transforming) return false;
                _ = DeleteSelection(); return true;
        }
        return false;
    }

    void UpdateUndoUi()
    {
        if (InvokeRequired) { BeginInvoke(UpdateUndoUi); return; }
        if (_undoBtn == null) return;
        _undoBtn.Enabled = _history.CanUndo; _redoBtn.Enabled = _history.CanRedo;
        _undoBtn.ToolTipText = _history.UndoLabel is { } u ? $"Undo {u} (Ctrl+Z)" : "Nothing to undo (Ctrl+Z)";
        _redoBtn.ToolTipText = _history.RedoLabel is { } r ? $"Redo {r} (Ctrl+Y)" : "Nothing to redo (Ctrl+Y)";
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

    // ------------------------------------------------------------------ atmosphere

    /// <summary>World > Atmosphere / Weather: the Atmosphere tab (sky, light, fog of each time of day; falling snow).</summary>
    void ShowAtmosphere(string? entry)
    {
        SelectCenter("Atmosphere");
        if (_scene == null) { Log("Atmosphere: open a world first (Worlds tab, double-click Showdown Town)."); return; }
        if (entry != null) try { _atmos.ScriptSelect(entry); } catch (Exception) { }
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

        public IReadOnlyCollection<string> WorldTextureStems =>
            Scene.Caff.Symbols.Select(AssetIds.DisplayName).Where(n => n.StartsWith("aid_texture_")).Select(TextureReplacer.Stem).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        public async Task<string> ReplaceManyAsync(List<(string Stem, string File)> items)
        {
            var f = _f; var sc = Scene; var ws = f._ws!;
            if (sc.Objects.Any(x => x.Dirty)) throw new InvalidOperationException("Save or undo the transform edits in this world first (World > Save).");
            var res = await Task.Run(() =>
            {
                var imgs = items.Select(i => { var (rgba, w, h) = ImageIO.Load(i.File); return (i.Stem, rgba, w, h); }).ToList();
                return TextureReplacer.ReplaceMany(ws, sc.Bundle, imgs);
            });
            foreach (var n in res.Notes.Where(n => !n.Contains("re-encoded"))) f.Log("  " + n);
            string summary = $"Replaced {items.Count} texture(s) of world {sc.Bundle:x6}: {res.ResidentAssets} resident and {res.StreamedAssets} streamed asset(s) re-encoded.";
            f.Log("texture library: " + summary + " Reloading world…");
            await f.OpenWorld(f._sceneEntry!, f._sceneAct);
            return summary;
        }

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
        // the same world again (after a duplicate, a delete, an import, an undo ...): keep the camera, the selection, the
        // undo history and any unsaved transform edits
        bool reload = _scene != null && _sceneEntry == w && _sceneAct == act;
        if (!reload && _scene != null && _scene.Objects.Any(o => o.Dirty) &&
            MessageBox.Show(this, "Discard unsaved edits in the current world?", Text, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        var carry = reload ? _scene!.Objects.Where(o => o.Dirty).Select(o => (Key: UndoHistory.KeyOf(o), o.Transform, Link: o.Marker?.Link)).ToList() : null;
        string? selKey = reload && _view.Selected != null ? UndoHistory.KeyOf(_view.Selected) : null;
        _busy = true; SetProgress($"Loading {w.Display}…", 0);
        try
        {
            var ws = _ws;
            var index = _index;
            var scene = await Task.Run(() => new WorldScene(ws, w.Bundle, w.BackgroundModel, new Progress<(string S, double P)>(p => BeginInvoke(() => SetProgress(p.S, p.P))),
                act != null ? new[] { act.ActBundle } : null, index));
            // textures: resolve every diffuse texture up front across the whole workspace (world bundle, shared/common
            // bundles, stream archives) and report what could not be found
            scene.Textures = new NB.Core.Textures.TextureResolver(ws, index, scene.Caff, w.Bundle);
            var texNames = scene.DiffuseTextureNames().ToList();
            await Task.Run(() =>
            {
                for (int i = 0; i < texNames.Count; i++)
                {
                    scene.Textures.Load(texNames[i]);
                    if (i % 8 == 0) { int k = i; BeginInvoke(() => SetProgress($"Loading textures {k}/{texNames.Count}…", k / (double)Math.Max(1, texNames.Count))); }
                }
            });
            _scene = scene; _sceneEntry = w; _sceneAct = act;
            if (reload)
            {
                var byKey = new Dictionary<string, SceneObject>();
                foreach (var o in scene.Objects) byKey.TryAdd(UndoHistory.KeyOf(o), o);
                foreach (var c in carry!)
                    if (byKey.TryGetValue(c.Key, out var o)) { o.Transform = c.Transform; if (c.Link is { } l && o.Marker != null) o.Marker.Link = l; }
                _history.Rebind(scene);
            }
            else _history.DropSceneSteps();
            if (_ws != null) { _settings.LastWorlds[_ws.Root] = WorldKey(w, act); _settings.Save(); }
            try { _atmos.SetWorld(w.Bundle, w.Display); } catch (Exception e) { Log("Atmosphere: " + e.Message); }
            _view.SetScene(scene, keepCamera: reload);
            FillTree();
            if (selKey != null && scene.Objects.FirstOrDefault(o => UndoHistory.KeyOf(o) == selKey) is { } sel) _view.Select(sel);
            Log($"Opened {(act?.Display ?? w.Display)} (world bundle {w.Bundle:x6}{(act != null ? $", act bundle {act.ActBundle:x6} markers" : "")}): {scene.Objects.Count} objects, {scene.Models.Count} reference models.");
            {
                var src = scene.Textures.Sources.Values.Select(v => v.StartsWith("world") ? "world bundle" : v.StartsWith("resident") ? "other resident bundles" : v.StartsWith("streamed") ? "stream archives" : "missing").GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
                Log($"  Textures: {texNames.Count} used — " + string.Join(", ", src.Select(kv => $"{kv.Value} from {kv.Key}")));
                var missing = scene.Textures.Missing.ToList();
                if (missing.Count > 0) Log($"  Missing textures ({missing.Count}, drawn untextured): " + string.Join(", ", missing.Take(12)) + (missing.Count > 12 ? " …" : ""));
            }
            Log("  Contents: " + scene.Audit.Summary());
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
            // player start points first (Banjo's start in Showdown Town, the Act start), so they are easy to find
            var starts = _scene.Objects.Where(o => SpawnPoints.Is(o) && (q == "" || NodeText(o).ToLowerInvariant().Contains(q))).ToList();
            if (starts.Count > 0)
            {
                var sn = _tree.Nodes.Add($"Player start points ({starts.Count})"); sn.Checked = true;
                sn.ToolTipText = "Marker type 4: where the game puts Banjo (new game in Showdown Town, the start of an Act). Green flag and arrow in the 3D view.";
                foreach (var o in starts) sn.Nodes.Add(new TreeNode(NodeText(o)) { Tag = o, Checked = o.Visible, ToolTipText = SpawnPoints.Detail(o), ForeColor = Color.FromArgb(20, 130, 50) });
                sn.Expand();
            }
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
                    foreach (var o in tg) tn.Nodes.Add(new TreeNode(NodeText(o)) { Tag = o, Checked = o.Visible, ToolTipText = SpawnPoints.Detail(o) ?? "" });
                }
            }
            terrain.Expand(); scenery.Expand();
        }
        _tree.EndUpdate();
    }

    TabPage _dialogueTab = null!;

    void OnSelection(SceneObject? o)
    {
        _transform.SetObject(o);
        // a character (a marker placing an actor): its dialogue lines, and the Dialogue tab when the user wants it shown
        if (o?.Marker != _dialogueFor?.Marker || o == null)
        {
            _dialogueFor = o;
            _dialogue.Show(o?.Kind == SceneObjectKind.Marker ? _scene : null, o?.Kind == SceneObjectKind.Marker ? o : null, _index);
            _dialogueTab.Text = _dialogue.LineCount > 0 ? $"Dialogue ({_dialogue.LineCount})" : "Dialogue";
            if (_dialogue.AutoShow && _dialogue.LineCount > 0 && _right.SelectedIndex == 0) _right.SelectedTab = _dialogueTab;   // from Properties only (not Tag Editor / Live)
        }
        _tags.ShowObject(_scene, o);
        _status.Text = o == null ? "" : $"{(SpawnPoints.Label(o) is { } sl ? sl + ": " : "")}{o.Name} — {AssetIds.DisplayName(o.ModelName)}{(o.ModelSource == "" || o.Model == null ? "" : o.Model.View == null ? $" ({o.ModelSource})" : $" (model {AssetIds.DisplayName(o.Model.View.Name).Replace("aid_model_banjox_", "")} from {o.ModelSource})")}  pos ({o.Transform.M41:F2}, {o.Transform.M42:F2}, {o.Transform.M43:F2})";
        if (o != null)
        {
            _syncingTree = true;
            var node = FindNode(_tree.Nodes, o);
            if (node != null) { _tree.SelectedNode = node; node.EnsureVisible(); }
            _syncingTree = false;
        }
        UpdateTitle();
    }

    SceneObject? _dialogueFor;

    static TreeNode? FindNode(TreeNodeCollection nodes, SceneObject o)
    {
        foreach (TreeNode n in nodes) { if (n.Tag == o) return n; var c = FindNode(n.Nodes, o); if (c != null) return c; }
        return null;
    }

    // ------------------------------------------------------------------ editing

    void PushUndo(SceneObject o, Matrix4x4 before, Matrix4x4 after)
    {
        if (before == after) return;
        _history.PushTransform(o, before, after);
        UpdateTitle();
        RefreshNode(o);
    }

    bool _undoing;

    async Task Undo() => await UndoRedo(undo: true);
    async Task Redo() => await UndoRedo(undo: false);

    async Task UndoRedo(bool undo)
    {
        if (_view.Transforming) { _view.CancelTransform(); Log("Transform cancelled."); return; }   // a G / R / S in progress is cancelled, not undone
        if (_busy || _undoing) { Log((undo ? "Undo" : "Redo") + ": NB Studio is busy (loading or saving), try again in a moment."); return; }
        _undoing = true;
        try
        {
            UndoStep? s;
            try { s = undo ? _history.Undo() : _history.Redo(); }
            catch (Exception e) { Error(undo ? "Undo failed" : "Redo failed", e); return; }
            if (s == null) { Log(undo ? "Nothing to undo." : "Nothing to redo."); return; }
            Log((undo ? "Undo: " : "Redo: ") + s.Label);
            switch (s)
            {
                case TransformStep t when t.Obj != null:
                    _view.Select(t.Obj); RefreshNode(t.Obj); break;
                case LinkStep l when l.Obj != null:
                    _view.Select(l.Obj); RefreshNode(l.Obj); break;
                case FileStep:
                    // game files went back: reload the world that shows them
                    if (_sceneEntry != null) await OpenWorld(_sceneEntry, _sceneAct);
                    break;
            }
            _view.Refresh3D(); UpdateTitle();
        }
        finally { _undoing = false; }
    }

    /// <summary>A label for a menu item: "&" shown as itself, long names shortened.</summary>
    static string MenuText(string s) => (s.Length > 60 ? s[..57] + "..." : s).Replace("&", "&&");

    void RefreshNode(SceneObject o)
    {
        // an object can be listed twice (a start point is also under its marker asset)
        void Walk(TreeNodeCollection ns) { foreach (TreeNode n in ns) { if (n.Tag == o) n.Text = NodeText(o); Walk(n.Nodes); } }
        Walk(_tree.Nodes);
    }

    /// <summary>The tree's text for an object: player start points carry their label ("Player start (Banjo) — …").</summary>
    static string NodeText(SceneObject o) => (SpawnPoints.Label(o) is { } l ? l + " — " : "") + o.Name + (o.Dirty ? " *" : "");

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
            int n;
            using (_history.Suppress()) n = _scene.Save();
            Log(n == 0 ? "No world changes to save." : $"Saved {n} changed object(s) → {Path.GetRelativePath(_ws.Root, _ws.Game.ResidentPath(_scene.Bundle))} (uncompressed CAFF, checksum recomputed).");
            FillTree(); UpdateTitle();
        }
        catch (Exception e) { Error("Saving failed", e); }
    }

    void UpdateTitle()
    {
        int dirty = _scene?.Objects.Count(o => o.Dirty) ?? 0;
        Text = "Nuts & Bolts Mod Tool" + (_ws != null ? $" — {Path.GetFileName(_ws.Root)}" : "") + (_scene != null ? $" — {WorldCatalog.DisplayNames.GetValueOrDefault(_scene.Background.View.Name.Replace("aid_model_banjox_background_", "").Replace("_default", ""), "")} [{_scene.Bundle:x6}]" : "") + (dirty > 0 || _tags.HasUnsaved || _atmos.HasUnsaved ? " *" : "");
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
            var inGame = new List<string>();
            foreach (var m in enabled.ToList())
            {
                var problems = NB.Core.Mods.ExePatches.Check(image, xex.ImageBase, m);
                if (problems.Count == 0) continue;
                enabled.Remove(m);
                // the words already hold this mod's values: it is built into this default.xex (a map mod applied to the workspace)
                if (NB.Core.Mods.ExePatches.IsApplied(image, xex.ImageBase, m)) inGame.Add(m.Name);
                else Log($"Mod '{m.Name}' does not match this default.xex: {string.Join("; ", problems)}");
            }
            if (inGame.Count > 0) Log($"Already in this game's default.xex (built in by an applied mod, nothing to add): {string.Join(", ", inGame)}.");
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

    // ------------------------------------------------------------------ F5: test the open world in Xenia

    Process? _qtProcess;
    CancellationTokenSource? _qtCts;
    Task<string>? _qtBoot;
    int _qtPort;
    byte[]? _qtProbe; string? _qtProbeXex;

    /// <summary>The script the test game starts in: the open Act, Showdown Town, or the first Act that loads the open world.</summary>
    QuickTest.Target QuickTarget()
    {
        var w = _sceneEntry; var act = _sceneAct;
        if (w == null) return new(QuickTest.TownScript, "Showdown Town", false, "no world is open: starting in Showdown Town");
        if (act != null) return new(act.Script, act.Display, true);
        if (w.World == "showdowntown") return new(QuickTest.TownScript, "Showdown Town", false);
        var a = _acts.Where(x => x.WorldBundle == w.Bundle).OrderBy(x => x.Act, StringComparer.Ordinal).FirstOrDefault();
        if (a != null) return new(a.Script, a.Display, true, $"the game enters {w.Display} through its Acts: starting {a.Display}");
        var b = _acts.Where(x => x.World == w.World).OrderBy(x => x.Act, StringComparer.Ordinal).FirstOrDefault();
        if (b != null) return new(b.Script, b.Display, true, $"NOTE: this copy of {w.Display} [{w.Bundle:x6}] is not loaded by any Act; {b.Display} loads [{b.WorldBundle:x6}], so changes made in this copy will not show");
        return new(QuickTest.TownScript, "Showdown Town", false, $"{w.Display} can't be started on its own: starting in Showdown Town");
    }

    /// <summary>
    /// F5 / Shift+F5: plays the open world in Xenia without the title screen, the menus and the intro (see
    /// <see cref="QuickTest"/>); the workspace and the user's saves are not changed. Shift+F5 then moves Banjo to the
    /// ground under the 3D view's camera.
    /// </summary>
    async Task QuickTestXenia(bool fromCamera)
    {
        if (_ws == null) { Log("Test in Xenia: open a workspace first."); return; }
        if (_busy) { Log("Test in Xenia: NB Studio is busy, try again in a moment."); return; }
        var t = QuickTarget();
        if (_scene != null && _scene.Objects.Any(o => o.Dirty))
        {
            var ans = _scripted ? DialogResult.Yes : MessageBox.Show(this, "The open world has unsaved changes. Save them first, so the test shows them?\n\nYes: save and test   No: test the last saved state", "Test in Xenia", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (ans == DialogResult.Cancel) return;
            if (ans == DialogResult.Yes) SaveWorld();
        }
        if (_atmos.HasUnsaved) Log("Note: unsaved Atmosphere changes are not in the test (Atmosphere > Save to Workspace first).");
        if (_qtProcess is { HasExited: false } old)
        {
            if (!_scripted && MessageBox.Show(this, "The test game NB Studio started is still running. Close it and start the new test?", "Test in Xenia", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            _qtCts?.Cancel();
            await CloseTestGame(old);
        }
        _qtBoot = null;
        var fork = QuickTest.FindForkXenia(_settings.XeniaPath);
        var exe = fork ?? _settings.XeniaPath;
        if (exe == null || !File.Exists(exe)) { PickXenia(); exe = _settings.XeniaPath; }
        if (exe == null || !File.Exists(exe)) return;
        bool isFork = QuickTest.IsFork(exe);
        Vector3? spawn = null;
        if (fromCamera)
        {
            if (!isFork) Log("Test from camera needs NB's Xenia (NB Multiplayer's xenia_canary_netplay.exe); starting at the world's spawn.");
            else if (_scene == null) Log("Test from camera: open a world first.");
            else spawn = _view.GroundBelow(_view.CameraPosition) ?? _view.CameraPosition;
        }
        var ws = _ws;
        Log($"Test in Xenia: {t.Display}{(t.Note.Length > 0 ? " (" + t.Note + ")" : "")}{(spawn is { } sp ? $", Banjo at the 3D view's camera spot {Fmt(sp)}" : "")}…");
        string xex;
        _busy = true; SetProgress("Preparing the test copy…", 0.05);
        var sw = Stopwatch.StartNew();
        try { xex = await Task.Run(() => QuickTest.Prepare(ws, t, s => Log(s), (s, p) => SetProgress(s, p))); }
        catch (Exception e) { Error("Preparing the test failed", e); return; }
        finally { _busy = false; SetProgress(null, 0); }
        Log($"  test copy ready in {sw.Elapsed.TotalSeconds:F1} s: {Path.GetDirectoryName(xex)}");
        string dir = QuickTest.Folder(ws);
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false };
        int port = 0;
        try
        {
            if (isFork)
            {
                // own storage (config, profile, an empty save: SINGLE PLAYER starts a new game; NB Multiplayer's and the user's saves are not touched)
                string storage = Path.Combine(dir, "xenia");
                Directory.CreateDirectory(storage);
                // the player's own Xenia settings (controls, keyboard, graphics) from NB Multiplayer, if it is installed there
                if (QuickTest.UserConfig(exe) is { } cfg)
                {
                    File.Copy(cfg, Path.Combine(storage, Path.GetFileName(cfg)), true);
                    Log($"  Xenia settings (controls, graphics) from {cfg}");
                }
                var content = Path.Combine(storage, "content");
                QuickTest.ClearSaves(content, keepBlueprints: !_settings.QuickTestFreshSave);
                var bps = QuickTest.TestBlueprints(content);
                Log(_settings.QuickTestFreshSave ? "  fresh test save (File > Settings)" : bps.Count > 0 ? $"  vehicles saved in earlier tests: {string.Join(", ", bps)} (Build > Reset Test Save forgets them)" : "  no vehicles saved in earlier tests yet");
                WriteExeModsFor(storage);
                port = _qtPort = int.TryParse(Environment.GetEnvironmentVariable("NB_STUDIO_PAD_PORT"), out var fixedPort) ? fixedPort : QuickTest.FreeUdpPort();   // env: test harnesses
                foreach (var a in new[] { $"--storage_root={storage}", $"--content_root={Path.Combine(storage, "content")}", $"--log_file={Path.Combine(storage, "xenia.log")}", "--network_mode=0",
                                          $"--nb_remote_input_port={port}", "--nb_create_profile=NBStudio", "--readback_resolve=fast" }) psi.ArgumentList.Add(a);
            }
            else
            {
                // any other Xenia: its own settings, but a separate content folder (an empty save, the user's saves untouched)
                string content = Path.Combine(dir, "content");
                Directory.CreateDirectory(content);
                QuickTest.ClearSaves(content, keepBlueprints: !_settings.QuickTestFreshSave);
                ApplyExeMods(askForXenia: true);
                psi.ArgumentList.Add($"--content_root={content}");
            }
            foreach (var a in (Environment.GetEnvironmentVariable("NB_STUDIO_XENIA_EXTRA") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add(xex);
            _qtProcess = Process.Start(psi); _live.PreferPid = _qtProcess?.Id;
        }
        catch (Exception e) { Error("Could not start Xenia", e); return; }
        if (_qtProcess == null) return;
        Log($"  started {Path.GetFileName(exe)} (pid {_qtProcess.Id}).");
        if (!isFork)
        {
            Log("  This Xenia has no virtual gamepad: press A on the title screen and on SINGLE PLAYER; the new game starts right in " + t.Display + ". " +
                "The test uses its own save folder (" + Path.Combine(dir, "content") + "), so the first time Xenia asks you to create a profile there. " +
                "NB Multiplayer's Xenia (installed with NB Multiplayer) does all of this by itself.");
            return;
        }
        Log("  NB Studio presses through the title screen and menus for you (virtual gamepad); hands off the controller for a few seconds.");
        try
        {
            if (_qtProbeXex != ws.Game.Xex || _qtProbe == null)
            {
                var img = XexFile.Read(File.ReadAllBytes(ws.Game.Xex)).GetImage();
                _qtProbe = img.AsSpan((int)(NB.Core.Live.XeniaLive.TextStart - 0x82000000), 64).ToArray(); _qtProbeXex = ws.Game.Xex;
            }
        }
        catch (Exception e) { Log("  could not read default.xex for the automatic start: " + e.Message); return; }
        _qtCts = new CancellationTokenSource();
        var proc = _qtProcess; var probe = _qtProbe; var ct = _qtCts.Token;
        _qtBoot = Task.Run(() => QuickTest.AutoBoot(proc, port, probe, t, spawn, s => Log("  " + s), ct));
        var res = await _qtBoot;
        Log($"Test in Xenia: {t.Display}: {res}");
    }

    /// <summary>Closes the test game Studio started like its window's close button (the game finishes writing a save it is
    /// in the middle of); only if it does not exit within 10 s is it ended.</summary>
    static async Task CloseTestGame(Process p)
    {
        try
        {
            if (p.HasExited) return;
            p.CloseMainWindow();
            for (int i = 0; i < 100 && !p.HasExited; i++) await Task.Delay(100);
            if (!p.HasExited) { p.Kill(); p.WaitForExit(8000); }
        }
        catch (Exception) { }
    }

    /// <summary>Build > Reset Test Save: empties this workspace's test save (game save and vehicles saved during tests).</summary>
    async Task ResetTestSave()
    {
        if (_ws == null) { Log("Reset Test Save: open a workspace first."); return; }
        var content = Path.Combine(QuickTest.Folder(_ws), "xenia", "content");
        var bps = QuickTest.TestBlueprints(content);
        if (!_scripted && MessageBox.Show(this, "Forget everything Test in Xenia saved for this workspace?" + (bps.Count > 0 ? "\n\nVehicles: " + string.Join(", ", bps) : "") +
                "\n\nYour NB Multiplayer and Xenia saves are not touched.", "Reset Test Save", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        if (_qtProcess is { HasExited: false } p) await CloseTestGame(p);
        try { QuickTest.ResetTestSave(_ws); Log($"Test save reset ({bps.Count} vehicle(s) forgotten); the next test starts with an empty save."); }
        catch (Exception e) { Error("Reset Test Save failed", e); }
    }

    /// <summary>The workspace's executable mods as a Xenia patch file in <paramref name="storage"/>\patches (NB's Xenia reads
    /// patches from its storage folder).</summary>
    void WriteExeModsFor(string storage)
    {
        if (_ws == null) return;
        try
        {
            var xb = File.ReadAllBytes(_ws.Game.Xex); var xex = XexFile.Read(xb); var img = xex.GetImage();
            var enabled = NB.Core.Mods.ExePatches.ResolveAll(_ws.Manifest.ExeMods).Where(m => NB.Core.Mods.ExePatches.Check(img, xex.ImageBase, m).Count == 0).ToList();
            var hash = NB.Core.Mods.ExePatches.ResolveXeniaHash(NB.Core.Mods.ExePatches.XeniaModuleHash(xb, img), storage);
            if (hash == null) { if (enabled.Count > 0) Log("  executable mods: unknown default.xex (no module hash yet); they apply from the second test on."); return; }
            NB.Core.Mods.ExePatches.WriteXeniaPatchFile(storage, hash.Value, enabled);
            if (enabled.Count > 0) Log($"  executable mods in the test: {string.Join(", ", enabled.Select(m => m.Name))}");
        }
        catch (Exception e) { Log("  executable mods not applied: " + e.Message); }
    }

    (string Key, HashSet<string> Ids)? _builtIn;

    /// <summary>Ids of the executable mods already built into the workspace's default.xex (all their words hold the
    /// patched values), cached per file size and time.</summary>
    HashSet<string> BuiltInExeMods()
    {
        if (_ws == null || !File.Exists(_ws.Game.Xex)) return new();
        var fi = new FileInfo(_ws.Game.Xex);
        string key = $"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        if (_builtIn is { } b && b.Key == key) return b.Ids;
        var ids = new HashSet<string>();
        try
        {
            var xex = XexFile.Read(File.ReadAllBytes(_ws.Game.Xex)); var img = xex.GetImage();
            foreach (var m in NB.Core.Mods.ExePatches.All) if (NB.Core.Mods.ExePatches.IsApplied(img, xex.ImageBase, m)) ids.Add(m.Id);
        }
        catch (Exception) { }
        _builtIn = (key, ids);
        return ids;
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
    readonly List<string> _scriptPictures = new();

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
                        var o = _scene!.Objects.FirstOrDefault(x => x.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                             ?? _scene.Objects.First(x => x.ModelName.Contains(q, StringComparison.OrdinalIgnoreCase));   // or by model name
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
                        var o = _view.Selected!;
                        if (SceneViewport.ScaleLocked(o)) { L($"script: {o.Name} is a marker: scale is locked"); break; }
                        var before = o.Transform; var t = before.Translation; var m = before; m.Translation = Vector3.Zero; m = Matrix4x4.CreateScale(f) * m; m.Translation = t; o.Transform = m;
                        PushUndo(o, before, o.Transform); L($"script: scaled {o.Name} x{f}"); break;
                    }
                    case "--undo": await Undo(); L("script: undo"); break;
                    case "--dialogue": L($"script: dialogue tab {(_right.SelectedTab == _dialogueTab ? "shown" : "hidden")}: " + _dialogue.ScriptState()); break;
                    case "--dialogue-lang": _dialogue.ScriptLanguage(Next()); L("script: dialogue " + _dialogue.ScriptState()); break;
                    case "--dialogue-edit": { var n = Next(); var t = Next(); L("script: dialogue " + _dialogue.ScriptEdit(n, t)); break; }
                    case "--dialogue-save": _dialogue.ScriptSave(); L("script: dialogue saved"); break;
                    case "--redo": await Redo(); L("script: redo"); break;
                    case "--path-link":
                    {
                        var m = _view.Selected?.Marker ?? throw new InvalidOperationException("--path-link: select a path node first");
                        if (m.Type != 22) throw new InvalidOperationException("--path-link: selection is not a path node");
                        int before = m.Link; m.Link = int.Parse(Next()); _view.Refresh3D(); UpdateTitle();
                        L($"script: path node #{m.Index} next {before} -> {m.Link}"); break;
                    }
                    case "--save": SaveWorld(); L("script: saved"); break;
                    case "--build-scene": await BuildScene(Next()); L("script: scene built"); break;
                    case "--import-vmf":
                    {
                        // --import-vmf <file.vmf> [game folder|-]: Tools > Import Source Map with default options
                        var f = Next(); var g = i + 1 < a.Count && !a[i + 1].StartsWith("--") ? Next() : null;
                        await ImportVmf(f, new NB.Core.SourceEngine.VmfImportOptions { GameFolder = g is null or "-" ? null : g });
                        L("script: source map imported"); break;
                    }
                    case "--vmf-dialog-shot":
                    {
                        // --vmf-dialog-shot <file.vmf> <png>: the import dialog analysing a map, captured, then cancelled
                        var f = Next(); var png = Next();
                        NB.Core.Formats.CaffFile? world = null;
                        try { world = _ws?.LoadResident(NB.Core.SourceEngine.VmfImportOptions.ShowdownTown); } catch (Exception) { }
                        using var dlg = new Panels.VmfImportDialog(f, world);
                        dlg.StartPosition = FormStartPosition.Manual; dlg.Location = new Point(Left + 60, Top + 60);
                        dlg.Show(this); Application.DoEvents(); await Task.Delay(300); dlg.Analyse(); Application.DoEvents(); await Task.Delay(300); Application.DoEvents();
                        using (var bmp = new Bitmap(dlg.Width, dlg.Height)) { dlg.DrawToBitmap(bmp, new Rectangle(0, 0, dlg.Width, dlg.Height)); bmp.Save(png); }
                        dlg.Close(); L($"script: vmf dialog captured {png}"); break;
                    }
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
                    case "--tour": StartTour(); await Task.Delay(400); L("script: tour started"); break;
                    case "--tour-step": { int n = int.Parse(Next()); _tour?.GoTo(n); await Task.Delay(400); L($"script: tour step {n}"); break; }
                    case "--tour-end": _tour?.Dispose(); _tour = null; break;
                    case "--menu-open":
                    {
                        // opens a menu path such as "File/Open Recent" (stays open for --screen)
                        ToolStripItemCollection items = MainMenuStrip!.Items;
                        foreach (var part in Next().Split('/'))
                        {
                            var mi = items.OfType<ToolStripMenuItem>().First(x => x.Text.Replace("&", "").StartsWith(part));
                            mi.ShowDropDown(); Application.DoEvents(); await Task.Delay(300); Application.DoEvents();
                            foreach (ToolStripItem it in mi.DropDownItems) L($"menu {part}: " + it.Text + (it.Enabled ? "" : " (disabled)") + (it is ToolStripMenuItem t && t.Checked ? "  [x]" : ""));
                            items = mi.DropDownItems;
                        }
                        break;
                    }
                    case "--menu-path-shot":
                    {
                        // a menu path ("File/Open Recent") drawn dropdown by dropdown, side by side
                        ToolStripItemCollection items = MainMenuStrip!.Items;
                        var shots = new List<Bitmap>();
                        foreach (var part in Next().Split('/'))
                        {
                            var mi = items.OfType<ToolStripMenuItem>().First(x => x.Text.Replace("&", "").StartsWith(part));
                            mi.ShowDropDown(); Application.DoEvents(); await Task.Delay(300); Application.DoEvents();
                            var dd = mi.DropDown; var b = new Bitmap(dd.Width, dd.Height); dd.DrawToBitmap(b, new Rectangle(0, 0, dd.Width, dd.Height)); shots.Add(b);
                            foreach (ToolStripItem it in mi.DropDownItems) L($"menu {part}: " + it.Text + (it.Enabled ? "" : " (disabled)") + (it is ToolStripMenuItem t && t.Checked ? "  [x]" : ""));
                            items = mi.DropDownItems;
                        }
                        using (var all = new Bitmap(shots.Sum(b => b.Width) + 8 * shots.Count, shots.Max(b => b.Height)))
                        {
                            using (var g = Graphics.FromImage(all)) { g.Clear(Color.White); int x = 0; foreach (var b in shots) { g.DrawImage(b, x, 0); x += b.Width + 8; } }
                            all.Save(Next());
                        }
                        foreach (var b in shots) b.Dispose();
                        foreach (ToolStripMenuItem mi in MainMenuStrip!.Items) mi.HideDropDown();
                        break;
                    }
                    case "--menu-close": foreach (ToolStripMenuItem mi in MainMenuStrip!.Items) mi.HideDropDown(); break;
                    case "--settings-shot":
                    {
                        using var d = new Panels.SettingsDialog(_settings, _history.Count, _ws?.Manifest.ExeMods);
                        d.StartPosition = FormStartPosition.Manual; d.Location = new Point(Left + 100, Top + 100);
                        d.Show(this); Application.DoEvents(); await Task.Delay(500); Application.DoEvents();
                        using var bmp = new Bitmap(d.Width, d.Height); d.DrawToBitmap(bmp, new Rectangle(0, 0, d.Width, d.Height));
                        bmp.Save(Next()); d.Close(); L("script: settings dialog captured"); break;
                    }
                    case "--settings":
                    {
                        // --settings key=value (UndoSteps, SScales)
                        var kv = Next().Split('=');
                        if (kv[0] == "UndoSteps") { _settings.UndoSteps = int.Parse(kv[1]); _history.Limit = _settings.UndoSteps; _history.ApplyLimit(); }
                        else if (kv[0] == "SScales") { _settings.SScales = bool.Parse(kv[1]); _view.SScales = _settings.SScales; }
                        L($"script: setting {kv[0]} = {kv[1]}; undo history {_history.Count} step(s)"); break;
                    }
                    case "--new-workspace":
                    {
                        // --new-workspace <original game dir> <new folder>: File > New Workspace without the folder dialogs
                        var src = Next(); var dst = Next();
                        var created = await Task.Run(() => Workspace.Create(src, dst));
                        ApplyNewWorkspaceMods(created);
                        await OpenWorkspace(dst);
                        L($"script: new workspace {dst}: mods {string.Join(", ", _ws!.Manifest.ExeMods)}"); break;
                    }
                    case "--mods-menu":
                    {
                        // the Mods menu as the user sees it (checked / disabled / text)
                        var mi = MainMenuStrip!.Items.OfType<ToolStripMenuItem>().First(x => x.Text.Replace("&", "") == "Mods");
                        mi.ShowDropDown(); Application.DoEvents(); await Task.Delay(300); Application.DoEvents();
                        foreach (ToolStripItem it in mi.DropDownItems) L($"menu Mods: {(it is ToolStripMenuItem t && t.Checked ? "[x]" : "[ ]")} {it.Text}{(it.Enabled ? "" : " (disabled)")}");
                        if (i + 1 < a.Count && !a[i + 1].StartsWith("--"))
                        {
                            var dd = mi.DropDown; using var bmp = new Bitmap(dd.Width, dd.Height); dd.DrawToBitmap(bmp, new Rectangle(0, 0, dd.Width, dd.Height)); bmp.Save(Next());
                        }
                        mi.HideDropDown(); break;
                    }
                    case "--exe-mods-check": ApplyExeMods(); L("script: executable mods checked (see the log above)"); break;
                    case "--export-console": { var dir = Next(); int n = await Task.Run(() => _ws!.Export(dir, true, null, bakeExeMods: true)); L($"script: console export {n} file(s) -> {dir}"); break; }
                    case "--history": L($"script: history {_history.Count} step(s); undo: {_history.UndoLabel ?? "-"}; redo: {_history.RedoLabel ?? "-"}"); break;
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
                    case "--folder-patch-shot":
                    {
                        // --folder-patch-shot <modified dir> <clean dir> <analysis png> <details png>: Create Patch from a Modified Game
                        // Folder, analysed and captured, then the prefilled mod details captured; nothing is built
                        var mod = Next(); var clean = Next(); var png1 = Next(); var png2 = Next();
                        using var dlg = new Panels.FolderPatchDialog(CleanGameCandidates());
                        dlg.Show(this); Application.DoEvents();
                        await dlg.ScriptAnalyse(mod, clean); Application.DoEvents(); await Task.Delay(300); Application.DoEvents();
                        using (var bmp = new Bitmap(dlg.Width, dlg.Height)) { dlg.DrawToBitmap(bmp, new Rectangle(0, 0, dlg.Width, dlg.Height)); bmp.Save(png1); }
                        var rep = dlg.Report!;
                        L($"script: folder analysed: {rep.Files.Count} file(s), category {rep.Category}, multiplayer {rep.Multiplayer}, reference {rep.ReferenceDir}");
                        dlg.Close();
                        int exeWords = rep.Exe == null ? 0 : rep.Exe.Known.Count + (rep.Exe.Other.Count > 0 ? 1 : 0);
                        using var info = new Panels.PatchInfoDialog(rep.SuggestedName, Environment.UserName, ModCategories.Find(rep.Category) ?? ModCategories.Tweak, rep.Carried.Count(), exeWords,
                            $"The mod holds the differences between {rep.ModDir} and the clean game: {rep.Carried.Count()} file(s)" + (exeWords > 0 ? $" and {exeWords} executable mod(s)" : "") + ". It contains no original game data.",
                            rep.SuggestedDescription, rep.Tags, rep.Multiplayer);
                        info.Show(this); Application.DoEvents(); await Task.Delay(300); Application.DoEvents();
                        using (var bmp = new Bitmap(info.Width, info.Height)) { info.DrawToBitmap(bmp, new Rectangle(0, 0, info.Width, info.Height)); bmp.Save(png2); }
                        info.Close(); L("script: mod details captured"); break;
                    }
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
                    case "--copy": L($"script: copy {(CopySelection() ? "ok: " + _clip[0].Name : "refused")}"); break;
                    case "--cut": { var n = _view.Selected?.Name; await CutSelection(); L($"script: cut {n}; selected {_view.Selected?.Name ?? "-"}; clipboard {(_clip.Count > 0 ? _clip[0].Name : "-")}"); break; }
                    case "--del": { var n = _view.Selected?.Name; await DeleteSelection(); L($"script: delete {n}; selected {_view.Selected?.Name ?? "-"}"); break; }
                    case "--paste":
                    {
                        // --paste X,Y (3D-view pixel) or --paste - (mouse / view centre)
                        var v = Next(); Point? at = v == "-" ? null : new Point(int.Parse(v.Split(',')[0]), int.Parse(v.Split(',')[1]));
                        var o = await PasteClipboard(at);
                        L($"script: paste -> {(o == null ? "nothing" : $"{o.Name} (instance {o.Instance?.Index}) at {Fmt(o.Transform.Translation)}")}; history {_history.Count}: {_history.UndoLabel}"); break;
                    }
                    case "--quicktest": { bool cam = i + 1 < a.Count && a[i + 1] == "camera"; if (cam) i++; await QuickTestXenia(cam); L($"script: quick test finished: pid {_qtProcess?.Id}"); break; }
                    case "--quicktest-launch":
                    {
                        // starts the test and returns at once (the automatic start runs on; --quicktest-wait waits for it)
                        bool cam = i + 1 < a.Count && a[i + 1] == "camera"; if (cam) i++;
                        _ = QuickTestXenia(cam);
                        for (int k = 0; k < 600 && _qtBoot == null; k++) await Task.Delay(100);
                        L($"script: quick test started: pid {_qtProcess?.Id}"); break;
                    }
                    case "--quicktest-reset": await ResetTestSave(); L("script: test save reset"); break;
                    case "--quicktest-blueprints": L("script: test blueprints: " + string.Join(", ", QuickTest.TestBlueprints(Path.Combine(QuickTest.Folder(_ws!), "xenia", "content")))); break;
                    case "--quicktest-wait": { if (_qtBoot != null) L("script: quick test: " + await _qtBoot); break; }
                    case "--quicktest-pid": { var f = Next(); File.WriteAllText(f, _qtProcess?.Id.ToString() ?? ""); L($"script: quick test pid {_qtProcess?.Id} -> {f}"); break; }
                    case "--import-collision":
                    case "--import-collision-box":
                    {
                        bool bx = a[i] == "--import-collision-box"; var obj = Next();
                        await ImportCollision(_view.Selected!, obj, confirm: false, box: bx);
                        _scene?.LoadCollision();
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
                        foreach (var tc in new[] { _center, _right, _leftTabs })
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
                    // ---- Atmosphere tab, weather, texture batch, patch details (same code paths as the controls)
                    case "--atmos": ShowAtmosphere(null); await Task.Delay(300); L("script: atmosphere: " + _atmos.ScriptState()); break;
                    case "--atmos-select": { var q = Next(); L($"script: atmosphere {_atmos.ScriptSelect(q)}: {_atmos.ScriptState()}"); await Task.Delay(200); break; }
                    case "--atmos-set": { var f = Next(); var v = Next(); _atmos.ScriptSet(f, v); L($"script: atmosphere {f} = {v}: {_atmos.ScriptState()}"); break; }
                    case "--atmos-winter": _atmos.ApplyWinter(); L("script: atmosphere winter preset: " + _atmos.ScriptState()); break;
                    case "--atmos-sky":
                    {
                        var q = Next(); var file = Next();
                        var stem = _atmos.SkyStems.First(x => x.Contains(q, StringComparison.OrdinalIgnoreCase));
                        L($"script: sky texture {stem} <- {file}: {(await _atmos.ReplaceSky(stem, file, confirm: false) ? "replaced" : "FAILED")}"); break;
                    }
                    case "--atmos-save": _atmos.Save(); L("script: atmosphere saved"); break;
                    case "--atmos-discard": _atmos.Discard(); L("script: atmosphere changes discarded: " + _atmos.ScriptState()); break;
                    case "--atmos-live": { int pid = int.Parse(Next()); L("script: atmosphere live: " + _atmos.AttachLive(pid)); break; }
                    case "--atmos-live-push": L($"script: atmosphere live push: {_atmos.PushLive()}; game reads back {_atmos.ReadLive()}"); break;
                    case "--weather-apply": await _atmos.ApplySnow(); L("script: weather applied: " + _atmos.ScriptState()); break;
                    case "--weather-remove": await _atmos.RemoveSnow(confirm: false); L("script: weather removed: " + _atmos.ScriptState()); break;
                    case "--texlib-folder":
                    {
                        var dir = Next();
                        L($"script: texture library replace from folder {dir}: {await _texLib!.ReplaceFromFolder(dir, confirm: false)}");
                        L("script:   replaced: " + string.Join(", ", _texLib!.LastBatch.Select(x => x.Replace("aid_texture_banjox_", "")))); break;
                    }
                    case "--texlib-folder-shot":
                    {
                        var dir = Next(); var png = Next();
                        using var dlg = new Panels.BatchTextureDialog(new TextureHost(this, null), dir);
                        dlg.Show(this); for (int k = 0; k < 400 && dlg.Visible; k++) { Application.DoEvents(); await Task.Delay(25); }
                        using (var bmp = new Bitmap(dlg.Width, dlg.Height)) { dlg.DrawToBitmap(bmp, new Rectangle(0, 0, dlg.Width, dlg.Height)); bmp.Save(png); }
                        L($"script: replace-from-folder dialog: {dlg.Matches.Count} match(es), {dlg.Unmatched.Count} unmatched; captured {png}");
                        dlg.Close(); break;
                    }
                    case "--patch-picture": _scriptPictures.Add(Next()); break;
                    case "--patch-create":
                    {
                        // --patch-create <out.nbpatch> <name> <version> <author> <category> <tags> <multiplayer> <description> <dialog png>
                        var target = Next(); var name = Next(); var ver = Next(); var author = Next(); var cat = Next(); var tags = Next(); var mp = Next(); var desc = Next(); var png = Next();
                        await CreatePatch(async info =>
                        {
                            info.ScriptFill(name, ver, author, cat, tags, mp, desc);
                            foreach (var p in _scriptPictures) info.AddPicture(p);
                            info.Show(this); Application.DoEvents(); await Task.Delay(400); Application.DoEvents();
                            using (var bmp = new Bitmap(info.Width, info.Height)) { info.DrawToBitmap(bmp, new Rectangle(0, 0, info.Width, info.Height)); bmp.Save(png); }
                            info.Hide();
                            return target;
                        });
                        L($"script: patch created {target} ({(File.Exists(target) ? new FileInfo(target).Length : 0):N0} bytes)"); break;
                    }
                    case "--log": i++; break;
                    case "--start-bg": { var f = Next(); _start.SetBackground(f); L("script: start page background " + f); break; }
                    case "--exit": L("script: exit"); Close(); return;
                    default:
                        if (await _view.RunScriptCommand(a[i], Next, L)) break;
                        L("script: unknown argument " + a[i]); break;
                }
                await Task.Delay(50);
            }
        }
        catch (Exception e) { L("script error: " + e); if (a.Contains("--exit")) Close(); }
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
