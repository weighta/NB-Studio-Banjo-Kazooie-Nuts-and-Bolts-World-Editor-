using System.Diagnostics;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NB.Core.Net;
using NB.Core.Live;
using NB.Core.Project;
using NB.Multiplayer.Services;

namespace NB.Multiplayer;

public partial class MainWindow : Window
{
    readonly AppSettings S = AppSettings.Load();
    RoomServer? _server;
    SteamNet.Host? _steamHost;
    SteamNet.Client? _steamClient;
    CoopNet? _coopNet;
    CoopService? _coop;
    /// <summary>The co-op settings of the room being joined (from the room info), applied to the joiner's game.</summary>
    CoopRoomSettings? _roomCoop;
    /// <summary>The room the game was last started for (api host:port, room code): "Start the game" in a co-op room.</summary>
    (string Api, string Code)? _lastLaunch;
    string _roomCode = "";
    (string Host, int Port)? _room;          // room being shown (own or joined)
    (string Host, int Port, string Code)? _pendingJoin;
    Process? _game;
    Updater.Release? _release;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    bool _polling;

    Brush B(string key) => (Brush)FindResource(key);

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            // dark title bar (Windows 10 2004+ / 11)
            var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int on = 1;
            DwmSetWindowAttribute(h, 20, ref on, 4);
        };
        VersionText.Text = $"v{Updater.Current}";
        AboutVersion.Text = $"NB Multiplayer {Updater.Current}";
        NameBox.Text = S.PlayerName;
        GameDirBox.Text = S.GameDir;
        UpdatesCheck.IsChecked = S.CheckForUpdates;
        _loadingSettings = true;
        AllUnlockedCheck.IsChecked = S.UseAllUnlockedSave;
        _loadingSettings = false;
        SaveCard.Visibility = Saves.Available ? Visibility.Visible : Visibility.Collapsed;
        JoinBox.Text = S.LastJoin;
        BuildAddressOptions();
        // mods that ship with NB Multiplayer (patches\*.nbpatch) are always in the library
        foreach (var bundled in Directory.Exists(BundledDir) ? Directory.GetFiles(BundledDir, "*.nbpatch") : Array.Empty<string>())
            try { ModLibrary.Add(bundled); } catch (Exception) { }
        RefreshEditions();
        _ = MakeTweaksAsync();
        RefreshSetup();
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();
        Loaded += async (_, _) =>
        {
            if (!S.ShortcutOffered)
            {
                S.ShortcutOffered = true; S.Save();
                if (MessageBox.Show(this, "Create a desktop shortcut for NB Multiplayer?", "NB Multiplayer", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    CreateShortcut();
            }
            if (S.CheckForUpdates) await CheckUpdatesAsync(manual: false);
        };
        Closing += (_, e) =>
        {
            if (_server != null && MessageBox.Show(this, "Closing NB Multiplayer closes your room: players in it are disconnected. Close anyway?",
                    "NB Multiplayer", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) { e.Cancel = true; return; }
            // never hang on exit: the same shutdown off the UI thread, at most 8 seconds
            try { Task.Run(() => ShutdownRoomAsync(client: true)).Wait(TimeSpan.FromSeconds(8)); } catch (Exception) { }
            try { Task.Run(SteamNet.Shutdown).Wait(TimeSpan.FromSeconds(3)); } catch (Exception) { }
        };
    }

    // ------------------------------------------------------------------ navigation

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PagePlay == null) return;
        PagePlay.Visibility = NavPlay.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageEditions.Visibility = NavEditions.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageProjects.Visibility = NavProjects.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (NavProjects.IsChecked == true) RefreshProjects();
        if (NavSettings.IsChecked == true) _ = CheckStorageAsync();
        PageSettings.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = NavAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }
    void GoSettings_Click(object s, RoutedEventArgs e) => NavSettings.IsChecked = true;
    void GoEditions_Click(object s, RoutedEventArgs e) => NavEditions.IsChecked = true;
    void GoProjects_Click(object s, RoutedEventArgs e) => NavProjects.IsChecked = true;

    void RefreshSetup()
    {
        var missing = new List<string>();
        if (!AppSettings.IsGameDir(S.GameDir)) missing.Add("choose your game folder");
        if (!AppSettings.IsValidName(S.PlayerName)) missing.Add("pick a player name");
        SetupCard.Visibility = missing.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SetupText.Text = "Before you can play: " + string.Join(" and ", missing) + ".";
        HostButton.IsEnabled = JoinButton.IsEnabled = missing.Count == 0;
    }

    bool Ready()
    {
        RefreshSetup();
        if (SetupCard.Visibility == Visibility.Visible) { NavSettings.IsChecked = true; return false; }
        return true;
    }

    // ------------------------------------------------------------------ editions

    Edition CurrentEdition => Editions.Find(S, S.Edition) ?? Editions.List(S)[0];

    void RefreshEditions()
    {
        var all = Editions.List(S);
        if (!all.Any(e => e.Name == S.Edition)) { S.Edition = Editions.VanillaName; S.Save(); }
        EditionChips.Items.Clear();
        foreach (var ed in all)
        {
            var rb = new RadioButton { Style = (Style)FindResource("Option"), GroupName = "edition", Content = ed.Name, Margin = new Thickness(0, 0, 8, 0), IsChecked = ed.Name == S.Edition, Tag = ed };
            rb.Checked += (_, _) => { S.Edition = ed.Name; S.Save(); EditionSub.Text = ed.Subtitle; };
            EditionChips.Items.Add(rb);
        }
        EditionSub.Text = CurrentEdition.Subtitle;

        CoopCard.Visibility = File.Exists(CoopPatch) ? Visibility.Visible : Visibility.Collapsed;
        // an edition built from an older bundled co-op mod is offered as an update
        string coopSha = File.Exists(CoopPatch) ? PatchPackage.FileSha(CoopPatch) : "";
        bool Current(string name) => all.Any(e => e.Name == name && e.Mods.Any(m => m.Sha256 == coopSha));
        bool Old(string name) => all.Any(e => e.Name == name) && !Current(name);
        CoopAddButton.Visibility = Current("Showdown Town Co-op") ? Visibility.Collapsed : Visibility.Visible;
        CoopAddButton.Content = Old("Showdown Town Co-op") ? "Update co-op edition" : "Add co-op edition";
        CoopUltraButton.Visibility = File.Exists(UltraPartsPatch) && !Current("Showdown Town Co-op + ULTRA") ? Visibility.Visible : Visibility.Collapsed;
        CoopUltraButton.Content = Old("Showdown Town Co-op + ULTRA") ? "Update co-op + ULTRA Parts" : "Add co-op + ULTRA Parts";
        EditionList.Children.Clear();
        foreach (var ed in all)
        {
            var card = new Border { Style = (Style)FindResource("CardBorder"), Margin = new Thickness(0, 0, 0, 12) };
            var dock = new DockPanel();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(buttons, Dock.Right);
            var use = new Button { Content = ed.Name == S.Edition ? "Selected" : "Play this edition", IsEnabled = ed.Name != S.Edition };
            use.Click += (_, _) => { S.Edition = ed.Name; S.Save(); RefreshEditions(); };
            buttons.Children.Add(use);
            if (!ed.IsVanilla && ed.Mods.Count > 0)
            {
                var change = new Button { Content = "Change mods", Margin = new Thickness(8, 0, 0, 0) };
                change.Click += (_, _) => StartEditing(ed);
                buttons.Children.Add(change);
            }
            if (!ed.IsVanilla)
            {
                var del = new Button { Content = "Delete", Margin = new Thickness(8, 0, 0, 0) };
                del.Click += (_, _) =>
                {
                    if (MessageBox.Show(this, $"Delete the edition \"{ed.Name}\"? (Your own game folder is not affected.)", "NB Multiplayer", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                    try { Editions.Delete(ed); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "NB Multiplayer"); }
                    RefreshEditions();
                };
                buttons.Children.Add(del);
            }
            dock.Children.Add(buttons);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = ed.Name, Style = (Style)FindResource("H2") });
            text.Children.Add(new TextBlock { Text = ed.Subtitle, Style = (Style)FindResource("SubText"), Margin = new Thickness(0, 2, 0, 0) });
            if (ed.Description.Length > 0) text.Children.Add(new TextBlock { Text = ed.Description, Margin = new Thickness(0, 8, 16, 0), FontSize = 13 });
            text.Children.Add(new TextBlock { Text = ed.IsVanilla ? (AppSettings.IsGameDir(ed.GameDir) ? ed.GameDir : "No game folder chosen yet (Settings)") : ed.GameDir,
                Style = (Style)FindResource("SubText"), FontSize = 12, Margin = new Thickness(0, 8, 0, 0) });
            dock.Children.Add(text);
            card.Child = dock;
            EditionList.Children.Add(card);
        }
        RefreshModLibrary(all);
    }

    readonly HashSet<string> _ticked = new(StringComparer.OrdinalIgnoreCase);
    string _modFilter = "";            // "" = all categories
    Edition? _editing;                 // "Change mods" of this edition

    /// <summary>Makes the built-in tweak mods from the player's game (once per game executable), then shows them.</summary>
    async Task MakeTweaksAsync()
    {
        if (!AppSettings.IsGameDir(S.GameDir)) return;
        var dir = S.GameDir;
        int n = await Task.Run(() => { try { return ModLibrary.EnsureTweaks(dir); } catch (Exception) { return 0; } });
        if (n > 0) RefreshEditions();
    }

    Border Badge(string text, bool accent = false) => new()
    {
        CornerRadius = new CornerRadius(9), Padding = new Thickness(9, 2, 9, 3), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        Background = accent ? B("Accent") : B("CardHi"),
        Child = new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = accent ? B("AccentText") : B("Sub") },
    };

    void RefreshModLibrary(List<Edition> editions)
    {
        ModList.Children.Clear();
        var mods = ModLibrary.Current(_ticked);   // newest version of each mod (and whatever is ticked, e.g. an edition's own versions)
        _ticked.RemoveWhere(sha => !mods.Any(m => m.Sha256 == sha));
        // filters: all + every category that has mods (author's category, or guessed from the files)
        var cats = mods.GroupBy(m => ModCategories.Of(m.Manifest).Id).ToDictionary(g => g.Key, g => g.Count());
        if (_modFilter.Length > 0 && !cats.ContainsKey(_modFilter)) _modFilter = "";
        ModFilters.Items.Clear();
        void Filter(string id, string text)
        {
            var rb = new RadioButton { Style = (Style)FindResource("Chip"), GroupName = "modfilter", Content = text, IsChecked = _modFilter == id };
            rb.Checked += (_, _) => { _modFilter = id; RefreshModLibrary(Editions.List(S)); };
            ModFilters.Items.Add(rb);
        }
        Filter("", $"All  {mods.Count}");
        foreach (var c in ModCategories.All.Where(c => cats.ContainsKey(c.Id))) Filter(c.Id, $"{c.Name}  {cats[c.Id]}");

        var shown = mods.Where(m => _modFilter.Length == 0 || ModCategories.Of(m.Manifest).Id == _modFilter)
            .OrderBy(m => ModLibrary.IsTweak(m) ? 1 : 0).ToList();   // community mods first, then the built-in tweaks
        if (mods.Count == 0)
            ModList.Children.Add(new TextBlock { Text = "No mods yet. Add a .nbpatch file above, join a room that plays a modded edition, or make one in NB Studio.", Style = (Style)FindResource("SubText") });
        foreach (var m in shown)
        {
            var man = m.Manifest;
            var cat = ModCategories.Of(man);
            bool tweak = ModLibrary.IsTweak(m);
            var usedBy = editions.Where(e => e.Mods.Any(x => x.Sha256 == m.Sha256)).Select(e => e.Name).ToList();
            var card = new Border { Style = (Style)FindResource("CardBorder"), Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(18, 14, 18, 14),
                BorderBrush = _ticked.Contains(m.Sha256) ? B("Accent") : B("Line") };
            var dock = new DockPanel();
            if (!tweak)
            {
                var remove = new Button { Content = "Remove", VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(12, 6, 12, 6), FontSize = 13,
                    IsEnabled = usedBy.Count == 0, ToolTip = usedBy.Count == 0 ? "Remove this mod from the library" : "Used by an edition: delete the edition first" };
                remove.Click += (_, _) => { ModLibrary.Remove(m.Sha256); RefreshEditions(); };
                DockPanel.SetDock(remove, Dock.Right);
                dock.Children.Add(remove);
            }
            var tick = new CheckBox { IsChecked = _ticked.Contains(m.Sha256), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 12, 0) };
            System.Windows.Automation.AutomationProperties.SetName(tick, "Include " + man.Name);
            tick.Checked += (_, _) => { _ticked.Add(m.Sha256); card.BorderBrush = B("Accent"); CheckCombination(); };
            tick.Unchecked += (_, _) => { _ticked.Remove(m.Sha256); card.BorderBrush = B("Line"); CheckCombination(); };
            DockPanel.SetDock(tick, Dock.Left);
            dock.Children.Add(tick);
            var text = new StackPanel();
            var title = new WrapPanel();
            title.Children.Add(new TextBlock { Text = man.Name, FontWeight = FontWeights.SemiBold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
            if (!tweak) title.Children.Add(new TextBlock { Text = $"  {man.Version}" + (man.Author.Length > 0 ? $"  by {man.Author}" : ""), Foreground = B("Sub"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            title.Children.Add(Badge(cat.Name, accent: true));
            if (tweak) title.Children.Add(Badge("Built-in"));
            foreach (var t in man.Tags.Take(4)) title.Children.Add(Badge(t));
            text.Children.Add(title);
            if (man.Description.Length > 0) text.Children.Add(new TextBlock { Text = man.Description, FontSize = 13, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
            var details = new List<string> { ModLibrary.MultiplayerText(man) };
            if (!tweak)
            {
                int files = man.Files.Count(f => f.Kind != "xexmods");
                if (files > 0) details.Add($"{files} game file(s)");
                if (man.ExeMods.Count > 0) details.Add($"{man.ExeMods.Count} executable tweak(s)");
                if (man.Ops.Count > 0) details.Add($"{man.Ops.Count} world edit(s), replayed on top of other mods");
                details.Add(m.Size >= 1048576 ? $"{m.Size / 1048576.0:N1} MB" : $"{m.Size / 1024.0:N0} KB");
            }
            if (usedBy.Count > 0) details.Add("in: " + string.Join(", ", usedBy));
            text.Children.Add(new TextBlock { Style = (Style)FindResource("SubText"), FontSize = 12, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap,
                Text = string.Join("   -   ", details) });
            dock.Children.Add(text);
            card.Child = dock;
            ModList.Children.Add(card);
        }
        CheckCombination();
    }

    List<NB.Core.Project.ModStack.Mod> TickedMods() =>
        ModLibrary.List().Where(m => _ticked.Contains(m.Sha256)).OrderBy(m => ModLibrary.IsTweak(m) ? 1 : 0).ToList();

    void CheckCombination()
    {
        var mods = TickedMods();
        BuildBar.Visibility = mods.Count > 0 || _editing != null ? Visibility.Visible : Visibility.Collapsed;
        CancelEditButton.Visibility = _editing != null ? Visibility.Visible : Visibility.Collapsed;
        CombineButton.IsEnabled = mods.Count > 0;
        CombineButton.Content = _editing != null ? $"Rebuild \"{_editing.Name}\"" : mods.Count > 1 ? $"Build an edition from {mods.Count} mods" : "Build an edition";
        BuildMods.Text = mods.Count == 0 ? "Tick at least one mod." : (_editing != null ? $"New mods of \"{_editing.Name}\": " : "Ticked: ") + string.Join(" + ", mods.Select(m => m.Manifest.Name));
        var problems = mods.Count > 1 ? NB.Core.Project.ModStack.Problems(mods) : new List<string>();
        CombineProblems.Text = problems.Count == 0 ? "" : "These mods cannot be combined yet:\n" + string.Join("\n", problems.Select(p => "  - " + p));
        var notes = mods.Count > 1 ? NB.Core.Project.ModStack.Notes(mods) : new List<string>();
        if (notes.Count > 0) BuildMods.Text += "\n" + string.Join("\n", notes);
        CombineProblems.Visibility = problems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (problems.Count > 0) CombineButton.IsEnabled = false;
    }

    /// <summary>"Change mods" of an edition: its mods are ticked; rebuilding replaces the edition (same name).</summary>
    void StartEditing(Edition ed)
    {
        _editing = ed;
        _ticked.Clear();
        foreach (var m in ed.Mods) _ticked.Add(m.Sha256);
        _modFilter = "";
        CombineName.Text = ed.Name;
        RefreshEditions();
        EditionsScroll.ScrollToVerticalOffset(ModList.TranslatePoint(new Point(0, 0), (UIElement)EditionsScroll.Content).Y - 120);
    }

    void CancelEdit_Click(object sender, RoutedEventArgs e)
    {
        _editing = null; _ticked.Clear(); CombineName.Text = "";
        RefreshEditions();
    }

    async void Combine_Click(object sender, RoutedEventArgs e)
    {
        var mods = TickedMods();
        if (mods.Count == 0) return;
        if (!AppSettings.IsGameDir(S.GameDir)) { MessageBox.Show(this, "Choose your game folder in Settings first.", "NB Multiplayer"); return; }
        if (_game is { HasExited: false }) { MessageBox.Show(this, "Close the game first: editions cannot change while it runs.", "NB Multiplayer"); return; }
        EditionBusy.Visibility = Visibility.Visible;
        EditionBusyText.Text = "Preparing..."; EditionBusyBar.Value = 0;
        CombineButton.IsEnabled = false;
        var prog = new Progress<(string Text, double Fraction)>(p => { EditionBusyText.Text = p.Text; EditionBusyBar.Value = p.Fraction; });
        string name = CombineName.Text.Trim();
        var editing = _editing;
        EditionsScroll.ScrollToTop();
        try
        {
            Edition ed;
            if (editing != null)
            {
                // build the new recipe next to the old edition, then swap it in under the old name
                var keepName = name.Length > 0 ? name : editing.Name;
                ed = await Task.Run(() => Editions.Create(S, mods, Editions.FreeName(S, keepName + " (new)"), prog));
                var built = ed;
                ed = await Task.Run(() => { Editions.Delete(editing); return Editions.Rename(S, built, keepName); });
            }
            else ed = await Task.Run(() => Editions.Create(S, mods, name, prog));
            S.Edition = ed.Name; S.Save();
            EditionBusyText.Text = $"\"{ed.Name}\" is ready and selected for playing.";
            _ticked.Clear(); CombineName.Text = ""; _editing = null;
        }
        catch (Exception ex) { EditionBusyText.Text = "The edition could not be built: " + ex.Message; }
        RefreshEditions();
    }

    // ------------------------------------------------------------------ play solo

    void Solo_Click(object sender, RoutedEventArgs e)
    {
        if (!Ready()) return;
        if (_game is { HasExited: false }) { MessageBox.Show(this, "The game is already running.", "NB Multiplayer"); return; }
        var ed = CurrentEdition;
        if (_coopNet != null && _lastLaunch is { } room) { _ = StartRoomGameAsync(room.Api, room.Code); return; }   // in a co-op room: the room's game
        try { _game = GameLauncher.StartSolo(S, ed.GameDir); }
        catch (Exception ex) { MessageBox.Show(this, "The game could not start:\n" + ex.Message, "NB Multiplayer"); }
    }

    /// <summary>The Showdown Town co-op patch shipped with the app (patches\ShowdownTownCoop.nbpatch).</summary>
    static string BundledDir => Path.Combine(AppContext.BaseDirectory, "patches");
    static string CoopPatch => Path.Combine(BundledDir, "ShowdownTownCoop.nbpatch");
    static string UltraPartsPatch => Path.Combine(BundledDir, "UltraParts.nbpatch");

    /// <summary>"Add co-op edition": Showdown Town Co-op with every vehicle part unlocked (whatever each player's save has).</summary>
    async void AddCoop_Click(object sender, RoutedEventArgs e) => await AddCoopEditionAsync(withUltra: false);

    /// <summary>The same with the ULTRA Parts mod: both players build with the ULTRA parts in town.</summary>
    async void AddCoopUltra_Click(object sender, RoutedEventArgs e) => await AddCoopEditionAsync(withUltra: true);

    async Task AddCoopEditionAsync(bool withUltra)
    {
        if (!AppSettings.IsGameDir(S.GameDir)) { MessageBox.Show(this, "Choose your game folder in Settings first.", "NB Multiplayer"); return; }
        EditionBusy.Visibility = Visibility.Visible;
        EditionBusyText.Text = "Preparing..."; EditionBusyBar.Value = 0;
        EditionsScroll.ScrollToTop();
        var prog = new Progress<(string Text, double Fraction)>(p => { EditionBusyText.Text = p.Text; EditionBusyBar.Value = p.Fraction; });
        string dir = S.GameDir;
        try
        {
            var ed = await Task.Run(() =>
            {
                ModLibrary.EnsureTweaks(dir);
                var mods = new List<NB.Core.Project.ModStack.Mod> { ModLibrary.Add(CoopPatch) };
                if (withUltra) mods.Insert(0, ModLibrary.Add(UltraPartsPatch));
                var allParts = ModLibrary.List().FirstOrDefault(m => m.Id == "tweak-developer-all-parts");
                if (allParts != null) mods.Add(allParts);
                string name = withUltra ? "Showdown Town Co-op + ULTRA" : "Showdown Town Co-op";
                if (Editions.Find(S, name) is { } old && old.RecipeKey != NB.Core.Project.ModStack.Key(mods.Select(m => m.Sha256)))
                    Editions.Delete(old);   // the co-op edition of an older NB Multiplayer: replaced by the current one
                return Editions.Create(S, mods, name, prog);
            });
            S.Edition = ed.Name; S.Save();
            EditionBusyText.Text = $"\"{ed.Name}\" is ready and selected for playing.";
        }
        catch (Exception ex) { EditionBusyText.Text = "The edition could not be built: " + ex.Message; }
        RefreshEditions();
    }

    /// <summary>
    /// "Add a modded game folder": a game folder someone modded by hand becomes a mod (NB.Core GameDiff: compared with the
    /// retail fingerprints, differences taken against a clean copy). When the player's own game folder is the modded one,
    /// NB Multiplayer offers to use the clean copy as the game folder from now on (editions are built on a clean game).
    /// </summary>
    async void AddModdedFolder_Click(object sender, RoutedEventArgs e)
    {
        var pick = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the modded game folder (with default.xex and Bundle)" };
        if (pick.ShowDialog(this) != true) return;
        var mod = pick.FolderName;
        if (!AppSettings.IsGameDir(mod)) { MessageBox.Show(this, "That folder is not a game folder (it needs default.xex and a Bundle folder).", "NB Multiplayer"); return; }
        if (_game is { HasExited: false }) { MessageBox.Show(this, "Close the game first.", "NB Multiplayer"); return; }
        EditionBusy.Visibility = Visibility.Visible;
        EditionBusyText.Text = "Preparing..."; EditionBusyBar.Value = 0;
        EditionsScroll.ScrollToTop();
        var prog = new Progress<(string Text, double Fraction)>(p => { EditionBusyText.Text = p.Text; EditionBusyBar.Value = p.Fraction; });
        var candidates = new List<string?> { S.GameDir };
        foreach (var p in NB.Core.Project.ProjectRegistry.Load())
        {
            try { var wj = Path.Combine(p.Path, "workspace.json"); if (File.Exists(wj)) candidates.Add(System.Text.Json.JsonDocument.Parse(File.ReadAllText(wj)).RootElement.GetProperty("OriginalPath").GetString()); }
            catch (Exception) { }
        }
        try
        {
            var cmp = await Task.Run(() => NB.Core.Project.GameDiff.CompareWithRetail(mod,
                new Progress<(string Text, double Fraction)>(p => ((IProgress<(string, double)>)prog).Report(("Comparing with the original game: " + p.Text, 0.6 * p.Fraction)))));
            if (cmp.All(c => c.State == "missing")) { EditionBusyText.Text = "That folder is the unmodified game: there is no mod in it."; return; }
            var changed = cmp.Where(c => c.State == "changed").Select(c => c.Path).ToList();
            string? clean = await Task.Run(() => NB.Core.Project.GameDiff.FindReference(candidates, changed, mod));
            while (clean == null && changed.Count > 0)
            {
                MessageBox.Show(this, "To turn this folder into a mod, NB Multiplayer also needs an unmodified copy of the game (the mod holds only the differences, " +
                    "no game data).\n\nChoose a clean copy of the game next. If you have none, extract the game disc again into a new folder.", "Add a modded game folder");
                var cp = new Microsoft.Win32.OpenFolderDialog { Title = "Choose an unmodified copy of the game" };
                if (cp.ShowDialog(this) != true) { EditionBusyText.Text = "Cancelled: no clean copy of the game was chosen."; return; }
                clean = await Task.Run(() => NB.Core.Project.GameDiff.FindReference(new[] { cp.FolderName }, changed, mod));
                if (clean == null) MessageBox.Show(this, "That copy is modified too (or is not a game folder).", "Add a modded game folder");
            }
            var rep = await Task.Run(() => NB.Core.Project.GameDiff.Analyze(mod, clean,
                new Progress<(string Text, double Fraction)>(p => ((IProgress<(string, double)>)prog).Report((p.Text, 0.6 + 0.2 * p.Fraction))), default, cmp));
            var name = rep.SuggestedName.Length > 0 ? rep.SuggestedName : "My modded game";
            var tmp = Path.Combine(Path.GetTempPath(), NB.Core.Project.PatchPackage.Slug(name) + ".nbpatch");
            await Task.Run(() => NB.Core.Project.PatchPackage.BuildFromFolders(rep, tmp, name, S.PlayerName, rep.SuggestedDescription,
                new Progress<(string File, double Fraction)>(p => ((IProgress<(string, double)>)prog).Report(("Making the mod: " + p.File, 0.8 + 0.2 * p.Fraction)))));
            var added = ModLibrary.Add(tmp);
            File.Delete(tmp);
            _ticked.Add(added.Sha256);
            EditionBusyBar.Value = 1;
            var cat = NB.Core.Project.ModCategories.Of(added.Manifest).Name;
            EditionBusyText.Text = $"\"{added.Manifest.Name}\" is in your mods ({cat}; {rep.Carried.Count()} file(s)" +
                (added.Manifest.ExeMods.Count > 0 ? $", {added.Manifest.ExeMods.Count} executable change(s)" : "") + "). It is ticked below: build an edition with it." +
                (rep.Warnings.Count > 0 ? "\n" + string.Join("\n", rep.Warnings) : "");
            if (clean != null && string.Equals(Path.GetFullPath(mod).TrimEnd('\\'), Path.GetFullPath(S.GameDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                && MessageBox.Show(this, $"Your game folder is the modded one. Use the clean copy\n{clean}\nas your game folder from now on?\n\n" +
                    "Editions are built on a clean game; your changes stay available as the mod \"" + added.Manifest.Name + "\".", "NB Multiplayer", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                S.GameDir = clean; S.Save();
            }
        }
        catch (Exception ex) { EditionBusyText.Text = "The folder could not be turned into a mod: " + ex.Message; }
        RefreshEditions();
    }

    async void AddEdition_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "NB patch (*.nbpatch)|*.nbpatch", Title = "Choose a patch" };
        if (dlg.ShowDialog(this) != true) return;
        await AddEditionAsync(dlg.FileName);
    }

    async Task AddEditionAsync(string patch)
    {
        if (!AppSettings.IsGameDir(S.GameDir)) { MessageBox.Show(this, "Choose your game folder in Settings first.", "NB Multiplayer"); return; }
        if (!File.Exists(patch)) { MessageBox.Show(this, "The patch file is missing: " + patch, "NB Multiplayer"); return; }
        EditionBusy.Visibility = Visibility.Visible;
        EditionBusyText.Text = "Preparing..."; EditionBusyBar.Value = 0;
        var prog = new Progress<(string Text, double Fraction)>(p => { EditionBusyText.Text = p.Text; EditionBusyBar.Value = p.Fraction; });
        try
        {
            var ed = await Task.Run(() => Editions.Create(S, patch, prog));
            S.Edition = ed.Name; S.Save();
            EditionBusyText.Text = $"\"{ed.Name}\" is ready and selected for playing.";
        }
        catch (Exception ex) { EditionBusyText.Text = "The edition could not be built: " + ex.Message; }
        RefreshEditions();
    }

    // ------------------------------------------------------------------ hosting

    void BuildAddressOptions()
    {
        AddressOptions.Children.Clear();
        foreach (var ip in Net.LocalIPv4())
        {
            var rb = new RadioButton { Style = (Style)FindResource("Option"), GroupName = "addr", Tag = ip, IsChecked = S.HostAddress == ip };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = "On my home network" });
            sp.Children.Add(new TextBlock { Text = ip, Style = (Style)FindResource("SubText"), FontSize = 12 });
            rb.Content = sp;
            AddressOptions.Children.Add(rb);
        }
        if (S.HostAddress.StartsWith("public:")) { PublicOption.IsChecked = true; PublicIpBox.Text = S.HostAddress[7..]; }
        if (!AddressOptions.Children.OfType<RadioButton>().Any(r => r.IsChecked == true) && PublicOption.IsChecked != true)
            SteamOption.IsChecked = true;
    }

    async void FindPublicIp_Click(object sender, RoutedEventArgs e)
    {
        PublicOption.IsChecked = true;
        PublicIpBox.Text = "looking up...";
        PublicIpBox.Text = await Net.PublicIpAsync() ?? "";
        if (PublicIpBox.Text.Length == 0) MessageBox.Show(this, "Could not look up your public address. Type it in (your router's status page shows it).", "NB Multiplayer");
    }

    async void Host_Click(object sender, RoutedEventArgs e)
    {
        if (!Ready()) return;
        string ip;
        bool steam = SteamOption.IsChecked == true;
        if (steam)
        {
            if (!SteamNet.Init()) { MessageBox.Show(this, SteamNet.Error, "NB Multiplayer"); return; }
            ip = Net.LocalIPv4().FirstOrDefault() ?? "127.0.0.1";
            S.HostAddress = "steam";
        }
        else if (PublicOption.IsChecked == true)
        {
            ip = PublicIpBox.Text.Trim();
            if (!IPAddress.TryParse(ip, out var pa) || pa.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            { MessageBox.Show(this, "Enter your public IPv4 address (or click \"Find my address\").", "NB Multiplayer"); return; }
            S.HostAddress = "public:" + ip;
        }
        else
        {
            ip = AddressOptions.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string ?? "";
            if (ip.Length == 0) { MessageBox.Show(this, "Choose where your friends are.", "NB Multiplayer"); return; }
            S.HostAddress = ip;
        }
        S.Save();
        var edition = CurrentEdition;
        if (Net.PortInUse(Net.Port))
        {
            MessageBox.Show(this, $"Port {Net.Port} is already in use on this PC: is another NB Multiplayer (or NB Studio room server) hosting a room? Close that room first.", "NB Multiplayer");
            return;
        }
        HostButton.IsEnabled = false;
        HostButton.Content = "Checking your game files...";
        try
        {
            var compat = await Task.Run(() => CompatProfile.FromGame(edition.GameDir));
            // is the host's own game folder the original game? (joiners whose files differ are told whose folder is modified)
            var hostBase = edition.IsVanilla ? compat : await Task.Run(() => CompatProfile.FromGame(S.GameDir));
            var baseModified = NB.Core.Project.GameDiff.NotRetail(hostBase);
            _server = new RoomServer
            {
                RoomName = $"{S.PlayerName}'s room", HostCompat = compat, Edition = edition.Name, HostBaseModified = baseModified,
                Recipe = edition.Mods.ToList(), ModFile = ModLibrary.Find,
            };
            _server.Start(Net.Port, "0.0.0.0");
            _roomCode = RoomCode.Encode(IPAddress.Parse(ip), Net.Port, compat.Tag);
            if (steam)
            {
                _steamHost = SteamNet.Host.Start() ?? throw new InvalidOperationException("Steam could not open a relay socket.");
                _roomCode = RoomCode.EncodeSteam(SteamNet.MySteamId, compat.Tag);
            }
        }
        catch (Exception ex)
        {
            _server?.Stop(); _server = null;
            _steamHost?.Stop(); _steamHost = null;
            MessageBox.Show(this, "The room could not start:\n" + ex.Message + "\n\n(Is another room server already using port 36000?)", "NB Multiplayer");
            HostButton.IsEnabled = true; HostButton.Content = "Start hosting";
            return;
        }
        HostButton.IsEnabled = true; HostButton.Content = "Start hosting";
        RoomCodeText.Text = _roomCode;
        HostAddressText.Text = steam
            ? $"Through Steam as {Steamworks.SteamClient.Name}. On your home network friends can also use {ip}:{Net.Port}   -   edition: {edition.Name}"
            : $"Friends can also join with {ip}:{Net.Port}   -   edition: {edition.Name}";
        HostSetup.Visibility = Visibility.Collapsed;
        HostRunning.Visibility = Visibility.Visible;
        InternetTestButton.Visibility = PublicOption.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        try { Clipboard.SetText(_roomCode); } catch (Exception) { }
        _room = ("127.0.0.1", Net.Port);
        if (steam)
        {
            ReachText.Foreground = B("Good");
            ReachText.Text = "Room code copied to the clipboard. Friends join through Steam: no port forwarding needed.";
        }
        else await CheckReachAsync();
        if (edition.IsCoop)
        {
            _coopNet = CoopNet.StartHost(S.Instance, S.PlayerName, _steamHost);
            _coopNet.TimeOfDay = S.CoopTimeOfDay is >= 1 and <= 4 ? S.CoopTimeOfDay : Random.Shared.Next(1, 5);
            _coopNet.AllUnlocked = S.UseAllUnlockedSave;
            if (_server != null) _server.Coop = new CoopRoomSettings { Protocol = CoopNet.Protocol };
            SyncRoomSettings();
        }
        LaunchGame("127.0.0.1:" + Net.Port, _roomCode);
    }

    async Task CheckReachAsync()
    {
        ReachText.Foreground = B("Sub");
        ReachText.Text = "Checking that other PCs can reach this room...";
        var lan = Net.LocalIPv4().FirstOrDefault();
        bool ok = lan != null && await Net.CanReachSelfAsync(lan, Net.Port);
        ReachText.Foreground = ok ? B("Good") : B("Bad");
        ReachText.Text = ok
            ? "Room code copied to the clipboard. This PC accepts connections" + (PublicOption.IsChecked == true ? "; for internet play your router must forward TCP 36000 and UDP 36001 here." : ".")
            : "Something on this PC blocks incoming connections (Windows Firewall, Portmaster or an antivirus firewall). Allow incoming connections for NBMultiplayer.exe, or friends cannot join.";
    }

    async void InternetTest_Click(object sender, RoutedEventArgs e)
    {
        var ip = S.HostAddress.StartsWith("public:") ? S.HostAddress[7..] : "";
        if (ip.Length == 0) return;
        ReachText.Foreground = B("Sub");
        ReachText.Text = $"Asking canyouseeme.org to connect to {ip}:{Net.Port}...";
        var r = await Net.InternetCheckAsync(ip, Net.Port);
        ReachText.Foreground = r == true ? B("Good") : r == false ? B("Bad") : B("Warn");
        ReachText.Text = r == true ? $"Reachable from the internet: {ip}:{Net.Port} works. (UDP 36001 can only be confirmed by a friend joining.)"
            : r == false ? $"Not reachable from the internet. Check the router's port forwarding (TCP 36000 and UDP 36001 to this PC) and this PC's firewall."
            : "The internet check did not give an answer; try again later.";
    }

    void HostLaunch_Click(object sender, RoutedEventArgs e) => LaunchGame("127.0.0.1:" + Net.Port, _roomCode);

    async void StopHost_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Close the room? Players in it are disconnected.", "NB Multiplayer", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        HostRunning.Visibility = Visibility.Collapsed;
        HostSetup.Visibility = Visibility.Visible;
        RoomCard.Visibility = Visibility.Collapsed;
        HostButton.IsEnabled = false; HostButton.Content = "Closing the room...";
        await ShutdownRoomAsync(client: false);
        HostButton.IsEnabled = true; HostButton.Content = "Start hosting";
    }

    /// <summary>
    /// Stops the room server, the Steam sockets and co-op off the UI thread (each step may wait for sockets and threads;
    /// on the UI thread a stuck step froze the whole app), each with a time limit, logged to data\shutdown.log.
    /// </summary>
    async Task ShutdownRoomAsync(bool client)
    {
        var server = _server; var steamHost = _steamHost; var steamClient = client ? _steamClient : null;
        var coop = _coop; var coopNet = _coopNet;
        _server = null; _room = null; _steamHost = null; if (client) _steamClient = null; _coop = null; _coopNet = null;
        var log = Path.Combine(AppSettings.DataDir, "shutdown.log");
        async Task Step(string what, Action a)
        {
            var sw = Stopwatch.StartNew();
            var t = Task.Run(a);
            bool done = await Task.WhenAny(t, Task.Delay(TimeSpan.FromSeconds(6))) == t;
            try { File.AppendAllText(log, $"{DateTime.Now:HH:mm:ss.fff} {what}: {(done ? (t.IsFaulted ? "failed " + t.Exception?.InnerException?.Message : "ok") : "still running after 6 s, left behind")} ({sw.ElapsedMilliseconds} ms){Environment.NewLine}"); }
            catch (Exception) { }
        }
        await Step("co-op", () => coop?.Dispose());
        await Step("co-op network", () => coopNet?.Dispose());
        await Step("room server", () => server?.Stop());
        await Step("Steam host", () => steamHost?.Stop());
        await Step("Steam client", () => steamClient?.Stop());
    }

    void CopyCode_Click(object sender, RoutedEventArgs e) { try { Clipboard.SetText(_roomCode); } catch (Exception) { } }

    // ------------------------------------------------------------------ joining

    void JoinBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) Join_Click(sender, e); }

    async void Join_Click(object sender, RoutedEventArgs e)
    {
        if (!Ready()) return;
        string code = Net.ExtractCode(JoinBox.Text);
        if (code.Length > 0) JoinBox.Text = code;
        string host; int port;
        if (RoomCode.TryDecodeSteam(code, out var hostSteamId, out _))
        {
            // through Steam: a local tunnel on 127.0.0.1:36000/36001 carries everything to the host
            if (!SteamNet.Init()) { ShowJoin(SteamNet.Error ?? "Steam is not available.", "Bad", false); return; }
            JoinButton.IsEnabled = false;
            JoinProgress.Visibility = Visibility.Visible;
            ShowJoin("Connecting to the host through Steam...", "Sub", false);
            try
            {
                _steamClient?.Stop();
                _steamClient = await SteamNet.Client.ConnectAsync(hostSteamId, TimeSpan.FromSeconds(30));
            }
            catch (Exception ex)
            {
                _steamClient = null; ShowJoin(ex.Message, "Bad", false);
                JoinButton.IsEnabled = true; JoinProgress.Visibility = Visibility.Collapsed;
                return;
            }
            if (_steamClient == null)
            {
                ShowJoin("Could not reach the host through Steam. Is their room open and their Steam running?", "Bad", false);
                JoinButton.IsEnabled = true; JoinProgress.Visibility = Visibility.Collapsed;
                return;
            }
            _steamClient.Lost += () => Dispatcher.Invoke(() => ShowJoin("The Steam connection to the host was lost.", "Bad", false));
            (host, port) = ("127.0.0.1", SteamNet.TunnelPort);
        }
        else if (!Net.TryParseTarget(JoinBox.Text, out host, out port))
        { ShowJoin("That is not a room code or address.", "Bad", false); return; }
        _pendingJoin = (host, port, code);
        JoinButton.IsEnabled = false;
        JoinProgress.Visibility = Visibility.Visible;
        ShowJoin($"Contacting the room at {host}:{port}...", "Sub", false);
        try
        {
            var room = await Net.GetRoomAsync(host, port, TimeSpan.FromSeconds(8));
            if (room == null)
            {
                ShowJoin("The room could not be reached. Check the code, and ask the host whether the room is open and the ports are forwarded.", "Bad", true);
                return;
            }
            _roomCoop = room.Coop;
            // the host's edition: if the selected one differs, the player decides (match / join anyway / cancel)
            var choice = await ChooseEditionForRoomAsync(room, host, port);
            if (choice == MatchHostDialog.Choice.Cancel) { ShowJoin("Join cancelled.", "Sub", false); return; }
            if (room.Compat != null && choice != MatchHostDialog.Choice.JoinAnyway)
            {
                ShowJoin($"Found {room.Name}. Comparing your game files with the host's (the first time takes a few minutes)...", "Sub", false);
                var edition = CurrentEdition;
                var mineCompat = await Task.Run(() => CompatProfile.FromGame(edition.GameDir));
                if (mineCompat.Fingerprint != room.Compat.Fingerprint)
                {
                    var diff = mineCompat.CompareTo(room.Compat);
                    var areas = string.Join("\n", diff.GroupBy(d => d.Area).Select(g => $"  - {g.Key}: {g.Count()} file(s)"));
                    // whose game folder is not the original game? (editions are built on each player's own game folder)
                    var mineBase = await Task.Run(() => CompatProfile.FromGame(S.GameDir));
                    var mineMod = NB.Core.Project.GameDiff.NotRetail(mineBase);
                    var hostMod = room.HostBaseModified;
                    string Files(List<string> l) => string.Join(", ", l.Take(4)) + (l.Count > 4 ? $" and {l.Count - 4} more" : "");
                    string why = mineMod.Count > 0
                        ? $"Your game folder is not the original game ({mineMod.Count} changed file(s): {Files(mineMod)}). Point Settings at an unmodified copy of the game; your changes can become a mod with Mods & editions > Add a modded game folder."
                        : hostMod is { Count: > 0 }
                            ? $"The host's game folder is not the original game ({hostMod.Count} changed file(s): {Files(hostMod)}). The host should point Settings at an unmodified copy (their changes can become a mod with Add a modded game folder)."
                            : hostMod != null
                                ? "Both game folders are the original game, so the difference is in the built edition: delete the edition in Mods & editions on both PCs and join again (it is rebuilt)."
                                : "Your game folder is the original game; the host's could not be checked (older NB Multiplayer).";
                    ShowJoin($"Your game files differ from the host's:\n{areas}\n{why}\nYou may not be able to join, or the game may go out of sync.", "Warn", true);
                    return;
                }
            }
            if (CurrentEdition.IsCoop && room.Coop?.Protocol != CoopNet.Protocol)
            {
                ShowJoin(room.Coop == null
                    ? $"{room.Name} runs an older NB Multiplayer: Showdown Town co-op needs the same version on every PC. Ask the host to update (About & updates)."
                    : $"{room.Name} runs a different NB Multiplayer version (co-op protocol {room.Coop.Protocol}, yours {CoopNet.Protocol}). Update both to the latest version.", "Bad", false);
                return;
            }
            ShowJoin(CurrentEdition.IsCoop
                ? $"Joined {room.Name} ({room.Edition}). In the game choose SINGLE PLAYER and load your save (or start a new game): you see each other once you are both in Showdown Town."
                : $"Joining {room.Name} ({room.Edition}). In the game open MULTIPLAYER, then Xbox LIVE: you join the host's party automatically.", "Good", false);
            StartJoined();
        }
        finally
        {
            JoinButton.IsEnabled = true;
            JoinProgress.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// The player's edition matching the host's: found by recipe, or built from it (mods the player lacks are downloaded
    /// from the host's room). Hosts older than 1.2 only name their edition. Null (with the reason shown) when impossible.
    /// </summary>
    async Task<Edition?> HostEditionAsync(RoomInfo room, string host, int port)
    {
        if (room.Recipe == null)
        {
            var named = Editions.Find(S, room.Edition);
            if (named == null && File.Exists(CoopPatch) && PatchPackage.ReadManifest(CoopPatch).Name == room.Edition)
            {
                ShowJoin($"{room.Name} plays \"{room.Edition}\". Installing it (a minute or two)...", "Sub", false);
                try { named = await Task.Run(() => Editions.Create(S, CoopPatch, new Progress<(string Text, double Fraction)>(_ => { }))); }
                catch (Exception ex) { ShowJoin("The co-op edition could not be installed: " + ex.Message, "Bad", false); return null; }
            }
            if (named == null)
                ShowJoin($"{room.Name} plays the edition \"{room.Edition}\", which you don't have. Get its .nbpatch file from the host and add it under Editions & mods.", "Bad", false);
            return named;
        }
        if (room.Recipe.Count == 0) return Editions.Find(S, Editions.VanillaName);
        var have = Editions.FindRecipe(S, room.Recipe);
        if (have != null) return have;
        var mods = new List<NB.Core.Project.ModStack.Mod>();
        foreach (var r in room.Recipe)
        {
            var m = ModLibrary.Get(r.Sha256);
            if (m == null)
            {
                string what = $"{room.Name} plays \"{room.Edition}\". Getting {r.Name} {r.Version} from the host ({r.Size / 1048576.0:N1} MB)";
                ShowJoin(what + "...", "Sub", false);
                try { m = await Net.DownloadModAsync(host, port, r, new Progress<double>(f => JoinResultText.Text = $"{what}: {f:P0}")); }
                catch (Exception ex) { ShowJoin($"Could not get \"{r.Name}\" from the host: {ex.Message}", "Bad", false); return null; }
            }
            mods.Add(m);
        }
        ShowJoin($"Building the edition \"{room.Edition}\" ({string.Join(" + ", mods.Select(x => x.Manifest.Name))}); a minute or two...", "Sub", false);
        try { return await Task.Run(() => Editions.Create(S, mods, room.Edition, new Progress<(string Text, double Fraction)>(_ => { }))); }
        catch (Exception ex) { ShowJoin("The host's edition could not be built: " + ex.Message, "Bad", false); return null; }
    }

    /// <summary>Does the selected edition carry the same mods as the room's (same recipe; rooms of hosts older than 1.2
    /// only name their edition)?</summary>
    bool MatchesRoom(RoomInfo room) =>
        room.Recipe != null
            ? CurrentEdition.RecipeKey == NB.Core.Project.ModStack.Key(room.Recipe.Select(r => r.Sha256))
            : CurrentEdition.Name == room.Edition;

    /// <summary>
    /// Joining (or starting the room's game again): when the selected edition is not the host's, the player chooses:
    /// match the host (their edition is found or built, missing mods downloaded from the room, and selected), join
    /// anyway with their own, or cancel. Nothing is switched without asking.
    /// </summary>
    async Task<MatchHostDialog.Choice> ChooseEditionForRoomAsync(RoomInfo room, string host, int port)
    {
        if (MatchesRoom(room)) return MatchHostDialog.Choice.Match;
        var mine = CurrentEdition;
        var hostMods = room.Recipe?.Select(r => $"{r.Name} {r.Version}") ?? new[] { "(not listed by the host's NB Multiplayer)" };
        var hostName = room.Players.FirstOrDefault(p => p.Xuid == room.HostXuid).Name is { Length: > 0 } hn ? hn : room.Name.Replace("'s room", "");
        var dlg = new MatchHostDialog(this, hostName, room.Edition, hostMods,
            mine.Name, mine.Mods.Select(m => $"{m.Name} {m.Version}"));
        dlg.ShowDialog();
        if (dlg.Result != MatchHostDialog.Choice.Match) return dlg.Result;
        var theirs = await HostEditionAsync(room, host, port);
        if (theirs == null) return MatchHostDialog.Choice.Cancel;   // the reason is shown
        if (theirs.Name != S.Edition) { S.Edition = theirs.Name; S.Save(); RefreshEditions(); }
        return MatchHostDialog.Choice.Match;
    }

    /// <summary>
    /// "Start the game" / Play solo in a co-op room the player joined: the host may have changed mods since (and
    /// started their game again), so the room is asked first; a different edition is offered again.
    /// </summary>
    async Task StartRoomGameAsync(string api, string code)
    {
        if (_server == null && _pendingJoin is { } j)
        {
            var room = await Net.GetRoomAsync(j.Host, j.Port, TimeSpan.FromSeconds(6));
            if (room != null && !MatchesRoom(room))
            {
                var editionBefore = S.Edition;
                var choice = await ChooseEditionForRoomAsync(room, j.Host, j.Port);
                if (choice == MatchHostDialog.Choice.Cancel) return;
                if (S.Edition != editionBefore)
                {
                    // another edition: connect co-op again for it (its game folder, its co-op settings)
                    _roomCoop = room.Coop;
                    StartJoined();
                    return;
                }
            }
            else if (room != null) _roomCoop = room.Coop;
        }
        LaunchGame(api, code);
    }

    /// <summary>
    /// Host: the room advertises the edition the host's game runs now. A host who closes the game, picks other mods and
    /// starts again (room still open) used to keep advertising the first edition, so joiners kept being switched back
    /// to it. The fingerprint of the game files is computed in the background while the game boots.
    /// </summary>
    async Task RefreshRoomEditionAsync(Edition ed)
    {
        if (_server is not { } server) return;
        if (server.Edition == ed.Name && server.Recipe != null && NB.Core.Project.ModStack.Key(server.Recipe.Select(r => r.Sha256)) == ed.RecipeKey) return;
        server.Edition = ed.Name;
        server.Recipe = ed.Mods.ToList();
        HostAddressText.Text = System.Text.RegularExpressions.Regex.Replace(HostAddressText.Text, "edition: .*$", "edition: " + ed.Name);
        try
        {
            var compat = await Task.Run(() => CompatProfile.FromGame(ed.GameDir));
            var hostBase = ed.IsVanilla ? compat : await Task.Run(() => CompatProfile.FromGame(S.GameDir));
            if (_server != server) return;
            server.HostCompat = compat;
            server.HostBaseModified = NB.Core.Project.GameDiff.NotRetail(hostBase);
        }
        catch (Exception) { }
    }

    void JoinAnyway_Click(object sender, RoutedEventArgs e) { if (_pendingJoin != null) StartJoined(); }

    void StartJoined()
    {
        if (_pendingJoin is not { } j) return;
        S.LastJoin = JoinBox.Text.Trim(); S.Save();
        _room = (j.Host, j.Port);
        JoinAnywayButton.Visibility = Visibility.Collapsed;
        if (CurrentEdition.IsCoop)
        {
            StopCoop();
            try { _coopNet = CoopNet.StartClient(S.Instance, S.PlayerName, _steamClient, j.Host); }
            catch (Exception ex) { ShowJoin("Co-op could not connect: " + ex.Message, "Bad", false); return; }
            // the room's settings, before the game starts: time of day for the first town load, all-unlocked save
            _coopNet.TimeOfDay = _roomCoop?.TimeOfDay ?? 0;
            _coopNet.AllUnlocked = _roomCoop?.AllUnlockedSave ?? false;
        }
        LaunchGame($"{j.Host}:{j.Port}", j.Code);
    }

    void ShowJoin(string text, string color, bool anyway)
    {
        JoinResult.Visibility = Visibility.Visible;
        JoinResultText.Text = text;
        JoinResultText.Foreground = B(color);
        JoinAnywayButton.Visibility = anyway ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ game + room status

    void LaunchGame(string api, string code)
    {
        if (_game is { HasExited: false })
        {
            MessageBox.Show(this, "The game is already running. Close it first to connect it to a different room.", "NB Multiplayer");
            return;
        }
        try
        {
            var ed = CurrentEdition;
            bool coop = ed.IsCoop && _coopNet != null;
            // co-op: the room's all-unlocked save applies to everyone (on top of the player's own setting)
            _game = GameLauncher.Start(S, ed, api, code, coop && _coopNet!.AllUnlocked ? true : null);
            _lastLaunch = (api, code);
            if (_server != null) _ = RefreshRoomEditionAsync(ed);   // host: the room now carries this edition
            if (coop)
                _coop ??= new CoopService(_coopNet!, () => _game, Path.Combine(ed.GameDir, "default.xex"), ed.PuppetBlueprintId, ed.ParkVector);
        }
        catch (Exception ex) { MessageBox.Show(this, "The game could not start:\n" + ex.Message, "NB Multiplayer"); }
    }

    /// <summary>Host: the room's co-op settings (time of day, all-unlocked save) as joiners see them.</summary>
    void SyncRoomSettings()
    {
        if (_coopNet == null || _server?.Coop is not { } c) return;
        _coopNet.AllUnlocked = S.UseAllUnlockedSave;
        c.TimeOfDay = _coopNet.TimeOfDay; c.AllUnlockedSave = S.UseAllUnlockedSave;
    }

    void StopCoop()
    {
        _coop?.Dispose(); _coop = null;   // also disposes the network
        _coopNet?.Dispose(); _coopNet = null;
    }

    /// <summary>Co-op games run single-player and never contact the room server: the room panel lists the co-op players.</summary>
    void ShowCoopRoom()
    {
        RoomCard.Visibility = Visibility.Visible;
        RoomTitle.Text = "Co-op room  -  " + CurrentEdition.Name;
        RoomPlayers.Items.Clear();
        static string Doing(CoopState st) => st.Mode switch
        {
            CoopMode.Vehicle or CoopMode.OnFoot when st.Flags.HasFlag(CoopFlags.Photo) => "taking photos",
            CoopMode.Vehicle or CoopMode.OnFoot when st.Flags.HasFlag(CoopFlags.Menu) => "in the pause menu",
            CoopMode.Vehicle => "in town", CoopMode.OnFoot => "on foot", CoopMode.Building => "changing vehicle",
            CoopMode.Paused => "paused", CoopMode.Garage => "in Mumbo's garage", _ => "not in town",
        };
        // the vehicle each player drives (its design's name, e.g. "Trolley Mk. 6")
        static string Veh(string? name, CoopState st) => st.Mode == CoopMode.Vehicle && !string.IsNullOrWhiteSpace(name)
            ? ", " + System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant()) : "";
        var names = new List<(string Name, bool Me, string Doing)> { (S.PlayerName, true, _game is { HasExited: false } ? Doing(_coop?.Local ?? default) + Veh(_coop?.LocalVehicle, _coop?.Local ?? default) : "game not running") };
        if (_coopNet != null) names.AddRange(_coopNet.Remotes.Select(kv => (kv.Value.Name, false, Doing(kv.Value.State)
            + Veh(_coopNet.Designs.TryGetValue(kv.Key, out var d) && d.Hash == kv.Value.State.Design ? d.Name : null, kv.Value.State))));
        foreach (var (name, me, doing) in names)
        {
            var chip = new Border { CornerRadius = new CornerRadius(16), Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 8),
                Background = me ? B("Accent") : B("CardHi") };
            chip.Child = new TextBlock { Text = name + (me ? "  (you)" : "") + "  -  " + doing, FontWeight = FontWeights.SemiBold, Foreground = me ? B("AccentText") : B("Text") };
            RoomPlayers.Items.Add(chip);
        }
        if (_coopNet != null && !_coopNet.OtherVersions.IsEmpty)
            RoomPlayers.Items.Add(new TextBlock { Text = $"{string.Join(", ", _coopNet.OtherVersions.Keys)}: a different NB Multiplayer version, not shown in your game. Everyone needs the same version (About & updates).",
                Foreground = B("Warn"), TextWrapping = TextWrapping.Wrap, MaxWidth = 700, Margin = new Thickness(0, 4, 0, 4) });
        if (_game is not { HasExited: false } && _lastLaunch is { } again)
        {
            var start = new Button { Style = (Style)FindResource("Primary"), Content = "Start the game", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 8) };
            start.Click += async (_, _) => { await StartRoomGameAsync(again.Api, again.Code); ShowCoopRoom(); };
            RoomPlayers.Items.Add(start);
        }
        RoomPlayers.Items.Add(new TextBlock { Text = _coop?.Status ?? "Start the game to sync.", Style = (Style)FindResource("SubText"), Margin = new Thickness(0, 8, 0, 0) });
        // Showdown Town's time of day is the same for everyone in the room (the host chooses; it applies when the town loads)
        var tod = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        tod.Children.Add(new TextBlock { Text = "Time of day", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 8) });
        int now = _coopNet?.TimeOfDay ?? 0;
        if (_server != null)
        {
            for (int i = 0; i <= 4; i++)
            {
                int v = i;
                var rb = new RadioButton { Style = (Style)FindResource("Chip"), GroupName = "tod", Content = CoopNet.TimeNames[i] + (i == 0 && S.CoopTimeOfDay == 0 && now > 0 ? $" ({CoopNet.TimeNames[now]})" : ""),
                    IsChecked = S.CoopTimeOfDay == i };
                rb.Checked += (_, _) =>
                {
                    S.CoopTimeOfDay = v; S.Save();
                    if (_coopNet != null) _coopNet.TimeOfDay = v > 0 ? v : Random.Shared.Next(1, 5);
                    SyncRoomSettings();
                    ShowCoopRoom();
                };
                tod.Children.Add(rb);
            }
        }
        else tod.Children.Add(new TextBlock { Text = now > 0 ? CoopNet.TimeNames[now] + " (chosen by the host)" : "set by the host", Style = (Style)FindResource("SubText"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) });
        RoomPlayers.Items.Add(tod);
        bool allUnlocked = _coopNet?.AllUnlocked ?? false;
        RoomPlayers.Items.Add(new TextBlock
        {
            Text = _server != null
                ? (allUnlocked ? "Everyone starts with the all-unlocked save (your setting in Settings)." : "Players use their own saves (turn on the all-unlocked save in Settings to give it to everyone).")
                : (allUnlocked ? "The room plays with the all-unlocked save: your game starts with it." : "The room plays with each player's own save."),
            Style = (Style)FindResource("SubText"), FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 700, Margin = new Thickness(0, 0, 0, 4),
        });
        RoomPlayers.Items.Add(new TextBlock { Text = "Everyone's Showdown Town uses the room's time of day. A change applies the next time the town loads (for example after visiting a world).",
            Style = (Style)FindResource("SubText"), FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 700 });
        RoomDot.Fill = B("Good");
        RoomStatus.Text = $"Co-op ({names.Count} player{(names.Count == 1 ? "" : "s")})";
    }

    bool _gameWasRunning;

    async Task TickAsync()
    {
        bool running = _game is { HasExited: false };
        // the game just closed: keep copies of the blueprints saved in it (BlueprintVault)
        if (_gameWasRunning && !running) _ = Task.Run(() => { try { BlueprintVault.Harvest(GameLauncher.StudioContentRoots()); } catch (Exception) { } });
        _gameWasRunning = running;
        GameDot.Fill = running ? B("Good") : B("Sub");
        GameStatus.Text = running ? "Game running" : "Game not running";
        if (_polling) return;
        if (_room is not { } r)
        {
            RoomDot.Fill = B("Sub"); RoomStatus.Text = "Not in a room";
            return;
        }
        if (_coopNet != null) { ShowCoopRoom(); return; }
        _polling = true;
        try
        {
            var info = await Net.GetRoomAsync(r.Host, r.Port, TimeSpan.FromSeconds(4));
            if (info == null)
            {
                RoomDot.Fill = B("Bad"); RoomStatus.Text = "Room not reachable";
                return;
            }
            RoomDot.Fill = B("Good");
            RoomStatus.Text = _server != null ? $"Hosting ({info.Players.Count} in room)" : $"In {info.Name}";
            RoomCard.Visibility = Visibility.Visible;
            RoomTitle.Text = $"{info.Name}  -  {info.Edition}";
            RoomPlayers.Items.Clear();
            if (info.Players.Count == 0)
                RoomPlayers.Items.Add(new TextBlock { Text = "Nobody is connected yet: players appear once their game has started.", Style = (Style)FindResource("SubText") });
            foreach (var p in info.Players)
            {
                bool isHost = p.Xuid == info.HostXuid;
                var chip = new Border { CornerRadius = new CornerRadius(16), Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 8),
                    Background = isHost ? B("Accent") : B("CardHi") };
                chip.Child = new TextBlock { Text = p.Name + (isHost ? "  (host)" : ""), FontWeight = FontWeights.SemiBold,
                    Foreground = isHost ? B("AccentText") : B("Text") };
                RoomPlayers.Items.Add(chip);
            }
        }
        finally { _polling = false; }
    }

    // ------------------------------------------------------------------ settings

    void NameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var n = NameBox.Text.Trim();
        if (AppSettings.IsValidName(n)) { S.PlayerName = n; S.Save(); NameError.Text = ""; }
        else NameError.Text = "Use letters and digits (and spaces), starting with a letter, up to 15 characters.";
        RefreshSetup();
    }

    void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Choose your Nuts & Bolts game folder (default.xex + Bundle folder)", UseDescriptionForTitle = true };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        if (!AppSettings.IsGameDir(dlg.SelectedPath)) { GameDirError.Text = "That folder has no default.xex and Bundle folder."; return; }
        GameDirError.Text = "";
        S.GameDir = dlg.SelectedPath; S.Save();
        GameDirBox.Text = S.GameDir;
        RefreshSetup(); RefreshEditions();
        _ = MakeTweaksAsync();
    }

    void UpdatesCheck_Changed(object sender, RoutedEventArgs e) { S.CheckForUpdates = UpdatesCheck.IsChecked == true; S.Save(); }

    bool _loadingSettings;

    void AllUnlocked_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        S.UseAllUnlockedSave = AllUnlockedCheck.IsChecked == true; S.Save();
        SyncRoomSettings();   // a co-op host's room reflects it to everyone who starts their game from now on
        if (_game is { HasExited: false }) { SaveStatus.Text = "Takes effect the next time the game starts (close the game first to switch saves now)."; return; }
        try
        {
            if (S.UseAllUnlockedSave)
            {
                int n = Saves.Install();
                SaveStatus.Text = n > 0 ? "The all-unlocked save is in place. Your own save is set aside." : "It is put in place when the game starts (your profile is created then).";
            }
            else
            {
                int n = Saves.Restore();
                SaveStatus.Text = n > 0 ? "Your own save is back." : "";
            }
        }
        catch (Exception ex) { SaveStatus.Text = "The save could not be changed: " + ex.Message; }
    }

    // ------------------------------------------------------------------ disk space

    async void StorageCheck_Click(object sender, RoutedEventArgs e) => await CheckStorageAsync();

    /// <summary>What the editions really take (files that are not hard links to the game), and whether some are full
    /// copies on another drive than the game (then "Free up space" rebuilds them next to the game as links).</summary>
    async Task CheckStorageAsync()
    {
        StorageText.Text = "Measuring...";
        var s = S;
        var (eds, own, copies) = await Task.Run(() =>
        {
            var list = Editions.List(s).Where(x => !x.IsVanilla).ToList();
            var target = s.EditionsDirFor(s.GameDir);
            long total = list.Sum(Editions.OwnSize);
            int far = list.Count(x => !Path.GetFullPath(x.GameDir).StartsWith(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase));
            return (list.Count, total, far);
        });
        StorageText.Text = $"{eds} edition(s) use {own / 1073741824.0:N1} GB of their own (unchanged game files are shared with your game folder as links)." +
            (copies > 0 ? $" {copies} of them are full copies of the game because they were made on another drive than your game folder: \"Free up space\" rebuilds them next to your game ({s.EditionsDirFor(s.GameDir)}), where they share its files." : "");
        CompactButton.Visibility = copies > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    async void Compact_Click(object sender, RoutedEventArgs e)
    {
        if (_game is { HasExited: false }) { MessageBox.Show(this, "Close the game first.", "NB Multiplayer"); return; }
        CompactButton.IsEnabled = false;
        var prog = new Progress<(string Text, double Fraction)>(p => StorageText.Text = $"{p.Text} ({p.Fraction:P0})");
        string result;
        try
        {
            long freed = await Task.Run(() => Editions.Compact(S, prog));
            result = $"Done: {Math.Max(0, freed) / 1073741824.0:N1} GB freed.";
            RefreshEditions();
        }
        catch (Exception ex) { result = "Could not rebuild the editions: " + ex.Message; }
        try { File.AppendAllText(Path.Combine(AppSettings.DataDir, "storage.log"), $"{DateTime.Now:u} {result}{Environment.NewLine}"); } catch (Exception) { }
        CompactButton.IsEnabled = true;
        await CheckStorageAsync();
        StorageText.Text = result + " " + StorageText.Text;
    }

    void Shortcut_Click(object sender, RoutedEventArgs e) { CreateShortcut(); MessageBox.Show(this, "Shortcut created on the desktop.", "NB Multiplayer"); }

    static void CreateShortcut()
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell")!;
            dynamic shell = Activator.CreateInstance(t)!;
            dynamic lnk = shell.CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "NB Multiplayer.lnk"));
            lnk.TargetPath = Environment.ProcessPath;
            lnk.WorkingDirectory = AppContext.BaseDirectory;
            lnk.Description = "Banjo-Kazooie: Nuts & Bolts online, co-op and mods";
            lnk.Save();
        }
        catch (Exception) { }
    }

    void OpenData_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppSettings.Root);
        Process.Start(new ProcessStartInfo(AppSettings.Root) { UseShellExecute = true });
    }

    // ------------------------------------------------------------------ updates

    async Task CheckUpdatesAsync(bool manual)
    {
        UpdateStatus.Text = "Checking GitHub for updates...";
        CheckUpdatesButton.IsEnabled = false;
        _release = await Updater.LatestAsync();
        CheckUpdatesButton.IsEnabled = true;
        if (_release == null) { UpdateStatus.Text = "Could not find a release on GitHub (offline, or no release published yet)."; return; }
        if (_release.Version <= Updater.Current) { UpdateStatus.Text = $"You have the latest version ({Updater.Current})."; return; }
        UpdateStatus.Text = $"Version {_release.Version} is available (you have {Updater.Current}).";
        if (!manual && S.SkippedVersion == _release.Tag) return;
        UpdateText.Text = $"NB Multiplayer {_release.Version} is available (you have {Updater.Current}).";
        ShowBanner(studio: false);
    }

    /// <summary>What the update banner offers: NB Multiplayer itself, or NB Studio (Projects page).</summary>
    bool _bannerForStudio;

    /// <summary>The update banner drops down from the top (a second chance to decide: Update now / What's new / Later).</summary>
    void ShowBanner(bool studio)
    {
        _bannerForStudio = studio;
        UpdateSkipButton.Visibility = studio ? Visibility.Collapsed : Visibility.Visible;
        UpdateNowButton.IsEnabled = true;
        UpdateProgress.Visibility = Visibility.Collapsed;
        UpdateBanner.Visibility = Visibility.Visible;
        UpdateBanner.UpdateLayout();
        var drop = new System.Windows.Media.Animation.DoubleAnimation(-Math.Max(40, UpdateBanner.ActualHeight), 0, TimeSpan.FromMilliseconds(260))
        { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } };
        UpdateBannerSlide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, drop);
    }

    async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckUpdatesAsync(manual: true);

    async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_bannerForStudio) { await InstallStudioFromBannerAsync(); return; }
        if (_release == null) return;
        if ((_server != null || _game is { HasExited: false }) &&
            MessageBox.Show(this, "Updating restarts NB Multiplayer and closes your room. Continue?", "NB Multiplayer", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        UpdateNowButton.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateText.Text = $"Downloading NB Multiplayer {_release.Version}...";
        try
        {
            await Updater.StageAndLaunchAsync(_release, new Progress<double>(p => UpdateProgress.Value = p));
            _server?.Stop(); _server = null;
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            UpdateText.Text = "The update failed: " + ex.Message;
            UpdateNowButton.IsEnabled = true;
            UpdateProgress.Visibility = Visibility.Collapsed;
        }
    }

    void WhatsNew_Click(object sender, RoutedEventArgs e) => OpenUrl(_bannerForStudio ? (_studioLatest?.PageUrl ?? StudioManager.ReleasesPage) : (_release?.PageUrl ?? Updater.ReleasesPage));
    void UpdateLater_Click(object sender, RoutedEventArgs e) => UpdateBanner.Visibility = Visibility.Collapsed;
    void UpdateSkip_Click(object sender, RoutedEventArgs e)
    {
        if (_release != null) { S.SkippedVersion = _release.Tag; S.Save(); }
        UpdateBanner.Visibility = Visibility.Collapsed;
    }
    void OpenReleases_Click(object sender, RoutedEventArgs e) => OpenUrl(Updater.ReleasesPage);
    static void OpenUrl(string url) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception) { } }
}
