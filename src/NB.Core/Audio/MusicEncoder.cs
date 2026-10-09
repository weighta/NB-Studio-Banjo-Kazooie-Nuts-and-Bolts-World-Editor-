using System.Diagnostics;
using System.Text.Json;
using NB.Core.IO;

namespace NB.Core.Audio;

/// <summary>A wave ready for a wave bank entry: codec 0 = big-endian PCM16, 1 = XMA2 (packets + seek table).</summary>
public sealed record EncodedWave(int Codec, int Channels, int Rate, byte[] Data, byte[]? SeekTable, int Samples)
{
    public string CodecName => Codec == 0 ? "PCM16" : "XMA2";
}

/// <summary>
/// The encoder slot of Replace…: PCM in, the game's format out.
///  • PCM16 (built in): big-endian 16-bit PCM in the XWB entry (format tag 0). The game's own format for Showdown Town,
///    World of Sports and Terrarium music; verified in Xenia (2026-10-08) both for a town stem and in place of an XMA2
///    track (Nutty Acres): the game streams the new samples while it plays. About 10 MB per stereo minute.
///  • XMA2 (optional): an external encoder in thirdparty/&lt;tool&gt;/ described by xma2encoder.json
///    {"exe": "encoder.exe", "args": "\"{in}\" \"{out}\""} — it gets a 16-bit WAV and must write a RIFF/WAVE file with
///    an XMA2 "fmt " (tag 0x166) and its "data" (XMA packets) and "seek" (big-endian u32 cumulative samples per 64 KB
///    block) chunks; those become the entry's data and seek table (the layout of the game's own XMA2 entries).
///    Untested until such an encoder is installed.
/// </summary>
public static class MusicEncoder
{
    public sealed record ExternalEncoder(string Name, string Exe, string Args);

    /// <summary>The XMA2 encoder described by a thirdparty/*/xma2encoder.json under <paramref name="baseDir"/>, or null.
    /// Folders that can't be listed are skipped.</summary>
    public static ExternalEncoder? FindXma2Encoder(string? baseDir)
    {
        for (var d = baseDir; d != null; d = Path.GetDirectoryName(d))
        {
            var tp = Path.Combine(d, "thirdparty");
            if (!Directory.Exists(tp)) continue;
            string[] found;
            try { found = Directory.GetFiles(tp, "xma2encoder.json", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }); }
            catch (Exception) { continue; }
            foreach (var cfg in found)
                try
                {
                    var j = JsonDocument.Parse(File.ReadAllText(cfg)).RootElement;
                    var exe = Path.Combine(Path.GetDirectoryName(cfg)!, j.GetProperty("exe").GetString()!);
                    if (File.Exists(exe)) return new ExternalEncoder(Path.GetFileName(Path.GetDirectoryName(cfg)!), exe, j.TryGetProperty("args", out var a) ? a.GetString()! : "\"{in}\" \"{out}\"");
                }
                catch (Exception) { }
        }
        return null;
    }

    /// <summary>Time limits of the external tools: a tool that hangs (a damaged input file) is stopped.</summary>
    public static TimeSpan DecodeTimeout = TimeSpan.FromMinutes(5), EncodeTimeout = TimeSpan.FromMinutes(20);

    /// <summary>Runs an external tool (vgmstream, an encoder) reading both of its outputs at once, with a time limit and
    /// cancellation: in either case the tool is killed and TimeoutException / OperationCanceledException is thrown.
    /// Returns the exit code and the error output.</summary>
    public static (int ExitCode, string StdErr) RunTool(ProcessStartInfo psi, TimeSpan timeout, CancellationToken ct = default)
    {
        psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
        ct.ThrowIfCancellationRequested();
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + psi.FileName);
        var err = p.StandardError.ReadToEndAsync(); var outp = p.StandardOutput.ReadToEndAsync();
        var sw = Stopwatch.StartNew();
        while (!p.WaitForExit(200))
        {
            if (!ct.IsCancellationRequested && sw.Elapsed < timeout) continue;
            try { p.Kill(true); } catch (Exception) { }
            p.WaitForExit(2000);
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"{Path.GetFileName(psi.FileName)} did not finish within {timeout.TotalMinutes:0.#} min and was stopped.");
        }
        Task.WaitAll(new Task[] { err, outp }, 5000);
        return (p.ExitCode, err.IsCompletedSuccessfully ? err.Result : "");
    }

    /// <summary>PCM (interleaved, host order) to the game's format: XMA2 through <paramref name="xma2"/> when given,
    /// else PCM16.</summary>
    public static EncodedWave Encode(short[] pcm, int channels, int rate, ExternalEncoder? xma2, CancellationToken ct = default)
    {
        int frames = pcm.Length / Math.Max(1, channels);
        if (xma2 == null)
        {
            var d = new byte[pcm.Length * 2];
            for (int i = 0; i < pcm.Length; i++) { d[2 * i] = (byte)(pcm[i] >> 8); d[2 * i + 1] = (byte)pcm[i]; }
            return new EncodedWave(0, channels, rate, d, null, frames);
        }
        var tmpIn = Path.Combine(Path.GetTempPath(), $"nb_enc_{Guid.NewGuid():N}.wav");
        var tmpOut = Path.ChangeExtension(tmpIn, ".xma");
        try
        {
            Wav.Write(tmpIn, pcm, channels, rate);
            var psi = new ProcessStartInfo(xma2.Exe, xma2.Args.Replace("{in}", tmpIn).Replace("{out}", tmpOut)) { WorkingDirectory = Path.GetDirectoryName(xma2.Exe)! };
            var (code, err) = RunTool(psi, EncodeTimeout, ct);
            if (code != 0 || !File.Exists(tmpOut)) throw new InvalidOperationException($"{xma2.Name}: encoding failed ({code}): {err.Trim()}");
            return ParseRiffXma2(File.ReadAllBytes(tmpOut));
        }
        finally { foreach (var f in new[] { tmpIn, tmpOut }) try { File.Delete(f); } catch (Exception) { } }
    }

    /// <summary>A RIFF/WAVE (little-endian chunks) or RIFX file holding XMA2 (fmt tag 0x166): channels, rate, packets,
    /// seek table and sample count.</summary>
    public static EncodedWave ParseRiffXma2(byte[] d)
    {
        bool be = d.Length > 12 && d[3] == 'X';
        if (d.Length < 12 || d[0] != 'R' || d[1] != 'I' || d[2] != 'F') throw new InvalidDataException("not a RIFF file");
        int U32(int o) => be ? BE.S32(d, o) : BitConverter.ToInt32(d, o);
        int U16(int o) => be ? BE.U16(d, o) : BitConverter.ToUInt16(d, o);
        int ch = 0, rate = 0, samples = 0; byte[]? data = null; byte[]? seek = null;
        for (int p = 12; p + 8 <= d.Length;)
        {
            string id = System.Text.Encoding.ASCII.GetString(d, p, 4); int sz = U32(p + 4);
            if (sz < 0 || p + 8 + sz > d.Length) break;
            if (id == "fmt ")
            {
                if (U16(p + 8) != 0x166) throw new InvalidDataException($"the encoder wrote format 0x{U16(p + 8):X4}, not XMA2 (0x166)");
                ch = U16(p + 10); rate = U32(p + 12);
                if (sz >= 0x1C) samples = U32(p + 8 + 0x18);   // XMA2WAVEFORMATEX: WAVEFORMATEX (18), NumStreams (2), ChannelMask (4), SamplesEncoded
            }
            else if (id == "data") data = d.AsSpan(p + 8, sz).ToArray();
            else if (id == "seek")
            {
                int n = sz / 4; seek = new byte[4 + 4 * n]; BE.W32(seek, 0, (uint)n);
                for (int k = 0; k < n; k++) BE.W32(seek, 4 + 4 * k, (uint)BE.S32(d, p + 8 + 4 * k));   // the seek chunk is big-endian
            }
            p += 8 + sz + (sz & 1);
        }
        if (data == null || ch == 0 || rate == 0) throw new InvalidDataException("the XMA2 file has no fmt / data chunk");
        if (seek != null && seek.Length >= 8) samples = (int)BE.U32(seek, seek.Length - 4);
        return new EncodedWave(1, ch, rate, data, seek, samples);
    }
}

public static class XwbEntryEncoded
{
    /// <summary>Puts an encoded wave into an entry (format word as the game's own entries: tag, channels, rate, block
    /// align 2 x channels, 16-bit flag). <paramref name="loop"/>: the wave loops from <paramref name="loopStart"/> (samples)
    /// to its end, like the game's own music (an intro, then the loop); else it plays once.</summary>
    public static void SetEncoded(this XwbEntry e, EncodedWave w, bool loop, int loopStart = 0)
    {
        e.Data = w.Data;
        e.Format = (uint)w.Codec | (uint)w.Channels << 2 | (uint)w.Rate << 5 | (uint)(w.Channels * 2) << 23 | 1u << 31;
        e.FlagsDuration = (e.FlagsDuration & 0xF) | (uint)w.Samples << 4;
        e.SeekTable = w.SeekTable;
        if (loop && (loopStart < 0 || loopStart >= w.Samples))
            throw new ArgumentOutOfRangeException(nameof(loopStart), $"loop start {loopStart} is outside the wave (0..{w.Samples - 1})");
        if (loop) { e.LoopStart = (uint)loopStart; e.LoopLength = (uint)(w.Samples - loopStart); } else { e.LoopStart = 0; e.LoopLength = 0; }
    }
}
