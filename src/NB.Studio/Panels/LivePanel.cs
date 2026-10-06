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

    /// <summary>Named teleport targets (game units). The Seattle landmarks come from seattle/gen (geo.py).</summary>
    public static readonly (string Name, Vector3 Pos)[] Bookmarks =
    {
        ("Spawn (Westlake Park)", new(8, 2, 306)), ("Pike Place Market", new(-150, 0, 345)), ("Pier 57 / Great Wheel", new(-173, -17, 513)),
        ("Space Needle", new(-400, -5, -180)), ("Lumen Field", new(170, -17, 1040)), ("T-Mobile Park", new(150, -17, 1200)),
        ("Mumbo's Motors", new(-45, 2, 340)), ("I-5 (north deck)", new(302, 3, -135)), ("Elliott Bay (water)", new(-215, -17, 500)),
    };

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
        var bmRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 68, WrapContents = true };
        foreach (var b in Bookmarks) _marks.Items.Add(b.Name);
        _marks.SelectedIndex = 0;
        var bmGo = new Button { Text = "Teleport", Width = 80 }; bmGo.Click += (_, _) => Teleport(Bookmarks[_marks.SelectedIndex].Pos);
        var toView = new Button { Text = "To 3D-view camera", Width = 130 }; toView.Click += (_, _) => { if (ViewCameraPosition?.Invoke() is Vector3 v) Teleport(v); };
        bmRow.Controls.AddRange(new Control[] { new Label { Text = "Bookmark", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, _marks, bmGo, toView });
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
