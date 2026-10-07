using System.Drawing.Imaging;
using NB.Core.Formats;

namespace NB.Studio.Panels;

/// <summary>
/// Tools > Xbox 360 Photo Viewer: drop Nuts &amp; Bolts photo packages (the files a console stores Take Photo shots in,
/// e.g. Content\&lt;profile&gt;\4D5307ED\...\0x0b190d70) and the photo pops up. Save as PNG / JPEG (the original bytes) /
/// BMP, copy to the clipboard, or drag it out into another program or folder. Several packages can be dropped at once.
/// </summary>
public sealed class PhotoViewerForm : Form
{
    sealed record Item(NbPhoto.Photo Photo, string Source, Image Image)
    {
        public override string ToString() => $"{Photo.Name}   ({Path.GetFileName(Source)})";
    }

    readonly PictureBox _pic = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(24, 24, 28) };
    readonly ListBox _list = new() { Dock = DockStyle.Left, Width = 240, IntegralHeight = false, Visible = false };
    readonly Label _drop = new()
    {
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.Gainsboro, BackColor = Color.FromArgb(24, 24, 28),
        Font = new Font("Segoe UI", 13f), Text = "Drop Xbox 360 Nuts & Bolts photo packages here (one or many)\n\n(or File > Open…)",
    };
    readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly List<Item> _items = new();
    readonly CheckBox _autoExtract = new()
    {
        Text = "Auto-extract dropped packages: save each photo as a JPEG in the folder it was dragged from", AutoSize = true, BackColor = Color.Transparent,
    };
    readonly ToolStripMenuItem _save, _saveAll, _copy, _import;

    public PhotoViewerForm(IEnumerable<string>? files = null)
    {
        Text = "Xbox 360 Photo Viewer"; Width = 1100; Height = 720; StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true; AllowDrop = true;
        var menu = new MenuStrip();
        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(new ToolStripMenuItem("&Open…", null, (_, _) => OpenDialog(), Keys.Control | Keys.O));
        _save = new ToolStripMenuItem("&Save As…", null, (_, _) => SaveCurrent(), Keys.Control | Keys.S);
        _saveAll = new ToolStripMenuItem("Save &All to Folder…", null, (_, _) => SaveAll(), Keys.Control | Keys.Shift | Keys.S);
        file.DropDownItems.Add(_save); file.DropDownItems.Add(_saveAll);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("&Close", null, (_, _) => Close()) { ShortcutKeyDisplayString = "Esc" });
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        var edit = new ToolStripMenuItem("&Edit");
        _copy = new ToolStripMenuItem("&Copy Image", null, (_, _) => CopyCurrent(), Keys.Control | Keys.C);
        edit.DropDownItems.Add(_copy);
        _import = new ToolStripMenuItem("&Import Image into Package…", null, (_, _) => ImportDialog(null), Keys.Control | Keys.I);
        edit.DropDownItems.Add(_import);
        edit.DropDownItems.Add(new ToolStripMenuItem("&Remove from List", null, (_, _) => RemoveCurrent(), Keys.Delete));
        menu.Items.AddRange(new ToolStripItem[] { file, edit });
        var hint = new ToolStripStatusLabel("Ctrl+C copy · Ctrl+S save · drag the photo out to save it anywhere") { ForeColor = SystemColors.GrayText };
        var status = new StatusStrip(); status.Items.AddRange(new ToolStripItem[] { _status, hint });

        var ctx = new ContextMenuStrip();
        ctx.Items.Add("Copy Image", null, (_, _) => CopyCurrent());
        ctx.Items.Add("Save As…", null, (_, _) => SaveCurrent());
        ctx.Items.Add("Import Image into Package…", null, (_, _) => ImportDialog(null));
        _pic.ContextMenuStrip = ctx;

        // bulk drop: every dropped package's photo is also written as a JPEG next to that package
        _autoExtract.Checked = LoadAutoExtract();
        _autoExtract.CheckedChanged += (_, _) => SaveAutoExtract(_autoExtract.Checked);
        var bar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(6, 2, 6, 2) };
        bar.Items.Add(new ToolStripControlHost(_autoExtract) { Margin = new Padding(0, 1, 0, 1) });
        Controls.Add(_pic); Controls.Add(_drop); Controls.Add(_list); Controls.Add(status); Controls.Add(bar); Controls.Add(menu);
        MainMenuStrip = menu;
        _drop.BringToFront();
        _list.SelectedIndexChanged += (_, _) => { Display(_list.SelectedItem as Item); UpdateUi(); };

        // drop packages anywhere in the window
        foreach (Control c in new Control[] { this, _pic, _drop, _list }) { c.AllowDrop = true; c.DragEnter += OnDragEnter; c.DragDrop += OnDragDrop; }
        // drag the photo out (Explorer, Discord, Paint...): a PNG file plus the bitmap
        Point down = default;
        _pic.MouseDown += (_, e) => down = e.Location;
        _pic.MouseMove += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || Current is not { } it) return;
            if (Math.Abs(e.X - down.X) + Math.Abs(e.Y - down.Y) < 8) return;
            var tmp = Path.Combine(Path.GetTempPath(), "NB Photos"); Directory.CreateDirectory(tmp);
            var png = Path.Combine(tmp, SafeName(it.Photo.Name) + ".png");
            try { it.Image.Save(png, ImageFormat.Png); } catch (Exception) { }
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, new[] { png });
            data.SetImage(it.Image);
            _pic.DoDragDrop(data, DragDropEffects.Copy);
        };
        UpdateUi();
        if (files != null) Shown += (_, _) => AddFiles(files, dropped: true);   // packages dropped on NBModStudio.exe
    }

    Item? Current => _list.SelectedItem as Item;

    void OnDragEnter(object? s, DragEventArgs e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
    void OnDragDrop(object? s, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] f) return;
        // a picture dropped on a shown package photo: offer to put it into the package
        if (f.Length == 1 && IsPicture(f[0]) && Current?.Photo.Package != null) { BeginInvoke(() => ImportDialog(f[0])); return; }
        AddFiles(f, dropped: true);
    }

    static readonly string[] PictureTypes = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };
    static bool IsPicture(string path) => PictureTypes.Contains(Path.GetExtension(path).ToLowerInvariant());

    void OpenDialog()
    {
        using var d = new OpenFileDialog { Title = "Xbox 360 photo packages", Multiselect = true, Filter = "Xbox 360 packages and photos|*.*" };
        if (d.ShowDialog(this) == DialogResult.OK) AddFiles(d.FileNames);
    }

    /// <summary>Adds the photos in these files (folders: every file inside) and shows the first new one.</summary>
    public void AddFiles(IEnumerable<string> paths, bool dropped = false)
    {
        var files = paths.SelectMany(p => Directory.Exists(p) ? Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories) : new[] { p }).ToList();
        // vehicle saves go to the Vehicle Editor
        var vehicles = files.Where(VehicleRouting.IsVehicleFile).ToList();
        if (vehicles.Count > 0) { VehicleRouting.Open(vehicles); files = files.Except(vehicles).ToList(); if (files.Count == 0) return; }
        var errors = new List<string>();
        var extracted = new List<string>();
        Item? first = null;
        foreach (var f in files)
        {
            try
            {
                foreach (var ph in NbPhoto.Read(f))
                {
                    var img = Image.FromStream(new MemoryStream(ph.Jpeg));
                    var it = new Item(ph, f, img);
                    if (dropped && _autoExtract.Checked && ph.Package != null)
                    {
                        var written = ExtractNextTo(f, ph);
                        if (written != null) extracted.Add(written);
                    }
                    _items.Add(it); _list.Items.Add(it);
                    first ??= it;
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IOException or OutOfMemoryException)
            {
                errors.Add($"{Path.GetFileName(f)}: {ex.Message}");
            }
        }
        if (first != null) _list.SelectedItem = first;
        if (extracted.Count > 0)
        {
            var dirs = extracted.Select(Path.GetDirectoryName).Distinct().ToList();
            _status.Text = $"Extracted {extracted.Count} photo(s) as JPEG to {(dirs.Count == 1 ? dirs[0] : dirs.Count + " folders")}.";
        }
        UpdateUi();
        if (errors.Count > 0 && (first == null || files.Count == 1))
            MessageBox.Show(this, "No photo in:\n" + string.Join("\n", errors.Take(10)) + (errors.Count > 10 ? $"\n… and {errors.Count - 10} more" : ""), Text);
        else if (errors.Count > 0) _status.Text += $"   ({errors.Count} file(s) without a photo skipped)";
    }

    void Display(Item? it)
    {
        _pic.Image = it?.Image;
        if (it == null) { _status.Text = ""; return; }
        var p = it.Photo.Package;
        _status.Text = $"{it.Photo.Name}  ·  {it.Image.Width}×{it.Image.Height} JPEG, {it.Photo.Jpeg.Length / 1024:N0} KB"
            + (p != null ? $"  ·  title {p.TitleId:X8}{(p.TitleId == NbPhoto.TitleId ? " (Nuts && Bolts)" : "")}, profile {p.ProfileId:X16}, {p.Magic} package"
               : it.Photo.ContentHeader != null ? $"  ·  content file extracted from a package (owner profile {NbPhoto.Owner(it.Photo):X16}; Import needs the package itself)" : "  ·  picture file")
            + $"  ·  {Path.GetFileName(it.Source)}";
    }

    void UpdateUi()
    {
        bool any = _items.Count > 0;
        _drop.Visible = !any;
        _list.Visible = _items.Count > 1;
        _save.Enabled = _copy.Enabled = any;
        _import.Enabled = Current?.Photo.Package != null;
        _saveAll.Enabled = _items.Count > 1;
        if (!any) _status.Text = "";
    }

    void CopyCurrent()
    {
        if (Current is not { } it) return;
        // the bitmap (paste into Paint, Discord, Word...) and a PNG file (paste into a folder)
        var tmp = Path.Combine(Path.GetTempPath(), "NB Photos"); Directory.CreateDirectory(tmp);
        var png = Path.Combine(tmp, SafeName(it.Photo.Name) + ".png");
        var data = new DataObject();
        data.SetImage(it.Image);
        try { it.Image.Save(png, ImageFormat.Png); data.SetFileDropList(new System.Collections.Specialized.StringCollection { png }); } catch (Exception) { }
        Clipboard.SetDataObject(data, true);
        _status.Text = $"Copied {it.Photo.Name} to the clipboard.";
    }

    void SaveCurrent()
    {
        if (Current is not { } it) return;
        using var d = new SaveFileDialog
        {
            Title = "Save photo", FileName = SafeName(it.Photo.Name), Filter = "PNG image|*.png|JPEG image (original, lossless copy)|*.jpg|Bitmap|*.bmp",
            InitialDirectory = Path.GetDirectoryName(it.Source),
        };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        try { Save(it, d.FileName); _status.Text = "Saved " + d.FileName; }
        catch (Exception ex) { MessageBox.Show(this, "Could not save: " + ex.Message, Text); }
    }

    void SaveAll()
    {
        using var d = new FolderBrowserDialog { Description = "Save every photo as PNG into this folder" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        int n = 0;
        foreach (var it in _items)
        {
            var name = SafeName(it.Photo.Name); var path = Path.Combine(d.SelectedPath, name + ".png");
            for (int k = 2; File.Exists(path); k++) path = Path.Combine(d.SelectedPath, $"{name} ({k}).png");
            try { Save(it, path); n++; } catch (Exception) { }
        }
        _status.Text = $"Saved {n} of {_items.Count} photo(s) to {d.SelectedPath}";
    }

    static void Save(Item it, string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".jpg" or ".jpeg": File.WriteAllBytes(path, it.Photo.Jpeg); break;   // the console's own JPEG, untouched
            case ".bmp": it.Image.Save(path, ImageFormat.Bmp); break;
            default: it.Image.Save(path, ImageFormat.Png); break;
        }
    }

    /// <summary>
    /// Puts a picture into the shown photo's package: 1280x720 JPEG + 90x50 thumbnail, integrity hashes recomputed
    /// (NbPhoto.Import). Written into the package (the original kept once as "&lt;package&gt;.original") or saved as a
    /// new package file.
    /// </summary>
    void ImportDialog(string? picture)
    {
        if (Current is not { Photo.Package: not null } it) { MessageBox.Show(this, "Open a photo package first (drop it here), then import a picture into it.", Text); return; }
        if (picture == null)
        {
            using var od = new OpenFileDialog { Title = "Picture to put into the package", Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files|*.*" };
            if (od.ShowDialog(this) != DialogResult.OK) return;
            picture = od.FileName;
        }
        byte[] file;
        Size size;
        try { file = File.ReadAllBytes(picture); using var probe = Image.FromStream(new MemoryStream(file)); size = probe.Size; }
        catch (Exception ex) { MessageBox.Show(this, "Not a picture this can read: " + ex.Message, Text); return; }

        ulong owner = NbPhoto.Owner(it.Photo);
        using var dlg = new Form
        {
            Text = "Import Image into Package", FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
            StartPosition = FormStartPosition.CenterParent, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12),
        };
        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        Label L(string t, bool gray = false) => new() { Text = t, AutoSize = true, MaximumSize = new Size(520, 0), UseMnemonic = false, ForeColor = gray ? SystemColors.GrayText : SystemColors.ControlText, Margin = new Padding(0, 0, 0, 8) };
        flow.Controls.Add(L($"Replace the photo \"{it.Photo.Name}\" with {Path.GetFileName(picture)} ({size.Width}×{size.Height})."));
        bool wide = Math.Abs((double)size.Width / size.Height - 16.0 / 9) > 0.01;
        var fill = new RadioButton { Text = "Fill the 16:9 frame (crop the edges)", AutoSize = true, Checked = true, Enabled = wide };
        var fit = new RadioButton { Text = "Fit inside (black bars)", AutoSize = true, Enabled = wide };
        flow.Controls.Add(L(wide ? "The game's photos are 1280×720 (16:9); this picture has another shape:" : "The picture is scaled to the game's 1280×720.", true));
        flow.Controls.Add(fill); flow.Controls.Add(fit);
        var ownerRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
        var ownerBox = new TextBox { Width = 160, Text = owner.ToString("X16") };
        ownerRow.Controls.Add(new Label { Text = "Owner profile (XUID):", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
        ownerRow.Controls.Add(ownerBox);
        flow.Controls.Add(ownerRow);
        flow.Controls.Add(L("The game's Photo Album only lists photos owned by the profile that is playing. Keep the owner to see it on the same profile.", true));
        var backup = new CheckBox { Text = "Keep a copy of the original package (.original, once)", AutoSize = true, Checked = true };
        flow.Controls.Add(backup);
        flow.Controls.Add(L("A real Xbox 360 also checks the package's console signature: rehash and resign the package with Horizon or Velocity before copying it to a console. Xenia does not need that.", true));
        var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        var write = new Button { Text = "Write into package", AutoSize = true, DialogResult = DialogResult.Yes };
        var saveAs = new Button { Text = "Save as new package…", AutoSize = true, DialogResult = DialogResult.No };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange(new Control[] { write, saveAs, cancel });
        flow.Controls.Add(buttons);
        dlg.Controls.Add(flow);
        dlg.AcceptButton = write; dlg.CancelButton = cancel;
        var choice = dlg.ShowDialog(this);
        if (choice is not (DialogResult.Yes or DialogResult.No)) return;

        try
        {
            ulong? newOwner = ulong.TryParse(ownerBox.Text.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var o) && o != owner ? o : null;
            var (jpeg, thumb) = NbPhoto.PrepareImage(file, crop: !fit.Checked);
            var bytes = NbPhoto.Import(it.Photo, jpeg, thumb, newOwner);
            string target = it.Source;
            if (choice == DialogResult.No)
            {
                using var sd = new SaveFileDialog { Title = "Save the new package", FileName = Path.GetFileName(it.Source), InitialDirectory = Path.GetDirectoryName(it.Source), Filter = "Xbox 360 package|*.*" };
                if (sd.ShowDialog(this) != DialogResult.OK) return;
                target = sd.FileName;
            }
            else if (backup.Checked && !File.Exists(it.Source + ".original")) File.Copy(it.Source, it.Source + ".original");
            File.WriteAllBytes(target, bytes);
            // show what is in the package now
            var ph = NbPhoto.Read(target).First();
            var item = new Item(ph, target, Image.FromStream(new MemoryStream(ph.Jpeg)));
            if (target == it.Source)
            {
                int i = _list.SelectedIndex;
                _items[_items.IndexOf(it)] = item; _list.Items[i] = item; _list.SelectedIndex = i;
                it.Image.Dispose();
            }
            else { _items.Add(item); _list.Items.Add(item); _list.SelectedItem = item; }
            Display(item); UpdateUi();
            _status.Text = $"Imported {Path.GetFileName(picture)} into {Path.GetFileName(target)} ({jpeg.Length / 1024:N0} KB JPEG).";
        }
        catch (Exception ex) { MessageBox.Show(this, "Could not import: " + ex.Message, Text); }
    }

    /// <summary>The photo as "&lt;photo name&gt;.jpg" beside its package (the console's JPEG bytes). An identical file
    /// already there is kept (dropping the same packages again writes nothing); another file of that name gets " (2)".</summary>
    static string? ExtractNextTo(string package, NbPhoto.Photo ph)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(package))!;
        var name = SafeName(ph.Name);
        var path = Path.Combine(dir, name + ".jpg");
        for (int k = 2; File.Exists(path); k++)
        {
            if (new FileInfo(path).Length == ph.Jpeg.Length && File.ReadAllBytes(path).AsSpan().SequenceEqual(ph.Jpeg)) return null;
            path = Path.Combine(dir, $"{name} ({k}).jpg");
        }
        try { File.WriteAllBytes(path, ph.Jpeg); return path; } catch (Exception) { return null; }
    }

    static string OptionFile => Path.Combine(NB.Core.Project.ProjectRegistry.DataDir, "photo_viewer.txt");
    static bool LoadAutoExtract() { try { return File.ReadAllText(OptionFile).Contains("autoExtract=1"); } catch (Exception) { return false; } }
    static void SaveAutoExtract(bool on)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(OptionFile)!); File.WriteAllText(OptionFile, "autoExtract=" + (on ? 1 : 0)); } catch (Exception) { }
    }

    void RemoveCurrent()
    {
        if (Current is not { } it) return;
        int i = _list.SelectedIndex;
        _items.Remove(it); _list.Items.Remove(it);
        if (_list.Items.Count > 0) _list.SelectedIndex = Math.Min(i, _list.Items.Count - 1); else Display(null);
        UpdateUi();
    }

    /// <summary>"01/10/2026 15:50 - PHOTO" -> "01-10-2026 15-50 - PHOTO".</summary>
    static string SafeName(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var r = new string(s.Select(c => bad.Contains(c) || c == ':' ? '-' : c).ToArray()).Trim();
        return r.Length > 0 ? r : "photo";
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _pic.Image = null;
        foreach (var it in _items) it.Image.Dispose();
        base.OnFormClosed(e);
    }
}
