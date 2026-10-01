using NB.Core.Project;

namespace NB.Studio.Panels;

/// <summary>
/// "Create Distributable Patch": what the mod is called and what kind of mod it is (category, shown as a badge and used
/// as a filter in NB Multiplayer's mod library), its version and a short description. The category starts as a guess
/// from the files the workspace changes.
/// </summary>
public sealed class PatchInfoDialog : Form
{
    readonly TextBox _name = new() { Width = 360 };
    readonly TextBox _version = new() { Width = 90, Text = "1.0" };
    readonly TextBox _author = new() { Width = 220 };
    readonly ComboBox _category = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    readonly Label _categoryHelp = new() { AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = SystemColors.GrayText };
    readonly TextBox _tags = new() { Width = 360 };
    readonly ComboBox _multiplayer = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360 };
    readonly TextBox _desc = new() { Width = 560, Height = 70, Multiline = true, ScrollBars = ScrollBars.Vertical };

    public string ModName => _name.Text.Trim();
    public string Version => _version.Text.Trim().Length > 0 ? _version.Text.Trim() : "1.0";
    public string Author => _author.Text.Trim();
    public string Description => _desc.Text.Trim();
    public string Category => ModCategories.All[_category.SelectedIndex].Id;
    public List<string> Tags => _tags.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    public string Multiplayer => _multiplayer.SelectedIndex switch { 0 => "world", 1 => "cosmetic", _ => "coop" };

    public PatchInfoDialog(string defaultName, string author, ModCategory guess, int changedFiles, int exeMods)
    {
        Text = "Create Distributable Patch"; StartPosition = FormStartPosition.CenterParent; MinimizeBox = false; MaximizeBox = false;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; FormBorderStyle = FormBorderStyle.FixedDialog; Padding = new Padding(12);
        _name.Text = defaultName; _author.Text = author;
        foreach (var c in ModCategories.All) _category.Items.Add(c.Name);
        _category.SelectedIndex = ModCategories.All.ToList().IndexOf(guess);
        _category.SelectedIndexChanged += (_, _) => _categoryHelp.Text = ModCategories.All[_category.SelectedIndex].Description;
        _categoryHelp.Text = guess.Description;
        _multiplayer.Items.AddRange(new object[]
        {
            "Changes the game: everyone in a multiplayer room needs it",
            "Looks / sounds only: players without it can still play together",
            "Adds what Showdown Town co-op needs",
        });
        _multiplayer.SelectedIndex = guess == ModCategories.Coop ? 2 : guess == ModCategories.Visual || guess == ModCategories.Audio ? 1 : 0;

        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill };
        void Row(string label, Control c) { grid.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 6, 8, 0) }); grid.Controls.Add(c); }
        grid.Controls.Add(new Label
        {
            Text = $"The patch holds this workspace's changes: {changedFiles} changed game file(s){(exeMods > 0 ? $" and {exeMods} executable mod(s)" : "")}. " +
                   "It contains no original game data. NB Multiplayer shows these details in its mod library.",
            AutoSize = true, MaximumSize = new Size(680, 0), Padding = new Padding(0, 0, 0, 8),
        });
        grid.SetColumnSpan(grid.Controls[^1], 2);
        Row("Name", _name);
        Row("Version", _version);
        Row("Author", _author);
        var cat = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
        cat.Controls.Add(_category); cat.Controls.Add(_categoryHelp);
        Row("Category", cat);
        Row("Tags", _tags);
        grid.Controls.Add(new Label()); grid.Controls.Add(new Label { Text = "Comma-separated, e.g. Showdown Town, AI vehicles", AutoSize = true, ForeColor = SystemColors.GrayText });
        Row("Multiplayer", _multiplayer);
        Row("Description", _desc);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom };
        var ok = new Button { Text = "Choose where to save…", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
        grid.Controls.Add(new Label()); grid.Controls.Add(buttons);
        AcceptButton = ok; CancelButton = cancel;
        Controls.Add(grid);
        ok.Click += (_, e) => { if (ModName.Length == 0) { MessageBox.Show(this, "Give the mod a name.", Text); DialogResult = DialogResult.None; } };
    }
}
