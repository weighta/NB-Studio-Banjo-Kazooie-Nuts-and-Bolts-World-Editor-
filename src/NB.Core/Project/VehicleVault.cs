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
        /// <summary><see cref="Deleted"/> as read from vault.json (not stored): <see cref="Store"/> keeps a restore that
        /// another writer made after this copy was loaded.</summary>
        [System.Text.Json.Serialization.JsonIgnore] public bool LoadedDeleted { get; set; }
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
        Dictionary<string, Entry> v;
        try { v = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(IndexFile(dir))) ?? new(); }
        catch (Exception) { return new(); }
        foreach (var e in v.Values) e.LoadedDeleted = e.Deleted;
        RefreshNames(dir, v);
        return v;
    }

    /// <summary>
    /// Writes the index <paramref name="v"/> (from <see cref="Load"/>). Other writers may have stored since it was loaded
    /// (a harvest runs on a test's watch thread, NB Multiplayer around its launches), so: removed.json is what counts for
    /// removed vehicles (an older NB Multiplayer / NB Studio clears Deleted when its storage still has the vehicle; they are
    /// marked deleted again for readers that only know the flag); a vehicle that was deleted when <paramref name="v"/> was
    /// loaded and is not deleted on disk now was restored meanwhile and stays restored; vehicles added on disk meanwhile
    /// are kept.
    /// </summary>
    public static void Store(string dir, Dictionary<string, Entry> v)
    {
        var removed = Removed(dir);
        Dictionary<string, Entry> disk;
        try { disk = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(IndexFile(dir))) ?? new(); }
        catch (Exception) { disk = new(); }
        foreach (var (h, e) in v)
        {
            if (removed.ContainsKey(h)) e.Deleted = true;
            else if (e.Deleted && e.LoadedDeleted && disk.TryGetValue(h, out var d) && !d.Deleted) e.Deleted = false;
        }
        foreach (var (h, d) in disk) v.TryAdd(h, d);
        Directory.CreateDirectory(dir);
        var tmp = IndexFile(dir) + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(v, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, IndexFile(dir), true);
    }

    /// <summary>The vehicles of the folder that are not deleted nor removed (removed.json; an older NB Multiplayer may
    /// have cleared their Deleted flag), oldest first.</summary>
    public static List<Entry> Vehicles(string dir)
    {
        var removed = Removed(dir);
        return Load(dir).Values.Where(e => !e.Deleted && !removed.ContainsKey(e.Hash) && File.Exists(Path.Combine(dir, e.Hash + ".bp"))).OrderBy(e => e.FirstSeen).ToList();
    }

    /// <summary>The part count of a vehicle of the folder (0 when unreadable).</summary>
    public static int PartsOf(string dir, Entry e)
    {
        try
        {
            using var s = File.OpenRead(Path.Combine(dir, e.Hash + ".bp"));
            var h = new byte[10];
            return s.Read(h, 0, 10) == 10 ? h[8] << 8 | h[9] : 0;
        }
        catch (IOException) { return 0; }
    }

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

    /// <summary>The name a vehicle file (content bytes) has in the folder: its SHA-256 (hex).</summary>
    public static string HashOf(byte[] content) => Hash(content);

    /// <summary>The vehicle's name (the name field at 8 + 0x20 of the blueprint file: UTF-16BE, or ASCII for the game's own
    /// vehicles; see <see cref="NB.Core.Vehicles.Blueprint.DecodeName"/>).</summary>
    public static string ReadName(byte[] b) => b.Length < 8 + 0x60 ? "" : NB.Core.Vehicles.Blueprint.DecodeName(b.AsSpan(8 + 0x20, 0x40));

    /// <summary>A vehicle file: long enough for the blueprint header, and not another kind of file that was put in a
    /// package folder (a profile's XDBF settings file reached a vault as a "vehicle" once).</summary>
    public static bool LooksLikeBlueprint(byte[] b) => b.Length >= 8 + 0x7C && !(b[0] == 'X' && b[1] == 'D' && b[2] == 'B' && b[3] == 'F');

    /// <summary>The names stored in the index read again from the vehicle files (indexes written before 1.20 hold
    /// "卡汶祂潢" for ASCII names such as "SalvyBob").</summary>
    static void RefreshNames(string dir, Dictionary<string, Entry> v)
    {
        foreach (var e in v.Values)
        {
            try
            {
                var f = Path.Combine(dir, e.Hash + ".bp");
                if (!File.Exists(f)) continue;
                var head = new byte[8 + 0x60];
                using (var s = File.OpenRead(f)) { int n = s.Read(head, 0, head.Length); if (n < head.Length) continue; }
                var name = ReadName(head);
                if (name != e.Name) e.Name = name;
            }
            catch (IOException) { }
        }
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
        var removed = Removed(dir);
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
                    if (!LooksLikeBlueprint(bytes)) continue;
                    var hash = Hash(bytes);
                    present.Add(hash);
                    if (removed.ContainsKey(hash)) continue;   // replaced or removed in the Vehicle Editor: stays out of the folder
                    if (!v.TryGetValue(hash, out var e))
                    {
                        string note = DuplicateNote(dir, v, removed, bytes, ReadName(bytes));
                        e = v[hash] = new Entry { Hash = hash, FirstSeen = DateTime.Now, Name = ReadName(bytes) };
                        Directory.CreateDirectory(dir);
                        File.WriteAllBytes(Path.Combine(dir, hash + ".bp"), bytes);
                        if (File.Exists(header)) File.Copy(header, Path.Combine(dir, hash + ".header"), true);
                        added.Add(e.Name + note);
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
        var put = LoadPut(dir); bool putChanged = false;
        foreach (var at in put.Values)
            foreach (var k in at.Keys.Where(k => k.StartsWith(owner + "|", StringComparison.Ordinal)).ToList()) { at.Remove(k); putChanged = true; }
        if (putChanged) StorePut(dir, put);
    }

    static string PutFile(string dir) => Path.Combine(dir, "studio_put.json");

    /// <summary>Where NB Studio's <see cref="RestoreInto"/> put each vehicle (hash → storage key → package index); NB
    /// Studio's own file (NB Multiplayer does not use it). Only these copies are taken back when a vehicle is removed: a
    /// vehicle the player built in a test storage (Harvest saw it there) stays.</summary>
    static Dictionary<string, Dictionary<string, int>> LoadPut(string dir)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(File.ReadAllText(PutFile(dir))) ?? new(); }
        catch (Exception) { return new(); }
    }

    static void StorePut(string dir, Dictionary<string, Dictionary<string, int>> put)
    {
        foreach (var h in put.Where(x => x.Value.Count == 0).Select(x => x.Key).ToList()) put.Remove(h);
        Directory.CreateDirectory(dir);
        var tmp = PutFile(dir) + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(put, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, PutFile(dir), true);
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
        var removed = Removed(dir);
        string xuid = Path.GetFileName(profileDir), key = owner + "|" + xuid;
        ulong profileId = ulong.TryParse(xuid, System.Globalization.NumberStyles.HexNumber, null, out var x) ? x : 0;
        var pkgRoot = Path.Combine(profileDir, Title, SaveType);
        var hdrRoot = Path.Combine(profileDir, Title, "Headers", SaveType);
        Directory.CreateDirectory(pkgRoot); Directory.CreateDirectory(hdrRoot);
        var have = new HashSet<string>(); var used = new HashSet<int>();
        var put = LoadPut(dir);
        bool changedIdx = false, changedPut = false;
        foreach (var pkg in Directory.GetDirectories(pkgRoot))
        {
            if (!IsBlueprint(Path.GetFileName(pkg), out int idx)) continue;
            var data = Path.Combine(pkg, Path.GetFileName(pkg)[2..]);
            if (File.Exists(data))
            {
                var h = Hash(File.ReadAllBytes(data));
                // a vehicle this folder put into this storage (studio_put.json, at this very package index) and that was
                // replaced / removed since: taken back out of this test storage. Vehicles built in the storage stay.
                if (removed.ContainsKey(h) && put.TryGetValue(h, out var putAt) && putAt.TryGetValue(key, out int at) && at == idx)
                {
                    try
                    {
                        File.Delete(data);
                        if (Directory.GetFileSystemEntries(pkg).Length == 0) Directory.Delete(pkg);
                        var hf = Path.Combine(hdrRoot, Path.GetFileName(pkg) + ".header");
                        if (File.Exists(hf)) File.Delete(hf);
                        putAt.Remove(key); changedPut = true;
                        if (v.TryGetValue(h, out var gone) && gone.SeenAt.Remove(key)) changedIdx = true;
                        continue;
                    }
                    catch (IOException) { }
                }
                have.Add(h);
            }
            used.Add(idx);
        }
        if (changedIdx) Store(dir, v);
        int next = 1;
        foreach (var e in v.Values.Where(e => !e.Deleted && !removed.ContainsKey(e.Hash) && !have.Contains(e.Hash)).OrderBy(e => e.FirstSeen))
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
            if (!put.TryGetValue(e.Hash, out var at)) put[e.Hash] = at = new();
            at[key] = next; changedPut = true;
            res.Add(e.Name);
        }
        if (res.Count > 0) Store(dir, v);
        if (changedPut) StorePut(dir, put);
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
        var removed = Removed(dir);
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
                if (v.TryGetValue(hash, out var old))
                {
                    if (old.Deleted || removed.ContainsKey(hash)) { Unremove(dir, hash); removed.Remove(hash); old.Deleted = false; added.Add(old.Name); } else known++;
                    continue;
                }
                string note = DuplicateNote(dir, v, removed, bytes, ReadName(bytes));
                var e = v[hash] = new Entry { Hash = hash, FirstSeen = DateTime.Now, Name = ReadName(bytes) };
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, hash + ".bp"), bytes);
                var hdr = new byte[HeaderSize];
                Array.Copy(raw, hdr, Math.Min(NameField, raw.Length));
                File.WriteAllBytes(Path.Combine(dir, hash + ".header"), hdr);
                added.Add(e.Name + note);
            }
            catch (Exception x) { skipped.Add($"{Path.GetFileName(f)} ({x.Message})"); }
        }
        if (added.Count > 0) Store(dir, v);
        return (added, known, skipped);
    }

    // ------------------------------------------------------------------ duplicates, names, replace / remove (Vehicle Editor)

    /// <summary>
    /// What makes two vehicle files the same vehicle: their part records (cell, orientation, part, paint, settings,
    /// buttons — 0x24 bytes each), in any order. The header is left out: the name, the stat bars, the weight, the button
    /// part types and the "named by the player" flag change when the game or NB Studio saves the same vehicle again. The
    /// category byte (+6) is left out too: it follows from the part (the game's own vehicles hold 0 there, NB Studio
    /// writes the part's category when it saves).
    /// </summary>
    public static string PartsKey(byte[] file)
    {
        int start = file.Length >= 8 + 0x7C && file[0] == 0x3F && file[1] == 0x9A ? 8 : 0;   // content files: f32 1.21, f32 3.32 prefix
        int first = start + 0x7C;
        if (file.Length < first) return Hash(file);
        int n = (file.Length - first) / 0x24;
        string Rec(int i)
        {
            var r = file.AsSpan(first + i * 0x24, 0x24).ToArray();
            r[6] = 0;
            return Convert.ToHexString(r);
        }
        var recs = Enumerable.Range(0, n).Select(Rec).OrderBy(x => x, StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(string.Concat(recs)))).ToLowerInvariant();
    }

    /// <summary>A vehicle of the folder like the one being saved: the same parts (<see cref="PartsKey"/>, whatever the name)
    /// and / or the same name (letter case ignored).</summary>
    public sealed record Match(Entry Entry, bool SameParts, bool SameName, bool SameBytes);

    /// <summary>The vehicles of the folder (not deleted) with the same parts or the same name as <paramref name="file"/>.</summary>
    public static List<Match> Similar(string dir, byte[] file, string name)
    {
        var res = new List<Match>();
        string key = PartsKey(file), hash = Hash(file);
        foreach (var e in Vehicles(dir))
        {
            bool sameName = string.Equals(e.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase);
            bool sameParts = false;
            try { sameParts = PartsKey(File.ReadAllBytes(Path.Combine(dir, e.Hash + ".bp"))) == key; } catch (IOException) { }
            if (sameName || sameParts) res.Add(new Match(e, sameParts, sameName, e.Hash == hash));
        }
        return res;
    }

    /// <summary>The first free name like <paramref name="name"/> among the folder's vehicles (leaving out
    /// <paramref name="except"/>, vehicles about to be replaced): "Racer" → "Racer 2", "Racer 3", … A copy of "Racer 2" is
    /// "Racer 3" (when "Racer" is there too), but "Apollo 13" keeps its number: "Apollo 13 2". Cut so it stays within
    /// <paramref name="maxChars"/>.</summary>
    public static string FreeName(string dir, string name, int maxChars = NB.Core.Vehicles.Blueprint.MaxNameChars, IEnumerable<string>? except = null)
    {
        var skip = except?.ToHashSet() ?? new HashSet<string>();
        var used = Vehicles(dir).Where(e => !skip.Contains(e.Hash)).Select(e => e.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(name.Trim())) return name;
        var bare = name.Trim();
        // only a small number after a name that is there itself is a copy number
        var m = System.Text.RegularExpressions.Regex.Match(bare, @"^(.*\S) (\d{1,2})$");
        if (m.Success && int.Parse(m.Groups[2].Value) is >= 2 and <= 20 && used.Contains(m.Groups[1].Value)) bare = m.Groups[1].Value;
        for (int k = 2; k < 1000; k++)
        {
            var tail = " " + k;
            var n = (bare.Length + tail.Length > maxChars ? bare[..(maxChars - tail.Length)].TrimEnd() : bare) + tail;
            if (!used.Contains(n)) return n;
        }
        return name;
    }

    static string RemovedFile(string dir) => Path.Combine(dir, "removed.json");

    /// <summary>
    /// Vehicles replaced or removed in the Vehicle Editor (removed.json: hash → why). Unlike "deleted in the game" they are
    /// not brought back when a test storage or NB Multiplayer still holds a copy: Harvest skips them, <see cref="Vehicles"/>
    /// does not list them, RestoreInto does not put them into a storage and takes back the copies it put there itself
    /// (studio_put.json, kept since NB Studio 1.23.0; copies older versions put there are not known and stay). NB
    /// Multiplayer reads the same file. <see cref="Restore"/> brings them back.
    /// </summary>
    public static Dictionary<string, string> Removed(string dir)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(RemovedFile(dir))) ?? new(); }
        catch (Exception) { return new(); }
    }

    static void StoreRemoved(string dir, Dictionary<string, string> r)
    {
        Directory.CreateDirectory(dir);
        var tmp = RemovedFile(dir) + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, RemovedFile(dir), true);
    }

    /// <summary>Takes vehicles out of the folder on purpose (Replace, Remove, Remove duplicates): listed in removed.json
    /// with <paramref name="why"/> (written first: it is what counts) and marked deleted. Their files stay;
    /// <see cref="Restore"/> brings them back.</summary>
    public static void Remove(string dir, IEnumerable<string> hashes, string why)
    {
        var r = Removed(dir);
        foreach (var h in hashes) r[h] = $"{why} ({DateTime.Now:yyyy-MM-dd HH:mm})";
        StoreRemoved(dir, r);
        Store(dir, Load(dir));   // marks them deleted
    }

    /// <summary>Removed vehicles (removed.json) whose files are still in the folder, with why and when; newest first.</summary>
    public static List<(Entry Entry, string Why)> RemovedVehicles(string dir)
    {
        var v = Load(dir); var res = new List<(Entry, string)>();
        foreach (var (h, why) in Removed(dir))
        {
            var f = Path.Combine(dir, h + ".bp");
            if (!File.Exists(f)) continue;
            if (!v.TryGetValue(h, out var e))
            {
                string name = "";
                try { name = ReadName(File.ReadAllBytes(f)); } catch (IOException) { }
                e = new Entry { Hash = h, Name = name, FirstSeen = File.GetLastWriteTime(f) };
            }
            res.Add((e, why));
        }
        return res.OrderByDescending(x => x.Item1.FirstSeen).ToList();
    }

    /// <summary>Puts removed vehicles back: off removed.json, not deleted; the next test (and NB Multiplayer) gets them again.</summary>
    public static void Restore(string dir, IEnumerable<string> hashes)
    {
        var list = hashes.ToList();
        var r = Removed(dir);
        foreach (var h in list) r.Remove(h);
        StoreRemoved(dir, r);
        var v = Load(dir);
        foreach (var h in list)
            if (v.TryGetValue(h, out var e)) e.Deleted = false;
            else if (File.Exists(Path.Combine(dir, h + ".bp")))
            {
                string name = "";
                try { name = ReadName(File.ReadAllBytes(Path.Combine(dir, h + ".bp"))); } catch (IOException) { }
                v[h] = new Entry { Hash = h, Name = name, FirstSeen = DateTime.Now };
            }
        Store(dir, v);
    }

    /// <summary>A removed vehicle saved or imported again on purpose: no longer kept out.</summary>
    public static void Unremove(string dir, string hash)
    {
        var r = Removed(dir);
        if (r.Remove(hash)) StoreRemoved(dir, r);
    }

    /// <summary>The folder's exact duplicates: groups of vehicles with the same parts (2 or more), newest first.</summary>
    public static List<List<Entry>> Duplicates(string dir)
    {
        var keyed = new List<(string Key, Entry E)>();
        foreach (var e in Vehicles(dir))
            try { keyed.Add((PartsKey(File.ReadAllBytes(Path.Combine(dir, e.Hash + ".bp"))), e)); } catch (IOException) { }
        return keyed.GroupBy(x => x.Key).Where(g => g.Count() > 1).Select(g => g.Select(x => x.E).OrderByDescending(e => e.FirstSeen).ToList()).ToList();
    }

    /// <summary>" (same vehicle as 'X' …)" / " (a vehicle named 'X' …)" for a vehicle arriving in the folder (logs).</summary>
    static string DuplicateNote(string dir, Dictionary<string, Entry> v, Dictionary<string, string> removed, byte[] bytes, string name)
    {
        try
        {
            string key = PartsKey(bytes);
            foreach (var e in v.Values.Where(e => !e.Deleted && !removed.ContainsKey(e.Hash)))
            {
                var f = Path.Combine(dir, e.Hash + ".bp");
                if (File.Exists(f) && PartsKey(File.ReadAllBytes(f)) == key)
                    return $" (the same vehicle as '{e.Name}' already in the vehicle saves: Vehicle Editor › Save As › My Vehicle Saves can remove duplicates)";
            }
            var same = v.Values.FirstOrDefault(e => !e.Deleted && !removed.ContainsKey(e.Hash) && string.Equals(e.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (same != null) return $" (another vehicle named '{same.Name}' is in the vehicle saves too)";
        }
        catch (IOException) { }
        return "";
    }
}
