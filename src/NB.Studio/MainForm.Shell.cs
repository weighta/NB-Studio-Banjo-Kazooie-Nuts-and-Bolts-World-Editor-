using System.Numerics;
using NB.Core.World;

namespace NB.Studio;

/// <summary>
/// The world's outer collision shell. Every world's terrain collision ends in a closed box around the level (Nutty Acres:
/// ±982 units, 12 triangles in aid_havok_…_nuttyacres_default). The game needs it: with those triangles deleted, looking
/// towards where they were shows a black sky with stars instead of the sky walls (reproduced in Xenia, Nutty Acres Act 6,
/// work/agent_src/studio19/shots/r5/sky). Deleting collision that lies on the faces of that box asks first.
/// </summary>
public sealed partial class MainForm
{
    /// <summary>True when the delete may go ahead (no shell triangles, or the user said yes).</summary>
    bool ConfirmShellDelete(Dictionary<string, HashSet<int>> sel)
    {
        int shell = ShellTriangles(sel);
        if (shell == 0) return true;
        string msg = $"{shell} of the selected collision triangles are part of the world's outer collision box (the shell around the whole level).\n\n" +
                     "The game needs it: without it the sky walls are cut off in that direction and you see a black sky with stars " +
                     "(\"a black plane\"), also near Banjo. Delete them anyway?";
        Log($"Delete collision: {shell} triangle(s) of the world's outer collision box are selected" + (_scripted ? " (script: deleted)" : ""));
        return _scripted || MessageBox.Show(this, msg, "Delete collision", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    /// <summary>Selected triangles lying flat on a face of the terrain collision's bounding box (all three corners within
    /// one unit of the same box face).</summary>
    int ShellTriangles(Dictionary<string, HashSet<int>> sel)
    {
        if (_scene == null) return 0;
        // the bounds of the terrain collision (the background model's own havok assets)
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var (o, asset, toWorld) in _view.CollisionInstances())
        {
            if (o.Kind != SceneObjectKind.Terrain || Soup(asset) is not { } s) continue;
            for (int i = 0; i < s.P.Count; i++) { var p = Vector3.Transform(s.P[i], toWorld); mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
        }
        if (mn.X > mx.X) return 0;
        int n = 0;
        foreach (var (asset, tris) in sel)
        {
            if (Soup(asset) is not { } s || !_collAt.TryGetValue(asset, out var at) || at.Item1.Kind != SceneObjectKind.Terrain) continue;
            foreach (int t in tris)
            {
                if (3 * t + 2 >= s.T.Count) continue;
                var a = Vector3.Transform(s.P[s.T[3 * t]], at.Item2); var b = Vector3.Transform(s.P[s.T[3 * t + 1]], at.Item2); var c = Vector3.Transform(s.P[s.T[3 * t + 2]], at.Item2);
                bool On(Func<Vector3, float> f, float v) => MathF.Abs(f(a) - v) < 1 && MathF.Abs(f(b) - v) < 1 && MathF.Abs(f(c) - v) < 1;
                if (On(p => p.X, mn.X) || On(p => p.X, mx.X) || On(p => p.Y, mn.Y) || On(p => p.Y, mx.Y) || On(p => p.Z, mn.Z) || On(p => p.Z, mx.Z)) n++;
            }
        }
        return n;
    }
}
