using NB.Core.Formats;
using NB.Core.IO;
using NB.Core.Project;

namespace NB.Core.World;

/// <summary>One line of text a character says (or shows): table, key and name in the loctext table, and why it belongs to
/// the character.</summary>
public sealed class DialogueLine
{
    public uint Table;
    public string TableName = "";
    public ushort Key;
    public string Name = "";
    public string Text = "";
    /// <summary>"dialog aid_dialog_…" (a dialog asset the character's objects use) or "name" (the line's name carries the
    /// character's key, e.g. dialog__mrfit_cantafford1 used by the shop script).</summary>
    public string Source = "";
}

/// <summary>
/// The dialogue of a character placed by a marker. Links found in the data (Showdown Town, 2026-10-05):
/// <list type="bullet">
/// <item>marker record → aid_objparams actor (+0xC0 model, +0x1D0 strategy, +0x1D4 mind) and the strategy objparams
/// (Mr. Fit's entityStrategyJogger: +0x2C8 aid_dialog, +0x2CC aid_script, +0x280 goals); scripts name further dialogs.</item>
/// <item>aid_dialog (type 0x43): +8 = the loctext table id (type 0x11, Debug/11/xx/yy/zz in English, loctext/&lt;language&gt;/&lt;id&gt;
/// otherwise); each node holds its name ("mrfit_running") and the line is the table entry named "dialog__" + node name.</item>
/// <item>lines a script or shop shows without a dialog asset carry the character's key in their name
/// (dialog__mrfit_cantafford1, showdowntown__mrfit_menutitle): those are added by name from the same tables and the
/// world's / act's own tables.</item>
/// </list>
/// Generic objects shared by many characters (actorstrategy_npc …) are not followed, so another character's lines are not
/// attributed to this one.
/// </summary>
public static class CharacterText
{
    static readonly HashSet<byte> Follow = new() { 0x1F, 0x19, 0x43, 0x0B, 0x0C };
    static readonly HashSet<string> NotKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "actor", "actorstrategy", "actormind", "actorbody", "npc", "props", "avatar", "objects", "showdowntown", "nuttyacres",
        "banjoland", "cpu", "terrorium", "worldofsport", "spiralmountain", "townsfolk", "global", "main", "default", "banjox",
    };

    /// <summary>Character key of an objparams name: its last meaningful token (actor_showdowntown_mrfit → "mrfit").</summary>
    public static string? KeyOf(string objparamsName)
    {
        var n = AssetIds.DisplayName(objparamsName).Replace("aid_objparams_banjox_", "");
        var toks = n.Split('_').Where(t => t.Length >= 3 && !NotKeys.Contains(t) && !(t.StartsWith("act") && t.Length <= 5)).ToList();
        return toks.Count > 0 ? toks[^1].ToLowerInvariant() : null;
    }

    public static string TablePath(Workspace ws, uint table, string language = "english") =>
        language == "english"
            ? Path.Combine(ws.Game.Root, "Debug", "11", ((table >> 16) & 0xFF).ToString("x2"), ((table >> 8) & 0xFF).ToString("x2"), (table & 0xFF).ToString("x2"))
            : Path.Combine(ws.Game.Root, "loctext", language, table.ToString("x8"));

    /// <summary>The character's name (key) and its lines. <paramref name="names"/> = asset id → name (asset index).</summary>
    public static (string? Key, List<DialogueLine> Lines, List<string> Notes) For(Workspace ws, AssetIndex index, MarkerRecord marker, string? worldName, string language = "english")
    {
        var notes = new List<string>();
        var lines = new List<DialogueLine>();
        var byId = new Dictionary<uint, AssetEntry>();
        foreach (var e in index.Entries) if (e.Id != 0 && !e.Streamed) byId.TryAdd(e.Id, e);
        string NameOf(uint id) => byId.TryGetValue(id, out var e) ? AssetIds.DisplayName(e.Name) : id.ToString("X8");
        // the character key: from the actor objparams in the record (the first objparams whose key is not generic)
        string? key = null;
        foreach (var id in marker.AssetIds.Where(a => a >> 24 == 0x1F))
            if (KeyOf(NameOf(id)) is { } k && !NameOf(id).Contains("strategy_npc")) { key = k; break; }
        if (key == null) { notes.Add("no character objparams in this marker"); return (null, lines, notes); }
        bool Related(string name) => name.Contains(key, StringComparison.OrdinalIgnoreCase);

        // 1. assets reachable from the marker through the character's own objects
        var dialogs = new List<uint>(); var seen = new HashSet<uint>(); var queue = new Queue<(uint Id, int Depth)>();
        foreach (var id in marker.AssetIds) if (Follow.Contains((byte)(id >> 24))) queue.Enqueue((id, 0));
        var data = new Dictionary<uint, CaffFile>();
        while (queue.Count > 0 && seen.Count < 200)
        {
            var (id, depth) = queue.Dequeue();
            if (!seen.Add(id) || !byId.TryGetValue(id, out var e)) continue;
            string nm = AssetIds.DisplayName(e.Name);
            byte type = (byte)(id >> 24);
            // dialogs listed in the record or reached through character-specific objects; other objects only when specific
            if (type == 0x43) { dialogs.Add(id); }
            else if (!(Related(nm) || (depth == 0 && type == 0x19))) continue;
            if (depth >= 4) continue;
            byte[]? d;
            try { var c = ws.LoadResident(e.Bundle & 0xFFFFFF); d = new AssetView(c, e.Symbol).Data(".data"); }
            catch { continue; }
            if (type == 0x43) continue;   // dialogs are read below
            for (int o = 0; o + 4 <= d.Length; o += 4)
            {
                uint v = BE.U32(d, o);
                if (v != id && Follow.Contains((byte)(v >> 24)) && byId.ContainsKey(v)) queue.Enqueue((v, depth + 1));
            }
        }

        // 2. dialog assets: table id and node names → "dialog__<node>"
        var tables = new Dictionary<uint, LocText?>();
        LocText? Table(uint t)
        {
            if (tables.TryGetValue(t, out var lt)) return lt;
            lt = null;
            try
            {
                var p = TablePath(ws, t, language);
                if (File.Exists(p)) { var c = CaffFile.Read(File.ReadAllBytes(p)); lt = LocText.Parse(c.Parts.First(x => c.SectionOf(x).Name == ".data").Data); }
            }
            catch (Exception ex) { notes.Add($"text table {t:X8}: {ex.Message}"); }
            return tables[t] = lt;
        }
        string TableName(uint t)
        {
            try { var p = TablePath(ws, t); if (File.Exists(p)) return AssetIds.DisplayName(CaffFile.Read(File.ReadAllBytes(p)).Symbols[0]).Replace("aid_loctext_banjox_", ""); } catch { }
            return t.ToString("X8");
        }
        var have = new HashSet<(uint, ushort)>();
        void Add(uint t, LocText lt, ushort k, string source)
        {
            if (!have.Add((t, k))) return;
            int i = lt.Strings.FindIndex(s => s.Key == k); if (i < 0) return;
            lines.Add(new DialogueLine { Table = t, TableName = TableName(t), Key = k, Name = lt.Names.GetValueOrDefault(k, ""), Text = lt.Strings[i].Text, Source = source });
        }
        foreach (var did in dialogs)
        {
            var e = byId[did];
            byte[] d;
            try { d = new AssetView(ws.LoadResident(e.Bundle & 0xFFFFFF), e.Symbol).Data(".data"); } catch { continue; }
            if (d.Length < 12) continue;
            uint t = BE.U32(d, 8);
            if (t >> 24 != 0x11 || Table(t) is not { } lt) { notes.Add($"{AssetIds.DisplayName(e.Name)}: text table {t:X8} not found"); continue; }
            var byName = lt.Names.GroupBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase);
            int found = 0;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(System.Text.Encoding.Latin1.GetString(d), "[a-z0-9_]{3,63}"))
            {
                if (byName.TryGetValue("dialog__" + m.Value, out var k) || byName.TryGetValue(m.Value, out k)) { Add(t, lt, k, "dialog " + AssetIds.DisplayName(e.Name).Replace("aid_dialog_banjox_", "")); found++; continue; }
                // a node whose lines are numbered (mrfit_cantafford → dialog__mrfit_cantafford1…4: one picked at random)
                foreach (var (nk, nn) in lt.Names)
                    if (nn.StartsWith("dialog__" + m.Value, StringComparison.OrdinalIgnoreCase) && nn.Length > 8 + m.Value.Length && nn[(8 + m.Value.Length)..].All(char.IsDigit))
                    { Add(t, lt, nk, "dialog " + AssetIds.DisplayName(e.Name).Replace("aid_dialog_banjox_", "")); found++; }
            }
            if (found == 0) notes.Add($"{AssetIds.DisplayName(e.Name)}: no line found by its node names");
        }

        // 3. lines named after the character in the same tables and the world's / act's own tables
        var nameTables = tables.Keys.ToList();
        if (worldName != null)
            foreach (var f in Directory.Exists(Path.Combine(ws.Game.Root, "Debug", "11")) ? Directory.GetFiles(Path.Combine(ws.Game.Root, "Debug", "11"), "*", SearchOption.AllDirectories) : Array.Empty<string>())
            {
                try
                {
                    var c = CaffFile.Read(File.ReadAllBytes(f));
                    var tn = AssetIds.DisplayName(c.Symbols[0]).Replace("aid_loctext_banjox_", "");
                    if (!(tn.StartsWith(worldName + "_") || tn.StartsWith("tt_" + worldName + "_") || tn == "jinjos" || tn == "actor_hitdialogs")) continue;
                    if (AssetIds.IdOf(c.Symbols[0]) is uint tid && !nameTables.Contains(tid)) nameTables.Add(tid);
                }
                catch { }
            }
        var keyRx = new System.Text.RegularExpressions.Regex($"(^|_){System.Text.RegularExpressions.Regex.Escape(key)}",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (var t in nameTables)
            if (Table(t) is { } lt)
                foreach (var (k, n) in lt.Names)
                {
                    int sep = n.IndexOf("__");
                    if (keyRx.IsMatch(sep >= 0 ? n[(sep + 2)..] : n)) Add(t, lt, k, "name");
                }
        return (key, lines, notes);
    }

    /// <summary>Writes edited lines back into their tables (one file per table and language), through the workspace (old
    /// version kept in its history, hard links to the original game broken). Returns the number of tables written.</summary>
    public static int Save(Workspace ws, IEnumerable<DialogueLine> edited, string language = "english")
    {
        int n = 0;
        foreach (var g in edited.GroupBy(l => l.Table))
        {
            var path = TablePath(ws, g.Key, language);
            var c = CaffFile.Read(File.ReadAllBytes(path));
            var part = c.Parts.First(x => c.SectionOf(x).Name == ".data");
            var lt = LocText.Parse(part.Data);
            if (!lt.Editable) throw new InvalidDataException($"text table {g.Key:X8} has a layout that is not editable yet");
            foreach (var l in g)
            {
                int i = lt.Strings.FindIndex(s => s.Key == l.Key);
                if (i >= 0) lt.Strings[i] = (l.Key, l.Text);
            }
            part.Data = lt.Write();
            if (LocText.Parse(part.Data).Strings.Count != lt.Strings.Count) throw new InvalidDataException("text table failed validation");
            ws.SaveFile(path, c.Write(), $"edited {g.Count()} line(s) of text table {AssetIds.DisplayName(c.Symbols[0])} ({language})");
            n++;
        }
        return n;
    }
}
