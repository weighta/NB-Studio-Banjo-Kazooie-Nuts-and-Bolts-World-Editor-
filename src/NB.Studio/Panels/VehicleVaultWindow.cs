using NB.Core.Project;

namespace NB.Studio.Panels;

/// <summary>
/// "My Vehicle Saves": the shared vehicle folder (<see cref="VehicleVault"/>: what every Test in Xenia and NB
/// Multiplayer put into Your Blueprints) as a list — name, parts, when it was saved, and notes for vehicles saved more than
/// once (same parts) or sharing a name. Open (into the editor, as a new vehicle), Remove… (asks first), Remove
/// duplicates… (asks first, keeps the newest of each), Show in Explorer. Show removed lists what was replaced or removed;
/// Restore puts it back. Nothing is removed without asking.
/// </summary>
public sealed class VehicleVaultWindow : QuietForm
{
    readonly string _dir;
    readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = true };
    readonly Label _info = new() { Dock = DockStyle.Top, Height = 40, Padding = new Padding(6), AutoEllipsis = true };
    readonly Button _open = new() { Text = "Open in Editor", AutoSize = true };
    readonly Button _remove = new() { Text = "Remove…", AutoSize = true };
    readonly Button _dups = new() { Text = "Remove Duplicates…", AutoSize = true };
    readonly Button _explorer = new() { Text = "Show in Explorer", AutoSize = true };
    readonly CheckBox _showRemoved = new() { Text = "Show removed", AutoSize = true, Margin = new Padding(12, 7, 3, 3) };
    readonly Button _restore = new() { Text = "Restore", AutoSize = true, Visible = false };
    readonly Button _close = new() { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };

    /// <summary>The user wants this vehicle in the editor (its file in the folder, its name).</summary>
    public event Action<string, string>? OpenRequested;
    public Action<string>? Log;
    /// <summary>Scripted tests: the answer to "remove?" questions (null: ask).</summary>
    public static bool? ScriptConfirm;

    sealed record Row(VehicleVault.Entry Entry, string? Removed);

    public VehicleVaultWindow(string dir)
    {
        _dir = dir;
        Text = "My Vehicle Saves";
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false; MinimizeBox = false;
        Size = new Size(760, 460); MinimumSize = new Size(520, 300);
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        _list.Columns.Add("Name", 220); _list.Columns.Add("Parts", 55, HorizontalAlignment.Right); _list.Columns.Add("Saved", 125); _list.Columns.Add("Note", 305);
        var row = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6), FlowDirection = FlowDirection.LeftToRight };
        row.Controls.AddRange(new Control[] { _open, _remove, _dups, _explorer, _showRemoved, _restore, _close });
        Controls.Add(_list); Controls.Add(row); Controls.Add(_info);
        CancelButton = _close;
        _open.Click += (_, _) => { if (Selected().FirstOrDefault() is { } r) OpenRequested?.Invoke(Path.Combine(_dir, r.Entry.Hash + ".bp"), r.Entry.Name); };
        _list.DoubleClick += (_, _) => _open.PerformClick();
        _remove.Click += (_, _) => RemoveSelected();
        _dups.Click += (_, _) => RemoveDuplicates();
        _explorer.Click += (_, _) => ShowFolder(_dir, Selected().FirstOrDefault() is { } r ? Path.Combine(_dir, r.Entry.Hash + ".bp") : null, Log);
        _showRemoved.CheckedChanged += (_, _) => { _restore.Visible = _showRemoved.Checked; Fill(); };
        _restore.Click += (_, _) => RestoreSelected();
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        Fill();
    }

    void UpdateButtons()
    {
        var sel = Selected();
        _open.Enabled = sel.Count == 1;
        _remove.Enabled = sel.Count > 0 && sel.All(r => r.Removed == null);
        _restore.Enabled = sel.Count > 0 && sel.All(r => r.Removed != null);
    }

    /// <summary>Opens the folder in Explorer (selecting <paramref name="file"/> when given).</summary>
    public static void ShowFolder(string dir, string? file, Action<string>? log)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var args = file != null && File.Exists(file) ? $"/select,\"{file}\"" : $"\"{dir}\"";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch (Exception e) { log?.Invoke("Could not open the folder: " + e.Message); }
    }

    List<Row> Selected() => _list.SelectedItems.Cast<ListViewItem>().Select(i => (Row)i.Tag!).ToList();

    public void Fill()
    {
        var all = VehicleVault.Vehicles(_dir);
        var dups = VehicleVault.Duplicates(_dir);
        var dupOf = new Dictionary<string, string>();
        foreach (var g in dups)
            foreach (var e in g.Skip(1)) dupOf[e.Hash] = $"same vehicle as '{g[0].Name}' ({g[0].FirstSeen:yyyy-MM-dd HH:mm})";
        var names = all.GroupBy(e => e.Name.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = _showRemoved.Checked ? VehicleVault.RemovedVehicles(_dir) : new();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var e in all.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenByDescending(e => e.FirstSeen))
        {
            string note = dupOf.TryGetValue(e.Hash, out var d) ? "duplicate: " + d : names.Contains(e.Name.Trim()) ? "another vehicle has this name" : "";
            var it = new ListViewItem(new[] { e.Name, VehicleVault.PartsOf(_dir, e).ToString(), e.FirstSeen.ToString("yyyy-MM-dd HH:mm"), note }) { Tag = new Row(e, null) };
            if (note.StartsWith("duplicate")) it.ForeColor = Color.FromArgb(170, 90, 0);
            _list.Items.Add(it);
        }
        foreach (var (e, why) in removed)
            _list.Items.Add(new ListViewItem(new[] { e.Name, VehicleVault.PartsOf(_dir, e).ToString(), e.FirstSeen.ToString("yyyy-MM-dd HH:mm"), "removed: " + why })
                { Tag = new Row(e, why), ForeColor = SystemColors.GrayText });
        _list.EndUpdate();
        int extra = dups.Sum(g => g.Count - 1);
        _info.Text = $"{all.Count} vehicles in {_dir}" + (extra > 0 ? $"\n{extra} duplicate(s) of {dups.Count} vehicle(s) — Remove Duplicates… keeps the newest of each." : "\nNo duplicates.")
            + (_showRemoved.Checked ? $" {removed.Count} removed (grey): Restore puts them back." : "");
        _dups.Enabled = extra > 0;
        UpdateButtons();
    }

    bool Confirm(string text) => ScriptConfirm ?? MessageBox.Show(this, text, "My Vehicle Saves", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK;

    /// <summary>What a removal does, for the questions.</summary>
    const string RemovalNote = "They stop coming into new tests and NB Multiplayer. At the next test NB Studio's test saves give back the copies NB Studio put there; " +
                               "vehicles built in a test save, NB Multiplayer profiles and consoles keep theirs. The files stay in the folder: Show removed › Restore brings them back.";

    void RemoveSelected()
    {
        var sel = Selected().Where(r => r.Removed == null).Select(r => r.Entry).ToList();
        if (sel.Count == 0) return;
        if (!Confirm($"Remove {sel.Count} vehicle(s) from your vehicle saves?\n\n{string.Join("\n", sel.Take(12).Select(e => "• " + e.Name))}\n\n{RemovalNote}")) return;
        VehicleVault.Remove(_dir, sel.Select(e => e.Hash), "removed in My Vehicle Saves");
        Log?.Invoke($"My Vehicle Saves: removed {string.Join(", ", sel.Select(e => e.Name))}.");
        Fill();
    }

    void RestoreSelected()
    {
        var sel = Selected().Where(r => r.Removed != null).Select(r => r.Entry).ToList();
        if (sel.Count == 0) return;
        VehicleVault.Restore(_dir, sel.Select(e => e.Hash));
        Log?.Invoke($"My Vehicle Saves: restored {string.Join(", ", sel.Select(e => e.Name))}: the next test (and NB Multiplayer) gets them again.");
        Fill();
    }

    /// <summary>The one-time clean-up: every vehicle saved more than once keeps its newest copy (asks first, lists them).</summary>
    public int RemoveDuplicates()
    {
        var dups = VehicleVault.Duplicates(_dir);
        if (dups.Count == 0) return 0;
        var lines = dups.Select(g => $"• keep '{g[0].Name}' ({g[0].FirstSeen:yyyy-MM-dd HH:mm}), remove {g.Count - 1}: {string.Join(", ", g.Skip(1).Select(e => $"'{e.Name}' ({e.FirstSeen:MM-dd HH:mm})"))}");
        if (!Confirm($"These vehicles are in your vehicle saves more than once (the same parts):\n\n{string.Join("\n", lines.Take(12))}{(dups.Count > 12 ? "\n…" : "")}\n\nRemove the older copies?\n\n{RemovalNote}")) return 0;
        var gone = dups.SelectMany(g => g.Skip(1)).ToList();
        VehicleVault.Remove(_dir, gone.Select(e => e.Hash), "duplicate removed in My Vehicle Saves");
        Log?.Invoke($"My Vehicle Saves: removed {gone.Count} duplicate(s): {string.Join(", ", gone.Select(e => e.Name))}.");
        Fill();
        return gone.Count;
    }

    /// <summary>Scripted tests: show removed vehicles, restore by name.</summary>
    public void ShowRemoved(bool on) => _showRemoved.Checked = on;
    public int RestoreByName(string name)
    {
        var hs = VehicleVault.RemovedVehicles(_dir).Where(x => string.Equals(x.Entry.Name, name, StringComparison.OrdinalIgnoreCase)).Select(x => x.Entry.Hash).ToList();
        if (hs.Count > 0) { VehicleVault.Restore(_dir, hs); Fill(); }
        return hs.Count;
    }

    /// <summary>Scripted tests: the list as text.</summary>
    public IEnumerable<string> Dump() => new[] { _info.Text.Replace("\n", " | ") }.Concat(_list.Items.Cast<ListViewItem>().Select(i => string.Join(" | ", i.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => s.Text))));
}
