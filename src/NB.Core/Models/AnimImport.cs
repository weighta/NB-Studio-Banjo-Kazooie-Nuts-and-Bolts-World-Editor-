using System.Numerics;

namespace NB.Core.Models;

/// <summary>
/// Converts FBX animation takes back to aid_anim keys — the inverse of <see cref="FbxExporter"/>'s WriteAnimation:
/// FBX and game space share axes (right-handed, Y up), so quaternions and translations map unchanged (rotations as XYZ
/// Euler degrees, translations as bind local translation + key offset).
/// </summary>
public static class AnimImport
{
    const float Deg = MathF.PI / 180f;

    /// <summary>FBX Euler angles (degrees) → quaternion. FBX order "XYZ" (0) means X is applied first: R = Rz·Ry·Rx.</summary>
    public static Quaternion EulerToQuaternion(Vector3 deg, int order = 0)
    {
        var qx = Quaternion.CreateFromAxisAngle(Vector3.UnitX, deg.X * Deg);
        var qy = Quaternion.CreateFromAxisAngle(Vector3.UnitY, deg.Y * Deg);
        var qz = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, deg.Z * Deg);
        return order switch
        {
            1 => qy * qz * qx,   // XZY
            2 => qx * qz * qy,   // YZX
            3 => qz * qx * qy,   // YXZ
            4 => qy * qx * qz,   // ZXY
            5 => qx * qy * qz,   // ZYX
            _ => qz * qy * qx,   // XYZ
        };
    }

    /// <summary>Rotation by X, then Y, then Z degrees about the game's own axes (for test edits such as anim-rotate).</summary>
    public static Quaternion GameEulerDegrees(Vector3 deg) => EulerToQuaternion(deg);

    public static Quaternion GameFromFbx(Quaternion f) => f;
    public static Vector3 GameFromFbx(Vector3 v) => v;

    /// <summary>
    /// Samples every joint of <paramref name="skeleton"/> (matched by model name) at the anim's key frames and returns
    /// new keys for <paramref name="anim"/>. Joints missing from the file, or without animation curves, keep their
    /// original keys. Frame f is sampled at LocalStart + f/30 s.
    /// </summary>
    public static AnimKey[][] FromFbx(FbxAnimation fa, AnimAsset anim, IReadOnlyList<Joint> skeleton, List<string>? notes = null)
    {
        var keys = anim.Keys.Select(r => (AnimKey[])r.Clone()).ToArray();
        int matched = 0; var missing = new List<string>(); var still = new List<string>();
        int n = Math.Min(anim.Tracks, skeleton.Count);
        var jointNames = skeleton.Select(x => x.Name).ToHashSet();
        for (int t = 0; t < n; t++)
        {
            var j = skeleton[t];
            if (!fa.Models.TryGetValue(j.Name, out var m)) { missing.Add(j.Name); continue; }
            if (!m.Animated) { still.Add(j.Name); continue; }
            matched++;
            var pre = EulerToQuaternion(m.PreRotation);
            var postInv = Quaternion.Inverse(EulerToQuaternion(m.PostRotation));
            // non-joint FBX ancestors (e.g. Blender's "Armature" node, scale 100 with UnitScaleFactor 1): their uniform
            // scale applies to every joint's translation; their rotation/translation between this joint and its nearest
            // joint ancestor are folded into this joint (root joints)
            var (qA, sA, tA) = (Quaternion.Identity, 1f, Vector3.Zero);
            bool direct = true;
            for (var p = m.Parent; p != null && fa.Models.TryGetValue(p, out var pm); p = pm.Parent)
            {
                if (jointNames.Contains(p)) { direct = false; continue; }
                sA *= pm.S.X;
                if (!direct) continue;
                var ql = EulerToQuaternion(pm.PreRotation) * EulerToQuaternion(pm.R, pm.RotationOrder) * Quaternion.Inverse(EulerToQuaternion(pm.PostRotation));
                (qA, tA) = (ql * qA, pm.T + Vector3.Transform(tA * pm.S.X, ql));   // parent ∘ accumulated
            }
            for (int k = 0; k < anim.KeyFrames.Length; k++)
            {
                long time = fa.LocalStart + (long)Math.Round(anim.KeyFrames[k] * (double)FbxReader.KTimePerSecond / AnimAsset.Fps);
                var qf = Quaternion.Normalize(pre * EulerToQuaternion(m.EulerDegrees(time), m.RotationOrder) * postInv);
                var tf = m.Translation(time) * sA;
                if (direct && (qA != Quaternion.Identity || tA != Vector3.Zero))
                {
                    qf = Quaternion.Normalize(qA * qf);
                    tf = tA + Vector3.Transform(tf, qA);
                }
                var tr = GameFromFbx(tf) - j.LocalTranslation;
                var orig = keys[t][k];
                // components without an FBX curve keep the original key (our exporter omits Lcl Scaling unless some
                // value differs from 1 by more than 1e-4, so tiny stored scale offsets would otherwise be lost)
                var sc = m.Scaling(time);
                sc = new Vector3(m.SCurves[0] != null ? sc.X : orig.Scale.X, m.SCurves[1] != null ? sc.Y : orig.Scale.Y, m.SCurves[2] != null ? sc.Z : orig.Scale.Z);
                if (m.TCurves.All(c => c == null)) tr = orig.Translation;
                keys[t][k] = new AnimKey(m.RCurves.All(c => c == null) ? orig.Rotation : GameFromFbx(qf), tr, sc);
            }
        }
        if (notes != null)
        {
            notes.Add($"take '{fa.Name}' (stacks: {string.Join(", ", fa.Stacks)}), start {fa.LocalStart / (double)FbxReader.KTimePerSecond * AnimAsset.Fps:0.##} f, " +
                      $"stop {fa.LocalStop / (double)FbxReader.KTimePerSecond * AnimAsset.Fps:0.##} f; anim has {anim.Frames} frames, {anim.KeyFrames.Length} keys");
            notes.Add($"{matched}/{n} joints animated in the FBX");
            if (missing.Count > 0) notes.Add($"not in the FBX (original keys kept): {string.Join(", ", missing.Take(12))}{(missing.Count > 12 ? $" … ({missing.Count})" : "")}");
            if (still.Count > 0) notes.Add($"no curves (original keys kept): {string.Join(", ", still.Take(12))}{(still.Count > 12 ? $" … ({still.Count})" : "")}");
        }
        return keys;
    }

    /// <summary>The character model whose skeleton drives an anim: the longest aid_model_X with aid_anim_X_… .</summary>
    public static string? GuessModel(string animName, IEnumerable<string> modelNames)
    {
        const string ap = "aid_anim_", mp = "aid_model_";
        if (!animName.StartsWith(ap)) return null;
        string rest = animName[ap.Length..];
        return modelNames.Where(m => m.StartsWith(mp) && rest.StartsWith(m[mp.Length..] + "_"))
            .OrderByDescending(m => m.Length).FirstOrDefault();
    }
}
