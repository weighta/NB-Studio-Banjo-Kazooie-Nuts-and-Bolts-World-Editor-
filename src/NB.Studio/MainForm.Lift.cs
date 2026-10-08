using System.Numerics;
using NB.Core.World;
using NB.Studio.Viewport;

namespace NB.Studio;

/// <summary>
/// Ctrl+X "lifts" the selection (actors, other markers, scenery, cameras, cut-scene keys — anything but the terrain) and
/// Ctrl+V puts it down where the mouse points: a move across the map in two keys, one undo step. Nothing is deleted:
/// the lifted objects are only hidden in the 3D view until they are put down, so
/// <list type="bullet">
/// <item>Esc, or lifting something else first, puts them back where they were (they never moved);</item>
/// <item>a lift that is never put down changes nothing (World > Save writes nothing for it);</item>
/// <item>Ctrl+Z after the put-down moves them back.</item>
/// </list>
/// Ctrl+C / Ctrl+V still paste copies of scenery (MainForm.cs). NB Studio 1.19 and older cut scenery as copy + delete and
/// could not cut markers at all.
/// </summary>
public sealed partial class MainForm
{
    readonly List<SceneObject> _lifted = new();

    bool Liftable(SceneObject o) => o.Kind != SceneObjectKind.Terrain && o.Kind != SceneObjectKind.Water;

    /// <summary>Ctrl+X: lifts the selected objects (an earlier lift that was not put down is put back first).</summary>
    void LiftSelection()
    {
        var sel = _view.SelectedObjects.Where(Liftable).ToList();
        if (sel.Count == 0) { Log("Cut: select an object first (the terrain and water can't be lifted)."); return; }
        if (_lifted.Count > 0) PutBackLifted("lifting something else");
        // a warp pad takes its camera with it (CameraPoints): lifted together, so they move together
        foreach (var c in sel.SelectMany(CameraPoints.CamerasOf).Distinct().ToList()) if (!sel.Contains(c)) sel.Add(c);
        _lifted.AddRange(sel);
        foreach (var o in _lifted) o.Visible = false;
        _view.Select(null); _view.Refresh3D();
        string names = sel.Count == 1 ? sel[0].Name : $"{sel.Count} objects ({string.Join(", ", sel.Take(3).Select(o => o.Name))}{(sel.Count > 3 ? ", …" : "")})";
        _status.Text = $"Lifted: {names} — Ctrl+V puts it down where the mouse points; Esc puts it back.";
        Log($"Cut (lifted) {names}: it stays where it is until you put it down with Ctrl+V (where the mouse points in the 3D view). Esc, or cutting something else first, leaves it where it was. Nothing is deleted.");
    }

    /// <summary>Esc / another lift: the lifted objects are shown again where they were (they were never moved).</summary>
    void PutBackLifted(string why)
    {
        if (_lifted.Count == 0) return;
        foreach (var o in _lifted) o.Visible = true;
        Log($"Lift cancelled ({why}): {(_lifted.Count == 1 ? _lifted[0].Name : $"{_lifted.Count} objects")} stays where it was.");
        _lifted.Clear();
        _status.Text = "";
        _view.Refresh3D();
    }

    /// <summary>
    /// Ctrl+V with something lifted: the first lifted object goes onto the surface under the mouse (or <paramref name="at"/>,
    /// a 3D-view pixel), the others keep their places relative to it; rotation kept. One undo step. Returns the first.
    /// </summary>
    SceneObject? PlaceLifted(Point? at = null)
    {
        if (_lifted.Count == 0 || _scene == null) return null;
        foreach (var o in _lifted) o.Visible = true;
        var first = _lifted.FirstOrDefault(o => !CameraPoints.Is(o) || _lifted.All(CameraPoints.Is)) ?? _lifted[0];
        foreach (var o in _lifted) o.Visible = false;   // the ray must not hit the lifted objects themselves
        float size = (first.BoundsMax - first.BoundsMin).Length();
        var (p, hit, mouse) = _view.SurfaceAt(at, Math.Clamp(size * 2, 10, 200));
        foreach (var o in _lifted) o.Visible = true;
        var target = SceneViewport.PlaceOn(first.Transform, first.BoundsMin, first.BoundsMax, p);
        var shift = target.Translation - first.Transform.Translation;
        var steps = new List<(SceneObject, Matrix4x4, Matrix4x4)>();
        foreach (var o in _lifted)
        {
            var before = o.Transform; var m = before; m.Translation += shift; o.Transform = m;
            steps.Add((o, before, o.Transform));
        }
        _history.PushTransforms(steps);
        string names = _lifted.Count == 1 ? first.Name : $"{_lifted.Count} objects";
        Log($"Put {names} down {(hit != null ? $"on {hit.Name}" : "in front of the camera (nothing under the mouse)")} at {Fmt(first.Transform.Translation)} (moved {shift.Length():0.#} units; Ctrl+Z moves it back; World > Save writes it).");
        var moved = _lifted.ToList();
        _lifted.Clear();
        _status.Text = $"Put down: {names} — not yet saved (Ctrl+S).";
        _view.SelectMany(moved, first);
        foreach (var o in moved) RefreshNode(o);
        _view.Refresh3D(); UpdateTitle();
        return first;
    }
}
