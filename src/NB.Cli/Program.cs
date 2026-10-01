using System.Diagnostics;
using NB.Core.Compression;

namespace NB.Cli;

static class Program
{
    /// <summary>World edits from a JSON file: a list of string lists (NB.Core.Project.WorldOps).</summary>
    static List<List<string>> ReadOps(string path) =>
        System.Text.Json.JsonSerializer.Deserialize<List<List<string>>>(File.ReadAllText(path)) ?? new();

    static int Main(string[] args)
    {
        if (args.Length == 0) { Usage(); return 1; }
        try
        {
            switch (args[0])
            {
                case "xdecomp":
                {
                    var sw = Stopwatch.StartNew();
                    var data = File.ReadAllBytes(args[1]);
                    var h = XCompressFile.ReadHeader(data);
                    Console.WriteLine($"segments={h.Segments} segSize=0x{h.SegmentSize:X} window=2^{h.WindowBits} out={h.UncompressedSize}");
                    var outp = XCompressFile.Decompress(data);
                    if (args.Length > 2) File.WriteAllBytes(args[2], outp);
                    Console.WriteLine($"ok {outp.Length} bytes in {sw.ElapsedMilliseconds} ms, head={Convert.ToHexString(outp, 0, Math.Min(32, outp.Length))}");
                    return 0;
                }
                case "xdecomp-dir":
                {
                    var sw = Stopwatch.StartNew();
                    Directory.CreateDirectory(args[2]);
                    int ok = 0, bad = 0, copied = 0;
                    var files = Directory.GetFiles(args[1]);
                    Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount / 2 }, f =>
                    {
                        var data = File.ReadAllBytes(f);
                        var dst = Path.Combine(args[2], Path.GetFileName(f));
                        if (!XCompressFile.IsCompressed(data)) { File.WriteAllBytes(dst, data); Interlocked.Increment(ref copied); return; }
                        try
                        {
                            var o = XCompressFile.Decompress(data);
                            File.WriteAllBytes(dst, o);
                            bool caff = o.Length > 4 && o[0] == 'C' && o[1] == 'A' && o[2] == 'F' && o[3] == 'F';
                            if (!caff) Console.WriteLine($"{Path.GetFileName(f)}: decompressed but not CAFF");
                            Interlocked.Increment(ref ok);
                        }
                        catch (Exception e) { Console.WriteLine($"{Path.GetFileName(f)}: {e.Message}"); Interlocked.Increment(ref bad); }
                    });
                    Console.WriteLine($"decompressed={ok} failed={bad} copied-as-is={copied} in {sw.Elapsed.TotalSeconds:F1}s");
                    return bad == 0 ? 0 : 3;
                }
                case "caff-roundtrip":
                {
                    int same = 0, diff = 0;
                    foreach (var f in args.Skip(1).SelectMany(a => Directory.Exists(a) ? Directory.GetFiles(a, "*", SearchOption.AllDirectories) : new[] { a }))
                    {
                        var d = File.ReadAllBytes(f);
                        if (!NB.Core.Formats.CaffFile.IsCaff(d)) continue;
                        try
                        {
                            var c = NB.Core.Formats.CaffFile.Read(d);
                            var o = c.Write();
                            if (o.AsSpan().SequenceEqual(d)) same++;
                            else
                            {
                                diff++;
                                int first = 0; while (first < Math.Min(o.Length, d.Length) && o[first] == d[first]) first++;
                                Console.WriteLine($"{f}: differs len {o.Length} vs {d.Length}, first diff at 0x{first:X}");
                            }
                        }
                        catch (Exception e) { diff++; Console.WriteLine($"{f}: {e.Message}"); }
                    }
                    Console.WriteLine($"identical={same} different={diff}");
                    return diff == 0 ? 0 : 3;
                }
                case "bundle-roundtrip":
                {
                    int same = 0, diff = 0; var kinds = new Dictionary<string, int>();
                    void Walk(byte[] d, string name, int depth)
                    {
                        var a = NB.Core.Formats.BundleArchive.Read(d);
                        var o = a.Write();
                        if (o.AsSpan().SequenceEqual(d)) same++; else { diff++; Console.WriteLine($"{name}: differs ({o.Length} vs {d.Length})"); }
                        foreach (var e in a.Entries)
                        {
                            kinds[e.Kind] = kinds.GetValueOrDefault(e.Kind) + 1;
                            if (e.Kind == "archive") Walk(e.Data!, $"{name}/{e.Id:X8}", depth + 1);
                        }
                    }
                    foreach (var f in Directory.GetFiles(args[1])) Walk(File.ReadAllBytes(f), Path.GetFileName(f), 0);
                    Console.WriteLine($"identical={same} different={diff} entries: {string.Join(", ", kinds.Select(k => $"{k.Key}={k.Value}"))}");
                    return diff == 0 ? 0 : 3;
                }
                case "tex-extract":
                {
                    // tex-extract <caff | stream archive> <outdir> [name filter]
                    var raw0 = File.ReadAllBytes(args[1]);
                    string filter = args.Length > 3 ? args[3] : "";
                    int ok = 0, fail = 0, sizeMismatch = 0;
                    var caffs = NB.Core.Formats.BundleArchive.IsArchive(raw0)
                        ? NB.Core.Formats.BundleArchive.Read(raw0).Entries.Where(e => e.Kind == "caff").Select(e => NB.Core.Formats.CaffFile.Read(e.Data!)).ToList()
                        : new List<NB.Core.Formats.CaffFile> { NB.Core.Formats.CaffFile.Read(raw0) };
                    foreach (var c in caffs)
                    {
                        for (int s = 1; s <= c.Symbols.Count; s++)
                        {
                            var name = c.Symbols[s - 1];
                            if (!name.Contains("aid_texture") || !name.Contains(filter)) continue;
                            var parts = c.PartsOf(s).ToList();
                            var cpu = parts.FirstOrDefault(p => c.SectionOf(p).Name == ".data");
                            var gpu = parts.FirstOrDefault(p => c.SectionOf(p).Name == ".texturegpu");
                            if (cpu == null || gpu == null || !NB.Core.Textures.TextureHeader.IsTexture(cpu.Data)) continue;
                            string shortName = name.Replace("D:\\LocalLibrary\\BanjoX\\", "").Split('\\')[0];
                            try
                            {
                                var t = new NB.Core.Textures.TextureAsset(cpu.Data, gpu.Data);
                                if (t.ExpectedGpuSize != gpu.Data.Length) { sizeMismatch++; Console.WriteLine($"size {shortName}: {t.Header} expected 0x{t.ExpectedGpuSize:X} got 0x{gpu.Data.Length:X}"); }
                                var (rgba, w, h) = t.Decode(0);
                                NB.Core.Textures.ImageIO.Save(Path.Combine(args[2], shortName + ".png"), rgba, w, h);
                                ok++;
                            }
                            catch (Exception e) { fail++; Console.WriteLine($"fail {shortName}: {e.Message}"); }
                        }
                    }
                    Console.WriteLine($"decoded={ok} failed={fail} sizeMismatch={sizeMismatch}");
                    return 0;
                }
                case "xex":
                {
                    var x = NB.Core.Formats.XexFile.Read(File.ReadAllBytes(args[1]));
                    Console.WriteLine($"title={x.TitleId:X8} name={x.OriginalName} entry=0x{x.EntryPoint:X8} base=0x{x.ImageBase:X8} imageSize=0x{x.ImageSize:X} enc={x.EncryptionType} comp={x.CompressionType}");
                    Console.WriteLine("imports: " + string.Join(", ", x.ImportLibraries));
                    foreach (var kv in x.OptionalHeaders) Console.WriteLine($"  opt 0x{kv.Key:X8} = 0x{kv.Value:X8}");
                    var img = x.GetImage();
                    Console.WriteLine($"decoded with {x.KeyUsed} key: {img.Length} bytes");
                    foreach (var s in NB.Core.Formats.XexFile.Sections(img)) Console.WriteLine($"  {s.Name,-8} va=0x{x.ImageBase + s.Va:X8} vsize=0x{s.VSize:X} raw=0x{s.RawOff:X} flags=0x{s.Flags:X8}");
                    if (args.Length > 2) File.WriteAllBytes(args[2], img);
                    return 0;
                }
                case "model":
                {
                    // model <caff> <exact name> [out.obj]
                    var c = NB.Core.Formats.CaffFile.Read(File.ReadAllBytes(args[1]));
                    int sym = c.Symbols.IndexOf(args[2]) + 1;
                    if (sym == 0) { Console.WriteLine("symbol not found"); return 1; }
                    var sw = Stopwatch.StartNew();
                    var m = NB.Core.Models.ModelAsset.Parse(c, sym);
                    Console.WriteLine($"{m.View.Name}: chunks [{string.Join(",", m.Chunks.Keys)}] nodes={m.Nodes.Count} instances={m.Instances.Count} textures={m.TextureTable.Count} draws={m.Draws.Count} resHdr=0x{m.ResourceHeader:X} ({sw.ElapsedMilliseconds} ms)");
                    foreach (var d in m.Draws.Take(args.Contains("--all") ? int.MaxValue : 12))
                        Console.WriteLine($"  vb 0x{d.VbRecord:X} stride {d.Stride} verts {d.Positions.Length} idx {d.Indices.Length} prim {d.Primitive} vs@0x{d.VertexShaderPoolOffset:X} [{string.Join("; ", d.Layout)}] tex: {string.Join(", ", d.Textures.Select(t => $"{t.Slot}:{t.Texture.Replace("aid_texture_banjox_shared_", "")}"))}");
                    foreach (var w in m.Warnings.Take(10)) Console.WriteLine("  warn: " + w);
                    if (args.Length > 3 && !args[3].StartsWith("--"))
                        NB.Core.Models.ObjExporter.Write(args[3], m.View.Name, m.Draws.Select(d => (d, System.Numerics.Matrix4x4.Identity)));
                    return 0;
                }
                case "ws-create":
                {
                    // ws-create <original game dir> <workspace root>
                    var ws = NB.Core.Project.Workspace.Create(args[1], args[2], new Progress<(string F, double P)>(p => { }));
                    Console.WriteLine($"workspace ready: {ws.Root}");
                    return 0;
                }
                case "ws-index":
                {
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    var sw = Stopwatch.StartNew();
                    var idx = NB.Core.Project.AssetIndex.LoadOrBuild(ws, new Progress<(string, double)>(p => { }), args.Contains("--rebuild"));
                    Console.WriteLine($"{idx.Entries.Count} assets in {sw.Elapsed.TotalSeconds:F0}s");
                    foreach (var g in idx.Entries.GroupBy(e => e.Type).OrderByDescending(g => g.Count()).Take(25)) Console.WriteLine($"  {g.Key,-22} {g.Count()}");
                    foreach (var w in NB.Core.Project.WorldCatalog.FromIndex(idx)) Console.WriteLine($"  world {w.Display,-22} bundle {w.Bundle:x6}");
                    return 0;
                }
                case "ws-status":
                {
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    var rep = ws.Original.Validate();
                    Console.WriteLine($"original: {ws.Original.Root} valid={rep.Ok}"); foreach (var i in rep.Info) Console.WriteLine("  " + i);
                    foreach (var f in ws.ModifiedFiles()) Console.WriteLine("  modified: " + f);
                    return 0;
                }
                case "world-load":
                {
                    // world-load <workspace> <bundle hex> <background model>
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    var sw = Stopwatch.StartNew();
                    var scene = new NB.Core.World.WorldScene(ws, Convert.ToUInt32(args[2], 16), args[3]);
                    int draws = scene.Objects.Sum(o => o.Model?.Draws.Count ?? 0), missing = scene.Objects.Count(o => o.Model == null);
                    Console.WriteLine($"objects={scene.Objects.Count} models={scene.Models.Count} draws(total instanced)={draws} missingModels={missing} in {sw.Elapsed.TotalSeconds:F1}s");
                    foreach (var l in scene.Log.Take(15)) Console.WriteLine("  " + l);
                    return 0;
                }
                case "tex-batch":
                {
                    // tex-batch <workspace> <bundle hex> <dir>: replace every texture of ONE bundle (resident + its streamed top level)
                    // that has a PNG named after it in <dir> (aid_texture_..._0x...mip.png); other bundles keep their copies
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    uint b = Convert.ToUInt32(args[2], 16);
                    var items = Directory.GetFiles(args[3], "*.png").Select(f => { var (rgba, w, h) = NB.Core.Textures.ImageIO.Load(f); return (Path.GetFileNameWithoutExtension(f), rgba, w, h); }).ToList();
                    var res = NB.Core.Textures.TextureReplacer.ReplaceMany(ws, b, items);
                    Console.WriteLine($"{items.Count} image(s): resident {res.ResidentAssets}, streamed {res.StreamedAssets}");
                    foreach (var n in res.Notes) Console.WriteLine("  " + n);
                    return 0;
                }
                case "tex-replace":
                {
                    // tex-replace <workspace> <texture name/stem> <image.png | checker>: replace in every bundle holding it
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    var idx = NB.Core.Project.AssetIndex.LoadOrBuild(ws);
                    string stem = NB.Core.Textures.TextureReplacer.Stem(args[2]);
                    byte[] rgba; int w, h;
                    if (args[3] == "checker")
                    {
                        w = h = 256; rgba = new byte[w * h * 4];
                        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                        {
                            bool on = ((x / 32) + (y / 32)) % 2 == 0; int i = (y * w + x) * 4;
                            rgba[i] = on ? (byte)255 : (byte)0; rgba[i + 1] = on ? (byte)0 : (byte)255; rgba[i + 2] = on ? (byte)255 : (byte)0; rgba[i + 3] = 255;
                        }
                    }
                    else (rgba, w, h) = NB.Core.Textures.ImageIO.Load(args[3]);
                    var bundles = idx.Entries.Where(e => e.Type == "texture" && NB.Core.Textures.TextureReplacer.Stem(e.Name) == stem).Select(e => e.Bundle).Distinct().ToList();
                    Console.WriteLine($"{stem}: in {bundles.Count} bundle(s)");
                    foreach (var b in bundles)
                    {
                        var res = NB.Core.Textures.TextureReplacer.Replace(ws, b, stem, rgba, w, h);
                        Console.WriteLine($"  {b:x6}: resident {res.ResidentAssets}, streamed {res.StreamedAssets}");
                        foreach (var n in res.Notes) Console.WriteLine("    " + n);
                    }
                    return 0;
                }
                case "text-add":
                {
                    // text-add <workspace> <table asset substring, e.g. loctext_banjox_blocks> <string name> <text> [language dir]
                    //   adds a named string (or sets it) in that table: key = djb2 low 16 bits, sorted insert (LocText.AddOrSet).
                    //   --roundtrip: only check that the table rewrites byte-identically.
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    string dir = args.Length > 5 && !args[5].StartsWith("--") ? Path.Combine(ws.Game.Root, "loctext", args[5]) : Path.Combine(ws.Game.Root, "Debug", "11");
                    int edited = 0;
                    foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        NB.Core.Formats.CaffFile c;
                        try { c = NB.Core.Formats.CaffFile.Read(File.ReadAllBytes(f)); } catch { continue; }
                        if (!c.Symbols.Any(s => s.Contains(args[2]))) continue;
                        var part = c.Parts.First(p => c.SectionOf(p).Name == ".data");
                        var t = NB.Core.Formats.LocText.Parse(part.Data);
                        if (args.Contains("--roundtrip"))
                        {
                            bool same = t.Write().AsSpan().SequenceEqual(part.Data);
                            Console.WriteLine($"{Path.GetRelativePath(ws.Game.Root, f)}: {t.Strings.Count} strings, editable {t.Editable}, round-trip {(same ? "identical" : "DIFFERENT")}");
                            return same ? 0 : 1;
                        }
                        ushort key = t.AddOrSet(args[3], args[4]);
                        part.Data = t.Write(); part.Size = part.Data.Length;
                        var check = NB.Core.Formats.LocText.Parse(part.Data);
                        if (check.Names.GetValueOrDefault(key) != args[3] || check.Strings.First(x => x.Key == key).Text != args[4]) throw new InvalidDataException("text table failed validation");
                        File.WriteAllBytes(f, c.Write());
                        ws.Log(Path.GetRelativePath(ws.Game.Root, f), $"text {args[3]} = \"{args[4]}\" (key {key:X4})");
                        Console.WriteLine($"{Path.GetRelativePath(ws.Game.Root, f)}: {args[3]} = \"{args[4]}\" (key {key:X4}, {check.Strings.Count} strings)");
                        edited++;
                    }
                    return edited > 0 ? 0 : 1;
                }
                case "text-set":
                {
                    // text-set <workspace> <string name> <new text> [language dir, default English = Debug/11]
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    string dir = args.Length > 4 ? Path.Combine(ws.Game.Root, "loctext", args[4]) : Path.Combine(ws.Game.Root, "Debug", "11");
                    int edited = 0;
                    foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        var c = NB.Core.Formats.CaffFile.Read(File.ReadAllBytes(f));
                        var part = c.Parts.First(p => c.SectionOf(p).Name == ".data");
                        NB.Core.Formats.LocText t;
                        try { t = NB.Core.Formats.LocText.Parse(part.Data); } catch { continue; }
                        var key = t.Names.FirstOrDefault(kv => kv.Value == args[2]).Key;
                        int i = t.Strings.FindIndex(s => s.Key == key && t.Names.ContainsKey(key));
                        if (i < 0) continue;
                        if (!t.Editable) { Console.WriteLine($"read-only table: {f}"); continue; }
                        Console.WriteLine($"{Path.GetRelativePath(ws.Game.Root, f)}: \"{t.Strings[i].Text}\" -> \"{args[3]}\"");
                        t.Strings[i] = (key, args[3]);
                        part.Data = t.Write();
                        if (NB.Core.Formats.LocText.Parse(part.Data).Strings[i].Text != args[3]) throw new InvalidDataException("text table failed validation");
                        File.WriteAllBytes(f, c.Write());
                        ws.Log(Path.GetRelativePath(ws.Game.Root, f), $"text {args[2]} = \"{args[3]}\"");
                        edited++;
                    }
                    Console.WriteLine($"edited {edited} table(s)");
                    return edited > 0 ? 0 : 1;
                }
                case "scene-shift":
                {
                    // scene-shift <workspace> <bundle hex> <name substring> dx,dy,dz: move every matching scenery instance (studio save path)
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    uint b = Convert.ToUInt32(args[2], 16);
                    var idx = NB.Core.Project.AssetIndex.LoadOrBuild(ws);
                    var w = NB.Core.Project.WorldCatalog.FromIndex(idx).First(x => x.Bundle == b);
                    var scene = new NB.Core.World.WorldScene(ws, b, w.BackgroundModel);
                    var dv = args[4].Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    int n = 0;
                    foreach (var o in scene.Objects.Where(o => o.Kind == NB.Core.World.SceneObjectKind.Scenery && o.Name.Contains(args[3], StringComparison.OrdinalIgnoreCase)))
                    {
                        var m = o.Transform; m.Translation += new System.Numerics.Vector3(dv[0], dv[1], dv[2]); o.Transform = m; n++;
                    }
                    Console.WriteLine($"moved {n} instance(s); saved {scene.Save()} change(s) to bundle {b:x6}");
                    return 0;
                }
                case "marker-set-asset":
                {
                    // marker-set-asset <workspace> <bundle hex> <marker asset name> <record index> <old asset name> <new asset name>
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    uint b = Convert.ToUInt32(args[2], 16);
                    var caff = ws.LoadResident(b);
                    int sym = caff.Symbols.FindIndex(s => NB.Core.Formats.AssetIds.DisplayName(s) == args[3]) + 1;
                    var ma = NB.Core.World.MarkerAsset.Parse(caff, sym);
                    var rec = ma.Records.First(r => r.Index == int.Parse(args[4]));
                    uint oldId = NB.Core.Formats.AssetIds.IdOf(args[5]) ?? throw new ArgumentException("bad old name");
                    uint newId = NB.Core.Formats.AssetIds.IdOf(args[6]) ?? throw new ArgumentException("bad new name");
                    var d = caff.PartsOf(sym).First(p => caff.SectionOf(p).Name == ".data").Data;
                    int hits = 0;
                    for (int o = rec.Offset; o + 4 <= rec.Offset + rec.Size; o += 4)
                        if (NB.Core.IO.BE.U32(d, o) == oldId) { NB.Core.IO.BE.W32(d, o, newId); hits++; Console.WriteLine($"  record +0x{o - rec.Offset:X}: {oldId:X8} -> {newId:X8}"); }
                    if (hits == 0) { Console.WriteLine("old asset id not found in that record"); return 1; }
                    ws.SaveResident(b, caff, $"marker {args[3]} #{args[4]}: {args[5]} -> {args[6]}");
                    Console.WriteLine($"saved ({hits} reference(s) changed)");
                    return 0;
                }
                case "xex-patch":
                {
                    // xex-patch <in default.xex> <out default.xex> <mod id> [mod id...]: bake executable mods into a decrypted XEX (for consoles)
                    if (Path.GetFullPath(args[1]) == Path.GetFullPath(args[2])) { Console.WriteLine("output must differ from input"); return 1; }
                    var bytes = File.ReadAllBytes(args[1]);
                    var xex = NB.Core.Formats.XexFile.Read(bytes);
                    var img = xex.GetImage();
                    var mods = NB.Core.Mods.ExePatches.ResolveAll(args.Skip(3));
                    foreach (var m in mods)
                    {
                        var probs = NB.Core.Mods.ExePatches.Check(img, xex.ImageBase, m);
                        if (probs.Count > 0) { Console.WriteLine($"{m.Id}: {string.Join("; ", probs)}"); return 1; }
                    }
                    var words = mods.SelectMany(m => m.Words).Select(w => (w.Address, w.Patched)).ToList();
                    var outBytes = xex.WritePatched(words);
                    File.WriteAllBytes(args[2], outBytes);
                    var check = NB.Core.Formats.XexFile.Read(outBytes);
                    var img2 = check.GetImage();
                    int diff = 0; for (int i = 0; i < img.Length; i += 4) if (NB.Core.IO.BE.U32(img, i) != NB.Core.IO.BE.U32(img2, i)) diff++;
                    bool ok = words.All(w => NB.Core.IO.BE.U32(img2, (int)(w.Address - check.ImageBase)) == w.Patched);
                    Console.WriteLine($"wrote {args[2]}: encryption {check.EncryptionType}, compression {check.CompressionType}, {diff} image words differ (expected {words.Count}), patched words {(ok ? "verified" : "MISMATCH")}");
                    return ok && diff == words.Count ? 0 : 1;
                }
                case "bundle-sizes":
                {
                    // bundle-sizes <workspace> <bundle hex> [top]: resident memory by section and the largest assets per section
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    var caff = ws.LoadResident(Convert.ToUInt32(args[2], 16));
                    int topN = args.Length > 3 ? int.Parse(args[3]) : 12;
                    Console.WriteLine($"total {caff.Parts.Sum(p => (long)p.Data.Length) / 1048576.0:F1} MB in {caff.Symbols.Count} assets");
                    foreach (var g in caff.Parts.GroupBy(p => caff.SectionOf(p).Name).OrderByDescending(g => g.Sum(p => (long)p.Data.Length)))
                    {
                        Console.WriteLine($"{g.Key,-14} {g.Sum(p => (long)p.Data.Length) / 1048576.0,7:F1} MB");
                        foreach (var p in g.OrderByDescending(p => p.Data.Length).Take(topN))
                            Console.WriteLine($"    {p.Data.Length / 1048576.0,6:F2}  {NB.Core.Formats.AssetIds.DisplayName(caff.Symbols[p.Symbol - 1])}");
                    }
                    foreach (var g in caff.Parts.GroupBy(p => { var n = NB.Core.Formats.AssetIds.DisplayName(caff.Symbols[p.Symbol - 1]); var k = n.IndexOf('_', 4); return k > 0 ? n[..k] : n; })
                                 .OrderByDescending(g => g.Sum(p => (long)p.Data.Length)).Take(10))
                        Console.WriteLine($"type {g.Key,-18} {g.Sum(p => (long)p.Data.Length) / 1048576.0,7:F1} MB");
                    return 0;
                }
                case "gpu-compact":
                {
                    // gpu-compact <workspace> <bundle hex> [--strip-hidden] [--dry]: frees model vertex/index memory in the
                    // resident bundle. --strip-hidden: models used only by hidden scenery (instances below y -200) and by no
                    // other asset keep their draws but point at a block of zeros. Then every model's unreferenced .gpu bytes
                    // are dropped.
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    uint b = Convert.ToUInt32(args[2], 16);
                    var caff = ws.LoadResident(b);
                    var r = NB.Core.World.SceneBuilder.CompactModels(caff, b, ws, args.Contains("--strip-hidden"));
                    foreach (var line in r) Console.WriteLine(line);
                    if (!args.Contains("--dry")) { ws.SaveResident(b, caff, "gpu-compact" + (args.Contains("--strip-hidden") ? " --strip-hidden" : "")); Console.WriteLine("saved"); }
                    return 0;
                }
                case "tex-only-stripped":
                {
                    // tex-only-stripped <workspace> <bundle hex>: diagnostics — texture memory referenced (by name string)
                    // only from models whose .gpu is a stripped zero block
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    var caff = ws.LoadResident(Convert.ToUInt32(args[2], 16));
                    var stripped = new HashSet<int>();
                    for (int s = 1; s <= caff.Symbols.Count; s++)
                    {
                        var g = caff.PartsOf(s).FirstOrDefault(p => caff.SectionOf(p).Name == ".gpu");
                        if (g != null && g.Data.Length <= 65536 && g.Data.All(x => x == 0) && NB.Core.Formats.AssetIds.DisplayName(caff.Symbols[s - 1]).StartsWith("aid_model_")) stripped.Add(s);
                    }
                    var byStripped = new HashSet<string>(); var byOthers = new HashSet<string>();
                    var pre = System.Text.Encoding.ASCII.GetBytes("aid_texture_");
                    foreach (var p in caff.Parts)
                    {
                        var sec = caff.SectionOf(p).Name; if (sec == ".gpu" || sec == ".texturegpu") continue;
                        var span = p.Data.AsSpan(); int at = 0;
                        while (true)
                        {
                            int k = span[at..].IndexOf(pre); if (k < 0) break; k += at;
                            int e = k; while (e < span.Length && span[e] != 0 && e - k < 256) e++;
                            var nm = NB.Core.Textures.TextureResolver.Stem(System.Text.Encoding.ASCII.GetString(span[k..e]));
                            if (caff.Symbols[p.Symbol - 1].Contains("texture")) { } else (stripped.Contains(p.Symbol) ? byStripped : byOthers).Add(nm);
                            at = e;
                        }
                    }
                    var only = byStripped.Except(byOthers).ToHashSet();
                    long bytes = 0; int n = 0;
                    for (int s = 1; s <= caff.Symbols.Count; s++)
                    {
                        var nm = NB.Core.Formats.AssetIds.DisplayName(caff.Symbols[s - 1]);
                        if (!nm.StartsWith("aid_texture_") || !only.Contains(NB.Core.Textures.TextureResolver.Stem(nm))) continue;
                        long b = caff.PartsOf(s).Where(p => caff.SectionOf(p).Name == ".texturegpu").Sum(p => (long)p.Data.Length);
                        bytes += b; n++;
                        if (args.Contains("-v")) Console.WriteLine($"  {b / 1024,6} KB {nm}");
                    }
                    Console.WriteLine($"{stripped.Count} stripped models; {only.Count} texture stems only they reference; {n} resident assets, {bytes / 1048576.0:F1} MB");
                    return 0;
                }
                case "bundle-pad":
                {
                    // bundle-pad <workspace> <bundle hex> <MB> [section]: TEST AID — appends unreferenced zero bytes to the largest
                    // part of a section (default .data) so the resident bundle takes more memory (simulates a console with less
                    // free memory than Xenia). Never ship a padded bundle.
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    uint b = Convert.ToUInt32(args[2], 16);
                    var caff = ws.LoadResident(b);
                    string sec = args.Length > 4 ? args[4] : ".data";
                    var part = caff.Parts.Where(p => caff.SectionOf(p).Name == sec).OrderByDescending(p => p.Data.Length).First();
                    int add = (int)(double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) * 1024 * 1024);
                    var nd = new byte[part.Data.Length + add]; Buffer.BlockCopy(part.Data, 0, nd, 0, part.Data.Length);
                    part.Data = nd; part.Size = nd.Length;
                    ws.SaveResident(b, caff, $"TEST bundle-pad {args[3]} MB in {sec}");
                    Console.WriteLine($"padded {caff.Symbols[part.Symbol - 1]} {sec} by {add:N0} bytes");
                    return 0;
                }
                case "mopp-stats":
                {
                    // mopp-stats <workspace> <bundle hex> <havok asset | --all-mesh> [queries]: how many triangles small physics
                    // queries (1.5-unit boxes, 3-unit rays near the surface) reach through the MOPP, vs the triangles they
                    // really touch. Every reached triangle costs a narrow-phase test on the console CPU.
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    var caff = ws.LoadResident(Convert.ToUInt32(args[2], 16));
                    int nq = args.Length > 4 ? int.Parse(args[4]) : 400;
                    var syms = args[3] == "--all-mesh"
                        ? Enumerable.Range(1, caff.Symbols.Count).Where(s => caff.Symbols[s - 1].StartsWith("aid_havok_")).ToList()
                        : new List<int> { Enumerable.Range(1, caff.Symbols.Count).First(s => NB.Core.Formats.AssetIds.DisplayName(caff.Symbols[s - 1]) == args[3]) };
                    foreach (int s in syms)
                    {
                        var av = new NB.Core.Formats.AssetView(caff, s);
                        if (!av.Has(".data")) continue;
                        var d = av.Data(".data");
                        if (!NB.Core.Havok.HkPackfile.IsPackfileAsset(d)) continue;
                        NB.Core.Havok.HkCollision hc; NB.Core.Havok.HkPackfile pf;
                        try { hc = NB.Core.Havok.HkCollision.ExtractAsset(d); pf = NB.Core.Havok.HkPackfile.FromAsset(d); } catch { continue; }
                        var mesh = hc.Meshes.Where(m => m.Kind == "mesh").ToList();
                        var moppObj = pf.ObjectsOf("hkpMoppCode").FirstOrDefault();
                        if (mesh.Count != 1 || moppObj == null) continue;
                        var P = mesh[0].Positions; var T = mesh[0].Triangles; int nt = T.Count / 3;
                        if (args[3] == "--all-mesh" && nt < 2000) continue;
                        var (cp, cn) = moppObj.Array("data");
                        var code = pf.SectionBytes(cp!.Value.Section).AsSpan(cp.Value.Offset, cn).ToArray();
                        var info = moppObj.Struct("info").V4("offset");
                        var rng = new Random(1);
                        var tmn = new System.Numerics.Vector3[nt]; var tmx = new System.Numerics.Vector3[nt];
                        for (int t = 0; t < nt; t++)
                        {
                            tmn[t] = System.Numerics.Vector3.Min(P[T[3 * t]], System.Numerics.Vector3.Min(P[T[3 * t + 1]], P[T[3 * t + 2]]));
                            tmx[t] = System.Numerics.Vector3.Max(P[T[3 * t]], System.Numerics.Vector3.Max(P[T[3 * t + 1]], P[T[3 * t + 2]]));
                        }
                        long boxReached = 0, boxTrue = 0, rayReached = 0; int boxMax = 0, rayMax = 0;
                        var h = new System.Numerics.Vector3(1.5f);
                        for (int i = 0; i < nq; i++)
                        {
                            int t = rng.Next(nt);
                            var c = (P[T[3 * t]] + P[T[3 * t + 1]] + P[T[3 * t + 2]]) / 3 + new System.Numerics.Vector3(0, 1, 0);
                            var got = NB.Core.Havok.MoppBuilder.Query(code, info, NB.Core.Havok.MoppBuilder.QueryKind.Aabb, c - h, c + h);
                            boxReached += got.Count; boxMax = Math.Max(boxMax, got.Count);
                            for (int k = 0; k < nt; k++)
                                if (tmn[k].X <= c.X + h.X && tmx[k].X >= c.X - h.X && tmn[k].Y <= c.Y + h.Y && tmx[k].Y >= c.Y - h.Y && tmn[k].Z <= c.Z + h.Z && tmx[k].Z >= c.Z - h.Z) boxTrue++;
                            var ray = NB.Core.Havok.MoppBuilder.Query(code, info, NB.Core.Havok.MoppBuilder.QueryKind.Ray, c + new System.Numerics.Vector3(0, 1.5f, 0), c - new System.Numerics.Vector3(0, 1.5f, 0));
                            rayReached += ray.Count; rayMax = Math.Max(rayMax, ray.Count);
                        }
                        var ext = tmx.Aggregate(System.Numerics.Vector3.Max) - tmn.Aggregate(System.Numerics.Vector3.Min);
                        Console.WriteLine($"{NB.Core.Formats.AssetIds.DisplayName(caff.Symbols[s - 1])}: {nt} tris, extent {ext.X:F0}x{ext.Y:F0}x{ext.Z:F0}, MOPP {cn} B | 3-unit box reaches avg {(double)boxReached / nq:F1} (max {boxMax}, touching {(double)boxTrue / nq:F1}) | 3-unit ray reaches avg {(double)rayReached / nq:F1} (max {rayMax})");
                    }
                    return 0;
                }
                case "collision-import":
                {
                    // collision-import <workspace> <bundle hex> <havok asset name> <mesh.obj|fbx> [--offset x,y,z] [--scale s] [--box] [--material 24hex] [--force-breakable] [--dry]
                    //   replace a mesh collision asset's triangles (new MOPP); --box = box of the mesh AABB; --info = dump the packfile
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    uint b = Convert.ToUInt32(args[2], 16);
                    var caff = ws.LoadResident(b);
                    if (args[3] == "--selftest")
                    {
                        // every packfile: writer round-trip byte-identical; every mesh collision: re-import its own triangles (in memory)
                        int rt = 0, rtBad = 0, meshOk = 0, boxOk = 0, meshBad = 0, origMiss = 0, genMiss = 0, queries = 0, origStruct = 0, genStruct = 0; long origHits = 0, genHits = 0;
                        for (int s = 1; s <= caff.Symbols.Count; s++)
                        {
                            if (!caff.Symbols[s - 1].StartsWith("aid_havok_")) continue;
                            var av = new NB.Core.Formats.AssetView(caff, s);
                            if (!av.Has(".data")) continue;
                            var d = av.Data(".data");
                            if (!NB.Core.Havok.HkPackfile.IsPackfileAsset(d)) continue;
                            var pfb = d.AsSpan(NB.Core.IO.BE.S32(d, 0x20), NB.Core.IO.BE.S32(d, 0x24)).ToArray();
                            if (NB.Core.Havok.HkPackfileWriter.Read(pfb).Write().AsSpan().SequenceEqual(pfb)) rt++; else { rtBad++; Console.WriteLine("round-trip differs: " + caff.Symbols[s - 1]); }
                            if (NB.Core.Havok.HkCollisionImport.TypeOneEntry(d) < 0) continue;
                            try
                            {
                                var hc = NB.Core.Havok.HkCollision.ExtractAsset(d);
                                var ms = hc.Meshes.Where(m => m.Kind == "mesh").ToList();
                                if (hc.Meshes.Count == 0 || hc.Notes.Count > 0) continue;
                                // own triangle mesh re-imported; shape-subpart collisions (boxes/convex pieces) get the box of their bounds
                                bool own = ms.Count == 1 && hc.Meshes.Count == 1;
                                var allP = hc.Meshes.SelectMany(m => m.Positions).ToList();
                                var (P, T) = own ? (ms[0].Positions, ms[0].Triangles)
                                    : NB.Core.Havok.HkCollisionImport.Box(allP.Aggregate(System.Numerics.Vector3.Min), allP.Aggregate(System.Numerics.Vector3.Max));
                                var (nd, _) = NB.Core.Havok.HkCollisionImport.ReplaceData(d, P, T, null, allowBreakable: true);
                                NB.Core.Havok.HkCollisionImport.Verify(nd, P, T);
                                if (own) meshOk++; else boxOk++;
                                // structure: the same invariants every shipped mesh collision satisfies
                                foreach (var (tag, bytes) in own ? new[] { ("original", d), ("generated", nd) } : new[] { ("generated", nd) })
                                {
                                    var probs = NB.Core.Havok.HkCollisionImport.CheckStructure(bytes);
                                    foreach (var pr in probs) Console.WriteLine($"{tag} {NB.Core.Formats.AssetIds.DisplayName(caff.Symbols[s - 1])}: {pr}");
                                    if (tag == "original") origStruct += probs.Count; else genStruct += probs.Count;
                                    // spatial: AABB / ray / linear-cast queries modelled on the game's MOPP machines
                                    var mc = NB.Core.Havok.HkPackfile.FromAsset(bytes).ObjectsOf("hkpMoppCode").First();
                                    var (cp, cn) = mc.Array("data");
                                    var code = mc.File.SectionBytes(cp!.Value.Section).AsSpan(cp.Value.Offset, cn).ToArray();
                                    var (nq, miss, hits) = NB.Core.Havok.MoppBuilder.SelfTest(code, mc.Struct("info").V4("offset"), P, T, s, 20);
                                    if (tag == "original") { origMiss += miss; origHits += hits; } else { genMiss += miss; genHits += hits; queries += nq; }
                                }
                            }
                            catch (Exception ex) { meshBad++; Console.WriteLine($"{NB.Core.Formats.AssetIds.DisplayName(caff.Symbols[s - 1])}: {ex.Message}"); }
                        }
                        Console.WriteLine($"packfile writer round-trip: {rt} identical, {rtBad} different; re-import of own mesh: {meshOk} verified; box onto shape-subpart collisions: {boxOk} verified; {meshBad} failed");
                        Console.WriteLine($"structure problems: original {origStruct}, generated {genStruct}");
                        Console.WriteLine($"{queries} generated-MOPP queries (AABB/ray/linear cast, game machine semantics): original MOPPs missed {origMiss} triangles ({origHits} reached), generated missed {genMiss} ({genHits} reached)");
                        return rtBad + meshBad + genMiss + genStruct == 0 ? 0 : 3;
                    }
                    int sym = caff.Symbols.FindIndex(s => NB.Core.Formats.AssetIds.DisplayName(s) == args[3]) + 1;
                    if (sym == 0) { Console.WriteLine("havok asset not found in bundle"); return 1; }
                    var before = new NB.Core.Formats.AssetView(caff, sym).Data(".data");
                    if (args.Contains("--info"))
                    {
                        var pfi = NB.Core.Havok.HkPackfile.FromAsset(before);
                        Console.Write(NB.Core.Havok.HkCollisionImport.Describe(pfi));
                        foreach (var (n, v) in NB.Core.Havok.HkCollisionImport.EnumItems(pfi, "hkpExtendedMeshShape", "weldingType")) Console.WriteLine($"  weldingType {n} = {v}");
                        return 0;
                    }
                    string Opt(string name) { int i = Array.IndexOf(args, name); return i > 0 && i + 1 < args.Length ? args[i + 1] : ""; }
                    float scale = Opt("--scale") is { Length: > 0 } ss ? float.Parse(ss, System.Globalization.CultureInfo.InvariantCulture) : 1f;
                    var offset = System.Numerics.Vector3.Zero;
                    if (Opt("--offset") is { Length: > 0 } os)
                    {
                        var f = os.Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                        offset = new System.Numerics.Vector3(f[0], f[1], f[2]);
                    }
                    var old = NB.Core.Havok.HkCollision.ExtractAsset(before);
                    Console.WriteLine("current: " + string.Join(", ", old.Meshes.Select(m => $"{m.Kind} {m.Positions.Count}v/{m.Triangles.Count / 3}t")));
                    var meshes = NB.Core.Models.ObjReader.ReadAny(args[4]);
                    var (inP, inT) = NB.Core.Havok.HkCollisionImport.Merge(meshes, scale, offset);
                    if (args.Contains("--box")) (inP, inT) = NB.Core.Havok.HkCollisionImport.Box(inP.Aggregate(System.Numerics.Vector3.Min), inP.Aggregate(System.Numerics.Vector3.Max));
                    Console.WriteLine($"input: {inT.Count / 3} triangles / {inP.Count} vertices, bounds {inP.Aggregate(System.Numerics.Vector3.Min)} .. {inP.Aggregate(System.Numerics.Vector3.Max)}");
                    byte[]? material = Opt("--material") is { Length: 24 } mh ? Convert.FromHexString(mh) : null;
                    NB.Core.Havok.HkCollisionImport.Result res;
                    try { res = NB.Core.Havok.HkCollisionImport.Replace(caff, sym, inP, inT, material, args.Contains("--force-breakable")); }
                    catch (InvalidDataException ex) { Console.WriteLine("not imported: " + ex.Message); return 1; }
                    Console.WriteLine($"replaced {res.OldTriangles} triangles / {res.OldVertices} vertices with {res.Triangles} / {res.Vertices}; MOPP {res.MoppBytes} bytes; asset .data {before.Length} -> {new NB.Core.Formats.AssetView(caff, sym).Data(".data").Length} bytes");
                    foreach (var n in res.Notes) Console.WriteLine("  " + n);
                    // verify on the serialized container
                    var back = NB.Core.Formats.CaffFile.Read(caff.Write());
                    Console.WriteLine("in-memory " + NB.Core.Havok.HkCollisionImport.Verify(new NB.Core.Formats.AssetView(back, sym).Data(".data"), inP, inT));
                    if (args.Contains("--dry")) return 0;
                    ws.SaveResident(b, caff, $"imported collision {Path.GetFileName(args[4])}{(args.Contains("--box") ? " (box)" : "")} into {args[3]}");
                    var saved = ws.LoadResident(b);
                    int sym2 = saved.Symbols.FindIndex(s => NB.Core.Formats.AssetIds.DisplayName(s) == args[3]) + 1;
                    var d2 = new NB.Core.Formats.AssetView(saved, sym2).Data(".data");
                    var hc2 = NB.Core.Havok.HkCollision.ExtractAsset(d2);
                    var all2 = hc2.Meshes.SelectMany(m => m.Positions).ToList();
                    Console.WriteLine($"saved asset re-parsed: {string.Join(", ", hc2.Meshes.Select(m => $"{m.Kind} {m.Positions.Count}v/{m.Triangles.Count / 3}t"))}, bounds {all2.Aggregate(System.Numerics.Vector3.Min)} .. {all2.Aggregate(System.Numerics.Vector3.Max)}");
                    Console.WriteLine("saved " + NB.Core.Havok.HkCollisionImport.Verify(d2, inP, inT));
                    return 0;
                }
                case "vehicle-replace":
                {
                    // vehicle-replace <workspace> <vehicle asset name> <blueprint.bin>: replace a blueprint's .data (0x7C header + 0x24-byte blocks) in every bundle holding it
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    var idx = NB.Core.Project.AssetIndex.LoadOrBuild(ws);
                    var data = File.ReadAllBytes(args[3]);
                    var va = NB.Core.Tags.VehicleAsset.TryParse(data) ?? throw new InvalidDataException("not a valid blueprint (header + count × 0x24 blocks)");
                    int done = 0;
                    foreach (var e in idx.Entries.Where(x => x.Name == args[2] && x.Symbol > 0 && !x.Streamed).GroupBy(x => x.Bundle).Select(g => g.First()))
                    {
                        var caff = ws.LoadResident(e.Bundle);
                        var part = caff.PartsOf(e.Symbol).First(p => caff.SectionOf(p).Name == ".data");
                        int pid = caff.Parts.IndexOf(part) + 1;
                        if (caff.Relocs.Any(r => r.FromPart == pid)) { Console.WriteLine($"{e.Bundle:x6}: blueprint has pointers, skipped"); continue; }
                        int old = part.Data.Length;
                        part.Data = data;
                        ws.SaveResident(e.Bundle, caff, $"blueprint {args[2]} replaced ({va.Count} blocks, {old} -> {data.Length} bytes)");
                        Console.WriteLine($"{e.Bundle:x6} {args[2]}: {old} -> {data.Length} bytes, {va.Count} blocks"); done++;
                    }
                    return done > 0 ? 0 : 1;
                }
                case "marker-move":
                {
                    // marker-move <workspace> <bundle hex> <marker asset name> <record index> x,y,z [--type T] : set a marker record's position
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    uint b = Convert.ToUInt32(args[2], 16);
                    var caff = ws.LoadResident(b);
                    int sym = caff.Symbols.FindIndex(s => NB.Core.Formats.AssetIds.DisplayName(s) == args[3]) + 1;
                    var ma = NB.Core.World.MarkerAsset.Parse(caff, sym);
                    int? mtype = Array.IndexOf(args, "--type") is int ti && ti > 0 ? int.Parse(args[ti + 1]) : null;
                    var rec = ma.Records.First(r => r.Index == int.Parse(args[4]) && (mtype == null || r.Type == mtype));
                    var p = args[5].Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    var before = rec.Position;
                    rec.Position = new System.Numerics.Vector3(p[0], p[1], p[2]);
                    NB.Core.World.MarkerAsset.WriteTransform(caff, sym, rec);
                    ws.SaveResident(b, caff, $"marker {args[3]} #{args[4]}: {before} -> {rec.Position}");
                    Console.WriteLine($"marker #{args[4]} ({NB.Core.World.MarkerRecord.TypeName(rec.Type)}): {before} -> {rec.Position}");
                    return 0;
                }
                case "path-link":
                {
                    // path-link <workspace> <bundle hex> <marker asset name> <node index> <next node index> : re-route a path (type-22 markers)
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    uint b = Convert.ToUInt32(args[2], 16);
                    var caff = ws.LoadResident(b);
                    int sym = caff.Symbols.FindIndex(s => NB.Core.Formats.AssetIds.DisplayName(s) == args[3]) + 1;
                    var ma = NB.Core.World.MarkerAsset.Parse(caff, sym);
                    var rec = ma.Records.First(r => r.Index == int.Parse(args[4]));
                    int next = int.Parse(args[5]);
                    if (next != rec.Index && !ma.Records.Any(r => r.Index == next && r.Type == 22)) { Console.WriteLine($"#{next} is not a path node in {args[3]}"); return 1; }
                    int before = rec.Link; rec.Link = next;
                    NB.Core.World.MarkerAsset.WriteLink(caff, sym, rec);
                    ws.SaveResident(b, caff, $"path {args[3]} node #{rec.Index}: next {before} -> {next}");
                    Console.WriteLine($"path node #{rec.Index}: next {before} -> {next}");
                    return 0;
                }
                case "script-set":
                {
                    // script-set <workspace> <script name> <opcode hex> <byte offset in command, hex> <f:float | u:uint | h:hex>  (first command with that opcode)
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    var caff = ws.LoadResident(NB.Core.World.TestMode.CommonBundle);
                    int sym = caff.Symbols.FindIndex(s => NB.Core.Formats.AssetIds.DisplayName(s) == args[2]) + 1;
                    var part = caff.PartsOf(sym).First(p => caff.SectionOf(p).Name == ".data");
                    var sc = NB.Core.World.ScriptAsset.Parse(part.Data);
                    var cmd = sc.Commands.First(c => c.Op == Convert.ToInt32(args[3], 16));
                    int off = Convert.ToInt32(args[4], 16);
                    string before = $"{NB.Core.IO.BE.U32(cmd.Data, off):X8} ({NB.Core.IO.BE.F32(cmd.Data, off):G6})";
                    var v = args[5];
                    if (v[0] == 'f') NB.Core.IO.BE.WF32(cmd.Data, off, float.Parse(v[2..], System.Globalization.CultureInfo.InvariantCulture));
                    else if (v[0] == 'u') NB.Core.IO.BE.W32(cmd.Data, off, uint.Parse(v[2..]));
                    else NB.Core.IO.BE.W32(cmd.Data, off, Convert.ToUInt32(v[2..], 16));
                    part.Data = sc.Write();
                    ws.SaveResident(NB.Core.World.TestMode.CommonBundle, caff, $"{args[2]} op {args[3]} +0x{off:X}: {before} -> {v}");
                    Console.WriteLine($"{args[2]} op 0x{cmd.Op:X2} +0x{off:X}: {before} -> {v}");
                    return 0;
                }
                case "paths":
                {
                    // paths <workspace> <bundle hex>: path-node link statistics for every marker asset in a bundle
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    var caff = ws.LoadResident(Convert.ToUInt32(args[2], 16));
                    for (int s = 1; s <= caff.Symbols.Count; s++)
                    {
                        if (!caff.Symbols[s - 1].StartsWith("aid_marker_")) continue;
                        var ma = NB.Core.World.MarkerAsset.Parse(caff, s);
                        var nodes = ma.Records.Where(r => r.Type == 22).ToList();
                        if (nodes.Count == 0) continue;
                        var byIdx = nodes.GroupBy(r => r.Index).ToDictionary(g => g.Key, g => g.First());
                        if (byIdx.Count != nodes.Count) Console.WriteLine($"  note: {nodes.Count - byIdx.Count} duplicate path-node indices");
                        int resolved = nodes.Count(n => n.Link != n.Index && byIdx.TryGetValue(n.Link, out var t) && t.Type == 22);
                        int ends = nodes.Count(n => !byIdx.ContainsKey(n.Link) || n.Link == n.Index);
                        var heads = nodes.Select(n => n.Index).Except(nodes.Select(n => n.Link)).Count();
                        double avg = nodes.Where(n => byIdx.TryGetValue(n.Link, out var t) && t.Type == 22).Select(n => (double)System.Numerics.Vector3.Distance(n.Position, byIdx[n.Link].Position)).DefaultIfEmpty(0).Average();
                        Console.WriteLine($"{NB.Core.Formats.AssetIds.DisplayName(ma.Name)}: {nodes.Count} path nodes, {resolved} link to another path node, {ends} end/unresolved, {heads} path starts, mean link length {avg:F1}");
                    }
                    return 0;
                }
                case "act-markers":
                {
                    // act-markers <workspace> <act bundle hex> [filter]: markers of one act with positions
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    var caff = ws.LoadResident(Convert.ToUInt32(args[2], 16));
                    var names = new Dictionary<uint, string>();
                    foreach (var s in caff.Symbols) if (NB.Core.Formats.AssetIds.IdOf(s) is uint id) names.TryAdd(id, NB.Core.Formats.AssetIds.DisplayName(s));
                    for (int s = 1; s <= caff.Symbols.Count; s++)
                    {
                        if (!caff.Symbols[s - 1].StartsWith("aid_marker_")) continue;
                        var ma = NB.Core.World.MarkerAsset.Parse(caff, s);
                        foreach (var r in ma.Records)
                        {
                            string desc = $"{NB.Core.World.MarkerRecord.TypeName(r.Type)} #{r.Index} {string.Join(" ", r.AssetIds.Select(x => names.GetValueOrDefault(x, "")).Where(x => x != "").Select(x => x.Replace("aid_objparams_banjox_", "")))} {string.Join(" ", r.Strings)}";
                            if (args.Length > 3 && !desc.Contains(args[3], StringComparison.OrdinalIgnoreCase)) continue;
                            Console.WriteLine($"{r.Position.X,9:F1} {r.Position.Y,9:F1} {r.Position.Z,9:F1}  {desc}  [{NB.Core.Formats.AssetIds.DisplayName(ma.Name)}]");
                        }
                    }
                    return 0;
                }
                case "acts":
                {
                    // acts <workspace>: every act, its bundle and the world bundle it loads
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    var acts = NB.Core.Project.ActCatalog.Build(ws, NB.Core.Project.AssetIndex.LoadOrBuild(ws));
                    foreach (var a in acts) Console.WriteLine($"{a.Display,-28} act bundle {a.ActBundle:x6}  world bundle {(a.WorldBundle == 0 ? "?" : a.WorldBundle.ToString("x6"))}  {a.Script}");
                    Console.WriteLine($"{acts.Count} acts");
                    return 0;
                }
                case "world-textures":
                {
                    // world-textures <workspace> <world bundle hex> [--old]: where every diffuse texture of a world comes from; --old = world bundle only
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    var idx = NB.Core.Project.AssetIndex.LoadOrBuild(ws);
                    uint b = Convert.ToUInt32(args[2], 16);
                    var we = NB.Core.Project.WorldCatalog.FromIndex(idx).First(x => x.Bundle == b);
                    var sc = new NB.Core.World.WorldScene(ws, we.Bundle, we.BackgroundModel);
                    var names = sc.DiffuseTextureNames().ToList();
                    if (!args.Contains("--old")) sc.Textures = new NB.Core.Textures.TextureResolver(ws, idx, sc.Caff);
                    int ok = names.Count(n => sc.LoadTexture(n) != null);
                    Console.WriteLine($"{we.Display}: {names.Count} diffuse textures, {ok} decoded, {names.Count - ok} missing");
                    if (sc.Textures != null)
                    {
                        foreach (var g in sc.Textures.Sources.Values.GroupBy(v => v.Split(' ', 3)[0] + (v.StartsWith("resident") || v.StartsWith("streamed") ? " " + v.Split(' ')[1] : "")).OrderByDescending(g => g.Count()))
                            Console.WriteLine($"  {g.Count(),5} × {g.Key}");
                        foreach (var m in sc.Textures.Missing.Take(10)) Console.WriteLine($"  missing: {m} — {sc.Textures.Sources[m]}");
                    }
                    return 0;
                }
                case "world-nested":
                {
                    // world-nested <workspace> <world bundle hex> [min extent]: scenery objects with nested reference models; lists large children
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    var idx = NB.Core.Project.AssetIndex.LoadOrBuild(ws);
                    uint b = Convert.ToUInt32(args[2], 16);
                    float minExt = args.Length > 3 ? float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 200;
                    var we = NB.Core.Project.WorldCatalog.FromIndex(idx).First(x => x.Bundle == b);
                    var sc = new NB.Core.World.WorldScene(ws, we.Bundle, we.BackgroundModel);
                    var withKids = sc.Objects.Where(o => o.Children.Count > 0).ToList();
                    Console.WriteLine($"{withKids.Count} objects with nested models, {withKids.Sum(o => o.Children.Count)} nested placements");
                    foreach (var o in withKids)
                        foreach (var (cm, cl) in o.Children)
                        {
                            var mn = new System.Numerics.Vector3(float.MaxValue); var mx = -mn;
                            foreach (var d in cm.Draws) foreach (var p in d.Positions) { var q = System.Numerics.Vector3.Transform(p, cl); mn = System.Numerics.Vector3.Min(mn, q); mx = System.Numerics.Vector3.Max(mx, q); }
                            var ext = mx - mn;
                            if (Math.Max(ext.X, Math.Max(ext.Y, ext.Z)) >= minExt)
                                Console.WriteLine($"  {o.Name} -> {NB.Core.Formats.AssetIds.DisplayName(cm.View.Name).Replace("aid_model_banjox_", "")}: extent {ext.X:F0} x {ext.Y:F0} x {ext.Z:F0}, local pos {cl.Translation}, scale {cl.M11:F2},{cl.M22:F2},{cl.M33:F2}");
                        }
                    return 0;
                }
                case "clone-place":
                {
                    // clone-place <workspace> <world bundle hex> <source model> <new name> x,y,z [yawDeg] [--template <instance index>] [--no-collision]
                    // clones a reference model (and its aid_havok_ collision) as aid_model_banjox_<new name> and places one instance
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    var idx = NB.Core.Project.AssetIndex.LoadOrBuild(ws);
                    uint b = Convert.ToUInt32(args[2], 16);
                    var we = NB.Core.Project.WorldCatalog.FromIndex(idx).First(x => x.Bundle == b);
                    var caff = ws.LoadResident(b);
                    int bg = caff.Symbols.IndexOf(we.BackgroundModel) + 1;
                    int src = caff.Symbols.FindIndex(s => NB.Core.Formats.AssetIds.DisplayName(s) == args[3]) + 1;
                    if (src == 0) { Console.WriteLine($"{args[3]} not in bundle {b:x6}"); return 1; }
                    string newModel = "aid_model_banjox_" + args[4], newHavok = "aid_havok_banjox_" + args[4];
                    var p = args[5].Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    float yaw = args.Length > 6 && !args[6].StartsWith("--") ? float.Parse(args[6], System.Globalization.CultureInfo.InvariantCulture) : 0;
                    int template = args.Contains("--template") ? int.Parse(args[Array.IndexOf(args, "--template") + 1]) : 0;
                    NB.Core.Formats.CaffEdit.CloneAsset(caff, src, newModel);
                    uint modelId = NB.Core.Formats.AssetIds.IdOf(newModel) ?? throw new InvalidDataException("cannot hash " + newModel);
                    uint? havokId = null;
                    string srcHavok = "aid_havok_" + args[3]["aid_model_".Length..];
                    int hsym = caff.Symbols.FindIndex(s => NB.Core.Formats.AssetIds.DisplayName(s) == srcHavok) + 1;
                    if (hsym > 0 && !args.Contains("--no-collision")) { NB.Core.Formats.CaffEdit.CloneAsset(caff, hsym, newHavok); havokId = NB.Core.Formats.AssetIds.IdOf(newHavok); }
                    var world = System.Numerics.Matrix4x4.CreateRotationY(yaw * MathF.PI / 180) * System.Numerics.Matrix4x4.CreateTranslation(p[0], p[1], p[2]);
                    if (args.Contains("--clone-only")) { ws.SaveResident(b, caff, $"clone {args[3]} -> {newModel} (not placed)"); Console.WriteLine($"cloned only: {newModel} {modelId:X8}"); return 0; }
                    if (args.Contains("--ref-only")) { int ri = NB.Core.World.InstanceEditor.AddReferenceModel(caff, bg, modelId); ws.SaveResident(b, caff, $"clone {args[3]} -> {newModel}, reference #{ri} (no instance)"); Console.WriteLine($"cloned + reference #{ri}"); return 0; }
                    int n = NB.Core.World.InstanceEditor.AddModelInstance(caff, bg, template, modelId, havokId, world, args[4]);
                    ws.SaveResident(b, caff, $"clone {args[3]} -> {newModel} (id {modelId:X8}{(havokId != null ? $", collision {newHavok} {havokId:X8}" : ", no collision")}), instance #{n} at {args[5]}");
                    Console.WriteLine($"cloned {args[3]} -> {newModel} id {modelId:X8}; havok {(havokId != null ? $"{newHavok} {havokId:X8}" : "none")}; instance #{n}");
                    return 0;
                }
                case "dup-place":
                {
                    // dup-place <workspace> <world bundle hex> <instance index> x,y,z [yawDeg]: duplicate a scenery instance to a new place
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    var idx = NB.Core.Project.AssetIndex.LoadOrBuild(ws);
                    uint b = Convert.ToUInt32(args[2], 16);
                    var we = NB.Core.Project.WorldCatalog.FromIndex(idx).First(x => x.Bundle == b);
                    var caff = ws.LoadResident(b);
                    int bg = caff.Symbols.IndexOf(we.BackgroundModel) + 1;
                    var p = args[4].Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    float yaw = args.Length > 5 ? float.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture) : 0;
                    var world = System.Numerics.Matrix4x4.CreateRotationY(yaw * MathF.PI / 180) * System.Numerics.Matrix4x4.CreateTranslation(p[0], p[1], p[2]);
                    int n = NB.Core.World.InstanceEditor.Duplicate(caff, bg, int.Parse(args[3]), world);
                    ws.SaveResident(b, caff, $"duplicate instance #{args[3]} -> #{n} at {args[4]}");
                    Console.WriteLine($"instance #{args[3]} duplicated as #{n}");
                    return 0;
                }
                case "scene-build":
                {
                    // scene-build <workspace> <scene.json>: assemble a custom world (see NB.Core.World.SceneBuilder, docs/SCENE_FORMAT.md)
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    var idx = NB.Core.Project.AssetIndex.LoadOrBuild(ws);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    string last = "";
                    var rep = NB.Core.World.SceneBuilder.Build(ws, idx, args[2], new Progress<(string, double)>(p => { var s = p.Item1.Split(' ')[0]; if (s != last) { last = s; Console.WriteLine($"  [{sw.Elapsed.TotalSeconds,6:F1}s] {p.Item1}"); } }));
                    Console.WriteLine($"scene built in {sw.Elapsed.TotalSeconds:F1}s: {rep.Textures} textures, {rep.Models} models, {rep.Instances} instances, {rep.Hidden} hidden, {rep.Markers} markers, terrain {rep.TerrainTriangles} tris, collision {rep.CollisionTriangles} tris");
                    foreach (var n in rep.Notes) Console.WriteLine("  " + n);
                    foreach (var e in rep.Errors) Console.WriteLine("  ERROR " + e);
                    return rep.Errors.Count == 0 ? 0 : 2;
                }
                case "tex-create":
                {
                    // tex-create <workspace> <bundle hex> <new texture name> <image> [WxH]: new resident DXT1 texture (full mip chain)
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    uint b = Convert.ToUInt32(args[2], 16);
                    var caff = ws.LoadResident(b);
                    string name = args[3].StartsWith("aid_texture_") ? args[3] : "aid_texture_banjox_" + args[3];
                    var (rgba, iw, ih) = NB.Core.Textures.ImageIO.Load(args[4]);
                    int w = iw, h = ih;
                    if (args.Length > 5) { var wh = args[5].Split('x'); w = int.Parse(wh[0]); h = int.Parse(wh[1]); }
                    int existing = caff.Symbols.FindIndex(s => NB.Core.Formats.AssetIds.DisplayName(s) == name) + 1;
                    if (existing > 0)
                    {
                        // re-encode in place (same size)
                        var cpu = caff.PartsOf(existing).First(p => caff.SectionOf(p).Name == ".data");
                        var gpu = caff.PartsOf(existing).First(p => caff.SectionOf(p).Name == ".texturegpu");
                        var t = new NB.Core.Textures.TextureAsset(cpu.Data, gpu.Data);
                        gpu.Data = NB.Core.Textures.TextureAsset.EncodeBlob(t.Header, t.Levels, rgba, iw, ih); gpu.Size = gpu.Data.Length;
                        ws.SaveResident(b, caff, $"texture {name} re-encoded from {Path.GetFileName(args[4])}");
                        Console.WriteLine($"updated {name} ({t.Header})");
                        return 0;
                    }
                    int s2 = NB.Core.Textures.TextureFactory.Create(caff, name, rgba, iw, ih, w, h);
                    ws.SaveResident(b, caff, $"new texture {name} {w}x{h} from {Path.GetFileName(args[4])}");
                    Console.WriteLine($"created {name} {w}x{h} DXT1 ({NB.Core.Textures.TextureFactory.LevelsFor(w, h)} levels) id {NB.Core.Formats.AssetIds.IdOf(name):X8}");
                    return 0;
                }
                case "model-create":
                {
                    // model-create <workspace> <bundle hex> <template model> <new name> <mesh.obj|fbx> [--retarget old=new ...] [--material <stem>]
                    //              [--collision none|box|mesh|<col.obj>] [--cull <dist>] : new unique model from a template (see ModelFactory)
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    uint b = Convert.ToUInt32(args[2], 16);
                    var caff = ws.LoadResident(b);
                    var o = new NB.Core.World.ModelFactory.Options { Template = args[3], Name = args[4], Meshes = NB.Core.Models.ObjReader.ReadAny(args[5]) };
                    for (int i = 6; i < args.Length; i++)
                    {
                        switch (args[i])
                        {
                            case "--retarget": { var kv = args[++i].Split('='); o.Retarget[kv[0]] = kv[1]; break; }
                            case "--material": o.SingleMaterial = args[++i]; break;
                            case "--flat-normal": o.FlatNormal = args[++i]; break;
                            case "--ao": o.NeutralAo = args[++i]; break;
                            case "--spec": o.Specular = args[++i]; break;
                            case "--collision-template": o.CollisionTemplate = args[++i]; break;
                            case "--cull": o.CullDistance = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                            case "--collision":
                            {
                                var c = args[++i];
                                if (c is "none" or "box" or "mesh" or "clone") o.Collision = c;
                                else { o.Collision = "mesh"; o.CollisionMeshes = NB.Core.Models.ObjReader.ReadAny(c); }
                                break;
                            }
                        }
                    }
                    var res = NB.Core.World.ModelFactory.Create(caff, o);
                    ws.SaveResident(b, caff, $"new model {NB.Core.World.ModelFactory.ModelName(o.Name)} from {o.Template} ({Path.GetFileName(args[5])}, collision {o.Collision})");
                    Console.WriteLine($"created {NB.Core.World.ModelFactory.ModelName(o.Name)} id {res.ModelId:X8}, collision {(res.HavokId is uint h ? h.ToString("X8") : "none")}");
                    foreach (var n in res.Notes) Console.WriteLine("  " + n);
                    return 0;
                }
                case "instance-add":
                {
                    // instance-add <workspace> <bundle hex> <model name> x,y,z [yawDeg] [--scale s] [--template <instance index>] [--no-collision]
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    var idx = NB.Core.Project.AssetIndex.LoadOrBuild(ws);
                    uint b = Convert.ToUInt32(args[2], 16);
                    var we = NB.Core.Project.WorldCatalog.FromIndex(idx).First(x => x.Bundle == b);
                    var caff = ws.LoadResident(b);
                    int bg = caff.Symbols.IndexOf(we.BackgroundModel) + 1;
                    string model = args[3].StartsWith("aid_model_") ? args[3] : NB.Core.World.ModelFactory.ModelName(args[3]);
                    string havok = "aid_havok_" + model["aid_model_".Length..];
                    if (!caff.Symbols.Any(s => NB.Core.Formats.AssetIds.DisplayName(s) == model)) { Console.WriteLine($"error: {model} is not in bundle {b:x6} (create it first)"); return 1; }
                    uint modelId = NB.Core.Formats.AssetIds.IdOf(model)!.Value;
                    uint? havokId = !args.Contains("--no-collision") && caff.Symbols.Any(s => NB.Core.Formats.AssetIds.DisplayName(s) == havok) ? NB.Core.Formats.AssetIds.IdOf(havok) : null;
                    var p = args[4].Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    float yaw = args.Length > 5 && !args[5].StartsWith("--") ? float.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture) : 0;
                    float scale = args.Contains("--scale") ? float.Parse(args[Array.IndexOf(args, "--scale") + 1], System.Globalization.CultureInfo.InvariantCulture) : 1;
                    int template = args.Contains("--template") ? int.Parse(args[Array.IndexOf(args, "--template") + 1]) : 0;
                    var world = System.Numerics.Matrix4x4.CreateScale(scale) * System.Numerics.Matrix4x4.CreateRotationY(yaw * MathF.PI / 180) * System.Numerics.Matrix4x4.CreateTranslation(p[0], p[1], p[2]);
                    string instName = model.Replace("aid_model_banjox_", "");
                    int n = NB.Core.World.InstanceEditor.AddModelInstance(caff, bg, template, modelId, havokId, world, instName);
                    ws.SaveResident(b, caff, $"instance #{n} of {model} at {args[4]} yaw {yaw}{(havokId != null ? "" : " (no collision)")}");
                    Console.WriteLine($"instance #{n} of {model}{(havokId != null ? " with collision" : "")}");
                    return 0;
                }
                case "index-survey":
                {
                    // index-survey <caff>: draws whose index values are all multiples of 4 and exceed the vertex count (vertex index x 4)
                    var c = NB.Core.Formats.CaffFile.Read(File.ReadAllBytes(args[1]));
                    var reloc = NB.Core.Formats.AssetView.BuildRelocIndex(c);
                    int plain = 0, x4 = 0, other = 0; var ex = new List<string>();
                    for (int s = 1; s <= c.Symbols.Count; s++)
                    {
                        if (!c.Symbols[s - 1].StartsWith("aid_model_")) continue;
                        NB.Core.Models.ModelAsset m; try { m = NB.Core.Models.ModelAsset.Parse(c, s, reloc); } catch { continue; }
                        foreach (var d in m.Draws.Where(d => d.Indices.Length > 0 && d.Positions.Length > 0))
                        {
                            int mx = d.Indices.Max(), nv = d.Positions.Length;
                            if (mx < nv) plain++;
                            else if (d.Indices.All(i => i % 4 == 0) && mx / 4 < nv) { x4++; if (ex.Count < 12) ex.Add($"{NB.Core.Formats.AssetIds.DisplayName(c.Symbols[s - 1])} vb 0x{d.VbRecord:X} max {mx} nv {nv} layout {string.Join(",", d.Layout.Select(e => e.Format))}"); }
                            else other++;
                        }
                    }
                    Console.WriteLine($"draws: {plain} plain, {x4} index x4, {other} other");
                    foreach (var e in ex) Console.WriteLine("  " + e);
                    return 0;
                }
                case "havok-survey":
                {
                    // havok-survey <caff> [filter]: havok assets: plain mesh collision (type-1 entry), breakable, size
                    var c = NB.Core.Formats.CaffFile.Read(File.ReadAllBytes(args[1]));
                    string f = args.Length > 2 ? args[2] : "";
                    for (int s = 1; s <= c.Symbols.Count; s++)
                    {
                        var nm = NB.Core.Formats.AssetIds.DisplayName(c.Symbols[s - 1]);
                        if (!nm.StartsWith("aid_havok_") || !nm.Contains(f)) continue;
                        var v = new NB.Core.Formats.AssetView(c, s);
                        if (!v.Has(".data")) continue;
                        var d = v.Data(".data");
                        Console.WriteLine($"{nm.Replace("aid_havok_banjox_background_showdowntown_showdowntownreferences_", "")}\tmesh {NB.Core.Havok.HkCollisionImport.TypeOneEntry(d) >= 0}\tbreakable {NB.Core.Havok.HkCollisionImport.IsBreakable(d)}\t{d.Length} bytes");
                    }
                    return 0;
                }
                case "water-dump":
                {
                    // water-dump <caff> <background model> [out.obj]: water regions (chunk 38); optional OBJ of the surfaces
                    var c = NB.Core.Formats.CaffFile.Read(File.ReadAllBytes(args[1]));
                    int sym = c.Symbols.FindIndex(s => NB.Core.Formats.AssetIds.DisplayName(s) == args[2]) + 1;
                    var regs = NB.Core.World.WaterEditor.Read(c, sym);
                    var sb = new System.Text.StringBuilder(); int vb = 1;
                    for (int i = 0; i < regs.Count; i++)
                    {
                        var r = regs[i];
                        var hs = r.Triangles.Select(p => MathF.Round(p.Y, 2)).Distinct().OrderBy(x => x).ToList();
                        Console.WriteLine($"region {i}: kind {r.Kind}, {r.Triangles.Count / 3} triangles, heights {string.Join(", ", hs.Take(8))}, x {r.Triangles.Min(p => p.X):F0}..{r.Triangles.Max(p => p.X):F0} z {r.Triangles.Min(p => p.Z):F0}..{r.Triangles.Max(p => p.Z):F0}");
                        sb.Append($"o water_{i}_kind{r.Kind}\n");
                        foreach (var p in r.Triangles) sb.Append(System.FormattableString.Invariant($"v {p.X} {p.Y} {p.Z}\n"));
                        for (int k = 0; k < r.Triangles.Count; k += 3) sb.Append($"f {vb + k} {vb + k + 1} {vb + k + 2}\n");
                        vb += r.Triangles.Count;
                    }
                    if (args.Length > 3) File.WriteAllText(args[3], sb.ToString());
                    return 0;
                }
                case "draw-budget":
                {
                    // draw-budget <workspace> <bundle hex> [x z radius]: draws / triangles of the scenery instances (visible vs hidden
                    // by the scene builder: y < -200), their LOD cull distances; with x z r only instances within r of (x, z)
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    var idx = NB.Core.Project.AssetIndex.LoadOrBuild(ws);
                    uint b = Convert.ToUInt32(args[2], 16);
                    var w = NB.Core.Project.WorldCatalog.FromIndex(idx).First(x => x.Bundle == b);
                    var scene = new NB.Core.World.WorldScene(ws, b, w.BackgroundModel);
                    float F(int i) => float.Parse(args[i], System.Globalization.CultureInfo.InvariantCulture);
                    bool near = args.Length > 5;
                    var caff = scene.Caff;
                    var lodCache = new Dictionary<string, (float Cull, bool HasCull)>();
                    (float, bool) Cull(string model)
                    {
                        if (lodCache.TryGetValue(model, out var r)) return r;
                        int sym = caff.Symbols.FindIndex(s => NB.Core.Formats.AssetIds.DisplayName(s) == model) + 1;
                        var t = sym > 0 ? NB.Core.Models.ModelEdit.GetLodTable(caff, sym) : new();
                        var culls = t.SelectMany(g => g).Where(l => l.Nodes == 0).Select(l => l.Distance).ToList();
                        return lodCache[model] = (culls.Count > 0 ? culls.Max() : float.PositiveInfinity, culls.Count > 0);
                    }
                    int[] cnt = new int[2], draws = new int[2]; long[] tris = new long[2]; int noCull = 0; var cullHist = new List<float>();
                    foreach (var o in scene.Objects.Where(o => o.Kind == NB.Core.World.SceneObjectKind.Scenery && o.Model != null))
                    {
                        var p = o.Transform.Translation;
                        bool hidden = p.Y < -200;
                        if (near && Math.Sqrt((p.X - F(3)) * (p.X - F(3)) + (p.Z - F(4)) * (p.Z - F(4))) > F(5)) continue;
                        int h = hidden ? 1 : 0;
                        var models = new List<NB.Core.Models.ModelAsset> { o.Model! }; models.AddRange(o.Children.Select(c => c.Model));
                        cnt[h]++;
                        foreach (var m in models)
                        {
                            var ds = m.Draws.Where(d => !m.LodOnlyNodes.Contains(d.Node)).ToList();
                            draws[h] += ds.Count; tris[h] += ds.Sum(d => (long)d.IndexCount / 3);
                        }
                        if (hidden) { var (c, has) = Cull(o.ModelName ?? ""); if (!has) noCull++; else cullHist.Add(c); }
                    }
                    Console.WriteLine($"visible instances {cnt[0]}: {draws[0]} draws, {tris[0]:N0} triangles (LOD0, incl. nested)");
                    Console.WriteLine($"hidden  instances {cnt[1]}: {draws[1]} draws, {tris[1]:N0} triangles (LOD0, incl. nested)");
                    if (cullHist.Count > 0) Console.WriteLine($"hidden: cull distance min {cullHist.Min():F0} median {cullHist.OrderBy(x => x).ElementAt(cullHist.Count / 2):F0} max {cullHist.Max():F0}; {noCull} without a cull level");
                    return 0;
                }
                case "xex-verify":
                {
                    // xex-verify <default.xex> [--bake mod-id ... --out file]: check the hash chain; optionally bake mods first
                    var raw = File.ReadAllBytes(args[1]);
                    Console.WriteLine($"{args[1]}: {(NB.Core.Formats.XexFile.VerifyHashes(raw, out var det) ? "OK" : "BAD")} ({det})");
                    int bi = Array.IndexOf(args, "--bake");
                    if (bi < 0) return 0;
                    var ids = args.Skip(bi + 1).TakeWhile(a => !a.StartsWith("--")).ToList();
                    var mods = NB.Core.Mods.ExePatches.ResolveAll(ids);
                    var baked = NB.Core.Formats.XexFile.Read(raw).WritePatched(mods.SelectMany(m => m.Words).Select(w => (w.Address, w.Patched)));
                    Console.WriteLine($"baked ({string.Join(", ", mods.Select(m => m.Id))}): {(NB.Core.Formats.XexFile.VerifyHashes(baked, out det) ? "OK" : "BAD")} ({det})");
                    var x2 = NB.Core.Formats.XexFile.Read(baked); var img2 = x2.GetImage();
                    foreach (var w in mods.SelectMany(m => m.Words))
                        Console.WriteLine($"  0x{w.Address:X8} = 0x{NB.Core.IO.BE.U32(img2, (int)(w.Address - x2.ImageBase)):X8} (want 0x{w.Patched:X8})");
                    int so = (int)x2.SecurityOffset, pe = (int)x2.PeDataOffset;
                    var diff = Enumerable.Range(0, raw.Length).Where(i => raw[i] != baked[i]).ToList();
                    Console.WriteLine($"  differing bytes: {diff.Count}; in header outside security info: {diff.Count(i => i < so || (i >= so + 0x184 + 0x18 * NB.Core.IO.BE.S32(raw, so + 0x180) && i < pe))}; in security info: {diff.Count(i => i >= so && i < so + 0x184 + 0x18 * NB.Core.IO.BE.S32(raw, so + 0x180))}; in payload: {diff.Count(i => i >= pe)}; encryption {x2.EncryptionType}, compression {x2.CompressionType}");
                    var same = NB.Core.Formats.XexFile.Read(raw).WritePatched(Array.Empty<(uint, uint)>());
                    Console.WriteLine($"  round trip with no changes identical to the input: {same.AsSpan().SequenceEqual(raw)}");
                    int oi = Array.IndexOf(args, "--out"); if (oi > 0) File.WriteAllBytes(args[oi + 1], baked);
                    return 0;
                }
                case "xex-hashcheck":
                {
                    // xex-hashcheck <default.xex>: test the XEX2 hash-chain hypotheses against the stored digests
                    var raw = File.ReadAllBytes(args[1]);
                    var x = NB.Core.Formats.XexFile.Read(raw);
                    var img = x.GetImage();
                    int so = (int)x.SecurityOffset, cnt = NB.Core.IO.BE.S32(raw, so + 0x180), pe = (int)x.PeDataOffset;
                    byte[] Sha(params byte[][] parts) { using var h = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA1); foreach (var p in parts) h.AppendData(p); return h.GetHashAndReset(); }
                    byte[] Desc(int i) => raw.AsSpan(so + 0x184 + 0x18 * i, 0x18).ToArray();
                    byte[] Dig(int i) => raw.AsSpan(so + 0x184 + 0x18 * i + 4, 20).ToArray();
                    int pageSize = (x.ImageFlags & 0x10000000) != 0 ? 0x1000 : 0x10000;
                    int[] start = new int[cnt + 1]; for (int i = 0; i < cnt; i++) start[i + 1] = start[i] + (int)(NB.Core.IO.BE.U32(raw, so + 0x184 + 0x18 * i) >> 4) * pageSize;
                    byte[] Pages(int i) => img.AsSpan(start[i], Math.Min(start[i + 1], img.Length) - start[i]).ToArray();
                    bool Eq(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);
                    Console.WriteLine($"descriptors {cnt}, page size 0x{pageSize:X}, image 0x{img.Length:X}");
                    int last = cnt - 1;
                    Console.WriteLine($"last: SHA1(pages) == digest? {Eq(Sha(Pages(last)), Dig(last))}");
                    Console.WriteLine($"desc0: SHA1(pages0 + desc1) == digest0? {Eq(Sha(Pages(0), Desc(1)), Dig(0))};  SHA1(pages0) == digest0? {Eq(Sha(Pages(0)), Dig(0))}");
                    var sec = raw.AsSpan(so + 0x114, 20).ToArray();
                    Console.WriteLine($"0x114 == SHA1(desc0)? {Eq(Sha(Desc(0)), sec)}");
                    // chain check
                    int ok = 0; for (int i = 0; i < cnt; i++) if (Eq(i == last ? Sha(Pages(i)) : Sha(Pages(i), Desc(i + 1)), Dig(i))) ok++;
                    Console.WriteLine($"chain descriptors matching: {ok}/{cnt}");
                    var which = Enumerable.Range(0, cnt).Where(i => Eq(i == last ? Sha(Pages(i)) : Sha(Pages(i), Desc(i + 1)), Dig(i))).ToList();
                    Console.WriteLine("  matching: " + string.Join(",", which) + "  (zero pages among them: " + string.Join(",", which.Where(i => Pages(i).All(b => b == 0))) + ")");
                    // per page: find which descriptor digest equals SHA1(page i + desc j) or SHA1(page i) for small shifts
                    var digs = Enumerable.Range(0, cnt).ToDictionary(i => Convert.ToHexString(Dig(i)), i => i);
                    for (int i = 0; i < 6; i++)
                    {
                        var found = new List<string>();
                        for (int j = 0; j <= cnt; j++)
                        {
                            var h1 = Convert.ToHexString(j < cnt ? Sha(Pages(i), Desc(j)) : Sha(Pages(i)));
                            if (digs.TryGetValue(h1, out int di)) found.Add($"SHA1(page{i}+desc{(j < cnt ? j.ToString() : "-")})=dig{di}");
                        }
                        Console.WriteLine($"  page {i}: {(found.Count == 0 ? "no match" : string.Join(" ", found))}");
                    }
                    int ok2 = 0; for (int i = 0; i < last; i++) if (Eq(Sha(Pages(i + 1), Desc(i + 1)), Dig(i))) ok2++;
                    Console.WriteLine($"forward chain dig[i] = SHA1(pages[i+1] + desc[i+1]): {ok2}/{last}; last digest = {Convert.ToHexString(Dig(last))}");
                    Console.WriteLine($"0x114 == SHA1(pages0 + desc0)? {Eq(Sha(Pages(0), Desc(0)), sec)}");
                    var hd = raw.AsSpan(so + 0x164, 20).ToArray();
                    int secEnd = so + 0x184 + 0x18 * cnt;
                    var cands = new (string, byte[])[]
                    {
                        ("[secEnd..pe] + [0..so+8]", Sha(raw[secEnd..pe], raw[0..(so + 8)])),
                        ("[secEnd..pe] + [0..so]", Sha(raw[secEnd..pe], raw[0..so])),
                        ("[0..so+8] + [secEnd..pe]", Sha(raw[0..(so + 8)], raw[secEnd..pe])),
                        ("[so+0x17C..pe]", Sha(raw[(so + 0x17C)..pe])),
                    };
                    foreach (var (n2, h2) in cands) Console.WriteLine($"header digest == SHA1{n2}? {Eq(h2, hd)}");
                    return 0;
                }
                case "cull-map":
                {
                    // cull-map <caff> <background model>: culling-tree nodes (chunk 0) vs draws: does node.group match draw.Node, and
                    // do the draw's vertices lie inside that node's box?
                    var c = NB.Core.Formats.CaffFile.Read(File.ReadAllBytes(args[1]));
                    int sym = c.Symbols.FindIndex(s => NB.Core.Formats.AssetIds.DisplayName(s) == args[2]) + 1;
                    var m = NB.Core.Models.ModelAsset.Parse(c, sym);
                    var d = new NB.Core.Formats.AssetView(c, sym).Data(".data");
                    int t = NB.Core.IO.BE.S32(d, 0), cn = NB.Core.IO.BE.S32(d, 4), c0 = -1;
                    for (int i = 0; i < cn; i++) if (NB.Core.IO.BE.S32(d, t + 8 * i) == 0) c0 = NB.Core.IO.BE.S32(d, t + 8 * i + 4);
                    int hdr = NB.Core.IO.BE.S32(d, c0 + 0x1C), n = NB.Core.IO.BE.S32(d, hdr), nodes = NB.Core.IO.BE.S32(d, hdr + 0x18);
                    var byNode = m.Draws.Select((dr, i) => (dr, i)).GroupBy(x => x.dr.Node).ToDictionary(g => g.Key, g => g.ToList());
                    Console.WriteLine($"{n} culling nodes, {m.Draws.Count} draws, distinct draw.Node values {byNode.Count} (min {byNode.Keys.Min()} max {byNode.Keys.Max()})");
                    int shown = 0, inside = 0, checkedN = 0;
                    for (int i = 0; i < n; i++)
                    {
                        int o = nodes + 32 * i; uint a = NB.Core.IO.BE.U32(d, o), g = NB.Core.IO.BE.U32(d, o + 4);
                        if (g == 0xFFFFFFFF) continue;
                        var mn = new System.Numerics.Vector3(NB.Core.IO.BE.F32(d, o + 8), NB.Core.IO.BE.F32(d, o + 12), NB.Core.IO.BE.F32(d, o + 16));
                        var mx = new System.Numerics.Vector3(NB.Core.IO.BE.F32(d, o + 20), NB.Core.IO.BE.F32(d, o + 24), NB.Core.IO.BE.F32(d, o + 28));
                        List<(NB.Core.Models.MeshDraw dr, int i)>? ds = null;
                        if (args.Contains("--by-index")) { if (g < m.Draws.Count) ds = new() { (m.Draws[(int)g], (int)g) }; }
                        else if (args.Contains("--blocks"))
                        {
                            var bl = System.Text.Json.JsonDocument.Parse(File.ReadAllText(args[Array.IndexOf(args, "--blocks") + 1])).RootElement;
                            if (g < bl.GetArrayLength()) ds = bl[(int)g][1].EnumerateArray().Select(x => x.GetInt32()).Where(x => x < m.Draws.Count).Select(x => (m.Draws[x], x)).ToList();
                        }
                        else byNode.TryGetValue((int)g, out ds);
                        string info = "";
                        if (ds != null)
                        {
                            var ps = ds.SelectMany(x => x.dr.Indices.Where(k => k < x.dr.Positions.Length).Select(k => x.dr.Positions[k])).ToList();
                            if (ps.Count > 0)
                            {
                                var pmn = ps.Aggregate(System.Numerics.Vector3.Min); var pmx = ps.Aggregate(System.Numerics.Vector3.Max);
                                bool ins = pmn.X >= mn.X - 1 && pmn.Z >= mn.Z - 1 && pmx.X <= mx.X + 1 && pmx.Z <= mx.Z + 1; checkedN++; if (ins) inside++;
                                info = $"draws {string.Join(",", ds.Select(x => x.i))} tris {ds.Sum(x => x.dr.IndexCount / 3)} bounds ({pmn.X:F0},{pmn.Z:F0})..({pmx.X:F0},{pmx.Z:F0}) inside={ins}";
                            }
                        }
                        if (shown++ < 12) Console.WriteLine($"node {i} a={a} group={g} box ({mn.X:F0},{mn.Z:F0})..({mx.X:F0},{mx.Z:F0})  {info}");
                    }
                    Console.WriteLine($"non-empty nodes whose group matches draw.Node with geometry: {checkedN}, geometry inside the node box: {inside}");
                    return 0;
                }
                case "view-budget":
                {
                    // view-budget <workspace> <bundle hex> x y z: what the scenery pass draws from this camera position with the engine's
                    // LOD rules (level = highest switch distance <= distance / instance scale; a level without nodes draws nothing).
                    // Frustum culling is ignored (upper bound). Terrain draws are counted in full.
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    var idx = NB.Core.Project.AssetIndex.LoadOrBuild(ws);
                    uint b = Convert.ToUInt32(args[2], 16);
                    var w = NB.Core.Project.WorldCatalog.FromIndex(idx).First(x => x.Bundle == b);
                    var scene = new NB.Core.World.WorldScene(ws, b, w.BackgroundModel);
                    float F(int i) => float.Parse(args[i], System.Globalization.CultureInfo.InvariantCulture);
                    var cam = new System.Numerics.Vector3(F(3), F(4), F(5));
                    (int Draws, long Tris) Drawn(NB.Core.Models.ModelAsset m, System.Numerics.Matrix4x4 world)
                    {
                        float scale = new System.Numerics.Vector3(world.M31, world.M32, world.M33).Length();
                        float metric = System.Numerics.Vector3.Distance(cam, world.Translation) / Math.Max(scale, 1e-6f);
                        var grouped = new HashSet<int>(m.LodLevels.SelectMany(g => g.SelectMany(l => l.Nodes)));
                        var active = new HashSet<int>();
                        foreach (var g in m.LodLevels)
                        {
                            int pick = 0;
                            for (int l = 0; l < g.Count; l++) if (g[l].Distance <= metric) pick = l;
                            foreach (var n in g[pick].Nodes) active.Add(n);
                        }
                        int dr = 0; long tr = 0;
                        foreach (var d in m.Draws)
                        {
                            bool on = m.LodLevels.Count == 0 ? !m.LodOnlyNodes.Contains(d.Node) : (!grouped.Contains(d.Node) || active.Contains(d.Node));
                            if (on) { dr++; tr += d.IndexCount / 3; }
                        }
                        return (dr, tr);
                    }
                    int[] n = new int[3]; int[] dcount = new int[3]; long[] tcount = new long[3];
                    var top = new Dictionary<string, (int, int, long)>();
                    foreach (var o in scene.Objects.Where(o => o.Model != null))
                    {
                        int k = o.Kind == NB.Core.World.SceneObjectKind.Terrain ? 2 : o.Transform.Translation.Y < -200 ? 1 : 0;
                        if (k == 2) { n[2]++; dcount[2] += o.Model!.Draws.Count; tcount[2] += o.Model.Draws.Sum(d => (long)d.IndexCount / 3); continue; }
                        var (dr, tr) = Drawn(o.Model!, o.Transform);
                        foreach (var (cm, local) in o.Children) { var (d2, t2) = Drawn(cm, local * o.Transform); dr += d2; tr += t2; }
                        if (dr > 0) n[k]++;
                        dcount[k] += dr; tcount[k] += tr;
                        if (k == 0 && dr > 0) { var key = o.ModelName; top[key] = (top.GetValueOrDefault(key).Item1 + 1, top.GetValueOrDefault(key).Item2 + dr, top.GetValueOrDefault(key).Item3 + tr); }
                    }
                    if (args.Contains("--top"))
                    {
                        Console.WriteLine($"objects: {scene.Objects.Count}, with a model: {scene.Objects.Count(o => o.Model != null)}");
                        foreach (var kv in top.OrderByDescending(x => x.Value.Item3).Take(25)) Console.WriteLine($"    {kv.Value.Item1,4} x {kv.Key}: {kv.Value.Item2} draws {kv.Value.Item3:N0} tris");
                    }
                    Console.WriteLine($"from {cam}:");
                    Console.WriteLine($"  scenery drawn        {n[0],4} instances {dcount[0],6} draws {tcount[0],10:N0} triangles");
                    Console.WriteLine($"  hidden but drawn     {n[1],4} instances {dcount[1],6} draws {tcount[1],10:N0} triangles");
                    Console.WriteLine($"  terrain (all draws)  {dcount[2],21} draws {tcount[2],10:N0} triangles");
                    Console.WriteLine($"  TOTAL                {dcount.Sum(),21} draws {tcount.Sum(),10:N0} triangles");
                    return 0;
                }
                case "mp-town":
                {
                    // mp-town <workspace> [x,y,z]: Xbox LIVE "Freewheel Festival" becomes free roam in Showdown Town
                    // (start grid centred on x,y,z; default: town drone-action marker #84)
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    System.Numerics.Vector3? at = null;
                    if (args.Length > 2 && !args[2].StartsWith("--"))
                    {
                        var p = args[2].Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                        at = new System.Numerics.Vector3(p[0], p[1], p[2]);
                    }
                    if (args.Contains("--add-town-only")) { Console.WriteLine(NB.Core.Net.MultiplayerTown.AddTownDependencyOnly(ws)); return 0; }
                    if (NB.Core.Net.MultiplayerTown.IsApplied(ws)) { Console.WriteLine("already applied (ws-revert the bundles 685374 and fd97da to redo)"); return 1; }
                    foreach (var line in NB.Core.Net.MultiplayerTown.Apply(ws, at)) Console.WriteLine(line);
                    return 0;
                }
                case "room-code":
                {
                    // room-code <ip> [port 36000] [game dir]: room code for players to join (the game dir adds its compatibility tag)
                    var ip = System.Net.IPAddress.Parse(args[1]);
                    int port = args.Length > 2 ? int.Parse(args[2]) : 36000;
                    ushort tag = args.Length > 3 ? NB.Core.Net.CompatProfile.FromGame(args[3]).Tag : (ushort)0;
                    Console.WriteLine(NB.Core.Net.RoomCode.Encode(ip, port, tag));
                    return 0;
                }
                case "room-decode":
                {
                    // room-decode <code | ip[:port]>: prints "ip port tag" (exit 1 when invalid)
                    var t = args[1].Trim();
                    if (NB.Core.Net.RoomCode.TryDecode(t, out var ip, out int port, out ushort tag)) { Console.WriteLine($"{ip} {port} {tag:X4}"); return 0; }
                    var hp = t.Split(':');
                    if (System.Net.IPAddress.TryParse(hp[0], out var ip2) || System.Uri.CheckHostName(hp[0]) == System.UriHostNameType.Dns)
                    {
                        Console.WriteLine($"{hp[0]} {(hp.Length > 1 ? int.Parse(hp[1]) : 36000)} 0000");
                        return 0;
                    }
                    Console.WriteLine("not a room code or address");
                    return 1;
                }
                case "compat-check":
                {
                    // compat-check <game dir> <host[:port]>: compares this game with the host's (GET /nb/room). Exit 0 same,
                    // 2 different (differences listed), 1 host not reachable / no profile published.
                    var hp = args[2].Split(':');
                    string url = $"http://{hp[0]}:{(hp.Length > 1 ? hp[1] : "36000")}/nb/room";
                    NB.Core.Net.CompatProfile? host;
                    string name;
                    try
                    {
                        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                        var json = System.Text.Json.Nodes.JsonNode.Parse(http.GetStringAsync(url).GetAwaiter().GetResult())!;
                        name = json["name"]?.GetValue<string>() ?? "";
                        host = new NB.Core.Net.CompatProfile { Fingerprint = json["fingerprint"]?.GetValue<string>() ?? "" };
                        if (json["files"] is System.Text.Json.Nodes.JsonObject fo) foreach (var (k, v) in fo) host.Files[k] = v!.GetValue<string>();
                    }
                    catch (Exception e) { Console.WriteLine($"host {url} not reachable: {e.Message}"); return 1; }
                    Console.WriteLine($"room \"{name}\" reachable");
                    if (host.Fingerprint.Length == 0) { Console.WriteLine("the host did not publish its game fingerprint (cannot compare)"); return 1; }
                    Console.WriteLine("checking this PC's game files (first time: a few minutes) ...");
                    var mine = NB.Core.Net.CompatProfile.FromGame(args[1]);
                    if (mine.Fingerprint == host.Fingerprint) { Console.WriteLine("game files match the host's"); return 0; }
                    var diff = mine.CompareTo(host);
                    Console.WriteLine($"game files differ from the host's ({diff.Count}):");
                    foreach (var g in diff.GroupBy(d => d.Area)) Console.WriteLine($"  {g.Key}: {string.Join(", ", g.Take(6).Select(d => $"{d.File} ({d.Kind})"))}{(g.Count() > 6 ? $" and {g.Count() - 6} more" : "")}");
                    return 2;
                }
                case "room-server":
                {
                    // room-server [--port 36000] [--bind 0.0.0.0] [--advertise <ip>] [--relaxed-search]: self-hosted Xenia netplay API (every Xenia sets api_address = "http://<host>:<port>/"), runs until Ctrl+C
                    string Opt(string k, string def) { int i = Array.LastIndexOf(args, k); return i > 0 && i + 1 < args.Length ? args[i + 1] : def; }
                    // [--game <dir>] publishes the host's compatibility profile (GET /nb/room) for joining players; [--name <room name>]
                    using var srv = new NB.Core.Net.RoomServer
                    {
                        Log = Console.WriteLine, AdvertiseAddress = Opt("--advertise", "") is { Length: > 0 } adv ? adv : null, RelaxedSearch = args.Contains("--relaxed-search"),
                    };
                    if (Opt("--name", "") is { Length: > 0 } rn) srv.RoomName = rn;
                    if (Opt("--game", "") is { Length: > 0 } gd)
                    {
                        Console.WriteLine("checking the game files (first time: a few minutes) ...");
                        srv.HostCompat = NB.Core.Net.CompatProfile.FromGame(gd);
                        Console.WriteLine($"game fingerprint {srv.HostCompat.Fingerprint[..16]} ({srv.HostCompat.Files.Count} files)");
                    }
                    using var quit = new ManualResetEventSlim();
                    Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Set(); };
                    srv.Start(int.Parse(Opt("--port", "36000")), Opt("--bind", "0.0.0.0"));
                    quit.Wait();
                    srv.Stop();
                    return 0;
                }
                case "live":
                {
                    // live <workspace> where | tp x y z | gravity [g] | cam x y z [pitch yaw] : runtime operations on the running Xenia
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    var img = NB.Core.Formats.XexFile.Read(File.ReadAllBytes(ws.Game.Xex)).GetImage();
                    var probe = img.AsSpan((int)(NB.Core.Live.XeniaLive.TextStart - 0x82000000), 64).ToArray();
                    using var x = NB.Core.Live.XeniaLive.Attach(probe);
                    float F(int i) => float.Parse(args[i], System.Globalization.CultureInfo.InvariantCulture);
                    switch (args[2])
                    {
                        case "where": Console.WriteLine($"running={x.Running()} player {x.PlayerPosition} camera {x.CameraPosition}"); break;
                        case "tp": Console.WriteLine($"moved {x.TeleportVehicle(new(F(3), F(4), F(5)))} body(ies); now {x.PlayerPosition}"); break;
                        case "gravity":
                        {
                            uint w = x.FindHavokWorld(); if (w == 0) throw new InvalidOperationException("hkpWorld not found");
                            if (args.Length > 3) x.SetGravity(w, F(3));
                            Console.WriteLine($"hkpWorld 0x{w:X8} gravity {x.GetGravity(w)}"); break;
                        }
                        case "cam":
                            if (!x.PhotoModeOpen) throw new InvalidOperationException("open photo mode first (pause > Take Photo)");
                            x.SetPhotoCamera(new(F(3), F(4), F(5)), args.Length > 7 ? F(6) : null, args.Length > 7 ? F(7) : null);
                            Console.WriteLine("camera moved"); break;
                        default: throw new ArgumentException("live <ws> where | tp x y z | gravity [g] | cam x y z [pitch yaw]");
                    }
                    return 0;
                }
                case "objparams-set":
                {
                    // objparams-set <workspace> <objparams asset> <offset hex> <u32 hex | asset name> [--bundle hex]: one u32 in every resident and streamed copy
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    NB.Core.Project.WorldOps.Run(ws, args.Take(1).Concat(args.Skip(2)).ToList(), Console.WriteLine, () => NB.Core.Project.AssetIndex.LoadOrBuild(ws));
                    return 0;
                }
                case "script-insert":
                {
                    // script-insert <workspace> <bundle hex> <script asset> <after: offset hex | op:XX> <command hex>: insert one script command (NB.Core.Project.WorldOps)
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    NB.Core.Project.WorldOps.Run(ws, args.Take(1).Concat(args.Skip(2)).ToList(), Console.WriteLine, () => NB.Core.Project.AssetIndex.LoadOrBuild(ws));
                    return 0;
                }
                case "asset-copy":
                {
                    // asset-copy <workspace> <src bundle hex> <src asset> <dst bundle hex> [new name]: copy a self-contained asset (e.g. a vehicle blueprint) into another resident bundle
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    NB.Core.Project.WorldOps.Run(ws, args.Take(1).Concat(args.Skip(2)).ToList(), Console.WriteLine, () => NB.Core.Project.AssetIndex.LoadOrBuild(ws));
                    NB.Core.Project.AssetIndex.LoadOrBuild(ws, null, true);
                    return 0;
                }
                case "objparams-copy":
                {
                    // objparams-copy <workspace> <src bundle hex> <src asset> <dst bundle hex> <dst template asset> [new name]: copy an objparams record into another bundle
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    NB.Core.Project.WorldOps.Run(ws, args.Take(1).Concat(args.Skip(2)).ToList(), Console.WriteLine, () => NB.Core.Project.AssetIndex.LoadOrBuild(ws));
                    NB.Core.Project.AssetIndex.LoadOrBuild(ws, null, true);
                    return 0;
                }
                case "ai-route":
                {
                    // ai-route <workspace> <world bundle hex> <marker asset> [options]: add a looping AI vehicle (options: NB.Core.Project.WorldOps)
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    NB.Core.Project.WorldOps.Run(ws, args.Take(1).Concat(args.Skip(2)).ToList(), Console.WriteLine, () => NB.Core.Project.AssetIndex.LoadOrBuild(ws));
                    NB.Core.Project.AssetIndex.LoadOrBuild(ws, null, true);
                    return 0;
                }
                case "model-nodes":
                {
                    // model-nodes <caff> <model>: chunk-2 nodes (parent, flags, local transform)
                    var c = NB.Core.Formats.CaffFile.Read(File.ReadAllBytes(args[1]));
                    int sym = c.Symbols.FindIndex(s => NB.Core.Formats.AssetIds.DisplayName(s) == args[2]) + 1;
                    var m = NB.Core.Models.ModelAsset.Parse(c, sym, geometry: false);
                    for (int i = 0; i < m.Nodes.Count; i++)
                    {
                        var n = m.Nodes[i]; var l = n.Local;
                        Console.WriteLine($"node {i,2} parent {n.Parent,5} flags {n.Flags:X4}  rows ({l.M11:0.###} {l.M12:0.###} {l.M13:0.###}) ({l.M21:0.###} {l.M22:0.###} {l.M23:0.###}) ({l.M31:0.###} {l.M32:0.###} {l.M33:0.###}) t ({l.M41:0.###} {l.M42:0.###} {l.M43:0.###})");
                    }
                    return 0;
                }
                case "attach-dump":
                {
                    // attach-dump <workspace> <part suffix | avatarhavokdata asset>…: footprint and attach points (NB.Core.Parts.AttachData)
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    var idx = NB.Core.Project.AssetIndex.LoadOrBuild(ws);
                    foreach (var n0 in args.Skip(2))
                    {
                        string n = n0.StartsWith("aid_") ? n0 : NB.Core.Parts.PartFactory.AttachPrefix + n0;
                        var e = idx.Entries.FirstOrDefault(x => x.Name == n && !x.Streamed) ?? throw new InvalidDataException("no resident " + n);
                        var caff = ws.LoadResident(e.Bundle);
                        var a = NB.Core.Parts.AttachData.Parse(caff.PartsOf(e.Symbol).First(p => caff.SectionOf(p).Name == ".data").Data);
                        Console.WriteLine($"{n} ({e.Bundle:x6}): {a}");
                        foreach (var p in a.Points) Console.WriteLine($"  {p}  normal {p.Normal}");
                    }
                    return 0;
                }
                case "stream-entry":
                {
                    // stream-entry <workspace> <stream bundle hex> <entry id hex>: symbols and parts of a streamed CAFF entry
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    var arch = ws.LoadStream(Convert.ToUInt32(args[2], 16));
                    if (args[3] == "--roundtrip")
                    {
                        int ok = 0, bad = 0;
                        foreach (var e in arch.Entries.Where(e => e.Kind == "caff"))
                        {
                            try { if (NB.Core.Formats.CaffFile.Read(e.Data!).Write().AsSpan().SequenceEqual(e.Data)) ok++; else { bad++; Console.WriteLine($"{e.Id:X8}: differs"); } }
                            catch (Exception ex) { bad++; Console.WriteLine($"{e.Id:X8}: {ex.Message}"); }
                        }
                        Console.WriteLine($"{ok} identical, {bad} failed");
                        return bad == 0 ? 0 : 1;
                    }
                    uint id = Convert.ToUInt32(args[3], 16);
                    var hits = arch.Entries.Select((e, i) => (e, i)).Where(x => x.e.Id == id).ToList();
                    Console.WriteLine($"{arch.Entries.Count} entries, {hits.Count} with id {id:X8}");
                    foreach (var (e, i) in hits)
                    {
                        Console.WriteLine($"#{i} {e.Kind} {e.Data?.Length ?? 0} bytes");
                        if (e.Kind != "caff") continue;
                        var sc = NB.Core.Formats.CaffFile.Read(e.Data!);
                        for (int s = 1; s <= sc.Symbols.Count; s++)
                            Console.WriteLine($"  [{s}] {sc.Symbols[s - 1]}  {string.Join(" ", sc.PartsOf(s).Select(p => $"{sc.SectionOf(p).Name}:{p.Data.Length}"))}");
                        Console.WriteLine($"  relocs {sc.Relocs.Count}");
                    }
                    return 0;
                }
                case "part-validate":
                {
                    // part-validate <workspace> <parts.json> [part id…]: check specs before building (NB.Core.Parts.PartValidator)
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    var idx = NB.Core.Project.AssetIndex.LoadOrBuild(ws);
                    var specs = NB.Core.Parts.PartFactory.LoadSpecs(args[2]);
                    var only = args.Skip(3).Where(a => !a.StartsWith("--")).ToHashSet();
                    string baseDir = Path.GetDirectoryName(Path.GetFullPath(args[2]))!;
                    int bad = 0;
                    foreach (var p in specs.Where(s => only.Count == 0 || only.Contains(s.Id)))
                    {
                        var r = NB.Core.Parts.PartValidator.Validate(ws, idx, p, baseDir);
                        Console.WriteLine($"== {p.Id}: {(r.Ok ? "OK" : $"{r.Errors.Count} error(s)")}, {r.Warnings.Count} warning(s)");
                        foreach (var l in r.Lines) Console.WriteLine("  " + l);
                        if (!r.Ok) bad++;
                    }
                    return bad == 0 ? 0 : 1;
                }
                case "part-remove":
                {
                    // part-remove <workspace> <parts.json> <part id…> [--keep-tier]: uninstall parts built by part-build
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    var idx = NB.Core.Project.AssetIndex.LoadOrBuild(ws);
                    var specs = NB.Core.Parts.PartFactory.LoadSpecs(args[2]);
                    var only = args.Skip(3).Where(a => !a.StartsWith("--")).ToHashSet();
                    foreach (var p in specs.Where(s => only.Contains(s.Id)))
                    {
                        Console.WriteLine($"== remove {p.Id}");
                        foreach (var l in NB.Core.Parts.PartFactory.Uninstall(ws, idx, p, !args.Contains("--keep-tier"))) Console.WriteLine("  " + l);
                    }
                    NB.Core.Project.AssetIndex.LoadOrBuild(ws, null, true);
                    return 0;
                }
                case "part-build":
                {
                    // part-build <workspace> <parts.json> [part id…]: build new vehicle parts from specs (NB.Core.Parts.PartFactory)
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    var idx = NB.Core.Project.AssetIndex.LoadOrBuild(ws);
                    var specs = NB.Core.Parts.PartFactory.LoadSpecs(args[2]);
                    var only = args.Skip(3).Where(a => !a.StartsWith("--")).ToHashSet();
                    string baseDir = Path.GetDirectoryName(Path.GetFullPath(args[2]))!;
                    foreach (var p in specs.Where(s => only.Count == 0 || only.Contains(s.Id)))
                    {
                        Console.WriteLine($"== {p.Id} ({p.Name})");
                        foreach (var l in NB.Core.Parts.PartFactory.Build(ws, idx, p, baseDir)) Console.WriteLine("  " + l);
                    }
                    Console.WriteLine("reindexing…");
                    NB.Core.Project.AssetIndex.LoadOrBuild(ws, null, true);
                    return 0;
                }
                case "grouping-add":
                {
                    // grouping-add <workspace> <garagegrouping asset> <new size key> <new label key> [copy-from size key]
                    //   aid_misc_banjox_garagegrouping_* = the tiers the garage lists for one part group: 204-byte records
                    //   (+0 size key, +0x40 label key "group_…" → loctext block__group_…, +0x80 id, +0x84 flag hash, +0x88 "seen"
                    //   flag name, +0xC8 float). Appends a copy of the copy-from record (default: the last) with the new keys, in
                    //   every resident and streamed copy. No relocations; the count follows from the size.
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    var idx = NB.Core.Project.AssetIndex.LoadOrBuild(ws);
                    string an = args[2].StartsWith("aid_") ? args[2] : "aid_misc_banjox_garagegrouping_" + args[2];
                    const int RS = 204;
                    byte[] Add(byte[] d, out string note)
                    {
                        int n = d.Length / RS;
                        if (d.Length % RS != 0) throw new InvalidDataException($"{an}: size {d.Length} is not a multiple of {RS}");
                        for (int i = 0; i < n; i++) if (NB.Core.IO.BE.CStr(d, i * RS, 64) == args[3]) { note = "already present"; return d; }
                        int src = n - 1;
                        if (args.Length > 5) src = Enumerable.Range(0, n).First(i => NB.Core.IO.BE.CStr(d, i * RS, 64) == args[5]);
                        var nd = new byte[d.Length + RS]; Buffer.BlockCopy(d, 0, nd, 0, d.Length); Buffer.BlockCopy(d, src * RS, nd, d.Length, RS);
                        void S(int o, string v) { Array.Clear(nd, o, 64); var b = System.Text.Encoding.ASCII.GetBytes(v); if (b.Length > 63) throw new ArgumentException(v + " too long"); b.CopyTo(nd, o); }
                        S(d.Length, args[3]); S(d.Length + 0x40, args[4]);
                        note = $"+1 record (copy of '{NB.Core.IO.BE.CStr(d, src * RS, 64)}') -> {n + 1}"; return nd;
                    }
                    int changed = 0;
                    foreach (var e in idx.Entries.Where(x => x.Name == an && !x.Streamed).GroupBy(x => x.Bundle).Select(g => g.First()))
                    {
                        var caff = ws.LoadResident(e.Bundle);
                        var part = caff.PartsOf(e.Symbol).First(p => caff.SectionOf(p).Name == ".data");
                        part.Data = Add(part.Data, out var note); part.Size = part.Data.Length;
                        ws.SaveResident(e.Bundle, caff, $"{an}: tier {args[3]} ({args[4]})"); Console.WriteLine($"{e.Bundle:x6} resident: {note}"); changed++;
                    }
                    foreach (var b in idx.Entries.Where(x => x.Name == an && x.Streamed).Select(x => x.Bundle).Distinct())
                    {
                        var arch = ws.LoadStream(b); uint id = idx.Entries.First(x => x.Name == an).Id; bool any = false;
                        foreach (var en in arch.Entries.Where(x => x.Id == id && x.Kind == "caff"))
                        {
                            var sc = NB.Core.Formats.CaffFile.Read(en.Data!);
                            int sym = sc.Symbols.FindIndex(s => NB.Core.Formats.AssetIds.DisplayName(s) == an) + 1; if (sym == 0) continue;
                            var part = sc.PartsOf(sym).First(p => sc.SectionOf(p).Name == ".data");
                            part.Data = Add(part.Data, out var note); part.Size = part.Data.Length; en.Data = sc.Write(); any = true;
                            Console.WriteLine($"{b:x6} streamed: {note}");
                        }
                        if (any) { ws.SaveStream(b, arch, $"{an}: tier {args[3]}"); changed++; }
                    }
                    return changed > 0 ? 0 : 1;
                }
                case "blockset-count":
                {
                    // blockset-count <workspace> <count> [name prefix]: sets the count of every (count, part) pair in every blockset
                    // (crates, Humba/log crates, keys, Jinjo bingo, start packs, blockset_all) in every resident and streamed copy.
                    // The inventory builder (0x8251CDD8) adds these as 32-bit counts each time the game loads, so owned crates
                    // grant the new amount to existing saves too.
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    var idx = NB.Core.Project.AssetIndex.LoadOrBuild(ws);
                    uint cnt = uint.Parse(args[2]);
                    string prefix = args.Length > 3 ? args[3] : "aid_misc_banjox_blockset_";
                    var names = idx.Entries.Where(e => e.Name.StartsWith(prefix)).Select(e => e.Name).ToHashSet();
                    int Set(byte[] d) { int n = 0; for (int o = 0; o + 8 <= d.Length; o += 8) if (NB.Core.IO.BE.U32(d, o) != cnt) { NB.Core.IO.BE.W32(d, o, cnt); n++; } return n; }
                    int totalSets = 0, totalPairs = 0;
                    foreach (var b in idx.Entries.Where(e => names.Contains(e.Name) && !e.Streamed).Select(e => e.Bundle).Distinct())
                    {
                        var caff = ws.LoadResident(b); int n = 0, sets = 0;
                        for (int s = 1; s <= caff.Symbols.Count; s++)
                        {
                            if (!names.Contains(NB.Core.Formats.AssetIds.DisplayName(caff.Symbols[s - 1]))) continue;
                            var part = caff.PartsOf(s).FirstOrDefault(p => caff.SectionOf(p).Name == ".data");
                            if (part == null || caff.Relocs.Any(r => r.FromPart == caff.Parts.IndexOf(part) + 1) || part.Data.Length % 8 != 0) continue;
                            n += Set(part.Data); sets++;
                        }
                        if (n > 0) ws.SaveResident(b, caff, $"blockset counts -> {cnt} ({sets} sets)");
                        Console.WriteLine($"{b:x6} resident: {sets} sets, {n} counts changed"); totalSets += sets; totalPairs += n;
                    }
                    foreach (var b in idx.Entries.Where(e => names.Contains(e.Name) && e.Streamed).Select(e => e.Bundle).Distinct())
                    {
                        var arch = ws.LoadStream(b); int n = 0, sets = 0;
                        var setIds = idx.Entries.Where(e => names.Contains(e.Name) && e.Streamed && e.Bundle == b).Select(e => e.Id).ToHashSet();
                        foreach (var en in arch.Entries.Where(x => x.Kind == "caff" && x.Data != null && setIds.Contains(x.Id)))
                        {
                            var sc = NB.Core.Formats.CaffFile.Read(en.Data!); int before = n;
                            for (int s = 1; s <= sc.Symbols.Count; s++)
                            {
                                if (!names.Contains(NB.Core.Formats.AssetIds.DisplayName(sc.Symbols[s - 1]))) continue;
                                var part = sc.PartsOf(s).FirstOrDefault(p => sc.SectionOf(p).Name == ".data");
                                if (part == null || sc.Relocs.Any(r => r.FromPart == sc.Parts.IndexOf(part) + 1) || part.Data.Length % 8 != 0) continue;
                                n += Set(part.Data); sets++;
                            }
                            if (n != before) en.Data = sc.Write();
                        }
                        if (n > 0) ws.SaveStream(b, arch, $"blockset counts -> {cnt} ({sets} sets)");
                        Console.WriteLine($"{b:x6} streamed: {sets} sets, {n} counts changed"); totalSets += sets; totalPairs += n;
                    }
                    Console.WriteLine($"{totalSets} blockset copies, {totalPairs} counts set to {cnt}");
                    return 0;
                }
                case "blockset-add":
                {
                    // blockset-add <workspace> <blockset asset> <count> <part objparams name...>: append (count, part) pairs to a
                    // blockset (flat big-endian u32 pairs, no pointers) in every resident and streamed copy; parts already listed are skipped
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    var idx = NB.Core.Project.AssetIndex.LoadOrBuild(ws);
                    string setName = args[2]; uint cnt = uint.Parse(args[3]);
                    var parts = args.Skip(4).Select(p => p.StartsWith("aid_") ? p : "aid_objparams_banjox_vehicleblock_" + p).ToList();
                    var ids = parts.Select(p => NB.Core.Formats.AssetIds.Make(0x1F, p["aid_objparams_".Length..])).ToList();
                    foreach (var (p, id) in parts.Zip(ids))
                        if (!idx.Entries.Any(e => e.Id == id)) throw new InvalidDataException($"{p} (0x{id:X8}) is not an asset in this game");
                    byte[] Extend(byte[] d, out int added)
                    {
                        var have = new HashSet<uint>(); for (int o = 0; o + 8 <= d.Length; o += 8) have.Add(NB.Core.IO.BE.U32(d, o + 4));
                        var add = ids.Where(i => !have.Contains(i)).ToList(); added = add.Count;
                        var nd = new byte[d.Length + 8 * add.Count]; Buffer.BlockCopy(d, 0, nd, 0, d.Length);
                        for (int k = 0; k < add.Count; k++) { NB.Core.IO.BE.W32(nd, d.Length + 8 * k, cnt); NB.Core.IO.BE.W32(nd, d.Length + 8 * k + 4, add[k]); }
                        return nd;
                    }
                    uint setId = idx.Entries.First(e => e.Name == setName).Id;
                    foreach (var e in idx.Entries.Where(x => x.Name == setName && !x.Streamed).GroupBy(x => x.Bundle).Select(g => g.First()))
                    {
                        var caff = ws.LoadResident(e.Bundle);
                        var part = caff.PartsOf(e.Symbol).First(p => caff.SectionOf(p).Name == ".data");
                        if (caff.Relocs.Any(r => r.FromPart == caff.Parts.IndexOf(part) + 1)) { Console.WriteLine($"{e.Bundle:x6}: has pointers, skipped"); continue; }
                        part.Data = Extend(part.Data, out int n); part.Size = part.Data.Length;
                        ws.SaveResident(e.Bundle, caff, $"{setName}: +{n} part(s)");
                        Console.WriteLine($"{e.Bundle:x6} resident: +{n} -> {part.Data.Length / 8} entries");
                    }
                    foreach (var b in idx.Entries.Where(x => x.Name == setName && x.Streamed).Select(x => x.Bundle).Distinct())
                    {
                        var arch = ws.LoadStream(b); bool changed = false;
                        foreach (var en in arch.Entries.Where(x => x.Id == setId && x.Kind == "caff"))
                        {
                            var sc = NB.Core.Formats.CaffFile.Read(en.Data!);
                            int sym = sc.Symbols.FindIndex(s => NB.Core.Formats.AssetIds.DisplayName(s) == setName) + 1;
                            if (sym == 0) continue;
                            var part = sc.PartsOf(sym).First(p => sc.SectionOf(p).Name == ".data");
                            part.Data = Extend(part.Data, out int n); part.Size = part.Data.Length;
                            en.Data = sc.Write(); changed = true;
                            Console.WriteLine($"{b:x6} streamed: +{n} -> {part.Data.Length / 8} entries");
                        }
                        if (changed) ws.SaveStream(b, arch, $"{setName}: parts added");
                    }
                    return 0;
                }
                case "marker-set":
                {
                    // marker-set <workspace> <bundle hex> <marker asset> <type> <index> <offset hex> <u32 hex>: write one word of a marker record
                    // (e.g. world door Jiggy cost at +0x88)
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    uint b = Convert.ToUInt32(args[2], 16);
                    var c = ws.LoadResident(b);
                    int sym = c.Symbols.FindIndex(s => NB.Core.Formats.AssetIds.DisplayName(s) == args[3]) + 1;
                    var ma = NB.Core.World.MarkerAsset.Parse(c, sym);
                    var r = ma.Records.First(x => x.Type == int.Parse(args[4]) && x.Index == int.Parse(args[5]));
                    int off = Convert.ToInt32(args[6], 16); uint val = Convert.ToUInt32(args[7], 16);
                    if (off < 0x30 || off + 4 > r.Size) throw new ArgumentException("offset outside the record's payload");
                    var d = c.PartsOf(sym).First(p => c.SectionOf(p).Name == ".data").Data;
                    uint old = NB.Core.IO.BE.U32(d, r.Offset + off);
                    NB.Core.IO.BE.W32(d, r.Offset + off, val);
                    ws.SaveResident(b, c, $"marker {args[3]} type {args[4]} #{args[5]} +0x{off:X}: 0x{old:X8} -> 0x{val:X8}");
                    Console.WriteLine($"+0x{off:X}: 0x{old:X8} -> 0x{val:X8}");
                    return 0;
                }
                case "marker-dump":
                {
                    // marker-dump <caff> <marker asset> <type> <index>: raw words of one marker record
                    var c = NB.Core.Formats.CaffFile.Read(File.ReadAllBytes(args[1]));
                    int sym = c.Symbols.FindIndex(s => NB.Core.Formats.AssetIds.DisplayName(s) == args[2]) + 1;
                    var ma = NB.Core.World.MarkerAsset.Parse(c, sym);
                    var r = ma.Records.First(x => x.Type == int.Parse(args[3]) && x.Index == int.Parse(args[4]));
                    var d = c.PartsOf(sym).First(p => c.SectionOf(p).Name == ".data").Data;
                    for (int k = 0; k < r.Size; k += 4)
                    {
                        uint v = NB.Core.IO.BE.U32(d, r.Offset + k);
                        string txt = new string(Enumerable.Range(0, 4).Select(i => d[r.Offset + k + i] is >= 0x20 and < 0x7F ? (char)d[r.Offset + k + i] : '.').ToArray());
                        Console.WriteLine($"+{k:X3}: {v:X8}  {BitConverter.Int32BitsToSingle((int)v),12:G6}  {txt}");
                    }
                    return 0;
                }
                case "model-draws":
                {
                    // model-draws <caff> <model> [texture substring]: every draw (node, LOD-only, instanced, layout, textures, bounds, pixel constants)
                    var c = NB.Core.Formats.CaffFile.Read(File.ReadAllBytes(args[1]));
                    int sym = c.Symbols.FindIndex(s => NB.Core.Formats.AssetIds.DisplayName(s) == args[2]) + 1;
                    var m = NB.Core.Models.ModelAsset.Parse(c, sym);
                    var lodOnly = m.LodOnlyNodes;
                    for (int i = 0; i < m.Draws.Count; i++)
                    {
                        var d = m.Draws[i];
                        if (args.Length > 3 && !d.Textures.Any(t => t.Texture.Contains(args[3]))) continue;
                        var used = d.Indices.Where(k => k < d.Positions.Length).Select(k => d.Positions[k]).ToList();
                        string bb = used.Count == 0 ? "-" : $"({used.Min(p => p.X):F0},{used.Min(p => p.Y):F0},{used.Min(p => p.Z):F0})..({used.Max(p => p.X):F0},{used.Max(p => p.Y):F0},{used.Max(p => p.Z):F0})";
                        Console.WriteLine($"#{i} vb{d.VbRecord} node {d.Node}{(lodOnly.Contains(d.Node) ? " LOD-ONLY" : "")}{(d.Instanced ? " INSTANCED" : "")} verts {d.Positions.Length} idx {d.Indices.Length} prim {d.Primitive} ib 0x{d.IbObject:X} idxRange {(d.Indices.Length > 0 ? d.Indices.Min() + "-" + d.Indices.Max() : "-")} stride {d.Stride} col {(d.Colors != null ? "y" : "n")} {bb}");
                        Console.WriteLine("    " + string.Join(" | ", d.Textures.Select(t => $"s{t.Slot}:{t.Texture.Replace("aid_texture_banjox_", "")}")));
                        if (d.PixelConstants.Count > 0) Console.WriteLine("    pc " + string.Join(" ", d.PixelConstants.Select(kv => $"c{kv.Key}={kv.Value}")));
                    }
                    return 0;
                }
                case "flat-draws":
                {
                    // flat-draws <caff> <model> [y ...]: draws whose drawn vertices all lie at one height (water surfaces), with their materials
                    var c = NB.Core.Formats.CaffFile.Read(File.ReadAllBytes(args[1]));
                    int sym = c.Symbols.FindIndex(s => NB.Core.Formats.AssetIds.DisplayName(s) == args[2]) + 1;
                    var m = NB.Core.Models.ModelAsset.Parse(c, sym);
                    foreach (var d in m.Draws)
                    {
                        if (d.Indices.Length == 0) continue;
                        var ys = d.Indices.Where(i => i < d.Positions.Length).Select(i => d.Positions[i].Y).ToList();
                        float mn = ys.Min(), mx = ys.Max();
                        if (mx - mn > 0.05f) continue;
                        var xs = d.Indices.Where(i => i < d.Positions.Length).Select(i => d.Positions[i]).ToList();
                        Console.WriteLine($"y {mn:F3}  tris {d.Indices.Length / 3,5}  x {xs.Min(p => p.X):F0}..{xs.Max(p => p.X):F0} z {xs.Min(p => p.Z):F0}..{xs.Max(p => p.Z):F0}  vb 0x{d.VbRecord:X} [{string.Join(",", d.Layout.Select(e => e.Format.ToString().Replace("k_", "")))}] tex {string.Join(", ", d.Textures.Select(t => t.Texture.Replace("aid_texture_banjox_shared_", "")))}  vs@0x{d.VertexShaderPoolOffset:X}");
                    }
                    return 0;
                }
                case "markers-json":
                {
                    // markers-json <workspace> <bundle hex> <marker asset> <out.json>: every record (type, index, position, rotation, names, link)
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    uint b = Convert.ToUInt32(args[2], 16);
                    var caff = ws.LoadResident(b);
                    int sym = caff.Symbols.FindIndex(s => NB.Core.Formats.AssetIds.DisplayName(s) == args[3]) + 1;
                    var ma = NB.Core.World.MarkerAsset.Parse(caff, sym);
                    var names = new Dictionary<uint, string>();
                    foreach (var s in caff.Symbols) if (NB.Core.Formats.AssetIds.IdOf(s) is uint id) names.TryAdd(id, NB.Core.Formats.AssetIds.DisplayName(s));
                    var list = ma.Records.Select(r => new
                    {
                        type = r.Type, index = r.Index, typeName = NB.Core.World.MarkerRecord.TypeName(r.Type),
                        pos = new[] { r.Position.X, r.Position.Y, r.Position.Z }, rot = new[] { r.Rotation.X, r.Rotation.Y, r.Rotation.Z }, scale = r.Scale,
                        link = r.Link, assets = r.AssetIds.Select(i => names.GetValueOrDefault(i, i.ToString("X8"))).ToList(), strings = r.Strings,
                    }).ToList();
                    File.WriteAllText(args[4], System.Text.Json.JsonSerializer.Serialize(list, new System.Text.Json.JsonSerializerOptions { WriteIndented = false }));
                    Console.WriteLine($"{list.Count} records -> {args[4]}");
                    return 0;
                }
                case "import-capacity":
                {
                    // import-capacity <caff> <model> [filter]: per diffuse material, LOD-0 vertex buffers usable for import (UVs, float32 positions)
                    var c = NB.Core.Formats.CaffFile.Read(File.ReadAllBytes(args[1]));
                    int sym = c.Symbols.FindIndex(s => NB.Core.Formats.AssetIds.DisplayName(s) == args[2]) + 1;
                    var m = NB.Core.Models.ModelAsset.Parse(c, sym);
                    Console.WriteLine($"LOD-only nodes: {m.LodOnlyNodes.Count}; instanced draws: {m.Draws.Count(d => d.Instanced)}");
                    foreach (var g in m.Draws.GroupBy(d => NB.Core.Models.ObjExporter.DiffuseTexture(d) ?? "-").OrderByDescending(g => g.Count()))
                    {
                        if (args.Length > 3 && !g.Key.Contains(args[3])) continue;
                        var vbs = g.Where(d => !m.LodOnlyNodes.Contains(d.Node)).GroupBy(d => d.VbRecord).Select(v => v.First()).ToList();
                        int uv = vbs.Count(d => d.Layout.Any(e => e.Format is NB.Core.Models.VtxFormat.k_16_16_FLOAT or NB.Core.Models.VtxFormat.k_32_32_FLOAT));
                        int f32 = vbs.Count(d => d.Layout.Any(e => e.Format is NB.Core.Models.VtxFormat.k_16_16_FLOAT or NB.Core.Models.VtxFormat.k_32_32_FLOAT) && (d.Layout.FirstOrDefault(e => e.Offset == 0) ?? d.Layout.First()).Format == NB.Core.Models.VtxFormat.k_32_32_32_FLOAT);
                        int novc = vbs.Count(d => !d.Layout.Any(e => e.Format == NB.Core.Models.VtxFormat.k_8_8_8_8));
                        Console.WriteLine($"{g.Count(),4} draws  {vbs.Count,3} LOD0 buffers  {uv,3} with UV  {f32,3} UV+float32  {novc,3} no vcol  {g.Key.Replace("aid_texture_banjox_shared_", "")}");
                    }
                    return 0;
                }
                case "model-survey":
                {
                    // model-survey <caff> [max draws]: reference models with their draw count, vertex layouts, texture slots (to pick clone templates)
                    var c = NB.Core.Formats.CaffFile.Read(File.ReadAllBytes(args[1]));
                    int maxDraws = args.Length > 2 ? int.Parse(args[2]) : 6;
                    var reloc = NB.Core.Formats.AssetView.BuildRelocIndex(c);
                    for (int s = 1; s <= c.Symbols.Count; s++)
                    {
                        var nm = c.Symbols[s - 1];
                        if (!nm.StartsWith("aid_model_")) continue;
                        NB.Core.Models.ModelAsset m;
                        try { m = NB.Core.Models.ModelAsset.Parse(c, s, reloc); } catch { continue; }
                        if (m.Draws.Count == 0 || m.Draws.Count > maxDraws || m.Instances.Count > 0) continue;
                        var lay = string.Join("|", m.Draws.Select(d => string.Join(",", d.Layout.Select(e => e.Format.ToString().Replace("k_", "")))).Distinct());
                        bool colour = m.Draws.Any(d => d.Colors != null);
                        int layered = m.Draws.Count(d => NB.Core.Models.ObjExporter.MaterialLayers(d).Overlay != null);
                        var mats = m.Draws.Select(d => NB.Core.Models.ObjExporter.DiffuseTexture(d)?.Replace("aid_texture_banjox_shared_", "") ?? "-").Distinct().ToList();
                        int vbs = m.Draws.Select(d => d.VbRecord).Distinct().Count();
                        int inst = m.Draws.Count(d => d.Instanced);
                        Console.WriteLine($"{NB.Core.Formats.AssetIds.DisplayName(nm).Replace("aid_model_banjox_background_showdowntown_showdowntownreferences_", "")}\tdraws {m.Draws.Count} vbs {vbs} mats {mats.Count} layered {layered} vcol {(colour ? "yes" : "no")} instanced {inst}\t[{lay}]\t{string.Join(" ; ", mats)}");
                    }
                    return 0;
                }
                case "tex-list":
                {
                    // tex-list <workspace> <bundle hex> [name filter] [--exclusive]: resident textures with size/format; --exclusive = only textures no other resident bundle has
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    var idx = NB.Core.Project.AssetIndex.LoadOrBuild(ws);
                    uint b = Convert.ToUInt32(args[2], 16);
                    string filt = args.Length > 3 && !args[3].StartsWith("--") ? args[3] : "";
                    var caff = ws.LoadResident(b);
                    var inOthers = idx.Entries.Where(e => e.Type == "texture" && !e.Streamed && e.Bundle != b).Select(e => e.Name).ToHashSet();
                    for (int s = 1; s <= caff.Symbols.Count; s++)
                    {
                        var nm = NB.Core.Formats.AssetIds.DisplayName(caff.Symbols[s - 1]);
                        if (!nm.StartsWith("aid_texture_") || !nm.Contains(filt)) continue;
                        if (args.Contains("--exclusive") && inOthers.Contains(nm)) continue;
                        var cpu = caff.PartsOf(s).FirstOrDefault(p => caff.SectionOf(p).Name == ".data");
                        if (cpu == null || !NB.Core.Textures.TextureHeader.IsTexture(cpu.Data)) continue;
                        var th = NB.Core.Textures.TextureHeader.Parse(cpu.Data);
                        Console.WriteLine($"{nm}\t{th}");
                    }
                    return 0;
                }
                case "ref-swap":
                {
                    // ref-swap <workspace> <world bundle hex> <old model name> <new model name>: repoint a chunk-12 reference model id (test tool)
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    var idx = NB.Core.Project.AssetIndex.LoadOrBuild(ws);
                    uint b = Convert.ToUInt32(args[2], 16);
                    var we = NB.Core.Project.WorldCatalog.FromIndex(idx).First(x => x.Bundle == b);
                    var caff = ws.LoadResident(b);
                    int bg = caff.Symbols.IndexOf(we.BackgroundModel) + 1;
                    var m = NB.Core.Models.ModelAsset.Parse(caff, bg, geometry: false);
                    uint oldId = NB.Core.Formats.AssetIds.IdOf(args[3]) ?? 0, newId = NB.Core.Formats.AssetIds.IdOf(args[4]) ?? 0;
                    var d = m.View.Data(".data"); int h = m.Chunks[12]; int ids = m.View.PtrAt(".data", h + 0x18)!.Value.Offset; int nref = NB.Core.IO.BE.S32(d, h);
                    int k = Enumerable.Range(0, nref).FirstOrDefault(i => NB.Core.IO.BE.U32(d, ids + 4 * i) == oldId, -1);
                    if (k < 0) { Console.WriteLine("old id not referenced"); return 1; }
                    NB.Core.IO.BE.W32(d, ids + 4 * k, newId);
                    ws.SaveResident(b, caff, $"reference #{k}: {args[3]} -> {args[4]}");
                    Console.WriteLine($"reference #{k}: {oldId:X8} -> {newId:X8}");
                    return 0;
                }
                case "caff-pool":
                {
                    // caff-pool <caff file>: list pool parts (part → owner symbol, section, count) and the pool record layout
                    var c = NB.Core.Formats.CaffFile.Read(File.ReadAllBytes(args[1]));
                    Console.WriteLine($"{c.Symbols.Count} symbols, {c.Parts.Count} parts, {c.Relocs.Count} reloc groups, {c.PoolParts.Count} pool parts, {c.PoolRecords.Length / 16} pool records");
                    foreach (var (part, count) in c.PoolParts)
                    {
                        var p = c.Parts[part - 1];
                        Console.WriteLine($"  pool part {part}: {c.SectionOf(p).Name} of {NB.Core.Formats.AssetIds.DisplayName(c.Symbols[p.Symbol - 1])}, {count} records");
                    }
                    for (int i = 0; i < Math.Min(8, c.PoolRecords.Length / 16); i++) Console.WriteLine("  rec " + Convert.ToHexString(c.PoolRecords, 16 * i, 16));
                    return 0;
                }
                case "model-winding":
                {
                    // model-winding <caff> <model name>: stored triangle order vs vertex normals (right-handed test: + = counter-clockwise front)
                    var c = NB.Core.Formats.CaffFile.Read(File.ReadAllBytes(args[1]));
                    int sym = c.Symbols.FindIndex(s => NB.Core.Formats.AssetIds.DisplayName(s) == args[2]) + 1;
                    var m = NB.Core.Models.ModelAsset.Parse(c, sym);
                    long pos = 0, neg = 0;
                    foreach (var d in m.Draws.Where(d => d.Normals != null))
                        for (int k = 0; k + 2 < d.Indices.Length; k += 3)
                        {
                            int a = d.Indices[k], b = d.Indices[k + 1], e = d.Indices[k + 2];
                            if (a >= d.Positions.Length || b >= d.Positions.Length || e >= d.Positions.Length) continue;
                            var n = System.Numerics.Vector3.Cross(d.Positions[b] - d.Positions[a], d.Positions[e] - d.Positions[a]);
                            float s = System.Numerics.Vector3.Dot(n, d.Normals![a] + d.Normals[b] + d.Normals[e]);
                            if (s > 0) pos++; else if (s < 0) neg++;
                        }
                    Console.WriteLine($"{args[2]}: {pos} triangles counter-clockwise about their normals (right-handed), {neg} clockwise");
                    return 0;
                }
                case "model-fbx":
                {
                    // model-fbx <caff> <model name> <out.fbx>: export a model's draws as binary FBX 7.4
                    var c = NB.Core.Formats.CaffFile.Read(File.ReadAllBytes(args[1]));
                    int sym = c.Symbols.FindIndex(s => NB.Core.Formats.AssetIds.DisplayName(s) == args[2]) + 1;
                    var m = NB.Core.Models.ModelAsset.Parse(c, sym);
                    var skel = NB.Core.Models.Skeleton.Parse(c.PartsOf(sym).First(p => c.SectionOf(p).Name == ".data").Data);
                    NB.Core.Models.FbxExporter.Write(args[3], args[2], m.Draws.Select(d => (d, System.Numerics.Matrix4x4.Identity)), skeleton: skel);
                    Console.WriteLine($"wrote {args[3]} ({new FileInfo(args[3]).Length:N0} bytes, {m.Draws.Count} draws, {skel?.Count ?? 0} joints)");
                    return 0;
                }
                case "anim-fbx":
                {
                    // anim-fbx <model caff> <model name> <anim caff> <anim name> <out.fbx>: character + skeleton + skin + one animation
                    var c = NB.Core.Formats.CaffFile.Read(File.ReadAllBytes(args[1]));
                    int sym = c.Symbols.FindIndex(s => NB.Core.Formats.AssetIds.DisplayName(s) == args[2]) + 1;
                    var m = NB.Core.Models.ModelAsset.Parse(c, sym);
                    var skel = NB.Core.Models.Skeleton.Parse(c.PartsOf(sym).First(p => c.SectionOf(p).Name == ".data").Data)
                               ?? throw new InvalidDataException("model has no skeleton");
                    var ac = args[3] == args[1] ? c : NB.Core.Formats.CaffFile.Read(File.ReadAllBytes(args[3]));
                    int asym = ac.Symbols.FindIndex(s => NB.Core.Formats.AssetIds.DisplayName(s) == args[4]) + 1;
                    if (asym == 0) { Console.WriteLine("animation not found"); return 1; }
                    var anim = NB.Core.Models.AnimAsset.Parse(ac.PartsOf(asym).First(p => ac.SectionOf(p).Name == ".data").Data);
                    if (anim.Tracks != skel.Count) Console.WriteLine($"warning: {anim.Tracks} tracks vs {skel.Count} joints");
                    NB.Core.Models.FbxExporter.Write(args[5], args[2], m.Draws.Select(d => (d, System.Numerics.Matrix4x4.Identity)), skeleton: skel,
                        animation: anim, animationName: args[4].Replace("aid_anim_banjox_", ""));
                    Console.WriteLine($"wrote {args[5]} ({new FileInfo(args[5]).Length:N0} bytes): {skel.Count} joints, {anim.Frames} frames ({anim.Duration:0.###} s), {anim.KeyFrames.Length} keys");
                    return 0;
                }
                case "anim-verify":
                    // anim-verify <workspace> [name filter] [--no-edit]: re-encode every cached anim (read-only) and check it
                    return AnimCommands.Verify(args);
                case "anim-rotate":
                    // anim-rotate <workspace> <anim> <joint> <rx,ry,rz degrees> [--model <model>] [--dry]: rotate one joint in every key
                    return AnimCommands.Rotate(args);
                case "anim-import":
                    // anim-import <workspace> <anim> <file.fbx> [--model <model>] [--take <name>] [--dry]: FBX take -> anim keys
                    return AnimCommands.Import(args);
                case "skeleton":
                {
                    // skeleton <caff> <model or anim name>: print the joint hierarchy ("pose" object)
                    var c = NB.Core.Formats.CaffFile.Read(File.ReadAllBytes(args[1]));
                    int sym = c.Symbols.FindIndex(s => NB.Core.Formats.AssetIds.DisplayName(s) == args[2]) + 1;
                    var joints = NB.Core.Models.Skeleton.Parse(c.PartsOf(sym).First(p => c.SectionOf(p).Name == ".data").Data);
                    if (joints == null) { Console.WriteLine("no skeleton (pose object) in this asset"); return 1; }
                    foreach (var j in joints)
                    {
                        int depth = 0; for (int p = j.Parent; p >= 0 && depth < 64; p = joints[p].Parent) depth++;
                        Console.WriteLine($"{j.Index,4} {new string(' ', depth)}{j.Name}  local ({j.LocalTranslation.X:0.###}, {j.LocalTranslation.Y:0.###}, {j.LocalTranslation.Z:0.###})" +
                            (j.Mirror >= 0 ? $"  mirror {joints[j.Mirror].Name}" : ""));
                    }
                    Console.WriteLine($"{joints.Count} joints");
                    return 0;
                }
                case "collision":
                {
                    // collision <workspace> <bundle hex> <havok asset name | --all> [out.obj]: decode Havok collision
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    var caff = ws.LoadResident(Convert.ToUInt32(args[2], 16));
                    var targets = args[3] == "--all"
                        ? Enumerable.Range(1, caff.Symbols.Count).Where(s => caff.Symbols[s - 1].StartsWith("aid_havok_")).ToList()
                        : new List<int> { caff.Symbols.FindIndex(s => NB.Core.Formats.AssetIds.DisplayName(s) == args[3]) + 1 };
                    int ok = 0, stub = 0, fail = 0, tris = 0; var notes = new Dictionary<string, int>();
                    foreach (int sym in targets)
                    {
                        var d = new NB.Core.Formats.AssetView(caff, sym).Data(".data");
                        if (!NB.Core.Havok.HkPackfile.IsPackfileAsset(d)) { stub++; continue; }
                        try
                        {
                            var hc = NB.Core.Havok.HkCollision.ExtractAsset(d);
                            ok++; tris += hc.Meshes.Sum(m => m.Triangles.Count / 3);
                            foreach (var n in hc.Notes) notes[n] = notes.GetValueOrDefault(n) + 1;
                            if (targets.Count == 1)
                            {
                                Console.WriteLine($"{hc.Meshes.Count} mesh(es): " + string.Join(", ", hc.Meshes.Select(m => $"{m.Kind} {m.Positions.Count}v/{m.Triangles.Count / 3}t")));
                                if (args.Length > 4) { hc.WriteObj(args[4]); Console.WriteLine("wrote " + args[4]); }
                            }
                        }
                        catch (Exception ex) { fail++; notes["error: " + ex.Message] = notes.GetValueOrDefault("error: " + ex.Message) + 1; }
                    }
                    Console.WriteLine($"havok assets: {ok} decoded, {stub} without packfile (stubs), {fail} failed; {tris} triangles");
                    foreach (var (n, c) in notes.OrderByDescending(kv => kv.Value).Take(15)) Console.WriteLine($"  {c,5} × {n}");
                    return fail == 0 ? 0 : 3;
                }
                case "exe-mods":
                {
                    // exe-mods <workspace> [xenia dir] [mod id...]: verify executable mods; write the Xenia patch file for the given ids
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    var xexBytes = File.ReadAllBytes(Path.Combine(ws.Game.Root, "default.xex"));
                    var xex = NB.Core.Formats.XexFile.Read(xexBytes);
                    var image = xex.GetImage();
                    ulong imageHash = NB.Core.Mods.ExePatches.XeniaModuleHash(xexBytes, image);
                    string? xdir = args.Length > 2 && !args[2].StartsWith("--") ? args[2] : null;
                    ulong hash = NB.Core.Mods.ExePatches.ResolveXeniaHash(imageHash, xdir) ?? throw new InvalidDataException("unknown executable: run it once in Xenia so xenia.log reports its module hash");
                    Console.WriteLine($"image hash {imageHash:X16} -> Xenia module hash {hash:X16}");
                    if (args.Contains("--hash-probe"))
                    {
                        uint sec = NB.Core.IO.BE.U32(xexBytes, 0x10), cnt = NB.Core.IO.BE.U32(xexBytes, (int)sec + 0x180);
                        long pageAcc = 0; var codeRanges = new List<(long, long)>();
                        for (int i = 0; i < cnt; i++)
                        {
                            uint v = NB.Core.IO.BE.U32(xexBytes, (int)sec + 0x184 + 0x18 * i);
                            Console.WriteLine($"  desc {i}: type {v & 0xF} pages {v >> 4}");
                            if ((v & 0xF) == 1) codeRanges.Add((pageAcc, pageAcc + (v >> 4)));
                            pageAcc += v >> 4;
                        }
                        foreach (uint ps in new uint[] { 0x1000, 0x10000 })
                        {
                            Console.WriteLine($"  index-based ps {ps:X}: {NB.Core.Mods.ExePatches.XeniaModuleHash(xexBytes, image, ps):X16}");
                            if (codeRanges.Count > 0)
                            {
                                long s = codeRanges[0].Item1 * ps, e = Math.Min(image.Length, codeRanges[^1].Item2 * ps);
                                Console.WriteLine($"  page-based ps {ps:X}: range {s:X}-{e:X} {System.IO.Hashing.XxHash3.HashToUInt64(image.AsSpan((int)s, (int)(e - s))):X16}");
                            }
                        }
                    }
                    foreach (var m in NB.Core.Mods.ExePatches.All)
                    {
                        var p = NB.Core.Mods.ExePatches.Check(image, xex.ImageBase, m);
                        Console.WriteLine($"  [{m.Id}] {m.Name}: {(p.Count == 0 ? "original bytes match" : string.Join("; ", p))}");
                    }
                    if (xdir != null)
                    {
                        var ids = args.Skip(3).Where(a => !a.StartsWith("--")).ToHashSet();
                        var path = NB.Core.Mods.ExePatches.WriteXeniaPatchFile(xdir, hash, NB.Core.Mods.ExePatches.ResolveAll(ids));
                        Console.WriteLine((ids.Count == 0 ? "removed " : "wrote ") + path);
                    }
                    return 0;
                }
                case "preset-flags":
                {
                    // preset-flags <workspace> [--remove] <flag> [flag...]: TEST MODE — set (or stop setting) these game flags when a new game starts
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    Console.WriteLine(args.Length > 2 && args[2] == "--remove"
                        ? NB.Core.World.TestMode.UnpresetFlags(ws, args.Skip(3))
                        : NB.Core.World.TestMode.PresetFlags(ws, args.Skip(2)));
                    return 0;
                }
                case "start-in":
                {
                    // start-in <workspace> <script name>: TEST MODE — a new game jumps to this script (world/act)
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    Console.WriteLine(NB.Core.World.TestMode.StartIn(ws, args[2]));
                    return 0;
                }
                case "ws-export":
                {
                    // ws-export <workspace> <target dir> [--changed-only] [--console]: playable game directory or mod package; --console bakes executable mods into default.xex (RGH/JTAG)
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    int n = ws.Export(args[2], args.Contains("--changed-only"), bakeExeMods: args.Contains("--console"));
                    if (ws.Manifest.ExeMods.Count > 0)
                    {
                        var xb = File.ReadAllBytes(Path.Combine(ws.Game.Root, "default.xex"));
                        var img = NB.Core.Formats.XexFile.Read(xb).GetImage();
                        var h = NB.Core.Mods.ExePatches.ResolveXeniaHash(NB.Core.Mods.ExePatches.XeniaModuleHash(xb, img), null);
                        if (h != null) NB.Core.Mods.ExePatches.WriteXeniaPatchFile(Path.Combine(args[2], "xenia_patches"), h.Value, NB.Core.Mods.ExePatches.ResolveAll(ws.Manifest.ExeMods));
                    }
                    Console.WriteLine($"exported {n} file(s) to {args[2]}");
                    return 0;
                }
                case "ops-apply":
                {
                    // ops-apply <workspace | game folder> <ops.json>: run world edits (a JSON list of string lists, see WorldOps) on a
                    // workspace or, with a plain game folder, in place like an edition build (with backups for patch-rollback)
                    var ops = ReadOps(args[2]);
                    if (File.Exists(Path.Combine(args[1], "workspace.json")))
                    {
                        var ws = NB.Core.Project.Workspace.Open(args[1]);
                        foreach (var op in ops) NB.Core.Project.WorldOps.Run(ws, op, Console.WriteLine, () => NB.Core.Project.AssetIndex.LoadOrBuild(ws));
                        NB.Core.Project.AssetIndex.LoadOrBuild(ws, null, true);
                    }
                    else
                        Console.WriteLine($"{NB.Core.Project.PatchPackage.ApplyOps(new NB.Core.Project.PatchPackage.PatchManifest { Name = Path.GetFileNameWithoutExtension(args[2]), Id = "ops-" + Path.GetFileNameWithoutExtension(args[2]), Ops = ops }, args[1], Console.WriteLine)} file(s) changed");
                    return 0;
                }
                case "game-verify":
                {
                    // game-verify <game dir>: compare every file with the retail fingerprints (size + SHA-256)
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var res = NB.Core.Project.GameDiff.CompareWithRetail(args[1]);
                    foreach (var r in res) Console.WriteLine($"{r.State,-8} {r.Path}");
                    Console.WriteLine(res.Count == 0 ? $"retail: all {NB.Core.Project.GameDiff.Retail.Count} files match ({sw.Elapsed.TotalSeconds:F0}s)" : $"{res.Count} difference(s) ({sw.Elapsed.TotalSeconds:F0}s)");
                    return res.Count == 0 ? 0 : 1;
                }
                case "game-diff":
                case "patch-from-folder":
                {
                    // game-diff <modified game dir> [--ref <clean game dir>]...: what a hand-modded game folder changes, per asset
                    // patch-from-folder <modified game dir> <out.nbpatch> [--ref dir]... [--name N] [--author A] [--desc D] [--version V]
                    //             [--category c] [--multiplayer m] [--tags a,b]: build a mod from it (clean reference: the first --ref that is retail)
                    bool build = args[0] == "patch-from-folder";
                    string Opt(string k, string d) { int i = Array.IndexOf(args, k); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    int lastPc = -1;
                    var cmp = NB.Core.Project.GameDiff.CompareWithRetail(args[1], new Progress<(string T, double F)>(p => { int pc = (int)(p.F * 100); if (pc != lastPc) { lastPc = pc; Console.Error.Write($"\r{pc}% "); } }));
                    Console.Error.WriteLine();
                    var refs = Enumerable.Range(0, args.Length - 1).Where(i => args[i] == "--ref").Select(i => args[i + 1]).ToList();
                    var refDir = NB.Core.Project.GameDiff.FindReference(refs, cmp.Where(c => c.State == "changed").Select(c => c.Path), args[1]);
                    Console.WriteLine($"reference: {refDir ?? "(none of the --ref folders has the original files)"}");
                    var rep = NB.Core.Project.GameDiff.Analyze(args[1], refDir, null, default, cmp);
                    foreach (var f in rep.Files)
                    {
                        Console.WriteLine($"{f.State,-8} {f.Path}  [{f.Area}]  {f.Summary}  kinds: {string.Join(",", f.Kinds)}");
                        foreach (var a in f.Changed.Take(12)) Console.WriteLine("           ~ " + a);
                        foreach (var a in f.Added.Take(12)) Console.WriteLine("           + " + a);
                        foreach (var a in f.Removed.Take(12)) Console.WriteLine("           - " + a);
                        int more = f.Changed.Count + f.Added.Count + f.Removed.Count - Math.Min(12, f.Changed.Count) - Math.Min(12, f.Added.Count) - Math.Min(12, f.Removed.Count);
                        if (more > 0) Console.WriteLine($"           ... {more} more");
                    }
                    if (rep.Exe != null)
                    {
                        foreach (var m in rep.Exe.Known) Console.WriteLine($"exe: known mod {m.Id} ({m.Name})");
                        foreach (var w in rep.Exe.Other.Take(20)) Console.WriteLine($"exe: {w.Address:X8} {w.Original:X8} -> {w.Patched:X8}");
                        if (rep.Exe.Other.Count > 20) Console.WriteLine($"exe: ... {rep.Exe.Other.Count - 20} more words");
                    }
                    foreach (var w in rep.Warnings) Console.WriteLine("warning: " + w);
                    Console.WriteLine($"suggested: category {rep.Category}, multiplayer {rep.Multiplayer}, tags [{string.Join(", ", rep.Tags)}], name \"{rep.SuggestedName}\"");
                    Console.WriteLine($"  {rep.SuggestedDescription}");
                    if (!build) return 0;
                    var tags = Opt("--tags", "");
                    var man = NB.Core.Project.PatchPackage.BuildFromFolders(rep, args[2], Opt("--name", rep.SuggestedName), Opt("--author", ""), Opt("--desc", rep.SuggestedDescription),
                        new Progress<(string F, double P)>(p => Console.WriteLine($"  [{sw.Elapsed.TotalSeconds,6:F1}s] {p.F}")),
                        m =>
                        {
                            m.Version = Opt("--version", m.Version); m.Id = Opt("--id", m.Id); m.Category = Opt("--category", m.Category); m.Multiplayer = Opt("--multiplayer", m.Multiplayer);
                            if (tags.Length > 0) m.Tags = tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                        });
                    foreach (var f in man.Files) Console.WriteLine($"{f.Kind,-7} {f.Path}: {f.TargetSize:N0} bytes = {f.CopiedBytes:N0} copied + {f.LiteralBytes:N0} new");
                    Console.WriteLine($"exe mods: {string.Join(", ", man.ExeMods.Select(m => $"{m.Id} ({m.Words.Count} words)"))}");
                    Console.WriteLine($"wrote {args[2]} ({new FileInfo(args[2]).Length:N0} bytes) in {sw.Elapsed.TotalSeconds:F0}s");
                    return 0;
                }
                case "xex-poke":
                {
                    // xex-poke <in default.xex> <out default.xex> [--mod <exe mod id>]... [address=value]...: write words into the
                    // executable image like a hand edit (testing "patch from a modified folder"); the result is decrypted, unsigned
                    var xex = NB.Core.Formats.XexFile.Read(File.ReadAllBytes(args[1]));
                    var words = new List<(uint, uint)>();
                    for (int i = 3; i < args.Length; i++)
                    {
                        if (args[i] == "--mod") { var m = NB.Core.Mods.ExePatches.Resolve(args[++i]) ?? throw new ArgumentException("unknown exe mod " + args[i]); words.AddRange(m.Words.Select(w => (w.Address, w.Patched))); continue; }
                        var kv = args[i].Split('='); words.Add((Convert.ToUInt32(kv[0], 16), Convert.ToUInt32(kv[1], 16)));
                    }
                    File.WriteAllBytes(args[2], xex.WritePatched(words));
                    Console.WriteLine($"{words.Count} word(s) written -> {args[2]}");
                    return 0;
                }
                case "link-copy":
                {
                    // link-copy <game dir> <new dir> [relative path]...: copy a game folder as hard links (same drive), the listed
                    // files as real copies (to be changed); the original is never written
                    NB.Core.IO.FileLinks.LinkCopy(args[1], args[2], new HashSet<string>(args.Skip(3).Select(f => f.Replace('/', Path.DirectorySeparatorChar)), StringComparer.OrdinalIgnoreCase));
                    Console.WriteLine($"linked {args[1]} -> {args[2]} ({args.Length - 3} real cop{(args.Length - 3 == 1 ? "y" : "ies")})");
                    return 0;
                }
                case "patch-build":
                {
                    // patch-build <workspace> <out.nbpatch> [--name N] [--author A] [--desc D] [--version V] [--id ID] [--multiplayer cosmetic|world|coop]
                    //             [--requires id,id] [--conflicts id,id] [--category map|parts|gameplay|visual|audio|tweak|coop] [--tags a,b] [--no-exe] [--extra key=value ...]: differential patch of every modified file
                    // (--extra: settings for tools, e.g. mode=coop puppetBlueprint=00123456 for NB Multiplayer)
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    string Opt(string k, string d) { int i = Array.IndexOf(args, k); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var man = NB.Core.Project.PatchPackage.Build(ws, args[2], Opt("--name", Path.GetFileNameWithoutExtension(args[2])), Opt("--author", ""), Opt("--desc", ""),
                        !args.Contains("--no-exe"), new Progress<(string F, double P)>(p => Console.WriteLine($"  [{sw.Elapsed.TotalSeconds,6:F1}s] {p.F}")),
                        Enumerable.Range(0, args.Length - 1).Where(i => args[i] == "--extra" && args[i + 1].Contains('='))
                            .Select(i => args[i + 1].Split('=', 2)).ToDictionary(kv => kv[0], kv => kv[1]),
                        m =>
                        {
                            List<string> Ids(string k) => Opt(k, "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                            m.Version = Opt("--version", m.Version); m.Id = Opt("--id", m.Id); m.Multiplayer = Opt("--multiplayer", m.Multiplayer);
                            m.Requires = Ids("--requires"); m.Conflicts = Ids("--conflicts");
                            m.Category = Opt("--category", m.Category); m.Tags = Ids("--tags");
                            if (Opt("--ops", "") is { Length: > 0 } of) m.Ops = ReadOps(of);   // --ops ops.json: world edits replayed onto the game (WorldOps)
                        });
                    Console.WriteLine($"mod {man.Id} {man.Version}  multiplayer: {(man.Multiplayer.Length > 0 ? man.Multiplayer : "(not stated)")}");
                    if (man.Ops.Count > 0) Console.WriteLine($"world edits: {man.Ops.Count} op(s) ({string.Join(", ", man.Ops.Select(o => o[0]).Distinct())})");
                    foreach (var f in man.Files) Console.WriteLine($"{f.Kind,-5} {f.Path}: {f.TargetSize:N0} bytes = {f.CopiedBytes:N0} copied from the original + {f.LiteralBytes:N0} new");
                    Console.WriteLine($"exe mods: {string.Join(", ", man.ExeMods.Select(m => m.Id))}");
                    Console.WriteLine($"wrote {args[2]} ({new FileInfo(args[2]).Length:N0} bytes, {man.Files.Count} files) in {sw.Elapsed.TotalSeconds:F0}s");
                    return 0;
                }
                case "patch-verify":
                {
                    // patch-verify <patch> <game dir>
                    var res = NB.Core.Project.PatchPackage.Verify(args[1], args[2]);
                    foreach (var r in res) Console.WriteLine($"{r.State,-9} {r.Path}  {r.Detail}");
                    bool ok = res.All(r => r.State is "ok" or "applied" or "new");
                    Console.WriteLine(ok ? "patch can be applied" : "patch CANNOT be applied to this directory");
                    return ok ? 0 : 1;
                }
                case "tweak-build":
                {
                    // tweak-build <original default.xex> <out dir>: one tick-box mod per NB Multiplayer tweak (identical bytes on every PC)
                    Directory.CreateDirectory(args[2]);
                    foreach (var (mod, name, blurb) in NB.Core.Mods.ExePatches.Tweaks)
                    {
                        var outp = Path.Combine(args[2], "tweak-" + NB.Core.Project.PatchPackage.Slug(mod.Id) + ".nbpatch");
                        NB.Core.Project.PatchPackage.BuildTweak(args[1], mod, name, blurb, outp);
                        Console.WriteLine($"{NB.Core.Project.PatchPackage.FileSha(outp)[..16]}  {new FileInfo(outp).Length,6:N0}  {Path.GetFileName(outp)}");
                    }
                    return 0;
                }
                case "stack-check":
                case "stack-apply":
                {
                    // stack-check <patch> <patch> ...: can these mods be combined into one edition (in this order)?
                    // stack-apply <game dir> <patch> <patch> ...: applies them to an unmodified game copy (executable mods merged)
                    bool apply = args[0] == "stack-apply";
                    var mods = args.Skip(apply ? 2 : 1).Select(NB.Core.Project.ModStack.Load).ToList();
                    foreach (var m in mods)
                        Console.WriteLine($"{m.Id,-24} {m.Manifest.Version,-6} {m.Manifest.Files.Count,3} file(s), {m.Manifest.ExeMods.Count} exe mod(s)  sha {m.Sha256[..12]}");
                    Console.WriteLine($"recipe key {NB.Core.Project.ModStack.Key(mods.Select(m => m.Sha256))}");
                    var probs = NB.Core.Project.ModStack.Problems(mods);
                    foreach (var p in probs) Console.WriteLine("PROBLEM: " + p);
                    if (probs.Count > 0) return 1;
                    foreach (var note in NB.Core.Project.ModStack.Notes(mods)) Console.WriteLine("note: " + note);
                    Console.WriteLine("these mods can be combined (shared files are checked asset by asset when they are applied)");
                    if (!apply) return 0;
                    int n = NB.Core.Project.ModStack.Apply(mods, args[1], null, Console.WriteLine);
                    Console.WriteLine($"applied: {n} file(s) written");
                    return 0;
                }
                case "stack-explain":
                {
                    // stack-explain <original game dir> <patch> <patch> ...: for every game file several mods change, which archive
                    // entries each mod changes (research for merging mods inside a file)
                    var mods = args.Skip(2).Select(NB.Core.Project.ModStack.Load).ToList();
                    var byFile = mods.SelectMany(m => m.Manifest.Files.Where(f => f.Kind == "delta").Select(f => (m, f)))
                        .GroupBy(x => x.f.Path, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1);
                    static Dictionary<uint, byte[]> Entries(byte[] d)
                    {
                        if (NB.Core.Compression.XCompressFile.IsCompressed(d)) d = NB.Core.Compression.XCompressFile.Decompress(d);
                        var res = new Dictionary<uint, byte[]>();
                        if (!NB.Core.Formats.BundleArchive.IsArchive(d)) return res;
                        foreach (var e in NB.Core.Formats.BundleArchive.Read(d).Entries)
                        {
                            var x = e.Data ?? Array.Empty<byte>();
                            if (NB.Core.Compression.XCompressFile.IsCompressed(x)) x = NB.Core.Compression.XCompressFile.Decompress(x);
                            res[e.Id] = x;
                        }
                        return res;
                    }
                    foreach (var g in byFile)
                    {
                        var raw = File.ReadAllBytes(Path.Combine(args[1], g.Key.Replace('/', Path.DirectorySeparatorChar)));
                        var orig = Entries(raw);
                        var src = NB.Core.Project.PatchPackage.Expand(raw);
                        Console.WriteLine($"== {g.Key}: {orig.Count} entries in the original");
                        var changedBy = new Dictionary<uint, List<string>>();
                        foreach (var (m, f) in g)
                        {
                            using var zip = System.IO.Compression.ZipFile.OpenRead(m.Path);
                            var ms = new MemoryStream(); using (var st = zip.GetEntry(f.Entry)!.Open()) st.CopyTo(ms);
                            var res = Entries(NB.Core.Project.Delta.Apply(src, ms.ToArray()));
                            var ch = res.Where(kv => !orig.TryGetValue(kv.Key, out var o) || !o.AsSpan().SequenceEqual(kv.Value)).Select(kv => kv.Key)
                                .Concat(orig.Keys.Where(k => !res.ContainsKey(k))).ToList();
                            Console.WriteLine($"  {m.Manifest.Name}: {ch.Count} entr(y/ies) changed/added/removed: {string.Join(", ", ch.Select(k => k.ToString("x8")))}");
                            foreach (var k in ch) (changedBy.TryGetValue(k, out var l) ? l : changedBy[k] = new()).Add(m.Manifest.Name);
                        }
                        var both = changedBy.Where(kv => kv.Value.Count > 1).ToList();
                        Console.WriteLine(both.Count == 0 ? "  -> no entry is changed by two mods: an entry-level merge would combine them"
                            : "  -> entries changed by more than one mod: " + string.Join(", ", both.Select(kv => $"{kv.Key:x8} ({string.Join(" + ", kv.Value)})")));
                    }
                    return 0;
                }
                case "patch-apply":
                {
                    // patch-apply <patch> <game dir> [--xenia <xenia dir>]
                    int xi = Array.IndexOf(args, "--xenia");
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    int n = NB.Core.Project.PatchPackage.Apply(args[1], args[2], xi >= 0 ? args[xi + 1] : null, Console.WriteLine);
                    Console.WriteLine($"patched {n} file(s) in {sw.Elapsed.TotalSeconds:F0}s; rollback: patch-rollback \"{args[2]}\"");
                    return 0;
                }
                case "patch-rollback":
                {
                    // patch-rollback <game dir>
                    int n = NB.Core.Project.PatchPackage.Rollback(args[1], Console.WriteLine);
                    Console.WriteLine($"restored {n} file(s)");
                    return 0;
                }
                case "ws-revert":
                case "ws-undo":
                {
                    // ws-revert <workspace> <relative path>: restore from the original;  ws-undo: restore the previous saved version
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    if (args[0] == "ws-revert") { ws.Revert(args[2]); Console.WriteLine($"reverted {args[2]} to the original"); }
                    else Console.WriteLine($"restored {args[2]} to version {ws.UndoLastSave(args[2])}");
                    return 0;
                }
                case "scene-dup":
                case "scene-del":
                {
                    // scene-dup <workspace> <bundle hex> <instance name> dx,dy,dz   |   scene-del <workspace> <bundle hex> <instance name>
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    uint b = Convert.ToUInt32(args[2], 16);
                    var idx = NB.Core.Project.AssetIndex.LoadOrBuild(ws);
                    var w = NB.Core.Project.WorldCatalog.FromIndex(idx).First(x => x.Bundle == b);
                    var caff = ws.LoadResident(b);
                    int sym = caff.Symbols.FindIndex(s => NB.Core.Formats.AssetIds.DisplayName(s) == w.BackgroundModel) + 1;
                    var m = NB.Core.Models.ModelAsset.Parse(caff, sym, geometry: false);
                    var inst = m.Instances.First(x => NB.Core.Models.ModelAsset.CleanInstanceName(x.Name) == args[3]);
                    if (args[0] == "scene-dup")
                    {
                        var dv = args[4].Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                        var world = inst.World; world.Translation += new System.Numerics.Vector3(dv[0], dv[1], dv[2]);
                        int ni = NB.Core.World.InstanceEditor.Duplicate(caff, sym, inst.Index, world);
                        Console.WriteLine($"duplicated {args[3]} (instance {inst.Index}) as instance {ni} at {world.Translation}");
                        ws.SaveResident(b, caff, $"duplicated {args[3]} at {world.Translation}");
                    }
                    else
                    {
                        NB.Core.World.InstanceEditor.Hide(caff, sym, inst.Index);
                        Console.WriteLine($"deleted {args[3]} (instance {inst.Index} hidden: zero scale, y {NB.Core.World.InstanceEditor.HiddenY})");
                        ws.SaveResident(b, caff, $"deleted {args[3]} (hidden)");
                    }
                    var m2 = NB.Core.Models.ModelAsset.Parse(ws.LoadResident(b), sym, geometry: false);
                    Console.WriteLine($"verify: {m2.Instances.Count} instances (was {m.Instances.Count}); names ok: {m2.Instances.All(x => x.Name.Length > 0)}");
                    return 0;
                }
                case "asset-diff":
                {
                    // asset-diff <workspace> <bundle hex> <asset name> [max]: word diff of an asset, workspace vs original game
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    uint b = Convert.ToUInt32(args[2], 16);
                    byte[] raw = File.ReadAllBytes(ws.Original.ResidentPath(b));
                    if (NB.Core.Compression.XCompressFile.IsCompressed(raw)) raw = NB.Core.Compression.XCompressFile.Decompress(raw);
                    var orig = NB.Core.Formats.CaffFile.Read(raw);
                    var cur = ws.LoadResident(b);
                    int so = orig.Symbols.FindIndex(s => NB.Core.Formats.AssetIds.DisplayName(s) == args[3]) + 1;
                    int sc = cur.Symbols.FindIndex(s => NB.Core.Formats.AssetIds.DisplayName(s) == args[3]) + 1;
                    int max = args.Length > 4 ? int.Parse(args[4]) : 80, shown = 0;
                    foreach (var po in orig.PartsOf(so))
                    {
                        string sec = orig.SectionOf(po).Name;
                        var pc = cur.PartsOf(sc).First(p => cur.SectionOf(p).Name == sec);
                        int n = Math.Min(po.Data.Length, pc.Data.Length) / 4, diffs = 0;
                        for (int i = 0; i < n; i++)
                        {
                            uint a = NB.Core.IO.BE.U32(po.Data, 4 * i), c2 = NB.Core.IO.BE.U32(pc.Data, 4 * i);
                            if (a == c2) continue;
                            diffs++;
                            if (shown++ < max) Console.WriteLine($"  {sec}+0x{4 * i:X}: {a:X8} ({BitConverter.Int32BitsToSingle((int)a):G5}) -> {c2:X8} ({BitConverter.Int32BitsToSingle((int)c2):G5})");
                        }
                        Console.WriteLine($"{sec}: {po.Data.Length} -> {pc.Data.Length} bytes, {diffs} word(s) differ");
                    }
                    return 0;
                }
                case "scene-poke":
                {
                    // scene-poke <workspace> <bundle hex> <instance name> <dy> matrix|node — experiment: move one structure only
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    uint b = Convert.ToUInt32(args[2], 16);
                    var idx = NB.Core.Project.AssetIndex.LoadOrBuild(ws);
                    var w = NB.Core.Project.WorldCatalog.FromIndex(idx).First(x => x.Bundle == b);
                    var caff = ws.LoadResident(b);
                    int sym = caff.Symbols.FindIndex(s => NB.Core.Formats.AssetIds.DisplayName(s) == w.BackgroundModel) + 1;
                    var m = NB.Core.Models.ModelAsset.Parse(caff, sym, geometry: false);
                    var inst = m.Instances.First(x => NB.Core.Models.ModelAsset.CleanInstanceName(x.Name) == args[3]);
                    var d = m.View.Data(".data");
                    float dy = float.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture);
                    if (args[5] == "matrix")
                    {
                        NB.Core.IO.BE.WF32(d, inst.MatrixOffset + 52, NB.Core.IO.BE.F32(d, inst.MatrixOffset + 52) + dy);
                        NB.Core.IO.BE.WF32(d, inst.PositionOffset + 4, NB.Core.IO.BE.F32(d, inst.PositionOffset + 4) + dy);
                    }
                    else
                    {
                        // node for instance i is n-1-i (verified for Showdown Town: 760/760 instances)
                        int nodeIndex = m.Instances.Count - 1 - inst.Index;
                        if (nodeIndex < 0 || nodeIndex >= m.Nodes.Count) throw new InvalidDataException("no node for this instance");
                        int node = m.Chunks[2] + 4 + 68 * nodeIndex;
                        NB.Core.IO.BE.WF32(d, node + 4 + 28, NB.Core.IO.BE.F32(d, node + 4 + 28) + dy);
                    }
                    Console.WriteLine($"{args[3]} (instance {inst.Index}, node {inst.PlacementNode}): {args[5]} y += {dy}");
                    ws.SaveResident(b, caff, $"experiment: {args[3]} {args[5]} y+{dy}");
                    return 0;
                }
                case "model-import":
                {
                    // model-import <workspace> <bundle hex> <model name> <mesh.obj> [--dry]: replace a model's geometry
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    uint b = Convert.ToUInt32(args[2], 16);
                    var caff = ws.LoadResident(b);
                    int sym = caff.Symbols.FindIndex(s => NB.Core.Formats.AssetIds.DisplayName(s) == args[3]) + 1;
                    if (sym == 0) { Console.WriteLine("model not found in bundle"); return 1; }
                    var meshes = NB.Core.Models.ObjReader.ReadAny(args[4]);
                    Console.WriteLine($"OBJ: {meshes.Count} mesh(es): " + string.Join(", ", meshes.Select(x => $"'{x.Name}' {x.Positions.Count}v/{x.Triangles.Count / 3}t")));
                    var allP = meshes.SelectMany(x => x.Positions).ToList();
                    if (allP.Count > 0) Console.WriteLine($"input bounds: {allP.Aggregate(System.Numerics.Vector3.Min)} .. {allP.Aggregate(System.Numerics.Vector3.Max)}");
                    if (args.Contains("--materials"))
                    {
                        // [--materials [--max-tex N] [--atlas N] [--no-neutral] [--png dir]]: import materials + textures (MaterialImport)
                        var mo = new NB.Core.Models.MaterialImport.Options();
                        int ai = Array.IndexOf(args, "--max-tex"); if (ai > 0) mo.MaxTextureSize = int.Parse(args[ai + 1]);
                        ai = Array.IndexOf(args, "--atlas"); if (ai > 0) mo.AtlasSize = int.Parse(args[ai + 1]);
                        mo.NeutraliseDetail = !args.Contains("--no-neutral");
                        Console.Write(NB.Core.Models.MaterialImport.MakePlan(caff, sym, meshes, mo));
                        var mr = NB.Core.Models.MaterialImport.Apply(caff, sym, meshes, mo);
                        foreach (var n in mr.Notes.Concat(mr.Geometry.Notes)) Console.WriteLine("  " + n);
                        // verify: re-read; every draw of the model decodes, and every created texture decodes at full size
                        var mback = NB.Core.Formats.CaffFile.Read(caff.Write());
                        int s2 = mback.Symbols.FindIndex(s => NB.Core.Formats.AssetIds.DisplayName(s) == args[3]) + 1;
                        var mm2 = NB.Core.Models.ModelAsset.Parse(mback, s2);
                        foreach (var dr in mm2.Draws.Where(x => x.Indices.Length > 3))
                            Console.WriteLine($"  draw vb{dr.VbRecord}: {dr.Indices.Length / 3} triangles, {dr.Positions.Length} vertices; {string.Join(" | ", dr.Textures.Select(t => $"s{t.Slot}:{t.Texture.Replace("aid_texture_banjox_", "")}"))}");
                        int pi = Array.IndexOf(args, "--png");
                        foreach (var tn in mr.Textures)
                        {
                            int ts = mback.Symbols.FindIndex(s => NB.Core.Formats.AssetIds.DisplayName(s) == tn) + 1;
                            var cpu = mback.PartsOf(ts).First(p => mback.SectionOf(p).Name == ".data"); var gpu = mback.PartsOf(ts).First(p => mback.SectionOf(p).Name == ".texturegpu");
                            var ta = new NB.Core.Textures.TextureAsset(cpu.Data, gpu.Data);
                            var (px, tw, th) = ta.Decode(0);
                            Console.WriteLine($"  texture {tn}: {tw}x{th}, {ta.Levels.Count} levels, decodes");
                            if (pi > 0) NB.Core.Textures.ImageIO.Save(Path.Combine(args[pi + 1], tn + ".png"), px, tw, th);
                        }
                        Console.WriteLine($"verify: warnings: {string.Join("; ", mm2.Warnings.DefaultIfEmpty("none"))}");
                        if (!args.Contains("--dry")) { ws.SaveResident(b, caff, $"imported {Path.GetFileName(args[4])} with materials into {args[3]}"); Console.WriteLine("saved"); }
                        return 0;
                    }
                    var res = NB.Core.Models.ModelImporter.Replace(caff, sym, meshes);
                    foreach (var n in res.Notes) Console.WriteLine("  " + n);
                    // verify: write, re-read and decode; every imported vertex must come back (half-float precision)
                    var back = NB.Core.Formats.CaffFile.Read(caff.Write());
                    var m2 = NB.Core.Models.ModelAsset.Parse(back, sym);
                    // every draw's triangles must decode to the positions of the mesh assigned to it
                    bool perDraw = meshes.Count == m2.Draws.Count;
                    var vbOrder = m2.Draws.Select(x => x.VbRecord).Distinct().ToList();
                    int checkedT = 0; double worst = 0;
                    for (int k = 0; k < m2.Draws.Count; k++)
                    {
                        var dr = m2.Draws[k];
                        int mi = perDraw ? k : vbOrder.IndexOf(dr.VbRecord);
                        if (mi >= meshes.Count) continue;
                        var src = meshes[mi];
                        if (dr.Indices.Length != src.Triangles.Count) throw new InvalidDataException($"draw {k}: {dr.Indices.Length} indices decoded, expected {src.Triangles.Count}");
                        for (int t = 0; t < src.Triangles.Count; t++)
                            worst = Math.Max(worst, System.Numerics.Vector3.Distance(dr.Positions[dr.Indices[t]], src.Positions[src.Triangles[t]]));
                        checkedT += src.Triangles.Count / 3;
                    }
                    Console.WriteLine($"verify: {checkedT} triangles decode back, worst position error {worst:G3}; warnings: {string.Join("; ", m2.Warnings.DefaultIfEmpty("none"))}");
                    if (!args.Contains("--dry")) { ws.SaveResident(b, caff, $"imported {Path.GetFileName(args[4])} into {args[3]}"); Console.WriteLine("saved"); }
                    return 0;
                }
                case "vehicles":
                {
                    // vehicles <workspace> [name]: parse every aid_vehicle asset; check block part ids; grid statistics
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    var idx = NB.Core.Project.AssetIndex.LoadOrBuild(ws);
                    var names = idx.Entries.Where(e => e.Id != 0).GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First().Name);
                    int ok = 0, bad = 0, blocks = 0, unresolved = 0, maxBlocks = 0; int[] maxG = new int[3];
                    var seen = new HashSet<string>();
                    foreach (var g in idx.Entries.Where(e => e.Type == "vehicle" && e.Symbol > 0 && !e.Streamed).GroupBy(e => e.Bundle))
                    {
                        var caff = ws.LoadResident(g.Key);
                        foreach (var e in g)
                        {
                            if (!seen.Add(e.Name)) continue;
                            var d = new NB.Core.Formats.AssetView(caff, e.Symbol).Data(".data");
                            var v = NB.Core.Tags.VehicleAsset.TryParse(d);
                            if (v == null || NB.Core.Tags.VehicleAsset.HeaderSize + v.Count * NB.Core.Tags.VehicleAsset.BlockSize != d.Length) { bad++; Console.WriteLine($"size mismatch: {e.Name} ({d.Length} bytes, count {v?.Count})"); continue; }
                            ok++; blocks += v.Count; maxBlocks = Math.Max(maxBlocks, v.Count);
                            foreach (var b in v.Blocks)
                            {
                                if (!names.TryGetValue(b.Part, out var pn) || !pn.StartsWith("aid_objparams_banjox_vehicleblock")) unresolved++;
                                maxG[0] = Math.Max(maxG[0], b.X); maxG[1] = Math.Max(maxG[1], b.Y); maxG[2] = Math.Max(maxG[2], b.Z);
                            }
                            if (args.Length > 2 && e.Name.Contains(args[2]))
                            {
                                Console.WriteLine($"{e.Name}: {v.Count} blocks, name \"{v.Name}\", flags 0x{v.Flags:X4}");
                                foreach (var b in v.Blocks) Console.WriteLine($"  ({b.X,2},{b.Y,2},{b.Z,2}) cat {b.Category,2} {names.GetValueOrDefault(b.Part, $"0x{b.Part:X8}")?.Replace("aid_objparams_banjox_vehicleblock_", "")} rot {b.Rotation} paint {b.Paint:X8} buttons {b.Buttons:X}");
                            }
                        }
                    }
                    Console.WriteLine($"vehicles parsed {ok}, size mismatches {bad}; blocks {blocks} (max {maxBlocks} per vehicle); part ids not resolving to vehicleblock objparams: {unresolved}; max grid x/y/z {maxG[0]}/{maxG[1]}/{maxG[2]}");
                    return bad == 0 ? 0 : 3;
                }
                case "asset-clone":
                {
                    // asset-clone <workspace> <bundle hex> <source asset> <new asset name>: copy a resident asset under a new name
                    // (new id from the name; inserted before the manifest and registered in it; relocations duplicated)
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    uint b = Convert.ToUInt32(args[2], 16);
                    var caff = ws.LoadResident(b);
                    int src = caff.Symbols.FindIndex(s => NB.Core.Formats.AssetIds.DisplayName(s) == args[3]) + 1;
                    if (src == 0) throw new InvalidDataException($"{args[3]} is not in bundle {b:x6}");
                    int ns = NB.Core.Formats.CaffEdit.CloneAsset(caff, src, args[4]);
                    ws.SaveResident(b, caff, $"cloned {args[3]} as {args[4]}");
                    Console.WriteLine($"{b:x6}: {args[4]} = symbol {ns}, id 0x{NB.Core.Formats.AssetIds.IdOf(caff.Symbols[ns - 1]):X8}");
                    return 0;
                }
                case "obj-dump":
                {
                    // obj-dump <workspace> <asset> [asset…] [--all]: objparams records side by side, field by field (kinds and names
                    // from the inferred schema). Only fields that differ between the assets unless --all.
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    var idx = NB.Core.Project.AssetIndex.LoadOrBuild(ws);
                    var db = NB.Core.Tags.ObjParamsSchema.LoadOrBuild(ws, idx);
                    var names = args.Skip(2).Where(a => !a.StartsWith("--")).Select(a => a.StartsWith("aid_") ? a : "aid_objparams_banjox_vehicleblock_" + a).ToList();
                    var datas = new List<byte[]>();
                    foreach (var n in names)
                    {
                        var e = idx.Entries.First(x => x.Name == n && !x.Streamed);
                        var caff = ws.LoadResident(e.Bundle);
                        datas.Add(caff.PartsOf(e.Symbol).First(p => caff.SectionOf(p).Name == ".data").Data);
                    }
                    var cls = NB.Core.Tags.ObjParamsSchema.ClassOf(datas[0]);
                    Console.WriteLine($"class {cls}; sizes {string.Join(", ", datas.Select(d => d.Length))}");
                    db.TryGetValue(cls, out var sch);
                    var idNames = idx.Entries.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().Name);
                    int len = datas.Max(d => d.Length);
                    Console.WriteLine("  off   kind      " + string.Join(" | ", names.Select(n => n.Replace("aid_objparams_banjox_vehicleblock_", ""))));
                    for (int o = 0; o < len; o += 4)
                    {
                        var f = sch?.FieldAt(o);
                        if (f != null && f.Offset != o) continue;
                        string kind = f?.Kind ?? "hex";
                        string V(byte[] d)
                        {
                            if (o + 4 > d.Length) return "-";
                            if (kind == "str") return "\"" + NB.Core.IO.BE.CStr(d, o, Math.Min(64, d.Length - o)) + "\"";
                            uint u = NB.Core.IO.BE.U32(d, o);
                            if (kind == "float") return NB.Core.IO.BE.F32(d, o).ToString("G6");
                            if (kind == "int") return ((int)u).ToString();
                            if (kind == "u16") return $"{u >> 16},{u & 0xFFFF}";
                            if (kind == "assetref") return idNames.TryGetValue(u, out var nm) ? nm.Replace("aid_", "") : $"0x{u:X8}";
                            return $"0x{u:X8}";
                        }
                        var vals = datas.Select(V).ToList();
                        if (!args.Contains("--all") && vals.Distinct().Count() == 1) continue;
                        Console.WriteLine($"  {o:X4}  {kind,-8}  {string.Join(" | ", vals)}{(f?.Name != null ? "   (" + f.Name + ")" : "")}");
                    }
                    return 0;
                }
                case "obj-schema":
                {
                    // obj-schema <workspace> [class] [--rebuild]: infer objparams layouts (cached) and print one class
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    var idx = NB.Core.Project.AssetIndex.LoadOrBuild(ws);
                    var sw = Stopwatch.StartNew();
                    var db = NB.Core.Tags.ObjParamsSchema.LoadOrBuild(ws, idx, null, args.Contains("--rebuild"));
                    Console.WriteLine($"{db.Count} classes, {db.Values.Sum(c => c.Instances)} assets, {db.Values.Sum(c => c.Fields.Count)} fields ({sw.Elapsed.TotalSeconds:F1}s)");
                    Console.WriteLine("field kinds: " + string.Join(", ", db.Values.SelectMany(c => c.Fields).GroupBy(f => f.Kind).Select(g => $"{g.Key}={g.Count()}")));
                    if (args.Length > 2 && !args[2].StartsWith("--") && db.TryGetValue(args[2], out var cs))
                    {
                        Console.WriteLine($"{cs.Class}: {cs.Size} bytes, {cs.Instances} instances");
                        var names = idx.Entries.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First().Name);
                        foreach (var f in cs.Fields)
                            Console.WriteLine($"  {f.Offset:X4} {f.Kind,-8} {(f.Varies ? "*" : " ")} {(f.Kind == "str" ? string.Join(" | ", f.Samples.Take(4)) : f.Kind == "assetref" ? string.Join(", ", f.Samples.Take(3).Select(v => names.GetValueOrDefault(Convert.ToUInt32(v, 16), v))) : $"[{f.Min:G5} .. {f.Max:G5}] {string.Join(",", f.Samples.Take(4))}")} {f.Name}");
                    }
                    return 0;
                }
                case "obj-set":
                {
                    // obj-set <workspace> <asset name> <offset hex> <f:float | u:uint | h:hex32 | s:string64> — edits .data in every bundle holding the asset
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    var idx = NB.Core.Project.AssetIndex.LoadOrBuild(ws);
                    int off = Convert.ToInt32(args[3], 16);
                    var (kind, val) = (args[4][0], args[4][2..]);
                    var bundles = idx.Entries.Where(e => e.Name == args[2] && e.Symbol > 0 && !e.Streamed).Select(e => e.Bundle).Distinct().ToList();
                    if (bundles.Count == 0) { Console.WriteLine("asset not found"); return 1; }
                    foreach (var b in bundles)
                    {
                        var caff = ws.LoadResident(b);
                        int sym = caff.Symbols.FindIndex(s => NB.Core.Formats.AssetIds.DisplayName(s) == args[2]) + 1;
                        var part = caff.PartsOf(sym).First(p => caff.SectionOf(p).Name == ".data");
                        var d = part.Data;
                        string before = $"{NB.Core.IO.BE.U32(d, off):X8} ({BitConverter.Int32BitsToSingle(NB.Core.IO.BE.S32(d, off)):G6})";
                        switch (kind)
                        {
                            case 'f': NB.Core.IO.BE.W32(d, off, (uint)BitConverter.SingleToInt32Bits(float.Parse(val, System.Globalization.CultureInfo.InvariantCulture))); break;
                            case 'u': NB.Core.IO.BE.W32(d, off, uint.Parse(val)); break;
                            case 'h': NB.Core.IO.BE.W32(d, off, Convert.ToUInt32(val, 16)); break;
                            case 's': Array.Clear(d, off, 64); System.Text.Encoding.ASCII.GetBytes(val).CopyTo(d, off); break;
                            default: throw new ArgumentException("value must be f:, u:, h: or s:");
                        }
                        ws.SaveResident(b, caff, $"{args[2]} +0x{off:X}: {before} -> {args[4]}");
                        Console.WriteLine($"{b:x6} {args[2]} +0x{off:X}: {before} -> {args[4]}");
                    }
                    return 0;
                }
                case "audio-banks":
                {
                    // audio-banks <workspace> [bundle hex]: list wave banks (all bundles when omitted)
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    var bundles = args.Length > 2 ? new[] { Convert.ToUInt32(args[2], 16) }
                        : Directory.GetFiles(Path.Combine(ws.Game.Root, "Bundle", "50")).Select(f => Convert.ToUInt32(Path.GetFileName(f), 16)).ToArray();
                    foreach (var b in bundles)
                        foreach (var bank in NB.Core.Audio.AudioService.ListBanks(ws, b))
                        {
                            var x = NB.Core.Audio.AudioService.LoadBank(ws, b, bank.Id);
                            Console.WriteLine($"{b:x6} {bank.Id:X8} {bank.Name,-40} n={bank.Entries,-4} {(bank.Streaming ? "streaming" : "memory")} {string.Join(",", x.Entries.Select(e => e.CodecName).Distinct())} {x.Entries.Sum(e => e.Seconds):F0}s");
                        }
                    return 0;
                }
                case "audio-replace":
                {
                    // audio-replace <workspace> <bundle hex> <bank id hex> <index|all> <file.wav | tone:Hz>
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    uint b = Convert.ToUInt32(args[2], 16), id = Convert.ToUInt32(args[3], 16);
                    var bank = NB.Core.Audio.AudioService.LoadBank(ws, b, id);
                    var idxs = args[4] == "all" ? Enumerable.Range(0, bank.Entries.Count) : new[] { int.Parse(args[4]) };
                    foreach (int i in idxs)
                    {
                        string wav = args[5];
                        if (wav.StartsWith("tone:"))
                        {
                            double hz = double.Parse(wav[5..], System.Globalization.CultureInfo.InvariantCulture);
                            var e = bank.Entries[i]; int rate = e.SampleRate, ch = e.Channels;
                            int n = (int)(Math.Max(1.0, e.Seconds) * rate);
                            var s = new short[n * ch];
                            for (int k = 0; k < n; k++) for (int c = 0; c < ch; c++) s[k * ch + c] = (short)(12000 * Math.Sin(2 * Math.PI * hz * k / rate));
                            wav = Path.Combine(Path.GetTempPath(), $"nb_tone_{i}.wav");
                            NB.Core.Audio.Wav.Write(wav, s, ch, rate);
                        }
                        Console.WriteLine(NB.Core.Audio.AudioService.ReplaceWithWav(ws, b, id, i, wav));
                    }
                    return 0;
                }
                case "audio-capture":
                {
                    // audio-capture <seconds> <out.wav> [tone Hz]: record the default output device (loopback)
                    var (s, rate, ch) = LoopbackCapture.Record(double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture));
                    LoopbackCapture.SaveWav(args[2], s, rate, ch);
                    double rms = Math.Sqrt(s.Select(v => (double)v * v).DefaultIfEmpty(0).Average());
                    Console.WriteLine($"captured {s.Length / Math.Max(1, ch) / (double)rate:F1}s at {rate} Hz x{ch}, rms {rms:F4}");
                    if (args.Length > 3) Console.WriteLine($"tone {args[3]} Hz energy share {LoopbackCapture.ToneShare(s, rate, ch, double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture)):P1}");
                    return 0;
                }
                case "world-objects":
                {
                    // world-objects <workspace> <bundle hex> [name filter]: list objects with world positions
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    var idx = NB.Core.Project.AssetIndex.LoadOrBuild(ws);
                    uint b = Convert.ToUInt32(args[2], 16);
                    var w = NB.Core.Project.WorldCatalog.FromIndex(idx).First(x => x.Bundle == b);
                    var scene = new NB.Core.World.WorldScene(ws, b, w.BackgroundModel);
                    foreach (var o in scene.Objects.Where(o => args.Length < 4 || o.Name.Contains(args[3], StringComparison.OrdinalIgnoreCase)))
                    {
                        var t = o.Transform.Translation;
                        Console.WriteLine($"{o.Kind,-8} {t.X,9:F1} {t.Y,9:F1} {t.Z,9:F1}  {o.Name}");
                        if (args.Length >= 4 && o.Model != null)
                        {
                            Console.WriteLine($"         model {o.ModelName}");
                            // world-space AABB of the model-space bounds (8 corners through the instance transform)
                            var wmin = new System.Numerics.Vector3(float.MaxValue); var wmax = new System.Numerics.Vector3(float.MinValue);
                            for (int c8 = 0; c8 < 8; c8++)
                            {
                                var p = new System.Numerics.Vector3((c8 & 1) != 0 ? o.BoundsMax.X : o.BoundsMin.X, (c8 & 2) != 0 ? o.BoundsMax.Y : o.BoundsMin.Y, (c8 & 4) != 0 ? o.BoundsMax.Z : o.BoundsMin.Z);
                                var wp = System.Numerics.Vector3.Transform(p, o.Transform);
                                wmin = System.Numerics.Vector3.Min(wmin, wp); wmax = System.Numerics.Vector3.Max(wmax, wp);
                            }
                            Console.WriteLine($"         model bounds {o.BoundsMin} .. {o.BoundsMax}; world AABB {wmin} .. {wmax}");
                            Console.WriteLine($"         matrix row0 ({o.Transform.M11:F3}, {o.Transform.M12:F3}, {o.Transform.M13:F3}) row2 ({o.Transform.M31:F3}, {o.Transform.M32:F3}, {o.Transform.M33:F3})");
                            foreach (var tex in o.Model.Draws.SelectMany(d => d.Textures).Select(x => x.Texture).Distinct())
                                Console.WriteLine($"         tex {tex}");
                        }
                    }
                    return 0;
                }
                case "script":
                {
                    // script <caff> <name prefix>: list commands
                    var c = NB.Core.Formats.CaffFile.Read(File.ReadAllBytes(args[1]));
                    int sym = c.Symbols.FindIndex(s => s.StartsWith(args[2])) + 1;
                    var names = new Dictionary<uint, string>();
                    foreach (var s in c.Symbols) if (NB.Core.Formats.AssetIds.IdOf(s) is uint id) names.TryAdd(id, NB.Core.Formats.AssetIds.DisplayName(s));
                    var sc = NB.Core.World.ScriptAsset.Parse(c.PartsOf(sym).First(p => c.SectionOf(p).Name == ".data").Data);
                    foreach (var cmd in sc.Commands) Console.WriteLine($"{cmd.Offset:X4} {cmd.Describe(i => names.GetValueOrDefault(i))}");
                    return 0;
                }
                case "skip-intro":
                {
                    var ws = NB.Core.Project.Workspace.Open(args[1]);
                    Console.WriteLine(NB.Core.World.TestMode.SkipStartOfGame(ws, !args.Contains("--no-town-intro")));
                    if (args.Contains("--preset-town")) Console.WriteLine(NB.Core.World.TestMode.PresetTownIntro(ws));
                    return 0;
                }
                case "xwb-test":
                {
                    // xwb-test <Bundle/50 file> <bank name> <index> <vgmstream-cli> <outdir>
                    var arch = NB.Core.Formats.BundleArchive.Read(File.ReadAllBytes(args[1]));
                    var ent = arch.Entries.Where(e => e.Kind == "xwb").First(e => NB.Core.Audio.XwbFile.Read(e.Data!).Name == args[2]);
                    var bank = NB.Core.Audio.XwbFile.Read(ent.Data!);
                    int idx = int.Parse(args[3]); string vg = args[4], od = args[5]; Directory.CreateDirectory(od);
                    var e0 = bank.Entries[idx];
                    Console.WriteLine($"{bank.Name}[{idx}] {e0.CodecName} {e0.Channels}ch {e0.SampleRate}Hz {e0.Seconds:F2}s");
                    NB.Core.Audio.AudioService.ExtractWav(bank, ent.Data!, idx, Path.Combine(od, "original.wav"), vg);
                    var (s, ch, rate) = NB.Core.Audio.Wav.Read(Path.Combine(od, "original.wav"));
                    Console.WriteLine($"decoded original: {s.Length / ch} frames, {ch}ch, {rate} Hz");
                    // make a recognisable replacement: 1 s of a 440 Hz tone at 22050 Hz mono
                    var tone = Enumerable.Range(0, 22050).Select(i => (short)(Math.Sin(i * 2 * Math.PI * 440 / 22050) * 12000)).ToArray();
                    NB.Core.Audio.Wav.Write(Path.Combine(od, "tone.wav"), tone, 1, 22050);
                    var (ts, tch, trate) = NB.Core.Audio.Wav.Read(Path.Combine(od, "tone.wav"));
                    bank.Entries[idx].SetPcm16(ts, tch, trate, keepLoop: true);
                    var rebuilt = bank.Write();
                    File.WriteAllBytes(Path.Combine(od, "rebuilt.xwb"), rebuilt);
                    var back = NB.Core.Audio.XwbFile.Read(rebuilt);
                    Console.WriteLine($"rebuilt bank {rebuilt.Length} bytes (was {ent.Data!.Length}); entry now {back.Entries[idx].CodecName} {back.Entries[idx].SampleRate}Hz {back.Entries[idx].Seconds:F2}s; other entries unchanged: {bank.Entries.Where((x, i) => i != idx).Select((x, i) => x.Data.Length).Sum() == back.Entries.Where((x, i) => i != idx).Sum(x => x.Data.Length)}");
                    NB.Core.Audio.AudioService.ExtractWav(back, rebuilt, idx, Path.Combine(od, "roundtrip_vgmstream.wav"), null);
                    var psi = new System.Diagnostics.ProcessStartInfo(Path.GetFullPath(vg), $"-s {idx + 1} -o \"{Path.Combine(od, "via_vgmstream.wav")}\" \"{Path.Combine(od, "rebuilt.xwb")}\"") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                    using (var p = System.Diagnostics.Process.Start(psi)!) { p.StandardOutput.ReadToEnd(); var err = p.StandardError.ReadToEnd(); p.WaitForExit(); Console.WriteLine($"vgmstream on rebuilt bank: exit {p.ExitCode} {err.Trim()}"); }
                    var (vs, vch, vrate) = NB.Core.Audio.Wav.Read(Path.Combine(od, "via_vgmstream.wav"));
                    int mism = 0; for (int i = 0; i < Math.Min(vs.Length, ts.Length); i++) if (vs[i] != ts[i]) mism++;
                    Console.WriteLine($"vgmstream decoded {vs.Length} samples {vch}ch {vrate}Hz; mismatching samples vs tone: {mism}");
                    return 0;
                }
                case "xwb-roundtrip":
                {
                    // xwb-roundtrip <Bundle/50 dir>: parse and rewrite every wave bank
                    int same = 0, diff = 0, entries = 0;
                    foreach (var f in Directory.GetFiles(args[1]))
                        foreach (var e in NB.Core.Formats.BundleArchive.Read(File.ReadAllBytes(f)).Entries.Where(e => e.Kind == "xwb"))
                        {
                            var x = NB.Core.Audio.XwbFile.Read(e.Data!);
                            entries += x.Entries.Count;
                            var o = x.Write();
                            if (o.AsSpan().SequenceEqual(e.Data)) same++;
                            else { diff++; int k = 0; while (k < Math.Min(o.Length, e.Data!.Length) && o[k] == e.Data[k]) k++; if (diff < 6) Console.WriteLine($"{Path.GetFileName(f)} {x.Name}: len {o.Length} vs {e.Data!.Length}, first diff 0x{k:X}"); }
                        }
                    Console.WriteLine($"identical={same} different={diff} entries={entries}");
                    return diff == 0 ? 0 : 3;
                }
                case "loctext":
                {
                    // loctext <file-or-dir> [--dump]: parse + round-trip every loctext CAFF
                    int same = 0, diff = 0, strings = 0;
                    foreach (var f in Directory.Exists(args[1]) ? Directory.GetFiles(args[1], "*", SearchOption.AllDirectories) : new[] { args[1] })
                    {
                        var c = NB.Core.Formats.CaffFile.Read(File.ReadAllBytes(f));
                        var part = c.Parts.First(p => c.SectionOf(p).Name == ".data");
                        NB.Core.Formats.LocText t;
                        try { t = NB.Core.Formats.LocText.Parse(part.Data); } catch (Exception ex) { diff++; Console.WriteLine($"parse error {f}: {ex.Message}"); continue; }
                        strings += t.Strings.Count;
                        if (!t.Editable) { Console.WriteLine($"read-only (non-standard prefix): {f} ({t.Strings.Count} strings)"); continue; }
                        if (t.Write().AsSpan().SequenceEqual(part.Data)) same++; else { diff++; Console.WriteLine("differs: " + f); }
                        if (args.Contains("--dump")) foreach (var (k, s) in t.Strings) Console.WriteLine($"{k:X4} [{t.Names.GetValueOrDefault(k, "")}] {s}");
                    }
                    Console.WriteLine($"identical={same} different={diff} strings={strings}");
                    return diff == 0 ? 0 : 3;
                }
                case "tex-roundtrip":
                {
                    // tex-roundtrip <caff>: untile→retile every level (must be byte-identical) and measure encoder PSNR
                    var c = NB.Core.Formats.CaffFile.Read(File.ReadAllBytes(args[1]));
                    int same = 0, diff = 0, n = 0; double psnrSum = 0; int psnrN = 0;
                    for (int s = 1; s <= c.Symbols.Count; s++)
                    {
                        var parts = c.PartsOf(s).ToList();
                        var cpu = parts.FirstOrDefault(p => c.SectionOf(p).Name == ".data");
                        var gpu = parts.FirstOrDefault(p => c.SectionOf(p).Name == ".texturegpu");
                        if (cpu == null || gpu == null || !NB.Core.Textures.TextureHeader.IsTexture(cpu.Data)) continue;
                        var t = new NB.Core.Textures.TextureAsset(cpu.Data, gpu.Data);
                        if (t.Header.Kind != 0) continue;
                        var h = t.Header; var blob = new byte[gpu.Data.Length];
                        try { foreach (var l in t.Levels) NB.Core.Textures.XenosTexture.WriteLevelBlocks(new byte[gpu.Data.Length], l, h.Format, 0, h.Tiled, NB.Core.Textures.XenosTexture.ReadLevelBlocks(gpu.Data, l, h.Format, 0, h.Tiled)); }
                        catch (Exception ex) { Console.WriteLine($"  {c.Symbols[s - 1]}: {h} {string.Join(" ", t.Levels.Select(q => $"L{q.Level}@{q.OffsetBytes:X}p{q.PitchBlocks}h{q.HeightBlocks}+{q.PackedX},{q.PackedY}"))} blob 0x{gpu.Data.Length:X}: {ex.Message}"); diff++; continue; }
                        foreach (var l in t.Levels)
                            NB.Core.Textures.XenosTexture.WriteLevelBlocks(blob, l, h.Format, h.Endian, h.Tiled, NB.Core.Textures.XenosTexture.ReadLevelBlocks(gpu.Data, l, h.Format, h.Endian, h.Tiled));
                        // compare only bytes the layout covers (padding in the original may hold anything)
                        var covered = new byte[gpu.Data.Length];
                        foreach (var l in t.Levels)
                            NB.Core.Textures.XenosTexture.WriteLevelBlocks(covered, l, h.Format, 0, h.Tiled, Enumerable.Repeat((byte)0xFF, NB.Core.Textures.XenosTexture.ReadLevelBlocks(gpu.Data, l, h.Format, 0, h.Tiled).Length).ToArray());
                        bool ok = true;
                        for (int i = 0; i < blob.Length; i++) if (covered[i] != 0 && blob[i] != gpu.Data[i]) { ok = false; break; }
                        if (ok) same++; else diff++;
                        if (n++ < 40 && h.Format is NB.Core.Textures.XenosFormat.DXT1 or NB.Core.Textures.XenosFormat.DXT4_5 or NB.Core.Textures.XenosFormat.DXN or NB.Core.Textures.XenosFormat.DXT2_3 or NB.Core.Textures.XenosFormat.CTX1)
                        {
                            var (rgba, w, hh) = t.Decode(0);
                            var enc = NB.Core.Textures.BlockCodec.Encode(rgba, w, hh, h.Format);
                            var dec = NB.Core.Textures.BlockCodec.Decode(enc, w, hh, h.Format);
                            double se = 0; for (int i = 0; i < rgba.Length; i++) if (i % 4 != 3) { double e = rgba[i] - dec[i]; se += e * e; }
                            double mse = se / (w * hh * 3); double psnr = mse == 0 ? 99 : 10 * Math.Log10(255 * 255 / mse);
                            psnrSum += psnr; psnrN++;
                        }
                    }
                    Console.WriteLine($"retile identical={same} different={diff}; re-encode mean PSNR vs decoded original {psnrSum / Math.Max(1, psnrN):F1} dB over {psnrN} textures");
                    return diff == 0 ? 0 : 3;
                }
                case "tex-check":
                {
                    // tex-check <dir of decompressed CAFFs>: report every texture whose layout size differs from its blob
                    int total = 0, bad = 0; var kinds = new Dictionary<string, int>();
                    foreach (var f in Directory.GetFiles(args[1]))
                    {
                        var c = NB.Core.Formats.CaffFile.Read(File.ReadAllBytes(f));
                        for (int s = 1; s <= c.Symbols.Count; s++)
                        {
                            var parts = c.PartsOf(s).ToList();
                            var cpu = parts.FirstOrDefault(p => c.SectionOf(p).Name == ".data");
                            var gpu = parts.FirstOrDefault(p => c.SectionOf(p).Name == ".texturegpu");
                            if (cpu == null || gpu == null || !NB.Core.Textures.TextureHeader.IsTexture(cpu.Data)) continue;
                            total++;
                            string key;
                            try
                            {
                                var t = new NB.Core.Textures.TextureAsset(cpu.Data, gpu.Data);
                                if (t.ExpectedGpuSize == gpu.Data.Length) continue;
                                key = $"{t.Header} hdr={cpu.Data.Length} exp=0x{t.ExpectedGpuSize:X} got=0x{gpu.Data.Length:X}";
                            }
                            catch (Exception e) { key = "exception " + e.Message; }
                            bad++;
                            if (kinds.TryAdd(key, 1)) Console.WriteLine($"{Path.GetFileName(f)} {NB.Core.Formats.AssetIds.DisplayName(c.Symbols[s - 1])}: {key}");
                            else kinds[key]++;
                        }
                    }
                    Console.WriteLine($"{bad} of {total} textures mismatch");
                    return 0;
                }
                default: Usage(); return 1;
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            Console.Error.WriteLine(e.StackTrace);
            return 2;
        }
    }

    static void Usage()
    {
        Console.WriteLine("nbcli xdecomp <in> [out]   decompress an 0x0FF512ED file");
    }
}
