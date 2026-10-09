using System.Numerics;
using NB.Core.World;

namespace NB.Studio.Viewport;

/// <summary>What a copy takes along (MainForm.Transplant.cs): the collision triangles of the level that go with the
/// copied objects, drawn in magenta on top until the next copy or another world. The scene is held weakly: after a
/// switch to another world or workspace the old one (with its bundle) must not stay in memory for the highlight.</summary>
public sealed partial class SceneViewport
{
    List<(Vector3, Vector3, Vector3)>? _clipLines;
    WeakReference<WorldScene>? _clipFor;

    public void SetClipCollision(IReadOnlyList<Vector3>? p, IReadOnlyList<int>? t)
    {
        _clipLines = null; _clipFor = null;
        if (p != null && t != null && Scene != null)
        {
            var c = new Vector3(1f, 0.25f, 0.9f);
            _clipLines = new();
            for (int k = 0; k + 2 < t.Count; k += 3)
            {
                var a = p[t[k]]; var b = p[t[k + 1]]; var d = p[t[k + 2]];
                _clipLines.Add((a, b, c)); _clipLines.Add((b, d, c)); _clipLines.Add((d, a, c));
            }
            _clipFor = new WeakReference<WorldScene>(Scene);
        }
        _gl.Invalidate();
    }

    void DrawClipCollision(Matrix4x4 vp)
    {
        if (_clipLines == null || _clipLines.Count == 0) return;
        if (_clipFor == null || !_clipFor.TryGetTarget(out var s) || s != Scene) { if (_clipFor != null && !_clipFor.TryGetTarget(out _)) { _clipLines = null; _clipFor = null; } return; }
        _r.Lines(_clipLines, vp, true, 1.5f);
    }
}
