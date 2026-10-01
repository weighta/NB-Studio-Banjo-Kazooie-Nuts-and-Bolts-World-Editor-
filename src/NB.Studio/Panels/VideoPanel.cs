using System.Diagnostics;
using NB.Core.Project;

namespace NB.Studio.Panels;

/// <summary>Videos in Debug/36/xx/yy/zz (ASF/WMV, asset type 0x36): play, export, replace.</summary>
public sealed class VideoPanel : UserControl
{
    readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };
    readonly FlowLayoutPanel _buttons = new() { Dock = DockStyle.Top, Height = 34 };
    public Action<string>? Log;
    Workspace? _ws;

    public VideoPanel()
    {
        _list.Columns.Add("Asset id", 100); _list.Columns.Add("File", 160); _list.Columns.Add("Size", 110); _list.Columns.Add("Modified", 80);
        void Btn(string t, Action a) { var b = new Button { Text = t, AutoSize = true }; b.Click += (_, _) => { try { a(); } catch (Exception e) { Log?.Invoke("ERROR: " + e.Message); } }; _buttons.Controls.Add(b); }
        Btn("▶ Play (system player)", Play); Btn("Export .wmv…", Export); Btn("Replace with .wmv…", Replace);
        _buttons.Controls.Add(new Label { AutoSize = true, Padding = new Padding(8, 8, 0, 0), ForeColor = SystemColors.GrayText, Text = "Replacements must be WMV (ASF) files, ideally with the same resolution/codec as the original (e.g. WMV9, 1280×720)." });
        Controls.Add(_list); Controls.Add(_buttons);
        _list.DoubleClick += (_, _) => Play();
    }

    public void SetWorkspace(Workspace ws)
    {
        _ws = ws; _list.Items.Clear();
        var dir = Path.Combine(ws.Game.Root, "Debug", "36");
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories).OrderBy(x => x))
        {
            var rel = Path.GetRelativePath(dir, f).Replace("\\", "");
            var orig = Path.Combine(ws.Original.Root, Path.GetRelativePath(ws.Game.Root, f));
            bool mod = !File.Exists(orig) || new FileInfo(orig).Length != new FileInfo(f).Length;
            _list.Items.Add(new ListViewItem(new[] { "36" + rel.ToUpperInvariant(), Path.GetRelativePath(ws.Game.Root, f), new FileInfo(f).Length.ToString("N0"), mod ? "yes" : "" }) { Tag = f });
        }
    }

    string Sel => _list.SelectedItems.Count > 0 ? (string)_list.SelectedItems[0].Tag! : throw new InvalidOperationException("Select a video first.");

    void Play()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"nb_video_{Path.GetFileName(Path.GetDirectoryName(Sel))}{Path.GetFileName(Sel)}.wmv");
        File.Copy(Sel, tmp, true);
        Process.Start(new ProcessStartInfo(tmp) { UseShellExecute = true });
    }

    void Export()
    {
        var src = Sel;
        using var d = new SaveFileDialog { Filter = "Windows Media Video|*.wmv", FileName = "video_" + _list.SelectedItems[0].Text + ".wmv" };
        if (d.ShowDialog(this) == DialogResult.OK) { File.Copy(src, d.FileName, true); Log?.Invoke("Exported " + d.FileName); }
    }

    void Replace()
    {
        if (_ws == null) return;
        var dst = Sel;
        using var o = new OpenFileDialog { Filter = "Windows Media Video|*.wmv;*.asf" };
        if (o.ShowDialog(this) != DialogResult.OK) return;
        var head = new byte[16];
        using (var fs = File.OpenRead(o.FileName)) fs.ReadExactly(head);
        if (!head.AsSpan().SequenceEqual(Convert.FromHexString("3026B2758E66CF11A6D900AA0062CE6C")))
        { MessageBox.Show(this, "That file is not an ASF/WMV container.", "Replace video"); return; }
        File.Copy(o.FileName, dst, true);
        _ws.Log(Path.GetRelativePath(_ws.Game.Root, dst), "replaced video with " + Path.GetFileName(o.FileName));
        Log?.Invoke("Replaced " + Path.GetRelativePath(_ws.Game.Root, dst));
        SetWorkspace(_ws);
    }
}
