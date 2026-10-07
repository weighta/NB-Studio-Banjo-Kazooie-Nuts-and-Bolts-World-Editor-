using System.Globalization;
using NB.Core.Formats;
using NB.Core.IO;
using NB.Core.Project;
using NB.Core.Tags;
using NB.Core.Textures;
using NB.Core.World;

namespace NB.Studio.Panels;

/// <summary>
/// Field-level editor for an asset's CPU (.data) part. Every 4-byte word is shown with its offset, a label
/// when the field is understood, and its value as u32 / s32 / float / hex. Pointers (known from the CAFF
/// relocation table) are shown with their target and are read-only, so edits can never break references.
/// Edits are same-size writes into the loaded bundle; "Save to Workspace" writes the bundle.
/// Fields without a documented meaning are labelled "unknown" and left exactly as they are unless edited.
/// </summary>
public sealed class TagEditorPanel : UserControl
{
    readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        Font = new Font("Consolas", 9),
    };
    readonly Label _head = new() { Dock = DockStyle.Top, Height = 48, AutoEllipsis = true };
    readonly TextBox _goto = new() { Width = 90, PlaceholderText = "offset (hex)" };
    readonly Button _save = new() { Text = "Save to Workspace", Width = 130, Enabled = false };
    readonly Button _prev = new() { Text = "◀", Width = 30 }, _next = new() { Text = "▶", Width = 30 };
    readonly CheckBox _structured = new() { Text = "Structured", Checked = true, AutoSize = true, Padding = new Padding(6, 6, 0, 0) };
    readonly ContextMenuStrip _values = new();
    /// <summary>Asset index of the open workspace (names for asset references, objparams schemas).</summary>
    public AssetIndex? Index;
    ObjClassSchema? _schema;
    Dictionary<uint, string>? _names;
    static Dictionary<string, ObjClassSchema>? s_schemas; static string? s_schemaRoot;
    public Action<string>? Log;
    public event Action? Changed;
    public bool HasUnsaved { get; private set; }

    Workspace? _ws; uint _bundle; CaffFile? _caff; AssetView? _view; byte[]? _data; string _section = ".data";
    int _base, _pageStart; const int PageWords = 1024;
    Dictionary<int, string> _labels = new();

    public TagEditorPanel()
    {
        _grid.Columns.Add("off", "Offset"); _grid.Columns.Add("field", "Field"); _grid.Columns.Add("type", "Type");
        _grid.Columns.Add("val", "Value"); _grid.Columns.Add("note", "Note");
        _grid.Columns[0].FillWeight = 14; _grid.Columns[1].FillWeight = 28; _grid.Columns[2].FillWeight = 12; _grid.Columns[3].FillWeight = 26; _grid.Columns[4].FillWeight = 30;
        _grid.Columns[0].ReadOnly = _grid.Columns[1].ReadOnly = _grid.Columns[4].ReadOnly = true;
        _grid.CellEndEdit += OnEdit;
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex == 2) CycleType(e.RowIndex); };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32 };
        bar.Controls.AddRange(new Control[] { _prev, _next, _goto, _save, _structured });
        _structured.CheckedChanged += (_, _) => ShowPage(_structured.Checked ? 0 : _pageStart);
        _grid.CellMouseClick += (_, e) => { if (e.Button == MouseButtons.Right && e.RowIndex >= 0) ShowValueMenu(e.RowIndex); };
        _goto.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter && int.TryParse(_goto.Text.Replace("0x", ""), NumberStyles.HexNumber, null, out int o)) ShowPage(o - o % 4); };
        _prev.Click += (_, _) => ShowPage(Math.Max(_base, _pageStart - PageWords * 4));
        _next.Click += (_, _) => { if (_data != null && _pageStart + PageWords * 4 < _data.Length) ShowPage(_pageStart + PageWords * 4); };
        _save.Click += (_, _) => Save();
        Controls.Add(_grid); Controls.Add(bar); Controls.Add(_head);
        var tip = new ToolTip(); tip.SetToolTip(_grid, "Double-click the Type cell to switch between u32 / s32 / float / hex. Pointers are read-only.");
    }

    public void ShowObject(WorldScene? scene, SceneObject? o)
    {
        if (scene == null || o == null) { Clear("No selection"); return; }
        _ws = scene.Workspace; _bundle = scene.Bundle; _caff = scene.Caff; _view = scene.Background.View; _section = ".data"; _schema = null;
        _data = _view.Data(".data");
        _labels = new();
        if (o.Instance is { } inst)
        {
            _base = inst.RecordOffset;
            var r = inst.RecordOffset;
            _labels[r] = "reference model index";
            _labels[r + 0x3C] = "name (string)";
            for (int k = 0; k < 16; k++) _labels[inst.MatrixOffset + 4 * k] = $"world matrix m{k / 4 + 1}{k % 4 + 1}";
            _labels[inst.PositionOffset] = "position x"; _labels[inst.PositionOffset + 4] = "position y"; _labels[inst.PositionOffset + 8] = "position z";
            _head.Text = $"{o.Name} — scene record in {AssetIds.DisplayName(_view.Name)} .data (bundle {_bundle:x6})\nRecord at 0x{r:X}; matrix at 0x{inst.MatrixOffset:X} (jump with the offset box). Unlabelled words are unknown.";
            ShowPage(r - r % 4);
        }
        else
        {
            _base = 0;
            LabelModelChunks(scene.Background);
            _head.Text = $"{o.Name} — {AssetIds.DisplayName(_view.Name)} .data ({_data.Length:N0} bytes)";
            ShowPage(0);
        }
    }

    public void ShowAsset(Workspace ws, AssetEntry e)
    {
        if (e.Streamed) { Clear($"{e.Name}\nStreamed asset (Bundle/50): open its bundle's resident copy or use Export in the preview."); return; }
        try
        {
            _ws = ws; _bundle = e.Bundle; _caff = ws.LoadResident(e.Bundle);
            _view = new AssetView(_caff, e.Symbol);
            _section = _view.Has(".data") ? ".data" : _view.PartBySection.Keys.First();
            _data = _view.Data(_section);
            _labels = new(); _base = 0; _schema = null;
            if (e.Type == "objparams" && ObjParamsSchema.IsObjParams(_data)) _schema = SchemaFor(ws, ObjParamsSchema.ClassOf(_data));
            else if (e.Type == "vehicle") LabelVehicle();
            else if (e.Type == "script" && LightSetup.FindFog(_data) is int fog && fog >= 0) LabelLightSetup(fog);
            else if (e.Type == "gpuparticleeffect" && _data.Length >= 0x184) LabelParticle();
            if (TextureHeader.IsTexture(_data))
            {
                _labels[0] = "tag \"texture\""; _labels[8] = "version string"; _labels[0x18] = "D3DFORMAT (format|endian<<6|tiled<<8)";
                _labels[0x24] = "width<<16 | height"; _labels[0x28] = "ptr → GPU data"; _labels[0x2C] = "base present (FFFFFFFF) / mips only (0)";
                _labels[0x30] = "level count (top byte)"; _labels[0x34] = "ptr → runtime D3D object";
            }
            else if (e.Type == "model") { try { LabelModelChunks(NB.Core.Models.ModelAsset.Parse(_caff, e.Symbol, geometry: false)); } catch { } }
            _head.Text = $"{e.Name} ({e.Type}) — bundle {e.Bundle:x6}, part {_section} ({_data.Length:N0} bytes)\n" + (_schema != null
                ? $"{_schema.Class}: layout inferred from {_schema.Instances} assets of this class. Named fields are verified; others are labelled by type. Right-click a text/reference field for known values."
                : "Pointer rows are read-only; unlabelled rows are unknown fields.");
            ShowPage(0);
        }
        catch (Exception ex) { Clear("Cannot open: " + ex.Message); }
    }

    void LabelModelChunks(NB.Core.Models.ModelAsset m)
    {
        _labels[0] = "ptr → chunk table"; _labels[4] = "chunk count";
        foreach (var (id, off) in m.Chunks) _labels[off] = $"chunk {id} start" + id switch { 2 => " (nodes: count, then 68-byte nodes)", 12 => " (scene instances header)", 10 => " (render data)", 5 => " (bounds)", _ => "" };
    }

    void Clear(string msg) { _head.Text = msg; _grid.Rows.Clear(); _data = null; }

    readonly Dictionary<int, string> _typeOverride = new();

    void ShowPage(int start)
    {
        if (_data == null || _view == null) return;
        _pageStart = Math.Clamp(start, 0, Math.Max(0, _data.Length - 4));
        _grid.Rows.Clear();
        if (_schema != null && _structured.Checked) { ShowFields(); return; }
        var rows = new List<DataGridViewRow>();
        for (int o = _pageStart; o + 4 <= _data.Length && o < _pageStart + PageWords * 4; o += 4)
        {
            var ptr = _view.PtrAt(_section, o);
            string type, val, note = "";
            if (ptr is { } p)
            {
                type = "ptr";
                string tsec = _view.IsOwnPart(p.Part) ? _view.SectionOfPart(p.Part) : $"{AssetIds.DisplayName(_caff!.Symbols[_view.Part(p.Part).Symbol - 1])}{_view.SectionOfPart(p.Part)}";
                val = $"→ {tsec}+0x{p.Offset:X}";
                if (tsec == ".data" && p.Offset >= 0 && p.Offset < _data.Length && _data[p.Offset] >= 0x20 && _data[p.Offset] < 0x7F) note = "\"" + BE.CStr(_data, p.Offset, 60) + "\"";
            }
            else
            {
                type = _typeOverride.GetValueOrDefault(o) ?? Guess(o);
                val = Format(o, type);
                if (_data[o] >= 0x20 && _data[o] < 0x7F && _data[o + 1] >= 0x20 && _data[o + 1] < 0x7F && _data[o + 2] >= 0x20) note = "text: " + BE.CStr(_data, o, 40);
            }
            var row = new DataGridViewRow();
            row.CreateCells(_grid, $"0x{o:X}", _labels.GetValueOrDefault(o, "unknown"), type, val, note);
            row.Tag = o;
            if (type == "ptr") { row.ReadOnly = true; row.DefaultCellStyle.ForeColor = Color.SteelBlue; }
            if (_labels.ContainsKey(o)) row.DefaultCellStyle.BackColor = Color.FromArgb(240, 248, 255);
            rows.Add(row);
        }
        _grid.Rows.AddRange(rows.ToArray());
    }

    string Guess(int o)
    {
        uint v = BE.U32(_data!, o);
        float f = BE.F32(_data!, o);
        if (v == 0) return "u32";
        if (float.IsFinite(f) && MathF.Abs(f) > 1e-5f && MathF.Abs(f) < 1e7f) return "float";
        return v < 0x10000 || v == 0xFFFFFFFF ? "u32" : "hex";
    }

    string Format(int o, string type) => type switch
    {
        "float" => BE.F32(_data!, o).ToString("R", CultureInfo.InvariantCulture),
        "s32" => BE.S32(_data!, o).ToString(CultureInfo.InvariantCulture),
        "hex" => "0x" + BE.U32(_data!, o).ToString("X8"),
        _ => BE.U32(_data!, o).ToString(CultureInfo.InvariantCulture),
    };

    void CycleType(int row)
    {
        var r = _grid.Rows[row];
        if (r.ReadOnly || r.Tag is not int o) return;
        string cur = (string)r.Cells[2].Value!;
        string next = cur switch { "u32" => "s32", "s32" => "float", "float" => "hex", _ => "u32" };
        _typeOverride[o] = next;
        r.Cells[2].Value = next; r.Cells[3].Value = Format(o, next);
    }

    void OnEdit(object? s, DataGridViewCellEventArgs e)
    {
        if (e.ColumnIndex != 3 || _data == null) return;
        var r = _grid.Rows[e.RowIndex];
        if (r.Tag is ObjField field) { EditField(r, field, Convert.ToString(r.Cells[3].Value, CultureInfo.InvariantCulture)?.Trim() ?? ""); return; }
        int o = (int)r.Tag!;
        string type = (string)r.Cells[2].Value!;
        string txt = Convert.ToString(r.Cells[3].Value, CultureInfo.InvariantCulture)?.Trim() ?? "";
        uint before = BE.U32(_data, o);
        try
        {
            switch (type)
            {
                case "float":
                    float f = float.Parse(txt, CultureInfo.InvariantCulture);
                    if (!float.IsFinite(f)) throw new FormatException("not a finite number");
                    BE.WF32(_data, o, f); break;
                case "s32": BE.W32(_data, o, int.Parse(txt, CultureInfo.InvariantCulture)); break;
                case "hex": BE.W32(_data, o, Convert.ToUInt32(txt.Replace("0x", "").Replace("0X", ""), 16)); break;
                default: BE.W32(_data, o, uint.Parse(txt, CultureInfo.InvariantCulture)); break;
            }
            if (BE.U32(_data, o) != before)
            {
                HasUnsaved = true; _save.Enabled = true;
                r.DefaultCellStyle.BackColor = Color.FromArgb(220, 255, 220);
                Log?.Invoke($"Tag edit: {_view!.Name} {_section}+0x{o:X}: 0x{before:X8} → 0x{BE.U32(_data, o):X8} (unsaved)");
                Changed?.Invoke();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Invalid value for {type}: {ex.Message}", "Tag Editor");
        }
        r.Cells[3].Value = Format(o, type);
    }

    // ---------------- structured (schema) view for objparams ----------------

    Dictionary<string, ObjClassSchema>? Schemas(Workspace ws)
    {
        if (Index == null) return null;
        if (s_schemas == null || s_schemaRoot != ws.Root) { s_schemas = ObjParamsSchema.LoadOrBuild(ws, Index); s_schemaRoot = ws.Root; }
        return s_schemas;
    }

    ObjClassSchema? SchemaFor(Workspace ws, string cls)
    {
        try
        {
            _names ??= Index?.Entries.Where(x => x.Id != 0).GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().Name);
            return Schemas(ws)?.GetValueOrDefault(cls);
        }
        catch (Exception ex) { Log?.Invoke("objparams schema unavailable: " + ex.Message); return null; }
    }

    static string FieldLabel(ObjField f)
    {
        if (f.Name != null) return f.Name;
        if (f.Offset == 0 && f.Kind == "u16") return "struct size";
        if (f.Offset == ObjParamsSchema.TagOffset) return "object tag";
        if (f.Offset == ObjParamsSchema.ClassOffset) return "class (objDefId)";
        if (f.Kind == "str" && f.Samples.Count > 0)
        {
            var s = f.Samples.FirstOrDefault(x => x.Length > 0) ?? "";
            int u = s.IndexOf("Id_", StringComparison.Ordinal);
            if (u > 0) return s[..u] + " id";
            if (s.StartsWith("gameFlag_")) return "game flag";
            if (s.StartsWith("colour_")) return "colour";
            return "text";
        }
        return (f.Kind switch { "assetref" => "asset reference", "ptr" => "pointer", _ => f.Kind }) + (f.Varies ? "" : " (same in all)");
    }

    string FieldValue(ObjField f) => f.Kind switch
    {
        "str" => BE.CStr(_data!, f.Offset, f.Size),
        "u16" => BE.U16(_data!, f.Offset).ToString(CultureInfo.InvariantCulture),
        "float" => BE.F32(_data!, f.Offset).ToString("R", CultureInfo.InvariantCulture),
        "int" => BE.U32(_data!, f.Offset).ToString(CultureInfo.InvariantCulture),
        "ptr" => _view!.PtrAt(_section, f.Offset) is { } p ? $"→ {_view.SectionOfPart(p.Part)}+0x{p.Offset:X}" : "(pointer)",
        _ => "0x" + BE.U32(_data!, f.Offset).ToString("X8"),
    };

    string FieldNote(ObjField f)
    {
        switch (f.Kind)
        {
            case "str": return f.Samples.Count > 1 ? $"{f.Samples.Count} known values (right-click)" : "";
            case "assetref":
                uint id = BE.U32(_data!, f.Offset);
                return id == 0 ? "(none)" : _names?.GetValueOrDefault(id) ?? "unknown asset id";
            case "float" or "int" or "u16": return f.Varies ? $"range in game data: {f.Min:G6} .. {f.Max:G6}" : "";
            default: return "";
        }
    }

    void ShowFields()
    {
        var rows = new List<DataGridViewRow>();
        foreach (var f in _schema!.Fields)
        {
            if (f.Offset + Math.Max(2, Math.Min(4, f.Size)) > _data!.Length) continue;
            var row = new DataGridViewRow();
            row.CreateCells(_grid, $"0x{f.Offset:X}", FieldLabel(f), f.Kind, FieldValue(f), FieldNote(f));
            row.Tag = f;
            if (f.Kind == "ptr") { row.ReadOnly = true; row.DefaultCellStyle.ForeColor = Color.SteelBlue; }
            if (f.Name != null) row.DefaultCellStyle.BackColor = Color.FromArgb(240, 248, 255);
            rows.Add(row);
        }
        _grid.Rows.AddRange(rows.ToArray());
    }

    void EditField(DataGridViewRow r, ObjField f, string txt)
    {
        var before = _data![f.Offset..Math.Min(_data.Length, f.Offset + Math.Max(4, f.Size))];
        try
        {
            switch (f.Kind)
            {
                case "str":
                    if (txt.Any(c => c < 0x20 || c > 0x7E)) throw new FormatException("only plain ASCII text is allowed");
                    if (txt.Length >= f.Size) throw new FormatException($"too long: at most {f.Size - 1} characters fit this field");
                    int old = BE.CStr(_data, f.Offset, f.Size).Length;
                    Array.Clear(_data, f.Offset, Math.Min(f.Size, Math.Max(old, txt.Length) + 1));
                    System.Text.Encoding.ASCII.GetBytes(txt).CopyTo(_data, f.Offset);
                    break;
                case "u16": BE.W16(_data, f.Offset, ushort.Parse(txt, CultureInfo.InvariantCulture)); break;
                case "float":
                    float v = float.Parse(txt, CultureInfo.InvariantCulture);
                    if (!float.IsFinite(v)) throw new FormatException("not a finite number");
                    BE.WF32(_data, f.Offset, v); break;
                case "int": BE.W32(_data, f.Offset, uint.Parse(txt, CultureInfo.InvariantCulture)); break;
                case "assetref":
                    uint id = txt.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToUInt32(txt[2..], 16)
                        : Index?.Entries.FirstOrDefault(x => x.Name == txt)?.Id ?? throw new FormatException("no asset with that name");
                    BE.W32(_data, f.Offset, id); break;
                case "ptr": return;
                default: BE.W32(_data, f.Offset, Convert.ToUInt32(txt.Replace("0x", "").Replace("0X", ""), 16)); break;
            }
            if (!before.AsSpan().SequenceEqual(_data.AsSpan(f.Offset, before.Length)))
            {
                HasUnsaved = true; _save.Enabled = true;
                r.DefaultCellStyle.BackColor = Color.FromArgb(220, 255, 220);
                Log?.Invoke($"Tag edit: {AssetIds.DisplayName(_view!.Name)} +0x{f.Offset:X} {FieldLabel(f)} = {FieldValue(f)} (unsaved)");
                Changed?.Invoke();
            }
        }
        catch (Exception ex) { MessageBox.Show(this, $"Invalid value for {f.Kind}: {ex.Message}", "Tag Editor"); }
        r.Cells[3].Value = FieldValue(f); r.Cells[4].Value = FieldNote(f);
    }

    void ShowValueMenu(int rowIndex)
    {
        var r = _grid.Rows[rowIndex];
        if (r.Tag is not ObjField f || r.ReadOnly || (f.Samples.Count < 2 && f.Kind != "assetref")) return;
        _values.Items.Clear();
        IEnumerable<string> vals = f.Kind == "assetref"
            ? f.Samples.Select(v => _names?.GetValueOrDefault(Convert.ToUInt32(v, 16)) ?? v)
            : f.Samples;
        foreach (var v in vals.Take(64))
        {
            var val = v;
            _values.Items.Add(string.IsNullOrEmpty(v) ? "(empty)" : v, null, (_, _) => { r.Cells[3].Value = val; EditField(r, f, val); });
        }
        _values.Show(Cursor.Position);
    }

    // ---------------- light setups and particle effects (also editable in the Atmosphere tab) ----------------

    void LabelLightSetup(int fog)
    {
        _labels[0x08] = "ambient colour RGB0 (verified)"; _labels[0x0C] = "sun colour RGB0 (verified)";
        _labels[0x10] = "sun elevation (rad)"; _labels[0x14] = "sun azimuth (rad)"; _labels[0x1C] = "sun intensity (verified)";
        _labels[fog] = "fog command: size"; _labels[fog + 4] = "fog command: op 0x53";
        _labels[fog + 0x08] = "fog on"; _labels[fog + 0x0C] = "fog start (verified)"; _labels[fog + 0x10] = "fog end (verified)";
        _labels[fog + 0x14] = "fog max opacity 0..1 (verified)"; _labels[fog + 0x24] = "fog colour RGB0 (verified)";
    }

    void LabelParticle()
    {
        _labels[0x10] = "particle buffer size (max alive)"; _labels[0x34] = "texture id";
        _labels[0x78] = "emitter box min x"; _labels[0x7C] = "emitter box min y"; _labels[0x80] = "emitter box min z";
        _labels[0x84] = "emitter box max x"; _labels[0x88] = "emitter box max y"; _labels[0x8C] = "emitter box max z";
        _labels[0xB8] = "size (start, min)"; _labels[0xBC] = "size (start, max)"; _labels[0xC0] = "size (end, min)"; _labels[0xC4] = "size (end, max)";
        _labels[0xCC] = "fall term"; _labels[0xD0] = "fall term";
        _labels[0x124] = "lifetime min (s)"; _labels[0x128] = "lifetime max (s)";
        _labels[0x13C] = "emission rate (particles/s; do not change live)";
        _labels[0x180] = "0x534E = follows the camera (exe mod snow-follows-camera)";
    }

    // ---------------- vehicles ----------------

    void LabelVehicle()
    {
        var v = VehicleAsset.TryParse(_data!);
        if (v == null) return;
        _names ??= Index?.Entries.Where(x => x.Id != 0).GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().Name);
        foreach (var (off, label) in v.Labels(id => _names?.GetValueOrDefault(id))) _labels[off] = label;
    }

    /// <summary>Saves pending tag edits (File > Save All, Ctrl+S).</summary>
    public void SaveNow() { if (HasUnsaved) Save(); }

    /// <summary>Drops pending tag edits ("Don't save"): the edited bundle is read again from the workspace when next used.</summary>
    public void DiscardNow()
    {
        if (!HasUnsaved) return;
        if (_ws != null) _ws.ForgetCache(_bundle);
        HasUnsaved = false; _save.Enabled = false;
        _caff = null; _view = null; _data = null; _grid.Rows.Clear(); _head.Text = "";
        Changed?.Invoke();
    }

    void Save()
    {
        if (_ws == null || _caff == null) return;
        try
        {
            _ws.SaveResident(_bundle, _caff, $"tag edits in {AssetIds.DisplayName(_view!.Name)}");
            HasUnsaved = false; _save.Enabled = false;
            Log?.Invoke($"Saved bundle {_bundle:x6} with tag edits.");
            Changed?.Invoke();
        }
        catch (Exception e) { MessageBox.Show(this, "Save failed: " + e.Message, "Tag Editor"); }
    }
}
