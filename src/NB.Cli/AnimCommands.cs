using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using NB.Core.Formats;
using NB.Core.IO;
using NB.Core.Models;
using NB.Core.Project;

namespace NB.Cli;

/// <summary>anim-verify / anim-rotate / anim-import (animation re-encoding, docs/research/16_anim_codec.md).</summary>
static class AnimCommands
{
    static string? Opt(string[] args, string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
    static CaffPart DataPart(CaffFile c, int sym) => c.PartsOf(sym).First(p => c.SectionOf(p).Name == ".data");
    static int FindSymbol(CaffFile c, string name) => c.Symbols.FindIndex(s => AssetIds.DisplayName(s) == name) + 1;

    /// <summary>Channel values in codec order (rotation xyz with w ≥ 0, translation, scale).</summary>
    static void Channels(AnimKey k, Span<double> v)
    {
        var r = k.Rotation;
        if (r.W < 0) r = new Quaternion(-r.X, -r.Y, -r.Z, -r.W);
        v[0] = r.X; v[1] = r.Y; v[2] = r.Z;
        v[3] = k.Translation.X; v[4] = k.Translation.Y; v[5] = k.Translation.Z;
        v[6] = k.Scale.X; v[7] = k.Scale.Y; v[8] = k.Scale.Z;
    }

    /// <summary>Largest |decoded − expected| over all channels, in quantisation steps of the anim's per-channel scales.</summary>
    static double MaxErrorQuanta(byte[] data, AnimKey[][] decoded, AnimKey[][] expected)
    {
        int D = (int)BE.U32(data, (int)BE.U32(data, 0x24) + 0x18);
        int scales = (int)BE.U32(data, D + 4);
        var sc = Enumerable.Range(0, 9).Select(c => (double)BE.F32(data, scales + 4 * c)).ToArray();
        double worst = 0;
        Span<double> a = stackalloc double[9], b = stackalloc double[9];
        for (int t = 0; t < expected.Length; t++)
            for (int k = 0; k < expected[t].Length; k++)
            {
                Channels(decoded[t][k], a); Channels(expected[t][k], b);
                // q and −q are the same rotation (the encoder may flip the sign when w ≈ 0): take the closer one
                double rp = 0, rn = 0;
                for (int c = 0; c < 3; c++) { rp = Math.Max(rp, Math.Abs(a[c] - b[c]) / sc[c]); rn = Math.Max(rn, Math.Abs(a[c] + b[c]) / sc[c]); }
                worst = Math.Max(worst, Math.Min(rp, rn));
                for (int c = 3; c < 9; c++) worst = Math.Max(worst, Math.Abs(a[c] - b[c]) / sc[c]);
            }
        return worst;
    }

    /// <summary>Angle between two rotations in degrees (small-angle safe: 4·asin(|q1 ∓ q2| / 2)).</summary>
    static double AngleDeg(Quaternion a, Quaternion b)
    {
        a = Quaternion.Normalize(a); b = Quaternion.Normalize(b);
        if (Quaternion.Dot(a, b) < 0) b = -b;
        double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z, dw = a.W - b.W;
        return 4 * Math.Asin(Math.Min(1, Math.Sqrt(dx * dx + dy * dy + dz * dz + dw * dw) / 2)) * 180 / Math.PI;
    }

    static bool KeysEqual(AnimKey[][] x, AnimKey[][] y)
    {
        if (x.Length != y.Length) return false;
        for (int t = 0; t < x.Length; t++)
            if (!x[t].AsSpan().SequenceEqual(y[t])) return false;
        return true;
    }

    // ------------------------------------------------------------------ anim-verify

    /// <summary>
    /// anim-verify &lt;workspace&gt; [name filter] [--no-edit]: for every anim in the workspace's decompressed-bundle cache
    /// (read-only): (1) Rebuild(orig, decode(orig)) with encoding reuse must be byte-identical and decode identically,
    /// (2) the same with a from-scratch minimal encoder (reports the byte-identical fraction), (3) an edit that grows the
    /// stream (rotated track + translated track) through ReplaceInCaff, CAFF write + re-read, decode within ½ quantum,
    /// all self-pointers retargeted to the same content and the pose object intact. Nothing is written to disk.
    /// </summary>
    public static int Verify(string[] args)
    {
        var ws = Workspace.Open(args[1]);
        var idx = AssetIndex.LoadOrBuild(ws);
        string? filter = args.Length > 2 && !args[2].StartsWith("--") ? args[2] : null;
        bool edit = !args.Contains("--no-edit");
        var groups = idx.Entries.Where(e => e.Type == "anim" && e.Symbol > 0 && !e.Streamed && (filter == null || e.Name.Contains(filter)))
            .GroupBy(e => e.Bundle).OrderBy(g => g.Key).ToList();
        var sw = Stopwatch.StartNew();
        int rescaled = 0, n = 0, fail = 0, same1 = 0, dec1 = 0, same0 = 0, dec0 = 0, size0 = 0, editOk = 0, editBad = 0, grew = 0, wideUp = 0, missingBundles = 0;
        double worst0 = 0, worstEdit = 0;
        var diffKinds = new Dictionary<string, int>();
        foreach (var g in groups)
        {
            var path = Path.Combine(ws.CacheDir, "4f", g.Key.ToString("x6"));
            if (!File.Exists(path)) { missingBundles++; continue; }
            var caff = CaffFile.Read(File.ReadAllBytes(path));
            var pending = new List<(int Sym, string Name, byte[] Orig, AnimKey[][] Expected, AnimRebuild R, HashSet<int> Self)>();
            foreach (var e in g.GroupBy(x => x.Symbol).Select(x => x.First()))
            {
                n++;
                try
                {
                    var part = DataPart(caff, e.Symbol);
                    int pid = caff.Parts.IndexOf(part) + 1;
                    var d = part.Data;
                    var a = AnimAsset.Parse(d);
                    var self = caff.Relocs.Where(r => r.FromPart == pid && r.ToPart == pid).SelectMany(r => r.Offsets).ToHashSet();
                    var r1 = AnimAsset.Rebuild(d, a.Keys, self, true);
                    if (r1.Data.AsSpan().SequenceEqual(d)) same1++;
                    else if (diffKinds.TryAdd("reuse:" + e.Name, 1)) Console.WriteLine($"  reuse differs: {e.Name} (delta {r1.Delta}, stride {r1.OldStride}->{r1.NewStride})");
                    if (KeysEqual(AnimAsset.Parse(r1.Data).Keys, a.Keys)) dec1++;
                    var r0 = AnimAsset.Rebuild(d, a.Keys, self, false);
                    if (r0.Data.AsSpan().SequenceEqual(d)) same0++;
                    else
                    {
                        if (r0.Delta != 0) size0++;
                        var lo = AnimAsset.ChannelLayout(d, out var fo, out var wo);
                        var ln = AnimAsset.ChannelLayout(r0.Data, out var fn, out var wn);
                        var whys = new SortedSet<string>();
                        if (wo != wn) whys.Add("wide");
                        for (int t = 0; t < lo.Length; t++)
                        {
                            if (!fo[t].AsSpan().SequenceEqual(fn[t])) whys.Add("flags");
                            for (int c = 0; c < 9; c++)
                            {
                                var (x, y) = (lo[t][c], ln[t][c]);
                                if (x == y) continue;
                                string grp = c < 3 ? "rot" : c < 6 ? "trans" : "scale";
                                if (x.Kind != y.Kind) whys.Add($"{grp}:{"DSA"[x.Kind]}->{"DSA"[y.Kind]}");
                                else if (x.Base != y.Base) whys.Add($"{grp}:base");
                                else whys.Add($"{grp}:width{(x.Width > y.Width ? "+" : "-")}{Math.Abs(x.Width - y.Width)}");
                            }
                        }
                        foreach (var why in whys.DefaultIfEmpty("bytes only")) diffKinds[why] = diffKinds.GetValueOrDefault(why) + 1;
                    }
                    var k0 = AnimAsset.Parse(r0.Data).Keys;
                    if (KeysEqual(k0, a.Keys)) dec0++;
                    worst0 = Math.Max(worst0, MaxErrorQuanta(d, k0, a.Keys));
                    if (edit)
                    {
                        var k2 = a.Keys.Select(r => (AnimKey[])r.Clone()).ToArray();
                        int t1 = e.Symbol * 7 % a.Tracks, t2 = (t1 + 1) % a.Tracks;
                        var rot = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1, 2, 3)), 40f * MathF.PI / 180f);
                        for (int k = 0; k < k2[t1].Length; k++)
                            k2[t1][k] = k2[t1][k] with { Rotation = Quaternion.Normalize(k2[t1][k].Rotation * rot) };
                        for (int k = 0; k < k2[t2].Length; k++)
                            k2[t2][k] = k2[t2][k] with { Translation = k2[t2][k].Translation + new Vector3(0.05f * k, 0, 0.3f) };
                        var r = AnimAsset.ReplaceInCaff(caff, e.Symbol, k2);
                        if (r.Delta > 0) grew++;
                        if (r.WideChanged) wideUp++;
                        pending.Add((e.Symbol, e.Name, d, k2, r, self));
                    }
                }
                catch (Exception ex) { fail++; Console.WriteLine($"  FAIL {e.Name}: {ex.Message}"); }
            }
            if (pending.Count == 0) continue;
            var back = CaffFile.Read(caff.Write());
            foreach (var (sym, name, orig, expected, r, self) in pending)
            {
                var errs = new List<string>();
                var part = DataPart(back, sym);
                int pid = back.Parts.IndexOf(part) + 1;
                var nd = part.Data;
                var na = AnimAsset.Parse(nd);
                double err = MaxErrorQuanta(nd, na.Keys, expected);
                worstEdit = Math.Max(worstEdit, err);
                if (err > 0.5001) errs.Add($"quantisation error {err:0.###} steps");
                // every original self-pointer: moved location, retargeted to the same content
                var newSelf = back.Relocs.Where(x => x.FromPart == pid && x.ToPart == pid).SelectMany(x => x.Offsets).ToHashSet();
                foreach (int at in self)
                {
                    int nat = r.MapOffset(at);
                    if (!newSelf.Contains(nat)) { errs.Add($"reloc 0x{at:X} lost"); break; }
                    int v = (int)BE.U32(orig, at), nv = (int)BE.U32(nd, nat);
                    if (v >= r.OldRegionEnd && nv != v + r.Delta)
                    { errs.Add($"pointer 0x{at:X} -> 0x{v:X} became 0x{nv:X}"); break; }
                }
                foreach (int at in r.RequiredPointers)
                    if (!newSelf.Contains(at)) errs.Add($"descriptor pointer 0x{at:X} not relocated");
                if (r.ScalesChanged.Count > 0) rescaled++;
                // bytes after the region are the original tail except for moved pointer values
                var tailOld = orig.AsSpan(r.OldRegionEnd); var tailNew = nd.AsSpan(r.OldRegionEnd + r.Delta);
                if (tailOld.Length != tailNew.Length) errs.Add("tail length");
                else
                {
                    int diffs = 0;
                    for (int i = 0; i < tailOld.Length; i++) if (tailOld[i] != tailNew[i]) diffs++;
                    int ptrBytes = self.Count(at => at >= r.OldRegionEnd) * 4;
                    if (diffs > ptrBytes) errs.Add($"tail: {diffs} bytes differ (> {ptrBytes} pointer bytes)");
                }
                // (anim poses name their joints through the bundle's string pool: compare without names)
                var s0 = Skeleton.Parse(orig)?.Select(j => j with { Name = "" }).ToList(); var s1 = Skeleton.Parse(nd)?.Select(j => j with { Name = "" }).ToList();
                if ((s0 == null) != (s1 == null) || s0 != null && !s0.SequenceEqual(s1!))
                    errs.Add("pose object changed: " + (s0 == null || s1 == null ? "missing" : s0.Zip(s1).Where(z => z.First != z.Second).Select(z => $"{z.First} vs {z.Second}").FirstOrDefault() ?? $"count {s0.Count} vs {s1.Count}"));
                if (errs.Count == 0) editOk++;
                else { editBad++; Console.WriteLine($"  EDIT FAIL {name}: {string.Join("; ", errs)}"); }
            }
        }
        Console.WriteLine($"{n} anims in {groups.Count - missingBundles} bundles ({sw.Elapsed.TotalSeconds:F0} s), {fail} failed, {missingBundles} bundles not cached");
        Console.WriteLine($"rebuild with encoding reuse:  {same1}/{n} byte-identical, {dec1}/{n} decode identically");
        Console.WriteLine($"minimal encoder (no reuse):   {same0}/{n} byte-identical ({100.0 * same0 / Math.Max(1, n):0.0}%), {dec0}/{n} decode identically, worst error {worst0:0.###} steps; differences: " +
                          string.Join(", ", diffKinds.Where(k => !k.Key.StartsWith("reuse:")).Select(k => $"{k.Key} {k.Value}")));
        if (edit)
            Console.WriteLine($"edited (rotate 40° + translate, ReplaceInCaff, CAFF write/read): {editOk} ok, {editBad} bad; worst error {worstEdit:0.###} steps; {grew} grew, {wideUp} switched to QUAT_BITSTREAM32, {rescaled} needed a coarser channel scale");
        return fail == 0 && editBad == 0 && dec1 == n && dec0 == n ? 0 : 1;
    }

    // ------------------------------------------------------------------ edits into the workspace

    static List<Joint> LoadSkeleton(Workspace ws, AssetIndex idx, string animName, string? modelName, out string model)
    {
        model = modelName ?? AnimImport.GuessModel(animName, idx.Entries.Where(e => e.Type == "model").Select(e => e.Name).Distinct())
                ?? throw new ArgumentException("cannot guess the character model of this anim: pass --model <model asset name>");
        string m = model;
        var animBundles = idx.Entries.Where(e => e.Name == animName).Select(e => e.Bundle).ToHashSet();
        var entry = idx.Entries.Where(e => e.Name == m && e.Symbol > 0 && !e.Streamed).OrderByDescending(e => animBundles.Contains(e.Bundle)).FirstOrDefault()
                    ?? throw new ArgumentException($"model {m} not found in any resident bundle");
        var caff = ws.LoadResident(entry.Bundle);
        int sym = FindSymbol(caff, m);
        return Skeleton.Parse(DataPart(caff, sym).Data) ?? throw new InvalidDataException($"{m} has no skeleton");
    }

    /// <summary>Applies an edit to the anim in every resident bundle holding it; verifies the re-read bundle; saves unless --dry.</summary>
    static int Apply(Workspace ws, AssetIndex idx, string animName, bool dry, string description, IReadOnlyList<string>? jointNames, Func<AnimAsset, AnimKey[][]> editKeys)
    {
        var bundles = idx.Entries.Where(e => e.Name == animName && e.Symbol > 0 && !e.Streamed).Select(e => e.Bundle).Distinct().ToList();
        if (bundles.Count == 0) { Console.WriteLine("anim not found in any resident bundle"); return 1; }
        foreach (var b in bundles)
        {
            var caff = ws.LoadResident(b);
            int sym = FindSymbol(caff, animName);
            var orig = DataPart(caff, sym).Data;
            var a = AnimAsset.Parse(orig);
            var keys = editKeys(a);
            var r = AnimAsset.ReplaceInCaff(caff, sym, keys);
            var back = CaffFile.Read(caff.Write());
            var nd = DataPart(back, FindSymbol(back, animName)).Data;
            double err = MaxErrorQuanta(nd, AnimAsset.Parse(nd).Keys, keys);
            bool identical = nd.AsSpan().SequenceEqual(orig);
            Console.WriteLine($"{b:x6} {animName}: {r.ChannelsChanged} channel(s) changed, .data {orig.Length} -> {nd.Length} bytes (stride {r.OldStride} -> {r.NewStride}" +
                              $"{(r.WideChanged ? ", now QUAT_BITSTREAM32" : "")}){(identical ? ", byte-identical to the original" : "")}; re-decoded within {err:0.###} quantisation steps");
            if (err > 0.5001) throw new InvalidDataException("re-decoded keys do not match the requested keys");
            if (r.Changed.Count > 0)
                Console.WriteLine("  changed channels: " + string.Join(", ", r.Changed.GroupBy(x => x.Track).Take(16).Select(g =>
                    $"{(jointNames != null && g.Key < jointNames.Count ? jointNames[g.Key] : "track " + g.Key)} {string.Join("", g.Select(x => new[] { "rx", "ry", "rz", "tx", "ty", "tz", "sx", "sy", "sz" }[x.Channel] + " "))}".TrimEnd())) +
                    (r.Changed.Select(x => x.Track).Distinct().Count() > 16 ? " …" : ""));
            // how far the new anim is from the original (largest per-track change)
            var nk = AnimAsset.Parse(nd).Keys;
            (double Deg, int T) rot = (0, -1); (double D, int T) tra = (0, -1);
            for (int t = 0; t < a.Tracks; t++)
                for (int k = 0; k < a.KeyFrames.Length; k++)
                {
                    double deg = AngleDeg(a.Keys[t][k].Rotation, nk[t][k].Rotation);
                    if (deg > rot.Deg) rot = (deg, t);
                    double dt = Vector3.Distance(a.Keys[t][k].Translation, nk[t][k].Translation);
                    if (dt > tra.D) tra = (dt, t);
                }
            string Nm(int t) => jointNames != null && t >= 0 && t < jointNames.Count ? jointNames[t] : $"track {t}";
            int movedTracks = Enumerable.Range(0, a.Tracks).Count(t => Enumerable.Range(0, a.KeyFrames.Length).Any(k =>
                AngleDeg(a.Keys[t][k].Rotation, nk[t][k].Rotation) > 0.5 ||
                Vector3.Distance(a.Keys[t][k].Translation, nk[t][k].Translation) > 1e-3));
            Console.WriteLine($"  change vs original: {movedTracks} track(s) moved (>0.5° or >1 mm); max rotation {rot.Deg:0.##}° ({Nm(rot.T)}), max translation {tra.D * 1000:0.#} mm ({Nm(tra.T)})");
            if (!dry && !identical) ws.SaveResident(b, caff, description);
        }
        Console.WriteLine(dry ? "dry run: nothing saved" : $"saved {bundles.Count} bundle(s)");
        return 0;
    }

    /// <summary>anim-rotate &lt;workspace&gt; &lt;anim&gt; &lt;joint&gt; &lt;rx,ry,rz degrees&gt; [--model &lt;model&gt;] [--dry]</summary>
    public static int Rotate(string[] args)
    {
        var ws = Workspace.Open(args[1]);
        var idx = AssetIndex.LoadOrBuild(ws);
        string anim = args[2], jointName = args[3];
        var p = args[4].Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        if (p.Length != 3) throw new ArgumentException("rotation must be rx,ry,rz in degrees");
        var skel = LoadSkeleton(ws, idx, anim, Opt(args, "--model"), out var model);
        int track = skel.FindIndex(j => j.Name.Equals(jointName, StringComparison.OrdinalIgnoreCase));
        if (track < 0)
        {
            Console.WriteLine($"joint {jointName} not in {model}; joints: {string.Join(" ", skel.Select(j => j.Name))}");
            return 1;
        }
        var rot = AnimImport.GameEulerDegrees(new Vector3(p[0], p[1], p[2]));
        Console.WriteLine($"{model}: joint {skel[track].Name} = track {track}; post-multiplying every key by ({p[0]}, {p[1]}, {p[2]})° (X, then Y, then Z, joint-local)");
        return Apply(ws, idx, anim, args.Contains("--dry"), $"anim {anim}: rotated {skel[track].Name} by ({args[4]})°", skel.Select(j => j.Name).ToList(), a =>
        {
            if (a.Tracks != skel.Count) Console.WriteLine($"warning: {a.Tracks} tracks vs {skel.Count} joints in {model}");
            var keys = a.Keys.Select(r => (AnimKey[])r.Clone()).ToArray();
            for (int k = 0; k < keys[track].Length; k++)
                keys[track][k] = keys[track][k] with { Rotation = Quaternion.Normalize(keys[track][k].Rotation * rot) };
            return keys;
        });
    }

    /// <summary>anim-import &lt;workspace&gt; &lt;anim&gt; &lt;file.fbx&gt; [--model &lt;model&gt;] [--take &lt;name&gt;] [--dry]</summary>
    public static int Import(string[] args)
    {
        var ws = Workspace.Open(args[1]);
        var idx = AssetIndex.LoadOrBuild(ws);
        string anim = args[2];
        var fa = FbxReader.ReadAnimation(args[3], Opt(args, "--take"));
        var skel = LoadSkeleton(ws, idx, anim, Opt(args, "--model"), out var model);
        Console.WriteLine($"{Path.GetFileName(args[3])}: {fa.Models.Count} models; skeleton {model} ({skel.Count} joints)");
        return Apply(ws, idx, anim, args.Contains("--dry"), $"anim {anim}: imported {Path.GetFileName(args[3])}", skel.Select(j => j.Name).ToList(), a =>
        {
            if (a.Tracks != skel.Count) Console.WriteLine($"warning: {a.Tracks} tracks vs {skel.Count} joints in {model}");
            var notes = new List<string>();
            var keys = AnimImport.FromFbx(fa, a, skel, notes);
            foreach (var s in notes) Console.WriteLine("  " + s);
            return keys;
        });
    }
}
