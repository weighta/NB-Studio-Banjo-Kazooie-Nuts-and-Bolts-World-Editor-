using System.Security.Cryptography;
using System.Text.Json;
using NB.Core.Compression;
using NB.Core.Formats;

namespace NB.Core.Project;

/// <summary>
/// A non-destructive modding workspace: a full copy of the original game directory (<c>game/</c>) that the
/// tools modify, plus a manifest (<c>workspace.json</c>) with the original's location and a change log.
/// The original directory is only ever read.
/// </summary>
public sealed class Workspace
{
    public string Root { get; private set; } = "";
    public GameDirectory Original { get; private set; } = null!;
    public GameDirectory Game { get; private set; } = null!;
    public WorkspaceManifest Manifest { get; private set; } = new();

    public string ManifestPath => Path.Combine(Root, "workspace.json");
    public string CacheDir => Path.Combine(Root, "cache");

    public sealed class WorkspaceManifest
    {
        public string OriginalPath { get; set; } = "";
        /// <summary>Enabled executable (After-Party) mods, applied as a Xenia patch file at launch.</summary>
        public List<string> ExeMods { get; set; } = new();
        public DateTime Created { get; set; }
        public List<ChangeEntry> Changes { get; set; } = new();
    }

    public sealed class ChangeEntry
    {
        public DateTime Time { get; set; }
        public string File { get; set; } = "";
        public string Description { get; set; } = "";
    }

    public static Workspace Open(string root)
    {
        var ws = new Workspace { Root = Path.GetFullPath(root) };
        ws.Manifest = JsonSerializer.Deserialize<WorkspaceManifest>(File.ReadAllText(ws.ManifestPath)) ?? throw new InvalidDataException("bad workspace.json");
        ws.Original = new GameDirectory(ws.Manifest.OriginalPath);
        ws.Game = new GameDirectory(Path.Combine(ws.Root, "game"));
        return ws;
    }

    /// <summary>
    /// A game folder edited in place without a project (mod ops replayed onto an edition): no workspace.json, no history;
    /// decompressed bundles are cached in a temporary folder (<see cref="DeleteCache"/>). <paramref name="beforeWrite"/> runs
    /// before a file is replaced (backups); files are replaced through a temp file, so hard-linked copies of the user's game
    /// are never written through.
    /// </summary>
    public static Workspace OnFolder(string gameDir, Action<string>? beforeWrite = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "NBModTool", "folder-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        var g = new GameDirectory(gameDir);
        return new Workspace { Root = root, Original = g, Game = g, Manifest = new WorkspaceManifest { OriginalPath = g.Root, Created = DateTime.Now }, _folder = true, _beforeWrite = beforeWrite };
    }

    bool _folder;
    Action<string>? _beforeWrite;

    /// <summary>Removes the temporary cache of a <see cref="OnFolder"/> workspace.</summary>
    public void DeleteCache()
    {
        if (_folder) try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Copies the original game into <paramref name="root"/>/game. Existing files are left untouched.</summary>
    public static Workspace Create(string originalDir, string root, IProgress<(string File, double Fraction)>? progress = null)
    {
        var orig = new GameDirectory(originalDir);
        var rep = orig.Validate();
        if (!rep.Ok) throw new InvalidOperationException("Original game directory is invalid:\n" + string.Join("\n", rep.Errors));
        root = Path.GetFullPath(root);
        if (Path.GetFullPath(originalDir).TrimEnd('\\').Equals(Path.Combine(root, "game"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Workspace must not be the original directory.");
        var gameDir = Path.Combine(root, "game");
        var files = Directory.GetFiles(orig.Root, "*", SearchOption.AllDirectories);
        long total = files.Sum(f => new FileInfo(f).Length), done = 0;
        foreach (var f in files)
        {
            var rel = Path.GetRelativePath(orig.Root, f);
            var dst = Path.Combine(gameDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            if (!File.Exists(dst) || new FileInfo(dst).Length != new FileInfo(f).Length)
            {
                File.Copy(f, dst, true);
                File.SetAttributes(dst, FileAttributes.Normal);
            }
            done += new FileInfo(f).Length;
            progress?.Report((rel, (double)done / total));
        }
        var ws = new Workspace
        {
            Root = root, Original = orig, Game = new GameDirectory(gameDir),
            Manifest = new WorkspaceManifest { OriginalPath = orig.Root, Created = DateTime.Now },
        };
        Directory.CreateDirectory(ws.CacheDir);
        ws.SaveManifest();
        return ws;
    }

    public void SaveManifest() { if (!_folder) File.WriteAllText(ManifestPath, JsonSerializer.Serialize(Manifest, new JsonSerializerOptions { WriteIndented = true })); }

    // ------------------------------------------------------------ bundles

    readonly Dictionary<uint, CaffFile> _residentCache = new();

    /// <summary>Loads the working copy's resident bundle (decompressing through a disk cache of the original).</summary>
    public CaffFile LoadResident(uint bundle)
    {
        bundle &= 0xFFFFFF;
        if (_residentCache.TryGetValue(bundle, out var c)) return c;
        var path = Game.ResidentPath(bundle);
        byte[] raw = File.ReadAllBytes(path);
        if (XCompressFile.IsCompressed(raw))
        {
            // unmodified compressed file: use/create the decompressed cache
            var cache = Path.Combine(CacheDir, "4f", bundle.ToString("x6"));
            if (File.Exists(cache) && File.GetLastWriteTimeUtc(cache) >= File.GetLastWriteTimeUtc(path)) raw = File.ReadAllBytes(cache);
            else
            {
                raw = XCompressFile.Decompress(raw);
                Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
                File.WriteAllBytes(cache, raw);
            }
        }
        c = CaffFile.Read(raw);
        _residentCache[bundle] = c;
        return c;
    }

    public BundleArchive LoadStream(uint bundle) => BundleArchive.Read(File.ReadAllBytes(Game.StreamPath(bundle)));

    /// <summary>Writes a modified resident bundle (uncompressed CAFF — the game accepts both) and logs the change.</summary>
    int _batch;
    readonly Dictionary<uint, (CaffFile Caff, List<string> Descriptions)> _deferred = new();

    /// <summary>
    /// Batches resident saves until the returned scope is disposed: a mod with thousands of world edits (character select)
    /// loads and writes each bundle once instead of once per edit. Inside the batch every load returns the edited bundle.
    /// </summary>
    public IDisposable Batch() { _batch++; return new BatchScope(this); }

    sealed class BatchScope : IDisposable
    {
        Workspace? _ws;
        public BatchScope(Workspace ws) { _ws = ws; }
        public void Dispose()
        {
            if (_ws is not { } ws) return;
            _ws = null;
            if (--ws._batch > 0) return;
            var pending = ws._deferred.ToList(); ws._deferred.Clear();
            foreach (var (b, (caff, ds)) in pending)
                ws.WriteResident(b, caff, ds.Count == 1 ? ds[0] : $"{ds.Count} world edits: " + string.Join("; ", ds.Distinct().Take(6)) + (ds.Distinct().Count() > 6 ? " ..." : ""));
        }
    }

    public void SaveResident(uint bundle, CaffFile caff, string description)
    {
        bundle &= 0xFFFFFF;
        if (_batch > 0)
        {
            _residentCache[bundle] = caff;
            if (!_deferred.TryGetValue(bundle, out var d)) _deferred[bundle] = d = (caff, new List<string>());
            _deferred[bundle] = (caff, d.Descriptions);
            d.Descriptions.Add(description);
            return;
        }
        WriteResident(bundle, caff, description);
    }

    void WriteResident(uint bundle, CaffFile caff, string description)
    {
        var bytes = caff.Write();
        // sanity: the written file must parse back and keep its checksum valid
        var check = CaffFile.Read(bytes);
        if (!CaffFile.VerifyHeaderChecksum(bytes) || check.Parts.Count != caff.Parts.Count) throw new InvalidDataException("rebuilt CAFF failed validation");
        var path = Game.ResidentPath(bundle);
        Snapshot(path);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, true);
        // keep the caller's object as the cached bundle: the open world, the tag editor, the atmosphere editor … all edit
        // the same CaffFile, so a later save by one of them cannot drop the saved edits of another (caching the re-read
        // copy made every earlier holder stale: a tag edit saved after a world save was lost on the next world save)
        _residentCache[bundle] = caff;
        Log(Path.GetRelativePath(Game.Root, path), description);
    }

    public void SaveStream(uint bundle, BundleArchive a, string description)
    {
        var path = Game.StreamPath(bundle);
        Snapshot(path);
        File.WriteAllBytes(path + ".tmp", a.Write());
        File.Move(path + ".tmp", path, true);
        Log(Path.GetRelativePath(Game.Root, path), description);
    }

    /// <summary>Raised just before a working-copy file is overwritten (full path), from the writing thread: an editor's
    /// undo history copies the old version here.</summary>
    public event Action<string>? BeforeWrite;
    /// <summary>Raised after a change was logged (relative file, description).</summary>
    public event Action<string, string>? Changed;

    /// <summary>Drops every cached resident bundle (after files were restored behind the cache's back).</summary>
    public void ForgetCaches() => _residentCache.Clear();

    public void Log(string file, string description)
    {
        Manifest.Changes.Add(new ChangeEntry { Time = DateTime.Now, File = file, Description = description });
        SaveManifest();
        Changed?.Invoke(file, description);
    }

    /// <summary>Files whose content differs from the original (size or SHA-1).</summary>
    public List<string> ModifiedFiles(bool hash = true)
    {
        var list = new List<string>();
        // without hashing, a same-size file counts as modified when it was written after the copy and the change log names it
        // (a re-encoded texture keeps the stream archive's size: the quick count used to miss Bundle/50 files)
        var logged = new HashSet<string>(Manifest.Changes.Select(c => c.File), StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.GetFiles(Game.Root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(Game.Root, f);
            var o = Path.Combine(Original.Root, rel);
            if (!File.Exists(o)) { list.Add(rel); continue; }
            var fi = new FileInfo(f); var oi = new FileInfo(o);
            if (fi.Length != oi.Length) { list.Add(rel); continue; }
            if (hash && fi.LastWriteTimeUtc != oi.LastWriteTimeUtc && !SameContent(f, o)) list.Add(rel);
            else if (!hash && fi.LastWriteTimeUtc != oi.LastWriteTimeUtc && logged.Contains(rel)) list.Add(rel);
        }
        return list;
    }

    static bool SameContent(string a, string b)
    {
        using var sa = File.OpenRead(a); using var sb = File.OpenRead(b);
        return SHA1.HashData(sa).AsSpan().SequenceEqual(SHA1.HashData(sb));
    }

    public string HistoryDir => Path.Combine(Root, "history");
    public const int HistoryKeep = 8;

    /// <summary>Keeps the current version of a working-copy file before it is overwritten (last <see cref="HistoryKeep"/> per file).</summary>
    public void Snapshot(string path)
    {
        BeforeWrite?.Invoke(path);
        if (_folder) { _beforeWrite?.Invoke(path); return; }
        if (!File.Exists(path)) return;
        var rel = Path.GetRelativePath(Game.Root, path);
        var dir = Path.Combine(HistoryDir, rel);
        Directory.CreateDirectory(dir);
        File.Copy(path, Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".bin"), true);
        foreach (var old in Directory.GetFiles(dir, "*.bin").OrderByDescending(f => f).Skip(HistoryKeep)) File.Delete(old);
    }

    /// <summary>Saved versions of a working-copy file, newest first.</summary>
    public List<string> History(string relative)
    {
        var dir = Path.Combine(HistoryDir, relative);
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.bin").OrderByDescending(f => f).ToList() : new();
    }

    /// <summary>Restores the newest saved version (undoing the last save); the current file is snapshotted first.</summary>
    public string UndoLastSave(string relative)
    {
        var h = History(relative);
        if (h.Count == 0) throw new InvalidOperationException("no saved history for " + relative);
        var dst = Path.Combine(Game.Root, relative);
        var bytes = File.ReadAllBytes(h[0]);
        BeforeWrite?.Invoke(dst);
        File.Delete(h[0]);
        File.WriteAllBytes(dst, bytes);
        _residentCache.Clear();
        Log(relative, "restored previous version " + Path.GetFileNameWithoutExtension(h[0]));
        return Path.GetFileNameWithoutExtension(h[0]);
    }

    /// <summary>Restores one file from the original directory.</summary>
    public void Revert(string relative)
    {
        var src = Path.Combine(Original.Root, relative);
        var dst = Path.Combine(Game.Root, relative);
        Snapshot(dst);
        File.Copy(src, dst, true);
        File.SetAttributes(dst, FileAttributes.Normal);
        if (relative.StartsWith("Bundle", StringComparison.OrdinalIgnoreCase)) _residentCache.Clear();
        Log(relative, "reverted to original");
    }

    public void ForgetCache(uint bundle) => _residentCache.Remove(bundle & 0xFFFFFF);

    /// <summary>
    /// Exports the modified game: either the whole working copy (a playable game directory) or only the files that differ
    /// from the original (a mod package). Always writes NBMOD_CHANGES.txt (modified files + change log).
    /// Returns the number of files copied.
    /// </summary>
    public int Export(string target, bool changedOnly, IProgress<(string File, double Fraction)>? progress = null, bool bakeExeMods = false)
    {
        if (Path.GetFullPath(target).StartsWith(Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("export target must be outside the workspace");
        if (Path.GetFullPath(target).StartsWith(Path.GetFullPath(Original.Root), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("export target must not be inside the original game directory");
        var modified = ModifiedFiles();
        var files = changedOnly ? modified : Directory.GetFiles(Game.Root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(Game.Root, f)).ToList();
        int n = 0;
        foreach (var rel in files)
        {
            progress?.Report((rel, (double)n / Math.Max(1, files.Count)));
            var dst = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(Path.Combine(Game.Root, rel), dst, true);
            n++;
        }
        string? baked = null;
        if (bakeExeMods && Manifest.ExeMods.Count > 0)
        {
            // console build: write default.xex with the executable mods applied (decrypted payload; needs RGH/JTAG)
            var xex = Formats.XexFile.Read(File.ReadAllBytes(Game.Xex));
            var img = xex.GetImage();
            var mods = Mods.ExePatches.ResolveAll(Manifest.ExeMods);
            foreach (var m in mods)
            {
                var probs = Mods.ExePatches.Check(img, xex.ImageBase, m);
                if (probs.Count > 0) throw new InvalidDataException($"mod {m.Id} cannot be applied: {string.Join("; ", probs)}");
            }
            var dstXex = Path.Combine(target, Path.GetRelativePath(Game.Root, Game.Xex));
            Directory.CreateDirectory(Path.GetDirectoryName(dstXex)!);
            File.WriteAllBytes(dstXex, xex.WritePatched(mods.SelectMany(m => m.Words).Select(w => (w.Address, w.Patched))));
            baked = string.Join(", ", mods.Select(m => m.Id));
            if (!files.Contains(Path.GetRelativePath(Game.Root, Game.Xex))) n++;
        }
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Exported by NB Mod Tool on {DateTime.Now:yyyy-MM-dd HH:mm} from workspace {Root}");
        sb.AppendLine(changedOnly ? "Mod package: copy these files over an unmodified game directory (same relative paths)." : "Complete game directory.");
        sb.AppendLine(); sb.AppendLine($"Modified files ({modified.Count}):");
        foreach (var m in modified) sb.AppendLine("  " + m);
        if (Manifest.ExeMods.Count > 0) { sb.AppendLine(); sb.AppendLine("Executable mods (Xenia patch file in xenia_patches\\): " + string.Join(", ", Manifest.ExeMods)); }
        if (baked != null) sb.AppendLine("default.xex has these mods built in (decrypted, unsigned: for RGH/JTAG consoles or emulators; do not also use the Xenia patch file): " + baked);
        sb.AppendLine(); sb.AppendLine("Change log:");
        foreach (var c in Manifest.Changes) sb.AppendLine($"  {c.Time:yyyy-MM-dd HH:mm:ss}  {c.File}  {c.Description}");
        File.WriteAllText(Path.Combine(target, "NBMOD_CHANGES.txt"), sb.ToString());
        return n;
    }
}
