using System.Numerics;

namespace NB.Core.World;

public sealed partial class SceneObject
{
    /// <summary><see cref="SceneObjectKind.CutsceneKey"/> objects: the cut-scene camera and the frame this key stands for.
    /// The transform is the camera at that frame with +Z turned to where it looks (see <see cref="CutsceneKeys"/>).</summary>
    public CutsceneCamera? Cutscene;
    public int CutsceneFrame;
}

/// <summary>
/// Editable keys of a cut-scene camera path. The game samples the camera every frame (<see cref="CutsceneCamera"/>);
/// NB Studio shows a key every <see cref="Spacing"/> frames (and the last frame). Moving or turning a key moves the path
/// around it with a smooth fade to the neighbouring keys, which stay where they are.
/// </summary>
public static class CutsceneKeys
{
    public const int Spacing = 30;   // one key per second (30 fps)

    /// <summary>A Maya camera looks down -Z: the key's transform turns it half way round Y so +Z is the view direction.</summary>
    static readonly Quaternion Flip = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI);

    public static Matrix4x4 KeyTransform(CutsceneCamera c, int f) =>
        Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(c.Rotations[f] * Flip)) * Matrix4x4.CreateTranslation(c.Positions[f]);

    public static IEnumerable<int> KeyFrames(CutsceneCamera c)
    {
        int n = c.Positions.Length;
        for (int f = 0; f < n - 1; f += Spacing) yield return f;
        yield return n - 1;
    }

    public static string ShortName(CutsceneCamera c) => c.Asset.Replace("aid_cutscene_banjox_", "");

    /// <summary>The key objects of a cut-scene camera (not added to the scene).</summary>
    public static List<SceneObject> Make(CutsceneCamera c, ref int nextId)
    {
        var res = new List<SceneObject>();
        int k = 0;
        foreach (int f in KeyFrames(c))
        {
            var m = KeyTransform(c, f);
            res.Add(new SceneObject
            {
                Id = nextId++, Kind = SceneObjectKind.CutsceneKey, Cutscene = c, CutsceneFrame = f,
                Name = $"{ShortName(c)} key {k++} ({f / 30f:0.0} s)", ModelName = c.Asset,
                Transform = m, OriginalTransform = m,
            });
        }
        return res;
    }

    /// <summary>
    /// Applies the moved keys of one camera to its samples (each key's change fades out over <see cref="Spacing"/> frames)
    /// and makes the keys unmodified again. Returns the number of keys applied.
    /// </summary>
    public static int Apply(CutsceneCamera c, IEnumerable<SceneObject> keys)
    {
        int n = 0;
        foreach (var k in keys.Where(k => k.Cutscene == c && k.Transform != k.OriginalTransform).ToList())
        {
            Matrix4x4.Decompose(k.OriginalTransform, out _, out var q0, out var p0);
            Matrix4x4.Decompose(k.Transform, out _, out var q1, out var p1);
            var turn = Quaternion.Normalize(q1 * Quaternion.Inverse(q0));
            c.MoveKey(k.CutsceneFrame, p1 - p0, MathF.Abs(turn.W) > 0.999999f ? Quaternion.Identity : turn, Spacing);
            n++;
        }
        foreach (var k in keys.Where(k => k.Cutscene == c)) k.Transform = k.OriginalTransform = KeyTransform(c, k.CutsceneFrame);
        return n;
    }

    /// <summary>The path as it will be once the moved keys are applied (for drawing while editing), without changing it.</summary>
    public static Vector3[] Preview(CutsceneCamera c, IEnumerable<SceneObject> keys)
    {
        var pos = (Vector3[])c.Positions.Clone();
        foreach (var k in keys)
        {
            if (k.Cutscene != c || k.Transform == k.OriginalTransform) continue;
            var d = k.Transform.Translation - k.OriginalTransform.Translation;
            for (int f = Math.Max(0, k.CutsceneFrame - Spacing + 1); f < Math.Min(pos.Length, k.CutsceneFrame + Spacing); f++)
                pos[f] += d * 0.5f * (1 + MathF.Cos(MathF.PI * Math.Abs(f - k.CutsceneFrame) / Spacing));
        }
        return pos;
    }
}
