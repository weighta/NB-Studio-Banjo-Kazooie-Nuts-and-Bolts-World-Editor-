using System.Text.RegularExpressions;
using NB.Core.IO;
using System.Numerics;
using NB.Core.Models;

namespace NB.Core.Vehicles;

/// <summary>
/// What of a vehicle part model the game shows. Part models carry their Maya node paths in the rendergraph (chunk 30):
/// 0x50-byte nodes (+0 full path, +4 short name, +8 index, +0xC type) in an array found through the header (+0x28 →
/// {u32 count, u32 array}); a draw's node is its index (draw op 0x30 +16). Two mechanisms hide geometry:
/// <list type="bullet">
/// <item><b>LOD groups</b> (header +0x140 count, +0x144 groups): level 0 is the full-detail model; nodes that only appear
/// in other levels are not drawn close up.</item>
/// <item><b>Switches</b>: a path component "…SWITCH&lt;n&gt;_&lt;k&gt;" is option k of switch n; one option of each switch is shown.
/// A switch the part does not set shows its lowest option in the model (option 0 when there is one; the trays' junk
/// SWITCH1_1 and the seat's blow-up Banjo SWITCH20_1 as the garage shows them). Wheels on a vehicle show option 1 of every
/// switch (suspension struts and springs; verified in Showdown Town). Seats share one model
/// (model_banjox_vehicleparts_seats_all) and turn switches 1–6 on from objparams +0x3DC … +0x3F0 (u32 flags: controls,
/// shared plating, airtight, strong, super, rivets).</item>
/// </list>
/// </summary>
public sealed class PartModelView
{
    public readonly List<string> Names = new();
    /// <summary>Per draw of the model: (switch, option) pairs on its node path.</summary>
    readonly List<(int Switch, int Option)>[] _drawSwitches;
    readonly bool[] _lodHidden;

    static readonly Regex Sw = new(@"SWITCH(\d+)_(\d+)", RegexOptions.Compiled);

    PartModelView(int draws) { _drawSwitches = new List<(int, int)>[draws]; _lodHidden = new bool[draws]; }

    public static PartModelView Of(ModelAsset m)
    {
        var v = new PartModelView(m.Draws.Count);
        for (int i = 0; i < m.Draws.Count; i++) v._drawSwitches[i] = new();
        try { v.Parse(m); } catch { }
        return v;
    }

    void Parse(ModelAsset m)
    {
        if (!m.Chunks.TryGetValue(30, out int c30)) return;
        var d = m.View.Data(".data");
        int rg = BE.S32(d, c30);
        if (rg <= 0 || rg + 0x148 > d.Length || !d.AsSpan(rg, 11).SequenceEqual("rendergraph"u8)) return;
        // LOD level node pointers
        var levels = new List<List<HashSet<int>>>();   // group -> level -> node pointers
        int ng = BE.S32(d, rg + 0x140), gp = BE.S32(d, rg + 0x144);
        for (int g = 0; g < ng && gp > 0; g++)
        {
            int e = gp + 0x14 * g, nl = BE.S32(d, e), lp = BE.S32(d, e + 4);
            var lv = new List<HashSet<int>>();
            for (int k = 0; k < nl; k++)
            {
                int cnt = BE.S32(d, lp + 16 * k + 8), np = BE.S32(d, lp + 16 * k + 12);
                var set = new HashSet<int>();
                for (int i = 0; i < cnt; i++) set.Add(BE.S32(d, np + 4 * i));
                lv.Add(set);
            }
            levels.Add(lv);
        }
        // node array: header +0x28 -> {count, array}; else walk back from the lowest LOD node while the indices run down to 0
        int count = 0, arr = 0;
        int cp = BE.S32(d, rg + 0x28);
        if (cp > 0 && cp + 8 <= d.Length)
        {
            count = BE.S32(d, cp); arr = BE.S32(d, cp + 4);
            if (count <= 0 || count > 20000 || arr <= 0 || arr + 0x50L * count > d.Length || BE.S32(d, arr + 8) != 0) { count = 0; arr = 0; }
        }
        if (arr == 0 && levels.SelectMany(l => l).SelectMany(s => s).DefaultIfEmpty(0).Min() is int lo and > 0)
        {
            int o = lo;
            while (o - 0x50 >= 0 && BE.S32(d, o - 0x50 + 8) == BE.S32(d, o + 8) - 1) o -= 0x50;
            if (BE.S32(d, o + 8) == 0) { arr = o; while (arr + 0x50L * (count + 1) <= d.Length && BE.S32(d, arr + 0x50 * count + 8) == count) count++; }
        }
        if (arr == 0 || count == 0) return;
        for (int i = 0; i < count; i++)
        {
            int p = BE.S32(d, arr + 0x50 * i);
            Names.Add(p > 0 && p < d.Length ? BE.CStr(d, p, 512) : "");
        }
        // hidden by LOD: nodes of other levels that are not in level 0, and everything below them
        var lodHiddenPaths = new List<string>();
        foreach (var lv in levels)
        {
            if (lv.Count < 2) continue;
            var keep = lv[0];
            foreach (var set in lv.Skip(1))
                foreach (var ptr in set)
                    if (!keep.Contains(ptr)) { int idx = (ptr - arr) / 0x50; if (idx >= 0 && idx < Names.Count && Names[idx].Length > 0) lodHiddenPaths.Add(Names[idx]); }
        }
        for (int i = 0; i < m.Draws.Count; i++)
        {
            int n = m.Draws[i].Node;
            if (n < 0 || n >= Names.Count) continue;
            var path = Names[n];
            foreach (Match x in Sw.Matches(path))
            {
                int sw = int.Parse(x.Groups[1].Value), op = int.Parse(x.Groups[2].Value);
                _drawSwitches[i].Add((sw, op));
                _lowest[sw] = _lowest.TryGetValue(sw, out var low) ? Math.Min(low, op) : op;
            }
            foreach (var h in lodHiddenPaths)
                if (path == h || path.StartsWith(h + "|", StringComparison.Ordinal)) { _lodHidden[i] = true; break; }
        }
    }

    /// <summary>Debug / research: 0 = per part rules, 1 = every switch on option 0, 2 = option 1, 3 = switches ignored.</summary>
    public static int Rule;

    /// <summary>Is draw <paramref name="i"/> shown with these switch settings (switch → option; missing = the default)?</summary>
    public bool Visible(int i, IReadOnlyDictionary<int, int>? switches)
    {
        if (i < 0 || i >= _lodHidden.Length) return true;
        if (_lodHidden[i]) return false;
        if (Rule == 3) return true;
        foreach (var (s, o) in _drawSwitches[i])
        {
            int want = Rule == 1 ? 0 : Rule == 2 ? 1 : switches != null && switches.TryGetValue(s, out var w) ? w : _lowest.GetValueOrDefault(s);
            if (want != o) return false;
        }
        return true;
    }

    /// <summary>Per switch, its lowest option in the model: what is shown when the part does not set the switch (option 0
    /// when the model has one: the standard wheel's frame (SWITCH7..10_0) in the Parts Store; else e.g. the propellers' blades
    /// SWITCH1_1, the trays' junk SWITCH1_1 and the seat's blow-up Banjo SWITCH20_1, as the garage shows them).</summary>
    readonly Dictionary<int, int> _lowest = new();

    /// <summary>The switch options a part turns on (from its objparams).</summary>
    public static Dictionary<int, int> SwitchesOf(PartInfo p, byte[]? objparams)
    {
        var s = new Dictionary<int, int>();
        if (objparams == null) return s;
        if (p.Class == "objDefId_vehicleBlockSeat" && objparams.Length >= 0x3F4 && p.Key.StartsWith("seats_"))
            for (int i = 0; i < 6; i++) s[i + 1] = BE.U32(objparams, 0x3DC + 4 * i) != 0 ? 1 : 0;
        // wheels on a vehicle show their suspension (switches 1, 3, 4, 6 option 1: struts and coil springs) instead of
        // the boxy frame of the Parts Store picture (switches 7-10 option 0): verified in Showdown Town on an editor-made car
        if (p.IsWheel) for (int i = 0; i <= 12; i++) s[i] = 1;
        return s;
    }

    /// <summary>
    /// A wheel as it hangs in the garage: its suspension fully extended. Wheel models are skinned to BASE /
    /// WHEELFORKROTATE / WHEELFORKTRAVEL / WHEELCOGROTATE / WHEELROTATE; the bind pose has the fork pushed all the way up
    /// (the tyre half inside the part above). The travel joint and the joints under it move down by
    /// <paramref name="travel"/> (objparams +0x400), weighted per vertex, so the strut and spring stretch.
    /// </summary>
    public static List<MeshDraw> Extended(ModelAsset m, List<MeshDraw> draws, float travel)
    {
        List<Joint>? sk;
        try { sk = Skeleton.Parse(m.View.Data(".data")); } catch { sk = null; }
        int t = sk?.FindIndex(j => j.Name == "WHEELFORKTRAVEL") ?? -1;
        if (sk == null || t < 0) return draws;
        var moved = new HashSet<int> { t };
        for (bool more = true; more;)
        {
            more = false;
            foreach (var j in sk) if (j.Parent >= 0 && moved.Contains(j.Parent) && moved.Add(j.Index)) more = true;
        }
        var clone = typeof(object).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var res = new List<MeshDraw>(draws.Count);
        foreach (var d in draws)
        {
            if (d.BlendIndices == null || d.BlendWeights == null || d.BlendIndices.Length < d.Positions.Length * 4) { res.Add(d); continue; }
            var c = (MeshDraw)clone.Invoke(d, null)!;
            var pos = new Vector3[d.Positions.Length];
            for (int i = 0; i < pos.Length; i++)
            {
                float w = 0;
                for (int k = 0; k < 4; k++) if (moved.Contains(d.BlendIndices[4 * i + k])) w += d.BlendWeights[4 * i + k];
                pos[i] = d.Positions[i] - new Vector3(0, travel * Math.Clamp(w, 0, 1), 0);
            }
            c.Positions = pos;
            res.Add(c);
        }
        return res;
    }
}
