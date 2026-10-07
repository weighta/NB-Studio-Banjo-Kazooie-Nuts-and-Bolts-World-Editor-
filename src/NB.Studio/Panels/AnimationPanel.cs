using System.Diagnostics;
using System.Numerics;
using NB.Core.Models;
using NB.Core.Project;
using NB.Core.Textures;
using NB.Core.World;
using NB.Studio.Viewport;

namespace NB.Studio.Panels;

/// <summary>
/// "Animations" tab: the animations the selected character can play in this world — the actions of its animtable
/// (objparams +0xD0) with their animations — played on the character in the 3D view (play / pause, loop, speed, scrub)
/// and exported as FBX (model, skeleton, skin and the animation, the existing exporter).
/// </summary>
public sealed class AnimationPanel : UserControl
{
    readonly Label _title = new() { Font = Ui.Title, AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 0, 0, 2) };
    readonly Label _info = Ui.Note("", 420);
    readonly ListView _list = new() { Dock = DockStyle.Fill, View = System.Windows.Forms.View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, BorderStyle = BorderStyle.None };
    readonly Button _play = Ui.Primary("▶ Play"), _stop = Ui.Plain("■ Stop"), _export = Ui.Plain("Export FBX…");
    readonly CheckBox _loop = new() { Text = "Loop", Checked = true, AutoSize = true, Margin = new Padding(8, 6, 0, 0) };
    readonly ComboBox _speed = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 64, Margin = new Padding(6, 3, 0, 0) };
    readonly TrackBar _scrub = new() { TickStyle = TickStyle.None, AutoSize = false, Height = 26, Dock = DockStyle.Fill, Minimum = 0, Maximum = 1 };
    readonly Label _time = new() { AutoSize = false, Width = 110, TextAlign = ContentAlignment.MiddleRight, Font = Ui.Small, ForeColor = Ui.Subtle, Dock = DockStyle.Right };
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
    readonly Stopwatch _clock = new();

    WorldScene? _scene; SceneObject? _obj; AssetIndex? _index;
    CharacterAnims? _set; AnimAction? _cur; AnimAsset? _anim;
    Matrix4x4[]? _invBind;
    float _t; double _lastClock; bool _playing, _quiet;
    public SceneViewport? View;
    public Action<string>? Log;

    public AnimationPanel()
    {
        BackColor = Color.White;
        var head = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(10, 8, 8, 4) };
        head.Controls.Add(_title); head.Controls.Add(_info);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(8, 0, 8, 2) };
        foreach (var s in new[] { "0.25×", "0.5×", "1×", "2×" }) _speed.Items.Add(s);
        _speed.SelectedIndex = 2;
        bar.Controls.AddRange(new Control[] { _play, _stop, _loop, _speed, _export });
        var scrubRow = new Panel { Dock = DockStyle.Top, Height = 30, Padding = new Padding(8, 2, 8, 2) };
        scrubRow.Controls.Add(_scrub); scrubRow.Controls.Add(_time);
        _list.Columns.Add("Action", 150); _list.Columns.Add("Animation", 190); _list.Columns.Add("Length", 60, HorizontalAlignment.Right);
        Controls.Add(_list); Controls.Add(scrubRow); Controls.Add(bar); Controls.Add(head);
        _list.SelectedIndexChanged += (_, _) => { if (!_quiet) Pick(_list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as AnimAction : null, play: true); };
        _play.Click += (_, _) => { if (_anim == null) return; if (_playing) Pause(); else Play(); };
        _stop.Click += (_, _) => StopToBind();
        _export.Click += (_, _) => Export(null);
        _scrub.Scroll += (_, _) => { if (_anim == null) return; Pause(); _t = _scrub.Value / AnimAsset.Fps; ApplyPose(); };
        _timer.Tick += (_, _) => Tick();
        new ToolTip().SetToolTip(_stop, "Stop: the character goes back to its idle pose (View > Characters in Idle Pose; else the stored bind pose)");
        new ToolTip().SetToolTip(_export, "The character with its skeleton, skin and the selected animation as FBX (30 fps), textures as PNG next to it");
        Show(null, null, null);
    }

    float Speed => _speed.SelectedIndex switch { 0 => 0.25f, 1 => 0.5f, 3 => 2f, _ => 1f };
    public int ActionCount => _set?.Actions.Count ?? 0;

    /// <summary>The selected object (null: nothing / not a character).</summary>
    public void Show(WorldScene? scene, SceneObject? o, AssetIndex? index)
    {
        if (o == _obj && scene == _scene && o != null) return;
        // nothing selected: the last character keeps its animation (and the tab its list) until another object is picked
        if (o == null && _set != null && _obj != null && View?.Scene == _scene && _scene != null) return;
        StopToBind();
        _scene = scene; _obj = o; _index = index; _set = null; _cur = null; _anim = null; _invBind = null;
        _quiet = true; _list.Items.Clear(); _quiet = false;
        if (scene != null && o != null && index != null)
            try { _set = CharacterAnims.For(scene, o, index); } catch (Exception e) { Log?.Invoke("Animations: " + e.Message); }
        if (_set == null)
        {
            _title.Text = "Animations";
            _info.Text = o == null ? "Select a character in the 3D view (a marker that places an actor) to see and play its animations." : $"{o.Name}: not a character with an animation table.";
            UpdateButtons(); return;
        }
        _title.Text = $"Animations — {Short(_set.ObjParams.Replace("aid_objparams_banjox_", ""))}";
        _info.Text = $"{_set.Actions.Count} actions in {_set.AnimTable} (objparams +0xD0); model {_set.ModelName} with {_set.Skeleton?.Count ?? 0} joints. Pick an action to play it on the character.";
        if (_set.Skeleton != null) _invBind = CharacterAnims.BindMatrices(_set.Skeleton).Select(m => Matrix4x4.Invert(m, out var inv) ? inv : Matrix4x4.Identity).ToArray();
        _quiet = true;
        foreach (var a in _set.Actions)
            _list.Items.Add(new ListViewItem(new[] { a.Action, a.AnimName.Replace("aid_anim_banjox_", ""), "" }) { Tag = a });
        _quiet = false;
        UpdateButtons();
    }

    static string Short(string s) => s.Length > 40 ? s[..40] + "…" : s;

    void Pick(AnimAction? a, bool play)
    {
        Pause();
        _cur = a; _anim = a != null && _set != null ? _set.Load(a) : null;
        if (a != null && _anim == null) Log?.Invoke($"Animations: {a.AnimName} could not be read");
        if (_anim != null && _set?.Skeleton != null && _anim.Tracks != _set.Skeleton.Count)
            Log?.Invoke($"Animations: {a!.AnimName} has {_anim.Tracks} tracks for {_set.Skeleton.Count} joints (played by index)");
        foreach (ListViewItem it in _list.Items)
            if (it.Tag == a && _anim != null) it.SubItems[2].Text = $"{_anim.Duration:0.00} s";
        _scrub.Maximum = Math.Max(1, (_anim?.Frames ?? 2) - 1); _scrub.Value = 0;
        _t = 0;
        if (_anim != null) { ApplyPose(); if (play) Play(); }
        UpdateButtons();
    }

    void Play()
    {
        if (_anim == null) return;
        if (_t >= Duration - 1e-3f) _t = 0;
        _playing = true; _clock.Restart(); _lastClock = 0; _timer.Start(); UpdateButtons();
    }

    void Pause() { _playing = false; _timer.Stop(); UpdateButtons(); }

    float Duration => _anim == null ? 0 : Math.Max(1, _anim.Frames - 1) / AnimAsset.Fps;

    void Tick()
    {
        if (!_playing || _anim == null) return;
        double now = _clock.Elapsed.TotalSeconds; float dt = (float)(now - _lastClock); _lastClock = now;
        _t += dt * Speed;
        if (_t > Duration) { if (_loop.Checked) _t %= Math.Max(1e-3f, Duration); else { _t = Duration; Pause(); } }
        ApplyPose();
    }

    void ApplyPose()
    {
        if (_anim == null || _set?.Skeleton == null || _invBind == null || _obj == null || View == null) return;
        var pose = CharacterAnims.PoseMatrices(_set.Skeleton, _anim, _t);
        var skin = new Matrix4x4[pose.Length];
        for (int j = 0; j < pose.Length; j++) skin[j] = _invBind[j] * pose[j];
        View.SetPose(_obj, skin);
        View.RenderFrame();
        _quiet = true;
        _scrub.Value = Math.Clamp((int)MathF.Round(_t * AnimAsset.Fps), _scrub.Minimum, _scrub.Maximum);
        _quiet = false;
        _time.Text = $"{_t:0.00} / {Duration:0.00} s  (frame {(int)MathF.Round(_t * AnimAsset.Fps)})";
    }

    void StopToBind()
    {
        Pause();
        if (_obj != null) View?.RestorePose(_obj);
        _t = 0; _time.Text = "";
    }

    void UpdateButtons()
    {
        _play.Enabled = _anim != null; _play.Text = _playing ? "❚❚ Pause" : "▶ Play";
        _stop.Enabled = _obj != null && _set != null; _export.Enabled = _anim != null && _set?.Skeleton != null;
        _scrub.Enabled = _anim != null;
    }

    /// <summary>Writes the character, its skeleton and skin and the current animation as FBX (and its textures as PNG).</summary>
    public string LastExport = "";
    string? _texErr;

    public string? Export(string? path)
    {
        _texErr = null;
        if (_anim == null || _cur == null || _set?.Skeleton == null || _obj?.Model == null) return null;
        string name = _cur.AnimName.Replace("aid_anim_banjox_", "");
        if (path == null)
        {
            using var d = new SaveFileDialog { Filter = "FBX|*.fbx", FileName = name + ".fbx", Title = "Export animation" };
            if (d.ShowDialog(this) != DialogResult.OK) return null;
            path = d.FileName;
        }
        var model = _obj.Model;
        var draws = model.Draws.Where(d => !model.LodOnlyNodes.Contains(d.Node)).Select(d => (d, Matrix4x4.Identity)).ToList();
        FbxExporter.Write(path, _set.ModelName.Replace("aid_model_banjox_", ""), draws, skeleton: _set.Skeleton, animation: _anim, animationName: name);
        // the diffuse textures the FBX refers to (textures/<stem>.png)
        int tex = 0;
        var dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "textures");
        foreach (var t in draws.Select(x => ObjExporter.DiffuseTexture(x.d)).Where(t => t != null).Distinct())
        {
            try
            {
                var img = _scene?.Textures?.LoadFull(t!) ?? _scene?.LoadTexture(t!);
                if (img == null) { _texErr = $"{t} not found"; continue; }
                Directory.CreateDirectory(dir);
                ImageIO.Save(Path.Combine(dir, ObjExporter.TextureFileStem(t!) + ".png"), img.Value.Rgba, img.Value.W, img.Value.H);
                tex++;
            }
            catch (Exception e) { _texErr = $"{t}: {e.Message}"; }
        }
        LastExport = $"{name}: {_anim.Frames} frames, {_set.Skeleton.Count} joints, {draws.Count} draws, {tex} textures{(_texErr != null ? " (" + _texErr + ")" : "")}";
        Log?.Invoke($"Animations: exported {name} ({_anim.Frames} frames, {_set.Skeleton.Count} joints, {draws.Count} draws, {tex} textures) → {path}");
        return path;
    }

    // ------------------------------------------------------------------ scripts (NBModStudio --anim-*)

    public string ScriptState() => _set == null ? $"no character ({_info.Text})" :
        $"{_set.ObjParams}: {_set.Actions.Count} actions ({_set.AnimTable}), skeleton {_set.Skeleton?.Count ?? 0}; current {(_cur?.Action ?? "-")} = {(_cur?.AnimName ?? "-")} {(_anim != null ? $"{_anim.Frames} frames, {_anim.Tracks} tracks, scale keys {_anim.Keys.SelectMany(k => k).Min(k => MathF.Min(k.Scale.X, MathF.Min(k.Scale.Y, k.Scale.Z))):0.###}..{_anim.Keys.SelectMany(k => k).Max(k => MathF.Max(k.Scale.X, MathF.Max(k.Scale.Y, k.Scale.Z))):0.###}, translation keys up to {_anim.Keys.SelectMany(k => k).Max(k => k.Translation.Length()):0.###}" : "")}, t {_t:0.00}, {(_playing ? "playing" : "paused")}";

    public IEnumerable<string> ScriptList() => _set?.Actions.Select(a => $"{a.Action} = {a.AnimName.Replace("aid_anim_banjox_", "")}") ?? Enumerable.Empty<string>();

    /// <summary>Selects the first action whose action or animation name contains <paramref name="q"/>, posed at
    /// <paramref name="seconds"/> (paused).</summary>
    public string ScriptPose(string q, float seconds)
    {
        var a = _set?.Actions.FirstOrDefault(x => x.Action.Equals(q, StringComparison.OrdinalIgnoreCase))
             ?? _set?.Actions.FirstOrDefault(x => x.Action.Contains(q, StringComparison.OrdinalIgnoreCase) || x.AnimName.Contains(q, StringComparison.OrdinalIgnoreCase));
        if (a == null) return "no action " + q;
        Pick(a, play: false);
        _t = Math.Clamp(seconds, 0, Duration); ApplyPose();
        return ScriptState();
    }

    public void ScriptPlay(bool on) { if (on) Play(); else Pause(); }

    protected override void Dispose(bool disposing) { if (disposing) _timer.Dispose(); base.Dispose(disposing); }
}
