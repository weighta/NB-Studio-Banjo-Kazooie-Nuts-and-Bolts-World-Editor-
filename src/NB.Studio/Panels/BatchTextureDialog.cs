using NB.Core.Textures;

namespace NB.Studio.Panels;

/// <summary>
/// Texture Library > Replace from Folder: every image in a folder that is named after a texture of this world (the names
/// Export All writes, "name.png", or the full asset name, with or without the mip/top suffix) replaces that texture.
/// The dialog shows each match as current → new; untick the ones to keep. One save of the world bundle and its stream
/// archive (NB.Core.Textures.TextureReplacer.ReplaceMany, the same as NB.Cli tex-batch).
/// </summary>
public sealed class BatchTextureDialog : Form
{
    readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, OwnerDraw = true };
    readonly ImageList _rowHeight = new() { ImageSize = new Size(1, 68) };
    readonly Label _summary = new() { Dock = DockStyle.Top, AutoSize = false, Height = 84, Padding = new Padding(10, 8, 10, 4), UseMnemonic = false };
    readonly Dictionary<string, string> _hash = new();   // image key -> pixel hash (an unchanged export is skipped)
    readonly HashSet<string> _unchanged = new();
    readonly Button _ok = Ui.Primary("Replace");
    readonly ITextureHost _host;
    readonly Dictionary<string, Bitmap?> _thumbs = new();
    readonly System.Windows.Forms.Timer _loader = new() { Interval = 10 };
    readonly Queue<(string Key, Func<(byte[] Rgba, int W, int H)?> Load)> _pending = new();

    public sealed record Match(string File, string Stem);
    public List<Match> Matches { get; } = new();
    public List<string> Unmatched { get; } = new();
    /// <summary>The ticked matches when the dialog closes with OK.</summary>
    public List<Match> Chosen => _list.Items.Cast<ListViewItem>().Where(i => i.Checked).Select(i => (Match)i.Tag!).ToList();

    public BatchTextureDialog(ITextureHost host, string folder)
    {
        _host = host;
        Text = "Replace Textures from a Folder"; Width = 980; Height = 680; StartPosition = FormStartPosition.CenterParent; MinimizeBox = false;
        var known = host.WorldTextureStems.ToDictionary(s => s.ToLowerInvariant(), s => s);
        foreach (var f in Directory.GetFiles(folder).Where(f => new[] { ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".tif", ".tiff" }.Contains(Path.GetExtension(f).ToLowerInvariant())).OrderBy(f => f))
        {
            var stem = StemOf(Path.GetFileNameWithoutExtension(f), known);
            if (stem != null && !Matches.Any(m => m.Stem == stem)) Matches.Add(new Match(f, stem)); else Unmatched.Add(Path.GetFileName(f));
        }
        _list.SmallImageList = _rowHeight;
        _list.Columns.Add("", 34); _list.Columns.Add("Current", 140); _list.Columns.Add("New", 140); _list.Columns.Add("Texture", 380); _list.Columns.Add("File", 220);
        foreach (var m in Matches)
        {
            var it = new ListViewItem(new[] { "", "", "", Short(m.Stem), Path.GetFileName(m.File) }) { Checked = true, Tag = m };
            _list.Items.Add(it);
            _pending.Enqueue(("cur:" + m.Stem, () => host.LoadFull(m.Stem)));
            _pending.Enqueue(("new:" + m.File, () => { try { return ImageIO.Load(m.File); } catch (Exception) { return null; } }));
        }
        _list.DrawColumnHeader += (_, e) => e.DrawDefault = true;
        _list.DrawItem += (_, _) => { };
        _list.DrawSubItem += DrawSubItem;
        _list.ItemChecked += (_, _) => UpdateSummary();
        _summary.Text = "";
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
        var cancel = Ui.Plain("Cancel"); cancel.DialogResult = DialogResult.Cancel;
        _ok.DialogResult = DialogResult.OK;
        var all = Ui.Plain("Tick All"); all.Click += (_, _) => { foreach (ListViewItem i in _list.Items) i.Checked = true; };
        var none = Ui.Plain("Untick All"); none.Click += (_, _) => { foreach (ListViewItem i in _list.Items) i.Checked = false; };
        buttons.Controls.AddRange(new Control[] { cancel, _ok, none, all });
        Controls.Add(_list); Controls.Add(buttons); Controls.Add(_summary);
        AcceptButton = _ok; CancelButton = cancel;
        _loader.Tick += (_, _) => LoadSome();
        Shown += (_, _) => _loader.Start();
        FormClosed += (_, _) => { _loader.Stop(); foreach (var b in _thumbs.Values) b?.Dispose(); };
        UpdateSummary();
    }

    /// <summary>The world texture a file name means, or null: full names ("aid_texture_…[mip|top]"), names without the
    /// "aid_texture_banjox_" prefix (Export All), case-insensitive.</summary>
    public static string? StemOf(string fileStem, IReadOnlyDictionary<string, string> known)
    {
        var n = fileStem.Trim();
        foreach (var c in new[] { n, "aid_texture_banjox_" + n, "aid_texture_" + n })
        {
            var s = TextureReplacer.Stem(c).ToLowerInvariant();
            if (known.TryGetValue(s, out var hit)) return hit;
        }
        return null;
    }

    static string Short(string stem) => stem.StartsWith("aid_texture_banjox_") ? stem["aid_texture_banjox_".Length..] : stem;

    void UpdateSummary()
    {
        int n = _list.Items.Cast<ListViewItem>().Count(i => i.Checked);
        _summary.Text = $"{Matches.Count} image(s) match textures of this world; {n} ticked" + (_unchanged.Count > 0 ? $" ({_unchanged.Count} are identical to the current texture and stay unticked)" : "") +
                        ". Each replaces the texture in this world's bundle (resident levels and the streamed full-size level); other levels keep theirs. The images are fitted to the stored sizes." +
                        (Unmatched.Count > 0 ? $"\n{Unmatched.Count} file(s) are not textures stored in this world's bundle (shared textures of other bundles: use Replace… on those) and are skipped: {string.Join(", ", Unmatched.Take(4))}{(Unmatched.Count > 4 ? " …" : "")}" : "");
        _ok.Text = $"Replace {n} Texture(s)"; _ok.Enabled = n > 0;
    }

    void LoadSome()
    {
        for (int k = 0; k < 2 && _pending.Count > 0; k++)
        {
            var (key, load) = _pending.Dequeue();
            Bitmap? b = null;
            try
            {
                var img = load();
                if (img != null)
                {
                    var (rgba, w, h) = img.Value;
                    _hash[key] = PixelHash(rgba, w, h);
                    while (w > 512 || h > 512) (rgba, w, h) = ImageIO.Half(rgba, w, h);
                    using var full = ImageIO.ToBitmap(rgba, w, h, keepAlpha: false);
                    float s = Math.Min(128f / full.Width, 64f / full.Height);
                    b = new Bitmap(full, Math.Max(1, (int)(full.Width * s)), Math.Max(1, (int)(full.Height * s)));
                }
            }
            catch (Exception) { }
            _thumbs[key] = b;
        }
        // an image that is pixel-for-pixel the current texture (e.g. an Export All file nobody edited) is unticked:
        // re-encoding it would only cost quality and patch size
        foreach (ListViewItem it in _list.Items)
        {
            var m = (Match)it.Tag!;
            if (_unchanged.Contains(m.Stem) || !_hash.TryGetValue("cur:" + m.Stem, out var a) || !_hash.TryGetValue("new:" + m.File, out var b) || a != b) continue;
            _unchanged.Add(m.Stem); it.Checked = false; it.SubItems[4].Text = "unchanged: " + Path.GetFileName(m.File);
        }
        _list.Invalidate();
        if (_pending.Count == 0) { _loader.Stop(); UpdateSummary(); }
    }

    /// <summary>Hash of the pixels as they look: colour premultiplied by alpha (image writers and readers keep that, not the
    /// colour under transparent or half-transparent pixels).</summary>
    static string PixelHash(byte[] rgba, int w, int h)
    {
        var c = (byte[])rgba.Clone();
        for (int i = 0; i + 3 < c.Length; i += 4)
        {
            int a = c[i + 3];
            if (a == 255) continue;
            c[i] = (byte)((c[i] * a + 127) / 255); c[i + 1] = (byte)((c[i + 1] * a + 127) / 255); c[i + 2] = (byte)((c[i + 2] * a + 127) / 255);
        }
        return $"{w}x{h}:" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(c));
    }

    /// <summary>Loads and compares every image now (scripts; the dialog does it in the background).</summary>
    public void LoadAll() { while (_pending.Count > 0) LoadSome(); }

    void DrawSubItem(object? s, DrawListViewSubItemEventArgs e)
    {
        if (e.Item == null) return;
        var m = (Match)e.Item.Tag!;
        var g = e.Graphics;
        var r = e.Bounds;
        using (var bg = new SolidBrush(e.Item.Selected ? Color.FromArgb(255, 236, 214) : e.ItemIndex % 2 == 0 ? Color.White : Color.FromArgb(248, 248, 250))) g.FillRectangle(bg, r);
        switch (e.ColumnIndex)
        {
            case 0:
                CheckBoxRenderer.DrawCheckBox(g, new Point(r.X + 4, r.Y + r.Height / 2 - 7), e.Item.Checked ? System.Windows.Forms.VisualStyles.CheckBoxState.CheckedNormal : System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal);
                break;
            case 1: case 2:
                var key = e.ColumnIndex == 1 ? "cur:" + m.Stem : "new:" + m.File;
                if (_thumbs.TryGetValue(key, out var b) && b != null) g.DrawImage(b, r.X + 4 + (128 - b.Width) / 2, r.Y + 2 + (64 - b.Height) / 2, b.Width, b.Height);
                else TextRenderer.DrawText(g, _thumbs.ContainsKey(key) ? "(no image)" : "…", Ui.Small, r, Ui.Subtle, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
                if (e.ColumnIndex == 2) TextRenderer.DrawText(g, "→", Ui.Section, new Rectangle(r.X - 14, r.Y, 14, r.Height), Ui.Subtle, TextFormatFlags.VerticalCenter);
                break;
            default:
                TextRenderer.DrawText(g, e.SubItem?.Text ?? "", Ui.Body, new Rectangle(r.X + 4, r.Y, r.Width - 6, r.Height), Color.FromArgb(30, 30, 36), TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
                break;
        }
    }

}
