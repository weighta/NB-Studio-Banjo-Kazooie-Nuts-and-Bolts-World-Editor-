using System.Numerics;
using NB.Core.Formats;
using NB.Core.Models;
using NB.Core.World;
using NB.Studio.Viewport;

namespace NB.Studio;

/// <summary>
/// Copy / paste across workspaces and worlds, and copying collision with the objects.
/// <list type="bullet">
/// <item>Ctrl+C keeps a self-contained package of the copied scenery (<see cref="TransplantPackage"/>): each model's
/// geometry per material, the decoded textures and its collision. Pasted into another workspace or world, the models
/// are reused when identical there, otherwise rebuilt (<see cref="AssetTransplant"/>) and placed; one bundle save = one
/// undo step. Before 1.23 a paste into another Showdown Town workspace duplicated whatever instance had the copied
/// index there (the user's oil rig arrived as an unrelated untextured model).</item>
/// <item>Copy with collision (Ctrl+C): an object's own collision (aid_havok_ of its model) goes with its model. Pieces of a
/// Source map import have none (their collision is in the level's terrain collision): they take the terrain triangles
/// that lie inside their own box (padded a little; never the level's outer shell). Game models without collision of
/// their own take level collision only with Edit > Copy With Level Collision. The triangles are highlighted and
/// counted. They are pasted as the own collision of a rebuilt copy of one copied object's model (a separate collision
/// asset added as a second entry of the instance's collision list hung the level load in Xenia; the same triangles as the
/// model's own collision load and collide). In the same world a paste whose carrier would be a game model stays a plain
/// copy. Edit > Copy Mesh Only (Ctrl+Shift+C) leaves all collision out, also the models' own.</item>
/// <item>The paste builds on a copy of the world bundle on a worker thread (progress in the status bar) and replaces the
/// open bundle only after the copy was saved: a failed paste changes nothing.</item>
/// </list>
/// </summary>
public sealed partial class MainForm
{
    /// <summary>Workspace and world the clipboard was copied in, its package (built at copy time) and the collision taken
    /// from the terrain (world space; null: none).</summary>
    string? _clipWs;
    TransplantPackage? _clipPkg;
    (List<Vector3> P, List<int> T)? _clipColl;
    int _clipCarrier = -1;   // the copied object whose pasted copy carries _clipColl (as its model's own collision)
    bool _clipCarrierGame;   // … and it is a game model (not a Source map piece): same-world pastes stay plain copies
    bool _clipMeshOnly;      // Copy Mesh Only: no collision at all, also not the models' own
    string? _pasteFailTest;  // script --paste-fail build|save: a forced failure (a failed paste must change nothing)
    int _pasteSlowMs;        // script --paste-slow MS: the build waits that long (to try other actions meanwhile)
    Task? _pasteTask;        // script --paste-async / --paste-wait
    /// <summary>A paste is being built on a worker: the world, its bundle and the workspace must not change meanwhile.</summary>
    bool _pasting;

    /// <summary>True (and says so) while a paste is being built; actions that open another world, restore, revert or
    /// rebuild the world bundle, undo, or close the workspace call this first. (_busy alone is not enough: other actions
    /// set and clear it without checking.)</summary>
    bool PasteRunning(string what)
    {
        if (!_pasting) return false;
        Log($"{what}: not now — a paste is being built (see the status bar); try again when it is done.");
        return true;
    }

    /// <summary>A model made by a Source map import (or pasted from one): its collision is in the level's terrain.</summary>
    static bool Imported(SceneObject o, TransplantModel? tm) =>
        o.ModelName.Contains("_vmf_", StringComparison.OrdinalIgnoreCase) ||
        tm?.Parts.Any(p => p.Colour?.Contains("_vmf_", StringComparison.OrdinalIgnoreCase) == true) == true;

    bool SameWorldClip() => _clipWs == _ws?.Root && _clip.Count > 0 && _clip[0].Bundle == _scene?.Bundle;

    /// <summary>Builds the package and the collision of a copy (CopySelection).</summary>
    void PrepareClip(List<SceneObject> list, bool meshOnly, bool levelCollision)
    {
        _clipWs = _ws?.Root; _clipColl = null; _clipCarrier = -1; _clipCarrierGame = false; _clipMeshOnly = meshOnly;
        _clipPkg = new TransplantPackage { SourceWorkspace = _ws?.Root ?? "", SourceBundle = _scene!.Bundle };
        foreach (var o in list)
        {
            var name = AssetIds.DisplayName(o.ModelName);
            if (!_clipPkg.Models.ContainsKey(name))
                try { _clipPkg.Models[name] = PackModel(o, _clipPkg); }
                catch (Exception e) { _clipPkg.Problems.Add($"{name}: {e.Message}"); }
        }
        TransplantModel? Tm(SceneObject o) => _clipPkg.Models.GetValueOrDefault(AssetIds.DisplayName(o.ModelName));
        if (meshOnly)
            foreach (var m in _clipPkg.Models.Values) { m.Havok = null; m.HavokSignature = 0; m.CollisionPositions.Clear(); m.CollisionTriangles.Clear(); }
        var own = list.Where(o => Tm(o)?.CollisionTriangles.Count > 0).ToHashSet();
        int ownTris = own.Select(Tm).Distinct().Sum(m => m!.CollisionTriangles.Count / 3);
        // level collision: automatic for Source map pieces, for game models only when asked (Copy With Level Collision)
        var rest = meshOnly ? new List<SceneObject>() : list.Where(o => !own.Contains(o) && (levelCollision || Imported(o, Tm(o)))).OrderBy(o => Imported(o, Tm(o)) ? 0 : 1).ToList();
        int gameNone = meshOnly || levelCollision ? 0 : list.Count(o => !own.Contains(o) && !Imported(o, Tm(o)));
        if (rest.Count > 0) _clipColl = TerrainCollisionInside(rest);
        if (_clipColl is { } cc && rest.Count > 0 && Tm(rest[0]) is { } m0 && m0.Parts.Count > 0 && Matrix4x4.Invert(rest[0].Transform, out var inv0))
        {
            // a separate collision asset as a second entry of the instance's collision list hung the level load
            // (Xenia, "Loading…" for 5 min); so the collision goes into a copy of one copied object's model, as that
            // model's own collision (aid_havok_X of aid_model_X, in its space), and that object is pasted as the copy
            var carrier = new TransplantModel { Name = m0.Name, Signature = 0 };
            carrier.Parts.AddRange(m0.Parts);
            carrier.CollisionPositions.AddRange(m0.CollisionPositions);
            carrier.CollisionTriangles.AddRange(m0.CollisionTriangles);
            int b0 = carrier.CollisionPositions.Count;
            carrier.CollisionPositions.AddRange(cc.P.Select(q => Vector3.Transform(q, inv0)));
            carrier.CollisionTriangles.AddRange(cc.T.Select(t => b0 + t));
            _clipPkg.Models[m0.Name + TransplantPackage.CarrierSuffix] = carrier;
            if (list.Count(o => AssetIds.DisplayName(o.ModelName) == m0.Name) == 1) _clipPkg.Models.Remove(m0.Name);   // only the carrier is placed
            _clipCarrier = list.IndexOf(rest[0]);
            _clipCarrierGame = !Imported(rest[0], m0);
        }
        int tt = _clipColl?.T.Count / 3 ?? 0;
        _view.SetClipCollision(_clipColl is { } c ? c.P : null, _clipColl?.T);
        if (meshOnly) Log("  Mesh only: no collision goes with the copies (not even the models' own).");
        else
        {
            Log(tt + ownTris == 0 ? "  No collision found with the copied objects (their models have none and no level collision lies inside their boxes)."
                : $"  With collision: {(ownTris > 0 ? $"{ownTris} triangles of the models' own collision" : "")}{(ownTris > 0 && tt > 0 ? " + " : "")}{(tt > 0 ? $"{tt} collision triangles of the level inside the copied objects' boxes (highlighted in magenta)" : "")}. Edit > Copy Mesh Only (Ctrl+Shift+C) copies without them.");
            if (gameNone > 0) Log($"  {gameNone} game object(s) without collision of their own: the level collision around them is not taken (Edit > Copy With Level Collision takes it).");
        }
        foreach (var p in _clipPkg.Problems) Log("  Copy: " + p);
    }

    /// <summary>One model's geometry (its LOD 0 draws and nested models, in model space) per material, its textures and
    /// its own collision.</summary>
    TransplantModel PackModel(SceneObject o, TransplantPackage pkg)
    {
        var tm = new TransplantModel { Name = AssetIds.DisplayName(o.ModelName) };
        if (o.Model?.View is { } v) tm.Signature = AssetTransplant.Signature(v.Caff, v.Symbol);
        var parts = new Dictionary<string, TransplantPart>();
        int glow = 0;
        void Add(ModelAsset m, Matrix4x4 local)
        {
            foreach (var d in m.Draws)
            {
                if (m.LodOnlyNodes.Contains(d.Node) || d.Positions.Length == 0 || d.Indices.Length < 3) continue;
                var mi = MaterialInfo.Of(d);
                // shadow planes (multiply) and glows / light beams (additive) have no template here: left out, not pasted solid
                if (mi.Blend is BlendKind.Multiply or BlendKind.Additive) { glow++; continue; }
                var kind = mi.Blend switch { BlendKind.Blend => TransplantPart.Kinds.Blend, BlendKind.Cutout => TransplantPart.Kinds.Cutout, _ => TransplantPart.Kinds.Opaque };
                string? colour = mi.Base != null ? ObjExporter.TextureFileStem(mi.Base) : null;
                // a separate mask texture; alpha in the colour texture itself becomes a mask in AssetTransplant.Finish
                string? mask = kind == TransplantPart.Kinds.Cutout && mi.AlphaTex != null && ObjExporter.TextureFileStem(mi.AlphaTex) != colour ? ObjExporter.TextureFileStem(mi.AlphaTex) : null;
                // the ambient-occlusion map or baked lightmap on the second UV set (opaque only; a Source map import's
                // vmf_lm_ pages count as colour by name, so they are found by name here)
                var bound = (d.Passes.Length > 0 && d.Passes[0].Textures.Count > 0 ? d.Passes[0].Textures : d.Textures).Select(t => t.Texture);
                string? aoTex = mi.Ao ?? bound.FirstOrDefault(t => t.Contains("_vmf_lm_", StringComparison.OrdinalIgnoreCase));
                string? ao = kind == TransplantPart.Kinds.Opaque && aoTex != null && d.UVs2 != null ? ObjExporter.TextureFileStem(aoTex) : null;
                // only real triangles (models built from a template keep its other draws as one degenerate triangle), and
                // only the vertices they use (draws share vertex buffers)
                var map = new Dictionary<int, int>();
                var tris = new List<int>();
                int n = d.Positions.Length;
                for (int k = 0; k + 2 < d.Indices.Length; k += 3)
                {
                    int a = d.Indices[k], b = d.Indices[k + 1], c = d.Indices[k + 2];
                    if (a >= n || b >= n || c >= n || a == b || b == c || a == c) continue;
                    if (Vector3.Cross(d.Positions[b] - d.Positions[a], d.Positions[c] - d.Positions[a]).LengthSquared() < 1e-12f) continue;
                    tris.Add(a); tris.Add(b); tris.Add(c);
                }
                if (tris.Count == 0) continue;
                string key = $"{kind}|{colour}|{mask}|{ao}";
                if (!parts.TryGetValue(key, out var part))
                {
                    parts[key] = part = new TransplantPart { Kind = kind, Colour = colour, Mask = mask, Ao = ao, Mesh = new ImportMesh { Name = colour ?? "untextured", Normals = new(), UVs = new(), UVs2 = ao != null ? new() : null } };
                    foreach (var t in new[] { colour, mask, ao }) if (t != null) PackTexture(t, kind == TransplantPart.Kinds.Blend && t == colour, pkg);
                }
                var mesh = part.Mesh;
                foreach (int i in tris)
                {
                    if (!map.TryGetValue(i, out int j))
                    {
                        map[i] = j = mesh.Positions.Count;
                        mesh.Positions.Add(Vector3.Transform(d.Positions[i], local));
                        var nn = d.Normals != null ? Vector3.TransformNormal(d.Normals[i], local) : Vector3.UnitY;
                        mesh.Normals!.Add(nn.LengthSquared() > 1e-12f ? Vector3.Normalize(nn) : Vector3.UnitY);
                        mesh.UVs!.Add(d.UVs != null ? d.UVs[i] : Vector2.Zero);
                        mesh.UVs2?.Add(d.UVs2 != null && i < d.UVs2.Length ? d.UVs2[i] : Vector2.Zero);
                    }
                    mesh.Triangles.Add(j);
                }
            }
        }
        if (o.Model != null) Add(o.Model, Matrix4x4.Identity);
        foreach (var (cm, cl) in o.Children) Add(cm, cl);
        tm.Parts.AddRange(parts.Values.Where(p => p.Mesh.Triangles.Count > 0));
        if (glow > 0) pkg.Problems.Add($"{tm.Name}: {glow} shadow / glow draw(s) (multiply or additive) are not carried");
        pkg.Notes.AddRange(AssetTransplant.Finish(pkg, tm));   // cut-out masks, tiled see-through faces, big parts split
        if (tm.Parts.Count == 0) pkg.Problems.Add($"{tm.Name}: no drawable geometry");
        // its own collision (the aid_havok_ asset of the model and of nested models); the viewport decodes collision on a
        // worker thread under the same lock
        string own = "aid_havok_" + tm.Name["aid_model_".Length..];
        lock (_scene!)
            foreach (var (asset, meshes, local) in _scene.CollisionOf(o))
            {
                if (asset == own && o.Model?.View is { } mv && AssetTransplant.Find(mv.Caff, asset) is int hs && hs > 0) { tm.Havok = asset; tm.HavokSignature = AssetTransplant.Signature(mv.Caff, hs); }
                foreach (var cm in meshes)
                {
                    int b = tm.CollisionPositions.Count;
                    tm.CollisionPositions.AddRange(cm.Positions.Select(p => Vector3.Transform(p, local)));
                    tm.CollisionTriangles.AddRange(cm.Triangles.Select(t => b + t));
                }
            }
        return tm;
    }

    /// <summary>A texture at full resolution (the game's resident "mip" entry is half size), at most 1024 on a side, with
    /// the source assets it was read from (stem, stem+"mip", stem+"top") and their checksums.</summary>
    void PackTexture(string stem, bool alpha, TransplantPackage pkg)
    {
        if (pkg.Textures.ContainsKey(stem)) return;
        var img = _scene!.Textures?.LoadFull(stem) ?? _scene.LoadTexture(stem) ?? _scene.LoadTexture(stem + "mip");
        if (img is not { } t) { pkg.Problems.Add($"texture {stem}: could not be read (the copy would be untextured there)"); return; }
        var (rgba, w, h) = t;
        while (w > 1024 || h > 1024) (rgba, w, h) = NB.Core.Textures.ImageIO.Resize(rgba, w, h, Math.Max(1, w / 2), Math.Max(1, h / 2));
        var tx = new TransplantTexture { Name = stem, Rgba = rgba, W = w, H = h, Format = alpha ? NB.Core.Textures.XenosFormat.DXT2_3 : NB.Core.Textures.XenosFormat.DXT1 };
        foreach (var cand in new[] { stem, stem + "mip", stem + "top" })
            if (AssetTransplant.Find(_scene.Caff, cand) is int s && s > 0) tx.SourceAssets[cand] = AssetTransplant.Signature(_scene.Caff, s);
        tx.Signature = tx.SourceAssets.Count > 0 ? tx.SourceAssets.Values.First() : NB.Core.IO.FastHash.Of(rgba);
        pkg.Textures[stem] = tx;
    }

    /// <summary>The level's terrain collision triangles that lie inside the box of one of <paramref name="objs"/> (each
    /// box padded by 2 % and a quarter unit), world space. Triangles of the level's outer shell (flat on a face of the
    /// terrain collision's bounding box and spanning most of it) are never taken.</summary>
    (List<Vector3> P, List<int> T)? TerrainCollisionInside(List<SceneObject> objs)
    {
        var boxes = new List<(Vector3 Min, Vector3 Max)>();
        foreach (var o in objs)
        {
            var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
            for (int i = 0; i < 8; i++)
            {
                var c = new Vector3((i & 1) != 0 ? o.BoundsMax.X : o.BoundsMin.X, (i & 2) != 0 ? o.BoundsMax.Y : o.BoundsMin.Y, (i & 4) != 0 ? o.BoundsMax.Z : o.BoundsMin.Z);
                var w = Vector3.Transform(c, o.Transform); mn = Vector3.Min(mn, w); mx = Vector3.Max(mx, w);
            }
            if (mn.X > mx.X) continue;
            var pad = (mx - mn) * 0.02f + new Vector3(0.25f);
            boxes.Add((mn - pad, mx + pad));
        }
        var terrain = _scene!.Objects.FirstOrDefault(o => o.Kind == SceneObjectKind.Terrain);
        if (terrain == null || boxes.Count == 0) return null;
        var tris = new List<(Vector3, Vector3, Vector3)>();
        var smn = new Vector3(float.MaxValue); var smx = new Vector3(float.MinValue);
        lock (_scene)
            foreach (var (_, meshes, local) in _scene.CollisionOf(terrain))
            {
                var toWorld = local * terrain.Transform;
                foreach (var cm in meshes)
                {
                    var wp = cm.Positions.Select(p => Vector3.Transform(p, toWorld)).ToList();
                    foreach (var p in wp) { smn = Vector3.Min(smn, p); smx = Vector3.Max(smx, p); }
                    for (int k = 0; k + 2 < cm.Triangles.Count; k += 3) tris.Add((wp[cm.Triangles[k]], wp[cm.Triangles[k + 1]], wp[cm.Triangles[k + 2]]));
                }
            }
        static bool In(Vector3 p, (Vector3 Min, Vector3 Max) b) => p.X >= b.Min.X && p.Y >= b.Min.Y && p.Z >= b.Min.Z && p.X <= b.Max.X && p.Y <= b.Max.Y && p.Z <= b.Max.Z;
        var ext = smx - smn;
        float tol = MathF.Max(0.05f, 0.002f * MathF.Max(ext.X, MathF.Max(ext.Y, ext.Z)));
        bool OnShell(Vector3 a, Vector3 b, Vector3 c)
        {
            // flat on a face of the level's collision box AND covering at least half of that face in both directions:
            // the closed box every level ends in, not an outermost wall or the highest roof
            var tmn = Vector3.Min(a, Vector3.Min(b, c)); var tmx = Vector3.Max(a, Vector3.Max(b, c)); var te = tmx - tmn;
            for (int ax = 0; ax < 3; ax++)
            {
                float lo = ax == 0 ? smn.X : ax == 1 ? smn.Y : smn.Z, hi = ax == 0 ? smx.X : ax == 1 ? smx.Y : smx.Z;
                float tlo = ax == 0 ? tmn.X : ax == 1 ? tmn.Y : tmn.Z, thi = ax == 0 ? tmx.X : ax == 1 ? tmx.Y : tmx.Z;
                bool onFace = (thi - lo < tol && tlo - lo > -tol) || (hi - tlo < tol && hi - thi > -tol);
                if (!onFace) continue;
                bool wide = ax switch
                {
                    0 => te.Y >= 0.5f * ext.Y && te.Z >= 0.5f * ext.Z,
                    1 => te.X >= 0.5f * ext.X && te.Z >= 0.5f * ext.Z,
                    _ => te.X >= 0.5f * ext.X && te.Y >= 0.5f * ext.Y,
                };
                if (wide) return true;
            }
            return false;
        }
        var P = new List<Vector3>(); var T = new List<int>();
        foreach (var (a, b, c) in tris)
        {
            if (!boxes.Any(bx => In(a, bx) && In(b, bx) && In(c, bx)) || OnShell(a, b, c)) continue;
            T.Add(P.Count); P.Add(a); T.Add(P.Count); P.Add(b); T.Add(P.Count); P.Add(c);
        }
        return T.Count == 0 ? null : (P, T);
    }

    /// <summary>True when the paste needs the package: another workspace or world, or level collision to carry with a
    /// Source map piece. In the same world a carrier that would be a game model (rebuilt with the importer's simpler
    /// materials) is not built: the paste stays a plain copy.</summary>
    bool PasteNeedsTransplant() => _clipPkg != null && (!SameWorldClip() || (_clipColl != null && _clipCarrier >= 0 && !_clipCarrierGame));

    /// <summary>Script --coll-list NAME: the collision-list records (model, havok) of the scenery objects named NAME in the
    /// world havok asset's type-7 table (what the game builds their collision from; empty = no collision).</summary>
    string CollisionListText(string name)
    {
        if (_scene == null) return "no world";
        string havokName = AssetIds.DisplayName(_scene.Background.View.Name).Replace("aid_model_", "aid_havok_");
        int sym = AssetTransplant.Find(_scene.Caff, havokName);
        if (sym == 0) return "no " + havokName;
        var v = new AssetView(_scene.Caff, sym);
        var d = v.Data(".data");
        int t = NB.Core.IO.BE.S32(d, 0), cn = NB.Core.IO.BE.S32(d, 4), e7 = -1;
        for (int i = 0; i < cn; i++) if (NB.Core.IO.BE.S32(d, t + 8 * i) == 7) e7 = NB.Core.IO.BE.S32(d, t + 8 * i + 4);
        if (e7 < 0) return "no collision table";
        int table = NB.Core.IO.BE.S32(d, e7), n = NB.Core.IO.BE.S32(d, e7 + 4);
        var sb = new List<string>();
        foreach (var o in _scene.Objects.Where(x => x.Instance != null && x.Name == name))
        {
            int i = o.Instance!.Index;
            if (i >= n) { sb.Add($"#{i}: beyond the table"); continue; }
            int at = NB.Core.IO.BE.S32(d, table + 8 * i), k = NB.Core.IO.BE.S32(d, table + 8 * i + 4);
            var recs = Enumerable.Range(0, k).Select(j => _scene.NameById.GetValueOrDefault(NB.Core.IO.BE.U32(d, at + 16 * j + 4), "?").Replace("aid_havok_banjox_", ""));
            sb.Add($"#{i}: {k} entr{(k == 1 ? "y" : "ies")}{(k > 0 ? " (" + string.Join(", ", recs) + ")" : "")}");
        }
        return sb.Count == 0 ? "no such object" : string.Join("; ", sb);
    }

    /// <summary>Before a plain same-world paste: says what is not carried.</summary>
    void NotePlainPaste()
    {
        if (_clipColl != null && _clipCarrierGame && SameWorldClip())
            Log("  The level collision taken with game models is not pasted in the same world (that would rebuild a game model); the copies are plain duplicates.");
    }

    /// <summary>
    /// Paste through the package: models reused or rebuilt in this world (AssetTransplant), the copies placed with
    /// <paramref name="shift"/>; the level collision taken with the copy is the own collision of one copy's model (a
    /// rebuilt copy of it). All of it is built on a copy of the world bundle on a worker thread; the open bundle is
    /// replaced only after that copy was saved (a failure changes nothing). One bundle save = one undo step; the world is
    /// reloaded and the copies selected. Null: paste the ordinary way (the same world, where only the collision could
    /// not be carried).
    /// </summary>
    async Task<List<SceneObject>?> PasteTransplant(Vector3 shift)
    {
        var res = new List<SceneObject>();
        var pkg = _clipPkg!;
        if (!CanAddOrRemove(null, "Paste")) return res;
        bool same = SameWorldClip();
        var missing = _clip.Select(c => AssetIds.DisplayName(c.ModelName)).Distinct().Where(n => !pkg.Models.ContainsKey(n) && !pkg.Models.ContainsKey(n + TransplantPackage.CarrierSuffix)).ToList();
        if (missing.Count > 0 || pkg.Models.Values.Any(m => m.Parts.Count == 0))
        {
            string why = string.Join("; ", pkg.Problems.DefaultIfEmpty("the copied models could not be read: " + string.Join(", ", missing)));
            if (same) { Log($"Paste: the level collision taken with the copy can't be carried ({why}); pasting the objects without it."); return null; }
            Log($"Paste: not done — {why}. Nothing was placed (a wrong model would be worse).");
            return res;
        }
        var caff = _scene!.Caff;
        uint bundle = _scene.Bundle;
        if (AssetTransplant.CannotPlace(caff, pkg) is string cannot)
        {
            if (same) { Log($"Paste: the collision taken with the copy can't be carried in this world ({cannot}); pasting the objects without it."); return null; }
            Log("Paste: not done — " + cannot + "."); return res;
        }
        var clip = _clip.ToList(); int carrier = _clipCarrier; string? failTest = _pasteFailTest; int slowMs = _pasteSlowMs;
        // the paste belongs to this workspace, world and act: nothing may switch them while it builds (PasteRunning)
        var ws = _ws!; var entry = _sceneEntry!; var act = _sceneAct;
        string bgName = _scene.Background.View.Name;
        int template = same && clip[0].Instance >= 0 ? clip[0].Instance : bundle == 0x234cec ? 750 : 0;
        string from = _clipWs != ws.Root ? $"from workspace {Path.GetFileName(_clipWs)}" : clip[0].Bundle != bundle ? $"from world {clip[0].Bundle:x6}" : "with collision";
        int collTris = _clipColl?.T.Count / 3 ?? 0;
        string memPath = Path.Combine(ws.Root, "cache", "transplants.json");   // what earlier pastes rebuilt here (reused)
        var memory = TransplantMemory.Load(memPath);
        var notes = new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int first = -1, count = 0, built = 0, reused = 0;
        // the world bundle on disk: a restore, revert, import or undo writes it (and drops the cache) without touching
        // this CaffFile; the paste must then not write its older copy over it
        string path = ws.Game.ResidentPath(bundle);
        (long, DateTime) Stamp() { var fi = new FileInfo(path); return fi.Exists ? (fi.Length, fi.LastWriteTimeUtc) : (-1, default); }
        var startStamp = Stamp();
        _busy = true; _pasting = true;
        try
        {
            SetProgress("Pasting: copying the world bundle…", 0);
            ulong startHash = AssetTransplant.ContentHash(caff);
            CaffFile work;
            (work, first, count, built, reused) = await Task.Run(() =>
            {
                var w = CaffFile.Read(caff.Write());   // a failure below leaves the open bundle untouched
                double tCopy = sw.Elapsed.TotalSeconds;
                var placed = AssetTransplant.Apply(w, pkg, notes, memory, bundle, (t, f) => SetProgress(t, 0.05 + 0.8 * f));
                double tBuild = sw.Elapsed.TotalSeconds;
                var items = new List<InstanceEditor.NewInstance>();
                for (int k = 0; k < clip.Count; k++)
                {
                    var c = clip[k];
                    var wm = c.Transform; wm.Translation += shift;
                    string key = AssetIds.DisplayName(c.ModelName);
                    if (k == carrier && placed.ContainsKey(key + TransplantPackage.CarrierSuffix)) key += TransplantPackage.CarrierSuffix;   // carries the collision
                    var parts = placed[key];   // one per material for a rebuilt model; the instance names are the copied object's
                    for (int j = 0; j < parts.Count; j++) items.Add(new InstanceEditor.NewInstance(parts[j].ModelId, parts[j].HavokId, wm, parts.Count == 1 ? c.Name : $"{c.Name}_m{j}"));
                }
                SetProgress("Pasting: placing the copies…", 0.86);
                int fi = InstanceEditor.AddModelInstances(w, AssetTransplant.Find(w, AssetIds.DisplayName(bgName)), template, items);
                if (failTest == "build") throw new InvalidOperationException("forced failure after building the copies (script --paste-fail build)");
                if (slowMs > 0) Thread.Sleep(slowMs);   // script --paste-slow
                int ru = placed.Values.Count(l => l.All(p => p.Reused));
                notes.Insert(0, $"time: bundle copy {tCopy:0.0} s, models and textures {tBuild - tCopy:0.0} s, placing {sw.Elapsed.TotalSeconds - tBuild:0.0} s");
                return (w, fi, items.Count, placed.Count - ru, ru);
            });
            SetProgress("Pasting: saving the world bundle…", 0.9);
            string desc = $"pasted {count} object(s) {from}: {built} model(s) added, {reused} reused{(collTris > 0 ? $", {collTris} collision triangles" : "")}";
            // anything that changed this world meanwhile (an edit or a save in memory, a restore / revert / import on disk,
            // another workspace) would be lost by putting the copy in: then nothing is pasted
            if (_ws != ws || AssetTransplant.ContentHash(caff) != startHash || Stamp() != startStamp)
                throw new InvalidOperationException("the world was changed, saved or restored while the paste was being built; nothing was pasted, paste again");
            // the open world, the tag, atmosphere and music editors share this CaffFile and the arrays of its parts: only
            // the parts the paste changed get new bytes (CaffMerge), and everything goes back when the save fails
            var merge = CaffMerge.Into(caff, work);
            try
            {
                if (failTest == "save") throw new IOException("forced failure while saving (script --paste-fail save)");
                // on this thread: the undo history closes a file step as soon as the UI thread is idle (a save on a worker
                // became an unlabelled "File change" step)
                _status.GetCurrentParent()?.Refresh();
                double tSave = sw.Elapsed.TotalSeconds;
                ws.SaveResident(bundle, caff, desc);
                notes.Insert(1, $"time: saving {sw.Elapsed.TotalSeconds - tSave:0.0} s; bundle parts kept {merge.Kept}, changed {merge.Changed}, added {merge.Added}");
            }
            catch { merge.Undo(); throw; }
            memory.Save(memPath);
        }
        catch (Exception e)
        {
            Log($"ERROR: Paste failed — the world was not changed: {e.Message}");
            if (!_scripted) MessageBox.Show(this, $"Paste failed — the world was not changed:\n{e.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return res;
        }
        finally { _pasting = false; _busy = false; SetProgress(null, 0); }
        Log($"Pasted {count} object(s) {from} in {sw.Elapsed.TotalSeconds:0.0} s: {built} model(s) rebuilt here, {reused} reused{(collTris > 0 ? $", {collTris} collision triangles of the level, in the copy of {clip[Math.Max(0, carrier)].Name}" : "")}{(_clipMeshOnly ? " (mesh only: no collision)" : "")} (Ctrl+Z removes them). Reloading world…");
        foreach (var n in pkg.Problems) Log("  Not carried: " + n);
        foreach (var n in pkg.Notes.Concat(notes).Take(40)) Log("  " + n);
        if (pkg.Notes.Count + notes.Count > 40) Log($"  … {pkg.Notes.Count + notes.Count - 40} more");
        // reload and select the copies only in the world they went into (it is still the open one: PasteRunning)
        if (_ws != ws || _sceneEntry != entry || _sceneAct != act) { Log($"  The copies are in {entry.Display}; open it to see them."); return res; }
        await OpenWorld(entry, act);
        if (_sceneEntry != entry || _scene == null) return res;
        res = _scene.Objects.Where(x => x.Instance != null && x.Instance.Index >= first && x.Instance.Index < first + count).ToList();
        if (res.Count > 0) _view.SelectMany(res);
        return res;
    }
}
