using System.Numerics;
using System.Runtime.InteropServices;
using NB.Core.Live;

namespace NB.Studio.Panels;

/// <summary>
/// Runtime mod menu ("Live"): attaches to the running Xenia and shows / changes game state while the game plays —
/// live coordinates, teleport (vehicle or Banjo on foot, typed coordinates, bookmarks, "to 3D-view camera"), gravity,
/// free-fly photo camera (noclip view) and a jump into the 3D view at the player's position.
/// Every write uses operations verified in Xenia (NB.Core.Live.XeniaLive).
/// </summary>
public sealed class LivePanel : UserControl
{
    readonly Button _attach = new() { Text = "Attach to Xenia", Width = 130 };
    readonly Label _state = new() { AutoSize = true, Padding = new Padding(4, 8, 0, 0) };
    readonly Label _pos = new() { Dock = DockStyle.Top, Height = 64, Font = new Font("Consolas", 10), Padding = new Padding(6) };
    readonly NumericUpDown[] _tp = new NumericUpDown[3];
    readonly CheckBox _foot = new() { Text = "Banjo on foot", AutoSize = true };
    readonly ComboBox _marks = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    readonly TrackBar _grav = new() { Minimum = 0, Maximum = 100, Value = 25, TickFrequency = 5, Width = 240 };
    readonly Label _gravLbl = new() { AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
    readonly NumericUpDown _step = new() { Minimum = 1, Maximum = 500, Value = 20, Width = 70 };
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    XeniaLive? _x;
    uint _world;
    readonly Func<byte[]?> _probe;
    public event Action<Vector3>? ShowInView;
    public Func<Vector3?>? ViewCameraPosition;
    public event Action<string>? Log;

    /// <summary>Teleport targets of the open world: its player start markers (from the scene).</summary>
    public Func<IEnumerable<(string Name, Vector3 Pos)>>? WorldBookmarks;
    /// <summary>Folder of the open workspace (bookmarks you add are kept there, per world) and a key for the open world.</summary>
    public Func<string?>? WorkspaceDir;
    public Func<string?>? WorldKey;
    readonly List<(string Name, Vector3 Pos, bool Mine)> _bookmarks = new();

    sealed record SavedBookmark(string World, string Name, float X, float Y, float Z);
    string? BookmarkFile => WorkspaceDir?.Invoke() is string d ? Path.Combine(d, "studio-bookmarks.json") : null;

    List<SavedBookmark> LoadSaved()
    {
        try { if (BookmarkFile is string f && File.Exists(f)) return System.Text.Json.JsonSerializer.Deserialize<List<SavedBookmark>>(File.ReadAllText(f)) ?? new(); }
        catch (Exception e) { Log?.Invoke("Live: bookmarks could not be read: " + e.Message); }
        return new();
    }

    void SaveSaved(List<SavedBookmark> l)
    {
        if (BookmarkFile is not string f) return;
        File.WriteAllText(f, System.Text.Json.JsonSerializer.Serialize(l, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Refills the bookmark list: the open world's player starts, then the bookmarks you added for this world.</summary>
    public void RefreshBookmarks()
    {
        _bookmarks.Clear();
        try { foreach (var (n, p) in WorldBookmarks?.Invoke() ?? Enumerable.Empty<(string, Vector3)>()) _bookmarks.Add((n, p, false)); } catch (Exception) { }
        var world = WorldKey?.Invoke();
        foreach (var b in LoadSaved().Where(b => b.World == world)) _bookmarks.Add((b.Name, new(b.X, b.Y, b.Z), true));
        _marks.Items.Clear();
        foreach (var b in _bookmarks) _marks.Items.Add(b.Mine ? b.Name : b.Name + "  (marker)");
        if (_marks.Items.Count == 0) _marks.Items.Add("(no bookmarks: open a world, or Add)");
        _marks.SelectedIndex = 0;
    }

    void AddBookmark()
    {
        if (_x == null) { Log?.Invoke("Live: attach to the game first, then Add saves where the vehicle (or Banjo on foot) is."); return; }
        if (WorkspaceDir?.Invoke() == null || WorldKey?.Invoke() is not string world) { Log?.Invoke("Live: open a workspace and a world first."); return; }
        var pos = CurrentPos();
        using var dlg = new Form { Text = "Add Bookmark", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, Width = 360, Height = 140, MinimizeBox = false, MaximizeBox = false };
        var box = new TextBox { Left = 12, Top = 12, Width = 320, Text = $"Bookmark {pos.X:F0}, {pos.Y:F0}, {pos.Z:F0}" };
        var ok = new Button { Text = "Add", Left = 176, Top = 48, Width = 75, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 257, Top = 48, Width = 75, DialogResult = DialogResult.Cancel };
        dlg.Controls.AddRange(new Control[] { box, ok, cancel }); dlg.AcceptButton = ok; dlg.CancelButton = cancel;
        if (dlg.ShowDialog(this) != DialogResult.OK || box.Text.Trim().Length == 0) return;
        var l = LoadSaved(); l.Add(new SavedBookmark(world, box.Text.Trim(), pos.X, pos.Y, pos.Z)); SaveSaved(l);
        RefreshBookmarks(); _marks.SelectedIndex = _marks.Items.Count - 1;
        Log?.Invoke($"Live: bookmark \"{box.Text.Trim()}\" saved at {pos} (this workspace, this world).");
    }

    void RemoveBookmark()
    {
        int i = _marks.SelectedIndex;
        if (i < 0 || i >= _bookmarks.Count || !_bookmarks[i].Mine) { Log?.Invoke("Live: only bookmarks you added can be removed (marker bookmarks come from the world)."); return; }
        var world = WorldKey?.Invoke(); var (name, pos, _) = _bookmarks[i];
        var l = LoadSaved();
        int k = l.FindIndex(b => b.World == world && b.Name == name && new Vector3(b.X, b.Y, b.Z) == pos);
        if (k >= 0) { l.RemoveAt(k); SaveSaved(l); }
        RefreshBookmarks();
    }

    public LivePanel(Func<byte[]?> textProbe)
    {
        _probe = textProbe;
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40 };
        top.Controls.AddRange(new Control[] { _attach, _state });
        var tpRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 68, WrapContents = true };
        tpRow.Controls.Add(new Label { Text = "Teleport to", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
        for (int i = 0; i < 3; i++) { _tp[i] = new NumericUpDown { Minimum = -5000, Maximum = 5000, DecimalPlaces = 1, Width = 80 }; tpRow.Controls.Add(_tp[i]); }
        var go = new Button { Text = "Go", Width = 50 }; go.Click += (_, _) => Teleport(new((float)_tp[0].Value, (float)_tp[1].Value, (float)_tp[2].Value));
        var here = new Button { Text = "Use current", Width = 90 }; here.Click += (_, _) => { if (_x != null) SetTp(_x.PlayerPosition); };
        tpRow.Controls.AddRange(new Control[] { go, here, _foot });
        var bmRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 72, WrapContents = true };
        _marks.DropDownStyle = ComboBoxStyle.DropDownList;
        _marks.DropDown += (_, _) => RefreshBookmarks();
        var bmGo = new Button { Text = "Teleport", Width = 80 };
        bmGo.Click += (_, _) => { int i = _marks.SelectedIndex; if (i >= 0 && i < _bookmarks.Count) Teleport(_bookmarks[i].Pos); };
        var bmAdd = new Button { Text = "Add", Width = 50 }; bmAdd.Click += (_, _) => AddBookmark();
        var bmDel = new Button { Text = "Remove", Width = 65 }; bmDel.Click += (_, _) => RemoveBookmark();
        var toView = new Button { Text = "To 3D-view camera", Width = 130 }; toView.Click += (_, _) => { if (ViewCameraPosition?.Invoke() is Vector3 v) Teleport(v); };
        bmRow.Controls.AddRange(new Control[] { new Label { Text = "Bookmark", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, _marks, bmGo, bmAdd, bmDel, toView });
        var nudgeRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 68, WrapContents = true };
        nudgeRow.Controls.Add(new Label { Text = "Nudge by", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
        nudgeRow.Controls.Add(_step);
        foreach (var (t, d) in new[] { ("+X", Vector3.UnitX), ("-X", -Vector3.UnitX), ("+Y (up)", Vector3.UnitY), ("-Y", -Vector3.UnitY), ("+Z", Vector3.UnitZ), ("-Z", -Vector3.UnitZ) })
        {
            var b = new Button { Text = t, Width = 60 }; var dir = d;
            b.Click += (_, _) => { if (_x != null) Teleport(CurrentPos() + dir * (float)_step.Value); };
            nudgeRow.Controls.Add(b);
        }
        var gRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 76, WrapContents = true };
        var gReset = new Button { Text = "Normal (25)", Width = 90 };
        var gMoon = new Button { Text = "Moon (4)", Width = 80 };
        var gZero = new Button { Text = "Zero-G", Width = 70 };
        _grav.Scroll += (_, _) => SetGravity(-_grav.Value);
        gReset.Click += (_, _) => { _grav.Value = 25; SetGravity(-25); };
        gMoon.Click += (_, _) => { _grav.Value = 4; SetGravity(-4); };
        gZero.Click += (_, _) => { _grav.Value = 0; SetGravity(0); };
        gRow.Controls.AddRange(new Control[] { new Label { Text = "Gravity", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, _grav, _gravLbl, gReset, gMoon, gZero });
        var camRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36 };
        var camHere = new Button { Text = "Photo camera → 3D-view camera", Width = 210 };
        camHere.Click += (_, _) =>
        {
            if (_x == null) return;
            if (!_x.PhotoModeOpen) { Log?.Invoke("Live: open photo mode in the game first (pause → Take Photo)."); return; }
            if (ViewCameraPosition?.Invoke() is Vector3 v) { _x.SetPhotoCamera(v); Log?.Invoke($"Live: photo camera moved to {v} (free flight, passes through walls)."); }
        };
        var show = new Button { Text = "Show player in 3D view", Width = 160 };
        show.Click += (_, _) => { if (_x != null) ShowInView?.Invoke(CurrentPos()); };
        camRow.Controls.AddRange(new Control[] { camHere, show });
        var help = new Label
        {
            Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText, Padding = new Padding(6),
            Text = "Runtime tools for the game running in Xenia (NB Studio's Build > Launch). Teleport moves the vehicle's rigid bodies, or " +
                   "Banjo's character body when 'on foot' is ticked. Gravity changes the Havok world's gravity (normal −25). " +
                   "The photo camera flies through walls (noclip view). Changes last until the level reloads.\n" +
                   "Also on the Mods menu: part limit, build area, draw distance (LOD ×4), world bounds, developer menus.",
        };
        Controls.AddRange(new Control[] { help, camRow, gRow, nudgeRow, bmRow, tpRow, _pos, top });
        _attach.Click += (_, _) => Attach();
        // opening the tab attaches by itself when a game is running (F5 starts NB's Xenia build, not xenia_canary.exe)
        VisibleChanged += (_, _) => { if (Visible) RefreshBookmarks(); };
        VisibleChanged += (_, _) => { if (Visible && _x == null && System.Linq.Enumerable.Any(XeniaLive.GameProcessNames, n => System.Diagnostics.Process.GetProcessesByName(n).Length > 0)) Attach(); };
        _timer.Tick += (_, _) => Poll();
        SetEnabled(false);
    }

    void SetTp(Vector3 v) { _tp[0].Value = (decimal)Math.Clamp(v.X, -5000, 5000); _tp[1].Value = (decimal)Math.Clamp(v.Y, -5000, 5000); _tp[2].Value = (decimal)Math.Clamp(v.Z, -5000, 5000); }
    Vector3 CurrentPos() => _x == null ? default : _foot.Checked ? _x.CameraPosition - new Vector3(0, 3, 0) : _x.PlayerPosition;

    void SetEnabled(bool on) { foreach (Control c in Controls) if (c != _pos && c is not Label && c.Controls.Count > 0 && !c.Controls.Contains(_attach)) c.Enabled = on; }

    /// <summary>The game NB Studio started last (Test in Xenia): attached first when several Xenia windows run.</summary>
    public int? PreferPid { get; set; }

    void Attach()
    {
        try
        {
            _x?.Dispose(); _x = null;
            var probe = _probe() ?? throw new InvalidOperationException("open a workspace first");
            _x = XeniaLive.Attach(probe, PreferPid is int pp && !System.Diagnostics.Process.GetProcesses().All(q => q.Id != pp) ? pp : null);
            _world = _x.FindHavokWorld();
            if (_world != 0) { int g = (int)MathF.Round(-_x.GetGravity(_world)); _grav.Value = Math.Clamp(g, 0, 100); }
            _state.Text = $"attached (pid {_x.Pid}){(_world == 0 ? "; no level loaded yet" : "")}";
            SetTp(_x.PlayerPosition);
            SetEnabled(true); _timer.Start();
            Log?.Invoke($"Live: attached to Xenia pid {_x.Pid}, guest base 0x{_x.Base:X}, hkpWorld 0x{_world:X8}.");
        }
        catch (Exception e) { _state.Text = e.Message; SetEnabled(false); }
    }

    void Poll()
    {
        if (_x == null) return;
        try
        {
            var p = _x.PlayerPosition; var c = _x.CameraPosition;
            _pos.Text = $"vehicle / player  X {p.X,9:F2}  Y {p.Y,8:F2}  Z {p.Z,9:F2}\ncamera            X {c.X,9:F2}  Y {c.Y,8:F2}  Z {c.Z,9:F2}";
            if (_world == 0 && (_world = _x.FindHavokWorld()) != 0) _state.Text = $"attached (pid {_x.Pid})";
        }
        catch { _timer.Stop(); _state.Text = "Xenia closed"; _x = null; SetEnabled(false); }
    }

    void Teleport(Vector3 v)
    {
        if (_x == null) return;
        try
        {
            int n = _foot.Checked ? _x.TeleportFoot(v, NudgeStick) : _x.TeleportVehicle(v);
            Log?.Invoke(n > 0 ? $"Live: teleported to {v} ({(_foot.Checked ? "Banjo" : $"{n} vehicle body(ies)")})."
                              : _foot.Checked ? "Live: Banjo's body was not found — stand in open space and try again." : "Live: no vehicle body found (get into the vehicle, or tick 'Banjo on foot').");
        }
        catch (Exception e) { Log?.Invoke("Live: teleport failed: " + e.Message); }
    }

    void SetGravity(float g)
    {
        _gravLbl.Text = $"{g:F0}";
        if (_x == null) return;
        if (_world == 0) _world = _x.FindHavokWorld();
        if (_world == 0) { Log?.Invoke("Live: no Havok world (load a level first)."); return; }
        _x.SetGravity(_world, g);
    }

    // stick nudge (toward the camera) posted to the Xenia window, used to identify Banjo's body
    [DllImport("user32")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32")] static extern uint MapVirtualKey(uint code, uint map);
    void NudgeStick()
    {
        var p = System.Diagnostics.Process.GetProcessById(_x!.Pid);
        IntPtr h = p.MainWindowHandle; const uint vk = 'S';   // left stick down (keyboard HID profile: WASD)
        uint sc = MapVirtualKey(vk, 0);
        PostMessage(h, 0x100, (IntPtr)vk, (IntPtr)(1 | (sc << 16)));
        Thread.Sleep(400);
        PostMessage(h, 0x101, (IntPtr)vk, unchecked((IntPtr)(int)(1 | (sc << 16) | 0xC0000000)));
        Thread.Sleep(300);
    }

    // script hooks (NBModStudio --live-attach / --live-tp / --live-gravity), same code paths as the buttons
    public void ScriptAttach() => Attach();
    public void ScriptTeleport(Vector3 v) => Teleport(v);
    public void ScriptGravity(float g) { _grav.Value = Math.Clamp((int)-g, 0, 100); SetGravity(g); }
    public void ScriptShow() { if (_x != null) ShowInView?.Invoke(CurrentPos()); }
    public string StateText => _state.Text;
    public string PositionText { get { Poll(); return _pos.Text.Replace("\n", " | "); } }

    protected override void Dispose(bool disposing) { if (disposing) { _timer.Dispose(); _x?.Dispose(); } base.Dispose(disposing); }
}
