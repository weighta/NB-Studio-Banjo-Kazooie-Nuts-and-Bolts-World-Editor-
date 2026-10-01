using System.Numerics;
using System.Text;
using NB.Core.Formats;
using NB.Core.IO;

namespace NB.Core.Models;

/// <summary>One stored key of one track: rotation, translation offset (added to the bind-pose local translation), scale.</summary>
public readonly record struct AnimKey(Quaternion Rotation, Vector3 Translation, Vector3 Scale);

/// <summary>Result of <see cref="AnimAsset.Rebuild"/>: the new .data and how the bytes after the rewritten region moved.</summary>
public sealed class AnimRebuild
{
    public byte[] Data = Array.Empty<byte>();
    /// <summary>Rewritten region [RegionStart, OldRegionEnd) of the original (flags, bases, widths, key bitstreams).</summary>
    public int RegionStart, OldRegionEnd, Delta;
    public bool Wide, WideChanged;
    public int OldStride, NewStride;
    /// <summary>Channels whose quantised values differ from the original / channels whose original encoding was kept.</summary>
    public int ChannelsChanged, ChannelsReused;
    /// <summary>Offsets (in the new data) of pointer fields that must be relocated (descriptor bases/widths).</summary>
    public List<int> RequiredPointers = new();
    /// <summary>(track, channel) of every channel whose quantised values changed (channel 0-2 rotation xyz, 3-5 translation, 6-8 scale).</summary>
    public List<(int Track, int Channel)> Changed = new();
    /// <summary>Channels (0-8) whose quantisation scale had to be coarsened to fit the new values.</summary>
    public List<int> ScalesChanged = new();
    public int MapOffset(int oldOffset) => oldOffset >= OldRegionEnd ? oldOffset + Delta : oldOffset;
}

/// <summary>
/// aid_anim_* decoder and encoder (docs/FORMATS.md §16, docs/research/16_anim_codec.md; mirrors the game's decoder 0x829A1970).
/// Header: "animation" + version, +0x1C f32 duration, +0x20 u16 tracks, +0x22 u16 frames (30 fps), +0x24 codec block
/// (QUAT_BITSTREAM: 9 channels per track = rotation xyz, translation xyz, scale xyz), +0x5C f32 keys per frame.
/// Animated channel value = (bits + base) × scale, bits read LSB-first from big-endian 32-bit words; w = √(1−x²−y²−z²).
/// </summary>
public sealed class AnimAsset
{
    public string Version = "";
    public float Duration;
    public int Tracks, Frames;
    public float KeyRate;
    public int[] KeyFrames = Array.Empty<int>();
    /// <summary>[track][key]</summary>
    public AnimKey[][] Keys = Array.Empty<AnimKey[]>();

    public const float Fps = 30f;

    public static bool IsAnim(byte[] d) => d.Length > 0x60 && Encoding.ASCII.GetString(d, 0, 9) == "animation";

    public static AnimAsset Parse(byte[] d)
    {
        if (!IsAnim(d)) throw new InvalidDataException("not an animation asset");
        var a = new AnimAsset
        {
            Version = BE.CStr(d, 10, 14),
            Duration = BE.F32(d, 0x1C),
            Tracks = BE.U16(d, 0x20),
            Frames = BE.U16(d, 0x22),
            KeyRate = BE.F32(d, 0x5C),
        };
        var s = QuatStream.Read(d);
        int step = a.KeyRate > 0 ? (int)(1.0 / a.KeyRate + 0.5) : 1;
        a.KeyFrames = Enumerable.Range(0, s.NKeys).Select(k => Math.Min(k * step, Math.Max(0, a.Frames - 1))).ToArray();
        a.Keys = s.ToKeys();
        a.Tracks = s.NTracks;
        return a;
    }

    // ------------------------------------------------------------------ stream model (integer level)

    internal const byte KindDefault = 0, KindStatic = 1, KindAnim = 2;

    /// <summary>One channel of one track: not stored (default), static (base) or animated (base + width bits per key).</summary>
    internal struct Chan
    {
        public byte Kind; public long Base; public int Width;
        /// <summary>Animated: quantised value per key (bits + base).</summary>
        public long[]? Q;
    }

    /// <summary>The QUAT_BITSTREAM[32] stream at anim+0x24 decoded to integers (descriptor fields + per-channel data).</summary>
    internal sealed class QuatStream
    {
        public int Codec, D; public string Name = ""; public bool Wide;
        public int Nch, NTracks, NKeys;
        public float[] Sc = Array.Empty<float>(), Df = Array.Empty<float>();
        public int Scales, Defaults, Flags, Bases, Widths, BaseBytes, WidthBytes, Stride, FlagsEnd, BitsPerKey;
        public Chan[][] Ch = Array.Empty<Chan[]>();
        public byte[][] TrackFlags = Array.Empty<byte[]>();
        public int KeyStart => Bases + BaseBytes + WidthBytes;
        public int KeyEnd => KeyStart + NKeys * Stride;

        public static QuatStream Read(byte[] d)
        {
            int codec = (int)BE.U32(d, 0x24);
            if (codec == 0) throw new InvalidDataException("animation has no keyframe codec");
            var s = new QuatStream { Codec = codec, Name = BE.CStr(d, codec, 0x16) };
            if (s.Name != "QUAT_BITSTREAM" && s.Name != "QUAT_BITSTREAM32")
                throw new NotSupportedException($"animation codec {s.Name} is not supported yet");
            int D = s.D = (int)BE.U32(d, codec + 0x18);
            if (D == 0) throw new InvalidDataException("animation keyframe stream is empty");
            s.Scales = (int)BE.U32(d, D + 4); s.Defaults = (int)BE.U32(d, D + 8); s.Flags = (int)BE.U32(d, D + 0xC);
            s.Bases = (int)BE.U32(d, D + 0x10); s.Widths = (int)BE.U32(d, D + 0x14);
            s.BaseBytes = BE.U16(d, D + 0x18); s.WidthBytes = BE.U16(d, D + 0x1A); s.Stride = BE.U16(d, D + 0x1C);
            int nch = s.Nch = BE.U16(d, D + 0x1E); s.NTracks = BE.U16(d, D + 0x22); int nkeys = s.NKeys = BE.U16(d, D + 0x24);
            bool wide = s.Wide = d[D + 0x26] != 0;
            if (nch != 9) throw new NotSupportedException($"{nch} channels per track (expected 9)");
            s.Sc = new float[nch]; s.Df = new float[nch];
            for (int c = 0; c < nch; c++) { s.Sc[c] = BE.F32(d, s.Scales + 4 * c); s.Df[c] = BE.F32(d, s.Defaults + 4 * c); }
            var readers = new BitReader[nkeys];
            for (int k = 0; k < nkeys; k++) readers[k] = new BitReader(d, s.KeyStart + k * s.Stride);
            s.Ch = new Chan[s.NTracks][]; s.TrackFlags = new byte[s.NTracks][];
            int fp = s.Flags, bp = s.Bases, wp = s.Widths, nib = 0;
            for (int t = 0; t < s.NTracks; t++)
            {
                var chans = s.Ch[t] = new Chan[nch];
                int ch = 0, fstart = fp;
                while (ch < nch)
                {
                    int f0 = d[fp], mode = f0 & 3;
                    int?[] triples = mode switch
                    {
                        1 => new int?[] { f0, null, d[fp + 1] },
                        2 => new int?[] { f0, d[fp + 1], null },
                        3 => new int?[] { f0, d[fp + 1], d[fp + 2] },
                        _ => new int?[] { f0, null, null },
                    };
                    fp += 1 + (mode == 0 ? 0 : mode == 3 ? 2 : 1);
                    foreach (var fb in triples)
                        for (int bit = 0; bit < 3 && ch < nch; bit++, ch++)
                        {
                            bool stored = fb.HasValue && ((fb.Value >> (7 - bit)) & 1) != 0;
                            bool isStatic = fb.HasValue && ((fb.Value >> (4 - bit)) & 1) != 0;
                            if (!stored) { chans[ch] = new Chan { Kind = KindDefault }; continue; }
                            int b;
                            if (wide) { b = BE.S32(d, bp); bp += 4; } else { b = (short)BE.U16(d, bp); bp += 2; }
                            if (isStatic) { chans[ch] = new Chan { Kind = KindStatic, Base = b }; continue; }
                            int n;
                            if (wide) { n = d[wp] + 1; wp++; }
                            else { n = ((d[wp] >> (4 * nib)) & 15) + 1; wp += nib; nib ^= 1; }
                            var q = new long[nkeys];
                            for (int k = 0; k < nkeys; k++) q[k] = (long)readers[k].Read(n) + b;
                            chans[ch] = new Chan { Kind = KindAnim, Base = b, Width = n, Q = q };
                            s.BitsPerKey += n;
                        }
                }
                s.TrackFlags[t] = d[fstart..fp];
            }
            s.FlagsEnd = fp;
            return s;
        }

        /// <summary>Dequantised keys, exactly as the game computes them.</summary>
        public AnimKey[][] ToKeys()
        {
            var keys = new AnimKey[NTracks][];
            var v = new float[Nch];
            for (int t = 0; t < NTracks; t++)
            {
                var row = keys[t] = new AnimKey[NKeys];
                for (int k = 0; k < NKeys; k++)
                {
                    for (int c = 0; c < Nch; c++)
                    {
                        var ch = Ch[t][c];
                        v[c] = ch.Kind switch { KindDefault => Df[c], KindStatic => (int)ch.Base * Sc[c], _ => ch.Q![k] * Sc[c] };
                    }
                    float w2 = 1f - (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
                    row[k] = new AnimKey(new Quaternion(v[0], v[1], v[2], w2 > 0 ? MathF.Sqrt(w2) : 0f),
                        new Vector3(v[3], v[4], v[5]), new Vector3(v[6], v[7], v[8]));
                }
            }
            return keys;
        }
    }

    // ------------------------------------------------------------------ encoder

    /// <summary>
    /// Re-encodes the QUAT_BITSTREAM stream of <paramref name="original"/> with new keys (same track count, key count and
    /// key frames; the original per-channel scales and defaults are kept). Per channel: not stored when every key is
    /// within half a quantum of the default, static when every key quantises to the same value, else animated with
    /// base = minimum and the minimal width. A value that needs more than 16 bits or a base outside s16 switches the
    /// stream to the wide QUAT_BITSTREAM32 layout. With <paramref name="reuseOriginalEncoding"/>, channels (and flag
    /// bytes) whose quantised values are unchanged keep the original encoding, so an unedited anim rebuilds
    /// byte-identically. Flags, bases, widths and key bitstreams are rewritten in place; everything after them (BITSTREAM
    /// streams, pose, tables) moves by <see cref="AnimRebuild.Delta"/> (a multiple of 16, so alignment is kept) and
    /// every self-pointer listed in <paramref name="selfPointerOffsets"/> (the CAFF relocations of this part that
    /// target itself) is fixed. Use <see cref="ReplaceInCaff"/> to do all of this on a loaded bundle.
    /// </summary>
    public static AnimRebuild Rebuild(byte[] original, AnimKey[][] newKeys, IReadOnlyCollection<int>? selfPointerOffsets = null,
        bool reuseOriginalEncoding = true)
    {
        if (!IsAnim(original)) throw new InvalidDataException("not an animation asset");
        var s = QuatStream.Read(original);
        int nt = s.NTracks, nk = s.NKeys, nch = s.Nch;
        if (newKeys.Length != nt) throw new ArgumentException($"{newKeys.Length} tracks given, the animation has {nt}");
        for (int t = 0; t < nt; t++)
            if (newKeys[t].Length != nk) throw new ArgumentException($"track {t}: {newKeys[t].Length} keys given, the animation has {nk}");

        int F = s.Flags;
        if (F == 0) throw new InvalidDataException("keyframe stream has no flag table");
        if (s.D + 0x28 > F || s.Codec + 0x1C > F || s.Scales + 4 * nch > F || s.Defaults + 4 * nch > F)
            throw new InvalidDataException("unexpected stream layout (descriptor/scale/default tables after the flags)");
        bool hasData = s.BaseBytes > 0 || s.WidthBytes > 0 || s.Stride > 0;
        if (hasData && (s.Bases != Align4(s.FlagsEnd) || s.Widths != s.Bases + s.BaseBytes && !(s.Widths == 0 && s.WidthBytes == 0)))
            throw new InvalidDataException("unexpected stream layout (flags, bases and widths are not contiguous)");
        int oldEnd = Align4(hasData ? Math.Max(s.KeyEnd, s.FlagsEnd) : s.FlagsEnd);

        var res = new AnimRebuild { RegionStart = F, OldRegionEnd = oldEnd, OldStride = s.Stride, NewStride = s.Stride, Wide = s.Wide };

        // --- channel values (rotation xyz with w >= 0, translation offset, scale)
        var origKeys = s.ToKeys();
        var vals = new double[nt][,];
        for (int t = 0; t < nt; t++)
        {
            var v = vals[t] = new double[nk, nch];
            for (int k = 0; k < nk; k++)
            {
                var key = newKeys[t][k];
                var r = key.Rotation;
                float len2 = r.LengthSquared();
                if (len2 > 0 && MathF.Abs(len2 - 1f) > 1e-3f) r = Quaternion.Normalize(r);
                if (r.W < 0) r = new Quaternion(-r.X, -r.Y, -r.Z, -r.W);   // w is rebuilt as +sqrt(1 - xyz²)
                // w ≈ 0 (half turns): q and −q both have w ≈ 0 and the sign is noise; keep the original key's xyz sign
                // (the codec's w is only accurate to ~1e-2 there anyway)
                var ok = origKeys[t][k].Rotation;
                if (r.W < 1e-3f && r.X * ok.X + r.Y * ok.Y + r.Z * ok.Z < 0) r = new Quaternion(-r.X, -r.Y, -r.Z, -r.W);
                v[k, 0] = r.X; v[k, 1] = r.Y; v[k, 2] = r.Z;
                v[k, 3] = key.Translation.X; v[k, 4] = key.Translation.Y; v[k, 5] = key.Translation.Z;
                v[k, 6] = key.Scale.X; v[k, 7] = key.Scale.Y; v[k, 8] = key.Scale.Z;
                for (int c = 0; c < nch; c++)
                    if (!double.IsFinite(v[k, c])) throw new ArgumentException($"track {t} key {k} channel {c}: value is not finite");
            }
        }
        bool IsDefault(int t, int c, double sc)
        {
            double df = s.Df[c], tol = sc > 0 ? 0.5 * sc : 1e-6;
            for (int k = 0; k < nk; k++) if (Math.Abs(vals[t][k, c] - df) > tol) return false;
            return true;
        }
        // unchanged = every key dequantises to the same float as before (large wide-stream values lose integer
        // precision in float, so compare decoded values, not integers)
        bool Same(int t, int c)
        {
            var o = s.Ch[t][c];
            float fsc = s.Sc[c];
            if (o.Kind == KindDefault) return IsDefault(t, c, fsc);
            if (!(fsc > 0)) return false;
            for (int k = 0; k < nk; k++)
            {
                double x = Math.Round(vals[t][k, c] / fsc);
                float before = o.Kind == KindStatic ? (int)o.Base * fsc : o.Q![k] * fsc;
                if ((float)x * fsc != before) return false;
            }
            return true;
        }
        var same = new bool[nt, nch];
        for (int t = 0; t < nt; t++) for (int c = 0; c < nch; c++) same[t, c] = Same(t, c);

        // --- a value too large for the channel's quantisation scale: coarsen that channel's scale (shared by all
        // tracks) by powers of two until every key fits in ±2^30 (s32 base, ≤ 32-bit widths)
        var scale = s.Sc.Select(x => (double)x).ToArray();
        var rescaled = new bool[nch];
        const double Limit = 1 << 30;
        for (int c = 0; c < nch; c++)
        {
            double sc = scale[c];
            if (!(sc > 0) || !float.IsFinite((float)sc)) continue;
            bool overflow = false; double maxAbs = 0;
            for (int t = 0; t < nt; t++)
                for (int k = 0; k < nk; k++)
                {
                    double a = Math.Abs(vals[t][k, c]);
                    maxAbs = Math.Max(maxAbs, a);
                    if (!(reuseOriginalEncoding && same[t, c]) && Math.Round(a / sc) > 2 * Limit) overflow = true;
                }
            if (!overflow) continue;
            while (maxAbs / sc > Limit) sc *= 2;
            scale[c] = sc; rescaled[c] = true; res.ScalesChanged.Add(c);
        }

        // --- choose the encoding of every channel
        var enc = new Chan[nt][];
        var q = new long[nk];
        bool needWide = false;
        for (int t = 0; t < nt; t++)
        {
            var row = enc[t] = new Chan[nch];
            for (int c = 0; c < nch; c++)
            {
                double sc = scale[c];
                bool isDefault = IsDefault(t, c, sc);
                bool unchanged = same[t, c] && !rescaled[c];
                if (!same[t, c]) { res.ChannelsChanged++; res.Changed.Add((t, c)); }
                if (unchanged && reuseOriginalEncoding) { row[c] = s.Ch[t][c]; res.ChannelsReused++; }
                else if (isDefault) row[c] = new Chan { Kind = KindDefault };
                else if (!(sc > 0) || !float.IsFinite((float)sc))
                    throw new ArgumentException($"track {t} channel {c}: the channel has no quantisation scale and must stay at its default {s.Df[c]}");
                else
                {
                    for (int k = 0; k < nk; k++)
                    {
                        double x = Math.Round(vals[t][k, c] / sc);
                        // one step past the s32 range (e.g. 2.0 at scale 2^-30, stored by the game as 2^31-1): clamp
                        if (Math.Abs(x) > 2 * Limit) throw new ArgumentException($"track {t} channel {c}: value {vals[t][k, c]} is out of range for scale {sc}");
                        q[k] = (long)Math.Clamp(x, int.MinValue, int.MaxValue);
                    }
                    long min = q.Min(), max = q.Max();
                    if (min == max) row[c] = new Chan { Kind = KindStatic, Base = min };
                    else
                    {
                        int width = 64 - BitOperations.LeadingZeroCount((ulong)(max - min));
                        if (width > 32) throw new ArgumentException($"track {t} channel {c}: key range needs {width} bits (max 32)");
                        row[c] = new Chan { Kind = KindAnim, Base = min, Width = width, Q = (long[])q.Clone() };
                    }
                }
                var e = row[c];
                if (e.Kind != KindDefault && (e.Base < short.MinValue || e.Base > short.MaxValue)) needWide = true;
                if (e.Kind == KindAnim && e.Width > 16) needWide = true;
            }
        }
        if (reuseOriginalEncoding && res.ChannelsChanged == 0 && res.ScalesChanged.Count == 0)
        {
            // nothing to re-encode: keep the original bytes (this also keeps the original encoder's quirks, e.g. 6 anims
            // whose trailing constant channels have width 1 but no bits in the key stride)
            res.Data = (byte[])original.Clone();
            return res;
        }
        bool wide = s.Wide || needWide;
        res.Wide = wide; res.WideChanged = wide != s.Wide;

        // --- flags
        var region = new List<byte>();
        for (int t = 0; t < nt; t++)
        {
            bool sameKinds = Enumerable.Range(0, nch).All(c => enc[t][c].Kind == s.Ch[t][c].Kind);
            if (reuseOriginalEncoding && sameKinds) { region.AddRange(s.TrackFlags[t]); continue; }
            int Triple(int first)
            {
                int f = 0;
                for (int b = 0; b < 3; b++)
                {
                    var e = enc[t][first + b];
                    if (e.Kind != KindDefault) f |= 0x80 >> b;
                    if (e.Kind == KindStatic) f |= 0x10 >> b;
                }
                return f;
            }
            int f0 = Triple(0), f1 = Triple(3), f2 = Triple(6);
            int mode = (f1 != 0 ? 2 : 0) | (f2 != 0 ? 1 : 0);
            region.Add((byte)(f0 | mode));
            if (f1 != 0) region.Add((byte)f1);
            if (f2 != 0) region.Add((byte)f2);
        }
        Pad4(region);
        // --- bases
        int basesAt = F + region.Count;
        int nbase = 0;
        for (int t = 0; t < nt; t++)
            for (int c = 0; c < nch; c++)
            {
                var e = enc[t][c];
                if (e.Kind == KindDefault) continue;
                if (wide) { region.Add((byte)(e.Base >> 24)); region.Add((byte)(e.Base >> 16)); region.Add((byte)(e.Base >> 8)); region.Add((byte)e.Base); nbase += 4; }
                else { region.Add((byte)(e.Base >> 8)); region.Add((byte)e.Base); nbase += 2; }
            }
        Pad4(region);
        int baseBytes = Align4(nbase);
        // --- widths
        int widthsAt = F + region.Count, nwidth = 0;
        bool high = false;
        long totalBits = 0;
        for (int t = 0; t < nt; t++)
            for (int c = 0; c < nch; c++)
            {
                var e = enc[t][c];
                if (e.Kind != KindAnim) continue;
                totalBits += e.Width;
                if (wide) { region.Add((byte)(e.Width - 1)); nwidth++; }
                else if (!high) { region.Add((byte)(e.Width - 1)); nwidth++; high = true; }
                else { region[^1] |= (byte)((e.Width - 1) << 4); high = false; }
            }
        Pad4(region);
        int widthBytes = Align4(nwidth);
        // --- key bitstreams (LSB-first bits in big-endian 32-bit words = a little-endian bitstream byte-swapped per word)
        int stride = (int)((totalBits + 7) / 8);
        if (stride > ushort.MaxValue || baseBytes > ushort.MaxValue || widthBytes > ushort.MaxValue)
            throw new ArgumentException("encoded animation is too large for the descriptor's 16-bit sizes");
        int keysAt = F + region.Count;
        var bits = new byte[Align4(nk * stride)];
        for (int k = 0; k < nk; k++)
        {
            long p = (long)k * stride * 8;
            for (int t = 0; t < nt; t++)
                for (int c = 0; c < nch; c++)
                {
                    var e = enc[t][c];
                    if (e.Kind != KindAnim) continue;
                    ulong val = (ulong)(e.Q![k] - e.Base);
                    if (e.Width < 64 && (val >> e.Width) != 0) throw new InvalidOperationException($"track {t} channel {c}: delta does not fit {e.Width} bits");
                    for (int i = 0; i < e.Width; i++, p++)
                        if (((val >> i) & 1) != 0) bits[(p >> 5) * 4 + 3 - ((p & 31) >> 3)] |= (byte)(1 << (int)(p & 7));
                }
        }
        region.AddRange(bits);
        int delta = region.Count - (oldEnd - F);
        int mis = ((delta % 16) + 16) % 16;
        if (mis != 0) { region.AddRange(new byte[16 - mis]); delta += 16 - mis; }
        res.Delta = delta; res.NewStride = stride;
        if (delta != 0 && selfPointerOffsets == null)
            throw new InvalidOperationException("the encoded stream changed size: pass the part's self-relocations (use AnimAsset.ReplaceInCaff)");

        // --- assemble
        var outData = new byte[original.Length + delta];
        Buffer.BlockCopy(original, 0, outData, 0, F);
        region.CopyTo(outData, F);
        Buffer.BlockCopy(original, oldEnd, outData, F + region.Count, original.Length - oldEnd);
        foreach (int at in selfPointerOffsets ?? Array.Empty<int>())
        {
            if (at >= F && at < oldEnd) throw new InvalidDataException($"pointer at 0x{at:X} lies inside the keyframe region");
            uint target = BE.U32(original, at);
            if (target >= oldEnd) BE.W32(outData, res.MapOffset(at), (uint)(target + delta));
            else if (target > F && target != s.Bases && target != s.Widths)
                throw new InvalidDataException($"pointer at 0x{at:X} targets 0x{target:X} inside the keyframe region");
        }
        int D = s.D;
        bool baseNull = s.Bases == 0 && baseBytes == 0, widthNull = s.Widths == 0 && widthBytes == 0;
        BE.W32(outData, D + 0x10, baseNull ? 0u : (uint)basesAt);
        BE.W32(outData, D + 0x14, widthNull ? 0u : (uint)widthsAt);
        if (!baseNull) res.RequiredPointers.Add(D + 0x10);
        if (!widthNull) res.RequiredPointers.Add(D + 0x14);
        BE.W16(outData, D + 0x18, (ushort)baseBytes);
        BE.W16(outData, D + 0x1A, (ushort)widthBytes);
        BE.W16(outData, D + 0x1C, (ushort)stride);
        outData[D + 0x26] = (byte)(wide ? 1 : 0);
        foreach (int c in res.ScalesChanged) BE.WF32(outData, s.Scales + 4 * c, (float)scale[c]);
        if (res.WideChanged)
        {
            Array.Clear(outData, s.Codec, 0x16);
            Encoding.ASCII.GetBytes("QUAT_BITSTREAM32").CopyTo(outData, s.Codec);
        }
        if (keysAt != basesAt + baseBytes + widthBytes) throw new InvalidOperationException("internal layout error");
        res.Data = outData;
        return res;
    }

    /// <summary>
    /// Rebuilds the anim of <paramref name="symbol"/> in a loaded CAFF: replaces its .data part, moves the part's
    /// relocation offsets that lie after the rewritten region, fixes pointers from other parts into it and makes sure
    /// the descriptor's bases/widths pointers are relocated.
    /// </summary>
    public static AnimRebuild ReplaceInCaff(CaffFile caff, int symbol, AnimKey[][] newKeys, bool reuseOriginalEncoding = true)
    {
        var part = caff.PartsOf(symbol).First(p => caff.SectionOf(p).Name == ".data");
        int pid = caff.Parts.IndexOf(part) + 1;
        var self = caff.Relocs.Where(r => r.FromPart == pid && r.ToPart == pid).SelectMany(r => r.Offsets).ToHashSet();
        var res = Rebuild(part.Data, newKeys, self, reuseOriginalEncoding);
        part.Data = res.Data;
        foreach (var g in caff.Relocs.Where(g => g.FromPart == pid))
            g.Offsets = g.Offsets.Select(res.MapOffset).ToArray();
        if (res.Delta != 0)
            foreach (var g in caff.Relocs.Where(g => g.ToPart == pid && g.FromPart != pid))
            {
                var fd = caff.Parts[g.FromPart - 1].Data;
                foreach (int o in g.Offsets)
                {
                    uint target = BE.U32(fd, o);
                    if (target >= res.OldRegionEnd) BE.W32(fd, o, (uint)(target + res.Delta));
                }
            }
        var missing = res.RequiredPointers.Where(o => !self.Contains(o)).ToList();
        if (missing.Count > 0)
        {
            var g = caff.Relocs.FirstOrDefault(r => r.FromPart == pid && r.ToPart == pid);
            if (g == null) caff.Relocs.Add(new CaffReloc(pid, pid, missing.OrderBy(x => x).ToArray()));
            else g.Offsets = g.Offsets.Concat(missing).OrderBy(x => x).ToArray();
        }
        return res;
    }

    /// <summary>Per track and channel: encoding kind (0 = default, 1 = static, 2 = animated), base and bit width; plus the raw flag bytes.</summary>
    public static (byte Kind, long Base, int Width)[][] ChannelLayout(byte[] d, out byte[][] trackFlags, out bool wide)
    {
        var s = QuatStream.Read(d);
        trackFlags = s.TrackFlags; wide = s.Wide;
        return s.Ch.Select(r => r.Select(c => (c.Kind, c.Base, c.Width)).ToArray()).ToArray();
    }

    static int Align4(int v) => (v + 3) & ~3;
    static void Pad4(List<byte> b) { while ((b.Count & 3) != 0) b.Add(0); }

    /// <summary>Bits consumed LSB-first from big-endian 32-bit words (mirror of 0x829A1538).</summary>
    sealed class BitReader
    {
        readonly byte[] _d; int _w, _off;
        public BitReader(byte[] d, int addr) { _d = d; _w = addr & ~3; _off = (addr & 3) * 8; }
        public uint Read(int n)
        {
            ulong w0 = _w + 4 <= _d.Length ? BE.U32(_d, _w) : 0u;
            ulong w1 = _w + 8 <= _d.Length ? BE.U32(_d, _w + 4) : 0u;
            ulong v = ((w0 | (w1 << 32)) >> _off) & ((1UL << n) - 1);
            _off += n; _w += (_off >> 5) * 4; _off &= 31;
            return (uint)v;
        }
    }
}
