using System.Numerics;
using NB.Core.Formats;
using NB.Core.IO;
using NB.Core.Project;

namespace NB.Core.World;

/// <summary>
/// The camera of an in-game cut-scene (aid_cutscene_* main record: the part of the asset that is not an "animation").
/// The camera is sampled per frame (30 fps), uncompressed:
/// <code>
/// header  +0x00 u16 frames   +0x02 u8 ?   +0x03 u8 entity count n   +0x04 f32 duration (s)
///         +0x08 u32 camera block offset (= 0x10 + 0x30 n)   +0x0C u32 entity list offset (0x10)
/// entities (0x30 each): +0x00 asset id (actor / model / vehicle of the scene), ... +0x28 f32 1.0, +0x2C u8 flags
/// camera  +0x00 u32 offset of the camera's name ("Camera_Shape1", "cameraShape1", "mumbo_camShape" … from Maya)
///         then 12 channels of (u16 sample count, u16 0, f32 duration, u32 offset of f32[count]):
///           0..2  position x, y, z (world space of the level that plays the cut-scene)
///           3..6  rotation quaternion x, y, z, w (sign may flip between frames)
///           7     vertical field of view in degrees (usually one sample: constant)
///           8..11 four more curves (not identified; not changed by the editor)
/// </code>
/// A channel with one sample is constant over the cut-scene. Verified: the camera of Showdown Town's intro in Xenia
/// follows these samples (docs: studio18 round 12 MERGE.md).
/// </summary>
public sealed class CutsceneCamera
{
    public string Asset = "";
    /// <summary>Resident (Bundle) or streamed (Bundle/50 archive of <see cref="Bundle"/>).</summary>
    public uint Bundle;
    public bool Streamed;
    public uint StreamId;
    public string CameraName = "";
    public int Frames;
    public float Duration;
    /// <summary>Offset of each of the 12 channels in the record and its sample count.</summary>
    public (int Offset, int Count)[] Channels = new (int, int)[12];
    public Vector3[] Positions = Array.Empty<Vector3>();
    public Quaternion[] Rotations = Array.Empty<Quaternion>();
    public float Fov;

    const int ChannelCount = 12;

    /// <summary>Reads the camera of a cut-scene record; null when the record has no camera block.</summary>
    public static CutsceneCamera? Parse(byte[] d)
    {
        if (d.Length < 0x10 || d.AsSpan(0, 9).SequenceEqual("animation"u8)) return null;
        int frames = BE.U16(d, 0), n = d[3];
        int camOff = BE.S32(d, 8), entOff = BE.S32(d, 12);
        if (entOff != 0x10 || camOff != 0x10 + 0x30 * n || camOff + 4 + 12 * ChannelCount > d.Length || frames == 0) return null;
        var c = new CutsceneCamera { Frames = frames, Duration = BE.F32(d, 4) };
        int nameOff = BE.S32(d, camOff);
        if (nameOff <= camOff || nameOff >= d.Length) return null;
        int e = nameOff; while (e < d.Length && d[e] != 0) e++;
        c.CameraName = System.Text.Encoding.ASCII.GetString(d, nameOff, e - nameOff);
        for (int k = 0; k < ChannelCount; k++)
        {
            int o = camOff + 4 + 12 * k;
            int cnt = BE.U16(d, o), off = BE.S32(d, o + 8);
            if (cnt == 0 || off < 0 || off + 4 * cnt > d.Length) return null;
            c.Channels[k] = (off, cnt);
        }
        float S(int ch, int f) { var (off, cnt) = c.Channels[ch]; return BE.F32(d, off + 4 * Math.Min(f, cnt - 1)); }
        int nf = c.Channels.Take(7).Max(x => x.Count);
        c.Positions = new Vector3[nf]; c.Rotations = new Quaternion[nf];
        for (int f = 0; f < nf; f++)
        {
            c.Positions[f] = new Vector3(S(0, f), S(1, f), S(2, f));
            var q = new Quaternion(S(3, f), S(4, f), S(5, f), S(6, f));
            c.Rotations[f] = q.LengthSquared() > 1e-8f ? Quaternion.Normalize(q) : Quaternion.Identity;
        }
        c.Fov = S(7, 0);
        c.FovSamples = Enumerable.Range(0, c.Channels[7].Count).Select(f => S(7, f)).ToArray();
        return c;
    }

    /// <summary>Writes positions and rotations back into the record (same sample counts; a constant channel stays one
    /// sample and takes frame 0).</summary>
    public void WriteTo(byte[] d)
    {
        void W(int ch, Func<int, float> v) { var (off, cnt) = Channels[ch]; for (int f = 0; f < cnt; f++) BE.WF32(d, off + 4 * f, v(Math.Min(f, Positions.Length - 1))); }
        W(0, f => Positions[f].X); W(1, f => Positions[f].Y); W(2, f => Positions[f].Z);
        W(3, f => Rotations[f].X); W(4, f => Rotations[f].Y); W(5, f => Rotations[f].Z); W(6, f => Rotations[f].W);
    }

    /// <summary>Vertical field of view (degrees) at a frame (channel 7; often one sample).</summary>
    public float[] FovSamples = Array.Empty<float>();
    public float FovAt(int f) => FovSamples.Length == 0 ? Fov : FovSamples[Math.Clamp(f, 0, FovSamples.Length - 1)];

    /// <summary>The camera's view direction at a frame: the camera looks down its local -Z (Maya convention).</summary>
    public Vector3 Forward(int f) => Vector3.Transform(-Vector3.UnitZ, Rotations[Math.Clamp(f, 0, Rotations.Length - 1)]);

    /// <summary>
    /// Moves the path around frame <paramref name="key"/> by <paramref name="delta"/> (and turns it by
    /// <paramref name="turn"/>), fading out smoothly over <paramref name="radius"/> frames on both sides, so a "key" of the
    /// sampled path can be dragged like a path node.
    /// </summary>
    public void MoveKey(int key, Vector3 delta, Quaternion turn, int radius)
    {
        for (int f = 0; f < Positions.Length; f++)
        {
            float t = Math.Abs(f - key) / (float)Math.Max(1, radius);
            if (t >= 1) continue;
            float w = 0.5f * (1 + MathF.Cos(MathF.PI * t));   // 1 at the key, 0 at the radius
            Positions[f] += delta * w;
            if (turn != Quaternion.Identity) Rotations[f] = Quaternion.Normalize(Quaternion.Slerp(Quaternion.Identity, turn, w) * Rotations[f]);
        }
    }

    /// <summary>Every cut-scene camera of a bundle: its resident assets and the streamed ones (Bundle/50).</summary>
    public static List<CutsceneCamera> InBundle(Workspace ws, uint bundle)
    {
        var res = new List<CutsceneCamera>();
        try
        {
            var caff = ws.LoadResident(bundle);
            res.AddRange(FromCaff(caff, bundle, false, 0));
        }
        catch (Exception) { }
        try
        {
            if (File.Exists(ws.Game.StreamPath(bundle)))
            {
                var arch = ws.LoadStream(bundle);
                foreach (var e in arch.Entries.Where(e => e.Kind == "caff"))
                {
                    CaffFile sc;
                    try { sc = CaffFile.Read(e.Data!); } catch (Exception) { continue; }
                    if (!sc.Symbols.Any(s => s.StartsWith("aid_cutscene_"))) continue;
                    res.AddRange(FromCaff(sc, bundle, true, e.Id));
                }
            }
        }
        catch (Exception) { }
        return res;
    }

    static IEnumerable<CutsceneCamera> FromCaff(CaffFile caff, uint bundle, bool streamed, uint streamId)
    {
        for (int s = 1; s <= caff.Symbols.Count; s++)
        {
            if (!caff.Symbols[s - 1].StartsWith("aid_cutscene_")) continue;
            foreach (var p in caff.PartsOf(s))
            {
                if (caff.SectionOf(p).Name != ".data") continue;
                CutsceneCamera? c;
                try { c = Parse(p.Data); } catch (Exception) { continue; }
                if (c == null) continue;
                c.Asset = AssetIds.DisplayName(caff.Symbols[s - 1]); c.Bundle = bundle; c.Streamed = streamed; c.StreamId = streamId;
                yield return c;
            }
        }
    }

    /// <summary>Saves an edited camera into the workspace (the resident bundle or the stream archive it came from).</summary>
    public void Save(Workspace ws, string description)
    {
        if (!Streamed)
        {
            var caff = ws.LoadResident(Bundle);
            if (!Patch(caff)) throw new InvalidDataException($"{Asset}: camera record not found in bundle {Bundle:x6}");
            ws.SaveResident(Bundle, caff, description);
            return;
        }
        var arch = ws.LoadStream(Bundle);
        var e = arch.Entries.FirstOrDefault(x => x.Id == StreamId && x.Kind == "caff") ?? throw new InvalidDataException($"{Asset}: stream entry {StreamId:X8} not found");
        var sc = CaffFile.Read(e.Data!);
        if (!Patch(sc)) throw new InvalidDataException($"{Asset}: camera record not found in the stream entry");
        e.Data = sc.Write();
        ws.SaveStream(Bundle, arch, description);
    }

    bool Patch(CaffFile caff)
    {
        for (int s = 1; s <= caff.Symbols.Count; s++)
        {
            if (AssetIds.DisplayName(caff.Symbols[s - 1]) != Asset) continue;
            foreach (var p in caff.PartsOf(s))
                if (caff.SectionOf(p).Name == ".data" && Parse(p.Data) is { } c && c.CameraName == CameraName && c.Frames == Frames)
                {
                    WriteTo(p.Data);
                    return true;
                }
        }
        return false;
    }
}
