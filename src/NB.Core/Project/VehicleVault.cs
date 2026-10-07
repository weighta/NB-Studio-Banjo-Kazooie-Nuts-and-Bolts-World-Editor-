using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NB.Core.Formats;

namespace NB.Core.Project;

/// <summary>
/// A folder of Nuts &amp; Bolts vehicle blueprints shared by every test storage that uses it — the same format as NB
/// Multiplayer's data\blueprint_vault (BlueprintVault.cs there), so NB Studio's tests and NB Multiplayer see the same
/// vehicles: &lt;hash&gt;.bp (the blueprint file, named by the SHA-256 of its bytes), &lt;hash&gt;.header (the Xenia content
/// header it was saved with) and vault.json (name, first seen, where it was seen, deleted).
///
/// In a Xenia content folder a blueprint is its own content package: content\&lt;profile xuid&gt;\4D5307ED\00000001\0x0000000N\
/// 0000000N plus the header content\&lt;xuid&gt;\4D5307ED\Headers\00000001\0x0000000N.header (0xA000 bytes: the STFS header
/// with the profile id at 0x371 and the display name "VEHICLE: NAME" at 0x411, then Xenia's file name at 0x971A). The save
/// slots are 0x0b0a5c5c / 0x0b0d6cca (not blueprints). A blueprint the player deletes in a game is remembered as deleted
/// (tracked per storage, "owner") and not brought back into any storage.
/// </summary>
public static class VehicleVault
{
    public const string Title = "4D5307ED", SaveType = "00000001";
    const int HeaderSize = 0xA000, NameField = 0x971A, DisplayField = 0x411, ProfileField = 0x371;

    public sealed class Entry
    {
        public string Hash { get; set; } = "";
        public string Name { get; set; } = "";
        public Dictionary<string, int> SeenAt { get; set; } = new();
        public bool Deleted { get; set; }
        public DateTime FirstSeen { get; set; }
    }

    /// <summary>NB Multiplayer's data folder (%LOCALAPPDATA%\NB-Multiplayer\data, or NB_MP_ROOT\data) when it exists.</summary>
    public static string? MultiplayerDataDir
    {
        get
        {
            var root = Environment.GetEnvironmentVariable("NB_MP_ROOT") is { Length: > 0 } r ? r
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NB-Multiplayer");
            var data = Path.Combine(root, "data");
            return Directory.Exists(data) ? data : null;
        }
    }

    /// <summary>NB Multiplayer's shared blueprint folder (null when NB Multiplayer is not installed).</summary>
    public static string? MultiplayerVault => MultiplayerDataDir is { } d ? Path.Combine(d, "blueprint_vault") : null;

    /// <summary>The default shared folder: NB Multiplayer's if it is installed, else one of NB Studio's own.</summary>
    public static string DefaultVault(string studioDataDir) => MultiplayerVault ?? Path.Combine(studioDataDir, "blueprint_vault");

    static string IndexFile(string dir) => Path.Combine(dir, "vault.json");

    public static Dictionary<string, Entry> Load(string dir)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(IndexFile(dir))) ?? new(); }
        catch (Exception) { return new(); }
    }

    static void Store(string dir, Dictionary<string, Entry> v)
    {
        Directory.CreateDirectory(dir);
        var tmp = IndexFile(dir) + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(v, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, IndexFile(dir), true);
    }

    /// <summary>The vehicles of the folder that are not deleted (name, first seen), oldest first.</summary>
    public static List<Entry> Vehicles(string dir) =>
        Load(dir).Values.Where(e => !e.Deleted && File.Exists(Path.Combine(dir, e.Hash + ".bp"))).OrderBy(e => e.FirstSeen).ToList();

    /// <summary>0x00000001..0x00FFFFFF are blueprint packages.</summary>
    public static bool IsBlueprint(string folder, out int index)
    {
        index = 0;
        return folder.Length == 10 && folder.StartsWith("0x") &&
               int.TryParse(folder[2..], System.Globalization.NumberStyles.HexNumber, null, out index) && index >= 1 && index < 0x01000000;
    }

    /// <summary>Profile folders of a Xenia content root (16 hex digits, not all zero, with an account folder FFFE07D1).</summary>
    public static List<string> Profiles(string contentRoot) =>
        !Directory.Exists(contentRoot) ? new() :
        Directory.GetDirectories(contentRoot).Where(d =>
        {
            var n = Path.GetFileName(d);
            return n.Length == 16 && n.All(Uri.IsHexDigit) && n.Trim('0').Length > 0 && Directory.Exists(Path.Combine(d, "FFFE07D1"));
        }).ToList();

    static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    /// <summary>The vehicle's name (UTF-16BE at 8 + 0x20 of the blueprint file).</summary>
    public static string ReadName(byte[] b)
    {
        var sb = new StringBuilder();
        for (int o = 8 + 0x20; o + 1 < Math.Min(b.Length, 8 + 0x60); o += 2) { char c = (char)(b[o] << 8 | b[o + 1]); if (c == 0) break; sb.Append(c); }
        return sb.ToString();
    }

    /// <summary>
    /// Copies every blueprint of every profile of <paramref name="contentRoot"/> into the folder. Blueprints seen there at
    /// the last harvest (by this <paramref name="owner"/>) and gone now were deleted in the game and are marked deleted.
    /// Returns the names of the vehicles that were new to the folder.
    /// </summary>
    public static List<string> Harvest(string dir, string contentRoot, string owner)
    {
        var added = new List<string>();
        if (!Directory.Exists(contentRoot)) return added;
        var v = Load(dir);
        bool changed = false;
        foreach (var prof in Profiles(contentRoot))
        {
            string key = owner + "|" + Path.GetFileName(prof);
            var pkgRoot = Path.Combine(prof, Title, SaveType);
            var present = new HashSet<string>();
            if (Directory.Exists(pkgRoot))
                foreach (var pkg in Directory.GetDirectories(pkgRoot))
                {
                    var name = Path.GetFileName(pkg);
                    if (!IsBlueprint(name, out int idx)) continue;
                    var data = Path.Combine(pkg, name[2..]);
                    var header = Path.Combine(prof, Title, "Headers", SaveType, name + ".header");
                    if (!File.Exists(data)) continue;
                    byte[] bytes;
                    try { bytes = File.ReadAllBytes(data); } catch (IOException) { continue; }   // still being written
                    if (bytes.Length < 8 + 0x7C) continue;
                    var hash = Hash(bytes);
                    present.Add(hash);
                    if (!v.TryGetValue(hash, out var e))
                    {
                        e = v[hash] = new Entry { Hash = hash, FirstSeen = DateTime.Now, Name = ReadName(bytes) };
                        Directory.CreateDirectory(dir);
                        File.WriteAllBytes(Path.Combine(dir, hash + ".bp"), bytes);
                        if (File.Exists(header)) File.Copy(header, Path.Combine(dir, hash + ".header"), true);
                        added.Add(e.Name);
                    }
                    if (e.Deleted) { e.Deleted = false; }
                    e.SeenAt[key] = idx;
                    changed = true;
                }
            foreach (var e in v.Values)
                if (e.SeenAt.ContainsKey(key) && !present.Contains(e.Hash)) { e.Deleted = true; e.SeenAt.Remove(key); changed = true; }
        }
        if (changed) Store(dir, v);
        return added;
    }

    /// <summary>Forgets where this owner's storage had vehicles (before its test save is emptied on purpose, so that
    /// is not taken for the player deleting them).</summary>
    public static void Forget(string dir, string owner)
    {
        var v = Load(dir);
        bool changed = false;
        foreach (var e in v.Values)
            foreach (var k in e.SeenAt.Keys.Where(k => k.StartsWith(owner + "|", StringComparison.Ordinal)).ToList()) { e.SeenAt.Remove(k); changed = true; }
        if (changed) Store(dir, v);
    }

    /// <summary>
    /// Puts every vehicle of the folder that the profile lacks (and that was not deleted) into it, at the lowest free
    /// package index, with a header for this profile (profile id, file name). Returns the names restored.
    /// </summary>
    public static List<string> RestoreInto(string dir, string profileDir, string owner, byte[]? headerTemplate = null)
    {
        var res = new List<string>();
        var v = Load(dir);
        if (v.Count == 0) return res;
        string xuid = Path.GetFileName(profileDir), key = owner + "|" + xuid;
        ulong profileId = ulong.TryParse(xuid, System.Globalization.NumberStyles.HexNumber, null, out var x) ? x : 0;
        var pkgRoot = Path.Combine(profileDir, Title, SaveType);
        var hdrRoot = Path.Combine(profileDir, Title, "Headers", SaveType);
        Directory.CreateDirectory(pkgRoot); Directory.CreateDirectory(hdrRoot);
        var have = new HashSet<string>(); var used = new HashSet<int>();
        foreach (var pkg in Directory.GetDirectories(pkgRoot))
        {
            if (!IsBlueprint(Path.GetFileName(pkg), out int idx)) continue;
            used.Add(idx);
            var data = Path.Combine(pkg, Path.GetFileName(pkg)[2..]);
            if (File.Exists(data)) have.Add(Hash(File.ReadAllBytes(data)));
        }
        int next = 1;
        foreach (var e in v.Values.Where(e => !e.Deleted && !have.Contains(e.Hash)).OrderBy(e => e.FirstSeen))
        {
            var src = Path.Combine(dir, e.Hash + ".bp");
            if (!File.Exists(src)) continue;
            while (used.Contains(next) || File.Exists(Path.Combine(hdrRoot, $"0x{next:x8}.header"))) next++;
            if (next > 990) break;                           // the game lists at most 999 content items (incl. the 2 save slots)
            string name = $"0x{next:x8}";
            Directory.CreateDirectory(Path.Combine(pkgRoot, name));
            File.Copy(src, Path.Combine(pkgRoot, name, name[2..]), true);
            File.WriteAllBytes(Path.Combine(hdrRoot, name + ".header"), HeaderFor(dir, e, name, profileId, headerTemplate));
            e.SeenAt[key] = next; used.Add(next);
            res.Add(e.Name);
        }
        if (res.Count > 0) Store(dir, v);
        return res;
    }

    /// <summary>The Xenia header for a vehicle package: the one it was saved with (or a template), with this package's
    /// file name and the playing profile as its owner.</summary>
    static byte[] HeaderFor(string dir, Entry e, string fileName, ulong profileId, byte[]? template)
    {
        var kept = Path.Combine(dir, e.Hash + ".header");
        byte[] h;
        if (File.Exists(kept) && new FileInfo(kept).Length >= NameField + 42) h = File.ReadAllBytes(kept);
        else
        {
            if (template == null || template.Length < NameField + 42) throw new InvalidOperationException("no content header for " + e.Name);
            h = (byte[])template.Clone();
            Array.Clear(h, DisplayField, 0x80);
            var display = Encoding.BigEndianUnicode.GetBytes(("VEHICLE: " + e.Name).ToUpperInvariant());
            Array.Copy(display, 0, h, DisplayField, Math.Min(display.Length, 0x7E));
        }
        if (h.Length < HeaderSize) Array.Resize(ref h, HeaderSize);
        Array.Clear(h, NameField, 42);
        Encoding.ASCII.GetBytes(fileName).CopyTo(h, NameField);
        if (profileId != 0) for (int i = 0; i < 8; i++) h[ProfileField + i] = (byte)(profileId >> (56 - 8 * i));
        return h;
    }

    /// <summary>
    /// Adds vehicle packages copied from an Xbox 360 (STFS "CON" files named 0x0000000N, display name "VEHICLE: …") to the
    /// folder: the blueprint file inside and the package header (its first 0x971A bytes, as Xenia keeps it). Returns
    /// (added, already there, not vehicles).
    /// </summary>
    public static (List<string> Added, int Known, List<string> Skipped) ImportPackages(string dir, IEnumerable<string> files)
    {
        var added = new List<string>(); var skipped = new List<string>(); int known = 0;
        var v = Load(dir);
        foreach (var f in files)
        {
            try
            {
                var raw = File.ReadAllBytes(f);
                if (!StfsPackage.IsStfs(raw)) { skipped.Add(Path.GetFileName(f) + " (not an Xbox 360 package)"); continue; }
                var pkg = new StfsPackage(raw);
                if (pkg.TitleId != 0x4D5307ED || !pkg.DisplayName.StartsWith("VEHICLE", StringComparison.OrdinalIgnoreCase)) { skipped.Add($"{Path.GetFileName(f)} ({pkg.DisplayName})"); continue; }
                var entry = pkg.Files.FirstOrDefault(x => !x.IsDirectory);
                if (entry == null) { skipped.Add(Path.GetFileName(f) + " (no file inside: a damaged copy?)"); continue; }
                var bytes = pkg.Extract(entry);
                if (bytes.Length < 8 + 0x7C) { skipped.Add(Path.GetFileName(f) + " (too small)"); continue; }
                var hash = Hash(bytes);
                if (v.TryGetValue(hash, out var old)) { if (old.Deleted) { old.Deleted = false; added.Add(old.Name); } else known++; continue; }
                var e = v[hash] = new Entry { Hash = hash, FirstSeen = DateTime.Now, Name = ReadName(bytes) };
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, hash + ".bp"), bytes);
                var hdr = new byte[HeaderSize];
                Array.Copy(raw, hdr, Math.Min(NameField, raw.Length));
                File.WriteAllBytes(Path.Combine(dir, hash + ".header"), hdr);
                added.Add(e.Name);
            }
            catch (Exception x) { skipped.Add($"{Path.GetFileName(f)} ({x.Message})"); }
        }
        if (added.Count > 0) Store(dir, v);
        return (added, known, skipped);
    }
}
