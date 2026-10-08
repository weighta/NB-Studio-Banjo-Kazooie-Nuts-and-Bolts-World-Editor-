using System.Numerics;
using NB.Core.World;

namespace NB.Studio;

/// <summary>
/// Characters placed by markers (objparams class objDefId_banjoactor: NPCs such as Thomas, Bottles, Klungo, the Jinjos,
/// Gruntbots and the wind-up critters). The game stands them upright: it drops the tilt and keeps the heading (tested in
/// Xenia, Showdown Town: Thomas tilted 60° about X and Bottles 45° about Z stood upright; Klungo stored as X 180 / Y 0 /
/// Z 180 faced the way the whole rotation points, not by its Y angle alone; Klungo tilted 64° faced as <see cref="Yaw"/>
/// gives; with a 120° pitch Klungo did not appear at all). Props and other objects keep their tilt (a Jig-o-Vend tilted
/// 30° stood tilted).
/// Studio therefore turns these markers about the vertical axis only, like player starts, and shows them upright.
/// </summary>
public static class ActorMarkers
{
    public const string ActorClass = "objDefId_banjoactor";

    public static bool Is(SceneObject? o) => o?.Kind == SceneObjectKind.Marker && o.ObjClass == ActorClass;

    /// <summary>Objects that turn about the vertical axis only: player starts and characters.</summary>
    public static bool YawOnly(SceneObject? o) => SpawnPoints.Is(o) || Is(o);

    /// <summary>The heading of a transform as the game stands it up (radians, 0 = facing +Z, as
    /// Matrix4x4.CreateRotationY): the tilt is taken out the shortest way (the up axis turned straight back to vertical)
    /// and the forward axis read after that. Compared in Xenia with Klungo stored as X -120 / Y 0 / Z -150 (64° tilt): the
    /// game faced him 162° as this gives, not 139° (forward axis laid flat), 180° (side axis) or 0° (the Y angle).</summary>
    public static float Yaw(Matrix4x4 m)
    {
        var f = Vector3.Normalize(new Vector3(m.M31, m.M32, m.M33));
        var u = Vector3.Normalize(new Vector3(m.M21, m.M22, m.M23));
        var axis = Vector3.Cross(u, Vector3.UnitY);
        float sin = axis.Length(), cos = Math.Clamp(u.Y, -1, 1);
        if (sin > 1e-5f)
        {
            // Rodrigues: f turned about the axis u × Y by the angle between u and Y
            var k = axis / sin;
            f = f * cos + Vector3.Cross(k, f) * sin + k * Vector3.Dot(k, f) * (1 - cos);
        }
        if (f.X * f.X + f.Z * f.Z > 1e-8f) return MathF.Atan2(f.X, f.Z);
        return MathF.Atan2(-m.M13, m.M11);   // upside down or degenerate: the side axis
    }

    /// <summary>How far the transform's up axis leans from the vertical, in degrees.</summary>
    public static float Tilt(Matrix4x4 m)
    {
        var u = new Vector3(m.M21, m.M22, m.M23);
        float l = u.Length();
        return l < 1e-6f ? 0 : MathF.Acos(Math.Clamp(u.Y / l, -1, 1)) * 180 / MathF.PI;
    }

    /// <summary>The transform stood upright as the game shows it: same position, scale and heading, no tilt.</summary>
    public static Matrix4x4 Upright(Matrix4x4 m)
    {
        var s = new Vector3(new Vector3(m.M11, m.M12, m.M13).Length(), new Vector3(m.M21, m.M22, m.M23).Length(), new Vector3(m.M31, m.M32, m.M33).Length());
        return Matrix4x4.CreateScale(s) * Matrix4x4.CreateRotationY(Yaw(m)) * Matrix4x4.CreateTranslation(m.Translation);
    }

    /// <summary>Stands a character upright when it is stored with a tilt (a new scene): what the game shows. The stored
    /// record keeps its angles until the marker is edited and saved; the object does not count as modified.</summary>
    public static void StandUpright(SceneObject o)
    {
        if (!Is(o) || Tilt(o.Transform) < 0.05f) return;
        o.Transform = o.OriginalTransform = Upright(o.Transform);
    }

    /// <summary>Properties text for a character marker.</summary>
    public static string Detail(SceneObject o)
    {
        float stored = o.Marker != null ? Tilt(o.Marker.Matrix) : 0;
        return "CHARACTER: the game stands characters upright and only uses which way they face (tested in Xenia), so it turns " +
               "about the vertical axis (Y) only here; X/Z tilts would be ignored by the game." +
               (stored >= 0.05f ? $" Its record is stored tilted by {stored:0.#}°: shown upright, as in the game" +
                   (stored > 90 ? " (in a test with a 120° tilt the character did not appear in the game at all)" : "") +
                   ". Turn or move it and save to store it upright." : "");
    }
}
