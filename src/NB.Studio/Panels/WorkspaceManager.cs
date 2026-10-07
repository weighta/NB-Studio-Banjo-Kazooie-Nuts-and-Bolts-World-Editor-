using NB.Core.Project;

namespace NB.Studio.Panels;

/// <summary>
/// File > Manage Workspaces / the start page's "All workspaces…": every workspace NB Studio knows (recent list and the
/// folders next to them), with the disk space each really uses (files of its own; game files shared through hard links
/// take no extra space), to open, show in Explorer or delete (several at once).
/// </summary>
public sealed class WorkspaceManager : Form
{
    readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = true, HideSelection = false };
    readonly Label _info = new() { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(8, 4, 8, 4), ForeColor = SystemColors.GrayText };
    readonly Button _open = new() { Text = "Open", Width = 90 }, _del = new() { Text = "Delete…", Width = 90 },
        _show = new() { Text = "Show in Explorer", Width = 130 }, _close = new() { Text = "Close", Width = 90, DialogResult = DialogResult.Cancel };
    readonly string? _current;
    readonly CancellationTokenSource _cts = new();
    readonly Dictionary<string, WorkspaceFiles.Usage> _usage = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The workspace to open when the dialog closes with OK.</summary>
    public string? OpenPath { get; private set; }
    /// <summary>Workspaces deleted while the dialog was open.</summary>
    public List<string> Deleted { get; } = new();

    public WorkspaceManager(string? currentWorkspace, IEnumerable<string>? extra = null)
    {
        _current = currentWorkspace == null ? null : Path.GetFullPath(currentWorkspace).TrimEnd('\\', '/');
        Text = "Workspaces"; Width = 900; Height = 560; StartPosition = FormStartPosition.CenterParent; MinimizeBox = false; ShowIcon = false;
        _list.Columns.Add("Workspace", 190); _list.Columns.Add("Last changed", 130); _list.Columns.Add("Own size", 90, HorizontalAlignment.Right);
        _list.Columns.Add("Shared with the game", 140, HorizontalAlignment.Right); _list.Columns.Add("Folder", 320);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
        buttons.Controls.AddRange(new Control[] { _close, _del, _show, _open });
        Controls.AddRange(new Control[] { _list, _info, buttons });
        CancelButton = _close;
        _info.Text = "Own size = disk space the workspace really uses (your changes, history, test saves). Game files shared with your game " +
                     "through hard links take no extra space. Deleting a workspace never changes your game.";

        foreach (var p in WorkspaceFiles.Known(extra))
        {
            var it = new ListViewItem(new[] { Path.GetFileName(p), File.GetLastWriteTime(Path.Combine(p, "workspace.json")).ToString("yyyy-MM-dd HH:mm"), "…", "…", p }) { Tag = p };
            if (string.Equals(p, _current, StringComparison.OrdinalIgnoreCase)) { it.Text += "  (open)"; it.ForeColor = SystemColors.GrayText; }
            _list.Items.Add(it);
        }
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.DoubleClick += (_, _) => Open();
        _list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) DeleteSelected(); };
        _list.ColumnClick += (_, e) => SortBy(e.Column);
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => Open());
        menu.Items.Add("Show in Explorer", null, (_, _) => ShowInExplorer());
        menu.Items.Add("Delete workspace…", null, (_, _) => DeleteSelected());
        _list.ContextMenuStrip = menu;
        _open.Click += (_, _) => Open(); _del.Click += (_, _) => DeleteSelected(); _show.Click += (_, _) => ShowInExplorer();
        UpdateButtons();
        Shown += (_, _) => _ = MeasureAll();
        FormClosed += (_, _) => _cts.Cancel();
    }

    List<string> Selected() => _list.SelectedItems.Cast<ListViewItem>().Select(i => (string)i.Tag!).ToList();

    void UpdateButtons()
    {
        var s = Selected();
        _open.Enabled = s.Count == 1; _show.Enabled = s.Count == 1; _del.Enabled = s.Count > 0;
    }

    async Task MeasureAll()
    {
        foreach (ListViewItem it in _list.Items.Cast<ListViewItem>().ToList())
        {
            var p = (string)it.Tag!;
            try
            {
                var u = await Task.Run(() => WorkspaceFiles.Measure(p, _cts.Token), _cts.Token);
                if (IsDisposed) return;
                _usage[p] = u;
                it.SubItems[2].Text = WorkspaceFiles.Size(u.OwnBytes); it.SubItems[3].Text = WorkspaceFiles.Size(u.LinkedBytes);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { if (!IsDisposed) it.SubItems[2].Text = "?"; }
        }
        if (!IsDisposed)
            _info.Text = $"{_list.Items.Count} workspace(s), {WorkspaceFiles.Size(_usage.Values.Sum(u => u.OwnBytes))} of their own. " +
                         "Game files shared with your game through hard links take no extra space; deleting a workspace never changes your game.";
    }

    void SortBy(int col)
    {
        var items = _list.Items.Cast<ListViewItem>().ToList();
        items = col switch
        {
            1 => items.OrderByDescending(i => i.SubItems[1].Text).ToList(),
            2 => items.OrderByDescending(i => _usage.TryGetValue((string)i.Tag!, out var u) ? u.OwnBytes : -1).ToList(),
            3 => items.OrderByDescending(i => _usage.TryGetValue((string)i.Tag!, out var u) ? u.LinkedBytes : -1).ToList(),
            _ => items.OrderBy(i => i.SubItems[col].Text, StringComparer.OrdinalIgnoreCase).ToList(),
        };
        _list.BeginUpdate(); _list.Items.Clear(); _list.Items.AddRange(items.ToArray()); _list.EndUpdate();
    }

    void Open()
    {
        var s = Selected(); if (s.Count != 1) return;
        OpenPath = s[0]; DialogResult = DialogResult.OK; Close();
    }

    void ShowInExplorer()
    {
        var s = Selected(); if (s.Count != 1) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{s[0]}\"") { UseShellExecute = true }); } catch (Exception) { }
    }

    async void DeleteSelected()
    {
        var s = Selected();
        if (s.Count == 0) return;
        Enabled = false; UseWaitCursor = true;
        var done = await DeleteWithConfirm(this, s, _current, _usage, t => _info.Text = t);
        foreach (var p in done)
        {
            Deleted.Add(p);
            var it = _list.Items.Cast<ListViewItem>().FirstOrDefault(i => string.Equals((string)i.Tag!, p, StringComparison.OrdinalIgnoreCase));
            if (it != null) _list.Items.Remove(it);
        }
        Enabled = true; UseWaitCursor = false;
        if (done.Count > 0) _info.Text = $"Deleted {done.Count} workspace(s). Your game was not changed.";
        UpdateButtons();
    }

    /// <summary>
    /// Asks, then deletes the workspaces (never the open one, nor one whose test game still runs). Sizes are measured
    /// when <paramref name="usage"/> lacks them. Returns the workspaces deleted.
    /// </summary>
    public static async Task<List<string>> DeleteWithConfirm(IWin32Window owner, List<string> paths, string? current,
        Dictionary<string, WorkspaceFiles.Usage>? usage = null, Action<string>? status = null)
    {
        var done = new List<string>();
        current = current == null ? null : Path.GetFullPath(current).TrimEnd('\\', '/');
        if (current != null && paths.Any(p => string.Equals(Path.GetFullPath(p).TrimEnd('\\', '/'), current, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(owner, "The open workspace can't be deleted. Close it first (File > Close Workspace).", "Delete workspace", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return done;
        }
        var running = RunningFrom(paths);
        if (running.Count > 0)
        {
            MessageBox.Show(owner, "Close the game first:\n" + string.Join("\n", running), "Delete workspace", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return done;
        }
        usage ??= new(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths.Where(p => !usage.ContainsKey(p)))
        {
            status?.Invoke($"Measuring {Path.GetFileName(p)}…");
            try { usage[p] = await Task.Run(() => WorkspaceFiles.Measure(p)); } catch (Exception) { }
        }
        long own = paths.Sum(p => usage.TryGetValue(p, out var u) ? u.OwnBytes : 0);
        var names = string.Join("\n", paths.Select(p => "  " + Path.GetFileName(p) + (usage.TryGetValue(p, out var u) ? $"  ({WorkspaceFiles.Size(u.OwnBytes)})" : "")));
        if (MessageBox.Show(owner, $"Delete {(paths.Count == 1 ? "this workspace" : $"these {paths.Count} workspaces")} for good?\n\n{names}\n\n" +
                $"All their changes, history and test saves are deleted ({WorkspaceFiles.Size(own)} freed). Your game is not changed. " +
                "Mods you exported (.nbpatch files) are kept.", "Delete workspace", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return done;
        var prog = new Progress<(string Text, double Fraction)>(p => status?.Invoke(p.Text));
        foreach (var p in paths)
        {
            try { await Task.Run(() => WorkspaceFiles.Delete(p, prog)); done.Add(p); }
            catch (Exception e)
            {
                MessageBox.Show(owner, $"{Path.GetFileName(p)} could not be deleted completely:\n{e.Message}\n\nFiles still in use (a running game or an Explorer window) stay; try again after closing them.", "Delete workspace", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        return done;
    }

    /// <summary>Workspaces whose test game is still running: its log in quicktest\xenia is held open by Xenia.</summary>
    static List<string> RunningFrom(List<string> roots)
    {
        var res = new List<string>();
        foreach (var r in roots)
        {
            var log = Path.Combine(r, "quicktest", "xenia", "xenia.log");
            if (!File.Exists(log)) continue;
            try { using var f = new FileStream(log, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { res.Add(Path.GetFileName(r) + ": its Test in Xenia game is still open"); }
            catch (UnauthorizedAccessException) { }
        }
        return res;
    }
}
