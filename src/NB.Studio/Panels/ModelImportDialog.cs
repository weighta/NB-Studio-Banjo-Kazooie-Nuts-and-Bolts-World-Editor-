using NB.Core.Models;

namespace NB.Studio.Panels;

/// <summary>
/// Confirmation for "Import Model": the file's materials (triangles, texture file or colour, where each goes in the
/// model) and the options — import materials and textures (default) or geometry only (the old behaviour: the model's
/// own textures on the new UVs), texture size limit and neutral detail maps. The plan is recomputed on every change.
/// </summary>
public sealed class ModelImportDialog : Form
{
    readonly CheckBox _materials = new() { Text = "Import materials and textures from the file (recommended)", Checked = true, AutoSize = true };
    readonly CheckBox _neutral = new() { Text = "Neutral detail maps (flat normal, dark specular, white AO) for the new surfaces", Checked = true, AutoSize = true };
    readonly ComboBox _size = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, ShowItemToolTips = true };
    readonly TextBox _notes = new() { Dock = DockStyle.Bottom, Height = 90, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    readonly Func<MaterialImport.Options, MaterialImport.Plan> _plan;
    readonly Button _ok = new() { Text = "Import", DialogResult = DialogResult.OK, AutoSize = true };

    public bool ImportMaterials => _materials.Checked;
    public MaterialImport.Options Options => new() { MaxTextureSize = int.Parse((string)_size.SelectedItem!), AtlasSize = Math.Max(2048, int.Parse((string)_size.SelectedItem!)), NeutraliseDetail = _neutral.Checked };

    public ModelImportDialog(string model, int instances, string file, IReadOnlyList<ImportMesh> meshes, Func<MaterialImport.Options, MaterialImport.Plan> plan)
    {
        _plan = plan;
        Text = "Import Model"; Width = 980; Height = 560; StartPosition = FormStartPosition.CenterParent; MinimizeBox = false;
        var head = new Label
        {
            Dock = DockStyle.Top, Height = 44, Padding = new Padding(6),
            Text = $"Replace {model} ({instances} instance(s) in this world) with {Path.GetFileName(file)}: {meshes.Count} mesh(es), " +
                   $"{meshes.Sum(m => m.Positions.Count)} vertices, {meshes.Sum(m => m.Triangles.Count / 3)} triangles. Undo: Edit > Undo Last Bundle Save."
        };
        var opts = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 64, WrapContents = true, Padding = new Padding(4) };
        _size.Items.AddRange(new object[] { "256", "512", "1024", "2048" }); _size.SelectedItem = "1024";
        opts.Controls.Add(_materials); opts.Controls.Add(new Label { Text = "   Max texture size:", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }); opts.Controls.Add(_size);
        opts.Controls.Add(_neutral);
        _list.Columns.Add("Material", 150); _list.Columns.Add("Triangles", 70); _list.Columns.Add("Goes to", 260); _list.Columns.Add("Texture source", 330); _list.Columns.Add("Warning", 200);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(cancel); buttons.Controls.Add(_ok);
        AcceptButton = _ok; CancelButton = cancel;
        Controls.Add(_list); Controls.Add(_notes); Controls.Add(buttons); Controls.Add(opts); Controls.Add(head);
        _materials.CheckedChanged += (_, _) => Refresh2();
        _neutral.CheckedChanged += (_, _) => Refresh2();
        _size.SelectedIndexChanged += (_, _) => Refresh2();
        Refresh2();
    }

    void Refresh2()
    {
        _neutral.Enabled = _size.Enabled = _materials.Checked;
        _list.Items.Clear();
        if (!_materials.Checked)
        {
            _notes.Text = "Geometry only: the model keeps its own materials and textures (e.g. the Seattle facade atlas), drawn with the file's UVs. " +
                          "Meshes are assigned to the model's vertex buffers in order; the file's textures are not used.";
            _ok.Enabled = true;
            return;
        }
        try
        {
            var p = _plan(Options);
            foreach (var m in p.Materials)
            {
                string goes = m.Mode switch
                {
                    MaterialImport.Mode.Keep => "keeps game texture " + Short(m.Slot),
                    MaterialImport.Mode.Own => "own texture (slot " + Short(m.Slot) + ")",
                    _ => "shared atlas (slot " + Short(m.Slot) + ")",
                };
                string src = m.Source.Length > 3 && m.Source[1] == ':' ? Path.GetFileName(m.Source) + "  (" + Path.GetDirectoryName(m.Source) + ")" : m.Source;
                var it = new ListViewItem(new[] { m.Name, m.Triangles.ToString(), goes, src, m.Problem ?? "" }) { ToolTipText = m.Source + (m.Problem != null ? "\n" + m.Problem : "") };
                if (m.Problem != null) it.ForeColor = Color.DarkOrange;
                _list.Items.Add(it);
            }
            _notes.Text = string.Join(Environment.NewLine, p.Notes.Prepend($"Material slots in this model: {p.Slots.Count} ({string.Join(", ", p.Slots.Select(Short))}). " +
                "Each new material becomes a texture in this world's bundle; when there are more materials than slots, the extra ones share an atlas texture (tiling up to 4x is baked in)."));
            _ok.Enabled = true;
        }
        catch (Exception e) { _notes.Text = "Cannot import materials into this model: " + e.Message + Environment.NewLine + "Untick the option to import geometry only."; _ok.Enabled = false; }
    }

    static string Short(string s) => s.StartsWith("aid_texture_banjox_") ? s["aid_texture_banjox_".Length..] : s;
}
