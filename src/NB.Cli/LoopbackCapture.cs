using System.Runtime.InteropServices;

namespace NB.Cli;

/// <summary>
/// Records what the default playback device is playing (WASAPI shared-mode loopback), for checking audio mods
/// in Xenia without extra dependencies. Output: 16-bit PCM WAV at the device mix rate/channels.
/// </summary>
static class LoopbackCapture
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCo { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        int GetBufferSize(out uint frames);
        int GetStreamLatency(out long latency);
        int GetCurrentPadding(out uint padding);
        int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        int GetMixFormat(out IntPtr format);
        int GetDevicePeriod(out long defaultPeriod, out long minPeriod);
        int Start();
        int Stop();
        int Reset();
        int SetEventHandle(IntPtr handle);
        int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        int ReleaseBuffer(uint frames);
        int GetNextPacketSize(out uint frames);
    }

    static void Check(int hr, string what) { if (hr < 0) throw new COMException(what, hr); }

    /// <summary>Records for <paramref name="seconds"/> and returns (interleaved float samples, rate, channels).</summary>
    public static (float[] Samples, int Rate, int Channels) Record(double seconds)
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
        Check(en.GetDefaultAudioEndpoint(0, 0, out var dev), "GetDefaultAudioEndpoint");
        var iidClient = typeof(IAudioClient).GUID;
        Check(dev.Activate(ref iidClient, 23, IntPtr.Zero, out var o), "Activate");
        var client = (IAudioClient)o;
        Check(client.GetMixFormat(out var fmt), "GetMixFormat");
        int channels = Marshal.ReadInt16(fmt, 2), rate = Marshal.ReadInt32(fmt, 4), bits = Marshal.ReadInt16(fmt, 14);
        ushort tag = (ushort)Marshal.ReadInt16(fmt, 0);
        bool isFloat = tag == 3 || (tag == 0xFFFE && bits == 32 && Marshal.ReadInt32(fmt, 24) == 3); // subformat IEEE float
        Check(client.Initialize(0, 0x20000 /* LOOPBACK */, 10_000_000, 0, fmt, IntPtr.Zero), "Initialize");
        var iidCap = typeof(IAudioCaptureClient).GUID;
        Check(client.GetService(ref iidCap, out var c), "GetService");
        var cap = (IAudioCaptureClient)c;
        var outp = new List<float>((int)(seconds * rate * channels) + 4096);
        long needed = (long)(seconds * rate);
        long got = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Check(client.Start(), "Start");
        while (sw.Elapsed.TotalSeconds < seconds + 0.5 && got < needed)
        {
            Thread.Sleep(10);
            while (true)
            {
                Check(cap.GetNextPacketSize(out var n), "GetNextPacketSize");
                if (n == 0) break;
                Check(cap.GetBuffer(out var data, out var frames, out var flags, out _, out _), "GetBuffer");
                int count = (int)frames * channels;
                if ((flags & 2) != 0) outp.AddRange(new float[count]); // silent
                else if (isFloat) { var tmp = new float[count]; Marshal.Copy(data, tmp, 0, count); outp.AddRange(tmp); }
                else { var tmp = new short[count]; Marshal.Copy(data, tmp, 0, count); outp.AddRange(tmp.Select(s => s / 32768f)); }
                got += frames;
                cap.ReleaseBuffer(frames);
            }
        }
        client.Stop();
        Marshal.FreeCoTaskMem(fmt);
        return (outp.ToArray(), rate, channels);
    }

    public static void SaveWav(string path, float[] s, int rate, int ch)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + s.Length * 2); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)ch);
        w.Write(rate); w.Write(rate * ch * 2); w.Write((short)(ch * 2)); w.Write((short)16); w.Write("data"u8); w.Write(s.Length * 2);
        foreach (var v in s) w.Write((short)Math.Clamp(v * 32767f, -32768, 32767));
    }

    /// <summary>Energy share of a narrow band around <paramref name="hz"/> in the mono mix (Goertzel vs total power).</summary>
    public static double ToneShare(float[] s, int rate, int ch, double hz)
    {
        int n = s.Length / ch; if (n == 0) return 0;
        double k = 2 * Math.Cos(2 * Math.PI * hz / rate), s1 = 0, s2 = 0, total = 0;
        for (int i = 0; i < n; i++)
        {
            double x = 0; for (int c = 0; c < ch; c++) x += s[i * ch + c]; x /= ch;
            total += x * x;
            double s0 = x + k * s1 - s2; s2 = s1; s1 = s0;
        }
        double power = s1 * s1 + s2 * s2 - k * s1 * s2;           // |X(f)|^2
        double tonePower = 2 * power / n;                          // ≈ sum of x^2 contributed by the tone
        return total <= 0 ? 0 : tonePower / total;
    }
}
