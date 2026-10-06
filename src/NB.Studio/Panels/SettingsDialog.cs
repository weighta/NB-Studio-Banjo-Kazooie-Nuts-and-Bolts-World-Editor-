namespace NB.Studio.Panels;

/// <summary>
/// File > Settings: undo steps, the 3D view's S key, what happens at startup, and the Xenia used by Launch. Same look as
/// the start page and the tour (charcoal, orange accent). Changes go into <see cref="Settings"/> on OK.
/// </summary>
public sealed class SettingsDialog : Form
{
    static readonly Color Bg = Color.FromArgb(28, 30, 38), Card = Color.FromArgb(36, 39, 49), Edge = Color.FromArgb(62, 66, 80),
        Accent = Color.FromArgb(255, 170, 70), Text1 = Color.FromArgb(236, 236, 242), Text2 = Color.FromArgb(160, 164, 178);

    readonly Settings _s;
    readonly NumericUpDown _undo = new() { Minimum = 1, Maximum = 1000, Width = 90, BackColor = Color.FromArgb(46, 49, 60), ForeColor = Text1, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 10.5f) };
    readonly CheckBox _sScales = Check("S scales the selected object (Blender style)");
    readonly CheckBox _autoOpen = Check("Open the last workspace when NB Studio starts");
    readonly TextBox _xenia = new() { BackColor = Color.FromArgb(46, 49, 60), ForeColor = Text1, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 9.5f) };

    readonly CheckedListBox _mods = new() { CheckOnClick = true, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(46, 49, 60), ForeColor = Color.FromArgb(236, 236, 242), Font = new Font("Segoe UI", 9f), IntegralHeight = false };

    /// <summary>The mod ids listed in <see cref="_mods"/> (same order).</summary>
    readonly List<string> _modIds = new();

    void FillMods(IEnumerable<string> ticked)
    {
        var on = new HashSet<string>(ticked);
        _mods.Items.Clear(); _modIds.Clear();
        // every executable mod of the Mods menu, plus custom values (e.g. part limit 600) that are in the list
        var ids = NB.Core.Mods.ExePatches.All.Select(m => m.Id).Concat(on.Where(i => NB.Core.Mods.ExePatches.All.All(m => m.Id != i))).ToList();
        foreach (var id in ids)
        {
            var name = NB.Core.Mods.ExePatches.Resolve(id)?.Name ?? id;
            _modIds.Add(id); _mods.Items.Add(name, on.Contains(id));
        }
    }

    public SettingsDialog(Settings s, int undoInHistory, IReadOnlyList<string>? workspaceMods = null)
    {
        _s = s;
        Text = "Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent; BackColor = Bg; ForeColor = Text1; Font = new Font("Segoe UI", 10f);
        ClientSize = new Size(600, 832);
        AutoScaleMode = AutoScaleMode.Dpi;

        var title = new Label { UseMnemonic = false, Text = "Settings", Font = new Font("Segoe UI Semibold", 16f), ForeColor = Accent, AutoSize = true, Location = new Point(22, 16) };
        Controls.Add(title);
        int y = 62;

        // ---- undo
        var undoCard = Section("Undo", ref y, 156);
        undoCard.Controls.Add(Label("Steps Ctrl+Z can go back:", 16, 40));
        _undo.Location = new Point(230, 37);
        _undo.Value = Math.Clamp(s.UndoSteps, 1, 1000);
        undoCard.Controls.Add(_undo);
        undoCard.Controls.Add(Note(
            "Everything you change can be undone: moving, rotating and scaling objects, path links, and actions that write the game " +
            $"files (import, duplicate, delete, tag, atmosphere and texture saves). {undoInHistory} step(s) in the history now. " +
            "Each file action keeps a copy of the files it changed until it leaves the history, so very high numbers use more disk space.",
            16, 72, 530, 76));

        // ---- 3D view
        var viewCard = Section("3D view", ref y, 112);
        _sScales.Location = new Point(16, 36); _sScales.Checked = s.SScales;
        viewCard.Controls.Add(_sScales);
        viewCard.Controls.Add(Note("While you fly (right mouse button held, or W A D Q E pressed a moment ago), S always flies backwards. " +
            "Untick to make S fly backwards all the time (scale with the Scale tool, 3, instead).", 36, 64, 520, 40));

        // ---- start
        var startCard = Section("Starting NB Studio", ref y, 72);
        _autoOpen.Location = new Point(16, 36); _autoOpen.Checked = s.AutoOpenLast;
        startCard.Controls.Add(_autoOpen);

        // ---- xenia
        var xCard = Section("Xenia (Build > Launch in Xenia, F5)", ref y, 84);
        _xenia.Location = new Point(16, 40); _xenia.Width = 420; _xenia.Text = s.XeniaPath ?? "";
        var browse = Btn("Browse…");
        browse.Location = new Point(446, 37);
        browse.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Filter = "Xenia (xenia*.exe)|xenia*.exe|Programs (*.exe)|*.exe", Title = "Xenia executable" };
            try { if (File.Exists(_xenia.Text)) d.InitialDirectory = Path.GetDirectoryName(_xenia.Text); } catch (Exception) { }
            if (d.ShowDialog(this) == DialogResult.OK) _xenia.Text = d.FileName;
        };
        xCard.Controls.Add(_xenia); xCard.Controls.Add(browse);

        // ---- mods ticked in every new workspace
        var modCard = Section("Mods for new workspaces", ref y, 230);
        modCard.Controls.Add(Note("Ticked in every workspace you create (File > New Workspace); workspaces you already have are not changed. " +
            "Change a workspace's own mods on the Mods menu.", 16, 32, 530, 34));
        _mods.Location = new Point(16, 68); _mods.Size = new Size(528, 118);
        FillMods(s.NewWorkspaceModsOrDefault);
        modCard.Controls.Add(_mods);
        var useWs = Btn("Use this workspace's mods"); useWs.Location = new Point(16, 192); useWs.Enabled = workspaceMods != null;
        useWs.Click += (_, _) => { if (workspaceMods != null) FillMods(workspaceMods); };
        var reset = Btn("Reset to recommended"); reset.Location = new Point(230, 192);
        reset.Click += (_, _) => FillMods(NB.Core.Mods.ExePatches.RecommendedForNewWorkspaces);
        modCard.Controls.Add(useWs); modCard.Controls.Add(reset);

        // ---- buttons
        var ok = Btn("Save", true); var cancel = Btn("Cancel");
        ok.DialogResult = DialogResult.OK; cancel.DialogResult = DialogResult.Cancel;
        var bar = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Location = new Point(20, ClientSize.Height - 52), Size = new Size(ClientSize.Width - 40, 40), BackColor = Color.Transparent };
        bar.Controls.Add(ok); bar.Controls.Add(cancel);
        Controls.Add(bar);
        AcceptButton = ok; CancelButton = cancel;
        ok.Click += (_, _) =>
        {
            _s.UndoSteps = (int)_undo.Value;
            _s.SScales = _sScales.Checked;
            _s.AutoOpenLast = _autoOpen.Checked;
            var list = _modIds.Where((_, i) => _mods.GetItemChecked(i)).ToList();
            // the recommended set is stored as "not customised", so later NB Studio versions can extend it
            bool same = list.Count == NB.Core.Mods.ExePatches.RecommendedForNewWorkspaces.Count && !list.Except(NB.Core.Mods.ExePatches.RecommendedForNewWorkspaces).Any();
            _s.NewWorkspaceMods = same ? null : list;
            var x = _xenia.Text.Trim().Trim('"');
            _s.XeniaPath = x.Length == 0 ? null : x;
        };
    }

    Panel Section(string heading, ref int y, int height)
    {
        var p = new Panel { Location = new Point(20, y), Size = new Size(560, height), BackColor = Card };
        p.Paint += (_, e) => { using var pen = new Pen(Edge); e.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1); using var a = new SolidBrush(Accent); e.Graphics.FillRectangle(a, 0, 8, 3, 18); };
        p.Controls.Add(new Label { UseMnemonic = false, Text = heading, Font = new Font("Segoe UI Semibold", 11f), ForeColor = Text1, AutoSize = true, Location = new Point(12, 7) });
        Controls.Add(p);
        y += height + 12;
        return p;
    }

    static Label Label(string t, int x, int y) => new() { UseMnemonic = false, Text = t, AutoSize = true, Location = new Point(x, y), ForeColor = Text1, BackColor = Color.Transparent };

    static Label Note(string t, int x, int y, int w, int h) => new()
    { UseMnemonic = false, Text = t, Location = new Point(x, y), Size = new Size(w, h), ForeColor = Text2, BackColor = Color.Transparent, Font = new Font("Segoe UI", 8.75f) };

    static CheckBox Check(string t) => new() { UseMnemonic = false, Text = t, AutoSize = true, ForeColor = Color.FromArgb(236, 236, 242), BackColor = Color.Transparent, Font = new Font("Segoe UI", 10f), FlatStyle = FlatStyle.Standard };

    static Button Btn(string t, bool primary = false)
    {
        var b = new Button
        {
            Text = t, AutoSize = true, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 10f, primary ? FontStyle.Bold : FontStyle.Regular),
            BackColor = primary ? Color.FromArgb(242, 140, 40) : Color.FromArgb(48, 50, 62), ForeColor = primary ? Color.FromArgb(22, 18, 14) : Color.FromArgb(230, 230, 236),
            Margin = new Padding(6, 0, 0, 0), Padding = new Padding(10, 2, 10, 2), Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderColor = primary ? Color.FromArgb(255, 200, 140) : Color.FromArgb(80, 84, 98);
        return b;
    }
}
