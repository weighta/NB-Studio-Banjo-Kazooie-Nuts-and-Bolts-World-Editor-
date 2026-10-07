using System.Numerics;
using NB.Core.Models;
using OpenTK.Graphics.OpenGL4;
using PixelFormat = OpenTK.Graphics.OpenGL4.PixelFormat;

namespace NB.Studio.Viewport;

/// <summary>
/// Draws vehicle parts for the Vehicle Editor with the game's paint: vehicle part materials carry an "editable" texture
/// (R = paint mask, G = hue variation) and the game passes the part's paint to the pixel shader as HSL in c42; the
/// shader tints the diffuse colour with mix(1, hsl2rgb(H + 0.1·G − 0.05, S, L), R) and the specular with half of that
/// (read from the part shaders, e.g. model_banjox_vehicleparts_wheel_standard: literals c79 = (0.1, −0.05), c104..c106 the
/// HSL→RGB constants). Other materials (rubber, chrome) keep their textures and colour constants.
/// </summary>
public sealed class VehicleRenderer : IDisposable
{
    public Func<string, (byte[] Rgba, int W, int H)?>? TextureSource;

    sealed class Batch
    {
        public int Vao, Vbo, Ebo, Count;
        public string? Base, Ao, Spec, Edit, Nrm;
        public bool AoUv2, Cutout, Blend;
        public Vector3 Tint = Vector3.One, SpecCol = new(0.25f);
        public float SpecPow;
        public int Has; public bool Resolved;
        public readonly int[] Tex = new int[5];
    }

    readonly Dictionary<ModelAsset, Batch[]> _batches = new();
    readonly Dictionary<string, int> _textures = new();
    int _prog, _lineProg, _lineVao, _lineVbo, _uLineMvp;
    readonly Dictionary<string, int> _u = new();
    Matrix4x4 _vp; Vector3 _eye;
    const int Floats = 10;

    const string VS = """
        #version 330 core
        layout(location=0) in vec3 aPos; layout(location=1) in vec3 aN; layout(location=2) in vec2 aUV; layout(location=3) in vec2 aUV2;
        uniform mat4 uMvp; uniform mat4 uModel;
        out vec3 vN; out vec3 vW; out vec2 vUV; out vec2 vUV2;
        void main(){ vec4 w = uModel * vec4(aPos, 1.0); vW = w.xyz; vN = mat3(uModel) * aN; vUV = aUV; vUV2 = aUV2; gl_Position = uMvp * vec4(aPos, 1.0); }
        """;

    const string FS = """
        #version 330 core
        in vec3 vN; in vec3 vW; in vec2 vUV; in vec2 vUV2; out vec4 o;
        uniform sampler2D tBase; uniform sampler2D tAo; uniform sampler2D tSpec; uniform sampler2D tEdit; uniform sampler2D tNrm;
        uniform int uHas;          // 1 base, 2 ao, 4 ao on uv2, 8 spec, 16 editable (paint) mask, 32 normal map, 64 base alpha
        uniform int uBlend;        // 0 opaque, 1 cut-out, 2 blended
        uniform vec3 uTint; uniform vec3 uSpecCol; uniform float uSpecPow;
        uniform vec4 uPaint;       // H, S, L, 1 = paint on
        uniform vec4 uOver;        // overlay colour (selection, ghost), amount
        uniform float uAlpha;      // ghost transparency
        uniform vec3 uEye; uniform vec3 uSunDir; uniform int uFlat;
        float hue(float p, float q, float t) {
            t = fract(t);
            if (t < 1.0/6.0) return p + (q - p) * 6.0 * t;
            if (t < 0.5) return q;
            if (t < 2.0/3.0) return p + (q - p) * (2.0/3.0 - t) * 6.0;
            return p;
        }
        vec3 hsl2rgb(float h, float s, float l) {
            float q = l < 0.5 ? l * (1.0 + s) : l + s - l * s;
            float p = 2.0 * l - q;
            return vec3(hue(p, q, h + 1.0/3.0), hue(p, q, h), hue(p, q, h - 1.0/3.0));
        }
        vec3 perturb(vec3 n, vec3 p, vec2 uv, vec3 t) {
            vec3 dp1 = dFdx(p), dp2 = dFdy(p); vec2 du1 = dFdx(uv), du2 = dFdy(uv);
            vec3 dp2perp = cross(dp2, n), dp1perp = cross(n, dp1);
            vec3 T = dp2perp * du1.x + dp1perp * du2.x; vec3 B = dp2perp * du1.y + dp1perp * du2.y;
            float invmax = inversesqrt(max(dot(T,T), dot(B,B)));
            if (!(invmax < 1e8)) return n;
            vec2 xy = t.xy * 2.0 - 1.0; xy.y = -xy.y; vec3 tn = vec3(xy, sqrt(max(0.0, 1.0 - dot(xy, xy))));
            return normalize(mat3(T * invmax, B * invmax, n) * tn);
        }
        void main(){
          vec3 n = length(vN) > 0.0 ? normalize(vN) : vec3(0,1,0);
          if (!gl_FrontFacing) n = -n;
          vec3 V = normalize(uEye - vW);
          if (uFlat == 1) { o = vec4(uOver.rgb, uAlpha); return; }
          vec4 base = (uHas & 1) != 0 ? texture(tBase, vUV) : vec4(1.0);
          if (uBlend == 1 && (uHas & 64) != 0 && base.a < 0.5) discard;
          vec3 col = base.rgb * uTint;
          vec3 specTint = vec3(1.0);
          if ((uHas & 16) != 0 && uPaint.w > 0.5) {
            vec4 e = texture(tEdit, vUV);
            float h = clamp(uPaint.x + e.g * 0.1 - 0.05, 0.0, 1.0);
            vec3 tint = mix(vec3(1.0), hsl2rgb(h, uPaint.y, uPaint.z), e.r);
            col *= tint;
            specTint = (tint + 1.0) * 0.5;
          }
          if ((uHas & 2) != 0) col *= texture(tAo, (uHas & 4) != 0 ? vUV2 : vUV).r;
          if ((uHas & 32) != 0) n = perturb(n, vW, vUV, texture(tNrm, vUV).rgb);
          vec3 L = normalize(uSunDir);
          float nl = max(dot(n, L), 0.0);
          // a bright garage-like setup: hemisphere ambient, key light, a soft light from the viewer
          vec3 amb = vec3(0.42) * (1.107 + 0.519 * n.y);
          vec3 F = normalize(V + vec3(0.0, 0.35, 0.0));
          vec3 rgb = col * (amb + vec3(0.75) * nl + vec3(0.35) * max(dot(n, F), 0.0));
          if (uSpecPow > 0.0) {
            vec3 specMask = (uHas & 8) != 0 ? texture(tSpec, vUV).rgb : vec3(1.0);
            vec3 H = normalize(L + V);
            float s = pow(max(dot(n, H), 0.0), uSpecPow) * (uSpecPow + 8.0) / 25.0;
            rgb += uSpecCol * specMask * specTint * s * step(0.0, dot(n, L));
          }
          rgb = mix(rgb, uOver.rgb, uOver.a);
          o = vec4(rgb, uBlend == 2 ? base.a * uAlpha : uAlpha);
        }
        """;

    const string LVS = "#version 330 core\nlayout(location=0) in vec3 aPos; layout(location=1) in vec3 aCol; uniform mat4 uMvp; out vec3 vC; void main(){ gl_Position = uMvp*vec4(aPos,1.0); vC=aCol; }";
    const string LFS = "#version 330 core\nin vec3 vC; out vec4 o; void main(){ o = vec4(vC,1.0); }";

    public void Init()
    {
        _prog = Link(VS, FS);
        foreach (var n in new[] { "uMvp", "uModel", "uHas", "uBlend", "uTint", "uSpecCol", "uSpecPow", "uPaint", "uOver", "uAlpha", "uEye", "uSunDir", "uFlat" })
            _u[n] = GL.GetUniformLocation(_prog, n);
        GL.UseProgram(_prog);
        string[] samplers = { "tBase", "tAo", "tSpec", "tEdit", "tNrm" };
        for (int i = 0; i < samplers.Length; i++) GL.Uniform1(GL.GetUniformLocation(_prog, samplers[i]), i);
        _lineProg = Link(LVS, LFS); _uLineMvp = GL.GetUniformLocation(_lineProg, "uMvp");
        _lineVao = GL.GenVertexArray(); _lineVbo = GL.GenBuffer();
        GL.BindVertexArray(_lineVao); GL.BindBuffer(BufferTarget.ArrayBuffer, _lineVbo);
        GL.EnableVertexAttribArray(0); GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 24, 0);
        GL.EnableVertexAttribArray(1); GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 24, 12);
        GL.BindVertexArray(0);
    }

    static int Link(string vs, string fs)
    {
        int v = GL.CreateShader(ShaderType.VertexShader); GL.ShaderSource(v, vs); GL.CompileShader(v);
        GL.GetShader(v, ShaderParameter.CompileStatus, out int ok); if (ok == 0) throw new Exception(GL.GetShaderInfoLog(v));
        int f = GL.CreateShader(ShaderType.FragmentShader); GL.ShaderSource(f, fs); GL.CompileShader(f);
        GL.GetShader(f, ShaderParameter.CompileStatus, out ok); if (ok == 0) throw new Exception(GL.GetShaderInfoLog(f));
        int p = GL.CreateProgram(); GL.AttachShader(p, v); GL.AttachShader(p, f); GL.LinkProgram(p);
        GL.GetProgram(p, GetProgramParameterName.LinkStatus, out ok); if (ok == 0) throw new Exception(GL.GetProgramInfoLog(p));
        GL.DeleteShader(v); GL.DeleteShader(f);
        return p;
    }

    /// <summary>Drops every GPU buffer and texture (workspace changed).</summary>
    public void Clear()
    {
        foreach (var bs in _batches.Values) foreach (var b in bs) { GL.DeleteBuffer(b.Vbo); GL.DeleteBuffer(b.Ebo); GL.DeleteVertexArray(b.Vao); }
        _batches.Clear();
        foreach (var t in _textures.Values) if (t != 0) GL.DeleteTexture(t);
        _textures.Clear();
    }

    public void Dispose()
    {
        Clear();
        if (_prog != 0) GL.DeleteProgram(_prog);
        if (_lineProg != 0) GL.DeleteProgram(_lineProg);
        if (_lineVbo != 0) GL.DeleteBuffer(_lineVbo);
        if (_lineVao != 0) GL.DeleteVertexArray(_lineVao);
    }

    int Texture(string? name)
    {
        if (name == null || TextureSource == null) return 0;
        if (_textures.TryGetValue(name, out int t)) return t;
        t = 0;
        (byte[] Rgba, int W, int H)? img = null;
        try { img = TextureSource(name); } catch { }
        if (img is { } im && im.W > 0 && im.H > 0)
        {
            t = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, t);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, im.W, im.H, 0, PixelFormat.Rgba, PixelType.UnsignedByte, im.Rgba);
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
        }
        _textures[name] = t;
        return t;
    }

    static Vector3 UntexturedColour(MeshDraw d)
    {
        var c = d.Passes.Length > 0 ? d.Passes[0].Constants : d.PixelConstants;
        if (c.TryGetValue(6, out var v) && v.X is > 0.05f and <= 1.5f && MathF.Abs(v.X - v.Y) < 0.3f && MathF.Abs(v.Y - v.Z) < 0.3f) return new Vector3(v.X, v.Y, v.Z) * 0.85f;
        return new Vector3(0.7f);
    }

    /// <summary>Is this texture a paint mask ("…_editable_0x…")?</summary>
    static bool IsEditable(string t)
    {
        var s = ObjExporter.TextureFileStem(t);
        int i = s.LastIndexOf("_0x", StringComparison.Ordinal);
        if (i > 0) s = s[..i];
        return s.EndsWith("_editable", StringComparison.Ordinal);
    }

    Batch[] Batches(ModelAsset model)
    {
        if (_batches.TryGetValue(model, out var bs)) return bs;
        var draws = model.Draws.Where(d => d.Positions.Length > 0 && d.Indices.Length > 0).ToList();
        var lod0 = draws.Where(d => !model.LodOnlyNodes.Contains(d.Node)).ToList();
        if (lod0.Count > 0) draws = lod0;
        var groups = draws.GroupBy(d =>
        {
            var m = MaterialInfo.Of(d);
            return m.Key + "|" + d.Textures.Select(t => t.Texture).FirstOrDefault(IsEditable);
        });
        var list = new List<Batch>();
        foreach (var g in groups)
        {
            var d0 = g.First();
            var m = MaterialInfo.Of(d0);
            var bt = new Batch
            {
                Base = m.Base, Ao = m.Ao, AoUv2 = m.AoUv2, Spec = m.Spec, Nrm = m.Normal,
                Edit = d0.Textures.Select(t => t.Texture).FirstOrDefault(IsEditable),
                Cutout = m.Blend == BlendKind.Cutout, Blend = m.Blend is BlendKind.Blend or BlendKind.Additive or BlendKind.Multiply,
                // vehicle part shaders use c5 / c6 for the specular map (mad spec, c5, c6), not as a diffuse tint: textured
                // materials keep their texture colour; untextured metals (no colour map) are the light grey c6 the shaders add
                Tint = m.Base != null ? Vector3.One : UntexturedColour(d0), SpecCol = new Vector3(0.35f), SpecPow = m.SpecPower > 0 ? m.SpecPower : 24,
            };
            if (m.Blend == BlendKind.Multiply) continue;                 // contact shadows: not needed in the editor
            var buf = new List<float>(); var idx = new List<uint>();
            foreach (var d in g)
            {
                uint b = (uint)(buf.Count / Floats); int n = d.Positions.Length;
                for (int i = 0; i < n; i++)
                {
                    var p = d.Positions[i]; var q = d.Normals != null ? d.Normals[i] : Vector3.Zero;
                    var t = d.UVs != null ? d.UVs[i] : Vector2.Zero; var t2 = d.UVs2 != null ? d.UVs2[i] : t;
                    buf.Add(p.X); buf.Add(p.Y); buf.Add(p.Z); buf.Add(q.X); buf.Add(q.Y); buf.Add(q.Z); buf.Add(t.X); buf.Add(t.Y); buf.Add(t2.X); buf.Add(t2.Y);
                }
                foreach (int i in d.Indices) if (i < n) idx.Add(b + (uint)i);
            }
            if (idx.Count == 0) continue;
            bt.Count = idx.Count;
            bt.Vao = GL.GenVertexArray(); bt.Vbo = GL.GenBuffer(); bt.Ebo = GL.GenBuffer();
            GL.BindVertexArray(bt.Vao);
            var fa = buf.ToArray(); var ia = idx.ToArray();
            GL.BindBuffer(BufferTarget.ArrayBuffer, bt.Vbo); GL.BufferData(BufferTarget.ArrayBuffer, fa.Length * 4, fa, BufferUsageHint.StaticDraw);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, bt.Ebo); GL.BufferData(BufferTarget.ElementArrayBuffer, ia.Length * 4, ia, BufferUsageHint.StaticDraw);
            int st = Floats * 4;
            GL.EnableVertexAttribArray(0); GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, st, 0);
            GL.EnableVertexAttribArray(1); GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, st, 12);
            GL.EnableVertexAttribArray(2); GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, st, 24);
            GL.EnableVertexAttribArray(3); GL.VertexAttribPointer(3, 2, VertexAttribPointerType.Float, false, st, 32);
            GL.BindVertexArray(0);
            list.Add(bt);
        }
        return _batches[model] = list.OrderBy(b => b.Blend ? 1 : 0).ToArray();
    }

    void Resolve(Batch b)
    {
        b.Tex[0] = Texture(b.Base); b.Tex[1] = Texture(b.Ao); b.Tex[2] = Texture(b.Spec); b.Tex[3] = Texture(b.Edit); b.Tex[4] = Texture(b.Nrm);
        int h = 0;
        if (b.Tex[0] != 0) { h |= 1; if (b.Cutout) h |= 64; }
        if (b.Tex[1] != 0) { h |= 2; if (b.AoUv2) h |= 4; }
        if (b.Tex[2] != 0) h |= 8;
        if (b.Tex[3] != 0) h |= 16;
        if (b.Tex[4] != 0) h |= 32;
        b.Has = h; b.Resolved = true;
    }

    public void Begin(Matrix4x4 viewProj, Vector3 eye)
    {
        _vp = viewProj; _eye = eye;
        GL.Enable(EnableCap.DepthTest); GL.DepthMask(true); GL.DepthFunc(DepthFunction.Lequal);
        GL.Disable(EnableCap.CullFace); GL.Disable(EnableCap.Blend);
        GL.UseProgram(_prog);
        GL.Uniform3(_u["uEye"], eye.X, eye.Y, eye.Z);
        var sun = Vector3.Normalize(new Vector3(-0.45f, 0.8f, -0.35f));
        GL.Uniform3(_u["uSunDir"], sun.X, sun.Y, sun.Z);
        GL.Uniform1(_u["uFlat"], 0);
    }

    /// <summary>RGB (0..1) → (H, S, L), the form the game passes to the part shaders.</summary>
    public static Vector3 Hsl(Vector3 c)
    {
        float mx = MathF.Max(c.X, MathF.Max(c.Y, c.Z)), mn = MathF.Min(c.X, MathF.Min(c.Y, c.Z));
        float l = (mx + mn) / 2, h = 0, s = 0;
        if (mx > mn)
        {
            float d = mx - mn;
            s = l > 0.5f ? d / (2 - mx - mn) : d / (mx + mn);
            if (mx == c.X) h = (c.Y - c.Z) / d + (c.Y < c.Z ? 6 : 0);
            else if (mx == c.Y) h = (c.Z - c.X) / d + 2;
            else h = (c.X - c.Y) / d + 4;
            h /= 6;
        }
        return new(h, s, l);
    }

    /// <summary>Draws a part model. <paramref name="paint"/>: RGB 0..1 (null = unpainted materials only);
    /// <paramref name="overlay"/>: rgb + amount (selection highlight); <paramref name="alpha"/> &lt; 1: a see-through ghost.</summary>
    public void DrawModel(ModelAsset model, Matrix4x4 world, Vector3? paint, Vector4 overlay, float alpha = 1, bool flat = false)
    {
        var batches = Batches(model);
        if (batches.Length == 0) return;
        GL.UseProgram(_prog);
        var mvp = world * _vp;
        SetMat("uMvp", mvp); SetMat("uModel", world);
        var hsl = paint is { } p ? Hsl(p) : Vector3.Zero;
        GL.Uniform4(_u["uPaint"], hsl.X, hsl.Y, hsl.Z, paint != null ? 1f : 0f);
        GL.Uniform4(_u["uOver"], overlay.X, overlay.Y, overlay.Z, overlay.W);
        GL.Uniform1(_u["uAlpha"], alpha);
        GL.Uniform1(_u["uFlat"], flat ? 1 : 0);
        bool ghost = alpha < 0.999f;
        if (ghost) { GL.Enable(EnableCap.Blend); GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha); GL.DepthMask(false); }
        foreach (var b in batches)
        {
            if (!b.Resolved) Resolve(b);
            for (int i = 0; i < 5; i++) { GL.ActiveTexture(TextureUnit.Texture0 + i); GL.BindTexture(TextureTarget.Texture2D, b.Tex[i]); }
            GL.Uniform1(_u["uHas"], b.Has);
            GL.Uniform1(_u["uBlend"], b.Blend ? 2 : b.Cutout ? 1 : 0);
            GL.Uniform3(_u["uTint"], b.Tint.X, b.Tint.Y, b.Tint.Z);
            GL.Uniform3(_u["uSpecCol"], b.SpecCol.X, b.SpecCol.Y, b.SpecCol.Z);
            GL.Uniform1(_u["uSpecPow"], b.SpecPow);
            if (b.Blend && !ghost) { GL.Enable(EnableCap.Blend); GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha); GL.DepthMask(false); }
            GL.BindVertexArray(b.Vao);
            GL.DrawElements(PrimitiveType.Triangles, b.Count, DrawElementsType.UnsignedInt, 0);
            if (b.Blend && !ghost) { GL.Disable(EnableCap.Blend); GL.DepthMask(true); }
        }
        if (ghost) { GL.Disable(EnableCap.Blend); GL.DepthMask(true); }
        GL.BindVertexArray(0);
        GL.ActiveTexture(TextureUnit.Texture0);
    }

    void SetMat(string n, Matrix4x4 m)
    {
        var a = new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 };
        GL.UniformMatrix4(_u[n], 1, false, a);
    }

    /// <summary>Coloured lines (world space); <paramref name="onTop"/> ignores depth.</summary>
    public void Lines(IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C)> lines, bool onTop = false, float width = 1.5f)
    {
        if (lines.Count == 0) return;
        var buf = new float[lines.Count * 12];
        int k = 0;
        foreach (var (a, b, c) in lines) { buf[k++] = a.X; buf[k++] = a.Y; buf[k++] = a.Z; buf[k++] = c.X; buf[k++] = c.Y; buf[k++] = c.Z; buf[k++] = b.X; buf[k++] = b.Y; buf[k++] = b.Z; buf[k++] = c.X; buf[k++] = c.Y; buf[k++] = c.Z; }
        GL.UseProgram(_lineProg);
        var m = _vp;
        GL.UniformMatrix4(_uLineMvp, 1, false, new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 });
        GL.BindVertexArray(_lineVao); GL.BindBuffer(BufferTarget.ArrayBuffer, _lineVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, buf.Length * 4, buf, BufferUsageHint.StreamDraw);
        if (onTop) GL.Disable(EnableCap.DepthTest);
        try { GL.LineWidth(width); } catch { }
        GL.DrawArrays(PrimitiveType.Lines, 0, lines.Count * 2);
        if (onTop) GL.Enable(EnableCap.DepthTest);
        GL.BindVertexArray(0);
    }

    /// <summary>The 12 edges of a box.</summary>
    public static void Box(List<(Vector3, Vector3, Vector3)> l, Vector3 mn, Vector3 mx, Vector3 c)
    {
        var p = new Vector3[8];
        for (int i = 0; i < 8; i++) p[i] = new((i & 1) != 0 ? mx.X : mn.X, (i & 2) != 0 ? mx.Y : mn.Y, (i & 4) != 0 ? mx.Z : mn.Z);
        int[] e = { 0, 1, 2, 3, 4, 5, 6, 7, 0, 2, 1, 3, 4, 6, 5, 7, 0, 4, 1, 5, 2, 6, 3, 7 };
        for (int i = 0; i < e.Length; i += 2) l.Add((p[e[i]], p[e[i + 1]], c));
    }
}
