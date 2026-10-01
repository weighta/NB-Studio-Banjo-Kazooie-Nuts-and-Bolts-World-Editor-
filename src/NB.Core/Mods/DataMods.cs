using NB.Core.Formats;
using NB.Core.IO;
using NB.Core.Project;

namespace NB.Core.Mods;

/// <summary>A game setting stored in a data asset (edited in the workspace; no executable patch).</summary>
public sealed record DataSetting(string Id, string Name, string Asset, int Offset, float Default, string Description, string Verified);

/// <summary>
/// Data-only gameplay settings used by the After-Party mods. Each is one float in an asset's .data, written into every
/// resident bundle of the workspace that holds the asset.
/// </summary>
public static class DataMods
{
    public static readonly DataSetting VehicleBlockDamage = new(
        "vehicle-block-damage",
        "Vehicle block damage multiplier",
        "aid_misc_banjox_vehicleblockglobals_default", 0x4, 1.0f,
        "Every hit on a vehicle block (crashes, explosions, weapons) is multiplied by this value (default 1.0). Higher values " +
        "make vehicles lose parts and break apart much sooner; applies in every world.",
        "Verified in Xenia: 50 is read by the game at load (guest memory) and, with the Showdown Town mod on, repeated wall " +
        "rams break the trolley apart in town.");

    public static readonly IReadOnlyList<DataSetting> All = new[] { VehicleBlockDamage };

    public static float? Get(Workspace ws, AssetIndex idx, DataSetting s)
    {
        var e = idx.Entries.FirstOrDefault(x => x.Name == s.Asset && x.Symbol > 0 && !x.Streamed);
        if (e == null) return null;
        var caff = ws.LoadResident(e.Bundle);
        int sym = caff.Symbols.FindIndex(n => AssetIds.DisplayName(n) == s.Asset) + 1;
        return BE.F32(caff.PartsOf(sym).First(p => caff.SectionOf(p).Name == ".data").Data, s.Offset);
    }

    /// <summary>Writes the value into every resident bundle holding the asset; returns the number of bundles changed.</summary>
    public static int Set(Workspace ws, AssetIndex idx, DataSetting s, float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value) || value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        int n = 0;
        foreach (var b in idx.Entries.Where(x => x.Name == s.Asset && x.Symbol > 0 && !x.Streamed).Select(x => x.Bundle).Distinct())
        {
            var caff = ws.LoadResident(b);
            int sym = caff.Symbols.FindIndex(x => AssetIds.DisplayName(x) == s.Asset) + 1;
            var d = caff.PartsOf(sym).First(p => caff.SectionOf(p).Name == ".data").Data;
            float before = BE.F32(d, s.Offset);
            if (before == value) continue;
            BE.WF32(d, s.Offset, value);
            ws.SaveResident(b, caff, $"{s.Name}: {before:G6} -> {value:G6}");
            n++;
        }
        return n;
    }
}
