using NB.Core.Formats;
using NB.Core.Models;
using NB.Core.Project;

namespace NB.Core.World;

/// <summary>One way the game presents a world: a level script's light setup (op 0x52 runs it) and skydome (op 0x2C,
/// first argument = skydome model id).</summary>
public sealed class WorldLook
{
    public string Script = "";
    public LevelLighting? Light;
    public uint DomeId;
    /// <summary>Last argument of the skydome command: 1 = the dome is centred on the camera (Showdown Town, Terrarium of
    /// Terror), 0 = it stands in the world at its own coordinates (Nutty Acres' bluesky, Spiral Mountain, Jiggoseum: world-size
    /// domes that hide the scenery behind them).</summary>
    public bool DomeFollowsCamera = true;
    public string? DomeName;
    public ModelAsset? Dome;
    /// <summary>Bundle the light setup came from.</summary>
    public uint LightBundle;
    public override string ToString() => $"{Script}: light {Light?.Name ?? "-"}, sky {DomeName ?? "-"}";
}

/// <summary>
/// Finds the light setups and sky domes a world is shown with, the way the game picks them: the level scripts
/// (act scripts, time-of-day scripts, the start-of-game and multiplayer scripts in the common bundle 685374, the world
/// and act bundles) that load this world's background model (op 0x01) or are named after the world, with the light setup
/// they run (op 0x52) and the skydome they set (op 0x2C). Sky dome and light setup models are loaded from whichever
/// bundle holds them (resident, or streamed in Bundle/50) — Spiral Mountain's, Nutty Acres' and Jiggoseum's domes are not
/// in the world bundle, Banjoland and LOGBOX 720 have no skydome command at all (their sky is level geometry).
/// </summary>
public static class WorldLooks
{
    public const uint CommonBundle = 0x685374;

    public static List<WorldLook> Find(Workspace ws, AssetIndex idx, WorldScene scene, Action<string>? log = null)
    {
        var looks = new List<WorldLook>();
        uint bgId = AssetIds.IdOf(scene.Background.View.Name) ?? 0;
        string disp = AssetIds.DisplayName(scene.Background.View.Name);
        // "aid_model_banjox_background_<world>_default"
        string world = disp.StartsWith("aid_model_banjox_background_") && disp.EndsWith("_default") ? disp["aid_model_banjox_background_".Length..^"_default".Length] : "";
        var bundles = new List<uint> { scene.Bundle };
        bundles.AddRange(scene.MarkerBundles);
        if (idx.Entries.Any(e => e.Bundle == CommonBundle && !e.Streamed)) bundles.Add(CommonBundle);
        var names = new Dictionary<uint, string>();
        foreach (var e in idx.Entries) if (e.Id != 0) names.TryAdd(e.Id, e.Name);
        var seen = new HashSet<string>();
        foreach (var b in bundles.Distinct())
        {
            CaffFile c;
            try { c = ws.LoadResident(b); } catch (Exception e) { log?.Invoke($"looks: bundle {b:x6}: {e.Message}"); continue; }
            for (int s = 1; s <= c.Symbols.Count; s++)
            {
                var nm = AssetIds.DisplayName(c.Symbols[s - 1]);
                if (!nm.StartsWith("aid_script_") || nm.StartsWith("aid_script_banjox_lightsetup_") || !seen.Add(nm)) continue;
                var part = c.PartsOf(s).FirstOrDefault(p => c.SectionOf(p).Name == ".data");
                if (part == null || part.Data.Length < 8) continue;
                ScriptAsset sc;
                try { sc = ScriptAsset.Parse(part.Data); } catch { continue; }
                bool loadsWorld = sc.Commands.Any(k => k.Op == 0x01 && k.Data.Length >= 12 && k.Arg(0) == bgId);
                var lightIds = sc.Commands.Where(k => k.Op == 0x52 && k.Data.Length >= 12).Select(k => k.Arg(0))
                    .Where(id => names.TryGetValue(id, out var n) && n.StartsWith("aid_script_banjox_lightsetup_")).ToList();
                var domeCmd = sc.Commands.FirstOrDefault(k => k.Op == 0x2C && k.Data.Length >= 12);
                var dome = domeCmd?.Arg(0) ?? 0;
                bool named = world.Length > 0 && nm.Contains("_" + world + "_") && (dome != 0 || lightIds.Count > 0)
                    && lightIds.All(id => names[id].Contains(world));
                if (!loadsWorld && !named) continue;
                if (lightIds.Count == 0 && dome == 0) continue;
                var look = new WorldLook { Script = nm, DomeId = dome, DomeName = dome != 0 ? names.GetValueOrDefault(dome) : null,
                    DomeFollowsCamera = domeCmd == null || domeCmd.Data.Length < 32 || domeCmd.Arg(5) != 0 };
                if (lightIds.Count > 0)
                {
                    var lid = lightIds[0];
                    // the act bundle's own copy first (acts differ), then any resident copy
                    var entries = idx.Entries.Where(e => e.Id == lid && !e.Streamed).OrderBy(e => scene.MarkerBundles.Contains(e.Bundle) ? 0 : e.Bundle == scene.Bundle ? 1 : 2).ToList();
                    foreach (var e in entries)
                    {
                        try { look.Light = LevelLighting.Parse(ws.LoadResident(e.Bundle), e.Symbol); look.LightBundle = e.Bundle; break; }
                        catch (Exception ex) { log?.Invoke($"looks: light {e.Name}: {ex.Message}"); }
                    }
                }
                looks.Add(look);
            }
        }
        // one look per (light, dome); prefer the act's own scripts (light setup from the opened act bundle) and time of day
        var res = new List<WorldLook>();
        foreach (var l in looks.OrderBy(l => Rank(l, scene)).ThenBy(l => l.Script))
            if (!res.Any(r => r.Light?.Name == l.Light?.Name && r.DomeId == l.DomeId)) res.Add(l);
        var domes = new Dictionary<uint, ModelAsset?>();
        foreach (var l in res.Where(l => l.DomeId != 0))
        {
            if (!domes.TryGetValue(l.DomeId, out var m)) domes[l.DomeId] = m = LoadModel(ws, idx, l.DomeId, scene, log);
            l.Dome = m;
        }
        return res;
    }

    static int Rank(WorldLook l, WorldScene scene)
    {
        int r = 0;
        if (scene.MarkerBundles.Count > 0 && !scene.MarkerBundles.Contains(l.LightBundle)) r += 4;
        if (l.Light == null) r += 2;
        if (l.DomeId == 0) r += 1;
        var s = l.Script;
        if (s.Contains("_live_") || s.Contains("_demo") || s.Contains("frontend")) r += 8;
        // Showdown Town: midday first (its scripts run lightsetup_showdowntown_main)
        if (l.Light?.Name.EndsWith("_main") == true) r -= 1;
        // Spiral Mountain: the start-of-game look (blue sky) rather than the ending / race scripts
        if (s.EndsWith("_startofgame")) r -= 2;
        return r;
    }

    /// <summary>A model by asset id from a resident bundle (world / act bundles first) or a Bundle/50 stream archive.</summary>
    public static ModelAsset? LoadModel(Workspace ws, AssetIndex idx, uint id, WorldScene? scene, Action<string>? log = null)
    {
        var entries = idx.Entries.Where(e => e.Id == id && e.Type == "model")
            .OrderBy(e => e.Streamed ? 1 : 0).ThenBy(e => scene != null && (e.Bundle == scene.Bundle || scene.MarkerBundles.Contains(e.Bundle)) ? 0 : 1).ToList();
        foreach (var e in entries)
        {
            try
            {
                if (!e.Streamed) return ModelAsset.Parse(ws.LoadResident(e.Bundle), e.Symbol);
                var arch = ws.LoadStream(e.Bundle);
                var be = arch.Entries.FirstOrDefault(x => x.Id == e.Id && x.Data != null);
                if (be == null) continue;
                var c = CaffFile.Read(be.Data!);
                int sym = c.Symbols.FindIndex(s => AssetIds.IdOf(s) == id) + 1;
                if (sym > 0) return ModelAsset.Parse(c, sym);
            }
            catch (Exception ex) { log?.Invoke($"looks: model {e.Name} ({e.Bundle:x6}): {ex.Message}"); }
        }
        return null;
    }
}
