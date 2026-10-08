using NB.Core.World;

namespace NB.Studio;

/// <summary>
/// Cut-scene cameras (<see cref="CutsceneCamera"/>): read in the background when a world opens (its resident and streamed
/// cut-scenes), drawn as violet paths, listed under Cameras › Cut-scene cameras. Choosing one there adds its keys (one per
/// second) as camera objects: select, move, turn, Look Through Camera / Set Camera from 3D View, undo; World > Save All
/// writes the edited cut-scenes (resident bundle or stream archive).
/// </summary>
public sealed partial class MainForm
{
    List<CutsceneCamera> _cutCams = new();
    Task? _cutLoad;

    /// <summary>The bundles whose cut-scenes play in this world: the world and act bundles of its load set (not the common
    /// bundle, the garage or other shared ones, whose cut-scenes belong elsewhere).</summary>
    static IEnumerable<uint> CutsceneBundles(WorldScene scene) =>
        new[] { scene.Bundle & 0xFFFFFF }.Concat(scene.LoadSet.Where(b => b != 0x685374 && b != 0x20abf9 && b != 0x4bf033)).Distinct();

    void LoadCutscenes(WorldScene scene)
    {
        _cutCams = new(); _view.SetCutscenePaths(_cutCams); _view.ActiveCutscene = null;
        var ws = _ws; if (ws == null) return;
        var bundles = CutsceneBundles(scene).ToList();
        _cutLoad = Task.Run(() => bundles.SelectMany(b => CutsceneCamera.InBundle(ws, b)).ToList()).ContinueWith(t =>
        {
            if (t.IsFaulted || IsDisposed) return;
            Invoke(() =>
            {
                if (_scene != scene) return;
                _cutCams = t.Result;
                _view.SetCutscenePaths(_cutCams);
                if (_cutCams.Count > 0)
                {
                    FillTree();
                    Log($"  Cut-scene cameras: {_cutCams.Count} ({_cutCams.Count(c => c.Streamed)} streamed) in {string.Join(", ", _cutCams.Select(c => c.Bundle).Distinct().Select(b => b.ToString("x6")))} — violet paths; Cameras › Cut-scene cameras opens one for editing.");
                }
            });
        });
    }

    /// <summary>Adds the keys of a cut-scene camera to the scene (once) and selects its first key.</summary>
    void OpenCutscene(CutsceneCamera c)
    {
        if (_scene == null) return;
        var keys = _scene.Objects.Where(o => o.Cutscene == c).ToList();
        if (keys.Count == 0)
        {
            int id = _scene.Objects.Count == 0 ? 0 : _scene.Objects.Max(o => o.Id) + 1;
            keys = CutsceneKeys.Make(c, ref id);
            foreach (var k in keys) _view.PrepareCameraObject(k);
            _scene.Objects.AddRange(keys);
            Log($"{c.Asset}: {keys.Count} keys (one per second) of camera {c.CameraName}, {c.Frames} frames ({c.Duration:0.0} s), field of view {c.Fov:0.#}°.");
            FillTree();
        }
        _view.ActiveCutscene = c;
        _view.Select(keys[0], focus: true);
        _view.Refresh3D();
    }

    /// <summary>World > Save: the cut-scene cameras with moved keys. Returns log lines.</summary>
    List<string> SaveCutscenes()
    {
        var res = new List<string>();
        if (_scene == null || _ws == null) return res;
        foreach (var g in _scene.Objects.Where(o => o.Cutscene != null).GroupBy(o => o.Cutscene!).ToList())
        {
            if (!g.Any(k => k.Dirty)) continue;
            int n = CutsceneKeys.Apply(g.Key, g);
            g.Key.Save(_ws, $"cut-scene camera {g.Key.Asset}: {n} key(s) moved");
            res.Add($"Saved the camera of {g.Key.Asset} ({n} key(s) moved) → {(g.Key.Streamed ? $"stream archive Bundle/50/{g.Key.Bundle:x6}" : $"bundle {g.Key.Bundle:x6}")}.");
        }
        if (res.Count > 0) _view.SetCutscenePaths(_cutCams);
        return res;
    }

    TreeNode? CutsceneNodes(string q)
    {
        var list = _cutCams.Where(c => q == "" || c.Asset.ToLowerInvariant().Contains(q)).OrderBy(c => c.Asset).ToList();
        if (list.Count == 0) return null;
        var cn = new TreeNode($"Cut-scene cameras ({list.Count})") { Checked = true, ToolTipText = "The camera paths of this world's cut-scenes (sampled every frame). Click one to show its keys and edit it." };
        foreach (var c in list)
        {
            var n = cn.Nodes.Add($"{CutsceneKeys.ShortName(c)} ({c.Duration:0.0} s{(c.Streamed ? ", streamed" : "")})");
            n.Tag = c; n.Checked = true; n.ForeColor = Color.FromArgb(120, 70, 170);
            n.ToolTipText = $"{c.Asset}: camera {c.CameraName}, {c.Frames} frames, field of view {c.Fov:0.#}°";
            foreach (var k in _scene!.Objects.Where(o => o.Cutscene == c)) n.Nodes.Add(new TreeNode(NodeText(k)) { Tag = k, Checked = k.Visible, ForeColor = Color.FromArgb(120, 70, 170) });
        }
        return cn;
    }
}
