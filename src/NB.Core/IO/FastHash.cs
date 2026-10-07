using System.Numerics;
using System.Runtime.InteropServices;

namespace NB.Core.IO;

/// <summary>A fast (SIMD, ~10 GB/s) 64-bit checksum of bytes, to tell unchanged data from edited data (not cryptographic).</summary>
public static class FastHash
{
    public static ulong Of(ReadOnlySpan<byte> d, ulong h = 1469598103934665603UL)
    {
        h = (h ^ (ulong)d.Length) * 1099511628211UL;
        var words = MemoryMarshal.Cast<byte, ulong>(d);
        if (Vector.IsHardwareAccelerated && words.Length >= Vector<ulong>.Count * 4)
        {
            var vecs = MemoryMarshal.Cast<ulong, Vector<ulong>>(words);
            var acc = new Vector<ulong>(h); var mul = new Vector<ulong>(0x9E3779B97F4A7C15UL);
            foreach (var v in vecs) acc = (acc ^ v) * mul + v;
            for (int i = 0; i < Vector<ulong>.Count; i++) h = (h ^ acc[i]) * 1099511628211UL;
            words = words[(vecs.Length * Vector<ulong>.Count)..];
        }
        foreach (var w in words) h = (h ^ w) * 1099511628211UL;
        foreach (var b in d[(d.Length & ~7)..]) h = (h ^ b) * 1099511628211UL;
        return h;
    }
}
