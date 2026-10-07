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
    readonly TrackBar _grav = new() { Minimum = 0, Maximum = 100, Value = 25, TickFrequency = 5, LargeChange = 5, Width = 240 };
    readonly Label _gravLbl = new() { AutoSize = true, Padding = new Padding(0, 8, 0, 0), Text = "25" };
    /// <summary>The gravity the game has now (read from its Havok world every 250 ms), or why it can't be changed yet.</summary>
    readonly Label _gravGame = new() { AutoSize = true, Padding = new Padding(0, 4, 0, 0), Text = "Game gravity: attach to the game first (Attach to Xenia, or start it with F5)" };
    /// <summary>The gravity set here (kept when the level reloads, e.g. after the garage or another Act), null: the game's own.</summary>
    float? _wanted;
    uint _keptFor;
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
        var gRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 104, WrapContents = true };
        var gReset = new Button { Text = "Normal (25)", Width = 90 };
        var gMoon = new Button { Text = "Moon (4)", Width = 80 };
        var gZero = new Button { Text = "Zero-G", Width = 70 };
        var gHigh = new Button { Text = "Heavy (60)", Width = 80 };
        _grav.Scroll += (_, _) => SetGravity(-_grav.Value);   // drag the slider: the game changes at once
        gReset.Click += (_, _) => { _grav.Value = 25; SetGravity(-25); };
        gMoon.Click += (_, _) => { _grav.Value = 4; SetGravity(-4); };
        gZero.Click += (_, _) => { _grav.Value = 0; SetGravity(0); };
        gHigh.Click += (_, _) => { _grav.Value = 60; SetGravity(-60); };
        var gTip = new ToolTip();
        gTip.SetToolTip(_grav, "Downward pull of the game's physics world, 0 (none) to 100; the game's normal is 25. Drag it while you drive.");
        gRow.Controls.AddRange(new Control[] { new Label { Text = "Gravity (down pull)", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, _grav, _gravLbl, gReset, gMoon, gZero, gHigh });
        gRow.SetFlowBreak(gHigh, true);
        gRow.Controls.Add(_gravGame);
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
                   "Banjo's character body when 'on foot' is ticked. Gravity: drag the slider or click a preset while you are in a level " +
                   "(25 is the game's normal; 'Game gravity now' shows what the game uses; your setting is applied again after the garage or another Act). " +
                   "The photo camera flies through walls (noclip view). Teleports last until the level reloads.\n" +
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
            _wanted = null;
            if (_world != 0) ShowGameGravity(true);
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
            // the level's physics world: a new one after every level load (the garage, another Act, a reload); 1.16 kept
            // the first one it found, so gravity written later went to a world no longer used
            uint lw = _x.LevelHavokWorld();
            if (lw != 0 && lw != _world) { _world = lw; _state.Text = $"attached (pid {_x.Pid})"; }
            else if (lw == 0 && _world != 0 && !_x.IsHavokWorld(_world)) _world = 0;
            // the gravity set here is kept: a level sets its own (25) a moment after it loads, so it is written again
            // whenever the game's value differs (Normal (25) gives the game its own value back)
            if (_world != 0 && _wanted is float w && MathF.Abs(_x.GetGravity(_world) - w) > 0.01f)
            {
                _x.SetGravity(_world, w);
                if (_keptFor != _world) Log?.Invoke($"Live: a level loaded and set its own gravity; your gravity {-w:0.#} is set again.");
                _keptFor = _world;
            }
            ShowGameGravity(false);
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
        _gravLbl.Text = $"{-g:0.#}";
        if (_x == null) { _gravGame.Text = "Game gravity: not attached — click Attach to Xenia (or start the game with F5) first"; return; }
        if (_world == 0 || !_x.IsHavokWorld(_world)) _world = _x.FindHavokWorld();
        if (_world == 0)
        {
            _gravGame.Text = "Game gravity: the game's physics world (hkpWorld) isn't there yet — load a level (drive in a world), then set it again";
            Log?.Invoke("Live: gravity not changed: no level is loaded in the game yet (the Havok world was not found).");
            return;
        }
        _x.SetGravity(_world, g);
        _wanted = MathF.Abs(g + 25) < 0.01f ? null : g;   // the normal value: follow the game again
        _keptFor = _world;
        ShowGameGravity(false);
    }

    /// <summary>Shows the gravity the game has now (and moves the slider to it when <paramref name="slider"/>).</summary>
    void ShowGameGravity(bool slider)
    {
        if (_x == null) return;
        if (_world == 0) { _gravGame.Text = "Game gravity: no level loaded yet (the Havok world appears when you are in a world)"; return; }
        float g = _x.GetGravity(_world);
        if (slider || (_wanted == null && !_grav.Focused)) { _grav.Value = Math.Clamp((int)MathF.Round(-g), 0, 100); _gravLbl.Text = $"{-g:0.#}"; }
        string t = $"Game gravity now: {-g:0.#}" + (MathF.Abs(g + 25) < 0.05f ? " (normal)" : MathF.Abs(g) < 0.05f ? " (zero-G)" : "")
            + (_wanted != null ? " — set here; kept when a level loads (Normal (25) gives it back to the game)" : "");
        if (_gravGame.Text != t) _gravGame.Text = t;
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
    /// <summary>Script runs attach only to the game this NB Studio started (never to another Xenia on the PC).</summary>
    public void ScriptAttach()
    {
        if (PreferPid is not int pid) { _state.Text = "script: no test game started"; return; }
        try
        {
            _x?.Dispose(); _x = null;
            _x = XeniaLive.AttachPid(pid, _probe() ?? throw new InvalidOperationException("open a workspace first"));
            _x.Dispose(); _x = null;
        }
        catch (Exception e) { _state.Text = e.Message; return; }
        Attach();
        if (_x != null && _x.Pid != pid) { _x.Dispose(); _x = null; _state.Text = "script: attached to another game; detached"; }
    }
    public void ScriptTeleport(Vector3 v) => Teleport(v);
    public void ScriptGravity(float g) { _grav.Value = Math.Clamp((int)-g, 0, 100); SetGravity(g); }
    public string GravityText => _gravGame.Text;
    public void ScriptShow() { if (_x != null) ShowInView?.Invoke(CurrentPos()); }
    /// <summary>Script research helper: guest memory at an address expression ("82FACA44", "[82FACA44]+10", nested
    /// brackets = pointer reads), <paramref name="len"/> bytes as hex lines of 16.</summary>
    public string ScriptDump(string expr, int len)
    {
        if (_x == null) return "not attached";
        uint Eval(string e)
        {
            e = e.Trim();
            int plus = -1, depth = 0;
            for (int i = e.Length - 1; i >= 0; i--) { if (e[i] == ']') depth++; else if (e[i] == '[') depth--; else if (e[i] == '+' && depth == 0) { plus = i; break; } }
            if (plus > 0) return Eval(e[..plus]) + Convert.ToUInt32(e[(plus + 1)..], 16);
            if (e.StartsWith("[") && e.EndsWith("]")) return _x.U32(Eval(e[1..^1]));
            return Convert.ToUInt32(e, 16);
        }
        uint a = Eval(expr);
        var d = _x.Read(a, len);
        var sb = new System.Text.StringBuilder($"{expr} = 0x{a:X8}:");
        for (int o = 0; o < d.Length; o += 16) sb.Append($"\n  {a + o:X8}: {Convert.ToHexString(d, o, Math.Min(16, d.Length - o))}");
        return sb.ToString();
    }
    public string ScriptProbe() => _x == null ? "not attached" : $"{_x.DescribeHavokWorld()}; FindHavokWorld 0x{_x.FindHavokWorld():X8}" + (_x.FindHavokWorld() is uint w && w != 0 ? $" gravity {_x.GetGravity(w)}" : "");
    /// <summary>Teleports the vehicle <paramref name="h"/> up and times its fall (Y sampled every 20 ms) until it stops
    /// falling: the fall time and the gravity it implies.</summary>
    public async Task<string> ScriptFall(float h)
    {
        if (_x == null) return "not attached";
        var p0 = _x.PlayerPosition;
        _x.TeleportVehicle(p0 + new Vector3(0, h, 0));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ys = new List<(double T, float Y)>();
        float top = p0.Y + h; double tLand = -1;
        while (sw.Elapsed.TotalSeconds < 12)
        {
            await Task.Delay(20);
            float y = _x.PlayerPosition.Y; double t = sw.Elapsed.TotalSeconds;
            ys.Add((t, y));
            if (y < top - h * 0.9f) { tLand = t; break; }
        }
        // free fall: 0.9 h = g t² / 2
        return tLand > 0.1 ? $"dropped {h} from {top:0.0}: 90% of the drop in {tLand:0.00} s (free fall with gravity {2 * 0.9 * h / (tLand * tLand):0.0})"
                           : tLand > 0 ? $"dropped {h}: the vehicle was not lifted (no vehicle body under the player?)" : $"dropped {h} from {top:0.0}: did not come down in 12 s";
    }
    public string StateText => _state.Text;
    public string PositionText { get { Poll(); return _pos.Text.Replace("\n", " | "); } }

    protected override void Dispose(bool disposing) { if (disposing) { _timer.Dispose(); _x?.Dispose(); } base.Dispose(disposing); }
}
