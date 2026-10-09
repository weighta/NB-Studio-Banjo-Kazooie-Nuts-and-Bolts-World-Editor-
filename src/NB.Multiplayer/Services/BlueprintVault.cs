using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace NB.Multiplayer.Services;

/// <summary>
/// Keeps the player's Nuts &amp; Bolts blueprint library safe. The game stores every saved blueprint as its own content
/// package (data\content\&lt;xuid&gt;\4D5307ED\00000001\0x0000000N\0000000N, header in Headers\00000001\0x0000000N.header,
/// display name "VEHICLE: ..."). The vault copies every blueprint it sees into data\blueprint_vault (by SHA-256 of the
/// data) and puts missing ones back into the signed-in profile before a game starts, so a recreated profile, another
/// Xenia storage folder or a wiped content folder never loses blueprints. Blueprints the player deleted in the game are
/// remembered (tombstones) and not brought back.
/// </summary>
public static class BlueprintVault
{
    const string Title = "4D5307ED", SaveType = "00000001";
    const int NameField = 0x971A, DisplayName = 0x411;
    static string Dir => Path.Combine(AppSettings.DataDir, "blueprint_vault");
    static string IndexFile => Path.Combine(Dir, "vault.json");

    public sealed class Entry
    {
        public string Hash { get; set; } = "";
        public string Name { get; set; } = "";
        public Dictionary<string, int> SeenAt { get; set; } = new();   // profile xuid -> package index at the last harvest
        public bool Deleted { get; set; }
        public DateTime FirstSeen { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public bool LoadedDeleted { get; set; }   // Deleted as loaded (see Store)
    }

    /// <summary>0x00000001..0x00FFFFFF are blueprints; the save slots are 0x0b0a5c5c / 0x0b0d6cca.</summary>
    static bool IsBlueprint(string folder, out int index)
    {
        index = 0;
        return folder.Length == 10 && folder.StartsWith("0x") &&
               int.TryParse(folder[2..], System.Globalization.NumberStyles.HexNumber, null, out index) && index >= 1 && index < 0x01000000;
    }

    static Dictionary<string, Entry> Load()
    {
        Dictionary<string, Entry> v;
        try { v = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(IndexFile)) ?? new(); }
        catch (Exception) { return new(); }
        foreach (var e in v.Values) e.LoadedDeleted = e.Deleted;
        // names written before 2.3 were read as UTF-16BE only ("SalvyBob" -> "卡汶祂潢"): read them again from the files
        foreach (var e in v.Values)
            try
            {
                var f = Path.Combine(Dir, e.Hash + ".bp");
                if (!File.Exists(f)) continue;
                var head = new byte[8 + 0x60];
                using (var s = File.OpenRead(f)) if (s.Read(head, 0, head.Length) < head.Length) continue;
                e.Name = ReadName(head);
            }
            catch (IOException) { }
        return v;
    }
    static void Store(Dictionary<string, Entry> v)
    {
        // as NB Studio's VehicleVault.Store: removed.json counts; a vehicle restored in NB Studio since this copy was loaded
        // stays restored; vehicles added meanwhile are kept
        var removed = NB.Core.Project.VehicleVault.Removed(Dir);
        Dictionary<string, Entry> disk;
        try { disk = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(IndexFile)) ?? new(); }
        catch (Exception) { disk = new(); }
        foreach (var (h, e) in v)
        {
            if (removed.ContainsKey(h)) e.Deleted = true;
            else if (e.Deleted && e.LoadedDeleted && disk.TryGetValue(h, out var d) && !d.Deleted) e.Deleted = false;
        }
        foreach (var (h, d) in disk) v.TryAdd(h, d);
        Directory.CreateDirectory(Dir);
        var tmp = IndexFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(v, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, IndexFile, true);
    }

    /// <summary>Content roots to read: NB Multiplayer's own, plus NB Studio's Xenia storage when known (read-only).</summary>
    static IEnumerable<string> ContentRoots(IEnumerable<string>? extra)
    {
        yield return Path.Combine(AppSettings.DataDir, "content");
        yield return Renut.UserRoot;   // reNut's profile (Settings > Game engine)
        foreach (var e in extra ?? Array.Empty<string>()) if (Directory.Exists(e)) yield return e;
    }

    /// <summary>Copies every blueprint of every profile into the vault. Call when the game has exited and before a launch.
    /// <paramref name="ownRoot"/> profiles (NB Multiplayer's) also get deletion tracking; extra roots are only read.</summary>
    public static void Harvest(IEnumerable<string>? extraContentRoots = null)
    {
        var v = Load();
        var removed = NB.Core.Project.VehicleVault.Removed(Dir);   // replaced / removed in NB Studio's Vehicle Editor: kept out
        var ownRoots = new[] { Path.Combine(AppSettings.DataDir, "content"), Renut.UserRoot };
        foreach (var root in ContentRoots(extraContentRoots))
        {
            if (!Directory.Exists(root)) continue;
            foreach (var prof in Directory.GetDirectories(root))
            {
                string xuid = Path.GetFileName(prof);
                var pkgRoot = Path.Combine(prof, Title, SaveType);
                var present = new HashSet<string>();
                if (Directory.Exists(pkgRoot))
                    foreach (var pkg in Directory.GetDirectories(pkgRoot))
                    {
                        if (!IsBlueprint(Path.GetFileName(pkg), out int idx)) continue;
                        var data = Path.Combine(pkg, Path.GetFileName(pkg)[2..]);
                        var header = Path.Combine(prof, Title, "Headers", SaveType, Path.GetFileName(pkg) + ".header");
                        if (!File.Exists(data)) continue;
                        var bytes = File.ReadAllBytes(data);
                        if (!NB.Core.Project.VehicleVault.LooksLikeBlueprint(bytes)) continue;
                        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                        present.Add(hash);
                        if (removed.ContainsKey(hash)) continue;
                        if (!v.TryGetValue(hash, out var e))
                        {
                            e = v[hash] = new Entry { Hash = hash, FirstSeen = DateTime.Now, Name = ReadName(bytes) };
                            Directory.CreateDirectory(Dir);
                            File.WriteAllBytes(Path.Combine(Dir, hash + ".bp"), bytes);
                            if (File.Exists(header)) File.Copy(header, Path.Combine(Dir, hash + ".header"), true);
                        }
                        e.Deleted = false;
                        if (ownRoots.Contains(root)) e.SeenAt[xuid] = idx;
                    }
                if (!ownRoots.Contains(root)) continue;
                // seen in this profile at the last harvest and gone now: the player deleted it in the game
                foreach (var e in v.Values)
                    if (e.SeenAt.ContainsKey(xuid) && !present.Contains(e.Hash)) { e.Deleted = true; e.SeenAt.Remove(xuid); }
            }
        }
        Store(v);
    }

    /// <summary>Puts every vault blueprint the active profile lacks (and the player did not delete) back into it, at the
    /// lowest free index. Returns how many were restored.</summary>
    public static int RestoreInto(string profileDir)
    {
        var v = Load();
        var removed = NB.Core.Project.VehicleVault.Removed(Dir);
        string xuid = Path.GetFileName(profileDir);
        var pkgRoot = Path.Combine(profileDir, Title, SaveType);
        var hdrRoot = Path.Combine(profileDir, Title, "Headers", SaveType);
        Directory.CreateDirectory(pkgRoot); Directory.CreateDirectory(hdrRoot);
        var have = new HashSet<string>(); var used = new HashSet<int>();
        foreach (var pkg in Directory.GetDirectories(pkgRoot))
        {
            if (!IsBlueprint(Path.GetFileName(pkg), out int idx)) continue;
            var data = Path.Combine(pkg, Path.GetFileName(pkg)[2..]);
            if (!File.Exists(data)) continue;                // a deleted blueprint leaves its header only
            used.Add(idx);
            have.Add(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(data))).ToLowerInvariant());
        }
        int restored = 0, next = 1;
        foreach (var e in v.Values.Where(e => !e.Deleted && !removed.ContainsKey(e.Hash) && !have.Contains(e.Hash)).OrderBy(e => e.FirstSeen))
        {
            var src = Path.Combine(Dir, e.Hash + ".bp");
            if (!File.Exists(src)) continue;
            while (used.Contains(next)) next++;
            if (next > 990) break;                           // the game lists at most 999 content items (incl. 2 save slots)
            string name = $"0x{next:x8}";
            Directory.CreateDirectory(Path.Combine(pkgRoot, name));
            File.Copy(src, Path.Combine(pkgRoot, name, name[2..]), true);
            byte[] hdr;
            if (xuid == Renut.ProfileXuid)
                hdr = Renut.ContentHeader(name, ("VEHICLE: " + e.Name).ToUpperInvariant());   // reNut's short header
            else
            {
                var kept = Path.Combine(Dir, e.Hash + ".header");
                hdr = File.Exists(kept) && new FileInfo(kept).Length > NameField + 42 ? File.ReadAllBytes(kept) : Saves.BlueprintHeaderTemplate(e.Name);
                var fn = System.Text.Encoding.ASCII.GetBytes(name);
                Array.Clear(hdr, NameField, 42); fn.CopyTo(hdr, NameField);
            }
            File.WriteAllBytes(Path.Combine(hdrRoot, name + ".header"), hdr);
            e.SeenAt[xuid] = next; used.Add(next); restored++;
        }
        Store(v);
        return restored;
    }

    static string ReadName(byte[] b) => NB.Core.Project.VehicleVault.ReadName(b);   // UTF-16BE or ASCII ("SalvyBob")
}
