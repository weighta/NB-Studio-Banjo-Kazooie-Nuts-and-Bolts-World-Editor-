using System.Numerics;
using NB.Core.Formats;
using NB.Core.IO;
using NB.Core.Project;
using NB.Core.World;

namespace NB.Core.Net;

/// <summary>
/// "Showdown Town Freewheel": turns the Xbox LIVE free-roam game "Freewheel Festival" (task entry
/// worldbanjoland/freewheel, script aid_script_banjox_live_banjoland_freewheel) into free roam in Showdown Town,
/// so a party started from the multiplayer lobby drives around the town together with the game's own netcode.
///
/// How a live game loads its world (see docs/FORMATS.md): the script header names its bundle (fd97da), whose manifest
/// dependencies pull in the world bundle (Banjoland 077143); op 0x4E loads marker sets (start grid, pickups), op 0x2C a
/// skydome, op 0x01 the level geometry (background model + map texture), op 0x52 set-up sub-scripts (audio, water,
/// lighting, effects) and op 0x60 starts the challenge (the game rules). This conversion swaps the world dependency to
/// Showdown Town (234cec), points the geometry, skydome and sub-scripts at the town's own assets and moves the
/// freewheel markers (start grid first) onto a town anchor point.
/// </summary>
public static class MultiplayerTown
{
    public const uint CommonBundle = 0x685374, FreewheelBundle = 0xFD97DA;
    public const uint BanjolandWorld = 0x077143, TownWorld = 0x234CEC;
    /// <summary>Freewheel dependencies only Banjoland needs (2b665e: 390 Banjoland textures, 26 MB), dropped to leave
    /// memory for the 201 MB town bundle.</summary>
    public static readonly uint[] DroppedDependencies = { 0x2B665E };
    public const string Script = "aid_script_banjox_live_banjoland_freewheel";
    public const string Markers = "aid_marker_banjox_live_banjoland_freewheel";
    /// <summary>Town "drone action" marker #84 in aid_marker_banjox_showdowntown_main (ground level).</summary>
    public static readonly Vector3 DefaultAnchor = new(8.0f, 0.6f, 306.0f);

    static uint Id(string name) => AssetIds.IdOf(name) ?? throw new InvalidOperationException("no id for " + name);

    static byte[] Cmd(int op, params uint[] args)
    {
        var d = new byte[8 + 4 * args.Length];
        BE.W32(d, 0, (uint)d.Length); BE.W32(d, 4, (uint)op);
        for (int i = 0; i < args.Length; i++) BE.W32(d, 8 + 4 * i, args[i]);
        return d;
    }

    /// <summary>Diagnostic: keep Banjoland freewheel as it is and only add the town bundle as an extra dependency
    /// (does a live match load with the 201 MB town resident?).</summary>
    public static string AddTownDependencyOnly(Workspace ws)
    {
        var fw = ws.LoadResident(FreewheelBundle);
        int ms = fw.Symbols.IndexOf("manifest") + 1;
        var mp = fw.Parts.First(p => p.Symbol == ms && fw.SectionOf(p).Name == ".data");
        int dp = BE.S32(mp.Data, 16), dc = BE.S32(mp.Data, 20);
        var md = new byte[dp + 4 * (dc + 1)];
        Buffer.BlockCopy(mp.Data, 0, md, 0, dp + 4 * dc);
        BE.W32(md, dp + 4 * dc, 0x4F000000u | TownWorld); BE.W32(md, 20, (uint)(dc + 1));
        mp.Data = md; mp.Size = md.Length;
        ws.SaveResident(FreewheelBundle, fw, "diagnostic: freewheel also depends on Showdown Town");
        using var streamEdit = ws.LockStream(FreewheelBundle);   // Workspace.LockStream: this load → change → save is one step (other writers of the archive wait)
        var st = ws.LoadStream(FreewheelBundle);
        st.Dependencies.Add(0x50000000u | TownWorld);
        ws.SaveStream(FreewheelBundle, st, "diagnostic: freewheel stream also depends on Showdown Town");
        return $"bundle {FreewheelBundle:x6}: + dependency {TownWorld:x6} ({dc + 1} dependencies)";
    }

    /// <summary>True when the workspace already has the town version of the freewheel script.</summary>
    public static bool IsApplied(Workspace ws)
    {
        var caff = ws.LoadResident(CommonBundle);
        int sym = caff.Symbols.FindIndex(s => AssetIds.DisplayName(s) == Script) + 1;
        if (sym == 0) return false;
        var sc = ScriptAsset.Parse(caff.PartsOf(sym).First(p => caff.SectionOf(p).Name == ".data").Data);
        return sc.Commands.Any(c => c.Op == 0x01 && c.Arg(0) == Id("aid_model_banjox_background_showdowntown_default"));
    }

    public static List<string> Apply(Workspace ws, Vector3? anchor = null)
    {
        var log = new List<string>();
        var at = anchor ?? DefaultAnchor;

        // 1. the freewheel script (resident in the common bundle)
        var common = ws.LoadResident(CommonBundle);
        int sym = common.Symbols.FindIndex(s => AssetIds.DisplayName(s) == Script) + 1;
        if (sym == 0) throw new InvalidDataException(Script + " not found");
        var part = common.PartsOf(sym).First(p => common.SectionOf(p).Name == ".data");
        var sc = ScriptAsset.Parse(part.Data);
        var level = sc.Commands.First(c => c.Op == 0x01);
        var challenge = sc.Commands.First(c => c.Op == 0x60);
        var lvl = (byte[])level.Data.Clone();
        BE.W32(lvl, 8, Id("aid_model_banjox_background_showdowntown_default"));
        BE.W32(lvl, 12, Id("aid_texture_banjox_ui_maps_showdowntown"));
        // skydome as in aid_script_banjox_showdowntown_midday (op 0x2C: model, 1, 0, 0, 0, 1; lighting = lightsetup_showdowntown_main)
        var sky = Cmd(0x2C, Id("aid_model_banjox_background_showdowntown_showdowntownreferences_skydomes_afternoon"), 1, 0, 0, 0, 1);
        var cmds = new List<byte[]>
        {
            Cmd(0x4E, Id(Markers), 0),
            sky,
            lvl,
            Cmd(0x52, Id("aid_script_banjox_common_audio_showdowntown")),
            Cmd(0x52, Id("aid_script_banjox_showdowntown_common_watersetup")),
            Cmd(0x52, Id("aid_script_banjox_lightsetup_showdowntown_main")),
            Cmd(0x52, Id("aid_script_banjox_showdowntown_fxsetup")),
            Cmd(0x71),
            Cmd(0x52, Id("aid_script_banjox_showdowntown_referenceanimation")),
            challenge.Data,
            Cmd(0x00),
        };
        sc.Commands = cmds.Select(d => new ScriptCommand { Data = d }).ToList();
        part.Data = sc.Write(); part.Size = part.Data.Length;
        ws.SaveResident(CommonBundle, common, "multiplayer: Freewheel Festival loads Showdown Town (geometry, sky, set-up scripts)");
        log.Add($"{Script}: level = Showdown Town, sky = afternoon, set-up scripts = town ({part.Data.Length} bytes)");

        // 2. the freewheel bundle loads the town instead of Banjoland (resident manifest + stream archive)
        var fw = ws.LoadResident(FreewheelBundle);
        int ms = fw.Symbols.IndexOf("manifest") + 1;
        var mp = fw.Parts.First(p => p.Symbol == ms && fw.SectionOf(p).Name == ".data");
        int dp = BE.S32(mp.Data, 16), dc = BE.S32(mp.Data, 20), swapped = 0;
        var deps = Enumerable.Range(0, dc).Select(i => BE.U32(mp.Data, dp + 4 * i) & 0xFFFFFF).ToList();
        int dropped = deps.RemoveAll(d => DroppedDependencies.Contains(d));
        for (int i = 0; i < deps.Count; i++) if (deps[i] == BanjolandWorld) { deps[i] = TownWorld; swapped++; }
        // the dependency list is the manifest's tail: rewrite it (and its count) in place, shortened
        var md = new byte[dp + 4 * deps.Count];
        Buffer.BlockCopy(mp.Data, 0, md, 0, dp);
        for (int i = 0; i < deps.Count; i++) BE.W32(md, dp + 4 * i, 0x4F000000u | deps[i]);
        BE.W32(md, 20, (uint)deps.Count);
        mp.Data = md; mp.Size = md.Length;

        // 3. markers: move the whole freewheel set so its start grid (drone actions) is centred on the anchor
        int msym = fw.Symbols.FindIndex(s => AssetIds.DisplayName(s) == Markers) + 1;
        var ma = MarkerAsset.Parse(fw, msym);
        var grid = ma.Records.Where(r => r.Type == 4).ToList();
        var centre = grid.Count > 0 ? grid.Aggregate(Vector3.Zero, (a, r) => a + r.Position) / grid.Count : Vector3.Zero;
        var delta = at + new Vector3(0, 1.0f, 0) - centre;
        int moved = 0;
        foreach (var r in ma.Records)
        {
            if (r.Position == Vector3.Zero) continue;
            r.Position += delta;
            MarkerAsset.WriteTransform(fw, msym, r);
            moved++;
        }
        ws.SaveResident(FreewheelBundle, fw, $"multiplayer: freewheel depends on Showdown Town ({swapped} dependency, {dropped} dropped), {moved} markers moved by {delta}");
        log.Add($"bundle {FreewheelBundle:x6}: world dependency {BanjolandWorld:x6} -> {TownWorld:x6} ({swapped}), {dropped} Banjoland-only dependency dropped; {moved} markers moved by {delta}");

        using var streamEdit = ws.LockStream(FreewheelBundle);   // Workspace.LockStream: this load → change → save is one step (other writers of the archive wait)
        var st = ws.LoadStream(FreewheelBundle);
        int s2 = 0;
        st.Dependencies.RemoveAll(d => DroppedDependencies.Contains(d & 0xFFFFFF));
        for (int i = 0; i < st.Dependencies.Count; i++)
            if (st.Dependencies[i] == (0x50000000u | BanjolandWorld)) { st.Dependencies[i] = 0x50000000u | TownWorld; s2++; }
        ws.SaveStream(FreewheelBundle, st, "multiplayer: freewheel stream archive depends on Showdown Town");
        log.Add($"stream {FreewheelBundle:x6}: world dependency swapped ({s2})");
        return log;
    }
}
