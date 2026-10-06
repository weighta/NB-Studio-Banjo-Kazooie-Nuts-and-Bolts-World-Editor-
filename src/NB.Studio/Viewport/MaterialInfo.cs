using System.Numerics;
using NB.Core.Models;

namespace NB.Studio.Viewport;

/// <summary>How a material is composited (from the stream's section flags, see <see cref="MeshDraw.SectionFlags"/>).</summary>
public enum BlendKind
{
    /// <summary>Section flags low nibble 1: solid.</summary>
    Opaque,
    /// <summary>Low nibble 0 (render states 0x3C/0x40 toggled): alpha-tested cut-outs (grilles, foliage, signs).</summary>
    Cutout,
    /// <summary>Low nibble 2 with SRC_ALPHA / ONE_MINUS_SRC_ALPHA: glass, petals, decals, dirt.</summary>
    Blend,
    /// <summary>A blended section whose only texture is a contact-occlusion map: darkens what is below it.</summary>
    Multiply,
    /// <summary>Section flags bit 0x100000: light beams, holograms (glow added to the frame).</summary>
    Additive,
}

/// <summary>
/// The viewer's reading of a draw's material: which bound texture plays which role (by the texture names, the only
/// reliable hint without translating the Xenos pixel shaders), the colour constants and the blend mode.
///
/// Constants (verified on Showdown Town's 8,553 draws): the lit colour pass (pass 0 of the 3-way pass switch) of a
/// shader with specular has c0 = (specular power, 0, 0, 1), c5 = specular colour and c6 = diffuse tint (alpha =
/// opacity, e.g. glass 0.34); shaders without specular have c0 = (0, 0, 0, 1) and c5 = diffuse tint (petal colours,
/// untextured grey, contact-occlusion strength). Vertex colours are 0xAARRGGBB: RGB baked shading / tint, A blend alpha.
/// </summary>
public sealed class MaterialInfo
{
    public string? Base, Overlay, Mask, Normal, Spec, Reflect, Ao, AlphaTex;
    public Vector3 OverTint = Vector3.One;
    public Vector3 Tint = Vector3.One;
    public float Opacity = 1;
    public Vector3 SpecColour = new(0.25f);
    public float SpecPower;
    /// <summary>Environment reflection amount (from the (strength, 1.001, 0.001, 1) constant), -1 when not given.</summary>
    public float ReflectStrength = -1;
    public Vector3 ReflectColour = Vector3.One;
    public BlendKind Blend;
    /// <summary>The draw's vertex colours are used (RGB multiply, A = alpha in blended sections).</summary>
    public bool VertexColour;
    /// <summary>The contact-occlusion map uses the second UV set.</summary>
    public bool AoUv2;
    /// <summary>Layer-blend mask / separate alpha mask on the second UV set (plaza blend maps, pavement-edge strips).</summary>
    public bool MaskUv2, AlphaUv2;
    /// <summary>An alpha-tested section whose render states also blend (SRC_ALPHA / ONE_MINUS_SRC_ALPHA).</summary>
    public bool CutoutBlends;
    public bool Untextured => Base == null;
    /// <summary>The draw's colour shader translated from its Xenos microcode (null: not translatable, the roles above are used).</summary>
    public ShaderTranslation? Shader;
    /// <summary>Texture bound to each sampler the translated shader fetches.</summary>
    public readonly Dictionary<int, string> SamplerTextures = new();

    public string Key => string.Join("|", Base, Overlay, Mask, Normal, Spec, Reflect, Ao, AlphaTex, OverTint, Tint, Opacity, SpecColour, SpecPower, ReflectStrength, ReflectColour, Blend, VertexColour, AoUv2, MaskUv2, AlphaUv2)
        + (Shader == null ? "" : "|" + Shader.Key + "|" + Shader.NormalSet + Shader.NormalRows + "|" + string.Join(",", Shader.ConstValues) + "|" + string.Join(",", SamplerTextures.OrderBy(k => k.Key).Select(k => k.Key + ":" + k.Value)));

    /// <summary>Set false to draw every material with the name-based roles (comparison runs).</summary>
    public static bool UseShaderTranslation = Environment.GetEnvironmentVariable("NB_NO_SHADER_XLATE") != "1";

    static string Stem(string t) => ObjExporter.TextureFileStem(t);
    static string Short(string t)
    {
        var s = Stem(t).Replace("aid_texture_banjox_", "");
        int i = s.LastIndexOf("_0x", StringComparison.Ordinal);
        return i > 0 ? s[..i] : s;
    }

    /// <summary>Two textures of one material ("…grill1_colour" / "…grill1_transparency").</summary>
    static bool SameMaterial(string a, string b)
    {
        string P(string t) { var s = Short(t); int i = s.LastIndexOf('_'); return i > 0 ? s[..i] : s; }
        return P(a) == P(b);
    }

    public enum Role { Colour, Normal, Spec, Ao, Mask, Reflect, Ignore }

    /// <summary>Role of a texture from its name.</summary>
    public static Role RoleOf(string texture)
    {
        var n = Short(texture);
        var last = n[(n.LastIndexOf('_') + 1)..];
        if (n.Contains("ambientocclusion") || last.EndsWith("ao") || last.Contains("ambient") || last == "occlusion") return Role.Ao;
        if (n.Contains("velvet") || last.StartsWith("blacktowhite") || last == "editable" || last == "parallax" || last == "height" || last == "colourchange" || last == "multiply") return Role.Ignore;
        if (last.Contains("normal") || last is "nm" or "norm" or "bump" or "bmp" || last.EndsWith("nm") || last.EndsWith("bump") || last.EndsWith("bmp") || last.EndsWith("normals")) return Role.Normal;
        if (last.StartsWith("spec") || last.EndsWith("specmask")) return Role.Spec;
        if (n.Contains("reflectionmaps") || last.Contains("ref1") || last.StartsWith("hilight") || last.StartsWith("glasshighlights") || last == "egggold" || last == "blurrymetal") return Role.Reflect;
        if (last.Contains("colourandtrans")) return Role.Colour;
        if (n.StartsWith("shared_masks_") || n.Contains("_blends_") || last.Contains("trans") || last.Contains("transp") || last.Contains("mask") || last.Contains("alpha")) return Role.Mask;
        return Role.Colour;
    }

    public static MaterialInfo Of(MeshDraw d)
    {
        var m = new MaterialInfo();
        // the lit colour pass (0); models without a pass switch only have the running state
        var pass = d.Passes.Length > 0 ? d.Passes[0] : null;
        var tex0 = (pass != null && pass.Textures.Count > 0 ? pass.Textures : d.Textures).GroupBy(t => t.Slot).Select(g => g.Last()).OrderBy(t => t.Slot).ToList();
        var tex = tex0.Select(t => t.Texture).ToList();
        var consts = pass?.Constants ?? d.PixelConstants;

        var colours = new List<string>();
        foreach (var t in tex)
            switch (RoleOf(t))
            {
                case Role.Colour: if (!colours.Any(c => Stem(c) == Stem(t))) colours.Add(t); break;
                case Role.Normal: m.Normal ??= t; break;
                case Role.Spec: m.Spec ??= t; break;
                case Role.Ao: m.Ao ??= t; break;
                case Role.Mask: m.Mask ??= t; break;
                case Role.Reflect: m.Reflect ??= t; break;
            }
        m.Base = colours.FirstOrDefault();
        if (colours.Count >= 2 && m.Mask != null)
        {
            // layered: a second colour blended over the first by the mask (painted wood, terrain path over grass)
            m.Overlay = colours[1];
        }
        else if (m.Mask != null && m.Base != null) { m.AlphaTex = m.Mask; m.Mask = null; }
        else if (m.Base == null && m.Mask != null) { m.Base = m.Mask; m.AlphaTex = m.Mask; m.Mask = null; }   // petals: a shape texture tinted by c5

        // colour constants
        var c0 = consts.TryGetValue(0, out var a0) ? a0 : new Vector4(0, 0, 0, 1);
        bool specShader = c0.X > 1.5f && c0.Y == 0 && c0.Z == 0;
        // only colour-like constants are tints: parallax shaders keep (4, 0, 0, …) and reflective metals
        // (strength, 1.001, 0.001, 1) in the same registers
        static bool ColourLike(Vector4 v) => v.X is >= 0 and <= 1.0001f && v.Y is >= 0 and <= 1.0001f && v.Z is >= 0 and <= 1.0001f;
        Vector4 tint = Vector4.One;
        // parallax shaders keep their parameters in c0 and move the specular power to c5 (c6 specular, c7 tint)
        var c5p = consts.TryGetValue(5, out var p5) ? p5 : Vector4.Zero;
        bool parallax = !specShader && c5p.X > 1.5f && c5p.Y == 0 && c5p.Z == 0 && c5p.W == 1;
        if (specShader || parallax)
        {
            int b = parallax ? 6 : 5;
            m.SpecPower = parallax ? c5p.X : c0.X;
            var sc = consts.TryGetValue(b, out var vs) ? vs : new Vector4(0.25f);
            m.SpecColour = ColourLike(sc) ? new Vector3(sc.X, sc.Y, sc.Z) : new Vector3(0.25f);
            if (consts.TryGetValue(b + 1, out var vt) && ColourLike(vt)) tint = vt;
        }
        else if (consts.TryGetValue(5, out var v5) && ColourLike(v5)) tint = v5;
        // reflection parameters: (strength, 1.001, 0.001, 1) in one of c6..c12
        foreach (var (reg, v) in consts.OrderBy(k => k.Key))
            if (reg >= 6 && MathF.Abs(v.Y - 1.001f) < 0.0005f && MathF.Abs(v.Z - 0.001f) < 0.0005f) { m.ReflectStrength = Math.Clamp(v.X, 0, 1); break; }
        if (consts.TryGetValue(7, out var r7) && ColourLike(r7) && new Vector3(r7.X, r7.Y, r7.Z).LengthSquared() > 1e-4f) m.ReflectColour = new Vector3(r7.X, r7.Y, r7.Z);
        var rgb = new Vector3(tint.X, tint.Y, tint.Z);
        if (m.Overlay != null)
        {
            // layered materials: the tint is the paint colour of the painted (lighter) layer — Mumbo's Motors' yellow on
            // painted wood over plain wood, blue plaster over bricks; the renderer picks the layer by brightness
            m.OverTint = rgb.LengthSquared() < 1e-6f ? Vector3.One : rgb;
            rgb = Vector3.One; tint.W = 1;
        }
        // a black tint: polished metals are reflection only (pipes, cogs); a textured material without a reflection map takes its
        // colour from runtime constants (the glowing Jiggy path): show the texture
        if (m.Base != null && rgb.LengthSquared() < 1e-6f && m.Reflect == null) rgb = Vector3.One;
        m.Tint = Vector3.Clamp(rgb, Vector3.Zero, new Vector3(4));

        // blend mode
        int mode = (int)(d.SectionFlags & 0xF);
        var states = pass?.States;
        bool blendStates = states != null && states.TryGetValue(0x48, out var sb) && sb == 6 && states.TryGetValue(0x4C, out var db) && db == 7;
        if (mode == 2 || (mode == 2 && blendStates))
        {
            bool aoOnly = m.Ao != null && m.Base == null && m.Normal == null;
            m.Blend = aoOnly ? BlendKind.Multiply : BlendKind.Blend;
            m.Opacity = tint.W > 0.01f && tint.W < 0.999f ? tint.W : 1;
        }
        else if (mode == 0 && d.SectionFlags != 0) { m.Blend = BlendKind.Cutout; m.CutoutBlends = blendStates; }
        else
        {
            // opaque sections ignore masks as alpha (the sky dome's sun-glow mask on the second UV set cut holes in the sky)
            m.Blend = m.Base != null && Short(m.Base).Contains("colourandtrans") ? BlendKind.Cutout : BlendKind.Opaque;
            m.AlphaTex = null;
        }
        if ((d.SectionFlags & 0x100000) != 0)
        {
            // glow effects: drawn additively; the torch beams of the police station are barely visible by day
            m.Blend = BlendKind.Additive;
            float a = consts.TryGetValue(5, out var g5) && g5.W > 0 && g5.W <= 1 ? g5.W : 0.5f;
            m.Opacity = a * 0.08f;
        }
        if (m.Blend == BlendKind.Multiply) { m.Base = null; m.Tint = Vector3.One; }
        m.VertexColour = d.Colors != null;
        m.AoUv2 = m.Ao != null && d.UVs2 != null;
        m.MaskUv2 = m.Mask != null && d.UVs2 != null;
        // glow sections (torch beams, holograms) keep the faint additive look: their shaders output full-strength colour
        // that the game scales at run time
        if (UseShaderTranslation && m.Blend != BlendKind.Additive && d.ColourShader is { } sh)
        {
            // texture-coordinate transforms of the vertex shader (per-texture tiling)
            Dictionary<int, XenosShader.UvSource>? uvs = null;
            try { if (d.ColourVertexShader is { } vs && pass != null) uvs = XenosShader.InterpolatorUvs(vs, pass.VsConstants, d.Layout); } catch { uvs = null; }
            var tr = XenosTranslator.Translate(sh, consts, d.Colors != null, d.UVs2 != null, uvs, d.UVs3 != null);
            if (tr.Fail == null)
            {
                var byslot = tex0.ToDictionary(x => x.Slot, x => x.Texture);
                bool ok = true;
                // the normal-map guess of the dataflow analysis is dropped when the bound texture is plainly something else
                // (a tree's specular map, a roof's second colour layer)
                if (tr.NormalSampler >= 0 && byslot.TryGetValue(tr.NormalSampler, out var nt) && !nt.StartsWith('#') && RoleOf(nt) is Role.Colour or Role.Spec)
                    tr.NormalSampler = -1;
                // a slot without a named texture (#mip/top) is an engine texture (environment cube): left unbound (black)
                foreach (var k in tr.Samplers.Append(tr.NormalSampler).Where(k => k >= 0))
                    if (byslot.TryGetValue(k, out var tn)) { if (!tn.StartsWith('#')) m.SamplerTextures[k] = tn; } else ok = false;
                if (ok) m.Shader = tr;
            }
        }
        // a mask of another material (pavement edge over flagstones) runs along the second UV set; a texture's own transparency map (grille) does not
        m.AlphaUv2 = m.AlphaTex != null && d.UVs2 != null && m.AlphaTex != m.Base && (m.Base == null || !SameMaterial(m.Base, m.AlphaTex));
        return m;
    }
}
