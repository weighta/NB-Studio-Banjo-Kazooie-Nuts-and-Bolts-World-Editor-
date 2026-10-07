using NB.Core.IO;

namespace NB.Core.Project;

/// <summary>
/// Disk use and removal of whole workspaces. A workspace's game folder is mostly hard links to the player's own game:
/// those files take no extra space, and deleting them must remove only the workspace's name for the file without
/// touching its read-only attribute (which the link shares with the original), so every file goes through
/// <see cref="FileLinks.DeleteIgnoringReadOnly"/>.
/// </summary>
public static class WorkspaceFiles
{
    public sealed record Usage(long OwnBytes, long LinkedBytes, int Files);

    public static bool IsWorkspace(string dir) => File.Exists(Path.Combine(dir, "workspace.json"));

    /// <summary>Bytes the workspace really uses (files of its own) and bytes shared with the game through hard links.</summary>
    public static Usage Measure(string root, CancellationToken ct = default)
    {
        long own = 0, linked = 0; int n = 0;
        foreach (var f in SafeFiles(root))
        {
            ct.ThrowIfCancellationRequested();
            long len; try { len = new FileInfo(f).Length; } catch (Exception) { continue; }
            n++;
            if (FileLinks.LinkCount(f) > 1) linked += len; else own += len;
        }
        return new Usage(own, linked, n);
    }

    static IEnumerable<string> SafeFiles(string root)
    {
        var stack = new Stack<string>(); stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            string[] files, dirs;
            try { files = Directory.GetFiles(d); dirs = Directory.GetDirectories(d); } catch (Exception) { continue; }
            foreach (var f in files) yield return f;
            foreach (var s in dirs)
            {
                // never follow junctions / symbolic links out of the workspace
                try { if (new DirectoryInfo(s).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue; } catch (Exception) { continue; }
                stack.Push(s);
            }
        }
    }

    /// <summary>
    /// Deletes a workspace folder (it must contain workspace.json and must not be a drive root). Hard links to the game
    /// lose only their workspace name; the game itself is never changed. Removes it from the recent-workspaces list.
    /// </summary>
    public static void Delete(string root, IProgress<(string Text, double Fraction)>? progress = null)
    {
        var full = Path.GetFullPath(root).TrimEnd('\\', '/');
        if (!IsWorkspace(full)) throw new InvalidOperationException($"{full} is not a workspace (no workspace.json): not deleted.");
        if (Path.GetPathRoot(full)?.TrimEnd('\\', '/') == full) throw new InvalidOperationException("Refusing to delete a drive root.");
        var files = SafeFiles(full).ToList();
        for (int i = 0; i < files.Count; i++)
        {
            if (i % 64 == 0) progress?.Report(($"Deleting {Path.GetFileName(full)} ({i}/{files.Count} files)", (double)i / Math.Max(1, files.Count)));
            FileLinks.DeleteIgnoringReadOnly(files[i]);
        }
        foreach (var d in Directory.GetDirectories(full, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            try { new DirectoryInfo(d).Attributes &= ~FileAttributes.ReadOnly; Directory.Delete(d, false); } catch (IOException) { }
        }
        Directory.Delete(full, true);
        ProjectRegistry.Remove(full);
        progress?.Report(($"Deleted {Path.GetFileName(full)}", 1));
    }

    /// <summary>Workspaces NB Studio knows: the recent list, plus every workspace folder next to them.</summary>
    public static List<string> Known(IEnumerable<string>? extra = null)
    {
        var roots = ProjectRegistry.Load().Select(e => e.Path).Concat(extra ?? Enumerable.Empty<string>()).Where(p => !string.IsNullOrEmpty(p)).ToList();
        var parents = roots.Select(p => Path.GetDirectoryName(p.TrimEnd('\\', '/'))).Where(p => p != null).Distinct(StringComparer.OrdinalIgnoreCase);
        var all = new List<string>(roots.Where(IsWorkspace));
        foreach (var parent in parents)
        {
            try { all.AddRange(Directory.GetDirectories(parent!).Where(IsWorkspace)); } catch (Exception) { }
        }
        return all.Select(p => Path.GetFullPath(p).TrimEnd('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(p => File.GetLastWriteTime(Path.Combine(p, "workspace.json"))).ToList();
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes} B",
    };
}
