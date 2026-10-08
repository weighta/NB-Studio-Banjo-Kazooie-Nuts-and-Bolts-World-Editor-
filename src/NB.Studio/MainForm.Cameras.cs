using System.Numerics;
using NB.Core.World;

namespace NB.Studio;

/// <summary>
/// Cameras (see <see cref="CameraPoints"/>): the object menu's Look Through Camera / Set Camera from 3D View, a warp pad's
/// camera, the "Cameras" group of the scene tree, and moving a warp pad (or another object with a camera) takes its camera
/// along — asked once, in the same undo step.
/// </summary>
public sealed partial class MainForm
{
    /// <summary>Cameras follow the objects that use them: null = ask, else the answer kept for this session.</summary>
    bool? _camFollow;

    void AddCameraMenuItems(SceneObject o)
    {
        if (CameraPoints.Is(o))
        {
            _objMenu.Items.Add(new ToolStripMenuItem("Look Through Camera", null, (_, _) => { _view.LookThrough(o); Log($"3D view: looking through {CameraPoints.Label(o)} ({o.Name})."); })
                { ToolTipText = "Puts the 3D view where this camera is, looking the way it looks (Rendered mode shows roughly what the game shows)." });
            _objMenu.Items.Add(new ToolStripMenuItem("Set Camera from 3D View", null, (_, _) => SetCameraFromView(o))
                { ToolTipText = "Moves this camera to the 3D view's position and direction (fly the view to the shot you want first). Undoable; World > Save writes it." });
        }
        foreach (var cam in CameraPoints.CamerasOf(o))
        {
            var c = cam;
            _objMenu.Items.Add(new ToolStripMenuItem($"Look Through Its Camera ({CameraPoints.Label(c)})", null, (_, _) => { _view.LookThrough(c); _view.Select(c); }));
            _objMenu.Items.Add(new ToolStripMenuItem("Select Its Camera", null, (_, _) => _view.Select(c)));
        }
    }

    void SetCameraFromView(SceneObject cam)
    {
        var before = cam.Transform;
        cam.Transform = _view.CameraFromView();
        PushUndo(cam, before, cam.Transform);
        _view.Select(cam); _view.Refresh3D();
        Log($"{CameraPoints.Label(cam) ?? cam.Name}: set from the 3D view (World > Save writes it).");
    }

    /// <summary>
    /// Objects just moved or turned that use cameras which were not moved with them (a warp pad and its WARP TO view): asks
    /// (once per session when "don't ask again" is ticked) and moves the cameras by the same change. Returns the moved
    /// cameras with their transforms before and after, for the same undo step.
    /// </summary>
    List<(SceneObject Obj, Matrix4x4 Before, Matrix4x4 After)> FollowCameras(IEnumerable<(SceneObject Obj, Matrix4x4 Before)> moved)
    {
        var res = new List<(SceneObject, Matrix4x4, Matrix4x4)>();
        var list = moved.ToList();
        var movedSet = list.Select(x => x.Obj).ToHashSet();
        var todo = list.Where(x => x.Obj.Transform != x.Before)
            .SelectMany(x => CameraPoints.CamerasOf(x.Obj).Where(c => !movedSet.Contains(c)).Select(c => (Owner: x.Obj, x.Before, Cam: c)))
            .GroupBy(t => t.Cam).Select(g => g.First()).ToList();
        if (todo.Count == 0) return res;
        bool follow = _camFollow ?? (_scripted || AskFollow(todo.Select(t => t.Owner).Distinct().ToList(), todo.Select(t => t.Cam).ToList()));
        if (!follow) return res;
        foreach (var (owner, before, cam) in todo)
        {
            if (!Matrix4x4.Invert(before, out var inv)) continue;
            var d = inv * owner.Transform;   // the owner's change in world space
            var camBefore = cam.Transform;
            cam.Transform = camBefore * d;
            res.Add((cam, camBefore, cam.Transform));
        }
        Log($"Moved {res.Count} camera(s) with {string.Join(", ", todo.Select(t => t.Owner.Name).Distinct())}: {string.Join(", ", res.Select(r => CameraPoints.Label(r.Item1)))}.");
        return res;
    }

    bool AskFollow(List<SceneObject> owners, List<SceneObject> cams)
    {
        var page = new TaskDialogPage
        {
            Caption = "Move its camera too?",
            Heading = owners.Count == 1 ? $"{owners[0].Name} has a camera" : $"{owners.Count} moved objects have cameras",
            Text = string.Join("\n", cams.Select(c => "• " + CameraPoints.Label(c))) +
                   "\n\nThe game shows the object through this camera (a warp pad's WARP TO menu). Move the camera with it, keeping the same offset and turn?",
            Buttons = { TaskDialogButton.Yes, TaskDialogButton.No },
            Verification = new TaskDialogVerificationCheckBox("Don't ask again (until NB Studio is closed)"),
            Icon = TaskDialogIcon.Information,
        };
        bool yes = TaskDialog.ShowDialog(this, page) == TaskDialogButton.Yes;
        if (page.Verification.Checked) _camFollow = yes;
        return yes;
    }

    void AddCameraNodes(string q)
    {
        var cams = _scene!.Objects.Where(o => CameraPoints.Is(o) && o.Kind != SceneObjectKind.CutsceneKey && (q == "" || NodeText(o).ToLowerInvariant().Contains(q))).OrderBy(o => CameraPoints.Label(o)).ToList();
        var cuts = CutsceneNodes(q);   // MainForm.Cutscenes.cs
        if (cams.Count == 0 && cuts == null) return;
        var cn = _tree.Nodes.Add($"Cameras ({cams.Count}{(cuts != null ? $" + {_cutCams.Count} cut-scenes" : "")})"); cn.Checked = true;
        if (cuts != null) cn.Nodes.Add(cuts);
        cn.ToolTipText = "Marker points the game uses as cameras: warp pads' WARP TO views, bolt-head and info-point close-ups. Cyan camera with its view in the 3D view; right-click: Look Through Camera / Set Camera from 3D View.";
        foreach (var o in cams) cn.Nodes.Add(new TreeNode(NodeText(o)) { Tag = o, Checked = o.Visible, ToolTipText = CameraPoints.Detail(o), ForeColor = Color.FromArgb(30, 120, 170) });
    }

    string CameraListText() => _scene == null ? "no world" : string.Join("; ", _scene.Objects.Where(CameraPoints.Is).Select(o =>
        $"{CameraPoints.Label(o)} = {o.Name} at {o.Transform.Translation} pitch {-CameraPoints.View(o.Transform).Pitch * 180 / MathF.PI:0.#} yaw {CameraPoints.View(o.Transform).Yaw * 180 / MathF.PI:0.#}" +
        (CameraPoints.Of(o)?.Owner is { } ow ? $" (used by {ow.Name})" : "")));
}
