using NB.Core.Formats;
using NB.Core.IO;
using NB.Core.World;

namespace NB.Core.Project;

/// <summary>One playable act: its main script, its own bundle and the world bundle it loads.</summary>
public sealed record ActEntry(string World, string Act, string Script, uint ActBundle, uint WorldBundle, uint BackgroundModelId)
{
    public string Display => $"{WorldCatalog.DisplayNames.GetValueOrDefault(World, World)} — {(Act == "actww" ? "Act WW" : Act.Replace("act", "Act "))}";
}

/// <summary>
/// Acts are defined by <c>aid_script_banjox_&lt;world&gt;_act&lt;N&gt;_main</c> scripts in the common bundle. Their
/// "load level" command (op 0x01) names the background model (0x04…) and the act's path-engine asset (0x4E…), whose
/// low 24 bits are the act's own bundle. That bundle's stream archive lists its dependencies, which include exactly
/// one world bundle holding the background model — the copy the game loads for the act (verified for Nutty Acres
/// act 1 = 13b0ff in Xenia).
/// </summary>
public static class ActCatalog
{
    public static List<ActEntry> Build(Workspace ws, AssetIndex idx)
    {
        var worlds = WorldCatalog.FromIndex(idx).ToDictionary(w => w.Bundle);
        var caff = ws.LoadResident(TestMode.CommonBundle);
        var list = new List<ActEntry>();
        for (int s = 1; s <= caff.Symbols.Count; s++)
        {
            var name = AssetIds.DisplayName(caff.Symbols[s - 1]);
            if (!name.StartsWith("aid_script_banjox_") || !name.EndsWith("_main")) continue;
            var parts = name["aid_script_banjox_".Length..^"_main".Length].Split('_');
            if (parts.Length != 2 || !parts[1].StartsWith("act")) continue;
            var data = caff.PartsOf(s).FirstOrDefault(p => caff.SectionOf(p).Name == ".data")?.Data;
            if (data == null) continue;
            ScriptAsset script;
            try { script = ScriptAsset.Parse(data); } catch { continue; }
            var load = script.Commands.FirstOrDefault(c => c.Op == 0x01 && c.Data.Length >= 24);
            if (load == null) continue;
            uint model = load.Arg(0), pathEngine = load.Arg(3);
            uint actBundle = pathEngine & 0xFFFFFF;
            uint worldBundle = 0;
            try { worldBundle = Dependencies(ws.Game.StreamPath(actBundle)).Select(d => d & 0xFFFFFF).FirstOrDefault(d => worlds.ContainsKey(d)); }
            catch (IOException) { }
            list.Add(new ActEntry(parts[0], parts[1], name, actBundle, worldBundle, model));
        }
        return list.OrderBy(a => a.World).ThenBy(a => a.Act).ToList();
    }

    /// <summary>Reads only the dependency list of a stream archive (header: magic, 12, count, timestamp, ndeps, deps).</summary>
    public static List<uint> Dependencies(string streamPath)
    {
        using var f = File.OpenRead(streamPath);
        var h = new byte[20];
        f.ReadExactly(h);
        if (BE.U32(h, 0) != BundleArchive.Magic) throw new InvalidDataException("not a stream archive: " + streamPath);
        int n = BE.S32(h, 16);
        var deps = new byte[4 * n];
        f.ReadExactly(deps);
        return Enumerable.Range(0, n).Select(i => BE.U32(deps, 4 * i)).ToList();
    }
}
