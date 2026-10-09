using System.Text;
using NB.Core.Formats;
using NB.Core.IO;
using NB.Core.Project;
using NB.Core.World;

namespace NB.Core.Audio;

/// <summary>
/// XACT 3 sound bank (big-endian "KBDS" = SDBK, tool/format version 43), read just far enough to know which wave each
/// cue plays: cue names, simple cues (one sound), complex cues with an interactive variation table (a cue variable's
/// value picks the sound) and the play-wave events of complex sounds. docs/FORMATS.md "Music regions and tracks".
/// Header: +0x13 u16 simple cues, +0x15 u16 complex cues, +0x19 u16 cues, +0x1B u8 wave banks, +0x1C u16 sounds,
/// +0x22 u32 simple cue table (5 bytes: u8 flags, u32 sound), +0x26 u32 complex cue table (15 bytes: u8 flags, u32 sound
/// or variation table, u32 transition, …), +0x2A u32 cue names (NUL separated, simple then complex), +0x32 variation
/// tables, +0x3A wave bank names (64 bytes each), +0x46 sounds, +0x4A name[64].
/// Sound: u8 flags (1 = complex), u16 category, u8 volume, u16 pitch, u8 priority, u16 size; simple: u16 wave, u8 bank;
/// complex: u8 tracks, [flags &amp; 0x0E: u16 size of the RPC/DSP block incl. itself], tracks (u8 volume, u32 events);
/// events: u8 count, then u32 header (type = low 5 bits; 1 = play wave: +8 u16 wave, +10 u8 bank).
/// Interactive variation table: u16 flags (low 3 bits 3), u16 count, u16 -1, u16 variable index, then count x
/// (u32 sound, f32 min, f32 max, u32 flags).
/// </summary>
public sealed class XsbFile
{
    public string Name = "";
    public readonly List<string> WaveBanks = new();
    public readonly List<XsbCue> Cues = new();

    public static bool Is(ReadOnlySpan<byte> d) => d.Length > 0x8A && d[0] == 'K' && d[1] == 'B' && d[2] == 'D' && d[3] == 'S';

    public static XsbFile Read(byte[] d)
    {
        if (!Is(d)) throw new InvalidDataException("not a big-endian XACT sound bank");
        var x = new XsbFile { Name = BE.CStr(d, 0x4A, 64) };
        int simple = BE.U16(d, 0x13), complex = BE.U16(d, 0x15), cues = BE.U16(d, 0x19), banks = d[0x1B];
        int so = BE.S32(d, 0x22), co = BE.S32(d, 0x26), no = BE.S32(d, 0x2A), wbo = BE.S32(d, 0x3A);
        for (int i = 0; i < banks; i++) x.WaveBanks.Add(BE.CStr(d, wbo + 64 * i, 64));
        var names = new List<string>();
        for (int p = no; names.Count < cues && p < d.Length;)
        {
            int e = Array.IndexOf(d, (byte)0, p); if (e < 0) e = d.Length;
            names.Add(Encoding.Latin1.GetString(d, p, e - p)); p = e + 1;
        }
        string Nm(int i) => i < names.Count ? names[i] : $"cue {i}";
        for (int i = 0; i < simple; i++)
            x.Cues.Add(new XsbCue { Name = Nm(i), Variations = { new XsbVariation(0, 0, SoundWaves(d, BE.S32(d, so + 5 * i + 1))) } });
        for (int i = 0; i < complex; i++)
        {
            int o = co + 15 * i; byte flags = d[o]; int at = BE.S32(d, o + 1);
            var cue = new XsbCue { Name = Nm(simple + i) };
            if ((flags & 0x04) != 0) cue.Variations.Add(new XsbVariation(0, 0, SoundWaves(d, at)));
            else if (at > 0 && at + 8 <= d.Length)
            {
                int vflags = BE.U16(d, at), n = BE.U16(d, at + 2);
                cue.VariableIndex = BE.U16(d, at + 6);
                if ((vflags & 7) == 3)
                    for (int k = 0; k < n; k++)
                    {
                        int e = at + 8 + 16 * k;
                        cue.Variations.Add(new XsbVariation(BE.F32(d, e + 4), BE.F32(d, e + 8), SoundWaves(d, BE.S32(d, e))));
                    }
            }
            x.Cues.Add(cue);
        }
        return x;
    }

    /// <summary>The (wave, bank) pairs a sound plays.</summary>
    static List<(int Wave, int Bank)> SoundWaves(byte[] d, int o)
    {
        var res = new List<(int, int)>();
        if (o <= 0 || o + 12 > d.Length) return res;
        byte flags = d[o];
        if ((flags & 1) == 0) { res.Add((BE.U16(d, o + 9), d[o + 11])); return res; }
        int tracks = d[o + 9], p = o + 10;
        if ((flags & 0x0E) != 0) p += BE.U16(d, p);
        for (int t = 0; t < tracks && p + 5 <= d.Length; t++, p += 5)
        {
            int ev = BE.S32(d, p + 1);
            if (ev <= 0 || ev >= d.Length) continue;
            int count = d[ev], q = ev + 1;
            for (int k = 0; k < count && q + 11 <= d.Length; k++)
            {
                uint h = BE.U32(d, q);
                if ((h & 0x1F) != 1) break;                 // only play-wave events are needed (all the game's music uses them)
                res.Add((BE.U16(d, q + 8), d[q + 10]));
                q += 16;
            }
        }
        return res;
    }
}

public sealed class XsbCue
{
    public string Name = "";
    /// <summary>Cue variable that picks the variation (interactive cues), else -1.</summary>
    public int VariableIndex = -1;
    public readonly List<XsbVariation> Variations = new();
}

public sealed record XsbVariation(float Min, float Max, List<(int Wave, int Bank)> Waves);

/// <summary>A music "event" of the game's cue list (aid_xcuelist_banjox_default): what level scripts and objects name by
/// hash. It plays the XACT cue <see cref="XactCue"/>, for interactive cues with the cue variable interactive_music set to
/// <see cref="Variable"/>; <see cref="Wave"/> is the wave of the music bank that this gives.</summary>
public sealed class MusicCue
{
    public uint Hash;
    /// <summary>The XACT cue (e.g. "Showdown_Town_Inter").</summary>
    public string XactCue = "";
    /// <summary>The event's own name (e.g. "Music_Interactive_Showdown_Town_Seaside").</summary>
    public string Label = "";
    public float? Variable;
    /// <summary>Wave bank (e.g. "MusicShowdownTown") and wave index, or "" / -1 when not resolved.</summary>
    public string Bank = "";
    public int Wave = -1;
    /// <summary>Offset of the record in the cue list's .data (the record is 0xB4 bytes).</summary>
    public int Offset;

    /// <summary>A readable name: "Showdown Town – Seaside", "Nutty Acres", "Ditty: Jinjo" …</summary>
    public string Display
    {
        get
        {
            var s = Label.StartsWith("Music_Interactive_") ? Label[18..] : Label.StartsWith("Music_") ? Label[6..] : Label;
            s = s.Replace("Showdown_Town_", "Showdown Town – ").Replace("_", " ");
            foreach (var (a, b) in Pretty) s = s.Replace(a, b);
            if (s.EndsWith(" Under")) s += "water";
            return s.Trim();
        }
    }
    static readonly (string, string)[] Pretty =
    {
        ("NuttyAcres", "Nutty Acres"), ("WorldofSport", "World of Sports"), ("CPUWorld", "LOGBOX 720"), ("Terrorium", "Terrarium of Terror"),
        ("SpiralMountain", "Spiral Mountain"), ("MumbosMoutain", "Mumbo's Mountain"), ("TestTrack", "Test-O-Track"), ("MainTheme", "Main Theme"),
        ("Town – LoG", "Town – L.O.G."),
    };
    public override string ToString() => Display;
}

/// <summary>
/// The game's music: the Music sound bank and the cue list of the common bundle, and the level script commands that
/// start music (op 0x19) or set a region → music table (op 0x85).
/// </summary>
public sealed class MusicCatalog
{
    public const uint CommonBundle = 0x685374;
    public const string CueListAsset = "aid_xcuelist_banjox_default";
    public XsbFile Bank = new();
    public readonly List<MusicCue> Cues = new();
    public readonly Dictionary<uint, MusicCue> ByHash = new();

    public MusicCue? Find(uint hash) => ByHash.GetValueOrDefault(hash);

    public static MusicCatalog Load(Workspace ws)
    {
        var caff = ws.LoadResident(CommonBundle);
        int sym = caff.Symbols.FindIndex(s => AssetIds.DisplayName(s) == CueListAsset) + 1;
        if (sym == 0) throw new InvalidDataException(CueListAsset + " not found in the common bundle");
        var d = caff.PartsOf(sym).First(p => caff.SectionOf(p).Name == ".data").Data;
        return Parse(d);
    }

    /// <summary>Parses the cue list's .data: header +0x18 u32 count / +0x1C u32 offset of the sound bank table (16-byte
    /// entries: u32 size, u32 offset of the KBDS, u32 name, u32 hash); the Music bank's 0xB4-byte event records follow
    /// its KBDS (u32 name, u32 label, u32 hash, …, +0x10 → (u32 wave bank stream id, variable name), +0x28 u8 sets
    /// the variable, +0x2C f32 its value).</summary>
    public static MusicCatalog Parse(byte[] d)
    {
        var cat = new MusicCatalog();
        int n = BE.S32(d, 0x18), tab = BE.S32(d, 0x1C);
        int at = -1, size = 0;
        for (int i = 0; i < n && tab + 16 * i + 16 <= d.Length; i++)
        {
            int sz = BE.S32(d, tab + 16 * i), off = BE.S32(d, tab + 16 * i + 4), nm = BE.S32(d, tab + 16 * i + 8);
            if (off > 0 && off + 4 <= d.Length && nm > 0 && nm < d.Length && Str(d, nm) == "Music") { at = off; size = sz; break; }
        }
        if (at < 0) throw new InvalidDataException("the Music sound bank is not in the cue list");
        cat.Bank = XsbFile.Read(d.AsSpan(at, size).ToArray());
        for (int p = (at + size + 3) & ~3; p + 0xB4 <= d.Length; p += 0xB4)
        {
            string? name = Str(d, BE.S32(d, p)), label = Str(d, BE.S32(d, p + 4));
            if (name == null || label == null || !label.StartsWith("Music")) break;
            var c = new MusicCue { Hash = BE.U32(d, p + 8), XactCue = name, Label = label, Offset = p };
            if (d[p + 0x28] != 0) c.Variable = BE.F32(d, p + 0x2C);
            var xc = cat.Bank.Cues.FirstOrDefault(x => x.Name == name);
            var v = xc?.Variations.FirstOrDefault(x => xc.VariableIndex < 0 || (c.Variable is float f && f >= x.Min && f <= x.Max))
                    ?? xc?.Variations.FirstOrDefault();
            if (v != null && v.Waves.Count > 0)
            {
                c.Wave = v.Waves[0].Wave;
                c.Bank = v.Waves[0].Bank < cat.Bank.WaveBanks.Count ? cat.Bank.WaveBanks[v.Waves[0].Bank] : "";
            }
            cat.Cues.Add(c); cat.ByHash.TryAdd(c.Hash, c);
        }
        return cat;
    }

    static string? Str(byte[] d, int o)
    {
        if (o <= 0 || o >= d.Length) return null;
        int e = Array.IndexOf(d, (byte)0, o);
        if (e < 0 || e == o || e - o > 96) return null;
        for (int i = o; i < e; i++) if (d[i] < 0x20 || d[i] > 0x7E) return null;
        return Encoding.ASCII.GetString(d, o, e - o);
    }
}

/// <summary>A music command of a level script: op 0x19 plays one cue (+8 hash, +0xC f32 volume), op 0x85 sets the
/// region → cue table of a music "set" (+8 u32 set: 0 Showdown Town districts, 1 World of Sports, 2 Terrarium of Terror;
/// then six (u32 region, u32 cue hash) pairs; region 0 = none).</summary>
public sealed class MusicCommand
{
    public uint Bundle;
    public string Script = "";
    public int Symbol;
    /// <summary>Offset of the command in the script's .data.</summary>
    public int Offset;
    public int Op;
    public uint Set;
    public uint Hash;
    public readonly List<(int Region, uint Hash)> Pairs = new();

    public const int OpPlay = 0x19, OpRegions = 0x85;

    /// <summary>Byte offset (in the script's .data) of the hash a slot holds: -1 = the op 0x19 cue, k = pair k.</summary>
    public int HashOffset(int slot) => slot < 0 ? Offset + 8 : Offset + 0x10 + 8 * slot;

    /// <summary>The time of day / Act / purpose from the script name ("midday", "Act 4 (night)", …).</summary>
    public string When
    {
        get
        {
            var s = AssetIds.DisplayName(Script).Replace("aid_script_banjox_", "");
            if (s.StartsWith("common_audio_")) return "world music";
            if (s.EndsWith("_general")) return "event jingle";
            foreach (var t in new[] { "morning", "midday", "afternoon", "night", "startofgame", "demo" })
                if (s.EndsWith("_" + t)) return t == "startofgame" ? "start of game" : t;
            var m = System.Text.RegularExpressions.Regex.Match(s, @"_act(\d+|ww)_main$");
            return m.Success ? (m.Groups[1].Value == "ww" ? "Act WW" : "Act " + m.Groups[1].Value) : s;
        }
    }

    /// <summary>Every music command of the scripts of a bundle (<paramref name="nameFilter"/>: a script name part).</summary>
    public static List<MusicCommand> Scan(CaffFile caff, uint bundle, Func<string, bool>? nameFilter = null)
    {
        var res = new List<MusicCommand>();
        for (int i = 0; i < caff.Symbols.Count; i++)
        {
            var name = AssetIds.DisplayName(caff.Symbols[i]);
            if (!name.StartsWith("aid_script_") || (nameFilter != null && !nameFilter(name))) continue;
            var part = caff.PartsOf(i + 1).FirstOrDefault(p => caff.SectionOf(p).Name == ".data");
            if (part == null) continue;
            // the commands from offset 0: what ScriptAsset reads as the header is the first command (Test-O-Track's music
            // command is that one)
            var d = part.Data;
            for (int o = 0; o + 8 <= d.Length;)
            {
                int size = BE.S32(d, o), op = BE.S32(d, o + 4);
                if (size < 8 || o + size > d.Length) break;
                if (op == OpPlay && size >= 12)
                    res.Add(new MusicCommand { Bundle = bundle, Script = name, Symbol = i + 1, Offset = o, Op = op, Hash = BE.U32(d, o + 8) });
                else if (op == OpRegions && size >= 0x3C)
                {
                    var m = new MusicCommand { Bundle = bundle, Script = name, Symbol = i + 1, Offset = o, Op = op, Set = BE.U32(d, o + 8) };
                    for (int k = 0; k < 6; k++) m.Pairs.Add((BE.S32(d, o + 0xC + 8 * k), BE.U32(d, o + 0x10 + 8 * k)));
                    res.Add(m);
                }
                if (op == 0) break;
                o += size;
            }
        }
        return res;
    }

    /// <summary>The music commands of a resident bundle file, read on its own (not through the workspace's bundle cache, so a
    /// worker can scan many bundles without holding them or racing the editors that use the cache).</summary>
    public static List<MusicCommand> ScanFile(GameDirectory game, uint bundle, Func<string, bool>? nameFilter = null)
    {
        var raw = File.ReadAllBytes(game.ResidentPath(bundle));
        if (NB.Core.Compression.XCompressFile.IsCompressed(raw)) raw = NB.Core.Compression.XCompressFile.Decompress(raw);
        return Scan(CaffFile.Read(raw), bundle, nameFilter);
    }

    /// <summary>The same command in a fresh scan (after a reload or a save): matched by bundle, script and offset.</summary>
    public bool SameAs(MusicCommand o) => o.Bundle == Bundle && o.Symbol == Symbol && o.Offset == Offset && o.Op == Op;

    /// <summary>Checks that the command is still where it was found (before writing into it).</summary>
    public bool StillIn(CaffFile caff)
    {
        var part = caff.PartsOf(Symbol).FirstOrDefault(p => caff.SectionOf(p).Name == ".data");
        return part != null && Offset + (Op == OpRegions ? 0x3C : 0x14) <= part.Data.Length && BE.U32(part.Data, Offset + 4) == (uint)Op;
    }

    /// <summary>The hash a slot holds in a bundle object (-1 = the op 0x19 cue).</summary>
    public uint HashIn(CaffFile caff, int slot) => BE.U32(caff.PartsOf(Symbol).First(p => caff.SectionOf(p).Name == ".data").Data, HashOffset(slot));

    /// <summary>Writes a cue hash into a script (slot as in <see cref="HashOffset"/>); the caller saves the bundle.</summary>
    public void SetHash(CaffFile caff, int slot, uint hash)
    {
        var part = caff.PartsOf(Symbol).First(p => caff.SectionOf(p).Name == ".data");
        int o = HashOffset(slot);
        if (o + 4 > part.Data.Length || BE.U32(part.Data, Offset + 4) != (uint)Op) throw new InvalidDataException($"{AssetIds.DisplayName(Script)}: the music command moved");
        BE.W32(part.Data, o, hash);
        if (slot < 0) Hash = hash; else Pairs[slot] = (Pairs[slot].Region, hash);
    }

    /// <summary>The script bundles of a world: its own bundle (and an Act's), plus the common bundle's scripts named
    /// after the world (Showdown Town's time-of-day scripts, the Acts' night music).</summary>
    public static List<MusicCommand> ForWorld(Workspace ws, IEnumerable<uint> bundles, string worldKey)
    {
        var res = new List<MusicCommand>();
        foreach (var b in bundles.Distinct())
        {
            try { res.AddRange(Scan(ws.LoadResident(b), b)); } catch (Exception) { }
        }
        if (!bundles.Contains(MusicCatalog.CommonBundle) && worldKey.Length > 0)
            try { res.AddRange(Scan(ws.LoadResident(MusicCatalog.CommonBundle), MusicCatalog.CommonBundle, n => n.Contains("_" + worldKey + "_"))); } catch (Exception) { }
        return res;
    }
}

/// <summary>
/// Music regions: Showdown Town's marker type 8 records with flag 0x200 (+0x34 u32 flags, +0x38 f32 radius, +0x84 u8
/// region number 1–6). The game tests the player's distance in X/Z only (an endless vertical cylinder), takes the first
/// region of the list that holds him, and plays that region's cue of the op 0x85 table; outside every region it plays
/// region 1's (pair 0). Verified in Xenia (2026-10-08).
/// </summary>
public static class MusicRegion
{
    public const int Type = 8;
    public const uint MusicFlag = 0x200;
    public const int FlagsOffset = 0x34, RadiusOffset = 0x38, RegionOffset = 0x84;

    public static byte[]? RecordData(MarkerAsset set, CaffFile worldCaff)
    {
        var caff = set.Caff ?? worldCaff;
        return caff.PartsOf(set.Symbol).FirstOrDefault(p => caff.SectionOf(p).Name == ".data")?.Data;
    }

    public static bool Is(MarkerRecord r, byte[] data) => r.Type == Type && r.Size > RegionOffset && (BE.U32(data, r.Offset + FlagsOffset) & MusicFlag) != 0;
    public static float Radius(MarkerRecord r, byte[] data) => BE.F32(data, r.Offset + RadiusOffset);
    public static int Region(MarkerRecord r, byte[] data) => data[r.Offset + RegionOffset];
    public static void Write(MarkerRecord r, byte[] data, float radius, int region)
    {
        BE.WF32(data, r.Offset + RadiusOffset, radius);
        data[r.Offset + RegionOffset] = (byte)Math.Clamp(region, 0, 255);
    }

    /// <summary>Showdown Town's six regions by the music their day scripts give them (op 0x85: 1 Market, 2 Docks, 3 Park,
    /// 4 Posh, 5 Seaside, 6 LoG). Region 1 has no volume of its own: it is "everywhere else" (the Town Square start).</summary>
    public static string DistrictName(int region) => region switch
    {
        1 => "Market (default)", 2 => "Docks", 3 => "Park", 4 => "Posh", 5 => "Seaside", 6 => "L.O.G.", _ => "region " + region,
    };
}
