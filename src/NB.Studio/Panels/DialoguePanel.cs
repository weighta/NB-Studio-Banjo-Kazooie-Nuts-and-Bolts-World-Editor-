using NB.Core.Project;
using NB.Core.World;

namespace NB.Studio.Panels;

/// <summary>
/// The dialogue of the selected character (a marker that places an actor): every text line its dialogs, scripts and shop
/// use (see <see cref="CharacterText"/>), editable in place and saved into the text tables of the workspace.
/// </summary>
public sealed class DialoguePanel : UserControl
{
    readonly Label _title = new() { Dock = DockStyle.Top, Height = 40, AutoEllipsis = true, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold) };
    readonly ComboBox _lang = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    readonly Button _save = new() { Text = "Save Lines", AutoSize = true, Enabled = false };
    readonly CheckBox _auto = new() { Text = "Show when a character is selected", AutoSize = true, Checked = true, Padding = new Padding(6, 4, 0, 0) };
    readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, Font = new Font("Segoe UI", 9),
    };
    readonly Label _info = new() { Dock = DockStyle.Bottom, Height = 48, ForeColor = SystemColors.GrayText };
    public Action<string>? Log;
    /// <summary>Selecting a character brings this tab to the front (checkbox).</summary>
    public bool AutoShow => _auto.Checked;
    public int LineCount => _lines.Count;
    public event Action? Saved;

    Workspace? _ws; AssetIndex? _index;
    MarkerRecord? _marker; string? _world; string _name = "";
    List<DialogueLine> _lines = new();
    readonly HashSet<DialogueLine> _edited = new();

    public DialoguePanel()
    {
        _grid.Columns.Add("name", "Line"); _grid.Columns.Add("text", "Text"); _grid.Columns.Add("src", "From");
        _grid.Columns[0].FillWeight = 26; _grid.Columns[1].FillWeight = 56; _grid.Columns[2].FillWeight = 18;
        _grid.Columns[0].ReadOnly = _grid.Columns[2].ReadOnly = true;
        _grid.Columns[1].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        _grid.CellEndEdit += (_, e) =>
        {
            if (_grid.Rows[e.RowIndex].Tag is not DialogueLine l) return;
            var s = Convert.ToString(_grid.Rows[e.RowIndex].Cells[1].Value) ?? "";
            if (s == l.Text) return;
            l.Text = s; _edited.Add(l); _save.Enabled = true;
            _grid.Rows[e.RowIndex].DefaultCellStyle.BackColor = Color.FromArgb(220, 255, 220);
        };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32 };
        bar.Controls.AddRange(new Control[] { new Label { Text = "Language", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, _lang, _save, _auto });
        _lang.SelectedIndexChanged += (_, _) => Reload();
        _save.Click += (_, _) => SaveEdits();
        Controls.Add(_grid); Controls.Add(_info); Controls.Add(bar); Controls.Add(_title);
        _info.Text = "Lines found through the character's dialogs and scripts, and lines named after it in the world's text tables. " +
                     "Keep text tags such as {MOODHAPPY}. Save writes the text tables into the workspace (Ctrl+Z undoes it).";
        Show(null, null, null);
    }

    public void SetWorkspace(Workspace ws, AssetIndex? index)
    {
        _ws = ws; _index = index;
        _lang.Items.Clear(); _lang.Items.Add("english");
        var dir = Path.Combine(ws.Game.Root, "loctext");
        if (Directory.Exists(dir)) foreach (var d in Directory.GetDirectories(dir).Select(Path.GetFileName).OrderBy(x => x)) _lang.Items.Add(d!);
        _lang.SelectedIndex = 0;
    }

    /// <summary>Shows the dialogue of a selected object (nothing for objects that are not characters).</summary>
    public void Show(WorldScene? scene, SceneObject? o, AssetIndex? index)
    {
        if (index != null) _index = index;
        if (_edited.Count > 0 && o?.Marker != _marker && _ws != null)
        {
            // unsaved edits of the previous character are kept: save them first so a click elsewhere does not lose them
            try { int n = CharacterText.Save(_ws, _edited, Language); Log?.Invoke($"Dialogue: saved {_edited.Count} edited line(s) of {_name} ({n} text table(s)) before showing another object."); Saved?.Invoke(); }
            catch (Exception e) { Log?.Invoke("Dialogue: saving failed: " + e.Message); }
            _edited.Clear();
        }
        _marker = o?.Marker; _name = o?.Name ?? "";
        _world = scene?.Background.View.Name is string bn ? NB.Core.Formats.AssetIds.DisplayName(bn).Replace("aid_model_banjox_background_", "").Replace("_default", "") : null;
        Reload();
    }

    string Language => _lang.SelectedItem as string ?? "english";

    void Reload()
    {
        _grid.Rows.Clear(); _lines = new(); _edited.Clear(); _save.Enabled = false;
        if (_marker == null || _ws == null || _index == null) { _title.Text = "Select a character (a marker that places an actor) to see its dialogue."; return; }
        try
        {
            var (key, lines, notes) = CharacterText.For(_ws, _index, _marker, _world, Language);
            _lines = lines;
            _title.Text = key == null ? $"{_name}\nNot a character (no actor objparams)." : $"{_name}\nCharacter \"{key}\": {lines.Count} line(s) — {Language}";
            foreach (var l in lines)
            {
                int r = _grid.Rows.Add(l.Name, l.Text, l.Source == "name" ? $"named ({l.TableName})" : l.Source);
                _grid.Rows[r].Tag = l;
            }
            if (notes.Count > 0) Log?.Invoke("Dialogue: " + string.Join("; ", notes.Take(4)));
        }
        catch (Exception e) { _title.Text = $"{_name}\nDialogue lookup failed: {e.Message}"; }
    }

    void SaveEdits()
    {
        if (_ws == null || _edited.Count == 0) return;
        try
        {
            int n = CharacterText.Save(_ws, _edited, Language);
            Log?.Invoke($"Dialogue: saved {_edited.Count} line(s) of {_name} into {n} text table(s) ({Language}).");
            _edited.Clear(); _save.Enabled = false;
            foreach (DataGridViewRow r in _grid.Rows) r.DefaultCellStyle.BackColor = Color.Empty;
            Saved?.Invoke();
        }
        catch (Exception e) { Log?.Invoke("Dialogue: saving failed: " + e.Message); MessageBox.Show(this, e.Message, "Saving dialogue failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    // ---- scripted checks
    public string ScriptState() => $"{_title.Text.Replace('\n', ' ')}; " + string.Join(" | ", _lines.Take(30).Select(l => $"{l.Name}={(l.Text.Length > 40 ? l.Text[..40] + "…" : l.Text)} [{l.Source}]"));
    public string ScriptEdit(string name, string text)
    {
        for (int r = 0; r < _grid.Rows.Count; r++)
            if (_grid.Rows[r].Tag is DialogueLine l && l.Name == name)
            {
                _grid.Rows[r].Cells[1].Value = text; l.Text = text; _edited.Add(l); _save.Enabled = true;
                _grid.Rows[r].DefaultCellStyle.BackColor = Color.FromArgb(220, 255, 220);
                return $"edited {name}";
            }
        return $"no line {name}";
    }
    public void ScriptSave() => SaveEdits();
    public void ScriptLanguage(string lang) { int i = _lang.Items.IndexOf(lang); if (i >= 0) _lang.SelectedIndex = i; }
}
