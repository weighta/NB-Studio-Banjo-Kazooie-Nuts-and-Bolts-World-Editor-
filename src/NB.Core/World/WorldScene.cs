using System.Numerics;
using NB.Core.Formats;
using NB.Core.IO;
using NB.Core.Models;
using NB.Core.Project;
using NB.Core.Textures;

namespace NB.Core.World;

public enum SceneObjectKind { Terrain, Scenery, Marker }

/// <summary>An editable object in a loaded world.</summary>
public sealed class SceneObject
{
    public int Id;
    public SceneObjectKind Kind;
    public string Name = "";
    public string ModelName = "";          // reference model asset (scenery) or background model (terrain)
    public ModelAsset? Model;              // shared geometry
    public Matrix4x4 Transform = Matrix4x4.Identity;
    public Matrix4x4 OriginalTransform = Matrix4x4.Identity;
    public SceneInstance? Instance;        // backing record for scenery
    public MarkerAsset? MarkerSet;         // backing asset for markers
    public MarkerRecord? Marker;
    public bool Visible = true;
    public Vector3 BoundsMin, BoundsMax;   // model space (including nested reference models)
    /// <summary>Models placed inside this object's reference model (composite buildings), flattened, with transforms
    /// relative to this object. Drawn and picked together with it; they move with it.</summary>
    public List<(ModelAsset Model, Matrix4x4 Local)> Children = new();
    /// <summary>Where <see cref="Model"/> comes from when it is not a reference model of the world bundle: for markers, the
    /// objparams that names it (props such as L.O.G.'s palace, characters, collectables); for scenery, another bundle of
    /// the world's load set. Empty for ordinary scenery.</summary>
    public string ModelSource = "";
    /// <summary>Bundle the drawn model was read from (the world bundle for ordinary scenery).</summary>
    public uint ModelBundle;
    /// <summary>View depth / scale beyond which the game draws nothing of this object (its model and nested models all
    /// end their LOD tables with a cull level); infinity when it is always drawn. See <see cref="ModelAsset.CullDistance"/>.</summary>
    public float CullDistance = float.PositiveInfinity;
    /// <summary>World doors (portal_worlddoor markers): whether the door shows Grunty's challenge sign, and why (see
    /// WorldScene.GruntyActs); null for every other object.</summary>
    public bool? GruntySign;
    public string GruntySignWhy = "";
    public bool Dirty => Transform != OriginalTransform || (Marker is { Type: 22 } m && m.Link != m.SavedLink);
}

/// <summary>
/// A world bundle opened for editing: the background (level) model's own draws as terrain, and every
/// scenery instance (chunk 12) with its reference model. Transform edits are written back into the
/// background model's .data (chunk-12 matrix + position, and the matching chunk-2 node), in place.
/// </summary>
public sealed class WorldScene
{
    public readonly Workspace Workspace;
    public readonly uint Bundle;
    public readonly CaffFile Caff;
    public readonly ModelAsset Background;
    public readonly List<SceneObject> Objects = new();
    public readonly Dictionary<string, ModelAsset> Models = new();
    public readonly List<string> Log = new();
    readonly Dictionary<int, List<(int, int)>> _relocs;

    /// <summary>Act bundles whose markers were loaded in addition to the world bundle's own.</summary>
    public readonly List<uint> MarkerBundles = new();

    /// <summary>What was loaded, skipped or failed while building the scene (Studio log, <c>NB.Cli world-audit</c>).</summary>
    public readonly WorldAudit Audit = new();

    /// <summary>Bundles searched for assets the world refers to, in the order the game has them loaded: the world bundle,
    /// the act bundle and its stream dependencies, the world's own dependencies and (without an act) the bundles every
    /// act of the world loads (Terrarium's cracked panel door is in ea08c0, Nutty Acres' shipping signs in fa4b2d), then
    /// the common bundle.</summary>
    public readonly List<uint> LoadSet = new();

    readonly AssetIndex? _index;
    readonly Dictionary<CaffFile, Dictionary<int, List<(int, int)>>> _relocByCaff = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<CaffFile, Dictionary<uint, int>> _idsByCaff = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<uint, CaffFile?> _caffs = new();

    /// <param name="index">Asset index of the workspace: finds the world's acts (for the load set) and, as a last resort,
    /// any resident bundle holding a referenced asset. When null, the workspace's cached index is used if it exists.</param>
    public WorldScene(Workspace ws, uint bundle, string backgroundModel, IProgress<(string, double)>? progress = null, IEnumerable<uint>? actBundles = null, AssetIndex? index = null)
    {
        Workspace = ws; Bundle = bundle;
        progress?.Report(("loading bundle", 0));
        Caff = ws.LoadResident(bundle);
        _caffs[bundle & 0xFFFFFF] = Caff;
        _relocs = AssetView.BuildRelocIndex(Caff);
        _relocByCaff[Caff] = _relocs;
        _index = index;
        if (_index == null && File.Exists(AssetIndex.PathFor(ws)))
            try { _index = AssetIndex.LoadOrBuild(ws); } catch (Exception e) { Log.Add("asset index: " + e.Message); }
        int bgSym = Caff.Symbols.IndexOf(backgroundModel) + 1;
        if (bgSym == 0) throw new InvalidDataException($"{backgroundModel} not in bundle {bundle:x6}");
        progress?.Report(("parsing terrain", 0.05));
        Background = ModelAsset.Parse(Caff, bgSym, _relocs);
        foreach (var w in Background.Warnings.Take(20)) Log.Add("terrain: " + w);

        var acts = (actBundles ?? Enumerable.Empty<uint>()).Select(a => a & 0xFFFFFF).ToList();
        BuildLoadSet(bundle & 0xFFFFFF, acts);

        int id = 0;
        var terrain = new SceneObject { Id = id++, Kind = SceneObjectKind.Terrain, Name = "Terrain (" + backgroundModel + ")", ModelName = backgroundModel, Model = Background, ModelBundle = bundle & 0xFFFFFF };
        ComputeBounds(terrain);
        Objects.Add(terrain);

        int n = 0;
        foreach (var inst in Background.Instances)
        {
            if (n++ % 25 == 0) progress?.Report(($"scenery {n}/{Background.Instances.Count}", 0.1 + 0.8 * n / Math.Max(1, Background.Instances.Count)));
            var obj = new SceneObject
            {
                Id = id++, Kind = SceneObjectKind.Scenery, Name = ModelAsset.CleanInstanceName(inst.Name), Instance = inst,
                Transform = inst.World, OriginalTransform = inst.World,
            };
            uint refId = inst.RefModel >= 0 && inst.RefModel < Background.ReferenceIds.Count ? (uint)Background.ReferenceIds[inst.RefModel] : 0;
            Audit.Scenery++;
            var loc = FindAsset(refId, Caff);
            if (loc != null && GetModel(loc.Value.Caff, loc.Value.Sym) is { } model)
            {
                obj.ModelName = loc.Value.Caff.Symbols[loc.Value.Sym - 1];
                obj.Model = model;
                obj.ModelBundle = loc.Value.Bundle;
                if (loc.Value.Bundle != (bundle & 0xFFFFFF))
                {
                    obj.ModelSource = $"bundle {loc.Value.Bundle:x6}";
                    Audit.SceneryOtherBundle++;
                    Audit.Note("scenery models from another bundle", $"{AssetIds.DisplayName(obj.ModelName)} ({loc.Value.Bundle:x6}{(LoadSet.Contains(loc.Value.Bundle) ? "" : ", outside the load set")})");
                }
                obj.Children = NestedModels(obj.Model, loc.Value.Caff);
                if (obj.Model.Draws.Count == 0 && obj.Children.Count == 0) Audit.Note("scenery models without geometry", AssetIds.DisplayName(obj.ModelName));
            }
            else
            {
                Audit.SceneryMissing++;
                Audit.Note(loc != null ? "scenery models that failed to parse" : "scenery models not found", $"{obj.Name} ({refId:X8})");
                Log.Add($"{obj.Name}: reference model id {refId:X8} {(loc != null ? "failed to parse" : "not found in the workspace")} (drawn as a marker)");
            }
            ComputeBounds(obj);
            Objects.Add(obj);
        }
        // markers (actors, props, pickups, spawn points, paths...) from every marker asset in this bundle and the act bundles
        var markerSources = new List<(uint Bundle, CaffFile Caff)> { (bundle, Caff) };
        foreach (var ab in acts)
            if (LoadBundle(ab) is { } ac) { markerSources.Add((ab, ac)); MarkerBundles.Add(ab); }
            else Log.Add($"act bundle {ab:x6}: could not be loaded");
        foreach (var (_, mc) in markerSources)
            for (int s = 1; s <= mc.Symbols.Count; s++)
                if (AssetIds.IdOf(mc.Symbols[s - 1]) is uint aid) NameById.TryAdd(aid, AssetIds.DisplayName(mc.Symbols[s - 1]));
        progress?.Report(("markers", 0.9));
        foreach (var (mb, mc) in markerSources)
        for (int s = 1; s <= mc.Symbols.Count; s++)
        {
            if (!mc.Symbols[s - 1].StartsWith("aid_marker_")) continue;
            try
            {
                var ma = MarkerAsset.Parse(mc, s);
                ma.Bundle = mb; ma.Caff = mc;
                Markers.Add(ma);
                foreach (var r in ma.Records) r.AssetNames = r.AssetIds.Select(x => NameById.GetValueOrDefault(x, "?")).ToList();
                foreach (var r in ma.Records)
                {
                    var m = r.Matrix;
                    var obj = new SceneObject
                    {
                        Id = id++, Kind = SceneObjectKind.Marker, MarkerSet = ma, Marker = r, Transform = m, OriginalTransform = m,
                        Name = $"{MarkerRecord.TypeName(r.Type)} #{r.Index}" + (r.AssetNames.FirstOrDefault(n => n.StartsWith("aid_objparams_")) is string op ? " " + op.Replace("aid_objparams_banjox_", "") : r.Strings.Count > 0 ? " " + r.Strings[0] : ""),
                        ModelName = AssetIds.DisplayName(ma.Name),
                        BoundsMin = new Vector3(-1), BoundsMax = new Vector3(1),
                    };
                    Audit.Markers++;
                    AttachMarkerModel(obj, mc);
                    ComputeBounds(obj);
                    Objects.Add(obj);
                }
            }
            catch (Exception e) { Log.Add($"{mc.Symbols[s - 1]}: {e.Message}"); Audit.Note("marker assets that failed to parse", $"{AssetIds.DisplayName(mc.Symbols[s - 1])}: {e.Message}"); }
        }
        // grass layers (chunk 17 of the background model, or of a reference model placed in the world such as Spiral
        // Mountain's grassboxes): the game covers each box with grass tiles at run time (see GrassLayer)
        try { LoadGrass(); }
        catch (Exception e) { Log.Add("grass layers: " + e.Message); }
        Audit.Models = Models.Count;
        Audit.Bundles = _caffs.Where(kv => kv.Value != null).Select(kv => kv.Key).ToList();
        foreach (var (cat, items) in Audit.Notes.OrderBy(kv => kv.Key))
            Log.Add($"{cat}: {items.Count} — " + string.Join(", ", items.Take(6)) + (items.Count > 6 ? ", …" : ""));
        progress?.Report(("done", 1));
    }

    // ------------------------------------------------------------------ load set and cross-bundle lookups

    void BuildLoadSet(uint world, List<uint> acts)
    {
        void Add(uint b) { b &= 0xFFFFFF; if (b != 0 && !LoadSet.Contains(b) && File.Exists(Workspace.Game.ResidentPath(b))) LoadSet.Add(b); }
        List<uint> Deps(uint b)
        {
            try { return File.Exists(Workspace.Game.StreamPath(b)) ? ActCatalog.Dependencies(Workspace.Game.StreamPath(b)).Select(d => d & 0xFFFFFF).ToList() : new(); }
            catch (Exception) { return new(); }
        }
        Add(world);
        foreach (var a in acts) { Add(a); foreach (var d in Deps(a)) Add(d); }
        foreach (var d in Deps(world)) Add(d);
        if (acts.Count == 0 && _index != null)
        {
            // no act chosen: the bundles that every act of this world loads (companions such as ea08c0 / fa4b2d / ee8a91)
            try
            {
                var worldActs = ActCatalog.Build(Workspace, _index).Where(a => a.WorldBundle == world).ToList();
                if (worldActs.Count > 0)
                {
                    var shared = Deps(worldActs[0].ActBundle);
                    foreach (var a in worldActs.Skip(1)) { var d = Deps(a.ActBundle).ToHashSet(); shared = shared.Where(d.Contains).ToList(); }
                    foreach (var d in shared) Add(d);
                }
            }
            catch (Exception e) { Log.Add("acts of this world: " + e.Message); }
        }
        Add(CommonBundle);
    }

    const uint CommonBundle = 0x685374;

    CaffFile? LoadBundle(uint b)
    {
        b &= 0xFFFFFF;
        if (_caffs.TryGetValue(b, out var c)) return c;
        try { c = Workspace.LoadResident(b); }
        catch (Exception e) { Log.Add($"bundle {b:x6}: {e.Message}"); c = null; }
        return _caffs[b] = c;
    }

    Dictionary<uint, int> IdsOf(CaffFile c)
    {
        if (_idsByCaff.TryGetValue(c, out var d)) return d;
        d = new();
        for (int s = 1; s <= c.Symbols.Count; s++) if (AssetIds.IdOf(c.Symbols[s - 1]) is uint id) d.TryAdd(id, s);
        return _idsByCaff[c] = d;
    }

    uint BundleOf(CaffFile c) => _caffs.FirstOrDefault(kv => ReferenceEquals(kv.Value, c)).Key;

    /// <summary>Finds a resident asset by id: first in <paramref name="prefer"/> (the bundle that refers to it), then in
    /// the load set in order, then in any resident bundle of the workspace. With an asset index only the bundles that hold
    /// the id are opened (bundles already open are searched as well, in case the index predates an edit).</summary>
    (CaffFile Caff, int Sym, uint Bundle)? FindAsset(uint id, CaffFile? prefer = null)
    {
        if (id == 0) return null;
        if (prefer != null && IdsOf(prefer).TryGetValue(id, out int ps)) return (prefer, ps, BundleOf(prefer));
        if (_index == null)
        {
            foreach (var b in LoadSet)
                if (LoadBundle(b) is { } c && IdsOf(c).TryGetValue(id, out int s)) return (c, s, b);
            return null;
        }
        if (_byId == null)
        {
            _byId = new();
            foreach (var e in _index.Entries)
                if (!e.Streamed && e.Id != 0 && e.Symbol > 0) { if (!_byId.TryGetValue(e.Id, out var l)) _byId[e.Id] = l = new(); l.Add(e.Bundle & 0xFFFFFF); }
        }
        var holders = _byId.GetValueOrDefault(id);
        foreach (var b in LoadSet)
            if ((holders != null && holders.Contains(b)) || _caffs.ContainsKey(b))
                if (LoadBundle(b) is { } c && IdsOf(c).TryGetValue(id, out int s)) return (c, s, b);
        if (holders != null)
            foreach (var b in holders.OrderBy(b => _caffs.ContainsKey(b) ? 0 : 1))
                if (LoadBundle(b) is { } c && IdsOf(c).TryGetValue(id, out int s)) return (c, s, b);
        return null;
    }
    Dictionary<uint, List<uint>>? _byId;

    /// <summary>
    /// The parts of a vehicle blueprint (aid_vehicle_*) as (model, transform relative to the vehicle). Each block sits in
    /// a one-unit garage cell (part models are authored around their origin cell, multi-cell parts extend over the
    /// footprint cells of their avatarhavokdata), rotated by its Euler angles (X, then Y, then Z, like markers). The
    /// vehicle is centred on its cells horizontally with its lowest point at the marker (approximate: the game spawns the
    /// vehicle body there and lets it settle).
    /// </summary>
    List<(ModelAsset Model, Matrix4x4 Local)> BuildVehicle(CaffFile caff, int sym, out int missing)
    {
        missing = 0;
        var list = new List<(ModelAsset, Matrix4x4)>();
        var d = caff.PartsOf(sym).FirstOrDefault(p => caff.SectionOf(p).Name == ".data")?.Data;
        var v = d == null ? null : NB.Core.Tags.VehicleAsset.TryParse(d);
        if (v == null || v.Blocks.Count == 0) return list;
        foreach (var b in v.Blocks)
        {
            ModelAsset? model = null;
            if (FindAsset(b.Part, caff) is { } op && op.Caff.PartsOf(op.Sym).FirstOrDefault(p => op.Caff.SectionOf(p).Name == ".data")?.Data is { Length: > 0x128 } od)
            {
                uint mid = NB.Core.IO.BE.U32(od, 0x124);
                if (mid >> 24 == 0x04 && FindAsset(mid, op.Caff) is { } ml) model = GetModel(ml.Caff, ml.Sym);
            }
            if (model == null) { missing++; continue; }
            var rot = Matrix4x4.CreateRotationX(b.Rotation.X) * Matrix4x4.CreateRotationY(b.Rotation.Y) * Matrix4x4.CreateRotationZ(b.Rotation.Z);
            list.Add((model, rot * Matrix4x4.CreateTranslation(b.X, b.Y, b.Z)));
        }
        if (list.Count == 0) return list;
        // centre horizontally on the occupied cells, lowest drawn point at y = 0
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var (m, l) in list)
            foreach (var dr in m.Draws)
            {
                if (m.LodOnlyNodes.Contains(dr.Node)) continue;
                foreach (int i in dr.Indices) if (i < dr.Positions.Length) { var q = Vector3.Transform(dr.Positions[i], l); mn = Vector3.Min(mn, q); mx = Vector3.Max(mx, q); }
            }
        if (mn.X > mx.X) return list;
        var shift = Matrix4x4.CreateTranslation(-(mn.X + mx.X) / 2, -mn.Y, -(mn.Z + mx.Z) / 2);
        return list.Select(x => (x.Item1, x.Item2 * shift)).ToList();
    }

    /// <summary>
    /// Objects the game's code spawns at tag markers that name no objparams themselves, by marker type: the six Jinjo
    /// houses (type 42, objTag_BanjoX_ShowDownTown_JinjoHome1..6) and the Jinjo lock-ups (type 43, ..._Lockup1..6) in
    /// Showdown Town. The objparams come from the game asset reference table (aid_misc_banjox_gameassetref_default:
    /// 64-byte name + asset id): "jinjo_house" → props_showdowntown_jinjohouse, "jinjo_lockup" → props_showdowntown_lockup1.
    /// Verified in Xenia: a house stands at every JinjoHome marker and a lock-up at every Lockup marker of a new game.
    /// </summary>
    static string? CodeSpawned(MarkerRecord r) => r.Type switch
    {
        42 when r.Strings.Any(s => s.Contains("_JinjoHome", StringComparison.Ordinal)) => "jinjo_house",
        43 when r.Strings.Any(s => s.Contains("_Lockup", StringComparison.Ordinal)) => "jinjo_lockup",
        _ => null,
    };

    Dictionary<string, uint>? _gameAssetRefs;

    /// <summary>The id the game asset reference table (common bundle) gives a name, or 0.</summary>
    uint GameAssetRef(string name)
    {
        if (_gameAssetRefs == null)
        {
            _gameAssetRefs = new();
            try
            {
                if (FindAsset(AssetIds.Make(0x0B, "banjox_gameassetref_default")) is { } g
                    && g.Caff.PartsOf(g.Sym).FirstOrDefault(p => g.Caff.SectionOf(p).Name == ".data")?.Data is { } d)
                    for (int o = 0; o + 0x44 <= d.Length; o += 0x44)
                    {
                        // records of 0x44 bytes: name (64 bytes) + asset id
                        string n = NB.Core.IO.BE.CStr(d, o, 64);
                        if (n.Length > 0 && n.All(c => c is >= ' ' and < (char)127)) _gameAssetRefs.TryAdd(n, NB.Core.IO.BE.U32(d, o + 0x40));
                    }
            }
            catch (Exception e) { Log.Add("game asset references: " + e.Message); }
        }
        return _gameAssetRefs.GetValueOrDefault(name);
    }

    /// <summary>objparams of the world doors in Showdown Town (aid_objparams_banjox_portal_worlddoor).</summary>
    static readonly uint WorldDoorParams = AssetIds.Make(0x1F, "banjox_portal_worlddoor");

    Dictionary<uint, string>? _gruntyActs;

    /// <summary>
    /// The Acts whose world door shows Grunty's challenge sign, by act script id, with the reason: the Acts that hold a
    /// challenge Grunty gives (a challenge listed in the act lists aid_misc_banjox_acts_&lt;world&gt; of the common bundle
    /// whose giver tag at +0x30C is objTag_BanjoX_Actor_Grunty: Nutty Acres 3, CPU 3, Banjoland 4, World of Sport 4 and
    /// Whirlwind, Terrarium 4). Compared in Xenia on every door of a new game and of the all-unlocked save: exactly these
    /// doors show the sign. The game does not read the sign from this data (changing the giver, the act script's giver
    /// tags or the golf-cart unlock list moves no sign; tested), so Studio shows it as information only.
    /// </summary>
    Dictionary<uint, string> GruntyActs()
    {
        if (_gruntyActs != null) return _gruntyActs;
        _gruntyActs = new();
        try
        {
            if (LoadBundle(CommonBundle) is not { } c) return _gruntyActs;
            var ids = IdsOf(c);
            for (int s = 1; s <= c.Symbols.Count; s++)
            {
                if (!AssetIds.DisplayName(c.Symbols[s - 1]).StartsWith("aid_misc_banjox_acts_")) continue;
                var d = c.PartsOf(s).FirstOrDefault(p => c.SectionOf(p).Name == ".data")?.Data;
                if (d == null) continue;
                for (int o = 8; o + 8 <= d.Length; o += 4)
                {
                    uint ch = NB.Core.IO.BE.U32(d, o), script = NB.Core.IO.BE.U32(d, o + 4);
                    if (ch >> 24 != 0x3D || script >> 24 != 0x19 || !ids.TryGetValue(ch, out int cs)) continue;
                    var cd = c.PartsOf(cs).FirstOrDefault(p => c.SectionOf(p).Name == ".data")?.Data;
                    if (cd == null || cd.Length < 0x30C + 64 || NB.Core.IO.BE.CStr(cd, 0x30C, 64) != "objTag_BanjoX_Actor_Grunty") continue;
                    _gruntyActs.TryAdd(script, $"Grunty gives {AssetIds.DisplayName(c.Symbols[cs - 1]).Replace("aid_challenge_banjox_", "")} in this Act");
                }
            }
        }
        catch (Exception e) { Log.Add("Grunty challenge acts: " + e.Message); }
        return _gruntyActs;
    }

    readonly Dictionary<ModelAsset, ModelAsset> _noSign = new();

    /// <summary>The door model without its Grunty challenge sign (the draws textured worlddoor_gruntydoor).</summary>
    ModelAsset WithoutGruntySign(ModelAsset m)
    {
        if (_noSign.TryGetValue(m, out var n)) return n;
        n = m.ShallowCopy();
        n.Draws = m.Draws.Where(d => !d.Textures.Any(t => t.Texture.Contains("worlddoor_gruntydoor", StringComparison.OrdinalIgnoreCase))).ToList();
        return _noSign[m] = n;
    }

    /// <summary>objparams fields that hold the object's own model: +0xC0 for every actor / avatar / prop class (115 props,
    /// 192 characters, all entityAvatar* classes), +0x124 for vehicle blocks lying in the world, +0x248 for the small
    /// dock cranes (entityAvatarShowDownTownCraneSmall). Other model references in objparams are alternates (damaged /
    /// lit states, counter digits) or extra parts.</summary>
    static readonly int[] ObjModelFields = { 0xC0, 0x124, 0x248 };

    /// <summary>Gives a marker the model of the object it places: a model id in the record itself, or the main model
    /// of its objparams (L.O.G.'s palace in Showdown Town is the prop marker props_showdowntown_logspalace).</summary>
    void AttachMarkerModel(SceneObject obj, CaffFile markerCaff)
    {
        var r = obj.Marker!;
        string? source = null; (CaffFile Caff, int Sym, uint Bundle)? loc = null;
        foreach (var a in r.AssetIds.Where(a => a >> 24 == 0x04))
            if (FindAsset(a, markerCaff) is { } l && l.Caff.Symbols[l.Sym - 1].StartsWith("aid_model_")) { loc = l; source = "marker record"; break; }
        string? cls = null; bool sawObj = false;
        if (loc == null)
            foreach (var a in r.AssetIds.Where(a => a >> 24 == 0x1F))
            {
                if (FindAsset(a, markerCaff) is not { } op) { Audit.Note("objparams not found", $"{a:X8} ({MarkerRecord.TypeName(r.Type)})"); continue; }
                var d = op.Caff.PartsOf(op.Sym).FirstOrDefault(p => op.Caff.SectionOf(p).Name == ".data")?.Data;
                if (d == null || d.Length < 0x80) continue;
                sawObj = true;
                cls ??= NB.Core.IO.BE.CStr(d, 0x42, 62);
                foreach (int f in ObjModelFields)
                {
                    if (f + 4 > d.Length) continue;
                    uint mid = NB.Core.IO.BE.U32(d, f);
                    if (mid >> 24 != 0x04 || (mid & 0xFFFFFF) == 0) continue;
                    if (FindAsset(mid, op.Caff) is { } ml && ml.Caff.Symbols[ml.Sym - 1].StartsWith("aid_model_"))
                    {
                        loc = ml; cls = NB.Core.IO.BE.CStr(d, 0x42, 62);
                        source = $"{AssetIds.DisplayName(op.Caff.Symbols[op.Sym - 1]).Replace("aid_objparams_banjox_", "objparams ")} +0x{f:X}";
                        break;
                    }
                    Audit.Note("object models not found", $"{AssetIds.DisplayName(op.Caff.Symbols[op.Sym - 1])} +0x{f:X} → {mid:X8}");
                }
                if (loc != null) break;
            }
        if (loc == null && !sawObj && CodeSpawned(r) is { } spawned)
        {
            // objects the game's code puts at tag markers (no objparams in the record): the Jinjo houses and lock-ups
            if (FindAsset(GameAssetRef(spawned), markerCaff) is { } op && op.Caff.PartsOf(op.Sym).FirstOrDefault(p => op.Caff.SectionOf(p).Name == ".data")?.Data is { Length: > 0xC4 } d
                && NB.Core.IO.BE.U32(d, 0xC0) is uint mid && mid >> 24 == 0x04 && FindAsset(mid, op.Caff) is { } ml && ml.Caff.Symbols[ml.Sym - 1].StartsWith("aid_model_"))
            {
                loc = ml; cls = NB.Core.IO.BE.CStr(d, 0x42, 62);
                source = $"game code: \"{spawned}\" ({AssetIds.DisplayName(op.Caff.Symbols[op.Sym - 1]).Replace("aid_objparams_banjox_", "objparams ")} +0xC0) at this tag marker";
                Audit.Count("objects the game places at tag markers", spawned);
            }
            else Audit.Note("objects the game places at tag markers (not found)", spawned);
        }
        if (loc == null)
        {
            var vehicles = r.AssetIds.Where(a => a >> 24 == 0x00).Select(a => FindAsset(a, markerCaff))
                .Where(v => v != null && v.Value.Caff.Symbols[v.Value.Sym - 1].StartsWith("aid_vehicle_")).Select(v => v!.Value).ToList();
            if (vehicles.Count > 0)
            {
                // a vehicle placed by the marker (AI racers, Jinjo taxis, act vehicles): its blueprint's parts
                var v = vehicles[0];
                string vname = AssetIds.DisplayName(v.Caff.Symbols[v.Sym - 1]).Replace("aid_vehicle_banjox_", "");
                var parts = BuildVehicle(v.Caff, v.Sym, out int missing);
                if (parts.Count > 0)
                {
                    obj.Model = new ModelAsset();   // empty root: the parts are children (one per block)
                    obj.Children = parts;
                    obj.ModelBundle = v.Bundle;
                    obj.ModelSource = $"vehicle {vname} ({parts.Count} parts{(missing > 0 ? $", {missing} missing" : "")})";
                    Audit.MarkerVehiclesDrawn++;
                    Audit.Count("vehicles drawn at markers", vname);
                    if (missing > 0) Audit.Note("vehicle parts not found", $"{vname}: {missing}");
                }
                else
                {
                    Audit.MarkerVehicles++;
                    Audit.Count("vehicles placed by markers (not drawn)", vname);
                }
            }
            else if (sawObj) { Audit.MarkerObjectsWithoutModel++; Audit.Count("objects without a model of their own", (cls ?? "?").Replace("objDefId_", "")); }
            return;
        }
        var model = GetModel(loc.Value.Caff, loc.Value.Sym);
        if (model == null) { Audit.MarkerModelsFailed++; return; }
        if (r.AssetIds.Contains(WorldDoorParams))
        {
            // a world door: Grunty's challenge sign (the gruntydoor hologram) only where the game shows it
            var acts = GruntyActs();
            uint script = r.AssetIds.FirstOrDefault(a => a >> 24 == 0x19);
            obj.GruntySign = acts.TryGetValue(script, out var why);
            obj.GruntySignWhy = obj.GruntySign == true ? why : "this Act has no challenge Grunty gives";
            if (obj.GruntySign == false) model = WithoutGruntySign(model);
        }
        obj.Model = model;
        obj.ModelBundle = loc.Value.Bundle;
        obj.ModelSource = source + (loc.Value.Bundle != (Bundle & 0xFFFFFF) ? $" (model in {loc.Value.Bundle:x6})" : "");
        obj.Children = NestedModels(model, loc.Value.Caff);
        if (cls == "objDefId_entityAvatarBall")
        {
            // physics balls (Jiggoseum bowling balls): the marker is the point they rest on; the game's physics pushes the
            // ball (origin = centre) out of the floor / pedestal, so it shows standing on its lowest point (compared in Xenia)
            float minY = float.MaxValue;
            foreach (var d in model.Draws) if (!model.LodOnlyNodes.Contains(d.Node)) foreach (int i in d.Indices) if (i < d.Positions.Length) minY = MathF.Min(minY, d.Positions[i].Y);
            if (minY < 0 && minY > -100)
            {
                obj.Children = obj.Children.Prepend((model, Matrix4x4.Identity)).Select(c => (c.Item1, c.Item2 * Matrix4x4.CreateTranslation(0, -minY, 0))).ToList();
                obj.Model = new ModelAsset();   // empty root; the lifted ball is a child
            }
        }
        Audit.MarkerModels++;
        Audit.Count("objects drawn at markers", (cls ?? "marker record").Replace("objDefId_", ""));
    }

    /// <summary>Grass layers of the world (chunk 17), with their grass model; tiles are laid out by the viewer once the
    /// shadow (density) texture is chosen (<see cref="GrassLayer.BuildTiles"/>).</summary>
    public readonly List<GrassLayer> Grass = new();

    void LoadGrass()
    {
        var sources = new List<(ModelAsset Model, CaffFile Caff, Matrix4x4 Place, string Name)> { (Background, Caff, Matrix4x4.Identity, "background") };
        foreach (var o in Objects.Where(o => o.Kind == SceneObjectKind.Scenery && o.Model != null && o.Model.Chunks.ContainsKey(17)))
            sources.Add((o.Model!, LoadBundle(o.ModelBundle) ?? Caff, o.Transform, o.Name));
        foreach (var (model, caff, place, name) in sources)
        {
            if (!model.Chunks.TryGetValue(17, out int c17)) continue;
            var layers = GrassLayer.Read(model.View.Data(".data"), c17, place, name);
            foreach (var l in layers)
            {
                if (FindAsset(l.ModelId, caff) is { } gl && GetModel(gl.Caff, gl.Sym) is { } gm)
                {
                    l.Model = gm; l.ModelName = AssetIds.DisplayName(gl.Caff.Symbols[gl.Sym - 1]);
                    try { l.Mesh = GrassMesh.From(gm); } catch (Exception e) { Log.Add($"grass model {l.ModelName}: {e.Message}"); }
                }
                l.HeightName = NameById.GetValueOrDefault(l.HeightId) ?? (FindAsset(l.HeightId, caff) is { } hl ? AssetIds.DisplayName(hl.Caff.Symbols[hl.Sym - 1]) : "");
                Grass.Add(l);
            }
            Audit.GrassLayers += layers.Count;
            var kinds = layers.GroupBy(l => l.ModelName.Replace("aid_model_banjox_background_", "")).Select(g => $"{(g.Key == "" ? "?" : g.Key)} ×{g.Count()}");
            Audit.Note("grass layers (chunk 17, tiles laid out like the game)", $"{name}: {layers.Count} layers: {string.Join(", ", kinds)}");
        }
    }

    /// <summary>Collision per model name (aid_model_X ↔ aid_havok_X), in model space. Filled by <see cref="LoadCollision"/>.</summary>
    public Dictionary<string, List<NB.Core.Havok.CollisionMesh>>? CollisionByModel;

    /// <summary>Decodes the Havok collision of the terrain and of every reference model present in the bundle.</summary>
    public (int Models, int Triangles, List<string> Notes) LoadCollision()
    {
        if (CollisionByModel != null) return (CollisionByModel.Count, CollisionByModel.Values.Sum(l => l.Sum(m => m.Triangles.Count / 3)), new());
        var result = new Dictionary<string, List<NB.Core.Havok.CollisionMesh>>();
        var notes = new List<string>();
        var bySym = new Dictionary<string, int>();
        for (int s = 1; s <= Caff.Symbols.Count; s++) bySym[AssetIds.DisplayName(Caff.Symbols[s - 1])] = s;
        foreach (var model in Objects.Where(o => o.Model != null && o.Kind != SceneObjectKind.Marker && o.ModelName.StartsWith("aid_model_")).Select(o => o.ModelName).Distinct())
        {
            var hk = "aid_havok_" + AssetIds.DisplayName(model)["aid_model_".Length..];
            if (!bySym.TryGetValue(hk, out int sym)) continue;
            try
            {
                var d = new AssetView(Caff, sym, _relocs).Data(".data");
                if (!NB.Core.Havok.HkPackfile.IsPackfileAsset(d)) continue;
                var hc = NB.Core.Havok.HkCollision.ExtractAsset(d);
                result[model] = hc.Meshes;
                notes.AddRange(hc.Notes.Select(n => $"{hk}: {n}"));
            }
            catch (Exception e) { notes.Add($"{hk}: {e.Message}"); }
        }
        CollisionByModel = result;
        return (result.Count, result.Values.Sum(l => l.Sum(m => m.Triangles.Count / 3)), notes);
    }

    /// <summary>
    /// Havok collision of one object, per drawn model: every model's collision is the aid_havok asset of the same name
    /// (terrain, scenery, nested reference models, and all 269 marker-object models of Showdown Town and the common bundle
    /// checked), looked up in the model's bundle, then the load set. Meshes are in the model's space; Local places them
    /// in the object (children of composite buildings, vehicle parts). Cached per model.
    /// </summary>
    public List<(string Asset, List<NB.Core.Havok.CollisionMesh> Meshes, Matrix4x4 Local)> CollisionOf(SceneObject o)
    {
        var list = new List<(string, List<NB.Core.Havok.CollisionMesh>, Matrix4x4)>();
        var models = new List<(ModelAsset M, Matrix4x4 L)>();
        if (o.Model != null) models.Add((o.Model, Matrix4x4.Identity));
        models.AddRange(o.Children);
        foreach (var (m, local) in models)
        {
            if (m.View == null) continue;
            if (!_collByModel.TryGetValue(m, out var hit))
            {
                hit = null;
                try
                {
                    string name = AssetIds.DisplayName(m.View.Name);
                    if (name.StartsWith("aid_model_") && AssetIds.IdOf("aid_havok_" + name["aid_model_".Length..]) is uint hid
                        && FindAsset(hid, m.View.Caff) is { } loc)
                    {
                        if (!_relocByCaff.TryGetValue(loc.Caff, out var rel)) _relocByCaff[loc.Caff] = rel = AssetView.BuildRelocIndex(loc.Caff);
                        var d = new AssetView(loc.Caff, loc.Sym, rel).Data(".data");
                        // actors' assets are empty 32-byte placeholders (their physics body is made at run time)
                        hit = (AssetIds.DisplayName(loc.Caff.Symbols[loc.Sym - 1]),
                               NB.Core.Havok.HkPackfile.IsPackfileAsset(d) ? NB.Core.Havok.HkCollision.ExtractAsset(d).Meshes : new List<NB.Core.Havok.CollisionMesh>());
                    }
                }
                catch (Exception e) { Log.Add($"collision of {AssetIds.DisplayName(m.View.Name)}: {e.Message}"); }
                _collByModel[m] = hit;
            }
            if (hit is { } h) list.Add((h.Asset, h.Meshes, local));
        }
        return list;
    }
    readonly Dictionary<ModelAsset, (string Asset, List<NB.Core.Havok.CollisionMesh> Meshes)?> _collByModel = new(ReferenceEqualityComparer.Instance);

    public readonly List<MarkerAsset> Markers = new();
    /// <summary>Asset id → display name for every asset in the bundle.</summary>
    public readonly Dictionary<uint, string> NameById = new();

    readonly Dictionary<ModelAsset, List<(ModelAsset, Matrix4x4)>> _nested = new();

    /// <summary>Flattens the reference-model instances inside <paramref name="m"/> (recursively, up to 6 levels) into
    /// (model, transform relative to m). Instance matrices inside a reference model are in that model's space. Nested
    /// models are looked up in the parent's bundle first, then in the world's load set.</summary>
    List<(ModelAsset Model, Matrix4x4 Local)> NestedModels(ModelAsset m, CaffFile caff, int depth = 0)
    {
        if (_nested.TryGetValue(m, out var hit)) return hit;
        var list = new List<(ModelAsset, Matrix4x4)>();
        _nested[m] = list;   // guards against cycles
        if (depth < 6)
            foreach (var inst in m.Instances)
            {
                uint rid = inst.RefModel >= 0 && inst.RefModel < m.ReferenceIds.Count ? (uint)m.ReferenceIds[inst.RefModel] : 0;
                Audit.Nested++;
                var loc = FindAsset(rid, caff);
                var child = loc != null ? GetModel(loc.Value.Caff, loc.Value.Sym) : null;
                if (child == null)
                {
                    Audit.NestedMissing++;
                    Audit.Note("nested models not found", $"{AssetIds.DisplayName(m.View.Name)} → {rid:X8}");
                    Log.Add($"{AssetIds.DisplayName(m.View.Name)}: nested model {rid:X8} not found in the workspace");
                    continue;
                }
                if (child == m) continue;
                if (!ReferenceEquals(loc!.Value.Caff, caff)) Audit.Note("nested models from another bundle", $"{AssetIds.DisplayName(child.View.Name)} ({loc.Value.Bundle:x6})");
                list.Add((child, inst.World));
                foreach (var (gm, gl) in NestedModels(child, loc.Value.Caff, depth + 1)) list.Add((gm, gl * inst.World));
            }
        return list;
    }

    /// <summary>Parses (once) the model at <paramref name="sym"/> of <paramref name="caff"/>; null when it fails to parse.</summary>
    ModelAsset? GetModel(CaffFile caff, int sym)
    {
        var name = caff.Symbols[sym - 1];
        if (Models.TryGetValue(name, out var m)) return m;
        if (_failed.Contains(name)) return null;
        if (!_relocByCaff.TryGetValue(caff, out var rel)) _relocByCaff[caff] = rel = AssetView.BuildRelocIndex(caff);
        try { m = ModelAsset.Parse(caff, sym, rel); }
        catch (Exception e)
        {
            _failed.Add(name); Audit.ModelsFailed++;
            Audit.Note("models that failed to parse", $"{AssetIds.DisplayName(name)}: {e.Message}");
            Log.Add($"{AssetIds.DisplayName(name)}: parse failed: {e.Message}");
            return null;
        }
        foreach (var w in m.Warnings.Take(5)) Log.Add($"{AssetIds.DisplayName(name)}: {w}");
        if (m.Warnings.Count > 0) Audit.Note("models with parse warnings", $"{AssetIds.DisplayName(name)}: {m.Warnings[0]}");
        Models[name] = m;
        return m;
    }
    readonly HashSet<string> _failed = new();

    static void ComputeBounds(SceneObject o)
    {
        if (o.Kind == SceneObjectKind.Marker && o.Model == null) return;
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        void Add(ModelAsset m, Matrix4x4 xf)
        {
            foreach (var d in m.Draws)
                foreach (int i in d.Indices)   // only vertices that are drawn (imported models keep unused old vertices)
                    if (i < d.Positions.Length) { var p = Vector3.Transform(d.Positions[i], xf); mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
        }
        if (o.Model != null) Add(o.Model, Matrix4x4.Identity);
        foreach (var (cm, cl) in o.Children) Add(cm, cl);
        o.CullDistance = o.Model == null ? float.PositiveInfinity
            : o.Children.Select(c => c.Model).Prepend(o.Model).Distinct().Select(m => m.CullDistance).DefaultIfEmpty(float.PositiveInfinity).Max();
        if (mn.X > mx.X) { mn = new Vector3(-1); mx = new Vector3(1); }
        o.BoundsMin = mn; o.BoundsMax = mx;
    }

    // ---------------------------------------------------------------- textures

    readonly Dictionary<string, (byte[] Rgba, int W, int H)?> _texCache = new();

    /// <summary>Decodes a texture referenced by a draw (uses the resident "mip" asset: half resolution).</summary>
    /// <summary>Workspace-wide texture lookup (world bundle, other resident bundles, stream archives). When null, only
    /// the world bundle is searched.</summary>
    public NB.Core.Textures.TextureResolver? Textures;

    /// <summary>Distinct diffuse textures used by the world's models (what the viewport draws).</summary>
    public IEnumerable<string> DiffuseTextureNames() =>
        Objects.Where(o => o.Model != null).SelectMany(o => o.Children.Select(c => c.Model).Prepend(o.Model!)).Distinct().SelectMany(m => m.Draws)
            .SelectMany(d => { var l = NB.Core.Models.ObjExporter.MaterialLayers(d); return new[] { l.Base, l.Overlay, l.Mask }; }).Where(n => n != null).Select(n => n!).Distinct();

    public (byte[] Rgba, int W, int H)? LoadTexture(string name)
    {
        if (Textures != null) return Textures.Load(name);
        if (_texCache.TryGetValue(name, out var t)) return t;
        t = null;
        try
        {
            string stem = name.EndsWith("top") || name.EndsWith("mip") ? name[..^3] : name;
            foreach (var cand in new[] { stem + "mip", stem, stem + "top" })
            {
                int s = Caff.Symbols.FindIndex(x => x.Contains(cand + "\\") || AssetIds.DisplayName(x) == cand) + 1;
                if (s == 0) continue;
                var parts = Caff.PartsOf(s).ToList();
                var cpu = parts.FirstOrDefault(p => Caff.SectionOf(p).Name == ".data");
                var gpu = parts.FirstOrDefault(p => Caff.SectionOf(p).Name == ".texturegpu");
                if (cpu == null || gpu == null || !TextureHeader.IsTexture(cpu.Data)) continue;
                var ta = new TextureAsset(cpu.Data, gpu.Data);
                t = ta.Decode(0);
                break;
            }
        }
        catch (Exception e) { Log.Add($"texture {name}: {e.Message}"); }
        _texCache[name] = t;
        return t;
    }

    // ---------------------------------------------------------------- editing

    /// <summary>Writes every changed scenery transform into the background model's .data part.</summary>
    /// <summary>Bundles changed by the last <see cref="ApplyEdits"/> (the world bundle and/or act bundles).</summary>
    public readonly HashSet<uint> DirtyBundles = new();

    /// <summary>Saving keeps every marker's stored scale (the editor locks marker scale). Off: a scaled marker transform
    /// writes its mean scale (old behaviour).</summary>
    public bool LockMarkerScale = true;

    public int ApplyEdits()
    {
        var part = Background.View.Part(Background.View.PartId(".data"));
        var d = part.Data;
        int changed = 0;
        foreach (var o in Objects)
        {
            if (o.Kind == SceneObjectKind.Marker && o.Marker != null && o.MarkerSet != null && o.Dirty)
            {
                if (o.Transform != o.OriginalTransform)
                {
                    Matrix4x4.Decompose(o.Transform, out var sc, out var q, out var t);
                    o.Marker.Position = t;
                    o.Marker.Rotation = MathUtil.EulerXYZ(Matrix4x4.CreateFromQuaternion(q));
                    // the record's scale is kept as stored: the editor does not scale markers (path nodes carry values
                    // such as 0.5 there that are not a size), and a rounding drift must not rewrite it
                    if (MathF.Abs((sc.X + sc.Y + sc.Z) / 3 - (o.Marker.Scale == 0 ? 1 : o.Marker.Scale)) > 1e-3f && !LockMarkerScale) o.Marker.Scale = (sc.X + sc.Y + sc.Z) / 3;
                    MarkerAsset.WriteTransform(o.MarkerSet.Caff ?? Caff, o.MarkerSet.Symbol, o.Marker);
                    o.Transform = o.Marker.Matrix; o.OriginalTransform = o.Transform;
                }
                if (o.Marker.Type == 22 && o.Marker.Link != o.Marker.SavedLink) MarkerAsset.WriteLink(o.MarkerSet.Caff ?? Caff, o.MarkerSet.Symbol, o.Marker);
                DirtyBundles.Add(o.MarkerSet.Caff != null ? o.MarkerSet.Bundle : Bundle);
                changed++;
                continue;
            }
            if (o.Kind != SceneObjectKind.Scenery || o.Instance == null || !o.Dirty) continue;
            var m = o.Transform;
            float[] f = { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 };
            for (int k = 0; k < 16; k++) BE.WF32(d, o.Instance.MatrixOffset + 4 * k, f[k]);
            BE.WF32(d, o.Instance.PositionOffset, m.M41); BE.WF32(d, o.Instance.PositionOffset + 4, m.M42); BE.WF32(d, o.Instance.PositionOffset + 8, m.M43);
            if (o.Instance.PlacementNode >= 0 && Background.Chunks.TryGetValue(2, out int c2))
            {
                // chunk-2 node: 3 rows (r0 r1 r2 t) — the transpose of the row-vector rotation plus translation
                int b = c2 + 4 + 68 * o.Instance.PlacementNode + 4;
                float[] rows = { m.M11, m.M21, m.M31, m.M41, m.M12, m.M22, m.M32, m.M42, m.M13, m.M23, m.M33, m.M43 };
                for (int k = 0; k < 12; k++) BE.WF32(d, b + 4 * k, rows[k]);
            }
            o.Instance.World = m;
            o.OriginalTransform = m;
            DirtyBundles.Add(Bundle);
            changed++;
        }
        return changed;
    }

    /// <summary>Applies edits and writes the bundle into the workspace game directory.</summary>
    public int Save()
    {
        var names = Objects.Where(o => o.Dirty).Select(o => o.Name).Take(8).ToList();
        DirtyBundles.Clear();
        int n = ApplyEdits();
        foreach (var b in DirtyBundles)
        {
            var caff = b == Bundle ? Caff : Markers.First(m => m.Bundle == b && m.Caff != null).Caff!;
            Workspace.SaveResident(b, caff, $"moved {n} object(s) (world {Bundle:x6}): {string.Join(", ", names)}{(n > 8 ? ", …" : "")}");
        }
        return n;
    }
}
