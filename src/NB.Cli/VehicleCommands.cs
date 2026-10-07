using System.Globalization;
using NB.Core.Formats;
using NB.Core.Project;
using NB.Core.Vehicles;

namespace NB.Cli;

/// <summary>vehicle-* commands: Xbox 360 vehicle saves (packages / content files), blueprints, the game's own vehicles.</summary>
static class VehicleCommands
{
    static string? Opt(string[] args, string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

    public static int Run(string[] args)
    {
        switch (args[0])
        {
            case "vehicle-info": return Info(args);
            case "vehicle-roundtrip": return RoundTrip(args);
            case "vehicle-export": return Export(args);
            case "vehicle-xenia-install": return XeniaInstall(args);
            case "pregame-vehicles": return Pregame(args);
            case "vehicle-to-game": return ToGame(args);
            default: Console.WriteLine("unknown vehicle command"); return 1;
        }
    }

    static IEnumerable<string> Files(string p) =>
        Directory.Exists(p) ? Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal) : new[] { p };

    /// <summary>vehicle-info &lt;file&gt; [--parts &lt;ws&gt;]: kind, name, header, parts.</summary>
    static int Info(string[] args)
    {
        var v = VehicleFile.Open(args[1]);
        PartCatalog? cat = Opt(args, "--parts") is { } ws ? PartCatalog.Load(Workspace.Open(ws)) : null;
        var bp = v.Blueprint;
        Console.WriteLine($"{v.Kind}: \"{v.DisplayName}\" name \"{bp.Name}\"{(bp.NameIsAscii ? " (ASCII)" : "")}, {bp.Blocks.Count} parts");
        if (v.Package is { } p) Console.WriteLine($"  package: profile {p.ProfileId:X16}, file {v.InnerName}, {p.Files.Count} file(s)");
        foreach (var pr in v.Problems) Console.WriteLine("  problem: " + pr);
        Console.WriteLine($"  header: one piece {bp.OnePiece}, power {bp.Power:G6}, +8 {bp.Stat8:G6}, +C {bp.StatC:G6}, weight {bp.Weight:G6}, +14 {bp.Stat14:G6}, abilities 0x{bp.AbilityMask:X}, +1C {bp.Flag1C}, buttons {bp.ButtonPart(0):X8}/{bp.ButtonPart(1):X8}/{bp.ButtonPart(2):X8}");
        var (mn, mx) = bp.CellBounds();
        Console.WriteLine($"  cells {mn} .. {mx}");
        if (cat != null)
        {
            float w = bp.Blocks.Sum(b => cat.WeightOf(b.Part) ?? 0);
            Console.WriteLine($"  weight from parts {w:G6} (header {bp.Weight:G6}); unknown parts: {bp.Blocks.Count(b => cat[b.Part] == null)}");
        }
        if (args.Contains("--overlaps") && cat != null)
        {
            var doc = VehicleDocument.From(bp);
            var seen = new Dictionary<(int, int, int), VehicleDocument.Part>();
            foreach (var q in doc.Parts)
                foreach (var c in VehicleDocument.Cells(q, cat[q.B.Part]))
                {
                    if (seen.TryGetValue(c, out var o) && o != q)
                        Console.WriteLine($"  overlap at {c}: {cat[q.B.Part]?.Key} ({q.X},{q.Y},{q.Z}) o{q.Orientation} bounds {cat[q.B.Part]?.Attach?.XMin},{cat[q.B.Part]?.Attach?.YMin},{cat[q.B.Part]?.Attach?.ZMin}..{cat[q.B.Part]?.Attach?.XMax},{cat[q.B.Part]?.Attach?.YMax},{cat[q.B.Part]?.Attach?.ZMax}  vs {cat[o.B.Part]?.Key} ({o.X},{o.Y},{o.Z}) o{o.Orientation}");
                    else seen[c] = q;
                }
        }
        if (args.Contains("--blocks"))
            foreach (var b in bp.Blocks)
            {
                Console.WriteLine($"  ({b.X,3},{b.Y,3},{b.Z,3}) g{b.Group} painted {b.Painted} set {b.Setting} cat {b.Category,2} {(cat?[b.Part]?.Key ?? b.Part.ToString("X8"))} rot {b.Rotation} o{b.Orientation} paint {b.Paint:X8} act {b.Action1:X}/{b.Action2:X}");
                if (cat?[b.Part] is { } pi && args.Contains("--models"))
                {
                    var m = cat.Model(pi);
                    Console.WriteLine($"      model {pi.ModelId:X8} -> {m?.View.Name} draws {m?.Draws.Count} lod0 {m?.Draws.Count(d => !m.LodOnlyNodes.Contains(d.Node))}, size {pi.Size}, colour {pi.ColourName} {pi.DefaultPaint:X8}");
                }
            }
        return 0;
    }

    /// <summary>vehicle-roundtrip &lt;dir|file&gt;...: every vehicle file read and written back unchanged (byte-identical), packages
    /// also rebuilt from scratch (new block layout) and re-read; orientation decode/encode checked on every part.</summary>
    static int RoundTrip(string[] args)
    {
        int ok = 0, bad = 0, damaged = 0, notVehicle = 0, rebuilt = 0, orientOk = 0, orientBad = 0;
        foreach (var f in args.Skip(1).Where(a => !a.StartsWith("--")).SelectMany(Files))
        {
            var orig = File.ReadAllBytes(f);
            VehicleFile v;
            try { v = VehicleFile.Read(orig); }
            catch (Exception e)
            {
                if (VehicleFile.Detect(orig) == null) { notVehicle++; continue; }
                damaged++; Console.WriteLine($"DAMAGED  {Path.GetFileName(f)}: {e.Message}"); continue;
            }
            byte[] again = v.Kind switch
            {
                VehicleFileKind.Package => v.ToPackage(),
                VehicleFileKind.Content => v.ContentBytes(),
                _ => v.Blueprint.Write(),
            };
            bool same = again.AsSpan().SequenceEqual(orig);
            // the blueprint itself, parsed and written again
            var inner = v.Kind == VehicleFileKind.Blueprint ? orig : v.Kind == VehicleFileKind.Content ? orig : v.Package!.Extract(v.Package.Files.First(x => x.Name == v.InnerName));
            bool sameInner = v.ContentBytes().AsSpan().SequenceEqual(inner) || v.Blueprint.Write().AsSpan().SequenceEqual(inner);
            string extra = "";
            if (v.Kind == VehicleFileKind.Package)
            {
                // force a rebuild (renaming the inner file to itself through a different path) and check integrity + content
                var forced = StfsBuilder.Build(StfsIntegrity.HeaderOf(orig), new[] { new StfsBuilder.File(v.InnerName, v.ContentBytes(), StfsBuilder.Now()) }, v.Package!.DisplayName);
                var st = StfsIntegrity.Check(forced);
                var re = VehicleFile.Read(forced);
                bool good = st.HeaderHashOk && st.TopHashOk && st.BadBlocks == 0 && re.ContentBytes().AsSpan().SequenceEqual(v.ContentBytes()) && re.Package!.DisplayName == v.Package.DisplayName;
                if (good) rebuilt++;
                extra = good ? ", rebuilt package verifies" : $", REBUILD FAILED ({st})";
                if (args.Contains("--write-rebuilt")) File.WriteAllBytes(Path.Combine(Opt(args, "--write-rebuilt")!, Path.GetFileName(f)), forced);
            }
            foreach (var b in v.Blueprint.Blocks)
            {
                var e = Orientations.Euler(b.Orientation);
                var r = b.Rotation;
                bool eq = BitConverter.SingleToUInt32Bits(e.X) == b.RotXBits && BitConverter.SingleToUInt32Bits(e.Y) == b.RotYBits && BitConverter.SingleToUInt32Bits(e.Z) == b.RotZBits;
                if (eq) orientOk++;
                else { orientBad++; if (args.Contains("--orient")) Console.WriteLine($"   orientation {r} -> #{b.Orientation} -> {e}"); }
            }
            if (same && sameInner) ok++; else bad++;
            Console.WriteLine($"{(same && sameInner ? "OK      " : "DIFFERS ")} {Path.GetFileName(f)}: {v.Kind} \"{v.Blueprint.Name}\" {v.Blueprint.Blocks.Count} parts{extra}{(v.Problems.Count > 0 ? " [" + string.Join("; ", v.Problems) + "]" : "")}");
        }
        Console.WriteLine($"identical {ok}, different {bad}, damaged {damaged}, not vehicles {notVehicle}; packages rebuilt+verified {rebuilt}; part orientations re-encoded identically {orientOk}, differently {orientBad}");
        return bad == 0 ? 0 : 3;
    }

    /// <summary>vehicle-export &lt;in&gt; &lt;out&gt; [--as package|content|blueprint] [--name TEXT] [--package-name 0x0000000N]
    /// [--template PKG] [--profile XUID]: convert between the kinds.</summary>
    static int Export(string[] args)
    {
        var v = VehicleFile.Open(args[1]);
        var bp = v.Blueprint.Clone();
        if (Opt(args, "--name") is { } n) bp.Name = n;
        string kind = Opt(args, "--as") ?? "package";
        byte[] outBytes = kind switch
        {
            "content" => v.ContentBytes(bp),
            "blueprint" => bp.Write(false),
            _ => v.ToPackage(bp, Opt(args, "--package-name") ?? (Path.GetFileName(args[2]).StartsWith("0x") ? Path.GetFileName(args[2]) : null),
                Opt(args, "--template") is { } t ? File.ReadAllBytes(t) : null,
                Opt(args, "--profile") is { } x ? ulong.Parse(x, NumberStyles.HexNumber) : null),
        };
        File.WriteAllBytes(args[2], outBytes);
        Console.WriteLine($"wrote {args[2]} ({kind}, {outBytes.Length} bytes, \"{bp.Name}\", {bp.Blocks.Count} parts)");
        return 0;
    }

    /// <summary>vehicle-xenia-install &lt;package&gt; &lt;xenia content\xuid dir&gt; [0x0000000N]</summary>
    static int XeniaInstall(string[] args)
    {
        var pkg = File.ReadAllBytes(args[1]);
        var dir = VehicleFile.InstallToXenia(pkg, args[2], args.Length > 3 ? args[3] : null);
        Console.WriteLine("installed: " + dir);
        return 0;
    }

    /// <summary>pregame-vehicles &lt;ws&gt; [act script | bundle hex ...]: the game's vehicles (of an act: act + world bundle).</summary>
    static int Pregame(string[] args)
    {
        var ws = Workspace.Open(args[1]);
        var idx = AssetIndex.LoadOrBuild(ws);
        var bundles = new List<uint>();
        foreach (var a in args.Skip(2))
        {
            if (a.StartsWith("aid_script"))
            {
                var act = ActCatalog.Build(ws, idx).First(x => x.Script == a);
                bundles.Add(act.ActBundle); if (act.WorldBundle != 0) bundles.Add(act.WorldBundle);
            }
            else bundles.Add(Convert.ToUInt32(a, 16));
        }
        var list = bundles.Count > 0 ? PregameVehicles.ForBundles(ws, idx, bundles) : PregameVehicles.All(idx);
        foreach (var v in list)
        {
            Console.WriteLine($"{v.Label,-60} {v.Parts,4} parts  bundles {string.Join(",", v.Bundles.Select(b => b.ToString("x6")))}");
            foreach (var u in v.Users) Console.WriteLine("    " + u);
        }
        return 0;
    }

    /// <summary>vehicle-to-game &lt;ws&gt; &lt;vehicle asset&gt; &lt;vehicle file&gt;: replaces a game vehicle's blueprint (the asset keeps
    /// its id; every bundle holding it is written).</summary>
    static int ToGame(string[] args)
    {
        var ws = Workspace.Open(args[1]);
        var idx = AssetIndex.LoadOrBuild(ws);
        var v = PregameVehicles.All(idx).FirstOrDefault(x => x.Asset == args[2] || x.Short == args[2]) ?? throw new ArgumentException("no vehicle asset " + args[2]);
        var src = VehicleFile.Open(args[3]);
        var cat = PartCatalog.Load(ws, idx);
        var bp = src.Blueprint.Clone();
        bp.UpdateHeader(cat.WeightOf);
        var done = PregameVehicles.Save(ws, v, bp, $"vehicle {v.Short} replaced by {Path.GetFileName(args[3])} ({bp.Blocks.Count} parts)");
        Console.WriteLine($"{v.Asset}: written to {string.Join(", ", done.Select(b => b.ToString("x6")))}");
        return 0;
    }
}
