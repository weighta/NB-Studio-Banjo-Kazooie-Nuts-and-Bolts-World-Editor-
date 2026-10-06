using NB.Core.Formats;
using NB.Core.Project;

namespace NB.Studio.Panels;

/// <summary>Localised text editor: English lives in Debug/11/xx/yy/zz, other languages in loctext/&lt;language&gt;/&lt;id&gt;.</summary>
public sealed class TextPanel : UserControl
{
    readonly ComboBox _lang = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    readonly ComboBox _table = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
    readonly TextBox _find = new() { Width = 200, PlaceholderText = "Filter text…" };
    readonly Button _save = new() { Text = "Save Table to Workspace", AutoSize = true, Enabled = false };
    readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, Font = new Font("Segoe UI", 9),
    };
    readonly Label _info = new() { Dock = DockStyle.Bottom, Height = 36, ForeColor = SystemColors.GrayText };
    public Action<string>? Log;
    Workspace? _ws; LocText? _text; CaffFile? _caff; string? _path;

    public TextPanel()
    {
        _grid.Columns.Add("key", "Key"); _grid.Columns.Add("name", "Name"); _grid.Columns.Add("text", "Text");
        _grid.Columns[0].FillWeight = 8; _grid.Columns[1].FillWeight = 25; _grid.Columns[2].FillWeight = 67;
        _grid.Columns[0].ReadOnly = _grid.Columns[1].ReadOnly = true;
        _grid.Columns[2].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        _grid.CellEndEdit += (_, e) =>
        {
            if (_text == null) return;
            int i = (int)_grid.Rows[e.RowIndex].Tag!;
            var s = Convert.ToString(_grid.Rows[e.RowIndex].Cells[2].Value) ?? "";
            if (s != _text.Strings[i].Text) { _text.Strings[i] = (_text.Strings[i].Key, s); _save.Enabled = true; _grid.Rows[e.RowIndex].DefaultCellStyle.BackColor = Color.FromArgb(220, 255, 220); }
        };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32 };
        bar.Controls.AddRange(new Control[] { new Label { Text = "Language", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, _lang, _table, _find, _save });
        _lang.SelectedIndexChanged += (_, _) => FillTables();
        _table.SelectedIndexChanged += (_, _) => LoadTable();
        _find.TextChanged += (_, _) => FillGrid();
        _save.Click += (_, _) => Save();
        Controls.Add(_grid); Controls.Add(_info); Controls.Add(bar);
        _info.Text = "Text tags like {MOODHAPPY} control speaker animation; keep them. Tables marked read-only use a layout that is not fully decoded yet.";
    }

    public void SetWorkspace(Workspace ws)
    {
        _ws = ws;
        _lang.Items.Clear(); _lang.Items.Add("english (Debug/11)");
        foreach (var d in Directory.GetDirectories(Path.Combine(ws.Game.Root, "loctext")).Select(Path.GetFileName).OrderBy(x => x)) _lang.Items.Add(d!);
        _lang.SelectedIndex = 0;
    }

    sealed record TableItem(string Path, string Name) { public override string ToString() => Name; }

    void FillTables()
    {
        if (_ws == null) return;
        _table.Items.Clear();
        IEnumerable<string> files = _lang.SelectedIndex == 0
            ? Directory.GetFiles(Path.Combine(_ws.Game.Root, "Debug", "11"), "*", SearchOption.AllDirectories)
            : Directory.GetFiles(Path.Combine(_ws.Game.Root, "loctext", (string)_lang.SelectedItem!));
        var items = new List<TableItem>();
        foreach (var f in files)
        {
            try { var c = CaffFile.Read(File.ReadAllBytes(f)); items.Add(new TableItem(f, AssetIds.DisplayName(c.Symbols[0]).Replace("aid_loctext_banjox_", ""))); } catch { }
        }
        foreach (var i in items.OrderBy(i => i.Name)) _table.Items.Add(i);
        if (_table.Items.Count > 0) _table.SelectedIndex = 0;
    }

    void LoadTable()
    {
        if (_table.SelectedItem is not TableItem ti) return;
        _path = ti.Path;
        _caff = CaffFile.Read(File.ReadAllBytes(ti.Path));
        _text = LocText.Parse(_caff.Parts.First(p => _caff.SectionOf(p).Name == ".data").Data);
        _save.Enabled = false;
        _grid.Columns[2].ReadOnly = !_text.Editable;
        FillGrid();
        Log?.Invoke($"Text table {ti.Name}: {_text.Strings.Count} strings{(_text.Editable ? "" : " (read-only)")} — {Path.GetRelativePath(_ws!.Game.Root, ti.Path)}");
    }

    void FillGrid()
    {
        _grid.Rows.Clear();
        if (_text == null) return;
        string q = _find.Text.Trim();
        for (int i = 0; i < _text.Strings.Count; i++)
        {
            var (k, s) = _text.Strings[i];
            var n = _text.Names.GetValueOrDefault(k, "");
            if (q != "" && !s.Contains(q, StringComparison.OrdinalIgnoreCase) && !n.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            int r = _grid.Rows.Add(k.ToString("X4"), n, s); _grid.Rows[r].Tag = i;
        }
    }

    void Save()
    {
        if (_ws == null || _text == null || _caff == null || _path == null) return;
        var part = _caff.Parts.First(p => _caff.SectionOf(p).Name == ".data");
        part.Data = _text.Write();
        var check = LocText.Parse(part.Data);
        if (check.Strings.Count != _text.Strings.Count) throw new InvalidDataException("text table failed validation");
        var bytes = _caff.Write();
        // through the workspace: history copy, and a hard link to the original game is replaced, not written through
        _ws.SaveFile(_path, bytes, "edited text table " + ((TableItem)_table.SelectedItem!).Name);
        _save.Enabled = false;
        Log?.Invoke($"Saved text table → {Path.GetRelativePath(_ws.Game.Root, _path)} ({bytes.Length:N0} bytes)");
    }
}
