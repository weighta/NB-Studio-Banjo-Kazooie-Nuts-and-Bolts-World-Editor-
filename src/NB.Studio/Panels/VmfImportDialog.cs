using NB.Core.SourceEngine;

namespace NB.Studio.Panels;

/// <summary>
/// Tools > Import Source Map (.vmf): the map, the Source game whose materials give the textures (Garry's Mod install or a
/// game folder with VPKs; detected when the map sits inside one), an optional folder of images per material, the scale,
/// light bake, 3D skybox / clip / town-object options and props. "Analyse" plans the import (no files written) and shows
/// what it would make with the size estimate against the world's memory budget.
/// </summary>
public sealed class VmfImportDialog : Form
{
    readonly TextBox _file = new() { Width = 520 }, _game = new() { Width = 520 }, _mats = new() { Width = 520 }, _props = new() { Width = 520 };
    readonly NumericUpDown _scale = new() { DecimalPlaces = 4, Increment = 0.005m, Minimum = 0.005m, Maximum = 0.5m, Value = 0.04m, Width = 80 };
    readonly NumericUpDown _luxel = new() { DecimalPlaces = 0, Minimum = 4, Maximum = 256, Value = 16, Width = 60 };
    readonly NumericUpDown _exposure = new() { DecimalPlaces = 2, Increment = 0.1m, Minimum = 0.1m, Maximum = 5, Value = 1, Width = 60 };
    readonly ComboBox _texSize = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70 };
    readonly CheckBox _bake = new() { Text = "Bake lights (light, light_spot, light_environment) into lightmaps", Checked = true, AutoSize = true };
    readonly ComboBox _sky3d = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
    readonly CheckBox _skyReplica = new() { Text = "Skip the skybox's replica of the map", Checked = true, AutoSize = true };
    readonly NumericUpDown _skyLuxel = new() { DecimalPlaces = 1, Increment = 0.5m, Minimum = 0.5m, Maximum = 16, Value = 1, Width = 50 };
    readonly NumericUpDown _skyStep = new() { DecimalPlaces = 0, Minimum = 1, Maximum = 8, Value = 2, Width = 45 };
    readonly CheckBox _skyCol = new() { Text = "Sky walls (tools/toolsskybox brushes) stay as invisible walls", Checked = false, AutoSize = true };
    readonly CheckBox _playerClip = new() { Text = "Player clips collide", Checked = false, AutoSize = true };
    readonly CheckBox _keepHull = new() { Text = "Outer nodraw hull (the leak seal around the map) stays as invisible walls", Checked = false, AutoSize = true };
    readonly CheckBox _townObjects = new() { Text = "Move the town's characters and objects out of the way", Checked = true, AutoSize = true };
    readonly CheckBox _garage = new() { Text = "Keep Mumbo's Motors (garage entrance)", Checked = false, AutoSize = true };
    readonly CheckBox _spawnHeight = new() { Text = "Put the player start at ground height", Checked = true, AutoSize = true };
    readonly CheckBox _includeProps = new() { Text = "Include props (prop_static / prop_dynamic) from OBJ/FBX files in:", Checked = false, AutoSize = true };
    readonly TextBox _plan = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font(FontFamily.GenericMonospace, 8.5f) };
    readonly Label _budget = new() { AutoSize = true, Padding = new Padding(4, 8, 4, 0), Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold) };
    readonly Button _ok = new() { Text = "Import into Showdown Town", DialogResult = DialogResult.OK, AutoSize = true };
    readonly NB.Core.Formats.CaffFile? _world;

    public string MapPath => _file.Text;

    public VmfImportOptions Options
    {
        get
        {
            var o = new VmfImportOptions
            {
                Scale = (float)_scale.Value, GameFolder = _game.Text.Length > 0 ? _game.Text : null, MaterialFolder = _mats.Text.Length > 0 ? _mats.Text : null,
                Skybox = (Skybox3DMode)_sky3d.SelectedIndex, SkipSkyboxInsideMap = _skyReplica.Checked, SkyLuxelScale = (float)_skyLuxel.Value,
                SkyCollisionStep = (int)_skyStep.Value, KeepSkyBrushes = _skyCol.Checked, KeepHull = _keepHull.Checked, PlayerClipCollision = _playerClip.Checked,
                RemoveTownObjects = _townObjects.Checked, KeepGarage = _garage.Checked, SpawnAtGroundHeight = _spawnHeight.Checked,
                IncludeProps = _includeProps.Checked, PropFolder = _props.Text.Length > 0 ? _props.Text : null,
                MaxTextureSize = int.Parse((string)_texSize.SelectedItem!),
            };
            o.Bake.Enabled = _bake.Checked; o.Bake.LuxelSize = (float)_luxel.Value; o.Bake.Exposure = (float)_exposure.Value;
            return o;
        }
    }

    /// <param name="world">The target world bundle (exact template sizes for the estimate), or null.</param>
    public VmfImportDialog(string? file, NB.Core.Formats.CaffFile? world)
    {
        _world = world;
        Text = "Import Source Map (.vmf)"; Width = 1000; Height = 760; StartPosition = FormStartPosition.CenterParent; MinimizeBox = false;
        _texSize.Items.AddRange(new object[] { "128", "256", "512", "1024" }); _texSize.SelectedItem = "512";
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(6) };
        void Row(string label, Control c, Action? browse)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            grid.Controls.Add(c);
            if (browse != null) { var b = new Button { Text = "Browse…", AutoSize = true }; b.Click += (_, _) => browse(); grid.Controls.Add(b); }
            else grid.Controls.Add(new Label());
        }
        Row("Map (.vmf):", _file, () =>
        {
            using var d = new OpenFileDialog { Filter = "Hammer map (*.vmf)|*.vmf", Title = "Source map to import" };
            if (d.ShowDialog(this) == DialogResult.OK) { _file.Text = d.FileName; DetectGame(); Analyse(); }
        });
        Row("Source game folder:", _game, () => { var f = PickFolder("Source game folder (Garry's Mod install, or a game folder with *_dir.vpk files). Read only."); if (f != null) _game.Text = f; });
        Row("Material images (optional):", _mats, () => { var f = PickFolder("Folder with one PNG/TGA/JPG per material (brick/brickwall001a.png); wins over the game's textures"); if (f != null) _mats.Text = f; });
        var opts = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(6, 0, 6, 0) };
        void Lbl(string t) => opts.Controls.Add(new Label { Text = t, AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
        Lbl("Scale (game units per Source unit):"); opts.Controls.Add(_scale);
        Lbl("Max texture size:"); opts.Controls.Add(_texSize);
        opts.SetFlowBreak(_texSize, true);
        opts.Controls.Add(_bake); Lbl("Luxel (Source units):"); opts.Controls.Add(_luxel); Lbl("Exposure:"); opts.Controls.Add(_exposure);
        opts.SetFlowBreak(_exposure, true);
        _sky3d.Items.AddRange(new object[] { "3D skybox: port as full-size terrain", "3D skybox: drop", "3D skybox: keep in place (tiny)" }); _sky3d.SelectedIndex = 0;
        opts.Controls.Add(_sky3d); opts.Controls.Add(_skyReplica); Lbl("Skybox luxel x:"); opts.Controls.Add(_skyLuxel); Lbl("collision every n-th row:"); opts.Controls.Add(_skyStep);
        opts.SetFlowBreak(_skyStep, true);
        foreach (var c in new Control[] { _skyCol, _keepHull, _playerClip, _spawnHeight, _townObjects, _garage }) opts.Controls.Add(c);
        opts.SetFlowBreak(_garage, true);
        opts.Controls.Add(_includeProps); opts.Controls.Add(_props);
        var pb = new Button { Text = "Browse…", AutoSize = true };
        pb.Click += (_, _) => { var f = PickFolder("Folder with prop models as OBJ/FBX by model path (props_c17/oildrum001.obj)"); if (f != null) _props.Text = f; };
        opts.Controls.Add(pb);
        var help = new Label
        {
            Dock = DockStyle.Top, Height = 62, Padding = new Padding(8, 4, 8, 0),
            Text = "The map replaces Showdown Town's geometry (the town is rebuilt from its original bundle on every import; undo: Build > Revert). " +
                   "Brushes, displacements and brush entities become textured, lightmapped models with collision; tool textures (nodraw, clip, trigger, skybox …) are never drawn. " +
                   "1 Source unit = 1 inch; the default scale 0.04 makes doors wide enough for the game's vehicles.",
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var analyse = new Button { Text = "Analyse", AutoSize = true };
        analyse.Click += (_, _) => Analyse();
        buttons.Controls.Add(cancel); buttons.Controls.Add(_ok); buttons.Controls.Add(analyse); buttons.Controls.Add(_budget);
        AcceptButton = _ok; CancelButton = cancel;
        Controls.Add(_plan); Controls.Add(buttons); Controls.Add(opts); Controls.Add(grid); Controls.Add(help);
        _ok.Enabled = false;
        if (file != null) { _file.Text = file; DetectGame(); }
        Shown += (_, _) => { if (_file.Text.Length > 0) Analyse(); };
    }

    string? PickFolder(string title)
    {
        using var d = new FolderBrowserDialog { Description = title, UseDescriptionForTitle = true };
        return d.ShowDialog(this) == DialogResult.OK ? d.SelectedPath : null;
    }

    /// <summary>A map inside a Garry's Mod install (…\GarrysMod\bin\x.vmf) uses that install's content.</summary>
    void DetectGame()
    {
        if (_game.Text.Length > 0 || _file.Text.Length == 0) return;
        var d = Path.GetDirectoryName(Path.GetFullPath(_file.Text));
        for (int i = 0; i < 4 && d != null; i++, d = Path.GetDirectoryName(d))
            if (Directory.Exists(Path.Combine(d, "garrysmod")) && Directory.Exists(Path.Combine(d, "sourceengine"))) { _game.Text = d; return; }
        // else a Garry's Mod install in the default Steam library
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
        {
            var g = Path.Combine(root, "Steam", "steamapps", "common", "GarrysMod");
            if (root.Length > 0 && Directory.Exists(Path.Combine(g, "garrysmod"))) { _game.Text = g; return; }
        }
    }

    /// <summary>Plans the import with the current options and shows the summary (nothing is written).</summary>
    public VmfPlan? Analyse()
    {
        _ok.Enabled = false;
        if (!File.Exists(_file.Text)) { _plan.Text = "Choose a .vmf file."; return null; }
        try
        {
            Cursor = Cursors.WaitCursor;
            var map = VmfMap.Load(_file.Text);
            var plan = VmfImporter.Plan(map, Options, _world);
            // the memory budget first (it decides whether the map runs on a real console), then the details
            var lines = VmfImporter.Budget(plan);
            lines.Add("");
            lines.AddRange(VmfImporter.Describe(plan).Where(x => !x.StartsWith("memory budget") && !x.StartsWith("  ") ));
            lines.Add("");
            lines.Add("materials (triangles, texture, source):");
            lines.AddRange(plan.Materials.Select(m => $"  {m.Material,-48} {m.Triangles,7}  {m.OutW}x{m.OutH}  {m.Source}"));
            var notDrawn = plan.MaterialUses.Where(kv => !kv.Value.Draw).Select(kv => $"  {kv.Key} ({kv.Value.Why}{(kv.Value.Collide ? ", collides" : "")})").ToList();
            if (notDrawn.Count > 0) { lines.Add("not drawn:"); lines.AddRange(notDrawn); }
            if (plan.Props.Count > 0)
            {
                lines.Add("props (model, count, file):");
                lines.AddRange(plan.Props.Select(p => $"  {p.Model,-56} x{p.Instances,-4} {p.File ?? "(no OBJ/FBX)"}{(p.GameVertices >= 0 ? $"  game mesh {p.GameVertices} vertices" : "")}"));
            }
            _plan.Text = string.Join(Environment.NewLine, lines);
            _budget.Text = plan.BudgetBytes > 0 ? $"World bundle ~{plan.EstimatedBytes / 1048576.0:F0} MB of {plan.BudgetBytes / 1048576.0:F0} MB ({plan.BudgetState})" : "";
            _budget.ForeColor = plan.BudgetState == "ok" ? Color.DarkGreen : plan.BudgetState == "warning" ? Color.DarkOrange : Color.Firebrick;
            _ok.Enabled = plan.RenderTriangles > 0 || plan.CollisionTriangles > 0;
            plan.Content?.Dispose();
            return plan;
        }
        catch (Exception e) { _plan.Text = "Cannot read the map: " + e.Message; return null; }
        finally { Cursor = Cursors.Default; }
    }
}
