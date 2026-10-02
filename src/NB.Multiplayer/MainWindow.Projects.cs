using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using NB.Core.Project;
using NB.Multiplayer.Services;

namespace NB.Multiplayer;

/// <summary>Projects page: the player's NB Studio projects (workspaces), NB Studio itself (get / update / open) and
/// turning a project into a mod for the mod library.</summary>
public partial class MainWindow
{
    Updater.Release? _studioLatest;
    bool _studioChecked;

    string ProjectsDir => S.ProjectsDir.Length > 0 ? S.ProjectsDir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NB Studio Projects");

    void RefreshProjects()
    {
        RefreshStudio();
        ProjectList.Children.Clear();
        var entries = ProjectRegistry.Load();
        if (entries.Count == 0) entries = SeedFromStudio();
        if (entries.Count == 0)
            ProjectList.Children.Add(new TextBlock { Style = (Style)FindResource("SubText"), TextWrapping = TextWrapping.Wrap,
                Text = "No projects yet. Create one here (or in NB Studio): it is a copy of your game that NB Studio edits. When it plays the way you like, make a mod from it." });
        foreach (var en in entries) ProjectList.Children.Add(ProjectCard(en.Path, en.LastOpened));
    }

    /// <summary>Projects NB Studio 1.0 knew (its last workspace and the workspaces next to it) - before projects.json existed.</summary>
    static List<ProjectRegistry.Entry> SeedFromStudio()
    {
        try
        {
            var settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NBModTool", "settings.json");
            if (!File.Exists(settings)) return new();
            var last = JsonDocument.Parse(File.ReadAllText(settings)).RootElement.TryGetProperty("LastWorkspace", out var lw) ? lw.GetString() : null;
            if (string.IsNullOrEmpty(last) || !File.Exists(Path.Combine(last, "workspace.json"))) return new();
            var parent = Path.GetDirectoryName(last.TrimEnd('\\', '/'));
            var dirs = new List<string> { last };
            if (parent != null) dirs.AddRange(Directory.GetDirectories(parent).Where(d => File.Exists(Path.Combine(d, "workspace.json")) && !string.Equals(d, last, StringComparison.OrdinalIgnoreCase)));
            foreach (var d in dirs) ProjectRegistry.Touch(d, File.GetLastWriteTime(Path.Combine(d, "workspace.json")));
            return ProjectRegistry.Load();
        }
        catch (Exception) { return new(); }
    }

    Border ProjectCard(string root, DateTime lastOpened)
    {
        var card = new Border { Style = (Style)FindResource("CardBorder"), Margin = new Thickness(0, 0, 0, 12) };
        var outer = new StackPanel();
        var dock = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(buttons, Dock.Right);
        dock.Children.Add(buttons);
        var text = new StackPanel();
        dock.Children.Add(text);
        outer.Children.Add(dock);
        card.Child = outer;
        text.Children.Add(new TextBlock { Text = Path.GetFileName(root.TrimEnd('\\', '/')), Style = (Style)FindResource("H2") });

        Workspace.WorkspaceManifest? man = null;
        try { man = JsonSerializer.Deserialize<Workspace.WorkspaceManifest>(File.ReadAllText(Path.Combine(root, "workspace.json"))); } catch (Exception) { }
        var remove = new Button { Style = (Style)FindResource("Link"), Content = "Remove from list", Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = "Only removes it from this list; the project folder stays" };
        remove.Click += (_, _) => { ProjectRegistry.Remove(root); RefreshProjects(); };
        if (man == null)
        {
            text.Children.Add(new TextBlock { Text = "This project folder is missing: " + root, Foreground = B("Warn"), FontSize = 13, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
            text.Children.Add(remove);
            return card;
        }
        var info = new List<string> { $"{man.Changes.Count} saved change(s)" };
        if (man.ExeMods.Count > 0) info.Add($"{man.ExeMods.Count} executable tweak(s)");
        if (lastOpened > DateTime.MinValue) info.Add($"opened {lastOpened:d MMM yyyy}");
        text.Children.Add(new TextBlock { Text = string.Join("   -   ", info), Style = (Style)FindResource("SubText"), Margin = new Thickness(0, 4, 0, 0) });
        if (man.Changes.Count > 0)
            text.Children.Add(new TextBlock { Text = "Latest: " + man.Changes[^1].Description, FontSize = 13, Margin = new Thickness(0, 6, 16, 0), TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = root, Style = (Style)FindResource("SubText"), FontSize = 12, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap });
        text.Children.Add(remove);

        var open = new Button { Style = (Style)FindResource("Primary"), Content = "Open in NB Studio", Margin = new Thickness(8, 0, 0, 0) };
        open.Click += (_, _) => OpenInStudio(root);
        var play = new Button { Content = "Play", Margin = new Thickness(8, 0, 0, 0), ToolTip = "Play this project on your own (with its executable tweaks)" };
        play.Click += (_, _) =>
        {
            if (!Ready()) return;
            if (_game is { HasExited: false }) { MessageBox.Show(this, "The game is already running.", "NB Multiplayer"); return; }
            try { _game = GameLauncher.StartSolo(S, Path.Combine(root, "game"), man.ExeMods); }
            catch (Exception ex) { MessageBox.Show(this, "The game could not start:\n" + ex.Message, "NB Multiplayer"); }
        };
        var make = new Button { Content = "Make a mod", Margin = new Thickness(8, 0, 0, 0), ToolTip = "Turn this project into a mod (.nbpatch) in your mod library" };
        var panel = MakeModPanel(root);
        panel.Visibility = Visibility.Collapsed;
        make.Click += (_, _) => panel.Visibility = panel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        string pn = Path.GetFileName(root.TrimEnd('\\', '/'));
        System.Windows.Automation.AutomationProperties.SetName(make, "Make a mod: " + pn);
        System.Windows.Automation.AutomationProperties.SetName(play, "Play: " + pn);
        System.Windows.Automation.AutomationProperties.SetName(open, "Open in NB Studio: " + pn);
        buttons.Children.Add(make); buttons.Children.Add(play); buttons.Children.Add(open);
        outer.Children.Add(panel);
        return card;
    }

    /// <summary>"Make a mod": name, version, category (pre-selected from what the project changes) and description.</summary>
    StackPanel MakeModPanel(string root)
    {
        var p = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        var name = new TextBox { Text = Path.GetFileName(root.TrimEnd('\\', '/')) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(name, "ModName");
        var version = new TextBox { Text = "1.0", Width = 110, HorizontalAlignment = HorizontalAlignment.Left };
        var desc = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60 };
        var tags = new TextBox();
        var cats = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        var catHelp = new TextBlock { Style = (Style)FindResource("SubText"), FontSize = 12, TextWrapping = TextWrapping.Wrap };
        string chosen = "";
        string group = "cat" + Guid.NewGuid().ToString("N");
        foreach (var c in ModCategories.All)
        {
            var rb = new RadioButton { Style = (Style)FindResource("Chip"), GroupName = group, Content = c.Name, Tag = c };
            rb.Checked += (_, _) => { chosen = c.Id; catHelp.Text = c.Description; };
            cats.Children.Add(rb);
        }
        // pre-select the category from the project's changed files (file sizes only: quick)
        p.Loaded += async (_, _) =>
        {
            if (chosen.Length > 0) return;
            var guess = await Task.Run(() =>
            {
                try
                {
                    var ws = Workspace.Open(root);
                    var files = ws.ModifiedFiles(hash: false);
                    var fake = new PatchPackage.PatchManifest { Files = files.Select(f => new PatchPackage.PatchFile { Path = f.Replace('\\', '/') }).ToList() };
                    if (files.Count == 0 && ws.Manifest.ExeMods.Count > 0) return ModCategories.Tweak;
                    return ModCategories.Suggest(fake);
                }
                catch (Exception) { return ModCategories.Map; }
            });
            if (chosen.Length == 0) cats.Children.OfType<RadioButton>().First(r => r.Tag == guess).IsChecked = true;
        };
        var status = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        var bar = new ProgressBar { Maximum = 1, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        var build = new Button { Style = (Style)FindResource("Primary"), Content = "Make the mod", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) };
        build.Click += async (_, _) =>
        {
            if (name.Text.Trim().Length == 0) { status.Text = "Give the mod a name."; status.Foreground = B("Warn"); return; }
            build.IsEnabled = false; bar.Visibility = Visibility.Visible; status.Foreground = B("Sub");
            string modName = name.Text.Trim(), ver = version.Text.Trim().Length > 0 ? version.Text.Trim() : "1.0", d = desc.Text.Trim(), cat = chosen;
            var tagList = tags.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            string author = S.PlayerName;
            try
            {
                var outDir = Path.Combine(root, "mods");
                Directory.CreateDirectory(outDir);
                var file = Path.Combine(outDir, string.Concat($"{modName} {ver}".Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)) + ".nbpatch");
                var prog = new Progress<(string F, double P)>(x => { status.Text = "Comparing with your game: " + x.F; bar.Value = x.P; });
                var mod = await Task.Run(() =>
                {
                    var ws = Workspace.Open(root);
                    PatchPackage.Build(ws, file, modName, author, d.Length > 0 ? d : "Made with NB Studio", true, prog, null,
                        m => { m.Version = ver; m.Category = cat; m.Tags = tagList; m.Multiplayer = cat == "coop" ? "coop" : cat is "visual" or "audio" ? "cosmetic" : "world"; });
                    return ModLibrary.Add(file);
                });
                status.Foreground = B("Good");
                status.Text = $"\"{mod.Manifest.Name}\" {mod.Manifest.Version} is in your mod library (Mods & editions). The file to share is {file}";
                RefreshEditions();
            }
            catch (Exception ex) { status.Foreground = B("Bad"); status.Text = "The mod could not be made: " + ex.Message; }
            finally { build.IsEnabled = true; bar.Visibility = Visibility.Collapsed; }
        };
        TextBlock L(string t) => new() { Text = t, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) };
        p.Children.Add(new Border { Height = 1, Background = B("Line"), Margin = new Thickness(0, 0, 0, 4) });
        p.Children.Add(L("Mod name")); p.Children.Add(name);
        p.Children.Add(L("Version")); p.Children.Add(version);
        p.Children.Add(L("What kind of mod is it?")); p.Children.Add(cats); p.Children.Add(catHelp);
        p.Children.Add(L("Tags (optional, comma-separated)")); p.Children.Add(tags);
        p.Children.Add(L("Description")); p.Children.Add(desc);
        p.Children.Add(build); p.Children.Add(bar); p.Children.Add(status);
        return p;
    }

    // ------------------------------------------------------------------ NB Studio

    void RefreshStudio()
    {
        var exe = StudioManager.Exe(S);
        StudioOpenButton.IsEnabled = exe != null;
        if (exe == null)
        {
            StudioStatus.Text = "Not installed yet. NB Multiplayer downloads it for you from GitHub (free and open source).";
            StudioGetButton.Content = "Get NB Studio";
        }
        else
        {
            var v = StudioManager.VersionOf(exe);
            bool newer = _studioLatest != null && _studioLatest.Version > v;
            StudioStatus.Text = $"NB Studio {v}" + (newer ? $" - version {_studioLatest!.Version} is available." : _studioChecked ? " - up to date." : "") +
                                (StudioManager.IsManaged(exe) ? "" : $"\nYour own copy: {exe}");
            StudioGetButton.Content = newer ? $"Update to {_studioLatest!.Version}" : "Check for updates";
        }
        if (!_studioChecked) _ = CheckStudioAsync(install: false);
    }

    async Task CheckStudioAsync(bool install)
    {
        _studioChecked = true;
        _studioLatest = await Updater.LatestAsync(StudioManager.Repo);
        if (!install) { RefreshStudio(); return; }
        var exe = StudioManager.Exe(S);
        if (_studioLatest == null) { StudioStatus.Text = "Could not reach GitHub. Try again later."; return; }
        if (exe != null && StudioManager.VersionOf(exe) >= _studioLatest.Version) { RefreshStudio(); StudioStatus.Text += "\nYou have the latest version."; return; }
        if (exe != null)
        {
            // an update: ask first, in the update banner (Update now / What's new / Later)
            UpdateText.Text = $"NB Studio {_studioLatest.Version} is available (you have {StudioManager.VersionOf(exe)}). Update it now?";
            ShowBanner(studio: true);
            RefreshStudio();
            return;
        }
        await InstallStudioAsync(exe);
    }

    /// <summary>"Update now" in the banner for NB Studio.</summary>
    async Task InstallStudioFromBannerAsync()
    {
        if (_studioLatest == null) { UpdateBanner.Visibility = Visibility.Collapsed; return; }
        UpdateNowButton.IsEnabled = false;
        UpdateText.Text = $"Updating NB Studio to {_studioLatest.Version}... (progress on the Projects page)";
        await InstallStudioAsync(StudioManager.Exe(S));
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    async Task InstallStudioAsync(string? exe)
    {
        if (_studioLatest == null) return;
        StudioGetButton.IsEnabled = false;
        StudioProgress.Visibility = Visibility.Visible;
        try
        {
            await StudioManager.InstallAsync(_studioLatest, new Progress<(string Text, double Fraction)>(p => { StudioStatus.Text = p.Text + "..."; StudioProgress.Value = p.Fraction; }));
            if (exe != null && !StudioManager.IsManaged(exe)) { S.StudioPath = ""; S.Save(); }   // use the updated copy from now on
            RefreshStudio();
        }
        catch (Exception ex) { StudioStatus.Text = "NB Studio could not be installed: " + ex.Message; }
        finally { StudioGetButton.IsEnabled = true; StudioProgress.Visibility = Visibility.Collapsed; }
    }

    async void StudioGet_Click(object sender, RoutedEventArgs e) => await CheckStudioAsync(install: true);

    void StudioOpen_Click(object sender, RoutedEventArgs e) => OpenInStudio(null);

    void OpenInStudio(string? project)
    {
        var exe = StudioManager.Exe(S);
        if (exe == null)
        {
            if (MessageBox.Show(this, "NB Studio is not installed yet. Download it from GitHub now?", "NB Multiplayer", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                _ = CheckStudioAsync(install: true);
            return;
        }
        try
        {
            StudioManager.Launch(exe, project);
            if (project != null) ProjectRegistry.Touch(project);
        }
        catch (Exception ex) { MessageBox.Show(this, "NB Studio could not start:\n" + ex.Message, "NB Multiplayer"); }
    }

    void StudioBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "NB Studio|NBModStudio.exe", Title = "Choose NBModStudio.exe" };
        if (dlg.ShowDialog(this) != true) return;
        S.StudioPath = dlg.FileName; S.Save();
        RefreshStudio();
    }

    void StudioReleases_Click(object sender, RoutedEventArgs e) => OpenUrl(StudioManager.ReleasesPage);

    // ------------------------------------------------------------------ new / existing projects

    void ProjectAdd_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Choose an NB Studio project folder (it contains workspace.json)", UseDescriptionForTitle = true };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        if (!File.Exists(Path.Combine(dlg.SelectedPath, "workspace.json"))) { MessageBox.Show(this, "That folder is not an NB Studio project (no workspace.json).", "NB Multiplayer"); return; }
        ProjectRegistry.Touch(dlg.SelectedPath);
        RefreshProjects();
    }

    void ProjectNew_Click(object sender, RoutedEventArgs e)
    {
        if (!Ready()) return;
        NewProjectCard.Visibility = Visibility.Visible;
        if (NewProjectName.Text.Length == 0) NewProjectName.Text = "My mod";
        UpdateNewProjectHelp();
        NewProjectName.Focus();
    }

    void UpdateNewProjectHelp()
    {
        long size = 0;
        try { size = Directory.GetFiles(S.GameDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); } catch (Exception) { }
        NewProjectHelp.Text = $"A project is a copy of your game ({size / 1073741824.0:N1} GB) that NB Studio edits; your own game folder stays untouched. It is created in {ProjectsDir}.";
    }

    void ProjectFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Where new projects are created", UseDescriptionForTitle = true, SelectedPath = ProjectsDir };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        S.ProjectsDir = dlg.SelectedPath; S.Save();
        UpdateNewProjectHelp();
    }

    void ProjectNewCancel_Click(object sender, RoutedEventArgs e) => NewProjectCard.Visibility = Visibility.Collapsed;

    async void ProjectCreate_Click(object sender, RoutedEventArgs e)
    {
        var name = string.Concat(NewProjectName.Text.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (name.Length == 0) return;
        var root = Path.Combine(ProjectsDir, name);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) { MessageBox.Show(this, $"{root} already exists. Choose another name.", "NB Multiplayer"); return; }
        long size = Directory.GetFiles(S.GameDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        try
        {
            var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(ProjectsDir))!).AvailableFreeSpace;
            if (free < size + (1L << 30)) { MessageBox.Show(this, $"Not enough free space on that drive: the project needs about {size / 1073741824.0:N1} GB.", "NB Multiplayer"); return; }
        }
        catch (Exception) { }
        NewProjectCard.Visibility = Visibility.Collapsed;
        ProjectBusy.Visibility = Visibility.Visible;
        ProjectNewButton.IsEnabled = false;
        var game = S.GameDir;
        try
        {
            await Task.Run(() => Workspace.Create(game, root, new Progress<(string File, double Fraction)>(p =>
                Dispatcher.BeginInvoke(() => { ProjectBusyText.Text = $"Copying your game into the project: {p.File}"; ProjectBusyBar.Value = p.Fraction; }))));
            ProjectRegistry.Touch(root);
            ProjectBusyText.Text = $"\"{name}\" is ready. Open it in NB Studio to start editing.";
            ProjectBusyBar.Value = 1;
        }
        catch (Exception ex) { ProjectBusyText.Text = "The project could not be created: " + ex.Message; }
        finally { ProjectNewButton.IsEnabled = true; }
        RefreshProjects();
    }
}
