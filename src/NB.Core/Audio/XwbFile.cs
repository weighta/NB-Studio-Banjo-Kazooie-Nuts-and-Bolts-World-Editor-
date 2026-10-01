using NB.Core.IO;

namespace NB.Core.Audio;

/// <summary>
/// XACT3 wave bank (version 43), big-endian ("DNBW"), as stored in Bundle/50 stream archives.
///   0x00 "DNBW"  0x04 u32 version (43)  0x08 u32 header version (42)
///   0x0C 5 x (u32 offset, u32 length): BankData, EntryMetaData, SeekTables, EntryNames, EntryWaveData
///   BankData: u32 flags (bit0 = streaming), u32 entry count, char[64] name, u32 metadata element size (24),
///             u32 name element size, u32 alignment (2048), u32 compact format, u64 build time
///   Entry (24 bytes): u32 flags(4)|duration(28), u32 mini format (tag:2 ch:3 rate:18 blockAlign:8 bits16:1),
///             u32 play offset, u32 play length, u32 loop start, u32 loop length (samples)
///   SeekTables: u32 offset[count] into the table data, then per entry u32 n, u32 samplePositions[n] (XMA only)
/// Codecs in the game: XMA (tag 1) for effects/dialogue, big-endian PCM16 (tag 0) for music.
/// </summary>
public sealed class XwbFile
{
    public uint Version, HeaderVersion;
    public byte[] BankData = Array.Empty<byte>();
    public List<XwbEntry> Entries = new();
    public byte[] EntryNames = Array.Empty<byte>();

    public uint Flags => BE.U32(BankData, 0);
    public bool Streaming => (Flags & 1) != 0;
    public string Name => BE.CStr(BankData, 8, 64);
    public int Alignment => Math.Max(4, BE.S32(BankData, 80));

    public static bool Is(ReadOnlySpan<byte> d) => d.Length > 0x34 && d[0] == 'D' && d[1] == 'N' && d[2] == 'B' && d[3] == 'W';

    public static XwbFile Read(byte[] d)
    {
        if (!Is(d)) throw new InvalidDataException("not a big-endian XACT wave bank");
        var x = new XwbFile { Version = BE.U32(d, 4), HeaderVersion = BE.U32(d, 8) };
        var seg = new (int Off, int Len)[5];
        for (int i = 0; i < 5; i++) seg[i] = (BE.S32(d, 12 + 8 * i), BE.S32(d, 16 + 8 * i));
        x.BankData = d.AsSpan(seg[0].Off, seg[0].Len).ToArray();
        int count = BE.S32(x.BankData, 4), esize = BE.S32(x.BankData, 72);
        if (esize != 24) throw new NotSupportedException($"wave bank entry size {esize} not supported");
        x.EntryNames = seg[3].Len > 0 ? d.AsSpan(seg[3].Off, seg[3].Len).ToArray() : Array.Empty<byte>();
        for (int i = 0; i < count; i++)
        {
            int e = seg[1].Off + 24 * i;
            var en = new XwbEntry
            {
                FlagsDuration = BE.U32(d, e), Format = BE.U32(d, e + 4),
                LoopStart = BE.U32(d, e + 16), LoopLength = BE.U32(d, e + 20),
            };
            int off = BE.S32(d, e + 8), len = BE.S32(d, e + 12);
            en.Data = d.AsSpan(seg[4].Off + off, len).ToArray();
            if (seg[2].Len > 0)
            {
                int to = BE.S32(d, seg[2].Off + 4 * i);
                if (to >= 0)
                {
                    int p = seg[2].Off + 4 * count + to;
                    int n = BE.S32(d, p);
                    en.SeekTable = d.AsSpan(p, 4 + 4 * n).ToArray();
                }
            }
            x.Entries.Add(en);
        }
        return x;
    }

    public byte[] Write()
    {
        int count = Entries.Count;
        var ms = new MemoryStream();
        var w = new NB.Core.Formats.BEWriter(ms);
        w.Bytes(new byte[0x34]);
        int bankOff = (int)ms.Length; w.Bytes(BankData);
        int metaOff = (int)ms.Length;
        var metaPos = new List<long>();
        foreach (var _ in Entries) { metaPos.Add(ms.Length); w.Bytes(new byte[24]); }
        int seekOff = (int)ms.Length;
        if (Entries.Any(e => e.SeekTable != null))
        {
            int acc = 0;
            foreach (var e in Entries) { w.S32(e.SeekTable != null ? acc : -1); acc += e.SeekTable?.Length ?? 0; }
            foreach (var e in Entries) if (e.SeekTable != null) w.Bytes(e.SeekTable);
        }
        int seekLen = (int)ms.Length - seekOff;
        int namesOff = EntryNames.Length > 0 ? (int)ms.Length : 0;
        w.Bytes(EntryNames);
        int align = Alignment;
        while (ms.Length % align != 0) w.U8(0);
        int waveOff = (int)ms.Length;
        var offs = new int[count];
        for (int i = 0; i < count; i++)
        {
            while ((ms.Length - waveOff) % align != 0) w.U8(0);
            offs[i] = (int)(ms.Length - waveOff);
            w.Bytes(Entries[i].Data);
        }
        while ((ms.Length - waveOff) % align != 0) w.U8(0);
        int waveLen = (int)ms.Length - waveOff;
        var buf = ms.ToArray();
        buf[0] = (byte)'D'; buf[1] = (byte)'N'; buf[2] = (byte)'B'; buf[3] = (byte)'W';
        BE.W32(buf, 4, Version); BE.W32(buf, 8, HeaderVersion);
        (int, int)[] segs = { (bankOff, BankData.Length), (metaOff, 24 * count), (seekOff, seekLen), (namesOff, EntryNames.Length), (waveOff, waveLen) };
        for (int i = 0; i < 5; i++) { BE.W32(buf, 12 + 8 * i, segs[i].Item1); BE.W32(buf, 16 + 8 * i, segs[i].Item2); }
        BE.W32(buf, bankOff + 4, count);
        for (int i = 0; i < count; i++)
        {
            int p = (int)metaPos[i]; var e = Entries[i];
            BE.W32(buf, p, e.FlagsDuration); BE.W32(buf, p + 4, e.Format); BE.W32(buf, p + 8, offs[i]);
            BE.W32(buf, p + 12, e.Data.Length); BE.W32(buf, p + 16, e.LoopStart); BE.W32(buf, p + 20, e.LoopLength);
        }
        return buf;
    }
}

public sealed class XwbEntry
{
    public uint FlagsDuration, Format, LoopStart, LoopLength;
    public byte[] Data = Array.Empty<byte>();
    public byte[]? SeekTable;

    public int Codec => (int)(Format & 3);                 // 0 PCM, 1 XMA, 2 ADPCM, 3 WMA
    public int Channels => (int)((Format >> 2) & 7);
    public int SampleRate => (int)((Format >> 5) & 0x3FFFF);
    public int BlockAlign => (int)((Format >> 23) & 0xFF);
    public bool Bits16 => (Format >> 31) != 0;
    public int DurationSamples => (int)(FlagsDuration >> 4);
    public string CodecName => Codec switch { 0 => "PCM16", 1 => "XMA2", 2 => "ADPCM", _ => "WMA" };
    public double Seconds => SampleRate > 0 ? DurationSamples / (double)SampleRate : 0;

    /// <summary>Replaces this entry with big-endian 16-bit PCM (the format the game uses for music).</summary>
    public void SetPcm16(short[] interleaved, int channels, int rate, bool keepLoop)
    {
        if (channels < 1 || channels > 7) throw new ArgumentOutOfRangeException(nameof(channels));
        if (rate <= 0 || rate > 0x3FFFF) throw new ArgumentOutOfRangeException(nameof(rate));
        int frames = interleaved.Length / channels;
        var d = new byte[frames * channels * 2];
        for (int i = 0; i < frames * channels; i++) { d[2 * i] = (byte)(interleaved[i] >> 8); d[2 * i + 1] = (byte)interleaved[i]; }
        bool looped = LoopLength != 0;
        Data = d;
        Format = 0u | (uint)channels << 2 | (uint)rate << 5 | (uint)(channels * 2) << 23 | 1u << 31;
        FlagsDuration = (FlagsDuration & 0xF) | (uint)frames << 4;
        SeekTable = null;
        if (keepLoop && looped) { LoopStart = 0; LoopLength = (uint)frames; } else { LoopStart = 0; LoopLength = 0; }
    }

    /// <summary>For PCM entries: interleaved samples (host order).</summary>
    public short[] PcmSamples()
    {
        if (Codec != 0) throw new InvalidOperationException("not PCM");
        var s = new short[Data.Length / 2];
        for (int i = 0; i < s.Length; i++) s[i] = (short)(Data[2 * i] << 8 | Data[2 * i + 1]);
        return s;
    }
}
