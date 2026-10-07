using System.Numerics;
using NB.Core.Formats;
using NB.Core.Havok;
using NB.Core.World;
using NB.Studio.Viewport;

namespace NB.Studio;

/// <summary>
/// Collision editing (toolbar "Edit Collision", View menu): the Havok collision of the open world can be selected in the
/// 3D view and changed. Every Havok asset that is a mesh collision stored in the world bundle can be edited — the world
/// (terrain) collision and the collision of scenery models (one asset per model: an edit changes every instance of that
/// model, which Properties says). Edits are kept in memory (one undo step each) and written by World > Save
/// (Ctrl+S): the asset is rebuilt with HkCollisionImport (new extended-mesh buffers and MOPP, per-triangle materials kept)
/// and verified by decoding it again.
/// <list type="bullet">
/// <item>Click: the whole piece under the mouse (triangles connected through shared corners), the flat face (Alt, or the
/// "Face" pick size) or one triangle; Shift adds / removes; a left-drag selects every triangle inside the rectangle.</item>
/// <item>The selection is moved / turned / scaled like an object (G / R / T, the gizmo, typed values in Properties): a
/// stand-in object at its centre is selected in the view.</item>
/// <item>Del deletes it, Ctrl+C / Ctrl+X / Ctrl+V copy, cut and paste it (pasted where the mouse points, into the world
/// collision), Ctrl+D duplicates it; the right-click menu adds a box / ramp / plane where the mouse points, makes a
/// scenery model's collision from its render mesh, or removes it.</item>
/// </list>
/// </summary>
public sealed partial class MainForm
{
    /// <summary>Loaded collision assets of the open world (null: not editable; reason in <see cref="_soupWhy"/>).</summary>
    readonly Dictionary<string, CollisionSoup?> _soups = new();
    readonly Dictionary<string, string> _soupWhy = new();
    readonly Dictionary<string, CollisionMesh> _soupMesh = new();
    /// <summary>Selected triangles per asset, and where each asset sits in the world (the object it was picked on).</summary>
    readonly Dictionary<string, HashSet<int>> _collSel = new();
    readonly Dictionary<string, (SceneObject Obj, Matrix4x4 ToWorld)> _collAt = new();
    SceneObject? _collProxy;
    Matrix4x4 _collProxyBase;
    enum PickSize { Piece, Face, Triangle }
    PickSize _pickSize = PickSize.Piece;
    ToolStripButton? _collModeBtn;
    ToolStripComboBox? _pickSizeBox;
    /// <summary>Copied collision triangles (world space).</summary>
    (List<Vector3> P, List<int> T)? _collClip;

    bool CollisionDirty => _soups.Values.Any(s => s?.Dirty == true);

    void InitCollisionEditing()
    {
        _view.CollisionOverride = a => _soupMesh.GetValueOrDefault(a);
        _view.CollisionSelection = () => _collSel;
        _view.CollisionPreview = () => _collProxy != null && _view.Selected == _collProxy && _view.Transforming && _collAt.Count > 0
            ? (_collAt.Values.First().Obj, Matrix4x4.Invert(_collProxyBase, out var inv) ? inv * _collProxy.Transform : Matrix4x4.Identity) : null;
        _view.CollisionPicked += OnCollisionPicked;
        _view.CollisionRectSelected += OnCollisionRect;
        _view.CollisionContextMenuRequested += (hit, p) => { BuildCollisionMenu(hit, p); _objMenu.Show(_view, p); };
        _view.CollisionModeChanged += on =>
        {
            if (_collModeBtn != null) _collModeBtn.Checked = on;
            if (!on) ClearCollisionSelection();
            Log(on ? "Edit Collision: click a collision piece (Shift adds, Alt: a flat face), drag a rectangle, then G / R / T, Del, Ctrl+C / X / V / D; right-click for shapes. World > Save writes it."
                   : "Edit Collision off.");
            UpdateCollisionStatus();
        };
    }

    ToolStripItem[] CollisionToolbarItems()
    {
        _collModeBtn = new ToolStripButton("Edit Collision") { CheckOnClick = true, ToolTipText = "Select and edit the Havok collision in the 3D view (click a piece, drag a rectangle; G / R / T, Del, right-click for shapes). World > Save writes it." };
        _collModeBtn.CheckedChanged += (_, _) => _view.CollisionMode = _collModeBtn.Checked;
        _pickSizeBox = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90, ToolTipText = "What a click selects in Edit Collision: a whole piece (connected triangles), a flat face, or one triangle (Alt+click: a flat face)" };
        _pickSizeBox.Items.AddRange(new object[] { "Piece", "Face", "Triangle" });
        _pickSizeBox.SelectedIndex = 0;
        _pickSizeBox.SelectedIndexChanged += (_, _) => _pickSize = (PickSize)_pickSizeBox.SelectedIndex;
        return new ToolStripItem[] { new ToolStripSeparator(), _collModeBtn, _pickSizeBox };
    }

    ToolStripMenuItem CollisionViewMenuItem()
    {
        var mi = new ToolStripMenuItem("Edit Collision (select collision in the 3D view)") { CheckOnClick = true };
        mi.CheckedChanged += (_, _) => { if (_view.CollisionMode != mi.Checked) _view.CollisionMode = mi.Checked; };
        _view.CollisionModeChanged += on => { if (mi.Checked != on) mi.Checked = on; };
        return mi;
    }

    // ------------------------------------------------------------------ assets

    /// <summary>The editable copy of a collision asset of the open world (loaded on first use); null when it can't be
    /// edited (the reason is logged once).</summary>
    CollisionSoup? Soup(string asset)
    {
        if (_scene == null) return null;
        if (_soups.TryGetValue(asset, out var s)) return s;
        int sym = _scene.Caff.Symbols.FindIndex(x => AssetIds.DisplayName(x) == asset) + 1;
        string? why = null;
        if (sym == 0) why = "it is not stored in this world's bundle (shared game data, e.g. characters and pickups)";
        else
            try { s = CollisionSoup.Load(_scene.Caff, sym, out why); }
            catch (Exception e) { why = e.Message; }
        _soups[asset] = s;
        if (s == null) { _soupWhy[asset] = why ?? "?"; Log($"Collision {Short(asset)} can't be edited: {why}"); }
        return s;
    }

    static string Short(string asset) => asset.Replace("aid_havok_banjox_background_", "").Replace("showdowntown_showdowntownreferences_", "");

    /// <summary>After an edit: the view shows the asset's new triangles.</summary>
    void CollisionChanged(string asset)
    {
        if (_soups.GetValueOrDefault(asset) is { } s) _soupMesh[asset] = s.ToMesh();
        _view.InvalidateCollisionAsset(asset);
        UpdateTitle();
    }

    /// <summary>Objects whose collision is this asset (an edit changes all of them).</summary>
    List<SceneObject> CollisionUsers(string asset) => _view.CollisionInstances().Where(x => x.Asset == asset).Select(x => x.Obj).Distinct().ToList();

    // ------------------------------------------------------------------ selection

    void OnCollisionPicked(SceneViewport.CollisionHit? hit, bool shift, bool alt)
    {
        if (hit is not { } h) { if (!shift) ClearCollisionSelection(); UpdateCollisionStatus(); return; }
        var s = Soup(h.Asset);
        if (s == null) { _status.Text = $"Collision of {h.Obj.Name} ({Short(h.Asset)}): can't be edited — {_soupWhy.GetValueOrDefault(h.Asset)}"; return; }
        if (h.Tri >= s.Triangles) return;
        var size = alt ? PickSize.Face : _pickSize;
        var tris = size switch { PickSize.Face => s.CoplanarRegion(h.Tri), PickSize.Triangle => new HashSet<int> { h.Tri }, _ => s.ConnectedPiece(h.Tri) };
        if (!shift) { _collSel.Clear(); _collAt.Clear(); }
        if (!_collSel.TryGetValue(h.Asset, out var set)) _collSel[h.Asset] = set = new();
        if (shift && tris.All(set.Contains)) set.ExceptWith(tris); else set.UnionWith(tris);
        if (set.Count == 0) _collSel.Remove(h.Asset);
        _collAt[h.Asset] = (h.Obj, h.ToWorld);
        CollisionSelectionChanged();
    }

    void OnCollisionRect(List<(SceneObject Obj, string Asset, Matrix4x4 ToWorld, List<int> Tris)> found, bool shift)
    {
        if (!shift) { _collSel.Clear(); _collAt.Clear(); }
        int skipped = 0;
        foreach (var (o, asset, toWorld, tris) in found)
        {
            var s = Soup(asset);
            if (s == null) { skipped += tris.Count; continue; }
            if (!_collSel.TryGetValue(asset, out var set)) _collSel[asset] = set = new();
            set.UnionWith(tris.Where(t => t < s.Triangles));
            _collAt.TryAdd(asset, (o, toWorld));   // the first object an asset was found on places it
        }
        if (skipped > 0) Log($"{skipped} triangle(s) in the rectangle belong to collision that can't be edited (not selected).");
        CollisionSelectionChanged();
    }

    void ClearCollisionSelection()
    {
        _collSel.Clear(); _collAt.Clear();
        // the view may still show the selection's stand-in (also an older one): it is not a scene object, so deselect it
        var sel = _view.Selected;
        _collProxy = null;
        if (sel != null && _scene != null && !_scene.Objects.Contains(sel)) _view.Select(null);
        _view.InvalidateCollisionSelection();
    }

    /// <summary>World-space bounds of the selection.</summary>
    (Vector3 Min, Vector3 Max)? SelectionBounds()
    {
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue); bool any = false;
        foreach (var (asset, tris) in _collSel)
        {
            if (tris.Count == 0 || _soups.GetValueOrDefault(asset) is not { } s || !_collAt.TryGetValue(asset, out var at)) continue;
            foreach (int t in tris) for (int k = 0; k < 3; k++) { var p = Vector3.Transform(s.P[s.T[3 * t + k]], at.ToWorld); mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); any = true; }
        }
        return any ? (mn, mx) : null;
    }

    /// <summary>Selects a stand-in object at the selection's centre (G / R / T, the gizmo and Properties move it).</summary>
    void CollisionSelectionChanged()
    {
        foreach (var k in _collSel.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList()) _collSel.Remove(k);
        _view.InvalidateCollisionSelection();
        if (SelectionBounds() is not { } b) { ClearCollisionSelection(); UpdateCollisionStatus(); return; }
        var c = (b.Min + b.Max) / 2;
        int n = _collSel.Values.Sum(x => x.Count);
        // one stand-in for the whole session of a selection, updated in place: after an edit or an undo / redo its box
        // follows the triangles (1.12 made a new one each time, and the transform that had just ended then reported the old
        // one as selected, which left an orange box behind at the place of the edit)
        _collProxy ??= new SceneObject { Kind = SceneObjectKind.Scenery };
        _collProxy.Name = $"Collision: {n:N0} triangle(s)"; _collProxy.ModelName = string.Join(", ", _collSel.Keys.Select(Short));
        _collProxy.Transform = _collProxy.OriginalTransform = Matrix4x4.CreateTranslation(c);
        _collProxy.BoundsMin = b.Min - c; _collProxy.BoundsMax = b.Max - c;
        _collProxyBase = _collProxy.Transform;
        _view.Select(_collProxy);
        _view.Refresh3D();
        UpdateCollisionStatus();
    }

    /// <summary>Properties text for the collision stand-in.</summary>
    string? CollisionInfo(SceneObject o)
    {
        if (o != _collProxy) return null;
        var sb = new System.Text.StringBuilder();
        foreach (var (asset, tris) in _collSel)
        {
            var s = _soups[asset]!;
            var users = CollisionUsers(asset);
            var shapes = tris.Select(t => s.Shape[t]).GroupBy(x => x).Select(g => $"{g.Key} ×{g.Count()}");
            var mats = tris.Select(t => s.Mat[t]).Distinct().OrderBy(x => x);
            var (mn, mx) = s.Bounds(tris);
            sb.AppendLine($"{asset}: {tris.Count:N0} of {s.Triangles:N0} triangles; shapes {string.Join(", ", shapes.Take(4))}{(shapes.Count() > 4 ? " …" : "")}; material index {string.Join(",", mats)}");
            sb.AppendLine($"  bounds (asset space) {Fmt(mn)} .. {Fmt(mx)}; used by {users.Count} object(s){(users.Count > 1 ? " — an edit changes all of them" : users.Count == 1 ? $" ({users[0].Name})" : "")}");
        }
        sb.Append("G / R / T or the fields above move it; Del deletes; World > Save writes the collision.");
        return sb.ToString();
    }

    void UpdateCollisionStatus()
    {
        if (!_view.CollisionMode && _collSel.Count == 0) return;
        int sel = _collSel.Values.Sum(x => x.Count);
        var edited = _soups.Values.Where(s => s != null && s.Dirty).Select(s => s!).ToList();
        string ed = edited.Count == 0 ? "" : $"; edited (unsaved): {string.Join(", ", edited.Select(s => $"{Short(s.Asset)} {s.OriginalTriangles:N0}→{s.Triangles:N0} tris (~{Est(s) / 1024:N0} KB)"))}";
        _status.Text = $"Edit Collision: {sel:N0} triangle(s) selected{(sel > 0 ? " in " + string.Join(", ", _collSel.Keys.Select(Short)) : "")}; {_view.CollisionSummary}{ed}";
    }

    /// <summary>Approximate size of a collision asset with these triangles: index + vertex + material buffers + MOPP (~27 B/triangle).</summary>
    static long Est(CollisionSoup s) => s.Triangles * (6L + 1 + 27) + s.P.Count * 12L + 4096;

    // ------------------------------------------------------------------ edits (each one undo step)

    /// <summary>Runs an edit on the selected assets with undo snapshots; <paramref name="what"/> names the step.</summary>
    void CollisionEdit(string what, IEnumerable<string> assets, Action<string, CollisionSoup> edit, bool keepSelection = true)
    {
        var list = assets.Distinct().Where(a => _soups.GetValueOrDefault(a) != null).ToList();
        if (list.Count == 0) return;
        var before = list.ToDictionary(a => a, a => _soups[a]!.Save());
        foreach (var a in list) edit(a, _soups[a]!);
        var after = list.ToDictionary(a => a, a => _soups[a]!.Save());
        void Apply(Dictionary<string, CollisionSoup.Snapshot> snaps)
        {
            foreach (var (a, snap) in snaps) if (_soups.GetValueOrDefault(a) is { } s) { s.Restore(snap); CollisionChanged(a); }
            foreach (var (a, set) in _collSel.ToList()) if (_soups.GetValueOrDefault(a) is { } s) set.RemoveWhere(t => t >= s.Triangles);
            CollisionSelectionChanged();
        }
        _history.PushCallback("Collision: " + what, () => Apply(before), () => Apply(after));
        foreach (var a in list) CollisionChanged(a);
        if (keepSelection) CollisionSelectionChanged(); else ClearCollisionSelection();
        Log($"Collision: {what} (World > Save writes it; Ctrl+Z undoes it).");
        UpdateCollisionStatus();
    }

    /// <summary>The stand-in was moved / turned / scaled (G / R / T, gizmo, Properties): the same change for the triangles.</summary>
    void ApplyCollisionTransform(Matrix4x4 before, Matrix4x4 after)
    {
        if (!Matrix4x4.Invert(before, out var inv)) return;
        var delta = inv * after;
        int n = _collSel.Values.Sum(x => x.Count);
        string what = before.Translation != after.Translation && new Vector3(after.M11, after.M22, after.M33) == new Vector3(before.M11, before.M22, before.M33) ? "move" : "transform";
        CollisionEdit($"{what} {n:N0} triangle(s) by {Fmt(after.Translation - before.Translation)}", _collSel.Keys.ToList(), (a, s) =>
        {
            var toWorld = _collAt[a].ToWorld;
            Matrix4x4.Invert(toWorld, out var w2a);
            s.Transform(_collSel[a], toWorld * delta * w2a);
        });
    }

    void DeleteCollisionSelection()
    {
        int n = _collSel.Values.Sum(x => x.Count);
        if (n == 0) { Log("Delete collision: select collision first (Edit Collision, click a piece)."); return; }
        var sel = _collSel.ToDictionary(kv => kv.Key, kv => kv.Value.ToHashSet());
        CollisionEdit($"delete {n:N0} triangle(s)", sel.Keys, (a, s) => s.Delete(sel[a]), keepSelection: false);
    }

    void DuplicateCollisionSelection()
    {
        int n = _collSel.Values.Sum(x => x.Count);
        if (n == 0) return;
        var newSel = new Dictionary<string, HashSet<int>>();
        CollisionEdit($"duplicate {n:N0} triangle(s)", _collSel.Keys.ToList(), (a, s) =>
        {
            var toWorld = _collAt[a].ToWorld; Matrix4x4.Invert(toWorld, out var w2a);
            newSel[a] = s.Duplicate(_collSel[a], toWorld * Matrix4x4.CreateTranslation(2, 0, 0) * w2a);
        });
        _collSel.Clear(); foreach (var (a, t) in newSel) _collSel[a] = t;
        CollisionSelectionChanged();
    }

    void CopyCollisionSelection(bool cut)
    {
        var p = new List<Vector3>(); var t = new List<int>();
        foreach (var (a, tris) in _collSel)
        {
            var s = _soups[a]!; var toWorld = _collAt[a].ToWorld;
            foreach (int tri in tris) for (int k = 0; k < 3; k++) { t.Add(p.Count); p.Add(Vector3.Transform(s.P[s.T[3 * tri + k]], toWorld)); }
        }
        if (t.Count == 0) { Log("Copy collision: select collision first."); return; }
        _collClip = (p, t);
        Log($"Copied {t.Count / 3:N0} collision triangle(s). Ctrl+V pastes them where the mouse points (into the world collision).");
        if (cut) DeleteCollisionSelection();
    }

    /// <summary>The world (terrain) collision: new shapes and pasted triangles go there (world space).</summary>
    (string Asset, Matrix4x4 ToWorld)? WorldCollision()
    {
        foreach (var (o, asset, toWorld) in _view.CollisionInstances())
            if (o.Kind == SceneObjectKind.Terrain && Soup(asset) != null) return (asset, toWorld);
        return null;
    }

    /// <summary>Adds triangles given in world space (bottom centre placed on <paramref name="at"/>) to the world collision and selects them.</summary>
    void AddWorldCollision(string what, List<Vector3> worldP, List<int> tris, Vector3 at)
    {
        if (WorldCollision() is not { } wc) { Log("Add collision: the world collision isn't loaded yet (switch the Collision view on and wait for it)."); return; }
        var mn = worldP.Aggregate(Vector3.Min); var mx = worldP.Aggregate(Vector3.Max);
        var shift = at - new Vector3((mn.X + mx.X) / 2, mn.Y, (mn.Z + mx.Z) / 2);
        Matrix4x4.Invert(wc.ToWorld, out var w2a);
        var local = worldP.Select(p => Vector3.Transform(p + shift, w2a)).ToList();
        HashSet<int> added = new();
        CollisionEdit($"add {what} at {Fmt(at)}", new[] { wc.Asset }, (a, s) => added = s.Add(local, tris, "added " + what));
        _collSel.Clear(); _collAt.Clear();
        _collSel[wc.Asset] = added;
        _collAt[wc.Asset] = (_scene!.Objects.First(o => o.Kind == SceneObjectKind.Terrain), wc.ToWorld);
        CollisionSelectionChanged();
    }

    void PasteCollision(Point? at)
    {
        if (_collClip is not { } clip) { Log("Paste collision: copy collision first (Ctrl+C in Edit Collision)."); return; }
        if (_view.CollisionSurfaceAt(at) is not { } hit) return;
        AddWorldCollision($"{clip.T.Count / 3:N0} pasted triangle(s)", clip.P.ToList(), clip.T.ToList(), hit.Point);
    }

    /// <summary>Right-click menu: a box, ramp or plane where the mouse points (world collision, facing away from the camera).</summary>
    void AddCollisionShape(string kind, Point? at)
    {
        if (_view.CollisionSurfaceAt(at) is not { } hit) return;
        var (p, t) = kind switch
        {
            "ramp" => CollisionSoup.RampMesh(new Vector3(3, 1.5f, 4)),
            "plane" => CollisionSoup.PlaneMesh(new Vector3(5, 0, 5)),
            _ => CollisionSoup.BoxMesh(new Vector3(2, 2, 2)),
        };
        var turn = Matrix4x4.CreateRotationY(_view.CameraYaw);
        AddWorldCollision(kind, p.Select(v => Vector3.Transform(v, turn)).ToList(), t, hit.Point);
    }

    /// <summary>The collision asset of a scenery object's model (its own aid_havok, all instances), when editable.</summary>
    string? ModelCollisionAsset(SceneObject o) =>
        _view.CollisionInstances().Where(x => x.Obj == o && x.Obj.Kind == SceneObjectKind.Scenery).Select(x => x.Asset).FirstOrDefault(a => Soup(a) != null);

    void CollisionFromRenderMesh(SceneObject o)
    {
        if (o.Model == null || ModelCollisionAsset(o) is not { } asset) { Log($"{o.Name}: its model has no editable collision asset in this world."); return; }
        var p = new List<Vector3>(); var t = new List<int>();
        void AddModel(NB.Core.Models.ModelAsset m, Matrix4x4 local)
        {
            foreach (var d in m.Draws)
            {
                if (m.LodOnlyNodes.Contains(d.Node)) continue;
                int b = p.Count;
                p.AddRange(d.Positions.Select(v => Vector3.Transform(v, local)));
                for (int i = 0; i + 2 < d.Indices.Length; i += 3)
                    if (d.Indices[i] < d.Positions.Length && d.Indices[i + 1] < d.Positions.Length && d.Indices[i + 2] < d.Positions.Length)
                    { t.Add(b + d.Indices[i]); t.Add(b + d.Indices[i + 1]); t.Add(b + d.Indices[i + 2]); }
            }
        }
        AddModel(o.Model, Matrix4x4.Identity);
        foreach (var (cm, cl) in o.Children) AddModel(cm, cl);
        var (wp, wt) = HkCollisionImport.Merge(new[] { new NB.Core.Models.ImportMesh { Positions = p, Triangles = t } });
        if (wt.Count == 0) { Log($"{o.Name}: the render mesh has no triangles."); return; }
        int users = CollisionUsers(asset).Count;
        CollisionEdit($"collision of {Short(asset)} from its render mesh ({wt.Count / 3:N0} triangles, {users} object(s))", new[] { asset }, (a, s) => s.ReplaceAll(wp, wt, "render mesh"), keepSelection: false);
    }

    void RemoveModelCollision(SceneObject o)
    {
        if (ModelCollisionAsset(o) is not { } asset) { Log($"{o.Name}: its model has no editable collision asset in this world."); return; }
        int users = CollisionUsers(asset).Count;
        if (!_scripted && users > 1 && MessageBox.Show(this, $"{Short(asset)} is the collision of {users} objects (every instance of this model). Remove it from all of them?", "Remove Collision", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        CollisionEdit($"remove the collision of {Short(asset)} ({users} object(s))", new[] { asset }, (a, s) => s.Delete(Enumerable.Range(0, s.Triangles).ToHashSet()), keepSelection: false);
    }

    /// <summary>Writes every edited collision asset into the world bundle's CAFF (World > Save). Returns lines for the log.</summary>
    List<string> WriteCollisionEdits()
    {
        var res = new List<string>();
        if (_scene == null) return res;
        // every edited asset is checked before any is rebuilt: a refused save leaves the bundle in memory untouched
        foreach (var (asset, s) in _soups)
        {
            if (s == null || !s.Dirty) continue;
            int sym = _scene.Caff.Symbols.FindIndex(x => AssetIds.DisplayName(x) == asset) + 1;
            var p = NB.Core.Havok.HkCollisionImport.CheckRelocations(_scene.Caff, new AssetView(_scene.Caff, sym).PartId(".data"));
            if (p.Count > 0) throw new InvalidDataException($"{asset} is damaged ({string.Join("; ", p)}); nothing was saved. Run NB.Cli collision-repair first (see the log when the world opens).");
        }
        foreach (var (asset, s) in _soups)
        {
            if (s == null || !s.Dirty) continue;
            int sym = _scene.Caff.Symbols.FindIndex(x => AssetIds.DisplayName(x) == asset) + 1;
            res.Add("  collision " + s.WriteTo(_scene.Caff, sym));
            s.OriginalTriangles = s.Triangles;
        }
        return res;
    }

    void BuildCollisionMenu(SceneViewport.CollisionHit? hit, Point p)
    {
        _objMenu.Items.Clear();
        int n = _collSel.Values.Sum(x => x.Count);
        _objMenu.Items.Add(new ToolStripLabel(hit is { } h0 ? $"Collision of {h0.Obj.Name} ({Short(h0.Asset)})" : "Collision") { Font = new Font(Font, FontStyle.Bold) });
        if (hit is { } h && (n == 0 || !_collSel.ContainsKey(h.Asset) || !_collSel[h.Asset].Contains(h.Tri)))
            _objMenu.Items.Add("Select This Piece", null, (_, _) => OnCollisionPicked(h, false, false));
        _objMenu.Items.Add(new ToolStripMenuItem($"Delete Selected Collision ({n:N0} triangles)", null, (_, _) => DeleteCollisionSelection()) { ShortcutKeyDisplayString = "Del", Enabled = n > 0 });
        _objMenu.Items.Add(new ToolStripMenuItem("Duplicate", null, (_, _) => DuplicateCollisionSelection()) { ShortcutKeyDisplayString = "Ctrl+D", Enabled = n > 0 });
        _objMenu.Items.Add(new ToolStripMenuItem("Copy", null, (_, _) => CopyCollisionSelection(false)) { ShortcutKeyDisplayString = "Ctrl+C", Enabled = n > 0 });
        _objMenu.Items.Add(new ToolStripMenuItem("Cut", null, (_, _) => CopyCollisionSelection(true)) { ShortcutKeyDisplayString = "Ctrl+X", Enabled = n > 0 });
        _objMenu.Items.Add(new ToolStripMenuItem("Paste Here", null, (_, _) => PasteCollision(p)) { ShortcutKeyDisplayString = "Ctrl+V", Enabled = _collClip != null });
        _objMenu.Items.Add(new ToolStripSeparator());
        _objMenu.Items.Add("Add Collision Box Here", null, (_, _) => AddCollisionShape("box", p));
        _objMenu.Items.Add("Add Collision Ramp Here", null, (_, _) => AddCollisionShape("ramp", p));
        _objMenu.Items.Add("Add Collision Plane Here", null, (_, _) => AddCollisionShape("plane", p));
        if (hit is { } h2 && h2.Obj.Kind == SceneObjectKind.Scenery)
        {
            _objMenu.Items.Add(new ToolStripSeparator());
            _objMenu.Items.Add($"Collision of {h2.Obj.Name} from Its Render Mesh", null, (_, _) => CollisionFromRenderMesh(h2.Obj));
            _objMenu.Items.Add($"Remove Collision of {h2.Obj.Name}'s Model", null, (_, _) => RemoveModelCollision(h2.Obj));
        }
        _objMenu.Items.Add(new ToolStripSeparator());
        _objMenu.Items.Add("Clear Selection", null, (_, _) => { ClearCollisionSelection(); UpdateCollisionStatus(); });
    }

    /// <summary>Del, Ctrl+C / X / V / D in Edit Collision (the 3D view's keys); false: not handled.</summary>
    bool HandleCollisionKey(Keys k)
    {
        if (!_view.CollisionMode) return false;
        switch (k)
        {
            case Keys.Delete: if (_view.Transforming) return false; DeleteCollisionSelection(); return true;
            case Keys.Control | Keys.C: CopyCollisionSelection(false); return true;
            case Keys.Control | Keys.X: CopyCollisionSelection(true); return true;
            case Keys.Control | Keys.V: PasteCollision(null); return true;
            case Keys.Control | Keys.D: DuplicateCollisionSelection(); return true;
        }
        return false;
    }

    /// <summary>Script steps for collision editing (tests).</summary>
    async Task<bool> CollisionScript(string cmd, Func<string> next, Action<string> L)
    {
        switch (cmd)
        {
            case "--coll-mode":
            {
                _view.CollisionMode = next() == "on";
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (_view.CollisionMode && !_view.CollisionReady && sw.ElapsedMilliseconds < 120000) { Application.DoEvents(); await Task.Delay(100); }
                L($"script: edit collision {(_view.CollisionMode ? "on" : "off")}: {_view.CollisionSummary}"); return true;
            }
            case "--coll-pick-size": _pickSize = Enum.Parse<PickSize>(next(), true); if (_pickSizeBox != null) _pickSizeBox.SelectedIndex = (int)_pickSize; L($"script: pick size {_pickSize}"); return true;
            case "--coll-pick":
            {
                var v = next().Split(','); var pt = new Point(int.Parse(v[0]), int.Parse(v[1]));
                var hit = _view.PickCollision(pt);
                OnCollisionPicked(hit, v.Length > 2 && v[2] == "shift", v.Length > 2 && v[2] == "alt");
                L($"script: collision pick {pt.X},{pt.Y}: {(hit is { } h ? $"{h.Obj.Name} {Short(h.Asset)} tri {h.Tri} at {Fmt(h.Point)}" : "nothing")}; selected {_collSel.Values.Sum(x => x.Count)} tris"); return true;
            }
            case "--coll-rect":
            {
                var v = next().Split(',').Select(int.Parse).ToArray();
                var r = Rectangle.FromLTRB(v[0], v[1], v[2], v[3]);
                var mi = typeof(SceneViewport).GetMethod("CollisionInRect", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                OnCollisionRect((List<(SceneObject, string, Matrix4x4, List<int>)>)mi.Invoke(_view, new object[] { r })!, false);
                L($"script: collision rectangle: {_collSel.Values.Sum(x => x.Count)} tris selected in {string.Join(", ", _collSel.Keys.Select(Short))}"); return true;
            }
            case "--coll-select-box":
            {
                // --coll-select-box x0,y0,z0,x1,y1,z1: every editable collision triangle with all corners inside the world box
                var v = next().Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                var mn = new Vector3(v[0], v[1], v[2]); var mx = new Vector3(v[3], v[4], v[5]);
                _collSel.Clear(); _collAt.Clear();
                foreach (var (o, asset, toWorld) in _view.CollisionInstances())
                {
                    if (Soup(asset) is not { } s) continue;
                    var set = new HashSet<int>();
                    for (int t = 0; t < s.Triangles; t++)
                    {
                        bool all = true;
                        for (int k = 0; k < 3 && all; k++) { var p = Vector3.Transform(s.P[s.T[3 * t + k]], toWorld); all = p.X >= mn.X && p.Y >= mn.Y && p.Z >= mn.Z && p.X <= mx.X && p.Y <= mx.Y && p.Z <= mx.Z; }
                        if (all) set.Add(t);
                    }
                    if (set.Count > 0) { if (_collSel.TryGetValue(asset, out var old)) old.UnionWith(set); else { _collSel[asset] = set; _collAt[asset] = (o, toWorld); } }
                }
                CollisionSelectionChanged();
                L($"script: collision box select: {_collSel.Values.Sum(x => x.Count)} tris in {string.Join(", ", _collSel.Keys.Select(Short))}"); return true;
            }
            case "--coll-delete": DeleteCollisionSelection(); L("script: collision deleted"); return true;
            case "--coll-dup": DuplicateCollisionSelection(); L("script: collision duplicated"); return true;
            case "--coll-add":
            {
                var kind = next(); var v = next().Split(',');
                if (v.Length == 3)
                {
                    // world position x,y,z
                    var at = new Vector3(float.Parse(v[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(v[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(v[2], System.Globalization.CultureInfo.InvariantCulture));
                    var (p, t) = kind switch { "ramp" => CollisionSoup.RampMesh(new Vector3(3, 1.5f, 4)), "plane" => CollisionSoup.PlaneMesh(new Vector3(5, 0, 5)), _ => CollisionSoup.BoxMesh(new Vector3(2, 2, 2)) };
                    AddWorldCollision(kind, p, t, at);
                }
                else AddCollisionShape(kind, new Point(int.Parse(v[0]), int.Parse(v[1])));
                L($"script: collision {kind} added; selected {_collSel.Values.Sum(x => x.Count)} tris"); return true;
            }
            case "--coll-scale-sel":
            {
                // --coll-scale-sel sx,sy,sz: scale the selection about its centre (through the stand-in, like S)
                var v = next().Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                if (_collProxy == null) { L("script: no collision selection"); return true; }
                var before = _collProxy.Transform; var t0 = before.Translation;
                var m = Matrix4x4.CreateScale(v[0], v[1], v[2]) * before; m.Translation = t0; _collProxy.Transform = m;
                PushUndo(_collProxy, before, m); L($"script: collision scaled by {v[0]},{v[1]},{v[2]}"); return true;
            }
            case "--coll-move":
            {
                var v = next().Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                if (_collProxy == null) { L("script: no collision selection"); return true; }
                var before = _collProxy.Transform; var m = before; m.Translation += new Vector3(v[0], v[1], v[2]); _collProxy.Transform = m;
                PushUndo(_collProxy, before, m); L($"script: collision moved by {v[0]},{v[1]},{v[2]}"); return true;
            }
            case "--coll-from-render": { var q = next(); var o = _scene!.Objects.First(x => x.Name.Contains(q, StringComparison.OrdinalIgnoreCase)); CollisionFromRenderMesh(o); L($"script: collision of {o.Name} from render mesh"); return true; }
            case "--coll-remove": { var q = next(); var o = _scene!.Objects.First(x => x.Name.Contains(q, StringComparison.OrdinalIgnoreCase)); RemoveModelCollision(o); L($"script: collision of {o.Name} removed"); return true; }
            case "--coll-info":
                L("script: collision: " + (_collProxy != null ? CollisionInfo(_collProxy)!.Replace(Environment.NewLine, " | ") : "no selection") + " || " + _status.Text); return true;
            case "--coll-menu-shot":
            {
                var v = next().Split(','); var png = next();
                var pt = new Point(int.Parse(v[0]), int.Parse(v[1]));
                BuildCollisionMenu(_view.PickCollision(pt), pt); _objMenu.Show(_view, pt); Application.DoEvents(); await Task.Delay(300); Application.DoEvents();
                using (var bmp = new Bitmap(_objMenu.Width, _objMenu.Height)) { _objMenu.DrawToBitmap(bmp, new Rectangle(0, 0, _objMenu.Width, _objMenu.Height)); bmp.Save(png); }
                _objMenu.Close(); L($"script: collision menu captured {png}"); return true;
            }
        }
        return false;
    }
}
