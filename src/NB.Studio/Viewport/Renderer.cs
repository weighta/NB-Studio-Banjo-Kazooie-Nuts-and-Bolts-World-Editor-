using System.Numerics;
using NB.Core.Models;
using OpenTK.Graphics.OpenGL4;

namespace NB.Studio.Viewport;

/// <summary>GPU-side resources: shaders, per-draw meshes and textures. All calls must happen with the GL context current.</summary>
public sealed class Renderer : IDisposable
{
    int _prog, _lineProg, _lineVao, _lineVbo;
    int _uMvp, _uModel, _uTint, _uHasTex, _uLight, _uLineMvp, _uLayered, _uOverTint;
    readonly Dictionary<string, int> _textures = new();
    public Func<string, (byte[] Rgba, int W, int H)?>? TextureSource;
    public bool Wireframe;

    const string VS = """
        #version 330 core
        layout(location=0) in vec3 aPos; layout(location=1) in vec3 aNrm; layout(location=2) in vec2 aUV;
        uniform mat4 uMvp; uniform mat4 uModel;
        out vec3 vN; out vec2 vUV;
        void main(){ gl_Position = uMvp * vec4(aPos,1.0); vN = mat3(uModel) * aNrm; vUV = aUV; }
        """;
    const string FS = """
        #version 330 core
        in vec3 vN; in vec2 vUV; out vec4 o;
        uniform sampler2D uTex; uniform sampler2D uTex2; uniform sampler2D uMask; uniform int uHasTex; uniform int uLayered; uniform vec3 uOverTint;
        uniform vec4 uTint; uniform vec3 uLight;
        void main(){
          vec4 c = uHasTex==1 ? texture(uTex, vUV) : vec4(0.72,0.72,0.70,1.0);
          if (uLayered==1) { vec4 o2 = texture(uTex2, vUV) * vec4(uOverTint, 1.0); float m = texture(uMask, vUV).r; c = vec4(mix(c.rgb, o2.rgb, m), max(c.a, o2.a)); }
          if (uHasTex==1 && c.a < 0.35) discard;
          vec3 n = length(vN) > 0.0 ? normalize(vN) : vec3(0,1,0);
          float l = 0.45 + 0.55*abs(dot(n, normalize(uLight)));
          o = vec4(mix(c.rgb*l, uTint.rgb, uTint.a), 1.0);
        }
        """;
    const string LVS = "#version 330 core\nlayout(location=0) in vec3 aPos; layout(location=1) in vec3 aCol; uniform mat4 uMvp; out vec3 vC; void main(){ gl_Position = uMvp*vec4(aPos,1.0); vC=aCol; }";
    const string LFS = "#version 330 core\nin vec3 vC; out vec4 o; void main(){ o = vec4(vC,1.0); }";

    public void Init()
    {
        _prog = Link(VS, FS);
        _uMvp = GL.GetUniformLocation(_prog, "uMvp"); _uModel = GL.GetUniformLocation(_prog, "uModel");
        _uTint = GL.GetUniformLocation(_prog, "uTint"); _uHasTex = GL.GetUniformLocation(_prog, "uHasTex"); _uLight = GL.GetUniformLocation(_prog, "uLight");
        _uLayered = GL.GetUniformLocation(_prog, "uLayered"); _uOverTint = GL.GetUniformLocation(_prog, "uOverTint");
        GL.UseProgram(_prog);
        GL.Uniform1(GL.GetUniformLocation(_prog, "uTex"), 0); GL.Uniform1(GL.GetUniformLocation(_prog, "uTex2"), 1); GL.Uniform1(GL.GetUniformLocation(_prog, "uMask"), 2);
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
        return p;
    }

    int Texture(string? name)
    {
        if (name == null || TextureSource == null) return 0;
        if (_textures.TryGetValue(name, out int t)) return t;
        t = 0;
        var img = TextureSource(name);
        if (img is { } im)
        {
            t = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, t);
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

    /// <summary>Statistics of the last frame (since <see cref="Begin"/>).</summary>
    public int ObjectsDrawn, DrawsIssued;

    public void Begin(Vector3 light)
    {
        ObjectsDrawn = DrawsIssued = 0;
        _boundTex = _boundTex2 = _boundMask = -1;   // other code may have bound textures since the last frame
        GL.UseProgram(_prog);
        GL.Uniform3(_uLight, light.X, light.Y, light.Z);
        GL.Enable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        GL.PolygonMode(MaterialFace.FrontAndBack, Wireframe ? PolygonMode.Line : PolygonMode.Fill);
    }

    /// <summary>
    /// GPU batches of a model: its LOD-0 draws (lower LODs overlap them; the game shows one level at a time), merged
    /// per material (same textures and overlay tint) into one vertex/index buffer each, with a bounding sphere for
    /// culling. Large models (terrain, whole city blocks) keep one batch per draw so off-screen tiles are culled.
    /// </summary>
    sealed class Batch
    {
        public int Vao, Vbo, Ebo, Count;
        public string? Base, Over, Mask;
        public Vector3 OverTint = Vector3.One;
        public Vector3 Center; public float Radius;
        public bool Resolved; public int Tex, Tex2, MaskTex;
    }
    readonly Dictionary<ModelAsset, Batch[]> _batches = new();

    Batch[] Batches(ModelAsset model)
    {
        if (_batches.TryGetValue(model, out var bs)) return bs;
        var draws = model.Draws.Where(d => d.Positions.Length > 0 && d.Indices.Length > 0).ToList();
        var lod0 = draws.Where(d => !model.LodOnlyNodes.Contains(d.Node)).ToList();
        if (lod0.Count > 0) draws = lod0;
        // model size from the drawn vertices
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var d in draws) foreach (int i in d.Indices) if (i < d.Positions.Length) { mn = Vector3.Min(mn, d.Positions[i]); mx = Vector3.Max(mx, d.Positions[i]); }
        bool big = mn.X <= mx.X && Vector3.Distance(mn, mx) > 300;
        var groups = big ? draws.Select(d => new List<MeshDraw> { d }).ToList()
            : draws.GroupBy(d => { var l = NB.Core.Models.ObjExporter.MaterialLayers(d); var c5 = d.PixelConstants.TryGetValue(5, out var v) ? v : Vector4.One; return (l.Base, l.Overlay, l.Mask, c5.X, c5.Y, c5.Z); })
                   .Select(g => g.ToList()).ToList();
        var list = new List<Batch>();
        foreach (var g in groups)
        {
            var buf = new List<float>(); var idx = new List<uint>();
            var bmn = new Vector3(float.MaxValue); var bmx = new Vector3(float.MinValue);
            foreach (var d in g)
            {
                uint b = (uint)(buf.Count / 8); int n = d.Positions.Length;
                for (int i = 0; i < n; i++)
                {
                    var p = d.Positions[i]; var q = d.Normals != null ? d.Normals[i] : Vector3.Zero; var t = d.UVs != null ? d.UVs[i] : Vector2.Zero;
                    buf.Add(p.X); buf.Add(p.Y); buf.Add(p.Z); buf.Add(q.X); buf.Add(q.Y); buf.Add(q.Z); buf.Add(t.X); buf.Add(t.Y);
                }
                foreach (int i in d.Indices)
                    if (i < n) { idx.Add(b + (uint)i); bmn = Vector3.Min(bmn, d.Positions[i]); bmx = Vector3.Max(bmx, d.Positions[i]); }
            }
            if (idx.Count == 0) continue;
            var (lb, lo, lm) = NB.Core.Models.ObjExporter.MaterialLayers(g[0]);
            var c5 = g[0].PixelConstants.TryGetValue(5, out var cc) ? cc : Vector4.One;
            var bt = new Batch { Base = lb, Over = lo, Mask = lm, OverTint = new Vector3(c5.X, c5.Y, c5.Z), Center = (bmn + bmx) / 2, Radius = Vector3.Distance(bmn, bmx) / 2, Count = idx.Count };
            bt.Vao = GL.GenVertexArray(); bt.Vbo = GL.GenBuffer(); bt.Ebo = GL.GenBuffer();
            GL.BindVertexArray(bt.Vao);
            var fa = buf.ToArray(); var ia = idx.ToArray();
            GL.BindBuffer(BufferTarget.ArrayBuffer, bt.Vbo); GL.BufferData(BufferTarget.ArrayBuffer, fa.Length * 4, fa, BufferUsageHint.StaticDraw);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, bt.Ebo); GL.BufferData(BufferTarget.ElementArrayBuffer, ia.Length * 4, ia, BufferUsageHint.StaticDraw);
            GL.EnableVertexAttribArray(0); GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 32, 0);
            GL.EnableVertexAttribArray(1); GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 32, 12);
            GL.EnableVertexAttribArray(2); GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, 32, 24);
            GL.BindVertexArray(0);
            list.Add(bt);
        }
        return _batches[model] = list.ToArray();
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

    int _boundTex = -1, _boundTex2 = -1, _boundMask = -1;

    public void DrawModel(ModelAsset model, Matrix4x4 world, Matrix4x4 viewProj, Vector4 tint, bool textured = true, Frustum? frustum = null)
    {
        var batches = Batches(model);
        if (batches.Length == 0) return;
        float scale = MathF.Sqrt(MathF.Max(world.M11 * world.M11 + world.M12 * world.M12 + world.M13 * world.M13,
                        MathF.Max(world.M21 * world.M21 + world.M22 * world.M22 + world.M23 * world.M23, world.M31 * world.M31 + world.M32 * world.M32 + world.M33 * world.M33)));
        bool any = false;
        foreach (var b in batches)
        {
            if (frustum is { } f && !f.Visible(Vector3.Transform(b.Center, world), b.Radius * scale)) continue;
            if (!any)
            {
                any = true; ObjectsDrawn++;
                UniformMat(_uMvp, world * viewProj); UniformMat(_uModel, world);
                GL.Uniform4(_uTint, tint.X, tint.Y, tint.Z, tint.W);
            }
            if (!b.Resolved && textured)
            {
                b.Tex = Texture(b.Base); b.Tex2 = b.Over != null ? Texture(b.Over) : 0; b.MaskTex = b.Mask != null ? Texture(b.Mask) : 0; b.Resolved = true;
            }
            int tex = textured ? b.Tex : 0, tex2 = textured ? b.Tex2 : 0, mask = textured ? b.MaskTex : 0;
            if (tex2 != _boundTex2) { GL.ActiveTexture(TextureUnit.Texture1); GL.BindTexture(TextureTarget.Texture2D, tex2); _boundTex2 = tex2; }
            if (mask != _boundMask) { GL.ActiveTexture(TextureUnit.Texture2); GL.BindTexture(TextureTarget.Texture2D, mask); _boundMask = mask; }
            if (tex != _boundTex) { GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D, tex); _boundTex = tex; }
            GL.Uniform1(_uHasTex, tex != 0 ? 1 : 0);
            GL.Uniform1(_uLayered, tex != 0 && tex2 != 0 && mask != 0 ? 1 : 0);
            GL.Uniform3(_uOverTint, b.OverTint.X, b.OverTint.Y, b.OverTint.Z);
            GL.BindVertexArray(b.Vao);
            GL.DrawElements(PrimitiveType.Triangles, b.Count, DrawElementsType.UnsignedInt, 0);
            DrawsIssued++;
        }
        GL.BindVertexArray(0);
    }

    /// <summary>Draws coloured line segments (pairs of points) in world space.</summary>
    public void Lines(IReadOnlyList<(Vector3 A, Vector3 B, Vector3 Color)> lines, Matrix4x4 viewProj, bool onTop)
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
        GL.LineWidth(2f);
        GL.DrawArrays(PrimitiveType.Lines, 0, lines.Count * 2);
        GL.BindVertexArray(0);
        GL.Enable(EnableCap.DepthTest);
    }

    /// <summary>A static set of line segments kept on the GPU (e.g. collision wireframes).</summary>
    public sealed class LineBatch { public int Vao, Vbo, Count; }

    public LineBatch CreateLineBatch(IReadOnlyList<(Vector3 A, Vector3 B)> lines, Vector3 color)
    {
        var buf = new float[lines.Count * 12];
        for (int i = 0; i < lines.Count; i++)
        {
            var (a, b) = lines[i]; int o = i * 12;
            buf[o] = a.X; buf[o + 1] = a.Y; buf[o + 2] = a.Z; buf[o + 3] = color.X; buf[o + 4] = color.Y; buf[o + 5] = color.Z;
            buf[o + 6] = b.X; buf[o + 7] = b.Y; buf[o + 8] = b.Z; buf[o + 9] = color.X; buf[o + 10] = color.Y; buf[o + 11] = color.Z;
        }
        var lb = new LineBatch { Vao = GL.GenVertexArray(), Vbo = GL.GenBuffer(), Count = lines.Count * 2 };
        GL.BindVertexArray(lb.Vao); GL.BindBuffer(BufferTarget.ArrayBuffer, lb.Vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, buf.Length * 4, buf, BufferUsageHint.StaticDraw);
        GL.EnableVertexAttribArray(0); GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 24, 0);
        GL.EnableVertexAttribArray(1); GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 24, 12);
        GL.BindVertexArray(0);
        return lb;
    }

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
        GL.Enable(EnableCap.DepthTest);
    }

    public void DeleteLineBatch(LineBatch lb) { GL.DeleteBuffer(lb.Vbo); GL.DeleteVertexArray(lb.Vao); }

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
        _boundTex = _boundTex2 = _boundMask = -1;
        foreach (var t in _textures.Values) if (t != 0) GL.DeleteTexture(t);
        _textures.Clear();
    }

    public void Dispose() { Clear(); }
}
