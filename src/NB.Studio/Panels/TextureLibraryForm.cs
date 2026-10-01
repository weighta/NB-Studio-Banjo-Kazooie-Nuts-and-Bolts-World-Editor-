using System.Diagnostics;
using System.Text;
using NB.Core.Textures;

namespace NB.Studio.Panels;

/// <summary>One texture listed in the library: stem (without mip/top), role (colour/normal/spec/…), how it is used.</summary>
public sealed record TextureItem(string Stem, string Role, string Usage, int OtherModels);

/// <summary>What the texture library needs from the open world (implemented by the main form).</summary>
public interface ITextureHost
{
    /// <summary>Decodes a texture at its largest stored size.</summary>
    (byte[] Rgba, int W, int H)? LoadFull(string stem);
    /// <summary>Where the texture is stored (world bundle, shared bundles, stream archives).</summary>
    string Where(string stem);
    /// <summary>Replaces the texture (everywhere it is stored, or only for the library's model as a new copy), saves
    /// and reloads the world. Returns the stem the model now uses, or null when nothing changed.</summary>
    Task<string?> ReplaceAsync(string stem, (byte[] Rgba, int W, int H) image, bool modelOnly);
    /// <summary>Current list of textures of the library's scope (after a replace the world is reloaded).</summary>
    List<TextureItem> Items();
    /// <summary>Folder for "Edit Externally" copies.</summary>
    string EditFolder { get; }
}

/// <summary>
/// Texture library of a model (or of the whole world): thumbnails of every texture it uses, a full-size preview, and
/// export (PNG), open in the system image viewer, edit externally and apply back, and replace — everywhere the texture
/// is stored, or for this model only (a new texture that only this model uses).
/// </summary>
public sealed class TextureLibraryForm : Form
{
    readonly ITextureHost _host;
    readonly bool _modelScope;
    readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.LargeIcon, MultiSelect = true, HideSelection = false };
    readonly ImageList _thumbs = new() { ImageSize = new Size(96, 96), ColorDepth = ColorDepth.Depth32Bit };
    readonly PictureBox _pic = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackgroundImageLayout = ImageLayout.Tile };
    readonly TextBox _info = new() { Dock = DockStyle.Bottom, Height = 110, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9) };
    readonly FlowLayoutPanel _buttons = new() { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Padding = new Padding(2) };
    readonly TextBox _filter = new() { Dock = DockStyle.Top, PlaceholderText = "Filter textures…" };
    readonly CheckBox _modelOnly = new() { Text = "Only this model (replace makes a copy for it; other models keep the original)", AutoSize = true };
    readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 22, TextAlign = ContentAlignment.MiddleLeft };
    readonly System.Windows.Forms.Timer _loader = new() { Interval = 15 };
    readonly Queue<ListViewItem> _pending = new();
    List<TextureItem> _items = new();
    public Action<string>? Log;

    public TextureLibraryForm(string title, ITextureHost host, bool modelScope)
    {
        _host = host; _modelScope = modelScope;
        Text = title; Width = 1100; Height = 720; StartPosition = FormStartPosition.CenterParent; KeyPreview = true;
        _pic.BackgroundImage = Checker();
        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2 };
        Load += (_, _) => split.SplitterDistance = Math.Max(200, split.Width - 500);   // preview side ~500 px
        split.Panel1.Controls.Add(_list); split.Panel1.Controls.Add(_filter);
        split.Panel2.Controls.Add(_pic); split.Panel2.Controls.Add(_info); split.Panel2.Controls.Add(_buttons);
        Controls.Add(split); Controls.Add(_status);
        _list.LargeImageList = _thumbs;
        _modelOnly.Enabled = modelScope; _modelOnly.Visible = modelScope;

        Button("Export PNG…", ExportSelected, "Save the selected texture(s) at full stored resolution.");
        Button("Export All…", ExportAll, "Save every listed texture as PNG into a folder.");
        Button("Open in Viewer", OpenInViewer, "Export to a temporary PNG and open it with Windows' default image viewer.");
        Button("Edit Externally", EditExternally, "Export to the workspace's texture_edits folder and open it; edit and save it there, then press Apply Edited File.");
        Button("Apply Edited File", async () => await ApplyEdited(), "Replace the texture with its edited copy from texture_edits.");
        Button("Replace…", async () => await ReplaceFromDialog(), "Replace the texture with an image file (PNG, JPG, BMP, TGA, TIFF).");
        Button("Open Folder", () => { Directory.CreateDirectory(_host.EditFolder); Process.Start(new ProcessStartInfo(_host.EditFolder) { UseShellExecute = true }); }, "Open the texture_edits folder.");
        _buttons.Controls.Add(_modelOnly);

        _list.SelectedIndexChanged += (_, _) => ShowSelected();
        _list.DoubleClick += (_, _) => OpenInViewer();
        _filter.TextChanged += (_, _) => Fill();
        _loader.Tick += (_, _) => LoadSomeThumbs();
        FormClosed += (_, _) => { _loader.Stop(); _pic.Image?.Dispose(); };
        Reload();
    }

    void Button(string text, Action a, string tip)
    {
        var b = new Button { Text = text, AutoSize = true };
        b.Click += (_, _) => { try { a(); } catch (Exception e) { Log?.Invoke("ERROR: " + e.Message); MessageBox.Show(this, e.Message, text); } };
        new ToolTip().SetToolTip(b, tip);
        _buttons.Controls.Add(b);
    }

    static Bitmap Checker()
    {
        var b = new Bitmap(16, 16);
        using var g = Graphics.FromImage(b);
        g.Clear(Color.FromArgb(70, 70, 76));
        using var br = new SolidBrush(Color.FromArgb(52, 52, 58));
        g.FillRectangle(br, 0, 0, 8, 8); g.FillRectangle(br, 8, 8, 8, 8);
        return b;
    }

    public void Reload()
    {
        _items = _host.Items();
        Fill();
    }

    void Fill()
    {
        _loader.Stop(); _pending.Clear();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var im in _thumbs.Images.Cast<Image>().ToList()) im.Dispose();
        _thumbs.Images.Clear();
        var f = _filter.Text.Trim();
        foreach (var t in _items.Where(t => f.Length == 0 || t.Stem.Contains(f, StringComparison.OrdinalIgnoreCase)))
        {
            var it = new ListViewItem(Short(t.Stem) + "\n" + t.Role) { Tag = t, ToolTipText = t.Stem };
            _list.Items.Add(it); _pending.Enqueue(it);
        }
        _list.EndUpdate();
        _status.Text = $"{_list.Items.Count} texture(s)";
        _loader.Start();
    }

    static string Short(string stem) => stem.StartsWith("aid_texture_banjox_") ? stem["aid_texture_banjox_".Length..] : stem;

    /// <summary>Decodes a few thumbnails per timer tick so the window stays responsive.</summary>
    void LoadSomeThumbs()
    {
        for (int k = 0; k < 3 && _pending.Count > 0; k++)
        {
            var it = _pending.Dequeue();
            if (it.ListView == null) continue;
            var t = (TextureItem)it.Tag!;
            Bitmap thumb = new(96, 96);
            using (var g = Graphics.FromImage(thumb))
            {
                g.Clear(Color.FromArgb(60, 60, 66));
                var img = SafeLoad(t.Stem);
                if (img != null)
                {
                    using var bmp = ImageIO.ToBitmap(img.Value.Rgba, img.Value.W, img.Value.H, keepAlpha: false);
                    float s = Math.Min(96f / bmp.Width, 96f / bmp.Height);
                    int w = Math.Max(1, (int)(bmp.Width * s)), h = Math.Max(1, (int)(bmp.Height * s));
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                    g.DrawImage(bmp, (96 - w) / 2, (96 - h) / 2, w, h);
                    it.Text = Short(t.Stem) + $"\n{t.Role} {img.Value.W}x{img.Value.H}";
                }
                else { using var p = new Pen(Color.IndianRed, 3); g.DrawLine(p, 10, 10, 86, 86); g.DrawLine(p, 86, 10, 10, 86); it.Text = Short(t.Stem) + "\n(not found)"; }
            }
            _thumbs.Images.Add(t.Stem, thumb);
            it.ImageKey = t.Stem;
        }
        if (_pending.Count == 0) _loader.Stop();
    }

    (byte[] Rgba, int W, int H)? SafeLoad(string stem) { try { return _host.LoadFull(stem); } catch (Exception) { return null; } }

    TextureItem? Selected => _list.SelectedItems.Count > 0 ? (TextureItem)_list.SelectedItems[0].Tag! : null;

    void ShowSelected()
    {
        var t = Selected;
        _pic.Image?.Dispose(); _pic.Image = null;
        if (t == null) { _info.Text = ""; return; }
        var img = SafeLoad(t.Stem);
        if (img != null) _pic.Image = ImageIO.ToBitmap(img.Value.Rgba, img.Value.W, img.Value.H);
        var sb = new StringBuilder();
        sb.AppendLine(t.Stem);
        sb.AppendLine($"role: {t.Role}    size: {(img != null ? $"{img.Value.W}x{img.Value.H}" : "not found")}    used: {t.Usage}");
        sb.AppendLine("stored: " + _host.Where(t.Stem));
        if (_modelScope && t.OtherModels > 0) sb.AppendLine($"also used by {t.OtherModels} other model(s) in this world: a normal replace changes them too; tick 'Only this model' to give this model its own copy.");
        _info.Text = sb.ToString().Replace("\n", Environment.NewLine);
        _modelOnly.Checked = false;
    }

    // ------------------------------------------------------------------ export / view

    public int ExportTo(string dir, IEnumerable<TextureItem> items)
    {
        Directory.CreateDirectory(dir);
        int n = 0;
        foreach (var t in items)
        {
            var img = SafeLoad(t.Stem);
            if (img == null) { Log?.Invoke($"texture library: {t.Stem} could not be decoded"); continue; }
            ImageIO.Save(Path.Combine(dir, Short(t.Stem) + ".png"), img.Value.Rgba, img.Value.W, img.Value.H);
            n++;
        }
        Log?.Invoke($"texture library: exported {n} texture(s) to {dir}");
        _status.Text = $"Exported {n} texture(s) to {dir}";
        return n;
    }

    void ExportSelected()
    {
        var sel = _list.SelectedItems.Cast<ListViewItem>().Select(i => (TextureItem)i.Tag!).ToList();
        if (sel.Count == 0) { MessageBox.Show(this, "Select a texture first.", Text); return; }
        if (sel.Count == 1)
        {
            using var d = new SaveFileDialog { Filter = "PNG image|*.png|BMP image|*.bmp|TIFF image|*.tif", FileName = Short(sel[0].Stem) + ".png" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            var img = SafeLoad(sel[0].Stem) ?? throw new InvalidDataException("texture could not be decoded");
            ImageIO.Save(d.FileName, img.Rgba, img.W, img.H);
            Log?.Invoke($"texture library: exported {sel[0].Stem} → {d.FileName}");
            _status.Text = "Exported " + d.FileName;
            return;
        }
        using var fd = new FolderBrowserDialog { Description = "Export the selected textures to…" };
        if (fd.ShowDialog(this) == DialogResult.OK) ExportTo(fd.SelectedPath, sel);
    }

    void ExportAll()
    {
        using var fd = new FolderBrowserDialog { Description = "Export every listed texture (PNG) to…" };
        if (fd.ShowDialog(this) == DialogResult.OK) ExportTo(fd.SelectedPath, _list.Items.Cast<ListViewItem>().Select(i => (TextureItem)i.Tag!));
    }

    void OpenInViewer()
    {
        var t = Selected ?? throw new InvalidOperationException("Select a texture first.");
        var dir = Path.Combine(Path.GetTempPath(), "NBStudioTextures");
        ExportTo(dir, new[] { t });
        Process.Start(new ProcessStartInfo(Path.Combine(dir, Short(t.Stem) + ".png")) { UseShellExecute = true });
    }

    string EditPath(TextureItem t) => Path.Combine(_host.EditFolder, Short(t.Stem) + ".png");

    void EditExternally()
    {
        var t = Selected ?? throw new InvalidOperationException("Select a texture first.");
        var p = EditPath(t);
        if (!File.Exists(p) || MessageBox.Show(this, $"{p} already exists (an earlier edit). Overwrite it with the current texture?", Text, MessageBoxButtons.YesNo) == DialogResult.Yes)
            ExportTo(_host.EditFolder, new[] { t });
        Process.Start(new ProcessStartInfo(p) { UseShellExecute = true, Verb = "edit" }.WithFallback());
        _status.Text = $"Editing {p} — save it, then press Apply Edited File.";
    }

    async Task ApplyEdited()
    {
        var t = Selected ?? throw new InvalidOperationException("Select a texture first.");
        var p = EditPath(t);
        if (!File.Exists(p)) { MessageBox.Show(this, $"No edited copy yet ({p}). Use Edit Externally first.", Text); return; }
        await ReplaceWith(t, p, _modelOnly.Checked, confirm: true);
    }

    async Task ReplaceFromDialog()
    {
        var t = Selected ?? throw new InvalidOperationException("Select a texture first.");
        using var d = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.tga;*.tif;*.tiff;*.gif|All files|*.*", Title = "Replace " + Short(t.Stem) };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        await ReplaceWith(t, d.FileName, _modelOnly.Checked, confirm: true);
    }

    /// <summary>Replaces <paramref name="t"/> with an image file (also used by Studio scripts).</summary>
    public async Task<string?> ReplaceWith(TextureItem t, string file, bool modelOnly, bool confirm)
    {
        var img = ImageIO.Load(file);
        if (confirm)
        {
            string scope = modelOnly ? "for this model only (a new texture is created and only this model is changed)"
                : t.OtherModels > 0 && _modelScope ? $"everywhere it is stored — {t.OtherModels} other model(s) in this world use it too" : "everywhere it is stored";
            if (MessageBox.Show(this, $"Replace {Short(t.Stem)} with {Path.GetFileName(file)} ({img.W}x{img.H}) {scope}?\n\nThe world is saved and reloaded. Undo: Edit > Undo Last Bundle Save.", Text, MessageBoxButtons.OKCancel) != DialogResult.OK) return null;
        }
        UseWaitCursor = true; Enabled = false;
        try
        {
            var now = await _host.ReplaceAsync(t.Stem, img, modelOnly);
            Reload();
            var it = _list.Items.Cast<ListViewItem>().FirstOrDefault(i => ((TextureItem)i.Tag!).Stem == (now ?? t.Stem));
            if (it != null) { it.Selected = true; it.EnsureVisible(); }
            _status.Text = now == null ? "Nothing changed." : $"Replaced: the model now uses {Short(now)}";
            return now;
        }
        finally { UseWaitCursor = false; Enabled = true; }
    }

    // ------------------------------------------------------------------ script hooks

    public bool SelectStem(string q)
    {
        _list.SelectedItems.Clear();
        var it = _list.Items.Cast<ListViewItem>().FirstOrDefault(i => ((TextureItem)i.Tag!).Stem.Contains(q, StringComparison.OrdinalIgnoreCase));
        if (it == null) return false;
        it.Selected = true; it.EnsureVisible(); ShowSelected();
        return true;
    }

    public TextureItem? FindItem(string q) => _items.FirstOrDefault(i => i.Stem.Contains(q, StringComparison.OrdinalIgnoreCase));

    public async Task WaitForThumbnails()
    {
        while (_pending.Count > 0) { LoadSomeThumbs(); await Task.Delay(1); }
    }

    public IEnumerable<string> ListedStems => _list.Items.Cast<ListViewItem>().Select(i => ((TextureItem)i.Tag!).Stem);

    public void SaveShot(string file)
    {
        Application.DoEvents();
        using var b = new Bitmap(Width, Height); DrawToBitmap(b, new Rectangle(0, 0, Width, Height));
        b.Save(file);
    }
}

static class ProcessStartInfoExt
{
    /// <summary>Uses the "edit" verb when Windows has one for the file type, otherwise the default (open).</summary>
    public static ProcessStartInfo WithFallback(this ProcessStartInfo p)
    {
        try
        {
            var verbs = new ProcessStartInfo(p.FileName).Verbs;
            if (!verbs.Contains(p.Verb, StringComparer.OrdinalIgnoreCase)) p.Verb = "";
        }
        catch (Exception) { p.Verb = ""; }
        return p;
    }
}
