using System.Numerics;
using NB.Core.Models;
using OpenTK.Graphics.OpenGL4;

namespace NB.Studio.Viewport;

/// <summary>The viewport's shading modes (the toggle in the corner of the 3D view).</summary>
public enum ViewMode { Wireframe, Solid, Textured, Rendered }

/// <summary>Lighting of the Rendered mode: the level's light setup (or a neutral default).</summary>
public sealed class SceneLighting
{
    public string Name = "default";
    public Vector3 SunDirection = Vector3.Normalize(new Vector3(-0.5f, 0.7f, -0.3f));
    public Vector3 Sun = Vector3.One, Ambient = new(0.35f);
    public float Intensity = 1.1f;
    public bool Fog;
    public float FogStart = 100, FogEnd = 1000, FogMax = 0.3f;
    public Vector3 FogColour = new(0.8f, 0.82f, 0.9f);

    public static SceneLighting From(NB.Core.World.LevelLighting l) => new()
    {
        Name = l.Name, SunDirection = l.SunDirection, Sun = l.Sun, Ambient = l.Ambient, Intensity = l.Intensity,
        Fog = l.Fog && l.FogEnd > l.FogStart, FogStart = l.FogStart, FogEnd = l.FogEnd, FogMax = l.FogMax, FogColour = l.FogColour,
    };
}

/// <summary>GPU-side resources: shaders, per-material mesh batches and textures. All calls must happen with the GL context current.
/// A frame is <see cref="Begin"/>, any number of <see cref="DrawModel"/> (opaque and cut-out batches draw at once, blended ones are
/// queued), then <see cref="FlushTransparent"/> (contact-occlusion overlays, then blended batches back to front).</summary>
public sealed class Renderer : IDisposable
{
    int _prog, _lineProg, _lineVao, _lineVbo, _ovProg, _ovVao, _ovVbo;
    int _uLineMvp, _uOvScreen;
    readonly Dictionary<string, int> _u = new();
    readonly Dictionary<string, int> _textures = new();
    /// <summary>Textures whose alpha channel is used (some texel below 250).</summary>
    readonly HashSet<int> _hasAlpha = new();
    /// <summary>Mean luminance of each texture (0..1): the lighter layer of a layered material takes its paint tint.</summary>
    readonly Dictionary<int, float> _lum = new();
    public Func<string, (byte[] Rgba, int W, int H)?>? TextureSource;
    public ViewMode Mode = ViewMode.Textured;
    public SceneLighting Lighting = new();
    float _maxAniso;

    const string VS = """
        #version 330 core
        layout(location=0) in vec3 aPos; layout(location=1) in vec3 aNrm; layout(location=2) in vec2 aUV; layout(location=3) in vec2 aUV2;
        layout(location=4) in vec3 aTan; layout(location=5) in vec4 aCol;
        uniform mat4 uMvp; uniform mat4 uModel; uniform mat4 uShadow;
        out vec3 vN; out vec3 vW; out vec2 vUV; out vec2 vUV2; out vec4 vCol; out vec4 vSh;
        void main(){ vec4 w = uModel * vec4(aPos,1.0); vW = w.xyz; vSh = uShadow * w; gl_Position = uMvp * vec4(aPos,1.0); vN = mat3(uModel) * aNrm; vUV = aUV; vUV2 = aUV2; vCol = aCol; }
        """;
    const string FS = """
        #version 330 core
        in vec3 vN; in vec3 vW; in vec2 vUV; in vec2 vUV2; in vec4 vCol; in vec4 vSh; out vec4 o;
        uniform sampler2DShadow tShadow; uniform int uUseShadow;
        float shadowAt(float nl) {
            if (uUseShadow == 0) return 1.0;
            vec3 sc = vSh.xyz / vSh.w * 0.5 + 0.5;
            if (sc.x <= 0.0 || sc.y <= 0.0 || sc.x >= 1.0 || sc.y >= 1.0 || sc.z >= 1.0) return 1.0;
            vec2 ts = 1.0 / vec2(textureSize(tShadow, 0));
            float bias = 0.0004 + 0.0012 * (1.0 - nl);
            float acc = 0.0;
            for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++) acc += texture(tShadow, vec3(sc.xy + vec2(x, y) * ts * 1.25, sc.z - bias));
            return acc / 9.0;
        }
        uniform sampler2D tBase; uniform sampler2D tOver; uniform sampler2D tMask; uniform sampler2D tNrm;
        uniform sampler2D tSpec; uniform sampler2D tRefl; uniform sampler2D tAo; uniform sampler2D tAlpha;
        uniform int uMode;      // 0 wire, 1 solid, 2 textured, 3 rendered, 4 unlit (sky)
        uniform int uHas;       // 1 base, 2 layered, 4 normal, 8 spec, 16 reflection, 32 ao, 64 alpha texture, 128 vertex colour, 256 ao on uv2, 512 base alpha, 1024 alpha tex alpha, 2048 paint tint on the base layer, 4096 mask on uv2, 8192 alpha mask on uv2
        uniform int uBlend;     // 0 opaque, 1 cut-out, 2 blend, 3 multiply
        uniform vec3 uOverTint; uniform vec3 uMatTint; uniform float uOpacity; uniform vec3 uSpecCol; uniform float uSpecPow; uniform float uRefl; uniform vec3 uReflCol;
        uniform vec4 uSel;      // selection / edited tint (rgb, amount)
        uniform vec3 uEye; uniform vec3 uSunDir; uniform vec3 uSunCol; uniform vec3 uAmb;
        uniform vec4 uFog; uniform vec3 uFogCol;     // start, end, max, enabled
        uniform vec3 uWire;
        vec3 perturb(vec3 n, vec3 p, vec2 uv, vec3 t) {
            // cotangent frame from screen-space derivatives (no stored tangents needed)
            vec3 dp1 = dFdx(p), dp2 = dFdy(p); vec2 du1 = dFdx(uv), du2 = dFdy(uv);
            vec3 dp2perp = cross(dp2, n), dp1perp = cross(n, dp1);
            vec3 T = dp2perp * du1.x + dp1perp * du2.x; vec3 B = dp2perp * du1.y + dp1perp * du2.y;
            float invmax = inversesqrt(max(dot(T,T), dot(B,B)));
            if (!(invmax < 1e8)) return n;
            // green = up in the image (a rivet's top edge is green), i.e. towards -v: flip it onto the +v cotangent
            vec2 xy = t.xy * 2.0 - 1.0; xy.y = -xy.y; vec3 tn = vec3(xy, sqrt(max(0.0, 1.0 - dot(xy, xy))));
            return normalize(mat3(T * invmax, B * invmax, n) * tn);
        }
        void main(){
          if (uMode == 0) { o = vec4(mix(uWire, uSel.rgb, uSel.a > 0.0 ? 0.85 : 0.0), 1.0); return; }
          vec3 n = length(vN) > 0.0 ? normalize(vN) : vec3(0,1,0);
          if (!gl_FrontFacing) n = -n;
          vec3 V = normalize(uEye - vW);
          if (uMode == 1) {
            // solid: neutral clay with a studio key + fill light
            vec3 L1 = normalize(V + vec3(0.3, 0.6, 0.0)); float k = max(dot(n, L1), 0.0);
            float f = max(dot(n, normalize(vec3(-0.4, 0.3, 0.5))), 0.0);
            vec3 c = vec3(0.78) * (0.28 + 0.62 * k + 0.18 * f) + vec3(0.12) * pow(max(dot(n, normalize(L1 + V)), 0.0), 24.0);
            o = vec4(mix(c, uSel.rgb, uSel.a), 1.0); return;
          }
          vec4 base = (uHas & 1) != 0 ? texture(tBase, vUV) : vec4(1.0);
          if ((uHas & 512) == 0) base.a = 1.0;
          if ((uHas & 2) != 0) { vec3 o2 = texture(tOver, vUV).rgb; if ((uHas & 2048) != 0) base.rgb *= uOverTint; else o2 *= uOverTint; float m = texture(tMask, (uHas & 4096) != 0 ? vUV2 : vUV).r; base.rgb = mix(base.rgb, o2, m); }
          float alpha = base.a;
          if ((uHas & 64) != 0) { vec4 at = texture(tAlpha, (uHas & 8192) != 0 ? vUV2 : vUV); alpha = (uHas & 1024) != 0 ? at.a : at.r; }
          vec3 col = base.rgb * uMatTint;
          if ((uHas & 128) != 0) { col *= vCol.rgb; if (uBlend >= 2) alpha *= vCol.a; }
          alpha *= uOpacity;
          if (uBlend == 3) {
            // contact occlusion: multiply what is below (blend DST_COLOR, ZERO)
            float ao = (uHas & 32) != 0 ? texture(tAo, (uHas & 256) != 0 ? vUV2 : vUV).r : 1.0;
            if ((uHas & 128) != 0) ao = mix(1.0, ao, vCol.a);
            o = vec4(vec3(ao), 1.0); return;
          }
          if (uBlend == 1 && alpha < 0.5) discard;
          if ((uBlend == 2 || uBlend == 4) && alpha < 0.004) discard;
          if ((uHas & 32) != 0) col *= texture(tAo, (uHas & 256) != 0 ? vUV2 : vUV).r;
          vec3 rgb;
          if (uMode == 4) rgb = col;
          else if (uMode == 2) {
            // textured: albedo with a soft headlight so shapes read
            float l = 0.55 + 0.45 * abs(dot(n, normalize(V + vec3(0.25, 0.5, 0.15))));
            // reflection-only metals have a black diffuse tint: show their texture
            if ((uHas & 16) != 0 && max(uMatTint.r, max(uMatTint.g, uMatTint.b)) < 0.01) col = base.rgb * uReflCol * 0.8 * (((uHas & 128) != 0) ? vCol.rgb : vec3(1.0));
            rgb = col * l;
          } else {
            if ((uHas & 4) != 0) n = perturb(n, vW, vUV, texture(tNrm, vUV).rgb);
            vec3 L = normalize(uSunDir);
            float nl = max(dot(n, L), 0.0);
            float sh = shadowAt(max(dot(length(vN) > 0.0 ? normalize(vN) : n, L), 0.0));
            // hemisphere ambient: the level ambient, a little brighter from the sky
            vec3 amb = uAmb * (0.85 + 0.3 * (n.y * 0.5 + 0.5));
            rgb = col * (amb + uSunCol * nl * sh);
            vec3 specMask = (uHas & 8) != 0 ? texture(tSpec, vUV).rgb : vec3(1.0);
            if (uSpecPow > 0.0) {
              vec3 H = normalize(L + V);
              float s = pow(max(dot(n, H), 0.0), uSpecPow) * (uSpecPow + 8.0) / 25.0;
              rgb += uSpecCol * specMask * uSunCol * s * step(0.0, dot(n, L)) * sh;
            }
            if ((uHas & 16) != 0) {
              vec3 R = reflect(-V, n);
              vec2 ruv = vec2(atan(R.z, R.x) / 6.2831853 + 0.5, 0.5 - asin(clamp(R.y, -1.0, 1.0)) / 3.1415927);
              float strength = uRefl >= 0.0 ? uRefl * (0.5 + 0.5 * dot(specMask, vec3(0.3333))) : (uSpecPow > 0.0 ? 0.6 : 0.35) * dot(specMask * max(uSpecCol, vec3(0.2)), vec3(0.3333));
              // metals (black diffuse) reflect at every angle; other surfaces mostly at grazing angles (Fresnel)
              float fres = max(uMatTint.r, max(uMatTint.g, uMatTint.b)) < 0.01 ? 1.0 : mix(0.3, 1.0, pow(1.0 - max(dot(n, V), 0.0), 3.0));
              rgb += texture(tRefl, ruv).rgb * uReflCol * clamp(strength, 0.0, 0.85) * fres;
            }
            // mild grade matched to the game's frames (7 Showdown Town views: ~10% more saturation, ~4% brighter)
            rgb = mix(vec3(dot(rgb, vec3(0.299, 0.587, 0.114))), rgb, 1.12) * 1.04;
            if (uFog.w > 0.5) {
              float d = length(uEye - vW);
              float f = clamp((d - uFog.x) / max(1.0, uFog.y - uFog.x), 0.0, 1.0) * uFog.z;
              rgb = mix(rgb, uFogCol, f);
            }
          }
          o = vec4(mix(rgb, uSel.rgb, uSel.a), (uBlend == 2 || uBlend == 4) ? alpha : 1.0);
        }
        """;
    const string SVS = "#version 330 core\nlayout(location=0) in vec3 aPos; layout(location=2) in vec2 aUV; uniform mat4 uMvp; out vec2 vUV; void main(){ gl_Position = uMvp*vec4(aPos,1.0); vUV = aUV; }";
    const string SFS = "#version 330 core\nin vec2 vUV; uniform sampler2D tA; uniform int uCut; void main(){ if (uCut == 1 && texture(tA, vUV).a < 0.5) discard; if (uCut == 2 && texture(tA, vUV).r < 0.5) discard; }";
    const string LVS = "#version 330 core\nlayout(location=0) in vec3 aPos; layout(location=1) in vec3 aCol; uniform mat4 uMvp; out vec3 vC; void main(){ gl_Position = uMvp*vec4(aPos,1.0); vC=aCol; }";
    const string LFS = "#version 330 core\nin vec3 vC; out vec4 o; void main(){ o = vec4(vC,1.0); }";
    const string OVS = "#version 330 core\nlayout(location=0) in vec2 aPos; layout(location=1) in vec2 aUV; uniform vec2 uScreen; out vec2 vUV; void main(){ gl_Position = vec4(aPos.x / uScreen.x * 2.0 - 1.0, 1.0 - aPos.y / uScreen.y * 2.0, 0.0, 1.0); vUV = aUV; }";
    const string OFS = "#version 330 core\nin vec2 vUV; uniform sampler2D tOv; out vec4 o; void main(){ o = texture(tOv, vUV); }";

    static readonly string[] Samplers = { "tBase", "tOver", "tMask", "tNrm", "tSpec", "tRefl", "tAo", "tAlpha" };

    public void Init()
    {
        _prog = Link(VS, FS);
        foreach (var n in new[] { "uMvp", "uModel", "uMode", "uHas", "uBlend", "uOverTint", "uMatTint", "uOpacity", "uSpecCol", "uSpecPow", "uRefl", "uReflCol", "uSel", "uEye", "uSunDir", "uSunCol", "uAmb", "uFog", "uFogCol", "uWire", "uShadow", "uUseShadow" })
            _u[n] = GL.GetUniformLocation(_prog, n);
        GL.UseProgram(_prog);
        for (int i = 0; i < Samplers.Length; i++) GL.Uniform1(GL.GetUniformLocation(_prog, Samplers[i]), i);
        GL.Uniform1(GL.GetUniformLocation(_prog, "tShadow"), 8);
        _shProg = Link(SVS, SFS); _uShMvp = GL.GetUniformLocation(_shProg, "uMvp"); _uShCut = GL.GetUniformLocation(_shProg, "uCut");
        GL.UseProgram(_shProg); GL.Uniform1(GL.GetUniformLocation(_shProg, "tA"), 0);
        _lineProg = Link(LVS, LFS); _uLineMvp = GL.GetUniformLocation(_lineProg, "uMvp");
        _lineVao = GL.GenVertexArray(); _lineVbo = GL.GenBuffer();
        GL.BindVertexArray(_lineVao); GL.BindBuffer(BufferTarget.ArrayBuffer, _lineVbo);
        GL.EnableVertexAttribArray(0); GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 24, 0);
        GL.EnableVertexAttribArray(1); GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 24, 12);
        _ovProg = Link(OVS, OFS); _uOvScreen = GL.GetUniformLocation(_ovProg, "uScreen");
        GL.UseProgram(_ovProg); GL.Uniform1(GL.GetUniformLocation(_ovProg, "tOv"), 0);
        _ovVao = GL.GenVertexArray(); _ovVbo = GL.GenBuffer();
        GL.BindVertexArray(_ovVao); GL.BindBuffer(BufferTarget.ArrayBuffer, _ovVbo);
        GL.EnableVertexAttribArray(0); GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 16, 0);
        GL.EnableVertexAttribArray(1); GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 16, 8);
        GL.BindVertexArray(0);
        try
        {
            int n = GL.GetInteger(GetPName.NumExtensions);
            for (int i = 0; i < n; i++)
                if (GL.GetString(StringNameIndexed.Extensions, i) is "GL_EXT_texture_filter_anisotropic" or "GL_ARB_texture_filter_anisotropic")
                { GL.GetFloat((GetPName)0x84FF, out _maxAniso); break; }
        }
        catch { _maxAniso = 0; }
        while (GL.GetError() != ErrorCode.NoError) { }
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

    int Texture(string? name)
    {
        if (name == null || TextureSource == null) return 0;
        if (_textures.TryGetValue(name, out int t)) return t;
        t = 0;
        var img = TextureSource(name);
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
            if (_maxAniso > 1) GL.TexParameter(TextureTarget.Texture2D, (TextureParameterName)0x84FE, Math.Min(8f, _maxAniso));
            for (int i = 3; i < im.Rgba.Length; i += 4) if (im.Rgba[i] < 250) { _hasAlpha.Add(t); break; }
            long sum = 0; int step = Math.Max(1, im.Rgba.Length / 4 / 4096);
            int cnt = 0; for (int i = 0; i + 2 < im.Rgba.Length; i += 4 * step) { sum += im.Rgba[i] * 3 + im.Rgba[i + 1] * 6 + im.Rgba[i + 2]; cnt++; }
            _lum[t] = cnt == 0 ? 0.5f : sum / (cnt * 2550f);
        }
        _textures[name] = t;
        return t;
    }

    /// <summary>Statistics of the last frame (since <see cref="Begin"/>).</summary>
    public int ObjectsDrawn, DrawsIssued;

    Matrix4x4 _viewProj; Vector3 _eye;

    public void Begin(Matrix4x4 viewProj, Vector3 eye)
    {
        ObjectsDrawn = DrawsIssued = 0;
        _viewProj = viewProj; _eye = eye;
        Array.Fill(_bound, -1);   // other code may have bound textures since the last frame
        _queue.Clear(); _multiply.Clear();
        GL.UseProgram(_prog);
        GL.Uniform1(_u["uMode"], (int)Mode);
        GL.Uniform3(_u["uEye"], eye.X, eye.Y, eye.Z);
        var l = Lighting;
        var sun = l.Sun * l.Intensity;
        GL.Uniform3(_u["uSunDir"], l.SunDirection.X, l.SunDirection.Y, l.SunDirection.Z);
        GL.Uniform3(_u["uSunCol"], sun.X, sun.Y, sun.Z);
        GL.Uniform3(_u["uAmb"], l.Ambient.X, l.Ambient.Y, l.Ambient.Z);
        GL.Uniform4(_u["uFog"], l.FogStart, l.FogEnd, l.FogMax, l.Fog ? 1f : 0f);
        GL.Uniform3(_u["uFogCol"], l.FogColour.X, l.FogColour.Y, l.FogColour.Z);
        GL.Uniform3(_u["uWire"], 0.78f, 0.80f, 0.84f);
        bool useSh = Mode == ViewMode.Rendered && _shadowValid && Shadows;
        GL.Uniform1(_u["uUseShadow"], useSh ? 1 : 0);
        UniformMat(_u["uShadow"], _shadowVP);
        GL.ActiveTexture(TextureUnit.Texture8); GL.BindTexture(TextureTarget.Texture2D, useSh ? _shTex : 0); GL.ActiveTexture(TextureUnit.Texture0);
        GL.Enable(EnableCap.DepthTest); GL.DepthMask(true); GL.DepthFunc(DepthFunction.Lequal);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.Blend);
        GL.PolygonMode(MaterialFace.FrontAndBack, Mode == ViewMode.Wireframe ? PolygonMode.Line : PolygonMode.Fill);
    }

    /// <summary>
    /// GPU batches of a model: its LOD-0 draws (lower LODs overlap them; the game shows one level at a time), merged
    /// per material into one vertex/index buffer each, with a bounding sphere for culling. Large models (terrain,
    /// whole city blocks) keep one batch per draw so off-screen tiles are culled.
    /// </summary>
    sealed class Batch
    {
        public int Vao, Vbo, Ebo, Count;
        public MaterialInfo Mat = null!;
        public Vector3 Center; public float Radius;
        public bool Resolved; public int Has;
        public readonly int[] Tex = new int[8];
    }
    readonly Dictionary<ModelAsset, Batch[]> _batches = new();
    /// <summary>Materials for generated geometry (water surfaces) instead of reading the draw's stream state.</summary>
    public readonly Dictionary<MeshDraw, MaterialInfo> MaterialOverrides = new();
    const int Floats = 17;   // pos 3, normal 3, uv 2, uv2 2, tangent 3, colour 4

    Batch[] Batches(ModelAsset model)
    {
        if (_batches.TryGetValue(model, out var bs)) return bs;
        var draws = model.Draws.Where(d => d.Positions.Length > 0 && d.Indices.Length > 0).ToList();
        var lod0 = draws.Where(d => !model.LodOnlyNodes.Contains(d.Node)).ToList();
        if (lod0.Count > 0) draws = lod0;
        var mats = draws.ToDictionary(d => d, d => MaterialOverrides.TryGetValue(d, out var mo) ? mo : MaterialInfo.Of(d));
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var d in draws) foreach (int i in d.Indices) if (i < d.Positions.Length) { mn = Vector3.Min(mn, d.Positions[i]); mx = Vector3.Max(mx, d.Positions[i]); }
        bool big = mn.X <= mx.X && Vector3.Distance(mn, mx) > 300;
        var groups = big ? draws.Select(d => new List<MeshDraw> { d }).ToList()
            : draws.GroupBy(d => mats[d].Key).Select(g => g.ToList()).ToList();
        var list = new List<Batch>();
        foreach (var g in groups)
        {
            var buf = new List<float>(); var idx = new List<uint>();
            var bmn = new Vector3(float.MaxValue); var bmx = new Vector3(float.MinValue);
            foreach (var d in g)
            {
                uint b = (uint)(buf.Count / Floats); int n = d.Positions.Length;
                for (int i = 0; i < n; i++)
                {
                    var p = d.Positions[i]; var q = d.Normals != null ? d.Normals[i] : Vector3.Zero;
                    var t = d.UVs != null ? d.UVs[i] : Vector2.Zero; var t2 = d.UVs2 != null ? d.UVs2[i] : t;
                    var tg = d.Tangents != null ? d.Tangents[i] : Vector3.Zero;
                    uint c = d.Colors != null && i < d.Colors.Length ? d.Colors[i] : 0xFFFFFFFF;
                    buf.Add(p.X); buf.Add(p.Y); buf.Add(p.Z); buf.Add(q.X); buf.Add(q.Y); buf.Add(q.Z); buf.Add(t.X); buf.Add(t.Y); buf.Add(t2.X); buf.Add(t2.Y);
                    buf.Add(tg.X); buf.Add(tg.Y); buf.Add(tg.Z);
                    buf.Add(((c >> 16) & 0xFF) / 255f); buf.Add(((c >> 8) & 0xFF) / 255f); buf.Add((c & 0xFF) / 255f); buf.Add((c >> 24) / 255f);   // 0xAARRGGBB
                }
                foreach (int i in d.Indices)
                    if (i < n) { idx.Add(b + (uint)i); bmn = Vector3.Min(bmn, d.Positions[i]); bmx = Vector3.Max(bmx, d.Positions[i]); }
            }
            if (idx.Count == 0) continue;
            var bt = new Batch { Mat = mats[g[0]], Center = (bmn + bmx) / 2, Radius = Vector3.Distance(bmn, bmx) / 2, Count = idx.Count };
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
            GL.EnableVertexAttribArray(4); GL.VertexAttribPointer(4, 3, VertexAttribPointerType.Float, false, st, 40);
            GL.EnableVertexAttribArray(5); GL.VertexAttribPointer(5, 4, VertexAttribPointerType.Float, false, st, 52);
            GL.BindVertexArray(0);
            list.Add(bt);
        }
        // opaque first, then cut-outs: fewer state changes and early depth for the blended pass
        return _batches[model] = list.OrderBy(b => b.Mat.Blend).ToArray();
    }

    void Resolve(Batch b)
    {
        var m = b.Mat;
        b.Tex[0] = Texture(m.Base); b.Tex[1] = Texture(m.Overlay); b.Tex[2] = Texture(m.Mask); b.Tex[3] = Texture(m.Normal);
        b.Tex[4] = Texture(m.Spec); b.Tex[5] = Texture(m.Reflect); b.Tex[6] = Texture(m.Ao); b.Tex[7] = Texture(m.AlphaTex);
        int h = 0;
        if (b.Tex[0] != 0) { h |= 1; if (_hasAlpha.Contains(b.Tex[0])) h |= 512; }
        if (b.Tex[1] != 0 && b.Tex[2] != 0) { h |= 2; if (_lum.GetValueOrDefault(b.Tex[0]) > _lum.GetValueOrDefault(b.Tex[1])) h |= 2048; }
        if (b.Tex[3] != 0) h |= 4;
        if (b.Tex[4] != 0) h |= 8;
        if (b.Tex[5] != 0) h |= 16;
        if (b.Tex[6] != 0) { h |= 32; if (m.AoUv2) h |= 256; }
        if (b.Tex[7] != 0) { h |= 64; if (_hasAlpha.Contains(b.Tex[7])) h |= 1024; }
        if (m.VertexColour) h |= 128;
        if (m.MaskUv2) h |= 4096;
        if (m.AlphaUv2) h |= 8192;
        b.Has = h; b.Resolved = true;
    }

    /// <summary>View frustum planes (a·p + d ≥ 0 inside) and camera position for culling in <see cref="DrawModel"/>.</summary>
    public readonly struct Frustum
    {
        public readonly Vector4[] Planes; public readonly Vector3 Eye; public readonly float MinSize;
        public Frustum(Matrix4x4 m, Vector3 eye, float minSize)
        {
            // row-vector clip = p · M; planes from the columns (depth 0..1: near = column 3)
            Vector4 C(int j) => j switch { 1 => new(m.M11, m.M21, m.M31, m.M41), 2 => new(m.M12, m.M22, m.M32, m.M42), 3 => new(m.M13, m.M23, m.M33, m.M43), _ => new(m.M14, m.M24, m.M34, m.M44) };
            var c4 = C(4);
            Planes = new[] { c4 + C(1), c4 - C(1), c4 + C(2), c4 - C(2), C(3), c4 - C(3) };
            for (int i = 0; i < 6; i++) Planes[i] /= new Vector3(Planes[i].X, Planes[i].Y, Planes[i].Z).Length();
            Eye = eye; MinSize = minSize;
        }
        /// <summary>False when the sphere is outside the view, or smaller than <see cref="MinSize"/> (radius / distance).</summary>
        public bool Visible(Vector3 c, float r)
        {
            foreach (var p in Planes) if (p.X * c.X + p.Y * c.Y + p.Z * c.Z + p.W < -r) return false;
            float dist = Vector3.Distance(c, Eye);
            return dist <= r || r / dist >= MinSize;
        }
    }

    readonly int[] _bound = new int[8];
    readonly List<(Batch B, Matrix4x4 World, Vector4 Tint, float Dist)> _queue = new(), _multiply = new();

    /// <summary>Draws a model's opaque and cut-out batches; blended and contact-occlusion batches are queued for
    /// <see cref="FlushTransparent"/>. <paramref name="unlit"/> draws without lighting or fog (sky domes).</summary>
    public void DrawModel(ModelAsset model, Matrix4x4 world, Vector4 tint, Frustum? frustum = null, bool unlit = false)
    {
        var batches = Batches(model);
        if (batches.Length == 0) return;
        float scale = MathF.Sqrt(MathF.Max(world.M11 * world.M11 + world.M12 * world.M12 + world.M13 * world.M13,
                        MathF.Max(world.M21 * world.M21 + world.M22 * world.M22 + world.M23 * world.M23, world.M31 * world.M31 + world.M32 * world.M32 + world.M33 * world.M33)));
        bool any = false, setMat = false;
        bool flat = Mode is ViewMode.Wireframe or ViewMode.Solid;
        foreach (var b in batches)
        {
            Vector3 wc = Vector3.Transform(b.Center, world);
            if (frustum is { } f && !f.Visible(wc, b.Radius * scale)) continue;
            if (!any) { any = true; ObjectsDrawn++; }
            if (!flat && !unlit && b.Mat.Blend is BlendKind.Blend or BlendKind.Multiply or BlendKind.Additive)
            {
                (b.Mat.Blend == BlendKind.Multiply ? _multiply : _queue).Add((b, world, tint, Vector3.DistanceSquared(wc, _eye)));
                continue;
            }
            if (!setMat) { setMat = true; SetObject(world, tint); }
            if (unlit)
            {
                GL.Uniform1(_u["uMode"], 4);
                if (b.Mat.Blend == BlendKind.Blend) { GL.Enable(EnableCap.Blend); GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha); }
                if (b.Mat.Blend == BlendKind.Multiply) continue;
            }
            DrawBatch(b, flat);
            if (unlit) { GL.Uniform1(_u["uMode"], (int)Mode); GL.Disable(EnableCap.Blend); }
        }
        GL.BindVertexArray(0);
    }

    void SetObject(Matrix4x4 world, Vector4 tint)
    {
        UniformMat(_u["uMvp"], world * _viewProj); UniformMat(_u["uModel"], world);
        GL.Uniform4(_u["uSel"], tint.X, tint.Y, tint.Z, tint.W);
    }

    void DrawBatch(Batch b, bool flat)
    {
        if (!flat)
        {
            if (!b.Resolved) Resolve(b);
            for (int i = 0; i < 8; i++)
                if (_bound[i] != b.Tex[i]) { GL.ActiveTexture(TextureUnit.Texture0 + i); GL.BindTexture(TextureTarget.Texture2D, b.Tex[i]); _bound[i] = b.Tex[i]; }
            var m = b.Mat;
            GL.Uniform1(_u["uHas"], b.Has);
            GL.Uniform1(_u["uBlend"], (int)m.Blend);
            GL.Uniform3(_u["uOverTint"], m.OverTint.X, m.OverTint.Y, m.OverTint.Z);
            var tint = m.Untextured && m.Blend != BlendKind.Multiply ? m.Tint : m.Tint;
            GL.Uniform3(_u["uMatTint"], tint.X, tint.Y, tint.Z);
            GL.Uniform1(_u["uOpacity"], m.Opacity);
            GL.Uniform3(_u["uSpecCol"], m.SpecColour.X, m.SpecColour.Y, m.SpecColour.Z);
            GL.Uniform1(_u["uSpecPow"], m.SpecPower);
            GL.Uniform1(_u["uRefl"], m.ReflectStrength);
            GL.Uniform3(_u["uReflCol"], m.ReflectColour.X, m.ReflectColour.Y, m.ReflectColour.Z);
        }
        GL.BindVertexArray(b.Vao);
        GL.DrawElements(PrimitiveType.Triangles, b.Count, DrawElementsType.UnsignedInt, 0);
        DrawsIssued++;
    }

    // ------------------------------------------------------------------ sun shadow map (Rendered mode)

    /// <summary>Cast sun shadows in the Rendered mode.</summary>
    public bool Shadows = true;
    public const int ShadowSize = 4096;
    int _shFbo, _shTex, _shProg, _uShMvp, _uShCut;
    Matrix4x4 _shadowVP = Matrix4x4.Identity; bool _shadowValid;

    /// <summary>Starts rendering the shadow map seen from the sun (orthographic <paramref name="lightViewProj"/>).</summary>
    public void BeginShadow(Matrix4x4 lightViewProj)
    {
        if (_shFbo == 0)
        {
            _shTex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _shTex);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent24, ShadowSize, ShadowSize, 0, PixelFormat.DepthComponent, PixelType.Float, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.CompareRefToTexture);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareFunc, (int)DepthFunction.Lequal);
            _shFbo = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _shFbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, _shTex, 0);
            GL.DrawBuffer(DrawBufferMode.None); GL.ReadBuffer(ReadBufferMode.None);
        }
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _shFbo);
        GL.Viewport(0, 0, ShadowSize, ShadowSize);
        GL.DepthMask(true); GL.Enable(EnableCap.DepthTest); GL.DepthFunc(DepthFunction.Lequal);
        GL.Clear(ClearBufferMask.DepthBufferBit);
        GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
        GL.Enable(EnableCap.PolygonOffsetFill); GL.PolygonOffset(1.5f, 3f);
        GL.UseProgram(_shProg);
        _shadowVP = lightViewProj;
        Array.Fill(_bound, -1);
    }

    /// <summary>Draws a model's opaque and cut-out batches into the shadow map.</summary>
    public void DrawShadow(ModelAsset model, Matrix4x4 world, Frustum? frustum)
    {
        var batches = Batches(model);
        float scale = MathF.Sqrt(MathF.Max(world.M11 * world.M11 + world.M12 * world.M12 + world.M13 * world.M13,
                        MathF.Max(world.M21 * world.M21 + world.M22 * world.M22 + world.M23 * world.M23, world.M31 * world.M31 + world.M32 * world.M32 + world.M33 * world.M33)));
        bool set = false;
        foreach (var b in batches)
        {
            if (b.Mat.Blend is BlendKind.Blend or BlendKind.Multiply or BlendKind.Additive) continue;
            if (frustum is { } f && !f.Visible(Vector3.Transform(b.Center, world), b.Radius * scale)) continue;
            if (!set) { set = true; UniformMat(_uShMvp, world * _shadowVP); }
            int cut = 0, tex = 0;
            if (b.Mat.Blend == BlendKind.Cutout)
            {
                if (!b.Resolved) Resolve(b);
                if (b.Tex[7] != 0) { tex = b.Tex[7]; cut = (b.Has & 1024) != 0 ? 1 : 2; }
                else if ((b.Has & 512) != 0) { tex = b.Tex[0]; cut = 1; }
            }
            GL.Uniform1(_uShCut, cut);
            if (cut != 0 && _bound[0] != tex) { GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D, tex); _bound[0] = tex; }
            GL.BindVertexArray(b.Vao);
            GL.DrawElements(PrimitiveType.Triangles, b.Count, DrawElementsType.UnsignedInt, 0);
        }
        GL.BindVertexArray(0);
    }

    public void EndShadow(int viewW, int viewH)
    {
        GL.Disable(EnableCap.PolygonOffsetFill);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.Viewport(0, 0, viewW, viewH);
        _shadowValid = true;
    }

    public void InvalidateShadow() => _shadowValid = false;

    /// <summary>Draws the queued contact-occlusion overlays (multiplied onto the frame) and the blended batches,
    /// farthest first, without depth writes.</summary>
    public void FlushTransparent()
    {
        if (_queue.Count == 0 && _multiply.Count == 0) return;
        GL.UseProgram(_prog);
        GL.Enable(EnableCap.Blend);
        GL.DepthMask(false);
        GL.Enable(EnableCap.PolygonOffsetFill); GL.PolygonOffset(-1f, -2f);
        GL.BlendFunc(BlendingFactor.DstColor, BlendingFactor.Zero);
        foreach (var (b, w, t, _) in _multiply) { SetObject(w, t); DrawBatch(b, false); }
        GL.Disable(EnableCap.PolygonOffsetFill);
        GL.BlendFuncSeparate(BlendingFactorSrc.SrcAlpha, BlendingFactorDest.OneMinusSrcAlpha, BlendingFactorSrc.One, BlendingFactorDest.OneMinusSrcAlpha);
        foreach (var (b, w, t, _) in _queue.OrderByDescending(x => x.Dist))
        {
            if (b.Mat.Blend == BlendKind.Additive) GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
            else GL.BlendFuncSeparate(BlendingFactorSrc.SrcAlpha, BlendingFactorDest.OneMinusSrcAlpha, BlendingFactorSrc.One, BlendingFactorDest.OneMinusSrcAlpha);
            SetObject(w, t); DrawBatch(b, false);
        }
        GL.BindVertexArray(0);
        GL.DepthMask(true);
        GL.Disable(EnableCap.Blend);
        _queue.Clear(); _multiply.Clear();
    }

    /// <summary>Draws coloured line segments (pairs of points) in world space.</summary>
    public void Lines(IReadOnlyList<(Vector3 A, Vector3 B, Vector3 Color)> lines, Matrix4x4 viewProj, bool onTop, float width = 2f)
    {
        if (lines.Count == 0) return;
        var buf = new float[lines.Count * 12];
        for (int i = 0; i < lines.Count; i++)
        {
            var (a, b, c) = lines[i];
            buf[i * 12] = a.X; buf[i * 12 + 1] = a.Y; buf[i * 12 + 2] = a.Z; buf[i * 12 + 3] = c.X; buf[i * 12 + 4] = c.Y; buf[i * 12 + 5] = c.Z;
            buf[i * 12 + 6] = b.X; buf[i * 12 + 7] = b.Y; buf[i * 12 + 8] = b.Z; buf[i * 12 + 9] = c.X; buf[i * 12 + 10] = c.Y; buf[i * 12 + 11] = c.Z;
        }
        GL.UseProgram(_lineProg);
        UniformMat(_uLineMvp, viewProj);
        if (onTop) GL.Disable(EnableCap.DepthTest);
        GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
        GL.BindVertexArray(_lineVao); GL.BindBuffer(BufferTarget.ArrayBuffer, _lineVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, buf.Length * 4, buf, BufferUsageHint.StreamDraw);
        GL.LineWidth(width);
        GL.DrawArrays(PrimitiveType.Lines, 0, lines.Count * 2);
        GL.LineWidth(1f);
        GL.BindVertexArray(0);
        GL.Enable(EnableCap.DepthTest);
    }

    /// <summary>A static set of line segments kept on the GPU (e.g. collision wireframes).</summary>
    public sealed class LineBatch { public int Vao, Vbo, Count; }

    public LineBatch CreateLineBatch(IReadOnlyList<(Vector3 A, Vector3 B)> lines, Vector3 color) =>
        CreateColoredLineBatch(lines.Select(l => (l.A, l.B, color)).ToList());

    /// <summary>A static set of line segments with their own colours (marker boxes, path links).</summary>
    public LineBatch CreateColoredLineBatch(IReadOnlyList<(Vector3 A, Vector3 B, Vector3 Color)> lines)
    {
        var buf = new float[lines.Count * 12];
        for (int i = 0; i < lines.Count; i++)
        {
            var (a, b, c) = lines[i]; int o = i * 12;
            buf[o] = a.X; buf[o + 1] = a.Y; buf[o + 2] = a.Z; buf[o + 3] = c.X; buf[o + 4] = c.Y; buf[o + 5] = c.Z;
            buf[o + 6] = b.X; buf[o + 7] = b.Y; buf[o + 8] = b.Z; buf[o + 9] = c.X; buf[o + 10] = c.Y; buf[o + 11] = c.Z;
        }
        var lb = new LineBatch { Vao = GL.GenVertexArray(), Vbo = GL.GenBuffer(), Count = lines.Count * 2 };
        GL.BindVertexArray(lb.Vao); GL.BindBuffer(BufferTarget.ArrayBuffer, lb.Vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, buf.Length * 4, buf, BufferUsageHint.StaticDraw);
        GL.EnableVertexAttribArray(0); GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 24, 0);
        GL.EnableVertexAttribArray(1); GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 24, 12);
        GL.BindVertexArray(0);
        return lb;
    }

    public void DrawLineBatch(LineBatch lb, Matrix4x4 mvp, bool onTop = false)
    {
        if (lb.Count == 0) return;
        GL.UseProgram(_lineProg);
        UniformMat(_uLineMvp, mvp);
        GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
        GL.LineWidth(onTop ? 2f : 1f);
        if (onTop) GL.Disable(EnableCap.DepthTest);
        GL.BindVertexArray(lb.Vao);
        GL.DrawArrays(PrimitiveType.Lines, 0, lb.Count);
        GL.BindVertexArray(0);
        GL.LineWidth(1f);
        GL.Enable(EnableCap.DepthTest);
    }

    public void DeleteLineBatch(LineBatch lb) { GL.DeleteBuffer(lb.Vbo); GL.DeleteVertexArray(lb.Vao); }

    // ------------------------------------------------------------------ 2D overlay (view-mode bar, transform HUD)

    /// <summary>A screen-space image (GDI+ drawn) uploaded as a texture; re-uploaded only when its content changes.</summary>
    public sealed class Overlay { public int Tex, W, H; public string Key = ""; }

    public void UpdateOverlay(Overlay ov, Bitmap bmp, string key)
    {
        if (ov.Tex != 0 && ov.Key == key && ov.W == bmp.Width && ov.H == bmp.Height) return;
        if (ov.Tex == 0) ov.Tex = GL.GenTexture();
        var bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        GL.BindTexture(TextureTarget.Texture2D, ov.Tex);
        GL.PixelStore(PixelStoreParameter.UnpackRowLength, bd.Stride / 4);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, bmp.Width, bmp.Height, 0, PixelFormat.Bgra, PixelType.UnsignedByte, bd.Scan0);
        GL.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
        bmp.UnlockBits(bd);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        ov.W = bmp.Width; ov.H = bmp.Height; ov.Key = key;
        _bound[0] = -1;
    }

    /// <summary>Draws an overlay at pixel position (x, y) of a viewport of the given size (premultiplied alpha).</summary>
    public void DrawOverlay(Overlay ov, int x, int y, int screenW, int screenH)
    {
        if (ov.Tex == 0) return;
        float[] v = { x, y, 0, 0, x + ov.W, y, 1, 0, x + ov.W, y + ov.H, 1, 1, x, y, 0, 0, x + ov.W, y + ov.H, 1, 1, x, y + ov.H, 0, 1 };
        GL.UseProgram(_ovProg);
        GL.Uniform2(_uOvScreen, (float)screenW, (float)screenH);
        GL.Disable(EnableCap.DepthTest);
        GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
        GL.Enable(EnableCap.Blend); GL.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
        GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D, ov.Tex); _bound[0] = -1;
        GL.BindVertexArray(_ovVao); GL.BindBuffer(BufferTarget.ArrayBuffer, _ovVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, v.Length * 4, v, BufferUsageHint.StreamDraw);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
        GL.BindVertexArray(0);
        GL.Disable(EnableCap.Blend);
        GL.Enable(EnableCap.DepthTest);
    }

    public void DeleteOverlay(Overlay ov) { if (ov.Tex != 0) GL.DeleteTexture(ov.Tex); ov.Tex = 0; ov.Key = ""; }

    static readonly float[] _mat = new float[16];
    static void UniformMat(int loc, Matrix4x4 m)
    {
        // System.Numerics is row-vector; GLSL expects column-major for M*v, which is the same memory layout.
        var a = _mat;
        a[0] = m.M11; a[1] = m.M12; a[2] = m.M13; a[3] = m.M14; a[4] = m.M21; a[5] = m.M22; a[6] = m.M23; a[7] = m.M24;
        a[8] = m.M31; a[9] = m.M32; a[10] = m.M33; a[11] = m.M34; a[12] = m.M41; a[13] = m.M42; a[14] = m.M43; a[15] = m.M44;
        GL.UniformMatrix4(loc, 1, false, a);
    }

    /// <summary>Frees all batches and textures (call when a new scene is loaded).</summary>
    public void Clear()
    {
        foreach (var b in _batches.Values.SelectMany(x => x)) { GL.DeleteVertexArray(b.Vao); GL.DeleteBuffer(b.Vbo); GL.DeleteBuffer(b.Ebo); }
        _batches.Clear();
        Array.Fill(_bound, -1);
        foreach (var t in _textures.Values) if (t != 0) GL.DeleteTexture(t);
        _textures.Clear(); _hasAlpha.Clear(); _lum.Clear();
        _queue.Clear(); _multiply.Clear();
    }

    public void Dispose()
    {
        Clear();
        if (_shFbo != 0) { GL.DeleteFramebuffer(_shFbo); GL.DeleteTexture(_shTex); _shFbo = _shTex = 0; }
    }
}
