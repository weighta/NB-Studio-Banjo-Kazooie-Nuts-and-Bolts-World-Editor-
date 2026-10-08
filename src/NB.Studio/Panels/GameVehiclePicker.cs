using NB.Core.Vehicles;

namespace NB.Studio.Panels;

/// <summary>
/// The Vehicle Editor's Game Vehicles navigator: a small window under the toolbar button with a search box and a tree
/// (the open world's vehicles first, then World › Act › vehicle, then "Other / templates": shop blueprints, demo, test,
/// live and credits vehicles), the part count on every vehicle and the details of the selected one. The list comes from
/// the editor's per-workspace catalog, built in the background when the workspace opens and kept in the workspace cache,
/// so the window opens at once. Browsing: with no unsaved changes in the editor, a single click (or the arrow keys, after a
/// short pause) opens the vehicle at once and the window stays open for the next one; with unsaved changes a click only
/// selects, and a double-click / Enter / Open opens it after the editor's Save / Don't save / Cancel. Esc or × hides it.
/// </summary>
public sealed class GameVehiclePicker : Form
{
    readonly TextBox _search = new() { Dock = DockStyle.Top, PlaceholderText = "Search: driver, challenge, name, asset…" };
    readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false, ShowNodeToolTips = true, FullRowSelect = true };
    readonly Label _info = new() { Dock = DockStyle.Bottom, Height = 92, Padding = new Padding(6), BorderStyle = BorderStyle.FixedSingle, AutoEllipsis = true };
    readonly Label _state = new() { Dock = DockStyle.Bottom, Height = 22, Padding = new Padding(6, 3, 0, 0), ForeColor = SystemColors.GrayText };
    readonly Button _open = new() { Text = "Open", Dock = DockStyle.Right, Width = 90, Enabled = false };
    List<PregameVehicle>? _list;
    (string World, string Act)? _here;
    string _hereLabel = "";

    /// <summary>A vehicle was chosen with a double-click, Enter or Open (with the World / Act it was picked under).</summary>
    public event Action<PregameVehicle, VehiclePlace?>? Picked;
    /// <summary>A vehicle was selected by a single click or the arrow keys while <see cref="CanBrowse"/> says yes.</summary>
    public event Action<PregameVehicle, VehiclePlace?>? Browsed;
    /// <summary>Single-click browsing is on (the editor has no unsaved changes).</summary>
    public Func<bool>? CanBrowse;
    /// <summary>Clicks and arrow keys open after a short pause, so holding Down does not open every vehicle on the way.</summary>
    readonly System.Windows.Forms.Timer _browse = new() { Interval = 220 };
    bool _filling;

    public GameVehiclePicker()
    {
        Text = "Game Vehicles";
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; KeyPreview = true;
        Size = new Size(560, 560); MinimumSize = new Size(360, 300);
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 30, Padding = new Padding(4) };
        bottom.Controls.Add(_state); _state.Dock = DockStyle.Fill; bottom.Controls.Add(_open);
        Controls.Add(_tree); Controls.Add(_info); Controls.Add(bottom); Controls.Add(_search);
        _search.TextChanged += (_, _) => Fill();
        _search.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Down) { _tree.Focus(); e.Handled = true; }
            if (e.KeyCode == Keys.Enter) { var leaf = FirstLeaf(_tree.Nodes); if (leaf != null) { _tree.SelectedNode = leaf; Choose(); } e.SuppressKeyPress = true; }
        };
        _tree.AfterSelect += (_, e) =>
        {
            ShowInfo();
            _browse.Stop();
            if (!_filling && e.Action != TreeViewAction.Unknown && _tree.SelectedNode?.Tag is Leaf && CanBrowse?.Invoke() == true)
            {
                // a click opens at once; the arrow keys after a pause (holding Down skips over the vehicles in between)
                if (e.Action == TreeViewAction.ByMouse) Browse(); else _browse.Start();
            }
        };
        _browse.Tick += (_, _) => { _browse.Stop(); if (CanBrowse?.Invoke() == true) Browse(); };
        _tree.NodeMouseDoubleClick += (_, e) => { if (e.Node?.Tag is Leaf) Choose(); };
        _tree.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { Choose(); e.SuppressKeyPress = true; } };
        _open.Click += (_, _) => Choose();
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { Hide(); e.Handled = true; } };
        // stays open while browsing (it used to hide when the main window was clicked); Esc or × hides it
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }

    sealed record Leaf(PregameVehicle V, VehiclePlace? P);

    /// <summary>The list (null while it is being built), the open world / Act and its name, and a status line.</summary>
    public void SetData(List<PregameVehicle>? list, (string World, string Act)? here, string hereLabel, string state)
    {
        bool same = ReferenceEquals(list, _list) && here == _here;
        _list = list; _here = here; _hereLabel = hereLabel; _state.Text = state;
        if (!same || _tree.Nodes.Count == 0) Fill();
    }

    public string SearchText { get => _search.Text; set => _search.Text = value; }

    /// <summary>The game's order of the worlds (hub first), then the rest by name.</summary>
    static readonly string[] WorldOrder = { "spiralmountain", "showdowntown", "nuttyacres", "cpu", "banjoland", "terrorium", "worldofsport" };
    static int ActSort(string act) => act.StartsWith("act") && int.TryParse(act[3..], out int n) ? n : act == "actww" ? 50 : act == "live" ? 70 : 60;

    /// <summary>The vehicles the picker shows under "Open world: …", in its order.</summary>
    public static List<(PregameVehicle V, VehiclePlace P)> OpenWorldVehicles(List<PregameVehicle> list, (string World, string Act) h) =>
        list.SelectMany(v => v.Places.Where(p => p.World == h.World && (h.Act.Length == 0 || p.Act == h.Act)).Take(1).Select(p => (V: v, P: p)))
            .OrderBy(x => x.P.Challenge.Length == 0 ? 1 : 0).ThenBy(x => x.P.Challenge).ThenBy(x => x.V.Title).ToList();

    /// <summary>The first vehicle the picker lists (first world in the game's order, its first Act, first vehicle).</summary>
    public static (PregameVehicle V, VehiclePlace? P)? FirstListed(List<PregameVehicle> list)
    {
        var first = list.SelectMany(v => v.Places.Select(p => (V: v, P: p)))
            .OrderBy(x => Array.IndexOf(WorldOrder, x.P.World) is int k && k >= 0 ? k : 99).ThenBy(x => x.P.WorldName)
            .ThenBy(x => ActSort(x.P.Act)).ThenBy(x => x.P.Challenge.Length == 0 ? 1 : 0).ThenBy(x => x.P.Challenge).ThenBy(x => x.V.Title)
            .FirstOrDefault();
        if (first.V != null) return (first.V, first.P);
        var rest = list.OrderBy(v => v.Section).ThenBy(v => v.Title).FirstOrDefault();
        return rest == null ? null : (rest, null);
    }

    static string LeafText(PregameVehicle v, VehiclePlace? p)
    {
        string who = v.Title;
        string asset = who == v.Short ? "" : $"  ({v.Short})";
        return $"{(p != null && p.Challenge.Length > 0 ? p.Challenge + " › " : "")}{who}{asset}{(v.Parts > 0 ? $" — {v.Parts} parts" : "")}";
    }

    bool Matches(PregameVehicle v, VehiclePlace? p)
    {
        var q = _search.Text.Trim();
        if (q.Length == 0) return true;
        string hay = $"{v.Title} {v.Short} {v.Friendly} {v.Owner} {v.Section} {string.Join(" ", v.Places.Select(x => x.ToString()))} {string.Join(" ", v.Users)}";
        return q.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => hay.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    TreeNode Node(TreeNodeCollection parent, string text)
    {
        var n = new TreeNode(text);
        parent.Add(n);
        return n;
    }

    void AddLeaf(TreeNode parent, PregameVehicle v, VehiclePlace? p, string? text = null)
    {
        var n = new TreeNode(text ?? LeafText(v, p)) { Tag = new Leaf(v, p), ToolTipText = $"{v.Asset}{(v.Users.Count > 0 ? "\n" + string.Join("\n", v.Users) : "")}" };
        if (v.Id == _openId) { n.ForeColor = OpenColour; n.BackColor = OpenBack; }
        parent.Nodes.Add(n);
    }

    uint _openId;
    static readonly Color OpenColour = Color.FromArgb(0, 100, 30), OpenBack = Color.FromArgb(222, 244, 226);

    /// <summary>The vehicle open in the editor is shown in green (bold text would be clipped by the tree).</summary>
    public void MarkOpen(uint id)
    {
        if (id == _openId) return;
        _openId = id;
        void M(TreeNodeCollection ns) { foreach (TreeNode n in ns) { if (n.Tag is Leaf l) { bool on = l.V.Id == id; n.ForeColor = on ? OpenColour : Color.Empty; n.BackColor = on ? OpenBack : Color.Empty; } M(n.Nodes); } }
        _tree.BeginUpdate(); M(_tree.Nodes); _tree.EndUpdate();
    }

    void Fill()
    {
        _filling = true;
        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        try
        {
            if (_list == null) { _tree.Nodes.Add(new TreeNode("Listing the game's vehicles… (the first time in a workspace takes a few seconds)")); return; }
            bool searching = _search.Text.Trim().Length > 0;
            // the open world / Act first
            if (_here is { } h)
            {
                var mine = OpenWorldVehicles(_list, h).Where(x => Matches(x.V, x.P)).ToList();
                if (mine.Count > 0)
                {
                    var n = Node(_tree.Nodes, $"Open world: {_hereLabel} ({mine.Count})");
                    foreach (var (v, p) in mine) AddLeaf(n, v, p);
                    n.Expand();
                }
            }
            foreach (var w in _list.SelectMany(v => v.Places.Select(p => (V: v, P: p))).Where(x => Matches(x.V, x.P)).GroupBy(x => x.P.World)
                         .OrderBy(g => Array.IndexOf(WorldOrder, g.Key) is int k && k >= 0 ? k : 99).ThenBy(g => g.First().P.WorldName))
            {
                var wn = Node(_tree.Nodes, $"{w.First().P.WorldName} ({w.Count()})");
                foreach (var a in w.GroupBy(x => x.P.Act).OrderBy(g => ActSort(g.Key)))
                {
                    var first = a.First().P;
                    var challenges = a.Select(x => x.P.Challenge).Where(c => c.Length > 0).Distinct().ToList();
                    var an = Node(wn.Nodes, $"{(a.Key.Length == 0 ? "Not in an Act" : first.ActName)}{(challenges.Count > 0 ? " — " + string.Join(", ", challenges) : "")} ({a.Count()})");
                    foreach (var (v, p) in a.OrderBy(x => x.P.Challenge.Length == 0 ? 1 : 0).ThenBy(x => x.P.Challenge).ThenBy(x => x.V.Title)) AddLeaf(an, v, p);
                    if (searching) an.Expand();
                }
                if (searching) wn.Expand();
            }
            // shop blueprints, demo / test / live / credits vehicles and leftovers
            var rest = _list.Where(v => v.Places.Count == 0 && Matches(v, null)).ToList();
            if (rest.Count > 0)
            {
                var on = Node(_tree.Nodes, $"Other / templates ({rest.Count})");
                foreach (var g in rest.GroupBy(v => v.Section.Length > 0 ? v.Section : "Other vehicles").OrderBy(g => g.Key))
                {
                    var sn = Node(on.Nodes, $"{g.Key} ({g.Count()})");
                    foreach (var v in g.OrderBy(v => v.Friendly.Length == 0 ? 1 : 0).ThenBy(v => v.Title))
                        AddLeaf(sn, v, null, $"{v.Title}{(v.Title == v.Short ? "" : $"  ({v.Short})")}{(v.Parts > 0 ? $" — {v.Parts} parts" : "")}");
                    if (searching) sn.Expand();
                }
                if (searching) on.Expand();
            }
            if (_tree.Nodes.Count == 0) _tree.Nodes.Add(new TreeNode("No vehicle matches the search."));
        }
        finally { _tree.EndUpdate(); _filling = false; }
        ShowInfo();
    }

    static TreeNode? FirstLeaf(TreeNodeCollection nodes)
    {
        foreach (TreeNode n in nodes)
        {
            if (n.Tag is Leaf) return n;
            if (FirstLeaf(n.Nodes) is { } l) return l;
        }
        return null;
    }

    void ShowInfo()
    {
        if (_tree.SelectedNode?.Tag is not Leaf l) { _info.Text = "Pick a vehicle: Enter, double-click or Open loads it into the editor (Save to Game writes it back)."; _open.Enabled = false; return; }
        var v = l.V;
        _open.Enabled = true;
        _info.Text = $"{v.Title}{(v.Friendly.Length > 0 && v.Friendly != v.Title ? $" — {v.Friendly}" : "")}   {v.Parts} parts\n" +
                     $"{(v.Places.Count > 0 ? string.Join("; ", v.Places.Select(p => p.ToString())) : v.Section)}\n" +
                     $"{v.Asset} in {string.Join(", ", v.Bundles.Select(b => b.ToString("x6")))}\n" +
                     string.Join("\n", v.Users.Take(3));
    }

    void Choose()
    {
        _browse.Stop();
        if (_tree.SelectedNode?.Tag is not Leaf l) return;
        Picked?.Invoke(l.V, l.P);
    }

    void Browse()
    {
        if (_tree.SelectedNode?.Tag is Leaf l) Browsed?.Invoke(l.V, l.P);
    }

    /// <summary>The status line under the tree (the list's state, and how clicks open now).</summary>
    public void SetHint(string state, bool dirty)
    {
        _state.Text = dirty ? "Unsaved changes: a click selects; double-click or Enter asks to save first." : state + "  ·  click a vehicle to open it";
        _state.ForeColor = dirty ? Color.FromArgb(170, 90, 0) : SystemColors.GrayText;
    }

    /// <summary>Scripted tests: posts a real mouse click (or double-click) to the tree at the vehicle's node.</summary>
    public bool PostClick(string q, bool dbl)
    {
        TreeNode? F(TreeNodeCollection ns)
        {
            foreach (TreeNode n in ns)
            {
                if (n.Tag is Leaf l && (l.V.Short.Contains(q, StringComparison.OrdinalIgnoreCase) || l.V.Title.Contains(q, StringComparison.OrdinalIgnoreCase))) return n;
                if (F(n.Nodes) is { } x) return x;
            }
            return null;
        }
        var hit = F(_tree.Nodes);
        if (hit == null) return false;
        hit.EnsureVisible(); Application.DoEvents();
        var b = hit.Bounds;
        IntPtr xy = (IntPtr)(((b.Top + b.Height / 2) & 0xFFFF) << 16 | ((b.Left + 8) & 0xFFFF));
        Post(_tree.Handle, 0x201, (IntPtr)1, xy); Post(_tree.Handle, 0x202, IntPtr.Zero, xy);
        if (dbl) { Post(_tree.Handle, 0x203, (IntPtr)1, xy); Post(_tree.Handle, 0x202, IntPtr.Zero, xy); }
        Application.DoEvents();
        return true;
    }

    /// <summary>Scripted tests: a key message to the tree (Down, Up, Return).</summary>
    public void PostKey(Keys k)
    {
        Post(_tree.Handle, 0x100, (IntPtr)(int)k, (IntPtr)1); Post(_tree.Handle, 0x101, (IntPtr)(int)k, unchecked((IntPtr)(int)0xC0000001));
        Application.DoEvents();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "PostMessage")] static extern bool Post(IntPtr h, int msg, IntPtr w, IntPtr l);

    public string SelectedText => _tree.SelectedNode?.Text ?? "";

    /// <summary>Scripted tests: the tree's text (expanded nodes indented).</summary>
    public IEnumerable<string> Dump()
    {
        IEnumerable<string> D(TreeNodeCollection ns, string ind)
        {
            foreach (TreeNode n in ns)
            {
                yield return ind + n.Text;
                foreach (var s in D(n.Nodes, ind + "  ")) yield return s;
            }
        }
        return D(_tree.Nodes, "");
    }

    /// <summary>Scripted tests: selects the first vehicle whose asset or title contains <paramref name="q"/>, expanding to it.</summary>
    public bool SelectVehicle(string q)
    {
        TreeNode? F(TreeNodeCollection ns)
        {
            foreach (TreeNode n in ns)
            {
                if (n.Tag is Leaf l && (l.V.Short.Contains(q, StringComparison.OrdinalIgnoreCase) || l.V.Title.Contains(q, StringComparison.OrdinalIgnoreCase))) return n;
                if (F(n.Nodes) is { } x) return x;
            }
            return null;
        }
        var hit = F(_tree.Nodes);
        if (hit == null) return false;
        hit.EnsureVisible(); _tree.SelectedNode = hit;
        return true;
    }

    public void ChooseSelected() => Choose();
}
