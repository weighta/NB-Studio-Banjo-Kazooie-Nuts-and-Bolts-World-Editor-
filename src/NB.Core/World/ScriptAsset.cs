using System.Text;
using NB.Core.Formats;
using NB.Core.IO;
using NB.Core.Project;

namespace NB.Core.World;

/// <summary>
/// aid_script_* assets: a header (first u32 = header size) followed by a flat list of commands
/// <c>(u32 size, u32 opcode, arguments…)</c>. Every opcode has a fixed size; scripts contain no pointers,
/// so commands can be removed or inserted and the asset resized (all 702 scripts parse exactly).
/// Known opcodes (from reading the start-of-game script): 0x00 end, 0x01 load level geometry, 0x02 spawn player
/// objparams, 0x48 set game flag (string), 0x4E load marker sets, 0x50 debug print (string), 0x5A run sub-script,
/// 0x60 start challenge, 0x86 go to script (level transition). The rest are **unknown** and preserved.
/// </summary>
public sealed class ScriptAsset
{
    public byte[] Header = Array.Empty<byte>();
    public List<ScriptCommand> Commands = new();

    public static ScriptAsset Parse(byte[] d)
    {
        int hs = BE.S32(d, 0);
        var s = new ScriptAsset { Header = d[..hs] };
        int o = hs;
        while (o + 8 <= d.Length)
        {
            int size = BE.S32(d, o);
            if (size < 8 || o + size > d.Length) throw new InvalidDataException($"script command at 0x{o:X} has bad size {size}");
            s.Commands.Add(new ScriptCommand { Offset = o, Data = d[o..(o + size)] });
            o += size;
        }
        if (o != d.Length) throw new InvalidDataException("trailing bytes after script commands");
        return s;
    }

    public byte[] Write()
    {
        var ms = new MemoryStream();
        ms.Write(Header);
        foreach (var c in Commands) ms.Write(c.Data);
        return ms.ToArray();
    }
}

public sealed class ScriptCommand
{
    public int Offset;
    public byte[] Data = Array.Empty<byte>();
    public int Op => BE.S32(Data, 4);
    public uint Arg(int i) => BE.U32(Data, 8 + 4 * i);
    public string Text => Encoding.Latin1.GetString(Data, 8, Data.Length - 8).Split('\0').FirstOrDefault(x => x.Length > 3) ?? "";

    public string Describe(Func<uint, string?> name) => Op switch
    {
        0x00 => "end",
        0x02 => $"spawn player {name(Arg(0)) ?? Arg(0).ToString("X8")}",
        0x48 => $"set flag {Text}",
        0x4E => "load markers " + string.Join(", ", Enumerable.Range(0, (Data.Length - 8) / 4).Select(i => name(Arg(i)) ?? Arg(i).ToString("X8"))),
        0x50 => $"print \"{Text}\"",
        0x5A => $"run script {name(Arg(0)) ?? Arg(0).ToString("X8")}",
        0x60 => $"start challenge \"{Text}\"",
        0x86 => $"go to {name(Arg(0)) ?? Arg(0).ToString("X8")}",
        _ => $"op 0x{Op:X2} ({Data.Length} bytes) [unknown]",
    };
}

/// <summary>Test-mode patches that shorten the path to gameplay (for testing mods in Xenia).</summary>
public static class TestMode
{
    /// <summary>Showdown Town intro steps pre-set by <see cref="PresetTownIntro"/> (the town then starts at "receive game globe").
    /// Not "…_Intro_01_AlreadyPlayingMusic": the town's time-of-day scripts start the district music (op 0x85) only while
    /// it is clear, so presetting it gave test games a silent town.</summary>
    public static readonly string[] TownIntroFlags =
    {
        "gameFlag_Normal_ShowdownTown_Intro_01_SeenAnimatedIntro",
        "gameFlag_Normal_ShowdownTown_Intro_02_GivenCrateToMumbo", "gameFlag_Normal_ShowdownTown_Intro_03_FlippedVehicle",
        "gameFlag_Normal_ShowdownTown_Intro_04_EnabledGetIn", "gameFlag_Normal_ShowdownTown_Intro_04_GotInVehicle",
        "gameFlag_Normal_ShowdownTown_Intro_05_FinishedDriving", "gameFlag_Normal_ShowdownTown_Intro_06_ReachedLOG",
        "gameFlag_Normal_ShowdownTown_Intro_07_SeenMapSpeedo",
    };

    /// <summary>Inserts "set flag" commands for arbitrary game flags before the start script's level transition.</summary>
    public static string PresetFlags(Workspace ws, IEnumerable<string> flags)
    {
        var caff = ws.LoadResident(CommonBundle);
        int sym = caff.Symbols.FindIndex(s => s.StartsWith(StartScript + ",")) + 1;
        var part = caff.PartsOf(sym).First(p => caff.SectionOf(p).Name == ".data");
        var script = ScriptAsset.Parse(part.Data);
        var template = script.Commands.First(c => c.Op == 0x48);
        int go = script.Commands.FindIndex(c => c.Op == 0x86);
        int added = 0;
        foreach (var flag in flags)
        {
            if (script.Commands.Any(c => c.Op == 0x48 && c.Text == flag)) continue;
            if (flag.Length >= 64 || !flag.StartsWith("gameFlag_")) throw new ArgumentException("not a game flag: " + flag);
            var d = (byte[])template.Data.Clone();
            Array.Clear(d, 8, 64);
            Encoding.ASCII.GetBytes(flag).CopyTo(d, 8);
            script.Commands.Insert(go++, new ScriptCommand { Data = d });
            added++;
        }
        if (added == 0) return "flags already preset";
        part.Data = script.Write();
        ws.SaveResident(CommonBundle, caff, $"TEST MODE: preset {added} game flag(s) at new game");
        return $"preset {added} game flag(s)";
    }

    /// <summary>TEST MODE: removes preset "set flag" commands for these flags from the start script.</summary>
    public static string UnpresetFlags(Workspace ws, IEnumerable<string> flags)
    {
        var caff = ws.LoadResident(CommonBundle);
        int sym = caff.Symbols.FindIndex(s => s.StartsWith(StartScript + ",")) + 1;
        var part = caff.PartsOf(sym).First(p => caff.SectionOf(p).Name == ".data");
        var script = ScriptAsset.Parse(part.Data);
        var set = new HashSet<string>(flags);
        int removed = script.Commands.RemoveAll(c => c.Op == 0x48 && c.Text != null && set.Contains(c.Text));
        if (removed == 0) return "none of these flags were preset";
        part.Data = script.Write();
        ws.SaveResident(CommonBundle, caff, $"TEST MODE: removed {removed} preset game flag(s)");
        return $"removed {removed} preset game flag(s)";
    }

    /// <summary>Inserts "set flag" commands (cloned from an existing one) before the level transition of the start script.</summary>
    public static string PresetTownIntro(Workspace ws)
    {
        var caff = ws.LoadResident(CommonBundle);
        int sym = caff.Symbols.FindIndex(s => s.StartsWith(StartScript + ",")) + 1;
        var part = caff.PartsOf(sym).First(p => caff.SectionOf(p).Name == ".data");
        var script = ScriptAsset.Parse(part.Data);
        var template = script.Commands.First(c => c.Op == 0x48);
        // workspaces prepared by earlier versions preset "…_Intro_01_AlreadyPlayingMusic", which keeps the town silent
        int dropped = script.Commands.RemoveAll(c => c.Op == 0x48 && c.Text == "gameFlag_Normal_ShowdownTown_Intro_01_AlreadyPlayingMusic");
        int go = script.Commands.FindIndex(c => c.Op == 0x86);
        int added = 0;
        foreach (var flag in TownIntroFlags)
        {
            if (script.Commands.Any(c => c.Op == 0x48 && c.Text == flag)) continue;
            if (flag.Length >= 64) throw new InvalidDataException("flag name too long: " + flag);
            var d = (byte[])template.Data.Clone();
            Array.Clear(d, 8, 64);
            Encoding.ASCII.GetBytes(flag).CopyTo(d, 8);
            script.Commands.Insert(go++, new ScriptCommand { Data = d });
            added++;
        }
        if (added == 0 && dropped == 0) return "town intro flags already preset";
        part.Data = script.Write();
        ws.SaveResident(CommonBundle, caff, $"TEST MODE: preset {added} Showdown Town intro flags" + (dropped > 0 ? " (the town music flag removed)" : ""));
        return $"preset {added} Showdown Town intro flags" + (dropped > 0 ? "; removed the old AlreadyPlayingMusic preset (the town has its music again)" : "");
    }

    /// <summary>
    /// Retargets the start-of-game script's final "go to" (op 0x86) so a new game starts in another script, e.g.
    /// "aid_script_banjox_nuttyacres_act1_main" (a world act) or "aid_script_banjox_showdowntown_midday" (default).
    /// </summary>
    public static string StartIn(Workspace ws, string scriptName)
    {
        var caff = ws.LoadResident(CommonBundle);
        int sym = caff.Symbols.FindIndex(s => s.StartsWith(StartScript + ",")) + 1;
        var part = caff.PartsOf(sym).First(p => caff.SectionOf(p).Name == ".data");
        var script = ScriptAsset.Parse(part.Data);
        var go = script.Commands.FirstOrDefault(c => c.Op == 0x86) ?? throw new InvalidDataException("start script has no go-to command");
        uint id = AssetIds.IdOf(scriptName) ?? throw new ArgumentException("not an asset name: " + scriptName);
        if (!caff.Symbols.Any(s => AssetIds.IdOf(s) == id)) throw new ArgumentException($"{scriptName} is not in the common bundle");
        uint old = go.Arg(0);
        BE.W32(go.Data, 8, id);
        part.Data = script.Write();
        ws.SaveResident(CommonBundle, caff, $"TEST MODE: new game starts in {scriptName}");
        return $"go-to 0x{old:X8} → 0x{id:X8} ({scriptName})";
    }

    public const uint CommonBundle = 0x685374;
    const string StartScript = "aid_script_banjox_spiralmountain_startofgame";

    /// <summary>
    /// Removes the start-of-game animated sequences and the Spiral Mountain tutorial challenge, so a new game
    /// sets the "seen start of game" flags and goes straight to Showdown Town. Returns a description.
    /// </summary>
    public static string SkipStartOfGame(Workspace ws, bool keepShowdownTownIntro = true)
    {
        var caff = ws.LoadResident(CommonBundle);
        int sym = caff.Symbols.FindIndex(s => s.StartsWith(StartScript + ",")) + 1;
        if (sym == 0) throw new InvalidDataException("start-of-game script not found in the common bundle");
        var part = caff.PartsOf(sym).First(p => caff.SectionOf(p).Name == ".data");
        var script = ScriptAsset.Parse(part.Data);
        int firstFlag = script.Commands.FindIndex(c => c.Op == 0x48);
        int firstSeq = script.Commands.FindIndex(c => c.Op == 0x5A && script.Commands.IndexOf(c) < firstFlag);
        if (firstFlag < 0 || firstSeq < 0)
        {
            if (keepShowdownTownIntro) return "already patched (no animated sequences before the flags)";
            int n = script.Commands.RemoveAll(c => c.Op == 0x48 && c.Text.Contains("ShowIntroMasterFlag"));
            if (n == 0) return "already patched";
            part.Data = script.Write();
            ws.SaveResident(CommonBundle, caff, "TEST MODE: Showdown Town intro disabled (ShowIntroMasterFlag not set)");
            return "Showdown Town intro disabled";
        }
        // drop everything from the debug print before the first sequence up to the first flag command
        int from = firstSeq > 0 && script.Commands[firstSeq - 1].Op == 0x50 ? firstSeq - 1 : firstSeq;
        while (from > 0 && script.Commands[from - 1].Op is 0x71) from--;
        var removed = script.Commands.GetRange(from, firstFlag - from);
        script.Commands.RemoveRange(from, firstFlag - from);
        if (!keepShowdownTownIntro)
            script.Commands.RemoveAll(c => c.Op == 0x48 && c.Text.Contains("ShowIntroMasterFlag"));
        part.Data = script.Write();
        ws.SaveResident(CommonBundle, caff, $"TEST MODE: removed {removed.Count} start-of-game commands (intro sequences + tutorial challenge)");
        return $"removed {removed.Count} commands: " + string.Join("; ", removed.Select(c => c.Describe(_ => null)));
    }
}
