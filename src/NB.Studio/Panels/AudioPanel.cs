using System.Media;
using NB.Core.Audio;
using NB.Core.Project;

namespace NB.Studio.Panels;

/// <summary>Wave bank browser: preview, export to WAV, replace with WAV (stored as PCM16, like the game's music).</summary>
public sealed class AudioPanel : UserControl
{
    readonly ComboBox _bundle = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380 };
    readonly ListBox _banks = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    readonly ListView _sounds = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false };
    readonly FlowLayoutPanel _buttons = new() { Dock = DockStyle.Top, Height = 34 };
    public Action<string>? Log;
    public string? VgmstreamPath;
    Workspace? _ws; AssetIndex? _index; AudioService.BankRef? _bank; XwbFile? _xwb; byte[]? _raw;
    SoundPlayer? _player;

    public AudioPanel()
    {
        _sounds.Columns.Add("#", 40); _sounds.Columns.Add("Codec", 70); _sounds.Columns.Add("Ch", 40); _sounds.Columns.Add("Rate", 70); _sounds.Columns.Add("Seconds", 70); _sounds.Columns.Add("Loop", 90); _sounds.Columns.Add("Bytes", 90);
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 260 };
        split.Panel1.Controls.Add(_banks); split.Panel2.Controls.Add(_sounds);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32 };
        top.Controls.Add(new Label { Text = "Bundle", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }); top.Controls.Add(_bundle);
        Btn("▶ Play", Play); Btn("■ Stop", () => _player?.Stop()); Btn("Export WAV…", Export); Btn("Export Whole Bank…", ExportAll); Btn("Replace with WAV…", Replace);
        Controls.Add(split); Controls.Add(_buttons); Controls.Add(top);
        _bundle.SelectedIndexChanged += (_, _) => FillBanks();
        _banks.SelectedIndexChanged += (_, _) => LoadBank();
        // a single click on a sound selects and plays it (the button plays the selected / focused one)
        _sounds.MouseClick += (_, e) =>
        {
            var hit = _sounds.HitTest(e.Location).Item;
            if (hit == null) return;
            hit.Selected = true; hit.Focused = true;
            try { Play(hit.Index); }
            catch (Exception ex) { Log?.Invoke("ERROR: " + ex.Message); MessageBox.Show(this, ex.Message, "Play sound"); }
        };
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    /// <summary>Test hook (Studio script --audio-click): opens bank <paramref name="bank"/> and clicks sound row
    /// <paramref name="sound"/> with real posted mouse messages, so the list's own click handling runs.</summary>
    public string TestClick(int bank, int sound)
    {
        if (_banks.Items.Count == 0) return "no banks";
        _banks.SelectedIndex = Math.Min(bank, _banks.Items.Count - 1);
        if (_sounds.Items.Count <= sound) return $"bank has {_sounds.Items.Count} sounds";
        _sounds.SelectedIndices.Clear();
        var r = _sounds.GetItemRect(sound);
        int x = r.Left + 20, y = r.Top + r.Height / 2;
        // Posted WM_LBUTTON messages do not work: the list's drag detection compares against the real cursor. Raise the
        // list's MouseClick with the row's coordinates instead (same handler: hit test, select, play).
        var m = typeof(Control).GetMethod("OnMouseClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        m.Invoke(_sounds, new object[] { new MouseEventArgs(MouseButtons.Left, 1, x, y, 0) });
        return $"clicked row {sound} at ({x},{y}) of {((BankItem)_banks.SelectedItem!).B.Name}; selected: {(_sounds.SelectedIndices.Count > 0 ? _sounds.SelectedIndices[0] : -1)}";
    }

    void Btn(string t, Action a)
    {
        var b = new Button { Text = t, AutoSize = true };
        b.Click += (_, _) => { try { a(); } catch (Exception e) { Log?.Invoke("ERROR: " + e.Message); MessageBox.Show(this, e.Message, t); } };
        _buttons.Controls.Add(b);
    }

    public void SetWorkspace(Workspace ws, AssetIndex idx)
    {
        _ws = ws; _index = idx;
        _bundle.Items.Clear();
        foreach (var g in idx.Entries.Where(e => e.Type == "wavebank").GroupBy(e => e.Bundle).OrderByDescending(g => g.Count()))
            _bundle.Items.Add($"{g.Key:x6}  ({g.Count()} banks)  {idx.BundleSummary.GetValueOrDefault(g.Key, "")}");
        if (_bundle.Items.Count > 0) _bundle.SelectedIndex = 0;
    }

    uint CurBundle => Convert.ToUInt32(((string)_bundle.SelectedItem!)[..6], 16);

    void FillBanks()
    {
        if (_ws == null || _bundle.SelectedItem == null) return;
        _banks.Items.Clear();
        foreach (var b in AudioService.ListBanks(_ws, CurBundle).OrderBy(b => b.Name)) _banks.Items.Add(new BankItem(b));
    }

    sealed record BankItem(AudioService.BankRef B) { public override string ToString() => $"{B.Name} ({B.Entries}){(B.Streaming ? " [stream]" : "")}"; }

    void LoadBank()
    {
        if (_ws == null || _banks.SelectedItem is not BankItem bi) return;
        _bank = bi.B;
        _raw = _ws.LoadStream(_bank.Bundle).Entries.First(e => e.Id == _bank.Id && e.Kind == "xwb").Data!;
        _xwb = XwbFile.Read(_raw);
        _sounds.Items.Clear();
        for (int i = 0; i < _xwb.Entries.Count; i++)
        {
            var e = _xwb.Entries[i];
            _sounds.Items.Add(new ListViewItem(new[] { i.ToString(), e.CodecName, e.Channels.ToString(), e.SampleRate.ToString(), e.Seconds.ToString("F2"), e.LoopLength > 0 ? $"{e.LoopStart}+{e.LoopLength}" : "", e.Data.Length.ToString("N0") }));
        }
        Log?.Invoke($"Wave bank {_xwb.Name}: {_xwb.Entries.Count} sounds, {(_xwb.Streaming ? "streaming" : "in-memory")}, id {_bank.Id:X8}");
    }

    int Sel
    {
        get
        {
            if (_xwb == null) throw new InvalidOperationException("Choose a wave bank in the list on the left first, then click a sound in the list on the right.");
            if (_xwb.Entries.Count == 0) throw new InvalidOperationException($"Wave bank {_xwb.Name} has no sounds.");
            if (_sounds.SelectedIndices.Count > 0) return _sounds.SelectedIndices[0];
            if (_sounds.FocusedItem != null) return _sounds.FocusedItem.Index;
            throw new InvalidOperationException("Click a sound in the list on the right first.");
        }
    }

    void Play() => Play(Sel);

    void Play(int index)
    {
        if (_xwb == null) throw new InvalidOperationException("Choose a wave bank first.");
        var e = _xwb.Entries[index];
        var tmp = Path.Combine(Path.GetTempPath(), $"nb_play_{Guid.NewGuid():N}.wav");
        try { AudioService.ExtractWav(_xwb, _raw!, index, tmp, VgmstreamPath); }
        catch (FileNotFoundException) { throw new InvalidOperationException($"Sound {index} is {e.CodecName}; decoding it needs vgmstream-cli.exe (expected at thirdparty/vgmstream/vgmstream-cli.exe next to the tool)."); }
        catch (Exception ex) { throw new InvalidOperationException($"Sound {index} ({e.CodecName}, {e.Seconds:F2} s) could not be decoded: {ex.Message}"); }
        _player?.Stop(); _player = new SoundPlayer(tmp); _player.Play();
        Log?.Invoke($"Playing {_xwb.Name} #{index} ({e.CodecName}, {e.Channels} ch, {e.SampleRate} Hz, {e.Seconds:F2} s)");
    }

    void Export()
    {
        if (_xwb == null) return;
        int i = Sel;
        using var d = new SaveFileDialog { Filter = "WAV|*.wav", FileName = $"{_xwb.Name}_{i:D3}.wav" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        AudioService.ExtractWav(_xwb, _raw!, i, d.FileName, VgmstreamPath);
        Log?.Invoke("Exported " + d.FileName);
    }

    void ExportAll()
    {
        if (_xwb == null) return;
        using var d = new FolderBrowserDialog { Description = "Export every sound of " + _xwb.Name };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        File.WriteAllBytes(Path.Combine(d.SelectedPath, _xwb.Name + ".xwb"), _raw!);
        for (int i = 0; i < _xwb.Entries.Count; i++) AudioService.ExtractWav(_xwb, _raw!, i, Path.Combine(d.SelectedPath, $"{_xwb.Name}_{i:D3}.wav"), VgmstreamPath);
        Log?.Invoke($"Exported {_xwb.Entries.Count} sounds and the raw bank to {d.SelectedPath}");
    }

    void Replace()
    {
        if (_ws == null || _bank == null) return;
        int i = Sel;
        using var o = new OpenFileDialog { Filter = "WAV|*.wav" };
        if (o.ShowDialog(this) != DialogResult.OK) return;
        var msg = AudioService.ReplaceWithWav(_ws, _bank.Bundle, _bank.Id, i, o.FileName);
        Log?.Invoke("Replaced " + msg + " (stored as PCM16; the game's music banks use the same format). Verify in Xenia.");
        LoadBank();
    }
}
