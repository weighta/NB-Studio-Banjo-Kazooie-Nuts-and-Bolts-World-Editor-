using System.Text;
using System.Text.Json;
using NB.Core.Formats;
using NB.Core.IO;
using NB.Core.Project;

namespace NB.Core.Tags;

/// <summary>
/// aid_objparams_* assets are fixed-size C structs, one layout per class. The class is named by the string at 0x42
/// ("objDefId_…"); the tag ("objTag_…") is at 0x02. The executable has no field-name table, so layouts are inferred by
/// comparing every instance of a class: 64-byte string fields, pointers (from relocations), asset references (values
/// that are asset ids), floats, small integers and hashes. Field names come from <see cref="KnownFields"/> only where a
/// meaning has been verified; everything else is labelled by type and offset.
/// </summary>
public sealed class ObjField
{
    public int Offset { get; set; }
    public int Size { get; set; }                 // 2, 4 or string length
    public string Kind { get; set; } = "hex";     // str, u16, float, int, hex, assetref, ptr
    public bool Varies { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
    public List<string> Samples { get; set; } = new();  // distinct values seen (strings: up to 64)
    public string? Name { get; set; }             // verified meaning, if any
}

public sealed class ObjClassSchema
{
    public string Class { get; set; } = "";
    public int Size { get; set; }
    public int Instances { get; set; }
    public List<ObjField> Fields { get; set; } = new();
    public ObjField? FieldAt(int off) => Fields.FirstOrDefault(f => off >= f.Offset && off < f.Offset + Math.Max(4, f.Size));
}

public static class ObjParamsSchema
{
    public const int ClassOffset = 0x42, TagOffset = 0x02;

    /// <summary>Verified field meanings: (class, offset) → name. Each entry records how it was confirmed.</summary>
    public static readonly Dictionary<(string Class, int Offset), string> KnownFields = new()
    {
        [("objDefId_vehicleBlockEngine", 0x3FC)] = "engine power (verified in Xenia: 0 → trolley barely moves, 10× → faster)",
        [("objDefId_vehicleBlockEngine", 0x3D0)] = "engine force/torque (edited with power; 200/300/450 for small/medium/large)",
        [("objDefId_vehicleBlockEngine", 0xA0)] = "part id",
        [("objDefId_vehicleBlockEngine", 0xE0)] = "shop description",
        [("objDefId_vehicleBlockEngine", 0x130)] = "colour",
        [("objDefId_vehicleBlockEngine", 0x150)] = "'seen in shop' game flag",
        [("objDefId_entityStateWalk", 0xF0)] = "walk band speed (walk 2.5 / jog 4.5 / run 8.2 for Banjo; not in-game verified)",
    };

    public static string ClassOf(byte[] data) => data.Length > ClassOffset + 8 ? BE.CStr(data, ClassOffset, 62) : "";
    public static string TagOf(byte[] data) => data.Length > TagOffset + 8 ? BE.CStr(data, TagOffset, 62) : "";
    public static bool IsObjParams(byte[] data) => ClassOf(data).StartsWith("objDefId_");

    static bool Printable(byte b) => b >= 0x20 && b < 0x7F;

    /// <summary>Infers one class layout from its instances (data + pointer offsets).</summary>
    public static ObjClassSchema Infer(string cls, List<(byte[] Data, HashSet<int> Ptrs)> inst, HashSet<uint> assetIds)
    {
        int size = inst.Min(i => i.Data.Length);
        var s = new ObjClassSchema { Class = cls, Size = size, Instances = inst.Count };
        var covered = new bool[size];

        // 1) string fields: even offsets where some instance starts a >=2 char identifier after a NUL (or at 2),
        //    and every instance holds either a clean C string or zeros there.
        var starts = new SortedSet<int>();
        foreach (var (d, _) in inst)
            for (int o = 2; o + 2 < size; o += 2)
                if (char.IsLetter((char)d[o]) && Printable(d[o + 1]) && Printable(d[o + 2]) && (d[o - 1] == 0 || o % 4 == 0 || o == TagOffset || o == ClassOffset))
                    starts.Add(o);
        starts.Add(TagOffset); starts.Add(ClassOffset);
        var accepted = new List<int>();
        int textEnd = 0;   // end of the longest text of the last accepted string: starts inside it are not fields
        foreach (int o in starts)
        {
            if (o < textEnd) continue;
            bool ok = true; int maxLen = 0;
            foreach (var (d, ptrs) in inst)
            {
                if (ptrs.Contains(o & ~3)) { ok = false; break; }
                int k = o; while (k < size && d[k] != 0) { if (!Printable(d[k])) { ok = false; break; } k++; }
                if (!ok || k >= size) { ok = false; break; }
                maxLen = Math.Max(maxLen, k - o);
            }
            // at least one instance must hold a real identifier (>= 3 characters, mostly letters/digits/_),
            // so float bytes such as 0x42480000 ("BH") are not taken for text
            bool word = inst.Any(i => { var t = BE.CStr(i.Data, o, 64); return t.Length >= 3 && t.Count(c => char.IsLetterOrDigit(c) || c == '_') >= t.Length * 0.8; });
            if (ok && maxLen >= 3 && word) { accepted.Add(o); textEnd = o + maxLen + 1; }
        }
        for (int a = 0; a < accepted.Count; a++)
        {
            int o = accepted[a];
            int next = a + 1 < accepted.Count ? accepted[a + 1] : size;
            // extent: up to 64 bytes (the usual char[64]) but never past the next string, and only over bytes that are
            // zero or string text in every instance
            int len = Math.Min(64, next - o);
            if (o == TagOffset || o == ClassOffset) len = Math.Min(len, 62);
            int ext = 0;
            for (int k = o; k < o + len; k++)
            {
                bool allText = inst.All(i => { int e = Array.IndexOf(i.Data, (byte)0, o); return k < e || i.Data[k] == 0; });
                if (!allText) break;
                ext = k - o + 1;
            }
            ext = Math.Max(ext, inst.Max(i => Array.IndexOf(i.Data, (byte)0, o) - o + 1));
            var vals = inst.Select(i => BE.CStr(i.Data, o, ext)).Distinct().ToList();
            s.Fields.Add(new ObjField { Offset = o, Size = ext, Kind = "str", Varies = vals.Count > 1, Samples = vals.OrderBy(v => v).Take(64).ToList() });
            for (int k = o; k < o + ext && k < size; k++) covered[k] = true;
        }

        // 2) 2-byte fields in front of a string that starts at o+2 (e.g. the header words at 0x00 / 0x40)
        foreach (var f in s.Fields.ToList())
            if (f.Offset % 4 == 2 && !covered[f.Offset - 2])
            {
                var vals = inst.Select(i => (uint)BE.U16(i.Data, f.Offset - 2)).ToList();
                s.Fields.Add(Numeric(f.Offset - 2, 2, "u16", vals));
                covered[f.Offset - 2] = covered[f.Offset - 1] = true;
            }

        // 3) remaining words
        for (int o = 0; o + 4 <= size; o += 4)
        {
            if (covered[o] || covered[o + 1] || covered[o + 2] || covered[o + 3]) continue;
            var vals = inst.Select(i => BE.U32(i.Data, o)).ToList();
            if (inst.Any(i => i.Ptrs.Contains(o))) { s.Fields.Add(new ObjField { Offset = o, Size = 4, Kind = "ptr" }); continue; }
            if (vals.All(v => v == 0)) continue;
            string kind;
            if (vals.All(v => v == 0 || assetIds.Contains(v))) kind = "assetref";
            else if (vals.All(v => v < 0x10000 || v == 0xFFFFFFFF)) kind = "int";
            else if (vals.All(v => v == 0 || v == 0x80000000 || ((v >> 23) & 0xFF) is >= 0x60 and <= 0x9F)) kind = "float";
            else kind = "hex";
            s.Fields.Add(Numeric(o, 4, kind, vals));
        }
        s.Fields.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        foreach (var f in s.Fields)
            if (KnownFields.TryGetValue((cls, f.Offset), out var n)) f.Name = n;
        return s;
    }

    static ObjField Numeric(int o, int size, string kind, List<uint> vals)
    {
        var f = new ObjField { Offset = o, Size = size, Kind = kind, Varies = vals.Distinct().Count() > 1 };
        var nums = vals.Select(v => kind == "float" ? (double)BitConverter.Int32BitsToSingle((int)v) : v).ToList();
        f.Min = nums.Min(); f.Max = nums.Max();
        f.Samples = vals.Distinct().Take(16).Select(v => kind switch
        {
            "float" => BitConverter.Int32BitsToSingle((int)v).ToString("G6", System.Globalization.CultureInfo.InvariantCulture),
            "int" or "u16" => v.ToString(),
            _ => "0x" + v.ToString("X8"),
        }).ToList();
        return f;
    }

    // ---- database of all classes (cached in the workspace) ----

    public static string CachePath(Workspace ws) => Path.Combine(ws.Root, "cache", "objschema.json");

    public static Dictionary<string, ObjClassSchema> LoadOrBuild(Workspace ws, AssetIndex idx, IProgress<(string, double)>? progress = null, bool rebuild = false)
    {
        var path = CachePath(ws);
        if (!rebuild && File.Exists(path))
            try { return JsonSerializer.Deserialize<Dictionary<string, ObjClassSchema>>(File.ReadAllText(path))!; } catch { }
        var db = Build(ws, idx, progress);
        File.WriteAllText(path, JsonSerializer.Serialize(db, new JsonSerializerOptions { WriteIndented = false }));
        return db;
    }

    public static Dictionary<string, ObjClassSchema> Build(Workspace ws, AssetIndex idx, IProgress<(string, double)>? progress = null)
    {
        var ids = idx.Entries.Select(e => e.Id).ToHashSet();
        var byClass = new Dictionary<string, List<(byte[], HashSet<int>)>>();
        var seen = new HashSet<string>();
        var groups = idx.Entries.Where(e => e.Type == "objparams" && e.Symbol > 0 && !e.Streamed).GroupBy(e => e.Bundle).ToList();
        int n = 0;
        foreach (var g in groups)
        {
            progress?.Report(($"objparams schema: bundle {g.Key:x6}", (double)n++ / groups.Count));
            var todo = g.Where(e => seen.Add(e.Name)).ToList();
            if (todo.Count == 0) continue;
            var caff = ws.LoadResident(g.Key);
            var relocs = AssetView.BuildRelocIndex(caff);
            foreach (var e in todo)
            {
                var v = new AssetView(caff, e.Symbol, relocs);
                if (!v.Has(".data")) continue;
                var d = v.Data(".data");
                string cls = ClassOf(d);
                if (!cls.StartsWith("objDefId_")) continue;
                var ptrs = new HashSet<int>();
                for (int o = 0; o + 4 <= d.Length; o += 4) if (v.PtrAt(".data", o) != null) ptrs.Add(o);
                if (!byClass.TryGetValue(cls, out var l)) byClass[cls] = l = new();
                l.Add((d, ptrs));
            }
        }
        return byClass.ToDictionary(kv => kv.Key, kv => Infer(kv.Key, kv.Value, ids));
    }
}
