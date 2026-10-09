using NB.Core.Formats;
using NB.Core.IO;
using NB.Core.Project;

namespace NB.Core.Audio;

/// <summary>Music audio: preview (one wave bank of the common stream bundle read on its own, not the 800 MB archive) and
/// replacing a cue's wave with any audio file (WAV, MP3, OGG, FLAC, M4A, WMA … decoded by the bundled vgmstream),
/// converted to the original wave's channels and rate and stored as big-endian PCM16 (the game's own music format;
/// PCM16 in place of an XMA wave verified in Xenia, docs/FORMATS.md §12 and §19).
/// The replacement's load → modify → save is one <see cref="Workspace.EditStream"/> (the archive's edit lock, which every
/// writer of a stream archive takes: part builds, texture and wave-bank replaces …); reads of parts of the file hold
/// <see cref="Workspace.StreamFileLock"/>, so a save never meets an open reader.</summary>
public static class MusicAudio
{
    /// <summary>One entry of a stream bundle (Bundle/50), read without loading the archive.</summary>
    public static byte[] ReadStreamEntry(Workspace ws, uint bundle, uint id) { lock (ws.StreamFileLock(bundle)) return ReadEntryAt(ws.Game.StreamPath(bundle), id); }

    static byte[] ReadEntryAt(string path, uint id)
    {
        using var f = File.OpenRead(path);
        var head = new byte[20]; f.ReadExactly(head);
        if (BE.U32(head, 0) != BundleArchive.Magic) throw new InvalidDataException("not a stream bundle");
        int count = BE.S32(head, 8), ndep = BE.S32(head, 16);
        var table = new byte[4 * ndep + 12 * count]; f.ReadExactly(table);
        for (int i = 0; i < count; i++)
        {
            int o = 4 * ndep + 12 * i;
            if (BE.U32(table, o) != id) continue;
            int off = BE.S32(table, o + 4), size = BE.S32(table, o + 8);
            var d = new byte[size]; f.Position = off; f.ReadExactly(d); return d;
        }
        throw new KeyNotFoundException($"entry {id:X8} not in {path}");
    }

    static readonly Dictionary<string, Dictionary<string, uint>> BankIdCache = new();

    /// <summary>Wave bank name → stream entry id in the common bundle (cached per stream file version).</summary>
    public static uint BankId(Workspace ws, string bankName)
    {
        var path = ws.Game.StreamPath(MusicCatalog.CommonBundle);
        Dictionary<string, uint>? map;
        lock (BankIdCache)
            lock (ws.StreamFileLock(MusicCatalog.CommonBundle))
            {
                string key = path + "|" + new FileInfo(path).LastWriteTimeUtc.Ticks;
                if (!BankIdCache.TryGetValue(key, out map))
                {
                    map = new();
                    using var f = File.OpenRead(path);
                    var head = new byte[20]; f.ReadExactly(head);
                    int count = BE.S32(head, 8), ndep = BE.S32(head, 16);
                    var table = new byte[4 * ndep + 12 * count]; f.ReadExactly(table);
                    var hdr = new byte[0x40];
                    for (int i = 0; i < count; i++)
                    {
                        int o = 4 * ndep + 12 * i, off = BE.S32(table, o + 4), size = BE.S32(table, o + 8);
                        if (size < 0x100) continue;
                        f.Position = off; f.ReadExactly(hdr);
                        if (!XwbFile.Is(hdr)) continue;
                        int bank = BE.S32(hdr, 12);
                        var b = new byte[72]; f.Position = off + bank; f.ReadExactly(b);
                        map.TryAdd(BE.CStr(b, 8, 64), BE.U32(table, o));
                    }
                    BankIdCache[key] = map;
                }
            }
        return map.TryGetValue(bankName, out var id) ? id : throw new KeyNotFoundException("wave bank " + bankName + " not found");
    }

    /// <summary>Writes the wave a cue plays to a WAV file (vgmstream decodes XMA); returns the wave's entry.</summary>
    public static XwbEntry ExtractCue(Workspace ws, MusicCue cue, string wavPath, string? vgmstream)
    {
        if (cue.Wave < 0 || cue.Bank == "") throw new InvalidOperationException($"{cue.Display}: its wave is not known");
        var raw = ReadStreamEntry(ws, MusicCatalog.CommonBundle, BankId(ws, cue.Bank));
        var bank = XwbFile.Read(raw);
        AudioService.ExtractWav(bank, raw, cue.Wave, wavPath, vgmstream);
        return bank.Entries[cue.Wave];
    }

    static readonly Dictionary<string, List<WaveInfo>> InfoCache = new();

    /// <summary>Format of one wave as stored (from the bank's header and entry table only, not its audio).</summary>
    public sealed record WaveInfo(string Codec, int Channels, int Rate, double Seconds, bool Loops, int Bytes, int Samples = 0, int LoopStart = 0)
    {
        public override string ToString() => $"XWB {Codec} {Channels} ch {Rate} Hz, {Seconds:F1} s{(Loops ? ", loops" : "")}";
    }

    /// <summary>The format of every wave of a music bank (cached per stream file version).</summary>
    public static List<WaveInfo> BankInfo(Workspace ws, string bankName) => BankInfoAt(ws, ws.Game.StreamPath(MusicCatalog.CommonBundle), bankName);

    /// <summary>The same for the untouched game (the workspace's original folder), or null when it is not there.</summary>
    public static List<WaveInfo>? OriginalBankInfo(Workspace ws, string bankName)
    {
        var p = ws.Original.StreamPath(MusicCatalog.CommonBundle);
        if (!File.Exists(p)) return null;
        try { return BankInfoAt(ws, p, bankName); } catch (Exception) { return null; }
    }

    static List<WaveInfo> BankInfoAt(Workspace ws, string path, string bankName)
    {
        uint id = BankId(ws, bankName);
        lock (InfoCache)
            lock (ws.StreamFileLock(MusicCatalog.CommonBundle))
            {
                string key = path + "|" + new FileInfo(path).LastWriteTimeUtc.Ticks + "|" + bankName;
                if (InfoCache.TryGetValue(key, out var cached)) return cached;
                using var f = File.OpenRead(path);
                var head = new byte[20]; f.ReadExactly(head);
                int count = BE.S32(head, 8), ndep = BE.S32(head, 16);
                var table = new byte[4 * ndep + 12 * count]; f.ReadExactly(table);
                long off = -1;
                for (int i = 0; i < count; i++) if (BE.U32(table, 4 * ndep + 12 * i) == id) off = BE.S32(table, 4 * ndep + 12 * i + 4);
                if (off < 0) throw new KeyNotFoundException($"wave bank {bankName} not in {path}");
                var hdr = new byte[0x34]; f.Position = off; f.ReadExactly(hdr);
                int bankOff = BE.S32(hdr, 12), metaOff = BE.S32(hdr, 20), metaLen = BE.S32(hdr, 24);
                var bank = new byte[8]; f.Position = off + bankOff; f.ReadExactly(bank);
                int n = BE.S32(bank, 4);
                var meta = new byte[Math.Max(metaLen, 24 * n)]; f.Position = off + metaOff; f.ReadExactly(meta);
                var list = new List<WaveInfo>();
                for (int i = 0; i < n; i++)
                {
                    var e = new XwbEntry { FlagsDuration = BE.U32(meta, 24 * i), Format = BE.U32(meta, 24 * i + 4), LoopStart = BE.U32(meta, 24 * i + 16), LoopLength = BE.U32(meta, 24 * i + 20) };
                    list.Add(new WaveInfo(e.CodecName, e.Channels, e.SampleRate, e.Seconds, e.LoopLength > 0, BE.S32(meta, 24 * i + 12), e.DurationSamples, (int)e.LoopStart));
                }
                InfoCache[key] = list;
                return list;
            }
    }

    /// <summary>The format of the wave a cue plays, or null.</summary>
    public static WaveInfo? InfoOf(Workspace ws, MusicCue cue)
    {
        if (cue.Wave < 0 || cue.Bank == "") return null;
        try { var l = BankInfo(ws, cue.Bank); return cue.Wave < l.Count ? l[cue.Wave] : null; } catch (Exception) { return null; }
    }

    /// <summary>The wave entry a cue plays (format, length).</summary>
    public static XwbEntry EntryOf(Workspace ws, MusicCue cue) =>
        XwbFile.Read(ReadStreamEntry(ws, MusicCatalog.CommonBundle, BankId(ws, cue.Bank))).Entries[cue.Wave];

    /// <summary>Decodes any audio file to interleaved 16-bit samples (WAV directly when it is PCM16, else vgmstream).</summary>
    public static (short[] Samples, int Channels, int Rate) Decode(string file, string? vgmstream, CancellationToken ct = default)
    {
        if (Path.GetExtension(file).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            try { return Wav.Read(file); } catch (Exception) { }
        if (vgmstream == null || !File.Exists(vgmstream)) throw new FileNotFoundException("vgmstream-cli.exe is needed to read " + Path.GetExtension(file) + " files", vgmstream);
        var tmp = Path.Combine(Path.GetTempPath(), $"nb_dec_{Guid.NewGuid():N}.wav");
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(Path.GetFullPath(vgmstream), $"-o \"{tmp}\" \"{file}\"");
            var (code, err) = MusicEncoder.RunTool(psi, MusicEncoder.DecodeTimeout, ct);   // killed on a hang or when cancelled
            if (code != 0 || !File.Exists(tmp)) throw new InvalidOperationException($"{Path.GetFileName(file)} could not be read: {err.Trim()}");
            return Wav.Read(tmp);
        }
        finally { try { File.Delete(tmp); } catch (Exception) { } }
    }

    /// <summary>Channel count and sample rate conversion (cubic interpolation).</summary>
    public static short[] Convert(short[] s, int ch, int rate, int toCh, int toRate)
    {
        ch = Math.Max(1, ch);
        int frames = s.Length / ch;
        var mixed = new float[(long)frames * toCh];
        for (int i = 0; i < frames; i++)
            for (int c = 0; c < toCh; c++)
                mixed[(long)i * toCh + c] = ch == 1 ? s[i] : toCh == 1 ? (s[i * ch] + s[i * ch + 1]) * 0.5f : s[i * ch + Math.Min(c, ch - 1)];
        if (rate == toRate || frames == 0)
        {
            var r0 = new short[mixed.Length];
            for (long i = 0; i < mixed.Length; i++) r0[i] = (short)Math.Clamp(MathF.Round(mixed[i]), -32768, 32767);
            return r0;
        }
        long outFrames = (long)frames * toRate / rate;
        var res = new short[outFrames * toCh];
        double step = rate / (double)toRate;
        float S(long i, int c) => mixed[Math.Clamp(i, 0, frames - 1) * toCh + c];
        for (long j = 0; j < outFrames; j++)
        {
            double x = j * step; long i = (long)x; float t = (float)(x - i);
            for (int c = 0; c < toCh; c++)
            {
                float p0 = S(i - 1, c), p1 = S(i, c), p2 = S(i + 1, c), p3 = S(i + 2, c);
                float v = p1 + 0.5f * t * (p2 - p0 + t * (2 * p0 - 5 * p1 + 4 * p2 - p3 + t * (3 * (p1 - p2) + p3 - p0)));
                res[j * toCh + c] = (short)Math.Clamp(MathF.Round(v), -32768, 32767);
            }
        }
        return res;
    }

    /// <summary>Replace… options: size (mono, 22.05 kHz), loop (from a time to the end, or as the original: off for
    /// waves that play once), length fitting, and further waves of the same bank to fill with the same audio (Showdown
    /// Town's six district stems: "all districts").</summary>
    public sealed class ReplaceOptions
    {
        public bool FitLength;
        public bool Mono;
        public bool HalfRate;
        /// <summary>null: loop when the original game's wave loops; true / false: force.</summary>
        public bool? Loop;
        public double LoopFromSeconds;
        public int[] AlsoWaves = Array.Empty<int>();
    }

    /// <summary>A replacement ready to write: the encoded wave for each listed wave of one bank.</summary>
    public sealed class PreparedReplace
    {
        public MusicCue Cue = null!;
        public string File = "";
        public readonly List<(int Wave, EncodedWave Encoded, bool Loop, int LoopStart)> Waves = new();
        public string Summary = "";
    }

    /// <summary>
    /// The slow part of Replace… (decode, convert, encode), touching no workspace file but reads: run it on a worker.
    /// The format, length (Fit) and loop default come from the untouched game's wave (the workspace's original folder; the
    /// workspace's own when there is none), so earlier replacements don't change them; with "all districts" the clicked
    /// cue's original wave sets them for every listed wave, so the six town stems stay in step (one encode for all).
    /// A loop start at or past the end of the new wave is refused. <paramref name="ct"/> stops it (and kills a decoder or
    /// encoder that runs) with OperationCanceledException.
    /// </summary>
    public static PreparedReplace PrepareReplace(Workspace ws, MusicCue cue, string file, string? vgmstream, ReplaceOptions o, MusicEncoder.ExternalEncoder? xma2 = null, CancellationToken ct = default)
    {
        if (cue.Wave < 0 || cue.Bank == "") throw new InvalidOperationException($"{cue.Display}: its wave is not known");
        var cur = BankInfo(ws, cue.Bank);
        var orig = OriginalBankInfo(ws, cue.Bank);
        WaveInfo Ref(int wave) => orig != null && wave < orig.Count ? orig[wave] : cur[wave];
        var (s, ch, rate) = Decode(file, vgmstream, ct);
        var p = new PreparedReplace { Cue = cue, File = file };
        var waves = o.AlsoWaves.Prepend(cue.Wave).Distinct().Where(w => w >= 0 && w < cur.Count).ToList();
        var shared = o.AlsoWaves.Length > 0 ? Ref(cue.Wave) : null;
        var encoded = new Dictionary<(int Ch, int Rate, long Frames, int Codec), EncodedWave>();
        var lines = new List<string>();
        foreach (int wave in waves)
        {
            var r = shared ?? Ref(wave);
            int toCh = o.Mono ? 1 : Math.Clamp(r.Channels, 1, 2), toRate = r.Rate > 0 ? r.Rate : 44100;
            if (o.HalfRate) toRate = Math.Max(8000, toRate / 2);
            long frames = o.FitLength && r.Samples > 0 ? (long)Math.Round((double)r.Samples * toRate / Math.Max(1, r.Rate)) : -1;
            int codec = cur[wave].Codec == "XMA2" && xma2 != null ? 1 : 0;
            var key = (toCh, toRate, frames, codec);
            ct.ThrowIfCancellationRequested();
            if (!encoded.TryGetValue(key, out var enc))
            {
                var conv = Convert(s, ch, rate, toCh, toRate);
                ct.ThrowIfCancellationRequested();
                if (frames > 0 && conv.Length > 0)
                {
                    var fit = new short[frames * toCh];
                    for (long i = 0; i < fit.Length; i++) fit[i] = conv[i % conv.Length];
                    conv = fit;
                }
                // the encoder slot: the original's codec when an encoder for it is installed (XMA2), else PCM16 (plays in place of XMA2)
                enc = encoded[key] = MusicEncoder.Encode(conv, toCh, toRate, codec == 1 ? xma2 : null, ct);
            }
            bool loop = o.Loop ?? r.Loops;
            int ls = (int)Math.Round(o.LoopFromSeconds * toRate);
            if (loop && ls >= enc.Samples)
                throw new InvalidOperationException($"The loop start ({o.LoopFromSeconds:0.##} s) is at or past the end of the new track ({enc.Samples / (double)toRate:0.##} s). Set 'from' to an earlier time (0 loops the whole track).");
            p.Waves.Add((wave, enc, loop, loop ? ls : 0));
            var c = cur[wave];
            lines.Add($"#{wave}: {c.Codec} {c.Channels} ch {c.Rate} Hz {c.Seconds:F1} s → {enc.CodecName} {toCh} ch {toRate} Hz {enc.Samples / (double)toRate:F1} s{(loop ? $", loops from {ls / (double)toRate:F2} s" : ", plays once")}");
        }
        p.Summary = $"{cue.Display} ({cue.Bank}) from {Path.GetFileName(file)}: {string.Join("; ", lines)}";
        return p;
    }

    /// <summary>
    /// The write of Replace…: loads the common stream archive, puts the prepared waves in, and saves it — one
    /// <see cref="Workspace.EditStream"/>, so it re-reads the archive after any other writer (a second replacement, a part
    /// build on a worker …) has saved, and no other writer can save in between. NB Studio runs it on the UI thread, so its
    /// undo step holds the whole write. Refused when the disk lacks room for the temporary copy and the history copies
    /// (about three times the archive).
    /// </summary>
    public static string ApplyReplace(Workspace ws, PreparedReplace p)
    {
        uint id = BankId(ws, p.Cue.Bank);   // before the edit lock (BankId takes its cache lock, then the file lock)
        var path = ws.Game.StreamPath(MusicCatalog.CommonBundle);
        long size = new FileInfo(path).Length;
        try
        {
            long free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
            if (free < 3 * size) throw new IOException($"Not enough free disk space to rewrite the music bundle ({free / 1e9:F1} GB free, about {3 * size / 1e9:F1} GB needed).");
        }
        catch (ArgumentException) { }
        string warn = "";
        ws.EditStream(MusicCatalog.CommonBundle, arch =>
        {
            var entry = arch.Entries.First(e => e.Id == id && e.Kind == "xwb");
            int sizeBefore = entry.Data!.Length;
            var bank = XwbFile.Read(entry.Data!);
            foreach (var (wave, enc, loop, ls) in p.Waves) bank.Entries[wave].SetEncoded(enc, loop, ls);
            entry.Data = bank.Write();
            XwbFile.Read(entry.Data);
            warn = !bank.Streaming && entry.Data.Length > sizeBefore + (1 << 20)
                ? $" WARNING: {p.Cue.Bank} is loaded into memory (not streamed) and grew from {sizeBefore / 1048576.0:F1} to {entry.Data.Length / 1048576.0:F1} MB; the console has little memory — use Mono / 22 kHz or a shorter track." : "";
            return $"music: {p.Cue.Display} ({p.Cue.Bank}) replaced with {Path.GetFileName(p.File)}";
        });
        return p.Summary + "." + warn;
    }

    /// <summary>Replace… in one go (scripts, the command line): <see cref="PrepareReplace"/> then <see cref="ApplyReplace"/>.</summary>
    public static string ReplaceCue(Workspace ws, MusicCue cue, string file, string? vgmstream, ReplaceOptions o, MusicEncoder.ExternalEncoder? xma2 = null) =>
        ApplyReplace(ws, PrepareReplace(ws, cue, file, vgmstream, o, xma2));

    public static string ReplaceCue(Workspace ws, MusicCue cue, string file, string? vgmstream, bool fitLength, MusicEncoder.ExternalEncoder? xma2 = null) =>
        ReplaceCue(ws, cue, file, vgmstream, new ReplaceOptions { FitLength = fitLength }, xma2);
}
