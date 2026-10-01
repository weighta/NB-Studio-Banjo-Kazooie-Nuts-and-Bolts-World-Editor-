using NB.Core.Compression;
using NB.Core.Formats;
using NB.Core.Textures;

namespace NB.Core.Project;

/// <summary>
/// Structural validation of every modified file in a workspace: resident bundles must parse as CAFF (or
/// decompress to one) with a valid header checksum and exact size accounting; stream archives must parse
/// and every CAFF inside must be valid; textures must still match their layout sizes. Produces a text report.
/// </summary>
public static class BuildValidator
{
    public static List<string> Validate(Workspace ws)
    {
        var rep = new List<string>();
        var files = ws.ModifiedFiles();
        rep.Add($"=== Validation report — {DateTime.Now:yyyy-MM-dd HH:mm} ===");
        rep.Add($"Workspace: {ws.Root}");
        rep.Add($"Modified files: {files.Count}");
        int errors = 0, warnings = 0;
        foreach (var rel in files)
        {
            var path = Path.Combine(ws.Game.Root, rel);
            var norm = rel.Replace('/', '\\');
            try
            {
                var d = File.ReadAllBytes(path);
                if (norm.StartsWith(@"Bundle\4f\", StringComparison.OrdinalIgnoreCase))
                {
                    bool compressed = XCompressFile.IsCompressed(d);
                    var raw = compressed ? XCompressFile.Decompress(d) : d;
                    if (!CaffFile.VerifyHeaderChecksum(raw)) { rep.Add($"ERROR {rel}: CAFF header checksum invalid"); errors++; continue; }
                    var c = CaffFile.Read(raw);
                    var again = c.Write();
                    bool canonical = again.AsSpan().SequenceEqual(raw);
                    int badTex = CheckTextures(c);
                    rep.Add($"OK    {rel}: {(compressed ? "xcompress" : "raw")} CAFF, {c.Symbols.Count} assets, {c.Parts.Count} parts, checksum valid{(canonical ? ", canonical layout" : ", NON-canonical layout (warning)")}{(badTex > 0 ? $", {badTex} texture(s) with layout mismatch" : "")}");
                    if (!canonical) warnings++;
                    if (badTex > 0) warnings++;
                    var orig = Path.Combine(ws.Original.Root, rel);
                    if (File.Exists(orig))
                    {
                        var oc = CaffFile.Read(ws.Original.ReadResidentRaw(Convert.ToUInt32(Path.GetFileName(rel), 16)));
                        var missing = oc.Symbols.Except(c.Symbols).Take(5).ToList();
                        if (missing.Count > 0) { rep.Add($"WARN  {rel}: assets removed vs original: {string.Join(", ", missing)}"); warnings++; }
                        int changedParts = 0;
                        for (int i = 0; i < Math.Min(oc.Parts.Count, c.Parts.Count); i++) if (!oc.Parts[i].Data.AsSpan().SequenceEqual(c.Parts[i].Data)) changedParts++;
                        rep.Add($"      changed parts vs original: {changedParts}");
                    }
                }
                else if (norm.StartsWith(@"Bundle\50\", StringComparison.OrdinalIgnoreCase))
                {
                    var a = BundleArchive.Read(d);
                    int bad = 0;
                    foreach (var e in a.Entries.Where(e => e.Kind == "caff"))
                        if (!CaffFile.VerifyHeaderChecksum(e.Data!)) bad++; else CaffFile.Read(e.Data!);
                    rep.Add(bad == 0 ? $"OK    {rel}: stream archive, {a.Entries.Count} entries, all CAFF checksums valid" : $"ERROR {rel}: {bad} CAFF entries with bad checksum");
                    errors += bad > 0 ? 1 : 0;
                }
                else if (norm.Equals("default.xex", StringComparison.OrdinalIgnoreCase))
                {
                    var x = XexFile.Read(d); x.GetImage();
                    rep.Add($"WARN  {rel}: executable modified (decodes OK). Modified XEX files need a patched kernel/emulator setting to run.");
                    warnings++;
                }
                else rep.Add($"INFO  {rel}: modified (no structural check for this file type)");
            }
            catch (Exception e) { rep.Add($"ERROR {rel}: {e.Message}"); errors++; }
        }
        rep.Add($"Result: {errors} error(s), {warnings} warning(s).");
        rep.Add("Known limitations: collision (havok) data is not updated when scenery is moved; in-game verification must be done in Xenia.");
        return rep;
    }

    static int CheckTextures(CaffFile c)
    {
        int bad = 0;
        for (int s = 1; s <= c.Symbols.Count; s++)
        {
            if (!c.Symbols[s - 1].Contains("aid_texture")) continue;
            var parts = c.PartsOf(s).ToList();
            var cpu = parts.FirstOrDefault(p => c.SectionOf(p).Name == ".data");
            var gpu = parts.FirstOrDefault(p => c.SectionOf(p).Name == ".texturegpu");
            if (cpu == null || gpu == null || !TextureHeader.IsTexture(cpu.Data) || cpu.Data.Length != 0x70) continue;
            try { if (new TextureAsset(cpu.Data, gpu.Data).ExpectedGpuSize != gpu.Data.Length) bad++; } catch { bad++; }
        }
        return bad;
    }
}
