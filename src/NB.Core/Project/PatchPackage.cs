using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using NB.Core.Compression;
using NB.Core.Formats;

namespace NB.Core.Project;

/// <summary>
/// Distributable differential patch (.nbpatch, a zip): binary deltas of every modified game file against the user's own
/// original files, so the package carries no game data of its own — only the bytes that are new.
///
/// Deltas are computed against the *expanded* original (xcompress bundles decompressed, xcompress entries inside stream
/// archives decompressed and appended), because a modified bundle is written back uncompressed and a delta against the
/// compressed file would have to carry the whole bundle. The patcher rebuilds the same expanded source from the
/// original file, so it must match the recorded SHA-256 exactly (validation). Applying keeps a backup of every file it
/// replaces (rollback), and checks the SHA-256 of every result.
///
/// Delta stream: "NBDL", u32 version 1, u64 target length, then ops until the end:
///   0x01 COPY  varint source offset, varint length
///   0x02 ADD   varint length, bytes
/// </summary>
public static class PatchPackage
{
    public const string ManifestName = "patch.json";
    public const string BackupDirName = ".nbpatch-backup";

    public sealed class PatchManifest
    {
        /// <summary>1 = original format; 2 adds <see cref="Id"/>, <see cref="Multiplayer"/>, <see cref="Requires"/> and <see cref="Conflicts"/>
        /// (older readers ignore them).</summary>
        public int Format { get; set; } = 1;
        /// <summary>Stable mod id ("ultra", "showdown-town-coop"); empty in format 1 = derived from the name (<see cref="IdOf"/>).</summary>
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Version { get; set; } = "1";
        public string Author { get; set; } = "";
        public string Description { get; set; } = "";
        public string Game { get; set; } = "Banjo-Kazooie: Nuts & Bolts (4D5307ED)";
        public DateTime Created { get; set; }
        public List<PatchFile> Files { get; set; } = new();
        public List<PatchExeMod> ExeMods { get; set; } = new();
        /// <summary>Hex XXH3 module hash for the Xenia patch file (retail executable).</summary>
        public string XeniaModuleHash { get; set; } = "";
        /// <summary>Extra settings for tools that use the patch, e.g. NB Multiplayer co-op editions:
        /// "mode" = "coop", "puppetBlueprint" = hex blueprint id of the puppet vehicles, "parkSpot" = "x,y,z".</summary>
        public Dictionary<string, string> Extra { get; set; } = new();
        /// <summary>How the mod affects online play: "cosmetic" (looks/sounds only), "world" (levels, physics, parts: every
        /// player must have it) or "coop" (adds what Showdown Town co-op needs). Empty = not stated (treated as "world").</summary>
        public string Multiplayer { get; set; } = "";
        /// <summary>Ids of mods that must come before this one in an edition.</summary>
        public List<string> Requires { get; set; } = new();
        /// <summary>Ids of mods this one cannot be combined with.</summary>
        public List<string> Conflicts { get; set; } = new();
        /// <summary>What kind of mod it is (<see cref="ModCategories"/>: map, parts, gameplay, visual, audio, tweak, coop);
        /// empty = guessed from the files it changes.</summary>
        public string Category { get; set; } = "";
        /// <summary>Free-form labels shown on the mod ("Showdown Town", "AI vehicles", ...).</summary>
        public List<string> Tags { get; set; } = new();
        /// <summary>
        /// World edits replayed onto the game after the file differences of every mod of an edition (<see cref="WorldOps"/>),
        /// so the mod combines with mods that change the same files. Format 3.
        /// </summary>
        public List<List<string>> Ops { get; set; } = new();
    }

    /// <summary>Newest manifest format this code understands (3 = <see cref="PatchManifest.Ops"/>).</summary>
    public const int CurrentFormat = 3;

    /// <summary>The mod id: <see cref="PatchManifest.Id"/>, or for older patches a slug of the name ("Showdown Town Co-op" -> "showdown-town-co-op").</summary>
    public static string IdOf(PatchManifest m) => m.Id.Length > 0 ? m.Id : Slug(m.Name);

    public static string Slug(string name)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in name.ToLowerInvariant())
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        return sb.ToString().Trim('-');
    }

    public sealed class PatchFile
    {
        public string Path { get; set; } = "";
        /// <summary>"delta" (modified original file), "new" (file that is not in the original game) or "xexmods" (default.xex
        /// with the patch's executable mods written into the user's own executable at apply time; no bytes are stored).</summary>
        public string Kind { get; set; } = "delta";
        public string SourceSha256 { get; set; } = "";
        public long SourceSize { get; set; }
        public string TargetSha256 { get; set; } = "";
        public long TargetSize { get; set; }
        public string Entry { get; set; } = "";
        public long CopiedBytes { get; set; }
        public long LiteralBytes { get; set; }
    }

    public sealed class PatchExeMod
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public List<uint[]> Words { get; set; } = new();   // [address, original, patched]
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // ------------------------------------------------------------------ building

    /// <summary>
    /// Builds a patch from a workspace: every file that differs from the original becomes a delta (or a new file).
    /// Executable mods enabled in the workspace are recorded and written as a Xenia patch file when applied.
    /// </summary>
    public static PatchManifest Build(Workspace ws, string outPath, string name, string author, string description,
        bool includeExeMods = true, IProgress<(string File, double Fraction)>? progress = null, Dictionary<string, string>? extra = null,
        Action<PatchManifest>? configure = null)
    {
        var man = new PatchManifest { Format = 2, Id = Slug(name), Name = name, Author = author, Description = description, Created = DateTime.Now, Extra = extra ?? new() };
        if (man.Extra.GetValueOrDefault("mode") == "coop") man.Multiplayer = "coop";
        var files = ws.ModifiedFiles();
        if (File.Exists(outPath)) File.Delete(outPath);
        using (var zip = ZipFile.Open(outPath, ZipArchiveMode.Create))
        {
            AddFiles(zip, man, files, ws.Game.Root, ws.Original.Root, progress);
            if (includeExeMods)
            {
                foreach (var id in ws.Manifest.ExeMods)
                {
                    var m = Mods.ExePatches.Resolve(id);
                    if (m == null) continue;
                    man.ExeMods.Add(new PatchExeMod { Id = m.Id, Name = m.Name, Words = m.Words.Select(w => new[] { w.Address, w.Original, w.Patched }).ToList() });
                }
                AddBuiltInExeMods(man, ws, name);
                AddExecutable(man, ws.Original.Xex, ws.Original.Root);
            }
            configure?.Invoke(man);
            foreach (var op in man.Ops) if (WorldOps.Problem(op) is { } bad) throw new ArgumentException("op " + string.Join(' ', op) + ": " + bad);
            if (man.Ops.Count > 0) man.Format = Math.Max(man.Format, 3);
            var me = zip.CreateEntry(ManifestName, CompressionLevel.Optimal);
            using (var s = me.Open()) JsonSerializer.Serialize(s, man, Json);
            var readme = zip.CreateEntry("README.txt");
            using (var w = new StreamWriter(readme.Open())) w.Write(Readme(man));
        }
        return man;
    }

    /// <summary>Adds a delta (or new file) per file: <paramref name="gameRoot"/>'s version against <paramref name="origRoot"/>'s.</summary>
    static void AddFiles(ZipArchive zip, PatchManifest man, IReadOnlyList<string> files, string gameRoot, string origRoot, IProgress<(string File, double Fraction)>? progress)
    {
        int k = 0;
        foreach (var rel in files)
        {
            progress?.Report((rel, (double)k / Math.Max(1, files.Count)));
            var tgtBytes = File.ReadAllBytes(System.IO.Path.Combine(gameRoot, rel));
            // a resident bundle someone recompressed: stored uncompressed (the game reads both), so the delta stays small
            if (XCompressFile.IsCompressed(tgtBytes) && rel.Replace('\\', '/').StartsWith("Bundle/4f/", StringComparison.OrdinalIgnoreCase)) tgtBytes = XCompressFile.Decompress(tgtBytes);
            var orig = System.IO.Path.Combine(origRoot, rel);
            var pf = new PatchFile { Path = rel.Replace('\\', '/'), TargetSha256 = Sha(tgtBytes), TargetSize = tgtBytes.Length, Entry = $"d/{k:D4}.bin" };
            byte[] payload;
            if (File.Exists(orig))
            {
                var srcRaw = File.ReadAllBytes(orig);
                pf.SourceSha256 = Sha(srcRaw); pf.SourceSize = srcRaw.Length;
                var src = Expand(srcRaw);
                payload = Delta.Encode(src, tgtBytes, out long copied, out long lit);
                pf.CopiedBytes = copied; pf.LiteralBytes = lit;
                // self-check: the delta must reproduce the target exactly
                if (!Delta.Apply(src, payload).AsSpan().SequenceEqual(tgtBytes)) throw new InvalidDataException("delta self-check failed for " + rel);
            }
            else
            {
                pf.Kind = "new"; payload = tgtBytes; pf.LiteralBytes = tgtBytes.Length;
            }
            var e = zip.CreateEntry(pf.Entry, CompressionLevel.SmallestSize);
            using (var s = e.Open()) s.Write(payload);
            man.Files.Add(pf);
            k++;
        }
    }

    /// <summary>
    /// Executable mods already built into the workspace's own default.xex (a map mod applied to the workspace bakes its
    /// mods, e.g. world-bounds-2048 and no-escape-reset): they become executable mods of the patch too, so they are neither
    /// lost when the ticked mods replace the executable's difference nor written twice. Words that are not a known mod join
    /// as one mod of this patch (only when the patch has executable mods; otherwise the executable stays a plain delta).
    /// </summary>
    static void AddBuiltInExeMods(PatchManifest man, Workspace ws, string name)
    {
        try
        {
            if (!File.Exists(ws.Game.Xex) || !File.Exists(ws.Original.Xex)) return;
            if (File.ReadAllBytes(ws.Game.Xex).AsSpan().SequenceEqual(File.ReadAllBytes(ws.Original.Xex))) return;
            var ch = GameDiff.ExeDiff(ws.Original.Xex, ws.Game.Xex);
            if (ch.Problem != null) return;
            foreach (var k in ch.Known.Where(k => !man.ExeMods.Any(e => e.Id == k.Id)))
                man.ExeMods.Add(new PatchExeMod { Id = k.Id, Name = k.Name, Words = k.Words.Select(w => new[] { w.Address, w.Original, w.Patched }).ToList() });
            var taken = man.ExeMods.SelectMany(e => e.Words.Select(w => w[0])).ToHashSet();
            var other = ch.Other.Where(w => !taken.Contains(w.Address)).ToList();
            if (other.Count > 0 && man.ExeMods.Count > 0)
                man.ExeMods.Add(new PatchExeMod { Id = "custom-" + man.Id, Name = $"{name}: executable changes", Words = other.Select(w => new[] { w.Address, w.Original, w.Patched }).ToList() });
        }
        catch (Exception e) when (e is IOException or InvalidDataException) { }
    }

    /// <summary>Checks the manifest's executable mods against the original default.xex and adds its "xexmods" entry.</summary>
    static void AddExecutable(PatchManifest man, string originalXex, string originalRoot)
    {
        if (man.ExeMods.Count == 0) return;
        var xb = File.ReadAllBytes(originalXex);
        var xex = XexFile.Read(xb);
        var img = xex.GetImage();
        foreach (var m in man.ExeMods)
        {
            var probs = Mods.ExePatches.Check(img, xex.ImageBase, ToMod(m));
            if (probs.Count > 0) throw new InvalidDataException($"mod {m.Id} does not fit this executable: {string.Join("; ", probs)}");
        }
        var h = Mods.ExePatches.ResolveXeniaHash(Mods.ExePatches.XeniaModuleHash(xb, img), null);
        man.XeniaModuleHash = h?.ToString("X16") ?? "";
        // the executable mods are written into the user's own default.xex when the patch is applied (console
        // builds and Xenia alike); the patch stores only the words and the checksums of the before/after files
        var baked = Bake(xb, man);
        if (!Bake(xb, man).AsSpan().SequenceEqual(baked)) throw new InvalidDataException("executable baking is not deterministic");
        string xrel = System.IO.Path.GetRelativePath(originalRoot, originalXex).Replace('\\', '/');
        man.Files.RemoveAll(f => f.Path.Equals(xrel, StringComparison.OrdinalIgnoreCase));
        man.Files.Add(new PatchFile { Path = xrel, Kind = "xexmods", SourceSha256 = Sha(xb), SourceSize = xb.Length, TargetSha256 = Sha(baked), TargetSize = baked.Length,
                                      LiteralBytes = 4 * man.ExeMods.Sum(m => m.Words.Count) });
    }

    /// <summary>
    /// Builds a mod from a game folder someone modified by hand (<see cref="GameDiff.Analyze"/>): every changed or new file
    /// becomes a delta against the clean reference copy; executable changes become executable mods (known mods by id, the
    /// other words as one mod of this patch). Category, online behaviour and tags come from the analysis unless
    /// <paramref name="configure"/> changes them.
    /// </summary>
    public static PatchManifest BuildFromFolders(GameDiff.Report rep, string outPath, string name, string author, string description,
        IProgress<(string File, double Fraction)>? progress = null, Action<PatchManifest>? configure = null)
    {
        if (rep.ReferenceDir == null) throw new InvalidOperationException("a clean copy of the game is needed to build the mod");
        var files = rep.Carried.Select(f => f.Path).ToList();
        var notClean = GameDiff.NotRetail(rep.ReferenceDir, files.Append("default.xex"));
        if (notClean.Count > 0) throw new InvalidOperationException("the reference copy is not an unmodified game: " + string.Join(", ", notClean.Take(5)));
        var man = new PatchManifest
        {
            Format = 2, Id = Slug(name), Name = name, Author = author, Description = description, Created = DateTime.Now,
            Category = rep.Category, Multiplayer = rep.Multiplayer, Tags = rep.Tags.ToList(),
        };
        if (File.Exists(outPath)) File.Delete(outPath);
        using (var zip = ZipFile.Open(outPath, ZipArchiveMode.Create))
        {
            AddFiles(zip, man, files, rep.ModDir, rep.ReferenceDir, progress);
            if (rep.Exe != null && rep.Exe.Problem == null)
            {
                foreach (var m in rep.Exe.Known)
                    man.ExeMods.Add(new PatchExeMod { Id = m.Id, Name = m.Name, Words = m.Words.Select(w => new[] { w.Address, w.Original, w.Patched }).ToList() });
                if (rep.Exe.Other.Count > 0)
                    man.ExeMods.Add(new PatchExeMod { Id = "custom-" + man.Id, Name = $"{name}: executable changes", Words = rep.Exe.Other.Select(w => new[] { w.Address, w.Original, w.Patched }).ToList() });
                AddExecutable(man, System.IO.Path.Combine(rep.ReferenceDir, "default.xex"), rep.ReferenceDir);
            }
            configure?.Invoke(man);
            if (man.Ops.Count > 0) man.Format = Math.Max(man.Format, 3);
            var me = zip.CreateEntry(ManifestName, CompressionLevel.Optimal);
            using (var s = me.Open()) JsonSerializer.Serialize(s, man, Json);
            var readme = zip.CreateEntry("README.txt");
            using (var w = new StreamWriter(readme.Open())) w.Write(Readme(man));
        }
        return man;
    }

    /// <summary>
    /// A mod made of one executable tweak (Tweaks in NB Multiplayer). The file is byte-for-byte the same on every PC with
    /// the retail default.xex (fixed dates, one fixed layout), so players who tick the same tweak have the same mod.
    /// </summary>
    public static PatchManifest BuildTweak(string originalXex, Mods.ExeMod mod, string name, string blurb, string outPath)
    {
        var xb = File.ReadAllBytes(originalXex);
        var fixedTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var man = new PatchManifest
        {
            Format = 2, Id = "tweak-" + Slug(mod.Id), Name = name, Version = "1", Author = "NB Studio", Description = blurb,
            Created = fixedTime, Category = "tweak", Multiplayer = "world",
        };
        man.ExeMods.Add(new PatchExeMod { Id = mod.Id, Name = mod.Name, Words = mod.Words.Select(w => new[] { w.Address, w.Original, w.Patched }).ToList() });
        if (Mods.ExePatches.TweakOps.TryGetValue(mod.Id, out var ops))
        {
            // tweaks with a world edit (e.g. No ceiling: the town collision lid) carry it as ops, replayed after every mod's files
            man.Format = 3;
            man.Ops = ops.Select(o => o.ToList()).ToList();
        }
        var baked = Bake(xb, man);
        man.Files.Add(new PatchFile { Path = "default.xex", Kind = "xexmods", SourceSha256 = Sha(xb), SourceSize = xb.Length, TargetSha256 = Sha(baked),
                                      TargetSize = baked.Length, LiteralBytes = 4 * mod.Words.Count });
        using (var fs = File.Create(outPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry(ManifestName, CompressionLevel.Optimal);
            e.LastWriteTime = fixedTime;
            using (var s = e.Open()) JsonSerializer.Serialize(s, man, Json);
            var r = zip.CreateEntry("README.txt", CompressionLevel.Optimal);
            r.LastWriteTime = fixedTime;
            using (var w = new StreamWriter(r.Open())) w.Write(Readme(man));
        }
        return man;
    }

    static Mods.ExeMod ToMod(PatchExeMod m) =>
        new(m.Id, m.Name, m.Name, "", m.Words.Select(w => new Mods.ExeWord(w[0], w[1], w[2], "")).ToList());

    /// <summary>default.xex with every executable mod of the patch applied (decrypted payload, unsigned: RGH/JTAG or Xenia).</summary>
    public static byte[] Bake(byte[] xexBytes, PatchManifest man)
    {
        var xex = XexFile.Read(xexBytes);
        var img = xex.GetImage();
        foreach (var m in man.ExeMods)
        {
            var probs = Mods.ExePatches.Check(img, xex.ImageBase, ToMod(m));
            // a mod whose words this default.xex already holds is built in already: not an error, nothing to change
            if (probs.Count > 0 && !Mods.ExePatches.IsApplied(img, xex.ImageBase, ToMod(m))) throw new InvalidDataException($"mod {m.Id} does not fit this default.xex: {string.Join("; ", probs)}");
        }
        var baked = xex.WritePatched(man.ExeMods.Where(m => !Mods.ExePatches.IsApplied(img, xex.ImageBase, ToMod(m))).SelectMany(m => m.Words).Select(w => (w[0], w[2])));
        // the console loader checks the page-hash chain and header digest: never hand out a file that fails them (B23)
        if (!XexFile.VerifyHashes(baked, out var why)) throw new InvalidDataException("baked default.xex failed its hash check: " + why);
        return baked;
    }

    static string Readme(PatchManifest m)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{m.Name} (version {m.Version}) by {m.Author}");
        sb.AppendLine(m.Description);
        sb.AppendLine();
        sb.AppendLine("This is a differential patch for Banjo-Kazooie: Nuts & Bolts. It contains no game files: it is applied to");
        sb.AppendLine("YOUR OWN copy of the game (extracted game directory with default.xex and Bundle\\). Apply it to a copy.");
        sb.AppendLine();
        sb.AppendLine("  NB.Cli patch-verify <patch.nbpatch> <game dir>     checks that every file it changes is the expected original");
        sb.AppendLine("  NB.Cli patch-apply  <patch.nbpatch> <game dir>     applies it (backup in .nbpatch-backup)");
        sb.AppendLine("  NB.Cli patch-rollback <game dir>                    restores every file the patch replaced");
        sb.AppendLine("(or Build → Apply Patch… in NB Studio).");
        sb.AppendLine();
        sb.AppendLine($"Files changed: {m.Files.Count}");
        foreach (var f in m.Files) sb.AppendLine($"  {f.Path}  ({f.Kind}, {f.LiteralBytes:N0} new bytes)");
        if (m.Ops.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"World edits replayed onto the game after the files ({m.Ops.Count}):");
            foreach (var op in m.Ops) sb.AppendLine("  " + string.Join(' ', op.Select(x => x.Length > 60 ? x[..57] + "..." : x)));
        }
        if (m.ExeMods.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Executable mods, written into your default.xex when the patch is applied (the result is decrypted and unsigned:");
            sb.AppendLine("it runs on RGH/JTAG consoles and in Xenia, not on a retail console): " + string.Join(", ", m.ExeMods.Select(x => x.Name)));
        }
        return sb.ToString();
    }

    public static PatchManifest ReadManifest(string patchPath)
    {
        using var zip = ZipFile.OpenRead(patchPath);
        using var s = (zip.GetEntry(ManifestName) ?? throw new InvalidDataException("not an .nbpatch (no patch.json)")).Open();
        return JsonSerializer.Deserialize<PatchManifest>(s) ?? throw new InvalidDataException("bad patch.json");
    }

    // ------------------------------------------------------------------ verifying / applying / rolling back

    public sealed record VerifyResult(string Path, string State, string Detail);   // State: ok | applied | missing | mismatch | new | exists

    /// <summary>Checks every file of the patch against a game directory (original? already patched?). Nothing is written.</summary>
    public static List<VerifyResult> Verify(string patchPath, string gameDir) => Verify(ReadManifest(patchPath), gameDir);

    public static List<VerifyResult> Verify(PatchManifest man, string gameDir)
    {
        var res = new List<VerifyResult>();
        foreach (var f in man.Files)
        {
            var p = System.IO.Path.Combine(gameDir, f.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (f.Kind == "new")
            {
                if (!File.Exists(p)) res.Add(new(f.Path, "new", "will be created"));
                else res.Add(new(f.Path, FileSha(p) == f.TargetSha256 ? "applied" : "exists", "file already present"));
                continue;
            }
            if (!File.Exists(p)) { res.Add(new(f.Path, "missing", "file not found")); continue; }
            var sha = FileSha(p);
            if (sha == f.SourceSha256) res.Add(new(f.Path, "ok", "original"));
            else if (sha == f.TargetSha256) res.Add(new(f.Path, "applied", "already patched"));
            else res.Add(new(f.Path, "mismatch", $"SHA-256 {sha[..12]}… is neither the original nor the patched file"));
        }
        return res;
    }

    /// <summary>
    /// Applies a patch: verifies all files first (nothing is written unless every file is the expected original or
    /// already patched), backs up each file it replaces, writes each result through a temp file after checking its
    /// SHA-256, and records the backup for <see cref="Rollback"/>. Returns the number of files written.
    /// </summary>
    /// <param name="withoutExecutable">Leave default.xex alone (the executable mods of several stacked mods are written
    /// together afterwards with <see cref="ApplyExeMods"/>).</param>
    /// <param name="withoutOps">Leave the mod's world edits (<see cref="PatchManifest.Ops"/>) for later: an edition replays
    /// every mod's ops after all file differences (<see cref="ApplyOps"/>).</param>
    /// <param name="skipFiles">Files written by the caller instead (merged changes of several mods).</param>
    public static int Apply(string patchPath, string gameDir, string? xeniaDir = null, Action<string>? log = null, IProgress<(string File, double Fraction)>? progress = null,
        bool withoutExecutable = false, bool withoutOps = false, ISet<string>? skipFiles = null)
    {
        PatchManifest? man = null;
        var lines = new List<string>();
        void L(string t) { lines.Add(t.Trim()); log?.Invoke(t); }
        try
        {
            man = ReadManifest(patchPath);
            if (man.Format > CurrentFormat) throw new InvalidDataException($"'{man.Name}' was made with a newer NB Studio (format {man.Format}); update to apply it");
            if (withoutExecutable) { man.Files.RemoveAll(f => f.Kind == "xexmods"); man.ExeMods.Clear(); }
            if (skipFiles != null) man.Files.RemoveAll(f => skipFiles.Contains(f.Path));
            var ops = man.Ops;
            if (withoutOps) man.Ops = new();
            log?.Invoke($"[{Now}] Applying patch '{man.Name}' {man.Version} ({System.IO.Path.GetFileName(patchPath)}) to {gameDir}");
            int n = ApplyCore(man, patchPath, gameDir, xeniaDir, L, progress);
            if (man.Ops.Count > 0) n += ApplyOps(man, gameDir, L);
            man.Ops = ops;
            AppendLog(gameDir, "APPLY", man, patchPath, "OK", $"{n} file(s) written" + (lines.Count > 0 ? ": " + string.Join("; ", lines) : ""));
            log?.Invoke($"[{Now}] Patch '{man.Name}' applied: {n} file(s) written (logged to {LogFileName})");
            return n;
        }
        catch (Exception e)
        {
            AppendLog(gameDir, "APPLY", man, patchPath, "FAILED", e.Message.Replace('\n', ' '));
            throw;
        }
    }

    /// <summary>Name of the patch history file kept in the game folder (outside the backup, so it survives rollbacks).</summary>
    public const string LogFileName = "nbpatch-log.txt";
    static string Now => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>Appends one line to the game folder's patch history: time, action, patch name and version, file, status, details.</summary>
    public static void AppendLog(string gameDir, string action, PatchManifest? man, string? patchPath, string status, string detail)
    {
        try
        {
            var line = $"{Now}  {action,-8} {status,-6}  patch '{man?.Name ?? "?"}'{(man?.Version is { Length: > 0 } v ? " " + v : "")}" +
                       $"{(patchPath != null ? " (" + System.IO.Path.GetFileName(patchPath) + ")" : "")}  {detail}";
            File.AppendAllText(System.IO.Path.Combine(gameDir, LogFileName), line + Environment.NewLine);
        }
        catch { /* logging must never break patching */ }
    }

    /// <summary>The patch history of a game folder (oldest first), or an empty list.</summary>
    public static List<string> ReadLog(string gameDir)
    {
        var p = System.IO.Path.Combine(gameDir, LogFileName);
        return File.Exists(p) ? File.ReadAllLines(p).ToList() : new();
    }

    /// <summary>
    /// Writes the executable mods of several mods into default.xex in one go (stacked editions). Every mod's own
    /// "xexmods" entry must name the same original executable; words two mods both change must agree (<see cref="ModStack.Problems"/>).
    /// </summary>
    public static int ApplyExeMods(string gameDir, IReadOnlyList<PatchManifest> mods, string label, Action<string>? log = null)
    {
        var withExe = mods.Where(m => m.Files.Any(f => f.Kind == "xexmods")).ToList();
        if (withExe.Count == 0) return 0;
        var xf = withExe[0].Files.First(f => f.Kind == "xexmods");
        if (withExe.Any(m => m.Files.First(f => f.Kind == "xexmods") is var f && (f.SourceSha256 != xf.SourceSha256 || !f.Path.Equals(xf.Path, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("the mods were made for different game executables");
        var combined = new PatchManifest { Name = label, Version = "", ExeMods = withExe.SelectMany(m => m.ExeMods).GroupBy(e => e.Id).Select(g => g.First()).ToList() };
        var p = System.IO.Path.Combine(gameDir, xf.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
        var baked = Bake(File.ReadAllBytes(p), combined);
        combined.Files.Add(new PatchFile { Path = xf.Path, Kind = "xexmods", SourceSha256 = xf.SourceSha256, SourceSize = xf.SourceSize,
                                           TargetSha256 = Sha(baked), TargetSize = baked.Length, LiteralBytes = 4 * combined.ExeMods.Sum(m => m.Words.Count) });
        try
        {
            int n = ApplyCore(combined, null, gameDir, null, log, null);
            AppendLog(gameDir, "APPLY", combined, null, "OK", "executable mods: " + string.Join(", ", combined.ExeMods.Select(m => m.Id)));
            return n;
        }
        catch (Exception e) { AppendLog(gameDir, "APPLY", combined, null, "FAILED", e.Message.Replace('\n', ' ')); throw; }
    }

    static int ApplyCore(PatchManifest man, string? patchPath, string gameDir, string? xeniaDir, Action<string>? log, IProgress<(string File, double Fraction)>? progress)
    {
        var check = Verify(man, gameDir);
        var bad = check.Where(c => c.State is "missing" or "mismatch" or "exists").ToList();
        if (bad.Count > 0) throw new InvalidOperationException("patch cannot be applied:\n" + string.Join("\n", bad.Select(b => $"  {b.Path}: {b.Detail}")));
        var backup = System.IO.Path.Combine(gameDir, BackupDirName);
        Directory.CreateDirectory(backup);
        var state = LoadState(backup);
        int n = 0, k = 0;
        // phase 1: build every result next to its file (<file>.nbtmp) and check its SHA-256 — nothing is replaced yet
        var pending = new List<(PatchFile F, string P)>();
        try
        {
            using var zip = man.Files.Any(f => f.Kind != "xexmods") ? ZipFile.OpenRead(patchPath!) : null;
            foreach (var f in man.Files)
            {
                progress?.Report((f.Path, (double)k++ / man.Files.Count));
                var p = System.IO.Path.Combine(gameDir, f.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
                if (check.First(c => c.Path == f.Path).State == "applied") { log?.Invoke($"  {f.Path}: already patched"); continue; }
                byte[] result;
                if (f.Kind == "xexmods") result = Bake(File.ReadAllBytes(p), man);
                else
                {
                    byte[] payload;
                    using (var s = zip!.GetEntry(f.Entry)!.Open()) { var ms = new MemoryStream(); s.CopyTo(ms); payload = ms.ToArray(); }
                    result = f.Kind == "new" ? payload : Delta.Apply(Expand(File.ReadAllBytes(p)), payload);
                }
                if (result.Length != f.TargetSize || Sha(result) != f.TargetSha256) throw new InvalidDataException($"{f.Path}: result does not match the patch (SHA-256)");
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
                File.WriteAllBytes(p + ".nbtmp", result);
                pending.Add((f, p));
            }
        }
        catch
        {
            foreach (var (_, p) in pending) File.Delete(p + ".nbtmp");
            throw;
        }
        // phase 2: back up and replace; on failure, restore what this run replaced
        var done = new List<(string P, BackupEntry? New)>();
        try
        {
            foreach (var (f, p) in pending)
            {
                BackupEntry? added = null;
                if (!state.Files.Any(b => b.Path == f.Path))
                {
                    added = new BackupEntry { Path = f.Path, Existed = File.Exists(p), Patch = man.Name };
                    if (added.Existed)
                    {
                        added.Backup = $"{state.Files.Count:D4}.bin";
                        File.Copy(p, System.IO.Path.Combine(backup, added.Backup), true);
                        File.SetAttributes(System.IO.Path.Combine(backup, added.Backup), FileAttributes.Normal);
                    }
                    state.Files.Add(added);
                    SaveState(backup, state);
                }
                IO.FileLinks.PrepareReplace(p);   // game copies are often read-only; edition files may be hard links
                File.Move(p + ".nbtmp", p, true);
                done.Add((p, added));
                log?.Invoke(f.Kind == "xexmods" ? $"  {f.Path}: executable mods written ({string.Join(", ", man.ExeMods.Select(m => m.Id))})" : $"  {f.Path}: patched ({f.LiteralBytes:N0} new bytes)");
                n++;
            }
        }
        catch
        {
            foreach (var (_, p) in pending) if (File.Exists(p + ".nbtmp")) File.Delete(p + ".nbtmp");
            foreach (var (p, added) in done)
            {
                if (added == null) continue;
                if (added.Existed) File.Copy(System.IO.Path.Combine(backup, added.Backup), p, true);
                else File.Delete(p);
                state.Files.Remove(added);
            }
            SaveState(backup, state);
            if (state.Files.Count == 0) Directory.Delete(backup, true);
            throw;
        }
        if (man.ExeMods.Count > 0 && !man.Files.Any(f => f.Kind == "xexmods") && xeniaDir != null
            && ulong.TryParse(man.XeniaModuleHash, System.Globalization.NumberStyles.HexNumber, null, out var hash))
        {
            // older patches (exe mods without a baked default.xex): Xenia patch file
            var path = Mods.ExePatches.WriteXeniaPatchFile(xeniaDir, hash, man.ExeMods.Select(ToMod));
            log?.Invoke("  Xenia patch file: " + path);
        }
        state.Applied.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {man.Name} {man.Version}");
        SaveState(backup, state);
        return n;
    }

    /// <summary>
    /// Replays a mod's world edits (<see cref="PatchManifest.Ops"/>) on a game folder. Every file they change is backed up
    /// first (rollback restores it); a mod's ops run once per folder (recorded in the backup state). Returns the number of
    /// files changed.
    /// </summary>
    public static int ApplyOps(PatchManifest man, string gameDir, Action<string>? log = null)
    {
        if (man.Ops.Count == 0) return 0;
        var backup = System.IO.Path.Combine(gameDir, BackupDirName);
        Directory.CreateDirectory(backup);
        var state = LoadState(backup);
        string key = $"{IdOf(man)} {man.Version}";
        if (state.OpsDone.Contains(key)) { log?.Invoke($"  world edits of '{man.Name}' already applied"); return 0; }
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void BeforeWrite(string p)
        {
            var rel = System.IO.Path.GetRelativePath(gameDir, p).Replace(System.IO.Path.DirectorySeparatorChar, '/');
            if (!changed.Add(rel) || state.Files.Any(b => b.Path.Equals(rel, StringComparison.OrdinalIgnoreCase))) return;
            var e = new BackupEntry { Path = rel, Existed = File.Exists(p), Patch = man.Name };
            if (e.Existed)
            {
                e.Backup = $"{state.Files.Count:D4}.bin";
                File.Copy(p, System.IO.Path.Combine(backup, e.Backup), true);
                File.SetAttributes(System.IO.Path.Combine(backup, e.Backup), FileAttributes.Normal);
            }
            state.Files.Add(e);
            SaveState(backup, state);
        }
        // (the workspace writes each file through a temp file and moves it over the old one: a hard link to the player's
        // game loses only its name, never its content or attributes)
        var ws = Workspace.OnFolder(gameDir, p => { BeforeWrite(p); IO.FileLinks.PrepareReplace(p); });
        try
        {
            WorldOps.RunAll(ws, man.Ops, t => log?.Invoke("  " + t));
        }
        finally { ws.DeleteCache(); }
        state.OpsDone.Add(key);
        state.Applied.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {man.Name} {man.Version} (world edits)");
        SaveState(backup, state);
        log?.Invoke($"  world edits of '{man.Name}': {man.Ops.Count} op(s), {changed.Count} file(s) changed");
        return changed.Count;
    }

    /// <summary>Writes one file into a game folder with a backup for <see cref="Rollback"/> (combined changes of several mods).</summary>
    public static void WriteFile(string gameDir, string rel, byte[] data, string label, Action<string>? log = null)
    {
        var backup = System.IO.Path.Combine(gameDir, BackupDirName);
        Directory.CreateDirectory(backup);
        var state = LoadState(backup);
        var p = System.IO.Path.Combine(gameDir, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
        if (!state.Files.Any(b => b.Path.Equals(rel, StringComparison.OrdinalIgnoreCase)))
        {
            var e = new BackupEntry { Path = rel, Existed = File.Exists(p), Patch = label };
            if (e.Existed)
            {
                e.Backup = $"{state.Files.Count:D4}.bin";
                File.Copy(p, System.IO.Path.Combine(backup, e.Backup), true);
                File.SetAttributes(System.IO.Path.Combine(backup, e.Backup), FileAttributes.Normal);
            }
            state.Files.Add(e);
            SaveState(backup, state);
        }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p + ".nbtmp", data);
        IO.FileLinks.PrepareReplace(p);
        File.Move(p + ".nbtmp", p, true);
        log?.Invoke($"  {rel}: combined changes written ({data.Length:N0} bytes)");
    }

    public sealed class BackupState
    {
        public List<BackupEntry> Files { get; set; } = new();
        public List<string> Applied { get; set; } = new();
        /// <summary>Mods ("id version") whose world edits were replayed in this folder.</summary>
        public List<string> OpsDone { get; set; } = new();
    }

    public sealed class BackupEntry
    {
        public string Path { get; set; } = "";
        public bool Existed { get; set; }
        public string Backup { get; set; } = "";
        public string Patch { get; set; } = "";
    }

    static BackupState LoadState(string dir)
    {
        var p = System.IO.Path.Combine(dir, "backup.json");
        return File.Exists(p) ? JsonSerializer.Deserialize<BackupState>(File.ReadAllText(p)) ?? new() : new();
    }

    static void SaveState(string dir, BackupState s) => File.WriteAllText(System.IO.Path.Combine(dir, "backup.json"), JsonSerializer.Serialize(s, Json));

    /// <summary>Restores every file replaced by patches (created files are removed) and deletes the backup. Returns the count.</summary>
    public static int Rollback(string gameDir, Action<string>? log = null)
    {
        var backup = System.IO.Path.Combine(gameDir, BackupDirName);
        if (!Directory.Exists(backup))
        {
            AppendLog(gameDir, "ROLLBACK", null, null, "FAILED", "nothing to roll back (no mod applied)");
            throw new InvalidOperationException("Nothing to roll back: no mod is applied to " + gameDir +
                " (it has no .nbpatch-backup folder), so its game files are the originals. To put a mod on it, use Build > Apply Patch to a Game Directory.");
        }
        var state = LoadState(backup);
        var names = string.Join(", ", state.Files.Select(f => f.Patch).Where(x => x.Length > 0).Distinct());
        log?.Invoke($"[{Now}] Rolling back {(names.Length > 0 ? "patch '" + names + "'" : "patches")} in {gameDir}");
        try { int r = RollbackCore(gameDir, backup, state, log); AppendLog(gameDir, "ROLLBACK", new PatchManifest { Name = names }, null, "OK", $"{r} file(s) restored: {string.Join(", ", state.Files.Select(f => f.Path))}"); return r; }
        catch (Exception e) { AppendLog(gameDir, "ROLLBACK", new PatchManifest { Name = names }, null, "FAILED", e.Message); throw; }
    }

    static int RollbackCore(string gameDir, string backup, BackupState state, Action<string>? log)
    {
        int n = 0;
        foreach (var e in state.Files)
        {
            var p = System.IO.Path.Combine(gameDir, e.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
            IO.FileLinks.PrepareReplace(p);   // (never through a hard link to the player's game)
            if (e.Existed) { File.Copy(System.IO.Path.Combine(backup, e.Backup), p, true); File.SetAttributes(p, FileAttributes.Normal); }
            log?.Invoke("  restored " + e.Path);
            n++;
        }
        Directory.Delete(backup, true);
        return n;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The delta source for an original file: decompressed xcompress bundles; stream archives plus their decompressed xcompress entries.</summary>
    public static byte[] Expand(byte[] raw)
    {
        if (XCompressFile.IsCompressed(raw)) return XCompressFile.Decompress(raw);
        if (BundleArchive.IsArchive(raw))
        {
            var extra = new List<byte[]>();
            try
            {
                foreach (var e in BundleArchive.Read(raw).Entries)
                    if (e.Data != null && XCompressFile.IsCompressed(e.Data)) extra.Add(XCompressFile.Decompress(e.Data));
            }
            catch (InvalidDataException) { }
            if (extra.Count == 0) return raw;
            var ms = new MemoryStream();
            ms.Write(raw);
            foreach (var x in extra) ms.Write(x);
            return ms.ToArray();
        }
        return raw;
    }

    public static string Sha(byte[] d) => Convert.ToHexString(SHA256.HashData(d)).ToLowerInvariant();
    public static string FileSha(string p) { using var s = File.OpenRead(p); return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant(); }
}

/// <summary>Block-matching binary delta (rolling hash over 32-byte windows of the target, source indexed every 8 bytes).</summary>
public static class Delta
{
    const int B = 32, Stride = 8;
    const ulong Prime = 0x100000001B3UL;

    public static byte[] Encode(byte[] src, byte[] tgt, out long copied, out long literal)
    {
        copied = 0; literal = 0;
        int bits = 22; while ((1L << bits) < src.Length / Stride * 2 && bits < 26) bits++;
        int mask = (1 << bits) - 1;
        var table = new int[1 << bits];
        Array.Fill(table, -1);
        ulong pw = 1; for (int i = 0; i < B - 1; i++) pw *= Prime;
        for (int i = 0; i + B <= src.Length; i += Stride)
        {
            ulong h = Hash(src, i);
            int slot = (int)((h ^ (h >> 29)) & (ulong)mask);
            if (table[slot] < 0) table[slot] = i;   // keep the first occurrence
        }
        var outp = new MemoryStream();
        outp.Write("NBDL"u8); WriteU32(outp, 1); WriteU64(outp, (ulong)tgt.Length);
        int pos = 0, lit = 0;
        long lastSrcEnd = -1;
        ulong rh = tgt.Length >= B ? Hash(tgt, 0) : 0;
        void FlushLit(int end, ref long literalRef)
        {
            if (end > lit) { outp.WriteByte(2); WriteVar(outp, (ulong)(end - lit)); outp.Write(tgt, lit, end - lit); literalRef += end - lit; }
        }
        while (pos + B <= tgt.Length)
        {
            int cand = -1;
            // 1) continuation of the previous copy (in-place edits keep offsets)
            if (lastSrcEnd >= 0)
            {
                long c = lastSrcEnd + (pos - lit);
                if (c >= 0 && c + B <= src.Length && Same(src, (int)c, tgt, pos, B)) cand = (int)c;
            }
            if (cand < 0)
            {
                int slot = (int)((rh ^ (rh >> 29)) & (ulong)mask);
                int c = table[slot];
                if (c >= 0 && Same(src, c, tgt, pos, B)) cand = c;
            }
            if (cand >= 0)
            {
                int s = cand, t = pos;
                while (t > lit && s > 0 && src[s - 1] == tgt[t - 1]) { s--; t--; }
                int e = pos + B, se = cand + B;
                while (e < tgt.Length && se < src.Length && src[se] == tgt[e]) { e++; se++; }
                FlushLit(t, ref literal);
                outp.WriteByte(1); WriteVar(outp, (ulong)s); WriteVar(outp, (ulong)(e - t));
                copied += e - t;
                pos = lit = e; lastSrcEnd = se;
                if (pos + B <= tgt.Length) rh = Hash(tgt, pos);
                continue;
            }
            if (pos + B < tgt.Length) rh = (rh - tgt[pos] * pw) * Prime + tgt[pos + B];
            pos++;
        }
        FlushLit(tgt.Length, ref literal);
        return outp.ToArray();
    }

    public static byte[] Apply(byte[] src, byte[] delta)
    {
        if (delta.Length < 16 || delta[0] != 'N' || delta[1] != 'B' || delta[2] != 'D' || delta[3] != 'L') throw new InvalidDataException("not an NB delta");
        int p = 8;
        ulong len = BitConverter.ToUInt64(delta, p); p += 8;
        var outp = new byte[len];
        int o = 0;
        while (p < delta.Length)
        {
            byte op = delta[p++];
            if (op == 1)
            {
                long s = (long)ReadVar(delta, ref p); int n = (int)ReadVar(delta, ref p);
                if (s < 0 || s + n > src.Length || o + n > outp.Length) throw new InvalidDataException("delta copy out of range (wrong source file?)");
                Buffer.BlockCopy(src, (int)s, outp, o, n); o += n;
            }
            else if (op == 2)
            {
                int n = (int)ReadVar(delta, ref p);
                Buffer.BlockCopy(delta, p, outp, o, n); p += n; o += n;
            }
            else throw new InvalidDataException($"bad delta op {op}");
        }
        if (o != outp.Length) throw new InvalidDataException("delta produced a short file");
        return outp;
    }

    static ulong Hash(byte[] d, int o) { ulong h = 0; for (int i = 0; i < B; i++) h = h * Prime + d[o + i]; return h; }
    static bool Same(byte[] a, int ao, byte[] b, int bo, int n) => a.AsSpan(ao, n).SequenceEqual(b.AsSpan(bo, n));
    static void WriteU32(Stream s, uint v) { Span<byte> b = stackalloc byte[4]; BitConverter.TryWriteBytes(b, v); s.Write(b); }
    static void WriteU64(Stream s, ulong v) { Span<byte> b = stackalloc byte[8]; BitConverter.TryWriteBytes(b, v); s.Write(b); }
    static void WriteVar(Stream s, ulong v) { while (v >= 0x80) { s.WriteByte((byte)(v | 0x80)); v >>= 7; } s.WriteByte((byte)v); }
    static ulong ReadVar(byte[] d, ref int p) { ulong v = 0; int sh = 0; while (true) { byte b = d[p++]; v |= (ulong)(b & 0x7F) << sh; if (b < 0x80) return v; sh += 7; } }
}
