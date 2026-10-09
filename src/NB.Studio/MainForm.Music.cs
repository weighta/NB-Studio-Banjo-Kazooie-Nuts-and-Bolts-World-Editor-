using System.Numerics;
using NB.Core.Audio;
using NB.Core.Formats;
using NB.Core.World;
using NB.Studio.Panels;
using NB.Studio.Viewport;

namespace NB.Studio;

/// <summary>
/// Music (docs/FORMATS.md "Music regions and tracks"): which track plays where, in every world and Act.
///  • Showdown Town: music regions (marker type 8, flag 0x200: centre, radius, region 1–6) and the day scripts' region →
///    track tables (op 0x85; night plays Park everywhere). Outside every region the town plays region 1's track.
///  • Other worlds / Acts: the Act's audio script plays one track (op 0x19); World of Sports and Terrarium switch to an
///    underwater track (op 0x85 sets 1 and 2: the game picks region 2 when Banjo is under water); some Acts play
///    Nighttime from their main script.
/// The 3D view draws the regions and a "♪ Music" speaker (Show › Music); the Scene list has a Music group; Properties
/// shows the tracks with ▶ preview, swap (another game track) and Replace… (any audio file). Region and track changes
/// wait for Save All (Ctrl+S) and are undoable; Replace… writes the music bank at once (Ctrl+Z undoes it).
/// </summary>
public sealed partial class MainForm
{
    readonly MusicPanel _musicPanel = new() { Dock = DockStyle.Bottom, Height = 390, Visible = false };
    MusicCatalog? _musicCat; object? _musicCatFor;
    List<MusicCommand> _musicCmds = new(); WorldScene? _musicCmdsFor; HashSet<uint> _musicSceneBundles = new();
    /// <summary>Unsaved track choices: (bundle, script symbol, command offset, slot) → stored and chosen cue hash.</summary>
    readonly Dictionary<(uint Bundle, int Symbol, int Offset, int Slot), (uint Saved, uint Now, MusicCommand Cmd)> _musicEdits = new();
    SceneObject? _musicShown;
    /// <summary>A Replace… is being prepared or written: Replace / ▶ are off in both panels; Undo and F5 wait; further
    /// Replace requests queue behind it (each re-reads the bank after the previous save). Closing NB Studio or the
    /// workspace cancels a replacement still being prepared (nothing of it is written yet; the write itself runs on the UI
    /// thread, so no close can come in the middle of it).</summary>
    public bool MusicBusy { get; private set; }
    readonly Queue<(MusicCue Cue, MusicAudio.ReplaceOptions Opts, string File, Action<string>? Done)> _musicQueue = new();
    /// <summary>The running replacement's cancellation (null when none runs) and its decode / encode task.</summary>
    CancellationTokenSource? _musicCts; Task? _musicTask;
    // while a close asks its questions (modal message loops dispatch the worker's BeginInvoke), a finished preparation is
    // held here instead of written, so "nothing has been written" stays true until the close goes ahead or is cancelled
    bool _musicHold; Action? _musicHeldFinish;

    /// <summary>Holds (or releases and runs) a finished preparation's write during a close's questions.</summary>
    void HoldMusic(bool on)
    {
        _musicHold = on;
        if (!on && _musicHeldFinish is { } f) { _musicHeldFinish = null; f(); }
    }
    /// <summary>Script check (--music-fail-lookup N): the next N encoder lookups throw.</summary>
    int _musicFailLookups;
    string? VgmstreamExe => _audio.VgmstreamPath;

    void InitMusic()
    {
        var prev = _transform.InfoFor;
        _transform.InfoFor = o => MusicInfo(o) ?? prev?.Invoke(o);
        _view.MusicLabel = MusicLabelOf;
        _musicPanel.RegionChanged += v => EditMusicRegion(_musicShown, region: v);
        _musicPanel.RadiusChanged += v => EditMusicRegion(_musicShown, radius: v);
        _musicPanel.CueChanged += (row, h) => SetMusicCue(row.Cmd, row.Slot, h);
        _musicPanel.Play += c => PlayMusicCue(c);
        _musicPanel.Stop += () => { MusicPreview.Stop(); Log("Music: stopped."); };
        _musicPanel.Replace += (c, opts) => ReplaceMusicCue(c, opts, null);
        _audio.Music.ReplaceRequested = (c, opts, file, done) => ReplaceMusicCue(c, opts, file, done);   // one Replace path for both panels
        FormClosed += (_, _) =>
        {
            MusicPreview.Stop();
            // a cancelled decoder / encoder is killed by its worker within a moment: let it, so none outlives NB Studio
            try { _musicTask?.Wait(3000); } catch (Exception) { }
        };
    }

    MusicCatalog? MusicCat
    {
        get
        {
            if (_ws == null) return null;
            if (_musicCatFor != _ws)
            {
                _musicCatFor = _ws;
                try { _musicCat = MusicCatalog.Load(_ws); } catch (Exception e) { _musicCat = null; Log("Music: the game's music list could not be read: " + e.Message); }
            }
            return _musicCat;
        }
    }

    static string WorldKeyOf(WorldScene s)
    {
        var n = AssetIds.DisplayName(s.Background.View.Name);
        const string pre = "aid_model_banjox_background_";
        if (n.StartsWith(pre)) n = n[pre.Length..];
        return n.EndsWith("_default") ? n[..^8] : n;
    }

    /// <summary>The music commands of the open world / Act (its bundles and the common bundle's scripts named after it).</summary>
    List<MusicCommand> MusicCmds
    {
        get
        {
            if (_scene == null || _ws == null) return new();
            if (_musicCmdsFor != _scene)
            {
                _musicCmdsFor = _scene;
                string key = WorldKeyOf(_scene);
                var bundles = new[] { _scene.Bundle & 0xFFFFFF }.Concat(_scene.MarkerBundles.Select(b => b & 0xFFFFFF)).Distinct().ToList();
                _musicCmds = new();
                foreach (var b in bundles) try { _musicCmds.AddRange(MusicCommand.Scan(_ws.LoadResident(b), b)); } catch (Exception) { }
                if (!bundles.Contains(MusicCatalog.CommonBundle))
                    try
                    {
                        _musicCmds.AddRange(MusicCommand.Scan(_ws.LoadResident(MusicCatalog.CommonBundle), MusicCatalog.CommonBundle,
                            n => n.Contains("_" + key + "_") || (key == "banjoshouseinterior" && n.Contains("_ui_frontend_"))));
                    }
                    catch (Exception e) { Log("Music: " + e.Message); }
                // worlds whose audio script is only in other bundles (Spiral Mountain's copies; the Car Park's is "testtrack")
                if (!_musicCmds.Any(c => c.When == "world music") && _index != null)
                {
                    string audio = "aid_script_banjox_common_audio_" + (key == "carpark" ? "testtrack" : key);
                    foreach (var b in _index.Entries.Where(e => !e.Streamed && e.Name == audio).Select(e => e.Bundle).Distinct().Where(b => !bundles.Contains(b)))
                        try { _musicCmds.AddRange(MusicCommand.Scan(_ws.LoadResident(b), b, n => n == audio)); } catch (Exception) { }
                }
                _musicSceneBundles = bundles.Append(MusicCatalog.CommonBundle).ToHashSet();
            }
            return _musicCmds;
        }
    }

    uint MusicHashNow(MusicCommand c, int slot) =>
        _musicEdits.TryGetValue((c.Bundle, c.Symbol, c.Offset, slot), out var e) ? e.Now : slot < 0 ? c.Hash : c.Pairs[slot].Hash;

    /// <summary>The current scan's object for a command (commands are rescanned after a reload or a save).</summary>
    MusicCommand CurrentCmd(MusicCommand c) => MusicCmds.FirstOrDefault(x => x.SameAs(c)) ?? c;

    string CueName(uint h) => MusicCat?.Find(h)?.Display.Replace("Showdown Town – ", "") ?? h.ToString("X8");

    static int WhenOrder(string w) => w.StartsWith("world") ? 0 : w.StartsWith("morning") ? 1 : w.StartsWith("midday") ? 2 : w.StartsWith("afternoon") ? 3 : w.StartsWith("night") ? 4
        : w.StartsWith("start") ? 5 : w.StartsWith("Act") ? 6 : w.StartsWith("event") ? 8 : w.StartsWith("demo") ? 9 : 7;

    /// <summary>Rows of the music panel: the commands that choose music for this region / for the whole world.</summary>
    List<MusicPanel.Row> MusicRows(SceneObject o) => MusicRowsUnsorted(o).Where(r => r.Hash != 0).OrderBy(r => WhenOrder(r.When)).ThenBy(r => r.When).ToList();

    List<MusicPanel.Row> MusicRowsUnsorted(SceneObject o)
    {
        var rows = new List<MusicPanel.Row>();
        foreach (var c in MusicCmds)
        {
            if (o.IsMusicRegion)
            {
                if (c.Op != MusicCommand.OpRegions || c.Set != 0) continue;
                for (int k = 0; k < c.Pairs.Count; k++)
                    if (c.Pairs[k].Region == o.MusicRegionId) rows.Add(new(c.When, c, k, MusicHashNow(c, k)));
            }
            else if (c.Op == MusicCommand.OpPlay) rows.Add(new(c.When + (_musicSceneBundles.Contains(c.Bundle) ? "" : $" [{c.Bundle:x6}]"), c, -1, MusicHashNow(c, -1)));
            else if (c.Set == 0)
            {
                for (int k = 0; k < c.Pairs.Count; k++)
                    if (c.Pairs[k].Region == 1) rows.Add(new(c.When + " (else)", c, k, MusicHashNow(c, k)));
            }
            else
                for (int k = 0; k < c.Pairs.Count; k++)
                    if (c.Pairs[k].Region is 1 or 2) rows.Add(new($"{c.When} ({(c.Pairs[k].Region == 1 ? "main" : "underwater")})", c, k, MusicHashNow(c, k)));
        }
        return rows;
    }

    /// <summary>The 3D view's speaker label.</summary>
    string? MusicLabelOf(SceneObject o)
    {
        var rows = MusicRows(o);
        if (rows.Count == 0) return o.IsMusicRegion ? $"Music region {o.MusicRegionId}" : "Music: set by each Act";
        rows = rows.Where(r => !r.When.StartsWith("event")).ToList();
        if (rows.Count == 0) return "Music: set by each Act";
        var day = rows.Where(r => !r.When.StartsWith("night") && !r.When.StartsWith("demo") && !r.When.StartsWith("Act ")).Select(r => CueName(r.Hash)).Distinct().ToList();
        var night = rows.Where(r => r.When.StartsWith("night")).Select(r => CueName(r.Hash)).Distinct().ToList();
        var main = day.Count > 0 ? day : rows.Select(r => CueName(r.Hash)).Distinct().Take(2).ToList();
        string s = "Music: " + string.Join(" / ", main);
        if (night.Count > 0 && !night.SequenceEqual(main)) s += " · night: " + string.Join(" / ", night);
        if (o.IsWorldMusic && _scene != null && _scene.Objects.Any(x => x.IsMusicRegion)) s += " (outside the regions)";
        return s;
    }

    /// <summary>The world-music speaker of a scene (a stand-in, no record) and bigger click boxes for its regions. OpenWorld
    /// calls it before the 3D view gets the scene (the collision decode enumerates the object list on a worker then). The
    /// speaker hangs beside the player start (4 units to its right, 6 up), so its label is drawn off the start figure; the
    /// 3D view picks it by that label only, and a marker under the pixel wins (SceneViewport.Pick).</summary>
    static SceneObject EnsureWorldMusic(WorldScene scene)
    {
        var w = scene.Objects.FirstOrDefault(o => o.IsWorldMusic);
        if (w != null) return w;
        foreach (var r in scene.Objects.Where(o => o.IsMusicRegion)) { r.BoundsMin = new Vector3(-2.5f, 0, -2.5f); r.BoundsMax = new Vector3(2.5f, 5, 2.5f); }
        var start = scene.Objects.Where(SpawnPoints.Is).OrderBy(o => SpawnPoints.KindOf(o) is SpawnPoints.Kind.TownStart or SpawnPoints.Kind.ActStart ? 0 : 1).FirstOrDefault();
        Vector3 at;
        if (start != null)
        {
            var right = new Vector3(start.Transform.M11, 0, start.Transform.M13);
            right = right.LengthSquared() > 1e-6f ? Vector3.Normalize(right) : Vector3.UnitX;
            at = start.Transform.Translation + right * 4 + new Vector3(0, 6, 0);
        }
        else
        {
            var t = scene.Objects.FirstOrDefault(o => o.Kind == SceneObjectKind.Terrain && !o.IsSkyDome);
            at = t != null ? Vector3.Transform((t.BoundsMin + t.BoundsMax) / 2, t.Transform) : Vector3.Zero;
        }
        w = new SceneObject
        {
            Id = scene.Objects.Count == 0 ? 0 : scene.Objects.Max(o => o.Id) + 1, Kind = SceneObjectKind.Terrain, IsWorldMusic = true,
            Name = "World music", ModelName = "music", Transform = Matrix4x4.CreateTranslation(at), OriginalTransform = Matrix4x4.CreateTranslation(at),
            BoundsMin = new Vector3(-1.5f, -1.5f, -1.5f), BoundsMax = new Vector3(1.5f, 1.5f, 1.5f),
        };
        scene.Objects.Add(w);
        return w;
    }

    /// <summary>Scene list: "Music (n)": the world's music and the regions by number.</summary>
    void AddMusicNodes(string q)
    {
        if (_scene == null) return;
        var world = EnsureWorldMusic(_scene);
        var regions = _scene.Objects.Where(o => o.IsMusicRegion && (q == "" || o.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || "music".Contains(q))).ToList();
        if (world == null || (q != "" && regions.Count == 0 && !"world music".Contains(q))) return;
        var mn = _tree.Nodes.Add($"Music ({regions.Count + 1})"); mn.Checked = true;
        mn.ToolTipText = "Where the game's music plays: the world's track, and Showdown Town's music regions (speakers and rings in the 3D view; Show › Music).";
        mn.Nodes.Add(new TreeNode("♪ " + (MusicLabelOf(world) ?? "World music")) { Tag = world, Checked = world.Visible, ToolTipText = "The music this world / Act plays (its level scripts)." });
        foreach (var g in regions.GroupBy(o => o.MusicRegionId).OrderBy(g => g.Key))
        {
            var gn = mn.Nodes.Add($"Region {g.Key} – {MusicRegion.DistrictName(g.Key)} ({g.Count()})"); gn.Checked = true;
            foreach (var o in g) gn.Nodes.Add(new TreeNode($"{o.Name} (radius {o.MusicRadius:0.#}){(o.Dirty ? " *" : "")}") { Tag = o, Checked = o.Visible });
        }
        mn.Expand();
    }

    /// <summary>Properties: the music section for a music region or the world speaker.</summary>
    void ShowMusicFor(SceneObject? o)
    {
        if (!SceneViewport.IsMusic(o) || MusicCat is not { } cat) { _musicShown = null; _musicPanel.HideMusic(); return; }
        _musicShown = o;
        var rows = MusicRows(o!);
        string title = o!.IsMusicRegion ? $"♪ Music region #{o.Marker!.Index}" : $"♪ Music of {_worldTitle()}";
        string note = o.IsMusicRegion
            ? "The game plays this region's track while Banjo is within the radius (measured flat: the region is an endless upright cylinder). " +
              "The first region in the list that holds him wins; outside every region the town plays region 1's track. Region and track changes wait for Save All (Ctrl+S). " +
              "Replace… puts your audio over that track's wave, so every place that plays it changes (written at once; Ctrl+Z undoes)."
            : rows.Count == 0 ? "This world has no music command of its own: each Act chooses its music (open an Act)."
            : "The track this world / Act plays (its level scripts). Pick another game track to swap it (Save All writes it), ▶ to listen, Replace… to import your own audio (WAV, MP3, OGG, FLAC, M4A, WMA).";
        var formats = rows.Select(r => cat.Find(r.Hash)).Where(c => c != null).Distinct()
            .Select(c => $"{c!.Display.Replace("Showdown Town – ", "")}: {MusicAudio.InfoOf(_ws!, c)?.ToString() ?? "?"} ({c.Bank} #{c.Wave})");
        note = string.Join(Environment.NewLine, formats) + Environment.NewLine + Environment.NewLine + note;
        _musicPanel.ShowMusic(title, o, rows, cat, note);
    }

    string _worldTitle() => _worlds.SelectedItem?.ToString()?.Trim().TrimStart('↳').Trim() ?? "this world";

    string? MusicInfo(SceneObject o)
    {
        if (o.IsWorldMusic)
            return "WORLD MUSIC: a stand-in speaker for the music this world or Act plays (no object in the game). Its tracks are listed below.";
        if (!o.IsMusicRegion) return null;
        return $"MUSIC REGION: marker type 8 #{o.Marker!.Index} with the music flag (0x200), radius {o.MusicRadius:0.##}, region {o.MusicRegionId} ({MusicRegion.DistrictName(o.MusicRegionId)}). " +
               "Move it like any marker; set its radius and region number below. Verified in Xenia: moving a region, or changing the track of a region number, changes what plays there.\n" +
               $"Marker asset {o.ModelName} at 0x{o.Marker.Offset:X}\n{(o.Dirty ? "Modified (not yet saved)" : "Unmodified")}";
    }

    void RefreshMusicViews()
    {
        _view.InvalidateMusicLabels();
        if (_musicShown != null) ShowMusicFor(_musicShown);
        UpdatePending();
    }

    /// <summary>Region number / radius (pending until World > Save). The undo step finds the region by its key in the scene
    /// open at the time, so it still works after the world was reloaded (paste, delete, an undone file step …).</summary>
    void EditMusicRegion(SceneObject? o, int? region = null, float? radius = null)
    {
        if (o is not { IsMusicRegion: true }) return;
        int r0 = o.MusicRegionId; float d0 = o.MusicRadius;
        int r1 = region ?? r0; float d1 = radius ?? d0;
        if (r1 == r0 && d1 == d0) return;
        string key = UndoHistory.KeyOf(o); int index = o.Marker!.Index;
        void Apply(int r, float d)
        {
            var x = _scene?.Objects.FirstOrDefault(y => y.IsMusicRegion && UndoHistory.KeyOf(y) == key);
            if (x == null) return;
            x.MusicRegionId = r; x.MusicRadius = d;
            RefreshMusicViews(); _transform.SetObject(_view.Selected);
        }
        Apply(r1, d1);
        _history.PushCallback($"music region #{index}", () => Apply(r0, d0), () => Apply(r1, d1));
        Log($"Music region #{index}: region {r1} ({MusicRegion.DistrictName(r1)}), radius {d1:0.#} (World > Save / Ctrl+S writes it).");
    }

    /// <summary>A track choice (pending until Save All). Works on the current scan's command (matched by bundle, script and
    /// offset), so it stays right after a reload or a save; the undo steps go through here again.</summary>
    void SetMusicCue(MusicCommand c0, int slot, uint hash, bool undoable = true)
    {
        var c = CurrentCmd(c0);
        var key = (c.Bundle, c.Symbol, c.Offset, slot);
        uint prev = MusicHashNow(c, slot), stored = slot < 0 ? c.Hash : c.Pairs[slot].Hash;
        if (prev == hash) return;
        if (hash == stored) _musicEdits.Remove(key); else _musicEdits[key] = (stored, hash, c);
        RefreshMusicViews();
        Log($"Music: {AssetIds.DisplayName(c.Script).Replace("aid_script_banjox_", "")} ({c.When}{(slot >= 0 ? $", region {c.Pairs[slot].Region}" : "")}) now plays {CueName(hash)} (Ctrl+S saves).");
        if (undoable) _history.PushCallback("music track", () => SetMusicCue(c, slot, prev, false), () => SetMusicCue(c, slot, hash, false));
    }

    int MusicPendingCount => _musicEdits.Count;

    /// <summary>
    /// Save All: writes the chosen tracks into the level scripts, each bundle once. Every edit of a bundle is checked
    /// first (the command still where it was found); when the save fails the bundle object gets its stored hashes back, so
    /// "Don't save" really drops them. The Atmosphere tab learns about the written bytes (it snapshots the same
    /// time-of-day scripts and would otherwise put the old track back on Discard). Commands are rescanned afterwards.
    /// </summary>
    void SaveMusic()
    {
        if (_ws == null || _musicEdits.Count == 0) return;
        int saved = 0;
        try
        {
            foreach (var g in _musicEdits.GroupBy(e => e.Key.Bundle).ToList())
            {
                var caff = _ws.LoadResident(g.Key);
                foreach (var e in g)
                    if (!e.Value.Cmd.StillIn(caff)) throw new InvalidDataException($"{AssetIds.DisplayName(e.Value.Cmd.Script)}: the music command is no longer where it was (the script changed); pick the track again.");
                var before = g.Select(e => (e.Value.Cmd, e.Key.Slot, Old: e.Value.Cmd.HashIn(caff, e.Key.Slot))).ToList();
                foreach (var e in g) e.Value.Cmd.SetHash(caff, e.Key.Slot, e.Value.Now);
                try { _ws.SaveResident(g.Key, caff, $"music: {g.Count()} track choice(s)"); }
                catch (Exception)
                {
                    foreach (var (cmd, slot, old) in before) cmd.SetHash(caff, slot, old);
                    throw;
                }
                foreach (var e in g)
                {
                    var data = caff.PartsOf(e.Value.Cmd.Symbol).First(p => caff.SectionOf(p).Name == ".data").Data;
                    _atmos.NoteExternalWrite(data, e.Value.Cmd.HashOffset(e.Key.Slot), 4);
                    _musicEdits.Remove(e.Key); saved++;
                }
            }
        }
        finally
        {
            if (saved > 0) Log($"Music: saved {saved} track choice(s).");
            _musicCmdsFor = null;   // rescan: the commands now hold the saved hashes
            _audio.Music.Invalidate();
            RefreshMusicViews();
        }
    }

    void DiscardMusic() { if (_musicEdits.Count == 0) return; _musicEdits.Clear(); RefreshMusicViews(); }

    /// <summary>▶: the shared preview player (a later ▶ or ■ Stop wins; nothing is left in %TEMP%).</summary>
    void PlayMusicCue(MusicCue c, Action<string>? done = null)
    {
        if (_ws == null) return;
        _status.Text = $"Preparing {c.Display}…";
        MusicPreview.Play(this, _ws, c, VgmstreamExe, _scripted, msg =>
        {
            Log("Music: " + msg); _status.Text = msg;
            done?.Invoke(msg);
        });
    }

    /// <summary>Replace…: imports an audio file over the cue's wave (asks first; lists what else plays that wave). The
    /// decode / encode runs on a worker; the bank is then written on the UI thread (so its undo step covers the whole write
    /// and nothing else can save meanwhile). A request made while one runs waits in a queue. Returns false when nothing
    /// was started or queued (no workspace, the file dialog or the question cancelled): <paramref name="done"/> isn't called.</summary>
    bool ReplaceMusicCue(MusicCue c, MusicAudio.ReplaceOptions opts, string? file, Action<string>? done = null)
    {
        if (_ws == null || MusicCat is not { } cat) return false;
        if (file == null)
        {
            using var d = new OpenFileDialog { Title = $"Replace {c.Display}", Filter = "Audio files|*.wav;*.mp3;*.ogg;*.flac;*.m4a;*.wma;*.aac;*.opus|All files|*.*" };
            if (d.ShowDialog(this) != DialogResult.OK) return false;
            file = d.FileName;
            var same = cat.Cues.Where(x => x.Bank == c.Bank && x.Wave == c.Wave).Select(x => x.Display).Distinct().ToList();
            if (MessageBox.Show(this, $"Replace the wave {c.Bank} #{c.Wave} with {Path.GetFileName(file)}?\n\nIt plays as: {string.Join(", ", same)}.\n\n" +
                    (opts.AlsoWaves.Length > 0 ? "It goes into all six district versions of the town music (Market, Seaside, Docks, Park, Posh, L.O.G.).\n\n" : "") +
                    (opts.FitLength ? "It is fitted to the original length (trimmed or repeated) so the town's district tracks stay in step.\n\n" : "") +
                    "The music bank is written at once (Ctrl+Z undoes it).", "Replace music", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return false;
        }
        if (MusicBusy)
        {
            _musicQueue.Enqueue((c, opts, file, done));
            Log($"Music: {c.Display} will be replaced after the replacement running now ({_musicQueue.Count} waiting).");
            return true;
        }
        RunMusicReplace(c, opts, file, done);
        return true;
    }

    void SetMusicBusy(bool on) { MusicBusy = on; _musicPanel.SetBusy(on); _audio.Music.SetBusy(on); }

    /// <summary>Starts one replacement: busy on, decode / encode on a worker, then <see cref="FinishMusicReplace"/> on the
    /// UI thread. Never throws: whatever fails before or after the worker ends in <see cref="EndMusicReplace"/>, which
    /// turns busy off again and starts the next waiting request.</summary>
    void RunMusicReplace(MusicCue c, MusicAudio.ReplaceOptions opts, string file, Action<string>? done)
    {
        var ws = _ws!;
        var cts = new CancellationTokenSource();
        _musicCts = cts;
        SetMusicBusy(true);
        try
        {
            _status.Text = $"Replacing {c.Display}: decoding and encoding {Path.GetFileName(file)}…";
            var enc = FindMusicEncoder();
            var vgm = VgmstreamExe;
            var prep = Task.Run(() =>
            {
                var r = MusicAudio.PrepareReplace(ws, c, file, vgm, opts, enc, cts.Token);
                // another writer of the archive at work (a part build …): wait for it here, not on the UI thread
                using (ws.LockStream(MusicCatalog.CommonBundle)) { }
                return r;
            }, cts.Token);
            _musicTask = prep;
            prep.ContinueWith(t =>
            {
                if (IsDisposed || !IsHandleCreated) return;
                try { BeginInvoke(() => FinishMusicReplace(ws, c, t, cts, done)); } catch (InvalidOperationException) { }   // the window is going away
            }, TaskScheduler.Default);
        }
        catch (Exception e)
        {
            Log($"Music: replacing {c.Display} failed: {e.Message}"); _status.Text = "Replacing failed: " + e.Message;
            EndMusicReplace(cts, "failed: " + e.Message, done);
        }
    }

    MusicEncoder.ExternalEncoder? FindMusicEncoder()
    {
        if (_musicFailLookups > 0) { _musicFailLookups--; throw new UnauthorizedAccessException("the encoder lookup failed (script check --music-fail-lookup)"); }
        return MusicEncoder.FindXma2Encoder(AppContext.BaseDirectory);
    }

    /// <summary>The prepared replacement is written here, on the UI thread (Undo, Save All and other UI-thread writers
    /// can't interleave; writers on workers wait for the archive's edit lock), as one undo step.</summary>
    void FinishMusicReplace(NB.Core.Project.Workspace ws, MusicCue c, Task<MusicAudio.PreparedReplace> t, CancellationTokenSource cts, Action<string>? done)
    {
        if (_musicHold) { _musicHeldFinish = () => FinishMusicReplace(ws, c, t, cts, done); return; }
        string result;
        try
        {
            if (cts.IsCancellationRequested || t.IsCanceled) throw new OperationCanceledException();
            if (t.IsFaulted) throw t.Exception!.GetBaseException();
            if (_ws != ws) throw new InvalidOperationException("the workspace was closed; the replacement was dropped");
            _status.Text = $"Replacing {c.Display}: writing the music bundle (about 800 MB)…"; _status.Owner?.Refresh();
            UseWaitCursor = true; Cursor.Current = Cursors.WaitCursor;
            try { result = MusicAudio.ApplyReplace(ws, t.Result); }
            finally { UseWaitCursor = false; }
            Log("Music: replaced " + result + " Test in Xenia (F5) to hear it in the game.");
            _status.Text = "Replaced " + result;
        }
        catch (OperationCanceledException) { result = "cancelled: nothing was written"; Log($"Music: replacing {c.Display} was cancelled; nothing was written."); _status.Text = "Replacing cancelled."; }
        catch (Exception e) { result = "failed: " + e.Message; Log("Music: replacing failed: " + e.Message); _status.Text = "Replacing failed: " + e.Message; }
        EndMusicReplace(cts, result, done);
    }

    /// <summary>Ends the replacement <paramref name="cts"/> belongs to (once): busy off, views refreshed, the caller
    /// told, the next waiting request started.</summary>
    void EndMusicReplace(CancellationTokenSource cts, string result, Action<string>? done)
    {
        if (_musicCts != cts) return;   // already ended
        _musicCts = null; _musicTask = null;
        SetMusicBusy(false);
        try { RefreshMusicViews(); _audio.Music.Invalidate(places: false); } catch (Exception e) { Log("Music: refreshing the music views failed: " + e.Message); }
        try { done?.Invoke(result); } catch (Exception e) { Log("Music: " + e.Message); }
        if (_musicQueue.Count > 0 && _ws != null) { var n = _musicQueue.Dequeue(); RunMusicReplace(n.Cue, n.Opts, n.File, n.Done); }
        else _musicQueue.Clear();
    }

    /// <summary>Closing NB Studio or the workspace while a replacement is prepared: asks (when <paramref name="ask"/>, not
    /// in scripted runs) whether to drop it. False: keep it (the close is cancelled).</summary>
    bool ConfirmDropMusicReplace(string closing, bool ask)
    {
        if (!MusicBusy || !ask || _scripted) return true;
        return MessageBox.Show(this, $"A music replacement is still being prepared (decoding / encoding). {closing} anyway?\n\nIt is dropped: nothing of it has been written yet.",
            "Music replacement", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
    }

    /// <summary>Drops the replacement being prepared (its worker stops; a running decoder or encoder is killed) and the
    /// waiting ones. Nothing of them has been written: the write runs on the UI thread, after the preparation.</summary>
    void CancelMusicReplace(string why)
    {
        int waiting = _musicQueue.Count; _musicQueue.Clear();
        if (_musicCts is not { } cts) return;
        cts.Cancel();
        Log($"Music: {why}: the replacement being prepared was cancelled{(waiting > 0 ? $" and {waiting} waiting dropped" : "")}; nothing of it was written.");
    }

    /// <summary>Script checks (--music-…).</summary>
    async Task<bool> MusicScript(string cmd, Func<string> next, Action<string> log)
    {
        switch (cmd)
        {
            case "--music-info":
            {
                var cat = MusicCat;
                log($"script: music: {cat?.Cues.Count ?? 0} cues; world key {(_scene != null ? WorldKeyOf(_scene) : "-")}; {MusicCmds.Count} commands; {_scene?.Objects.Count(o => o.IsMusicRegion) ?? 0} regions");
                foreach (var c in MusicCmds) log($"script:   {AssetIds.DisplayName(c.Script)} [{c.When}] op 0x{c.Op:X2} " + (c.Op == MusicCommand.OpPlay ? CueName(MusicHashNow(c, -1)) : $"set {c.Set}: " + string.Join(", ", c.Pairs.Select((p, k) => $"{p.Region}={CueName(MusicHashNow(c, k))}"))));
                return true;
            }
            case "--music-labels":
                foreach (var o in _scene?.Objects.Where(SceneViewport.IsMusic) ?? Enumerable.Empty<SceneObject>()) log($"script: {o.Name} at {o.Transform.Translation}: {MusicLabelOf(o)}");
                return true;
            case "--music-select":
            {
                var q = next();
                var o = q == "world" ? _scene!.Objects.First(x => x.IsWorldMusic) : _scene!.Objects.First(x => x.IsMusicRegion && x.Marker!.Index == int.Parse(q));
                _view.Select(o); log($"script: selected {o.Name}; music rows: {string.Join(" | ", _musicPanel.Rows.Select(r => $"{r.When}: {CueName(r.Hash)}"))}");
                return true;
            }
            case "--music-pick": { int row = int.Parse(next()); var name = next(); log("script: music pick: " + _musicPanel.ScriptPick(row, name)); return true; }
            case "--music-region": { int r = int.Parse(next()); float d = float.Parse(next(), System.Globalization.CultureInfo.InvariantCulture); EditMusicRegion(_view.Selected, r, d); log($"script: region now {_view.Selected?.MusicRegionId} radius {_view.Selected?.MusicRadius}; dirty {_view.Selected?.Dirty}"); return true; }
            case "--music-play":
            {
                var name = next(); var c = MusicCat!.Cues.First(x => x.Display.Contains(name, StringComparison.OrdinalIgnoreCase) || x.Label.Contains(name, StringComparison.OrdinalIgnoreCase));
                var tcs = new TaskCompletionSource<string>(); PlayMusicCue(c, s => tcs.TrySetResult(s));
                log("script: music play: " + await tcs.Task); return true;
            }
            case "--music-replace":
            case "--music-replace-nowait":
            {
                // --music-replace <cue> <file> <options: fit,mono,22k,loop,once,from=<s>,all (comma list, or "-")>
                var name = next(); var file = next(); var opt = next().Split(',');
                var c = MusicCat!.Cues.First(x => x.Display.Contains(name, StringComparison.OrdinalIgnoreCase) || x.Label.Contains(name, StringComparison.OrdinalIgnoreCase));
                var o = new MusicAudio.ReplaceOptions
                {
                    FitLength = opt.Contains("fit"), Mono = opt.Contains("mono"), HalfRate = opt.Contains("22k"),
                    Loop = opt.Contains("loop") ? true : opt.Contains("once") ? false : null,
                    LoopFromSeconds = opt.FirstOrDefault(x => x.StartsWith("from=")) is string fr ? double.Parse(fr[5..], System.Globalization.CultureInfo.InvariantCulture) : 0,
                    AlsoWaves = opt.Contains("all") ? new[] { 0, 1, 2, 3, 4, 5 } : Array.Empty<int>(),
                };
                var tcs = new TaskCompletionSource<string>(); ReplaceMusicCue(c, o, file, s => tcs.TrySetResult(s));
                if (cmd == "--music-replace-nowait") { log($"script: music replace started ({c.Display}); busy {MusicBusy}"); _ = tcs.Task.ContinueWith(t => BeginInvoke(() => log("script: music replace (no wait) done: " + t.Result))); return true; }
                log("script: music replace: " + await tcs.Task); return true;
            }
            case "--music-undo": log($"script: undo requested while busy={MusicBusy}"); await UndoRedo(true); log($"script: undo done; history undo now: {_history.UndoLabel ?? "-"}"); return true;
            case "--music-aim-start":
            {
                // --music-aim-start <pitch°>: the camera looks at the player start's pole top (start + 3.4 up) from 20 units,
                // so it is at the view's centre; logs the centre pixel and the speaker's label
                float pitch = float.Parse(next(), System.Globalization.CultureInfo.InvariantCulture), pr = pitch * MathF.PI / 180;
                var start = _scene!.Objects.Where(SpawnPoints.Is).OrderBy(o => SpawnPoints.KindOf(o) is SpawnPoints.Kind.TownStart or SpawnPoints.Kind.ActStart ? 0 : 1).First();
                var target = start.Transform.Translation + new Vector3(0, 3.4f, 0);
                var f = new Vector3(0, MathF.Sin(pr), MathF.Cos(pr));
                _view.SetCamera(target - f * 20, 0, pitch); _view.RenderFrame(); Application.DoEvents(); _view.RenderFrame();
                var c = _view.ScreenOf(target);
                var lr = _view.WorldMusicLabelRect;
                var picked = c is { } cp ? _view.Pick(new Point((int)MathF.Round(cp.X), (int)MathF.Round(cp.Y))).Obj?.Name ?? "-" : "-";
                var onLabel = lr is { } r ? _view.Pick(new Point(r.X + r.Width / 2, r.Y + r.Height / 2)).Obj?.Name ?? "-" : "-";
                log(FormattableString.Invariant($"script: aim at {start.Name} pole top, pitch {pitch}: centre pixel {c?.X:F0},{c?.Y:F0} picks {picked}; speaker label {(lr is { } r2 ? $"{r2.X},{r2.Y} {r2.Width}x{r2.Height}" : "not drawn")}, its centre picks {onLabel}"));
                return true;
            }
            case "--music-click-speaker":
            {
                var lr = _view.WorldMusicLabelRect;
                if (lr is not { } r) { log("script: speaker label not drawn"); return true; }
                var p = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
                log($"script: click on the speaker label at {p.X},{p.Y}: " + _view.ScriptClick(p));
                return true;
            }
            case "--music-list-cancelled":
            {
                // the Audio › Music list's Replace… when the main form's file dialog / question is cancelled (stand-in: the
                // request is refused, as ReplaceMusicCue does then): the status line must not stay on "Replacing …"
                var name = next(); log("script: music list select: " + _audio.Music.ScriptSelect(name));
                var c = MusicCat!.Cues.First(x => x.Display.Contains(name, StringComparison.OrdinalIgnoreCase) || x.Label.Contains(name, StringComparison.OrdinalIgnoreCase));
                var keep = _audio.Music.ReplaceRequested; string before = _audio.Music.StatusText;
                _audio.Music.ReplaceRequested = (_, _, _, _) => false;
                try { _audio.Music.Replace(c, null); } finally { _audio.Music.ReplaceRequested = keep; }
                log($"script: music list status before \"{before}\", after a cancelled Replace… \"{_audio.Music.StatusText}\"");
                return true;
            }
            case "--music-tool":
            {
                // --music-tool <decoder exe> <time limit s>: another decoder (a stand-in that hangs) and its time limit
                _audio.VgmstreamPath = next(); MusicEncoder.DecodeTimeout = TimeSpan.FromSeconds(double.Parse(next(), System.Globalization.CultureInfo.InvariantCulture));
                log($"script: music decoder {_audio.VgmstreamPath}, time limit {MusicEncoder.DecodeTimeout.TotalSeconds} s"); return true;
            }
            case "--music-fail-lookup": _musicFailLookups = int.Parse(next()); log($"script: the next {_musicFailLookups} encoder lookup(s) throw"); return true;
            case "--music-busy": log($"script: music busy {MusicBusy}, {_musicQueue.Count} waiting; Replace buttons enabled: {_audio.Music.ReplaceEnabled}; stream writers that waited for another: {_ws?.StreamLockWaits}"); return true;
            case "--music-rescan": _musicCmdsFor = null; log("script: music commands rescanned from the workspace's bundle objects"); return true;
            case "--music-play-nowait":
            {
                var name = next(); var c = MusicCat!.Cues.First(x => x.Display.Contains(name, StringComparison.OrdinalIgnoreCase) || x.Label.Contains(name, StringComparison.OrdinalIgnoreCase));
                PlayMusicCue(c, r => log($"script: preview of {c.Display}: {r}")); log($"script: preview of {c.Display} started"); return true;
            }
            case "--music-stop": MusicPreview.Stop(); log("script: preview stopped"); return true;
            case "--music-wait": { int ms = int.Parse(next()); for (int k = 0; k < ms / 100; k++) { await Task.Delay(100); Application.DoEvents(); } return true; }
            case "--music-idle":
                for (int k = 0; k < 6000 && (MusicBusy || _musicQueue.Count > 0); k++) { await Task.Delay(100); Application.DoEvents(); }
                log($"script: music idle (busy {MusicBusy})"); return true;
            case "--music-list":
            {
                SelectCenter("Audio"); _audio.ShowMusicPage(); Application.DoEvents();
                for (int k = 0; k < 600 && !_audio.Music.PlacesReady; k++) { await Task.Delay(100); Application.DoEvents(); }
                log("script: music list: " + _audio.Music.ScriptSelect(next())); return true;
            }
            case "--music-list-replace":
            {
                var name = next(); var file = next(); log("script: music list select: " + _audio.Music.ScriptSelect(name));
                var c = MusicCat!.Cues.First(x => x.Display.Contains(name, StringComparison.OrdinalIgnoreCase) || x.Label.Contains(name, StringComparison.OrdinalIgnoreCase));
                var tcs = new TaskCompletionSource<string>(); _audio.Music.Replace(c, file, s => tcs.TrySetResult(s));
                log("script: music list replace: " + await tcs.Task); return true;
            }
            case "--music-panel-shot":
            {
                var f = next(); Application.DoEvents();
                using var b = new Bitmap(_right.Width, _right.Height); _right.DrawToBitmap(b, new Rectangle(0, 0, _right.Width, _right.Height)); b.Save(f);
                log("script: properties captured"); return true;
            }
        }
        return false;
    }
}
