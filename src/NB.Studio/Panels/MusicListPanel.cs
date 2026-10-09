using System.Text.RegularExpressions;
using NB.Core.Audio;
using NB.Core.Formats;
using NB.Core.Project;

namespace NB.Studio.Panels;

/// <summary>
/// Audio tab › Music: every music track of the game (the cue list of the common bundle) with its wave, format (all of the
/// game's audio is XACT wave banks: PCM16 for Showdown Town / World of Sports / Terrarium, XMA2 for the rest) and where it
/// plays (the level scripts' music commands of every world and Act). Double-click or ▶ plays it; Replace… imports any
/// audio file over its wave; Export WAV… saves it.
/// </summary>
public sealed class MusicListPanel : UserControl
{
    readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false };
    readonly FlowLayoutPanel _buttons = new() { Dock = DockStyle.Top, Height = 34 };
    readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 20, ForeColor = Color.FromArgb(90, 94, 104) };
    readonly CheckBox _fit = new() { Text = "Fit town stems to length", AutoSize = true, Checked = true, Padding = new Padding(6, 6, 0, 0) };
    readonly CheckBox _all = new() { Text = "All six districts", AutoSize = true, Padding = new Padding(4, 6, 0, 0) };
    readonly CheckBox _mono = new() { Text = "Mono", AutoSize = true, Padding = new Padding(4, 6, 0, 0) };
    readonly CheckBox _half = new() { Text = "22 kHz", AutoSize = true, Padding = new Padding(4, 6, 0, 0) };
    readonly CheckBox _loop = new() { Text = "Loop", AutoSize = true, ThreeState = true, CheckState = CheckState.Indeterminate, Padding = new Padding(4, 6, 0, 0) };
    readonly NumericUpDown _loopFrom = new() { Minimum = 0, Maximum = 3600, DecimalPlaces = 2, Increment = 0.5M, Width = 60, Margin = new Padding(2, 5, 0, 0) };
    public Action<string>? Log;
    public string? VgmstreamPath;
    Workspace? _ws; AssetIndex? _index; MusicCatalog? _cat;
    bool _filled;
    int _gen;
    Button _playBtn = null!, _replaceBtn = null!;
    /// <summary>Where each cue plays (cue hash → places), computed once in the background and kept (small); recomputed
    /// after a track choice is saved (<see cref="Invalidate"/>).</summary>
    Dictionary<uint, List<string>>? _places;
    /// <summary>Replace… goes through the main form (one queue, one busy state for both music panels): cue, options,
    /// file (null: ask) and a callback with the result.</summary>
    public Func<MusicCue, MusicAudio.ReplaceOptions, string?, Action<string>?, bool>? ReplaceRequested;
    /// <summary>Script checks: Replace… is enabled (off while a replacement runs); the status line.</summary>
    public bool ReplaceEnabled => _replaceBtn.Enabled;
    public string StatusText => _status.Text;

    public MusicListPanel()
    {
        _list.Columns.Add("Track", 250); _list.Columns.Add("Wave", 170); _list.Columns.Add("Format", 210); _list.Columns.Add("Where it plays", 520);
        _playBtn = Btn("▶ Play", () => Play(Sel)); Btn("■ Stop", () => { MusicPreview.Stop(); _status.Text = "Stopped."; });
        _replaceBtn = Btn("Replace…", () => Replace(Sel, null)); Btn("Export WAV…", () => Export(Sel)); Btn("Refresh", () => Invalidate(true));
        _buttons.Controls.Add(_fit); _buttons.Controls.Add(_all); _buttons.Controls.Add(_mono); _buttons.Controls.Add(_half); _buttons.Controls.Add(_loop);
        _buttons.Controls.Add(new Label { Text = "from (s)", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }); _buttons.Controls.Add(_loopFrom);
        _list.DoubleClick += (_, _) => { try { Play(Sel); } catch (Exception e) { Log?.Invoke("ERROR: " + e.Message); } };
        Controls.Add(_list); Controls.Add(_status); Controls.Add(_buttons);
        VisibleChanged += (_, _) => { if (Visible) Fill(); };
    }

    Button Btn(string t, Action a)
    {
        var b = new Button { Text = t, AutoSize = true };
        b.Click += (_, _) => { try { a(); } catch (Exception e) { Log?.Invoke("ERROR: " + e.Message); MessageBox.Show(this, e.Message, t); } };
        _buttons.Controls.Add(b);
        return b;
    }

    public void SetWorkspace(Workspace ws, AssetIndex idx) { _ws = ws; _index = idx; _filled = false; _cat = null; _places = null; if (Visible) Fill(); }

    /// <summary>A replacement runs: Replace… and ▶ wait.</summary>
    public void SetBusy(bool busy) { _replaceBtn.Enabled = !busy; _playBtn.Enabled = !busy; }

    /// <summary>The list is out of date: formats (after a Replace…) and, with <paramref name="places"/>, where tracks play
    /// (after a track choice was saved). Refills now when shown, else when shown next.</summary>
    public void Invalidate(bool places = true)
    {
        _filled = false;
        if (places) { _places = null; _gen++; }
        if (Visible) Fill();
    }

    MusicCue Sel => _list.SelectedItems.Count > 0 && _list.SelectedItems[0].Tag is MusicCue c ? c : throw new InvalidOperationException("Click a track in the list first.");

    /// <summary>Fills the list (formats at once, the places in the background).</summary>
    public void Fill()
    {
        if (_filled || _ws == null || _index == null) return;
        _filled = true;
        try { _cat = MusicCatalog.Load(_ws); } catch (Exception e) { _status.Text = "The music list could not be read: " + e.Message; return; }
        _list.BeginUpdate(); _list.Items.Clear();
        foreach (var c in _cat.Cues.GroupBy(c => c.Hash).Select(g => g.First()).OrderBy(c => c.Display))
        {
            var info = MusicAudio.InfoOf(_ws, c);
            _list.Items.Add(new ListViewItem(new[] { c.Display, c.Wave >= 0 ? $"{c.Bank} #{c.Wave}" : "?", info?.ToString() ?? "", "…" }) { Tag = c, ToolTipText = $"{c.Label} (XACT cue {c.XactCue}{(c.Variable is float v ? $", interactive_music = {v}" : "")}, hash {c.Hash:X8})" });
        }
        _list.EndUpdate();
        _list.ShowItemToolTips = true;
        if (_places != null) { ShowPlaces(_places); return; }
        _status.Text = $"{_list.Items.Count} music tracks. Finding where each plays…";
        var ws = _ws; var idx = _index; int gen = ++_gen;
        // names of Acts and worlds here (ActCatalog reads the common bundle through the workspace's cache, which belongs
        // to the UI thread); the scripts are read on the worker without that cache
        Dictionary<uint, string> acts, worlds;
        try
        {
            acts = ActCatalog.Build(ws, idx).GroupBy(a => a.ActBundle).ToDictionary(g => g.Key, g => g.First().Display);
            worlds = WorldCatalog.FromIndex(idx).GroupBy(w => w.Bundle).ToDictionary(g => g.Key, g => g.First().Display);
        }
        catch (Exception e) { _status.Text = "Places: " + e.Message; return; }
        Task.Run(() => Places(ws, idx, acts, worlds)).ContinueWith(t =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(() =>
            {
                if (gen != _gen) return;
                if (t.IsFaulted) { _status.Text = "Places: " + t.Exception!.GetBaseException().Message; return; }
                _places = t.Result;
                ShowPlaces(_places);
            });
        });
    }

    void ShowPlaces(Dictionary<uint, List<string>> places)
    {
        foreach (ListViewItem it in _list.Items)
            it.SubItems[3].Text = it.Tag is MusicCue c && places.TryGetValue(c.Hash, out var w) ? string.Join("; ", w.Distinct()) : it.Tag is MusicCue c2 ? StartedBy(c2) : "";
        _status.Text = $"{_list.Items.Count} music tracks. All of the game's audio is in XACT wave banks (Bundle/50): PCM16 or XMA2. Replace… stores your audio as PCM16 (plays in place of XMA2; verified in Xenia).";
    }

    /// <summary>Music the level scripts don't start: who does (from the assets that name these cues).</summary>
    static string StartedBy(MusicCue c)
    {
        var n = c.Label;
        if (n.Contains("Ditty")) return "jingle (objects, cut-scenes, pick-ups)";
        if (Regex.IsMatch(n, "_(Race|Lose|Win|DrumRoll)$|Race_|Challenge|Grunty_Final|Bonus")) return "challenges (aid_misc_…_challengesfx)";
        if (n.Contains("Klungo")) return "Klungo's arcade (scenecontrol_showdowntown_klungosarcade)";
        if (n.Contains("Garage")) return "Mumbo's garage (garage_pitstop)";
        if (n.Contains("Pause")) return "pause menu (frontendsfx)";
        if (n.Contains("Radio")) return "vehicle radio (stereotracklists)";
        if (Regex.IsMatch(n, "Intro|Outro")) return "cut-scenes";
        if (n.Contains("MainTheme")) return "Jiggy bank, credits";
        return "— (not started by a level script)";
    }

    /// <summary>Cue hash → where it plays, from every music command of the level scripts. Each bundle file is read and
    /// parsed on its own (MusicCommand.ScanFile), not through the workspace's bundle cache: nothing stays in memory and no
    /// second copy of a bundle an editor holds is made.</summary>
    static Dictionary<uint, List<string>> Places(Workspace ws, AssetIndex idx, Dictionary<uint, string> acts, Dictionary<uint, string> worlds)
    {
        var bundles = idx.Entries.Where(e => !e.Streamed && e.Name.StartsWith("aid_script_banjox_") &&
                (e.Name.Contains("common_audio_") || Regex.IsMatch(e.Name, @"_act(\d+|ww)_main$") || Regex.IsMatch(e.Name, @"showdowntown_(morning|midday|afternoon|night|startofgame|demo|general)$") || e.Name.Contains("ui_frontend_") || e.Name.EndsWith("_demo")))
            .Select(e => e.Bundle).Distinct().ToList();
        var res = new Dictionary<uint, List<string>>();
        foreach (var b in bundles)
        {
            List<MusicCommand> cmds;
            try { cmds = MusicCommand.ScanFile(ws.Game, b, n => n.StartsWith("aid_script_banjox_")); } catch (Exception) { continue; }
            foreach (var c in cmds)
            {
                string script = AssetIds.DisplayName(c.Script).Replace("aid_script_banjox_", "");
                string where = acts.TryGetValue(b, out var a) ? a : worlds.TryGetValue(b, out var w) ? w : "";
                if (where == "")
                {
                    var m = Regex.Match(script, @"^([a-z]+)_");
                    where = m.Success ? WorldCatalog.DisplayNames.GetValueOrDefault(m.Groups[1].Value, m.Groups[1].Value) : script;
                    if (script.StartsWith("ui_frontend_")) where = "Banjo's House menus";
                }
                string when = c.When == "world music" ? "" : $" ({c.When})";
                if (c.Op == MusicCommand.OpPlay) Add(c.Hash, where + when);
                else
                    for (int k = 0; k < c.Pairs.Count; k++)
                    {
                        if (c.Pairs[k].Region == 0) continue;
                        string region = c.Set == 0 ? $"region {c.Pairs[k].Region}" : c.Pairs[k].Region == 2 ? "underwater" : "main";
                        Add(c.Pairs[k].Hash, $"{where}{when}, {region}");
                    }
            }
        }
        return res;
        void Add(uint h, string s) { if (!res.TryGetValue(h, out var l)) res[h] = l = new(); l.Add(s); }
    }

    /// <summary>▶ through the shared preview player (a later ▶ or ■ Stop, here or in Properties, wins).</summary>
    public void Play(MusicCue c)
    {
        if (_ws == null) return;
        _status.Text = $"Preparing {c.Display}…";
        MusicPreview.Play(this, _ws, c, VgmstreamPath, false, msg => { _status.Text = msg; Log?.Invoke("Music: " + msg); });
    }

    void Export(MusicCue c)
    {
        if (_ws == null) return;
        using var d = new SaveFileDialog { Filter = "WAV|*.wav", FileName = c.Display.Replace("–", "-").Replace(":", "") + ".wav" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        MusicAudio.ExtractCue(_ws, c, d.FileName, VgmstreamPath);
        Log?.Invoke("Music: exported " + d.FileName);
    }

    /// <summary>Replace… (file null: asks for one).</summary>
    public void Replace(MusicCue c, string? file, Action<string>? done = null)
    {
        if (_ws == null || _cat == null) return;
        var opts = new MusicAudio.ReplaceOptions
        {
            FitLength = _fit.Checked && c.Variable != null, Mono = _mono.Checked, HalfRate = _half.Checked,
            Loop = _loop.CheckState == CheckState.Indeterminate ? null : _loop.Checked, LoopFromSeconds = (double)_loopFrom.Value,
            AlsoWaves = _all.Checked && c.Bank == "MusicShowdownTown" && c.Wave is >= 0 and <= 5 ? new[] { 0, 1, 2, 3, 4, 5 } : Array.Empty<int>(),
        };
        // the main form asks for the file (when null), confirms, queues and writes; it refreshes this list afterwards
        if (ReplaceRequested == null) throw new InvalidOperationException("Replace is not available here.");
        bool ended = false;
        bool started = ReplaceRequested(c, opts, file, r =>
        {
            ended = true;
            _status.Text = r.StartsWith("failed") || r.StartsWith("cancelled") ? "Replacing " + r : "Replaced " + r;
            done?.Invoke(r);
        });
        // "Replacing …" only once the main form took it (the file dialog or the question may have been cancelled)
        if (started && !ended) _status.Text = $"Replacing {c.Display}…";
    }

    /// <summary>Script check: selects a track by name.</summary>
    public string ScriptSelect(string name)
    {
        Fill();
        foreach (ListViewItem it in _list.Items)
            if (it.Tag is MusicCue c && (c.Display.Contains(name, StringComparison.OrdinalIgnoreCase) || c.Label.Contains(name, StringComparison.OrdinalIgnoreCase)))
            { it.Selected = true; it.Focused = true; it.EnsureVisible(); return $"{c.Display}: {it.SubItems[1].Text}, {it.SubItems[2].Text}; {it.SubItems[3].Text}"; }
        return "not found: " + name;
    }

    public bool PlacesReady => _status.Text.StartsWith($"{_list.Items.Count} music tracks. All");
}
