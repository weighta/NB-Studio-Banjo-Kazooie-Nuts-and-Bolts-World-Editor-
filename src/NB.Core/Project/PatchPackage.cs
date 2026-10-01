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
        bool includeExeMods = true, IProgress<(string File, double Fraction)>? progress = null, Dictionary<string, string>? extra = null)
    {
        var man = new PatchManifest { Name = name, Author = author, Description = description, Created = DateTime.Now, Extra = extra ?? new() };
        var files = ws.ModifiedFiles();
        if (File.Exists(outPath)) File.Delete(outPath);
        using (var zip = ZipFile.Open(outPath, ZipArchiveMode.Create))
        {
            int k = 0;
            foreach (var rel in files)
            {
                progress?.Report((rel, (double)k / Math.Max(1, files.Count)));
                var tgtBytes = File.ReadAllBytes(System.IO.Path.Combine(ws.Game.Root, rel));
                var orig = System.IO.Path.Combine(ws.Original.Root, rel);
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
            if (includeExeMods)
            {
                foreach (var id in ws.Manifest.ExeMods)
                {
                    var m = Mods.ExePatches.Resolve(id);
                    if (m == null) continue;
                    man.ExeMods.Add(new PatchExeMod { Id = m.Id, Name = m.Name, Words = m.Words.Select(w => new[] { w.Address, w.Original, w.Patched }).ToList() });
                }
                if (man.ExeMods.Count > 0)
                {
                    var xb = File.ReadAllBytes(ws.Original.Xex);
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
                    string xrel = System.IO.Path.GetRelativePath(ws.Original.Root, ws.Original.Xex).Replace('\\', '/');
                    man.Files.RemoveAll(f => f.Path.Equals(xrel, StringComparison.OrdinalIgnoreCase));
                    man.Files.Add(new PatchFile { Path = xrel, Kind = "xexmods", SourceSha256 = Sha(xb), SourceSize = xb.Length, TargetSha256 = Sha(baked), TargetSize = baked.Length,
                                                  LiteralBytes = 4 * man.ExeMods.Sum(m => m.Words.Count) });
                }
            }
            var me = zip.CreateEntry(ManifestName, CompressionLevel.Optimal);
            using (var s = me.Open()) JsonSerializer.Serialize(s, man, Json);
            var readme = zip.CreateEntry("README.txt");
            using (var w = new StreamWriter(readme.Open())) w.Write(Readme(man));
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
            if (probs.Count > 0) throw new InvalidDataException($"mod {m.Id} does not fit this default.xex: {string.Join("; ", probs)}");
        }
        var baked = xex.WritePatched(man.ExeMods.SelectMany(m => m.Words).Select(w => (w[0], w[2])));
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
    public static List<VerifyResult> Verify(string patchPath, string gameDir)
    {
        var man = ReadManifest(patchPath);
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
    public static int Apply(string patchPath, string gameDir, string? xeniaDir = null, Action<string>? log = null, IProgress<(string File, double Fraction)>? progress = null)
    {
        PatchManifest? man = null;
        var lines = new List<string>();
        void L(string t) { lines.Add(t.Trim()); log?.Invoke(t); }
        try
        {
            man = ReadManifest(patchPath);
            log?.Invoke($"[{Now}] Applying patch '{man.Name}' {man.Version} ({System.IO.Path.GetFileName(patchPath)}) to {gameDir}");
            int n = ApplyCore(man, patchPath, gameDir, xeniaDir, L, progress);
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

    static int ApplyCore(PatchManifest man, string patchPath, string gameDir, string? xeniaDir, Action<string>? log, IProgress<(string File, double Fraction)>? progress)
    {
        var check = Verify(patchPath, gameDir);
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
            using var zip = ZipFile.OpenRead(patchPath);
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
                    using (var s = zip.GetEntry(f.Entry)!.Open()) { var ms = new MemoryStream(); s.CopyTo(ms); payload = ms.ToArray(); }
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
                if (File.Exists(p)) File.SetAttributes(p, FileAttributes.Normal);   // game copies are often read-only
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

    public sealed class BackupState
    {
        public List<BackupEntry> Files { get; set; } = new();
        public List<string> Applied { get; set; } = new();
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
            AppendLog(gameDir, "ROLLBACK", null, null, "FAILED", "no patch backup in this folder");
            throw new InvalidOperationException("no patch backup in " + gameDir);
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
            if (File.Exists(p)) File.SetAttributes(p, FileAttributes.Normal);
            if (e.Existed) File.Copy(System.IO.Path.Combine(backup, e.Backup), p, true);
            else if (File.Exists(p)) File.Delete(p);
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
