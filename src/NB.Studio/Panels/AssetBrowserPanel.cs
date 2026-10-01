using NB.Core.Project;

namespace NB.Studio.Panels;

/// <summary>Searchable list of every asset in the game (from the <see cref="AssetIndex"/>).</summary>
public sealed class AssetBrowserPanel : UserControl
{
    readonly TextBox _search = new() { Dock = DockStyle.Top, PlaceholderText = "Search name, id (hex) or bundle…" };
    readonly ComboBox _type = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox _bundle = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, VirtualMode = true, FullRowSelect = true, HideSelection = false };
    readonly Label _count = new() { Dock = DockStyle.Bottom, Height = 20 };
    AssetIndex? _index;
    List<AssetEntry> _filtered = new();
    public event Action<AssetEntry>? AssetActivated;
    public IReadOnlyList<AssetEntry> Filtered => _filtered;

    public AssetBrowserPanel()
    {
        _list.Columns.Add("Name", 260); _list.Columns.Add("Type", 80); _list.Columns.Add("Bundle", 60); _list.Columns.Add("Id", 80); _list.Columns.Add("Size", 70);
        _list.RetrieveVirtualItem += (_, e) =>
        {
            var a = _filtered[e.ItemIndex];
            e.Item = new ListViewItem(new[] { a.Name, a.Type, (a.Streamed ? "50/" : "4f/") + a.Bundle.ToString("x6"), a.Id.ToString("X8"), a.Size.ToString("N0") }) { Tag = a };
        };
        _list.DoubleClick += (_, _) => Activate();
        _list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) Activate(); };
        _list.SelectedIndexChanged += (_, _) => Activate();
        _search.TextChanged += (_, _) => Filter();
        _type.SelectedIndexChanged += (_, _) => Filter();
        _bundle.SelectedIndexChanged += (_, _) => Filter();
        Controls.Add(_list); Controls.Add(_count); Controls.Add(_bundle); Controls.Add(_type); Controls.Add(_search);
    }

    void Activate()
    {
        if (_list.SelectedIndices.Count == 1) AssetActivated?.Invoke(_filtered[_list.SelectedIndices[0]]);
    }

    public void SetIndex(AssetIndex idx)
    {
        _index = idx;
        _type.Items.Clear(); _type.Items.Add("(all types)");
        foreach (var t in idx.Entries.GroupBy(e => e.Type).OrderByDescending(g => g.Count())) _type.Items.Add($"{t.Key} ({t.Count()})");
        _type.SelectedIndex = 0;
        _bundle.Items.Clear(); _bundle.Items.Add("(all bundles)");
        foreach (var b in idx.BundleSummary.OrderBy(kv => kv.Value)) _bundle.Items.Add($"{b.Key:x6}  {b.Value}");
        _bundle.SelectedIndex = 0;
        Filter();
    }

    void Filter()
    {
        if (_index == null) return;
        string q = _search.Text.Trim().ToLowerInvariant();
        string? type = _type.SelectedIndex > 0 ? ((string)_type.SelectedItem!).Split(' ')[0] : null;
        uint? bundle = _bundle.SelectedIndex > 0 ? Convert.ToUInt32(((string)_bundle.SelectedItem!)[..6], 16) : null;
        _filtered = _index.Entries.Where(e =>
                (type == null || e.Type == type) && (bundle == null || e.Bundle == bundle) &&
                (q == "" || e.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Id.ToString("x8").Contains(q) || e.Bundle.ToString("x6") == q))
            .ToList();
        _list.VirtualListSize = _filtered.Count;
        _list.Invalidate();
        _count.Text = $"{_filtered.Count:N0} of {_index.Entries.Count:N0} assets";
    }

    /// <summary>Exports every asset in the current filter: textures → PNG, models → OBJ, everything else → raw parts.</summary>
    public async Task BulkExport(IWin32Window owner, Workspace? ws, Action<string> log, Action<string?, double> progress)
    {
        if (ws == null || _filtered.Count == 0) { log("Nothing to export (open a workspace and filter the asset list first)."); return; }
        using var d = new FolderBrowserDialog { Description = $"Export {_filtered.Count:N0} assets to…" };
        if (d.ShowDialog(owner) != DialogResult.OK) return;
        var items = _filtered.ToList(); string root = d.SelectedPath;
        int ok = 0, fail = 0;
        await Task.Run(() =>
        {
            foreach (var g in items.GroupBy(i => i.Bundle))
            {
                foreach (var a in g)
                {
                    try { AssetExporter.Export(ws, a, Path.Combine(root, a.Bundle.ToString("x6"), a.Type)); ok++; }
                    catch (Exception e) { fail++; log($"  export failed {a.Name}: {e.Message}"); }
                    if ((ok + fail) % 25 == 0) progress($"Exporting {ok + fail}/{items.Count}", (ok + fail) / (double)items.Count);
                }
                ws.ForgetCache(g.Key);
            }
        });
        progress(null, 0);
        log($"Bulk export: {ok} exported, {fail} failed → {root}");
    }
}
