using System.Numerics;
using System.Text;
using NB.Core.IO;

namespace NB.Core.Models;

/// <summary>One joint of a character skeleton ("pose" object).</summary>
public sealed record Joint(int Index, string Name, int Parent, int FirstChild, int NextSibling, int Mirror,
    Vector3 LocalTranslation, Vector3 BindTranslation, Quaternion Rotation);

/// <summary>
/// Character skeleton stored as a "pose" object inside a model's rendergraph (chunk 30) and at the start of every
/// animation: tag "pose\0\0\0\0", version text (e.g. 19.12.06.0036), +0x18 u32 joint count, +0x20 offset of the name
/// table (u32 count, u32 offset of 12-byte records {u32 name offset, 0, u32 ordinal}, 0, offset of a hash table),
/// +0x2C offset of the joint array: 52 bytes per joint = f32[3] translation relative to the parent, f32[3] bind-pose
/// model-space translation, f32[4] rotation quaternion (x, y, z, w), u16 parent, first child, next sibling, index,
/// mirror joint (LF_* ↔ RT_*), pad (0xFFFF = none). Offsets are plain .data offsets.
/// Verified on Banjo (175 joints = the 175 animation tracks), Mumbo 127, Grunty 119, Bottles 77, Jinjo 66.
/// </summary>
public static class Skeleton
{
    const int JointSize = 0x34;

    public static List<Joint>? Parse(byte[] d)
    {
        int at = FindPose(d);
        if (at < 0) return null;
        int n = (int)BE.U32(d, at + 0x18);
        int nameHdr = (int)BE.U32(d, at + 0x20), joints = (int)BE.U32(d, at + 0x2C);
        if (n <= 0 || n > 4096 || joints <= 0 || joints + n * JointSize > d.Length) return null;
        int nameCount = nameHdr > 0 && nameHdr + 8 <= d.Length ? (int)BE.U32(d, nameHdr) : 0;
        int nameRecs = nameCount > 0 ? (int)BE.U32(d, nameHdr + 4) : 0;
        var list = new List<Joint>(n);
        for (int k = 0; k < n; k++)
        {
            int r = joints + k * JointSize;
            Vector3 V(int o) => new(BE.F32(d, o), BE.F32(d, o + 4), BE.F32(d, o + 8));
            var q = new Quaternion(BE.F32(d, r + 24), BE.F32(d, r + 28), BE.F32(d, r + 32), BE.F32(d, r + 36));
            int U(int o) { int v = BE.U16(d, r + o); return v == 0xFFFF ? -1 : v; }
            string name = $"joint{k}";
            if (k < nameCount && nameRecs + 12 * k + 4 <= d.Length)
            {
                int no = (int)BE.U32(d, nameRecs + 12 * k);
                if (no > 0 && no < d.Length) name = CString(d, no);
            }
            list.Add(new Joint(k, name, U(40), U(42), U(44), U(48), V(r), V(r + 12), q));
        }
        return list;
    }

    static int FindPose(byte[] d)
    {
        var tag = Encoding.ASCII.GetBytes("pose\0\0\0\0");
        for (int i = d.AsSpan().IndexOf(tag); i >= 0;)
        {
            if (i + 0x30 <= d.Length && d[i + 8] == 0 && d[i + 10] >= (byte)'0' && d[i + 10] <= (byte)'9') return i;
            int next = d.AsSpan(i + 1).IndexOf(tag);
            i = next < 0 ? -1 : i + 1 + next;
        }
        return -1;
    }

    static string CString(byte[] d, int o)
    {
        int e = o; while (e < d.Length && d[e] != 0) e++;
        return Encoding.Latin1.GetString(d, o, e - o);
    }
}
