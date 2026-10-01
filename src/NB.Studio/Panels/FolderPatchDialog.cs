using NB.Core.Project;

namespace NB.Studio.Panels;

/// <summary>
/// Build > Create Patch from a Modified Game Folder: someone modded their game folder by hand (replaced files, a
/// hex-edited default.xex). The folder is compared with the retail game (fingerprints shipped with the tools) and with a
/// clean copy for the original bytes; the dialog shows what changed per file and asset, and the suggested category,
/// online behaviour and tags. "Create mod…" then continues with the usual patch details.
/// </summary>
public sealed class FolderPatchDialog : Form
{
    readonly TextBox _mod = new() { Width = 520 };
    readonly TextBox _ref = new() { Width = 520 };
    readonly Label _refState = new() { UseMnemonic = false, AutoSize = true, MaximumSize = new Size(640, 0), ForeColor = SystemColors.GrayText };
    readonly Button _analyze = new() { Text = "Analyse", AutoSize = true };
    readonly ProgressBar _bar = new() { Width = 640, Height = 14, Visible = false };
    readonly Label _status = new() { UseMnemonic = false, AutoSize = true, MaximumSize = new Size(640, 0) };
    readonly TreeView _tree = new() { Width = 640, Height = 280 };
    readonly Label _summary = new() { UseMnemonic = false, AutoSize = true, MaximumSize = new Size(640, 0) };
    readonly Button _create = new() { Text = "Create mod…", AutoSize = true, Enabled = false, DialogResult = DialogResult.OK };
    readonly IReadOnlyList<string> _candidates;
    CancellationTokenSource? _cts;

    /// <summary>The analysis (with a clean reference) when the dialog closes with OK.</summary>
    public GameDiff.Report? Report { get; private set; }

    public FolderPatchDialog(IEnumerable<string?> cleanCandidates)
    {
        _candidates = cleanCandidates.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Text = "Create Patch from a Modified Game Folder"; StartPosition = FormStartPosition.CenterParent; MinimizeBox = false; MaximizeBox = false;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; FormBorderStyle = FormBorderStyle.FixedDialog; Padding = new Padding(12);
        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        grid.Controls.Add(new Label
        {
            Text = "Turn a game folder you modded by hand (replaced bundles, textures, a hex-edited default.xex...) into a mod. The folder is " +
                   "compared with the retail game; the mod holds only the differences, against a clean copy of the game.",
            AutoSize = true, MaximumSize = new Size(640, 0), Padding = new Padding(0, 0, 0, 8),
        });
        grid.Controls.Add(PathRow("Modified game folder", _mod, "The game folder with your changes (default.xex + Bundle)"));
        grid.Controls.Add(PathRow("Clean copy of the game", _ref, "An unmodified copy of the game (default.xex + Bundle)"));
        grid.Controls.Add(_refState);
        _refState.Text = _candidates.Count > 0 ? "Leave empty to look in: " + string.Join("; ", _candidates) : "Choose an unmodified copy of the game (extract the disc again if you have none).";
        var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 8, 0, 4) };
        row.Controls.Add(_analyze);
        grid.Controls.Add(row);
        grid.Controls.Add(_bar);
        grid.Controls.Add(_status);
        grid.Controls.Add(_tree);
        grid.Controls.Add(_summary);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom };
        var cancel = new Button { Text = "Close", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(cancel); buttons.Controls.Add(_create);
        grid.Controls.Add(buttons);
        CancelButton = cancel;
        Controls.Add(grid);
        _analyze.Click += async (_, _) => await Analyse();
        FormClosing += (_, _) => _cts?.Cancel();
    }

    Control PathRow(string label, TextBox box, string browseTitle)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
        p.Controls.Add(new Label { Text = label, Width = 150, Padding = new Padding(0, 6, 0, 0) });
        p.Controls.Add(box);
        var b = new Button { Text = "Browse…", AutoSize = true };
        b.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { Description = browseTitle, UseDescriptionForTitle = true };
            if (Directory.Exists(box.Text)) d.SelectedPath = box.Text;
            if (d.ShowDialog(this) == DialogResult.OK) box.Text = d.SelectedPath;
        };
        p.Controls.Add(b);
        return p;
    }

    /// <summary>Script mode (NB Studio --folder-patch-shot): fill both folders and run the analysis.</summary>
    public async Task ScriptAnalyse(string mod, string clean) { _mod.Text = mod; _ref.Text = clean; await Analyse(); if (_tree.Nodes.Count > 0) _tree.Nodes[0].Expand(); }

    static bool IsGame(string d) => File.Exists(Path.Combine(d, "default.xex")) && Directory.Exists(Path.Combine(d, "Bundle"));

    async Task Analyse()
    {
        var mod = _mod.Text.Trim();
        if (!IsGame(mod)) { MessageBox.Show(this, "Choose the modified game folder (it has default.xex and a Bundle folder).", Text); return; }
        var refs = _ref.Text.Trim().Length > 0 ? new List<string> { _ref.Text.Trim() } : _candidates.ToList();
        _cts = new CancellationTokenSource(); var ct = _cts.Token;
        _analyze.Enabled = _create.Enabled = false; _bar.Visible = true; _tree.Nodes.Clear(); _summary.Text = "";
        var prog = new Progress<(string Text, double Fraction)>(p => { _status.Text = p.Text; _bar.Value = (int)Math.Clamp(p.Fraction * 1000, 0, 1000); });
        _bar.Maximum = 1000;
        try
        {
            var rep = await Task.Run(() =>
            {
                var cmp = GameDiff.CompareWithRetail(mod, new Progress<(string Text, double Fraction)>(p => ((IProgress<(string, double)>)prog).Report(("Comparing with the retail game: " + p.Text, p.Fraction * 0.8))), ct);
                ((IProgress<(string, double)>)prog).Report(("Looking for a clean copy of the game…", 0.8));
                var r = GameDiff.FindReference(refs, cmp.Where(c => c.State == "changed").Select(c => c.Path), mod);
                return GameDiff.Analyze(mod, r, new Progress<(string Text, double Fraction)>(p => ((IProgress<(string, double)>)prog).Report((p.Text, 0.8 + 0.2 * p.Fraction))), ct, cmp);
            }, ct);
            Show(rep);
            Report = rep;
            _create.Enabled = rep.ReferenceDir != null && (rep.Carried.Any() || (rep.Exe != null && rep.Exe.Problem == null && (rep.Exe.Known.Count + rep.Exe.Other.Count) > 0));
        }
        catch (OperationCanceledException) { _status.Text = "Cancelled."; }
        catch (Exception e) { _status.Text = "Analysis failed: " + e.Message; }
        finally { _analyze.Enabled = true; _bar.Visible = false; }
    }

    void Show(GameDiff.Report rep)
    {
        int n = rep.Files.Count(f => f.State != "missing");
        _status.Text = n == 0 ? "This folder is the unmodified retail game: nothing to make a mod of." : $"{n} changed or new file(s).";
        _refState.Text = rep.ReferenceDir != null ? "Clean copy: " + rep.ReferenceDir + " (matches the retail game for every changed file)"
            : "No clean copy found: choose an unmodified copy of the game above, then Analyse again.";
        _refState.ForeColor = rep.ReferenceDir != null ? Color.DarkGreen : Color.Firebrick;
        _tree.BeginUpdate();
        foreach (var f in rep.Files)
        {
            var node = _tree.Nodes.Add($"{f.Path}   [{f.Area}]   {f.Summary}");
            foreach (var a in f.Changed.Take(200)) node.Nodes.Add("changed  " + a);
            foreach (var a in f.Added.Take(200)) node.Nodes.Add("added    " + a);
            foreach (var a in f.Removed.Take(200)) node.Nodes.Add("removed  " + a);
            if (f.Path.Equals("default.xex", StringComparison.OrdinalIgnoreCase) && rep.Exe != null)
            {
                if (rep.Exe.Problem != null) node.Nodes.Add(rep.Exe.Problem);
                foreach (var m in rep.Exe.Known) node.Nodes.Add($"known executable mod: {m.Name}");
                foreach (var w in rep.Exe.Other.Take(200)) node.Nodes.Add($"word {w.Address:X8}: {w.Original:X8} -> {w.Patched:X8}");
            }
        }
        _tree.EndUpdate();
        var cat = ModCategories.Find(rep.Category)?.Name ?? rep.Category;
        _summary.Text = $"Suggested: {cat} · {(rep.Multiplayer == "cosmetic" ? "looks/sounds only (players without it can still play together)" : "everyone in a multiplayer room needs it")}" +
                        (rep.Tags.Count > 0 ? " · tags: " + string.Join(", ", rep.Tags) : "") +
                        (rep.Warnings.Count > 0 ? "\n" + string.Join("\n", rep.Warnings.Select(w => "⚠ " + w)) : "");
    }
}
