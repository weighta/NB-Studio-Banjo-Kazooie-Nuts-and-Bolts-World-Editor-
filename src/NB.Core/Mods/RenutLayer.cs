using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NB.Core.Formats;
using NB.Core.IO;

namespace NB.Core.Mods;

/// <summary>
/// Builds NB's executable-mod layer into a reNut (ReXGlue static recompilation) source tree, so editions with
/// executable mods run under reNut like under Xenia.
/// <para>
/// reNut compiles the original game code ahead of time, so a patched word in default.xex does nothing by itself. For
/// every word any <see cref="ExePatches"/> mod replaces inside the compiled code (a "site") the generated code gets a
/// check: when the word in guest memory differs from the original (the edition's default.xex carries the mod), the
/// patched code is run by reNut's NB interpreter (src/nb/nbpatch.cpp) and the function continues where that code
/// returns to original code. Mod code in zero padding ("caves") needs nothing: the interpreter reads it from memory.
/// </para>
/// <para>
/// Two steps around ReXGlue's codegen: <see cref="WriteHooks"/> (before) adds a mid-asm hook at every site and at every
/// place the patched code can continue (so those positions are marked in the generated code), and
/// <see cref="ApplyToGenerated"/> (after) turns the hook calls into the check, a switch of gotos, and labels.
/// </para>
/// </summary>
public static class RenutLayer
{
    /// <summary>A patch site: its original word and the addresses it can continue at (0xFFFFFFFF = function returns).</summary>
    public sealed record Site(uint Address, uint Original, string Mod, List<uint> Exits);

    public const uint Return = 0xFFFFFFFF;

    /// <summary>The compiled code range of default.xex (.text) as reNut sees it.</summary>
    public static (uint Start, uint End) CodeRange(XexFile xex) => (0x821E0000u, 0x821E0000u + 0xB82CC4u);

    /// <summary>Every site of every NB executable mod, with its possible continuations (static walk of the patched
    /// image through the mod code, following calls into mod code one level at a time).</summary>
    public static List<Site> Analyze(byte[] image, uint imageBase, IEnumerable<ExeMod> mods, (uint Start, uint End) code)
    {
        var patched = (byte[])image.Clone();
        var modOf = new Dictionary<uint, string>(); var orig = new Dictionary<uint, uint>();
        foreach (var m in mods)
            foreach (var w in m.Words)
            {
                int o = (int)(w.Address - imageBase);
                if (o < 0 || o + 4 > patched.Length) continue;
                BE.W32(patched, o, w.Patched);
                modOf.TryAdd(w.Address, m.Id); orig.TryAdd(w.Address, w.Original);
            }
        uint W(uint va) { int o = (int)(va - imageBase); return o >= 0 && o + 4 <= patched.Length ? BE.U32(patched, o) : 0; }
        bool Original(uint va) => va >= code.Start && va < code.End && !modOf.ContainsKey(va) && W(va) != 0;

        var sites = new List<Site>();
        foreach (var site in modOf.Keys.Where(a => a >= code.Start && a < code.End && orig[a] != 0).OrderBy(a => a))
        {
            var exits = new SortedSet<uint>(); var seen = new HashSet<(uint, int)>(); var work = new Stack<(uint Pc, int Depth)>();
            work.Push((site, 0));
            while (work.Count > 0)
            {
                var (pc, depth) = work.Pop();
                if (!seen.Add((pc, depth)) || depth > 8 || seen.Count > 20000) continue;
                if (pc != site && depth == 0 && Original(pc)) { exits.Add(pc); continue; }
                uint x = W(pc); int op = (int)(x >> 26);
                if (op == 18)
                {
                    int li = (int)(x & 0x3FFFFFC); if ((li & 0x2000000) != 0) li -= 0x4000000;
                    uint t = (x & 2) != 0 ? (uint)li : pc + (uint)li;
                    if ((x & 1) != 0) { if (!Original(t)) work.Push((t, depth + 1)); work.Push((pc + 4, depth)); }
                    else if (depth == 0 && Original(t)) exits.Add(t);
                    else if (!Original(t)) work.Push((t, depth));
                    continue;
                }
                if (op == 16)
                {
                    int bd = (short)(x & 0xFFFC); uint t = (x & 2) != 0 ? (uint)bd : pc + (uint)bd; int bo = (int)((x >> 21) & 31);
                    if ((x & 1) != 0) { if (!Original(t)) work.Push((t, depth + 1)); work.Push((pc + 4, depth)); continue; }
                    if (depth == 0 && Original(t)) exits.Add(t); else if (!Original(t)) work.Push((t, depth));
                    if ((bo & 0x14) != 0x14) work.Push((pc + 4, depth));
                    continue;
                }
                if (op == 19)
                {
                    int xo = (int)((x >> 1) & 0x3FF); int bo = (int)((x >> 21) & 31);
                    if (xo == 16 && (x & 1) == 0)
                    {
                        if (depth == 0) exits.Add(Return);
                        if ((bo & 0x14) != 0x14) work.Push((pc + 4, depth));
                        continue;
                    }
                    if (xo == 528 && (x & 1) == 0) { if (depth == 0) exits.Add(Return); continue; }
                }
                work.Push((pc + 4, depth));
            }
            sites.Add(new Site(site, orig[site], modOf[site], exits.ToList()));
        }
        return sites;
    }

    static string Hex(uint a) => a.ToString("X8");

    /// <summary>Writes config/nb_hooks.toml (mid-asm hooks: nbsite_X at every site, nbmark_X at every continuation)
    /// and config/nb_layer.json (the sites, for <see cref="ApplyToGenerated"/>), and lists nb_hooks.toml in the codegen
    /// config. <paramref name="xexPath"/> is the untouched default.xex the tree is generated from.</summary>
    public static List<Site> WriteHooks(string renutRoot, string xexPath, IEnumerable<ExeMod>? mods = null)
    {
        var xex = XexFile.Read(File.ReadAllBytes(xexPath));
        var image = xex.GetImage();
        var sites = Analyze(image, xex.ImageBase, mods ?? ExePatches.All, CodeRange(xex));
        var siteSet = sites.Select(s => s.Address).ToHashSet();
        var marks = sites.SelectMany(s => s.Exits).Where(e => e != Return && !siteSet.Contains(e)).Distinct().OrderBy(e => e).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("# NB executable-mod layer (generated by NB.Core RenutLayer.WriteHooks; do not edit).");
        sb.AppendLine("# nbsite_X: a word some NB mod replaces; nbmark_X: where patched code can continue. tools/nb_layer turns the");
        sb.AppendLine("# hook calls into the interpreter check (src/nb/nbpatch.cpp) after codegen.");
        foreach (var s in sites)
            sb.AppendLine($"\n[[midasm_hook]]\naddress = 0x{Hex(s.Address)}\nname = \"nbsite_{Hex(s.Address)}\"\nregisters = []\nafter_instruction = false");
        foreach (var e in marks)
            sb.AppendLine($"\n[[midasm_hook]]\naddress = 0x{Hex(e)}\nname = \"nbmark_{Hex(e)}\"\nregisters = []\nafter_instruction = false");
        Directory.CreateDirectory(Path.Combine(renutRoot, "config"));
        File.WriteAllText(Path.Combine(renutRoot, "config", "nb_hooks.toml"), sb.ToString());
        File.WriteAllText(Path.Combine(renutRoot, "config", "nb_layer.json"), JsonSerializer.Serialize(sites, new JsonSerializerOptions { WriteIndented = true }));
        // list the hooks in the codegen config
        var cfg = Path.Combine(renutRoot, "renut_config.toml");
        var text = File.ReadAllText(cfg);
        if (!text.Contains("config/nb_hooks.toml"))
        {
            text = Regex.Replace(text, @"includes\s*=\s*\[", m => m.Value + "\n\"config/nb_hooks.toml\",", RegexOptions.None, TimeSpan.FromSeconds(1));
            File.WriteAllText(cfg, text);
        }
        return sites;
    }

    static readonly Regex FuncStart = new(@"^DEFINE_REX_FUNC\((\w+)\)", RegexOptions.Multiline);
    static readonly Regex HookCall = new(@"^(\s*)nb(site|mark)_([0-9A-F]{8})\(\);\s*$", RegexOptions.Multiline);
    static readonly Regex HookDecl = new(@"^extern void nb(site|mark)_[0-9A-F]{8}\(\);\r?\n", RegexOptions.Multiline);

    /// <summary>After codegen: replaces the nbsite/nbmark hook calls in generated/*.cpp with the interpreter check,
    /// the continuation switch and labels. Idempotent (already converted files have no hook calls left). Returns the
    /// number of sites converted.</summary>
    public static int ApplyToGenerated(string renutRoot, string generatedDir = "generated")
    {
        var sites = JsonSerializer.Deserialize<List<Site>>(File.ReadAllText(Path.Combine(renutRoot, "config", "nb_layer.json")))!
            .ToDictionary(s => s.Address);
        int converted = 0;
        foreach (var file in Directory.GetFiles(Path.Combine(renutRoot, generatedDir), "*.cpp"))
        {
            var src = File.ReadAllText(file);
            if (!src.Contains("nbsite_") && !src.Contains("nbmark_")) continue;
            src = HookDecl.Replace(src, "");
            // per function: which labels exist (sites and marks), so a site only jumps to labels of its own function
            var starts = FuncStart.Matches(src).Select(m => m.Index).Append(src.Length).ToList();
            var outText = new StringBuilder(src.Length + 4096);
            outText.Append(src, 0, starts[0]);
            for (int f = 0; f + 1 < starts.Count; f++)
            {
                string body = src[starts[f]..starts[f + 1]];
                var labels = HookCall.Matches(body).Select(m => Convert.ToUInt32(m.Groups[3].Value, 16)).ToHashSet();
                body = HookCall.Replace(body, m =>
                {
                    string ind = m.Groups[1].Value; uint a = Convert.ToUInt32(m.Groups[3].Value, 16);
                    if (m.Groups[2].Value == "mark") return $"nbl_{Hex(a)}:";
                    var s = sites[a];
                    var jumps = s.Exits.Where(e => e != Return && labels.Contains(e)).ToList();
                    bool ret = s.Exits.Contains(Return);
                    converted++;
                    var b = new StringBuilder();
                    b.Append($"nbl_{Hex(a)}:\n");
                    b.Append($"{ind}if (REX_LOAD_U32(0x{Hex(a)}) != 0x{Hex(s.Original)}u) {{   // NB mod {s.Mod}\n");
                    b.Append($"{ind}\tstatic const uint32_t nbx[] = {{ {(jumps.Count == 0 ? "0" : string.Join(", ", jumps.Select(e => $"0x{Hex(e)}u")))} }};\n");
                    b.Append($"{ind}\tswitch (nbpatch::Run(ctx, base, 0x{Hex(a)}u, nbx, {jumps.Count})) {{\n");
                    foreach (var e in jumps) b.Append($"{ind}\tcase 0x{Hex(e)}u: goto nbl_{Hex(e)};\n");
                    b.Append($"{ind}\tcase nbpatch::kFallback: break;\n");
                    b.Append($"{ind}\tdefault: return;\n");
                    b.Append($"{ind}\t}}\n{ind}}}");
                    return b.ToString();
                });
                outText.Append(body);
            }
            var result = outText.ToString();
            if (!result.Contains("nb/nbpatch.h"))
            {
                // after the last #include of the file's preamble
                int at = result.IndexOf("\n", result.LastIndexOf("#include", starts[0] > 0 ? starts[0] - 1 : 0, StringComparison.Ordinal), StringComparison.Ordinal) + 1;
                result = result.Insert(at, "#include \"nb/nbpatch.h\"\n");
            }
            File.WriteAllText(file, result);
        }
        return converted;
    }
}
