using System.Text;
using NB.Core.Formats;
using NB.Core.Models;
using NB.Core.Project;
using NB.Core.Textures;

namespace NB.Studio.Panels;

/// <summary>Preview + export/import for the asset selected in the browser.</summary>
public sealed class AssetPreviewPanel : UserControl
{
    readonly PictureBox _pic = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(40, 40, 46) };
    readonly TextBox _info = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Font = new Font("Consolas", 9), WordWrap = false };
    readonly FlowLayoutPanel _buttons = new() { Dock = DockStyle.Top, Height = 34 };
    readonly SplitContainer _split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
    public Action<string>? Log;
    Workspace? _ws; AssetEntry? _asset;

    public AssetPreviewPanel()
    {
        // transparent texels show a checkerboard (alpha textures: glass, foliage, decals)
        _pic.BackgroundImage = Checker(); _pic.BackgroundImageLayout = ImageLayout.Tile;
        _split.Panel1.Controls.Add(_pic);
        _split.Panel2.Controls.Add(_info);
        Controls.Add(_split); Controls.Add(_buttons);
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

    void Button(string text, Action a, bool enabled = true, string? tip = null)
    {
        var b = new Button { Text = text, AutoSize = true, Enabled = enabled };
        b.Click += (_, _) => { try { a(); } catch (Exception e) { Log?.Invoke("ERROR: " + e.Message); MessageBox.Show(this, e.Message, text); } };
        if (tip != null) new ToolTip().SetToolTip(b, tip);
        _buttons.Controls.Add(b);
    }

    public void Show(Workspace ws, AssetEntry a, Action<string> log)
    {
        _ws = ws; _asset = a; Log = log;
        _buttons.Controls.Clear();
        _pic.Image?.Dispose(); _pic.Image = null;
        var sb = new StringBuilder();
        sb.AppendLine($"{a.Name}\r\ntype {a.Type}   id {a.Id:X8}   bundle {(a.Streamed ? "Bundle/50/" : "Bundle/4f/")}{a.Bundle:x6}   size {a.Size:N0}");
        if (!string.IsNullOrEmpty(a.Parts)) sb.AppendLine("parts: " + a.Parts);
        try
        {
            if (a.Type == "texture") ShowTexture(ws, a, sb);
            else if (a.Type == "model" && !a.Streamed) ShowModel(ws, a, sb);
            else ShowRaw(ws, a, sb);
        }
        catch (Exception e) { sb.AppendLine("Preview failed: " + e.Message); }
        Button("Export…", () => ExportTo());
        _info.Text = sb.ToString();
        _split.SplitterDistance = _pic.Image != null ? Math.Max(100, Height - 220) : 30;
    }

    void ShowTexture(Workspace ws, AssetEntry a, StringBuilder sb)
    {
        CaffFile caff; int sym;
        if (a.Streamed)
        {
            var e = ws.LoadStream(a.Bundle).Entries.First(x => x.Id == a.Id && x.Data != null);
            caff = CaffFile.Read(e.Data!); sym = caff.Symbols.FindIndex(s => AssetIds.DisplayName(s) == a.Name) + 1;
        }
        else { caff = ws.LoadResident(a.Bundle); sym = a.Symbol; }
        var parts = caff.PartsOf(sym).ToList();
        var cpu = parts.First(p => caff.SectionOf(p).Name == ".data");
        var gpu = parts.First(p => caff.SectionOf(p).Name == ".texturegpu");
        var t = new TextureAsset(cpu.Data, gpu.Data);
        var (rgba, w, h) = t.Decode(0);
        _pic.Image = ImageIO.ToBitmap(rgba, w, h);
        sb.AppendLine($"{t.Header}   D3DFORMAT 0x{t.Header.D3DFormat:X8}");
        int transparent = 0; for (int i = 3; i < rgba.Length; i += 4) if (rgba[i] < 250) transparent++;
        sb.AppendLine(transparent == 0 ? "alpha: opaque" : $"alpha: {100.0 * transparent / (w * h):0.#}% of the texels are (partly) transparent (checkerboard behind)");
        sb.AppendLine($"stored levels: {string.Join(", ", t.Levels.Select(l => $"L{l.Level} {l.Width}x{l.Height}{(l.PackedX + l.PackedY > 0 ? " (packed tail)" : "")}"))}");
        sb.AppendLine($"GPU blob {gpu.Data.Length:N0} bytes (layout {(t.ExpectedGpuSize == gpu.Data.Length ? "matches" : "MISMATCH")}); showing level {t.Levels[0].Level}.");
        sb.AppendLine("A texture is usually two assets: '…mip' (resident, levels 1..n) and '…top' (streamed, full size). Replace updates both.");
        Button("Replace with Image…", () => ReplaceTexture(), true, "Imports a PNG/BMP/JPG; it is resized to the stored size and re-encoded in the original format.");
    }

    void ReplaceTexture()
    {
        if (_ws == null || _asset == null) return;
        using var o = new OpenFileDialog { Filter = "Images|*.png;*.bmp;*.jpg;*.jpeg;*.tif;*.tiff;*.gif" };
        if (o.ShowDialog(this) != DialogResult.OK) return;
        var (rgba, w, h) = ImageIO.Load(o.FileName);
        var r = TextureReplacer.Replace(_ws, _asset.Bundle, _asset.Name, rgba, w, h);
        foreach (var n in r.Notes) Log?.Invoke("  " + n);
        Log?.Invoke($"Texture replaced in {r.ResidentAssets} resident and {r.StreamedAssets} streamed asset(s) of bundle {_asset.Bundle:x6}. The same texture may also exist in other bundles (search its name).");
        Show(_ws, _asset, Log!);
    }

    void ShowModel(Workspace ws, AssetEntry a, StringBuilder sb)
    {
        var caff = ws.LoadResident(a.Bundle);
        var m = ModelAsset.Parse(caff, a.Symbol);
        sb.AppendLine($"chunks: {string.Join(", ", m.Chunks.Keys)}   nodes {m.Nodes.Count}   scene instances {m.Instances.Count}   draws {m.Draws.Count}   textures {m.TextureTable.Count}");
        foreach (var d in m.Draws.Take(40))
            sb.AppendLine($"  draw: {d.Positions.Length} verts, {d.Indices.Length / 3} tris, stride {d.Stride}; layout [{string.Join("; ", d.Layout)}]; textures: {string.Join(", ", d.Textures.Select(t => t.Slot + ":" + t.Texture))}");
        foreach (var w in m.Warnings.Take(10)) sb.AppendLine("  warning: " + w);
        var b = new System.Drawing.Bitmap(512, 512);
        ModelThumb.Render(m, b);
        _pic.Image = b;
    }

    void ShowRaw(Workspace ws, AssetEntry a, StringBuilder sb)
    {
        byte[]? data = null;
        if (a.Streamed)
        {
            var e = ws.LoadStream(a.Bundle).Entries.First(x => x.Id == a.Id && x.Data != null);
            data = e.Data; sb.AppendLine($"stream entry kind: {e.Kind}");
            if (e.Kind == "xwb") sb.AppendLine("XACT wave bank (big-endian 'WBND'). Export saves the .xwb; decoding/replacement is not implemented yet.");
        }
        else
        {
            var caff = ws.LoadResident(a.Bundle);
            var v = new AssetView(caff, a.Symbol);
            data = v.Has(".data") ? v.Data(".data") : v.Part(v.PartBySection.Values.First()).Data;
            sb.AppendLine($"pointers in this asset: {v.Pointers.Count}. Use the Tag Editor tab to inspect or edit fields.");
        }
        if (data != null) sb.AppendLine(Hex(data, 0x400));
    }

    static string Hex(byte[] d, int max)
    {
        var sb = new StringBuilder();
        for (int o = 0; o < Math.Min(d.Length, max); o += 16)
        {
            sb.Append($"{o:X6}  ");
            for (int i = 0; i < 16; i++) sb.Append(o + i < d.Length ? $"{d[o + i]:X2} " : "   ");
            sb.Append(' ');
            for (int i = 0; i < 16 && o + i < d.Length; i++) sb.Append(d[o + i] is >= 0x20 and < 0x7F ? (char)d[o + i] : '.');
            sb.AppendLine();
        }
        return sb.ToString();
    }

    void ExportTo()
    {
        if (_ws == null || _asset == null) return;
        using var d = new FolderBrowserDialog { Description = "Export folder" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        var p = AssetExporter.Export(_ws, _asset, d.SelectedPath);
        Log?.Invoke($"Exported {_asset.Name} → {p}");
    }
}

/// <summary>Tiny software renderer for model thumbnails.</summary>
public static class ModelThumb
{
    public static void Render(ModelAsset m, Bitmap bmp)
    {
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(40, 40, 46));
        var pts = m.Draws.SelectMany(d => d.Positions).ToList();
        if (pts.Count == 0) return;
        var mn = new System.Numerics.Vector3(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Min(p => p.Z));
        var mx = new System.Numerics.Vector3(pts.Max(p => p.X), pts.Max(p => p.Y), pts.Max(p => p.Z));
        var c = (mn + mx) / 2; float r = MathF.Max(1e-3f, (mx - mn).Length() / 2);
        var rot = System.Numerics.Matrix4x4.CreateRotationY(0.6f) * System.Numerics.Matrix4x4.CreateRotationX(-0.45f);
        var tris = new List<(PointF[] P, float Z, int Shade)>();
        foreach (var d in m.Draws)
            for (int k = 0; k + 2 < d.Indices.Length && tris.Count < 200000; k += 3)
            {
                var v = new System.Numerics.Vector3[3];
                bool ok = true;
                for (int j = 0; j < 3; j++) { int ix = d.Indices[k + j]; if (ix >= d.Positions.Length) { ok = false; break; } v[j] = System.Numerics.Vector3.Transform((d.Positions[ix] - c) / r, rot); }
                if (!ok) continue;
                var n = System.Numerics.Vector3.Cross(v[1] - v[0], v[2] - v[0]);
                float len = n.Length(); int shade = len < 1e-12f ? 128 : (int)(70 + 170 * MathF.Abs(n.Z / len));
                tris.Add((v.Select(p => new PointF(bmp.Width / 2 + p.X * bmp.Width * 0.42f, bmp.Height / 2 - p.Y * bmp.Height * 0.42f)).ToArray(), (v[0].Z + v[1].Z + v[2].Z) / 3, shade));
            }
        foreach (var t in tris.OrderByDescending(t => t.Z))
            using (var br = new SolidBrush(Color.FromArgb(t.Shade, t.Shade, (int)(t.Shade * 0.92f)))) g.FillPolygon(br, t.P);
    }
}
