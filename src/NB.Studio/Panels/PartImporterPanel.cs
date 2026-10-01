using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using NB.Core.Models;
using NB.Core.Parts;
using NB.Core.Project;
using NB.Core.Textures;

namespace NB.Studio.Panels;

/// <summary>
/// Vehicle-part importer: edits part specs (NB.Core.Parts.PartFactory.PartSpec, the same JSON as `NB.Cli part-build`),
/// previews the model with its footprint, edits attachment points, validates, builds the part into the workspace and
/// removes it again.
/// </summary>
public sealed class PartImporterPanel : UserControl
{
    public Action<string>? Log;
    Workspace? _ws; AssetIndex? _idx;
    string? _file;
    readonly List<PartFactory.PartSpec> _specs = new();
    PartFactory.PartSpec? Cur => _list.SelectedIndex >= 0 && _list.SelectedIndex < _specs.Count ? _specs[_list.SelectedIndex] : null;
    string BaseDir => _file != null ? Path.GetDirectoryName(Path.GetFullPath(_file))! : Environment.CurrentDirectory;

    readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    readonly PropertyGrid _partGrid = new() { Dock = DockStyle.Fill, ToolbarVisible = false, HelpVisible = true };
    readonly PropertyGrid _physGrid = new() { Dock = DockStyle.Fill, ToolbarVisible = false, HelpVisible = true };
    readonly TextBox _templateInfo = new() { Dock = DockStyle.Bottom, Height = 110, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 8.5f) };
    // model
    readonly TextBox _obj = new() { Width = 320 };
    readonly TextBox _modelTemplate = new() { Width = 220 };
    readonly TextBox _maps = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 8.5f) };
    readonly PartPreview _preview = new() { Dock = DockStyle.Fill };
    // attach
    readonly ComboBox _footprint = new() { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox _rule = new() { Width = 130, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly AttachView _attach = new() { Dock = DockStyle.Fill };
    readonly Label _attachInfo = new() { AutoSize = true, Padding = new Padding(4, 6, 4, 0) };
    // build
    readonly TextBox _out = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9) };
    bool _loading;

    public PartImporterPanel()
    {
        // left: spec list + file buttons
        var left = new Panel { Dock = DockStyle.Left, Width = 230 };
        var fileBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight };
        Btn(fileBar, "Open…", OpenSpecs, "Open a parts JSON (e.g. ultra/parts.json)");
        Btn(fileBar, "Save", () => SaveSpecs(false));
        Btn(fileBar, "Save As…", () => SaveSpecs(true));
        Btn(fileBar, "New", NewSpec, "New part from a template part");
        Btn(fileBar, "Duplicate", DuplicateSpec);
        Btn(fileBar, "Delete", DeleteSpec, "Remove the spec from the list (does not touch the workspace; use Build → Remove part)");
        left.Controls.Add(_list); left.Controls.Add(fileBar);
        _list.SelectedIndexChanged += (_, _) => ShowSpec();

        // Part tab
        var tPart = new TabPage("Part");
        tPart.Controls.Add(_partGrid); tPart.Controls.Add(_templateInfo);
        _partGrid.PropertyValueChanged += (_, _) => { _list.Items[_list.SelectedIndex] = Label(Cur!); UpdateTemplateInfo(); };
        // Physics tab
        var tPhys = new TabPage("Physics / stats");
        tPhys.Controls.Add(_physGrid);
        var physNote = new Label { Dock = DockStyle.Top, Height = 58, Text = "Stats are written into the cloned record. Empty = keep the template's value (shown in the Part tab). Collision: the template part's convex hull, optionally scaled to the model or to the footprint (Attach points tab).", Padding = new Padding(4) };
        tPhys.Controls.Add(physNote);
        // Model tab
        var tModel = new TabPage("Model");
        var mBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        mBar.Controls.Add(new Label { Text = "OBJ/FBX:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }); mBar.Controls.Add(_obj);
        Btn(mBar, "…", BrowseObj);
        mBar.Controls.Add(new Label { Text = "Model template:", AutoSize = true, Padding = new Padding(8, 6, 0, 0) }); mBar.Controls.Add(_modelTemplate);
        Btn(mBar, "Apply", ApplyModelFields, "Apply the model file, template and the mapping text");
        Btn(mBar, "Suggest mapping", SuggestMapping, "List the template model's diffuse textures as mapping lines");
        var mSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, Panel1MinSize = 220 };
        mSplit.Panel1.Controls.Add(_maps);
        mSplit.Panel2.Controls.Add(_preview);
        var mapHelp = new Label { Dock = DockStyle.Top, Height = 36, Text = "Mapping lines: material <OBJ material> = <template texture>, retarget <template texture> = <new texture>, texture <new texture> = <png>, tint <texture> = r,g,b.  Preview: drag to orbit, wheel to zoom; boxes = footprint cells.", Padding = new Padding(4) };
        tModel.Controls.Add(mSplit); tModel.Controls.Add(mapHelp); tModel.Controls.Add(mBar);
        mSplit.SizeChanged += (_, _) => { if (mSplit.Width > 500) mSplit.SplitterDistance = Math.Max(220, Math.Min(460, mSplit.Width * 2 / 5)); };
        // Attach tab
        var tAttach = new TabPage("Attach points");
        var aBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        aBar.Controls.Add(new Label { Text = "Footprint from:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }); aBar.Controls.Add(_footprint);
        aBar.Controls.Add(new Label { Text = "Attachable:", AutoSize = true, Padding = new Padding(8, 6, 0, 0) }); aBar.Controls.Add(_rule);
        aBar.Controls.Add(_attachInfo);
        _rule.Items.AddRange(new object[] { "(template)", "all", "none", "bottom", "top", "sides", "bottom+sides", "custom" });
        _footprint.SelectedIndexChanged += (_, _) => { if (!_loading) ApplyAttach(); };
        _rule.SelectedIndexChanged += (_, _) => { if (!_loading && (string)_rule.SelectedItem! != "custom") ApplyAttach(); };
        _attach.FaceToggled += () => { if (Cur == null) return; EnsureAttach(); Cur.Attach!.Attachable = _attach.Rule(); _loading = true; _rule.SelectedItem = "custom"; _loading = false; UpdateAttachInfo(); };
        var aHelp = new Label { Dock = DockStyle.Top, Height = 30, Text = "Each view shows the outer faces on one side of the footprint (cells). Green = another part can attach there. Click a face to toggle it.", Padding = new Padding(4) };
        tAttach.Controls.Add(_attach); tAttach.Controls.Add(aHelp); tAttach.Controls.Add(aBar);
        // Build tab
        var tBuild = new TabPage("Validate / Build");
        var bBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        Btn(bBar, "Validate", () => RunValidate(Cur));
        Btn(bBar, "Validate all", () => { foreach (var s in _specs) RunValidate(s); });
        Btn(bBar, "Build into workspace", () => RunBuild(false), "Clone record/model/collision/attach data, import the model, register name, tier, description, inventory (PartFactory.Build)");
        Btn(bBar, "Build all", () => RunBuild(true));
        Btn(bBar, "Remove part from workspace", RunRemove, "Undo a build: remove the part's assets, inventory entries and tier (PartFactory.Uninstall)");
        tBuild.Controls.Add(_out); tBuild.Controls.Add(bBar);

        _tabs.TabPages.AddRange(new[] { tPart, tPhys, tModel, tAttach, tBuild });
        Controls.Add(_tabs); Controls.Add(left);
    }

    static void Btn(Control parent, string text, Action a, string? tip = null)
    {
        var b = new Button { Text = text, AutoSize = true };
        b.Click += (_, _) => { try { a(); } catch (Exception e) { MessageBox.Show(parent.FindForm(), e.Message, text); } };
        if (tip != null) new ToolTip().SetToolTip(b, tip);
        parent.Controls.Add(b);
    }

    public void SetWorkspace(Workspace ws, AssetIndex idx)
    {
        _ws = ws; _idx = idx;
        PartView.Templates = idx.Entries.Where(e => e.Name.StartsWith("aid_objparams_banjox_vehicleblock_") && !e.Streamed)
            .Select(e => e.Name["aid_objparams_banjox_vehicleblock_".Length..]).Distinct().OrderBy(x => x).ToArray();
        PartView.Groupings = idx.Entries.Where(e => e.Name.StartsWith("aid_misc_banjox_garagegrouping_") && !e.Streamed)
            .Select(e => e.Name["aid_misc_banjox_garagegrouping_".Length..]).Distinct().OrderBy(x => x).ToArray();
        _footprint.Items.Clear(); _footprint.Items.Add("(template)");
        foreach (var n in idx.Entries.Where(e => e.Name.StartsWith(PartFactory.AttachPrefix) && !e.Streamed).Select(e => e.Name).Distinct().OrderBy(x => x))
            _footprint.Items.Add(n[PartFactory.AttachPrefix.Length..]);
        if (_specs.Count > 0) ShowSpec();
    }

    // ------------------------------------------------------------------ specs

    static string Label(PartFactory.PartSpec p) => $"{p.Name}  ({p.Id})";

    public void LoadSpecs(string file)
    {
        _file = file;
        _specs.Clear(); _specs.AddRange(PartFactory.LoadSpecs(file));
        _list.Items.Clear(); foreach (var s in _specs) _list.Items.Add(Label(s));
        if (_specs.Count > 0) _list.SelectedIndex = 0;
        Log?.Invoke($"Part importer: {_specs.Count} part spec(s) from {file}");
    }

    void OpenSpecs()
    {
        using var o = new OpenFileDialog { Filter = "Part specs (*.json)|*.json" };
        if (o.ShowDialog(this) == DialogResult.OK) LoadSpecs(o.FileName);
    }

    void SaveSpecs(bool ask)
    {
        if (ask || _file == null)
        {
            using var s = new SaveFileDialog { Filter = "Part specs (*.json)|*.json", FileName = _file != null ? Path.GetFileName(_file) : "parts.json" };
            if (s.ShowDialog(this) != DialogResult.OK) return;
            _file = s.FileName;
        }
        var opt = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
        File.WriteAllText(_file!, JsonSerializer.Serialize(_specs, opt));
        Log?.Invoke($"Part importer: saved {_specs.Count} spec(s) to {_file}");
    }

    void NewSpec()
    {
        var p = new PartFactory.PartSpec { Id = "new_part", Key = "newpart", Name = "New Part", Template = "body_super_cube", Blocksets = new() { ["startpack"] = 5, ["all"] = 5 } };
        _specs.Add(p); _list.Items.Add(Label(p)); _list.SelectedIndex = _specs.Count - 1;
    }

    void DuplicateSpec()
    {
        if (Cur == null) return;
        var p = JsonSerializer.Deserialize<PartFactory.PartSpec>(JsonSerializer.Serialize(Cur))!;
        p.Id += "_copy"; p.Key += "copy"; p.Name += " (copy)";
        if (p.Model != null) p.Model.Id += "_copy";
        if (p.Attach != null) p.Attach.Id += "_copy";
        _specs.Add(p); _list.Items.Add(Label(p)); _list.SelectedIndex = _specs.Count - 1;
    }

    void DeleteSpec()
    {
        int i = _list.SelectedIndex; if (i < 0) return;
        _specs.RemoveAt(i); _list.Items.RemoveAt(i);
        if (_specs.Count > 0) _list.SelectedIndex = Math.Min(i, _specs.Count - 1);
    }

    void ShowSpec()
    {
        var p = Cur; if (p == null) return;
        _loading = true;
        _partGrid.SelectedObject = new PartView(p);
        _physGrid.SelectedObject = new StatsView(p);
        _obj.Text = p.Model?.Obj ?? ""; _modelTemplate.Text = p.Model?.Template ?? "";
        _maps.Text = MappingText(p);
        _footprint.SelectedItem = p.Attach?.Template ?? "(template)";
        _rule.SelectedItem = p.Attach?.Attachable switch { null => "(template)", var r when _rule.Items.Contains(r) => r, _ => "custom" };
        _loading = false;
        UpdateTemplateInfo(); UpdatePreview(); UpdateAttach();
    }

    // ------------------------------------------------------------------ template info / preview

    PartValidator.TemplateInfo? TemplateOf(PartFactory.PartSpec p) =>
        _ws == null || _idx == null ? null : PartValidator.Template(_ws, _idx, p.Template, p.Model?.Template);

    void UpdateTemplateInfo()
    {
        var p = Cur; if (p == null) return;
        try
        {
            var t = TemplateOf(p);
            if (t == null) { _templateInfo.Text = _ws == null ? "Open a workspace to see the template part." : $"Template '{p.Template}' not found."; return; }
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Template {t.Record} ({t.RecordSize} bytes). Model {t.Model}. Footprint {t.Attach?.ToString() ?? "?"}");
            string F(int o) => o + 4 <= t.Data.Length ? NB.Core.IO.BE.F32(t.Data, o).ToString("0.###", CultureInfo.InvariantCulture) : "-";
            string S(int o) => o + 64 <= t.Data.Length ? NB.Core.IO.BE.CStr(t.Data, o, 64) : "";
            sb.AppendLine($"category '{S(0x228)}', tier strings +0x268 '{S(0x268)}' / +0x2A8 '{S(0x2A8)}', material '{S(0x2E8)}'");
            sb.AppendLine($"weight +0x190 = {F(0x190)}, +0x194 = {F(0x194)}; engine power +0x3FC = {F(0x3FC)}, force +0x3D0 = {F(0x3D0)}; capacity +0x3C0 = {F(0x3C0)}; wheel spin +0x404 = {F(0x404)}, grip +0x3F8 = {F(0x3F8)}");
            sb.AppendLine("diffuse textures of the model template: " + string.Join(", ", t.DiffuseStems.Select(s => s.Replace("aid_texture_banjox_", ""))));
            _templateInfo.Text = sb.ToString();
        }
        catch (Exception e) { _templateInfo.Text = "Template: " + e.Message; }
    }

    void UpdatePreview()
    {
        var p = Cur;
        _preview.SetModel(null, null, null);
        if (p?.Model == null || string.IsNullOrEmpty(p.Model.Obj)) return;
        string path = Path.IsPathRooted(p.Model.Obj) ? p.Model.Obj : Path.Combine(BaseDir, p.Model.Obj);
        if (!File.Exists(path)) return;
        try
        {
            var meshes = ObjReader.ReadAny(path);
            // material → texture image (retargeted new texture if any) for colours
            var tex = new Dictionary<string, (byte[] Rgba, int W, int H)>();
            foreach (var m in meshes)
            {
                if (!p.Model.Materials.TryGetValue(m.Name, out var stem)) continue;
                if (p.Model.Retarget.TryGetValue(stem, out var nt) && p.Model.Textures.TryGetValue(nt, out var png))
                {
                    var pp = Path.IsPathRooted(png) ? png : Path.Combine(BaseDir, png);
                    if (File.Exists(pp)) try { tex[m.Name] = ImageIO.Load(pp); } catch { }
                }
            }
            _preview.SetModel(meshes, tex, FootprintOf(p));
        }
        catch (Exception e) { Log?.Invoke("Preview: " + e.Message); }
    }

    AttachData? FootprintOf(PartFactory.PartSpec p)
    {
        if (_ws == null || _idx == null) return null;
        string name = p.Attach != null ? PartFactory.AttachPrefix + p.Attach.Template : TemplateOf(p)?.AttachAsset ?? "";
        var e = _idx.Entries.FirstOrDefault(x => x.Name == name && !x.Streamed);
        if (e == null) return null;
        var c = _ws.LoadResident(e.Bundle);
        var a = AttachData.Parse(c.PartsOf(e.Symbol).First(x => c.SectionOf(x).Name == ".data").Data);
        if (p.Attach?.Attachable != null) try { a.SetAttachable(p.Attach.Attachable); } catch { }
        return a;
    }

    // ------------------------------------------------------------------ model mapping

    static string MappingText(PartFactory.PartSpec p)
    {
        if (p.Model == null) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var (k, v) in p.Model.Materials) sb.AppendLine($"material {k} = {v}");
        foreach (var (k, v) in p.Model.Retarget) sb.AppendLine($"retarget {k} = {v}");
        foreach (var (k, v) in p.Model.Textures) sb.AppendLine($"texture {k} = {v}");
        foreach (var (k, v) in p.Model.Tint) sb.AppendLine($"tint {k} = {v}");
        return sb.ToString();
    }

    void ApplyModelFields()
    {
        var p = Cur; if (p == null) return;
        p.Model ??= new PartFactory.ModelSpec { Id = p.Id };
        p.Model.Obj = _obj.Text.Trim();
        p.Model.Template = string.IsNullOrWhiteSpace(_modelTemplate.Text) ? null : _modelTemplate.Text.Trim();
        p.Model.Materials.Clear(); p.Model.Retarget.Clear(); p.Model.Textures.Clear(); p.Model.Tint.Clear();
        foreach (var raw in _maps.Lines)
        {
            var l = raw.Trim(); if (l.Length == 0 || l.StartsWith('#')) continue;
            int sp = l.IndexOf(' '), eq = l.IndexOf('=');
            if (sp < 0 || eq < sp) throw new FormatException("mapping line: " + l);
            string kind = l[..sp], k = l[sp..eq].Trim(), v = l[(eq + 1)..].Trim();
            var d = kind switch { "material" => p.Model.Materials, "retarget" => p.Model.Retarget, "texture" => p.Model.Textures, "tint" => p.Model.Tint, _ => throw new FormatException("unknown mapping kind " + kind) };
            d[k] = v;
        }
        UpdatePreview(); UpdateTemplateInfo();
        Log?.Invoke($"Part importer: model settings of {p.Id} applied");
    }

    void BrowseObj()
    {
        using var o = new OpenFileDialog { Filter = "Models (*.obj;*.fbx)|*.obj;*.fbx" };
        if (o.ShowDialog(this) != DialogResult.OK) return;
        var rel = Path.GetRelativePath(BaseDir, o.FileName);
        _obj.Text = rel.StartsWith("..") ? o.FileName : rel.Replace('\\', '/');
        ApplyModelFields();
    }

    void SuggestMapping()
    {
        var p = Cur; if (p == null) return;
        var t = TemplateOf(p); if (t == null) return;
        var sb = new System.Text.StringBuilder(_maps.Text);
        if (sb.Length > 0 && !_maps.Text.EndsWith("\n")) sb.AppendLine();
        sb.AppendLine("# diffuse textures of " + t.Model + ": map each OBJ material to one of them");
        foreach (var s in t.DiffuseStems) sb.AppendLine("# material <obj material> = " + s);
        _maps.Text = sb.ToString();
    }

    // ------------------------------------------------------------------ attach points

    void EnsureAttach()
    {
        var p = Cur!;
        if (p.Attach != null) return;
        var t = TemplateOf(p);
        string tpl = t?.AttachAsset.Length > PartFactory.AttachPrefix.Length ? t.AttachAsset[PartFactory.AttachPrefix.Length..] : p.Template;
        p.Attach = new PartFactory.AttachSpec { Template = tpl, Id = p.Id };
    }

    void ApplyAttach()
    {
        var p = Cur; if (p == null) return;
        var fpSel = (string?)_footprint.SelectedItem; var rule = (string?)_rule.SelectedItem;
        if (fpSel == "(template)" && rule == "(template)") p.Attach = null;
        else
        {
            EnsureAttach();
            if (fpSel != null && fpSel != "(template)") p.Attach!.Template = fpSel;
            p.Attach!.Attachable = rule is null or "(template)" or "custom" ? p.Attach.Attachable : rule;
        }
        UpdateAttach(); UpdatePreview();
    }

    void UpdateAttach()
    {
        var p = Cur; if (p == null) return;
        try { _attach.SetData(FootprintOf(p)); } catch (Exception e) { _attach.SetData(null); Log?.Invoke("Attach points: " + e.Message); }
        UpdateAttachInfo();
    }

    void UpdateAttachInfo() => _attachInfo.Text = _attach.Data?.ToString() ?? "(no footprint: open a workspace)";

    // ------------------------------------------------------------------ validate / build

    void Out(string s) { if (InvokeRequired) { BeginInvoke(() => Out(s)); return; } _out.AppendText(s + Environment.NewLine); Log?.Invoke(s); }

    bool RunValidate(PartFactory.PartSpec? p)
    {
        if (p == null || _ws == null || _idx == null) { Out("Open a workspace and a part spec first."); return false; }
        _tabs.SelectedIndex = 4;
        var r = PartValidator.Validate(_ws, _idx, p, BaseDir);
        Out($"== validate {p.Id}: {(r.Ok ? "OK" : $"{r.Errors.Count} error(s)")}, {r.Warnings.Count} warning(s)");
        foreach (var l in r.Lines) Out("  " + l);
        return r.Ok;
    }

    public async void RunBuild(bool all) => await BuildAsync(all);

    public async Task BuildAsync(bool all)
    {
        if (_ws == null || _idx == null) { Out("Open a workspace first."); return; }
        var list = all ? _specs.ToList() : Cur != null ? new List<PartFactory.PartSpec> { Cur } : new();
        foreach (var p in list) if (!RunValidate(p)) { Out($"Build stopped: {p.Id} has errors."); return; }
        var ws = _ws; var idx = _idx; string dir = BaseDir;
        Out("Building (close Xenia first: it keeps the bundles open)…");
        try
        {
            await Task.Run(() =>
            {
                foreach (var p in list)
                {
                    Out($"== build {p.Id} ({p.Name})");
                    foreach (var l in PartFactory.Build(ws, idx, p, dir)) Out("  " + l);
                }
                Out("reindexing…");
                _idx = AssetIndex.LoadOrBuild(ws, null, true);
            });
            Out("Build finished. Test: boot the workspace in Xenia; the part is in the start pack / garage.");
        }
        catch (Exception e) { Out("BUILD FAILED: " + e.Message); }
    }

    async void RunRemove() => await RemoveAsync(true);

    public async Task RemoveAsync(bool confirm)
    {
        var p = Cur; if (p == null || _ws == null || _idx == null) return;
        if (confirm && MessageBox.Show(this, $"Remove '{p.Name}' ({p.Id}) from the workspace?\n\nIts record, model, collision, attach data and description are deleted and it is taken out of every blockset, the network list and its tier. Blueprints that use it must be changed separately.", "Remove part", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        var ws = _ws; var idx = _idx;
        try
        {
            await Task.Run(() =>
            {
                Out($"== remove {p.Id}");
                foreach (var l in PartFactory.Uninstall(ws, idx, p)) Out("  " + l);
                _idx = AssetIndex.LoadOrBuild(ws, null, true);
            });
        }
        catch (Exception e) { Out("REMOVE FAILED: " + e.Message); }
    }

    // scripted test hooks (MainForm --parts …)
    public void SelectPart(string id) { int i = _specs.FindIndex(s => s.Id == id); if (i >= 0) _list.SelectedIndex = i; }
    public void SelectTab(string name) { foreach (TabPage t in _tabs.TabPages) if (t.Text.StartsWith(name, StringComparison.OrdinalIgnoreCase)) _tabs.SelectedTab = t; }
    public bool ValidateCurrent() => RunValidate(Cur);
    public void Orbit(int dx, int dy) => _preview.Orbit(dx, dy);
    public string OutputText => _out.Text;

    // ------------------------------------------------------------------ property views

    public sealed class CollisionConverter : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext? c) => true;
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext? c) => true;
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? c) => new(new[] { "(template)", "model", "footprint" });
    }

    public sealed class ListConverter : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext? c) => true;
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext? c) => false;
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? c) =>
            new(c?.PropertyDescriptor?.Name == nameof(PartView.Grouping) ? PartView.Groupings : PartView.Templates);
    }

    sealed class PartView
    {
        public static string[] Templates = Array.Empty<string>(), Groupings = Array.Empty<string>();
        readonly PartFactory.PartSpec _p;
        public PartView(PartFactory.PartSpec p) { _p = p; }
        [Category("1 Identity"), Description("New part id: aid_objparams_banjox_vehicleblock_<id>.")] public string Id { get => _p.Id; set => _p.Id = value; }
        [Category("1 Identity"), Description("Existing part to clone (class, stats, collision, footprint)."), TypeConverter(typeof(ListConverter))] public string Template { get => _p.Template; set => _p.Template = value; }
        [Category("1 Identity"), Description("Part key (+0xA0); the name is looked up as block__<key>.")] public string Key { get => _p.Key; set => _p.Key = value; }
        [Category("1 Identity"), Description("Name shown in the garage.")] public string Name { get => _p.Name; set => _p.Name = value; }
        [Category("1 Identity"), Description("Garage description spoken by Mumbo (own dialog). Empty keeps the template's.")] public string? Dialog { get => _p.Dialog; set => _p.Dialog = string.IsNullOrWhiteSpace(value) ? null : value; }
        [Category("1 Identity"), Description("Developer description (+0xE0, max 63 characters).")] public string? DevDescription { get => _p.Description; set => _p.Description = value; }
        [Category("1 Identity"), Description("Paint colour key (+0x130), e.g. colour_orange.")] public string? Colour { get => _p.Colour; set => _p.Colour = string.IsNullOrWhiteSpace(value) ? null : value; }
        [Category("2 Garage"), Description("Tier / size string (e.g. ultra). The parts store lists one entry per tier.")] public string? Tier { get => _p.Tier; set => _p.Tier = value; }
        [Category("2 Garage"), Description("Text shown for the tier (block__group_<tier>).")] public string? TierLabel { get => _p.TierLabel; set => _p.TierLabel = value; }
        [Category("2 Garage"), Description("Offset of the tier string in the record: 2A8 engines, 268 fuel/ammo/wheels/body/accessories.")] public string TierOffset { get => _p.TierOffset; set => _p.TierOffset = value; }
        [Category("2 Garage"), Description("Tier registry: aid_misc_banjox_garagegrouping_<x>."), TypeConverter(typeof(ListConverter))] public string? Grouping { get => _p.Grouping; set => _p.Grouping = value; }
        [Category("2 Garage"), Description("Tier record to copy (e.g. super).")] public string? GroupingCopyFrom { get => _p.GroupingCopyFrom; set => _p.GroupingCopyFrom = value; }
        [Category("3 Inventory"), Description("blockset=count; … (e.g. startpack=10; all=10).")]
        public string Blocksets
        {
            get => string.Join("; ", _p.Blocksets.Select(kv => $"{kv.Key}={kv.Value}"));
            set { _p.Blocksets = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s.Split('=')).ToDictionary(a => a[0].Trim(), a => int.Parse(a[1].Trim())); }
        }
        [Category("3 Inventory"), Description("Also list the part for online play (network list).")] public bool NetworkEnum { get => _p.NetworkEnum; set => _p.NetworkEnum = value; }
    }

    sealed class StatsView
    {
        readonly PartFactory.PartSpec _p;
        public StatsView(PartFactory.PartSpec p) { _p = p; }
        float? GetF(string off) => _p.Set.TryGetValue(off, out var v) && v.StartsWith("f:") ? float.Parse(v[2..], CultureInfo.InvariantCulture) : null;
        void SetF(string off, float? v) { if (v == null) _p.Set.Remove(off); else _p.Set[off] = "f:" + v.Value.ToString(CultureInfo.InvariantCulture); }
        [Category("All parts"), Description("+0x190 weight (light block 10, heavy 40).")] public float? Weight { get => GetF("190"); set => SetF("190", value); }
        [Category("All parts"), Description("+0x194 toughness (light block 5, super 20).")] public float? Toughness { get => GetF("194"); set => SetF("194", value); }
        [Category("All parts"), Description("+0x2E8 impact material (metal_light, metal_heavy, solid, …).")]
        public string? Material { get => _p.Set.TryGetValue("2E8", out var v) && v.StartsWith("s:") ? v[2..] : null; set { if (string.IsNullOrWhiteSpace(value)) _p.Set.Remove("2E8"); else _p.Set["2E8"] = "s:" + value; } }
        [Category("Engine"), Description("+0x3FC power (small 40, super 160; ULTRA 800).")] public float? EnginePower { get => GetF("3FC"); set => SetF("3FC", value); }
        [Category("Engine"), Description("+0x3D0 force (small 200, super 450; ULTRA 1400).")] public float? EngineForce { get => GetF("3D0"); set => SetF("3D0", value); }
        [Category("Fuel / ammo"), Description("+0x3C0 and +0x3C4 capacity (super fuel 1460, super ammo 1600).")] public float? Capacity { get => GetF("3C0"); set { SetF("3C0", value); SetF("3C4", value); } }
        [Category("Wheels"), Description("+0x404 maximum wheel spin; top speed ≈ spin × radius (+0x400) × 2.4 units/s (standard 60 → 72 u/s).")] public float? WheelMaxSpin { get => GetF("404"); set => SetF("404", value); }
        [Category("Wheels"), Description("+0x3F8 grip (standard 1.7, super 4.3).")] public float? WheelGrip { get => GetF("3F8"); set => SetF("3F8", value); }
        [Category("Collision"), Description("Collision shape: (template) = the template part's convex hull as is; model = that hull scaled to the imported model's bounds; footprint = scaled to the part's cells (use for parts bigger than their template, e.g. a 3×3 part)."), TypeConverter(typeof(CollisionConverter))]
        public string Collision
        {
            get => _p.Model?.CollisionFit ?? "(template)";
            set { if (_p.Model != null) _p.Model.CollisionFit = value is "model" or "footprint" ? value : null; }
        }
        [Category("Raw"), Description("All field edits: offset(hex)=f:/u:/h:/s: value; …")]
        public string Fields
        {
            get => string.Join("; ", _p.Set.Select(kv => $"{kv.Key}={kv.Value}"));
            set { _p.Set = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s.Split('=', 2)).ToDictionary(a => a[0].Trim(), a => a[1].Trim()); }
        }
    }
}

/// <summary>Orbit preview of an imported part (software renderer) with the footprint cells as wire boxes.</summary>
public sealed class PartPreview : Control
{
    List<ImportMesh>? _meshes; Dictionary<string, (byte[] Rgba, int W, int H)>? _tex; AttachData? _fp;
    float _yaw = 0.7f, _pitch = 0.45f, _zoom = 1f; Point _last; bool _drag;
    static readonly Dictionary<string, Color> RoleColours = new()
    {
        ["metal"] = Color.FromArgb(150, 160, 175), ["paint"] = Color.FromArgb(230, 140, 40), ["glow"] = Color.FromArgb(90, 210, 255),
        ["glass"] = Color.FromArgb(170, 200, 230), ["panel"] = Color.FromArgb(240, 200, 40), ["plain"] = Color.FromArgb(60, 62, 70),
    };

    public PartPreview() { DoubleBuffered = true; BackColor = Color.FromArgb(36, 38, 44); }

    public void SetModel(List<ImportMesh>? m, Dictionary<string, (byte[], int, int)>? tex, AttachData? fp) { _meshes = m; _tex = tex; _fp = fp; Invalidate(); }
    public void Orbit(int dx, int dy) { _yaw += dx * 0.01f; _pitch = Math.Clamp(_pitch + dy * 0.01f, -1.5f, 1.5f); Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e) { _drag = true; _last = e.Location; }
    protected override void OnMouseUp(MouseEventArgs e) => _drag = false;
    protected override void OnMouseMove(MouseEventArgs e) { if (!_drag) return; Orbit(e.X - _last.X, e.Y - _last.Y); _last = e.Location; }
    protected override void OnMouseWheel(MouseEventArgs e) { _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.1f : 0.9f), 0.2f, 8f); Invalidate(); }

    Color MatColour(ImportMesh m, int tri)
    {
        if (_tex != null && _tex.TryGetValue(m.Name, out var t) && m.UVs != null && m.UVs.Count == m.Positions.Count)
        {
            var uv = (m.UVs[m.Triangles[tri]] + m.UVs[m.Triangles[tri + 1]] + m.UVs[m.Triangles[tri + 2]]) / 3;
            int x = (int)(((uv.X % 1) + 1) % 1 * t.W), y = (int)((1 - ((uv.Y % 1) + 1) % 1) * t.H);
            int o = (Math.Clamp(y, 0, t.H - 1) * t.W + Math.Clamp(x, 0, t.W - 1)) * 4;
            return Color.FromArgb(t.Rgba[o], t.Rgba[o + 1], t.Rgba[o + 2]);
        }
        return RoleColours.TryGetValue(m.Name, out var c) ? c : Color.FromArgb(180, 180, 170);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        if (_meshes == null || _meshes.Count == 0) { g.DrawString("No model (set an OBJ in the Model tab)", Font, Brushes.Gray, 10, 10); return; }
        var rot = Matrix4x4.CreateRotationY(_yaw) * Matrix4x4.CreateRotationX(_pitch);
        float ext = 0.6f;
        if (_fp != null) ext = MathF.Max(ext, 0.6f + MathF.Max(MathF.Max(_fp.Size.X, _fp.Size.Y), _fp.Size.Z) * 0.5f);
        float s = MathF.Min(Width, Height) * 0.45f / ext * _zoom;
        PointF Pj(Vector3 v) { var p = Vector3.Transform(v, rot); return new PointF(Width / 2f + p.X * s, Height / 2f - p.Y * s); }
        var light = Vector3.Normalize(new Vector3(0.4f, 0.8f, 0.5f));
        var tris = new List<(PointF[] P, float Z, Color C)>();
        foreach (var m in _meshes)
            for (int k = 0; k + 2 < m.Triangles.Count; k += 3)
            {
                var a = m.Positions[m.Triangles[k]]; var b = m.Positions[m.Triangles[k + 1]]; var c = m.Positions[m.Triangles[k + 2]];
                var n = Vector3.Cross(b - a, c - a); if (n.LengthSquared() < 1e-12f) continue;
                n = Vector3.Normalize(Vector3.TransformNormal(n, rot));
                float sh = 0.35f + 0.65f * MathF.Abs(Vector3.Dot(n, Vector3.TransformNormal(light, rot)));
                var col = MatColour(m, k);
                var z = Vector3.Transform((a + b + c) / 3, rot).Z;
                tris.Add((new[] { Pj(a), Pj(b), Pj(c) }, z, Color.FromArgb((int)(col.R * sh), (int)(col.G * sh), (int)(col.B * sh))));
            }
        foreach (var t in tris.OrderBy(t => t.Z)) using (var br = new SolidBrush(t.C)) g.FillPolygon(br, t.P);
        if (_fp != null)
        {
            using var pen = new Pen(Color.FromArgb(160, 255, 255, 255), 1) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
            for (int x = _fp.XMin; x <= _fp.XMax; x++)
                for (int y = _fp.YMin; y <= _fp.YMax; y++)
                    for (int z = _fp.ZMin; z <= _fp.ZMax; z++)
                    {
                        var c0 = new Vector3(x, y, z);
                        Vector3 V(int i) => c0 + new Vector3((i & 1) != 0 ? 0.5f : -0.5f, (i & 2) != 0 ? 0.5f : -0.5f, (i & 4) != 0 ? 0.5f : -0.5f);
                        foreach (var (i, j) in new[] { (0, 1), (2, 3), (4, 5), (6, 7), (0, 2), (1, 3), (4, 6), (5, 7), (0, 4), (1, 5), (2, 6), (3, 7) })
                            g.DrawLine(pen, Pj(V(i)), Pj(V(j)));
                    }
            using var ap = new SolidBrush(Color.FromArgb(200, 80, 230, 110)); using var np = new SolidBrush(Color.FromArgb(120, 230, 80, 80));
            foreach (var p in _fp.Points) { var q = Pj(p.Position); g.FillEllipse(p.Attachable ? ap : np, q.X - 3, q.Y - 3, 6, 6); }
        }
        // axis gizmo
        var o = new PointF(40, Height - 40);
        foreach (var (v, c, n) in new[] { (Vector3.UnitX, Color.Red, "X"), (Vector3.UnitY, Color.LimeGreen, "Y"), (Vector3.UnitZ, Color.DeepSkyBlue, "Z front") })
        {
            var p = Vector3.Transform(v, rot); var q = new PointF(o.X + p.X * 25, o.Y - p.Y * 25);
            using var pen = new Pen(c, 2); g.DrawLine(pen, o, q); g.DrawString(n, Font, new SolidBrush(c), q);
        }
        int nt = _meshes.Sum(m => m.Triangles.Count / 3);
        g.DrawString($"{nt} triangles{(_fp != null ? $", footprint {_fp.Size.X}x{_fp.Size.Y}x{_fp.Size.Z}" : "")} — drag to orbit, wheel to zoom", Font, Brushes.Silver, 8, 8);
    }
}

/// <summary>Attach-point editor: six views (one per side) of the footprint's outer faces; click toggles a face.</summary>
public sealed class AttachView : Control
{
    public AttachData? Data { get; private set; }
    public event Action? FaceToggled;
    readonly List<(RectangleF R, AttachData.Point P)> _hits = new();
    static readonly (Vector3 N, string Name)[] Sides =
    {
        (Vector3.UnitY, "Top (+Y)"), (-Vector3.UnitY, "Bottom (−Y)"), (Vector3.UnitZ, "Front (+Z)"),
        (-Vector3.UnitZ, "Back (−Z)"), (Vector3.UnitX, "Left (+X)"), (-Vector3.UnitX, "Right (−X)"),
    };

    public AttachView() { DoubleBuffered = true; BackColor = Color.FromArgb(36, 38, 44); ForeColor = Color.Gainsboro; }
    public void SetData(AttachData? a) { Data = a; Invalidate(); }

    /// <summary>The attachable faces as a rule string ("all", "none", or a face list).</summary>
    public string Rule()
    {
        if (Data == null) return "all";
        if (Data.Points.All(p => p.Attachable)) return "all";
        if (Data.Points.All(p => !p.Attachable)) return "none";
        return string.Join(";", Data.Points.Where(p => p.Attachable).Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Position.X:0.#},{p.Position.Y:0.#},{p.Position.Z:0.#}")));
    }

    static (float U, float V) Plane(Vector3 n, Vector3 p) =>
        n.Y != 0 ? (p.X, n.Y > 0 ? -p.Z : p.Z) : n.Z != 0 ? (n.Z > 0 ? -p.X : p.X, -p.Y) : (n.X > 0 ? p.Z : -p.Z, -p.Y);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(BackColor); _hits.Clear();
        if (Data == null) { g.DrawString("No footprint", Font, Brushes.Gray, 10, 10); return; }
        int cols = 3, rows = 2; float cw = Width / (float)cols, ch = Height / (float)rows;
        for (int i = 0; i < Sides.Length; i++)
        {
            var (n, name) = Sides[i];
            var area = new RectangleF(i % cols * cw, i / cols * ch, cw, ch);
            g.DrawString(name, Font, Brushes.Silver, area.X + 6, area.Y + 4);
            var faces = Data.Points.Where(p => Vector3.Dot(p.Normal, n) > 0.5f).ToList();
            if (faces.Count == 0) continue;
            var uv = faces.Select(f => Plane(n, f.Position)).ToList();
            float u0 = uv.Min(t => t.U) - 0.5f, u1 = uv.Max(t => t.U) + 0.5f, v0 = uv.Min(t => t.V) - 0.5f, v1 = uv.Max(t => t.V) + 0.5f;
            float cell = MathF.Min((area.Width - 20) / (u1 - u0), (area.Height - 36) / (v1 - v0));
            cell = MathF.Min(cell, 60);
            float ox = area.X + (area.Width - cell * (u1 - u0)) / 2, oy = area.Y + 24 + (area.Height - 28 - cell * (v1 - v0)) / 2;
            for (int k = 0; k < faces.Count; k++)
            {
                var r = new RectangleF(ox + (uv[k].U - 0.5f - u0) * cell + 2, oy + (uv[k].V - 0.5f - v0) * cell + 2, cell - 4, cell - 4);
                using var br = new SolidBrush(faces[k].Attachable ? Color.FromArgb(70, 190, 100) : Color.FromArgb(150, 60, 60));
                g.FillRectangle(br, r); g.DrawRectangle(Pens.Black, r.X, r.Y, r.Width, r.Height);
                _hits.Add((r, faces[k]));
            }
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        foreach (var (r, p) in _hits)
            if (r.Contains(e.Location)) { p.Attachable = !p.Attachable; Invalidate(); FaceToggled?.Invoke(); return; }
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }
}
