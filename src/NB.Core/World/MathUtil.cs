using System.Numerics;

namespace NB.Core.World;

public static class MathUtil
{
    /// <summary>Euler angles (radians) for a rotation built as RotX * RotY * RotZ (row-vector convention).</summary>
    public static Vector3 EulerXYZ(Matrix4x4 m)
    {
        float y = MathF.Asin(Math.Clamp(-m.M13, -1, 1));
        float x, z;
        if (MathF.Abs(m.M13) < 0.9999f) { x = MathF.Atan2(m.M23, m.M33); z = MathF.Atan2(m.M12, m.M11); }
        else { x = MathF.Atan2(-m.M32, m.M22); z = 0; }
        return new Vector3(x, y, z);
    }
}
