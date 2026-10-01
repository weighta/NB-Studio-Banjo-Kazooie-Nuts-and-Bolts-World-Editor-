using System.Diagnostics;
using System.Text;
using NB.Core.Formats;
using NB.Core.Project;

namespace NB.Core.Audio;

/// <summary>Minimal RIFF WAVE reader/writer (PCM 8/16/24/32-bit and IEEE float in, PCM16 out).</summary>
public static class Wav
{
    public static void Write(string path, short[] samples, int channels, int rate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var w = new BinaryWriter(File.Create(path));
        int data = samples.Length * 2;
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + data); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        w.Write(16); w.Write((short)1); w.Write((short)channels); w.Write(rate); w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(data);
        foreach (var s in samples) w.Write(s);
    }

    public static (short[] Samples, int Channels, int Rate) Read(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "RIFF") throw new InvalidDataException("not a RIFF file");
        r.ReadInt32();
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "WAVE") throw new InvalidDataException("not a WAVE file");
        int fmtTag = 0, ch = 0, rate = 0, bits = 0; byte[]? data = null;
        while (r.BaseStream.Position + 8 <= r.BaseStream.Length)
        {
            string id = Encoding.ASCII.GetString(r.ReadBytes(4)); int len = r.ReadInt32();
            long next = r.BaseStream.Position + len + (len & 1);
            if (id == "fmt ")
            {
                fmtTag = r.ReadInt16(); ch = r.ReadInt16(); rate = r.ReadInt32(); r.ReadInt32(); r.ReadInt16(); bits = r.ReadInt16();
                if (fmtTag == unchecked((short)0xFFFE) && len >= 40) { r.ReadInt16(); r.ReadInt16(); r.ReadInt32(); fmtTag = r.ReadInt16(); }
            }
            else if (id == "data") data = r.ReadBytes(len);
            r.BaseStream.Position = Math.Min(next, r.BaseStream.Length);
        }
        if (data == null || ch == 0) throw new InvalidDataException("WAV has no fmt/data chunk");
        short[] s;
        if (fmtTag == 1 && bits == 16) { s = new short[data.Length / 2]; Buffer.BlockCopy(data, 0, s, 0, s.Length * 2); }
        else if (fmtTag == 1 && bits == 8) s = data.Select(b => (short)((b - 128) << 8)).ToArray();
        else if (fmtTag == 1 && bits == 24) { s = new short[data.Length / 3]; for (int i = 0; i < s.Length; i++) s[i] = (short)(data[3 * i + 1] | data[3 * i + 2] << 8); }
        else if (fmtTag == 1 && bits == 32) { s = new short[data.Length / 4]; for (int i = 0; i < s.Length; i++) s[i] = (short)(BitConverter.ToInt32(data, 4 * i) >> 16); }
        else if (fmtTag == 3 && bits == 32) { s = new short[data.Length / 4]; for (int i = 0; i < s.Length; i++) s[i] = (short)Math.Clamp(BitConverter.ToSingle(data, 4 * i) * 32767f, -32768f, 32767f); }
        else throw new NotSupportedException($"WAV format {fmtTag} / {bits}-bit not supported (use PCM or float)");
        return (s, ch, rate);
    }
}

/// <summary>Wave bank access across the workspace: listing, extraction to WAV, replacement with WAV.</summary>
public static class AudioService
{
    public sealed record BankRef(uint Bundle, uint Id, string Name, int Entries, bool Streaming, long Size);

    public static List<BankRef> ListBanks(Workspace ws, uint bundle)
    {
        var list = new List<BankRef>();
        foreach (var e in ws.LoadStream(bundle).Entries.Where(e => e.Kind == "xwb"))
        {
            var x = XwbFile.Read(e.Data!);
            list.Add(new BankRef(bundle, e.Id, x.Name, x.Entries.Count, x.Streaming, e.Data!.Length));
        }
        return list;
    }

    public static XwbFile LoadBank(Workspace ws, uint bundle, uint id) =>
        XwbFile.Read(ws.LoadStream(bundle).Entries.First(e => e.Id == id && e.Kind == "xwb").Data!);

    /// <summary>
    /// Extracts entry <paramref name="index"/> to a WAV file. PCM entries are converted directly; XMA entries are
    /// decoded with vgmstream-cli (bundled in thirdparty\vgmstream) from a temporary .xwb.
    /// </summary>
    public static void ExtractWav(XwbFile bank, byte[] rawBank, int index, string outPath, string? vgmstreamExe)
    {
        var e = bank.Entries[index];
        if (e.Codec == 0) { Wav.Write(outPath, e.PcmSamples(), e.Channels, e.SampleRate); return; }
        if (vgmstreamExe == null || !File.Exists(vgmstreamExe)) throw new FileNotFoundException("vgmstream-cli.exe is needed to decode XMA audio", vgmstreamExe);
        var tmp = Path.Combine(Path.GetTempPath(), $"nb_{Guid.NewGuid():N}.xwb");
        File.WriteAllBytes(tmp, rawBank);
        try
        {
            var psi = new ProcessStartInfo(Path.GetFullPath(vgmstreamExe), $"-s {index + 1} -o \"{outPath}\" \"{tmp}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            string err = p.StandardError.ReadToEnd(); p.StandardOutput.ReadToEnd(); p.WaitForExit();
            if (p.ExitCode != 0 || !File.Exists(outPath)) throw new InvalidOperationException("vgmstream failed: " + err.Trim());
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    /// <summary>Replaces one entry with a WAV (stored as big-endian PCM16) and writes the stream bundle.</summary>
    public static string ReplaceWithWav(Workspace ws, uint bundle, uint bankId, int index, string wavPath)
    {
        var (s, ch, rate) = Wav.Read(wavPath);
        var arch = ws.LoadStream(bundle);
        var entry = arch.Entries.First(e => e.Id == bankId && e.Kind == "xwb");
        var bank = XwbFile.Read(entry.Data!);
        var old = bank.Entries[index];
        string before = $"{old.CodecName} {old.Channels}ch {old.SampleRate} Hz {old.Seconds:F2}s";
        old.SetPcm16(s, ch, rate, keepLoop: true);
        entry.Data = bank.Write();
        XwbFile.Read(entry.Data); // re-parse as a check
        ws.SaveStream(bundle, arch, $"replaced sound {index} of wave bank {bank.Name} ({before} → PCM16 {ch}ch {rate} Hz {old.Seconds:F2}s)");
        return $"{bank.Name}[{index}]: {before} → PCM16 {ch}ch {rate} Hz {old.Seconds:F2}s";
    }
}
