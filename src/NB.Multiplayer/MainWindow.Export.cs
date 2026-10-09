using System.IO;
using System.Windows;
using System.Windows.Controls;
using NB.Core.Project;
using NB.Multiplayer.Services;

namespace NB.Multiplayer;

/// <summary>
/// "Export": built-in tweaks, bundled mods (ULTRA Parts, co-op, Character Select, Snowy town, Seattle), downloaded mods and
/// editions saved as ONE standalone .nbpatch (<see cref="ModExport"/>) for another game copy, another PC (NB Multiplayer,
/// NB Studio, NB.Cli) or the mods folder of a reNut that loads .nbpatch files.
/// </summary>
public partial class MainWindow
{
    /// <summary>Tests: export into this folder without the save dialog.</summary>
    static string? TestExportDir => Environment.GetEnvironmentVariable("NB_MP_EXPORT_DIR") is { Length: > 0 } d ? d : null;

    Button ExportButton(Func<List<ModStack.Mod>> mods, string name)
    {
        var b = new Button { Content = "Export...", VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(12, 6, 12, 6), FontSize = 13,
            ToolTip = "Save as a standalone .nbpatch for another game copy, PC or reNut (right-click: reNut mods folder)" };
        System.Windows.Automation.AutomationProperties.SetName(b, "Export " + name);
        b.Click += async (_, _) => await ExportAsync(mods(), name, toRenut: false);
        b.ContextMenu = ExportMenu(mods, name);
        return b;
    }

    ContextMenu ExportMenu(Func<List<ModStack.Mod>> mods, string name)
    {
        var menu = new ContextMenu();
        var file = new MenuItem { Header = "Export as .nbpatch..." };
        file.Click += async (_, _) => await ExportAsync(mods(), name, toRenut: false);
        var renut = new MenuItem { Header = S.RenutModsFolder.Length > 0 ? $"Export to reNut mods folder ({S.RenutModsFolder})" : "Export to reNut mods folder..." };
        renut.Click += async (_, _) => await ExportAsync(mods(), name, toRenut: true);
        var choose = new MenuItem { Header = "Choose the reNut mods folder..." };
        choose.Click += (_, _) => ChooseRenutMods();
        menu.Items.Add(file); menu.Items.Add(renut); menu.Items.Add(new Separator()); menu.Items.Add(choose);
        return menu;
    }

    static List<ModStack.Mod> EditionMods(Edition ed)
    {
        var res = new List<ModStack.Mod>();
        foreach (var r in ed.Mods)
            res.Add(ModLibrary.Get(r.Sha256) ?? throw new FileNotFoundException($"The mod \"{r.Name}\" of this edition is no longer in the library."));
        return res;
    }

    async void ExportSelected_Click(object sender, RoutedEventArgs e) => await ExportAsync(TickedMods(), CombineName.Text.Trim(), toRenut: false);

    async void ExportRenut_Click(object sender, RoutedEventArgs e) => await ExportAsync(TickedMods(), CombineName.Text.Trim(), toRenut: true);

    void ChooseRenutMods_Click(object sender, RoutedEventArgs e) => ChooseRenutMods();

    bool ChooseRenutMods()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose reNut's mods folder (next to renut.exe; reNut loads every .nbpatch in it)" };
        if (S.RenutModsFolder.Length > 0 && Directory.Exists(S.RenutModsFolder)) dlg.InitialDirectory = S.RenutModsFolder;
        if (dlg.ShowDialog(this) != true) return false;
        S.RenutModsFolder = dlg.FolderName; S.Save();
        RefreshEditions();   // context menus show the folder
        return true;
    }

    static string SafeFile(string n)
    {
        var s = string.Concat(n.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' or '+' or '.' ? c : '_')).Trim();
        return (s.Length == 0 ? "mod" : s.Length > 80 ? s[..80].Trim() : s) + ".nbpatch";
    }

    /// <summary>Exports <paramref name="mods"/> as one .nbpatch: to a file the player picks, or into the reNut mods folder.</summary>
    async Task ExportAsync(List<ModStack.Mod> mods, string name, bool toRenut)
    {
        if (mods.Count == 0) return;
        if (string.IsNullOrWhiteSpace(name)) name = ModExport.DefaultName(mods);
        bool flatten = mods.Count > 1 || mods.Any(m => m.Manifest.Ops.Count > 0);
        if (flatten && !AppSettings.IsGameDir(S.GameDir)) { MessageBox.Show(this, "Choose your game folder in Settings first: exports are made against your unmodified game.", "NB Multiplayer"); return; }
        string outPath;
        var others = new List<string>();
        if (toRenut)
        {
            if ((S.RenutModsFolder.Length == 0 || !Directory.Exists(S.RenutModsFolder)) && !ChooseRenutMods()) return;
            outPath = Path.Combine(S.RenutModsFolder, SafeFile(name));
            // a reNut mods folder never combines two mods' changes to one file: name the patches that would clash
            var files = mods.SelectMany(m => m.Manifest.Files).Where(f => f.Kind != "xexmods").Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Directory.GetFiles(S.RenutModsFolder, "*.nbpatch").Where(p => !p.Equals(outPath, StringComparison.OrdinalIgnoreCase)))
                try { if (PatchPackage.ReadManifest(p).Files.Any(f => f.Kind != "xexmods" && files.Contains(f.Path))) others.Add(Path.GetFileName(p)); } catch (Exception) { }
            if (File.Exists(outPath) && TestExportDir == null &&
                MessageBox.Show(this, $"{Path.GetFileName(outPath)} is already in the reNut mods folder. Replace it?", "NB Multiplayer", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        }
        else if (TestExportDir is { } dir) { Directory.CreateDirectory(dir); outPath = Path.Combine(dir, SafeFile(name)); }
        else
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Export as .nbpatch", Filter = "NB patch (*.nbpatch)|*.nbpatch", FileName = SafeFile(name), AddExtension = true, DefaultExt = ".nbpatch" };
            if (dlg.ShowDialog(this) != true) return;
            outPath = dlg.FileName;
        }

        EditionBusy.Visibility = Visibility.Visible;
        EditionBusyText.Text = $"Exporting {name}..."; EditionBusyBar.Value = 0; EditionBusyBar.Visibility = Visibility.Visible;
        EditionsScroll.ScrollToTop();
        var prog = new Progress<(string Text, double Fraction)>(p => { EditionBusyText.Text = $"Exporting {name}: {p.Text}"; EditionBusyBar.Value = p.Fraction; });
        var work = Path.Combine(S.EditionsDirFor(S.GameDir), "_export");
        try
        {
            var res = await Task.Run(() => ModExport.Export(mods, S.GameDir, outPath, work, name, prog));
            var notes = new List<string>(res.Notes);
            if (toRenut)
            {
                notes.Add("reNut loads it at its next start (mods load in file-name order).");
                if (others.Count > 0)
                    notes.Add("Also in that folder and changing the same game files: " + string.Join(", ", others) +
                              ". reNut does not combine two mods' changes to one file and skips one of them: tick them together here and export them as one patch instead.");
            }
            EditionBusyText.Text = $"Exported \"{res.Manifest.Name}\" to {outPath} ({new FileInfo(outPath).Length / 1024.0:N0} KB" +
                                   (res.Flattened ? ", combined into one patch" : "") + ").\n" + string.Join("\n", notes.Select(n => "- " + n));
        }
        catch (Exception ex) { EditionBusyText.Text = "The export failed: " + ex.Message; }
        EditionBusyBar.Visibility = Visibility.Collapsed;
        try { if (Directory.Exists(work) && !Directory.EnumerateFileSystemEntries(work).Any()) Directory.Delete(work); } catch (IOException) { }
    }
}
