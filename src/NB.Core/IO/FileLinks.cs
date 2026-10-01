using System.Runtime.InteropServices;

namespace NB.Core.IO;

/// <summary>
/// Game copies made of hard links (NB Multiplayer editions, NB Studio's test copies): unchanged files are links to the
/// player's own game, so nothing may be written through them, and their attributes (shared with the original) must not
/// change. Files that are about to change are first made private (<see cref="MakePrivate"/>).
/// </summary>
public static class FileLinks
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle h, int infoClass, ref uint info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle h, out ByHandleInfo info);

    [StructLayout(LayoutKind.Sequential)]
    struct ByHandleInfo
    {
        public uint Attributes, CreatedLow, CreatedHigh, AccessedLow, AccessedHigh, WrittenLow, WrittenHigh;   // FILETIMEs as two DWORDs (4-byte aligned)
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    public static bool TryLink(string link, string existing) => CreateHardLink(link, existing, IntPtr.Zero);

    /// <summary>Number of names the file has (1 = not a hard link).</summary>
    public static int LinkCount(string path)
    {
        using var h = CreateFileW(path, 0x80, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);   // FILE_READ_ATTRIBUTES; FILE_FLAG_BACKUP_SEMANTICS
        return !h.IsInvalid && GetFileInformationByHandle(h, out var i) ? (int)i.Links : -1;   // -1 = unknown
    }

    /// <summary>Deletes one name of a file without clearing its read-only attribute (which a hard link shares with the original).</summary>
    public static void DeleteIgnoringReadOnly(string path)
    {
        const uint DELETE = 0x00010000, SHARE_ALL = 7, OPEN_EXISTING = 3, OPEN_REPARSE_POINT = 0x00200000;
        const int FileDispositionInfoEx = 21;
        uint flags = 0x1 | 0x2 | 0x10;   // DELETE | POSIX_SEMANTICS | IGNORE_READONLY_ATTRIBUTE
        using var h = CreateFileW(path, DELETE, SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, OPEN_REPARSE_POINT, IntPtr.Zero);
        if (h.IsInvalid || !SetFileInformationByHandle(h, FileDispositionInfoEx, ref flags, 4))
            throw new IOException($"cannot delete {path} (error {Marshal.GetLastWin32Error()})");
    }

    /// <summary>
    /// Makes <paramref name="path"/> a file of its own, writable, before it is replaced or changed: a hard link (or a
    /// read-only file) becomes a private copy, so the player's original keeps its content and attributes.
    /// </summary>
    public static void MakePrivate(string path)
    {
        if (!File.Exists(path)) return;
        bool ro = File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly);
        if (LinkCount(path) == 1) { if (ro) File.SetAttributes(path, FileAttributes.Normal); return; }
        var tmp = path + ".nbprivate";
        File.Copy(path, tmp, true);
        File.SetAttributes(tmp, FileAttributes.Normal);
        DeleteIgnoringReadOnly(path);
        File.Move(tmp, path);
    }

    /// <summary>
    /// Before <paramref name="path"/> is replaced by another file (move over it): a hard link loses only this name (the
    /// player's original is untouched); a read-only file of its own becomes writable.
    /// </summary>
    public static void PrepareReplace(string path)
    {
        // removing the name is right for both: it never touches the attributes a hard link shares with the original
        if (File.Exists(path)) DeleteIgnoringReadOnly(path);
    }

    /// <summary>
    /// Copies a game folder as hard links (same drive) or copies; files in <paramref name="copies"/> (relative paths) are
    /// always real copies. Patch backups and logs are left out.
    /// </summary>
    public static void LinkCopy(string src, string dst, ISet<string>? copies = null, IProgress<(string Text, double Fraction)>? progress = null)
    {
        var files = Directory.GetFiles(src, "*", SearchOption.AllDirectories).Where(f => !f.Contains(Project.PatchPackage.BackupDirName)
            && !Path.GetFileName(f).Equals(Project.PatchPackage.LogFileName, StringComparison.OrdinalIgnoreCase)).ToList();
        bool sameVolume = string.Equals(Path.GetPathRoot(Path.GetFullPath(src)), Path.GetPathRoot(Path.GetFullPath(dst)), StringComparison.OrdinalIgnoreCase);
        for (int i = 0; i < files.Count; i++)
        {
            var rel = Path.GetRelativePath(src, files[i]);
            var to = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            progress?.Report(($"Preparing game files ({i + 1}/{files.Count})", (double)i / files.Count));
            if ((copies?.Contains(rel) ?? false) || !sameVolume || !TryLink(to, files[i]))
            {
                File.Copy(files[i], to, true);
                File.SetAttributes(to, FileAttributes.Normal);
            }
        }
    }
}
