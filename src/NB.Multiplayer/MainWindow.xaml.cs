using System.Diagnostics;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NB.Core.Net;
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
        JoinBox.Text = S.LastJoin;
        BuildAddressOptions();
        RefreshEditions();
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
            try { _server?.Stop(); } catch (Exception) { }
            try { _steamHost?.Stop(); } catch (Exception) { }
            try { _steamClient?.Stop(); } catch (Exception) { }
            try { StopCoop(); } catch (Exception) { }
            SteamNet.Shutdown();
        };
    }

    // ------------------------------------------------------------------ navigation

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PagePlay == null) return;
        PagePlay.Visibility = NavPlay.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageEditions.Visibility = NavEditions.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = NavAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }
    void GoSettings_Click(object s, RoutedEventArgs e) => NavSettings.IsChecked = true;
    void GoEditions_Click(object s, RoutedEventArgs e) => NavEditions.IsChecked = true;

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

        bool coopInstalled = all.Any(e => e.IsCoop);
        CoopCard.Visibility = File.Exists(CoopPatch) ? Visibility.Visible : Visibility.Collapsed;
        CoopAddButton.Visibility = coopInstalled ? Visibility.Collapsed : Visibility.Visible;
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
    }

    /// <summary>The Showdown Town co-op patch shipped with the app (patches\ShowdownTownCoop.nbpatch).</summary>
    static string CoopPatch => Path.Combine(AppContext.BaseDirectory, "patches", "ShowdownTownCoop.nbpatch");

    async void AddCoop_Click(object sender, RoutedEventArgs e) => await AddEditionAsync(CoopPatch);

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
            _server = new RoomServer { RoomName = $"{S.PlayerName}'s room", HostCompat = compat, Edition = edition.Name };
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
        if (edition.IsCoop) _coopNet = CoopNet.StartHost(S.Instance, S.PlayerName, _steamHost);
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

    void StopHost_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Close the room? Players in it are disconnected.", "NB Multiplayer", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _server?.Stop(); _server = null; _room = null;
        _steamHost?.Stop(); _steamHost = null;
        StopCoop();
        HostRunning.Visibility = Visibility.Collapsed;
        HostSetup.Visibility = Visibility.Visible;
        RoomCard.Visibility = Visibility.Collapsed;
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
            // play the host's edition
            if (!string.Equals(room.Edition, S.Edition, StringComparison.OrdinalIgnoreCase))
            {
                var mine = Editions.Find(S, room.Edition);
                if (mine == null && File.Exists(CoopPatch) && PatchPackage.ReadManifest(CoopPatch).Name == room.Edition)
                {
                    // the host plays the co-op edition that ships with NB Multiplayer: install it now
                    ShowJoin($"{room.Name} plays \"{room.Edition}\". Installing it (a minute or two)...", "Sub", false);
                    try { mine = await Task.Run(() => Editions.Create(S, CoopPatch, new Progress<(string Text, double Fraction)>(_ => { }))); }
                    catch (Exception ex) { ShowJoin("The co-op edition could not be installed: " + ex.Message, "Bad", false); return; }
                }
                if (mine == null)
                {
                    ShowJoin($"{room.Name} plays the edition \"{room.Edition}\", which you don't have. Get its .nbpatch file from the host and add it under Editions & mods.", "Bad", false);
                    return;
                }
                S.Edition = mine.Name; S.Save(); RefreshEditions();
            }
            if (room.Compat != null)
            {
                ShowJoin($"Found {room.Name}. Comparing your game files with the host's (the first time takes a few minutes)...", "Sub", false);
                var edition = CurrentEdition;
                var mineCompat = await Task.Run(() => CompatProfile.FromGame(edition.GameDir));
                if (mineCompat.Fingerprint != room.Compat.Fingerprint)
                {
                    var diff = mineCompat.CompareTo(room.Compat);
                    var areas = string.Join("\n", diff.GroupBy(d => d.Area).Select(g => $"  - {g.Key}: {g.Count()} file(s)"));
                    ShowJoin($"Your game files differ from the host's:\n{areas}\nYou may not be able to join, or the game may go out of sync.", "Warn", true);
                    return;
                }
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
            _game = GameLauncher.Start(S, ed, api, code);
            if (ed.IsCoop && _coopNet != null)
            {
                _coop?.Dispose();
                _coop = new CoopService(_coopNet, _game.Id, Path.Combine(ed.GameDir, "default.xex"), ed.PuppetBlueprintId, ed.ParkVector);
            }
        }
        catch (Exception ex) { MessageBox.Show(this, "The game could not start:\n" + ex.Message, "NB Multiplayer"); }
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
        var names = new List<(string Name, bool Me)> { (S.PlayerName, true) };
        if (_coopNet != null) names.AddRange(_coopNet.Remotes.Values.Select(r => (r.Name, false)));
        foreach (var (name, me) in names)
        {
            var chip = new Border { CornerRadius = new CornerRadius(16), Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 8),
                Background = me ? B("Accent") : B("CardHi") };
            chip.Child = new TextBlock { Text = name + (me ? "  (you)" : ""), FontWeight = FontWeights.SemiBold, Foreground = me ? B("AccentText") : B("Text") };
            RoomPlayers.Items.Add(chip);
        }
        RoomPlayers.Items.Add(new TextBlock { Text = _coop?.Status ?? "Start the game to sync.", Style = (Style)FindResource("SubText"), Margin = new Thickness(0, 8, 0, 0) });
        RoomDot.Fill = B("Good");
        RoomStatus.Text = $"Co-op ({names.Count} player{(names.Count == 1 ? "" : "s")})";
    }

    async Task TickAsync()
    {
        bool running = _game is { HasExited: false };
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
    }

    void UpdatesCheck_Changed(object sender, RoutedEventArgs e) { S.CheckForUpdates = UpdatesCheck.IsChecked == true; S.Save(); }

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
            lnk.Description = "Banjo-Kazooie: Nuts & Bolts online with friends";
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
        UpdateBanner.Visibility = Visibility.Visible;
    }

    async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckUpdatesAsync(manual: true);

    async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
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

    void WhatsNew_Click(object sender, RoutedEventArgs e) => OpenUrl(_release?.PageUrl ?? Updater.ReleasesPage);
    void UpdateLater_Click(object sender, RoutedEventArgs e) => UpdateBanner.Visibility = Visibility.Collapsed;
    void UpdateSkip_Click(object sender, RoutedEventArgs e)
    {
        if (_release != null) { S.SkippedVersion = _release.Tag; S.Save(); }
        UpdateBanner.Visibility = Visibility.Collapsed;
    }
    void OpenReleases_Click(object sender, RoutedEventArgs e) => OpenUrl(Updater.ReleasesPage);
    static void OpenUrl(string url) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception) { } }
}
